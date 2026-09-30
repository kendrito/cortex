using System.Text.Json;
using Testy.Core;

namespace Testy.Tests;

internal static class WorkflowCoverageChecks
{
    public static IEnumerable<(string Name, Func<Task> Execute)> All()
    {
        yield return ("saved workflow requires each exact local mutation in order", LocalActions);
        yield return ("native clicks and typing map to observed saved controls", NativeActions);
        yield return ("native typing into the wrong field cannot claim an existing correct value", WrongNativeField);
        yield return ("initial automatic observation cannot fulfill a saved screenshot step", ExplicitScreenshot);
        yield return ("native workflow mapping rejects outside coordinates and truncated trees", InvalidNativeEvidence);
        yield return ("native workflow value comparisons reject truncated observations", TruncatedNativeValues);
    }

    private static Task LocalActions()
    {
        var click = Step(StepAction.Click, "id:Add");
        var type = Step(StepAction.TypeText, "id:Name", "new value");
        var assertion = Step(StepAction.AssertText, "id:Status", "Ready");
        var test = Test(type, click, assertion);
        Check(!SavedWorkflowVerifier.Verify(test, [Local(assertion)]).Complete, "Final assertion substituted for all interactions.");
        Check(!SavedWorkflowVerifier.Verify(test, [Local(click), Local(type), Local(assertion)]).Complete, "Reordered actions were accepted.");
        Check(!SavedWorkflowVerifier.Verify(test, [Local(Step(StepAction.TypeText, "id:Name", "wrong")), Local(click), Local(assertion)]).Complete, "Changed mutation value was accepted.");
        Check(SavedWorkflowVerifier.Verify(test, [Local(type), Local(click), Local(assertion)]).Complete, "Exact ordered local workflow was rejected.");
        var repeat = Local(click);
        Check(!SavedWorkflowVerifier.Verify(Test(click, TestyJson.Clone(click)), [repeat, repeat]).Complete, "Repeated observation reference covered two interactions.");
        return Task.CompletedTask;
    }

    private static Task NativeActions()
    {
        var test = Test(Step(StepAction.Click, "id:Add"), Step(StepAction.TypeText, "id:Name", "new value"), Step(StepAction.AssertText, "id:Status", "Ready"));
        var initial = Snapshot();
        var afterAdd = Snapshot(focus: "id:Add");
        var afterFocus = Snapshot(focus: "id:Name");
        var afterType = Snapshot("new value", "id:Name");
        var observations = new List<ComputerToolObservation>
        {
            Initial(initial), Native(new { type = "click", x = 30, y = 50, button = "left" }, afterAdd),
            Native(new { type = "click", x = 30, y = 100, button = "left" }, afterFocus),
            Native(new { type = "type", text = "new value" }, afterType), Local(test.Steps[2], afterType)
        };
        Check(SavedWorkflowVerifier.Verify(test, observations).Complete, "Observed native click/focus/type/local assertion did not map to the saved workflow.");
        return Task.CompletedTask;
    }

    private static Task WrongNativeField()
    {
        var test = Test(Step(StepAction.TypeText, "id:Name", "already correct"));
        var before = Snapshot("already correct", "id:Other");
        var after = Snapshot("already correct", "id:Other");
        Check(!SavedWorkflowVerifier.Verify(test, [Initial(before), Native(new { type = "type", text = "already correct" }, after)]).Complete,
            "Typing in another focused field passed because the expected field already held the desired value.");
        Check(!SavedWorkflowVerifier.Verify(test, [Initial(Snapshot("already correct", "id:Name")), Native(new { type = "click", x = 30, y = 100 }, after)]).Complete,
            "Clicking an already correct field substituted for typing.");
        return Task.CompletedTask;
    }

