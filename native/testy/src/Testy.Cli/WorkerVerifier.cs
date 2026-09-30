using System.Diagnostics;
using System.Text.Json;
using Testy.Core;

namespace Testy.Cli;

internal static class WorkerVerifier
{
    public static async Task<VerificationReport> VerifyAsync(string labExecutable, string artifacts, CancellationToken ct)
    {
        labExecutable = Path.GetFullPath(labExecutable); artifacts = Path.GetFullPath(artifacts);
        if (!File.Exists(labExecutable) || Path.GetFileName(labExecutable) != "Testy.TestLab.exe") throw new ArgumentException("Worker verification launches only the included Testy.TestLab.exe.");
        Directory.CreateDirectory(artifacts);
        var report = new VerificationReport { Executable = labExecutable, PlannedChecks = 10 };
        string reportPath = Path.Combine(artifacts, "worker-verification.json");
        void Save() => WorkerCommand.DurableWrite(reportPath, report);
        Save();
        // Each invocation writes into its own folder: the checks count worker attempts and benchmark entries,
        // so results an earlier run left in the same artifacts directory must not be counted.
        string runRoot = Path.Combine(artifacts, "run-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(runRoot);
        var test = CustomerTest();
        string testFile = Path.Combine(runRoot,"worker-customer.json"); WorkerCommand.DurableWrite(testFile, test);
        WorkerOptions Options(string folder, bool probe = false) => new() { TestFile = testFile, Executable = labExecutable, Replay = true, Probe = probe, ArtifactsRoot = Path.Combine(runRoot,folder), TimeoutSeconds = 40, ShutdownGraceSeconds = 1 };
        async Task Check(string name, Func<Task<string>> action)
        {
            ct.ThrowIfCancellationRequested(); var item = new VerificationCheck { Name = name, Driver = "Isolated worker", Expected = RunStatus.Passed };
            try { item.Message = await action(); item.Passed = true; item.Actual = RunStatus.Passed; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { report.Cancelled = true; throw; }
            catch (Exception ex) { item.Message = ex.GetType().Name + ": " + ex.Message; item.Actual = RunStatus.Failed; }
            report.Checks.Add(item); Save();
        }
        try
        {
            await Check("Copied recovery guard evidence preserves bytes and rejects changed source or copy", () => Task.FromResult(WorkerGuardCopyChecks.Verify(Path.Combine(runRoot,"guard-copy-integrity"))));
            await Check("Malformed test starts no target or execution child", async () =>
            {
                string file = Path.Combine(runRoot,"invalid.json"); await File.WriteAllTextAsync(file, "{ invalid", ct);
                var options = Options("invalid-test"); options.TestFile = file;
                var result = await WorkerCommand.RunAsync(options, ct);
                RequireNoLaunch(result); return result.Message;
            });
            await Check("Impossible AI budget is rejected before target launch or provider request", async () =>
            {
                var settings = new ProviderSettings { Kind = ProviderKind.Compatible, Endpoint = "http://127.0.0.1:1/v1/chat/completions", Model = "not-called", ApiKeyEnvironmentVariable = "TESTY_MISSING_WORKER_TEST_KEY", MaximumAgentTurns = 1, NativeComputerUse = false };
                string file = Path.Combine(runRoot,"invalid-settings.json"); WorkerCommand.DurableWrite(file, settings);
                var options = Options("invalid-budget"); options.Replay = false; options.SettingsFile = file;
                var result = await WorkerCommand.RunAsync(options, ct);
                RequireNoLaunch(result); WorkerCommand.Require(result.Message.Contains("turn", StringComparison.OrdinalIgnoreCase), "Budget rejection was not the observed reason."); return result.Message;
            });
            foreach (bool probe in new[] { false, true }) await Check((probe ? "Probe" : "UIA") + " child verifies six canonical customer steps then closes its own target", async () =>
            {
                var result = await WorkerCommand.RunAsync(Options(probe ? "probe" : "uia", probe), ct);
                RequireClosed(result); WorkerCommand.Require(result.Passed && result.SavedSteps == 6 && result.EvidenceImages == 6, result.Message);
                return result.ArtifactDirectory;
            });
            await Check("Wrong assertion fails and later mutation remains skipped", async () =>
            {
                var failed = new TestCase { Name = "Owned worker exact assertion failure", Steps = [Step(StepAction.Click, "ResetButton"), Step(StepAction.AssertText, "StatusMessage", "This must not match"), Step(StepAction.TypeText, "CustomerName", "MUST NOT EXECUTE")] };
                string file = Path.Combine(runRoot,"expected-failure.json"); WorkerCommand.DurableWrite(file, failed);
                var options = Options("expected-failure"); options.TestFile = file;
                var result = await WorkerCommand.RunAsync(options, ct); RequireClosed(result);
                WorkerCommand.Require(!result.Passed && result.Status == "failed" && result.ChildExitCode == 1, result.Message);
                var run = await WorkerCommand.ReadJsonAsync<RunResult>(Path.Combine(result.RunDirectory, "run.json"), ct);
                WorkerCommand.Require(run.Steps.Count == 3 && run.Steps[0].Status == RunStatus.Passed && run.Steps[1].Status == RunStatus.Failed && run.Steps[2].Status == RunStatus.Skipped, "Failure did not preserve exact expected execution/skipped positions.");
                WorkerCommand.Require(run.Steps[1].FailureDiagnostics.Any(d => d.Category == FailureCategory.AssertionMismatch), "Expected assertion mismatch diagnostic missing.");
                return result.ArtifactDirectory;
            });
            await Check("Cancellation writes a terminal result, stops before next mutation, and cleans owned processes", async () =>
            {
                var waiting = new TestCase { Name = "Cooperative worker cancellation", Steps = [new TestStep { Title = "Wait for cancellation", Action = StepAction.Wait, Value = "15000", TimeoutMs = 20000 }, Step(StepAction.TypeText, "CustomerName", "MUST NOT EXECUTE")] };
                string file = Path.Combine(runRoot,"cancellation-test.json"); WorkerCommand.DurableWrite(file, waiting);
                var options = Options("cancelled"); options.TestFile = file;
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var running = WorkerCommand.RunAsync(options, cancellation.Token);
                var timer = Stopwatch.StartNew();
                while (!running.IsCompleted && !Directory.EnumerateFiles(options.ArtifactsRoot, "start.signal", SearchOption.AllDirectories).Any())
                { if (timer.Elapsed.TotalSeconds > 25) throw new TimeoutException("Cancellation verifier never observed child start gate."); await Task.Delay(50, ct); }
                await Task.Delay(700, ct); cancellation.Cancel();
                var result = await running; RequireClosed(result);
                WorkerCommand.Require(result.Status == "cancelled" && !result.Passed && result.FinishedAt is not null, result.Message);
                foreach (var path in Directory.EnumerateFiles(result.ArtifactDirectory, "run.json", SearchOption.AllDirectories))
                {
                    var run = await WorkerCommand.ReadJsonAsync<RunResult>(path, ct);
                    WorkerCommand.Require(!run.Steps.Any(s => s.Step.Value == "MUST NOT EXECUTE" && s.Status is not RunStatus.Skipped), "Later mutation began after cancellation.");
                }
                return result.ArtifactDirectory;
            });
            await Check("Uncooperative child is terminated by the watchdog with uncertain-input result and no retry", async () =>
            {
                var options = Options("watchdog"); options.TimeoutSeconds = 1;
                var result = await WorkerCommand.RunCoreAsync(options, ct, _ =>
                {
                    var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
                    { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60" }) start.ArgumentList.Add(argument);
                    return Process.Start(start)!;
                });
                RequireClosed(result);
                WorkerCommand.Require(result.Status == "timedOut" && result.ChildForcedTermination && result.InputMayHaveOccurred && result.WallMs < 20000 && !result.Passed, "Watchdog result did not prove bounded forced termination.");
                WorkerCommand.Require(Directory.GetDirectories(options.ArtifactsRoot, "worker-*").Length == 1, "Worker retried an uncertain run.");
                return result.ArtifactDirectory;
            });
            await Check("Target startup failure is bounded and no execution child starts", async () =>
            {
                var options = Options("startup-failure"); options.StartupTimeoutSeconds = 2;
                // The current CLI --help exits without a GUI. It is an owned process, not an unrelated installed application.
                options.Executable = Path.ChangeExtension(typeof(WorkerCommand).Assembly.Location, ".exe");
                string args = Path.Combine(runRoot,"help-arguments.json"); WorkerCommand.DurableWrite(args, new[] { "--help" }); options.TargetArgumentsFile = args;
                var result = await WorkerCommand.RunAsync(options, ct); RequireClosed(result);
                WorkerCommand.Require(result.Status == "unavailable" && result.ChildProcessId == 0 && !result.Passed, result.Message);
                return result.ArtifactDirectory;
            });
            await Check("Benchmark export preserves exact observed outcomes, comparison groups and quoted text", async () =>
            {
                string json = Path.Combine(runRoot,"benchmark.json"), csv = Path.Combine(runRoot,"benchmark.csv");
                await BenchmarkCommand.ExportAsync(runRoot, json, "json", ct); await BenchmarkCommand.ExportAsync(runRoot, csv, "csv", ct);
                using var export = JsonDocument.Parse(await File.ReadAllTextAsync(json, ct));
                WorkerCommand.Require(export.RootElement.GetProperty("entries").GetArrayLength() == 8, "Benchmark omitted or duplicated an observed worker run.");
                WorkerCommand.Require(export.RootElement.GetProperty("competitor").GetProperty("executionMs").ValueKind == JsonValueKind.Null, "Unmeasured competitor was given a timing.");
                WorkerCommand.Require(BenchmarkCommand.CsvRow(["a,b", "a\"b", "line\nbreak", "=formula"]) == "\"a,b\",\"a\"\"b\",\"line\nbreak\",\"'=formula\"", "CSV quoting/formula protection changed text unexpectedly.");
                return json;
            });
            ct.ThrowIfCancellationRequested(); report.CompletedAllScenarios = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { report.Cancelled = true; }
        finally { report.Finish(ct); Save(); }
        return report;
    }
    internal static TestCase CustomerTest() => new()
    {
        Id = "worker-customer-six-steps", Name = "Owned worker create customer", Steps =
        [Step(StepAction.Click, "ResetButton"), Step(StepAction.TypeText, "CustomerName", "Worker Ada"), Step(StepAction.TypeText, "CustomerEmail", "worker@example.test"), Step(StepAction.Click, "AddCustomer"), Step(StepAction.AssertText, "StatusMessage", "Customer added: Worker Ada"), Step(StepAction.AssertText, "ResultCount", "1 customer")]
    };
    internal static TestStep Step(StepAction action, string id, string value = "") => new() { Title = action + " " + id, Action = action, Selector = "id:" + id, Value = value, TimeoutMs = 5000 };
    private static void RequireNoLaunch(WorkerResult result)
    { WorkerCommand.Require(result.Status == "invalidConfiguration" && result.TargetProcessId == 0 && result.ChildProcessId == 0 && result.CleanupComplete && !result.Passed && result.FinishedAt is not null, "Invalid input launched a process or produced a nonterminal result."); }
    private static void RequireClosed(WorkerResult result)
    {
        WorkerCommand.Require(result.CleanupComplete && result.FinishedAt is not null, "Owned-process cleanup was not completed.");
        foreach (var identity in new[] { (result.TargetProcessId, result.TargetStartedAt), (result.ChildProcessId, result.ChildStartedAt) })
        {
            if (identity.Item1 == 0) continue;
            try { using var process = Process.GetProcessById(identity.Item1); WorkerCommand.Require(process.StartTime.ToUniversalTime() != identity.Item2, "An owned worker process is still running."); }
            catch (ArgumentException) { }
        }
    }
}
