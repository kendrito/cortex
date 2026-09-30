using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Testy.Core;

namespace Testy.Cli.Mcp;

/// <summary>The options of the `mcp` verb. It has its own parser: options of other verbs are refused and --allow-exe/--allow-target repeat.</summary>
internal sealed class McpOptions
{
    public const string TokenVariable = "TESTY_MCP_TOKEN";
    public const string Allowed = "--workspace, --transport, --port, --token, --parent-pid, --no-launch, --allow-exe, --allow-target, --describe, --format, --relative, --help";
    public const string Usage = """
        Testy MCP server (Model Context Protocol)
          Testy.Cli.exe mcp [--workspace DIRECTORY]                                 stdio (default)
          Testy.Cli.exe mcp --transport http [--port N] [--token TOKEN]              Streamable HTTP on loopback
          Testy.Cli.exe mcp --describe [--format manifest|server-json] [--relative]  print the discovery manifest and exit

        Options
          --workspace DIRECTORY   Testy workspace to read and write (default %LOCALAPPDATA%\Testy\Workspace).
          --transport stdio|http  stdio: newline-delimited JSON-RPC 2.0 on stdin/stdout, logs on stderr.
                                  http: binds 127.0.0.1 and ::1 only and prints one JSON line with its url when ready.
          --port N                http: loopback port; 0 (default) picks a free one.
          --token TOKEN           http: require Authorization: Bearer TOKEN. Prefer the environment variable
                                  TESTY_MCP_TOKEN, which other programs cannot read from the command line.
          --parent-pid N          Exit when process N exits (for a host that cannot close this server's stdin).
          --no-launch             launch_app and run_test (exe) start nothing.
          --allow-exe PATH        Start only this program, or programs in this folder (repeatable); also
                                  the only way to allow a program that is part of Windows itself.
          --allow-target NAME     Connect only to this process name or exe path (repeatable).
          --describe              Print how to start and reach this server, and what it offers, as JSON.
          --format manifest|server-json   With --describe: the manifest (default) or the registry-shaped identity.
          --relative              With --describe: commands relative to the program folder.

        The server can start desktop (GUI) programs the caller names and send keyboard and mouse input to the
        application the caller names by pid, with the rights of the user who runs it. See docs/MCP.md.
        """;

    public string Workspace { get; private set; } = AgentFiles.DefaultWorkspace;
    public string Transport { get; private set; } = "stdio";
    public int Port { get; private set; }
    public string? Token { get; private set; }
    public int? ParentPid { get; private set; }
    public bool Describe { get; private set; }
    public string Format { get; private set; } = "manifest";
    public bool Relative { get; private set; }
    public bool Help { get; private set; }
    public bool NoLaunch { get; private set; }
    public List<string> AllowedExecutables { get; } = [];
    public List<string> AllowedTargets { get; } = [];
    public McpLaunchPolicy Policy() => new(!NoLaunch, AllowedExecutables, AllowedTargets);

    /// <summary>Parses the arguments after the verb. <paramref name="environmentToken"/> is TESTY_MCP_TOKEN; it applies to the HTTP transport only.</summary>
    public static McpOptions Parse(string[] args, string? environmentToken)
    {
        var options = new McpOptions();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? explicitToken = null;
        var portGiven = false;
        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            if (name is "--help" or "-h" or "help") { options.Help = true; continue; }
            if (!name.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unexpected argument: {name}. Allowed options: {Allowed}.");
            if (name is not ("--workspace" or "--transport" or "--port" or "--token" or "--parent-pid" or "--no-launch" or "--allow-exe" or "--allow-target" or "--describe" or "--format" or "--relative"))
                throw new ArgumentException($"Unknown option {name} for mcp. Allowed: {Allowed}.");
            if (name is not ("--allow-exe" or "--allow-target") && !seen.Add(name)) throw new ArgumentException($"Duplicate option {name}.");
            switch (name)
            {
                case "--describe": options.Describe = true; continue;
                case "--relative": options.Relative = true; continue;
                case "--no-launch": options.NoLaunch = true; continue;
            }
            if (++index == args.Length || args[index].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"{name} requires a value.");
            var value = args[index];
            switch (name)
            {
                case "--workspace": options.Workspace = value; break;
                case "--transport":
                    if (value is not ("stdio" or "http")) throw new ArgumentException("--transport must be stdio or http.");
                    options.Transport = value;
                    break;
                case "--port":
                    if (!int.TryParse(value, out var port) || port is < 0 or > 65535) throw new ArgumentException("--port must be 0 (pick a free port) or 1–65535.");
                    options.Port = port; portGiven = true;
                    break;
                case "--token":
                    if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("--token cannot be blank.");
                    explicitToken = value;
                    break;
                case "--parent-pid":
                    if (!int.TryParse(value, out var parent) || parent <= 0) throw new ArgumentException("--parent-pid must be a process id.");
                    options.ParentPid = parent;
                    break;
                case "--format":
                    if (value is not ("manifest" or "server-json")) throw new ArgumentException("--format must be manifest or server-json.");
                    options.Format = value;
                    break;
                case "--allow-exe": options.AllowedExecutables.Add(value); break;
                case "--allow-target": options.AllowedTargets.Add(value); break;
            }
        }
        if (options.Workspace.Length == 0) throw new ArgumentException("--workspace requires a directory.");
        try { options.Workspace = Path.GetFullPath(options.Workspace); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { throw new ArgumentException("--workspace is not a valid path: " + ex.Message); }
        if (options.Transport == "stdio" && (portGiven || explicitToken is not null)) throw new ArgumentException("--port and --token apply to --transport http.");
        // --token wins over the environment; stdio ignores the variable, so one environment serves both transports.
        if (options.Transport == "http") options.Token = explicitToken ?? (string.IsNullOrWhiteSpace(environmentToken) ? null : environmentToken.Trim());
        return options;
    }
}

/// <summary>The `mcp` verb. stdout carries protocol messages only (or the --describe/--help text); every error of this verb goes to stderr.</summary>
internal static class McpCommand
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private enum InputKind { None, Pipe, Console }

