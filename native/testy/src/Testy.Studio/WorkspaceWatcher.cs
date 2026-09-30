using System.IO;
using System.Windows.Threading;
using Testy.Core;

namespace Testy.Studio;

internal enum WorkspaceArea { Tests, Runs }

/// <summary>A file in the workspace's tests or runs folder that changed (created, written, renamed or deleted) since the last pass.</summary>
internal sealed record WorkspaceChange(WorkspaceArea Area, string Path);

/// <summary>
/// Watches <c>&lt;workspace&gt;\tests</c> and <c>&lt;workspace&gt;\runs</c> for files written by anything other than this Studio process: an AI agent
/// through the MCP server, another Testy tool, or a person. Events are debounced (<see cref="ChangeDebounce"/>: 300 ms after the last one, at most
/// 1 s after the first) and delivered on the Dispatcher as one batch of changed paths. Studio's own saves are recognized by content through a
/// <see cref="WorkspaceWriteLedger"/>, so a save never looks like an outside change. The watcher only reports; the window decides what to reload.
/// It never changes busy state, disables nothing and opens no dialogs.
/// </summary>
internal sealed class WorkspaceWatcher : IDisposable
{
    public static readonly TimeSpan Debounce = ChangeDebounce.QuietPeriod;
    private readonly Dispatcher dispatcher;
    private readonly Action<IReadOnlyList<WorkspaceChange>> onChanged;
    private readonly Action<string> onError;
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly Dictionary<string, WorkspaceChange> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly WorkspaceWriteLedger ledger = new();
    private readonly object gate = new();
    private readonly Timer timer;
    private DateTimeOffset? firstPending;
    private bool disposed;

    public WorkspaceWatcher(string root, Dispatcher dispatcher, Action<IReadOnlyList<WorkspaceChange>> onChanged, Action<string> onError)
    {
        this.dispatcher = dispatcher; this.onChanged = onChanged; this.onError = onError;
        Root = Path.GetFullPath(root);
        timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            Watch(Path.Combine(Root, "tests"), WorkspaceArea.Tests);
            Watch(Path.Combine(Root, "runs"), WorkspaceArea.Runs);
        }
        catch { Dispose(); throw; }
    }

    public string Root { get; }
    public string TestPath(string id) => Path.Combine(Root, "tests", id + ".json");
    public string RunPath(string id) => Path.Combine(Root, "runs", id + ".json");

    private void Watch(string directory, WorkspaceArea area)
    {
        Directory.CreateDirectory(directory);
        // "*.json" leaves out the store's "<id>.json.<guid>.tmp" files; the rename that completes an atomic write still matches by its new name.
        var watcher = new FileSystemWatcher(directory, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            InternalBufferSize = 64 * 1024, IncludeSubdirectories = false
        };
        watcher.Created += (_, e) => Enqueue(area, e.FullPath);
        watcher.Changed += (_, e) => Enqueue(area, e.FullPath);
        watcher.Deleted += (_, e) => Enqueue(area, e.FullPath);
        watcher.Renamed += (_, e) => { Enqueue(area, e.OldFullPath); Enqueue(area, e.FullPath); };
        watcher.Error += (_, e) => { var message = e.GetException()?.Message ?? "The file system watcher stopped."; dispatcher.BeginInvoke(() => { if (!disposed) onError(message); }); };
        watcher.EnableRaisingEvents = true;
        watchers.Add(watcher);
    }

    private void Enqueue(WorkspaceArea area, string path)
    {
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return;
        lock (gate)
        {
            if (disposed) return;
            var now = DateTimeOffset.UtcNow;
            firstPending ??= now;
            pending[path] = new WorkspaceChange(area, path);
            timer.Change(ChangeDebounce.Delay(firstPending.Value, now), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Asks for another pass over these paths later (a file was still being written when it was read, or its test is held by an operation).</summary>
    public void Retry(IEnumerable<WorkspaceChange> changes, TimeSpan delay)
    {
        lock (gate)
        {
            if (disposed) return;
            foreach (var change in changes) pending[change.Path] = change;
            if (pending.Count > 0) { firstPending ??= DateTimeOffset.UtcNow; timer.Change(delay, Timeout.InfiniteTimeSpan); }
        }
    }

    private void Flush()
    {
        List<WorkspaceChange> changes;
        lock (gate)
        {
            if (disposed || pending.Count == 0) return;
            changes = pending.Values.ToList(); pending.Clear(); firstPending = null;
        }
        dispatcher.BeginInvoke(DispatcherPriority.Normal, () => { if (!disposed) onChanged(changes); });
    }

    /// <summary>Call right after the store wrote <paramref name="value"/> to <paramref name="path"/>: the bytes on disk are exactly this serialization.</summary>
    public void NoteOwnWrite<T>(string path, T value) => ledger.NoteWrite(path, value);
    public void NoteOwnDelete(string path) => ledger.NoteDelete(path);
    /// <summary>The delete did not happen after all.</summary>
    public void ForgetOwnDelete(string path) => ledger.ForgetDelete(path);
    /// <summary>True when the file holds exactly what this Studio last wrote there.</summary>
    public bool IsOwnWrite(string path, byte[] content) => ledger.IsOwnWrite(path, content);
    /// <summary>True (once) when this Studio deleted the file itself.</summary>
    public bool IsOwnDelete(string path) => ledger.IsOwnDelete(path);

    /// <summary>Reads a file another process may still be writing, without locking it. Throws IOException while the writer holds it exclusively.</summary>
    public static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; pending.Clear(); }
        timer.Dispose();
        foreach (var watcher in watchers) { try { watcher.EnableRaisingEvents = false; watcher.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { } }
        watchers.Clear();
    }
}
