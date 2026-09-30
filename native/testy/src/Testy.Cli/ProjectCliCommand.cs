using System.Text.Json;
using Testy.Core;

namespace Testy.Cli;

internal static class ProjectCliCommand
{
    internal static async Task<ProviderSettings> SettingsAsync(string file, string? projectFile, CancellationToken ct)
    {
        var settings = await WorkerCommand.ReadJsonAsync<ProviderSettings>(file, ct);
        if (projectFile is not null) settings.ProjectTools = await ProjectConfiguration.LoadAsync(projectFile, ct);
        if (settings.ProjectTools is { Enabled: true }) ProjectToolSession.ValidateConfiguration(settings.ProjectTools);
        return settings;
    }

    internal static async Task<object> ConfigureAsync(string file, string workspace, CancellationToken ct)
    {
        var settings = await ProjectConfiguration.LoadAsync(file, ct);
        var store = new WorkspaceStore(workspace);
        store.ConfigureProjectTools(settings);
        return new { configured = true, workspace = store.RootDirectory, settings.Enabled, project = settings.RootDirectory,
            commands = settings.Commands.Select(c => c.Id).ToArray(),
            message = "Restart Studio, or open Automate again, to load this selected project. Create test, Improve and AI-guided Run use the project tools. Saving settings retains the project selection." };
    }

    internal static async Task<ProjectToolResult> ToolAsync(string file, string requestFile, string artifacts, CancellationToken ct)
    {
        var settings = await ProjectConfiguration.LoadAsync(file, ct);
        var text = await MaintenanceCommand.ReadBoundedAsync(requestFile, ct);
        if (text.Length > 16384) throw new InvalidDataException("Project tool request exceeds 16384 characters.");
        using var request = JsonDocument.Parse(text);
        var fields = request.RootElement.ValueKind == JsonValueKind.Object ? request.RootElement.EnumerateObject().ToArray() : [];
        if (fields.Length != 2 || fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != 2
            || fields.Any(f => f.Name is not ("tool" or "arguments")) || request.RootElement.GetProperty("tool").ValueKind != JsonValueKind.String
            || request.RootElement.GetProperty("arguments").ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Project tool request requires exactly tool (string) and arguments (object).");
        var session = new ProjectToolSession(settings, Path.Combine(artifacts, "project-" + Guid.NewGuid().ToString("N")));
        return await session.DispatchAsync(request.RootElement.GetProperty("tool").GetString()!, request.RootElement.GetProperty("arguments").GetRawText(), ct);
    }

    internal static async Task<object> DraftAsync(ITargetDriver driver, string settingsFile, string? projectFile,
        string instructionsFile, string? acceptanceFile, string output, string artifacts, CancellationToken ct)
    {
        if (File.Exists(output) || File.Exists(output + ".provenance.json")) throw new IOException("Draft or provenance exists; choose a new output path.");
        var settings = await SettingsAsync(settingsFile, projectFile, ct);
        if (settings.Kind == ProviderKind.Offline) throw new InvalidDataException("AI drafting requires a model provider.");
        var instructions = await MaintenanceCommand.ReadBoundedAsync(instructionsFile, ct);
        if (string.IsNullOrWhiteSpace(instructions) || instructions.Length > 12000) throw new InvalidDataException("Draft instructions require 1–12000 characters.");
        TestCase? acceptance = null;
        if (acceptanceFile is not null)
        {
            acceptance = await WorkerCommand.ReadJsonAsync<TestCase>(acceptanceFile, ct);
            TestValidator.Validate(acceptance);
            if (acceptance.Steps.Any(s => !TestValidator.IsAssertion(s.Action))) throw new InvalidDataException("Acceptance must contain assertions only.");
        }
        var directory = Path.Combine(artifacts, "draft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var snapshot = await driver.SnapshotAsync(ct);
        var screenshot = await driver.CaptureAsync(Path.Combine(directory, "initial.png"), ct);
        WorkspaceStore.WriteAtomic(Path.Combine(directory, "initial.json"), snapshot);
        var request = new PlanningRequest
        {
            Instructions = instructions + (acceptance is null ? "" : "\nPreserve every supplied acceptance assertion exactly, in order, including action, selector, value and timeout. Do not add, remove or weaken assertions."),
            Snapshot = snapshot, ScreenshotPath = settings.SupportsImages ? screenshot : "", ExistingTest = acceptance,
            ProjectArtifactsDirectory = Path.Combine(directory, "project-tools")
        };
        var plan = await PlannerFactory.Create(settings, directory).CreatePlanAsync(request, ct);
        TestValidator.Validate(plan);
        if (!plan.Steps.Any(s => TestValidator.IsAssertion(s.Action))) throw new InvalidDataException("AI draft must include an acceptance assertion.");
        if (acceptance is not null) MaintenanceCommand.VerifyAcceptance(plan, acceptance);
        plan.Id = Guid.NewGuid().ToString("N"); plan.Category = "AI project draft";
        var provenance = new
        {
            kind = "testy.project-draft.v1", createdAt = DateTimeOffset.UtcNow, state = "UnexecutedDraft",
            providerKind = settings.Kind, settings.Model, targetProcessId = driver.Target!.ProcessId,
            instructionsSha256 = MaintenanceCommand.Hash(instructions),
            acceptanceSha256 = acceptance is null ? null : MaintenanceCommand.Hash(JsonSerializer.Serialize(acceptance, TestyJson.Options)),
            project = settings.ProjectTools?.Enabled == true ? settings.ProjectTools.RootDirectory : null,
            projectEvidence = request.ProjectEvidence, artifactDirectory = directory,
            steps = plan.Steps.Count, assertions = plan.Steps.Count(s => TestValidator.IsAssertion(s.Action))
        };
        // Validate both serialized payloads before publishing either file.
        _ = JsonSerializer.Serialize(plan, TestyJson.Options); _ = JsonSerializer.Serialize(provenance, TestyJson.Options);
        MaintenanceCommand.WriteNew(output, plan);
        MaintenanceCommand.WriteNew(output + ".provenance.json", provenance);
        return new { output = Path.GetFullPath(output), state = "UnexecutedDraft", steps = plan.Steps.Count,
            projectTools = request.ProjectEvidence.Count, provenance = Path.GetFullPath(output + ".provenance.json"),
            message = "Review the generated test before running it against a fresh test instance. Project commands and file observations do not prove UI acceptance." };
    }
}
