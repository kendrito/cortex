using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Testy.Cli.Mcp;

/// <summary>
/// A JSON-RPC protocol error (the request itself is wrong). Tool execution problems are reported inside results with isError instead.
/// <paramref name="httpStatus"/> is the HTTP status the Streamable HTTP transport answers with when the code alone does not decide it.
/// </summary>
internal sealed class McpProtocolException(int code, string message, JsonNode? data = null, int? httpStatus = null) : Exception(message)
{
    public int Code { get; } = code;
    public JsonNode? ErrorData { get; } = data;
    public int? HttpStatus { get; } = httpStatus;
}

/// <summary>What a transport writes for one request: the JSON-RPC response and, over HTTP, its status.</summary>
internal sealed record McpReply(JsonObject Message, int HttpStatus);

/// <summary>One request that is registered as in flight: its cancellation token and who cancelled it.</summary>
internal sealed class McpInflight(string key, CancellationTokenSource source)
{
    private int cancelledByClient;
    public string Key => key;
    public CancellationTokenSource Source => source;
    public CancellationToken Token { get; } = source.Token;
    /// <summary>True when the client cancelled (notifications/cancelled, or it closed its response stream), as opposed to the server stopping.</summary>
    public bool CancelledByClient => Volatile.Read(ref cancelledByClient) != 0;
    public bool Cancel(bool byClient)
    {
        if (byClient) Volatile.Write(ref cancelledByClient, 1);
        try { source.Cancel(); return true; } catch (ObjectDisposedException) { return false; }
    }
}

/// <summary>Per-connection state: the stdio process, one HTTP session of a handshake revision, or one stateless HTTP request.</summary>
internal sealed class McpConnection
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<string, McpInflight> inflight = new(StringComparer.Ordinal);
    private long lastActivity = DateTimeOffset.UtcNow.UtcTicks;
    public McpConnection(string id, string transport) { Id = id; Transport = transport; }
    public string Id { get; }
    /// <summary>What log lines show instead of the id: an HTTP session id is a credential for that session.</summary>
    public string LogId => Id.Length <= 8 ? Id : Id[..8] + "…";
    public string Transport { get; }
    public string? NegotiatedVersion { get; set; }
    public bool InitializeReceived { get; set; }
    public bool Initialized { get; set; }
    public string LogLevel { get; set; } = "info";
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActivity => new(Interlocked.Read(ref lastActivity), TimeSpan.Zero);
    public CancellationToken Closed => lifetime.Token;
    public int InFlight => inflight.Count;
    public void Touch() => Interlocked.Exchange(ref lastActivity, DateTimeOffset.UtcNow.UtcTicks);
    /// <summary>Registers a request id; false when that id is still in flight (JSON-RPC ids must be unique until answered).</summary>
    public bool TryBegin(string key, CancellationToken shutdown, out McpInflight request)
    {
        request = new McpInflight(key, CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, shutdown));
        if (inflight.TryAdd(key, request)) return true;
        request.Source.Dispose();
        return false;
    }
    /// <summary>The request is over. Idle time counts from here, so a session is not swept right after a long call finished.</summary>
    public void End(McpInflight request)
    {
        inflight.TryRemove(new KeyValuePair<string, McpInflight>(request.Key, request));
        request.Source.Dispose();
        Touch();
    }
    /// <summary>notifications/cancelled for a request of this connection.</summary>
    public bool Cancel(string key) => inflight.TryGetValue(key, out var request) && request.Cancel(byClient: true);
    /// <summary>Cancels every in-flight request; nothing is written for them afterwards (a subscription stream sends its closure messages first).</summary>
    public void Close() { try { lifetime.Cancel(); } catch (ObjectDisposedException) { } }
}

