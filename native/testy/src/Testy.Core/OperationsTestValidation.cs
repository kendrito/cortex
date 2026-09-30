namespace Testy.Core;

/// <summary>Definition-only checks for queued saved-test runs. Paths are not required to exist yet:
/// a queue is revalidated on every load, and guest paths cannot be observed from the host.</summary>
public static class OperationsTestValidation
{
    /// <summary>Prefix resolved inside the guest to the staged, hash-verified Testy worker folder.</summary>
    public const string WorkerToken = "{worker}";

    public static void Validate(OperationsTestRequest request, OperationsTarget target)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Test);
        TestValidator.Validate(request.Test);
        if (request.TimeoutSeconds is < 1 or > 7200 || request.StartupTimeoutSeconds is < 1 or > 120 || request.ShutdownGraceSeconds is < 1 or > 30)
            throw new InvalidDataException("Test run budget must be 1–7200 seconds, startup 1–120 and shutdown grace 1–30.");
        if (request.TargetArguments is null || request.TargetArguments.Count > 64 || request.TargetArguments.Any(a => a is null || a.Length > 4096 || a.Contains('\0')))
            throw new InvalidDataException("Target arguments must be at most 64 literal strings, at most 4096 characters each, without NUL.");
        ValidateExecutable(request, target);
        if (request.Provider is not { } provider) return; // Explicit deterministic replay.
        if (!Enum.IsDefined(provider.Kind) || provider.Kind == ProviderKind.Offline || !provider.AiDirectedExecution)
            throw new InvalidDataException("AI test runs need an enabled AI provider with AI-directed execution. Queue the test as replay for deterministic execution.");
        if (provider.ProjectTools is { Enabled: true }) throw new InvalidDataException("Project-aware AI runs use lifecycle profiles; a queued test run has no repository access.");
        if (!target.IsLocal && provider.Kind == ProviderKind.Codex)
            throw new InvalidDataException("The Codex bridge runs only on this PC. Use an OpenAI or OpenAI-compatible endpoint for VM runs.");
        if (!target.IsLocal && !CortexModelBridge.Enabled && Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out var endpoint) && endpoint.IsLoopback)
            throw new InvalidDataException("A localhost model endpoint would point at the VM itself. Use an endpoint the VM can reach for VM runs.");
        AiTestRunner.ValidateExecution(request.Test, provider);
    }

    private static void ValidateExecutable(OperationsTestRequest request, OperationsTarget target)
    {
        var exe = request.Executable ?? "";
        if (exe.Length is 0 or > 1024 || exe.Contains('\0') || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The test target must name a Windows .exe.");
        if (target.IsLocal)
        {
            if (request.StageDirectory is not null) throw new InvalidDataException("Folder staging applies only to VM targets.");
            if (!Path.IsPathFullyQualified(exe) || exe.StartsWith(@"\\", StringComparison.Ordinal)) throw new InvalidDataException("A local test target must be an absolute local .exe path.");
            return;
        }
        if (request.StageDirectory is { } stage)
        {
            if (!Path.IsPathFullyQualified(stage) || stage.StartsWith(@"\\", StringComparison.Ordinal) || stage.Length > 1024)
                throw new InvalidDataException("The folder to copy into the VM must be an absolute local host path.");
            RequireRelative(exe, "With a copied folder, the VM target must be a relative .exe path inside that folder.");
            return;
        }
        if (exe.StartsWith(WorkerToken + "\\", StringComparison.Ordinal)) { RequireRelative(exe[(WorkerToken.Length + 1)..], "A {worker} target must stay inside the staged worker folder."); return; }
        if (!IsAbsoluteGuestPath(exe)) throw new InvalidDataException("A VM target must be an absolute guest path such as C:\\Apps\\App.exe, a {worker}\\ path, or relative to a copied folder.");
    }
    internal static bool IsAbsoluteGuestPath(string path) => path.Length > 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\'
        && !path.Split('\\').Skip(1).Any(p => p is "" or "." or "..") && path.IndexOfAny(['/', '"', '<', '>', '|', '?', '*']) < 0 && path.IndexOf(':', 2) < 0;
    private static void RequireRelative(string path, string message)
    {
        if (path.Length == 0 || Path.IsPathRooted(path) || path.Contains(':') || path.Contains('/') || path.IndexOfAny(['"', '<', '>', '|', '?', '*']) >= 0
            || path.Split('\\').Any(p => p is "" or "." or ".."))
            throw new InvalidDataException(message);
    }
}
