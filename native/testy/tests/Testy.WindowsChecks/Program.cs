using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Testy.Core;
using Testy.Windows;

var project = FindRoot();
if (args.Length > 0 && args[0] == "--composite-capture")
{
    CompositeCaptureChecks.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(project, "verification/composite-capture.json"));
    return;
}
if (args.Length > 0 && args[0] == "--capture-quality")
{
    CaptureQualityChecks.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(project, "verification/capture-quality.json"));
    return;
}
var lab = args.Length > 0 ? Path.GetFullPath(args[0]) : new[] { "src/Testy.TestLab/bin/Debug/net9.0-windows/Testy.TestLab.exe", "src/Testy.TestLab/bin/Release/net9.0-windows/Testy.TestLab.exe", "dist/TestLab/Testy.TestLab.exe" }.Select(p => Path.Combine(project, p)).FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("Build Testy.TestLab first, or pass its EXE path as argument 1.");
var output = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(project, "verification/supplemental"); Directory.CreateDirectory(output);
var results = new List<object>();
var startedAt = DateTimeOffset.UtcNow;
File.WriteAllText(Path.Combine(output, "supplemental-results.json"), JsonSerializer.Serialize(new { passed = false, status = "running", startedAt, checks = results }, TestyJson.Options));
using var process = Process.Start(new ProcessStartInfo(lab) { UseShellExecute = false })!;
try
{
    await Task.Delay(900);
    foreach (bool probe in new[] { false, true })
    {
        Console.WriteLine("Supplemental mode: " + (probe ? "probe" : "uia"));
        using ITargetDriver driver = probe ? new WpfProbeDriver() : new UiAutomationDriver();
        await driver.AttachAsync(process.Id);
        async Task Action(StepAction action, string selector = "", string value = "") { await driver.ExecuteAsync(new TestStep { Action = action, Selector = selector, Value = value }); await Task.Delay(150); }
        async Task Expect(string id, string text)
        {
            var snapshot = await driver.SnapshotAsync(); var value = snapshot.Elements.Single(e => e.AutomationId == id).Value;
            if (value != text) throw new Exception($"{id}: expected {text}, observed {value}");
        }
        await Action(StepAction.Click, "id:ResetButton");
        await Action(StepAction.TypeText, "id:CustomerName", "Keyboard Test");
        await Action(StepAction.TypeText, "id:CustomerEmail", "keyboard@example.test");
        await Action(StepAction.KeyPress, "id:AddCustomer", "SPACE");
        await Expect("StatusMessage", "Customer added: Keyboard Test");
        results.Add(new { mode = probe ? "probe" : "uia", check = "Guarded physical keypress", passed = true });
        var before = await driver.SnapshotAsync();
        var window = before.Elements.Single(e => e.AutomationId == "CustomerDeskWindow").Bounds;
        var reset = before.Elements.Single(e => e.AutomationId == "ResetButton").Bounds;
        await driver.ExecuteAsync(new TestStep { Action = StepAction.CoordinateClick, X = (int)(reset.X + reset.Width / 2 - window.X), Y = (int)(reset.Y + reset.Height / 2 - window.Y) });
        await Task.Delay(150); await Expect("StatusMessage", "Ready for a new customer.");
        results.Add(new { mode = probe ? "probe" : "uia", check = "Window-relative coordinate click", passed = true });
        bool rejected = false;
        try { await driver.ExecuteAsync(new TestStep { Action = StepAction.CoordinateClick, X = -1, Y = -1 }); } catch (ArgumentOutOfRangeException) { rejected = true; }
        if (!rejected) throw new Exception("Out-of-bounds coordinate was not rejected.");
        results.Add(new { mode = probe ? "probe" : "uia", check = "Out-of-bounds click rejected", passed = true });
        await Action(StepAction.Click, "id:OpenReview");
        await Expect("ReviewSummary", "0 customer records in this session.");
        var capture = await driver.CaptureAsync(Path.Combine(output, probe ? "probe-modal.png" : "uia-modal.png"));
        await Action(StepAction.Click, "id:CloseReview");
        if ((await driver.SnapshotAsync()).Elements.Any(e => e.AutomationId == "ReviewDialog")) throw new Exception("Modal did not close.");
        results.Add(new { mode = probe ? "probe" : "uia", check = "Same-process modal inspect, capture, and close", passed = true });
        for (int i = 0; i < 2; i++)
        {
            await Action(StepAction.TypeText, "id:CustomerName", "Duplicate Marker");
            await Action(StepAction.TypeText, "id:CustomerEmail", $"duplicate{i}@example.test");
            await Action(StepAction.Click, "id:AddCustomer");
        }
        rejected = false;
        try { await Action(StepAction.Click, "name:Duplicate Marker"); } catch (InvalidOperationException ex) when (ex.Message.Contains("ambiguous")) { rejected = true; }
        if (!rejected) throw new Exception("Ambiguous name was not rejected.");
        results.Add(new { mode = probe ? "probe" : "uia", check = "Ambiguous name rejected", passed = true });
        await Action(StepAction.Click, "id:ResetButton");
        var native = new NativeComputerActionExecutor(driver);
        await driver.CaptureAsync(Path.Combine(output, "native-before.png"));
        async Task NativeAction(object action)
        {
            var actionJson = JsonSerializer.SerializeToElement(action);
            var beforeNative = await driver.SnapshotAsync();
            await native.ExecuteAsync(actionJson);
            if (!probe && actionJson.GetProperty("type").GetString() is "click" or "type" or "keypress")
            {
                if (native.LastReceipt is not { InputDelivered: true } receipt || receipt.ProcessId != process.Id || receipt.HitRuntimeIds.Count == 0)
                    throw new Exception("Native action lacks synchronized input delivery: " + actionJson.GetRawText() + " Receipt=" + JsonSerializer.Serialize(native.LastReceipt, TestyJson.Options));
                if (!beforeNative.Elements.Any(e => !string.IsNullOrEmpty(e.RuntimeId) && receipt.HitRuntimeIds.Contains(e.RuntimeId)))
                    throw new Exception("Native receipt does not match an actual inspected UIA runtime identity.");
            }
            await Task.Delay(120);
            await driver.CaptureAsync(Path.Combine(output, (probe ? "probe" : "uia") + "-native-latest.png"));
        }
        async Task<object> Center(string id, string type = "click")
        {
            var snapshot = await driver.SnapshotAsync(); var root = snapshot.Elements.Single(e => e.AutomationId == "CustomerDeskWindow").Bounds; var control = snapshot.Elements.Single(e => e.AutomationId == id).Bounds;
            return new { type, x = (int)(control.X + control.Width / 2 - root.X), y = (int)(control.Y + control.Height / 2 - root.Y), button = "left" };
        }
        await NativeAction(await Center("CustomerName"));
        await NativeAction(new { type = "type", text = "Zoë 東京" });
        await NativeAction(await Center("CustomerEmail"));
        await NativeAction(new { type = "type", text = "unicode@example.test" });
        await NativeAction(await Center("AddCustomer"));
        await Expect("StatusMessage", "Customer added: Zoë 東京");
        results.Add(new { mode = probe ? "probe" : "uia", check = "Native click and Unicode type produce verified customer", passed = true });
        await NativeAction(await Center("CustomerName"));
        await NativeAction(new { type = "keypress", keys = new[] { "CTRL", "A" } });
        await NativeAction(new { type = "type", text = "Replacement" });
        await Expect("CustomerName", "Replacement");
        results.Add(new { mode = probe ? "probe" : "uia", check = "Native keypress chord selects text for replacement", passed = true });
        await NativeAction(await Center("CustomerName", "double_click"));
        await NativeAction(await Center("CustomerEmail", "move"));
        await NativeAction(new { type = "scroll", x = 700, y = 400, scroll_x = 0, scroll_y = 100 });
        await NativeAction(new { type = "drag", path = new[] { new { x = 130, y = 310 }, new { x = 170, y = 310 } } });
        results.Add(new { mode = probe ? "probe" : "uia", check = "Native double_click, move, scroll, drag dispatch inside target", passed = true });
        rejected = false;
        try { await native.ExecuteAsync(JsonSerializer.SerializeToElement(new { type = "launch_shell" })); } catch (NotSupportedException) { rejected = true; }
        if (!rejected) throw new Exception("Unsupported native action accepted.");
        rejected = false;
        try { await native.ExecuteAsync(JsonSerializer.SerializeToElement(new { type = "keypress", keys = new[] { "CTRL", "ESC" } })); } catch (InvalidOperationException) { rejected = true; }
        if (!rejected) throw new Exception("Global system chord was accepted.");
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); rejected = false;
        try { await native.ExecuteAsync(JsonSerializer.SerializeToElement(new { type = "click", x = 20, y = 20 }), cancel.Token); } catch (OperationCanceledException) { rejected = true; }
        if (!rejected) throw new Exception("Cancelled input was accepted.");
        results.Add(new { mode = probe ? "probe" : "uia", check = "Unsupported actions, global chords, cancelled input rejected", passed = true });
        if (driver is UiAutomationDriver uia)
        {
            uia.MaxElements = 2; if (!(await driver.SnapshotAsync()).IsTruncated) throw new Exception("Truncation was not recorded.");
            rejected = false;
            try { await driver.ExecuteAsync(new TestStep { Action = StepAction.Click, Selector = "id:ResetButton" }); } catch (InvalidOperationException ex) when (ex.Message.Contains("truncated")) { rejected = true; }
            if (!rejected) throw new Exception("Mutation of truncated UI was not rejected.");
            results.Add(new { mode = "uia", check = "Truncated snapshot blocks mutations", passed = true });
        }
    }
    Console.WriteLine(JsonSerializer.Serialize(new { passed = true, checks = results }, TestyJson.Options));
    File.WriteAllText(Path.Combine(output, "supplemental-results.json"), JsonSerializer.Serialize(new { passed = true, startedAt, finishedAt = DateTimeOffset.UtcNow, checks = results }, TestyJson.Options));
}
catch (Exception ex)
{
    File.WriteAllText(Path.Combine(output, "supplemental-results.json"), JsonSerializer.Serialize(new { passed = false, startedAt, finishedAt = DateTimeOffset.UtcNow, error = ex.ToString(), checks = results }, TestyJson.Options));
    throw;
}
finally { if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(2000)) process.Kill(); } }

static string FindRoot()
{
    foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) && Directory.Exists(Path.Combine(directory.FullName, "src/Testy.Windows"))) return directory.FullName;
    throw new DirectoryNotFoundException("Run from the Testy repository or its built test output.");
}
