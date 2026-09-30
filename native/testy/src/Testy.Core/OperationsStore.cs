using System.Diagnostics;

namespace Testy.Core;

/// <summary>Durable local same-user queue. A process lock serializes whole-state atomic transitions across hosts.</summary>
public sealed class OperationsStore
{
    public const int MaximumJobs = 1000, MaximumPending = 100, MaximumSchedules = 50;
    public string RootDirectory { get; }
    private string StatePath => Path.Combine(RootDirectory, "queue.json");
    public OperationsStore(string directory) { RootDirectory = OperationsStorage.Root(directory, true); }
    private T Change<T>(Func<OperationsState, T> operation, bool write = true)
    {
        using var gate = OperationsStorage.Lock(Path.Combine(RootDirectory, "queue.lock"));
        var state = File.Exists(StatePath) ? OperationsStorage.Read<OperationsState>(StatePath) : new();
        ValidateState(state);
        var result = operation(state);
        // Format 2 marks stores holding saved-test or targeted jobs, so older builds stop with an explicit
        // version message instead of misreporting unfamiliar fields as a modified snapshot.
        state.FormatVersion = UsesFormat2(state) ? 2 : 1;
        ValidateState(state);
        if (write) OperationsStorage.Write(StatePath, state);
        return TestyJson.Clone(result);
    }
    private static bool UsesFormat2(OperationsState state) =>
        state.Jobs.Any(j => j.Request.Test is not null || j.Request.Target is not null) || state.Schedules.Any(s => s.Definition.Request.Test is not null || s.Definition.Request.Target is not null);
    /// <summary>Jobs and schedules from one locked read, for pollers that must not add lock traffic.</summary>
    public OperationsSnapshot Snapshot() => Change(s => new OperationsSnapshot { Jobs = s.Jobs.ToList(), Schedules = s.Schedules.ToList() }, false);
    public static void ValidateRequest(OperationsJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 500) throw new InvalidDataException("Job name must contain 1–500 characters.");
        if (!OperationsTargets.IsCanonical(request.Target)) throw new InvalidDataException("Job target must use its canonical spelling: local or vm:<lowercase VM GUID>.");
        var target = OperationsTargets.Parse(request.Target);
        if ((request.Lifecycle is null) == (request.Test is null)) throw new InvalidDataException("A job must contain exactly one workload: a lifecycle profile or a saved test run.");
        if (request.Lifecycle is not null)
        {
            if (!target.IsLocal) throw new InvalidDataException("Lifecycle profiles run on this PC; VM targets accept saved test runs.");
            LifecycleValidation.ValidateRequest(request.Lifecycle);
        }
        else OperationsTestValidation.Validate(request.Test!, target);
    }
    internal static void ValidateState(OperationsState state)
    {
        if (state.FormatVersion is not (1 or 2)) throw new InvalidDataException("Unsupported operations format version; do not overwrite this store.");
        if (state.Jobs is null || state.Schedules is null || state.Jobs.Count > MaximumJobs || state.Schedules.Count > MaximumSchedules)
            throw new InvalidDataException("Invalid or excessive operations records.");
        if (state.Jobs.Select(j => j.Id).Distinct(StringComparer.Ordinal).Count() != state.Jobs.Count || state.Schedules.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != state.Schedules.Count)
            throw new InvalidDataException("Duplicate operations identities.");
        foreach (var job in state.Jobs)
        {
            TestValidator.ValidateId(job.Id); ValidateRequest(job.Request);
            if (!Enum.IsDefined(job.Status) || job.RequestSha256 != OperationsStorage.Hash(job.Request)) throw new InvalidDataException("Invalid or modified frozen job.");
            if (job.Status == OperationsJobStatus.Running && (job.OwnerProcessId < 1 || job.OwnerProcessStartUtcTicks <= 0 || string.IsNullOrEmpty(job.ClaimToken) || !job.StartedAt.HasValue || job.FinishedAt.HasValue))
                throw new InvalidDataException("Running job lacks an exact owner claim.");
            if (job.Status is not (OperationsJobStatus.Queued or OperationsJobStatus.Running) && (!job.FinishedAt.HasValue || job.FinishedAt < job.CreatedAt)) throw new InvalidDataException("Terminal job lacks valid completion time.");
            if (job.Status == OperationsJobStatus.Passed && !job.Passed) throw new InvalidDataException("Passing job lacks verified lifecycle completion.");
        }
        foreach (var schedule in state.Schedules) { TestValidator.ValidateId(schedule.Id); ValidateSchedule(schedule.Definition); }
    }
    private static void ValidateSchedule(OperationsScheduleDefinition definition)
    {
        if (definition is null || string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length > 500 || definition.IntervalSeconds is < 60 or > 2592000 || definition.FirstRunAt == default)
            throw new InvalidDataException("Schedules require a name, initial UTC instant and interval60 seconds–30 days.");
        ValidateRequest(definition.Request);
    }
    public OperationsJob Enqueue(OperationsJobRequest request)
    {
        var frozen = TestyJson.Clone(request); ValidateRequest(frozen);
        return Change(state => AddJob(state, frozen, DateTimeOffset.UtcNow));
    }
    private OperationsJob AddJob(OperationsState state, OperationsJobRequest request, DateTimeOffset now, string? scheduleId = null, string? previous = null)
    {
        if (state.Jobs.Count >= MaximumJobs || state.Jobs.Count(j => j.Status is OperationsJobStatus.Queued or OperationsJobStatus.Running) >= MaximumPending)
            throw new InvalidDataException("Queue limit reached; remove terminal records or wait for pending jobs.");
        var job = new OperationsJob { Request = TestyJson.Clone(request), CreatedAt = now, ScheduleId = scheduleId, PreviousJobId = previous };
        job.RequestSha256 = OperationsStorage.Hash(job.Request);
        job.ArtifactDirectory = OperationsStorage.Relative(RootDirectory, "attempts/" + job.Id);
        state.Jobs.Add(job); return job;
    }
    public IReadOnlyList<OperationsJob> ListJobs() => Change(s => s.Jobs.ToList(), false);
    /// <summary>Read-only: a queued job or a due enabled schedule matches. Lets a VM pump skip slow guest checks when idle.</summary>
    internal bool HasEligibleWork(Func<OperationsJobRequest, bool> eligible) => Change(state =>
        state.Jobs.Any(j => j.Status == OperationsJobStatus.Queued && eligible(j.Request))
        || state.Schedules.Any(s => s.Definition.Enabled && s.ActiveJobId is null && s.NextRunAt <= DateTimeOffset.UtcNow && eligible(s.Definition.Request)), false);
    public IReadOnlyList<OperationsSchedule> ListSchedules() => Change(s => s.Schedules.ToList(), false);
    public OperationsJob RequestCancellation(string id) => Change(state =>
    {
        var job = Find(state, id);
        if (job.Status is OperationsJobStatus.Queued or OperationsJobStatus.Running)
        {
            job.CancellationRequestedAt ??= DateTimeOffset.UtcNow;
            if (job.Status == OperationsJobStatus.Queued) { job.Status = OperationsJobStatus.Cancelled; job.FinishedAt = DateTimeOffset.UtcNow; job.Message = "Cancelled before claim; no executor dispatched."; Pause(state, job); }
        }
        return job;
    });
    public OperationsJob Rerun(string id) => Change(state =>
    {
        var previous = Find(state, id);
        if (previous.Status is OperationsJobStatus.Queued or OperationsJobStatus.Running) throw new InvalidOperationException("A pending job cannot be rerun.");
        return AddJob(state, previous.Request, DateTimeOffset.UtcNow, previous: previous.Id);
    });
    public void DeleteTerminalJob(string id) => Change(state =>
    {
        var job = Find(state, id);
        if (job.Status is OperationsJobStatus.Queued or OperationsJobStatus.Running) throw new InvalidOperationException("Cannot delete a pending attempt.");
        state.Jobs.Remove(job); return true;
    });
    public OperationsSchedule AddSchedule(OperationsScheduleDefinition definition)
    {
        definition = TestyJson.Clone(definition); ValidateSchedule(definition);
        return Change(state =>
        {
            if (state.Schedules.Count >= MaximumSchedules) throw new InvalidDataException("At most50 schedules are supported.");
            var schedule = new OperationsSchedule { Definition = definition, NextRunAt = definition.Enabled ? definition.FirstRunAt : null };
            state.Schedules.Add(schedule); return schedule;
        });
    }
    public OperationsSchedule SetScheduleEnabled(string id, bool enabled) => Change(state =>
    {
        TestValidator.ValidateId(id); var schedule = state.Schedules.SingleOrDefault(s => s.Id == id) ?? throw new KeyNotFoundException("Schedule not found.");
        schedule.Definition.Enabled = enabled;
        schedule.NextRunAt = enabled && schedule.ActiveJobId is null ? DateTimeOffset.UtcNow : null;
        schedule.Message = enabled ? "Explicitly enabled; next fresh occurrence is eligible." : "Paused by the user; an already queued/running occurrence must be cancelled separately.";
        return schedule;
    });
    public void DeleteSchedule(string id) => Change(state =>
    {
        TestValidator.ValidateId(id); var schedule = state.Schedules.SingleOrDefault(s => s.Id == id) ?? throw new KeyNotFoundException("Schedule not found.");
        if (schedule.ActiveJobId is not null) throw new InvalidOperationException("Cancel/finish the current occurrence before deleting its schedule.");
        state.Schedules.Remove(schedule); return true;
    });
    public IReadOnlyList<OperationsJob> Reconcile() => Change(state => { Reconcile(state, DateTimeOffset.UtcNow); return state.Jobs.ToList(); });
    /// <summary>Read-only observation for supervisors deciding whether an explicit Reconcile is worthwhile. Unknown liveness counts as alive.</summary>
    public static bool IsClaimOwnerAlive(OperationsJob job) => OwnerAlive(job);
    private static bool OwnerAlive(OperationsJob job)
    {
        try { using var process = Process.GetProcessById(job.OwnerProcessId); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == job.OwnerProcessStartUtcTicks; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; } // Cannot establish death: never steal a live/unknown owner.
    }
    private void Reconcile(OperationsState state, DateTimeOffset now)
    {
        foreach (var job in state.Jobs.Where(j => j.Status == OperationsJobStatus.Running && !OwnerAlive(j)))
        {
            job.Status = OperationsJobStatus.Interrupted; job.FinishedAt = now; job.ActionOutcomeUnknown = true;
            job.Message = "The owning process exited before a durable terminal result. Input/command outcome is unknown. Inspect evidence and explicitly request a fresh rerun.";
            Pause(state, job);
        }
        foreach (var schedule in state.Schedules.Where(s => s.Definition.Enabled && s.ActiveJobId is null && s.NextRunAt <= now))
        {
            if (state.Jobs.Count >= MaximumJobs || state.Jobs.Count(j => j.Status is OperationsJobStatus.Queued or OperationsJobStatus.Running) >= MaximumPending) break;
            var job = AddJob(state, schedule.Definition.Request, now, schedule.Id); schedule.ActiveJobId = job.Id; schedule.NextRunAt = null;
        }
    }
    private static void Pause(OperationsState state, OperationsJob job)
    {
        var schedule = state.Schedules.SingleOrDefault(s => s.Id == job.ScheduleId);
        if (schedule is null) return;
        schedule.Definition.Enabled = false; schedule.NextRunAt = null; schedule.ActiveJobId = null;
        schedule.Message = "Previous occurrence did not pass. Schedule paused; review it before explicit re-enable or fresh rerun.";
    }
    private static OperationsJob Find(OperationsState state, string id) { TestValidator.ValidateId(id); return state.Jobs.SingleOrDefault(j => j.Id == id) ?? throw new KeyNotFoundException("Job not found."); }
    internal OperationsJob? Claim(OperationsDesktopLease lease) => Claim(lease, _ => true);
    /// <summary>Claims the first queued local-desktop job this executor accepts. VM jobs are never claimed with a desktop lease.</summary>
    internal OperationsJob? Claim(OperationsDesktopLease lease, Func<OperationsJobRequest, bool> accepts)
    {
        lease.AssertHeld();
        return ClaimFirst(request => OperationsTargets.Parse(request.Target).IsLocal && accepts(request));
    }
    /// <summary>Claims the first queued job for exactly the leased VM target.</summary>
    internal OperationsJob? Claim(OperationsTargetLease lease, Func<OperationsJobRequest, bool> accepts)
    {
        lease.AssertHeld(); var target = lease.Target;
        return ClaimFirst(request => OperationsTargets.Parse(request.Target) == target && accepts(request));
    }
    private OperationsJob? ClaimFirst(Func<OperationsJobRequest, bool> eligible)
    {
        return Change(state =>
        {
            Reconcile(state, DateTimeOffset.UtcNow);
            var job = state.Jobs.FirstOrDefault(j => j.Status == OperationsJobStatus.Queued && eligible(j.Request));
            if (job is null) return null;
            using var owner = Process.GetCurrentProcess();
            job.Status = OperationsJobStatus.Running; job.StartedAt = DateTimeOffset.UtcNow; job.OwnerProcessId = owner.Id;
            job.OwnerProcessStartUtcTicks = owner.StartTime.ToUniversalTime().Ticks; job.ClaimToken = Guid.NewGuid().ToString("N");
            job.Message = "Claim committed before executor dispatch."; return job;
        });
    }
    internal bool CancellationRequested(string id, string claim) => Change(state => { var job = Find(state, id); VerifyClaim(job, claim); return job.CancellationRequestedAt.HasValue; }, false);
    internal OperationsJob Complete(string id, string claim, LifecycleResult? result, string? error, bool cancelled) => Change(state =>
    {
        var job = Find(state, id); VerifyClaim(job, claim);
        job.FinishedAt = DateTimeOffset.UtcNow; job.Result = result is null ? null : TestyJson.Clone(result);
        bool validTime = result?.StartedAt >= job.StartedAt && result.FinishedAt >= result.StartedAt && result.FinishedAt <= job.FinishedAt;
        // A saved-test run has no preparation phase; a lifecycle always has one. A result of the wrong kind cannot pass.
        bool kindMatches = result is null || (job.Request.Test is not null ? result.PreparationNotApplicable && result.Preparation is null : !result.PreparationNotApplicable);
        if (!kindMatches) error ??= "Executor returned a result for a different workload kind; outcome is uncertain.";
        job.ActionOutcomeUnknown = result is null || !kindMatches || result.ActionOutcomeUnknown || !result.CleanupComplete || !validTime
            || result.Status is LifecycleStatus.Preparing or LifecycleStatus.Prepared or LifecycleStatus.WorkerStarting or LifecycleStatus.WorkerRunning;
        if (job.ActionOutcomeUnknown) job.Status = OperationsJobStatus.Interrupted;
        else if (cancelled || job.CancellationRequestedAt.HasValue || result?.Status == LifecycleStatus.Cancelled) job.Status = OperationsJobStatus.Cancelled;
        else job.Status = result?.Passed == true && validTime ? OperationsJobStatus.Passed : OperationsJobStatus.Failed;
        job.Message = error ?? result?.Message ?? "Executor did not return a terminal result; no automatic retry.";
        var schedule = state.Schedules.SingleOrDefault(s => s.Id == job.ScheduleId);
        if (schedule is not null)
        {
            if (job.Passed) { schedule.ActiveJobId = null; schedule.NextRunAt = schedule.Definition.Enabled ? job.FinishedAt.Value.AddSeconds(schedule.Definition.IntervalSeconds) : null; }
            else Pause(state, job);
        }
        return job;
    });
    private static void VerifyClaim(OperationsJob job, string claim)
    {
        if (job.Status != OperationsJobStatus.Running || job.ClaimToken != claim || job.OwnerProcessId != Environment.ProcessId || !OwnerAlive(job)) throw new InvalidOperationException("The job claim is no longer owned by this execution.");
    }
    internal void SuspendRestored()
    {
        Change(state =>
        {
            foreach (var job in state.Jobs.Where(j => j.Status is OperationsJobStatus.Queued or OperationsJobStatus.Running))
            {
                job.ActionOutcomeUnknown = job.Status == OperationsJobStatus.Running;
                job.Status = job.ActionOutcomeUnknown ? OperationsJobStatus.Interrupted : OperationsJobStatus.Cancelled;
                job.FinishedAt = DateTimeOffset.UtcNow; job.Message = "Restored from a backup. No attempt resumes automatically; explicitly request a fresh rerun.";
            }
            foreach (var schedule in state.Schedules) { schedule.Definition.Enabled = false; schedule.ActiveJobId = null; schedule.NextRunAt = null; schedule.Message = "Paused on workspace restore."; }
            return true;
        });
    }
}
