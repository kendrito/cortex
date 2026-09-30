using System.Text.Json;
using Testy.Core;

namespace Testy.Tests;

internal static class SelectorChecks
{
    private const string ShippingApply = """query:{"id":"ApplyAction","type":"Button","label":"Apply","ancestor":{"label":"Shipping","type":"Group"}}""";
    public static IEnumerable<(string Name, Func<Task> Execute)> All()
    {
        yield return ("composite selectors combine exact id label and type", Composite);
        yield return ("ancestor selectors isolate duplicate controls across reordered sections", ScopedReordering);
        yield return ("nested ancestor selectors do not cross siblings windows or self", AncestorBoundaries);
        yield return ("query selectors reject malformed unknown duplicate and unbounded grammar", InvalidGrammar);
        yield return ("legacy selectors and literal Unicode values remain exact", LegacyAndLiterals);
        yield return ("scoped selector ambiguity and absence prevent mutation", RunnerFailures);
        yield return ("scoped selector runner requires complete trees and exact outcomes", RunnerSafety);
        yield return ("native saved key coverage honors scoped focus identity", NativeFocus);
    }
    private static Task Composite()
    {
        var snapshot = Tree();
        Check(UiSelectors.Find(snapshot, """query:{"label":"Apply"}""").Count == 4, "Label unexpectedly implied a control type or uniqueness.");
        Check(UiSelectors.Find(snapshot, """query:{"label":"Apply","type":"Button"}""").Count == 2, "Type and label were not combined.");
        Check(UiSelectors.Find(snapshot, """query:{"id":"ApplyAction","type":"Text"}""").Count == 0, "Conflicting properties were treated as alternatives.");
        Check(UiSelectors.Find(snapshot, """query:{"label":"apply","type":"Button"}""").Count == 0, "Case was ignored.");
        return Task.CompletedTask;
    }
    private static Task ScopedReordering()
    {
        foreach (var reverse in new[] { false, true })
        {
            var snapshot = Tree(reverse);
            var found = UiSelectors.Find(snapshot, ShippingApply);
            Check(found.Count == 1 && found[0].RuntimeId == "shipping-apply", "Scoped locator changed when sections reordered.");
            Check(UiSelectors.Matches(snapshot, found[0], ShippingApply), "Snapshot-aware matching lost the selected identity.");
        }
        return Task.CompletedTask;
    }
    private static Task AncestorBoundaries()
    {
        var snapshot = Tree();
        Check(UiSelectors.Find(snapshot, """query:{"label":"Shipping","ancestor":{"label":"Shipping"}}""").Count == 0, "An element was its own strict ancestor.");
        Check(UiSelectors.Find(snapshot, """query:{"label":"Billing","ancestor":{"label":"Shipping"}}""").Count == 0, "Sibling section leaked into ancestry.");
        Check(UiSelectors.Find(snapshot, """query:{"type":"Button","ancestor":{"label":"Shipping","ancestor":{"id":"OrderWindow"}}}""").Count == 1, "Nested ancestry did not match.");
        Check(UiSelectors.Find(snapshot, """query:{"type":"Button","ancestor":{"id":"OrderWindow","ancestor":{"label":"Shipping"}}}""").Count == 0, "Ancestor order was ignored.");
        snapshot.Elements.Add(Element("OtherWindow", "Other", "Window", 0));
        snapshot.Elements.Add(Element("ApplyAction", "Apply", "Button", 1, "other-apply"));
        Check(UiSelectors.Find(snapshot, ShippingApply).Single().RuntimeId == "shipping-apply", "Ancestry leaked across top-level windows.");
        Throws<InvalidOperationException>(() => UiSelectors.Matches(snapshot.Elements[3], ShippingApply));
        return Task.CompletedTask;
    }
    private static Task InvalidGrammar()
    {
        string[] invalid = ["query:{}", "query:[]", "query:{", "query:{\"id\":1}", "query:{\"id\":null}",
            "query:{\"id\":\" \"}", "query:{\"name\":\"Apply\"}", "query:{\"id\":\"a\",\"id\":\"b\"}",
            "query:{\"id\":\"a\",\"ancestor\":null}", "query:{\"ancestor\":{\"id\":\"a\"}}", "query:{\"id\":\"a\",\"index\":0}",
            "query:{\"id\":\"a\",}", "id:", "name:", "path:", "Apply"];
        foreach (var value in invalid) Throws<InvalidDataException>(() => TestValidator.ValidateStep(Step(StepAction.Click, value)));
        var deep = "{\"id\":\"a\"}";
        for (int i = 0; i < UiSelectors.MaximumQueryLevels; i++) deep = "{\"id\":\"a\",\"ancestor\":" + deep + "}";
        Throws<InvalidDataException>(() => UiSelectors.Validate("query:" + deep));
        Throws<InvalidDataException>(() => UiSelectors.Validate("query:{\"id\":\"" + new string('a', 2048) + "\"}"));
        return Task.CompletedTask;
    }
    private static Task LegacyAndLiterals()
    {
        var snapshot = Tree();
        Check(UiSelectors.Find(snapshot, "id:ApplyAction").Count == 2, "Legacy duplicate IDs were silently narrowed.");
        Check(UiSelectors.Find(snapshot, "name:Apply").Count == 4, "Legacy names changed.");
        var button = snapshot.Elements.First(e => e.RuntimeId == "shipping-apply");
        Check(UiSelectors.Find(snapshot, button.Selector).Single() == button, "Observed legacy path stopped resolving.");
        button.Name = "送付 \"A/B\": 東京";
        var query = "query:" + JsonSerializer.Serialize(new { type = "Button", label = button.Name, ancestor = new { label = "Shipping" } });
        Check(UiSelectors.Find(snapshot, query).Single() == button, "Literal escaped Unicode was not preserved.");
        return Task.CompletedTask;
    }
    private static async Task RunnerFailures()
    {
        using var driver = new SelectorDriver(Tree());
        var ambiguous = await Run(driver, Step(StepAction.Click, """query:{"id":"ApplyAction","type":"Button"}"""));
        Check(ambiguous.Status == RunStatus.Failed && ambiguous.Steps[0].Message.Contains("ambiguous"), "Ambiguous query did not fail explicitly.");
        var missing = await Run(driver, Step(StepAction.Click, """query:{"id":"Missing","ancestor":{"label":"Shipping"}}"""));
        Check(missing.Status == RunStatus.Failed && driver.Executed.Count == 0, "Absent target dispatched input.");
    }
    private static async Task RunnerSafety()
    {
        using var driver = new SelectorDriver(Tree());
        var passed = await Run(driver, Step(StepAction.Click, ShippingApply), Step(StepAction.AssertText, "id:Status", "Applied shipping"));
        Check(passed.Status == RunStatus.Passed && driver.Executed.SequenceEqual(["shipping-apply"]), "Scoped action failed to preserve exact expected outcome.");
        driver.Snapshot.IsTruncated = true;
        Throws<InvalidOperationException>(() => UiSelectors.Find(driver.Snapshot, ShippingApply));
        var failed = await Run(driver, Step(StepAction.AssertNotExists, """query:{"id":"Missing","ancestor":{"label":"Shipping"}}"""));
        Check(failed.Status == RunStatus.Failed && failed.Steps[0].Message.Contains("truncated"), "Truncated scoped absence was treated as proof.");
        failed = await Run(driver, Step(StepAction.Click, ShippingApply));
        Check(failed.Status == RunStatus.Failed && driver.Executed.Count == 1, "Truncated query dispatched another action.");
    }
    private static Task NativeFocus()
    {
        var before = Tree(); before.CapturedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        var focus = before.Elements.Single(e => e.RuntimeId == "shipping-apply"); before.FocusedSelector = focus.Selector;
        var after = TestyJson.Clone(before); after.CapturedAt = DateTimeOffset.UtcNow;
        var action = new ComputerToolObservation
        {
            NativeAction = JsonSerializer.SerializeToElement(new { type = "keypress", keys = new[] { "SPACE" } }), Snapshot = after,
            NativeReceipt = new() { ProcessId = before.Target.ProcessId, CapturedAt = before.CapturedAt.AddMilliseconds(500), InputDelivered = true, HitRuntimeIds = [focus.RuntimeId] },
            Execution = new() { Status = RunStatus.Passed }
        };
        var initial = new ComputerToolObservation { ToolName = "observe_application", Snapshot = before };
        var test = new TestCase { Steps = [Step(StepAction.KeyPress, ShippingApply, "SPACE")] };
        Check(SavedWorkflowVerifier.Verify(test, [initial, action]).Complete, "Acknowledged native keypress did not recognize scoped focus.");
        before.FocusedSelector = before.Elements.Single(e => e.RuntimeId == "billing-apply").Selector;
        Check(!SavedWorkflowVerifier.Verify(test, [initial, action]).Complete, "Wrong scoped field satisfied native saved coverage.");
        return Task.CompletedTask;
    }
    private static UiSnapshot Tree(bool reverse = false)
    {
        var elements = new List<UiElementInfo> { Element("OrderWindow", "Orders", "Window", 0) };
        foreach (var section in reverse ? new[] { "Billing", "Shipping" } : new[] { "Shipping", "Billing" })
        {
            elements.Add(Element("", section, "Group", 1));
            elements.Add(Element("", "Template", "Pane", 2));
            elements.Add(Element("ApplyAction", "Apply", "Button", 3, section.ToLowerInvariant() + "-apply"));
            elements.Add(Element("", "Apply", "Text", 4));
        }
        elements.Add(Element("Status", "Status", "Text", 1));
        for (int i = 0; i < elements.Count; i++) elements[i].Selector = elements[i].AutomationId is "OrderWindow" or "Status" ? "id:" + elements[i].AutomationId : "path:0/" + i;
        return new() { Target = new() { ProcessId = 55 }, Elements = elements };
    }
    private static UiElementInfo Element(string id, string label, string type, int depth, string runtime = "") => new() { AutomationId = id, Name = label, ControlType = type, Depth = depth, RuntimeId = runtime, IsEnabled = true };
    private static TestStep Step(StepAction action, string selector, string value = "") => new() { Title = "Selector check", Action = action, Selector = selector, Value = value, TimeoutMs = 150 };
    private static async Task<RunResult> Run(SelectorDriver driver, params TestStep[] steps)
    {
        var folder = Path.Combine(Path.GetTempPath(), "testy-selector-checks-" + Guid.NewGuid().ToString("N"));
        try { return await new TestRunner(driver, folder).RunAsync(new() { Name = "Selectors", Steps = steps.ToList() }); }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed class SelectorDriver(UiSnapshot snapshot) : ITargetDriver
    {
        public UiSnapshot Snapshot { get; } = snapshot;
        public List<string> Executed { get; } = [];
        public TargetInfo? Target => Snapshot.Target;
        public Task<IReadOnlyList<TargetInfo>> GetTargetsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TargetInfo>>([Snapshot.Target]);
        public Task AttachAsync(int processId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<UiSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
        public Task ExecuteAsync(TestStep step, CancellationToken cancellationToken = default)
        {
            var found = UiSelectors.Find(Snapshot, step.Selector).Single(); Executed.Add(found.RuntimeId);
            Snapshot.Elements.Single(e => e.AutomationId == "Status").Value = "Applied " + (found.RuntimeId == "shipping-apply" ? "shipping" : "billing");
            return Task.CompletedTask;
        }
        public Task<string> CaptureAsync(string filePath, CancellationToken cancellationToken = default)
        { File.WriteAllBytes(filePath, [1]); return Task.FromResult(filePath); }
        public void Dispose() { }
    }
}
