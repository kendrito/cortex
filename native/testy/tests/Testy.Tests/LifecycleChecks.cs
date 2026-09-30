using System.Net;
using System.Text.Json;
using Testy.Core;

namespace Testy.Tests;

internal static class LifecycleChecks
{
    private static readonly ProjectChoice Inspect = new("project_read_file", "{\"path\":\"Feature.cs\",\"startLine\":1,\"maxLines\":8}");
    private static readonly ProjectChoice Complete = new("complete_preparation", "{\"message\":\"Required project preparation verified.\"}");
    public static IEnumerable<(string Name, Func<Task> Execute)> All()
    {
        yield return ("prelaunch AI preparation uses real project observations without any fake UI target", PreparationProviders);
        yield return ("preparation rejects premature completion batches and impossible budgets before commands", PreparationBoundaries);
        yield return ("lifecycle prepares before launching and gives the UI phase readonly project access", StageOrdering);
        yield return ("failed preparation and missing build outputs never invoke the worker", FailedPreparation);
        yield return ("interrupted lifecycle reconciliation never resumes unknown commands or input", InterruptedCheckpoint);
        yield return ("transient model response retries dispatch each command once with durable attempt evidence", TransientProvider);
        yield return ("nontransient model errors and exhausted retry budgets execute no tools", ProviderLimits);
        yield return ("long local workflows retain every exact saved action and bounded model decisions", LongWorkflow);
        yield return ("agent journals persist pending dispatch before input and observations after completion", AgentCheckpoint);
    }
    private static async Task PreparationProviders()
    {
        foreach (var adapter in new[] { "responses", "compatible", "codex" })
        {
            using var f = new ProjectFixture(adapter, true);
            var path = Path.Combine(f.Root, "preparation");
            var (agent, client) = Agent(f, adapter, path, [Inspect, f.Command(), Complete]);
            using (client)
            {
                var result = await agent.RunAsync("Inspect and build before launching.", 5);
                ProjectFixture.Check(result.Passed && result.ModelTurns == 3 && f.CommandCalls == 1 && result.ProjectEvidence.Count == 2 && f.Driver.Snapshots == 0 && f.Driver.Mutations == 0, "Preparation invented UI state or skipped actual project tools.");
                ProjectEvidenceVerifier.ValidateSequence(result.ProjectEvidence, path, f.Settings.ProjectTools, true);
                var saved = Read<PreparationResult>(Path.Combine(path, "preparation.json"));
                ProjectFixture.Check(saved.Passed && saved.PendingTool == "" && saved.FinishedAt is not null, "Terminal preparation checkpoint is incomplete.");
                ProjectFixture.Check(f.Requests[1].Contains("ProjectFixtureBusinessRule", StringComparison.Ordinal) && f.Requests[2].Contains("Build complete", StringComparison.Ordinal), "Each model decision did not receive actual previous project feedback.");
                if (adapter != "codex") foreach (var body in f.Requests)
                {
                    using var request = JsonDocument.Parse(body);
                    var names = request.RootElement.GetProperty("tools").EnumerateArray().Select(t => adapter == "compatible" ? t.GetProperty("function").GetProperty("name").GetString() : t.GetProperty("name").GetString());
                    ProjectFixture.Check(names.All(n => n == "complete_preparation" || ProjectToolSession.IsTool(n!)) && !body.Contains("data:image", StringComparison.Ordinal), "Prelaunch request exposed a UI tool or fake screenshot.");
                }
            }
        }
    }
    private static async Task PreparationBoundaries()
    {
        foreach (var choices in new[] { new[] { Complete }, new[] { Inspect, Complete } })
        {
            using var f = new ProjectFixture("compatible", true);
            var (agent, client) = Agent(f, "compatible", Path.Combine(f.Root, "preparation"), choices);
            using (client) { var result = await agent.RunAsync("Prepare", 5); ProjectFixture.Check(!result.Passed && f.CommandCalls == 0, "Missing prerequisites granted preparation or auto-ran a command."); }
        }
        using (var f = new ProjectFixture("compatible", true) { Batch = true })
        {
            var (agent, client) = Agent(f, "compatible", Path.Combine(f.Root, "preparation"), [f.Command()]);
            using (client) { var result = await agent.RunAsync("Prepare", 5); ProjectFixture.Check(!result.Passed && f.CommandCalls == 0, "Mixed preparation calls dispatched a command."); }
        }
        using (var f = new ProjectFixture("codex", true))
        {
            var (agent, _) = Agent(f, "codex", Path.Combine(f.Root, "preparation"), [f.Command()]);
            await Reject(() => agent.RunAsync("Prepare", 2));
            ProjectFixture.Check(f.Requests.Count == 0 && f.CommandCalls == 0, "Impossible preparation budget reached provider/command.");
        }
    }
    private static async Task StageOrdering()
    {
        using var f = new ProjectFixture("codex", true);
        var request = Request(f); var canonical = JsonSerializer.Serialize(request.Test, TestyJson.Options); var launches = 0;
        var runner = new LifecycleRunner((_, directory) => Agent(f, "codex", directory, [Inspect, f.Command(), Complete]).Agent, (ui, directory, _) =>
        {
            launches++;
            ProjectFixture.Check(f.CommandCalls == 1 && Read<PreparationResult>(Path.Combine(Path.GetDirectoryName(directory)!, "preparation", "preparation.json")).Passed, "Target was launched before terminal command evidence.");
            ProjectFixture.Check(ui.Provider.ProjectTools!.Enabled && ui.Provider.ProjectTools.Commands.Count == 0 && ui.Provider.ProjectTools.RequiredBeforeUiCommands.Count == 0 && JsonSerializer.Serialize(ui.Test, TestyJson.Options) == canonical, "UI stage repeated preparation commands or changed immutable acceptance.");
            return Task.FromResult(new LifecycleWorkerOutcome { Passed = true, Status = "passed", CleanupComplete = true, ArtifactDirectory = directory, Message = "Injected worker acceptance seam; no desktop launched." });
        });
        var result = await runner.RunAsync(request, Path.Combine(f.Root, "lifecycle"));
        ProjectFixture.Check(result.Passed && launches == 1 && result.Checkpoints.Select(c => c.Stage).SequenceEqual(new[] { "preparing", "prepared", "workerStarting", "workerFinished", "terminal" }), "Lifecycle stages did not finish in order.");
        ProjectFixture.Check(request.Provider.ProjectTools!.Commands.Count == 1 && result.Preparation!.ProjectEvidence.All(e => e.EvidencePath.Contains("preparation", StringComparison.Ordinal)), "Caller configuration mutated or preparation evidence misattributed to UI.");
    }
    private static async Task FailedPreparation()
    {
        foreach (var missingOutput in new[] { false, true })
        {
            using var f = new ProjectFixture("codex", true) { CommandSucceeds = missingOutput };
            var request = Request(f); if (missingOutput) request.Profile.Executable = Path.Combine(f.Root, "not-produced.exe");
            var launches = 0;
            var runner = new LifecycleRunner((_, directory) => Agent(f, "codex", directory, [Inspect, f.Command(), Complete]).Agent, (_, _, _) => { launches++; throw new InvalidOperationException("Must not launch"); });
            var result = await runner.RunAsync(request, Path.Combine(f.Root, "lifecycle"));
            ProjectFixture.Check(!result.Passed && result.Status == LifecycleStatus.Failed && launches == 0 && f.CommandCalls == 1 && result.FinishedAt is not null, "Failed/missing preparation output launched or repeated input.");
        }
        using (var f = new ProjectFixture("codex", true))
        {
            var request = Request(f); request.Profile.PreparationTimeoutSeconds = 1; var launches = 0;
            var runner = new LifecycleRunner((_, directory) => new DelegatePreparation(async (instructions, turns, token) =>
            {
                var completed = await Agent(f, "codex", directory, [Inspect, f.Command(), Complete]).Agent.RunAsync(instructions, turns, token);
                await Task.Delay(1100); return completed;
            }), (_, _, _) => { launches++; throw new InvalidOperationException("Must not launch"); });
            var result = await runner.RunAsync(request, Path.Combine(f.Root, "late"));
            ProjectFixture.Check(result.Status == LifecycleStatus.TimedOut && !result.Passed && launches == 0 && f.CommandCalls == 1, "Preparation completed past its deadline but target was launched.");
        }
        using (var f = new ProjectFixture("codex", true))
        {
            var runner = new LifecycleRunner((_, directory) => new DelegatePreparation((_, _, _) =>
            {
                LifecycleJournal.Write(Path.Combine(directory, "preparation.json"), new PreparationResult { PendingTool = "project_run_command", ActionOutcomeUnknown = true });
                throw new IOException("Injected interruption after pending command checkpoint");
            }), (_, _, _) => throw new InvalidOperationException("Must not launch"));
            var result = await runner.RunAsync(Request(f), Path.Combine(f.Root, "uncertain"));
            ProjectFixture.Check(!result.Passed && result.ActionOutcomeUnknown && !result.CleanupComplete && result.Preparation?.PendingTool == "project_run_command", "Throwing preparation lost its durable unknown command outcome.");
        }
    }
    private static Task InterruptedCheckpoint()
    {
        using var f = new ProjectFixture("codex", true); var directory = Path.Combine(f.Root, "interrupted"); Directory.CreateDirectory(Path.Combine(directory, "preparation"));
        var lockPath = Path.Combine(directory, "active.lock"); File.WriteAllText(lockPath, "");
        LifecycleJournal.Write(Path.Combine(directory, "lifecycle-result.json"), new LifecycleResult { ArtifactDirectory = directory, Status = LifecycleStatus.Preparing });
        LifecycleJournal.Write(Path.Combine(directory, "preparation", "preparation.json"), new PreparationResult { PendingTool = "project_run_command", ActionOutcomeUnknown = true });
        using (var active = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) RejectSync(() => LifecycleRunner.InspectInterrupted(directory));
        var result = LifecycleRunner.InspectInterrupted(directory);
        ProjectFixture.Check(result.Status == LifecycleStatus.NeedsReview && !result.Passed && result.ActionOutcomeUnknown && !result.CleanupComplete && f.CommandCalls == 0 && result.FinishedAt is not null, "Interrupted pending command was treated as safe to resume.");
        ProjectFixture.Check(LifecycleRunner.InspectInterrupted(directory).Status == LifecycleStatus.NeedsReview, "Repeated inspection changed interruption into success.");
        return Task.CompletedTask;
    }
    private static async Task TransientProvider()
    {
        foreach (var adapter in new[] { "responses", "compatible" })
        {
            using var f = new ProjectFixture(adapter, true); f.Settings.ProviderRetryDelayMs = 0;
            var calls = 0; var queue = new Queue<ProjectChoice>([Inspect, f.Command(), Complete]);
            using var client = new HttpClient(new ProjectHandler(async request =>
            {
                var body = await request.Content!.ReadAsStringAsync(); f.Requests.Add(body); calls++;
                if (calls is 1 or 3) return new HttpResponseMessage(HttpStatusCode.GatewayTimeout) { Content = new StringContent("{\"error\":{\"message\":\"upstream timeout\"}}") };
                return ProjectFixture.Reply(queue.Dequeue(), calls.ToString(), adapter == "responses");
            }));
            var result = await new ProjectPreparationAgent(f.Settings, Path.Combine(f.Root, "preparation"), client, path => f.Session(Path.Combine(path, "project-tools"))).RunAsync("Prepare", 6);
            ProjectFixture.Check(result.Passed && result.ModelTurns == 3 && calls == 5 && f.CommandCalls == 1 && f.Requests[0] == f.Requests[1] && f.Requests[2] == f.Requests[3], "Provider retry repeated a host command or changed the pending model request.");
            var attempts = Read<List<ProviderRequestAttempt>>(Path.Combine(f.Root, "preparation", "provider-requests.json"));
            ProjectFixture.Check(attempts.Count == 5 && attempts.Count(a => a.StatusCode == 504 && a.Retrying) == 2 && attempts.All(a => a.FinishedAt >= a.StartedAt) && attempts[0].RequestId == attempts[1].RequestId && attempts[2].RequestId == attempts[3].RequestId, "Durable provider retry evidence lost request identity, statuses or timing.");
        }
    }
    private static async Task ProviderLimits()
    {
        foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.GatewayTimeout })
        {
            using var f = new ProjectFixture("compatible", true); f.Settings.ProviderRetryDelayMs = 0; f.Settings.MaximumProviderRetries = 2; var calls = 0;
            using var client = new HttpClient(new ProjectHandler(_ => { calls++; return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{\"error\":{\"message\":\"fixture\"}}") }); }));
            var result = await new ProjectPreparationAgent(f.Settings, Path.Combine(f.Root, "preparation"), client, path => f.Session(Path.Combine(path, "project-tools"))).RunAsync("Prepare", 5);
            ProjectFixture.Check(!result.Passed && calls == (status == HttpStatusCode.BadRequest ? 1 : 3) && f.CommandCalls == 0 && result.ProjectEvidence.Count == 0, "Provider error was retried outside budget or dispatched commands.");
        }
    }
    private static async Task LongWorkflow()
    {
        using var f = new ProjectFixture("codex"); f.Settings.ProjectTools = null; f.Settings.MaximumAgentTurns = 65;
        f.Test.Steps = Enumerable.Range(0, 50).Select(i => new TestStep { Id = "assert-" + i, Title = "Readiness" + i, Action = StepAction.AssertText, Selector = "id:Status", Value = "Ready", TimeoutMs = 200 }).ToList();
        AiTestRunner.ValidateExecution(f.Test, f.Settings); var index = 0;
        var agent = new CodexComputerAgent(f.Settings, f.Driver, Path.Combine(f.Root, "long"), (_, _, _, _) =>
        {
            var done = index == f.Test.Steps.Count; var step = done ? "" : f.Test.Steps[index++].Id;
            return Task.FromResult(JsonSerializer.Serialize(new { done, explanation = "Observed", toolName = done ? "observe_application" : "perform_saved_step", stepId = step }));
        }, f.Test);
        var result = await agent.RunAsync("Every exact saved assertion in order", 65);
        ProjectFixture.Check(result.Completed && result.ModelTurns == 51 && SavedWorkflowVerifier.Verify(f.Test, result.Observations).Complete, "A workflow above the legacy40-turn limit lost immutable steps.");
        f.Settings.MaximumAgentTurns = AgentLimits.MaximumLocalTurns + 1; RejectSync(() => AiTestRunner.ValidateExecution(f.Test, f.Settings));
    }
    private static async Task AgentCheckpoint()
    {
        using var f = new ProjectFixture("compatible");
        var path = Path.Combine(f.Root, "agent"); var calls = 0;
        using var client = new HttpClient(new ProjectHandler(_ =>
        {
            calls++; using var journal = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "agent-checkpoint.json")));
            ProjectFixture.Check(journal.RootElement.GetProperty("stage").GetString() == "awaitingProvider", "Model request lacks a durable preceding checkpoint.");
            return Task.FromResult(ProjectFixture.Reply(calls == 1 ? f.Saved() : ProjectChoice.Finish, calls.ToString(), false));
        }));
        var driver = new JournalDriver(f.Driver, path);
        var result = await new ComputerUseAgent(f.Settings, driver, path, client, f.Test).RunAsync("Verify", 4);
        using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "agent-checkpoint.json")));
        ProjectFixture.Check(result.Completed && driver.SawPendingDispatch && saved.RootElement.GetProperty("stage").GetString() == "terminal" && saved.RootElement.GetProperty("pendingTool").GetString() == "", "Pending dispatch was not durable before driver observation, or terminal checkpoint forgot completion.");
    }
    private sealed class JournalDriver(ITargetDriver inner, string path) : ITargetDriver
    {
        public bool SawPendingDispatch;
        public TargetInfo? Target => inner.Target;
        public Task<IReadOnlyList<TargetInfo>> GetTargetsAsync(CancellationToken ct = default) => inner.GetTargetsAsync(ct);
        public Task AttachAsync(int pid, CancellationToken ct = default) => inner.AttachAsync(pid, ct);
        public Task<UiSnapshot> SnapshotAsync(CancellationToken ct = default)
        {
            var file = Path.Combine(path, "agent-checkpoint.json");
            if (File.Exists(file))
            {
                using var journal = JsonDocument.Parse(File.ReadAllText(file));
                SawPendingDispatch |= journal.RootElement.GetProperty("stage").GetString() == "dispatching" && journal.RootElement.GetProperty("pendingTool").GetString() == "perform_saved_step";
            }
            return inner.SnapshotAsync(ct);
        }
        public Task ExecuteAsync(TestStep step, CancellationToken ct = default) => inner.ExecuteAsync(step, ct);
        public Task<string> CaptureAsync(string file, CancellationToken ct = default) => inner.CaptureAsync(file, ct);
        public void Dispose() { }
    }
    private sealed class DelegatePreparation(Func<string, int, CancellationToken, Task<PreparationResult>> run) : IProjectPreparationAgent
    {
        public Task<PreparationResult> RunAsync(string instructions, int turns, CancellationToken ct = default) => run(instructions, turns, ct);
    }
    private static LifecycleRequest Request(ProjectFixture f) => new() { Profile = new LifecycleProfile { Executable = Environment.ProcessPath!, MaximumPreparationTurns = 5 }, Provider = TestyJson.Clone(f.Settings), Test = TestyJson.Clone(f.Test) };
    private static (ProjectPreparationAgent Agent, HttpClient? Client) Agent(ProjectFixture f, string adapter, string path, ProjectChoice[] choices)
    {
        var queue = new Queue<ProjectChoice>(choices); var index = 0;
        if (adapter == "codex") return (new ProjectPreparationAgent(f.Settings, path, sessionFactory: d => f.Session(Path.Combine(d, "project-tools")), decisionProvider: (prompt, _, image, _) =>
        {
            ProjectFixture.Check(image == "", "Prelaunch Codex received a fabricated screenshot."); f.Requests.Add(prompt); var next = queue.Dequeue();
            return Task.FromResult(JsonSerializer.Serialize(new { done = false, explanation = "Prepare", toolName = next.Name, arguments = next.Arguments }));
        }), null);
        var client = new HttpClient(new ProjectHandler(async request => { f.Requests.Add(await request.Content!.ReadAsStringAsync()); var next = queue.Dequeue(); return ProjectFixture.Reply(next, (++index).ToString(), adapter == "responses", f.Batch ? f.Command() : null); }));
        return (new ProjectPreparationAgent(f.Settings, path, client, d => f.Session(Path.Combine(d, "project-tools"))), client);
    }
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), TestyJson.Options)!;
    private static async Task Reject(Func<Task> action) { try { await action(); } catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { return; } throw new InvalidOperationException("Expected rejection."); }
    private static void RejectSync(Action action) { try { action(); } catch (Exception e) when (e is InvalidDataException or InvalidOperationException or IOException) { return; } throw new InvalidOperationException("Expected rejection."); }
}
