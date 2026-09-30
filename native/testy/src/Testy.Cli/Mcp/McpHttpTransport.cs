using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Testy.Cli.Mcp;

/// <summary>
/// Streamable HTTP on loopback only (http://127.0.0.1:port/mcp). Handshake revisions (2025-03-26 … 2025-11-25) get sessions through
/// Mcp-Session-Id; revision 2026-07-28 requests are stateless with validated MCP-Protocol-Version/Mcp-Method/Mcp-Name headers.
/// tools/call and subscriptions/listen are answered as an SSE stream whenever the client accepts text/event-stream, so closing that stream
/// cancels the call; a request is validated before any status is written, so errors still arrive as plain JSON with their HTTP status.
/// </summary>
internal sealed class McpHttpTransport : IDisposable
{
    private const string EndpointPath = "/mcp";
    public const long MaximumBodyBytes = JsonRpc.MaximumMessageBytes;
    public const int DefaultMaximumSessions = 64;
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(10), DefaultKeepAlive = TimeSpan.FromSeconds(2);
    private readonly McpServer server;
    private readonly McpLog log;
    private readonly string? token;
    private readonly int maximumSessions;
    private readonly TimeSpan idleTimeout, keepAlive;
    private readonly HttpListener listener = new();
    private readonly ConcurrentDictionary<string, McpConnection> sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> pending = new();
    /// <summary>In-flight requests of the stateless revision by request id, so a stateless notifications/cancelled can find the one it names.</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<McpPending, byte>> statelessRequests = new(StringComparer.Ordinal);
    private readonly McpConnection stateless = new("http-stateless", "http");
    private readonly CancellationTokenSource stopSource = new();
    private readonly List<string> hosts = [];
    private readonly object stopGate = new();
    private Task? stopTask;
    private volatile bool stopping;

    public McpHttpTransport(McpServer server, McpLog log, string? token, int maximumSessions = DefaultMaximumSessions, TimeSpan? idleTimeout = null, TimeSpan? keepAlive = null)
    {
        this.server = server;
        this.log = log;
        this.token = string.IsNullOrEmpty(token) ? null : token;
        this.maximumSessions = maximumSessions;
        this.idleTimeout = idleTimeout ?? DefaultIdleTimeout;
        this.keepAlive = keepAlive ?? DefaultKeepAlive;
    }
    public int Port { get; private set; }
    public string Url => $"http://127.0.0.1:{Port}{EndpointPath}";
    public IReadOnlyList<string> Hosts => hosts;
    public int SessionCount => sessions.Count;

