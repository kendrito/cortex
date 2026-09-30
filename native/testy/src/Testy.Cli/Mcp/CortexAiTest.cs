using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Testy.Core;
using Testy.Windows;

namespace Testy.Cli.Mcp;

internal sealed partial class TestyMcpService
{
    private sealed record CapturedTarget(AiTargetCandidate Candidate, long? ProcessStarted, long? WindowHandle, string? ExecutableHash);
    private static void AiProgress(WorkflowTask task, string phase, string message, object? target = null, string? runId = null)
    {
        lock (task) { task.Phase = phase; task.ProgressMessage = message; if (target is not null) task.Target = target; if (runId is not null) task.RunId = runId; }
    }
    private static string? CortexWorkspace(McpRequestContext context)
    {
        var metadata = context.Param("_meta");
        if (metadata is not { ValueKind: JsonValueKind.Object } meta || !meta.TryGetProperty("cortex/workspace", out var value)) return null;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 4096 } path || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            throw new McpToolException("Cortex workspace metadata must identify an existing absolute directory.");
        return Path.GetFullPath(path);
    }
    private McpToolResult StartAiTest(ToolArguments arguments, McpRequestContext context, bool refine = false, bool forceDraftOnly = false, string operation = "ai_test")
    {
        var instructions = arguments.RequireString("instructions",16000);
        var existing = arguments.String("testId",100) is { } id ? TestyJson.Clone(RequireTest(id)) : null;
        var sourceTestId=existing?.Id; var sourceRevision=existing is null ? null : WorkspaceStore.Revision(existing);
        if (arguments.Has("draft")) { existing=ApplyAuthoringDraft(existing,arguments.RequireElement("draft"),arguments.String("expectedRevision",64)); refine=true; }
        else if (arguments.String("expectedRevision",64) is { } expected && expected!=sourceRevision) throw new McpToolException("This test changed outside this editor. Reload it before improving it.");
        var pid = arguments.IntOrNull("pid",1); var exe = arguments.String("exe",1024);
        if (pid is not null && exe is not null) throw new McpToolException("Choose either the running app override or the executable override.");
        if (pid is { } specifiedPid) Policy.RequireTarget(specifiedPid);
        if (exe is not null) exe = Policy.RequireLaunchable(exe);
        if (existing is not null && existing.Steps.Count>0) TestValidator.Validate(existing);
        var workspace = CortexWorkspace(context); var settings = TestyJson.Clone(Store.LoadSettings()); settings.AiDirectedExecution = true;
        var draftOnly = forceDraftOnly || arguments.Bool("draftOnly",false); var probe = arguments.Bool("probe",false);
        return StartWorkflow(operation, async (task, ct) =>
        {
            task.Stop.CancelAfter(TimeSpan.FromMinutes(15));
            AiProgress(task,"discovering","Finding the app described in your request…");
            var directory = Path.Combine(SessionDirectory,"ai-tests",task.Id); Directory.CreateDirectory(directory);
            CapturedTarget? selected; string reason;
            if (pid is { } overridePid)
            {
                selected = await CaptureRunningTargetAsync(overridePid,ct);
                reason = "You selected this running application.";
            }
            else if (exe is not null) { selected = CaptureExecutable(exe,"override"); reason = "You selected this executable."; }
            else
            {
                var candidates = await CaptureAiTargetsAsync(workspace,existing?.TargetPath,instructions,ct);
                if (candidates.Count == 0) return new { kind = "needsTarget", question = "Open the application you want to test, or choose its executable, then try again.", candidates = Array.Empty<object>() };
                var evidence = candidates.Select(c => c.Candidate).ToArray();
                WorkspaceStore.WriteAtomic(Path.Combine(directory,"target-inventory.json"),new { instructions, workspace, savedTargetPath=existing?.TargetPath, candidates=evidence });
                var decision = await new AiTargetSelectionPlanner(settings).SelectAsync(instructions,workspace,existing?.TargetPath,evidence,ct);
                WorkspaceStore.WriteAtomic(Path.Combine(directory,"target-selection.json"),decision);
                if (decision.CandidateId is null) return new { kind="needsTarget",question=decision.Question,candidates=evidence };
                selected = candidates.Single(c => c.Candidate.Id == decision.CandidateId); reason = decision.Reason;
            }
            ct.ThrowIfCancellationRequested(); RequireDesktop();
            DesktopSlot? slot = await AcquireDesktopAsync(ct,sendsInput:true); LaunchedApp? ownedApp = null;
            try
            {
                int targetPid;
                if (selected.Candidate.Pid is { } capturedPid)
                {
                    ValidateTargetIdentity(capturedPid,selected.ProcessStarted!.Value);
                    targetPid=capturedPid;
                }
                else
                {
                    var launchPath=Policy.RequireLaunchable(selected.Candidate.Exe!);
                    if (ExecutableHash(launchPath) != selected.ExecutableHash) throw new McpToolException("The selected executable changed during app discovery. Start a fresh request.");
                    AiProgress(task,"discovering","Opening "+selected.Candidate.Title+"…");
                    ownedApp=StartProcess(launchPath,[],Path.GetDirectoryName(launchPath),keepOpen:false);
                    await WaitForWindowAsync(ownedApp.Process,selected.Candidate.Title,TimeSpan.FromSeconds(30),(_,message)=>AiProgress(task,"discovering",message),ct);
                    targetPid=ownedApp.Process.Id; selected=await CaptureRunningTargetAsync(targetPid,ct);
                }
                var target=new { pid=targetPid,title=selected.Candidate.Title,processName=selected.Candidate.ProcessName,exe=selected.Candidate.Exe,reason };
                TestCase test;
                using (var driver=await AttachAsync(targetPid,probe,ct))
                {
                    ValidateTargetIdentity(targetPid,selected.ProcessStarted!.Value);
                    if (selected.WindowHandle is { } window && driver.Target?.WindowHandle != window) throw new McpToolException("The app's main window changed during discovery. Start a fresh request.");
                    if (driver.Target is { WindowHandle: not 0 } current)
                    {
                        if (IsIconic((nint)current.WindowHandle)) ShowWindowAsync((nint)current.WindowHandle,9);
                        SetForegroundWindow((nint)current.WindowHandle);
                    }
                    if (existing is null || refine)
                    {
                        AiProgress(task,"drafting","Planning the test from the app's current controls…",target);
                        var snapshot=await driver.SnapshotAsync(ct);
                        var screenshot=settings.SupportsImages ? await driver.CaptureAsync(Path.Combine(directory,"target.png"),ct) : "";
                        test=await PlannerFactory.Create(settings,directory).CreatePlanAsync(new() { Instructions=instructions,Snapshot=snapshot,ScreenshotPath=screenshot,ExistingTest=refine ? existing : null },ct);
                        test.Id=existing?.Id ?? Guid.NewGuid().ToString("N"); test.TargetPath=selected.Candidate.Exe ?? existing?.TargetPath ?? "";
                        TestValidator.Validate(test);
                    }
                    else test=existing;
                }
                ct.ThrowIfCancellationRequested();
                if (draftOnly) { AiProgress(task,"drafting","Draft ready to review.",target); return new { kind="draft",draft=TestView(test),target,sourceTestId,expectedRevision=sourceRevision,saved=false }; }
                ValidateTargetIdentity(targetPid,selected.ProcessStarted!.Value);
                AiTestRunner.ValidateExecution(test,settings);
                if (existing is null) Store.SaveTest(test);
                AiProgress(task,"running","Running the test with live app observations…",target);
                var active=new ActiveRun(test,CancellationTokenSource.CreateLinkedTokenSource(ct,shutdown.Token)); activeRuns[active]=0;
                var runSlot=slot; slot=null;
                active.Completion=ExecuteRunAsync(active,test,settings,targetPid,null,[],null,TimeSpan.FromSeconds(30),false,false,TimeSpan.FromMinutes(10),runSlot,context,
                    update=>AiProgress(task,"running",update.Message,target,update.Run.Id),selected.ProcessStarted);
                await active.Completion;
                if (active.Latest is not { } run) throw active.Failure ?? new McpToolException("The AI run ended before it recorded a result.");
                AiProgress(task,"running",run.Summary,target,run.Id);
                return new { kind="completed",testId=test.Id,runId=run.Id,target,status=run.Status,draft=existing is null ? TestView(test) : null };
            }
            finally
            {
                try { if (ownedApp is not null) await ForgetAsync(ownedApp,TimeSpan.FromSeconds(3)); }
                finally { slot?.Dispose(); }
            }
        });
    }
    internal static TestCase ApplyAuthoringDraft(TestCase? saved,JsonElement document,string? expectedRevision)
    {
        if (saved is not null && (expectedRevision is null || expectedRevision!=WorkspaceStore.Revision(saved))) throw new McpToolException("This test changed outside this editor or its revision is missing. Reload it before improving it.");
        var fields=new ToolArguments(document,CortexMcpTools.EditableDraft()); var draft=saved is null ? new TestCase() : TestyJson.Clone(saved);
        draft.Name=NonBlank(fields.String("name",200),"Untitled test"); draft.Intent=fields.String("intent",4000) ?? "";
        draft.Category=NonBlank(fields.String("category",200),"Functional"); draft.TargetPath=TargetPath(fields.String("targetPath",1024));
        var steps=fields.RequireElement("steps");
        if (steps.ValueKind!=JsonValueKind.Array) throw new McpToolException("Draft steps must be an array.");
        draft.Steps=steps.GetArrayLength()==0 ? [] : ParseSteps(steps,draft.Steps);
        if (draft.Steps.Count>0) TestValidator.Validate(draft);
        return draft;
    }
    internal static void ValidateTargetIdentity(int pid,long startedTicks)
    {
        try
        {
            using var process=Process.GetProcessById(pid);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != startedTicks) throw new McpToolException("The selected process ended or changed during discovery. No action was sent; start a fresh request.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { throw new McpToolException("The selected process is no longer available. No action was sent; start a fresh request."); }
    }
    private async Task<CapturedTarget> CaptureRunningTargetAsync(int pid,CancellationToken ct)
    {
        Policy.RequireTarget(pid);
        using var driver=new UiAutomationDriver(); await driver.AttachAsync(pid,ct);
        using var process=Process.GetProcessById(pid); var target=driver.Target!;
        return new(new("pid-"+pid,target.Title,target.ProcessName,pid,ExePath(pid)),process.StartTime.ToUniversalTime().Ticks,target.WindowHandle,null);
    }
    private static string ExecutableHash(string path) { using var input=File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    private CapturedTarget CaptureExecutable(string path,string id,string? title=null)
    {
        var allowed=Policy.RequireLaunchable(path);
        var name=Path.GetFileNameWithoutExtension(allowed);
        return new(new(id,string.IsNullOrWhiteSpace(title) ? name : title.Trim(),name,null,allowed),null,null,ExecutableHash(allowed));
    }
    /// <summary>Whether a request mentions one of the app's names as whole words ("check Customer Desk…" names "Customer Desk").</summary>
    private static bool Mentions(List<string> request,AppCandidate app)
    {
        foreach (var name in new[] { app.Name, app.ProductName }.Concat(app.OtherNames).Concat(app.ShortcutNames))
        {
            var words=AppResolver.Tokens(name);
            if (words.Count==0 || (words.Count==1 && words[0].Length<4)) continue;
            for (var start=0; start+words.Count<=request.Count; start++)
                if (request.Skip(start).Take(words.Count).SequenceEqual(words)) return true;
        }
        return false;
    }
    private async Task<List<CapturedTarget>> CaptureAiTargetsAsync(string? workspace,string? savedTarget,string instructions,CancellationToken ct)
    {
        using var driver=new UiAutomationDriver(); var windows=await driver.GetTargetsAsync(ct); var result=new List<CapturedTarget>();
        foreach (var group in windows.GroupBy(t=>t.ProcessId).Take(80))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var candidate=await CaptureRunningTargetAsync(group.Key,ct);
                if (group.Count()>1) candidate=candidate with { Candidate=candidate.Candidate with { Title=candidate.Candidate.Title+" (other windows: "+string.Join("; ",group.Where(w=>w.WindowHandle!=candidate.WindowHandle).Take(5).Select(w=>w.Title))+ ")" } };
                result.Add(candidate);
            }
            catch (Exception ex) when (ex is McpToolException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { /* The window closed or is outside this server's target policy. */ }
        }
        var known=result.Where(c=>c.Candidate.Exe is not null).Select(c=>c.Candidate.Exe!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        void AddExecutable(string path,string? title=null)
        {
            if (result.Count>=100 || known.Contains(path)) return;
            try { var candidate=CaptureExecutable(path,"exe-"+result.Count,title); result.Add(candidate); known.Add(candidate.Candidate.Exe!); }
            catch (Exception ex) when (ex is McpToolException or IOException or UnauthorizedAccessException) { /* Only existing GUI apps permitted by the launch policy are candidates. */ }
        }
        if (!string.IsNullOrWhiteSpace(savedTarget)) AddExecutable(savedTarget);
        // The user never picks the app: the bundled samples, programs earlier tests used, and installed apps the request names are
        // candidates even when they are not open, so the model can choose one and Testy opens it (through the same launch policy).
        try
        {
            var request=AppResolver.Tokens(instructions);
            foreach (var app in await Task.Run(() => AppCandidates(includeInstalled: true),ct))
            {
                if (result.Count>=100) break;
                if (app.IsRunning || app.Packaged || app.ExePath.Length==0) continue;
                if (app.Kind is AppCandidateKind.Sample or AppCandidateKind.Recent || Mentions(request,app)) AddExecutable(app.ExePath,app.Name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { /* Discovery is best effort; open apps remain candidates. */ }
        if (workspace is not null)
        {
            var pending=new Queue<(string Path,int Depth)>(); pending.Enqueue((workspace,0)); int examined=0;
            while (pending.Count>0 && examined<2000 && result.Count<100)
            {
                var folder=pending.Dequeue();
                try
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(folder.Path))
                    {
                        ct.ThrowIfCancellationRequested(); if (++examined>2000 || result.Count>=100) break;
                        var attributes=File.GetAttributes(entry); if ((attributes&FileAttributes.ReparsePoint)!=0) continue;
                        if ((attributes&FileAttributes.Directory)!=0)
                        {
                            if (folder.Depth<4 && Path.GetFileName(entry) is not (".git" or "node_modules" or "obj" or ".cortex" or ".venv")) pending.Enqueue((entry,folder.Depth+1));
                        }
                        else if (entry.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)) AddExecutable(entry);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Unreadable directories are not discovery candidates. */ }
            }
        }
        return result;
    }
    private async Task<McpToolResult> LaunchSampleAsync(ToolArguments arguments,McpRequestContext context)
    {
        var sample=arguments.Choice("sample",["testlab","orderlab","wpflab"],"testlab");
        var testLab=SampleApp ?? throw new McpToolException("The bundled sample applications are unavailable in this build.");
        var exe=sample=="testlab" ? testLab : Path.Combine(Path.GetDirectoryName(testLab)!,sample=="orderlab" ? "Testy.OrderLab.exe" : "Testy.WpfLab.exe");
        exe=Policy.RequireLaunchable(exe); RequireDesktop(); using var slot=await AcquireDesktopAsync(context.Token,sendsInput:true);
        var owned=StartProcess(exe,[],Path.GetDirectoryName(exe),keepOpen:false);
        try
        {
            var title=await WaitForWindowAsync(owned.Process,Path.GetFileName(exe),TimeSpan.FromSeconds(30),(_,_)=>{},context.Token);
            return McpToolResult.Json(new { pid=owned.Process.Id,title,processName=ProcessName(owned.Process),exe,launchedByThisServer=true });
        }
        catch { await ForgetAsync(owned,TimeSpan.FromSeconds(3)); throw; }
    }
}
