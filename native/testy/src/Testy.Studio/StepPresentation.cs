using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using Testy.Core;

namespace Testy.Studio;

internal enum SentenceKind { Text, Control, Value, Placeholder }
internal readonly record struct SentencePart(string Text, SentenceKind Kind);

/// <summary>A step as people read it: a verb ("Click", "Type", "Check") and a sentence ("Ada Lovelace into Customer name").</summary>
internal sealed class StepSentence
{
    public required string Verb { get; init; }
    public required bool IsCheck { get; init; }
    public required IReadOnlyList<SentencePart> Parts { get; init; }
    /// <summary>"Type “Ada Lovelace” into Customer name": values quoted, for screen readers, tooltips and copied summaries.</summary>
    public string Plain => Verb + (Parts.Count == 0 ? "" : " " + string.Concat(Parts.Select(p => p.Kind == SentenceKind.Value ? $"“{p.Text}”" : p.Text)));
}

internal sealed record ActionChoice(StepAction Action, string Label) { public override string ToString() => Label; }
internal sealed record ControlChoice(string Selector, string Label) { public override string ToString() => Label; }

/// <summary>
/// Plain-language text for steps and run results: step sentences, runner messages ("Found Reset button, clicked it"), screenshot captions
/// and relative times. Selectors, enum names and raw runner messages stay behind Show technical details.
/// </summary>
internal static class StepText
{
    private const int MaxName = 48, MaxValue = 60;

    public static IReadOnlyList<ActionChoice> ActionChoices { get; } =
    [
        new(StepAction.Click, "Click"), new(StepAction.TypeText, "Type text"), new(StepAction.Select, "Select an item"), new(StepAction.Toggle, "Turn on or off"),
        new(StepAction.KeyPress, "Press keys"), new(StepAction.Wait, "Wait"), new(StepAction.Screenshot, "Take a screenshot"),
        new(StepAction.AssertText, "Check the text"), new(StepAction.AssertExists, "Check it's there"), new(StepAction.AssertNotExists, "Check it's gone"),
        new(StepAction.AssertEnabled, "Check it's enabled"), new(StepAction.AssertProperty, "Check a property"), new(StepAction.AssertItemExists, "Check a list has an item"),
        new(StepAction.AssertItemAbsent, "Check a list doesn't have an item"), new(StepAction.Expand, "Expand"), new(StepAction.Collapse, "Collapse"),
        new(StepAction.RealizeItem, "Find an item in a list"), new(StepAction.ScrollIntoView, "Scroll into view"), new(StepAction.ScrollPercent, "Scroll to a position"),
        new(StepAction.CoordinateClick, "Click a point"), new(StepAction.GridEditCell, "Edit a table cell"), new(StepAction.GridCommitRow, "Save a table row"),
        new(StepAction.GridCancelRow, "Cancel a table row edit"),
    ];

    public static bool IsCheck(StepAction action) => TestValidator.IsAssertion(action);

    /// <summary>Label of the value field in the step editor, or null when the action has no value.</summary>
    public static string? ValueLabel(StepAction action) => action switch
    {
        StepAction.TypeText => "Text to type",
        StepAction.Select => "Item to select",
        StepAction.AssertText => "Expected text",
        StepAction.KeyPress => "Keys",
        StepAction.Wait => "Wait (milliseconds)",
        StepAction.AssertProperty or StepAction.AssertItemExists or StepAction.AssertItemAbsent or StepAction.RealizeItem or StepAction.ScrollPercent
            or StepAction.GridEditCell or StepAction.GridCommitRow or StepAction.GridCancelRow => "Details (JSON)",
        _ => null
    };

