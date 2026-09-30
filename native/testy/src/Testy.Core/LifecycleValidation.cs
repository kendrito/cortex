namespace Testy.Core;

public static class LifecycleValidation
{
    public static void ValidateRequest(LifecycleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Profile);
        ArgumentNullException.ThrowIfNull(request.Provider);
        var p = request.Profile;
        if (p.Schema is not (LifecycleProfile.CurrentSchema or LifecycleProfile.LegacySchema)) throw new InvalidDataException("Unsupported lifecycle profile schema.");
        TestValidator.ValidateId(p.Id); TestValidator.Validate(request.Test);
        if (string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 200 || string.IsNullOrWhiteSpace(p.PreparationInstructions) || p.PreparationInstructions.Length > 30000)
            throw new InvalidDataException("Lifecycle name or preparation instructions are missing or too long.");
        if (!Path.IsPathFullyQualified(p.Executable) || !Path.GetExtension(p.Executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Lifecycle executable must be a resolved absolute Windows .exe path; it may be produced during preparation.");
        if (p.MaximumPreparationTurns is < 2 or > 80 || p.PreparationTimeoutSeconds is < 1 or > 7200 || p.WorkerTimeoutSeconds is < 1 or > 7200 || p.StartupTimeoutSeconds is < 1 or > 120 || p.ShutdownGraceSeconds is < 1 or > 30)
            throw new InvalidDataException("Lifecycle budget is outside its bounds: preparation2–80 turns, phase timeouts1–7200s, startup1–120s, shutdown1–30s.");
        if (request.Provider.Kind == ProviderKind.Offline || !Enum.IsDefined(request.Provider.Kind)) throw new InvalidDataException("Preparation requires a configured AI provider.");
        if (request.Provider.MaximumProviderRetries is < 0 or > 3 || request.Provider.ProviderRetryDelayMs is < 0 or > 5000)
            throw new InvalidDataException("Provider response retry budget must be 0–3 with delay 0–5000ms; commands and UI actions are never retried.");
        if (request.Provider.ProjectTools is not { Enabled: true } project) throw new InvalidDataException("Preparation requires an explicitly enabled project configuration.");
        if (project.RequiredBeforeUiCommands is null || project.Commands is null || project.RequiredBeforeUiCommands.Count == 0)
            throw new InvalidDataException("A lifecycle must declare at least one required preparation command.");
        if (p.MaximumPreparationTurns < project.RequiredBeforeUiCommands.Count + 2)
            throw new InvalidDataException("Preparation budget cannot cover required commands, repository inspection and model completion. No tools were invoked.");
        if (request.TargetArguments is null || request.TargetArguments.Count > 64 || request.TargetArguments.Any(a => a is null || a.Length > 4096 || a.Contains('\0')))
            throw new InvalidDataException("Target arguments must be at most64 literal strings, at most4096 characters each, without NUL.");
        if (!p.Replay)
        {
            var ui = UiProvider(request.Provider);
            if (!request.Test.Steps.Any(s => TestValidator.IsAssertion(s.Action))) throw new InvalidDataException("AI UI execution requires saved acceptance assertions.");
            if (ui.MaximumAgentTurns < request.Test.Steps.Count + 1 || ui.MaximumAgentTurns > AgentLimits.Maximum(ui)) throw new InvalidDataException("UI phase model turn budget cannot execute this saved workflow and complete, or exceeds its provider mode limit.");
        }
    }
    public static void ValidateEnvironment(LifecycleRequest request)
    {
        ValidateRequest(request);
        ProjectToolSession.ValidateConfiguration(request.Provider.ProjectTools!);
        if (!request.Profile.Replay) AiTestRunner.ValidateExecution(request.Test, UiProvider(request.Provider));
    }
    public static ProviderSettings UiProvider(ProviderSettings preparationProvider)
    {
        var settings = TestyJson.Clone(preparationProvider);
        if (settings.ProjectTools is { } project)
        {
            // Preparation provenance remains in its original stage. The attached UI session can inspect
            // source, but it cannot rebuild the loaded app or repeat a one-shot preparation command.
            project.Commands.Clear(); project.RequiredBeforeUiCommands.Clear();
        }
        return settings;
    }
}