/// <summary>Everything a method handler needs: the request, the negotiated revision, progress reporting and the cancellation token tied to notifications/cancelled.</summary>
internal sealed class McpRequestContext(JsonElement id, string method, JsonElement? parameters, McpConnection connection, string protocolVersion, bool modern, JsonElement? progressToken, Func<JsonObject, Task> notify, McpInflight inflight)
{
    /// <summary>At most one progress notification per token in this interval; the newest value wins.</summary>
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);
    private readonly object gate = new();
    private Channel<JsonObject>? queue;
    private Task? pump;
    private bool finished;
    private double lastProgress = double.NegativeInfinity;
    public JsonElement Id => id;
    public string Method => method;
    public JsonElement? Params => parameters;
    public McpConnection Connection => connection;
    public string ProtocolVersion => protocolVersion;
    /// <summary>True for revision 2026-07-28 requests: results carry resultType, serverInfo and cache hints.</summary>
    public bool Modern => modern;
    public bool WantsProgress => progressToken is not null;
    public CancellationToken Token => inflight.Token;
    public bool CancelledByClient => inflight.CancelledByClient;
    /// <summary>Set by a handler whose result must be written although its token was cancelled (a subscription stream closed by the server).</summary>
    public bool RespondAfterCancellation { get; set; }
    public JsonElement? Param(string name) => parameters is { } p && p.TryGetProperty(name, out var value) ? value : null;
    public string? StringParam(string name) => Param(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    /// <summary>
    /// Queues notifications/progress for this request and returns at once, so a slow reader never stretches the timing of the work that reports.
    /// Values are forced to increase, as the specification requires; nothing is queued once the request was cancelled or answered.
    /// </summary>
    public void Progress(double progress, double? total, string? message)
    {
        if (progressToken is null || inflight.Token.IsCancellationRequested) return;
        lock (gate)
        {
            if (finished) return;
            if (total is { } limit && progress > limit) progress = limit;
            if (progress <= lastProgress)
            {
                progress = lastProgress + 0.01;
                if (total is { } ceiling && progress > ceiling) return; // nothing above the total is ever reported
            }
            lastProgress = progress;
            var payload = new JsonObject { ["progressToken"] = JsonRpc.Clone(progressToken.Value), ["progress"] = progress };
            if (total is { } known) payload["total"] = known;
            if (!string.IsNullOrEmpty(message)) payload["message"] = message;
            queue ??= Channel.CreateUnbounded<JsonObject>(new UnboundedChannelOptions { SingleReader = true });
            pump ??= Task.Run(PumpAsync);
            queue.Writer.TryWrite(JsonRpc.Notification("notifications/progress", payload));
        }
    }

    private async Task PumpAsync()
    {
        var reader = queue!.Reader;
        var sinceLast = Stopwatch.StartNew();
        var first = true;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            JsonObject? latest = null;
            while (reader.TryRead(out var item)) latest = item;
            var wait = ProgressInterval - sinceLast.Elapsed;
            if (!first && wait > TimeSpan.Zero && !reader.Completion.IsCompleted)
            {
                await Task.WhenAny(Task.Delay(wait), reader.Completion).ConfigureAwait(false);
                while (reader.TryRead(out var item)) latest = item;
            }
            if (latest is null || inflight.Token.IsCancellationRequested) continue; // a cancelled request sends nothing more
            first = false;
            sinceLast.Restart();
            await SendAsync(latest).ConfigureAwait(false);
        }
    }

    /// <summary>A notification that belongs to this request, written directly (the acknowledgement and closure of a subscription stream).</summary>
    public Task NotifyAsync(JsonObject notification) => SendAsync(notification);

    private async Task SendAsync(JsonObject notification)
    {
        try { await notify(notification).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or System.Net.HttpListenerException) { /* The client went away; the request token observes that separately. */ }
    }

    /// <summary>Ends progress reporting. The queued notifications are written first, so none follows the response.</summary>
    public async Task FinishProgressAsync()
    {
        Task? running;
        lock (gate) { finished = true; queue?.Writer.TryComplete(); running = pump; }
        if (running is not null) await running.ConfigureAwait(false);
    }
}

/// <summary>
/// One received message after <see cref="McpServer.Begin"/>: either already settled (a notification, a client response, or a request answered
/// on the spot) or a registered request whose work <see cref="RunAsync"/> performs.
/// </summary>
internal sealed class McpPending
{
    private readonly Func<Task<McpReply?>>? run;
    private readonly McpInflight? inflight;
    private Task<McpReply?>? running;
    private McpPending(McpReply? reply, string method, string? idKey, bool modern) { Reply = reply; Method = method; IdKey = idKey; Modern = modern; Settled = true; }
    private McpPending(Func<Task<McpReply?>> run, McpInflight inflight, string method, bool modern, bool streamable)
    { this.run = run; this.inflight = inflight; Method = method; IdKey = inflight.Key; Modern = modern; Streamable = streamable; }
    public static McpPending Done(McpReply? reply, string method = "", string? idKey = null, bool modern = false) => new(reply, method, idKey, modern);
    public static McpPending Work(Func<Task<McpReply?>> run, McpInflight inflight, string method, bool modern, bool streamable) => new(run, inflight, method, modern, streamable);
    /// <summary>Nothing is left to run; <see cref="Reply"/> is the answer, or null when none is due.</summary>
    public bool Settled { get; }
    public McpReply? Reply { get; }
    public string Method { get; }
    public string? IdKey { get; }
    public bool Modern { get; }
    /// <summary>A long-running method whose HTTP answer is a stream the client can close to cancel it (tools/call, subscriptions/listen).</summary>
    public bool Streamable { get; }
    /// <summary>The client gave up on this request: it closed its response stream or named it in a stateless notifications/cancelled.</summary>
    public void CancelByClient() => inflight?.Cancel(byClient: true);
    /// <summary>Runs the request once; every caller receives the same outcome. Null means no response is written (the request was cancelled).</summary>
    public Task<McpReply?> RunAsync() => Settled ? Task.FromResult(Reply) : running ??= run!();
}

