using System.Diagnostics;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Testy.Core;
using Testy.Windows;

namespace Testy.Cli;

internal sealed class AdvancedWpfReport
{
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public int Repetitions { get; set; }
    public int PlannedChecks => 54 * Repetitions;
    public bool CompletedAllScenarios { get; set; }
    public bool Cancelled { get; set; }
    public string Mode => "Explicit replay on owned WPF Control Lab; no model calls";
    public bool Passed => FinishedAt is not null && CompletedAllScenarios && !Cancelled && Checks.Count == PlannedChecks && Checks.All(c => c.Passed);
    public List<AdvancedWpfCheck> Checks { get; set; } = [];
}

internal sealed class AdvancedWpfCheck
{
    public string Driver { get; set; } = "";
    public int Repetition { get; set; }
    public string Name { get; set; } = "";
    public RunStatus Expected { get; set; }
    public RunStatus Actual { get; set; }
    public bool Passed { get; set; }
    public double DurationMs { get; set; }
    public int EvidenceImages { get; set; }
    public string ArtifactDirectory { get; set; } = "";
    public string Message { get; set; } = "";
    public List<FailureCategory> Categories { get; set; } = [];
    public List<int> CaptureTransitionSteps { get; set; } = [];
    public bool ReadOnlyStateUnchanged { get; set; }
    public string ModalGuardError { get; set; } = "";
    public List<object> RecyclingObservations { get; set; } = [];
    public List<string> ObserverErrors { get; set; } = [];
}

/// <summary>Real framework integration checks. Unsupported observations must never become inferred values.</summary>
internal static class AdvancedWpfVerifier
{
    private sealed record Scenario(string Name, TestStep[] Steps, RunStatus Expected = RunStatus.Passed,
        FailureCategory? Category = null, bool ReadOnly = false, bool Metadata = false, bool Cancel = false, bool Modal = false,
        bool DispatchFailure = false, string FailureContains = "", bool Recycle = false);

