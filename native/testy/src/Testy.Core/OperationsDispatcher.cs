namespace Testy.Core;

public sealed class OperationsDispatcher
{
    private readonly OperationsStore store;
    private readonly OperationsJobExecutor executor;
    private readonly Func<OperationsJobRequest, bool> accepts;

    /// <summary>Historical lifecycle-only executor. It never claims saved-test runs or VM jobs.</summary>
    public OperationsDispatcher(OperationsStore store, OperationsExecutor executor)
        : this(store, (request, artifacts, ct, lease) => executor(request.Lifecycle!, artifacts, ct, lease ?? throw new InvalidOperationException("Lifecycle execution requires the local desktop lease.")),
            request => request.Lifecycle is not null) { }
    /// <summary>Executor for both workload kinds and any target (local desktop or VM).</summary>
    public static OperationsDispatcher ForAllJobs(OperationsStore store, OperationsJobExecutor executor) => new(store, executor, _ => true);
    private OperationsDispatcher(OperationsStore store, OperationsJobExecutor executor, Func<OperationsJobRequest, bool> accepts)
    { this.store = store; this.executor = executor; this.accepts = accepts; }

    /// <summary>Local desktop: the desktop lease and an available (unlocked, connected) input desktop are both required before any claim.</summary>
    public Task<OperationsDispatchResult> RunNextAsync(CancellationToken cancellationToken = default) => RunNextAsync(OperationsDesktopLease.TryAcquire, ObserveDesktop, cancellationToken);
    internal Task<OperationsDispatchResult> RunNextAsync(Func<OperationsDesktopLease?> acquire, CancellationToken cancellationToken) =>
        RunNextAsync(acquire, () => new OperationsTargetReadiness(true, "Owned test desktop."), cancellationToken);
    internal async Task<OperationsDispatchResult> RunNextAsync(Func<OperationsDesktopLease?> acquire, Func<OperationsTargetReadiness> desktop, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var lease = acquire();
        if (lease is null) return new() { State = "desktopBusy", Message = "Another Testy worker owns this desktop. No job was claimed and no action was sent." };
        var ready = desktop();
        if (!ready.Ready) return new() { State = "desktopUnavailable", Message = ready.Reason + " No job was claimed and no action was sent." };
        var job = store.Claim(lease, accepts);
        if (job is null) return new() { State = "idle", Message = "No eligible queued job." };
        return await DispatchAsync(job, lease, OperationsTargets.LocalName, cancellationToken);
    }

    /// <summary>Any target. A VM target holds its own lease, observes guest readiness only when work is eligible, and then claims only that target's first queued job.</summary>
    public Task<OperationsDispatchResult> RunNextAsync(OperationsTarget target, Func<CancellationToken, Task<OperationsTargetReadiness>> readiness, CancellationToken cancellationToken = default) =>
        target.IsLocal ? RunNextAsync(cancellationToken) : RunNextAsync(target, () => OperationsTargetLease.TryAcquire(target), readiness, cancellationToken);
    internal async Task<OperationsDispatchResult> RunNextAsync(OperationsTarget target, Func<OperationsTargetLease?> acquire, Func<CancellationToken, Task<OperationsTargetReadiness>> readiness, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (target.IsLocal) throw new InvalidOperationException("The local desktop uses the desktop lease path.");
        using var lease = acquire();
        if (lease is null) return new() { State = "targetBusy", Target = target.Canonical, Message = "Another Testy run from this Windows user owns this VM. No job was claimed." };
        bool Eligible(OperationsJobRequest request) => OperationsTargets.Parse(request.Target) == target && accepts(request);
        if (!store.HasEligibleWork(Eligible)) return new() { State = "idle", Target = target.Canonical, Message = "No eligible queued job for this VM." };
        OperationsTargetReadiness ready;
        try { ready = await readiness(cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { ready = new(false, "Readiness check failed: " + ex.Message); }
        if (!ready.Ready) return new() { State = "targetUnavailable", Target = target.Canonical, Message = ready.Reason + " No job was claimed and nothing was sent to the VM." };
        var job = store.Claim(lease, accepts);
        if (job is null) return new() { State = "idle", Target = target.Canonical, Message = "No eligible queued job for this VM." };
        return await DispatchAsync(job, null, target.Canonical, cancellationToken);
    }

    private static OperationsTargetReadiness ObserveDesktop() { var desktop = InteractiveDesktop.Observe(); return new(desktop.Available, desktop.Reason); }

    private async Task<OperationsDispatchResult> DispatchAsync(OperationsJob job, OperationsDesktopLease? desktopLease, string target, CancellationToken cancellationToken)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var stopMonitor = new CancellationTokenSource();
        Exception? monitorFailure = null;
        var monitor = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    if (store.CancellationRequested(job.Id, job.ClaimToken)) { cancel.Cancel(); return; }
                    await Task.Delay(100, stopMonitor.Token);
                }
            }
            catch (OperationCanceledException) when (stopMonitor.IsCancellationRequested) { }
            catch (Exception ex) { monitorFailure = ex; cancel.Cancel(); }
        });
        LifecycleResult? result = null; string? error = null; bool dispatched = false;
        try
        {
            Directory.CreateDirectory(job.ArtifactDirectory);
            OperationsStorage.NoReparse(job.ArtifactDirectory);
            OperationsStorage.Write(Path.Combine(job.ArtifactDirectory, "frozen-job.json"), job.Request);
            if (store.CancellationRequested(job.Id, job.ClaimToken)) cancel.Cancel();
            cancel.Token.ThrowIfCancellationRequested();
            // Never detach a still-running delegate on cancellation: it retains desktop/VM ownership until cleanup completes.
            dispatched = true;
            result = await executor(TestyJson.Clone(job.Request), job.ArtifactDirectory, cancel.Token, desktopLease);
        }
        catch (OperationCanceledException)
        {
            error = "Execution cancelled; only verified terminal cleanup can establish a known input outcome.";
            if (!dispatched) result = new() { Status = LifecycleStatus.Cancelled, StartedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow, CleanupComplete = true, Message = "Cancelled before executor dispatch; no command/input sent." };
        }
        catch (Exception) { error = "Executor failed without a verified terminal result. Inspect local evidence; no automatic retry."; }
        finally { stopMonitor.Cancel(); await monitor; }
        if (monitorFailure is not null) { result = null; error = "Cancellation/state monitor failed; input outcome is uncertain."; }
        var terminal = store.Complete(job.Id, job.ClaimToken, result, error, cancel.IsCancellationRequested);
        return new() { State = "completed", Target = target, Job = terminal, Message = terminal.Message };
    }
}
