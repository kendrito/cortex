using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Testy.Cli.Mcp;
using Testy.Core;

namespace Testy.Tests;

/// <summary>Apps named in words (the pure resolver and the MCP app argument), the time limits of long calls, and the backward compatibility of every tool.</summary>
internal static partial class McpChecks
{
    public static (string Name, Func<Task> Execute)[] AppChecks() =>
    [
        ("App names: exact beats prefix beats whole words beats fuzzy overlap, each with its reason", AppScoring),
        ("App names: one clear winner above the threshold, a margin makes ties ambiguous, a single running instance wins a tie", AppAmbiguity),
        ("App names: discovery entries merge per program and a running instance takes the names of its program", AppMerging),
        ("App names: a description names its app only in a leading clause", AppMentions),
        ("MCP find_app and the app argument: candidates, ambiguity as isError, stored apps, and the pid rules", AppArguments),
        ("MCP every earlier tool argument, range, required field and resource is still accepted and declared", BackwardCompatibility),
        ("MCP run_test hands a long run over after waitSeconds and get_run waitSeconds waits for its end", TimeLimits)
    ];

    private static AppCandidate Program(string name, string exe, AppCandidateKind kind = AppCandidateKind.Installed, string product = "", string description = "") =>
        new() { Kind = kind, Name = name, ExePath = exe, ProductName = product, FileDescription = description, Sources = [kind] };
    private static AppCandidate Instance(int pid, string name, string exe, string title) =>
        new() { Kind = AppCandidateKind.Running, ProcessId = pid, Name = name, ExePath = exe, WindowTitle = title, ProcessName = Path.GetFileNameWithoutExtension(exe), Sources = [AppCandidateKind.Running] };

    private static Task AppScoring()
    {
        var desk = Program("Customer Desk", @"C:\Apps\Desk\Testy.TestLab.exe", AppCandidateKind.Sample, product: "Testy Windows test studio", description: "Customer Desk");
        var exact = AppResolver.Score("Customer Desk", desk)!;
        Check(exact.Level == AppMatchLevel.Exact && exact.Confidence == 1.0 && exact.Reason.Contains("exact name", StringComparison.Ordinal), "An exact name is exact with confidence 1: " + exact.Reason);
        Check(AppResolver.Score("customer   desk", desk)!.Level == AppMatchLevel.Exact && AppResolver.Score("the Customer Desk app", desk)!.Level == AppMatchLevel.Exact, "Case, spacing and filler words do not matter.");
        Check(AppResolver.Score("Testy.TestLab.exe", desk)!.Level == AppMatchLevel.Exact && AppResolver.Score("testlab", desk) is { Level: AppMatchLevel.Word } && AppResolver.Score("TestLab", desk) is { Level: AppMatchLevel.Word },
            "The exe name matches exactly, and one of its words matches whether camel case is written or not.");
        var prefix = AppResolver.Score("Customer", desk)!;
        Check(prefix.Level == AppMatchLevel.Prefix && prefix.Confidence is > 0.8 and < 1.0, "The first word is a prefix match: " + prefix.Confidence);
        var partial = AppResolver.Score("Calc", Program("Calculator", @"C:\Apps\Calculator.exe"))!;
        Check(partial.Level == AppMatchLevel.Prefix && partial.Confidence < prefix.Confidence, "The start of a word is a prefix match, a little below whole words at the start.");
        var word = AppResolver.Score("Desk", desk)!;
        Check(word.Level == AppMatchLevel.Word && word.Confidence < partial.Confidence && word.Confidence >= AppResolver.MinimumConfidence, "A whole word inside the name matches at the threshold or above.");
        var fuzzy = AppResolver.Score("Desk Customer", desk)!;
        Check(fuzzy.Level == AppMatchLevel.Fuzzy && fuzzy.Confidence < AppResolver.MinimumConfidence, "Words out of order are only a fuzzy overlap, below the threshold: " + fuzzy.Confidence);
        Check(AppResolver.Score("Custmer Desk", desk) is { Level: AppMatchLevel.Fuzzy }, "One typo in a long word still overlaps.");
        Check(AppResolver.Score("Spreadsheet", desk) is null && AppResolver.Score("", desk) is null && AppResolver.Score("a", desk) is null, "Unrelated or empty names do not match.");
        var titled = Instance(10, "", @"C:\Apps\Word\winword.exe", "Report.docx - Word");
        var title = AppResolver.Score("Word", titled)!;
        Check(title.Level == AppMatchLevel.Exact && title.Confidence is < 1.0 and > 0.95 && title.Field == "window title", "A part of a window title matches exactly, just below the whole title.");
        Check(AppResolver.Score("Report.docx - Word", titled)!.Confidence == 1.0, "The whole window title is exact.");
        var shortcut = Program("Orders", @"C:\Apps\o.exe"); shortcut.ShortcutNames.Add("Order Desk");
        Check(AppResolver.Score("Order Desk", shortcut) is { Level: AppMatchLevel.Exact, Field: "shortcut name" }, "A Start menu shortcut name matches.");
        return Task.CompletedTask;
    }

