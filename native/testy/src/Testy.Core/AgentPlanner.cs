namespace Testy.Core;

public sealed record AgentPumpDecision(string Target, int QueuedJobs);
public sealed record AgentPlan(IReadOnlyList<AgentPumpDecision> Launch, IReadOnlyList<AgentWaitStatus> Waiting, bool ReconcileNeeded);

/// <summary>Pure decision function for the background agent: which targets deserve a short-lived pump right now.
/// It never claims work itself; pumps still re-check leases and readiness before any claim.</summary>
public static class AgentPlanner
{
    public const int DefaultMaximumVmPumps = 4;

    public static AgentPlan Decide(IReadOnlyList<OperationsJob> jobs, IReadOnlyList<OperationsSchedule> schedules, DateTimeOffset now, bool paused,
        OperationsTargetReadiness localDesktop, IReadOnlyDictionary<string, OperationsTargetReadiness> vmReadiness, IReadOnlySet<string> runningPumps,
        IReadOnlyDictionary<string, DateTimeOffset> backoffUntil, Func<OperationsJob, bool> ownerAlive, int maximumVmPumps = DefaultMaximumVmPumps)
    {
        static string? Key(string? target) { try { return OperationsTargets.Parse(target).Canonical; } catch (InvalidDataException) { return null; } }
        bool Due(OperationsSchedule s) => s.Definition.Enabled && s.ActiveJobId is null && s.NextRunAt <= now;
        bool reconcile = schedules.Any(Due) || jobs.Any(j => j.Status == OperationsJobStatus.Running && !ownerAlive(j));

        var work = new Dictionary<string, (int Count, DateTimeOffset Oldest)>(StringComparer.Ordinal);
        void Add(string? target, DateTimeOffset at)
        {
            if (Key(target) is not { } key) return;
            work[key] = work.TryGetValue(key, out var existing) ? (existing.Count + 1, at < existing.Oldest ? at : existing.Oldest) : (1, at);
        }
        foreach (var job in jobs.Where(j => j.Status == OperationsJobStatus.Queued)) Add(job.Request.Target, job.CreatedAt);
        foreach (var schedule in schedules.Where(Due)) Add(schedule.Definition.Request.Target, schedule.NextRunAt ?? now);
        var claimedElsewhere = jobs.Where(j => j.Status == OperationsJobStatus.Running).Select(j => Key(j.Request.Target)).OfType<string>().ToHashSet(StringComparer.Ordinal);

        var launch = new List<AgentPumpDecision>(); var waiting = new List<AgentWaitStatus>();
        int vmPumps = runningPumps.Count(t => t != OperationsTargets.LocalName);
        foreach (var (target, (count, _)) in work.OrderBy(w => w.Key == OperationsTargets.LocalName ? 0 : 1).ThenBy(w => w.Value.Oldest))
        {
            void Wait(string reason) => waiting.Add(new AgentWaitStatus { Target = target, QueuedJobs = count, Reason = reason });
            if (runningPumps.Contains(target)) continue;
            if (paused) { Wait("Paused. Resume the agent to run queued work."); continue; }
            if (claimedElsewhere.Contains(target)) { Wait("Another Testy worker is running a job on this target."); continue; }
            if (backoffUntil.TryGetValue(target, out var until) && until > now) { Wait("Target was not ready; checking again shortly."); continue; }
            if (target == OperationsTargets.LocalName)
            {
                if (!localDesktop.Ready) { Wait(localDesktop.Reason); continue; }
            }
            else
            {
                if (!vmReadiness.TryGetValue(target, out var ready)) { Wait("This VM is not in the current Hyper-V inventory."); continue; }
                if (!ready.Ready) { Wait(ready.Reason); continue; }
                if (vmPumps >= maximumVmPumps) { Wait($"At most {maximumVmPumps} VM runs execute at once."); continue; }
                vmPumps++;
            }
            launch.Add(new AgentPumpDecision(target, count));
        }
        return new AgentPlan(launch, waiting, reconcile);
    }
}
