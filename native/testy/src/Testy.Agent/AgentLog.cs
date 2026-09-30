using System.Text;

namespace Testy.Agent;

/// <summary>Daily agent log under the agent state folder, kept for seven days. Never receives secrets.</summary>
internal sealed class AgentLog
{
    private readonly string directory;
    private readonly object gate = new();
    public AgentLog(string directory) { this.directory = directory; System.IO.Directory.CreateDirectory(directory); Prune(); }
    public string Folder => directory;
    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message, Exception? ex = null)
    {
        var detail = ex is null ? "" : " " + ex.GetType().Name + ": " + ex.Message + " @ " + (ex.StackTrace?.ReplaceLineEndings(" | ") ?? "");
        Write("ERROR", message + (detail.Length > 2000 ? detail[..2000] : detail));
    }
    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.UtcNow:O} {level} {message.ReplaceLineEndings(" ")}{Environment.NewLine}";
        lock (gate)
        {
            try { File.AppendAllText(Path.Combine(directory, $"agent-{DateTime.UtcNow:yyyyMMdd}.log"), line, Encoding.UTF8); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    private void Prune()
    {
        try
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(directory, "agent-*.log"))
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-7)) File.Delete(file);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
