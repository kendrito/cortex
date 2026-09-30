namespace Testy.Core;

/// <summary>Deterministic observations and suggested investigation steps. These never change test verdicts.</summary>
public static class FailureDiagnostics
{
    public static FailureDiagnostic Create(FailureCategory category, string observedFact, TestStep? step = null,
        string? expected = null, string? actual = null, string? comparison = null,
        ActionOutcome actionOutcome = ActionOutcome.NotDispatched, DateTimeOffset? observedAt = null) => new()
    {
        Category = category, ObservedFact = observedFact, Selector = step?.Selector ?? "",
        Expected = expected, Actual = actual, Comparison = comparison, ActionOutcome = actionOutcome, ObservedAt = observedAt,
        CauseAssessment = actionOutcome == ActionOutcome.Unknown
            ? "Input may have been partially or fully delivered before execution stopped. Its application outcome and root cause are not established. Do not retry automatically."
            : category switch
            {
                FailureCategory.Cancelled => "Cancellation is not an application defect or a completed validation of the remaining workflow.",
                FailureCategory.EvidenceUnavailable => "An evidence or automation-environment problem prevented verification. This does not establish an application defect.",
                FailureCategory.CapabilityUnavailable => "The target or selected driver does not expose the required capability. This does not establish an application defect.",
                FailureCategory.AssertionMismatch => "The observed value did not meet the saved expectation. Application behavior, test data, timing, and the expectation still need investigation; no root cause is established.",
                FailureCategory.ProviderFailure or FailureCategory.AgentFailure => "The model session stopped. This does not establish a defect in the application under test.",
                FailureCategory.WorkflowNotVerified => "The recorded actions do not prove the required saved workflow. Model commentary cannot replace missing execution evidence.",
                _ => "This category describes the observed failure, not an established application root cause."
            },
        SuggestedNextChecks = Checks(category, actionOutcome)
    };

    internal static FailureDiagnostic FromException(Exception error, TestStep? step = null, ActionOutcome actionOutcome = ActionOutcome.NotDispatched)
    {
        if (error is StepDiagnosticException observed) return observed.Diagnostic;
        var category = error switch
        {
            OperationCanceledException => FailureCategory.Cancelled,
            TimeoutException => FailureCategory.AutomationTimeout,
            HttpRequestException => FailureCategory.ProviderFailure,
            _ => FailureCategory.AutomationError
        };
        return Create(category, error.Message, step, actionOutcome: actionOutcome);
    }

    /// <summary>Refreshes diagnostic context and evidence links without replacing recorded assertion observations.</summary>
    public static void Refresh(RunResult run)
    {
        foreach (var step in run.Steps)
        {
            if (step.Status is RunStatus.Failed or RunStatus.Cancelled && step.FailureDiagnostics.Count == 0)
                step.FailureDiagnostics.Add(Create(step.Status == RunStatus.Cancelled ? FailureCategory.Cancelled : FailureCategory.Unknown, step.Message, step.Step,
                    actionOutcome: TestValidator.IsAssertion(step.Step.Action) || step.Step.Action is StepAction.Wait or StepAction.Screenshot ? ActionOutcome.NotDispatched : ActionOutcome.Unknown));
            foreach (var diagnostic in step.FailureDiagnostics)
            {
                diagnostic.StepIndex = step.Index;
                AddContext(run, diagnostic, step.Index);
                AddEvidence(diagnostic, "post-step screenshot", step.ScreenshotPath);
                if (step.Snapshot is not null)
                    AddEvidence(diagnostic, "post-step snapshot", Path.Combine(run.ArtifactDirectory, $"step-{step.Index + 1:000}.json"));
            }
        }
        var sessionDiagnostics = run.FailureDiagnostics.Where(d => d.StepIndex is null).ToList();
        if (run.Status == RunStatus.Cancelled && !sessionDiagnostics.Concat(run.Steps.SelectMany(s => s.FailureDiagnostics)).Any(d => d.Category == FailureCategory.Cancelled))
            sessionDiagnostics.Add(Create(FailureCategory.Cancelled, "The run was cancelled before the remaining workflow completed."));
        if (run.Status == RunStatus.Failed && sessionDiagnostics.Count == 0 && run.Steps.All(s => s.FailureDiagnostics.Count == 0))
            sessionDiagnostics.Add(Create(FailureCategory.Unknown, run.Summary));
        foreach (var diagnostic in sessionDiagnostics) AddContext(run, diagnostic, run.Steps.Count);
        run.FailureDiagnostics = run.Steps.SelectMany(s => s.FailureDiagnostics).Concat(sessionDiagnostics).DistinctBy(d => d.Id).ToList();
    }

    internal static void AppendSummary(RunResult run)
    {
        if (run.Status is not (RunStatus.Failed or RunStatus.Cancelled) || run.Summary.Contains(" Diagnosis: ", StringComparison.Ordinal)) return;
        var primary = run.Status == RunStatus.Cancelled ? run.FailureDiagnostics.FirstOrDefault(d => d.Category == FailureCategory.Cancelled) : run.FailureDiagnostics.FirstOrDefault();
        if (primary is null) return;
        run.Summary += " Diagnosis: " + Label(primary.Category) + ".";
        if (run.FailureDiagnostics.Any(d => d.ActionOutcome == ActionOutcome.Unknown))
            run.Summary += " Input delivery may be partial; do not retry automatically.";
    }

