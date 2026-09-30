using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Testy.Core;

namespace Testy.Tests;

internal static class FailureDiagnosticChecks
{
    public static IEnumerable<(string Name, Func<Task> Execute)> All()
    {
        yield return ("failure diagnostics preserve exact assertion observations and report evidence", AssertionFacts);
        yield return ("capture failure is an evidence problem after a completed input", EvidenceFailure);
        yield return ("cancellation during input records an unknown outcome without a product verdict", CancelledInput);
        yield return ("ignored driver cancellation returns at deadline with uncertain input and no retry", IgnoredCancellation);
        yield return ("selector diagnostics distinguish missing ambiguous disabled and truncated observations", SelectorFailures);
        yield return ("AI workflow omissions remain verification failures rather than application defects", MissingAiWorkflow);
        yield return ("HTTP model failures preserve provider classification through the AI runner", ProviderFailure);
        yield return ("native dispatch failures retain uncertain input diagnostics", NativeFailure);
        yield return ("failure explanation context separates observations from prior model hypotheses", ExplanationContext);
        yield return ("terminal failed or cancelled runs cannot emit all-green JUnit", TerminalReportVerdicts);
        yield return ("snapshot deadline retains an earlier mismatch as supporting evidence", SnapshotDeadlineFacts);
    }

    private static async Task AssertionFacts() => await WithRoot(async root =>
    {
        using var driver = new Driver { Text = "<actual&>" };
        var expected = "<expected&>";
        var test = Test(new TestStep { Title = "Baseline screenshot", Action = StepAction.Screenshot }, Assertion(expected));
        var result = await new TestRunner(driver, root).RunAsync(test);
        var diagnostic = result.FailureDiagnostics.Single(d => d.Category == FailureCategory.AssertionMismatch);
        Check(result.Status == RunStatus.Failed && diagnostic.Expected == expected && diagnostic.Actual == "<actual&>", "Assertion facts were not preserved exactly.");
        Check(diagnostic.Comparison == "case-sensitive exact text" && diagnostic.StepIndex == 1 && diagnostic.LastSuccessfulStepIndex == 0 && diagnostic.LastSuccessfulStepTitle == "Baseline screenshot", "Failure context is incorrect.");
        Check(diagnostic.ActionOutcome == ActionOutcome.NotDispatched && driver.InputCount == 0, "Assertion was represented as dispatched input.");
        Check(diagnostic.Evidence.Any(e => e.Kind == "observation used to decide failure" && File.Exists(e.Path)) && diagnostic.Evidence.Any(e => e.Kind == "post-step screenshot" && File.Exists(e.Path)), "Failing observation and screenshot references missing.");
        Check(diagnostic.CauseAssessment.Contains("no root cause", StringComparison.OrdinalIgnoreCase) && diagnostic.SuggestedNextChecks.Count > 0, "Observation was represented as a certain root cause.");
        result.Steps[1].Snapshot!.Elements.Single(e => e.Selector == "id:Status").Value = expected;
        TestRunner.WriteReports(result);
        Check(diagnostic.Actual == "<actual&>" && result.Status == RunStatus.Failed && test.Steps[1].Value == expected, "A later snapshot changed the failure fact, verdict or saved expectation.");
        var saved = JsonSerializer.Deserialize<RunResult>(File.ReadAllText(Path.Combine(result.ArtifactDirectory, "run.json")), TestyJson.Options)!;
        Check(saved.FailureDiagnostics.Single().Actual == "<actual&>" && result.Summary.Contains("Diagnosis:", StringComparison.Ordinal), "JSON or existing UI summary lost structured diagnosis.");
        var html = File.ReadAllText(Path.Combine(result.ArtifactDirectory, "report.html"));
        Check(html.Contains("&lt;actual&amp;&gt;", StringComparison.Ordinal) && !html.Contains("<actual&>", StringComparison.Ordinal), "Diagnostic HTML did not escape observed text.");
        var failure = XDocument.Load(Path.Combine(result.ArtifactDirectory, "junit.xml")).Descendants("failure").Single();
        Check((string?)failure.Attribute("type") == nameof(FailureCategory.AssertionMismatch) && failure.Value.Contains("Observed: <actual&>", StringComparison.Ordinal), "JUnit omitted failure classification or observed value.");
    });