    /// <summary>Binds 127.0.0.1 and ::1 (falling back to IPv4 only). Port 0 picks a free port.</summary>
    public void Start(int requestedPort)
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = requestedPort == 0 ? FreePort() : requestedPort;
            foreach (var prefixes in new[] { new[] { $"http://127.0.0.1:{port}{EndpointPath}/", $"http://[::1]:{port}{EndpointPath}/" }, new[] { $"http://127.0.0.1:{port}{EndpointPath}/" } })
            {
                listener.Prefixes.Clear();
                foreach (var prefix in prefixes) listener.Prefixes.Add(prefix);
                try
                {
                    listener.Start();
                    Port = port;
                    hosts.Clear();
                    hosts.AddRange(prefixes.Select(p => new Uri(p).Host));
                    return;
                }
                catch (HttpListenerException ex) { log.Warn($"Could not listen on {string.Join(", ", prefixes)}: {ex.Message}"); }
            }
            if (requestedPort != 0 || attempt >= 4) throw new InvalidOperationException($"Could not bind the MCP HTTP endpoint on loopback port {port}. Another program may be using it; choose another --port, or 0 for a free one.");
        }
    }
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    public async Task RunAsync(CancellationToken shutdown)
    {
        using var registration = shutdown.Register(() => _ = StopAsync());
        while (true)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                if (stopping || shutdown.IsCancellationRequested) break;
                log.Warn("HTTP accept failed: " + ex.Message);
                continue;
            }
            var work = Task.Run(() => HandleAsync(context), CancellationToken.None);
            pending[work] = 0;
            _ = work.ContinueWith(t => pending.TryRemove(t, out _), TaskScheduler.Default);
        }
        await StopAsync();
    }

    /// <summary>
    /// Orderly stop: every in-flight request is cancelled first (an open subscription stream writes its closure messages), the handlers get
    /// up to five seconds to finish writing, and only then the listener closes its connections.
    /// </summary>
    public Task StopAsync()
    {
        lock (stopGate) return stopTask ??= StopCoreAsync();
    }
    private async Task StopCoreAsync()
    {
        stopping = true;
        try { stopSource.Cancel(); } catch (ObjectDisposedException) { }
        foreach (var session in sessions.Values) session.Close();
        stateless.Close();
        var outstanding = pending.Keys.ToArray();
        if (outstanding.Length > 0)
        {
            try { await Task.WhenAll(outstanding).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { log.Warn("HTTP requests were still running at shutdown."); }
            catch (Exception ex) { log.Warn("An HTTP request failed during shutdown: " + ex.Message); }
        }
        try { if (listener.IsListening) listener.Stop(); } catch (ObjectDisposedException) { }
    }
    public void Dispose()
    {
        stopping = true;
        try { stopSource.Cancel(); } catch (ObjectDisposedException) { }
        try { if (listener.IsListening) listener.Stop(); listener.Close(); } catch (ObjectDisposedException) { }
        stopSource.Dispose();
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            SweepSessions();
            var path = request.Url?.AbsolutePath ?? "";
            if (path is not (EndpointPath or EndpointPath + "/")) { await WriteJsonAsync(response, 404, JsonRpc.Error(null, JsonRpc.InvalidRequest, $"Unknown endpoint. Use {EndpointPath}.")); return; }
            if (!OriginAllowed(request.Headers["Origin"]) || !IsLoopbackHost(HostOf(request)))
            {
                log.Warn($"Rejected a request with Origin '{request.Headers["Origin"]}' and Host '{request.UserHostName}'.");
                await WriteJsonAsync(response, 403, JsonRpc.Error(null, JsonRpc.InvalidRequest, "Forbidden: this MCP endpoint accepts only local (loopback) origins."));
                return;
            }
            if (token is not null && !Authorized(request.Headers["Authorization"]))
            {
                response.Headers["WWW-Authenticate"] = "Bearer realm=\"testy-mcp\"";
                await WriteJsonAsync(response, 401, JsonRpc.Error(null, JsonRpc.InvalidRequest, "Unauthorized: send Authorization: Bearer <token> (the token this server was started with)."));
                return;
            }
            if (stopping) { await WriteJsonAsync(response, 503, JsonRpc.Error(null, JsonRpc.ServerBusy, "The server is shutting down.")); return; }
            switch (request.HttpMethod)
            {
                case "POST": await PostAsync(request, response); break;
                case "DELETE": await DeleteAsync(request, response); break;
                default:
                    response.Headers["Allow"] = "POST, DELETE";
                    await WriteJsonAsync(response, 405, null);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Error("HTTP request failed: " + ex);
            try { await WriteJsonAsync(response, 500, JsonRpc.Error(null, JsonRpc.InternalError, "Internal error: " + ex.Message)); } catch (Exception) { /* Already faulted. */ }
        }
        catch (OperationCanceledException) { /* The server is stopping. */ }
        finally
        {
            try { response.Close(); } catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { }
        }
    }

    private async Task PostAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        // A browser page can send text/plain or a form without a preflight; JSON it cannot. Refuse before reading anything.
        if (!IsJsonContent(request.ContentType))
        {
            await WriteJsonAsync(response, 415, JsonRpc.Error(null, JsonRpc.InvalidRequest, "Unsupported media type: POST a JSON-RPC message with Content-Type: application/json (UTF-8)."));
            return;
        }
        if (request.ContentLength64 > MaximumBodyBytes) { await WriteTooLargeAsync(response); return; }
        var body = await ReadBodyAsync(request.InputStream, stopSource.Token);
        if (body is null) { await WriteTooLargeAsync(response); return; }
        var content = JsonRpc.WithoutByteOrderMark(body);
        if (content.Length == 0) { await WriteJsonAsync(response, 400, JsonRpc.Error(null, JsonRpc.ParseError, "Parse error: the request body is empty.")); return; }
        if (!JsonRpc.TryParse(content, out var root, out var problem)) { await WriteJsonAsync(response, 400, JsonRpc.Error(null, JsonRpc.ParseError, problem)); return; }
        if (root.ValueKind == JsonValueKind.Array) { await BatchAsync(request, response, root); return; }
        var message = JsonRpc.Classify(root);
        if (message.Kind == JsonRpcKind.Invalid) { await WriteJsonAsync(response, 400, JsonRpc.Error(message.Id, message.ProblemCode, message.Problem)); return; }
        var headerVersion = request.Headers["MCP-Protocol-Version"];
        var sessionless = string.IsNullOrEmpty(request.Headers["Mcp-Session-Id"]);
        var isStateless = McpServer.DeclaresStatelessVersion(message.Params) || McpServer.IsStateless(headerVersion)
            || message.Method == "server/discover" || (message.Method == "subscriptions/listen" && sessionless);
        if (isStateless) await StatelessAsync(request, response, root, message, headerVersion);
        else await SessionAsync(request, response, root, message);
    }

    /// <summary>Reads the body and stops as soon as more than the limit has arrived (also for a chunked body, whose length is not declared). Null means too large.</summary>
    private static async Task<byte[]?> ReadBodyAsync(Stream input, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var count = await input.ReadAsync(chunk, token);
            if (count == 0) return buffer.ToArray();
            if (buffer.Length + count > MaximumBodyBytes) return null;
            buffer.Write(chunk, 0, count);
        }
    }
    private static Task WriteTooLargeAsync(HttpListenerResponse response)
    {
        response.KeepAlive = false; // the rest of the body is not read; the connection ends with this answer
        return WriteJsonAsync(response, 413, JsonRpc.Error(null, JsonRpc.InvalidRequest, "The request body exceeds 32 MiB."));
    }

    /// <summary>Revision 2026-07-28: no sessions; headers mirror the body and are validated; per-request connection so a disconnect cancels only that request.</summary>
    private async Task StatelessAsync(HttpListenerRequest request, HttpListenerResponse response, JsonElement root, JsonRpcMessage message, string? headerVersion)
    {
        if (message.Kind is JsonRpcKind.Notification or JsonRpcKind.Response)
        {
            if (message.Kind == JsonRpcKind.Notification && message.Method == "notifications/cancelled") CancelStateless(message);
            else server.Begin(root, stateless, NoNotify, stopSource.Token);
            await WriteJsonAsync(response, 202, null);
            return;
        }
        // The headers mirror the body. A body that names no version is answered by the envelope validation instead (invalid params).
        if (McpServer.MetaVersion(message.Params) is { } metaVersion && HeaderMismatch(request, message, metaVersion) is { } mismatch)
        {
            await WriteJsonAsync(response, 400, JsonRpc.Error(message.Id, JsonRpc.HeaderMismatch, "Header mismatch: " + mismatch));
            return;
        }
        var connection = new McpConnection(Guid.NewGuid().ToString("N"), "http");
        await RespondAsync(request, response, root, connection, sessionHeader: null, headerVersion, isStateless: true);
    }

    private void CancelStateless(JsonRpcMessage message)
    {
        if (McpServer.CancelledRequest(message) is not { } requestId) { log.Info("Ignoring a stateless cancellation without a valid requestId."); return; }
        var candidates = statelessRequests.TryGetValue(JsonRpc.IdKey(requestId), out var found) ? found.Keys.ToArray() : [];
        if (candidates.Length == 1) { candidates[0].CancelByClient(); log.Info($"Stateless cancellation for request {requestId.GetRawText()}: stopping it."); }
        else log.Info($"Stateless cancellation for request {requestId.GetRawText()}: {(candidates.Length == 0 ? "not in flight" : $"{candidates.Length} requests in flight share that id")}; ignored.");
    }

    /// <summary>Handshake revisions: initialize mints a session; every later message needs its Mcp-Session-Id.</summary>
    private async Task SessionAsync(HttpListenerRequest request, HttpListenerResponse response, JsonElement root, JsonRpcMessage message)
    {
        var sessionId = request.Headers["Mcp-Session-Id"];
        if (message.Kind == JsonRpcKind.Request && message.Method == "initialize")
        {
            // Initializing again on a session replaces it: the old id stops working and a fresh one is minted.
            if (!string.IsNullOrEmpty(sessionId) && sessions.TryRemove(sessionId, out var replaced))
            {
                replaced.Close();
                log.Info($"HTTP session {replaced.LogId} was replaced by a new initialize.");
            }
            var created = CreateSession();
            if (created is null)
            {
                await WriteJsonAsync(response, 503, JsonRpc.Error(message.Id, JsonRpc.ServerBusy, $"This server allows {maximumSessions} concurrent MCP sessions; DELETE an unused session or wait for one to expire ({idleTimeout.TotalMinutes:0.#} idle minutes)."));
                return;
            }
            log.Info($"HTTP session {created.LogId} created.");
            var reply = await RespondAsync(request, response, root, created, sessionHeader: created.Id, headerVersion: null, isStateless: false);
            if (reply is null || JsonRpc.ErrorCode(reply.Message) is not null) { sessions.TryRemove(created.Id, out _); created.Close(); }
            return;
        }
        if (string.IsNullOrEmpty(sessionId)) { await WriteJsonAsync(response, 400, JsonRpc.Error(message.Id, JsonRpc.InvalidRequest, "Mcp-Session-Id header is required: send initialize first and reuse the session id it returns.")); return; }
        if (!sessions.TryGetValue(sessionId, out var session)) { await WriteJsonAsync(response, 404, JsonRpc.Error(message.Id, JsonRpc.InvalidRequest, "Unknown or expired session; start a new one with initialize.")); return; }
        var headerVersion = request.Headers["MCP-Protocol-Version"];
        if (headerVersion is not null && !McpServer.SupportedVersions.Contains(headerVersion, StringComparer.Ordinal))
        {
            await WriteJsonAsync(response, 400, JsonRpc.Error(message.Id, JsonRpc.InvalidRequest, "Unsupported MCP-Protocol-Version header: " + headerVersion, new JsonObject { ["supported"] = JsonRpc.Strings(McpServer.SupportedVersions) }), sessionId);
            return;
        }
        session.Touch();
        if (message.Kind is JsonRpcKind.Notification or JsonRpcKind.Response)
        {
            server.Begin(root, session, NoNotify, stopSource.Token);
            await WriteJsonAsync(response, 202, null, sessionId);
            return;
        }
        await RespondAsync(request, response, root, session, sessionHeader: sessionId, headerVersion: null, isStateless: false);
    }

    /// <summary>JSON-RPC batches (revision 2025-03-26) are answered with one JSON array; batches are not part of later revisions.</summary>
    private async Task BatchAsync(HttpListenerRequest request, HttpListenerResponse response, JsonElement root)
    {
        var items = root.EnumerateArray().ToArray();
        if (items.Length == 0) { await WriteJsonAsync(response, 400, JsonRpc.Error(null, JsonRpc.InvalidRequest, "An empty batch is not a valid request.")); return; }
        var sessionId = request.Headers["Mcp-Session-Id"];
        if (string.IsNullOrEmpty(sessionId)) { await WriteJsonAsync(response, 400, JsonRpc.Error(null, JsonRpc.InvalidRequest, "Mcp-Session-Id header is required: send initialize first (on its own, not in a batch).")); return; }
        if (!sessions.TryGetValue(sessionId, out var session)) { await WriteJsonAsync(response, 404, JsonRpc.Error(null, JsonRpc.InvalidRequest, "Unknown or expired session; start a new one with initialize.")); return; }
        session.Touch();
        var entries = items.Select(item => server.Begin(item, session, NoNotify, stopSource.Token, inBatch: true)).ToArray();
        var replies = await Task.WhenAll(entries.Select(entry => entry.RunAsync()));
        var answers = new JsonArray(replies.Where(reply => reply is not null).Select(reply => (JsonNode?)reply!.Message).ToArray());
        if (answers.Count == 0) await WriteJsonAsync(response, 202, null, sessionId);
        else await WriteJsonAsync(response, 200, answers, sessionId);
    }

    private async Task DeleteAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        var sessionId = request.Headers["Mcp-Session-Id"];
        if (string.IsNullOrEmpty(sessionId)) { await WriteJsonAsync(response, 400, JsonRpc.Error(null, JsonRpc.InvalidRequest, "DELETE needs the Mcp-Session-Id of the session to end.")); return; }
        if (!sessions.TryRemove(sessionId, out var session)) { await WriteJsonAsync(response, 404, JsonRpc.Error(null, JsonRpc.InvalidRequest, "Unknown or expired session.")); return; }
        session.Close();
        log.Info($"HTTP session {session.LogId} ended by the client.");
        await WriteJsonAsync(response, 200, null);
    }

    /// <summary>Answers one request. The request is validated and registered first; only a request that really runs may become an SSE stream.</summary>
    private async Task<McpReply?> RespondAsync(HttpListenerRequest request, HttpListenerResponse response, JsonElement root, McpConnection connection, string? sessionHeader, string? headerVersion, bool isStateless)
    {
        Stream? stream = null;
        var gate = new SemaphoreSlim(1, 1);
        var disconnected = false;
        McpPending? running = null;
        async Task WriteEventAsync(string text)
        {
            if (stream is null) return; // a plain JSON answer carries no notifications
            await gate.WaitAsync();
            try
            {
                if (disconnected) throw new IOException("The client closed its response stream.");
                var bytes = Encoding.UTF8.GetBytes(text);
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException)
            {
                // Closing the response stream is how an HTTP client cancels: the request stops and nothing more is written for it.
                if (!disconnected) { disconnected = true; log.Info("The client closed its response stream; cancelling the request."); running?.CancelByClient(); }
                throw new IOException("The client closed its response stream.", ex);
            }
            finally { gate.Release(); }
        }
        static string Event(JsonObject message) => "event: message\ndata: " + JsonRpc.Serialize(message) + "\n\n";

        var pendingRequest = server.Begin(root, connection, notification => WriteEventAsync(Event(notification)), stopSource.Token, headerVersion);
        running = pendingRequest;
        if (pendingRequest.Settled)
        {
            if (pendingRequest.Reply is { } settled) await WriteJsonAsync(response, settled.HttpStatus, settled.Message, sessionHeader);
            else await WriteJsonAsync(response, 202, null, sessionHeader);
            return pendingRequest.Reply;
        }
        var streaming = pendingRequest.Streamable && AcceptsEventStream(request.Headers["Accept"]);
        if (pendingRequest.Method == "subscriptions/listen" && !streaming)
        {
            pendingRequest.CancelByClient();
            await pendingRequest.RunAsync();
            var refused = new McpReply(JsonRpc.Error(JsonRpc.Classify(root).Id, JsonRpc.InvalidRequest, "subscriptions/listen is answered as a stream: send Accept: text/event-stream."), 406);
            await WriteJsonAsync(response, refused.HttpStatus, refused.Message, sessionHeader);
            return refused;
        }
        if (isStateless) Register(pendingRequest);
        try
        {
            if (!streaming)
            {
                var reply = await pendingRequest.RunAsync();
                // A cancelled request has no JSON-RPC response; at the transport level the exchange ends with 204 and no body.
                if (reply is null) await WriteJsonAsync(response, 204, null, sessionHeader);
                else await WriteJsonAsync(response, reply.HttpStatus, reply.Message, sessionHeader);
                return reply;
            }
            response.StatusCode = 200;
            response.ContentType = "text/event-stream";
            response.Headers["Cache-Control"] = "no-cache";
            response.Headers["X-Accel-Buffering"] = "no";
            if (sessionHeader is not null) response.Headers["Mcp-Session-Id"] = sessionHeader;
            response.SendChunked = true;
            stream = response.OutputStream;
            var handling = pendingRequest.RunAsync();
            while (!handling.IsCompleted)
            {
                await Task.WhenAny(handling, Task.Delay(keepAlive));
                if (handling.IsCompleted) break;
                // The comment keeps intermediaries from closing an idle stream, and a failed write is how a closed stream is noticed.
                try { await WriteEventAsync(": keep-alive\n\n"); } catch (IOException) { break; }
            }
            var final = await handling;
            if (final is not null && !disconnected)
            {
                try { await WriteEventAsync(Event(final.Message)); } catch (IOException) { /* Client gone. */ }
            }
            return final;
        }
        finally { if (isStateless) Unregister(pendingRequest); }
    }

    private void Register(McpPending request) => statelessRequests.GetOrAdd(request.IdKey!, _ => new ConcurrentDictionary<McpPending, byte>()).TryAdd(request, 0);
    private void Unregister(McpPending request)
    {
        if (!statelessRequests.TryGetValue(request.IdKey!, out var set)) return;
        set.TryRemove(request, out _);
        if (set.IsEmpty) statelessRequests.TryRemove(new KeyValuePair<string, ConcurrentDictionary<McpPending, byte>>(request.IdKey!, set));
    }

    private static Task NoNotify(JsonObject notification) => Task.CompletedTask;

    private static string? HeaderMismatch(HttpListenerRequest request, JsonRpcMessage message, string metaVersion)
    {
        var headerVersion = request.Headers["MCP-Protocol-Version"];
        if (headerVersion is null) return "the MCP-Protocol-Version header is required and must equal _meta " + McpServer.MetaProtocolVersion + ".";
        if (headerVersion != metaVersion) return $"MCP-Protocol-Version header '{headerVersion}' does not match the body's protocol version '{metaVersion}'.";
        var method = request.Headers["Mcp-Method"];
        if (method is null) return "the Mcp-Method header is required.";
        if (method != message.Method) return $"Mcp-Method header '{method}' does not match the body method '{message.Method}'.";
        if (message.Method is "tools/call" or "prompts/get" or "resources/read")
        {
            var field = message.Method == "resources/read" ? "uri" : "name";
            var header = request.Headers["Mcp-Name"];
            if (header is null) return $"the Mcp-Name header is required for {message.Method}.";
            // A body without that field fails the method's own parameter check (invalid params); there is nothing for the header to mirror.
            if (message.Params is not { } p || !p.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String) return null;
            if (DecodeSentinel(header) != value.GetString()) return $"Mcp-Name header '{header}' does not match the body {field} '{value.GetString()}'.";
        }
        return null;
    }
    /// <summary>Header values that are not plain ASCII travel as =?base64?…?= (RFC 9110 field-value safe).</summary>
    internal static string DecodeSentinel(string value)
    {
        if (!value.StartsWith("=?base64?", StringComparison.Ordinal) || !value.EndsWith("?=", StringComparison.Ordinal)) return value;
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value[9..^2])); }
        catch (FormatException) { return value; }
    }

    private McpConnection? CreateSession()
    {
        SweepSessions();
        if (sessions.Count >= maximumSessions) return null;
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var connection = new McpConnection(id, "http");
        return sessions.TryAdd(id, connection) ? connection : null;
    }
    private void SweepSessions()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in sessions.ToArray())
            if (pair.Value.InFlight == 0 && now - pair.Value.LastActivity > idleTimeout && sessions.TryRemove(pair.Key, out var expired))
            {
                expired.Close();
                log.Info($"HTTP session {expired.LogId} expired after {idleTimeout.TotalMinutes:0.#} idle minutes.");
            }
    }

    /// <summary>
    /// No Origin header (a program, not a browser page) or an http/https origin whose host is exactly localhost or a loopback address.
    /// "null", extension schemes and look-alike hosts such as localhost.example.com are refused.
    /// </summary>
    internal static bool OriginAllowed(string? origin)
    {
        if (string.IsNullOrEmpty(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        return IsLoopbackHost(uri.Host);
    }
    internal static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        var name = host.Trim('[', ']');
        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(name, out var address) && IPAddress.IsLoopback(address);
    }
    private static string HostOf(HttpListenerRequest request)
    {
        var host = request.UserHostName ?? request.Url?.Host ?? "";
        if (host.StartsWith('[')) { var end = host.IndexOf(']'); return end > 0 ? host[..(end + 1)] : host; }
        var colon = host.LastIndexOf(':');
        return colon > 0 ? host[..colon] : host;
    }
    private bool Authorized(string? header)
    {
        if (token is null) return true;
        if (header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var supplied = Encoding.UTF8.GetBytes(header[7..].Trim());
        var expected = Encoding.UTF8.GetBytes(token);
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    /// <summary>True when the Accept header lists text/event-stream itself with a quality above zero. A wildcard alone keeps the JSON answer.</summary>
    internal static bool AcceptsEventStream(string? accept)
    {
        if (string.IsNullOrWhiteSpace(accept)) return false;
        foreach (var item in accept.Split(','))
        {
            var parts = item.Split(';');
            if (!parts[0].Trim().Equals("text/event-stream", StringComparison.OrdinalIgnoreCase)) continue;
            var quality = 1.0;
            foreach (var parameter in parts.Skip(1))
            {
                var pair = parameter.Split('=', 2);
                if (pair.Length == 2 && pair[0].Trim().Equals("q", StringComparison.OrdinalIgnoreCase)
                    && !double.TryParse(pair[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out quality)) quality = 0;
            }
            if (quality > 0) return true;
        }
        return false;
    }
    /// <summary>application/json, in any letter case, with optional parameters; a charset other than UTF-8 is refused.</summary>
    internal static bool IsJsonContent(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return false;
        var parts = contentType.Split(';');
        if (!parts[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var parameter in parts.Skip(1))
        {
            var pair = parameter.Split('=', 2);
            if (pair.Length != 2 || !pair[0].Trim().Equals("charset", StringComparison.OrdinalIgnoreCase)) continue;
            var charset = pair[1].Trim().Trim('"');
            if (!charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase) && !charset.Equals("utf8", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int status, JsonNode? body, string? sessionHeader = null)
    {
        response.StatusCode = status;
        if (sessionHeader is not null) response.Headers["Mcp-Session-Id"] = sessionHeader;
        if (body is null) { response.ContentLength64 = 0; return; }
        var bytes = Encoding.UTF8.GetBytes(JsonRpc.Serialize(body));
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }
}