    public static StepSentence Sentence(TestStep step, UiSnapshot? snapshot)
    {
        var name = ControlName(step.Selector, snapshot, out var element);
        var target = name.Length > 0 ? new SentencePart(name, SentenceKind.Control) : new SentencePart("choose a control", SentenceKind.Placeholder);
        var secret = element?.IsPassword == true || LooksSecret(step.Selector);
        var value = secret && step.Value.Length > 0 ? "••••••" : Clip(OneLine(step.Value), MaxValue);
        static SentencePart T(string text) => new(text, SentenceKind.Text);
        static SentencePart V(string text) => new(text, SentenceKind.Value);
        static SentencePart P(string text) => new(text, SentenceKind.Placeholder);
        (string Verb, List<SentencePart> Parts) sentence = step.Action switch
        {
            StepAction.Click => ("Click", [target]),
            StepAction.TypeText => value.Length == 0 ? ("Clear", [target]) : ("Type", [V(value), T(" into "), target]),
            StepAction.Select => ("Select", value.Length > 0 ? [V(value), T(" in "), target] : [target]),
            StepAction.Toggle => ("Toggle", [target]),
            StepAction.AssertText => ("Check", step.Value.StartsWith("contains:", StringComparison.Ordinal)
                ? [target, T(" contains "), V(Clip(OneLine(step.Value[9..]), MaxValue))]
                : value.Length > 0 ? [target, T(" says "), V(value)] : [target, T(" says "), P("the expected text")]),
            StepAction.AssertExists => ("Check", [target, T(" is there")]),
            StepAction.AssertNotExists => ("Check", [target, T(" is gone")]),
            StepAction.AssertEnabled => ("Check", [target, T(" is enabled")]),
            StepAction.AssertProperty => ("Check", Property(step.Value) is { } property ? [target, T($" {property.Property} is "), V(property.Expected)] : [target, T(" has "), V(value)]),
            StepAction.AssertItemExists => ("Check", [target, T(" has "), V(Item(step.Value) ?? value)]),
            StepAction.AssertItemAbsent => ("Check", [target, T(" doesn't have "), V(Item(step.Value) ?? value)]),
            StepAction.Wait => ("Wait", [T(WaitText(step))]),
            StepAction.Screenshot => ("Capture", [T("a screenshot")]),
            StepAction.KeyPress => ("Press", value.Length > 0 ? [V(value)] : [P("choose keys")]),
            StepAction.CoordinateClick => ("Click", [T(string.Format(CultureInfo.CurrentCulture, "at {0}, {1}", step.X, step.Y))]),
            StepAction.Expand => ("Expand", [target]),
            StepAction.Collapse => ("Collapse", [target]),
            StepAction.RealizeItem => ("Find", [V(Item(step.Value) ?? value), T(" in "), target]),
            StepAction.ScrollIntoView => ("Scroll", [target, T(" into view")]),
            StepAction.ScrollPercent => ("Scroll", [target, T(" to "), V(Percent(step.Value) ?? value)]),
            StepAction.GridEditCell => ("Edit", Grid(step.Value) is { } cell ? [T("cell "), V(cell.Column), T(" of row "), V(cell.Row), T(" in "), target, T(" to "), V(cell.Text)] : [T("a cell in "), target]),
            StepAction.GridCommitRow => ("Save", Grid(step.Value) is { } commit ? [T("row "), V(commit.Row), T(" in "), target] : [T("the row in "), target]),
            StepAction.GridCancelRow => ("Cancel", Grid(step.Value) is { } cancel ? [T("edits to row "), V(cancel.Row), T(" in "), target] : [T("the row edit in "), target]),
            _ => ("Do", [target])
        };
        return new StepSentence { Verb = sentence.Verb, IsCheck = IsCheck(step.Action), Parts = sentence.Parts };
    }

