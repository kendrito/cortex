using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Testy.WpfProbe;

namespace Testy.WpfLab;

/// <summary>Owned edit/commit fixture. Its persisted store changes only after the UI transaction ends.</summary>
public sealed class GridWorkflowWindow : Window
{
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    private readonly ObservableCollection<InvoiceRow> rows = [];
    private readonly Dictionary<string, int> persisted = new(StringComparer.Ordinal);
    private readonly DataGrid grid = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Single, EnableRowVirtualization = true, EnableColumnVirtualization = true, MinHeight = 230 };
    private readonly TextBlock status = Label("EditStatus", "Ready"), persistedState = Label("PersistedState", "INV-0001=1"), counters = Label("EditCounters", ""), savedKey = Label("LastSavedKey", "INV-0001"), savedQuantity = Label("LastSavedQuantity", "1");
    private readonly TextBlock bindingFailure = Label("BindingFailure", "");
    private readonly CheckBox duplicateRows = Toggle("DuplicateRowKeys", "Duplicate row key"), duplicateColumns = Toggle("DuplicateColumnKeys", "Duplicate column key"), reject = Toggle("RejectCommit", "Reject UI commit"), delay = Toggle("DelaySave", "Delay save 700 ms"), fail = Toggle("FailSave", "Fail persistence"), wrong = Toggle("WrongSave", "Save wrong quantity"), commandAllowed = Toggle("CommandAllowed", "Enable command");
    private readonly ListCollectionView view;
    private readonly StackPanel recovery = new() { Orientation = Orientation.Horizontal };
    private readonly Button recoveryApply;
    private readonly TextBlock recoveryCount = Label("RecoveryCount", "0");
    private readonly List<FrameworkElement> extraRecovery = [];
    private int beginCount, commitCount, cancelCount, savedCount, recoveryInvocations, generation;
    private bool resetting;

    public GridWorkflowWindow()
    {
        view = new ListCollectionView(rows);
        Title = "Testy WPF Grid Workflow Lab"; Width = 1060; Height = 780; MinWidth = 920; MinHeight = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Background = System.Windows.Media.Brushes.WhiteSmoke;
        AutomationProperties.SetAutomationId(this, "GridWorkflowWindow");
        var root = new Grid { Margin = new Thickness(16) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto }) root.RowDefinitions.Add(new() { Height = height });
        root.Children.Add(new TextBlock { Text = "WPF edit, commit and diagnostic verification", FontSize = 23, FontWeight = FontWeights.SemiBold });
        var toolbar = new WrapPanel { Margin = new Thickness(0, 10, 0, 6) }; Grid.SetRow(toolbar, 1); root.Children.Add(toolbar);
        toolbar.Children.Add(Button("EditReset", "Reset", Reset));
        toolbar.Children.Add(Button("EditSortDescending", "Sort descending", () => { if (!RequireNoEdit()) return; view.SortDescriptions.Clear(); view.SortDescriptions.Add(new(nameof(InvoiceRow.TestyRowKey), ListSortDirection.Descending)); status.Text = "Sorted descending"; }));
        toolbar.Children.Add(Button("EditFilterHigh", "Filter 501+", () => { if (!RequireNoEdit()) return; view.Filter = row => ((InvoiceRow)row).Number >= 501; status.Text = "Filtered to 501+"; }));
        toolbar.Children.Add(Button("EditClearFilter", "Clear filter", () => { if (!RequireNoEdit()) return; view.Filter = null; status.Text = "Filter cleared"; }));
        toolbar.Children.Add(Button("EditReorderColumns", "Reorder columns", () => { if (!RequireNoEdit()) return; grid.Columns[1].DisplayIndex = 2; status.Text = "Quantity column moved last"; }));
        toolbar.Children.Add(Button("EditOpenModal", "Open modal", OpenModal));
        var switches = new WrapPanel(); Grid.SetRow(switches, 2); root.Children.Add(switches);
        foreach (var toggle in new[] { duplicateRows, duplicateColumns, reject, delay, fail, wrong }) switches.Children.Add(toggle);
        void UpdateDuplicateRows(object sender, RoutedEventArgs e)
        {
            if (!CanChangeFixtureToggle(duplicateRows)) return;
            if (duplicateRows.IsChecked == true) rows.Add(NewRow(1001, "INV-0001"));
            else foreach (var row in rows.Where(r => r.Number == 1001).ToArray()) rows.Remove(row);
        }
        void UpdateDuplicateColumns(object sender, RoutedEventArgs e)
        {
            if (!CanChangeFixtureToggle(duplicateColumns)) return;
            if (duplicateColumns.IsChecked == true) grid.Columns.Add(QuantityColumn("Duplicate quantity"));
            else foreach (var column in grid.Columns.Where(c => Equals(c.Header, "Duplicate quantity")).ToArray()) grid.Columns.Remove(column);
        }
        duplicateRows.Checked += UpdateDuplicateRows; duplicateRows.Unchecked += UpdateDuplicateRows;
        duplicateColumns.Checked += UpdateDuplicateColumns; duplicateColumns.Unchecked += UpdateDuplicateColumns;
        Grid.SetRow(grid, 3); root.Children.Add(grid); AutomationProperties.SetAutomationId(grid, "EditableOrders"); AutomationProperties.SetName(grid, "Editable invoices");
        GridAutomation.SetEnable(grid, true); VirtualizingPanel.SetIsVirtualizing(grid, true); VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling);
        var rowStyle = new Style(typeof(DataGridRow)); rowStyle.Setters.Add(new Setter(AutomationProperties.AutomationIdProperty, new Binding(nameof(InvoiceRow.TestyRowKey)))); rowStyle.Setters.Add(new Setter(AutomationProperties.NameProperty, new Binding(nameof(InvoiceRow.TestyRowKey)))); grid.RowStyle = rowStyle;
        var cellStyle = new Style(typeof(DataGridCell));
        var cellId = new MultiBinding { Converter = new GridCellIdConverter() };
        cellId.Bindings.Add(new Binding(nameof(InvoiceRow.TestyRowKey))); cellId.Bindings.Add(new Binding("Column") { RelativeSource = new(RelativeSourceMode.Self) });
        cellStyle.Setters.Add(new Setter(AutomationProperties.AutomationIdProperty, cellId)); grid.CellStyle = cellStyle;
        var keyColumn = new DataGridTextColumn { Header = "Key", Binding = new Binding(nameof(InvoiceRow.TestyRowKey)), IsReadOnly = true, Width = 160 }; GridAutomation.SetColumnKey(keyColumn, "Key");
        var noteColumn = new DataGridTextColumn { Header = "Note", Binding = new Binding(nameof(InvoiceRow.Note)) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus, NotifyOnValidationError = true }, Width = new DataGridLength(1, DataGridLengthUnitType.Star), EditingElementStyle = EditorStyle() }; GridAutomation.SetColumnKey(noteColumn, "Note");
        grid.Columns.Add(keyColumn); grid.Columns.Add(QuantityColumn("Quantity")); grid.Columns.Add(noteColumn);
        grid.ItemsSource = view;
        grid.RowEditEnding += (_, e) => { if (!resetting && e.EditAction == DataGridEditAction.Commit && reject.IsChecked == true) { e.Cancel = true; status.Text = "Commit rejected"; } };

        var state = new StackPanel { Margin = new Thickness(0, 8, 0, 5) }; Grid.SetRow(state, 4); root.Children.Add(state);
        foreach (var text in new[] { status, persistedState, counters, savedKey, savedQuantity }) state.Children.Add(text);
        var diagnostics = new WrapPanel(); Grid.SetRow(diagnostics, 5); root.Children.Add(diagnostics);
        bindingFailure.SetBinding(TextBlock.TextProperty, new Binding("MissingFixtureProperty") { Source = new object(), FallbackValue = "Binding unavailable" }); diagnostics.Children.Add(bindingFailure);
        diagnostics.Children.Add(Button("RepairBinding", "Repair binding", () => bindingFailure.SetBinding(TextBlock.TextProperty, new Binding(nameof(BindingFixture.Text)) { Source = new BindingFixture() })));
        var secret = new PasswordBox { Password = "fixture-editor-secret-42", Width = 130, Margin = new Thickness(4), Padding = new Thickness(4) }; AutomationProperties.SetAutomationId(secret, "EditorPassword"); diagnostics.Children.Add(secret);
        commandAllowed.IsChecked = true; diagnostics.Children.Add(commandAllowed);
        commandAllowed.Checked += (_, _) => CommandManager.InvalidateRequerySuggested();
        commandAllowed.Unchecked += (_, _) => CommandManager.InvalidateRequerySuggested();
        var command = new RoutedUICommand("Fixture diagnostic command", "FixtureDiagnostic", typeof(GridWorkflowWindow));
        CommandBindings.Add(new CommandBinding(command, (_, _) => status.Text = "Diagnostic command observed", (_, e) => e.CanExecute = commandAllowed.IsChecked == true));
        var commandButton = Button("DiagnosticCommand", "Run diagnostic command", () => { }); commandButton.Command = command; diagnostics.Children.Add(commandButton);

        var recoveryGroup = new GroupBox { Header = "Selector recovery fixture", Content = recovery };
        AutomationProperties.SetAutomationId(recoveryGroup, "RecoveryArea"); Grid.SetRow(recoveryGroup, 6); root.Children.Add(recoveryGroup);
        recoveryApply = Button("OriginalApply", "Apply recovery fixture", () => { recoveryCount.Text = (++recoveryInvocations).ToString(CultureInfo.InvariantCulture); }); AutomationProperties.SetName(recoveryApply, "Apply recovery fixture"); recovery.Children.Add(recoveryApply);
        recovery.Children.Add(Button("ChangeSelector", "Change selector", () => AutomationProperties.SetAutomationId(recoveryApply, "UpdatedApply")));
        recovery.Children.Add(Button("DuplicateRecovery", "Duplicate alternative", () => { var duplicate = Button("UpdatedApply", "Apply recovery fixture", () => { recoveryCount.Text = (++recoveryInvocations).ToString(CultureInfo.InvariantCulture); }); AutomationProperties.SetName(duplicate, "Apply recovery fixture"); extraRecovery.Add(duplicate); recovery.Children.Add(duplicate); }));
        recovery.Children.Add(recoveryCount);
        Content = root;
        Loaded += (_, _) => { ShowWindow(new WindowInteropHelper(this).Handle, 5); Activate(); };
        Reset();
    }

    private static DataGridTextColumn QuantityColumn(string header)
    {
        var binding = new Binding(nameof(InvoiceRow.Quantity)) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus, NotifyOnValidationError = true, ValidatesOnExceptions = true };
        binding.ValidationRules.Add(new InvoiceQuantityRule());
        var column = new DataGridTextColumn { Header = header, Binding = binding, Width = 145, EditingElementStyle = EditorStyle() }; GridAutomation.SetColumnKey(column, "Quantity"); return column;
    }
    private static Style EditorStyle() { var style = new Style(typeof(TextBox)); style.Setters.Add(new Setter(AutomationProperties.AutomationIdProperty, "GridCellEditor")); return style; }
    private InvoiceRow NewRow(int number, string? key = null) => new(number, key ?? $"INV-{number:0000}", Began, Committed, Cancelled);
    private void Began(InvoiceRow row) { if (!resetting) { beginCount++; status.Text = "Editing " + row.TestyRowKey; RefreshCounters(); } }
    private void Cancelled(InvoiceRow row) { if (!resetting) { cancelCount++; status.Text = "Cancelled " + row.TestyRowKey; RefreshCounters(); } }
    private void Committed(InvoiceRow row)
    {
        if (resetting) return;
        commitCount++; RefreshCounters(); int stamp = generation; string key = row.TestyRowKey; int quantity = row.Quantity;
        bool failSave = fail.IsChecked == true, wrongSave = wrong.IsChecked == true, delaySave = delay.IsChecked == true;
        status.Text = "Saving " + key + "...";
        async void Save()
        {
            if (delaySave) await Task.Delay(700);
            if (stamp != generation || !IsLoaded) return;
            if (failSave) { status.Text = "Save failed " + key; return; }
            persisted[key] = wrongSave ? quantity + 1 : quantity;
            savedKey.Text = key; savedQuantity.Text = persisted[key].ToString(CultureInfo.InvariantCulture); persistedState.Text = key + "=" + savedQuantity.Text;
            status.Text = $"Saved {key}: {persisted[key]}"; savedCount++; RefreshCounters();
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Background, Save);
    }
    private void Reset()
    {
        resetting = true; generation++;
        grid.CancelEdit(DataGridEditingUnit.Cell); grid.CancelEdit(DataGridEditingUnit.Row);
        view.Filter = null; view.SortDescriptions.Clear(); rows.Clear(); persisted.Clear();
        for (int i = 1; i <= 1000; i++) { var row = NewRow(i); rows.Add(row); persisted[row.TestyRowKey] = 1; }
        foreach (var toggle in new[] { duplicateRows, duplicateColumns, reject, delay, fail, wrong }) toggle.IsChecked = false;
        foreach (var column in grid.Columns.Where(c => Equals(c.Header, "Duplicate quantity")).ToArray()) grid.Columns.Remove(column);
        grid.Columns[0].DisplayIndex = 0; grid.Columns[1].DisplayIndex = 1; grid.SelectedItem = null; grid.ScrollIntoView(rows[0]);
        beginCount = commitCount = cancelCount = savedCount = recoveryInvocations = 0; status.Text = "Ready"; persistedState.Text = "INV-0001=1"; savedKey.Text = "INV-0001"; savedQuantity.Text = "1"; RefreshCounters();
        AutomationProperties.SetAutomationId(recoveryApply, "OriginalApply"); recoveryApply.IsEnabled = true; recoveryCount.Text = "0";
        foreach (var extra in extraRecovery) recovery.Children.Remove(extra); extraRecovery.Clear();
        bindingFailure.SetBinding(TextBlock.TextProperty, new Binding("MissingFixtureProperty") { Source = new object(), FallbackValue = "Binding unavailable" });
        commandAllowed.IsChecked = true; CommandManager.InvalidateRequerySuggested();
        resetting = false;
    }
    private bool CanChangeFixtureToggle(CheckBox toggle)
    {
        if (resetting) return false;
        if (RequireNoEdit()) return true;
        resetting = true;
        try { toggle.IsChecked = toggle.IsChecked != true; }
        finally { resetting = false; }
        return false;
    }
    private bool RequireNoEdit() { if (!((IEditableCollectionView)grid.Items).IsEditingItem) return true; status.Text = "Commit or cancel before changing the view"; return false; }
    private void RefreshCounters() => counters.Text = $"begin={beginCount}; commit={commitCount}; cancel={cancelCount}; saved={savedCount}";
    private void OpenModal()
    {
        var dialog = new Window { Title = "Grid workflow modal", Owner = this, Width = 340, Height = 180, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Content = Button("EditCloseModal", "Close modal", dialog.Close); dialog.ShowDialog();
    }
    private static TextBlock Label(string id, string text) { var result = new TextBlock { Text = text, Margin = new Thickness(3), TextWrapping = TextWrapping.Wrap }; AutomationProperties.SetAutomationId(result, id); return result; }
    private static CheckBox Toggle(string id, string text) { var result = new CheckBox { Content = text, Margin = new Thickness(5) }; AutomationProperties.SetAutomationId(result, id); return result; }
    private static Button Button(string id, string text, Action action) { var button = new Button { Content = text, Margin = new Thickness(3), Padding = new Thickness(8, 4, 8, 4) }; AutomationProperties.SetAutomationId(button, id); button.Click += (_, _) => action(); return button; }
    private sealed class BindingFixture { public string Text => "Binding repaired"; }
}

