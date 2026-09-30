using System.Diagnostics;
using System.Globalization;
using Testy.Cli.HyperV;
using Testy.Core;

namespace Testy.Cli;

internal static class OperationsCommand
{
    public static int ExitCode(object result) => result is OperationsPumpResult pump ? pump.ExitCode : 0;
    public static async Task<object> ExecuteAsync(string operation, IReadOnlyDictionary<string, string> options, CancellationToken ct)
    {
        string Get(string key, string? fallback = null) => options.TryGetValue(key, out var value) ? value : fallback ?? throw new InvalidDataException("Missing --" + key + ".");
        int Number(string key, int fallback) => int.Parse(Get(key, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
        string workspace = Path.GetFullPath(Get("workspace", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testy", "Workspace")));
        ct.ThrowIfCancellationRequested();
        if (operation == "backup") return await Task.Run(() => WorkspaceMaintenance.Backup(workspace, Get("archive"), ct), ct);
        if (operation == "restore") return await Task.Run(() => WorkspaceMaintenance.Restore(Get("archive"), Get("destination"), ct), ct);
        if (operation == "migrate") return WorkspaceMaintenance.Migrate(workspace);
        var store = new OperationsStore(Path.Combine(workspace, "operations"));
        async Task<OperationsJobRequest> JobRequestAsync()
        {
            if (options.ContainsKey("test"))
            {
                if (options.ContainsKey("profile")) throw new InvalidDataException("Choose --profile (lifecycle) or --test (saved test run), not both.");
                var target = OperationsTargets.Parse(OperationsTargets.Normalize(Get("target", OperationsTargets.LocalName)));
                var test = await LoadTestRequestAsync(options, target, ct);
                return new() { Name = Get("name", test.Test.Name), Test = test, Target = target.IsLocal ? null : target.Canonical };
            }
            if (options.TryGetValue("target", out var requested) && !OperationsTargets.Parse(OperationsTargets.Normalize(requested)).IsLocal)
                throw new InvalidDataException("Lifecycle profiles run on this PC; use --test to queue a saved test for a VM.");
            var lifecycle = await LifecycleCommand.LoadRequestAsync(Get("profile"), ct);
            return new() { Name = Get("name", lifecycle.Profile.Name), Lifecycle = lifecycle };
        }
        switch (operation)
        {
            case "enqueue":
            case "enqueue-test":
                return store.Enqueue(await JobRequestAsync());
            case "list": return store.ListJobs();
            case "reconcile": return store.Reconcile();
            case "cancel": return store.RequestCancellation(Get("id"));
            case "rerun": return store.Rerun(Get("id"));
            case "delete-job": store.DeleteTerminalJob(Get("id")); return new { completed = true, message = "Terminal queue record removed. Artifact files retained." };
            case "schedules": return store.ListSchedules();
            case "schedule-pause": return store.SetScheduleEnabled(Get("id"), false);
            case "schedule-resume": return store.SetScheduleEnabled(Get("id"), true);
            case "schedule-delete": store.DeleteSchedule(Get("id")); return new { completed = true };
            case "schedule-add":
            {
                var request = await JobRequestAsync();
                return store.AddSchedule(new() { Name = request.Name, Request = request, IntervalSeconds = Number("interval-seconds", 3600),
                    FirstRunAt = options.TryGetValue("first-run", out var first) ? DateTimeOffset.Parse(first, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : DateTimeOffset.UtcNow });
            }
            case "pump":
            {
                bool once = options.ContainsKey("once"); int seconds = Number("seconds", once ? 15000 : 60);
                if (seconds is < 1 or > 86400) throw new InvalidDataException("Pump duration must be 1–86400 seconds.");
                var target = OperationsTargets.Parse(OperationsTargets.Normalize(Get("target", OperationsTargets.LocalName)));
                var records = new List<OperationsDispatchResult>();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
                var dispatcher = OperationsDispatcher.ForAllJobs(store, QueuedJobExecutor.ExecuteAsync);
                Func<CancellationToken, Task<OperationsTargetReadiness>> readiness = async token =>
                {
                    var observed = await VmRunner.CheckAsync(target.VirtualMachineId, token);
                    return new OperationsTargetReadiness(observed.Ready, observed.Reason);
                };
                var wall = Stopwatch.StartNew(); bool cancelled = false;
                try
                {
                    do
                    {
                        var next = await dispatcher.RunNextAsync(target, readiness, deadline.Token);
                        if (next.Job is not null || once) records.Add(next);
                        if (once) break;
                        await Task.Delay(target.IsLocal ? 500 : 5000, deadline.Token);
                    } while (!deadline.IsCancellationRequested);
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { cancelled = ct.IsCancellationRequested; }
                return new OperationsPumpResult { Completed = true, Cancelled = cancelled, ElapsedMs = wall.Elapsed.TotalMilliseconds, Results = records, Workspace = workspace, Target = target.Canonical };
            }
            default: throw new InvalidDataException("Unknown operations action. Use enqueue/enqueue-test/list/cancel/rerun/delete-job/reconcile/schedule-add/schedules/schedule-pause/schedule-resume/schedule-delete/pump/backup/restore/migrate.");
        }
    }

    /// <summary>Freezes a saved test, provider settings (or explicit replay) and launch arguments into a queueable request.</summary>
    internal static async Task<OperationsTestRequest> LoadTestRequestAsync(IReadOnlyDictionary<string, string> options, OperationsTarget target, CancellationToken ct)
    {
        string? Value(string key) => options.TryGetValue(key, out var value) ? value : null;
        int Number(string key, int fallback) => Value(key) is { } text ? int.Parse(text, CultureInfo.InvariantCulture) : fallback;
        bool replay = options.ContainsKey("replay");
        if (replay == (Value("settings") is not null)) throw new InvalidDataException("Choose exactly one of --replay or --settings FILE.");
        var test = await WorkerCommand.ReadJsonAsync<TestCase>(Value("test") ?? throw new InvalidDataException("Missing --test."), ct);
        var exe = Value("exe") ?? throw new InvalidDataException("Missing --exe (on this PC: absolute path; in a VM: guest path, {worker}\\... or a path inside --stage-directory).");
        var stage = Value("stage-directory") is { } folder ? Path.GetFullPath(folder) : null;
        if (stage is not null && !Directory.Exists(stage)) throw new InvalidDataException("The folder to copy into the VM does not exist: " + stage);
        var request = new OperationsTestRequest
        {
            Test = test, Provider = replay ? null : await ProjectCliCommand.SettingsAsync(Value("settings")!, null, ct),
            Executable = target.IsLocal ? Path.GetFullPath(exe) : exe, Probe = options.ContainsKey("probe"), StageDirectory = stage,
            TargetArguments = Value("target-arguments") is { } args ? [.. await WorkerCommand.ReadJsonAsync<string[]>(args, ct)] : [],
            TimeoutSeconds = Number("timeout-seconds", 900), StartupTimeoutSeconds = Number("startup-timeout-seconds", 30), ShutdownGraceSeconds = Number("shutdown-grace-seconds", 3)
        };
        OperationsTestValidation.Validate(request, target);
        return request;
    }
}
internal sealed class OperationsPumpResult
{
    public bool Completed { get; set; }
    public bool Cancelled { get; set; }
    public double ElapsedMs { get; set; }
    public string Target { get; set; } = OperationsTargets.LocalName;
    public List<OperationsDispatchResult> Results { get; set; } = [];
    public string Workspace { get; set; } = "";
    public int ExitCode => !Completed || Cancelled || Results.Any(r => r.Job is not null && !r.Job.Passed) ? 1 : 0;
    public string Message => "Pump stopped. Unstarted queued jobs remain durable. Scheduled work runs while a pump or the Testy background agent is active.";
}
