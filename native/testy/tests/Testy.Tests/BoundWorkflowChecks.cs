using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Testy.Core;
using static Testy.Tests.AdvancedCoreChecks;

namespace Testy.Tests;

internal static class BoundWorkflowChecks
{
    public static IEnumerable<(string Name, Func<Task> Execute)> All()
    {
        yield return ("bound dispatch uses immutable saved definitions and fresh evidence", CanonicalDispatch);
        yield return ("bound dispatcher rejects unknown repeated reordered and rewritten steps before input", Boundaries);
        yield return ("failed or missing bound evidence cannot be reset into a mutation retry", FailedEvidence);
        yield return ("Codex governs each canonical saved step from updated observations", CodexBinding);
        yield return ("bound model completion cannot skip saved workflow coverage", EarlyCompletion);
        yield return ("insufficient bound model budgets stop before any target input", BudgetPreflight);
        yield return ("public AI preflight rejects impossible saved provider contracts without a driver", PublicPreflight);
        yield return ("suite progress JUnit cannot appear successful before terminal completion", SuiteProgressReports);
        yield return ("bound compatible loop retains paired action facts but only latest image and tree", CompatibleHistory);
        yield return ("bound Responses loop retains reasoning and paired results with latest observation only", ResponsesHistory);
        yield return ("native bound assertions execute immutable saved IDs without input", NativeBinding);
    }
    private static TestCase Test(params TestStep[] steps) => new() { Name = "Canonical workflow", Steps = steps.ToList() };
    private static string Id(TestStep step) => JsonSerializer.Serialize(new { stepId = step.Id });
    private static Task RejectAsync(Func<Task> action) => RejectAny(action, typeof(InvalidDataException), typeof(InvalidOperationException));
    private static async Task RejectAny(Func<Task> action, params Type[] allowed)
    {
        try { await action(); } catch (Exception ex) when (allowed.Contains(ex.GetType())) { return; }
        throw new InvalidOperationException("A prohibited bound action was accepted.");
    }
    private static async Task CanonicalDispatch()
    {
        using var f = new AdvancedFixture();
        var click = AdvancedFixture.Step(StepAction.Click); click.Selector = "query:{\"id\":\"Container\"}";
        var canonical = TestyJson.Clone(click);
        var assertion = AdvancedFixture.Step(StepAction.AssertText, "After input");
        var test = Test(click, assertion);
        var tools = new LocalComputerTools(f, f.Root, savedTest: test);
        click.Selector = "query:{\"id\":\"Container\",\"ancestor\":{\"type\":\"Group\"}}"; click.Action = StepAction.TypeText; click.Value = "rewritten";
        await RejectAsync(async () => await tools.DispatchAsync("perform_saved_step", Id(canonical)));
        Check(f.Inputs.Count == 0, "Bound input accepted before initial evidence.");
        var initial = await tools.DispatchAsync("observe_application", "{}");
        var observed = await tools.DispatchAsync("perform_saved_step", Id(canonical));
        Check(f.Inputs.Count == 1 && f.Inputs[0].Selector == canonical.Selector && f.Inputs[0].Action == StepAction.Click && f.Inputs[0].Value == canonical.Value, "Source edits changed the immutable dispatched step.");
        Check(observed.SavedStepId == canonical.Id && observed.Execution!.Steps[0].Step.Id == canonical.Id && observed.Snapshot!.Elements[0].Value == "After input", "Canonical identity or fresh post-action state was not recorded.");
        var checkedState = await tools.DispatchAsync("perform_saved_step", Id(assertion));
        var immutable = Test(canonical, assertion);
        Check(SavedWorkflowVerifier.Verify(immutable, [initial, observed, checkedState]).Complete, "Canonical dispatch failed strict workflow coverage.");
    }
    private static async Task Boundaries()
    {
        using var f = new AdvancedFixture();
        var first = AdvancedFixture.Step(StepAction.Click); var second = AdvancedFixture.Step(StepAction.AssertExists);
        var tools = new LocalComputerTools(f, f.Root, savedTest: Test(first, second));
        await tools.DispatchAsync("observe_application", "{}");
        foreach (var arguments in new[] { "{\"stepId\":\"missing\"}", Id(second), "{\"stepId\":\"" + first.Id + "\",\"selector\":\"id:Other\"}", "{\"stepId\":\"" + first.Id + "\",\"stepId\":\"" + first.Id + "\"}", "{\"stepId\":42}" })
            await RejectAsync(async () => await tools.DispatchAsync("perform_saved_step", arguments));
        await RejectAsync(async () => await tools.DispatchAsync("perform_ui_action", "{}"));
        Check(f.Inputs.Count == 0, "Invalid saved ID/schema reached driver.");
        await tools.DispatchAsync("perform_saved_step", Id(first));
        await RejectAsync(async () => await tools.DispatchAsync("perform_saved_step", Id(first)));
        await tools.DispatchAsync("perform_saved_step", Id(second));
        Check(f.Inputs.Count == 1, "Repeated saved input executed.");
    }
    private static async Task FailedEvidence()
    {
        foreach (var mode in new[] { "input", "capture" })
        {
            using var f = new AdvancedFixture();
            var first = AdvancedFixture.Step(StepAction.Click); var second = AdvancedFixture.Step(StepAction.Click);
            var tools = new LocalComputerTools(f, f.Root, savedTest: Test(first, second));
            await tools.DispatchAsync("observe_application", "{}");
            f.FailInput = mode == "input"; f.FailCapture = mode == "capture";
            Check((await tools.DispatchAsync("perform_saved_step", Id(first))).Execution!.Status == RunStatus.Failed, "Fixture failure did not stop bound execution.");
            f.FailInput = false; f.FailCapture = false;
            await tools.DispatchAsync("observe_application", "{}");
            await RejectAsync(async () => await tools.DispatchAsync("perform_saved_step", Id(first)));
            await RejectAsync(async () => await tools.DispatchAsync("perform_saved_step", Id(second)));
            Check(f.Inputs.Count == 1, "Fresh observation reset an uncertain/failed action into retryable state.");
        }
    }
    private static async Task CodexBinding()
    {
        using var f = new AdvancedFixture();
        var click = AdvancedFixture.Step(StepAction.Click); var assertion = AdvancedFixture.Step(StepAction.AssertText, "After input");
        var screenshot = new TestStep { Title = "Required screenshot", Action = StepAction.Screenshot };
        var test = Test(click, assertion, screenshot); var turns = 0;
        var agent = new CodexComputerAgent(new ProviderSettings(), f, Path.Combine(f.Root, "agent"), (prompt, schema, image, ct) =>
        {
            Check(!schema.GetProperty("properties").TryGetProperty("step", out _) && schema.GetProperty("properties").TryGetProperty("stepId", out _), "Bound Codex can rewrite raw steps.");
            Check(File.Exists(image), "Codex missing fresh image.");
            if (turns > 0) Check(prompt.Contains("After input", StringComparison.Ordinal) && f.Inputs.Count == 1, "Next decision did not observe previous action outcome.");
            var decision = turns < test.Steps.Count ? Decision(test.Steps[turns].Id) : Decision(""); turns++;
            return Task.FromResult(decision);
        }, test);
        var result = await agent.RunAsync("Execute the saved workflow.", 4);
        Check(result.Completed && result.ModelTurns == 4 && result.Observations.Count == 4 && SavedWorkflowVerifier.Verify(test, result.Observations).Complete, "Bound Codex failed one-decision-per-step coverage: " + result.Message);
        Check(result.Observations[^1].ToolName == "perform_saved_step" && result.Observations[^1].Execution!.Steps[0].Step.Id == screenshot.Id, "Mandatory initial observation replaced explicit saved screenshot.");
    }
    private static async Task EarlyCompletion()
    {
        using var f = new AdvancedFixture();
        var test = Test(AdvancedFixture.Step(StepAction.Click), AdvancedFixture.Step(StepAction.AssertExists));
        var result = await new AiTestRunner(f, new ProviderSettings(), f.Root, agentFactory: path => new CodexComputerAgent(new ProviderSettings(), f, path, (_, _, _, _) => Task.FromResult(Decision("")), test)).RunAsync(test);
        Check(result.Status == RunStatus.Failed && f.Inputs.Count == 0 && result.FailureDiagnostics.Any(d => d.Category == FailureCategory.WorkflowNotVerified), "Model completion replaced saved steps.");
    }
    private static async Task BudgetPreflight()
    {
        using var f = new AdvancedFixture();
        var test = Test(AdvancedFixture.Step(StepAction.Click), AdvancedFixture.Step(StepAction.AssertExists));
        var settings = new ProviderSettings { MaximumAgentTurns = 2 };
        await RejectAsync(async () => await new AiTestRunner(f, settings, f.Root).RunAsync(test));
        Check(f.Inputs.Count == 0 && f.Snapshots == 0, "Impossible saved workflow started execution.");
        var calls = 0;
        var agent = new CodexComputerAgent(settings, f, Path.Combine(f.Root, "direct"), (_, _, _, _) => { calls++; return Task.FromResult(Decision("")); }, test);
        await RejectAsync(async () => await agent.RunAsync("Run", 2));
        Check(calls == 0 && f.Snapshots == 0, "Direct bound agent spent a model call before budget rejection.");
    }
    private static Task PublicPreflight()
    {
        var test = Test(AdvancedFixture.Step(StepAction.Click), AdvancedFixture.Step(StepAction.AssertExists));
        AiTestRunner.ValidateExecution(test, new ProviderSettings { MaximumAgentTurns = 3 });
        Reject(() => AiTestRunner.ValidateExecution(test, new ProviderSettings { MaximumAgentTurns = 2 }));
        Reject(() => AiTestRunner.ValidateExecution(test, new ProviderSettings { Kind = ProviderKind.Offline }));
        Reject(() => AiTestRunner.ValidateExecution(test, new ProviderSettings { MaximumAgentTurns = AgentLimits.MaximumLocalTurns + 1 }));
        Reject(() => AiTestRunner.ValidateExecution(Test(AdvancedFixture.Step(StepAction.Click)), new ProviderSettings()));
        var native = new ProviderSettings { Kind = ProviderKind.OpenAI, NativeComputerUse = true };
        AiTestRunner.ValidateExecution(Test(AdvancedFixture.Step(StepAction.Expand), AdvancedFixture.Step(StepAction.AssertExists)), native);
        native.SupportsImages = false;
        Reject(() => AiTestRunner.ValidateExecution(test, native));
        native.NativeComputerUse = false;
        AiTestRunner.ValidateExecution(Test(AdvancedFixture.Step(StepAction.Expand), AdvancedFixture.Step(StepAction.AssertExists)), native);
        return Task.CompletedTask;
    }
    private static Task CompatibleHistory() => History(false);
    private static async Task SuiteProgressReports()
    {
        using var f = new AdvancedFixture();
        var count = 0;
        var runner = new SuiteRunner(async (test, directory, ct) =>
        {
            count++;
            var suiteDirectory = Path.GetDirectoryName(directory)!;
            var xml = XDocument.Load(Path.Combine(suiteDirectory, "suite-junit.xml"));
            Check((int?)xml.Root!.Attribute("tests") == 3 && (int?)xml.Root.Attribute("errors") == 1 && (int?)xml.Root.Attribute("failures") == 0, "In-progress suite can be interpreted as successful JUnit.");
            Check(xml.Descendants("error").Single().Attribute("type")!.Value == "SuiteIncomplete", "Missing explicit incomplete-suite error.");
            Check(xml.Descendants("property").Single(p => p.Attribute("name")!.Value == "testy.status").Attribute("value")!.Value == "Running", "Progress status missing from JUnit.");
            var progress = JsonSerializer.Deserialize<SuiteResult>(File.ReadAllText(Path.Combine(suiteDirectory, "suite.json")), TestyJson.Options)!;
            Check(progress.Entries[count - 1].Status == RunStatus.Running && (count != 1 || progress.Entries[1].Status == RunStatus.Pending), "Live report lost pending/running states.");
            return await new TestRunner(f, directory).RunAsync(test, cancellationToken: ct);
        }, f.Root, "Fixture replay");
        var suite = new TestSuite { Name = "Progress lifecycle", Tests = [Test(new TestStep { Action = StepAction.Screenshot }), Test(new TestStep { Action = StepAction.Screenshot })] };
        var result = await runner.RunAsync(suite);
        var final = XDocument.Load(Path.Combine(result.ArtifactDirectory, "suite-junit.xml"));
        Check(result.Status == RunStatus.Passed && count == 2 && (int?)final.Root!.Attribute("tests") == 2 && (int?)final.Root.Attribute("errors") == 0 && !final.Descendants("error").Any(), "Terminal success retained the incomplete error or wrong test count.");
    }
    private static Task ResponsesHistory() => History(true);
    private static async Task History(bool responses)
    {
        using var f = new AdvancedFixture();
        var click = AdvancedFixture.Step(StepAction.Click); var assertion = AdvancedFixture.Step(StepAction.AssertText, "After input");
        var test = Test(click, assertion); var turns = 0;
        using var handler = new Handler(async request =>
        {
            turns++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = body.RootElement;
            var offered = root.GetProperty("tools").EnumerateArray().Select(t => (responses ? t : t.GetProperty("function")).GetProperty("name").GetString()).ToArray();
            Check(offered.SequenceEqual(new[] { "observe_application", "perform_saved_step" }), "Bound model received arbitrary mutation tool.");
            var transcript = root.GetProperty(responses ? "input" : "messages");
            var observations = transcript.EnumerateArray().Where(m => m.TryGetProperty("role", out var role) && role.GetString() == "user" && m.GetProperty("content").ValueKind == JsonValueKind.Array).ToArray();
            Check(observations.Length == 1, "Historical screenshot/tree messages accumulated in context.");
            var observation = observations[0].GetProperty("content");
            Check(observation.EnumerateArray().Count(p => p.GetProperty("type").GetString() == (responses ? "input_image" : "image_url")) == 1, "Latest screenshot missing or duplicated.");
            if (turns > 1) Check(observation.GetRawText().Contains("After input", StringComparison.Ordinal), "Latest tree is stale.");
            var results = transcript.EnumerateArray().Where(m => responses ? m.TryGetProperty("type", out var type) && type.GetString() == "function_call_output" : m.TryGetProperty("role", out var role) && role.GetString() == "tool").ToArray();
            Check(results.Length == turns - 1, "Prior tool output pairs were discarded.");
            foreach (var item in results)
            {
                var output = item.GetProperty(responses ? "output" : "content").GetString()!;
                Check(!output.Contains("\"snapshot\"", StringComparison.OrdinalIgnoreCase) && output.Contains("savedStepId", StringComparison.Ordinal), "Historical result duplicated full tree or lost saved identity.");
            }
            if (turns == 3 && responses) Check(transcript.EnumerateArray().Count(m => m.TryGetProperty("type", out var type) && type.GetString() == "reasoning") == 2, "Responses reasoning continuation was discarded.");
            if (turns == 3) return responses ? Json(new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = "Verified saved workflow." } } } } }) : Json(new { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = "Verified saved workflow." } } } });
            var step = turns == 1 ? click : assertion;
            return responses ? Json(new { status = "completed", output = new object[] { new { type = "reasoning", id = "reason" + turns, encrypted_content = "fixture-encrypted" }, new { type = "function_call", call_id = "call" + turns, name = "perform_saved_step", arguments = Id(step) } } })
                : Json(new { choices = new[] { new { finish_reason = "tool_calls", message = new { role = "assistant", content = (string?)null, tool_calls = new[] { new { id = "call" + turns, type = "function", function = new { name = "perform_saved_step", arguments = Id(step) } } } } } } });
        });
        using var client = new HttpClient(handler);
        var settings = new ProviderSettings { Kind = responses ? ProviderKind.OpenAI : ProviderKind.Compatible, NativeComputerUse = false, Model = "fixture", Endpoint = "http://localhost/mock", ApiKeyEnvironmentVariable = f.KeyVariable };
        var result = await new ComputerUseAgent(settings, f, Path.Combine(f.Root, "agent"), client, test).RunAsync("Execute", 4);
        Check(result.Completed && result.ModelTurns == 3 && f.Inputs.Count == 1 && SavedWorkflowVerifier.Verify(test, result.Observations).Complete, "Bound HTTP workflow failed: " + result.Message);
        Check(result.Observations.All(o => o.Execution!.Steps[0].Snapshot!.Elements.Count == 1 && File.Exists(o.ScreenshotPath) && File.Exists(Path.Combine(o.Execution.ArtifactDirectory, "step-001.json"))), "Context compaction discarded local screenshot/tree artifacts.");
    }
    private static async Task NativeBinding()
    {
        using var f = new AdvancedFixture();
        var settings = new ProviderSettings { Kind = ProviderKind.OpenAI, Model = "fixture", Endpoint = "http://localhost/mock", ApiKeyEnvironmentVariable = f.KeyVariable };
        var assertion = AdvancedFixture.Step(StepAction.AssertItemAbsent, "{\"item\":{\"id\":\"Absent\"}}"); f.Lookup = new() { Status = ItemLookupStatus.Missing };
        var executor = new Executor(); var calls = 0;
        using var handler = new Handler(async request =>
        {
            calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Check(body.RootElement.GetProperty("tools")[1].GetProperty("name").GetString() == "verify_saved_assertion", "Native bound assertion did not use canonical ID schema.");
            return calls == 1 ? Json(new { status = "completed", output = new[] { new { type = "function_call", call_id = "assert1", name = "verify_saved_assertion", arguments = Id(assertion) } } }) : Json(new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = "Logical absence verified." } } } } });
        });
        using var client = new HttpClient(handler);
        var result = await new NativeComputerUseAgent(settings, f, Path.Combine(f.Root, "native"), executor, client, Test(assertion)).RunAsync("Verify saved absence", 3);
        Check(result.Completed && result.HasVerifiedAssertions && result.Observations[1].SavedStepId == assertion.Id && executor.Calls == 0 && f.Inputs.Count == 0, "Native canonical assertion changed UI or lost identity.");
    }
    private static string Decision(string stepId) => JsonSerializer.Serialize(new { done = stepId.Length == 0, explanation = stepId.Length == 0 ? "Finished." : "Execute next canonical step.", toolName = stepId.Length == 0 ? "observe_application" : "perform_saved_step", stepId });
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => reply(request); }
    private sealed class Executor : IComputerActionExecutor
    { public int Calls { get; private set; } public Task ExecuteAsync(JsonElement action, CancellationToken cancellationToken = default) { Calls++; return Task.CompletedTask; } }
}