    public static async Task<int> MainAsync(string[] args, CancellationToken cancellation)
    {
        // stdout is the protocol channel: capture it, then send Console.Out (and any stray write in shared code) to stderr.
        var stdout = Console.OpenStandardOutput();
        var stderr = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(stderr);
        try
        {
            var options = McpOptions.Parse(args, Environment.GetEnvironmentVariable(McpOptions.TokenVariable));
            if (options.Help) { await WriteTextAsync(stdout, McpOptions.Usage + Environment.NewLine, cancellation); return 0; }
            return await RunAsync(options, stdout, stderr, cancellation);
        }
        catch (OperationCanceledException) { stderr.WriteLine(JsonSerializer.Serialize(new { error = "Cancelled", status = "cancelled" }, TestyJson.Options)); return 1; }
        catch (Exception ex) { stderr.WriteLine(JsonSerializer.Serialize(new { error = ex.Message, type = ex.GetType().Name }, TestyJson.Options)); return 2; }
    }
    // Raw UTF-8 without a BOM, independent of the console code page, so a script can capture it into a file verbatim.
    private static Task WriteTextAsync(Stream stdout, string text, CancellationToken cancellation) => stdout.WriteAsync(new UTF8Encoding(false).GetBytes(text), cancellation).AsTask();

    private static async Task<int> RunAsync(McpOptions options, Stream stdout, StreamWriter stderr, CancellationToken cancellation)
    {
        var policy = options.Policy();
        if (options.Describe)
        {
            var catalog = new McpToolCatalog(null);
            var document = options.Format == "manifest" ? McpDescribe.Manifest(catalog, options.Workspace, TestyMcpService.FindSampleApp(), options.Relative, policy) : McpDescribe.ServerJson(catalog, options.Relative);
            await WriteTextAsync(stdout, document.ToJsonString(Pretty) + Environment.NewLine, cancellation);
            return 0;
        }
        var log = new McpLog(stderr, TestyMcpService.LogDirectory(options.Workspace));
        log.Info($"Testy MCP server {McpServer.Version} starting: transport {options.Transport}, workspace {options.Workspace}, protocol versions {string.Join(", ", McpServer.SupportedVersions)}.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var parent = WatchParent(options.ParentPid, lifetime, log);
        await using var service = new TestyMcpService(options.Workspace, log, policy: policy);
        service.RemoveExpiredEvidence();
        var server = new McpServer(service, log);
        if (options.Transport == "stdio")
        {
            await McpStdioTransport.RunAsync(server, Console.OpenStandardInput(), stdout, log, lifetime.Token);
        }
        else
        {
            using var http = new McpHttpTransport(server, log, options.Token);
            http.Start(options.Port);
            var startedAt = DateTimeOffset.UtcNow;
            var startup = new JsonObject
            {
                ["transport"] = "http", ["url"] = http.Url, ["port"] = http.Port, ["endpoint"] = "/mcp", ["bind"] = JsonRpc.Strings(http.Hosts),
                ["tokenRequired"] = options.Token is not null, ["pid"] = Environment.ProcessId, ["sessions"] = "Mcp-Session-Id for handshake revisions (initialize); stateless for 2026-07-28",
                ["protocolVersions"] = JsonRpc.Strings(McpServer.SupportedVersions), ["serverInfo"] = McpServer.ServerInfo()
            };
            var endpointFile = WriteEndpointFile(options.Workspace, http, options.Token is not null, startedAt, log);
            try
            {
                var line = Encoding.UTF8.GetBytes(JsonRpc.Serialize(startup) + "\n");
                await stdout.WriteAsync(line, cancellation);
                await stdout.FlushAsync(cancellation);
                var input = StandardInputKind();
                log.Info($"HTTP transport listening at {http.Url}{(options.Token is null ? " (no bearer token: any local process can connect)" : " (bearer token required)")}; " +
                    (input == InputKind.None ? "no stdin to watch, so it runs until it is terminated" : "running until stdin closes") + (options.ParentPid is { } pid ? $", process {pid} exits" : "") + " or Ctrl+C.");
                var serving = http.RunAsync(lifetime.Token);
                await WaitForStopAsync(input, log, lifetime.Token);
                await http.StopAsync();
                await serving;
            }
            finally { RemoveEndpointFile(endpointFile); }
        }
        await service.ShutdownAsync();
        log.Info("Testy MCP server stopped.");
        return 0;
    }

    /// <summary>--parent-pid: the server ends when that process does, which bounds its life when the host gave it no stdin to close.</summary>
    private static IDisposable? WatchParent(int? pid, CancellationTokenSource lifetime, McpLog log)
    {
        if (pid is not { } id) return null;
        Process process;
        try { process = Process.GetProcessById(id); }
        catch (ArgumentException) { throw new ArgumentException($"--parent-pid {id}: no such process is running."); }
        _ = Task.Run(async () =>
        {
            try { await process.WaitForExitAsync(lifetime.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { log.Warn($"Process {id} can no longer be watched: {ex.Message}"); }
            log.Info($"Process {id} (--parent-pid) exited; shutting down.");
            try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
        });
        return process;
    }

    /// <summary>
    /// A pipe or a console ends the HTTP server at end of input. The NUL device (what "ignore stdin" means in most process libraries), a file
    /// or no handle at all is "no stdin": reading it would report end of input at once, so the server runs until it is terminated instead.
    /// </summary>
    private static InputKind StandardInputKind()
    {
        const int StandardInput = -10, Disk = 1, Character = 2, Pipe = 3;
        var handle = GetStdHandle(StandardInput);
        if (handle == 0 || handle == -1) return InputKind.None;
        return (GetFileType(handle) & 0x7FFF) switch
        {
            Pipe => InputKind.Pipe,
            Character => GetConsoleMode(handle, out _) ? InputKind.Console : InputKind.None,
            Disk => InputKind.None,
            _ => InputKind.None
        };
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GetStdHandle(int handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int GetFileType(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(nint handle, out uint mode);

    /// <summary>HTTP mode lifetime: end of stdin (the harness closed its pipe), the parent process, or cancellation.</summary>
    private static async Task WaitForStopAsync(InputKind kind, McpLog log, CancellationToken cancellation)
    {
        if (kind == InputKind.None)
        {
            try { await Task.Delay(Timeout.Infinite, cancellation); } catch (OperationCanceledException) { log.Info("Shutdown requested; stopping the HTTP transport."); }
            return;
        }
        var input = Console.OpenStandardInput();
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var read = input.ReadAsync(buffer, cancellation).AsTask();
                var finished = await Task.WhenAny(read, Task.Delay(Timeout.Infinite, cancellation));
                if (finished != read) { cancellation.ThrowIfCancellationRequested(); }
                if (await read == 0) { log.Info("stdin closed; shutting down the HTTP transport."); return; }
            }
        }
        catch (OperationCanceledException) { log.Info("Shutdown requested; stopping the HTTP transport."); }
        catch (IOException ex) { log.Info("stdin ended (" + ex.Message + "); shutting down the HTTP transport."); }
    }

    /// <summary>
    /// &lt;workspace&gt;\mcp\endpoint.json names the HTTP endpoint while this server listens, so a harness can find the current address without
    /// parsing stdout. It never contains the token. The file is removed when the server stops; its pid tells a reader whether it is stale.
    /// </summary>
    internal static string? WriteEndpointFile(string workspace, McpHttpTransport http, bool tokenRequired, DateTimeOffset startedAt, McpLog log)
    {
        var path = EndpointFile(workspace);
        try
        {
            WorkspaceStore.WriteAtomic(path, new
            {
                url = http.Url, port = http.Port, tokenRequired, pid = Environment.ProcessId, startedAt,
                protocolVersions = McpServer.SupportedVersions, serverInfo = new { name = McpServer.Name, title = McpServer.Title, version = McpServer.Version }
            });
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Warn("The endpoint file could not be written: " + ex.Message); return null; }
    }
    public static string EndpointFile(string workspace) => Path.Combine(Path.GetFullPath(workspace), "mcp", "endpoint.json");
    internal static void RemoveEndpointFile(string? path)
    {
        if (path is null) return;
        try
        {
            if (!File.Exists(path)) return;
            // Another server on the same workspace may have replaced the file meanwhile; remove only this server's own.
            using (var document = JsonDocument.Parse(File.ReadAllText(path)))
                if (!document.RootElement.TryGetProperty("pid", out var pid) || !pid.TryGetInt32(out var owner) || owner != Environment.ProcessId) return;
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }
}
