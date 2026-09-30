using System.Net;
using System.Text;
using System.Text.Json;
using Testy.Core;

namespace Testy.Tests;

internal static class ProjectAgentChecks
{
    public static IEnumerable<(string Name, Func<Task> Execute)> All()
    {
        yield return ("all execution adapters inspect project files without advancing saved UI workflow", ReadThenUi);
        yield return ("all execution adapters require fresh UI observation after a project command", CommandRefresh);
        yield return ("failed project commands block UI and final success but permit read-only diagnostics", FailedCommands);
        yield return ("required project commands cannot be skipped by completion or saved UI input", RequiredCommands);
        yield return ("project and UI calls in one model turn are rejected before either dispatch", MixedCalls);
        yield return ("AI run aggregates project evidence separately from canonical UI actions", AggregateEvidence);
        yield return ("native computer actions coexist with project functions and immutable assertions", NativeCoexistence);
        yield return ("required-command execution budgets fail before model calls, observations or commands", ImpossibleBudgets);
        yield return ("UI failure explanations retain project evidence without granting more tool actions", FailureReviewContext);
    }
    private static async Task ReadThenUi()
    {
        foreach (var adapter in ProjectFixture.Adapters)
        {
            using var f = new ProjectFixture(adapter);
            var result = await f.Run([new("project_read_file", "{\"path\":\"Feature.cs\",\"startLine\":1,\"maxLines\":8}"), f.Saved(), ProjectChoice.Finish]);
            ProjectFixture.Check(result.Completed && result.HasVerifiedAssertions && result.Status == RunStatus.Pending, adapter + ": " + result.Message);
            ProjectFixture.Check(SavedWorkflowVerifier.Verify(f.Test, result.Observations).Complete, "Project read advanced/lost saved workflow coverage.");
            var read = result.Observations.Single(o => o.Project is not null);
            ProjectFixture.Check(read.Execution is null && read.Snapshot is null && read.ScreenshotPath == "" && read.SavedStepId == "", "Project result invented UI evidence.");
            ProjectFixture.Check(read.Project!.Status == ProjectToolStatus.Succeeded && File.Exists(read.Project.EvidencePath), "Project file evidence not retained.");
            ProjectFixture.Check(f.Requests.Skip(1).Any(p => p.Contains("ProjectFixtureBusinessRule", StringComparison.Ordinal)), "Updated project contents did not reach the next model turn.");
            if (adapter != "codex")
            {
                using var request = JsonDocument.Parse(f.Requests[1]);
                var transcript = request.RootElement.GetProperty(adapter == "compatible" ? "messages" : "input");
                ProjectFixture.Check(transcript.EnumerateArray().Any(item => adapter == "compatible"
                    ? item.TryGetProperty("tool_call_id", out var id) && id.GetString() == "1"
                    : item.TryGetProperty("type", out var kind) && kind.GetString() == "function_call_output" && item.GetProperty("call_id").GetString() == "1"), "Project feedback lost its original function call ID.");
            }
        }
    }
    private static async Task CommandRefresh()
    {
        foreach (var adapter in ProjectFixture.Adapters)
        {
            using (var f = new ProjectFixture(adapter))
            {
                var result = await f.Run([f.Command(), f.Saved()]);
                ProjectFixture.Check(result.Status == RunStatus.Failed && !result.HasVerifiedAssertions && f.Driver.Mutations == 0 && f.CommandCalls == 1, "Stale observation was used: " + adapter);
                ProjectFixture.Check(result.Message.Contains("observation", StringComparison.OrdinalIgnoreCase), "Stale-UI rejection omitted diagnosis.");
            }
            using (var f = new ProjectFixture(adapter))
            {
                var result = await f.Run([f.Command(), new("observe_application", "{}"), f.Saved(), ProjectChoice.Finish]);
                ProjectFixture.Check(result.Completed && result.HasVerifiedAssertions && f.CommandCalls == 1, "Fresh explicit observation did not enable saved assertion: " + result.Message);
                ProjectFixture.Check(SavedWorkflowVerifier.Verify(f.Test, result.Observations).Complete, "Fresh observation lost workflow coverage.");
            }
        }
    }
    private static async Task FailedCommands()
    {
        foreach (var adapter in ProjectFixture.Adapters)
        {
            using var f = new ProjectFixture(adapter) { CommandSucceeds = false };
            var result = await f.Run([f.Command(), new("project_list_files", "{\"directory\":\".\"}"), ProjectChoice.Finish]);
            ProjectFixture.Check(result.Status == RunStatus.Failed && !result.Completed && f.CommandCalls == 1 && f.Driver.Mutations == 0, "Failed command became success: " + adapter);
            ProjectFixture.Check(result.Observations.Count(o => o.Project is not null) == 2 && result.Observations.Any(o => o.Project?.BlocksFurtherActions == true), "Read-only diagnostics or blocking evidence lost.");
            ProjectFixture.Check(result.Observations.Last(o => o.Project is not null).Project!.Status == ProjectToolStatus.Succeeded, "Read-only diagnosis was blocked after command failure.");
        }
    }
    private static async Task RequiredCommands()
    {
        foreach (var adapter in ProjectFixture.Adapters)
            foreach (var input in new[] { false, true })
            {
                using var f = new ProjectFixture(adapter, requiredCommand: true);
                var result = await f.Run(input ? [new("observe_application", "{}"), f.Saved()] : [ProjectChoice.Finish]);
                ProjectFixture.Check(result.Status == RunStatus.Failed && !result.Completed && !result.HasVerifiedAssertions && f.CommandCalls == 0 && f.Driver.Mutations == 0, "Required project command was skipped: " + adapter);
            }
        using var native = new ProjectFixture("native", requiredCommand: true);
        native.Test.Steps.Insert(0, new TestStep { Id = "initial", Title = "Saved screenshot", Action = StepAction.Screenshot });
        var screenshot = await native.Run([new("computer", "{\"type\":\"screenshot\"}")]);
        ProjectFixture.Check(screenshot.Status == RunStatus.Failed && native.Executor.Calls == 0, "Native screenshot credited a saved step before required project commands.");
    }
    private static async Task MixedCalls()
    {
        foreach (var adapter in new[] { "responses", "compatible", "native" })
        {
            using var f = new ProjectFixture(adapter);
            f.Batch = true;
            var result = await f.Run([new("project_list_files", "{\"directory\":\".\"}")]);
            ProjectFixture.Check(result.Status == RunStatus.Failed && !result.Observations.Any(o => o.Project is not null) && !result.HasVerifiedAssertions && f.Driver.Mutations == 0, "Mixed call turn dispatched work.");
        }
    }
    private static async Task AggregateEvidence()
    {
        using var f = new ProjectFixture("compatible");
        var run = await new AiTestRunner(f.Driver, f.Settings, Path.Combine(f.Root, "aggregate"), agentFactory: path => f.Agent([new("project_list_files", "{\"directory\":\".\"}"), f.Saved(), ProjectChoice.Finish], path)).RunAsync(f.Test);
        ProjectFixture.Check(run.Status == RunStatus.Passed && run.ProjectEvidence.Count == 1 && run.Steps.Count == 2 && run.Steps.All(s => s.Status == RunStatus.Passed), "Project evidence changed flattened UI action counts: " + run.Summary);
        var saved = JsonSerializer.Deserialize<RunResult>(await File.ReadAllTextAsync(Path.Combine(run.ArtifactDirectory, "run.json")), TestyJson.Options)!;
        ProjectFixture.Check(saved.ProjectEvidence[0].Id == run.ProjectEvidence[0].Id && File.Exists(saved.ProjectEvidence[0].EvidencePath), "Project provenance missing from persisted run.");
    }
    private static async Task NativeCoexistence()
    {
        using var f = new ProjectFixture("native");
        f.Test.Steps.Insert(0, new TestStep { Id = "image", Title = "Saved screenshot", Action = StepAction.Screenshot });
        var result = await f.Run([new("project_list_files", "{\"directory\":\".\"}"), new("computer", "{\"type\":\"screenshot\"}"), new("verify_saved_assertion", "{\"stepId\":\"assert\"}"), ProjectChoice.Finish]);
        ProjectFixture.Check(result.Completed && SavedWorkflowVerifier.Verify(f.Test, result.Observations).Complete && f.Executor.Calls == 1, "Native/project/assertion tools did not coexist: " + result.Message);
        ProjectFixture.Check(f.Requests.All(p => p.Contains("\"computer\"", StringComparison.Ordinal) && p.Contains("project_read_file", StringComparison.Ordinal) && p.Contains("verify_saved_assertion", StringComparison.Ordinal)), "Native tool catalog omitted required functions.");
    }
    private static async Task ImpossibleBudgets()
    {
        foreach (var adapter in ProjectFixture.Adapters)
        {
            using var f = new ProjectFixture(adapter, requiredCommand: true);
            f.Settings.MaximumAgentTurns = 3;
            var rejected = false;
            try { AiTestRunner.ValidateExecution(f.Test, f.Settings); } catch (InvalidDataException) { rejected = true; }
            ProjectFixture.Check(rejected, "Public preflight accepted an impossible prerequisite budget.");
            rejected = false;
            try { await f.Agent([f.Command()]).RunAsync("Run required build then saved step.", 3); }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException) { rejected = true; }
            ProjectFixture.Check(rejected && f.CommandCalls == 0 && f.Requests.Count == 0 && f.Driver.Snapshots == 0, "Impossible direct-agent budget caused side effects.");
        }
    }
    private static async Task FailureReviewContext()
    {
        foreach (var adapter in new[] { "responses", "compatible", "native" })
        {
            using var f = new ProjectFixture(adapter);
            f.Test.Steps[0].Value = "Unexpected";
            var result = await f.Run([new("project_read_file", "{\"path\":\"Feature.cs\",\"startLine\":1,\"maxLines\":8}"), f.Saved(), ProjectChoice.Finish]);
            ProjectFixture.Check(result.Status == RunStatus.Failed && !result.Completed && f.Requests.Count == 3, "UI failure review was skipped or changed failure status.");
            using var review = JsonDocument.Parse(f.Requests[2]);
            ProjectFixture.Check(!review.RootElement.TryGetProperty("tools", out _) && f.Requests[2].Contains("ProjectFixtureBusinessRule", StringComparison.Ordinal) && f.Requests[2].Contains("data:image/png", StringComparison.Ordinal), "Failure reviewer lost project evidence or failed screenshot, or received more tools.");
        }
    }
}

