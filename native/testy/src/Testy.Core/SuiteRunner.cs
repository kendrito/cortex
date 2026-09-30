using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace Testy.Core;

public sealed class TestSuite
{
    public string Name { get; set; } = "Functional suite";
    public int Repetitions { get; set; } = 1;
    public List<TestCase> Tests { get; set; } = [];
}

public sealed class SuiteEntryResult
{
    public int Repetition { get; set; }
    public string TestId { get; set; } = "";
    public string TestName { get; set; } = "";
    public RunStatus Status { get; set; } = RunStatus.Pending;
    public double DurationMs { get; set; }
    public string Message { get; set; } = "";
    public RunResult? Run { get; set; }
}

public sealed class SuiteResult
{
    public string Name { get; set; } = "";
    public RunStatus Status { get; set; } = RunStatus.Running;
    public string ExecutionMode { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string ArtifactDirectory { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<SuiteEntryResult> Entries { get; set; } = [];
    public int Passed => Entries.Count(x => x.Status == RunStatus.Passed);
    public int Failed => Entries.Count(x => x.Status == RunStatus.Failed);
    public int Cancelled => Entries.Count(x => x.Status == RunStatus.Cancelled);
    public int Skipped => Entries.Count(x => x.Status == RunStatus.Skipped);
}

/// <summary>Sequential, fail-fast suites on one attached target. No implicit reset, retry, or model fallback.</summary>
public sealed class SuiteRunner(
    Func<TestCase, string, CancellationToken, Task<RunResult>> execute,
    string artifactsRoot,
    string executionMode)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<SuiteResult> RunAsync(TestSuite suite, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(suite);
        // Validate the entire suite before executing even the first test.
        if (string.IsNullOrWhiteSpace(suite.Name) || suite.Name.Length > 1000)
            throw new InvalidDataException("Suite name must contain 1–1000 characters.");
        if (suite.Repetitions is < 1 or > 100 || suite.Tests is null || suite.Tests.Count is < 1 or > 200 || suite.Tests.Count * suite.Repetitions > 1000)
            throw new InvalidDataException("Suites require 1–200 tests, 1–100 repetitions, and at most 1000 planned runs.");
        foreach (var test in suite.Tests) TestValidator.Validate(test);
        if (suite.Tests.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != suite.Tests.Count)
            throw new InvalidDataException("Suite test IDs must be unique; use repetitions to run tests again.");
        var frozen = TestyJson.Clone(suite);
        if (!await gate.WaitAsync(0, ct)) throw new InvalidOperationException("This suite runner is already active.");
        try { return await RunCoreAsync(frozen, ct); }
        finally { gate.Release(); }
    }

    private async Task<SuiteResult> RunCoreAsync(TestSuite suite, CancellationToken ct)
    {
        var result = new SuiteResult { Name = suite.Name, ExecutionMode = executionMode };
        result.ArtifactDirectory = Path.GetFullPath(Path.Combine(artifactsRoot, $"suite-{result.StartedAt:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(result.ArtifactDirectory);
        WorkspaceStore.WriteAtomic(Path.Combine(result.ArtifactDirectory, "suite-definition.json"), suite);
        for (var repetition = 1; repetition <= suite.Repetitions; repetition++)
            foreach (var test in suite.Tests)
                result.Entries.Add(new() { Repetition = repetition, TestId = test.Id, TestName = test.Name });
        WriteReports(result);
        for (var index = 0; index < result.Entries.Count; index++)
        {
            var entry = result.Entries[index];
            if (result.Status != RunStatus.Running || ct.IsCancellationRequested)
            {
                if (ct.IsCancellationRequested && result.Status == RunStatus.Running) result.Status = RunStatus.Cancelled;
                entry.Status = RunStatus.Skipped;
                entry.Message = "Not started: the suite stopped. No reset or retry was attempted.";
                continue;
            }
            entry.Status = RunStatus.Running;
            WriteReports(result);
            var clock = Stopwatch.StartNew();
            try
            {
                var test = TestyJson.Clone(suite.Tests[index % suite.Tests.Count]);
                var runDirectory = Path.Combine(result.ArtifactDirectory, $"case-{index + 1:0000}");
                Directory.CreateDirectory(runDirectory);
                var run = await execute(test, runDirectory, ct);
                entry.Run = run ?? throw new InvalidDataException("The test runner returned no result.");
                if (run.TestId != entry.TestId) throw new InvalidDataException("The test runner returned a result for a different test.");
                if (run.Status is not (RunStatus.Passed or RunStatus.Failed or RunStatus.Cancelled))
                    throw new InvalidDataException("The test runner did not return a completed outcome.");
                if (run.FinishedAt is null || run.FinishedAt < run.StartedAt)
                    throw new InvalidDataException("The test runner returned an unfinalized outcome.");
                if (run.Status == RunStatus.Passed && (run.Steps.Count == 0 || run.Steps.Any(s => s.Status != RunStatus.Passed)))
                    throw new InvalidDataException("A passing run must contain completed, passing step records.");
                entry.Status = run.Status;
                entry.Message = run.Summary;
                if (run.Status != RunStatus.Passed) result.Status = run.Status;
                else if (ct.IsCancellationRequested) result.Status = RunStatus.Cancelled;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                entry.Status = RunStatus.Cancelled;
                entry.Message = "Cancelled. Already-dispatched Windows actions cannot be undone.";
                result.Status = RunStatus.Cancelled;
            }
            catch (Exception ex)
            {
                entry.Status = RunStatus.Failed;
                entry.Message = "Suite execution stopped: " + ex.Message;
                result.Status = RunStatus.Failed;
            }
            entry.DurationMs = clock.Elapsed.TotalMilliseconds;
            WriteReports(result);
        }
        if (result.Status == RunStatus.Running) result.Status = RunStatus.Passed;
        result.FinishedAt = DateTimeOffset.UtcNow;
        result.Summary = $"{result.Status}: {result.Passed}/{result.Entries.Count} planned runs passed, {result.Failed} failed, {result.Cancelled} cancelled, {result.Skipped} skipped.";
        WriteReports(result);
        return result;
    }

    public static void WriteReports(SuiteResult result)
    {
        WorkspaceStore.WriteAtomic(Path.Combine(result.ArtifactDirectory, "suite.json"), result);
        static string Seconds(double ms) => (ms / 1000).ToString("0.###", CultureInfo.InvariantCulture);
        var cancelled = result.Status == RunStatus.Cancelled;
        var incomplete = result.Status is RunStatus.Pending or RunStatus.Running;
        var completionError = cancelled || incomplete;
        var xml = new XElement("testsuite", new XAttribute("name", result.Name),
            new XAttribute("tests", result.Entries.Count + (completionError ? 1 : 0)), new XAttribute("failures", result.Failed), new XAttribute("errors", completionError ? 1 : 0),
            new XAttribute("skipped", result.Entries.Count(e => e.Status is RunStatus.Skipped or RunStatus.Cancelled or RunStatus.Pending or RunStatus.Running)),
            new XAttribute("time", Seconds(result.Entries.Sum(x => x.DurationMs))));
        xml.Add(new XElement("properties",
            new XElement("property", new XAttribute("name", "testy.status"), new XAttribute("value", result.Status)),
            new XElement("property", new XAttribute("name", "testy.executionMode"), new XAttribute("value", result.ExecutionMode))));
        if (completionError) xml.Add(new XElement("testcase", new XAttribute("name", "Suite completion"), new XAttribute("classname", result.Name),
            new XElement("error", new XAttribute("type", cancelled ? "SuiteCancelled" : "SuiteIncomplete"), new XAttribute("message", cancelled ? "The suite was cancelled; it did not complete successfully." : "The suite has not finalized. This progress report cannot establish a successful outcome."))));
        static string H(string value) => WebUtility.HtmlEncode(value);
        var html = new StringBuilder("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>Testy suite</title><style>body{font:16px system-ui;max-width:1100px;margin:40px auto;padding:0 20px;color:#182638}table{border-collapse:collapse;width:100%}td,th{padding:12px;border-bottom:1px solid #ccd5e0;text-align:left}code{white-space:pre-wrap}a{color:#075c96}</style>");
        html.Append($"<h1>{H(result.Name)}</h1><p>{H(result.Status.ToString())} · {H(result.ExecutionMode)}</p><p>{H(result.Summary)}</p><p>Runs execute sequentially and stop on the first failure or cancellation. Skipped cases were not tested. Each test must establish its own starting state.</p><table><tr><th>Repeat</th><th>Test</th><th>Outcome</th><th>Seconds</th><th>Evidence</th></tr>");
        foreach (var entry in result.Entries)
        {
            var testCase = new XElement("testcase", new XAttribute("name", $"{entry.TestName} [repeat {entry.Repetition}]"),
                new XAttribute("classname", result.Name), new XAttribute("time", Seconds(entry.DurationMs)));
            if (entry.Status == RunStatus.Failed) testCase.Add(new XElement("failure", new XAttribute("message", entry.Message), entry.Message));
            else if (entry.Status != RunStatus.Passed) testCase.Add(new XElement("skipped", new XAttribute("message", entry.Message.Length == 0 ? "Run not completed." : entry.Message)));
            string evidence = "";
            if (entry.Run?.ArtifactDirectory is { Length: > 0 } directory)
            {
                // Never embed a supplied URL or escaping path as a clickable report link.
                var relative = Path.GetRelativePath(result.ArtifactDirectory, Path.Combine(directory, "report.html"));
                if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    evidence = $"<a href=\"{H(string.Join('/', relative.Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString)))}\">Run report</a>";
                testCase.Add(new XElement("system-out", "Run artifacts: " + directory));
            }
            xml.Add(testCase);
            html.Append($"<tr><td>{entry.Repetition}</td><td>{H(entry.TestName)}</td><td>{H(entry.Status.ToString())}<br><small>{H(entry.Message)}</small></td><td>{Seconds(entry.DurationMs)}</td><td>{evidence}</td></tr>");
        }
        html.Append("</table></html>");
        WriteTextAtomic(Path.Combine(result.ArtifactDirectory, "suite-junit.xml"), new XDocument(xml).ToString());
        WriteTextAtomic(Path.Combine(result.ArtifactDirectory, "suite.html"), html.ToString());
    }

    private static void WriteTextAtomic(string path, string contents)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temporary, contents); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
