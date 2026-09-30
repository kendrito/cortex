using System.Text.Json;
using Testy.Core;

namespace Testy.Tests;

internal static partial class McpChecks
{
    private static (string Name, Func<Task> Execute)[] CortexWorkflows() =>
    [
        ("MCP Cortex workflow suites CSV preferences and revisions round trip", CortexWorkflowRoundTrip),
        ("MCP Cortex backup task exposes completed result and survives polling", CortexWorkflowTask),
        ("MCP Cortex run history pages deterministically and validates offsets", CortexRunPages),
        ("MCP Cortex standalone report embeds contained PNGs and escapes untrusted evidence", CortexRunReport),
        ("MCP Cortex standalone report rejects foreign folders malformed PNGs and oversized downloads", CortexReportLimits)
    ];
    private static async Task CortexWorkflowRoundTrip()
    {
        await using var h = new Harness("cortex-workflows"); await h.InitializeAsync();
        async Task<JsonElement> Call(string name, object? args = null) => (await h.Client.CallToolAsync(name, args)).RequireOk(name).Structured;
        var created = await Call("create_test", new { name = "Workflow fixture", intent = "Observe exact label", steps = new[] { new { id = "assert-label", action = "assertText", selector = "id:Label", value = "Original" } } });
        var id = created.GetProperty("testId").GetString()!; var revision = created.GetProperty("revision").GetString();
        var saved = await Call("save_suite", new { name = "Regression set", testIds = new[] { id }, repetitions = 2 });
        var suiteId = saved.GetProperty("suiteId").GetString();
        Check((await Call("list_suites")).GetProperty("suites").GetArrayLength() == 1, "Suite was not saved.");
        var test = h.Service.Store.LoadTests().Single();
        var matrix = await Call("materialize_template", new { name = "Data matrix", template = new { version = 1, name = "Labels", test, bindings = new[] { new { parameter = "label", stepId = "assert-label", field = "value" } } }, csv = "label\nFirst\nSecond\n" });
        var materialized = matrix.GetProperty("materializedTests");
        Check(materialized.GetArrayLength() == 2 && materialized[1].GetProperty("steps")[0].GetProperty("value").GetString() == "Second", "CSV literal expansion failed.");
        var prefs = await Call("save_preferences", new { supportsImages = false, liveReview = true, maximumAgentTurns = 42 });
        Check(!prefs.GetProperty("supportsImages").GetBoolean() && prefs.GetProperty("maximumAgentTurns").GetInt32() == 42, "Execution preferences did not roundtrip.");
        await Call("update_test", new { testId = id, expectedRevision = revision, name = "Current fixture" });
        var conflict = await h.Client.CallToolAsync("delete_test", new { testId = id, expectedRevision = revision }); Check(conflict.IsError, "MCP stale deletion succeeded.");
        var found = (await Call("list_tests")).GetProperty("tests")[0]; Check(found.GetProperty("searchText").GetString()!.Contains("Original"), "Full saved step text is missing from search.");
        await Call("delete_suite", new { suiteId });
        Check((await Call("list_suites")).GetProperty("suites").GetArrayLength() == 1, "Deleting named suite removed the data matrix.");
        var traversal = await h.Client.CallToolAsync("delete_suite", new { suiteId = "../outside" }); Check(traversal.IsError, "Workflow id escaped its directory.");
    }
    private static async Task CortexWorkflowTask()
    {
        await using var h = new Harness("cortex-backup"); await h.InitializeAsync();
        var archive = Path.Combine(Root, "cortex-backup.zip");
        var started = (await h.Client.CallToolAsync("workspace_backup", new { archive })).RequireOk("workspace_backup").Structured;
        var id = started.GetProperty("taskId").GetString()!;
        JsonElement task = started;
        await UntilAsync(async () => { task = (await h.Client.CallToolAsync("get_task", new { taskId = id })).RequireOk("get_task").Structured; return !task.GetProperty("running").GetBoolean(); }, "Backup never completed.");
        Check(task.GetProperty("status").GetString() == "completed" && task.GetProperty("result").GetProperty("passed").GetBoolean() && File.Exists(archive), "Task omitted successful backup evidence.");
        var listed = (await h.Client.CallToolAsync("list_tasks")).RequireOk("list_tasks").Structured.GetProperty("tasks");
        Check(listed.GetArrayLength() == 1 && listed[0].GetProperty("taskId").GetString() == id, "Completed task cannot be polled.");
    }

