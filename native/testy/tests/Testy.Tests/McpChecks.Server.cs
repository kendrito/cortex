using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Testy.Cli;
using Testy.Cli.Mcp;
using Testy.Core;

namespace Testy.Tests;

/// <summary>Launch policy, cancellation, framing, the stateless revision, runs (with a stand-in driver and execution), files and process lifetime.</summary>
internal static partial class McpChecks
{
    // ═══════════════ Launch policy ═══════════════
    private static byte[] Image(ushort subsystem, ushort characteristics = 0x0002, ushort magic = 0x20B, int length = 512)
    {
        var bytes = new byte[length];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        const int offset = 0x80;
        BitConverter.TryWriteBytes(bytes.AsSpan(0x3C), offset);
        if (length < offset + 24 + 70) return bytes;
        bytes[offset] = (byte)'P'; bytes[offset + 1] = (byte)'E';
        BitConverter.TryWriteBytes(bytes.AsSpan(offset + 4 + 18), characteristics);
        BitConverter.TryWriteBytes(bytes.AsSpan(offset + 24), magic);
        BitConverter.TryWriteBytes(bytes.AsSpan(offset + 24 + 68), subsystem);
        return bytes;
    }
    private static string Refusal(Action action)
    {
        try { action(); }
        catch (McpToolException ex) { return ex.Message; }
        throw new InvalidOperationException("The launch policy accepted what it must refuse.");
    }

    private static Task LaunchPolicy()
    {
        // The PE header decides: only the Windows GUI subsystem is a desktop program.
        Check(McpLaunchPolicy.ReadImage(Image(2)).Problem is null, "A GUI-subsystem image is a desktop program.");
        Check(McpLaunchPolicy.ReadImage(Image(2, magic: 0x10B)).Problem is null, "A 32-bit GUI image is a desktop program.");
        Check(McpLaunchPolicy.ReadImage(Image(3)).Problem!.Contains("console program", StringComparison.Ordinal), "A console-subsystem image is refused.");
        Check(McpLaunchPolicy.ReadImage(Image(1)).Problem!.Contains("subsystem", StringComparison.Ordinal), "A native-subsystem image is refused.");
        Check(McpLaunchPolicy.ReadImage(Image(2, characteristics: 0x2002)).Problem!.Contains("library", StringComparison.Ordinal), "A DLL is refused even with the GUI subsystem.");
        Check(McpLaunchPolicy.ReadImage(Image(2, length: 0x90)).Problem!.Contains("truncated", StringComparison.Ordinal), "A truncated header is refused.");
        Check(McpLaunchPolicy.ReadImage(Encoding.ASCII.GetBytes(new string('x', 600))).Problem!.Contains("MZ", StringComparison.Ordinal), "A file that is not a program is refused.");
        Check(McpLaunchPolicy.ReadImage(Image(2, magic: 0x107)).Problem is not null, "An image with an unknown optional header is refused.");
        var noSignature = Image(2); noSignature[0x80] = 0;
        Check(McpLaunchPolicy.ReadImage(noSignature).Problem!.Contains("PE", StringComparison.Ordinal), "An image without the PE signature is refused.");

        // Paths that must never be touched: the probes record every call.
        var touched = new List<string>();
        var policy = new McpLaunchPolicy { FileExists = path => { touched.Add(path); return File.Exists(path); }, DirectoryExists = path => { touched.Add(path); return Directory.Exists(path); } };
        foreach (var path in new[] { @"\\server\share\app.exe", "//server/share/app.exe", @"\\?\C:\Windows\notepad.exe", @"\\.\PhysicalDrive0\app.exe", @"\\?\UNC\server\share\app.exe" })
            Check(Refusal(() => policy.RequireLaunchable(path)).Contains("local drive", StringComparison.Ordinal), $"{path} must be refused as a network or device path.");
        foreach (var path in new[] { "notepad", "notepad.exe", @"apps\app.exe", @"..\app.exe", @"C:app.exe", @"\Windows\notepad.exe" })
            Check(Refusal(() => policy.RequireLaunchable(path)).Contains("full path", StringComparison.Ordinal) || Refusal(() => policy.RequireLaunchable(path)).Contains("local drive", StringComparison.Ordinal), $"{path} must be refused as a relative path.");
        Check(Refusal(() => policy.RequireLaunchable(@"C:\Apps\app.exe:stream.exe")).Contains("colon", StringComparison.Ordinal), "An alternate data stream is not a program path.");
        Check(Refusal(() => policy.RequireDirectory(@"\\server\share", "workingDirectory")).Contains("local drive", StringComparison.Ordinal), "A network working directory is refused.");
        Check(Refusal(() => policy.RequireDirectory("relative", "workingDirectory")).Contains("full path", StringComparison.Ordinal), "A relative working directory is refused.");
        Check(Refusal(() => McpLaunchPolicy.TargetPath(@"\\server\share\app.exe", "targetPath")).Contains("local drive", StringComparison.Ordinal), "A network targetPath is refused.");
        Check(touched.Count == 0, "A refused path form must be rejected before any file-system call; touched: " + string.Join(", ", touched));

        // Shells and script hosts by name, without reading them; console programs by their header; renamed copies by header or version resource.
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        foreach (var name in new[] { "cmd.exe", "wscript.exe", "cscript.exe", "mshta.exe", "rundll32.exe", "regsvr32.exe", "msiexec.exe", "conhost.exe", @"WindowsPowerShell\v1.0\powershell.exe" })
            Check(Refusal(() => policy.RequireLaunchable(Path.Combine(system, name))).Contains("shell, script host or system launcher", StringComparison.Ordinal), $"{name} must be refused by name.");
        Check(touched.Count == 0, "A program refused by name is not read.");
        Check(Refusal(() => policy.RequireLaunchable(@"C:\Apps\readme.txt")).Contains(".exe", StringComparison.Ordinal), "Only .exe files are started.");
        var folder = Path.Combine(Root, "policy");
        Directory.CreateDirectory(folder);
        var renamedShell = Path.Combine(folder, "app.exe");
        File.Copy(Path.Combine(system, "cmd.exe"), renamedShell, overwrite: true);
        Check(Refusal(() => policy.RequireLaunchable(renamedShell)).Contains("console program", StringComparison.Ordinal), "A copy of cmd.exe under another name is a console program and is refused.");
        var renamedHost = Path.Combine(folder, "viewer.exe");
        File.Copy(Path.Combine(system, "wscript.exe"), renamedHost, overwrite: true);
        Check(Refusal(() => policy.RequireLaunchable(renamedHost)).Contains("wscript", StringComparison.Ordinal), "A copy of wscript.exe under another name is recognized by its version resource.");
        var text = Path.Combine(folder, "notes.exe");
        File.WriteAllText(text, "not a program");
        Check(Refusal(() => policy.RequireLaunchable(text)).Contains("not a Windows program", StringComparison.Ordinal), "A text file named .exe is refused.");
        Check(Refusal(() => policy.RequireLaunchable(Path.Combine(folder, "missing.exe"))).Contains("does not exist", StringComparison.Ordinal), "A missing program is reported.");

        // Real desktop programs are accepted: the idle fixture, and the sample app when it is built.
        EnsureRuntime();
        Check(policy.RequireLaunchable(IdleFixture) == IdleFixture, "The idle fixture is a GUI program and must be accepted.");
        if (TestyMcpService.FindSampleApp() is { } sample) Check(policy.RequireLaunchable(sample) == Path.GetFullPath(sample), "Testy.TestLab.exe must be accepted.");

        // Narrower policies.
        Check(Refusal(() => new McpLaunchPolicy(allowLaunch: false).RequireLaunchable(IdleFixture)).Contains("--no-launch", StringComparison.Ordinal), "--no-launch starts nothing.");
        var fixtureFolder = Path.GetDirectoryName(IdleFixture)!;
        Check(new McpLaunchPolicy(allowedExecutables: [fixtureFolder]).RequireLaunchable(IdleFixture) == IdleFixture, "A program inside an allowed folder is started.");
        Check(new McpLaunchPolicy(allowedExecutables: [IdleFixture]).RequireLaunchable(IdleFixture) == IdleFixture, "An allowed file is started.");
        Check(Refusal(() => new McpLaunchPolicy(allowedExecutables: [folder]).RequireLaunchable(IdleFixture)).Contains(folder, StringComparison.OrdinalIgnoreCase), "A program outside the allowed locations is refused, and the message names them.");
        Check(Refusal(() => new McpLaunchPolicy(allowedExecutables: [fixtureFolder + "X"]).RequireLaunchable(IdleFixture)).Contains("outside", StringComparison.Ordinal), "A folder whose name merely starts like an allowed one does not count.");
        using var self = Process.GetCurrentProcess();
        new McpLaunchPolicy(allowedTargets: [self.ProcessName]).RequireTarget(self.Id);
        new McpLaunchPolicy(allowedTargets: [self.MainModule!.FileName]).RequireTarget(self.Id);
        Check(Refusal(() => new McpLaunchPolicy(allowedTargets: ["some-other-app"]).RequireTarget(self.Id)).Contains("some-other-app", StringComparison.Ordinal), "A process that is not an allowed target is refused.");
        var described = new McpLaunchPolicy(allowLaunch: false, allowedTargets: ["desk"]).Describe();
        Check(described["launch"]!.GetValue<string>().Contains("--no-launch", StringComparison.Ordinal) && described["allowedTargets"]!.AsArray().Count == 1 && described["refusedPrograms"]!.AsArray().Count == McpLaunchPolicy.RefusedPrograms.Length, "The policy describes itself.");
        Check(described["rules"]!.GetValue<string>().Contains("Windows itself", StringComparison.Ordinal) && described["rules"]!.GetValue<string>().Contains("argument", StringComparison.Ordinal), "The rules name the Windows-program and argument rules.");

        // Program launchers and GUI script hosts that Windows ships: refused by name wherever they are, with or without arguments.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var launcher in new[] { Path.Combine(windows, "explorer.exe"), Path.Combine(system, "pcalua.exe"), Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell_ise.exe"), Path.Combine(windows, "hh.exe"), Path.Combine(system, "mmc.exe"), Path.Combine(system, "runas.exe"), Path.Combine(system, "forfiles.exe") })
            Check(Refusal(() => policy.RequireLaunchable(launcher)).Contains("shell, script host or system launcher", StringComparison.Ordinal), $"{Path.GetFileName(launcher)} starts other programs or scripts and must be refused by name.");
        // Every other program of Windows itself: refused unless --allow-exe names it; the application under test is almost never one.
        var notepad = Path.Combine(system, "notepad.exe");
        if (File.Exists(notepad) && McpLaunchPolicy.ReadImage(notepad).Problem is null)
        {
            Check(Refusal(() => policy.RequireLaunchable(notepad)).Contains("Windows itself", StringComparison.Ordinal) && Refusal(() => policy.RequireLaunchable(notepad)).Contains("--allow-exe", StringComparison.Ordinal),
                "A GUI program inside the Windows folder is refused and the message names --allow-exe.");
            Check(new McpLaunchPolicy(allowedExecutables: [notepad]).RequireLaunchable(notepad) == notepad, "--allow-exe naming a program of Windows allows it.");
            Check(new McpLaunchPolicy(allowedExecutables: [system]).RequireLaunchable(notepad) == notepad, "--allow-exe naming its folder allows it too.");
            Check(Refusal(() => new McpLaunchPolicy(allowedExecutables: [system]).RequireLaunchable(Path.Combine(system, "pcalua.exe"))).Contains("shell, script host or system launcher", StringComparison.Ordinal), "A refused launcher stays refused even inside an allowed folder.");
        }
        Check(ExecutableRules.WindowsComponentOf(IdleFixture) is null, "A program outside Windows is not a Windows component.");
        // A copy of a Windows program elsewhere, even one without a recorded name, is recognized by the product its version resource names.
        var pcalua = Path.Combine(system, "pcalua.exe");
        if (File.Exists(pcalua))
        {
            var copied = Path.Combine(folder, "helper.exe");
            File.Copy(pcalua, copied, overwrite: true);
            Check(ExecutableRules.WindowsComponentOf(copied) is { } component && component.Contains("Windows", StringComparison.Ordinal), "A copy of a Windows program is recognized by its version resource.");
            Check(Refusal(() => policy.RequireLaunchable(copied)).Contains("Windows itself", StringComparison.Ordinal), "A copied Windows launcher under another name is refused.");
        }
        var aliases = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        Check(ExecutableRules.WindowsComponentOf(aliases) is { } alias && alias.Contains("alias", StringComparison.Ordinal), "An app execution alias is not the program itself.");

        // Arguments that name a refused program: a launcher that is allowed would start it. No file is touched.
        var touchedBefore = touched.Count;
        foreach (var (arguments, named) in new (string[] Arguments, string? Named)[]
        {
            (["cmd"], "cmd"), (["powershell.exe"], "powershell"), (["-a", Path.Combine(system, "cmd.exe")], "cmd"), (["/run=wscript.exe"], "wscript"),
            (["\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -c x"], "pwsh"), (["--open", "C:/Windows/System32/mshta.exe"], "mshta"), ([@"C:\Tools\explorer.EXE"], "explorer"),
            (["open the control panel"], null), (["--mode", "normal"], null), (["report.txt"], null), ([@"C:\Data\customers.csv"], null), (["--name=cmdline"], null), ([], null)
        })
            Check(ExecutableRules.RefusedArgument(arguments) == named, $"Arguments [{string.Join(" | ", arguments)}] must name {named ?? "no refused program"}; got {ExecutableRules.RefusedArgument(arguments) ?? "none"}.");
        Check(Refusal(() => McpLaunchPolicy.RequireArguments(["-a", "cmd.exe"])).Contains("args names cmd", StringComparison.Ordinal), "An argument naming a refused program is refused with the rule.");
        McpLaunchPolicy.RequireArguments(["--customers", "100", @"C:\Data\orders.json"]);
        Check(touched.Count == touchedBefore, "The argument rule touches no file.");
        return Task.CompletedTask;
    }