public sealed class InvoiceRow(int number, string key, Action<InvoiceRow> began, Action<InvoiceRow> committed, Action<InvoiceRow> cancelled) : IGridRowIdentity, IEditableObject, INotifyPropertyChanged
{
    private int quantity = 1, originalQuantity;
    private string note = "Draft", originalNote = "";
    private bool editing;
    public int Number { get; } = number;
    public string TestyRowKey { get; } = key;
    public int Quantity { get => quantity; set { quantity = value; PropertyChanged?.Invoke(this, new(nameof(Quantity))); } }
    public string Note { get => note; set { note = value; PropertyChanged?.Invoke(this, new(nameof(Note))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void BeginEdit() { if (editing) return; originalQuantity = quantity; originalNote = note; editing = true; began(this); }
    public void EndEdit() { if (!editing) return; editing = false; committed(this); }
    public void CancelEdit() { if (!editing) return; editing = false; Quantity = originalQuantity; Note = originalNote; cancelled(this); }
    public override string ToString() => TestyRowKey;
}
public sealed class InvoiceQuantityRule : ValidationRule
{
    public override ValidationResult Validate(object value, CultureInfo cultureInfo) => int.TryParse(value?.ToString(), NumberStyles.Integer, cultureInfo, out int quantity) && quantity is >= 1 and <= 999
        ? ValidationResult.ValidResult : new ValidationResult(false, "Quantity must be a whole number from 1 to 999.");
}
public sealed class GridCellIdConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => values.Length == 2 && values[0] is string key && values[1] is DataGridColumn column ? $"EditCell-{key}-{GridAutomation.GetColumnKey(column)}" : "";
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
