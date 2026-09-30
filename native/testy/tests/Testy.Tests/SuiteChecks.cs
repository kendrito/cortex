using System.Xml.Linq;
using Testy.Core;

namespace Testy.Tests;

internal static class SuiteChecks
{
    public static IEnumerable<(string, Func<Task>)> All()
    {
        yield return ("suite validates later cases before issuing any actions", Preflight);
        yield return ("suite repetitions preserve saved inputs and independent artifacts", Repetitions);
        yield return ("suite fails fast and records unexecuted cases as skipped", FailFast);
        yield return ("suite cancellation preserves completed evidence and skips later cases", Cancellation);
        yield return ("suite rejects incomplete or mismatched runner outcomes", InvalidOutcome);
        yield return ("suite reports escape text and prevent artifact links escaping the suite", Reports);
        yield return ("suite rejects concurrent execution on the same runner", Concurrency);
    }
    private static TestCase Case(string name = "Customer") => new() { Name = name, Steps = [new() { Action = StepAction.AssertExists, Selector = "id:CustomerName" }] };
    private static RunResult Result(TestCase test, RunStatus status = RunStatus.Passed) => new() { TestId = test.Id, TestName = test.Name, Status = status, Summary = status.ToString(), StartedAt = DateTimeOffset.UtcNow.AddSeconds(-1), FinishedAt = DateTimeOffset.UtcNow, Steps = [new() { Step = TestyJson.Clone(test.Steps[0]), Status = status }] };
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task WithRoot(Func<string, Task> execute)
    {
        var root = Path.Combine(Path.GetTempPath(), "Testy-suite-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await execute(root); }
        finally { Directory.Delete(root, true); }
    }
    private static Task Preflight() => WithRoot(async root =>
    {
        var count = 0;
        var bad = Case(); bad.Steps[0].TimeoutMs = -1;
        var runner = new SuiteRunner((test, _, _) => { count++; return Task.FromResult(Result(test)); }, root, "fake");
        try { await runner.RunAsync(new() { Tests = [Case(), bad] }); throw new Exception("Invalid suite accepted."); }
        catch (InvalidDataException) { }
        Check(count == 0 && Directory.GetFileSystemEntries(root).Length == 0, "Invalid later test allowed an earlier action.");
    });
    private static Task Repetitions() => WithRoot(async root =>
    {
        var original = Case(); var observed = new List<string>(); var paths = new HashSet<string>();
        var result = await new SuiteRunner((test, path, _) =>
        {
            observed.Add(test.Steps[0].Selector); paths.Add(path); test.Steps[0].Selector = "id:Changed";
            return Task.FromResult(Result(test));
        }, root, "fake").RunAsync(new() { Tests = [original], Repetitions = 3 });
        Check(result.Status == RunStatus.Passed && result.Passed == 3 && paths.Count == 3, "Repetitions shared artifact paths or were not counted.");
        Check(observed.All(x => x == "id:CustomerName") && original.Steps[0].Selector == "id:CustomerName", "An execution mutated subsequent acceptance criteria.");
        Check(File.Exists(Path.Combine(result.ArtifactDirectory, "suite-definition.json")), "Original definition not preserved.");
    });
    private static Task FailFast() => WithRoot(async root =>
    {
        var count = 0;
        var result = await new SuiteRunner((test, _, _) => Task.FromResult(Result(test, ++count == 2 ? RunStatus.Failed : RunStatus.Passed)), root, "fake")
            .RunAsync(new() { Tests = [Case("A"), Case("B")], Repetitions = 3 });
        Check(count == 2 && result.Passed == 1 && result.Failed == 1 && result.Skipped == 4, "Suite retried a failed target or lost skipped cases.");
        var xml = XDocument.Load(Path.Combine(result.ArtifactDirectory, "suite-junit.xml"));
        Check(xml.Descendants("failure").Count() == 1 && xml.Descendants("skipped").Count() == 4, "CI report counts are wrong.");
    });
    private static Task Cancellation() => WithRoot(async root =>
    {
        using var cancellation = new CancellationTokenSource();
        var count = 0;
        var result = await new SuiteRunner((test, _, _) => { count++; cancellation.Cancel(); return Task.FromResult(Result(test)); }, root, "fake")
            .RunAsync(new() { Tests = [Case()], Repetitions = 3 }, cancellation.Token);
        Check(result.Status == RunStatus.Cancelled && result.Passed == 1 && result.Skipped == 2 && count == 1, "Cancellation ran later cases or erased completed results.");
        Check(result.FinishedAt is not null && File.Exists(Path.Combine(result.ArtifactDirectory, "suite.json")), "Cancelled suite was not finalized.");
        Check(XDocument.Load(Path.Combine(result.ArtifactDirectory, "suite-junit.xml")).Descendants("error").Count() == 1, "JUnit concealed suite cancellation behind passing/skipped cases.");
    });
    private static Task InvalidOutcome() => WithRoot(async root =>
    {
        foreach (var invalid in new[] { "running", "mismatch", "unfinished", "empty" })
        {
            var result = await new SuiteRunner((test, _, _) =>
                {
                    var run = invalid == "mismatch" ? Result(Case()) : Result(test);
                    if (invalid == "running") run.Status = RunStatus.Running;
                    if (invalid == "unfinished") run.FinishedAt = null;
                    if (invalid == "empty") run.Steps.Clear();
                    return Task.FromResult(run);
                }, root, "fake")
                .RunAsync(new() { Tests = [Case()], Repetitions = 2 });
            Check(result.Status == RunStatus.Failed && result.Skipped == 1 && result.Passed == 0, "Invalid or unrelated result granted a pass.");
        }
    });
    private static Task Reports() => WithRoot(async root =>
    {
        var result = await new SuiteRunner((test, _, _) =>
        {
            var run = Result(test, RunStatus.Failed); run.Summary = "<script>alert(1)</script>"; run.ArtifactDirectory = root;
            return Task.FromResult(run);
        }, root, "<test>").RunAsync(new() { Name = "<script>suite</script>", Tests = [Case("<img src=x onerror=alert(1)>")] });
        var html = await File.ReadAllTextAsync(Path.Combine(result.ArtifactDirectory, "suite.html"));
        Check(!html.Contains("<script>") && !html.Contains("<img") && !html.Contains("<a href="), "Untrusted text or escaping artifact link was emitted.");
        Check(html.Contains("&lt;script&gt;"), "Report omitted rather than escaped evidence.");
    });
    private static Task Concurrency() => WithRoot(async root =>
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new SuiteRunner(async (test, _, _) => { entered.SetResult(); await release.Task; return Result(test); }, root, "fake");
        var active = runner.RunAsync(new() { Tests = [Case()] });
        await entered.Task;
        try
        {
            try { await runner.RunAsync(new() { Tests = [Case()] }); throw new Exception("Concurrent suite accepted."); }
            catch (InvalidOperationException) { }
        }
        finally { release.SetResult(); await active; }
    });
}