    private static Task AppAmbiguity()
    {
        var desk = Program("Customer Desk", @"C:\Apps\Desk\Desk.exe", AppCandidateKind.Sample);
        var orders = Program("Order Desk", @"C:\Apps\Orders\Orders.exe");
        var unique = AppResolver.Resolve("Customer Desk", [desk, orders]);
        Check(unique.Status == AppResolutionStatus.Unique && unique.Best!.Candidate == desk && unique.Matches.Count == 2 && unique.Matches[1].Confidence < AppResolver.MinimumConfidence, "One exact match among weak ones is unique.");
        var missing = AppResolver.Resolve("Spreadsheet", [desk, orders]);
        Check(missing.Status == AppResolutionStatus.NotFound && missing.Best is null && missing.Message.Contains("Spreadsheet", StringComparison.Ordinal), "No match is not found.");
        var weak = AppResolver.Resolve("Desk Customer", [desk]);
        Check(weak.Status == AppResolutionStatus.NotFound && weak.Matches.Count == 1 && weak.Message.Contains("below", StringComparison.Ordinal), "A match below the threshold selects nothing but is still listed.");
        // Two programs with the same name: ambiguous. A running instance of one of them wins the tie.
        var copy = Program("Customer Desk", @"D:\Other\Desk.exe");
        var tie = AppResolver.Resolve("Customer Desk", [desk, copy]);
        Check(tie.Status == AppResolutionStatus.Ambiguous && tie.Best is null && tie.Message.Contains("2 apps", StringComparison.Ordinal), "Equal names are ambiguous: " + tie.Message);
        // A running instance named as a program (its file description, product or exe name) is the app the person is looking at: it wins the tie.
        var running = Instance(4242, "Customer Desk", @"D:\Other\Desk.exe", "Customer Desk"); running.FileDescription = "Customer Desk";
        var wins = AppResolver.Resolve("Customer Desk", [desk, running]);
        Check(wins.Status == AppResolutionStatus.Unique && wins.Best!.Candidate.ProcessId == 4242 && wins.Matches[0].Candidate.IsRunning, "A single running instance wins a tie and is ranked first.");
        // A window whose title merely reads like the name (a folder, a browser or terminal tab) is another program: the tie stays ambiguous.
        var folder = Instance(4250, "Windows Explorer", @"C:\Windows\explorer.exe", "Customer Desk"); folder.FileDescription = "Windows Explorer";
        var byTitle = AppResolver.Resolve("Customer Desk", [desk, folder]);
        Check(byTitle.Status == AppResolutionStatus.Ambiguous && byTitle.Best is null && byTitle.Message.Contains("4250", StringComparison.Ordinal), "An installed exact name and an unrelated running window with that exact title are ambiguous: " + byTitle.Message);
        var untitled = Instance(4251, "Customer Desk", @"C:\Tools\Viewer.exe", "Customer Desk"); // no version resource: its display name comes from its title
        Check(AppResolver.Resolve("Customer Desk", [desk, untitled]).Status == AppResolutionStatus.Ambiguous, "A display name that is only the window title does not name the program.");
        var sameProgram = Instance(4252, "", @"C:\Apps\Desk\Desk.exe", "Customer Desk");
        Check(AppResolver.Resolve("Customer Desk", [Program("Customer Desk", @"C:\Apps\Desk\Desk.exe"), sameProgram]) is { Status: AppResolutionStatus.Unique } same && same.Best!.Candidate.ProcessId == 4252,
            "When every contender is the same program, its running instance wins the tie, however it was named.");
        // Within the margin but not equal: ambiguous, even when the lower one runs (a running instance only breaks an exact tie).
        var near = Instance(4243, "Customer Desktop", @"E:\Pro\Pro.exe", "Customer Desktop");
        var close = AppResolver.Resolve("Customer Desk", [Program("Customer Desk Pro Suite", @"F:\s.exe"), near]);
        Check(close.Status == AppResolutionStatus.Ambiguous && close.Matches[0].Confidence - close.Matches[1].Confidence is > 0 and <= AppResolver.AmbiguityMargin, "Two matches within the margin are ambiguous whatever runs: " + close.Message);
        // Beyond the margin: the better one wins even when the weaker one runs.
        var beyond = AppResolver.Resolve("Customer Desk", [desk, Instance(4244, "Customer Desk Pro", @"E:\Pro\Pro.exe", "Customer Desk Pro")]);
        Check(beyond.Status == AppResolutionStatus.Unique && beyond.Best!.Candidate == desk, "An exact match beats a running prefix match by more than the margin.");
        // Two running instances of one program: two answers to attach to, one program to launch.
        var first = Instance(5001, "Customer Desk", @"C:\Apps\Desk\Desk.exe", "Testy TestLab — Customer Desk");
        var second = Instance(5002, "Customer Desk", @"C:\Apps\Desk\Desk.exe", "Testy TestLab — Customer Desk");
        var attach = AppResolver.Resolve("Customer Desk", [first, second], AppResolvePurpose.Attach);
        Check(attach.Status == AppResolutionStatus.Ambiguous && attach.Message.Contains("5001", StringComparison.Ordinal) && attach.Message.Contains("5002", StringComparison.Ordinal) && attach.Message.Contains("pid", StringComparison.Ordinal),
            "Two running instances are ambiguous to attach to, and the message names both pids: " + attach.Message);
        var launch = AppResolver.Resolve("Customer Desk", [first, second], AppResolvePurpose.Launch);
        Check(launch.Status == AppResolutionStatus.Unique && launch.Matches.Count == 1 && launch.Best!.Candidate.ExePath == @"C:\Apps\Desk\Desk.exe", "Two instances of one program are one program to launch.");
        Check(AppResolver.Rank([AppResolver.Score("Customer Desk", desk)!, AppResolver.Score("Customer Desk", first)!]).First().Candidate.IsRunning, "Running instances come first among equals.");
        return Task.CompletedTask;
    }

