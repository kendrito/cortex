namespace Testy.Core;

/// <summary>Host-authored paths and limits. Referenced files are frozen into LifecycleRequest before preparation.</summary>
public sealed class LifecycleProfile
{
    public const string CurrentSchema = "testy.lifecycle.v1";
    /// <summary>Profiles saved before the product was renamed from Axiom; still accepted when loading.</summary>
    public const string LegacySchema = "axiom.lifecycle.v1";
    public string Schema { get; set; } = CurrentSchema;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Prepare and test";
    public string TestFile { get; set; } = "";
    public string SettingsFile { get; set; } = "";
    public string? ProjectFile { get; set; }
    public string Executable { get; set; } = "";
    public string? TargetArgumentsFile { get; set; }
    public bool Probe { get; set; }
    public bool Replay { get; set; }
    public string PreparationInstructions { get; set; } = "Inspect the selected project and execute every required preparation command. Stop on failure; complete preparation only when all prerequisites are verified.";
    public int MaximumPreparationTurns { get; set; } = 20;
    public int PreparationTimeoutSeconds { get; set; } = 600;
    public int WorkerTimeoutSeconds { get; set; } = 900;
    public int StartupTimeoutSeconds { get; set; } = 20;
    public int ShutdownGraceSeconds { get; set; } = 3;
}

/// <summary>Immutable-by-copy workload used by direct runs and durable queues. No referenced configuration is reloaded during execution.</summary>
public sealed class LifecycleRequest
{
    public LifecycleProfile Profile { get; set; } = new();
    public TestCase Test { get; set; } = new();
    public ProviderSettings Provider { get; set; } = new();
    public List<string> TargetArguments { get; set; } = [];
}

public enum LifecycleStatus { Preparing, Prepared, WorkerStarting, WorkerRunning, Passed, Failed, Cancelled, TimedOut, NeedsReview, InvalidConfiguration }
public sealed class LifecycleResult
{
    public string Schema { get; set; } = "testy.lifecycle-result.v1";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public LifecycleStatus Status { get; set; } = LifecycleStatus.Preparing;
    public bool Passed => Status == LifecycleStatus.Passed && FinishedAt >= StartedAt && (Preparation?.Passed == true || PreparationNotApplicable && Preparation is null)
        && WorkerPassed && CleanupComplete && !ActionOutcomeUnknown;
    /// <summary>True only for a queued plain test run, which has no AI preparation phase to pass.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] public bool PreparationNotApplicable { get; set; }
    /// <summary>Where the workload ran: "local" or "vm:&lt;guid&gt;". Empty for historical lifecycle results.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public string? Target { get; set; }
    public int ExitCode => Passed ? 0 : Status == LifecycleStatus.InvalidConfiguration ? 2 : 1;
    public string Message { get; set; } = "";
    public string ArtifactDirectory { get; set; } = "";
    public string FrozenRequestSha256 { get; set; } = "";
    public string WorkerArtifactDirectory { get; set; } = "";
    public string WorkerStatus { get; set; } = "";
    public bool WorkerPassed { get; set; }
    public bool CleanupComplete { get; set; }
    public bool ActionOutcomeUnknown { get; set; }
    public PreparationResult? Preparation { get; set; }
    public List<LifecycleCheckpoint> Checkpoints { get; set; } = [];
}
public sealed class LifecycleCheckpoint
{
    public int Sequence { get; set; }
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Stage { get; set; } = "";
    public string Message { get; set; } = "";
    public bool MayHaveSideEffects { get; set; }
}
public sealed class PreparationResult
{
    public bool Completed { get; set; }
    public bool Passed => Completed && Status == RunStatus.Passed && FinishedAt >= StartedAt && !ActionOutcomeUnknown && ProjectEvidence.All(e => !e.BlocksFurtherActions);
    public RunStatus Status { get; set; } = RunStatus.Running;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public int ModelTurns { get; set; }
    public string Message { get; set; } = "";
    public string PendingTool { get; set; } = "";
    public bool ActionOutcomeUnknown { get; set; }
    public List<ProjectToolResult> ProjectEvidence { get; set; } = [];
}
public interface IProjectPreparationAgent
{
    Task<PreparationResult> RunAsync(string instructions, int maximumTurns, CancellationToken cancellationToken = default);
}
public sealed class LifecycleWorkerOutcome
{
    public bool Passed { get; set; }
    public string Status { get; set; } = "";
    public string ArtifactDirectory { get; set; } = "";
    public string Message { get; set; } = "";
    public bool CleanupComplete { get; set; }
    public bool ActionOutcomeUnknown { get; set; }
}

/// <summary>Bounded UI model decisions; commands and input are never replayed to satisfy these limits.</summary>
public static class AgentLimits
{
    public const int MaximumLocalTurns = 240;
    public const int MaximumNativeTurns = 80;
    public static int Maximum(ProviderSettings settings) => settings.Kind == ProviderKind.OpenAI && settings.NativeComputerUse ? MaximumNativeTurns : MaximumLocalTurns;
}
