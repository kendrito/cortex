using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Testy.Core;
using Testy.Windows;

namespace Testy.Studio;

/// <summary>
/// The app a test is about, named in words: "New test from a description" resolves an app named in a leading clause ("In Customer Desk: …"),
/// or with no app connected an app named anywhere in it, and connects to it (starting it when needed) before planning, and stores it on the
/// new test; Run connects to (or starts) the app a test stores or names. The person never has to pick the app. An ambiguous name is answered
/// with an inline choice (AppChoiceBar), never a dialog. A manual Change app choice (a technical-details override) made for the selected test
/// stays an override for its runs.
/// </summary>
public partial class MainWindow
{
    /// <summary>Apps Studio started for a test (other than the sample app, which is <see cref="_labProcess"/>); closed with Studio like the sample.</summary>
    private readonly List<Process> _startedApps = [];
    /// <summary>
    /// The connection the person last made by hand (Change app, Open an app…, Sample app): the test that was selected then, and the process and
    /// window connected. Its runs use that app while that very window is the connected one; once Studio connects elsewhere (Run for another
    /// test connects to that test's app), the choice no longer applies and the test runs in its own app again.
    /// </summary>
    private (string TestId, int ProcessId, long WindowHandle)? _manualConnection;
    private Func<AppCandidate, Task>? _appChoiceAction;
    private const int MaximumAppChoices = 5;

    private static bool HasStoredApp(TestCase test) => !string.IsNullOrWhiteSpace(test.TargetPath) || !string.IsNullOrWhiteSpace(test.TargetAppId);
    private static string StoredAppName(TestCase test) =>
        !string.IsNullOrWhiteSpace(test.TargetName) ? test.TargetName : !string.IsNullOrWhiteSpace(test.TargetPath) ? Path.GetFileNameWithoutExtension(test.TargetPath) : test.TargetAppId;

    /// <summary>Notes a connection the person made by hand: for the selected test it overrides the app the test stores.</summary>
    private void NoteManualConnection() =>
        _manualConnection = _selected is { } test && _driver.Target is { } target ? (test.Id, target.ProcessId, target.WindowHandle) : null;
    private bool ManualOverrideFor(TestCase test) =>
        _manualConnection is { } manual && manual.TestId == test.Id && _driver.Target is { } target
        && target.ProcessId == manual.ProcessId && target.WindowHandle == manual.WindowHandle && ConnectionAlive();

    /// <summary>True while the connected app's process and window still exist (a closed app leaves a stale target behind).</summary>
    private bool ConnectionAlive()
    {
        if (_driver.Target is not { } target) return false;
        try { using var process = Process.GetProcessById(target.ProcessId); if (process.HasExited) return false; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
        return target.WindowHandle == 0 || IsWindow((nint)target.WindowHandle);
    }
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);

    /// <summary>Whether the connected app is an instance of the app <paramref name="test"/> stores.</summary>
    private bool ConnectedToStoredApp(TestCase test) =>
        ConnectionAlive() && AppDiscovery.RunningInstances(test.TargetPath, test.TargetAppId, [Environment.ProcessId]).Any(i => i.ProcessId == _driver.Target!.ProcessId);

    /// <summary>Running windows, installed apps, the apps saved tests use and Testy's sample apps (Studio itself excluded).</summary>
    /// <remarks>Reads the test list on the calling (UI) thread; the discovery itself runs on a worker thread.</remarks>
    private Task<List<AppCandidate>> DiscoverAppsAsync()
    {
        var recent = _tests.Where(HasStoredApp)
            .GroupBy(t => string.IsNullOrWhiteSpace(t.TargetAppId) ? "exe:" + (t.TargetPath ?? "").ToUpperInvariant() : "appid:" + t.TargetAppId.ToUpperInvariant())
            .Select(g => g.OrderByDescending(t => t.UpdatedAt).First()).Select(t => (t.TargetPath ?? "", t.TargetName ?? "", t.TargetAppId ?? "")).ToList();
        return Task.Run(() => AppDiscovery.Discover(new AppDiscoveryRequest { Recent = recent, SamplePaths = AppDiscovery.SamplePaths(AppContext.BaseDirectory), ExcludeProcessIds = [Environment.ProcessId] }));
    }