    private static async Task EvidenceFailure() => await WithRoot(async root =>
    {
        using var driver = new Driver { CaptureFails = true };
        var run = await new TestRunner(driver, root).RunAsync(Test(Click(), Assertion("Ready")));
        var diagnostic = run.FailureDiagnostics.Single();
        Check(run.Status == RunStatus.Failed && diagnostic.Category == FailureCategory.EvidenceUnavailable && diagnostic.ActionOutcome == ActionOutcome.Completed, "Completed input plus missing capture was misclassified.");
        Check(diagnostic.Expected is null && diagnostic.Actual is null && diagnostic.CauseAssessment.Contains("does not establish an application defect", StringComparison.Ordinal), "Missing evidence invented an assertion result or app defect.");
        Check(driver.InputCount == 1 && run.Steps[1].Status == RunStatus.Skipped, "Evidence failure continued or repeated input.");
    });

    private static async Task CancelledInput() => await WithRoot(async root =>
    {
        using var driver = new Driver { BlockInput = true };
        using var cancellation = new CancellationTokenSource();
        var task = new TestRunner(driver, root).RunAsync(Test(Click(), Assertion("Ready")), cancellationToken: cancellation.Token);
        await driver.InputStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var run = await task;
        var diagnostic = run.FailureDiagnostics.Single(d => d.Category == FailureCategory.Cancelled);
        Check(run.Status == RunStatus.Cancelled && run.Steps[0].Status == RunStatus.Cancelled && diagnostic.ActionOutcome == ActionOutcome.Unknown, "Cancellation lost uncertain dispatch state.");
        Check(diagnostic.CauseAssessment.Contains("partially", StringComparison.Ordinal) && diagnostic.Actual is null && driver.InputCount == 1, "Cancellation claimed a known result or repeated input.");
        Check(XDocument.Load(Path.Combine(run.ArtifactDirectory, "junit.xml")).Descendants("skipped").Any(), "Cancelled run produced green-only JUnit.");
    });

    private static async Task IgnoredCancellation() => await WithRoot(async root =>
    {
        using var driver = new Driver { IgnoreCancellation = true };
        var click = Click(); click.TimeoutMs = 100;
        var running = new TestRunner(driver, root).RunAsync(Test(click, Assertion("Ready")));
        var run = await running.WaitAsync(TimeSpan.FromSeconds(2));
        var diagnostic = run.FailureDiagnostics.Single();
        Check(run.Status == RunStatus.Failed && diagnostic.Category == FailureCategory.AutomationTimeout && diagnostic.ActionOutcome == ActionOutcome.Unknown, "Uncooperative dispatch did not return an uncertain deadline failure.");
        Check(driver.InputCount == 1 && !driver.PendingInput.Task.IsCompleted && run.Steps[1].Status == RunStatus.Skipped, "Driver was retried or represented as forcibly stopped.");
        driver.PendingInput.SetResult();
        await driver.LateInputCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(driver.LateSideEffect && run.Status == RunStatus.Failed && diagnostic.ActionOutcome == ActionOutcome.Unknown, "Late side effect changed the authoritative recorded result.");
    });

