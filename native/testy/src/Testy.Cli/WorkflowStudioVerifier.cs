using System.Diagnostics;
using System.Text.Json;
using Testy.Core;
using Testy.Windows;

namespace Testy.Cli;

/// <summary>Operates only a newly owned Studio workspace. No external model requests.</summary>
internal static class WorkflowStudioVerifier
{
    public static async Task<VerificationReport> VerifyAsync(string executable, string lab, string artifacts, CancellationToken ct)
    {
        executable = Path.GetFullPath(executable); lab = Path.GetFullPath(lab); artifacts = Path.GetFullPath(artifacts);
        if (!File.Exists(executable) || Path.GetFileName(executable) != "Testy.Studio.exe" || !File.Exists(lab) || Path.GetFileName(lab) != "Testy.TestLab.exe") throw new ArgumentException("Owned Studio and TestLab executables are required.");
        Directory.CreateDirectory(artifacts); var report = new VerificationReport { Executable = executable, PlannedChecks = 15 };
        var store = new WorkspaceStore(Path.Combine(artifacts, "workspace-" + Guid.NewGuid().ToString("N")));
        var project = Path.Combine(artifacts, "owned-project"); Directory.CreateDirectory(project); File.WriteAllText(Path.Combine(project, "readme.txt"), "Owned workflow UI qualification fixture.");
        var settings = new ProviderSettings { Kind = ProviderKind.Compatible, Endpoint = "http://127.0.0.1:15999/v1/chat/completions", Model = "not-contacted", ApiKeyEnvironmentVariable = "TESTY_UNUSED_WORKFLOW_TEST", NativeComputerUse = false };
        store.SaveSettings(settings);
        var test = new TestCase { Name = "Owned acceptance", Steps = [new() { Title = "Name exists", Action = StepAction.AssertExists, Selector = "id:CustomerName" }] }; store.SaveTest(test);
        Directory.CreateDirectory(Path.Combine(store.RootDirectory, "profiles")); File.WriteAllText(Path.Combine(store.RootDirectory, "profiles", "malformed.json"), "{bad profile");
        var queue = new OperationsStore(Path.Combine(store.RootDirectory, "operations"));
        Process? process = null; string stage = "Launch owned Studio";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(4)); var token = deadline.Token;
        using var driver = new UiAutomationDriver { MaxElements = 5000 };
        try
        {
            var start = StudioLaunch.StartInfo(executable, store.RootDirectory);
            process = Process.Start(start) ?? throw new IOException("Studio did not start."); report.OwnedProcessId = process.Id;
            await Wait(() => { process.Refresh(); return process.MainWindowHandle != 0; }); await driver.AttachAsync(process.Id, token);
            // Automate is an in-window page (no longer a modal window). It opens on its first tab, Background runs.
            await Act(StepAction.Click, "NavWorkflows"); await WaitElement("AgentStatus");
            stage = "Malformed profile does not block workflow UI";
            var automate = await Snapshot();
            Require(automate.Elements.Any(e => e.AutomationId == "WorkflowStatus" && e.Name.Contains("could not be loaded")), "Malformed-profile diagnostic was not displayed.");
            Require(new[] { "WorkflowTabJobs", "WorkflowTabSuites", "WorkflowTabMachines", "WorkflowTabDemonstrations", "WorkflowTabLifecycle", "WorkflowTabProject", "WorkflowTabWorkspace" }.All(id => automate.Elements.Count(e => e.AutomationId == id) == 1), "Automate tabs are missing or duplicated.");
            Require(automate.Elements.Single(e => e.AutomationId == "WorkflowTabJobs").Properties.TryGetValue("uia.isSelected", out var landing) && landing.Value is { ValueKind: JsonValueKind.True }, "Automate did not open on Background runs.");
            Require(automate.Elements.All(e => e.AutomationId != "CloseWorkflows"), "The retired Close button is still present; navigation replaces it.");
            await Capture("01-automate"); Pass(stage);

            stage = "Project form saves explicit scoped access";
            await Act(StepAction.Click, "WorkflowTabProject"); await WaitElement("ProjectRoot");
            await Act(StepAction.TypeText, "ProjectRoot", project);
            // Leaving Automate discards the unsaved edit and removes the page. Set the persisted config through the same
            // explicit user selection the form represents, then re-enter to verify round-trip fields without invoking any command.
            await Act(StepAction.Click, "NavLibrary"); await WaitGone("WorkflowStatus");
            store.ConfigureProjectTools(new() { Enabled = true, RootDirectory = project, Commands = [new() { Id = "prepare", Description = "Owned no-op preparation", Executable = Environment.ProcessPath!, Arguments = ["--help"], WorkingDirectory = ".", TimeoutSeconds = 30 }], RequiredBeforeUiCommands = ["prepare"] });
            await Act(StepAction.Click, "NavWorkflows"); await WaitElement("AgentStatus"); await Act(StepAction.Click, "WorkflowTabProject"); await WaitElement("ProjectRoot"); await Act(StepAction.Click, "SaveProject");
            await Wait(() => store.LoadSettings().ProjectTools?.Commands.Count == 1);
            Require(Read(store.LoadSettings).ProjectTools?.RootDirectory == project, "Project scope changed."); Pass(stage);

            stage = "Lifecycle form freezes test and provider inputs";
            await Act(StepAction.Click, "WorkflowTabLifecycle"); await Act(StepAction.TypeText, "LifecycleExecutable", lab); await Act(StepAction.TypeText, "LifecycleName", "Owned lifecycle profile");
            await Act(StepAction.Click, "SaveLifecycle");
            await Wait(() => Directory.GetFiles(Path.Combine(store.RootDirectory, "profiles"), "*.json").Length == 2);
            var profilePath = Directory.GetFiles(Path.Combine(store.RootDirectory, "profiles"), "*.json").Single(p => !p.EndsWith("malformed.json"));
            var request = await LifecycleCommand.LoadRequestAsync(profilePath, token);
            Require(request.Test.Id == test.Id && request.Profile.Executable == lab && request.Provider.ProjectTools?.RequiredBeforeUiCommands.SequenceEqual(["prepare"]) == true, "Profile did not preserve inputs."); await Capture("02-lifecycle"); Pass(stage);

            stage = "Suite form saves selected tests and repetitions";
            await Act(StepAction.Click, "WorkflowTabSuites"); await WaitElement("SuiteTests"); await Capture("03-suites-before-select");
            await driver.ExecuteAsync(new() { Action = StepAction.Click, Selector = "query:{\"label\":\"Owned acceptance\",\"type\":\"ListItem\",\"ancestor\":{\"id\":\"SuiteTests\"}}", TimeoutMs = 5000 }, token);
            await Act(StepAction.TypeText, "SuiteRepetitions", "2"); await Act(StepAction.Click, "SaveSuite");
            await Wait(() => Directory.Exists(Path.Combine(store.RootDirectory, "suites")) && Directory.GetFiles(Path.Combine(store.RootDirectory, "suites"), "*.json").Length == 1);
            var suite = JsonSerializer.Deserialize<TestSuite>(File.ReadAllText(Directory.GetFiles(Path.Combine(store.RootDirectory, "suites"), "*.json").Single()), TestyJson.Options)!;
            Require(suite.Repetitions == 2 && suite.Tests.Count == 1 && suite.Tests[0].Id == test.Id, "Suite form changed test selection or repetitions."); await Capture("03-suites"); Pass(stage);

            stage = "Demonstration view states its target and scope";
            await Act(StepAction.Click, "WorkflowTabDemonstrations"); var demo = await Snapshot();
            Require(demo.Elements.Any(e => e.Name.Contains("limited event recorder")) && demo.Elements.Any(e => e.Name.Contains("Attached process: none")), "Recorder scope/target status missing."); await Capture("04-demonstrations"); Pass(stage);

            stage = "Queue form persists the selected frozen profile";
            var changed = store.LoadSettings(); changed.Model = "current-connection-changed-after-profile"; store.SaveSettings(changed);
            var changedTest = TestyJson.Clone(test); changedTest.Intent = "Changed after the profile was saved."; store.SaveTest(changedTest);
            await Act(StepAction.Click, "WorkflowTabJobs"); await Act(StepAction.Click, "EnqueueLifecycle"); await Wait(() => queue.ListJobs().Count == 1);
            Require(queue.ListJobs().Single().Status == OperationsJobStatus.Queued && queue.ListJobs().Single().Request.Lifecycle!.Test.Id == test.Id
                && queue.ListJobs().Single().Request.Lifecycle!.Test.Intent == test.Intent && queue.ListJobs().Single().Request.Lifecycle!.Provider.Model == "not-contacted", "Queued profile changed after current connection/library edits."); Pass(stage);

            stage = "Schedule form saves bounded interval";
            await WaitEnabled("AddSchedule"); await Act(StepAction.TypeText, "ScheduleInterval", "120"); await Act(StepAction.Click, "AddSchedule"); await Wait(() => queue.ListSchedules().Count == 1);
            Require(queue.ListSchedules().Single().Definition.IntervalSeconds == 120, "Schedule interval not saved."); await WaitEnabled("RefreshJobs"); await Capture("05-jobs"); Pass(stage);

            stage = "Background agent panel reports this workspace has no agent and keeps local workers available";
            var agentPanel = await Snapshot();
            Require(new[] { "AgentStatus", "AgentEnable", "AgentDisable", "AgentPause", "AgentProcessNow", "AgentLocalRuns" }.All(id => agentPanel.Elements.Any(e => e.AutomationId == id)), "Agent panel controls missing.");
            Require(agentPanel.Elements.Any(e => e.AutomationId == "AgentStatus" && e.Name.StartsWith("Not running", StringComparison.Ordinal)), "An owned temporary workspace must not report a running agent.");
            Require(agentPanel.Elements.Any(e => e.AutomationId == "ProcessOneJob" && e.IsEnabled) && agentPanel.Elements.Any(e => e.AutomationId == "StartLocalWorker" && e.IsEnabled), "Local worker controls were disabled without a running agent.");
            await Capture("05b-agent-panel"); Pass(stage);

            stage = "Machines tab requires the elevated agent before Hyper-V actions";
            await Act(StepAction.Click, "WorkflowTabMachines"); await WaitElement("MachinesGrid"); var machinesTab = await Snapshot();
            Require(new[] { "MachinesGrid", "MachinesRefresh", "MachineSetup", "MachineForget", "MachineTest", "MachineExe", "MachineStage", "MachineMode", "MachineRun", "MachineSchedule" }.All(id => machinesTab.Elements.Any(e => e.AutomationId == id)), "Machines controls missing.");
            Require(machinesTab.Elements.Any(e => e.AutomationId == "MachinesStatus" && e.Name.Contains("Enable the background agent", StringComparison.Ordinal)), "Machines tab did not explain the agent requirement.");
            await Capture("05c-machines"); await Act(StepAction.Click, "WorkflowTabJobs"); await WaitEnabled("RefreshJobs"); Pass(stage);

            stage = "Queue cancellation and explicit fresh rerun preserve attempt history";
            await SelectRow("OperationsJobs"); await Act(StepAction.Click, "CancelJob"); await Wait(() => queue.ListJobs().Single().Status == OperationsJobStatus.Cancelled); await WaitEnabled("RerunJob");
            await SelectRow("OperationsJobs"); await Act(StepAction.Click, "RerunJob"); await Wait(() => queue.ListJobs().Count == 2);
            Require(queue.ListJobs().Count(j => j.Status == OperationsJobStatus.Cancelled) == 1 && queue.ListJobs().Count(j => j.Status == OperationsJobStatus.Queued && j.PreviousJobId != null) == 1, "Fresh rerun overwrote history or did not create a new attempt."); await WaitEnabled("RefreshJobs"); Pass(stage);

            stage = "Schedule pause and resume use the selected durable record";
            await SelectRow("OperationsSchedules"); await Act(StepAction.Click, "PauseSchedule"); await Wait(() => !queue.ListSchedules().Single().Definition.Enabled); await WaitEnabled("ResumeSchedule");
            await SelectRow("OperationsSchedules"); await Act(StepAction.Click, "ResumeSchedule"); await Wait(() => queue.ListSchedules().Single().Definition.Enabled); await WaitEnabled("RefreshJobs"); Pass(stage);

            stage = "Workspace controls expose backup and fresh-directory restore";
            await Act(StepAction.Click, "WorkflowTabWorkspace"); var workspace = await Snapshot();
            Require(new[] { "BackupWorkspace", "RestoreWorkspace", "MigrateWorkspace", "OpenOtherWorkspace" }.All(id => workspace.Elements.Any(e => e.AutomationId == id)), "Workspace controls missing.");
            await Act(StepAction.Click, "MigrateWorkspace"); await Wait(() => File.Exists(Path.Combine(store.RootDirectory, "workspace-format.json"))); await WaitEnabled("BackupWorkspace"); await Capture("06-workspace"); Pass(stage);

            stage = "Returning to Studio reloads saved project without restart";
            await Act(StepAction.Click, "NavLibrary"); await WaitGone("WorkflowStatus"); // Leaving Automate removes its page and reloads the library.
            stage = "Step options edits persist through the library";
            await SelectRow("StepsEditor"); await Act(StepAction.Click, "StepOptions"); await WaitElement("StepOptionsTimeout");
            await Capture("07-step-options");
            await Act(StepAction.TypeText, "StepOptionsTimeout", "3500"); await Act(StepAction.Click, "ApplyStepOptions"); await Act(StepAction.Click, "SaveTest");
            await Wait(() => store.LoadTests().Single(t => t.Id == test.Id).Steps.Single().TimeoutMs == 3500); Pass(stage);
            stage = "Returning to Studio reloads saved project without restart";
            await Act(StepAction.Click, "NavSettings");
            await WaitElement("ProviderCredential"); await driver.CaptureAsync(Path.Combine(artifacts, "07-connection.png"), token);
            var connection = await Snapshot(); Require(new[] { "StoreCredential", "RemoveCredential", "TestConnection" }.All(id => connection.Elements.Any(e => e.AutomationId == id)), "Credential setup missing."); Pass(stage);

            stage = "Owned process closes without running queued work";
            Require(queue.ListJobs().All(j => j.Status is OperationsJobStatus.Queued or OperationsJobStatus.Cancelled) && queue.ListJobs().All(j => j.StartedAt is null), "A queue entry ran without an explicit worker.");
            process.CloseMainWindow(); await process.WaitForExitAsync(token); Require(process.ExitCode == 0, "Studio exited abnormally."); Pass(stage); report.CompletedAllScenarios = true;
        }
        catch (Exception ex)
        {
            try { WorkspaceStore.WriteAtomic(Path.Combine(artifacts, "failure-tree.json"), await driver.SnapshotAsync(CancellationToken.None)); await driver.CaptureAsync(Path.Combine(artifacts, "failure.png"), CancellationToken.None); } catch { }
            report.Checks.Add(new() { Name = stage, Driver = "Studio UIA", Passed = false, Expected = RunStatus.Passed, Actual = ex is OperationCanceledException ? RunStatus.Cancelled : RunStatus.Failed, Message = ex.ToString() });
        }
        finally
        {
            if (process != null) { try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } } catch (Exception ex) { report.CompletedAllScenarios = false; report.Checks.Add(new() { Name = "Owned cleanup", Passed = false, Message = ex.Message }); } process.Dispose(); }
            report.Finish(ct); WorkspaceStore.WriteAtomic(Path.Combine(artifacts, "workflow-studio-report.json"), report);
        }
        return report;
        void Pass(string name) => report.Checks.Add(new() { Name = name, Driver = "Studio UIA", Passed = true, Expected = RunStatus.Passed, Actual = RunStatus.Passed, Message = "Observed in the owned Studio process and verified against persisted workspace data." });
        async Task Wait(Func<bool> condition) { var watch = Stopwatch.StartNew(); while (!Holds(condition)) { token.ThrowIfCancellationRequested(); if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Expected saved state did not appear."); await Task.Delay(100, token); } }
        // Studio replaces workspace files atomically; a read that lands on the replacement fails transiently and is retried.
        static bool Holds(Func<bool> condition) { try { return condition(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; } }
        static T Read<T>(Func<T> read) { for (var attempt = 1; ; attempt++) { try { return read(); } catch (Exception ex) when (attempt < 20 && ex is IOException or UnauthorizedAccessException) { Thread.Sleep(50); } } }
        async Task<UiSnapshot> Snapshot() { var result = await driver.SnapshotAsync(token); if (result.IsTruncated) throw new InvalidDataException("Workflow tree is incomplete."); return result; }
        async Task WaitElement(string id) { for (int i = 0; i < 80; i++) { if ((await Snapshot()).Elements.Any(e => e.AutomationId == id)) return; await Task.Delay(100, token); } throw new TimeoutException("Control missing: " + id); }
        async Task WaitGone(string id) { for (int i = 0; i < 80; i++) { if (!(await Snapshot()).Elements.Any(e => e.AutomationId == id)) return; await Task.Delay(100, token); } throw new TimeoutException("Control still present: " + id); }
        async Task WaitEnabled(string id) { for (int i = 0; i < 150; i++) { if ((await Snapshot()).Elements.Any(e => e.AutomationId == id && e.IsEnabled)) return; await Task.Delay(100, token); } throw new TimeoutException("Control stayed disabled: " + id); }
        async Task Act(StepAction action, string id, string value = "") => await driver.ExecuteAsync(new() { Action = action, Selector = "id:" + id, Value = value, TimeoutMs = 10000 }, token);
        async Task SelectRow(string grid)
        {
            var snapshot = await Snapshot(); var matches = UiSelectors.Find(snapshot, "query:{\"type\":\"DataItem\",\"ancestor\":{\"id\":\"" + grid + "\"}}");
            // Prefer a visible row; a row scrolled below the page is still selectable through UI Automation.
            var row = matches.FirstOrDefault(e => !e.IsOffscreen) ?? matches.FirstOrDefault() ?? throw new InvalidOperationException("No job/schedule row to select.");
            // Editable WPF rows also expose Invoke to begin editing. Select explicitly
            // so the subsequent row-level command has a selected item.
            await driver.ExecuteAsync(new() { Action = StepAction.Select, Selector = row.Selector, TimeoutMs = 10000 }, token);
        }
        async Task Capture(string name) { WorkspaceStore.WriteAtomic(Path.Combine(artifacts, name + ".json"), await Snapshot()); await driver.CaptureAsync(Path.Combine(artifacts, name + ".png"), token); }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    }
}
