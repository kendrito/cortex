using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Testy.Cli.Mcp;
using Testy.Core;

namespace Testy.Cli;

/// <summary>
/// Drives the real `Testy.Cli mcp` child process as a generic MCP client over both transports (stdio and Streamable HTTP), against a
/// fresh workspace and the owned Customer Desk fixture: lifecycle, tools, evidence, output schemas, progress, background runs, cancellation,
/// HTTP security rules, process lifetime and clean shutdown.
/// </summary>
internal static class McpVerifier
{
    internal static readonly string[] ExpectedTools =
    [
        "get_workspace_info", "list_tests", "get_test", "create_test", "update_test", "delete_test", "validate_test", "list_apps", "launch_app", "close_app", "activate_app",
        "inspect_app", "screenshot_app", "perform_step", "run_test", "cancel_run", "list_runs", "get_run", "get_run_report", "get_run_screenshot", "find_app", .. CortexMcpTools.Define(null).Select(tool => tool.Name)
    ];
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static async Task<VerificationReport> VerifyAsync(string? lab, string artifacts, CancellationToken ct)
    {
        artifacts = Path.GetFullPath(artifacts);
        Directory.CreateDirectory(artifacts);
        if (lab is not null)
        {
            lab = Path.GetFullPath(lab);
            if (!File.Exists(lab) || !Path.GetFileName(lab).Equals("Testy.TestLab.exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("verify-mcp launches only the included Testy.TestLab.exe.");
        }
        var report = new VerificationReport { Executable = typeof(McpVerifier).Assembly.Location };
        var reportPath = Path.Combine(artifacts, "mcp-verification.json");
        void Save() => WorkerCommand.DurableWrite(reportPath, report);
        var runRoot = Path.Combine(artifacts, "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(runRoot);
        await using var stdio = new StdioScenario(runRoot, lab);
        await using var http = new HttpScenario(runRoot, lab);
        await using var lifetime = new LifetimeScenario(runRoot, lab);
        await using var names = new AppNameScenario(runRoot);
        var checks = stdio.Checks().Concat(http.Checks()).Concat(lifetime.Checks()).Concat(names.Checks()).ToList();
        report.PlannedChecks = checks.Count;
        Save();
        try
        {
            foreach (var (name, driver, run) in checks)
            {
                ct.ThrowIfCancellationRequested();
                var check = new VerificationCheck { Name = name, Driver = driver, Expected = RunStatus.Passed };
                var watch = Stopwatch.StartNew();
                try { check.Message = await run(ct); check.Passed = true; check.Actual = RunStatus.Passed; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { report.Cancelled = true; throw; }
                catch (Exception ex) { check.Message = ex.GetType().Name + ": " + ex.Message; check.Actual = RunStatus.Failed; }
                check.Message += $" [{watch.ElapsedMilliseconds} ms]";
                report.Checks.Add(check);
                Save();
            }
            report.CompletedAllScenarios = report.Checks.Count == report.PlannedChecks;
        }
        finally { report.Finish(ct); Save(); }
        return report;
    }

    // ═══════════════ Shared helpers ═══════════════
    internal static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static JsonElement Result(JsonElement response, string what)
    {
        if (response.TryGetProperty("error", out var error)) throw new InvalidOperationException($"{what}: JSON-RPC error {error.GetProperty("code").GetInt32()}: {error.GetProperty("message").GetString()}");
        return response.GetProperty("result");
    }
    private static Process StartServer(string workspace, string stderrPath, out Task<List<string>> stderr, params string[] extra)
    {
        var start = WorkerCommand.CreateSelfStartInfo();
        start.RedirectStandardInput = true;
        start.Environment.Remove(McpOptions.TokenVariable);
        foreach (var argument in new[] { "mcp", "--workspace", workspace }.Concat(extra)) start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("The MCP server process did not start.");
        stderr = DrainAsync(process.StandardError, stderrPath);
        return process;
    }
    private static async Task<List<string>> DrainAsync(StreamReader reader, string path)
    {
        var lines = new List<string>();
        await using var file = new StreamWriter(path, false, new UTF8Encoding(false));
        while (await reader.ReadLineAsync() is { } line) { lines.Add(line); await file.WriteLineAsync(line); }
        return lines;
    }
    private static async Task<int> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        if (!await WorkerCommand.WaitForExitAsync(process, timeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"The MCP server did not exit within {timeout.TotalSeconds:F0} s after its stdin closed.");
        }
        return process.ExitCode;
    }
    private static bool Alive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
    }
    private static void Kill(int pid)
    {
        if (pid == 0) return;
        try { using var app = Process.GetProcessById(pid); if (!app.HasExited) app.Kill(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    private static async Task RequireGoneAsync(int pid, string what, int timeoutMs = 8000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (Alive(pid))
        {
            if (DateTimeOffset.UtcNow > deadline) throw new InvalidOperationException($"{what} (pid {pid}) is still running.");
            await Task.Delay(100);
        }
    }
    private static async Task UntilAsync(Func<Task<bool>> condition, string failure, int timeoutMs, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        while (!await condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs) throw new TimeoutException(failure);
            await Task.Delay(150, ct);
        }
    }
    private static void RequireNoJsonRpc(IEnumerable<string> stderr)
    {
        var leaked = stderr.FirstOrDefault(l => l.TrimStart().StartsWith('{') && l.Contains("\"jsonrpc\"", StringComparison.Ordinal));
        Require(leaked is null, "stderr contained a JSON-RPC message: " + leaked);
    }
    internal static (int Width, int Height, int Colors) ValidatePng(byte[] bytes, string what, int minimumWidth = 200)
    {
        Require(bytes.Length > 8 && bytes.Take(8).SequenceEqual(PngSignature), what + " is not a PNG.");
        using var stream = new MemoryStream(bytes);
        var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        Require(frame.PixelWidth >= minimumWidth && frame.PixelHeight >= minimumWidth * 3 / 5, $"{what} is too small ({frame.PixelWidth}x{frame.PixelHeight}).");
        var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        var colors = new HashSet<int>();
        var step = Math.Max(1, bitmap.PixelWidth / 180);
        for (var y = 0; y < bitmap.PixelHeight; y += step)
            for (var x = 0; x < bitmap.PixelWidth; x += step)
            { var i = y * stride + x * 4; colors.Add(pixels[i] | pixels[i + 1] << 8 | pixels[i + 2] << 16); }
        Require(colors.Count >= 8, $"{what} is uniform ({colors.Count} colours); the app was not rendered.");
        return (frame.PixelWidth, frame.PixelHeight, colors.Count);
    }
    private static object CustomerTestSteps(string name, string email) => new object[]
    {
        new { title = "Reset the form", action = "click", selector = "id:ResetButton" },
        new { title = "Enter the customer name", action = "typeText", selector = "id:CustomerName", value = name },
        new { title = "Enter the email", action = "typeText", selector = "id:CustomerEmail", value = email },
        new { title = "Add the customer", action = "click", selector = "id:AddCustomer" },
        new { title = "Verify the confirmation", action = "assertText", selector = "id:StatusMessage", value = "Customer added: " + name }
    };
    private static object CancellationTestSteps() => new object[]
    {
        new { action = "click", selector = "id:ResetButton" },
        new { title = "Wait long enough to be cancelled", action = "wait", value = "15000", timeoutMs = 20000 },
        new { title = "Must not execute", action = "typeText", selector = "id:CustomerName", value = "MUST NOT EXECUTE" }
    };
    private static string ResolveSampleApp(string? lab, JsonElement workspaceInfo)
    {
        var reported = workspaceInfo.TryGetProperty("sampleApp", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        Require(reported is not null && File.Exists(reported), "get_workspace_info did not name an existing sample app (Testy.TestLab.exe).");
        return lab ?? reported!;
    }
    private static string? Text(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    /// <summary>Every structured result is held to the output schema its tool declares, top-level properties included.</summary>
    private static McpToolCall Conforming(Dictionary<string, JsonElement> schemas, string tool, McpToolCall call)
    {
        Require(schemas.TryGetValue(tool, out var schema), $"tools/list has no output schema for {tool}.");
        var violation = McpSchemaValidator.FirstViolation(schema, call.Structured, tool, declaredTopLevelOnly: true);
        Require(violation is null, $"{tool} returned structuredContent that does not satisfy its outputSchema: {violation}");
        return call;
    }
    private static Dictionary<string, JsonElement> Schemas(JsonElement toolsList) =>
        toolsList.GetProperty("tools").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("outputSchema").Clone(), StringComparer.Ordinal);
    /// <summary>A cancelled run is found through list_runs and read through get_run: status cancelled, the step after the wait skipped.</summary>
    private static string RequireCancelledRun(JsonElement runs, JsonElement run)
    {
        Require(run.GetProperty("status").GetString() == "cancelled" && !run.GetProperty("running").GetBoolean(), "The run status is not cancelled: " + run.GetProperty("summary").GetString());
        var steps = run.GetProperty("steps").EnumerateArray().ToList();
        Require(steps.Count == 3 && steps[2].GetProperty("status").GetString() == "skipped", "The step after the cancelled wait must be skipped.");
        Require(runs.GetProperty("runs").EnumerateArray().Any(r => r.GetProperty("runId").GetString() == run.GetProperty("runId").GetString() && r.GetProperty("status").GetString() == "cancelled"), "list_runs does not show the cancelled run.");
        return run.GetProperty("runId").GetString()!;
    }
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);

    // ═══════════════ stdio ═══════════════
    private sealed class StdioScenario(string runRoot, string? lab) : IAsyncDisposable
    {
        private const string Driver = "stdio";
        private readonly string workspace = Path.Combine(runRoot, "workspace-stdio");
        private Process? server;
        private McpLineClient? client;
        private Task<List<string>>? stderr;
        private Dictionary<string, JsonElement> schemas = [];
        private string sampleApp = "", testId = "", runId = "", waitTestId = "";
        private int appPid;
        private McpLineClient Client => client ?? throw new InvalidOperationException("The stdio server is not running.");
        private async Task<McpToolCall> CallAsync(string tool, object? arguments = null, string? progressToken = null, int timeoutSeconds = 60) =>
            Conforming(schemas, tool, (await Client.CallToolAsync(tool, arguments, progressToken, TimeSpan.FromSeconds(timeoutSeconds))).RequireOk(tool));

        public IEnumerable<(string Name, string Driver, Func<CancellationToken, Task<string>> Run)> Checks()
        {
            yield return ("initialize negotiates 2025-06-18, describes the server and answers ping", Driver, InitializeAsync);
            yield return ("tools/list exposes every Testy tool (native, find_app and Cortex) with object schemas, annotations and the step action enum", Driver, ToolsListAsync);
            yield return ("get_workspace_info names the fresh workspace, the sample app and the launch policy", Driver, WorkspaceInfoAsync);
            yield return ("launch_app starts the sample Customer Desk and refuses a console program, Windows' launchers and programs, and arguments that name a shell", Driver, LaunchAsync);
            yield return ("find_app resolves \"Customer Desk\" to the running sample app and inspect_app takes the name", Driver, FindRunningAsync);
            yield return ("inspect_app returns the CustomerDeskWindow tree, its controls and a real screenshot", Driver, InspectAsync);
            yield return ("inspect_app scopes, filters and pages the control list and reads one control in full", Driver, InspectScopeAsync);
            yield return ("screenshot_app returns a valid PNG with bounds, and a scale when it is made smaller", Driver, ScreenshotAsync);
            yield return ("a minimized app is reported as such and activate_app restores it", Driver, ActivateAsync);
            yield return ("perform_step clicks id:ResetButton", Driver, PerformClickAsync);
            yield return ("perform_step types into id:CustomerName and reports what changed", Driver, PerformTypeAsync);
            yield return ("create_test saves the five-step customer test where Studio reads it", Driver, CreateAsync);
            yield return ("run_test replays the test with progress notifications and every step passes", Driver, RunAsync);
            yield return ("get_run, the run resource and list_tests report the passed run", Driver, GetRunAsync);
            yield return ("get_run_screenshot returns the step 5 PNG and explains a step number beyond the run", Driver, RunScreenshotAsync);
            yield return ("list_runs includes the run", Driver, ListRunsAsync);
            yield return ("update_test renames the test, replaces its steps and keeps the step ids", Driver, UpdateAsync);
            yield return ("run_test with wait false is polled, keeps other input out and is stopped by cancel_run", Driver, BackgroundRunAsync);
            yield return ("an unknown tool is a -32602 protocol error and a failing tool call sets isError", Driver, ErrorsAsync);
            yield return ("notifications/cancelled stops a running test without a response and the app stays usable", Driver, CancelAsync);
            yield return ("run_test with the test's stored app connects to the running instance; with exe it launches the app for the run, reports progress on one scale and closes it", Driver, RunWithExeAsync);
            yield return ("close_app closes the launched app and launch_app starts it again", Driver, CloseAndRelaunchAsync);
            yield return ("delete_test removes the saved file", Driver, DeleteAsync);
            yield return ("closing stdin exits the server with code 0 and closes the launched app", Driver, ShutdownAsync);
            yield return ("stdout carried only JSON-RPC objects and stderr carried no JSON-RPC", Driver, HygieneAsync);
        }

        private async Task<string> InitializeAsync(CancellationToken ct)
        {
            Directory.CreateDirectory(workspace);
            server = StartServer(workspace, Path.Combine(runRoot, "stdio-stderr.txt"), out stderr);
            client = new McpLineClient(server.StandardOutput.BaseStream, server.StandardInput.BaseStream);
            var response = await client.RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "testy-verify-mcp", ["version"] = McpServer.Version }
            }, TimeSpan.FromSeconds(45));
            var result = Result(response, "initialize");
            Require(result.GetProperty("protocolVersion").GetString() == "2025-06-18", "The requested protocol version was not echoed.");
            Require(result.GetProperty("serverInfo").GetProperty("name").GetString() == "testy", "serverInfo.name is not testy.");
            Require(result.GetProperty("capabilities").TryGetProperty("tools", out _) && result.GetProperty("capabilities").TryGetProperty("resources", out _) && result.GetProperty("capabilities").TryGetProperty("prompts", out _), "Capabilities are incomplete.");
            Require(result.GetProperty("instructions").GetString()!.Length > 100, "instructions are missing.");
            await client.NotifyAsync("notifications/initialized");
            var ping = Result(await client.RequestAsync("ping"), "ping");
            Require(ping.ValueKind == JsonValueKind.Object, "ping must return an object.");
            return $"protocol 2025-06-18, server testy {result.GetProperty("serverInfo").GetProperty("version").GetString()}, pid {server.Id}";
        }

        private async Task<string> ToolsListAsync(CancellationToken ct)
        {
            var listed = Result(await Client.RequestAsync("tools/list"), "tools/list");
            schemas = Schemas(listed);
            var tools = listed.GetProperty("tools").EnumerateArray().ToList();
            var names = tools.Select(t => t.GetProperty("name").GetString()!).ToList();
            Require(names.Count == names.Distinct(StringComparer.Ordinal).Count(), "Tool names are not unique.");
            Require(names.SequenceEqual(ExpectedTools), "Tool names differ from the expected list: " + string.Join(", ", names));
            foreach (var tool in tools)
            {
                var name = tool.GetProperty("name").GetString();
                Require(tool.GetProperty("description").GetString()!.Length > 20, $"{name} lacks a description.");
                var schema = tool.GetProperty("inputSchema");
                Require(schema.GetProperty("type").GetString() == "object", $"{name} inputSchema.type is not object.");
                var properties = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                if (schema.TryGetProperty("required", out var required)) Require(required.EnumerateArray().All(r => properties.Contains(r.GetString()!)), $"{name} requires a property it does not declare.");
                var annotations = tool.GetProperty("annotations");
                Require(annotations.TryGetProperty("readOnlyHint", out _) && annotations.TryGetProperty("destructiveHint", out _) && annotations.TryGetProperty("idempotentHint", out _) && annotations.TryGetProperty("openWorldHint", out _), $"{name} lacks annotations.");
            }
            var launch = tools.Single(t => t.GetProperty("name").GetString() == "launch_app").GetProperty("annotations");
            Require(launch.GetProperty("destructiveHint").GetBoolean() && launch.GetProperty("openWorldHint").GetBoolean() && !launch.GetProperty("readOnlyHint").GetBoolean(), "launch_app is not annotated as having side effects.");
            // Every tool that can start a program (launch true included) is not read-only, so a client that approves read-only tools unasked still asks.
            foreach (var starting in tools.Where(t => t.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("launch", out _) || t.GetProperty("name").GetString() is "launch_app" or "run_test"))
                Require(!starting.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean() && starting.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean(), $"{starting.GetProperty("name").GetString()} can start a program and must not be annotated read-only.");
            var create = tools.Single(t => t.GetProperty("name").GetString() == "create_test");
            var actions = create.GetProperty("inputSchema").GetProperty("properties").GetProperty("steps").GetProperty("items").GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray();
            Require(actions.SequenceEqual(Enum.GetNames<StepAction>().Select(JsonNamingPolicy.CamelCase.ConvertName)), "The step action enum differs from StepAction.");
            return $"{tools.Count} tools; {actions.Length} step actions";
        }

        private async Task<string> WorkspaceInfoAsync(CancellationToken ct)
        {
            var info = (await CallAsync("get_workspace_info")).Structured;
            Require(string.Equals(Path.GetFullPath(info.GetProperty("workspace").GetString()!).TrimEnd('\\'), Path.GetFullPath(workspace).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase), "The workspace path differs from --workspace.");
            Require(info.GetProperty("counts").GetProperty("tests").GetInt32() == 0, "A fresh workspace should have no tests.");
            Require(info.GetProperty("actions").GetArrayLength() == Enum.GetNames<StepAction>().Length, "The action catalog does not cover every StepAction.");
            Require(info.GetProperty("launchPolicy").GetProperty("refusedPrograms").GetArrayLength() > 0 && info.GetProperty("launchedApps").GetArrayLength() == 0, "The launch policy or the launched apps are not reported.");
            sampleApp = ResolveSampleApp(lab, info);
            return "workspace " + workspace + "; sample app " + sampleApp;
        }

        private async Task<string> LaunchAsync(CancellationToken ct)
        {
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var console = Path.Combine(system, "where.exe");
            var refused = await Client.CallToolAsync("launch_app", new { exe = console });
            Require(refused.Error is null && refused.IsError && refused.Text.Contains("console program", StringComparison.Ordinal), "A console program must be refused: " + refused.Text);
            // Windows' program launchers, the other programs of Windows itself, and arguments that name a shell: refused, nothing started.
            var explorers = Process.GetProcessesByName("explorer").Length;
            var labs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(sampleApp)).Length;
            foreach (var (exe, args, rule) in new (string Exe, string[] Args, string Rule)[]
            {
                (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), [], "system launcher"), (Path.Combine(system, "pcalua.exe"), [], "system launcher"),
                (Path.Combine(system, "notepad.exe"), [], "Windows itself"), (sampleApp, ["--then", Path.Combine(system, "cmd.exe")], "args names cmd")
            })
            {
                var launcher = await Client.CallToolAsync("launch_app", new { exe, args, waitForWindowSeconds = 5 });
                Require(launcher.Error is null && launcher.IsError && launcher.Text.Contains(rule, StringComparison.Ordinal), $"launch_app {Path.GetFileName(exe)} [{string.Join(" ", args)}] must be refused ({rule}): " + launcher.Text);
            }
            Require(Process.GetProcessesByName("explorer").Length <= explorers && Process.GetProcessesByName(Path.GetFileNameWithoutExtension(sampleApp)).Length == labs, "A refused launch must start nothing.");
            var launched = (await CallAsync("launch_app", new { exe = sampleApp, waitForWindowSeconds = 30 })).Structured;
            appPid = launched.GetProperty("pid").GetInt32();
            var title = launched.GetProperty("title").GetString() ?? "";
            Require(title.Contains("Customer Desk", StringComparison.OrdinalIgnoreCase), "Unexpected window title: " + title);
            Require(Alive(appPid), "The launched app exited.");
            var apps = (await CallAsync("list_apps")).Structured.GetProperty("apps").EnumerateArray().ToList();
            var listed = apps.FirstOrDefault(a => a.GetProperty("pid").GetInt32() == appPid);
            Require(listed.ValueKind == JsonValueKind.Object && listed.GetProperty("launchedByThisServer").GetBoolean() && !listed.GetProperty("minimized").GetBoolean(), "list_apps does not list the launched app as launched by this server.");
            var info = (await CallAsync("get_workspace_info")).Structured.GetProperty("launchedApps");
            Require(info.GetArrayLength() == 1 && info[0].GetProperty("pid").GetInt32() == appPid && !info[0].GetProperty("keepOpen").GetBoolean(), "get_workspace_info does not list the launched app.");
            return $"pid {appPid}: {title}; where.exe refused as a console program; explorer and pcalua as launchers, notepad as part of Windows, and cmd named in args, all without starting anything";
        }

