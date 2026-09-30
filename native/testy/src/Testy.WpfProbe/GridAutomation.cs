using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Testy.Core;

namespace Testy.WpfProbe;

/// <summary>Explicit read-only business identity for an opted-in grid's current item view.</summary>
public interface IGridRowIdentity { string TestyRowKey { get; } }

/// <summary>Opt-in standard DataGrid editing. This API never assigns item/model properties.</summary>
public static class GridAutomation
{
    public static readonly DependencyProperty EnableProperty = DependencyProperty.RegisterAttached("Enable", typeof(bool), typeof(GridAutomation), new PropertyMetadata(false));
    public static readonly DependencyProperty ColumnKeyProperty = DependencyProperty.RegisterAttached("ColumnKey", typeof(string), typeof(GridAutomation), new PropertyMetadata(""));
    public static readonly DependencyProperty EditorIdProperty = DependencyProperty.RegisterAttached("EditorId", typeof(string), typeof(GridAutomation), new PropertyMetadata(""));
    public static void SetEnable(DependencyObject target, bool value) => target.SetValue(EnableProperty, value);
    public static bool GetEnable(DependencyObject target) => (bool)target.GetValue(EnableProperty);
    public static void SetColumnKey(DependencyObject target, string value) => target.SetValue(ColumnKeyProperty, value);
    public static string GetColumnKey(DependencyObject target) => (string)target.GetValue(ColumnKeyProperty);
    /// <summary>Explicit AutomationId of the sole standard editor in a template column.</summary>
    public static void SetEditorId(DependencyObject target, string value) => target.SetValue(EditorIdProperty, value);
    public static string GetEditorId(DependencyObject target) => (string)target.GetValue(EditorIdProperty);

    internal static void Observe(DataGrid grid, UiElementInfo info)
    {
        if (!GetEnable(grid)) return;
        if (!grid.IsReadOnly) info.Capabilities.AddRange(["GridEdit", "GridCommit", "GridCancel"]);
        var view = (IEditableCollectionView)grid.Items;
        info.Properties["wpf.gridIsEditing"] = Known(view.IsEditingItem);
        if (!view.IsEditingItem) info.Properties["wpf.gridEditingRowKey"] = Known("");
        else if (view.CurrentEditItem is IGridRowIdentity identity)
        {
            string key = identity.TestyRowKey;
            info.Properties["wpf.gridEditingRowKey"] = key is { Length: <= 256 }
                ? Known(key) : new() { Status = UiPropertyStatus.Truncated, Source = "Opt-in grid row identity" };
        }
        else info.Properties["wpf.gridEditingRowKey"] = new() { Status = UiPropertyStatus.Unavailable, Source = "Editing item has no opted-in row identity" };
    }

    private static UiPropertyObservation Known(object value) => new() { Status = UiPropertyStatus.Known, Source = "Opt-in WPF DataGrid", Value = JsonSerializer.SerializeToElement(value) };

