using System.Text.Json;
using Testy.Core;

namespace Testy.Tests;

internal static class SnapshotReadinessChecks
{
    public static IEnumerable<(string Name, Func<Task> Execute)> All()
    {
        yield return ("transient incomplete trees are observed again before exact assertions", TransientAssertion);
        yield return ("transient incomplete readiness never duplicates or prematurely dispatches input", TransientMutation);
        yield return ("persistent incomplete trees cannot prove selectors or absence before deadline", PersistentTruncation);
        yield return ("cancellation during incomplete readiness dispatches no input", Cancellation);
        yield return ("snapshot deadline after an incomplete observation retains evidence classification", SnapshotDeadline);
        yield return ("late synchronous complete observations cannot bypass the step deadline", LateCompleteSnapshot);
        yield return ("post-step incomplete evidence is reobserved without repeating the action", TransientPostStepEvidence);
        yield return ("persistent post-step incompleteness fails a completed action without retry", PersistentPostStepEvidence);
        yield return ("late matching snapshots preserve prior selector facts and their original evidence", LateSnapshotFacts);
        yield return ("late synchronous screenshots cannot bypass the shared evidence deadline", LateScreenshot);
        yield return ("a virtualized or recycled UIA element is transient incompleteness, not a snapshot failure", ElementUnavailableIsTransient);
    }

    private static Task ElementUnavailableIsTransient()
    {
        // A recycled WPF row can fail a cached property read with the provider's error marker, which the managed UIA client raises as a
        // COMException carrying UIA_E_ELEMENTNOTAVAILABLE rather than as ElementNotAvailableException. The UIA driver must treat both as a
        // truncated tree (so the runner observes again without repeating the action), and nothing else.
        const string virtualized = "Element does not exist or it is virtualized; use VirtualizedItem Pattern if it is supported.";
        Check(Testy.Windows.UiaErrors.IsElementUnavailable(new System.Windows.Automation.ElementNotAvailableException(virtualized)), "ElementNotAvailableException must mark the tree incomplete.");
        Check(Testy.Windows.UiaErrors.IsElementUnavailable(new System.Runtime.InteropServices.COMException(virtualized, unchecked((int)0x80040201))),
            "A COMException carrying UIA_E_ELEMENTNOTAVAILABLE must mark the tree incomplete.");
        Check(!Testy.Windows.UiaErrors.IsElementUnavailable(new System.Runtime.InteropServices.COMException("Provider failure.", unchecked((int)0x80004005))),
            "Other provider COM failures must still fail the snapshot.");
        Check(!Testy.Windows.UiaErrors.IsElementUnavailable(new InvalidOperationException(virtualized)),
            "Only the UIA element-unavailable condition is transient; other exception types with the same text are not.");
        return Task.CompletedTask;
    }

    private static async Task TransientAssertion()
    {
        using var fixture = new Fixture { IncompleteReads = 2 };
        var run = await fixture.Run(Step(StepAction.AssertText, "Ready"));
        Check(run.Status == RunStatus.Passed && fixture.Reads >= 4 && fixture.Inputs == 0, "Transient observation did not recover through read-only polling.");
        Check(run.Steps[0].Snapshot?.IsTruncated == false && run.Steps[0].Step.Value == "Ready", "Assertion accepted incomplete evidence or altered its expectation.");

        using var mismatch = new Fixture { IncompleteReads = 1, CompleteValue = "Wrong" };
        var failed = await mismatch.Run(Step(StepAction.AssertText, "Ready", 280));
        Check(failed.Status == RunStatus.Failed && failed.FailureDiagnostics.Any(d => d.Category == FailureCategory.AssertionMismatch && d.Actual == "Wrong") && mismatch.Inputs == 0,
            "A matching value in the incomplete tree was accepted before the complete mismatching observation.");
    }

