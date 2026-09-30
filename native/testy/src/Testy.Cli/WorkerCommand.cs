using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Testy.Core;

namespace Testy.Cli;

internal sealed class WorkerOptions
{
    public string TestFile { get; set; } = "";
    public string Executable { get; set; } = "";
    public string? SettingsFile { get; set; }
    public string? ProjectFile { get; set; }
    public bool Replay { get; set; }
    public bool Probe { get; set; }
    public string ArtifactsRoot { get; set; } = "artifacts";
    public int TimeoutSeconds { get; set; } = 300;
    public int StartupTimeoutSeconds { get; set; } = 20;
    public int ShutdownGraceSeconds { get; set; } = 3;
    public string? TargetArgumentsFile { get; set; }
    internal OperationsDesktopLease? DesktopLease { get; set; }
}

internal sealed class WorkerResult
{
    public string Schema { get; set; } = "testy.worker.v1";
    public string ProductVersion { get; set; } = typeof(WorkerResult).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string Status { get; set; } = "preflighting";
    public bool Passed => FinishedAt >= StartedAt && Status == "passed" && ChildExitCode == 0 && CanonicalCoverageVerified && CleanupComplete;
    public int ExitCode => Passed ? 0 : Status is "invalidConfiguration" or "unavailable" ? 2 : 1;
    public string Message { get; set; } = "";
    public string ArtifactDirectory { get; set; } = "";
    public string RunDirectory { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Driver { get; set; } = "";
    public string TestId { get; set; } = "";
    public string TestName { get; set; } = "";
    public string TestSha256 { get; set; } = "";
    public string FrozenTestSha256 { get; set; } = "";
    public string TestHashPolicy => "Canonical saved TestCase with only UpdatedAt normalized to UnixEpoch; all IDs, actions, expectations and approved alternatives retained.";
    public string TargetExecutable { get; set; } = "";
    public string TargetSha256 { get; set; } = "";
    public string CliSha256 { get; set; } = "";
    public string TargetBuildSha256 { get; set; } = "";
    public string TargetBuildAfterSha256 { get; set; } = "";
    public string RunnerBuildSha256 { get; set; } = "";
    public string ProjectConfigurationSha256 { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public int TargetProcessId { get; set; }
    public DateTimeOffset? TargetStartedAt { get; set; }
    public int ChildProcessId { get; set; }
    public DateTimeOffset? ChildStartedAt { get; set; }
    public int? ChildExitCode { get; set; }
    public bool InputMayHaveOccurred { get; set; }
    public bool ActionOutcomeUnknown { get; set; }
    public bool ChildForcedTermination { get; set; }
    public bool TargetForcedTermination { get; set; }
    public bool CleanupComplete { get; set; }
    public bool CanonicalCoverageVerified { get; set; }
    public int SavedSteps { get; set; }
    public int EvidenceImages { get; set; }
    public int RecoveryAttempts { get; set; }
    public int? ModelTurns { get; set; }
    public int ProjectToolCalls { get; set; }
    public int ProjectCommandRuns { get; set; }
    public double StartupMs { get; set; }
    public double ExecutionMs { get; set; }
    public double CleanupMs { get; set; }
    public double WallMs { get; set; }
    public DesktopPreflight? Desktop { get; set; }
    public List<string> CleanupErrors { get; set; } = [];
    public List<LifecycleCheckpoint> Checkpoints { get; set; } = [];
    public string RetryPolicy => "No automatic retry. A cancelled or killed child may have delivered input; inspect its preserved evidence before starting a new run.";
}

internal static class WorkerCommand
{
    private const long MaximumJsonBytes = 32 * 1024 * 1024;
    internal static async Task<T> ReadJsonAsync<T>(string path, CancellationToken ct)
    {
        var info = new FileInfo(Path.GetFullPath(path));
        if (!info.Exists || info.Length > MaximumJsonBytes) throw new InvalidDataException("JSON file is missing or exceeds 32 MiB: " + info.Name);
        return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(info.FullName, ct), TestyJson.Options)
            ?? throw new InvalidDataException("JSON file is null: " + info.Name);
    }

    public static Task<WorkerResult> RunAsync(WorkerOptions options, CancellationToken ct) => RunCoreAsync(options, ct);

    // Internal injection is used only by the owned-fixture watchdog verifier; it is not a CLI feature.
    internal static async Task<WorkerResult> RunCoreAsync(WorkerOptions options, CancellationToken ct,
        Func<ProcessStartInfo, Process>? startChild = null)
    {
        var result = new WorkerResult { Mode = options.Replay ? "replay" : "ai", Driver = options.Probe ? "wpfProbe" : "uia" };
        result.ArtifactDirectory = Path.Combine(Path.GetFullPath(options.ArtifactsRoot), $"worker-{result.StartedAt:yyyyMMdd-HHmmss}-{result.Id[..8]}");
        Directory.CreateDirectory(result.ArtifactDirectory);
        string resultPath = Path.Combine(result.ArtifactDirectory, "worker-result.json");
        string cancelPath = Path.Combine(result.ArtifactDirectory, "cancel.signal");
        string startPath = Path.Combine(result.ArtifactDirectory, "start.signal");
        void Save()
        {
            if (result.Checkpoints.Count == 0 || result.Checkpoints[^1].Stage != result.Status)
            {
                result.Checkpoints.Add(new LifecycleCheckpoint { Sequence = result.Checkpoints.Count, Stage = result.Status, Message = result.Message, MayHaveSideEffects = result.InputMayHaveOccurred });
                DurableWrite(Path.Combine(result.ArtifactDirectory, $"worker-checkpoint-{result.Checkpoints.Count:000}.json"), result.Checkpoints[^1]);
            }
            DurableWrite(resultPath, result);
        }
        Save();
        var wall = Stopwatch.StartNew();
        Process? target = null, child = null;
        OwnedProcessJob? job = null;
        OperationsDesktopLease? ownedDesktopLease = null;
        IDisposable? desktopExecution = null;
        Task? stdout = null, stderr = null;
        TestCase? test = null;
        ProviderSettings? settings = null;
        try
        {
            ValidateOptions(options);
            test = await ReadJsonAsync<TestCase>(options.TestFile, ct);
            TestValidator.Validate(test);
            if (!options.Replay)
            {
                settings = await ProjectCliCommand.SettingsAsync(options.SettingsFile!, options.ProjectFile, ct);
                AiTestRunner.ValidateExecution(test, settings);
                ValidateProvider(settings);
                result.Mode = settings.Kind == ProviderKind.OpenAI && settings.NativeComputerUse ? "aiNativeHybrid" : "aiLocalTools";
            }
            var arguments = string.IsNullOrWhiteSpace(options.TargetArgumentsFile) ? [] : await ReadJsonAsync<string[]>(options.TargetArgumentsFile, ct);
            if (arguments.Length > 64 || arguments.Any(a => a is null || a.Length > 4096 || a.Contains('\0')))
                throw new InvalidDataException("Target arguments must be a JSON array of at most 64 strings, each at most 4096 characters and without NUL.");
            result.TargetExecutable = Path.GetFullPath(options.Executable);
            result.TargetSha256 = HashFile(result.TargetExecutable);
            result.CliSha256 = HashFile(typeof(WorkerCommand).Assembly.Location);
            var targetBuild = BuildFingerprint.Capture(result.TargetExecutable);
            var runnerBuild = BuildFingerprint.Capture(typeof(WorkerCommand).Assembly.Location);
            result.TargetBuildSha256 = targetBuild.Sha256; result.RunnerBuildSha256 = runnerBuild.Sha256;
            DurableWrite(Path.Combine(result.ArtifactDirectory, "target-build.json"), targetBuild);
            DurableWrite(Path.Combine(result.ArtifactDirectory, "runner-build.json"), runnerBuild);
            result.TestId = test.Id; result.TestName = test.Name; result.SavedSteps = test.Steps.Count;
            result.Provider = settings?.Kind.ToString() ?? "none"; result.Model = settings?.Model ?? "";
            if (settings?.ProjectTools?.Enabled == true)
                result.ProjectConfigurationSha256 = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(settings.ProjectTools, TestyJson.Options)));
            var frozenTest = Path.Combine(result.ArtifactDirectory, "requested-test.json");
            DurableWrite(frozenTest, test); result.FrozenTestSha256 = HashFile(frozenTest); result.TestSha256 = WorkloadHash(test);
            var frozenSettings = Path.Combine(result.ArtifactDirectory, "provider-settings.json");
            if (settings is not null) DurableWrite(frozenSettings, settings); // Environment-variable NAME only; never its value.
            DurableWrite(Path.Combine(result.ArtifactDirectory, "target-arguments.json"), arguments);
            result.Desktop = DesktopPreflight.Observe(); Save();
            if (!result.Desktop.Available) throw new WorkerUnavailableException(result.Desktop.Reason);
            var desktopLease = options.DesktopLease ?? (ownedDesktopLease = OperationsDesktopLease.TryAcquire());
            if (desktopLease is null) throw new WorkerUnavailableException("This interactive desktop is reserved by another Testy worker. No target was started.");
            desktopExecution = desktopLease.EnterExecution();
            ct.ThrowIfCancellationRequested();
            job = new OwnedProcessJob();
            var startup = Stopwatch.StartNew(); result.Status = "startingTarget"; Save();
            var targetInfo = new ProcessStartInfo(result.TargetExecutable)
            { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(result.TargetExecutable)! };
            foreach (string argument in arguments) targetInfo.ArgumentList.Add(argument);
            target = job.StartTarget(targetInfo);
            desktopLease.RegisterOwnedProcess(target);
            result.TargetProcessId = target.Id; result.TargetStartedAt = target.StartTime.ToUniversalTime();
            Save();
            while (true)
            {
                ct.ThrowIfCancellationRequested(); target.Refresh();
                if (target.HasExited) throw new WorkerUnavailableException("The newly started target exited before exposing a window. Verify its runtime dependencies; single-instance handoff to another process is not supported.");
                if (target.MainWindowHandle != 0) break;
                if (startup.Elapsed.TotalSeconds >= options.StartupTimeoutSeconds) throw new WorkerUnavailableException("The newly started target did not expose a window before the startup deadline.");
                await Task.Delay(100, ct);
            }
            result.StartupMs = startup.Elapsed.TotalMilliseconds;
            var childInfo = CreateSelfStartInfo();
            foreach (string argument in new[] { options.Replay ? "run" : "run-ai", "--test", frozenTest, "--pid", target.Id.ToString(), "--artifacts", Path.Combine(result.ArtifactDirectory, "execution"), "--worker-start-file", startPath, "--worker-cancel-file", cancelPath }) childInfo.ArgumentList.Add(argument);
            if (options.Probe) childInfo.ArgumentList.Add("--probe");
            if (settings is not null) { childInfo.ArgumentList.Add("--settings"); childInfo.ArgumentList.Add(frozenSettings); }
            child = startChild?.Invoke(childInfo) ?? Process.Start(childInfo) ?? throw new InvalidOperationException("The isolated CLI child did not start.");
            desktopLease.RegisterOwnedProcess(child);
            result.ChildProcessId = child.Id; result.ChildStartedAt = child.StartTime.ToUniversalTime();
            stdout = DrainAsync(child.StandardOutput, Path.Combine(result.ArtifactDirectory, "child-stdout.json"));
            stderr = DrainAsync(child.StandardError, Path.Combine(result.ArtifactDirectory, "child-stderr.txt"));
            job.Add(child);
            ct.ThrowIfCancellationRequested();
            result.Status = "running"; result.InputMayHaveOccurred = true; result.ActionOutcomeUnknown = true; Save();
            DurableWrite(startPath, new { start = true }); // Child cannot attach or dispatch until job assignment and durable state exist.
            var execution = Stopwatch.StartNew();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            try { await child.WaitForExitAsync(combined.Token); }
            catch (OperationCanceledException)
            {
                result.Status = ct.IsCancellationRequested ? "cancelled" : "timedOut";
                result.Message = ct.IsCancellationRequested ? "Cancellation requested; no automatic retry was attempted." : "The child exceeded the execution watchdog deadline; no automatic retry was attempted.";
                DurableWrite(cancelPath, new { cancel = true }); Save();
                if (!await WaitForExitAsync(child, TimeSpan.FromSeconds(options.ShutdownGraceSeconds)))
                { result.ChildForcedTermination = true; child.Kill(entireProcessTree: true); await WaitForExitAsync(child, TimeSpan.FromSeconds(3)); }
            }
            finally { result.ExecutionMs = execution.Elapsed.TotalMilliseconds; }
            if (child.HasExited) result.ChildExitCode = child.ExitCode;
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
            if (result.Status is not ("cancelled" or "timedOut"))
            {
                var run = await ReadJsonAsync<RunResult>(Path.Combine(result.ArtifactDirectory, "child-stdout.json"), CancellationToken.None);
                result.RunDirectory = run.ArtifactDirectory;
                result.ProjectToolCalls = run.ProjectEvidence.Count;
                result.ProjectCommandRuns = run.ProjectEvidence.Count(p => p.Command is not null);
                result.TargetBuildAfterSha256 = BuildFingerprint.Capture(result.TargetExecutable).Sha256;
                Require(result.TargetBuildAfterSha256 == result.TargetBuildSha256, "The target's executable-directory build changed while the application was attached. The loaded application cannot be qualified as that rebuilt version; launch a fresh test instance.");
                ValidateTerminalRun(run, test, target.Id, result.ArtifactDirectory, options.Replay, result.ChildExitCode ?? -1);
                result.EvidenceImages = run.Steps.Count(s => !string.IsNullOrEmpty(s.ScreenshotPath));
                result.RecoveryAttempts = run.Steps.Count(s => s.SelectorRecovery is not null);
                result.CanonicalCoverageVerified = run.Status == RunStatus.Passed;
                if (!options.Replay && File.Exists(Path.Combine(run.ArtifactDirectory, "computer-agent.json")))
                    result.ModelTurns = (await ReadJsonAsync<ComputerAgentResult>(Path.Combine(run.ArtifactDirectory, "computer-agent.json"), CancellationToken.None)).ModelTurns;
                result.Status = run.Status switch { RunStatus.Passed => "passed", RunStatus.Cancelled => "cancelled", _ => "failed" };
                result.Message = run.Summary;
                result.InputMayHaveOccurred = run.Steps.Any(s => !TestValidator.IsAssertion(s.Step.Action) && s.Step.Action is not (StepAction.Screenshot or StepAction.Wait) && s.Status != RunStatus.Skipped);
                result.ActionOutcomeUnknown = HasUnknownActionOutcome(run);
            }
        }
        catch (OperationCanceledException)
        { result.Status = "cancelled"; result.Message = "Worker cancelled. Preserved evidence must be reviewed before any new run."; }
        catch (WorkerUnavailableException ex) { result.Status = "unavailable"; result.Message = ex.Message; }
        catch (Exception ex)
        { result.Status = target is null ? "invalidConfiguration" : "failed"; result.Message = ex.GetType().Name + ": " + ex.Message; }
        finally
        {
            var cleanup = Stopwatch.StartNew();
            if (child is not null && !child.HasExited)
            {
                try
                {
                    DurableWrite(cancelPath, new { cancel = true });
                    if (!await WaitForExitAsync(child, TimeSpan.FromSeconds(options.ShutdownGraceSeconds is >= 1 and <= 30 ? options.ShutdownGraceSeconds : 3)))
                    { result.ChildForcedTermination = true; child.Kill(entireProcessTree: true); }
                    await WaitForExitAsync(child, TimeSpan.FromSeconds(3));
                }
                catch (Exception ex) { result.CleanupErrors.Add("Child cleanup: " + ex.Message); }
            }
            if (target is not null && !target.HasExited)
            {
                try
                {
                    target.CloseMainWindow();
                    if (!await WaitForExitAsync(target, TimeSpan.FromSeconds(2)))
                    { result.TargetForcedTermination = true; target.Kill(entireProcessTree: true); }
                    await WaitForExitAsync(target, TimeSpan.FromSeconds(3));
                }
                catch (Exception ex) { result.CleanupErrors.Add("Target cleanup: " + ex.Message); }
            }
            bool jobEmpty = true;
            if (job is not null)
            {
                try { jobEmpty = await job.StopAndVerifyAsync(); }
                catch (Exception ex) { jobEmpty = false; result.CleanupErrors.Add("Owned job cleanup: " + ex.Message); }
                finally { job.Dispose(); }
            }
            result.CleanupComplete = jobEmpty && (child is null || child.HasExited) && (target is null || target.HasExited) && result.CleanupErrors.Count == 0;
            if (child is not null && child.HasExited) result.ChildExitCode = child.ExitCode;
            try { if (stdout is not null && stderr is not null) await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception ex) { result.CleanupErrors.Add("Child output drain: " + ex.Message); result.CleanupComplete = false; }
            child?.Dispose(); target?.Dispose();
            desktopExecution?.Dispose(); ownedDesktopLease?.Dispose();
            if (ct.IsCancellationRequested) { result.Status = "cancelled"; result.Message = "Worker cancelled; no automatic retry was attempted."; }
            if (!result.CleanupComplete && result.Status == "passed") { result.Status = "failed"; result.Message = "Execution passed but owned-process cleanup could not be verified."; }
            result.CleanupMs = cleanup.Elapsed.TotalMilliseconds; result.WallMs = wall.Elapsed.TotalMilliseconds;
            result.FinishedAt = DateTimeOffset.UtcNow; Save();
            DurableWrite(Path.Combine(result.ArtifactDirectory, "benchmark-entry.json"), BenchmarkEntry.From(result));
        }
        return result;
    }

    internal static void ValidateOptions(WorkerOptions options)
    {
        if (options.Replay && options.ProjectFile is not null) throw new ArgumentException("Project tools require AI-directed execution, not replay.");
        if (!OperatingSystem.IsWindows()) throw new WorkerUnavailableException("The interactive worker requires Windows.");
        if (options.Replay == !string.IsNullOrWhiteSpace(options.SettingsFile)) throw new ArgumentException("Choose exactly one of --replay or --settings FILE.");
        if (options.TimeoutSeconds is < 1 or > 7200 || options.StartupTimeoutSeconds is < 1 or > 120 || options.ShutdownGraceSeconds is < 1 or > 30)
            throw new ArgumentException("Execution timeout must be1–7200 seconds, startup1–120, and shutdown grace1–30.");
        if (!File.Exists(options.Executable) || !Path.GetExtension(options.Executable).Equals(".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The target must be an existing Windows .exe.");
    }

    internal static bool HasUnknownActionOutcome(RunResult run) => run.FailureDiagnostics.Any(d => d.ActionOutcome == ActionOutcome.Unknown)
        || run.Steps.Any(s => s.FailureDiagnostics.Any(d => d.ActionOutcome == ActionOutcome.Unknown))
        || run.ProjectEvidence.Any(p => p.Command is { CleanupComplete: false });

    internal static void ValidateProvider(ProviderSettings settings)
    {
        if (settings.Kind is ProviderKind.OpenAI or ProviderKind.Compatible)
        {
            if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback) || !string.IsNullOrEmpty(uri.UserInfo)) throw new InvalidDataException("Provider endpoint must be HTTPS, or HTTP on localhost, without embedded credentials.");
            if (string.IsNullOrWhiteSpace(settings.Model)) throw new InvalidDataException("API provider model is required.");
            var key = ProviderCredentialStore.Resolve(settings);
            if (settings.Kind == ProviderKind.OpenAI && string.IsNullOrWhiteSpace(key)) throw new InvalidDataException("The configured API credential is absent in Windows Credential Manager and process/User environment scope.");
        }
        else if (settings.Kind == ProviderKind.Codex)
        {
            string path = CodexExecutableResolver.Resolve(settings.CodexExecutable, Environment.GetEnvironmentVariable("PATH") ?? "", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            if (!File.Exists(path)) throw new InvalidDataException("The configured Codex executable could not be resolved before target launch.");
        }
    }

    // Actual acceptance seam: called by the parent after child completion, and by independent corruption tests.
    internal static void ValidateTerminalRun(RunResult run, TestCase test, int pid, string workerDirectory, bool replay, int exitCode)
    {
        Require(run.FinishedAt is not null && run.FinishedAt >= run.StartedAt, "Child run is not finalized.");
        Require(run.TestId == test.Id && run.TestName == test.Name && run.Target.ProcessId == pid, "Child result belongs to a different test or target.");
        Require(run.Status is RunStatus.Passed or RunStatus.Failed or RunStatus.Cancelled, "Child result is not terminal.");
        Require(exitCode == (run.Status == RunStatus.Passed ? 0 : 1), "Child exit code contradicts its terminal result.");
        RequireWithin(run.ArtifactDirectory, workerDirectory);
        foreach (string name in new[] { "run.json", "requested-test.json", "report.html", "junit.xml" })
            Require(File.Exists(Path.Combine(run.ArtifactDirectory, name)), "Child omitted required report: " + name);
        var persisted = JsonSerializer.Deserialize<RunResult>(File.ReadAllText(Path.Combine(run.ArtifactDirectory, "run.json")), TestyJson.Options)!;
        Require(JsonSerializer.Serialize(persisted, TestyJson.Options) == JsonSerializer.Serialize(run, TestyJson.Options), "Child stdout disagrees with its persisted terminal run.");
        var saved = JsonSerializer.Deserialize<TestCase>(File.ReadAllText(Path.Combine(run.ArtifactDirectory, "requested-test.json")), TestyJson.Options)!;
        Require(JsonSerializer.Serialize(saved, TestyJson.Options) == JsonSerializer.Serialize(test, TestyJson.Options), "Child changed the frozen requested test.");
        ProjectToolSettings? projectConfiguration = null;
        var providerFile = Path.Combine(workerDirectory, "provider-settings.json");
        if (File.Exists(providerFile)) projectConfiguration = JsonSerializer.Deserialize<ProviderSettings>(File.ReadAllText(providerFile), TestyJson.Options)?.ProjectTools;
        if (run.ProjectEvidence.Count > 0)
            Require(!replay && projectConfiguration?.Enabled == true, "Project execution was not enabled for this worker.");
        ProjectEvidenceVerifier.ValidateSequence(run.ProjectEvidence, workerDirectory,
            projectConfiguration?.Enabled == true ? projectConfiguration : null, requireComplete: run.Status == RunStatus.Passed);
        if (run.Status == RunStatus.Passed && projectConfiguration?.Enabled == true)
            Require(projectConfiguration.RequiredBeforeUiCommands.All(id => run.ProjectEvidence.Any(e => e.Command is not null && e.Status == ProjectToolStatus.Succeeded
                && e.Request!.Value.GetProperty("commandId").GetString() == id)), "Worker omitted a required project command.");
        if (replay)
        {
            Require(run.Steps.Count == test.Steps.Count, "Replay omitted or added canonical step records.");
            for (int i = 0; i < test.Steps.Count; i++) Require(Canonical(run.Steps[i].Step, test.Steps[i]), "Replay changed a canonical step.");
            if (run.Status == RunStatus.Passed) Require(run.Steps.All(s => s.Status == RunStatus.Passed), "Passed replay contains non-passing steps.");
            else Require(run.Steps.Any(s => s.Status is RunStatus.Failed or RunStatus.Cancelled), "Failed replay has no terminal failing step.");
        }
        for (int i = 0; i < run.Steps.Count; i++)
        {
            var step = run.Steps[i]; Require(step.Index == i, "Child step indices are missing, duplicated or reordered.");
            Require(step.Snapshot is null || step.Snapshot.Target.ProcessId == pid, "Child snapshot belongs to another process.");
            Require(step.Status is RunStatus.Passed or RunStatus.Failed or RunStatus.Cancelled or RunStatus.Skipped, "Child contains a pending step.");
            if (step.Status == RunStatus.Passed)
            {
                Require(step.Snapshot is not null && !step.Snapshot.IsTruncated, "Passed child step has missing or truncated evidence.");
                RequireWithin(step.ScreenshotPath, workerDirectory); ValidatePng(step.ScreenshotPath);
                string treePath = Path.Combine(run.ArtifactDirectory, $"step-{i + 1:000}.json");
                Require(File.Exists(treePath), "Passed step tree artifact missing.");
                var tree = JsonSerializer.Deserialize<UiSnapshot>(File.ReadAllText(treePath), TestyJson.Options)!;
                Require(JsonSerializer.Serialize(tree, TestyJson.Options) == JsonSerializer.Serialize(step.Snapshot, TestyJson.Options), "Persisted step tree disagrees with the terminal record.");
                if (step.SelectorRecovery is not null)
                {
                    RequireWithin(step.SelectorRecovery.GuardSnapshotPath, workerDirectory);
                    SelectorRecovery.ValidateEvidence(step.Step, step);
                }
            }
        }
        if (!replay && run.Status == RunStatus.Passed)
        {
            var agentPath = Path.Combine(run.ArtifactDirectory, "computer-agent.json");
            Require(File.Exists(agentPath), "Passed AI child omitted the agent evidence.");
            var agent = JsonSerializer.Deserialize<ComputerAgentResult>(File.ReadAllText(agentPath), TestyJson.Options)!;
            Require(agent.Completed && agent.Status is RunStatus.Pending or RunStatus.Passed, "AI child did not complete.");
            Require(SavedWorkflowVerifier.Verify(test, agent.Observations).Complete && AiTestRunner.VerifyAssertions(test, run.Steps).Complete, "Saved AI workflow/assertion coverage is incomplete.");
            Require(run.Steps.Count > 0 && run.Steps.All(s => s.Status == RunStatus.Passed), "Passed AI child contains a non-passing action.");
            var projectObservations = agent.Observations.Where(o => o.Project is not null).Select(o => o.Project!).ToArray();
            Require(JsonSerializer.Serialize(projectObservations, TestyJson.Options) == JsonSerializer.Serialize(run.ProjectEvidence, TestyJson.Options), "Flat project evidence differs from the model's actual project observations.");
            var completedProjectCommands = new HashSet<string>(StringComparer.Ordinal);
            int flattened = 0;
            foreach (var observation in agent.Observations)
            {
                if (observation.Project is { } project)
                {
                    Require(ProjectToolSession.IsTool(observation.ToolName) && observation.ToolName == project.ToolName,
                        "Project observation tool identity disagrees with its evidence.");
                    Require(observation.Execution is null && observation.NativeAction is null && observation.NativeReceipt is null && observation.SavedStepId.Length == 0,
                        "Project observation claims UI action evidence.");
                    if (project.Command is not null && project.Status == ProjectToolStatus.Succeeded)
                        completedProjectCommands.Add(project.Request!.Value.GetProperty("commandId").GetString()!);
                    continue;
                }
                Require(!ProjectToolSession.IsTool(observation.ToolName), "Project tool observation omitted its project evidence.");
                if (observation.ToolName != "observe_application" && projectConfiguration?.Enabled == true)
                    Require(projectConfiguration.RequiredBeforeUiCommands.All(completedProjectCommands.Contains), "UI execution preceded its required project commands.");
                Require(observation.Snapshot?.Target.ProcessId == pid, "Agent observation lacks the owned target identity.");
                RequireWithin(observation.ScreenshotPath, workerDirectory); ValidatePng(observation.ScreenshotPath);
                if (observation.ToolName is "perform_saved_step" or "perform_saved_step_alternate" or "perform_saved_control_step" or "verify_saved_assertion")
                {
                    var canonical = test.Steps.SingleOrDefault(s => s.Id == observation.SavedStepId);
                    Require(canonical is not null && observation.Execution?.Steps.Count == 1 && Canonical(observation.Execution.Steps[0].Step, canonical), "Bound agent observation changed its canonical saved step.");
                }
                foreach (var action in observation.Execution?.Steps ?? [])
                {
                    Require(flattened < run.Steps.Count, "Agent observations exceed the terminal step records.");
                    var normalized = TestyJson.Clone(action); var actual = run.Steps[flattened];
                    normalized.Index = flattened++; normalized.Snapshot ??= observation.Snapshot;
                    string sourceImage = string.IsNullOrEmpty(normalized.ScreenshotPath) ? observation.ScreenshotPath : normalized.ScreenshotPath;
                    RequireWithin(sourceImage, workerDirectory);
                    Require(HashFile(sourceImage) == HashFile(actual.ScreenshotPath), "Flat action screenshot differs from its agent evidence.");
                    normalized.ScreenshotPath = actual.ScreenshotPath;
                    if (normalized.SelectorRecovery is not null && actual.SelectorRecovery is not null)
                    {
                        string sourceGuard = normalized.SelectorRecovery.GuardSnapshotPath;
                        string copiedGuard = actual.SelectorRecovery.GuardSnapshotPath;
                        RequireWithin(sourceGuard, workerDirectory); RequireWithin(copiedGuard, workerDirectory);
                        Require(File.Exists(sourceGuard) && File.Exists(copiedGuard), "Recovery guard source or copied evidence is missing.");
                        Require(HashFile(sourceGuard) == HashFile(copiedGuard), "Flat recovery guard differs from its original agent evidence.");
                        normalized.SelectorRecovery.GuardSnapshotPath = copiedGuard;
                    }
                    Require(JsonSerializer.Serialize(normalized, TestyJson.Options) == JsonSerializer.Serialize(actual, TestyJson.Options), "Flat AI action record differs from its original agent observation.");
                }
            }
            Require(flattened == run.Steps.Count, "Terminal AI run includes records absent from agent observations.");
        }
    }

    internal static bool Canonical(TestStep a, TestStep b) => JsonSerializer.Serialize(a, TestyJson.Options) == JsonSerializer.Serialize(b, TestyJson.Options);
    internal static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    internal static void RequireWithin(string path, string root)
    { Require(!string.IsNullOrWhiteSpace(path) && Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Child evidence escaped this worker's artifact directory."); }
    internal static void ValidatePng(string path)
    {
        Require(File.Exists(path) && new FileInfo(path).Length is > 100 and < 100_000_000, "Screenshot is absent or outside size limits.");
        using var stream = File.OpenRead(path);
        var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Require(frame.PixelWidth > 1 && frame.PixelHeight > 1, "Screenshot has invalid dimensions.");
    }
    internal static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    internal static string WorkloadHash(TestCase test)
    {
        var canonical = TestyJson.Clone(test); canonical.UpdatedAt = DateTimeOffset.UnixEpoch;
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical, TestyJson.Options)));
    }
    internal static void DurableWrite<T>(string path, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, TestyJson.Options);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { stream.Write(bytes); stream.Flush(flushToDisk: true); }
        File.Move(temporary, path, overwrite: true);
    }
    internal static ProcessStartInfo CreateSelfStartInfo()
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate current CLI host.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(WorkerCommand).Assembly.Location);
        CortexModelBridge.BindChild(start);
        return start;
    }
    private static async Task DrainAsync(StreamReader reader, string path)
    {
        await using var file = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        var buffer = new char[8192]; long retained = 0;
        while (true)
        {
            int count = await reader.ReadAsync(buffer); if (count == 0) break;
            if (retained < 64 * 1024 * 1024) { await file.WriteAsync(buffer.AsMemory(0, count)); retained += count; }
        }
        if (retained >= 64 * 1024 * 1024) await file.WriteLineAsync("\nOUTPUT LIMIT EXCEEDED; terminal JSON cannot be accepted.");
    }
    internal static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        try { await process.WaitForExitAsync().WaitAsync(timeout); return true; }
        catch (TimeoutException) { return process.HasExited; }
    }
    public static async Task WaitForStartAsync(string? startFile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(startFile)) return;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct); bounded.CancelAfter(TimeSpan.FromSeconds(10));
        while (!File.Exists(startFile)) await Task.Delay(50, bounded.Token);
        bounded.Token.ThrowIfCancellationRequested();
    }
    public static IDisposable MonitorCancellation(string? cancelFile, CancellationTokenSource cancellation) => new CancellationFileMonitor(cancelFile, cancellation);
    private sealed class CancellationFileMonitor : IDisposable
    {
        private readonly CancellationTokenSource lifetime = new();
        public CancellationFileMonitor(string? path, CancellationTokenSource cancellation)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            _ = Task.Run(async () =>
            {
                try { while (!lifetime.IsCancellationRequested) { if (File.Exists(path)) { cancellation.Cancel(); return; } await Task.Delay(100, lifetime.Token); } }
                catch (OperationCanceledException) { }
            });
        }
        public void Dispose() { lifetime.Cancel(); /* The polling task owns its token until it returns. */ }
    }
    private sealed class WorkerUnavailableException(string message) : Exception(message);
}

