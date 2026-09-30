using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Testy.Studio;

/// <summary>
/// How an agent on this PC starts Testy's MCP server (<c>Testy.Cli.exe mcp --workspace …</c>). The server is the companion CLI next to Studio in
/// the bundle; in the development tree it is the built Testy.Cli.exe, or <c>dotnet Testy.Cli.dll</c> when only the dll exists. Nothing here is
/// specific to any agent product: the command line and the <c>mcpServers</c> JSON are a common client configuration shape, not part of the protocol.
/// </summary>
internal sealed class McpServerCommand
{
    private static readonly JsonSerializerOptions JsonLayout = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    /// <summary>The environment variable the server reads its bearer token from, so the token never appears on a command line.</summary>
    public const string TokenVariable = "TESTY_MCP_TOKEN";

    private McpServerCommand(string fileName, IReadOnlyList<string> leadingArguments, string workspace, string serverPath, bool isBundled)
    {
        FileName = fileName; LeadingArguments = leadingArguments; Workspace = workspace; ServerPath = serverPath; IsBundled = isBundled;
    }

    /// <summary>The program an agent starts: Testy.Cli.exe, or the dotnet host when only Testy.Cli.dll is built.</summary>
    public string FileName { get; }
    /// <summary>Empty for the exe; the dll path for the dotnet host form.</summary>
    public IReadOnlyList<string> LeadingArguments { get; }
    public string Workspace { get; }
    /// <summary>Testy.Cli.exe or Testy.Cli.dll, for messages.</summary>
    public string ServerPath { get; }
    /// <summary>Found next to Testy.Studio.exe (the portable bundle) rather than in the development tree.</summary>
    public bool IsBundled { get; }
    /// <summary>The DOTNET_ROOT the development apphost needs and an agent's environment may lack; null when nothing needs to be set.</summary>
    public string? RequiredRuntimeRoot => !IsBundled && LeadingArguments.Count == 0 && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ROOT")) ? RuntimeRoot() : null;

    public IReadOnlyList<string> Arguments(params string[] extra) => [.. LeadingArguments, "mcp", "--workspace", Workspace, .. extra];
    /// <summary>"C:\…\Testy.Cli.exe" mcp --workspace "C:\…\Workspace": the arguments quoted for the Windows command-line rules, as an agent's settings expect them.</summary>
    public string CommandLine => string.Join(" ", new[] { Quote(FileName) }.Concat(LeadingArguments.Select(Quote)).Concat(["mcp", "--workspace", Quote(Workspace)]));
    /// <summary>A common MCP client configuration shape: {"mcpServers":{"testy":{"command":…,"args":[…]}}}, with the runtime variable when the development build needs one.</summary>
    public string ClientConfigurationJson
    {
        get
        {
            var testy = new JsonObject { ["command"] = FileName, ["args"] = new JsonArray(Arguments().Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()) };
            if (RequiredRuntimeRoot is { } root) testy["env"] = new JsonObject { ["DOTNET_ROOT"] = root };
            return new JsonObject { ["mcpServers"] = new JsonObject { ["testy"] = testy } }.ToJsonString(JsonLayout);
        }
    }
    /// <summary>The same shape for a server that is already listening over HTTP, with the bearer token as a header.</summary>
    public static string HttpConfigurationJson(string url, string? token)
    {
        var testy = new JsonObject { ["type"] = "http", ["url"] = url };
        if (!string.IsNullOrEmpty(token)) testy["headers"] = new JsonObject { ["Authorization"] = "Bearer " + token };
        return new JsonObject { ["mcpServers"] = new JsonObject { ["testy"] = testy } }.ToJsonString(JsonLayout);
    }

