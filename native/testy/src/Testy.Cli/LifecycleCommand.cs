using System.Text.Json;
using System.Text.Json.Serialization;
using Testy.Core;

namespace Testy.Cli;

internal static class LifecycleCommand
{
    public static async Task<LifecycleRequest> LoadRequestAsync(string profilePath, CancellationToken ct)
    {
        profilePath = Path.GetFullPath(profilePath);
        var options = new JsonSerializerOptions(TestyJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        var text = await MaintenanceCommand.ReadBoundedAsync(profilePath, ct);
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.RootElement.EnumerateObject().Count())
            throw new InvalidDataException("Lifecycle profile must be an object without duplicate fields.");
        var profile = JsonSerializer.Deserialize<LifecycleProfile>(text, options) ?? throw new InvalidDataException("Lifecycle profile is null.");
        profile = WorkspaceMaintenance.ResolveRestoredProfileInputs(profilePath, profile);
        var directory = Path.GetDirectoryName(profilePath)!;
        string Resolve(string path) => !string.IsNullOrWhiteSpace(path) ? Path.GetFullPath(path, directory) : throw new InvalidDataException("Lifecycle profile is missing a required path.");
        profile.TestFile = Resolve(profile.TestFile); profile.SettingsFile = Resolve(profile.SettingsFile); profile.Executable = Resolve(profile.Executable);
        if (profile.ProjectFile is not null) profile.ProjectFile = Resolve(profile.ProjectFile);
        if (profile.TargetArgumentsFile is not null) profile.TargetArgumentsFile = Resolve(profile.TargetArgumentsFile);
        var request = new LifecycleRequest { Profile = profile, Test = await WorkerCommand.ReadJsonAsync<TestCase>(profile.TestFile, ct), Provider = await ProjectCliCommand.SettingsAsync(profile.SettingsFile, profile.ProjectFile, ct),
            TargetArguments = profile.TargetArgumentsFile is null ? [] : [.. await WorkerCommand.ReadJsonAsync<string[]>(profile.TargetArgumentsFile, ct)] };
        LifecycleValidation.ValidateRequest(request);
        return request;
    }
    public static async Task<LifecycleResult> RunAsync(string profilePath, string artifactsRoot, CancellationToken ct)
        => await RunAsync(await LoadRequestAsync(profilePath, ct), artifactsRoot, ct);

    public static async Task<LifecycleResult> RunAsync(LifecycleRequest request, string artifactsRoot, CancellationToken ct, OperationsDesktopLease? lease = null)
    {
        request = TestyJson.Clone(request);
        LifecycleValidation.ValidateEnvironment(request);
        WorkerCommand.ValidateProvider(request.Provider);
        using var ownedLease = lease is null ? OperationsDesktopLease.TryAcquire() : null;
        lease ??= ownedLease ?? throw new InvalidOperationException("This interactive desktop is reserved by another Testy operation. No preparation command or target was started.");
        var runner = new LifecycleRunner((settings, directory) => new ProjectPreparationAgent(settings, directory), async (frozen, directory, token) =>
        {
            Directory.CreateDirectory(directory);
            var test = Path.Combine(directory, "ui-test.json"); var settings = Path.Combine(directory, "ui-settings.json"); var args = Path.Combine(directory, "ui-arguments.json");
            WorkerCommand.DurableWrite(test, frozen.Test); WorkerCommand.DurableWrite(settings, frozen.Provider); WorkerCommand.DurableWrite(args, frozen.TargetArguments);
            var result = await WorkerCommand.RunAsync(new WorkerOptions
            {
                TestFile = test, SettingsFile = frozen.Profile.Replay ? null : settings, Executable = frozen.Profile.Executable, TargetArgumentsFile = args,
                Replay = frozen.Profile.Replay, Probe = frozen.Profile.Probe, ArtifactsRoot = directory,
                TimeoutSeconds = frozen.Profile.WorkerTimeoutSeconds, StartupTimeoutSeconds = frozen.Profile.StartupTimeoutSeconds,
                ShutdownGraceSeconds = frozen.Profile.ShutdownGraceSeconds, DesktopLease = lease
            }, token);
            return new LifecycleWorkerOutcome { Passed = result.Passed, Status = result.Status, ArtifactDirectory = result.ArtifactDirectory, Message = result.Message,
                CleanupComplete = result.CleanupComplete, ActionOutcomeUnknown = result.ActionOutcomeUnknown };
        });
        return await runner.RunAsync(request, artifactsRoot, ct);
    }
    public static Task<LifecycleResult> InspectAsync(string lifecycleDirectory, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(LifecycleRunner.InspectInterrupted(lifecycleDirectory)); }
}
