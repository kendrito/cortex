using System.Text.Json;

namespace Testy.Core;

/// <summary>
/// Executes a saved test through a model-governed, one-action-per-turn computer-use session.
/// The model may choose how to interact, but cannot weaken the saved assertions or grant a pass.
/// </summary>
public sealed class AiTestRunner
{
    private readonly ITargetDriver driver;
    private readonly ProviderSettings settings;
    private readonly string artifactsRoot;
    private readonly IComputerActionExecutor? nativeExecutor;
    private readonly Func<string, IComputerAgent>? agentFactory;
    private readonly SemaphoreSlim gate = new(1, 1);

    public AiTestRunner(ITargetDriver driver, ProviderSettings settings, string artifactsRoot,
        IComputerActionExecutor? nativeExecutor = null, Func<string, IComputerAgent>? agentFactory = null)
    {
        this.driver = driver;
        this.settings = TestyJson.Clone(settings);
        this.artifactsRoot = Path.GetFullPath(artifactsRoot);
        this.nativeExecutor = nativeExecutor;
        this.agentFactory = agentFactory;
    }

    public async Task<RunResult> RunAsync(TestCase test, IProgress<RunProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateExecutionCore(test, settings, agentFactory is null);
        if (driver.Target is null) throw new InvalidOperationException("Attach the application before starting an AI-directed test.");
        if (!await gate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("This runner already has an active test.");
        try { return await RunCoreAsync(TestyJson.Clone(test), progress, cancellationToken); }
        finally { gate.Release(); }
    }

    /// <summary>Checks a saved AI workflow before any model request or target input. Suites must validate every case before starting.</summary>
    public static void ValidateExecution(TestCase test, ProviderSettings settings) => ValidateExecutionCore(test, settings, true);

    private static void ValidateExecutionCore(TestCase test, ProviderSettings settings, bool validateProvider)
    {
        ArgumentNullException.ThrowIfNull(settings);
        TestValidator.Validate(test);
        if (!test.Steps.Any(s => TestValidator.IsAssertion(s.Action)))
            throw new InvalidDataException("An AI-directed test needs at least one saved assertion. Add an expected result so completion can be verified independently.");
        if (settings.MaximumAgentTurns < 1 || settings.MaximumAgentTurns > AgentLimits.Maximum(settings)) throw new InvalidDataException($"The AI turn budget must be1–{AgentLimits.Maximum(settings)} for this provider mode.");
        if (settings.MaximumProviderRetries is < 0 or > 3 || settings.ProviderRetryDelayMs is < 0 or > 5000) throw new InvalidDataException("Invalid bounded provider retry configuration.");
        if (settings.ProjectTools?.Enabled == true) ProjectToolSession.ValidateConfiguration(settings.ProjectTools);
        if (!validateProvider) return; // Injected agents provide their own provider contract; deterministic definition checks still apply.
        if (!Enum.IsDefined(settings.Kind) || settings.Kind == ProviderKind.Offline)
            throw new InvalidDataException("AI-directed execution requires Codex or an API provider. Offline mode requires explicit deterministic replay.");
        if (settings.Kind == ProviderKind.OpenAI && settings.NativeComputerUse)
        {
            if (!settings.SupportsImages) throw new InvalidDataException("Native computer mode requires screenshot sharing.");
            if (test.Steps.Any(s => s.SelectorAlternatives.Count > 0)) throw new InvalidDataException("Native computer mode does not support approved selector alternatives. Use local semantic tools for this explicitly recovery-enabled saved workflow; no input was dispatched.");
            foreach (var step in test.Steps.Where(s => s.Action == StepAction.Wait))
            {
                var duration = step.Value.Length == 0 ? step.TimeoutMs : int.Parse(step.Value);
                if (duration > 5000) throw new InvalidDataException($"Saved native wait '{step.Title}' requires {duration} ms, but the native executor supports at most 5000 ms per action. Use local tools or separate explicit saved wait steps; no actions were executed.");
            }
        }
        var requiredCommands = settings.ProjectTools?.Enabled == true ? settings.ProjectTools.RequiredBeforeUiCommands.Count : 0;
        var refreshTurns = requiredCommands == 0 || settings.Kind == ProviderKind.OpenAI && settings.NativeComputerUse && test.Steps[0].Action == StepAction.Screenshot ? 0 : 1;
        var minimumTurns = test.Steps.Count + 1 + requiredCommands + refreshTurns;
        if (settings.MaximumAgentTurns < minimumTurns)
            throw new InvalidDataException($"This saved workflow requires at least {minimumTurns} model turns (saved steps, completion and any required project commands/fresh observation); configured budget is {settings.MaximumAgentTurns}. No actions were executed.");
    }

    private async Task<RunResult> RunCoreAsync(TestCase test, IProgress<RunProgress>? progress, CancellationToken ct)
    {
        var run = new RunResult { TestId = test.Id, TestName = test.Name, Target = TestyJson.Clone(driver.Target!), Status = RunStatus.Running };
        run.ArtifactDirectory = Path.Combine(artifactsRoot, run.StartedAt.ToString("yyyyMMdd-HHmmss") + "-ai-" + run.Id[..8]);
        Directory.CreateDirectory(run.ArtifactDirectory);
        WorkspaceStore.WriteAtomic(Path.Combine(run.ArtifactDirectory, "requested-test.json"), test);
        var seen = new HashSet<ComputerToolObservation>(ReferenceEqualityComparer.Instance);
        void Report(StepResult? step, string message) => progress?.Report(new RunProgress { Run = TestyJson.Clone(run), Step = step is null ? null : TestyJson.Clone(step), Message = message });
        void Observe(ComputerToolObservation observation)
        {
            if (!seen.Add(observation)) return;
            if (observation.Project is not null)
            {
                if (observation.Execution is not null || observation.NativeAction is not null || observation.SavedStepId.Length != 0)
                    throw new InvalidDataException("Project evidence cannot also claim a UI action or saved-step execution.");
                run.ProjectEvidence.Add(TestyJson.Clone(observation.Project));
                WorkspaceStore.WriteAtomic(Path.Combine(run.ArtifactDirectory, "run.json"), run);
                Report(null, $"Project tool {observation.ToolName}: {observation.Project.Status}. {observation.Message}");
                return;
            }
            if (driver.Target?.ProcessId != run.Target.ProcessId) throw new InvalidOperationException("The attached application changed during the AI session.");
            var sourceSteps = observation.Execution?.Steps ?? [];
            var isAction = observation.NativeAction is not null || observation.ToolName is "perform_ui_action" or "verify_ui_assertion" or "perform_saved_step" or "perform_saved_step_alternate" or "verify_saved_assertion" or "perform_saved_control_step";
            if (isAction && sourceSteps.Count == 0)
            {
                sourceSteps = [new StepResult
                {
                    Step = new TestStep { Title = "Missing action evidence", Action = StepAction.Screenshot },
                    Status = RunStatus.Failed,
                    Message = "The agent reported an action without an executed step record. Its execution and evidence cannot be verified.",
                    FailureDiagnostics = [FailureDiagnostics.Create(FailureCategory.EvidenceUnavailable, "An agent action had no executed step record. Input delivery is not established.", actionOutcome: ActionOutcome.Unknown)]
                }];
            }
            foreach (var source in sourceSteps)
            {
                var step = TestyJson.Clone(source);
                step.Index = run.Steps.Count;
                step.Snapshot ??= observation.Snapshot is null ? null : TestyJson.Clone(observation.Snapshot);
                if (string.IsNullOrWhiteSpace(step.ScreenshotPath)) step.ScreenshotPath = observation.ScreenshotPath;
                if (step.Status == RunStatus.Passed && (step.Snapshot is null || !File.Exists(step.ScreenshotPath) || new FileInfo(step.ScreenshotPath).Length == 0))
                {
                    step.Status = RunStatus.Failed;
                    step.Message += " The action has no complete screenshot and control-snapshot evidence.";
                    step.FailureDiagnostics.Add(FailureDiagnostics.Create(FailureCategory.EvidenceUnavailable, "The recorded action has no complete screenshot and control-snapshot evidence.", step.Step,
                        actionOutcome: TestValidator.IsAssertion(step.Step.Action) || step.Step.Action is StepAction.Wait or StepAction.Screenshot ? ActionOutcome.NotDispatched : ActionOutcome.Unknown));
                }
                if (step.Snapshot != null) WorkspaceStore.WriteAtomic(Path.Combine(run.ArtifactDirectory, $"step-{step.Index + 1:000}.json"), step.Snapshot);
                if (step.SelectorRecovery is not null && File.Exists(step.SelectorRecovery.GuardSnapshotPath))
                {
                    var guardCopy = Path.Combine(run.ArtifactDirectory, $"step-{step.Index + 1:000}-recovery.json");
                    File.Copy(step.SelectorRecovery.GuardSnapshotPath, guardCopy, true); step.SelectorRecovery.GuardSnapshotPath = guardCopy;
                }
                if (!string.IsNullOrWhiteSpace(step.ScreenshotPath) && File.Exists(step.ScreenshotPath))
                {
                    var copy = Path.Combine(run.ArtifactDirectory, $"step-{step.Index + 1:000}.png");
                    File.Copy(step.ScreenshotPath, copy, true); step.ScreenshotPath = copy;
                }
                run.Steps.Add(step);
                FailureDiagnostics.Refresh(run);
                WorkspaceStore.WriteAtomic(Path.Combine(run.ArtifactDirectory, "run.json"), run);
                Report(step, $"Observation {step.Index + 1}: {step.Status} · {step.Step.Title}. Reviewing the result before the next decision.");
            }
        }
        Report(null, "AI is inspecting the application and deciding the first action…");
        try
        {
            var agent = agentFactory?.Invoke(run.ArtifactDirectory)
                ?? ComputerAgentFactory.Create(settings, driver, run.ArtifactDirectory, nativeExecutor, test);
            var instructions = """
                Execute the saved functional test below, one computer-use action per model turn.
                The user's intent and workflow must be preserved. Inspect each returned screenshot and UI tree before deciding the next action.
                Use native computer interaction when supplied for ordinary physical actions; call perform_saved_control_step with the immutable step ID for a saved advanced control operation when that explicit hybrid tool is supplied. Otherwise call perform_saved_step with the next saved step ID. You may add observations when needed.
                The listed assertions are immutable acceptance criteria. Issue every listed assertion through the provided deterministic assertion tool (verify_saved_assertion in bound native mode, perform_saved_step in bound local mode) with the exact saved step ID, in the listed order. The tool supplies canonical action, selector, and value. Do not substitute weaker checks, edit expected values, or infer success from a screenshot alone.
                Do not skip required interactions merely because the app starts in a similar state. Stop and explain if the workflow cannot be completed or an assertion fails.
                Call only one action per turn. When every required assertion has passed, finish with a concise explanation citing the actual evidence.
                SAVED TEST:
                """ + JsonSerializer.Serialize(test, TestyJson.Options);
            var result = await agent.RunAsync(instructions, settings.MaximumAgentTurns, new InlineProgress<ComputerToolObservation>(Observe), ct);
            foreach (var observation in result.Observations) Observe(observation);
            if (run.ProjectEvidence.Count > 0 || settings.ProjectTools?.Enabled == true)
                ProjectEvidenceVerifier.ValidateSequence(run.ProjectEvidence, run.ArtifactDirectory, settings.ProjectTools,
                    requireComplete: result.Completed && result.Status is not (RunStatus.Failed or RunStatus.Cancelled));
            run.AiAnalysis = result.Message;
            run.FailureDiagnostics.AddRange(result.FailureDiagnostics.Select(TestyJson.Clone));
            if (result.Status == RunStatus.Cancelled || ct.IsCancellationRequested) run.Status = RunStatus.Cancelled;
            else if (result.Status == RunStatus.Failed || run.ProjectEvidence.Any(p => p.BlocksFurtherActions) || run.Steps.Any(s => s.Status is RunStatus.Failed or RunStatus.Cancelled))
            {
                run.Status = RunStatus.Failed;
                var failed = run.Steps.FirstOrDefault(s => s.Status is RunStatus.Failed or RunStatus.Cancelled);
                if (failed is not null) run.Summary = $"Failed at action {failed.Index + 1}, '{failed.Step.Title}': {failed.Message}";
                else if (result.FailureDiagnostics.Count == 0) run.FailureDiagnostics.Add(FailureDiagnostics.Create(FailureCategory.AgentFailure, "The model session returned failure before the saved workflow could be verified."));
            }
            else
            {
                var coverage = VerifyAssertions(test, run.Steps);
                var workflow = SavedWorkflowVerifier.Verify(test, result.Observations);
                if (!result.Completed) { run.Status = RunStatus.Failed; run.Summary = "The model stopped without completing the test."; run.FailureDiagnostics.Add(FailureDiagnostics.Create(FailureCategory.AgentFailure, run.Summary)); }
                else if (!coverage.Complete) { run.Status = RunStatus.Failed; run.Summary = coverage.Message; run.FailureDiagnostics.Add(FailureDiagnostics.Create(FailureCategory.WorkflowNotVerified, coverage.Message)); }
                else if (!workflow.Complete) { run.Status = RunStatus.Failed; run.Summary = workflow.Message; run.FailureDiagnostics.Add(FailureDiagnostics.Create(FailureCategory.WorkflowNotVerified, workflow.Message)); }
                else { run.Status = RunStatus.Passed; run.Summary = $"Passed: saved steps verified: {test.Steps.Count}; assertions verified: {coverage.RequiredCount}; AI turns: {result.ModelTurns}."; }
            }
            if (string.IsNullOrEmpty(run.Summary))
                run.Summary = $"{run.Status}: {result.Message}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { run.Status = RunStatus.Cancelled; run.Summary = "AI-directed execution cancelled. Completed observations have been preserved."; run.FailureDiagnostics.Add(FailureDiagnostics.Create(FailureCategory.Cancelled, run.Summary)); }
        catch (Exception ex)
        { run.Status = RunStatus.Failed; run.Summary = "AI-directed execution stopped: " + ex.Message; run.FailureDiagnostics.Add(FailureDiagnostics.FromException(ex)); }
        run.FinishedAt = DateTimeOffset.UtcNow;
        FailureDiagnostics.Refresh(run);
        // Final report must not make missing acceptance evidence look like an all-green action list.
        if (run.Status == RunStatus.Failed && !run.Steps.Any(s => s.Status == RunStatus.Failed))
            run.Steps.Add(new StepResult { Index = run.Steps.Count, Step = new TestStep { Title = "Verify saved acceptance criteria", Action = StepAction.Screenshot }, Status = RunStatus.Failed, Message = run.Summary, FailureDiagnostics = run.FailureDiagnostics.Where(d => d.StepIndex is null).ToList() });
        if (run.Status == RunStatus.Cancelled && !run.Steps.Any(s => s.Status == RunStatus.Cancelled))
            run.Steps.Add(new StepResult { Index = run.Steps.Count, Step = new TestStep { Title = "AI session cancelled", Action = StepAction.Screenshot }, Status = RunStatus.Cancelled, Message = run.Summary, FailureDiagnostics = run.FailureDiagnostics.Where(d => d.Category == FailureCategory.Cancelled).ToList() });
        TestRunner.WriteReports(run);
        Report(null, run.Summary);
        return run;
    }

    public static AssertionCoverage VerifyAssertions(TestCase test, IEnumerable<StepResult> executed)
    {
        var required = test.Steps.Where(s => TestValidator.IsAssertion(s.Action)).ToList();
        var observations = executed.Where(s => s.Status == RunStatus.Passed && TestValidator.IsAssertion(s.Step.Action)).Select(s => s.Step).ToList();
        var cursor = 0;
        foreach (var expected in required)
        {
            var found = false;
            while (cursor < observations.Count)
            {
                var actual = observations[cursor++];
                if (actual.Action == expected.Action && string.Equals(actual.Selector, expected.Selector, StringComparison.Ordinal) && string.Equals(actual.Value, expected.Value, StringComparison.Ordinal))
                { found = true; break; }
            }
            if (!found) return new AssertionCoverage(false, required.Count, $"Required assertion was not verified in order: '{expected.Title}' ({expected.Action} {expected.Selector}, expected '{expected.Value}'). Model completion cannot grant a pass.");
        }
        return new AssertionCoverage(required.Count > 0, required.Count, required.Count == 0 ? "No saved acceptance assertions were provided." : "Every saved assertion was verified in order.");
    }
    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T> { public void Report(T value) => callback(value); }
}

public sealed record AssertionCoverage(bool Complete, int RequiredCount, string Message);
