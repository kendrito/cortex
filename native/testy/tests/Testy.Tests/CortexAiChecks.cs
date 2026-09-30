using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Testy.Core;
using Testy.Cli.Mcp;

namespace Testy.Tests;

internal static class CortexAiChecks
{
    public static (string Name, Func<Task> Execute)[] All() =>
    [
        ("Cortex AI discovery selects only captured application ids", Selection),
        ("Cortex AI discovery preserves ambiguity without selecting an app", Ambiguity),
        ("Cortex AI discovery refuses invented or malformed target responses", Refusal),
        ("Cortex AI discovery refuses a reused or exited process identity", Identity),
        ("Cortex AI discovery uses its pinned model context and treats window titles as data", Model),
        ("Cortex AI discovery keeps saved sample executables eligible beside the Studio controller", SavedSamples),
        ("Cortex AI entry requires intent but no manual app selection", Catalog),
        ("Cortex AI refinement preserves unsaved authoring and checks the original revision", Authoring)
    ];
    private static readonly AiTargetCandidate[] Candidates =
    [new("pid-100","Customer Desk","Testy.TestLab",100,"C:\\Apps\\Testy.TestLab.exe"),new("pid-200","Order Desk","Testy.OrderLab",200,"C:\\Apps\\Testy.OrderLab.exe")];
    private static void Check(bool value,string message) { if (!value) throw new InvalidOperationException(message); }
    private static Task Selection()
    {
        var selected=AiTargetSelectionPlanner.Parse("{\"candidateId\":\"pid-100\",\"question\":\"\",\"reason\":\"The request names Customer Desk.\"}",Candidates);
        Check(selected.CandidateId=="pid-100" && selected.Question=="","Observed app selection changed."); return Task.CompletedTask;
    }
    private static Task Ambiguity()
    {
        var selected=AiTargetSelectionPlanner.Parse("{\"candidateId\":null,\"question\":\"Customer Desk or Order Desk?\",\"reason\":\"The request does not distinguish the two apps.\"}",Candidates);
        Check(selected.CandidateId is null && selected.Question.Length>0,"Ambiguous app was selected."); return Task.CompletedTask;
    }
    private static Task Refusal()
    {
        foreach (var json in new[]
        {
            "{\"candidateId\":\"pid-999\",\"question\":\"\",\"reason\":\"Invented process.\"}",
            "{\"candidateId\":\"C:\\\\Windows\\\\cmd.exe\",\"question\":\"\",\"reason\":\"Invented executable.\"}",
            "{\"candidateId\":null,\"question\":\"\",\"reason\":\"No match.\"}",
            "{\"candidateId\":\"pid-100\",\"question\":\"Which app?\",\"reason\":\"Ambiguous.\"}",
            "{\"candidateId\":100,\"question\":\"\",\"reason\":\"Wrong type.\"}",
            "{\"candidateId\":\"pid-100\",\"question\":\"\",\"reason\":\"Match.\",\"exe\":\"cmd.exe\"}"
        })
        {
            bool rejected=false; try { AiTargetSelectionPlanner.Parse(json,Candidates); } catch (InvalidDataException) { rejected=true; }
            Check(rejected,"Invalid target response was accepted: "+json);
        }
        return Task.CompletedTask;
    }
    private static Task Identity()
    {
        using var process=Process.GetCurrentProcess(); var ticks=process.StartTime.ToUniversalTime().Ticks;
        TestyMcpService.ValidateTargetIdentity(process.Id,ticks);
        foreach (var item in new[] {(process.Id,ticks+1),(int.MaxValue,ticks)})
        {
            bool rejected=false; try { TestyMcpService.ValidateTargetIdentity(item.Item1,item.Item2); } catch (McpToolException) { rejected=true; }
            Check(rejected,"Changed process identity was accepted.");
        }
        return Task.CompletedTask;
    }
    private sealed class ModelHandler : HttpMessageHandler
    {
        public string? Context; public string? Authorization; public string? Body;
        public string ResponseCandidateId { get; init; } = "pid-100";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Context=request.Headers.GetValues("X-Testy-Cortex-Context").Single(); Authorization=request.Headers.Authorization?.ToString(); Body=await request.Content!.ReadAsStringAsync(ct);
            var selection=JsonSerializer.Serialize(new { candidateId=ResponseCandidateId, question="", reason="The captured application matches the requested target." });
            return new(HttpStatusCode.OK) { Content=new StringContent(JsonSerializer.Serialize(new { choices=new[] { new { finish_reason="stop",message=new { content=selection } } } }),Encoding.UTF8,"application/json") };
        }
    }
    private static async Task Model()
    {
        var names=new[] {"TESTY_CORTEX_INTEGRATED",CortexModelBridge.UrlVariable,CortexModelBridge.TokenVariable,CortexModelBridge.ContextVariable};
        var previous=names.ToDictionary(name=>name,Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(names[0],"1"); Environment.SetEnvironmentVariable(names[1],"http://127.0.0.1:31999/v1/chat/completions"); Environment.SetEnvironmentVariable(names[2],"fake-native-target-test-token-123456"); Environment.SetEnvironmentVariable(names[3],null);
            using var scope=CortexModelBridge.Enter("ai-target-operation-123456"); using var handler=new ModelHandler(); using var client=new HttpClient(handler);
            var decision=await new AiTargetSelectionPlanner(new ProviderSettings(),client).SelectAsync("Test Customer Desk","C:\\Project",null,Candidates,CancellationToken.None);
            Check(decision.CandidateId=="pid-100" && handler.Context=="ai-target-operation-123456","Selection used a different operation.");
            Check(handler.Authorization=="Bearer fake-native-target-test-token-123456","Bridge authentication missing.");
            using var body=JsonDocument.Parse(handler.Body!); Check(body.RootElement.GetProperty("model").GetString()=="cortex","Workspace selected a provider model.");
            Check(body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("name").GetString()=="testy_target_selection","Target response schema changed.");
            Check(body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("untrusted evidence"),"App titles were not identified as data.");
            var payload=body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
            Check(!payload.Contains("fake-native-target-test-token") && payload.Contains("pid-100"),"Candidate prompt exposed credentials or omitted evidence.");
        }
        finally { foreach (var item in previous) Environment.SetEnvironmentVariable(item.Key,item.Value); }
    }
    private static async Task SavedSamples()
    {
        var names=new[] {"TESTY_CORTEX_INTEGRATED",CortexModelBridge.UrlVariable,CortexModelBridge.TokenVariable,CortexModelBridge.ContextVariable};
        var previous=names.ToDictionary(name=>name,Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(names[0],"1"); Environment.SetEnvironmentVariable(names[1],"http://127.0.0.1:31999/v1/chat/completions"); Environment.SetEnvironmentVariable(names[2],"fake-native-target-test-token-123456"); Environment.SetEnvironmentVariable(names[3],null);
            using var scope=CortexModelBridge.Enter("saved-sample-operation-123456");
            foreach (var sample in new[] {"Testy.TestLab","Testy.OrderLab","Testy.WpfLab"})
            {
                var savedTarget="C:\\Apps\\"+sample+".exe";
                AiTargetCandidate[] candidates=[new("pid-200","Testy","Testy.Studio",200,"C:\\Apps\\Testy.Studio.exe"),new("exe-1",sample,sample,null,savedTarget)];
                const string instructions="Assert without interacting with the application that the ready-state label exactly reads \"Ready for a new customer.\"";
                using var handler=new ModelHandler { ResponseCandidateId="exe-1" }; using var client=new HttpClient(handler);
                var decision=await new AiTargetSelectionPlanner(new ProviderSettings(),client).SelectAsync(instructions,"C:\\Project",savedTarget,candidates,CancellationToken.None);
                Check(decision.CandidateId=="exe-1" && decision.Question=="","The captured saved executable was refused because no process was running.");
                using var body=JsonDocument.Parse(handler.Body!);
                var prompt=body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
                Check(prompt.Contains("Testy.Studio") && prompt.Contains("Cortex user interface"),"Controller exclusion does not identify the controller interfaces.");
                Check(prompt.Contains("Testy.TestLab, Testy.OrderLab and Testy.WpfLab are sample applications under test") && prompt.Contains("eligible"),"The prompt excludes or fails to distinguish legitimate Testy sample targets.");
                Check(!prompt.Contains("Do not choose Testy,"),"The broad Testy brand exclusion returned.");
                Check(prompt.Contains("savedTargetPath") && prompt.Contains("evidence") && prompt.Contains("pid is null"),"Saved executable evidence is still described as only a running process.");
                Check(prompt.Contains("untrusted evidence") && prompt.Contains("exact candidate id") && prompt.Contains("candidateId null"),"Sample guidance weakened the untrusted-data, captured-id or ambiguity rules.");
                using var payload=JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
                Check(payload.RootElement.GetProperty("instructions").GetString()==instructions && payload.RootElement.GetProperty("savedTargetPath").GetString()==savedTarget,"The request lost the saved target or changed the user's intent.");
                var captured=payload.RootElement.GetProperty("candidates").Deserialize<AiTargetCandidate[]>(TestyJson.Options)!;
                Check(captured.SequenceEqual(candidates),"The controller competitor or captured executable identity was rewritten or filtered.");
            }
        }
        finally { foreach (var item in previous) Environment.SetEnvironmentVariable(item.Key,item.Value); }
    }
    private static Task Catalog()
    {
        var tools=CortexMcpTools.Define(null); var ai=tools.Single(t=>t.Name=="ai_test");
        Check(ai.UsesModel && ai.InputSchema["required"]!.AsArray().Select(n=>n!.GetValue<string>()).SequenceEqual(["instructions"]),"AI testing requires manual target selection.");
        Check(!ai.InputSchema["properties"]!.AsObject().ContainsKey("workspace"),"Trusted workspace metadata became a model-controlled argument.");
        Check(!tools.Single(t=>t.Name=="launch_sample").UsesModel,"Sample launch incorrectly needs model access.");
        foreach (var name in new[] {"draft_test","refine_test"})
        {
            var tool=tools.Single(t=>t.Name==name);
            Check(!tool.InputSchema["required"]!.AsArray().Any(n=>n!.GetValue<string>()=="pid"),"Authoring still requires manual app selection.");
            Check(tool.InputSchema["properties"]!.AsObject().ContainsKey("exe"),"An executable clarification cannot be applied.");
        }
        return Task.CompletedTask;
    }
    private static Task Authoring()
    {
        var saved=new TestCase { Name="Saved test",Steps=[new() { Id="status",Title="Saved assertion",Action=StepAction.AssertText,Selector="id:StatusMessage",Value="Ready" }] };
        var before=JsonSerializer.Serialize(saved,TestyJson.Options); var revision=WorkspaceStore.Revision(saved);
        var inline=JsonSerializer.SerializeToElement(new { name="Unsaved name",intent="Unsaved intent",category="Edited",targetPath="",steps=new[] {new {id="status",title="Unsaved assertion",action="assertText",selector="id:StatusMessage",value="Changed locally"}} });
        var draft=TestyMcpService.ApplyAuthoringDraft(saved,inline,revision);
        Check(draft.Id==saved.Id && draft.Name=="Unsaved name" && draft.Steps.Single().Value=="Changed locally","Refinement omitted current authored edits.");
        Check(JsonSerializer.Serialize(saved,TestyJson.Options)==before,"Refinement mutated the saved source.");
        foreach (var expected in new string?[] {null,new('0',64)})
        {
            bool refused=false; try { TestyMcpService.ApplyAuthoringDraft(saved,inline,expected); } catch (McpToolException) { refused=true; }
            Check(refused,"Inline saved-test edits accepted a missing or stale revision.");
        }
        var empty=JsonSerializer.SerializeToElement(new {name="New draft",steps=Array.Empty<object>()});
        Check(TestyMcpService.ApplyAuthoringDraft(null,empty,null).Steps.Count==0,"An unsaved empty draft could not be authored with AI.");
        return Task.CompletedTask;
    }
}
