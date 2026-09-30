using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Testy.Cli.Mcp;

/// <summary>Serialized single-line writes to one output stream (stdout or an in-memory pipe).</summary>
internal sealed class McpLineWriter(Stream output)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    /// <summary>One message, or the array that answers a batch, as one line.</summary>
    public async Task WriteAsync(JsonNode message)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonRpc.Serialize(message) + "\n");
        await gate.WaitAsync();
        try { await output.WriteAsync(bytes); await output.FlushAsync(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { /* The reader closed the pipe; stdin reaches end-of-file next. */ }
        finally { gate.Release(); }
    }
}

/// <summary>
/// The stdio binding: newline-delimited JSON-RPC over stdin/stdout. Every message is registered in arrival order (so a cancellation always finds
/// the request written before it) and then handled concurrently, so pings and cancellations are answered while a UI tool call runs; the service
/// serializes desktop work itself. A batch is answered with one array. A leading byte order mark and CRLF line ends are tolerated; a line above
/// 32 MiB is refused as soon as the limit is crossed. End of input is the shutdown signal.
/// </summary>
internal static class McpStdioTransport
{
    public const int MaximumLineBytes = JsonRpc.MaximumMessageBytes;

    public static async Task<int> RunAsync(McpServer server, Stream input, Stream output, McpLog log, CancellationToken shutdown)
    {
        var connection = new McpConnection("stdio", "stdio");
        var writer = new McpLineWriter(output);
        var pending = new ConcurrentDictionary<Task, byte>();
        Func<JsonObject, Task> notify = message => writer.WriteAsync(message);
        void Track(Func<Task> work)
        {
            var task = Task.Run(work, CancellationToken.None);
            pending[task] = 0;
            _ = task.ContinueWith(t => pending.TryRemove(t, out _), TaskScheduler.Default);
        }
        log.Info("stdio transport ready: newline-delimited JSON-RPC 2.0 on stdin/stdout; logs on stderr.");
        try
        {
            await foreach (var (raw, tooLong) in ReadLinesAsync(input, shutdown))
            {
                if (tooLong) { await writer.WriteAsync(JsonRpc.Error(null, JsonRpc.ParseError, $"Parse error: a message exceeded {MaximumLineBytes} bytes (32 MiB); the rest of that line is ignored.")); continue; }
                var line = JsonRpc.WithoutByteOrderMark(raw);
                if (IsBlank(line.Span)) continue;
                if (!JsonRpc.TryParse(line, out var root, out var problem)) { await writer.WriteAsync(JsonRpc.Error(null, JsonRpc.ParseError, problem)); continue; }
                if (root.ValueKind != JsonValueKind.Array)
                {
                    var single = server.Begin(root, connection, notify, shutdown);
                    if (single.Settled) { if (single.Reply is { } settled) await writer.WriteAsync(settled.Message); continue; }
                    Track(async () => { if (await single.RunAsync() is { } reply) await writer.WriteAsync(reply.Message); });
                    continue;
                }
                var entries = root.EnumerateArray().ToArray();
                if (entries.Length == 0) { await writer.WriteAsync(JsonRpc.Error(null, JsonRpc.InvalidRequest, "An empty batch is not a valid request.")); continue; }
                var batch = entries.Select(entry => server.Begin(entry, connection, notify, shutdown, inBatch: true)).ToArray();
                Track(async () =>
                {
                    var replies = await Task.WhenAll(batch.Select(entry => entry.RunAsync()));
                    var answers = new JsonArray(replies.Where(reply => reply is not null).Select(reply => (JsonNode?)reply!.Message).ToArray());
                    if (answers.Count > 0) await writer.WriteAsync(answers); // nothing at all when every entry was a notification or was cancelled
                });
            }
            log.Info("stdin closed; shutting down.");
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { log.Info("Shutdown requested; stopping the stdio transport."); }
        connection.Close();
        var outstanding = pending.Keys.ToArray();
        if (outstanding.Length > 0)
        {
            try { await Task.WhenAll(outstanding).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { log.Warn($"{outstanding.Count(t => !t.IsCompleted)} request(s) were still running at shutdown."); }
        }
        return 0;
    }

    private static bool IsBlank(ReadOnlySpan<byte> line)
    {
        foreach (var value in line) if (value is not ((byte)' ' or (byte)'\t' or (byte)'\r')) return false;
        return true;
    }

    private static async IAsyncEnumerable<(ReadOnlyMemory<byte> Line, bool TooLong)> ReadLinesAsync(Stream input, [EnumeratorCancellation] CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        var line = new MemoryStream();
        var discarding = false;
        while (true)
        {
            // Console input cannot be cancelled, so race the read against the shutdown token instead of blocking on it.
            var read = input.ReadAsync(buffer, token).AsTask();
            var finished = await Task.WhenAny(read, Task.Delay(Timeout.Infinite, token));
            if (finished != read) token.ThrowIfCancellationRequested();
            var count = await read;
            if (count == 0)
            {
                if (line.Length > 0 && !discarding) yield return (TrimCarriageReturn(line.ToArray()), false);
                yield break;
            }
            var start = 0;
            for (var index = 0; index < count; index++)
            {
                if (buffer[index] != (byte)'\n') continue;
                if (discarding) discarding = false; // the oversized line ends here; its error was reported when the limit was crossed
                else
                {
                    line.Write(buffer, start, index - start);
                    if (line.Length > MaximumLineBytes) yield return (default, true);
                    else yield return (TrimCarriageReturn(line.ToArray()), false);
                    if (line.Capacity > buffer.Length * 4) line = new MemoryStream(); else line.SetLength(0);
                }
                start = index + 1;
            }
            if (discarding) continue;
            line.Write(buffer, start, count - start);
            if (line.Length <= MaximumLineBytes) continue;
            // Report at once: a client that sent an oversized message without a newline must not wait for an answer that cannot come.
            discarding = true;
            line = new MemoryStream();
            yield return (default, true);
        }
    }

    private static ReadOnlyMemory<byte> TrimCarriageReturn(byte[] line) => line.Length > 0 && line[^1] == (byte)'\r' ? line.AsMemory(0, line.Length - 1) : line;
}
