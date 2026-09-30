using System.Text.Json;
using System.Xml.Linq;
using Testy.Core;

namespace Testy.Tests;

internal static class AiTestRunnerChecks
{
    public static IEnumerable<(string Name, Func<Task> Execute)> All()
    {
        yield return ("AI runner requires every immutable saved assertion", MissingAssertion);
        yield return ("AI runner rejects skipped interactions despite a passing final assertion", MissingInteraction);
        yield return ("AI runner rejects wrong or weaker acceptance checks", WrongAssertion);
        yield return ("AI runner enforces assertion order and duplicate occurrences", OrderedAssertions);
        yield return ("AI runner requires explicit completion independently of checks", Incomplete);
        yield return ("AI runner accepts completed exact assertions and deduplicates progress", Complete);
        yield return ("AI runner preserves failure and cancellation in reports", TerminalFailures);
        yield return ("AI runner refuses passed actions without screenshot and snapshot evidence", MissingEvidence);
        yield return ("AI runner validates assertions and turn limits before invoking model", InvalidSavedTests);
        yield return ("execution service defaults to AI and replay is explicitly selectable", ServiceRouting);
    }

    private static async Task MissingAssertion()
    {
        var test = Test(Assertion("id:StatusMessage", "Idle"), Assertion("id:CustomerName", ""));
        var result = await Run(test, [Passed(test.Steps[0])]);
        Check(result.Status == RunStatus.Failed && result.Summary.Contains("not verified", StringComparison.Ordinal), "Missing saved check received a pass.");
        Check(result.Steps.Last().Status == RunStatus.Failed, "Missing acceptance evidence was not visible as a failed report step.");
    }

    private static async Task MissingInteraction()
    {
        var assertion = Assertion("id:StatusMessage", "Idle");
        var test = Test(new TestStep { Title = "Required interaction", Action = StepAction.Click, Selector = "id:AddCustomer" }, assertion);
        var result = await Run(test, [Passed(assertion)]);
        Check(result.Status == RunStatus.Failed && result.Summary.Contains("Saved step", StringComparison.Ordinal), "A passing final state skipped a required interaction.");
    }

    private static async Task WrongAssertion()
    {
        var test = Test(Assertion("id:StatusMessage", "Idle"));
        foreach (var actual in new[]
        {
            Assertion("id:StatusMessage", "contains:Idle"),
            Assertion("id:WrongTarget", "Idle"),
            Assertion("id:StatusMessage", "Different"),
            new TestStep { Title = "Weaker existence check", Action = StepAction.AssertExists, Selector = "id:StatusMessage" }
        }) Check((await Run(test, [Passed(actual)])).Status == RunStatus.Failed, "A weakened or substituted assertion passed.");
    }

    private static async Task OrderedAssertions()
    {
        var first = Assertion("id:StatusMessage", "Idle");
        var second = Assertion("id:CustomerName", "");
        Check((await Run(Test(first, second), [Passed(second), Passed(first)])).Status == RunStatus.Failed, "Out-of-order assertions passed.");
        var repeated = TestyJson.Clone(first); repeated.Id = Guid.NewGuid().ToString("N");
        Check((await Run(Test(first, repeated), [Passed(first)])).Status == RunStatus.Failed, "One assertion occurrence covered two required occurrences.");
        Check((await Run(Test(first, repeated), [Passed(first), Passed(repeated)])).Status == RunStatus.Passed, "Two exact occurrences were not accepted.");
    }

    private static async Task Incomplete()
    {
        var step = Assertion("id:StatusMessage", "Idle");
        var result = await Run(Test(step), [Passed(step)], completed: false);
        Check(result.Status == RunStatus.Failed && result.Summary.Contains("without completing", StringComparison.Ordinal), "A stopped model session was called complete.");
    }

    private static async Task Complete()
    {
        var first = Assertion("id:StatusMessage", "Idle");
        var second = Assertion("id:CustomerName", "");
        var result = await Run(Test(first, second), [Passed(first), Passed(second)], status: RunStatus.Pending);
        Check(result.Status == RunStatus.Passed, result.Summary);
        Check(result.Steps.Count == 2 && result.Steps.Select(s => s.Index).SequenceEqual(new[] { 0, 1 }), "Progress observations were duplicated when final observations arrived.");
        Check(result.AiAnalysis == "Model final explanation.", "Model explanation was not retained.");
    }

    private static async Task TerminalFailures()
    {
        var step = Assertion("id:StatusMessage", "Idle");
        var failed = await Run(Test(step), [Passed(step)], status: RunStatus.Failed);
        Check(failed.Status == RunStatus.Failed && failed.Steps.Any(s => s.Status == RunStatus.Failed), "Model failure was incorrectly promoted to pass.");
        var badStep = Passed(step); badStep.Status = RunStatus.Failed; badStep.Message = "Observed mismatch";
        Check((await Run(Test(step), [badStep], status: RunStatus.Passed)).Status == RunStatus.Failed, "Failed local evidence was overridden by model status.");
        var cancelled = await Run(Test(step), [], status: RunStatus.Cancelled, completed: false);
        Check(cancelled.Status == RunStatus.Cancelled && cancelled.Steps.Any(s => s.Status == RunStatus.Cancelled), "Cancelled session lacks a cancelled report step.");
    }