    private static async Task LaunchRefusals()
    {
        await using var harness = new Harness("launch-refusals");
        await harness.InitializeAsync();
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var before = Process.GetProcessesByName("cmd").Length + Process.GetProcessesByName("wscript").Length + Process.GetProcessesByName("mshta").Length;
        foreach (var (exe, reason) in new[] { (Path.Combine(system, "cmd.exe"), "shell"), (Path.Combine(system, "wscript.exe"), "script host"), (Path.Combine(system, "mshta.exe"), "script host"), (Path.Combine(system, "where.exe"), "console program"), (@"\\localhost\c$\Windows\notepad.exe", "local drive"), ("notepad.exe", "full path") })
        {
            var launch = await harness.Client.CallToolAsync("launch_app", new { exe, args = new[] { "/c", "exit" } });
            Check(launch.Error is null && launch.IsError && launch.Text.Contains(reason, StringComparison.Ordinal), $"launch_app must refuse {exe} with an isError result that names the rule: " + launch.Text);
            Check(launch.Text.Contains("list_apps", StringComparison.Ordinal) || launch.Text.Contains("full path", StringComparison.Ordinal) || launch.Text.Contains("local drive", StringComparison.Ordinal), "The refusal must name the alternative: " + launch.Text);
        }
        // Windows' own program launchers and GUI script hosts, and the other programs of Windows, with and without arguments.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var explorers = Process.GetProcessesByName("explorer").Length;
        foreach (var (exe, args, reason) in new (string Exe, string[] Args, string Reason)[]
        {
            (Path.Combine(windows, "explorer.exe"), [], "system launcher"), (Path.Combine(windows, "explorer.exe"), [Path.Combine(system, "cmd.exe")], "args names cmd"),
            (Path.Combine(system, "pcalua.exe"), ["-a", "notepad.exe"], "system launcher"), (Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell_ise.exe"), [], "system launcher"),
            (Path.Combine(system, "notepad.exe"), [], "Windows itself"),
            (IdleFixture, [Path.Combine(system, "cmd.exe")], "args names cmd"), (IdleFixture, ["-a", "powershell"], "args names powershell"), (IdleFixture, ["/run=mshta.exe"], "args names mshta")
        })
        {
            var launch = await harness.Client.CallToolAsync("launch_app", new { exe, args, waitForWindowSeconds = 1 });
            Check(launch.Error is null && launch.IsError && launch.Text.Contains(reason, StringComparison.Ordinal), $"launch_app must refuse {Path.GetFileName(exe)} [{string.Join(" ", args)}] with an isError result that names the rule ({reason}): " + launch.Text);
        }
        Check(Process.GetProcessesByName("explorer").Length <= explorers, "No shell window may have been opened.");
        var created = (await harness.Client.CallToolAsync("create_test", new { name = "Refusals", intent = "run_test applies the launch policy.", steps = CustomerSteps("Ada") })).RequireOk("create_test").Structured.GetProperty("testId").GetString();
        var run = await harness.Client.CallToolAsync("run_test", new { testId = created, exe = Path.Combine(system, "cmd.exe") });
        Check(run.IsError && run.Text.Contains("shell", StringComparison.Ordinal), "run_test exe applies the same policy: " + run.Text);
        var runArgs = await harness.Client.CallToolAsync("run_test", new { testId = created, exe = IdleFixture, args = new[] { "--then", @"C:\Windows\System32\wscript.exe" } });
        Check(runArgs.IsError && runArgs.Text.Contains("args names wscript", StringComparison.Ordinal), "run_test applies the argument rule too: " + runArgs.Text);
        var runLauncher = await harness.Client.CallToolAsync("run_test", new { testId = created, exe = Path.Combine(system, "pcalua.exe") });
        Check(runLauncher.IsError && runLauncher.Text.Contains("system launcher", StringComparison.Ordinal), "run_test refuses a program launcher: " + runLauncher.Text);
        var workingDirectory = await harness.Client.CallToolAsync("launch_app", new { exe = IdleFixture, workingDirectory = @"\\localhost\c$" });
        Check(workingDirectory.IsError && workingDirectory.Text.Contains("workingDirectory", StringComparison.Ordinal) && workingDirectory.Text.Contains("local drive", StringComparison.Ordinal), "A network working directory is refused: " + workingDirectory.Text);
        var target = await harness.Client.CallToolAsync("create_test", new { name = "x", intent = "y", targetPath = @"\\server\share\app.exe", steps = CustomerSteps("Ada") });
        Check(target.IsError && target.Text.Contains("targetPath", StringComparison.Ordinal), "A network targetPath is refused: " + target.Text);
        Check(await harness.LaunchedCountAsync() == 0, "A refused launch leaves nothing running.");
        Check(Process.GetProcessesByName("cmd").Length + Process.GetProcessesByName("wscript").Length + Process.GetProcessesByName("mshta").Length <= before, "No shell or script host may have been started.");
        var policy = (await harness.Client.CallToolAsync("get_workspace_info")).RequireOk("get_workspace_info").Structured.GetProperty("launchPolicy");
        Check(policy.GetProperty("refusedPrograms").EnumerateArray().Select(p => p.GetString()).Contains("powershell") && policy.GetProperty("rules").GetString()!.Contains("not a sandbox", StringComparison.Ordinal), "get_workspace_info reports the launch policy.");
        await using var locked = new Harness("launch-disabled", policy: new McpLaunchPolicy(allowLaunch: false));
        await locked.InitializeAsync();
        var disabled = await locked.Client.CallToolAsync("launch_app", new { exe = IdleFixture });
        Check(disabled.IsError && disabled.Text.Contains("--no-launch", StringComparison.Ordinal), "A server started with --no-launch starts nothing.");
    }

    // ═══════════════ Cancellation, progress, batches, framing ═══════════════
    private static async Task Cancellation()
    {
        await using var harness = new Harness("cancel");
        await harness.InitializeAsync();
        var watch = Stopwatch.StartNew();
        var pending = harness.Client.StartToolAsync("launch_app", IdleLaunch(30), JsonValue.Create("slow-1"), progressToken: "slow");
        await harness.Client.WaitForNotificationAsync(n => n.GetProperty("method").GetString() == "notifications/progress", TimeSpan.FromSeconds(10));
        var pid = await harness.LaunchedPidAsync();
        var lines = harness.Client.RawLines.Count;
        await harness.Client.NotifyAsync("notifications/cancelled", new { requestId = "slow-1", reason = "unit check" });
        await RequireGoneAsync(pid, "The idle fixture of the cancelled launch", 6000);
        Check(watch.Elapsed < TimeSpan.FromSeconds(10), $"Cancellation took {watch.Elapsed.TotalSeconds:F1} s.");
        var progressBefore = harness.Client.Notifications.Count(n => n.GetProperty("method").GetString() == "notifications/progress");
        await Task.Delay(2000);
        Check(!pending.IsCompleted, "A cancelled request must not be answered.");
        Check(harness.Client.RawLines.Skip(lines).All(line => !line.Contains("\"slow-1\"", StringComparison.Ordinal)), "No line may mention the cancelled request id after its cancellation.");
        Check(harness.Client.Notifications.Count(n => n.GetProperty("method").GetString() == "notifications/progress") == progressBefore, "No progress notification may follow the cancellation.");
        var progress = harness.Client.Notifications.Where(n => n.GetProperty("method").GetString() == "notifications/progress").Select(n => n.GetProperty("params")).ToList();
        Check(progress.Count >= 1 && progress.All(p => p.GetProperty("progressToken").GetString() == "slow" && p.GetProperty("total").GetDouble() == 30), "Progress notifications must carry the token and total.");
        await harness.Client.NotifyAsync("notifications/cancelled", new { requestId = "never-existed" });
        await harness.Client.NotifyAsync("notifications/cancelled", new { requestId = 1.5 });
        Check(Result(await harness.Client.RequestAsync("ping")).ValueKind == JsonValueKind.Object, "The connection must stay usable after a cancellation.");
        Check(await harness.LaunchedCountAsync() == 0, "The cancelled launch must leave no app behind.");
        Check(harness.Client.UnexpectedResponses.Count == 0 && harness.Client.UnparseableLines == 0, "Nothing unexpected may have been written.");
    }

    private static async Task CancellationOrdering()
    {
        await using var harness = new Harness("cancel-order");
        await harness.InitializeAsync();
        for (var index = 0; index < 200; index++)
        {
            var id = "order-" + index;
            var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = "launch_app", ["arguments"] = JsonRpc.ToNode(IdleLaunch(30)) } };
            var cancel = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled", ["params"] = new JsonObject { ["requestId"] = id } };
            // One write carries the request and its cancellation: the cancellation must find the request, whatever the thread pool does.
            // A cancellation that ran first would be ignored, and the launch would go on for 30 seconds and be answered.
            await harness.Client.SendBytesAsync(Encoding.UTF8.GetBytes(request.ToJsonString() + "\n" + cancel.ToJsonString() + "\n"));
        }
        Check(Result(await harness.Client.RequestAsync("ping")).ValueKind == JsonValueKind.Object, "The server must answer after the burst.");
        await UntilAsync(async () => await harness.LaunchedCountAsync() == 0, "A launch outlived its cancellation.", 15000);
        await Task.Delay(500);
        Check(harness.Client.UnexpectedResponses.Count == 0, $"{harness.Client.UnexpectedResponses.Count} cancelled requests were answered: " + string.Join(" | ", harness.Client.UnexpectedResponses.Take(3).Select(r => r.GetRawText())));
        Check(harness.Client.Notifications.Count == 0, "Nothing may have been written for the cancelled requests.");
        // The same for a request that follows initialize in one write: it sees the negotiated version.
        await using var second = new Harness("initialize-order");
        var initialize = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "i", ["method"] = "initialize", ["params"] = new JsonObject { ["protocolVersion"] = "2025-03-26", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "unit", ["version"] = "1" } } };
        await second.Client.SendBytesAsync(Encoding.UTF8.GetBytes(initialize.ToJsonString() + "\n{\"jsonrpc\":\"2.0\",\"id\":\"l\",\"method\":\"subscriptions/listen\"}\n"));
        await UntilAsync(() => second.Client.UnexpectedResponses.Count == 2, "Both requests must be answered.");
        var listen = second.Client.UnexpectedResponses.Single(r => r.GetProperty("id").GetString() == "l");
        Check(ErrorCode(listen) == JsonRpc.MethodNotFound, "A request written right after initialize belongs to the handshake connection: " + listen.GetRawText());
    }

    private static async Task ProgressRules()
    {
        await using var harness = new Harness("progress");
        await harness.InitializeAsync();
        var quiet = await harness.Client.CallToolAsync("launch_app", IdleLaunch(2), timeout: TimeSpan.FromSeconds(30));
        Check(quiet.IsError && quiet.Text.Contains("no visible titled window", StringComparison.Ordinal), "A windowless program must fail launch_app after the wait: " + quiet.Text);
        Check(harness.Client.Notifications.Count == 0, "A call without a progressToken must produce no notification at all.");

        // A reader that needs 500 ms per notification: reporting returns at once, and every notification is written before the response.
        var written = new List<(string Kind, TimeSpan At)>();
        var clock = Stopwatch.StartNew();
        var connection = new McpConnection("slow-reader", "unit");
        async Task SlowNotify(JsonObject notification)
        {
            await Task.Delay(500);
            lock (written) written.Add(("progress " + notification["params"]!["progress"], clock.Elapsed));
        }
        var call = JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = 7, method = "tools/call", @params = new { name = "launch_app", arguments = IdleLaunch(2), _meta = new { progressToken = "p" } } });
        var response = await harness.Server.HandleAsync(call, connection, SlowNotify, CancellationToken.None);
        var finished = clock.Elapsed;
        Check(response is not null && response["result"]!["isError"]!.GetValue<bool>(), "The call must end with its tool error.");
        lock (written)
        {
            Check(written.Count >= 1, "Progress was expected.");
            Check(written.All(entry => entry.At <= finished), "Every notification must be written before the response is returned.");
        }
        var slow = NewContext(new McpConnection("slow-values", "unit"), "slow", async _ => await Task.Delay(500));
        var reporting = Stopwatch.StartNew();
        for (var report = 1; report <= 20; report++) slow.Progress(report, 20, "step " + report);
        Check(reporting.ElapsedMilliseconds < 250, $"Reporting progress must not wait for the reader; 20 reports took {reporting.ElapsedMilliseconds} ms.");
        await slow.FinishProgressAsync();

        // Values rise strictly and never pass the total; at most one notification per interval, the newest value.
        var values = new List<double>();
        var context = NewContext(new McpConnection("values", "unit"), "v", notification => { lock (values) values.Add(notification["params"]!["progress"]!.GetValue<double>()); return Task.CompletedTask; });
        context.Progress(1, 5, "one");
        await Task.Delay(150);
        context.Progress(1, 5, "one again");
        await Task.Delay(150);
        for (var burst = 0; burst < 50; burst++) context.Progress(2 + burst * 0.01, 5, "burst");
        await Task.Delay(150);
        context.Progress(9, 5, "beyond the total");
        context.Progress(5, 5, "still the total");
        await context.FinishProgressAsync();
        context.Progress(5, 5, "after the end");
        lock (values)
        {
            Check(values.Zip(values.Skip(1)).All(pair => pair.Second > pair.First), "Progress must increase strictly: " + string.Join(", ", values));
            Check(values.All(value => value <= 5) && values[^1] == 5, "Progress must never pass the total: " + string.Join(", ", values));
            Check(values.Count is >= 3 and <= 6, $"A burst of 50 reports must be coalesced; {values.Count} notifications were written: " + string.Join(", ", values));
        }
    }
    private static McpRequestContext NewContext(McpConnection connection, string id, Func<JsonObject, Task> notify)
    {
        Check(connection.TryBegin("s:" + id, CancellationToken.None, out var inflight), "The request could not be registered.");
        return new McpRequestContext(JsonSerializer.SerializeToElement(id), "tools/call", null, connection, McpServer.LatestHandshakeVersion, false, JsonSerializer.SerializeToElement("token"), notify, inflight);
    }

    private static async Task Batches()
    {
        await using var harness = new Harness("batch");
        await harness.InitializeAsync();
        var lines = harness.Client.RawLines.Count;
        await harness.Client.SendAsync("[{\"jsonrpc\":\"2.0\",\"id\":\"b1\",\"method\":\"ping\"},{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"},{\"jsonrpc\":\"2.0\",\"id\":\"b2\",\"method\":\"tools/list\"}]");
        await UntilAsync(() => harness.Client.Batches.Count == 1, "The batch was not answered with an array.");
        var answer = harness.Client.Batches[0];
        Check(answer.GetArrayLength() == 2 && answer[0].GetProperty("id").GetString() == "b1" && answer[1].GetProperty("id").GetString() == "b2", "A batch is answered with one array, in request order: " + answer.GetRawText()[..Math.Min(200, answer.GetRawText().Length)]);
        Check(answer[1].GetProperty("result").GetProperty("tools").GetArrayLength() == ExpectedTools.Length, "The batched tools/list carries the tools.");
        Check(harness.Client.RawLines.Count == lines + 1 && harness.Client.RawLines[lines].StartsWith('['), "Exactly one line, an array, answers the batch.");
        await harness.Client.SendAsync("[{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"},{\"jsonrpc\":\"2.0\",\"method\":\"notifications/unknown\"}]");
        Check(Result(await harness.Client.RequestAsync("ping")).ValueKind == JsonValueKind.Object, "The connection stays usable.");
        Check(harness.Client.Batches.Count == 1 && harness.Client.RawLines.Count == lines + 2, "A batch of notifications produces no line.");
        var initialize = "{\"jsonrpc\":\"2.0\",\"id\":\"i1\",\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{}}}";
        var stateless = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "s1", ["method"] = "tools/list", ["params"] = new JsonObject { ["_meta"] = Meta() } }.ToJsonString();
        await harness.Client.SendAsync("[{\"jsonrpc\":\"2.0\",\"id\":\"ok\",\"method\":\"ping\"},{\"id\":\"bad\",\"method\":\"ping\"},42," + initialize + "," + stateless + "]");
        await UntilAsync(() => harness.Client.Batches.Count == 2, "The mixed batch was not answered with an array.");
        var mixed = harness.Client.Batches[1].EnumerateArray().ToList();
        Check(mixed.Count == 5, "Every entry of the batch, valid or not, has its answer in the array: " + harness.Client.Batches[1].GetRawText());
        Check(mixed[0].TryGetProperty("result", out _) && ErrorCode(mixed[1]) == JsonRpc.InvalidRequest && mixed[1].GetProperty("id").GetString() == "bad" && ErrorCode(mixed[2]) == JsonRpc.InvalidRequest && mixed[2].GetProperty("id").ValueKind == JsonValueKind.Null,
            "A valid entry is answered and an invalid one gets its error in the same array.");
        Check(ErrorCode(mixed[3]) == JsonRpc.InvalidRequest && ErrorText(mixed[3]).Contains("initialize", StringComparison.Ordinal), "initialize inside a batch is refused with -32600.");
        Check(ErrorCode(mixed[4]) == JsonRpc.InvalidRequest && ErrorText(mixed[4]).Contains("2026-07-28", StringComparison.Ordinal), "An entry of the stateless revision inside a batch is refused with -32600.");
        Check(harness.Client.UnparseableLines == 0, "Every line is JSON.");
    }

    private static async Task Framing()
    {
        await using var harness = new Harness("framing");
        byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
        byte[] bom = [0xEF, 0xBB, 0xBF];
        var initialize = "{\"jsonrpc\":\"2.0\",\"id\":\"init\",\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"unit\",\"version\":\"1\"}}}";
        async Task<JsonElement> Answer(string id)
        {
            await UntilAsync(() => harness.Client.UnexpectedResponses.Any(r => r.GetProperty("id").ValueKind == JsonValueKind.String && r.GetProperty("id").GetString() == id), $"Request {id} was not answered.");
            return harness.Client.UnexpectedResponses.Single(r => r.GetProperty("id").ValueKind == JsonValueKind.String && r.GetProperty("id").GetString() == id);
        }
        // A byte order mark before the first message, and CRLF line ends.
        await harness.Client.SendBytesAsync([.. bom, .. Bytes(initialize + "\r\n")]);
        Check((await Answer("init")).GetProperty("result").GetProperty("protocolVersion").GetString() == "2025-06-18", "A first message preceded by a byte order mark must be answered normally.");
        await harness.Client.SendBytesAsync(Bytes("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\r\n{\"jsonrpc\":\"2.0\",\"id\":\"crlf\",\"method\":\"ping\"}\r\n"));
        Check((await Answer("crlf")).TryGetProperty("result", out _), "Messages that end in CRLF are accepted.");
        await harness.Client.SendBytesAsync([.. bom, .. Bytes("{\"jsonrpc\":\"2.0\",\"id\":\"bom2\",\"method\":\"ping\"}\n")]);
        Check((await Answer("bom2")).TryGetProperty("result", out _), "A byte order mark is tolerated on any line.");
        // Blank lines and lines of spaces are ignored.
        var nullIds = harness.Client.UnexpectedResponses.Count(r => r.GetProperty("id").ValueKind == JsonValueKind.Null);
        await harness.Client.SendBytesAsync(Bytes("\n\r\n   \n\t\r\n{\"jsonrpc\":\"2.0\",\"id\":\"blank\",\"method\":\"ping\"}\n"));
        Check((await Answer("blank")).TryGetProperty("result", out _), "A message after blank lines is answered.");
        Check(harness.Client.UnexpectedResponses.Count(r => r.GetProperty("id").ValueKind == JsonValueKind.Null) == nullIds, "Blank lines must not be answered.");
        // A message split across several writes.
        var split = Bytes("{\"jsonrpc\":\"2.0\",\"id\":\"split\",\"method\":\"ping\"}\n");
        foreach (var part in split.Chunk(7)) { await harness.Client.SendBytesAsync(part); await Task.Delay(5); }
        Check((await Answer("split")).TryGetProperty("result", out _), "A message that arrives in pieces is answered once it is complete.");
        // Invalid UTF-8 is a parse error with a null id; the connection stays usable.
        await harness.Client.SendBytesAsync([.. Bytes("{\"jsonrpc\":\"2.0\",\"id\":\"utf8\",\"method\":\"ping\",\"params\":{\"x\":\""), 0xC3, 0x28, .. Bytes("\"}}\n")]);
        await UntilAsync(() => harness.Client.UnexpectedResponses.Count(r => r.GetProperty("id").ValueKind == JsonValueKind.Null) == nullIds + 1, "Invalid UTF-8 was not answered.");
        var invalid = harness.Client.UnexpectedResponses.Last(r => r.GetProperty("id").ValueKind == JsonValueKind.Null);
        Check(ErrorCode(invalid) == JsonRpc.ParseError && ErrorText(invalid).Contains("UTF-8", StringComparison.Ordinal), "Invalid UTF-8 must be -32700: " + invalid.GetRawText());
        await harness.Client.SendAsync("{\"jsonrpc\":\"2.0\",\"id\":\"after-utf8\",\"method\":\"ping\"}");
        Check((await Answer("after-utf8")).TryGetProperty("result", out _), "The connection stays usable after invalid UTF-8.");
        // A line above 32 MiB: the error comes when the limit is crossed, not when the newline finally arrives.
        var block = new byte[1024 * 1024];
        Array.Fill(block, (byte)'a');
        await harness.Client.SendBytesAsync(Bytes("{\"jsonrpc\":\"2.0\",\"id\":\"huge\",\"method\":\"ping\",\"params\":{\"pad\":\""));
        for (var megabyte = 0; megabyte < 32; megabyte++) await harness.Client.SendBytesAsync(block);
        var watch = Stopwatch.StartNew();
        await harness.Client.SendBytesAsync([(byte)'a']);
        await UntilAsync(() => harness.Client.UnexpectedResponses.Count(r => r.GetProperty("id").ValueKind == JsonValueKind.Null) == nullIds + 2, "The oversized line was not refused while it was still being sent.", 5000);
        Check(watch.Elapsed < TimeSpan.FromSeconds(3), $"The refusal of an oversized line took {watch.Elapsed.TotalSeconds:F1} s.");
        var tooLong = harness.Client.UnexpectedResponses.Last(r => r.GetProperty("id").ValueKind == JsonValueKind.Null);
        Check(ErrorCode(tooLong) == JsonRpc.ParseError && ErrorText(tooLong).Contains("32 MiB", StringComparison.Ordinal), "An oversized line must be -32700 and name the limit: " + tooLong.GetRawText());
        await harness.Client.SendBytesAsync(Bytes("the rest of the oversized line\"}}\n{\"jsonrpc\":\"2.0\",\"id\":\"after-huge\",\"method\":\"ping\"}\n"));
        Check((await Answer("after-huge")).TryGetProperty("result", out _), "The next line after an oversized one is handled normally.");
        await Task.Delay(200);
        Check(harness.Client.UnexpectedResponses.Count(r => r.GetProperty("id").ValueKind == JsonValueKind.Null) == nullIds + 2, "The oversized line must be reported once.");
        Check(harness.Client.UnexpectedResponses.All(r => r.GetProperty("id").ValueKind != JsonValueKind.String || r.GetProperty("id").GetString() != "huge"), "The oversized request itself must not be answered.");
        Check(harness.Client.UnparseableLines == 0, "Every answer is one JSON line.");
    }

    // ═══════════════ The stateless revision ═══════════════
    private static async Task Stateless()
    {
        await using var harness = new Harness("stateless");
        var discover = Result(await harness.Client.RequestAsync("server/discover", new JsonObject { ["_meta"] = Meta() }));
        Check(discover.GetProperty("resultType").GetString() == "complete", "server/discover must carry resultType complete.");
        Check(discover.GetProperty("supportedVersions").EnumerateArray().Select(v => v.GetString()).SequenceEqual(McpServer.SupportedVersions), "server/discover must list every supported version.");
        Check(discover.GetProperty("_meta").GetProperty(McpServer.MetaServerInfo).GetProperty("name").GetString() == "testy", "server/discover must identify the server in _meta.");
        Check(discover.GetProperty("capabilities").TryGetProperty("tools", out _) && !discover.GetProperty("capabilities").TryGetProperty("logging", out _), "Stateless capabilities omit the removed logging feature.");
        Check(discover.GetProperty("ttlMs").GetInt32() > 0 && discover.GetProperty("cacheScope").GetString() == "public", "server/discover is cacheable.");
        var tools = Result(await harness.Client.RequestAsync("tools/list", new JsonObject { ["_meta"] = Meta() }));
        Check(tools.GetProperty("resultType").GetString() == "complete" && tools.GetProperty("ttlMs").GetInt32() > 0 && tools.GetProperty("cacheScope").GetString() == "public" && tools.GetProperty("tools").GetArrayLength() == ExpectedTools.Length, "Stateless tools/list must carry resultType and cache hints.");
        Check(tools.GetProperty("_meta").GetProperty(McpServer.MetaServerInfo).GetProperty("version").GetString() == McpServer.Version, "Stateless results identify the server.");
        var call = new McpToolCall(await harness.Client.RequestAsync("tools/call", new JsonObject { ["name"] = "list_tests", ["arguments"] = new JsonObject(), ["_meta"] = Meta() })).RequireOk("stateless tools/call");
        Check(call.Result!.Value.GetProperty("resultType").GetString() == "complete" && call.Structured.GetProperty("count").GetInt32() == 0, "Stateless tool results carry resultType.");
        var legacyShaped = Result(await harness.Client.RequestAsync("tools/list", new JsonObject { ["_meta"] = Meta("2025-06-18") }));
        Check(!legacyShaped.TryGetProperty("resultType", out _), "A handshake version declared per request is answered in its own plain shape.");
        var unsupported = await harness.Client.RequestAsync("tools/list", new JsonObject { ["_meta"] = Meta("1999-01-01") });
        Check(ErrorCode(unsupported) == JsonRpc.UnsupportedProtocolVersion, "Unknown per-request versions must be -32022.");
        var data = unsupported.GetProperty("error").GetProperty("data");
        Check(data.GetProperty("requested").GetString() == "1999-01-01" && data.GetProperty("supported").EnumerateArray().Select(v => v.GetString()).SequenceEqual(McpServer.SupportedVersions), "-32022 must name the requested and supported versions.");
        Check(ErrorCode(await harness.Client.RequestAsync("resources/read", new JsonObject { ["uri"] = "testy://runs/none", ["_meta"] = Meta() })) == JsonRpc.InvalidParams, "Stateless resource-not-found is -32602.");
        Check(ErrorCode(await harness.Client.RequestAsync("no/such", new JsonObject { ["_meta"] = Meta() })) == JsonRpc.MethodNotFound, "Unknown stateless methods are -32601.");
        Check(ErrorCode(await harness.Client.RequestAsync("tools/call", new JsonObject { ["name"] = "nope", ["arguments"] = new JsonObject(), ["_meta"] = Meta() })) == JsonRpc.InvalidParams, "An unknown tool is -32602 in the stateless revision too.");
        var ping = Result(await harness.Client.RequestAsync("ping"));
        Check(!ping.TryGetProperty("resultType", out _), "Requests without _meta keep the handshake shape.");
        Check(McpResources.Instructions.Length < 3000, $"Instructions stay concise ({McpResources.Instructions.Length} characters).");
    }

    private static async Task StatelessEnvelope()
    {
        await using var harness = new Harness("stateless-envelope");
        async Task Invalid(string what, string method, JsonObject? parameters, string mentions)
        {
            var response = await harness.Client.RequestAsync(method, parameters);
            Check(response.TryGetProperty("error", out _) && ErrorCode(response) == JsonRpc.InvalidParams && ErrorText(response).Contains(mentions, StringComparison.Ordinal), $"{what} must be -32602 and name {mentions}: " + response.GetRawText());
        }
        JsonObject Without(string key) { var meta = Meta(); meta.Remove(key); return new JsonObject { ["_meta"] = meta }; }
        JsonObject With(string key, JsonNode? value) { var meta = Meta(); meta[key] = value; return new JsonObject { ["_meta"] = meta }; }
        await Invalid("A request without clientCapabilities", "tools/list", Without(McpServer.MetaClientCapabilities), "clientCapabilities");
        await Invalid("clientCapabilities that is not an object", "tools/list", With(McpServer.MetaClientCapabilities, "none"), "clientCapabilities");
        await Invalid("A protocolVersion that is a number", "tools/list", With(McpServer.MetaProtocolVersion, 20260728), "protocolVersion");
        await Invalid("A _meta that is not an object", "tools/list", new JsonObject { ["_meta"] = "2026-07-28" }, "_meta");
        await Invalid("server/discover without _meta", "server/discover", null, "_meta");
        await Invalid("server/discover with an empty _meta", "server/discover", new JsonObject { ["_meta"] = new JsonObject() }, "protocolVersion");
        await Invalid("A clientInfo without a version", "tools/list", With(McpServer.MetaClientInfo, new JsonObject { ["name"] = "unit" }), "clientInfo");
        await Invalid("A clientInfo that is a string", "tools/list", With(McpServer.MetaClientInfo, "unit"), "clientInfo");
        await Invalid("An unknown logLevel", "tools/list", With(McpServer.MetaLogLevel, "loud"), "logLevel");
        Check(Result(await harness.Client.RequestAsync("tools/list", With(McpServer.MetaLogLevel, "warning"))).GetProperty("tools").GetArrayLength() == ExpectedTools.Length, "A valid logLevel is accepted.");
        Check(Result(await harness.Client.RequestAsync("tools/list", Without(McpServer.MetaClientInfo))).GetProperty("tools").GetArrayLength() == ExpectedTools.Length, "clientInfo is optional.");
        // Methods the stateless revision removed are not found there; the handshake revisions keep them.
        foreach (var removed in new[] { "ping", "logging/setLevel", "initialize", "resources/subscribe", "resources/unsubscribe" })
        {
            var response = await harness.Client.RequestAsync(removed, new JsonObject { ["level"] = "info", ["_meta"] = Meta() });
            Check(ErrorCode(response) == JsonRpc.MethodNotFound, $"{removed} is not part of revision 2026-07-28: " + response.GetRawText());
            if (removed == "initialize") Check(McpServer.SupportedVersions.All(version => ErrorText(response).Contains(version, StringComparison.Ordinal)), "The refusal of initialize names the supported versions.");
        }
        Check(Result(await harness.Client.RequestAsync("ping")).ValueKind == JsonValueKind.Object, "A handshake-era ping still returns an empty object.");
        Check(Result(await harness.Client.RequestAsync("logging/setLevel", new { level = "info" })).ValueKind == JsonValueKind.Object, "A handshake-era logging/setLevel is still accepted.");
    }

    private static async Task Subscriptions()
    {
        var harness = new Harness("subscriptions");
        JsonObject Listen(JsonNode? filter) { var parameters = new JsonObject { ["_meta"] = Meta() }; if (filter is not null) parameters["notifications"] = filter; return parameters; }
        var invalidFilters = new (JsonNode? Filter, string Mentions)[]
        {
            (null, "notifications"), (JsonValue.Create("all"), "notifications"), (new JsonObject { ["toolsListChanged"] = "yes" }, "toolsListChanged"),
            (new JsonObject { ["resourceSubscriptions"] = "testy://workspace" }, "resourceSubscriptions"), (new JsonObject { ["resourceSubscriptions"] = new JsonArray(1) }, "resourceSubscriptions")
        };
        foreach (var (filter, mentions) in invalidFilters)
        {
            var refused = await harness.Client.RequestAsync("subscriptions/listen", Listen(filter));
            Check(ErrorCode(refused) == JsonRpc.InvalidParams && ErrorText(refused).Contains(mentions, StringComparison.Ordinal), "An invalid filter must be -32602: " + refused.GetRawText());
        }
        Check(harness.Client.Notifications.Count == 0 && harness.Server.ListenStreams == 0, "A refused subscription acknowledges nothing.");

        // Acknowledged first, with the request id as the subscription id (same JSON type); nothing is granted.
        var lines = harness.Client.RawLines.Count;
        var numeric = harness.Client.StartAsync("subscriptions/listen", Listen(new JsonObject { ["toolsListChanged"] = true, ["resourceSubscriptions"] = new JsonArray("testy://workspace") }), JsonValue.Create(41));
        await UntilAsync(() => harness.Client.RawLines.Count > lines, "The subscription was not acknowledged.");
        var first = JsonDocument.Parse(harness.Client.RawLines[lines]).RootElement;
        Check(first.GetProperty("method").GetString() == "notifications/subscriptions/acknowledged", "The acknowledgement must be the first message: " + first.GetRawText());
        var acknowledged = first.GetProperty("params");
        Check(acknowledged.GetProperty("_meta").GetProperty(McpServer.MetaSubscriptionId).ValueKind == JsonValueKind.Number && acknowledged.GetProperty("_meta").GetProperty(McpServer.MetaSubscriptionId).GetInt32() == 41, "The acknowledgement carries the request id as the subscription id.");
        Check(acknowledged.GetProperty("notifications").ValueKind == JsonValueKind.Object && !acknowledged.GetProperty("notifications").EnumerateObject().Any(), "No notification type is granted: this server offers none.");
        Check(harness.Server.ListenStreams == 1, "The stream is open.");
        Check(Result(await harness.Client.RequestAsync("tools/list", new JsonObject { ["_meta"] = Meta() })).GetProperty("tools").GetArrayLength() == ExpectedTools.Length, "Other requests are served while the stream is open.");
        // Cancelled by the client: silence.
        lines = harness.Client.RawLines.Count;
        await harness.Client.NotifyAsync("notifications/cancelled", new { requestId = 41 });
        await UntilAsync(() => harness.Server.ListenStreams == 0, "The cancelled stream stayed open.");
        await Task.Delay(500);
        Check(!numeric.IsCompleted && harness.Client.RawLines.Count == lines, "Nothing may be written for a subscription its client cancelled.");

        // Closed by the server: notifications/cancelled for the request, then the completion result.
        var text = harness.Client.StartAsync("subscriptions/listen", Listen(new JsonObject()), JsonValue.Create("stream-a"));
        // The stream counts as open a moment before its acknowledgement is written; wait for the acknowledgement so it is not taken for a closing message.
        await UntilAsync(() => harness.Server.ListenStreams == 1 && harness.Client.RawLines.Any(line => line.Contains("notifications/subscriptions/acknowledged", StringComparison.Ordinal) && line.Contains("\"stream-a\"", StringComparison.Ordinal)),
            "The second stream did not open.");
        lines = harness.Client.RawLines.Count;
        harness.Client.CloseInput();
        var closure = await text.WaitAsync(TimeSpan.FromSeconds(10));
        await harness.Client.Reader.WaitAsync(TimeSpan.FromSeconds(5));
        var closing = harness.Client.RawLines.Skip(lines).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList();
        Check(closing.Count == 2 && closing[0].GetProperty("method").GetString() == "notifications/cancelled" && closing[0].GetProperty("params").GetProperty("requestId").GetString() == "stream-a",
            "A stream the server closes is cancelled by notification first: " + string.Join(" | ", harness.Client.RawLines.Skip(lines)));
        var result = closure.GetProperty("result");
        Check(result.GetProperty("resultType").GetString() == "complete" && result.GetProperty("_meta").GetProperty(McpServer.MetaSubscriptionId).GetString() == "stream-a"
            && result.GetProperty("_meta").GetProperty(McpServer.MetaServerInfo).GetProperty("name").GetString() == "testy", "The completion result names the subscription and the server: " + closure.GetRawText());
        Check(await harness.Transport.WaitAsync(TimeSpan.FromSeconds(10)) == 0 && harness.Server.ListenStreams == 0, "The transport ends cleanly.");
        await harness.Service.DisposeAsync();

        // No more than sixteen streams at once.
        await using var crowded = new Harness("subscriptions-limit");
        for (var index = 0; index < McpServer.MaximumListenStreams; index++) _ = crowded.Client.StartAsync("subscriptions/listen", Listen(new JsonObject()), JsonValue.Create("l" + index));
        await UntilAsync(() => crowded.Server.ListenStreams == McpServer.MaximumListenStreams, "The streams did not open.");
        var busy = await crowded.Client.RequestAsync("subscriptions/listen", Listen(new JsonObject()));
        Check(ErrorCode(busy) == JsonRpc.ServerBusy, "One stream too many is refused with the server's busy code: " + busy.GetRawText());
    }

    // ═══════════════ Runs, with a stand-in driver and execution ═══════════════
    private sealed class StandInDriver(int pid) : ITargetDriver
    {
        public TargetInfo? Target { get; } = new() { ProcessId = pid, Title = "Stand-in app", ProcessName = "stand-in" };
        public Task<IReadOnlyList<TargetInfo>> GetTargetsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TargetInfo>>([Target!]);
        public Task AttachAsync(int processId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<UiSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(new UiSnapshot { Target = Target! });
        public Task ExecuteAsync(TestStep step, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> CaptureAsync(string filePath, CancellationToken cancellationToken = default) => throw new InvalidOperationException("The stand-in app has no window to capture.");
        public void Dispose() { }
    }
    /// <summary>A run that reports like Testy's runner does, takes <paramref name="perStep"/> per step, honours cancellation, and can stop with an error after its first report.</summary>
    private static Func<TestCase, IProgress<RunProgress>, CancellationToken, Task<RunResult>> StandInRun(TimeSpan perStep, string? failAfterStart = null) => async (test, progress, token) =>
    {
        var run = new RunResult { TestId = test.Id, TestName = test.Name, Status = RunStatus.Running, Target = new TargetInfo { ProcessId = 4242, Title = "Stand-in app", ProcessName = "stand-in" } };
        void Report(StepResult? step, string message) => progress.Report(new RunProgress { Run = TestyJson.Clone(run), Step = step is null ? null : TestyJson.Clone(step), Message = message });
        Report(null, "Run started.");
        if (failAfterStart is not null) throw new InvalidOperationException(failAfterStart);
        for (var index = 0; index < test.Steps.Count; index++)
        {
            var step = new StepResult { Index = index, Step = TestyJson.Clone(test.Steps[index]), Status = RunStatus.Running };
            run.Steps.Add(step);
            Report(step, $"Step {index + 1} is running.");
            try { await Task.Delay(perStep, token); }
            catch (OperationCanceledException)
            {
                step.Status = RunStatus.Cancelled;
                for (var rest = index + 1; rest < test.Steps.Count; rest++) run.Steps.Add(new StepResult { Index = rest, Step = TestyJson.Clone(test.Steps[rest]), Status = RunStatus.Skipped });
                run.Status = RunStatus.Cancelled; run.FinishedAt = DateTimeOffset.UtcNow; run.Summary = $"Cancelled at step {index + 1}.";
                Report(null, run.Summary);
                return run;
            }
            step.Status = RunStatus.Passed; step.Message = "Done.";
            Report(step, $"Step {index + 1} passed.");
        }
        run.Status = RunStatus.Passed; run.FinishedAt = DateTimeOffset.UtcNow; run.Summary = $"{test.Steps.Count} of {test.Steps.Count} steps passed.";
        Report(null, run.Summary);
        return run;
    };
    private static bool DesktopAvailable => InteractiveDesktop.Observe().Available;
    private static async Task<(Harness Harness, string TestId)> RunHarnessAsync(string name, TimeSpan perStep, string? failAfterStart = null)
    {
        var harness = new Harness(name);
        harness.Service.DriverOverride = pid => new StandInDriver(pid);
        harness.Service.ExecutionOverride = StandInRun(perStep, failAfterStart);
        await harness.InitializeAsync();
        var created = (await harness.Client.CallToolAsync("create_test", new { name = "Stand-in run", intent = "Three steps against the stand-in app.", steps = CustomerSteps("Ada") })).RequireOk("create_test").Structured;
        return (harness, created.GetProperty("testId").GetString()!);
    }

    private static async Task BackgroundRun()
    {
        if (!DesktopAvailable) return; // runs need an unlocked interactive session; the desktop checks cover them on such a session
        var (harness, testId) = await RunHarnessAsync("background-run", TimeSpan.FromSeconds(5));
        await using var _ = harness;
        var schema = Result(await harness.Client.RequestAsync("tools/list")).GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "run_test").GetProperty("outputSchema");
        var watch = Stopwatch.StartNew();
        var started = (await harness.Client.CallToolAsync("run_test", new { testId, pid = 4242, wait = false })).RequireOk("run_test wait false");
        Check(watch.Elapsed < TimeSpan.FromSeconds(3), $"wait false must answer at once; it took {watch.Elapsed.TotalSeconds:F1} s.");
        var runId = started.Structured.GetProperty("runId").GetString()!;
        Check(started.Structured.GetProperty("running").GetBoolean() && started.Structured.GetProperty("status").GetString() == "running" && started.Structured.GetProperty("message").GetString()!.Contains("cancel_run", StringComparison.Ordinal),
            "wait false returns the run id with running true and says how to go on: " + started.Text);
        Check(McpSchemaValidator.FirstViolation(schema, started.Structured, "run_test", declaredTopLevelOnly: true) is null, "The wait false result must satisfy run_test's output schema: " + McpSchemaValidator.FirstViolation(schema, started.Structured, "run_test", declaredTopLevelOnly: true));
        var polled = (await harness.Client.CallToolAsync("get_run", new { runId })).RequireOk("get_run").Structured;
        Check(polled.GetProperty("running").GetBoolean() && polled.GetProperty("testId").GetString() == testId, "get_run reports the run while it is in progress.");
        var listed = (await harness.Client.CallToolAsync("list_runs", new { testId })).RequireOk("list_runs").Structured.GetProperty("runs");
        Check(listed.GetArrayLength() == 1 && listed[0].GetProperty("runId").GetString() == runId && listed[0].GetProperty("running").GetBoolean(), "list_runs includes the run in progress.");
        var info = (await harness.Client.CallToolAsync("get_workspace_info")).RequireOk("get_workspace_info").Structured;
        Check(info.GetProperty("counts").GetProperty("activeRuns").GetInt32() == 1 && info.GetProperty("activeRuns")[0].GetProperty("runId").GetString() == runId, "get_workspace_info names the active run.");
        var resource = JsonDocument.Parse(Result(await harness.Client.RequestAsync("resources/read", new { uri = "testy://runs/" + runId })).GetProperty("contents")[0].GetProperty("text").GetString()!).RootElement;
        Check(resource.GetProperty("running").GetBoolean() && resource.GetProperty("runId").GetString() == runId, "The run resource shows an active run.");
        watch.Restart();
        var busy = await harness.Client.CallToolAsync("perform_step", new { pid = 4242, step = new { action = "click", selector = "id:ResetButton" } });
        Check(busy.IsError && busy.Text.Contains("Stand-in run", StringComparison.Ordinal) && busy.Text.Contains(runId, StringComparison.Ordinal) && busy.Text.Contains("cancel_run", StringComparison.Ordinal),
            "While a run holds the desktop, perform_step must say which test and run, and how to stop it: " + busy.Text);
        Check(watch.Elapsed < TimeSpan.FromSeconds(4), $"The busy answer took {watch.Elapsed.TotalSeconds:F1} s.");
        var secondRun = await harness.Client.CallToolAsync("run_test", new { testId, pid = 4242 });
        Check(secondRun.IsError && secondRun.Text.Contains(runId, StringComparison.Ordinal), "A second run is refused while the first holds the desktop.");
        var launch = await harness.Client.CallToolAsync("launch_app", new { exe = IdleFixture, waitForWindowSeconds = 1 });
        Check(launch.IsError && launch.Text.Contains(runId, StringComparison.Ordinal), "launch_app waits its turn like the input tools: " + launch.Text);
        var cancelled = (await harness.Client.CallToolAsync("cancel_run", new { runId })).RequireOk("cancel_run").Structured;
        Check(cancelled.GetProperty("status").GetString() == "cancelled" && !cancelled.GetProperty("running").GetBoolean() && cancelled.GetProperty("steps").GetArrayLength() == 3, "cancel_run returns the cancelled run: " + cancelled.GetRawText());
        Check(McpSchemaValidator.FirstViolation(schema, cancelled, "cancel_run", declaredTopLevelOnly: true) is null, "cancel_run's result must satisfy its output schema.");
        Check(File.Exists(Path.Combine(harness.Workspace, "runs", runId + ".json")), "The cancelled run is saved.");
        var saved = (await harness.Client.CallToolAsync("get_run", new { runId })).RequireOk("get_run").Structured;
        Check(saved.GetProperty("status").GetString() == "cancelled" && !saved.GetProperty("running").GetBoolean() && saved.GetProperty("steps")[2].GetProperty("status").GetString() == "skipped", "get_run reads the final status from the saved file.");
        Check(!(await harness.Client.CallToolAsync("list_runs")).RequireOk("list_runs").Structured.GetProperty("runs")[0].GetProperty("running").GetBoolean(), "list_runs shows running false afterwards.");
        var again = await harness.Client.CallToolAsync("cancel_run", new { runId });
        Check(again.IsError && again.Text.Contains("already finished with status cancelled", StringComparison.Ordinal), "cancel_run of a finished run says so: " + again.Text);
        harness.Service.ExecutionOverride = StandInRun(TimeSpan.FromMilliseconds(50));
        var free = (await harness.Client.CallToolAsync("run_test", new { testId, pid = 4242 })).RequireOk("run_test after cancel_run").Structured;
        Check(free.GetProperty("status").GetString() == "passed" && !free.GetProperty("running").GetBoolean(), "The desktop is free again after cancel_run.");
    }

    private static async Task RunWaiting()
    {
        if (!DesktopAvailable) return;
        var (harness, testId) = await RunHarnessAsync("run-waiting", TimeSpan.FromSeconds(1));
        await using var _ = harness;
        // The run takes three seconds; the call waits one and hands it to the background.
        var watch = Stopwatch.StartNew();
        var handed = (await harness.Client.CallToolAsync("run_test", new { testId, pid = 4242, waitSeconds = 1 })).RequireOk("run_test").Structured;
        Check(watch.Elapsed < TimeSpan.FromSeconds(2.5) && handed.GetProperty("running").GetBoolean() && handed.GetProperty("message").GetString()!.Contains("still going after 1 s", StringComparison.Ordinal),
            "A run longer than waitSeconds is returned as running: " + handed.GetRawText());
        var runId = handed.GetProperty("runId").GetString()!;
        await UntilAsync(async () => !(await harness.Client.CallToolAsync("get_run", new { runId })).RequireOk("get_run").Structured.GetProperty("running").GetBoolean(), "The background run never finished.", 10000);
        var done = (await harness.Client.CallToolAsync("get_run", new { runId })).RequireOk("get_run").Structured;
        Check(done.GetProperty("status").GetString() == "passed" && done.GetProperty("steps").EnumerateArray().All(s => s.GetProperty("status").GetString() == "passed"), "The handed-over run completes on its own.");

        // Cancelling the call while it waits cancels the run; the call gets no response and the run is found through list_runs.
        var lines = harness.Client.RawLines.Count;
        var waiting = harness.Client.StartToolAsync("run_test", new { testId, pid = 4242 }, JsonValue.Create("waiting-run"), progressToken: "w");
        var first = await harness.Client.WaitForNotificationAsync(n => n.GetProperty("method").GetString() == "notifications/progress" && n.GetProperty("params").GetProperty("progressToken").GetString() == "w", TimeSpan.FromSeconds(5));
        Check(first.GetProperty("params").GetProperty("message").GetString()!.StartsWith("[run ", StringComparison.Ordinal), "Progress names the run, so a client that cancels can find it.");
        await harness.Client.NotifyAsync("notifications/cancelled", new { requestId = "waiting-run", reason = "unit check" });
        await UntilAsync(async () => (await harness.Client.CallToolAsync("list_runs", new { testId })).RequireOk("list_runs").Structured.GetProperty("runs").EnumerateArray().Any(r => r.GetProperty("status").GetString() == "cancelled" && !r.GetProperty("running").GetBoolean()),
            "The cancelled run was not recorded.");
        var progressCount = harness.Client.Notifications.Count(n => n.GetProperty("method").GetString() == "notifications/progress");
        await Task.Delay(2000);
        Check(!waiting.IsCompleted, "A cancelled run_test must not be answered.");
        Check(harness.Client.RawLines.Skip(lines).All(line => !line.Contains("\"waiting-run\"", StringComparison.Ordinal)), "No line may carry the cancelled request id.");
        Check(harness.Client.Notifications.Count(n => n.GetProperty("method").GetString() == "notifications/progress") == progressCount, "No progress follows the cancellation.");
        var cancelledId = (await harness.Client.CallToolAsync("list_runs", new { testId })).RequireOk("list_runs").Structured.GetProperty("runs").EnumerateArray().First(r => r.GetProperty("status").GetString() == "cancelled").GetProperty("runId").GetString();
        Check(first.GetProperty("params").GetProperty("message").GetString()!.Contains(cancelledId!, StringComparison.Ordinal), "The run id in the progress message is the id of the recorded run.");
        var cancelled = (await harness.Client.CallToolAsync("get_run", new { runId = cancelledId })).RequireOk("get_run").Structured;
        Check(cancelled.GetProperty("steps").GetArrayLength() == 3 && cancelled.GetProperty("steps")[2].GetProperty("status").GetString() == "skipped", "The steps after the cancelled one are skipped.");
        Check(Result(await harness.Client.RequestAsync("ping")).ValueKind == JsonValueKind.Object, "The connection stays usable.");
    }

    private static async Task RunFailure()
    {
        if (!DesktopAvailable) return;
        var (harness, testId) = await RunHarnessAsync("run-failure", TimeSpan.FromMilliseconds(50), failAfterStart: "The provider stopped answering.");
        await using var _ = harness;
        var started = (await harness.Client.CallToolAsync("run_test", new { testId, pid = 4242, wait = false })).RequireOk("run_test wait false").Structured;
        var runId = started.GetProperty("runId").GetString()!;
        await UntilAsync(async () => { var poll = await harness.Client.CallToolAsync("get_run", new { runId }); return !poll.IsError && !poll.Structured.GetProperty("running").GetBoolean(); }, "A client polling get_run must find the run after it stopped with an error.");
        var failed = (await harness.Client.CallToolAsync("get_run", new { runId })).RequireOk("get_run").Structured;
        Check(failed.GetProperty("status").GetString() == "failed" && failed.GetProperty("summary").GetString()!.Contains("The provider stopped answering.", StringComparison.Ordinal) && failed.GetProperty("finishedAt").ValueKind == JsonValueKind.String,
            "The run is recorded as failed with the reason: " + failed.GetRawText());
        Check((await harness.Client.CallToolAsync("list_runs")).RequireOk("list_runs").Structured.GetProperty("runs").EnumerateArray().Any(r => r.GetProperty("runId").GetString() == runId && r.GetProperty("status").GetString() == "failed"), "list_runs lists the failed run.");
        var waited = (await harness.Client.CallToolAsync("run_test", new { testId, pid = 4242 })).RequireOk("run_test").Structured;
        Check(waited.GetProperty("status").GetString() == "failed" && !waited.GetProperty("running").GetBoolean(), "A waiting call returns the failed run.");
    }

    private static async Task RunProgressScale()
    {
        if (!DesktopAvailable) return;
        var (harness, testId) = await RunHarnessAsync("run-progress", TimeSpan.FromMilliseconds(150));
        await using var _ = harness;
        var run = (await harness.Client.CallToolAsync("run_test", new { testId, pid = 4242 }, progressToken: "scale")).RequireOk("run_test").Structured;
        var progress = harness.Client.Notifications.Where(n => n.GetProperty("method").GetString() == "notifications/progress").Select(n => n.GetProperty("params")).ToList();
        var values = progress.Select(p => p.GetProperty("progress").GetDouble()).ToList();
        Check(values.Count >= 3, $"Progress was expected for three steps; {values.Count} notifications arrived.");
        Check(progress.All(p => p.GetProperty("total").GetDouble() == 3), "The total stays the number of steps.");
        Check(values.Zip(values.Skip(1)).All(pair => pair.Second > pair.First), "Progress increases strictly: " + string.Join(", ", values));
        Check(values.All(value => value <= 3) && values[^1] == 3, "Progress ends at the total and never passes it: " + string.Join(", ", values));
        Check(progress.All(p => p.GetProperty("message").GetString()!.StartsWith($"[run {run.GetProperty("runId").GetString()}]", StringComparison.Ordinal)), "Every message names the run.");
        var lastNotification = harness.Client.RawLines.ToList().FindLastIndex(line => line.Contains("notifications/progress", StringComparison.Ordinal));
        var response = harness.Client.RawLines.ToList().FindLastIndex(line => line.Contains("\"structuredContent\"", StringComparison.Ordinal) && line.Contains(run.GetProperty("runId").GetString()!, StringComparison.Ordinal));
        Check(lastNotification < response, "No notification may follow the response.");
    }

    private static async Task RunFinishedBeforeCleanup()
    {
        if (!DesktopAvailable) return;
        var (harness, testId) = await RunHarnessAsync("run-finished", TimeSpan.FromMilliseconds(50));
        await using var owned = harness;
        // After the result is recorded the run still closes the app it started (here: a stand-in that takes five seconds).
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Service.AfterRunRecorded = async () => { await Task.Delay(5000); cleanup.TrySetResult(); };
        var runId = (await harness.Client.CallToolAsync("run_test", new { testId, pid = 4242, wait = false })).RequireOk("run_test wait false").Structured.GetProperty("runId").GetString()!;
        var watch = Stopwatch.StartNew();
        var waited = (await harness.Client.CallToolAsync("get_run", new { runId, waitSeconds = 10 })).RequireOk("get_run waitSeconds").Structured;
        Check(watch.Elapsed < TimeSpan.FromSeconds(2.5) && !cleanup.Task.IsCompleted, $"get_run waitSeconds must answer as soon as the result is recorded, not after the cleanup; it took {watch.Elapsed.TotalSeconds:F1} s.");
        Check(waited.GetProperty("status").GetString() == "passed" && !waited.GetProperty("running").GetBoolean() && !waited.TryGetProperty("message", out _), "A recorded final status is never reported as still running: " + waited.GetRawText());
        var listed = (await harness.Client.CallToolAsync("list_runs", new { testId })).RequireOk("list_runs").Structured.GetProperty("runs")[0];
        Check(listed.GetProperty("runId").GetString() == runId && !listed.GetProperty("running").GetBoolean(), "list_runs shows the finished run as not running while the app is closed.");
        var info = (await harness.Client.CallToolAsync("get_workspace_info")).RequireOk("get_workspace_info").Structured;
        Check(info.GetProperty("counts").GetProperty("activeRuns").GetInt32() == 0 && info.GetProperty("activeRuns").GetArrayLength() == 0, "A finished run is not an active run.");
        var resource = JsonDocument.Parse(Result(await harness.Client.RequestAsync("resources/read", new { uri = "testy://runs/" + runId })).GetProperty("contents")[0].GetProperty("text").GetString()!).RootElement;
        Check(!resource.GetProperty("running").GetBoolean(), "The run resource agrees.");
        var cancel = await harness.Client.CallToolAsync("cancel_run", new { runId });
        Check(cancel.IsError && cancel.Text.Contains("already finished with status passed", StringComparison.Ordinal), "cancel_run of a run that finished (its app still closing) says so and changes nothing: " + cancel.Text);
        var busy = await harness.Client.CallToolAsync("perform_step", new { pid = 4242, step = new { action = "click", selector = "id:ResetButton" } });
        Check(busy.IsError && busy.Text.Contains("closing the app", StringComparison.Ordinal) && busy.Text.Contains(runId, StringComparison.Ordinal), "While the finished run still holds the desktop, a step waits with a message that says why: " + busy.Text);
        await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(10));
        harness.Service.AfterRunRecorded = null;
        await UntilAsync(async () => !(await harness.Client.CallToolAsync("run_test", new { testId, pid = 4242, wait = false })).IsError, "The desktop was not released after the cleanup.", 10000);
    }

    private static async Task StoredPaths()
    {
        // Pure checks: only full local drive paths count as local.
        foreach (var local in new[] { @"C:\Apps\Desk.exe", "D:/Tools/x.exe", @"C:\" }) Check(ExecutableRules.IsLocalDrivePath(local), local + " is a local drive path.");
        foreach (var other in new[] { @"\\host\share\app.exe", "//host/share/app.exe", @"\\?\C:\app.exe", @"\\.\pipe\x", "app.exe", @"Apps\app.exe", "C:app.exe", @"C:\a:b.exe", "", null })
            Check(!ExecutableRules.IsLocalDrivePath(other), (other ?? "null") + " is not a local drive path.");
        // Discovery skips a stored network path without opening it (an unreachable host would take seconds to fail).
        var watch = Stopwatch.StartNew();
        var found = Testy.Windows.AppDiscovery.Discover(new Testy.Windows.AppDiscoveryRequest
        {
            IncludeRunning = false, IncludeInstalled = false, Recent = [(@"\\testy-unreachable.invalid\share\Remote.exe", "Unreachable Remote Desk", ""), ("relative.exe", "Relative Desk", "")]
        });
        Check(found.Count == 0 && watch.Elapsed < TimeSpan.FromSeconds(1), $"Stored network and relative paths are skipped at once; found {found.Count} in {watch.ElapsedMilliseconds} ms.");

        await using var harness = new Harness("stored-paths");
        await harness.InitializeAsync();
        var store = new WorkspaceStore(harness.Workspace);
        // A test file written elsewhere (restored, imported, synced or edited) that names a network program.
        var remote = new TestCase { Name = "Remote program", Intent = "Names a network path.", TargetPath = @"\\testy-unreachable.invalid\share\Remote.exe", TargetName = "Unreachable Remote Desk", Steps = [new TestStep { Action = StepAction.AssertExists, Selector = "id:ResetButton" }] };
        store.SaveTest(remote);
        watch.Restart();
        var run = await harness.Client.CallToolAsync("run_test", new { testId = remote.Id });
        Check(run.IsError && run.Text.Contains("The test's targetPath must be on a local drive", StringComparison.Ordinal) && run.Text.Contains("Nothing was opened", StringComparison.Ordinal) && watch.Elapsed < TimeSpan.FromSeconds(2),
            $"run_test refuses a stored network program without opening it ({watch.ElapsedMilliseconds} ms): " + run.Text);
        var relative = new TestCase { Name = "Relative program", Intent = "Names a relative path.", TargetPath = "Desk.exe", Steps = [new TestStep { Action = StepAction.AssertExists, Selector = "id:ResetButton" }] };
        store.SaveTest(relative);
        var relativeRun = await harness.Client.CallToolAsync("run_test", new { testId = relative.Id });
        Check(relativeRun.IsError && relativeRun.Text.Contains("full path", StringComparison.Ordinal), "A stored relative path is refused the same way: " + relativeRun.Text);
        watch.Restart();
        var find = (await harness.Client.CallToolAsync("find_app", new { query = "Unreachable Remote Desk", includeInstalled = false })).RequireOk("find_app").Structured;
        Check(find.GetProperty("candidates").EnumerateArray().All(c => !(c.GetProperty("exePath").GetString() ?? "").StartsWith(@"\\", StringComparison.Ordinal)) && watch.Elapsed < TimeSpan.FromSeconds(5),
            "Resolving a name never offers (or opens) a stored network program: " + find.GetRawText());

        // A copied file (tests\A.json holding test B): reading or updating it through A would write test B's file.
        var original = (await harness.Client.CallToolAsync("create_test", new { name = "The original", intent = "Its file is copied.", steps = CustomerSteps("Ada") })).RequireOk("create_test").Structured.GetProperty("testId").GetString()!;
        var originalPath = Path.Combine(harness.Workspace, "tests", original + ".json");
        var before = File.ReadAllText(originalPath);
        File.Copy(originalPath, Path.Combine(harness.Workspace, "tests", "copied.json"));
        var get = await harness.Client.CallToolAsync("get_test", new { testId = "copied" });
        Check(get.IsError && get.Text.Contains($"holds the test with id '{original}'", StringComparison.Ordinal), "A test file whose id differs from its name is refused: " + get.Text);
        var update = await harness.Client.CallToolAsync("update_test", new { testId = "copied", name = "Overwritten through the copy" });
        Check(update.IsError && File.ReadAllText(originalPath) == before, "update_test through a copied file must not change the test the copy names: " + update.Text);
        WriteRun(harness.Workspace, "real-run", original, RunStatus.Passed);
        File.Copy(Path.Combine(harness.Workspace, "runs", "real-run.json"), Path.Combine(harness.Workspace, "runs", "copied-run.json"), overwrite: true);
        var copiedRun = await harness.Client.CallToolAsync("get_run", new { runId = "copied-run" });
        Check(copiedRun.IsError && copiedRun.Text.Contains("holds the run with id 'real-run'", StringComparison.Ordinal), "A run file whose id differs from its name is refused: " + copiedRun.Text);
    }

    private static async Task DesktopLease()
    {
        if (!DesktopAvailable) return;
        var (harness, testId) = await RunHarnessAsync("desktop-lease", TimeSpan.FromMilliseconds(50));
        await using var _ = harness;
        harness.Service.DriverOverride = null; // the real attach: it fails for a pid without a window, which proves the lease was passed
        var pid = Environment.ProcessId == 4 ? 8 : 4;
        using (var lease = OperationsDesktopLease.TryAcquire())
        {
            // Held here, or by Testy Studio or the background agent if one of them is running a test at this moment: refused either way.
            foreach (var attempt in new[] { 1, 2 })
            {
                var step = await harness.Client.CallToolAsync("perform_step", new { pid, step = new { action = "click", selector = "id:ResetButton" } });
                Check(step.IsError && step.Text.Contains("holds the desktop lease", StringComparison.Ordinal), $"perform_step (attempt {attempt}) must be refused while the lease is held: " + step.Text);
                var run = await harness.Client.CallToolAsync("run_test", new { testId, pid });
                Check(run.IsError && run.Text.Contains("holds the desktop lease", StringComparison.Ordinal), $"run_test (attempt {attempt}) must be refused while the lease is held: " + run.Text);
                // Starting a program puts a new window in front of a run in progress elsewhere: launch_app and launch true wait for the lease too.
                var launch = await harness.Client.CallToolAsync("launch_app", new { exe = IdleFixture, waitForWindowSeconds = 1 });
                Check(launch.IsError && launch.Text.Contains("holds the desktop lease", StringComparison.Ordinal), $"launch_app (attempt {attempt}) must be refused while the lease is held: " + launch.Text);
                var launchInspect = await harness.Client.CallToolAsync("inspect_app", new { app = IdleFixture, launch = true, includeScreenshot = false });
                Check(launchInspect.IsError && launchInspect.Text.Contains("holds the desktop lease", StringComparison.Ordinal), $"inspect_app with launch true (attempt {attempt}) must be refused while the lease is held: " + launchInspect.Text);
                Check(await harness.LaunchedCountAsync() == 0, "Nothing may have been started while the lease was held elsewhere.");
            }
            if (lease is null) return; // another Testy process holds it; what follows needs it free
        }
        foreach (var attempt in new[] { 1, 2 })
        {
            var step = await harness.Client.CallToolAsync("perform_step", new { pid, step = new { action = "click", selector = "id:ResetButton" } });
            Check(step.IsError && step.Text.Contains("Could not connect to process", StringComparison.Ordinal), $"With the lease free, perform_step (attempt {attempt}) gets as far as connecting: " + step.Text);
        }
        var inspect = await harness.Client.CallToolAsync("inspect_app", new { pid, includeScreenshot = false });
        Check(inspect.IsError && inspect.Text.Contains("Could not connect to process", StringComparison.Ordinal), "Observation tools never needed the lease.");
        using var free = OperationsDesktopLease.TryAcquire();
        Check(free is not null, "A refused or failed call must release the lease and the in-process slot.");
    }

    // ═══════════════ Files ═══════════════
    private static async Task EvidenceContainment()
    {
        await using var harness = new Harness("evidence");
        await harness.InitializeAsync();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        string Save(string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, png); return path; }
        var artifacts = Path.Combine(harness.Workspace, "artifacts");
        var inside = Save(Path.Combine(artifacts, "good", "step-001.png"));
        var outside = Save(Path.Combine(Root, "evidence-outside", "secret.png"));
        var notPng = Path.Combine(artifacts, "text", "step-001.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(notPng)!);
        File.WriteAllText(notPng, "not an image");
        var otherRun = Save(Path.Combine(artifacts, "other-run", "step-001.png"));
        var elsewhere = Save(Path.Combine(harness.Workspace, "tests", "picture.png"));
        var cases = new (string Run, string Screenshot, string? Folder, bool Served)[]
        {
            ("good", inside, null, true),
            ("outside", outside, null, false),
            ("not-png", notPng, Path.Combine(artifacts, "text"), false),
            ("parent", Path.Combine(artifacts, "parent", "..", "good", "step-001.png"), Path.Combine(artifacts, "parent"), false),
            ("escape", Path.Combine(artifacts, "escape", "..", "..", "..", "evidence-outside", "secret.png"), Path.Combine(artifacts, "escape"), false),
            ("other-run", otherRun, Path.Combine(artifacts, "mine"), false),
            ("workspace-not-artifacts", elsewhere, null, false),
            ("network", @"\\localhost\c$\Windows\Web\Screen\img100.png", null, false),
            ("missing", Path.Combine(artifacts, "missing", "step-001.png"), null, false)
        };
        foreach (var (run, screenshot, folder, served) in cases)
        {
            WriteRun(harness.Workspace, run, "evidence-test", RunStatus.Passed, screenshot: screenshot, artifactDirectory: folder ?? Path.Combine(artifacts, run));
            var view = (await harness.Client.CallToolAsync("get_run", new { runId = run })).RequireOk("get_run").Structured;
            Check(view.GetProperty("steps")[0].GetProperty("screenshotAvailable").GetBoolean() == served && view.GetProperty("screenshotsAvailable").GetBoolean() == served, $"screenshotAvailable of run '{run}' must be {served}.");
            var image = await harness.Client.CallToolAsync("get_run_screenshot", new { runId = run, stepNumber = 1 });
            if (served) Check(!image.IsError && image.Images.Count == 1 && image.Structured.GetProperty("path").GetString() == inside && image.Structured.GetProperty("scale").GetDouble() == 1, "The screenshot inside the run's evidence folder is returned.");
            else Check(image.IsError && image.Images.Count == 0 && image.Text.Contains("has no screenshot", StringComparison.Ordinal), $"Run '{run}' names a file that must not be served: " + image.Text);
        }
        var beyond = await harness.Client.CallToolAsync("get_run_screenshot", new { runId = "good", stepNumber = 3 });
        Check(beyond.IsError && beyond.Text.Contains("stepNumber must be 1–2", StringComparison.Ordinal), "A step number beyond the run is explained: " + beyond.Text);
        var skipped = await harness.Client.CallToolAsync("get_run_screenshot", new { runId = "good", stepNumber = 2 });
        Check(skipped.IsError && skipped.Text.Contains("has no screenshot", StringComparison.Ordinal), "A step without a screenshot says so.");
    }

    private static async Task RunIndexAndRetention()
    {
        await using var harness = new Harness("run-index");
        await harness.InitializeAsync();
        var created = (await harness.Client.CallToolAsync("create_test", new { name = "Indexed", intent = "Many runs.", steps = CustomerSteps("Ada") })).RequireOk("create_test").Structured.GetProperty("testId").GetString()!;
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        for (var index = 0; index < 300; index++) WriteRun(harness.Workspace, "run-" + index.ToString("000"), created, index % 2 == 0 ? RunStatus.Passed : RunStatus.Failed, snapshotElements: 5, startedAt: start.AddSeconds(index));
        var first = (await harness.Client.CallToolAsync("list_tests")).RequireOk("list_tests").Structured;
        Check(first.GetProperty("tests")[0].GetProperty("lastRun").GetProperty("runId").GetString() == "run-299", "The latest run is found among 300.");
        var read = harness.Service.RunFilesRead;
        Check(read == 300, $"Every run file is read once; {read} reads were counted.");
        (await harness.Client.CallToolAsync("list_tests")).RequireOk("list_tests");
        var runs = (await harness.Client.CallToolAsync("list_runs", new { limit = 5 })).RequireOk("list_runs").Structured;
        Check(runs.GetProperty("total").GetInt32() == 300 && runs.GetProperty("returned").GetInt32() == 5 && runs.GetProperty("runs")[0].GetProperty("runId").GetString() == "run-299" && runs.GetProperty("runs")[0].GetProperty("stepCount").GetInt32() == 2, "list_runs pages the newest runs with their step counts.");
        Check((await harness.Client.CallToolAsync("get_workspace_info")).RequireOk("get_workspace_info").Structured.GetProperty("counts").GetProperty("runs").GetInt32() == 300, "The run count comes from the same index.");
        Check(harness.Service.RunFilesRead == read, $"Unchanged run files must not be read again; {harness.Service.RunFilesRead - read} further reads were counted.");
        WriteRun(harness.Workspace, "run-150", created, RunStatus.Cancelled, snapshotElements: 7, startedAt: start.AddSeconds(150));
        File.Delete(Path.Combine(harness.Workspace, "runs", "run-000.json"));
        File.WriteAllText(Path.Combine(harness.Workspace, "runs", "half-written.json"), "{\"id\":\"half");
        var after = (await harness.Client.CallToolAsync("list_runs", new { limit = 200 })).RequireOk("list_runs").Structured;
        Check(after.GetProperty("total").GetInt32() == 299 && after.GetProperty("runs").EnumerateArray().Single(r => r.GetProperty("runId").GetString() == "run-150").GetProperty("status").GetString() == "cancelled",
            "A changed file is read again, a deleted one leaves the list, and an unreadable one is left out without failing the call.");
        Check(harness.Service.RunFilesRead == read + 2, $"Only the changed and the new file are read: {harness.Service.RunFilesRead - read} reads.");

        // Retention: evidence of old server sessions and old logs go; recent ones and this session's stay.
        var sessions = harness.Service.SessionsDirectory;
        var logs = TestyMcpService.LogDirectory(harness.Workspace);
        Directory.CreateDirectory(harness.Service.SessionDirectory);
        Directory.CreateDirectory(logs);
        string Session(string name, int ageDays) { var path = Path.Combine(sessions, name); Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "inspect-001.png"), "x"); Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays)); return path; }
        string LogFile(string name, int ageDays) { var path = Path.Combine(logs, name); File.WriteAllText(path, "line"); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays)); return path; }
        var oldSession = Session("20200101-000000-aaaaaa", 15);
        var recentSession = Session("20990101-000000-bbbbbb", 13);
        Directory.SetLastWriteTimeUtc(harness.Service.SessionDirectory, DateTime.UtcNow.AddDays(-40));
        var oldLog = LogFile("mcp-20200101.log", 31);
        var recentLog = LogFile("mcp-20990101.log", 29);
        var foreign = LogFile("notes.txt", 400);
        harness.Service.RemoveExpiredEvidence();
        Check(!Directory.Exists(oldSession) && Directory.Exists(recentSession) && Directory.Exists(harness.Service.SessionDirectory), "Session evidence older than 14 days is removed; recent evidence and this session's stay.");
        Check(!File.Exists(oldLog) && File.Exists(recentLog) && File.Exists(foreign), "Logs older than 30 days are removed; recent logs and other files stay.");
    }

    // ═══════════════ Shutdown and process lifetime ═══════════════
    private static async Task Shutdown()
    {
        var harness = new Harness("shutdown");
        await harness.InitializeAsync();
        var launch = await harness.Client.CallToolAsync("launch_app", IdleLaunch(1), timeout: TimeSpan.FromSeconds(20));
        Check(launch.IsError && launch.Text.Contains("no visible titled window", StringComparison.Ordinal), "A windowless process must fail launch_app after the wait: " + launch.Text);
        var pidText = System.Text.RegularExpressions.Regex.Match(launch.Text, @"pid (\d+)").Groups[1].Value;
        Check(pidText.Length > 0, "The failure names the pid.");
        await RequireGoneAsync(int.Parse(pidText), "The windowless process from a failed launch");
        Check(await harness.LaunchedCountAsync() == 0, "A failed launch is forgotten.");
        // The service owns the Process objects it tracks and disposes them; only the ids are kept here.
        var tracked = Process.Start(new ProcessStartInfo(IdleFixture) { UseShellExecute = false, ArgumentList = { "30000" } })!;
        var trackedId = tracked.Id;
        harness.Service.Track(tracked, IdleFixture, keepOpen: false);
        var kept = Process.Start(new ProcessStartInfo(IdleFixture) { UseShellExecute = false, ArgumentList = { "30000" } })!;
        var keptId = kept.Id;
        harness.Service.Track(kept, IdleFixture, keepOpen: true);
        try
        {
            var listed = (await harness.Client.CallToolAsync("get_workspace_info")).RequireOk("get_workspace_info").Structured.GetProperty("launchedApps").EnumerateArray().ToList();
            Check(listed.Count == 2 && listed.Any(a => a.GetProperty("pid").GetInt32() == keptId && a.GetProperty("keepOpen").GetBoolean()), "get_workspace_info lists the launched apps with keepOpen.");
            var closed = (await harness.Client.CallToolAsync("close_app", new { pid = trackedId, force = true, waitSeconds = 1 })).RequireOk("close_app").Structured;
            Check(closed.GetProperty("closed").GetBoolean() && closed.GetProperty("forced").GetBoolean(), "close_app with force ends an app that has no window to close: " + closed.GetRawText());
            await RequireGoneAsync(trackedId, "The app closed with close_app");
            Check(await harness.LaunchedCountAsync() == 1, "A closed app leaves the list.");
            var second = Process.Start(new ProcessStartInfo(IdleFixture) { UseShellExecute = false, ArgumentList = { "30000" } })!;
            var secondId = second.Id;
            harness.Service.Track(second, IdleFixture, keepOpen: false);
            harness.Client.CloseInput();
            var exit = await harness.Transport.WaitAsync(TimeSpan.FromSeconds(10));
            Check(exit == 0, "The stdio transport must complete with 0 when its input ends.");
            await harness.Client.Reader.WaitAsync(TimeSpan.FromSeconds(5));
            await harness.Service.DisposeAsync();
            await RequireGoneAsync(secondId, "A tracked app");
            Check(Alive(keptId), "An app marked keepOpen stays open when the server shuts down.");
            Check(harness.Client.UnparseableLines == 0, "Output stays clean through shutdown.");
        }
        finally { Kill(keptId); Kill(trackedId); }
    }

    /// <summary>A real server child process (dotnet Testy.Cli.dll mcp …) with its pipes.</summary>
    private sealed class ServerProcess : IAsyncDisposable
    {
        private readonly StringBuilder errors = new();
        public ServerProcess(string workspace, IDictionary<string, string?>? environment = null, bool redirectInput = true, params string[] arguments)
        {
            EnsureRuntime();
            var start = WorkerCommand.CreateSelfStartInfo();
            start.RedirectStandardInput = redirectInput;
            start.ArgumentList.Add("mcp");
            if (workspace.Length > 0) { start.ArgumentList.Add("--workspace"); start.ArgumentList.Add(workspace); }
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment.Remove(McpOptions.TokenVariable);
            if (environment is not null) foreach (var (name, value) in environment) start.Environment[name] = value;
            Process = Process.Start(start) ?? throw new InvalidOperationException("The server process did not start.");
            ErrorsRead = Task.Run(async () => { while (await Process.StandardError.ReadLineAsync() is { } line) lock (errors) errors.AppendLine(line); });
        }
        public Process Process { get; }
        public Task ErrorsRead { get; }
        public string Errors { get { lock (errors) return errors.ToString(); } }
        public McpLineClient Connect() => new(Process.StandardOutput.BaseStream, Process.StandardInput.BaseStream);
        public async Task<int> ExitAsync(int timeoutMs = 15000)
        {
            using var limit = new CancellationTokenSource(timeoutMs);
            try { await Process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) { throw new InvalidOperationException("The server process did not exit. stderr: " + Errors); }
            try { await ErrorsRead.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
            return Process.ExitCode;
        }
        public ValueTask DisposeAsync()
        {
            try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            Process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
    private static async Task InitializeAsync(McpLineClient client) =>
        Result(await client.RequestAsync("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "unit", ["version"] = "1" } }, TimeSpan.FromSeconds(45)));
    private static async Task<int> LaunchedPidAsync(McpLineClient client)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            var apps = (await client.CallToolAsync("get_workspace_info")).RequireOk("get_workspace_info").Structured.GetProperty("launchedApps");
            if (apps.GetArrayLength() > 0) return apps[0].GetProperty("pid").GetInt32();
            if (DateTimeOffset.UtcNow > deadline) throw new InvalidOperationException("The launched app was never listed.");
            await Task.Delay(100);
        }
    }

    private static async Task ProcessLifetime()
    {
        foreach (var keepOpen in new[] { false, true })
        {
            var workspace = Path.Combine(Root, "lifetime-" + keepOpen);
            await using var server = new ServerProcess(workspace);
            await using var client = server.Connect();
            await InitializeAsync(client);
            _ = client.StartToolAsync("launch_app", IdleLaunch(30, keepOpen), JsonValue.Create("launch"));
            var pid = await LaunchedPidAsync(client);
            Check(Alive(pid), "The launched fixture is running.");
            // The server is terminated, not shut down: no code of its own runs any more.
            server.Process.Kill();
            await server.ExitAsync();
            try
            {
                if (!keepOpen) await RequireGoneAsync(pid, "An app launched without keepOpen must end with the server, even when the server is killed", 5000);
                else
                {
                    await Task.Delay(1500);
                    Check(Alive(pid), "An app launched with keepOpen must survive the server.");
                }
            }
            finally { Kill(pid); }
        }
    }

    private static async Task CommandLine()
    {
        // The parser of the verb.
        static string Refused(params string[] args)
        {
            try { McpOptions.Parse(args, null); }
            catch (ArgumentException ex) { return ex.Message; }
            throw new InvalidOperationException("The options were accepted: " + string.Join(' ', args));
        }
        Check(Refused("--bogus").Contains("Unknown option --bogus for mcp", StringComparison.Ordinal) && Refused("--bogus").Contains("--workspace", StringComparison.Ordinal), "An unknown option names the allowed ones.");
        Check(Refused("--pid", "5", "--describe").Contains("Unknown option --pid for mcp", StringComparison.Ordinal), "Options of other verbs do not belong to mcp.");
        Check(Refused("--workspace").Contains("requires a value", StringComparison.Ordinal) && Refused("--port", "70000").Contains("--port", StringComparison.Ordinal) && Refused("--transport", "pipe").Contains("stdio or http", StringComparison.Ordinal), "Bad values are explained.");
        Check(Refused("--token", "t").Contains("--transport http", StringComparison.Ordinal) && Refused("--port", "0").Contains("--transport http", StringComparison.Ordinal), "An explicit --token or --port needs the HTTP transport.");
        Check(Refused("--workspace", "a", "--workspace", "b").Contains("Duplicate", StringComparison.Ordinal), "An option is given once.");
        Check(McpOptions.Parse([], "from-environment").Token is null && McpOptions.Parse([], "from-environment").Transport == "stdio", "stdio ignores the token variable.");
        Check(McpOptions.Parse(["--transport", "http"], "from-environment").Token == "from-environment", "The HTTP transport takes the token from the environment.");
        Check(McpOptions.Parse(["--transport", "http", "--token", "from-option"], "from-environment").Token == "from-option", "--token wins over the environment.");
        Check(McpOptions.Parse(["--transport", "http"], "   ").Token is null && McpOptions.Parse(["--transport", "http"], null).Token is null, "A blank variable means no token.");
        var repeated = McpOptions.Parse(["--allow-exe", @"C:\Apps", "--allow-exe", @"C:\Tools\tool.exe", "--allow-target", "desk", "--no-launch", "--parent-pid", "4"], null);
        Check(repeated.AllowedExecutables.Count == 2 && repeated.AllowedTargets.Single() == "desk" && repeated.NoLaunch && repeated.ParentPid == 4 && !repeated.Policy().AllowLaunch, "--allow-exe and --allow-target repeat.");
        Check(McpOptions.Parse(["--help"], null).Help && McpOptions.Parse(["-h"], null).Help, "--help and -h are understood.");

        // The real process: stdout is the protocol channel, so every startup error goes to stderr.
        async Task<(int Exit, string Output, string Errors)> RunAsync(IDictionary<string, string?>? environment, params string[] arguments)
        {
            await using var server = new ServerProcess("", environment, redirectInput: true, arguments);
            server.Process.StandardInput.Close();
            var output = await server.Process.StandardOutput.ReadToEndAsync();
            return (await server.ExitAsync(), output, server.Errors);
        }
        var bogus = await RunAsync(null, "--bogus");
        Check(bogus.Exit == 2 && bogus.Output.Length == 0 && bogus.Errors.Contains("Unknown option --bogus", StringComparison.Ordinal), $"mcp --bogus must exit 2 with the error on stderr and nothing on stdout: exit {bogus.Exit}, stdout '{bogus.Output}', stderr '{bogus.Errors}'.");
        var badWorkspace = await RunAsync(null, "--workspace", "C:\\bad|path<here>");
        Check(badWorkspace.Exit == 2 && badWorkspace.Output.Length == 0 && badWorkspace.Errors.Contains("error", StringComparison.Ordinal), $"An invalid workspace must exit 2 on stderr only: exit {badWorkspace.Exit}, stdout '{badWorkspace.Output}'.");
        var duplicate = await RunAsync(null, "--transport", "stdio", "--transport", "stdio");
        Check(duplicate.Exit == 2 && duplicate.Output.Length == 0 && duplicate.Errors.Contains("Duplicate", StringComparison.Ordinal), "A duplicate option must exit 2 on stderr only.");
        var help = await RunAsync(null, "--help");
        Check(help.Exit == 0 && help.Output.Contains("--transport", StringComparison.Ordinal) && help.Output.Contains(McpOptions.TokenVariable, StringComparison.Ordinal) && help.Output.Contains("--allow-exe", StringComparison.Ordinal), "mcp --help prints the usage and exits 0: " + help.Output);
        var describe = await RunAsync(null, "--describe", "--no-launch");
        Check(describe.Exit == 0 && JsonDocument.Parse(describe.Output).RootElement.GetProperty("launchPolicy").GetProperty("launch").GetString()!.Contains("--no-launch", StringComparison.Ordinal), "--describe reports the launch policy it was started with.");
        Check(describe.Output[0] == '{', "--describe writes JSON without a byte order mark.");

        // stdio starts normally with the token variable set, and the variable protects the HTTP transport.
        var workspace = Path.Combine(Root, "command-line");
        await using (var stdio = new ServerProcess(workspace, new Dictionary<string, string?> { [McpOptions.TokenVariable] = "unit-environment-token" }))
        {
            await using var client = stdio.Connect();
            await InitializeAsync(client);
            Check(Result(await client.RequestAsync("ping")).ValueKind == JsonValueKind.Object, "stdio works with the token variable set.");
            client.CloseInput();
            Check(await stdio.ExitAsync() == 0, "The stdio server exits with 0 when its stdin closes.");
            Check(!stdio.Errors.Contains("unit-environment-token", StringComparison.Ordinal), "The token never appears in the log.");
        }
        await using var http = new ServerProcess(workspace, new Dictionary<string, string?> { [McpOptions.TokenVariable] = "unit-environment-token" }, redirectInput: true, "--transport", "http", "--port", "0");
        var startup = JsonDocument.Parse(await http.Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(45)) ?? throw new InvalidOperationException("No startup line. stderr: " + http.Errors)).RootElement;
        Check(startup.GetProperty("tokenRequired").GetBoolean() && startup.GetProperty("pid").GetInt32() == http.Process.Id, "The startup line says that a token is required and names the process.");
        Check(!startup.GetRawText().Contains("unit-environment-token", StringComparison.Ordinal), "The startup line never contains the token.");
        var endpoint = JsonDocument.Parse(await File.ReadAllTextAsync(McpCommand.EndpointFile(workspace))).RootElement;
        Check(endpoint.GetProperty("url").GetString() == startup.GetProperty("url").GetString() && endpoint.GetProperty("pid").GetInt32() == http.Process.Id && endpoint.GetProperty("tokenRequired").GetBoolean() && !endpoint.GetRawText().Contains("unit-environment-token", StringComparison.Ordinal),
            "endpoint.json names the endpoint and never the token: " + endpoint.GetRawText());
        using var web = new HttpClient();
        async Task<int> PostAsync(string? bearer)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, startup.GetProperty("url").GetString()) { Content = new StringContent(Initialize(1).ToJsonString(), Encoding.UTF8, "application/json") };
            if (bearer is not null) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
            using var response = await web.SendAsync(request);
            return (int)response.StatusCode;
        }
        Check(await PostAsync(null) == 401 && await PostAsync("wrong") == 401 && await PostAsync("unit-environment-token") == 200, "The token from the environment is enforced.");
        http.Process.StandardInput.Close();
        Check(await http.ExitAsync() == 0, "The HTTP server exits with 0 when its stdin closes. stderr: " + http.Errors);
        Check(!File.Exists(McpCommand.EndpointFile(workspace)), "endpoint.json is removed when the server stops.");
        Check(!http.Errors.Contains("unit-environment-token", StringComparison.Ordinal), "The token never appears in the log.");

        // --parent-pid: the server ends when that process does, although its stdin stays open.
        using var parent = Process.Start(new ProcessStartInfo(IdleFixture) { UseShellExecute = false, ArgumentList = { "2500" } })!;
        await using var watched = new ServerProcess(workspace, null, redirectInput: true, "--transport", "http", "--port", "0", "--parent-pid", parent.Id.ToString());
        var watchedStartup = await watched.Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(45)) ?? throw new InvalidOperationException("No startup line. stderr: " + watched.Errors);
        Check(watchedStartup.Contains("\"transport\":\"http\"", StringComparison.Ordinal), "The watched server started.");
        var ending = Stopwatch.StartNew();
        Check(await watched.ExitAsync(20000) == 0, "The server exits with 0 when its --parent-pid process ends. stderr: " + watched.Errors);
        Check(ending.Elapsed < TimeSpan.FromSeconds(10) && watched.Errors.Contains("--parent-pid", StringComparison.Ordinal), "The exit follows the parent's end and is logged.");
        var missingParent = await RunAsync(null, "--transport", "http", "--parent-pid", "999999999");
        Check(missingParent.Exit == 2 && missingParent.Errors.Contains("--parent-pid", StringComparison.Ordinal), "A --parent-pid that is not running is refused.");
    }
}