        private async Task<string> FindRunningAsync(CancellationToken ct)
        {
            var found = (await CallAsync("find_app", new { query = "Customer Desk" }, timeoutSeconds: 90)).Structured;
            var best = found.GetProperty("best");
            Require(found.GetProperty("status").GetString() == "unique" && best.ValueKind == JsonValueKind.Object && best.GetProperty("kind").GetString() == "running" && best.GetProperty("pid").GetInt32() == appPid
                && best.GetProperty("sources").EnumerateArray().Any(s => s.GetString() == "sample") && string.Equals(best.GetProperty("exePath").GetString(), sampleApp, StringComparison.OrdinalIgnoreCase),
                "find_app must resolve Customer Desk to the running sample app: " + found.GetRawText());
            var inspected = (await CallAsync("inspect_app", new { app = "Customer Desk", includeScreenshot = false, selector = "id:StatusMessage" })).Structured;
            var resolved = inspected.GetProperty("resolvedApp");
            Require(inspected.GetProperty("pid").GetInt32() == appPid && resolved.GetProperty("pid").GetInt32() == appPid && !resolved.GetProperty("launched").GetBoolean() && resolved.GetProperty("kind").GetString() == "running",
                "inspect_app with app must use the running instance: " + resolved.GetRawText());
            return $"best: pid {appPid}, {best.GetProperty("reason").GetString()}; inspect_app by name read {inspected.GetProperty("elements").GetArrayLength()} control(s)";
        }

        private async Task<string> InspectAsync(CancellationToken ct)
        {
            var call = await CallAsync("inspect_app", new { pid = appPid });
            var structured = call.Structured;
            var elements = structured.GetProperty("elements").EnumerateArray().ToList();
            var root = elements.FirstOrDefault(e => e.GetProperty("selector").GetString() == "id:CustomerDeskWindow");
            Require(root.ValueKind == JsonValueKind.Object && root.GetProperty("controlType").GetString() == "Window" && root.GetProperty("depth").GetInt32() == 0, "id:CustomerDeskWindow is not the depth-0 Window root.");
            foreach (var id in new[] { "id:ResetButton", "id:CustomerName", "id:CustomerEmail", "id:AddCustomer", "id:StatusMessage" })
                Require(elements.Any(e => e.GetProperty("selector").GetString() == id), $"{id} is missing from the tree.");
            Require(!structured.GetProperty("treeTruncated").GetBoolean(), "The tree was truncated.");
            var add = elements.Single(e => e.GetProperty("selector").GetString() == "id:AddCustomer");
            Require(add.GetProperty("bounds").GetArrayLength() == 4 && add.GetProperty("center").GetArrayLength() == 2 && !add.TryGetProperty("enabled", out _) && !add.TryGetProperty("automationId", out _), "An element view carries bounds and center and leaves out usual values: " + add.GetRawText());
            Require(call.Images.Count == 1, "inspect_app returned no image content.");
            var image = ValidatePng(call.Images[0], "inspect_app screenshot");
            var text = call.Result!.Value.GetProperty("content")[0].GetProperty("text").GetString()!;
            Require(!text.Contains('\n') && !text.Contains("\\u0022", StringComparison.Ordinal), "The text content is not compact JSON with literal quotes.");
            return $"{elements.Count} controls in {text.Length} characters of text, screenshot {image.Width}x{image.Height} with {image.Colors} sampled colours";
        }

