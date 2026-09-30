using System.Diagnostics;
using System.Text.Json;
using Testy.Core;
using Testy.Windows;

namespace Testy.Cli;

internal sealed class VerificationReport
{
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public int PlannedChecks { get; set; }
    public bool CompletedAllScenarios { get; set; }
    public bool Cancelled { get; set; }
    public bool Passed => FinishedAt is not null && CompletedAllScenarios && !Cancelled && PlannedChecks > 0 && Checks.Count == PlannedChecks && Checks.All(x => x.Passed);
    public string Executable { get; set; } = "";
    public int OwnedProcessId { get; set; }
    public List<VerificationCheck> Checks { get; set; } = [];
    public void Finish(CancellationToken cancellationToken)
    {
        Cancelled |= cancellationToken.IsCancellationRequested;
        FinishedAt = DateTimeOffset.UtcNow;
    }
}

internal sealed class VerificationCheck
{
    public string Name { get; set; } = "";
    public string Driver { get; set; } = "";
    public bool Passed { get; set; }
    public RunStatus Expected { get; set; }
    public RunStatus Actual { get; set; }
    public string Message { get; set; } = "";
    public string RunDirectory { get; set; } = "";
}

internal static class LabVerifier
{
    public static async Task<VerificationReport> VerifyAsync(string executable, string artifacts, CancellationToken cancellationToken)
    {
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("TestLab executable was not found.", executable);
        if (!string.Equals(Path.GetFileName(executable), "Testy.TestLab.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("verify-lab launches only the included Testy.TestLab.exe fixture.");
        Directory.CreateDirectory(artifacts);
        var report = new VerificationReport { Executable = executable, PlannedChecks = 20 };
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        }) ?? throw new InvalidOperationException("The TestLab process did not start.");
        report.OwnedProcessId = process.Id;
        try
        {
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(20))
            {
                cancellationToken.ThrowIfCancellationRequested();
                process.Refresh();
                if (process.HasExited) throw new InvalidOperationException($"TestLab exited with code {process.ExitCode} before its window appeared.");
                if (process.MainWindowHandle != 0) break;
                await Task.Delay(150, cancellationToken);
            }
            process.Refresh();
            if (process.MainWindowHandle == 0) throw new TimeoutException("TestLab did not create a window within 20 seconds.");
            foreach (var probe in new[] { false, true })
            {
                using ITargetDriver driver = probe ? new WpfProbeDriver() : new UiAutomationDriver();
                var driverName = probe ? "WPF in-process probe" : "Windows UI Automation";
                try
                {
                    await driver.AttachAsync(process.Id, cancellationToken);
                    var snapshot = await driver.SnapshotAsync(cancellationToken);
                    var requiredIds = new[] { "CustomerName", "CustomerEmail", "AddCustomer", "StatusMessage", "CustomerList", "SearchBox", "ResultCount", "ResetButton", "DelayedButton", "DefectToggle" };
                    var absent = requiredIds.Where(id => snapshot.Elements.All(e => e.AutomationId != id)).ToArray();
                    report.Checks.Add(new VerificationCheck
                    {
                        Name = "Inspect required controls", Driver = driverName, Passed = absent.Length == 0,
                        Expected = RunStatus.Passed, Actual = absent.Length == 0 ? RunStatus.Passed : RunStatus.Failed,
                        Message = absent.Length == 0 ? $"Found all {requiredIds.Length} controls in {snapshot.Elements.Count} elements." : "Missing: " + string.Join(", ", absent)
                    });
                    await File.WriteAllTextAsync(Path.Combine(artifacts, probe ? "probe-snapshot.json" : "uia-snapshot.json"), JsonSerializer.Serialize(snapshot, TestyJson.Options), cancellationToken);
                    foreach (var scenario in Scenarios())
                    {
                        var test = scenario.Test;
                        test.TargetPath = executable;
                        var result = await new TestRunner(driver, Path.Combine(artifacts, probe ? "probe" : "uia")).RunAsync(test, null, cancellationToken);
                        var evidenceErrors = new List<string>();
                        foreach (var step in result.Steps.Where(x => x.Status is RunStatus.Passed or RunStatus.Failed))
                        {
                            if (step.Snapshot is null) evidenceErrors.Add($"Step {step.Index} lacks a snapshot.");
                            if (!File.Exists(step.ScreenshotPath)) evidenceErrors.Add($"Step {step.Index} lacks a screenshot.");
                            else
                            {
                                var header = new byte[8];
                                using var stream = File.OpenRead(step.ScreenshotPath);
                                var count = await stream.ReadAsync(header, cancellationToken);
                                if (count != 8 || !header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                                    evidenceErrors.Add($"Step {step.Index} screenshot is not PNG.");
                            }
                        }
                        if (!Directory.EnumerateFiles(result.ArtifactDirectory, "*.json").Any()) evidenceErrors.Add("Run JSON is absent.");
                        if (!Directory.EnumerateFiles(result.ArtifactDirectory, "*.html").Any()) evidenceErrors.Add("Run HTML is absent.");
                        var passed = result.Status == scenario.Expected && evidenceErrors.Count == 0;
                        if (scenario.Expected == RunStatus.Failed && !result.Steps.Any(x => x.Status == RunStatus.Failed && x.Message.Length > 0))
                        {
                            passed = false;
                            evidenceErrors.Add("Expected failure did not contain a step explanation.");
                        }
                        report.Checks.Add(new VerificationCheck
                        {
                            Name = test.Name, Driver = driverName, Passed = passed, Expected = scenario.Expected,
                            Actual = result.Status, Message = result.Summary + (evidenceErrors.Count == 0 ? "" : " " + string.Join(" ", evidenceErrors)),
                            RunDirectory = result.ArtifactDirectory
                        });
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    report.Checks.Add(new VerificationCheck { Name = "Driver integration", Driver = driverName, Passed = false, Actual = RunStatus.Failed, Expected = RunStatus.Passed, Message = exception.ToString() });
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            report.CompletedAllScenarios = report.Checks.Count == report.PlannedChecks;
        }
        catch (OperationCanceledException) { report.Cancelled = true; throw; }
        finally
        {
            // This process was created above exclusively for this verifier. Never close discovered targets.
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                try { await process.WaitForExitAsync(exitTimeout.Token); }
                catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: false); }
            }
            report.Finish(cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(artifacts, "integration-report.json"), JsonSerializer.Serialize(report, TestyJson.Options), CancellationToken.None);
        }
        return report;
    }

