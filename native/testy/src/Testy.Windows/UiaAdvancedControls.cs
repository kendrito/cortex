using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using Testy.Core;

namespace Testy.Windows;

internal static class UiaAdvancedControls
{
    private static readonly (string Name, AutomationProperty Availability, AutomationProperty Property)[] Fields =
        {
            ("uia.expandCollapseState", AutomationElement.IsExpandCollapsePatternAvailableProperty, ExpandCollapsePattern.ExpandCollapseStateProperty),
            ("uia.isSelected", AutomationElement.IsSelectionItemPatternAvailableProperty, SelectionItemPattern.IsSelectedProperty),
            ("uia.isReadOnly", AutomationElement.IsValuePatternAvailableProperty, ValuePattern.IsReadOnlyProperty),
            ("uia.isReadOnly", AutomationElement.IsRangeValuePatternAvailableProperty, RangeValuePattern.IsReadOnlyProperty),
            ("uia.horizontalScrollPercent", AutomationElement.IsScrollPatternAvailableProperty, ScrollPattern.HorizontalScrollPercentProperty),
            ("uia.verticalScrollPercent", AutomationElement.IsScrollPatternAvailableProperty, ScrollPattern.VerticalScrollPercentProperty),
            ("uia.row", AutomationElement.IsGridItemPatternAvailableProperty, GridItemPattern.RowProperty),
            ("uia.column", AutomationElement.IsGridItemPatternAvailableProperty, GridItemPattern.ColumnProperty),
            ("uia.rowCount", AutomationElement.IsGridPatternAvailableProperty, GridPattern.RowCountProperty),
            ("uia.columnCount", AutomationElement.IsGridPatternAvailableProperty, GridPattern.ColumnCountProperty)
        };
    private static readonly (AutomationProperty Availability, string Name)[] CapabilityFields =
        {
            (AutomationElement.IsExpandCollapsePatternAvailableProperty, "ExpandCollapse"),
            (AutomationElement.IsItemContainerPatternAvailableProperty, "ItemContainer"),
            (AutomationElement.IsScrollPatternAvailableProperty, "Scroll"),
            (AutomationElement.IsScrollItemPatternAvailableProperty, "ScrollItem"),
            (AutomationElement.IsVirtualizedItemPatternAvailableProperty, "VirtualizedItem")
        };
    internal static CacheRequest CreateSnapshotRequest()
    {
        var request = new CacheRequest { TreeScope = TreeScope.Element, TreeFilter = Automation.RawViewCondition };
        var basic = new[]
        {
            AutomationElement.ProcessIdProperty, AutomationElement.NativeWindowHandleProperty, AutomationElement.IsPasswordProperty,
            AutomationElement.AutomationIdProperty, AutomationElement.NameProperty, AutomationElement.ClassNameProperty,
            AutomationElement.ControlTypeProperty, AutomationElement.IsEnabledProperty, AutomationElement.IsOffscreenProperty,
            AutomationElement.BoundingRectangleProperty, AutomationElement.RuntimeIdProperty,
            AutomationElement.IsTextPatternAvailableProperty, AutomationElement.IsTogglePatternAvailableProperty,
            AutomationElement.IsSelectionPatternAvailableProperty, TogglePattern.ToggleStateProperty
        };
        foreach (var property in basic.Concat(Fields.SelectMany(f => new[] { f.Availability, f.Property })).Concat(CapabilityFields.Select(c => c.Availability)).Distinct()) request.Add(property);
        return request;
    }
    internal static bool Supports(AutomationElement cached, AutomationProperty availability)
    {
        // UIA defines false as the default for an unsupported pattern-availability property.
        // State/value properties below deliberately ignore defaults, so unavailable data cannot look like zero/empty/false.
        var value = cached.GetCachedPropertyValue(availability);
        return value is bool supported ? supported : throw new InvalidOperationException("The UI Automation provider returned invalid pattern availability.");
    }
    internal static void ObserveCached(AutomationElement cached, UiElementInfo info, string? valueText, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var (name, availability, property) in Fields)
        {
            if (info.Properties.ContainsKey(name)) continue;
            if (!Supports(cached, availability)) continue; // Omitted means Unsupported, never a fabricated default value.
            var value = cached.GetCachedPropertyValue(property, true);
            if (ReferenceEquals(value, AutomationElement.NotSupported))
                info.Properties[name] = new() { Status = UiPropertyStatus.Unavailable, Source = "Windows UI Automation: property unavailable" };
            else info.Properties[name] = Known(value is ExpandCollapseState state ? state.ToString() : value);
        }
        foreach (var (availability, name) in CapabilityFields)
        {
            if (Supports(cached, availability)) info.Capabilities.Add(name);
        }
        if (info.IsPassword) info.Properties["uia.value"] = new() { Status = UiPropertyStatus.Redacted, Source = "Password control" };
        else if (Supports(cached, AutomationElement.IsValuePatternAvailableProperty))
            info.Properties["uia.value"] = valueText is null ? new() { Status = UiPropertyStatus.Unavailable, Source = "Windows UI Automation: value unavailable" } : Known(valueText);
        if (info.Capabilities.Contains("ItemContainer") || info.ControlType is "List" or "DataGrid" or "Table" or "Tree" or "TreeItem" or "ComboBox") info.ChildCoverage = ChildCoverage.RealizedOnly;
        else if (info.Capabilities.Contains("Scroll")) info.ChildCoverage = ChildCoverage.Unknown;
        if (info.IsPassword) info.Properties["uia.isReadOnly"] = new() { Status = UiPropertyStatus.Redacted, Source = "Password control" };
    }

    private static UiPropertyObservation Known(object value)
    {
        if (value is not (string or bool or int or double)) return new() { Status = UiPropertyStatus.Unavailable, Source = "Windows UI Automation: invalid property type" };
        if (value is double number && !double.IsFinite(number)) return new() { Status = UiPropertyStatus.Unavailable, Source = "Windows UI Automation: nonfinite value" };
        bool truncated = value is string text && text.Length > 2048;
        return new() { Status = truncated ? UiPropertyStatus.Truncated : UiPropertyStatus.Known, Value = JsonSerializer.SerializeToElement(truncated ? ((string)value)[..2048] : value), Source = "Windows UI Automation" };
    }
    private static bool ProviderError(Exception ex) => ex is ElementNotAvailableException or InvalidOperationException or ArgumentException or COMException or InvalidCastException;

    internal static ItemLookupResult Lookup(AutomationElement container, ItemQuery item, CancellationToken ct)
    {
        try { var result = Find(container, item, ct); return new() { Status = result.Status, Message = result.Message, ObservedAt = DateTimeOffset.UtcNow }; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return new() { Status = ItemLookupStatus.Unsupported, Message = "The container does not support this exact item property lookup: " + ex.Message }; }
        catch (Exception ex) when (ProviderError(ex)) { return new() { Status = ItemLookupStatus.Unavailable, Message = "Item lookup could not complete: " + ex.Message }; }
    }
    private sealed record Found(ItemLookupStatus Status, string Message, ItemContainerPattern? Pattern = null);
    private static Found Find(AutomationElement container, ItemQuery query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!container.TryGetCurrentPattern(ItemContainerPattern.Pattern, out var candidate)) return new(ItemLookupStatus.Unsupported, "This control does not expose ItemContainerPattern.");
        var pattern = (ItemContainerPattern)candidate;
        var property = query.Id is not null ? AutomationElement.AutomationIdProperty : AutomationElement.NameProperty;
        object key = query.Id ?? query.Label!;
        var first = pattern.FindItemByProperty(null, property, key); ct.ThrowIfCancellationRequested();
        if (first is null) return new(ItemLookupStatus.Missing, "Supported exact lookup found no provider-exposed logical peer.", pattern);
        var second = pattern.FindItemByProperty(first, property, key); ct.ThrowIfCancellationRequested();
        return second is null ? new(ItemLookupStatus.Unique, "Exactly one provider-exposed logical peer matches; hidden equal backing items are outside this contract.", pattern)
            : new(ItemLookupStatus.Ambiguous, "Exact item lookup is ambiguous; at least two provider-exposed logical peers match.", pattern);
    }
    internal static void Execute(AutomationElement element, TestStep step, TargetInfo target, nint window, CancellationToken ct, Action? beforeInput = null)
    {
        void Guard()
        {
            ct.ThrowIfCancellationRequested(); Native.Validate(target);
            if (!Native.IsWindowEnabled(window) || !element.Current.IsEnabled) throw new InvalidOperationException("The item container is disabled or its window is blocked by a modal dialog.");
            if (element.Current.ProcessId != target.ProcessId) throw new InvalidOperationException("The control is outside the attached process.");
        }
        AdvancedSteps.Validate(step); Guard();
        switch (step.Action)
        {
            case StepAction.Expand:
            case StepAction.Collapse:
                if (!element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var exp)) throw new NotSupportedException("This control does not expose ExpandCollapsePattern.");
                var expand = (ExpandCollapsePattern)exp;
                if (expand.Current.ExpandCollapseState == ExpandCollapseState.LeafNode) throw new NotSupportedException("A leaf node cannot expand or collapse.");
                Guard(); beforeInput?.Invoke();
                if (step.Action == StepAction.Expand) expand.Expand(); else expand.Collapse();
                return;
            case StepAction.ScrollIntoView:
                if (!element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var item)) throw new NotSupportedException("This control does not expose ScrollItemPattern.");
                Guard(); beforeInput?.Invoke(); ((ScrollItemPattern)item).ScrollIntoView(); return;
            case StepAction.ScrollPercent:
                var percentages = AdvancedSteps.ParseScrollPercent(step.Value);
                if (!element.TryGetCurrentPattern(ScrollPattern.Pattern, out var scroll)) throw new NotSupportedException("This control does not expose ScrollPattern.");
                var scrolling = (ScrollPattern)scroll;
                if (percentages.Horizontal.HasValue && !scrolling.Current.HorizontallyScrollable || percentages.Vertical.HasValue && !scrolling.Current.VerticallyScrollable)
                    throw new NotSupportedException("The requested scroll direction is unsupported.");
                Guard(); beforeInput?.Invoke(); scrolling.SetScrollPercent(percentages.Horizontal ?? ScrollPattern.NoScroll, percentages.Vertical ?? ScrollPattern.NoScroll); return;
            case StepAction.RealizeItem:
                var query = AdvancedSteps.ParseItem(step.Value); var found = Find(element, query, ct);
                if (found.Status != ItemLookupStatus.Unique) throw new InvalidOperationException(found.Message);
                // A second lookup can invalidate its first placeholder; reacquire only after proving uniqueness.
                var peer = found.Pattern!.FindItemByProperty(null, query.Id is not null ? AutomationElement.AutomationIdProperty : AutomationElement.NameProperty, query.Id ?? query.Label!);
                if (peer is null) throw new ElementNotAvailableException("The unique item disappeared before realization.");
                Guard();
                if (peer.TryGetCurrentPattern(VirtualizedItemPattern.Pattern, out var virtualized)) { Guard(); beforeInput?.Invoke(); ((VirtualizedItemPattern)virtualized).Realize(); }
                // Scope before realization comes from the selected container provider, since a placeholder may expose no properties.
                if (peer.Current.ProcessId != target.ProcessId) throw new InvalidOperationException("The realized item is outside the attached process.");
                return;
            default: throw new NotSupportedException($"{step.Action} is not an advanced control mutation.");
        }
    }
}
