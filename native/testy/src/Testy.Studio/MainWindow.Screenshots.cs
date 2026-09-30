using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Testy.Core;

namespace Testy.Studio;

/// <summary>
/// Hidden review aid, never used in normal runs:
/// <c>--screenshot-dir DIR [--screenshot-pages tests,lastrun,activity,search,improve,json,results,inspect,settings,agentaccess,externalchange,automate[:TabAutomationId],help,changeapp,stepoptions]
/// [--screenshot-prefix NAME] [--screenshot-setup none|lab|run] [--screenshot-test NAME]</c>. Use it with a scratch <c>--workspace</c> and <c>--theme light|dark</c>.
/// Each page is captured from the screen as shown (Mica backdrop and flyouts included), then Studio closes; no preference is saved in this session.
/// "run" connects the sample app and replays "Create a customer" (or the sample named by --screenshot-test) with replay settings held in memory only.
/// "agentaccess" is Settings scrolled to the Agent access card with the HTTP listener on; "externalchange" edits the selected test, changes it
/// through the real MCP server and captures the "changed outside Studio" bar. "screenshots" is kept as another name for "lastrun".
/// </summary>
public partial class MainWindow
{
    private async Task RunScreenshotModeAsync()
    {
        var args = Environment.GetCommandLineArgs();
        if (App.Option(args, "--screenshot-dir") is not { } directory) return;
        var pages = (App.Option(args, "--screenshot-pages") ?? "tests").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var prefix = App.Option(args, "--screenshot-prefix") ?? "studio";
        var setup = App.Option(args, "--screenshot-setup") ?? "none";
        Directory.CreateDirectory(directory);
        StudioPreferences.Current.DisablePersistenceForSession();
        Topmost = true; Activate();
        if (setup is "lab" or "run")
        {
            LaunchLab_Click(this, new RoutedEventArgs()); await WaitIdle();
            Topmost = true; Activate();
        }
        var sampleName = App.Option(args, "--screenshot-test") ?? "Create a customer";
        if (setup == "run" && _tests.FirstOrDefault(t => t.Name == sampleName) is { } sample)
        {
            var saved = _settings; _settings = TestyJson.Clone(saved); _settings.Kind = ProviderKind.Offline; _settings.AiDirectedExecution = false; _settings.LiveReview = false;
            SelectTest(sample.Id); RunTest_Click(this, new RoutedEventArgs()); await WaitIdle();
            _settings = saved; LoadConnectionFields();
        }
        foreach (var page in pages)
        {
            var name = page.ToLowerInvariant();
            var tab = name.StartsWith("automate:", StringComparison.Ordinal) ? page["automate:".Length..] : null;
            Window? capture = this;
            switch (tab is null ? name : "automate")
            {
                case "tests": ShowPage(LibraryPage); ShowEditorView(EditorView.Steps); if (StepsGrid.Items.Count > 1 && StepsGrid.SelectedIndex < 0) StepsGrid.SelectedIndex = 1; break;
                case "lastrun" or "screenshots": ShowPage(LibraryPage); ShowEditorView(EditorView.LastRun); break;
                case "running" when _driver.Target != null && _selected != null:
                {
                    // A live run, captured while it runs (replay settings held in memory only, as for setup "run").
                    ShowPage(LibraryPage);
                    var saved = _settings; _settings = TestyJson.Clone(saved); _settings.Kind = ProviderKind.Offline; _settings.AiDirectedExecution = false; _settings.LiveReview = false;
                    RunTest_Click(this, new RoutedEventArgs());
                    for (var i = 0; i < 40 && (_shownRun?.Steps.Count ?? 0) < 3; i++) await Task.Delay(50);
                    CaptureScreen(this, Path.Combine(directory, $"{prefix}-running.png"));
                    await WaitIdle(); _settings = saved; LoadConnectionFields();
                    continue;
                }
                case "activity": ShowPage(LibraryPage); ShowEditorView(EditorView.LastRun); OpenActivity(ActivityScope.ThisRun); break;
                case "search": ShowPage(LibraryPage); GlobalSearch.Focus(); GlobalSearch.Text = "customer"; break;
                case "improve": ShowPage(LibraryPage); ShowEditorView(EditorView.Steps); Improve_Click(this, new RoutedEventArgs()); break;
                case "json": ShowPage(LibraryPage); ShowEditorView(EditorView.Json); break;
                case "results": RunsNav_Click(this, new RoutedEventArgs()); if (RunsGrid.Items.Count > 0) RunsGrid.SelectedIndex = 0; break;
                case "inspect": InspectorNav_Click(this, new RoutedEventArgs()); await WaitIdle(); break;
                case "settings": SettingsNav_Click(this, new RoutedEventArgs()); break;
                case "agentaccess":
                    SettingsNav_Click(this, new RoutedEventArgs());
                    if (McpListenToggle.IsChecked != true) McpListenToggle.IsChecked = true; // starts the HTTP listener as the switch does
                    for (var i = 0; i < 200 && McpUrlBox.Text.Length == 0 && McpListenToggle.IsChecked == true; i++) await Task.Delay(100);
                    SettingsScroll.ScrollToEnd();
                    break;
                case "externalchange" when _selected != null:
                    ShowPage(LibraryPage); ShowEditorView(EditorView.Steps);
                    await ChangeSelectedTestOutsideAsync();
                    break;
                case "automate": Workflows_Click(this, new RoutedEventArgs()); if (tab != null) _automate?.SelectTab(tab); break;
                case "help": ShowPage(LibraryPage); OpenFlyout(HelpFlyout); break;
                case "changeapp": ShowPage(LibraryPage); ChangeApp_Click(this, new RoutedEventArgs()); break;
                case "stepoptions":
                    ShowPage(LibraryPage); ShowEditorView(EditorView.Steps); if (StepsGrid.Items.Count > 0) StepsGrid.SelectedIndex = 0;
                    _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () => StepOptions_Click(this, new RoutedEventArgs()));
                    await Task.Delay(600); capture = OwnedWindows.OfType<StepOptionsWindow>().FirstOrDefault();
                    break;
                default: continue;
            }
            await Task.Delay(1200);
            if (capture != null) CaptureScreen(capture, Path.Combine(directory, $"{prefix}-{name.Replace(':', '-')}.png"));
            if (capture is StepOptionsWindow dialog) dialog.Close();
            CloseFlyouts(); CloseActivity();
            if (name == "search") { _searchUpdating = true; GlobalSearch.Text = ""; _searchUpdating = false; }
        }
        Topmost = false;
        Close();
    }

    private async Task WaitIdle()
    {
        await Task.Delay(300);
        for (var i = 0; _busy && i < 600; i++) await Task.Delay(100);
        await Task.Delay(300);
    }

    /// <summary>
    /// For the "externalchange" capture: gives the selected test an unsaved edit, then changes its file the way an agent does (update_test through the
    /// real MCP server over stdio; a plain write when no server is built) and waits for the "changed outside Studio" bar.
    /// </summary>
    private async Task ChangeSelectedTestOutsideAsync()
    {
        var test = _selected!;
        TestIntent.Text = TestIntent.Text.TrimEnd() + " Edited in Studio, not saved yet.";
        const string intent = "Changed by an outside agent through MCP while Studio held unsaved edits.";
        try
        {
            if (_mcpCommand is { } command)
            {
                await using var session = McpStdioSession.Start(command);
                await session.InitializeAsync(TimeSpan.FromSeconds(20));
                await session.CallToolAsync("update_test", new JsonObject { ["testId"] = test.Id, ["intent"] = intent }, TimeSpan.FromSeconds(20));
            }
            else { var copy = TestyJson.Clone(test); copy.Intent = intent; WorkspaceStore.WriteAtomic(TestPath(test.Id), copy); }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception or JsonException)
        { SetStatus("The outside change could not be made: " + ex.Message, ActivityLevel.Error, "Screenshots"); }
        for (var i = 0; i < 100 && ExternalChangeBar.Visibility != Visibility.Visible; i++) await Task.Delay(100);
    }

    /// <summary>Copies the window's on-screen pixels (extended frame bounds), so the Mica backdrop and open popups appear as the user sees them.</summary>
    private static void CaptureScreen(Window window, string path)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (DwmGetWindowAttribute(handle, 9, out Rect32 bounds, Marshal.SizeOf<Rect32>()) != 0) GetWindowRect(handle, out bounds);
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        nint screen = GetDC(0), memory = CreateCompatibleDC(screen), bitmap = CreateCompatibleBitmap(screen, width, height), previous = SelectObject(memory, bitmap);
        try
        {
            BitBlt(memory, 0, 0, width, height, screen, bounds.Left, bounds.Top, 0x00CC0020 | 0x40000000); // SRCCOPY | CAPTUREBLT
            SelectObject(memory, previous);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = File.Create(path); encoder.Save(stream);
        }
        finally { DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(0, screen); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect32 { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out Rect32 value, int size);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect32 rect);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
