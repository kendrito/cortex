using System.Text.Json;
using System.Text.Json.Serialization;

namespace Testy.Core;

public enum StepAction { Click, TypeText, Select, Toggle, AssertText, AssertExists, AssertNotExists, AssertEnabled, Wait, Screenshot, KeyPress, CoordinateClick, Expand, Collapse, RealizeItem, ScrollIntoView, ScrollPercent, AssertProperty, AssertItemExists, AssertItemAbsent, GridEditCell, GridCommitRow, GridCancelRow }
public enum UiPropertyStatus { Known, Unsupported, Unavailable, Redacted, Truncated }
public enum ChildCoverage { Complete, RealizedOnly, Unknown }
public enum ItemLookupStatus { Unique, Missing, Ambiguous, Unsupported, Unavailable }
public sealed class UiPropertyObservation
{
    public UiPropertyStatus Status { get; set; } = UiPropertyStatus.Unavailable;
    public JsonElement? Value { get; set; }
    public string Source { get; set; } = "";
}
public sealed class ItemLookupResult
{
    public ItemLookupStatus Status { get; set; } = ItemLookupStatus.Unavailable;
    public string Message { get; set; } = "";
    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.UtcNow;
}
/// <summary>Read-only logical item lookup. It must never realize, scroll, expand, select, or focus an item.</summary>
public interface IItemLookupDriver
{
    Task<ItemLookupResult> LookupItemAsync(string containerSelector, string itemValue, CancellationToken cancellationToken = default);
}
public enum RunStatus { Pending, Running, Passed, Failed, Cancelled, Skipped }
public enum FailureCategory { AssertionMismatch, SelectorNotFound, SelectorAmbiguous, ControlNotReady, TargetUnavailable, AutomationTimeout, AutomationError, EvidenceUnavailable, ProviderFailure, AgentFailure, WorkflowNotVerified, Cancelled, Unknown, CapabilityUnavailable }
/// <summary>What the runner knows about a dispatched input, not whether the application behaved correctly.</summary>
public enum ActionOutcome { NotDispatched, Completed, Unknown }
public sealed class DiagnosticEvidence
{
    public string Kind { get; set; } = "";
    public string Path { get; set; } = "";
}
public sealed class FailureDiagnostic
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public FailureCategory Category { get; set; } = FailureCategory.Unknown;
    public string ObservedFact { get; set; } = "";
    public string? Expected { get; set; }
    public string? Actual { get; set; }
    public string? Comparison { get; set; }
    public string Selector { get; set; } = "";
    public ActionOutcome ActionOutcome { get; set; }
    public DateTimeOffset? ObservedAt { get; set; }
    public int? StepIndex { get; set; }
    public int? LastSuccessfulStepIndex { get; set; }
    public string LastSuccessfulStepTitle { get; set; } = "";
    public string CauseAssessment { get; set; } = "Root cause is not established by these observations.";
    public List<DiagnosticEvidence> Evidence { get; set; } = [];
    public List<string> SuggestedNextChecks { get; set; } = [];
}
public sealed class TestStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "New step";
    public StepAction Action { get; set; } = StepAction.Click;
    public string Selector { get; set; } = "";
    public string Value { get; set; } = "";
    public int TimeoutMs { get; set; } = 5000;
    public int X { get; set; }
    public int Y { get; set; }
    /// <summary>Explicitly authored equivalents. Empty by default; never selected automatically.</summary>
    public List<SelectorAlternative> SelectorAlternatives { get; set; } = [];
}
public sealed class TestCase
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Untitled test";
    public string Intent { get; set; } = "";
    public string Category { get; set; } = "Functional";
    public string TargetPath { get; set; } = "";
    /// <summary>
    /// The app's friendly name ("Customer Desk"), stored with TargetPath when the app was named rather than picked. Without a TargetPath (the
    /// sample tests), Studio's Run finds the app by this name.
    /// </summary>
    public string TargetName { get; set; } = "";
    /// <summary>The AppUserModelID of a packaged (Store/MSIX) app the test runs in; empty for a desktop program.</summary>
    public string TargetAppId { get; set; } = "";
    public List<TestStep> Steps { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class TargetInfo
{
    public int ProcessId { get; set; }
    public long WindowHandle { get; set; }
    public string Title { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public override string ToString() => $"{Title}  ({ProcessId})";
}
public sealed class ElementBounds
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}
public sealed class UiElementInfo
{
    public string RuntimeId { get; set; } = "";
    public string AutomationId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ControlType { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string Value { get; set; } = "";
    public string Selector { get; set; } = "";
    /// <summary>The legacy Value is an incomplete observation and cannot prove text equality or a value transition.</summary>
    public bool IsValueTruncated { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsOffscreen { get; set; }
    public bool IsPassword { get; set; }
    public int Depth { get; set; }
    public ElementBounds Bounds { get; set; } = new();
    public Dictionary<string, UiPropertyObservation> Properties { get; set; } = new(StringComparer.Ordinal);
    public List<string> Capabilities { get; set; } = [];
    public ChildCoverage ChildCoverage { get; set; } = ChildCoverage.Complete;
}
public sealed class UiSnapshot
{
    public TargetInfo Target { get; set; } = new();
    public string Source { get; set; } = "Windows UI Automation";
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<UiElementInfo> Elements { get; set; } = [];
    public bool IsTruncated { get; set; }
    public ElementBounds ScreenshotBounds { get; set; } = new();
    public string FocusedSelector { get; set; } = "";
    public List<UiApplicationDiagnostic> ApplicationDiagnostics { get; set; } = [];
    public bool DiagnosticsTruncated { get; set; }
}
/// <summary>Bounded application/probe observations. These are facts, never inferred root causes or raw binding exception content.</summary>
public sealed class UiApplicationDiagnostic
{
    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Source { get; set; } = "";
    public string Kind { get; set; } = "";
    public UiPropertyStatus Status { get; set; } = UiPropertyStatus.Unavailable;
    public string Selector { get; set; } = "";
    public string Message { get; set; } = "";
    public string ObservedRuntimeId { get; set; } = "";
    public string ObservedAutomationId { get; set; } = "";
    public UiPropertyStatus IdentityStatus { get; set; } = UiPropertyStatus.Unavailable;
}
public sealed class StepResult
{
    public int Index { get; set; }
    public TestStep Step { get; set; } = new();
    public RunStatus Status { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public double DurationMs { get; set; }
    public string Message { get; set; } = "";
    public string ScreenshotPath { get; set; } = "";
    /// <summary>Original successful capture provenance; its path remains the source when evidence is copied into an aggregate run.</summary>
    public ScreenshotEvidence? ScreenshotEvidence { get; set; }
    public UiSnapshot? Snapshot { get; set; }
    public ItemLookupResult? ItemLookup { get; set; }
    public List<FailureDiagnostic> FailureDiagnostics { get; set; } = [];
    public SelectorRecoveryEvidence? SelectorRecovery { get; set; }
}
public sealed class RunResult
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TestId { get; set; } = "";
    public string TestName { get; set; } = "";
    public RunStatus Status { get; set; }
    public TargetInfo Target { get; set; } = new();
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public List<StepResult> Steps { get; set; } = [];
    public string Summary { get; set; } = "";
    public string AiAnalysis { get; set; } = "";
    public string ArtifactDirectory { get; set; } = "";
    public List<ProjectToolResult> ProjectEvidence { get; set; } = [];
    public List<FailureDiagnostic> FailureDiagnostics { get; set; } = [];
}
public sealed class RunProgress
{
    public RunResult Run { get; set; } = new();
    public StepResult? Step { get; set; }
    public string Message { get; set; } = "";
}
public enum ProviderKind { Codex, OpenAI, Compatible, Offline }
public sealed class ProviderSettings
{
    private ProviderKind kind = ProviderKind.Codex;
    private string model = "", endpoint = "https://api.openai.com/v1/responses", keyVariable = "OPENAI_API_KEY";
    private bool nativeComputerUse = true;
    public ProviderKind Kind { get => CortexModelBridge.Enabled ? ProviderKind.Compatible : kind; set => kind = value; }
    public string Model { get => CortexModelBridge.Enabled ? "cortex" : model; set => model = value; }
    public string Endpoint { get => CortexModelBridge.Enabled ? "http://127.0.0.1/cortex-model" : endpoint; set => endpoint = value; }
    public string ApiKeyEnvironmentVariable { get => CortexModelBridge.Enabled ? CortexModelBridge.TokenVariable : keyVariable; set => keyVariable = value; }
    public string CodexExecutable { get; set; } = "codex.exe";
    public bool LiveReview { get; set; }
    public bool AiDirectedExecution { get; set; } = true;
    public bool NativeComputerUse { get => !CortexModelBridge.Enabled && nativeComputerUse; set => nativeComputerUse = value; }
    public int MaximumAgentTurns { get; set; } = 30;
    /// <summary>Retries only unsuccessful transient model HTTP responses before dispatching any returned tool. Never retries commands or UI input.</summary>
    public int MaximumProviderRetries { get; set; } = 2;
    public int ProviderRetryDelayMs { get; set; } = 500;
    public bool SupportsImages { get; set; } = true;
    public ProjectToolSettings? ProjectTools { get; set; }
}
public sealed class PlanningRequest
{
    public string Instructions { get; set; } = "";
    public UiSnapshot Snapshot { get; set; } = new();
    public string ScreenshotPath { get; set; } = "";
    public TestCase? ExistingTest { get; set; }
    public List<ProjectToolResult> ProjectEvidence { get; set; } = [];
    public string ProjectArtifactsDirectory { get; set; } = "";
}
public interface ITargetDriver : IDisposable
{
    TargetInfo? Target { get; }
    Task<IReadOnlyList<TargetInfo>> GetTargetsAsync(CancellationToken cancellationToken = default);
    Task AttachAsync(int processId, CancellationToken cancellationToken = default);
    Task<UiSnapshot> SnapshotAsync(CancellationToken cancellationToken = default);
    Task ExecuteAsync(TestStep step, CancellationToken cancellationToken = default);
    Task<string> CaptureAsync(string filePath, CancellationToken cancellationToken = default);
}
/// <summary>The physical coordinate space committed by a successful target-only screenshot.</summary>
public sealed class ScreenshotEvidence
{
    public string Path { get; set; } = "";
    public int ProcessId { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public ElementBounds Bounds { get; set; } = new();
}
public interface IScreenshotEvidenceSource
{
    ScreenshotEvidence? LastScreenshot { get; }
}
public interface ITestPlanner
{
    string Name { get; }
    Task<TestCase> CreatePlanAsync(PlanningRequest request, CancellationToken cancellationToken = default);
    Task<string> ExplainAsync(RunResult run, CancellationToken cancellationToken = default);
}
public interface IComputerActionExecutor
{
    Task ExecuteAsync(JsonElement action, CancellationToken cancellationToken = default);
}
/// <summary>Pre-input accessibility hit identities recorded by the native Windows executor.</summary>
public sealed class NativeActionReceipt
{
    public int ProcessId { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool InputDelivered { get; set; }
    /// <summary>Direct hit first, stopping at the nearest independently actionable control.</summary>
    public List<string> HitRuntimeIds { get; set; } = [];
}
public interface INativeActionReceiptSource
{
    NativeActionReceipt? LastReceipt { get; }
}
public static class TestyJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}