    private static Task AppMerging()
    {
        var exe = @"C:\Apps\Desk\Testy.TestLab.exe";
        var sample = Program("Customer Desk", exe, AppCandidateKind.Sample);
        var installed = Program("Desk shortcut", exe); installed.ShortcutNames.Add("Desk shortcut");
        var recent = Program("My desk", exe.ToUpperInvariant(), AppCandidateKind.Recent);
        var merged = AppResolver.Merge([sample, installed, recent]);
        Check(merged.Count == 1 && merged[0].Kind == AppCandidateKind.Recent && merged[0].Sources.Contains(AppCandidateKind.Sample) && merged[0].Sources.Contains(AppCandidateKind.Installed), "Entries for one program merge, whatever the letter case of the path; the earlier test's kind wins.");
        Check(AppResolver.Resolve("Customer Desk", merged).Status == AppResolutionStatus.Unique && AppResolver.Resolve("Desk shortcut", merged).Status == AppResolutionStatus.Unique, "Every merged name still finds the program.");
        var window = Instance(77, "", exe, "Testy TestLab — Customer Desk");
        var other = Instance(77, "", exe, "Review");
        var withRunning = AppResolver.Merge([sample, window, other, installed]);
        Check(withRunning.Count == 1 && withRunning[0].IsRunning && withRunning[0].OtherTitles.Contains("Review") && withRunning[0].Sources.Contains(AppCandidateKind.Sample), "A running instance absorbs its program's entries and its other windows.");
        Check(AppResolver.Resolve("Customer Desk", withRunning).Best!.Candidate.ProcessId == 77, "The running instance answers to the sample's name.");
        // A Store app listed twice (the package, and an App Paths entry for a file inside it) is one app.
        var package = new AppCandidate { Kind = AppCandidateKind.Installed, Name = "Paint", AppId = "Microsoft.Paint_8wekyb3d8bbwe!App", Packaged = true, PackageInstallPath = @"C:\Program Files\WindowsApps\Microsoft.Paint_1_x64__8wekyb3d8bbwe", Sources = [AppCandidateKind.Installed] };
        var file = Program("Paint", @"C:\Program Files\WindowsApps\Microsoft.Paint_1_x64__8wekyb3d8bbwe\PaintApp\mspaint.exe");
        var store = AppResolver.Merge([package, file]);
        Check(store.Count == 1 && store[0].Packaged && store[0].ExePath.EndsWith("mspaint.exe", StringComparison.Ordinal) && AppResolver.Resolve("Paint", store).Status == AppResolutionStatus.Unique, "A file inside a package folds into the packaged app.");
        return Task.CompletedTask;
    }

    private static Task AppMentions()
    {
        Check(AppResolver.FindMention("In Customer Desk: click id:ResetButton\nassert text id:StatusMessage = \"Ready\"") is { App: "Customer Desk" } colon
            && colon.Remainder == "click id:ResetButton\nassert text id:StatusMessage = \"Ready\"", "In X: names the app and keeps every command.");
        Check(AppResolver.FindMention("In the Customer Desk app, add a customer named Ada") is { App: "Customer Desk", Remainder: "add a customer named Ada" }, "In the X app, … names X.");
        Check(AppResolver.FindMention("Using Order Desk - enter an order") is { App: "Order Desk", Remainder: "enter an order" }, "Using X - … names X.");
        Check(AppResolver.FindMention("Launch Customer Desk and add a customer") is { App: "Customer Desk", Remainder: "add a customer" }, "Launch X and … names X.");
        Check(AppResolver.FindMention("App: Customer Desk\nclick id:ResetButton") is { App: "Customer Desk", Remainder: "click id:ResetButton" }, "A first line App: X names X.");
        Check(AppResolver.FindMention("In Customer Desk:\nclick id:ResetButton") is { App: "Customer Desk", Remainder: "click id:ResetButton" }, "The commands may start on the next line.");
        Check(AppResolver.FindMention("click id:ResetButton\nassert text id:StatusMessage = \"x\"") is null, "Plain commands name no app.");
        Check(AppResolver.FindMention("Open Settings and enable dark mode") is null && AppResolver.FindMention("Start the order and submit it") is null, "open and start describe something inside the app, not an app.");
        Check(AppResolver.FindMention("add a customer in Customer Desk") is null, "Only a leading clause names the app.");
        Check(AppResolver.FindMention("In " + new string('x', 100) + ": click id:A") is null && AppResolver.FindMention("In one two three four five six seven eight nine: click") is null, "A long phrase is not an app name.");
        Check(AppResolver.FindMention("") is null && AppResolver.FindMention(null) is null, "Nothing names nothing.");
        return Task.CompletedTask;
    }

    /// <summary>The synthetic apps the app-argument checks resolve against: a sample that is not running and two instances of one program.</summary>
    private static List<AppCandidate> SyntheticApps() =>
    [
        Program("Customer Desk", @"C:\Apps\Desk\Desk.exe", AppCandidateKind.Sample),
        Instance(4242, "Order Desk", @"C:\Apps\Orders\Orders.exe", "Order Desk — North"),
        Instance(4243, "Order Desk", @"C:\Apps\Orders\Orders.exe", "Order Desk — South")
    ];

