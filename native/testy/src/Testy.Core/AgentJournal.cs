namespace Testy.Core;

/// <summary>Crash-visible boundaries; checkpoints authorize inspection only, never replay or automatic continuation.</summary>
internal sealed class AgentJournal(string directory)
{
    private string pending = "";
    public void Write(ComputerAgentResult result, string stage, string? pendingTool = null)
    {
        if (pendingTool is not null) pending = pendingTool;
        if (stage == "observed") pending = "";
        LifecycleJournal.Write(Path.Combine(directory, "computer-agent.json"), result);
        LifecycleJournal.Write(Path.Combine(directory, "agent-checkpoint.json"), new
        {
            schema = "testy.agent-checkpoint.v1", recordedAt = DateTimeOffset.UtcNow, stage, result.Status,
            result.ModelTurns, observations = result.Observations.Count, pendingTool = pending,
            actionOutcomeUnknown = pending.Length > 0 && pending is not ("observe_application" or "project_list_files" or "project_read_file" or "project_search_text") ||
                result.FailureDiagnostics.Any(d => d.ActionOutcome == ActionOutcome.Unknown) ||
                result.Observations.Any(o => o.Project?.Command is { CleanupComplete: false } ||
                    o.Execution?.FailureDiagnostics.Any(d => d.ActionOutcome == ActionOutcome.Unknown) == true ||
                    o.Execution?.Steps.Any(s => s.FailureDiagnostics.Any(d => d.ActionOutcome == ActionOutcome.Unknown)) == true),
            resumePolicy = "Inspect evidence and start a new explicitly requested run. Never resume or replay an in-flight command or UI action from this checkpoint."
        });
    }
}
