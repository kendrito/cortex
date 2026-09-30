using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Testy.Core;
using Testy.Windows;
using Microsoft.Win32;

namespace Testy.Studio;

public partial class MainWindow : Window
{
    private readonly WorkspaceStore _store;
    private readonly ObservableCollection<StepRow> _steps = [];
    private List<TestCase> _tests = [];
    /// <summary>Status and times of every stored run (the test list's status icons and the latest run per test).</summary>
    private List<RunHeader> _runIndex = [];
    private ITargetDriver _driver = new UiAutomationDriver();
    private ProviderSettings _settings;
    private TestCase? _selected;
    private UiSnapshot? _snapshot;
    /// <summary>The last run finished in this session (Improve sends its evidence when it belongs to the test being improved).</summary>
    private RunResult? _lastRun;
    private CancellationTokenSource? _operation;
    private bool _loading = true;
    private bool _dirty;
    private bool _busy;
    private string _screenshot = "";
    private string _targetExe = "";
    private Process? _labProcess;
    private AutomatePage? _automate;
    private bool _compactNav, _compactBar;
    private EditorView _view = EditorView.Steps;
    private bool _switchingView;

    private enum EditorView { Steps, LastRun, Json }

    public MainWindow()
    {
        InitializeComponent();
        VersionLabel.Text = "Testy " + typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
        var args = Environment.GetCommandLineArgs();
        var argIndex = Array.IndexOf(args, "--workspace");
        var workspace = argIndex >= 0 && argIndex + 1 < args.Length ? Path.GetFullPath(args[argIndex + 1])
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testy", "Workspace");
        _store = new WorkspaceStore(workspace);
        WorkspacePathText.Text = _store.RootDirectory;
        _settings = _store.LoadSettings();
        ActionColumn.ItemsSource = Enum.GetValues<StepAction>();
        StepActionPicker.ItemsSource = StepText.ActionChoices; StepActionPicker.DisplayMemberPath = nameof(ActionChoice.Label); StepActionPicker.SelectedValuePath = nameof(ActionChoice.Action);
        StepControlPicker.DisplayMemberPath = nameof(ControlChoice.Label); StepControlPicker.SelectedValuePath = nameof(ControlChoice.Selector);
        StepsGrid.ItemsSource = _steps;
        RunStepList.ItemsSource = _runRows;
        ProviderCombo.ItemsSource = Enum.GetValues<ProviderKind>();
        LoadConnectionFields();
        // Tolerant of other writers: one unreadable file in tests\ is reported in the activity log, not a reason for Studio not to start.
        _tests = LoadTestsTolerant();
        if (InitializeWorkspace(_store, _tests.Count)) _tests = LoadTestsTolerant();
        _runIndex = RunHeader.LoadAll(Path.Combine(_store.RootDirectory, "runs"));
        _loading = false;
        RefreshLibrary();
        if (TestList.Items.Count > 0)
            SelectTest((_tests.FirstOrDefault(t => t.Name == "Create a customer") ?? (TestList.Items[0] as LibraryRow)?.Test)?.Id);
        UpdateTargetCard();
        BuildShortcutList();
        // The Help flyout opens beside the Help item, bottom-aligned with it (the surface has 12/16 px of shadow margin).
        HelpFlyout.CustomPopupPlacementCallback = (popup, target, _) => [new CustomPopupPlacement(new Point(target.Width - 4, target.Height - popup.Height + 16), PopupPrimaryAxis.Horizontal)];
        // Improve opens under its button, right-aligned with it.
        ImproveFlyout.CustomPopupPlacementCallback = (popup, target, _) => [new CustomPopupPlacement(new Point(target.Width - popup.Width + 12, target.Height), PopupPrimaryAxis.Vertical)];
        FitToWorkArea();
        StudioPreferences.Current.PropertyChanged += Preferences_Changed;
        ActivityLogPath.Text = _activity.CurrentFile;
        ShowEditorView(EditorView.Steps);
        UpdateGettingStarted();
        Log(ActivityLevel.Info, "App", "Testy started. Data folder: " + _store.RootDirectory);
        // Tests and runs written by anything else (an agent through the MCP server, another tool) appear as they are saved.
        StartWorkspaceWatcher();
        InitializeAgentAccess();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await Guard(() => PerformOperation(RefreshTargets));
        SetStatus("Connect an app to test, or try the sample app.");
        await RunScreenshotModeAsync();
    }

    private static TestStep Step(string title, StepAction action, string selector = "", string value = "", int timeout = 5000) => new()
    { Title = title, Action = action, Selector = selector, Value = value, TimeoutMs = timeout };

    /// <summary>Mark a first empty workspace initialized; only standalone Studio creates the demonstration tests.</summary>
    internal static bool InitializeWorkspace(WorkspaceStore store, int existingTestCount)
    {
        var marker = Path.Combine(store.RootDirectory, ".initialized");
        if (existingTestCount != 0 || File.Exists(marker)) return false;
        if (!CortexModelBridge.Enabled)
            foreach (var test in SeedTests()) store.SaveTest(test);
        File.WriteAllText(marker, "Testy workspace initialized");
        return true;
    }

    private static IEnumerable<TestCase> SeedTests()
    {
        yield return new TestCase
        {
            Name = "Create a customer", Category = "Customer Desk", Intent = "A valid customer can be added, and a clear confirmation appears.",
            Steps = [Step("Reset the test lab", StepAction.Click, "id:ResetButton"), Step("Enter customer name", StepAction.TypeText, "id:CustomerName", "Ada Lovelace"), Step("Enter email address", StepAction.TypeText, "id:CustomerEmail", "ada@example.test"), Step("Add the customer", StepAction.Click, "id:AddCustomer"), Step("Verify confirmation", StepAction.AssertText, "id:StatusMessage", "Customer added: Ada Lovelace")]
        };
        yield return new TestCase
        {
            Name = "Required field validation", Category = "Customer Desk", Intent = "Submitting an empty customer form shows a validation message.",
            Steps = [Step("Reset the test lab", StepAction.Click, "id:ResetButton"), Step("Submit empty form", StepAction.Click, "id:AddCustomer"), Step("Verify validation", StepAction.AssertText, "id:StatusMessage", "Enter a customer name.")]
        };
        yield return new TestCase
        {
            Name = "Inspect customer form", Category = "Smoke", Intent = "Core customer entry controls are available and enabled.",
            Steps = [Step("Find name field", StepAction.AssertExists, "id:CustomerName"), Step("Find email field", StepAction.AssertExists, "id:CustomerEmail"), Step("Verify add is enabled", StepAction.AssertEnabled, "id:AddCustomer"), Step("Capture customer desk", StepAction.Screenshot)]
        };
        yield return new TestCase
        {
            Name = "Failure evidence example", Category = "Expected failure", Intent = "Demonstrate a failed assertion with observed values and screenshot evidence. This test intentionally fails.",
            Steps = [Step("Reset the test lab", StepAction.Click, "id:ResetButton"), Step("Assert an impossible message", StepAction.AssertText, "id:StatusMessage", "This message deliberately does not exist", 1200)]
        };
    }

