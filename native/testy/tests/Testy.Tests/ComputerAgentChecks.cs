using System.Net;
using System.Text;
using System.Text.Json;
using Testy.Core;

namespace Testy.Tests;

internal static class ComputerAgentChecks
{
    private const string Assertion = """{"title":"Verify Ready","action":"assertText","selector":"id:Status","value":"Ready","timeoutMs":200,"x":0,"y":0}""";
    public static IEnumerable<(string Name, Func<Task> Execute)> All()
    {
        yield return ("native computer loop returns matching screenshot call IDs and verifies assertions", NativeProtocol);
        yield return ("native safety checks, batches and mutation assertion tools execute nothing", NativeBoundaries);
        yield return ("Codex live controller uses updated observations before each decision", CodexDecisions);
        yield return ("text-only Codex decisions omit images while retaining local screenshot evidence", CodexTextOnly);
        yield return ("Codex decision validation prevents arbitrary tools", CodexBoundary);
        yield return ("text-only compatible models receive trees without images", TextOnly);
        yield return ("native failure review receives the failed screenshot without more actions", NativeFailureFeedback);
        yield return ("truncated compatible tool decisions execute no action", TruncatedToolDecision);
        yield return ("selector snapshot wait respects the step deadline", SnapshotDeadline);
        yield return ("agent locks are released after artifact directory failures", ArtifactFailureRelease);
        yield return ("saved wait duration cannot be shortened through a different timeout", WaitCoverage);
        yield return ("native saved clicks reject unrelated overlapping UI controls", OverlapCoverage);
        yield return ("native saved selection requires an option in the intended subtree", SelectionCoverage);
        yield return ("AI runner rejects empty action records and empty image evidence", MissingActionEvidence);
        yield return ("native coverage requires fresh acknowledged input on the intended runtime control", NativeReceiptCoverage);
        yield return ("execution service forwards replay reviews without bypassing AI-directed routing", ReplayObserverRouting);
    }
    private static async Task NativeProtocol()
    {
        await WithNative(async (root, settings, driver) =>
        {
            var turn = 0;
            var executor = new Executor { ReceiptToReturn = new NativeActionReceipt { ProcessId = 42, InputDelivered = true, HitRuntimeIds = ["runtime-status"] } };
            using var handler = new Handler(async request =>
            {
                turn++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Check(body.RootElement.GetProperty("tools")[0].GetProperty("type").GetString() == "computer", "Native computer tool not enabled.");
                Check(body.RootElement.GetProperty("include")[0].GetString() == "reasoning.encrypted_content", "Reasoning continuation not requested.");
                if (turn == 1) return NativeCall(new[] { new { type = "click", button = "left", x = 0, y = 0 } });
                if (turn == 2)
                {
                    var reply = body.RootElement.GetProperty("input").EnumerateArray().Single(i => i.TryGetProperty("type", out var type) && type.GetString() == "computer_call_output");
                    Check(reply.GetProperty("call_id").GetString() == "native_1", "Native call ID lost.");
                    Check(reply.GetProperty("output").GetProperty("type").GetString() == "computer_screenshot", "Native screenshot output missing.");
                    Check(body.RootElement.GetProperty("input").GetRawText().Contains("runtime-status", StringComparison.Ordinal), "Actual input receipt was not sent back to the model.");
                    return Json(new { status = "completed", output = new[] { new { type = "function_call", call_id = "assert_1", name = "verify_ui_assertion", arguments = Assertion } } });
                }
                Check(body.RootElement.GetProperty("input").GetRawText().Contains("assert_1", StringComparison.Ordinal), "Assertion output not returned.");
                return Final("Ready was verified.");
            });
            using var client = new HttpClient(handler);
            var result = await new NativeComputerUseAgent(settings, driver, root, executor, client).RunAsync("Click then assert Ready.", 5);
            Check(result.Completed && result.Status == RunStatus.Pending && result.HasVerifiedAssertions, "Native run failed: " + result.Message);
            Check(executor.Calls == 1 && result.ModelTurns == 3, "Native actions were not singular.");
            Check(result.Observations[1].NativeAction?.GetProperty("type").GetString() == "click", "Native action was not recorded.");
            Check(result.Observations[1].NativeReceipt?.InputDelivered == true && !ReferenceEquals(result.Observations[1].NativeReceipt, executor.LastReceipt), "Executor receipt was not copied into immutable action evidence.");
        });
    }
    private static async Task NativeBoundaries()
    {
        foreach (var scenario in new[] { "safety", "batch", "mutation", "multiple-calls" })
            await WithNative(async (root, settings, driver) =>
            {
                var executor = new Executor();
                using var handler = new Handler(_ => Task.FromResult(scenario switch
                {
                    "safety" => Json(new { status = "completed", output = new[] { new { type = "computer_call", call_id = "native_1", pending_safety_checks = new[] { new { id = "safety_1", message = "Needs confirmation" } }, actions = new[] { new { type = "click", x = 0, y = 0, button = "left" } } } } }),
                    "batch" => NativeCall(new[] { new { type = "click", x = 0, y = 0, button = "left" }, new { type = "click", x = 0, y = 0, button = "left" } }),
                    "mutation" => Json(new { status = "completed", output = new[] { new { type = "function_call", call_id = "assert_1", name = "verify_ui_assertion", arguments = Assertion.Replace("assertText", "click", StringComparison.Ordinal) } } }),
                    _ => Json(new { status = "completed", output = new[] { new { type = "computer_call", call_id = "a", actions = new[] { new { type = "screenshot" } } }, new { type = "computer_call", call_id = "b", actions = new[] { new { type = "screenshot" } } } } })
                }));
                using var client = new HttpClient(handler);
                var result = await new NativeComputerUseAgent(settings, driver, root, executor, client).RunAsync("Inspect.", 3);
                Check(result.Status == RunStatus.Failed && !result.Completed && executor.Calls == 0 && driver.Mutations == 0, $"Native {scenario} boundary executed an action.");
            });
    }
    private static async Task NativeFailureFeedback()
    {
        await WithNative(async (root, settings, driver) =>
        {
            var turn = 0;
            var executor = new Executor { Fail = true };
            using var handler = new Handler(async request =>
            {
                turn++;
                if (turn == 1) return NativeCall(new[] { new { type = "click", button = "left", x = 0, y = 0 } });
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Check(!body.RootElement.TryGetProperty("tools", out _), "Failure reviewer was given mutation tools.");
                var raw = body.RootElement.GetProperty("input").GetRawText();
                Check(raw.Contains("data:image/png;base64,", StringComparison.Ordinal) && raw.Contains("Simulated native failure", StringComparison.Ordinal), "Failure image or actual diagnostic missing.");
                return Final("Observed action failed; no retry was performed.");
            });
            using var client = new HttpClient(handler);
            var result = await new NativeComputerUseAgent(settings, driver, root, executor, client).RunAsync("Click.", 4);
            Check(result.Status == RunStatus.Failed && !result.Completed && turn == 2 && executor.Calls == 1, "Failure review changed verdict or repeated the action.");
        });
    }
    private static async Task CodexDecisions()
    {
        var root = Temp("codex-decisions");
        try
        {
            using var driver = new Driver();
            var count = 0;
            var agent = new CodexComputerAgent(new ProviderSettings(), driver, root, (prompt, schema, screenshot, ct) =>
            {
                count++;
                Check(File.Exists(screenshot), "Decision missing current screenshot.");
                if (count == 1) return Task.FromResult(Decision(false, Assertion));
                Check(prompt.Contains("Assertion satisfied", StringComparison.Ordinal), "Next Codex decision did not get previous assertion result.");
                return Task.FromResult(Decision(true, """{"title":"Finished","action":"screenshot","selector":"","value":"","timeoutMs":1000,"x":0,"y":0}"""));
            });
            var result = await agent.RunAsync("Verify Ready.", 4);
            Check(result.Completed && result.Status == RunStatus.Pending && result.HasVerifiedAssertions && count == 2, "Codex live loop failed: " + result.Message);
            Check(File.Exists(Path.Combine(root, "decision-001.json")) && File.Exists(Path.Combine(root, "decision-002.json")), "Per-turn decisions not persisted.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task CodexTextOnly()
    {
        var root = Temp("codex-text-only");
        try
        {
            using var driver = new Driver();
            var count = 0;
            var agent = new CodexComputerAgent(new ProviderSettings { SupportsImages = false }, driver, root, (prompt, schema, screenshot, ct) =>
            {
                count++;
                Check(screenshot == "", "A screenshot argument reached the text-only Codex decision provider.");
                Check(prompt.Contains("id:Status", StringComparison.Ordinal), "Text-only Codex lost the current UI tree.");
                if (count == 1) return Task.FromResult(Decision(false, Assertion));
                Check(prompt.Contains("Assertion satisfied", StringComparison.Ordinal), "Text-only Codex lost prior action feedback.");
                return Task.FromResult(Decision(true, Assertion));
            });
            var result = await agent.RunAsync("Verify Ready.", 4);
            Check(result.Completed && result.Status == RunStatus.Pending && result.HasVerifiedAssertions && count == 2, "Text-only Codex loop did not complete with assertion evidence: " + result.Message);
            Check(result.Observations.Count == 2 && result.Observations.All(o => File.Exists(o.ScreenshotPath) && new FileInfo(o.ScreenshotPath).Length > 0), "Disabling image sharing removed local screenshot evidence.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task CodexBoundary()
    {
        var root = Temp("codex-boundary");
        try
        {
            using var driver = new Driver();
            var agent = new CodexComputerAgent(new ProviderSettings(), driver, root, (prompt, schema, screenshot, ct) => Task.FromResult(Decision(false, Assertion).Replace("perform_ui_action", "execute_shell", StringComparison.Ordinal)));
            var result = await agent.RunAsync("Inspect.", 3);
            Check(result.Status == RunStatus.Failed && driver.Mutations == 0, "Invalid Codex tool executed.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task TextOnly()
    {
        var root = Temp("text-only");
        try
        {
            using var driver = new Driver();
            using var handler = new Handler(async request =>
            {
                var body = await request.Content!.ReadAsStringAsync();
                Check(!body.Contains("image_url", StringComparison.Ordinal) && !body.Contains("base64", StringComparison.Ordinal), "Image sent to text-only provider.");
                Check(body.Contains("id:Status", StringComparison.Ordinal), "UI tree missing for text-only provider.");
                return Json(new { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = "Cannot claim a pass without assertions." } } } });
            });
            using var client = new HttpClient(handler);
            var settings = new ProviderSettings { Kind = ProviderKind.Compatible, Model = "text-only", Endpoint = "http://localhost/v1/chat/completions", SupportsImages = false, ApiKeyEnvironmentVariable = "" };
            var result = await new ComputerUseAgent(settings, driver, root, client).RunAsync("Inspect.", 3);
            Check(result.Completed && !result.HasVerifiedAssertions && result.Status == RunStatus.Pending, "Text-only agent falsely passed.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task TruncatedToolDecision()
    {
        var root = Temp("truncated-decision");
        try
        {
            using var driver = new Driver();
            using var handler = new Handler(_ => Task.FromResult(Json(new { choices = new[] { new { finish_reason = "length", message = new { role = "assistant", content = (string?)null, tool_calls = new[] { new { id = "call_1", type = "function", function = new { name = "perform_ui_action", arguments = Assertion.Replace("assertText", "click", StringComparison.Ordinal) } } } } } } })));
            using var client = new HttpClient(handler);
            var settings = new ProviderSettings { Kind = ProviderKind.Compatible, Model = "mock", Endpoint = "http://localhost/v1/chat/completions", ApiKeyEnvironmentVariable = "" };
            var result = await new ComputerUseAgent(settings, driver, root, client).RunAsync("Inspect.", 3);
            Check(result.Status == RunStatus.Failed && result.Message.Contains("length", StringComparison.Ordinal) && driver.Mutations == 0, "Truncated tool response executed an action.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task SnapshotDeadline()
    {
        var root = Temp("snapshot-deadline");
        try
        {
            using var driver = new Driver { SlowFirstSnapshot = true };
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var run = await new TestRunner(driver, root).RunAsync(new TestCase { Name = "Bounded snapshot", Steps = [new TestStep { Title = "Click status", Action = StepAction.Click, Selector = "id:Status", TimeoutMs = 100 }] });
            Check(run.Status == RunStatus.Failed && driver.Mutations == 0 && timer.ElapsedMilliseconds < 450, "Slow provider escaped the step deadline.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task ArtifactFailureRelease()
    {
        var parent = Temp("artifact-error");
        try
        {
            foreach (var kind in new[] { ProviderKind.Codex, ProviderKind.Compatible, ProviderKind.OpenAI })
            {
                var root = Path.Combine(parent, kind.ToString());
                await File.WriteAllTextAsync(root, "This file blocks directory creation.");
                using var driver = new Driver();
                using var handler = new Handler(_ => Task.FromResult(kind == ProviderKind.Compatible
                    ? Json(new { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = "Finished." } } } })
                    : Final("Finished.")));
                using var client = new HttpClient(handler);
                var settings = new ProviderSettings { Kind = kind, Model = "mock", Endpoint = "http://localhost/v1/responses", ApiKeyEnvironmentVariable = "" };
                var variable = "TESTY_GATE_TEST_" + Guid.NewGuid().ToString("N");
                settings.ApiKeyEnvironmentVariable = variable;
                Environment.SetEnvironmentVariable(variable, "mock-key");
                try
                {
                    IComputerAgent agent = kind switch
                    {
                        ProviderKind.Codex => new CodexComputerAgent(settings, driver, root, (p, s, i, c) => Task.FromResult(Decision(true, Assertion))),
                        ProviderKind.Compatible => new ComputerUseAgent(settings, driver, root, client),
                        _ => new NativeComputerUseAgent(settings, driver, root, new Executor(), client)
                    };
                    try { await agent.RunAsync("Inspect.", 3); } catch (IOException) { }
                    File.Delete(root);
                    Directory.CreateDirectory(root);
                    var recovered = await agent.RunAsync("Inspect.", 3);
                    Check(recovered.Completed, $"{kind} agent lock leaked after artifact error: {recovered.Message}");
                }
                finally { Environment.SetEnvironmentVariable(variable, null); }
            }
        }
        finally { Directory.Delete(parent, true); }
    }
    private static Task WaitCoverage()
    {
        var expected = new TestStep { Title = "Wait five seconds", Action = StepAction.Wait, TimeoutMs = 5000 };
        var test = new TestCase { Name = "Wait contract", Steps = [expected] };
        ComputerToolObservation Observed(TestStep step) => new()
        {
            ToolName = "perform_ui_action",
            Execution = new RunResult { Status = RunStatus.Passed, Steps = [new StepResult { Step = step, Status = RunStatus.Passed }] }
        };
        var shortened = TestyJson.Clone(expected); shortened.TimeoutMs = 100;
        Check(!SavedWorkflowVerifier.Verify(test, [Observed(shortened)]).Complete, "Five-second default wait matched a 100ms wait.");
        var explicitDuration = TestyJson.Clone(shortened); explicitDuration.Value = "5000";
        Check(SavedWorkflowVerifier.Verify(test, [Observed(explicitDuration)]).Complete, "Equivalent explicit wait duration was rejected.");
        explicitDuration.Value = "6000";
        Check(SavedWorkflowVerifier.Verify(test, [Observed(explicitDuration)]).Complete, "Longer bounded wait was rejected.");
        explicitDuration.Value = "4000";
        Check(!SavedWorkflowVerifier.Verify(test, [Observed(explicitDuration)]).Complete, "Shorter explicit wait was accepted.");
        return Task.CompletedTask;
    }
    private static async Task ReplayObserverRouting()
    {
        var root = Temp("replay-observer-routing");
        try
        {
            using var driver = new Driver();
            var click = new TestStep { Title = "Click status", Action = StepAction.Click, Selector = "id:Status" };
            var assertion = new TestStep { Title = "Verify Ready", Action = StepAction.AssertText, Selector = "id:Status", Value = "Ready" };
            var calls = 0;
            Task Observer(RunResult run, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                calls++;
                var latest = run.Steps.Last();
                Check(latest.Snapshot is not null && File.Exists(latest.ScreenshotPath), "Replay observer did not receive completed evidence.");
                run.AiAnalysis = $"Reviewed {calls} completed replay steps.";
                return Task.CompletedTask;
            }
            var ai = new TestExecutionService(driver, new ProviderSettings { Kind = ProviderKind.Codex }, Path.Combine(root, "ai")) { StepObserver = Observer };
            var rejected = false;
            try { await ai.RunAsync(new TestCase { Name = "AI needs immutable acceptance", Steps = [click] }); }
            catch (InvalidDataException ex) when (ex.Message.Contains("at least one saved assertion", StringComparison.Ordinal)) { rejected = true; }
            Check(rejected && calls == 0 && driver.Mutations == 0, "Adding an observer bypassed AI execution validation or started deterministic replay.");
            var replay = new TestExecutionService(driver, new ProviderSettings { Kind = ProviderKind.Offline, AiDirectedExecution = false }, Path.Combine(root, "replay")) { StepObserver = Observer };
            var result = await replay.RunAsync(new TestCase { Name = "Explicit replay with review", Steps = [click, assertion] });
            Check(result.Status == RunStatus.Passed && calls == 2 && driver.Mutations == 1, "Replay observer was not invoked once per completed step.");
            Check(result.AiAnalysis == "Reviewed 2 completed replay steps.", "Replay review was not preserved in the final run.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static Task OverlapCoverage()
    {
        var before = MappingSnapshot();
        var click = new TestStep { Title = "Click intended button", Action = StepAction.Click, Selector = "id:Intended" };
        var test = new TestCase { Name = "Overlap", Steps = [click] };
        var button = before.Elements[1];
        before.Elements.Add(new UiElementInfo { Selector = "id:Overlay", Name = "Unrelated modal", ControlType = "Window", Depth = 0, IsEnabled = true, Bounds = TestyJson.Clone(button.Bounds) });
        Check(!SavedWorkflowVerifier.Verify(test, [Initial(before), NativeObservation(new { type = "click", button = "left", x = 30, y = 50 }, before)]).Complete,
            "A modal covering the required button was treated as a verified click on that button.");
        before.Elements.RemoveAt(before.Elements.Count - 1);
        before.Elements.Add(new UiElementInfo { RuntimeId = "runtime-text", Selector = "id:ButtonText", Name = "Button content", ControlType = "Text", Depth = 2, IsEnabled = true, Bounds = TestyJson.Clone(button.Bounds) });
        Check(SavedWorkflowVerifier.Verify(test, [Initial(before), NativeObservation(new { type = "click", button = "left", x = 30, y = 50 }, before)]).Complete,
            "Expected button's own text descendant made its click ambiguous.");
        return Task.CompletedTask;
    }
    private static Task SelectionCoverage()
    {
        var before = MappingSnapshot();
        var combo = before.Elements[1]; combo.ControlType = "ComboBox"; combo.Value = "Old";
        var option = new UiElementInfo { RuntimeId = "runtime-option", Selector = "id:Option", Name = "New", ControlType = "ListItem", Depth = 2, IsEnabled = true, Bounds = new ElementBounds { X = 120, Y = 275, Width = 100, Height = 25 } };
        before.Elements.Add(option);
        var after = TestyJson.Clone(before); after.Elements[1].Value = "New";
        var test = new TestCase { Name = "Select ownership", Steps = [new TestStep { Title = "Select New", Action = StepAction.Select, Selector = "id:Intended", Value = "New" }] };
        var action = new { type = "click", button = "left", x = 30, y = 85 };
        Check(SavedWorkflowVerifier.Verify(test, [Initial(before), NativeObservation(action, after)]).Complete, "Observed descendant option was not recognized.");
        before.Elements.Insert(2, new UiElementInfo { Selector = "id:OtherList", Name = "Another control", ControlType = "List", Depth = 1, IsEnabled = true });
        Check(!SavedWorkflowVerifier.Verify(test, [Initial(before), NativeObservation(action, after)]).Complete, "Option from an unrelated list was accepted because its label matched.");
        return Task.CompletedTask;
    }
    private static async Task MissingActionEvidence()
    {
        var root = Temp("missing-action-evidence");
        try
        {
            using var driver = new Driver();
            var snapshot = await driver.SnapshotAsync();
            var assertion = new TestStep { Title = "Ready", Action = StepAction.AssertText, Selector = "id:Status", Value = "Ready" };
            var test = new TestCase { Name = "Evidence contract", Steps = [assertion] };
            var png = await driver.CaptureAsync(Path.Combine(root, "proof.png"));
            var valid = new ComputerToolObservation { ToolName = "perform_ui_action", Snapshot = snapshot, ScreenshotPath = png,
                Execution = new RunResult { Status = RunStatus.Passed, Steps = [new StepResult { Step = assertion, Status = RunStatus.Passed, Snapshot = snapshot, ScreenshotPath = png }] } };
            var empty = new ComputerToolObservation { ToolName = "computer", NativeAction = JsonSerializer.SerializeToElement(new { type = "click", x = 0, y = 0 }), Snapshot = snapshot, Execution = new RunResult { Status = RunStatus.Passed } };
            var runner = new AiTestRunner(driver, new ProviderSettings(), Path.Combine(root, "empty-record"), agentFactory: _ => new ResultAgent([empty, valid]));
            var run = await runner.RunAsync(test);
            Check(run.Status == RunStatus.Failed && run.Steps.Any(s => s.Step.Title == "Missing action evidence"), "Empty native action record bypassed evidence verification.");
            await File.WriteAllBytesAsync(png, []);
            var emptyImageRunner = new AiTestRunner(driver, new ProviderSettings(), Path.Combine(root, "empty-image"), agentFactory: _ => new ResultAgent([valid]));
            var emptyImageRun = await emptyImageRunner.RunAsync(test);
            Check(emptyImageRun.Status == RunStatus.Failed && emptyImageRun.Steps.Any(s => s.Message.Contains("no complete screenshot", StringComparison.Ordinal)), "Zero-byte image granted a passing action.");
        }
        finally { Directory.Delete(root, true); }
    }
    private static Task NativeReceiptCoverage()
    {
        var before = MappingSnapshot();
        var after = TestyJson.Clone(before); after.CapturedAt = before.CapturedAt.AddMilliseconds(20);
        var test = new TestCase { Name = "Native receipt", Steps = [new TestStep { Title = "Click intended", Action = StepAction.Click, Selector = "id:Intended" }] };
        var click = new { type = "click", button = "left", x = 30, y = 50 };
        var valid = NativeObservation(click, after);
        Check(SavedWorkflowVerifier.Verify(test, [Initial(before), valid]).Complete, "Valid acknowledged input receipt was rejected.");
        var missing = NativeObservation(click, after); missing.NativeReceipt = null;
        var missingResult = SavedWorkflowVerifier.Verify(test, [Initial(before), missing]);
        Check(!missingResult.Complete && missingResult.Message.Contains("synchronized input", StringComparison.Ordinal), "Missing native delivery evidence did not produce an actionable failure.");
        var unacknowledged = NativeObservation(click, after); unacknowledged.NativeReceipt!.InputDelivered = false;
        Check(!SavedWorkflowVerifier.Verify(test, [Initial(before), unacknowledged]).Complete, "Unacknowledged input was accepted.");
        var stale = NativeObservation(click, after); stale.NativeReceipt!.CapturedAt = before.CapturedAt.AddMilliseconds(-1);
        Check(!SavedWorkflowVerifier.Verify(test, [Initial(before), stale]).Complete, "Stale input receipt was accepted.");
        var wrongProcess = NativeObservation(click, after); wrongProcess.NativeReceipt!.ProcessId = 999;
        Check(!SavedWorkflowVerifier.Verify(test, [Initial(before), wrongProcess]).Complete, "Another process's input receipt was accepted.");
        var intercepted = NativeObservation(click, after); intercepted.NativeReceipt!.HitRuntimeIds = ["runtime-new-overlay"];
        Check(!SavedWorkflowVerifier.Verify(test, [Initial(before), intercepted]).Complete, "A new overlay after the initial snapshot counted as input on the old intended button.");
        var childButton = NativeObservation(click, after); childButton.NativeReceipt!.HitRuntimeIds = ["runtime-nested-delete"];
        Check(!SavedWorkflowVerifier.Verify(test, [Initial(before), childButton]).Complete, "Input on an independently actionable child was credited to its parent.");
        var labelBefore = MappingSnapshot();
        labelBefore.Elements.Add(new UiElementInfo { RuntimeId = "runtime-label", Selector = "id:Label", ControlType = "Text", Depth = 2, IsEnabled = true, Bounds = TestyJson.Clone(labelBefore.Elements[1].Bounds) });
        var labelAfter = TestyJson.Clone(labelBefore); labelAfter.CapturedAt = labelBefore.CapturedAt.AddMilliseconds(20);
        var labelTest = new TestCase { Name = "Passive child", Steps = [new TestStep { Title = "Click label", Action = StepAction.Click, Selector = "id:Label" }] };
        var parentAck = NativeObservation(click, labelAfter); parentAck.NativeReceipt!.HitRuntimeIds = ["runtime-label", "runtime-intended"];
        Check(!SavedWorkflowVerifier.Verify(labelTest, [Initial(labelBefore), parentAck]).Complete, "Parent Button acknowledgement was incorrectly credited to a passive text child.");
        var typingBefore = MappingSnapshot(); typingBefore.FocusedSelector = "id:Intended"; typingBefore.Elements[1].ControlType = "Edit"; typingBefore.Elements[1].Value = "Already correct";
        var typingAfter = TestyJson.Clone(typingBefore); typingAfter.CapturedAt = typingBefore.CapturedAt.AddMilliseconds(20);
        var typeTest = new TestCase { Name = "Focus receipt", Steps = [new TestStep { Title = "Enter correct value", Action = StepAction.TypeText, Selector = "id:Intended", Value = "Already correct" }] };
        var elsewhere = NativeObservation(new { type = "type", text = "Already correct" }, typingAfter);
        elsewhere.NativeReceipt!.HitRuntimeIds = ["runtime-other-focused-field"];
        Check(!SavedWorkflowVerifier.Verify(typeTest, [Initial(typingBefore), elsewhere]).Complete, "Stale focus snapshot hid typing into another field.");
        return Task.CompletedTask;
    }
    private static UiSnapshot MappingSnapshot() => new()
    {
        Target = new TargetInfo { ProcessId = 42 }, ScreenshotBounds = new ElementBounds { X = 100, Y = 200, Width = 500, Height = 400 },
        Elements = [new UiElementInfo { RuntimeId = "runtime-window", Selector = "id:Window", ControlType = "Window", Depth = 0, IsEnabled = true, Bounds = new ElementBounds { X = 100, Y = 200, Width = 500, Height = 400 } },
            new UiElementInfo { RuntimeId = "runtime-intended", Selector = "id:Intended", AutomationId = "Intended", ControlType = "Button", Depth = 1, IsEnabled = true, Bounds = new ElementBounds { X = 120, Y = 240, Width = 100, Height = 30 } }]
    };
    private static ComputerToolObservation Initial(UiSnapshot snapshot) => new() { ToolName = "observe_application", Snapshot = snapshot, Execution = new RunResult { Status = RunStatus.Passed } };
    private static ComputerToolObservation NativeObservation(object action, UiSnapshot snapshot)
    {
        var json = JsonSerializer.SerializeToElement(action);
        var ids = new List<string>();
        if (json.TryGetProperty("x", out var x) && json.TryGetProperty("y", out var y))
        {
            var screenX = snapshot.ScreenshotBounds.X + x.GetInt32(); var screenY = snapshot.ScreenshotBounds.Y + y.GetInt32();
            ids = snapshot.Elements.Where(e => e.ControlType != "Window" && screenX >= e.Bounds.X && screenX < e.Bounds.X + e.Bounds.Width && screenY >= e.Bounds.Y && screenY < e.Bounds.Y + e.Bounds.Height)
                .OrderByDescending(e => e.Depth).Select(e => e.RuntimeId).Where(id => !string.IsNullOrEmpty(id)).ToList();
        }
        else ids = snapshot.Elements.Where(e => e.Selector == snapshot.FocusedSelector).Select(e => e.RuntimeId).ToList();
        return new ComputerToolObservation { ToolName = "computer", Snapshot = snapshot, NativeAction = json,
            NativeReceipt = new NativeActionReceipt { ProcessId = snapshot.Target.ProcessId, CapturedAt = snapshot.CapturedAt, InputDelivered = true, HitRuntimeIds = ids },
            Execution = new RunResult { Status = RunStatus.Passed } };
    }
    private sealed class ResultAgent(List<ComputerToolObservation> observations) : IComputerAgent
    {
        public Task<ComputerAgentResult> RunAsync(string instructions, int maximumTurns = 30, IProgress<ComputerToolObservation>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ComputerAgentResult { Completed = true, Status = RunStatus.Pending, ModelTurns = 2, Observations = observations });
    }
    private static async Task WithNative(Func<string, ProviderSettings, Driver, Task> test)
    {
        var root = Temp("native");
        var variable = "TESTY_NATIVE_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "mock-key");
        try
        {
            using var driver = new Driver();
            await test(root, new ProviderSettings { Kind = ProviderKind.OpenAI, Model = "mock", ApiKeyEnvironmentVariable = variable }, driver);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); Directory.Delete(root, true); }
    }
    private static string Temp(string label) { var root = Path.Combine(Path.GetTempPath(), "Testy-" + label + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
    private static string Decision(bool done, string step) => "{\"done\":" + (done ? "true" : "false") + ",\"explanation\":\"Observe then verify\",\"toolName\":\"perform_ui_action\",\"step\":" + step + "}";
    private static HttpResponseMessage NativeCall(object actions) => Json(new { status = "completed", output = new[] { new { type = "computer_call", call_id = "native_1", actions } } });
    private static HttpResponseMessage Final(string text) => Json(new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text } } } } });
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => handle(request); }
    private sealed class Executor : IComputerActionExecutor, INativeActionReceiptSource
    {
        public bool Fail { get; set; }
        public NativeActionReceipt? ReceiptToReturn { get; set; }
        public NativeActionReceipt? LastReceipt { get; private set; }
        public int Calls { get; private set; }
        public Task ExecuteAsync(JsonElement action, CancellationToken ct = default)
        {
            Calls++; LastReceipt = null;
            if (Fail) throw new InvalidOperationException("Simulated native failure");
            if (ReceiptToReturn is not null) { ReceiptToReturn.CapturedAt = DateTimeOffset.UtcNow; LastReceipt = ReceiptToReturn; }
            return Task.CompletedTask;
        }
    }
    private sealed class Driver : ITargetDriver
    {
        public TargetInfo? Target { get; } = new() { ProcessId = 42, Title = "Fixture" };
        public int Mutations { get; private set; }
        public bool SlowFirstSnapshot { get; set; }
        private int snapshots;
        public Task<IReadOnlyList<TargetInfo>> GetTargetsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TargetInfo>>([Target!]);
        public Task AttachAsync(int processId, CancellationToken ct = default) => Task.CompletedTask;
        public async Task<UiSnapshot> SnapshotAsync(CancellationToken ct = default)
        {
            if (SlowFirstSnapshot && Interlocked.Increment(ref snapshots) == 1) await Task.Delay(500);
            return new UiSnapshot { Target = Target!, Elements = [new UiElementInfo { AutomationId = "Status", Selector = "id:Status", Name = "Ready", Value = "Ready", IsEnabled = true }] };
        }
        public Task ExecuteAsync(TestStep step, CancellationToken ct = default) { Mutations++; return Task.CompletedTask; }
        public async Task<string> CaptureAsync(string path, CancellationToken ct = default)
        { await File.WriteAllBytesAsync(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6hUAAAAASUVORK5CYII="), ct); return path; }
        public void Dispose() { }
    }
}
