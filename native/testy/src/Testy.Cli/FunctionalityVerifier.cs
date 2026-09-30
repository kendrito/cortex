using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Testy.Core;
using Testy.Windows;

namespace Testy.Cli;

internal sealed class FunctionalityReport
{
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public int Repetitions { get; set; }
    public int PlannedChecks => Repetitions * 19;
    public bool CompletedAllScenarios { get; set; }
    public bool Cancelled { get; set; }
    public string ExecutionMode { get; set; } = "Explicit deterministic replay on real owned desktop applications";
    public int ModelCalls => 0;
    public bool Passed => FinishedAt is not null && CompletedAllScenarios && !Cancelled && Checks.Count == PlannedChecks && Checks.All(c => c.Passed);
    public int PassedChecks => Checks.Count(c => c.Passed);
    public int FailedChecks => Checks.Count(c => !c.Passed);
    public int ExpectedFailureRuns => Checks.Count(c => c.Expected == RunStatus.Failed);
    public List<FunctionalityCheck> Checks { get; set; } = [];
}

internal sealed class FunctionalityCheck
{
    public int Repetition { get; set; }
    public string Fixture { get; set; } = "";
    public string Driver { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Passed { get; set; }
    public RunStatus Expected { get; set; }
    public RunStatus Actual { get; set; }
    public double DurationMs { get; set; }
    public int Steps { get; set; }
    public int EvidenceImages { get; set; }
    public List<FailureCategory> DiagnosticCategories { get; set; } = [];
    public string ModalGuardError { get; set; } = "";
    public List<int> CaptureWindowTransitionSteps { get; set; } = [];
    public string Message { get; set; } = "";
    public string ArtifactDirectory { get; set; } = "";
}

/// <summary>Provider-independent, repeated tests of two real owned UI technologies.</summary>
internal static class FunctionalityVerifier
{
    private sealed record Scenario(TestCase Test, RunStatus Expected, string FailureHint = "", string UnchangedId = "", string UnchangedValue = "", bool CancelOnWait = false, bool CheckNewRuntimeId = false, bool VerifyModalGuard = false);

