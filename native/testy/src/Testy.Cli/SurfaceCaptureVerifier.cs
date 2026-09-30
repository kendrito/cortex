using System.Diagnostics;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Testy.Core;
using Testy.Windows;

namespace Testy.Cli;

internal static class SurfaceCaptureVerifier
{
    internal static async Task<VerificationReport> VerifyAsync(string executable, string artifacts, CancellationToken ct)
    {
        executable = Path.GetFullPath(executable); artifacts = Path.GetFullPath(artifacts); Directory.CreateDirectory(artifacts);
        if (!File.Exists(executable) || Path.GetFileName(executable) != "Testy.WpfLab.exe") throw new ArgumentException("Surface verification requires owned Testy.WpfLab.exe.");
        var report = new VerificationReport { Executable = executable, PlannedChecks = 8 };
        Process? process = null, foreign = null; using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct); limit.CancelAfter(TimeSpan.FromMinutes(3)); var token = limit.Token;
        string stage = "Launch owned capture fixture";
        try
        {
            process = Start(); report.OwnedProcessId = process.Id; await DesktopVerifierSupport.Ready(process, token);
            using var driver = new UiAutomationDriver(); await driver.AttachAsync(process.Id, token); var native = new NativeComputerActionExecutor(driver);
            await Act(StepAction.Click, "OpenAuxiliary");
            stage = "Composite includes owned auxiliary at its actual screen position";
            await Snapshot("auxiliary-opening-tree.json", s => UiSelectors.Find(s, "id:CaptureAuxiliary").Count == 1);
            string composite = await Capture("auxiliary-composite.png");
            var both = await Snapshot("auxiliary-tree.json", s => UiSelectors.Find(s, "id:CaptureAuxiliary").Count == 1); var main = Find(both, "CaptureLabWindow").Bounds; var aux = Find(both, "CaptureAuxiliary").Bounds;
            var (width, height, pixels) = Pixels(composite);
            DesktopVerifierSupport.Require(width == (int)both.ScreenshotBounds.Width && height == (int)both.ScreenshotBounds.Height && width > main.Width, "Image dimensions do not match union bounds.");
            int gapX = (int)((main.X + main.Width + aux.X) / 2 - both.ScreenshotBounds.X), gapY = (int)(aux.Y + aux.Height / 2 - both.ScreenshotBounds.Y);
            int gapIndex = (gapY * width + gapX) * 4;
            DesktopVerifierSupport.Require(pixels[gapIndex] == 32 && pixels[gapIndex + 1] == 32 && pixels[gapIndex + 2] == 32, "Window gap contains pixels instead of neutral background.");
            Pass(stage, "Owned auxiliary and main window are composed with physical union bounds; gap is neutral, never desktop pixels.");

            stage = "Neutral canvas gaps reject input before foreground or mouse dispatch";
            await Reject(new { type = "click", x = gapX, y = gapY, button = "left" }, "gap"); Pass(stage, "Gap rejected with no input receipt.");
            stage = "Changed surface layout rejects stale coordinates";
            await Act(StepAction.Click, "CloseAuxiliary");
            await Snapshot("auxiliary-closed.json", s => UiSelectors.Find(s, "id:CaptureAuxiliary").Count == 0);
            await Reject(new { type = "move", x = 20, y = 20 }, "surfaces"); Pass(stage, "Closing captured auxiliary invalidated the frame before foreground/input.");

            stage = "Titleless popup pixels are present in composed screenshot";
            await Act(StepAction.Expand, "CaptureChoice"); await Task.Delay(150, token);
            var expanded = await Snapshot("popup-tree.json", s => s.Elements.Any(e => e.Name == "Orange choice" && !e.IsOffscreen)); string popupImage = await Capture("popup-composite.png"); var popupPixels = Pixels(popupImage);
            int magenta = 0;
            for (int i = 0; i < popupPixels.Pixels.Length; i += 4) if (popupPixels.Pixels[i] == 190 && popupPixels.Pixels[i + 1] == 41 && popupPixels.Pixels[i + 2] == 217) magenta++;
            DesktopVerifierSupport.Require(magenta > 5000, "Known magenta popup client content is missing from the screenshot.");
            DesktopVerifierSupport.Require(expanded.Elements.Any(e => e.Name == "Orange choice" && !e.IsOffscreen), "Popup option missing from owned snapshot.");
            using (var probe = new WpfProbeDriver())
            {
                await probe.AttachAsync(process.Id, token); var probePopup = await probe.SnapshotAsync(token);
                WorkspaceStore.WriteAtomic(Path.Combine(artifacts, "probe-popup-tree.json"), probePopup);
                var probeOptions = UiSelectors.Find(probePopup, "id:CaptureOptionOrange");
                DesktopVerifierSupport.Require(!probePopup.IsTruncated && probeOptions.Count == 1 && probeOptions[0].IsEnabled && !probeOptions[0].IsOffscreen, "Probe omitted or disabled the actual HwndSource popup root.");
            }
            Pass(stage, $"Captured {magenta} exact fixture popup-color pixels with its visible option tree.");
            await Act(StepAction.Collapse, "CaptureChoice");

            stage = "Physical click reaches the observed owned control";
            // Explicit legitimate focus preparation of the selected target control. This does
            // not bypass Native.Foreground or any hit/window-identity checks.
            var control = AutomationElement.FromHandle(process.MainWindowHandle).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "NativeText"));
            control.SetFocus(); await Task.Delay(150, token);
            var ready = await Snapshot("physical-before.json"); await Capture("physical-before.png"); var click = Center(ready, "NativeIncrement");
            await Native(new { type = "click", x = click.X, y = click.Y, button = "left" });
            DesktopVerifierSupport.Require(native.LastReceipt?.InputDelivered == true, "Click lacked synchronized-input acknowledgement.");
            DesktopVerifierSupport.Require(Find(await Snapshot("physical-click-after.json"), "NativeCount").Value == "1", "Physical click did not increment the owned counter.");
            Pass(stage, "Counter increment and synchronized-input receipt both verified.");

            stage = "Physical keyboard input changes only the selected owned editor";
            control.SetFocus(); ready = await Snapshot("keyboard-before.json"); await Capture("keyboard-before.png");
            await Native(new { type = "type", text = "Physical Ω" });
            DesktopVerifierSupport.Require(native.LastReceipt?.InputDelivered == true, "Typing lacked synchronized-input acknowledgement.");
            DesktopVerifierSupport.Require(Find(await Snapshot("typed.json"), "NativeText").Value == "Physical Ω", "Unicode input did not reach the selected editor.");
            await Capture("keyboard-replace.png"); await Native(new { type = "keypress", keys = new[] { "CTRL", "A" } });
            DesktopVerifierSupport.Require(native.LastReceipt?.InputDelivered == true, "Key chord lacked synchronized-input acknowledgement.");
            await Capture("keyboard-selected.png"); await Native(new { type = "type", text = "Replaced" });
            DesktopVerifierSupport.Require(Find(await Snapshot("keyboard-after.json"), "NativeText").Value == "Replaced", "Selection/replacement did not reach the selected editor.");
            Pass(stage, "Unicode text, Ctrl+A and replacement have matching UI state; direct typing and key chord acknowledged.");

            stage = "Raw popup coordinates select exactly; missing peer acknowledgement cannot credit semantic Select";
            await Act(StepAction.Expand, "CaptureChoice"); await Task.Delay(150, token); ready = await Snapshot("popup-click-before.json"); await Capture("popup-click-before.png");
            var option = ready.Elements.Where(e => e.Name == "Orange choice" && e.ControlType == "ListItem" && !e.IsOffscreen && e.Bounds.Width > 0).First();
            int ox = (int)(option.Bounds.X + option.Bounds.Width / 2 - ready.ScreenshotBounds.X), oy = (int)(option.Bounds.Y + option.Bounds.Height / 2 - ready.ScreenshotBounds.Y);
            var popupAction = JsonSerializer.SerializeToElement(new { type = "click", x = ox, y = oy, button = "left" });
            await native.ExecuteAsync(popupAction, token);
            var popupAfter = await Snapshot("popup-click-after.json");
            await Capture("popup-click-after.png");
            DesktopVerifierSupport.Require(Find(popupAfter, "CaptureChoice").Value == "Orange choice", "Popup image-coordinate click selected the wrong option.");
            // Feed the real before/after trees, action and unmodified receipt through the public
            // coverage checker. These small observation envelopes are verifier-created, not a
            // claimed model run. Raw coordinates must not silently become semantic selection.
            ComputerToolObservation[] popupObservations = [new() { ToolName = "observe_application", Snapshot = ready }, new() { NativeAction = popupAction, NativeReceipt = native.LastReceipt, Snapshot = popupAfter, Execution = new() { Status = RunStatus.Passed } }];
            var rawCoverage = SavedWorkflowVerifier.Verify(new() { Steps = [new() { Action = StepAction.CoordinateClick, X = ox, Y = oy }] }, popupObservations);
            var semanticCoverage = SavedWorkflowVerifier.Verify(new() { Steps = [new() { Action = StepAction.Select, Selector = "id:CaptureChoice", Value = "Orange choice" }] }, popupObservations);
            WorkspaceStore.WriteAtomic(Path.Combine(artifacts, "popup-click-receipt.json"), new { receipt = native.LastReceipt, observedSelection = Find(popupAfter, "CaptureChoice").Value, rawCoordinateCoverage = rawCoverage, semanticSelectCoverage = semanticCoverage, envelopeSource = "Verifier adapter around actual driver evidence; no model call" });
            DesktopVerifierSupport.Require(rawCoverage.Complete, "Actual raw coordinate action was not covered.");
            DesktopVerifierSupport.Require(!semanticCoverage.Complete, "Unacknowledged/distinct-peer popup input was incorrectly credited as semantic Select.");
            Pass(stage, "Raw coordinate click selected exact Orange choice. Semantic Select remains unverified and is rejected by saved-workflow coverage; no acknowledgement was invented.");

            stage = "Composite and tree exclude a different owned PID";
            foreign = Start(); await DesktopVerifierSupport.Ready(foreign, token);
            using var foreignDriver = new UiAutomationDriver(); await foreignDriver.AttachAsync(foreign.Id, token);
            var foreignTree = await foreignDriver.SnapshotAsync(token); var foreignIds = foreignTree.Elements.Select(e => e.RuntimeId).ToHashSet(StringComparer.Ordinal);
            var own = await Snapshot("foreign-exclusion.json"); await Capture("foreign-exclusion.png");
            DesktopVerifierSupport.Require(own.Elements.All(e => !foreignIds.Contains(e.RuntimeId)) && own.Elements.Count(e => e.AutomationId == "CaptureLabWindow") == 1, "Foreign process elements entered owned evidence.");
            DesktopVerifierSupport.Require(own.ScreenshotBounds.Width == main.Width && own.ScreenshotBounds.Height == main.Height, "Foreign process expanded the target canvas.");
            Pass(stage, "A second same-title owned process is excluded by PID from tree and canvas.");
            token.ThrowIfCancellationRequested(); report.CompletedAllScenarios = true;

            async Task Act(StepAction action, string id) => await driver.ExecuteAsync(new() { Action = action, Selector = "id:" + id, TimeoutMs = 6000 }, token);
            async Task<UiSnapshot> Snapshot(string name, Func<UiSnapshot, bool>? ready = null)
            {
                var timer = Stopwatch.StartNew(); UiSnapshot snapshot;
                do { snapshot = await driver.SnapshotAsync(token); if (!snapshot.IsTruncated && (ready?.Invoke(snapshot) ?? true)) break; await Task.Delay(80, token); } while (timer.Elapsed < TimeSpan.FromSeconds(6));
                DesktopVerifierSupport.Require(!snapshot.IsTruncated && snapshot.Target.ProcessId == process.Id && (ready?.Invoke(snapshot) ?? true), "Owned snapshot unavailable, incomplete or expected fixture state not ready.");
                WorkspaceStore.WriteAtomic(Path.Combine(artifacts, name), snapshot); return snapshot;
            }
            Task<string> Capture(string name) => driver.CaptureAsync(Path.Combine(artifacts, name), token);
            Task Native(object action) => native.ExecuteAsync(JsonSerializer.SerializeToElement(action), token);
            async Task Reject(object action, string expected)
            {
                bool rejected = false;
                try { await Native(action); } catch (InvalidOperationException ex) when (ex.Message.Contains(expected, StringComparison.OrdinalIgnoreCase)) { rejected = true; }
                DesktopVerifierSupport.Require(rejected && native.LastReceipt is null, "Negative coordinate action was not rejected before dispatch.");
            }
        }
        catch (Exception ex) { report.Checks.Add(new() { Name = stage, Driver = "Owned WPF surfaces / native input", Passed = false, Expected = RunStatus.Passed, Actual = ex is OperationCanceledException ? RunStatus.Cancelled : RunStatus.Failed, Message = ex.ToString() }); }
        finally
        {
            if (!(await DesktopVerifierSupport.Stop(process) & await DesktopVerifierSupport.Stop(foreign))) report.CompletedAllScenarios = false;
            report.Finish(ct); WorkspaceStore.WriteAtomic(Path.Combine(artifacts, "surfaces-report.json"), report);
        }
        return report;

        Process Start() { var start = new ProcessStartInfo(executable) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden }; start.ArgumentList.Add("--capture-lab"); return Process.Start(start) ?? throw new InvalidOperationException("Owned capture fixture did not launch."); }
        void Pass(string name, string message) { report.Checks.Add(new() { Name = name, Driver = "Owned WPF surfaces / native input", Passed = true, Expected = RunStatus.Passed, Actual = RunStatus.Passed, Message = message }); WorkspaceStore.WriteAtomic(Path.Combine(artifacts, "surfaces-report.json"), report); }
    }
    private static UiElementInfo Find(UiSnapshot snapshot, string id) => UiSelectors.Find(snapshot, "id:" + id).Single();
    private static (int X, int Y) Center(UiSnapshot snapshot, string id) { var e = Find(snapshot, id); return ((int)(e.Bounds.X + e.Bounds.Width / 2 - snapshot.ScreenshotBounds.X), (int)(e.Bounds.Y + e.Bounds.Height / 2 - snapshot.ScreenshotBounds.Y)); }
    private static (int Width, int Height, byte[] Pixels) Pixels(string path)
    {
        using var stream = File.OpenRead(path); var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var image = new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0); var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(pixels, image.PixelWidth * 4, 0); return (image.PixelWidth, image.PixelHeight, pixels);
    }
}