    private static async Task AppArguments()
    {
        await using var harness = new Harness("app-arguments");
        harness.Service.CandidatesOverride = _ => SyntheticApps();
        await harness.InitializeAsync();
        var tools = Result(await harness.Client.RequestAsync("tools/list")).GetProperty("tools").EnumerateArray().ToList();
        JsonElement Schema(string tool, string kind) => tools.Single(t => t.GetProperty("name").GetString() == tool).GetProperty(kind);
        var find = Schema("find_app", "inputSchema");
        Check(find.GetProperty("required").EnumerateArray().Select(r => r.GetString()).SequenceEqual(["query"]) && find.GetProperty("properties").GetProperty("includeInstalled").GetProperty("default").GetBoolean()
            && find.GetProperty("properties").GetProperty("limit").GetProperty("default").GetInt32() == 10, "find_app takes query (required), includeInstalled (default true) and limit (default 10).");
        var findAnnotations = tools.Single(t => t.GetProperty("name").GetString() == "find_app").GetProperty("annotations");
        Check(findAnnotations.GetProperty("readOnlyHint").GetBoolean() && findAnnotations.GetProperty("openWorldHint").GetBoolean() && !findAnnotations.GetProperty("destructiveHint").GetBoolean(), "find_app reads what other applications show and changes nothing.");
        foreach (var tool in new[] { "inspect_app", "screenshot_app", "perform_step", "activate_app", "run_test", "launch_app", "create_test", "update_test", "validate_test" })
            Check(Schema(tool, "inputSchema").GetProperty("properties").TryGetProperty("app", out var app) && app.GetProperty("type").GetString() == "string", $"{tool} accepts app.");
        foreach (var tool in new[] { "inspect_app", "screenshot_app", "perform_step" })
            Check(Schema(tool, "inputSchema").GetProperty("properties").GetProperty("launch").GetProperty("default").GetBoolean() == false, $"{tool} starts a named app only with launch true.");

        var unique = (await harness.Client.CallToolAsync("find_app", new { query = "Customer Desk" })).RequireOk("find_app").Structured;
        Check(unique.GetProperty("status").GetString() == "unique" && unique.GetProperty("best").GetProperty("kind").GetString() == "sample" && unique.GetProperty("best").GetProperty("exePath").GetString() == @"C:\Apps\Desk\Desk.exe"
            && unique.GetProperty("best").GetProperty("pid").ValueKind == JsonValueKind.Null && unique.GetProperty("best").GetProperty("confidence").GetDouble() == 1.0, "find_app finds the sample by name: " + unique.GetRawText());
        Check(McpSchemaValidator.FirstViolation(Schema("find_app", "outputSchema"), unique, "find_app", declaredTopLevelOnly: true) is null, "find_app's result satisfies its output schema.");
        var ambiguous = (await harness.Client.CallToolAsync("find_app", new { query = "Order Desk", limit = 1 })).RequireOk("find_app").Structured;
        Check(ambiguous.GetProperty("status").GetString() == "ambiguous" && ambiguous.GetProperty("best").ValueKind == JsonValueKind.Null && ambiguous.GetProperty("candidates").GetArrayLength() == 1 && ambiguous.GetProperty("total").GetInt32() >= 2,
            "Two instances are ambiguous, and limit caps the list: " + ambiguous.GetRawText());
        Check((await harness.Client.CallToolAsync("find_app", new { query = "Spreadsheet" })).RequireOk("find_app").Structured.GetProperty("status").GetString() == "notFound", "An unknown name is notFound, not an error.");

        var missing = await harness.Client.CallToolAsync("inspect_app", new { includeScreenshot = false });
        Check(missing.IsError && missing.Text.Contains("Missing required argument 'pid'", StringComparison.Ordinal) && missing.Text.Contains("app", StringComparison.Ordinal), "Without pid or app the call names both: " + missing.Text);
        var both = await harness.Client.CallToolAsync("inspect_app", new { pid = 4242, app = "Order Desk", includeScreenshot = false });
        Check(both.IsError && both.Text.Contains("not both", StringComparison.Ordinal), "pid and app together are refused.");
        foreach (var tool in new[] { "inspect_app", "screenshot_app", "activate_app" })
        {
            var twice = await harness.Client.CallToolAsync(tool, new { app = "Order Desk" });
            Check(twice.IsError && twice.Text.Contains("4242", StringComparison.Ordinal) && twice.Text.Contains("4243", StringComparison.Ordinal) && twice.Text.Contains("Candidates", StringComparison.Ordinal) && twice.Text.Contains("\"kind\":\"running\"", StringComparison.Ordinal),
                $"{tool} with an ambiguous name returns isError with the candidates as JSON: " + twice.Text);
        }
        var step = await harness.Client.CallToolAsync("perform_step", new { app = "Order Desk", step = new { action = "click", selector = "id:X" } });
        Check(step.IsError && step.Text.Contains("4243", StringComparison.Ordinal), "perform_step refuses an ambiguous name before sending anything.");
        var notRunning = await harness.Client.CallToolAsync("inspect_app", new { app = "Customer Desk", includeScreenshot = false });
        Check(notRunning.IsError && notRunning.Text.Contains("not running", StringComparison.Ordinal) && notRunning.Text.Contains("launch true", StringComparison.Ordinal), "A named app that is not running is started only with launch true: " + notRunning.Text);
        var unknown = await harness.Client.CallToolAsync("inspect_app", new { app = "Spreadsheet" });
        Check(unknown.IsError && unknown.Text.Contains("find_app", StringComparison.Ordinal), "An unknown name points to find_app.");
        var pathNotRunning = await harness.Client.CallToolAsync("inspect_app", new { app = @"C:\Apps\Nowhere\Missing.exe", includeScreenshot = false });
        Check(pathNotRunning.IsError && pathNotRunning.Text.Contains("does not exist", StringComparison.Ordinal), "An exe path that is neither running nor present is explained: " + pathNotRunning.Text);

        // create_test/update_test/validate_test: the app is stored as the program and its name.
        var created = (await harness.Client.CallToolAsync("create_test", new { name = "Named app", intent = "Stored app.", app = "Customer Desk", steps = CustomerSteps("Ada") })).RequireOk("create_test").Structured;
        var id = created.GetProperty("testId").GetString()!;
        Check(created.GetProperty("targetPath").GetString() == @"C:\Apps\Desk\Desk.exe" && created.GetProperty("targetName").GetString() == "Customer Desk" && created.GetProperty("message").GetString()!.Contains("Customer Desk", StringComparison.Ordinal),
            "create_test with app stores the resolved program and name: " + created.GetRawText());
        Check(McpSchemaValidator.FirstViolation(Schema("create_test", "outputSchema"), created, "create_test", declaredTopLevelOnly: true) is null, "create_test's result satisfies its output schema.");
        var stored = new WorkspaceStore(harness.Workspace).LoadTests().Single(t => t.Id == id);
        Check(stored.TargetPath == @"C:\Apps\Desk\Desk.exe" && stored.TargetName == "Customer Desk" && stored.TargetAppId == "", "The test file carries the program and its display name for Studio.");
        var fetched = (await harness.Client.CallToolAsync("get_test", new { testId = id })).RequireOk("get_test").Structured;
        Check(fetched.GetProperty("targetName").GetString() == "Customer Desk" && fetched.GetProperty("targetPath").GetString() == @"C:\Apps\Desk\Desk.exe", "get_test shows the stored app.");
        Check((await harness.Client.CallToolAsync("list_tests")).RequireOk("list_tests").Structured.GetProperty("tests")[0].GetProperty("targetName").GetString() == "Customer Desk", "list_tests shows the stored app.");
        var program = (await harness.Client.CallToolAsync("create_test", new { name = "One program", intent = "Two instances, one program.", app = "Order Desk", steps = CustomerSteps("Ada") })).RequireOk("create_test").Structured;
        Check(program.GetProperty("targetPath").GetString() == @"C:\Apps\Orders\Orders.exe", "Two running instances of one program are one program to store.");
        var byPath = (await harness.Client.CallToolAsync("create_test", new { name = "By path", intent = "An exe path.", app = @"C:\Apps\Other\Other.exe", steps = CustomerSteps("Ada") })).RequireOk("create_test").Structured;
        Check(byPath.GetProperty("targetPath").GetString() == @"C:\Apps\Other\Other.exe" && byPath.GetProperty("targetName").GetString() == "Other", "An exe path is stored as given, named after its file.");
        var count = new WorkspaceStore(harness.Workspace).LoadTests().Count;
        var nothing = await harness.Client.CallToolAsync("create_test", new { name = "No app", intent = "x", app = "Spreadsheet", steps = CustomerSteps("Ada") });
        var clash = await harness.Client.CallToolAsync("create_test", new { name = "Clash", intent = "x", app = "Customer Desk", targetPath = @"C:\Apps\Desk\Desk.exe", steps = CustomerSteps("Ada") });
        Check(nothing.IsError && clash.IsError && clash.Text.Contains("not both", StringComparison.Ordinal) && new WorkspaceStore(harness.Workspace).LoadTests().Count == count, "An unknown app or app with targetPath saves nothing.");
        var validated = (await harness.Client.CallToolAsync("validate_test", new { name = "V", steps = CustomerSteps("Ada"), app = "Customer Desk" })).RequireOk("validate_test").Structured;
        Check(validated.GetProperty("valid").GetBoolean() && validated.GetProperty("targetName").GetString() == "Customer Desk", "validate_test resolves app like create_test.");
        var invalid = (await harness.Client.CallToolAsync("validate_test", new { name = "V", steps = CustomerSteps("Ada"), app = "Spreadsheet" })).RequireOk("validate_test").Structured;
        Check(!invalid.GetProperty("valid").GetBoolean() && invalid.GetProperty("problems")[0].GetString()!.StartsWith("app:", StringComparison.Ordinal), "validate_test reports an unknown app as a problem.");
        var cleared = (await harness.Client.CallToolAsync("update_test", new { testId = id, app = "" })).RequireOk("update_test").Structured;
        Check(cleared.GetProperty("targetPath").GetString() == "" && cleared.GetProperty("targetName").GetString() == "", "update_test with an empty app clears the stored app.");
        (await harness.Client.CallToolAsync("update_test", new { testId = id, app = "Customer Desk" })).RequireOk("update_test");

        // run_test: the stored app is used when nothing else is named; an ambiguous name is an error; one target at most.
        var stale = await harness.Client.CallToolAsync("run_test", new { testId = id });
        Check(stale.IsError && stale.Text.Contains(@"This test targets C:\Apps\Desk\Desk.exe", StringComparison.Ordinal), "run_test uses the stored app, and explains a program that is gone: " + stale.Text);
        var ambiguousRun = await harness.Client.CallToolAsync("run_test", new { testId = id, app = "Order Desk" });
        Check(ambiguousRun.IsError && ambiguousRun.Text.Contains("4242", StringComparison.Ordinal) && ambiguousRun.Text.Contains("4243", StringComparison.Ordinal), "run_test with an ambiguous name lists the candidates.");
        var several = await harness.Client.CallToolAsync("run_test", new { testId = id, app = "Customer Desk", pid = 4242 });
        Check(several.IsError && several.Text.Contains("only one of pid", StringComparison.Ordinal), "run_test takes one of pid, exe or app.");
        var extras = await harness.Client.CallToolAsync("run_test", new { testId = id, pid = 4242, keepOpen = true });
        Check(extras.IsError && extras.Text.Contains("apply only when exe is given", StringComparison.Ordinal), "Launch options still do not combine with pid.");
        var unnamed = (await harness.Client.CallToolAsync("create_test", new { name = "Unnamed", intent = "x", steps = CustomerSteps("Ada") })).RequireOk("create_test").Structured.GetProperty("testId").GetString();
        var nameIt = await harness.Client.CallToolAsync("run_test", new { testId = unnamed });
        Check(nameIt.IsError && nameIt.Text.Contains("pid", StringComparison.Ordinal) && nameIt.Text.Contains("exe", StringComparison.Ordinal) && nameIt.Text.Contains("app", StringComparison.Ordinal) && nameIt.Text.Contains("update_test", StringComparison.Ordinal),
            "A test without an app asks for pid, exe or app and says how to store one: " + nameIt.Text);
        var launchAmbiguous = await harness.Client.CallToolAsync("launch_app", new { app = "Spreadsheet" });
        var launchBoth = await harness.Client.CallToolAsync("launch_app", new { app = "Customer Desk", exe = @"C:\Apps\Desk\Desk.exe" });
        var launchNone = await harness.Client.CallToolAsync("launch_app", new { });
        Check(launchAmbiguous.IsError && launchBoth.IsError && launchBoth.Text.Contains("not both", StringComparison.Ordinal) && launchNone.IsError && launchNone.Text.Contains("Missing required argument 'exe'", StringComparison.Ordinal),
            "launch_app takes exe or app, and names both when neither is given.");
        var noLaunch = new Harness("app-no-launch", policy: new McpLaunchPolicy(allowLaunch: false));
        await using (noLaunch)
        {
            noLaunch.Service.CandidatesOverride = _ => SyntheticApps();
            await noLaunch.InitializeAsync();
            var refused = await noLaunch.Client.CallToolAsync("launch_app", new { app = "Customer Desk" });
            Check(refused.IsError && refused.Text.Contains("--no-launch", StringComparison.Ordinal), "An app name starts nothing under --no-launch: " + refused.Text);
        }
    }

