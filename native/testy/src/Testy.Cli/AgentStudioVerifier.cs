using System.Diagnostics;
using Testy.Core;
using Testy.Windows;

namespace Testy.Cli;

/// <summary>Read-only check of Studio's agent panel and Machines tab against a workspace whose background agent is running.
/// It only navigates and captures; it changes no queue, setting or credential.</summary>
internal static class AgentStudioVerifier
{
    public static async Task<VerificationReport> VerifyAsync(string executable, string workspace, string artifacts, CancellationToken ct)
    {
        executable = Path.GetFullPath(executable); workspace = Path.GetFullPath(workspace); artifacts = Path.GetFullPath(artifacts);
        if (!File.Exists(executable) || Path.GetFileName(executable) != "Testy.Studio.exe") throw new ArgumentException("The Testy Studio executable is required.");
        Directory.CreateDirectory(artifacts); var report = new VerificationReport { Executable = executable, PlannedChecks = 3 };
        Process? process = null; string stage = "Launch Studio on the agent's workspace";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(3)); var token = deadline.Token;
        using var driver = new UiAutomationDriver { MaxElements = 5000 };
        try
        {
            var start = StudioLaunch.StartInfo(executable, workspace, hidden: false);
            process = Process.Start(start) ?? throw new IOException("Studio did not start."); report.OwnedProcessId = process.Id;
            await Wait(() => { process.Refresh(); return process.MainWindowHandle != 0; }); await driver.AttachAsync(process.Id, token);
            await Act("NavWorkflows"); await WaitFor(s => s.Elements.Any(e => e.AutomationId == "AgentStatus")); // Automate opens on Background runs.
            Pass(stage);

            stage = "Agent panel shows the running elevated agent";
            await Act("WorkflowTabJobs");
            var jobs = await WaitFor(s => s.Elements.Any(e => e.AutomationId == "AgentStatus" && e.Name.StartsWith("Running", StringComparison.Ordinal)));
            Require(jobs.Elements.Any(e => e.AutomationId == "AgentStatus" && e.Name.Contains("administrator", StringComparison.Ordinal)), "The agent is not reported as elevated.");
            Require(jobs.Elements.Any(e => e.AutomationId == "ProcessOneJob" && !e.IsEnabled), "Studio's own worker stayed enabled while the agent runs.");
            await Capture("agent-panel"); Pass(stage);

            stage = "Machines tab lists Hyper-V VMs with readiness";
            await Act("WorkflowTabMachines");
            var machines = await WaitFor(s => UiSelectors.Find(s, "query:{\"type\":\"DataItem\",\"ancestor\":{\"id\":\"MachinesGrid\"}}").Count > 0);
            Require(machines.Elements.Any(e => e.AutomationId == "MachinesStatus" && e.Name.Contains("VM(s) found", StringComparison.Ordinal)), "Machines status did not report the inventory.");
            await Capture("machines"); Pass(stage);
            stage = "Close Studio";
            await Act("NavLibrary"); // Leaving Automate removes its page from the window.
            await WaitFor(s => !s.Elements.Any(e => e.AutomationId == "WorkflowStatus"));
            process.CloseMainWindow(); await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(20), token);
            report.CompletedAllScenarios = true;
        }
        catch (Exception ex)
        {
            try { WorkspaceStore.WriteAtomic(Path.Combine(artifacts, "failure-tree.json"), await driver.SnapshotAsync(CancellationToken.None)); await driver.CaptureAsync(Path.Combine(artifacts, "failure.png"), CancellationToken.None); } catch { }
            report.Checks.Add(new() { Name = stage, Driver = "Studio UIA", Passed = false, Expected = RunStatus.Passed, Actual = ex is OperationCanceledException ? RunStatus.Cancelled : RunStatus.Failed, Message = ex.ToString() });
        }
        finally
        {
            if (process != null) { try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } } catch { } process.Dispose(); }
            report.Finish(ct); WorkspaceStore.WriteAtomic(Path.Combine(artifacts, "agent-studio-report.json"), report);
        }
        return report;
        void Pass(string name) => report.Checks.Add(new() { Name = name, Driver = "Studio UIA", Passed = true, Expected = RunStatus.Passed, Actual = RunStatus.Passed, Message = "Observed in Studio's UI Automation tree." });
        async Task Wait(Func<bool> condition) { var watch = Stopwatch.StartNew(); while (!condition()) { token.ThrowIfCancellationRequested(); if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Studio did not show its window."); await Task.Delay(100, token); } }
        async Task<UiSnapshot> WaitFor(Func<UiSnapshot, bool> condition)
        {
            for (int i = 0; i < 150; i++) { var snapshot = await driver.SnapshotAsync(token); if (!snapshot.IsTruncated && condition(snapshot)) return snapshot; await Task.Delay(200, token); }
            throw new TimeoutException("Expected Studio state did not appear.");
        }
        async Task Act(string id) => await driver.ExecuteAsync(new() { Action = StepAction.Click, Selector = "id:" + id, TimeoutMs = 10000 }, token);
        async Task Capture(string name) { WorkspaceStore.WriteAtomic(Path.Combine(artifacts, name + ".json"), await driver.SnapshotAsync(token)); await driver.CaptureAsync(Path.Combine(artifacts, name + ".png"), token); }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    }
}
