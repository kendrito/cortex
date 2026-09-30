using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Testy.Core;

public sealed class WorkspaceFormat
{
    public int FormatVersion { get; set; } = 1;
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<string> OriginalRoots { get; set; } = [];
    public string Message { get; set; } = "";
}
public sealed class WorkspaceMaintenanceResult
{
    public bool Passed { get; set; }
    public string Operation { get; set; } = "";
    public int FormatVersion { get; set; } = 1;
    public string WorkspaceDirectory { get; set; } = "";
    public string ArchivePath { get; set; } = "";
    public string ArchiveSha256 { get; set; } = "";
    public int Files { get; set; }
    public long Bytes { get; set; }
    public List<string> OriginalRoots { get; set; } = [];
    public string Message { get; set; } = "";
}
internal sealed class WorkspaceArchiveFile
{
    public string Path { get; set; } = "";
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = "";
}
internal sealed class WorkspaceArchiveManifest
{
    public string Schema { get; set; } = "testy.workspace-backup.v1";
    public int FormatVersion { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string OriginalRoot { get; set; } = "";
    public List<string> OriginalRoots { get; set; } = [];
    public List<WorkspaceArchiveFile> Files { get; set; } = [];
    public List<string> ExcludedRuntimeLocks { get; set; } = [];
}

/// <summary>Local validated backup and fresh-directory restore. Evidence bytes remain immutable; historical paths resolve through an explicit root map.</summary>
public static class WorkspaceMaintenance
{
    public const int MaximumFiles = 20000;
    public const long MaximumTotalBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumFileBytes = 256L * 1024 * 1024;
    private const string Marker = "workspace-format.json";
    private const string LockName = ".workspace-maintenance.lock";
    public static WorkspaceMaintenanceResult Migrate(string workspace)
    {
        var root = OperationsStorage.Root(workspace, true);
        using var gate = OperationsStorage.Lock(Path.Combine(root, LockName));
        using var operations = QuiescentOperations(root);
        ValidateWorkspace(root, operationsLocked: true);
        var format = ReadFormat(root);
        bool legacy = !File.Exists(Path.Combine(root, Marker));
        format.RecordedAt = DateTimeOffset.UtcNow;
        format.Message = legacy ? "Legacy layout validated and adopted as format1. Test/evidence files were not modified." : "Workspace format1 validated; no migration required.";
        OperationsStorage.Write(Path.Combine(root, Marker), format);
        return new() { Passed = true, Operation = "migrate", WorkspaceDirectory = root, OriginalRoots = format.OriginalRoots, Message = format.Message };
    }
    public static WorkspaceMaintenanceResult Backup(string workspace, string archive, CancellationToken cancellationToken = default)
    {
        var root = OperationsStorage.Root(workspace, false);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Workspace does not exist.");
        var archivePath = Path.GetFullPath(archive); OperationsStorage.NoReparse(archivePath);
        if (Inside(root, archivePath) || File.Exists(archivePath)) throw new InvalidDataException("Choose a new backup path outside the workspace.");
        var parent = OperationsStorage.Root(Path.GetDirectoryName(archivePath)!, true);
        using var gate = OperationsStorage.Lock(Path.Combine(root, LockName));
        using var testWrites = OperationsStorage.Lock(Path.Combine(root, "tests-write.lock"));
        using var operations = QuiescentOperations(root);
        ValidateWorkspace(root, operationsLocked: true);
        var format = ReadFormat(root);
        var inventory = Inventory(root);
        var opened = new List<(WorkspaceArchiveFile File, FileStream Stream)>();
        var temporary = Path.Combine(parent, ".backup-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            long total = 0;
            foreach (var relative in inventory.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = OperationsStorage.Relative(root, relative);
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                try
                {
                    total = checked(total + stream.Length);
                    if (stream.Length > MaximumFileBytes || total > MaximumTotalBytes) throw new InvalidDataException("Workspace backup size limit exceeded.");
                    var file = new WorkspaceArchiveFile { Path = relative, Bytes = stream.Length, Sha256 = Convert.ToHexString(SHA256.HashData(stream)) };
                    stream.Position = 0; opened.Add((file, stream));
                }
                catch { stream.Dispose(); throw; }
            }
            var manifest = new WorkspaceArchiveManifest { OriginalRoot = root, OriginalRoots = format.OriginalRoots, Files = opened.Select(e => e.File).ToList(), ExcludedRuntimeLocks = inventory.Excluded };
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    foreach (var item in opened)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using var target = zip.CreateEntry("payload/" + item.File.Path, CompressionLevel.Optimal).Open();
                        item.Stream.CopyTo(target);
                    }
                    using var entry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal).Open();
                    JsonSerializer.Serialize(entry, manifest, TestyJson.Options);
                }
                output.Flush(true);
            }
            if (!inventory.Files.SequenceEqual(Inventory(root).Files, StringComparer.Ordinal)) throw new IOException("Workspace changed during backup; no archive was committed.");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, archivePath, false);
            return new() { Passed = true, Operation = "backup", WorkspaceDirectory = root, ArchivePath = archivePath, ArchiveSha256 = HashFile(archivePath), Files = opened.Count, Bytes = total,
                OriginalRoots = new[] { root }.Concat(format.OriginalRoots).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Message = "Consistent bounded file snapshot saved. Runtime lock files excluded. External artifacts and dependencies outside this workspace are not bundled; backup is not encrypted." };
        }
        finally { foreach (var item in opened) item.Stream.Dispose(); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static WorkspaceMaintenanceResult Restore(string archive, string destination, CancellationToken cancellationToken = default)
    {
        var archivePath = Path.GetFullPath(archive); OperationsStorage.NoReparse(archivePath);
        _ = OperationsStorage.Root(Path.GetDirectoryName(archivePath)!, false);
        var root = OperationsStorage.Root(destination, false);
        if (File.Exists(root) || (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())) throw new InvalidDataException("Restore requires a fresh or empty destination. Existing workspaces are never overwritten.");
        if (Inside(root, archivePath)) throw new InvalidDataException("Archive cannot be inside the restore destination.");
        var parent = OperationsStorage.Root(Path.GetDirectoryName(root)!, true);
        var stage = Path.Combine(parent, ".testy-restore-" + Guid.NewGuid().ToString("N"));
        var emptyBackup = Path.Combine(parent, ".testy-empty-" + Guid.NewGuid().ToString("N"));
        var lockPath = Path.Combine(parent, ".restore-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root.ToUpperInvariant()))) + ".lock");
        using var gate = OperationsStorage.Lock(lockPath);
        if (File.Exists(root) || (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())) throw new InvalidDataException("Restore destination became nonempty.");
        Directory.CreateDirectory(stage);
        try
        {
            using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaximumTotalBytes + 64L * 1024 * 1024) throw new InvalidDataException("Backup archive exceeds its bounded size.");
            using var zip = new ZipArchive(input, ZipArchiveMode.Read);
            if (zip.Entries.Count > MaximumFiles + 1) throw new InvalidDataException("Backup archive has too many entries.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                if (!names.Add(entry.FullName)) throw new InvalidDataException("Duplicate archive entry.");
                _ = OperationsStorage.Relative(stage, entry.FullName);
                if (entry.FullName.EndsWith('/') || entry.Length < 0 || entry.Length > MaximumFileBytes || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidDataException("Unsupported archive entry type or size.");
            }
            var manifestEntry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("Backup manifest is absent.");
            if (manifestEntry.Length > 8 * 1024 * 1024) throw new InvalidDataException("Backup manifest is too large.");
            WorkspaceArchiveManifest manifest;
            using (var stream = manifestEntry.Open()) manifest = JsonSerializer.Deserialize<WorkspaceArchiveManifest>(stream, TestyJson.Options) ?? throw new InvalidDataException("Backup manifest is null.");
            if (manifest.Schema is not ("testy.workspace-backup.v1" or "axiom.workspace-backup.v1") /* backups made before the rename from Axiom still restore */ || manifest.FormatVersion != 1 || manifest.Files is null || manifest.Files.Count > MaximumFiles || zip.Entries.Count != manifest.Files.Count + 1)
                throw new InvalidDataException("Unsupported or incomplete backup manifest.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seen.Add(file.Path) || file.Bytes < 0 || file.Bytes > MaximumFileBytes || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid backup file record.");
                var path = OperationsStorage.Relative(stage, file.Path);
                if (RuntimeLock(file.Path)) throw new InvalidDataException("Runtime locks cannot be restored.");
                total = checked(total + file.Bytes); if (total > MaximumTotalBytes) throw new InvalidDataException("Backup expands beyond the workspace size limit.");
                var entry = zip.GetEntry("payload/" + file.Path) ?? throw new InvalidDataException("A manifest file is absent.");
                if (entry.Length != file.Bytes) throw new InvalidDataException("Backup length does not match its manifest.");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var source = entry.Open()) using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    CopyBounded(source, output, file.Bytes, cancellationToken); output.Flush(true);
                }
                if (!HashFile(path).Equals(file.Sha256, StringComparison.Ordinal)) throw new InvalidDataException("Backup file checksum mismatch.");
            }
            ValidateWorkspace(stage);
            var roots = new[] { manifest.OriginalRoot }.Concat(manifest.OriginalRoots ?? []).Select(p => OperationsStorage.Root(p, false)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (roots.Count > 32) throw new InvalidDataException("Too many historical workspace roots.");
            OperationsStorage.Write(Path.Combine(stage, Marker), new WorkspaceFormat { OriginalRoots = roots, Message = "Restored to a fresh workspace. Evidence bytes retained; historical paths resolve through this explicit map. Schedules paused and pending attempts require explicit fresh rerun." });
            if (File.Exists(Path.Combine(stage, "operations", "queue.json"))) new OperationsStore(Path.Combine(stage, "operations")).SuspendRestored();
            cancellationToken.ThrowIfCancellationRequested();
            bool movedEmpty = false;
            try
            {
                if (Directory.Exists(root)) { if (Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("Restore destination became nonempty."); Directory.Move(root, emptyBackup); movedEmpty = true; }
                Directory.Move(stage, root);
            }
            catch { if (movedEmpty && !Directory.Exists(root)) Directory.Move(emptyBackup, root); throw; }
            if (movedEmpty) Directory.Delete(emptyBackup, false);
            return new() { Passed = true, Operation = "restore", WorkspaceDirectory = root, ArchivePath = archivePath, ArchiveSha256 = HashFile(archivePath), Files = manifest.Files.Count, Bytes = total, OriginalRoots = roots,
                Message = "Verified backup restored into a fresh workspace. Select it explicitly; no queued or scheduled execution was resumed." };
        }
        finally { if (Directory.Exists(stage)) DeleteOwnedStage(stage, parent); }
    }
    public static string ResolveRestoredPath(string workspace, string recordedPath)
    {
        var root = OperationsStorage.Root(workspace, false);
        var format = ReadFormat(root);
        if (string.IsNullOrWhiteSpace(recordedPath)) return recordedPath;
        string full;
        try { full = Path.GetFullPath(recordedPath); } catch { return recordedPath; }
        if (Inside(root, full)) { OperationsStorage.NoReparse(full); return full; }
        // Map first even if the old workspace still exists: restored history must not display a different workspace's files.
        foreach (var original in format.OriginalRoots.OrderByDescending(p => p.Length))
        {
            if (!Inside(original, full)) continue;
            return OperationsStorage.Relative(root, full[(original.TrimEnd(Path.DirectorySeparatorChar).Length + 1)..].Replace(Path.DirectorySeparatorChar, '/'));
        }
        return recordedPath; // Explicitly external historical evidence is not silently copied or claimed restored.
    }
    /// <summary>Resolves copied profile input files without rewriting the profile or rebinding its executable/project.</summary>
    public static LifecycleProfile ResolveRestoredProfileInputs(string profilePath, LifecycleProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profilePath = Path.GetFullPath(profilePath);
        OperationsStorage.NoReparse(profilePath);
        var directory = Path.GetDirectoryName(profilePath) ?? throw new InvalidDataException("Profile directory is unavailable.");
        string? workspace = null;
        for (var cursor = new DirectoryInfo(directory); cursor is not null; cursor = cursor.Parent)
        {
            if (!File.Exists(Path.Combine(cursor.FullName, Marker))) continue;
            workspace = OperationsStorage.Root(cursor.FullName, false);
            break;
        }
        string ResolveInput(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Lifecycle profile is missing a required input path.");
            var resolved = Path.GetFullPath(value, directory);
            return workspace is null ? resolved : ResolveRestoredPath(workspace, resolved);
        }
        var result = TestyJson.Clone(profile);
        result.TestFile = ResolveInput(profile.TestFile);
        result.SettingsFile = ResolveInput(profile.SettingsFile);
        if (profile.TargetArgumentsFile is not null) result.TargetArgumentsFile = ResolveInput(profile.TargetArgumentsFile);
        // Executable, ProjectFile and nested settings/project roots remain explicit external configuration.
        return result;
    }
    internal static void ValidateWorkspace(string root, bool operationsLocked = false)
    {
        _ = ReadFormat(root); _ = Inventory(root);
        foreach (var directory in new[] { "tests", "runs" })
        {
            var path = Path.Combine(root, directory); if (!Directory.Exists(path)) continue;
            foreach (var file in Directory.EnumerateFiles(path, "*.json", SearchOption.TopDirectoryOnly))
            {
                if (directory == "tests")
                {
                    var test = OperationsStorage.Read<TestCase>(file); TestValidator.ValidateId(test.Id);
                    if (test.Id != Path.GetFileNameWithoutExtension(file) || string.IsNullOrWhiteSpace(test.Name) || test.Name.Length > 1000 || test.Steps is null || test.Steps.Count > 200) throw new InvalidDataException("Invalid stored test/draft.");
                    var ids = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var step in test.Steps) { if (step is null) throw new InvalidDataException("Null draft step."); TestValidator.ValidateId(step.Id); if (!ids.Add(step.Id) || !Enum.IsDefined(step.Action)) throw new InvalidDataException("Invalid draft step identity/action."); }
                }
                else { var run = OperationsStorage.Read<RunResult>(file); TestValidator.ValidateId(run.Id); if (run.Id != Path.GetFileNameWithoutExtension(file) || !Enum.IsDefined(run.Status) || run.Steps is null) throw new InvalidDataException("Invalid saved run index."); }
            }
        }
        if (File.Exists(Path.Combine(root, "settings.json")))
        {
            var settings = OperationsStorage.Read<ProviderSettings>(Path.Combine(root, "settings.json"));
            if (!Enum.IsDefined(settings.Kind) || settings.MaximumAgentTurns is < 1 or > 240) throw new InvalidDataException("Invalid stored provider settings.");
        }
        if (File.Exists(Path.Combine(root, "project-tools.json"))) _ = ProjectConfiguration.ReadStored(File.ReadAllText(Path.Combine(root, "project-tools.json")));
        if (File.Exists(Path.Combine(root, "operations", "queue.json")))
        {
            if (operationsLocked) OperationsStore.ValidateState(OperationsStorage.Read<OperationsState>(Path.Combine(root, "operations", "queue.json")));
            else _ = new OperationsStore(Path.Combine(root, "operations")).ListJobs();
        }
    }
    private static IDisposable QuiescentOperations(string root)
    {
        var directory = OperationsStorage.Root(Path.Combine(root, "operations"), true);
        var lease = OperationsStorage.Lock(Path.Combine(directory, "queue.lock"));
        try
        {
            var path = Path.Combine(directory, "queue.json");
            if (File.Exists(path))
            {
                var state = OperationsStorage.Read<OperationsState>(path); OperationsStore.ValidateState(state);
                if (state.Jobs.Any(j => j.Status == OperationsJobStatus.Running)) throw new IOException("Stop and finalize active queued operations before workspace maintenance.");
            }
            return lease; // Held through the snapshot so another pump cannot claim after the quiescent check.
        }
        catch { lease.Dispose(); throw; }
    }
    private static WorkspaceFormat ReadFormat(string root)
    {
        var path = Path.Combine(root, Marker);
        var format = File.Exists(path) ? OperationsStorage.Read<WorkspaceFormat>(path) : new();
        if (format.FormatVersion != 1 || format.OriginalRoots is null || format.OriginalRoots.Count > 32) throw new InvalidDataException("Unsupported workspace format; future versions are never downgraded.");
        foreach (var original in format.OriginalRoots) _ = OperationsStorage.Root(original, false);
        return format;
    }
    private static (List<string> Files, List<string> Excluded) Inventory(string root)
    {
        var files = new List<string>(); var excluded = new List<string>();
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((root, 0));
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            if (depth > 64) throw new InvalidDataException("Workspace directory depth exceeds64.");
            OperationsStorage.NoReparse(directory);
            foreach (var item in Directory.EnumerateFileSystemEntries(directory))
            {
                OperationsStorage.NoReparse(item);
                if (Directory.Exists(item)) { pending.Push((item, depth + 1)); continue; }
                var relative = Path.GetRelativePath(root, item).Replace(Path.DirectorySeparatorChar, '/');
                _ = OperationsStorage.Relative(root, relative);
                if (RuntimeLock(relative)) { excluded.Add(relative); continue; }
                if (relative.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) throw new IOException("Workspace contains a pending write. Close other writers before maintenance.");
                files.Add(relative); if (files.Count > MaximumFiles) throw new InvalidDataException("Workspace contains too many files for one backup.");
            }
        }
        files.Sort(StringComparer.Ordinal); excluded.Sort(StringComparer.Ordinal); return (files, excluded);
    }
    private static bool Inside(string root, string path) => path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool RuntimeLock(string relative) => relative.Equals(LockName, StringComparison.OrdinalIgnoreCase) || relative.Equals("tests-write.lock", StringComparison.OrdinalIgnoreCase) || relative.Equals("operations/queue.lock", StringComparison.OrdinalIgnoreCase);
    private static string HashFile(string path) { using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Convert.ToHexString(SHA256.HashData(file)); }
    private static void CopyBounded(Stream source, Stream target, long expected, CancellationToken token)
    {
        var buffer = new byte[81920]; long total = 0; int count;
        while ((count = source.Read(buffer, 0, buffer.Length)) != 0) { token.ThrowIfCancellationRequested(); total += count; if (total > expected) throw new InvalidDataException("Archive stream exceeded its declared size."); target.Write(buffer, 0, count); }
        if (total != expected) throw new InvalidDataException("Archive stream ended before its declared size.");
    }
    private static void DeleteOwnedStage(string path, string parent)
    {
        var full = Path.GetFullPath(path);
        if (Path.GetDirectoryName(full) != parent || !Path.GetFileName(full).StartsWith(".testy-restore-", StringComparison.Ordinal)) throw new IOException("Unsafe staging cleanup.");
        OperationsStorage.NoReparse(full); Directory.Delete(full, true);
    }
}
