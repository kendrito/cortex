using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Testy.Core;
using Testy.Cli;
using Testy.Cli.Mcp;
using Testy.Studio;

namespace Testy.Tests;

internal static class CortexChecks
{
    public static (string Name, Func<Task> Execute)[] All() =>
    [
        ("Cortex provider routing is pinned and settings never persist bridge credentials", Routing),
        ("Cortex model operations isolate async contexts and fail closed without one", Contexts),
        ("Cortex AI planner sends the authenticated route and current context", Planner),
        ("Cortex test revisions prevent stale saves and deletion", Revisions),
        ("Cortex Studio initializes empty workspaces without starter tests and preserves standalone demos", StudioInitialization),
        ("Cortex full workflow catalog marks AI and human-only configuration", Catalog),
        ("Cortex native target launches never inherit private bridge capabilities", TargetEnvironment)
    ];
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> previous = new();
        public EnvironmentScope()
        {
            foreach (var item in new Dictionary<string, string?> { ["TESTY_CORTEX_INTEGRATED"] = "1", [CortexModelBridge.UrlVariable] = "http://127.0.0.1:31999/v1/chat/completions", [CortexModelBridge.TokenVariable] = "private-bridge-test-credential-12345", [CortexModelBridge.ContextVariable] = null })
            { previous[item.Key] = Environment.GetEnvironmentVariable(item.Key); Environment.SetEnvironmentVariable(item.Key, item.Value); }
        }
        public void Dispose() { foreach (var item in previous) Environment.SetEnvironmentVariable(item.Key, item.Value); }
    }
    private static Task Routing()
    {
        using var scope = new EnvironmentScope();
        var settings = new ProviderSettings { Kind = ProviderKind.OpenAI, Model = "malicious", Endpoint = "https://untrusted.example", ApiKeyEnvironmentVariable = "OTHER_KEY", NativeComputerUse = true };
        Check(settings.Kind == ProviderKind.Compatible && settings.Model == "cortex" && !settings.NativeComputerUse, "Workspace overrode host model.");
        var before = JsonSerializer.Serialize(settings, TestyJson.Options);
        Environment.SetEnvironmentVariable(CortexModelBridge.UrlVariable, "http://127.0.0.1:32000/v1/chat/completions");
        Check(before == JsonSerializer.Serialize(settings, TestyJson.Options), "Persistent workload hash depends on transient bridge port.");
        Check(!before.Contains(CortexModelBridge.Token) && !before.Contains("31999") && !before.Contains("untrusted"), "Provider settings persist a secret or endpoint.");
        var child = new ProcessStartInfo("example.exe"); CortexModelBridge.BindChild(child); CortexModelBridge.ScrubTarget(child);
        Check(!child.Environment.Keys.Any(k => k.StartsWith("TESTY_CORTEX_")), "Target inherited bridge capability.");
        Environment.SetEnvironmentVariable(CortexModelBridge.UrlVariable, "https://untrusted.example/v1/chat/completions");
        try { _ = CortexModelBridge.Endpoint; throw new Exception("External bridge accepted."); } catch (InvalidOperationException) { }
        return Task.CompletedTask;
    }
    private static async Task Contexts()
    {
        using var scope = new EnvironmentScope();
        using var request = new HttpRequestMessage(HttpMethod.Post, CortexModelBridge.Endpoint);
        try { CortexModelBridge.BindRequest(request); throw new Exception("Missing operation context was accepted."); } catch (InvalidOperationException) { }
        async Task<string?> Child(string value) { using var context = CortexModelBridge.Enter(value); await Task.Delay(10); return CortexModelBridge.Context; }
        var values = await Task.WhenAll(Child("context-one-00000001"), Child("context-two-00000002"));
        Check(values.SequenceEqual(new[] { "context-one-00000001", "context-two-00000002" }) && CortexModelBridge.Context is null, "Concurrent contexts leaked.");
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Check(request.RequestUri?.AbsoluteUri == CortexModelBridge.Endpoint, "Wrong model endpoint.");
            Check(request.Headers.Authorization?.Parameter == CortexModelBridge.Token, "Wrong auth.");
            Check(request.Headers.GetValues("X-Testy-Cortex-Context").Single() == "planner-context-00001", "Wrong context.");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); Check(body.RootElement.GetProperty("model").GetString() == "cortex", "Workspace model reached wire.");
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"Evidence explains this failure.\"}}]}", Encoding.UTF8, "application/json") };
        }
    }
    private static async Task Planner()
    {
        using var scope = new EnvironmentScope(); using var context = CortexModelBridge.Enter("planner-context-00001"); using var handler = new Handler(); using var http = new HttpClient(handler);
        var result = await new CompatiblePlanner(new ProviderSettings { Model = "ignored" }, http).ExplainAsync(new RunResult { Summary = "Observed failure" });
        Check(handler.Calls == 1 && result.Contains("Evidence"), "Integrated planner did not complete exactly once.");
    }
    private static Task Revisions()
    {
        var path = Path.Combine(Path.GetTempPath(), "testy-cortex-revision-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorkspaceStore(path); var test = new TestCase { Name = "Check", Steps = [new() { Action = StepAction.AssertExists, Selector = "id:Control" }] }; store.SaveTest(test);
            var revision = WorkspaceStore.Revision(test); test.Name = "Changed elsewhere"; store.SaveTest(test);
            try { store.SaveTest(test, revision); throw new Exception("Stale save accepted."); } catch (InvalidDataException) { }
            try { store.DeleteTest(test.Id, revision); throw new Exception("Stale delete accepted."); } catch (InvalidDataException) { }
            store.DeleteTest(test.Id, WorkspaceStore.Revision(test)); Check(store.LoadTests().Count == 0, "Current revision deletion failed.");
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
        return Task.CompletedTask;
    }
    private static Task Catalog()
    {
        var catalog = new McpToolCatalog(null);
        foreach (var name in new[] { "run_test", "draft_test", "refine_test", "explain_run", "run_suite", "pump_jobs", "run_lifecycle", "draft_from_demo", "queue_machine" })
            Check(catalog.Find(name)!.ToNode()["_meta"]!["cortex/usesModel"]!.GetValue<bool>(), name + " is not marked AI-capable.");
        Check(catalog.Find("save_project")!.HumanOnly && catalog.Find("setup_machine")!.HumanOnly, "Privileged config is model-visible.");
        Check(catalog.Find("get_project")!.UsesModel == false, "Read-only settings must not mint AI contexts.");
        return Task.CompletedTask;
    }
    private static Task StudioInitialization()
    {
        var names = new[] { "TESTY_CORTEX_INTEGRATED", CortexModelBridge.UrlVariable, CortexModelBridge.TokenVariable, CortexModelBridge.ContextVariable, CortexGuestRelay.DirectoryVariable };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var directory = Path.Combine(Path.GetTempPath(), "testy-cortex-studio-initialization-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, null);
            Environment.SetEnvironmentVariable("TESTY_CORTEX_INTEGRATED", "1");
            var integrated = new WorkspaceStore(Path.Combine(directory, "integrated"));
            Check(MainWindow.InitializeWorkspace(integrated, 0), "First integrated workspace was not initialized.");
            var marker = Path.Combine(integrated.RootDirectory, ".initialized");
            Check(integrated.LoadTests().Count == 0 && File.ReadAllText(marker) == "Testy workspace initialized", "Integrated startup seeded tests or omitted its initialization marker.");
            Check(!MainWindow.InitializeWorkspace(integrated, 0) && integrated.LoadTests().Count == 0, "Integrated startup initialized the empty workspace again.");

            Environment.SetEnvironmentVariable("TESTY_CORTEX_INTEGRATED", null);
            Check(!CortexModelBridge.Enabled, "Standalone fixture retained Cortex integration.");
            Check(!MainWindow.InitializeWorkspace(integrated, 0) && integrated.LoadTests().Count == 0, "A marked integrated workspace was seeded when later opened standalone.");
            var standalone = new WorkspaceStore(Path.Combine(directory, "standalone"));
            Check(MainWindow.InitializeWorkspace(standalone, 0), "First standalone workspace was not initialized.");
            var seeded = standalone.LoadTests();
            Check(seeded.Select(test => test.Name).Order().SequenceEqual(new[] { "Create a customer", "Failure evidence example", "Inspect customer form", "Required field validation" }.Order()), "Standalone startup no longer creates exactly the four existing demonstrations.");
            Check(File.Exists(Path.Combine(standalone.RootDirectory, ".initialized")), "Standalone initialization marker is missing.");
            var original = seeded.ToDictionary(test => test.Id, WorkspaceStore.Revision);
            Check(!MainWindow.InitializeWorkspace(standalone, seeded.Count), "An existing standalone workspace was initialized again.");
            Check(standalone.LoadTests().All(test => original[test.Id] == WorkspaceStore.Revision(test)), "Existing demonstration tests changed on reopening.");
            foreach (var test in seeded) standalone.DeleteTest(test.Id);
            Check(!MainWindow.InitializeWorkspace(standalone, 0) && standalone.LoadTests().Count == 0, "Deleting demonstrations caused them to be recreated.");

            var existing = new WorkspaceStore(Path.Combine(directory, "existing"));
            var userTest = new TestCase { Name = "User-authored test", Steps = [new() { Action = StepAction.AssertExists, Selector = "id:Control" }] };
            existing.SaveTest(userTest); var revision = WorkspaceStore.Revision(userTest);
            foreach (var integratedMode in new[] { false, true })
            {
                Environment.SetEnvironmentVariable("TESTY_CORTEX_INTEGRATED", integratedMode ? "1" : null);
                Check(!MainWindow.InitializeWorkspace(existing, 1), "A populated workspace was initialized.");
                Check(existing.LoadTests().Count == 1 && WorkspaceStore.Revision(existing.LoadTests()[0]) == revision && !File.Exists(Path.Combine(existing.RootDirectory, ".initialized")), "Existing user tests or initialization state were changed.");
            }
        }
        finally
        {
            foreach (var item in previous) Environment.SetEnvironmentVariable(item.Key, item.Value);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        return Task.CompletedTask;
    }
    private static async Task TargetEnvironment()
    {
        using var scope = new EnvironmentScope();
        Environment.SetEnvironmentVariable(CortexModelBridge.ContextVariable, "native-launch-context-00001");
        var directory = Path.Combine(Path.GetTempPath(), "testy-cortex-environment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var tests = output.Parent!.Parent!.Parent!.Parent!;
        var fixture = Path.Combine(tests.FullName, "Testy.IdleFixture", "bin", output.Parent.Name, output.Name, "Testy.IdleFixture.exe");
        Check(File.Exists(fixture), "Environment fixture was not built.");
        try
        {
            foreach (var mode in new[] { "target", "visible-owned", "visible-independent" })
            {
                using var job = new OwnedProcessJob();
                var report = Path.Combine(directory, mode + ".json");
                var start = new ProcessStartInfo(fixture) { UseShellExecute = false, WorkingDirectory = directory };
                start.ArgumentList.Add("--cortex-environment-report"); start.ArgumentList.Add(report);
                start.Environment["TESTY_ENVIRONMENT_TEST_UNICODE"] = "café-界-🙂";
                start.Environment["TESTY_ENVIRONMENT_TEST_REMOVED"] = null;
                start.Environment["testy_cortex_extra"] = "must-not-reach-target";
                using var child = mode == "target" ? job.StartTarget(start) : OwnedProcessJob.StartVisible(start, mode == "visible-owned" ? job : null);
                try
                {
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    Check(child.ExitCode == 0, mode + " helper failed.");
                    using var actual = JsonDocument.Parse(await File.ReadAllTextAsync(report));
                    Check(actual.RootElement.GetProperty("bridgeNames").GetArrayLength() == 0, mode + " inherited bridge credentials or context.");
                    Check(actual.RootElement.GetProperty("unicode").GetString() == "café-界-🙂", mode + " lost the explicit Unicode environment.");
                    Check(actual.RootElement.GetProperty("removed").ValueKind == JsonValueKind.Null, mode + " retained a removed environment value.");
                }
                finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } }
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}
