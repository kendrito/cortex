using Testy.Core;

namespace Testy.Tests;

internal static class ScreenshotPairChecks
{
    public static IEnumerable<(string Name, Func<Task> Execute)> All()
    {
        yield return ("screenshot geometry evidence is bound to the exact target artifact", StablePair);
        yield return ("changed screenshot origin and size refresh evidence without repeating input", TransientPair);
        yield return ("persistent screenshot coordinate disagreement cannot pass or repeat input", PersistentPair);
        yield return ("missing foreign and malformed screenshot provenance fails closed", InvalidProvenance);
        yield return ("cancelled screenshot pairing does not continue polling or input", Cancellation);
        yield return ("initial model observation uses a settled screenshot and tree pair", InitialObservation);
    }
    private static async Task StablePair()
    {
        using var f = new Driver(); var run = await f.Run(); var step = run.Steps.Single();
        Check(run.Status == RunStatus.Passed && f.Inputs == 1 && f.Captures == 1 && step.ScreenshotEvidence?.ProcessId == 42
            && step.ScreenshotEvidence.Path == step.ScreenshotPath && step.ScreenshotEvidence.Bounds.X == step.Snapshot!.ScreenshotBounds.X,
            "Successful screenshot lost target/path/origin provenance.");
    }
    private static async Task TransientPair()
    {
        foreach (var resize in new[] { false, true })
        {
            using var f = new Driver { ChangeOnce = true, Resize = resize }; var run = await f.Run();
            Check(run.Status == RunStatus.Passed && f.Inputs == 1 && f.Captures == 2 && f.Reads == 3
                && run.Steps[0].Snapshot!.ScreenshotBounds.X == (resize ? 100 : 125)
                && run.Steps[0].Snapshot!.ScreenshotBounds.Width == (resize ? 325 : 300),
                "Image/tree mismatch was accepted, input repeated, or origin was ignored when dimensions matched.");
        }
    }
    private static async Task PersistentPair()
    {
        using var f = new Driver { ChangeAlways = true }; var run = await f.Run(later: true);
        Check(run.Status == RunStatus.Failed && f.Inputs == 1 && f.Captures > 1 && run.Steps[1].Status == RunStatus.Skipped
            && run.Steps[0].DurationMs >= 7900 && run.Steps[0].DurationMs < 10500
            && run.Steps[0].FailureDiagnostics.All(d => d.Category == FailureCategory.EvidenceUnavailable && d.ActionOutcome == ActionOutcome.Completed),
            "Persistent geometry mismatch passed, extended its evidence deadline, or repeated an action.");
    }
    private static async Task InvalidProvenance()
    {
        foreach (var invalid in new[] { "missing", "pid", "path", "bounds" })
        {
            using var f = new Driver { Invalid = invalid }; var run = await f.Run(later: true);
            Check(run.Status == RunStatus.Failed && f.Inputs == 1 && f.Captures == 1 && run.Steps[1].Status == RunStatus.Skipped
                && run.Steps[0].FailureDiagnostics.Any(d => d.Category == FailureCategory.EvidenceUnavailable),
                "Invalid screenshot provenance was accepted or retried input: " + invalid);
        }
    }
    private static async Task Cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var f = new Driver { ChangeOnce = true, Captured = cancellation.Cancel };
        var run = await f.Run(later: true, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(2));
        Check(run.Status != RunStatus.Passed && f.Inputs == 1 && f.Captures == 1 && f.Reads == 2 && run.Steps[1].Status == RunStatus.Skipped,
            "Cancellation kept refreshing mismatched evidence or dispatched later input.");
    }
    private static async Task InitialObservation()
    {
        using var f = new Driver { ChangeOnce = true };
        var observation = await new LocalComputerTools(f, f.Root).DispatchAsync("observe_application", "{}");
        Check(observation.Execution?.Status == RunStatus.Passed && f.Inputs == 0 && f.Reads == 2 && f.Captures == 2
            && observation.Snapshot!.ScreenshotBounds.X == 125, "The model's initial image and tree have different coordinate origins.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Driver : ITargetDriver, IScreenshotEvidenceSource
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Testy-screenshot-pair-" + Guid.NewGuid().ToString("N"));
        public TargetInfo? Target { get; } = new() { ProcessId = 42, Title = "Screenshot pair fixture" };
        public ScreenshotEvidence? LastScreenshot { get; private set; }
        public int Inputs, Reads, Captures;
        public bool ChangeOnce, ChangeAlways, Resize;
        public string Invalid = "";
        public Action? Captured;
        private ElementBounds bounds = new() { X = 100, Y = 200, Width = 300, Height = 180 };
        public Task<RunResult> Run(bool later = false, CancellationToken ct = default)
        {
            var test = new TestCase { Name = "Screenshot geometry", Steps = [new() { Title = "Click", Action = StepAction.Click, Selector = "id:Target" }] };
            if (later) test.Steps.Add(new() { Title = "Must skip", Action = StepAction.Click, Selector = "id:Target" });
            return new TestRunner(this, Root).RunAsync(test, cancellationToken: ct);
        }
        public Task<IReadOnlyList<TargetInfo>> GetTargetsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TargetInfo>>([Target!]);
        public Task AttachAsync(int pid, CancellationToken ct = default) => Task.CompletedTask;
        public Task<UiSnapshot> SnapshotAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Reads++;
            return Task.FromResult(new UiSnapshot { Target = TestyJson.Clone(Target!), ScreenshotBounds = TestyJson.Clone(bounds),
                Elements = [new() { Selector = "id:Target", AutomationId = "Target", ControlType = "Button", IsEnabled = true }] });
        }
        public Task ExecuteAsync(TestStep step, CancellationToken ct = default) { Inputs++; return Task.CompletedTask; }
        public async Task<string> CaptureAsync(string path, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); LastScreenshot = null; Captures++;
            if (ChangeAlways || ChangeOnce && Captures == 1) { if (Resize) bounds.Width += 25; else bounds.X += 25; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, [137, 80, 78, 71], ct);
            if (Invalid != "missing") LastScreenshot = new() { Path = Invalid == "path" ? path + ".foreign" : path, ProcessId = Invalid == "pid" ? 43 : 42,
                CapturedAt = DateTimeOffset.UtcNow, Bounds = Invalid == "bounds" ? new() { Width = 0, Height = 180 } : TestyJson.Clone(bounds) };
            Captured?.Invoke(); return path;
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
