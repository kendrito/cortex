using System.Collections.Concurrent;
using System.Text.Json;
using Testy.Core;
using Testy.Windows;
using Testy.Cli.HyperV;

namespace Testy.Cli.Mcp;

internal sealed partial class TestyMcpService
{
    private sealed class WorkflowTask(string operation, CancellationToken parent)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Operation { get; } = operation;
        public string? ContextId { get; } = CortexModelBridge.Context;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedAt { get; set; }
        public string Status { get; set; } = "running";
        public object? Result { get; set; }
        public string? Error { get; set; }
        public string? Phase { get; set; }
        public string? ProgressMessage { get; set; }
        public object? Target { get; set; }
        public string? RunId { get; set; }
        public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(parent);
        public Task Completion { get; set; } = Task.CompletedTask;
    }
    private sealed class SavedSuite
    {
        public string SuiteId { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Test set";
        public List<string> TestIds { get; set; } = [];
        public int Repetitions { get; set; } = 1;
        public List<TestCase>? MaterializedTests { get; set; }
    }
    private readonly ConcurrentDictionary<string, WorkflowTask> workflowTasks = new();
    private readonly object workflowGate = new();
    private OperationsStore Queue => new(Path.Combine(Workspace, "operations"));
    private static object TaskView(WorkflowTask task)
    {
        lock (task) return new
        {
            taskId = task.Id, operation = task.Operation, contextId = task.ContextId, running = task.FinishedAt is null,
            status = task.Status, result = task.Result, error = task.Error, startedAt = task.StartedAt, finishedAt = task.FinishedAt,
            phase = task.Phase, progressMessage = task.ProgressMessage, target = task.Target, runId = task.RunId
        };
    }
    private McpToolResult StartWorkflow(string operation, Func<CancellationToken, Task<object>> execute) => StartWorkflow(operation, (_, ct) => execute(ct));
    private McpToolResult StartWorkflow(string operation, Func<WorkflowTask, CancellationToken, Task<object>> execute)
    {
        lock (workflowGate)
        {
            foreach (var old in workflowTasks.Values.Where(t => t.FinishedAt is not null).OrderBy(t => t.StartedAt).Take(Math.Max(0, workflowTasks.Count - 95)))
                if (workflowTasks.TryRemove(old.Id, out _)) old.Stop.Dispose();
            if (workflowTasks.Count >= 128) throw new McpToolException("Too many active workflow tasks. Stop an existing task before starting another.");
            var task = new WorkflowTask(operation, shutdown.Token);
            workflowTasks[task.Id] = task;
            task.Completion = Task.Run(async () =>
            {
                object? result = null; string? error = null; string status;
                try { result = await execute(task, task.Stop.Token); status = task.Stop.IsCancellationRequested ? "cancelled" : "completed"; }
                catch (OperationCanceledException) when (task.Stop.IsCancellationRequested) { status = "cancelled"; }
                catch (Exception failure) { status = task.Stop.IsCancellationRequested ? "cancelled" : "failed"; error = status == "failed" ? failure.Message : null; }
                lock (task) { task.Result = result; task.Error = error; task.Status = status; task.FinishedAt = DateTimeOffset.UtcNow; }
            });
            return McpToolResult.Json(TaskView(task));
        }
    }
    private WorkflowTask Workflow(string id) => workflowTasks.TryGetValue(id, out var task) ? task : throw new McpToolException("Task is not retained by this server. Finished run evidence remains in Results.");
    private string WorkflowFile(string group, string id) => Path.Combine(Workspace, "workflows", group, RequireId(id, "id") + ".json");
    private T ReadWorkflow<T>(string group, string id) => MaintenanceCommand.ParseStrict<T>(File.ReadAllText(WorkflowFile(group, id)));
    private IEnumerable<T> ReadWorkflows<T>(string group)
    {
        var path = Path.Combine(Workspace, "workflows", group);
        return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.json").Select(p => MaintenanceCommand.ParseStrict<T>(File.ReadAllText(p))).ToArray() : [];
    }
    private void WriteWorkflow<T>(string group, string id, T value)
    {
        var path = WorkflowFile(group, id); Directory.CreateDirectory(Path.GetDirectoryName(path)!); WorkspaceStore.WriteAtomic(path, value);
    }
    private ProviderSettings WorkflowSettings(ToolArguments arguments)
    {
        var settings = TestyJson.Clone(Store.LoadSettings()); settings.AiDirectedExecution = arguments.Choice("mode", ["ai", "replay"], "ai") == "ai"; return settings;
    }
    private OperationsJobRequest JobRequest(ToolArguments a, string? forcedTarget = null)
    {
        var target = OperationsTargets.Parse(OperationsTargets.Normalize(forcedTarget ?? a.String("target", 100) ?? "local"));
        var settings = WorkflowSettings(a);
        var test = RequireTest(a.RequireString("testId", 100)); TestValidator.Validate(test);
        var exe = a.RequireString("exe"); if (target.IsLocal) exe = Policy.RequireLaunchable(exe);
        return new() { Name = test.Name, Target = target.IsLocal ? null : target.Canonical,
            Test = new() { Test = test, Executable = exe, Probe = a.Bool("probe", false), Provider = settings.AiDirectedExecution ? settings : null,
                TargetArguments = [.. a.StringArray("args",64,4096) ?? []], TimeoutSeconds = a.Int("timeoutSeconds",900,1,7200), StartupTimeoutSeconds = a.Int("startupTimeoutSeconds",30,1,120), ShutdownGraceSeconds = a.Int("shutdownGraceSeconds",3,1,30), StageDirectory = a.String("stageDirectory") } };
    }

    public async Task<McpToolResult> WorkflowAsync(string name, ToolArguments a, McpRequestContext context)
    {
        context.Token.ThrowIfCancellationRequested();
        switch (name)
        {
            case "ai_test": return StartAiTest(a, context);
            case "launch_sample": return await LaunchSampleAsync(a, context);
            case "list_tasks": return McpToolResult.Json(new { tasks = workflowTasks.Values.OrderByDescending(t => t.StartedAt).Select(TaskView).ToArray() });
            case "get_task": return McpToolResult.Json(TaskView(Workflow(a.RequireString("taskId", 100))));
            case "cancel_task":
            {
                var task = Workflow(a.RequireString("taskId", 100)); task.Stop.Cancel();
                try { await task.Completion.WaitAsync(TimeSpan.FromSeconds(15), context.Token); } catch (TimeoutException) { }
                return McpToolResult.Json(TaskView(task));
            }
            case "list_suites": return McpToolResult.Json(new { suites = ReadWorkflows<SavedSuite>("suites") });
            case "save_suite":
            {
                var ids = a.StringArray("testIds", 200, 100) ?? []; if (ids.Length == 0 || ids.Distinct().Count() != ids.Length) throw new McpToolException("Choose 1–200 unique tests.");
                foreach (var id in ids) TestValidator.Validate(RequireTest(id));
                var suite = new SavedSuite { SuiteId = a.String("suiteId", 100) ?? Guid.NewGuid().ToString("N"), Name = a.RequireString("name", 200), TestIds = [.. ids], Repetitions = a.Int("repetitions", 1, 1, 100) };
                if (ids.Length * suite.Repetitions > 1000) throw new McpToolException("A suite supports at most 1000 planned runs.");
                WriteWorkflow("suites", suite.SuiteId, suite); return McpToolResult.Json(suite);
            }
            case "delete_suite": File.Delete(WorkflowFile("suites", a.RequireString("suiteId", 100))); return McpToolResult.Json(new { completed = true });
            case "run_suite":
            {
                var saved = ReadWorkflow<SavedSuite>("suites", a.RequireString("suiteId", 100));
                var suite = new TestSuite { Name = saved.Name, Repetitions = saved.Repetitions, Tests = saved.MaterializedTests ?? saved.TestIds.Select(RequireTest).ToList() };
                var pid = a.RequireInt("pid", 1); Policy.RequireTarget(pid); var settings = WorkflowSettings(a); var probe = a.Bool("probe", false);
                return StartWorkflow(name, async ct =>
                {
                    using var slot = await AcquireDesktopAsync(ct, sendsInput: true); using var driver = await AttachAsync(pid, probe, ct);
                    return await new SuiteRunner(async (test, directory, token) =>
                    { var run = await new TestExecutionService(driver, settings, directory, new NativeComputerActionExecutor(driver)).RunAsync(test, null, token); Store.SaveRun(run); return run; },
                        Path.Combine(Workspace, "artifacts"), settings.AiDirectedExecution ? "ai" : "replay").RunAsync(suite, ct);
                });
            }
            case "draft_test": case "refine_test":
                return StartAiTest(a,context,refine:name=="refine_test",forceDraftOnly:true,operation:name);
            case "explain_run":
            {
                var run = RequireRun(a.RequireString("runId", 100)); var settings = TestyJson.Clone(Store.LoadSettings());
                return StartWorkflow(name, async ct => new { runId = run.Id, explanation = await PlannerFactory.Create(settings, Workspace).ExplainAsync(run, ct), verdict = run.Status });
            }
            case "get_project": return McpToolResult.Json(new { project = Store.LoadSettings().ProjectTools, modelManagedByCortex = CortexModelBridge.Enabled });
            case "get_preferences": return McpToolResult.Json(Preferences(Store.LoadSettings()));
            case "save_preferences":
            {
                var settings = Store.LoadSettings();
                if (a.Has("supportsImages")) settings.SupportsImages = a.Bool("supportsImages", true);
                if (a.Has("liveReview")) settings.LiveReview = a.Bool("liveReview", false);
                if (a.Has("aiDirectedExecution")) settings.AiDirectedExecution = a.Bool("aiDirectedExecution", true);
                if (a.Has("maximumAgentTurns")) settings.MaximumAgentTurns = a.Int("maximumAgentTurns",30,1,240);
                if (a.Has("maximumProviderRetries")) settings.MaximumProviderRetries = a.Int("maximumProviderRetries",2,0,3);
                if (a.Has("providerRetryDelayMs")) settings.ProviderRetryDelayMs = a.Int("providerRetryDelayMs",500,0,5000);
                Store.SaveSettings(settings); return McpToolResult.Json(Preferences(settings));
            }
            case "save_project": Store.ConfigureProjectTools(MaintenanceCommand.ParseStrict<ProjectToolSettings>(a.RequireElement("project").GetRawText())); return McpToolResult.Json(new { project = Store.LoadSettings().ProjectTools });
            case "list_jobs": return McpToolResult.Json(new { jobs = Queue.ListJobs() });
            case "queue_test": return McpToolResult.Json(Queue.Enqueue(JobRequest(a)));
            case "queue_machine": return McpToolResult.Json(Queue.Enqueue(JobRequest(a, "vm:" + a.RequireString("machineId", 100))));
            case "cancel_job": return McpToolResult.Json(Queue.RequestCancellation(a.RequireString("jobId", 100)));
            case "rerun_job": return McpToolResult.Json(Queue.Rerun(a.RequireString("jobId", 100)));
            case "delete_job": Queue.DeleteTerminalJob(a.RequireString("jobId", 100)); return McpToolResult.Json(new { completed = true });
            case "pump_jobs":
            {
                var seconds = a.Int("seconds", 60, 1, 86400); var target = a.String("target", 100) ?? "local";
                return StartWorkflow(name, ct => OperationsCommand.ExecuteAsync("pump", new Dictionary<string, string> { ["workspace"] = Workspace, ["seconds"] = seconds.ToString(), ["target"] = target }, ct));
            }
            case "list_schedules": return McpToolResult.Json(new { schedules = Queue.ListSchedules() });
            case "save_schedule":
                if (a.String("scheduleId", 100) is { } scheduleId) return McpToolResult.Json(Queue.SetScheduleEnabled(scheduleId, a.Bool("enabled", true)));
                return McpToolResult.Json(Queue.AddSchedule(new() { Name = a.String("name", 200) ?? "Scheduled test", Request = JobRequest(a), Enabled = a.Bool("enabled", true), IntervalSeconds = a.Int("intervalSeconds", 3600, 60, 2592000) }));
            case "delete_schedule": Queue.DeleteSchedule(a.RequireString("scheduleId", 100)); return McpToolResult.Json(new { completed = true });
            case "record_demo":
            {
                var pid = a.RequireInt("pid", 1); Policy.RequireTarget(pid); var duration = a.Int("durationSeconds", 30, 1, 600);
                return StartWorkflow(name, async ct =>
                {
                    using var slot = await AcquireDesktopAsync(ct, sendsInput: false);
                    var id = Guid.NewGuid().ToString("N"); var file = WorkflowFile("recordings", id); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    var demo = await RecordingCommand.RecordAsync(pid, file, duration, ct); return new { recordingId = id, demonstration = demo };
                });
            }
            case "list_recordings":
            {
                var directory = Path.Combine(Workspace, "workflows", "recordings");
                return McpToolResult.Json(new { recordings = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.json").Select(path => new { recordingId = Path.GetFileNameWithoutExtension(path), recordedAt = File.GetLastWriteTimeUtc(path) }).ToArray() : [] });
            }
            case "draft_from_demo":
            {
                var demo = WorkflowFile("recordings", a.RequireString("recordingId", 100)); var steps = ParseSteps(a.RequireElement("acceptance"), null); var instructions = a.RequireString("instructions", 16000); var settings = TestyJson.Clone(Store.LoadSettings());
                return StartWorkflow(name, async ct =>
                {
                    var dir = Path.Combine(SessionDirectory, "drafts", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
                    var acceptance = Path.Combine(dir, "acceptance.json"); var provider = Path.Combine(dir, "settings.json"); var instructionFile = Path.Combine(dir, "instructions.txt"); var output = Path.Combine(dir, "draft.json");
                    WorkspaceStore.WriteAtomic(acceptance, new TestCase { Name = "Expected assertions", Steps = steps }); WorkspaceStore.WriteAtomic(provider, settings); await File.WriteAllTextAsync(instructionFile, instructions, ct);
                    await MaintenanceCommand.DraftAsync(demo, acceptance, provider, instructionFile, output, dir, ct);
                    return new { draft = TestView(MaintenanceCommand.ParseStrict<TestCase>(await File.ReadAllTextAsync(output, ct))), saved = false };
                });
            }
            case "materialize_template":
            {
                var template = MaintenanceCommand.ParseStrict<TestTemplate>(a.RequireElement("template").GetRawText()); var rows = TestDataCsv.Parse(a.RequireString("csv", 1000000));
                var data = new TestDataSet { Rows = rows.Select((row, index) => new TestDataRow { Id = "row-" + (index + 1), Name = "Row " + (index + 1), Values = new(row) }).ToList() };
                var materialized = MaintenanceCommand.Materialize(template, data); var suite = new SavedSuite { Name = a.RequireString("name", 200), MaterializedTests = materialized.Tests };
                WriteWorkflow("suites", suite.SuiteId, suite); return McpToolResult.Json(suite);
            }
            case "list_lifecycles": return McpToolResult.Json(new { lifecycles = ReadWorkflows<LifecycleRequest>("lifecycles") });
            case "save_lifecycle":
            {
                var test = RequireTest(a.RequireString("testId", 100)); var settings = WorkflowSettings(a); var id = a.String("lifecycleId", 100) ?? Guid.NewGuid().ToString("N");
                var exe = a.RequireString("exe");
                if (!Path.IsPathFullyQualified(exe) || exe.StartsWith(@"\\") || !Path.GetExtension(exe).Equals(".exe", StringComparison.OrdinalIgnoreCase)) throw new McpToolException("The target after preparation must be an absolute local .exe path.");
                var request = new LifecycleRequest { Test = test, Provider = settings, TargetArguments = [.. a.StringArray("args",64,4096) ?? []], Profile = new() { Id = id, Name = a.RequireString("name", 200), TestFile = TestPath(test.Id), SettingsFile = Path.Combine(Workspace, "settings.json"), Executable = Path.GetFullPath(exe), Probe = a.Bool("probe", false), Replay = !settings.AiDirectedExecution, PreparationInstructions = a.RequireString("instructions", 16000),
                    MaximumPreparationTurns = a.Int("maximumPreparationTurns",20,2,80), PreparationTimeoutSeconds = a.Int("preparationTimeoutSeconds",600,1,7200), WorkerTimeoutSeconds = a.Int("workerTimeoutSeconds",900,1,7200), StartupTimeoutSeconds = a.Int("startupTimeoutSeconds",20,1,120), ShutdownGraceSeconds = a.Int("shutdownGraceSeconds",3,1,30) } };
                LifecycleValidation.ValidateRequest(request); WriteWorkflow("lifecycles", id, request); return McpToolResult.Json(request);
            }
            case "run_lifecycle":
            {
                var request = ReadWorkflow<LifecycleRequest>("lifecycles", a.RequireString("lifecycleId", 100));
                return StartWorkflow(name, async ct => await LifecycleCommand.RunAsync(request, Path.Combine(Workspace, "artifacts"), ct));
            }
            case "delete_lifecycle": File.Delete(WorkflowFile("lifecycles", a.RequireString("lifecycleId", 100))); return McpToolResult.Json(new { completed = true });
            case "list_machines": case "check_machine": case "setup_machine": case "forget_machine":
            {
                var operation = name switch { "list_machines" => "list", "check_machine" => "check", "setup_machine" => "setup", _ => "forget" };
                var options = new Dictionary<string, string?>(); if (a.String("machineId", 100) is { } id) options["vm"] = id; if (a.Bool("deep", false)) options["deep"] = "true";
                return StartWorkflow(name, async ct => { var result = await VmCommand.ExecuteAsync(operation, options, ct); return new { result = result.Result, exitCode = result.ExitCode }; });
            }
            case "workspace_backup": { var archive = a.RequireString("archive"); return StartWorkflow(name, ct => Task.FromResult<object>(WorkspaceMaintenance.Backup(Workspace, archive, ct))); }
            case "workspace_restore": { var archive = a.RequireString("archive"); var destination = a.RequireString("destination"); return StartWorkflow(name, ct => Task.FromResult<object>(WorkspaceMaintenance.Restore(archive, destination, ct))); }
            case "workspace_check":
                var tests = Store.LoadTests(); foreach (var test in tests) TestValidator.Validate(test); return McpToolResult.Json(new { completed = true, tests = tests.Count, runs = AllRuns().Count(), workspace = Workspace });
            default: throw new McpToolException("Unknown workflow operation.");
        }
    }
    private static object Preferences(ProviderSettings settings) => new { settings.SupportsImages, settings.LiveReview, settings.AiDirectedExecution, settings.MaximumAgentTurns, settings.MaximumProviderRetries, settings.ProviderRetryDelayMs, modelManagedByCortex = CortexModelBridge.Enabled };
}