    private static async Task TransientMutation()
    {
        using var fixture = new Fixture { IncompleteReads = 2 };
        var run = await fixture.Run(Step(StepAction.Click), Step(StepAction.AssertText, "Ready"));
        Check(run.Status == RunStatus.Passed && fixture.Inputs == 1 && fixture.FirstInputRead >= 3 && !fixture.InputDuringIncomplete,
            "Input was duplicated or dispatched before a complete readiness observation.");

        using var failedInput = new Fixture { IncompleteReads = 1, FailInput = true };
        var failed = await failedInput.Run(Step(StepAction.Click), Step(StepAction.Click));
        Check(failed.Status == RunStatus.Failed && failedInput.Inputs == 1 && failed.Steps[1].Status == RunStatus.Skipped,
            "Readiness polling retried an already dispatched mutation or ran later input.");
    }

    private static async Task PersistentTruncation()
    {
        foreach (var action in new[] { StepAction.Click, StepAction.AssertText, StepAction.AssertNotExists })
        {
            using var fixture = new Fixture { IncompleteReads = int.MaxValue };
            var first = Step(action, action == StepAction.AssertText ? "Ready" : "", 180);
            if (action == StepAction.AssertNotExists) first.Selector = "id:Absent";
            var run = await fixture.Run(first, Step(StepAction.Click));
            Check(run.Status == RunStatus.Failed && fixture.Inputs == 0 && fixture.Reads >= 2 && run.Steps[1].Status == RunStatus.Skipped,
                "Persistent truncation proved a selector/absence or continued into input.");
            Check(run.FailureDiagnostics.Count > 0 && run.FailureDiagnostics.All(d => d.Category == FailureCategory.EvidenceUnavailable && d.ActionOutcome == ActionOutcome.NotDispatched)
                && run.FailureDiagnostics.Any(d => d.Evidence.Any(e => e.Kind == "observation used to decide failure" && File.Exists(e.Path))),
                "Persistent incomplete evidence lost its factual classification or deciding snapshot.");
        }
    }

    private static async Task Cancellation()
    {
        using var fixture = new Fixture { IncompleteReads = int.MaxValue };
        using var cancellation = new CancellationTokenSource();
        var running = new TestRunner(fixture, fixture.Root).RunAsync(new TestCase { Name = "Cancelled readiness", Steps = [Step(StepAction.Click), Step(StepAction.Click)] }, cancellationToken: cancellation.Token);
        await fixture.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var run = await running.WaitAsync(TimeSpan.FromSeconds(2));
        Check(run.Status == RunStatus.Cancelled && fixture.Inputs == 0 && run.Steps[1].Status == RunStatus.Skipped
            && run.FailureDiagnostics.Any(d => d.Category == FailureCategory.Cancelled && d.ActionOutcome == ActionOutcome.NotDispatched),
            "Cancellation during read-only readiness claimed dispatched input or continued the workflow.");
    }

    private static async Task SnapshotDeadline()
    {
        using var fixture = new Fixture { IncompleteReads = 1, BlockSecondRead = true };
        var run = await fixture.Run(Step(StepAction.Click, timeout: 180));
        Check(run.Status == RunStatus.Failed && fixture.Inputs == 0 && fixture.Reads == 3, "Blocked observation ignored deadline or dispatched input.");
        Check(run.FailureDiagnostics.All(d => d.Category == FailureCategory.EvidenceUnavailable && d.ActionOutcome == ActionOutcome.NotDispatched)
            && run.FailureDiagnostics.Any(d => d.ObservedFact.Contains("deadline", StringComparison.Ordinal)), "Incomplete snapshot deadline became a product mismatch or lost evidence classification.");
    }

    private static async Task LateCompleteSnapshot()
    {
        foreach (var action in new[] { StepAction.Click, StepAction.AssertText })
        {
            using var fixture = new Fixture { DelayFirstReadSynchronously = true };
            var run = await fixture.Run(Step(action, action == StepAction.AssertText ? "Ready" : "", 100), Step(StepAction.Click));
            Check(run.Status == RunStatus.Failed && fixture.Inputs == 0 && run.Steps[1].Status == RunStatus.Skipped
                && run.FailureDiagnostics.Any(d => d.Category == FailureCategory.AutomationTimeout && d.ActionOutcome == ActionOutcome.NotDispatched),
                "A synchronous snapshot returning after deadline proved acceptance or dispatched input.");
        }
    }

