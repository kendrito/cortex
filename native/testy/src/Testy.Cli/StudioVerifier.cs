using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Testy.Cli.Mcp;
using Testy.Core;
using Testy.Windows;

namespace Testy.Cli;

/// <summary>Exercises the actual Studio interface in a disposable, isolated workspace.</summary>
internal static class StudioVerifier
{
    public static async Task<VerificationReport> VerifyRegressionsAsync(string executable, string artifacts, CancellationToken ct, ProviderSettings? liveSettings = null, string? labExecutable = null)
    {
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable) || Path.GetFileName(executable) != "Testy.Studio.exe") throw new ArgumentException("verify-studio-regressions requires the included Testy.Studio.exe.");
        if (liveSettings?.Kind == ProviderKind.Offline) throw new ArgumentException("Natural-language authoring verification requires a real AI provider.");
        if (liveSettings is not null)
        {
            if (labExecutable is null || !File.Exists(labExecutable) || Path.GetFileName(labExecutable) != "Testy.TestLab.exe") throw new ArgumentException("Live authoring requires --lab TESTY_TESTLAB_EXE.");
            labExecutable = Path.GetFullPath(labExecutable);
            var labs = Process.GetProcessesByName("Testy.TestLab");
            try { if (labs.Length > 0) throw new InvalidOperationException("Close existing Testy TestLab windows before live authoring verification."); }
            finally { foreach (var existing in labs) existing.Dispose(); }
        }
        Directory.CreateDirectory(artifacts);
        var workspace = Path.Combine(artifacts, "workspace-" + Guid.NewGuid().ToString("N"));
        var store = new WorkspaceStore(workspace);
        store.SaveSettings(liveSettings is null ? new ProviderSettings { Kind = ProviderKind.Offline, AiDirectedExecution = false } : TestyJson.Clone(liveSettings));
        var historyTest = new TestCase { Name = "History target", Steps = [new TestStep { Title = "Fixture-only editor step", Action = StepAction.AssertExists, Selector = "id:FixtureControl" }] };
        var filterTest = new TestCase { Name = "Filter target", Steps = [new TestStep { Title = "Fixture-only editor step", Action = StepAction.AssertExists, Selector = "id:FixtureControl" }] };
        store.SaveTest(historyTest); store.SaveTest(filterTest);
        // A deliberately labeled history fixture exercises navigation; it is not an executed application test.
        store.SaveRun(new RunResult { TestId = historyTest.Id, TestName = historyTest.Name, Status = RunStatus.Failed, FinishedAt = DateTimeOffset.UtcNow,
            Summary = "Fixture-only navigation record. No application test was executed." });
        var report = new VerificationReport { Executable = executable, PlannedChecks = liveSettings is null ? 3 : 4 };
        var reportPath = Path.Combine(artifacts, "studio-regression-report.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, TestyJson.Options), ct);
        using var studio = Process.Start(StudioLaunch.StartInfo(executable, workspace)) ?? throw new InvalidOperationException("Studio could not start.");
        report.OwnedProcessId = studio.Id;
        Process? lab = null;
        using var driver = new UiAutomationDriver { MaxElements = 3500 };
        string stage = "Open regression workspace";
        try
        {
            await WaitForAsync(() => { studio.Refresh(); return !studio.HasExited && studio.MainWindowHandle != 0; }, ct);
            await driver.AttachAsync(studio.Id, ct);
            stage = "Create test through filtered library";
            await Type(driver, "SearchTests", "Filter target", ct);
            await Click(driver, "NewTest", ct);
            await WaitForValue(driver, "TestName", "New functional test", ct);
            Check(await Value(driver, "SearchTests", ct) == "", "New test did not clear its conflicting library filter.");
            await Type(driver, "TestName", "Created through filtered library", ct);
            await Click(driver, "SaveTest", ct);
            Check(store.LoadTests().Any(t => t.Name == "Created through filtered library"), "The new filtered-library test was not saved.");
            Pass(report, "New test clears conflicting filters and selects the new editor", "The created test became selected and was persisted under its edited name.");

            stage = "Open history while library is filtered";
            await Type(driver, "SearchTests", "Filter target", ct);
            await Select(driver, "TestLibrary", "Filter target", ct);
            await Click(driver, "NavRuns", ct);
            await WaitForElement(driver, "id:RunsGrid", ct);
            var root = AutomationElement.FromHandle(new IntPtr(driver.Target!.WindowHandle));
            var grid = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "RunsGrid"));
            var row = grid?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem));
            Check(row is not null && row.Current.ProcessId == studio.Id, "Fixture history row was not available.");
            Check(row!.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection), "Fixture history row was not selectable.");
            ((SelectionItemPattern)selection).Select();
            await Click(driver, "ViewRun", ct);
            await WaitForValue(driver, "TestName", "History target", ct);
            Check(await Value(driver, "SearchTests", ct) == "", "Opening history did not clear the conflicting library filter.");
            Pass(report, "History opens the matching editor despite a library filter", "The selected history record opened History target and cleared the Filter target filter.");
            await Evidence(driver, artifacts, "history-navigation", ct);

            if (liveSettings is not null)
            {
                stage = "Author a real natural-language test through Studio";
                // Sample app is a technical-details override: the AI normally finds the app from the test.
                await Toggle(driver, "ShowTechnicalDetails", true, ct);
                await Click(driver, "LaunchLab", ct);
                await Toggle(driver, "ShowTechnicalDetails", false, ct);
                await WaitForAsync(() =>
                {
                    foreach (var candidate in Process.GetProcessesByName("Testy.TestLab"))
                    {
                        if (lab is null && candidate.StartTime.ToUniversalTime() >= studio.StartTime.ToUniversalTime() &&
                            string.Equals(candidate.MainModule?.FileName, labExecutable, StringComparison.OrdinalIgnoreCase)) lab = candidate;
                        else candidate.Dispose();
                    }
                    return lab is not null && File.Exists(Path.Combine(workspace, "inspection", "latest.png"));
                }, ct);
                var previousTests = store.LoadTests().Select(t => t.Id).ToHashSet();
                var previousRuns = store.LoadRuns().Count;
                await Type(driver, "AiPrompt", "Create a read-only test of the attached Customer Desk form: assert the customer name field exists, assert the email address field exists, and assert the Add customer button is enabled. Use exactly those three assertions against the observed controls. Do not add or modify customers.", ct);
                await Click(driver, "GenerateTest", ct);
                await WaitForAsync(() => store.LoadTests().Any(t => !previousTests.Contains(t.Id)), ct, 120000);
                var generated = store.LoadTests().Single(t => !previousTests.Contains(t.Id));
                TestValidator.Validate(generated);
                Check(generated.Steps.Count == 3 && generated.Steps.All(s => TestValidator.IsAssertion(s.Action)), "Natural-language authoring did not preserve the requested three read-only assertions.");
                Check(generated.Steps.Any(s => s.Action == StepAction.AssertExists && s.Selector == "id:CustomerName") &&
                    generated.Steps.Any(s => s.Action == StepAction.AssertExists && s.Selector == "id:CustomerEmail") &&
                    generated.Steps.Any(s => s.Action == StepAction.AssertEnabled && s.Selector == "id:AddCustomer"),
                    "Natural-language authoring did not bind the required assertions to observed Lab controls.");
                await WaitForValue(driver, "TestName", generated.Name, ct);
                await WaitForEnabled(driver, "RunTest", ct);
                Check(store.LoadRuns().Count == previousRuns, "Generate test unexpectedly executed the generated workflow.");
                Pass(report, "Natural-language GUI authoring uses the actual configured AI provider", $"{liveSettings.Kind} generated and selected a persisted, validated three-assertion test using actual Lab selectors; it was not automatically executed.");
                await Evidence(driver, artifacts, "natural-language-authoring", ct);
                await Select(driver, "TestLibrary", "History target", ct);
            }

            stage = "Retain invalid unsaved edits across selection and close";
            var invalidName = new string('X', 1001);
            await Type(driver, "TestName", invalidName, ct);
            await Click(driver, "SaveTest", ct);
            Check(store.LoadTests().Single(t => t.Id == historyTest.Id).Name == "History target", "Rejected draft overwrote the saved test.");
            await Select(driver, "TestLibrary", "Filter target", ct);
            Check(await Value(driver, "TestName", ct) == invalidName, "Selection lost invalid unsaved editor text.");
            studio.CloseMainWindow();
            await Task.Delay(500, ct); studio.Refresh();
            Check(!studio.HasExited, "Studio closed despite a failed draft save.");
            Check(await Value(driver, "TestName", ct) == invalidName, "Close rejection did not retain unsaved editor text.");
            await Evidence(driver, artifacts, "retained-invalid-edit", ct);
            await Type(driver, "TestName", "Recovered history target", ct);
            await Click(driver, "SaveTest", ct);
            Check(store.LoadTests().Single(t => t.Id == historyTest.Id).Name == "Recovered history target", "Corrected draft did not save to the original test identity.");
            studio.CloseMainWindow();
            using var exitTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            exitTimeout.CancelAfter(5000);
            await studio.WaitForExitAsync(exitTimeout.Token);
            Pass(report, "Invalid edits survive selection and close, then save and close after correction", "The 1001-character name was rejected, original persisted identity stayed intact, and the corrected name saved before normal exit.");
            ct.ThrowIfCancellationRequested();
            report.CompletedAllScenarios = report.Checks.Count == report.PlannedChecks;
        }
        catch (Exception ex)
        {
            report.Checks.Add(new VerificationCheck { Name = stage, Driver = "Studio UI Automation", Passed = false, Expected = RunStatus.Passed, Actual = RunStatus.Failed, Message = ex.ToString() });
            try { if (!studio.HasExited && driver.Target is not null) await Evidence(driver, artifacts, "failure", CancellationToken.None); } catch { }
        }
        finally
        {
            await CloseOwned(studio);
            if (lab is not null) { await CloseOwned(lab); lab.Dispose(); }
            report.Finish(ct);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, TestyJson.Options), CancellationToken.None);
        }
        return report;
    }

    public static async Task<VerificationReport> VerifyAsync(string executable, string labExecutable, string artifacts, CancellationToken cancellationToken, ProviderSettings? liveSettings = null)
    {
        executable = Path.GetFullPath(executable);
        labExecutable = Path.GetFullPath(labExecutable);
        if (!File.Exists(executable) || Path.GetFileName(executable) != "Testy.Studio.exe") throw new ArgumentException("verify-studio requires the included Testy.Studio.exe.");
        if (!File.Exists(labExecutable) || Path.GetFileName(labExecutable) != "Testy.TestLab.exe") throw new ArgumentException("--lab requires the included Testy.TestLab.exe.");
        if (liveSettings?.Kind == ProviderKind.Offline) throw new ArgumentException("Live Studio verification requires a configured AI provider; omit --settings for replay-only checks.");
        var existingLabs = Process.GetProcessesByName("Testy.TestLab");
        try { if (existingLabs.Length > 0) throw new InvalidOperationException("Close existing Testy TestLab windows before verification so the verifier can own its fixture process."); }
        finally { foreach (var existing in existingLabs) existing.Dispose(); }
        Directory.CreateDirectory(artifacts);
        var workspace = Path.Combine(artifacts, "workspace-" + Guid.NewGuid().ToString("N"));
        var store = new WorkspaceStore(workspace);
        // Start with explicit replay. An optional real-provider phase then switches modes through the GUI.
        store.SaveSettings(new ProviderSettings { Kind = ProviderKind.Offline, AiDirectedExecution = false, NativeComputerUse = false, SupportsImages = false, MaximumAgentTurns = 12 });
        var report = new VerificationReport { Executable = executable, PlannedChecks = liveSettings is null ? 19 : 21 };
        await File.WriteAllTextAsync(Path.Combine(artifacts, "studio-report.json"), JsonSerializer.Serialize(report, TestyJson.Options), cancellationToken);
        // The technical-details and agent-access checks toggle switches; the session options start them off and keep the user's saved preferences
        // untouched. The listener's token is given for this session so the HTTP checks can present it without reading the clipboard.
        var mcpToken = "verify-studio-" + Guid.NewGuid().ToString("N");
        using var studio = Process.Start(StudioLaunch.StartInfo(executable, workspace, hidden: true, "--mcp-token", mcpToken, "--mcp-port", "0")) ?? throw new InvalidOperationException("Studio could not start.");
        report.OwnedProcessId = studio.Id;
        Process? lab = null;
        using var driver = new UiAutomationDriver { MaxElements = 3500 };
        var stage = "Open Studio";
        try
        {
            await WaitForAsync(() => { studio.Refresh(); if (studio.HasExited) throw new InvalidOperationException($"Studio exited with {studio.ExitCode}."); return studio.MainWindowHandle != 0; }, cancellationToken);
            await driver.AttachAsync(studio.Id, cancellationToken);
            var initial = await driver.SnapshotAsync(cancellationToken);
            string[] required = ["NavLibrary", "NavRuns", "NavWorkflows", "NavHelp", "NavInspector", "NavSettings", "ShowTechnicalDetails", "TargetStatus", "GlobalSearch", "ActivityButton",
                "TestLibrary", "TestName", "NewTest", "SaveTest", "DuplicateTest", "DeleteTest", "RunTest", "StepsEditor", "StepOptions", "AiPrompt", "GenerateTest"];
            Check(required.All(id => initial.Elements.Any(e => e.AutomationId == id)), "Studio is missing a primary control.");
            // The user never picks the app: Change app and Sample app appear only as technical-details overrides.
            // WPF keeps collapsed controls in the automation tree as offscreen, so only on-screen ones count.
            Check(!initial.Elements.Any(e => e.AutomationId is "ChangeApp" or "LaunchLab" && !e.IsOffscreen), "Manual app selection is visible without technical details.");
            // Navigation stays plain buttons (Invoke), never toggle buttons; the technical-details switch is a toggle.
            string[] navigation = ["NavLibrary", "NavRuns", "NavWorkflows", "NavHelp", "NavSettings", "NavInspector"];
            Check(navigation.All(id => initial.Elements.Single(e => e.AutomationId == id).ClassName == "Button"), "A navigation item is not a plain button.");
            Check(initial.Elements.Single(e => e.AutomationId == "ShowTechnicalDetails").ClassName == "CheckBox", "Show technical details is not a toggle.");
            Check(!string.IsNullOrWhiteSpace(initial.Target.Title), "Studio's main window has no title.");
            Pass(report, "Studio opens and exposes primary controls", $"Found all {required.Length} primary controls; navigation items are plain buttons.");
            await Evidence(driver, artifacts, "01-library", cancellationToken);

            stage = "Seed tests";
            var seeds = store.LoadTests();
            Check(seeds.Count == 4, $"Expected 4 initial tests, found {seeds.Count}.");
            foreach (var seed in seeds) TestValidator.Validate(seed);
            Pass(report, "Sample tests are persisted and valid", $"All {seeds.Count} seed tests validated.");

            stage = "Launch lab from Studio";
            await Toggle(driver, "ShowTechnicalDetails", true, cancellationToken);
            await Click(driver, "LaunchLab", cancellationToken);
            await Toggle(driver, "ShowTechnicalDetails", false, cancellationToken);
            await WaitForAsync(() =>
            {
                var candidates = Process.GetProcessesByName("Testy.TestLab");
                foreach (var candidate in candidates)
                {
                    if (lab is null && candidate.StartTime.ToUniversalTime() >= studio.StartTime.ToUniversalTime() &&
                        string.Equals(candidate.MainModule?.FileName, labExecutable, StringComparison.OrdinalIgnoreCase)) lab = candidate;
                    else candidate.Dispose();
                }
                return lab is not null && File.Exists(Path.Combine(workspace, "inspection", "latest.png"));
            }, cancellationToken);
            await WaitForName(driver, "TargetStatus", "Connected", cancellationToken);
            Pass(report, "Open test lab launches and attaches the fixture", $"Attached the owned fixture process {lab!.Id}; target screenshot was saved and the app card reads Connected.");

            stage = "Run sample through Studio";
            await Select(driver, "TestLibrary", "Create a customer", cancellationToken);
            await Click(driver, "RunTest", cancellationToken);
            await WaitForAsync(() => store.LoadRuns().Any(r => r.TestName == "Create a customer" && r.FinishedAt is not null), cancellationToken, 30000);
            var sample = store.LoadRuns().First(r => r.TestName == "Create a customer");
            Check(sample.Status == RunStatus.Passed && sample.Steps.Count == 5, sample.Summary);
            Check(sample.Steps.All(s => File.Exists(s.ScreenshotPath) && s.Snapshot is not null), "Saved GUI run lacks evidence.");
            Check(!File.Exists(Path.Combine(sample.ArtifactDirectory, "computer-agent.json")), "Explicit replay unexpectedly invoked an AI agent.");
            Pass(report, "Run a sample through explicit replay", sample.Summary, sample.ArtifactDirectory);
            await WaitForEnabled(driver, "RunTest", cancellationToken);
            await Evidence(driver, artifacts, "02-timeline", cancellationToken);

            stage = "Last run view";
            // The run opens on Last run: the run trace lists every step, the step list composes one runner message per step,
            // and choosing a step on the trace moves the screenshot viewer and the step list to it.
            var lastRun = await CompleteSnapshot(driver, cancellationToken);
            Check(lastRun.Elements.Count(e => e.AutomationId == "RunTrace") == 1 && lastRun.Elements.Any(e => e.AutomationId == "RunTrace" && e.ControlType == "List" && !e.IsOffscreen),
                "The Last run view does not show the run trace after the run.");
            var traceSteps = lastRun.Elements.Where(e => e.AutomationId.StartsWith("RunTraceStep", StringComparison.Ordinal)).ToList();
            Check(traceSteps.Count == 5 && Enumerable.Range(1, 5).All(n => traceSteps.Count(e => e.AutomationId == $"RunTraceStep{n}" && e.ControlType == "ListItem") == 1),
                $"The run trace lists {traceSteps.Count} steps instead of the run's 5.");
            var runnerMessages = UiSelectors.Find(lastRun, "query:{\"type\":\"Text\",\"ancestor\":{\"id\":\"LastRunSteps\"}}")
                .Where(e => e.Name.StartsWith("Found ", StringComparison.Ordinal) || e.Name.StartsWith("Read ", StringComparison.Ordinal)).Select(e => e.Name).ToList();
            Check(runnerMessages.Count == 5, $"The step list shows {runnerMessages.Count} runner messages instead of 5.");
            var captionBefore = lastRun.Elements.Single(e => e.AutomationId == "ScreenshotCaption").Name;
            await Click(driver, "RunTraceStep4", cancellationToken);
            var caption = await WaitForNameStart(driver, "ScreenshotCaption", "Step 4", cancellationToken);
            Check(caption != captionBefore, "Choosing step 4 on the run trace did not change the screenshot caption.");
            var synced = await CompleteSnapshot(driver, cancellationToken);
            var runRows = UiSelectors.Find(synced, "query:{\"type\":\"ListItem\",\"ancestor\":{\"id\":\"LastRunSteps\"}}");
            Check(runRows.Count == 5 && IsSelected(runRows[3]) && runRows.Count(IsSelected) == 1, "Choosing step 4 on the run trace did not select step 4 in the step list.");
            Pass(report, "Last run shows the run trace, step messages and screenshot",
                $"RunTrace lists 5 steps; the step list shows 5 runner messages (\"{runnerMessages[0]}\" … \"{runnerMessages[^1]}\"); choosing RunTraceStep4 changed the caption from \"{captionBefore}\" to \"{caption}\" and selected step 4 in the list.");
            await Evidence(driver, artifacts, "02b-last-run", cancellationToken);

            stage = "Activity panel";
            await Click(driver, "ActivityButton", cancellationToken);
            await WaitForElement(driver, "id:ActivityList", cancellationToken);
            var activity = await CompleteSnapshot(driver, cancellationToken);
            var activityEntries = UiSelectors.Find(activity, "query:{\"type\":\"ListItem\",\"ancestor\":{\"id\":\"ActivityList\"}}");
            Check(activity.Elements.Any(e => e.AutomationId == "ActivityPanel" && !e.IsOffscreen), "The Activity panel did not open.");
            Check(activityEntries.Count >= 3 && activityEntries.Any(e => e.Name.Contains("Run finished", StringComparison.Ordinal) && e.Name.Contains("Create a customer", StringComparison.Ordinal)),
                $"The Activity panel lists {activityEntries.Count} entries and none records the finished run of Create a customer.");
            await Evidence(driver, artifacts, "02c-activity", cancellationToken);
            await Click(driver, "ActivityButton", cancellationToken);
            Pass(report, "Activity panel lists entries after a run", $"{activityEntries.Count} entries are shown, including the finished run of Create a customer.");

            stage = "Inspector navigation";
            await Click(driver, "NavInspector", cancellationToken);
            await WaitForElement(driver, "id:ElementsGrid", cancellationToken);
            var inspector = await driver.SnapshotAsync(cancellationToken);
            Check(inspector.Elements.Any(e => !e.IsOffscreen && (e.Name == "id:CustomerDeskWindow" || e.Value == "id:CustomerDeskWindow")), "Inspector does not display the selected target's root selector.");
            Pass(report, "Inspector displays attached target controls", "The target CustomerDeskWindow selector is visible in the inspector grid.");
            await Evidence(driver, artifacts, "03-inspector", cancellationToken);

            stage = "Run history navigation";
            await Click(driver, "NavRuns", cancellationToken);
            var history = await driver.SnapshotAsync(cancellationToken);
            Check(history.Elements.Any(e => e.Name == "Create a customer" || e.Value == "Create a customer"), "Saved run is not visible in history.");
            Pass(report, "Run history displays persisted results", "The saved Create a customer run is visible.");
            await Evidence(driver, artifacts, "04-history", cancellationToken);

            stage = "Provider settings";
            await Click(driver, "NavSettings", cancellationToken);
            await WaitForElement(driver, "id:ProviderPicker", cancellationToken);
            await Select(driver, "ProviderPicker", "Offline", cancellationToken);
            await Click(driver, "SaveConnection", cancellationToken);
            await WaitForAsync(() => store.LoadSettings().Kind == ProviderKind.Offline, cancellationToken);
            var saved = Read(store.LoadSettings);
            Check(!saved.AiDirectedExecution && !saved.NativeComputerUse && !saved.SupportsImages && saved.MaximumAgentTurns == 12,
                "Saving provider settings lost explicit execution, native-tool, image, or turn-budget settings.");
            Pass(report, "Provider and execution settings persist through the GUI", "Offline/replay and non-default native, image and turn-budget values survived Save connection.");
            await Evidence(driver, artifacts, "05-settings", cancellationToken);

            stage = "Create, rename, duplicate and delete";
            await Click(driver, "NavLibrary", cancellationToken);
            await Click(driver, "NewTest", cancellationToken);
            await WaitForAsync(() => store.LoadTests().Count == 5, cancellationToken);
            await Type(driver, "TestName", "GUI verification draft", cancellationToken);
            await Click(driver, "SaveTest", cancellationToken);
            await WaitForAsync(() => store.LoadTests().Any(t => t.Name == "GUI verification draft"), cancellationToken);
            await Click(driver, "DuplicateTest", cancellationToken);
            await WaitForAsync(() => store.LoadTests().Count == 6 && store.LoadTests().Any(t => t.Name == "GUI verification draft (copy)"), cancellationToken);
            await Click(driver, "DeleteTest", cancellationToken);
            await WaitForAsync(() => store.LoadTests().Count == 5 && store.LoadTests().All(t => t.Name != "GUI verification draft (copy)"), cancellationToken);
            Pass(report, "Create, rename, duplicate and delete tests", "New draft saved, renamed, duplicated and duplicate deleted through the GUI.");

            stage = "Technical details";
            // Off by default: the steps read as sentences. On: the raw action, selector and value columns appear; off again hides them.
            Check(!SelectorColumnVisible(await CompleteSnapshot(driver, cancellationToken)), "The step selector column is visible while technical details are off.");
            await Toggle(driver, "ShowTechnicalDetails", true, cancellationToken);
            await WaitForSnapshot(driver, SelectorColumnVisible, "Turning on technical details did not show the step selector column.", cancellationToken);
            await Evidence(driver, artifacts, "05b-technical-details", cancellationToken);
            await Toggle(driver, "ShowTechnicalDetails", false, cancellationToken);
            await WaitForSnapshot(driver, s => !SelectorColumnVisible(s), "Turning off technical details did not hide the step selector column.", cancellationToken);
            Pass(report, "Show technical details reveals and hides the step selectors", "The Selector column of the steps editor appeared with technical details on and disappeared when they were turned off.");

            stage = "Global search";
            const string searchResult = "query:{\"label\":\"Create a customer\",\"type\":\"ListItem\",\"ancestor\":{\"id\":\"GlobalSearchResults\"}}";
            await Type(driver, "GlobalSearch", "Create a customer", cancellationToken);
            await WaitForElement(driver, searchResult, cancellationToken);
            await Evidence(driver, artifacts, "05c-global-search", cancellationToken);
            await driver.ExecuteAsync(new TestStep { Title = "Open the search result", Action = StepAction.Select, Selector = searchResult, TimeoutMs = 10000 }, cancellationToken);
            await WaitForValue(driver, "TestName", "Create a customer", cancellationToken);
            Check(await Value(driver, "GlobalSearch", cancellationToken) == "", "Opening a search result did not clear the search box.");
            Pass(report, "Global search finds and opens a test", "GlobalSearch listed Create a customer; choosing the result opened it in the editor and cleared the search.");

            stage = "Offline authoring through Studio";
            await Type(driver, "AiPrompt", "click id:ResetButton\nassert text id:StatusMessage = \"Ready for a new customer.\"", cancellationToken);
            await Click(driver, "GenerateTest", cancellationToken);
            await WaitForAsync(() => store.LoadTests().Any(t => t.Category == "Offline commands" && t.Steps.Count == 2), cancellationToken, 30000);
            await Click(driver, "RunTest", cancellationToken);
            await WaitForAsync(() => store.LoadRuns().Any(r => r.TestName == "Command script" && r.FinishedAt is not null), cancellationToken, 30000);
            var generatedRun = store.LoadRuns().First(r => r.TestName == "Command script");
            Check(generatedRun.Status == RunStatus.Passed, generatedRun.Summary);
            Pass(report, "Generate and run an offline command test through Studio", generatedRun.Summary, generatedRun.ArtifactDirectory);
            await WaitForEnabled(driver, "RunTest", cancellationToken);
            await Evidence(driver, artifacts, "06-generated-test", cancellationToken);

            stage = "Tests created by an outside agent appear in Studio";
            // Any MCP client on this PC: the real `Testy.Cli mcp --workspace <the same workspace>` child, driven over stdio while Studio is open on it.
            var agentStart = WorkerCommand.CreateSelfStartInfo();
            agentStart.RedirectStandardInput = true;
            foreach (var argument in new[] { "mcp", "--workspace", workspace }) agentStart.ArgumentList.Add(argument);
            using (var server = Process.Start(agentStart) ?? throw new InvalidOperationException("The MCP server process did not start."))
            {
                var serverLog = DrainToFileAsync(server.StandardError, Path.Combine(artifacts, "outside-agent-stderr.txt"));
                await using var agent = new McpLineClient(server.StandardOutput.BaseStream, server.StandardInput.BaseStream);
                await agent.RequestAsync("initialize", new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "outside-agent", ["version"] = "1.0" }
                }, TimeSpan.FromSeconds(45));
                await agent.NotifyAsync("notifications/initialized");
                var created = (await agent.CallToolAsync("create_test", new
                {
                    name = "Outside agent test", intent = "Created through MCP by an outside agent while Studio was open on this workspace.", category = "Outside agents",
                    steps = new object[]
                    {
                        new { title = "Reset the sample app", action = "click", selector = "id:ResetButton" },
                        new { title = "Verify the status", action = "assertText", selector = "id:StatusMessage", value = "Ready for a new customer." }
                    }
                })).RequireOk("create_test").Structured;
                var outsideId = created.GetProperty("testId").GetString()!;
                // A targeted UI Automation find (a full snapshot takes over a second and would hide Studio's real latency); the snapshot confirms it after.
                var studioRoot = AutomationElement.FromHandle(new IntPtr(driver.Target!.WindowHandle));
                var library = studioRoot.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "TestLibrary"))
                    ?? throw new InvalidOperationException("TestLibrary was not found in Studio's UI Automation tree.");
                var outsideItem = new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem), new PropertyCondition(AutomationElement.NameProperty, "Outside agent test"));
                var appeared = Stopwatch.StartNew();
                await WaitForAsync(() => library.FindFirst(TreeScope.Descendants, outsideItem) is not null, cancellationToken, 3000);
                var latency = appeared.ElapsedMilliseconds;
                Check(LibraryLists(await CompleteSnapshot(driver, cancellationToken), "Outside agent test"), "The complete snapshot does not list the test created by the outside agent.");
                Check(await Value(driver, "TestName", cancellationToken) == "Command script", "An outside add must not move the selection away from the open test.");
                await Click(driver, "ActivityButton", cancellationToken);
                await WaitForElement(driver, "id:ActivityList", cancellationToken);
                var outsideActivity = UiSelectors.Find(await CompleteSnapshot(driver, cancellationToken), "query:{\"type\":\"ListItem\",\"ancestor\":{\"id\":\"ActivityList\"}}");
                Check(outsideActivity.Any(e => e.Name.Contains("Outside agent test", StringComparison.Ordinal) && e.Name.Contains("outside tool", StringComparison.Ordinal)),
                    "The Activity panel has no entry for the test added by the outside agent.");
                await Evidence(driver, artifacts, "06b-outside-agent-activity", cancellationToken);
                await Click(driver, "ActivityButton", cancellationToken);
                // Selected with a clean editor, the test follows an outside rename in the list and in the editor.
                await Select(driver, "TestLibrary", "Outside agent test", cancellationToken);
                await WaitForValue(driver, "TestName", "Outside agent test", cancellationToken);
                (await agent.CallToolAsync("update_test", new { testId = outsideId, name = "Outside agent test (renamed)" })).RequireOk("update_test");
                await WaitForValue(driver, "TestName", "Outside agent test (renamed)", cancellationToken);
                await WaitForSnapshot(driver, s => LibraryLists(s, "Outside agent test (renamed)") && !LibraryLists(s, "Outside agent test"), "The list does not show the renamed test.", cancellationToken);
                Pass(report, "Tests created by an outside agent appear in Studio",
                    $"create_test through the real MCP server was listed in TestLibrary after {latency} ms without moving the selection, and logged in Activity; update_test renamed it in the list and in the open editor.");

                stage = "Outside changes to a test with unsaved edits: Reload and Keep my edits";
                // With unsaved edits the outside version is not loaded over them: the bar asks, with both buttons once in the tree.
                await Type(driver, "TestIntent", "Edited in Studio, not saved yet.", cancellationToken);
                (await agent.CallToolAsync("update_test", new { testId = outsideId, intent = "Changed by the agent (first)." })).RequireOk("update_test");
                await WaitForElement(driver, "id:ReloadExternalChange", cancellationToken);
                var changedBar = await CompleteSnapshot(driver, cancellationToken);
                foreach (var id in new[] { "ExternalChangeBar", "ExternalChangeText", "ReloadExternalChange", "KeepMyEdits" }) Check(changedBar.Elements.Count(e => e.AutomationId == id) == 1, $"{id} must appear exactly once while the bar asks about a change.");
                Check(changedBar.Elements.Single(e => e.AutomationId == "ExternalChangeBar").Name == "Changed outside Studio", "The bar's name must say that the test was changed.");
                Check(await Value(driver, "TestIntent", cancellationToken) == "Edited in Studio, not saved yet.", "The unsaved edit must stay while the bar asks.");
                var untouched = store.LoadTests().Single(t => t.Id == outsideId);
                Check(untouched.Intent == "Changed by the agent (first).", "The outside version must stay on disk while the bar asks.");
                // Switching away is refused with the choice; the test stays open.
                await Select(driver, "TestLibrary", "Command script", cancellationToken);
                await Task.Delay(400, cancellationToken);
                Check(await Value(driver, "TestName", cancellationToken) == "Outside agent test (renamed)", "Switching tests while the bar asks must keep the test open.");
                Check(store.LoadTests().Single(t => t.Id == outsideId).Intent == "Changed by the agent (first).", "An implicit commit must not overwrite the outside version.");
                await Evidence(driver, artifacts, "06c-outside-change-bar", cancellationToken);
                await Click(driver, "ReloadExternalChange", cancellationToken);
                await WaitForValue(driver, "TestIntent", "Changed by the agent (first).", cancellationToken);
                await WaitForSnapshot(driver, s => !s.Elements.Any(e => e.AutomationId == "ExternalChangeBar" && !e.IsOffscreen), "Reload did not hide the bar.", cancellationToken);
                await Type(driver, "TestIntent", "Edited again in Studio.", cancellationToken);
                (await agent.CallToolAsync("update_test", new { testId = outsideId, intent = "Changed by the agent (second)." })).RequireOk("update_test");
                await WaitForElement(driver, "id:KeepMyEdits", cancellationToken);
                await Click(driver, "KeepMyEdits", cancellationToken);
                await WaitForSnapshot(driver, s => !s.Elements.Any(e => e.AutomationId == "ExternalChangeBar" && !e.IsOffscreen), "Keep my edits did not hide the bar.", cancellationToken);
                Check(await Value(driver, "TestIntent", cancellationToken) == "Edited again in Studio.", "Keep my edits must keep the editor's text.");
                Check(store.LoadTests().Single(t => t.Id == outsideId).Intent == "Changed by the agent (second).", "Keep my edits must not write to disk by itself.");
                await Click(driver, "SaveTest", cancellationToken);
                await WaitForAsync(() => store.LoadTests().Single(t => t.Id == outsideId).Intent == "Edited again in Studio.", cancellationToken);
                Pass(report, "Outside changes to a test with unsaved edits: Reload and Keep my edits",
                    "An outside update while the intent was edited showed the bar and left both the edit and the file alone; switching tests was refused; Reload loaded the outside version; Keep my edits kept the text, and Save then replaced the file.");

                stage = "Outside deletion of the open test: Save a copy and Discard";
                // Deleted while selected: it leaves the list; the editor keeps its copy and offers Save a copy / Discard.
                (await agent.CallToolAsync("delete_test", new { testId = outsideId })).RequireOk("delete_test");
                await WaitForSnapshot(driver, s => !LibraryLists(s, "Outside agent test (renamed)"), "The deleted test is still listed.", cancellationToken);
                await WaitForElement(driver, "id:DiscardExternalDeleted", cancellationToken);
                var deletedBar = await CompleteSnapshot(driver, cancellationToken);
                foreach (var id in new[] { "ExternalChangeBar", "ExternalChangeText", "SaveExternalDeleted", "DiscardExternalDeleted" }) Check(deletedBar.Elements.Count(e => e.AutomationId == id) == 1, $"{id} must appear exactly once while the bar asks about a deletion.");
                Check(deletedBar.Elements.Single(e => e.AutomationId == "ExternalChangeBar").Name == "Deleted outside Studio", "The bar's name must say that the test was deleted.");
                Check(!deletedBar.Elements.Any(e => (e.AutomationId is "ReloadExternalChange" or "KeepMyEdits") && !e.IsOffscreen), "Reload and Keep my edits do not apply to a deletion (they must be collapsed).");
                Check(deletedBar.Elements.Where(e => e.AutomationId is "SaveExternalDeleted" or "DiscardExternalDeleted").All(e => !e.IsOffscreen), "Save a copy and Discard must be on screen while the bar asks about a deletion.");
                await Type(driver, "TestIntent", "Edited after the deletion.", cancellationToken);
                await Evidence(driver, artifacts, "06d-outside-agent-deleted", cancellationToken);
                await Click(driver, "SaveExternalDeleted", cancellationToken);
                await WaitForAsync(() => store.LoadTests().Any(t => t.Id != outsideId && t.Name == "Outside agent test (renamed)" && t.Intent == "Edited after the deletion."), cancellationToken);
                var copy = store.LoadTests().Single(t => t.Name == "Outside agent test (renamed)");
                Check(copy.Id != outsideId && !File.Exists(Path.Combine(workspace, "tests", outsideId + ".json")), "Save a copy must save under a new id and not restore the deleted file.");
                await WaitForValue(driver, "TestName", "Outside agent test (renamed)", cancellationToken);
                var second = (await agent.CallToolAsync("create_test", new { name = "Outside agent test 2", intent = "Deleted outside Studio and discarded.", category = "Outside agents", steps = new object[] { new { action = "assertExists", selector = "id:ResetButton" } } })).RequireOk("create_test").Structured.GetProperty("testId").GetString()!;
                await WaitForSnapshot(driver, s => LibraryLists(s, "Outside agent test 2"), "The second outside test was not listed.", cancellationToken);
                await Select(driver, "TestLibrary", "Outside agent test 2", cancellationToken);
                await WaitForValue(driver, "TestName", "Outside agent test 2", cancellationToken);
                (await agent.CallToolAsync("delete_test", new { testId = second })).RequireOk("delete_test");
                await WaitForElement(driver, "id:DiscardExternalDeleted", cancellationToken);
                await Click(driver, "DiscardExternalDeleted", cancellationToken);
                await WaitForSnapshot(driver, s => s.Elements.Single(e => e.AutomationId == "TestName").Value != "Outside agent test 2", "Discard did not close the deleted test.", cancellationToken);
                Check(store.LoadTests().All(t => t.Name != "Outside agent test 2"), "Discard must not write the deleted test back.");
                Pass(report, "Outside deletion of the open test: Save a copy and Discard",
                    $"delete_test removed the open test from the list; the bar (named for the deletion) offered Save a copy, which saved the edited copy as {copy.Id}; a second deleted test was discarded without a file.");

                stage = "Studio's own saves are not outside changes; an outside run reaches Results and Activity";
                await Select(driver, "TestLibrary", "Inspect customer form", cancellationToken);
                await WaitForValue(driver, "TestName", "Inspect customer form", cancellationToken);
                await Type(driver, "TestIntent", "Edited and saved in Studio.", cancellationToken);
                await Click(driver, "SaveTest", cancellationToken);
                await WaitForAsync(() => store.LoadTests().Single(t => t.Name == "Inspect customer form").Intent == "Edited and saved in Studio.", cancellationToken);
                var smoke = store.LoadTests().Single(t => t.Name == "Inspect customer form");
                await Task.Delay(1500, cancellationToken); // longer than the watcher's debounce: a report of the save would be logged by now
                await Click(driver, "ActivityButton", cancellationToken);
                await WaitForElement(driver, "id:ActivityList", cancellationToken);
                var ownSave = UiSelectors.Find(await CompleteSnapshot(driver, cancellationToken), "query:{\"type\":\"ListItem\",\"ancestor\":{\"id\":\"ActivityList\"}}");
                Check(!ownSave.Any(e => e.Name.Contains("Inspect customer form", StringComparison.Ordinal) && e.Name.Contains("outside tool", StringComparison.Ordinal)), "Studio's own save must not be reported as an outside change.");
                await Click(driver, "ActivityButton", cancellationToken);
                var previousRuns = store.LoadRuns().Select(r => r.Id).ToHashSet();
                var outsideRun = (await agent.CallToolAsync("run_test", new { testId = smoke.Id, pid = lab!.Id, waitSeconds = 90 }, timeout: TimeSpan.FromSeconds(120))).RequireOk("run_test").Structured;
                Check(outsideRun.GetProperty("status").GetString() == "passed" && !outsideRun.GetProperty("running").GetBoolean(), "The outside run of the read-only smoke test did not pass: " + outsideRun.GetProperty("summary").GetString());
                var outsideRunId = outsideRun.GetProperty("runId").GetString()!;
                Check(!previousRuns.Contains(outsideRunId) && File.Exists(Path.Combine(workspace, "runs", outsideRunId + ".json")), "The outside run was not saved to the workspace.");
                await Click(driver, "ActivityButton", cancellationToken);
                await WaitForSnapshot(driver, s => UiSelectors.Find(s, "query:{\"type\":\"ListItem\",\"ancestor\":{\"id\":\"ActivityList\"}}").Any(e => e.Name.Contains("Run of “Inspect customer form” finished", StringComparison.Ordinal) && e.Name.Contains("outside tool", StringComparison.Ordinal)),
                    "The Activity panel has no entry for the run made by the outside agent.", cancellationToken);
                await Evidence(driver, artifacts, "06e-outside-run-activity", cancellationToken);
                await Click(driver, "ActivityButton", cancellationToken);
                await Click(driver, "NavRuns", cancellationToken);
                await WaitForElement(driver, "id:RunsGrid", cancellationToken);
                var results = await CompleteSnapshot(driver, cancellationToken);
                Check(UiSelectors.Find(results, "query:{\"type\":\"DataItem\",\"ancestor\":{\"id\":\"RunsGrid\"}}").Count >= store.LoadRuns().Count, "Results does not list every run, the outside run included.");
                await Click(driver, "NavLibrary", cancellationToken);
                agent.CloseInput();
                Check(await WorkerCommand.WaitForExitAsync(server, TimeSpan.FromSeconds(10)), "The MCP server did not exit after its stdin closed.");
                Check(server.ExitCode == 0, $"The MCP server exited with code {server.ExitCode}.");
                try { await serverLog.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); } catch (TimeoutException) { }
                Pass(report, "Studio's own saves are not outside changes; an outside run reaches Results and Activity",
                    $"Saving in Studio produced no outside-tool entry; run_test through MCP produced run {outsideRunId}, logged as finished from an outside tool and listed under Results; the server exited with code 0.");
            }

            stage = "Agent access settings";
            await Click(driver, "NavSettings", cancellationToken);
            await WaitForElement(driver, "id:McpCommand", cancellationToken);
            var agentAccess = await CompleteSnapshot(driver, cancellationToken);
            string[] agentControls = ["McpStatus", "McpCommand", "CopyMcpCommand", "CopyMcpJson", "McpListen", "McpUrl", "CopyMcpUrl", "CopyMcpHttpJson", "McpPort", "McpToken", "CopyMcpToken", "TestMcpConnection", "OpenMcpGuide"];
            Check(agentControls.All(id => agentAccess.Elements.Count(e => e.AutomationId == id) == 1), "The Agent access card is missing a control, or an ID is duplicated.");
            Check(agentAccess.Elements.Single(e => e.AutomationId == "McpListen").ClassName == "CheckBox", "Listen for agents is not a toggle.");
            var command = agentAccess.Elements.Single(e => e.AutomationId == "McpCommand").Value;
            Check(command.Contains(" mcp ", StringComparison.Ordinal) && command.Contains("--workspace", StringComparison.Ordinal) && command.Contains(workspace, StringComparison.OrdinalIgnoreCase),
                "McpCommand does not show the mcp command for this workspace: " + command);
            Check(agentAccess.Elements.Single(e => e.AutomationId == "McpUrl").Value == "", "McpUrl shows an address before the listener is on.");
            var shownToken = agentAccess.Elements.Single(e => e.AutomationId == "McpToken").Value;
            Check(shownToken.EndsWith(mcpToken[^4..], StringComparison.Ordinal) && !shownToken.Contains(mcpToken, StringComparison.Ordinal), "The card shows only the end of the token: " + shownToken);
            await Click(driver, "TestMcpConnection", cancellationToken);
            var answer = await WaitForNameContains(driver, "McpStatus", "answered", cancellationToken, 15000);
            Check(answer.Contains($"answered with {McpVerifier.ExpectedTools.Length} tools", StringComparison.Ordinal), "Test the connection must count the tools: " + answer);
            await Toggle(driver, "McpListen", true, cancellationToken);
            var url = await WaitForValueStart(driver, "McpUrl", "http://127.0.0.1", cancellationToken, 15000);
            var listening = (await CompleteSnapshot(driver, cancellationToken)).Elements.Single(e => e.AutomationId == "McpStatus").Name;
            var pidText = Regex.Match(listening, @"server process (\d+)");
            Check(pidText.Success, "McpStatus does not name the server process: " + listening);
            var serverPid = int.Parse(pidText.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Check(ProcessAlive(serverPid), $"The HTTP server process {serverPid} is not running.");
            Check(await Value(driver, "McpPort", cancellationToken) == new Uri(url).Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "The card must show the port the listener picked.");
            var endpointFile = Path.Combine(workspace, "mcp", "endpoint.json");
            Check(File.Exists(endpointFile) && JsonDocument.Parse(await File.ReadAllTextAsync(endpointFile, cancellationToken)).RootElement.GetProperty("url").GetString() == url, "endpoint.json must name the listener's address.");
            // The address answers as an MCP endpoint to this separate process, only with the token: initialize, tools/list and get_workspace_info over HTTP.
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
            {
                async Task<(int Status, JsonElement Body, string? Session)> PostAsync(JsonObject message, string? bearer, string? session)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json") };
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                    if (bearer is not null) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
                    if (session is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
                    using var response = await http.SendAsync(request, cancellationToken);
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    return ((int)response.StatusCode, JsonDocument.Parse(body).RootElement.Clone(), response.Headers.TryGetValues("Mcp-Session-Id", out var ids) ? ids.FirstOrDefault() : null);
                }
                var handshake = new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
                    ["params"] = new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "verify-studio", ["version"] = "1.0" } }
                };
                Check((await PostAsync(handshake, null, null)).Status == 401, "A request without the token must be 401.");
                Check((await PostAsync(handshake, "wrong-token", null)).Status == 401, "A request with a wrong token must be 401.");
                var (status, initialized, session) = await PostAsync(handshake, mcpToken, null);
                Check(status == 200 && session is not null && initialized.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString() == "testy", $"POST initialize with the token answered {status}: {initialized.GetRawText()}");
                var tools = await PostAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/list" }, mcpToken, session);
                Check(tools.Status == 200 && tools.Body.GetProperty("result").GetProperty("tools").GetArrayLength() == McpVerifier.ExpectedTools.Length, "tools/list over HTTP must list every tool.");
                var info = await PostAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 3, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = "get_workspace_info", ["arguments"] = new JsonObject() } }, mcpToken, session);
                var reportedWorkspace = info.Body.GetProperty("result").GetProperty("structuredContent").GetProperty("workspace").GetString()!;
                Check(info.Status == 200 && string.Equals(Path.TrimEndingDirectorySeparator(reportedWorkspace), Path.TrimEndingDirectorySeparator(workspace), StringComparison.OrdinalIgnoreCase), "get_workspace_info over HTTP must name Studio's workspace: " + reportedWorkspace);
            }
            await Evidence(driver, artifacts, "06f-agent-access", cancellationToken);
            await Toggle(driver, "McpListen", false, cancellationToken);
            await WaitForValue(driver, "McpUrl", "", cancellationToken);
            await WaitForAsync(() => !ProcessAlive(serverPid), cancellationToken, 30000);
            await WaitForAsync(() => !File.Exists(endpointFile), cancellationToken, 5000);
            Pass(report, "Agent access settings",
                $"McpCommand shows {command}; Test the connection reported \"{answer}\"; Listen for agents published {url} (server process {serverPid}), which refused requests without the token and answered initialize, tools/list and get_workspace_info with it; turning it off cleared the address, ended the process and removed endpoint.json.");

            if (liveSettings is not null)
            {
                stage = "Configure live AI execution through Studio";
                await Click(driver, "NavSettings", cancellationToken);
                await Select(driver, "ProviderPicker", liveSettings.Kind.ToString(), cancellationToken);
                await Type(driver, "ModelName", liveSettings.Model, cancellationToken);
                await Type(driver, "ProviderEndpoint", liveSettings.Endpoint, cancellationToken);
                await Type(driver, "ApiKeyVariable", liveSettings.ApiKeyEnvironmentVariable, cancellationToken);
                await Type(driver, "CodexExecutable", liveSettings.CodexExecutable, cancellationToken);
                await Toggle(driver, "LiveReview", false, cancellationToken);
                await Toggle(driver, "AiDirectedExecution", true, cancellationToken);
                await Toggle(driver, "NativeComputerUse", liveSettings.NativeComputerUse, cancellationToken);
                await Toggle(driver, "SupportsImages", liveSettings.SupportsImages, cancellationToken);
                await Type(driver, "MaximumAgentTurns", liveSettings.MaximumAgentTurns.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
                await Click(driver, "SaveConnection", cancellationToken);
                await WaitForAsync(() => store.LoadSettings().Kind == liveSettings.Kind && store.LoadSettings().AiDirectedExecution, cancellationToken);
                saved = Read(store.LoadSettings);
                Check(saved.NativeComputerUse == liveSettings.NativeComputerUse && saved.SupportsImages == liveSettings.SupportsImages && saved.MaximumAgentTurns == liveSettings.MaximumAgentTurns,
                    "Live execution settings did not persist exactly.");
                Pass(report, "Live AI execution is configured through the GUI", $"{saved.Kind}; AI-directed execution enabled; turn budget {saved.MaximumAgentTurns}.");
                await Evidence(driver, artifacts, "07-live-settings", cancellationToken);

                stage = "Run actual AI controller from Studio";
                await Click(driver, "NavLibrary", cancellationToken);
                await Select(driver, "TestLibrary", "Create a customer", cancellationToken);
                var previousRunIds = store.LoadRuns().Select(r => r.Id).ToHashSet();
                await Click(driver, "RunTest", cancellationToken);
                await WaitForAsync(() => store.LoadRuns().Any(r => !previousRunIds.Contains(r.Id) && r.TestName == "Create a customer" && r.FinishedAt is not null), cancellationToken, 240000);
                var aiRun = store.LoadRuns().First(r => !previousRunIds.Contains(r.Id) && r.TestName == "Create a customer");
                Check(aiRun.Status == RunStatus.Passed, aiRun.Summary);
                var agentPath = Path.Combine(aiRun.ArtifactDirectory, "computer-agent.json");
                Check(File.Exists(agentPath), "Run test did not produce a live computer-agent trace; it may have replayed steps instead.");
                var agent = JsonSerializer.Deserialize<ComputerAgentResult>(await File.ReadAllTextAsync(agentPath, cancellationToken), TestyJson.Options)
                    ?? throw new InvalidDataException("The live agent trace is empty.");
                Check(agent.Completed && agent.ModelTurns > 1, "The live controller did not record successive model decisions and completion.");
                var requested = JsonSerializer.Deserialize<TestCase>(await File.ReadAllTextAsync(Path.Combine(aiRun.ArtifactDirectory, "requested-test.json"), cancellationToken), TestyJson.Options)!;
                var coverage = SavedWorkflowVerifier.Verify(requested, agent.Observations);
                Check(coverage.Complete, coverage.Message);
                var assertions = AiTestRunner.VerifyAssertions(requested, agent.Observations.SelectMany(o => o.Execution?.Steps ?? []));
                Check(assertions.Complete, assertions.Message);
                Check(agent.Observations.All(o => o.Snapshot is not null && File.Exists(o.ScreenshotPath)), "Live GUI execution is missing fresh screenshot/tree evidence.");
                await WaitForEnabled(driver, "RunTest", cancellationToken);
                Pass(report, "Run test invokes the actual live AI controller", $"{agent.ModelTurns} model turns; {coverage.Message} {assertions.Message}", aiRun.ArtifactDirectory);
                await Evidence(driver, artifacts, "08-live-run", cancellationToken);
            }

            stage = "Closing Studio ends the listener";
            await Click(driver, "NavSettings", cancellationToken);
            await WaitForElement(driver, "id:McpListen", cancellationToken);
            await Toggle(driver, "McpListen", true, cancellationToken);
            await WaitForValueStart(driver, "McpUrl", "http://127.0.0.1", cancellationToken, 15000);
            var closingStatus = (await CompleteSnapshot(driver, cancellationToken)).Elements.Single(e => e.AutomationId == "McpStatus").Name;
            var closingPid = int.Parse(Regex.Match(closingStatus, @"server process (\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Check(ProcessAlive(closingPid), "The listener did not start again.");
            studio.CloseMainWindow();
            using (var exit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                exit.CancelAfter(15000);
                await studio.WaitForExitAsync(exit.Token);
            }
            await WaitForAsync(() => !ProcessAlive(closingPid), cancellationToken, 10000);
            Pass(report, "Closing Studio ends the listener", $"With the listener on (server process {closingPid}), closing Studio ended Studio and the server.");
            cancellationToken.ThrowIfCancellationRequested();
            report.CompletedAllScenarios = report.Checks.Count == report.PlannedChecks;
        }
        catch (Exception exception)
        {
            report.Checks.Add(new VerificationCheck { Name = stage, Driver = "Studio UI Automation", Passed = false, Expected = RunStatus.Passed, Actual = RunStatus.Failed, Message = exception.ToString() });
            if (driver.Target is not null)
            {
                try { await Evidence(driver, artifacts, "failure", CancellationToken.None); } catch { }
            }
        }
        finally
        {
            await CloseOwned(studio);
            if (lab is not null) { await CloseOwned(lab); lab.Dispose(); }
            report.Finish(cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(artifacts, "studio-report.json"), JsonSerializer.Serialize(report, TestyJson.Options), CancellationToken.None);
        }
        return report;
    }

    private static void Pass(VerificationReport report, string name, string message, string runDirectory = "") => report.Checks.Add(new VerificationCheck
    { Name = name, Driver = "Studio UI Automation", Passed = true, Expected = RunStatus.Passed, Actual = RunStatus.Passed, Message = message, RunDirectory = runDirectory });
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task WaitForAsync(Func<bool> predicate, CancellationToken ct, int timeoutMs = 15000)
    {
        var timer = Stopwatch.StartNew();
        while (!Holds(predicate))
        {
            if (timer.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"Expected state did not appear within {timeoutMs} ms.");
            await Task.Delay(100, ct);
        }
    }
    /// <summary>Studio replaces workspace files atomically; a read that lands on the replacement fails transiently and is retried.</summary>
    private static bool Holds(Func<bool> predicate)
    {
        try { return predicate(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    private static T Read<T>(Func<T> read)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return read(); }
            catch (Exception ex) when (attempt < 20 && ex is IOException or UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }
    private static async Task WaitForElement(ITargetDriver driver, string selector, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var snapshot = await driver.SnapshotAsync(ct);
            if (snapshot.IsTruncated)
            {
                // A transient settings/layout transition is not yet enough evidence to select a control.
                if (timer.ElapsedMilliseconds > 10000) throw new TimeoutException($"Complete UI evidence for {selector} did not appear.");
                await Task.Delay(100, ct);
                continue;
            }
            if (UiSelectors.Find(snapshot, selector).Any(e => !e.IsOffscreen)) return;
            if (selector.StartsWith("id:", StringComparison.Ordinal) && driver.Target is { WindowHandle: not 0 } target)
            {
                // Focus/scroll only the selected Studio control to reveal settings inside its scroll viewer.
                var root = AutomationElement.FromHandle(new IntPtr(target.WindowHandle));
                var element = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, selector[3..]));
                if (element is not null && element.Current.ProcessId == target.ProcessId)
                {
                    if (element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scroll)) ((ScrollItemPattern)scroll).ScrollIntoView();
                    else if (element.Current.IsKeyboardFocusable) element.SetFocus();
                }
            }
            if (timer.ElapsedMilliseconds > 10000) throw new TimeoutException($"Visible element {selector} did not appear.");
            await Task.Delay(100, ct);
        }
    }
    private static async Task Execute(ITargetDriver driver, StepAction action, string id, string value, CancellationToken ct)
    {
        await WaitForElement(driver, "id:" + id, ct);
        await driver.ExecuteAsync(new TestStep { Title = action + " " + id, Action = action, Selector = "id:" + id, Value = value, TimeoutMs = 10000 }, ct);
        await Task.Delay(100, ct);
    }
    private static Task Click(ITargetDriver driver, string id, CancellationToken ct) => Execute(driver, StepAction.Click, id, "", ct);
    private static Task Type(ITargetDriver driver, string id, string value, CancellationToken ct) => Execute(driver, StepAction.TypeText, id, value, ct);
    private static Task Select(ITargetDriver driver, string id, string value, CancellationToken ct) => Execute(driver, StepAction.Select, id, value, ct);
    private static Task Toggle(ITargetDriver driver, string id, bool value, CancellationToken ct) => Execute(driver, StepAction.Toggle, id, value ? "true" : "false", ct);
    private static async Task<string> Value(ITargetDriver driver, string id, CancellationToken ct) =>
        (await driver.SnapshotAsync(ct)).Elements.Single(e => e.AutomationId == id).Value;
    private static async Task WaitForValue(ITargetDriver driver, string id, string expected, CancellationToken ct)
    {
        var timeout = Stopwatch.StartNew();
        while (await Value(driver, id, ct) != expected)
        {
            if (timeout.ElapsedMilliseconds > 10000) throw new TimeoutException($"{id} did not contain its expected value.");
            await Task.Delay(100, ct);
        }
    }
    private static async Task WaitForName(ITargetDriver driver, string id, string expected, CancellationToken ct)
    {
        var timeout = Stopwatch.StartNew();
        while (!(await driver.SnapshotAsync(ct)).Elements.Any(e => e.AutomationId == id && e.Name == expected))
        {
            if (timeout.ElapsedMilliseconds > 10000) throw new TimeoutException($"{id} did not show \"{expected}\".");
            await Task.Delay(100, ct);
        }
    }
    private static async Task<string> WaitForNameStart(ITargetDriver driver, string id, string prefix, CancellationToken ct)
    {
        var timeout = Stopwatch.StartNew();
        while (true)
        {
            var name = (await driver.SnapshotAsync(ct)).Elements.FirstOrDefault(e => e.AutomationId == id)?.Name ?? "";
            if (name.StartsWith(prefix, StringComparison.Ordinal)) return name;
            if (timeout.ElapsedMilliseconds > 10000) throw new TimeoutException($"{id} did not start with \"{prefix}\"; it showed \"{name}\".");
            await Task.Delay(100, ct);
        }
    }
    private static async Task<UiSnapshot> CompleteSnapshot(ITargetDriver driver, CancellationToken ct)
    {
        var timeout = Stopwatch.StartNew();
        while (true)
        {
            var snapshot = await driver.SnapshotAsync(ct);
            if (!snapshot.IsTruncated) return snapshot;
            if (timeout.ElapsedMilliseconds > 10000) throw new TimeoutException("A complete Studio UI tree was not available.");
            await Task.Delay(100, ct);
        }
    }
    private static async Task<UiSnapshot> WaitForSnapshot(ITargetDriver driver, Func<UiSnapshot, bool> condition, string failure, CancellationToken ct, int timeoutMs = 10000)
    {
        var timeout = Stopwatch.StartNew();
        while (true)
        {
            var snapshot = await driver.SnapshotAsync(ct);
            if (!snapshot.IsTruncated && condition(snapshot)) return snapshot;
            if (timeout.ElapsedMilliseconds > timeoutMs) throw new TimeoutException(failure);
            await Task.Delay(100, ct);
        }
    }
    /// <summary>A ListItem named exactly <paramref name="name"/> inside TestLibrary (the library's item names are the test names).</summary>
    private static bool LibraryLists(UiSnapshot snapshot, string name) =>
        UiSelectors.Find(snapshot, "query:{\"label\":" + JsonSerializer.Serialize(name) + ",\"type\":\"ListItem\",\"ancestor\":{\"id\":\"TestLibrary\"}}").Count > 0;
    private static bool ProcessAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
    private static async Task DrainToFileAsync(StreamReader reader, string path)
    {
        await using var file = new StreamWriter(path, false, new UTF8Encoding(false));
        while (await reader.ReadLineAsync() is { } line) await file.WriteLineAsync(line);
    }
    private static async Task<string> WaitForNameContains(ITargetDriver driver, string id, string fragment, CancellationToken ct, int timeoutMs)
    {
        var timeout = Stopwatch.StartNew();
        while (true)
        {
            var name = (await driver.SnapshotAsync(ct)).Elements.FirstOrDefault(e => e.AutomationId == id)?.Name ?? "";
            if (name.Contains(fragment, StringComparison.Ordinal)) return name;
            if (timeout.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"{id} did not contain \"{fragment}\" within {timeoutMs} ms; it showed \"{name}\".");
            await Task.Delay(150, ct);
        }
    }
    private static async Task<string> WaitForValueStart(ITargetDriver driver, string id, string prefix, CancellationToken ct, int timeoutMs)
    {
        var timeout = Stopwatch.StartNew();
        while (true)
        {
            var value = (await driver.SnapshotAsync(ct)).Elements.FirstOrDefault(e => e.AutomationId == id)?.Value ?? "";
            if (value.StartsWith(prefix, StringComparison.Ordinal)) return value;
            if (timeout.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"{id} did not start with \"{prefix}\" within {timeoutMs} ms; it showed \"{value}\".");
            await Task.Delay(150, ct);
        }
    }
    private static bool IsSelected(UiElementInfo element) =>
        element.Properties.TryGetValue("uia.isSelected", out var selected) && selected.Status == UiPropertyStatus.Known && selected.Value is { ValueKind: JsonValueKind.True };
    /// <summary>The raw Selector column header of the steps editor, shown only with technical details on.</summary>
    private static bool SelectorColumnVisible(UiSnapshot snapshot) =>
        UiSelectors.Find(snapshot, "query:{\"label\":\"Selector\",\"ancestor\":{\"id\":\"StepsEditor\"}}").Any(e => !e.IsOffscreen);
    private static async Task WaitForEnabled(ITargetDriver driver, string id, CancellationToken ct)
    {
        var timeout = Stopwatch.StartNew();
        while (!(await driver.SnapshotAsync(ct)).Elements.Any(e => e.AutomationId == id && e.IsEnabled))
        {
            if (timeout.ElapsedMilliseconds > 20000) throw new TimeoutException($"{id} did not become enabled after the operation completed.");
            await Task.Delay(150, ct);
        }
        await Task.Delay(200, ct); // Allow the final WPF layout/render pass before evidence capture.
    }
    private static async Task Evidence(ITargetDriver driver, string directory, string name, CancellationToken ct)
    {
        await driver.CaptureAsync(Path.Combine(directory, name + ".png"), ct);
        await File.WriteAllTextAsync(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(await driver.SnapshotAsync(ct), TestyJson.Options), ct);
    }
    private static async Task CloseOwned(Process process)
    {
        if (process.HasExited) return;
        process.CloseMainWindow();
        using var timeout = new CancellationTokenSource(4000);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: false); }
    }
}