    private static Task ExplicitScreenshot()
    {
        var screenshot = Step(StepAction.Screenshot);
        var assertion = Step(StepAction.AssertText, "id:Status", "Ready");
        var test = Test(screenshot, assertion);
        Check(!SavedWorkflowVerifier.Verify(test, [Initial(Snapshot()), Local(assertion)]).Complete, "Automatic initial observation replaced an explicitly saved screenshot action.");
        Check(SavedWorkflowVerifier.Verify(test, [Initial(Snapshot()), Local(screenshot), Local(assertion)]).Complete, "Explicit screenshot action was not accepted.");
        return Task.CompletedTask;
    }

    private static Task InvalidNativeEvidence()
    {
        var test = Test(Step(StepAction.Click, "id:Add"));
        Check(!SavedWorkflowVerifier.Verify(test, [Initial(Snapshot()), Native(new { type = "click", x = -1, y = 50 }, Snapshot())]).Complete, "Out-of-image native coordinate was accepted.");
        Check(!SavedWorkflowVerifier.Verify(test, [Initial(Snapshot()), Native(new { type = "click", x = 30, y = 100 }, Snapshot())]).Complete, "Wrong control click was accepted.");
        var truncated = Snapshot(); truncated.IsTruncated = true;
        Check(!SavedWorkflowVerifier.Verify(test, [Initial(truncated), Native(new { type = "click", x = 30, y = 50 }, Snapshot())]).Complete, "Truncated source tree proved native control correspondence.");
        var disabled = Snapshot(); disabled.Elements.Single(e => e.AutomationId == "Add").IsEnabled = false;
        var assertion = Step(StepAction.AssertText, "id:Status", "Ready");
        Check(!SavedWorkflowVerifier.Verify(Test(test.Steps[0], assertion), [Initial(disabled), Native(new { type = "click", x = 30, y = 50 }, Snapshot()), Local(assertion)]).Complete,
            "Disabled-control coordinates plus an already true assertion claimed a required interaction.");
        return Task.CompletedTask;
    }

