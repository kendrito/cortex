using System.Diagnostics;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Testy.Core;
using Testy.Windows;

namespace Testy.Cli;

internal sealed class TextBoundaryReport
{
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public int PlannedChecks => 7;
    public bool CompletedAllScenarios { get; set; }
    public bool Cancelled { get; set; }
    public string Mode => "Real WPF text boundaries through UIA and opt-in probe; explicit replay, no model calls";
    public bool Passed => FinishedAt is not null && CompletedAllScenarios && !Cancelled && Checks.Count == PlannedChecks && Checks.All(c => c.Passed);
    public List<TextBoundaryCheck> Checks { get; set; } = [];
}

internal sealed class TextBoundaryCheck
{
    public string Driver { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Passed { get; set; }
    public RunStatus Expected { get; set; }
    public RunStatus Actual { get; set; }
    public int ObservedValueLength { get; set; }
    public bool IsValueTruncated { get; set; }
    public UiPropertyStatus? TypedValueStatus { get; set; }
    public double DurationMs { get; set; }
    public int EvidenceImages { get; set; }
    public string ArtifactDirectory { get; set; } = "";
    public string Message { get; set; } = "";
}

internal static class TextBoundaryVerifier
{
    public static async Task<TextBoundaryReport> VerifyAsync(string executable, string artifacts, CancellationToken cancellationToken)
    {
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable) || Path.GetFileName(executable) != "Testy.WpfLab.exe")
            throw new ArgumentException("Only the included Testy.WpfLab.exe fixture is allowed.");
        artifacts = Path.GetFullPath(artifacts); Directory.CreateDirectory(artifacts);
        var report = new TextBoundaryReport();
        void Save() => WorkspaceStore.WriteAtomic(Path.Combine(artifacts, "text-boundary-report.json"), report);
        Save();
        try
        {
            foreach (bool probe in new[] { false, true })
            {
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(executable)! };
                start.ArgumentList.Add("--text-boundary");
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Owned WPF Lab did not start.");
                try
                {
                    var timer = Stopwatch.StartNew();
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested(); process.Refresh();
                        if (process.HasExited) throw new InvalidOperationException("Owned WPF Lab exited before its window appeared.");
                        if (process.MainWindowHandle != 0) break;
                        if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Owned WPF Lab window did not appear.");
                        await Task.Delay(100, cancellationToken);
                    }
                    using ITargetDriver driver = probe ? new WpfProbeDriver() : new UiAutomationDriver();
                    await driver.AttachAsync(process.Id, cancellationToken);
                    foreach (var kind in probe ? new[] { 0, 1, 2 } : new[] { 0, 1, 2, 3 })
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string id = kind == 3 ? "RichTextBoundary" : "ValidatedQuantity";
                        string content = kind switch { 0 => new string('A', 8192) + "Z", 1 => new string('B', 8192), 2 => new string('C', 3000), _ => new string('R', 8193) };
                        string title = kind switch { 0 => "8193-character value cannot pass its 8192-character prefix", 1 => "Exact 8192-character legacy value remains assertable", 2 => "3000-character legacy text passes while typed value is truncated", _ => "TextPattern lookahead rejects an 8192-character prefix" };
                        var check = new TextBoundaryCheck { Driver = probe ? "WPF in-process probe" : "Windows UI Automation", Name = title, Expected = kind == 1 ? RunStatus.Passed : RunStatus.Failed };
                        var watch = Stopwatch.StartNew();
                        try
                        {
                            var steps = new List<TestStep> { Step(StepAction.Click, "ResetLab"), Step(StepAction.AssertText, "FixtureState", "selections=0; viewports=0; trees=0; actions=0") };
                            if (kind == 3) steps.Add(Step(StepAction.ScrollPercent, "LongDocument", "{\"vertical\":100}"));
                            else steps.Add(Step(StepAction.TypeText, id, content));
                            steps.Add(Step(StepAction.AssertText, id, kind is 0 or 3 ? content[..8192] : content));
                            if (kind == 2) steps.Add(Step(StepAction.AssertProperty, id, JsonSerializer.Serialize(new { property = "uia.value", equals = content })));
                            if (kind != 1) steps.Add(Step(StepAction.Click, "TemplatedAction"));
                            var test = new TestCase { Name = title, Intent = title, Category = "Text observation boundaries", Steps = steps };
                            var result = await new TestRunner(driver, Path.Combine(artifacts, probe ? "probe" : "uia")).RunAsync(test, null, cancellationToken);
                            check.Actual = result.Status; check.ArtifactDirectory = result.ArtifactDirectory;
                            ValidateRecords(result, test, process.Id);
                            Check(result.Status == check.Expected, $"Expected {check.Expected}; got {result.Status}: {result.Summary}");
                            int failedIndex = kind == 2 ? 4 : 3;
                            if (kind != 1)
                            {
                                Check(result.Steps.Take(failedIndex).All(s => s.Status == RunStatus.Passed), "A setup or earlier valid assertion failed instead of the boundary assertion.");
                                var failure = result.Steps[failedIndex];
                                Check(failure.Status == RunStatus.Failed && failure.FailureDiagnostics.Any(d => d.Category == FailureCategory.EvidenceUnavailable), "Incomplete text did not fail as EvidenceUnavailable.");
                                Check(result.Steps.Skip(failedIndex + 1).All(s => s.Status == RunStatus.Skipped), "A later mutation was not skipped.");
                            }
                            else Check(result.Steps.All(s => s.Status == RunStatus.Passed), "The complete boundary text did not pass all exact steps.");
                            var observed = result.Steps[kind == 2 ? 4 : 3].Snapshot ?? throw new InvalidOperationException("Boundary assertion lacks its observed tree.");
                            var value = UiSelectors.Find(observed, "id:" + id).Single();
                            check.ObservedValueLength = value.Value.Length; check.IsValueTruncated = value.IsValueTruncated;
                            check.TypedValueStatus = value.Properties.GetValueOrDefault("uia.value")?.Status;
                            Check(value.Value.Length == (kind == 2 ? 3000 : 8192), "The observed legacy text length does not match its documented limit.");
                            Check(value.IsValueTruncated == (kind is 0 or 3), "Legacy truncation metadata is incorrect.");
                            if (kind != 3) Check(check.TypedValueStatus == UiPropertyStatus.Truncated, "The independent 2048-character typed-value bound was not reported as Truncated.");
                            var after = await driver.SnapshotAsync(cancellationToken);
                            WorkspaceStore.WriteAtomic(Path.Combine(result.ArtifactDirectory, "boundary-after.json"), after);
                            Check(UiSelectors.Find(after, "id:FixtureStatus").Single().Name == "Reset complete", "A skipped action changed the fixture status.");
                            Check(UiSelectors.Find(after, "id:FixtureState").Single().Name.EndsWith("actions=0", StringComparison.Ordinal), "A skipped action incremented the fixture action counter.");
                            foreach (var step in result.Steps.Where(s => s.Status is RunStatus.Passed or RunStatus.Failed))
                            {
                                Check(step.Snapshot is not null, "Executed step is missing a tree."); ValidateImage(step.ScreenshotPath); check.EvidenceImages++;
                            }
                            foreach (string file in new[] { "run.json", "report.html", "junit.xml" }) Check(File.Exists(Path.Combine(result.ArtifactDirectory, file)), "Missing report: " + file);
                            check.Message = result.Summary; check.Passed = true;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { check.Message = ex.ToString(); }
                        finally { watch.Stop(); check.DurationMs = watch.Elapsed.TotalMilliseconds; report.Checks.Add(check); Save(); }
                    }
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.CloseMainWindow(); using var timeout = new CancellationTokenSource(4000);
                        try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: false); }
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested(); report.CompletedAllScenarios = report.Checks.Count == report.PlannedChecks;
        }
        catch (OperationCanceledException) { report.Cancelled = true; throw; }
        finally { report.FinishedAt = DateTimeOffset.UtcNow; Save(); }
        return report;
    }

    private static TestStep Step(StepAction action, string id, string value = "") => new() { Title = action + " " + id, Action = action, Selector = "id:" + id, Value = value, TimeoutMs = 8000 };
    // Kept independent of desktop setup so offline corruption fixtures exercise this exact acceptance gate.
    internal static void ValidateRecords(RunResult result, TestCase test, int expectedProcessId)
    {
        Check(result.FinishedAt is not null && result.FinishedAt >= result.StartedAt, "Run is not finalized with a valid terminal timestamp.");
        Check(result.Target.ProcessId == expectedProcessId && result.TestId == test.Id, "Run does not belong to the owned process and requested test.");
        Check(result.Steps.Count == test.Steps.Count, "Run does not contain exactly every planned step, including skipped steps.");
        for (int index = 0; index < test.Steps.Count; index++)
        {
            var actual = result.Steps[index]; var expected = test.Steps[index];
            Check(actual.Index == index && actual.Step.Id == expected.Id && actual.Step.Action == expected.Action
                && actual.Step.Selector == expected.Selector && actual.Step.Value == expected.Value && actual.Step.TimeoutMs == expected.TimeoutMs,
                $"Step record {index} is not the canonical saved step at that position.");
            Check(actual.Snapshot is null || actual.Snapshot.Target.ProcessId == expectedProcessId, $"Step {index} contains evidence from another process.");
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void ValidateImage(string path)
    {
        Check(File.Exists(path), "Screenshot missing."); using var stream = File.OpenRead(path);
        var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        Check(frame.PixelWidth >= 800 && frame.PixelHeight >= 600, "Text-boundary evidence is not a full fixture window.");
        var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0); int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, stride, 0); int total = 0, lit = 0; var colors = new HashSet<int>();
        for (int y = bitmap.PixelHeight / 5; y < bitmap.PixelHeight * 9 / 10; y += 9)
            for (int x = bitmap.PixelWidth / 10; x < bitmap.PixelWidth * 9 / 10; x += 9)
            { int p = y * stride + x * 4; total++; if (Math.Max(pixels[p], Math.Max(pixels[p + 1], pixels[p + 2])) > 32) lit++; colors.Add(pixels[p] | pixels[p + 1] << 8 | pixels[p + 2] << 16); }
        Check(lit > total / 2 && colors.Count >= 8, "Screenshot client is blank or lacks fixture detail.");
    }
}
