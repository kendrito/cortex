using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Testy.Core;

namespace Testy.Studio;

/// <summary>
/// The Last run view: the result InfoBar, the run trace (RunTraceView), the steps with composed runner messages, and the screenshot of the
/// selected step with the control Testy acted on outlined. The trace, the list and the screenshot stay in sync both ways. A live run is shown
/// while it runs: every progress report updates the trace and the rows in place, and the current step reads "running…".
/// </summary>
public partial class MainWindow
{
    private readonly ObservableCollection<RunStepRow> _runRows = [];
    private RunResult? _shownRun;
    /// <summary>The test whose run is shown; a different selected test loads its own latest run when Last run opens.</summary>
    private string? _shownRunFor;
    private bool _shownRunLive;
    /// <summary>The shown run was chosen on purpose (opened from Results, or just produced by Studio): an outside run of the same test does not replace it.</summary>
    private bool _shownRunPinned;
    private int _runStepIndex = -1;
    private bool _syncingRunSelection;
    /// <summary>During a live run the newest step stays selected until the person picks one.</summary>
    private bool _followLive;
    private TestCase? _liveTest;
    private StepTargetRegion? _shotRegion;
    private StepAction _shotAction;
    private string? _shotPath;
    /// <summary>The run in progress (known from its first progress report); activity entries logged meanwhile are tagged with it.</summary>
    private string? _activeRunId;
    private List<ActivityEntry>? _pendingRunEntries;

    private enum RunStepSource { Code, Trace, List }