    /// <summary>A hidden child process with redirected UTF-8 pipes; stdout is the protocol channel, stderr carries the server's log lines.</summary>
    public ProcessStartInfo Start(params string[] extra)
    {
        var utf8 = new UTF8Encoding(false);
        var info = new ProcessStartInfo(FileName)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = utf8, StandardErrorEncoding = utf8,
            WorkingDirectory = Path.GetDirectoryName(ServerPath) ?? Environment.CurrentDirectory
        };
        foreach (var argument in Arguments(extra)) info.ArgumentList.Add(argument);
        // The development apphost looks for the runtime through DOTNET_ROOT; point it at the runtime Studio itself runs on when nothing else does.
        if (RequiredRuntimeRoot is { } root) info.Environment["DOTNET_ROOT"] = root;
        return info;
    }

    /// <summary>The same lookup as <see cref="StudioCommandClient.ResolveExecutable"/>, plus the dll form for a development tree without the apphost. Null when nothing is built.</summary>
    public static McpServerCommand? Resolve(string workspace)
    {
        // A trailing separator would end the quoted argument with \" and break it; a drive root keeps its backslash, which Quote doubles.
        workspace = Path.GetFullPath(workspace);
        if (!IsDriveRoot(workspace)) workspace = Path.TrimEndingDirectorySeparator(workspace);
        var bundled = Path.Combine(AppContext.BaseDirectory, "Testy.Cli.exe");
        if (File.Exists(bundled)) return new McpServerCommand(bundled, [], workspace, bundled, isBundled: true);
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "Testy.sln"))) continue;
            var output = Path.Combine(directory.FullName, "src", "Testy.Cli", "bin", "Release", "net9.0-windows");
            var exe = Path.Combine(output, "Testy.Cli.exe");
            if (File.Exists(exe)) return new McpServerCommand(exe, [], workspace, exe, isBundled: false);
            var dll = Path.Combine(output, "Testy.Cli.dll");
            if (File.Exists(dll)) return new McpServerCommand(DotNetHost(), [dll], workspace, dll, isBundled: false);
        }
        return null;
    }
    private static bool IsDriveRoot(string path) => path.Length == 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == Path.DirectorySeparatorChar;

    /// <summary>dotnet.exe from DOTNET_ROOT, else the host Studio runs on, else "dotnet" from PATH.</summary>
    private static string DotNetHost()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(configured) && File.Exists(Path.Combine(configured, "dotnet.exe"))) return Path.Combine(configured, "dotnet.exe");
        return RuntimeRoot() is { } root ? Path.Combine(root, "dotnet.exe") : "dotnet";
    }
    /// <summary>The dotnet root of the shared runtime this process runs on (…\dotnet\shared\Microsoft.NETCore.App\9.0.x → …\dotnet); null when self-contained.</summary>
    private static string? RuntimeRoot()
    {
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (string.IsNullOrEmpty(runtime)) return null;
        var root = Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
        return File.Exists(Path.Combine(root, "dotnet.exe")) ? root : null;
    }
    /// <summary>Windows command-line quoting: a quote is escaped, and backslashes before a quote (including the closing one) are doubled.</summary>
    internal static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            if (ch == '"') result.Append('\\', slashes * 2 + 1).Append('"');
            else result.Append('\\', slashes).Append(ch);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    /// <summary>A fresh bearer token: 32 random bytes as URL-safe text.</summary>
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// A small MCP client over a server child's stdio (newline-delimited JSON-RPC), enough for Settings › Test the connection (initialize, tools/list)
/// and the screenshot aid (a tools/call). Responses are matched by id; notifications are ignored. Disposing closes stdin, the server's shutdown signal.
/// </summary>
internal sealed class McpStdioSession : IAsyncDisposable
{
    private readonly Process process;
    private readonly Stream input;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private readonly ConcurrentQueue<string> errors = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private int nextId;

    private McpStdioSession(Process process)
    {
        this.process = process;
        input = process.StandardInput.BaseStream;
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(async () =>
        {
            try { while (await process.StandardError.ReadLineAsync() is { } line) { errors.Enqueue(line); while (errors.Count > 12) errors.TryDequeue(out _); } }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        });
    }

    public static McpStdioSession Start(McpServerCommand command)
    {
        var info = command.Start();
        info.RedirectStandardInput = true;
        return new McpStdioSession(Process.Start(info) ?? throw new InvalidOperationException("The MCP server process did not start."));
    }