    public static async Task<FunctionalityReport> VerifyAsync(string ordersExecutable, string labExecutable, string artifacts, int repetitions, CancellationToken cancellationToken)
    {
        ValidateFixture(ordersExecutable, "Testy.OrderLab.exe"); ValidateFixture(labExecutable, "Testy.TestLab.exe");
        if (repetitions is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(repetitions), "Choose 1–20 repetitions.");
        artifacts = Path.GetFullPath(artifacts); Directory.CreateDirectory(artifacts);
        var report = new FunctionalityReport { Repetitions = repetitions };
        void Save() => WorkspaceStore.WriteAtomic(Path.Combine(artifacts, "functionality-report.json"), report);
        Save();
        try
        {
            foreach (var fixture in new[] { (Path: ordersExecutable, Name: "WinForms Orders", Folder: "orders", Probe: false),
                (Path: labExecutable, Name: "WPF Customer Desk", Folder: "wpf-uia", Probe: false),
                (Path: labExecutable, Name: "WPF Customer Desk", Folder: "wpf-probe", Probe: true) })
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var process = Start(fixture.Path);
                try
                {
                    await WaitForWindow(process, cancellationToken);
                    using ITargetDriver driver = fixture.Probe ? new WpfProbeDriver() : new UiAutomationDriver();
                    await driver.AttachAsync(process.Id, cancellationToken);
                    var snapshot = await driver.SnapshotAsync(cancellationToken);
                    var ready = Stopwatch.StartNew();
                    string readyId = fixture.Folder == "orders" ? "ResetOrders" : "ResetButton";
                    while (!snapshot.Elements.Any(e => e.AutomationId == readyId))
                    {
                        if (ready.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Fixture controls did not become accessible after its window appeared.");
                        await Task.Delay(100, cancellationToken); snapshot = await driver.SnapshotAsync(cancellationToken);
                    }
                    var initial = Path.Combine(artifacts, fixture.Folder); Directory.CreateDirectory(initial);
                    WorkspaceStore.WriteAtomic(Path.Combine(initial, "initial-tree.json"), snapshot);
                    var initialImage = await driver.CaptureAsync(Path.Combine(initial, "initial.png"), cancellationToken);
                    ValidateImage(initialImage, snapshot);
                    if (fixture.Folder == "orders")
                    {
                        Check(UiSelectors.Find(snapshot, "name:Apply").Count == 2, "OrderLab must expose two identically named Apply buttons.");
                        Check(UiSelectors.Find(snapshot, Scope("ApplyAction", "Shipping")).Count == 1, "Shipping scope did not uniquely resolve Apply.");
                        Check(UiSelectors.Find(snapshot, Scope("ApplyAction", "Billing")).Count == 1, "Billing scope did not uniquely resolve Apply.");
                    }
                    for (int iteration = 1; iteration <= repetitions; iteration++)
                    {
                        var cases = fixture.Folder == "orders" ? OrderScenarios(iteration) : WpfScenarios(iteration);
                        foreach (var scenario in cases)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var outcome = new FunctionalityCheck { Repetition = iteration, Fixture = fixture.Name,
                                Driver = fixture.Probe ? "WPF in-process probe" : "Windows UI Automation", Name = scenario.Test.Name, Expected = scenario.Expected };
                            var started = Stopwatch.StartNew();
                            try
                            {
                                using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                                bool cancellationRequested = false;
                                var progress = new InlineProgress<RunProgress>(p =>
                                {
                                    if (scenario.CancelOnWait && !cancellationRequested && p.Step?.Step.Action == StepAction.Wait && p.Step.Status == RunStatus.Running)
                                    { cancellationRequested = true; stop.CancelAfter(120); }
                                });
                                bool modalGuardAttempted = false, modalGuardPassed = false;
                                var captureWindowTrees = new Dictionary<int, UiSnapshot>();
                                var runner = new TestRunner(driver, Path.Combine(initial, $"repeat-{iteration:00}"));
                                runner.StepObserver = async (observedRun, token) =>
                                {
                                    var completedStep = observedRun.Steps[^1];
                                    if (completedStep.Snapshot is { } recorded && File.Exists(completedStep.ScreenshotPath))
                                    {
                                        var (width, height) = ImageDimensions(completedStep.ScreenshotPath);
                                        if (!MatchesWindow(recorded, width, height))
                                        {
                                            // A modal can appear between tree traversal and image capture.
                                            // Preserve a separate post-capture tree; never rewrite the original.
                                            var afterCapture = await driver.SnapshotAsync(token);
                                            WorkspaceStore.WriteAtomic(Path.Combine(observedRun.ArtifactDirectory, $"step-{completedStep.Index + 1:000}-capture-window-tree.json"), afterCapture);
                                            if (MatchesBounds(recorded.ScreenshotBounds, width, height) && MatchesWindow(afterCapture, width, height))
                                            { captureWindowTrees[completedStep.Index] = afterCapture; outcome.CaptureWindowTransitionSteps.Add(completedStep.Index); }
                                        }
                                    }
                                    if (!scenario.VerifyModalGuard || modalGuardAttempted || completedStep.Step.Selector != "id:ConfirmationText" || completedStep.Status != RunStatus.Passed) return;
                                    modalGuardAttempted = true;
                                    try
                                    {
                                        var before = await driver.SnapshotAsync(token);
                                        Check(UiSelectors.Find(before, "id:ConfirmAction").Count == 1, "The modal confirmation must have one control identity.");
                                        Check(UiSelectors.Find(before, "query:{\"id\":\"ConfirmOrderDialog\",\"type\":\"Window\"}").Count == 1, "The modal must appear as one window in the combined tree.");
                                        Check(!UiSelectors.Find(before, "id:ResetOrders").Single().IsEnabled, "The modal owner must expose its Reset button as disabled.");
                                        string originalCount = Value(before, "MutationCount");
                                        WorkspaceStore.WriteAtomic(Path.Combine(observedRun.ArtifactDirectory, "modal-guard-before.json"), before);
                                        using var guardDeadline = CancellationTokenSource.CreateLinkedTokenSource(token); guardDeadline.CancelAfter(1500);
                                        try
                                        {
                                            await driver.ExecuteAsync(Click("ResetOrders"), guardDeadline.Token);
                                            throw new InvalidOperationException("The direct driver incorrectly accepted a mutation behind a modal window.");
                                        }
                                        catch (InvalidOperationException ex) when (ex.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase))
                                        { outcome.ModalGuardError = ex.Message; }
                                        var after = await driver.SnapshotAsync(token);
                                        WorkspaceStore.WriteAtomic(Path.Combine(observedRun.ArtifactDirectory, "modal-guard-after.json"), after);
                                        var guardImage = await driver.CaptureAsync(Path.Combine(observedRun.ArtifactDirectory, "modal-guard.png"), token); ValidateImage(guardImage, after);
                                        Check(Value(after, "MutationCount") == originalCount && Value(after, "ShippingStatus") == Value(before, "ShippingStatus"), "Blocked owner action changed the application state.");
                                        modalGuardPassed = outcome.ModalGuardError.Length > 0;
                                    }
                                    catch (Exception ex) { outcome.ModalGuardError = "Guard verification failed: " + ex.Message; }
                                };
                                var run = await runner.RunAsync(scenario.Test, progress, stop.Token);
                                outcome.Actual = run.Status; outcome.Steps = run.Steps.Count; outcome.ArtifactDirectory = run.ArtifactDirectory;
                                outcome.DiagnosticCategories = run.FailureDiagnostics.Select(d => d.Category).Distinct().ToList();
                                outcome.Message = run.Summary;
                                Check(run.Status == scenario.Expected, $"Expected {scenario.Expected}; actual {run.Status}.");
                                Check(run.Steps.Count == scenario.Test.Steps.Count, "A saved step vanished from the result.");
                                if (scenario.VerifyModalGuard) Check(modalGuardAttempted && modalGuardPassed, outcome.ModalGuardError.Length == 0 ? "The modal guard check was not reached." : outcome.ModalGuardError);
                                foreach (var step in run.Steps.Where(s => s.Status != RunStatus.Skipped))
                                {
                                    Check(step.Snapshot is not null && !step.Snapshot.IsTruncated, $"Step {step.Index + 1} lacks a complete tree.");
                                    ValidateImage(step.ScreenshotPath, captureWindowTrees.GetValueOrDefault(step.Index) ?? step.Snapshot); outcome.EvidenceImages++;
                                }
                                foreach (string file in new[] { "run.json", "report.html", "junit.xml" }) Check(File.Exists(Path.Combine(run.ArtifactDirectory, file)), $"Missing {file}.");
                                if (scenario.Expected == RunStatus.Failed)
                                {
                                    var failed = run.Steps.Single(s => s.Status == RunStatus.Failed);
                                    Check(failed.Message.Contains(scenario.FailureHint, StringComparison.OrdinalIgnoreCase), $"Failure does not explain '{scenario.FailureHint}': {failed.Message}");
                                    Check(!failed.Message.Contains("Evidence", StringComparison.OrdinalIgnoreCase), "The negative result was caused by missing evidence.");
                                    Check(run.Steps.Where(s => s.Index > failed.Index).All(s => s.Status == RunStatus.Skipped), "A later step ran after failure.");
                                    var expectedCategory = scenario.FailureHint switch
                                    { "ambiguous" => FailureCategory.SelectorAmbiguous, "not found" => FailureCategory.SelectorNotFound,
                                        "disabled" => FailureCategory.ControlNotReady, "observed" => FailureCategory.AssertionMismatch, _ => FailureCategory.Unknown };
                                    bool snapshotDeadline = failed.Message.Contains("UI snapshot did not return", StringComparison.Ordinal);
                                    Check(failed.FailureDiagnostics.Any(d => d.Category == expectedCategory || (snapshotDeadline && d.Category == FailureCategory.AutomationTimeout)),
                                        "The structured failure category does not match the observed failure.");
                                    Check(failed.FailureDiagnostics.All(d => d.ActionOutcome == ActionOutcome.NotDispatched), "A readiness/assertion rejection must not claim a dispatched mutation.");
                                    Check(failed.FailureDiagnostics.All(d => d.ObservedFact.Length > 0 && d.CauseAssessment.Length > 0), "Failure diagnostics omit observed facts or cause uncertainty.");
                                }
                                if (scenario.CancelOnWait)
                                {
                                    Check(cancellationRequested && started.Elapsed < TimeSpan.FromSeconds(8), "Cancellation did not stop the bounded wait promptly.");
                                    Check(run.Steps[^1].Status == RunStatus.Skipped, "A mutation ran after cancellation.");
                                    Check(run.FailureDiagnostics.Any(d => d.Category == FailureCategory.Cancelled), "Cancellation lacks its structured diagnostic.");
                                }
                                if (scenario.UnchangedId.Length > 0)
                                {
                                    var after = await driver.SnapshotAsync(cancellationToken);
                                    Check(Value(after, scenario.UnchangedId) == scenario.UnchangedValue, $"Unexpected post-state at {scenario.UnchangedId}: {Value(after, scenario.UnchangedId)}.");
                                }
                                if (scenario.CheckNewRuntimeId)
                                {
                                    var before = UiSelectors.Find(run.Steps[0].Snapshot!, "id:PriorityExpress").Single().RuntimeId;
                                    var after = UiSelectors.Find(run.Steps[1].Snapshot!, "id:PriorityExpress").Single().RuntimeId;
                                    Check(before.Length > 0 && after.Length > 0 && before != after, "Dynamic fixture did not replace the accessibility control identity.");
                                }
                                outcome.Passed = true;
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception ex) { outcome.Passed = false; outcome.Message += " Verification: " + ex.Message; }
                            outcome.DurationMs = started.Elapsed.TotalMilliseconds; report.Checks.Add(outcome); Save();
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    report.Checks.Add(new() { Fixture = fixture.Name, Driver = fixture.Probe ? "WPF in-process probe" : "Windows UI Automation", Name = "Fixture setup", Expected = RunStatus.Passed, Actual = RunStatus.Failed, Message = ex.ToString() }); Save();
                }
                finally { await CloseOwned(process); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            report.CompletedAllScenarios = report.Checks.Count == report.PlannedChecks;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { report.Cancelled = true; throw; }
        finally { report.FinishedAt = DateTimeOffset.UtcNow; Save(); }
        return report;
    }

    private static IEnumerable<Scenario> OrderScenarios(int iteration)
    {
        string shipping = "Shipping " + iteration, billing = "Billing " + iteration;
        yield return new(Case("Scoped duplicate controls, delayed inventory and modal order", OrderPreparation(shipping, billing).Concat([
            Click("CheckInventory"), Assert("OrderStatus", "Inventory ready"), Click("SubmitOrder"),
            Assert("ConfirmationText", $"Confirm 3 units for {shipping}"), Click("ConfirmAction"), Assert("OrderStatus", "Order confirmed: 3 units; total $75.00.")]).ToArray()), RunStatus.Passed, VerifyModalGuard: true);
        yield return new(Case("Stable selector survives recreation and reorder", Click("ResetOrders"), Click("ReorderControls"), Assert("GenerationStatus", "Generation 1"),
            Click("PriorityExpress"), Assert("PriorityStatus", "Priority: Express"), Assert("MutationCount", "2")), RunStatus.Passed, CheckNewRuntimeId: true);
        yield return Negative("Ambiguous label dispatches nothing", new() { Action = StepAction.Click, Selector = "name:Apply", TimeoutMs = 400 }, "ambiguous");
        yield return Negative("Ambiguous duplicate ID dispatches nothing", new() { Action = StepAction.TypeText, Selector = "id:Recipient", Value = "WRONG", TimeoutMs = 400 }, "ambiguous");
        yield return Negative("Missing target dispatches nothing", new() { Action = StepAction.Click, Selector = "id:AbsentAction", TimeoutMs = 400 }, "not found");
        yield return Negative("Disabled target dispatches nothing", new() { Action = StepAction.Click, Selector = "id:DisabledAction", TimeoutMs = 400 }, "disabled");
        yield return Negative("Wrong exact text reaches bounded timeout", Assert("OrderStatus", "This value must not appear", 400), "observed");
        yield return new(Case("Seeded incorrect total cannot pass", OrderPreparation(shipping, billing).Concat([
            new TestStep { Title = "Enable wrong total", Action = StepAction.Toggle, Selector = "id:DefectToggle", Value = "true" }, Click("SubmitOrder"),
            Assert("ConfirmationText", $"Confirm 3 units for {shipping}"), Click("ConfirmAction"), Assert("OrderStatus", "Order confirmed: 3 units; total $75.00.", 400), Click("PriorityExpress")]).ToArray()),
            RunStatus.Failed, "observed", "OrderStatus", "Order confirmed: 3 units; total $76.00.");
        yield return new(Case("Cancellation prevents the later mutation", Click("ResetOrders"), new() { Title = "Cancellable wait", Action = StepAction.Wait, Value = "2000", TimeoutMs = 2000 }, Click("PriorityExpress")),
            RunStatus.Cancelled, UnchangedId: "MutationCount", UnchangedValue: "0", CancelOnWait: true);
    }
    private static Scenario Negative(string name, TestStep invalid, string hint) => new(Case(name, Click("ResetOrders"), invalid, Click("PriorityExpress")), RunStatus.Failed, hint, "MutationCount", "0");
    private static IEnumerable<TestStep> OrderPreparation(string shipping, string billing) => [Click("ResetOrders"),
        new() { Title = "Enter shipping recipient", Action = StepAction.TypeText, Selector = Scope("Recipient", "Shipping"), Value = shipping },
        new() { Title = "Apply shipping only", Action = StepAction.Click, Selector = Scope("ApplyAction", "Shipping") }, Assert("ShippingStatus", "Shipping: " + shipping), Assert("BillingStatus", "Billing not applied"),
        new() { Title = "Enter billing recipient", Action = StepAction.TypeText, Selector = Scope("Recipient", "Billing"), Value = billing },
        new() { Title = "Apply billing only", Action = StepAction.Click, Selector = Scope("ApplyAction", "Billing") }, Assert("BillingStatus", "Billing: " + billing), Type("Quantity", "3")];

    private static IEnumerable<Scenario> WpfScenarios(int iteration)
    {
        string name = "Repeat " + iteration;
        yield return new(Case("Customer create, modal review and delayed state", Click("ResetButton"),
            new() { Title = "Scoped selector enters name", Action = StepAction.TypeText, Selector = "query:{\"id\":\"CustomerName\",\"ancestor\":{\"id\":\"CustomerDeskWindow\"}}", Value = name },
            Type("CustomerEmail", $"repeat{iteration}@example.test"),
            new() { Title = "Scoped selector submits customer", Action = StepAction.Click, Selector = "query:{\"id\":\"AddCustomer\",\"type\":\"Button\",\"ancestor\":{\"id\":\"CustomerDeskWindow\"}}" },
            Assert("StatusMessage", "Customer added: " + name), Assert("ResultCount", "1 customer"),
            Click("OpenReview"), Assert("ReviewSummary", "1 customer records in this session."), Click("CloseReview"), Click("DelayedButton"), Assert("StatusMessage", "Background check complete.")), RunStatus.Passed);
        yield return new(Case("Missing target cannot pass", Click("ResetButton"), new() { Title = "Missing control", Action = StepAction.Click, Selector = "id:MissingRepeatedControl", TimeoutMs = 400 }, Click("AddCustomer")), RunStatus.Failed, "not found", "StatusMessage", "Ready for a new customer.");
        yield return new(Case("Wrong exact text reaches bounded timeout", Click("ResetButton"), Assert("StatusMessage", "This value must not appear", 400), Click("AddCustomer")), RunStatus.Failed, "observed", "StatusMessage", "Ready for a new customer.");
        yield return new(Case("Seeded wrong customer name cannot pass", Click("ResetButton"), Type("CustomerName", name), Type("CustomerEmail", $"repeat{iteration}@example.test"),
            new() { Title = "Enable wrong name", Action = StepAction.Toggle, Selector = "id:DefectToggle", Value = "true" }, Click("AddCustomer"), Assert("StatusMessage", "Customer added: " + name, 400), Click("ResetButton")),
            RunStatus.Failed, "observed", "StatusMessage", "Customer added: [incorrect name]");
        yield return new(Case("Cancellation prevents the later mutation", Click("ResetButton"), new() { Title = "Cancellable wait", Action = StepAction.Wait, Value = "2000", TimeoutMs = 2000 }, Click("AddCustomer")),
            RunStatus.Cancelled, UnchangedId: "StatusMessage", UnchangedValue: "Ready for a new customer.", CancelOnWait: true);
    }
    private static string Scope(string id, string section) => "query:" + JsonSerializer.Serialize(new { id, ancestor = new { label = section } });
    private static string Value(UiSnapshot snapshot, string id) => UiSelectors.Find(snapshot, "id:" + id).Single().Value;
    private static TestCase Case(string name, params TestStep[] steps) => new() { Name = name, Intent = name, Category = "Repeated real desktop functionality", Steps = steps.ToList() };
    private static TestStep Click(string id) => new() { Title = "Click " + id, Action = StepAction.Click, Selector = "id:" + id };
    private static TestStep Type(string id, string value) => new() { Title = "Enter " + id, Action = StepAction.TypeText, Selector = "id:" + id, Value = value };
    private static TestStep Assert(string id, string value, int timeout = 3000) => new() { Title = "Assert " + id, Action = StepAction.AssertText, Selector = "id:" + id, Value = value, TimeoutMs = timeout };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void ValidateFixture(string executable, string name)
    { if (!File.Exists(executable) || !string.Equals(Path.GetFileName(executable), name, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Only the included " + name + " fixture is allowed."); }
    private static Process Start(string executable) => Process.Start(new ProcessStartInfo(Path.GetFullPath(executable))
    { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!, WindowStyle = ProcessWindowStyle.Hidden }) ?? throw new InvalidOperationException("Owned fixture failed to start.");
    private static async Task WaitForWindow(Process process, CancellationToken token)
    {
        var wait = Stopwatch.StartNew();
        while (true)
        { token.ThrowIfCancellationRequested(); process.Refresh(); if (process.HasExited) throw new InvalidOperationException("Owned fixture exited before showing its window."); if (process.MainWindowHandle != 0 && IsShown(process.MainWindowHandle)) return; if (wait.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Owned fixture did not show a window."); await Task.Delay(100, token); }
    }
    // A new WPF window has a handle before DWM uncloaks it, and capture skips cloaked windows, so wait until it is really shown.
    private static bool IsShown(nint window) => IsWindowVisible(window) && !(DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    private static async Task CloseOwned(Process process)
    {
        if (process.HasExited) return; process.CloseMainWindow(); using var deadline = new CancellationTokenSource(4000);
        try { await process.WaitForExitAsync(deadline.Token); } catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: false); }
    }
    private static void ValidateImage(string path, UiSnapshot? snapshot = null)
    {
        Check(File.Exists(path), "Screenshot is absent.");
        using var stream = File.OpenRead(path);
        var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        Check(frame.PixelWidth >= 300 && frame.PixelHeight >= 180, "Screenshot dimensions do not contain the fixture.");
        if (snapshot is not null)
            Check(MatchesWindow(snapshot, frame.PixelWidth, frame.PixelHeight),
                "Screenshot dimensions do not match any observed top-level fixture window.");
        var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int stride = bitmap.PixelWidth * 4; var pixels = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, stride, 0);
        int total = 0, light = 0; var colors = new HashSet<int>();
        for (int y = bitmap.PixelHeight * 15 / 100; y < bitmap.PixelHeight * 9 / 10; y += 9)
            for (int x = bitmap.PixelWidth / 10; x < bitmap.PixelWidth * 9 / 10; x += 9)
            { int i = y * stride + x * 4; total++; if (Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) > 32) light++; colors.Add(pixels[i] | pixels[i + 1] << 8 | pixels[i + 2] << 16); }
        Check(light > total / 2 && colors.Count >= 8, "Screenshot client is blank or lacks fixture detail.");
    }
    private static (int Width, int Height) ImageDimensions(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }
    private static bool MatchesBounds(ElementBounds bounds, int width, int height) => Math.Abs(bounds.Width - width) < 2 && Math.Abs(bounds.Height - height) < 2;
    private static bool MatchesWindow(UiSnapshot snapshot, int width, int height) => snapshot.Elements.Any(e => e.Depth == 0 && e.ControlType == "Window" && MatchesBounds(e.Bounds, width, height));
    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}
