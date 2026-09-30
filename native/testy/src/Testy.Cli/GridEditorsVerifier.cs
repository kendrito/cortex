using System.Diagnostics;
using System.Text.Json;
using Testy.Core;
using Testy.Windows;

namespace Testy.Cli;

internal static class GridEditorsVerifier
{
    internal static async Task<VerificationReport> VerifyAsync(string executable, string artifacts, CancellationToken ct)
    {
        executable = Path.GetFullPath(executable); artifacts = Path.GetFullPath(artifacts); Directory.CreateDirectory(artifacts);
        if (!File.Exists(executable) || Path.GetFileName(executable) != "Testy.WpfLab.exe") throw new ArgumentException("Grid editor verification requires owned Testy.WpfLab.exe.");
        var report = new VerificationReport { Executable = executable, PlannedChecks = 17 };
        Process? process = null; using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct); limit.CancelAfter(TimeSpan.FromMinutes(6)); var token = limit.Token;
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden }; start.ArgumentList.Add("--grid-editors");
            process = Process.Start(start) ?? throw new InvalidOperationException("Owned fixture did not start."); report.OwnedProcessId = process.Id;
            await DesktopVerifierSupport.Ready(process, token);
            using var driver = new WpfProbeDriver(); await driver.AttachAsync(process.Id, token);
            foreach (var editor in new[] { ("Enabled", "true", "ROW-1|true|Alpha|seed|false|Alpha"), ("Category", "Beta", "ROW-1|false|Beta|seed|false|Alpha"), ("TemplateNote", "typed note", "ROW-1|false|Alpha|typed note|false|Alpha"), ("TemplateFlag", "true", "ROW-1|false|Alpha|seed|true|Alpha"), ("TemplateChoice", "Beta", "ROW-1|false|Alpha|seed|false|Beta") })
            {
                await Check(editor.Item1 + " commits through the actual editor", [Reset(), Edit(editor.Item1, editor.Item2), Row(StepAction.GridCommitRow), Assert(editor.Item3)]);
                await Check(editor.Item1 + " cancellation restores transaction", [Reset(), Edit(editor.Item1, editor.Item2), Row(StepAction.GridCancelRow), Assert(Default), new() { Action = StepAction.AssertText, Selector = "id:EditorCounters", Value = "begin=1;commit=0;cancel=1", TimeoutMs = 6000 }]);
            }
            await Check("Duplicate combo labels reject selection", [Reset(), Toggle("DuplicateChoices"), Edit("Category", "Beta")], "ambiguous");
            await Check("Ambiguous template editor identity rejects input", [Reset(), Toggle("AmbiguousTemplate"), Edit("TemplateNote", "forbidden")], "unique");
            await Check("Invalid checkbox text rejected", [Reset(), Edit("Enabled", "yes")], "exactly");
            await Check("Unapproved template rejected", [Reset(), Edit("Unsupported", "forbidden")], "opt in");
            await Check("Unknown business row rejected", [Reset(), Edit("Category", "Beta", "MISSING")], "No grid row");
            await Check("Exact row/column survive sorting and column reordering", [Reset(), new() { Action = StepAction.Click, Selector = "id:SortEditors" }, new() { Action = StepAction.Click, Selector = "id:ReorderEditors" }, Edit("Category", "Beta"), Row(StepAction.GridCommitRow), Assert("ROW-1|false|Beta|seed|false|Alpha")]);
            using var uia = new UiAutomationDriver(); await uia.AttachAsync(process.Id, token);
            await driver.ExecuteAsync(Reset(), token);
            var unsupported = await new TestRunner(uia, Path.Combine(artifacts, "uia-rejection")).RunAsync(new() { Name = "UIA cannot claim transactional editors", Steps = [Edit("Enabled", "true")] }, cancellationToken: token);
            bool rejected = unsupported.Status == RunStatus.Failed && unsupported.Steps.Single().FailureDiagnostics.Any(d => d.Category == FailureCategory.CapabilityUnavailable);
            var unchanged = await driver.SnapshotAsync(token);
            DesktopVerifierSupport.Require(UiSelectors.Find(unchanged, "id:EditorPersisted").Single().Value == Default, "UIA capability rejection changed persistence.");
            report.Checks.Add(new() { Name = "UIA rejects unsupported transactional editor capability", Driver = "UIA", Passed = rejected, Expected = RunStatus.Failed, Actual = unsupported.Status, RunDirectory = unsupported.ArtifactDirectory });
            token.ThrowIfCancellationRequested(); report.CompletedAllScenarios = true;

            async Task Check(string name, List<TestStep> steps, string? rejection = null)
            {
                foreach (var step in steps) { step.TimeoutMs = 6000; if (step.Title.Length == 0) step.Title = step.Action.ToString(); }
                var run = await new TestRunner(driver, Path.Combine(artifacts, $"check-{report.Checks.Count + 1:00}")).RunAsync(new() { Name = name, Steps = steps }, cancellationToken: token);
                bool okay = rejection is null ? run.Status == RunStatus.Passed && run.Steps.All(s => s.Status == RunStatus.Passed)
                    : run.Status == RunStatus.Failed && run.Steps.Last().Status == RunStatus.Failed && run.Steps.Last().Message.Contains(rejection, StringComparison.OrdinalIgnoreCase);
                string message = run.Summary;
                foreach (var step in run.Steps.Where(s => s.Status is RunStatus.Passed or RunStatus.Failed))
                    if (step.Snapshot is null || step.Snapshot.IsTruncated || step.Snapshot.Target.ProcessId != process.Id || !File.Exists(step.ScreenshotPath)) { okay = false; message = "Missing complete owned tree/image evidence."; }
                if (rejection is not null)
                {
                    var after = await driver.SnapshotAsync(token); WorkspaceStore.WriteAtomic(Path.Combine(run.ArtifactDirectory, "after-negative.json"), after);
                    if (UiSelectors.Find(after, "id:EditorPersisted").Single().Value != Default) { okay = false; message = "Rejected edit changed persisted state."; }
                }
                report.Checks.Add(new() { Name = name, Driver = "Opt-in WPF standard editors", Passed = okay, Expected = rejection is null ? RunStatus.Passed : RunStatus.Failed, Actual = run.Status, Message = message, RunDirectory = run.ArtifactDirectory });
                WorkspaceStore.WriteAtomic(Path.Combine(artifacts, "grid-editors-report.json"), report);
            }
        }
        catch (Exception ex) { report.Checks.Add(new() { Name = "Grid editor harness completion", Passed = false, Actual = ex is OperationCanceledException ? RunStatus.Cancelled : RunStatus.Failed, Message = ex.ToString() }); }
        finally
        {
            if (!await DesktopVerifierSupport.Stop(process)) report.CompletedAllScenarios = false;
            report.Finish(ct); WorkspaceStore.WriteAtomic(Path.Combine(artifacts, "grid-editors-report.json"), report);
        }
        return report;
    }
    private const string Default = "ROW-1|false|Alpha|seed|false|Alpha";
    private static TestStep Reset() => new() { Action = StepAction.Click, Selector = "id:ResetEditors" };
    private static TestStep Toggle(string id) => new() { Action = StepAction.Toggle, Selector = "id:" + id, Value = "true" };
    private static TestStep Edit(string column, string value, string row = "ROW-1") => new() { Action = StepAction.GridEditCell, Selector = "id:EditorGrid", Value = JsonSerializer.Serialize(new { rowKey = row, columnKey = column, text = value }), TimeoutMs = 6000 };
    private static TestStep Row(StepAction action) => new() { Action = action, Selector = "id:EditorGrid", Value = "{\"rowKey\":\"ROW-1\"}" };
    private static TestStep Assert(string value) => new() { Action = StepAction.AssertText, Selector = "id:EditorPersisted", Value = value };
}

internal static class DesktopVerifierSupport
{
    internal static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static async Task Ready(Process process, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(12)) { ct.ThrowIfCancellationRequested(); process.Refresh(); if (process.HasExited) throw new InvalidOperationException("Owned fixture exited during startup."); if (process.MainWindowHandle != 0) { await Task.Delay(600, ct); return; } await Task.Delay(100, ct); }
        throw new TimeoutException("Owned fixture did not create its window.");
    }
    internal static async Task<bool> Stop(Process? process)
    {
        if (process is null) return true;
        try { if (!process.HasExited) { process.CloseMainWindow(); try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } } return process.HasExited; }
        catch { return false; } finally { process.Dispose(); }
    }
}
