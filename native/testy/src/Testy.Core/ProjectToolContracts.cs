using System.Text.Json;

namespace Testy.Core;

/// <summary>Explicitly selected project and host-authored command catalog. Disabled by default.</summary>
public sealed class ProjectToolSettings
{
    public bool Enabled { get; set; }
    public string RootDirectory { get; set; } = "";
    public List<ProjectCommandDefinition> Commands { get; set; } = [];
    public List<string> RequiredBeforeUiCommands { get; set; } = [];
    public int MaximumCalls { get; set; } = 20;
    public int MaximumCommandInvocations { get; set; } = 3;
    public int MaximumPlanningTurns { get; set; } = 12;
    public int MaximumOutputCharacters { get; set; } = 16000;
    public int MaximumFiles { get; set; } = 2000;
    public int MaximumFileBytes { get; set; } = 512000;
}

public sealed class ProjectCommandDefinition
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public string Executable { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public string WorkingDirectory { get; set; } = ".";
    public int TimeoutSeconds { get; set; } = 120;
}

public enum ProjectCommandStatus { Completed, Failed, TimedOut, Cancelled, InvalidConfiguration, CleanupFailed }

public sealed class ProjectCommandExecution
{
    public ProjectCommandStatus Status { get; set; }
    public int? ExitCode { get; set; }
    public string Stdout { get; set; } = "";
    public string Stderr { get; set; } = "";
    public bool Truncated { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public int? ProcessId { get; set; }
    public bool CleanupComplete { get; set; }
    public string Message { get; set; } = "";
}

public enum ProjectToolStatus { Succeeded, Denied, Failed, Cancelled, TimedOut, Unavailable }

public sealed class ProjectFileReference
{
    public string Path { get; set; } = "";
    public string Identity { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Bytes { get; set; }
    public int? StartLine { get; set; }
    public int? EndLine { get; set; }
    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Project evidence is distinct from a UI action or an acceptance assertion.</summary>
public sealed class ProjectToolResult
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ToolName { get; set; } = "";
    public ProjectToolStatus Status { get; set; }
    public string Message { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public JsonElement? Request { get; set; }
    public JsonElement? Data { get; set; }
    public List<ProjectFileReference> Files { get; set; } = [];
    public ProjectCommandExecution? Command { get; set; }
    public bool Truncated { get; set; }
    public bool Redacted { get; set; }
    public bool BlocksFurtherActions { get; set; }
    public string EvidencePath { get; set; } = "";
}