    /// <summary>What agents could already rely on before this version: argument names, types, ranges, defaults, required fields and results.</summary>
    private static readonly Dictionary<string, string[]> EarlierInputs = new(StringComparer.Ordinal)
    {
        ["get_workspace_info"] = [], ["list_tests"] = [], ["get_test"] = ["testId"],
        ["create_test"] = ["name", "intent", "category", "targetPath", "steps"], ["update_test"] = ["testId", "name", "intent", "category", "targetPath", "steps"],
        ["delete_test"] = ["testId"], ["validate_test"] = ["name", "intent", "category", "targetPath", "steps"], ["list_apps"] = [],
        ["launch_app"] = ["exe", "args", "workingDirectory", "waitForWindowSeconds", "keepOpen"], ["close_app"] = ["pid", "force", "waitSeconds"], ["activate_app"] = ["pid"],
        ["inspect_app"] = ["pid", "probe", "includeScreenshot", "maxWidth", "selector", "details", "maxElements", "offset", "filter", "within", "includeOffscreen"],
        ["screenshot_app"] = ["pid", "probe", "maxWidth"],
        ["perform_step"] = ["pid", "step", "probe", "includeScreenshot", "maxWidth", "observation", "maxElements", "offset", "filter", "within", "includeOffscreen"],
        ["run_test"] = ["testId", "mode", "pid", "exe", "args", "workingDirectory", "waitForWindowSeconds", "keepOpen", "probe", "timeoutSeconds", "wait", "waitSeconds"],
        ["cancel_run"] = ["runId"], ["list_runs"] = ["testId", "limit"], ["get_run"] = ["runId"], ["get_run_screenshot"] = ["runId", "stepNumber", "maxWidth"]
    };
    private static readonly Dictionary<string, string[]> EarlierRequiredInputs = new(StringComparer.Ordinal)
    {
        ["get_test"] = ["testId"], ["create_test"] = ["name", "intent", "steps"], ["update_test"] = ["testId"], ["delete_test"] = ["testId"], ["validate_test"] = ["name", "steps"],
        ["launch_app"] = ["exe"], ["close_app"] = ["pid"], ["activate_app"] = ["pid"], ["inspect_app"] = ["pid"], ["screenshot_app"] = ["pid"], ["perform_step"] = ["pid", "step"],
        ["run_test"] = ["testId"], ["cancel_run"] = ["runId"], ["get_run"] = ["runId"], ["get_run_screenshot"] = ["runId", "stepNumber"]
    };
    private static readonly Dictionary<string, (int Minimum, int Maximum, int Default)> EarlierRanges = new(StringComparer.Ordinal)
    {
        ["launch_app.waitForWindowSeconds"] = (1, 60, 20), ["close_app.waitSeconds"] = (1, 60, 5), ["inspect_app.maxElements"] = (1, 5000, 150), ["inspect_app.maxWidth"] = (64, 8192, 1280),
        ["perform_step.maxElements"] = (1, 5000, 40), ["run_test.waitForWindowSeconds"] = (1, 60, 20), ["run_test.timeoutSeconds"] = (1, 900, 300), ["run_test.waitSeconds"] = (1, 600, 45),
        ["list_runs.limit"] = (1, 200, 20), ["get_run_screenshot.maxWidth"] = (64, 8192, 1280), ["screenshot_app.maxWidth"] = (64, 8192, 1280)
    };
    private static readonly Dictionary<string, string[]> EarlierResults = new(StringComparer.Ordinal)
    {
        ["get_workspace_info"] = ["workspace", "testyVersion", "counts", "actions", "selectorSyntax", "sampleApp", "launchPolicy", "workflow", "desktop", "activeRuns", "launchedApps"],
        ["list_tests"] = ["tests", "count"], ["get_test"] = ["testId", "name", "steps", "intent", "category", "targetPath", "updatedAt", "stepCount"],
        ["create_test"] = ["testId", "name", "stepCount", "path", "warnings", "category", "message"], ["update_test"] = ["testId", "name", "stepCount", "path", "warnings", "category", "message"],
        ["delete_test"] = ["testId", "deleted", "name"], ["validate_test"] = ["valid", "problems", "warnings", "stepCount"], ["list_apps"] = ["apps", "count", "note"],
        ["launch_app"] = ["pid", "title", "processName", "exe", "startedAt", "keepOpen", "message"], ["close_app"] = ["pid", "closed", "forced", "exitCode", "message"],
        ["activate_app"] = ["pid", "minimized", "foreground", "title", "restored", "bounds", "message"],
        ["inspect_app"] = ["pid", "elements", "treeTruncated", "title", "processName", "source", "capturedAt", "focusedSelector", "screenshotBounds", "summary", "nextOffset", "screenshot", "screenshotError", "hints"],
        ["screenshot_app"] = ["pid", "width", "height", "scale", "title", "capturedAt", "bounds", "path", "mimeType", "sourceWidth", "sourceHeight", "note"],
        ["perform_step"] = ["status", "passed", "message", "pid", "step", "stepStatus", "stepMessage", "durationMs", "assertion", "diagnostics", "hint", "observation", "evidenceDirectory", "screenshot", "note"],
        ["run_test"] = ["runId", "status", "steps", "running", "testId", "testName", "passed", "summary", "startedAt", "finishedAt", "durationMs", "target", "artifactDirectory", "screenshotsAvailable", "diagnostics", "aiAnalysis", "projectEvidenceCount", "message"],
        ["list_runs"] = ["runs", "total", "returned"], ["get_run_screenshot"] = ["runId", "stepNumber", "width", "height", "title", "status", "path", "mimeType", "sourceWidth", "sourceHeight", "scale"]
    };
    private static readonly Dictionary<string, string[]> EarlierRequiredResults = new(StringComparer.Ordinal)
    {
        ["get_workspace_info"] = ["workspace", "testyVersion", "counts", "actions", "selectorSyntax"], ["list_tests"] = ["tests", "count"], ["get_test"] = ["testId", "name", "steps"],
        ["create_test"] = ["testId", "name", "stepCount", "path", "warnings"], ["update_test"] = ["testId", "name", "stepCount", "path", "warnings"], ["delete_test"] = ["testId", "deleted"],
        ["validate_test"] = ["valid", "problems", "warnings"], ["list_apps"] = ["apps", "count"], ["launch_app"] = ["pid", "title"], ["close_app"] = ["pid", "closed", "forced"],
        ["activate_app"] = ["pid", "minimized", "foreground"], ["inspect_app"] = ["pid", "elements", "treeTruncated"], ["screenshot_app"] = ["pid", "width", "height", "scale"],
        ["perform_step"] = ["status", "passed", "message"], ["run_test"] = ["runId", "status", "steps", "running"], ["cancel_run"] = ["runId", "status", "steps", "running"],
        ["list_runs"] = ["runs", "total"], ["get_run"] = ["runId", "status", "steps", "running"], ["get_run_screenshot"] = ["runId", "stepNumber", "width", "height"]
    };

