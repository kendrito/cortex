using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Testy.WpfProbe;

namespace Testy.WpfLab;

/// <summary>Owned standard-editor fixture. Persistence changes only in IEditableObject.EndEdit.</summary>
public sealed class GridEditorsWindow : Window
{
    private readonly DataGrid grid = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, EnableRowVirtualization = true, MinHeight = 300 };
    private readonly ObservableCollection<EditorRow> rows = [];
    private readonly TextBlock persisted = Text("EditorPersisted", ""), counters = Text("EditorCounters", ""), status = Text("EditorStatus", "Ready");
    private readonly CheckBox duplicates = Check("DuplicateChoices", "Duplicate combo labels"), ambiguous = Check("AmbiguousTemplate", "Duplicate template editor identity"), veto = Check("RejectEditorCommit", "Reject commit");
    private int begins, commits, cancels;
    private bool resetting;
    private DataGridComboBoxColumn category = null!;
    private DataGridTemplateColumn template = null!;
    public GridEditorsWindow()
    {
        Title = "Testy standard WPF grid editors"; Width = 1120; Height = 660; WindowStartupLocation = WindowStartupLocation.CenterScreen; Background = Brushes.WhiteSmoke;
        AutomationProperties.SetAutomationId(this, "GridEditorsWindow");
        var panel = new DockPanel { Margin = new Thickness(20) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "Standard checkbox, combo and template editors", FontSize = 24, FontWeight = FontWeights.SemiBold });
        var toolbar = new WrapPanel { Margin = new Thickness(0, 12, 0, 12) }; top.Children.Add(toolbar);
        AddButton(toolbar, "ResetEditors", "Reset", Reset);
        AddButton(toolbar, "SortEditors", "Reverse rows", () => { var view = CollectionViewSource.GetDefaultView(rows); view.SortDescriptions.Clear(); view.SortDescriptions.Add(new(nameof(EditorRow.TestyRowKey), ListSortDirection.Descending)); });
        AddButton(toolbar, "ReorderEditors", "Reorder columns", () => category.DisplayIndex = grid.Columns.Count - 1);
        toolbar.Children.Add(duplicates); toolbar.Children.Add(ambiguous); toolbar.Children.Add(veto);
        duplicates.Checked += (_, _) => category.ItemsSource = new[] { "Alpha", "Beta", "Beta" };
        duplicates.Unchecked += (_, _) => category.ItemsSource = new[] { "Alpha", "Beta" };
        ambiguous.Checked += (_, _) => template.CellEditingTemplate = EditorTemplate("TemplateNoteEditor", typeof(TextBox), TextBox.TextProperty, nameof(EditorRow.TemplateNote), true);
        ambiguous.Unchecked += (_, _) => template.CellEditingTemplate = EditorTemplate("TemplateNoteEditor", typeof(TextBox), TextBox.TextProperty, nameof(EditorRow.TemplateNote), false);
        foreach (var label in new[] { status, persisted, counters }) { label.Margin = new Thickness(0, 5, 0, 5); top.Children.Add(label); }
        GridAutomation.SetEnable(grid, true); AutomationProperties.SetAutomationId(grid, "EditorGrid");
        grid.ItemsSource = rows;
        grid.Columns.Add(Key(new DataGridTextColumn { Header = "Business key", Binding = new Binding(nameof(EditorRow.TestyRowKey)), IsReadOnly = true, Width = 100 }, "Key"));
        grid.Columns.Add(Key(new DataGridCheckBoxColumn { Header = "Enabled", Binding = Bound(nameof(EditorRow.Enabled)), Width = 85, EditingElementStyle = EditorStyle(typeof(CheckBox), "EnabledEditor") }, "Enabled"));
        category = new() { Header = "Category", ItemsSource = new[] { "Alpha", "Beta" }, SelectedItemBinding = Bound(nameof(EditorRow.Category)), Width = 125, EditingElementStyle = EditorStyle(typeof(ComboBox), "CategoryEditor") };
        grid.Columns.Add(Key(category, "Category"));
        template = Templated("TemplateNote", "TemplateNoteEditor", typeof(TextBox), TextBox.TextProperty, nameof(EditorRow.TemplateNote)); grid.Columns.Add(template);
        grid.Columns.Add(Templated("TemplateFlag", "TemplateFlagEditor", typeof(CheckBox), ToggleButton.IsCheckedProperty, nameof(EditorRow.TemplateFlag)));
        grid.Columns.Add(Templated("TemplateChoice", "TemplateChoiceEditor", typeof(ComboBox), Selector.SelectedItemProperty, nameof(EditorRow.TemplateChoice)));
        var unsupported = new DataGridTemplateColumn { Header = "Not opted in", Width = 130, CellTemplate = Display(nameof(EditorRow.TemplateNote)), CellEditingTemplate = EditorTemplate("UnapprovedEditor", typeof(TextBox), TextBox.TextProperty, nameof(EditorRow.TemplateNote), false) };
        grid.Columns.Add(Key(unsupported, "Unsupported"));
        grid.RowEditEnding += (_, e) => { if (!resetting && veto.IsChecked == true && e.EditAction == DataGridEditAction.Commit) { e.Cancel = true; status.Text = "Commit rejected"; } };
        panel.Children.Add(grid); Content = panel; Loaded += (_, _) => Activate(); Reset();
    }
    private static Binding Bound(string path) => new(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, NotifyOnValidationError = true };
    private static Style EditorStyle(Type type, string id) { var style = new Style(type); style.Setters.Add(new Setter(AutomationProperties.AutomationIdProperty, id)); return style; }
    private static T Key<T>(T column, string key) where T : DataGridColumn { GridAutomation.SetColumnKey(column, key); return column; }
    private static DataGridTemplateColumn Templated(string key, string id, Type type, DependencyProperty property, string path)
    {
        var column = Key(new DataGridTemplateColumn { Header = key, Width = 155, CellTemplate = Display(path), CellEditingTemplate = EditorTemplate(id, type, property, path, false) }, key);
        GridAutomation.SetEditorId(column, id); return column;
    }
    private static DataTemplate Display(string path) { var factory = new FrameworkElementFactory(typeof(TextBlock)); factory.SetBinding(TextBlock.TextProperty, new Binding(path)); return new DataTemplate { VisualTree = factory }; }
    private static DataTemplate EditorTemplate(string id, Type type, DependencyProperty property, string path, bool duplicate)
    {
        var parent = new FrameworkElementFactory(typeof(StackPanel));
        for (int i = 0; i < (duplicate ? 2 : 1); i++)
        {
            var child = new FrameworkElementFactory(type); child.SetValue(AutomationProperties.AutomationIdProperty, id); child.SetBinding(property, Bound(path));
            if (type == typeof(ComboBox)) child.SetValue(ItemsControl.ItemsSourceProperty, new[] { "Alpha", "Beta" });
            parent.AppendChild(child);
        }
        return new DataTemplate { VisualTree = parent };
    }
    private void Reset()
    {
        resetting = true; grid.CancelEdit(DataGridEditingUnit.Cell); grid.CancelEdit(DataGridEditingUnit.Row);
        duplicates.IsChecked = false; ambiguous.IsChecked = false; veto.IsChecked = false;
        CollectionViewSource.GetDefaultView(rows).SortDescriptions.Clear(); rows.Clear();
        for (int i = 1; i <= 100; i++) rows.Add(new EditorRow($"ROW-{i}", phase =>
        {
            if (resetting) return;
            if (phase == "begin") begins++; else if (phase == "cancel") cancels++; else commits++;
            counters.Text = $"begin={begins};commit={commits};cancel={cancels}";
            status.Text = phase;
        }, value => persisted.Text = value));
        begins = commits = cancels = 0; counters.Text = "begin=0;commit=0;cancel=0"; persisted.Text = "ROW-1|false|Alpha|seed|false|Alpha"; status.Text = "Ready";
        category.DisplayIndex = 2; grid.ScrollIntoView(rows[0]); resetting = false;
    }
    private static TextBlock Text(string id, string text) { var c = new TextBlock { Text = text }; AutomationProperties.SetAutomationId(c, id); return c; }
    private static CheckBox Check(string id, string text) { var c = new CheckBox { Content = text, Margin = new Thickness(8, 4, 8, 4) }; AutomationProperties.SetAutomationId(c, id); return c; }
    private static void AddButton(Panel panel, string id, string text, Action action) { var b = new Button { Content = text, Margin = new Thickness(3), Padding = new Thickness(8, 4, 8, 4) }; AutomationProperties.SetAutomationId(b, id); b.Click += (_, _) => action(); panel.Children.Add(b); }
}

