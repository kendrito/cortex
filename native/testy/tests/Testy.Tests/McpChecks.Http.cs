using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Testy.Cli.Mcp;
using Testy.Core;

namespace Testy.Tests;

/// <summary>The Streamable HTTP transport on a free loopback port, log hygiene and the discovery documents.</summary>
internal static partial class McpChecks
{
    private sealed record HttpReply(int Status, string Body, string? MediaType, string? SessionId, string? Authenticate)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement.Clone();
        public int Code => Json.GetProperty("error").GetProperty("code").GetInt32();
        /// <summary>The JSON-RPC messages of an SSE body, in order; comments (keep-alives) are left out.</summary>
        public List<JsonElement> Events => Body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(block => string.Concat(block.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].TrimStart())))
            .Where(data => data.Length > 0).Select(data => JsonDocument.Parse(data).RootElement.Clone()).ToList();
        /// <summary>The response message: the body of a JSON answer, or the last event with an id of a stream.</summary>
        public JsonElement Message => MediaType == "text/event-stream" ? Events.Last(e => e.TryGetProperty("id", out _) && !e.TryGetProperty("method", out _)) : Json;
    }
    private sealed class HttpHarness : IAsyncDisposable
    {
        private readonly Harness harness;
        private readonly McpHttpTransport transport;
        private readonly CancellationTokenSource stop = new();
        private readonly Task serving;
        private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(2) };
        public HttpHarness(string name, string? token, int maximumSessions = McpHttpTransport.DefaultMaximumSessions, TimeSpan? idleTimeout = null, McpLog? log = null)
        {
            harness = new Harness(name, log);
            Token = token;
            transport = new McpHttpTransport(harness.Server, harness.Log, token, maximumSessions, idleTimeout);
            transport.Start(0);
            serving = transport.RunAsync(stop.Token);
        }
        public string? Token { get; }
        public string Url => transport.Url;
        public string Workspace => harness.Workspace;
        public McpServer Server => harness.Server;
        public TestyMcpService Service => harness.Service;
        public int SessionCount => transport.SessionCount;
        public HttpRequestMessage Request(HttpMethod method, object? body, IDictionary<string, string>? headers = null, bool authorize = true, string? session = null, string? accept = "application/json, text/event-stream", string? host = null, string? contentType = "application/json; charset=utf-8")
        {
            var request = new HttpRequestMessage(method, Url);
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body as byte[] ?? Encoding.UTF8.GetBytes(body as string ?? (body as JsonNode)?.ToJsonString() ?? JsonSerializer.Serialize(body, TestyJson.Options)));
                if (contentType is not null) request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
            if (accept is not null) request.Headers.TryAddWithoutValidation("Accept", accept);
            if (authorize && Token is not null) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
            if (session is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
            if (host is not null) request.Headers.Host = host;
            if (headers is not null) foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
            return request;
        }
        public async Task<HttpReply> SendAsync(HttpRequestMessage request)
        {
            using (request)
            using (var response = await http.SendAsync(request))
                return new HttpReply((int)response.StatusCode, await response.Content.ReadAsStringAsync(), response.Content.Headers.ContentType?.MediaType,
                    response.Headers.TryGetValues("Mcp-Session-Id", out var ids) ? ids.FirstOrDefault() : null, response.Headers.WwwAuthenticate.FirstOrDefault()?.ToString());
        }
        public Task<HttpReply> SendAsync(HttpMethod method, object? body, IDictionary<string, string>? headers = null, bool authorize = true, string? session = null, string? accept = "application/json, text/event-stream", string? host = null, string? contentType = "application/json; charset=utf-8") =>
            SendAsync(Request(method, body, headers, authorize, session, accept, host, contentType));
        public Task<HttpReply> PostAsync(object body, IDictionary<string, string>? headers = null, bool authorize = true, string? session = null, string? accept = "application/json, text/event-stream", string? host = null, string? contentType = "application/json; charset=utf-8") =>
            SendAsync(HttpMethod.Post, body, headers, authorize, session, accept, host, contentType);
        /// <summary>Opens a stream and returns once its headers have arrived; the caller reads or closes it.</summary>
        public Task<HttpResponseMessage> OpenAsync(HttpRequestMessage request) => http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        public async Task<string> InitializeAsync(int id = 1)
        {
            var reply = await PostAsync(Initialize(id));
            Check(reply.Status == 200 && reply.SessionId is { Length: 32 }, $"initialize must return 200 with a session id; got {reply.Status} {reply.Body}");
            return reply.SessionId!;
        }
        public async Task<JsonElement> WorkspaceInfoAsync(string session) =>
            new McpToolCall((await PostAsync(ToolCall("get_workspace_info", new JsonObject(), "info-" + Guid.NewGuid().ToString("N")[..6]), session: session)).Message).RequireOk("get_workspace_info").Structured;
        public async Task<int> LaunchedPidAsync(string session)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (true)
            {
                var apps = (await WorkspaceInfoAsync(session)).GetProperty("launchedApps");
                if (apps.GetArrayLength() > 0) return apps[0].GetProperty("pid").GetInt32();
                if (DateTimeOffset.UtcNow > deadline) throw new InvalidOperationException("The launched app was never listed by get_workspace_info.");
                await Task.Delay(50);
            }
        }
        /// <summary>Stops the transport the way the server does at shutdown; streams that are open receive their closure.</summary>
        public async Task StopAsync() { stop.Cancel(); try { await serving.WaitAsync(TimeSpan.FromSeconds(10)); } catch (TimeoutException) { } }
        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            http.Dispose();
            transport.Dispose();
            await harness.DisposeAsync();
        }
    }
    private static JsonObject Request(string method, object? parameters, JsonNode id)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null) message["params"] = parameters as JsonNode ?? JsonRpc.ToNode(parameters);
        return message;
    }
    private static JsonObject Initialize(int id) => Request("initialize", new JsonObject { ["protocolVersion"] = "2025-11-25", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "unit", ["version"] = "1" } }, JsonValue.Create(id));
    private static JsonObject ToolCall(string name, object arguments, string id, string? progressToken = null)
    {
        var parameters = new JsonObject { ["name"] = name, ["arguments"] = arguments as JsonNode ?? JsonRpc.ToNode(arguments) };
        if (progressToken is not null) parameters["_meta"] = new JsonObject { ["progressToken"] = progressToken };
        return Request("tools/call", parameters, JsonValue.Create(id));
    }
    private static JsonObject Modern(string method, JsonObject? parameters, string id, string version = "2026-07-28")
    {
        var p = parameters ?? new JsonObject();
        var meta = Meta(version);
        if (p["_meta"] is JsonObject given) foreach (var (key, value) in given.ToList()) { given.Remove(key); meta[key] = value; }
        p["_meta"] = meta;
        return Request(method, p, JsonValue.Create(id));
    }
    private static Dictionary<string, string> ModernHeaders(string method, string? name = null, string version = "2026-07-28")
    {
        var headers = new Dictionary<string, string> { ["MCP-Protocol-Version"] = version, ["Mcp-Method"] = method };
        if (name is not null) headers["Mcp-Name"] = name;
        return headers;
    }
    private static string Sentinel(string value) => "=?base64?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "?=";

    private static async Task HttpRules()
    {
        await using var http = new HttpHarness("http", "unit-token-0123456789", maximumSessions: 3);
        Check(http.Url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal) && http.Url.EndsWith("/mcp", StringComparison.Ordinal), "The URL must be the loopback /mcp endpoint.");
        var unauthorized = await http.PostAsync(Initialize(1), authorize: false);
        Check(unauthorized.Status == 401 && unauthorized.Authenticate is not null && unauthorized.Authenticate.StartsWith("Bearer", StringComparison.Ordinal), "A missing bearer token must be 401 with WWW-Authenticate.");
        Check(!unauthorized.Body.Contains("unit-token", StringComparison.Ordinal), "The refusal never repeats the token.");
        Check((await http.PostAsync(Initialize(1), new Dictionary<string, string> { ["Authorization"] = "Bearer wrong-token" }, authorize: false)).Status == 401, "A wrong token must be 401.");
        var initialized = await http.PostAsync(Initialize(1));
        Check(initialized.Status == 200 && initialized.MediaType == "application/json" && initialized.SessionId is { Length: 32 }, $"initialize must return 200 JSON with a session id; got {initialized.Status}.");
        Check(Result(initialized.Json).GetProperty("protocolVersion").GetString() == "2025-11-25", "The negotiated version is echoed.");
        var session = initialized.SessionId!;
        Check((await http.PostAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, session: session)).Status == 202, "Notifications must be 202.");
        Check((await http.PostAsync(Request("tools/list", null, JsonValue.Create(2)))).Status == 400, "A request without a session must be 400.");
        Check((await http.PostAsync(Request("tools/list", null, JsonValue.Create(3)), session: "00000000000000000000000000000000")).Status == 404, "An unknown session must be 404.");
        var tools = await http.PostAsync(Request("tools/list", null, JsonValue.Create(4)), session: session);
        Check(tools.Status == 200 && tools.MediaType == "application/json" && tools.SessionId == session && Result(tools.Json).GetProperty("tools").GetArrayLength() == ExpectedTools.Length, "tools/list over the session must succeed as JSON.");
        Check((await http.PostAsync(Request("tools/list", null, JsonValue.Create(5)), new Dictionary<string, string> { ["MCP-Protocol-Version"] = "1999-01-01" }, session: session)).Status == 400, "An unsupported MCP-Protocol-Version header must be 400.");
        Check((await http.PostAsync(Request("tools/list", null, JsonValue.Create(6)), new Dictionary<string, string> { ["MCP-Protocol-Version"] = "2025-11-25" }, session: session)).Status == 200, "A supported MCP-Protocol-Version header is accepted.");
        Check((await http.SendAsync(HttpMethod.Get, null, session: session)).Status == 405, "GET must be 405 (no standalone stream).");
        Check((await http.SendAsync(HttpMethod.Put, Initialize(7))).Status == 405, "Other methods must be 405.");
        var preflight = await http.SendAsync(HttpMethod.Options, null, new Dictionary<string, string> { ["Origin"] = "http://localhost:5173", ["Access-Control-Request-Method"] = "POST" }, authorize: false);
        Check(preflight.Status is 401 or 405, $"A browser preflight must fail; got {preflight.Status}.");

        // Origin and Host: only exact loopback names; no Origin at all is a program, not a page.
        foreach (var origin in new[] { "http://evil.example", "null", "http://localhost.evil.com", "http://127.0.0.1.evil.com", "http://evil.localhost.com", "chrome-extension://abcdefghijklmnop", "file://", "http://192.168.1.10:5173", "http://[::2]:80", "not an origin" })
            Check((await http.PostAsync(Request("ping", null, JsonValue.Create(8)), new Dictionary<string, string> { ["Origin"] = origin }, session: session)).Status == 403, $"Origin '{origin}' must be 403.");
        foreach (var origin in new[] { "http://localhost:5173", "https://localhost", "http://127.0.0.1:8080", "http://[::1]:5173", "HTTP://LOCALHOST:1" })
            Check((await http.PostAsync(Request("ping", null, JsonValue.Create(10)), new Dictionary<string, string> { ["Origin"] = origin }, session: session)).Status == 200, $"Origin '{origin}' is loopback and is served.");
        var port = new Uri(http.Url).Port;
        Check((await http.PostAsync(Request("ping", null, JsonValue.Create(12)), session: session, host: "evil.example")).Status == 403, "A rebound Host header must be 403.");
        Check((await http.PostAsync(Request("ping", null, JsonValue.Create(12)), session: session, host: "localhost.evil.com:" + port)).Status == 403, "A look-alike Host header must be 403.");
        Check((await http.PostAsync(Request("ping", null, JsonValue.Create(13)), session: session, host: "localhost:" + port)).Status == 200, "Host localhost:<port> is accepted.");

        // Content-Type: JSON only, so a page cannot send a request that needs no preflight.
        foreach (var contentType in new string?[] { "text/plain", null, "application/json; charset=utf-16", "application/x-www-form-urlencoded", "application/jsonp", "text/json" })
        {
            var refused = await http.PostAsync(Request("ping", null, JsonValue.Create(14)), session: session, contentType: contentType);
            Check(refused.Status == 415 && refused.Json.GetProperty("id").ValueKind == JsonValueKind.Null, $"Content-Type '{contentType ?? "(none)"}' must be 415; got {refused.Status}.");
        }
        foreach (var contentType in new[] { "application/json; charset=utf-8", "Application/JSON", "application/json", "application/json;charset=\"UTF-8\"" })
            Check((await http.PostAsync(Request("ping", null, JsonValue.Create(15)), session: session, contentType: contentType)).Status == 200, $"Content-Type '{contentType}' is served.");
        Check((await http.PostAsync(Initialize(16), contentType: "text/plain")).Status == 415 && http.SessionCount == 1, "initialize as text/plain is refused and mints no session.");

        // Accept: without text/event-stream (or without the header) the answer is JSON.
        var noAccept = await http.PostAsync(ToolCall("list_tests", new JsonObject(), "a1"), session: session, accept: null);
        Check(noAccept.Status == 200 && noAccept.MediaType == "application/json", "A request without an Accept header is answered as JSON.");
        var wildcard = await http.PostAsync(ToolCall("list_tests", new JsonObject(), "a2"), session: session, accept: "*/*");
        Check(wildcard.Status == 200 && wildcard.MediaType == "application/json", "A wildcard Accept keeps the JSON answer.");
        var refusedStream = await http.PostAsync(ToolCall("list_tests", new JsonObject(), "a3"), session: session, accept: "application/json, text/event-stream;q=0");
        Check(refusedStream.MediaType == "application/json", "text/event-stream with q=0 is not accepted.");

        Check((await http.PostAsync("{oops", session: session)).Status == 400, "Malformed JSON must be 400.");
        byte[] bom = [0xEF, 0xBB, 0xBF];
        Check((await http.PostAsync((byte[])[.. bom, .. Encoding.UTF8.GetBytes(Request("ping", null, JsonValue.Create(17)).ToJsonString())], session: session)).Status == 200, "A body with a byte order mark is parsed.");
        Check((await http.PostAsync((byte[])[.. Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":18,\"method\":\"ping\",\"params\":{\"x\":\""), 0xC3, 0x28, .. Encoding.UTF8.GetBytes("\"}}")], session: session)).Status == 400, "A body that is not valid UTF-8 must be 400.");
        var batch = await http.PostAsync("[{\"jsonrpc\":\"2.0\",\"id\":\"x1\",\"method\":\"ping\"},{\"jsonrpc\":\"2.0\",\"id\":\"x2\",\"method\":\"ping\"}]", session: session);
        Check(batch.Status == 200 && batch.Json.ValueKind == JsonValueKind.Array && batch.Json.GetArrayLength() == 2, "A batch of requests is answered with a JSON array.");
        Check((await http.PostAsync("[{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}]", session: session)).Status == 202, "A batch of notifications is 202.");
        var initializeInBatch = await http.PostAsync("[{\"jsonrpc\":\"2.0\",\"id\":\"x3\",\"method\":\"ping\"}," + Initialize(19).ToJsonString() + "]", session: session);
        Check(initializeInBatch.Status == 200 && initializeInBatch.Json.GetArrayLength() == 2 && initializeInBatch.Json[1].GetProperty("error").GetProperty("code").GetInt32() == JsonRpc.InvalidRequest && http.SessionCount == 1,
            "initialize inside a batch is refused with a -32600 entry and mints no session.");

        var second = await http.PostAsync(Initialize(20));
        var third = await http.PostAsync(Initialize(21));
        var full = await http.PostAsync(Initialize(22));
        Check(second.Status == 200 && third.Status == 200 && full.Status == 503 && full.Code == JsonRpc.ServerBusy, "The session limit must be enforced with 503 and the server's busy code.");
        Check((await http.SendAsync(HttpMethod.Delete, null)).Status == 400, "DELETE without a session must be 400.");
        Check((await http.SendAsync(HttpMethod.Delete, null, session: "00000000000000000000000000000000")).Status == 404, "DELETE of an unknown session must be 404.");
        Check((await http.SendAsync(HttpMethod.Delete, null, session: session)).Status == 200, "DELETE must end the session.");
        Check((await http.PostAsync(Request("ping", null, JsonValue.Create(30)), session: session)).Status == 404, "The ended session must be 404.");
        Check((await http.PostAsync(Initialize(23))).Status == 200, "After DELETE a new session can be opened again.");
        using var plain = new HttpClient();
        var wrongPath = await plain.PostAsync(http.Url.Replace("/mcp", "/other"), new StringContent("{}", Encoding.UTF8, "application/json"));
        Check((int)wrongPath.StatusCode == 404, "Other paths must be 404.");
    }

    private static async Task HttpSessions()
    {
        await using (var http = new HttpHarness("http-sessions", null))
        {
            var first = await http.InitializeAsync();
            var again = await http.PostAsync(Initialize(2), session: first);
            Check(again.Status == 200 && again.SessionId is { Length: 32 } && again.SessionId != first, "initialize on an existing session mints a fresh session id.");
            Check((await http.PostAsync(Request("ping", null, JsonValue.Create(3)), session: first)).Status == 404, "The replaced session id stops working.");
            Check((await http.PostAsync(Request("ping", null, JsonValue.Create(4)), session: again.SessionId)).Status == 200 && http.SessionCount == 1, "The new session works, and only one session exists.");
            var unknown = await http.PostAsync(Initialize(5), session: "00000000000000000000000000000000");
            Check(unknown.Status == 200 && unknown.SessionId != "00000000000000000000000000000000" && http.SessionCount == 2, "initialize with an unknown session id just opens a new session.");
            var sessions = new List<string> { again.SessionId!, unknown.SessionId! };
            for (var index = sessions.Count; index < McpHttpTransport.DefaultMaximumSessions; index++) sessions.Add(await http.InitializeAsync(10 + index));
            Check(http.SessionCount == 64, $"64 sessions can be open at once; {http.SessionCount} are.");
            var tooMany = await http.PostAsync(Initialize(100));
            Check(tooMany.Status == 503 && tooMany.Code == JsonRpc.ServerBusy && tooMany.SessionId is null, "The 65th session is refused with 503.");
            Check((await http.SendAsync(HttpMethod.Delete, null, session: sessions[10])).Status == 200 && (await http.PostAsync(Initialize(101))).Status == 200, "After a DELETE a session can be opened again.");
        }

        await using var idle = new HttpHarness("http-idle", null, idleTimeout: TimeSpan.FromMilliseconds(200));
        var expiring = await idle.InitializeAsync();
        Check((await idle.PostAsync(Request("ping", null, JsonValue.Create(2)), session: expiring)).Status == 200, "A session that is used stays.");
        await Task.Delay(500);
        Check((await idle.PostAsync(Request("ping", null, JsonValue.Create(3)), session: expiring)).Status == 404, "A session that was idle for longer than the limit is gone.");
        // A request that runs longer than the idle limit: the session survives it, and idle time counts from its end.
        var working = await idle.InitializeAsync(4);
        var slow = idle.PostAsync(ToolCall("launch_app", IdleLaunch(1), "slow"), session: working, accept: "application/json");
        await Task.Delay(600);
        Check((await idle.PostAsync(Request("ping", null, JsonValue.Create(5)), session: "00000000000000000000000000000000")).Status == 404, "Another request sweeps the sessions meanwhile.");
        var finished = await slow;
        Check(finished.Status == 200 && Result(finished.Json).GetProperty("isError").GetBoolean(), "The long request is answered on its session.");
        Check((await idle.PostAsync(Request("ping", null, JsonValue.Create(6)), session: working)).Status == 200, "A session whose request just finished is still valid, however long the request ran.");
    }

    private static async Task HttpStateless()
    {
        await using var http = new HttpHarness("http-stateless", null);
        var discover = await http.PostAsync(Modern("server/discover", null, "d1"), ModernHeaders("server/discover"));
        Check(discover.Status == 200 && Result(discover.Json).GetProperty("resultType").GetString() == "complete" && discover.SessionId is null && http.SessionCount == 0, "Stateless server/discover works without a session and mints none.");
        var missingHeader = await http.PostAsync(Modern("tools/list", null, "d2"));
        Check(missingHeader.Status == 400 && missingHeader.Code == JsonRpc.HeaderMismatch, "A missing MCP-Protocol-Version header must be 400/-32020.");
        var wrongMethod = await http.PostAsync(Modern("tools/list", null, "d3"), ModernHeaders("ping"));
        Check(wrongMethod.Status == 400 && wrongMethod.Code == JsonRpc.HeaderMismatch, "A mismatching Mcp-Method header must be 400/-32020.");
        var unsupported = await http.PostAsync(Modern("tools/list", null, "d4", "1999-01-01"), ModernHeaders("tools/list", version: "1999-01-01"));
        Check(unsupported.Status == 400 && unsupported.Code == JsonRpc.UnsupportedProtocolVersion, "An unsupported version must be 400/-32022.");
        var unknown = await http.PostAsync(Modern("no/such", null, "d5"), ModernHeaders("no/such"));
        Check(unknown.Status == 404 && unknown.Code == JsonRpc.MethodNotFound, "An unknown stateless method must be 404/-32601.");
        var withToken = await http.PostAsync(Modern("no/such", new JsonObject { ["_meta"] = new JsonObject { ["progressToken"] = "p" } }, "d5b"), ModernHeaders("no/such"));
        Check(withToken.Status == 404 && withToken.MediaType == "application/json" && withToken.Code == JsonRpc.MethodNotFound, "An unknown method with a progressToken is still 404 as plain JSON, not a stream.");
        foreach (var removed in new[] { "ping", "logging/setLevel", "initialize" })
        {
            var gone = await http.PostAsync(Modern(removed, new JsonObject { ["level"] = "info" }, "gone-" + removed), ModernHeaders(removed));
            Check(gone.Status == 404 && gone.Code == JsonRpc.MethodNotFound && gone.SessionId is null, $"{removed} is not part of revision 2026-07-28: 404/-32601; got {gone.Status} {gone.Body}");
        }

        // The envelope: what every request of the revision must carry. Invalid → -32602 with HTTP 400.
        async Task Invalid(string what, JsonObject body, Dictionary<string, string>? headers)
        {
            var reply = await http.PostAsync(body, headers);
            Check(reply.Status == 400 && reply.MediaType == "application/json" && reply.Code == JsonRpc.InvalidParams, $"{what} must be 400/-32602; got {reply.Status} {reply.Body}");
        }
        JsonObject Changed(string method, Action<JsonObject> change, string id, JsonObject? parameters = null) { var body = Modern(method, parameters, id); change(body["params"]!["_meta"]!.AsObject()); return body; }
        await Invalid("A request without clientCapabilities", Changed("tools/list", meta => meta.Remove(McpServer.MetaClientCapabilities), "e1"), ModernHeaders("tools/list"));
        await Invalid("clientCapabilities that is not an object", Changed("tools/list", meta => meta[McpServer.MetaClientCapabilities] = true, "e2"), ModernHeaders("tools/list"));
        await Invalid("A protocolVersion that is a number", Changed("tools/list", meta => meta[McpServer.MetaProtocolVersion] = 20260728, "e3"), ModernHeaders("tools/list"));
        await Invalid("A _meta that is not an object", Request("tools/list", new JsonObject { ["_meta"] = "2026-07-28" }, JsonValue.Create("e4")), ModernHeaders("tools/list"));
        await Invalid("The header 2026-07-28 without _meta", Request("tools/list", null, JsonValue.Create("e5")), ModernHeaders("tools/list"));
        await Invalid("server/discover without _meta", Request("server/discover", null, JsonValue.Create("e6")), null);
        await Invalid("A streamed tools/call without clientCapabilities", Changed("tools/call", meta => meta.Remove(McpServer.MetaClientCapabilities), "e7", new JsonObject { ["name"] = "list_tests", ["arguments"] = new JsonObject() }), ModernHeaders("tools/call", "list_tests"));
        Check(http.SessionCount == 0, "Nothing of this opens a session.");

        // Mcp-Name mirrors the tool, prompt or resource the body names.
        var listTests = new JsonObject { ["name"] = "list_tests", ["arguments"] = new JsonObject() };
        var noName = await http.PostAsync(Modern("tools/call", listTests.DeepClone().AsObject(), "n1"), ModernHeaders("tools/call"));
        Check(noName.Status == 400 && noName.Code == JsonRpc.HeaderMismatch, "tools/call without Mcp-Name must be 400/-32020.");
        var differing = await http.PostAsync(Modern("tools/call", listTests.DeepClone().AsObject(), "n2"), ModernHeaders("tools/call", "get_test"));
        Check(differing.Status == 400 && differing.Code == JsonRpc.HeaderMismatch, "A differing Mcp-Name must be 400/-32020.");
        var matching = await http.PostAsync(Modern("tools/call", listTests.DeepClone().AsObject(), "n3"), ModernHeaders("tools/call", "list_tests"), accept: "application/json");
        Check(matching.Status == 200 && Result(matching.Json).GetProperty("resultType").GetString() == "complete", "A matching Mcp-Name is served.");
        var encoded = await http.PostAsync(Modern("tools/call", listTests.DeepClone().AsObject(), "n4"), ModernHeaders("tools/call", Sentinel("list_tests")), accept: "application/json");
        Check(encoded.Status == 200 && Result(encoded.Json).GetProperty("resultType").GetString() == "complete", "A base64 sentinel Mcp-Name must be decoded and served.");
        var nameless = await http.PostAsync(Modern("tools/call", new JsonObject { ["arguments"] = new JsonObject() }, "n5"), ModernHeaders("tools/call", "list_tests"));
        Check(nameless.Status == 200 && nameless.Code == JsonRpc.InvalidParams, "A body without a tool name is an invalid-params error of the method (200), not a header mismatch: " + nameless.Body);
        var badTool = await http.PostAsync(Modern("tools/call", new JsonObject { ["name"] = "nope", ["arguments"] = new JsonObject() }, "n6"), ModernHeaders("tools/call", "nope"));
        Check(badTool.Status == 200 && badTool.MediaType == "application/json" && badTool.Code == JsonRpc.InvalidParams, "An unknown tool stays an in-band -32602 (HTTP 200).");
        const string uri = "testy://docs/mcp";
        Check((await http.PostAsync(Modern("resources/read", new JsonObject { ["uri"] = uri }, "r1"), ModernHeaders("resources/read", uri))).Status == 200, "resources/read with the URI in Mcp-Name is served.");
        Check((await http.PostAsync(Modern("resources/read", new JsonObject { ["uri"] = uri }, "r2"), ModernHeaders("resources/read", Sentinel(uri)))).Status == 200, "A base64 sentinel URI is decoded.");
        var otherUri = await http.PostAsync(Modern("resources/read", new JsonObject { ["uri"] = uri }, "r3"), ModernHeaders("resources/read", "testy://workspace"));
        Check(otherUri.Status == 400 && otherUri.Code == JsonRpc.HeaderMismatch, "A differing URI header must be 400/-32020.");
        Check((await http.PostAsync(Modern("resources/read", new JsonObject { ["uri"] = uri }, "r4"), ModernHeaders("resources/read"))).Code == JsonRpc.HeaderMismatch, "resources/read without Mcp-Name must be -32020.");
        var noUri = await http.PostAsync(Modern("resources/read", null, "r5"), ModernHeaders("resources/read", uri));
        Check(noUri.Status == 200 && noUri.Code == JsonRpc.InvalidParams, "resources/read without a uri is the method's invalid-params error.");
        var missingResource = await http.PostAsync(Modern("resources/read", new JsonObject { ["uri"] = "testy://runs/none" }, "r6"), ModernHeaders("resources/read", "testy://runs/none"));
        Check(missingResource.Status == 200 && missingResource.Code == JsonRpc.InvalidParams, "A resource that does not exist stays -32602 with HTTP 200.");
        var prompt = new JsonObject { ["name"] = "write_test", ["arguments"] = new JsonObject { ["app"] = "C:\\Apps\\Desk.exe", ["goal"] = "a goal" } };
        Check((await http.PostAsync(Modern("prompts/get", prompt.DeepClone().AsObject(), "p1"), ModernHeaders("prompts/get", "write_test"))).Status == 200, "prompts/get with a matching Mcp-Name is served.");
        Check((await http.PostAsync(Modern("prompts/get", prompt.DeepClone().AsObject(), "p2"), ModernHeaders("prompts/get", Sentinel("write_test")))).Status == 200, "A base64 sentinel prompt name is decoded.");
        Check((await http.PostAsync(Modern("prompts/get", prompt.DeepClone().AsObject(), "p3"), ModernHeaders("prompts/get", "other"))).Code == JsonRpc.HeaderMismatch, "A differing prompt name must be -32020.");
        Check((await http.PostAsync(Modern("prompts/get", prompt.DeepClone().AsObject(), "p4"), ModernHeaders("prompts/get"))).Code == JsonRpc.HeaderMismatch, "prompts/get without Mcp-Name must be -32020.");
        var unknownPrompt = await http.PostAsync(Modern("prompts/get", new JsonObject { ["name"] = "nope" }, "p5"), ModernHeaders("prompts/get", "nope"));
        Check(unknownPrompt.Status == 200 && unknownPrompt.Code == JsonRpc.InvalidParams, "An unknown prompt stays -32602 with HTTP 200.");
        Check((await http.PostAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled", ["params"] = new JsonObject { ["requestId"] = "x", ["_meta"] = Meta() } })).Status == 202, "Stateless notifications are 202.");
        // A handshake revision named per request belongs to a session, with that revision's status rules.
        var legacyMeta = await http.PostAsync(Modern("no/such", null, "l1", "2025-06-18"));
        Check(legacyMeta.Status == 400 && legacyMeta.Json.GetProperty("error").GetProperty("message").GetString()!.Contains("Mcp-Session-Id", StringComparison.Ordinal), "A request that names a handshake revision needs a session.");
        var session = await http.InitializeAsync();
        var inSession = await http.PostAsync(Modern("no/such", null, "l2", "2025-06-18"), session: session);
        Check(inSession.Status == 200 && inSession.Code == JsonRpc.MethodNotFound, "On a session an unknown method is -32601 with HTTP 200, whatever _meta names the revision.");
    }

    private static async Task HttpSse()
    {
        await using var http = new HttpHarness("sse", null);
        var session = await http.InitializeAsync();
        var streamed = await http.PostAsync(ToolCall("launch_app", IdleLaunch(2), "s1", progressToken: "p1"), session: session);
        Check(streamed.Status == 200 && streamed.MediaType == "text/event-stream" && streamed.SessionId == session, $"tools/call must be answered as a stream when the client accepts one; got {streamed.Status} {streamed.MediaType}.");
        var progress = streamed.Events.Where(e => e.TryGetProperty("method", out var m) && m.GetString() == "notifications/progress").ToList();
        Check(progress.Count >= 1 && progress.All(p => p.GetProperty("params").GetProperty("progressToken").GetString() == "p1"), "Progress events must precede the response.");
        var final = streamed.Events.Last();
        Check(final.GetProperty("id").GetString() == "s1" && final.GetProperty("result").GetProperty("isError").GetBoolean(), "The final event must be the response (a windowless app fails launch_app).");
        Check(streamed.Body.Contains("event: message\ndata: ", StringComparison.Ordinal), "SSE events use event: message and data: lines.");
        // Without a progress token the stream carries keep-alive comments and the response, nothing else.
        var quiet = await http.PostAsync(ToolCall("launch_app", IdleLaunch(3), "s2"), session: session);
        Check(quiet.MediaType == "text/event-stream" && quiet.Events.Count == 1 && quiet.Events[0].GetProperty("id").GetString() == "s2", "Without a progressToken the stream contains only the response: " + quiet.Body);
        Check(quiet.Body.Contains(": keep-alive\n\n", StringComparison.Ordinal), "A stream that is quiet for two seconds carries a keep-alive comment.");
        var blocks = quiet.Body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Check(blocks.All(block => block.StartsWith(": keep-alive", StringComparison.Ordinal) || block.StartsWith("event: message", StringComparison.Ordinal)), "Only keep-alive comments and messages may appear.");
        var quick = await http.PostAsync(ToolCall("list_tests", new JsonObject(), "s3"), session: session);
        Check(quick.MediaType == "text/event-stream" && new McpToolCall(quick.Message).RequireOk("list_tests").Structured.GetProperty("count").GetInt32() == 0, "Every tools/call is streamed, with or without a progress token.");
        var json = await http.PostAsync(ToolCall("launch_app", IdleLaunch(1), "s4", progressToken: "p2"), session: session, accept: "application/json");
        Check(json.Status == 200 && json.MediaType == "application/json" && json.Json.GetProperty("id").GetString() == "s4", "Without text/event-stream in Accept the response is plain JSON, even with a progress token.");
        var other = await http.PostAsync(Request("tools/list", null, JsonValue.Create("s5")), session: session);
        Check(other.MediaType == "application/json", "Methods other than tools/call are answered as JSON.");
        var unknownTool = await http.PostAsync(ToolCall("nope", new JsonObject(), "s6"), session: session);
        Check(unknownTool.Status == 200 && unknownTool.MediaType == "application/json" && unknownTool.Code == JsonRpc.InvalidParams, "A call that is refused before it runs is answered as JSON.");
        var stateless = await http.PostAsync(Modern("tools/call", new JsonObject { ["name"] = "list_tests", ["arguments"] = new JsonObject() }, "s7"), ModernHeaders("tools/call", "list_tests"));
        Check(stateless.Status == 200 && stateless.MediaType == "text/event-stream" && Result(stateless.Message).GetProperty("resultType").GetString() == "complete", "Stateless calls are streamed the same way.");

        // Closing the stream cancels the call, in both eras: the launched fixture is closed and nothing is left behind.
        foreach (var modern in new[] { false, true })
        {
            var body = modern ? Modern("tools/call", new JsonObject { ["name"] = "launch_app", ["arguments"] = JsonRpc.ToNode(IdleLaunch(30)), ["_meta"] = new JsonObject { ["progressToken"] = "d" } }, "disconnect")
                : ToolCall("launch_app", IdleLaunch(30), "disconnect", progressToken: "d");
            var request = http.Request(HttpMethod.Post, body, modern ? ModernHeaders("tools/call", "launch_app") : null, session: modern ? null : session);
            var response = await http.OpenAsync(request);
            Check(response.Content.Headers.ContentType?.MediaType == "text/event-stream", "The call is streamed.");
            var stream = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[4096];
            var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Check(read > 0 && Encoding.UTF8.GetString(buffer, 0, read).Contains("notifications/progress", StringComparison.Ordinal), "The first event is progress.");
            var pid = await http.LaunchedPidAsync(session);
            var watch = Stopwatch.StartNew();
            response.Dispose();
            request.Dispose();
            await RequireGoneAsync(pid, $"The fixture of a call whose stream was closed ({(modern ? "stateless" : "session")})", 6000);
            Check(watch.Elapsed < TimeSpan.FromSeconds(6), $"Closing the stream must cancel the call within seconds; it took {watch.Elapsed.TotalSeconds:F1} s.");
            await UntilAsync(async () => (await http.WorkspaceInfoAsync(session)).GetProperty("counts").GetProperty("launchedApps").GetInt32() == 0, "The cancelled call left an app behind.");
        }

        // notifications/cancelled: through the session, and through the stateless revision by request id.
        foreach (var modern in new[] { false, true })
        {
            var id = "cancel-" + modern;
            var body = modern ? Modern("tools/call", new JsonObject { ["name"] = "launch_app", ["arguments"] = JsonRpc.ToNode(IdleLaunch(30)) }, id) : ToolCall("launch_app", IdleLaunch(30), id);
            var pending = http.PostAsync(body, modern ? ModernHeaders("tools/call", "launch_app") : null, session: modern ? null : session);
            var pid = await http.LaunchedPidAsync(session);
            var cancel = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled", ["params"] = new JsonObject { ["requestId"] = id } };
            if (modern) cancel["params"]!["_meta"] = Meta();
            Check((await http.PostAsync(cancel, modern ? new Dictionary<string, string> { ["MCP-Protocol-Version"] = "2026-07-28", ["Mcp-Method"] = "notifications/cancelled" } : null, session: modern ? null : session)).Status == 202, "The cancellation is accepted.");
            var ended = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            Check(ended.Status == 200 && ended.MediaType == "text/event-stream" && ended.Events.Count == 0, $"The stream of a cancelled request ({(modern ? "stateless" : "session")}) ends without a response event: " + ended.Body);
            await RequireGoneAsync(pid, "The fixture of the cancelled request");
        }
        // Not streamed: a cancelled request ends with 204 and no body.
        var plain = http.PostAsync(ToolCall("launch_app", IdleLaunch(30), "cancel-json"), session: session, accept: "application/json");
        var plainPid = await http.LaunchedPidAsync(session);
        await http.PostAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled", ["params"] = new JsonObject { ["requestId"] = "cancel-json" } }, session: session);
        var none = await plain.WaitAsync(TimeSpan.FromSeconds(10));
        Check(none.Status == 204 && none.Body.Length == 0, $"A cancelled request that is not streamed ends with 204 and no body; got {none.Status} '{none.Body}'.");
        await RequireGoneAsync(plainPid, "The fixture of the cancelled request");
        // Two stateless requests with the same id: a cancellation that names it cannot know which one is meant and stops neither.
        var twinA = http.PostAsync(Modern("tools/call", new JsonObject { ["name"] = "launch_app", ["arguments"] = JsonRpc.ToNode(IdleLaunch(3)) }, "twin"), ModernHeaders("tools/call", "launch_app"));
        await http.LaunchedPidAsync(session);
        var twinB = http.PostAsync(Modern("subscriptions/listen", new JsonObject { ["notifications"] = new JsonObject() }, "twin"), ModernHeaders("subscriptions/listen"));
        await UntilAsync(() => http.Server.ListenStreams == 1, "The second request with the same id did not start.");
        await http.PostAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled", ["params"] = new JsonObject { ["requestId"] = "twin", ["_meta"] = Meta() } });
        var twinResult = await twinA.WaitAsync(TimeSpan.FromSeconds(15));
        Check(twinResult.Events.Count == 1 && Result(twinResult.Message).GetProperty("isError").GetBoolean(), "An ambiguous stateless cancellation is ignored: the call runs to its end.");
        Check(http.Server.ListenStreams == 1, "The stream with the same id stays open too.");
        await http.StopAsync();
        await twinB.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task HttpSubscriptions()
    {
        var http = new HttpHarness("http-subscriptions", null);
        await using var _ = http;
        var body = Modern("subscriptions/listen", new JsonObject { ["notifications"] = new JsonObject { ["toolsListChanged"] = true } }, "listen-1");
        var notStreamed = await http.PostAsync(body.DeepClone(), ModernHeaders("subscriptions/listen"), accept: "application/json");
        Check(notStreamed.Status == 406 && notStreamed.Json.GetProperty("error").GetProperty("message").GetString()!.Contains("text/event-stream", StringComparison.Ordinal) && http.Server.ListenStreams == 0,
            $"subscriptions/listen needs Accept: text/event-stream (406); got {notStreamed.Status} {notStreamed.Body}");
        var invalid = await http.PostAsync(Modern("subscriptions/listen", new JsonObject { ["notifications"] = "all" }, "listen-0"), ModernHeaders("subscriptions/listen"));
        Check(invalid.Status == 400 && invalid.MediaType == "application/json" && invalid.Code == JsonRpc.InvalidParams, "An invalid filter is 400/-32602 as JSON: " + invalid.Body);
        var withoutMeta = await http.PostAsync(Request("subscriptions/listen", new JsonObject { ["notifications"] = new JsonObject() }, JsonValue.Create("listen-x")));
        Check(withoutMeta.Status == 400 && withoutMeta.Code == JsonRpc.InvalidParams, "subscriptions/listen without _meta and without a session is an invalid request of the stateless revision.");

        // Closed by the client: the stream just ends.
        var request = http.Request(HttpMethod.Post, body.DeepClone(), ModernHeaders("subscriptions/listen"));
        var response = await http.OpenAsync(request);
        Check((int)response.StatusCode == 200 && response.Content.Headers.ContentType?.MediaType == "text/event-stream", "The subscription is answered as a stream.");
        var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[4096];
        var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var first = Encoding.UTF8.GetString(buffer, 0, read);
        Check(first.StartsWith("event: message\ndata: ", StringComparison.Ordinal) && first.Contains("notifications/subscriptions/acknowledged", StringComparison.Ordinal) && first.Contains("\"" + McpServer.MetaSubscriptionId + "\":\"listen-1\"", StringComparison.Ordinal),
            "The acknowledgement is the first event and carries the subscription id: " + first);
        Check(http.Server.ListenStreams == 1, "The stream is open.");
        response.Dispose();
        request.Dispose();
        await UntilAsync(() => http.Server.ListenStreams == 0, "Closing the stream must end the subscription.", 8000);

        // Closed by the server: notifications/cancelled, then the completion result, then the end of the stream.
        var open = http.PostAsync(Modern("subscriptions/listen", new JsonObject { ["notifications"] = new JsonObject() }, "listen-2"), ModernHeaders("subscriptions/listen"));
        await UntilAsync(() => http.Server.ListenStreams == 1, "The second stream did not open.");
        await http.StopAsync();
        var closed = await open.WaitAsync(TimeSpan.FromSeconds(10));
        var events = closed.Events;
        Check(events.Count == 3 && events[0].GetProperty("method").GetString() == "notifications/subscriptions/acknowledged" && events[1].GetProperty("method").GetString() == "notifications/cancelled"
            && events[1].GetProperty("params").GetProperty("requestId").GetString() == "listen-2", "A stream the server closes is acknowledged, then cancelled by notification: " + closed.Body);
        var result = events[2].GetProperty("result");
        Check(events[2].GetProperty("id").GetString() == "listen-2" && result.GetProperty("resultType").GetString() == "complete" && result.GetProperty("_meta").GetProperty(McpServer.MetaSubscriptionId).GetString() == "listen-2",
            "The completion result closes the stream: " + closed.Body);
    }

    /// <summary>Content whose bytes are produced while they are sent, without a declared length: the request goes out chunked.</summary>
    private sealed class LazyContent(long length, byte[] head, byte[] tail) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(head);
            var block = new byte[64 * 1024];
            Array.Fill(block, (byte)'a');
            for (var left = length - head.Length - tail.Length; left > 0; left -= block.Length) await stream.WriteAsync(block.AsMemory(0, (int)Math.Min(block.Length, left)));
            await stream.WriteAsync(tail);
        }
        protected override bool TryComputeLength(out long computed) { computed = 0; return false; }
    }

    private static async Task HttpLimits()
    {
        await using var http = new HttpHarness("http-limits", null);
        var session = await http.InitializeAsync();
        var head = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":\"big\",\"method\":\"ping\",\"params\":{\"pad\":\"");
        var tail = Encoding.UTF8.GetBytes("\"}}");
        async Task<HttpReply> SendAsync(long length, bool chunked)
        {
            var request = http.Request(HttpMethod.Post, null, session: session);
            if (chunked) { request.Content = new LazyContent(length, head, tail); request.Headers.TransferEncodingChunked = true; }
            else
            {
                var body = new byte[length];
                Array.Fill(body, (byte)'a');
                head.CopyTo(body, 0); tail.CopyTo(body, length - tail.Length);
                request.Content = new ByteArrayContent(body);
            }
            request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            return await http.SendAsync(request);
        }
        var atLimit = await SendAsync(McpHttpTransport.MaximumBodyBytes, chunked: false);
        Check(atLimit.Status == 200 && atLimit.Json.GetProperty("id").GetString() == "big", $"A body of exactly 32 MiB is parsed; got {atLimit.Status}.");
        var chunkedAtLimit = await SendAsync(McpHttpTransport.MaximumBodyBytes, chunked: true);
        Check(chunkedAtLimit.Status == 200, $"A chunked body of exactly 32 MiB is parsed; got {chunkedAtLimit.Status}.");
        var declared = await SendAsync(McpHttpTransport.MaximumBodyBytes + 1, chunked: false);
        Check(declared.Status == 413, $"A declared length above 32 MiB must be 413; got {declared.Status}.");
        using var self = Process.GetCurrentProcess();
        GC.Collect();
        var chunked = await SendAsync(McpHttpTransport.MaximumBodyBytes + 1, chunked: true);
        Check(chunked.Status == 413 && chunked.Json.GetProperty("error").GetProperty("message").GetString()!.Contains("32 MiB", StringComparison.Ordinal), $"A chunked body above 32 MiB must be 413; got {chunked.Status} {chunked.Body}");
        Check((await http.PostAsync(Request("ping", null, JsonValue.Create("after")), session: session)).Status == 200, "The server keeps serving after a refused body.");
        Check((await http.PostAsync("", session: session)).Status == 400, "An empty body must be 400.");
    }

    private static async Task LogHygiene()
    {
        var captured = new StringWriter();
        const string token = "unit-secret-token-0123456789abcdef";
        var log = new McpLog(TextWriter.Synchronized(captured), null);
        var ids = new List<string>();
        await using (var http = new HttpHarness("log-hygiene", token, idleTimeout: TimeSpan.FromMilliseconds(200), log: log))
        {
            Check((await http.PostAsync(Initialize(1), authorize: false)).Status == 401, "A request without the token is refused.");
            Check((await http.PostAsync(Initialize(1), new Dictionary<string, string> { ["Authorization"] = "Bearer " + token + "x" }, authorize: false)).Status == 401, "A request with a wrong token is refused.");
            var first = await http.InitializeAsync();
            ids.Add(first);
            Check((await http.PostAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, session: first)).Status == 202, "initialized is accepted.");
            Check((await http.PostAsync(ToolCall("list_tests", new JsonObject(), "t"), session: first)).Status == 200, "A tool call is served.");
            var replaced = await http.PostAsync(Initialize(2), session: first);
            ids.Add(replaced.SessionId!);
            Check((await http.SendAsync(HttpMethod.Delete, null, session: replaced.SessionId)).Status == 200, "DELETE ends the session.");
            var expiring = await http.InitializeAsync(3);
            ids.Add(expiring);
            await Task.Delay(500);
            Check((await http.PostAsync(Request("ping", null, JsonValue.Create(4)), session: expiring)).Status == 404, "The idle session expired.");
            Check((await http.PostAsync(Request("ping", null, JsonValue.Create(5)), new Dictionary<string, string> { ["Origin"] = "http://evil.example" }, session: expiring)).Status == 403, "A foreign origin is refused.");
        }
        var lines = captured.ToString();
        Check(lines.Contains("created", StringComparison.Ordinal) && lines.Contains("ended by the client", StringComparison.Ordinal) && lines.Contains("expired", StringComparison.Ordinal) && lines.Contains("replaced", StringComparison.Ordinal) && lines.Contains("Client initialized", StringComparison.Ordinal),
            "The session events are logged: " + lines);
        foreach (var id in ids)
        {
            Check(!lines.Contains(id, StringComparison.OrdinalIgnoreCase), "A whole session id must never be logged.");
            Check(lines.Contains(id[..8] + "…", StringComparison.Ordinal), "A session is logged by the first characters of its id.");
        }
        Check(!lines.Contains(token, StringComparison.Ordinal) && !lines.Contains("Bearer", StringComparison.OrdinalIgnoreCase), "Neither the token nor an Authorization header may be logged.");
    }

    // ═══════════════ Studio's watcher helpers ═══════════════
    private static Task WriteLedger()
    {
        var ledger = new WorkspaceWriteLedger();
        var path = Path.Combine(Root, "ledger", "tests", "abc.json");
        var test = new TestCase { Id = "abc", Name = "Ledger", Intent = "Own save", Steps = [new TestStep { Action = StepAction.AssertExists, Selector = "id:X" }] };
        var bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(test, TestyJson.Options));
        Check(!ledger.IsOwnWrite(path, bytes), "Nothing was noted yet, so the content is an outside change.");
        ledger.NoteWrite(path, test);
        Check(ledger.IsOwnWrite(path, bytes), "Exactly the serialized bytes are recognized as Studio's own save.");
        Check(ledger.IsOwnWrite(path.Replace("tests", "TESTS", StringComparison.Ordinal), bytes), "Paths compare without regard to letter case.");
        Check(ledger.IsOwnWrite(path, bytes), "An own write stays recognized while it is remembered (a watcher may read the file more than once).");
        var withBom = Encoding.UTF8.GetPreamble().Concat(bytes).ToArray();
        Check(!ledger.IsOwnWrite(path, withBom), "A byte order mark another tool added makes the content an outside change.");
        test.Intent = "Changed by an outside tool";
        Check(!ledger.IsOwnWrite(path, new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(test, TestyJson.Options))), "Different content is an outside change.");
        Check(!ledger.IsOwnWrite(Path.Combine(Root, "ledger", "tests", "other.json"), bytes), "The same content under another path is an outside change.");
        var deleted = Path.Combine(Root, "ledger", "tests", "gone.json");
        Check(!ledger.IsOwnDelete(deleted), "A delete that was not noted is an outside delete.");
        ledger.NoteDelete(deleted);
        Check(ledger.IsOwnDelete(deleted), "A noted delete is Studio's own.");
        Check(!ledger.IsOwnDelete(deleted), "An own delete counts once: the next delete of that path is an outside one.");
        ledger.NoteDelete(deleted);
        ledger.ForgetDelete(deleted);
        Check(!ledger.IsOwnDelete(deleted), "A delete that failed after being noted is forgotten.");
        var forgetful = new WorkspaceWriteLedger(TimeSpan.FromMilliseconds(1));
        forgetful.NoteWrite(path, bytes);
        forgetful.NoteDelete(deleted);
        Thread.Sleep(20);
        Check(!forgetful.IsOwnWrite(path, bytes) && !forgetful.IsOwnDelete(deleted), "Entries are forgotten after the memory span.");

        var first = DateTimeOffset.UtcNow;
        Check(ChangeDebounce.Delay(first, first) == ChangeDebounce.QuietPeriod, "A fresh burst waits the quiet period.");
        Check(ChangeDebounce.Delay(first, first.AddMilliseconds(500)) == ChangeDebounce.QuietPeriod, "Half a second in, another event still waits the quiet period.");
        Check(ChangeDebounce.Delay(first, first.AddMilliseconds(800)) == TimeSpan.FromMilliseconds(200), "Near the maximum wait only the remainder is waited.");
        Check(ChangeDebounce.Delay(first, first.AddMilliseconds(1000)) == TimeSpan.Zero && ChangeDebounce.Delay(first, first.AddSeconds(5)) == TimeSpan.Zero, "At or after the maximum wait a steady stream is delivered at once.");
        Check(ChangeDebounce.MaximumWait == TimeSpan.FromSeconds(1) && ChangeDebounce.QuietPeriod == TimeSpan.FromMilliseconds(300), "The documented debounce values (300 ms quiet, 1 s at most) are the ones in force.");
        return Task.CompletedTask;
    }

    // ═══════════════ Discovery ═══════════════
    private static Task Describe()
    {
        var catalog = new McpToolCatalog(null);
        var manifest = McpDescribe.Manifest(catalog, Path.Combine(Root, "describe"), null, relative: false);
        Check(manifest["schema"]!.GetValue<string>() == McpDescribe.ManifestSchema && manifest["name"]!.GetValue<string>() == "testy" && manifest["version"]!.GetValue<string>() == McpServer.Version, "Manifest identity fields are wrong.");
        Check(manifest["protocolVersions"]!["all"]!.AsArray().Count == 5 && manifest["tools"]!.AsArray().Count == ExpectedTools.Length, "Manifest must list every version and tool.");
        var stdio = manifest["transports"]!["stdio"]!;
        Check(File.Exists(stdio["command"]!.GetValue<string>()) && stdio["args"]!.AsArray().Select(a => a!.GetValue<string>()).Contains("mcp"), "The stdio command must be an existing executable with the mcp verb.");
        var http = manifest["transports"]!["http"]!;
        Check(http["urlTemplate"]!.GetValue<string>() == "http://127.0.0.1:{port}/mcp" && http["args"]!.AsArray().Select(a => a!.GetValue<string>()).SequenceEqual(stdio["args"]!.AsArray().Select(a => a!.GetValue<string>()).Concat(["--transport", "http", "--port", "0"])), "The HTTP launch description is inconsistent.");
        Check(http["options"]!.AsObject().Any(option => option.Key.StartsWith(McpOptions.TokenVariable, StringComparison.Ordinal)) && http["endpointFile"]!.GetValue<string>().EndsWith(@"mcp\endpoint.json", StringComparison.Ordinal), "The HTTP description names the token variable and the endpoint file.");
        Check(manifest["clientConfiguration"]!["mcpServers"]!["testy"]!["command"]!.GetValue<string>() == stdio["command"]!.GetValue<string>(), "The client configuration shape must match the stdio command.");
        Check(manifest["resources"]!.AsArray().Count == 2 && manifest["resourceTemplates"]!.AsArray().Count == 3 && manifest["prompts"]!.AsArray().Count == 1, "Resources, templates and prompts must be summarized.");
        // What the server can do is stated plainly wherever a harness or its owner reads about it.
        foreach (var security in new[] { manifest["security"]!.GetValue<string>(), http["security"]!.GetValue<string>(), McpDescribe.Security })
        {
            Check(security.Contains("start desktop (GUI) programs", StringComparison.Ordinal) && security.Contains("keyboard and mouse input", StringComparison.Ordinal), "The security text must say that the server starts programs and sends input.");
            Check(security.Contains("not a sandbox", StringComparison.Ordinal) && security.Contains(McpOptions.TokenVariable, StringComparison.Ordinal) && security.Contains("another Windows account", StringComparison.Ordinal), "The security text must name the limits of the protection.");
        }
        Check(manifest["launchPolicy"]!["refusedPrograms"]!.AsArray().Count == McpLaunchPolicy.RefusedPrograms.Length, "The manifest reports the launch policy.");
        Check(McpOptions.Usage.Contains("start desktop (GUI) programs", StringComparison.Ordinal), "The usage text says what the server can do.");
        var relative = McpDescribe.Manifest(catalog, Path.Combine(Root, "describe"), null, relative: true)["transports"]!["stdio"]!;
        Check(!Path.IsPathRooted(relative["command"]!.GetValue<string>()) && relative["args"]!.AsArray().All(a => !Path.IsPathRooted(a!.GetValue<string>())), "Relative manifests must not contain rooted paths.");
        var server = McpDescribe.ServerJson(catalog, relative: true);
        // The rules of the registry schema this file names: name pattern and length, description length, a specific version, no ranges.
        Check(server["$schema"]!.GetValue<string>() == McpDescribe.RegistrySchema, "server.json must reference the registry schema.");
        var name = server["name"]!.GetValue<string>();
        Check(Regex.IsMatch(name, "^[a-zA-Z0-9.-]+/[a-zA-Z0-9._-]+$") && name.Length is >= 3 and <= 200 && name == McpDescribe.RegistryNamespace + "/testy", "server.json name must be <reverse-DNS namespace>/<name>.");
        var description = server["description"]!.GetValue<string>();
        Check(description.Length is >= 1 and <= 100, "server.json description must be 1–100 characters.");
        var version = server["version"]!.GetValue<string>();
        Check(Regex.IsMatch(version, @"^\d+\.\d+\.\d+") && version.Length <= 255 && !Regex.IsMatch(version, @"[\^~<>=*]|\bx\b|latest", RegexOptions.IgnoreCase), "server.json version must be one specific version, not a range.");
        Check(server["title"]!.GetValue<string>().Length is >= 1 and <= 100, "server.json title must be 1–100 characters.");
        Check(server.AsObject().Select(p => p.Key).All(key => key is "$schema" or "name" or "title" or "description" or "version" or "_meta"), "server.json carries only properties of the registry schema.");
        Check(server["_meta"]!.AsObject().Select(p => p.Key).SequenceEqual([McpDescribe.PublisherMetaKey]), "Publisher data lives under the registry's publisher-provided key.");
        var local = server["_meta"]![McpDescribe.PublisherMetaKey]![McpDescribe.LocalServerKey]!;
        var transports = local["transports"]!.AsArray();
        Check(transports.Count == 2 && transports[0]!["type"]!.GetValue<string>() == "stdio" && transports[1]!["type"]!.GetValue<string>() == "streamable-http", "server.json must describe both transports.");
        Check(transports[0]!["args"]!.AsArray().Select(a => a!.GetValue<string>()).Contains("mcp") && !Path.IsPathRooted(transports[0]!["command"]!.GetValue<string>()), "The relative server.json command must not be rooted.");
        Check(local["tools"]!.AsArray().Count == ExpectedTools.Length && local["security"]!.GetValue<string>() == McpDescribe.Security, "server.json must list the tools and state what the server can do.");
        Check(JsonDocument.Parse(server.ToJsonString()).RootElement.ValueKind == JsonValueKind.Object, "server.json must serialize.");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Check(!server.ToJsonString().Contains(profile, StringComparison.OrdinalIgnoreCase) && !McpDescribe.Manifest(catalog, Path.Combine(Root, "describe"), null, relative: true).ToJsonString().Contains(profile, StringComparison.OrdinalIgnoreCase), "Portable (relative) descriptions must not bake in this machine's user profile path.");
        Check(McpDescribe.ServerJson(catalog, relative: false).ToJsonString().Contains(AgentFiles.DefaultWorkspace.Replace("\\", "\\\\"), StringComparison.Ordinal), "The absolute description names the real default workspace.");
        return Task.CompletedTask;
    }
}
