using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Testy.Core;

public abstract class HttpPlannerBase
{
    protected readonly ProviderSettings Settings;
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(3), MaxResponseContentBufferSize = 16 * 1024 * 1024 };
    private readonly HttpClient http;
    protected List<ProviderRequestAttempt> ProviderAttempts { get; } = [];
    protected HttpPlannerBase(ProviderSettings settings, HttpClient? client = null)
    {
        Settings = TestyJson.Clone(settings);
        http = client ?? SharedClient;
    }
    protected async Task<JsonDocument> SendAsync(object body, CancellationToken ct)
    {
        if (CortexGuestRelay.Enabled) return await CortexGuestRelay.SendAsync(JsonSerializer.Serialize(body, TestyJson.Options), ct);
        if (string.IsNullOrWhiteSpace(Settings.Model)) throw new InvalidOperationException("Set a model name in Provider settings before using this provider.");
        if (!Uri.TryCreate(CortexModelBridge.Enabled ? CortexModelBridge.Endpoint : Settings.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" && !(endpoint.Scheme == "http" && endpoint.IsLoopback) || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new InvalidOperationException("The provider endpoint must be HTTPS, or HTTP on localhost, without credentials in the URL.");
        if (Settings.MaximumProviderRetries is < 0 or > 3 || Settings.ProviderRetryDelayMs is < 0 or > 5000) throw new InvalidDataException("Provider retry budget must be0–3 and delay0–5000ms.");
        var key = ProviderCredentialStore.Resolve(Settings);
        if (Settings.Kind == ProviderKind.OpenAI && string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"Set environment variable {Settings.ApiKeyEnvironmentVariable} for the API adapter. Codex and Offline modes do not require an API key.");
        var requestId = Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.Serialize(body, TestyJson.Options);
        for (var attempt = 0; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
            if (!string.IsNullOrWhiteSpace(key)) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            CortexModelBridge.BindRequest(message);
            message.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            var started = DateTimeOffset.UtcNow;
            using var response = await http.SendAsync(message, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            var retry = !response.IsSuccessStatusCode && (int)response.StatusCode is 429 or 502 or 503 or 504 && attempt < Settings.MaximumProviderRetries;
            ProviderAttempts.Add(new ProviderRequestAttempt { RequestId = requestId, Attempt = attempt + 1, StartedAt = started, FinishedAt = DateTimeOffset.UtcNow, StatusCode = (int)response.StatusCode, Retrying = retry });
            if (response.IsSuccessStatusCode) return JsonDocument.Parse(text);
            if (retry)
            {
                // No model output has been parsed or dispatched. Never put host tool execution inside this retry.
                var delay = Math.Min(5000, Settings.ProviderRetryDelayMs * (1 << attempt));
                if (response.Headers.RetryAfter?.Delta is { } retryAfter) delay = Math.Min(5000, Math.Max(delay, (int)Math.Min(5000, retryAfter.TotalMilliseconds)));
                await Task.Delay(delay, ct); continue;
            }
            string detail = response.ReasonPhrase ?? "Provider error";
            try
            {
                using var error = JsonDocument.Parse(text);
                if (error.RootElement.TryGetProperty("error", out var value) && value.TryGetProperty("message", out var msg)) detail = msg.GetString() ?? detail;
            }
            catch (JsonException) { }
            if (!string.IsNullOrEmpty(key)) detail = detail.Replace(key, "[REDACTED]", StringComparison.Ordinal);
            if (detail.Length > 1200) detail = detail[..1200];
            throw new HttpRequestException($"Provider returned HTTP {(int)response.StatusCode}: {detail}", null, response.StatusCode);
        }
    }
    protected static string ResponsesText(JsonElement root)
    {
        if (root.TryGetProperty("status", out var status) && status.GetString() is not ("completed" or null)) throw new InvalidDataException($"Provider response status is '{status.GetString()}'; no complete answer was returned.");
        var chunks = new List<string>();
        if (root.TryGetProperty("output", out var output))
            foreach (var item in output.EnumerateArray())
                if (item.TryGetProperty("type", out var type) && type.GetString() == "message" && item.TryGetProperty("content", out var content))
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.GetProperty("type").GetString() == "output_text") chunks.Add(part.GetProperty("text").GetString() ?? "");
                        else if (part.GetProperty("type").GetString() == "refusal") throw new InvalidOperationException("The model declined this request: " + part.GetProperty("refusal").GetString());
                    }
        if (chunks.Count == 0) throw new InvalidDataException("Provider returned no output text.");
        return string.Join("", chunks);
    }
}

