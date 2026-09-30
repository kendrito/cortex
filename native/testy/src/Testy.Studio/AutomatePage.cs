using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Testy.Core;
using Microsoft.Win32;

namespace Testy.Studio;

/// <summary>
/// The Automate page: guided forms over the same checked CLI contracts used by unattended workers.
/// MainWindow builds it fresh from disk each time NavWorkflows is invoked and removes it from the tree when you leave (it replaced the
/// modal "Workflows and jobs" window). All tab content is built synchronously here; the 2 s refresh rebinds grids only when a revision changes.
/// </summary>
internal sealed class AutomatePage : UserControl
{
    private readonly WorkspaceStore store;
    private readonly Action<RunResult> saveRun;
    private readonly Action<TestCase> saveTest;
    private readonly TestCase? selected;
    private int? pid;
    private string? targetTitle;
    private bool probe;
    private readonly Func<bool> hostBusy;
    private readonly Action<string, string>? report;
    private readonly TabControl tabs = new();
    private readonly TextBlock status = new() { Text = "Ready", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border statusBar = new();
    private readonly Control statusIcon = new();
    private readonly TextBox details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Button stop = new() { Content = "Stop", IsEnabled = false };
    private CancellationTokenSource? operation;
    private string lastOutput = "";
    private readonly ObservableCollection<CommandRow> commands = [];
    private readonly TextBox projectRoot = new();
    private readonly CheckBox projectEnabled = new() { Content = "Let the AI read this project and choose from the commands below" };
    private readonly TextBox projectCalls = new(), commandCalls = new();
    private readonly TextBox profileName = new() { Text = "Prepare and test" }, executable = new(), launchArguments = new();
    private readonly TextBox preparation = new() { Text = new LifecycleProfile().PreparationInstructions, MinHeight = 76 };
    private readonly TextBox prepTurns = new() { Text = "20" }, prepSeconds = new() { Text = "600" }, runSeconds = new() { Text = "900" };
    private readonly ComboBox lifecycleTest = new() { DisplayMemberPath = "Name" };
    private readonly CheckBox lifecycleProbe = new() { Content = "Use the WPF helper (the app must include Testy.WpfProbe)" };
    private readonly ComboBox profiles = new() { DisplayMemberPath = "Name" };
    private readonly TextBlock profileSummary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox suiteTests = new() { SelectionMode = SelectionMode.Multiple, DisplayMemberPath = "Name", Height = 168 };
    private readonly TextBox suiteName = new() { Text = "Functional suite" }, repetitions = new() { Text = "1" };
    private readonly ObservableCollection<BindingRow> bindings = [];
    private string csvPath = "", suitePath = "", demonstrationPath = "";
    private readonly TextBlock csvSummary = new() { TextWrapping = TextWrapping.Wrap }, suiteSummary = new() { TextWrapping = TextWrapping.Wrap }, demoStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock demoTarget = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox demoSeconds = new() { Text = "30" }, demoInstructions = new() { Text = "Create a test from this demonstration and preserve every explicit acceptance assertion.", MinHeight = 76 };
    private readonly DataGrid jobs = GridView("OperationsJobs"), schedules = GridView("OperationsSchedules");
    private readonly TextBox interval = new() { Text = "3600" };
    private readonly TextBox workerSeconds = new() { Text = "3600" };
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool refreshing;
    private string jobsRevision = "", schedulesRevision = "";
    private readonly TextBlock agentStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 4) };
    private readonly TextBlock agentDetail = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox localRuns = new() { MinWidth = 380, HorizontalAlignment = HorizontalAlignment.Left };
    private Button? pauseAgent, processOneJob, startLocalWorker;
    private bool agentRunning;
    private readonly DataGrid machines = GridView("MachinesGrid");
    private readonly TextBlock machinesStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly ComboBox machineTest = new() { DisplayMemberPath = "Name" };
    private readonly TextBox machineExe = new() { Text = @"{worker}\Testy.TestLab.exe" }, machineStage = new(), machineTimeout = new() { Text = "900" }, machineInterval = new() { Text = "3600" };
    private readonly ComboBox machineMode = new();
    private readonly CheckBox machineProbe = new() { Content = "Use the WPF helper (the app must include Testy.WpfProbe)" };
    private string machinesRevision = "";

    /// <param name="report">Receives every status this page shows (text, severity) for the Studio activity log.</param>
    /// <param name="saveRun">Saves a run the way the window does (noted with the workspace watcher, so it is never reported as an outside change); the store when absent.</param>
    /// <param name="saveTest">The same for a recorded draft test.</param>
    internal AutomatePage(WorkspaceStore store, TestCase? selected, int? processId, string? processTitle, bool probe, Func<bool> hostBusy, Action<string, string>? report = null, Action<RunResult>? saveRun = null, Action<TestCase>? saveTest = null)
    {
        this.store = store; this.selected = selected is null ? null : TestyJson.Clone(selected); pid = processId; targetTitle = processTitle; this.probe = probe; this.hostBusy = hostBusy; this.report = report;
        this.saveRun = saveRun ?? store.SaveRun; this.saveTest = saveTest ?? store.SaveTest;
        Focusable = false;
        var root = new Grid(); Content = root;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition());
        var heading = new StackPanel { Margin = new Thickness(32, 24, 32, 0) };
        heading.Children.Add(new TextBlock { Text = "Automate", Style = Res("PageTitle") });
        heading.Children.Add(new TextBlock { Text = "Run tests in the background, on a schedule, as sets or in virtual machines, and back up your data.", Style = Res("PageSubtitle") });
        root.Children.Add(heading);
        root.Children.Add(StatusArea());
        tabs.Margin = new Thickness(32, 12, 20, 0); Grid.SetRow(tabs, 2); root.Children.Add(tabs);
        // Ordered by how often people need them. Tab AutomationIds are explicit and stable.
        JobsPage(); SuitePage(); MachinesPage(); DemoPage(); LifecyclePage(); ProjectPage(); WorkspacePage();
        refresh.Tick += (_, _) => { RefreshOperations(silent: true); RefreshAgent(); }; refresh.Start();
        RefreshAgent();
        RefreshProfiles(); RefreshOperations(silent: false);
    }

    internal bool IsBusy => operation != null;
    /// <summary>Called when navigation away from Automate is refused because an operation is still running.</summary>
    internal void RequestStop() { operation?.Cancel(); Status("Stopping the current operation safely. You can leave Automate when it finishes.", "Warning"); }
    /// <summary>Called when the page is removed from the window: stops the 2 s refresh.</summary>
    internal void Leave() => refresh.Stop();
    /// <summary>MainWindow connected another app while this page is shown.</summary>
    internal void UpdateTarget(int? processId, string? processTitle, bool useProbe) { pid = processId; targetTitle = processTitle; probe = useProbe; UpdateDemoTarget(); }
    /// <summary>Selects a tab by its AutomationId (used by the hidden screenshot mode).</summary>
    internal void SelectTab(string automationId) { foreach (var tab in tabs.Items.OfType<TabItem>()) if (AutomationProperties.GetAutomationId(tab) == automationId) tabs.SelectedItem = tab; }

    private Style Res(string key) => (Style)FindResource(key);
    private FrameworkElement StatusArea()
    {
        var area = new StackPanel { Margin = new Thickness(32, 16, 32, 0) }; Grid.SetRow(area, 1);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusIcon.Style = Res("InfoBarIcon"); statusIcon.Tag = "Informational"; grid.Children.Add(statusIcon);
        Grid.SetColumn(status, 1); grid.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        stop.Style = Res("IconButton"); stop.Tag = ""; stop.ToolTip = "Stop the current operation safely";
        stop.Click += (_, _) => { operation?.Cancel(); Status("Stopping safely. Waiting for cleanup and the final results…", "Warning"); };
        var output = Button("Open last output", "WorkflowEvidence", () => Open(lastOutput), false, "", "SubtleButton"); output.Margin = new Thickness(8, 0, 0, 0);
        actions.Children.Add(stop); actions.Children.Add(output); Grid.SetColumn(actions, 2); grid.Children.Add(actions);
        statusBar.Style = Res("InfoBar"); statusBar.Child = grid; area.Children.Add(statusBar);
        details.Style = Res("CodeEditor"); details.Height = 140;
        var expander = new Expander { Header = "Details", Content = details, Margin = new Thickness(0, 6, 0, 0) };
        expander.SetBinding(VisibilityProperty, new Binding(nameof(StudioPreferences.ShowTechnicalDetails)) { Source = StudioPreferences.Current, Converter = (IValueConverter)FindResource("BoolToVisibility") });
        area.Children.Add(expander);
        AutomationProperties.SetAutomationId(status, "WorkflowStatus"); AutomationProperties.SetAutomationId(stop, "StopWorkflow");
        return area;
    }
    /// <summary>InfoBar-style status. Severity is Informational, Success, Warning or Error.</summary>
    private void Status(string text, string severity = "Informational")
    {
        status.Text = text; statusIcon.Tag = severity;
        statusBar.Style = Res(severity switch { "Success" => "InfoBarSuccess", "Warning" => "InfoBarWarning", "Error" => "InfoBarError", _ => "InfoBar" });
        if (text != "Working…") report?.Invoke(text, severity);
    }

    private StackPanel Page(string header, string id, string description)
    {
        var body = new StackPanel { Margin = new Thickness(0, 16, 12, 32) };
        var intro = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush"); body.Children.Add(intro);
        var width = new Grid(); width.ColumnDefinitions.Add(new ColumnDefinition { MaxWidth = 1060 }); width.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); width.Children.Add(body);
        var tab = new TabItem { Header = header, Content = new ScrollViewer { Content = width, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        AutomationProperties.SetAutomationId(tab, id); tabs.Items.Add(tab);
        return body;
    }
    /// <summary>A Fluent card that groups one task on a tab.</summary>
    private StackPanel Card(Panel parent, string title, string? description = null)
    {
        var inner = new StackPanel();
        inner.Children.Add(new TextBlock { Text = title, Style = Res("PaneTitle") });
        if (description != null) inner.Children.Add(new TextBlock { Text = description, Style = Res("HelpText"), Margin = new Thickness(0, 2, 0, 0) });
        parent.Children.Add(new Border { Style = Res("Card"), Padding = new Thickness(20, 16, 20, 18), Margin = new Thickness(0, 16, 0, 0), Child = inner });
        return inner;
    }
    private void Field(Panel parent, string label, FrameworkElement input, string id)
    {
        if (label.Length > 0) parent.Children.Add(new TextBlock { Text = label, Style = Res("FieldLabel") });
        input.Margin = new Thickness(0, label.Length > 0 ? 0 : 12, 0, 4); AutomationProperties.SetAutomationId(input, id);
        if (label.Length > 0 && string.IsNullOrEmpty(AutomationProperties.GetName(input))) AutomationProperties.SetName(input, label);
        parent.Children.Add(input);
    }
    private static WrapPanel Actions(Panel parent, params Button[] buttons)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) }; foreach (var button in buttons) panel.Children.Add(button); parent.Children.Add(panel); return panel;
    }
    private Button Button(string text, string id, Action action, bool guarded = true, string? glyph = null, string style = "IconButton")
    {
        var button = new Button { Content = text, Tag = glyph, Style = Res(style), Margin = new Thickness(0, 8, 8, 0) };
        AutomationProperties.SetAutomationId(button, id);
        button.Click += (_, _) => { try { if (guarded && operation != null) throw new InvalidOperationException("Wait for the current operation to finish."); action(); } catch (Exception ex) { ShowError(ex); } };
        return button;
    }
    private Button AsyncButton(string text, string id, Func<CancellationToken, Task> action, string? glyph = null, string style = "IconButton") => Button(text, id, async () => await Run(action), true, glyph, style);
    private static DataGrid GridView(string id)
    {
        var grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, Height = 180, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Single };
        AutomationProperties.SetAutomationId(grid, id); return grid;
    }
    private static void Column(DataGrid grid, string label, string property, bool readOnly = true, double width = 1) => grid.Columns.Add(new DataGridTextColumn
    { Header = label, Binding = new Binding(property) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, IsReadOnly = readOnly, Width = new DataGridLength(width, DataGridLengthUnitType.Star) });
    /// <summary>Friendly local time; sorting still uses the stored value.</summary>
    private void TimeColumn(DataGrid grid, string label, string property, double width = 1) => grid.Columns.Add(new DataGridTextColumn
    { Header = label, Binding = new Binding(property) { Converter = (IValueConverter)FindResource("FriendlyTime") }, SortMemberPath = property, IsReadOnly = true, Width = new DataGridLength(width, DataGridLengthUnitType.Star) });
    /// <summary>"This PC" or the VM's Hyper-V name (from the agent's inventory), falling back to its short ID.</summary>
    private sealed class TargetNames : IValueConverter
    {
        public static readonly TargetNames Instance = new();
        public Dictionary<Guid, string> Names { get; } = [];
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            try { var target = OperationsTargets.Parse(value as string); return target.IsLocal ? "This PC" : Names.TryGetValue(target.VirtualMachineId, out var name) ? name : OperationsTargets.Label(target.Canonical); }
            catch (InvalidDataException) { return "Invalid target"; }
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }
    private sealed class OnOff : IValueConverter
    {
        public static readonly OnOff Instance = new();
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value is true ? "On" : "Paused";
        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }
    private static void TargetColumn(DataGrid grid, string property) => grid.Columns.Add(new DataGridTextColumn
    { Header = "Where", Binding = new Binding(property) { Converter = TargetNames.Instance }, IsReadOnly = true, Width = new DataGridLength(1.5, DataGridLengthUnitType.Star) });
    private static void Commit(DataGrid grid) { grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true); }
    private static int Number(TextBox field, int min, int max, string name) => int.TryParse(field.Text, out var value) && value >= min && value <= max ? value : throw new InvalidDataException($"{name} must be between {min} and {max}.");
    private string Area(string name) { var path = Path.Combine(store.RootDirectory, name); Directory.CreateDirectory(path); return path; }
    private string SaveInput<T>(string name, T value) { var path = Path.Combine(Area("workflow-inputs"), name + "-" + Guid.NewGuid().ToString("N") + ".json"); WorkspaceStore.WriteAtomic(path, value); return path; }
    private string CurrentSettings() => SaveInput("settings", store.LoadSettings());
    private string Artifacts => Area("artifacts");
    private static string? PickFile(string title, string filter = "All files|*.*") { var picker = new OpenFileDialog { Title = title, Filter = filter }; return picker.ShowDialog() == true ? picker.FileName : null; }
    private static string? PickFolder(string title) { var picker = new OpenFolderDialog { Title = title }; return picker.ShowDialog() == true ? picker.FolderName : null; }
    private static void Open(string path) { if (!Directory.Exists(path) && !File.Exists(path)) throw new FileNotFoundException("No saved output is available at this path yet.", path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (operation != null) return;
        if (hostBusy()) { Status("Wait for the current operation in Tests to finish, or stop it first.", "Warning"); return; }
        operation = new CancellationTokenSource(); tabs.IsEnabled = false; stop.IsEnabled = true; Status("Working…");
        try { await action(operation.Token); }
        catch (OperationCanceledException) { Status("Stopped. Review what was saved before starting a fresh attempt.", "Warning"); }
        catch (Exception ex) { ShowError(ex); }
        finally { operation.Dispose(); operation = null; tabs.IsEnabled = true; stop.IsEnabled = false; RefreshOperations(silent: true); }
    }
    private void ShowError(Exception ex) { Status(ex.Message, "Error"); details.Text = ex.ToString(); }
    private async Task<StudioCommandResult> Command(CancellationToken ct, params string[] arguments)
    {
        var result = await StudioCommandClient.RunAsync(store.RootDirectory, arguments, ct);
        lastOutput = Path.GetDirectoryName(result.OutputPath)!; details.Text = JsonSerializer.Serialize(result.Json, TestyJson.Options);
        string message = result.Json.ValueKind == JsonValueKind.Object && result.Json.TryGetProperty("message", out var m) ? m.ToString()
            : result.Json.ValueKind == JsonValueKind.Object && result.Json.TryGetProperty("summary", out var s) ? s.ToString()
            : result.Json.ValueKind == JsonValueKind.Object && result.Json.TryGetProperty("error", out var e) ? e.ToString() : "The result was saved with its evidence.";
        Status((result.ExitCode == 0 ? "Completed. " : "Didn't pass. ") + message, result.ExitCode == 0 ? "Success" : "Warning");
        if (result.Json.ValueKind == JsonValueKind.Object && result.Json.TryGetProperty("artifactDirectory", out var directory) && Directory.Exists(directory.GetString())) lastOutput = directory.GetString()!;
        return result;
    }
    private void RequireTarget() { if (pid is not > 0) throw new InvalidOperationException("Connect an app first (Change app, at the top of the window) before running test sets or recording."); }
    /// <summary>Test sets and recordings drive this desktop; the background agent's local jobs hold the same lease.</summary>
    private static OperationsDesktopLease DesktopLease() => OperationsDesktopLease.TryAcquire()
        ?? throw new InvalidOperationException("The Testy background agent or another worker is using this PC. Wait for it to finish, or pause background runs.");

    private void ProjectPage()
    {
        var body = Page("Source code access", "WorkflowTabProject", "Choose the project the AI may read and the exact commands it may run. Arguments are literal values, one per line. Build steps that must run before the app opens belong in a Build, then test setup.");
        var config = store.LoadSettings().ProjectTools ?? new ProjectToolSettings();
        var project = Card(body, "Project");
        projectEnabled.IsChecked = config.Enabled; projectEnabled.Margin = new Thickness(0, 10, 0, 0); project.Children.Add(projectEnabled); projectRoot.Text = config.RootDirectory;
        Field(project, "Project folder", projectRoot, "ProjectRoot");
        Actions(project, Button("Choose folder…", "BrowseProject", () => { var path = PickFolder("Select a test project"); if (path != null) projectRoot.Text = path; }, glyph: ""));
        var catalog = Card(body, "Commands the AI may run", "Only these commands, with exactly these arguments. Mark the ones that must pass before testing.");
        var grid = GridView("ProjectCommands"); grid.IsReadOnly = false; grid.ItemsSource = commands; grid.Height = 188;
        Column(grid, "Name", nameof(CommandRow.Id), false); Column(grid, "Description", nameof(CommandRow.Description), false, 2); Column(grid, "Program", nameof(CommandRow.Executable), false, 3); Column(grid, "Run in folder", nameof(CommandRow.WorkingDirectory), false); Column(grid, "Time limit (s)", nameof(CommandRow.TimeoutSeconds), false);
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Must pass first", Binding = new Binding(nameof(CommandRow.Required)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 120 });
        foreach (var command in config.Commands) commands.Add(new CommandRow(command, config.RequiredBeforeUiCommands.Contains(command.Id)));
        Field(catalog, "", grid, "ProjectCommands");
        Actions(catalog, Button("Add command", "AddProjectCommand", () => { var row = new CommandRow { Id = "command-" + (commands.Count + 1), TimeoutSeconds = 120, WorkingDirectory = "." }; commands.Add(row); grid.SelectedItem = row; }, glyph: ""),
            Button("Choose program…", "ChooseCommandExecutable", () => { if (grid.SelectedItem is not CommandRow row) throw new InvalidOperationException("Select a command first."); var path = PickFile("Choose the exact program to run", "Programs|*.exe"); if (path != null) { row.Executable = path; grid.Items.Refresh(); } }, glyph: ""),
            Button("Remove command", "RemoveProjectCommand", () => { if (grid.SelectedItem is CommandRow row) commands.Remove(row); }, glyph: ""));
        var args = new TextBox { Style = Res("MultiLineBox"), TextWrapping = TextWrapping.NoWrap, Height = 84 };
        args.SetBinding(TextBox.TextProperty, new Binding("SelectedItem.ArgumentsText") { Source = grid, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        Field(catalog, "Arguments for the selected command, one per line", args, "CommandArguments");
        var limits = Card(body, "Limits");
        projectCalls.Text = config.MaximumCalls.ToString(); commandCalls.Text = config.MaximumCommandInvocations.ToString();
        var numbers = new UniformGrid { Columns = 2, HorizontalAlignment = HorizontalAlignment.Left, Width = 520 }; var n1 = new StackPanel { Margin = new Thickness(0, 0, 16, 0) }; var n2 = new StackPanel(); numbers.Children.Add(n1); numbers.Children.Add(n2);
        Field(n1, "Most project tool calls", projectCalls, "ProjectCallLimit"); Field(n2, "Most command runs", commandCalls, "CommandCallLimit"); limits.Children.Add(numbers);
        Actions(limits, Button("Save source code access", "SaveProject", () =>
        {
            Commit(grid); var next = TestyJson.Clone(config); next.Enabled = projectEnabled.IsChecked == true; next.RootDirectory = projectRoot.Text.Trim();
            next.Commands = commands.Select(c => c.ToDefinition()).ToList(); next.RequiredBeforeUiCommands = commands.Where(c => c.Required).Select(c => c.Id).ToList();
            next.MaximumCalls = Number(projectCalls, 1, 200, "Project tool calls"); next.MaximumCommandInvocations = Number(commandCalls, 0, 20, "Command runs");
            store.ConfigureProjectTools(next); var settings = store.LoadSettings(); store.SaveSettings(settings); Status("Source code access saved. The next AI operation uses these commands.", "Success");
        }, glyph: "", style: "AccentIconButton"));
    }

    private void LifecyclePage()
    {
        var body = Page("Build, then test", "WorkflowTabLifecycle", "The AI looks at your project and runs the required build steps, then Testy opens the app they produce, runs the saved test and closes the app. The AI chooses every build step and every UI step. Saved setups can also be added to the waiting list on Background runs.");
        var saved = Card(body, "Saved setups");
        Field(saved, "Setup", profiles, "LifecycleProfiles");
        profileSummary.Style = Res("HelpText"); profileSummary.Margin = new Thickness(0, 6, 0, 0); saved.Children.Add(profileSummary);
        profiles.SelectionChanged += (_, _) => { if (profiles.SelectedItem is ProfileItem item) LoadProfile(item.Path); };
        Actions(saved, AsyncButton("Run saved setup", "RunLifecycle", async ct => { await Command(ct, "lifecycle-run", "--profile", SelectedProfile(), "--artifacts", Artifacts); }, ""),
            AsyncButton("Check a run that was cut off…", "InspectLifecycle", async ct => { var folder = PickFolder("Choose the folder of the interrupted run"); if (folder != null) await Command(ct, "lifecycle-inspect", "--directory", folder); }, ""));
        var form = Card(body, "New setup", "Saving freezes a copy of the test and of your current AI settings into the setup.");
        Field(form, "Name", profileName, "LifecycleName");
        lifecycleTest.ItemsSource = store.LoadTests(); lifecycleTest.SelectedItem = ((IEnumerable<TestCase>)lifecycleTest.ItemsSource).FirstOrDefault(t => t.Id == selected?.Id);
        Field(form, "Test to run", lifecycleTest, "LifecycleTest"); Field(form, "App to open (the build may create it)", executable, "LifecycleExecutable");
        Actions(form, Button("Choose app…", "BrowseLifecycleExe", () => { var path = PickFile("Choose the app to open", "Programs|*.exe"); if (path != null) executable.Text = path; }, glyph: ""));
        launchArguments.Style = Res("MultiLineBox"); launchArguments.TextWrapping = TextWrapping.NoWrap; launchArguments.Height = 60; Field(form, "Launch arguments, one per line", launchArguments, "LifecycleArguments");
        lifecycleProbe.IsChecked = probe; lifecycleProbe.Margin = new Thickness(0, 12, 0, 0); form.Children.Add(lifecycleProbe);
        preparation.Style = Res("MultiLineBox"); Field(form, "Before testing, the AI should…", preparation, "PreparationInstructions");
        var limits = new UniformGrid { Columns = 3 }; var p1 = new StackPanel { Margin = new Thickness(0, 0, 16, 0) }; var p2 = new StackPanel { Margin = new Thickness(0, 0, 16, 0) }; var p3 = new StackPanel(); limits.Children.Add(p1); limits.Children.Add(p2); limits.Children.Add(p3);
        Field(p1, "Most AI build steps", prepTurns, "PreparationTurns"); Field(p2, "Build time limit (seconds)", prepSeconds, "PreparationTimeout"); Field(p3, "Test time limit (seconds)", runSeconds, "LifecycleTimeout"); form.Children.Add(limits);
        Actions(form, Button("Save as new setup", "SaveLifecycle", () => { var path = SaveProfile(); RefreshProfiles(path); Status("New setup saved with these values and your current AI settings.", "Success"); }, glyph: "", style: "AccentIconButton"));
    }
    private string SaveProfile()
    {
        if (lifecycleTest.SelectedItem is not TestCase test) throw new InvalidOperationException("Select a saved test to run.");
        TestValidator.Validate(test); var settings = store.LoadSettings();
        if (!settings.AiDirectedExecution || settings.Kind == ProviderKind.Offline) throw new InvalidOperationException("Choose an AI service and turn on \"Let the AI guide each run\" in Settings.");
        var profile = new LifecycleProfile
        {
            Name = profileName.Text.Trim(), TestFile = SaveInput("test", test), SettingsFile = CurrentSettings(), Executable = Path.GetFullPath(executable.Text.Trim()),
            TargetArgumentsFile = SaveInput("arguments", Lines(launchArguments.Text)), Probe = lifecycleProbe.IsChecked == true, PreparationInstructions = preparation.Text,
            MaximumPreparationTurns = Number(prepTurns, 2, 80, "AI build steps"), PreparationTimeoutSeconds = Number(prepSeconds, 1, 7200, "Build time limit"), WorkerTimeoutSeconds = Number(runSeconds, 1, 7200, "Test time limit")
        };
        if (string.IsNullOrWhiteSpace(executable.Text)) throw new InvalidDataException("Enter the exact path of the app to open.");
        LifecycleValidation.ValidateEnvironment(new LifecycleRequest { Profile = profile, Test = test, Provider = settings, TargetArguments = Lines(launchArguments.Text) });
        var path = Path.Combine(Area("profiles"), profile.Id + ".json"); WorkspaceStore.WriteAtomic(path, profile); return path;
    }
    private void RefreshProfiles(string? selectPath = null)
    {
        var old = selectPath ?? (profiles.SelectedItem as ProfileItem)?.Path;
        var items = new List<ProfileItem>(); var invalid = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Area("profiles"), "*.json"))
        {
            try { var profile = JsonSerializer.Deserialize<LifecycleProfile>(File.ReadAllText(path), TestyJson.Options) ?? throw new InvalidDataException("Empty profile."); items.Add(new(path, profile.Name)); }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException) { invalid.Add(Path.GetFileName(path) + ": " + ex.Message); }
        }
        items = items.OrderBy(x => x.Name).ToList();
        profiles.ItemsSource = items; profiles.SelectedItem = items.FirstOrDefault(x => x.Path == old);
        if (invalid.Count > 0) { Status($"{invalid.Count} saved setup(s) could not be loaded. Everything else still works.", "Warning"); details.Text = string.Join(Environment.NewLine, invalid); }
    }
    private void LoadProfile(string path)
    {
        try
        {
            var p = JsonSerializer.Deserialize<LifecycleProfile>(File.ReadAllText(path), TestyJson.Options) ?? throw new InvalidDataException("Empty profile.");
            p = WorkspaceMaintenance.ResolveRestoredProfileInputs(path, p);
            profileSummary.Text = "Run saved setup and Add to waiting list use the exact test and AI settings saved with this setup. To use edited values or your current AI settings, save a new setup.";
            profileName.Text = p.Name; executable.Text = p.Executable; preparation.Text = p.PreparationInstructions; prepTurns.Text = p.MaximumPreparationTurns.ToString(); prepSeconds.Text = p.PreparationTimeoutSeconds.ToString(); runSeconds.Text = p.WorkerTimeoutSeconds.ToString(); lifecycleProbe.IsChecked = p.Probe;
            if (File.Exists(p.TestFile)) { var test = JsonSerializer.Deserialize<TestCase>(File.ReadAllText(p.TestFile), TestyJson.Options); lifecycleTest.SelectedItem = ((IEnumerable<TestCase>)lifecycleTest.ItemsSource).FirstOrDefault(t => t.Id == test?.Id); }
            launchArguments.Text = p.TargetArgumentsFile is not null && File.Exists(p.TargetArgumentsFile) ? string.Join(Environment.NewLine, JsonSerializer.Deserialize<List<string>>(File.ReadAllText(p.TargetArgumentsFile), TestyJson.Options) ?? []) : "";
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private string SelectedProfile() => (profiles.SelectedItem as ProfileItem)?.Path ?? throw new InvalidOperationException("Choose a saved setup on the Build, then test tab first.");

    private void SuitePage()
    {
        var body = Page("Test sets & data", "WorkflowTabSuites", "Group saved tests into a set, or run one test once for each row of a spreadsheet. Every run uses your AI settings and the test's own checks. Add reset steps when tests share the connected app.");
        var set = Card(body, "Test set");
        suiteName.Width = 360; suiteName.HorizontalAlignment = HorizontalAlignment.Left; repetitions.Width = 120; repetitions.HorizontalAlignment = HorizontalAlignment.Left;
        Field(set, "Name", suiteName, "SuiteName"); Field(set, "Run the whole set this many times", repetitions, "SuiteRepetitions"); suiteTests.ItemsSource = store.LoadTests();
        suiteTests.Style = Res("FlatList"); suiteTests.BorderThickness = new Thickness(1); suiteTests.SetResourceReference(BorderBrushProperty, "ControlStrokeColorDefaultBrush"); suiteTests.Padding = new Thickness(0, 4, 0, 4);
        suiteTests.ItemContainerStyle = new Style(typeof(ListBoxItem), Res("ListItem")); suiteTests.ItemContainerStyle.Setters.Add(new Setter(AutomationProperties.NameProperty, new Binding("Name")));
        Field(set, "Tests (Ctrl+click to pick several)", suiteTests, "SuiteTests");
        Actions(set, Button("Save set", "SaveSuite", () =>
        {
            var suite = new TestSuite { Name = suiteName.Text.Trim(), Repetitions = Number(repetitions, 1, 100, "Repetitions"), Tests = suiteTests.SelectedItems.Cast<TestCase>().Select(TestyJson.Clone).ToList() };
            if (suite.Tests.Count == 0 || suite.Tests.Count * suite.Repetitions > 1000) throw new InvalidDataException("Pick at least one test (at most 1,000 planned runs)."); foreach (var test in suite.Tests) TestValidator.Validate(test);
            suitePath = Path.Combine(Area("suites"), Guid.NewGuid().ToString("N") + ".json"); WorkspaceStore.WriteAtomic(suitePath, suite); suiteSummary.Text = $"Saved {suite.Tests.Count} test(s) × {suite.Repetitions} time(s).";
        }, glyph: ""),
            Button("Load set…", "LoadSuite", () => { var path = PickFile("Select a test set", "Testy test set|*.json"); if (path != null) { var suite = JsonSerializer.Deserialize<TestSuite>(File.ReadAllText(path), TestyJson.Options) ?? throw new InvalidDataException("Empty test set."); suitePath = path; suiteSummary.Text = $"Loaded {suite.Name}: {suite.Tests.Count} test(s)."; } }, glyph: ""),
            AsyncButton("Run saved set", "RunSuite", RunSuite, "", "AccentIconButton"));
        suiteSummary.Margin = new Thickness(0, 10, 0, 0); set.Children.Add(suiteSummary);
        var dataCard = Card(body, "Run one test with spreadsheet rows", "Choose the CSV column that fills each step value. A blank column keeps the saved value.");
        var grid = GridView("ParameterBindings"); grid.IsReadOnly = false; grid.ItemsSource = bindings;
        Column(grid, "Step", nameof(BindingRow.Title), true, 2); Column(grid, "Do", nameof(BindingRow.Action)); Column(grid, "CSV column (blank keeps the saved value)", nameof(BindingRow.Parameter), false, 2);
        if (selected != null) foreach (var step in selected.Steps.Where(s => s.Action is StepAction.TypeText or StepAction.Select or StepAction.AssertText)) bindings.Add(new BindingRow { StepId = step.Id, Title = step.Title, Action = step.Action.ToString() });
        Field(dataCard, "Test: " + (selected?.Name ?? "select a test in Tests first"), grid, "ParameterBindings");
        Actions(dataCard, Button("Load CSV…", "LoadTestCsv", () => { var path = PickFile("Load test data", "CSV data|*.csv"); if (path != null) { var rows = TestDataCsv.Parse(File.ReadAllText(path)); csvPath = path; csvSummary.Text = $"{rows.Count} rows. Columns: {string.Join(", ", rows[0].Keys)}"; } }, glyph: ""),
            AsyncButton("Make a set from the rows", "MaterializeDataSuite", async ct =>
            {
                if (selected is null || !File.Exists(csvPath)) throw new InvalidOperationException("Select a test in Tests and load a CSV file."); Commit(grid);
                var rows = TestDataCsv.Parse(File.ReadAllText(csvPath)); var active = bindings.Where(b => !string.IsNullOrWhiteSpace(b.Parameter)).ToList();
                var template = SaveInput("template", new { version = 1, name = suiteName.Text, test = selected, bindings = active.Select(b => new { parameter = b.Parameter.Trim(), stepId = b.StepId, field = "value" }) });
                var data = SaveInput("data", new { version = 1, rows = rows.Select((values, i) => new { id = "row-" + (i + 1), name = "Row " + (i + 1), values }) });
                var output = Path.Combine(Area("suites"), Guid.NewGuid().ToString("N") + ".json"); var result = await Command(ct, "materialize-template", "--template", template, "--data", data, "--output", output);
                if (result.ExitCode == 0) { suitePath = output; suiteSummary.Text = $"Test set ready: {rows.Count} rows. Choose Run saved set to run it."; }
            }, ""));
        csvSummary.Margin = new Thickness(0, 10, 0, 0); dataCard.Children.Add(csvSummary);
    }
    private async Task RunSuite(CancellationToken ct)
    {
        RequireTarget(); if (!File.Exists(suitePath)) throw new InvalidOperationException("Save or load a test set first.");
        var args = new List<string> { "run-suite", "--suite", suitePath, "--settings", CurrentSettings(), "--pid", pid!.Value.ToString(), "--artifacts", Artifacts }; if (probe) args.Add("--probe");
        using var desktop = DesktopLease(); using var execution = desktop.EnterExecution();
        var result = await Command(ct, args.ToArray());
        if (result.Json.ValueKind == JsonValueKind.Object && result.Json.TryGetProperty("entries", out _))
        { var suite = result.Json.Deserialize<SuiteResult>(TestyJson.Options); if (suite != null) foreach (var entry in suite.Entries.Where(x => x.Run != null)) saveRun(entry.Run!); }
    }

    private void DemoPage()
    {
        var body = Page("Record", "WorkflowTabDemonstrations", "Record clicks and typed text in the connected app's first window. This is a limited event recorder; it does not capture every gesture or additional windows. The AI turns the recording into a draft test and keeps the checks of the test selected in Tests. Review and run every draft.");
        demoTarget.Margin = new Thickness(0, 10, 0, 0); UpdateDemoTarget(); body.Children.Add(demoTarget);
        var record = Card(body, "Record");
        demoSeconds.Width = 120; demoSeconds.HorizontalAlignment = HorizontalAlignment.Left;
        Field(record, "Recording length (seconds)", demoSeconds, "DemoDuration");
        Actions(record, AsyncButton("Record", "RecordDemonstration", async ct =>
        {
            RequireTarget(); using var desktop = DesktopLease(); using var execution = desktop.EnterExecution();
            demonstrationPath = Path.Combine(Area("demonstrations"), Guid.NewGuid().ToString("N") + ".json"); demoStatus.Text = "Getting ready to record…";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (_, _) => { try { if (File.Exists(demonstrationPath)) { using var doc = JsonDocument.Parse(File.ReadAllText(demonstrationPath)); if (doc.RootElement.TryGetProperty("recordingActive", out var active) && active.GetBoolean()) demoStatus.Text = "Recording is active. Use the connected app now."; } } catch (IOException) { } catch (JsonException) { } };
            timer.Start();
            try { var result = await Command(ct, "record", "--pid", pid!.Value.ToString(), "--output", demonstrationPath, "--duration-seconds", Number(demoSeconds, 1, 600, "Recording length").ToString()); demoStatus.Text = result.ExitCode == 0 ? "Recording saved. Ready to draft a test." : "The recording is incomplete. Review its evidence and warnings."; }
            finally { timer.Stop(); }
        }, "", "AccentIconButton"),
            Button("Load recording…", "LoadDemonstration", () => { var path = PickFile("Select a recording", "Recording|*.json"); if (path != null) { demonstrationPath = path; demoStatus.Text = "Loaded " + Path.GetFileName(path); } }, glyph: ""));
        demoStatus.Margin = new Thickness(0, 10, 0, 0); record.Children.Add(demoStatus);
        var draft = Card(body, "Turn the recording into a test");
        demoInstructions.Style = Res("MultiLineBox"); Field(draft, "Instructions for the AI", demoInstructions, "DemoInstructions");
        Actions(draft, AsyncButton("Draft a test", "DraftDemonstration", async ct =>
        {
            if (selected is null || !File.Exists(demonstrationPath)) throw new InvalidOperationException("Select a test with checks in Tests, then record or load a recording.");
            var acceptance = TestyJson.Clone(selected); acceptance.Steps = acceptance.Steps.Where(s => TestValidator.IsAssertion(s.Action)).ToList(); TestValidator.Validate(acceptance);
            var acceptanceFile = SaveInput("acceptance", acceptance); var instructions = Path.Combine(Area("workflow-inputs"), Guid.NewGuid().ToString("N") + ".txt"); File.WriteAllText(instructions, demoInstructions.Text);
            var output = Path.Combine(Area("demonstrations"), Guid.NewGuid().ToString("N") + "-draft.json");
            var result = await Command(ct, "draft-from-demo", "--demo", demonstrationPath, "--acceptance", acceptanceFile, "--instructions", instructions, "--settings", CurrentSettings(), "--output", output, "--artifacts", Artifacts);
            if (result.ExitCode == 0) { var draftTest = JsonSerializer.Deserialize<TestCase>(File.ReadAllText(output), TestyJson.Options) ?? throw new InvalidDataException("Missing draft."); draftTest.Id = Guid.NewGuid().ToString("N"); draftTest.Category = "Recorded · not run yet"; saveTest(draftTest); demoStatus.Text = "Draft added to Tests. It has not been run yet."; }
        }, ""));
    }
    /// <summary>Names the connected app; its process ID is only in the tooltip. "Attached process: none" is kept verbatim when nothing is connected.</summary>
    private void UpdateDemoTarget()
    {
        demoTarget.Text = $"Attached process: {(pid is null ? "none" : targetTitle ?? "connected app")} · Test to draft from: {selected?.Name ?? "none"}";
        demoTarget.ToolTip = pid is null ? null : $"Process ID {pid}";
    }

    private void JobsPage()
    {
        var body = Page("Background runs", "WorkflowTabJobs", "Add a build-and-test setup to the waiting list, run it now or on a schedule. Only one test uses this PC's mouse and keyboard at a time. A schedule pauses after a run that doesn't pass; Run again starts a fresh attempt.");
        AgentPanel(body);
        var queueCard = Card(body, "Waiting list", "Setups and saved tests waiting to run, running, or finished.");
        var setup = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        setup.SetBinding(TextBlock.TextProperty, new Binding("SelectedItem.Name") { Source = profiles, StringFormat = "Setup to add: {0}", FallbackValue = "Setup to add: none yet. Save one on the Build, then test tab.", TargetNullValue = "Setup to add: none yet. Save one on the Build, then test tab." });
        queueCard.Children.Add(setup);
        Actions(queueCard, AsyncButton("Add setup to waiting list", "EnqueueLifecycle", async ct => { await Command(ct, "operations", "--operation", "enqueue", "--workspace", store.RootDirectory, "--profile", SelectedProfile()); RefreshOperations(false); }, "", "AccentIconButton"),
            Button("Refresh", "RefreshJobs", () => RefreshOperations(false), glyph: ""));
        Column(jobs, "Run", "Request.Name", true, 2); TargetColumn(jobs, "Request.Target"); Column(jobs, "Status", "Status"); TimeColumn(jobs, "Added", "CreatedAt", 1.5); Column(jobs, "Message", "Message", true, 3); Field(queueCard, "", jobs, "OperationsJobs");
        Actions(queueCard, AsyncButton("Cancel", "CancelJob", async ct => { if (jobs.SelectedItem is not OperationsJob job) throw new InvalidOperationException("Select a run."); await MutateQueue(ct, queue => queue.RequestCancellation(job.Id), "Cancellation saved. A running test stops safely; check how it ended."); }, ""),
            AsyncButton("Run again", "RerunJob", async ct => { if (jobs.SelectedItem is not OperationsJob job) throw new InvalidOperationException("Select a finished run."); await MutateQueue(ct, queue => queue.Rerun(job.Id), "A fresh attempt was added to the waiting list."); }, ""),
            Button("Open results", "JobEvidence", () => { if (jobs.SelectedItem is not OperationsJob job) throw new InvalidOperationException("Select a run."); Open(WorkspaceMaintenance.ResolveRestoredPath(store.RootDirectory, job.ArtifactDirectory)); }, glyph: ""),
            AsyncButton("Remove from list", "DeleteJob", async ct => { if (jobs.SelectedItem is not OperationsJob job) throw new InvalidOperationException("Select a finished run."); await MutateQueue(ct, queue => queue.DeleteTerminalJob(job.Id), "Removed from the list; its results stay on disk."); }, ""));
        var repeat = Card(body, "Schedules", "Repeat the setup selected on Build, then test. The interval counts from the last successful run.");
        interval.Width = 160; interval.HorizontalAlignment = HorizontalAlignment.Left;
        Field(repeat, "Repeat every (seconds, from the last successful run; at least 60)", interval, "ScheduleInterval");
        Actions(repeat, AsyncButton("Schedule setup", "AddSchedule", async ct => { await Command(ct, "operations", "--operation", "schedule-add", "--workspace", store.RootDirectory, "--profile", SelectedProfile(), "--interval-seconds", Number(interval, 60, 2592000, "Interval").ToString()); RefreshOperations(false); }, ""));
        Column(schedules, "Schedule", "Definition.Name", true, 2); TargetColumn(schedules, "Definition.Request.Target");
        schedules.Columns.Add(new DataGridTextColumn { Header = "State", Binding = new Binding("Definition.Enabled") { Converter = OnOff.Instance }, IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        TimeColumn(schedules, "Next run", "NextRunAt", 1.5); Column(schedules, "Message", "Message", true, 3); Field(repeat, "", schedules, "OperationsSchedules");
        Actions(repeat, AsyncButton("Pause", "PauseSchedule", ct => SetSchedule(false, ct), ""), AsyncButton("Resume", "ResumeSchedule", ct => SetSchedule(true, ct), ""),
            AsyncButton("Remove schedule", "DeleteSchedule", async ct => { if (schedules.SelectedItem is not OperationsSchedule schedule) throw new InvalidOperationException("Select a schedule."); await MutateQueue(ct, queue => queue.DeleteSchedule(schedule.Id), "Schedule removed. Results of earlier runs stay on disk."); }, ""));
        var local = Card(body, "Run waiting tests on this PC", "Use these while Testy is open and the background agent is off.");
        workerSeconds.Width = 160; workerSeconds.HorizontalAlignment = HorizontalAlignment.Left;
        Field(local, "Keep running for (seconds)", workerSeconds, "WorkerLifetime");
        Actions(local, processOneJob = AsyncButton("Run the next waiting test", "ProcessOneJob", async ct => await Command(ct, "operations", "--operation", "pump", "--workspace", store.RootDirectory, "--once", "--seconds", "15000"), ""),
            startLocalWorker = AsyncButton("Keep running waiting tests", "StartLocalWorker", async ct => await Command(ct, "operations", "--operation", "pump", "--workspace", store.RootDirectory, "--seconds", Number(workerSeconds, 1, 86400, "Keep running for").ToString()), ""));
    }

    private static string AgentExecutable => Path.Combine(AppContext.BaseDirectory, "Testy.Agent.exe");
    private void AgentPanel(StackPanel body)
    {
        if (CortexModelBridge.Enabled) { Card(body, "Managed by Cortex", "Use the queue controls while Cortex is running. Cortex does not install an elevated agent or start Testy at sign-in."); return; }
        var card = Card(body, "Background agent", "Runs waiting and scheduled tests at sign-in, even when Testy is closed.");
        AutomationProperties.SetAutomationId(agentStatus, "AgentStatus"); AutomationProperties.SetAutomationId(agentDetail, "AgentDetail");
        agentDetail.Style = Res("HelpText");
        card.Children.Add(agentStatus); card.Children.Add(agentDetail);
        var actions = Actions(card,
            AsyncButton("Enable at sign-in…", "AgentEnable", ct => AgentInstall("install", ct), ""),
            AsyncButton("Disable at sign-in…", "AgentDisable", ct => AgentInstall("uninstall", ct)),
            pauseAgent = Button("Pause jobs", "AgentPause", () =>
            {
                var settings = AgentFiles.LoadSettings(store.RootDirectory); settings.Paused = !settings.Paused; AgentFiles.SaveSettings(store.RootDirectory, settings);
                Status(settings.Paused ? "Background runs paused. A running test finishes; waiting tests wait." : "Background runs resumed."); RefreshAgent();
            }, false, ""),
            Button("Run queued jobs now", "AgentProcessNow", () => { AgentFiles.Submit(store.RootDirectory, "process-now"); Status("Asked the agent to run waiting tests on this PC now, without waiting for idle time."); }, false, ""));
        localRuns.Margin = new Thickness(0, 8, 8, 0); actions.Children.Add(localRuns); AutomationProperties.SetAutomationId(localRuns, "AgentLocalRuns");
        AutomationProperties.SetName(localRuns, "Tests on this PC");
        localRuns.ItemsSource = new[] { "Run tests on this PC when I'm idle for 2 minutes", "Run tests on this PC any time", "Never run tests on this PC (VMs only)" };
        var current = AgentFiles.LoadSettings(store.RootDirectory).LocalRuns;
        localRuns.SelectedIndex = current == AgentSettings.Always ? 1 : current == AgentSettings.Never ? 2 : 0;
        localRuns.SelectionChanged += (_, _) =>
        {
            var settings = AgentFiles.LoadSettings(store.RootDirectory);
            settings.LocalRuns = localRuns.SelectedIndex switch { 1 => AgentSettings.Always, 2 => AgentSettings.Never, _ => AgentSettings.WhenIdle };
            AgentFiles.SaveSettings(store.RootDirectory, settings); Status("Saved. Tests that run on this PC use its mouse and keyboard while they run.", "Success");
        };
    }
    private async Task AgentInstall(string action, CancellationToken ct)
    {
        if (CortexModelBridge.Enabled) throw new InvalidOperationException("Cortex owns Testy while its plugin is enabled. Use the Cortex queue controls; background installation and elevation are unavailable in integrated mode.");
        if (!File.Exists(AgentExecutable)) throw new FileNotFoundException("Testy.Agent.exe is not part of this Testy build. Use a published Testy package.", AgentExecutable);
        var resultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testy", "Agent", "install-result.json");
        var before = File.Exists(resultPath) ? File.GetLastWriteTimeUtc(resultPath) : DateTime.MinValue;
        Status(action == "install" ? "Waiting for administrator approval to install the background agent…" : "Waiting for administrator approval to remove the background agent…");
        var start = new ProcessStartInfo(AgentExecutable) { UseShellExecute = true, Verb = "runas", Arguments = action + " --workspace " + AgentTaskXml.Quote(store.RootDirectory) };
        Process process;
        try { process = Process.Start(start) ?? throw new InvalidOperationException("The agent installer did not start."); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { Status("Administrator approval was declined. Nothing changed.", "Warning"); return; }
        using (process) await process.WaitForExitAsync(ct);
        var result = File.Exists(resultPath) && File.GetLastWriteTimeUtc(resultPath) > before ? JsonDocument.Parse(File.ReadAllText(resultPath)).RootElement : default;
        var message = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "The agent installer finished without a result.";
        Status(message); details.Text = result.ValueKind == JsonValueKind.Object ? JsonSerializer.Serialize(result, TestyJson.Options) : "";
        RefreshAgent();
    }
    private void RefreshAgent()
    {
        if (CortexModelBridge.Enabled) { agentRunning = false; return; }
        try
        {
            var agent = AgentFiles.TryRead<AgentStatus>(AgentFiles.StatusPath(store.RootDirectory));
            agentRunning = AgentFiles.IsRunning(agent);
            var settings = AgentFiles.LoadSettings(store.RootDirectory);
            if (agentRunning && agent is not null)
            {
                var version = agent.Version == AgentVersion() ? "" : $" The agent's version ({agent.Version}) differs from this Testy; choose Enable at sign-in to update it.";
                agentStatus.Text = $"Running ({(agent.Elevated ? "administrator" : "not elevated — virtual machines are unavailable")}): {agent.Message}{version}";
                agentDetail.Text = string.Join(Environment.NewLine, agent.Waiting.Take(2).Select(w => $"Waiting · {OperationsTargets.Label(w.Target)} · {w.QueuedJobs} waiting · {w.Reason}")
                    .Concat(agent.Recent.Take(1).Select(r => $"Last: {r.Status} · {r.Name} · {OperationsTargets.Label(r.Target)} · {(r.FinishedAt is { } finished ? FriendlyTimeConverter.Format(finished) : "")}")));
            }
            else
            {
                agentStatus.Text = "Not running. Enable it to run waiting and scheduled tests in the background at sign-in (it asks for administrator approval once), or use Run waiting tests on this PC below while Testy is open.";
                agentDetail.Text = "";
            }
            if (pauseAgent is not null) pauseAgent.Content = settings.Paused ? "Resume jobs" : "Pause jobs"; // Same words as the agent's tray menu.
            if (processOneJob is not null) processOneJob.IsEnabled = !agentRunning;
            if (startLocalWorker is not null) startLocalWorker.IsEnabled = !agentRunning;
            RefreshMachines(agentRunning, agent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { agentStatus.Text = "Agent status unavailable: " + ex.Message; }
    }
    private static string AgentVersion() => typeof(AgentStatus).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    private sealed class MachineRow
    {
        public MachineInfo Machine { get; init; } = new();
        public string Name => Machine.Name;
        public string State => Machine.State + (Machine.Running && Machine.Heartbeat.Length > 0 ? $" · {Machine.Heartbeat}" : "");
        public string GuestOs => Machine.GuestOs;
        public string SignIn => Machine.CredentialStored ? Machine.CredentialUser : "Not set up";
        public string Desktop => Machine.Readiness is { Checked: true } r ? (r.DesktopUser.Length > 0 ? r.DesktopUser + (r.DesktopUnlocked ? " · unlocked" : " · locked") : "nobody signed in") : "";
        public string Readiness => !Machine.Running ? "Start the VM" : !Machine.CredentialStored ? "Set up for Testy" : Machine.Readiness is { Checked: true } r ? (r.Ready ? "Ready" : r.Reason) : "Checking…";
    }
    private void MachinesPage()
    {
        var body = Page("Virtual machines", "WorkflowTabMachines", "Hyper-V virtual machines on this PC. Testy copies its runner into a VM over the Hyper-V connection (no network needed), runs a saved test on the VM's signed-in desktop, and copies the screenshots and results back. Save an administrator sign-in for each VM and keep that account signed in with the screen unlocked (VM Connect, basic session). " + (CortexModelBridge.Enabled ? "AI requests use Cortex through PowerShell Direct; the guest needs no model credentials or internet access. Hyper-V operations use your current Windows permissions. Refresh to load the VM list." : "Allow internet access for AI-guided tests."));
        var list = Card(body, "Virtual machines");
        if (CortexModelBridge.Enabled) machinesStatus.Text = "Choose Refresh to read Hyper-V inventory with your current Windows permissions.";
        AutomationProperties.SetAutomationId(machinesStatus, "MachinesStatus"); list.Children.Add(machinesStatus);
        Column(machines, "VM", nameof(MachineRow.Name), true, 2); Column(machines, "State", nameof(MachineRow.State), true, 2); Column(machines, "Windows", nameof(MachineRow.GuestOs), true, 2);
        Column(machines, "Testy sign-in", nameof(MachineRow.SignIn), true, 2); Column(machines, "Desktop", nameof(MachineRow.Desktop), true, 2); Column(machines, "Ready?", nameof(MachineRow.Readiness), true, 3);
        Field(list, "", machines, "MachinesGrid");
        Actions(list, AsyncButton("Refresh", "MachinesRefresh", RefreshMachineInventory, ""),
            Button(CortexModelBridge.Enabled ? "Save VM sign-in…" : "Set up for Testy…", "MachineSetup", SetupSelectedMachine, false, ""),
            Button("Forget sign-in", "MachineForget", () =>
            {
                var machine = SelectedMachine(); VmCredentialStore.Delete(machine.VmId);
                machine.CredentialStored = false; machine.CredentialUser = ""; machine.Readiness = null; machines.Items.Refresh();
                Status($"The saved sign-in for {machine.Name} was removed. Files already copied into the VM are unchanged.");
                if (!CortexModelBridge.Enabled) AgentFiles.Submit(store.RootDirectory, "refresh-machines");
            }, false, ""));
        var run = Card(body, "Run a saved test on the selected VM");
        machineTest.ItemsSource = store.LoadTests(); machineTest.SelectedItem = ((IEnumerable<TestCase>)machineTest.ItemsSource).FirstOrDefault(t => t.Id == selected?.Id);
        Field(run, "Test", machineTest, "MachineTest");
        Field(run, "App to test, as a path inside the VM ({worker}\\ is Testy's folder in the VM, which includes the sample apps)", machineExe, "MachineExe");
        Field(run, "Optional: folder on this PC to copy into the VM first (the app path is then relative to it)", machineStage, "MachineStage");
        Actions(run, Button("Choose folder…", "MachineStageBrowse", () => { var path = PickFolder("Folder to copy into the VM for each run"); if (path != null) machineStage.Text = path; }, false, ""));
        machineMode.ItemsSource = new[] { "AI-guided (your current AI settings)", "Exact replay (no AI)" }; machineMode.SelectedIndex = store.LoadSettings().AiDirectedExecution ? 0 : 1;
        Field(run, "How to run", machineMode, "MachineMode"); machineProbe.Margin = new Thickness(0, 12, 0, 0); run.Children.Add(machineProbe); AutomationProperties.SetAutomationId(machineProbe, "MachineProbe");
        var limits = new UniformGrid { Columns = 2, HorizontalAlignment = HorizontalAlignment.Left, Width = 520 }; var l1 = new StackPanel { Margin = new Thickness(0, 0, 16, 0) }; var l2 = new StackPanel(); limits.Children.Add(l1); limits.Children.Add(l2);
        Field(l1, "Time limit (seconds)", machineTimeout, "MachineTimeout"); Field(l2, "Repeat every (seconds)", machineInterval, "MachineInterval"); run.Children.Add(limits);
        Actions(run, AsyncButton("Queue run on selected VM", "MachineRun", ct => QueueMachineRun(ct, schedule: false), "", "AccentIconButton"),
            AsyncButton("Schedule on selected VM", "MachineSchedule", ct => QueueMachineRun(ct, schedule: true), ""));
    }
    private MachineInfo SelectedMachine() => (machines.SelectedItem as MachineRow)?.Machine ?? throw new InvalidOperationException("Select a VM first.");
    private void RequireAgent() { if (!CortexModelBridge.Enabled && !agentRunning) throw new InvalidOperationException("Enable the background agent first (Background runs tab). VM features run through it because Hyper-V needs administrator rights."); }
    private async Task RefreshMachineInventory(CancellationToken ct)
    {
        if (!CortexModelBridge.Enabled)
        {
            RequireAgent(); AgentFiles.Submit(store.RootDirectory, "refresh-machines");
            Status("The agent is refreshing the VM list and checking set-up VMs (about a minute)."); return;
        }
        var result = await Command(ct, "vms", "--operation", "list", "--deep");
        if (result.Json.ValueKind != JsonValueKind.Object || !result.Json.TryGetProperty("machines", out _)) return;
        var inventory = result.Json.Deserialize<MachineInventory>(TestyJson.Options) ?? throw new InvalidDataException("The VM inventory was empty.");
        machinesStatus.Text = $"{inventory.Message} Updated {inventory.CollectedAt.ToLocalTime():t}.";
        ApplyMachineInventory(inventory);
    }
    private void SetupSelectedMachine()
    {
        RequireAgent(); var machine = SelectedMachine();
        if (!machine.Running) throw new InvalidOperationException($"Start {machine.Name} and sign in to its desktop first.");
        var dialog = new Window { Title = "Set up " + machine.Name, Width = 480, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = FontFamily, FontSize = FontSize, UseLayoutRounding = true };
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) }; dialog.Content = panel;
        panel.Children.Add(new TextBlock { Text = "Set up " + machine.Name, Style = Res("PaneTitle"), FontSize = 20 });
        panel.Children.Add(new TextBlock { Text = "Enter an administrator account in the VM, and keep that same account signed in to the VM's desktop. The password is stored only in Windows Credential Manager for your Windows user.", Style = Res("HelpText"), Margin = new Thickness(0, 6, 0, 0) });
        var user = new TextBox { Text = machine.CredentialUser.Length > 0 ? machine.CredentialUser : "Administrator" }; var password = new PasswordBox();
        Field(panel, "VM account", user, "MachineUser"); Field(panel, "Password", password, "MachinePassword");
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) }; error.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush"); panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = CortexModelBridge.Enabled ? "Save sign-in" : "Save and set up", IsDefault = true, Style = Res("AccentIconButton") }; var cancel = new Button { Content = "Cancel", IsCancel = true, Style = Res("IconButton"), Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetAutomationId(ok, "MachineSetupSave"); ok.Click += (_, _) => { try { VmCredentialStore.Save(machine.VmId, user.Text, password.Password); dialog.DialogResult = true; } catch (Exception ex) { error.Text = ex.Message; } };
        buttons.Children.Add(ok); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        bool saved;
        try { saved = dialog.ShowDialog() == true; }
        finally { password.Clear(); }
        if (!saved) return;
        machine.CredentialStored = true; machine.CredentialUser = user.Text; machine.Readiness = null; machines.Items.Refresh();
        if (CortexModelBridge.Enabled)
        {
            Status($"Sign-in saved for {machine.Name}. Return to Cortex and choose Prepare virtual machine to check the guest and copy the Testy runner.", "Success"); return;
        }
        AgentFiles.Submit(store.RootDirectory, "setup-vm", machine.Target);
        Status($"Sign-in saved. The agent is checking {machine.Name} and copying the Testy runner into it (the first time takes about a minute).");
    }
    private async Task QueueMachineRun(CancellationToken ct, bool schedule)
    {
        RequireAgent(); var machine = SelectedMachine();
        if (!machine.CredentialStored) throw new InvalidOperationException($"Set up {machine.Name} for Testy first.");
        if (machineTest.SelectedItem is not TestCase test) throw new InvalidOperationException("Choose a saved test.");
        TestValidator.Validate(test);
        var args = new List<string> { "operations", "--operation", schedule ? "schedule-add" : "enqueue-test", "--workspace", store.RootDirectory, "--test", SaveInput("test", test),
            "--exe", machineExe.Text.Trim(), "--target", machine.Target, "--timeout-seconds", Number(machineTimeout, 30, 7200, "Time limit").ToString(), "--name", test.Name + " · " + machine.Name };
        if (machineMode.SelectedIndex == 0) { args.Add("--settings"); args.Add(CurrentSettings()); } else args.Add("--replay");
        if (machineProbe.IsChecked == true) args.Add("--probe");
        if (machineStage.Text.Trim().Length > 0) { args.Add("--stage-directory"); args.Add(machineStage.Text.Trim()); }
        if (schedule) { args.Add("--interval-seconds"); args.Add(Number(machineInterval, 60, 2592000, "Interval").ToString()); }
        var result = await Command(ct, args.ToArray());
        if (result.ExitCode == 0) Status(CortexModelBridge.Enabled
            ? $"{(schedule ? "Scheduled" : "Queued")} on {machine.Name}. Start the queue in Cortex to process ready work while the plugin is enabled."
            : schedule ? $"Scheduled on {machine.Name}. The agent runs it when the VM is ready." : $"Queued on {machine.Name}. The agent runs it when the VM is ready; follow it on Background runs.", "Success");
        RefreshOperations(false);
    }
    private void RefreshMachines(bool running, AgentStatus? agent)
    {
        var inventory = AgentFiles.TryRead<MachineInventory>(AgentFiles.MachinesPath(store.RootDirectory));
        machinesStatus.Text = !running ? "Enable the background agent (Background runs tab) to use VMs; Hyper-V needs administrator rights, which the agent has."
            : agent is { Elevated: false } ? "The agent is running without administrator rights, so it cannot reach Hyper-V. Choose Enable at sign-in to reinstall it."
            : inventory is null ? "The agent is collecting the Hyper-V inventory…"
            : $"{inventory.Message} Updated {inventory.CollectedAt.ToLocalTime():t}.";
        ApplyMachineInventory(inventory);
    }
    private void ApplyMachineInventory(MachineInventory? inventory)
    {
        var rows = (inventory?.Machines ?? []).Select(m => new MachineRow { Machine = m }).ToList();
        bool renamed = false;
        foreach (var machine in rows.Select(r => r.Machine))
            if (!TargetNames.Instance.Names.TryGetValue(machine.VmId, out var known) || known != machine.Name) { TargetNames.Instance.Names[machine.VmId] = machine.Name; renamed = true; }
        if (renamed) { jobsRevision = ""; schedulesRevision = ""; } // Re-bind job rows so "Where" shows VM names.
        var revision = JsonSerializer.Serialize(rows.Select(r => new { r.Machine.VmId, r.State, r.SignIn, r.Desktop, r.Readiness }));
        if (revision == machinesRevision) return;
        var id = (machines.SelectedItem as MachineRow)?.Machine.VmId; machines.ItemsSource = rows; machines.SelectedItem = rows.FirstOrDefault(r => r.Machine.VmId == id) ?? rows.FirstOrDefault(); machinesRevision = revision;
    }
    private async Task SetSchedule(bool enabled, CancellationToken ct)
    {
        if (schedules.SelectedItem is not OperationsSchedule schedule) throw new InvalidOperationException("Select a schedule.");
        await MutateQueue(ct, queue => queue.SetScheduleEnabled(schedule.Id, enabled), enabled ? "Schedule resumed." : "Schedule paused. A run that already started is unchanged.");
    }
    private async Task MutateQueue(CancellationToken ct, Action<OperationsStore> mutation, string message)
    {
        // Cancellation can prevent dispatch, but cannot roll back a committed atomic store update.
        await Task.Run(() => { ct.ThrowIfCancellationRequested(); mutation(new OperationsStore(Path.Combine(store.RootDirectory, "operations"))); }, ct);
        Status(message + (ct.IsCancellationRequested ? " Stop arrived after the change was saved; check the refreshed list." : ""), "Success");
        RefreshOperations(false);
    }
    private async void RefreshOperations(bool silent)
    {
        if (refreshing) return; refreshing = true;
        try
        {
            var (j, s) = await Task.Run(() => { var queue = new OperationsStore(Path.Combine(store.RootDirectory, "operations")); return (queue.ListJobs(), queue.ListSchedules()); });
            var nextJobs = JsonSerializer.Serialize(j.Select(x => new { x.Id, x.Status, x.Request.Name, x.CreatedAt, x.StartedAt, x.FinishedAt, x.CancellationRequestedAt, x.Message, x.ArtifactDirectory, x.ActionOutcomeUnknown }));
            var nextSchedules = JsonSerializer.Serialize(s.Select(x => new { x.Id, x.Definition.Name, x.Definition.Enabled, x.NextRunAt, x.ActiveJobId, x.Message }));
            // Preserve row peers and the user's selection when nothing visible changed.
            if (nextJobs != jobsRevision) { var id = (jobs.SelectedItem as OperationsJob)?.Id; jobs.ItemsSource = j; jobs.SelectedItem = j.FirstOrDefault(x => x.Id == id); jobsRevision = nextJobs; }
            if (nextSchedules != schedulesRevision) { var id = (schedules.SelectedItem as OperationsSchedule)?.Id; schedules.ItemsSource = s; schedules.SelectedItem = s.FirstOrDefault(x => x.Id == id); schedulesRevision = nextSchedules; }
        }
        catch (Exception ex) { if (!silent) ShowError(ex); }
        finally { refreshing = false; }
    }

    private void WorkspacePage()
    {
        var body = Page("Data & backup", "WorkflowTabWorkspace", "Backups include the tests, settings and results in this data folder. Keys stay in Windows, and files outside the folder or project sources are not included. Restore checks every file and uses a new or empty folder; earlier results stay unchanged.");
        var folder = Card(body, "Data folder");
        folder.Children.Add(new TextBlock { Text = store.RootDirectory, Style = Res("MonoText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        Actions(folder, Button("Open folder", "OpenDataFolder", () => Open(store.RootDirectory), false, ""),
            Button("Open another data folder…", "OpenOtherWorkspace", () =>
            {
                if (CortexModelBridge.Enabled) throw new InvalidOperationException("This Testy window belongs to Cortex's configured workspace. Change the plugin workspace in Cortex before opening another one.");
                var path = PickFolder("Choose a data folder to open in a separate Testy window"); if (path is null) return;
                var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the Testy program.")) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                start.ArgumentList.Add("--workspace"); start.ArgumentList.Add(path); Process.Start(start);
            }, glyph: ""));
        var backup = Card(body, "Backup and restore");
        Actions(backup, AsyncButton("Create verified backup…", "BackupWorkspace", async ct =>
        {
            var dialog = new SaveFileDialog { Title = "Save the backup outside this data folder", Filter = "Testy backup ZIP|*.zip", FileName = "Testy-workspace-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip" };
            if (dialog.ShowDialog() == true) await Command(ct, "operations", "--operation", "backup", "--workspace", store.RootDirectory, "--archive", dialog.FileName);
        }, ""),
            AsyncButton("Restore into an empty folder…", "RestoreWorkspace", async ct =>
            {
                var archive = PickFile("Choose a Testy backup", "Testy backup ZIP|*.zip"); if (archive is null) return;
                var destination = PickFolder("Choose a new or empty folder to restore into"); if (destination is null) return;
                await Command(ct, "operations", "--operation", "restore", "--workspace", store.RootDirectory, "--archive", archive, "--destination", destination);
            }, ""),
            AsyncButton("Check for problems", "MigrateWorkspace", async ct => await Command(ct, "operations", "--operation", "migrate", "--workspace", store.RootDirectory), ""));
        var help = Card(body, "Documentation");
        Actions(help, Button("Open product documentation", "OpenProductDocs", () => Open(MainWindow.FindDocumentation()), glyph: ""));
    }
    private static List<string> Lines(string value) => value.Length == 0 ? [] : value.Replace("\r\n", "\n").Split('\n').ToList();
    private sealed record ProfileItem(string Path, string Name);
    private sealed class BindingRow { public string StepId { get; set; } = ""; public string Title { get; set; } = ""; public string Action { get; set; } = ""; public string Parameter { get; set; } = ""; }
    private sealed class CommandRow
    {
        public string Id { get; set; } = ""; public string Description { get; set; } = ""; public string Executable { get; set; } = "";
        public string WorkingDirectory { get; set; } = "."; public int TimeoutSeconds { get; set; } = 120; public bool Required { get; set; } public string ArgumentsText { get; set; } = "";
        public CommandRow() { }
        public CommandRow(ProjectCommandDefinition command, bool required) { Id = command.Id; Description = command.Description; Executable = command.Executable; WorkingDirectory = command.WorkingDirectory; TimeoutSeconds = command.TimeoutSeconds; Required = required; ArgumentsText = string.Join(Environment.NewLine, command.Arguments); }
        public ProjectCommandDefinition ToDefinition() => new() { Id = Id, Description = Description, Executable = Executable, WorkingDirectory = WorkingDirectory, TimeoutSeconds = TimeoutSeconds, Arguments = Lines(ArgumentsText) };
    }
}