    /// <summary>The control's friendly name: its accessible name from the snapshot (text elements use their automation ID), else a readable form of the selector.</summary>
    public static string ControlName(string? selector, UiSnapshot? snapshot, out UiElementInfo? element)
    {
        element = string.IsNullOrWhiteSpace(selector) ? null : StepTargetLocator.FindElement(snapshot, selector);
        if (element is not null)
        {
            var textLike = element.ControlType is "Text" or "Document";
            var name = OneLine(element.Name);
            if (!textLike && name.Length is > 0 and <= MaxName) return name;
            if (!string.IsNullOrWhiteSpace(element.AutomationId)) return StepDescriber.Humanize(element.AutomationId);
            if (name.Length > 0) return Clip(name, MaxName);
        }
        if (string.IsNullOrWhiteSpace(selector)) return "";
        selector = selector.Trim();
        if (selector.StartsWith("id:", StringComparison.Ordinal)) return StepDescriber.Humanize(selector[3..]);
        if (selector.StartsWith("name:", StringComparison.Ordinal)) return Clip(OneLine(selector[5..]), MaxName);
        if (selector.StartsWith("path:", StringComparison.Ordinal)) return "a control found by its position";
        if (selector.StartsWith("query:", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(selector[6..]);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String) return Clip(OneLine(label.GetString()), MaxName);
                    if (doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) return StepDescriber.Humanize(id.GetString() ?? "");
                }
            }
            catch (JsonException) { }
            return "a control found by a query";
        }
        return Clip(OneLine(selector), MaxName);
    }

    /// <summary>"button", "field", "text"… for runner messages ("Found Add customer button").</summary>
    public static string KindWord(UiElementInfo? element, StepAction action) => element?.ControlType switch
    {
        "Button" or "SplitButton" => "button",
        "Edit" => "field",
        "Document" => "document",
        "Text" => "text",
        "CheckBox" => "check box",
        "RadioButton" => "option",
        "ComboBox" => "list",
        "List" => "list",
        "ListItem" or "TreeItem" => "item",
        "Tree" => "tree",
        "Tab" => "tabs",
        "TabItem" => "tab",
        "MenuItem" => "menu item",
        "Menu" or "MenuBar" => "menu",
        "Hyperlink" => "link",
        "DataGrid" or "Table" => "table",
        "DataItem" => "row",
        "Slider" => "slider",
        "Spinner" => "spin box",
        "Window" => "window",
        "Image" => "image",
        "ToolBar" => "toolbar",
        _ => action switch { StepAction.TypeText => "field", StepAction.AssertText => "text", StepAction.Toggle => "switch", _ => "" }
    };

    /// <summary>One line composed from the step and its result: "Found Reset button, clicked it", "Read Status message text, it matches",
    /// or, when it failed, "Expected “X”, found “Y”". The original runner message stays behind technical details.</summary>
    public static string RunnerMessage(StepResult result)
    {
        var step = result.Step ?? new TestStep();
        var name = ControlName(StepTargetLocator.TargetSelector(result), result.Snapshot, out var element);
        var label = name.Length > 0 ? name : "the control";
        var kind = KindWord(element, step.Action);
        var what = kind.Length > 0 ? $"{label} {kind}" : label;
        var value = OneLine(step.Value);
        switch (result.Status)
        {
            case RunStatus.Running: return "running…";
            case RunStatus.Pending: return "Waiting to run";
            case RunStatus.Skipped: return "Didn't run: the run stopped before this step";
            case RunStatus.Cancelled: return "Stopped before this step finished";
            case RunStatus.Failed: return FailureMessage(result, what, label);
        }
        return step.Action switch
        {
            StepAction.Click => $"Found {what}, clicked it",
            StepAction.TypeText => value.Length == 0 ? $"Found {what}, cleared it" : $"Found {what}, typed {Count(new StringInfo(step.Value).LengthInTextElements, "character")}",
            StepAction.Select => $"Found {what}, selected “{Clip(value, 40)}”",
            StepAction.Toggle => $"Found {what}, toggled it",
            StepAction.AssertText => $"Read {what}, it matches",
            StepAction.AssertExists => $"Found {what}",
            StepAction.AssertNotExists => $"Looked for {label}, it isn't there",
            StepAction.AssertEnabled => $"Found {what}, it's enabled",
            StepAction.AssertProperty => $"Read {what}, the property matches",
            StepAction.AssertItemExists => $"Found “{Item(step.Value) ?? value}” in {what}",
            StepAction.AssertItemAbsent => $"Checked {what}, “{Item(step.Value) ?? value}” isn't there",
            StepAction.Wait => $"Waited {WaitText(step)[4..]}",
            StepAction.Screenshot => "Took a screenshot",
            StepAction.KeyPress => $"Pressed {Clip(value, 40)}",
            StepAction.CoordinateClick => string.Format(CultureInfo.CurrentCulture, "Clicked at {0}, {1}", step.X, step.Y),
            StepAction.Expand => $"Found {what}, expanded it",
            StepAction.Collapse => $"Found {what}, collapsed it",
            StepAction.RealizeItem => $"Found “{Item(step.Value) ?? value}” in {what}",
            StepAction.ScrollIntoView => $"Found {what}, scrolled it into view",
            StepAction.ScrollPercent => $"Found {what}, scrolled it",
            StepAction.GridEditCell => $"Found {what}, edited the cell",
            StepAction.GridCommitRow => $"Found {what}, saved the row",
            StepAction.GridCancelRow => $"Found {what}, cancelled the row edit",
            _ => "Done"
        };
    }

    private static string FailureMessage(StepResult result, string what, string label)
    {
        var step = result.Step ?? new TestStep();
        // The observed mismatch (expected and actual values) explains a failed check best, even when the step then hit its deadline.
        var diagnostic = result.FailureDiagnostics.FirstOrDefault(d => d.Category == FailureCategory.AssertionMismatch && d.Expected is not null && d.Actual is not null)
            ?? result.FailureDiagnostics.FirstOrDefault(d => d.Category is not (FailureCategory.EvidenceUnavailable or FailureCategory.Unknown))
            ?? result.FailureDiagnostics.FirstOrDefault();
        var timeout = RunTrace.FormatDuration(step.TimeoutMs);
        switch (diagnostic?.Category)
        {
            case FailureCategory.AssertionMismatch:
                if (step.Action == StepAction.AssertEnabled) return $"Found {what}, but it's disabled";
                if (step.Action == StepAction.AssertNotExists) return $"{Capitalize(label)} is still there";
                if (step.Action == StepAction.AssertItemExists) return $"“{Item(step.Value) ?? OneLine(step.Value)}” isn't in {what}";
                if (step.Action == StepAction.AssertItemAbsent) return $"“{Item(step.Value) ?? OneLine(step.Value)}” is still in {what}";
                if (diagnostic.Expected is not null && diagnostic.Actual is not null)
                    return $"Expected “{Clip(OneLine(diagnostic.Expected), 48)}”, found “{Clip(OneLine(diagnostic.Actual), 48)}”";
                break;
            case FailureCategory.SelectorNotFound: return $"Couldn't find {what} within {timeout}";
            case FailureCategory.SelectorAmbiguous: return $"More than one control matches {label}";
            case FailureCategory.ControlNotReady:
                return diagnostic.Comparison == "control offscreen" ? $"Found {what}, but it was off screen" : $"Found {what}, but it was disabled";
            case FailureCategory.TargetUnavailable: return "The app closed or changed during the run";
            case FailureCategory.AutomationTimeout: return $"Timed out after {timeout}";
            case FailureCategory.CapabilityUnavailable: return $"{Capitalize(what)} doesn't support this action";
            case FailureCategory.EvidenceUnavailable: return "Couldn't capture the screenshot or the controls";
            case FailureCategory.ProviderFailure: return "The AI service didn't respond";
            case FailureCategory.AgentFailure: return "The AI stopped before finishing";
            case FailureCategory.WorkflowNotVerified: return "The AI didn't show every saved step";
            case FailureCategory.Cancelled: return "Stopped before this step finished";
        }
        var first = FirstSentence(result.Message);
        return first.Length > 0 ? first : "Didn't pass";
    }

    /// <summary>"Screenshot after the click", "Screenshot when the check failed".</summary>
    public static string ScreenshotPhrase(StepResult result)
    {
        var action = result.Step?.Action ?? StepAction.Screenshot;
        return result.Status switch
        {
            RunStatus.Running => "Waiting for this step to finish",
            RunStatus.Skipped or RunStatus.Pending => "This step didn't run",
            RunStatus.Failed => IsCheck(action) ? "Screenshot when the check failed" : "Screenshot when the step failed",
            RunStatus.Cancelled => "Screenshot when the run stopped",
            _ => action switch
            {
                StepAction.Click or StepAction.CoordinateClick => "Screenshot after the click",
                StepAction.TypeText => "Screenshot after typing",
                StepAction.Select => "Screenshot after selecting",
                StepAction.Toggle => "Screenshot after toggling",
                StepAction.KeyPress => "Screenshot after the key press",
                StepAction.Wait => "Screenshot after waiting",
                StepAction.Screenshot => "Screenshot",
                _ when IsCheck(action) => "Screenshot after the check",
                _ => "Screenshot after the step"
            }
        };
    }

    /// <summary>Caption under an outlined screenshot: "Outline marks the control Testy clicked".</summary>
    public static string OutlineNote(StepAction action) => action switch
    {
        StepAction.Click => "Outline marks the control Testy clicked",
        StepAction.CoordinateClick => "The marker shows where Testy clicked",
        StepAction.TypeText => "Outline marks the control Testy typed into",
        StepAction.Select => "Outline marks the control Testy selected in",
        StepAction.Toggle => "Outline marks the control Testy toggled",
        _ when IsCheck(action) => "Outline marks the control Testy checked",
        _ => "Outline marks the control Testy used"
    };

    /// <summary>The word on the outline's label: "Clicked", "Typed", "Checked".</summary>
    public static string OutlineTag(StepAction action) => action switch
    {
        StepAction.Click or StepAction.CoordinateClick => "Clicked",
        StepAction.TypeText => "Typed",
        StepAction.Select => "Selected",
        StepAction.Toggle => "Toggled",
        _ when IsCheck(action) => "Checked",
        _ => "Used"
    };

    /// <summary>"just now", "2 min ago", "3 h ago", "yesterday", "4 days ago", "on 12 Sep". Local time, never UTC.</summary>
    public static string Relative(DateTimeOffset when, DateTimeOffset now)
    {
        var span = now - when;
        if (span < TimeSpan.FromSeconds(45)) return "just now";
        if (span < TimeSpan.FromMinutes(59.5)) return Math.Max(1, (int)Math.Round(span.TotalMinutes)).ToString(CultureInfo.CurrentCulture) + " min ago";
        var local = when.ToLocalTime().Date; var today = now.ToLocalTime().Date;
        if (local == today) return Math.Max(1, (int)span.TotalHours).ToString(CultureInfo.CurrentCulture) + " h ago";
        if (local == today.AddDays(-1)) return "yesterday";
        if ((today - local).Days < 7) return (today - local).Days.ToString(CultureInfo.CurrentCulture) + " days ago";
        return "on " + when.ToLocalTime().ToString(local.Year == today.Year ? "d MMM" : "d MMM yyyy", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// A run's outcome as one sentence, composed from its steps and times (Results' Summary column): "5 of 5 steps passed in 1.3 s",
    /// "Failed at step 3 of 5", "Stopped at step 2 of 5". The runner's own summary stays behind Show technical details.
    /// </summary>
    public static string RunOutcome(RunResult run)
    {
        var steps = run.Steps ?? [];
        var total = steps.Count;
        var passed = steps.Count(s => s?.Status == RunStatus.Passed);
        var of = string.Format(CultureInfo.CurrentCulture, "of {0} {1}", total, total == 1 ? "step" : "steps");
        var failed = steps.FindIndex(s => s?.Status == RunStatus.Failed);
        var stopped = steps.FindIndex(s => s?.Status == RunStatus.Cancelled);
        string At(int index) => string.Format(CultureInfo.CurrentCulture, "step {0} {1}", index + 1, of);
        return run.Status switch
        {
            RunStatus.Passed when total == 0 => "Passed, no steps to run",
            RunStatus.Passed => string.Format(CultureInfo.CurrentCulture, "{0} {1} passed in {2}", passed, of, RunTrace.From(run).TotalTimeText),
            RunStatus.Failed when failed >= 0 => "Failed at " + At(failed),
            RunStatus.Failed when total == 0 => "Failed before the first step",
            RunStatus.Failed => string.Format(CultureInfo.CurrentCulture, "Failed after {0} {1} passed", passed, of),
            RunStatus.Cancelled when stopped >= 0 => "Stopped at " + At(stopped),
            RunStatus.Cancelled when total == 0 => "Stopped before the first step",
            RunStatus.Cancelled => string.Format(CultureInfo.CurrentCulture, "Stopped after {0} {1}", passed, of),
            RunStatus.Running or RunStatus.Pending => "Didn't finish",
            _ => string.Format(CultureInfo.CurrentCulture, "{0} {1} passed", passed, of)
        };
    }

    public static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : string.Format(CultureInfo.CurrentCulture, "{0} {1}s", count, noun);
    public static string StatusWord(RunStatus status) => status switch
    {
        RunStatus.Passed => "Passed", RunStatus.Failed => "Failed", RunStatus.Cancelled => "Stopped", RunStatus.Running => "Running",
        RunStatus.Skipped => "Not run", _ => "Waiting"
    };

    private static string WaitText(TestStep step)
    {
        var ms = step.Value.Length > 0 && int.TryParse(step.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : step.TimeoutMs;
        return "for " + RunTrace.FormatDuration(ms);
    }
    private static (string Property, string Expected)? Property(string value)
    {
        try
        {
            using var doc = JsonDocument.Parse(value);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("property", out var property) && doc.RootElement.TryGetProperty("equals", out var equals))
            {
                var name = property.GetString() ?? "";
                var dot = name.IndexOf('.');
                return (StepDescriber.Humanize(dot >= 0 ? name[(dot + 1)..] : name).ToLowerInvariant(), equals.ValueKind == JsonValueKind.String ? equals.GetString() ?? "" : equals.GetRawText());
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        return null;
    }
    private static string? Item(string value)
    {
        try
        {
            using var doc = JsonDocument.Parse(value);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
                foreach (var field in item.EnumerateObject()) if (field.Value.ValueKind == JsonValueKind.String) return Clip(OneLine(field.Value.GetString()), MaxValue);
        }
        catch (JsonException) { }
        return null;
    }
    private static string? Percent(string value)
    {
        try
        {
            using var doc = JsonDocument.Parse(value);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var parts = new List<string>();
            if (doc.RootElement.TryGetProperty("vertical", out var v) && v.TryGetDouble(out var vertical)) parts.Add(string.Format(CultureInfo.CurrentCulture, "{0:0.#}% down", vertical));
            if (doc.RootElement.TryGetProperty("horizontal", out var h) && h.TryGetDouble(out var horizontal)) parts.Add(string.Format(CultureInfo.CurrentCulture, "{0:0.#}% across", horizontal));
            return parts.Count > 0 ? string.Join(", ", parts) : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
    }
    private static (string Row, string Column, string Text)? Grid(string value)
    {
        try
        {
            using var doc = JsonDocument.Parse(value);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("rowKey", out var row)) return null;
            var column = doc.RootElement.TryGetProperty("columnKey", out var c) ? c.GetString() ?? "" : "";
            var text = doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            return (Clip(OneLine(row.GetString()), 40), Clip(OneLine(column), 40), Clip(OneLine(text), 40));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
    }

    public static string OneLine(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "" : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    public static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
    private static string FirstSentence(string? text)
    {
        var line = OneLine(text);
        var end = line.IndexOf(". ", StringComparison.Ordinal);
        return Clip(end > 0 ? line[..end] : line.TrimEnd('.'), 120);
    }
    private static bool LooksSecret(string? text) =>
        !string.IsNullOrEmpty(text) && (text.Contains("password", StringComparison.OrdinalIgnoreCase) || text.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || text.Contains("token", StringComparison.OrdinalIgnoreCase) || text.Contains("apikey", StringComparison.OrdinalIgnoreCase));

    /// <summary>Controls of a snapshot that make sense as step targets, for the step editor's control picker.</summary>
    public static List<ControlChoice> ControlChoices(UiSnapshot? snapshot, string current)
    {
        var choices = new List<ControlChoice>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (snapshot?.Elements is { } elements)
        {
            foreach (var element in elements)
            {
                if (choices.Count >= 200) break;
                if (element is null || string.IsNullOrWhiteSpace(element.Selector) || element.Selector.StartsWith("path:", StringComparison.Ordinal)) continue;
                if (element.ControlType is "Pane" or "ScrollBar" or "Thumb" or "TitleBar" or "Separator" or "Custom" or "Group" or "Header" or "HeaderItem" || element.Depth == 0) continue;
                if (element.ControlType == "Text" && string.IsNullOrWhiteSpace(element.AutomationId)) continue;
                if (!seen.Add(element.Selector)) continue;
                var name = ControlName(element.Selector, snapshot, out _);
                var kind = KindWord(element, StepAction.Click);
                choices.Add(new ControlChoice(element.Selector, kind.Length > 0 ? $"{name} ({kind})" : name));
            }
        }
        if (!string.IsNullOrWhiteSpace(current) && seen.Add(current)) choices.Insert(0, new ControlChoice(current, ControlName(current, snapshot, out _)));
        return choices;
    }
}

/// <summary>One row of the Steps editor (StepsEditor DataGrid, DataItem rows). Raw columns shown with technical details edit the step directly.</summary>
internal sealed class StepRow : INotifyPropertyChanged
{
    private StepSentence sentence;
    private UiSnapshot? snapshot;
    private int number;

    public StepRow(TestStep step, int number, UiSnapshot? snapshot)
    {
        Step = step; this.number = number; this.snapshot = snapshot;
        sentence = StepText.Sentence(step, snapshot);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public TestStep Step { get; private set; }
    public int Number { get => number; set { if (number == value) return; number = value; Raise(nameof(Number)); } }
    public string Verb => sentence.Verb;
    public bool IsCheck => sentence.IsCheck;
    public IReadOnlyList<SentencePart> Parts => sentence.Parts;
    public string Plain => sentence.Plain;
    public string ToolTipText => string.IsNullOrWhiteSpace(Step.Title) || Step.Title == "New step" || Step.Title == Plain ? Plain : Plain + Environment.NewLine + Step.Title;

    public StepAction Action { get => Step.Action; set { if (Step.Action == value) return; Edit(() => Step.Action = value); } }
    public string Selector { get => Step.Selector; set { if (Step.Selector == (value ?? "")) return; Edit(() => Step.Selector = value ?? ""); } }
    public string Value { get => Step.Value; set { if (Step.Value == (value ?? "")) return; Edit(() => Step.Value = value ?? ""); } }

    /// <summary>Replaces the step (Step options returns an edited clone) and recomputes the sentence.</summary>
    public void Replace(TestStep step) { Step = step; Refresh(snapshot); }
    /// <summary>Recomputes the sentence, e.g. after a new scan of the app gives controls their friendly names.</summary>
    public void Refresh(UiSnapshot? names)
    {
        snapshot = names; sentence = StepText.Sentence(Step, snapshot);
        Raise("");
    }
    /// <summary>Auto titles follow the sentence while the title is still "New step" or the previous sentence, so reports stay readable.</summary>
    private void Edit(Action change)
    {
        var previous = sentence.Plain;
        change();
        sentence = StepText.Sentence(Step, snapshot);
        if (string.IsNullOrWhiteSpace(Step.Title) || Step.Title == "New step" || Step.Title == previous) Step.Title = sentence.Plain;
        Raise("");
    }
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A test in the library list. Its UI Automation name is the test name (LibraryItem style).</summary>
internal sealed class LibraryRow(TestCase test, RunHeader? last, DateTimeOffset now)
{
    public TestCase Test { get; } = test;
    public string Name => Test.Name;
    public string Category => string.IsNullOrWhiteSpace(Test.Category) ? "Tests" : Test.Category;
    public string StepCount => StepText.Count(Test.Steps.Count, "step");
    public string StatusKind { get; } = last?.Status switch
    {
        RunStatus.Passed => "Passed", RunStatus.Failed => "Failed", RunStatus.Cancelled => "Cancelled", RunStatus.Running => "Running", _ => "NotRun"
    };
    public string StatusText { get; } = last is null ? "Not run yet" : StepText.StatusWord(last.Status) + " " + StepText.Relative(last.When, now);
    public string ToolTipText => last is null ? Name : $"{Name}{Environment.NewLine}Last run: {FriendlyTimeConverter.Format(last.When, full: true)}";
}

/// <summary>A step of the displayed run (Last run step list). Updated in place on every progress report of a live run.</summary>
internal sealed class RunStepRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public int Index { get; private set; }
    public int Number => Index + 1;
    public string Status { get; private set; } = "Pending";
    public string Verb { get; private set; } = "";
    public bool IsCheck { get; private set; }
    public IReadOnlyList<SentencePart> Parts { get; private set; } = [];
    public string Plain { get; private set; } = "";
    public string Duration { get; private set; } = "";
    public string Message { get; private set; } = "";
    public string Technical { get; private set; } = "";
    public bool IsProblem => Status is "Failed" or "Cancelled";
    public string AutomationName => $"Step {Number}, {Plain}, {StepText.StatusWord(Enum.TryParse<RunStatus>(Status, out var s) ? s : RunStatus.Pending).ToLowerInvariant()}" + (Duration.Length > 0 ? $", {Duration}" : "");

    public RunStepRow(StepResult result, int position, UiSnapshot? names = null) => Update(result, position, names);

    /// <summary>
    /// Refreshes the row from the step's latest result. <paramref name="position"/> is its index in RunResult.Steps (as on the run trace);
    /// <paramref name="names"/> names the controls when the step has no snapshot of its own (it is running, or was skipped).
    /// </summary>
    public void Update(StepResult result, int position, UiSnapshot? names = null)
    {
        var sentence = StepText.Sentence(result.Step ?? new TestStep(), result.Snapshot ?? names);
        Index = position; Status = result.Status.ToString();
        Verb = sentence.Verb; IsCheck = sentence.IsCheck; Parts = sentence.Parts; Plain = sentence.Plain;
        Duration = result.Status == RunStatus.Running ? "…" : result.Status is RunStatus.Skipped or RunStatus.Pending ? "" : RunTrace.FormatDuration(result.DurationMs);
        Message = StepText.RunnerMessage(result);
        Technical = StepText.OneLine(result.Message);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    }
}

/// <summary>The fields of a stored run the Studio needs for lists (status, times), read without deserializing steps and snapshots.</summary>
internal sealed class RunHeader
{
    public string Id { get; set; } = "";
    public string TestId { get; set; } = "";
    public string TestName { get; set; } = "";
    public RunStatus Status { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset When => FinishedAt ?? StartedAt;

    public static RunHeader From(RunResult run) => new() { Id = run.Id, TestId = run.TestId, TestName = run.TestName, Status = run.Status, StartedAt = run.StartedAt, FinishedAt = run.FinishedAt };

    /// <summary>Reads every run in the workspace's runs folder; unreadable files are skipped (Results reports them).</summary>
    public static List<RunHeader> LoadAll(string runsDirectory)
    {
        var headers = new List<RunHeader>();
        if (!Directory.Exists(runsDirectory)) return headers;
        foreach (var path in Directory.EnumerateFiles(runsDirectory, "*.json"))
        {
            try { if (JsonSerializer.Deserialize<RunHeader>(File.ReadAllBytes(path), TestyJson.Options) is { Id.Length: > 0 } header) headers.Add(header); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return headers;
    }
}

/// <summary>Renders <see cref="SentencePart"/>s: values in the mono font, placeholders in tertiary text.</summary>
internal sealed class StepSentenceBlock : TextBlock
{
    public static readonly DependencyProperty PartsProperty = DependencyProperty.Register(nameof(Parts), typeof(IReadOnlyList<SentencePart>), typeof(StepSentenceBlock),
        new PropertyMetadata(null, (d, _) => ((StepSentenceBlock)d).Rebuild()));
    public IReadOnlyList<SentencePart>? Parts { get => (IReadOnlyList<SentencePart>?)GetValue(PartsProperty); set => SetValue(PartsProperty, value); }

    private void Rebuild()
    {
        Inlines.Clear();
        foreach (var part in Parts ?? [])
        {
            var run = new Run(part.Text);
            if (part.Kind == SentenceKind.Value)
            {
                if (TryFindResource("MonoFont") is FontFamily mono) run.FontFamily = mono;
                run.FontSize = Math.Max(11, FontSize - 1);
            }
            else if (part.Kind == SentenceKind.Placeholder) run.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorTertiaryBrush");
            Inlines.Add(run);
        }
    }
}

/// <summary>Results' Summary column: <see cref="StepText.RunOutcome"/> of the row's run. The runner's own summary is a technical column.</summary>
public sealed class RunOutcomeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is RunResult run ? StepText.RunOutcome(run) : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"1 test" / "4 tests" (ConverterParameter is the singular noun).</summary>
public sealed class PluralConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count ? StepText.Count(count, parameter as string ?? "item") : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
