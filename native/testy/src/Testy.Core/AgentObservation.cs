namespace Testy.Core;

/// <summary>What any agent, built in or outside, gets to see of a control tree: a copy with password values and unknown property values removed and diagnostics bounded.</summary>
public static class AgentObservation
{
    public static UiSnapshot Sanitize(UiSnapshot snapshot) => PlannerPrompt.Sanitize(snapshot);
}
