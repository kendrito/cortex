using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Testy.Cli.Mcp;
using Testy.Core;

namespace Testy.Tests;

/// <summary>
/// Drives the MCP server in-process over in-memory streams and the HTTP transport on a free loopback port. No desktop application is driven:
/// slow launches use the windowless idle fixture, and runs use a stand-in driver and execution.
/// </summary>
internal static partial class McpChecks
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "Testy-mcp-checks-" + Guid.NewGuid().ToString("N"));
    internal static readonly string[] ExpectedTools =
    [
        "get_workspace_info", "list_tests", "get_test", "create_test", "update_test", "delete_test", "validate_test", "list_apps", "launch_app", "close_app", "activate_app",
        "inspect_app", "screenshot_app", "perform_step", "run_test", "cancel_run", "list_runs", "get_run", "get_run_report", "get_run_screenshot", "find_app", .. CortexMcpTools.Define(null).Select(tool => tool.Name)
    ];

    public static (string Name, Func<Task> Execute)[] All() =>
    [
        .. CortexWorkflows(),
        ("MCP initialize negotiates every handshake revision and answers unknown versions with the latest", InitializeNegotiation),
        ("MCP initialized notification gets no response and ping returns an empty result", InitializedAndPing),
        ("MCP tools/list exposes unique schema-complete tools whose action enum equals StepAction, with side-effect and open-world hints", ToolsList),
        ("MCP protocol errors: unknown method, malformed JSON, missing params, null and fractional ids, bad progress token", ProtocolErrors),
        ("MCP echoes string and number request ids exactly", IdEcho),
        ("MCP refuses an id that is still in flight and tells string ids from number ids", DuplicateIds),
        ("MCP tool failures return isError with guidance and an unknown tool is a -32602 protocol error", ToolErrors),
        ("MCP argument rules: exact action spelling, whole numbers, earlier argument names, error wording", ArgumentRules),
        ("MCP create/get/update/delete/list/validate round trip writes the workspace files Studio reads", WorkspaceRoundTrip),
        ("MCP validation refuses keys, chords and toggle values that cannot run, and warns about what is probably unintended", ValidationCompleteness),
        ("MCP structured tool results satisfy their declared output schemas and declare every top-level property", OutputSchemas),
        ("MCP text content is compact JSON with literal quotes and non-ASCII text", TextContent),
        ("MCP observations leave out usual values, page, scope to a control and redact passwords", Observations),
        ("MCP resources list/read cover the workspace, the guide, saved tests and runs as the tools return them", Resources),
        ("MCP prompts/get renders the write_test workflow and rejects missing arguments", Prompts),
        ("MCP launch policy starts only local Windows GUI programs and touches no refused path", LaunchPolicy),
        ("MCP launch_app and run_test refuse shells, script hosts, console programs and network paths without starting anything", LaunchRefusals),
        ("MCP a cancelled request gets no response and no further progress, and its app is closed", Cancellation),
        ("MCP a cancellation written right after its request always finds it", CancellationOrdering),
        ("MCP a long call without a progress token sends no notification, and a slow reader does not slow the call", ProgressRules),
        ("MCP JSON-RPC batches are answered with one array", Batches),
        ("MCP stdin framing: byte order mark, CRLF, blank lines, split writes, invalid UTF-8 and an oversized line", Framing),
        ("MCP stateless 2026-07-28 requests: server/discover, per-request version negotiation and resultType", Stateless),
        ("MCP stateless 2026-07-28 requests must carry their _meta fields, and removed methods are not found", StatelessEnvelope),
        ("MCP subscriptions/listen acknowledges, stays silent after a cancel and closes properly when the server stops", Subscriptions),
        ("MCP a background run is polled, blocks other input with a clear message and is stopped by cancel_run", BackgroundRun),
        ("MCP run_test hands a long run to the background, and cancelling the waiting call cancels the run without a response", RunWaiting),
        ("MCP a run that stops with an error after it started is saved as failed", RunFailure),
        ("MCP run progress stays on one scale, increases strictly and names the run", RunProgressScale),
        ("MCP perform_step and run_test refuse to work while the desktop lease is held elsewhere", DesktopLease),
        ("MCP a run is reported finished as soon as its result is recorded, while it still closes its app", RunFinishedBeforeCleanup),
        ("MCP a stored network or relative program path is never opened, and a copied test or run file is refused", StoredPaths),
        ("MCP run screenshots are served only from the run's evidence folder in the workspace", EvidenceContainment),
        ("MCP list tools read each run file once, and old evidence is removed at start", RunIndexAndRetention),
        ("MCP end of input completes the stdio transport and shutdown closes tracked apps", Shutdown),
        ("MCP launched apps end with the server however it ends, unless keepOpen", ProcessLifetime),
        ("MCP command line: own options, help, errors on stderr only, token from the environment", CommandLine),
        ("MCP HTTP transport enforces sessions, origin, host, bearer token, content type and method rules", HttpRules),
        ("MCP HTTP sessions: initialize replaces a session, 64 at most, idle sessions expire", HttpSessions),
        ("MCP HTTP revision 2026-07-28: headers mirror the body, invalid envelopes are 400 and unknown tools stay 200", HttpStateless),
        ("MCP HTTP answers tools/call as an SSE stream, and closing the stream cancels the call", HttpSse),
        ("MCP HTTP subscriptions/listen streams its acknowledgement and closure", HttpSubscriptions),
        ("MCP HTTP refuses a body above 32 MiB while reading it and parses one at the limit", HttpLimits),
        ("MCP logs never contain a whole session id or the bearer token", LogHygiene),
        ("MCP Studio's write ledger tells its own saves and deletes from outside changes, and the watcher debounce is bounded", WriteLedger),
        ("MCP --describe manifest and mcp-server.json are structurally valid and state what the server can do", Describe)
    ];

    // ═══════════════ In-memory harness ═══════════════
    private sealed class InMemoryPipe : Stream
    {
        private readonly Channel<byte[]> channel = Channel.CreateUnbounded<byte[]>();
        private ReadOnlyMemory<byte> current;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (current.IsEmpty)
            {
                if (!await channel.Reader.WaitToReadAsync(cancellationToken)) return 0;
                if (channel.Reader.TryRead(out var chunk)) current = chunk;
            }
            var count = Math.Min(buffer.Length, current.Length);
            current[..count].CopyTo(buffer);
            current = current[count..];
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!channel.Writer.TryWrite(buffer.ToArray())) throw new IOException("The pipe is closed.");
            return ValueTask.CompletedTask;
        }
        public override void Write(byte[] buffer, int offset, int count) { if (!channel.Writer.TryWrite(buffer.AsSpan(offset, count).ToArray())) throw new IOException("The pipe is closed."); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public void Complete() => channel.Writer.TryComplete();
        protected override void Dispose(bool disposing) { Complete(); base.Dispose(disposing); }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly InMemoryPipe toServer = new();
        private readonly InMemoryPipe fromServer = new();
        public Harness(string name, McpLog? log = null, McpLaunchPolicy? policy = null)
        {
            EnsureRuntime();
            Workspace = Path.Combine(Root, name);
            Directory.CreateDirectory(Workspace);
            Log = log ?? McpLog.Silent;
            Service = new TestyMcpService(Workspace, Log, sampleApp: null, policy);
            Server = new McpServer(Service, Log);
            Transport = McpStdioTransport.RunAsync(Server, toServer, fromServer, Log, CancellationToken.None);
            _ = Transport.ContinueWith(_ => fromServer.Complete(), TaskScheduler.Default);
            Client = new McpLineClient(fromServer, toServer);
        }
        public string Workspace { get; }
        public McpLog Log { get; }
        public TestyMcpService Service { get; }
        public McpServer Server { get; }
        public McpLineClient Client { get; }
        public Task<int> Transport { get; }
        public async Task<JsonElement> InitializeAsync(string version = "2025-06-18")
        {
            var result = Result(await Client.RequestAsync("initialize", new JsonObject { ["protocolVersion"] = version, ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "unit", ["version"] = "1" } }));
            await Client.NotifyAsync("notifications/initialized");
            return result;
        }
        /// <summary>The pid of the one app this server has launched and not yet closed, once it is listed.</summary>
        public async Task<int> LaunchedPidAsync()
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (true)
            {
                var apps = (await Client.CallToolAsync("get_workspace_info")).RequireOk("get_workspace_info").Structured.GetProperty("launchedApps");
                if (apps.GetArrayLength() > 0) return apps[0].GetProperty("pid").GetInt32();
                if (DateTimeOffset.UtcNow > deadline) throw new InvalidOperationException("The launched app was never listed by get_workspace_info.");
                await Task.Delay(50);
            }
        }
        public async Task<int> LaunchedCountAsync() =>
            (await Client.CallToolAsync("get_workspace_info")).RequireOk("get_workspace_info").Structured.GetProperty("counts").GetProperty("launchedApps").GetInt32();
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            try { await Transport.WaitAsync(TimeSpan.FromSeconds(10)); } catch (TimeoutException) { }
            await Service.DisposeAsync();
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static JsonElement Result(JsonElement response)
    {
        if (response.TryGetProperty("error", out var error)) throw new InvalidOperationException("JSON-RPC error " + error.GetRawText());
        return response.GetProperty("result");
    }
    private static int ErrorCode(JsonElement response) => response.GetProperty("error").GetProperty("code").GetInt32();
    private static string ErrorText(JsonElement response) => response.GetProperty("error").GetProperty("message").GetString() ?? "";
    private static object CustomerSteps(string name) => new object[]
    {
        new { action = "click", selector = "id:ResetButton" },
        new { action = "typeText", selector = "id:CustomerName", value = name },
        new { action = "assertText", selector = "id:StatusMessage", value = "Customer added: " + name }
    };
    private static string[] ActionNames => Enum.GetNames<StepAction>().Select(JsonNamingPolicy.CamelCase.ConvertName).ToArray();
    private static JsonObject Meta(string version = "2026-07-28") => new()
    {
        [McpServer.MetaProtocolVersion] = version, [McpServer.MetaClientInfo] = new JsonObject { ["name"] = "unit", ["version"] = "1" }, [McpServer.MetaClientCapabilities] = new JsonObject()
    };
    private static async Task UntilAsync(Func<bool> condition, string failure, int timeoutMs = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs) throw new InvalidOperationException(failure);
            await Task.Delay(25);
        }
    }
    private static async Task UntilAsync(Func<Task<bool>> condition, string failure, int timeoutMs = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (!await condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs) throw new InvalidOperationException(failure);
            await Task.Delay(50);
        }
    }

    // ═══════════════ Fixtures ═══════════════
    /// <summary>The apphost of a launched .NET program finds its runtime through DOTNET_ROOT when none is installed system-wide.</summary>
    private static void EnsureRuntime()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ROOT"))) return;
        var host = Environment.ProcessPath;
        if (host is not null && Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) Environment.SetEnvironmentVariable("DOTNET_ROOT", Path.GetDirectoryName(host));
    }
    /// <summary>tests\Testy.IdleFixture\bin\&lt;configuration&gt;\&lt;framework&gt;\Testy.IdleFixture.exe: a GUI-subsystem program that sleeps and never shows a window.</summary>
    private static string IdleFixture
    {
        get
        {
            var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var tests = output.Parent?.Parent?.Parent?.Parent ?? throw new InvalidOperationException("The test output folder is not inside the tests folder.");
            var path = Path.Combine(tests.FullName, "Testy.IdleFixture", "bin", output.Parent!.Name, output.Name, "Testy.IdleFixture.exe");
            Check(File.Exists(path), "The idle fixture was not built: " + path);
            return path;
        }
    }
    /// <summary>launch_app arguments for a program that runs for 30 s without a window: the call keeps waiting until it is cancelled or times out.</summary>
    private static object IdleLaunch(int waitSeconds, bool keepOpen = false) => new { exe = IdleFixture, args = new[] { "30000" }, waitForWindowSeconds = waitSeconds, keepOpen };
    private static async Task RequireGoneAsync(int pid, string what, int timeoutMs = 8000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            if (!Alive(pid)) return;
            if (DateTimeOffset.UtcNow > deadline) { Kill(pid); throw new InvalidOperationException($"{what} (pid {pid}) was not closed."); }
            await Task.Delay(100);
        }
    }
    private static bool Alive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
    }
    private static void Kill(int pid)
    {
        try { using var process = Process.GetProcessById(pid); process.Kill(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    // ═══════════════ Lifecycle and protocol ═══════════════
    private static async Task InitializeNegotiation()
    {
        await using var harness = new Harness("initialize");
        foreach (var version in McpServer.HandshakeVersions)
        {
            var result = Result(await harness.Client.RequestAsync("initialize", new JsonObject { ["protocolVersion"] = version, ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "unit", ["version"] = "1" } }));
            Check(result.GetProperty("protocolVersion").GetString() == version, $"Requested {version} was not echoed.");
            Check(result.GetProperty("serverInfo").GetProperty("name").GetString() == "testy", "serverInfo.name must be testy.");
            Check(result.GetProperty("serverInfo").GetProperty("version").GetString() == McpServer.Version, "serverInfo.version must be the product version.");
            var capabilities = result.GetProperty("capabilities");
            foreach (var name in new[] { "tools", "resources", "prompts", "logging" }) Check(capabilities.TryGetProperty(name, out _), $"capabilities.{name} is missing.");
            Check(capabilities.GetProperty("tools").GetProperty("listChanged").ValueKind == JsonValueKind.False, "tools.listChanged must be false.");
            Check(result.GetProperty("instructions").GetString()!.Contains("get_workspace_info", StringComparison.Ordinal), "instructions must teach the workflow.");
        }
        foreach (var unknown in new[] { "1999-01-01", "2026-07-28", "not-a-version" })
        {
            var result = Result(await harness.Client.RequestAsync("initialize", new JsonObject { ["protocolVersion"] = unknown, ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "unit", ["version"] = "1" } }));
            Check(result.GetProperty("protocolVersion").GetString() == McpServer.LatestHandshakeVersion, $"An unsupported handshake version ({unknown}) must be answered with {McpServer.LatestHandshakeVersion}.");
        }
        var missing = Result(await harness.Client.RequestAsync("initialize", new JsonObject { ["capabilities"] = new JsonObject() }));
        Check(missing.GetProperty("protocolVersion").GetString() == McpServer.LatestHandshakeVersion, "initialize without a version must negotiate the latest handshake version.");
        Check(McpServer.SupportedVersions.SequenceEqual(new[] { "2026-07-28", "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" }), "Supported versions changed unexpectedly.");
    }

    private static async Task InitializedAndPing()
    {
        await using var harness = new Harness("ping");
        await harness.InitializeAsync();
        var before = harness.Client.RawLines.Count;
        await harness.Client.NotifyAsync("notifications/initialized");
        await harness.Client.NotifyAsync("notifications/unknown", new { anything = true });
        await Task.Delay(300);
        Check(harness.Client.RawLines.Count == before, "Notifications must not be answered.");
        var ping = Result(await harness.Client.RequestAsync("ping"));
        Check(ping.ValueKind == JsonValueKind.Object && !ping.EnumerateObject().Any(), "ping must return an empty object for handshake revisions.");
        var level = Result(await harness.Client.RequestAsync("logging/setLevel", new { level = "debug" }));
        Check(level.ValueKind == JsonValueKind.Object, "logging/setLevel must be accepted.");
        Check(ErrorCode(await harness.Client.RequestAsync("logging/setLevel", new { level = "loud" })) == JsonRpc.InvalidParams, "An unknown log level must be -32602.");
        Check(harness.Client.UnparseableLines == 0 && harness.Client.RawLines.All(l => l.StartsWith('{') && l.EndsWith('}')), "Every stdout line must be one JSON object.");
        Check(harness.Client.Notifications.Count == 0, "The server must not send unsolicited notifications.");
    }

    private static async Task ToolsList()
    {
        await using var harness = new Harness("tools");
        await harness.InitializeAsync();
        var tools = Result(await harness.Client.RequestAsync("tools/list")).GetProperty("tools").EnumerateArray().ToList();
        var names = tools.Select(t => t.GetProperty("name").GetString()!).ToList();
        Check(names.Distinct(StringComparer.Ordinal).Count() == names.Count, "Tool names must be unique.");
        Check(names.SequenceEqual(ExpectedTools), "Tool names or order changed: " + string.Join(", ", names));
        Check(McpVerifierTools().SequenceEqual(ExpectedTools), "verify-mcp expects a different tool list than the unit checks.");
        foreach (var tool in tools)
        {
            var name = tool.GetProperty("name").GetString();
            Check(Regex.IsMatch(name!, "^[a-z][a-z0-9_]{1,63}$"), $"{name} is not a snake_case tool name.");
            var description = tool.GetProperty("description").GetString()!;
            Check(description.Length >= 40, $"{name} needs a descriptive description.");
            Check(!description.Contains("observe_application", StringComparison.Ordinal) && !description.Contains("perform_ui_action", StringComparison.Ordinal), $"{name} names a tool that does not exist on this server.");
            Check(tool.GetProperty("title").GetString()!.Length > 0, $"{name} needs a title.");
            var schema = tool.GetProperty("inputSchema");
            Check(schema.GetProperty("type").GetString() == "object", $"{name}.inputSchema.type must be object.");
            var properties = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            if (schema.TryGetProperty("required", out var required)) Check(required.EnumerateArray().All(r => properties.Contains(r.GetString()!)), $"{name} requires an undeclared property.");
            foreach (var property in schema.GetProperty("properties").EnumerateObject()) Check(property.Value.TryGetProperty("description", out _), $"{name}.{property.Name} lacks a description.");
            Check(!properties.Contains("id") && !properties.Contains("screenshotMaxWidth"), $"{name} still advertises an earlier argument name.");
            var output = tool.GetProperty("outputSchema");
            Check(output.GetProperty("type").GetString() == "object", $"{name}.outputSchema.type must be object.");
            foreach (var property in output.GetProperty("properties").EnumerateObject()) Check(property.Value.TryGetProperty("description", out _), $"{name} output {property.Name} lacks a description.");
            var annotations = tool.GetProperty("annotations");
            foreach (var hint in new[] { "readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint" })
                Check(annotations.GetProperty(hint).ValueKind is JsonValueKind.True or JsonValueKind.False, $"{name}.annotations.{hint} must be boolean.");
        }
        JsonElement Tool(string name) => tools.Single(t => t.GetProperty("name").GetString() == name);
        bool Hint(string name, string hint) => Tool(name).GetProperty("annotations").GetProperty(hint).GetBoolean();
        var stepSchema = Tool("create_test").GetProperty("inputSchema").GetProperty("properties").GetProperty("steps").GetProperty("items");
        var actions = stepSchema.GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Check(actions.SequenceEqual(ActionNames), "The step action enum must equal StepAction's names exactly.");
        var performSchema = Tool("perform_step").GetProperty("inputSchema").GetProperty("properties").GetProperty("step");
        Check(performSchema.GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).SequenceEqual(actions), "perform_step must share the step schema.");
        Check(McpResources.Actions.Select(a => a.Action).OrderBy(a => a, StringComparer.Ordinal).SequenceEqual(ActionNames.OrderBy(a => a, StringComparer.Ordinal)), "The action catalog must cover every StepAction exactly once.");
        foreach (var mutating in new[] { "create_test", "update_test", "delete_test", "launch_app", "close_app", "activate_app", "perform_step", "run_test", "cancel_run" }) Check(!Hint(mutating, "readOnlyHint"), $"{mutating} must not be read-only.");
        foreach (var reading in new[] { "get_workspace_info", "list_tests", "get_test", "validate_test", "list_apps", "list_runs", "get_run", "get_run_screenshot", "find_app" }) Check(Hint(reading, "readOnlyHint"), $"{reading} must be read-only.");
        // inspect_app and screenshot_app start the named app with launch true: a client that approves read-only tools unasked must still ask.
        foreach (var starting in new[] { "inspect_app", "screenshot_app" })
        {
            Check(!Hint(starting, "readOnlyHint") && Hint(starting, "destructiveHint") && Hint(starting, "openWorldHint") && Hint(starting, "idempotentHint"), $"{starting} can start a program (launch true), so it is not read-only.");
            Check(Tool(starting).GetProperty("inputSchema").GetProperty("properties").TryGetProperty("launch", out _), $"{starting} still takes launch.");
        }
        foreach (var tool in tools.Where(t => t.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("launch", out _) || t.GetProperty("name").GetString() is "launch_app" or "run_test"))
            Check(!tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean() && tool.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean(), $"{tool.GetProperty("name").GetString()} can start a program and must not be read-only.");
        // find_app is offered to Cortex's model like list_apps: no Cortex group of its own and not reserved for the human-controlled pane.
        var findMeta = Tool("find_app").GetProperty("_meta");
        Check(findMeta.GetProperty("cortex/humanOnly").ValueKind == JsonValueKind.False && findMeta.GetProperty("cortex/usesModel").ValueKind == JsonValueKind.False
            && findMeta.GetProperty("cortex/group").GetRawText() == Tool("list_apps").GetProperty("_meta").GetProperty("cortex/group").GetRawText(), "find_app must be model-visible with list_apps' Cortex metadata.");
        // Starting a program and sending input have side effects outside Testy; results read from applications may carry untrusted content.
        Check(Hint("launch_app", "destructiveHint") && Hint("launch_app", "openWorldHint") && !Hint("launch_app", "idempotentHint"), "launch_app must be annotated as having side effects in the open world.");
        foreach (var open in new[] { "list_apps", "launch_app", "inspect_app", "screenshot_app", "perform_step", "run_test", "get_run", "get_run_screenshot" }) Check(Hint(open, "openWorldHint"), $"{open} returns content from applications and must set openWorldHint.");
        foreach (var closed in new[] { "get_workspace_info", "list_tests", "get_test", "create_test", "update_test", "delete_test", "validate_test", "list_runs" }) Check(!Hint(closed, "openWorldHint"), $"{closed} touches only the workspace.");
        Check(Hint("perform_step", "destructiveHint") && Hint("run_test", "destructiveHint") && Hint("cancel_run", "destructiveHint") && Hint("cancel_run", "idempotentHint"), "Tools that act on applications must be marked destructive.");
        Check(Tool("launch_app").GetProperty("description").GetString()!.Contains("Start a program on this PC", StringComparison.Ordinal), "launch_app must say that it starts a program.");
        Check(Tool("run_test").GetProperty("description").GetString()!.Contains("started on this PC", StringComparison.Ordinal), "run_test must say that exe starts a program.");
        Check(Tool("inspect_app").GetProperty("inputSchema").GetProperty("properties").GetProperty("maxElements").GetProperty("default").GetInt32() == 150, "inspect_app returns 150 controls by default.");
        Check(Tool("perform_step").GetProperty("inputSchema").GetProperty("properties").GetProperty("maxElements").GetProperty("default").GetInt32() == 40, "perform_step returns 40 controls by default.");
    }
    private static string[] McpVerifierTools() => Testy.Cli.McpVerifier.ExpectedTools;

    private static async Task ProtocolErrors()
    {
        await using var harness = new Harness("errors");
        await harness.InitializeAsync();
        Check(ErrorCode(await harness.Client.RequestAsync("nope/method")) == JsonRpc.MethodNotFound, "Unknown methods must be -32601.");
        Check(ErrorCode(await harness.Client.RequestAsync("tools/call")) == JsonRpc.InvalidParams, "tools/call without params must be -32602.");
        Check(ErrorCode(await harness.Client.RequestAsync("tools/call", new { name = "list_tests", arguments = "text" })) == JsonRpc.InvalidParams, "Non-object arguments must be -32602.");
        Check(ErrorCode(await harness.Client.RequestAsync("resources/read")) == JsonRpc.InvalidParams, "resources/read without uri must be -32602.");
        Check(ErrorCode(await harness.Client.RequestAsync("prompts/get", new { name = "unknown" })) == JsonRpc.InvalidParams, "An unknown prompt must be -32602.");
        Check(ErrorCode(await harness.Client.RequestAsync("subscriptions/listen")) == JsonRpc.MethodNotFound, "subscriptions/listen is not offered on a handshake connection.");
        Check(ErrorCode(await harness.Client.RequestAsync("resources/subscribe", new { uri = "testy://workspace" })) == JsonRpc.MethodNotFound, "resources/subscribe is not offered.");
        Check(ErrorCode(await harness.Client.RequestAsync("completion/complete")) == JsonRpc.MethodNotFound, "completion/complete is not offered.");
        var badToken = await harness.Client.RequestAsync("tools/call", new JsonObject { ["name"] = "list_tests", ["arguments"] = new JsonObject(), ["_meta"] = new JsonObject { ["progressToken"] = true } });
        Check(ErrorCode(badToken) == JsonRpc.InvalidParams && ErrorText(badToken).Contains("progressToken", StringComparison.Ordinal), "A progressToken that is neither string nor integer must be -32602.");
        Check(ErrorCode(await harness.Client.RequestAsync("tools/call", new JsonObject { ["name"] = "list_tests", ["_meta"] = new JsonObject { ["progressToken"] = 1.5 } })) == JsonRpc.InvalidParams, "A fractional progressToken must be -32602.");
        Check(ErrorCode(await harness.Client.RequestAsync("ping", new JsonObject { ["_meta"] = 5 })) == JsonRpc.InvalidParams, "A _meta that is not an object must be -32602.");
        var before = harness.Client.UnexpectedResponses.Count;
        await harness.Client.SendAsync("{not json at all");
        await harness.Client.SendAsync("[]");
        await harness.Client.SendAsync("\"a string\"");
        await harness.Client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"ping\"}");
        await harness.Client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":1.5,\"method\":\"ping\"}");
        await harness.Client.SendAsync("{\"id\":7,\"method\":\"ping\"}");
        await harness.Client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"ping\",\"params\":[1]}");
        await harness.Client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":9}");
        await UntilAsync(() => harness.Client.UnexpectedResponses.Count >= before + 8, "Eight error responses were expected.");
        var responses = harness.Client.UnexpectedResponses.Skip(before).ToList();
        Check(responses.Count == 8, $"Expected 8 error responses, got {responses.Count}.");
        var nullIdCodes = responses.Where(r => r.GetProperty("id").ValueKind == JsonValueKind.Null).Select(ErrorCode).OrderBy(c => c).ToList();
        Check(nullIdCodes.SequenceEqual(new[] { JsonRpc.ParseError, JsonRpc.InvalidRequest, JsonRpc.InvalidRequest, JsonRpc.InvalidRequest, JsonRpc.InvalidRequest }),
            "Malformed JSON must be -32700, and an empty batch, a non-object message, a null id and a fractional id must be -32600, all with a null id: " + string.Join(",", nullIdCodes));
        int CodeFor(int id) => ErrorCode(responses.Single(r => r.GetProperty("id").ValueKind == JsonValueKind.Number && r.GetProperty("id").GetInt32() == id));
        Check(CodeFor(7) == JsonRpc.InvalidRequest, "A missing jsonrpc field must be -32600 with the id echoed.");
        Check(CodeFor(8) == JsonRpc.InvalidParams, "Array params must be -32602.");
        Check(CodeFor(9) == JsonRpc.InvalidRequest, "A message without method/result/error must be -32600.");
        var whole = await harness.Client.RequestAsync("ping", null, JsonNode.Parse("12.0")!);
        Check(whole.TryGetProperty("result", out _) && whole.GetProperty("id").GetRawText() == "12.0", "A number id without a fraction (12.0) is an integer id and is echoed as sent.");
        Check(harness.Client.UnparseableLines == 0, "Error responses must still be single JSON objects.");
        Check(JsonRpc.ServerBusy > -32000 || JsonRpc.ServerBusy < -32768, "The server's own error code must lie outside the JSON-RPC reserved range.");
    }

    private static async Task IdEcho()
    {
        await using var harness = new Harness("ids");
        await harness.InitializeAsync();
        var text = await harness.Client.RequestAsync("ping", null, JsonValue.Create("request-abc"));
        Check(text.GetProperty("id").ValueKind == JsonValueKind.String && text.GetProperty("id").GetString() == "request-abc", "String ids must be echoed as strings.");
        var number = await harness.Client.RequestAsync("ping", null, JsonValue.Create(4242));
        Check(number.GetProperty("id").ValueKind == JsonValueKind.Number && number.GetProperty("id").GetRawText() == "4242", "Number ids must be echoed as numbers.");
        var digits = await harness.Client.RequestAsync("ping", null, JsonValue.Create("4242"));
        Check(digits.GetProperty("id").ValueKind == JsonValueKind.String, "The string \"4242\" must stay a string.");
    }

    private static async Task DuplicateIds()
    {
        await using var harness = new Harness("duplicate-ids");
        await harness.InitializeAsync();
        // Raw lines, so the client's own bookkeeping does not pair the refusal with the first request.
        var launch = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "dup", ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = "launch_app", ["arguments"] = JsonRpc.ToNode(IdleLaunch(30)) } };
        await harness.Client.SendAsync(launch.ToJsonString());
        var pid = await harness.LaunchedPidAsync();
        // The same id while the first request is in flight: refused, and the answer names the id.
        await harness.Client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"dup\",\"method\":\"ping\"}");
        await UntilAsync(() => harness.Client.UnexpectedResponses.Count == 1, "The duplicate id was not answered.");
        var refused = harness.Client.UnexpectedResponses[0];
        Check(ErrorCode(refused) == JsonRpc.InvalidRequest && ErrorText(refused).Contains("\"dup\"", StringComparison.Ordinal), "A duplicate in-flight id must be -32600 and name the id: " + refused.GetRawText());
        Check(Alive(pid), "The refusal must not end the request that owns the id.");
        // "1" and 1 are different ids.
        var textOne = harness.Client.StartAsync("subscriptions/listen", new JsonObject { ["notifications"] = new JsonObject(), ["_meta"] = Meta() }, JsonValue.Create("1"));
        await harness.Client.WaitForNotificationAsync(n => n.GetProperty("method").GetString() == "notifications/subscriptions/acknowledged", TimeSpan.FromSeconds(5));
        var numberOne = await harness.Client.RequestAsync("ping", null, JsonValue.Create(1));
        Check(numberOne.TryGetProperty("result", out _), "The number 1 must not collide with the string \"1\": " + numberOne.GetRawText());
        await harness.Client.NotifyAsync("notifications/cancelled", new { requestId = "1" });
        await harness.Client.NotifyAsync("notifications/cancelled", new { requestId = "dup" });
        await RequireGoneAsync(pid, "The idle fixture of the cancelled launch");
        await UntilAsync(() => harness.Server.ListenStreams == 0, "The cancelled subscription stream stayed open.");
        var reused = await harness.Client.RequestAsync("ping", null, JsonValue.Create("dup"));
        Check(reused.TryGetProperty("result", out _), "An id can be used again once its request has ended.");
        Check(!textOne.IsCompletedSuccessfully && harness.Client.UnexpectedResponses.Count == 1, "The cancelled requests must not be answered.");
    }

    private static async Task ToolErrors()
    {
        await using var harness = new Harness("tool-errors");
        await harness.InitializeAsync();
        var missing = await harness.Client.CallToolAsync("get_test", new { testId = "does-not-exist" });
        Check(missing.Error is null && missing.IsError && missing.Text.Contains("list_tests", StringComparison.Ordinal), "A missing test must be a tool error with guidance.");
        Check(missing.Result!.Value.GetProperty("content")[0].GetProperty("type").GetString() == "text", "Tool errors carry text content.");
        var unknown = await harness.Client.CallToolAsync("no_such_tool");
        Check(unknown.ErrorCode == JsonRpc.InvalidParams && unknown.Error!.Value.GetProperty("message").GetString()!.Contains("Unknown tool", StringComparison.Ordinal), "Unknown tools must be -32602 protocol errors.");
        var badAction = await harness.Client.CallToolAsync("create_test", new { name = "x", intent = "y", steps = new object[] { new { action = "explode", selector = "id:x" } } });
        Check(badAction.IsError && badAction.Text.Contains("Unknown action 'explode'", StringComparison.Ordinal) && badAction.Text.Contains("click", StringComparison.Ordinal), "Unknown actions must list the allowed actions: " + badAction.Text);
        var badArgument = await harness.Client.CallToolAsync("get_test", new { testId = "abc", extra = 1 });
        Check(badArgument.IsError && badArgument.Text.Contains("Unknown argument 'extra'", StringComparison.Ordinal), "Unknown arguments must be rejected with the allowed list.");
        var badType = await harness.Client.CallToolAsync("list_runs", new { limit = "ten" });
        Check(badType.IsError && badType.Text.Contains("must be an integer", StringComparison.Ordinal), "Type mismatches must be explained.");
        var outOfRange = await harness.Client.CallToolAsync("list_runs", new { limit = 5000 });
        Check(outOfRange.IsError && outOfRange.Text.Contains("between", StringComparison.Ordinal), "Range violations must be explained.");
        var badTimeout = await harness.Client.CallToolAsync("create_test", new { name = "x", intent = "y", steps = new object[] { new { action = "click", selector = "id:x", timeoutMs = 10 } } });
        Check(badTimeout.IsError && badTimeout.Text.Contains("100–60000", StringComparison.Ordinal), "The timeout range must be named: " + badTimeout.Text);
        var badSelector = await harness.Client.CallToolAsync("create_test", new { name = "x", intent = "y", steps = new object[] { new { action = "click", selector = "Reset" } } });
        Check(badSelector.IsError && badSelector.Text.Contains("id:", StringComparison.Ordinal), "Selector syntax errors must mention the prefixes: " + badSelector.Text);
        var missingPid = await harness.Client.CallToolAsync("inspect_app", new { });
        Check(missingPid.IsError && missingPid.Text.Contains("Missing required argument 'pid'", StringComparison.Ordinal), "Missing required arguments must be named.");
        var noWindow = await harness.Client.CallToolAsync("inspect_app", new { pid = Environment.ProcessId, includeScreenshot = false });
        Check(noWindow.IsError, "Inspecting a windowless process must be a tool error, not a protocol error.");
        var badMode = await harness.Client.CallToolAsync("run_test", new { testId = "x", mode = "fast", pid = 1 });
        Check(badMode.IsError, "An unknown run mode must be a tool error.");
        var notLaunched = await harness.Client.CallToolAsync("close_app", new { pid = Environment.ProcessId });
        Check(notLaunched.IsError && notLaunched.Text.Contains("was not launched by this server", StringComparison.Ordinal), "close_app closes only apps this server launched: " + notLaunched.Text);
        var noRun = await harness.Client.CallToolAsync("cancel_run", new { runId = "nope" });
        Check(noRun.IsError && noRun.Text.Contains("list_runs", StringComparison.Ordinal), "cancel_run of an unknown run must say where runs are listed.");
        var beyond = await harness.Client.CallToolAsync("get_run_screenshot", new { runId = "nope", stepNumber = 1 });
        Check(beyond.IsError, "A screenshot of an unknown run must be a tool error.");
        Check(new WorkspaceStore(harness.Workspace).LoadTests().Count == 0, "Failed create_test calls must not save anything.");
    }

    private static async Task ArgumentRules()
    {
        await using var harness = new Harness("argument-rules");
        await harness.InitializeAsync();
        var upper = await harness.Client.CallToolAsync("validate_test", new { name = "x", steps = new object[] { new { action = "Click", selector = "id:x" } } });
        Check(upper.RequireOk("validate_test").Structured.GetProperty("problems")[0].GetString()!.Contains("Unknown action 'Click'", StringComparison.Ordinal), "Actions are spelled as the schema's enum spells them (click, not Click).");
        var whole = await harness.Client.CallToolAsync("list_runs", JsonNode.Parse("{\"limit\":5.0}"));
        Check(!whole.IsError, "5.0 is an integer for JSON Schema and must be accepted: " + whole.Text);
        var fraction = await harness.Client.CallToolAsync("list_runs", JsonNode.Parse("{\"limit\":5.5}"));
        Check(fraction.IsError && fraction.Text.Contains("must be an integer", StringComparison.Ordinal), "5.5 is not an integer.");
        var created = (await harness.Client.CallToolAsync("create_test", new { name = "Alias", intent = "Earlier argument names.", steps = CustomerSteps("Ada") })).RequireOk("create_test").Structured;
        var id = created.GetProperty("testId").GetString()!;
        var byAlias = await harness.Client.CallToolAsync("get_test", new { id });
        Check(!byAlias.IsError && byAlias.Structured.GetProperty("testId").GetString() == id, "The earlier argument name id is still accepted for testId.");
        var twice = await harness.Client.CallToolAsync("get_test", new { id, testId = id });
        Check(twice.IsError && twice.Text.Contains("given twice", StringComparison.Ordinal), "An argument given under both names is refused.");
        // Wording: one "Step", the action in brackets, what is allowed.
        var noSelector = await harness.Client.CallToolAsync("create_test", new { name = "x", intent = "y", steps = new object[] { new { action = "click" } } });
        Check(noSelector.IsError && noSelector.Text == "Step 1 (click): a selector is required (id:, name:, path: or query:).", "The missing-selector message changed: " + noSelector.Text);
        var stepNumber = await harness.Client.CallToolAsync("get_run_screenshot", new { runId = "abc", stepNumber = 0 });
        Check(stepNumber.IsError && stepNumber.Text == "Argument 'stepNumber' must be at least 1.", "A one-sided range must not name the largest integer: " + stepNumber.Text);
        var badId = await harness.Client.CallToolAsync("get_test", new { testId = "not a valid id!" });
        Check(badId.IsError && badId.Text.StartsWith("Argument 'testId':", StringComparison.Ordinal), "An id error must name its argument: " + badId.Text);
        var badRunId = await harness.Client.CallToolAsync("get_run", new { runId = "../x" });
        Check(badRunId.IsError && badRunId.Text.StartsWith("Argument 'runId':", StringComparison.Ordinal), "A run id error must name its argument: " + badRunId.Text);
        var unexpected = await harness.Client.CallToolAsync("inspect_app", new { pid = Environment.ProcessId, includeScreenshot = false });
        Check(unexpected.IsError && !Regex.IsMatch(unexpected.Text, @"\b\w+Exception\b"), "Tool errors must not name .NET exception types: " + unexpected.Text);
        var noTarget = await harness.Client.CallToolAsync("run_test", new { testId = id });
        Check(noTarget.IsError && noTarget.Text.Contains("pid", StringComparison.Ordinal) && noTarget.Text.Contains("exe", StringComparison.Ordinal), "run_test without pid and exe must say what to pass.");
        (await harness.Client.CallToolAsync("update_test", new { testId = id, targetPath = @"C:\Apps\Desk\Desk.exe" })).RequireOk("update_test");
        var targeted = await harness.Client.CallToolAsync("run_test", new { testId = id });
        Check(targeted.IsError && targeted.Text.Contains(@"This test targets C:\Apps\Desk\Desk.exe", StringComparison.Ordinal), "run_test must name the test's target program: " + targeted.Text);
        var both = await harness.Client.CallToolAsync("run_test", new { testId = id, pid = 4, exe = IdleFixture });
        Check(both.IsError && both.Text.Contains("not both", StringComparison.Ordinal), "pid and exe together are refused.");
    }

    // ═══════════════ Workspace tools ═══════════════
    private static async Task WorkspaceRoundTrip()
    {
        await using var harness = new Harness("roundtrip");
        await harness.InitializeAsync();
        var created = (await harness.Client.CallToolAsync("create_test", new { name = "Add Ada", intent = "Verify the confirmation.", category = "Customers", steps = CustomerSteps("Ada") })).RequireOk("create_test").Structured;
        var id = created.GetProperty("testId").GetString()!;
        Check(created.GetProperty("stepCount").GetInt32() == 3 && created.GetProperty("warnings").GetArrayLength() == 0, "create_test must report the step count and no warnings for a sound test.");
        var file = Path.Combine(harness.Workspace, "tests", id + ".json");
        Check(File.Exists(file), "The test file must be written under <workspace>/tests.");
        var stored = new WorkspaceStore(harness.Workspace).LoadTests().Single();
        Check(stored.Id == id && stored.Name == "Add Ada" && stored.Category == "Customers" && stored.Steps.Count == 3 && stored.Steps[2].Action == StepAction.AssertText, "Studio's store must load the saved test.");
        TestValidator.Validate(stored);
        var fetched = (await harness.Client.CallToolAsync("get_test", new { testId = id })).RequireOk("get_test").Structured;
        var steps = fetched.GetProperty("steps").EnumerateArray().ToList();
        Check(steps.Count == 3 && steps[1].GetProperty("value").GetString() == "Ada" && steps[1].GetProperty("action").GetString() == "typeText" && steps[1].GetProperty("timeoutMs").GetInt32() == 5000, "get_test must return the full steps with defaults applied.");
        Check(steps.All(s => s.GetProperty("title").GetString() is { Length: > 0 } title && title == title.Trim()), "Default titles must be generated without stray spaces.");
        var stepIds = steps.Select(s => s.GetProperty("id").GetString()!).ToList();
        var replacement = new object[]
        {
            new { id = stepIds[0], action = "click", selector = "id:ResetButton" },
            new { id = stepIds[1], action = "typeText", selector = "id:CustomerName", value = "Grace" },
            new { id = stepIds[2], action = "assertText", selector = "id:StatusMessage", value = "Customer added: Grace" }
        };
        var updated = (await harness.Client.CallToolAsync("update_test", new { testId = id, name = "Add Grace", steps = replacement })).RequireOk("update_test").Structured;
        Check(updated.GetProperty("name").GetString() == "Add Grace", "update_test must rename.");
        var after = (await harness.Client.CallToolAsync("get_test", new { testId = id })).RequireOk("get_test").Structured.GetProperty("steps").EnumerateArray().ToList();
        Check(after.Select(s => s.GetProperty("id").GetString()).SequenceEqual(stepIds) && after[1].GetProperty("value").GetString() == "Grace", "update_test must preserve supplied step ids and apply new values.");
        Check(new WorkspaceStore(harness.Workspace).LoadTests().Single().Name == "Add Grace", "The rename must be persisted for Studio.");
        var nothing = await harness.Client.CallToolAsync("update_test", new { testId = id });
        Check(nothing.IsError, "update_test without changes must explain what to supply.");
        var list = (await harness.Client.CallToolAsync("list_tests")).RequireOk("list_tests").Structured;
        Check(list.GetProperty("count").GetInt32() == 1 && list.GetProperty("tests")[0].GetProperty("testId").GetString() == id && list.GetProperty("tests")[0].GetProperty("stepCount").GetInt32() == 3
            && list.GetProperty("tests")[0].GetProperty("lastRun").ValueKind == JsonValueKind.Null, "list_tests must report the test without a run.");
        var invalid = (await harness.Client.CallToolAsync("validate_test", new { name = "Broken", steps = new object[] { new { action = "click", selector = "id:x", timeoutMs = 10 }, new { action = "fly", selector = "id:x" }, new { action = "click" } } })).RequireOk("validate_test").Structured;
        var problems = invalid.GetProperty("problems").EnumerateArray().Select(p => p.GetString()!).ToList();
        Check(!invalid.GetProperty("valid").GetBoolean() && problems.Count == 3 && problems[0].StartsWith("Step 1 (click):", StringComparison.Ordinal) && problems[1].StartsWith("Step 2:", StringComparison.Ordinal) && problems[2].StartsWith("Step 3 (click):", StringComparison.Ordinal),
            "validate_test must list every problem: " + string.Join(" | ", problems));
        var valid = (await harness.Client.CallToolAsync("validate_test", new { name = "Fine", targetPath = @"C:\Apps\Desk.exe", steps = CustomerSteps("Ada") })).RequireOk("validate_test").Structured;
        Check(valid.GetProperty("valid").GetBoolean() && valid.GetProperty("problems").GetArrayLength() == 0, "validate_test takes what create_test takes, targetPath included.");
        var noAssertion = (await harness.Client.CallToolAsync("validate_test", new { name = "Fine", steps = new object[] { new { action = "click", selector = "id:x" } } })).RequireOk("validate_test").Structured;
        Check(noAssertion.GetProperty("valid").GetBoolean() && noAssertion.GetProperty("warnings").GetArrayLength() == 1, "A test without assertions is valid but warned about.");
        Check(new WorkspaceStore(harness.Workspace).LoadTests().Count == 1, "validate_test must not save.");
        var weak = (await harness.Client.CallToolAsync("create_test", new { name = "add grace", intent = "Same name, no assertion.", steps = new object[] { new { action = "click", selector = "id:x" } } })).RequireOk("create_test").Structured;
        var warnings = weak.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToList();
        Check(warnings.Count == 2 && warnings.Any(w => w.Contains("no assertion", StringComparison.Ordinal)) && warnings.Any(w => w.Contains(id, StringComparison.Ordinal)),
            "create_test must warn about a missing assertion and about the test that already has this name: " + string.Join(" | ", warnings));
        (await harness.Client.CallToolAsync("delete_test", new { testId = weak.GetProperty("testId").GetString() })).RequireOk("delete_test");
        var deleted = (await harness.Client.CallToolAsync("delete_test", new { testId = id })).RequireOk("delete_test").Structured;
        Check(deleted.GetProperty("deleted").GetBoolean() && !File.Exists(file), "delete_test must remove the file.");
        Check((await harness.Client.CallToolAsync("list_tests")).RequireOk("list_tests").Structured.GetProperty("count").GetInt32() == 0, "The deleted test must be gone from list_tests.");
        Check((await harness.Client.CallToolAsync("delete_test", new { testId = id })).IsError, "Deleting twice must be a tool error.");
        var runs = (await harness.Client.CallToolAsync("list_runs")).RequireOk("list_runs").Structured;
        Check(runs.GetProperty("total").GetInt32() == 0, "A fresh workspace has no runs.");
        Check((await harness.Client.CallToolAsync("get_run", new { runId = "nope" })).IsError, "An unknown run must be a tool error.");
    }

    private static async Task ValidationCompleteness()
    {
        await using var harness = new Harness("validation");
        await harness.InitializeAsync();
        async Task<List<string>> Problems(object step)
        {
            var result = (await harness.Client.CallToolAsync("validate_test", new { name = "Candidate", steps = new[] { step, new { action = "assertExists", selector = "id:x", value = "" } } })).RequireOk("validate_test").Structured;
            return result.GetProperty("problems").EnumerateArray().Select(p => p.GetString()!).ToList();
        }
        foreach (var chord in new[] { "WIN+R", "ALT+F4", "ALT+TAB", "CTRL+ESC", "Hello", "CTRL+SHIFT+ALT+A+B", "CTRL+PLUS", "+" })
        {
            var problems = await Problems(new { action = "keyPress", selector = "", value = chord });
            Check(problems.Count == 1 && problems[0].StartsWith("Step 1 (keyPress):", StringComparison.Ordinal) && problems[0].Contains("ENTER", StringComparison.Ordinal) && problems[0].Contains("at most 4 keys", StringComparison.Ordinal),
                $"keyPress {chord} cannot run and must be refused with what is allowed: " + string.Join(" | ", problems));
        }
        foreach (var chord in new[] { "ENTER", "ctrl+a", "CTRL+SHIFT+S", "F12", "ALT+A", "7", "PAGEDOWN" })
            Check((await Problems(new { action = "keyPress", selector = "", value = chord })).Count == 0, $"keyPress {chord} is supported.");
        var toggle = await Problems(new { action = "toggle", selector = "id:x", value = "yes" });
        Check(toggle.Count == 1 && toggle[0].Contains("on/off", StringComparison.Ordinal) && toggle[0].Contains("true/false", StringComparison.Ordinal), "toggle yes must be refused with the accepted values: " + string.Join(" | ", toggle));
        foreach (var value in new[] { "", "On", "off", "TRUE", "false", "checked", "Unchecked", "1", "0" })
            Check((await Problems(new { action = "toggle", selector = "id:x", value })).Count == 0, $"toggle '{value}' is accepted.");
        var select = (await harness.Client.CallToolAsync("validate_test", new { name = "Candidate", steps = new object[] { new { action = "select", selector = "id:List" }, new { action = "assertExists", selector = "id:x" } } })).RequireOk("validate_test").Structured;
        Check(select.GetProperty("valid").GetBoolean() && select.GetProperty("warnings").EnumerateArray().Any(w => w.GetString()!.Contains("Step 1 (select)", StringComparison.Ordinal)),
            "A select without a value is valid only for an item selector and must be warned about.");
        var refused = await harness.Client.CallToolAsync("create_test", new { name = "x", intent = "y", steps = new object[] { new { action = "keyPress", value = "WIN+R" } } });
        Check(refused.IsError && refused.Text.Contains("WIN+R", StringComparison.Ordinal) && new WorkspaceStore(harness.Workspace).LoadTests().Count == 0, "create_test must not save a test with a key chord that cannot run.");
        Check(KeyChords.TryParse("CTRL+A", out var codes, out _) && codes.SequenceEqual(new ushort[] { 0x11, 0x41 }), "The key table yields virtual-key codes in chord order.");
        var coordinate = await Problems(new { action = "coordinateClick", x = 0, y = 0, selector = "", value = "" });
        Check(coordinate.Count == 0, "coordinateClick at 0,0 is valid.");
    }

    private static async Task OutputSchemas()
    {
        await using var harness = new Harness("schemas");
        await harness.InitializeAsync();
        var schemas = Result(await harness.Client.RequestAsync("tools/list")).GetProperty("tools").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("outputSchema"));
        Check(schemas.Count == ExpectedTools.Length, "Every tool declares an output schema.");
        WriteRun(harness.Workspace, "schema-run", "schema-test", RunStatus.Failed, withDiagnostics: true);
        var created = (await harness.Client.CallToolAsync("create_test", new { name = "Schema", intent = "Check schemas.", steps = CustomerSteps("Ada") })).RequireOk("create_test");
        var id = created.Structured.GetProperty("testId").GetString()!;
        var calls = new List<(string Tool, McpToolCall Call)>
        {
            ("create_test", created),
            ("get_workspace_info", (await harness.Client.CallToolAsync("get_workspace_info")).RequireOk("get_workspace_info")),
            ("list_tests", (await harness.Client.CallToolAsync("list_tests")).RequireOk("list_tests")),
            ("get_test", (await harness.Client.CallToolAsync("get_test", new { testId = id })).RequireOk("get_test")),
            ("validate_test", (await harness.Client.CallToolAsync("validate_test", new { name = "x", steps = CustomerSteps("Ada") })).RequireOk("validate_test")),
            ("update_test", (await harness.Client.CallToolAsync("update_test", new { testId = id, intent = "Changed." })).RequireOk("update_test")),
            ("list_runs", (await harness.Client.CallToolAsync("list_runs")).RequireOk("list_runs")),
            ("get_run", (await harness.Client.CallToolAsync("get_run", new { runId = "schema-run" })).RequireOk("get_run")),
            ("list_apps", (await harness.Client.CallToolAsync("list_apps")).RequireOk("list_apps")),
            ("delete_test", (await harness.Client.CallToolAsync("delete_test", new { testId = id })).RequireOk("delete_test"))
        };
        foreach (var (tool, call) in calls)
        {
            var violation = McpSchemaValidator.FirstViolation(schemas[tool], call.Structured, tool, declaredTopLevelOnly: true);
            Check(violation is null, "structuredContent does not satisfy its outputSchema: " + violation);
            var text = call.Result!.Value.GetProperty("content")[0].GetProperty("text").GetString()!;
            Check(JsonDocument.Parse(text).RootElement.ValueKind == JsonValueKind.Object, $"{tool} text content must be the serialized JSON.");
            Check(call.Result.Value.GetProperty("isError").ValueKind == JsonValueKind.False, $"{tool} must report isError false explicitly.");
        }
        // The observation views of inspect_app and perform_step, produced from a control tree without an application.
        var inspectElements = schemas["inspect_app"].GetProperty("properties").GetProperty("elements");
        var observation = JsonSerializer.SerializeToElement(TestyMcpService.Observation(SampleSnapshot(), details: true, includeOffscreen: true));
        Check(McpSchemaValidator.FirstViolation(inspectElements, observation.GetProperty("elements"), "inspect_app.elements") is null, "Element views must satisfy inspect_app's schema: " + McpSchemaValidator.FirstViolation(inspectElements, observation.GetProperty("elements"), "inspect_app.elements"));
        var performObservation = schemas["perform_step"].GetProperty("properties").GetProperty("observation");
        Check(McpSchemaValidator.FirstViolation(performObservation, observation, "perform_step.observation", declaredTopLevelOnly: true) is null, "Observations must satisfy perform_step's schema: " + McpSchemaValidator.FirstViolation(performObservation, observation, "perform_step.observation", declaredTopLevelOnly: true));
        // The validator itself: enum, minimum, maximum, nullable types, required and undeclared properties.
        static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
        var schema = Json("{\"type\":\"object\",\"properties\":{\"status\":{\"type\":\"string\",\"enum\":[\"passed\",\"failed\"]},\"count\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5},\"note\":{\"type\":[\"string\",\"null\"]}},\"required\":[\"status\"]}");
        Check(McpSchemaValidator.FirstViolation(schema, Json("{\"status\":\"passed\",\"count\":5,\"note\":null}")) is null, "A conforming value was refused.");
        Check(McpSchemaValidator.FirstViolation(schema, Json("{\"status\":\"maybe\"}"))!.StartsWith("$.status:", StringComparison.Ordinal), "An enum violation must be found at its path.");
        Check(McpSchemaValidator.FirstViolation(schema, Json("{\"status\":\"passed\",\"count\":9}"))!.Contains("maximum", StringComparison.Ordinal), "A maximum violation must be found.");
        Check(McpSchemaValidator.FirstViolation(schema, Json("{\"status\":\"passed\",\"count\":0}"))!.Contains("minimum", StringComparison.Ordinal), "A minimum violation must be found.");
        Check(McpSchemaValidator.FirstViolation(schema, Json("{\"count\":2}"))!.Contains("required", StringComparison.Ordinal), "A missing required property must be found.");
        Check(McpSchemaValidator.FirstViolation(schema, Json("{\"status\":\"passed\",\"note\":5}"))!.StartsWith("$.note:", StringComparison.Ordinal), "A type violation must be found.");
        Check(McpSchemaValidator.FirstViolation(schema, Json("{\"status\":\"passed\",\"extra\":1}"), declaredTopLevelOnly: true)!.StartsWith("$.extra:", StringComparison.Ordinal), "An undeclared top-level property must be found.");
    }

    private static async Task TextContent()
    {
        await using var harness = new Harness("text");
        await harness.InitializeAsync();
        var info = (await harness.Client.CallToolAsync("get_workspace_info")).RequireOk("get_workspace_info");
        var text = info.Result!.Value.GetProperty("content")[0].GetProperty("text").GetString()!;
        // Inside a JSON string a quote is written as \" (nothing else is possible); " and friends are what must not appear.
        Check(text.Contains("query:{\\\"type\\\":\\\"Button\\\"", StringComparison.Ordinal), "The selector reference must show a query selector with quotes escaped only by a backslash: " + text[..Math.Min(400, text.Length)]);
        Check(!text.Contains("\\u0022", StringComparison.Ordinal) && !text.Contains("\\u0027", StringComparison.Ordinal) && !text.Contains("\\u003C", StringComparison.OrdinalIgnoreCase) && !text.Contains("\\u2013", StringComparison.OrdinalIgnoreCase) && !text.Contains("\\u002B", StringComparison.OrdinalIgnoreCase),
            "Quotes, apostrophes, angle brackets, plus signs and dashes must be written literally in text content.");
        Check(!text.Contains('\n') && !text.Contains("  \"", StringComparison.Ordinal), "Text content must be compact JSON.");
        Check(JsonSerializer.Serialize(info.Structured) == JsonSerializer.Serialize(JsonDocument.Parse(text).RootElement), "The text content and structuredContent must be the same JSON.");
        const string name = "Überweisung prüfen – 顧客を追加 «Тест» it's <ok> + more";
        var created = (await harness.Client.CallToolAsync("create_test", new { name, intent = "Non-ASCII text round trip.", steps = new object[] { new { action = "assertText", selector = "name:Überweisung", value = "Сумма: 10 €" } } })).RequireOk("create_test").Structured;
        var listed = (await harness.Client.CallToolAsync("list_tests")).RequireOk("list_tests");
        Check(listed.Text.Contains(name, StringComparison.Ordinal), "A test name with non-ASCII characters must appear literally in list_tests text: " + listed.Text);
        var fetched = (await harness.Client.CallToolAsync("get_test", new { testId = created.GetProperty("testId").GetString() })).RequireOk("get_test");
        Check(fetched.Text.Contains("name:Überweisung", StringComparison.Ordinal) && fetched.Text.Contains("Сумма: 10 €", StringComparison.Ordinal), "Selectors and expected values must be copyable exactly as they are.");
        var resource = Result(await harness.Client.RequestAsync("resources/read", new { uri = "testy://workspace" })).GetProperty("contents")[0].GetProperty("text").GetString()!;
        Check(resource.Contains("query:{\\\"type\\\":\\\"Button\\\"", StringComparison.Ordinal) && !resource.Contains('\n') && !resource.Contains("\\u0022", StringComparison.Ordinal), "Resources use the same compact, literal JSON.");
    }

    /// <summary>A small control tree: a window, a named button, a long edit value, a disabled and an offscreen control, a list with a virtualized item, a password field.</summary>
    private static UiSnapshot SampleSnapshot()
    {
        static UiElementInfo Element(string id, string type, int depth, string name = "", string value = "", double x = 110, double y = 220) => new()
        {
            AutomationId = id, Selector = "id:" + id, ControlType = type, Depth = depth, Name = name, Value = value, IsEnabled = true,
            Bounds = new ElementBounds { X = x, Y = y, Width = 80, Height = 30 }
        };
        var window = Element("Desk", "Window", 0, "Customer Desk", x: 100, y: 200);
        window.Bounds = new ElementBounds { X = 100, Y = 200, Width = 800, Height = 600 };
        var save = Element("Save", "Button", 1, "Save");
        var notes = Element("Notes", "Edit", 1, "", new string('n', 9000));
        var disabled = Element("Disabled", "Button", 1, "Later"); disabled.IsEnabled = false;
        var hidden = Element("Hidden", "Button", 1, "Below the fold"); hidden.IsOffscreen = true;
        var list = Element("Orders", "List", 1); list.ChildCoverage = ChildCoverage.RealizedOnly; list.Capabilities = ["ItemContainer", "Scroll"];
        var item = Element("Order1", "ListItem", 2, "Order 1");
        item.Properties["uia.isSelected"] = new UiPropertyObservation { Status = UiPropertyStatus.Known, Value = JsonSerializer.SerializeToElement(true), Source = "unit" };
        item.Properties["uia.value"] = new UiPropertyObservation { Status = UiPropertyStatus.Unavailable, Source = "unit" };
        var byName = Element("", "Text", 2, "Total"); byName.Selector = "name:Total"; byName.AutomationId = "TotalLabel";
        var password = Element("Secret", "Edit", 1, "Password", "hunter2"); password.IsPassword = true;
        return new UiSnapshot
        {
            Target = new TargetInfo { ProcessId = 4242, Title = "Customer Desk", ProcessName = "desk" }, FocusedSelector = "id:Save",
            ScreenshotBounds = new ElementBounds { X = 100, Y = 200, Width = 800, Height = 600 },
            Elements = [window, save, notes, disabled, hidden, list, item, byName, password]
        };
    }

    private static Task Observations()
    {
        var snapshot = AgentObservation.Sanitize(SampleSnapshot());
        JsonObject Element(JsonObject observation, string selector) => observation["elements"]!.AsArray().Select(e => e!.AsObject()).Single(e => e["selector"]!.GetValue<string>() == selector);
        var all = TestyMcpService.Observation(snapshot);
        Check(all["totalElements"]!.GetValue<int>() == 9 && all["shownElements"]!.GetValue<int>() == 8 && all["hiddenOffscreen"]!.GetValue<int>() == 1 && all["nextOffset"] is null, "Offscreen controls are left out by default and counted.");
        var save = Element(all, "id:Save");
        Check(save["name"]!.GetValue<string>() == "Save" && !save.ContainsKey("value") && !save.ContainsKey("enabled") && !save.ContainsKey("offscreen") && !save.ContainsKey("automationId"), "Usual values are left out: " + save.ToJsonString());
        Check(save["bounds"]!.AsArray().Select(n => n!.GetValue<int>()).SequenceEqual(new[] { 110, 220, 80, 30 }), "bounds are [x, y, width, height] integers in screen pixels.");
        Check(save["center"]!.AsArray().Select(n => n!.GetValue<int>()).SequenceEqual(new[] { 50, 35 }), "center is relative to the top-left of the app screenshot: " + save["center"]!.ToJsonString());
        Check(Element(all, "id:Disabled")["enabled"]!.GetValue<bool>() == false, "A disabled control says so.");
        Check(Element(all, "name:Total")["automationId"]!.GetValue<string>() == "TotalLabel", "automationId is given when the selector is not id: that id.");
        var notes = Element(all, "id:Notes");
        Check(notes["value"]!.GetValue<string>().Length == 401 && notes["valueShortened"]!.GetValue<bool>() && notes["valueLength"]!.GetValue<int>() == 9000, "A cut value is flagged with its full length.");
        var list = Element(all, "id:Orders");
        Check(list["childCoverage"]!.GetValue<string>() == "realizedOnly" && list["capabilities"]!.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(new[] { "ItemContainer", "Scroll" }), "Capabilities and incomplete child coverage are reported.");
        Check(!Element(all, "id:Order1").ContainsKey("properties"), "Properties are left out unless details are asked for.");
        var secret = Element(all, "id:Secret");
        Check(secret["value"]!.GetValue<string>() == "[REDACTED]" && secret["password"]!.GetValue<bool>(), "A password value is redacted before it is observed.");
        Check(!all.ToJsonString().Contains("hunter2", StringComparison.Ordinal), "The password must not appear anywhere in the observation.");
        var withOffscreen = TestyMcpService.Observation(snapshot, includeOffscreen: true);
        Check(withOffscreen["shownElements"]!.GetValue<int>() == 9 && Element(withOffscreen, "id:Hidden")["offscreen"]!.GetValue<bool>() && !Element(withOffscreen, "id:Hidden").ContainsKey("center"), "includeOffscreen lists offscreen controls, without a click point.");
        var firstPage = TestyMcpService.Observation(snapshot, maxElements: 3);
        Check(firstPage["shownElements"]!.GetValue<int>() == 3 && firstPage["nextOffset"]!.GetValue<int>() == 3 && firstPage["truncatedByLimit"]!.GetValue<bool>(), "A limited page names the next offset.");
        var lastPage = TestyMcpService.Observation(snapshot, maxElements: 100, offset: 3);
        Check(lastPage["shownElements"]!.GetValue<int>() == 5 && lastPage["nextOffset"] is null && lastPage["elements"]![0]!["selector"]!.GetValue<string>() == "id:Disabled", "offset continues where the page ended: " + lastPage["elements"]![0]!["selector"]);
        var within = TestyMcpService.Observation(snapshot, within: "id:Orders");
        Check(within["elements"]!.AsArray().Select(e => e!["selector"]!.GetValue<string>()).SequenceEqual(new[] { "id:Orders", "id:Order1", "name:Total" }), "within returns the control and everything inside it.");
        var single = TestyMcpService.Observation(snapshot, selector: "id:Notes", details: true);
        Check(single["shownElements"]!.GetValue<int>() == 1 && single["elements"]![0]!["value"]!.GetValue<string>().Length == 8193 && single["elements"]![0]!["valueLength"]!.GetValue<int>() == 9000, "selector reads one control with its value up to 8192 characters.");
        var details = TestyMcpService.Observation(snapshot, selector: "id:Order1", details: true)["elements"]![0]!.AsObject();
        Check(details["properties"]!["uia.isSelected"]!.GetValue<bool>() && details["unavailableProperties"]!.AsArray().Single()!.GetValue<string>() == "uia.value", "details lists known properties and names the unavailable ones.");
        var filtered = TestyMcpService.Observation(snapshot, filter: "order");
        Check(filtered["matchedElements"]!.GetValue<int>() == 2, "filter matches names, selectors and automation ids without regard to case.");
        try { TestyMcpService.Observation(snapshot, within: "id:Nowhere"); throw new InvalidOperationException("A scope that matches nothing must be refused."); }
        catch (McpToolException ex) { Check(ex.Message.Contains("matches 0 controls", StringComparison.Ordinal), ex.Message); }
        var compact = TestyMcpService.Observation(snapshot, includeOffscreen: true)["elements"]!.ToJsonString().Length / 9;
        Check(compact < 1200, $"An element view should stay small; the sample averages {compact} characters with a 400-character value among them.");
        return Task.CompletedTask;
    }

    private static async Task Resources()
    {
        await using var harness = new Harness("resources");
        await harness.InitializeAsync();
        var id = (await harness.Client.CallToolAsync("create_test", new { name = "Resource test", intent = "Listed as a resource.", steps = CustomerSteps("Ada") })).RequireOk("create_test").Structured.GetProperty("testId").GetString();
        var resources = Result(await harness.Client.RequestAsync("resources/list")).GetProperty("resources").EnumerateArray().ToList();
        var uris = resources.Select(r => r.GetProperty("uri").GetString()).ToList();
        Check(uris.Contains("testy://workspace") && uris.Contains("testy://docs/mcp") && uris.Contains("testy://tests/" + id), "resources/list must include the workspace, the guide and saved tests: " + string.Join(", ", uris));
        Check(resources.All(r => r.TryGetProperty("name", out _) && r.TryGetProperty("mimeType", out _)), "Resources need name and mimeType.");
        var entry = resources.Single(r => r.GetProperty("uri").GetString() == "testy://tests/" + id);
        Check(entry.GetProperty("name").GetString() == "test-" + id && entry.GetProperty("title").GetString() == "Resource test" && entry.GetProperty("description").GetString()!.Contains("3 steps", StringComparison.Ordinal),
            "A test entry has a stable name, the test's name as its title and a description.");
        Check(resources.Select(r => r.GetProperty("name").GetString()).Distinct().Count() == resources.Count, "Resource names must be unique.");
        var templates = Result(await harness.Client.RequestAsync("resources/templates/list")).GetProperty("resourceTemplates").EnumerateArray().Select(t => t.GetProperty("uriTemplate").GetString()).ToList();
        Check(templates.SequenceEqual(new[] { "testy://tests/{id}", "testy://runs/{id}", "testy://runs/{id}/report" }), "Test, run and standalone report templates must be listed.");
        var workspace = Result(await harness.Client.RequestAsync("resources/read", new { uri = "testy://workspace" })).GetProperty("contents")[0];
        Check(workspace.GetProperty("mimeType").GetString() == "application/json" && JsonDocument.Parse(workspace.GetProperty("text").GetString()!).RootElement.GetProperty("counts").GetProperty("tests").GetInt32() == 1, "The workspace resource must be JSON with counts.");
        var docs = Result(await harness.Client.RequestAsync("resources/read", new { uri = "testy://docs/mcp" })).GetProperty("contents")[0];
        var guide = docs.GetProperty("text").GetString()!;
        Check(docs.GetProperty("mimeType").GetString() == "text/markdown" && guide.Contains("# Testy MCP server", StringComparison.Ordinal) && guide.Contains("query:", StringComparison.Ordinal), "The guide must be Markdown covering selectors.");
        Check(guide.Contains("{\"action\":\"typeText\",\"selector\":\"id:CustomerName\",\"value\":\"Ada\"}", StringComparison.Ordinal) && guide.Contains(McpResources.ExampleQuery, StringComparison.Ordinal), "The guide must show a complete step and a complete query selector.");
        Check(guide.Contains("On or Off", StringComparison.Ordinal) && guide.Contains("center", StringComparison.Ordinal) && guide.Contains("EvidenceUnavailable", StringComparison.Ordinal) && guide.Contains("data about the app", StringComparison.Ordinal),
            "The guide must state what assertText compares, the coordinate contract, what a truncated tree means and that app text is data.");
        Check(!guide.Contains("observe_application", StringComparison.Ordinal) && !guide.Contains("perform_ui_action", StringComparison.Ordinal), "The guide names only tools of this server.");
        var test = JsonDocument.Parse(Result(await harness.Client.RequestAsync("resources/read", new { uri = "testy://tests/" + id })).GetProperty("contents")[0].GetProperty("text").GetString()!).RootElement;
        var viaTool = (await harness.Client.CallToolAsync("get_test", new { testId = id })).RequireOk("get_test").Structured;
        Check(JsonSerializer.Serialize(test) == JsonSerializer.Serialize(viaTool), "The test resource is what get_test returns.");
        // A stored run carries the whole control tree of every step; the resource must not.
        WriteRun(harness.Workspace, "resource-run", id!, RunStatus.Passed, snapshotElements: 400);
        var stored = new FileInfo(Path.Combine(harness.Workspace, "runs", "resource-run.json")).Length;
        var runText = Result(await harness.Client.RequestAsync("resources/read", new { uri = "testy://runs/resource-run" })).GetProperty("contents")[0].GetProperty("text").GetString()!;
        var run = JsonDocument.Parse(runText).RootElement;
        Check(!runText.Contains("\"snapshot\"", StringComparison.Ordinal) && runText.Length < 8000 && stored > 100_000, $"The run resource must leave out per-step control trees: {runText.Length} characters for a {stored}-byte run file.");
        var detailedRun = JsonNode.Parse((await harness.Client.CallToolAsync("get_run", new { runId = "resource-run" })).RequireOk("get_run").Structured.GetRawText())!;
        Check(detailedRun["steps"]![0]!["snapshot"]!["elements"]!.AsArray().Count == 400, "get_run must expose full observation evidence for Cortex's viewer.");
        foreach (var step in detailedRun["steps"]!.AsArray().OfType<JsonObject>()) { step.Remove("snapshot"); step.Remove("screenshotEvidence"); }
        Check(JsonNode.DeepEquals(JsonNode.Parse(run.GetRawText()), detailedRun), "The compact resource must retain the same verdict and step results as get_run.");
        Check(ErrorCode(await harness.Client.RequestAsync("resources/read", new { uri = "testy://runs/missing" })) == JsonRpc.ResourceNotFound, "Unknown runs must be -32002 for handshake revisions.");
        Check(ErrorCode(await harness.Client.RequestAsync("resources/read", new { uri = "file:///C:/Windows/win.ini" })) == JsonRpc.ResourceNotFound, "Foreign URIs must not be served.");
        var listed = (await harness.Client.CallToolAsync("list_tests")).RequireOk("list_tests").Structured.GetProperty("tests")[0].GetProperty("lastRun");
        Check(listed.GetProperty("runId").GetString() == "resource-run" && listed.GetProperty("status").GetString() == "passed", "list_tests reports the test's latest run.");
    }

    /// <summary>Writes a run file as Testy's runner stores it, with two steps; optionally with large per-step snapshots or failure diagnostics.</summary>
    private static RunResult WriteRun(string workspace, string runId, string testId, RunStatus status, int snapshotElements = 0, bool withDiagnostics = false, string? screenshot = null, string? artifactDirectory = null, DateTimeOffset? startedAt = null)
    {
        UiSnapshot? Snapshot() => snapshotElements == 0 ? null : new UiSnapshot
        {
            Elements = Enumerable.Range(0, snapshotElements).Select(i => new UiElementInfo { AutomationId = "Control" + i, Selector = "id:Control" + i, ControlType = "Button", Name = "Control number " + i, IsEnabled = true, Depth = 1 }).ToList()
        };
        var started = startedAt ?? DateTimeOffset.UtcNow.AddSeconds(-5);
        var run = new RunResult
        {
            Id = runId, TestId = testId, TestName = "Stored run", Status = status, StartedAt = started, FinishedAt = started.AddSeconds(2), Summary = "2 of 2 steps " + status,
            Target = new TargetInfo { ProcessId = 4242, Title = "Customer Desk", ProcessName = "desk" }, ArtifactDirectory = artifactDirectory ?? Path.Combine(workspace, "artifacts", runId),
            Steps =
            [
                new StepResult { Index = 0, Step = new TestStep { Title = "Reset", Action = StepAction.Click, Selector = "id:ResetButton" }, Status = RunStatus.Passed, Message = "Clicked.", Snapshot = Snapshot(), ScreenshotPath = screenshot ?? "" },
                new StepResult { Index = 1, Step = new TestStep { Title = "Verify", Action = StepAction.AssertText, Selector = "id:StatusMessage", Value = "Ready" }, Status = status, Message = "Compared.", Snapshot = Snapshot() }
            ]
        };
        if (withDiagnostics)
        {
            var diagnostic = FailureDiagnostics.Create(FailureCategory.AssertionMismatch, "Expected 'Ready', observed 'Busy'.", run.Steps[1].Step, "Ready", "Busy", "case-sensitive exact text");
            diagnostic.StepIndex = 1;
            run.Steps[1].FailureDiagnostics.Add(diagnostic);
            run.FailureDiagnostics.Add(diagnostic);
        }
        new WorkspaceStore(workspace).SaveRun(run);
        return run;
    }

    private static async Task Prompts()
    {
        await using var harness = new Harness("prompts");
        await harness.InitializeAsync();
        var prompts = Result(await harness.Client.RequestAsync("prompts/list")).GetProperty("prompts").EnumerateArray().ToList();
        Check(prompts.Count == 1 && prompts[0].GetProperty("name").GetString() == "write_test", "Exactly one prompt, write_test, is offered.");
        var arguments = prompts[0].GetProperty("arguments").EnumerateArray().Select(a => (a.GetProperty("name").GetString()!, a.GetProperty("required").GetBoolean())).ToList();
        Check(arguments.SequenceEqual(new[] { ("app", true), ("goal", true) }), "write_test takes required app and goal arguments.");
        var prompt = Result(await harness.Client.RequestAsync("prompts/get", new { name = "write_test", arguments = new { app = "C:\\Apps\\Desk.exe", goal = "adding a customer shows a confirmation" } }));
        var message = prompt.GetProperty("messages")[0];
        var text = message.GetProperty("content").GetProperty("text").GetString()!;
        Check(message.GetProperty("role").GetString() == "user" && text.Contains("C:\\Apps\\Desk.exe", StringComparison.Ordinal) && text.Contains("adding a customer shows a confirmation", StringComparison.Ordinal) && text.Contains("run_test", StringComparison.Ordinal)
            && text.Contains("cancel_run", StringComparison.Ordinal) && text.Contains("testId", StringComparison.Ordinal), "The prompt must embed the arguments and the workflow.");
        Check(ErrorCode(await harness.Client.RequestAsync("prompts/get", new { name = "write_test", arguments = new { app = "x" } })) == JsonRpc.InvalidParams, "A missing required argument must be -32602.");
        Check(ErrorCode(await harness.Client.RequestAsync("prompts/get", new { name = "write_test", arguments = "text" })) == JsonRpc.InvalidParams, "Non-object arguments must be -32602.");
    }
}