    internal static void Execute(FrameworkElement element, TestStep step, Action guard, CancellationToken ct)
    {
        if (element is not DataGrid grid || !GetEnable(grid)) throw new NotSupportedException("Grid workflow requires an explicitly opted-in WPF DataGrid.");
        if (grid.IsReadOnly) throw new InvalidOperationException("The opted-in grid is read-only.");
        var view = (IEditableCollectionView)grid.Items;
        if (step.Action == StepAction.GridEditCell)
        {
            var edit = AdvancedSteps.ParseGridEdit(step.Value);
            object item = ResolveRow(grid, edit.RowKey, ct);
            if (item is not IEditableObject) throw new NotSupportedException("Transactional grid editing requires an item implementing IEditableObject; direct model updates are not an alternative.");
            var columns = grid.Columns.Where(c => GetColumnKey(c) == edit.ColumnKey).Take(2).ToArray();
            if (columns.Length != 1) throw new InvalidOperationException(columns.Length == 0 ? "No grid column has the exact business key." : "The grid column key is ambiguous.");
            var column = columns[0];
            if (column.IsReadOnly || column.Visibility != Visibility.Visible || column is not (DataGridTextColumn or DataGridCheckBoxColumn or DataGridComboBoxColumn or DataGridTemplateColumn))
                throw new NotSupportedException("GridEditCell requires a visible editable standard text, checkbox, combo or explicitly opted-in template column.");
            if (column is DataGridTemplateColumn && (string.IsNullOrWhiteSpace(GetEditorId(column)) || GetEditorId(column).Length > 256))
                throw new NotSupportedException("A template column must opt in with a bounded GridAutomation.EditorId before editing.");
            if (column is DataGridCheckBoxColumn checkColumn) ParseBoolean(edit.Text, checkColumn.IsThreeState);
            if (view.IsEditingItem && (!ReferenceEquals(view.CurrentEditItem, item) || !ReferenceEquals(grid.CurrentCell.Column, column)))
                throw new InvalidOperationException("Another grid cell has a pending edit. Commit or cancel that row explicitly before changing cells.");

            // Realization, selection and focus are explicit effects of GridEditCell. Assertions never call this path.
            guard(); grid.ScrollIntoView(item, column); grid.UpdateLayout();
            var row = grid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow
                ?? throw new InvalidOperationException("The keyed row could not be realized.");
            if (!ReferenceEquals(row.Item, item)) throw new InvalidOperationException("The realized row was recycled before editing.");
            var cell = Descendants<DataGridCell>(row).SingleOrDefault(c => ReferenceEquals(c.Column, column))
                ?? throw new InvalidOperationException("The keyed cell could not be realized.");
            guard(); grid.SelectedItem = item; grid.CurrentCell = new DataGridCellInfo(item, column); cell.Focus();
            guard(); if (!grid.BeginEdit()) throw new InvalidOperationException("The DataGrid rejected BeginEdit.");
            grid.UpdateLayout();
            var editors = Descendants<FrameworkElement>(cell).Where(t => t is TextBox or CheckBox or ComboBox)
                .Where(t => column is DataGridTemplateColumn ? AutomationProperties.GetAutomationId(t) == GetEditorId(column)
                    : column is DataGridTextColumn ? t is TextBox : column is DataGridCheckBoxColumn ? t is CheckBox : t is ComboBox).Take(2).ToArray();
            if (editors.Length != 1 || !cell.IsEditing || !row.IsEditing || !ReferenceEquals(row.Item, item))
                throw new InvalidOperationException("A unique standard editor was not created for the keyed cell; edit outcome must be inspected.");
            var editor = editors[0];
            // Resolve binding/value/option identity before writing anything to the editor.
            Action write = PrepareEditorWrite(editor, edit.Text);
            guard();
            if (!ReferenceEquals(ResolveRow(grid, edit.RowKey, ct), item) || !ReferenceEquals(row.Item, item) || !ReferenceEquals(cell.Column, column) || GetColumnKey(column) != edit.ColumnKey)
                throw new InvalidOperationException("The business row or column changed during BeginEdit; no editor value was written.");
            if (!editor.IsEnabled || !editor.IsVisible || editor is TextBox { IsReadOnly: true })
                throw new InvalidOperationException("The generated editor is no longer visible and editable; no value was written.");
            write();
            // Commit is deliberately separate. WPF binding/validation semantics remain authoritative.
            return;
        }

        var requested = AdvancedSteps.ParseGridRow(step.Value);
        object expected = ResolveRow(grid, requested.RowKey, ct);
        if (expected is not IEditableObject) throw new NotSupportedException("Transactional grid commands require an item implementing IEditableObject.");
        if (!view.IsEditingItem || !ReferenceEquals(view.CurrentEditItem, expected))
            throw new InvalidOperationException("The requested business row is not the grid's current pending edit.");
        var editingRow = grid.ItemContainerGenerator.ContainerFromItem(expected) as DataGridRow;
        if (editingRow is null || !ReferenceEquals(editingRow.Item, expected)) throw new InvalidOperationException("The pending row's visual identity is unavailable.");
        switch (step.Action)
        {
            case StepAction.GridCommitRow:
                guard();
                if (!grid.CommitEdit(DataGridEditingUnit.Cell, true)) throw new InvalidOperationException("Grid cell commit was rejected by validation or application code; the row is not committed.");
                guard();
                if (!ReferenceEquals(ResolveRow(grid, requested.RowKey, ct), expected) || !ReferenceEquals(view.CurrentEditItem, expected))
                    throw new InvalidOperationException("The business row changed during cell commit; no row commit was issued.");
                if (!grid.CommitEdit(DataGridEditingUnit.Row, true) || view.IsEditingItem || editingRow.IsEditing)
                    throw new InvalidOperationException("Grid row commit was rejected or remains pending. Application persistence is not established.");
                return;
            case StepAction.GridCancelRow:
                guard();
                if (!grid.CancelEdit(DataGridEditingUnit.Cell)) throw new InvalidOperationException("Grid cell cancellation was rejected.");
                guard();
                if (!ReferenceEquals(ResolveRow(grid, requested.RowKey, ct), expected) || view.IsEditingItem && !ReferenceEquals(view.CurrentEditItem, expected))
                    throw new InvalidOperationException("The business row changed during cell cancellation; no row cancellation was issued.");
                if (!grid.CancelEdit(DataGridEditingUnit.Row) || view.IsEditingItem || editingRow.IsEditing)
                    throw new InvalidOperationException("Grid row cancellation was rejected or remains pending.");
                return;
            default: throw new NotSupportedException("Unsupported grid workflow action.");
        }
    }

    private static bool? ParseBoolean(string value, bool threeState) => value switch
    {
        "true" => true, "false" => false, "null" when threeState => null,
        _ => throw new ArgumentException("Checkbox editor text must be exactly true or false (null only for an explicitly three-state editor).")
    };