    private static async Task CortexRunPages()
    {
        await using var h = new Harness("cortex-run-pages"); await h.InitializeAsync();
        var sameTime = DateTimeOffset.UtcNow;
        foreach (var id in new[] { "z", "a", "m" }) WriteRun(h.Workspace, id, "selected", RunStatus.Passed, startedAt: sameTime);
        WriteRun(h.Workspace, "other", "different", RunStatus.Passed, startedAt: sameTime.AddSeconds(1));
        var first = (await h.Client.CallToolAsync("list_runs", new { testId = "selected", limit = 2 })).RequireOk("first page").Structured;
        Check(first.GetProperty("total").GetInt32() == 3 && first.GetProperty("offset").GetInt32() == 0 && first.GetProperty("nextOffset").GetInt32() == 2, "First page metadata is incomplete.");
        Check(first.GetProperty("runs").EnumerateArray().Select(r => r.GetProperty("runId").GetString()).SequenceEqual(new[] { "a", "m" }), "Equal timestamps must sort by ordinal run id.");
        var last = (await h.Client.CallToolAsync("list_runs", new { testId = "selected", limit = 2, offset = 2 })).RequireOk("last page").Structured;
        Check(last.GetProperty("runs")[0].GetProperty("runId").GetString() == "z" && last.GetProperty("returned").GetInt32() == 1 && last.GetProperty("nextOffset").ValueKind == JsonValueKind.Null, "Final page lost or duplicated a run.");
        var beyond = (await h.Client.CallToolAsync("list_runs", new { offset = 100000 })).RequireOk("empty page").Structured;
        Check(beyond.GetProperty("returned").GetInt32() == 0 && beyond.GetProperty("nextOffset").ValueKind == JsonValueKind.Null, "Out-of-range page must end cleanly.");
        foreach (var offset in new[] { -1, 100001 }) Check((await h.Client.CallToolAsync("list_runs", new { offset })).IsError, "Invalid offset accepted.");
    }

    private static readonly byte[] ReportPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
    private static RunResult WriteReportFixture(string workspace, string id)
    {
        var folder = Path.Combine(workspace, "artifacts", id); Directory.CreateDirectory(folder);
        var png = Path.Combine(folder, "step.png"); File.WriteAllBytes(png, ReportPng);
        var run = WriteRun(workspace, id, "report-test", RunStatus.Passed, screenshot: png);
        TestRunner.WriteReports(run);
        return run;
    }