    public static async Task<AdvancedWpfReport> VerifyAsync(string executable, string artifacts, int repetitions, CancellationToken cancellationToken)
    {
        if (!File.Exists(executable) || !Path.GetFileName(executable).Equals("Testy.WpfLab.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only the included Testy.WpfLab.exe fixture is allowed.");
        if (repetitions is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(repetitions));
        artifacts = Path.GetFullPath(artifacts); Directory.CreateDirectory(artifacts);
        var report = new AdvancedWpfReport { Repetitions = repetitions };
        void Save() => WorkspaceStore.WriteAtomic(Path.Combine(artifacts, "wpf-report.json"), report);
        Save();
        try
        {
            foreach (bool probe in new[] { false, true })
            {
                string driverName = probe ? "WPF in-process probe" : "Windows UI Automation";
                string driverFolder = Path.Combine(artifacts, probe ? "probe" : "uia"); Directory.CreateDirectory(driverFolder);
                cancellationToken.ThrowIfCancellationRequested();
                using var process = Process.Start(new ProcessStartInfo(Path.GetFullPath(executable))
                { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!, WindowStyle = ProcessWindowStyle.Hidden })
                    ?? throw new InvalidOperationException("Owned WPF Lab failed to start.");
                try
                {
                    await WaitForWindow(process, cancellationToken);
                    using ITargetDriver driver = probe ? new WpfProbeDriver() : new UiAutomationDriver();
                    await driver.AttachAsync(process.Id, cancellationToken);
                    var initial = await WaitForControls(driver, cancellationToken);
                    WorkspaceStore.WriteAtomic(Path.Combine(driverFolder, "initial-tree.json"), initial);
                    var initialImage = await driver.CaptureAsync(Path.Combine(driverFolder, "initial.png"), cancellationToken);
                    ValidateImage(initialImage, initial);
                    for (int repetition = 1; repetition <= repetitions; repetition++)
                    {
                        var scenarios = Scenarios(probe).ToArray();
                        Check(scenarios.Length == 27, "The planned scenario count changed without updating the report guard.");
                        foreach (var scenario in scenarios)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var check = new AdvancedWpfCheck { Name = scenario.Name, Expected = scenario.Expected, Driver = driverName, Repetition = repetition };
                            var watch = Stopwatch.StartNew();
                            try
                            {
                                using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                                bool requestedCancellation = false;
                                var progress = new InlineProgress<RunProgress>(p =>
                                {
                                    if (scenario.Cancel && !requestedCancellation && p.Step?.Step.Action == StepAction.Wait && p.Step.Status == RunStatus.Running)
                                    { requestedCancellation = true; stop.CancelAfter(150); }
                                });
                                var test = new TestCase { Name = scenario.Name, Intent = scenario.Name, Category = "Advanced real WPF controls",
                                    Steps = [Click("ResetLab"), Text("FixtureState", "selections=0; viewports=0; trees=0; actions=0"), .. scenario.Steps] };
                                UiSnapshot? baseline = null;
                                var transitionTrees = new Dictionary<int, UiSnapshot>();
                                bool modalGuardPassed = false;
                                var runner = new TestRunner(driver, Path.Combine(driverFolder, $"repeat-{repetition:00}"));
                                runner.StepObserver = async (run, token) =>
                                {
                                    var step = run.Steps[^1];
                                    try
                                    {
                                    if (step.Index == 1 && step.Status == RunStatus.Passed) baseline = step.Snapshot;
                                    if (scenario.Recycle && step.Status == RunStatus.Passed && step.Step.Action == StepAction.AssertExists)
                                    {
                                        var row = UiSelectors.Find(step.Snapshot!, step.Step.Selector).Single();
                                        string expectedName = step.Step.Selector == "id:Order-0001" ? "Order 0001" : "Order 1999";
                                        Check(row.Name == expectedName && ValidIdentity(step.Snapshot!, row, probe) && !row.IsOffscreen, "Recycled row identity/key did not match the freshly requested visible order.");
                                        check.RecyclingObservations.Add(new { stepIndex = step.Index, row.AutomationId, row.Name, row.RuntimeId, row.IsOffscreen });
                                    }
                                    if (step.Status == RunStatus.Passed && step.Step.Action == StepAction.AssertProperty && step.Step.Selector.StartsWith("id:Cell-", StringComparison.Ordinal))
                                    {
                                        var cell = UiSelectors.Find(step.Snapshot!, step.Step.Selector).Single();
                                        Check(ValidIdentity(step.Snapshot!, cell, probe) && !cell.IsOffscreen, "Cell property did not come from the fresh visible cell identity.");
                                    }
                                    if (step.Snapshot is { } recorded && File.Exists(step.ScreenshotPath))
                                    {
                                        var (width, height) = Dimensions(step.ScreenshotPath);
                                        if (!MatchesWindow(recorded, width, height))
                                        {
                                            var after = await driver.SnapshotAsync(token);
                                            WorkspaceStore.WriteAtomic(Path.Combine(run.ArtifactDirectory, $"step-{step.Index + 1:000}-capture-window-tree.json"), after);
                                            if (MatchesBounds(recorded.ScreenshotBounds, width, height) && MatchesWindow(after, width, height))
                                            { transitionTrees[step.Index] = after; check.CaptureTransitionSteps.Add(step.Index); }
                                        }
                                    }
                                    if (scenario.Modal && step.Step.Selector == "id:CloseModal" && step.Step.Action == StepAction.AssertExists && step.Status == RunStatus.Passed)
                                    {
                                        var before = await driver.SnapshotAsync(token);
                                        Check(!UiSelectors.Find(before, "id:TemplatedAction").Single().IsEnabled, "The modal owner exposes an enabled action.");
                                        string state = Value(before, "FixtureState");
                                        WorkspaceStore.WriteAtomic(Path.Combine(run.ArtifactDirectory, "modal-guard-before.json"), before);
                                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(1500);
                                        try { await driver.ExecuteAsync(Click("TemplatedAction"), deadline.Token); throw new InvalidOperationException("An action behind the modal was accepted."); }
                                        catch (InvalidOperationException ex) when (ex.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase)) { check.ModalGuardError = ex.Message; }
                                        var after = await driver.SnapshotAsync(token);
                                        WorkspaceStore.WriteAtomic(Path.Combine(run.ArtifactDirectory, "modal-guard-after.json"), after);
                                        var image = await driver.CaptureAsync(Path.Combine(run.ArtifactDirectory, "modal-guard.png"), token); ValidateImage(image, after);
                                        Check(Value(after, "FixtureState") == state, "Blocked owner action changed counters."); modalGuardPassed = check.ModalGuardError.Length > 0;
                                    }
                                    }
                                    catch (Exception ex) { check.ObserverErrors.Add($"Step {step.Index + 1}: {ex.Message}"); }
                                };
                                var result = await runner.RunAsync(test, progress, stop.Token);
                                check.Actual = result.Status; check.ArtifactDirectory = result.ArtifactDirectory; check.Message = result.Summary;
                                check.Categories = result.FailureDiagnostics.Select(d => d.Category).Distinct().ToList();
                                Check(result.Status == scenario.Expected, $"Expected {scenario.Expected}, observed {result.Status}.");
                                Check(result.Steps.Count == test.Steps.Count, "Result omitted a saved step.");
                                Check(check.ObserverErrors.Count == 0, "Evidence observer verification failed: " + string.Join("; ", check.ObserverErrors));
                                Check(result.Steps.Take(2).All(s => s.Status == RunStatus.Passed), "Reset/readiness did not pass; the intended scenario was not exercised.");
                                foreach (var step in result.Steps.Where(s => s.Status != RunStatus.Skipped))
                                {
                                    Check(step.Snapshot is not null && !step.Snapshot.IsTruncated, "Executed step lacks complete snapshot evidence.");
                                    ValidateImage(step.ScreenshotPath, transitionTrees.GetValueOrDefault(step.Index) ?? step.Snapshot!); check.EvidenceImages++;
                                    if (step.Status == RunStatus.Passed && step.Step.Action is StepAction.AssertItemExists or StepAction.AssertItemAbsent)
                                    {
                                        Check(step.ItemLookup is not null && step.ItemLookup.ObservedAt >= step.StartedAt, "Logical item assertion lacks its separately timestamped provider observation.");
                                        Check(step.ItemLookup!.Status == (step.Step.Action == StepAction.AssertItemExists ? ItemLookupStatus.Unique : ItemLookupStatus.Missing), "Logical item evidence does not support the assertion outcome.");
                                    }
                                }
                                foreach (string name in new[] { "run.json", "report.html", "junit.xml" }) Check(File.Exists(Path.Combine(result.ArtifactDirectory, name)), $"Missing {name}.");
                                if (scenario.Expected == RunStatus.Failed)
                                {
                                    var failed = result.Steps.Single(s => s.Status == RunStatus.Failed);
                                    Check(failed.Index == 2, "A different step failed before the intended negative check.");
                                    Check(result.Steps.Where(s => s.Index > failed.Index).All(s => s.Status == RunStatus.Skipped), "Later mutation ran after failure.");
                                    if (scenario.Category is { } category) Check(failed.FailureDiagnostics.Any(d => d.Category == category), $"Expected diagnostic {category}; observed {string.Join(", ", check.Categories)}.");
                                    if (!scenario.DispatchFailure) Check(failed.FailureDiagnostics.All(d => d.ActionOutcome == ActionOutcome.NotDispatched), "Rejected assertion/capability check claims dispatched mutation.");
                                    else Check(failed.FailureDiagnostics.All(d => d.ActionOutcome != ActionOutcome.Completed), "Rejected driver operation claims completed mutation.");
                                    if (scenario.FailureContains.Length > 0) Check(failed.Message.Contains(scenario.FailureContains, StringComparison.OrdinalIgnoreCase), "The driver rejected the action for a different reason than the intended guard.");
                                }
                                var afterScenario = await driver.SnapshotAsync(cancellationToken);
                                WorkspaceStore.WriteAtomic(Path.Combine(result.ArtifactDirectory, "verification-after.json"), afterScenario);
                                if (scenario.ReadOnly || scenario.Cancel)
                                {
                                    Check(baseline is not null, "No settled baseline snapshot.");
                                    AssertReadOnlyUnchanged(baseline!, afterScenario); check.ReadOnlyStateUnchanged = true;
                                }
                                if (scenario.Metadata) ValidateMetadata(afterScenario, probe);
                                if (scenario.Modal) Check(modalGuardPassed, "The direct modal owner guard did not pass.");
                                if (scenario.Recycle) Check(check.RecyclingObservations.Count == 3, "Recycling roundtrip did not capture each fresh row identity.");
                                if (scenario.Cancel) Check(requestedCancellation && watch.Elapsed < TimeSpan.FromSeconds(12) && result.Steps[^1].Status == RunStatus.Skipped, "Cancellation did not promptly skip the later action.");
                                check.Passed = true;
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception ex) { check.Message += " Verification: " + ex.Message; }
                            check.DurationMs = watch.Elapsed.TotalMilliseconds; report.Checks.Add(check); Save();
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { report.Checks.Add(new() { Driver = driverName, Name = "Fixture setup", Expected = RunStatus.Passed, Actual = RunStatus.Failed, Message = ex.ToString() }); Save(); }
                finally { await CloseOwned(process); }
            }
            cancellationToken.ThrowIfCancellationRequested(); report.CompletedAllScenarios = report.Checks.Count == report.PlannedChecks;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { report.Cancelled = true; throw; }
        finally { report.FinishedAt = DateTimeOffset.UtcNow; Save(); }
        return report;
    }

    private static IEnumerable<Scenario> Scenarios(bool probe)
    {
        yield return new("Passive metadata and password redaction", [Property("ReadOnlyReference", "uia.isReadOnly", true), Step(StepAction.Screenshot, "")], ReadOnly: true, Metadata: true);
        yield return new("Grid logical lookup never realizes or scrolls", [Item(StepAction.AssertItemExists, "OrdersGrid", "Order 1999"), Item(StepAction.AssertItemAbsent, "OrdersGrid", "Order 9999")], ReadOnly: true);
        yield return new("List logical lookup never realizes or scrolls", [Item(StepAction.AssertItemExists, "OrdersList", "Order 1999"), Item(StepAction.AssertItemAbsent, "OrdersList", "Order 9999")], ReadOnly: true);
        yield return Negative("Duplicate grid item is ambiguous", Item(StepAction.AssertItemExists, "OrdersGrid", "Duplicate entry"), FailureCategory.SelectorAmbiguous);
        yield return Negative("Duplicate list item is ambiguous", Item(StepAction.AssertItemExists, "OrdersList", "Duplicate entry"), FailureCategory.SelectorAmbiguous);
        yield return Negative("Realized tree cannot prove virtual item absence", Step(StepAction.AssertNotExists, "Order-1999"), FailureCategory.EvidenceUnavailable);
        yield return new("Explicit grid realization and scrolling", [Item(StepAction.RealizeItem, "OrdersGrid", "Order 1999"), Step(StepAction.ScrollIntoView, "Order-1999"), Step(StepAction.AssertExists, "Order-1999")]);
        yield return new("Explicit list realization and selection", [Item(StepAction.RealizeItem, "OrdersList", "Order 1999"), Step(StepAction.ScrollIntoView, "ListOrder-1999"), Step(StepAction.Select, "OrdersList", "Order 1999"), Property("ListOrder-1999", "uia.isSelected", true), Text("SelectionState", "Grid selection: none; List selection: Order-1999")]);
        yield return new("Filtered sorted reordered grid uses current logical view", [Click("SortDescending"), Click("FilterHigh"), Click("ReorderColumns"), Item(StepAction.AssertItemAbsent, "OrdersGrid", "Order 0001"), Item(StepAction.AssertItemExists, "OrdersGrid", "Order 1999"), Property("OrdersGrid", "uia.rowCount", 501), Item(StepAction.RealizeItem, "OrdersGrid", "Order 1999"), Step(StepAction.ScrollIntoView, "Order-1999"), Step(StepAction.AssertExists, "Order-1999")]);
        yield return new("Nested tree expands and collapses explicitly", [Property("TreeRoot", "uia.expandCollapseState", "Collapsed"), Step(StepAction.Expand, "TreeRoot"), Step(StepAction.Expand, "TreeBranch"), Property("TreeRoot", "uia.expandCollapseState", "Expanded"), Property("TreeBranch", "uia.expandCollapseState", "Expanded"), Step(StepAction.AssertExists, "TreeLeaf"), Step(StepAction.Collapse, "TreeBranch"), Property("TreeBranch", "uia.expandCollapseState", "Collapsed"), Step(StepAction.Collapse, "TreeRoot"), Property("TreeRoot", "uia.expandCollapseState", "Collapsed")]);
        yield return new("Explicit percentage scroll reaches both ends", [Step(StepAction.ScrollPercent, "LongDocument", "{\"vertical\":100}"), Property("LongDocument", "uia.verticalScrollPercent", 100), Step(StepAction.ScrollPercent, "LongDocument", "{\"vertical\":0}"), Property("LongDocument", "uia.verticalScrollPercent", 0)]);
        yield return Negative("Incorrect read-only property cannot pass", Property("ReadOnlyReference", "uia.isReadOnly", false, 4000), FailureCategory.AssertionMismatch);
        if (probe) yield return new("Probe observes validation before and after edit", [Property("ValidatedQuantity", "wpf.validationHasError", true), Step(StepAction.TypeText, "ValidatedQuantity", "42"), Property("ValidatedQuantity", "wpf.validationHasError", false)]);
        else yield return Negative("UIA cannot invent WPF validation state", Property("ValidatedQuantity", "wpf.validationHasError", true), FailureCategory.CapabilityUnavailable);
        yield return Negative("Password property observation is redacted", Property("SecretBox", "uia.isReadOnly", false), FailureCategory.EvidenceUnavailable);
        yield return Negative("Missing logical item cannot pass existence", Item(StepAction.AssertItemExists, "OrdersGrid", "Order 9999", 4000), FailureCategory.AssertionMismatch);
        yield return Negative("Unsupported property cannot become a value", Property("TemplatedAction", "uia.row", 0), FailureCategory.CapabilityUnavailable);
        yield return Negative("Unsupported expand cannot invoke a button", Step(StepAction.Expand, "TemplatedAction"), FailureCategory.CapabilityUnavailable);
        yield return new("Cancellation prevents later templated action", [Step(StepAction.Wait, "", "3000", 3000), Click("TemplatedAction")], RunStatus.Cancelled, Cancel: true);
        yield return new("Templated button retains semantic invocation", [Click("TemplatedAction"), Text("FixtureStatus", "Templated action invoked"), Text("FixtureState", "selections=0; viewports=0; trees=0; actions=1")]);
        yield return new("Modal disables owner and rejects direct mutation", [Click("OpenModal"), Step(StepAction.AssertExists, "CloseModal"), Click("CloseModal"), Text("FixtureState", "selections=0; viewports=0; trees=0; actions=1")], Modal: true);
        yield return RejectedMutation("Duplicate grid realization dispatches no change", Item(StepAction.RealizeItem, "OrdersGrid", "Duplicate entry"), "ambiguous");
        yield return RejectedMutation("Missing grid realization dispatches no change", Item(StepAction.RealizeItem, "OrdersGrid", "Order 9999"), "no provider-exposed logical peer");
        yield return RejectedMutation("Duplicate list realization dispatches no change", Item(StepAction.RealizeItem, "OrdersList", "Duplicate entry"), "ambiguous");
        yield return RejectedMutation("Missing list realization dispatches no change", Item(StepAction.RealizeItem, "OrdersList", "Order 9999"), "no provider-exposed logical peer");
        yield return RejectedMutation("Leaf expansion is rejected without state change", Step(StepAction.Expand, "StandaloneLeaf"), "leaf");
        yield return new("Recycling roundtrip rechecks current order identity", [Item(StepAction.RealizeItem, "OrdersGrid", "Order 1999"), Step(StepAction.ScrollIntoView, "Order-1999"), Step(StepAction.AssertExists, "Order-1999"),
            Item(StepAction.RealizeItem, "OrdersGrid", "Order 0001"), Step(StepAction.ScrollIntoView, "Order-0001"), Step(StepAction.AssertExists, "Order-0001"),
            Item(StepAction.RealizeItem, "OrdersGrid", "Order 1999"), Step(StepAction.ScrollIntoView, "Order-1999"), Step(StepAction.AssertExists, "Order-1999")], Recycle: true);
        // WPF GridItem.Column is the Columns collection index; display reorder does not change it.
        // GridItem.Row is the index in the current sorted/filtered Items view.
        yield return new("Cell properties follow sorting filtering and recycling", [
            Property("Cell-Order-0001-Key", "uia.row", 0), Property("Cell-Order-0001-Key", "uia.column", 0), Property("Cell-Order-0001-Key", "uia.isReadOnly", true), Property("Cell-Order-0001-Key", "uia.value", "Order-0001"),
            Click("ReorderColumns"), Property("Cell-Order-0001-Key", "uia.column", 0), Click("SortDescending"), Click("FilterHigh"),
            Item(StepAction.RealizeItem, "OrdersGrid", "Order 1999"), Step(StepAction.ScrollIntoView, "Order-1999"),
            Property("Cell-Order-1999-Description", "uia.row", 1), Property("Cell-Order-1999-Description", "uia.column", 1), Property("Cell-Order-1999-Description", "uia.isReadOnly", true), Property("Cell-Order-1999-Description", "uia.value", "Order 1999"),
            Item(StepAction.RealizeItem, "OrdersGrid", "Order 1500"), Step(StepAction.ScrollIntoView, "Order-1500"),
            Property("Cell-Order-1500-Key", "uia.row", 500), Property("Cell-Order-1500-Key", "uia.column", 0), Property("Cell-Order-1500-Key", "uia.isReadOnly", true), Property("Cell-Order-1500-Key", "uia.value", "Order-1500"),
            Item(StepAction.RealizeItem, "OrdersGrid", "Order 1999"), Step(StepAction.ScrollIntoView, "Order-1999"),
            Property("Cell-Order-1999-Description", "uia.row", 1), Property("Cell-Order-1999-Description", "uia.column", 1), Property("Cell-Order-1999-Description", "uia.value", "Order 1999")]);
    }

    private static Scenario Negative(string name, TestStep step, FailureCategory category) => new(name, [step, Click("TemplatedAction")], RunStatus.Failed, category, ReadOnly: true);
    private static Scenario RejectedMutation(string name, TestStep step, string reason) => new(name, [step, Click("TemplatedAction")], RunStatus.Failed, ReadOnly: true, DispatchFailure: true, FailureContains: reason);
    private static TestStep Step(StepAction action, string id, string value = "", int timeout = 7000) => new() { Title = action + " " + id, Action = action, Selector = id.Length == 0 ? "" : "id:" + id, Value = value, TimeoutMs = timeout };
    private static TestStep Click(string id) => Step(StepAction.Click, id);
    private static TestStep Text(string id, string value) => Step(StepAction.AssertText, id, value);
    private static TestStep Property(string id, string property, object value, int timeout = 7000) => Step(StepAction.AssertProperty, id, JsonSerializer.Serialize(new { property, equals = value }), timeout);
    private static TestStep Item(StepAction action, string id, string label, int timeout = 7000) => Step(action, id, JsonSerializer.Serialize(new { item = new { label } }), timeout);
    private static string Value(UiSnapshot snapshot, string id)
    { var element = UiSelectors.Find(snapshot, "id:" + id).Single(); return element.Value.Length > 0 ? element.Value : element.Name; }
    // Probe object identities are diagnostic only and cannot match numeric native UIA input receipts.
    private static bool ValidIdentity(UiSnapshot snapshot, UiElementInfo element, bool probe) => probe
        ? element.RuntimeId.StartsWith($"wpf:{snapshot.Target.ProcessId}:", StringComparison.Ordinal)
        : element.RuntimeId.Length > 0 && !element.RuntimeId.StartsWith("wpf:", StringComparison.Ordinal);
    private static void AssertReadOnlyUnchanged(UiSnapshot before, UiSnapshot after)
    {
        Check(Value(before, "FixtureState") == Value(after, "FixtureState"), "Read-only observation changed fixture counters.");
        Check(Value(before, "SelectionState") == Value(after, "SelectionState"), "Read-only observation changed selection.");
        Check(before.FocusedSelector == after.FocusedSelector, "Read-only observation changed keyboard focus.");
        foreach (string id in new[] { "OrdersGrid", "OrdersList", "LongDocument" })
            foreach (string property in new[] { "uia.verticalScrollPercent", "uia.horizontalScrollPercent" })
            {
                var a = UiSelectors.Find(before, "id:" + id).Single().Properties.GetValueOrDefault(property);
                var b = UiSelectors.Find(after, "id:" + id).Single().Properties.GetValueOrDefault(property);
                Check(a?.Status == b?.Status && a?.Value?.GetRawText() == b?.Value?.GetRawText(), $"Read-only observation changed {id}/{property}.");
            }
    }
    private static void ValidateMetadata(UiSnapshot snapshot, bool probe)
    {
        var secret = UiSelectors.Find(snapshot, "id:SecretBox").Single();
        Check(secret.IsPassword, "Password control was not identified.");
        Check(!JsonSerializer.Serialize(snapshot, TestyJson.Options).Contains("fixture-secret-42", StringComparison.Ordinal), "Password content leaked into snapshot evidence.");
        foreach (string id in new[] { "OrdersGrid", "OrdersList" })
        {
            var container = UiSelectors.Find(snapshot, "id:" + id).Single();
            Check(container.ChildCoverage != ChildCoverage.Complete, "A virtualized collection falsely advertises complete realized children.");
            Check(container.Capabilities.Contains("ItemContainer"), "Virtual collection omits logical ItemContainer capability.");
        }
        if (probe)
        {
            var grid = UiSelectors.Find(snapshot, "id:OrdersGrid").Single();
            Check(grid.Properties.TryGetValue("wpf.enableRowVirtualization", out var rows) && rows.Status == UiPropertyStatus.Known && rows.Value?.ValueKind == JsonValueKind.True, "Probe row virtualization property is not known true.");
        }
    }
    private static async Task<UiSnapshot> WaitForControls(ITargetDriver driver, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        { var snapshot = await driver.SnapshotAsync(token); if (UiSelectors.Find(snapshot, "id:ResetLab").Count == 1) return snapshot; if (watch.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("WPF Lab controls did not become accessible."); await Task.Delay(100, token); }
    }
    private static async Task WaitForWindow(Process process, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        { token.ThrowIfCancellationRequested(); process.Refresh(); if (process.HasExited) throw new InvalidOperationException("Owned fixture exited before showing a window."); if (process.MainWindowHandle != 0) return; if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Owned fixture did not show a window."); await Task.Delay(100, token); }
    }
    private static async Task CloseOwned(Process process)
    {
        if (process.HasExited) return; process.CloseMainWindow(); using var deadline = new CancellationTokenSource(4000);
        try { await process.WaitForExitAsync(deadline.Token); } catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: false); }
    }
    private static void ValidateImage(string path, UiSnapshot snapshot)
    {
        Check(File.Exists(path), "Screenshot missing."); using var stream = File.OpenRead(path);
        var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        Check(frame.PixelWidth >= 300 && frame.PixelHeight >= 160 && MatchesWindow(snapshot, frame.PixelWidth, frame.PixelHeight), "Screenshot does not match a full observed fixture window.");
        var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0); int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, stride, 0); int total = 0, light = 0; var colors = new HashSet<int>();
        for (int y = bitmap.PixelHeight * 15 / 100; y < bitmap.PixelHeight * 9 / 10; y += 7)
            for (int x = bitmap.PixelWidth / 10; x < bitmap.PixelWidth * 9 / 10; x += 7)
            { int p = y * stride + x * 4; total++; if (Math.Max(pixels[p], Math.Max(pixels[p + 1], pixels[p + 2])) > 32) light++; colors.Add(pixels[p] | pixels[p + 1] << 8 | pixels[p + 2] << 16); }
        Check(light > total / 2 && colors.Count >= 8, "Screenshot client is blank or lacks visible fixture detail.");
    }
    private static (int Width, int Height) Dimensions(string path)
    { using var stream = File.OpenRead(path); var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0]; return (frame.PixelWidth, frame.PixelHeight); }
    private static bool MatchesBounds(ElementBounds bounds, int width, int height) => Math.Abs(bounds.Width - width) < 2 && Math.Abs(bounds.Height - height) < 2;
    private static bool MatchesWindow(UiSnapshot snapshot, int width, int height) => snapshot.Elements.Any(e => e.Depth == 0 && e.ControlType == "Window" && MatchesBounds(e.Bounds, width, height));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}