internal sealed class BuildFingerprint
{
    public string Scope => "Top-level .exe/.dll/.deps.json/.runtimeconfig.json files in the executable directory, bounded to 1000 files and 1 GiB. Nested plugins and externally selected runtime dependencies are not covered.";
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Directory { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public List<BuildFingerprintFile> Files { get; set; } = [];
    public static BuildFingerprint Capture(string executable)
    {
        var result = new BuildFingerprint { Directory = Path.GetDirectoryName(Path.GetFullPath(executable))! };
        var paths = System.IO.Directory.EnumerateFiles(result.Directory, "*", SearchOption.TopDirectoryOnly)
            .Where(p => Path.GetExtension(p).Equals(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(p).Equals(".dll", StringComparison.OrdinalIgnoreCase)
                || p.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
            .Take(1001).ToList();
        WorkerCommand.Require(paths.Count is > 0 and <= 1000, "Binary fingerprint exceeds its 1000-file bound or contains no binaries.");
        long total = 0;
        foreach (string path in paths.OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var file = new FileInfo(path); total = checked(total + file.Length);
            WorkerCommand.Require(total <= 1024L * 1024 * 1024, "Binary fingerprint exceeds its 1 GiB bound.");
            result.Files.Add(new() { Name = file.Name, Length = file.Length, Sha256 = WorkerCommand.HashFile(path) });
        }
        result.Sha256 = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(result.Files, TestyJson.Options)));
        return result;
    }
}
internal sealed class BuildFingerprintFile
{
    public string Name { get; set; } = "";
    public long Length { get; set; }
    public string Sha256 { get; set; } = "";
}

internal sealed class DesktopPreflight
{
    public bool Available { get; set; }
    public string Reason { get; set; } = "";
    public int ProcessSession { get; set; }
    public uint ActiveConsoleSession { get; set; }
    public string WindowStation { get; set; } = "";
    public string ThreadDesktop { get; set; } = "";
    public string InputDesktop { get; set; } = "";
    public int SessionState { get; set; } = -1;
    public int InputDesktopError { get; set; }
    /// <summary>Same observation the pump and background agent use before claiming local work.</summary>
    public static DesktopPreflight Observe()
    {
        var state = InteractiveDesktop.Observe();
        return new DesktopPreflight { Available = state.Available, Reason = state.Reason, ProcessSession = state.ProcessSession, ActiveConsoleSession = state.ActiveConsoleSession,
            WindowStation = state.WindowStation, ThreadDesktop = state.ThreadDesktop, InputDesktop = state.InputDesktop, SessionState = state.SessionState, InputDesktopError = state.InputDesktopError };
    }
}

internal sealed class OwnedProcessJob : IDisposable
{
    private nint handle;
    public OwnedProcessJob()
    {
        handle = CreateJobObjectW(0, null); if (handle == 0) throw new Win32Exception();
        var limits = new ExtendedLimit { Basic = new BasicLimit { Flags = 0x2000 } }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimit>())) { int error = Marshal.GetLastWin32Error(); Dispose(); throw new Win32Exception(error); }
    }
    public Process StartTarget(ProcessStartInfo start)
    {
        // Windows 10+: job association is part of process creation, before any target startup code runs.
        // https://devblogs.microsoft.com/oldnewthing/20230209-00/?p=107812
        nuint size = 0; InitializeProcThreadAttributeList(0, 1, 0, ref size);
        nint attributes = Marshal.AllocHGlobal(checked((int)size)), jobList = Marshal.AllocHGlobal(nint.Size);
        bool initialized = false; nint environment = 0;
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception();
            initialized = true; Marshal.WriteIntPtr(jobList, handle);
            if (!UpdateProcThreadAttribute(attributes, 0, (nint)0x2000D, jobList, (nuint)nint.Size, 0, 0)) throw new Win32Exception();
            var info = new StartupInfoEx { Startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfoEx>(), Flags = 1, ShowWindow = 0 }, Attributes = attributes };
            var command = new StringBuilder(string.Join(" ", new[] { start.FileName }.Concat(start.ArgumentList).Select(QuoteArgument)));
            environment = TargetEnvironment(start);
            if (!CreateProcessW(start.FileName, command, 0, 0, false, 0x00080000 | 0x08000000 | 0x00000400, environment, start.WorkingDirectory, ref info, out var created)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Owned target process/runtime could not be started.");
            try { return Process.GetProcessById((int)created.ProcessId); }
            finally { CloseHandle(created.Thread); CloseHandle(created.Process); }
        }
        finally { if (initialized) DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); Marshal.FreeHGlobal(jobList); if (environment != 0) Marshal.FreeHGlobal(environment); }
    }
    /// <summary>
    /// Starts a desktop program with its window shown. It inherits none of this process's handles (so it never keeps a protocol pipe open), and
    /// with <paramref name="job"/> it belongs to that job from its first instruction; without one it outlives this process.
    /// </summary>
    public static Process StartVisible(ProcessStartInfo start, OwnedProcessJob? job)
    {
        nint attributes = 0, jobList = 0, environment = 0; bool initialized = false;
        try
        {
            if (job is not null)
            {
                nuint size = 0; InitializeProcThreadAttributeList(0, 1, 0, ref size);
                attributes = Marshal.AllocHGlobal(checked((int)size)); jobList = Marshal.AllocHGlobal(nint.Size);
                if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception();
                initialized = true; Marshal.WriteIntPtr(jobList, job.handle);
                if (!UpdateProcThreadAttribute(attributes, 0, (nint)0x2000D, jobList, (nuint)nint.Size, 0, 0)) throw new Win32Exception();
            }
            // Without an attribute list this is a plain STARTUPINFO: its size says so and EXTENDED_STARTUPINFO_PRESENT stays off.
            var info = new StartupInfoEx { Startup = new StartupInfo { Size = (uint)(job is null ? Marshal.SizeOf<StartupInfo>() : Marshal.SizeOf<StartupInfoEx>()) }, Attributes = attributes };
            var command = new StringBuilder(string.Join(" ", new[] { start.FileName }.Concat(start.ArgumentList).Select(QuoteArgument)));
            environment = TargetEnvironment(start);
            if (!CreateProcessW(start.FileName, command, 0, 0, false, (job is null ? 0u : 0x00080000u) | 0x00000400, environment, string.IsNullOrEmpty(start.WorkingDirectory) ? null : start.WorkingDirectory, ref info, out var created)) throw new Win32Exception();
            try
            {
                var process = Process.GetProcessById((int)created.ProcessId);
                try { _ = process.SafeHandle; } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { } // keeps the exit code readable after the process ends
                return process;
            }
            finally { CloseHandle(created.Thread); CloseHandle(created.Process); }
        }
        finally { if (initialized) DeleteProcThreadAttributeList(attributes); if (attributes != 0) Marshal.FreeHGlobal(attributes); if (jobList != 0) Marshal.FreeHGlobal(jobList); if (environment != 0) Marshal.FreeHGlobal(environment); }
    }
    /// <summary>CreateProcessW must receive the scrubbed Unicode block explicitly; a null pointer inherits the unsanitized parent environment.</summary>
    private static nint TargetEnvironment(ProcessStartInfo start)
    {
        CortexModelBridge.ScrubTarget(start);
        var entries = start.Environment.Where(pair => pair.Value is not null).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key + "=" + pair.Value);
        return Marshal.StringToHGlobalUni(string.Join('\0', entries) + "\0\0");
    }
    internal static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            if (ch == '"') result.Append('\\', slashes * 2 + 1).Append('"');
            else result.Append('\\', slashes).Append(ch);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    public void Add(Process process) { if (!AssignProcessToJobObject(handle, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot assign owned process to cleanup job. Execution gate remains closed."); }
    public async Task<bool> StopAndVerifyAsync()
    {
        if (!TerminateJobObject(handle, 1)) throw new Win32Exception();
        var timer = Stopwatch.StartNew();
        while (true)
        {
            if (!QueryInformationJobObject(handle, 1, out var accounting, (uint)Marshal.SizeOf<BasicAccounting>(), 0)) throw new Win32Exception();
            if (accounting.ActiveProcesses == 0) return true;
            if (timer.Elapsed > TimeSpan.FromSeconds(3)) return false;
            await Task.Delay(25);
        }
    }
    public void Dispose() { if (handle != 0) { CloseHandle(handle); handle = 0; } }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { public uint Size; public nint Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags; public ushort ShowWindow, ReservedSize; public nint ReservedBytes, StandardInput, StandardOutput, StandardError; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicAccounting { public long UserTime, KernelTime, ThisPeriodUserTime, ThisPeriodKernelTime; public uint PageFaultCount, TotalProcesses, ActiveProcesses, TerminatedProcesses; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimit { public long PerProcessUserTime, PerJobUserTime; public uint Flags; public nuint MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperation, WriteOperation, OtherOperation, ReadTransfer, WriteTransfer, OtherTransfer; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit { public BasicLimit Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(nint job, int informationClass, ref ExtendedLimit information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(nint job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(nint job, int information, out BasicAccounting data, uint length, nint returnLength);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(nint attributes, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(nint attributes, uint flags, nint attribute, nint value, nuint size, nint previous, nint returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(nint attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string application, StringBuilder commandLine, nint processAttributes, nint threadAttributes, bool inheritHandles, uint flags, nint environment, string? directory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
