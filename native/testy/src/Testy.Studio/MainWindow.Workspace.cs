using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Testy.Core;

namespace Testy.Studio;

/// <summary>
/// Live workspace updates: tests and runs written outside this Studio (an AI agent through the MCP server, another Testy tool, a person) appear at
/// once. <see cref="WorkspaceWatcher"/> reports the changed files; this part decides what to reload, and never changes busy state, disables anything
/// or opens a dialog. Another test → the list is rebound with the selection, focus and scroll kept. The selected test with a clean editor → reloaded
/// in place (same view, same step). With unsaved edits (in any field, the JSON view or a cell still being edited) → the edits stay and an InfoBar
/// offers Reload / Keep my edits, and nothing implicit overwrites the outside version meanwhile. Deleted → the editor keeps its copy and offers
/// Save a copy / Discard (Save keeps it under its own id). A run → Results and, for the selected test, Last run (unless a run was opened on purpose).
/// While Studio is busy or a dialog is open, changes to the selected test wait. A file still being written is skipped and read again on the next
/// pass; every file is normalised and checked before it is shown, and nothing is ever wiped because one file is unreadable.
/// </summary>
public partial class MainWindow
{
    private const string WorkspaceSource = "Workspace";
    private const int MaximumReadRetries = 6;
    private WorkspaceWatcher? _watcher;
    /// <summary>The version on disk of the selected test while the editor keeps unsaved edits (Reload loads it).</summary>
    private TestCase? _externalVersion;
    /// <summary>The selected test's file was deleted outside Studio; the editor keeps its copy until Save a copy, Save or Discard.</summary>
    private bool _externalDeleted;
    /// <summary>An outside run of the selected test arrived while Studio was busy; Last run is refreshed when the operation ends.</summary>
    private bool _lastRunStale;
    /// <summary>A modal dialog of Studio is open (step options, file pickers): changes to the selected test wait until it closes.</summary>
    private int _modalDepth;
    private readonly Dictionary<string, int> _readRetries = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Test file → test id, filled when tests are loaded and when a file is read, so a deleted or renamed file maps to its test even when it was not named after its id.</summary>
    private readonly Dictionary<string, string> _testFileIds = new(StringComparer.OrdinalIgnoreCase);

    private enum ChangeOutcome { None, Changed, Retry }

