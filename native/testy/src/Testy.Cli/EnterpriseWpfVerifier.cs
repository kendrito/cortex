using System.Diagnostics;
using System.Text.Json;
using Testy.Core;
using Testy.Windows;

namespace Testy.Cli;

internal sealed class EnterpriseReport
{
    public string Mode => "Owned editable WPF fixture; real UIA/probe replay and explicit guarded recovery; no model calls";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public int Repetitions { get; set; }
    public int PlannedChecks { get; set; }
    public bool CompletedAllScenarios { get; set; }
    public bool Cancelled { get; set; }
    public bool Passed => FinishedAt >= StartedAt && CompletedAllScenarios && !Cancelled && PlannedChecks > 0 && Checks.Count == PlannedChecks && Checks.All(c => c.Passed);
    public List<EnterpriseCheck> Checks { get; set; } = [];
}
internal sealed class EnterpriseCheck
{
    public string Name { get; set; } = "";
    public string Driver { get; set; } = "";
    public int Repetition { get; set; }
    public bool Passed { get; set; }
    public double DurationMs { get; set; }
    public int EvidenceImages { get; set; }
    public List<string> RunDirectories { get; set; } = [];
    public string Message { get; set; } = "";
}

internal static class EnterpriseWpfVerifier
{
    private const string Grid = "EditableOrders";
    private const string UnchangedCounters = "begin=0; commit=0; cancel=0; saved=0";
    private const string OriginalSelector = "query:{\"id\":\"OriginalApply\",\"ancestor\":{\"id\":\"RecoveryArea\"}}";
    private const string AlternativeSelector = "query:{\"id\":\"UpdatedApply\",\"ancestor\":{\"id\":\"RecoveryArea\"}}";
    public static async Task<EnterpriseReport> VerifyAsync(string executable, string artifacts, int repetitions, CancellationToken ct)
    {
        executable = Path.GetFullPath(executable); artifacts = Path.GetFullPath(artifacts);
        if (!File.Exists(executable) || Path.GetFileName(executable) != "Testy.WpfLab.exe") throw new ArgumentException("Enterprise verification launches only Testy.WpfLab.exe --grid-workflows.");
        if (repetitions is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(repetitions));
        Directory.CreateDirectory(artifacts);
        var report = new EnterpriseReport { Repetitions = repetitions, PlannedChecks = 31 * repetitions };
        void Save() => WorkerCommand.DurableWrite(Path.Combine(artifacts, "enterprise-report.json"), report);
        Save();
        try
        {
            for (int repetition = 1; repetition <= repetitions; repetition++)
                foreach (bool probe in new[] { false, true })
                {
                    ct.ThrowIfCancellationRequested();
                    var start = new ProcessStartInfo(executable) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(executable)! }; start.ArgumentList.Add("--grid-workflows");
                    using var process = Process.Start(start) ?? throw new InvalidOperationException("Owned enterprise fixture did not start.");
                    try
                    {
                        await WaitForWindowAsync(process, ct);
                        using ITargetDriver driver = probe ? new WpfProbeDriver() : new UiAutomationDriver();
                        await driver.AttachAsync(process.Id, ct);
                        string phase = Path.Combine(artifacts, $"repetition-{repetition:00}", probe ? "probe" : "uia"); Directory.CreateDirectory(phase);
                        int scenario = 0;
                        async Task Check(string name, Func<ScenarioContext, Task> action)
                        {
                            ct.ThrowIfCancellationRequested();
                            var check = new EnterpriseCheck { Name = name, Driver = probe ? "WPF in-process probe" : "Windows UI Automation", Repetition = repetition };
                            string directory = Path.Combine(phase, $"{++scenario:00}"); Directory.CreateDirectory(directory);
                            var context = new ScenarioContext(driver, process.Id, directory, check, ct); var timer = Stopwatch.StartNew();
                            try
                            {
                                await action(context);
                                foreach (string runPath in check.RunDirectories)
                                {
                                    var run = await WorkerCommand.ReadJsonAsync<RunResult>(Path.Combine(runPath, "run.json"), ct);
                                    foreach (var step in run.Steps.Where(s => s.Status is RunStatus.Passed or RunStatus.Failed))
                                    {
                                        WorkerCommand.Require(step.Snapshot?.Target.ProcessId == process.Id, "Executed scenario step has no owned-process snapshot.");
                                        WorkerCommand.ValidatePng(step.ScreenshotPath); check.EvidenceImages++;
                                    }
                                }
                                var final = await driver.SnapshotAsync(ct); WorkerCommand.DurableWrite(Path.Combine(directory, "verification-after.json"), final);
                                WorkerCommand.Require(!JsonSerializer.Serialize(final, TestyJson.Options).Contains("fixture-editor-secret-42", StringComparison.Ordinal), "Password leaked into snapshot evidence.");
                                check.Passed = true; check.Message = "Exact scenario outcomes, canonical step records, owned target identity and image/tree evidence verified.";
                            }
                            catch (OperationCanceledException) when (ct.IsCancellationRequested) { report.Cancelled = true; throw; }
                            catch (Exception ex) { check.Message = ex.GetType().Name + ": " + ex.Message; }
                            finally { check.DurationMs = timer.Elapsed.TotalMilliseconds; report.Checks.Add(check); Save(); }
                        }

                        if (!probe)
                        {
                            foreach (var action in new[] { StepAction.GridEditCell, StepAction.GridCommitRow, StepAction.GridCancelRow })
                                await Check($"UIA explicitly rejects unsupported {action} before grid input", async c =>
                                {
                                    var rejected = action == StepAction.GridEditCell ? Edit("INV-0001", "7") : Row(action, "INV-0001");
                                    await c.Run([Reset(), rejected, Sentinel()], 1, FailureCategory.CapabilityUnavailable);
                                    await c.Unchanged();
                                });
                            await Check("UIA reports probe-only binding property as unsupported", async c =>
                            {
                                await c.Run([Reset(), Property("BindingFailure", "wpf.bindingStatus", "PathError"), Sentinel()], 1, FailureCategory.CapabilityUnavailable);
                                await c.Unchanged();
                            });
                        }
                        else
                        {
                            await Check("Quantity edit remains pending until explicit commit and persisted value is exact", async c =>
                            {
                                await c.Run([Reset(), Edit("INV-0001", "7"), Property(Grid, "wpf.gridIsEditing", true), Property(Grid, "wpf.gridEditingRowKey", "INV-0001"), Text("GridCellEditor", "7"), Text("PersistedState", "INV-0001=1"), Row(StepAction.GridCommitRow, "INV-0001"), Property(Grid, "wpf.gridIsEditing", false), Text("PersistedState", "INV-0001=7"), Text("EditStatus", "Saved INV-0001: 7"), Text("LastSavedKey", "INV-0001"), Text("LastSavedQuantity", "7")]);
                            });
                            await Check("Cancellation restores the row and leaves persistent storage unchanged", async c =>
                            {
                                await c.Run([Reset(), Edit("INV-0001", "9"), Row(StepAction.GridCancelRow, "INV-0001"), Property(Grid, "wpf.gridIsEditing", false), Text("EditStatus", "Cancelled INV-0001"), Text("PersistedState", "INV-0001=1"), Property("EditCell-INV-0001-Quantity", "uia.value", "1"), Text("EditCounters", "begin=1; commit=0; cancel=1; saved=0")]);
                            });
                            await Check("Invalid quantity rejects commit, preserves validation evidence and skips later input", async c =>
                            {
                                await c.Run([Reset(), Edit("INV-0001", "0"), Row(StepAction.GridCommitRow, "INV-0001"), Sentinel()], 2);
                                await c.Run([Text("PersistedState", "INV-0001=1"), Property(Grid, "wpf.gridIsEditing", true), Property("GridCellEditor", "wpf.validationHasError", true), Text("RecoveryCount", "0")]);
                            });
                            foreach (var rejected in new[]
                            {
                                ("Read-only business key column", Array.Empty<TestStep>(), Edit("INV-0001", "changed", "Key")),
                                ("Missing business row", Array.Empty<TestStep>(), Edit("INV-9999", "7")),
                                ("Duplicate business row keys", new[]{ Toggle("DuplicateRowKeys") }, Edit("INV-0001", "7")),
                                ("Duplicate business column keys", new[]{ Toggle("DuplicateColumnKeys") }, Edit("INV-0001", "7")),
                                ("Filtered-out business row", new[]{ Click("EditFilterHigh") }, Edit("INV-0001", "7"))
                            }) await Check(rejected.Item1 + " rejects editing without opening a transaction", async c =>
                            {
                                var steps = new List<TestStep> { Reset() }; steps.AddRange(rejected.Item2); steps.Add(rejected.Item3); steps.Add(Sentinel());
                                await c.Run(steps, steps.Count - 2); await c.Unchanged();
                            });
                            await Check("Commit for another row cannot consume the current pending edit", async c =>
                            {
                                await c.Run([Reset(), Edit("INV-0001", "7"), Row(StepAction.GridCommitRow, "INV-0002"), Sentinel()], 2);
                                await c.Run([Text("PersistedState", "INV-0001=1"), Property(Grid, "wpf.gridEditingRowKey", "INV-0001"), Text("GridCellEditor", "7"), Text("EditCounters", "begin=1; commit=0; cancel=0; saved=0"), Text("RecoveryCount", "0")]);
                            });
                            await Check("Application row-commit veto cannot report a save", async c =>
                            {
                                await c.Run([Reset(), Toggle("RejectCommit"), Edit("INV-0001", "7"), Row(StepAction.GridCommitRow, "INV-0001"), Sentinel()], 3);
                                await c.Run([Text("EditStatus", "Commit rejected"), Text("PersistedState", "INV-0001=1"), Property(Grid, "wpf.gridIsEditing", true), Text("RecoveryCount", "0")]);
                            });
                            await Check("Delayed persistence is awaited through exact saved-state assertions", async c =>
                            {
                                await c.Run([Reset(), Toggle("DelaySave"), Edit("INV-0001", "7"), Row(StepAction.GridCommitRow, "INV-0001"), Text("PersistedState", "INV-0001=7"), Text("EditStatus", "Saved INV-0001: 7"), Text("EditCounters", "begin=1; commit=1; cancel=0; saved=1")]);
                            });
                            await Check("Seeded asynchronous persistence failure fails the exact acceptance assertion", async c =>
                            {
                                await c.Run([Reset(), Toggle("FailSave"), Edit("INV-0001", "7"), Row(StepAction.GridCommitRow, "INV-0001"), Text("EditStatus", "Save failed INV-0001"), Text("PersistedState", "INV-0001=7"), Sentinel()], 5, FailureCategory.AssertionMismatch);
                                await c.Run([Text("PersistedState", "INV-0001=1"), Text("EditCounters", "begin=1; commit=1; cancel=0; saved=0"), Text("RecoveryCount", "0")]);
                            });
                            await Check("Seeded wrong saved quantity never passes the requested business value", async c =>
                            {
                                await c.Run([Reset(), Toggle("WrongSave"), Edit("INV-0001", "7"), Row(StepAction.GridCommitRow, "INV-0001"), Text("EditStatus", "Saved INV-0001: 8"), Text("PersistedState", "INV-0001=7"), Sentinel()], 5, FailureCategory.AssertionMismatch);
                                await c.Run([Text("PersistedState", "INV-0001=8"), Text("LastSavedQuantity", "8"), Text("RecoveryCount", "0")]);
                            });
                            await Check("Sorted, filtered, reordered and recycled rows retain exact business identity", async c =>
                            {
                                await c.Run([Reset(), Click("EditSortDescending"), Click("EditFilterHigh"), Click("EditReorderColumns"), Edit("INV-0999", "31"), Row(StepAction.GridCommitRow, "INV-0999"), Text("PersistedState", "INV-0999=31"), Text("LastSavedKey", "INV-0999"), Click("EditClearFilter"), Edit("INV-0001", "11"), Row(StepAction.GridCommitRow, "INV-0001"), Text("PersistedState", "INV-0001=11"), Edit("INV-0999", "32"), Row(StepAction.GridCommitRow, "INV-0999"), Text("PersistedState", "INV-0999=32"), Text("LastSavedKey", "INV-0999")]);
                            });
                            await Check("Binding failure and later repair are typed observations, not inferred causes", async c =>
                            {
                                await c.Run([Reset(), Property("BindingFailure", "wpf.bindingStatus", "PathError"), Text("BindingFailure", "Binding unavailable"), Click("RepairBinding"), Property("BindingFailure", "wpf.bindingStatus", "Active"), Text("BindingFailure", "Binding repaired")]);
                            });
                            await Check("Command availability changes without invoking the disabled command", async c =>
                            {
                                await c.Run([Reset(), Property("DiagnosticCommand", "wpf.commandCanExecute", true), new TestStep { Title = "Disable diagnostic command", Action = StepAction.Toggle, Selector = "id:CommandAllowed", Value = "false" }, Property("DiagnosticCommand", "wpf.commandCanExecute", false), Text("EditStatus", "Ready")]);
                                WorkerCommand.Require(!UiSelectors.Find(await driver.SnapshotAsync(ct), "id:DiagnosticCommand").Single().IsEnabled, "CanExecute=false did not disable the command button.");
                                await c.Unchanged();
                            });
                            await Check("Modal owner prevents grid input until the owned dialog closes", async c =>
                            {
                                try
                                {
                                    await c.Run([Reset(), Click("EditOpenModal"), Step(StepAction.AssertExists, "EditCloseModal"), Edit("INV-0001", "7"), Sentinel()], 3, FailureCategory.ControlNotReady);
                                    await c.Unchanged();
                                }
                                finally
                                {
                                    var snapshot = await driver.SnapshotAsync(ct);
                                    if (UiSelectors.Find(snapshot, "id:EditCloseModal").Count == 1) await driver.ExecuteAsync(Click("EditCloseModal"), ct);
                                }
                            });
                        }
                        await Check("Password values remain redacted in all observed property channels", async c =>
                        {
                            await c.Run([Reset(), Step(StepAction.AssertExists, "EditorPassword")]);
                            var snapshot = await driver.SnapshotAsync(ct); var secret = UiSelectors.Find(snapshot, "id:EditorPassword").Single();
                            WorkerCommand.Require(secret.IsPassword && !secret.Value.Contains("fixture-editor-secret-42", StringComparison.Ordinal), "Password legacy value was exposed.");
                            WorkerCommand.Require(secret.Properties.GetValueOrDefault("uia.value")?.Status == UiPropertyStatus.Redacted, "Typed password value was not Redacted.");
                            WorkerCommand.Require(!JsonSerializer.Serialize(snapshot, TestyJson.Options).Contains("fixture-editor-secret-42", StringComparison.Ordinal), "A secret leaked in diagnostics or another property.");
                        });
                        await Check("Strict execution never silently substitutes an authored alternative", async c =>
                        {
                            await c.Run([Reset(), Click("ChangeSelector")]);
                            var before = await driver.SnapshotAsync(ct);
                            WorkerCommand.DurableWrite(Path.Combine(c.Directory, "strict-before.json"), before);
                            WorkerCommand.Require(UiSelectors.Find(before, OriginalSelector).Count == 0 && UiSelectors.Find(before, AlternativeSelector).Count == 1, "Strict recovery scenario did not expose exactly one alternative and an absent original in the intended scope.");
                            await c.Run([RecoveryStep(2000), Sentinel()], 0, FailureCategory.SelectorNotFound);
                            await c.Run([Text("RecoveryCount", "0")]);
                        });
                        await Check("Explicit approved selector recovery invokes exactly once with canonical evidence", async c =>
                        {
                            await c.Run([Reset(), Click("ChangeSelector")]);
                            var canonical = RecoveryStep(); var test = new TestCase { Name = "Explicit approved alternative", Steps = [canonical] };
                            var run = await new TestRunner(driver, c.Directory).RunWithSelectorAlternativeAsync(test, canonical.Id, "renamed-apply", null, ct);
                            c.Check.RunDirectories.Add(run.ArtifactDirectory); TextBoundaryVerifier.ValidateRecords(run, test, process.Id);
                            WorkerCommand.Require(run.Status == RunStatus.Passed && WorkerCommand.Canonical(run.Steps.Single().Step, canonical), "Approved recovery changed or failed the canonical saved step.");
                            SelectorRecovery.ValidateEvidence(canonical, run.Steps.Single());
                            await c.Run([Text("RecoveryCount", "1")]);
                        });
                        await Check("Ambiguous approved alternative is rejected without invocation", async c =>
                        {
                            await c.Run([Reset(), Click("ChangeSelector"), Click("DuplicateRecovery")]);
                            var before = await driver.SnapshotAsync(ct);
                            WorkerCommand.DurableWrite(Path.Combine(c.Directory, "ambiguity-before.json"), before);
                            WorkerCommand.Require(UiSelectors.Find(before, OriginalSelector).Count == 0 && UiSelectors.Find(before, AlternativeSelector).Count == 2, "Ambiguity scenario did not actually expose two alternative controls in the intended scope.");
                            var canonical = RecoveryStep(); var test = new TestCase { Name = "Ambiguous approved alternative", Steps = [canonical] };
                            var run = await new TestRunner(driver, c.Directory).RunWithSelectorAlternativeAsync(test, canonical.Id, "renamed-apply", null, ct);
                            c.Check.RunDirectories.Add(run.ArtifactDirectory); TextBoundaryVerifier.ValidateRecords(run, test, process.Id);
                            WorkerCommand.Require(run.Status == RunStatus.Failed && run.Steps.Single().Status == RunStatus.Failed, "Ambiguous recovery was not failed.");
                            await c.Run([Text("RecoveryCount", "0")]);
                        });
                        await Check("A stale recovery guard cannot dispatch after the original identity returns", async c =>
                        {
                            await c.Run([Reset(), Click("ChangeSelector")]);
                            var canonical = RecoveryStep(); var before = await driver.SnapshotAsync(ct); var guard = SelectorRecovery.Resolve(canonical, "renamed-apply", before);
                            WorkerCommand.DurableWrite(Path.Combine(c.Directory, "guard-before.json"), before);
                            await c.Run([Reset()]);
                            var resolved = TestyJson.Clone(canonical); resolved.Selector = AlternativeSelector;
                            bool rejected = false;
                            try { await ((IGuardedTargetDriver)driver).ExecuteGuardedAsync(resolved, guard, ct); }
                            catch (Exception ex) when (ex is not OperationCanceledException) { rejected = true; WorkerCommand.DurableWrite(Path.Combine(c.Directory, "guard-rejection.json"), new { error = ex.Message, type = ex.GetType().Name }); }
                            WorkerCommand.Require(rejected, "A stale guarded mutation was accepted.");
                            await c.Run([Text("RecoveryCount", "0")]);
                        });
                    }
                    finally { await CloseOwnedAsync(process); }
                }
            ct.ThrowIfCancellationRequested(); report.CompletedAllScenarios = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { report.Cancelled = true; }
        finally { report.FinishedAt = DateTimeOffset.UtcNow; report.Cancelled |= ct.IsCancellationRequested; Save(); }
        return report;
    }

