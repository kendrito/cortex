using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Testy.Cli.Mcp;

/// <summary>One tools/call outcome as a client sees it: protocol error, tool error (isError) or content plus structuredContent.</summary>
internal sealed class McpToolCall
{
    public McpToolCall(JsonElement response)
    {
        Response = response;
        if (response.TryGetProperty("error", out var error)) Error = error;
        else if (response.TryGetProperty("result", out var result)) Result = result;
    }
    public JsonElement Response { get; }
    public JsonElement? Error { get; }
    public JsonElement? Result { get; }
    public int? ErrorCode => Error is { } e && e.TryGetProperty("code", out var code) ? code.GetInt32() : null;
    public bool IsError => Error is not null || (Result is { } r && r.TryGetProperty("isError", out var flag) && flag.ValueKind == JsonValueKind.True);
    public JsonElement Structured => Result is { } r && r.TryGetProperty("structuredContent", out var s) ? s : throw new InvalidOperationException("The tool result has no structuredContent: " + Response.GetRawText());
    public string Text => Result is { } r && r.TryGetProperty("content", out var content)
        ? string.Join("\n", content.EnumerateArray().Where(c => c.TryGetProperty("type", out var t) && t.GetString() == "text").Select(c => c.GetProperty("text").GetString()))
        : Error?.GetRawText() ?? "";
    public List<byte[]> Images => Result is { } r && r.TryGetProperty("content", out var content)
        ? content.EnumerateArray().Where(c => c.TryGetProperty("type", out var t) && t.GetString() == "image").Select(c => Convert.FromBase64String(c.GetProperty("data").GetString()!)).ToList() : [];
    public McpToolCall RequireOk(string what)
    {
        if (Error is { } error) throw new InvalidOperationException($"{what}: protocol error {error.GetProperty("code").GetInt32()}: {error.GetProperty("message").GetString()}");
        if (IsError) throw new InvalidOperationException($"{what}: {Text}");
        return this;
    }
}

/// <summary>
/// A generic MCP client over newline-delimited JSON-RPC streams (a child process's stdio or in-memory pipes). It correlates responses by id,
/// collects notifications, and keeps every raw line so callers can check that the server wrote nothing but JSON objects.
/// </summary>
internal sealed class McpLineClient : IAsyncDisposable
{
    private readonly Stream toServer;
    private readonly StreamReader fromServer;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> waiting = new(StringComparer.Ordinal);
    private readonly List<string> raw = [];
    private readonly List<JsonElement> notifications = [];
    private readonly List<JsonElement> unexpectedResponses = [];
    private readonly List<JsonElement> batches = [];
    private readonly object gate = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private int nextId;
    public McpLineClient(Stream fromServer, Stream toServer)
    {
        this.toServer = toServer;
        this.fromServer = new StreamReader(fromServer, new UTF8Encoding(false), false, 65536);
        Reader = Task.Run(ReadLoopAsync);
    }
    /// <summary>Completes when the server closes its output.</summary>
    public Task Reader { get; }
    public int UnparseableLines { get; private set; }
    public IReadOnlyList<string> RawLines { get { lock (gate) return raw.ToArray(); } }
    public IReadOnlyList<JsonElement> Notifications { get { lock (gate) return notifications.ToArray(); } }
    public IReadOnlyList<JsonElement> UnexpectedResponses { get { lock (gate) return unexpectedResponses.ToArray(); } }
    /// <summary>The arrays that answered JSON-RPC batches, in arrival order.</summary>
    public IReadOnlyList<JsonElement> Batches { get { lock (gate) return batches.ToArray(); } }