    // ═══════════════ Test list ═══════════════
    private static bool Matches(TestCase test, string query) =>
        test.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || test.Category.Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Rebuilds the grouped list (group = category, sorted by name) with each test's latest run. <paramref name="selectId"/> selects that
    /// test (clearing a filter that would hide it) and loads it; otherwise the current test stays selected without reloading the editor.
    /// </summary>
    private void RefreshLibrary(string? selectId = null)
    {
        UpdateGettingStarted();
        var query = SearchTests?.Text?.Trim() ?? "";
        var selected = _tests.FirstOrDefault(t => t.Id == selectId);
        if (selected != null && query.Length > 0 && !Matches(selected, query))
        {
            var loading = _loading; _loading = true; SearchTests!.Text = ""; _loading = loading; query = "";
        }
        var latest = LatestRuns();
        var now = DateTimeOffset.Now;
        var rows = _tests.Where(t => query.Length == 0 || Matches(t, query)).Select(t => new LibraryRow(t, latest.GetValueOrDefault(t.Id), now)).ToList();
        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LibraryRow.Category)));
        view.SortDescriptions.Add(new SortDescription(nameof(LibraryRow.Category), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(LibraryRow.Name), ListSortDirection.Ascending));
        var wasLoading = _loading;
        _loading = true; TestList.ItemsSource = view; _loading = wasLoading;
        var target = rows.FirstOrDefault(r => r.Test.Id == (selectId ?? _selected?.Id));
        if (target is null) return;
        if (selectId is null || ReferenceEquals(target.Test, _selected)) { _loading = true; TestList.SelectedItem = target; _loading = wasLoading; }
        else TestList.SelectedItem = target; // loads it through SelectionChanged
    }
    private Dictionary<string, RunHeader> LatestRuns() =>
        _runIndex.GroupBy(r => r.TestId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.StartedAt).First());
    private LibraryRow? RowFor(TestCase? test) => test is null ? null : TestList.Items.OfType<LibraryRow>().FirstOrDefault(r => r.Test.Id == test.Id);
    /// <summary>Selects a test by ID (clearing a filter that hides it) and loads it into the editor.</summary>
    private void SelectTest(string? id) { if (id != null) RefreshLibrary(id); }

    private void SearchTests_Changed(object sender, TextChangedEventArgs e)
    {
        // Filtering is not a reason to decide a pending "changed/deleted outside Studio" question: then the editor is only read, not saved.
        if (!_loading && !_busy && TestList != null) GuardSync(() => { CommitEditor(save: _externalVersion is null && !_externalDeleted); RefreshLibrary(); });
    }
    private void TestList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || TestList.SelectedItem is not LibraryRow row || ReferenceEquals(row.Test, _selected)) return;
        var test = row.Test;
        try
        {
            if (_selected?.Id != test.Id) CommitEditor();
            LoadTest(test);
        }
        catch (Exception ex)
        {
            _loading = true; TestList.SelectedItem = RowFor(_selected); _loading = false;
            ReportError("Changes could not be saved, so this test stays open: " + ex.Message, "Tests");
        }
    }
    private void TestList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None && e.OriginalSource is ListBoxItem or ListBox) { DeleteTest_Click(sender, e); e.Handled = true; }
    }
    /// <summary>
    /// Shows a test in the editor. The rows are built before anything on screen changes, and the loading flag is always released, so a test
    /// that cannot be shown leaves the editor working. With <paramref name="keepView"/> (an outside change of the open test) the shown run and the
    /// assistant's message stay.
    /// </summary>
    private void LoadTest(TestCase test, bool keepView = false)
    {
        var number = 1;
        var rows = test.Steps.Select(step => new StepRow(step, number++, _snapshot)).ToList();
        var json = JsonSerializer.Serialize(test, TestyJson.Options);
        _loading = true;
        try
        {
            _selected = test;
            TestName.Text = test.Name;
            TestIntent.Text = test.Intent;
            _steps.Clear();
            foreach (var row in rows) _steps.Add(row);
            JsonEditor.Text = json;
            _dirty = false;
            SaveState.Text = "Saved";
        }
        finally { _loading = false; }
        LoadStepEditor(null);
        UpdateRunsIn();
        if (!keepView) { ForgetShownRun(); HideEditorMessage(); HideAppChoice(); }
        HideExternalChangeBar();
        if (!keepView) ShowEditorView(EditorView.Steps);
    }
    /// <summary>
    /// Writes the editor into the selected test and, with <paramref name="save"/>, to disk. An implicit commit (switching tests, filtering, running,
    /// closing) never decides for the person while the ExternalChangeBar asks: it refuses with the choice, so the caller keeps the test open. Only Save
    /// and Keep my edits (<paramref name="explicitSave"/>) replace a version changed outside Studio, and only Save keeps a test deleted outside it.
    /// </summary>
    private void CommitEditor(bool save = true, bool explicitSave = false)
    {
        if (_loading || _selected == null) return;
        if (save && !explicitSave && _externalVersion is not null)
            throw new InvalidOperationException($"“{_selected.Name}” was changed outside Studio. Choose Reload or Keep my edits above before leaving it.");
        if (save && !explicitSave && _externalDeleted)
            throw new InvalidOperationException($"“{_selected.Name}” was deleted outside Studio. Choose Save a copy or Discard above before leaving it (Save keeps it under its own id).");
        StepsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        StepsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        _selected.Name = string.IsNullOrWhiteSpace(TestName.Text) ? "Untitled test" : TestName.Text.Trim();
        _selected.Intent = TestIntent.Text.Trim();
        _selected.Steps = _steps.Select(r => r.Step).ToList();
        _selected.UpdatedAt = DateTimeOffset.UtcNow;
        if (!save) return;
        if (_externalVersion is not null) Log(ActivityLevel.Warning, WorkspaceSource, $"Saved “{_selected.Name}” over the version an outside tool wrote.");
        Persist(_store.SaveDraft, _selected);
        _testFileIds[TestPath(_selected.Id)] = _selected.Id;
        _dirty = false;
        SaveState.Text = "Saved";
        HideExternalChangeBar(); // a save settles "changed outside Studio": this version is the one on disk now
    }
    /// <summary>
    /// Workspace files are replaced atomically. Another process reading a file at that instant (a verifier polling the workspace, a backup or
    /// antivirus scan) makes the replacement fail transiently, so writes are retried briefly. Still synchronous; validation errors are not retried.
    /// A saved test or run is then noted with the workspace watcher, so Studio's own save never looks like a change made outside it.
    /// </summary>
    private void Persist<T>(Action<T> write, T value)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { write(value); break; }
            catch (Exception ex) when (attempt < 8 && ex is UnauthorizedAccessException or IOException && ex is not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException))
            { Thread.Sleep(20 * attempt); }
        }
        switch (value)
        {
            case TestCase test: _watcher?.NoteOwnWrite(TestPath(test.Id), test); break;
            case RunResult run: _watcher?.NoteOwnWrite(RunPath(run.Id), run); break;
        }
    }
    private void Editor_Changed(object sender, TextChangedEventArgs e) { if (!_loading) MarkDirty(); }
    private void StepsGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) => MarkDirty();
    private void MarkDirty() { _dirty = true; RestoreSaveState(); }
    private void SaveTest_Click(object sender, RoutedEventArgs e) => GuardSync(() =>
    {
        KeepDeletedTest(); CommitEditor(explicitSave: true); RefreshLibrary(_selected?.Id);
        if (EditorMessageIcon.Tag as string == "Error") HideEditorMessage();
        SetStatus("Test saved.", ActivityLevel.Success, "Tests");
    });
    private void NewTest_Click(object sender, RoutedEventArgs e) => GuardSync(() =>
    {
        CommitEditor(); var test = new TestCase { Name = "New functional test", Intent = "Describe what should happen in your application." };
        Persist(_store.SaveDraft, test); _tests.Add(test); RefreshLibrary(test.Id); ShowEditorView(EditorView.Steps); TestName.Focus(); TestName.SelectAll();
        Log(ActivityLevel.Info, "Tests", "Created a new test.");
    });
    private void DuplicateTest_Click(object sender, RoutedEventArgs e) => GuardSync(() =>
    {
        if (_selected == null) return; CommitEditor(); var copy = TestyJson.Clone(_selected); copy.Id = Guid.NewGuid().ToString("N"); copy.Name += " (copy)";
        foreach (var step in copy.Steps) step.Id = Guid.NewGuid().ToString("N"); Persist(_store.SaveDraft, copy); _tests.Add(copy); RefreshLibrary(copy.Id);
        SetStatus($"Duplicated as “{copy.Name}”.", ActivityLevel.Success, "Tests");
    });
    private void DeleteTest_Click(object sender, RoutedEventArgs e) => GuardSync(() =>
    {
        if (_selected == null) return;
        var name = _selected.Name;
        var index = Math.Max(0, TestList.SelectedIndex);
        if (!_externalDeleted)
        {
            var path = TestPath(_selected.Id);
            _watcher?.NoteOwnDelete(path);
            try { Persist(_store.DeleteTest, _selected.Id); }
            catch { _watcher?.ForgetOwnDelete(path); throw; } // the file is still there: an outside delete of it later is not Studio's own
            _testFileIds.Remove(path);
        }
        HideExternalChangeBar(); // whatever the bar asked about this test is moot once it is deleted
        _tests.Remove(_selected); _selected = null;
        _loading = true;
        try { _steps.Clear(); TestName.Text = ""; TestIntent.Text = ""; JsonEditor.Text = ""; _dirty = false; }
        finally { _loading = false; }
        LoadStepEditor(null); ForgetShownRun();
        RefreshLibrary(); if (TestList.Items.Count > 0) TestList.SelectedIndex = Math.Min(index, TestList.Items.Count - 1);
        SetStatus($"Deleted “{name}”.", ActivityLevel.Info, "Tests");
    });
    private void RefreshJson_Click(object sender, RoutedEventArgs e) => GuardSync(() =>
    {
        CommitEditor(false);
        var wasLoading = _loading; _loading = true; // the JSON view mirrors the editor: refreshing it is not an edit
        try { JsonEditor.Text = JsonSerializer.Serialize(_selected, TestyJson.Options); } finally { _loading = wasLoading; }
    });
    private void ApplyJson_Click(object sender, RoutedEventArgs e) => GuardSync(() =>
    {
        if (_selected == null) return;
        var test = JsonSerializer.Deserialize<TestCase>(JsonEditor.Text, TestyJson.Options) ?? throw new InvalidOperationException("The JSON does not contain a test.");
        TestValidator.Validate(test); test.Id = _selected.Id;
        var index = _tests.IndexOf(_selected);
        if (index >= 0) _tests[index] = test; else _tests.Add(test); // deleted outside Studio: applying the JSON keeps the test, as Save does
        Persist(_store.SaveTest, test); _testFileIds[TestPath(test.Id)] = test.Id; HideExternalChangeBar(); _selected = null; RefreshLibrary(test.Id);
        ShowEditorView(EditorView.Json); SetStatus("JSON checked and applied.", ActivityLevel.Success, "Tests");
    });
    /// <summary>More (⋯) in the editor's command row: Import test… and Export test…, in a menu right-aligned under the button.</summary>
    private void MoreActions_Click(object sender, RoutedEventArgs e)
    {
        ExportTestItem.IsEnabled = _selected != null;
        MoreActionsMenu.PlacementTarget = MoreButton; MoreActionsMenu.Placement = PlacementMode.Custom;
        MoreActionsMenu.CustomPopupPlacementCallback = (popup, target, _) => [new CustomPopupPlacement(new Point(target.Width - popup.Width, target.Height), PopupPrimaryAxis.Vertical)];
        MoreActionsMenu.IsOpen = true;
    }
    private void ImportTest_Click(object sender, RoutedEventArgs e) => GuardSync(() =>
    {
        var dialog = new OpenFileDialog { Filter = "Testy test (*.json)|*.json", Title = "Import a test" };
        if (ShowModal(() => dialog.ShowDialog()) != true) return;
        var test = JsonSerializer.Deserialize<TestCase>(File.ReadAllText(dialog.FileName), TestyJson.Options) ?? throw new InvalidOperationException("The file does not contain a test.");
        TestValidator.Validate(test); test.Id = Guid.NewGuid().ToString("N"); CommitEditor(); Persist(_store.SaveTest, test); _tests.Add(test); _testFileIds[TestPath(test.Id)] = test.Id; RefreshLibrary(test.Id);
        SetStatus($"Imported “{test.Name}”.", ActivityLevel.Success, "Tests");
    });
    private void ExportTest_Click(object sender, RoutedEventArgs e) => GuardSync(() =>
    {
        if (_selected == null) return; CommitEditor();
        var dialog = new SaveFileDialog { Filter = "Testy test (*.json)|*.json", FileName = "testy-test.json" };
        if (ShowModal(() => dialog.ShowDialog()) == true) { File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(_selected, TestyJson.Options)); SetStatus("Test exported to " + dialog.FileName, ActivityLevel.Success, "Tests"); }
    });
    /// <summary>Runs a modal dialog; changes the workspace watcher brings for the selected test wait until it has closed, then are applied.</summary>
    private T ShowModal<T>(Func<T> dialog)
    {
        _modalDepth++;
        try { return dialog(); }
        finally { _modalDepth--; }
    }

    // ═══════════════ Connected app ═══════════════
    private async Task RefreshTargets(CancellationToken token = default)
    {
        var selectedPid = (TargetCombo.SelectedItem as TargetInfo)?.ProcessId ?? _driver.Target?.ProcessId;
        var targets = await _driver.GetTargetsAsync(token);
        var choices = targets.Where(t => t.ProcessId != Environment.ProcessId).ToList();
        TargetCombo.ItemsSource = choices;
        TargetCombo.SelectedItem = choices.FirstOrDefault(t => t.ProcessId == selectedPid) ?? choices.FirstOrDefault(t => t.ProcessName.Contains("TestLab"));
    }
    private async void RefreshTargets_Click(object sender, RoutedEventArgs e) => await Guard(() => PerformOperation(RefreshTargets));
    private async void Attach_Click(object sender, RoutedEventArgs e)
    {
        CloseFlyouts();
        await Guard(() => PerformOperation(async token =>
        {
            if (TargetCombo.SelectedItem is not TargetInfo target) throw new InvalidOperationException("Choose an app from the list of open windows first.");
            await Attach(target.ProcessId, token);
            NoteManualConnection(); UpdateRunsIn();
        }));
    }
    private async Task Attach(int pid, CancellationToken token, bool announce = true)
    {
        ITargetDriver next = ProbeCheck.IsChecked == true ? new WpfProbeDriver() : new UiAutomationDriver();
        try { await next.AttachAsync(pid, token); token.ThrowIfCancellationRequested(); }
        catch { next.Dispose(); throw; }
        _driver.Dispose(); _driver = next;
        _snapshot = null; _screenshot = "";
        try { using var process = Process.GetProcessById(pid); _targetExe = process.MainModule?.FileName ?? ""; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { _targetExe = ""; }
        ElementsGrid.ItemsSource = null; ContextStatus.Text = "Looking at the connected app…"; InspectorSummary.Text = "Scanning the connected app…";
        UpdateTargetCard(); UpdateRunsIn(); UpdateGettingStarted(); _automate?.UpdateTarget(pid, _driver.Target?.Title, ProbeCheck.IsChecked == true);
        if (announce) SetStatus($"Connected to {_driver.Target?.Title}.", ActivityLevel.Success, "App");
        await Inspect(token);
    }
    /// <summary>The app card shows the app's name and "Connected"; the process ID and program path appear only with technical details on, and in the tooltip.</summary>
    private void UpdateTargetCard()
    {
        if (_driver.Target is not { } target)
        {
            AppName.Text = "No app connected"; AppInitials.Text = ""; AppGlyph.Visibility = Visibility.Visible; StatusDot.Visibility = Visibility.Collapsed;
            TargetStatus.Text = "Not connected"; TargetPid.Text = ""; AppCard.ToolTip = "No app is connected. Use Change app, or Sample app to try the included Customer Desk.";
            return;
        }
        var name = FriendlyAppName(target.Title);
        AppName.Text = name; AppInitials.Text = Initials(name); AppGlyph.Visibility = Visibility.Collapsed; StatusDot.Visibility = Visibility.Visible;
        TargetStatus.Text = "Connected"; TargetPid.Text = $"· PID {target.ProcessId}" + (_targetExe.Length > 0 ? " · " + Path.GetFileName(_targetExe) : "");
        TargetPid.ToolTip = _targetExe.Length > 0 ? _targetExe : null;
        UpdateAppBarLayout();
        AppCard.ToolTip = $"{target.Title}\nProcess {target.ProcessName}, ID {target.ProcessId}" + (_targetExe.Length > 0 ? "\n" + _targetExe : "");
    }
    private static string FriendlyAppName(string title)
    {
        foreach (var separator in new[] { " — ", " – ", " - " })
        {
            var index = title.LastIndexOf(separator, StringComparison.Ordinal);
            if (index > 0 && index + separator.Length < title.Length) return title[(index + separator.Length)..].Trim();
        }
        return string.IsNullOrWhiteSpace(title) ? "Connected app" : title.Trim();
    }
    private static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).ToArray();
        return words.Length switch { 0 => "?", 1 => char.ToUpperInvariant(words[0][0]).ToString(), _ => $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}" };
    }
    /// <summary>
    /// "Runs in Customer Desk": the app the test stores (Run connects to it or starts it), unless the person connected another app by hand for
    /// this test; for a test that stores no app, the connected app, else "the connected app".
    /// </summary>
    private void UpdateRunsIn()
    {
        string app;
        string tip;
        if (_selected is { } test && HasStoredApp(test))
        {
            var stored = StoredAppName(test);
            var where = !string.IsNullOrWhiteSpace(test.TargetPath) ? test.TargetPath : test.TargetAppId;
            if (_driver.Target is { } connected && ManualOverrideFor(test) && !ConnectedToStoredApp(test))
            {
                app = $"{FriendlyAppName(connected.Title)} (chosen with Change app; the test names {stored})";
                tip = $"You connected {FriendlyAppName(connected.Title)} for this test, so Run uses it. The test's own app is {where}.";
            }
            else
            {
                app = stored;
                tip = $"This test runs in {where}. Run connects to it, or starts it when it isn't running. Connect another app with Change app to run it there instead.";
            }
        }
        else
        {
            app = _driver.Target is { } target ? FriendlyAppName(target.Title) : "the connected app";
            tip = "The app this test acts on";
        }
        RunsInText.Text = "Runs in " + app;
        RunsInText.ToolTip = tip;
    }
    private async void OpenExe_Click(object sender, RoutedEventArgs e)
    {
        CloseFlyouts();
        await Guard(() => PerformOperation(async token =>
        {
            var dialog = new OpenFileDialog { Filter = "Windows apps (*.exe)|*.exe", Title = "Open an app to test" };
            if (ShowModal(() => dialog.ShowDialog()) != true) return;
            using var process = await LaunchAndAttach(dialog.FileName, token);
            if (_selected != null)
            {
                _selected.TargetPath = dialog.FileName; _selected.TargetAppId = "";
                _selected.TargetName = AppDiscovery.FromProgram(dialog.FileName, AppCandidateKind.Installed).Name;
                CommitEditor();
            }
            NoteManualConnection(); UpdateRunsIn();
        }));
    }
    private async Task<Process> LaunchAndAttach(string exe, CancellationToken token, bool announce = true)
    {
        var targetStart = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        CortexModelBridge.ScrubTarget(targetStart);
        var process = Process.Start(targetStart) ?? throw new InvalidOperationException("Windows could not start this app.");
        try
        {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(15))
        {
            await Task.Delay(200, token); process.Refresh();
            if (process.HasExited) throw new InvalidOperationException("The app closed before it opened a window. If it started another program, choose that window under Change app and connect to it.");
            if (process.MainWindowHandle != IntPtr.Zero && IsShown(process.MainWindowHandle)) { await RefreshTargets(token); await Attach(process.Id, token, announce); return process; }
        }
        throw new TimeoutException("The app started but didn't open a window within 15 seconds. Choose it under Change app when it's ready.");
        }
        catch { process.Dispose(); throw; }
    }
    /// <summary>
    /// A new WPF window stays DWM-cloaked until its first frame renders, and WPF briefly shows an untitled helper window while it starts.
    /// Attach only once the process's main window is its visible, uncloaked, titled window (attaching needs a titled window anyway).
    /// </summary>
    private static bool IsShown(nint window) => IsWindowVisible(window) && GetWindowTextLength(window) > 0 && !(DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")] private static extern int GetWindowTextLength(nint window);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    private string FindLab()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var level = 0; dir != null && level < 7; level++, dir = dir.Parent)
        {
            string[] candidates = [Path.Combine(dir.FullName, "Testy.TestLab.exe"), Path.Combine(dir.FullName, "TestLab", "Testy.TestLab.exe"), Path.Combine(dir.FullName, "dist", "TestLab", "Testy.TestLab.exe"), Path.Combine(dir.FullName, "src", "Testy.TestLab", "bin", "Release", "net9.0-windows", "Testy.TestLab.exe"), Path.Combine(dir.FullName, "src", "Testy.TestLab", "bin", "Debug", "net9.0-windows", "Testy.TestLab.exe")];
            foreach (var path in candidates) if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("The sample app (Testy.TestLab.exe) was not found. Run scripts/build.ps1, or choose it with Change app › Open an app….");
    }
    private async void LaunchLab_Click(object sender, RoutedEventArgs e) => await Guard(() => PerformOperation(async token =>
    {
        if (_labProcess != null && !_labProcess.HasExited) { await RefreshTargets(token); await Attach(_labProcess.Id, token, announce: false); }
        else { _labProcess?.Dispose(); _labProcess = await LaunchAndAttach(FindLab(), token, announce: false); }
        NoteManualConnection(); UpdateRunsIn();
        ShowPage(LibraryPage); SetStatus("Connected to the sample app (Customer Desk). The sample tests are ready to run.", ActivityLevel.Success, "App");
    }));
    private async Task Inspect(CancellationToken token = default)
    {
        if (_driver.Target == null) throw new InvalidOperationException("Connect an app first.");
        _snapshot = await _driver.SnapshotAsync(token);
        ElementsGrid.ItemsSource = _snapshot.Elements;
        InspectorSummary.Text = $"{_snapshot.Elements.Count} controls found in {FriendlyAppName(_snapshot.Target.Title)} · scanned {_snapshot.CapturedAt.ToLocalTime():t}"
            + (StudioPreferences.Current.ShowTechnicalDetails ? $" · {_snapshot.Source}" : "");
        ContextStatus.Text = $"{_snapshot.Elements.Count} controls";
        RefreshStepNames();
        var path = Path.Combine(_store.RootDirectory, "inspection", "latest.png"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { _screenshot = await _driver.CaptureAsync(path, token); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _screenshot = ""; SetStatus("No screenshot of the app: " + ex.Message, ActivityLevel.Warning, "App"); }
    }
    private async void Inspect_Click(object sender, RoutedEventArgs e) => await Guard(() => PerformOperation(Inspect));
    private void InspectorClickStep_Click(object sender, RoutedEventArgs e) => AddInspectorStep(StepAction.Click);
    private void InspectorAssertStep_Click(object sender, RoutedEventArgs e) => AddInspectorStep(StepAction.AssertText);
    private void AddInspectorStep(StepAction action)
    {
        if (_busy) return;
        if (ElementsGrid.SelectedItem is not UiElementInfo element || _selected == null) { SetStatus("Select a control here and a test in Tests first."); return; }
        var hasTextValue = element.ControlType is "Edit" or "TextBox" or "ComboBox" or "Document";
        InsertStep(Step(action == StepAction.Click ? $"Click {element.Name}" : $"Verify {element.Name}", action, element.Selector, action == StepAction.AssertText ? (hasTextValue || element.Value.Length > 0 ? element.Value : element.Name) : ""));
        ShowPage(LibraryPage); ShowEditorView(EditorView.Steps);
    }
    private void CopySelector_Click(object sender, RoutedEventArgs e) => GuardSync(() => { if (ElementsGrid.SelectedItem is UiElementInfo element) { Clipboard.SetText(element.Selector); SetStatus("Control ID copied."); } });
    /// <summary>Opens the latest screenshot of the connected app (taken by the last scan).</summary>
    private void InspectorEvidence_Click(object sender, RoutedEventArgs e) => TryShell(() =>
    {
        if (string.IsNullOrEmpty(_screenshot)) throw new InvalidOperationException("Scan the connected app first; the screenshot is taken with each scan.");
        OpenPath(_screenshot);
    });

    // ═══════════════ Run ═══════════════
    private async void RunTest_Click(object sender, RoutedEventArgs e) => await RunSelectedAsync(null);
    /// <summary>
    /// Runs the selected test. A test that stores its app runs there: Studio connects to a running instance, or starts the app, unless the person
    /// connected another app by hand for this test (Change app stays an override). <paramref name="chosen"/> is the instance picked in AppChoiceBar.
    /// </summary>
    private async Task RunSelectedAsync(AppCandidate? chosen) => await Guard(async () =>
    {
        if (_busy) return;
        if (_selected == null) throw new InvalidOperationException("Create or select a test first.");
        var stored = HasStoredApp(_selected) && !ManualOverrideFor(_selected);
        if (_driver.Target == null && !stored) throw new InvalidOperationException("Connect the app to test first (Change app, or Sample app).");
        CommitEditor(); TestValidator.Validate(_selected); var test = TestyJson.Clone(_selected);
        HideAppChoice();
        // The app the test stores: already connected, one running instance, a choice between several, or the program to start.
        AppCandidate? connectTo = chosen;
        if (chosen is null && stored && !ConnectedToStoredApp(test))
        {
            var instances = AppDiscovery.RunningInstances(test.TargetPath, test.TargetAppId, [Environment.ProcessId]);
            if (instances.Count > 1)
            {
                ShowAppChoice($"{instances.Count} windows of {StoredAppName(test)} are open. Choose the one to run “{test.Name}” in.", instances, candidate => RunSelectedAsync(candidate));
                return;
            }
            connectTo = instances.Count == 1 ? instances[0] : StoredAppCandidate(test)
                ?? throw new InvalidOperationException(string.IsNullOrWhiteSpace(test.TargetAppId) && !ExecutableRules.IsLocalDrivePath(test.TargetPath)
                    ? $"“{test.Name}” names its app by a path that is not on a local drive ({test.TargetPath}), so Testy does not open it. Connect the app with Change app, or open it from a local drive with Open an app…."
                    : $"“{test.Name}” runs in {StoredAppName(test)} ({test.TargetPath}), which is not running and is not installed at that path. Connect the app with Change app to run the test there.");
        }
        // The same desktop lease the background agent's local jobs hold: two workers never send input to this desktop at once.
        using var desktopLease = OperationsDesktopLease.TryAcquire()
            ?? throw new InvalidOperationException("The Testy background agent or another worker is running a test on this PC. Try again when it finishes, or pause background runs in Automate.");
        using var desktopExecution = desktopLease.EnterExecution();
        StartOperation();
        if (connectTo is not null)
        {
            try
            {
                var started = await ConnectToAppAsync(connectTo, $"to run “{test.Name}”", _operation!.Token);
                Log(ActivityLevel.Info, "Run", $"“{test.Name}” runs in {(connectTo.Name.Length > 0 ? connectTo.Name : StoredAppName(test))} ({(started ? "started by Testy" : "already running")}, process {_driver.Target?.ProcessId}).");
            }
            catch { EndOperation(); throw; }
        }
        else if (HasStoredApp(test) && !ConnectedToStoredApp(test))
            Log(ActivityLevel.Warning, "Run", $"“{test.Name}” runs in {FriendlyAppName(_driver.Target!.Title)}, chosen with Change app, instead of its own app {StoredAppName(test)}.");
        else if (HasStoredApp(test))
            Log(ActivityLevel.Info, "Run", $"“{test.Name}” runs in {StoredAppName(test)} (process {_driver.Target?.ProcessId}).");
        var mode = _settings.AiDirectedExecution ? "AI-guided" : "exact replay";
        BeginLiveRun(test);
        SetStatus($"Running “{test.Name}” ({mode})…", ActivityLevel.Info, "Run");
        var context = new Progress<RunProgress>(OnRunProgress);
        try
        {
            var runner = new TestExecutionService(_driver, _settings, Path.Combine(_store.RootDirectory, "artifacts"), new NativeComputerActionExecutor(_driver));
            if (!_settings.AiDirectedExecution && _settings.LiveReview && _settings.Kind != ProviderKind.Offline)
            {
                var livePlanner = PlannerFactory.Create(_settings, Path.Combine(_store.RootDirectory, "ai"));
                runner.StepObserver = async (observedRun, token) =>
                {
                    await Dispatcher.InvokeAsync(() => SetStatus($"The AI is commenting on step {observedRun.Steps.Count}…", ActivityLevel.Info, "Assistant"));
                    var review = await livePlanner.ExplainAsync(observedRun, token);
                    observedRun.AiAnalysis += $"\nStep {observedRun.Steps.Count} review:\n{review}\n";
                    await Dispatcher.InvokeAsync(() => Log(ActivityLevel.Info, "Assistant", $"{livePlanner.Name} on step {observedRun.Steps.Count}: {review}"));
                };
            }
            var finished = await runner.RunAsync(test, context, _operation!.Token);
            _lastRun = finished; Persist(_store.SaveRun, finished); IndexRun(finished);
            EndLiveRun(_lastRun);
            RefreshLibrary(_selected?.Id);
            SetStatus($"Run finished: {test.Name}. {StepText.StatusWord(_lastRun.Status)}: {ResultSentence(_lastRun, withWord: false, forLog: true)}",
                _lastRun.Status switch { RunStatus.Passed => ActivityLevel.Success, RunStatus.Failed => ActivityLevel.Error, _ => ActivityLevel.Warning }, "Run", runId: _lastRun.Id);
            if (!string.IsNullOrWhiteSpace(_lastRun.AiAnalysis)) Log(ActivityLevel.Info, "Assistant", "AI notes were saved with the run.", _lastRun.Id);
            if (_lastRun.Status == RunStatus.Failed && string.IsNullOrWhiteSpace(_lastRun.AiAnalysis) && !_operation.IsCancellationRequested)
            {
                SetStatus("Explaining the failed step from what Testy saw…", ActivityLevel.Info, "Assistant");
                try { await ExplainRun(_lastRun, _operation.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { Log(ActivityLevel.Warning, "Assistant", "No explanation available: " + ex.Message, _lastRun.Id); }
            }
        }
        finally { EndOperation(); if (_shownRunLive) AbandonLiveRun(); _activeRunId = null; _pendingRunEntries = null; UpdateGettingStarted(); if (_lastRunStale) RefreshLastRunFromDisk(); }
    });
    private void Stop_Click(object sender, RoutedEventArgs e) { _operation?.Cancel(); SetStatus("Stopping safely at the next step…", ActivityLevel.Warning, "Run"); }
    /// <summary>Reloads Results tolerantly (an unreadable run file is reported and left out) and keeps the selected row, so an outside run does not pull a row from under the person.</summary>
    private void RefreshRunList()
    {
        var selectedId = (RunsGrid.SelectedItem as RunResult)?.Id;
        var offset = FindChild<ScrollViewer>(RunsGrid)?.VerticalOffset ?? 0;
        var runs = LoadRunsTolerant();
        RunsGrid.ItemsSource = runs;
        _runIndex = runs.Select(RunHeader.From).ToList();
        if (selectedId is not null && runs.FirstOrDefault(r => r.Id == selectedId) is { } still) RunsGrid.SelectedItem = still;
        if (offset > 0) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (FindChild<ScrollViewer>(RunsGrid) is { } scroll) scroll.ScrollToVerticalOffset(Math.Min(offset, scroll.ScrollableHeight)); });
    }
    private void IndexRun(RunResult run) { _runIndex.RemoveAll(r => r.Id == run.Id); _runIndex.Add(RunHeader.From(run)); }
    private void RefreshRuns_Click(object sender, RoutedEventArgs e) => GuardSync(RefreshRunList);
    private void ViewRun_Click(object sender, RoutedEventArgs e) => GuardSync(ViewSelectedRun);
    private void RunsGrid_DoubleClick(object sender, MouseButtonEventArgs e) => GuardSync(ViewSelectedRun);
    /// <summary>Opens the selected run in Tests, on Last run (the same view a live run uses).</summary>
    private void ViewSelectedRun()
    {
        if (_busy) { SetStatus("Wait for the current operation before opening another run."); return; }
        if (RunsGrid.SelectedItem is not RunResult run) return;
        var test = _tests.FirstOrDefault(t => t.Id == run.TestId); if (test != null) { CommitEditor(); RefreshLibrary(test.Id); }
        if (!ShowPage(LibraryPage)) return;
        ShowRun(run, forTestId: _selected?.Id, pinned: true); ShowEditorView(EditorView.LastRun);
        if (test == null) ShowEditorMessage($"The test “{run.TestName}” no longer exists. Its run is shown here.", "Warning");
        Log(ActivityLevel.Info, "Results", $"Opened the run of “{run.TestName}” from {FriendlyTimeConverter.Format(run.StartedAt)}.", run.Id);
    }
    private void OpenReport_Click(object sender, RoutedEventArgs e) => GuardSync(() =>
    {
        var run = RunsGrid.SelectedItem as RunResult ?? _shownRun ?? _lastRun;
        if (run == null) throw new InvalidOperationException("Select a run first.");
        OpenRunReport(run);
    });
    private void OpenRunReport(RunResult run)
    {
        var directory = DisplayPath(run.ArtifactDirectory);
        var html = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.html").FirstOrDefault() : null;
        OpenPath(html ?? directory);
    }
    private string DisplayPath(string path) => string.IsNullOrEmpty(path) ? path : WorkspaceMaintenance.ResolveRestoredPath(_store.RootDirectory, path);
    private void OpenWorkspace_Click(object sender, RoutedEventArgs e) => TryShell(() => { CloseFlyouts(); OpenPath(_store.RootDirectory); });
    private static void OpenPath(string path) { if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("The file or folder is no longer available.", path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }

    // ═══════════════ Assistant: create, improve, explain ═══════════════
    private async void Generate_Click(object sender, RoutedEventArgs e) => await Plan(false);
    private async void Refine_Click(object sender, RoutedEventArgs e) => await Plan(true);
    private void Improve_Click(object sender, RoutedEventArgs e)
    {
        if (ImproveFlyout.IsOpen) { CloseFlyouts(); return; }
        OpenFlyout(ImproveFlyout);
        ImproveFlyout.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { ImprovePrompt.Focus(); ImprovePrompt.SelectAll(); });
    }
    private async Task Plan(bool refine, AppCandidate? chosenApp = null) => await Guard(async () =>
    {
        if (_busy) return;
        var request = refine ? ImprovePrompt.Text.Trim() : PromptBox.Text.Trim();
        if (!refine && request.Length == 0) throw new InvalidOperationException("Describe what a user does in your app, then choose Create test.");
        if (refine && request.Length == 0) request = "Review this test against the connected app and its last run. Fix controls or values that are wrong; keep its purpose and every check.";
        // A new test's description may name its app ("In Customer Desk: …"): resolve it, and connect to it (starting it if needed) before planning.
        var mention = refine ? null : AppResolver.FindMention(request);
        var app = refine ? null : chosenApp;
        var planned = request;
        if (!refine && app is null && mention is not null)
        {
            var candidates = await DiscoverAppsAsync();
            var resolution = AppResolver.Resolve(mention.App, candidates, AppResolvePurpose.Attach);
            var connectedPid = ConnectionAlive() ? _driver.Target!.ProcessId : (int?)null;
            if (resolution.Status == AppResolutionStatus.Ambiguous && Contenders(resolution).Count(c => c.ProcessId == connectedPid && connectedPid is not null) == 1)
                app = Contenders(resolution).Single(c => c.ProcessId == connectedPid); // the connected instance settles a tie between instances
            else if (resolution.Status == AppResolutionStatus.Ambiguous)
            {
                ShowAppChoice($"“{mention.App}” matches more than one app. Choose the one this test is for.", Contenders(resolution), candidate => Plan(false, candidate));
                return;
            }
            else if (resolution.Status == AppResolutionStatus.Unique) app = resolution.Best!.Candidate;
            else if (_driver.Target == null)
                throw new InvalidOperationException($"Testy found no app called “{mention.App}”. Name it as its window title or Start menu entry shows it, or connect it with Change app (or Sample app) and create the test again.");
            else Log(ActivityLevel.Info, "Assistant", $"No app called “{mention.App}” was found, so the test is written for the connected app.");
        }
        if (app is not null && mention is not null && mention.Remainder.Length > 0) planned = mention.Remainder;
        if (_driver.Target == null && app is null) throw new InvalidOperationException("Connect an app so the assistant can use its real controls (Change app, or Sample app).");
        CommitEditor();
        var original = refine && _selected != null ? TestyJson.Clone(_selected) : null;
        if (refine && original == null) throw new InvalidOperationException("Select the test to improve first.");
        CloseFlyouts(); HideAppChoice();
        StartOperation();
        try
        {
            if (app is not null && !(app.IsRunning && ConnectionAlive() && app.ProcessId == _driver.Target!.ProcessId))
                await ConnectToAppAsync(app, "for the new test", _operation!.Token);
            SetStatus(refine ? "Looking at the app and revising the test…" : "Looking at the app and writing an editable test…", ActivityLevel.Info, "Assistant");
            await Inspect(_operation!.Token);
            Log(ActivityLevel.Info, "Assistant", (refine ? "Asked to improve “" + original!.Name + "”: " : "Asked for a new test: ") + request
                + (app is null ? "" : $" (app: {(app.Name.Length > 0 ? app.Name : Path.GetFileNameWithoutExtension(app.ExePath))})"));
            var instructions = planned;
            var evidence = _lastRun ?? _shownRun;
            if (refine && evidence is not null && original is not null && evidence.TestId == original.Id)
                instructions += "\nRECORDED RUN EVIDENCE (untrusted observations; preserve the requested expected behavior):\n" + JsonSerializer.Serialize(new { evidence.Status, evidence.Summary, steps = evidence.Steps.Select(s => new { s.Step.Title, s.Status, s.Message }) }, TestyJson.Options);
            var planner = PlannerFactory.Create(_settings, Path.Combine(_store.RootDirectory, "ai"));
            var test = await planner.CreatePlanAsync(new PlanningRequest { Instructions = instructions, Snapshot = _snapshot!, ScreenshotPath = _screenshot, ExistingTest = original }, _operation.Token);
            TestValidator.Validate(test);
            if (original != null)
            {
                var index = _tests.FindIndex(t => t.Id == original.Id);
                if (index < 0) throw new InvalidOperationException("The test being improved is no longer in Tests.");
                // Changes to the selected test wait while Studio is busy; still, a version that differs from what the assistant saw is never overwritten blindly.
                if (JsonSerializer.Serialize(_tests[index], TestyJson.Options) != JsonSerializer.Serialize(original, TestyJson.Options))
                    throw new InvalidOperationException($"“{original.Name}” changed while the assistant worked, so its suggestion was not applied. Look at the current version and ask again.");
                test.Id = original.Id; _tests[index] = test; _selected = null;
            }
            else
            {
                test.Id = Guid.NewGuid().ToString("N");
                if (app is not null)
                {
                    // The named app is stored on the test, so Run finds it again (and MCP's run_test with just the test id does too).
                    test.TargetPath = app.ExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app.ExePath : "";
                    test.TargetAppId = app.Packaged ? app.AppId : "";
                    test.TargetName = app.Name.Length > 0 ? app.Name : Path.GetFileNameWithoutExtension(app.ExePath);
                }
                _tests.Add(test);
            }
            Persist(_store.SaveTest, test); RefreshLibrary(test.Id); ShowEditorView(EditorView.Steps);
            if (refine) ImprovePrompt.Text = "";
            var reply = refine ? $"Improved “{test.Name}”. It now has {StepText.Count(test.Steps.Count, "step")}. Review them, then run."
                : $"Created “{test.Name}” with {StepText.Count(test.Steps.Count, "step")}. Review them, then run.";
            ShowEditorMessage(reply, "Success");
            SetStatus(reply, ActivityLevel.Success, "Assistant");
        }
        finally { EndOperation(); }
    });
    private async void Analyze_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (_busy) return;
        var run = _shownRun ?? _lastRun ?? throw new InvalidOperationException("Run a test first, or open a run from Results.");
        if (run.FinishedAt is null) throw new InvalidOperationException("Wait for the run to finish before asking for an explanation.");
        StartOperation();
        try { SetStatus("Reading the run's screenshots and results…", ActivityLevel.Info, "Assistant"); await ExplainRun(run, _operation!.Token, showNotes: true); }
        finally { EndOperation(); }
    });
    /// <summary>Asks the configured AI (or the offline summary) to explain the run and appends the answer to the run's AI notes.</summary>
    private async Task ExplainRun(RunResult run, CancellationToken token, bool showNotes = false)
    {
        var planner = PlannerFactory.Create(_settings, Path.Combine(_store.RootDirectory, "ai"));
        var analysis = (await planner.ExplainAsync(TestyJson.Clone(run), token)).Trim();
        run.AiAnalysis = string.IsNullOrWhiteSpace(run.AiAnalysis) ? analysis : run.AiAnalysis.TrimEnd() + Environment.NewLine + Environment.NewLine + analysis;
        Persist(_store.SaveRun, run);
        if (DisplayPath(run.ArtifactDirectory) == run.ArtifactDirectory && Directory.Exists(run.ArtifactDirectory)) TestRunner.WriteReports(run);
        if (ReferenceEquals(run, _shownRun) || run.Id == _shownRun?.Id) { if (_shownRun != null) _shownRun.AiAnalysis = run.AiAnalysis; UpdateAiNotes(); if (showNotes) AiNotesExpander.IsExpanded = true; }
        Log(ActivityLevel.Success, "Assistant", $"{planner.Name} explained the run: {analysis}", run.Id);
        SetStatus("Explanation added to the run's AI notes.", ActivityLevel.Success, "Assistant");
    }

    // ═══════════════ Settings ═══════════════
    private void LoadConnectionFields()
    {
        ProviderCombo.SelectedItem = _settings.Kind; ModelBox.Text = _settings.Model; EndpointBox.Text = _settings.Endpoint;
        KeyEnvironmentBox.Text = _settings.ApiKeyEnvironmentVariable; CodexPathBox.Text = _settings.CodexExecutable; LiveReviewCheck.IsChecked = _settings.LiveReview;
        AiDirectedCheck.IsChecked = _settings.AiDirectedExecution; NativeUseCheck.IsChecked = _settings.NativeComputerUse;
        ImagesCheck.IsChecked = _settings.SupportsImages; MaxTurnsBox.Text = _settings.MaximumAgentTurns.ToString();
        if (CortexModelBridge.Enabled)
        {
            foreach (FrameworkElement control in new FrameworkElement[] { ProviderCombo, ModelBox, EndpointBox, KeyEnvironmentBox, CodexPathBox, NativeUseCheck, CredentialBox })
            {
                DependencyObject? item = control;
                while (item is not null && item is not Border) item = LogicalTreeHelper.GetParent(item);
                if (item is Border card) card.Visibility = Visibility.Collapsed;
            }
        }
        UpdateRunModeTexts();
        UpdateProviderHelp();
    }
    /// <summary>The Run split button's label and menu, and the composer's hint, follow the saved connection.</summary>
    private void UpdateRunModeTexts()
    {
        RunModeText.Text = _settings.AiDirectedExecution ? "AI-guided" : "Exact replay";
        RunButton.ToolTip = _settings.AiDirectedExecution ? "Run (F5). The AI chooses each action from what is on screen." : "Run (F5). Exact replay of the saved steps, without AI decisions.";
        RunModeAiItem.IsChecked = _settings.AiDirectedExecution; RunModeReplayItem.IsChecked = !_settings.AiDirectedExecution;
        ComposerHelp.Text = _settings.Kind == ProviderKind.Offline
            ? "Offline (no AI): one command per line, such as click id:AddCustomer. You review the steps before running."
            : $"{(_settings.Kind == ProviderKind.Codex ? "Codex" : "The AI")} writes the steps from the connected app. You review them before running.";
    }
    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (ProviderHelp != null) UpdateProviderHelp(); }
    private void UpdateProviderHelp()
    {
        if (CortexModelBridge.Enabled) { ProviderHelp.Text = "Cortex manages the model for this Testy session. Change the selected model in Cortex; new AI requests use that selection. Model credentials remain in Cortex."; return; }
        ProviderHelp.Text = ProviderCombo.SelectedItem switch
        {
            ProviderKind.Codex => "Codex uses the Codex program installed on this PC and your existing sign-in; no API key is needed. In AI-guided runs the AI chooses one action at a time, sees the result, and decides again. Created tests stay editable before you run them.",
            ProviderKind.OpenAI => "OpenAI uses the Responses API with a supported model. OpenAI computer-use mode sends real Windows input; turn it off to use Testy's control tools. Enter the server URL, the model and an API key (saved below, or from an environment variable).",
            ProviderKind.Compatible => "Compatible works with any Chat Completions server, such as OpenRouter, LM Studio or Ollama, and uses Testy's control tools. Turn off screenshots for text-only models. Enter the server URL, the model and, if needed, a key.",
            _ => "Offline is not an AI. It turns simple commands into steps, one per line:\nclick id:AddCustomer\ntype \"Ada\" into id:CustomerName\nassert text id:StatusMessage = \"Customer added: Ada\"\nChoose Codex to describe tests in everyday language."
        };
    }
    private void SaveSettings_Click(object sender, RoutedEventArgs e) => GuardSync(() =>
    {
        if (_busy) throw new InvalidOperationException("Wait for the current operation to finish before changing the AI service.");
        var settings = ReadConnectionSettings();
        Persist(_store.SaveSettings, settings); _settings = settings; LoadConnectionFields();
        SetStatus($"Settings saved: {settings.Kind}, {(settings.AiDirectedExecution ? "AI-guided runs" : "exact replay")}.", ActivityLevel.Success, "Settings");
        UpdateGettingStarted();
    });
    private ProviderSettings ReadConnectionSettings()
    {
        var settings = TestyJson.Clone(_settings);
        settings.Kind = ProviderCombo.SelectedItem is ProviderKind k ? k : ProviderKind.Codex;
        settings.Model = ModelBox.Text.Trim(); settings.Endpoint = EndpointBox.Text.Trim(); settings.ApiKeyEnvironmentVariable = KeyEnvironmentBox.Text.Trim();
        settings.CodexExecutable = CodexPathBox.Text.Trim(); settings.LiveReview = LiveReviewCheck.IsChecked == true;
        settings.AiDirectedExecution = AiDirectedCheck.IsChecked == true; settings.NativeComputerUse = NativeUseCheck.IsChecked == true;
        settings.SupportsImages = ImagesCheck.IsChecked == true;
        if (!int.TryParse(MaxTurnsBox.Text, out var turns) || turns < 1 || turns > AgentLimits.Maximum(settings))
            throw new InvalidOperationException($"Most AI actions per run must be between 1 and {AgentLimits.Maximum(settings)} for this service and mode.");
        settings.MaximumAgentTurns = turns;
        if (settings.Kind == ProviderKind.OpenAI && settings.AiDirectedExecution && settings.NativeComputerUse && !settings.SupportsImages)
            throw new InvalidOperationException("OpenAI computer-use mode needs screenshots. Turn on Send screenshots to the AI, or turn off computer-use mode.");
        return settings;
    }
    private void RunMode_Click(object sender, RoutedEventArgs e)
    {
        UpdateRunModeTexts();
        RunModeMenu.PlacementTarget = RunModeButton; RunModeMenu.Placement = PlacementMode.Bottom; RunModeMenu.IsOpen = true;
    }
    private void RunModeAi_Click(object sender, RoutedEventArgs e) => SetRunMode(true);
    private void RunModeReplay_Click(object sender, RoutedEventArgs e) => SetRunMode(false);
    /// <summary>
    /// Changes the saved connection's AiDirectedExecution exactly as Settings › Save would (the stored connection, the store's SaveSettings),
    /// and shows the same value on the Settings page. Other unsaved Settings fields are left as they are.
    /// </summary>
    private void SetRunMode(bool aiGuided) => GuardSync(() =>
    {
        if (_settings.AiDirectedExecution == aiGuided) { UpdateRunModeTexts(); return; }
        if (aiGuided && _settings.Kind == ProviderKind.Offline)
            throw new InvalidOperationException("AI-guided runs need an AI service. Choose one in Settings (Codex, OpenAI or Compatible), or keep Exact replay.");
        var settings = TestyJson.Clone(_settings);
        settings.AiDirectedExecution = aiGuided;
        Persist(_store.SaveSettings, settings); _settings = settings;
        AiDirectedCheck.IsChecked = aiGuided;
        UpdateRunModeTexts(); UpdateGettingStarted();
        SetStatus(aiGuided ? "Runs are AI-guided: the AI chooses each action from what is on screen." : "Runs are an exact replay of the saved steps.", ActivityLevel.Success, "Settings");
    });
    private void StoreCredential_Click(object sender, RoutedEventArgs e) => GuardSync(() =>
    {
        var settings = ReadConnectionSettings();
        Persist(_store.SaveSettings, settings);
        try { ProviderCredentialStore.Save(settings, CredentialBox.Password); }
        finally { CredentialBox.Clear(); }
        _settings = settings; ConnectionStatus.Text = "Key saved in Windows Credential Manager for this server URL."; LoadConnectionFields();
        Log(ActivityLevel.Success, "Settings", ConnectionStatus.Text);
    });
    private void RemoveCredential_Click(object sender, RoutedEventArgs e) => GuardSync(() =>
    {
        ProviderCredentialStore.Delete(ReadConnectionSettings()); CredentialBox.Clear();
        ConnectionStatus.Text = "Saved key removed. An environment variable may still provide a key.";
        Log(ActivityLevel.Info, "Settings", ConnectionStatus.Text);
    });
    private async void TestConnection_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (_busy) return;
        var settings = ReadConnectionSettings();
        StartOperation();
        try
        {
            ConnectionStatus.Text = "Checking that the AI answers correctly…";
            var result = await ProviderConnectionCheck.RunAsync(settings, Path.Combine(_store.RootDirectory, "connection-checks"), _operation!.Token);
            ConnectionStatus.Text = (result.Passed ? "Works. " : "Didn't work. ") + result.Message;
            SetStatus(ConnectionStatus.Text, result.Passed ? ActivityLevel.Success : ActivityLevel.Error, "Settings");
        }
        finally { EndOperation(); }
    });

    // ═══════════════ Automate (in-window page) ═══════════════
    /// <summary>Builds a fresh Automate page from disk each time you enter it (the former modal "Workflows and jobs" window).</summary>
    private void Workflows_Click(object sender, RoutedEventArgs e) => GuardSync(() =>
    {
        if (_automate != null) { ShowPage(AutomateHost); return; }
        CommitEditor(); Persist(_store.SaveSettings, _settings);
        // Automate saves through Persist too, so its suite runs and recorded drafts are Studio's own writes for the workspace watcher.
        _automate = new AutomatePage(_store, _selected, _driver.Target?.ProcessId, _driver.Target?.Title, ProbeCheck.IsChecked == true, () => _busy, AutomateReport,
            run => Persist(_store.SaveRun, run), test => Persist(_store.SaveTest, test));
        AutomateHost.Child = _automate;
        ShowPage(AutomateHost);
    });
    /// <summary>Automate's results and errors go to the activity log (its own status bar shows them on the page).</summary>
    private void AutomateReport(string message, string severity) =>
        Log(severity switch { "Error" => ActivityLevel.Error, "Warning" => ActivityLevel.Warning, "Success" => ActivityLevel.Success, _ => ActivityLevel.Info }, "Automate", message);
    /// <summary>Record: Automate's Record tab, with the selected test's checks.</summary>
    private void Record_Click(object sender, RoutedEventArgs e)
    {
        Workflows_Click(sender, e);
        if (_automate != null && AutomateHost.Visibility == Visibility.Visible) _automate.SelectTab("WorkflowTabDemonstrations");
    }
    /// <summary>Leaving Automate detaches the page (stopping its refresh), then reloads what it may have changed, as after the old modal window closed.</summary>
    private bool LeaveAutomate()
    {
        var page = _automate!;
        if (page.IsBusy) { page.RequestStop(); SetStatus("Automate is stopping its current operation safely. Try again when it finishes.", ActivityLevel.Warning, "Automate"); return false; }
        page.Leave(); AutomateHost.Child = null; _automate = null;
        _settings = _store.LoadSettings(); LoadConnectionFields(); _tests = LoadTestsTolerant();
        RefreshRunList(); RefreshLibrary(_selected?.Id);
        UpdateGettingStarted();
        return true;
    }

    // ═══════════════ Busy state ═══════════════
    private void StartOperation()
    {
        if (_busy) throw new InvalidOperationException("Wait for the current operation to finish, or stop it first.");
        if (_automate?.IsBusy == true) throw new InvalidOperationException("Wait for the Automate operation to finish, or stop it first.");
        _busy = true; _operation = new CancellationTokenSource();
        RunButton.IsEnabled = false; RunModeButton.IsEnabled = false; GenerateButton.IsEnabled = false; RefineButton.IsEnabled = false; AnalyzeButton.IsEnabled = false; AttachButton.IsEnabled = false; StopButton.IsEnabled = true;
        SetEditorEnabled(false); ApplyEditorLayout();
    }
    private void EndOperation()
    {
        _busy = false; _operation?.Dispose(); _operation = null;
        RunButton.IsEnabled = true; RunModeButton.IsEnabled = true; GenerateButton.IsEnabled = true; RefineButton.IsEnabled = true; AnalyzeButton.IsEnabled = true; AttachButton.IsEnabled = true; StopButton.IsEnabled = false;
        SetEditorEnabled(true); ApplyEditorLayout();
        if (_lastRunStale) RefreshLastRunFromDisk(); // a run of the selected test arrived from outside while Studio was busy
    }
    private void SetEditorEnabled(bool enabled)
    {
        TestList.IsEnabled = enabled; SearchTests.IsEnabled = enabled; TargetCombo.IsEnabled = enabled; ProbeCheck.IsEnabled = enabled;
        TestName.IsReadOnly = !enabled; TestIntent.IsReadOnly = !enabled; StepsGrid.IsReadOnly = !enabled;
        StepTimeout.IsReadOnly = !enabled; JsonEditor.IsReadOnly = !enabled; PromptBox.IsReadOnly = !enabled; ImprovePrompt.IsReadOnly = !enabled; SettingsPage.IsEnabled = enabled;
        StepActionPicker.IsEnabled = enabled; StepControlPicker.IsEnabled = enabled; StepValueBox.IsReadOnly = !enabled; StepSelectorBox.IsReadOnly = !enabled;
    }
    private async Task PerformOperation(Func<CancellationToken, Task> operation)
    {
        StartOperation(); try { await operation(_operation!.Token); } finally { EndOperation(); }
    }

    // ═══════════════ Navigation and views ═══════════════
    private void LibraryNav_Click(object sender, RoutedEventArgs e) => ShowPage(LibraryPage);
    private void RunsNav_Click(object sender, RoutedEventArgs e) { if (_automate != null && !LeaveAutomate()) return; GuardSync(RefreshRunList); ShowPage(RunsPage); }
    private async void InspectorNav_Click(object sender, RoutedEventArgs e) { if (!ShowPage(InspectorPage)) return; if (_driver.Target != null && !_busy) await Guard(() => PerformOperation(Inspect)); }
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => ShowPage(SettingsPage);
    private bool ShowPage(FrameworkElement page)
    {
        if (page != AutomateHost && _automate != null && !LeaveAutomate()) return false;
        CloseFlyouts();
        foreach (var view in new FrameworkElement[] { LibraryPage, RunsPage, InspectorPage, SettingsPage, AutomateHost }) view.Visibility = view == page ? Visibility.Visible : Visibility.Collapsed;
        return true;
    }
    private void EditorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switchingView || !IsInitialized || StepsView is null || !ReferenceEquals(e.OriginalSource, EditorTabs)) return;
        ShowEditorView(EditorTabs.SelectedItem == LastRunTab ? EditorView.LastRun : EditorTabs.SelectedItem == JsonTab ? EditorView.Json : EditorView.Steps);
    }
    /// <summary>Shows the Steps, Last run or JSON view and lays out the editor header for it.</summary>
    private void ShowEditorView(EditorView view)
    {
        if (view == EditorView.Json && !StudioPreferences.Current.ShowTechnicalDetails) view = EditorView.Steps;
        _view = view;
        _switchingView = true;
        try { EditorTabs.SelectedItem = view switch { EditorView.LastRun => LastRunTab, EditorView.Json => JsonTab, _ => StepsTab }; }
        finally { _switchingView = false; }
        StepsView.Visibility = view == EditorView.Steps ? Visibility.Visible : Visibility.Collapsed;
        LastRunView.Visibility = view == EditorView.LastRun ? Visibility.Visible : Visibility.Collapsed;
        JsonView.Visibility = view == EditorView.Json ? Visibility.Visible : Visibility.Collapsed;
        if (view == EditorView.LastRun) EnsureShownRun();
        ApplyEditorLayout();
    }
    /// <summary>
    /// Steps and JSON: getting started, the name as a heading, the purpose, "Runs in" and the command row, then the views.
    /// Last run: one compact row (name, views, Save, Run again and Stop while running), then the result InfoBar.
    /// </summary>
    private void ApplyEditorLayout()
    {
        var compact = _view == EditorView.LastRun;
        Place(TestName, 2, 0, compact ? 1 : 4);
        TestName.FontSize = compact ? 20 : 26; TestName.MaxWidth = compact ? 420 : double.PositiveInfinity; TestName.Margin = new Thickness(-7, 0, compact ? 8 : -7, 0);
        TestIntent.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        MetaLine.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CommandMiddle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        Place(RunControls, compact ? 2 : 5, compact ? 3 : 0, 1); RunControls.Margin = compact ? new Thickness(8, 0, 0, 0) : new Thickness(0, 14, 0, 6);
        Place(CommandRight, compact ? 2 : 5, compact ? 2 : 3, 1); CommandRight.Margin = compact ? new Thickness(12, 0, 0, 0) : new Thickness(12, 14, 0, 6);
        foreach (var button in new UIElement[] { ImproveButton, DuplicateButton, DeleteButton, MoreButton }) button.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        Place(EditorTabs, compact ? 2 : 6, compact ? 1 : 0, compact ? 1 : 4); EditorTabs.Margin = compact ? new Thickness(4, 0, 0, 0) : new Thickness(-12, 4, 0, 0);
        EditorTabs.VerticalAlignment = compact ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
        Place(ViewsDivider, compact ? 2 : 6, 0, 4);
        RunResultBar.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        RunVerbText.Text = compact && _shownRun != null ? "Run again" : "Run"; RunModeText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = compact && !_busy ? Visibility.Collapsed : Visibility.Visible;
        UpdateGettingStarted();
        static void Place(UIElement element, int row, int column, int span) { Grid.SetRow(element, row); Grid.SetColumn(element, column); Grid.SetColumnSpan(element, span); }
    }
    private void Preferences_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StudioPreferences.ShowTechnicalDetails)) return;
        if (!StudioPreferences.Current.ShowTechnicalDetails && _view == EditorView.Json) ShowEditorView(EditorView.Steps);
        LoadStepEditor(StepsGrid.SelectedItem as StepRow);
        UpdateScreenshotViewer(); UpdateAppBarLayout();
        Log(ActivityLevel.Info, "App", StudioPreferences.Current.ShowTechnicalDetails ? "Technical details shown." : "Technical details hidden.");
    }
    /// <summary>Below 1280 DIP the pane shows icons only (the items stay; their labels move to tooltips), like NavigationView's compact mode.
    /// Below 1440 DIP (1600 with technical details on and an app connected) the app bar's commands show icons only as well.</summary>
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        CloseFlyouts();
        var compact = ActualWidth < 1280;
        if (compact != _compactNav)
        {
            _compactNav = compact; NavColumn.Width = new GridLength(compact ? 56 : 240);
            foreach (var item in new[] { LibraryNav, RunsNav, WorkflowsNav, HelpNav, SettingsNav })
            {
                item.ApplyTemplate(); item.Padding = new Thickness(compact ? 16 : 12, 0, 12, 0);
                if (item.Template.FindName("Label", item) is UIElement label) label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        UpdateAppBarLayout();
    }
    /// <summary>The app bar's commands show icons only when the window is too narrow for their labels (the card is wider with technical details on).</summary>
    private void UpdateAppBarLayout()
    {
        if (ActualWidth <= 0) return;
        GlobalSearch.Width = ActualWidth < 1280 ? 200 : 248;
        var bar = ActualWidth < (StudioPreferences.Current.ShowTechnicalDetails && _driver.Target != null ? 1600 : 1440);
        if (bar == _compactBar) return;
        _compactBar = bar;
        foreach (var button in new[] { SampleAppButton, InspectButton, ActivityButton })
        {
            button.ApplyTemplate();
            if (VisualTreeHelper.GetChildrenCount(button) > 0 && FindChild<ContentPresenter>(button) is { } label) label.Visibility = bar ? Visibility.Collapsed : Visibility.Visible;
            if (FindChild<TextBlock>(button) is { } glyph) glyph.Margin = bar ? new Thickness(0) : new Thickness(0, 0, 8, 0);
        }
        TechToggle.Content = bar ? null : "Show technical details";
    }
    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(Width, area.Width - 32)); Height = Math.Max(MinHeight, Math.Min(Height, area.Height - 32));
    }

    // ═══════════════ Getting started ═══════════════
    /// <summary>Choose an AI (saved settings, or Offline), describe a test (any test; the AI finds the app), run a test (any run). Hidden when all are done or dismissed.</summary>
    private void UpdateGettingStarted()
    {
        if (StartAiLink is null) return;
        var ai = _settings.Kind == ProviderKind.Offline || File.Exists(Path.Combine(_store.RootDirectory, "settings.json"));
        var described = _tests.Count > 0;
        var ran = _runIndex.Count > 0;
        var done = new[] { ai, described, ran };
        var next = Array.IndexOf(done, false);
        Mark(StartAiRing, StartAiMark, StartAiLink, 1, ai, next == 0);
        Mark(StartConnectRing, StartConnectMark, StartConnectLink, 2, described, next == 1);
        Mark(StartRunRing, StartRunMark, StartRunLink, 3, ran, next == 2);
        GettingStartedCount.Text = $"{done.Count(d => d)} of 3 done";
        GettingStarted.Visibility = next >= 0 && !StudioPreferences.Current.GettingStartedDismissed && _view != EditorView.LastRun ? Visibility.Visible : Visibility.Collapsed;

        static void Mark(System.Windows.Shapes.Ellipse ring, TextBlock mark, Button link, int number, bool isDone, bool isNext)
        {
            ring.Visibility = isDone ? Visibility.Collapsed : Visibility.Visible;
            mark.Text = isDone ? "" : number.ToString(System.Globalization.CultureInfo.CurrentCulture);
            mark.FontFamily = (FontFamily)Application.Current.Resources[isDone ? "IconFont" : "SmallFont"];
            mark.FontSize = isDone ? 14 : 11;
            mark.SetResourceReference(TextBlock.ForegroundProperty, isDone ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush");
            link.SetResourceReference(ForegroundProperty, isDone ? "TextFillColorSecondaryBrush" : isNext ? "AccentTextFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");
        }
    }
    private void StartAi_Click(object sender, RoutedEventArgs e) => SettingsNav_Click(sender, e);
    // Step 2 no longer connects an app by hand: it opens "New test from a description", where the app is named and the AI finds it.
    private void StartConnect_Click(object sender, RoutedEventArgs e)
    {
        if (ShowPage(LibraryPage)) { PromptBox.Focus(); Keyboard.Focus(PromptBox); }
    }
    private void StartRun_Click(object sender, RoutedEventArgs e) => RunTest_Click(sender, e);
    private void DismissGettingStarted_Click(object sender, RoutedEventArgs e)
    {
        StudioPreferences.Current.GettingStartedDismissed = true; UpdateGettingStarted();
        Log(ActivityLevel.Info, "App", "Getting started dismissed.");
    }

    // ═══════════════ Messages: editor InfoBar, status line, activity ═══════════════
    /// <summary>Assistant replies and errors appear as an InfoBar at the top of the editor (Severity: Informational, Success, Warning, Error).</summary>
    private void ShowEditorMessage(string text, string severity = "Informational")
    {
        EditorMessageText.Text = text; EditorMessageIcon.Tag = severity;
        EditorMessage.Style = (Style)FindResource(severity switch { "Success" => "InfoBarSuccess", "Warning" => "InfoBarWarning", "Error" => "InfoBarError", _ => "InfoBar" });
        EditorMessage.Visibility = Visibility.Visible;
    }
    private void HideEditorMessage() { if (EditorMessage != null) EditorMessage.Visibility = Visibility.Collapsed; }
    private void DismissEditorMessage_Click(object sender, RoutedEventArgs e) => HideEditorMessage();
    /// <summary>The status line shows the latest message; every message is also an activity entry.</summary>
    private void SetStatus(string message, ActivityLevel level = ActivityLevel.Info, string source = "App", string? detail = null, string? runId = null)
    {
        StatusText.Text = message; StatusText.Tag = level == ActivityLevel.Error ? "Error" : null;
        Log(level, source, message, runId, detail);
    }
    /// <summary>An error: status line (with an error icon), activity log, and on the Tests page an error InfoBar.</summary>
    private void ReportError(string message, string source = "App")
    {
        SetStatus(message, ActivityLevel.Error, source);
        if (LibraryPage.Visibility == Visibility.Visible) ShowEditorMessage(message, "Error");
    }

    // ═══════════════ Flyouts: Change app, Help, Improve, search ═══════════════
    private async void ChangeApp_Click(object sender, RoutedEventArgs e)
    {
        if (ChangeAppFlyout.IsOpen) { CloseFlyouts(); return; }
        OpenFlyout(ChangeAppFlyout);
        if (_busy) return;
        try { await RefreshTargets(); } // A quiet refresh of the window list; it is not an operation and disables nothing.
        catch (Exception ex) when (ex is not OperationCanceledException) { SetStatus(ex.Message, ActivityLevel.Warning); }
    }
    private void Help_Click(object sender, RoutedEventArgs e) { if (HelpFlyout.IsOpen) CloseFlyouts(); else OpenFlyout(HelpFlyout); }
    private void OpenFlyout(Popup flyout)
    {
        CloseFlyouts(); flyout.IsOpen = true;
        if (flyout.Child is FrameworkElement root) root.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => root.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)));
    }
    private void CloseFlyouts()
    {
        if (ChangeAppFlyout != null) ChangeAppFlyout.IsOpen = false;
        if (HelpFlyout != null) HelpFlyout.IsOpen = false;
        if (ImproveFlyout != null) ImproveFlyout.IsOpen = false;
        CloseSearch();
    }
    private void CloseFlyouts_Click(object sender, RoutedEventArgs e) => CloseFlyouts();
    private void Flyout_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Control owner = HelpFlyout.IsOpen ? HelpNav : ImproveFlyout.IsOpen ? ImproveButton : SearchFlyout.IsOpen ? GlobalSearch : ChangeAppButton;
        CloseFlyouts(); owner.Focus(); e.Handled = true;
    }
    /// <summary>Light dismiss: clicks inside a flyout go to its own popup window, so a click that reaches this window is outside it.
    /// The Activity pane is part of this window, so clicks inside it are recognized here.</summary>
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (ActivityPanel.Visibility == Visibility.Visible && source != null && !IsWithin(source, ActivityPanel) && !IsWithin(source, ActivityButton)) CloseActivity();
        if (!ChangeAppFlyout.IsOpen && !HelpFlyout.IsOpen && !ImproveFlyout.IsOpen && !SearchFlyout.IsOpen) return;
        if (source != null && (IsWithin(source, ChangeAppButton) || IsWithin(source, HelpNav) || IsWithin(source, ImproveButton) || IsWithin(source, GlobalSearch))) return;
        CloseFlyouts();
    }
    private static bool IsWithin(DependencyObject element, DependencyObject? ancestor)
    {
        if (ancestor is null) return false;
        for (var current = element; current != null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current == ancestor) return true;
        return false;
    }
    private void Window_Deactivated(object? sender, EventArgs e) => CloseFlyouts();
    private void Window_LocationChanged(object? sender, EventArgs e) => CloseFlyouts();
    private void Documentation_Click(object sender, RoutedEventArgs e) => TryShell(() => { CloseFlyouts(); OpenPath(FindDocumentation()); });
    /// <summary>The docs folder beside the app (release package) or at the repository root (development build).</summary>
    internal static string FindDocumentation()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var docs = Path.Combine(directory.FullName, "docs");
            if (Directory.Exists(docs) && (File.Exists(Path.Combine(directory.FullName, "README.md")) || File.Exists(Path.Combine(directory.FullName, "Testy.sln")))) return docs;
        }
        throw new DirectoryNotFoundException("The Testy documentation was not found beside this app. It is in the docs folder of the Testy package.");
    }
    private void TryShell(Action action) { try { action(); } catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { ReportError(ex.Message); } }

    // ═══════════════ Keyboard ═══════════════
    internal static readonly (string Keys, string Action)[] Shortcuts =
    [
        ("F5", "Run the selected test"), ("Shift+F5", "Stop the run"), ("Ctrl+N", "New test"), ("Ctrl+S", "Save the test"), ("Ctrl+D", "Duplicate the test"),
        ("Delete", "Delete the selected test (in the list)"), ("Ctrl+F", "Filter tests"), ("Ctrl+K", "Search tests and steps"),
        ("Ctrl+1 · 2 · 3", "Tests, Results, Automate"), ("Ctrl+I", "Inspect controls"), ("Ctrl+,", "Settings"), ("F1", "Help"), ("Esc", "Close a panel or flyout")
    ];
    private void BuildShortcutList()
    {
        foreach (var (keys, action) in Shortcuts)
        {
            var row = ShortcutsGrid.RowDefinitions.Count; ShortcutsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var chip = new Border { Style = (Style)FindResource("KeyChip"), Margin = new Thickness(0, 3, 16, 3), Child = new TextBlock { Text = keys, FontSize = 12 } };
            var text = new TextBlock { Text = action, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(chip, row); Grid.SetRow(text, row); Grid.SetColumn(text, 1); ShortcutsGrid.Children.Add(chip); ShortcutsGrid.Children.Add(text);
        }
    }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        if (key == Key.Escape && (ChangeAppFlyout.IsOpen || HelpFlyout.IsOpen || ImproveFlyout.IsOpen || SearchFlyout.IsOpen)) { CloseFlyouts(); e.Handled = true; return; }
        if (key == Key.Escape && ActivityPanel.Visibility == Visibility.Visible) { CloseActivity(); ActivityButton.Focus(); e.Handled = true; return; }
        if (modifiers == ModifierKeys.None && key == Key.F1) { Help_Click(HelpNav, e); e.Handled = true; }
        else if (modifiers == ModifierKeys.None && key == Key.F5) { if (LibraryPage.IsVisible && RunButton.IsEnabled) RunTest_Click(RunButton, e); e.Handled = true; }
        else if (modifiers == ModifierKeys.Shift && key == Key.F5) { if (StopButton.IsEnabled) Stop_Click(StopButton, e); e.Handled = true; }
        else if (modifiers != ModifierKeys.Control) return;
        else if (key == Key.S && LibraryPage.IsVisible) { SaveTest_Click(this, e); e.Handled = true; }
        else if (key == Key.N && LibraryPage.IsVisible) { NewTest_Click(this, e); e.Handled = true; }
        else if (key == Key.D && LibraryPage.IsVisible) { DuplicateTest_Click(this, e); e.Handled = true; }
        else if (key == Key.F && LibraryPage.IsVisible) { SearchTests.Focus(); SearchTests.SelectAll(); e.Handled = true; }
        else if (key == Key.K) { GlobalSearch.Focus(); GlobalSearch.SelectAll(); e.Handled = true; }
        else if (key is Key.D1 or Key.NumPad1) { LibraryNav_Click(this, e); e.Handled = true; }
        else if (key is Key.D2 or Key.NumPad2) { RunsNav_Click(this, e); e.Handled = true; }
        else if (key is Key.D3 or Key.NumPad3) { Workflows_Click(this, e); e.Handled = true; }
        else if (key == Key.I) { InspectorNav_Click(this, e); e.Handled = true; }
        else if (key == Key.OemComma) { SettingsNav_Click(this, e); e.Handled = true; }
    }

    // ═══════════════ Errors and closing ═══════════════
    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException)
        {
            SetStatus("Stopped. Screenshots and results saved so far are kept.", ActivityLevel.Warning);
            if (LibraryPage.Visibility == Visibility.Visible) ShowEditorMessage("Stopped. Screenshots and results saved so far are kept.", "Warning");
        }
        catch (Exception ex) { ReportError(ex.Message); }
    }
    private void GuardSync(Action action)
    {
        try { if (_busy) throw new InvalidOperationException("Wait for the current operation to finish before changing this workspace."); action(); }
        catch (Exception ex) { ReportError(ex.Message); }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy) { _operation?.Cancel(); e.Cancel = true; SetStatus("Stopping the current operation. Close Testy again when it finishes.", ActivityLevel.Warning); return; }
        if (_automate is { IsBusy: true }) { _automate.RequestStop(); e.Cancel = true; SetStatus("Stopping the Automate operation. Close Testy again when it finishes.", ActivityLevel.Warning); return; }
        try { if (_dirty || _selected != null) CommitEditor(); }
        catch (Exception ex)
        {
            e.Cancel = true;
            ReportError(_externalVersion is not null || _externalDeleted ? "Testy stayed open: " + ex.Message : "Testy stayed open because the test could not be saved: " + ex.Message, "Tests");
            return;
        }
        CloseFlyouts(); _automate?.Leave();
        _watcher?.Dispose(); _watcher = null;
        StopAgentAccessBlocking();
        _driver.Dispose();
        // Refresh first: the handle cached while the lab started can belong to a short-lived startup window.
        if (_labProcess != null) { _labProcess.Refresh(); if (!_labProcess.HasExited) _labProcess.CloseMainWindow(); _labProcess.Dispose(); }
        // Apps Studio started for a test close with it, as the sample app does; apps the person started stay open.
        foreach (var started in _startedApps)
        {
            try { started.Refresh(); if (!started.HasExited) started.CloseMainWindow(); }
            catch (InvalidOperationException) { }
            started.Dispose();
        }
        _startedApps.Clear();
        Log(ActivityLevel.Info, "App", "Testy closed.");
    }
}
