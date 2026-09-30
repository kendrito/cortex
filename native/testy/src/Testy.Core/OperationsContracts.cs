using System.Text.Json.Serialization;

namespace Testy.Core;

public enum OperationsJobStatus { Queued, Running, Passed, Failed, Cancelled, Interrupted }
/// <summary>Exactly one workload kind. New optional members are omitted when null so earlier frozen request hashes stay byte-identical.</summary>
public sealed class OperationsJobRequest
{
    public string Name { get; set; } = "Queued test";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public LifecycleRequest? Lifecycle { get; set; }
    /// <summary>Null or "local" is this desktop; "vm:&lt;guid&gt;" is one Hyper-V guest reached through the elevated agent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Target { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public OperationsTestRequest? Test { get; set; }
}
/// <summary>A plain saved-test run without an AI preparation phase. Provider null means explicit deterministic replay.</summary>
public sealed class OperationsTestRequest
{
    public TestCase Test { get; set; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ProviderSettings? Provider { get; set; }
    /// <summary>Absolute local path, absolute guest path, "{worker}\..." inside the staged guest worker, or a path relative to StageDirectory.</summary>
    public string Executable { get; set; } = "";
    public bool Probe { get; set; }
    public List<string> TargetArguments { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 900;
    public int StartupTimeoutSeconds { get; set; } = 30;
    public int ShutdownGraceSeconds { get; set; } = 3;
    /// <summary>VM targets only: a host folder copied into the guest run folder before launch.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? StageDirectory { get; set; }
}
public sealed class OperationsJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public OperationsJobRequest Request { get; set; } = new();
    public string RequestSha256 { get; set; } = "";
    public OperationsJobStatus Status { get; set; } = OperationsJobStatus.Queued;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? CancellationRequestedAt { get; set; }
    public int OwnerProcessId { get; set; }
    public long OwnerProcessStartUtcTicks { get; set; }
    public string ClaimToken { get; set; } = "";
    public string? ScheduleId { get; set; }
    public string? PreviousJobId { get; set; }
    public string ArtifactDirectory { get; set; } = "";
    public string Message { get; set; } = "";
    public bool ActionOutcomeUnknown { get; set; }
    public LifecycleResult? Result { get; set; }
    public bool Passed => Status == OperationsJobStatus.Passed && FinishedAt >= StartedAt && StartedAt.HasValue && Result?.Passed == true && !ActionOutcomeUnknown;
    [JsonIgnore] public string TargetLabel => OperationsTargets.Label(Request.Target);
}
public sealed class OperationsScheduleDefinition
{
    public string Name { get; set; } = "Scheduled test";
    public OperationsJobRequest Request { get; set; } = new();
    public int IntervalSeconds { get; set; } = 3600;
    public DateTimeOffset FirstRunAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Enabled { get; set; } = true;
}
public sealed class OperationsSchedule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public OperationsScheduleDefinition Definition { get; set; } = new();
    public DateTimeOffset? NextRunAt { get; set; }
    public string? ActiveJobId { get; set; }
    public string Message { get; set; } = "";
    [JsonIgnore] public string TargetLabel => OperationsTargets.Label(Definition.Request.Target);
}
/// <summary>A consistent copy of the queue taken under one lock. (Change() copies results through JSON, so this must be a class with properties.)</summary>
public sealed class OperationsSnapshot
{
    public List<OperationsJob> Jobs { get; set; } = [];
    public List<OperationsSchedule> Schedules { get; set; } = [];
}
internal sealed class OperationsState
{
    public int FormatVersion { get; set; } = 1;
    public List<OperationsJob> Jobs { get; set; } = [];
    public List<OperationsSchedule> Schedules { get; set; } = [];
}
public sealed class OperationsDispatchResult
{
    /// <summary>idle, desktopBusy, desktopUnavailable, targetBusy, targetUnavailable or completed.</summary>
    public string State { get; set; } = "idle";
    public string Target { get; set; } = OperationsTargets.LocalName;
    public OperationsJob? Job { get; set; }
    public string Message { get; set; } = "";
}
/// <summary>Readiness observed immediately before a claim. An unready target leaves its jobs queued and untouched.</summary>
public sealed record OperationsTargetReadiness(bool Ready, string Reason);
public delegate Task<LifecycleResult> OperationsExecutor(LifecycleRequest request, string artifactsDirectory, CancellationToken cancellationToken, OperationsDesktopLease lease);
/// <summary>Executes either workload kind. The desktop lease is present only for local-desktop targets.</summary>
public delegate Task<LifecycleResult> OperationsJobExecutor(OperationsJobRequest request, string artifactsDirectory, CancellationToken cancellationToken, OperationsDesktopLease? desktopLease);
