using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;

namespace Testy.Studio;

internal enum ActivityLevel { Info, Success, Warning, Error }

/// <summary>One timestamped Studio event: a status or assistant message, an error, a save, a run start or finish, or an Automate result.</summary>
internal sealed class ActivityEntry
{
    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;
    public ActivityLevel Level { get; init; }
    /// <summary>What produced it: "Run", "Assistant", "Tests", "Settings", "Automate", "App".</summary>
    public string Source { get; init; } = "App";
    public string Message { get; init; } = "";
    /// <summary>The original, technical text behind a friendly message (a raw runner message). Shown with technical details on, and in the log file.</summary>
    public string? Detail { get; init; }
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    /// <summary>The run this entry belongs to, when it was logged while a run was active (the "This run" filter).</summary>
    public string? RunId { get; set; }

    public string TimeText => Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
    public string FullTime => Time.ToLocalTime().ToString("dddd d MMMM yyyy, HH:mm:ss.fff", CultureInfo.CurrentCulture);
    public string LevelName => Level.ToString();
    public string AutomationName => $"{TimeText}, {Source}, {Message}";
    public override string ToString() => ActivityLog.Format(this);
}

/// <summary>
/// The Studio activity log: the latest <see cref="Capacity"/> entries in memory (bound to the Activity panel) and every entry appended to
/// %LOCALAPPDATA%\Testy\Logs\studio-YYYYMMDD.log. Each entry is appended with its own open, write and close, so several Studio
/// processes can share the daily file; a failed write never interrupts the app.
/// </summary>
internal sealed class ActivityLog
{
    public const int Capacity = 500;
    private static readonly int ProcessId = Environment.ProcessId;
    private string? lastKey;
    private DateTimeOffset lastTime;

    public ActivityLog(string? directory = null) =>
        LogDirectory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Testy", "Logs");

    public ObservableCollection<ActivityEntry> Entries { get; } = [];
    public string LogDirectory { get; }
    public string CurrentFile => Path.Combine(LogDirectory, "studio-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");

    /// <summary>Adds an entry. An identical message from the same source within two seconds is not repeated.</summary>
    public ActivityEntry? Add(ActivityLevel level, string source, string message, string? runId = null, string? detail = null)
    {
        message = (message ?? "").Trim();
        if (message.Length == 0) return null;
        var now = DateTimeOffset.Now;
        var key = $"{level}|{source}|{message}";
        if (key == lastKey && now - lastTime < TimeSpan.FromSeconds(2)) return null;
        lastKey = key; lastTime = now;
        detail = string.IsNullOrWhiteSpace(detail) || detail.Trim() == message ? null : detail.Trim();
        var entry = new ActivityEntry { Time = now, Level = level, Source = source, Message = message, Detail = detail, RunId = runId };
        Entries.Add(entry);
        while (Entries.Count > Capacity) Entries.RemoveAt(0);
        Append(entry);
        return entry;
    }

    /// <summary>"2026-09-28 14:05:01.123 pid=4120 ERROR   Run run=1a2b3c4d: message" (continuation lines are indented).</summary>
    public static string Format(ActivityEntry entry)
    {
        var builder = new StringBuilder();
        builder.Append(entry.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(" pid=").Append(ProcessId.ToString(CultureInfo.InvariantCulture)).Append(' ')
            .Append(entry.Level.ToString().ToUpperInvariant().PadRight(8)).Append(entry.Source);
        if (!string.IsNullOrEmpty(entry.RunId)) builder.Append(" run=").Append(entry.RunId.Length > 8 ? entry.RunId[..8] : entry.RunId);
        builder.Append(": ").Append(entry.Message.Replace("\r\n", "\n").Replace("\n", Environment.NewLine + "    "));
        if (entry.HasDetail) builder.Append(Environment.NewLine).Append("    ").Append(entry.Detail!.Replace("\r\n", "\n").Replace("\n", Environment.NewLine + "    "));
        return builder.ToString();
    }

    /// <summary>All entries in memory as text (Copy all).</summary>
    public static string Text(IEnumerable<ActivityEntry> entries) => string.Join(Environment.NewLine, entries.Select(Format));

    private void Append(ActivityEntry entry)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            using var stream = new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new UTF8Encoding(false).GetBytes(Format(entry) + Environment.NewLine);
            stream.Write(bytes, 0, bytes.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { } // The log is a convenience; the app keeps working.
    }
}
