using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Testy.Core;
using Testy.Windows;

namespace Testy.Cli;

/// <summary>Actual owned Studio ComboBox regression. No model calls or changes to the user's workspace.</summary>
internal static class PopupVerifier
{
    internal static async Task<VerificationReport> VerifyAsync(string executable, string artifacts, CancellationToken ct)
    {
        executable = Path.GetFullPath(executable); artifacts = Path.GetFullPath(artifacts);
        if (!File.Exists(executable) || Path.GetFileName(executable) != "Testy.Studio.exe") throw new ArgumentException("verify-popup requires the included Testy.Studio.exe.");
        Directory.CreateDirectory(artifacts);
        var report = new VerificationReport { Executable = executable, PlannedChecks = 3 };
        var stores = new Dictionary<string, WorkspaceStore>(StringComparer.Ordinal);
        Process? target = null, foreign = null;
        string stage = "Launch isolated Studio fixtures";
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct); limit.CancelAfter(TimeSpan.FromSeconds(75));
        var token = limit.Token;
        try
        {
            foreign = Start("foreign"); await Ready(foreign, token);
            using var otherDriver = new UiAutomationDriver { MaxElements = 3500 };
            await otherDriver.AttachAsync(foreign.Id, token);
            var foreignSnapshot = await CompleteSnapshot(otherDriver, token);
            var foreignIds = foreignSnapshot.Elements.Select(e => e.RuntimeId).Where(s => s.Length > 0).ToHashSet(StringComparer.Ordinal);
            target = Start("target"); report.OwnedProcessId = target.Id; await Ready(target, token);
            using var driver = new UiAutomationDriver { MaxElements = 3500 };
            await driver.AttachAsync(target.Id, token);
            await CompleteSnapshot(driver, token);
            await Execute(StepAction.Click, "NavSettings");
            await CompleteSnapshot(driver, token);
            stage = "Expand a real titleless WPF ComboBox popup";
            await Execute(StepAction.Expand, "ProviderPicker");
            var timer = Stopwatch.StartNew(); List<nint> popups;
            do { popups = TitlelessWindows(target.Id); if (popups.Count > 0) break; await Task.Delay(80, token); } while (timer.Elapsed < TimeSpan.FromSeconds(5));
            Require(popups.Count > 0, "Expanding ProviderPicker did not create a visible same-process titleless top-level HWND; regression was not exercised.");
            WorkspaceStore.WriteAtomic(Path.Combine(artifacts, "popup-windows.json"), new { observedAt = DateTimeOffset.UtcNow, targetProcessId = target.Id, excludedProcessId = foreign.Id, titlelessHandles = popups.Select(h => h.ToInt64()).ToArray() });
            Pass(stage, $"Observed {popups.Count} real visible titleless top-level HWND(s) belonging to the selected PID.");

            stage = "Complete popup tree preserves unique options and excludes another process";
            var expanded = await CompleteSnapshot(driver, token);
            WorkspaceStore.WriteAtomic(Path.Combine(artifacts, "expanded-tree.json"), expanded);
            Require(expanded.Target.ProcessId == target.Id, "Popup snapshot target PID changed.");
            string[] choices = ["Codex", "OpenAI", "Compatible", "Offline"];
            foreach (string choice in choices)
                Require(UiSelectors.Find(expanded, "query:{\"label\":\"" + choice + "\",\"type\":\"ListItem\",\"ancestor\":{\"id\":\"ProviderPicker\"}}").Count == 1,
                    $"Expected exactly one provider option {choice} within the explicit ProviderPicker ancestor.");
            foreach (nint popup in popups)
            {
                var element = AutomationElement.FromHandle(popup);
                Require(element.Current.ProcessId == target.Id, "Popup HWND identity changed to a different process.");
                string identity = string.Join('.', element.GetRuntimeId());
                Require(expanded.Elements.Count(e => e.RuntimeId == identity) == 1, "Visible popup root is missing or duplicated in the complete snapshot.");
            }
            Require(expanded.Elements.All(e => !foreignIds.Contains(e.RuntimeId)), "Snapshot included runtime identities from a different owned process.");
            Require(expanded.Elements.Count(e => e.AutomationId == "ProviderPicker") == 1, "Target control became duplicated across processes or top-level trees.");
            const string ambiguousChoice = "query:{\"label\":\"Codex\",\"type\":\"ListItem\"}";
            Require(UiSelectors.Find(expanded, ambiguousChoice).Count > 1, "The logical/visual peer ambiguity was not exercised.");
            bool rejected = false;
            try { await driver.ExecuteAsync(new() { Action = StepAction.Click, Selector = ambiguousChoice, TimeoutMs = 10000 }, token); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("matched", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("ambiguous", StringComparison.OrdinalIgnoreCase)) { rejected = true; }
            Require(rejected, "An unscoped duplicated popup option was not rejected before dispatch.");
            var afterRejected = await CompleteSnapshot(driver, token);
            Require(UiSelectors.Find(afterRejected, "id:ProviderPicker").Single().Value == "Offline", "Rejected ambiguous selection changed the provider.");
            await driver.CaptureAsync(Path.Combine(artifacts, "expanded-popup.png"), token);
            Pass(stage, "All four logical options are unique within ProviderPicker and each titleless root appears once; distinct logical/visual peers remain globally ambiguous and are rejected before input. The second owned Studio's identities are excluded.");

            stage = "Same-value selection permits the next ordinary action";
            await Execute(StepAction.Select, "ProviderPicker", "Offline");
            var selected = await CompleteSnapshot(driver, token);
            Require(UiSelectors.Find(selected, "id:ProviderPicker").Single().Value == "Offline", "Same-value selection changed the expected provider.");
            WorkspaceStore.WriteAtomic(Path.Combine(artifacts, "after-select-tree.json"), selected);
            await Execute(StepAction.TypeText, "ModelName", "popup-regression-model");
            await Execute(StepAction.Click, "SaveConnection");
            timer.Restart();
            while (stores["target"].LoadSettings().Model != "popup-regression-model" && timer.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(80, token);
            var saved = stores["target"].LoadSettings();
            Require(saved.Kind == ProviderKind.Offline && saved.Model == "popup-regression-model", "The next model field edit and SaveConnection invocation did not persist.");
            var final = await CompleteSnapshot(driver, token);
            WorkspaceStore.WriteAtomic(Path.Combine(artifacts, "after-save-tree.json"), final);
            await driver.CaptureAsync(Path.Combine(artifacts, "after-save.png"), token);
            Pass(stage, "Selected Offline again, edited the model textbox and invoked SaveConnection successfully; no implicit popup-collapse workaround was added.");
            token.ThrowIfCancellationRequested(); report.CompletedAllScenarios = true;

            async Task Execute(StepAction action, string id, string value = "") => await driver.ExecuteAsync(new() { Action = action, Selector = "id:" + id, Value = value, TimeoutMs = 10000 }, token);
        }
        catch (Exception ex) { report.Checks.Add(new() { Name = stage, Driver = "UIA / real WPF popup", Passed = false, Expected = RunStatus.Passed, Actual = ex is OperationCanceledException ? RunStatus.Cancelled : RunStatus.Failed, Message = ex.ToString() }); }
        finally
        {
            bool clean = await Stop(target) & await Stop(foreign);
            if (!clean) { report.CompletedAllScenarios = false; report.Checks.Add(new() { Name = "Owned fixture cleanup", Passed = false, Expected = RunStatus.Passed, Actual = RunStatus.Failed, Message = "An owned Studio process did not exit within the cleanup bound." }); }
            report.Finish(ct);
            WorkspaceStore.WriteAtomic(Path.Combine(artifacts, "popup-report.json"), report);
        }
        return report;

        Process Start(string name)
        {
            var store = new WorkspaceStore(Path.Combine(artifacts, name + "-workspace-" + Guid.NewGuid().ToString("N")));
            stores[name] = store; store.SaveSettings(new() { Kind = ProviderKind.Offline, AiDirectedExecution = false });
            var info = StudioLaunch.StartInfo(executable, store.RootDirectory);
            return Process.Start(info) ?? throw new InvalidOperationException("Owned Studio could not start.");
        }
        void Pass(string name, string message) => report.Checks.Add(new() { Name = name, Driver = "UIA / real WPF popup", Passed = true, Expected = RunStatus.Passed, Actual = RunStatus.Passed, Message = message });
    }
    private static async Task Ready(Process process, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested(); process.Refresh();
            if (process.HasExited) throw new InvalidOperationException("Owned Studio exited during startup.");
            if (process.MainWindowHandle != 0) return;
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Owned Studio did not open its primary window.");
            await Task.Delay(80, ct);
        }
    }
    private static async Task<UiSnapshot> CompleteSnapshot(ITargetDriver driver, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var snapshot = await driver.SnapshotAsync(ct);
            if (!snapshot.IsTruncated && snapshot.Elements.Count > 0) return snapshot;
            if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new InvalidOperationException("Popup UI tree remained incomplete; no selector action was permitted.");
            await Task.Delay(80, ct);
        }
    }
    private static async Task<bool> Stop(Process? process)
    {
        if (process is null) return true;
        try
        {
            if (!process.HasExited) { process.CloseMainWindow(); await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(2000)); }
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(3000)); }
            return process.HasExited;
        }
        catch { return false; }
        finally { process.Dispose(); }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static List<nint> TitlelessWindows(int processId)
    {
        var result = new List<nint>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner == processId && IsWindowVisible(window)) { var title = new StringBuilder(2048); GetWindowText(window, title, title.Capacity); if (title.Length == 0) result.Add(window); }
            return true;
        }, 0);
        return result;
    }
    private delegate bool EnumProc(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int length);
}
