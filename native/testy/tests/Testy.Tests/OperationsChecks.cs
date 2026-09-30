using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Testy.Core;

namespace Testy.Tests;

internal static class OperationsChecks
{
    internal static IEnumerable<(string Name, Func<Task> Execute)> All()
    {
        yield return ("operations freeze typed workloads and survive a new store instance", FrozenQueue);
        yield return ("operations reject invalid profiles, altered snapshots and future store versions", InvalidStore);
        yield return ("operations queue limits reject excess pending jobs without changing earlier jobs", QueueLimits);
        yield return ("operations desktop lease excludes another process and concurrent borrowers", CrossProcessLease);
        yield return ("operations residual registered child prevents desktop reuse until it exits", ResidualChild);
        yield return ("operations commit claims atomically and dead owners become uncertain without replay", CrashRecovery);
        yield return ("operations busy desktop never claims or dispatches a pending job", BusyDesktop);
        yield return ("operations successful dispatch persists verified terminal result and releases lease", DispatchPass);
        yield return ("operations queued cancellation never dispatches and rerun is a new explicit attempt", QueuedCancellation);
        yield return ("operations cancellation reaches the active executor and pauses its schedule", RunningCancellation);
        yield return ("operations thrown or incomplete executors cannot pass or repeat automatically", UnknownExecutor);
        yield return ("operations schedules coalesce missed ticks and pause after failed occurrences", Schedules);
        yield return ("operations restored pending jobs never automatically resume", RestoredJobs);
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "Testy-operations-checks-" + Guid.NewGuid().ToString("N"));
        internal string StorePath => Path.Combine(Root, "operations");
        internal string LeasePath => Path.Combine(Root, "leases");
        internal Fixture() { Directory.CreateDirectory(Root); }
        internal OperationsStore Store() => new(StorePath);
        internal OperationsDesktopLease? Lease() => OperationsDesktopLease.Acquire(LeasePath, "owned-test-desktop");
        internal OperationsJobRequest Request() => RequestForRoot(Root);
        public void Dispose()
        {
            var full = Path.GetFullPath(Root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("Testy-operations-checks-", StringComparison.Ordinal)) throw new IOException("Unsafe fixture cleanup.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
    internal static OperationsJobRequest RequestForRoot(string root) => new()
        {
            Name = "Owned queued workflow",
            Lifecycle = new()
            {
                Profile = new() { Executable = Environment.ProcessPath!, Name = "Owned lifecycle", Replay = true },
                Test = new() { Name = "Observed acceptance", Steps = [new() { Action = StepAction.AssertExists, Selector = "id:Owned" }] },
                Provider = new() { Kind = ProviderKind.Codex, ProjectTools = new() { Enabled = true, RootDirectory = root, Commands = [new() { Id = "check", Executable = Environment.ProcessPath! }], RequiredBeforeUiCommands = ["check"] } }
            }
        };
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static void Reject(Action action) { try { action(); } catch (Exception e) when (e is InvalidDataException or InvalidOperationException or IOException or ArgumentException) { return; } throw new InvalidOperationException("Expected rejection."); }
    private static LifecycleResult Result(LifecycleStatus status = LifecycleStatus.Passed, bool unknown = false)
    {
        var now = DateTimeOffset.UtcNow;
        return new() { Status = status, StartedAt = now, FinishedAt = now, CleanupComplete = !unknown, ActionOutcomeUnknown = unknown, WorkerPassed = status == LifecycleStatus.Passed,
            Preparation = new() { Completed = true, Status = RunStatus.Passed, StartedAt = now, FinishedAt = now } };
    }
    private static Task FrozenQueue()
    {
        using var f = new Fixture(); var request = f.Request(); var job = f.Store().Enqueue(request);
        request.Name = "changed"; request.Lifecycle!.Test.Steps[0].Selector = "id:other"; job.Request.Name = "changed returned copy";
        var persisted = new OperationsStore(f.StorePath).ListJobs().Single();
        Check(persisted.Request.Name == "Owned queued workflow" && persisted.Request.Lifecycle!.Test.Steps[0].Selector == "id:Owned" && persisted.Status == OperationsJobStatus.Queued, "Frozen typed workload changed.");
        Check(persisted.RequestSha256 == OperationsStorage.Hash(persisted.Request), "Frozen request hash mismatch."); return Task.CompletedTask;
    }
    private static Task InvalidStore()
    {
        using var f = new Fixture(); var request = f.Request(); request.Lifecycle!.Profile.WorkerTimeoutSeconds = 0;
        Reject(() => f.Store().Enqueue(request)); Check(!File.Exists(Path.Combine(f.StorePath, "queue.json")), "Invalid admission wrote queue.");
        f.Store().Enqueue(f.Request()); var path = Path.Combine(f.StorePath, "queue.json"); var original = File.ReadAllText(path);
        File.WriteAllText(path, original.Replace("id:Owned", "id:Edited")); Reject(() => f.Store().ListJobs());
        File.WriteAllText(path, original.Replace("\"formatVersion\": 1", "\"formatVersion\": 999")); Reject(() => f.Store().ListJobs());
        Check(File.ReadAllText(path).Contains("999"), "Future version was overwritten."); return Task.CompletedTask;
    }
    private static Task QueueLimits()
    {
        using var f = new Fixture(); var store = f.Store(); var request = f.Request();
        for (int i = 0; i < OperationsStore.MaximumPending; i++) store.Enqueue(request);
        Reject(() => store.Enqueue(request)); Check(store.ListJobs().Count == OperationsStore.MaximumPending, "Queue overflow changed existing work.");
        foreach (int seconds in new[] { 0, 59, 2592001 }) Reject(() => store.AddSchedule(new() { Request = request, IntervalSeconds = seconds })); return Task.CompletedTask;
    }
    internal static async Task<int> RunHelperAsync(string[] args)
    {
        if (args.Length != 5) return 20;
        string mode = args[1], root = args[2], ready = args[3], release = args[4];
        if (mode == "sleep")
        {
            File.WriteAllText(ready, "ready"); var timer = Stopwatch.StartNew();
            while (!File.Exists(release) && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(25);
            return File.Exists(release) ? 0 : 4;
        }
        if (mode == "hold")
        {
            using var lease = OperationsDesktopLease.Acquire(Path.Combine(root, "leases"), "owned-test-desktop");
            if (lease is null) return 3;
            File.WriteAllText(ready, "locked"); var timer = Stopwatch.StartNew();
            while (!File.Exists(release) && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(25);
            return File.Exists(release) ? 0 : 4;
        }
        if (mode == "vmhold") return await TargetOperationsChecks.HoldVmLeaseAsync(root, ready, release);
        if (mode == "claim")
        {
            using var lease = OperationsDesktopLease.Acquire(Path.Combine(root, "leases"), "owned-test-desktop");
            if (lease is null) return 3;
            var job = new OperationsStore(Path.Combine(root, "operations")).Claim(lease);
            File.WriteAllText(ready, job?.Id ?? "none"); return job is null ? 4 : 0; // Deliberate process exit without terminal completion, no application input.
        }
        return 21;
    }
    private static Process Helper(Fixture f, string mode, string ready, string release)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (var arg in new[] { "--operations-helper", mode, f.Root, ready, release }) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException("Owned helper did not start.");
    }
    private static async Task WaitFile(string file, Process process)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(file)) { if (process.HasExited || timer.Elapsed > TimeSpan.FromSeconds(10)) throw new IOException("Owned helper did not become ready."); await Task.Delay(25); }
    }
    private static async Task CrossProcessLease()
    {
        using var f = new Fixture(); string ready = Path.Combine(f.Root, "ready"), release = Path.Combine(f.Root, "release");
        using var helper = Helper(f, "hold", ready, release);
        try { await WaitFile(ready, helper); using var denied = f.Lease(); Check(denied is null, "Two processes obtained the same lease."); }
        finally { File.WriteAllText(release, "release"); if (!helper.WaitForExit(12000)) { helper.Kill(true); helper.WaitForExit(); } }
        Check(helper.ExitCode == 0, "Owned lease helper failed."); using var lease = f.Lease(); Check(lease is not null, "Lease was not released on process exit.");
        using (lease!.EnterExecution()) { Reject(() => lease.EnterExecution()); Reject(lease.Dispose); }
        using (lease.EnterExecution()) { } // Explicit sequential reuse is allowed, never simultaneous borrowers.
    }
    private static async Task CrashRecovery()
    {
        using var f = new Fixture(); var store = f.Store(); var job = store.Enqueue(f.Request()); var ready = Path.Combine(f.Root, "claim");
        using (var helper = Helper(f, "claim", ready, Path.Combine(f.Root, "unused"))) { await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); Check(helper.ExitCode == 0, "Claim helper failed."); }
        Check(File.ReadAllText(ready) == job.Id, "Claim was not persisted before helper exited.");
        var recovered = store.Reconcile().Single(); Check(recovered.Status == OperationsJobStatus.Interrupted && recovered.ActionOutcomeUnknown && recovered.FinishedAt.HasValue, "Dead-owner input uncertainty was lost.");
        using var lease = f.Lease(); Check(store.Claim(lease!) is null, "Interrupted attempt automatically replayed.");
        var fresh = store.Rerun(job.Id); Check(fresh.Id != job.Id && fresh.PreviousJobId == job.Id && fresh.Status == OperationsJobStatus.Queued, "Rerun did not create fresh identity.");
    }
    private static async Task ResidualChild()
    {
        using var f = new Fixture(); string ready = Path.Combine(f.Root, "ready"), release = Path.Combine(f.Root, "release");
        using var child = Helper(f, "sleep", ready, release);
        try
        {
            await WaitFile(ready, child);
            using (var lease = f.Lease()) { Check(lease is not null, "First lease unavailable."); lease!.RegisterOwnedProcess(child); }
            using var denied = f.Lease(); Check(denied is null, "Desktop reused while a registered child remained alive.");
        }
        finally { File.WriteAllText(release, "done"); if (!child.WaitForExit(12000)) { child.Kill(true); child.WaitForExit(); } }
        using var released = f.Lease(); Check(released is not null, "Dead exact child identity blocked recovery indefinitely.");
    }
    private static async Task BusyDesktop()
    {
        using var f = new Fixture(); var store = f.Store(); store.Enqueue(f.Request()); int calls = 0;
        var dispatcher = new OperationsDispatcher(store, (_, _, _, _) => { calls++; return Task.FromResult(Result()); });
        var result = await dispatcher.RunNextAsync(() => null, CancellationToken.None);
        Check(result.State == "desktopBusy" && calls == 0 && store.ListJobs().Single().Status == OperationsJobStatus.Queued, "Busy desktop changed a job or invoked executor.");
    }
    private static async Task DispatchPass()
    {
        using var f = new Fixture(); var store = f.Store(); var job = store.Enqueue(f.Request()); int calls = 0;
        var dispatcher = new OperationsDispatcher(store, (request, artifacts, _, lease) =>
        {
            using var execution = lease.EnterExecution(); calls++; Check(File.Exists(Path.Combine(artifacts, "frozen-job.json")), "Executor preceded frozen artifact.");
            request.Test.Name = "mutated local copy"; return Task.FromResult(Result());
        });
        var terminal = await dispatcher.RunNextAsync(f.Lease, CancellationToken.None);
        Check(terminal.Job?.Passed == true && calls == 1 && store.ListJobs().Single().Request.Lifecycle!.Test.Name == "Observed acceptance", "Terminal pass or workload copy failed.");
        var idle = await dispatcher.RunNextAsync(f.Lease, CancellationToken.None); Check(idle.State == "idle" && calls == 1, "Terminal job repeated.");
        using var lease = f.Lease(); Check(lease is not null, "Dispatcher retained desktop lease after completion.");
    }
    private static Task QueuedCancellation()
    {
        using var f = new Fixture(); var store = f.Store(); var queued = store.Enqueue(f.Request()); var cancelled = store.RequestCancellation(queued.Id);
        Check(cancelled.Status == OperationsJobStatus.Cancelled && !cancelled.ActionOutcomeUnknown && !cancelled.StartedAt.HasValue, "Queued cancellation implied execution.");
        var fresh = store.Rerun(queued.Id); Check(fresh.Id != queued.Id && fresh.CancellationRequestedAt is null, "Fresh rerun inherited cancellation.");
        Reject(() => store.DeleteTerminalJob(fresh.Id)); store.DeleteTerminalJob(cancelled.Id); Check(store.ListJobs().Count == 1, "Terminal removal affected pending job."); return Task.CompletedTask;
    }
    private static async Task RunningCancellation()
    {
        using var f = new Fixture(); var store = f.Store(); var schedule = store.AddSchedule(new() { Request = f.Request(), FirstRunAt = DateTimeOffset.UtcNow.AddSeconds(-1) });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new OperationsDispatcher(store, async (_, _, ct, lease) => { using var execution = lease.EnterExecution(); started.SetResult(); try { await Task.Delay(10000, ct); } catch (OperationCanceledException) { } return Result(LifecycleStatus.Cancelled); });
        var dispatch = dispatcher.RunNextAsync(f.Lease, CancellationToken.None); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var running = store.ListJobs().Single(); store.RequestCancellation(running.Id); var terminal = await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        Check(terminal.Job?.Status == OperationsJobStatus.Cancelled && !store.ListSchedules().Single().Definition.Enabled, "Cancellation did not terminate and pause scheduled work.");
    }
    private static async Task UnknownExecutor()
    {
        using var f = new Fixture(); var store = f.Store(); store.Enqueue(f.Request()); int calls = 0;
        var dispatcher = new OperationsDispatcher(store, (_, _, _, _) => { calls++; throw new IOException("Deliberately unknown executor outcome"); });
        var run = await dispatcher.RunNextAsync(f.Lease, CancellationToken.None);
        Check(run.Job?.Status == OperationsJobStatus.Interrupted && run.Job.ActionOutcomeUnknown && !run.Job.Passed, "Thrown executor became a known success.");
        await dispatcher.RunNextAsync(f.Lease, CancellationToken.None); Check(calls == 1, "Unknown input was retried.");
        store.Enqueue(f.Request()); var incomplete = new OperationsDispatcher(store, (_, _, _, _) => Task.FromResult(new LifecycleResult { CleanupComplete = true, Status = LifecycleStatus.WorkerRunning }));
        var bad = await incomplete.RunNextAsync(f.Lease, CancellationToken.None); Check(bad.Job?.Passed == false, "Incomplete executor became passed.");
    }
    private static async Task Schedules()
    {
        using var f = new Fixture(); var store = f.Store(); var schedule = store.AddSchedule(new() { Request = f.Request(), FirstRunAt = DateTimeOffset.UtcNow.AddDays(-10), IntervalSeconds = 60 });
        store.Reconcile(); store.Reconcile(); Check(store.ListJobs().Count == 1 && store.ListSchedules().Single().NextRunAt is null, "Missed ticks produced a backlog/duplicate occurrence.");
        var dispatcher = new OperationsDispatcher(store, (_, _, _, _) => Task.FromResult(Result()));
        var result = await dispatcher.RunNextAsync(f.Lease, CancellationToken.None);
        var completed = store.ListSchedules().Single(); Check(result.Job?.Passed == true && completed.ActiveJobId is null && completed.NextRunAt > DateTimeOffset.UtcNow.AddSeconds(50), "Next tick did not start after completion.");
        store.SetScheduleEnabled(schedule.Id, false); store.Reconcile(); Check(store.ListJobs().Count == 1, "Paused schedule fired.");
        store.SetScheduleEnabled(schedule.Id, true); var failure = new OperationsDispatcher(store, (_, _, _, _) => Task.FromResult(Result(LifecycleStatus.Failed)));
        await failure.RunNextAsync(f.Lease, CancellationToken.None); Check(!store.ListSchedules().Single().Definition.Enabled, "Failed occurrence stayed enabled.");
    }
    private static Task RestoredJobs()
    {
        using var f = new Fixture(); var store = f.Store(); store.Enqueue(f.Request()); store.AddSchedule(new() { Request = f.Request() }); store.Reconcile();
        using var lease = f.Lease(); store.Claim(lease!); store.SuspendRestored();
        Check(store.ListJobs().All(j => j.Status is OperationsJobStatus.Cancelled or OperationsJobStatus.Interrupted) && store.ListJobs().Any(j => j.ActionOutcomeUnknown), "Restore retained pending work.");
        Check(store.ListSchedules().All(s => !s.Definition.Enabled), "Restore activated a schedule."); return Task.CompletedTask;
    }
}