        private async Task<string> InspectScopeAsync(CancellationToken ct)
        {
            var filtered = (await CallAsync("inspect_app", new { pid = appPid, filter = "customer", includeScreenshot = false })).Structured;
            var all = (await CallAsync("inspect_app", new { pid = appPid, includeScreenshot = false, includeOffscreen = true, maxElements = 5000 })).Structured;
            Require(filtered.GetProperty("elements").GetArrayLength() > 0 && filtered.GetProperty("elements").GetArrayLength() < all.GetProperty("elements").GetArrayLength(), "filter did not narrow the list.");
            var page = (await CallAsync("inspect_app", new { pid = appPid, includeScreenshot = false, maxElements = 3 })).Structured;
            Require(page.GetProperty("elements").GetArrayLength() == 3 && page.GetProperty("nextOffset").GetInt32() == 3, "A limited page must name the next offset.");
            var next = (await CallAsync("inspect_app", new { pid = appPid, includeScreenshot = false, maxElements = 3, offset = 3 })).Structured;
            Require(next.GetProperty("elements")[0].GetProperty("selector").GetString() != page.GetProperty("elements")[0].GetProperty("selector").GetString(), "offset did not move the page.");
            var within = (await CallAsync("inspect_app", new { pid = appPid, includeScreenshot = false, within = "id:CustomerList", includeOffscreen = true })).Structured;
            Require(within.GetProperty("elements")[0].GetProperty("selector").GetString() == "id:CustomerList", "within must start at the named control.");
            var single = (await CallAsync("inspect_app", new { pid = appPid, includeScreenshot = false, selector = "id:StatusMessage" })).Structured;
            Require(single.GetProperty("elements").GetArrayLength() == 1 && single.GetProperty("elements")[0].GetProperty("selector").GetString() == "id:StatusMessage", "selector must return exactly that control.");
            var unknown = await Client.CallToolAsync("inspect_app", new { pid = appPid, includeScreenshot = false, selector = "id:NoSuchControl" });
            Require(unknown.IsError && unknown.Text.Contains("matches 0 controls", StringComparison.Ordinal), "A selector that matches nothing must be explained: " + unknown.Text);
            return $"filter: {filtered.GetProperty("elements").GetArrayLength()} of {all.GetProperty("elements").GetArrayLength()} controls; paging, within and selector verified";
        }

        private async Task<string> ScreenshotAsync(CancellationToken ct)
        {
            var call = await CallAsync("screenshot_app", new { pid = appPid });
            Require(call.Images.Count == 1, "screenshot_app returned no image.");
            var image = ValidatePng(call.Images[0], "screenshot_app image");
            var bounds = call.Structured.GetProperty("bounds");
            Require(bounds.ValueKind == JsonValueKind.Object && bounds.GetProperty("width").GetDouble() > 0, "bounds are missing.");
            Require(call.Structured.GetProperty("width").GetInt32() == image.Width, "Reported width differs from the PNG.");
            var small = await CallAsync("screenshot_app", new { pid = appPid, maxWidth = 320 });
            var reduced = ValidatePng(small.Images[0], "the reduced screenshot");
            var scale = small.Structured.GetProperty("scale").GetDouble();
            var source = small.Structured.GetProperty("sourceWidth").GetInt32();
            Require(reduced.Width == 320 && small.Structured.GetProperty("width").GetInt32() == 320 && source == (int)bounds.GetProperty("width").GetDouble() && Math.Abs(scale - 320.0 / source) < 0.0001,
                $"maxWidth 320 must return a 320 pixel image with scale 320/{source}; got {reduced.Width} pixels and scale {scale}.");
            return $"{image.Width}x{image.Height}, bounds {bounds.GetProperty("width").GetDouble()}x{bounds.GetProperty("height").GetDouble()}; reduced to {reduced.Width}x{reduced.Height} at scale {scale:0.###}";
        }

        private async Task<string> ActivateAsync(CancellationToken ct)
        {
            nint window;
            using (var process = Process.GetProcessById(appPid)) window = process.MainWindowHandle;
            Require(window != 0, "The sample app has no main window.");
            ShowWindowAsync(window, 6); // SW_MINIMIZE
            await UntilAsync(() => Task.FromResult(IsIconic(window)), "The sample app did not minimize.", 5000, ct);
            // Let Windows finish its minimize animation, as it has long finished when a person minimized a window: a window restored in the
            // middle of that animation is minimized again by Windows a few seconds later, whoever restored it.
            await Task.Delay(1000, ct);
            var listed = (await CallAsync("list_apps")).Structured.GetProperty("apps").EnumerateArray().First(a => a.GetProperty("pid").GetInt32() == appPid);
            Require(listed.GetProperty("minimized").GetBoolean(), "list_apps does not report the window as minimized.");
            var inspect = await Client.CallToolAsync("inspect_app", new { pid = appPid });
            Require(inspect.IsError && inspect.Text.Contains("minimized", StringComparison.Ordinal) && inspect.Text.Contains("activate_app", StringComparison.Ordinal), "Inspecting a minimized app must say so and name activate_app: " + inspect.Text);
            var step = await Client.CallToolAsync("perform_step", new { pid = appPid, step = new { action = "click", selector = "id:ResetButton" } });
            Require(step.IsError && step.Text.Contains("activate_app", StringComparison.Ordinal), "A step against a minimized app must name activate_app: " + step.Text);
            var activated = (await CallAsync("activate_app", new { pid = appPid })).Structured;
            Require(activated.GetProperty("restored").GetBoolean() && !activated.GetProperty("minimized").GetBoolean() && !IsIconic(window), "activate_app did not restore the window: " + activated.GetRawText());
            var again = await CallAsync("inspect_app", new { pid = appPid });
            Require(again.Structured.GetProperty("elements").GetArrayLength() > 5 && again.Images.Count == 1, "The restored app cannot be inspected.");
            return $"minimized → reported, refused with guidance, restored by activate_app (foreground {activated.GetProperty("foreground").GetBoolean()})";
        }

        private async Task<string> PerformClickAsync(CancellationToken ct)
        {
            var call = await CallAsync("perform_step", new { pid = appPid, step = new { action = "click", selector = "id:ResetButton" } });
            Require(call.Structured.GetProperty("status").GetString() == "passed" && call.Structured.GetProperty("passed").GetBoolean(), "The click did not pass: " + call.Structured.GetProperty("message").GetString());
            Require(call.Structured.GetProperty("stepStatus").GetString() == "passed", "stepStatus is not passed.");
            Require(call.Images.Count == 1, "perform_step returned no screenshot.");
            var bare = await CallAsync("perform_step", new { pid = appPid, step = new { action = "assertExists", selector = "id:AddCustomer" }, observation = "none", includeScreenshot = false });
            Require(bare.Images.Count == 0 && bare.Structured.GetProperty("observation").ValueKind == JsonValueKind.Null && bare.Structured.GetProperty("assertion").GetProperty("satisfied").GetBoolean(), "observation none with includeScreenshot false must return neither controls nor an image.");
            return call.Structured.GetProperty("stepMessage").GetString() ?? "";
        }

        private async Task<string> PerformTypeAsync(CancellationToken ct)
        {
            var call = await CallAsync("perform_step", new { pid = appPid, step = new { action = "typeText", selector = "id:CustomerName", value = "MCP Ada" } });
            Require(call.Structured.GetProperty("status").GetString() == "passed", "typeText did not pass: " + call.Structured.GetProperty("message").GetString());
            var observation = call.Structured.GetProperty("observation");
            var elements = observation.GetProperty("elements").EnumerateArray().ToList();
            var observed = elements.FirstOrDefault(e => e.GetProperty("selector").GetString() == "id:CustomerName");
            Require(observation.GetProperty("mode").GetString() == "changed" && observed.ValueKind == JsonValueKind.Object && Text(observed, "value") == "MCP Ada", "The post-step observation does not show the typed text.");
            Require(elements.Count < 10, $"The changed observation should be short; it lists {elements.Count} controls.");
            var full = await CallAsync("perform_step", new { pid = appPid, step = new { action = "assertText", selector = "id:CustomerName", value = "MCP Ada" }, observation = "full", filter = "CustomerName", includeScreenshot = false });
            Require(full.Structured.GetProperty("observation").GetProperty("mode").GetString() == "full" && full.Structured.GetProperty("observation").GetProperty("elements").EnumerateArray().Any(e => Text(e, "value") == "MCP Ada"), "observation full with a filter must list the control.");
            return $"typed 'MCP Ada'; the step changed {observation.GetProperty("changedElements").GetInt32()} control(s) and {elements.Count} were returned";
        }

        private async Task<string> CreateAsync(CancellationToken ct)
        {
            var created = (await CallAsync("create_test", new { name = "MCP verify: add a customer", intent = "Reset Customer Desk, add MCP Ada and verify the confirmation text.", category = "MCP verification", steps = CustomerTestSteps("MCP Ada", "mcp@example.test") })).Structured;
            testId = created.GetProperty("testId").GetString()!;
            var file = Path.Combine(workspace, "tests", testId + ".json");
            Require(File.Exists(file), "The test file was not written where Studio reads it: " + file);
            var saved = new WorkspaceStore(workspace).LoadTests().Single(t => t.Id == testId);
            Require(saved.Steps.Count == 5 && saved.Steps[4].Action == StepAction.AssertText, "The saved test does not have the five expected steps.");
            var resources = Result(await Client.RequestAsync("resources/list"), "resources/list").GetProperty("resources").EnumerateArray().ToList();
            var entry = resources.FirstOrDefault(r => r.GetProperty("uri").GetString() == "testy://tests/" + testId);
            Require(entry.ValueKind == JsonValueKind.Object && entry.GetProperty("title").GetString() == "MCP verify: add a customer" && entry.GetProperty("name").GetString() == "test-" + testId, "resources/list does not list the test with its name and title.");
            return $"test {testId} saved to {file}";
        }

        private async Task<string> RunAsync(CancellationToken ct)
        {
            var call = await CallAsync("run_test", new { testId, pid = appPid, mode = "replay", waitSeconds = 120 }, progressToken: "run-1", timeoutSeconds: 180);
            var run = call.Structured;
            runId = run.GetProperty("runId").GetString()!;
            Require(run.GetProperty("status").GetString() == "passed" && !run.GetProperty("running").GetBoolean(), "The run did not pass: " + run.GetProperty("summary").GetString());
            var steps = run.GetProperty("steps").EnumerateArray().ToList();
            Require(steps.Count == 5 && steps.All(s => s.GetProperty("status").GetString() == "passed"), "Not every step passed.");
            var progress = Client.Notifications.Where(n => n.GetProperty("method").GetString() == "notifications/progress" && n.GetProperty("params").GetProperty("progressToken").GetString() == "run-1").Select(n => n.GetProperty("params")).ToList();
            var values = progress.Select(p => p.GetProperty("progress").GetDouble()).ToList();
            Require(values.Count >= 3, $"Only {values.Count} progress notifications arrived.");
            Require(values.Zip(values.Skip(1)).All(pair => pair.Second > pair.First) && values[^1] == 5 && progress.All(p => p.GetProperty("total").GetDouble() == 5), "Progress must rise strictly to the total of 5: " + string.Join(", ", values));
            Require(progress.All(p => p.GetProperty("message").GetString()!.Contains(runId, StringComparison.Ordinal)), "Progress messages must name the run.");
            var file = Path.Combine(workspace, "runs", runId + ".json");
            Require(File.Exists(file), "run.json was not saved to the workspace runs folder: " + file);
            return $"run {runId} passed with {steps.Count} steps and {values.Count} progress notifications";
        }

        private async Task<string> GetRunAsync(CancellationToken ct)
        {
            var call = await CallAsync("get_run", new { runId });
            var run = call.Structured;
            Require(run.GetProperty("status").GetString() == "passed" && run.GetProperty("screenshotsAvailable").GetBoolean(), "get_run does not report a passed run with screenshots.");
            Require(run.GetProperty("steps").GetArrayLength() == 5 && run.GetProperty("steps").EnumerateArray().All(s => s.GetProperty("screenshotAvailable").GetBoolean()), "Step screenshots are not all available.");
            var resource = Result(await Client.RequestAsync("resources/read", new { uri = "testy://runs/" + runId }), "resources/read").GetProperty("contents")[0].GetProperty("text").GetString()!;
            var stored = new FileInfo(Path.Combine(workspace, "runs", runId + ".json")).Length;
            Require(!resource.Contains("\"snapshot\"", StringComparison.Ordinal) && resource.Length < stored / 4 && JsonDocument.Parse(resource).RootElement.GetProperty("runId").GetString() == runId, $"The run resource must be the trimmed view; it has {resource.Length} characters for a {stored}-byte run file.");
            var listed = (await CallAsync("list_tests")).Structured.GetProperty("tests").EnumerateArray().Single(t => t.GetProperty("testId").GetString() == testId).GetProperty("lastRun");
            Require(listed.ValueKind == JsonValueKind.Object && listed.GetProperty("runId").GetString() == runId && listed.GetProperty("status").GetString() == "passed", "list_tests does not show the run as the test's last run.");
            return $"{run.GetProperty("summary").GetString()} Resource: {resource.Length} characters (run file: {stored} bytes).";
        }

