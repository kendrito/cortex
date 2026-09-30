using System.Text.Json;
using Testy.Core;

namespace Testy.Tests;

internal static class AdvancedCoreChecks
{
    public static IEnumerable<(string Name, Func<Task> Execute)> All()
    {
        yield return ("advanced value contracts reject malformed ambiguous and coercible payloads", StrictValues);
        yield return ("advanced actions preserve legacy enum and JSON contracts across schemas", SchemaCompatibility);
        yield return ("typed property assertions inspect disabled offscreen controls without input", ReadOnlyProperties);
        yield return ("typed UIA value assertions distinguish empty cell values from accessible names", TypedValue);
        yield return ("truncated legacy text never proves exact or substring assertions", TruncatedLegacyText);
        yield return ("complete legacy text remains independent of bounded typed properties", CompleteLegacyText);
        yield return ("property diagnostics preserve scalar types and distinguish unavailable evidence", PropertyFailures);
        yield return ("password and unknown property observations never leak values into evidence", PropertyRedaction);
        yield return ("logical item assertions persist read-only lookup evidence despite virtualization", ItemAssertions);
        yield return ("unsupported ambiguous and unknown item lookups never prove absence", ItemFailureBoundaries);
        yield return ("realized-only trees cannot prove generic selector absence", VirtualizedAbsence);
        yield return ("advanced mutation capability gates prevent input and dispatch supported actions once", CapabilityGates);
        yield return ("ignored read-only item lookup cancellation remains bounded without mutation", LookupDeadline);
        yield return ("item lookup deadlines preserve earlier completed mismatch as supporting evidence", LookupDeadlineFacts);
    }
    private static Task StrictValues()
    {
        foreach (var invalid in new[] { "{}", "{\"item\":{}}", "{\"item\":{\"id\":\"A\",\"label\":\"B\"}}", "{\"item\":{\"index\":1}}", "{\"item\":{\"id\":null}}", "{\"item\":{\"id\":\"A\",\"id\":\"B\"}}", "{\"item\":{\"id\":\"A\"},\"extra\":false}" }) Reject(() => AdvancedSteps.ParseItem(invalid));
        foreach (var invalid in new[] { "{}", "{\"vertical\":-1}", "{\"horizontal\":101}", "{\"vertical\":\"20\"}", "{\"vertical\":1e999}", "{\"vertical\":1,\"vertical\":2}", "{\"x\":30}" }) Reject(() => AdvancedSteps.ParseScrollPercent(invalid));
        foreach (var invalid in new[] { "{\"property\":\"DataContext.Secret\",\"equals\":false}", "{\"property\":\"wpf.hasItems\"}", "{\"property\":\"wpf.hasItems\",\"equals\":[]}", "{\"property\":\"wpf.hasItems\",\"equals\":false,\"equals\":true}", "{\"property\":\"uia.row\",\"equals\":1e999}" }) Reject(() => AdvancedSteps.ParseProperty(invalid));
        foreach (var action in new[] { StepAction.Expand, StepAction.Collapse, StepAction.ScrollIntoView }) Reject(() => TestValidator.ValidateStep(AdvancedFixture.Step(action, "unexpected")));
        Check(AdvancedSteps.ParseItem("{\"item\":{\"id\":\"A\"}}").Id == "A", "Exact item ID lost.");
        Check(AdvancedSteps.ParseScrollPercent("{\"vertical\":0,\"horizontal\":100}") == new ScrollPercentValue(0, 100), "Percentage endpoints rejected.");
        Check(!AdvancedSteps.ScalarEquals(JsonSerializer.SerializeToElement(false), JsonSerializer.SerializeToElement("false")), "Property equality coerced types.");
        return Task.CompletedTask;
    }
    private static Task SchemaCompatibility()
    {
        Check((int)StepAction.CoordinateClick == 11 && (int)StepAction.Expand == 12, "Existing enum identities moved.");
        var legacy = JsonSerializer.Deserialize<UiElementInfo>("{\"selector\":\"id:Legacy\"}", TestyJson.Options)!;
        Check(legacy.ChildCoverage == ChildCoverage.Complete && legacy.Properties.Count == 0 && legacy.Capabilities.Count == 0, "Legacy snapshot defaults changed.");
        var planActions = PlanCodec.Schema.GetProperty("properties").GetProperty("steps").GetProperty("items").GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray();
        var toolActions = LocalComputerTools.ResponsesTools[1].GetProperty("parameters").GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray();
        foreach (var name in new[] { "expand", "collapse", "realizeItem", "scrollIntoView", "scrollPercent", "assertProperty", "assertItemExists", "assertItemAbsent" }) Check(planActions.Contains(name) && toolActions.Contains(name), "Schema omitted " + name);
        var nativeActions = NativeComputerUseAgent.NativeTools[1].GetProperty("parameters").GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray();
        Check(nativeActions.Contains("assertProperty") && nativeActions.Contains("assertItemAbsent") && !nativeActions.Contains("realizeItem"), "Native assertion schema allows mutation or loses advanced assertions.");
        return Task.CompletedTask;
    }
    private static async Task ReadOnlyProperties()
    {
        using var f = new AdvancedFixture();
        f.Element.IsEnabled = false; f.Element.IsOffscreen = true;
        f.Element.Properties["wpf.hasItems"] = new() { Status = UiPropertyStatus.Known, Value = JsonSerializer.SerializeToElement(false), Source = "fixture explicit getter" };
        var result = await f.Run(AdvancedFixture.Step(StepAction.AssertProperty, "{\"property\":\"wpf.hasItems\",\"equals\":false}"));
        Check(result.Status == RunStatus.Passed && f.Inputs.Count == 0 && f.Lookups == 0, "Read-only property assertion dispatched input or rejected disabled/offscreen observation.");
    }
    private static async Task PropertyFailures()
    {
        using var f = new AdvancedFixture();
        var step = AdvancedFixture.Step(StepAction.AssertProperty, "{\"property\":\"wpf.hasItems\",\"equals\":\"false\"}");
        f.Element.Properties["wpf.hasItems"] = new() { Status = UiPropertyStatus.Known, Value = JsonSerializer.SerializeToElement(false), Source = "fixture" };
        var mismatch = await f.Run(step);
        var fact = mismatch.FailureDiagnostics.First(d => d.Category == FailureCategory.AssertionMismatch);
        Check(fact.Expected == "\"false\"" && fact.Actual == "false" && fact.Comparison!.Contains("wpf.hasItems", StringComparison.Ordinal), "Typed failure values were flattened/coerced.");
        foreach (var status in new[] { UiPropertyStatus.Unsupported, UiPropertyStatus.Unavailable, UiPropertyStatus.Redacted, UiPropertyStatus.Truncated })
        {
            f.Element.Properties["wpf.hasItems"] = new() { Status = status, Value = JsonSerializer.SerializeToElement("do-not-use") };
            var run = await f.Run(step);
            Check(run.Status == RunStatus.Failed && run.FailureDiagnostics[0].Category == (status == UiPropertyStatus.Unsupported ? FailureCategory.CapabilityUnavailable : FailureCategory.EvidenceUnavailable), "Unknown property state became an assertion value.");
        }
        f.Element.Properties.Clear();
        Check((await f.Run(step)).FailureDiagnostics[0].Category == FailureCategory.CapabilityUnavailable, "Omitted unsupported property classified as a value.");
        f.Element.Properties["wpf.hasItems"] = new() { Status = UiPropertyStatus.Known, Value = JsonSerializer.SerializeToElement<object?>(null) };
        Check((await f.Run(AdvancedFixture.Step(StepAction.AssertProperty, "{\"property\":\"wpf.hasItems\",\"equals\":null}"))).Status == RunStatus.Passed, "Known JSON null was confused with missing observation.");
        Check(f.Inputs.Count == 0, "Property failures changed UI.");
    }
    private static async Task TypedValue()
    {
        using var f = new AdvancedFixture(); f.Element.Name = "Accessible cell label"; f.Element.Value = "Legacy value must not replace property";
        foreach (var value in new[] { "", "Cell contents" })
        {
            f.Element.Properties["uia.value"] = new() { Status = UiPropertyStatus.Known, Value = JsonSerializer.SerializeToElement(value), Source = "fixture ValuePattern" };
            var step = AdvancedFixture.Step(StepAction.AssertProperty, JsonSerializer.Serialize(new { property = "uia.value", equals = value }));
            var run = await f.Run(step);
            Check(run.Status == RunStatus.Passed && run.Steps[0].Snapshot!.Elements[0].Properties["uia.value"].Value!.Value.GetString() == value, "Typed cell value was replaced by Name/legacy Value or empty string lost.");
        }
        Check(f.Inputs.Count == 0, "Cell property assertion changed UI.");
    }
    private static async Task TruncatedLegacyText()
    {
        using var f = new AdvancedFixture();
        var prefix = new string('x', 8192);
        f.Element.ControlType = "Edit"; f.Element.Value = prefix; f.Element.IsValueTruncated = true;
        foreach (var expected in new[] { prefix, "contains:xxx" })
        {
            var run = await f.Run(AdvancedFixture.Step(StepAction.AssertText, expected), AdvancedFixture.Step(StepAction.Click));
            Check(run.Status == RunStatus.Failed && run.Steps[1].Status == RunStatus.Skipped
                && run.FailureDiagnostics[0].Category == FailureCategory.EvidenceUnavailable
                && run.FailureDiagnostics[0].ActionOutcome == ActionOutcome.NotDispatched,
                "Incomplete 8193+ character value falsely proved text or became an application mismatch.");
            Check(run.Steps[0].Snapshot!.Elements[0].IsValueTruncated, "Evidence lost the legacy text truncation flag.");
        }
        f.Element.ControlType = "Text"; f.Element.Value = ""; f.Element.Name = "Matching accessible name";
        var fallback = await f.Run(AdvancedFixture.Step(StepAction.AssertText, f.Element.Name));
        Check(fallback.Status == RunStatus.Failed && fallback.FailureDiagnostics[0].Category == FailureCategory.EvidenceUnavailable,
            "Accessible-name fallback concealed a truncated empty legacy value.");
        Check(f.Inputs.Count == 0, "Failed text assertions dispatched or retried input.");
    }
    private static async Task CompleteLegacyText()
    {
        using var f = new AdvancedFixture(); f.Element.ControlType = "Edit";
        f.Element.Properties["uia.value"] = new() { Status = UiPropertyStatus.Truncated, Source = "Typed property has a separate 2048-character bound." };
        foreach (var length in new[] { 3000, 8192 })
        {
            f.Element.Value = new string('x', length);
            Check((await f.Run(AdvancedFixture.Step(StepAction.AssertText, f.Element.Value))).Status == RunStatus.Passed,
                "Complete legacy text was rejected because a separately bounded typed property was truncated.");
        }
        f.Element.ControlType = "Text"; f.Element.Value = ""; f.Element.Name = "Accessible name";
        Check((await f.Run(AdvancedFixture.Step(StepAction.AssertText, f.Element.Name))).Status == RunStatus.Passed,
            "Complete empty legacy values no longer permit ordinary accessible-name fallback.");
        var legacy = JsonSerializer.Deserialize<UiElementInfo>("{\"value\":\"legacy\"}", TestyJson.Options)!;
        Check(!legacy.IsValueTruncated && f.Inputs.Count == 0, "Legacy default changed or a text assertion dispatched input.");
    }
    private static async Task PropertyRedaction()
    {
        using var f = new AdvancedFixture();
        f.Element.IsPassword = true;
        f.Element.Properties["wpf.hasItems"] = new() { Status = UiPropertyStatus.Known, Value = JsonSerializer.SerializeToElement("secret-property-value") };
        var run = await f.Run(new TestStep { Action = StepAction.Screenshot });
        var json = File.ReadAllText(Path.Combine(run.ArtifactDirectory, "run.json"));
        Check(!json.Contains("secret-property-value", StringComparison.Ordinal) && run.Steps[0].Snapshot!.Elements[0].Properties["wpf.hasItems"].Status == UiPropertyStatus.Redacted, "Password property leaked into recorded evidence.");
        Check(f.Element.Properties["wpf.hasItems"].Value!.Value.GetString() == "secret-property-value", "Redaction mutated driver's source data.");
    }
    private static async Task ItemAssertions()
    {
        using var f = new AdvancedFixture(); f.Element.ChildCoverage = ChildCoverage.RealizedOnly; f.Element.IsEnabled = false; f.Element.IsOffscreen = true;
        foreach (var (action, status) in new[] { (StepAction.AssertItemExists, ItemLookupStatus.Unique), (StepAction.AssertItemAbsent, ItemLookupStatus.Missing) })
        {
            f.Lookup = new() { Status = status, Message = "Read-only logical lookup complete.", ObservedAt = DateTimeOffset.UtcNow.AddSeconds(1) };
            var run = await f.Run(AdvancedFixture.Step(action, "{\"item\":{\"id\":\"virtual-1042\"}}"));
            Check(run.Status == RunStatus.Passed && run.Steps[0].ItemLookup?.Status == status && run.Steps[0].ItemLookup?.ObservedAt == f.Lookup.ObservedAt, "Logical lookup evidence missing or virtualized absence not proven.");
        }
        Check(f.Inputs.Count == 0 && f.Lookups == 2, "Item assertions performed UI mutation or repeated successful lookup.");
    }
    private static async Task ItemFailureBoundaries()
    {
        using var f = new AdvancedFixture();
        foreach (var status in new[] { ItemLookupStatus.Ambiguous, ItemLookupStatus.Unsupported, ItemLookupStatus.Unavailable, (ItemLookupStatus)99 })
        {
            f.Lookup = new() { Status = status, ObservedAt = DateTimeOffset.UtcNow.AddSeconds(1) };
            var run = await f.Run(AdvancedFixture.Step(StepAction.AssertItemAbsent, "{\"item\":{\"label\":\"duplicate\"}}"));
            Check(run.Status == RunStatus.Failed && run.Steps[0].ItemLookup?.Status == status && run.FailureDiagnostics[0].ObservedAt == f.Lookup.ObservedAt, "Invalid/unsupported lookup proved absence or lost lookup timestamp.");
        }
        Check(f.Inputs.Count == 0, "Failed logical assertion mutated UI.");
    }
    private static async Task VirtualizedAbsence()
    {
        using var f = new AdvancedFixture();
        var step = new TestStep { Action = StepAction.AssertNotExists, Selector = "id:UnrealizedItem", TimeoutMs = 100 };
        foreach (var coverage in new[] { ChildCoverage.RealizedOnly, ChildCoverage.Unknown })
        {
            f.Element.ChildCoverage = coverage;
            var run = await f.Run(step);
            Check(run.Status == RunStatus.Failed && run.FailureDiagnostics[0].Category == FailureCategory.EvidenceUnavailable, "Visible-tree absence passed despite incomplete logical coverage.");
        }
        f.Element.ChildCoverage = ChildCoverage.Complete;
        Check((await f.Run(step)).Status == RunStatus.Passed, "Complete legacy tree absence stopped working.");
    }
    private static async Task CapabilityGates()
    {
        using var f = new AdvancedFixture();
        foreach (var action in new[] { StepAction.Expand, StepAction.Collapse, StepAction.RealizeItem, StepAction.ScrollIntoView, StepAction.ScrollPercent })
        {
            var step = AdvancedFixture.Step(action, action == StepAction.RealizeItem ? "{\"item\":{\"id\":\"A\"}}" : action == StepAction.ScrollPercent ? "{\"vertical\":20}" : "");
            f.Element.Capabilities.Clear();
            var count = f.Inputs.Count;
            var failed = await f.Run(step);
            Check(failed.FailureDiagnostics[0].Category == FailureCategory.CapabilityUnavailable && f.Inputs.Count == count, "Unsupported advanced action reached input.");
            f.Element.Capabilities.Add(AdvancedSteps.Capability(action)); f.Element.IsOffscreen = true;
            Check((await f.Run(step)).Status == RunStatus.Passed && f.Inputs.Count == count + 1 && f.Inputs[^1].Action == action, "Supported advanced action did not execute exactly once.");
        }
    }
    private static async Task LookupDeadline()
    {
        using var f = new AdvancedFixture(); f.PendingLookup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = await f.Run(AdvancedFixture.Step(StepAction.AssertItemAbsent, "{\"item\":{\"id\":\"A\"}}" )).WaitAsync(TimeSpan.FromSeconds(2));
        Check(run.Status == RunStatus.Failed && run.FailureDiagnostics[0].Category == FailureCategory.AutomationTimeout && f.Inputs.Count == 0, "Uncooperative lookup hung or became absence.");
        f.PendingLookup.SetResult(new() { Status = ItemLookupStatus.Missing });
        Check(run.Status == RunStatus.Failed, "Late lookup rewrote verdict.");
    }
    private static async Task LookupDeadlineFacts()
    {
        using var f = new AdvancedFixture();
        var pending = new TaskCompletionSource<ItemLookupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new ItemLookupResult { Status = ItemLookupStatus.Unique, Message = "One logical item matched.", ObservedAt = DateTimeOffset.UtcNow };
        f.LookupProvider = count => count == 1 ? Task.FromResult(observed) : pending.Task;
        var step = AdvancedFixture.Step(StepAction.AssertItemAbsent, "{\"item\":{\"id\":\"Present\"}}"); step.TimeoutMs = 400;
        var run = await f.Run(step).WaitAsync(TimeSpan.FromSeconds(2));
        Check(run.Status == RunStatus.Failed && run.FailureDiagnostics[0].Category == FailureCategory.AutomationTimeout && f.Lookups == 2 && f.Inputs.Count == 0, "Later stalled lookup lost primary timeout or changed UI.");
        var prior = run.FailureDiagnostics.Single(d => d.Category == FailureCategory.AssertionMismatch);
        Check(prior.Expected == "Missing" && prior.Actual == "Unique" && prior.ObservedAt == observed.ObservedAt && run.Steps[0].ItemLookup?.Status == ItemLookupStatus.Unique, "Earlier completed logical lookup mismatch was lost or replaced with a timeout guess.");
        pending.SetResult(new() { Status = ItemLookupStatus.Missing });
        Check(run.Status == RunStatus.Failed && prior.Actual == "Unique", "Late lookup changed prior authoritative evidence.");
    }
    internal static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Invalid payload accepted."); }
}

