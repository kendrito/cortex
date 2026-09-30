using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Testy.Core;

namespace Testy.Tests;

internal static class CompatibleSchemaChecks
{
    public static IEnumerable<(string Name, Func<Task> Execute)> All()
    {
        yield return ("compatible planning omits only the redundant remote array cap and preserves strict schema", RemotePayload);
        yield return ("compatible planning still accepts200 and rejects201 steps through authoritative host validation", LocalLimit);
    }
    private static ProviderSettings Settings() => new() { Kind = ProviderKind.Compatible, Model = "provider-neutral-fixture", Endpoint = "https://schema-fixture.invalid/v1/chat/completions", ApiKeyEnvironmentVariable = "", SupportsImages = false };
    private static string Plan(int count) => JsonSerializer.Serialize(new { name = "Boundary plan", intent = "Exact immutable readiness checks", steps = Enumerable.Range(0, count).Select(i => new { title = "Ready " + i, action = "assertText", selector = "id:Status", value = "Ready", timeoutMs = 1000, x = 0, y = 0 }) });
    private static HttpResponseMessage Reply(int count) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = Plan(count) } } } })) };
    private static async Task RemotePayload()
    {
        var calls = 0;
        using var client = new HttpClient(new ProjectHandler(async request =>
        {
            calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var format = body.RootElement.GetProperty("response_format").GetProperty("json_schema");
            var remote = JsonNode.Parse(format.GetProperty("schema").GetRawText());
            var expected = JsonNode.Parse(PlanCodec.Schema.GetRawText())!;
            Check(expected["properties"]!["steps"]!["maxItems"]!.GetValue<int>() == TestValidator.MaximumSteps, "Authoritative schema limit changed.");
            expected["properties"]!["steps"]!.AsObject().Remove("maxItems");
            Check(format.GetProperty("strict").GetBoolean() && JsonNode.DeepEquals(remote, expected), "Compatibility adaptation changed more than the remote steps.maxItems keyword.");
            return Reply(1);
        }));
        var result = await new CompatiblePlanner(Settings(), client).CreatePlanAsync(new PlanningRequest { Instructions = "Assert exact readiness" });
        Check(calls == 1 && result.Steps.Single().Action == StepAction.AssertText && result.Steps[0].Selector == "id:Status" && result.Steps[0].Value == "Ready" && result.Steps[0].TimeoutMs == 1000,
            "Adapted schema lost the exact returned assertion or made an extra request.");
        Check(PlanCodec.Schema.GetProperty("properties").GetProperty("steps").GetProperty("maxItems").GetInt32() == 200, "Remote schema mutation modified the shared host/Codex/Responses schema.");
    }
    private static async Task LocalLimit()
    {
        var count = 200; var calls = 0;
        using var client = new HttpClient(new ProjectHandler(_ => { calls++; return Task.FromResult(Reply(count)); }));
        var planner = new CompatiblePlanner(Settings(), client);
        Check((await planner.CreatePlanAsync(new PlanningRequest { Instructions = "Verify each readiness assertion in the supplied boundary plan." })).Steps.Count == 200, "Valid200-step plan was rejected.");
        count = 201; var rejected = false;
        try { await planner.CreatePlanAsync(new PlanningRequest { Instructions = "Verify each readiness assertion in the supplied boundary plan." }); }
        catch (InvalidDataException ex) { rejected = ex.Message.Contains("200", StringComparison.Ordinal); }
        Check(rejected && calls == 2, "Oversized compatible model output bypassed the host200-step limit or was silently retried.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
