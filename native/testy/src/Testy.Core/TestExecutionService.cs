namespace Testy.Core;

/// <summary>Single entry point for the UI/CLI. AI-directed execution is the default setting.</summary>
public sealed class TestExecutionService(ITargetDriver driver, ProviderSettings settings, string artifactsRoot,
    IComputerActionExecutor? nativeExecutor = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    /// <summary>Optional evidence review after replay steps. AI-directed runs already receive per-action model feedback.</summary>
    public Func<RunResult, CancellationToken, Task>? StepObserver { get; set; }
    public async Task<RunResult> RunAsync(TestCase test, IProgress<RunProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!await gate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("This execution service already has an active run.");
        try
        {
            if (settings.AiDirectedExecution)
            {
                if (settings.Kind == ProviderKind.Offline)
                    throw new InvalidOperationException("Offline mode has no LLM. Choose Codex, OpenAI, or a compatible model for AI-directed execution, or explicitly select deterministic replay.");
                return await new AiTestRunner(driver, settings, artifactsRoot, nativeExecutor).RunAsync(test, progress, cancellationToken);
            }
            return await new TestRunner(driver, artifactsRoot) { StepObserver = StepObserver }.RunAsync(test, progress, cancellationToken);
        }
        finally { gate.Release(); }
    }
}