    private async Task ReadLoopAsync()
    {
        while (true)
        {
            string? line;
            try { line = await fromServer.ReadLineAsync(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { break; }
            if (line is null) break;
            lock (gate) raw.Add(line);
            JsonElement element;
            try { using var document = JsonDocument.Parse(line); element = document.RootElement.Clone(); }
            catch (JsonException) { lock (gate) UnparseableLines++; continue; }
            // The answer to a JSON-RPC batch is one array of response objects on one line.
            if (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > 0 && element.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Object)) { lock (gate) batches.Add(element); continue; }
            if (element.ValueKind != JsonValueKind.Object) { lock (gate) UnparseableLines++; continue; }
            var isResponse = !element.TryGetProperty("method", out _) && (element.TryGetProperty("result", out _) || element.TryGetProperty("error", out _));
            if (isResponse)
            {
                // A response whose id is null (parse errors) or unknown is kept separately so a caller can still inspect it.
                if (element.TryGetProperty("id", out var id) && JsonRpc.IsValidId(id) && waiting.TryRemove(JsonRpc.IdKey(id), out var pending)) pending.TrySetResult(element);
                else lock (gate) unexpectedResponses.Add(element);
                continue;
            }
            lock (gate) notifications.Add(element);
        }
        foreach (var pending in waiting.Values) pending.TrySetException(new IOException("The MCP server closed its output before answering."));
    }

    public JsonNode NextId() => JsonValue.Create(Interlocked.Increment(ref nextId));
    public Task<JsonElement> RequestAsync(string method, object? parameters = null, TimeSpan? timeout = null) => RequestAsync(method, parameters, NextId(), timeout);
    public async Task<JsonElement> RequestAsync(string method, object? parameters, JsonNode id, TimeSpan? timeout = null)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["method"] = method };
        if (parameters is not null) message["params"] = ToNode(parameters);
        var key = id is JsonValue value && value.TryGetValue<string>(out var text) ? "s:" + text : "n:" + id.ToJsonString();
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        waiting[key] = pending;
        await SendAsync(JsonRpc.Serialize(message));
        var limit = timeout ?? TimeSpan.FromSeconds(60);
        try { return await pending.Task.WaitAsync(limit); }
        catch (TimeoutException) { waiting.TryRemove(key, out _); throw new TimeoutException($"No response to {method} (id {id.ToJsonString()}) within {limit.TotalSeconds:F0} s."); }
    }
    public Task NotifyAsync(string method, object? parameters = null)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null) message["params"] = ToNode(parameters);
        return SendAsync(JsonRpc.Serialize(message));
    }
    public async Task<McpToolCall> CallToolAsync(string name, object? arguments = null, string? progressToken = null, TimeSpan? timeout = null, JsonNode? id = null)
    {
        var parameters = new JsonObject { ["name"] = name, ["arguments"] = arguments is null ? new JsonObject() : ToNode(arguments) };
        if (progressToken is not null) parameters["_meta"] = new JsonObject { ["progressToken"] = progressToken };
        return new McpToolCall(await RequestAsync("tools/call", parameters, id ?? NextId(), timeout));
    }
    public Task SendAsync(string line) => SendBytesAsync(Encoding.UTF8.GetBytes(line + "\n"));
    /// <summary>Writes the bytes as they are (no newline is added), for framing checks.</summary>
    public async Task SendBytesAsync(byte[] bytes)
    {
        await writeGate.WaitAsync();
        try { await toServer.WriteAsync(bytes); await toServer.FlushAsync(); }
        finally { writeGate.Release(); }
    }
    /// <summary>
    /// Sends a request whose answer may never come (it is about to be cancelled). The returned task completes with the response if one
    /// arrives, and is cancelled when the server closes its output without answering.
    /// </summary>
    public Task<JsonElement> StartAsync(string method, object? parameters, JsonNode id)
    {
        var pending = RequestAsync(method, parameters, id, Timeout.InfiniteTimeSpan);
        _ = pending.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        return pending;
    }
    public Task<JsonElement> StartToolAsync(string name, object? arguments, JsonNode id, string? progressToken = null)
    {
        var parameters = new JsonObject { ["name"] = name, ["arguments"] = arguments is null ? new JsonObject() : ToNode(arguments) };
        if (progressToken is not null) parameters["_meta"] = new JsonObject { ["progressToken"] = progressToken };
        return StartAsync("tools/call", parameters, id);
    }
    public async Task<JsonElement> WaitForNotificationAsync(Func<JsonElement, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            var match = Notifications.FirstOrDefault(predicate);
            if (match.ValueKind == JsonValueKind.Object) return match;
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException("The expected notification did not arrive.");
            await Task.Delay(50);
        }
    }
    /// <summary>Closing the server's stdin is the MCP shutdown signal.</summary>
    public void CloseInput() { try { toServer.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } }
    private static JsonNode ToNode(object parameters) => parameters as JsonNode ?? JsonRpc.ToNode(parameters);
    public async ValueTask DisposeAsync()
    {
        CloseInput();
        try { await Reader.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception ex) when (ex is TimeoutException or IOException) { }
    }
}