    private static async Task TransientPostStepEvidence()
    {
        foreach (var action in new[] { StepAction.Click, StepAction.AssertText, StepAction.Screenshot })
        {
            var evidenceRead = action == StepAction.Screenshot ? 1 : 2;
            using var fixture = new Fixture { IncompleteAtRead = read => read >= evidenceRead && read < evidenceRead + 2 };
            var run = await fixture.Run(Step(action, action == StepAction.AssertText ? "Ready" : ""));
            Check(run.Status == RunStatus.Passed && fixture.Reads == evidenceRead + 2 && fixture.Inputs == (action == StepAction.Click ? 1 : 0)
                && run.Steps[0].Snapshot?.IsTruncated == false, "Post-step polling repeated input or accepted incomplete evidence.");
            var saved = JsonSerializer.Deserialize<UiSnapshot>(File.ReadAllText(Path.Combine(run.ArtifactDirectory, "step-001.json")), TestyJson.Options)!;
            Check(!saved.IsTruncated && saved.CapturedAt == run.Steps[0].Snapshot!.CapturedAt, "Durable step evidence retained the transient incomplete tree.");
        }
    }

    private static async Task PersistentPostStepEvidence()
    {
        using var fixture = new Fixture { IncompleteAtRead = read => read >= 2, BlockThirdRead = true };
        var run = await fixture.Run(Step(StepAction.Click), Step(StepAction.Click)).WaitAsync(TimeSpan.FromSeconds(11));
        Check(run.Status == RunStatus.Failed && fixture.Inputs == 1 && fixture.Reads == 3 && run.Steps[1].Status == RunStatus.Skipped,
            "Failed post-action evidence repeated or continued input.");
        Check(run.Steps[0].Snapshot?.IsTruncated == true && run.FailureDiagnostics.All(d => d.Category == FailureCategory.EvidenceUnavailable && d.ActionOutcome == ActionOutcome.Completed),
            "Missing complete post-action evidence became a pass or an application defect.");
        Check(run.Steps[0].DurationMs >= 7900 && run.Steps[0].DurationMs < 10500, "Post-action snapshot/capture extended their shared evidence budget.");
    }

    private static async Task LateSnapshotFacts()
    {
        foreach (var missing in new[] { true, false })
        {
            using var fixture = new Fixture { MissingFirstRead = missing, WrongFirstValue = !missing, DelaySecondReadSynchronously = true };
            var run = await fixture.Run(Step(missing ? StepAction.Click : StepAction.AssertText, missing ? "" : "Ready", 180), Step(StepAction.Click));
            var expectedCategory = missing ? FailureCategory.SelectorNotFound : FailureCategory.AssertionMismatch;
            var prior = run.FailureDiagnostics.Single(d => d.Category == expectedCategory);
            Check(run.Status == RunStatus.Failed && fixture.Inputs == 0 && run.Steps[1].Status == RunStatus.Skipped
                && run.Summary.Contains(missing ? "not found" : "observed", StringComparison.Ordinal), "Late snapshot discarded the prior selector fact or proved a pass.");
            var evidence = prior.Evidence.First(e => e.Kind == "last completed observation before deadline");
            var saved = JsonSerializer.Deserialize<UiSnapshot>(File.ReadAllText(evidence.Path), TestyJson.Options)!;
            Check(saved.CapturedAt == prior.ObservedAt && (missing ? saved.Elements.Count == 0 : saved.Elements.Single().Value == "Wrong"),
                "Prior diagnostic was incorrectly linked to the late now-matching snapshot.");
            Check(run.Steps[0].Snapshot!.Elements.Single().Value == "Ready", "Regression did not distinguish earlier failing evidence from later matching evidence.");
        }
    }

