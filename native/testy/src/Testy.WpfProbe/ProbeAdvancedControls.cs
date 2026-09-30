using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Testy.Core;

namespace Testy.WpfProbe;

internal static class ProbeAdvancedControls
{
    internal static void Observe(FrameworkElement element, UiElementInfo info, CancellationToken ct)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(element);
        // Standard WPF item peers attach their logical peer to the realized wrapper's EventsSource.
        // GetChildren reads the existing item-host children; it neither realizes items nor changes selection/scroll.
        // Keep this initialization restricted to the known WPF implementations, never an arbitrary custom peer.
        if (peer?.GetType() == typeof(DataGridAutomationPeer) || peer?.GetType() == typeof(ListBoxAutomationPeer))
        {
            ct.ThrowIfCancellationRequested();
            _ = peer.GetChildren();
        }
        if (peer is DataGridRowAutomationPeer && peer.EventsSource is DataGridItemAutomationPeer rowPeer)
        {
            ct.ThrowIfCancellationRequested();
            ValidateItemPeer(peer, rowPeer);
            // Initialize the already-realized row's cell peer links; this never calls Grid.GetItem,
            // which would scroll/realize a cell as a side effect.
            _ = rowPeer.GetChildren();
        }
        var cache = new Dictionary<PatternInterface, object?>();
        object? Pattern(PatternInterface pattern)
        {
            ct.ThrowIfCancellationRequested();
            if (!cache.TryGetValue(pattern, out var value)) { value = PatternFromPeer(peer, pattern); cache[pattern] = value; }
            return value;
        }
        void Property(string name, PatternInterface pattern, Func<object, object> read)
        {
            try { var supported = Pattern(pattern); if (supported is not null) info.Properties[name] = Known(read(supported), "WPF automation peer"); }
            catch (Exception ex) when (ProviderError(ex)) { info.Properties[name] = new() { Status = UiPropertyStatus.Unavailable, Source = "WPF automation peer: " + ex.GetType().Name }; }
        }
        Property("uia.expandCollapseState", PatternInterface.ExpandCollapse, p => ((IExpandCollapseProvider)p).ExpandCollapseState.ToString());
        Property("uia.isSelected", PatternInterface.SelectionItem, p => ((ISelectionItemProvider)p).IsSelected);
        Property("uia.isReadOnly", PatternInterface.Value, p => ((IValueProvider)p).IsReadOnly);
        if (info.IsPassword) info.Properties["uia.value"] = new() { Status = UiPropertyStatus.Redacted, Source = "Password control" };
        else Property("uia.value", PatternInterface.Value, p => ((IValueProvider)p).Value ?? throw new InvalidOperationException("The value provider returned null text."));
        if (!info.Properties.ContainsKey("uia.isReadOnly")) Property("uia.isReadOnly", PatternInterface.RangeValue, p => ((IRangeValueProvider)p).IsReadOnly);
        Property("uia.horizontalScrollPercent", PatternInterface.Scroll, p => ((IScrollProvider)p).HorizontalScrollPercent);
        Property("uia.verticalScrollPercent", PatternInterface.Scroll, p => ((IScrollProvider)p).VerticalScrollPercent);
        Property("uia.row", PatternInterface.GridItem, p => ((IGridItemProvider)p).Row);
        Property("uia.column", PatternInterface.GridItem, p => ((IGridItemProvider)p).Column);
        Property("uia.rowCount", PatternInterface.Grid, p => ((IGridProvider)p).RowCount);
        Property("uia.columnCount", PatternInterface.Grid, p => ((IGridProvider)p).ColumnCount);
        foreach (var (pattern, name) in new[] { (PatternInterface.ExpandCollapse, "ExpandCollapse"), (PatternInterface.ItemContainer, "ItemContainer"), (PatternInterface.Scroll, "Scroll"), (PatternInterface.ScrollItem, "ScrollItem"), (PatternInterface.VirtualizedItem, "VirtualizedItem") })
        {
            try { if (Pattern(pattern) is not null) info.Capabilities.Add(name); }
            catch (Exception ex) when (ProviderError(ex)) { info.ChildCoverage = ChildCoverage.Unknown; }
        }
        if (element is Control || Validation.GetHasError(element)) info.Properties["wpf.validationHasError"] = Known(Validation.GetHasError(element), "WPF dependency property");
        if (element is ItemsControl items)
        {
            info.Properties["wpf.hasItems"] = Known(items.HasItems, "WPF dependency property");
            info.Properties["wpf.isVirtualizing"] = Known(VirtualizingPanel.GetIsVirtualizing(items), "WPF dependency property");
            info.ChildCoverage = ChildCoverage.RealizedOnly;
        }
        else if (element is ScrollViewer || info.Capabilities.Contains("Scroll")) info.ChildCoverage = ChildCoverage.Unknown;
        if (element is DataGrid grid)
        {
            info.Properties["wpf.enableRowVirtualization"] = Known(grid.EnableRowVirtualization, "WPF dependency property");
            info.Properties["wpf.enableColumnVirtualization"] = Known(grid.EnableColumnVirtualization, "WPF dependency property");
            info.Properties["uia.isReadOnly"] = Known(grid.IsReadOnly, "WPF DataGrid.IsReadOnly");
        }
        if (info.IsPassword) info.Properties["uia.isReadOnly"] = new() { Status = UiPropertyStatus.Redacted, Source = "Password control" };
    }
    private static UiPropertyObservation Known(object value, string source)
    {
        if (value is double number && !double.IsFinite(number)) return new() { Status = UiPropertyStatus.Unavailable, Source = source + ": nonfinite value" };
        bool truncated = value is string text && text.Length > 2048;
        return new() { Status = truncated ? UiPropertyStatus.Truncated : UiPropertyStatus.Known, Value = JsonSerializer.SerializeToElement(truncated ? ((string)value)[..2048] : value), Source = source };
    }
    private static bool ProviderError(Exception ex) => ex is ElementNotAvailableException or InvalidOperationException or ArgumentException or COMException or InvalidCastException;
    internal static object? PatternFor(FrameworkElement element, PatternInterface pattern) => PatternFromPeer(UIElementAutomationPeer.CreatePeerForElement(element), pattern);
    private static object? PatternFromPeer(AutomationPeer? peer, PatternInterface pattern)
    {
        // A ScrollViewer can redirect events to its owning ListBox; that must not turn it into
        // the list's item container. Only the public logical-item bridge is followed here.
        if (peer?.EventsSource is ItemAutomationPeer itemPeer)
        {
            ValidateItemPeer(peer, itemPeer);
            var logical = itemPeer.GetPattern(pattern);
            if (logical is not null) return logical;
        }
        if (peer is DataGridCellAutomationPeer cellPeer && peer.EventsSource is DataGridCellItemAutomationPeer logicalCell)
        {
            var cell = (DataGridCell)cellPeer.Owner;
            var row = DataGridRow.GetRowContainingElement(cell);
            var grid = row is null ? null : ItemsControl.ItemsControlFromItemContainer(row) as DataGrid;
            int rowIndex = grid?.Items.IndexOf(row!.Item) ?? -1;
            int columnIndex = grid is null || cell.Column is null ? -1 : grid.Columns.IndexOf(cell.Column);
            if (grid is null || cell.Column is null || logicalCell.GetPattern(PatternInterface.GridItem) is not IGridItemProvider gridItem
                || rowIndex < 0 || columnIndex < 0
                || gridItem.Row != rowIndex || gridItem.Column != columnIndex)
                throw new ElementNotAvailableException("The logical grid cell no longer matches its visible row and column.");
            var logical = logicalCell.GetPattern(pattern);
            if (logical is not null) return logical;
        }
        return peer?.GetPattern(pattern);
    }
    private static void ValidateItemPeer(AutomationPeer peer, ItemAutomationPeer itemPeer)
    {
        if (peer is not UIElementAutomationPeer visual || ItemsControl.ItemsControlFromItemContainer(visual.Owner) is not { } owner
            || !ReferenceEquals(itemPeer.ItemsControlAutomationPeer?.Owner, owner))
            throw new ElementNotAvailableException("The logical item peer is no longer attached to this visual's item container.");
        var currentItem = owner.ItemContainerGenerator.ItemFromContainer(visual.Owner);
        if (ReferenceEquals(currentItem, DependencyProperty.UnsetValue) || !SameItem(currentItem, itemPeer.Item))
            throw new ElementNotAvailableException("The visual item was recycled; its logical identity must be observed again.");
    }
    private static bool SameItem(object? actual, object? expected) => ReferenceEquals(actual, expected)
        || actual is not null && actual.GetType().IsValueType && actual.Equals(expected);
    private sealed record Found(ItemLookupStatus Status, string Message, IItemContainerProvider? Pattern = null);
    private static Found Find(FrameworkElement element, ItemQuery item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var peer = UIElementAutomationPeer.CreatePeerForElement(element);
        if (PatternFromPeer(peer, PatternInterface.ItemContainer) is not IItemContainerProvider container) return new(ItemLookupStatus.Unsupported, "This WPF control does not expose the ItemContainer pattern.");
        int property = item.Id is not null ? AutomationElementIdentifiers.AutomationIdProperty.Id : AutomationElementIdentifiers.NameProperty.Id;
        var first = container.FindItemByProperty(null!, property, item.Id ?? item.Label!); ct.ThrowIfCancellationRequested();
        if (first is null) return new(ItemLookupStatus.Missing, "Supported exact lookup found no provider-exposed logical peer.", container);
        var second = container.FindItemByProperty(first, property, item.Id ?? item.Label!); ct.ThrowIfCancellationRequested();
        return second is null ? new(ItemLookupStatus.Unique, "Exactly one provider-exposed logical peer matches; hidden equal backing items are outside this contract.", container)
            : new(ItemLookupStatus.Ambiguous, "Exact item lookup is ambiguous; at least two provider-exposed logical peers match.", container);
    }
    internal static ItemLookupResult Lookup(FrameworkElement element, ItemQuery item, CancellationToken ct)
    {
        try { var result = Find(element, item, ct); return new() { Status = result.Status, Message = result.Message, ObservedAt = DateTimeOffset.UtcNow }; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return new() { Status = ItemLookupStatus.Unsupported, Message = "Exact item property lookup is unsupported: " + ex.Message }; }
        catch (Exception ex) when (ProviderError(ex)) { return new() { Status = ItemLookupStatus.Unavailable, Message = "Item lookup could not complete: " + ex.Message }; }
    }
    internal static void Execute(FrameworkElement element, TestStep step, Action guard, CancellationToken ct)
    {
        AdvancedSteps.Validate(step); guard();
        var peer = UIElementAutomationPeer.CreatePeerForElement(element);
        switch (step.Action)
        {
            case StepAction.Expand:
            case StepAction.Collapse:
                if (PatternFromPeer(peer, PatternInterface.ExpandCollapse) is not IExpandCollapseProvider expand) throw new NotSupportedException("This WPF control does not expose ExpandCollapse.");
                if (expand.ExpandCollapseState == ExpandCollapseState.LeafNode) throw new NotSupportedException("A leaf node cannot expand or collapse.");
                guard(); if (step.Action == StepAction.Expand) expand.Expand(); else expand.Collapse(); return;
            case StepAction.ScrollIntoView:
                if (PatternFromPeer(peer, PatternInterface.ScrollItem) is not IScrollItemProvider scrollItem) throw new NotSupportedException("This WPF control does not expose ScrollItem.");
                guard(); scrollItem.ScrollIntoView(); return;
            case StepAction.ScrollPercent:
                var percentages = AdvancedSteps.ParseScrollPercent(step.Value);
                if (PatternFromPeer(peer, PatternInterface.Scroll) is not IScrollProvider scroll) throw new NotSupportedException("This WPF control does not expose Scroll.");
                if (percentages.Horizontal.HasValue && !scroll.HorizontallyScrollable || percentages.Vertical.HasValue && !scroll.VerticallyScrollable) throw new NotSupportedException("The requested scroll direction is unsupported.");
                guard(); scroll.SetScrollPercent(percentages.Horizontal ?? ScrollPatternIdentifiers.NoScroll, percentages.Vertical ?? ScrollPatternIdentifiers.NoScroll); return;
            case StepAction.RealizeItem:
                var query = AdvancedSteps.ParseItem(step.Value); var found = Find(element, query, ct);
                if (found.Status != ItemLookupStatus.Unique) throw new InvalidOperationException(found.Message);
                var provider = found.Pattern!.FindItemByProperty(null!, query.Id is not null ? AutomationElementIdentifiers.AutomationIdProperty.Id : AutomationElementIdentifiers.NameProperty.Id, query.Id ?? query.Label!);
                if (provider is null) throw new ElementNotAvailableException("The unique item disappeared before realization.");
                guard();
                if (provider.GetPatternProvider(VirtualizedItemPatternIdentifiers.Pattern.Id) is IVirtualizedItemProvider virtualized) { guard(); virtualized.Realize(); }
                else
                {
                    // A fully realized provider need not expose VirtualizedItem; ensure it is still an actual item.
                    _ = provider.GetPropertyValue(AutomationElementIdentifiers.ControlTypeProperty.Id);
                }
                return;
            default: throw new NotSupportedException($"{step.Action} is not an advanced WPF mutation.");
        }
    }
}
