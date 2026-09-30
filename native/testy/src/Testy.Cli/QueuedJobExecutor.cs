using Testy.Cli.HyperV;
using Testy.Core;

namespace Testy.Cli;

/// <summary>Routes one claimed queue job to its executor: lifecycle profiles and local saved tests on this desktop, saved tests in a VM through PowerShell Direct.</summary>
internal static class QueuedJobExecutor
{
    public static Task<LifecycleResult> ExecuteAsync(OperationsJobRequest request, string artifacts, CancellationToken ct, OperationsDesktopLease? lease)
    {
        if (request.Lifecycle is { } lifecycle) return LifecycleCommand.RunAsync(lifecycle, artifacts, ct, lease ?? throw new InvalidOperationException("Lifecycle jobs require the local desktop lease."));
        var test = request.Test ?? throw new InvalidDataException("Queued job has no workload.");
        var target = OperationsTargets.Parse(request.Target);
        return target.IsLocal ? RunLocalAsync(test, artifacts, ct, lease ?? throw new InvalidOperationException("Local test jobs require the desktop lease."))
            : RunVmAsync(test, target.VirtualMachineId, artifacts, ct);
    }

    private static async Task<LifecycleResult> RunLocalAsync(OperationsTestRequest request, string artifacts, CancellationToken ct, OperationsDesktopLease lease)
    {
        var started = DateTimeOffset.UtcNow;
        var inputs = Path.Combine(artifacts, "inputs"); Directory.CreateDirectory(inputs);
        var test = Path.Combine(inputs, "test.json"); var args = Path.Combine(inputs, "arguments.json"); string? settings = null;
        WorkerCommand.DurableWrite(test, request.Test); WorkerCommand.DurableWrite(args, request.TargetArguments);
        if (request.Provider is not null) { settings = Path.Combine(inputs, "settings.json"); WorkerCommand.DurableWrite(settings, request.Provider); }
        var worker = await WorkerCommand.RunAsync(new WorkerOptions
        {
            TestFile = test, SettingsFile = settings, Executable = request.Executable, TargetArgumentsFile = args, Replay = request.Provider is null, Probe = request.Probe,
            ArtifactsRoot = artifacts, TimeoutSeconds = request.TimeoutSeconds, StartupTimeoutSeconds = request.StartupTimeoutSeconds, ShutdownGraceSeconds = request.ShutdownGraceSeconds, DesktopLease = lease
        }, ct);
        bool passed = worker.Passed && worker.CleanupComplete && !worker.ActionOutcomeUnknown;
        return new LifecycleResult
        {
            StartedAt = started, FinishedAt = DateTimeOffset.UtcNow, PreparationNotApplicable = true, Target = OperationsTargets.LocalName,
            ArtifactDirectory = artifacts, WorkerArtifactDirectory = worker.ArtifactDirectory, WorkerStatus = worker.Status, WorkerPassed = worker.Passed,
            CleanupComplete = worker.CleanupComplete, ActionOutcomeUnknown = worker.ActionOutcomeUnknown, Message = worker.Message,
            Status = passed ? LifecycleStatus.Passed : worker.Status switch { "cancelled" => LifecycleStatus.Cancelled, "timedOut" => LifecycleStatus.TimedOut, _ => LifecycleStatus.Failed }
        };
    }

    private static async Task<LifecycleResult> RunVmAsync(OperationsTestRequest request, Guid vmId, string artifacts, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var run = await VmRunner.RunAsync(vmId, request, artifacts, ct);
        return VmRunner.ToLifecycle(run, started);
    }
}