    private static async Task LateScreenshot()
    {
        using var fixture = new Fixture { DelayCaptureSynchronously = true };
        var run = await fixture.Run(Step(StepAction.Click), Step(StepAction.Click));
        Check(run.Status == RunStatus.Failed && fixture.Inputs == 1 && run.Steps[1].Status == RunStatus.Skipped
            && run.FailureDiagnostics.Any(d => d.Category == FailureCategory.EvidenceUnavailable && d.ActionOutcome == ActionOutcome.Completed && d.ObservedFact.Contains("screenshot", StringComparison.Ordinal)),
            "A screenshot returning after the shared evidence deadline granted a pass or repeated input.");
        Check(File.Exists(run.Steps[0].ScreenshotPath), "Fixture did not actually return a late nonempty screenshot file.");
    }

    private static TestStep Step(StepAction action, string value = "", int timeout = 1000) => new() { Title = action.ToString(), Action = action, Selector = "id:Target", Value = value, TimeoutMs = timeout };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Fixture : ITargetDriver
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Testy-readiness-" + Guid.NewGuid().ToString("N"));
        public string Name => "Readiness fixture";
        public TargetInfo? Target { get; } = new() { ProcessId = 42, Title = "Owned fake target" };
        public int IncompleteReads { get; init; }
        public string CompleteValue { get; init; } = "Ready";
        public bool FailInput { get; init; }
        public bool BlockSecondRead { get; init; }
        public bool DelayFirstReadSynchronously { get; init; }
        public bool DelaySecondReadSynchronously { get; init; }
        public bool BlockThirdRead { get; init; }
        public bool MissingFirstRead { get; init; }
        public bool WrongFirstValue { get; init; }
        public Func<int, bool>? IncompleteAtRead { get; init; }
        public bool DelayCaptureSynchronously { get; init; }
        public int Reads { get; private set; }
        public int Inputs { get; private set; }
        public int FirstInputRead { get; private set; }
        public bool InputDuringIncomplete { get; private set; }
        public TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<TargetInfo>> GetTargetsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TargetInfo>>([Target!]);
        public Task AttachAsync(int processId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<RunResult> Run(params TestStep[] steps) => new TestRunner(this, Root).RunAsync(new TestCase { Name = "Snapshot readiness fixture", Steps = [.. steps] });
        public async Task<UiSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            if (Reads == 1 && DelayFirstReadSynchronously) Thread.Sleep(150);
            if (Reads == 2 && DelaySecondReadSynchronously) Thread.Sleep(150);
            if (Reads == 2 && BlockSecondRead) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (Reads == 3 && BlockThirdRead) await Task.Delay(Timeout.Infinite, cancellationToken);
            var snapshot = new UiSnapshot { Target = TestyJson.Clone(Target!), IsTruncated = IncompleteAtRead?.Invoke(Reads) ?? Reads <= IncompleteReads,
                Elements = Reads == 1 && MissingFirstRead ? [] : [new UiElementInfo { AutomationId = "Target", Selector = "id:Target", ControlType = "Edit", Value = Reads == 1 && WrongFirstValue ? "Wrong" : Reads <= IncompleteReads ? "Ready" : CompleteValue, IsEnabled = true }] };
            FirstRead.TrySetResult();
            return snapshot;
        }
        public Task ExecuteAsync(TestStep step, CancellationToken cancellationToken = default)
        {
            Inputs++;
            if (Inputs == 1) FirstInputRead = Reads;
            InputDuringIncomplete |= Reads <= IncompleteReads;
            if (FailInput) throw new IOException("Fixture input failed after dispatch.");
            return Task.CompletedTask;
        }
        public async Task<string> CaptureAsync(string path, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (DelayCaptureSynchronously)
            {
                Thread.Sleep(8100);
                File.WriteAllBytes(path, [137, 80, 78, 71]);
                return path;
            }
            await File.WriteAllBytesAsync(path, [137, 80, 78, 71], cancellationToken);
            return path;
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
