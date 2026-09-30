namespace Testy.Core;

public interface IComputerAgent
{
    Task<ComputerAgentResult> RunAsync(string instructions, int maximumTurns = 30, IProgress<ComputerToolObservation>? progress = null, CancellationToken cancellationToken = default);
}

public static class ComputerAgentFactory
{
    public static IComputerAgent Create(ProviderSettings settings, ITargetDriver driver, string artifactsDirectory, IComputerActionExecutor? nativeExecutor = null, TestCase? savedTest = null) => settings.Kind switch
    {
        ProviderKind.Codex => new CodexComputerAgent(settings, driver, artifactsDirectory, savedTest: savedTest),
        ProviderKind.OpenAI when settings.NativeComputerUse => new NativeComputerUseAgent(settings, driver, artifactsDirectory, nativeExecutor ?? throw new InvalidOperationException("OpenAI native computer use requires a local computer action executor."), savedTest: savedTest),
        ProviderKind.OpenAI or ProviderKind.Compatible => new ComputerUseAgent(settings, driver, artifactsDirectory, savedTest: savedTest),
        _ => throw new InvalidOperationException("Offline mode cannot make AI decisions. Select Codex or an API provider, or explicitly choose deterministic replay.")
    };
}
