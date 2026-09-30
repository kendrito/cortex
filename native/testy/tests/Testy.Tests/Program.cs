using System.Diagnostics;
using System.Text.Json;
using Testy.Core;

namespace Testy.Tests;

internal static class Program
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "Testy-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly List<object> Results = [];

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--project-command-helper") return await ProjectCommandChecks.RunHelperAsync(args);
        if (args.Length > 0 && args[0] == "--operations-helper") return await OperationsChecks.RunHelperAsync(args);
        Directory.CreateDirectory(Root);
        var checks = new (string Name, Func<Task> Execute)[]
        {
            ("runner executes ordered actions and captures evidence", RunnerPass),
            ("assertion mismatch fails and stops later actions", RunnerFailure),
            ("assertions poll until delayed state appears", EventualAssertion),
            ("missing selector times out without invoking an action", MissingSelector),
            ("cancellation stops before later actions", Cancellation),
            ("evidence capture failure fails the run", EvidenceFailure),
            ("runner escapes untrusted text in HTML report", HtmlEscaping),
            ("workspace test, run and settings round trip", StorageRoundTrip),
            ("workspace rejects path traversal IDs", StorageTraversal),
            ("workspace reports corrupt saved data", StorageCorruption),
            ("schema rejects invalid actions, timeouts and empty tests", Validation),
            ("plan parser accepts valid structured plans", PlanParsing),
            ("plan parser rejects invalid and hostile plans", HostilePlans),
            ("JSON round trip preserves Unicode and special characters", JsonRoundTrip),
            ("step observer receives evidence and preserves authoritative outcomes", StepObserver),
            ("draft storage allows incomplete editing but execution rejects it", DraftStorage),
            ("truncated trees cannot prove selector absence", TruncatedTree)
        }.Concat(ProviderChecks.All()).Concat(AiTestRunnerChecks.All()).Concat(ComputerAgentChecks.All()).Concat(WorkflowCoverageChecks.All()).Concat(SuiteChecks.All()).Concat(SelectorChecks.All()).Concat(FailureDiagnosticChecks.All()).Concat(AdvancedCoreChecks.All()).Concat(BoundWorkflowChecks.All()).Concat(NativeHybridChecks.All()).Concat(EnterpriseCoreChecks.All()).Concat(SelectorRecoveryChecks.All())
            .Concat(ProjectCommandChecks.All()).Concat(ProjectFileToolChecks.All()).Concat(ProjectAgentChecks.All()).Concat(ProjectPlanningChecks.All()).Concat(ProjectWorkspaceChecks.All()).Concat(ProjectEvidenceChecks.All()).Concat(SnapshotReadinessChecks.All())
            .Concat(ProductShellChecks.All()).Concat(LifecycleChecks.All()).Concat(OperationsChecks.All()).Concat(TargetOperationsChecks.All()).Concat(WorkspaceMaintenanceChecks.All()).Concat(ScreenshotPairChecks.All()).Concat(CompatibleSchemaChecks.All()).Concat(McpChecks.All()).Concat(McpChecks.AppChecks()).Concat(CortexChecks.All()).Concat(CortexRelayChecks.All()).Concat(CortexAiChecks.All()).ToArray();
        // --only TEXT runs the checks whose name contains TEXT (a development convenience; the report is written either way).
        var only = Array.IndexOf(args, "--only") is var index && index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        if (only is not null) checks = checks.Where(c => c.Item1.Contains(only, StringComparison.OrdinalIgnoreCase)).ToArray();
        var failed = 0;
        foreach (var (name, execute) in checks)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                await execute();
                Results.Add(new { name, passed = true, durationMs = timer.ElapsedMilliseconds });
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failed++;
                Results.Add(new { name, passed = false, durationMs = timer.ElapsedMilliseconds, error = exception.ToString() });
                Console.WriteLine($"FAIL {name}: {exception.Message}");
            }
        }
        var report = new { timestamp = DateTimeOffset.UtcNow, total = checks.Length, passed = checks.Length - failed, failed, results = Results };
        if (Array.IndexOf(args, "--report") is var reportIndex && reportIndex >= 0 && reportIndex + 1 < args.Length)
        {
            var output = Path.GetFullPath(args[reportIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, TestyJson.Options));
        }
        Console.WriteLine($"{checks.Length - failed}/{checks.Length} passed.");
        // This harness created this unique temporary directory and owns all of its contents.
        Directory.Delete(Root, recursive: true);
        return failed == 0 ? 0 : 1;
    }

    private static async Task RunnerPass()
    {
        using var driver = new FakeDriver();
        var run = await Runner(driver).RunAsync(Case("Create customer",
            Step(StepAction.TypeText, "id:CustomerName", "Zoë <QA>"),
            Step(StepAction.Click, "id:AddCustomer"),
            Step(StepAction.AssertText, "id:StatusMessage", "Customer added: Zoë <QA>")));
        Check(run.Status == RunStatus.Passed, run.Summary);
        Check(driver.Executed.Select(x => x.Action).SequenceEqual(new[] { StepAction.TypeText, StepAction.Click }), "Actions were not exactly once and in order.");
        Check(run.Steps.Count == 3 && run.Steps.All(x => x.Status == RunStatus.Passed), "Unexpected step results.");
        Check(run.Steps.All(x => File.Exists(x.ScreenshotPath) && x.Snapshot is not null), "Each step must retain screenshot and tree evidence.");
        Check(run.FinishedAt is not null, "Run did not finish.");
    }

    private static async Task RunnerFailure()
    {
        using var driver = new FakeDriver();
        var run = await Runner(driver).RunAsync(Case("Fails", Step(StepAction.AssertText, "id:StatusMessage", "wrong", 160), Step(StepAction.Click, "id:AddCustomer")));
        Check(run.Status == RunStatus.Failed, "Incorrect assertion passed.");
        Check(driver.Executed.Count == 0, "Later actions ran after failure.");
        Check(run.Steps.Any(x => x.Status == RunStatus.Failed && !string.IsNullOrWhiteSpace(x.Message)), "Failure has no explanation.");
    }

    private static async Task EventualAssertion()
    {
        using var driver = new FakeDriver { ReadyAfterSnapshots = 3 };
        var run = await Runner(driver).RunAsync(Case("Eventually ready", Step(StepAction.AssertText, "id:StatusMessage", "Ready", 1500)));
        Check(run.Status == RunStatus.Passed, run.Summary);
        Check(driver.SnapshotCount >= 3, "Assertion never polled state.");
        Check(driver.Executed.Count == 0, "Assertions must not mutate the target.");
    }

    private static async Task MissingSelector()
    {
        using var driver = new FakeDriver();
        var timer = Stopwatch.StartNew();
        var run = await Runner(driver).RunAsync(Case("Missing", Step(StepAction.Click, "id:DoesNotExist", timeout: 180)));
        Check(run.Status == RunStatus.Failed, "Missing target passed.");
        Check(timer.Elapsed < TimeSpan.FromSeconds(3), "Timeout was not bounded.");
        Check(driver.Executed.Count == 0, "An action was issued without an identified target.");
    }

    private static async Task Cancellation()
    {
        using var driver = new FakeDriver();
        using var cancellation = new CancellationTokenSource(80);
        var run = await Runner(driver).RunAsync(Case("Cancelled", Step(StepAction.Wait, value: "5000"), Step(StepAction.Click, "id:AddCustomer")), null, cancellation.Token);
        Check(run.Status == RunStatus.Cancelled, "Cancelled run has the wrong status.");
        Check(driver.Executed.Count == 0, "Action ran after cancellation.");
    }

    private static async Task EvidenceFailure()
    {
        using var driver = new FakeDriver { FailCapture = true };
        var run = await Runner(driver).RunAsync(Case("Evidence must exist", Step(StepAction.AssertExists, "id:CustomerName")));
        Check(run.Status == RunStatus.Failed, "Run passed despite missing evidence.");
    }

    private static async Task HtmlEscaping()
    {
        using var driver = new FakeDriver();
        var run = await Runner(driver).RunAsync(Case("<script>alert('owned')</script>", Step(StepAction.AssertExists, "id:CustomerName")));
        var htmlPath = Directory.EnumerateFiles(run.ArtifactDirectory, "*.html").Single();
        var html = await File.ReadAllTextAsync(htmlPath);
        Check(!html.Contains("<script>alert('owned')</script>", StringComparison.Ordinal), "Untrusted title became HTML markup.");
        Check(html.Contains("&lt;script&gt;", StringComparison.Ordinal), "Report did not include escaped title.");
    }

    private static Task StorageRoundTrip()
    {
        var store = new WorkspaceStore(Path.Combine(Root, "roundtrip"));
        var test = Case("Unicode café 日本語", Step(StepAction.AssertExists, "id:CustomerName"));
        store.SaveTest(test);
        Check(store.LoadTests().Single().Name == test.Name, "Test round trip lost data.");
        test.Name = "Edited";
        store.SaveTest(test);
        Check(store.LoadTests().Single().Name == "Edited", "Atomic overwrite duplicated or lost the test.");
        var run = new RunResult { TestId = test.Id, TestName = test.Name, Status = RunStatus.Failed, Summary = "Expected failure" };
        store.SaveRun(run);
        Check(store.LoadRuns().Single().Status == RunStatus.Failed, "Run round trip lost status.");
        store.SaveSettings(new ProviderSettings { Kind = ProviderKind.Compatible, Model = "local-model", ApiKeyEnvironmentVariable = "TESTY_TEST_TOKEN" });
        Check(store.LoadSettings().Model == "local-model", "Settings round trip failed.");
        store.DeleteTest(test.Id);
        Check(!store.LoadTests().Any(), "Deleted test remains.");
        return Task.CompletedTask;
    }

    private static Task StorageTraversal()
    {
        var store = new WorkspaceStore(Path.Combine(Root, "traversal"));
        foreach (var id in new[] { "../escape", "..\\escape", "C:\\escape", "a/b", "a:b", new string('x', 101) })
        {
            var test = Case("Bad ID", Step(StepAction.AssertExists, "id:CustomerName"));
            test.Id = id;
            Throws(() => store.SaveTest(test));
            Throws(() => store.DeleteTest(id));
        }
        Check(!File.Exists(Path.Combine(Root, "escape.json")), "Store escaped its root.");
        return Task.CompletedTask;
    }

    private static Task StorageCorruption()
    {
        var path = Path.Combine(Root, "corrupt");
        var store = new WorkspaceStore(path);
        Directory.CreateDirectory(Path.Combine(path, "tests"));
        File.WriteAllText(Path.Combine(path, "tests", "broken.json"), "{\"steps\":");
        Throws(() => store.LoadTests());
        return Task.CompletedTask;
    }

    private static Task Validation()
    {
        Throws(() => TestValidator.Validate(new TestCase()));
        Throws(() => TestValidator.Validate(Case("Invalid", Step((StepAction)999, "id:x"))));
        Throws(() => TestValidator.Validate(Case("Invalid", Step(StepAction.Click, "id:x", timeout: -1))));
        Throws(() => TestValidator.Validate(Case("Invalid", Step(StepAction.Click, ""))));
        Throws(() => TestValidator.Validate(Case("Invalid", Step(StepAction.Wait, value: "90000000"))));
        TestValidator.Validate(Case("Valid", Step(StepAction.AssertExists, "id:CustomerName")));
        return Task.CompletedTask;
    }

    private static Task PlanParsing()
    {
        const string json = """{"name":"Generated","intent":"Verify the field","steps":[{"title":"Name field exists","action":"assertExists","selector":"id:CustomerName","value":"","timeoutMs":1000,"x":0,"y":0}]}""";
        var plan = PlanCodec.Parse(json, new PlanningRequest { Instructions = "Verify the name field." });
        Check(plan.Steps.Count == 1 && plan.Steps[0].Selector == "id:CustomerName", "Plan parse lost steps.");
        return Task.CompletedTask;
    }

    private static Task HostilePlans()
    {
        foreach (var json in new[]
        {
            "null", "not JSON", "{\"name\":\"empty\",\"steps\":[]}",
            "{\"name\":\"bad\",\"steps\":[{\"action\":\"RunShell\",\"value\":\"shutdown\"}]}",
            "{\"name\":\"bad\",\"steps\":[{\"action\":999,\"selector\":\"id:x\"}]}",
            "{\"name\":\"bad\",\"steps\":[{\"action\":\"Click\",\"selector\":\"id:x\",\"timeoutMs\":-1}]}"
        }) Throws(() => PlanCodec.Parse(json, new PlanningRequest()));
        return Task.CompletedTask;
    }

    private static Task JsonRoundTrip()
    {
        var test = Case("a \"quoted\" 日本語\nline", Step(StepAction.TypeText, "id:CustomerName", "back\\slash\t♥"));
        var cloned = TestyJson.Clone(test);
        Check(cloned.Name == test.Name && cloned.Steps[0].Value == test.Steps[0].Value, "JSON round trip changed text.");
        return Task.CompletedTask;
    }

    private static async Task StepObserver()
    {
        using var driver = new FakeDriver();
        var runner = Runner(driver);
        var observations = 0;
        runner.StepObserver = (observed, token) =>
        {
            observations++;
            Check(observed.Steps.Last().Snapshot is not null && File.Exists(observed.Steps.Last().ScreenshotPath), "Observer ran before evidence was captured.");
            observed.Status = RunStatus.Failed;
            observed.AiAnalysis = "Step evidence reviewed.";
            return Task.CompletedTask;
        };
        var result = await runner.RunAsync(Case("Observed", Step(StepAction.AssertExists, "id:CustomerName"), Step(StepAction.AssertExists, "id:AddCustomer")));
        Check(result.Status == RunStatus.Passed && observations == 2, "Observer changed authoritative result or missed a step.");
        Check(result.AiAnalysis.Contains("Step evidence reviewed.", StringComparison.Ordinal), "Review text was not retained.");
    }

    private static Task DraftStorage()
    {
        var store = new WorkspaceStore(Path.Combine(Root, "draft"));
        var draft = new TestCase { Name = "Draft without steps" };
        store.SaveDraft(draft);
        Check(store.LoadTests().Single().Steps.Count == 0, "Draft could not round trip.");
        Throws(() => TestValidator.Validate(draft));
        draft.Id = "../escape";
        Throws(() => store.SaveDraft(draft));
        return Task.CompletedTask;
    }

    private static async Task TruncatedTree()
    {
        using var driver = new FakeDriver { Truncated = true };
        var result = await Runner(driver).RunAsync(Case("Absent in partial tree", Step(StepAction.AssertNotExists, "id:Missing")));
        Check(result.Status == RunStatus.Failed && result.Steps[0].Message.Contains("truncated", StringComparison.OrdinalIgnoreCase), "Partial snapshot was accepted as proof of absence.");
    }

    private static TestRunner Runner(ITargetDriver driver) => new(driver, Path.Combine(Root, "runs"));
    private static TestCase Case(string name, params TestStep[] steps) => new() { Name = name, Intent = name, Steps = steps.ToList() };
    private static TestStep Step(StepAction action, string selector = "", string value = "", int timeout = 1500) =>
        new() { Title = action + " " + selector, Action = action, Selector = selector, Value = value, TimeoutMs = timeout };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or JsonException or FormatException) { return; }
        throw new InvalidOperationException("Expected invalid input to be rejected.");
    }
}