    public static string Label(FailureCategory category) => category switch
    {
        FailureCategory.AssertionMismatch => "saved expectation not met",
        FailureCategory.SelectorNotFound => "control not found",
        FailureCategory.SelectorAmbiguous => "selector matched multiple controls",
        FailureCategory.ControlNotReady => "control was not ready",
        FailureCategory.TargetUnavailable => "attached application unavailable",
        FailureCategory.AutomationTimeout => "automation deadline exceeded",
        FailureCategory.AutomationError => "automation operation failed",
        FailureCategory.EvidenceUnavailable => "verification evidence unavailable",
        FailureCategory.CapabilityUnavailable => "required UI capability unavailable",
        FailureCategory.ProviderFailure => "model provider request failed",
        FailureCategory.AgentFailure => "model session did not complete",
        FailureCategory.WorkflowNotVerified => "saved workflow not verified",
        FailureCategory.Cancelled => "run cancelled",
        _ => "cause not classified"
    };

    public static string Format(FailureDiagnostic diagnostic)
    {
        var parts = new List<string> { "Classification: " + Label(diagnostic.Category), "Observed fact: " + diagnostic.ObservedFact };
        if (diagnostic.Expected is not null) parts.Add("Expected: " + diagnostic.Expected);
        if (diagnostic.Actual is not null) parts.Add("Observed: " + diagnostic.Actual);
        if (diagnostic.Comparison is not null) parts.Add("Comparison: " + diagnostic.Comparison);
        parts.Add("Input outcome: " + diagnostic.ActionOutcome);
        parts.Add(diagnostic.LastSuccessfulStepIndex is int index ? $"Last successful step: {index + 1}. {diagnostic.LastSuccessfulStepTitle}" : "No earlier successful step was recorded.");
        parts.Add("Cause assessment: " + diagnostic.CauseAssessment);
        parts.AddRange(diagnostic.Evidence.Select(e => "Evidence (" + e.Kind + "): " + e.Path));
        parts.AddRange(diagnostic.SuggestedNextChecks.Select(check => "Suggested next check: " + check));
        return string.Join("\n", parts);
    }

    private static void AddContext(RunResult run, FailureDiagnostic diagnostic, int beforeIndex)
    {
        var last = run.Steps.LastOrDefault(s => s.Index < beforeIndex && s.Status == RunStatus.Passed);
        diagnostic.LastSuccessfulStepIndex = last?.Index;
        diagnostic.LastSuccessfulStepTitle = last?.Step.Title ?? "";
    }
    private static void AddEvidence(FailureDiagnostic diagnostic, string kind, string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path) && !diagnostic.Evidence.Any(e => e.Kind == kind && e.Path == path))
            diagnostic.Evidence.Add(new DiagnosticEvidence { Kind = kind, Path = path });
    }
    private static List<string> Checks(FailureCategory category, ActionOutcome outcome)
    {
        var checks = category switch
        {
            FailureCategory.AssertionMismatch => new List<string> { "Compare the saved expected value with the captured failing observation and test data.", "Inspect application logs or reproduce the same workflow before assigning a product root cause." },
            FailureCategory.SelectorNotFound or FailureCategory.SelectorAmbiguous => ["Inspect the saved UI tree and confirm the intended control and its unique selector.", "Check whether navigation, a modal window, or the target version changed the available controls."],
            FailureCategory.ControlNotReady => ["Check the captured enabled/visible state and required setup steps.", "Check whether the application was still loading before changing a timeout."],
            FailureCategory.TargetUnavailable => ["Check whether the selected process exited or the attachment changed.", "Reattach the intended application before a new run."],
            FailureCategory.AutomationTimeout => ["Inspect the last observation and application responsiveness at the deadline.", "Confirm the state of the application before deciding whether a fresh run is safe."],
            FailureCategory.EvidenceUnavailable => ["Check screenshot/control-tree access and whether the selected process is still available.", "Check artifact directory permissions and disk availability before a new run."],
            FailureCategory.CapabilityUnavailable => ["Inspect the target's advertised UI Automation patterns and property observations.", "Use the cooperative WPF probe when it exposes the required capability; do not replace unavailable observations with false or null."],
            FailureCategory.ProviderFailure or FailureCategory.AgentFailure => ["Inspect the model-session error and configured provider capabilities.", "Review preserved actions before restarting; earlier actions may already have changed the application."],
            FailureCategory.WorkflowNotVerified => ["Compare requested-test.json with recorded actions and deterministic assertions in order.", "Inspect missing or unmatched action evidence without weakening the saved expectations."],
            FailureCategory.Cancelled => ["Review completed evidence and the current application state before starting again."],
            _ => ["Inspect the recorded error and application/automation logs before assigning a root cause."]
        };
        if (outcome == ActionOutcome.Unknown) checks.Insert(0, "Do not automatically repeat the input. Inspect the application for partial or completed side effects first.");
        return checks;
    }
}

public sealed class StepDiagnosticException(FailureDiagnostic diagnostic, UiSnapshot? observation = null, Exception? inner = null, IEnumerable<FailureDiagnostic>? supportingDiagnostics = null)
    : Exception(diagnostic.ObservedFact, inner)
{
    public FailureDiagnostic Diagnostic { get; } = diagnostic;
    public UiSnapshot? Observation { get; } = observation;
    public IReadOnlyList<FailureDiagnostic> SupportingDiagnostics { get; } = supportingDiagnostics?.ToList() ?? [];
}
