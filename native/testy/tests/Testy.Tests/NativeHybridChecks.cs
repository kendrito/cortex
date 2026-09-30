using System.Net;
using System.Text;
using System.Text.Json;
using Testy.Core;
using static Testy.Tests.AdvancedCoreChecks;

namespace Testy.Tests;

internal static class NativeHybridChecks
{
    public static IEnumerable<(string Name, Func<Task> Execute)> All()
    {
        yield return ("native hybrid protocol combines native input with every canonical advanced control operation", HybridProtocol);
        yield return ("native hybrid tool categories IDs order and one-call boundary reject misuse before input", HybridBoundaries);
        yield return ("hybrid semantic dispatch requires earlier full native workflow receipt coverage", PhysicalPrefix);
        yield return ("hybrid unsupported capabilities stop before input and retain deterministic diagnostics", UnsupportedCapability);
        yield return ("missing or incorrectly attributed hybrid semantic steps cannot satisfy saved workflow coverage", SemanticCoverage);
        yield return ("native saved workflow budgets reject impossible execution before model or driver calls", NativeBudgetPreflight);
        yield return ("native saved waits beyond executor capability reject before model or input", NativeWaitPreflight);
        yield return ("native receipt coverage rejects blank and reserved probe identities", ReceiptIdentityBoundary);
    }
    private static ProviderSettings Settings(AdvancedFixture fixture) => new() { Kind = ProviderKind.OpenAI, Model = "fixture-native", Endpoint = "http://localhost/mock", ApiKeyEnvironmentVariable = fixture.KeyVariable, MaximumAgentTurns = 20 };
    private static TestCase Test(params TestStep[] steps) => new() { Name = "Native hybrid fixture", Steps = steps.ToList() };
    private static TestStep Property(string name, object expected) => AdvancedFixture.Step(StepAction.AssertProperty, JsonSerializer.Serialize(new { property = name, equals = expected }));
    private static TestStep Point() => new() { Title = "Native point click", Action = StepAction.CoordinateClick, X = 0, Y = 0, TimeoutMs = 100 };
    private static string Arguments(TestStep step) => JsonSerializer.Serialize(new { stepId = step.Id });
    private static object Function(string name, string arguments, string id = "function-1") => new { type = "function_call", call_id = id, name, arguments };
    private static object Computer(string id = "computer-1") => new { type = "computer_call", call_id = id, actions = new[] { new { type = "click", button = "left", x = 0, y = 0 } } };
    private static HttpResponseMessage Outputs(params object[] output) => Json(new { status = "completed", output });
    private static HttpResponseMessage Final() => Outputs(new { type = "message", content = new[] { new { type = "output_text", text = "Saved evidence reviewed; workflow complete." } } });
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private static async Task HybridProtocol()
    {
        using var f = new AdvancedFixture();
        f.Element.Capabilities.AddRange(["ExpandCollapse", "ItemContainer", "ScrollItem", "Scroll"]);
        f.OnInput = step =>
        {
            if (step.Action is StepAction.Expand or StepAction.Collapse)
                f.Element.Properties["uia.expandCollapseState"] = new() { Status = UiPropertyStatus.Known, Value = JsonSerializer.SerializeToElement(step.Action == StepAction.Expand ? "Expanded" : "Collapsed"), Source = "fixture pattern" };
            if (step.Action == StepAction.ScrollPercent)
                f.Element.Properties["uia.verticalScrollPercent"] = new() { Status = UiPropertyStatus.Known, Value = JsonSerializer.SerializeToElement(AdvancedSteps.ParseScrollPercent(step.Value).Vertical!.Value), Source = "fixture Scroll" };
        };
        var expand = AdvancedFixture.Step(StepAction.Expand); expand.Selector = "query:{\"id\":\"Container\"}";
        var test = Test(Point(), expand, Property("uia.expandCollapseState", "Expanded"),
            AdvancedFixture.Step(StepAction.RealizeItem, "{\"item\":{\"id\":\"Order-1042\"}}"), AdvancedFixture.Step(StepAction.AssertItemExists, "{\"item\":{\"id\":\"Order-1042\"}}"),
            AdvancedFixture.Step(StepAction.ScrollIntoView), AdvancedFixture.Step(StepAction.ScrollPercent, "{\"vertical\":65,\"horizontal\":0}"), Property("uia.verticalScrollPercent", 65),
            AdvancedFixture.Step(StepAction.Collapse), Property("uia.expandCollapseState", "Collapsed"));
        var canonical = TestyJson.Clone(test);
        var turn = 0; var executor = new Executor();
        using var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = body.RootElement;
            var offered = root.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("type").GetString() == "computer" ? "computer" : t.GetProperty("name").GetString()).ToArray();
            Check(offered.SequenceEqual(new[] { "computer", "verify_saved_assertion", "perform_saved_control_step" }), "Hybrid provider did not receive native and explicit semantic tools together.");
            var input = root.GetProperty("input").GetRawText();
            if (turn > 1) Check(input.Contains("perform_saved_control_step", StringComparison.Ordinal) && input.Contains(expand.Id, StringComparison.Ordinal), "Next model decision lost explicit semantic operation identity.");
            var index = turn++;
            if (index == canonical.Steps.Count) return Final();
            var step = canonical.Steps[index];
            if (index == 0) return Outputs(Computer());
            return Outputs(Function(TestValidator.IsAssertion(step.Action) ? "verify_saved_assertion" : "perform_saved_control_step", Arguments(step), "step-" + index));
        });
        using var client = new HttpClient(handler);
        var agent = new NativeComputerUseAgent(Settings(f), f, f.Root, executor, client, test);
        // Caller edits after binding cannot rewrite any dispatched operation.
        expand.Selector = "id:Wrong";
        var result = await agent.RunAsync("Execute every saved step.", 12);
        Check(result.Completed && result.Status == RunStatus.Pending && result.ModelTurns == 11 && executor.Calls == 1, "Hybrid protocol did not complete one action per turn: " + result.Message);
        Check(f.Inputs.Select(s => s.Action).SequenceEqual(new[] { StepAction.Expand, StepAction.RealizeItem, StepAction.ScrollIntoView, StepAction.ScrollPercent, StepAction.Collapse }), "Ordinary physical input leaked into local driver or advanced action was repeated/skipped.");
        Check(f.Inputs[0].Selector == canonical.Steps[1].Selector && f.Inputs[1].Value == canonical.Steps[3].Value, "Hybrid dispatched a rewritten canonical definition.");
        var semantic = result.Observations.Where(o => o.ToolName == "perform_saved_control_step").ToList();
        Check(semantic.Count == 5 && semantic.All(o => o.NativeAction is null && o.SavedStepId == o.Execution!.Steps[0].Step.Id && File.Exists(o.ScreenshotPath) && o.Snapshot is not null), "Explicit semantic receipts lack canonical identity or fresh evidence.");
        Check(SavedWorkflowVerifier.Verify(canonical, result.Observations).Complete, "Exact native and semantic observations failed full ordered workflow coverage.");
    }

    private static async Task HybridBoundaries()
    {
        foreach (var scenario in new[] { "assertion-as-control", "control-as-assertion", "physical-as-control", "unknown", "out-of-order", "repeated", "rewritten", "duplicate-id-field", "batch", "raw-bound-tool" })
        {
            using var f = new AdvancedFixture(); f.Element.Capabilities.Add("ExpandCollapse");
            var expand = AdvancedFixture.Step(StepAction.Expand); var collapse = AdvancedFixture.Step(StepAction.Collapse); var assertion = AdvancedFixture.Step(StepAction.AssertExists); var click = AdvancedFixture.Step(StepAction.Click);
            var test = scenario == "assertion-as-control" ? Test(assertion) : scenario == "physical-as-control" ? Test(click, assertion) : Test(expand, collapse, assertion);
            var turn = 0; var executor = new Executor();
            using var handler = new Handler(_ =>
            {
                turn++;
                var call = scenario switch
                {
                    "assertion-as-control" => Function("perform_saved_control_step", Arguments(assertion)),
                    "control-as-assertion" => Function("verify_saved_assertion", Arguments(expand)),
                    "physical-as-control" => Function("perform_saved_control_step", Arguments(click)),
                    "unknown" => Function("perform_saved_control_step", "{\"stepId\":\"unknown\"}"),
                    "out-of-order" => Function("perform_saved_control_step", Arguments(collapse)),
                    "rewritten" => Function("perform_saved_control_step", "{\"stepId\":\"" + expand.Id + "\",\"selector\":\"id:Other\"}"),
                    "duplicate-id-field" => Function("perform_saved_control_step", "{\"stepId\":\"" + expand.Id + "\",\"stepId\":\"" + expand.Id + "\"}"),
                    "raw-bound-tool" => Function("perform_saved_step", Arguments(expand)),
                    _ => Function("perform_saved_control_step", Arguments(expand), "request-" + turn)
                };
                return Task.FromResult(scenario == "batch" ? Outputs(Computer(), call) : Outputs(call));
            });
            using var client = new HttpClient(handler);
            var result = await new NativeComputerUseAgent(Settings(f), f, f.Root, executor, client, test).RunAsync("Execute.", 5);
            Check(result.Status == RunStatus.Failed && !result.Completed && executor.Calls == 0 && f.Inputs.Count == (scenario == "repeated" ? 1 : 0), "Hybrid " + scenario + " performed unauthorized input.");
        }
    }

    private static async Task PhysicalPrefix()
    {
        using var f = new AdvancedFixture(); f.Element.Capabilities.Add("ExpandCollapse");
        f.Element.RuntimeId = "runtime-container"; f.Element.Bounds = new() { Width = 1, Height = 1 };
        var click = AdvancedFixture.Step(StepAction.Click); var expand = AdvancedFixture.Step(StepAction.Expand); var assertion = AdvancedFixture.Step(StepAction.AssertExists);
        var test = Test(click, expand, assertion); var turn = 0; var executor = new Executor();
        using var handler = new Handler(_ => Task.FromResult(++turn == 1 ? Outputs(Computer()) : Outputs(Function("perform_saved_control_step", Arguments(expand)))));
        using var client = new HttpClient(handler);
        var result = await new NativeComputerUseAgent(Settings(f), f, f.Root, executor, client, test).RunAsync("Execute saved controls.", 5);
        Check(result.Status == RunStatus.Failed && executor.Calls == 1 && f.Inputs.Count == 0 && result.Message.Contains("out of full workflow order", StringComparison.Ordinal), "Semantic input bypassed an earlier missing native delivery receipt.");
        Check(!SavedWorkflowVerifier.Verify(test, result.Observations).Complete, "Unacknowledged physical action counted as saved workflow coverage.");
    }

    private static async Task UnsupportedCapability()
    {
        using var f = new AdvancedFixture();
        var expand = AdvancedFixture.Step(StepAction.Expand); var test = Test(expand, AdvancedFixture.Step(StepAction.AssertExists)); var turn = 0;
        using var handler = new Handler(_ => Task.FromResult(++turn == 1 ? Outputs(Function("perform_saved_control_step", Arguments(expand))) : Final()));
        using var client = new HttpClient(handler);
        var result = await new NativeComputerUseAgent(Settings(f), f, f.Root, new Executor(), client, test).RunAsync("Execute saved controls.", 4);
        Check(result.Status == RunStatus.Failed && f.Inputs.Count == 0 && result.Observations[1].ToolName == "perform_saved_control_step" && result.Observations[1].Execution!.FailureDiagnostics.Any(d => d.Category == FailureCategory.CapabilityUnavailable), "Unsupported hybrid capability reached input or lost its deterministic failure.");
    }

    private static async Task SemanticCoverage()
    {
        using var f = new AdvancedFixture(); f.Element.Capabilities.Add("ExpandCollapse");
        var expand = AdvancedFixture.Step(StepAction.Expand); var assertion = AdvancedFixture.Step(StepAction.AssertExists); var test = Test(expand, assertion);
        var tools = new LocalComputerTools(f, Path.Combine(f.Root, "evidence"), savedTest: test);
        var initial = await tools.DispatchAsync("observe_application", "{}");
        var mutation = await tools.DispatchAsync("perform_saved_step", Arguments(expand)); mutation.ToolName = "perform_saved_control_step";
        var check = await tools.DispatchAsync("perform_saved_step", Arguments(assertion)); check.ToolName = "verify_saved_assertion";
        Check(SavedWorkflowVerifier.Verify(test, [initial, mutation, check]).Complete, "Canonical semantic receipt not credited.");
        var wrongIdentity = TestyJson.Clone(mutation); wrongIdentity.SavedStepId = "wrong";
        var wrongTool = TestyJson.Clone(mutation); wrongTool.ToolName = "verify_saved_assertion";
        var fakeNative = TestyJson.Clone(mutation); fakeNative.ToolName = "computer"; fakeNative.NativeAction = JsonSerializer.SerializeToElement(new { type = "click", button = "left", x = 0, y = 0 });
        foreach (var candidate in new[] { wrongIdentity, wrongTool, fakeNative })
            Check(!SavedWorkflowVerifier.Verify(test, [initial, candidate, check]).Complete, "A wrong identity/tool or native click inferred an advanced semantic operation.");
        var omitted = new ComputerAgentResult { Status = RunStatus.Pending, Completed = true, Message = "Model says done.", Observations = [initial, check] };
        var result = await new AiTestRunner(f, Settings(f), Path.Combine(f.Root, "missing"), agentFactory: _ => new FixedAgent(omitted)).RunAsync(test);
        Check(result.Status == RunStatus.Failed && result.FailureDiagnostics.Any(d => d.Category == FailureCategory.WorkflowNotVerified), "Passing assertion replaced an omitted semantic mutation.");
    }
    private static async Task NativeBudgetPreflight()
    {
        using var f = new AdvancedFixture();
        var settings = Settings(f); settings.MaximumAgentTurns = 2;
        var test = Test(Point(), AdvancedFixture.Step(StepAction.AssertExists));
        Reject(() => AiTestRunner.ValidateExecution(test, settings));
        var requests = 0; var executor = new Executor();
        using var handler = new Handler(_ => { requests++; return Task.FromResult(Final()); }); using var client = new HttpClient(handler);
        var agent = new NativeComputerUseAgent(settings, f, f.Root, executor, client, test);
        try { await agent.RunAsync("Execute.", 2); throw new InvalidOperationException("Impossible native budget accepted."); }
        catch (InvalidDataException ex) { Check(ex.Message.Contains("at least 3 model turns", StringComparison.Ordinal), "Native budget preflight reported the wrong reason."); }
        Check(requests == 0 && executor.Calls == 0 && f.Inputs.Count == 0 && f.Snapshots == 0, "Native budget rejection occurred after model or target work.");
        settings.MaximumAgentTurns = 3; AiTestRunner.ValidateExecution(test, settings);
    }
    private static async Task NativeWaitPreflight()
    {
        using var f = new AdvancedFixture(); var settings = Settings(f); var requests = 0; var executor = new Executor();
        using var handler = new Handler(_ => { requests++; return Task.FromResult(Final()); }); using var client = new HttpClient(handler);
        foreach (var wait in new[] { new TestStep { Action = StepAction.Wait, Value = "5001" }, new TestStep { Action = StepAction.Wait, TimeoutMs = 5001 } })
        {
            var test = Test(wait, AdvancedFixture.Step(StepAction.AssertExists));
            Reject(() => AiTestRunner.ValidateExecution(test, settings));
            var agent = new NativeComputerUseAgent(settings, f, f.Root, executor, client, test);
            try { await agent.RunAsync("Wait.", 3); throw new InvalidOperationException("Unsupported native wait accepted."); }
            catch (InvalidDataException ex) { Check(ex.Message.Contains("at most 5000 ms", StringComparison.Ordinal), "Native wait preflight reported the wrong reason."); }
            var local = TestyJson.Clone(settings); local.NativeComputerUse = false; AiTestRunner.ValidateExecution(test, local);
        }
        AiTestRunner.ValidateExecution(Test(new TestStep { Action = StepAction.Wait, Value = "5000" }, AdvancedFixture.Step(StepAction.AssertExists)), settings);
        Check(requests == 0 && executor.Calls == 0 && f.Inputs.Count == 0 && f.Snapshots == 0, "Unsupported saved native wait reached model or input.");
    }
    private static Task ReceiptIdentityBoundary()
    {
        var before = new UiSnapshot
        {
            Target = new() { ProcessId = 42 }, ScreenshotBounds = new() { Width = 300, Height = 200 }, FocusedSelector = "id:Control",
            Elements = [new() { AutomationId = "Control", Selector = "id:Control", ControlType = "Button", IsEnabled = true, RuntimeId = "runtime-control", Bounds = new() { X = 10, Y = 10, Width = 50, Height = 20 } }]
        };
        var after = TestyJson.Clone(before); after.CapturedAt = before.CapturedAt.AddMilliseconds(10);
        var initial = new ComputerToolObservation { ToolName = "observe_application", Snapshot = before };
        ComputerToolObservation Observed(object action, params string[] ids) => new()
        {
            ToolName = "computer", NativeAction = JsonSerializer.SerializeToElement(action), Snapshot = after,
            NativeReceipt = new() { ProcessId = 42, CapturedAt = before.CapturedAt.AddMilliseconds(5), InputDelivered = true, HitRuntimeIds = ids.ToList() },
            Execution = new() { Status = RunStatus.Passed }
        };
        var clickTest = Test(new TestStep { Action = StepAction.Click, Selector = "query:{\"id\":\"Control\",\"type\":\"Button\"}" });
        var keyTest = Test(new TestStep { Action = StepAction.KeyPress, Value = "ENTER" });
        object click = new { type = "click", button = "left", x = 20, y = 15 }; object key = new { type = "keypress", keys = new[] { "ENTER" } };
        Check(SavedWorkflowVerifier.Verify(clickTest, [initial, Observed(click, "runtime-control")]).Complete && SavedWorkflowVerifier.Verify(keyTest, [initial, Observed(key, "runtime-control")]).Complete, "Valid fixture receipt stopped working.");
        foreach (var invalid in new[] { "", "  ", "wpf:synthetic-control", "WPF:synthetic-control" })
        {
            before.Elements[0].RuntimeId = invalid; after.Elements[0].RuntimeId = invalid;
            Check(!SavedWorkflowVerifier.Verify(clickTest, [initial, Observed(click, invalid)]).Complete, "Matching blank or synthetic probe identity proved a native scoped click.");
            Check(!SavedWorkflowVerifier.Verify(keyTest, [initial, Observed(key, invalid)]).Complete, "Blank or synthetic probe receipt proved a selectorless native key.");
            before.Elements[0].RuntimeId = "runtime-control"; after.Elements[0].RuntimeId = "runtime-control";
            Check(!SavedWorkflowVerifier.Verify(clickTest, [initial, Observed(click, invalid, "runtime-control")]).Complete, "Invalid earlier identity in a hit chain was ignored.");
        }
        return Task.CompletedTask;
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => reply(request); }
    private sealed class Executor : IComputerActionExecutor
    { public int Calls { get; private set; } public Task ExecuteAsync(JsonElement action, CancellationToken cancellationToken = default) { Calls++; return Task.CompletedTask; } }
    private sealed class FixedAgent(ComputerAgentResult result) : IComputerAgent
    { public Task<ComputerAgentResult> RunAsync(string instructions, int maximumTurns = 30, IProgress<ComputerToolObservation>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(result); }
}
