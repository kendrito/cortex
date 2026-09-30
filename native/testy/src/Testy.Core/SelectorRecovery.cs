namespace Testy.Core;

public sealed class SelectorAlternative
{
    public string Id { get; set; } = "";
    public string Selector { get; set; } = "";
    public string ExpectedControlType { get; set; } = "";
    public string ExpectedAutomationId { get; set; } = "";
    public string ExpectedName { get; set; } = "";
}

/// <summary>Exact observation identity to revalidate on the target driver's worker immediately before use.</summary>
public sealed class UiExecutionGuard
{
    public int ExpectedProcessId { get; set; }
    public string AbsentSelector { get; set; } = "";
    public string Selector { get; set; } = "";
    public string ExpectedRuntimeId { get; set; } = "";
    public string ExpectedControlType { get; set; } = "";
    public string ExpectedAutomationId { get; set; } = "";
    public string ExpectedName { get; set; } = "";
    public DateTimeOffset ObservedAt { get; set; }
}

public interface IGuardedTargetDriver
{
    /// <summary>Return the same newly captured tree whose guarded identity was validated, without mutation.</summary>
    Task<UiSnapshot> SnapshotGuardedAsync(UiExecutionGuard guard, CancellationToken cancellationToken = default);
    /// <summary>Validate on the normal driver worker, use that resolved live node once, and return its pre-input tree only after successful dispatch.</summary>
    Task<UiSnapshot> ExecuteGuardedAsync(TestStep resolvedStep, UiExecutionGuard guard, CancellationToken cancellationToken = default);
}

public sealed class SelectorRecoveryEvidence
{
    public string AlternativeId { get; set; } = "";
    public string OriginalSelector { get; set; } = "";
    public string ResolvedSelector { get; set; } = "";
    public UiExecutionGuard Guard { get; set; } = new();
    public DateTimeOffset? GuardedAt { get; set; }
    public bool EngineVerified { get; set; }
    public ActionOutcome ActionOutcome { get; set; } = ActionOutcome.NotDispatched;
    public string GuardSnapshotPath { get; set; } = "";
}

public static class SelectorRecovery
{
    /// <summary>Validate a recorded recovery against the immutable saved definition and its persisted guarded observation.</summary>
    public static void ValidateEvidence(TestStep canonical, StepResult result)
    {
        var recovery = result.SelectorRecovery ?? throw new InvalidDataException("Recovered execution has no audited selector evidence.");
        if (!recovery.EngineVerified || recovery.GuardedAt is null || recovery.OriginalSelector != canonical.Selector
            || !File.Exists(recovery.GuardSnapshotPath) || recovery.ActionOutcome != (TestValidator.IsAssertion(canonical.Action) ? ActionOutcome.NotDispatched : ActionOutcome.Completed))
            throw new InvalidDataException("Recovery evidence does not prove a complete guarded operation.");
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<UiSnapshot>(File.ReadAllText(recovery.GuardSnapshotPath), TestyJson.Options)
            ?? throw new InvalidDataException("Recovery observation is missing.");
        var expected = Resolve(canonical, recovery.AlternativeId, snapshot);
        if (recovery.ResolvedSelector != expected.Selector || recovery.Guard.Selector != expected.Selector || recovery.Guard.AbsentSelector != canonical.Selector
            || recovery.Guard.ExpectedRuntimeId != expected.ExpectedRuntimeId || recovery.GuardedAt != snapshot.CapturedAt)
            throw new InvalidDataException("Recovery evidence differs from the approved identity and guarded observation.");
        ValidateGuard(snapshot, recovery.Guard);
    }
    public static void ValidateAlternatives(TestStep step)
    {
        if (step.SelectorAlternatives is null || step.SelectorAlternatives.Count > 4) throw new InvalidDataException("A step supports at most four explicitly authored selector alternatives.");
        if (step.SelectorAlternatives.Count == 0) return;
        if (!TestValidator.RequiresSelector(step.Action) || step.Action is StepAction.AssertNotExists or StepAction.AssertItemExists or StepAction.AssertItemAbsent)
            throw new InvalidDataException("Selector recovery is unavailable for selector-free actions, absence assertions and logical item lookups.");
        var ids = new HashSet<string>(StringComparer.Ordinal); var selectors = new HashSet<string>(StringComparer.Ordinal) { step.Selector };
        foreach (var alternative in step.SelectorAlternatives)
        {
            if (alternative is null) throw new InvalidDataException("Selector alternatives cannot be null.");
            TestValidator.ValidateId(alternative.Id);
            if (!ids.Add(alternative.Id)) throw new InvalidDataException("Selector alternative IDs must be unique within their step.");
            UiSelectors.Validate(alternative.Selector);
            if (!selectors.Add(alternative.Selector)) throw new InvalidDataException("Selector alternatives must differ from the original and one another.");
            if (string.IsNullOrWhiteSpace(alternative.ExpectedControlType) || alternative.ExpectedControlType.Length > 128)
                throw new InvalidDataException("Each selector alternative requires an exact expected control type (at most 128 characters).");
            if (alternative.ExpectedAutomationId is null || alternative.ExpectedName is null || alternative.ExpectedAutomationId.Length > 256 || alternative.ExpectedName.Length > 256
                || string.IsNullOrWhiteSpace(alternative.ExpectedAutomationId) && string.IsNullOrWhiteSpace(alternative.ExpectedName))
                throw new InvalidDataException("Each selector alternative requires an explicit exact automation ID and/or accessible name, at most 256 characters each.");
        }
    }