    private static async Task MissingEvidence()
    {
        var step = Assertion("id:StatusMessage", "Idle");
        var result = await Run(Test(step), [Passed(step)], includeEvidence: false);
        Check(result.Status == RunStatus.Failed, "An assertion passed without screenshot and snapshot evidence.");
    }

    private static async Task InvalidSavedTests()
    {
        using var driver = new FakeDriver();
        var root = Path.Combine(Path.GetTempPath(), "Testy-ai-invalid-" + Guid.NewGuid().ToString("N"));
        var invoked = false;
        var settings = new ProviderSettings { MaximumAgentTurns = 12 };
        var runner = new AiTestRunner(driver, settings, root, agentFactory: _ => { invoked = true; return new Agent([]); });
        await Throws<InvalidDataException>(() => runner.RunAsync(Test(new TestStep { Title = "No acceptance check", Action = StepAction.Click, Selector = "id:AddCustomer" })));
        settings.MaximumAgentTurns = 0;
        runner = new AiTestRunner(driver, settings, root, agentFactory: _ => { invoked = true; return new Agent([]); });
        await Throws<InvalidDataException>(() => runner.RunAsync(Test(Assertion("id:StatusMessage", "Idle"))));
        Check(!invoked, "Invalid tests reached the model.");
    }

    private static async Task ServiceRouting()
    {
        using var driver = new FakeDriver();
        var root = Path.Combine(Path.GetTempPath(), "Testy-service-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new ProviderSettings { Kind = ProviderKind.Offline };
            Check(settings.AiDirectedExecution, "AI-directed execution is not the default.");
            await Throws<InvalidOperationException>(() => new TestExecutionService(driver, settings, root).RunAsync(Test(Assertion("id:StatusMessage", "Idle"))));
            settings.AiDirectedExecution = false;
            var result = await new TestExecutionService(driver, settings, root).RunAsync(Test(Assertion("id:StatusMessage", "Idle")));
            Check(result.Status == RunStatus.Passed, "Explicit offline replay did not route to deterministic execution.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task<RunResult> Run(TestCase test, List<StepResult> steps, RunStatus status = RunStatus.Passed, bool completed = true, bool includeEvidence = true)
    {
        using var driver = new FakeDriver();
        var root = Path.Combine(Path.GetTempPath(), "Testy-ai-run-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            foreach (var step in steps)
            {
                if (includeEvidence)
                {
                    step.Snapshot = await driver.SnapshotAsync();
                    step.ScreenshotPath = await driver.CaptureAsync(Path.Combine(root, Guid.NewGuid().ToString("N") + ".png"));
                }
            }
            var agent = new Agent(steps) { Status = status, Completed = completed };
            var runner = new AiTestRunner(driver, new ProviderSettings { MaximumAgentTurns = 12 }, Path.Combine(root, "runs"), agentFactory: _ => agent);
            var result = await runner.RunAsync(test);
            Check(result.FinishedAt is not null, "AI run was not finalized.");
            var saved = JsonSerializer.Deserialize<RunResult>(await File.ReadAllTextAsync(Path.Combine(result.ArtifactDirectory, "run.json")), TestyJson.Options)!;
            Check(saved.Status == result.Status, "Saved run status differs from returned status.");
            var xml = XDocument.Load(Path.Combine(result.ArtifactDirectory, "junit.xml"));
            if (result.Status == RunStatus.Failed) Check(xml.Descendants("failure").Any(), "Failed run produced an all-green JUnit report.");
            if (result.Status == RunStatus.Cancelled) Check(xml.Descendants("skipped").Any(), "Cancelled run produced an all-green JUnit report.");
            Check(File.Exists(Path.Combine(result.ArtifactDirectory, "requested-test.json")) && File.Exists(Path.Combine(result.ArtifactDirectory, "report.html")), "Requested contract or HTML report missing.");
            return result;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static TestCase Test(params TestStep[] steps) => new() { Name = "Immutable acceptance", Intent = "Verify recorded acceptance criteria.", Steps = steps.ToList() };
    private static TestStep Assertion(string selector, string value) => new() { Title = "Assert " + selector + " = " + value, Action = StepAction.AssertText, Selector = selector, Value = value };
    private static StepResult Passed(TestStep step) => new() { Step = TestyJson.Clone(step), Status = RunStatus.Passed, Message = "Assertion satisfied." };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task> execute) where T : Exception
    {
        try { await execute(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class Agent(List<StepResult> steps) : IComputerAgent
    {
        public RunStatus Status { get; init; } = RunStatus.Passed;
        public bool Completed { get; init; } = true;
        public Task<ComputerAgentResult> RunAsync(string instructions, int maximumTurns = 20, IProgress<ComputerToolObservation>? progress = null, CancellationToken cancellationToken = default)
        {
            Check(instructions.Contains("immutable", StringComparison.OrdinalIgnoreCase), "Agent did not receive immutable acceptance instructions.");
            var observations = steps.Select(step => new ComputerToolObservation { Status = step.Status.ToString(), Execution = new RunResult { Status = step.Status, Steps = [step] }, Snapshot = step.Snapshot, ScreenshotPath = step.ScreenshotPath }).ToList();
            foreach (var observation in observations) progress?.Report(observation);
            return Task.FromResult(new ComputerAgentResult { Status = Status, Completed = Completed, Message = "Model final explanation.", ModelTurns = steps.Count + 1, Observations = observations });
        }
    }
}
