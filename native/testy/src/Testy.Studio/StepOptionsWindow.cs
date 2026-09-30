using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Testy.Core;

namespace Testy.Studio;

/// <summary>Modal step options dialog (Fluent window with Mica). Apply validates a clone of the step and closes with DialogResult=true.</summary>
internal sealed class StepOptionsWindow : Window
{
    internal TestStep Step { get; }
    internal StepOptionsWindow(TestStep original)
    {
        Step = TestyJson.Clone(original); Title = "Testy — Step options"; Width = 880; Height = 680; MinWidth = 760; MinHeight = 520;
        // The app icon (all sizes); Window picks the best frame for the title bar and the taskbar.
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/Testy.ico", UriKind.Absolute));
        WindowStartupLocation = WindowStartupLocation.CenterOwner; FontFamily = (FontFamily)FindResource("UIFont"); FontSize = 14; UseLayoutRounding = true;
        var root = new Grid(); root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Content = root;
        var body = new StackPanel { Margin = new Thickness(28, 22, 28, 20) };
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(new TextBlock { Text = Step.Title, Style = Res("PageTitle"), FontSize = 24, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None });
        body.Children.Add(new TextBlock { Text = $"{Step.Action} · {Step.Selector}", Style = Res("HelpText"), FontFamily = (FontFamily)FindResource("MonoFont"), Margin = new Thickness(0, 4, 0, 0) });
        var wait = Card(body, "Wait time", "How long Testy waits for the control before the step fails.");
        var timeout = Field(wait, "Wait up to (milliseconds, 100 to 60000)", Step.TimeoutMs.ToString(), "StepOptionsTimeout"); timeout.Width = 180; timeout.HorizontalAlignment = HorizontalAlignment.Left;
        TextBox? row = null, column = null, value = null;
        if (Step.Action is StepAction.GridEditCell or StepAction.GridCommitRow or StepAction.GridCancelRow)
        {
            string r = "", c = "", t = "";
            try { using var parsed = JsonDocument.Parse(Step.Value); if (parsed.RootElement.TryGetProperty("rowKey", out var p)) r = p.GetString() ?? ""; if (parsed.RootElement.TryGetProperty("columnKey", out p)) c = p.GetString() ?? ""; if (parsed.RootElement.TryGetProperty("text", out p)) t = p.GetString() ?? ""; } catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
            var cell = Card(body, "Grid cell", Step.Action == StepAction.GridEditCell ? "Changes stay in the current row until a separate save or cancel step. Template editors need an explicit WPF helper EditorId." : null);
            row = Field(cell, "Exact row key", r, "GridRowKey");
            if (Step.Action == StepAction.GridEditCell) { column = Field(cell, "Exact column key", c, "GridColumnKey"); value = Field(cell, "New value: text, the exact option label, or true / false / null", t, "GridEditorValue"); }
        }
        var backups = Card(body, "Backup ways to find this control", "If the saved control is missing, the AI may use one of these only when the control it sees matches the exact identity below. Checks keep their expected values. Up to four; leave empty for no recovery.");
        var alternatives = new ObservableCollection<SelectorAlternative>(Step.SelectorAlternatives);
        var grid = new DataGrid { ItemsSource = alternatives, AutoGenerateColumns = false, CanUserAddRows = false, Height = 168, Margin = new Thickness(0, 10, 0, 0) };
        AutomationProperties.SetAutomationId(grid, "SelectorAlternatives");
        foreach (var (header, name) in new[] { ("Name", "Id"), ("Control ID", "Selector"), ("Kind", "ExpectedControlType"), ("Automation ID", "ExpectedAutomationId"), ("Accessible name", "ExpectedName") })
            grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(name) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var gridCard = new Border { Style = Res("Card"), Child = grid, Margin = new Thickness(0, 8, 0, 0) }; gridCard.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorSecondaryBrush");
        backups.Children.Add(gridCard);
        var listActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-4, 8, 0, 0) }; backups.Children.Add(listActions);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) }; error.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush"); body.Children.Add(error);
        AddButton(listActions, "Add", "AddAlternative", "SubtleButton", "", () => { if (alternatives.Count >= 4) throw new InvalidOperationException("A step can have at most four backup ways."); alternatives.Add(new() { Id = "alternative-" + Guid.NewGuid().ToString("N")[..8] }); });
        AddButton(listActions, "Remove selected", "RemoveAlternative", "SubtleButton", "", () => { if (grid.SelectedItem is SelectorAlternative a) alternatives.Remove(a); });
        // Command area, as in a Fluent dialog: primary action on the right.
        var commands = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(24, 16, 24, 16) }; Grid.SetRow(commands, 1); root.Children.Add(commands);
        commands.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorSecondaryBrush"); commands.SetResourceReference(Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; commands.Child = buttons;
        var apply = AddButton(buttons, "Apply", "ApplyStepOptions", "AccentIconButton", null, () =>
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
            if (!int.TryParse(timeout.Text, out var milliseconds) || milliseconds is < 100 or > 60000) throw new InvalidOperationException("The wait time must be 100 to 60000 ms.");
            Step.TimeoutMs = milliseconds; Step.SelectorAlternatives = alternatives.ToList();
            if (row != null) Step.Value = column != null ? JsonSerializer.Serialize(new { rowKey = row.Text, columnKey = column.Text, text = value!.Text }) : JsonSerializer.Serialize(new { rowKey = row.Text });
            TestValidator.Validate(new TestCase { Name = "Step validation", Steps = [Step] }); DialogResult = true;
        });
        apply.MinWidth = 120; apply.IsDefault = true;
        var cancel = AddButton(buttons, "Cancel", "CancelStepOptions", "IconButton", null, () => DialogResult = false); cancel.MinWidth = 120; cancel.IsCancel = true; cancel.Margin = new Thickness(8, 0, 0, 0);

        Style Res(string key) => (Style)FindResource(key);
        StackPanel Card(Panel parent, string title, string? description)
        {
            var inner = new StackPanel(); inner.Children.Add(new TextBlock { Text = title, Style = Res("PaneTitle") });
            if (description != null) inner.Children.Add(new TextBlock { Text = description, Style = Res("HelpText"), Margin = new Thickness(0, 2, 0, 0) });
            parent.Children.Add(new Border { Style = Res("Card"), Padding = new Thickness(20, 16, 20, 18), Margin = new Thickness(0, 16, 0, 0), Child = inner }); return inner;
        }
        TextBox Field(Panel parent, string label, string text, string id)
        {
            parent.Children.Add(new TextBlock { Text = label, Style = Res("FieldLabel") }); var box = new TextBox { Text = text };
            AutomationProperties.SetAutomationId(box, id); AutomationProperties.SetName(box, label); parent.Children.Add(box); return box;
        }
        Button AddButton(Panel parent, string title, string id, string style, string? glyph, Action action)
        {
            var button = new Button { Content = title, Tag = glyph, Style = Res(style), Margin = new Thickness(0, 0, 4, 0) }; AutomationProperties.SetAutomationId(button, id);
            button.Click += (_, _) => { try { error.Text = ""; action(); } catch (Exception ex) { error.Text = ex.Message; } }; parent.Children.Add(button); return button;
        }
    }
}