    private static async Task BackwardCompatibility()
    {
        await using var harness = new Harness("compatibility");
        await harness.InitializeAsync();
        var listed = Result(await harness.Client.RequestAsync("tools/list")).GetProperty("tools").EnumerateArray().ToList();
        var tools = listed.ToDictionary(t => t.GetProperty("name").GetString()!, t => t, StringComparer.Ordinal);
        // In this Cortex build get_run_report (added by Cortex) sits between get_run and get_run_screenshot, and the Cortex tools follow find_app.
        var order = listed.Select(t => t.GetProperty("name").GetString()!).ToList();
        Check(EarlierInputs.Keys.All(tools.ContainsKey) && order.Where(EarlierInputs.ContainsKey).SequenceEqual(EarlierInputs.Keys) && order.IndexOf("find_app") > order.FindLastIndex(EarlierInputs.ContainsKey)
            && order.SequenceEqual(ExpectedTools), "Every earlier tool is still listed, in its earlier order, before find_app.");
        foreach (var (tool, arguments) in EarlierInputs)
        {
            Check(tools.ContainsKey(tool), $"{tool} is still offered.");
            var input = tools[tool].GetProperty("inputSchema");
            var properties = input.GetProperty("properties");
            foreach (var argument in arguments)
                Check(properties.TryGetProperty(argument, out _), $"{tool} still accepts {argument}.");
            var required = input.TryGetProperty("required", out var list) ? list.EnumerateArray().Select(r => r.GetString()!).ToHashSet(StringComparer.Ordinal) : [];
            var earlier = EarlierRequiredInputs.TryGetValue(tool, out var before) ? before : [];
            Check(required.All(earlier.Contains), $"{tool} must not require anything it did not require before: {string.Join(", ", required.Except(earlier))}.");
            Check(input.GetProperty("additionalProperties").ValueKind == JsonValueKind.False, $"{tool} still names its unknown arguments.");
        }
        foreach (var (key, (minimum, maximum, fallback)) in EarlierRanges)
        {
            var parts = key.Split('.');
            var schema = tools[parts[0]].GetProperty("inputSchema").GetProperty("properties").GetProperty(parts[1]);
            Check(schema.GetProperty("type").GetString() == "integer" && schema.GetProperty("default").GetInt32() == fallback, $"{key} keeps its type and default {fallback}.");
            Check(schema.GetProperty("minimum").GetInt32() <= minimum && schema.GetProperty("maximum").GetInt32() >= maximum, $"{key} still accepts {minimum}–{maximum}.");
        }
        foreach (var (tool, fields) in EarlierResults)
        {
            var declared = tools[tool].GetProperty("outputSchema").GetProperty("properties");
            foreach (var field in fields) Check(declared.TryGetProperty(field, out _), $"{tool} still returns {field}.");
        }
        foreach (var (tool, fields) in EarlierRequiredResults)
        {
            var required = tools[tool].GetProperty("outputSchema").GetProperty("required").EnumerateArray().Select(r => r.GetString()!).ToList();
            Check(fields.All(required.Contains), $"{tool} still always returns {string.Join(", ", fields)}.");
        }
        // Earlier argument names and the previous resource URIs and prompt still work.
        Check(McpResources.WorkspaceUri == "testy://workspace" && McpResources.DocsUri == "testy://docs/mcp" && McpResources.TestTemplate == "testy://tests/{id}" && McpResources.RunTemplate == "testy://runs/{id}", "Resource URIs are unchanged.");
        Check(McpResources.Prompts()[0]!["name"]!.GetValue<string>() == "write_test", "The write_test prompt is still offered.");
        var created = (await harness.Client.CallToolAsync("create_test", new { name = "Old shape", intent = "Arguments as before.", targetPath = @"C:\Apps\Desk\Desk.exe", steps = CustomerSteps("Ada") })).RequireOk("create_test").Structured;
        var id = created.GetProperty("testId").GetString()!;
        Check(created.GetProperty("targetPath").GetString() == @"C:\Apps\Desk\Desk.exe", "targetPath is stored as before.");
        Check(!(await harness.Client.CallToolAsync("get_test", new { id })).IsError && !(await harness.Client.CallToolAsync("inspect_app", new { pid = Environment.ProcessId, screenshotMaxWidth = 100, includeScreenshot = false })).Text.Contains("Unknown argument", StringComparison.Ordinal),
            "Earlier argument names are still accepted.");
        // The instructions (sent with initialize) state the limits and the one-input-at-a-time rule.
        var instructions = McpResources.Instructions;
        Check(instructions.Contains("45 s", StringComparison.Ordinal) && instructions.Contains("60-second", StringComparison.Ordinal) && instructions.Contains("get_run with waitSeconds", StringComparison.Ordinal), "The instructions state the time limits.");
        Check(instructions.Contains("do not use any other desktop-control tool", StringComparison.Ordinal) && instructions.Contains("fight over the same input", StringComparison.Ordinal), "The instructions forbid other desktop control while Testy works.");
        Check(instructions.Contains("find_app", StringComparison.Ordinal) && instructions.Contains("Customer Desk", StringComparison.Ordinal), "The instructions teach naming the app.");
    }