/// <summary>
/// Transport-agnostic MCP server core. It serves the handshake revisions (2024-11-05 … 2025-11-25: initialize, sessions) and the
/// stateless revision 2026-07-28 (per-request _meta, server/discover, subscriptions/listen) at the same time, selecting the behaviour from
/// how each request opens. <see cref="Begin"/> is synchronous and is called in arrival order; the work itself runs concurrently.
/// </summary>
internal sealed class McpServer
{
    public const string Name = "testy", Title = "Testy";
    public static string Version { get; } = ProductVersion();
    /// <summary>Handshake-based revisions, newest first; the first entry answers an initialize that asks for an unknown version.</summary>
    public static readonly string[] HandshakeVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];
    /// <summary>Stateless revisions that declare their version on every request.</summary>
    public static readonly string[] StatelessVersions = ["2026-07-28"];
    public static readonly string[] SupportedVersions = [.. StatelessVersions, .. HandshakeVersions];
    public static string LatestHandshakeVersion => HandshakeVersions[0];
    public const string MetaProtocolVersion = "io.modelcontextprotocol/protocolVersion", MetaClientInfo = "io.modelcontextprotocol/clientInfo",
        MetaClientCapabilities = "io.modelcontextprotocol/clientCapabilities", MetaServerInfo = "io.modelcontextprotocol/serverInfo",
        MetaLogLevel = "io.modelcontextprotocol/logLevel", MetaSubscriptionId = "io.modelcontextprotocol/subscriptionId";
    /// <summary>Open subscriptions/listen streams per process. They carry no notifications (this server has none), so a few are plenty.</summary>
    public const int MaximumListenStreams = 16;
    private static readonly string[] LogLevels = ["debug", "info", "notice", "warning", "error", "critical", "alert", "emergency"];
    private static readonly string[] Methods =
    [
        "initialize", "ping", "server/discover", "tools/list", "tools/call", "resources/list", "resources/templates/list", "resources/read",
        "prompts/list", "prompts/get", "logging/setLevel", "subscriptions/listen"
    ];
    /// <summary>Methods of the handshake revisions that revision 2026-07-28 removed.</summary>
    private static readonly string[] HandshakeOnly = ["initialize", "ping", "logging/setLevel"];
    private readonly McpLog log;
    private int listening;

    public McpServer(TestyMcpService service, McpLog log)
    {
        Service = service;
        this.log = log;
        Tools = new McpToolCatalog(service);
    }
    public TestyMcpService Service { get; }
    public McpToolCatalog Tools { get; }
    public int ListenStreams => Volatile.Read(ref listening);

    public static JsonObject ServerInfo() => new() { ["name"] = Name, ["title"] = Title, ["version"] = Version };
    /// <summary>logging/setLevel exists only in the handshake revisions; 2026-07-28 removed it.</summary>
    public static JsonObject Capabilities(bool modern)
    {
        var capabilities = new JsonObject
        {
            ["tools"] = new JsonObject { ["listChanged"] = false },
            ["resources"] = new JsonObject { ["subscribe"] = false, ["listChanged"] = false },
            ["prompts"] = new JsonObject { ["listChanged"] = false }
        };
        if (!modern) capabilities["logging"] = new JsonObject();
        return capabilities;
    }
    public static bool IsStateless(string? version) => version is not null && StatelessVersions.Contains(version, StringComparer.Ordinal);
    /// <summary>The per-request protocol version when params._meta carries one as a string.</summary>
    public static string? MetaVersion(JsonElement? parameters) =>
        parameters is { } p && p.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object
        && meta.TryGetProperty(MetaProtocolVersion, out var version) && version.ValueKind == JsonValueKind.String ? version.GetString() : null;
    /// <summary>True when the body declares a protocol version that is not one of the handshake revisions (a stateless or an unknown one): such a message is not part of a session.</summary>
    public static bool DeclaresStatelessVersion(JsonElement? parameters) => MetaVersion(parameters) is { } version && !HandshakeVersions.Contains(version, StringComparer.Ordinal);

    /// <summary>Handles one message completely. Returns the response to write, or null for notifications, client responses and cancelled requests.</summary>
    public async Task<JsonObject?> HandleAsync(JsonElement element, McpConnection connection, Func<JsonObject, Task> notify, CancellationToken shutdown) =>
        (await Begin(element, connection, notify, shutdown).RunAsync())?.Message;

    /// <summary>
    /// The synchronous first half of handling a message, called in arrival order: classification, envelope validation, registration of the
    /// request as in flight, and everything a notification or initialize does. A cancellation that follows its request on the wire therefore
    /// always finds it, and a request that follows initialize always sees the negotiated version.
    /// </summary>
    public McpPending Begin(JsonElement element, McpConnection connection, Func<JsonObject, Task> notify, CancellationToken shutdown, string? headerVersion = null, bool inBatch = false)
    {
        try { return BeginCore(element, connection, notify, shutdown, headerVersion, inBatch); }
        catch (Exception ex) when (ex is not McpProtocolException)
        {
            log.Error("A message could not be handled: " + ex);
            JsonElement? id = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("id", out var raw) && JsonRpc.IsValidId(raw) ? raw : null;
            return McpPending.Done(new McpReply(JsonRpc.Error(id, JsonRpc.InternalError, "Internal error: " + ex.Message), 500));
        }
    }

    private McpPending BeginCore(JsonElement element, McpConnection connection, Func<JsonObject, Task> notify, CancellationToken shutdown, string? headerVersion, bool inBatch)
    {
        connection.Touch();
        var message = JsonRpc.Classify(element);
        if (message.Kind == JsonRpcKind.Invalid) return McpPending.Done(new McpReply(JsonRpc.Error(message.Id, message.ProblemCode, message.Problem), 400));
        if (message.Kind == JsonRpcKind.Response) { log.Info($"Ignoring a client response (id {message.Id?.GetRawText() ?? "absent"}); this server sends no requests."); return McpPending.Done(null); }
        if (message.Kind == JsonRpcKind.Notification) { HandleNotification(message, connection); return McpPending.Done(null, message.Method); }
        var id = message.Id!.Value;
        var key = JsonRpc.IdKey(id);
        McpPending Refuse(McpProtocolException problem, bool modernRequest) =>
            McpPending.Done(new McpReply(JsonRpc.Error(id, problem.Code, problem.Message, problem.ErrorData), problem.HttpStatus ?? StatusFor(problem.Code, modernRequest)), message.Method, key, modernRequest);

        string version; bool modern;
        try { (version, modern) = ValidateEnvelope(message, connection, headerVersion); }
        catch (McpProtocolException ex) { return Refuse(ex, modernRequest: true); }
        try
        {
            if (inBatch && message.Method == "initialize") throw new McpProtocolException(JsonRpc.InvalidRequest, "initialize must be sent on its own, not inside a batch.");
            if (inBatch && modern) throw new McpProtocolException(JsonRpc.InvalidRequest, $"JSON-RPC batches are not part of revision {version}; send each request on its own.");
            RequireMethod(message.Method, modern, connection);
            var progressToken = ProgressToken(message.Params);
            if (message.Method == "tools/call") ValidateToolCall(message.Params);
            if (message.Method == "subscriptions/listen") ValidateListen(message.Params);
            if (!connection.TryBegin(key, shutdown, out var inflight))
                throw new McpProtocolException(JsonRpc.InvalidRequest, $"Request id {id.GetRawText()} is still in flight; ids must be unique until they are answered.");
            var context = new McpRequestContext(id, message.Method, message.Params, connection, version, modern, progressToken, notify, inflight);
            if (message.Method == "initialize")
            {
                // Settled here so the next message on the wire already sees the negotiated version.
                try { return McpPending.Done(new McpReply(JsonRpc.Response(id, Initialize(context)), 200), message.Method, key); }
                finally { connection.End(inflight); }
            }
            return McpPending.Work(() => RunAsync(context, inflight), inflight, message.Method, modern, streamable: message.Method is "tools/call" or "subscriptions/listen");
        }
        catch (McpProtocolException ex) { return Refuse(ex, modern); }
    }

    /// <summary>
    /// Decides which revision a request belongs to and checks what that revision requires of every request. A request is a 2026-07-28 request
    /// when its _meta names that revision, when the HTTP MCP-Protocol-Version header does, or when its method exists only there
    /// (server/discover; subscriptions/listen on a connection that made no initialize handshake).
    /// </summary>
    private static (string Version, bool Modern) ValidateEnvelope(JsonRpcMessage message, McpConnection connection, string? headerVersion)
    {
        static McpProtocolException Invalid(string text) => new(JsonRpc.InvalidParams, text, httpStatus: 400);
        JsonElement? meta = null;
        if (message.Params is { } parameters && parameters.TryGetProperty("_meta", out var supplied))
        {
            if (supplied.ValueKind != JsonValueKind.Object) throw Invalid("params._meta must be a JSON object.");
            meta = supplied;
        }
        string? requested = null;
        if (meta is { } m && m.TryGetProperty(MetaProtocolVersion, out var declared))
        {
            if (declared.ValueKind != JsonValueKind.String) throw Invalid($"params._meta \"{MetaProtocolVersion}\" must be a string such as \"{StatelessVersions[0]}\".");
            requested = declared.GetString();
        }
        if (requested is not null && !SupportedVersions.Contains(requested, StringComparer.Ordinal))
            throw new McpProtocolException(JsonRpc.UnsupportedProtocolVersion, "Unsupported protocol version",
                new JsonObject { ["supported"] = JsonRpc.Strings(SupportedVersions), ["requested"] = requested }, httpStatus: 400);
        if (requested is null)
        {
            var modernOnly = IsStateless(headerVersion) || message.Method == "server/discover" || (message.Method == "subscriptions/listen" && !connection.InitializeReceived);
            if (modernOnly)
                throw Invalid($"{message.Method} is a revision {StatelessVersions[0]} request, and every such request must carry params._meta with \"{MetaProtocolVersion}\" and \"{MetaClientCapabilities}\".");
            return (connection.NegotiatedVersion ?? LatestHandshakeVersion, false);
        }
        if (!IsStateless(requested)) return (requested, false);
        var fields = meta!.Value;
        if (!fields.TryGetProperty(MetaClientCapabilities, out var capabilities) || capabilities.ValueKind != JsonValueKind.Object)
            throw Invalid($"params._meta \"{MetaClientCapabilities}\" is required and must be a JSON object (an empty object when the client offers nothing).");
        if (fields.TryGetProperty(MetaClientInfo, out var client))
        {
            if (client.ValueKind != JsonValueKind.Object || !client.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                || !client.TryGetProperty("version", out var clientVersion) || clientVersion.ValueKind != JsonValueKind.String)
                throw Invalid($"params._meta \"{MetaClientInfo}\" must be an object with a string name and a string version.");
        }
        if (fields.TryGetProperty(MetaLogLevel, out var level) && (level.ValueKind != JsonValueKind.String || !LogLevels.Contains(level.GetString(), StringComparer.Ordinal)))
            throw Invalid($"params._meta \"{MetaLogLevel}\" must be one of {string.Join(", ", LogLevels)}.");
        return (requested, true);
    }

    private static void RequireMethod(string method, bool modern, McpConnection connection)
    {
        if (modern && HandshakeOnly.Contains(method, StringComparer.Ordinal))
            throw new McpProtocolException(JsonRpc.MethodNotFound, method == "initialize"
                ? $"Method not found: initialize is not part of revision {StatelessVersions[0]}; use server/discover. Supported versions: {string.Join(", ", SupportedVersions)}."
                : $"Method not found: {method} is not part of revision {StatelessVersions[0]}.");
        if (!modern && method is "subscriptions/listen" or "server/discover")
            throw new McpProtocolException(JsonRpc.MethodNotFound, $"Method not found: {method} belongs to revision {StatelessVersions[0]}; declare that version in params._meta.");
        if (method is "resources/subscribe" or "resources/unsubscribe")
            throw new McpProtocolException(JsonRpc.MethodNotFound, $"Method not found: {method}. This server does not advertise resource subscriptions.");
        if (method == "completion/complete")
            throw new McpProtocolException(JsonRpc.MethodNotFound, "Method not found: completion/complete. This server does not advertise the completions capability.");
        if (!Methods.Contains(method, StringComparer.Ordinal)) throw new McpProtocolException(JsonRpc.MethodNotFound, "Method not found: " + method);
    }

    /// <summary>The progress token of a request, or null. A token that is neither a string nor an integer is an invalid request parameter.</summary>
    public static JsonElement? ProgressToken(JsonElement? parameters)
    {
        if (parameters is not { } p || !p.TryGetProperty("_meta", out var meta) || meta.ValueKind != JsonValueKind.Object || !meta.TryGetProperty("progressToken", out var token)) return null;
        if (token.ValueKind == JsonValueKind.String || JsonRpc.IsInteger(token)) return token;
        throw new McpProtocolException(JsonRpc.InvalidParams, "params._meta.progressToken must be a string or an integer.");
    }

    private void ValidateToolCall(JsonElement? parameters)
    {
        var supplied = parameters ?? throw new McpProtocolException(JsonRpc.InvalidParams, "tools/call requires params with a tool name and arguments.");
        if (!supplied.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))
            throw new McpProtocolException(JsonRpc.InvalidParams, "tools/call requires a string name.");
        if (supplied.TryGetProperty("arguments", out var arguments) && arguments.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object))
            throw new McpProtocolException(JsonRpc.InvalidParams, "tools/call arguments must be a JSON object.");
        if (Tools.Find(name.GetString()!) is null)
            throw new McpProtocolException(JsonRpc.InvalidParams, $"Unknown tool: {name.GetString()}. Available tools: {string.Join(", ", Tools.Names)}.");
    }

    private async Task<McpReply?> RunAsync(McpRequestContext context, McpInflight inflight)
    {
        McpReply? reply = null;
        try
        {
            try { reply = new McpReply(JsonRpc.Response(context.Id, await DispatchAsync(context)), 200); }
            catch (McpProtocolException ex) { reply = new McpReply(JsonRpc.Error(context.Id, ex.Code, ex.Message, ex.ErrorData), ex.HttpStatus ?? StatusFor(ex.Code, context.Modern)); }
            catch (OperationCanceledException) when (inflight.Token.IsCancellationRequested) { /* No response is due. */ }
            catch (Exception ex)
            {
                log.Error($"{context.Method} failed: {ex}");
                reply = new McpReply(JsonRpc.Error(context.Id, JsonRpc.InternalError, "Internal error: " + ex.Message), 200);
            }
            // A cancelled request gets no response, whatever its handler produced meanwhile; the effect stays visible through other calls.
            if (inflight.Token.IsCancellationRequested && !context.RespondAfterCancellation)
            {
                if (inflight.CancelledByClient) log.Info($"Request {context.Id.GetRawText()} ({context.Method}) was cancelled by the client; no response is sent.");
                reply = null;
            }
            await context.FinishProgressAsync();
            return reply;
        }
        finally { context.Connection.End(inflight); }
    }

    /// <summary>HTTP status of a JSON-RPC error: revision 2026-07-28 maps a few codes to 4xx; the handshake revisions answer 200.</summary>
    private static int StatusFor(int code, bool modern) => !modern ? 200 : code switch
    {
        JsonRpc.MethodNotFound => 404,
        JsonRpc.ParseError or JsonRpc.InvalidRequest or JsonRpc.HeaderMismatch or JsonRpc.MissingRequiredClientCapability or JsonRpc.UnsupportedProtocolVersion => 400,
        _ => 200
    };

    private void HandleNotification(JsonRpcMessage message, McpConnection connection)
    {
        switch (message.Method)
        {
            case "notifications/initialized":
                connection.Initialized = true;
                log.Info($"Client initialized ({connection.Transport} connection {connection.LogId}, protocol {connection.NegotiatedVersion ?? LatestHandshakeVersion}).");
                break;
            case "notifications/cancelled":
                if (CancelledRequest(message) is not { } requestId) { log.Info("Ignoring a cancellation without a valid requestId."); break; }
                var found = connection.Cancel(JsonRpc.IdKey(requestId));
                log.Info($"Cancellation for request {requestId.GetRawText()}: {(found ? "stopping it" : "not in flight; ignored")}.");
                break;
            case "notifications/progress":
            case "notifications/roots/list_changed":
                break; // This server sends no requests, so these carry nothing for it.
            default:
                log.Info($"Ignoring unknown notification {message.Method}.");
                break;
        }
    }
    /// <summary>The requestId of a notifications/cancelled message, when it is a valid id.</summary>
    public static JsonElement? CancelledRequest(JsonRpcMessage message) =>
        message.Method == "notifications/cancelled" && message.Params is { } p && p.TryGetProperty("requestId", out var value) && JsonRpc.IsValidId(value) ? value : null;

    private async Task<JsonNode> DispatchAsync(McpRequestContext context)
    {
        switch (context.Method)
        {
            case "ping": return Shape(context, new JsonObject());
            case "server/discover": return Discover();
            case "tools/list": return Shape(context, new JsonObject { ["tools"] = Tools.ListNode() }, cacheable: true, ttlMs: 3_600_000, cacheScope: "public");
            case "tools/call": return await CallToolAsync(context);
            case "resources/list": return Shape(context, new JsonObject { ["resources"] = McpResources.List(Service) }, cacheable: true, ttlMs: 5_000, cacheScope: "private");
            case "resources/templates/list": return Shape(context, new JsonObject { ["resourceTemplates"] = McpResources.Templates() }, cacheable: true, ttlMs: 3_600_000, cacheScope: "public");
            case "resources/read": return ReadResource(context);
            case "prompts/list": return Shape(context, new JsonObject { ["prompts"] = McpResources.Prompts() }, cacheable: true, ttlMs: 3_600_000, cacheScope: "public");
            case "prompts/get": return Shape(context, McpResources.GetPrompt(context.StringParam("name"), context.Param("arguments")));
            case "logging/setLevel":
                var level = context.StringParam("level");
                if (level is null || !LogLevels.Contains(level, StringComparer.Ordinal)) throw new McpProtocolException(JsonRpc.InvalidParams, "logging/setLevel requires level: " + string.Join(", ", LogLevels) + ".");
                context.Connection.LogLevel = level; // accepted for compatibility; this server sends no notifications/message
                return Shape(context, new JsonObject());
            case "subscriptions/listen": return await ListenAsync(context);
            default: throw new McpProtocolException(JsonRpc.MethodNotFound, "Method not found: " + context.Method);
        }
    }

    private JsonNode Initialize(McpRequestContext context)
    {
        var requested = context.StringParam("protocolVersion");
        var negotiated = requested is not null && HandshakeVersions.Contains(requested, StringComparer.Ordinal) ? requested : LatestHandshakeVersion;
        context.Connection.NegotiatedVersion = negotiated;
        context.Connection.InitializeReceived = true;
        var client = context.Param("clientInfo");
        var clientName = client is { ValueKind: JsonValueKind.Object } info && info.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : "unknown client";
        log.Info($"initialize from {clientName} on {context.Connection.Transport}: requested protocol {requested ?? "(none)"}, negotiated {negotiated}.");
        return new JsonObject
        {
            ["protocolVersion"] = negotiated,
            ["capabilities"] = Capabilities(modern: false),
            ["serverInfo"] = ServerInfo(),
            ["instructions"] = McpResources.Instructions
        };
    }

    /// <summary>server/discover (2026-07-28) is always answered in the stateless shape; it is how a modern client learns which revisions exist.</summary>
    public static JsonObject Discover() => new()
    {
        ["resultType"] = "complete",
        ["supportedVersions"] = JsonRpc.Strings(SupportedVersions),
        ["capabilities"] = Capabilities(modern: true),
        ["_meta"] = new JsonObject { [MetaServerInfo] = ServerInfo() },
        ["instructions"] = McpResources.Instructions,
        ["ttlMs"] = 3_600_000,
        ["cacheScope"] = "public"
    };

    /// <summary>
    /// subscriptions/listen (2026-07-28). This server has no change notifications to send (listChanged and subscribe are false), so the stream
    /// acknowledges an empty set and then stays open until the client cancels it or the server stops, as the message pattern requires.
    /// </summary>
    /// <summary>The filter of subscriptions/listen, checked before the request is registered so a refusal is a plain answer, not a stream.</summary>
    private static void ValidateListen(JsonElement? parameters)
    {
        if (parameters is not { } p || !p.TryGetProperty("notifications", out var filter) || filter.ValueKind != JsonValueKind.Object)
            throw new McpProtocolException(JsonRpc.InvalidParams, "subscriptions/listen requires params.notifications, an object that names the notifications to receive.", httpStatus: 400);
        foreach (var flag in new[] { "toolsListChanged", "promptsListChanged", "resourcesListChanged" })
            if (filter.TryGetProperty(flag, out var value) && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new McpProtocolException(JsonRpc.InvalidParams, $"params.notifications.{flag} must be true or false.", httpStatus: 400);
        if (filter.TryGetProperty("resourceSubscriptions", out var resources) && (resources.ValueKind != JsonValueKind.Array || resources.EnumerateArray().Any(r => r.ValueKind != JsonValueKind.String)))
            throw new McpProtocolException(JsonRpc.InvalidParams, "params.notifications.resourceSubscriptions must be an array of resource URI strings.", httpStatus: 400);
    }

    private async Task<JsonNode> ListenAsync(McpRequestContext context)
    {
        if (Interlocked.Increment(ref listening) > MaximumListenStreams)
        {
            Interlocked.Decrement(ref listening);
            throw new McpProtocolException(JsonRpc.ServerBusy, $"This server keeps at most {MaximumListenStreams} subscription streams open; cancel one first.", httpStatus: 503);
        }
        try
        {
            JsonObject Subscription() => new() { [MetaSubscriptionId] = JsonRpc.Clone(context.Id) };
            // Nothing is granted: every requested notification type is one this server does not support.
            await context.NotifyAsync(JsonRpc.Notification("notifications/subscriptions/acknowledged", new JsonObject { ["_meta"] = Subscription(), ["notifications"] = new JsonObject() }));
            log.Info($"subscriptions/listen {context.Id.GetRawText()} acknowledged with no notification types; the stream stays open until it is cancelled.");
            try { await Task.Delay(Timeout.Infinite, context.Token); } catch (OperationCanceledException) { }
            if (context.CancelledByClient) throw new OperationCanceledException(context.Token);
            // The server is stopping (or the connection ended): close the stream the way both specification pages ask for.
            context.RespondAfterCancellation = true;
            await context.NotifyAsync(JsonRpc.Notification("notifications/cancelled", new JsonObject { ["requestId"] = JsonRpc.Clone(context.Id), ["reason"] = "The server is shutting down." }));
            return Shape(context, new JsonObject { ["_meta"] = Subscription() });
        }
        finally { Interlocked.Decrement(ref listening); }
    }

    private async Task<JsonNode> CallToolAsync(McpRequestContext context)
    {
        var parameters = context.Params!.Value; // validated by ValidateToolCall before the request was registered
        var name = parameters.GetProperty("name").GetString()!;
        JsonElement? arguments = parameters.TryGetProperty("arguments", out var supplied) && supplied.ValueKind == JsonValueKind.Object ? supplied : null;
        var tool = Tools.Find(name)!;
        var started = Stopwatch.StartNew();
        var result = await Tools.CallAsync(tool, arguments, context, log);
        log.Info($"tools/call {name}: {(result.IsError ? "error" : "ok")} in {started.ElapsedMilliseconds} ms.");
        return Shape(context, result.ToNode());
    }

    private JsonNode ReadResource(McpRequestContext context)
    {
        var uri = context.StringParam("uri") ?? throw new McpProtocolException(JsonRpc.InvalidParams, "resources/read requires a uri.");
        var content = McpResources.Read(Service, uri)
            ?? throw new McpProtocolException(context.Modern ? JsonRpc.InvalidParams : JsonRpc.ResourceNotFound, "Resource not found", new JsonObject { ["uri"] = uri });
        var item = new JsonObject { ["uri"] = uri, ["mimeType"] = content.MimeType, ["text"] = content.Text };
        return Shape(context, new JsonObject { ["contents"] = new JsonArray(item) }, cacheable: true, ttlMs: 5_000, cacheScope: "private");
    }

    /// <summary>Revision 2026-07-28 results carry resultType, the server identity and cache hints; handshake revisions receive the plain result.</summary>
    private static JsonObject Shape(McpRequestContext context, JsonObject result, bool cacheable = false, int ttlMs = 0, string cacheScope = "public")
    {
        if (!context.Modern) return result;
        result["resultType"] = "complete";
        var meta = result["_meta"] as JsonObject ?? new JsonObject();
        result.Remove("_meta");
        meta[MetaServerInfo] = ServerInfo();
        result["_meta"] = meta;
        if (cacheable) { result["ttlMs"] = ttlMs; result["cacheScope"] = cacheScope; }
        return result;
    }

    private static string ProductVersion()
    {
        var informational = typeof(McpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational)) return typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var plus = informational.IndexOf('+');
        return plus > 0 ? informational[..plus] : informational;
    }
}