    private void ForgetShownRun()
    {
        if (_shownRunLive && _busy) return;
        _shownRun = null; _shownRunFor = null; _shownRunPinned = false;
    }
    /// <summary>Last run shows the selected test's latest stored run (loaded when the view opens), unless a run from Results or a live run is shown.</summary>
    private void EnsureShownRun()
    {
        if (_shownRunLive && _busy) return;
        if (_selected == null) { ShowRun(null); return; }
        if (_shownRun != null && _shownRunFor == _selected.Id) { UpdateResultBar(); return; }
        var header = _runIndex.Where(r => r.TestId == _selected.Id).OrderByDescending(r => r.StartedAt).FirstOrDefault();
        ShowRun(header is null ? null : LoadRun(header.Id), _selected.Id);
    }
    private RunResult? LoadRun(string id)
    {
        try
        {
            var path = Path.Combine(_store.RootDirectory, "runs", id + ".json");
            return File.Exists(path) ? JsonSerializer.Deserialize<RunResult>(File.ReadAllText(path), TestyJson.Options) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { ReportError("This run could not be read: " + ex.Message, "Run"); return null; }
    }

    /// <summary>Shows a run (or the empty state) in Last run and selects its first problem, else its last step.</summary>
    private void ShowRun(RunResult? run, string? forTestId = null, bool live = false, bool pinned = false)
    {
        _shownRun = run; _shownRunFor = forTestId ?? run?.TestId; _shownRunLive = live; _shownRunPinned = pinned && run != null;
        _syncingRunSelection = true;
        try { RunTraceView.Run = run; SyncRunRows(run); }
        finally { _syncingRunSelection = false; }
        TraceCard.Visibility = RunColumns.Visibility = run != null ? Visibility.Visible : Visibility.Collapsed;
        LastRunEmpty.Visibility = run != null ? Visibility.Collapsed : Visibility.Visible;
        if (_view == EditorView.LastRun) RunVerbText.Text = run != null ? "Run again" : "Run";
        UpdateResultBar(); UpdateAiNotes();
        SelectRunStep(run == null ? -1 : live ? run.Steps.Count - 1 : DefaultStep(run));
    }
    private static int DefaultStep(RunResult run)
    {
        var problem = run.Steps.FindIndex(s => s.Status is RunStatus.Failed or RunStatus.Cancelled);
        return problem >= 0 ? problem : run.Steps.Count - 1;
    }
    /// <summary>Updates the rows in place (no rebinding), so a live run doesn't rebuild the list on every report.</summary>
    private void SyncRunRows(RunResult? run)
    {
        var steps = run?.Steps ?? [];
        for (var i = 0; i < steps.Count; i++)
        {
            var names = NamesFor(steps, i);
            if (i < _runRows.Count) _runRows[i].Update(steps[i], i, names); else _runRows.Add(new RunStepRow(steps[i], i, names));
        }
        while (_runRows.Count > steps.Count) _runRows.RemoveAt(_runRows.Count - 1);
    }
    /// <summary>The snapshot that names a step's controls: its own, else the latest one before it (a running or skipped step has none), else the last scan.</summary>
    private UiSnapshot? NamesFor(IReadOnlyList<StepResult> steps, int position)
    {
        for (var i = Math.Min(position, steps.Count - 1); i >= 0; i--) if (steps[i].Snapshot is { } snapshot) return snapshot;
        return _snapshot;
    }

    // ── Live runs ──
    private void BeginLiveRun(TestCase test)
    {
        _liveTest = test; _followLive = true; _activeRunId = null; _pendingRunEntries = [];
        ShowRun(new RunResult { TestId = test.Id, TestName = test.Name, Status = RunStatus.Running }, test.Id, live: true);
        AiNotesExpander.IsExpanded = false;
        ShowEditorView(EditorView.LastRun);
    }
    private void OnRunProgress(RunProgress progress)
    {
        if (_activeRunId == null && !string.IsNullOrEmpty(progress.Run.Id))
        {
            _activeRunId = progress.Run.Id;
            foreach (var entry in _pendingRunEntries ?? []) entry.RunId = _activeRunId;
            _pendingRunEntries = null;
        }
        var (friendly, level) = FriendlyProgress(progress);
        if (friendly != null) SetStatus(friendly, level, "Run", detail: progress.Message);
        if (progress.Step?.Snapshot is { } snapshot)
        {
            _snapshot = snapshot; ElementsGrid.ItemsSource = snapshot.Elements;
            ContextStatus.Text = $"{snapshot.Elements.Count} controls · step {progress.Step.Index + 1}";
        }
        if (!_shownRunLive) return;
        _shownRun = progress.Run;
        _syncingRunSelection = true;
        try { RunTraceView.Run = progress.Run; SyncRunRows(progress.Run); }
        finally { _syncingRunSelection = false; }
        UpdateResultBar();
        if (_followLive) SelectRunStep(progress.Run.Steps.Count - 1); else UpdateScreenshotViewer();
    }
    /// <summary>
    /// The status line and activity text for a progress report, composed from the step and its result ("Step 2 of 5: Type “Ada” into
    /// Customer name", "Step 2 passed: Found Customer name field, typed 3 characters."). The runner's own message is kept as the entry's detail.
    /// Null for the runner's opening and closing lines: Run already reports "Running “…”" and "Run finished: …" itself.
    /// </summary>
    private (string? Text, ActivityLevel Level) FriendlyProgress(RunProgress progress)
    {
        var run = progress.Run;
        if (progress.Step is { } step)
        {
            var position = run.Steps.FindIndex(s => s.Index == step.Index);
            var number = (position >= 0 ? position : step.Index) + 1;
            var total = Math.Max(_liveTest?.Steps.Count ?? 0, run.Steps.Count);
            var aiGuided = RunTrace.From(run).IsAiGuided;
            // A running step has no snapshot yet: its controls are named from the latest one (the previous step's, or the scan).
            var sentence = StepText.Sentence(step.Step ?? new TestStep(), step.Snapshot ?? _snapshot).Plain;
            return step.Status switch
            {
                RunStatus.Running when progress.Message.StartsWith("Reviewing", StringComparison.Ordinal) => ($"Step {number}: the AI is commenting on what happened…", ActivityLevel.Info),
                RunStatus.Running => (aiGuided ? $"Step {number}: {sentence}" : $"Step {number} of {total}: {sentence}", ActivityLevel.Info),
                RunStatus.Passed => ($"Step {number} passed: {Period(StepText.RunnerMessage(step))}", ActivityLevel.Info),
                RunStatus.Failed => ($"Step {number} failed: {Period(StepText.RunnerMessage(step))}", ActivityLevel.Error),
                RunStatus.Cancelled => ($"Step {number} stopped.", ActivityLevel.Warning),
                _ => (progress.Message, ActivityLevel.Info)
            };
        }
        if (run.FinishedAt is not null || (run.Summary.Length > 0 && progress.Message == run.Summary)) return (null, ActivityLevel.Info);
        if (run.Steps.Count == 0 && progress.Message == $"Running {run.TestName}.") return (null, ActivityLevel.Info);
        return (progress.Message, ActivityLevel.Info);
        static string Period(string text) => text.EndsWith('.') || text.EndsWith('”') || text.EndsWith('…') ? text : text + ".";
    }
    private void EndLiveRun(RunResult run)
    {
        var picked = _followLive ? -1 : _runStepIndex;
        _liveTest = null;
        ShowRun(run, _selected?.Id, pinned: true);
        if (picked >= 0) SelectRunStep(picked);
    }
    /// <summary>The run stopped with an error before it returned a result: show what was recorded, or the test's previous run.</summary>
    private void AbandonLiveRun()
    {
        var partial = _activeRunId != null ? _shownRun : null;
        _shownRunLive = false; _liveTest = null;
        if (partial == null) { _shownRun = null; _shownRunFor = null; if (_view == EditorView.LastRun) EnsureShownRun(); return; }
        partial.Status = RunStatus.Cancelled; partial.FinishedAt ??= DateTimeOffset.UtcNow;
        foreach (var step in partial.Steps.Where(s => s.Status is RunStatus.Running or RunStatus.Pending)) step.Status = RunStatus.Cancelled;
        ShowRun(partial, _selected?.Id, pinned: true);
    }

    // ── Selection: trace ↔ list ↔ screenshot ──
    private void SelectRunStep(int index, RunStepSource source = RunStepSource.Code)
    {
        var count = _shownRun?.Steps.Count ?? 0;
        index = count == 0 ? -1 : Math.Clamp(index, -1, count - 1);
        _runStepIndex = index;
        _syncingRunSelection = true;
        try
        {
            if (source != RunStepSource.Trace) RunTraceView.SelectedStepIndex = index;
            if (source != RunStepSource.List) { RunStepList.SelectedIndex = index; if (index >= 0 && index < RunStepList.Items.Count) RunStepList.ScrollIntoView(RunStepList.Items[index]); }
        }
        finally { _syncingRunSelection = false; }
        UpdateScreenshotViewer();
    }
    private void RunTrace_StepSelected(object? sender, RunTraceStepSelectedEventArgs e)
    {
        if (_syncingRunSelection) return;
        if (e.IsUserInitiated) _followLive = false;
        SelectRunStep(e.StepIndex, RunStepSource.Trace);
    }
    private void RunStepList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingRunSelection || !ReferenceEquals(e.OriginalSource, RunStepList)) return;
        _followLive = false;
        SelectRunStep(RunStepList.SelectedIndex, RunStepSource.List);
    }
    private void PreviousStep_Click(object sender, RoutedEventArgs e) { _followLive = false; if (_runStepIndex > 0) SelectRunStep(_runStepIndex - 1); }
    private void NextStep_Click(object sender, RoutedEventArgs e) { _followLive = false; if (_shownRun != null && _runStepIndex < _shownRun.Steps.Count - 1) SelectRunStep(_runStepIndex + 1); }

    // ── Screenshot viewer ──
    /// <summary>"Step 4 · Screenshot after the click", "4 of 5", the screenshot, and the outline around the control Testy used (none when unknown).</summary>
    private void UpdateScreenshotViewer()
    {
        if (CaptionStep is null) return;
        var run = _shownRun; var index = _runStepIndex;
        var step = run != null && index >= 0 && index < run.Steps.Count ? run.Steps[index] : null;
        if (step == null)
        {
            CaptionStep.Text = ""; CaptionPhrase.Text = run == null || run.Steps.Count == 0 ? "" : "Select a step to see its screenshot";
            ScreenshotPosition.Text = run is { Steps.Count: > 0 } ? $"– of {run.Steps.Count}" : "";
            ShowShot(null, run == null ? "" : run.Steps.Count == 0 ? (_shownRunLive ? "Waiting for the first step…" : "No steps ran.") : "Select a step to see its screenshot.");
            _shotRegion = null; PositionOutline();
            ScreenshotNote.Text = ""; ShotNoteRow.Visibility = Visibility.Collapsed;
            RunStepDetails.Text = run == null ? "" : RunDetails(run);
            return;
        }
        CaptionStep.Text = "Step " + (index + 1).ToString(CultureInfo.CurrentCulture);
        CaptionPhrase.Text = " · " + StepText.ScreenshotPhrase(step);
        ScreenshotPosition.Text = string.Format(CultureInfo.CurrentCulture, "{0} of {1}", index + 1, run!.Steps.Count);
        var shown = ShowShot(ScreenshotFile(step, run), step.Status switch
        {
            RunStatus.Running => "Waiting for this step to finish…",
            RunStatus.Skipped or RunStatus.Pending => "This step didn't run.",
            _ => "No screenshot for this step."
        });
        _shotAction = step.Step?.Action ?? StepAction.Screenshot;
        _shotRegion = shown ? StepTargetLocator.Locate(step) : null;
        PositionOutline();
        var usesControl = TestValidator.RequiresSelector(_shotAction) || _shotAction == StepAction.CoordinateClick;
        ScreenshotNote.Text = _shotRegion != null ? StepText.OutlineNote(_shotAction) : shown && usesControl ? "Testy couldn't place this control on the screenshot." : "";
        ShotSwatch.Visibility = _shotRegion != null ? Visibility.Visible : Visibility.Collapsed;
        ShotNoteRow.Visibility = ScreenshotNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RunStepDetails.Text = StepDetails(step, run);
    }
    private string? ScreenshotFile(StepResult step, RunResult run)
    {
        var raw = !string.IsNullOrWhiteSpace(step.ScreenshotPath) ? step.ScreenshotPath : step.ScreenshotEvidence?.Path;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { if (!Path.IsPathRooted(raw) && !string.IsNullOrWhiteSpace(run.ArtifactDirectory)) raw = Path.Combine(run.ArtifactDirectory, raw); }
        catch (ArgumentException) { return null; }
        return DisplayPath(raw);
    }
    /// <summary>Loads the screenshot without locking the file. False (and the empty message) when there is none or it can't be read.</summary>
    private bool ShowShot(string? path, string emptyText)
    {
        if (path == null || path != _shotPath || ShotImage.Source == null)
        {
            _shotPath = null; ShotImage.Source = null;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                    ShotImage.Source = bitmap; _shotPath = path;
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or COMException) { }
            }
        }
        var shown = ShotImage.Source != null;
        ShotEmpty.Visibility = shown ? Visibility.Collapsed : Visibility.Visible;
        ShotEmptyText.Text = emptyText;
        return shown;
    }
    /// <summary>
    /// Places the accent outline over the control, in the image's displayed coordinates, and its "Clicked"/"Typed"/"Checked" label just outside
    /// the outline's top-left corner: above it; else below it, or beside it, when the image edge would clip it. The label stays inside the image
    /// and never covers the outlined control (with no room outside the outline it is hidden; the note under the screenshot still explains it).
    /// </summary>
    private void PositionOutline()
    {
        if (ShotOutline is null) return;
        if (_shotRegion is not { } region || ShotImage.Source is null || ShotImage.ActualWidth <= 0 || ShotImage.ActualHeight <= 0)
        {
            ShotOutline.Visibility = Visibility.Collapsed; ShotTag.Visibility = Visibility.Collapsed; return;
        }
        var rect = region.MapTo(new Rect(0, 0, ShotImage.ActualWidth, ShotImage.ActualHeight));
        if (rect.IsEmpty) { ShotOutline.Visibility = Visibility.Collapsed; ShotTag.Visibility = Visibility.Collapsed; return; }
        if (region.Kind == StepTargetKind.Point) rect = new Rect(rect.X - 7, rect.Y - 7, 14, 14); else rect.Inflate(3, 3);
        ShotOutline.CornerRadius = new CornerRadius(region.Kind == StepTargetKind.Point ? 7 : 3);
        Canvas.SetLeft(ShotOutline, rect.X); Canvas.SetTop(ShotOutline, rect.Y);
        ShotOutline.Width = Math.Max(6, rect.Width); ShotOutline.Height = Math.Max(6, rect.Height);
        ShotOutline.Visibility = Visibility.Visible;
        ShotTagText.Text = StepText.OutlineTag(_shotAction);
        ShotTag.Visibility = Visibility.Visible;
        ShotTag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var tag = ShotTag.DesiredSize;
        double width = ShotImage.ActualWidth, height = ShotImage.ActualHeight;
        const double gap = 2;
        var left = Math.Clamp(rect.Left, 0, Math.Max(0, width - tag.Width));
        var besideTop = Math.Clamp(rect.Top, 0, Math.Max(0, height - tag.Height));
        double top;
        if (rect.Top - gap - tag.Height >= 0) top = rect.Top - gap - tag.Height;
        else if (rect.Bottom + gap + tag.Height <= height) top = rect.Bottom + gap;
        else if (rect.Right + gap + tag.Width <= width) (left, top) = (rect.Right + gap, besideTop);
        else if (rect.Left - gap - tag.Width >= 0) (left, top) = (rect.Left - gap - tag.Width, besideTop);
        else { ShotTag.Visibility = Visibility.Collapsed; return; }
        Canvas.SetLeft(ShotTag, left); Canvas.SetTop(ShotTag, top);
    }
    private void ShotImage_SizeChanged(object sender, SizeChangedEventArgs e) => PositionOutline();

    // ── Result InfoBar, summary, report, AI notes ──
    private void UpdateResultBar()
    {
        if (RunSummaryWord is null) return;
        var run = _shownRun;
        string word, sentence, severity;
        if (run == null)
        {
            word = _selected == null ? "No test selected" : "Not run yet";
            sentence = "";
            severity = "Informational";
        }
        else
        {
            var live = _shownRunLive && run.FinishedAt is null;
            word = live ? "Running" : StepText.StatusWord(run.Status);
            sentence = ResultSentence(run, withWord: false);
            severity = live ? "Informational" : run.Status switch { RunStatus.Passed => "Success", RunStatus.Failed => "Error", RunStatus.Cancelled => "Warning", _ => "Informational" };
        }
        RunSummaryWord.Text = word; RunSummaryText.Text = sentence.Length > 0 ? "   " + sentence : "";
        RunSummaryIcon.Tag = severity;
        RunResultBar.Style = (Style)FindResource(severity switch { "Success" => "InfoBarSuccess", "Error" => "InfoBarError", "Warning" => "InfoBarWarning", _ => "InfoBar" });
        RunSummary.ToolTip = sentence.Length > 0 ? word + ". " + sentence : null;
        CopySummaryButton.Visibility = OpenRunReportButton.Visibility = run != null && !_shownRunLive ? Visibility.Visible : Visibility.Collapsed;
    }
    /// <summary>"5 of 5 steps in 8.2 s. Exact replay on this PC, just now." Plain sentences; the status word is shown separately.
    /// <paramref name="forLog"/> gives the time-free form for the activity log: "5 of 5 steps in 8.2 s (exact replay)."</summary>
    private string ResultSentence(RunResult run, bool withWord, bool fullTime = false, bool forLog = false)
    {
        var live = run.FinishedAt is null && run.Status is RunStatus.Running or RunStatus.Pending;
        var trace = RunTrace.From(run, live ? DateTimeOffset.UtcNow : null);
        var mode = trace.IsAiGuided ? "AI-guided" : "Exact replay";
        var total = run.Steps.Count;
        var passed = run.Steps.Count(s => s.Status == RunStatus.Passed);
        string text;
        if (live)
        {
            var planned = Math.Max(_liveTest?.Steps.Count ?? 0, total);
            text = total == 0 ? $"Starting. {mode} on this PC."
                : trace.IsAiGuided ? $"{StepText.Count(total, "step")} so far, {trace.TotalTimeText}. {mode} on this PC."
                : $"Step {total} of {planned}, {trace.TotalTimeText} so far. {mode} on this PC.";
        }
        else
        {
            var when = fullTime ? FriendlyTimeConverter.Format(run.FinishedAt ?? run.StartedAt, full: true) : StepText.Relative(run.FinishedAt ?? run.StartedAt, DateTimeOffset.Now);
            var failed = run.Steps.FindIndex(s => s.Status == RunStatus.Failed);
            var counts = run.Status switch
            {
                RunStatus.Passed => $"{passed} of {total} steps in {trace.TotalTimeText}",
                RunStatus.Failed when failed >= 0 => $"Step {failed + 1} of {total} didn't pass, after {trace.TotalTimeText}",
                RunStatus.Failed => $"{passed} of {total} steps passed in {trace.TotalTimeText}",
                RunStatus.Cancelled => $"Stopped after {passed} of {total} steps, {trace.TotalTimeText}",
                _ => $"{passed} of {total} steps"
            };
            text = forLog ? $"{counts} ({(trace.IsAiGuided ? "AI-guided" : "exact replay")})." : $"{counts}. {mode} on this PC, {when}.";
        }
        return withWord ? StepText.StatusWord(run.Status) + ". " + text : text;
    }
    private void CopySummary_Click(object sender, RoutedEventArgs e)
    {
        if (_shownRun is not { } run) return;
        try { Clipboard.SetText(PlainSummary(run)); SetStatus("Run summary copied.", ActivityLevel.Success, "Run"); }
        catch (Exception ex) when (ex is COMException or ExternalException) { ReportError("The summary could not be copied: " + ex.Message, "Run"); }
    }
    /// <summary>A plain-text summary: result, one line per step with its runner message, the AI notes and where the report is.</summary>
    private string PlainSummary(RunResult run)
    {
        var text = new StringBuilder();
        text.AppendLine($"{run.TestName}: {StepText.StatusWord(run.Status)}");
        text.AppendLine(ResultSentence(run, withWord: false, fullTime: true));
        text.AppendLine();
        for (var i = 0; i < run.Steps.Count; i++)
        {
            var step = run.Steps[i];
            var sentence = StepText.Sentence(step.Step ?? new TestStep(), NamesFor(run.Steps, i)).Plain;
            var duration = step.Status is RunStatus.Skipped or RunStatus.Pending ? "" : ", " + RunTrace.FormatDuration(step.DurationMs);
            text.AppendLine($"{i + 1}. {sentence} ({StepText.StatusWord(step.Status).ToLowerInvariant()}{duration}): {StepText.RunnerMessage(step)}");
        }
        if (!string.IsNullOrWhiteSpace(run.AiAnalysis)) text.AppendLine().AppendLine("AI notes:").AppendLine(run.AiAnalysis.Trim());
        var directory = DisplayPath(run.ArtifactDirectory);
        if (!string.IsNullOrEmpty(directory))
        {
            var html = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.html").FirstOrDefault() : null;
            text.AppendLine().Append("Report: ").AppendLine(html ?? directory);
        }
        return text.ToString().TrimEnd();
    }
    private void OpenRunReport_Click(object sender, RoutedEventArgs e) => TryShell(() => OpenRunReport(_shownRun ?? throw new InvalidOperationException("There is no run to open yet.")));
    private void OpenRunFolder_Click(object sender, RoutedEventArgs e) => TryShell(() =>
        OpenPath(DisplayPath((_shownRun ?? throw new InvalidOperationException("There is no run to open yet.")).ArtifactDirectory)));
    private void UpdateAiNotes()
    {
        var notes = _shownRun?.AiAnalysis?.Trim() ?? "";
        var count = notes.Length == 0 ? 0 : notes.Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries).Length;
        AiNotesSummary.Text = count == 0 ? "No notes yet" : StepText.Count(count, "note") + " from the assistant";
        AiNotesText.Text = notes.Length == 0 ? "Explain this run asks the AI what happened. Its answer is added here and saved with the run." : notes;
        AiNotesText.SetResourceReference(TextBlock.ForegroundProperty, notes.Length == 0 ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush");
    }
    private void RunActivity_Click(object sender, RoutedEventArgs e) => OpenActivity(ActivityScope.ThisRun);

    // ── Technical details (Show technical details on) ──
    private string RunDetails(RunResult run) =>
        $"Run {run.Id}{Environment.NewLine}Folder: {DisplayPath(run.ArtifactDirectory)}{Environment.NewLine}Summary: {run.Summary}";
    private string StepDetails(StepResult step, RunResult run)
    {
        var text = new StringBuilder();
        text.AppendLine($"Message: {StepText.OneLine(step.Message)}");
        text.AppendLine($"Action: {step.Step?.Action}  Selector: {step.Step?.Selector}");
        if (step.SelectorRecovery is { } recovery)
            text.AppendLine($"Selector recovery: alternative '{recovery.AlternativeId}', {recovery.OriginalSelector} → {recovery.ResolvedSelector}, engine verified: {(recovery.EngineVerified ? "yes" : "no")}, input: {recovery.ActionOutcome}");
        text.AppendLine($"Started {step.StartedAt.ToLocalTime():HH:mm:ss.fff}, {step.DurationMs.ToString("N0", CultureInfo.CurrentCulture)} ms");
        if (ScreenshotFile(step, run) is { } shot) text.AppendLine($"Screenshot: {shot}");
        foreach (var diagnostic in step.FailureDiagnostics) text.AppendLine().AppendLine(FailureDiagnostics.Format(diagnostic));
        text.AppendLine().Append(RunDetails(run));
        return text.ToString().TrimEnd();
    }
}
