using System.Net;
using System.Text;
using System.Text.Json;
using Testy.Core;
using static Testy.Tests.AdvancedCoreChecks;

namespace Testy.Tests;

internal static class EnterpriseCoreChecks
{
    public static IEnumerable<(string Name, Func<Task> Execute)> All()
    {
        yield return ("grid contracts reject unbounded ambiguous and reflective payloads", StrictGridPayloads);
        yield return ("grid actions require explicit capabilities and execute each mutation once", GridCapabilities);
        yield return ("grid and diagnostic state remain typed read-only observations", TypedGridState);
        yield return ("native hybrid exposes separate canonical grid edit commit and cancel decisions", NativeGridProtocol);
        yield return ("legacy records default to strict execution and empty application diagnostics", AdditiveDefaults);
    }
    private static Task StrictGridPayloads()
    {
        foreach (var json in new[] { "{}", "{\"rowKey\":\"R\",\"columnKey\":\"C\"}", "{\"rowKey\":\"R\",\"columnKey\":\"C\",\"text\":7}", "{\"rowKey\":\"R\",\"rowKey\":\"Other\",\"columnKey\":\"C\",\"text\":\"7\"}", "{\"rowKey\":\" \",\"columnKey\":\"C\",\"text\":\"7\"}", "{\"rowKey\":\"R\",\"columnKey\":\"C\",\"text\":\"7\",\"propertyPath\":\"DataContext.Secret\"}" })
            Reject(() => AdvancedSteps.ParseGridEdit(json));
        Reject(() => AdvancedSteps.ParseGridEdit(JsonSerializer.Serialize(new { rowKey = new string('R',257), columnKey = "C", text = "" })));
        Reject(() => AdvancedSteps.ParseGridEdit(JsonSerializer.Serialize(new { rowKey = "R", columnKey = "C", text = new string('x',4097) })));
        Reject(() => AdvancedSteps.ParseGridRow("{\"rowKey\":\"R\",\"commitAll\":true}"));
        Reject(() => AdvancedSteps.ParseGridRow("{\"rowKey\":null}"));
        Check(AdvancedSteps.ParseGridEdit("{\"rowKey\":\"ORD-1\",\"columnKey\":\"Quantity\",\"text\":\"\"}") == new GridCellEditValue("ORD-1", "Quantity", ""), "Empty replacement or business identity was lost.");
        var escaped = new string('\n',4096);
        Check(AdvancedSteps.ParseGridEdit(JsonSerializer.Serialize(new {rowKey="ORD-1",columnKey="Note",text=escaped})).Text==escaped, "A valid4096-character value was rejected because its JSON escaping exceeded the legacy raw payload bound.");
        Check((int)StepAction.AssertItemAbsent == 19 && (int)StepAction.GridEditCell == 20, "Existing action identities changed.");
        var actions = PlanCodec.Schema.GetProperty("properties").GetProperty("steps").GetProperty("items").GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray().Select(v => v.GetString());
        foreach (var action in new[] { "gridEditCell", "gridCommitRow", "gridCancelRow" }) Check(actions.Contains(action), "Planner schema lost " + action);
        return Task.CompletedTask;
    }
    private static async Task GridCapabilities()
    {
        using var f = new AdvancedFixture();
        foreach (var action in new[] { StepAction.GridEditCell, StepAction.GridCommitRow, StepAction.GridCancelRow })
        {
            var step = AdvancedFixture.Step(action, action == StepAction.GridEditCell ? "{\"rowKey\":\"ORD-1\",\"columnKey\":\"Quantity\",\"text\":\"7\"}" : "{\"rowKey\":\"ORD-1\"}");
            var before = f.Inputs.Count;
            f.Element.Capabilities.Clear();
            var rejected = await f.Run(step);
            Check(rejected.Status == RunStatus.Failed && rejected.FailureDiagnostics[0].Category == FailureCategory.CapabilityUnavailable && f.Inputs.Count == before, "Unsupported grid operation reached input.");
            f.Element.Capabilities.Add(AdvancedSteps.Capability(action)); f.Element.IsOffscreen = true;
            Check((await f.Run(step)).Status == RunStatus.Passed && f.Inputs.Count == before + 1 && f.Inputs[^1].Value == step.Value, "Grid operation was skipped, repeated or rewritten.");
        }
        f.FailInput = true;
        var failed = await f.Run(AdvancedFixture.Step(StepAction.GridCancelRow, "{\"rowKey\":\"ORD-1\"}"));
        Check(failed.Status == RunStatus.Failed && failed.FailureDiagnostics[0].ActionOutcome == ActionOutcome.Unknown && f.Inputs.Count == 4, "Uncertain grid input was retried or reported complete.");
    }
    private static async Task TypedGridState()
    {
        using var f = new AdvancedFixture(); f.Element.IsEnabled = false; f.Element.IsOffscreen = true;
        foreach (var (property,value) in new (string,object)[] { ("wpf.gridIsEditing",true), ("wpf.gridEditingRowKey","ORD-1"), ("wpf.bindingStatus","Active"), ("wpf.commandCanExecute",false) })
        {
            f.Element.Properties[property] = new() { Status = UiPropertyStatus.Known, Value = JsonSerializer.SerializeToElement(value), Source = "opt-in WPF observation" };
            Check((await f.Run(AdvancedFixture.Step(StepAction.AssertProperty, JsonSerializer.Serialize(new {property,equals=value})))).Status == RunStatus.Passed, "Grid state was coerced or required mutation.");
        }
        Check(f.Inputs.Count == 0, "Grid property assertions changed UI.");
    }
    private static async Task NativeGridProtocol()
    {
        using var f = new AdvancedFixture(); f.Element.Capabilities.AddRange(["GridEdit", "GridCommit", "GridCancel"]);
        f.Element.Properties["wpf.gridIsEditing"] = new() { Status = UiPropertyStatus.Known, Value = JsonSerializer.SerializeToElement(false), Source = "mock grid" };
        var test = new TestCase { Name = "Canonical grid transactions", Steps = [
            AdvancedFixture.Step(StepAction.GridEditCell,"{\"rowKey\":\"ORD-1\",\"columnKey\":\"Quantity\",\"text\":\"7\"}"),
            AdvancedFixture.Step(StepAction.GridCommitRow,"{\"rowKey\":\"ORD-1\"}"),
            AdvancedFixture.Step(StepAction.GridCancelRow,"{\"rowKey\":\"ORD-1\"}"),
            AdvancedFixture.Step(StepAction.AssertProperty,"{\"property\":\"wpf.gridIsEditing\",\"equals\":false}")] };
        var turn = 0; var native = new NoNative();
        using var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Check(body.RootElement.GetProperty("tools").EnumerateArray().Any(t => t.TryGetProperty("name",out var name) && name.GetString() == "perform_saved_control_step"), "Grid workflow lost explicit native semantic tool.");
            if (turn > 0) Check(body.RootElement.GetProperty("input").GetRawText().Contains(test.Steps[turn-1].Id, StringComparison.Ordinal), "Next decision omitted the preceding immutable grid step.");
            if (turn == test.Steps.Count) { turn++; return Json(new {status="completed",output=new[]{new{type="message",content=new[]{new{type="output_text",text="Observed edit, commit, cancel and exact state."}}}}}); }
            var step = test.Steps[turn++];
            return Json(new{status="completed",output=new[]{new{type="function_call",call_id="grid-"+turn,name=TestValidator.IsAssertion(step.Action)?"verify_saved_assertion":"perform_saved_control_step",arguments=JsonSerializer.Serialize(new{stepId=step.Id})}}});
        });
        using var client = new HttpClient(handler);
        var settings = new ProviderSettings {Kind=ProviderKind.OpenAI,Model="fixture",Endpoint="http://localhost/mock",ApiKeyEnvironmentVariable=f.KeyVariable};
        var result = await new NativeComputerUseAgent(settings,f,f.Root,native,client,test).RunAsync("Execute",6);
        Check(result.Completed && result.ModelTurns == 5 && native.Inputs == 0 && f.Inputs.Count == 3 && SavedWorkflowVerifier.Verify(test,result.Observations).Complete, "Grid hybrid did not preserve one canonical operation per decision: " + result.Message);
    }
    private static Task AdditiveDefaults()
    {
        var step = JsonSerializer.Deserialize<TestStep>("{\"action\":\"click\",\"selector\":\"id:Save\"}",TestyJson.Options)!;
        var snapshot = JsonSerializer.Deserialize<UiSnapshot>("{}",TestyJson.Options)!;
        Check(step.SelectorAlternatives.Count == 0 && snapshot.ApplicationDiagnostics.Count == 0 && !snapshot.DiagnosticsTruncated, "Legacy data silently enabled recovery or invented diagnostics.");
        return Task.CompletedTask;
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json") };
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> send) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken) => send(request); }
    private sealed class NoNative : IComputerActionExecutor { public int Inputs; public Task ExecuteAsync(JsonElement action,CancellationToken cancellationToken=default) { Inputs++;return Task.CompletedTask; } }
}
