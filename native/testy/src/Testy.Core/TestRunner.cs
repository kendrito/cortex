using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Testy.Core;

public sealed class TestRunner(ITargetDriver driver, string artifactsRoot)
{
    private readonly SemaphoreSlim runGate = new(1, 1);
    public Func<RunResult, CancellationToken, Task>? StepObserver { get; set; }
    public async Task<RunResult> RunAsync(TestCase test, IProgress<RunProgress>? progress = null, CancellationToken cancellationToken = default)
        => await RunValidatedAsync(test, null, null, progress, cancellationToken);
    /// <summary>An explicit caller/model choice of a previously authored alternative; never an automatic retry.</summary>
    public async Task<RunResult> RunWithSelectorAlternativeAsync(TestCase test, string stepId, string alternativeId, IProgress<RunProgress>? progress = null, CancellationToken cancellationToken = default)
        => await RunValidatedAsync(test, stepId, alternativeId, progress, cancellationToken);
    private async Task<RunResult> RunValidatedAsync(TestCase test, string? recoveryStepId, string? alternativeId, IProgress<RunProgress>? progress, CancellationToken cancellationToken)
    {
        TestValidator.Validate(test);
        if (recoveryStepId is not null && (test.Steps.Count != 1 || test.Steps[0].Id != recoveryStepId || !test.Steps[0].SelectorAlternatives.Any(a => a.Id == alternativeId)))
            throw new InvalidDataException("An explicit recovery call must identify one saved step and one of its authored selector alternatives.");
        if (driver.Target is null) throw new InvalidOperationException("Attach to a target application before running a test.");
        if (!await runGate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("This runner already has a test in progress.");
        try { return await RunCoreAsync(TestyJson.Clone(test), progress, cancellationToken, alternativeId); }
        finally { runGate.Release(); }
    }
    private async Task<RunResult> RunCoreAsync(TestCase test, IProgress<RunProgress>? progress, CancellationToken ct, string? alternativeId = null)
    {
        var run = new RunResult { TestId = test.Id, TestName = test.Name, Target = TestyJson.Clone(driver.Target!), Status = RunStatus.Running };
        run.ArtifactDirectory = Path.GetFullPath(Path.Combine(artifactsRoot, run.StartedAt.ToString("yyyyMMdd-HHmmss") + "-" + run.Id[..8]));
        Directory.CreateDirectory(run.ArtifactDirectory);
        WorkspaceStore.WriteAtomic(Path.Combine(run.ArtifactDirectory, "requested-test.json"), test);
        Report(progress, run, null, $"Running {test.Name}.");
        var stopped = false;
        for (var index = 0; index < test.Steps.Count; index++)
        {
            var step = test.Steps[index];
            var result = new StepResult { Index = index, Step = step, Status = RunStatus.Running };
            if (alternativeId is not null) result.SelectorRecovery = new() { AlternativeId = alternativeId, OriginalSelector = step.Selector, ResolvedSelector = step.SelectorAlternatives.Single(a => a.Id == alternativeId).Selector };
            run.Steps.Add(result);
            if (stopped)
            {
                result.Status = RunStatus.Skipped;
                result.Message = "Skipped because the run stopped before this step.";
                continue;
            }
            var watch = Stopwatch.StartNew();
            var actionOutcome = ActionOutcome.NotDispatched;
            Report(progress, run, result, $"Step {index + 1}/{test.Steps.Count}: {step.Title}");
            try
            {
                ct.ThrowIfCancellationRequested();
                EnsureTarget(run.Target.ProcessId);
                await ExecuteStepAsync(step, ct, lookup => result.ItemLookup = TestyJson.Clone(lookup), alternativeId, (recovery, snapshot) =>
                {
                    result.SelectorRecovery = TestyJson.Clone(recovery);
                    result.SelectorRecovery.GuardSnapshotPath = Path.Combine(run.ArtifactDirectory, $"step-{index + 1:000}-recovery.json");
                    WorkspaceStore.WriteAtomic(result.SelectorRecovery.GuardSnapshotPath, PlannerPrompt.Sanitize(snapshot));
                });
                if (!TestValidator.IsAssertion(step.Action) && step.Action is not (StepAction.Wait or StepAction.Screenshot)) actionOutcome = ActionOutcome.Completed;
                result.Status = RunStatus.Passed;
                result.Message = TestValidator.IsAssertion(step.Action) ? "Assertion satisfied." : "Action completed.";
                if (result.ItemLookup is not null) result.Message += $" Read-only item lookup: {result.ItemLookup.Status}. {result.ItemLookup.Message}";
                if (result.SelectorRecovery is not null) result.Message += $" Explicit selector alternative '{result.SelectorRecovery.AlternativeId}': {result.SelectorRecovery.OriginalSelector} → {result.SelectorRecovery.ResolvedSelector}; exact engine identity verified. Saved action and expectation unchanged.";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                result.Status = RunStatus.Cancelled;
                result.Message = "Run cancelled by the user.";
                result.FailureDiagnostics.Add(FailureDiagnostics.Create(FailureCategory.Cancelled, result.Message, step));
                run.Status = RunStatus.Cancelled;
                stopped = true;
            }
            catch (Exception ex)
            {
                result.Status = ex is StepDiagnosticException { Diagnostic.Category: FailureCategory.Cancelled } && ct.IsCancellationRequested ? RunStatus.Cancelled : RunStatus.Failed;
                result.Message = ex.Message;
                var diagnostic = FailureDiagnostics.FromException(ex, step);
                result.FailureDiagnostics.Add(diagnostic);
                if (ex is StepDiagnosticException supporting) result.FailureDiagnostics.AddRange(supporting.SupportingDiagnostics);
                actionOutcome = diagnostic.ActionOutcome;
                if (result.SelectorRecovery is not null) result.SelectorRecovery.ActionOutcome = actionOutcome;
                if (ex is StepDiagnosticException { Observation: not null } observed)
                {
                    try
                    {
                        var failurePath = Path.Combine(run.ArtifactDirectory, $"step-{index + 1:000}-failure.json");
                        WorkspaceStore.WriteAtomic(failurePath, PlannerPrompt.Sanitize(observed.Observation));
                        diagnostic.Evidence.Add(new DiagnosticEvidence { Kind = "observation used to decide failure", Path = failurePath });
                        foreach (var fact in observed.SupportingDiagnostics) fact.Evidence.Add(new DiagnosticEvidence { Kind = "last completed observation before deadline", Path = failurePath });
                    }
                    catch (Exception evidenceError) { result.FailureDiagnostics.Add(FailureDiagnostics.Create(FailureCategory.EvidenceUnavailable, "Could not persist the failing observation: " + evidenceError.Message, step)); }
                }
                run.Status = result.Status;
                stopped = true;
            }
            // Evidence is attempted independently, including after action failures and cancellation.
            using var evidenceTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            async Task ReadCompleteEvidenceAsync()
            {
                while (true)
                {
                    EnsureTarget(run.Target.ProcessId);
                    result.Snapshot = PlannerPrompt.Sanitize(await driver.SnapshotAsync(evidenceTimeout.Token).WaitAsync(evidenceTimeout.Token));
                    WorkspaceStore.WriteAtomic(Path.Combine(run.ArtifactDirectory, $"step-{index + 1:000}.json"), result.Snapshot);
                    evidenceTimeout.Token.ThrowIfCancellationRequested();
                    if (!result.Snapshot.IsTruncated) break;
                    if (ct.IsCancellationRequested) throw new IOException("The final post-cancellation UI snapshot is truncated; complete evidence is unavailable.");
                    // Re-observe a transient visual-tree transition without repeating the completed action.
                    // Snapshot and screenshot still share the original eight-second evidence deadline.
                    await Task.Delay(100, evidenceTimeout.Token);
                }
            }
            try { await ReadCompleteEvidenceAsync(); }
            catch (Exception ex)
            {
                EvidenceError(run, result, "snapshot", result.Snapshot?.IsTruncated == true
                    ? new IOException("A complete post-step UI snapshot was not obtained; the last observation remained truncated. " + ex.Message, ex) : ex, actionOutcome);
                stopped = true;
            }
            var pairMismatch = false;
            try
            {
                EnsureTarget(run.Target.ProcessId);
                var path = Path.Combine(run.ArtifactDirectory, $"step-{index + 1:000}.png");
                while (true)
                {
                    result.ScreenshotEvidence = null;
                    result.ScreenshotPath = await driver.CaptureAsync(path, evidenceTimeout.Token).WaitAsync(evidenceTimeout.Token);
                    evidenceTimeout.Token.ThrowIfCancellationRequested();
                    if (!File.Exists(result.ScreenshotPath)) throw new IOException("Driver returned a screenshot path that does not exist.");
                    if (new FileInfo(result.ScreenshotPath).Length == 0) throw new IOException("Driver returned an empty screenshot file.");
                    if (driver is not IScreenshotEvidenceSource source) break;
                    var captured = source.LastScreenshot ?? throw new IOException("Driver did not provide committed screenshot geometry.");
                    result.ScreenshotEvidence = TestyJson.Clone(captured);
                    if (captured.ProcessId != run.Target.ProcessId || !string.Equals(Path.GetFullPath(captured.Path), Path.GetFullPath(result.ScreenshotPath), StringComparison.OrdinalIgnoreCase)
                        || captured.CapturedAt == default || captured.CapturedAt > DateTimeOffset.UtcNow || !ValidBounds(captured.Bounds))
                        throw new IOException("Screenshot provenance does not match the returned artifact and attached target.");
                    if (result.Snapshot is not null && !result.Snapshot.IsTruncated && captured.CapturedAt >= result.Snapshot.CapturedAt && SameBounds(result.Snapshot.ScreenshotBounds, captured.Bounds)) break;
                    pairMismatch = true;
                    if (ct.IsCancellationRequested) throw new IOException("The final post-cancellation screenshot and control tree have different coordinate spaces; complete paired evidence is unavailable.");
                    // Window/popup animations can settle between tree inspection and capture. Only
                    // refresh evidence; never repeat the step, and never extend the shared deadline.
                    await Task.Delay(100, evidenceTimeout.Token);
                    await ReadCompleteEvidenceAsync();
                }
            }
            catch (Exception ex) { EvidenceError(run, result, "screenshot", pairMismatch ? new IOException("Screenshot and control-tree coordinate spaces did not establish a matching pair within the evidence budget. " + ex.Message, ex) : ex, actionOutcome); stopped = true; }
            result.DurationMs = watch.Elapsed.TotalMilliseconds;
            FailureDiagnostics.Refresh(run);
            if (StepObserver is not null && !ct.IsCancellationRequested)
            {
                try
                {
                    Report(progress, run, result, $"Reviewing evidence for step {index + 1}…");
                    var observed = TestyJson.Clone(run);
                    await StepObserver(observed, ct);
                    if (!string.IsNullOrWhiteSpace(observed.AiAnalysis)) run.AiAnalysis = observed.AiAnalysis;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                { run.Status = RunStatus.Cancelled; stopped = true; }
                catch (Exception ex)
                { run.AiAnalysis += $"\nAI review unavailable after step {index + 1}: {ex.Message}"; }
            }
            if (ct.IsCancellationRequested && run.Status != RunStatus.Failed) { run.Status = RunStatus.Cancelled; stopped = true; }
            Report(progress, run, result, $"{result.Status}: {step.Title}. {result.Message}");
            WorkspaceStore.WriteAtomic(Path.Combine(run.ArtifactDirectory, "run.json"), run);
        }
        if (run.Status == RunStatus.Running) run.Status = RunStatus.Passed;
        run.FinishedAt = DateTimeOffset.UtcNow;
        var passed = run.Steps.Count(s => s.Status == RunStatus.Passed);
        var failed = run.Steps.FirstOrDefault(s => s.Status == RunStatus.Failed);
        run.Summary = $"{run.Status}: {passed}/{test.Steps.Count} steps passed." + (failed is null ? "" : $" Step {failed.Index + 1}: {failed.Message}");
        WriteReports(run);
        Report(progress, run, null, run.Summary);
        return run;
    }
    private void EnsureTarget(int pid)
    {
        if (driver.Target?.ProcessId != pid) throw new StepDiagnosticException(FailureDiagnostics.Create(FailureCategory.TargetUnavailable, "The attached target changed during the run. Run stopped."));
    }
    private static bool ValidBounds(ElementBounds? bounds) => bounds is not null && double.IsFinite(bounds.X) && double.IsFinite(bounds.Y)
        && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) && bounds.Width > 0 && bounds.Height > 0;
    private static bool SameBounds(ElementBounds? left, ElementBounds right) => ValidBounds(left) && left!.X == right.X && left.Y == right.Y && left.Width == right.Width && left.Height == right.Height;
    private static void EvidenceError(RunResult run, StepResult step, string kind, Exception ex, ActionOutcome actionOutcome)
    {
        step.Message += $" Evidence {kind} failed: {ex.Message}";
        step.FailureDiagnostics.Add(FailureDiagnostics.Create(FailureCategory.EvidenceUnavailable, $"Evidence {kind} failed: {ex.Message}", step.Step, actionOutcome: actionOutcome));
        if (step.Status != RunStatus.Cancelled) step.Status = RunStatus.Failed;
        if (run.Status != RunStatus.Cancelled) run.Status = RunStatus.Failed;
    }
    private async Task ExecuteStepAsync(TestStep step, CancellationToken ct, Action<ItemLookupResult> recordLookup, string? alternativeId, Action<SelectorRecoveryEvidence, UiSnapshot> recordRecovery)
    {
        var operationWatch = Stopwatch.StartNew();
        UiExecutionGuard? executionGuard = null;
        UiSnapshot? guardedSnapshot = null;
        SelectorRecoveryEvidence? recoveryEvidence = null;
        var canonical = step;
        if (alternativeId is not null)
        {
            if (driver is not IGuardedTargetDriver) throw new StepDiagnosticException(FailureDiagnostics.Create(FailureCategory.CapabilityUnavailable, "The selected driver cannot revalidate an approved selector alternative immediately before use.", canonical));
            using var preparation = CancellationTokenSource.CreateLinkedTokenSource(ct); preparation.CancelAfter(step.TimeoutMs);
            UiSnapshot initial;
            try
            {
                initial = await driver.SnapshotAsync(preparation.Token).WaitAsync(preparation.Token);
                executionGuard = SelectorRecovery.Resolve(canonical, alternativeId, initial);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new StepDiagnosticException(FailureDiagnostics.Create(FailureCategory.AutomationTimeout, "Selector recovery preparation exceeded the deadline before input.", canonical)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { throw new StepDiagnosticException(FailureDiagnostics.Create(FailureCategory.EvidenceUnavailable, "Selector recovery rejected before input: " + ex.Message, canonical), inner: ex); }
            recoveryEvidence = new() { AlternativeId = alternativeId, OriginalSelector = canonical.Selector, ResolvedSelector = executionGuard.Selector, Guard = TestyJson.Clone(executionGuard) };
            recordRecovery(recoveryEvidence, initial);
            guardedSnapshot = initial;
            step = TestyJson.Clone(canonical); step.Selector = executionGuard.Selector;
        }
        if (step.Action == StepAction.Wait)
        {
            await Task.Delay(step.Value.Length == 0 ? step.TimeoutMs : int.Parse(step.Value), ct);
            return;
        }
        if (step.Action == StepAction.Screenshot) return;
        if (TestValidator.RequiresSelector(step.Action))
        {
            var watch = operationWatch;
            string last = "No matching element.";
            var lastDiagnostic = FailureDiagnostics.Create(FailureCategory.SelectorNotFound, last, step, "1 matching control", "0", "selector match count");
            FailureDiagnostic? lastLookupMismatch = null;
            UiSnapshot? lastSnapshot = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (lastSnapshot is not null && watch.Elapsed.TotalMilliseconds >= step.TimeoutMs)
                {
                    lastDiagnostic.ObservedFact = $"After {step.TimeoutMs} ms: {last}";
                    throw new StepDiagnosticException(lastDiagnostic, lastSnapshot);
                }
                UiSnapshot snapshot;
                using (var snapshotTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    snapshotTimeout.CancelAfter(Math.Max(1, (int)(step.TimeoutMs - watch.Elapsed.TotalMilliseconds)));
                    try
                    {
                        snapshot = executionGuard is null
                            ? await driver.SnapshotAsync(snapshotTimeout.Token).WaitAsync(snapshotTimeout.Token)
                            : await ((IGuardedTargetDriver)driver).SnapshotGuardedAsync(executionGuard, snapshotTimeout.Token).WaitAsync(snapshotTimeout.Token);
                        if (executionGuard is not null)
                        {
                            SelectorRecovery.ValidateGuard(snapshot, executionGuard);
                            guardedSnapshot = snapshot;
                            recoveryEvidence!.EngineVerified = true; recoveryEvidence.GuardedAt = snapshot.CapturedAt;
                            recordRecovery(recoveryEvidence, snapshot);
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    { throw new StepDiagnosticException(FailureDiagnostics.Create(lastSnapshot?.IsTruncated == true ? FailureCategory.EvidenceUnavailable : FailureCategory.AutomationTimeout,
                        $"After {step.TimeoutMs} ms: UI snapshot did not return before the step deadline. Last observation: {last}", step), lastSnapshot,
                        supportingDiagnostics: lastSnapshot is null ? null : [lastDiagnostic]); }
                }
                if (!snapshot.IsTruncated && watch.Elapsed.TotalMilliseconds >= step.TimeoutMs)
                    throw new StepDiagnosticException(FailureDiagnostics.Create(FailureCategory.AutomationTimeout,
                        $"After {step.TimeoutMs} ms: a complete UI observation arrived after the step deadline; no selector decision or input was made from it. Last observation: {(lastSnapshot is null ? "No completed readiness observation." : last)}", step), lastSnapshot,
                        supportingDiagnostics: lastSnapshot is null ? null : [lastDiagnostic]);
                lastSnapshot = snapshot;
                if (snapshot.IsTruncated)
                {
                    // A modal/visual-tree transition may make one read incomplete. Poll only observations:
                    // no selector decision or input may use this tree, and the original step deadline remains in force.
                    last = "The application UI tree was truncated. Selector uniqueness and absence cannot be verified until a complete observation is available.";
                    lastDiagnostic = FailureDiagnostics.Create(FailureCategory.EvidenceUnavailable, last, step, observedAt: snapshot.CapturedAt);
                    var observationRemaining = step.TimeoutMs - watch.Elapsed.TotalMilliseconds;
                    if (observationRemaining <= 0)
                    {
                        lastDiagnostic.ObservedFact = $"After {step.TimeoutMs} ms: {last}";
                        throw new StepDiagnosticException(lastDiagnostic, snapshot);
                    }
                    await Task.Delay((int)Math.Min(100, Math.Max(1, observationRemaining)), ct);
                    continue;
                }
                var matches = UiSelectors.Find(snapshot, step.Selector);
                if (step.Action == StepAction.AssertNotExists)
                {
                    if (matches.Count == 0)
                    {
                        if (snapshot.Elements.Any(e => e.ChildCoverage != ChildCoverage.Complete))
                            throw new StepDiagnosticException(FailureDiagnostics.Create(FailureCategory.EvidenceUnavailable, "The realized UI tree cannot prove logical item absence: a collection has realized-only or unknown child coverage. Use AssertItemAbsent with a supported item lookup.", step, observedAt: snapshot.CapturedAt), snapshot);
                        return;
                    }
                    last = $"Expected '{step.Selector}' to be absent; found {matches.Count} element(s).";
                    lastDiagnostic = FailureDiagnostics.Create(FailureCategory.AssertionMismatch, last, step, "0", matches.Count.ToString(), "selector match count", observedAt: snapshot.CapturedAt);
                }
                else if (matches.Count > 1) throw new StepDiagnosticException(FailureDiagnostics.Create(FailureCategory.SelectorAmbiguous, $"Selector '{step.Selector}' is ambiguous ({matches.Count} matches). Use a unique automation ID or scoped query.", step, "1 unique match", matches.Count.ToString(), "selector match count", observedAt: snapshot.CapturedAt), snapshot);
                else if (matches.Count == 1)
                {
                    var element = matches[0];
                    if (step.Action == StepAction.AssertExists) return;
                    if (step.Action == StepAction.AssertProperty)
                    {
                        var property = AdvancedSteps.ParseProperty(step.Value);
                        if (element.IsPassword) throw new StepDiagnosticException(FailureDiagnostics.Create(FailureCategory.EvidenceUnavailable, "Property observations on password fields are redacted.", step, observedAt: snapshot.CapturedAt), snapshot);
                        if (!element.Properties.TryGetValue(property.Property, out var observed) || observed.Status != UiPropertyStatus.Known)
                            throw new StepDiagnosticException(FailureDiagnostics.Create(observed is null || observed.Status == UiPropertyStatus.Unsupported ? FailureCategory.CapabilityUnavailable : FailureCategory.EvidenceUnavailable,
                                $"Property '{property.Property}' is {observed?.Status.ToString() ?? "Unsupported"}; no value can be asserted.", step, property.Expected.GetRawText(), observed?.Status.ToString() ?? "Unsupported", "typed property equality", observedAt: snapshot.CapturedAt), snapshot);
                        // Nullable JsonElement serializes an observed JSON null as null. Status=Known distinguishes it from missing evidence.
                        var actualProperty = observed.Value ?? JsonSerializer.SerializeToElement<object?>(null);
                        if (AdvancedSteps.ScalarEquals(actualProperty, property.Expected)) return;
                        last = $"Property '{property.Property}' expected {property.Expected.GetRawText()}, observed {actualProperty.GetRawText()} (source: {observed.Source}).";
                        lastDiagnostic = FailureDiagnostics.Create(FailureCategory.AssertionMismatch, last, step, property.Expected.GetRawText(), actualProperty.GetRawText(), "typed property equality: " + property.Property, observedAt: snapshot.CapturedAt);
                    }
                    else if (step.Action is StepAction.AssertItemExists or StepAction.AssertItemAbsent)
                    {
                        if (driver is not IItemLookupDriver lookup) throw new StepDiagnosticException(FailureDiagnostics.Create(FailureCategory.CapabilityUnavailable, "This driver does not support read-only item lookup.", step), snapshot);
                        using var lookupTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        lookupTimeout.CancelAfter(Math.Max(1, (int)(step.TimeoutMs - watch.Elapsed.TotalMilliseconds)));
                        ItemLookupResult found;
                        try { found = await lookup.LookupItemAsync(step.Selector, step.Value, lookupTimeout.Token).WaitAsync(lookupTimeout.Token); }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new StepDiagnosticException(FailureDiagnostics.Create(FailureCategory.AutomationTimeout, "Read-only item lookup exceeded the step deadline; its final outcome was not established.", step), snapshot,
                            supportingDiagnostics: lastLookupMismatch is null ? null : [lastLookupMismatch]); }
                        recordLookup(found);
                        if (!Enum.IsDefined(found.Status) || found.Status is ItemLookupStatus.Unsupported or ItemLookupStatus.Unavailable)
                            throw new StepDiagnosticException(FailureDiagnostics.Create(found.Status == ItemLookupStatus.Unsupported ? FailureCategory.CapabilityUnavailable : FailureCategory.EvidenceUnavailable, "Read-only item lookup: " + found.Status + ". " + found.Message, step, observedAt: found.ObservedAt), snapshot);
                        if (found.Status == ItemLookupStatus.Ambiguous) throw new StepDiagnosticException(FailureDiagnostics.Create(FailureCategory.SelectorAmbiguous, "Item lookup matched multiple items. " + found.Message, step, observedAt: found.ObservedAt), snapshot);
                        if (found.Status == (step.Action == StepAction.AssertItemExists ? ItemLookupStatus.Unique : ItemLookupStatus.Missing)) return;
                        last = $"Read-only item lookup expected {(step.Action == StepAction.AssertItemExists ? "Unique" : "Missing")}, observed {found.Status}. {found.Message}";
                        lastDiagnostic = FailureDiagnostics.Create(FailureCategory.AssertionMismatch, last, step, step.Action == StepAction.AssertItemExists ? "Unique" : "Missing", found.Status.ToString(), "logical item lookup", observedAt: found.ObservedAt);
                        lastLookupMismatch = lastDiagnostic;
                    }
                    else if (step.Action == StepAction.AssertText)
                    {
                        if (element.IsPassword) throw new InvalidOperationException("Text assertions on password fields are not supported.");
                        if (element.IsValueTruncated) throw new StepDiagnosticException(FailureDiagnostics.Create(FailureCategory.EvidenceUnavailable,
                            "The control's text value was truncated. Its complete text was not observed; neither exact text nor substring assertions can be verified from this incomplete value.", step,
                            actual: "Truncated", comparison: "text observation completeness", observedAt: snapshot.CapturedAt), snapshot);
                        var hasTextValue = element.ControlType is "Edit" or "TextBox" or "ComboBox" or "Document";
                        var actual = hasTextValue || element.Value.Length > 0 ? element.Value : element.Name;
                        var contains = step.Value.StartsWith("contains:", StringComparison.Ordinal);
                        var expected = contains ? step.Value[9..] : step.Value;
                        if (contains ? actual.Contains(expected, StringComparison.Ordinal) : actual == expected) return;
                        last = $"Expected {(contains ? "text containing" : "exact text")} '{expected}' at '{step.Selector}', observed '{actual}'.";
                        lastDiagnostic = FailureDiagnostics.Create(FailureCategory.AssertionMismatch, last, step, expected, actual, contains ? "case-sensitive substring" : "case-sensitive exact text", observedAt: snapshot.CapturedAt);
                    }
                    else if (AdvancedSteps.IsMutation(step.Action) && !element.Capabilities.Contains(AdvancedSteps.Capability(step.Action), StringComparer.Ordinal))
                        throw new StepDiagnosticException(FailureDiagnostics.Create(FailureCategory.CapabilityUnavailable, $"Control '{step.Selector}' does not advertise {AdvancedSteps.Capability(step.Action)} capability for {step.Action}.", step, observedAt: snapshot.CapturedAt), snapshot);
                    else if (!element.IsEnabled)
                    {
                        last = $"Element '{step.Selector}' is disabled.";
                        lastDiagnostic = FailureDiagnostics.Create(step.Action == StepAction.AssertEnabled ? FailureCategory.AssertionMismatch : FailureCategory.ControlNotReady, last, step, "true", "false", "control enabled", observedAt: snapshot.CapturedAt);
                    }
                    else if (step.Action == StepAction.AssertEnabled) return;
                    else if (element.IsOffscreen && !AdvancedSteps.IsMutation(step.Action))
                    {
                        last = $"Element '{step.Selector}' is offscreen.";
                        lastDiagnostic = FailureDiagnostics.Create(FailureCategory.ControlNotReady, last, step, "false", "true", "control offscreen", observedAt: snapshot.CapturedAt);
                    }
                    else break;
                }
                else
                {
                    last = $"Element '{step.Selector}' was not found.";
                    lastDiagnostic = FailureDiagnostics.Create(FailureCategory.SelectorNotFound, last, step, "1 matching control", "0", "selector match count", observedAt: snapshot.CapturedAt);
                }
                var remaining = step.TimeoutMs - watch.Elapsed.TotalMilliseconds;
                if (remaining <= 0)
                {
                    lastDiagnostic.ObservedFact = $"After {step.TimeoutMs} ms: {last}";
                    throw new StepDiagnosticException(lastDiagnostic, lastSnapshot);
                }
                await Task.Delay((int)Math.Min(100, Math.Max(1, remaining)), ct);
            }
        }
        // Mutation is deliberately issued once. Retrying an action could create duplicate data.
        var inputRemaining = step.TimeoutMs - operationWatch.Elapsed.TotalMilliseconds;
        if (inputRemaining <= 0)
            throw new StepDiagnosticException(FailureDiagnostics.Create(FailureCategory.AutomationTimeout,
                $"After {step.TimeoutMs} ms: the step deadline expired before input dispatch.", step));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Math.Max(1, (int)inputRemaining));
        try
        {
            if (executionGuard is null) await driver.ExecuteAsync(step, timeout.Token).WaitAsync(timeout.Token);
            else
            {
                recoveryEvidence!.ActionOutcome = ActionOutcome.Unknown;
                recordRecovery(recoveryEvidence, guardedSnapshot!);
                var beforeInput = await ((IGuardedTargetDriver)driver).ExecuteGuardedAsync(step, executionGuard, timeout.Token).WaitAsync(timeout.Token);
                SelectorRecovery.ValidateGuard(beforeInput, executionGuard);
                recoveryEvidence.EngineVerified = true; recoveryEvidence.GuardedAt = beforeInput.CapturedAt; recoveryEvidence.ActionOutcome = ActionOutcome.Completed;
                recordRecovery(recoveryEvidence, beforeInput);
            }
        }
        catch (OperationCanceledException ex)
        {
            throw new StepDiagnosticException(FailureDiagnostics.Create(ct.IsCancellationRequested ? FailureCategory.Cancelled : FailureCategory.AutomationTimeout,
                ct.IsCancellationRequested ? "Run cancelled while input was being dispatched; its completion was not confirmed." : $"Action '{step.Title}' exceeded {step.TimeoutMs} ms; it was not retried.", step, actionOutcome: ActionOutcome.Unknown), inner: ex);
        }
        catch (Exception ex) { throw new StepDiagnosticException(FailureDiagnostics.FromException(ex, step, ActionOutcome.Unknown), inner: ex); }
    }
    private static void Report(IProgress<RunProgress>? progress, RunResult run, StepResult? step, string message) => progress?.Report(new RunProgress { Run = TestyJson.Clone(run), Step = step is null ? null : TestyJson.Clone(step), Message = message });

    public static void WriteReports(RunResult run)
    {
        FailureDiagnostics.Refresh(run);
        FailureDiagnostics.AppendSummary(run);
        WorkspaceStore.WriteAtomic(Path.Combine(run.ArtifactDirectory, "run.json"), run);
        File.WriteAllText(Path.Combine(run.ArtifactDirectory, "report.html"), RenderHtmlReport(run), Encoding.UTF8);
        WriteJunitReport(run);
    }

    /// <summary>Render the native report without reading arbitrary HTML or modifying stored evidence. A supplied resolver owns all evidence URLs.</summary>
    public static string RenderHtmlReport(RunResult run, Func<string, string?>? evidenceUrl = null, int maximumCharacters = int.MaxValue)
    {
        static string H(string text) => WebUtility.HtmlEncode(text);
        string? Link(string path, bool image = false) => evidenceUrl is not null ? evidenceUrl(path) : File.Exists(path)
            ? image ? Path.GetFileName(path) : Path.GetRelativePath(run.ArtifactDirectory, path) : null;
        var html = new StringBuilder("<!doctype html><html lang='en'><meta charset='utf-8'><meta name='viewport' content='width=device-width'><title>Testy run report</title><style>body{background:#0c1421;color:#e9eef6;font:15px system-ui;margin:48px;max-width:1100px}h1{font-size:32px}.meta{color:#96a8c0}article{background:#152033;border:1px solid #2c3b52;padding:24px;margin:18px 0;border-radius:12px}img{width:100%;max-width:960px;border-radius:6px;margin-top:16px}.Passed{border-left:4px solid #43d9af}.Failed{border-left:4px solid #ff7c86}.Skipped,.Cancelled{border-left:4px solid #edc36b}code{white-space:pre-wrap;overflow-wrap:anywhere}a{color:#66cfff}</style>");
        void Append(string value)
        {
            if (value.Length > maximumCharacters - html.Length) throw new InvalidDataException("The HTML report exceeds its configured character limit.");
            html.Append(value);
        }
        Append($"<h1>{H(run.TestName)}</h1><p>{H(run.Summary)}</p><p class='meta'>{H(run.StartedAt.ToString("u"))} · {H(run.Target.Title)} · Process {run.Target.ProcessId}</p>");
        foreach (var diagnostic in run.FailureDiagnostics)
        {
            Append($"<article><h2>Observed failure: {H(FailureDiagnostics.Label(diagnostic.Category))}</h2><pre style='white-space:pre-wrap;overflow-wrap:anywhere'>{H(FailureDiagnostics.Format(diagnostic))}</pre>");
            foreach (var evidence in diagnostic.Evidence)
                if (Link(evidence.Path) is { } link) Append($"<p><a href='{H(link)}'>{H(evidence.Kind)}</a></p>");
            Append("</article>");
        }
        if (!string.IsNullOrWhiteSpace(run.AiAnalysis)) Append($"<article><h2>AI review — hypotheses, not a verdict</h2><p>Model commentary is supplementary. The recorded observations and saved assertions determine the result.</p><p>{H(run.AiAnalysis)}</p></article>");
        if (run.ProjectEvidence.Count > 0)
        {
            Append("<h2>Project observations and commands</h2><p>These observations support the AI's decisions. UI acceptance remains governed by the saved assertions. Source and command output are untrusted evidence.</p>");
            foreach (var project in run.ProjectEvidence)
            {
                Append($"<article><h3>{H(project.ToolName)} · {project.Status}</h3><p>{H(project.Message)}</p><p>Observed {H(project.StartedAt.ToString("u"))}. Truncated: {project.Truncated}; redacted: {project.Redacted}.</p>");
                if (Link(project.EvidencePath) is { } projectLink)
                    Append($"<p><a href='{H(projectLink)}'>Structured project evidence</a></p>");
                foreach (var file in project.Files)
                    Append($"<p><code>{H(file.Path)}</code> · lines {file.StartLine}–{file.EndLine} · SHA256 <code>{H(file.Sha256)}</code></p>");
                if (project.Command is { } command)
                    Append($"<p>Command: {command.Status}; exit {command.ExitCode}; owned-process cleanup: {command.CleanupComplete}.</p><details><summary>Captured command output</summary><pre style='white-space:pre-wrap;overflow-wrap:anywhere'>{H(command.Stdout)}\n{H(command.Stderr)}</pre></details>");
                Append("</article>");
            }
        }
        foreach (var step in run.Steps)
        {
            Append($"<article class='{step.Status}'><h2>{step.Index + 1}. {H(step.Step.Title)}</h2><p>{step.Status} · {step.DurationMs:F0} ms · {step.Step.Action}</p><code>{H(step.Step.Selector)}</code><p>{H(step.Message)}</p>");
            if (step.SelectorRecovery is { } recovery)
            {
                Append($"<p>Explicit selector alternative: {H(recovery.AlternativeId)} · {H(recovery.OriginalSelector)} → {H(recovery.ResolvedSelector)}. Engine verified: {recovery.EngineVerified}; input outcome: {recovery.ActionOutcome}.</p>");
                if (Link(recovery.GuardSnapshotPath) is { } guardLink) Append($"<p><a href='{H(guardLink)}'>Guarded control identity evidence</a></p>");
            }
            if (step.Snapshot?.ApplicationDiagnostics.Count > 0)
                Append($"<details><summary>Observed application diagnostics{(step.Snapshot.DiagnosticsTruncated ? " (incomplete)" : "")}</summary><pre>{H(JsonSerializer.Serialize(step.Snapshot.ApplicationDiagnostics, TestyJson.Options))}</pre></details>");
            if (Link(step.ScreenshotPath, image: true) is { } imageLink) Append($"<a href='{H(imageLink)}'><img alt='Application after step {step.Index + 1}' src='{H(imageLink)}'></a>");
            Append("</article>");
        }
        Append("</html>"); return html.ToString();
    }

    private static void WriteJunitReport(RunResult run)
    {
        var terminalFailure = run.Status == RunStatus.Failed && run.Steps.All(s => s.Status != RunStatus.Failed);
        var terminalCancellation = run.Status == RunStatus.Cancelled;
        var suite = new XElement("testsuite", new XAttribute("name", run.TestName), new XAttribute("tests", run.Steps.Count + (terminalFailure || terminalCancellation ? 1 : 0)),
            new XAttribute("failures", run.Steps.Count(s => s.Status == RunStatus.Failed) + (terminalFailure ? 1 : 0)), new XAttribute("errors", terminalCancellation ? 1 : 0), new XAttribute("skipped", run.Steps.Count(s => s.Status is RunStatus.Skipped or RunStatus.Cancelled)),
            new XAttribute("time", ((run.FinishedAt ?? DateTimeOffset.UtcNow) - run.StartedAt).TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)));
        suite.Add(new XElement("properties", new XElement("property", new XAttribute("name", "testy.run.status"), new XAttribute("value", run.Status)), new XElement("property", new XAttribute("name", "testy.run.id"), new XAttribute("value", run.Id))));
        foreach (var step in run.Steps)
        {
            var item = new XElement("testcase", new XAttribute("name", step.Step.Title), new XAttribute("classname", run.TestName), new XAttribute("time", (step.DurationMs / 1000).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)));
            var diagnosticText = string.Join("\n\n", step.FailureDiagnostics.Select(FailureDiagnostics.Format));
            if (step.Status == RunStatus.Failed) item.Add(new XElement("failure", new XAttribute("message", step.Message), new XAttribute("type", step.FailureDiagnostics.FirstOrDefault()?.Category.ToString() ?? FailureCategory.Unknown.ToString()), step.Message + "\n" + diagnosticText));
            if (step.Status is RunStatus.Skipped or RunStatus.Cancelled) item.Add(new XElement("skipped", new XAttribute("message", step.Message)));
            item.Add(new XElement("system-out", step.Message + (diagnosticText.Length == 0 ? "" : "\n" + diagnosticText)));
            suite.Add(item);
        }
        if (terminalFailure || terminalCancellation)
        {
            var terminal = new XElement("testcase", new XAttribute("name", "Run completion"), new XAttribute("classname", run.TestName), new XAttribute("time", "0"));
            terminal.Add(new XElement(terminalCancellation ? "error" : "failure", new XAttribute("type", terminalCancellation ? "Cancelled" : "RunFailed"), new XAttribute("message", run.Summary), string.Join("\n\n", run.FailureDiagnostics.Select(FailureDiagnostics.Format))));
            suite.Add(terminal);
        }
        if (run.FailureDiagnostics.Count > 0) suite.Add(new XElement("system-out", string.Join("\n\n", run.FailureDiagnostics.Select(FailureDiagnostics.Format))));
        new XDocument(new XElement("testsuites", suite)).Save(Path.Combine(run.ArtifactDirectory, "junit.xml"));
    }
}