    private static async Task TimeLimits()
    {
        var instructions = McpResources.Instructions;
        Check(TestyMcpService.DefaultRunWaitSeconds < 60 && TestyMcpService.MaximumGetRunWaitSeconds < 60, "Default waits stay below a 60-second client limit.");
        await using var ranges = new Harness("time-ranges");
        await ranges.InitializeAsync();
        var saved = (await ranges.Client.CallToolAsync("create_test", new { name = "Ranges", intent = "x", steps = CustomerSteps("Ada") })).RequireOk("create_test").Structured.GetProperty("testId").GetString();
        var tooLong = await ranges.Client.CallToolAsync("run_test", new { testId = saved, pid = 4242, waitSeconds = 901 });
        Check(tooLong.IsError && tooLong.Text.Contains("between 0 and 900", StringComparison.Ordinal), "run_test waitSeconds is 0–900: " + tooLong.Text);
        var pollTooLong = await ranges.Client.CallToolAsync("get_run", new { runId = "x", waitSeconds = 46 });
        Check(pollTooLong.IsError && pollTooLong.Text.Contains("between 0 and 45", StringComparison.Ordinal), "get_run waitSeconds is 0–45: " + pollTooLong.Text);
        if (!DesktopAvailable) return; // runs need an unlocked interactive session
        var (harness, testId) = await RunHarnessAsync("time-limits", TimeSpan.FromSeconds(1));
        await using var owned = harness;
        var schema = Result(await harness.Client.RequestAsync("tools/list")).GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "run_test").GetProperty("outputSchema");
        var watch = Stopwatch.StartNew();
        var handed = (await harness.Client.CallToolAsync("run_test", new { testId, pid = 4242, waitSeconds = 0 })).RequireOk("run_test").Structured;
        Check(watch.Elapsed < TimeSpan.FromSeconds(3) && handed.GetProperty("running").GetBoolean() && handed.GetProperty("status").GetString() == "running" && handed.GetProperty("completedSteps").GetInt32() < 3,
            "waitSeconds 0 returns status running at once with the steps completed so far: " + handed.GetRawText());
        var message = handed.GetProperty("message").GetString()!;
        Check(message.Contains("get_run", StringComparison.Ordinal) && message.Contains("waitSeconds", StringComparison.Ordinal) && message.Contains("cancel_run", StringComparison.Ordinal), "The answer says to call get_run with waitSeconds: " + message);
        Check(McpSchemaValidator.FirstViolation(schema, handed, "run_test", declaredTopLevelOnly: true) is null, "The handed-over result satisfies run_test's output schema.");
        var runId = handed.GetProperty("runId").GetString()!;
        watch.Restart();
        var shortPoll = (await harness.Client.CallToolAsync("get_run", new { runId, waitSeconds = 1 })).RequireOk("get_run").Structured;
        Check(watch.Elapsed >= TimeSpan.FromSeconds(0.9) && watch.Elapsed < TimeSpan.FromSeconds(2.5) && shortPoll.GetProperty("running").GetBoolean() && shortPoll.GetProperty("message").GetString()!.Contains("after waiting 1 s", StringComparison.Ordinal),
            $"get_run waits no longer than asked while the run goes on ({watch.Elapsed.TotalSeconds:F1} s): " + shortPoll.GetRawText());
        watch.Restart();
        var done = (await harness.Client.CallToolAsync("get_run", new { runId, waitSeconds = 30 })).RequireOk("get_run").Structured;
        Check(watch.Elapsed < TimeSpan.FromSeconds(6) && !done.GetProperty("running").GetBoolean() && done.GetProperty("status").GetString() == "passed" && done.GetProperty("completedSteps").GetInt32() == 3 && !done.TryGetProperty("message", out _),
            $"get_run with waitSeconds returns as soon as the run ends ({watch.Elapsed.TotalSeconds:F1} s): " + done.GetRawText());
        watch.Restart();
        var immediate = (await harness.Client.CallToolAsync("get_run", new { runId, waitSeconds = 30 })).RequireOk("get_run").Structured;
        Check(watch.Elapsed < TimeSpan.FromSeconds(2) && immediate.GetProperty("status").GetString() == "passed", "A finished run is answered at once whatever waitSeconds says.");
        // The run stays cancellable after its call answered: cancel_run takes the same path as notifications/cancelled.
        var again = (await harness.Client.CallToolAsync("run_test", new { testId, pid = 4242, waitSeconds = 0 })).RequireOk("run_test").Structured.GetProperty("runId").GetString()!;
        var cancelled = (await harness.Client.CallToolAsync("cancel_run", new { runId = again })).RequireOk("cancel_run").Structured;
        Check(cancelled.GetProperty("status").GetString() == "cancelled" && !cancelled.GetProperty("running").GetBoolean(), "A handed-over run is stopped by cancel_run.");
        Check(instructions.Contains("cancel_run", StringComparison.Ordinal), "The instructions name cancel_run.");
    }
}