    public int ProcessId => process.Id;
    /// <summary>The server's latest log lines, for error messages.</summary>
    public string ErrorTail => errors.IsEmpty ? "" : " Server log: " + string.Join(" | ", errors.Select(TrimLogPrefix));
    /// <summary>"[testy-mcp] 2026-09-29 10:00:00.000 info message" → "info message".</summary>
    public static string TrimLogPrefix(string line)
    {
        const string prefix = "[testy-mcp] ";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return line;
        var rest = line[prefix.Length..];
        // Skip the date and time tokens.
        var parts = rest.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 ? parts[2] : rest;
    }
    /// <summary>The server's last words: a dying child writes its reason to stderr a moment before its pipe ends.</summary>
    private async Task<string> LastWordsAsync()
    {
        await Task.Delay(150);
        var exit = ""; try { if (process.HasExited) exit = $" (exit code {process.ExitCode})"; } catch (InvalidOperationException) { }
        return exit + ErrorTail;
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                JsonElement element;
                try { using var document = JsonDocument.Parse(line); element = document.RootElement.Clone(); }
                catch (JsonException) { continue; }
                if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var number) && pending.TryRemove(number, out var waiter))
                    waiter.TrySetResult(element);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        var reason = await LastWordsAsync();
        foreach (var waiter in pending.Values) waiter.TrySetException(new IOException("The server closed its output before answering." + reason));
    }

    public async Task<JsonElement> RequestAsync(string method, JsonObject? parameters, TimeSpan timeout)
    {
        var id = Interlocked.Increment(ref nextId);
        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = waiter;
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null) message["params"] = parameters;
        await SendAsync(message);
        JsonElement response;
        try { response = await waiter.Task.WaitAsync(timeout); }
        catch (TimeoutException) { pending.TryRemove(id, out _); throw new TimeoutException($"No answer to {method} within {timeout.TotalSeconds:F0} s." + ErrorTail); }
        if (response.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"{method} was refused: {(error.TryGetProperty("message", out var text) ? text.GetString() : error.GetRawText())}");
        return response.TryGetProperty("result", out var result) ? result : throw new InvalidOperationException($"{method} answered without a result.");
    }
    public Task NotifyAsync(string method) => SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method });

    /// <summary>The MCP handshake: initialize (protocol 2025-06-18) and notifications/initialized. Returns the initialize result.</summary>
    public async Task<JsonElement> InitializeAsync(TimeSpan timeout)
    {
        var version = typeof(McpStdioSession).Assembly.GetName().Version?.ToString(3) ?? "0";
        var result = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "testy-studio", ["version"] = version }
        }, timeout);
        await NotifyAsync("notifications/initialized");
        return result;
    }

    /// <summary>tools/call; a tool result with isError is thrown as an error with the tool's own text.</summary>
    public async Task<JsonElement> CallToolAsync(string name, JsonObject arguments, TimeSpan timeout)
    {
        var result = await RequestAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments }, timeout);
        if (result.TryGetProperty("isError", out var flag) && flag.ValueKind == JsonValueKind.True)
        {
            var text = result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
                ? string.Join(" ", content.EnumerateArray().Where(c => c.TryGetProperty("type", out var t) && t.GetString() == "text").Select(c => c.GetProperty("text").GetString()))
                : result.GetRawText();
            throw new InvalidOperationException($"{name} could not do it: {text}");
        }
        return result;
    }

    private async Task SendAsync(JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        await writeGate.WaitAsync();
        try { await input.WriteAsync(bytes); await input.FlushAsync(); }
        catch (IOException ex) { throw new IOException("The server did not accept the request: " + ex.Message + await LastWordsAsync(), ex); }
        finally { writeGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        try { input.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        if (!await WaitForExitAsync(process, TimeSpan.FromSeconds(5))) KillQuietly(process);
        process.Dispose();
    }

    internal static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        try { await process.WaitForExitAsync().WaitAsync(timeout); return true; }
        catch (TimeoutException) { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }
    /// <summary>Ends the server process itself. Apps it launched are bound to its job object unless they were kept open on purpose, so they are not killed here.</summary>
    internal static void KillQuietly(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: false); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }
}

/// <summary>What the server reported when it started listening: its address and whether it requires the bearer token.</summary>
internal sealed record McpListenerStartup(string Url, int Port, bool TokenRequired);

/// <summary>
/// Settings › Listen for agents (HTTP): the MCP server as a Studio child (<c>mcp --transport http --port N</c>, the token in its environment). Its one
/// stdout startup line gives the loopback URL; stderr log lines are handed to the window at a low rate; closing its stdin stops it (then a kill after a grace period).
/// </summary>
internal sealed class McpListener
{
    private readonly Process process;
    private volatile bool stopping;

    private McpListener(Process process, McpListenerStartup startup) { this.process = process; Startup = startup; ProcessId = process.Id; }

