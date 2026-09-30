using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Testy.Core;

namespace Testy.Tests;

internal static class WorkspaceMaintenanceChecks
{
    internal static IEnumerable<(string Name, Func<Task> Execute)> All()
    {
        yield return ("workspace backup roundtrip preserves evidence bytes and resolves restored history locally", RoundTrip);
        yield return ("workspace restore checksum failures leave the destination unchanged", CorruptPayload);
        yield return ("workspace archives reject duplicate, traversal, device and extra entries", HostileArchives);
        yield return ("workspace maintenance rejects future formats and validates before migration", Migration);
        yield return ("workspace backup refuses active writes, pending files and source-contained archives", ConcurrentWrite);
        yield return ("workspace maintenance excludes active queued operations without disrupting their state", ActiveOperations);
        yield return ("workspace restore never overwrites a populated workspace", ExistingDestination);
        yield return ("workspace maintenance refuses actual junctions without reading external files", Reparse);
        yield return ("workspace cancellation commits no partial archive or restored directory", Cancellation);
        yield return ("workspace backup includes only local files and keeps original history immutable", ExternalEvidence);
        yield return ("restored profiles load copied frozen inputs when the original workspace is absent", RestoredProfileInputs);
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "Testy-workspace-checks-" + Guid.NewGuid().ToString("N"));
        internal string Workspace => Path.Combine(Root, "workspace");
        internal string Archive => Path.Combine(Root, "backup.zip");
        internal string Restore => Path.Combine(Root, "restored");
        internal Fixture()
        {
            var store = new WorkspaceStore(Workspace); store.SaveDraft(new() { Id = "draft", Name = "Draft still being edited", Steps = [] });
            store.SaveSettings(new() { Kind = ProviderKind.Offline, AiDirectedExecution = false });
            Directory.CreateDirectory(Path.Combine(Workspace, "artifacts", "run"));
            File.WriteAllBytes(Path.Combine(Workspace, "artifacts", "run", "step.png"), [1, 2, 3, 4, 5]);
            store.SaveRun(new() { Id = "run", TestId = "draft", Status = RunStatus.Failed, ArtifactDirectory = Path.Combine(Workspace, "artifacts", "run"),
                Steps = [new() { ScreenshotPath = Path.Combine(Workspace, "artifacts", "run", "step.png"), Status = RunStatus.Failed }] });
        }
        public void Dispose()
        {
            var full = Path.GetFullPath(Root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("Testy-workspace-checks-", StringComparison.Ordinal)) throw new IOException("Unsafe owned cleanup path.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or ArgumentException or JsonException) { return; } throw new InvalidOperationException("Expected maintenance rejection."); }
    private static Task RoundTrip()
    {
        using var f = new Fixture(); var oldRun = File.ReadAllBytes(Path.Combine(f.Workspace, "runs", "run.json"));
        var saved = WorkspaceMaintenance.Backup(f.Workspace, f.Archive); Check(saved.Passed && saved.Files == 4 && saved.ArchiveSha256.Length == 64, "Backup inventory/hash missing.");
        var result = WorkspaceMaintenance.Restore(f.Archive, f.Restore); Check(result.Passed && result.OriginalRoots.Contains(f.Workspace), "Original path mapping absent.");
        Check(File.ReadAllBytes(Path.Combine(f.Restore, "runs", "run.json")).SequenceEqual(oldRun), "Immutable historical JSON was rewritten.");
        var recorded = Path.Combine(f.Workspace, "artifacts", "run", "step.png"); var mapped = WorkspaceMaintenance.ResolveRestoredPath(f.Restore, recorded);
        Check(mapped == Path.Combine(f.Restore, "artifacts", "run", "step.png") && File.ReadAllBytes(mapped).SequenceEqual(new byte[] { 1, 2, 3, 4, 5 }), "Restored evidence mapped to the old workspace or changed bytes.");
        Check(new WorkspaceStore(f.Restore).LoadTests().Single().Steps.Count == 0, "Incomplete draft was rejected/lost on restore."); return Task.CompletedTask;
    }
    private static Task CorruptPayload()
    {
        using var f = new Fixture(); WorkspaceMaintenance.Backup(f.Workspace, f.Archive);
        using (var zip = ZipFile.Open(f.Archive, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("payload/artifacts/run/step.png")!; entry.Delete();
            using var stream = zip.CreateEntry("payload/artifacts/run/step.png").Open(); stream.Write(new byte[] { 9, 2, 3, 4, 5 });
        }
        Directory.CreateDirectory(f.Restore); Reject(() => WorkspaceMaintenance.Restore(f.Archive, f.Restore));
        Check(Directory.Exists(f.Restore) && !Directory.EnumerateFileSystemEntries(f.Restore).Any(), "Failed checksum partially replaced the destination.");
        Check(!Directory.EnumerateDirectories(f.Root, ".testy-restore-*").Any(), "Failed restore left a staging directory."); return Task.CompletedTask;
    }
    private static Task HostileArchives()
    {
        using var f = new Fixture(); WorkspaceMaintenance.Backup(f.Workspace, f.Archive);
        foreach (var name in new[] { "payload/../outside.txt", "payload/CON.txt", "payload/artifacts/run/step.png", "payload/unmanifested.txt", "payload/a:stream" })
        {
            var modified = Path.Combine(f.Root, Guid.NewGuid().ToString("N") + ".zip"); File.Copy(f.Archive, modified);
            using (var zip = ZipFile.Open(modified, ZipArchiveMode.Update)) { using var stream = zip.CreateEntry(name).Open(); stream.WriteByte(42); }
            Reject(() => WorkspaceMaintenance.Restore(modified, f.Restore)); Check(!Directory.Exists(f.Restore), "Hostile archive committed a workspace.");
        }
        Check(!File.Exists(Path.Combine(f.Root, "outside.txt")), "Traversal escaped extraction."); return Task.CompletedTask;
    }
    private static Task Migration()
    {
        using var f = new Fixture(); var testBytes = File.ReadAllBytes(Path.Combine(f.Workspace, "tests", "draft.json"));
        var result = WorkspaceMaintenance.Migrate(f.Workspace); Check(result.Passed && File.ReadAllBytes(Path.Combine(f.Workspace, "tests", "draft.json")).SequenceEqual(testBytes), "Legacy migration changed test bytes.");
        Check(WorkspaceMaintenance.Migrate(f.Workspace).Passed, "Idempotent migration failed.");
        var marker = Path.Combine(f.Workspace, "workspace-format.json"); File.WriteAllText(marker, "{\"formatVersion\":999,\"originalRoots\":[]}");
        Reject(() => WorkspaceMaintenance.Migrate(f.Workspace)); Reject(() => WorkspaceMaintenance.Backup(f.Workspace, f.Archive)); Check(File.ReadAllText(marker).Contains("999"), "Future format was downgraded.");
        File.Delete(marker); File.WriteAllText(Path.Combine(f.Workspace, "runs", "run.json"), "broken json");
        Reject(() => WorkspaceMaintenance.Migrate(f.Workspace)); Check(!File.Exists(marker), "Corrupt workspace acquired a success marker."); return Task.CompletedTask;
    }
    private static Task ConcurrentWrite()
    {
        using var f = new Fixture(); var file = Path.Combine(f.Workspace, "artifacts", "run", "step.png");
        using (var writer = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            Reject(() => WorkspaceMaintenance.Backup(f.Workspace, f.Archive));
        Check(!File.Exists(f.Archive), "Active-writer backup committed.");
        File.WriteAllText(Path.Combine(f.Workspace, "pending.tmp"), "pending"); Reject(() => WorkspaceMaintenance.Backup(f.Workspace, f.Archive)); File.Delete(Path.Combine(f.Workspace, "pending.tmp"));
        Reject(() => WorkspaceMaintenance.Backup(f.Workspace, Path.Combine(f.Workspace, "backup.zip")));
        WorkspaceMaintenance.Backup(f.Workspace, f.Archive); var before = File.ReadAllBytes(f.Archive); Reject(() => WorkspaceMaintenance.Backup(f.Workspace, f.Archive)); Check(File.ReadAllBytes(f.Archive).SequenceEqual(before), "Existing backup overwritten."); return Task.CompletedTask;
    }
    private static Task ExistingDestination()
    {
        using var f = new Fixture(); WorkspaceMaintenance.Backup(f.Workspace, f.Archive);
        Directory.CreateDirectory(f.Restore); var file = Path.Combine(f.Restore, "keep.txt"); File.WriteAllText(file, "existing user workspace");
        Reject(() => WorkspaceMaintenance.Restore(f.Archive, f.Restore)); Check(File.ReadAllText(file) == "existing user workspace", "Existing workspace changed."); return Task.CompletedTask;
    }
    private static Task ActiveOperations()
    {
        using var f = new Fixture(); var store = new OperationsStore(Path.Combine(f.Workspace, "operations")); store.Enqueue(OperationsChecks.RequestForRoot(f.Root));
        using var lease = OperationsDesktopLease.Acquire(Path.Combine(f.Root, "leases"), "owned-test-desktop");
        var job = store.Claim(lease!)!;
        Reject(() => WorkspaceMaintenance.Backup(f.Workspace, f.Archive)); Reject(() => WorkspaceMaintenance.Migrate(f.Workspace));
        Check(!File.Exists(f.Archive) && store.ListJobs().Single().Status == OperationsJobStatus.Running, "Backup disturbed a running claim.");
        var now = DateTimeOffset.UtcNow;
        store.Complete(job.Id, job.ClaimToken, new() { Status = LifecycleStatus.Cancelled, StartedAt = now, FinishedAt = now, CleanupComplete = true }, null, true);
        File.WriteAllText(Path.Combine(f.Workspace, "data.lock"), "ordinary user evidence, not an engine lock");
        WorkspaceMaintenance.Backup(f.Workspace, f.Archive); WorkspaceMaintenance.Restore(f.Archive, f.Restore);
        Check(File.Exists(Path.Combine(f.Restore, "data.lock")), "Unrelated .lock evidence was silently excluded."); return Task.CompletedTask;
    }
    private static Task Reparse()
    {
        using var f = new Fixture(); var outside = Path.Combine(f.Root, "outside"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "private.txt"), "Owned sentinel, never included");
        var junction = Path.Combine(f.Workspace, "junction"); var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("/d"); info.ArgumentList.Add("/c"); info.ArgumentList.Add("mklink"); info.ArgumentList.Add("/J"); info.ArgumentList.Add(junction); info.ArgumentList.Add(outside);
        using (var process = Process.Start(info)!) { process.WaitForExit(); Check(process.ExitCode == 0, "Owned junction creation failed."); }
        try { Reject(() => WorkspaceMaintenance.Backup(f.Workspace, f.Archive)); Reject(() => WorkspaceMaintenance.Migrate(junction)); Check(!File.Exists(f.Archive), "Junction backup committed."); }
        finally { Directory.Delete(junction, false); }
        Check(File.ReadAllText(Path.Combine(outside, "private.txt")).Contains("sentinel"), "External sentinel changed."); return Task.CompletedTask;
    }
    private static Task Cancellation()
    {
        using var f = new Fixture(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { WorkspaceMaintenance.Backup(f.Workspace, f.Archive, cancellation.Token); throw new InvalidOperationException("Cancellation ignored."); } catch (OperationCanceledException) { }
        Check(!File.Exists(f.Archive), "Cancelled backup committed."); WorkspaceMaintenance.Backup(f.Workspace, f.Archive);
        try { WorkspaceMaintenance.Restore(f.Archive, f.Restore, cancellation.Token); throw new InvalidOperationException("Cancellation ignored."); } catch (OperationCanceledException) { }
        Check(!Directory.Exists(f.Restore) && !Directory.EnumerateDirectories(f.Root, ".testy-restore-*").Any(), "Cancelled restore left partial destination."); return Task.CompletedTask;
    }
    private static Task ExternalEvidence()
    {
        using var f = new Fixture(); var external = Path.Combine(f.Root, "external.png"); File.WriteAllText(external, "not workspace evidence");
        var store = new WorkspaceStore(f.Workspace); var run = store.LoadRuns().Single(); run.Steps[0].ScreenshotPath = external; store.SaveRun(run);
        WorkspaceMaintenance.Backup(f.Workspace, f.Archive); WorkspaceMaintenance.Restore(f.Archive, f.Restore);
        using var archive = ZipFile.OpenRead(f.Archive); Check(!archive.Entries.Any(e => e.Name == "external.png"), "External artifact silently copied.");
        Check(WorkspaceMaintenance.ResolveRestoredPath(f.Restore, external) == external, "External reference silently rebound.");
        var sibling = f.Workspace + "-sibling/artifacts/file.png"; Check(WorkspaceMaintenance.ResolveRestoredPath(f.Restore, sibling) == sibling, "Sibling-prefix path treated as child."); return Task.CompletedTask;
    }
    private static Task RestoredProfileInputs()
    {
        using var f = new Fixture();
        var inputs = Path.Combine(f.Workspace, "workflow-inputs"); var profiles = Path.Combine(f.Workspace, "profiles");
        Directory.CreateDirectory(inputs); Directory.CreateDirectory(profiles);
        var request = OperationsChecks.RequestForRoot(f.Root);
        var profile = request.Lifecycle!.Profile;
        profile.TestFile = Path.Combine(inputs, "test.json"); profile.SettingsFile = Path.Combine(inputs, "settings.json"); profile.TargetArgumentsFile = Path.Combine(inputs, "arguments.json");
        profile.Executable = Path.Combine(f.Workspace, "explicit-application.exe"); profile.ProjectFile = Path.Combine(f.Workspace, "explicit-project.json");
        WorkspaceStore.WriteAtomic(profile.TestFile, request.Lifecycle!.Test); WorkspaceStore.WriteAtomic(profile.SettingsFile, request.Lifecycle!.Provider);
        WorkspaceStore.WriteAtomic(profile.TargetArgumentsFile, new[] { "literal argument", "Unicode ☃" });
        var path = Path.Combine(profiles, "saved.json"); WorkspaceStore.WriteAtomic(path, profile); var originalBytes = File.ReadAllBytes(path);
        WorkspaceMaintenance.Backup(f.Workspace, f.Archive); WorkspaceMaintenance.Restore(f.Archive, f.Restore);
        // An owned rename makes the old absolute input paths genuinely unavailable without deleting evidence.
        Directory.Move(f.Workspace, Path.Combine(f.Root, "original-unavailable"));
        var restoredProfile = Path.Combine(f.Restore, "profiles", "saved.json");
        var loaded = JsonSerializer.Deserialize<LifecycleProfile>(File.ReadAllText(restoredProfile), TestyJson.Options)!;
        var resolved = WorkspaceMaintenance.ResolveRestoredProfileInputs(restoredProfile, loaded);
        Check(!Directory.Exists(f.Workspace) && resolved.TestFile == Path.Combine(f.Restore, "workflow-inputs", "test.json")
            && resolved.SettingsFile == Path.Combine(f.Restore, "workflow-inputs", "settings.json")
            && resolved.TargetArgumentsFile == Path.Combine(f.Restore, "workflow-inputs", "arguments.json"), "Copied profile inputs were not rebound to the restored workspace.");
        Check(JsonSerializer.Deserialize<TestCase>(File.ReadAllText(resolved.TestFile), TestyJson.Options)!.Id == request.Lifecycle!.Test.Id
            && JsonSerializer.Deserialize<ProviderSettings>(File.ReadAllText(resolved.SettingsFile), TestyJson.Options)!.ProjectTools!.RootDirectory == f.Root
            && JsonSerializer.Deserialize<string[]>(File.ReadAllText(resolved.TargetArgumentsFile!), TestyJson.Options)!.SequenceEqual(new[] { "literal argument", "Unicode ☃" }), "Resolved inputs did not retain the frozen test/provider/arguments.");
        Check(resolved.Executable == profile.Executable && resolved.ProjectFile == profile.ProjectFile && loaded.TestFile == profile.TestFile
            && File.ReadAllBytes(restoredProfile).SequenceEqual(originalBytes), "Restore rebound an executable/project or rewrote profile bytes.");
        var relative = TestyJson.Clone(loaded); relative.TestFile = "../workflow-inputs/test.json";
        Check(WorkspaceMaintenance.ResolveRestoredProfileInputs(restoredProfile, relative).TestFile == resolved.TestFile, "Relative profile input did not retain normal profile-directory semantics.");
        var external = TestyJson.Clone(loaded); external.SettingsFile = f.Workspace + "-sibling/settings.json";
        var resolvedExternal = WorkspaceMaintenance.ResolveRestoredProfileInputs(restoredProfile, external).SettingsFile;
        Check(resolvedExternal == Path.GetFullPath(external.SettingsFile)
            && !resolvedExternal.StartsWith(f.Restore + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Sibling-prefix external input was silently rebound.");
        Check(WorkspaceMaintenance.ResolveRestoredProfileInputs(Path.Combine(f.Root, "outside-profile.json"), loaded).TestFile == loaded.TestFile, "A profile outside a restored workspace inherited an unrelated root mapping.");
        return Task.CompletedTask;
    }
}