internal sealed class FakeDriver : ITargetDriver
{
    private readonly Dictionary<string, string> values = new() { ["CustomerName"] = "", ["StatusMessage"] = "Idle", ["AddCustomer"] = "Add customer" };
    public TargetInfo? Target { get; } = new() { ProcessId = 12345, Title = "Fake owned fixture", ProcessName = "FakeLab" };
    public List<TestStep> Executed { get; } = [];
    public int SnapshotCount { get; private set; }
    public int ReadyAfterSnapshots { get; init; } = int.MaxValue;
    public bool FailCapture { get; init; }
    public bool Truncated { get; init; }
    public Task<IReadOnlyList<TargetInfo>> GetTargetsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TargetInfo>>([Target!]);
    public Task AttachAsync(int processId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<UiSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SnapshotCount++;
        if (SnapshotCount >= ReadyAfterSnapshots) values["StatusMessage"] = "Ready";
        return Task.FromResult(new UiSnapshot { Target = Target!, IsTruncated = Truncated, Elements = values.Select(x => new UiElementInfo
        {
            AutomationId = x.Key, Selector = "id:" + x.Key, Name = x.Value, Value = x.Value, IsEnabled = true,
            ControlType = x.Key == "AddCustomer" ? "Button" : "Edit"
        }).ToList() });
    }
    public Task ExecuteAsync(TestStep step, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Executed.Add(TestyJson.Clone(step));
        if (step.Action == StepAction.TypeText) values[step.Selector[3..]] = step.Value;
        if (step.Action == StepAction.Click) values["StatusMessage"] = "Customer added: " + values["CustomerName"];
        return Task.CompletedTask;
    }
    public async Task<string> CaptureAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (FailCapture) throw new IOException("Synthetic capture failure");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllBytesAsync(filePath, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII="), cancellationToken);
        return filePath;
    }
    public void Dispose() { }
}