internal sealed class AdvancedFixture : ITargetDriver, IItemLookupDriver
{
    public string KeyVariable { get; } = "TESTY_ADVANCED_FIXTURE_KEY_" + Guid.NewGuid().ToString("N");
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "Testy-advanced-" + Guid.NewGuid().ToString("N"));
    public TargetInfo? Target { get; } = new() { ProcessId = 42, Title = "Fixture" };
    public UiElementInfo Element { get; } = new() { AutomationId = "Container", Selector = "id:Container", Name = "Ready", ControlType = "List", IsEnabled = true };
    public List<TestStep> Inputs { get; } = [];
    public int Snapshots { get; private set; }
    public int Lookups { get; private set; }
    public bool FailCapture { get; set; }
    public bool FailInput { get; set; }
    public Action<TestStep>? OnInput { get; set; }
    public ItemLookupResult Lookup { get; set; } = new() { Status = ItemLookupStatus.Unique };
    public TaskCompletionSource<ItemLookupResult>? PendingLookup { get; set; }
    public Func<int, Task<ItemLookupResult>>? LookupProvider { get; set; }
    public AdvancedFixture() { Directory.CreateDirectory(Root); Environment.SetEnvironmentVariable(KeyVariable, "fixture-only-not-a-real-key"); }
    public static TestStep Step(StepAction action, string value = "") => new() { Title = action.ToString(), Action = action, Selector = "id:Container", Value = value, TimeoutMs = 100 };
    public Task<RunResult> Run(params TestStep[] steps) => new TestRunner(this, Root).RunAsync(new TestCase { Name = "Advanced fixture", Steps = steps.ToList() });
    public Task<IReadOnlyList<TargetInfo>> GetTargetsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TargetInfo>>([Target!]);
    public Task AttachAsync(int processId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<UiSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        Snapshots++;
        return Task.FromResult(new UiSnapshot { Target = Target!, Elements = [TestyJson.Clone(Element)], ScreenshotBounds = new() { Width = 1, Height = 1 } });
    }
    public Task ExecuteAsync(TestStep step, CancellationToken cancellationToken = default)
    {
        Inputs.Add(TestyJson.Clone(step));
        if (FailInput) throw new InvalidOperationException("Fixture input uncertain.");
        Element.Value = step.Action == StepAction.TypeText ? step.Value : "After input";
        OnInput?.Invoke(step);
        return Task.CompletedTask;
    }
    public Task<ItemLookupResult> LookupItemAsync(string containerSelector, string itemValue, CancellationToken cancellationToken = default)
    {
        Lookups++; AdvancedSteps.ParseItem(itemValue);
        return LookupProvider?.Invoke(Lookups) ?? PendingLookup?.Task ?? Task.FromResult(TestyJson.Clone(Lookup));
    }
    public Task<string> CaptureAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (FailCapture) throw new IOException("Fixture screenshot unavailable.");
        File.WriteAllBytes(filePath, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jU1sAAAAASUVORK5CYII="));
        return Task.FromResult(filePath);
    }
    public void Dispose() { Environment.SetEnvironmentVariable(KeyVariable, null); if (Directory.Exists(Root) && Path.GetFileName(Root).StartsWith("Testy-advanced-", StringComparison.Ordinal) && Path.GetDirectoryName(Root) == Path.TrimEndingDirectorySeparator(Path.GetTempPath())) Directory.Delete(Root, true); }
}