    private static async Task CortexRunReport()
    {
        await using var h = new Harness("cortex-report"); await h.InitializeAsync();
        var run = WriteReportFixture(h.Workspace, "safe-report");
        run.TestName = "<script>alert('title')</script>"; run.Steps[0].Message = "<img src=https://example.invalid/steal>";
        run.AiAnalysis = "<iframe src=file:///secret></iframe>";
        new WorkspaceStore(h.Workspace).SaveRun(run);
        File.WriteAllText(Path.Combine(run.ArtifactDirectory, "report.html"), "<script>tamperedReport()</script><img src=file:///secret.png>");
        var report = (await h.Client.CallToolAsync("get_run_report", new { runId = run.Id })).RequireOk("report").Structured;
        var html = report.GetProperty("html").GetString()!;
        Check(report.GetProperty("filename").GetString() == "testy-run-safe-report.html" && report.GetProperty("mimeType").GetString() == "text/html", "Download metadata missing.");
        Check(html.Contains("data:image/png;base64," + Convert.ToBase64String(ReportPng)) && html.Contains("Content-Security-Policy"), "Offline report omitted evidence or restrictive policy.");
        Check(html.Contains("&lt;script&gt;") && !html.Contains("<script>") && !html.Contains("tamperedReport") && !html.Contains("<img src=https:") && !html.Contains("<iframe src=file:"), "Untrusted report content became active HTML.");
        var resource = Result(await h.Client.RequestAsync("resources/read", new { uri = "testy://runs/safe-report/report" })).GetProperty("contents")[0];
        Check(resource.GetProperty("mimeType").GetString() == "text/html" && resource.GetProperty("text").GetString() == html, "Tool/resource report parity differs.");
        var outside = Path.Combine(Root, "outside-report.png"); File.WriteAllBytes(outside, ReportPng);
        run.Steps[0].ScreenshotPath = outside; new WorkspaceStore(h.Workspace).SaveRun(run);
        var contained = (await h.Client.CallToolAsync("get_run_report", new { runId = run.Id })).RequireOk("contained report").Structured.GetProperty("html").GetString()!;
        Check(!contained.Contains("data:image/png"), "Foreign evidence entered report.");
        Check((await h.Client.CallToolAsync("get_run_report", new { runId = "../safe-report" })).IsError, "Report traversal accepted.");
        Check(ErrorCode(await h.Client.RequestAsync("resources/read", new { uri = "testy://runs/../safe-report/report" })) == Testy.Cli.Mcp.JsonRpc.ResourceNotFound, "Resource traversal accepted.");
        Check(ErrorCode(await h.Client.RequestAsync("resources/read", new { uri = "testy://runs/report" })) == Testy.Cli.Mcp.JsonRpc.ResourceNotFound, "The run id report was mistaken for a report suffix.");
    }

    private static async Task CortexReportLimits()
    {
        await using var h = new Harness("cortex-report-limits"); await h.InitializeAsync();
        var run = WriteReportFixture(h.Workspace, "limits");
        File.WriteAllBytes(run.Steps[0].ScreenshotPath, new byte[24]);
        Check((await h.Client.CallToolAsync("get_run_report", new { runId = run.Id })).IsError, "Non-PNG content accepted.");
        File.WriteAllBytes(run.Steps[0].ScreenshotPath, new byte[4 * 1024 * 1024 + 1]);
        Check((await h.Client.CallToolAsync("get_run_report", new { runId = run.Id })).IsError, "Oversized image accepted.");
        File.WriteAllBytes(run.Steps[0].ScreenshotPath, ReportPng);
        var resolvedImages = 0;
        var repeated = new RunResult { Steps = Enumerable.Repeat(run.Steps[0], 100).ToList() };
        try { TestRunner.RenderHtmlReport(repeated, _ => { resolvedImages++; return "data:image/png;base64," + new string('A', 128 * 1024); }, 512 * 1024); throw new Exception("Renderer ignored its incremental bound."); }
        catch (InvalidDataException) { Check(resolvedImages < 5, "Renderer allocated all repeated images before enforcing its bound."); }
        run.Summary = new string('x', 8 * 1024 * 1024 + 1); new WorkspaceStore(h.Workspace).SaveRun(run);
        Check((await h.Client.CallToolAsync("get_run_report", new { runId = run.Id })).IsError, "Oversized HTML accepted.");
        run.Summary = "safe"; run.ArtifactDirectory = Path.Combine(Root, "foreign-report"); Directory.CreateDirectory(run.ArtifactDirectory); File.WriteAllText(Path.Combine(run.ArtifactDirectory, "report.html"), "secret");
        new WorkspaceStore(h.Workspace).SaveRun(run);
        Check((await h.Client.CallToolAsync("get_run_report", new { runId = run.Id })).IsError, "Foreign artifact folder accepted.");
    }
}
