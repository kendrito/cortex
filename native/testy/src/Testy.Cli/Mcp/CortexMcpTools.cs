using System.Text.Json.Nodes;

namespace Testy.Cli.Mcp;

/// <summary>The Studio workflow API, shared by Cortex's UI and model tool discovery.</summary>
internal static class CortexMcpTools
{
    private static JsonObject Text(string description, int maximum = 4096) => Schema.String(description, maximum);
    private static JsonObject Id(string noun) => Text($"The {noun} id returned by its list or save operation.", 100);
    private static JsonObject Document(string description) => Schema.Object(new(), description: description, additionalProperties: true);
    private static JsonObject Mode() => Schema.String("AI uses Cortex's current model; replay is deterministic.", values: ["ai", "replay"], fallback: "ai");
    private static JsonObject Probe() => Schema.Boolean("Use the target's explicitly installed WPF helper.", false);
    internal static JsonObject EditableDraft() => Schema.Object(new()
    {
        ["name"] = Text("Current editable test name.",200), ["intent"] = Text("Current expected behavior.",4000), ["category"] = Text("Library category.",200),
        ["targetPath"] = Text("Saved target executable hint.",1024), ["steps"] = Schema.Array(Schema.Step(),"Current authored steps, including unsaved changes.",200)
    },["name","steps"],"Unsaved editor content; never persisted by authoring tools.");
    private static JsonObject Strings(string description) => Schema.Array(Text("Value."), description, 200);
    private static JsonObject Project() => Schema.Object(new()
    {
        ["enabled"] = Schema.Boolean("Allow project tools.", false), ["rootDirectory"] = Text("Explicit local project directory."),
        ["commands"] = Schema.Array(Schema.Object(new() { ["id"] = Text("Fixed command id.",128), ["description"] = Text("Purpose."), ["executable"] = Text("Absolute executable path."), ["arguments"] = Strings("Literal arguments."), ["workingDirectory"] = Text("Project-relative working directory."), ["timeoutSeconds"] = Schema.Integer("Command deadline.",1,600,120) }, ["id","executable","arguments","workingDirectory"]), "Human-authorized command catalog.", 32),
        ["requiredBeforeUiCommands"] = Strings("Command ids that must pass before UI actions."), ["maximumCalls"] = Schema.Integer("Tool call limit.",1,200,20),
        ["maximumCommandInvocations"] = Schema.Integer("Command invocation limit.",0,20,3), ["maximumPlanningTurns"] = Schema.Integer("Planning turn limit.",1,80,12),
        ["maximumOutputCharacters"] = Schema.Integer("Returned text limit.",1000,100000,16000), ["maximumFiles"] = Schema.Integer("File inventory limit.",1,10000,2000), ["maximumFileBytes"] = Schema.Integer("Individual file byte limit.",1,5000000,512000)
    }, description: "Explicit project scope and fixed commands.");
    private static JsonObject Template() => Schema.Object(new()
    {
        ["version"] = Schema.Integer("Template version.",1,1,1), ["name"] = Text("Template name."),
        ["test"] = Schema.Object(new() { ["id"] = Id("test"), ["name"] = Text("Test name."), ["intent"] = Text("Expected behavior."), ["category"] = Text("Library group."), ["targetPath"] = Text("Target executable."), ["steps"] = Schema.Array(Schema.Step(), "Saved step template.",200,1) }, ["name","steps"], "Template test definition."),
        ["bindings"] = Schema.Array(Schema.Object(new() { ["parameter"] = Text("CSV column header."), ["stepId"] = Text("Step whose value to replace."), ["field"] = Schema.String("Only literal whole-value binding is allowed.",values:["value"],fallback:"value") }, ["parameter","stepId"]), "CSV column bindings.",200)
    }, ["test","bindings"], "Literal data matrix template.");