internal sealed record ProjectChoice(string Name, string Arguments, bool Done = false)
{
    public static ProjectChoice Finish { get; } = new("observe_application", "{}", true);
}

internal sealed class ProjectFixture : IDisposable
{
    public static readonly string[] Adapters = ["responses", "compatible", "codex", "native"];
    public readonly string Root = Path.Combine(Path.GetTempPath(), "Testy-project-protocol-" + Guid.NewGuid().ToString("N"));
    private readonly string variable = "TESTY_PROJECT_PROTOCOL_" + Guid.NewGuid().ToString("N");
    private readonly string adapter;
    private HttpClient? client;
    public readonly ProjectDriver Driver = new();
    public readonly ProjectExecutor Executor = new();
    public ProviderSettings Settings { get; }
    public TestCase Test { get; } = new() { Name = "Project-informed test", Steps = [new TestStep { Id = "assert", Title = "Verify ready", Action = StepAction.AssertText, Selector = "id:Status", Value = "Ready", TimeoutMs = 200 }] };
    public List<string> Requests { get; } = [];
    public int CommandCalls;
    public bool CommandSucceeds = true;
    public bool Batch;
    public ProjectFixture(string adapter, bool requiredCommand = false)
    {
        this.adapter = adapter;
        var project = Path.Combine(Root, "selected-project"); Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "Feature.cs"), "// ProjectFixtureBusinessRule: Ready means initialized.\npublic class Feature {}\n");
        Environment.SetEnvironmentVariable(variable, "protocol-only-dummy-key");
        Settings = new ProviderSettings
        {
            Kind = adapter == "codex" ? ProviderKind.Codex : adapter == "compatible" ? ProviderKind.Compatible : ProviderKind.OpenAI,
            NativeComputerUse = adapter == "native", Model = "fixture", Endpoint = "http://localhost/mock", ApiKeyEnvironmentVariable = variable, MaximumAgentTurns = 12,
            ProjectTools = new ProjectToolSettings { Enabled = true, RootDirectory = project, Commands = [new ProjectCommandDefinition { Id = "build", Description = "Fixture build", Executable = Environment.ProcessPath!, WorkingDirectory = "." }], RequiredBeforeUiCommands = requiredCommand ? ["build"] : [] }
        };
    }
    public ProjectToolSession Session(string path) => new(Settings.ProjectTools!, path, ["protocol-only-dummy-key"], (_, _, _) =>
    {
        CommandCalls++;
        return Task.FromResult(new ProjectCommandExecution { Status = CommandSucceeds ? ProjectCommandStatus.Completed : ProjectCommandStatus.Failed, ExitCode = CommandSucceeds ? 0 : 1, ProcessId = 4242, CleanupComplete = true, StartedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow, Stdout = CommandSucceeds ? "Build complete" : "Build failed", Message = "Mock host command" });
    });
    public ProjectChoice Saved() => new(adapter == "native" ? "verify_saved_assertion" : "perform_saved_step", "{\"stepId\":\"assert\"}");
    public ProjectChoice Command() => new("project_run_command", "{\"commandId\":\"build\"}");
    public Task<ComputerAgentResult> Run(ProjectChoice[] choices) => Agent(choices).RunAsync("Inspect project and execute every saved step.", 12);
    public IComputerAgent Agent(ProjectChoice[] choices, string? artifactDirectory = null)
    {
        var queue = new Queue<ProjectChoice>(choices); var index = 0;
        var artifacts = artifactDirectory ?? Path.Combine(Root, "agent");
        var session = Session(Path.Combine(artifacts, "project-tools"));
        if (adapter == "codex") return new CodexComputerAgent(Settings, Driver, artifacts, (prompt, schema, image, ct) =>
        {
            Requests.Add(prompt); var next = queue.Dequeue();
            Check(schema.GetRawText().Contains("project_read_file", StringComparison.Ordinal), "Codex decision schema lacks project tools.");
            return Task.FromResult(JsonSerializer.Serialize(new { done = next.Done, explanation = "Fixture decision", toolName = next.Name, arguments = next.Arguments }));
        }, Test, session);
        client = new HttpClient(new ProjectHandler(async request =>
        {
            Requests.Add(await request.Content!.ReadAsStringAsync());
            var choice = queue.Dequeue(); index++;
            return Reply(choice, index.ToString(), adapter != "compatible", Batch ? Saved() : null);
        }));
        return adapter == "native" ? new NativeComputerUseAgent(Settings, Driver, artifacts, Executor, client, Test, session) : new ComputerUseAgent(Settings, Driver, artifacts, client, Test, session);
    }
    public static HttpResponseMessage Reply(ProjectChoice choice, string id, bool responses, ProjectChoice? second = null)
    {
        object body;
        if (choice.Done) body = responses ? new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = "Fixture completed." } } } } } : (object)new { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = "Fixture completed." } } } };
        else if (responses)
        {
            object Call(ProjectChoice value, string callId) => value.Name == "computer"
                ? new { type = "computer_call", call_id = callId, actions = new[] { JsonDocument.Parse(value.Arguments).RootElement.Clone() } }
                : new { type = "function_call", call_id = callId, name = value.Name, arguments = value.Arguments };
            var calls = new List<object> { Call(choice, id) }; if (second is not null) calls.Add(Call(second, id + "b"));
            body = new { status = "completed", output = calls };
        }
        else
        {
            object Call(ProjectChoice value, string callId) => new { id = callId, type = "function", function = new { name = value.Name, arguments = value.Arguments } };
            var calls = new List<object> { Call(choice, id) }; if (second is not null) calls.Add(Call(second, id + "b"));
            body = new { choices = new[] { new { finish_reason = "tool_calls", message = new { role = "assistant", content = (string?)null, tool_calls = calls } } } };
        }
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
    public static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public void Dispose() { client?.Dispose(); Driver.Dispose(); Environment.SetEnvironmentVariable(variable, null); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}
