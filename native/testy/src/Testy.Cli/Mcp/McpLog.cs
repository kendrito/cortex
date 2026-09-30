namespace Testy.Cli.Mcp;

/// <summary>
/// Diagnostics for the MCP server. Lines go to stderr (never stdout, which is the protocol channel) and, when a workspace is known, to
/// mcp\logs\mcp-YYYYMMDD.log. Callers never pass a bearer token or a whole session id: both are credentials.
/// </summary>
internal sealed class McpLog
{
    private readonly TextWriter sink;
    private readonly string? directory;
    private readonly object gate = new();
    public McpLog(TextWriter sink, string? logDirectory) { this.sink = sink; directory = logDirectory; }
    public static McpLog Silent { get; } = new(TextWriter.Null, null);
    public static string FileName(DateTime utc) => $"mcp-{utc:yyyyMMdd}.log";
    public void Info(string message) => Write("info", message);
    public void Warn(string message) => Write("warn", message);
    public void Error(string message) => Write("error", message);
    private void Write(string level, string message)
    {
        var line = $"[testy-mcp] {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff} {level} {message.ReplaceLineEndings(" ")}";
        lock (gate)
        {
            try { sink.WriteLine(line); sink.Flush(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            if (directory is null) return;
            try
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, FileName(DateTime.UtcNow)), line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