    private static async Task SelectorFailures() => await WithRoot(async root =>
    {
        foreach (var scenario in new[] { "missing", "ambiguous", "disabled", "truncated" })
        {
            using var driver = new Driver { SelectorScenario = scenario };
            var run = await new TestRunner(driver, Path.Combine(root, scenario)).RunAsync(Test(Click()));
            var expected = scenario switch { "missing" => FailureCategory.SelectorNotFound, "ambiguous" => FailureCategory.SelectorAmbiguous, "disabled" => FailureCategory.ControlNotReady, _ => FailureCategory.EvidenceUnavailable };
            Check(run.FailureDiagnostics[0].Category == expected && driver.InputCount == 0, $"{scenario} was misclassified or dispatched input.");
            Check(run.FailureDiagnostics[0].ActionOutcome == ActionOutcome.NotDispatched, "Readiness failure claimed a dispatched input.");
        }
    });

    private static async Task MissingAiWorkflow() => await WithRoot(async root =>
    {
        using var driver = new Driver();
        var assertion = Assertion("Ready");
        var local = await new TestRunner(driver, Path.Combine(root, "action")).RunAsync(Test(assertion));
        var observation = new ComputerToolObservation { ToolName = "perform_ui_action", Status = "Passed", Execution = local, Snapshot = local.Steps[0].Snapshot, ScreenshotPath = local.Steps[0].ScreenshotPath };
        var agent = new Agent(new ComputerAgentResult { Completed = true, Status = RunStatus.Pending, Message = "The database is broken.", Observations = [observation] });
        var saved = Test(Click(), assertion);
        var run = await new AiTestRunner(driver, new ProviderSettings(), Path.Combine(root, "run"), agentFactory: _ => agent).RunAsync(saved);
        var diagnostic = run.FailureDiagnostics.Single(d => d.Category == FailureCategory.WorkflowNotVerified);
        Check(run.Status == RunStatus.Failed && diagnostic.Actual is null && !diagnostic.ObservedFact.Contains("database", StringComparison.OrdinalIgnoreCase), "Model opinion became an authoritative failure fact.");
        Check(diagnostic.LastSuccessfulStepIndex == 0 && run.AiAnalysis == "The database is broken." && saved.Steps[1].Value == "Ready", "Workflow diagnostics lost prior evidence or changed expectations.");
        Check(File.ReadAllText(Path.Combine(run.ArtifactDirectory, "report.html")).Contains("hypotheses, not a verdict", StringComparison.Ordinal), "Model explanation was not labelled supplementary.");
    });

