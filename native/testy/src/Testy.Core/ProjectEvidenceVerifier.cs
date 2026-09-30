using System.Security.Cryptography;
using System.Text.Json;

namespace Testy.Core;

/// <summary>Validates durable project observations without treating them as UI acceptance.</summary>
public static class ProjectEvidenceVerifier
{
    public static void ValidateRecord(ProjectToolResult record, string artifactsRoot)
    {
        ArgumentNullException.ThrowIfNull(record);
        TestValidator.ValidateId(record.Id);
        Require(ProjectToolSession.IsTool(record.ToolName), "Unknown project evidence tool.");
        Require(Enum.IsDefined(record.Status), "Unknown project evidence status.");
        Require(record.FinishedAt is not null && record.FinishedAt >= record.StartedAt, "Project evidence is not finalized.");
        Require(record.Request is { ValueKind: JsonValueKind.Object }, "Project evidence lacks its structured request.");
        var path = Within(record.EvidencePath, artifactsRoot);
        Require(File.Exists(path) && new FileInfo(path).Length is > 0 and <= 2_000_000, "Project evidence artifact is missing or oversized.");
        var bytes = File.ReadAllBytes(path);
        var digest = Convert.ToHexString(SHA256.HashData(bytes));
        Require(File.Exists(path + ".sha256") && File.ReadAllText(path + ".sha256").Trim() == digest, "Project evidence checksum does not match.");
        var stored = JsonSerializer.Deserialize<ProjectToolResult>(bytes, TestyJson.Options);
        Require(stored is not null && JsonSerializer.Serialize(stored, TestyJson.Options) == JsonSerializer.Serialize(record, TestyJson.Options), "Project evidence disagrees with its persisted record.");
        Require(record.Files is not null && record.Files.Count <= 10000, "Project file citation count is invalid.");
        foreach (var file in record.Files!)
        {
            Require(!string.IsNullOrWhiteSpace(file.Path) && !Path.IsPathRooted(file.Path) && !file.Path.Contains(':')
                && !file.Path.Replace('\\', '/').Split('/').Any(s => s == ".."), "Project citation is not relative to its selected root.");
            Require(file.Sha256.Length == 64 && file.Sha256.All(Uri.IsHexDigit) && file.Bytes >= 0, "Project citation has an invalid file fingerprint.");
            Require(file.StartLine is null or >= 1 && (file.EndLine is null || file.StartLine is not null && file.EndLine >= file.StartLine), "Project citation line range is invalid.");
        }
        if (record.Command is { } command)
        {
            Require(record.ToolName == "project_run_command", "A non-command project record contains command execution.");
            Require(Enum.IsDefined(command.Status) && command.FinishedAt is not null && command.FinishedAt >= command.StartedAt,
                "Project command evidence is not terminal.");
            Require(command.StartedAt >= record.StartedAt && command.FinishedAt <= record.FinishedAt,
                "Project command timestamps fall outside the recorded tool call.");
            var successful = command.Status == ProjectCommandStatus.Completed && command.ExitCode == 0 && command.CleanupComplete;
            Require(!successful || command.ProcessId is > 0, "A successful project command lacks its launched process identity.");
            Require(successful == (record.Status == ProjectToolStatus.Succeeded), "Project tool and command outcomes disagree.");
            Require(successful ? !record.BlocksFurtherActions : record.BlocksFurtherActions, "Project command blocking state contradicts its observed result.");
        }
        else Require(record.ToolName != "project_run_command" || record.Status != ProjectToolStatus.Succeeded, "A successful project command has no process evidence.");
    }

    public static void ValidateSequence(IReadOnlyList<ProjectToolResult> records, string artifactsRoot,
        ProjectToolSettings? configuration = null, bool requireComplete = false)
    {
        Require(records.Count <= 200, "Project observation count exceeds its maximum.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var executed = new HashSet<string>(StringComparer.Ordinal);
        var successful = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset? previousFinished = null;
        foreach (var record in records)
        {
            ValidateRecord(record, artifactsRoot);
            Require(ids.Add(record.Id), "Project evidence ID was duplicated.");
            Require(previousFinished is null || record.StartedAt >= previousFinished, "Project evidence is reordered or overlapping.");
            previousFinished = record.FinishedAt;
            if (record.Command is null) continue;
            Require(record.Request!.Value.TryGetProperty("commandId", out var id) && id.ValueKind == JsonValueKind.String, "Command evidence lacks its catalog ID.");
            var name = id.GetString()!;
            Require(executed.Add(name), "A configured command was executed repeatedly in one session.");
            if (configuration is not null) Require(configuration.Enabled && configuration.Commands.Any(c => c.Id == name), "Executed command is outside the configured catalog.");
            if (record.Status == ProjectToolStatus.Succeeded) successful.Add(name);
        }
        if (configuration is not null)
        {
            Require(records.Count <= configuration.MaximumCalls && executed.Count <= configuration.MaximumCommandInvocations, "Project evidence exceeds configured budgets.");
            if (requireComplete) Require(configuration.RequiredBeforeUiCommands.All(successful.Contains), "Required project commands were omitted or failed.");
        }
        if (requireComplete) Require(records.All(r => !r.BlocksFurtherActions), "A failed project command prevents a passing workflow.");
    }

    private static string Within(string path, string root)
    {
        Require(!string.IsNullOrWhiteSpace(path), "Project evidence path is empty.");
        var full = Path.GetFullPath(path);
        Require(full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Project evidence escaped its artifact directory.");
        return full;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
