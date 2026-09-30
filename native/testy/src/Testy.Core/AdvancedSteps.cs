using System.Text.Json;

namespace Testy.Core;

public sealed record ItemQuery(string? Id, string? Label);
public sealed record ScrollPercentValue(double? Vertical, double? Horizontal);
public sealed record PropertyAssertion(string Property, JsonElement Expected);
public sealed record GridCellEditValue(string RowKey, string ColumnKey, string Text);
public sealed record GridRowValue(string RowKey);

/// <summary>Strict, provider-independent value contracts for advanced UI actions.</summary>
public static class AdvancedSteps
{
    public static IReadOnlySet<string> PropertyNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "uia.value", "uia.expandCollapseState", "uia.isSelected", "uia.isReadOnly", "uia.horizontalScrollPercent", "uia.verticalScrollPercent",
        "uia.row", "uia.column", "uia.rowCount", "uia.columnCount", "wpf.validationHasError", "wpf.hasItems", "wpf.isVirtualizing",
        "wpf.enableRowVirtualization", "wpf.enableColumnVirtualization", "wpf.gridIsEditing", "wpf.gridEditingRowKey", "wpf.bindingStatus", "wpf.commandCanExecute"
    };
    public static bool IsMutation(StepAction action) => action is StepAction.Expand or StepAction.Collapse or StepAction.RealizeItem or StepAction.ScrollIntoView or StepAction.ScrollPercent or StepAction.GridEditCell or StepAction.GridCommitRow or StepAction.GridCancelRow;
    public static string Capability(StepAction action) => action switch
    {
        StepAction.Expand or StepAction.Collapse => "ExpandCollapse",
        StepAction.RealizeItem => "ItemContainer",
        StepAction.ScrollIntoView => "ScrollItem",
        StepAction.ScrollPercent => "Scroll",
        StepAction.GridEditCell => "GridEdit",
        StepAction.GridCommitRow => "GridCommit",
        StepAction.GridCancelRow => "GridCancel",
        _ => ""
    };
    public static void Validate(TestStep step)
    {
        switch (step.Action)
        {
            case StepAction.Expand: case StepAction.Collapse: case StepAction.ScrollIntoView:
                if (step.Value.Length != 0) throw new InvalidDataException($"{step.Action} requires an empty value.");
                break;
            case StepAction.RealizeItem: case StepAction.AssertItemExists: case StepAction.AssertItemAbsent: ParseItem(step.Value); break;
            case StepAction.ScrollPercent: ParseScrollPercent(step.Value); break;
            case StepAction.AssertProperty: ParseProperty(step.Value); break;
            case StepAction.GridEditCell: ParseGridEdit(step.Value); break;
            case StepAction.GridCommitRow: case StepAction.GridCancelRow: ParseGridRow(step.Value); break;
        }
    }
    public static GridCellEditValue ParseGridEdit(string value)
    {
        // The text contract is decoded characters; JSON escaping may use six bytes per character.
        using var document = Parse(value, 32768);
        var root = Object(document.RootElement, "rowKey", "columnKey", "text");
        var row = GridKey(root, "rowKey"); var column = GridKey(root, "columnKey");
        if (!root.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String || text.GetString()!.Length > 4096)
            throw new InvalidDataException("GridEditCell text must be a string with at most 4096 characters.");
        return new(row, column, text.GetString()!);
    }
    public static GridRowValue ParseGridRow(string value)
    {
        using var document = Parse(value);
        return new(GridKey(Object(document.RootElement, "rowKey"), "rowKey"));
    }
    private static string GridKey(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var key) || key.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(key.GetString()) || key.GetString()!.Length > 256)
            throw new InvalidDataException($"Grid {name} must be a nonempty string with at most 256 characters.");
        return key.GetString()!;
    }
    public static ItemQuery ParseItem(string value)
    {
        using var document = Parse(value);
        var root = Object(document.RootElement, "item");
        if (!root.TryGetProperty("item", out var item)) throw new InvalidDataException("Item value requires an item object.");
        Object(item, "id", "label");
        var fields = item.EnumerateObject().ToArray();
        if (fields.Length != 1 || fields[0].Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(fields[0].Value.GetString()))
            throw new InvalidDataException("Item requires exactly one nonempty string id or label.");
        return fields[0].Name == "id" ? new(fields[0].Value.GetString(), null) : new(null, fields[0].Value.GetString());
    }
    public static ScrollPercentValue ParseScrollPercent(string value)
    {
        using var document = Parse(value);
        var root = Object(document.RootElement, "vertical", "horizontal");
        if (!root.EnumerateObject().Any()) throw new InvalidDataException("ScrollPercent requires vertical and/or horizontal.");
        double? Read(string key)
        {
            if (!root.TryGetProperty(key, out var number)) return null;
            if (number.ValueKind != JsonValueKind.Number || !number.TryGetDouble(out var result) || !double.IsFinite(result) || result is < 0 or > 100)
                throw new InvalidDataException("Scroll percentages must be finite numbers from 0 to 100.");
            return result;
        }
        return new(Read("vertical"), Read("horizontal"));
    }
    public static PropertyAssertion ParseProperty(string value)
    {
        using var document = Parse(value);
        var root = Object(document.RootElement, "property", "equals");
        if (!root.TryGetProperty("property", out var name) || name.ValueKind != JsonValueKind.String || !PropertyNames.Contains(name.GetString()!))
            throw new InvalidDataException("AssertProperty requires a registered property name.");
        if (!root.TryGetProperty("equals", out var expected) || expected.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
            throw new InvalidDataException("AssertProperty equals must be a JSON scalar.");
        if (expected.ValueKind == JsonValueKind.Number && (!expected.TryGetDouble(out var number) || !double.IsFinite(number)))
            throw new InvalidDataException("Property numbers must be finite.");
        return new(name.GetString()!, expected.Clone());
    }
    public static bool ScalarEquals(JsonElement actual, JsonElement expected)
    {
        if (actual.ValueKind != expected.ValueKind) return false;
        return actual.ValueKind switch
        {
            JsonValueKind.String => actual.GetString() == expected.GetString(),
            JsonValueKind.Number => actual.TryGetDecimal(out var a) && expected.TryGetDecimal(out var b) ? a == b : actual.TryGetDouble(out var ad) && expected.TryGetDouble(out var bd) && double.IsFinite(ad) && ad == bd,
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => false
        };
    }
    private static JsonDocument Parse(string value, int maximumCharacters = 8192)
    {
        if (value.Length > maximumCharacters) throw new InvalidDataException($"Advanced action value exceeds {maximumCharacters} characters.");
        try { return JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 4 }); }
        catch (JsonException ex) { throw new InvalidDataException("Advanced action value must be valid JSON.", ex); }
    }
    private static JsonElement Object(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Advanced action value must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!allowed.Contains(field.Name, StringComparer.Ordinal) || !seen.Add(field.Name)) throw new InvalidDataException($"Unknown or duplicate field '{field.Name}'.");
        return value;
    }
}
