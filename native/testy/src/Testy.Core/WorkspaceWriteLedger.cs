using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Testy.Core;

/// <summary>
/// Remembers what one process wrote to or deleted from a watched folder, so a file-system watcher can tell the process's own saves from
/// changes made by other tools: a save is recognized by content (the hash of exactly what was serialized), a delete by path, once. Entries are
/// forgotten after a minute.
/// </summary>
public sealed class WorkspaceWriteLedger
{
    private readonly TimeSpan memory;
    private readonly Dictionary<string, (string Hash, DateTimeOffset When)> writes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> deletes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    public WorkspaceWriteLedger(TimeSpan? memory = null) { this.memory = memory ?? TimeSpan.FromSeconds(60); }

    /// <summary>Call right after <paramref name="value"/> was written to <paramref name="path"/> with Testy's JSON options: the bytes on disk are exactly this serialization.</summary>
    public void NoteWrite<T>(string path, T value) => NoteWrite(path, new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(value, TestyJson.Options)));
    public void NoteWrite(string path, byte[] content) { lock (gate) { Purge(); writes[Path.GetFullPath(path)] = (Hash(content), DateTimeOffset.UtcNow); } }
    public void NoteDelete(string path) { lock (gate) { Purge(); deletes[Path.GetFullPath(path)] = DateTimeOffset.UtcNow; } }
    /// <summary>A delete that did not happen after all (the store threw): an outside delete of the file later is not this process's own.</summary>
    public void ForgetDelete(string path) { lock (gate) deletes.Remove(Path.GetFullPath(path)); }
    /// <summary>True when the file holds exactly what this process last wrote there (a byte order mark another tool added does not count as its own).</summary>
    public bool IsOwnWrite(string path, byte[] content)
    {
        lock (gate) { Purge(); return writes.TryGetValue(Path.GetFullPath(path), out var own) && own.Hash == Hash(content); }
    }
    /// <summary>True (once) when this process deleted the file itself.</summary>
    public bool IsOwnDelete(string path) { lock (gate) { Purge(); return deletes.Remove(Path.GetFullPath(path)); } }

    private void Purge()
    {
        var limit = DateTimeOffset.UtcNow - memory;
        foreach (var stale in writes.Where(w => w.Value.When < limit).Select(w => w.Key).ToList()) writes.Remove(stale);
        foreach (var stale in deletes.Where(d => d.Value < limit).Select(d => d.Key).ToList()) deletes.Remove(stale);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

/// <summary>
/// When a burst of file events is delivered: 300 ms after the last event (so a file written in several steps is read once), but never later than
/// one second after the first event that is still pending, so a steady stream of writes still shows up while it is going on.
/// </summary>
public static class ChangeDebounce
{
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(300), MaximumWait = TimeSpan.FromSeconds(1);
    /// <summary>How long to wait from <paramref name="now"/>, given when the oldest pending event arrived.</summary>
    public static TimeSpan Delay(DateTimeOffset firstPending, DateTimeOffset now)
    {
        var remaining = MaximumWait - (now - firstPending);
        if (remaining <= TimeSpan.Zero) return TimeSpan.Zero;
        return remaining < QuietPeriod ? remaining : QuietPeriod;
    }
}
