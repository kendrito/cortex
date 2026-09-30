using System.Text.Json;

namespace Testy.Core;

/// <summary>Verifies that a completed agent actually performed the saved workflow, not just its final assertion.</summary>
public static class SavedWorkflowVerifier
{
    public static WorkflowCoverageResult Verify(TestCase test, IReadOnlyList<ComputerToolObservation> observations)
    {
        var cursor = 0;
        var missingNativeDelivery = false;
        UiSnapshot? before = null;
        var projectStale = false;
        var projectBlocked = false;
        var seen = new HashSet<ComputerToolObservation>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < observations.Count; index++)
        {
            var observation = observations[index];
            if (!seen.Add(observation)) continue;
            if (observation.Project is not null)
            {
                if (observation.Execution is not null || observation.NativeAction is not null || observation.SavedStepId.Length != 0)
                    return new(false, cursor, "Project evidence cannot establish saved UI workflow coverage.");
                if (observation.Project.ToolName == "project_run_command" || observation.Project.Command is not null) projectStale = true;
                projectBlocked |= observation.Project.BlocksFurtherActions;
                continue;
            }
            // The mandatory initial observation is environment setup, not an LLM-selected saved test step.
            if (observation.NativeAction is null && (observation.ToolName == "observe_application" || index == 0 && observation.Execution?.TestName == "Agent observation"))
            { before = observation.Snapshot; if (observation.Execution?.Status == RunStatus.Passed && before is not null && File.Exists(observation.ScreenshotPath)) projectStale = false; continue; }
            var nativeScreenshot = observation.NativeAction is JsonElement nativeObservation && nativeObservation.TryGetProperty("type", out var nativeKind) && nativeKind.GetString() == "screenshot";
            if (projectBlocked || projectStale && !nativeScreenshot)
                return new(false, cursor, "A UI action followed a failed project command or lacked a fresh observation after a project command.");
            if (cursor < test.Steps.Count && observation.Execution?.Status == RunStatus.Passed)
            {
                if (observation.NativeAction is JsonElement native)
                {
                    var expected = test.Steps[cursor];
                    if (expected.Action is StepAction.Click or StepAction.Select or StepAction.Toggle or StepAction.TypeText or StepAction.KeyPress
                        && (before is null || observation.Snapshot is null || !ReceiptValid(observation.NativeReceipt, before, observation.Snapshot)
                            || !string.IsNullOrEmpty(expected.Selector) && !ValidRuntimeId(Unique(before, expected.Selector)?.RuntimeId)))
                        missingNativeDelivery = true;
                    if (MatchesNative(expected, native, before, observation.Snapshot, observation.NativeReceipt)) { cursor++; missingNativeDelivery = false; }
                }
                else
                {
                    foreach (var performed in observation.Execution.Steps.Where(s => s.Status == RunStatus.Passed))
                    {
                        if (cursor < test.Steps.Count && MatchesRecordedLocal(test.Steps[cursor], performed, observation)) { cursor++; missingNativeDelivery = false; }
                    }
                }
            }
            before = observation.Snapshot ?? before;
            if (nativeScreenshot && observation.Execution?.Status == RunStatus.Passed && observation.Snapshot is not null) projectStale = false;
        }
        return cursor == test.Steps.Count
            ? new(true, cursor, $"All {cursor} saved steps were observed in order.")
            : new(false, cursor, $"Saved step {cursor + 1}, '{test.Steps[cursor].Title}', was not verified as performed in order. A passing final assertion cannot replace a missing workflow step."
                + (missingNativeDelivery ? " Native input delivery or control identity could not be verified. The target's UI Automation provider must acknowledge synchronized input and expose runtime identities; use local semantic tools or the WPF probe when that evidence is unavailable." : ""));
    }

    private static bool MatchesRecordedLocal(TestStep expected, StepResult record, ComputerToolObservation observation)
    {
        var actual = record.Step;
        if (observation.ToolName == "perform_saved_step_alternate")
        {
            try { SelectorRecovery.ValidateEvidence(expected, record); }
            catch { return false; }
        }
        else if (record.SelectorRecovery is not null) return false;
        if (observation.ToolName is "perform_saved_step" or "perform_saved_step_alternate" or "perform_saved_control_step" or "verify_saved_assertion")
        {
            if (observation.SavedStepId != expected.Id || actual.Id != expected.Id) return false;
            if (observation.ToolName == "perform_saved_control_step" && !AdvancedSteps.IsMutation(actual.Action)) return false;
            if (observation.ToolName == "verify_saved_assertion" && !TestValidator.IsAssertion(actual.Action)) return false;
        }
        return MatchesLocal(expected, actual);
    }

    private static bool MatchesLocal(TestStep expected, TestStep actual)
    {
        if (expected.Action != actual.Action || !string.Equals(expected.Selector, actual.Selector, StringComparison.Ordinal)) return false;
        if (expected.Action == StepAction.Wait)
            return WaitDuration(expected) is int required && WaitDuration(actual) is int observed && observed >= required;
        return string.Equals(expected.Value, actual.Value, StringComparison.Ordinal)
            && (expected.Action != StepAction.CoordinateClick || expected.X == actual.X && expected.Y == actual.Y);
    }
    private static int? WaitDuration(TestStep step)
    {
        var value = string.IsNullOrEmpty(step.Value) ? step.TimeoutMs : int.TryParse(step.Value, out var milliseconds) ? milliseconds : -1;
        return value is >= 0 and <= TestValidator.MaximumTimeoutMs ? value : null;
    }

    private static bool MatchesNative(TestStep expected, JsonElement action, UiSnapshot? before, UiSnapshot? after, NativeActionReceipt? receipt)
    {
        if (before is null || after is null || before.IsTruncated || after.IsTruncated || action.ValueKind != JsonValueKind.Object
            || !action.TryGetProperty("type", out var kind) || kind.ValueKind != JsonValueKind.String) return false;
        var type = kind.GetString();
        if (expected.Action == StepAction.CoordinateClick)
            return type == "click" && IsLeft(action) && PointInsideScreenshot(action, before) && Coordinate(action, "x") == expected.X && Coordinate(action, "y") == expected.Y;
        if (expected.Action == StepAction.Screenshot) return type == "screenshot";
        if (expected.Action == StepAction.Wait)
        {
            var required = string.IsNullOrEmpty(expected.Value) ? expected.TimeoutMs : int.Parse(expected.Value);
            return type == "wait" && (Coordinate(action, "milliseconds") ?? 1000) >= required;
        }
        if (expected.Action == StepAction.KeyPress)
            return type == "keypress" && Keys(action) == NormalizeChord(expected.Value)
                && ReceiptValid(receipt, before, after)
                && (string.IsNullOrEmpty(expected.Selector) || FocusMatches(before, expected.Selector) && ReceiptProves(Unique(before, expected.Selector), receipt, before, after));
        var original = Unique(before, expected.Selector);
        var current = Unique(after, expected.Selector);
        if (original is null) return false;
        if (expected.Action == StepAction.Click)
            return type == "click" && IsLeft(action) && UnambiguousHit(action, original, before) && ReceiptProves(original, receipt, before, after);
        if (expected.Action == StepAction.TypeText)
        {
            // The native protocol inserts text. Require proof that it targeted the intended field and achieved replacement semantics.
            if (current is null || current.IsPassword || current.IsValueTruncated || current.Value != expected.Value || !FocusMatches(before, expected.Selector) || !ReceiptProves(original, receipt, before, after)) return false;
            if (expected.Value.Length == 0)
                return type == "keypress" && Keys(action) is "DELETE" or "BACKSPACE";
            return type == "type" && action.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String && text.GetString() == expected.Value;
        }
        if (expected.Action == StepAction.Toggle && current is not null)
        {
            var interacted = type == "click" && IsLeft(action) && UnambiguousHit(action, original, before)
                || type == "keypress" && Keys(action) == "SPACE" && FocusMatches(before, expected.Selector);
            return !current.IsValueTruncated && !original.IsValueTruncated && interacted && ReceiptProves(original, receipt, before, after) && (string.IsNullOrEmpty(expected.Value) ? current.Value != original.Value : ToggleValue(current.Value) == ToggleValue(expected.Value) && ToggleValue(expected.Value) is not null);
        }
        if (expected.Action == StepAction.Select && current is not null)
        {
            // Require the value to change and the actual clicked option to carry the requested text.
            // Same-value re-selection cannot be proven from these observations and is intentionally not guessed.
            return !current.IsValueTruncated && !original.IsValueTruncated && type == "click" && IsLeft(action) && current.Value == expected.Value && original.Value != current.Value
                && before.Elements.Any(e => e.Name == expected.Value && DescendantOf(before, original, e) && UnambiguousHit(action, e, before) && ReceiptProves(e, receipt, before, after));
        }
        // Native mouse/keyboard actions never substitute for a deterministic assertion tool call.
        return false;
    }

    private static UiElementInfo? Unique(UiSnapshot snapshot, string selector)
    {
        if (snapshot.IsTruncated) return null;
        var values = UiSelectors.Find(snapshot, selector);
        return values.Count == 1 ? values[0] : null;
    }
    private static bool FocusMatches(UiSnapshot snapshot, string selector)
    {
        if (string.IsNullOrEmpty(snapshot.FocusedSelector)) return false;
        var focused = Unique(snapshot, snapshot.FocusedSelector);
        return focused is not null && focused.IsEnabled && !focused.IsOffscreen && UiSelectors.Matches(snapshot, focused, selector);
    }
    private static bool ReceiptValid(NativeActionReceipt? receipt, UiSnapshot before, UiSnapshot after) => receipt is not null
        && receipt.InputDelivered && receipt.ProcessId == before.Target.ProcessId && receipt.ProcessId == after.Target.ProcessId
        && receipt.CapturedAt >= before.CapturedAt && receipt.CapturedAt <= after.CapturedAt && receipt.HitRuntimeIds is { Count: > 0 }
        && receipt.HitRuntimeIds.All(ValidRuntimeId);
    private static bool ReceiptProves(UiElementInfo? expected, NativeActionReceipt? receipt, UiSnapshot before, UiSnapshot after) => expected is not null
        && ValidRuntimeId(expected.RuntimeId) && ReceiptValid(receipt, before, after) && string.Equals(receipt!.HitRuntimeIds[^1], expected.RuntimeId, StringComparison.Ordinal);
    private static bool ValidRuntimeId(string? value) => !string.IsNullOrWhiteSpace(value) && !value.StartsWith("wpf:", StringComparison.OrdinalIgnoreCase);
    private static bool Hits(JsonElement action, UiElementInfo element, UiSnapshot snapshot)
    {
        var x = Coordinate(action, "x"); var y = Coordinate(action, "y"); var window = snapshot.ScreenshotBounds;
        if (x is null || y is null || window.Width <= 0 || window.Height <= 0 || x < 0 || y < 0 || x >= window.Width || y >= window.Height || element.IsOffscreen || !element.IsEnabled) return false;
        var screenX = window.X + x.Value; var screenY = window.Y + y.Value; var rect = element.Bounds;
        return rect.Width > 0 && rect.Height > 0 && screenX >= rect.X && screenX < rect.X + rect.Width && screenY >= rect.Y && screenY < rect.Y + rect.Height;
    }
    private static bool PointInsideScreenshot(JsonElement action, UiSnapshot snapshot)
    {
        var x = Coordinate(action, "x"); var y = Coordinate(action, "y");
        return x is >= 0 && y is >= 0 && x < snapshot.ScreenshotBounds.Width && y < snapshot.ScreenshotBounds.Height;
    }
    private static bool UnambiguousHit(JsonElement action, UiElementInfo expected, UiSnapshot snapshot)
    {
        if (!Hits(action, expected, snapshot)) return false;
        // Bounds alone do not establish z-order. An unrelated overlapping control or window
        // can intercept the click. Without hit-test identity evidence, reject that ambiguity.
        return !snapshot.Elements.Any(candidate => !ReferenceEquals(candidate, expected)
            && !DescendantOf(snapshot, expected, candidate) && !DescendantOf(snapshot, candidate, expected)
            && Hits(action, candidate, snapshot));
    }
    private static bool DescendantOf(UiSnapshot snapshot, UiElementInfo ancestor, UiElementInfo candidate)
    {
        var ancestorIndex = snapshot.Elements.IndexOf(ancestor);
        var candidateIndex = snapshot.Elements.IndexOf(candidate);
        if (ancestorIndex < 0 || candidateIndex <= ancestorIndex || candidate.Depth <= ancestor.Depth) return false;
        for (var index = ancestorIndex + 1; index <= candidateIndex; index++)
            if (snapshot.Elements[index].Depth <= ancestor.Depth) return false;
        return true;
    }
    private static int? Coordinate(JsonElement action, string name) => action.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    private static bool IsLeft(JsonElement action) => !action.TryGetProperty("button", out var button) || button.ValueKind == JsonValueKind.String && button.GetString() == "left";
    private static string? ToggleValue(string value) => value.ToLowerInvariant() switch { "on" or "true" or "checked" or "1" => "on", "off" or "false" or "unchecked" or "0" => "off", _ => null };
    private static string Keys(JsonElement action) => action.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Array && keys.EnumerateArray().All(k => k.ValueKind == JsonValueKind.String)
        ? NormalizeChord(string.Join('+', keys.EnumerateArray().Select(k => k.GetString()))) : "";
    private static string NormalizeChord(string text) => string.Join('+', text.Split('+', StringSplitOptions.TrimEntries).Select(k => k.ToUpperInvariant() switch { "CONTROL" => "CTRL", "RETURN" => "ENTER", "ESCAPE" => "ESC", var key => key }));
}

public sealed record WorkflowCoverageResult(bool Complete, int VerifiedSteps, string Message);
