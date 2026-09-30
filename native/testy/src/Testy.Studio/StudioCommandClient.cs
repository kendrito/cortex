using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Testy.Core;

namespace Testy.Studio;

internal sealed record StudioCommandResult(int ExitCode, JsonElement Json, string OutputPath);

internal static class StudioCommandClient
{
    internal static string ResolveExecutable()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Testy.Cli.exe");
        if (File.Exists(bundled)) return bundled;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "Testy.sln"))) continue;
            var built = Path.Combine(directory.FullName, "src", "Testy.Cli", "bin", "Release", "net9.0-windows", "Testy.Cli.exe");
            if (File.Exists(built)) return built;
        }
        throw new FileNotFoundException("The companion Testy.Cli.exe was not found. Keep the installed application files together.");
    }

    internal static async Task<StudioCommandResult> RunAsync(string workspace, IEnumerable<string> arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var directory = Path.Combine(workspace, "operations", "studio", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var stop = Path.Combine(directory, "cancel.request");
        var info = new ProcessStartInfo(ResolveExecutable())
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workspace
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.ArgumentList.Add("--cancel-file"); info.ArgumentList.Add(stop);
        CortexModelBridge.BindChild(info);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the local Testy runner.");
        var output = ReadBoundedAsync(process.StandardOutput);
        var errors = ReadBoundedAsync(process.StandardError);
        using var registration = ct.Register(() => { try { File.WriteAllText(stop, "Stop requested by Studio."); } catch (IOException) { } });
        var forced = false;
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException) { forced = true; if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
        var stdout = await output;
        var stderr = await errors;
        var outputPath = Path.Combine(directory, "console.json");
        await File.WriteAllTextAsync(outputPath, stdout.Text);
        await File.WriteAllTextAsync(Path.Combine(directory, "stderr.txt"), stderr.Text);
        if (forced) throw new IOException("The runner exceeded the stop grace period and was terminated. Its interrupted actions remain uncertain; inspect the saved lifecycle/job evidence before a fresh run. " + directory);
        if (stdout.Overflow || stderr.Overflow) throw new IOException("Runner output exceeded the 32 MiB display limit. The run was not accepted by Studio; inspect its artifacts. " + directory);
        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        if (string.IsNullOrWhiteSpace(stdout.Text)) throw new IOException("The runner returned no result. Inspect " + directory);
        try
        {
            using var document = JsonDocument.Parse(stdout.Text);
            return new(process.ExitCode, document.RootElement.Clone(), outputPath);
        }
        catch (JsonException ex) { throw new IOException("The runner did not return valid result JSON. Inspect " + directory, ex); }
    }

    private static async Task<(string Text, bool Overflow)> ReadBoundedAsync(StreamReader reader)
    {
        const int maximum = 32 * 1024 * 1024;
        var text = new StringBuilder(); var buffer = new char[8192]; var overflow = false;
        int length;
        while ((length = await reader.ReadAsync(buffer)) > 0)
        {
            var keep = Math.Min(length, maximum - text.Length);
            if (keep > 0) text.Append(buffer, 0, keep);
            if (keep < length) overflow = true;
        }
        return (text.ToString(), overflow);
    }
}
