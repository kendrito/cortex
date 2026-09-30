using System.Security.Cryptography;
using System.Text.Json;
using Testy.Core;

namespace Testy.Tests;

internal static class ProjectEvidenceChecks
{
    public static IEnumerable<(string Name, Func<Task> Execute)> All()
    {
        yield return ("project evidence validates finalized real file records and mock command provenance", ValidRecords);
        yield return ("project evidence rejects missing changed and escaped artifacts or mismatched records", ArtifactIntegrity);
        yield return ("project evidence rejects malformed citations and incomplete record status", CitationIntegrity);
        yield return ("project evidence rejects false command success and unowned command timelines", CommandIntegrity);
        yield return ("project evidence rejects duplicate reordered and repeated command sequences", SequenceIntegrity);
        yield return ("project evidence cannot satisfy missing prerequisites or pass blocking failures", PrerequisiteIntegrity);
    }
    private static async Task ValidRecords()
    {
        using var f = new EvidenceFixture();
        var read = await f.Read(); var command = await f.Command();
        ProjectEvidenceVerifier.ValidateSequence([read, command], f.Artifacts, f.Settings, requireComplete: true);
        var roundTrip = TestyJson.Clone(read);
        ProjectEvidenceVerifier.ValidateRecord(roundTrip, f.Artifacts);
        ProjectFixture.Check(roundTrip.Files.Single().Identity.Length > 0 && roundTrip.Files.Single().Sha256.Length == 64, "Actual file identity/hash missing.");
    }
    private static async Task ArtifactIntegrity()
    {
        using var f = new EvidenceFixture(); var record = await f.Read();
        var path = record.EvidencePath; var bytes = File.ReadAllBytes(path); var digest = File.ReadAllText(path + ".sha256");
        File.Delete(path); Reject(() => ProjectEvidenceVerifier.ValidateRecord(record, f.Artifacts)); File.WriteAllBytes(path, bytes);
        File.WriteAllText(path + ".sha256", new string('0', 64)); Reject(() => ProjectEvidenceVerifier.ValidateRecord(record, f.Artifacts)); File.WriteAllText(path + ".sha256", digest);
        File.AppendAllText(path, " "); Reject(() => ProjectEvidenceVerifier.ValidateRecord(record, f.Artifacts)); File.WriteAllBytes(path, bytes);
        var altered = TestyJson.Clone(record); altered.Message += " changed after dispatch";
        Reject(() => ProjectEvidenceVerifier.ValidateRecord(altered, f.Artifacts));
        var escaped = TestyJson.Clone(record); escaped.EvidencePath = Path.Combine(f.Fixture.Root, "outside.json"); Persist(escaped);
        Reject(() => ProjectEvidenceVerifier.ValidateRecord(escaped, f.Artifacts));
        ProjectEvidenceVerifier.ValidateRecord(record, f.Artifacts);
    }
    private static async Task CitationIntegrity()
    {
        using var f = new EvidenceFixture(); var record = await f.Read();
        Action<ProjectToolResult>[] corruptions =
        [
            r => r.FinishedAt = null,
            r => r.FinishedAt = r.StartedAt.AddTicks(-1),
            r => r.Status = (ProjectToolStatus)999,
            r => r.Request = null,
            r => r.ToolName = "execute_shell",
            r => r.Files[0].Path = "../outside.cs",
            r => r.Files[0].Sha256 = "invalid",
            r => r.Files[0].Bytes = -1,
            r => r.Files[0].StartLine = 0,
            r => { r.Files[0].StartLine = 10; r.Files[0].EndLine = 9; }
        ];
        foreach (var corrupt in corruptions)
        {
            var changed = Copy(record, f.Artifacts); corrupt(changed); Persist(changed);
            Reject(() => ProjectEvidenceVerifier.ValidateRecord(changed, f.Artifacts));
        }
    }
    private static async Task CommandIntegrity()
    {
        using var f = new EvidenceFixture(); var record = await f.Command();
        Action<ProjectToolResult>[] corruptions =
        [
            r => r.Command = null,
            r => r.Command!.ExitCode = 12,
            r => r.Command!.CleanupComplete = false,
            r => r.Command!.ProcessId = null,
            r => r.Command!.ProcessId = 0,
            r => r.Command!.StartedAt = r.StartedAt.AddTicks(-1),
            r => r.Command!.FinishedAt = r.FinishedAt!.Value.AddTicks(1),
            r => r.Command!.FinishedAt = null,
            r => r.BlocksFurtherActions = true,
            r => r.ToolName = "project_read_file"
        ];
        foreach (var corrupt in corruptions)
        {
            var changed = Copy(record, f.Artifacts); corrupt(changed); Persist(changed);
            Reject(() => ProjectEvidenceVerifier.ValidateRecord(changed, f.Artifacts));
        }
    }
    private static async Task SequenceIntegrity()
    {
        using var f = new EvidenceFixture(); var read = await f.Read(); var command = await f.Command();
        Reject(() => ProjectEvidenceVerifier.ValidateSequence([read, read], f.Artifacts));
        Reject(() => ProjectEvidenceVerifier.ValidateSequence([command, read], f.Artifacts));
        var repeated = Copy(command, f.Artifacts);
        var time = command.FinishedAt!.Value.AddTicks(1);
        repeated.StartedAt = time; repeated.FinishedAt = time.AddMilliseconds(1); repeated.Command!.StartedAt = time; repeated.Command.FinishedAt = time.AddMilliseconds(1); Persist(repeated);
        Reject(() => ProjectEvidenceVerifier.ValidateSequence([command, repeated], f.Artifacts, f.Settings));
        var small = TestyJson.Clone(f.Settings); small.MaximumCalls = 1;
        Reject(() => ProjectEvidenceVerifier.ValidateSequence([read, command], f.Artifacts, small));
        var unknown = Copy(command, f.Artifacts); unknown.Request = JsonSerializer.SerializeToElement(new { commandId = "unconfigured" }); Persist(unknown);
        Reject(() => ProjectEvidenceVerifier.ValidateSequence([unknown], f.Artifacts, f.Settings));
    }
    private static async Task PrerequisiteIntegrity()
    {
        using var f = new EvidenceFixture(); var read = await f.Read();
        Reject(() => ProjectEvidenceVerifier.ValidateSequence([read], f.Artifacts, f.Settings, requireComplete: true));
        f.Fixture.CommandSucceeds = false;
        var failed = await f.Command();
        ProjectEvidenceVerifier.ValidateSequence([read, failed], f.Artifacts, f.Settings, requireComplete: false);
        Reject(() => ProjectEvidenceVerifier.ValidateSequence([read, failed], f.Artifacts, f.Settings, requireComplete: true));
        var forged = Copy(failed, f.Artifacts); forged.BlocksFurtherActions = false; Persist(forged);
        Reject(() => ProjectEvidenceVerifier.ValidateRecord(forged, f.Artifacts));
    }
    private static ProjectToolResult Copy(ProjectToolResult source, string artifacts)
    {
        var record = TestyJson.Clone(source); record.Id = Guid.NewGuid().ToString("N"); record.EvidencePath = Path.Combine(artifacts, "corruption-" + record.Id + ".json"); return record;
    }
    private static void Persist(ProjectToolResult record)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, TestyJson.Options);
        File.WriteAllBytes(record.EvidencePath, bytes); File.WriteAllText(record.EvidencePath + ".sha256", Convert.ToHexString(SHA256.HashData(bytes)));
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException) { return; }
        throw new InvalidOperationException("Corrupted project evidence was accepted.");
    }
    private sealed class EvidenceFixture : IDisposable
    {
        public ProjectFixture Fixture { get; } = new("compatible", requiredCommand: true);
        public string Artifacts { get; }
        public ProjectToolSettings Settings => Fixture.Settings.ProjectTools!;
        private readonly ProjectToolSession session;
        public EvidenceFixture() { Artifacts = Path.Combine(Fixture.Root, "evidence"); session = Fixture.Session(Artifacts); }
        public Task<ProjectToolResult> Read() => session.DispatchAsync("project_read_file", "{\"path\":\"Feature.cs\",\"startLine\":1,\"maxLines\":8}");
        public Task<ProjectToolResult> Command() => session.DispatchAsync("project_run_command", "{\"commandId\":\"build\"}");
        public void Dispose() => Fixture.Dispose();
    }
}
