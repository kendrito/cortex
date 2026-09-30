using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Testy.Core;

namespace Testy.Studio;

/// <summary>
/// The Steps view: each row of the StepsEditor DataGrid (DataItem rows) is a sentence ("Click Reset", "Type “Ada” into Customer name",
/// "Check Status message says …"); the docked step editor under it edits the selected row. Edits change the TestStep in place and mark the
/// test dirty; saving stays synchronous (Save, switching tests, closing), exactly as before.
/// </summary>
public partial class MainWindow
{
    private bool _loadingStepEditor;

    /// <summary>Adds a step after the selected one (or at the end), selects it and shows it in the step editor.</summary>
    private void InsertStep(TestStep step)
    {
        var index = StepsGrid.SelectedIndex >= 0 ? StepsGrid.SelectedIndex + 1 : _steps.Count;
        var row = new StepRow(step, index + 1, _snapshot);
        _steps.Insert(index, row);
        Renumber();
        StepsGrid.SelectedItem = row; StepsGrid.ScrollIntoView(row);
        MarkDirty();
    }
    private void AddStep_Click(object sender, RoutedEventArgs e) => AddStep(StepAction.Click);
    private void AddCheck_Click(object sender, RoutedEventArgs e) => AddStep(StepAction.AssertText);
    private void AddStep(StepAction action)
    {
        if (_busy) return;
        if (_selected == null) { ReportError("Create or select a test first.", "Tests"); return; }
        ShowEditorView(EditorView.Steps);
        InsertStep(Step("New step", action));
        StepControlPicker.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => StepControlPicker.Focus());
    }
    private void RemoveStep_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || StepsGrid.SelectedItem is not StepRow row) return;
        var index = _steps.IndexOf(row);
        _steps.Remove(row); Renumber();
        if (_steps.Count > 0) StepsGrid.SelectedIndex = Math.Min(index, _steps.Count - 1); else LoadStepEditor(null);
        MarkDirty();
    }
    private void StepOptions_Click(object sender, RoutedEventArgs e) => GuardSync(() =>
    {
        if (StepsGrid.SelectedItem is not StepRow row) throw new InvalidOperationException("Select a step to change its options.");
        var dialog = new StepOptionsWindow(row.Step) { Owner = this };
        var stepId = row.Step.Id;
        if (ShowModal(() => dialog.ShowDialog()) != true) return;
        // The rows may have been rebuilt while the dialog was open (a reload of the test): edit the row that holds the step now.
        var target = _steps.FirstOrDefault(r => r.Step.Id == stepId) ?? throw new InvalidOperationException("The step is no longer part of this test, so the options were not applied.");
        target.Replace(dialog.Step); StepsGrid.SelectedItem = target; LoadStepEditor(target); MarkDirty();
    });
    private void MoveStepUp_Click(object sender, RoutedEventArgs e) => MoveStep(-1);
    private void MoveStepDown_Click(object sender, RoutedEventArgs e) => MoveStep(1);
    private void MoveStep(int delta)
    {
        if (_busy) return;
        int index = StepsGrid.SelectedIndex, next = index + delta;
        if (index >= 0 && next >= 0 && next < _steps.Count) { _steps.Move(index, next); Renumber(); StepsGrid.SelectedIndex = next; MarkDirty(); }
    }
    private void Renumber() { for (var i = 0; i < _steps.Count; i++) _steps[i].Number = i + 1; if (StepsGrid.SelectedItem is StepRow row) StepEditorTitle.Text = $"Step {row.Number}"; }
    /// <summary>A new scan of the app gives controls their friendly names in the step sentences.</summary>
    private void RefreshStepNames()
    {
        foreach (var row in _steps) row.Refresh(_snapshot);
        if (StepsGrid.SelectedItem is StepRow selected) LoadStepEditor(selected);
    }

    private void StepsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, StepsGrid)) LoadStepEditor(StepsGrid.SelectedItem as StepRow);
    }
    /// <summary>Fills the docked editor from the selected row (or shows its empty state).</summary>
    private void LoadStepEditor(StepRow? row)
    {
        if (StepEditorCard is null) return;
        _loadingStepEditor = true;
        try
        {
            var technical = StudioPreferences.Current.ShowTechnicalDetails;
            StepEditorFields.Visibility = row != null ? Visibility.Visible : Visibility.Collapsed;
            StepEditorTechnical.Visibility = row != null && technical ? Visibility.Visible : Visibility.Collapsed;
            StepEditorEmpty.Visibility = row != null ? Visibility.Collapsed : Visibility.Visible;
            StepEditorEmpty.Text = _selected == null ? "Select a test to see its steps." : _steps.Count == 0 ? "This test has no steps yet. Use Add step or Add check." : "Select a step to change it.";
            if (row == null) { StepEditorTitle.Text = "Step"; StepControlPicker.ItemsSource = null; return; }
            StepEditorTitle.Text = $"Step {row.Number}";
            StepActionPicker.SelectedValue = row.Action;
            var usesControl = TestValidator.RequiresSelector(row.Action);
            StepControlLabel.Visibility = StepControlPicker.Visibility = usesControl ? Visibility.Visible : Visibility.Hidden;
            StepControlPicker.ItemsSource = usesControl ? StepText.ControlChoices(_snapshot, row.Selector) : null;
            StepControlPicker.SelectedValue = usesControl && row.Selector.Length > 0 ? row.Selector : null;
            StepEditorHint.Visibility = usesControl && _snapshot == null ? Visibility.Visible : Visibility.Collapsed;
            var label = StepText.ValueLabel(row.Action);
            StepValueLabel.Text = label ?? "";
            StepValueLabel.Visibility = StepValueBox.Visibility = label != null ? Visibility.Visible : Visibility.Hidden;
            StepValueBox.Text = row.Value;
            StepValueBox.FontFamily = (System.Windows.Media.FontFamily)FindResource(label == "Details (JSON)" ? "MonoFont" : "UIFont");
            StepSelectorBox.Text = row.Selector;
            StepTimeout.Text = row.Step.TimeoutMs.ToString(CultureInfo.InvariantCulture);
        }
        finally { _loadingStepEditor = false; }
    }
    private StepRow? EditedRow => _loadingStepEditor || _loading || _busy ? null : StepsGrid.SelectedItem as StepRow;
    private void StepActionPicker_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (EditedRow is not { } row || StepActionPicker.SelectedValue is not StepAction action || row.Action == action) return;
        row.Action = action; MarkDirty(); LoadStepEditor(row);
    }
    private void StepControlPicker_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (EditedRow is not { } row || StepControlPicker.SelectedValue is not string selector || row.Selector == selector) return;
        row.Selector = selector; MarkDirty();
        _loadingStepEditor = true; StepSelectorBox.Text = selector; _loadingStepEditor = false;
    }
    private void StepValueBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (EditedRow is not { } row || row.Value == StepValueBox.Text) return;
        row.Value = StepValueBox.Text; MarkDirty();
    }
    private void StepSelectorBox_Changed(object sender, TextChangedEventArgs e)
    {
        var selector = StepSelectorBox.Text.Trim();
        if (EditedRow is not { } row || row.Selector == selector) return;
        row.Selector = selector; MarkDirty();
        _loadingStepEditor = true;
        StepControlPicker.ItemsSource = StepText.ControlChoices(_snapshot, selector);
        StepControlPicker.SelectedValue = selector.Length > 0 ? selector : null;
        _loadingStepEditor = false;
    }
    private void StepTimeout_LostFocus(object sender, RoutedEventArgs e)
    {
        if (StepsGrid.SelectedItem is not StepRow row) return;
        if (int.TryParse(StepTimeout.Text, out var timeout) && timeout is >= 100 and <= 60000) { if (row.Step.TimeoutMs != timeout) { row.Step.TimeoutMs = timeout; MarkDirty(); } }
        else { StepTimeout.Text = row.Step.TimeoutMs.ToString(CultureInfo.InvariantCulture); SetStatus("The wait time must be between 100 and 60000 ms.", ActivityLevel.Warning, "Tests"); }
    }
}