internal sealed class ProjectHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
}
internal sealed class ProjectExecutor : IComputerActionExecutor
{
    public int Calls;
    public Task ExecuteAsync(JsonElement action, CancellationToken cancellationToken = default) { Calls++; return Task.CompletedTask; }
}
internal sealed class ProjectDriver : ITargetDriver
{
    public int Mutations;
    public int Snapshots;
    public TargetInfo? Target { get; } = new() { ProcessId = 42, Title = "Project fixture" };
    public Task<IReadOnlyList<TargetInfo>> GetTargetsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TargetInfo>>([Target!]);
    public Task AttachAsync(int processId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<UiSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) { Snapshots++; return Task.FromResult(new UiSnapshot { Target = TestyJson.Clone(Target!), Elements = [new UiElementInfo { Selector = "id:Status", AutomationId = "Status", Name = "Ready", Value = "Ready", IsEnabled = true, ControlType = "Text", RuntimeId = "status-runtime" }] }); }
    public Task ExecuteAsync(TestStep step, CancellationToken cancellationToken = default) { Mutations++; return Task.CompletedTask; }
    public async Task<string> CaptureAsync(string filePath, CancellationToken cancellationToken = default) { await File.WriteAllBytesAsync(filePath, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6hUAAAAASUVORK5CYII="), cancellationToken); return filePath; }
    public void Dispose() { }
}
