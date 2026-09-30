using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Testy.Core;

namespace Testy.Cli.HyperV;

/// <summary>Runs the shipped Hyper-V bridge in Windows PowerShell 5.1 (PowerShell Direct is only reachable from PowerShell).
/// Secrets travel on standard input only; the request file never contains them.</summary>
internal static class HyperVBridge
{
    internal static string ScriptsDirectory => Path.Combine(AppContext.BaseDirectory, "hyperv");
    internal static string PowerShellPath => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    internal static async Task<JsonElement> InvokeAsync(string operation, object request, IReadOnlyList<string?> secrets, string workDirectory, TimeSpan timeout,
        CancellationToken ct, Action<string, string>? progress = null)
    {
        var script = Path.Combine(ScriptsDirectory, "Testy.HyperV.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("The Hyper-V bridge script is missing from this Testy build.", script);
        if (!File.Exists(PowerShellPath)) throw new FileNotFoundException("Windows PowerShell 5.1 is required for Hyper-V operations.", PowerShellPath);
        Directory.CreateDirectory(workDirectory);
        string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        string requestPath = Path.Combine(workDirectory, $"{operation.ToLowerInvariant()}-{stamp}-request.json");
        string resultPath = Path.Combine(workDirectory, $"{operation.ToLowerInvariant()}-{stamp}-result.json");
        string cancelPath = Path.Combine(workDirectory, $"{operation.ToLowerInvariant()}-{stamp}-cancel.request");
        string logPath = Path.Combine(workDirectory, $"{operation.ToLowerInvariant()}-{stamp}-progress.log");
        WorkerCommand.DurableWrite(requestPath, request);
        var start = new ProcessStartInfo(PowerShellPath)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false), WorkingDirectory = ScriptsDirectory
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Operation", operation, "-RequestFile", requestPath, "-ResultFile", resultPath, "-CancelFile", cancelPath })
            start.ArgumentList.Add(argument);
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)).ToList()) start.Environment.Remove(name);
        CortexModelBridge.BindChild(start); // Host process only: credentials never enter Invoke-Guest or request files.
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows PowerShell did not start.");
        // Base64 keeps any password character intact across the console code page; lines are written once and the pipe closed.
        foreach (var secret in secrets) await process.StandardInput.WriteLineAsync(secret is null ? "" : Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)));
        process.StandardInput.Close();
        var stderr = new StringBuilder();
        var stderrTask = Task.Run(async () => { var buffer = new char[4096]; int n; while ((n = await process.StandardError.ReadAsync(buffer)) > 0) if (stderr.Length < 64 * 1024) stderr.Append(buffer, 0, n); });
        var stdoutTask = Task.Run(async () =>
        {
            await using var log = new StreamWriter(new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
            {
                if (line.Length > 8192) line = line[..8192];
                await log.WriteLineAsync(line); await log.FlushAsync();
                if (!line.StartsWith('{')) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("stage", out var stage) && doc.RootElement.TryGetProperty("message", out var message))
                        progress?.Invoke(stage.GetString() ?? "", message.GetString() ?? "");
                }
                catch (JsonException) { }
            }
        });
        var exited = process.WaitForExitAsync();
        var deadline = Task.Delay(timeout);
        var cancelled = Task.Delay(Timeout.Infinite, ct);
        var first = await Task.WhenAny(exited, deadline, cancelled);
        if (first != exited)
        {
            // Cooperative first: the bridge forwards cancellation to the guest worker and still cleans up the run task and key.
            await File.WriteAllTextAsync(cancelPath, "{\"cancel\":true}", CancellationToken.None);
            if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromMinutes(6))) != exited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await exited.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(10));
        if (!File.Exists(resultPath))
            throw new InvalidOperationException($"The Hyper-V bridge ended (exit {process.ExitCode}) without a result. " + Tail(stderr.ToString()));
        // A cancelled bridge still returns its result (including guest cleanup); callers decide from ct and the result's status.
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath, CancellationToken.None));
        return result.RootElement.Clone();
    }

    internal static string Tail(string text) { text = text.ReplaceLineEndings(" ").Trim(); return text.Length > 600 ? text[^600..] : text; }
    internal static string? String(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal static bool Bool(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
