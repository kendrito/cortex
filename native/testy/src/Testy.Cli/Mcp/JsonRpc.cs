using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Testy.Core;

namespace Testy.Cli.Mcp;

internal enum JsonRpcKind { Request, Notification, Response, Invalid }

/// <summary>One classified JSON-RPC 2.0 message. An invalid message carries the error code and text to answer with.</summary>
internal sealed class JsonRpcMessage
{
    public JsonRpcKind Kind { get; init; }
    public JsonElement Element { get; init; }
    public JsonElement? Id { get; init; }
    public string Method { get; init; } = "";
    public JsonElement? Params { get; init; }
    public int ProblemCode { get; init; }
    public string Problem { get; init; } = "";
}

/// <summary>JSON-RPC 2.0 envelope helpers for the MCP wire format. Every serialized message is one line of UTF-8 JSON.</summary>
internal static class JsonRpc
{
    public const int ParseError = -32700, InvalidRequest = -32600, MethodNotFound = -32601, InvalidParams = -32602, InternalError = -32603;
    /// <summary>Application-defined (outside the JSON-RPC reserved range -32768…-32000): this server has no free session or subscription stream.</summary>
    public const int ServerBusy = 1001;
    /// <summary>Resource not found for the handshake revisions (2025-11-25 and earlier); revision 2026-07-28 uses InvalidParams instead.</summary>
    public const int ResourceNotFound = -32002;
    public const int HeaderMismatch = -32020, MissingRequiredClientCapability = -32021, UnsupportedProtocolVersion = -32022;
    /// <summary>The largest single message either transport accepts (one stdin line, one HTTP body).</summary>
    public const int MaximumMessageBytes = 32 * 1024 * 1024;

    /// <summary>Single-line output; the encoder keeps Unicode readable and always escapes control characters, so a message never contains a raw newline.</summary>
    public static JsonSerializerOptions WireOptions { get; } = new(JsonSerializerDefaults.General) { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    /// <summary>
    /// The JSON an agent reads as text (tool results, resources): Testy's property names and enum spelling, compact, with quotes, apostrophes,
    /// angle brackets and non-ASCII text written literally so selectors and expected values can be copied exactly as they appear.
    /// </summary>
    public static JsonSerializerOptions TextOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    public static string Serialize(JsonNode node) => node.ToJsonString(WireOptions);
    public static JsonNode? Clone(JsonElement element) => JsonNode.Parse(element.GetRawText());
    public static JsonNode ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, TextOptions) ?? new JsonObject();
    public static string ToText(JsonNode node) => node.ToJsonString(TextOptions);
    public static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    public static JsonObject Response(JsonElement id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = Clone(id), ["result"] = result };
    public static JsonObject Error(JsonElement? id, int code, string message, JsonNode? data = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null) error["data"] = data;
        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id is null ? null : Clone(id.Value), ["error"] = error };
    }
    public static JsonObject Notification(string method, JsonObject? parameters)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null) message["params"] = parameters;
        return message;
    }
    /// <summary>Distinguishes the string id "1" from the number 1 when tracking in-flight requests.</summary>
    public static string IdKey(JsonElement id) => id.ValueKind == JsonValueKind.String ? "s:" + id.GetString() : "n:" + id.GetRawText();
    /// <summary>A string or an integer. A number with a fraction (1.5) is not a valid MCP request id.</summary>
    public static bool IsValidId(JsonElement? id) => id is { ValueKind: JsonValueKind.String } || (id is { ValueKind: JsonValueKind.Number } number && IsInteger(number));
    public static bool IsInteger(JsonElement number) =>
        number.ValueKind == JsonValueKind.Number && (number.TryGetInt64(out _) || (number.TryGetDouble(out var value) && double.IsFinite(value) && Math.Floor(value) == value));
    public static int? ErrorCode(JsonObject message) => message["error"] is JsonObject error && error["code"] is JsonValue code && code.TryGetValue<int>(out var value) ? value : null;

    /// <summary>Removes a leading UTF-8 byte order mark: some hosts write one when they open the pipe or save the body.</summary>
    public static ReadOnlyMemory<byte> WithoutByteOrderMark(ReadOnlyMemory<byte> bytes) =>
        bytes.Length >= 3 && bytes.Span[0] == 0xEF && bytes.Span[1] == 0xBB && bytes.Span[2] == 0xBF ? bytes[3..] : bytes;

    /// <summary>Parses one message (or batch). A failure is a -32700 text that names the problem; the input must be valid UTF-8 JSON.</summary>
    public static bool TryParse(ReadOnlyMemory<byte> bytes, out JsonElement root, out string problem)
    {
        root = default; problem = "";
        try { StrictUtf8.GetCharCount(bytes.Span); }
        catch (DecoderFallbackException) { problem = "Parse error: the message is not valid UTF-8."; return false; }
        try { using var document = JsonDocument.Parse(bytes); root = document.RootElement.Clone(); return true; }
        catch (JsonException ex) { problem = "Parse error: " + ex.Message; return false; }
    }

    public static JsonRpcMessage Classify(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return Invalid(null, InvalidRequest, "A JSON-RPC message must be a JSON object.", element);
        JsonElement? id = element.TryGetProperty("id", out var rawId) ? rawId : null;
        var validId = IsValidId(id);
        if (!element.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")
            return Invalid(validId ? id : null, InvalidRequest, "Expected \"jsonrpc\": \"2.0\".", element);
        if (!element.TryGetProperty("method", out var method))
        {
            if (element.TryGetProperty("result", out _) || element.TryGetProperty("error", out _))
                return new JsonRpcMessage { Kind = JsonRpcKind.Response, Element = element, Id = validId ? id : null };
            return Invalid(validId ? id : null, InvalidRequest, "A message needs a method (request or notification) or a result/error (response).", element);
        }
        if (method.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(method.GetString()))
            return Invalid(validId ? id : null, InvalidRequest, "The method must be a nonempty string.", element);
        JsonElement? parameters = null;
        if (element.TryGetProperty("params", out var supplied) && supplied.ValueKind != JsonValueKind.Null)
        {
            if (supplied.ValueKind != JsonValueKind.Object)
            {
                if (id is null) return new JsonRpcMessage { Kind = JsonRpcKind.Notification, Element = element, Method = method.GetString()! };
                return Invalid(validId ? id : null, InvalidParams, "params must be a JSON object.", element);
            }
            parameters = supplied;
        }
        if (id is null) return new JsonRpcMessage { Kind = JsonRpcKind.Notification, Element = element, Method = method.GetString()!, Params = parameters };
        if (!validId) return Invalid(null, InvalidRequest, "Request ids must be strings or integers; null and numbers with a fraction are not allowed.", element);
        return new JsonRpcMessage { Kind = JsonRpcKind.Request, Element = element, Id = id, Method = method.GetString()!, Params = parameters };
    }

    private static JsonRpcMessage Invalid(JsonElement? id, int code, string problem, JsonElement element) =>
        new() { Kind = JsonRpcKind.Invalid, Element = element, Id = id, ProblemCode = code, Problem = problem };
}
