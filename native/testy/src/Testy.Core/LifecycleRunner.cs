using System.Security.Cryptography;
using System.Text.Json;

namespace Testy.Core;

/// <summary>Preparation and owned UI-worker orchestration. Process ownership/acceptance are provided by the existing isolated worker.</summary>
public sealed class LifecycleRunner(
    Func<ProviderSettings, string, IProjectPreparationAgent> preparationFactory,
    Func<LifecycleRequest, string, CancellationToken, Task<LifecycleWorkerOutcome>> workerExecutor)
{
    public async Task<LifecycleResult> RunAsync(LifecycleRequest request, string artifactsRoot, CancellationToken cancellationToken = default)
    {
        request = TestyJson.Clone(request);
        LifecycleValidation.ValidateEnvironment(request);
        var result = new LifecycleResult();
        result.ArtifactDirectory = Path.Combine(Path.GetFullPath(artifactsRoot), $"lifecycle-{result.StartedAt:yyyyMMdd-HHmmss}-{result.Id[..8]}");
        Directory.CreateDirectory(result.ArtifactDirectory);
        var requestPath = Path.Combine(result.ArtifactDirectory, "request.json");
        LifecycleJournal.Write(requestPath, request);
        result.FrozenRequestSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(requestPath)));
        using var owner = new FileStream(Path.Combine(result.ArtifactDirectory, "active.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var resultPath = Path.Combine(result.ArtifactDirectory, "lifecycle-result.json");
        void Checkpoint(string stage, string message, bool sideEffects = false)
        {
            result.Checkpoints.Add(new LifecycleCheckpoint { Sequence = result.Checkpoints.Count, Stage = stage, Message = message, MayHaveSideEffects = sideEffects });
            LifecycleJournal.Write(Path.Combine(result.ArtifactDirectory, $"checkpoint-{result.Checkpoints.Count:000}.json"), result.Checkpoints[^1]);
            LifecycleJournal.Write(resultPath, result);
        }
        void RecoverPreparationOutcome()
        {
            if (result.Preparation is not null || result.Checkpoints.Any(c => c.Stage == "workerStarting")) return;
            var file = Path.Combine(result.ArtifactDirectory, "preparation", "preparation.json");
            try
            {
                if (File.Exists(file)) result.Preparation = JsonSerializer.Deserialize<PreparationResult>(File.ReadAllText(file), TestyJson.Options);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
            // A throwing preparation adapter did not supply a terminal account. Never infer safe retry.
            result.ActionOutcomeUnknown = result.Preparation is null || result.Preparation.ActionOutcomeUnknown || result.Preparation.PendingTool == "project_run_command";
            result.CleanupComplete = result.Preparation?.FinishedAt is not null && !result.ActionOutcomeUnknown && result.Preparation.ProjectEvidence.All(e => e.Command is null || e.Command.CleanupComplete);
        }
        try
        {
            Checkpoint("preparing", "AI preparation starts without a target process. Commands remain explicitly model-selected.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(request.Profile.PreparationTimeoutSeconds));
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            var preparationDirectory = Path.Combine(result.ArtifactDirectory, "preparation");
            result.Preparation = await preparationFactory(request.Provider, preparationDirectory).RunAsync(request.Profile.PreparationInstructions, request.Profile.MaximumPreparationTurns, combined.Token);
            result.ActionOutcomeUnknown = result.Preparation.ActionOutcomeUnknown;
            result.CleanupComplete = result.Preparation.ProjectEvidence.All(e => e.Command is null || e.Command.CleanupComplete) && !result.ActionOutcomeUnknown;
            ProjectEvidenceVerifier.ValidateSequence(result.Preparation.ProjectEvidence, preparationDirectory, request.Provider.ProjectTools, result.Preparation.Passed);
            if (!result.Preparation.Passed || combined.IsCancellationRequested)
            {
                result.Status = cancellationToken.IsCancellationRequested ? LifecycleStatus.Cancelled : deadline.IsCancellationRequested ? LifecycleStatus.TimedOut : LifecycleStatus.Failed;
                result.Message = "Target not launched. " + (combined.IsCancellationRequested ? "Preparation deadline or cancellation arrived before launch. " : "") + result.Preparation.Message; return result;
            }
            if (!File.Exists(request.Profile.Executable)) throw new InvalidDataException("Preparation completed but the configured target executable was not produced. No target was launched.");
            result.Status = LifecycleStatus.Prepared;
            Checkpoint("prepared", "Required preparation evidence is terminal and verified; target has not launched.");
            cancellationToken.ThrowIfCancellationRequested();
            if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(requestPath))) != result.FrozenRequestSha256) throw new InvalidDataException("Frozen lifecycle workload changed; target launch prohibited.");
            var uiRequest = TestyJson.Clone(request); uiRequest.Provider = LifecycleValidation.UiProvider(request.Provider);
            result.Status = LifecycleStatus.WorkerStarting; result.ActionOutcomeUnknown = true; result.CleanupComplete = false;
            Checkpoint("workerStarting", "Starting the owned worker after preparation. UI session receives read-only project access; preparation commands are not replayed.", true);
            var worker = await workerExecutor(uiRequest, Path.Combine(result.ArtifactDirectory, "worker"), cancellationToken);
            result.WorkerArtifactDirectory = worker.ArtifactDirectory; result.WorkerStatus = worker.Status; result.WorkerPassed = worker.Passed;
            result.CleanupComplete = worker.CleanupComplete; result.ActionOutcomeUnknown = worker.ActionOutcomeUnknown;
            result.Status = worker.Passed && worker.CleanupComplete && !worker.ActionOutcomeUnknown ? LifecycleStatus.Passed : worker.Status switch
            { "cancelled" => LifecycleStatus.Cancelled, "timedOut" => LifecycleStatus.TimedOut, _ => LifecycleStatus.Failed };
            result.Message = worker.Message;
            Checkpoint("workerFinished", "Worker returned its terminal acceptance and cleanup outcome.", worker.ActionOutcomeUnknown);
            return result;
        }
        catch (OperationCanceledException)
        { RecoverPreparationOutcome(); result.Status = LifecycleStatus.Cancelled; result.Message = "Lifecycle cancelled. No interrupted command or UI action will be replayed."; return result; }
        catch (Exception ex)
        { RecoverPreparationOutcome(); result.Status = result.Checkpoints.Any(c => c.Stage == "workerStarting") ? LifecycleStatus.NeedsReview : LifecycleStatus.Failed; result.Message = ProjectToolSession.Redact(ex.Message, ProjectAgentContext.Secrets(request.Provider)); return result; }
        finally
        {
            if (cancellationToken.IsCancellationRequested && result.Status == LifecycleStatus.Passed) result.Status = LifecycleStatus.Cancelled;
            result.FinishedAt = DateTimeOffset.UtcNow;
            Checkpoint("terminal", result.Message, result.ActionOutcomeUnknown);
        }
    }

    public static LifecycleResult InspectInterrupted(string lifecycleDirectory)
    {
        var root = Path.GetFullPath(lifecycleDirectory);
        // The exclusive lease prevents another process from relabeling a live lifecycle as interrupted.
        using var owner = new FileStream(Path.Combine(root, "active.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var path = Path.Combine(root, "lifecycle-result.json");
        var result = JsonSerializer.Deserialize<LifecycleResult>(File.ReadAllText(path), TestyJson.Options) ?? throw new InvalidDataException("Missing lifecycle state.");
        if (result.FinishedAt is not null) return result;
        var preparationPath = Path.Combine(root, "preparation", "preparation.json");
        if (File.Exists(preparationPath)) result.Preparation = JsonSerializer.Deserialize<PreparationResult>(File.ReadAllText(preparationPath), TestyJson.Options);
        result.Status = LifecycleStatus.NeedsReview;
        result.ActionOutcomeUnknown |= result.Preparation?.ActionOutcomeUnknown == true || result.Preparation?.PendingTool == "project_run_command" || result.Checkpoints.Any(c => c.Stage == "workerStarting");
        result.CleanupComplete = false;
        result.Message = "Interrupted lifecycle requires review. Preserved checkpoints do not authorize automatic resume or replay. An in-flight command/input may have completed; inspect owned-process cleanup and artifacts before explicitly starting a new run.";
        result.FinishedAt = DateTimeOffset.UtcNow;
        result.Checkpoints.Add(new LifecycleCheckpoint { Sequence = result.Checkpoints.Count, Stage = "interruptionReconciled", Message = result.Message, MayHaveSideEffects = result.ActionOutcomeUnknown });
        LifecycleJournal.Write(path, result);
        return result;
    }
}
