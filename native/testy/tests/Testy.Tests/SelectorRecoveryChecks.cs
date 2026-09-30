using System.Net;
using System.Text;
using System.Text.Json;
using Testy.Core;
using static Testy.Tests.AdvancedCoreChecks;

namespace Testy.Tests;

internal static class SelectorRecoveryChecks
{
    public static IEnumerable<(string Name, Func<Task> Execute)> All()
    {
        yield return ("selector alternatives are explicit bounded identities and never weaken absence checks", Contracts);
        yield return ("strict replay ignores authored alternatives and preserves requested definition", StrictDefault);
        yield return ("approved recovery records immutable expectations and engine identity evidence", AuditedRecovery);
        yield return ("recovery rejects existing ambiguous disabled or changed target identities before input", RejectedTargets);
        yield return ("recovery absence proof respects complete scoped collection boundaries", ScopedCoverage);
        yield return ("recovery fails when identity changes on the driver's final guard", FinalGuard);
        yield return ("read-only recovered assertions cannot change or weaken expected values", Assertions);
        yield return ("failed or uncertain recovered mutations are never repeated in a bound session", NoRetry);
        yield return ("Codex chooses each explicit approved recovery step from fresh evidence", CodexProtocol);
        yield return ("compatible recovery feedback exposes exact approved identity and fresh evidence", ()=>HttpProtocol(false));
        yield return ("Responses recovery feedback exposes exact approved identity and fresh evidence", ()=>HttpProtocol(true));
        yield return ("bound recovery rejects unknown reordered and rewritten proposals before target access", ProposalBoundaries);
        yield return ("AI drafts preserve authored alternatives only for uniquely unchanged saved contracts", DraftAlternativePreservation);
        yield return ("native recovery is rejected before model target or executor use", NativeUnsupported);
        yield return ("bounded classified application diagnostics survive model context and reports", DiagnosticContext);
    }
    private static TestCase Case(params TestStep[] steps) => new() {Name="Approved recovery fixture",Steps=steps.ToList()};
    private static TestStep Step(StepAction action=StepAction.Click,string value="") => new()
    {
        Id="save-record",Title="Save the record",Action=action,Selector="query:{\"id\":\"SaveV1\",\"ancestor\":{\"id\":\"RecoveryPanel\"}}",Value=value,TimeoutMs=100,
        SelectorAlternatives=[new(){Id="new-save",Selector="query:{\"id\":\"SaveV2\",\"ancestor\":{\"id\":\"RecoveryPanel\"}}",ExpectedControlType="Button",ExpectedAutomationId="SaveV2",ExpectedName="Save record"}]
    };
    private static TestStep Assertion() => new(){Id="verify-saved",Title="Verify saved",Action=StepAction.AssertText,Selector="id:Status",Value="Saved",TimeoutMs=100};
    private static Task Contracts()
    {
        var step=Step(); TestValidator.ValidateStep(step);
        foreach(var mutate in new Action<TestStep>[] {s=>s.SelectorAlternatives[0].ExpectedControlType="",s=>{s.SelectorAlternatives[0].ExpectedAutomationId="";s.SelectorAlternatives[0].ExpectedName="";},s=>s.SelectorAlternatives[0].Selector=s.Selector,s=>s.SelectorAlternatives.Add(TestyJson.Clone(s.SelectorAlternatives[0])),s=>s.Action=StepAction.AssertNotExists,s=>s.Action=StepAction.AssertItemAbsent,s=>s.Action=StepAction.Screenshot})
        {var invalid=Step();mutate(invalid);Reject(()=>TestValidator.ValidateStep(invalid));}
        return Task.CompletedTask;
    }
    private static async Task StrictDefault()
    {
        using var f=new RecoveryFixture();var test=Case(Step());
        var result=await new TestRunner(f,f.Root).RunAsync(test);
        Check(result.Status==RunStatus.Failed&&f.Inputs.Count==0&&result.Steps[0].SelectorRecovery is null,"Strict execution selected an alternative automatically.");
        var requested=JsonSerializer.Deserialize<TestCase>(File.ReadAllText(Path.Combine(result.ArtifactDirectory,"requested-test.json")),TestyJson.Options)!;
        Check(JsonSerializer.Serialize(requested,TestyJson.Options)==JsonSerializer.Serialize(test,TestyJson.Options),"Replay request provenance changed the frozen test.");
    }
    private static async Task AuditedRecovery()
    {
        using var f=new RecoveryFixture();var step=Step();var original=TestyJson.Clone(step);
        var result=await new TestRunner(f,f.Root).RunWithSelectorAlternativeAsync(Case(step),step.Id,"new-save");
        var recorded=result.Steps[0];
        Check(result.Status==RunStatus.Passed&&f.Inputs.Count==1&&f.Inputs[0].Selector==step.SelectorAlternatives[0].Selector,"Approved recovery did not dispatch exactly once on its explicit alternative: "+result.Summary);
        Check(JsonSerializer.Serialize(recorded.Step,TestyJson.Options)==JsonSerializer.Serialize(original,TestyJson.Options),"Recovery rewrote the canonical saved step.");
        SelectorRecovery.ValidateEvidence(original,recorded);
        Check(recorded.SelectorRecovery!.EngineVerified&&recorded.SelectorRecovery.ActionOutcome==ActionOutcome.Completed&&File.Exists(recorded.SelectorRecovery.GuardSnapshotPath),"Recovery lacks engine proof.");
        var observation=new ComputerToolObservation{ToolName="perform_saved_step_alternate",SavedStepId=step.Id,Execution=result,Snapshot=recorded.Snapshot};
        Check(SavedWorkflowVerifier.Verify(Case(step),[observation]).Complete,"Verified alternate was not attributed to the immutable saved step.");
        recorded.SelectorRecovery.Guard.ExpectedRuntimeId="forged";
        Check(!SavedWorkflowVerifier.Verify(Case(step),[observation]).Complete,"Forged recovery runtime evidence received workflow credit.");
        Check(File.ReadAllText(Path.Combine(result.ArtifactDirectory,"report.html")).Contains("Explicit selector alternative",StringComparison.Ordinal),"Human report hid the recovery.");
    }
    private static async Task RejectedTargets()
    {
        foreach(var mode in new[]{"existing","disabled-original","ambiguous-original","duplicate-alternative","wrong-role","wrong-name","password","truncated"})
        {
            using var f=new RecoveryFixture(); var step=Step();
            if(mode is "existing" or "disabled-original" or "ambiguous-original") {var original=TestyJson.Clone(f.Replacement);original.AutomationId="SaveV1";original.IsEnabled=mode!="disabled-original";f.Elements.Insert(2,original);if(mode=="ambiguous-original")f.Elements.Insert(2,TestyJson.Clone(original));}
            if(mode=="duplicate-alternative")f.Elements.Insert(2,TestyJson.Clone(f.Replacement));
            if(mode=="wrong-role")f.Replacement.ControlType="Text";
            if(mode=="wrong-name")f.Replacement.Name="Delete record";
            if(mode=="password")f.Replacement.IsPassword=true;
            if(mode=="truncated")f.Truncated=true;
            var run=await new TestRunner(f,f.Root).RunWithSelectorAlternativeAsync(Case(step),step.Id,"new-save");
            Check(run.Status==RunStatus.Failed&&f.Inputs.Count==0&&run.FailureDiagnostics[0].ActionOutcome==ActionOutcome.NotDispatched&&run.Steps[0].SelectorRecovery?.AlternativeId=="new-save","Rejected recovery touched target or lost trace: "+mode);
        }
    }
    private static Task ScopedCoverage()
    {
        using var f=new RecoveryFixture();f.Elements.Add(new(){AutomationId="OtherGrid",RuntimeId="other-grid",ControlType="DataGrid",Depth=1,ChildCoverage=ChildCoverage.RealizedOnly});
        var scoped=Step();SelectorRecovery.Resolve(scoped,"new-save",f.Snapshot());
        var global=Step();global.Selector="id:SaveV1";
        Throws(()=>SelectorRecovery.Resolve(global,"new-save",f.Snapshot()));
        f.Elements[1].ChildCoverage=ChildCoverage.Unknown;
        Throws(()=>SelectorRecovery.Resolve(scoped,"new-save",f.Snapshot()));
        Check(f.Inputs.Count==0,"Coverage inspection mutated UI.");return Task.CompletedTask;
    }
    private static async Task FinalGuard()
    {
        using var f=new RecoveryFixture();f.BeforeExecuteGuard=()=>f.Replacement.RuntimeId="recycled-after-model-observation";
        var step=Step();var run=await new TestRunner(f,f.Root).RunWithSelectorAlternativeAsync(Case(step),step.Id,"new-save");
        Check(run.Status==RunStatus.Failed&&f.Inputs.Count==0&&run.FailureDiagnostics[0].Category==FailureCategory.EvidenceUnavailable&&run.FailureDiagnostics[0].ActionOutcome==ActionOutcome.NotDispatched,"Final worker identity replacement allowed input or invented uncertain dispatch.");
    }
    private static async Task Assertions()
    {
        using var f=new RecoveryFixture();var step=Step(StepAction.AssertText,"expected");f.Replacement.Value="wrong";
        var failed=await new TestRunner(f,f.Root).RunWithSelectorAlternativeAsync(Case(step),step.Id,"new-save");
        Check(failed.Status==RunStatus.Failed&&failed.FailureDiagnostics.Any(d=>d.Category==FailureCategory.AssertionMismatch&&d.Expected=="expected"&&d.Actual=="wrong")&&f.Inputs.Count==0,"Recovery weakened the saved assertion.");
        f.Replacement.Value="expected";var passed=await new TestRunner(f,f.Root).RunWithSelectorAlternativeAsync(Case(step),step.Id,"new-save");
        Check(passed.Status==RunStatus.Passed&&f.Inputs.Count==0&&passed.Steps[0].SelectorRecovery!.ActionOutcome==ActionOutcome.NotDispatched,"Read-only recovery mutated input.");
        SelectorRecovery.ValidateEvidence(step,passed.Steps[0]);
    }
    private static async Task NoRetry()
    {
        using var f=new RecoveryFixture();f.FailAfterInput=true;var step=Step();var tools=new LocalComputerTools(f,f.Root,savedTest:Case(step));
        await tools.DispatchAsync("observe_application","{}");var args=JsonSerializer.Serialize(new{stepId=step.Id,alternativeId="new-save"});
        var result=await tools.DispatchAsync("perform_saved_step_alternate",args);
        Check(result.Execution!.Status==RunStatus.Failed&&result.Execution.FailureDiagnostics[0].ActionOutcome==ActionOutcome.Unknown&&result.Execution.Steps[0].SelectorRecovery!.ActionOutcome==ActionOutcome.Unknown,"Uncertain recovered input lost its outcome.");
        await tools.DispatchAsync("observe_application","{}");
        await ThrowsAsync(()=>tools.DispatchAsync("perform_saved_step_alternate",args));
        Check(f.Inputs.Count==1,"Fresh observation permitted repetition after uncertain dispatch.");
    }
    private static async Task CodexProtocol()
    {
        using var f=new RecoveryFixture();var step=Step();var test=Case(step,Assertion());var turn=0;
        var agent=new CodexComputerAgent(new ProviderSettings{SupportsImages=false},f,f.Root,(prompt,schema,image,ct)=>
        {
            Check(image.Length==0&&schema.GetProperty("properties").TryGetProperty("alternativeId",out _),"Opt-in Codex recovery schema/image preference missing.");
            if(turn==1)Check(prompt.Contains("EngineVerified",StringComparison.OrdinalIgnoreCase)&&prompt.Contains("SaveV2",StringComparison.Ordinal),"Codex next decision lost guarded recovery evidence.");
            var current=turn++;
            return Task.FromResult(JsonSerializer.Serialize(new{done=current==2,explanation="Inspect fresh evidence then choose next canonical action.",toolName=current==0?"perform_saved_step_alternate":current==1?"perform_saved_step":"observe_application",stepId=current==0?step.Id:current==1?test.Steps[1].Id:"",alternativeId=current==0?"new-save":""}));
        },test);
        var result=await agent.RunAsync("Execute",4);
        Check(result.Completed&&result.ModelTurns==3&&f.Inputs.Count==1&&SavedWorkflowVerifier.Verify(test,result.Observations).Complete,"Codex did not govern exact recovery and assertion: "+result.Message);
    }
    private static async Task HttpProtocol(bool responses)
    {
        using var f=new RecoveryFixture();var test=Case(Step(),Assertion());var turn=0;
        using var handler=new Handler(async request=>
        {
            using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync());var root=body.RootElement;
            Check(root.GetProperty("tools").EnumerateArray().Any(t=>(responses?t:t.GetProperty("function")).GetProperty("name").GetString()=="perform_saved_step_alternate"),"HTTP model was not offered explicit approved recovery.");
            var transcript=root.GetProperty(responses?"input":"messages").GetRawText();
            if(turn==1)Check(transcript.Contains("selectorRecovery",StringComparison.Ordinal)&&transcript.Contains("new-save",StringComparison.Ordinal),"Next model decision omitted recovery evidence.");
            var current=turn++;
            if(current==2)return responses?Json(new{status="completed",output=new[]{new{type="message",content=new[]{new{type="output_text",text="Observed saved result."}}}}}):Json(new{choices=new[]{new{finish_reason="stop",message=new{role="assistant",content="Observed saved result."}}}});
            var name=current==0?"perform_saved_step_alternate":"perform_saved_step";
            var args=current==0?JsonSerializer.Serialize(new{stepId=test.Steps[0].Id,alternativeId="new-save"}):JsonSerializer.Serialize(new{stepId=test.Steps[1].Id});
            return responses?Json(new{status="completed",output=new[]{new{type="function_call",call_id="call-"+current,name,arguments=args}}}):Json(new{choices=new[]{new{finish_reason="tool_calls",message=new{role="assistant",content=(string?)null,tool_calls=new[]{new{id="call-"+current,type="function",function=new{name,arguments=args}}}}}}});
        });
        using var client=new HttpClient(handler);
        var settings=new ProviderSettings{Kind=responses?ProviderKind.OpenAI:ProviderKind.Compatible,NativeComputerUse=false,Model="fixture",Endpoint="http://localhost/mock",ApiKeyEnvironmentVariable=f.KeyVariable,SupportsImages=false,MaximumAgentTurns=4};
        var runner=new AiTestRunner(f,settings,f.Root,agentFactory:path=>new ComputerUseAgent(settings,f,path,client,test));
        var run=await runner.RunAsync(test);
        Check(run.Status==RunStatus.Passed&&turn==3&&f.Inputs.Count==1&&run.Steps.Any(s=>s.SelectorRecovery?.EngineVerified==true),"Compatible AI recovery did not pass immutable workflow: "+run.Summary+run.AiAnalysis);
        foreach(var recovered in run.Steps.Where(s=>s.SelectorRecovery is not null))SelectorRecovery.ValidateEvidence(test.Steps[0],recovered);
    }
    private static async Task ProposalBoundaries()
    {
        using var f=new RecoveryFixture();var test=Case(Step(),Assertion());var tools=new LocalComputerTools(f,f.Root,savedTest:test);
        await tools.DispatchAsync("observe_application","{}");var before=f.Snapshots;
        foreach(var request in new[]{"{\"stepId\":\"save-record\",\"alternativeId\":\"invented\"}","{\"stepId\":\"verify-saved\",\"alternativeId\":\"new-save\"}","{\"stepId\":\"save-record\",\"alternativeId\":\"new-save\",\"value\":\"weakened\"}"})
            await ThrowsAsync(()=>tools.DispatchAsync("perform_saved_step_alternate",request));
        Check(f.Inputs.Count==0&&f.Snapshots==before,"Unapproved recovery proposal touched the target before validation.");
    }
    private static Task NativeUnsupported()
    {
        using var f=new RecoveryFixture();var settings=new ProviderSettings{Kind=ProviderKind.OpenAI,NativeComputerUse=true,Model="fixture",MaximumAgentTurns=4};var test=Case(Step(),Assertion());
        Reject(()=>AiTestRunner.ValidateExecution(test,settings));
        Reject(()=>new NativeComputerUseAgent(settings,f,f.Root,new NoNative(),savedTest:test));
        Check(f.Snapshots==0&&f.Inputs.Count==0,"Native rejected recovery after touching target.");return Task.CompletedTask;
    }
    private static Task DraftAlternativePreservation()
    {
        var step=Step();var saved=Case(step);
        string Plan(string selector,int repeats=1)=>JsonSerializer.Serialize(new{name="Reviewed draft",intent="Preserve expected action",steps=Enumerable.Range(0,repeats).Select(_=>new{title="Updated human-readable title",action="click",selector,value="",timeoutMs=100,x=0,y=0})});
        var unchanged=PlanCodec.Parse(Plan(step.Selector),new(){ExistingTest=saved});
        Check(unchanged.Steps[0].Id==step.Id&&unchanged.Steps[0].SelectorAlternatives.Single().Id=="new-save", "An unchanged acceptance/action contract lost its authored recovery permissions.");
        var changed=PlanCodec.Parse(Plan("id:Other"),new(){ExistingTest=saved});
        Check(changed.Steps[0].SelectorAlternatives.Count==0,"A changed selector silently inherited equivalence authorization.");
        var duplicate=PlanCodec.Parse(Plan(step.Selector,2),new(){ExistingTest=saved});
        Check(duplicate.Steps.All(s=>s.SelectorAlternatives.Count==0),"A duplicated planned operation inherited recovery authorization ambiguously.");
        var second=TestyJson.Clone(step);second.Id="another-previous-step";saved.Steps.Add(second);
        Check(PlanCodec.Parse(Plan(step.Selector),new(){ExistingTest=saved}).Steps[0].SelectorAlternatives.Count==0,"Ambiguous existing step contracts lent arbitrary recovery permissions.");
        return Task.CompletedTask;
    }
    private static async Task DiagnosticContext()
    {
        using var f=new RecoveryFixture();
        for(var i=0;i<70;i++)f.Diagnostics.Add(new(){Status=UiPropertyStatus.Known,Source="WPF classified validation",Kind="Validation",Message=new string('x',600)});
        f.Diagnostics.Add(new(){Status=UiPropertyStatus.Redacted,Source="WPF password binding",Kind="Binding",Message="secret-diagnostic-content"});
        var run=await new TestRunner(f,f.Root).RunAsync(Case(new TestStep{Action=StepAction.Screenshot}));
        var snapshot=run.Steps[0].Snapshot!;
        Check(snapshot.DiagnosticsTruncated&&snapshot.ApplicationDiagnostics.Count==64&&snapshot.ApplicationDiagnostics.All(d=>d.Message.Length<=512)&&snapshot.ApplicationDiagnostics[^1].Message=="","Bounded diagnostic context leaked redacted or oversized text.");
        var json=File.ReadAllText(Path.Combine(run.ArtifactDirectory,"run.json"));
        Check(!json.Contains("secret-diagnostic-content",StringComparison.Ordinal)&&File.ReadAllText(Path.Combine(run.ArtifactDirectory,"report.html")).Contains("Observed application diagnostics",StringComparison.Ordinal),"Saved reports omitted bounded diagnostics or leaked redacted data.");
    }
    private static void Throws(Action action){try{action();}catch(InvalidOperationException){return;}throw new InvalidOperationException("Unsafe recovery was accepted.");}
    private static async Task ThrowsAsync(Func<Task> action){try{await action();}catch(InvalidOperationException){return;}catch(InvalidDataException){return;}throw new InvalidOperationException("Repeated recovered action was accepted.");}
    private static HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json")};
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> send):HttpMessageHandler{protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>send(request);}
    private sealed class NoNative:IComputerActionExecutor{public Task ExecuteAsync(JsonElement action,CancellationToken cancellationToken=default)=>throw new InvalidOperationException("Native input must not execute.");}
}