    public static UiExecutionGuard Resolve(TestStep step, string alternativeId, UiSnapshot snapshot)
    {
        ValidateAlternatives(step);
        var approved = step.SelectorAlternatives.SingleOrDefault(a => a.Id == alternativeId)
            ?? throw new InvalidDataException("The requested selector alternative was not explicitly authored for this saved step.");
        var matches = UiSelectors.Find(snapshot, approved.Selector);
        if (matches.Count != 1) throw new InvalidOperationException("The approved selector alternative must match exactly one currently observed control.");
        var element = matches[0];
        if (element.ControlType != approved.ExpectedControlType
            || approved.ExpectedAutomationId.Length > 0 && element.AutomationId != approved.ExpectedAutomationId
            || approved.ExpectedName.Length > 0 && element.Name != approved.ExpectedName)
            throw new InvalidOperationException("The approved selector alternative does not match its saved exact role and business identity.");
        if (element.IsPassword) throw new InvalidOperationException("Selector recovery is unavailable for password controls.");
        var guard = new UiExecutionGuard
        {
            ExpectedProcessId = snapshot.Target.ProcessId, AbsentSelector = step.Selector, Selector = approved.Selector,
            ExpectedRuntimeId = element.RuntimeId, ExpectedControlType = element.ControlType,
            ExpectedAutomationId = element.AutomationId, ExpectedName = element.Name, ObservedAt = snapshot.CapturedAt
        };
        ValidateGuard(snapshot, guard);
        return guard;
    }

    public static UiElementInfo ValidateGuard(UiSnapshot snapshot, UiExecutionGuard guard)
    {
        if (snapshot.Target.ProcessId <= 0 || snapshot.Target.ProcessId != guard.ExpectedProcessId || snapshot.CapturedAt < guard.ObservedAt || snapshot.IsTruncated)
            throw new InvalidOperationException("Selector recovery requires a fresh complete tree from the same attached process.");
        if (UiSelectors.Find(snapshot, guard.AbsentSelector).Count != 0)
            throw new InvalidOperationException("Selector recovery is permitted only when the original selector is absent; existing, ambiguous or disabled original controls cannot be substituted.");
        if (!UiSelectors.HasCompleteAbsenceCoverage(snapshot, guard.AbsentSelector))
            throw new InvalidOperationException("The original selector's collection scope is incomplete or virtualized; its logical absence cannot be established for recovery.");
        var matches = UiSelectors.Find(snapshot, guard.Selector);
        if (matches.Count != 1) throw new InvalidOperationException("The recovery control is no longer uniquely observed.");
        var element = matches[0];
        if (string.IsNullOrWhiteSpace(guard.ExpectedRuntimeId) || element.RuntimeId != guard.ExpectedRuntimeId || element.ControlType != guard.ExpectedControlType
            || element.AutomationId != guard.ExpectedAutomationId || element.Name != guard.ExpectedName || element.IsPassword)
            throw new InvalidOperationException("The recovery control identity changed before use; no substituted input is authorized.");
        return element;
    }
}
