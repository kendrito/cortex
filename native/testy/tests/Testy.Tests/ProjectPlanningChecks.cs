using System.Text.Json;
using Testy.Core;

namespace Testy.Tests;

internal static class ProjectPlanningChecks
{
    private const string Plan = """{"name":"Source-informed readiness","intent":"Verify observable readiness","steps":[{"title":"Verify ready","action":"assertText","selector":"id:Status","value":"Ready","timeoutMs":200,"x":0,"y":0}]}""";
    public static IEnumerable<(string Name, Func<Task> Execute)> All()
    {
        yield return ("project-aware authoring performs actual tool turns before a validated draft for every provider", ToolPlanning);
        yield return ("project-aware planning rejects uninspected submissions and missing command prerequisites", PrematureSubmission);
        yield return ("project-aware planning cannot grant a draft after a failed configured command", FailedCommand);
        yield return ("project-aware planning rejects mixed calls and preserves evidence when turn budget ends", PlanningBoundaries);
    }
    private static async Task ToolPlanning()
    {
        foreach (var adapter in new[] { "responses", "compatible", "codex" })
        {
            using var f = new ProjectFixture(adapter);
            f.Settings.SupportsImages = false;
            var choices = new[] { new ProjectChoice("project_read_file", "{\"path\":\"Feature.cs\",\"startLine\":1,\"maxLines\":8}"), new ProjectChoice("submit_test_plan", Plan) };
            var request = await Request(f);
            var (planner, client) = Planner(f, adapter, choices);
            using (client)
            {
                var test = await planner.CreatePlanAsync(request);
                ProjectFixture.Check(test.Steps.Count == 1 && test.Steps[0].Value == "Ready" && request.ProjectEvidence.Count == 1, "Invalid project-informed plan or provenance.");
                ProjectFixture.Check(request.ProjectEvidence[0].Files.Count == 1 && File.Exists(Path.Combine(request.ProjectArtifactsDirectory, "project-evidence.json")), "Planning file citations were not preserved.");
                ProjectFixture.Check(f.Requests.Count == 2 && f.Requests[1].Contains("ProjectFixtureBusinessRule", StringComparison.Ordinal), "Model did not receive the actual file result before planning.");
                ProjectFixture.Check(f.Requests.All(p => !p.Contains("data:image", StringComparison.Ordinal)), "Text-only project planning sent images.");
                ProjectFixture.Check(PlannerFactory.Create(f.Settings, f.Root) is ProjectAwarePlanner, "Normal planner factory did not enable the project wrapper.");
            }
        }
    }
    private static async Task PrematureSubmission()
    {
        foreach (var adapter in new[] { "responses", "compatible", "codex" })
            foreach (var required in new[] { false, true })
            {
                using var f = new ProjectFixture(adapter, required);
                var choices = required ? new[] { new ProjectChoice("project_list_files", "{\"directory\":\".\"}"), new ProjectChoice("submit_test_plan", Plan) } : [new ProjectChoice("submit_test_plan", Plan)];
                var request = await Request(f);
                var (planner, client) = Planner(f, adapter, choices);
                using (client) await Reject(() => planner.CreatePlanAsync(request));
                ProjectFixture.Check(!File.Exists(Path.Combine(request.ProjectArtifactsDirectory, "draft.json")) && f.CommandCalls == 0, "Premature draft was persisted or command executed automatically.");
            }
    }
    private static async Task FailedCommand()
    {
        foreach (var adapter in new[] { "responses", "compatible", "codex" })
        {
            using var f = new ProjectFixture(adapter) { CommandSucceeds = false };
            var request = await Request(f);
            var (planner, client) = Planner(f, adapter, [f.Command(), new("project_list_files", "{\"directory\":\".\"}"), new("submit_test_plan", Plan)]);
            using (client) await Reject(() => planner.CreatePlanAsync(request));
            ProjectFixture.Check(request.ProjectEvidence.Count == 2 && request.ProjectEvidence[0].BlocksFurtherActions && f.CommandCalls == 1, "Failed planning command was not recorded or diagnosed once.");
        }
    }
    private static async Task PlanningBoundaries()
    {
        foreach (var adapter in new[] { "responses", "compatible" })
        {
            using var f = new ProjectFixture(adapter) { Batch = true };
            var request = await Request(f);
            var (planner, client) = Planner(f, adapter, [new("project_list_files", "{\"directory\":\".\"}")]);
            using (client) await Reject(() => planner.CreatePlanAsync(request));
            ProjectFixture.Check(request.ProjectEvidence.Count == 0, "Multiple planning tools dispatched in one turn.");
        }
        using (var f = new ProjectFixture("compatible"))
        {
            f.Settings.ProjectTools!.MaximumPlanningTurns = 2;
            var request = await Request(f);
            var (planner, client) = Planner(f, "compatible", [new("project_list_files", "{\"directory\":\".\"}"), f.Command()]);
            using (client) await Reject(() => planner.CreatePlanAsync(request));
            ProjectFixture.Check(f.CommandCalls == 0 && request.ProjectEvidence.Count == 1 && File.Exists(Path.Combine(request.ProjectArtifactsDirectory, "project-evidence.json")), "Turn-limit command executed or prior evidence disappeared.");
        }
        using (var f = new ProjectFixture("compatible", requiredCommand: true))
        {
            f.Settings.ProjectTools!.MaximumPlanningTurns = 2;
            var request = await Request(f);
            var (planner, client) = Planner(f, "compatible", [f.Command()]);
            using (client) await Reject(() => planner.CreatePlanAsync(request));
            ProjectFixture.Check(f.CommandCalls == 0 && f.Requests.Count == 0, "Known-impossible planning budget dispatched a command or model request.");
        }
    }
    private static async Task<PlanningRequest> Request(ProjectFixture fixture) => new() { Instructions = "Inspect the implementation and draft a test verifying Ready.", Snapshot = await fixture.Driver.SnapshotAsync(), ProjectArtifactsDirectory = Path.Combine(fixture.Root, "planning") };
    private static (ProjectAwarePlanner Planner, HttpClient? Client) Planner(ProjectFixture fixture, string adapter, ProjectChoice[] choices)
    {
        var queue = new Queue<ProjectChoice>(choices); var index = 0;
        if (adapter == "codex")
            return (new ProjectAwarePlanner(fixture.Settings, fixture.Root, new OfflinePlanner(), decisionProvider: (prompt, schema, image, ct) =>
            {
                fixture.Requests.Add(prompt); var next = queue.Dequeue();
                ProjectFixture.Check(image == "", "Unexpected image for project planning fixture.");
                return Task.FromResult(JsonSerializer.Serialize(new { done = false, explanation = "Inspect before draft", toolName = next.Name, arguments = next.Arguments }));
            }, sessionFactory: path => fixture.Session(Path.Combine(path, "project-tools"))), null);
        var client = new HttpClient(new ProjectHandler(async request =>
        {
            fixture.Requests.Add(await request.Content!.ReadAsStringAsync());
            return ProjectFixture.Reply(queue.Dequeue(), (++index).ToString(), adapter == "responses", fixture.Batch ? new("submit_test_plan", Plan) : null);
        }));
        return (new ProjectAwarePlanner(fixture.Settings, fixture.Root, new OfflinePlanner(), client, sessionFactory: path => fixture.Session(Path.Combine(path, "project-tools"))), client);
    }
    private static async Task Reject(Func<Task<TestCase>> run)
    {
        try { await run(); }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException) { return; }
        throw new Exception("Expected bounded planning rejection.");
    }
}
