using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using Testy.Core;
using Testy.Windows;

namespace Testy.Cli;

/// <summary>Real desktop and real protocol adapter with queued model responses; no API call or paid model is used.</summary>
internal static class NativeProtocolVerifier
{
    public static async Task<VerificationReport> VerifyAsync(string executable, string artifacts, CancellationToken cancellationToken)
    {
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable) || Path.GetFileName(executable) != "Testy.TestLab.exe") throw new ArgumentException("The native verifier launches only the included Testy.TestLab.exe.");
        Directory.CreateDirectory(artifacts);
        var report = new VerificationReport { Executable = executable, PlannedChecks = 1 };
        using var fixture = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(executable)! })
            ?? throw new InvalidOperationException("The native fixture failed to start.");
        report.OwnedProcessId = fixture.Id;
        var variable = "TESTY_NATIVE_PROTOCOL_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "mock-only-never-sent");
        try
        {
            var timer = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested(); fixture.Refresh();
                if (fixture.HasExited) throw new InvalidOperationException("The native fixture exited.");
                if (fixture.MainWindowHandle != 0) break;
                if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("The native fixture did not open.");
                await Task.Delay(100, cancellationToken);
            }
            // A window handle can appear before the initial Windows activation message is processed.
            await Task.Delay(900, cancellationToken);
            using var driver = new UiAutomationDriver();
            await driver.AttachAsync(fixture.Id, cancellationToken);
            // Initialize and activate this owned fixture through its accessibility provider before physical input.
            // The native loop still performs and records its own reset as the first required action.
            await driver.ExecuteAsync(new TestStep { Title = "Fixture setup", Action = StepAction.Click, Selector = "id:ResetButton" }, cancellationToken);
            var fixtureWindow = AutomationElement.FromHandle(fixture.MainWindowHandle);
            var fixtureNameField = fixtureWindow.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, "CustomerName"),
                new PropertyCondition(AutomationElement.ProcessIdProperty, fixture.Id))) ?? throw new InvalidOperationException("The owned fixture name field was not found for focus setup.");
            fixtureNameField.SetFocus();
            await Task.Delay(250, cancellationToken);
            var snapshot = await driver.SnapshotAsync(cancellationToken);
            var window = snapshot.Elements.Single(e => e.AutomationId == "CustomerDeskWindow");
            object Click(string id)
            {
                var element = snapshot.Elements.Single(e => e.AutomationId == id);
                return new { type = "click", x = (int)Math.Round(element.Bounds.X - window.Bounds.X + element.Bounds.Width / 2), y = (int)Math.Round(element.Bounds.Y - window.Bounds.Y + element.Bounds.Height / 2), button = "left" };
            }
            object Computer(int id, object action) => new { status = "completed", output = new[] { new { type = "computer_call", call_id = "native-" + id, actions = new[] { action }, pending_safety_checks = Array.Empty<object>() } } };
            var assertion = new TestStep { Title = "Verify real native creation", Action = StepAction.AssertText, Selector = "id:StatusMessage", Value = "Customer added: Protocol QA" };
            var assertionArguments = JsonSerializer.Serialize(new { title = assertion.Title, action = "assertText", selector = assertion.Selector, value = assertion.Value, timeoutMs = 5000, x = 0, y = 0 });
            var queue = new Queue<object>(new object[]
            {
                Computer(1, Click("ResetButton")), Computer(2, Click("CustomerName")),
                Computer(3, new { type = "type", text = "Protocol QA" }), Computer(4, Click("CustomerEmail")),
                Computer(5, new { type = "type", text = "protocol@example.test" }), Computer(6, Click("AddCustomer")),
                new { status = "completed", output = new[] { new { type = "function_call", name = "verify_ui_assertion", call_id = "native-assert", arguments = assertionArguments } } },
                new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = "The real native input created Protocol QA and the exact required assertion passed." } } } } }
            });
            using var handler = new ProtocolHandler(queue);
            using var client = new HttpClient(handler);
            var settings = new ProviderSettings { Kind = ProviderKind.OpenAI, Model = "mock-native-protocol", ApiKeyEnvironmentVariable = variable, MaximumAgentTurns = 12, NativeComputerUse = true };
            var test = new TestCase { Name = "Native protocol with real Windows input", Intent = "Create Protocol QA using the native computer loop and verify the exact success message.", Steps =
            [
                new() { Title = "Reset fixture", Action = StepAction.Click, Selector = "id:ResetButton" },
                new() { Title = "Enter name", Action = StepAction.TypeText, Selector = "id:CustomerName", Value = "Protocol QA" },
                new() { Title = "Enter email", Action = StepAction.TypeText, Selector = "id:CustomerEmail", Value = "protocol@example.test" },
                new() { Title = "Add customer", Action = StepAction.Click, Selector = "id:AddCustomer" }, assertion
            ] };
            var result = await new AiTestRunner(driver, settings, artifacts, agentFactory: path => new NativeComputerUseAgent(settings, driver, path, new NativeComputerActionExecutor(driver), client)).RunAsync(test, null, cancellationToken);
            var valid = result.Status == RunStatus.Passed && handler.Calls == 8 && queue.Count == 0 && result.Steps.All(s => s.Snapshot is not null && File.Exists(s.ScreenshotPath));
            report.Checks.Add(new VerificationCheck { Name = "Queued native computer responses drive the real Lab", Driver = "Mock model / real native input", Passed = valid, Expected = RunStatus.Passed, Actual = result.Status, Message = result.Summary + $" {handler.Calls} response turns; {result.Steps.Count} observations with evidence.", RunDirectory = result.ArtifactDirectory });
            await File.WriteAllTextAsync(Path.Combine(artifacts, "protocol-requests.json"), JsonSerializer.Serialize(handler.Requests, TestyJson.Options), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            report.CompletedAllScenarios = report.Checks.Count == report.PlannedChecks;
        }
        catch (Exception exception)
        {
            report.Checks.Add(new VerificationCheck { Name = "Native protocol integration", Driver = "Mock model / real native input", Passed = false, Expected = RunStatus.Passed, Actual = RunStatus.Failed, Message = exception.ToString() });
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            if (!fixture.HasExited)
            {
                fixture.CloseMainWindow();
                using var timeout = new CancellationTokenSource(4000);
                try { await fixture.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { if (!fixture.HasExited) fixture.Kill(); }
            }
            report.Finish(cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(artifacts, "native-protocol-report.json"), JsonSerializer.Serialize(report, TestyJson.Options), CancellationToken.None);
        }
        return report;
    }

    private sealed class ProtocolHandler(Queue<object> queue) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<object> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            if (!root.TryGetProperty("tools", out _))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = "The real native action failed as recorded. No additional input was requested by the mock failure reviewer." } } } } }), Encoding.UTF8, "application/json") };
            if (root.GetProperty("tools")[0].GetProperty("type").GetString() != "computer") throw new InvalidDataException("Native computer tool missing from protocol.");
            if (root.GetProperty("parallel_tool_calls").GetBoolean()) throw new InvalidDataException("Parallel calls would bypass per-action feedback.");
            var inputs = root.GetProperty("input").EnumerateArray().ToArray();
            var outputs = inputs.Count(item => item.TryGetProperty("type", out var type) && type.GetString() == "computer_call_output");
            if (Calls > 1 && outputs != Math.Min(Calls - 1, 6)) throw new InvalidDataException("Native screenshot outputs were not fed back after every action.");
            Requests.Add(new { turn = Calls, inputItems = inputs.Length, computerScreenshotOutputs = outputs, nativeToolPresent = true, parallelToolCalls = false });
            if (queue.Count == 0) throw new InvalidDataException("The model requested an unexpected extra turn.");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(queue.Dequeue()), Encoding.UTF8, "application/json") };
        }
    }
}