        private async Task<string> RunScreenshotAsync(CancellationToken ct)
        {
            var call = await CallAsync("get_run_screenshot", new { runId, stepNumber = 5 });
            Require(call.Images.Count == 1, "No image content.");
            var image = ValidatePng(call.Images[0], "step 5 screenshot");
            var beyond = await Client.CallToolAsync("get_run_screenshot", new { runId, stepNumber = 6 });
            Require(beyond.Error is null && beyond.IsError && beyond.Text.Contains("stepNumber must be 1–5", StringComparison.Ordinal), "A step number beyond the run must be explained: " + beyond.Text);
            return $"step 5 screenshot {image.Width}x{image.Height}; step 6 refused with the valid range";
        }

        private async Task<string> ListRunsAsync(CancellationToken ct)
        {
            var runs = (await CallAsync("list_runs", new { testId })).Structured;
            Require(runs.GetProperty("runs").EnumerateArray().Any(r => r.GetProperty("runId").GetString() == runId && !r.GetProperty("running").GetBoolean()), "list_runs does not include the run.");
            return $"{runs.GetProperty("total").GetInt32()} run(s) for the test";
        }

        private async Task<string> UpdateAsync(CancellationToken ct)
        {
            await CallAsync("update_test", new { testId, name = "MCP verify: add a customer (renamed)" });
            var test = (await CallAsync("get_test", new { testId })).Structured;
            Require(test.GetProperty("name").GetString() == "MCP verify: add a customer (renamed)" && test.GetProperty("steps").GetArrayLength() == 5, "The rename was not persisted or steps changed.");
            var ids = test.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("id").GetString()!).ToList();
            var replacement = new object[]
            {
                new { id = ids[0], title = "Reset the form", action = "click", selector = "id:ResetButton" },
                new { id = ids[1], title = "Enter the customer name", action = "typeText", selector = "id:CustomerName", value = "MCP Grace" },
                new { title = "A new last step", action = "assertExists", selector = "id:AddCustomer" }
            };
            var updated = (await CallAsync("update_test", new { testId, steps = replacement })).Structured;
            Require(updated.GetProperty("stepCount").GetInt32() == 3, "update_test did not replace the steps.");
            var after = (await CallAsync("get_test", new { testId })).Structured.GetProperty("steps").EnumerateArray().ToList();
            Require(after.Count == 3 && after[0].GetProperty("id").GetString() == ids[0] && after[1].GetProperty("id").GetString() == ids[1] && after[1].GetProperty("value").GetString() == "MCP Grace" && !ids.Contains(after[2].GetProperty("id").GetString()!),
                "Replacement steps must keep the supplied ids and give the new step a new id.");
            var saved = new WorkspaceStore(workspace).LoadTests().Single(t => t.Id == testId);
            Require(saved.Steps.Count == 3 && saved.Steps[2].Action == StepAction.AssertExists, "The replaced steps were not saved for Studio.");
            return "renamed; three replacement steps saved, two with their ids kept";
        }

        private async Task<string> BackgroundRunAsync(CancellationToken ct)
        {
            waitTestId = (await CallAsync("create_test", new { name = "MCP verify: cancellation", intent = "A long wait that the client cancels.", steps = CancellationTestSteps() })).Structured.GetProperty("testId").GetString()!;
            var watch = Stopwatch.StartNew();
            var started = (await CallAsync("run_test", new { testId = waitTestId, pid = appPid, wait = false })).Structured;
            var background = started.GetProperty("runId").GetString()!;
            Require(watch.Elapsed < TimeSpan.FromSeconds(8) && started.GetProperty("running").GetBoolean(), $"wait false must return the run id at once with running true; it took {watch.Elapsed.TotalSeconds:F1} s.");
            var polled = (await CallAsync("get_run", new { runId = background })).Structured;
            Require(polled.GetProperty("running").GetBoolean(), "get_run must report the run as running.");
            Require((await CallAsync("list_runs", new { testId = waitTestId })).Structured.GetProperty("runs").EnumerateArray().Any(r => r.GetProperty("runId").GetString() == background && r.GetProperty("running").GetBoolean()), "list_runs must show the run as running.");
            var busy = await Client.CallToolAsync("perform_step", new { pid = appPid, step = new { action = "click", selector = "id:ResetButton" } });
            Require(busy.IsError && busy.Text.Contains("MCP verify: cancellation", StringComparison.Ordinal) && busy.Text.Contains(background, StringComparison.Ordinal) && busy.Text.Contains("cancel_run", StringComparison.Ordinal),
                "While the run holds the desktop, perform_step must name the test, the run and cancel_run: " + busy.Text);
            watch.Restart();
            var cancelled = (await CallAsync("cancel_run", new { runId = background })).Structured;
            Require(watch.Elapsed < TimeSpan.FromSeconds(12), $"cancel_run took {watch.Elapsed.TotalSeconds:F1} s.");
            RequireCancelledRun((await CallAsync("list_runs", new { testId = waitTestId })).Structured, cancelled);
            Require(File.Exists(Path.Combine(workspace, "runs", background + ".json")), "The cancelled run was not saved.");
            var saved = (await CallAsync("get_run", new { runId = background })).Structured;
            Require(saved.GetProperty("status").GetString() == "cancelled" && !saved.GetProperty("running").GetBoolean(), "get_run must read the final status from the saved file.");
            var again = await Client.CallToolAsync("cancel_run", new { runId = background });
            Require(again.IsError && again.Text.Contains("already finished", StringComparison.Ordinal), "cancel_run of a finished run must say so: " + again.Text);
            var click = await CallAsync("perform_step", new { pid = appPid, step = new { action = "click", selector = "id:ResetButton" }, includeScreenshot = false });
            Require(click.Structured.GetProperty("status").GetString() == "passed", "The app is not usable after cancel_run.");
            return $"run {background} polled as running, kept perform_step out, cancelled in {watch.Elapsed.TotalSeconds:F1} s and read back from its file";
        }

        private async Task<string> ErrorsAsync(CancellationToken ct)
        {
            var unknown = await Client.CallToolAsync("no_such_tool");
            Require(unknown.ErrorCode == JsonRpc.InvalidParams, "An unknown tool must be a -32602 protocol error; got " + unknown.Response.GetRawText());
            var missing = await Client.CallToolAsync("get_test", new { testId = "does-not-exist" });
            Require(missing.Error is null && missing.IsError && missing.Text.Contains("list_tests", StringComparison.Ordinal), "A failing tool call must return isError with guidance.");
            return "unknown tool → -32602; missing test → isError";
        }

        private async Task<string> CancelAsync(CancellationToken ct)
        {
            var known = (await CallAsync("list_runs", new { testId = waitTestId })).Structured.GetProperty("runs").EnumerateArray().Select(r => r.GetProperty("runId").GetString()).ToHashSet();
            var lines = Client.RawLines.Count;
            var watch = Stopwatch.StartNew();
            var pending = Client.StartToolAsync("run_test", new { testId = waitTestId, pid = appPid }, JsonValue.Create("cancel-1"), progressToken: "cancel-progress");
            await Task.Delay(2500, ct);
            await Client.NotifyAsync("notifications/cancelled", new { requestId = "cancel-1", reason = "verify-mcp cancellation check" });
            JsonElement runs = default;
            await UntilAsync(async () =>
            {
                runs = (await CallAsync("list_runs", new { testId = waitTestId })).Structured;
                return runs.GetProperty("runs").EnumerateArray().Any(r => !known.Contains(r.GetProperty("runId").GetString()) && r.GetProperty("status").GetString() == "cancelled" && !r.GetProperty("running").GetBoolean());
            }, "The cancelled run was not recorded.", 12000, ct);
            var elapsed = watch.Elapsed;
            var cancelledId = runs.GetProperty("runs").EnumerateArray().First(r => !known.Contains(r.GetProperty("runId").GetString())).GetProperty("runId").GetString();
            RequireCancelledRun(runs, (await CallAsync("get_run", new { runId = cancelledId })).Structured);
            var progress = Client.Notifications.Count(n => n.GetProperty("method").GetString() == "notifications/progress" && n.GetProperty("params").GetProperty("progressToken").GetString() == "cancel-progress");
            await Task.Delay(2000, ct);
            Require(!pending.IsCompleted, "A cancelled request must not be answered.");
            Require(Client.RawLines.Skip(lines).All(line => !line.Contains("\"cancel-1\"", StringComparison.Ordinal)), "A line carried the id of the cancelled request.");
            Require(Client.Notifications.Count(n => n.GetProperty("method").GetString() == "notifications/progress" && n.GetProperty("params").GetProperty("progressToken").GetString() == "cancel-progress") == progress, "Progress was sent after the cancellation.");
            var click = await CallAsync("perform_step", new { pid = appPid, step = new { action = "click", selector = "id:ResetButton" }, observation = "full", filter = "CustomerName" });
            Require(click.Structured.GetProperty("status").GetString() == "passed", "The app is not usable after cancellation.");
            var name = Text(click.Structured.GetProperty("observation").GetProperty("elements").EnumerateArray().First(e => e.GetProperty("selector").GetString() == "id:CustomerName"), "value");
            Require(name != "MUST NOT EXECUTE", "The step after the cancelled wait ran.");
            return $"cancelled after {elapsed.TotalSeconds:F1} s without a response; run {cancelledId} recorded as cancelled with its third step skipped; app usable";
        }

        private async Task<string> RunWithExeAsync(CancellationToken ct)
        {
            var created = (await CallAsync("create_test", new { name = "MCP verify: launched for the run", intent = "The run starts its own Customer Desk.", targetPath = sampleApp, steps = CustomerTestSteps("MCP Launch", "launch@example.test") })).Structured.GetProperty("testId").GetString()!;
            // Without pid, exe or app the test's stored app is used: here the instance launched earlier, which keeps running.
            var stored = (await CallAsync("run_test", new { testId = created, waitSeconds = 120 }, timeoutSeconds: 180)).Structured;
            var storedApp = stored.GetProperty("resolvedApp");
            Require(stored.GetProperty("status").GetString() == "passed" && storedApp.GetProperty("pid").GetInt32() == appPid && !storedApp.GetProperty("launched").GetBoolean() && storedApp.GetProperty("kind").GetString() == "test",
                "run_test with only the testId must connect to the running instance of the stored app: " + stored.GetRawText());
            Require(Alive(appPid), "A running instance used for the run must stay open.");
            var call = await CallAsync("run_test", new { testId = created, exe = sampleApp, waitForWindowSeconds = 30, waitSeconds = 120 }, progressToken: "run-exe", timeoutSeconds: 180);
            var run = call.Structured;
            Require(run.GetProperty("status").GetString() == "passed", "The run against the launched app did not pass: " + run.GetProperty("summary").GetString());
            var launchedPid = run.GetProperty("target").GetProperty("pid").GetInt32();
            Require(launchedPid != appPid, "The run must use the app it launched.");
            var progress = Client.Notifications.Where(n => n.GetProperty("method").GetString() == "notifications/progress" && n.GetProperty("params").GetProperty("progressToken").GetString() == "run-exe").Select(n => n.GetProperty("params")).ToList();
            var values = progress.Select(p => p.GetProperty("progress").GetDouble()).ToList();
            Require(values.Count >= 3 && progress.All(p => p.GetProperty("total").GetDouble() == 6), "The total must be the steps plus the launch (6) throughout: " + string.Join(", ", progress.Select(p => p.GetProperty("total").GetDouble())));
            Require(values.Zip(values.Skip(1)).All(pair => pair.Second > pair.First) && values.All(v => v <= 6) && values[^1] == 6, "Progress must rise strictly and end at the total: " + string.Join(", ", values));
            await RequireGoneAsync(launchedPid, "The app launched for the run");
            Require(Alive(appPid), "The app launched earlier must stay open.");
            await CallAsync("delete_test", new { testId = created });
            return $"the stored app ran in the running pid {appPid} (not launched); with exe: launched pid {launchedPid} for the run, {values.Count} progress notifications on a scale of 6, closed afterwards";
        }

        private async Task<string> CloseAndRelaunchAsync(CancellationToken ct)
        {
            var foreign = await Client.CallToolAsync("close_app", new { pid = server!.Id });
            Require(foreign.IsError && foreign.Text.Contains("was not launched by this server", StringComparison.Ordinal), "close_app must refuse a process this server did not launch: " + foreign.Text);
            var closed = (await CallAsync("close_app", new { pid = appPid })).Structured;
            Require(closed.GetProperty("closed").GetBoolean() && !closed.GetProperty("forced").GetBoolean(), "close_app did not close the app: " + closed.GetRawText());
            await RequireGoneAsync(appPid, "The app closed with close_app");
            Require((await CallAsync("get_workspace_info")).Structured.GetProperty("launchedApps").GetArrayLength() == 0, "The closed app is still listed.");
            var old = appPid;
            appPid = (await CallAsync("launch_app", new { exe = sampleApp, waitForWindowSeconds = 30 })).Structured.GetProperty("pid").GetInt32();
            var status = (await CallAsync("inspect_app", new { pid = appPid, includeScreenshot = false, selector = "id:StatusMessage" })).Structured.GetProperty("elements")[0];
            return $"closed pid {old}; started again as pid {appPid} with status '{Text(status, "value") ?? Text(status, "name")}'";
        }

        private async Task<string> DeleteAsync(CancellationToken ct)
        {
            var deleted = (await CallAsync("delete_test", new { testId })).Structured;
            Require(deleted.GetProperty("deleted").GetBoolean() && !File.Exists(Path.Combine(workspace, "tests", testId + ".json")), "The test file still exists.");
            var tests = (await CallAsync("list_tests")).Structured;
            Require(tests.GetProperty("tests").EnumerateArray().All(t => t.GetProperty("testId").GetString() != testId), "list_tests still lists the deleted test.");
            return "deleted " + testId;
        }

        private async Task<string> ShutdownAsync(CancellationToken ct)
        {
            Client.CloseInput();
            var exit = await WaitForExitAsync(server!, TimeSpan.FromSeconds(15));
            Require(exit == 0, $"The server exited with code {exit}.");
            await RequireGoneAsync(appPid, "The launched sample app");
            return $"exit code {exit}; sample app pid {appPid} closed";
        }

        private async Task<string> HygieneAsync(CancellationToken ct)
        {
            await Client.Reader.WaitAsync(TimeSpan.FromSeconds(5));
            Require(Client.UnparseableLines == 0, $"{Client.UnparseableLines} stdout line(s) were not JSON objects.");
            Require(Client.RawLines.Count > 0 && Client.RawLines.All(l => l.TrimStart().StartsWith('{')), "stdout contained a non-object line.");
            var lines = await stderr!.WaitAsync(TimeSpan.FromSeconds(5));
            RequireNoJsonRpc(lines);
            Require(lines.Any(l => l.StartsWith("[testy-mcp]", StringComparison.Ordinal)), "stderr has no [testy-mcp] log lines.");
            return $"{Client.RawLines.Count} stdout JSON lines; {lines.Count} stderr log lines";
        }

        public async ValueTask DisposeAsync()
        {
            if (client is not null) await client.DisposeAsync();
            if (server is not null)
            {
                if (!server.HasExited) { try { server.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
                server.Dispose();
            }
            Kill(appPid);
        }
    }

    // ═══════════════ Streamable HTTP ═══════════════
    private sealed record HttpReply(int Status, string Body, string? MediaType, string? SessionId)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement.Clone();
        public List<JsonElement> Events => Body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(block => string.Concat(block.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].TrimStart())))
            .Where(data => data.Length > 0).Select(data => JsonDocument.Parse(data).RootElement.Clone()).ToList();
        /// <summary>The response: the body of a JSON answer, or the last event with an id of a stream.</summary>
        public JsonElement Message => MediaType == "text/event-stream" ? Events.Last(e => e.TryGetProperty("id", out _) && !e.TryGetProperty("method", out _)) : Json;
    }
    private sealed class HttpScenario(string runRoot, string? lab) : IAsyncDisposable
    {
        private const string Driver = "http";
        private readonly string token = "verify-mcp-" + Guid.NewGuid().ToString("N");
        private readonly string workspace = Path.Combine(runRoot, "workspace-http");
        private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };
        private Process? server, detached;
        private Task<List<string>>? stderr;
        private Dictionary<string, JsonElement> schemas = [];
        private string url = "", sessionId = "", sampleApp = "", testId = "", runId = "", waitTestId = "";
        private int appPid, requestId = 100;
        private static readonly JsonObject Meta = new() { [McpServer.MetaProtocolVersion] = "2026-07-28", [McpServer.MetaClientInfo] = new JsonObject { ["name"] = "testy-verify-mcp", ["version"] = McpServer.Version }, [McpServer.MetaClientCapabilities] = new JsonObject() };

        public IEnumerable<(string Name, string Driver, Func<CancellationToken, Task<string>> Run)> Checks()
        {
            yield return ("the HTTP server starts on a free loopback port, prints its URL, writes endpoint.json and answers GET with 405", Driver, StartAsync);
            yield return ("initialize returns an Mcp-Session-Id; missing token, missing session and unknown session are 401, 400 and 404", Driver, SessionRulesAsync);
            yield return ("a non-loopback Origin is 403, a localhost Origin is served, and a body that is not application/json is 415", Driver, OriginAsync);
            yield return ("launch_app, create_test and run_test replay over SSE deliver progress events and a passing run", Driver, RunAsync);
            yield return ("get_run over HTTP reports the passed run", Driver, GetRunAsync);
            yield return ("run_test with wait false over HTTP is polled and stopped by cancel_run", Driver, BackgroundRunAsync);
            yield return ("notifications/cancelled through the session ends the stream without a response and the run is recorded as cancelled", Driver, CancelAsync);
            yield return ("closing the SSE stream cancels a running test", Driver, DisconnectAsync);
            yield return ("stateless 2026-07-28 requests: server/discover, resultType, header validation and error codes", Driver, StatelessAsync);
            yield return ("an HTTP server started without stdin (the NUL device) keeps serving", Driver, DetachedAsync);
            yield return ("DELETE ends the session and closing stdin exits the server cleanly", Driver, ShutdownAsync);
        }

        private HttpRequestMessage Message(HttpMethod method, object? body, IDictionary<string, string>? headers = null, bool authorize = true, bool session = true, string contentType = "application/json", string? address = null)
        {
            var request = new HttpRequestMessage(method, address ?? url);
            if (body is not null)
            {
                var text = body as string ?? (body is JsonNode node ? node.ToJsonString() : JsonSerializer.Serialize(body, TestyJson.Options));
                request.Content = new StringContent(text, Encoding.UTF8, contentType);
            }
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            if (authorize) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            if (session && sessionId.Length > 0) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
            if (headers is not null) foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
            return request;
        }
        private async Task<HttpReply> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using (request)
            using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                var content = await response.Content.ReadAsStringAsync(ct);
                return new HttpReply((int)response.StatusCode, content, response.Content.Headers.ContentType?.MediaType, response.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.FirstOrDefault() : null);
            }
        }
        private Task<HttpReply> SendAsync(HttpMethod method, object? body, IDictionary<string, string>? headers = null, bool authorize = true, bool session = true, CancellationToken ct = default) => SendAsync(Message(method, body, headers, authorize, session), ct);
        private Task<HttpReply> PostAsync(object body, CancellationToken ct, IDictionary<string, string>? headers = null, bool authorize = true, bool session = true) => SendAsync(HttpMethod.Post, body, headers, authorize, session, ct);
        private static JsonObject Request(string method, object? parameters, JsonNode id) { var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method }; if (parameters is not null) message["params"] = parameters as JsonNode ?? JsonRpc.ToNode(parameters); return message; }
        private static JsonObject ToolCall(string name, object arguments, JsonNode id, string? progressToken = null)
        {
            var parameters = new JsonObject { ["name"] = name, ["arguments"] = arguments as JsonNode ?? JsonRpc.ToNode(arguments) };
            if (progressToken is not null) parameters["_meta"] = new JsonObject { ["progressToken"] = progressToken };
            return Request("tools/call", parameters, id);
        }
        private static JsonObject Modern(string method, JsonObject? parameters, JsonNode id, string? version = null)
        {
            var meta = Meta.DeepClone().AsObject();
            if (version is not null) meta[McpServer.MetaProtocolVersion] = version;
            var p = parameters ?? new JsonObject();
            p["_meta"] = meta;
            return Request(method, p, id);
        }
        private static Dictionary<string, string> ModernHeaders(string method, string? name = null, string version = "2026-07-28")
        {
            var headers = new Dictionary<string, string> { ["MCP-Protocol-Version"] = version, ["Mcp-Method"] = method };
            if (name is not null) headers["Mcp-Name"] = name;
            return headers;
        }
        /// <summary>A tool call over the session. It is answered as an SSE stream; the result is held to the tool's output schema.</summary>
        private async Task<McpToolCall> CallAsync(string tool, object arguments, CancellationToken ct, bool conform = true)
        {
            var reply = await PostAsync(ToolCall(tool, arguments, JsonValue.Create(Interlocked.Increment(ref requestId))), ct);
            Require(reply.Status == 200 && reply.MediaType == "text/event-stream", $"{tool}: a tools/call must be answered as a stream; got HTTP {reply.Status} {reply.MediaType} {reply.Body}");
            var call = new McpToolCall(reply.Message);
            return conform ? Conforming(schemas, tool, call.RequireOk(tool)) : call;
        }

        private async Task<string> StartAsync(CancellationToken ct)
        {
            Directory.CreateDirectory(workspace);
            var start = WorkerCommand.CreateSelfStartInfo();
            start.RedirectStandardInput = true;
            // The token travels in the environment, not on the command line.
            start.Environment[McpOptions.TokenVariable] = token;
            foreach (var argument in new[] { "mcp", "--workspace", workspace, "--transport", "http", "--port", "0" }) start.ArgumentList.Add(argument);
            server = Process.Start(start) ?? throw new InvalidOperationException("The MCP server process did not start.");
            stderr = DrainAsync(server.StandardError, Path.Combine(runRoot, "http-stderr.txt"));
            var line = await server.StandardOutput.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(45), ct) ?? throw new InvalidOperationException("The HTTP server printed no startup line.");
            var startup = JsonDocument.Parse(line).RootElement;
            Require(startup.GetProperty("transport").GetString() == "http", "The startup line is not the HTTP announcement.");
            url = startup.GetProperty("url").GetString()!;
            var port = startup.GetProperty("port").GetInt32();
            Require(url == $"http://127.0.0.1:{port}/mcp" && port > 0, "Unexpected URL: " + url);
            Require(startup.GetProperty("tokenRequired").GetBoolean() && !line.Contains(token, StringComparison.Ordinal), "tokenRequired should be true, and the token must not be printed.");
            var endpoint = JsonDocument.Parse(await File.ReadAllTextAsync(McpCommand.EndpointFile(workspace), ct)).RootElement;
            Require(endpoint.GetProperty("url").GetString() == url && endpoint.GetProperty("pid").GetInt32() == server.Id && endpoint.GetProperty("tokenRequired").GetBoolean() && !endpoint.GetRawText().Contains(token, StringComparison.Ordinal), "endpoint.json must name the endpoint and never the token: " + endpoint.GetRawText());
            var get = await SendAsync(HttpMethod.Get, null, ct: ct);
            Require(get.Status == 405, $"GET must be 405; got {get.Status}.");
            return url + "; endpoint.json written";
        }

        private async Task<string> SessionRulesAsync(CancellationToken ct)
        {
            var initialize = Request("initialize", new JsonObject { ["protocolVersion"] = "2025-11-25", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "testy-verify-mcp", ["version"] = McpServer.Version } }, JsonValue.Create(1));
            var unauthorized = await PostAsync(initialize, ct, authorize: false);
            Require(unauthorized.Status == 401, $"Missing bearer token must be 401; got {unauthorized.Status}.");
            var accepted = await PostAsync(initialize, ct);
            Require(accepted.Status == 200 && accepted.SessionId is { Length: > 0 }, $"initialize must return 200 with Mcp-Session-Id; got {accepted.Status}.");
            var result = Result(accepted.Json, "initialize");
            Require(result.GetProperty("protocolVersion").GetString() == "2025-11-25", "The protocol version was not echoed.");
            sessionId = accepted.SessionId!;
            var initialized = await PostAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, ct);
            Require(initialized.Status == 202, $"A notification must be 202; got {initialized.Status}.");
            var noSession = await PostAsync(Request("tools/list", null, JsonValue.Create(2)), ct, session: false);
            Require(noSession.Status == 400, $"A request without Mcp-Session-Id must be 400; got {noSession.Status}.");
            var unknownSession = await PostAsync(Request("tools/list", null, JsonValue.Create(3)), ct, new Dictionary<string, string> { ["Mcp-Session-Id"] = "0123456789ABCDEF0123456789ABCDEF" }, session: false);
            Require(unknownSession.Status == 404, $"An unknown session must be 404; got {unknownSession.Status}.");
            var tools = await PostAsync(Request("tools/list", null, JsonValue.Create(4)), ct);
            Require(tools.Status == 200 && tools.MediaType == "application/json" && Result(tools.Json, "tools/list").GetProperty("tools").GetArrayLength() == ExpectedTools.Length, "tools/list over the session failed.");
            schemas = Schemas(Result(tools.Json, "tools/list"));
            var old = sessionId;
            var again = await PostAsync(initialize, ct);
            Require(again.Status == 200 && again.SessionId is { Length: > 0 } && again.SessionId != old, "initialize on an existing session must mint a new session id.");
            sessionId = again.SessionId!;
            var replaced = await PostAsync(Request("ping", null, JsonValue.Create(5)), ct, new Dictionary<string, string> { ["Mcp-Session-Id"] = old }, session: false);
            Require(replaced.Status == 404, $"The replaced session must be 404; got {replaced.Status}.");
            return $"session {sessionId[..8]}…; 401/400/404 rules hold; initialize replaced session {old[..8]}…";
        }

        private async Task<string> OriginAsync(CancellationToken ct)
        {
            var evil = await PostAsync(Request("ping", null, JsonValue.Create(5)), ct, new Dictionary<string, string> { ["Origin"] = "http://evil.example" });
            Require(evil.Status == 403, $"A foreign Origin must be 403; got {evil.Status}.");
            var lookalike = await PostAsync(Request("ping", null, JsonValue.Create(5)), ct, new Dictionary<string, string> { ["Origin"] = "http://localhost.evil.example" });
            Require(lookalike.Status == 403, $"A look-alike Origin must be 403; got {lookalike.Status}.");
            var local = await PostAsync(Request("ping", null, JsonValue.Create(6)), ct, new Dictionary<string, string> { ["Origin"] = "http://localhost:5173" });
            Require(local.Status == 200, $"A localhost Origin must be served; got {local.Status}.");
            var plain = await SendAsync(Message(HttpMethod.Post, Request("ping", null, JsonValue.Create(7)), contentType: "text/plain"), ct);
            Require(plain.Status == 415, $"A text/plain body must be 415; got {plain.Status}.");
            return "evil.example and localhost.evil.example → 403; localhost → 200; text/plain → 415";
        }

        private async Task<string> RunAsync(CancellationToken ct)
        {
            var info = (await CallAsync("get_workspace_info", new JsonObject(), ct)).Structured;
            sampleApp = ResolveSampleApp(lab, info);
            var launched = (await CallAsync("launch_app", new { exe = sampleApp, waitForWindowSeconds = 30 }, ct)).Structured;
            appPid = launched.GetProperty("pid").GetInt32();
            await CallAsync("inspect_app", new { pid = appPid, maxWidth = 640 }, ct);
            await CallAsync("screenshot_app", new { pid = appPid }, ct);
            await CallAsync("perform_step", new { pid = appPid, step = new { action = "click", selector = "id:ResetButton" } }, ct);
            var created = (await CallAsync("create_test", new { name = "MCP verify (http): add a customer", intent = "Add a customer through the HTTP transport.", steps = CustomerTestSteps("MCP Grace", "grace@example.test") }, ct)).Structured;
            testId = created.GetProperty("testId").GetString()!;
            var reply = await PostAsync(ToolCall("run_test", new { testId, pid = appPid, waitSeconds = 120 }, JsonValue.Create(13), progressToken: "http-run"), ct);
            Require(reply.Status == 200 && reply.MediaType == "text/event-stream", $"run_test must stream SSE; got {reply.Status} {reply.MediaType}.");
            var events = reply.Events;
            var progress = events.Where(e => e.TryGetProperty("method", out var m) && m.GetString() == "notifications/progress").Select(e => e.GetProperty("params").GetProperty("progress").GetDouble()).ToList();
            Require(events.Count > 0 && events[^1].TryGetProperty("id", out _), "The response must be the last event of the stream.");
            var call = Conforming(schemas, "run_test", new McpToolCall(reply.Message).RequireOk("run_test"));
            runId = call.Structured.GetProperty("runId").GetString()!;
            Require(call.Structured.GetProperty("status").GetString() == "passed", "The HTTP run did not pass: " + call.Structured.GetProperty("summary").GetString());
            Require(progress.Count >= 3 && progress.Zip(progress.Skip(1)).All(p => p.Second > p.First) && progress[^1] == 5, $"Expected progress rising to 5; got {string.Join(", ", progress)}.");
            Require(File.Exists(Path.Combine(workspace, "runs", runId + ".json")), "run.json is missing.");
            return $"pid {appPid}; test {testId}; run {runId} passed; {progress.Count} SSE progress events";
        }

        private async Task<string> GetRunAsync(CancellationToken ct)
        {
            var run = (await CallAsync("get_run", new { runId }, ct)).Structured;
            Require(run.GetProperty("status").GetString() == "passed" && run.GetProperty("steps").GetArrayLength() == 5 && run.GetProperty("screenshotsAvailable").GetBoolean(), "get_run is inconsistent with the run.");
            var screenshot = await CallAsync("get_run_screenshot", new { runId, stepNumber = 4 }, ct);
            ValidatePng(screenshot.Images.Single(), "step 4 screenshot");
            await CallAsync("list_runs", new { testId }, ct);
            await CallAsync("list_apps", new JsonObject(), ct);
            return run.GetProperty("summary").GetString() ?? "";
        }

        private async Task<string> BackgroundRunAsync(CancellationToken ct)
        {
            waitTestId = (await CallAsync("create_test", new { name = "MCP verify (http): cancellation", intent = "A long wait cancelled through the session.", steps = CancellationTestSteps() }, ct)).Structured.GetProperty("testId").GetString()!;
            var started = (await CallAsync("run_test", new { testId = waitTestId, pid = appPid, wait = false }, ct)).Structured;
            var background = started.GetProperty("runId").GetString()!;
            Require(started.GetProperty("running").GetBoolean(), "wait false must return running true.");
            Require((await CallAsync("get_run", new { runId = background }, ct)).Structured.GetProperty("running").GetBoolean(), "get_run must report the run as running.");
            var busy = await CallAsync("perform_step", new { pid = appPid, step = new { action = "click", selector = "id:ResetButton" } }, ct, conform: false);
            Require(busy.IsError && busy.Text.Contains(background, StringComparison.Ordinal), "perform_step must be refused while the run holds the desktop: " + busy.Text);
            var cancelled = (await CallAsync("cancel_run", new { runId = background }, ct)).Structured;
            RequireCancelledRun((await CallAsync("list_runs", new { testId = waitTestId }, ct)).Structured, cancelled);
            Require(!(await CallAsync("get_run", new { runId = background }, ct)).Structured.GetProperty("running").GetBoolean(), "get_run must report the run as finished.");
            return $"run {background} polled as running, cancelled by cancel_run and read back as cancelled";
        }

        private async Task<HashSet<string?>> KnownRunsAsync(CancellationToken ct) =>
            (await CallAsync("list_runs", new { testId = waitTestId, limit = 200 }, ct)).Structured.GetProperty("runs").EnumerateArray().Select(r => r.GetProperty("runId").GetString()).ToHashSet();
        private async Task<string> CancelledRunAsync(HashSet<string?> known, CancellationToken ct)
        {
            JsonElement runs = default;
            await UntilAsync(async () =>
            {
                runs = (await CallAsync("list_runs", new { testId = waitTestId, limit = 200 }, ct)).Structured;
                return runs.GetProperty("runs").EnumerateArray().Any(r => !known.Contains(r.GetProperty("runId").GetString()) && r.GetProperty("status").GetString() == "cancelled" && !r.GetProperty("running").GetBoolean());
            }, "The cancelled run was not recorded.", 12000, ct);
            var id = runs.GetProperty("runs").EnumerateArray().First(r => !known.Contains(r.GetProperty("runId").GetString())).GetProperty("runId").GetString();
            return RequireCancelledRun(runs, (await CallAsync("get_run", new { runId = id }, ct)).Structured);
        }

        private async Task<string> CancelAsync(CancellationToken ct)
        {
            var known = await KnownRunsAsync(ct);
            var watch = Stopwatch.StartNew();
            var pending = PostAsync(ToolCall("run_test", new { testId = waitTestId, pid = appPid }, JsonValue.Create("http-cancel"), progressToken: "http-cancel-progress"), ct);
            await Task.Delay(2500, ct);
            var cancel = await PostAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled", ["params"] = new JsonObject { ["requestId"] = "http-cancel", ["reason"] = "verify-mcp" } }, ct);
            Require(cancel.Status == 202, $"The cancellation notification must be 202; got {cancel.Status}.");
            var reply = await pending;
            Require(watch.Elapsed < TimeSpan.FromSeconds(12), $"Cancellation took {watch.Elapsed.TotalSeconds:F1} s.");
            Require(reply.Status == 200 && reply.MediaType == "text/event-stream" && reply.Events.All(e => !e.TryGetProperty("id", out _)), "The stream of a cancelled request must end without a response event: " + reply.Body);
            var cancelledId = await CancelledRunAsync(known, ct);
            var click = await CallAsync("perform_step", new { pid = appPid, step = new { action = "click", selector = "id:ResetButton" } }, ct);
            Require(click.Structured.GetProperty("status").GetString() == "passed", "The app is not usable after cancellation.");
            return $"stream ended without a response after {watch.Elapsed.TotalSeconds:F1} s; run {cancelledId} recorded as cancelled; app usable";
        }

        private async Task<string> DisconnectAsync(CancellationToken ct)
        {
            var known = await KnownRunsAsync(ct);
            var request = Message(HttpMethod.Post, ToolCall("run_test", new { testId = waitTestId, pid = appPid }, JsonValue.Create("http-disconnect"), progressToken: "http-disconnect-progress"));
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            Require(response.Content.Headers.ContentType?.MediaType == "text/event-stream", "run_test must be answered as a stream.");
            var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[4096];
            var read = await stream.ReadAsync(buffer, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(15), ct);
            Require(read > 0 && Encoding.UTF8.GetString(buffer, 0, read).Contains("notifications/progress", StringComparison.Ordinal), "The stream must start with a progress event.");
            var watch = Stopwatch.StartNew();
            response.Dispose();
            request.Dispose();
            var cancelledId = await CancelledRunAsync(known, ct);
            Require(watch.Elapsed < TimeSpan.FromSeconds(12), $"Closing the stream cancelled the run only after {watch.Elapsed.TotalSeconds:F1} s.");
            var click = await CallAsync("perform_step", new { pid = appPid, step = new { action = "click", selector = "id:ResetButton" } }, ct);
            Require(click.Structured.GetProperty("status").GetString() == "passed", "The app is not usable after the stream was closed.");
            return $"closing the stream cancelled run {cancelledId} within {watch.Elapsed.TotalSeconds:F1} s; app usable";
        }

        private async Task<string> StatelessAsync(CancellationToken ct)
        {
            var discover = await PostAsync(Modern("server/discover", null, JsonValue.Create("d-1")), ct, ModernHeaders("server/discover"), session: false);
            Require(discover.Status == 200, $"server/discover must be 200; got {discover.Status} {discover.Body}");
            var discovered = Result(discover.Json, "server/discover");
            Require(discovered.GetProperty("resultType").GetString() == "complete" && discovered.GetProperty("supportedVersions").EnumerateArray().Any(v => v.GetString() == "2026-07-28"), "server/discover is incomplete.");
            var list = await PostAsync(Modern("tools/list", null, JsonValue.Create("d-2")), ct, ModernHeaders("tools/list"), session: false);
            var listed = Result(list.Json, "modern tools/list");
            Require(list.Status == 200 && listed.GetProperty("resultType").GetString() == "complete" && listed.TryGetProperty("ttlMs", out _) && listed.GetProperty("tools").GetArrayLength() == ExpectedTools.Length, "The stateless tools/list lacks resultType/ttlMs.");
            var noHeader = await PostAsync(Modern("tools/list", null, JsonValue.Create("d-3")), ct, session: false);
            Require(noHeader.Status == 400 && noHeader.Json.GetProperty("error").GetProperty("code").GetInt32() == JsonRpc.HeaderMismatch, $"A missing MCP-Protocol-Version header must be 400/-32020; got {noHeader.Status} {noHeader.Body}");
            var unsupported = await PostAsync(Modern("tools/list", null, JsonValue.Create("d-4"), "1999-01-01"), ct, ModernHeaders("tools/list", version: "1999-01-01"), session: false);
            Require(unsupported.Status == 400 && unsupported.Json.GetProperty("error").GetProperty("code").GetInt32() == JsonRpc.UnsupportedProtocolVersion && unsupported.Json.GetProperty("error").GetProperty("data").GetProperty("supported").GetArrayLength() > 0, $"An unsupported version must be 400/-32022 with supported versions; got {unsupported.Status} {unsupported.Body}");
            var unknown = await PostAsync(Modern("no/such", null, JsonValue.Create("d-5")), ct, ModernHeaders("no/such"), session: false);
            Require(unknown.Status == 404 && unknown.Json.GetProperty("error").GetProperty("code").GetInt32() == JsonRpc.MethodNotFound, $"An unknown method must be 404/-32601; got {unknown.Status}.");
            var ping = await PostAsync(Modern("ping", null, JsonValue.Create("d-5b")), ct, ModernHeaders("ping"), session: false);
            Require(ping.Status == 404 && ping.Json.GetProperty("error").GetProperty("code").GetInt32() == JsonRpc.MethodNotFound, $"ping is not part of revision 2026-07-28 (404/-32601); got {ping.Status}.");
            var incomplete = Modern("tools/list", null, JsonValue.Create("d-5c"));
            incomplete["params"]!["_meta"]!.AsObject().Remove(McpServer.MetaClientCapabilities);
            var invalid = await PostAsync(incomplete, ct, ModernHeaders("tools/list"), session: false);
            Require(invalid.Status == 400 && invalid.Json.GetProperty("error").GetProperty("code").GetInt32() == JsonRpc.InvalidParams, $"A request without clientCapabilities must be 400/-32602; got {invalid.Status} {invalid.Body}");
            var call = await PostAsync(Modern("tools/call", new JsonObject { ["name"] = "get_workspace_info", ["arguments"] = new JsonObject() }, JsonValue.Create("d-6")), ct, ModernHeaders("tools/call", "get_workspace_info"), session: false);
            var called = Result(call.Message, "modern tools/call");
            Require(call.Status == 200 && call.MediaType == "text/event-stream" && called.GetProperty("resultType").GetString() == "complete" && called.TryGetProperty("structuredContent", out _), "The stateless tools/call result is incomplete.");
            var noName = await PostAsync(Modern("tools/call", new JsonObject { ["name"] = "get_workspace_info", ["arguments"] = new JsonObject() }, JsonValue.Create("d-7")), ct, ModernHeaders("tools/call"), session: false);
            Require(noName.Status == 400 && noName.Json.GetProperty("error").GetProperty("code").GetInt32() == JsonRpc.HeaderMismatch, "tools/call without Mcp-Name must be 400/-32020.");
            var unknownTool = await PostAsync(Modern("tools/call", new JsonObject { ["name"] = "nope", ["arguments"] = new JsonObject() }, JsonValue.Create("d-8")), ct, ModernHeaders("tools/call", "nope"), session: false);
            Require(unknownTool.Status == 200 && unknownTool.Json.GetProperty("error").GetProperty("code").GetInt32() == JsonRpc.InvalidParams, $"An unknown tool must stay 200/-32602; got {unknownTool.Status}.");
            var notification = await PostAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled", ["params"] = new JsonObject { ["requestId"] = "none", ["_meta"] = Meta.DeepClone() } }, ct, session: false);
            Require(notification.Status == 202, $"A stateless notification must be 202; got {notification.Status}.");
            // subscriptions/listen: acknowledged on a stream that the client closes.
            var listen = Message(HttpMethod.Post, Modern("subscriptions/listen", new JsonObject { ["notifications"] = new JsonObject() }, JsonValue.Create("d-9")), ModernHeaders("subscriptions/listen"), session: false);
            using (var response = await http.SendAsync(listen, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                Require((int)response.StatusCode == 200 && response.Content.Headers.ContentType?.MediaType == "text/event-stream", $"subscriptions/listen must be answered as a stream; got {(int)response.StatusCode}.");
                var buffer = new byte[4096];
                var read = await (await response.Content.ReadAsStreamAsync(ct)).ReadAsync(buffer, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct);
                Require(Encoding.UTF8.GetString(buffer, 0, read).Contains("notifications/subscriptions/acknowledged", StringComparison.Ordinal), "The subscription was not acknowledged first.");
            }
            listen.Dispose();
            return "discover, resultType, -32020, -32022, 404/-32601, 400/-32602, streamed tools/call, acknowledged subscription and 202 verified";
        }

        private async Task<string> DetachedAsync(CancellationToken ct)
        {
            // What "ignore stdin" means in most process libraries: the NUL device. The server must not take its end of input for a shutdown signal.
            var detachedWorkspace = Path.Combine(runRoot, "workspace-http-detached");
            Directory.CreateDirectory(detachedWorkspace);
            var self = WorkerCommand.CreateSelfStartInfo();
            var command = string.Join(" ", new[] { self.FileName }.Concat(self.ArgumentList).Concat(["mcp", "--workspace", detachedWorkspace, "--transport", "http", "--port", "0"]).Select(OwnedProcessJob.QuoteArgument));
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), $"/d /s /c \"{command} < NUL\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment.Remove(McpOptions.TokenVariable);
            detached = Process.Start(start) ?? throw new InvalidOperationException("The detached server did not start.");
            var log = DrainAsync(detached.StandardError, Path.Combine(runRoot, "http-detached-stderr.txt"));
            var line = await detached.StandardOutput.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(45), ct) ?? throw new InvalidOperationException("The detached server printed no startup line.");
            var startup = JsonDocument.Parse(line).RootElement;
            var address = startup.GetProperty("url").GetString()!;
            var serverPid = startup.GetProperty("pid").GetInt32();
            Require(!startup.GetProperty("tokenRequired").GetBoolean(), "Without a token the startup line must say so.");
            await Task.Delay(2500, ct);
            Require(Alive(serverPid), "A server started with stdin from NUL exited right after its startup line.");
            var discover = await SendAsync(Message(HttpMethod.Post, Modern("server/discover", null, JsonValue.Create("n-1")), ModernHeaders("server/discover"), authorize: false, session: false, address: address), ct);
            Require(discover.Status == 200 && Result(discover.Json, "server/discover").GetProperty("resultType").GetString() == "complete", $"The detached server must still answer after 2.5 s; got {discover.Status}.");
            try { using var process = Process.GetProcessById(serverPid); process.Kill(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
            await RequireGoneAsync(serverPid, "The detached server");
            var lines = await log.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Require(lines.Any(l => l.Contains("no stdin to watch", StringComparison.Ordinal)), "The server must log that it has no stdin to watch.");
            return $"{address} (server process {serverPid}) answered server/discover 2.5 s after it started without stdin";
        }

        private async Task<string> ShutdownAsync(CancellationToken ct)
        {
            var deleted = await SendAsync(HttpMethod.Delete, null, ct: ct);
            Require(deleted.Status == 200, $"DELETE must end the session with 200; got {deleted.Status}.");
            var afterwards = await PostAsync(Request("tools/list", null, JsonValue.Create(20)), ct);
            Require(afterwards.Status == 404, $"A request on the ended session must be 404; got {afterwards.Status}.");
            server!.StandardInput.Close();
            var exit = await WaitForExitAsync(server, TimeSpan.FromSeconds(15));
            Require(exit == 0, $"The HTTP server exited with code {exit}.");
            var remaining = await server.StandardOutput.ReadToEndAsync(ct);
            Require(remaining.Trim().Length == 0, "The HTTP server wrote more than its startup line to stdout: " + remaining);
            await RequireGoneAsync(appPid, "The launched sample app");
            Require(!File.Exists(McpCommand.EndpointFile(workspace)), "endpoint.json must be removed when the server stops.");
            var lines = await stderr!.WaitAsync(TimeSpan.FromSeconds(5), ct);
            RequireNoJsonRpc(lines);
            Require(lines.All(l => !l.Contains(token, StringComparison.Ordinal) && !l.Contains(sessionId, StringComparison.OrdinalIgnoreCase)), "The log must never contain the token or a whole session id.");
            return $"session ended; exit code {exit}; sample app pid {appPid} closed; endpoint.json removed; no secret in {lines.Count} log lines";
        }

        public async ValueTask DisposeAsync()
        {
            http.Dispose();
            foreach (var process in new[] { server, detached })
            {
                if (process is null) continue;
                if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
                process.Dispose();
            }
            Kill(appPid);
            await Task.CompletedTask;
        }
    }

    // ═══════════════ Process lifetime ═══════════════
    /// <summary>Apps the server launched end with it, however it ends, unless keepOpen was asked for.</summary>
    private sealed class LifetimeScenario(string runRoot, string? lab) : IAsyncDisposable
    {
        private const string Driver = "stdio";
        private readonly List<int> apps = [];
        private readonly List<Process> servers = [];

        public IEnumerable<(string Name, string Driver, Func<CancellationToken, Task<string>> Run)> Checks()
        {
            yield return ("an app launched with keepOpen survives the server's exit, one without ends even when the server is killed", Driver, LifetimeAsync);
        }

        private async Task<(Process Server, McpLineClient Client, int App)> LaunchAsync(string name, bool keepOpen, CancellationToken ct)
        {
            var workspace = Path.Combine(runRoot, "workspace-" + name);
            Directory.CreateDirectory(workspace);
            var server = StartServer(workspace, Path.Combine(runRoot, name + "-stderr.txt"), out _);
            servers.Add(server);
            var client = new McpLineClient(server.StandardOutput.BaseStream, server.StandardInput.BaseStream);
            Result(await client.RequestAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "testy-verify-mcp", ["version"] = McpServer.Version } }, TimeSpan.FromSeconds(45)), "initialize");
            var info = (await client.CallToolAsync("get_workspace_info")).RequireOk("get_workspace_info").Structured;
            var launched = (await client.CallToolAsync("launch_app", new { exe = ResolveSampleApp(lab, info), waitForWindowSeconds = 30, keepOpen }, timeout: TimeSpan.FromSeconds(60))).RequireOk("launch_app").Structured;
            var app = launched.GetProperty("pid").GetInt32();
            apps.Add(app);
            Require(launched.GetProperty("keepOpen").GetBoolean() == keepOpen && Alive(app), "The app was not launched as asked.");
            return (server, client, app);
        }

        private async Task<string> LifetimeAsync(CancellationToken ct)
        {
            // keepOpen: the server exits in an orderly way (stdin closed) and the app stays; the pipes close although the app is still running.
            var kept = await LaunchAsync("keep-open", keepOpen: true, ct);
            kept.Client.CloseInput();
            var exit = await WaitForExitAsync(kept.Server, TimeSpan.FromSeconds(15));
            await kept.Client.Reader.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await Task.Delay(1000, ct);
            Require(exit == 0 && Alive(kept.App), "An app launched with keepOpen must survive the server's exit.");
            Kill(kept.App);
            await RequireGoneAsync(kept.App, "The app that was kept open");

            // keepOpen, server killed: the app still stays.
            var orphan = await LaunchAsync("keep-open-killed", keepOpen: true, ct);
            orphan.Server.Kill();
            await WorkerCommand.WaitForExitAsync(orphan.Server, TimeSpan.FromSeconds(10));
            await Task.Delay(1500, ct);
            Require(Alive(orphan.App), "An app launched with keepOpen must survive a killed server.");
            Kill(orphan.App);
            await RequireGoneAsync(orphan.App, "The app that was kept open");

            // Without keepOpen the app is bound to the server's job object: killing the server ends it, although no server code runs any more.
            var bound = await LaunchAsync("bound", keepOpen: false, ct);
            var watch = Stopwatch.StartNew();
            bound.Server.Kill();
            await WorkerCommand.WaitForExitAsync(bound.Server, TimeSpan.FromSeconds(10));
            await RequireGoneAsync(bound.App, "An app launched without keepOpen must end with a killed server", 5000);
            return $"keepOpen: pid {kept.App} survived exit code {exit} and pid {orphan.App} survived a killed server; pid {bound.App} ended {watch.ElapsedMilliseconds} ms after its server was killed";
        }

        public ValueTask DisposeAsync()
        {
            foreach (var server in servers)
            {
                try { if (!server.HasExited) server.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                server.Dispose();
            }
            foreach (var app in apps) Kill(app);
            return ValueTask.CompletedTask;
        }
    }

    // ═══════════════ Apps named in words, and long runs ═══════════════
    /// <summary>
    /// A fresh server with no Customer Desk running: find_app resolves the sample by name, run_test starts it by name (and from the app a test
    /// stores), a long run is handed over after waitSeconds and awaited with get_run waitSeconds, and two instances make the name ambiguous.
    /// </summary>
    private sealed class AppNameScenario(string runRoot) : IAsyncDisposable
    {
        private const string Driver = "stdio";
        private readonly string workspace = Path.Combine(runRoot, "workspace-app-names");
        private Process? server;
        private McpLineClient? client;
        private Task<List<string>>? stderr;
        private Dictionary<string, JsonElement> schemas = [];
        private string sample = "";
        private int first, second;
        private McpLineClient Client => client ?? throw new InvalidOperationException("The app-name server is not running.");
        private async Task<McpToolCall> CallAsync(string tool, object? arguments = null, string? progressToken = null, int timeoutSeconds = 60) =>
            Conforming(schemas, tool, (await Client.CallToolAsync(tool, arguments, progressToken, TimeSpan.FromSeconds(timeoutSeconds))).RequireOk(tool));
        private static List<int> SampleInstances(string exe)
        {
            var found = new List<int>();
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
            {
                try { if (string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase) && !process.HasExited) found.Add(process.Id); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                finally { process.Dispose(); }
            }
            return found;
        }

        public IEnumerable<(string Name, string Driver, Func<CancellationToken, Task<string>> Run)> Checks()
        {
            yield return ("find_app resolves \"Customer Desk\" to the sample app while none runs", Driver, FindAsync);
            yield return ("run_test by app name with no pid launches the sample app, passes and closes it", Driver, RunByNameAsync);
            yield return ("create_test with app stores the sample app, and run_test with just the testId launches it and passes", Driver, StoredAppAsync);
            yield return ("run_test with waitSeconds 1 answers status running, and get_run with waitSeconds waits until the run passed", Driver, WaitSecondsAsync);
            yield return ("with two instances running, \"Customer Desk\" is ambiguous and returns isError with the candidates", Driver, AmbiguousAsync);
            yield return ("closing stdin exits the app-name server with code 0 and closes the apps it launched", Driver, ShutdownAsync);
        }

        private async Task<string> FindAsync(CancellationToken ct)
        {
            Directory.CreateDirectory(workspace);
            server = StartServer(workspace, Path.Combine(runRoot, "app-names-stderr.txt"), out stderr);
            client = new McpLineClient(server.StandardOutput.BaseStream, server.StandardInput.BaseStream);
            Result(await client.RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "testy-verify-mcp", ["version"] = McpServer.Version }
            }, TimeSpan.FromSeconds(45)), "initialize");
            await client.NotifyAsync("notifications/initialized");
            schemas = Schemas(Result(await client.RequestAsync("tools/list"), "tools/list"));
            var info = (await CallAsync("get_workspace_info")).Structured;
            sample = Path.GetFullPath(Text(info, "sampleApp") ?? throw new InvalidOperationException("get_workspace_info names no sample app."));
            var foreign = SampleInstances(sample);
            Require(foreign.Count == 0, $"A Customer Desk from {sample} is already running (pid {string.Join(", ", foreign)}); these checks need none. Close it and run again.");
            var found = (await CallAsync("find_app", new { query = "Customer Desk" }, timeoutSeconds: 90)).Structured;
            var best = found.GetProperty("best");
            Require(found.GetProperty("status").GetString() == "unique" && best.ValueKind == JsonValueKind.Object, "find_app did not resolve Customer Desk uniquely: " + found.GetRawText());
            Require(best.GetProperty("kind").GetString() == "sample" && best.GetProperty("pid").ValueKind == JsonValueKind.Null && best.GetProperty("confidence").GetDouble() == 1.0
                && string.Equals(best.GetProperty("exePath").GetString(), sample, StringComparison.OrdinalIgnoreCase) && best.GetProperty("name").GetString() == "Customer Desk",
                "find_app must name the sample app: " + best.GetRawText());
            return $"unique: {best.GetProperty("reason").GetString()} → {sample} ({found.GetProperty("total").GetInt32()} matching candidates)";
        }

        private async Task<string> RunByNameAsync(CancellationToken ct)
        {
            var testId = (await CallAsync("create_test", new { name = "MCP verify: by app name", intent = "Run against the app named in words.", steps = CustomerTestSteps("MCP Name", "name@example.test") })).Structured.GetProperty("testId").GetString()!;
            var watch = Stopwatch.StartNew();
            var run = (await CallAsync("run_test", new { testId, app = "Customer Desk", waitSeconds = 120 }, progressToken: "by-name", timeoutSeconds: 180)).Structured;
            Require(run.GetProperty("status").GetString() == "passed" && !run.GetProperty("running").GetBoolean(), "The run by app name did not pass: " + run.GetProperty("summary").GetString());
            var resolved = run.GetProperty("resolvedApp");
            var pid = resolved.GetProperty("pid").GetInt32();
            Require(resolved.GetProperty("launched").GetBoolean() && resolved.GetProperty("kind").GetString() == "sample" && string.Equals(resolved.GetProperty("exePath").GetString(), sample, StringComparison.OrdinalIgnoreCase)
                && run.GetProperty("target").GetProperty("pid").GetInt32() == pid, "resolvedApp must say that Testy launched the sample for the run: " + resolved.GetRawText());
            await RequireGoneAsync(pid, "The sample app launched for the run");
            return $"launched pid {pid} ({resolved.GetProperty("reason").GetString()}), run {run.GetProperty("runId").GetString()} passed in {watch.Elapsed.TotalSeconds:F1} s, app closed afterwards";
        }

        private async Task<string> StoredAppAsync(CancellationToken ct)
        {
            var created = (await CallAsync("create_test", new { name = "MCP verify: stored app", intent = "The test names its app.", app = "Customer Desk", steps = CustomerTestSteps("MCP Stored", "stored@example.test") }, timeoutSeconds: 90)).Structured;
            Require(string.Equals(created.GetProperty("targetPath").GetString(), sample, StringComparison.OrdinalIgnoreCase) && created.GetProperty("targetName").GetString() == "Customer Desk", "create_test with app must store the sample: " + created.GetRawText());
            var testId = created.GetProperty("testId").GetString()!;
            var saved = new WorkspaceStore(workspace).LoadTests().Single(t => t.Id == testId);
            Require(string.Equals(saved.TargetPath, sample, StringComparison.OrdinalIgnoreCase) && saved.TargetName == "Customer Desk", "The saved test file must carry the app for Studio.");
            var run = (await CallAsync("run_test", new { testId, waitSeconds = 120 }, timeoutSeconds: 180)).Structured;
            Require(run.GetProperty("status").GetString() == "passed", "The run of the stored app did not pass: " + run.GetProperty("summary").GetString());
            var resolved = run.GetProperty("resolvedApp");
            Require(resolved.GetProperty("kind").GetString() == "test" && resolved.GetProperty("launched").GetBoolean(), "resolvedApp must name the stored app as launched: " + resolved.GetRawText());
            await RequireGoneAsync(resolved.GetProperty("pid").GetInt32(), "The stored app launched for the run");
            return $"stored {saved.TargetName} ({saved.TargetPath}); run_test with just the testId launched pid {resolved.GetProperty("pid").GetInt32()} and passed";
        }

        private async Task<string> WaitSecondsAsync(CancellationToken ct)
        {
            first = (await CallAsync("launch_app", new { app = "Customer Desk", waitForWindowSeconds = 30 }, timeoutSeconds: 90)).Structured.GetProperty("pid").GetInt32();
            var slow = (await CallAsync("create_test", new
            {
                name = "MCP verify: longer than the wait", intent = "A run that outlives its call.",
                steps = new object[]
                {
                    new { action = "click", selector = "id:ResetButton" },
                    new { title = "Take longer than the call waits", action = "wait", value = "5000", timeoutMs = 10000 },
                    new { title = "Verify the ready text", action = "assertText", selector = "id:StatusMessage", value = "Ready for a new customer." }
                }
            })).Structured.GetProperty("testId").GetString()!;
            var watch = Stopwatch.StartNew();
            var handed = (await CallAsync("run_test", new { testId = slow, pid = first, waitSeconds = 1 }, progressToken: "slow", timeoutSeconds: 60)).Structured;
            var answered = watch.Elapsed;
            Require(handed.GetProperty("status").GetString() == "running" && handed.GetProperty("running").GetBoolean() && answered < TimeSpan.FromSeconds(10), $"waitSeconds 1 must answer status running at once; it took {answered.TotalSeconds:F1} s: " + handed.GetRawText());
            var message = handed.GetProperty("message").GetString()!;
            Require(message.Contains("get_run", StringComparison.Ordinal) && message.Contains("waitSeconds", StringComparison.Ordinal), "The answer must say to call get_run with waitSeconds: " + message);
            var runId = handed.GetProperty("runId").GetString()!;
            var done = (await CallAsync("get_run", new { runId, waitSeconds = 30 }, timeoutSeconds: 60)).Structured;
            Require(done.GetProperty("status").GetString() == "passed" && !done.GetProperty("running").GetBoolean() && done.GetProperty("completedSteps").GetInt32() == 3, "get_run with waitSeconds must return the passed run: " + done.GetRawText());
            Require(File.Exists(Path.Combine(workspace, "runs", runId + ".json")), "The handed-over run was not saved.");
            return $"run_test answered running after {answered.TotalSeconds:F1} s with {handed.GetProperty("completedSteps").GetInt32()} step(s) done; get_run waited until it passed at {watch.Elapsed.TotalSeconds:F1} s";
        }

        private async Task<string> AmbiguousAsync(CancellationToken ct)
        {
            second = (await CallAsync("launch_app", new { app = "Customer Desk", waitForWindowSeconds = 30 }, timeoutSeconds: 90)).Structured.GetProperty("pid").GetInt32();
            Require(second != first && Alive(first) && Alive(second), "launch_app with app must start a second instance.");
            var found = (await CallAsync("find_app", new { query = "Customer Desk" })).Structured;
            var pids = found.GetProperty("candidates").EnumerateArray().Where(c => c.GetProperty("pid").ValueKind == JsonValueKind.Number).Select(c => c.GetProperty("pid").GetInt32()).ToHashSet();
            Require(found.GetProperty("status").GetString() == "ambiguous" && pids.Contains(first) && pids.Contains(second), "find_app must report both instances as ambiguous: " + found.GetRawText());
            var inspect = await Client.CallToolAsync("inspect_app", new { app = "Customer Desk", includeScreenshot = false });
            Require(inspect.Error is null && inspect.IsError && inspect.Text.Contains(first.ToString(), StringComparison.Ordinal) && inspect.Text.Contains(second.ToString(), StringComparison.Ordinal) && inspect.Text.Contains("Candidates", StringComparison.Ordinal),
                "inspect_app with an ambiguous name must return isError with the candidates: " + inspect.Text);
            var testId = (await CallAsync("list_tests")).Structured.GetProperty("tests").EnumerateArray().First().GetProperty("testId").GetString()!;
            var run = await Client.CallToolAsync("run_test", new { testId, app = "Customer Desk" });
            Require(run.IsError && run.Text.Contains(second.ToString(), StringComparison.Ordinal), "run_test with an ambiguous name must return isError with the candidates: " + run.Text);
            var chosen = (await CallAsync("inspect_app", new { pid = second, includeScreenshot = false, selector = "id:StatusMessage" })).Structured;
            Require(chosen.GetProperty("pid").GetInt32() == second, "A pid from the candidates must work.");
            return $"two instances (pids {first}, {second}): find_app ambiguous, inspect_app and run_test answered isError with both candidates; pid {second} chosen from them works";
        }

        private async Task<string> ShutdownAsync(CancellationToken ct)
        {
            Client.CloseInput();
            var exit = await WaitForExitAsync(server!, TimeSpan.FromSeconds(15));
            Require(exit == 0, $"The server exited with code {exit}.");
            await RequireGoneAsync(first, "The first launched Customer Desk");
            await RequireGoneAsync(second, "The second launched Customer Desk");
            var lines = await stderr!.WaitAsync(TimeSpan.FromSeconds(5), ct);
            RequireNoJsonRpc(lines);
            return $"exit code {exit}; pids {first} and {second} closed";
        }

        public async ValueTask DisposeAsync()
        {
            if (client is not null) await client.DisposeAsync();
            if (server is not null)
            {
                if (!server.HasExited) { try { server.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
                server.Dispose();
            }
            Kill(first);
            Kill(second);
        }
    }
}