    private void StartWorkspaceWatcher()
    {
        _watcher?.Dispose(); _watcher = null;
        try { _watcher = new WorkspaceWatcher(_store.RootDirectory, Dispatcher, OnWorkspaceChanged, OnWorkspaceWatcherError); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        { Log(ActivityLevel.Warning, WorkspaceSource, "Tests and runs changed outside Studio won't appear until it restarts: " + ex.Message); }
    }
    private string TestPath(string id) => Path.Combine(_store.RootDirectory, "tests", id + ".json");
    private string RunPath(string id) => Path.Combine(_store.RootDirectory, "runs", id + ".json");

    // ── Tolerant loading ──
    /// <summary>
    /// Every readable test in the workspace, normalised and checked. Unreadable or invalid files are reported once in the activity log and left out,
    /// so one bad file (written by another tool, or half written) never keeps Studio from starting or refreshing.
    /// </summary>
    private List<TestCase> LoadTestsTolerant()
    {
        var tests = new List<TestCase>();
        _testFileIds.Clear();
        string[] files;
        try { files = Directory.GetFiles(Path.Combine(_store.RootDirectory, "tests"), "*.json"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log(ActivityLevel.Warning, WorkspaceSource, "The tests folder could not be listed: " + ex.Message); return tests; }
        foreach (var file in files)
        {
            TestCase? test;
            try { test = WorkspaceFiles.ReadTest(WorkspaceWatcher.ReadShared(file), out var problem); if (test is null) { Log(ActivityLevel.Warning, WorkspaceSource, $"{Path.GetFileName(file)} was left out: {problem}", detail: file); continue; } }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { Log(ActivityLevel.Warning, WorkspaceSource, $"{Path.GetFileName(file)} could not be read and was left out: {ex.Message}", detail: file); continue; }
            if (tests.Any(t => t.Id == test.Id)) { Log(ActivityLevel.Warning, WorkspaceSource, $"{Path.GetFileName(file)} was left out: another file already holds the test with id {test.Id}.", detail: file); continue; }
            _testFileIds[file] = test.Id;
            tests.Add(test);
        }
        return tests.OrderByDescending(t => t.UpdatedAt).ToList();
    }
    /// <summary>Every readable run, newest first, for the Results page; unreadable files are reported once and left out.</summary>
    private List<RunResult> LoadRunsTolerant()
    {
        var runs = new List<RunResult>();
        string[] files;
        try { files = Directory.GetFiles(Path.Combine(_store.RootDirectory, "runs"), "*.json"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log(ActivityLevel.Warning, WorkspaceSource, "The runs folder could not be listed: " + ex.Message); return runs; }
        foreach (var file in files)
        {
            try
            {
                var run = JsonSerializer.Deserialize<RunResult>(WorkspaceFiles.WithoutByteOrderMark(WorkspaceWatcher.ReadShared(file)), TestyJson.Options);
                if (run is null || string.IsNullOrEmpty(run.Id)) { Log(ActivityLevel.Warning, WorkspaceSource, $"{Path.GetFileName(file)} holds no run and was left out.", detail: file); continue; }
                run.Steps ??= []; run.FailureDiagnostics ??= []; run.ProjectEvidence ??= []; run.Target ??= new TargetInfo(); run.TestName ??= ""; run.Summary ??= ""; run.AiAnalysis ??= ""; run.ArtifactDirectory ??= "";
                runs.Add(run);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { Log(ActivityLevel.Warning, WorkspaceSource, $"{Path.GetFileName(file)} could not be read and was left out of Results: {ex.Message}", detail: file); }
        }
        return runs.OrderByDescending(r => r.StartedAt).ToList();
    }

    /// <summary>The watcher lost events (its buffer overflowed): watch again and compare everything on disk with what Studio shows.</summary>
    private void OnWorkspaceWatcherError(string message)
    {
        Log(ActivityLevel.Warning, WorkspaceSource, "Catching up with changes in the data folder: " + message);
        StartWorkspaceWatcher();
        var changes = new List<WorkspaceChange>();
        try
        {
            changes.AddRange(Directory.EnumerateFiles(Path.Combine(_store.RootDirectory, "tests"), "*.json").Select(p => new WorkspaceChange(WorkspaceArea.Tests, p)));
            // The files Studio knows tests by (not their ids: a test may be stored under another file name); a test whose file is gone is reported as deleted.
            changes.AddRange(_testFileIds.Keys.Select(p => new WorkspaceChange(WorkspaceArea.Tests, p)));
            changes.AddRange(Directory.EnumerateFiles(Path.Combine(_store.RootDirectory, "runs"), "*.json").Select(p => new WorkspaceChange(WorkspaceArea.Runs, p)));
            changes.AddRange(_runIndex.Select(r => new WorkspaceChange(WorkspaceArea.Runs, RunPath(r.Id))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log(ActivityLevel.Warning, WorkspaceSource, "The data folder could not be listed: " + ex.Message); }
        OnWorkspaceChanged(changes.DistinctBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>One debounced batch of changed files, on the UI thread. Nothing that happens here may surface as a dialog: problems go to the activity log.</summary>
    private void OnWorkspaceChanged(IReadOnlyList<WorkspaceChange> changes)
    {
        if (_watcher is null) return;
        try
        {
            var retry = new List<WorkspaceChange>();
            bool tests = false, runs = false, selectedRun = false;
            foreach (var change in changes)
            {
                ChangeOutcome outcome;
                // The selected test is not swapped under an operation or a dialog that holds it: its changes wait until they are over.
                if (change.Area == WorkspaceArea.Tests && (_busy || _modalDepth > 0) && ConcernsSelectedTest(change.Path)) { retry.Add(change); continue; }
                try { outcome = change.Area == WorkspaceArea.Tests ? ApplyTestChange(change) : ApplyRunChange(change, ref selectedRun); }
                catch (Exception ex) { Log(ActivityLevel.Warning, WorkspaceSource, "A change in the data folder could not be applied: " + ex.Message, detail: change.Path + Environment.NewLine + ex); continue; }
                if (outcome == ChangeOutcome.Retry) retry.Add(change);
                else if (outcome == ChangeOutcome.Changed) { if (change.Area == WorkspaceArea.Tests) tests = true; else runs = true; }
            }
            if (tests || runs) RebindLibraryKeepingPlace();
            if (runs) RefreshAfterOutsideRuns(selectedRun);
            if (retry.Count > 0) _watcher.Retry(retry, TimeSpan.FromMilliseconds(500));
        }
        catch (Exception ex) { Log(ActivityLevel.Error, WorkspaceSource, "Changes in the data folder could not be shown: " + ex.Message, detail: ex.ToString()); }
    }
    private bool ConcernsSelectedTest(string path)
    {
        if (_selected is null) return false;
        if (_testFileIds.TryGetValue(path, out var id)) return id == _selected.Id;
        return string.Equals(Path.GetFileNameWithoutExtension(path), _selected.Id, StringComparison.OrdinalIgnoreCase);
    }

    private ChangeOutcome ApplyTestChange(WorkspaceChange change)
    {
        var path = change.Path;
        if (!File.Exists(path))
        {
            _readRetries.Remove(path);
            var id = _testFileIds.Remove(path, out var known) ? known : Path.GetFileNameWithoutExtension(path);
            if (_watcher!.IsOwnDelete(path)) return ChangeOutcome.None;
            if (_testFileIds.ContainsValue(id)) return ChangeOutcome.None; // another file still holds this test
            var gone = _tests.FirstOrDefault(t => t.Id == id);
            if (gone is null) return ChangeOutcome.None;
            if (ReferenceEquals(gone, _selected)) MarkSelectedDeletedOutside();
            else { _tests.Remove(gone); Log(ActivityLevel.Info, WorkspaceSource, $"Test “{gone.Name}” was deleted by an outside tool."); }
            return ChangeOutcome.Changed;
        }
        byte[] bytes; TestCase? loaded; string? problem = null;
        try { bytes = WorkspaceWatcher.ReadShared(path); loaded = bytes.Length == 0 ? null : WorkspaceFiles.ReadTest(bytes, out problem); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return ScheduleRetry(path, ex.Message); }
        if (bytes.Length == 0) return ScheduleRetry(path, "the file is empty yet");
        if (loaded is null)
        {
            // Not valid JSON, or not a test: either still being written (tried again) or written wrongly (reported once, then ignored until it changes).
            if (WorkspaceFiles.LooksIncomplete(bytes)) return ScheduleRetry(path, problem);
            _readRetries.Remove(path);
            Log(ActivityLevel.Warning, WorkspaceSource, $"{Path.GetFileName(path)} is not a test Studio can show: {problem}", detail: path);
            return ChangeOutcome.None;
        }
        _readRetries.Remove(path);
        var previousId = _testFileIds.TryGetValue(path, out var earlier) ? earlier : null;
        _testFileIds[path] = loaded.Id;
        if (_watcher!.IsOwnWrite(path, bytes)) return ChangeOutcome.None;
        var changed = false;
        if (previousId is not null && previousId != loaded.Id && !_testFileIds.ContainsValue(previousId))
        {
            // The file now holds another test: the one it held before has no file any more.
            var orphan = _tests.FirstOrDefault(t => t.Id == previousId);
            if (orphan is not null && !ReferenceEquals(orphan, _selected)) { _tests.Remove(orphan); changed = true; Log(ActivityLevel.Info, WorkspaceSource, $"Test “{orphan.Name}” was replaced in its file by an outside tool."); }
            else if (orphan is not null) { MarkSelectedDeletedOutside(); changed = true; }
        }
        var existing = _tests.FirstOrDefault(t => t.Id == loaded.Id);
        if (existing is null && _externalDeleted && _selected?.Id == loaded.Id)
        {
            // The deleted file is back (the outside tool restored it): from here on it is a change of the open test.
            _tests.Add(_selected); existing = _selected; HideExternalChangeBar();
            if (SameJson(existing, loaded)) { _dirty = false; RestoreSaveState(); Log(ActivityLevel.Info, WorkspaceSource, $"Test “{loaded.Name}” is back on disk, unchanged."); return ChangeOutcome.Changed; }
        }
        if (existing is null)
        {
            _tests.Add(loaded);
            Log(ActivityLevel.Info, WorkspaceSource, $"Test “{loaded.Name}” was added by an outside tool.");
            return ChangeOutcome.Changed;
        }
        if (SameJson(existing, loaded)) return changed ? ChangeOutcome.Changed : ChangeOutcome.None;
        if (ReferenceEquals(existing, _selected))
        {
            if (EditorHasUnsavedWork())
            {
                _externalVersion = loaded; ShowExternalChangeBar(deleted: false);
                Log(ActivityLevel.Warning, WorkspaceSource, $"Test “{loaded.Name}” was changed by an outside tool. Your unsaved edits are kept until you choose Reload or Keep my edits.");
            }
            else if (ReplaceSelectedTest(loaded)) Log(ActivityLevel.Info, WorkspaceSource, $"Test “{loaded.Name}” was changed by an outside tool.");
            return ChangeOutcome.Changed;
        }
        _tests[_tests.IndexOf(existing)] = loaded;
        Log(ActivityLevel.Info, WorkspaceSource, $"Test “{loaded.Name}” was changed by an outside tool.");
        return ChangeOutcome.Changed;
    }

    private ChangeOutcome ApplyRunChange(WorkspaceChange change, ref bool selectedAffected)
    {
        var path = change.Path;
        if (!File.Exists(path))
        {
            _readRetries.Remove(path);
            var id = Path.GetFileNameWithoutExtension(path);
            var removed = _runIndex.FirstOrDefault(r => r.Id == id);
            if (removed is null) return ChangeOutcome.None;
            _runIndex.Remove(removed);
            if (removed.TestId == _selected?.Id) selectedAffected = true;
            Log(ActivityLevel.Info, WorkspaceSource, $"A run of “{removed.TestName}” was removed by an outside tool.", removed.Id);
            return ChangeOutcome.Changed;
        }
        byte[] bytes; RunHeader? header;
        try { bytes = WorkspaceWatcher.ReadShared(path); header = bytes.Length == 0 ? null : JsonSerializer.Deserialize<RunHeader>(WorkspaceFiles.WithoutByteOrderMark(bytes), TestyJson.Options); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return ScheduleRetry(path, ex.Message); }
        if (header is null || string.IsNullOrEmpty(header.Id)) return ScheduleRetry(path, "the file is empty or has no run id yet");
        _readRetries.Remove(path);
        if (_watcher!.IsOwnWrite(path, bytes)) return ChangeOutcome.None;
        var previous = _runIndex.FirstOrDefault(r => r.Id == header.Id);
        if (previous is not null && previous.Status == header.Status && previous.FinishedAt == header.FinishedAt && previous.StartedAt == header.StartedAt) return ChangeOutcome.None;
        _runIndex.RemoveAll(r => r.Id == header.Id); _runIndex.Add(header);
        if (header.TestId == _selected?.Id) selectedAffected = true;
        if (header.FinishedAt is not null)
            Log(header.Status switch { RunStatus.Passed => ActivityLevel.Success, RunStatus.Failed => ActivityLevel.Error, _ => ActivityLevel.Warning }, WorkspaceSource,
                $"Run of “{header.TestName}” finished ({StepText.StatusWord(header.Status)}) from an outside tool.", header.Id);
        else Log(ActivityLevel.Info, WorkspaceSource, $"Run of “{header.TestName}” started from an outside tool.", header.Id);
        return ChangeOutcome.Changed;
    }

    /// <summary>A file that could not be read (still being written, or locked) is tried again a few times; then it waits for its next change.</summary>
    private ChangeOutcome ScheduleRetry(string path, string? reason)
    {
        var attempts = _readRetries.GetValueOrDefault(path) + 1;
        if (attempts > MaximumReadRetries)
        {
            _readRetries.Remove(path);
            Log(ActivityLevel.Warning, WorkspaceSource, $"{Path.GetFileName(path)} could not be read after several attempts; Studio shows it again when it changes.", detail: reason);
            return ChangeOutcome.None;
        }
        _readRetries[path] = attempts;
        return ChangeOutcome.Retry;
    }
    private static bool SameJson<T>(T left, T right) => JsonSerializer.Serialize(left, TestyJson.Options) == JsonSerializer.Serialize(right, TestyJson.Options);

    /// <summary>
    /// Unsaved work in any form: the dirty flag, JSON text that differs from the selected test, a step timeout typed but not yet applied, or a
    /// grid cell still being edited. The dirty flag alone misses the last three.
    /// </summary>
    private bool EditorHasUnsavedWork()
    {
        if (_dirty || _selected is null) return _dirty;
        try
        {
            if (JsonView.Visibility == Visibility.Visible && JsonEditor.Text.Length > 0 && JsonEditor.Text != JsonSerializer.Serialize(_selected, TestyJson.Options)) return true;
            if (StepsGrid.SelectedItem is StepRow row && int.TryParse(StepTimeout.Text, out var timeout) && timeout != row.Step.TimeoutMs) return true;
            if (StepsGrid.CurrentItem is not null && StepsGrid.ItemContainerGenerator.ContainerFromItem(StepsGrid.CurrentItem) is DataGridRow current && current.IsEditing) return true;
            if (TestName.Text.Trim() != _selected.Name || TestIntent.Text.Trim() != _selected.Intent) return true;
        }
        catch (InvalidOperationException) { return true; }
        return false;
    }

    /// <summary>Rebinds the grouped list without moving it: the selected test stays selected (not reloaded), the scroll position is restored, and the keyboard focus returns to the selected item when the list had it.</summary>
    private void RebindLibraryKeepingPlace()
    {
        var offset = FindChild<ScrollViewer>(TestList)?.VerticalOffset ?? 0;
        var hadFocus = TestList.IsKeyboardFocusWithin;
        RefreshLibrary();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (offset > 0 && FindChild<ScrollViewer>(TestList) is { } scroll) scroll.ScrollToVerticalOffset(Math.Min(offset, scroll.ScrollableHeight));
            if (hadFocus && TestList.SelectedItem is { } item && TestList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container) container.Focus();
            else if (hadFocus && !TestList.IsKeyboardFocusWithin) TestList.Focus();
        });
    }

    /// <summary>The selected test, clean, changed on disk: reload it into the editor keeping the current view, step, shown run and message. False when it could not be shown (the current test stays).</summary>
    private bool ReplaceSelectedTest(TestCase fresh)
    {
        var view = _view; var step = StepsGrid.SelectedIndex;
        var index = _tests.FindIndex(t => t.Id == fresh.Id);
        var before = index >= 0 ? _tests[index] : null;
        if (index >= 0) _tests[index] = fresh; else _tests.Add(fresh);
        try { LoadTest(fresh, keepView: true); }
        catch (Exception ex)
        {
            // Rows could not be built from the new version: the test on screen stays as it was.
            if (index >= 0) _tests[index] = before!; else _tests.Remove(fresh);
            if (_selected is { } current) { try { LoadTest(current, keepView: true); } catch (Exception inner) { Log(ActivityLevel.Warning, WorkspaceSource, "The open test could not be redrawn: " + inner.Message); } }
            Log(ActivityLevel.Warning, WorkspaceSource, $"The new version of “{fresh.Name}” could not be shown, so the open one stays: {ex.Message}", detail: ex.ToString());
            return false;
        }
        if (view != EditorView.Steps) ShowEditorView(view);
        if (step >= 0 && step < _steps.Count) StepsGrid.SelectedIndex = step;
        return true;
    }
    private void MarkSelectedDeletedOutside()
    {
        var test = _selected!;
        _tests.Remove(test);
        _externalVersion = null; _externalDeleted = true;
        ShowExternalChangeBar(deleted: true);
        RestoreSaveState();
        Log(ActivityLevel.Warning, WorkspaceSource, $"Test “{test.Name}” was deleted by an outside tool. Your copy stays open until you choose Save a copy or Discard.");
    }
    /// <summary>The save-state text for the current situation: deleted outside (amber), unsaved edits, or saved.</summary>
    private void RestoreSaveState()
    {
        if (SaveState is null) return;
        SaveState.Text = _externalDeleted ? (_dirty ? "Not on disk: deleted outside Studio. Save or Save a copy keeps it with your edits." : "Not on disk: deleted outside Studio. Save or Save a copy keeps it.")
            : _dirty ? "Unsaved changes. They're saved when you switch tests or press Save." : "Saved";
    }

    /// <summary>Results (when shown) and, for the selected test, Last run follow a run written outside Studio; during a Studio operation Last run waits.</summary>
    private void RefreshAfterOutsideRuns(bool selectedAffected)
    {
        if (RunsPage.Visibility == Visibility.Visible) RefreshRunList();
        if (!selectedAffected) return;
        if (_busy) { _lastRunStale = true; return; }
        RefreshLastRunFromDisk();
    }
    /// <summary>
    /// Last run shows the selected test's latest stored run again, unless a run is shown on purpose (opened from Results, or just produced by
    /// Studio): that one stays, and the status line says that a newer run exists.
    /// </summary>
    private void RefreshLastRunFromDisk()
    {
        if (_shownRunLive) return;
        _lastRunStale = false;
        if (_selected is null) return;
        if (_shownRun is not null && _shownRunFor != _selected.Id) return;
        if (_shownRun is not null && _shownRunPinned)
        {
            var latest = LatestRuns().GetValueOrDefault(_selected.Id);
            if (latest is not null && latest.Id != _shownRun.Id) SetStatus($"A newer run of “{_selected.Name}” arrived from an outside tool; the run shown here stays. Find it under Results.", ActivityLevel.Info, WorkspaceSource, runId: latest.Id);
            return;
        }
        _shownRun = null; _shownRunFor = null;
        if (_view == EditorView.LastRun) EnsureShownRun();
    }

    // ── The InfoBar for the selected test (ExternalChangeBar) ──
    private void ShowExternalChangeBar(bool deleted)
    {
        ExternalChangeText.Text = deleted
            ? "This test was deleted outside Studio. Your copy stays open: save it as a new test, or discard it."
            : "This test was changed outside Studio. Reload to see that version, or keep your unsaved edits (saving replaces it).";
        AutomationProperties.SetName(ExternalChangeBar, deleted ? "Deleted outside Studio" : "Changed outside Studio");
        AutomationProperties.SetLiveSetting(ExternalChangeText, deleted ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        ReloadExternalButton.Visibility = KeepEditsButton.Visibility = deleted ? Visibility.Collapsed : Visibility.Visible;
        SaveDeletedButton.Visibility = DiscardDeletedButton.Visibility = deleted ? Visibility.Visible : Visibility.Collapsed;
        ExternalChangeBar.Visibility = Visibility.Visible;
        AnnounceLiveRegion(ExternalChangeText);
    }
    private void HideExternalChangeBar()
    {
        _externalVersion = null; _externalDeleted = false;
        if (ExternalChangeBar != null) ExternalChangeBar.Visibility = Visibility.Collapsed;
    }
    /// <summary>After a bar button collapsed the bar, the keyboard focus moves on instead of vanishing with the button.</summary>
    private void FocusAfterBar() { if (Keyboard.FocusedElement is not UIElement { IsVisible: true }) { if (TestName.IsVisible) TestName.Focus(); else TestList.Focus(); } }
    /// <summary>Save while the deleted bar shows: the person wants the test back under its own id.</summary>
    private void KeepDeletedTest()
    {
        if (!_externalDeleted || _selected is null) return;
        if (!_tests.Contains(_selected)) _tests.Add(_selected);
        HideExternalChangeBar();
    }
    private void ReloadExternalChange_Click(object sender, RoutedEventArgs e) => GuardSync(() =>
    {
        if (_externalVersion is not { } fresh) { HideExternalChangeBar(); return; }
        HideExternalChangeBar();
        if (ReplaceSelectedTest(fresh)) SetStatus($"Reloaded “{fresh.Name}” from the data folder; your unsaved edits were replaced.", ActivityLevel.Info, WorkspaceSource);
        RebindLibraryKeepingPlace();
        FocusAfterBar();
    });
    private void KeepMyEdits_Click(object sender, RoutedEventArgs e)
    {
        HideExternalChangeBar();
        SetStatus("Your edits are kept. Saving replaces the version changed outside Studio.", ActivityLevel.Info, WorkspaceSource);
        FocusAfterBar();
    }
    private void SaveExternalDeleted_Click(object sender, RoutedEventArgs e) => GuardSync(() =>
    {
        if (_selected is null) { HideExternalChangeBar(); return; }
        CommitEditor(save: false);
        var copy = TestyJson.Clone(_selected); copy.Id = Guid.NewGuid().ToString("N");
        foreach (var step in copy.Steps) step.Id = Guid.NewGuid().ToString("N");
        Persist(_store.SaveDraft, copy); _tests.Add(copy); _testFileIds[TestPath(copy.Id)] = copy.Id;
        HideExternalChangeBar();
        _selected = null; // the deleted original is not written back
        RefreshLibrary(copy.Id);
        SetStatus($"Saved a copy of “{copy.Name}”.", ActivityLevel.Success, WorkspaceSource);
        FocusAfterBar();
    });
    private void DiscardExternalDeleted_Click(object sender, RoutedEventArgs e) => GuardSync(() =>
    {
        var name = _selected?.Name ?? "the test";
        HideExternalChangeBar();
        _selected = null;
        _loading = true;
        try { _steps.Clear(); TestName.Text = ""; TestIntent.Text = ""; JsonEditor.Text = ""; _dirty = false; SaveState.Text = "Saved"; }
        finally { _loading = false; }
        LoadStepEditor(null); ForgetShownRun();
        RefreshLibrary(); if (TestList.Items.Count > 0) TestList.SelectedIndex = 0;
        SetStatus($"Discarded “{name}”.", ActivityLevel.Info, WorkspaceSource);
        FocusAfterBar();
    });
}

/// <summary>Reading test files that other tools wrote: a byte order mark is tolerated, and a test is normalised and checked before Studio shows it.</summary>
internal static class WorkspaceFiles
{
    public static ReadOnlySpan<byte> WithoutByteOrderMark(byte[] bytes) => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? bytes.AsSpan(3) : bytes;
    /// <summary>A file that ends before its JSON does is probably still being written.</summary>
    public static bool LooksIncomplete(byte[] bytes)
    {
        var span = WithoutByteOrderMark(bytes);
        var end = span.Length - 1;
        while (end >= 0 && span[end] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t') end--;
        return end < 0 || span[end] != (byte)'}';
    }
    /// <summary>
    /// The test in the file, with every reference property made non-null (System.Text.Json assigns explicit nulls), null steps dropped and
    /// the ids checked. Null with the reason when the content is not a test Studio can show.
    /// </summary>
    public static TestCase? ReadTest(byte[] bytes, out string? problem)
    {
        problem = null;
        TestCase? test;
        try { test = JsonSerializer.Deserialize<TestCase>(WithoutByteOrderMark(bytes), TestyJson.Options); }
        catch (JsonException ex) { problem = "the file is not valid JSON (" + ex.Message + ")"; return null; }
        if (test is null) { problem = "the file holds no test"; return null; }
        test.Name = string.IsNullOrWhiteSpace(test.Name) ? "Untitled test" : test.Name;
        test.Intent ??= ""; test.Category = string.IsNullOrWhiteSpace(test.Category) ? "Functional" : test.Category; test.TargetPath ??= ""; test.TargetName ??= ""; test.TargetAppId ??= "";
        test.Steps = (test.Steps ?? []).Where(s => s is not null).ToList();
        try { TestValidator.ValidateId(test.Id); } catch (InvalidDataException) { problem = "its id is not valid (1–100 letters, digits, underscores or hyphens)"; return null; }
        foreach (var step in test.Steps)
        {
            step.Title ??= ""; step.Selector ??= ""; step.Value ??= ""; step.SelectorAlternatives ??= [];
            try { TestValidator.ValidateId(step.Id); } catch (InvalidDataException) { problem = "a step id is not valid"; return null; }
        }
        if (test.Steps.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != test.Steps.Count) { problem = "two steps share an id"; return null; }
        return test;
    }
}