    /// <summary>Connects to a resolved app: attaches to a running instance, or starts the program under Testy's launch rules and attaches to its window.</summary>
    private async Task<bool> ConnectToAppAsync(AppCandidate app, string purpose, CancellationToken token)
    {
        var name = app.Name.Length > 0 ? app.Name : Path.GetFileNameWithoutExtension(app.ExePath);
        if (app.IsRunning)
        {
            await RefreshTargets(token);
            await Attach(app.ProcessId!.Value, token, announce: false);
            SetStatus($"Connected to {name} {purpose}.", ActivityLevel.Success, "App");
            return false;
        }
        SetStatus($"Starting {name} {purpose}…", ActivityLevel.Info, "App");
        var windowPid = await StartAppAsync(app, name, token);
        await RefreshTargets(token);
        await Attach(windowPid, token, announce: false);
        SetStatus($"Started {name} and connected to it {purpose}.", ActivityLevel.Success, "App");
        return true;
    }

    /// <summary>
    /// Starts a program the way the MCP server's launch_app does: a local .exe with the Windows GUI subsystem that is not a shell, terminal or
    /// script host; a packaged app only when the program its manifest names passes the same checks. Returns the pid that shows its window.
    /// </summary>
    private async Task<int> StartAppAsync(AppCandidate app, string name, CancellationToken token)
    {
        const string Alternative = " Start it yourself, then try again: Testy connects to the running app.";
        if (app.Packaged && app.AppId.Length > 0)
        {
            var program = await Task.Run(() => PackagedApps.ProgramFor(app.AppId, app.PackageInstallPath.Length > 0 ? app.PackageInstallPath : null), token);
            if (program is null) throw new InvalidOperationException($"{name} is a packaged app whose program Testy cannot read, so Testy does not start it." + Alternative);
            if (ExecutableRules.LaunchProblem(program) is { } refused) throw new InvalidOperationException(refused + Alternative);
            var requested = DateTimeOffset.UtcNow;
            var pid = await Task.Run(() => PackagedApps.Activate(app.AppId), token);
            try
            {
                var process = Process.GetProcessById(pid);
                // A single-instance app answers with its existing process: that one is the person's and is never closed by Studio.
                if (process.StartTime.ToUniversalTime() >= requested.UtcDateTime.AddSeconds(-2)) _startedApps.Add(process); else process.Dispose();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(20))
            {
                token.ThrowIfCancellationRequested();
                var window = AppDiscovery.RunningInstances(program, app.AppId, [Environment.ProcessId]).FirstOrDefault();
                if (window?.ProcessId is { } shown) return shown;
                await Task.Delay(250, token);
            }
            throw new TimeoutException($"{name} started but didn't open a window within 20 seconds." + Alternative);
        }
        if (app.ExePath.Length == 0) throw new InvalidOperationException($"Testy does not know which program starts {name}." + Alternative);
        var exe = Path.GetFullPath(app.ExePath);
        if (exe.StartsWith(@"\\", StringComparison.Ordinal)) throw new InvalidOperationException($"{exe} is not on a local drive, so Testy does not start it." + Alternative);
        if (ExecutableRules.LaunchProblem(exe) is { } problem) throw new InvalidOperationException(problem + Alternative);
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        CortexModelBridge.ScrubTarget(start); // the app under test never inherits Cortex's model bridge credentials, as with Open an app…
        var started = Process.Start(start) ?? throw new InvalidOperationException($"Windows could not start {name}.");
        var isSample = string.Equals(exe, TryFindLab(), StringComparison.OrdinalIgnoreCase) && (_labProcess is null || _labProcess.HasExited);
        if (isSample) { _labProcess?.Dispose(); _labProcess = started; } else _startedApps.Add(started);
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(200, token); started.Refresh();
            if (started.HasExited) throw new InvalidOperationException($"{name} closed before it opened a window." + Alternative);
            if (started.MainWindowHandle != IntPtr.Zero && IsShown(started.MainWindowHandle)) return started.Id;
        }
        throw new TimeoutException($"{name} started but didn't open a window within 20 seconds. Try again once its window is open: Testy connects to the running app.");
    }
    private string? TryFindLab()
    {
        try { return Path.GetFullPath(FindLab()); }
        catch (FileNotFoundException) { return null; }
    }

    /// <summary>The app a test stores, as a candidate to start: its packaged app or its program.</summary>
    private static AppCandidate? StoredAppCandidate(TestCase test)
    {
        if (!string.IsNullOrWhiteSpace(test.TargetAppId))
            return new AppCandidate { Kind = AppCandidateKind.Recent, Name = StoredAppName(test), AppId = test.TargetAppId.Trim(), Packaged = true, ExePath = test.TargetPath ?? "" };
        // A network path is never opened, not even to check it: that would authenticate this user to the host the test file names.
        if (!ExecutableRules.IsLocalDrivePath(test.TargetPath) || !File.Exists(test.TargetPath)) return null;
        var program = AppDiscovery.FromProgram(test.TargetPath, AppCandidateKind.Recent);
        program.Name = StoredAppName(test);
        return program;
    }

    // ═══════════════ The inline app choice ═══════════════
    /// <summary>Shows the candidates of an ambiguous name as buttons in the editor (AppChoiceBar); choosing one runs <paramref name="action"/> with it.</summary>
    private void ShowAppChoice(string text, IReadOnlyList<AppCandidate> candidates, Func<AppCandidate, Task> action)
    {
        HideEditorMessage();
        AppChoiceText.Text = text;
        AppChoiceButtons.Children.Clear();
        var number = 0;
        foreach (var candidate in candidates.Take(MaximumAppChoices))
        {
            number++;
            var label = candidate.Name.Length > 0 ? candidate.Name : Path.GetFileNameWithoutExtension(candidate.ExePath);
            // Two windows of one app usually share a title, so a running one is told apart by its process; the title is in the tooltip.
            var detail = candidate.IsRunning ? $"running, process {candidate.ProcessId}" : candidate.Packaged ? "installed (Store app)" : candidate.Kind == AppCandidateKind.Sample ? "sample app" : "installed";
            var button = new Button
            {
                Style = (Style)FindResource("IconButton"), Content = $"{label} · {detail}", Margin = new Thickness(0, 0, 8, 6),
                ToolTip = (candidate.IsRunning ? $"“{candidate.WindowTitle}”, process {candidate.ProcessId}\n" : "") + (candidate.Packaged ? candidate.AppId : candidate.ExePath)
            };
            AutomationProperties.SetAutomationId(button, "AppChoice" + number);
            AutomationProperties.SetName(button, $"{label}, {detail}");
            button.Click += async (_, _) => await ChooseAppAsync(candidate);
            AppChoiceButtons.Children.Add(button);
        }
        _appChoiceAction = action;
        AppChoiceBar.Visibility = Visibility.Visible;
        Log(ActivityLevel.Warning, "App", text);
    }
    private void HideAppChoice()
    {
        if (AppChoiceBar is null) return;
        AppChoiceBar.Visibility = Visibility.Collapsed;
        AppChoiceButtons.Children.Clear();
        _appChoiceAction = null;
    }
    private async Task ChooseAppAsync(AppCandidate candidate)
    {
        if (_busy || _appChoiceAction is not { } action) return;
        HideAppChoice();
        await action(candidate);
    }
    private void DismissAppChoice_Click(object sender, RoutedEventArgs e)
    {
        HideAppChoice();
        SetStatus("No app was chosen; nothing was created or run.", ActivityLevel.Info, "App");
    }

    /// <summary>The contenders of an ambiguous resolution: the matches within the margin of the best one.</summary>
    private static List<AppCandidate> Contenders(AppResolution resolution)
    {
        if (resolution.Matches.Count == 0) return [];
        var top = resolution.Matches[0].Confidence;
        return resolution.Matches.Where(m => m.Confidence >= top - AppResolver.AmbiguityMargin - 1e-9).Select(m => m.Candidate).ToList();
    }
}