internal sealed class RecoveryFixture:ITargetDriver,IGuardedTargetDriver
{
    public string Root{get;}=Path.Combine(Path.GetTempPath(),"Testy-recovery-"+Guid.NewGuid().ToString("N"));
    public string KeyVariable{get;}="TESTY_RECOVERY_TEST_KEY_"+Guid.NewGuid().ToString("N");
    public TargetInfo? Target{get;}=new(){ProcessId=42,Title="Recovery fixture"};
    public List<UiElementInfo> Elements{get;}=[];
    public List<UiApplicationDiagnostic> Diagnostics{get;}=[];
    public UiElementInfo Replacement{get;}=new(){AutomationId="SaveV2",Selector="id:SaveV2",RuntimeId="save-runtime",Name="Save record",ControlType="Button",Depth=2,IsEnabled=true};
    public List<TestStep> Inputs{get;}=[];
    public int Snapshots{get;private set;}
    public bool Truncated{get;set;}
    public bool FailAfterInput{get;set;}
    public Action? BeforeExecuteGuard{get;set;}
    public RecoveryFixture(){Directory.CreateDirectory(Root);Environment.SetEnvironmentVariable(KeyVariable,"fixture-not-a-secret");Elements.AddRange([new(){AutomationId="Window",RuntimeId="window",ControlType="Window",Depth=0,IsEnabled=true},new(){AutomationId="RecoveryPanel",RuntimeId="panel",ControlType="Pane",Depth=1,IsEnabled=true},Replacement,new(){AutomationId="Status",RuntimeId="status",Selector="id:Status",ControlType="Text",Depth=1,Name="Pending",Value="Pending",IsEnabled=true}]);}
    public UiSnapshot Snapshot(){Snapshots++;return new(){Target=TestyJson.Clone(Target!),Elements=TestyJson.Clone(Elements),ApplicationDiagnostics=TestyJson.Clone(Diagnostics),IsTruncated=Truncated,Source="Guarded fixture",ScreenshotBounds=new(){Width=1,Height=1}};}
    public Task<UiSnapshot> SnapshotAsync(CancellationToken cancellationToken=default)=>Task.FromResult(Snapshot());
    public Task<UiSnapshot> SnapshotGuardedAsync(UiExecutionGuard guard,CancellationToken cancellationToken=default){var snapshot=Snapshot();try{SelectorRecovery.ValidateGuard(snapshot,guard);}catch(Exception ex){throw new StepDiagnosticException(FailureDiagnostics.Create(FailureCategory.EvidenceUnavailable,ex.Message),snapshot,ex);}return Task.FromResult(snapshot);}
    public async Task<UiSnapshot> ExecuteGuardedAsync(TestStep step,UiExecutionGuard guard,CancellationToken cancellationToken=default){BeforeExecuteGuard?.Invoke();var snapshot=await SnapshotGuardedAsync(guard,cancellationToken);await ExecuteAsync(step,cancellationToken);return snapshot;}
    public Task ExecuteAsync(TestStep step,CancellationToken cancellationToken=default){Inputs.Add(TestyJson.Clone(step));if(FailAfterInput)throw new IOException("Input may have been partially delivered.");Elements.Single(e=>e.AutomationId=="Status").Value="Saved";return Task.CompletedTask;}
    public Task<string> CaptureAsync(string filePath,CancellationToken cancellationToken=default){File.WriteAllBytes(filePath,Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jU1sAAAAASUVORK5CYII="));return Task.FromResult(filePath);}
    public Task<IReadOnlyList<TargetInfo>> GetTargetsAsync(CancellationToken cancellationToken=default)=>Task.FromResult<IReadOnlyList<TargetInfo>>([Target!]);
    public Task AttachAsync(int processId,CancellationToken cancellationToken=default)=>Task.CompletedTask;
    public void Dispose(){Environment.SetEnvironmentVariable(KeyVariable,null);if(Directory.Exists(Root)&&Path.GetFileName(Root).StartsWith("Testy-recovery-",StringComparison.Ordinal)&&Path.GetDirectoryName(Root)==Path.TrimEndingDirectorySeparator(Path.GetTempPath()))Directory.Delete(Root,true);}
}
