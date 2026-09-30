using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Testy.Core;
using Testy.Windows;

namespace Testy.Cli;

internal sealed class HybridWpfProtocolReport
{
    public string Mode => "Queued Responses protocol with a real owned WPF target; no live OpenAI API. Native screenshot actions only; no physical mouse/keyboard input proof.";
    public string Executable { get; set; } = "";
    public int OwnedProcessId { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public bool Cancelled { get; set; }
    public bool VerificationCompleted { get; set; }
    public RunStatus RunStatus { get; set; } = RunStatus.Pending;
    public int SavedSteps { get; set; }
    public int VerifiedSavedSteps { get; set; }
    public bool FullWorkflowVerified { get; set; }
    public int ProtocolTurns { get; set; }
    public int ModelTurns { get; set; }
    public int NativeScreenshotCalls { get; set; }
    public int SemanticCalls { get; set; }
    public int AssertionCalls { get; set; }
    public int EvidenceImages { get; set; }
    public string RunArtifactDirectory { get; set; } = "";
    public string ProtocolTracePath { get; set; } = "";
    public List<string> Checks { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public bool Passed => FinishedAt is not null && VerificationCompleted && !Cancelled && Errors.Count == 0
        && RunStatus == RunStatus.Passed && SavedSteps == 8 && FullWorkflowVerified && VerifiedSavedSteps == SavedSteps
        && ProtocolTurns == 9 && ModelTurns == ProtocolTurns && NativeScreenshotCalls == 2 && SemanticCalls == 3 && AssertionCalls == 3
        && EvidenceImages == SavedSteps + 1;
}

/// <summary>Real WPF semantic drivers and native screenshot protocol, with deterministic queued responses instead of a live model.</summary>
internal static class HybridWpfProtocolVerifier
{
    public static async Task<HybridWpfProtocolReport> VerifyAsync(string executable, string artifacts, CancellationToken cancellationToken)
    {
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable) || !Path.GetFileName(executable).Equals("Testy.WpfLab.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The hybrid verifier launches only the included Testy.WpfLab.exe fixture.");
        artifacts = Path.GetFullPath(artifacts); Directory.CreateDirectory(artifacts);
        var report = new HybridWpfProtocolReport { Executable = executable, ProtocolTracePath = Path.Combine(artifacts, "protocol-requests.json") };
        var reportPath = Path.Combine(artifacts, "hybrid-wpf-protocol-report.json");
        var keyVariable = "TESTY_HYBRID_FIXTURE_" + Guid.NewGuid().ToString("N");
        Process? fixture = null;
        void Save() => WorkspaceStore.WriteAtomic(reportPath, report);
        Save();
        Environment.SetEnvironmentVariable(keyVariable, "queued-fixture-only-never-sent-to-network");
        try
        {
            fixture = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)!, WindowStyle = ProcessWindowStyle.Hidden
            }) ?? throw new InvalidOperationException("The owned WPF fixture did not start.");
            report.OwnedProcessId = fixture.Id; Save();
            await WaitForWindow(fixture, cancellationToken);
            using var driver = new UiAutomationDriver();
            await driver.AttachAsync(fixture.Id, cancellationToken);
            var initial = await WaitForControls(driver, cancellationToken);
            Check(!initial.Elements.Any(e => e.AutomationId == "Order-1999"), "The far grid row was already realized before the explicit realization step.");
            WorkspaceStore.WriteAtomic(Path.Combine(artifacts, "initial-tree.json"), initial);