    internal static TestCase LiveGridTest() => new()
    {
        Id = "enterprise-edit-commit-exact", Name = "Edit and commit an invoice through WPF", Intent = "Change only invoice INV-0001 Quantity to 7 through the UI editor and explicit row commit, then verify exact persistent data.",
        Steps = [Reset(), Edit("INV-0001", "7"), Property(Grid, "wpf.gridIsEditing", true), Text("PersistedState", "INV-0001=1"), Row(StepAction.GridCommitRow, "INV-0001"), Text("PersistedState", "INV-0001=7"), Text("EditStatus", "Saved INV-0001: 7"), Text("LastSavedKey", "INV-0001")]
    };
    private sealed class ScenarioContext(ITargetDriver driver, int pid, string directory, EnterpriseCheck check, CancellationToken ct)
    {
        public string Directory => directory;
        public EnterpriseCheck Check => check;
        public async Task<RunResult> Run(IEnumerable<TestStep> steps, int? failedIndex = null, FailureCategory? category = null)
        {
            var test = new TestCase { Name = check.Name, Category = "Enterprise WPF verification", Steps = steps.ToList() };
            var run = await new TestRunner(driver, directory).RunAsync(test, null, ct); check.RunDirectories.Add(run.ArtifactDirectory);
            TextBoundaryVerifier.ValidateRecords(run, test, pid);
            for (int i = 0; i < test.Steps.Count; i++)
            {
                WorkerCommand.Require(WorkerCommand.Canonical(run.Steps[i].Step, test.Steps[i]), "A scenario step differs from its canonical saved definition.");
                var expected = failedIndex is not int failure || i < failure ? RunStatus.Passed : i == failure ? RunStatus.Failed : RunStatus.Skipped;
                WorkerCommand.Require(run.Steps[i].Status == expected, $"Step {i + 1} expected {expected}, got {run.Steps[i].Status}: {run.Steps[i].Message}");
            }
            WorkerCommand.Require(run.Status == (failedIndex.HasValue ? RunStatus.Failed : RunStatus.Passed), "Scenario aggregate outcome contradicts exact step outcomes.");
            if (failedIndex is int index && category is FailureCategory required)
                WorkerCommand.Require(run.Steps[index].FailureDiagnostics.Any(d => d.Category == required), "Missing expected observed diagnostic " + required + ".");
            return run;
        }
        public async Task Unchanged()
        {
            var observed = await driver.SnapshotAsync(ct); WorkerCommand.DurableWrite(Path.Combine(directory, "unchanged-state.json"), observed);
            foreach (var expected in new[] { ("PersistedState", "INV-0001=1"), ("EditCounters", UnchangedCounters), ("RecoveryCount", "0") })
            {
                var actual = UiSelectors.Find(observed, "id:" + expected.Item1).Single();
                WorkerCommand.Require((string.IsNullOrEmpty(actual.Value) ? actual.Name : actual.Value) == expected.Item2, "Rejected input changed " + expected.Item1 + ".");
            }
        }
    }
    private static TestStep RecoveryStep(int timeoutMs = 6000) => new()
    {
        Title = "Invoke the explicitly approved renamed apply control", Action = StepAction.Click, Selector = OriginalSelector, TimeoutMs = timeoutMs,
        SelectorAlternatives = [new SelectorAlternative { Id = "renamed-apply", Selector = AlternativeSelector, ExpectedAutomationId = "UpdatedApply", ExpectedName = "Apply recovery fixture", ExpectedControlType = "Button" }]
    };
    private static TestStep Step(StepAction action, string id, string value = "") => new() { Title = action + " " + id, Action = action, Selector = "id:" + id, Value = value, TimeoutMs = 6000 };
    private static TestStep Click(string id) => Step(StepAction.Click, id);
    private static TestStep Reset() => Click("EditReset");
    private static TestStep Sentinel() => Click("OriginalApply");
    private static TestStep Text(string id, string text) => Step(StepAction.AssertText, id, text);
    private static TestStep Toggle(string id) => Step(StepAction.Toggle, id, "true");
    private static TestStep Property(string id, string property, object value) => Step(StepAction.AssertProperty, id, JsonSerializer.Serialize(new { property, equals = value }));
    private static TestStep Edit(string row, string text, string column = "Quantity") => Step(StepAction.GridEditCell, Grid, JsonSerializer.Serialize(new { rowKey = row, columnKey = column, text }));
    private static TestStep Row(StepAction action, string row) => Step(action, Grid, JsonSerializer.Serialize(new { rowKey = row }));
    internal static async Task WaitForWindowAsync(Process process, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested(); process.Refresh();
            if (process.HasExited) throw new InvalidOperationException("Owned fixture exited during startup.");
            if (process.MainWindowHandle != 0) return;
            if (timer.Elapsed.TotalSeconds > 20) throw new TimeoutException("Owned fixture did not expose a window.");
            await Task.Delay(100, ct);
        }
    }
    internal static async Task CloseOwnedAsync(Process process)
    {
        if (process.HasExited) return; process.CloseMainWindow();
        if (!await WorkerCommand.WaitForExitAsync(process, TimeSpan.FromSeconds(3)))
        { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
    }
}
