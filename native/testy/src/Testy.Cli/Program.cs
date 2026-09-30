using System.Text.Json;
using Testy.Cli.Mcp;
using Testy.Core;
using Testy.Windows;

namespace Testy.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            {
                Console.WriteLine("""
                    Testy Windows UI testing
                      list-targets
                      inspect --pid PID [--probe]
                      project-config --project FILE --workspace DIRECTORY
                      check-provider --settings FILE [--artifacts DIRECTORY]
                      project-tool --project FILE --request FILE [--artifacts DIRECTORY]
                      draft-test --pid PID --settings FILE --instructions FILE --output FILE [--project FILE] [--acceptance FILE] [--probe] [--artifacts DIRECTORY]
                      run --test FILE --pid PID [--probe] [--artifacts DIRECTORY]
                      run-ai --test FILE --pid PID --settings FILE [--project FILE] [--probe] [--artifacts DIRECTORY]
                      run-suite --suite FILE --pid PID (--settings FILE | --replay) [--project FILE] [--probe] [--artifacts DIRECTORY]
                      worker-run --exe EXE --test FILE (--settings FILE | --replay) [--project FILE] [--probe] [--timeout-seconds 300] [--artifacts DIRECTORY]
                      lifecycle-run --profile FILE [--artifacts DIRECTORY]
                      lifecycle-inspect --directory DIRECTORY
                      operations --operation ACTION --workspace DIRECTORY [--profile FILE] [--id ID] [--once] [--seconds 60]
                      operations --operation schedule-add --workspace DIRECTORY --profile FILE --interval-seconds SECONDS
                      operations --operation enqueue-test --workspace DIRECTORY --test FILE (--settings FILE | --replay) --exe EXE [--target local|vm:GUID] [--probe] [--stage-directory DIR]
                      operations --operation pump --workspace DIRECTORY [--target local|vm:GUID] [--once] [--seconds 60]
                      vms --operation list [--deep] | check --vm GUID | setup --vm GUID | set-credential --vm GUID --username USER (password on stdin) | forget --vm GUID
                      vm-run --vm GUID --exe GUEST_EXE --test FILE (--settings FILE | --replay) [--probe] [--stage-directory DIR] [--timeout-seconds 900] [--artifacts DIRECTORY]
                      operations --operation backup --workspace DIRECTORY --archive FILE
                      operations --operation restore --archive FILE --destination EMPTY_DIRECTORY
                      operations --operation migrate --workspace DIRECTORY
                      benchmark-export --source DIRECTORY --output FILE [--format json|csv]
                      record --pid PID --output FILE [--duration-seconds 30]
                      draft-from-demo --demo FILE --acceptance FILE --instructions FILE --settings FILE --output FILE [--project FILE] [--artifacts DIRECTORY]
                      materialize-template --template FILE --data FILE --output FILE
                      tool --pid PID --request JSON_FILE [--probe] [--artifacts DIRECTORY]
                      mcp [--workspace DIRECTORY] [--transport stdio|http] [--port N] [--token TOKEN] [--parent-pid PID] [--no-launch] [--allow-exe PATH]... [--allow-target NAME]...
                      mcp --describe [--format manifest|server-json] [--relative]
                      mcp --help
                      verify-mcp [--lab TESTY_TESTLAB_EXE] [--artifacts DIRECTORY]
                      verify-lab --exe TESTY_TESTLAB_EXE [--artifacts DIRECTORY]
                      verify-functionality --orders TESTY_ORDERLAB_EXE --lab TESTY_TESTLAB_EXE [--repetitions 3] [--artifacts DIRECTORY]
                      verify-wpf --exe TESTY_WPFLAB_EXE [--repetitions 3] [--artifacts DIRECTORY]
                      verify-wpf-hybrid --exe TESTY_WPFLAB_EXE [--artifacts DIRECTORY]
                      verify-grid-editors --exe TESTY_WPFLAB_EXE [--artifacts DIRECTORY]
                      verify-surfaces --exe TESTY_WPFLAB_EXE [--artifacts DIRECTORY]
                      verify-text-boundaries --exe TESTY_WPFLAB_EXE [--artifacts DIRECTORY]
                      verify-enterprise --exe TESTY_WPFLAB_EXE [--repetitions 3] [--artifacts DIRECTORY]
                      verify-worker --exe TESTY_TESTLAB_EXE [--artifacts DIRECTORY]
                      verify-maintenance [--artifacts DIRECTORY]
                      verify-project [--artifacts DIRECTORY]
                      verify-popup --exe TESTY_STUDIO_EXE [--artifacts DIRECTORY]
                      verify-native-protocol --exe TESTY_TESTLAB_EXE [--artifacts DIRECTORY]
                      verify-studio --exe TESTY_STUDIO_EXE --lab TESTY_TESTLAB_EXE [--settings LIVE_PROVIDER_FILE] [--artifacts DIRECTORY]
                      verify-workflows --exe TESTY_STUDIO_EXE --lab TESTY_TESTLAB_EXE [--artifacts DIRECTORY]
                      verify-agent-ui --exe TESTY_STUDIO_EXE [--workspace DIRECTORY] [--artifacts DIRECTORY]
                      verify-studio-regressions --exe TESTY_STUDIO_EXE [--lab TESTY_TESTLAB_EXE --settings LIVE_PROVIDER_FILE] [--artifacts DIRECTORY]

                    All command results are JSON. Run exits 0 for passed, 1 for failed/cancelled,
                    2 for invalid input or driver errors. Ctrl+C or --cancel-file PATH cancels cooperatively.
                    operations actions: enqueue/list/cancel/rerun/delete-job/reconcile/schedule-add/
                    schedules/schedule-pause/schedule-resume/schedule-delete/pump/backup/restore/migrate.
                    Schedules run while an operations pump or the Testy background agent is active. VM verbs need elevation (Hyper-V).
                    tool request: {"operation":"inspect|execute|screenshot", "step":{...}}
                    tool screenshot files are written only within the artifacts directory.
                    --probe requires the opt-in Testy.WpfProbe endpoint in the selected process.
                    mcp serves the Model Context Protocol for other AI harnesses (stdio by default: stdout carries only
                    protocol messages, logs and errors go to stderr; --transport http listens on loopback; the bearer token
                    is best given in the environment variable TESTY_MCP_TOKEN). See docs/MCP.md.
                    """);
                return 0;
            }

            // The MCP server parses its own options and keeps stdout for protocol messages: its errors, startup ones included, go to stderr.
            if (args[0] == "mcp") return await McpCommand.MainAsync(args[1..], cancellation.Token);
            var options = ParseOptions(args.Skip(1).ToArray());
            using var fileCancellation = options.TryGetValue("cancel-file", out var cancelPath)
                ? new CancellationFileMonitor(Path.GetFullPath(cancelPath ?? throw new ArgumentException("--cancel-file requires a path.")), cancellation) : null;
            var artifacts = Path.GetFullPath(options.GetValueOrDefault("artifacts") ?? Path.Combine(Environment.CurrentDirectory, "artifacts"));
            if (options.ContainsKey("project") && args[0] is not ("project-config" or "project-tool" or "draft-test" or "run-ai" or "run-suite" or "worker-run" or "draft-from-demo"))
                throw new ArgumentException("--project applies only to AI authoring/execution or explicit project configuration/tools.");
            if (args[0] == "verify-mcp")
            {
                var report = await McpVerifier.VerifyAsync(options.GetValueOrDefault("lab"), artifacts, cancellation.Token);
                Write(report); return report.Passed ? 0 : 1;
            }
            if (args[0] == "project-config")
            {
                Write(await ProjectCliCommand.ConfigureAsync(Required(options, "project"), Required(options, "workspace"), cancellation.Token));
                return 0;
            }
            if (args[0] == "check-provider")
            {
                var settings = await ProjectCliCommand.SettingsAsync(Required(options, "settings"), null, cancellation.Token);
                var result = await ProviderConnectionCheck.RunAsync(settings, artifacts, cancellation.Token);
                Write(result); return result.Passed ? 0 : 1;
            }
            if (args[0] == "verify-agent-ui")
            {
                var result = await AgentStudioVerifier.VerifyAsync(Required(options, "exe"), options.GetValueOrDefault("workspace") ?? AgentFiles.DefaultWorkspace, artifacts, cancellation.Token);
                Write(result); return result.Passed ? 0 : 1;
            }
            if (args[0] == "verify-workflows")
            {
                var result = await WorkflowStudioVerifier.VerifyAsync(Required(options, "exe"), Required(options, "lab"), artifacts, cancellation.Token);
                Write(result); return result.Passed ? 0 : 1;
            }
            if (args[0] is "lifecycle-run" or "lifecycle-inspect")
            {
                var result = args[0] == "lifecycle-run"
                    ? await LifecycleCommand.RunAsync(Required(options, "profile"), artifacts, cancellation.Token)
                    : await LifecycleCommand.InspectAsync(Required(options, "directory"), cancellation.Token);
                Write(result); return result.ExitCode;
            }
            if (args[0] == "vms")
            {
                var (vmResult, vmExit) = await HyperV.VmCommand.ExecuteAsync(Required(options, "operation"), options, cancellation.Token);
                Write(vmResult); return vmExit;
            }
            if (args[0] == "vm-run")
            {
                var vmRun = await HyperV.VmCommand.RunAsync(options, artifacts, cancellation.Token);
                Write(vmRun); return vmRun.ExitCode;
            }
            if (args[0] == "operations")
            {
                var result = await OperationsCommand.ExecuteAsync(Required(options, "operation"), options.ToDictionary(x => x.Key, x => x.Value ?? "true"), cancellation.Token);
                Write(result); return OperationsCommand.ExitCode(result);
            }
            if (args[0] is "verify-grid-editors" or "verify-surfaces")
            {
                var result = args[0] == "verify-grid-editors"
                    ? await GridEditorsVerifier.VerifyAsync(Required(options, "exe"), artifacts, cancellation.Token)
                    : await SurfaceCaptureVerifier.VerifyAsync(Required(options, "exe"), artifacts, cancellation.Token);
                Write(result); return result.Passed ? 0 : 1;
            }
            if (args[0] == "project-tool")
            {
                var result = await ProjectCliCommand.ToolAsync(Required(options, "project"), Required(options, "request"), artifacts, cancellation.Token);
                Write(result); return result.Status == ProjectToolStatus.Succeeded ? 0 : 1;
            }
            if (args[0] == "worker-run")
            {
                var result = await WorkerCommand.RunAsync(new WorkerOptions
                {
                    Executable = Required(options, "exe"), TestFile = Required(options, "test"), SettingsFile = options.GetValueOrDefault("settings"), ProjectFile = options.GetValueOrDefault("project"),
                    Replay = options.ContainsKey("replay"), Probe = options.ContainsKey("probe"), ArtifactsRoot = artifacts,
                    TimeoutSeconds = IntegerOption(options, "timeout-seconds", 300), StartupTimeoutSeconds = IntegerOption(options, "startup-timeout-seconds", 20),
                    ShutdownGraceSeconds = IntegerOption(options, "shutdown-grace-seconds", 3), TargetArgumentsFile = options.GetValueOrDefault("target-arguments")
                }, cancellation.Token);
                Write(result); return result.ExitCode;
            }
            if (args[0] == "benchmark-export")
            {
                Write(await BenchmarkCommand.ExportAsync(Required(options, "source"), Required(options, "output"), options.GetValueOrDefault("format") ?? "json", cancellation.Token));
                return 0;
            }
            if (args[0] == "materialize-template")
            {
                Write(await MaintenanceCommand.MaterializeAsync(Required(options, "template"), Required(options, "data"), Required(options, "output"), cancellation.Token));
                return 0;
            }
            if (args[0] == "draft-from-demo")
            {
                Write(await MaintenanceCommand.DraftAsync(Required(options, "demo"), Required(options, "acceptance"), Required(options, "settings"), Required(options, "instructions"), Required(options, "output"), artifacts, cancellation.Token, options.GetValueOrDefault("project")));
                return 0;
            }
            if (args[0] == "record")
            {
                if (!int.TryParse(Required(options, "pid"), out int recordingPid) || recordingPid <= 0) throw new ArgumentException("--pid must be a positive process ID.");
                var recording = await RecordingCommand.RecordAsync(recordingPid, Required(options, "output"), IntegerOption(options, "duration-seconds", 30), cancellation.Token);
                Write(recording); return recording.Completed ? 0 : 1;
            }
            if (args[0] == "verify-popup")
            {
                var report = await PopupVerifier.VerifyAsync(Required(options, "exe"), artifacts, cancellation.Token); Write(report);
                return report.Passed ? 0 : 1;
            }
            if (args[0] == "verify-project")
            {
                var report = await ProjectWorkerChecks.VerifyAsync(artifacts, cancellation.Token); Write(report);
                return report.Passed ? 0 : 1;
            }
            if (args[0] == "verify-maintenance")
            {
                var report = MaintenanceVerifier.Verify(artifacts); Write(report);
                return JsonSerializer.SerializeToElement(report, TestyJson.Options).GetProperty("passed").GetBoolean() ? 0 : 1;
            }
            if (args[0] == "verify-enterprise")
            {
                var report = await EnterpriseWpfVerifier.VerifyAsync(Required(options, "exe"), artifacts, IntegerOption(options, "repetitions", 3), cancellation.Token);
                Write(report); return report.Passed ? 0 : 1;
            }
            if (args[0] == "verify-worker")
            {
                var report = await WorkerVerifier.VerifyAsync(Required(options, "exe"), artifacts, cancellation.Token);
                Write(report); return report.Passed ? 0 : 1;
            }
            if (args[0] == "verify-text-boundaries")
            {
                var report = await TextBoundaryVerifier.VerifyAsync(Required(options, "exe"), artifacts, cancellation.Token);
                Write(report);
                return report.Passed ? 0 : 1;
            }
            if (args[0] == "verify-wpf-hybrid")
            {
                var report = await HybridWpfProtocolVerifier.VerifyAsync(Required(options, "exe"), artifacts, cancellation.Token);
                Write(report);
                return report.Passed ? 0 : 1;
            }
            if (args[0] == "verify-wpf")
            {
                var repetitions = 3;
                if (options.ContainsKey("repetitions") && (!int.TryParse(options["repetitions"], out repetitions) || repetitions is < 1 or > 20))
                    throw new ArgumentException("--repetitions must be 1–20.");
                var report = await AdvancedWpfVerifier.VerifyAsync(Required(options, "exe"), artifacts, repetitions, cancellation.Token);
                Write(report);
                return report.Passed ? 0 : 1;
            }
            if (args[0] == "verify-functionality")
            {
                var repetitions = 3;
                if (options.ContainsKey("repetitions") && (!int.TryParse(options["repetitions"], out repetitions) || repetitions is < 1 or > 20))
                    throw new ArgumentException("--repetitions must be 1–20.");
                var report = await FunctionalityVerifier.VerifyAsync(Required(options, "orders"), Required(options, "lab"), artifacts, repetitions, cancellation.Token);
                Write(report);
                return report.Passed ? 0 : 1;
            }
            if (args[0] == "verify-lab")
            {
                var report = await LabVerifier.VerifyAsync(Required(options, "exe"), artifacts, cancellation.Token);
                Write(report);
                return report.Passed ? 0 : 1;
            }
            if (args[0] == "verify-studio")
            {
                ProviderSettings? liveSettings = null;
                if (options.GetValueOrDefault("settings") is { Length: > 0 } settingsFile)
                    liveSettings = JsonSerializer.Deserialize<ProviderSettings>(await File.ReadAllTextAsync(settingsFile, cancellation.Token), TestyJson.Options)
                        ?? throw new InvalidDataException("The live verification settings file is empty or null.");
                var report = await StudioVerifier.VerifyAsync(Required(options, "exe"), Required(options, "lab"), artifacts, cancellation.Token, liveSettings);
                Write(report);
                return report.Passed ? 0 : 1;
            }
            if (args[0] == "verify-studio-regressions")
            {
                ProviderSettings? liveSettings = null;
                if (options.GetValueOrDefault("settings") is { Length: > 0 } settingsFile)
                    liveSettings = JsonSerializer.Deserialize<ProviderSettings>(await File.ReadAllTextAsync(settingsFile, cancellation.Token), TestyJson.Options)
                        ?? throw new InvalidDataException("The live verification settings file is empty or null.");
                var report = await StudioVerifier.VerifyRegressionsAsync(Required(options, "exe"), artifacts, cancellation.Token, liveSettings,
                    liveSettings is null ? null : Required(options, "lab"));
                Write(report);
                return report.Passed ? 0 : 1;
            }
            if (args[0] == "verify-native-protocol")
            {
                var report = await NativeProtocolVerifier.VerifyAsync(Required(options, "exe"), artifacts, cancellation.Token);
                Write(report);
                return report.Passed ? 0 : 1;
            }

            if (args[0] is not ("list-targets" or "run-suite" or "inspect" or "run" or "run-ai" or "tool" or "draft-test")) throw new ArgumentException($"Unknown command: {args[0]}. Use --help.");
            if ((options.ContainsKey("worker-cancel-file") || options.ContainsKey("worker-start-file")) && args[0] is not ("run" or "run-ai"))
                throw new ArgumentException("Worker coordination signals apply only to child run/run-ai commands.");
            using var workerMonitor = WorkerCommand.MonitorCancellation(options.GetValueOrDefault("worker-cancel-file"), cancellation);
            await WorkerCommand.WaitForStartAsync(options.GetValueOrDefault("worker-start-file"), cancellation.Token);
            using ITargetDriver driver = options.ContainsKey("probe") ? new WpfProbeDriver() : new UiAutomationDriver();
            if (args[0] == "list-targets")
            {
                Write(await driver.GetTargetsAsync(cancellation.Token));
                return 0;
            }
            if (!int.TryParse(Required(options, "pid"), out var pid) || pid <= 0)
                throw new ArgumentException("--pid must be a positive process ID.");
            await driver.AttachAsync(pid, cancellation.Token);
            switch (args[0])
            {
                case "draft-test":
                    Write(await ProjectCliCommand.DraftAsync(driver, Required(options, "settings"), options.GetValueOrDefault("project"), Required(options, "instructions"), options.GetValueOrDefault("acceptance"), Required(options, "output"), artifacts, cancellation.Token));
                    return 0;
                case "run-suite":
                {
                    var result = await SuiteCommand.RunAsync(Required(options, "suite"), options.GetValueOrDefault("settings"), options.ContainsKey("replay"), driver, artifacts, cancellation.Token, options.GetValueOrDefault("project"));
                    Write(result);
                    return result.Status == RunStatus.Passed ? 0 : 1;
                }
                case "inspect":
                    Write(await driver.SnapshotAsync(cancellation.Token));
                    return 0;
                case "run":
                case "run-ai":
                {
                    var test = JsonSerializer.Deserialize<TestCase>(await File.ReadAllTextAsync(Required(options, "test"), cancellation.Token), TestyJson.Options)
                        ?? throw new InvalidDataException("The test file is empty or null.");
                    TestValidator.Validate(test);
                    RunResult result;
                    if (args[0] == "run-ai")
                    {
                        var settings = await ProjectCliCommand.SettingsAsync(Required(options, "settings"), options.GetValueOrDefault("project"), cancellation.Token);
                        result = await new AiTestRunner(driver, settings, artifacts, new NativeComputerActionExecutor(driver)).RunAsync(test, null, cancellation.Token);
                    }
                    else result = await new TestRunner(driver, artifacts).RunAsync(test, null, cancellation.Token);
                    Write(result);
                    return result.Status == RunStatus.Passed ? 0 : 1;
                }
                case "tool":
                {
                    var request = JsonSerializer.Deserialize<ToolRequest>(await File.ReadAllTextAsync(Required(options, "request"), cancellation.Token), TestyJson.Options)
                        ?? throw new InvalidDataException("The tool request is empty or null.");
                    switch (request.Operation)
                    {
                        case "inspect":
                            Write(await driver.SnapshotAsync(cancellation.Token));
                            return 0;
                        case "screenshot":
                            Directory.CreateDirectory(artifacts);
                            var image = await driver.CaptureAsync(Path.Combine(artifacts, $"capture-{Guid.NewGuid():N}.png"), cancellation.Token);
                            Write(new { screenshotPath = image, snapshot = await driver.SnapshotAsync(cancellation.Token) });
                            return 0;
                        case "execute":
                            if (request.Step is null) throw new InvalidDataException("execute requires a step.");
                            var test = new TestCase { Name = "Local computer tool", Steps = [request.Step] };
                            TestValidator.Validate(test);
                            var result = await new TestRunner(driver, artifacts).RunAsync(test, null, cancellation.Token);
                            Write(result);
                            return result.Status == RunStatus.Passed ? 0 : 1;
                        default:
                            throw new InvalidDataException("operation must be inspect, execute or screenshot.");
                    }
                }
                default: throw new ArgumentException($"Unknown command: {args[0]}. Use --help.");
            }
        }
        catch (OperationCanceledException)
        {
            Write(new { error = "Cancelled", status = "cancelled" });
            return 1;
        }
        catch (Exception exception)
        {
            Write(new { error = exception.Message, type = exception.GetType().Name });
            return 2;
        }
    }

    internal static void Write<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, TestyJson.Options));
    private static string Required(Dictionary<string, string?> options, string key) =>
        options.GetValueOrDefault(key) is { Length: > 0 } value ? value : throw new ArgumentException($"Missing --{key}.");
    private static int IntegerOption(Dictionary<string, string?> options, string key, int fallback) => !options.ContainsKey(key) ? fallback :
        int.TryParse(Required(options, key), out int value) ? value : throw new ArgumentException($"--{key} must be an integer.");

    private static Dictionary<string, string?> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unexpected argument: {args[i]}");
            var name = args[i][2..];
            if (name is not ("pid" or "probe" or "artifacts" or "exe" or "lab" or "test" or "request" or "settings" or "suite" or "replay" or "orders" or "repetitions"
                or "timeout-seconds" or "startup-timeout-seconds" or "shutdown-grace-seconds" or "target-arguments" or "worker-cancel-file" or "worker-start-file"
                or "source" or "output" or "format" or "demo" or "acceptance" or "instructions" or "template" or "data" or "duration-seconds" or "project" or "workspace"
                or "profile" or "directory" or "operation" or "id" or "once" or "seconds" or "archive" or "destination" or "interval-seconds" or "first-run" or "name" or "cancel-file"
                or "target" or "vm" or "deep" or "username" or "stage-directory")) throw new ArgumentException($"Unknown option --{name}.");
            if (result.ContainsKey(name)) throw new ArgumentException($"Duplicate option --{name}.");
            if (name is "probe" or "replay" or "once" or "deep") { result.Add(name, null); continue; }
            if (++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"--{name} requires a value.");
            result.Add(name, args[i]);
        }
        return result;
    }

    private sealed class ToolRequest
    {
        public string Operation { get; set; } = "inspect";
        public TestStep? Step { get; set; }
    }
}