            var test = SavedTest(); report.SavedSteps = test.Steps.Count;
            var settings = new ProviderSettings
            {
                Kind = ProviderKind.OpenAI, Model = "queued-native-wpf-protocol", Endpoint = "http://localhost/testy-queued-hybrid/responses",
                ApiKeyEnvironmentVariable = keyVariable, AiDirectedExecution = true, NativeComputerUse = true, SupportsImages = true, MaximumAgentTurns = 12
            };
            AiTestRunner.ValidateExecution(test, settings);
            var turns = test.Steps.Select((step, index) => new QueuedTurn("hybrid-step-" + (index + 1), TestyJson.Clone(step))).ToList();
            RecordingAgent? recording = null;
            using var handler = new QueuedProtocolHandler(turns, report.ProtocolTracePath, () => recording?.Observations ?? []);
            using var client = new HttpClient(handler);
            var runner = new AiTestRunner(driver, settings, Path.Combine(artifacts, "runs"), agentFactory: directory =>
                recording = new RecordingAgent(new NativeComputerUseAgent(settings, driver, directory, new NativeComputerActionExecutor(driver), client, test)));
            var result = await runner.RunAsync(test, cancellationToken: cancellationToken);
            report.RunStatus = result.Status; report.RunArtifactDirectory = result.ArtifactDirectory;
            report.ProtocolTurns = handler.ProtocolCalls; report.ModelTurns = recording?.Result?.ModelTurns ?? 0;
            Check(result.Status == RunStatus.Passed, "The hybrid run did not pass: " + result.Summary);
            var agent = recording?.Result ?? throw new InvalidOperationException("The hybrid adapter returned no recorded agent result.");
            Check(agent.Completed && agent.Status == RunStatus.Pending && handler.AllTurnsConsumed && handler.ProtocolCalls == test.Steps.Count + 1, "Queued protocol did not finish every planned action plus its completion turn.");
            var coverage = SavedWorkflowVerifier.Verify(test, agent.Observations);
            report.FullWorkflowVerified = coverage.Complete; report.VerifiedSavedSteps = coverage.VerifiedSteps;
            Check(coverage.Complete, coverage.Message); report.Checks.Add("Every immutable saved step has exact ordered execution evidence.");

            var native = agent.Observations.Where(o => o.NativeAction is not null).ToArray();
            report.NativeScreenshotCalls = native.Length;
            Check(native.Length == 2 && native.All(o => o.NativeAction!.Value.GetProperty("type").GetString() == "screenshot"), "The integration verifier issued unexpected native input.");
            var semantics = agent.Observations.Where(o => o.ToolName == "perform_saved_control_step").ToArray(); report.SemanticCalls = semantics.Length;
            Check(semantics.Select(o => o.Execution!.Steps.Single().Step.Action).SequenceEqual(new[] { StepAction.RealizeItem, StepAction.ScrollIntoView, StepAction.Expand }), "Semantic tool operations differ from the explicit saved workflow.");
            var assertions = agent.Observations.Where(o => o.ToolName == "verify_saved_assertion").ToArray(); report.AssertionCalls = assertions.Length;
            Check(assertions.Length == 3 && semantics.Concat(assertions).All(o => o.NativeAction is null && o.SavedStepId == o.Execution!.Steps.Single().Step.Id), "Hybrid function observations lost canonical saved IDs or their explicit tool kind.");
            report.Checks.Add("Native screenshot and explicit semantic/assertion tools coexist without physical mouse or keyboard input.");

            var cellResult = result.Steps.Single(s => s.Step.Id == test.Steps[3].Id);
            var cell = UiSelectors.Find(cellResult.Snapshot!, "id:Cell-Order-1999-Key").Single();
            Check(cell.Properties.TryGetValue("uia.value", out var cellValue) && cellValue.Status == UiPropertyStatus.Known && cellValue.Value?.ValueKind == JsonValueKind.String && cellValue.Value.Value.GetString() == "Order-1999", "The far grid cell did not provide its exact typed ValuePattern string.");
            var missing = result.Steps.Single(s => s.Step.Id == test.Steps[6].Id);
            Check(missing.ItemLookup is { Status: ItemLookupStatus.Missing } && missing.ItemLookup.ObservedAt >= missing.StartedAt, "Missing-order assertion lacks a separately timestamped logical lookup result.");
            report.Checks.Add("Realized cell value, expanded tree state and read-only logical absence are independently asserted.");