    private static IEnumerable<(TestCase Test, RunStatus Expected)> Scenarios()
    {
        yield return (Case("Create a customer", Click("ResetButton"), Type("CustomerName", "Ada Lovelace"), Type("CustomerEmail", "ada@example.test"),
            Click("AddCustomer"), Assert("StatusMessage", "Customer added: Ada Lovelace"), Exists("CustomerList")), RunStatus.Passed);
        yield return (Case("Required name validation", Click("ResetButton"), Click("AddCustomer"), Assert("StatusMessage", "Enter a customer name.")), RunStatus.Passed);
        yield return (Case("Invalid email validation", Click("ResetButton"), Type("CustomerName", "Grace Hopper"), Type("CustomerEmail", "invalid-email"),
            Click("AddCustomer"), Assert("StatusMessage", "Enter a valid email address.")), RunStatus.Passed);
        yield return (Case("Search filters customer records", Click("ResetButton"), Type("CustomerName", "Ada Lovelace"), Type("CustomerEmail", "ada@example.test"), Click("AddCustomer"),
            Type("CustomerName", "Grace Hopper"), Type("CustomerEmail", "grace@example.test"), Click("AddCustomer"), Assert("ResultCount", "2 customers"),
            Type("SearchBox", "Ada"), Assert("ResultCount", "1 customer"), Type("SearchBox", "no-match"), Assert("ResultCount", "0 customers"),
            Type("SearchBox", ""), Assert("ResultCount", "2 customers")), RunStatus.Passed);
        yield return (Case("Reset clears text fields", Type("CustomerName", "Temporary"), Type("SearchBox", "filter"), Click("ResetButton"),
            Assert("CustomerName", ""), Assert("SearchBox", ""), Assert("StatusMessage", "Ready for a new customer.")), RunStatus.Passed);
        yield return (Case("Async state is awaited", Click("ResetButton"), Click("DelayedButton"), Assert("StatusMessage", "Background check complete.", 4000)), RunStatus.Passed);
        yield return (Case("Missing selector produces a diagnostic", new TestStep { Title = "Find a deliberately absent control", Action = StepAction.Click, Selector = "id:DeliberatelyMissing", TimeoutMs = 400 }), RunStatus.Failed);
        yield return (Case("Intentional product defect is detected", Click("ResetButton"), Type("CustomerName", "Defect Check"), Type("CustomerEmail", "defect@example.test"),
            new TestStep { Title = "Enable deliberate fixture defect", Action = StepAction.Toggle, Selector = "id:DefectToggle", Value = "true" }, Click("AddCustomer"),
            Assert("StatusMessage", "Customer added: Defect Check", 400)), RunStatus.Failed);
        yield return (Case("Disable fixture defect", new TestStep { Title = "Disable deliberate fixture defect", Action = StepAction.Toggle, Selector = "id:DefectToggle", Value = "false" }, Click("ResetButton"), Assert("StatusMessage", "Ready for a new customer.")), RunStatus.Passed);
    }

    private static TestCase Case(string name, params TestStep[] steps) => new() { Name = name, Category = "Integration verification", Intent = name, Steps = steps.ToList() };
    private static TestStep Click(string id) => new() { Title = "Click " + id, Action = StepAction.Click, Selector = "id:" + id };
    private static TestStep Type(string id, string value) => new() { Title = "Enter " + id, Action = StepAction.TypeText, Selector = "id:" + id, Value = value };
    private static TestStep Assert(string id, string value, int timeout = 3000) => new() { Title = "Verify " + id, Action = StepAction.AssertText, Selector = "id:" + id, Value = value, TimeoutMs = timeout };
    private static TestStep Exists(string id) => new() { Title = "Verify " + id + " exists", Action = StepAction.AssertExists, Selector = "id:" + id };
}