    private static Action PrepareEditorWrite(FrameworkElement editor, string value)
    {
        static void Binding(DependencyObject control, DependencyProperty property)
        {
            var expression = BindingOperations.GetBindingExpression(control, property);
            if (expression is null || expression.ParentBinding.Mode is BindingMode.OneWay or BindingMode.OneTime or BindingMode.OneWayToSource)
                throw new NotSupportedException("The generated editor requires a standard writable two-way binding; no value was written.");
            if (expression.ParentBinding.Mode == BindingMode.Default && property.GetMetadata(control.GetType()) is FrameworkPropertyMetadata metadata && !metadata.BindsTwoWayByDefault)
                throw new NotSupportedException("The generated editor binding does not default to two-way updates; no value was written.");
        }
        switch (editor)
        {
            case TextBox text:
                Binding(text, TextBox.TextProperty);
                return () => { text.SetCurrentValue(TextBox.TextProperty, value); if (text.Text != value) throw new InvalidOperationException("The editor did not retain the requested text."); };
            case CheckBox check:
                Binding(check, ToggleButton.IsCheckedProperty); bool? expected = ParseBoolean(value, check.IsThreeState);
                return () => { check.SetCurrentValue(ToggleButton.IsCheckedProperty, expected); if (check.IsChecked != expected) throw new InvalidOperationException("The editor did not retain the requested checkbox state."); };
            case ComboBox combo:
                if (combo.IsEditable) throw new NotSupportedException("Editable combo free-text editors are unsupported; use an exact choice in a noneditable combo.");
                if (BindingOperations.GetBindingExpression(combo, Selector.SelectedItemProperty) is not null) Binding(combo, Selector.SelectedItemProperty);
                else if (BindingOperations.GetBindingExpression(combo, Selector.SelectedValueProperty) is not null) Binding(combo, Selector.SelectedValueProperty);
                else throw new NotSupportedException("The combo requires a writable SelectedItem or SelectedValue binding.");
                object selected = ResolveChoice(combo, value);
                return () =>
                {
                    if (!ReferenceEquals(ResolveChoice(combo, value), selected)) throw new InvalidOperationException("The combo choice identity changed before editor input.");
                    combo.SetCurrentValue(Selector.SelectedItemProperty, selected);
                    if (!ReferenceEquals(combo.SelectedItem, selected)) throw new InvalidOperationException("The combo did not retain the requested exact item identity.");
                };
            default: throw new NotSupportedException("Unsupported grid editor; no value was written.");
        }
    }
    private static object ResolveChoice(ComboBox combo, string label)
    {
        if (combo.Items.Count > 2000) throw new NotSupportedException("Combo item lookup is bounded to 2,000 current items.");
        object? found = null;
        foreach (object item in combo.Items)
        {
            // No arbitrary item property/reflection getter or ToString call. Complex choices
            // need a realized ComboBoxItem with an explicit accessible name.
            var container = item as ComboBoxItem ?? combo.ItemContainerGenerator.ContainerFromItem(item) as ComboBoxItem;
            string? name = item as string;
            if (name is null && container is not null)
            {
                name = AutomationProperties.GetName(container);
                if (string.IsNullOrEmpty(name)) name = container.Content as string;
            }
            if (name is null || name.Length > 4096) throw new NotSupportedException("Every combo choice needs a bounded exact string or observed accessible container label.");
            if (name != label) continue;
            if (found is not null) throw new InvalidOperationException("The combo choice label is ambiguous; no selection was made.");
            found = item;
        }
        return found ?? throw new InvalidOperationException("No combo choice has the exact requested label; no selection was made.");
    }

    private static object ResolveRow(DataGrid grid, string key, CancellationToken ct)
    {
        if (grid.Items.Count > 20000) throw new NotSupportedException("Grid business-key lookup is bounded to 20,000 items in the current view.");
        object? found = null;
        foreach (object item in grid.Items)
        {
            ct.ThrowIfCancellationRequested();
            if (ReferenceEquals(item, CollectionView.NewItemPlaceholder)) continue;
            if (item is not IGridRowIdentity identity) throw new NotSupportedException("Every current-view grid item must implement the opted-in IGridRowIdentity contract.");
            string candidate = identity.TestyRowKey;
            if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 256) throw new InvalidOperationException("A grid item supplied an invalid business key.");
            if (candidate != key) continue;
            if (found is not null) throw new InvalidOperationException("The grid row business key is ambiguous.");
            found = item;
        }
        return found ?? throw new InvalidOperationException("No grid row has the exact business key in the current view.");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        var stack = new Stack<DependencyObject>(); stack.Push(parent); int count = 0;
        while (stack.Count > 0)
        {
            if (++count > 2000) throw new InvalidOperationException("The realized cell subtree exceeds the bounded grid inspection limit.");
            var current = stack.Pop();
            if (current is T match) yield return match;
            for (int i = VisualTreeHelper.GetChildrenCount(current) - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(current, i));
        }
    }
}
