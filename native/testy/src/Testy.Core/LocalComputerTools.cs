using System.Text.Json;

namespace Testy.Core;

public sealed class ComputerToolObservation
{
    public string ToolName { get; set; } = "";
    public string SavedStepId { get; set; } = "";
    public JsonElement? NativeAction { get; set; }
    public NativeActionReceipt? NativeReceipt { get; set; }
    public string Status { get; set; } = "observed";
    public string Message { get; set; } = "";
    public UiSnapshot? Snapshot { get; set; }
    public string ScreenshotPath { get; set; } = "";
    public RunResult? Execution { get; set; }
    public ProjectToolResult? Project { get; set; }
}

/// <summary>
/// A provider-independent, bounded function-call dispatcher. It never executes model-supplied code.
/// An adapter can return its Snapshot plus the screenshot as a multimodal tool observation.
/// </summary>
public sealed class LocalComputerTools
{
    public static JsonElement ResponsesTools { get; } = JsonDocument.Parse("""
    [
      {"type":"function","name":"observe_application","description":"Inspect only the already attached application. Returns its current UI tree and an application screenshot artifact.","strict":true,"parameters":{"type":"object","properties":{},"required":[],"additionalProperties":false}},
      {"type":"function","name":"perform_ui_action","description":"Execute one bounded step in the already attached application, then return assertion status, UI tree and screenshot. Mutating actions are never retried. Requires an explicit action and a unique observed selector, except for coordinates, keys, wait and screenshot. No shell, process launch or filesystem actions are available.","strict":true,"parameters":{
        "type":"object","additionalProperties":false,"properties":{
          "title":{"type":"string"},
          "action":{"type":"string","enum":["click","typeText","select","toggle","assertText","assertExists","assertNotExists","assertEnabled","wait","screenshot","keyPress","coordinateClick","expand","collapse","realizeItem","scrollIntoView","scrollPercent","assertProperty","assertItemExists","assertItemAbsent","gridEditCell","gridCommitRow","gridCancelRow"]},
          "selector":{"type":"string"},"value":{"type":"string"},"timeoutMs":{"type":"integer","minimum":100,"maximum":60000},"x":{"type":"integer","minimum":0},"y":{"type":"integer","minimum":0}
        },"required":["title","action","selector","value","timeoutMs","x","y"]
      }}
    ]
    """).RootElement.Clone();
    public static JsonElement CompatibleTools { get; } = JsonSerializer.SerializeToElement(ResponsesTools.EnumerateArray().Select(t => new
    {
        type = "function", function = new { name = t.GetProperty("name").GetString(), description = t.GetProperty("description").GetString(), strict = true, parameters = t.GetProperty("parameters") }
    }), TestyJson.Options);
    public static JsonElement BoundResponsesTools { get; } = JsonDocument.Parse("""
    [
      {"type":"function","name":"observe_application","description":"Inspect the attached application and obtain fresh screenshot and UI tree evidence without mutation.","strict":true,"parameters":{"type":"object","properties":{},"required":[],"additionalProperties":false}},
      {"type":"function","name":"perform_saved_step","description":"Execute exactly the next saved step by ID, using its immutable stored action, selector, value and timeout. Returns fresh screenshot, UI tree and deterministic assertion status. Unknown, repeated and out-of-order IDs are rejected before input. Never invent or rewrite a saved step.","strict":true,"parameters":{"type":"object","properties":{"stepId":{"type":"string"}},"required":["stepId"],"additionalProperties":false}}
    ]
    """).RootElement.Clone();
    public static JsonElement BoundCompatibleTools { get; } = JsonSerializer.SerializeToElement(BoundResponsesTools.EnumerateArray().Select(t => new
    {
        type = "function", function = new { name = t.GetProperty("name").GetString(), description = t.GetProperty("description").GetString(), strict = true, parameters = t.GetProperty("parameters") }
    }), TestyJson.Options);
    public static JsonElement RecoveryBoundResponsesTools { get; } = JsonSerializer.SerializeToElement(BoundResponsesTools.EnumerateArray().Select(t => t.Clone()).Append(JsonDocument.Parse("""
    {"type":"function","name":"perform_saved_step_alternate","description":"Explicitly select one previously authored selector alternative for the exact next saved step. The original must be absent with complete scope coverage, and the engine revalidates the approved unique role/business identity immediately before use. Action/value/expectation remain immutable. Never retry a failed, dispatched or uncertain action.","strict":true,"parameters":{"type":"object","properties":{"stepId":{"type":"string"},"alternativeId":{"type":"string"}},"required":["stepId","alternativeId"],"additionalProperties":false}}
    """).RootElement.Clone()), TestyJson.Options);
    private static JsonElement RecoveryBoundCompatibleTools { get; } = JsonSerializer.SerializeToElement(RecoveryBoundResponsesTools.EnumerateArray().Select(t => new
    { type = "function", function = new { name = t.GetProperty("name").GetString(), description = t.GetProperty("description").GetString(), strict = true, parameters = t.GetProperty("parameters") } }), TestyJson.Options);
    public const string SavedStepInstructions = "This is a bound saved workflow. Use observe_application or perform_saved_step with the exact next saved stepId. Use an alternate-selector tool only when it is explicitly supplied and an approved alternative is already authored. The dispatcher supplies the immutable saved action, selector, value and timeout; do not rewrite or add selector terms. Execute every step in order, including screenshots and assertions, even when the desired state already appears true. Inspect returned screenshot and tree before choosing each next step. Finish only after every saved step passed; failures must stop execution.";
    public bool IsBound => savedTest is not null;
    public bool HasRecoveryAlternatives => savedTest?.Steps.Any(s => s.SelectorAlternatives.Count > 0) == true;
    public int MinimumModelTurns => savedTest is null ? 1 : savedTest.Steps.Count + 1;
    public string SavedWorkflowJson => savedTest is null ? "" : JsonSerializer.Serialize(savedTest, TestyJson.Options);
    public JsonElement SessionResponsesTools => IsBound ? HasRecoveryAlternatives ? RecoveryBoundResponsesTools : BoundResponsesTools : ResponsesTools;
    public JsonElement SessionCompatibleTools => IsBound ? HasRecoveryAlternatives ? RecoveryBoundCompatibleTools : BoundCompatibleTools : CompatibleTools;
    public const string RecoveryInstructions = "If perform_saved_step_alternate is supplied and the original selector is absent, you may explicitly choose an alternativeId already authored on that exact saved step. Never invent an alternative, change action/value/assertion, substitute an existing ambiguous/disabled original, or retry any failed/uncertain/dispatched mutation. The engine must prove fresh unique approved role/business identity and record recovery evidence. Otherwise stop and propose a reviewable test change; do not execute it.";