public sealed class EditorRow(string key, Action<string> phase, Action<string> persist) : IGridRowIdentity, IEditableObject, INotifyPropertyChanged
{
    public string TestyRowKey { get; } = key;
    private bool enabled, flag;
    private string category = "Alpha", note = "seed", choice = "Alpha";
    private (bool Enabled, string Category, string Note, bool Flag, string Choice)? backup;
    public bool Enabled { get => enabled; set { enabled = value; Changed(nameof(Enabled)); } }
    public string Category { get => category; set { category = value; Changed(nameof(Category)); } }
    public string TemplateNote { get => note; set { note = value; Changed(nameof(TemplateNote)); } }
    public bool TemplateFlag { get => flag; set { flag = value; Changed(nameof(TemplateFlag)); } }
    public string TemplateChoice { get => choice; set { choice = value; Changed(nameof(TemplateChoice)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
    public void BeginEdit() { if (backup is not null) return; backup = (enabled, category, note, flag, choice); phase("begin"); }
    public void EndEdit() { if (backup is null) return; backup = null; persist($"{TestyRowKey}|{enabled.ToString().ToLowerInvariant()}|{category}|{note}|{flag.ToString().ToLowerInvariant()}|{choice}"); phase("commit"); }
    public void CancelEdit() { if (backup is not { } old) return; backup = null; Enabled = old.Enabled; Category = old.Category; TemplateNote = old.Note; TemplateFlag = old.Flag; TemplateChoice = old.Choice; phase("cancel"); }
}