public sealed class ProviderRequestAttempt
{
    public string RequestId { get; set; } = "";
    public int Attempt { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public int StatusCode { get; set; }
    public bool Retrying { get; set; }
}

/// <summary>OpenAI Responses structured planning adapter. Does not execute native computer calls.</summary>
public sealed class ResponsesPlanner(ProviderSettings settings, HttpClient? client = null) : HttpPlannerBase(settings, client), ITestPlanner
{
    public string Name => "OpenAI Responses";
    public async Task<TestCase> CreatePlanAsync(PlanningRequest request, CancellationToken cancellationToken = default)
    {
        var content = new List<object> { new { type = "input_text", text = PlannerPrompt.Plan(request) } };
        var image = Settings.SupportsImages ? await PlannerPrompt.ImageDataAsync(request.ScreenshotPath, cancellationToken) : null;
        if (image is not null) content.Add(new { type = "input_image", image_url = image });
        using var response = await SendAsync(new
        {
            model = Settings.Model, store = false, instructions = PlannerPrompt.System,
            input = new[] { new { role = "user", content } },
            text = new { format = new { type = "json_schema", name = "testy_test_plan", strict = true, schema = PlanCodec.Schema } }
        }, cancellationToken);
        return PlanCodec.Parse(ResponsesText(response.RootElement), request);
    }
    public async Task<string> ExplainAsync(RunResult run, CancellationToken cancellationToken = default)
    {
        var content = new List<object> { new { type = "input_text", text = PlannerPrompt.Explain(run) } };
        var path = run.Steps.LastOrDefault(s => !string.IsNullOrWhiteSpace(s.ScreenshotPath))?.ScreenshotPath ?? "";
        var image = Settings.SupportsImages ? await PlannerPrompt.ImageDataAsync(path, cancellationToken) : null;
        if (image is not null) content.Add(new { type = "input_image", image_url = image });
        using var response = await SendAsync(new { model = Settings.Model, store = false, input = new[] { new { role = "user", content } } }, cancellationToken);
        return ResponsesText(response.RootElement);
    }
}

/// <summary>OpenAI-compatible Chat Completions structured planning adapter for models without a native computer tool.</summary>
public sealed class CompatiblePlanner(ProviderSettings settings, HttpClient? client = null) : HttpPlannerBase(settings, client), ITestPlanner
{
    private static readonly JsonElement RemotePlanSchema = CreateRemotePlanSchema();
    private static JsonElement CreateRemotePlanSchema()
    {
        // Some compatible schema decoders reject large bounded arrays. Preserve every field/type
        // constraint remotely; the authoritative host parser still enforces the 200-step limit.
        var schema = JsonNode.Parse(PlanCodec.Schema.GetRawText())!;
        schema["properties"]!["steps"]!.AsObject().Remove("maxItems");
        return JsonSerializer.SerializeToElement(schema);
    }
    public string Name => "Compatible Chat Completions";
    public async Task<TestCase> CreatePlanAsync(PlanningRequest request, CancellationToken cancellationToken = default)
    {
        var content = new List<object> { new { type = "text", text = PlannerPrompt.Plan(request) } };
        var image = Settings.SupportsImages ? await PlannerPrompt.ImageDataAsync(request.ScreenshotPath, cancellationToken) : null;
        if (image is not null) content.Add(new { type = "image_url", image_url = new { url = image } });
        using var response = await SendAsync(new
        {
            model = Settings.Model,
            messages = new object[] { new { role = "system", content = PlannerPrompt.System }, new { role = "user", content } },
            response_format = new { type = "json_schema", json_schema = new { name = "testy_test_plan", strict = true, schema = RemotePlanSchema } }
        }, cancellationToken);
        return PlanCodec.Parse(ReadText(response.RootElement), request);
    }
    public async Task<string> ExplainAsync(RunResult run, CancellationToken cancellationToken = default)
    {
        var content = new List<object> { new { type = "text", text = PlannerPrompt.Explain(run) } };
        var path = run.Steps.LastOrDefault(s => !string.IsNullOrWhiteSpace(s.ScreenshotPath))?.ScreenshotPath ?? "";
        var image = Settings.SupportsImages ? await PlannerPrompt.ImageDataAsync(path, cancellationToken) : null;
        if (image is not null) content.Add(new { type = "image_url", image_url = new { url = image } });
        using var response = await SendAsync(new { model = Settings.Model, messages = new[] { new { role = "user", content } } }, cancellationToken);
        return ReadText(response.RootElement);
    }
    internal static string ReadText(JsonElement root)
    {
        var choice = root.GetProperty("choices")[0];
        if (choice.TryGetProperty("finish_reason", out var finish) && finish.GetString() is not ("stop" or null))
            throw new InvalidDataException($"Provider response ended with '{finish.GetString()}'; no complete plan was returned.");
        return choice.GetProperty("message").GetProperty("content").GetString() ?? throw new InvalidDataException("Provider returned no message content.");
    }
}
