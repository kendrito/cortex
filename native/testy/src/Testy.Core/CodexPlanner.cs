using System.Diagnostics;
using System.Text.Json;

namespace Testy.Core;

/// <summary>Development bridge to an installed, authenticated Codex CLI. Production providers are independent HTTP adapters.</summary>
public sealed class CodexPlanner(ProviderSettings settings, string workingDirectory) : ITestPlanner
{
    public string Name => "Codex (authenticated local CLI)";
    public async Task<TestCase> CreatePlanAsync(PlanningRequest request, CancellationToken cancellationToken = default)
    {
        var answer = await InvokeAsync(PlannerPrompt.System + "\n\n" + PlannerPrompt.Plan(request), PlanCodec.Schema, request.ScreenshotPath, cancellationToken);
        return PlanCodec.Parse(answer, request);
    }
    public async Task<string> ExplainAsync(RunResult run, CancellationToken cancellationToken = default)
    {
        using var schema = JsonDocument.Parse("""{"type":"object","additionalProperties":false,"properties":{"explanation":{"type":"string"}},"required":["explanation"]}""");
        var screenshot = run.Steps.LastOrDefault(s => !string.IsNullOrEmpty(s.ScreenshotPath))?.ScreenshotPath ?? "";
        var answer = await InvokeAsync("You are an evidence reviewer. Do not use tools, read files or execute commands. Return only the requested JSON.\n" + PlannerPrompt.Explain(run), schema.RootElement, screenshot, cancellationToken);
        using var parsed = JsonDocument.Parse(answer);
        return parsed.RootElement.GetProperty("explanation").GetString() ?? throw new InvalidDataException("Codex returned an empty explanation.");
    }
    internal async Task<string> InvokeAsync(string prompt, JsonElement schema, string screenshot, CancellationToken ct)
    {
        var jobDirectory = Path.Combine(Path.GetFullPath(workingDirectory), "planner-jobs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(jobDirectory);
        var schemaPath = Path.Combine(jobDirectory, "schema.json");
        var resultPath = Path.Combine(jobDirectory, "result.json");
        await File.WriteAllTextAsync(schemaPath, schema.GetRawText(), ct);
        var info = new ProcessStartInfo
        {
            FileName = CodexExecutableResolver.Resolve(settings.CodexExecutable, Environment.GetEnvironmentVariable("PATH") ?? "", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            WorkingDirectory = jobDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "exec", "--ignore-user-config", "--ignore-rules", "--skip-git-repo-check", "--ephemeral", "--sandbox", "read-only", "--color", "never", "--output-schema", schemaPath, "--output-last-message", resultPath }) info.ArgumentList.Add(arg);
        // Tools are disabled, and each invocation receives an empty working directory with only the supplied schema.
        foreach (var feature in new[] { "shell_tool", "unified_exec", "apps", "plugins", "hooks", "browser_use", "browser_use_external", "computer_use", "in_app_browser", "multi_agent", "image_generation", "view_image", "memories", "skill_search" })
        { info.ArgumentList.Add("--disable"); info.ArgumentList.Add(feature); }
        info.ArgumentList.Add("-c"); info.ArgumentList.Add("web_search=\"disabled\"");
        info.ArgumentList.Add("-c"); info.ArgumentList.Add("project_doc_max_bytes=0");
        if (!string.IsNullOrWhiteSpace(settings.Model)) { info.ArgumentList.Add("--model"); info.ArgumentList.Add(settings.Model); }
        if (settings.SupportsImages && !string.IsNullOrWhiteSpace(screenshot))
        {
            // Validate the supplied artifact before asking the CLI to read this one image.
            if (!File.Exists(screenshot)) throw new FileNotFoundException("The selected application screenshot is unavailable.", screenshot);
            info.ArgumentList.Add("--image"); info.ArgumentList.Add(Path.GetFullPath(screenshot));
        }
        info.ArgumentList.Add("-");
        using var process = new Process { StartInfo = info };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(4));
        try
        {
            try { process.Start(); }
            catch (System.ComponentModel.Win32Exception ex)
            { throw new InvalidOperationException("Could not start Codex. Install or select codex.exe and authenticate with codex login, or use the Offline command parser.", ex); }
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(prompt.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await stdout; // Drain output to avoid a pipe deadlock. Never treat logs as the structured result.
            var diagnostics = await stderr;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Codex exited with code {process.ExitCode}. " + Tail(diagnostics, 1800));
            if (!File.Exists(resultPath)) throw new InvalidDataException("Codex completed without a structured result. Check CLI authentication and model availability.");
            return await File.ReadAllTextAsync(resultPath, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("Codex planning exceeded four minutes. The request was stopped; no application action was executed."); }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            // No chat histories are stored; keep only schema/result artifacts to diagnose this invocation.
        }
    }
    private static string Tail(string value, int length) => value.Length <= length ? value.Trim() : value[^length..].Trim();
}