    public static McpTool[] Define(TestyMcpService? service)
    {
        McpTool Tool(string name, string title, string group, string description, JsonObject properties, string[] required,
            bool readOnly = false, bool usesModel = false)
        {
            if (name is "draft_test" or "refine_test")
            {
                properties["draft"] = EditableDraft();
                properties["expectedRevision"] = Text("Required with an inline draft of a saved test; the editor's original saved revision.",64);
            }
            if (name is "queue_test" or "queue_machine" or "save_schedule")
            {
                properties["args"] = Schema.Array(Text("Literal target argument."), "Arguments for the target application.",64);
                properties["timeoutSeconds"] = Schema.Integer("Testing deadline.",1,7200,900);
                properties["startupTimeoutSeconds"] = Schema.Integer("Target startup deadline.",1,120,30);
                properties["shutdownGraceSeconds"] = Schema.Integer("Target graceful stop period.",1,30,3);
                if (name != "save_schedule") properties["stageDirectory"] = Text("VM only: explicitly selected host application folder copied into the guest.");
            }
            return new()
            {
                Name = name, Title = title, Description = description,
                InputSchema = Schema.Object(properties, required), OutputSchema = Schema.Output(new()),
                Annotations = new(readOnly, !readOnly, readOnly, group is "machines" or "authoring" or "recording" or "lifecycle" || name == "run_suite"), UsesModel = usesModel, Group = group, HumanOnly = name is "save_project" or "save_preferences" or "setup_machine" or "forget_machine" or "workspace_restore",
                Handler = (arguments, context) => (service ?? throw new InvalidOperationException("Start the server to call tools.")).WorkflowAsync(name, arguments, context)
            };
        }
        return
        [
            Tool("ai_test", "Test with AI", "authoring", "Describe what to test. Cortex's current model discovers the app, drafts and saves a test, then runs it with live observations. A saved test skips drafting. Optional target overrides are available; otherwise an ambiguous target returns a question without controlling any app. Returns a bounded background task; poll get_task or cancel_task.", new()
            { ["instructions"] = Text("Application, workflow and expected result in your own words.",16000), ["testId"] = Id("saved test"), ["pid"] = Schema.Integer("Optional explicit running app override.",1), ["exe"] = Text("Optional explicit Windows desktop executable override.",1024), ["draftOnly"] = Schema.Boolean("Return an editable unsaved draft without running its steps.",false) }, ["instructions"], usesModel:true),
            Tool("launch_sample", "Open sample app", "authoring", "Open one bundled Testy sample under this server's ownership. It closes when the plugin stops; no model request is sent.", new()
            { ["sample"] = Schema.String("Bundled application.",values:["testlab","orderlab","wpflab"],fallback:"testlab") }, []),
            Tool("list_tasks", "Background tasks", "activity", "Read this server's bounded workflow tasks and their status.", new(), [], true),
            Tool("get_task", "Task details", "activity", "Poll a workflow task; result becomes available after it stops.", new() { ["taskId"] = Id("task") }, ["taskId"], true),
            Tool("cancel_task", "Stop task", "activity", "Request cancellation and wait for owned work to stop. Earlier actions are not undone.", new() { ["taskId"] = Id("task") }, ["taskId"]),
            Tool("list_suites", "Test sets", "suites", "List saved test sets, selected tests and repetition counts.", new(), [], true),
            Tool("save_suite", "Save test set", "suites", "Save a suite from existing tests. Tests are frozen when the suite runs.", new()
            { ["suiteId"] = Id("suite"), ["name"] = Text("Test set name.", 200), ["testIds"] = Strings("Saved tests in execution order."), ["repetitions"] = Schema.Integer("Repeat the complete set.", 1, 100, 1) }, ["name", "testIds"]),
            Tool("delete_suite", "Delete test set", "suites", "Remove a saved test set; run evidence remains.", new() { ["suiteId"] = Id("suite") }, ["suiteId"]),
            Tool("run_suite", "Run test set", "suites", "Start a sequential fail-fast suite against an existing app; returns a background task.", new()
            { ["suiteId"] = Id("suite"), ["pid"] = Schema.Integer("Attached app process id.", 1), ["mode"] = Mode(), ["probe"] = Probe() }, ["suiteId", "pid"], usesModel: true),
            Tool("draft_test", "Draft a test", "authoring", "Use Cortex's model and current application evidence to create an editable test draft; does not execute its steps. The model discovers the app when pid is omitted; ambiguity returns a clarification question.", new()
            { ["pid"] = Schema.Integer("Optional application process override.", 1), ["exe"] = Text("Optional explicit Windows desktop executable override.",1024), ["instructions"] = Text("Requested workflow and expected result.", 16000), ["probe"] = Probe() }, ["instructions"], usesModel: true),
            Tool("refine_test", "Improve a test", "authoring", "Draft changes to an existing test using Cortex's model, discovering the app when pid is omitted. The saved test is not overwritten; save the returned steps with expectedRevision after review.", new()
            { ["testId"] = Id("test"), ["pid"] = Schema.Integer("Optional application process override.", 1), ["exe"] = Text("Optional explicit Windows desktop executable override.",1024), ["instructions"] = Text("Requested changes.", 16000), ["probe"] = Probe() }, ["testId", "instructions"], usesModel: true),
            Tool("explain_run", "Explain a run", "results", "Ask Cortex's model to explain recorded evidence without changing its verdict.", new() { ["runId"] = Id("run") }, ["runId"], usesModel: true),
            Tool("get_project", "Source access", "project", "Read the explicitly selected project and fixed command catalog.", new(), [], true),
            Tool("get_preferences", "Execution preferences", "project", "Read screenshot sharing, replay review and AI budget preferences.", new(), [], true),
            Tool("save_preferences", "Save execution preferences", "project", "Change execution preferences without changing Cortex's model or its provider credentials.", new()
            { ["supportsImages"] = Schema.Boolean("Send target screenshots to Cortex's selected model."), ["liveReview"] = Schema.Boolean("Add AI comments during deterministic replay."), ["aiDirectedExecution"] = Schema.Boolean("Default native Studio run mode."), ["maximumAgentTurns"] = Schema.Integer("Most model actions per run.",1,240), ["maximumProviderRetries"] = Schema.Integer("Retry transient model responses before any action.",0,3), ["providerRetryDelayMs"] = Schema.Integer("Initial model retry delay.",0,5000) }, []),
            Tool("save_project", "Save source access", "project", "Save the human-authorized project scope and fixed command catalog. Commands cannot be invented by the test runner.", new()
            { ["project"] = Project() }, ["project"]),
            Tool("list_jobs", "Queued jobs", "jobs", "Read durable queued, active and finished jobs.", new(), [], true),
            Tool("queue_test", "Queue a test", "jobs", "Queue a frozen test against an executable. Run pump_jobs to process local work; no service or startup task is installed.", new()
            { ["testId"] = Id("test"), ["exe"] = Text("Absolute target executable path."), ["mode"] = Mode(), ["probe"] = Probe(), ["target"] = Text("local or vm:<GUID>.", 100) }, ["testId", "exe"], usesModel: true),
            Tool("cancel_job", "Cancel job", "jobs", "Request cancellation of queued or active work.", new() { ["jobId"] = Id("job") }, ["jobId"]),
            Tool("rerun_job", "Queue a fresh attempt", "jobs", "Explicitly queue a new attempt from a finished job; preserves earlier evidence.", new() { ["jobId"] = Id("job") }, ["jobId"], usesModel: true),
            Tool("delete_job", "Delete job record", "jobs", "Delete a terminal queue record; retain evidence files.", new() { ["jobId"] = Id("job") }, ["jobId"]),
            Tool("pump_jobs", "Process queued work", "jobs", "Start a bounded foreground-owned queue pump. Current Cortex context supplies AI for claimed jobs. No elevation or autostart.", new()
            { ["seconds"] = Schema.Integer("Maximum pump lifetime in seconds.", 1, 86400, 60), ["target"] = Text("local or vm:<GUID>.", 100) }, [], usesModel: true),
            Tool("list_schedules", "Schedules", "schedules", "Read saved schedules. They only dispatch while a queue pump is active.", new(), [], true),
            Tool("save_schedule", "Save schedule", "schedules", "Create a periodic saved-test schedule or enable/disable an existing schedule. AI execution binds Cortex's model when the queue pump starts.", new()
            { ["scheduleId"] = Id("schedule"), ["enabled"] = Schema.Boolean("Whether the schedule is enabled.", true), ["name"] = Text("Schedule name.", 200), ["testId"] = Id("test"), ["exe"] = Text("Target executable."), ["mode"] = Mode(), ["probe"] = Probe(), ["intervalSeconds"] = Schema.Integer("Interval, 60 seconds to 30 days.", 60, 2592000, 3600) }, []),
            Tool("delete_schedule", "Delete schedule", "schedules", "Remove a schedule without deleting its earlier runs.", new() { ["scheduleId"] = Id("schedule") }, ["scheduleId"]),
            Tool("record_demo", "Record a demonstration", "recording", "Record supported UI Automation actions for a bounded duration. Operate the selected application yourself; returns a task.", new()
            { ["pid"] = Schema.Integer("Application process id.", 1), ["durationSeconds"] = Schema.Integer("Recording duration.", 1, 600, 30) }, ["pid"]),
            Tool("list_recordings", "Recordings", "recording", "List completed demonstrations with their ids and recording times.", new(), [], true),
            Tool("draft_from_demo", "Draft from a demonstration", "recording", "Use Cortex's model to draft a reviewed test from recorded actions and explicit expected assertions.", new()
            { ["recordingId"] = Id("recording"), ["instructions"] = Text("Requested workflow.", 16000), ["acceptance"] = Schema.Array(Schema.Step(), "Explicit immutable acceptance assertions.", 200, 1) }, ["recordingId", "instructions", "acceptance"], usesModel: true),
            Tool("materialize_template", "Create a data matrix", "suites", "Expand a literal parameterized test and CSV data into a saved suite; no model calls.", new()
            { ["template"] = Template(), ["csv"] = Text("CSV rows including the header.", 1000000), ["name"] = Text("Saved suite name.", 200) }, ["template", "csv", "name"]),
            Tool("list_lifecycles", "Build then test", "lifecycle", "Read saved project-preparation and app-testing profiles.", new(), [], true),
            Tool("save_lifecycle", "Save build then test", "lifecycle", "Save an explicit build/preparation workflow using the selected project and saved test.", new()
            { ["lifecycleId"] = Id("lifecycle"), ["name"] = Text("Workflow name.", 200), ["testId"] = Id("test"), ["exe"] = Text("Target executable after preparation; may be produced by the build."), ["instructions"] = Text("Preparation instructions.", 16000), ["probe"] = Probe(), ["mode"] = Mode(),
                ["args"] = Schema.Array(Text("Literal target argument."), "Arguments for the target application.",64),
                ["maximumPreparationTurns"] = Schema.Integer("Preparation model turn budget.",2,80,20), ["preparationTimeoutSeconds"] = Schema.Integer("Preparation deadline.",1,7200,600),
                ["workerTimeoutSeconds"] = Schema.Integer("Testing deadline.",1,7200,900), ["startupTimeoutSeconds"] = Schema.Integer("Target startup deadline.",1,120,20), ["shutdownGraceSeconds"] = Schema.Integer("Target graceful stop period.",1,30,3)
            }, ["name", "testId", "exe", "instructions"]),
            Tool("run_lifecycle", "Build then test", "lifecycle", "Start the saved preparation and UI workflow using Cortex's model; returns a cancellable task.", new() { ["lifecycleId"] = Id("lifecycle") }, ["lifecycleId"], usesModel: true),
            Tool("delete_lifecycle", "Delete build then test", "lifecycle", "Remove a saved build-and-test profile; previous run evidence remains.", new() { ["lifecycleId"] = Id("lifecycle") }, ["lifecycleId"]),
            Tool("list_machines", "Virtual machines", "machines", "Read Hyper-V inventory without installing or elevating anything.", new() { ["deep"] = Schema.Boolean("Check existing guest readiness.", false) }, [], true),
            Tool("check_machine", "Check virtual machine", "machines", "Check the already configured Hyper-V guest.", new() { ["machineId"] = Id("machine") }, ["machineId"], true),
            Tool("setup_machine", "Prepare virtual machine", "machines", "Stage Testy's worker in an existing configured guest. Requires existing Hyper-V rights and separately stored guest credentials; never elevates.", new() { ["machineId"] = Id("machine") }, ["machineId"]),
            Tool("forget_machine", "Forget guest sign-in", "machines", "Remove the current user's stored credential for a guest; staged files remain.", new() { ["machineId"] = Id("machine") }, ["machineId"]),
            Tool("queue_machine", "Queue a guest test", "machines", "Queue a saved test on an existing guest. AI uses Cortex's model through the authenticated PowerShell Direct relay; replay needs no model.", new()
            { ["machineId"] = Id("machine"), ["testId"] = Id("test"), ["exe"] = Text("Guest executable path."), ["mode"] = Mode(), ["probe"] = Probe() }, ["machineId", "testId", "exe"], usesModel: true),
            Tool("workspace_backup", "Back up Testy data", "data", "Create a verified archive of this Testy workspace.", new() { ["archive"] = Text("Destination archive path.") }, ["archive"]),
            Tool("workspace_restore", "Restore Testy data", "data", "Verify and restore an archive into a new empty directory. Never overwrites this active workspace.", new()
            { ["archive"] = Text("Archive path."), ["destination"] = Text("New empty destination directory.") }, ["archive", "destination"]),
            Tool("workspace_check", "Check Testy data", "data", "Read workspace counts and verify saved test definitions without changing files.", new(), [], true)
        ];
    }
}