    private static async Task ProviderFailure() => await WithRoot(async root =>
    {
        using var driver = new Driver();
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"error\":{\"message\":\"Fixture rate limit\"}}") });
        using var client = new HttpClient(handler);
        var settings = new ProviderSettings { Kind = ProviderKind.Compatible, Endpoint = "http://localhost/mock", Model = "fixture", ApiKeyEnvironmentVariable = "", SupportsImages = false };
        var run = await new AiTestRunner(driver, settings, root, agentFactory: directory => new ComputerUseAgent(settings, driver, directory, client)).RunAsync(Test(Assertion("Ready")));
        var diagnostic = run.FailureDiagnostics.Single(d => d.Category == FailureCategory.ProviderFailure);
        Check(run.Status == RunStatus.Failed && diagnostic.ObservedFact.Contains("429", StringComparison.Ordinal) && diagnostic.ActionOutcome == ActionOutcome.NotDispatched && driver.InputCount == 0, "Provider error was misattributed to application input.");
    });

    private static async Task NativeFailure() => await WithRoot(async root =>
    {
        using var driver = new Driver();
        var variable = "TESTY_DIAGNOSTIC_DUMMY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "dummy");
        try
        {
            var calls = 0;
            using var handler = new Handler(_ =>
            {
                calls++;
                return Json(calls == 1
                    ? "{\"status\":\"completed\",\"output\":[{\"type\":\"computer_call\",\"call_id\":\"one\",\"actions\":[{\"type\":\"click\",\"x\":0,\"y\":0,\"button\":\"left\"}]}]}"
                    : "{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"Hypothesis: input interrupted.\"}]}]}");
            });
            using var client = new HttpClient(handler);
            var settings = new ProviderSettings { Kind = ProviderKind.OpenAI, Model = "fixture", ApiKeyEnvironmentVariable = variable };
            var result = await new NativeComputerUseAgent(settings, driver, root, new FailedExecutor(), client).RunAsync("Click once.", 4);
            var diagnostic = result.Observations.Last().Execution!.FailureDiagnostics.Single();
            Check(result.Status == RunStatus.Failed && diagnostic.ActionOutcome == ActionOutcome.Unknown && diagnostic.Category == FailureCategory.AutomationError, "Native input interruption was reported as known completion or application defect.");
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    });

    private static async Task ExplanationContext() => await WithRoot(async root =>
    {
        using var driver = new Driver { Text = "Observed" };
        var run = await new TestRunner(driver, root).RunAsync(Test(Assertion("Expected")));
        run.AiAnalysis = "UNVERIFIED PRIOR MODEL ROOT CAUSE";
        var prompt = PlannerPrompt.Explain(run);
        Check(prompt.Contains("Facts", StringComparison.Ordinal) && prompt.Contains("Hypotheses", StringComparison.Ordinal) && prompt.Contains("failureDiagnostics", StringComparison.Ordinal), "Explanation lacks factual diagnostic boundaries.");
        Check(prompt.Contains("Expected", StringComparison.Ordinal) && prompt.Contains("Observed", StringComparison.Ordinal) && !prompt.Contains(run.AiAnalysis, StringComparison.Ordinal), "Prior model opinion was promoted into recorded facts.");
    });

    private static async Task TerminalReportVerdicts() => await WithRoot(root =>
    {
        foreach (var status in new[] { RunStatus.Failed, RunStatus.Cancelled })
        {
            var run = new RunResult { TestName = "Terminal report fixture", Status = status, Summary = "Session ended.", ArtifactDirectory = Path.Combine(root, status.ToString()), FinishedAt = DateTimeOffset.UtcNow };
            Directory.CreateDirectory(run.ArtifactDirectory);
            if (status == RunStatus.Cancelled) run.Steps.Add(new StepResult { Status = RunStatus.Passed, Message = "The earlier step completed." });
            TestRunner.WriteReports(run);
            var xml = XDocument.Load(Path.Combine(run.ArtifactDirectory, "junit.xml"));
            var suite = xml.Descendants("testsuite").Single();
            Check(xml.Descendants(status == RunStatus.Cancelled ? "error" : "failure").Any(), "Terminal run status produced an all-green JUnit report.");
            Check((int)suite.Attribute("tests")! == run.Steps.Count + 1 && run.Steps.All(s => s.Status == RunStatus.Passed), "Synthetic report verdict modified actual step records.");
            Check(suite.Descendants("property").Any(p => (string?)p.Attribute("name") == "testy.run.status" && (string?)p.Attribute("value") == status.ToString()), "JUnit lost authoritative run status.");
        }
        var legacy = new RunResult { Status = RunStatus.Failed, Steps = [new StepResult { Step = Click(), Status = RunStatus.Failed, Message = "Legacy mutation failure without structured evidence." }] };
        FailureDiagnostics.Refresh(legacy);
        Check(legacy.FailureDiagnostics.Single().Category == FailureCategory.Unknown && legacy.FailureDiagnostics.Single().ActionOutcome == ActionOutcome.Unknown, "Legacy failed input incorrectly claimed it was never dispatched.");
        return Task.CompletedTask;
    });
    private static async Task SnapshotDeadlineFacts() => await WithRoot(async root =>
    {
        using var driver = new Driver { Text = "Before timeout", SlowSecondSnapshot = true };
        var assertion = Assertion("Expected"); assertion.TimeoutMs = 250;
        var run = await new TestRunner(driver, root).RunAsync(Test(assertion));
        Check(run.Status == RunStatus.Failed && run.FailureDiagnostics[0].Category == FailureCategory.AutomationTimeout, "A stalled snapshot was presented as a confirmed current application mismatch.");
        var earlier = run.FailureDiagnostics.Single(d => d.Category == FailureCategory.AssertionMismatch);
        Check(earlier.Actual == "Before timeout" && earlier.Expected == "Expected" && earlier.ObservedAt is not null, "Last completed assertion observation was lost on snapshot timeout.");
        Check(earlier.Evidence.Any(e => e.Kind == "last completed observation before deadline" && File.Exists(e.Path)), "Supporting observation does not reference its captured evidence.");
    });
    private static TestCase Test(params TestStep[] steps) => new() { Name = "Diagnostic fixture", Steps = steps.ToList() };
    private static TestStep Assertion(string expected) => new() { Title = "Check status", Action = StepAction.AssertText, Selector = "id:Status", Value = expected, TimeoutMs = 100 };
    private static TestStep Click() => new() { Title = "Click button", Action = StepAction.Click, Selector = "id:Button", TimeoutMs = 100 };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static async Task WithRoot(Func<string, Task> operation)
    {
        var root = Path.Combine(Path.GetTempPath(), "Testy-diagnostics-" + Guid.NewGuid().ToString("N"));
        try { Directory.CreateDirectory(root); await operation(root); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class Agent(ComputerAgentResult result) : IComputerAgent
    {
        public Task<ComputerAgentResult> RunAsync(string instructions, int maximumTurns = 30, IProgress<ComputerToolObservation>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handle(request));
    }
    private sealed class FailedExecutor : IComputerActionExecutor
    {
        public Task ExecuteAsync(JsonElement action, CancellationToken cancellationToken = default) => throw new IOException("Fixture interrupted native input.");
    }
    private sealed class Driver : ITargetDriver
    {
        public string Text { get; set; } = "Ready";
        public string SelectorScenario { get; set; } = "";
        public bool CaptureFails { get; set; }
        public bool BlockInput { get; set; }
        public bool IgnoreCancellation { get; set; }
        public bool SlowSecondSnapshot { get; set; }
        private int snapshotCalls;
        public bool LateSideEffect { get; private set; }
        public int InputCount { get; private set; }
        public TaskCompletionSource InputStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PendingInput { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LateInputCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TargetInfo? Target { get; } = new() { ProcessId = 42, Title = "Diagnostics fixture" };
        public Task<IReadOnlyList<TargetInfo>> GetTargetsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TargetInfo>>([Target!]);
        public Task AttachAsync(int processId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task<UiSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
        {
            if (++snapshotCalls == 2 && SlowSecondSnapshot) await Task.Delay(5000, cancellationToken);
            var elements = new List<UiElementInfo> { new() { Selector = "id:Status", AutomationId = "Status", Value = Text, IsEnabled = true } };
            if (SelectorScenario != "missing") elements.Add(new UiElementInfo { Selector = "id:Button", AutomationId = "Button", IsEnabled = SelectorScenario != "disabled" });
            if (SelectorScenario == "ambiguous") elements.Add(new UiElementInfo { Selector = "id:Button", AutomationId = "Button", IsEnabled = true });
            return new UiSnapshot { Target = Target!, Elements = elements, IsTruncated = SelectorScenario == "truncated" };
        }
        public async Task ExecuteAsync(TestStep step, CancellationToken cancellationToken = default)
        {
            InputCount++; InputStarted.TrySetResult();
            if (BlockInput) await Task.Delay(5000, cancellationToken);
            if (IgnoreCancellation) { await PendingInput.Task; LateSideEffect = true; LateInputCompleted.TrySetResult(); }
        }
        public async Task<string> CaptureAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (CaptureFails) throw new IOException("Fixture capture unavailable.");
            await File.WriteAllBytesAsync(filePath, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6hUAAAAASUVORK5CYII="), cancellationToken);
            return filePath;
        }
        public void Dispose() { PendingInput.TrySetResult(); }
    }
}