            Check(result.Steps.Count == test.Steps.Count + 1 && result.Steps.All(s => s.Status == RunStatus.Passed && s.Snapshot is not null && !s.Snapshot.IsTruncated), "Executed steps lack complete control-state evidence.");
            foreach (var step in result.Steps) { ValidateImage(step.ScreenshotPath, step.Snapshot!); report.EvidenceImages++; }
            foreach (var name in new[] { "requested-test.json", "run.json", "computer-agent.json", "report.html", "junit.xml" })
                Check(File.Exists(Path.Combine(result.ArtifactDirectory, name)), "Missing integration artifact: " + name);
            var persisted = JsonSerializer.Deserialize<RunResult>(await File.ReadAllTextAsync(Path.Combine(result.ArtifactDirectory, "run.json"), cancellationToken), TestyJson.Options)!;
            Check(persisted.Status == RunStatus.Passed && persisted.Steps.Count == result.Steps.Count && persisted.Steps.Single(s => s.Step.Id == missing.Step.Id).ItemLookup?.Status == ItemLookupStatus.Missing, "Persisted JSON lost the final result or logical lookup evidence.");
            var junit = XDocument.Load(Path.Combine(result.ArtifactDirectory, "junit.xml")); var suite = junit.Descendants("testsuite").Single();
            Check((int?)suite.Attribute("tests") == result.Steps.Count && (int?)suite.Attribute("failures") == 0 && (int?)suite.Attribute("errors") == 0 && !suite.Descendants("failure").Any() && !suite.Descendants("error").Any() && !suite.Descendants("skipped").Any(), "JUnit did not faithfully report completed successful steps.");
            Check(File.ReadAllText(Path.Combine(result.ArtifactDirectory, "report.html")).Contains(test.Name, StringComparison.Ordinal), "HTML report lacks the actual test name.");
            report.Checks.Add("Every recorded image is decodable, nonblank and scoped to the observed window; JSON, HTML and JUnit are consistent.");
            report.VerificationCompleted = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { report.Cancelled = true; report.RunStatus = RunStatus.Cancelled; report.Errors.Add("Verification cancelled; preserved evidence does not establish completion."); }
        catch (Exception exception)
        { report.Errors.Add(exception.ToString()); }
        finally
        {
            Environment.SetEnvironmentVariable(keyVariable, null);
            if (fixture is not null)
            {
                try { await CloseOwned(fixture); }
                catch (Exception exception) { report.Errors.Add("Owned fixture cleanup: " + exception.Message); }
                fixture.Dispose();
            }
            report.FinishedAt = DateTimeOffset.UtcNow; Save();
        }
        return report;
    }

    private static TestCase SavedTest()
    {
        TestStep Step(string id, string title, StepAction action, string selector = "", string value = "") => new() { Id = id, Title = title, Action = action, Selector = selector, Value = value, TimeoutMs = 10000 };
        string Property(string name, object value) => JsonSerializer.Serialize(new { property = name, equals = value });
        return new TestCase
        {
            Id = "queued-native-wpf-hybrid", Name = "Queued native protocol with real WPF semantic tools", Category = "Integration verification",
            Intent = "Verify explicit hybrid computer protocol against the owned WPF fixture. The queued responses are not a live model and screenshot actions do not prove physical input delivery.",
            Steps =
            [
                Step("initial-screenshot", "Native screenshot before WPF operations", StepAction.Screenshot),
                Step("realize-far-order", "Explicitly realize far grid order", StepAction.RealizeItem, "id:OrdersGrid", "{\"item\":{\"label\":\"Order 1999\"}}"),
                Step("show-far-order", "Explicitly scroll far order into view", StepAction.ScrollIntoView, "id:Order-1999"),
                Step("verify-far-cell", "Verify typed far-cell key", StepAction.AssertProperty, "id:Cell-Order-1999-Key", Property("uia.value", "Order-1999")),
                Step("expand-tree", "Explicitly expand tree root", StepAction.Expand, "id:TreeRoot"),
                Step("verify-expanded-tree", "Verify expanded tree state", StepAction.AssertProperty, "id:TreeRoot", Property("uia.expandCollapseState", "Expanded")),
                Step("verify-missing-order", "Verify missing logical order without realization", StepAction.AssertItemAbsent, "id:OrdersGrid", "{\"item\":{\"label\":\"Order 9999\"}}"),
                Step("final-screenshot", "Native screenshot after WPF operations", StepAction.Screenshot)
            ]
        };
    }

    private sealed record QueuedTurn(string CallId, TestStep Step)
    {
        public string ToolName => Step.Action == StepAction.Screenshot ? "computer" : TestValidator.IsAssertion(Step.Action) ? "verify_saved_assertion" : "perform_saved_control_step";
        public object Response() => Step.Action == StepAction.Screenshot
            ? new { status = "completed", output = new[] { new { type = "computer_call", call_id = CallId, actions = new[] { new { type = "screenshot" } }, pending_safety_checks = Array.Empty<object>() } } }
            : new { status = "completed", output = new[] { new { type = "function_call", call_id = CallId, name = ToolName, arguments = JsonSerializer.Serialize(new { stepId = Step.Id }) } } };
    }

    private sealed class QueuedProtocolHandler(List<QueuedTurn> turns, string tracePath, Func<IReadOnlyList<ComputerToolObservation>> recordedObservations) : HttpMessageHandler
    {
        private readonly List<object> trace = [];
        public int ProtocolCalls { get; private set; }
        public bool AllTurnsConsumed => ProtocolCalls == turns.Count + 1;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)); var root = document.RootElement;
            if (!root.TryGetProperty("tools", out var tools))
            {
                trace.Add(new { failureReview = true, model = "queued response only", actionDispatched = false }); Save();
                return Final("The recorded operation failed. This queued review requests no additional actions and cannot override its result.");
            }
            ProtocolCalls++;
            Check(ProtocolCalls <= turns.Count + 1, "Unexpected extra queued protocol turn.");
            var names = tools.EnumerateArray().Select(t => t.GetProperty("type").GetString() == "computer" ? "computer" : t.GetProperty("name").GetString()).ToArray();
            Check(names.SequenceEqual(new[] { "computer", "verify_saved_assertion", "perform_saved_control_step" }), "Native hybrid tools were not offered together.");
            Check(!root.GetProperty("parallel_tool_calls").GetBoolean(), "Parallel tool calls were enabled.");
            var input = root.GetProperty("input").EnumerateArray().ToArray();
            var returned = input.Where(i => i.TryGetProperty("type", out var type) && type.GetString() is "computer_call_output" or "function_call_output").ToArray();
            Check(returned.Length == ProtocolCalls - 1, "An action's model feedback was missing or duplicated.");
            var recorded = recordedObservations();
            Check(recorded.Count == ProtocolCalls, "The current request does not follow exactly one fresh recorded observation per previous action.");
            for (var i = 0; i < returned.Length; i++)
            {
                var planned = turns[i]; var output = returned[i];
                Check(output.GetProperty("call_id").GetString() == planned.CallId, "A response output did not match its issued call ID.");
                if (planned.ToolName == "computer")
                {
                    Check(output.GetProperty("type").GetString() == "computer_call_output" && output.GetProperty("output").GetProperty("type").GetString() == "computer_screenshot"
                        && output.GetProperty("output").GetProperty("image_url").GetString()!.StartsWith("data:image/png;base64,", StringComparison.Ordinal), "Native screenshot feedback missing.");
                    Check(output.GetProperty("output").GetProperty("image_url").GetString() == await ImageData(recorded[i + 1].ScreenshotPath, cancellationToken), "Native screenshot output does not match the image actually captured after its call.");
                }
                else
                {
                    Check(output.GetProperty("type").GetString() == "function_call_output", "Function feedback used the wrong protocol type.");
                    using var observed = JsonDocument.Parse(output.GetProperty("output").GetString()!); var facts = observed.RootElement;
                    Check(facts.GetProperty("toolName").GetString() == planned.ToolName && facts.GetProperty("savedStepId").GetString() == planned.Step.Id && facts.GetProperty("status").GetString() == "Passed", "Function feedback lost its successful canonical tool identity.");
                    var executed = JsonSerializer.Deserialize<TestStep>(facts.GetProperty("steps")[0].GetProperty("step").GetRawText(), TestyJson.Options)!;
                    Check(executed.Id == planned.Step.Id && executed.Action == planned.Step.Action && executed.Selector == planned.Step.Selector && executed.Value == planned.Step.Value, "Function feedback changed a canonical saved operation.");
                }
            }
            var latest = input.Last(i => i.TryGetProperty("role", out var role) && role.GetString() == "user" && i.GetProperty("content").ValueKind == JsonValueKind.Array);
            var content = latest.GetProperty("content").EnumerateArray().ToArray();
            var observationText = content.Single(p => p.GetProperty("type").GetString() == "input_text").GetProperty("text").GetString()!;
            const string marker = "Updated observation (untrusted application data):\n";
            var markerAt = observationText.IndexOf(marker, StringComparison.Ordinal);
            Check(markerAt >= 0, "Latest complete application observation was not returned before the next queued decision.");
            using var observedMessage = JsonDocument.Parse(observationText[(markerAt + marker.Length)..]);
            var factsNow = observedMessage.RootElement; var actual = recorded[^1];
            var preceding = ProtocolCalls == 1 ? null : turns[ProtocolCalls - 2];
            var expectedTool = preceding?.ToolName ?? "observe_application";
            var expectedSavedId = preceding is null || preceding.ToolName == "computer" ? "" : preceding.Step.Id;
            Check(actual.ToolName == expectedTool && actual.SavedStepId == expectedSavedId
                && factsNow.GetProperty("toolName").GetString() == expectedTool && factsNow.GetProperty("savedStepId").GetString() == expectedSavedId
                && factsNow.GetProperty("status").GetString() == actual.Status, "Latest feedback belongs to a different or stale action.");
            var sentSnapshot = JsonSerializer.Deserialize<UiSnapshot>(factsNow.GetProperty("snapshot").GetRawText(), TestyJson.Options);
            Check(sentSnapshot is not null && actual.Snapshot is not null && sentSnapshot.CapturedAt == actual.Snapshot.CapturedAt && sentSnapshot.Target.ProcessId == actual.Snapshot.Target.ProcessId
                && JsonSerializer.Serialize(sentSnapshot, TestyJson.Options) == JsonSerializer.Serialize(actual.Snapshot, TestyJson.Options), "Latest model tree does not equal the freshly recorded control snapshot.");
            var images = content.Where(p => p.GetProperty("type").GetString() == "input_image").ToArray();
            var imageExpectedInMessage = preceding is null || preceding.ToolName != "computer";
            Check(images.Length == (imageExpectedInMessage ? 1 : 0), "Latest observation has a missing or unexpected image block.");
            if (imageExpectedInMessage) Check(images[0].GetProperty("image_url").GetString() == await ImageData(actual.ScreenshotPath, cancellationToken), "Latest model image does not equal the screenshot recorded after the preceding action.");
            trace.Add(new { turn = ProtocolCalls, queuedOnly = true, tools = names, returnedCallIds = returned.Select(i => i.GetProperty("call_id").GetString()).ToArray(), latestToolName = expectedTool, latestSavedStepId = expectedSavedId,
                latestSnapshotCapturedAt = sentSnapshot!.CapturedAt, latestTreeMatchesRecorded = true, latestImageMatchesRecorded = true,
                nextTool = ProtocolCalls <= turns.Count ? turns[ProtocolCalls - 1].ToolName : "final", nextSavedStepId = ProtocolCalls <= turns.Count ? turns[ProtocolCalls - 1].Step.Id : "" }); Save();
            return ProtocolCalls <= turns.Count ? Json(turns[ProtocolCalls - 1].Response()) : Final("The real WPF saved workflow passed its explicit operations and deterministic assertions. These were queued protocol responses, not live model decisions.");
        }
        private void Save() => WorkspaceStore.WriteAtomic(tracePath, trace);
        private static async Task<string> ImageData(string path, CancellationToken token) => "data:image/png;base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(path, token));
        private static HttpResponseMessage Final(string text) => Json(new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text } } } } });
        private static HttpResponseMessage Json(object response) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") };
    }

    private sealed class RecordingAgent(IComputerAgent inner) : IComputerAgent
    {
        public ComputerAgentResult? Result { get; private set; }
        public List<ComputerToolObservation> Observations { get; } = [];
        public async Task<ComputerAgentResult> RunAsync(string instructions, int maximumTurns = 30, IProgress<ComputerToolObservation>? progress = null, CancellationToken cancellationToken = default)
            => Result = await inner.RunAsync(instructions, maximumTurns, new CapturingProgress(observation =>
            {
                Observations.Add(TestyJson.Clone(observation));
                progress?.Report(observation);
            }), cancellationToken);
        private sealed class CapturingProgress(Action<ComputerToolObservation> capture) : IProgress<ComputerToolObservation>
        { public void Report(ComputerToolObservation value) => capture(value); }
    }
    private static async Task WaitForWindow(Process process, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        while (true) { deadline.Token.ThrowIfCancellationRequested(); process.Refresh(); if (process.HasExited) throw new InvalidOperationException("Owned WPF fixture exited before attachment."); if (process.MainWindowHandle != 0) return; await Task.Delay(100, deadline.Token); }
    }
    private static async Task<UiSnapshot> WaitForControls(ITargetDriver driver, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            var snapshot = await driver.SnapshotAsync(deadline.Token).WaitAsync(deadline.Token);
            if (!snapshot.IsTruncated && UiSelectors.Find(snapshot, "id:OrdersGrid").Count == 1 && UiSelectors.Find(snapshot, "id:TreeRoot").Count == 1) return snapshot;
            await Task.Delay(100, deadline.Token);
        }
    }
    private static async Task CloseOwned(Process process)
    {
        if (process.HasExited) return; process.CloseMainWindow(); using var deadline = new CancellationTokenSource(4000);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) { process.Kill(entireProcessTree: false); await process.WaitForExitAsync(); } }
    }
    private static void ValidateImage(string path, UiSnapshot snapshot)
    {
        Check(File.Exists(path), "Screenshot file is missing."); using var stream = File.OpenRead(path);
        var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        Check(frame.PixelWidth >= 300 && frame.PixelHeight >= 160 && snapshot.Elements.Any(e => e.Depth == 0 && e.ControlType == "Window" && Math.Abs(e.Bounds.Width - frame.PixelWidth) < 2 && Math.Abs(e.Bounds.Height - frame.PixelHeight) < 2), "Screenshot dimensions do not match the observed full fixture window.");
        var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0); var stride = bitmap.PixelWidth * 4; var pixels = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, stride, 0);
        var colors = new HashSet<int>(); var samples = 0; var visible = 0;
        for (var y = bitmap.PixelHeight * 15 / 100; y < bitmap.PixelHeight * 9 / 10; y += 7)
            for (var x = bitmap.PixelWidth / 10; x < bitmap.PixelWidth * 9 / 10; x += 7)
            { var p = y * stride + x * 4; samples++; if (Math.Max(pixels[p], Math.Max(pixels[p + 1], pixels[p + 2])) > 32) visible++; colors.Add(pixels[p] | pixels[p + 1] << 8 | pixels[p + 2] << 16); }
        Check(visible > samples / 2 && colors.Count >= 8, "Screenshot is blank or lacks visible fixture detail.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
