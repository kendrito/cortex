namespace Testy.Core;

public sealed class ProviderConnectionResult
{
    public bool Passed { get; set; }
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool NativeInputQualified { get; set; }
}

public static class ProviderConnectionCheck
{
    /// <summary>One harmless model planning request: no project content, desktop capture, tools, or input.</summary>
    public static async Task<ProviderConnectionResult> RunAsync(ProviderSettings settings, string workspace, CancellationToken ct = default)
    {
        var copy = TestyJson.Clone(settings);
        var result = new ProviderConnectionResult { Provider = copy.Kind.ToString(), Model = copy.Model };
        if (copy.Kind == ProviderKind.Offline)
        {
            result.Message = "Offline parser is available. No AI provider was contacted or qualified.";
            return result;
        }
        copy.ProjectTools = null; copy.SupportsImages = false; copy.NativeComputerUse = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var planner = PlannerFactory.Create(copy, workspace);
            var plan = await planner.CreatePlanAsync(new PlanningRequest
            {
                Instructions = "This is a connection and structured-output test. No desktop exists and nothing will execute. Return a test named Connection check containing one assertExists step with selector id:ConnectionCheck and a 1000 ms timeout. Do not request project access or invent application behavior.",
                Snapshot = new UiSnapshot()
            }, timeout.Token).WaitAsync(timeout.Token);
            TestValidator.Validate(plan);
            if (plan.Steps.Count != 1 || plan.Steps[0].Action != StepAction.AssertExists || plan.Steps[0].Selector != "id:ConnectionCheck")
                throw new InvalidDataException("The endpoint answered but did not preserve the harmless connection-check contract.");
            result.Passed = true;
            result.Message = "The endpoint returned valid structured planning output. No application was opened or operated. Native computer input and production workflows require separate qualification.";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { result.Message = "Connection validation timed out after 60 seconds. No application input occurred."; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var message = ex.Message;
            string? secret = null;
            try { secret = ProviderCredentialStore.Resolve(copy); } catch (Exception) { /* Preserve the original connection failure if the vault is unavailable. */ }
            if (!string.IsNullOrEmpty(secret)) message = message.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            result.Message = message.Length > 1500 ? message[..1500] : message;
        }
        return result;
    }
}