    private readonly ITargetDriver driver;
    private readonly string artifactsRoot;
    private readonly int expectedProcess;
    private readonly int maximumCalls;
    private readonly SemaphoreSlim gate = new(1, 1);
    private int calls;
    private readonly TestCase? savedTest;
    private int nextStep;
    private bool freshEvidence;
    private bool stopped;
    /// <summary>A project command may change the application; require a new explicit observation before saved input.</summary>
    public void InvalidateObservation() => freshEvidence = false;
    public LocalComputerTools(ITargetDriver driver, string artifactsRoot, int maximumCalls = 40, TestCase? savedTest = null)
    {
        if (maximumCalls is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(maximumCalls), "The tool budget must be1–500 calls.");
        this.driver = driver;
        this.artifactsRoot = Path.GetFullPath(artifactsRoot);
        this.maximumCalls = maximumCalls;
        expectedProcess = driver.Target?.ProcessId ?? throw new InvalidOperationException("Attach a target before creating a computer tool session.");
        if (savedTest is not null) { TestValidator.Validate(savedTest); this.savedTest = TestyJson.Clone(savedTest); }
    }
    public async Task<ComputerToolObservation> DispatchAsync(string name, string argumentsJson, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (driver.Target?.ProcessId != expectedProcess) throw new InvalidOperationException("The tool session's attached application changed. Create a new session.");
            if (++calls > maximumCalls) throw new InvalidOperationException($"Computer tool session exceeded its {maximumCalls}-call budget.");
            using var args = JsonDocument.Parse(argumentsJson);
            if (name == "observe_application")
            {
                if (args.RootElement.ValueKind != JsonValueKind.Object || args.RootElement.EnumerateObject().Any())
                    throw new InvalidDataException("observe_application expects an empty JSON object.");
                var test = new TestCase { Name = "Agent observation", Steps = [new TestStep { Title = "Observe attached application", Action = StepAction.Screenshot }] };
                var observation = Observe(await new TestRunner(driver, artifactsRoot).RunAsync(test, null, cancellationToken));
                observation.ToolName = "observe_application";
                freshEvidence = HasFreshEvidence(observation);
                if (!freshEvidence) stopped = true;
                return observation;
            }
            if (name is "perform_saved_step" or "perform_saved_step_alternate")
            {
                if (savedTest is null) throw new InvalidDataException("perform_saved_step requires a bound saved workflow.");
                var alternate = name == "perform_saved_step_alternate";
                var fields = args.RootElement.ValueKind == JsonValueKind.Object ? args.RootElement.EnumerateObject().ToArray() : [];
                var expectedFields = alternate ? new[] { "stepId", "alternativeId" } : ["stepId"];
                if (fields.Length != expectedFields.Length || fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != expectedFields.Length
                    || fields.Any(f => !expectedFields.Contains(f.Name, StringComparer.Ordinal) || f.Value.ValueKind != JsonValueKind.String))
                    throw new InvalidDataException(name + " requires exactly these string fields: " + string.Join(", ", expectedFields));
                var id = args.RootElement.GetProperty("stepId").GetString();
                if (nextStep >= savedTest.Steps.Count || savedTest.Steps[nextStep].Id != id)
                    throw new InvalidDataException($"Unknown, repeated or out-of-order saved step '{id}'. Next step: {(nextStep < savedTest.Steps.Count ? savedTest.Steps[nextStep].Id : "none; workflow complete")}.");
                if (stopped || !freshEvidence) throw new InvalidOperationException("A saved step requires fresh successful screenshot and UI tree evidence. Failed or uncertain dispatch cannot be retried in this session.");
                var alternativeId = alternate ? args.RootElement.GetProperty("alternativeId").GetString() : null;
                if (alternate && !savedTest.Steps[nextStep].SelectorAlternatives.Any(a => a.Id == alternativeId))
                    throw new InvalidDataException("The requested alternative is not explicitly authored on the next saved step.");
                freshEvidence = false;
                stopped = true; // An exception or cancellation after dispatch must not make the same mutation retryable.
                var test = new TestCase { Name = savedTest.Name, Intent = savedTest.Intent, Steps = [TestyJson.Clone(savedTest.Steps[nextStep])] };
                var runner = new TestRunner(driver, artifactsRoot);
                var observation = Observe(alternate
                    ? await runner.RunWithSelectorAlternativeAsync(test, id!, alternativeId!, null, cancellationToken)
                    : await runner.RunAsync(test, null, cancellationToken));
                observation.ToolName = name;
                observation.SavedStepId = id!;
                nextStep++;
                freshEvidence = HasFreshEvidence(observation);
                stopped = !freshEvidence;
                return observation;
            }
            if (name == "perform_ui_action")
            {
                if (savedTest is not null) throw new InvalidDataException("Bound saved execution only accepts perform_saved_step; arbitrary action payloads are prohibited.");
                var json = "{\"name\":\"Agent action\",\"intent\":\"One scoped computer tool action\",\"steps\":[" + args.RootElement.GetRawText() + "]}";
                var test = PlanCodec.Parse(json, new PlanningRequest());
                var observation = Observe(await new TestRunner(driver, artifactsRoot).RunAsync(test, null, cancellationToken));
                observation.ToolName = "perform_ui_action";
                return observation;
            }
            throw new InvalidDataException($"Unknown computer tool '{name}'. Use observe_application and {(IsBound ? "perform_saved_step" : "perform_ui_action")}.");
        }
        finally { gate.Release(); }
    }
    private static bool HasFreshEvidence(ComputerToolObservation observation) => observation.Execution?.Status == RunStatus.Passed && observation.Snapshot is not null && File.Exists(observation.ScreenshotPath) && new FileInfo(observation.ScreenshotPath).Length > 0;
    private static ComputerToolObservation Observe(RunResult run)
    {
        var step = run.Steps.LastOrDefault(s => s.Status != RunStatus.Skipped);
        return new ComputerToolObservation
        {
            Status = run.Status.ToString(), Message = run.Summary, Execution = run,
            Snapshot = step?.Snapshot is null ? null : PlannerPrompt.Sanitize(step.Snapshot),
            ScreenshotPath = step?.ScreenshotPath ?? ""
        };
    }
}