    public McpListenerStartup Startup { get; }
    public string Url => Startup.Url;
    public int ProcessId { get; }
    /// <summary>The server ended on its own (not through <see cref="StopAsync"/>): the exit code, from a thread-pool thread.</summary>
    public event Action<int>? Exited;

    /// <summary>Starts the server on the given loopback port (0 picks a free one). The token, when given, is passed through the environment and must be reported as required.</summary>
    public static async Task<McpListener> StartAsync(McpServerCommand command, Action<string> onServerLine, TimeSpan startupTimeout, int port = 0, string? token = null)
    {
        var info = command.Start("--transport", "http", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.RedirectStandardInput = true;
        if (!string.IsNullOrEmpty(token)) info.Environment[McpServerCommand.TokenVariable] = token; else info.Environment.Remove(McpServerCommand.TokenVariable);
        var process = Process.Start(info) ?? throw new InvalidOperationException("The MCP server process did not start.");
        var errors = new ConcurrentQueue<string>();
        _ = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                {
                    errors.Enqueue(line); while (errors.Count > 12) errors.TryDequeue(out _);
                    onServerLine(line);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        });
        string ServerLog() => errors.IsEmpty ? "" : " Server log: " + string.Join(" | ", errors.Select(McpStdioSession.TrimLogPrefix));
        try
        {
            var startup = ReadStartupLineAsync(process.StandardOutput);
            if (await Task.WhenAny(startup, Task.Delay(startupTimeout)) != startup)
            {
                _ = startup.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously); // observed: the process is killed below
                await Task.Delay(150);
                throw new TimeoutException($"The server did not report its address within {startupTimeout.TotalSeconds:F0} s." + ServerLog());
            }
            McpListenerStartup reported;
            try { reported = await startup; }
            catch (IOException ex)
            {
                await Task.Delay(150); // let the stderr pump catch the server's last words
                var exit = process.HasExited ? $" (exit code {process.ExitCode})" : "";
                throw new IOException(ex.Message + exit + ServerLog(), ex);
            }
            if (!string.IsNullOrEmpty(token) && !reported.TokenRequired) throw new InvalidOperationException("The server started without requiring the token, so it was stopped again.");
            var listener = new McpListener(process, reported);
            _ = Task.Run(async () => { try { while (await process.StandardOutput.ReadLineAsync() is not null) { } } catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { } });
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                if (listener.stopping) return;
                int code; try { code = process.ExitCode; } catch (InvalidOperationException) { code = -1; }
                listener.Exited?.Invoke(code);
                process.Dispose();
            };
            return listener;
        }
        catch
        {
            McpStdioSession.KillQuietly(process);
            process.Dispose();
            throw;
        }
    }

    private static async Task<McpListenerStartup> ReadStartupLineAsync(StreamReader stdout)
    {
        while (await stdout.ReadLineAsync() is { } line)
        {
            if (!line.TrimStart().StartsWith('{')) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("url", out var url) && url.GetString() is { Length: > 0 } text)
                    return new McpListenerStartup(text, root.TryGetProperty("port", out var port) && port.TryGetInt32(out var number) ? number : 0, root.TryGetProperty("tokenRequired", out var required) && required.ValueKind == JsonValueKind.True);
            }
            catch (JsonException) { }
        }
        throw new IOException("The server exited before it reported its address.");
    }

    public bool HasExited { get { try { return process.HasExited; } catch (InvalidOperationException) { return true; } } }

    /// <summary>Closes stdin (the server's shutdown signal) and waits for its orderly stop: it gives outstanding requests, background runs and launched apps a bounded time. Then kills what is left.</summary>
    public async Task StopAsync(TimeSpan grace)
    {
        stopping = true;
        CloseInput();
        if (!await McpStdioSession.WaitForExitAsync(process, grace)) { McpStdioSession.KillQuietly(process); await McpStdioSession.WaitForExitAsync(process, TimeSpan.FromSeconds(3)); }
        process.Dispose();
    }
    /// <summary>The same, synchronously, for the window's Closing handler.</summary>
    public void StopBlocking(TimeSpan grace)
    {
        stopping = true;
        CloseInput();
        try { if (!process.WaitForExit((int)grace.TotalMilliseconds)) { McpStdioSession.KillQuietly(process); process.WaitForExit(2000); } }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
        process.Dispose();
    }
    private void CloseInput() { try { process.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { } }
}
