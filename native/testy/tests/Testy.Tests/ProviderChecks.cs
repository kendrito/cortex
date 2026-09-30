using System.Net;
using System.Text;
using System.Text.Json;
using Testy.Core;

namespace Testy.Tests;

internal static class ProviderChecks
{
    private const string Plan = """{"name":"Observe status","intent":"Verify ready","steps":[{"title":"Ready status","action":"assertText","selector":"id:Status","value":"Ready","timeoutMs":200,"x":0,"y":0}]}""";
    public static IEnumerable<(string Name, Func<Task> Execute)> All()
    {
        yield return ("Responses provider sends strict schema and parses output", ResponsesProtocol);
        yield return ("Compatible provider uses chat messages and strict schema", CompatibleProtocol);
        yield return ("HTTP credentials fall back to Windows user variables and preserve process precedence", UserCredentialFallback);
        yield return ("Responses planning and explanations honor the image-sharing setting", ResponsesImageSetting);
        yield return ("API provider refuses missing credential before making a request", MissingCredential);
        yield return ("provider rejects incomplete and malformed model responses", InvalidResponse);
        yield return ("provider restricts plaintext remote endpoints", EndpointBoundary);
        yield return ("local computer tools enforce schemas, target and call limits", ComputerTools);
        yield return ("offline parser is deterministic and rejects unsupported instructions", OfflineCommands);
        yield return ("custom computer-use loop returns tool observations and preserves assertion results", ComputerLoop);
        yield return ("custom computer-use loop enforces turn and tool boundaries", ComputerLoopBoundaries);
        yield return ("Codex resolution honors explicit configuration and process PATH order", CodexResolutionPriority);
        yield return ("Codex resolution finds only direct official installs and latest version children", CodexOfficialResolution);
    }
    private static Task CodexResolutionPriority()
    {
        var root = Path.Combine(Path.GetTempPath(), "Testy-codex-resolution-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = CreateExecutable(Path.Combine(root, "first path", "codex.exe"));
            var second = CreateExecutable(Path.Combine(root, "second", "codex.exe"));
            CreateExecutable(Path.Combine(root, "local", "OpenAI", "Codex", "bin", "version", "codex.exe"));
            var searchPath = "\"" + Path.GetDirectoryName(first) + "\"" + Path.PathSeparator + Path.GetDirectoryName(second);
            foreach (var explicitValue in new[] { "custom-codex", Path.Combine(root, "configured", "codex.exe") })
                Check(CodexExecutableResolver.Resolve(explicitValue, searchPath, Path.Combine(root, "local")) == explicitValue, "Explicit Codex configuration was overridden.");
            Check(CodexExecutableResolver.Resolve("codex.exe", searchPath, Path.Combine(root, "local")) == first, "Process PATH order was not preserved ahead of official fallback.");
            Check(CodexExecutableResolver.Resolve(" ", Path.GetDirectoryName(second)!, Path.Combine(root, "local")) == second, "Blank configuration did not use default executable resolution.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        return Task.CompletedTask;
    }
    private static Task CodexOfficialResolution()
    {
        var root = Path.Combine(Path.GetTempPath(), "Testy-codex-official-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bin = Path.Combine(root, "OpenAI", "Codex", "bin");
            CreateExecutable(Path.Combine(root, "unrelated", "codex.exe"));
            CreateExecutable(Path.Combine(bin, "version", "nested", "codex.exe"));
            Check(CodexExecutableResolver.Resolve("codex.exe", "", root) == "codex.exe", "Resolver scanned outside the direct official install locations.");
            var direct = CreateExecutable(Path.Combine(bin, "codex.exe"));
            File.SetLastWriteTimeUtc(direct, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Check(CodexExecutableResolver.Resolve("codex.exe", "", root) == direct, "Direct official install was not found.");
            var older = CreateExecutable(Path.Combine(bin, "version-old", "codex.exe"));
            File.SetLastWriteTimeUtc(older, new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var newer = CreateExecutable(Path.Combine(bin, "version-new", "codex.exe"));
            File.SetLastWriteTimeUtc(newer, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Check(CodexExecutableResolver.Resolve("CODEX.EXE", "", root) == newer, "Newest immediate official version was not selected.");
            Check(CodexExecutableResolver.Resolve(null, "", Path.Combine(root, "missing")) == "codex.exe", "Missing installation did not preserve the friendly CLI launch failure path.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        return Task.CompletedTask;
    }
    private static string CreateExecutable(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "Resolver test only; never executed.");
        return Path.GetFullPath(path);
    }
    private static async Task ResponsesProtocol()
    {
        var variable = "TESTY_TEST_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "test-only-key");
        try
        {
            using var handler = new Handler(async request =>
            {
                Check(request.Headers.Authorization?.Parameter == "test-only-key", "Expected configured environment credential.");
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Check(body.RootElement.GetProperty("store").GetBoolean() == false, "Responses requests must opt out of response storage.");
                Check(body.RootElement.GetProperty("text").GetProperty("format").GetProperty("type").GetString() == "json_schema", "Missing schema format.");
                Check(body.RootElement.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean(), "Strict schema disabled.");
                var text = body.RootElement.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString()!;
                Check(!text.Contains("private-password", StringComparison.Ordinal), "Password leaked to provider context.");
                return Json(new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = Plan } } } } });
            });
            using var client = new HttpClient(handler);
            var planner = new ResponsesPlanner(new ProviderSettings { Kind = ProviderKind.OpenAI, Model = "configured-model", ApiKeyEnvironmentVariable = variable }, client);
            var result = await planner.CreatePlanAsync(Request());
            Check(result.Steps[0].Action == StepAction.AssertText && result.Steps[0].Value == "Ready", "Response plan was not parsed.");
            Check(handler.Calls == 1, "Unexpected number of HTTP calls.");
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }
    private static async Task CompatibleProtocol()
    {
        using var handler = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Check(body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString() == "system", "Missing system prompt.");
            Check(body.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean(), "Missing strict chat schema.");
            return Json(new { choices = new[] { new { finish_reason = "stop", message = new { content = Plan } } } });
        });
        using var client = new HttpClient(handler);
        var planner = new CompatiblePlanner(new ProviderSettings { Kind = ProviderKind.Compatible, Model = "local-model", Endpoint = "http://localhost:1234/v1/chat/completions", ApiKeyEnvironmentVariable = "" }, client);
        Check((await planner.CreatePlanAsync(Request())).Name == "Observe status", "Compatible response not parsed.");
    }
    private static async Task UserCredentialFallback()
    {
        if (!OperatingSystem.IsWindows()) return;
        var variable = "TESTY_DUMMY_USER_KEY_" + Guid.NewGuid().ToString("N");
        var priorProcess = Environment.GetEnvironmentVariable(variable);
        var priorUser = Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.User);
        try
        {
            Environment.SetEnvironmentVariable(variable, null);
            Environment.SetEnvironmentVariable(variable, "dummy-user-value", EnvironmentVariableTarget.User);
            var expected = "dummy-user-value";
            using var handler = new Handler(request =>
            {
                Check(request.Headers.Authorization?.Parameter == expected, "Credential source or precedence was incorrect.");
                return Task.FromResult(Json(new { choices = new[] { new { finish_reason = "stop", message = new { content = Plan } } } }));
            });
            using var client = new HttpClient(handler);
            var planner = new CompatiblePlanner(new ProviderSettings { Kind = ProviderKind.Compatible, Model = "fixture-model", Endpoint = "https://openrouter.ai/api/v1/chat/completions", ApiKeyEnvironmentVariable = variable }, client);
            await planner.CreatePlanAsync(Request());
            Environment.SetEnvironmentVariable(variable, "dummy-process-value"); expected = "dummy-process-value";
            await planner.CreatePlanAsync(Request());
            Environment.SetEnvironmentVariable(variable, null);
            Environment.SetEnvironmentVariable(variable, "dummy-updated-user-value", EnvironmentVariableTarget.User); expected = "dummy-updated-user-value";
            await planner.CreatePlanAsync(Request());
            Check(handler.Calls == 3, "User credential changes were not re-read for each request.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, priorProcess);
            Environment.SetEnvironmentVariable(variable, priorUser, EnvironmentVariableTarget.User);
        }
    }
    private static async Task ResponsesImageSetting()
    {
        var root = Path.Combine(Path.GetTempPath(), "Testy-response-images-" + Guid.NewGuid().ToString("N"));
        var variable = "TESTY_DUMMY_IMAGE_KEY_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "dummy-image-key");
        try
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "evidence.png");
            await File.WriteAllBytesAsync(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6hUAAAAASUVORK5CYII="));
            foreach (var supportsImages in new[] { false, true })
            {
                using var handler = new Handler(async request =>
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                    var content = body.RootElement.GetProperty("input")[0].GetProperty("content");
                    Check(content.EnumerateArray().Any(p => p.GetProperty("type").GetString() == "input_image") == supportsImages, "Responses image-sharing setting was ignored.");
                    Check(content[0].GetProperty("text").GetString()!.Contains("id:Status", StringComparison.Ordinal), "Responses lost the UI context when changing image sharing.");
                    return Json(new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = Plan } } } } });
                });
                using var client = new HttpClient(handler);
                var planner = new ResponsesPlanner(new ProviderSettings { Kind = ProviderKind.OpenAI, Model = "fixture-model", ApiKeyEnvironmentVariable = variable, SupportsImages = supportsImages }, client);
                var request = Request(); request.ScreenshotPath = path;
                await planner.CreatePlanAsync(request);
                await planner.ExplainAsync(new RunResult { Steps = [new StepResult { ScreenshotPath = path, Snapshot = request.Snapshot }] });
                Check(handler.Calls == 2 && File.Exists(path), "Image-sharing setting changed local evidence or skipped a provider operation.");
            }
        }
        finally { Environment.SetEnvironmentVariable(variable, null); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static async Task MissingCredential()
    {
        using var handler = new Handler(_ => throw new Exception("HTTP must not be called with missing credentials."));
        using var client = new HttpClient(handler);
        var planner = new ResponsesPlanner(new ProviderSettings { Kind = ProviderKind.OpenAI, Model = "configured-model", ApiKeyEnvironmentVariable = "TESTY_MISSING_" + Guid.NewGuid().ToString("N") }, client);
        await Throws<InvalidOperationException>(() => planner.CreatePlanAsync(Request()));
        Check(handler.Calls == 0, "Made network call before checking credentials.");
    }
    private static async Task InvalidResponse()
    {
        using var handler = new Handler(_ => Task.FromResult(Json(new { choices = new[] { new { finish_reason = "length", message = new { content = Plan } } } })));
        using var client = new HttpClient(handler);
        var planner = new CompatiblePlanner(new ProviderSettings { Kind = ProviderKind.Compatible, Model = "local-model", Endpoint = "http://localhost:1234/v1/chat/completions", ApiKeyEnvironmentVariable = "" }, client);
        await Throws<InvalidDataException>(() => planner.CreatePlanAsync(Request()));
        await Throws<InvalidDataException>(() => Task.FromResult(PlanCodec.Parse(Plan.Replace("assertText", "shell", StringComparison.Ordinal), Request())));
        await Throws<InvalidDataException>(() => Task.FromResult(PlanCodec.Parse(Plan.Replace("assertText", "+1", StringComparison.Ordinal), Request())));
        await Throws<InvalidDataException>(() => { TestValidator.ValidateId("valid-looking\n"); return Task.CompletedTask; });
    }
    private static async Task EndpointBoundary()
    {
        using var handler = new Handler(_ => throw new Exception("Insecure endpoint must not be called."));
        using var client = new HttpClient(handler);
        var planner = new CompatiblePlanner(new ProviderSettings { Kind = ProviderKind.Compatible, Model = "local", Endpoint = "http://example.com/v1/chat/completions", ApiKeyEnvironmentVariable = "" }, client);
        await Throws<InvalidOperationException>(() => planner.CreatePlanAsync(Request()));
        Check(handler.Calls == 0, "Plaintext credential transport was allowed.");
    }
    private static async Task ComputerTools()
    {
        var root = Path.Combine(Path.GetTempPath(), "Testy-computer-tools-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var driver = new ToolDriver();
            Check(LocalComputerTools.ResponsesTools.GetArrayLength() == 2 && LocalComputerTools.CompatibleTools.GetArrayLength() == 2, "Tool schemas missing.");
            var tools = new LocalComputerTools(driver, root, 2);
            var observed = await tools.DispatchAsync("observe_application", "{}");
            Check(observed.Status == "Passed" && File.Exists(observed.ScreenshotPath), "Observation evidence missing.");
            var argument = JsonDocument.Parse(Plan).RootElement.GetProperty("steps")[0].GetRawText();
            var asserted = await tools.DispatchAsync("perform_ui_action", argument);
            Check(asserted.Execution?.Status == RunStatus.Passed, "Function tool assertion failed.");
            await Throws<InvalidOperationException>(() => tools.DispatchAsync("observe_application", "{}"));
            var strict = new LocalComputerTools(driver, root);
            await Throws<InvalidDataException>(() => strict.DispatchAsync("execute_shell", "{}"));
            await Throws<InvalidDataException>(() => strict.DispatchAsync("observe_application", "{\"path\":\"C:/Users\"}"));
            driver.Target = new TargetInfo { ProcessId = 99 };
            await Throws<InvalidOperationException>(() => strict.DispatchAsync("observe_application", "{}"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static async Task OfflineCommands()
    {
        var planner = new OfflinePlanner();
        var request = Request();
        request.Instructions = "assert text id:Status = \"Ready\"\nscreenshot";
        var result = await planner.CreatePlanAsync(request);
        Check(result.Steps.Count == 2 && planner.Name.Contains("no AI", StringComparison.Ordinal), "Offline parser identity or commands incorrect.");
        request.Instructions = "do whatever is needed to fix my app";
        await Throws<InvalidDataException>(() => planner.CreatePlanAsync(request));
    }
    private static async Task ComputerLoop()
    {
        foreach (var kind in new[] { ProviderKind.OpenAI, ProviderKind.Compatible })
        {
            var root = Path.Combine(Path.GetTempPath(), "Testy-agent-loop-" + Guid.NewGuid().ToString("N"));
            var variable = "TESTY_TEST_KEY_" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(variable, "test-only-key");
            try
            {
                var turn = 0;
                using var handler = new Handler(async request =>
                {
                    turn++;
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                    var rootBody = body.RootElement;
                    Check(rootBody.GetProperty("tools").GetArrayLength() == 2, "Custom schemas not supplied to model.");
                    if (turn == 1)
                    {
                        var args = JsonDocument.Parse(Plan).RootElement.GetProperty("steps")[0].GetRawText();
                        return kind == ProviderKind.OpenAI
                            ? Json(new { status = "completed", output = new[] { new { type = "function_call", call_id = "call_1", name = "perform_ui_action", arguments = args } } })
                            : Json(new { choices = new[] { new { finish_reason = "tool_calls", message = new { role = "assistant", content = (string?)null, tool_calls = new[] { new { id = "call_1", type = "function", function = new { name = "perform_ui_action", arguments = args } } } } } } });
                    }
                    var transcript = rootBody.GetProperty(kind == ProviderKind.OpenAI ? "input" : "messages");
                    var hasOutput = transcript.EnumerateArray().Any(item => kind == ProviderKind.OpenAI
                        ? item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output" && item.GetProperty("call_id").GetString() == "call_1"
                        : item.TryGetProperty("role", out var role) && role.GetString() == "tool" && item.GetProperty("tool_call_id").GetString() == "call_1");
                    Check(hasOutput, "Tool output lost original call ID.");
                    Check(transcript.GetRawText().Contains("data:image/png;base64,", StringComparison.Ordinal), "Screenshots missing from model context.");
                    return kind == ProviderKind.OpenAI
                        ? Json(new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = "Status Ready verified." } } } } })
                        : Json(new { choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content = "Status Ready verified." } } } });
                });
                using var client = new HttpClient(handler);
                using var driver = new ToolDriver();
                var agent = new ComputerUseAgent(new ProviderSettings { Kind = kind, Model = "test-model", ApiKeyEnvironmentVariable = variable }, driver, root, client);
                var result = await agent.RunAsync("Verify Ready status.", 4);
                Check(result.Completed && result.Status == RunStatus.Pending && result.HasVerifiedAssertions && result.ModelTurns == 2, "Custom loop did not return assertion evidence for independent verdict: " + result.Message);
                Check(File.Exists(Path.Combine(root, "computer-agent.json")), "Agent evidence summary missing.");
            }
            finally { Environment.SetEnvironmentVariable(variable, null); if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
    private static async Task ComputerLoopBoundaries()
    {
        var root = Path.Combine(Path.GetTempPath(), "Testy-agent-boundary-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var driver = new ToolDriver();
            using var handler = new Handler(_ => Task.FromResult(Json(new { choices = new[] { new { finish_reason = "tool_calls", message = new { role = "assistant", content = (string?)null, tool_calls = new[] { new { id = "call_1", type = "function", function = new { name = "execute_shell", arguments = "{}" } } } } } } })));
            using var client = new HttpClient(handler);
            var settings = new ProviderSettings { Kind = ProviderKind.Compatible, Model = "mock", Endpoint = "http://localhost/v1/chat/completions", ApiKeyEnvironmentVariable = "" };
            var agent = new ComputerUseAgent(settings, driver, Path.Combine(root, "unknown"), client);
            var result = await agent.RunAsync("Inspect app.", 3);
            Check(result.Status == RunStatus.Failed && result.Message.Contains("Unknown computer tool", StringComparison.Ordinal), "Unknown tool not rejected.");
            var bounded = new ComputerUseAgent(settings, driver, Path.Combine(root, "limited"), client);
            var limit = await bounded.RunAsync("Inspect app.", 1);
            Check(limit.Status == RunStatus.Failed && limit.Message.Contains("turn limit", StringComparison.Ordinal), "Turn limit not enforced.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static PlanningRequest Request() => new()
    {
        Instructions = "Verify status is Ready", Snapshot = new UiSnapshot { Target = new TargetInfo { ProcessId = 42, Title = "Fixture" }, Elements = [new UiElementInfo { AutomationId = "Status", Selector = "id:Status", Name = "Ready", Value = "Ready", IsEnabled = true }, new UiElementInfo { AutomationId = "Password", Selector = "id:Password", Value = "private-password", IsPassword = true }] }
    };
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Throws<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; return handle(request); }
    }
    private sealed class ToolDriver : ITargetDriver
    {
        public TargetInfo? Target { get; set; } = new() { ProcessId = 42, Title = "Fixture" };
        public Task<IReadOnlyList<TargetInfo>> GetTargetsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TargetInfo>>([Target!]);
        public Task AttachAsync(int processId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<UiSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(Request().Snapshot);
        public Task ExecuteAsync(TestStep step, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task<string> CaptureAsync(string filePath, CancellationToken cancellationToken = default)
        {
            await File.WriteAllBytesAsync(filePath, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a6hUAAAAASUVORK5CYII="), cancellationToken);
            return filePath;
        }
        public void Dispose() { }
    }
}
