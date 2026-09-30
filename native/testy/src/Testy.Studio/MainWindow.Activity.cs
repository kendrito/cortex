using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace Testy.Studio;

/// <summary>
/// The activity log and its in-window pane (ActivityButton in the app bar; Activity under a run's steps opens it filtered to that run).
/// Every status and assistant message, error, save, run start and finish and Automate result goes through <see cref="Log"/>. The pane's list
/// is bound only while it is open, so its entries are in the UI Automation tree only then.
/// </summary>
public partial class MainWindow
{
    private readonly ActivityLog _activity = new();
    private ListCollectionView? _activityView;
    private ActivityScope _activityScope;
    private bool _switchingActivity;

    private enum ActivityScope { All, ThisRun, Errors }

    private void Log(ActivityLevel level, string source, string message, string? runId = null, string? detail = null)
    {
        var entry = _activity.Add(level, source, message, runId ?? _activeRunId, detail);
        if (entry == null) return;
        if (entry.RunId == null && _pendingRunEntries != null) _pendingRunEntries.Add(entry);
        if (_activityView != null && ActivityList.Items.Count > 0 && _activityView.Contains(entry)) ActivityList.ScrollIntoView(entry);
    }
    private void ActivityButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActivityPanel.Visibility == Visibility.Visible) CloseActivity(); else OpenActivity(ActivityScope.All);
    }
    private void OpenActivity(ActivityScope scope)
    {
        CloseFlyouts();
        _activityScope = scope;
        _activityView = new ListCollectionView(_activity.Entries) { Filter = ShowsEntry };
        _switchingActivity = true;
        try { ActivityFilter.SelectedIndex = (int)scope; }
        finally { _switchingActivity = false; }
        ActivityList.ItemsSource = _activityView;
        UpdateActivityTexts();
        ActivityPanel.Visibility = Visibility.Visible;
        ScrollActivityToEnd();
        ActivityPanel.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (ActivityPanel.Visibility == Visibility.Visible) ActivityFilter.Focus(); });
    }
    private void CloseActivity()
    {
        if (ActivityPanel.Visibility != Visibility.Visible) return;
        ActivityPanel.Visibility = Visibility.Collapsed;
        ActivityList.ItemsSource = null; _activityView = null;
    }
    private void CloseActivity_Click(object sender, RoutedEventArgs e) { CloseActivity(); ActivityButton.Focus(); }
    private void ActivityPanel_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseActivity(); ActivityButton.Focus(); e.Handled = true;
    }
    /// <summary>"This run" is the run shown in Last run (or the last one in this session); "Errors" are error entries.</summary>
    private string? ActivityRunId => _shownRun?.Id ?? _lastRun?.Id;
    private bool ShowsEntry(object item) => item is ActivityEntry entry && _activityScope switch
    {
        ActivityScope.ThisRun => ActivityRunId is { } id && entry.RunId == id,
        ActivityScope.Errors => entry.Level == ActivityLevel.Error,
        _ => true
    };
    private void ActivityFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switchingActivity || _activityView == null || !ReferenceEquals(e.OriginalSource, ActivityFilter)) return;
        _activityScope = (ActivityScope)Math.Max(0, ActivityFilter.SelectedIndex);
        _activityView.Refresh(); UpdateActivityTexts(); ScrollActivityToEnd();
    }
    private void UpdateActivityTexts()
    {
        var run = _shownRun ?? _lastRun;
        ActivitySubtitle.Text = _activityScope switch
        {
            ActivityScope.ThisRun => run != null ? $"The run of “{run.TestName}” from {FriendlyTimeConverter.Format(run.StartedAt)}." : "No run is open in Last run.",
            ActivityScope.Errors => "Errors in this session.",
            _ => "Messages, saves, runs and errors in this session."
        };
        ActivityEmptyText.Text = _activityScope switch
        {
            ActivityScope.ThisRun => "No activity for this run in this session",
            ActivityScope.Errors => "No errors",
            _ => "Nothing here yet"
        };
        ActivityLogPath.Text = _activity.CurrentFile;
    }
    private void OpenLogFolder_Click(object sender, RoutedEventArgs e) => TryShell(() =>
    {
        System.IO.Directory.CreateDirectory(_activity.LogDirectory);
        OpenPath(_activity.LogDirectory);
    });
    private void ScrollActivityToEnd()
    {
        if (_activityView is { Count: > 0 } view) ActivityList.ScrollIntoView(view.GetItemAt(view.Count - 1));
    }
    private void CopyActivity_Click(object sender, RoutedEventArgs e)
    {
        var entries = _activityView?.Cast<ActivityEntry>().ToList() ?? _activity.Entries.ToList();
        try { Clipboard.SetText(entries.Count == 0 ? "No activity." : ActivityLog.Text(entries)); SetStatus(entries.Count == 1 ? "Copied 1 activity entry." : $"Copied {entries.Count} activity entries.", ActivityLevel.Info, "App"); }
        catch (Exception ex) when (ex is COMException or ExternalException) { SetStatus("The activity could not be copied: " + ex.Message, ActivityLevel.Error); }
    }
}
