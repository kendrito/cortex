using System.Text.RegularExpressions;

namespace Testy.Core;

public static partial class TestValidator
{
    public const int MaximumSteps = 200;
    public const int MaximumTimeoutMs = 60000;
    public static void Validate(TestCase test)
    {
        ArgumentNullException.ThrowIfNull(test);
        ValidateId(test.Id);
        if (string.IsNullOrWhiteSpace(test.Name)) throw new InvalidDataException("Test name is required.");
        if ((test.TargetName?.Length ?? 0) > 200) throw new InvalidDataException("The target app's name may be at most 200 characters.");
        if ((test.TargetAppId?.Length ?? 0) > 400) throw new InvalidDataException("The target app's AppUserModelID may be at most 400 characters.");
        if (test.Steps is null || test.Steps.Count is < 1 or > MaximumSteps)
            throw new InvalidDataException($"A test must contain 1 to {MaximumSteps} steps.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in test.Steps)
        {
            if (step is null) throw new InvalidDataException("A test cannot contain a null step.");
            ValidateStep(step);
            if (!ids.Add(step.Id)) throw new InvalidDataException($"Duplicate step ID '{step.Id}'.");
        }
    }
    public static void ValidateStep(TestStep step)
    {
        ValidateId(step.Id);
        if (!Enum.IsDefined(step.Action)) throw new InvalidDataException($"Unknown action '{step.Action}'.");
        if (string.IsNullOrWhiteSpace(step.Title)) throw new InvalidDataException("Every step needs a title.");
        if (step.TimeoutMs < 100 || step.TimeoutMs > MaximumTimeoutMs)
            throw new InvalidDataException($"Step '{step.Title}' timeout must be 100–{MaximumTimeoutMs} ms.");
        if (RequiresSelector(step.Action) && string.IsNullOrWhiteSpace(step.Selector))
            throw new InvalidDataException($"Step '{step.Title}' requires a selector.");
        if (step.Selector is null || step.Value is null) throw new InvalidDataException("Selector and value cannot be null.");
        if (step.Selector.Length > 2048 || step.Value.Length > 100000)
            throw new InvalidDataException($"Step '{step.Title}' exceeds the selector or text limit.");
        if (step.Selector.Length != 0) UiSelectors.Validate(step.Selector);
        if (step.Action == StepAction.Wait && step.Value.Length != 0 && (!int.TryParse(step.Value, out var delay) || delay is < 0 or > MaximumTimeoutMs))
            throw new InvalidDataException("Wait value must be a number of milliseconds between 0 and 60000.");
        if (step.Action == StepAction.CoordinateClick && (step.X < 0 || step.Y < 0))
            throw new InvalidDataException("CoordinateClick coordinates must be nonnegative and relative to the top-left of the app screenshot.");
        if (step.Action == StepAction.KeyPress)
        {
            if (string.IsNullOrWhiteSpace(step.Value)) throw new InvalidDataException("KeyPress requires a supported key name. " + KeyChords.Allowed);
            if (!KeyChords.TryParse(step.Value, out _, out var problem)) throw new InvalidDataException(problem);
        }
        if (step.Action == StepAction.Toggle && !KeyChords.TryParseToggle(step.Value, out _))
            throw new InvalidDataException($"toggle value '{step.Value}' is not supported. Use {KeyChords.ToggleValues}.");
        AdvancedSteps.Validate(step);
        SelectorRecovery.ValidateAlternatives(step);
    }
    public static bool RequiresSelector(StepAction action) => action is not (StepAction.Wait or StepAction.Screenshot or StepAction.KeyPress or StepAction.CoordinateClick);
    public static bool IsAssertion(StepAction action) => action is StepAction.AssertText or StepAction.AssertExists or StepAction.AssertNotExists or StepAction.AssertEnabled or StepAction.AssertProperty or StepAction.AssertItemExists or StepAction.AssertItemAbsent;
    public static void ValidateId(string id)
    {
        if (id is null || !SafeId().IsMatch(id)) throw new InvalidDataException("IDs must be 1–100 ASCII letters, digits, underscores or hyphens.");
    }
    [GeneratedRegex("\\A[A-Za-z0-9_-]{1,100}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeId();
}