    private static Task TruncatedNativeValues()
    {
        var text = new string('x', 8192);
        var initial = Snapshot("", "id:Name"); var typed = Snapshot(text, "id:Name");
        var test = Test(Step(StepAction.TypeText, "id:Name", text));
        Check(SavedWorkflowVerifier.Verify(test, [Initial(initial), Native(new { type = "type", text }, typed)]).Complete, "Complete native typed value was not accepted.");
        typed.Elements.Single(e => e.AutomationId == "Name").IsValueTruncated = true;
        Check(!SavedWorkflowVerifier.Verify(test, [Initial(initial), Native(new { type = "type", text }, typed)]).Complete, "Truncated native typed prefix proved replacement.");

        foreach (var action in new[] { StepAction.Toggle, StepAction.Select })
        {
            var before = Snapshot(); before.Elements = [before.Elements[0]];
            before.Elements[0].Value = action == StepAction.Toggle ? "Off" : "Old";
            var expected = action == StepAction.Toggle ? "On" : "Choice";
            if (action == StepAction.Select)
                before.Elements.Add(new() { AutomationId = "Option", RuntimeId = "test-option", Name = expected, ControlType = "ListItem", IsEnabled = true, Depth = 1,
                    Bounds = new() { X = 120, Y = 240, Width = 100, Height = 30 } });
            var after = TestyJson.Clone(before); after.CapturedAt = DateTimeOffset.UtcNow; after.Elements[0].Value = expected;
            var workflow = Test(Step(action, "id:Add", expected));
            var input = new { type = "click", x = 30, y = 50, button = "left" };
            Check(SavedWorkflowVerifier.Verify(workflow, [Initial(before), Native(input, after)]).Complete, "Complete native value transition was not accepted: " + action);
            after.Elements[0].IsValueTruncated = true;
            Check(!SavedWorkflowVerifier.Verify(workflow, [Initial(before), Native(input, after)]).Complete, "Truncated native final value proved " + action);
            after.Elements[0].IsValueTruncated = false; before.Elements[0].IsValueTruncated = true;
            Check(!SavedWorkflowVerifier.Verify(workflow, [Initial(before), Native(input, after)]).Complete, "Truncated native initial value proved transition for " + action);
        }
        return Task.CompletedTask;
    }
    private static TestStep Step(StepAction action, string selector = "", string value = "") => new() { Title = action + " " + selector, Action = action, Selector = selector, Value = value };
    private static TestCase Test(params TestStep[] steps) => new() { Name = "Workflow mapping", Steps = steps.ToList() };
    private static ComputerToolObservation Local(TestStep step, UiSnapshot? snapshot = null) => new() { Snapshot = snapshot ?? Snapshot(), Execution = new RunResult { TestName = "Agent action", Status = RunStatus.Passed, Steps = [new StepResult { Step = step, Status = RunStatus.Passed }] } };
    private static ComputerToolObservation Initial(UiSnapshot snapshot) { var observation = Local(Step(StepAction.Screenshot), snapshot); observation.Execution!.TestName = "Agent observation"; return observation; }
    private static ComputerToolObservation Native(object action, UiSnapshot snapshot)
    {
        var json = JsonSerializer.SerializeToElement(action);
        NativeActionReceipt? receipt = null;
        if (json.TryGetProperty("x", out var x) && json.TryGetProperty("y", out var y))
        {
            var absoluteX = snapshot.ScreenshotBounds.X + x.GetInt32();
            var absoluteY = snapshot.ScreenshotBounds.Y + y.GetInt32();
            receipt = new NativeActionReceipt
            {
                ProcessId = 42, CapturedAt = snapshot.CapturedAt, InputDelivered = true,
                HitRuntimeIds = snapshot.Elements.Where(e => e.Bounds.Width > 0 && e.Bounds.Height > 0 &&
                    absoluteX >= e.Bounds.X && absoluteX < e.Bounds.X + e.Bounds.Width &&
                    absoluteY >= e.Bounds.Y && absoluteY < e.Bounds.Y + e.Bounds.Height).Select(e => e.RuntimeId).ToList()
            };
        }
        else if (json.GetProperty("type").GetString() is "type" or "keypress")
        {
            receipt = new NativeActionReceipt
            {
                ProcessId = 42, CapturedAt = snapshot.CapturedAt, InputDelivered = true,
                HitRuntimeIds = [snapshot.Elements.SingleOrDefault(e => e.Selector == snapshot.FocusedSelector)?.RuntimeId ?? ""]
            };
        }
        return new ComputerToolObservation { Snapshot = snapshot, NativeAction = json, NativeReceipt = receipt, Execution = new RunResult { Status = RunStatus.Passed } };
    }
    private static UiSnapshot Snapshot(string value = "", string focus = "") => new()
    {
        Target = new TargetInfo { ProcessId = 42 }, ScreenshotBounds = new ElementBounds { X = 100, Y = 200, Width = 500, Height = 400 }, FocusedSelector = focus,
        Elements =
        [
            new() { AutomationId = "Add", RuntimeId = "test-add", Selector = "id:Add", Name = "Add", ControlType = "Button", IsEnabled = true, Bounds = new ElementBounds { X = 120, Y = 240, Width = 100, Height = 30 } },
            new() { AutomationId = "Name", RuntimeId = "test-name", Selector = "id:Name", Name = "Name", ControlType = "Edit", IsEnabled = true, Value = value, Bounds = new ElementBounds { X = 120, Y = 290, Width = 100, Height = 30 } },
            new() { AutomationId = "Other", RuntimeId = "test-other", Selector = "id:Other", Name = "Other", ControlType = "Edit", IsEnabled = true, Bounds = new ElementBounds { X = 120, Y = 340, Width = 100, Height = 30 } },
            new() { AutomationId = "Status", RuntimeId = "test-status", Selector = "id:Status", Name = "Ready", Value = "Ready", IsEnabled = true }
        ]
    };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
