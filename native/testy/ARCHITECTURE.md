# Testy Windows testing studio

A native .NET 9 WPF desktop studio. Tests are portable JSON, and model-generated plans are editable before execution. By default the LLM chooses each next action from a fresh screenshot, control tree, and prior results. The runner independently verifies ordered coverage of the saved workflow and its exact assertions before accepting a pass. Deterministic replay is an explicit alternative.

## Components

- `Testy.Core`: shared contracts, persistence, AI planning, local/native computer-use loops, saved-workflow verification, and the `TestExecutionService` used by Studio to select AI execution or explicit replay.
- `Testy.Windows`: process-scoped UI Automation driver and optional WPF probe client.
- `Testy.WpfProbe`: opt-in in-process WPF visual-tree inspection using a current-user named pipe.
- `Testy.TestLab`: a real WPF fixture with customer creation, validation, search, delayed status and a deliberate defect.
- `Testy.OrderLab`: an independent WinForms fixture with repeated control names, dynamic control recreation, modal confirmation, delayed inventory and a deliberately incorrect total.
- `Testy.WpfLab`: a WPF fixture with 2,000 virtualized grid/list items, nested trees, sorting/filtering, column reordering, typed cell values, validation and a modal.
- `Testy.Studio`: native desktop GUI, test library, step editing, live inspection, screenshots, run history and AI authoring.
- `Testy.Cli`: CI execution and desktop integration verification. It also owns queue pumps with execution targets (`local` or `vm:<guid>`) and the Hyper-V bridge (`hyperv\Testy.HyperV.ps1`, `guest-run.ps1`) that runs saved tests inside a VM's signed-in desktop over PowerShell Direct.
- `Testy.Agent`: tray app started elevated at sign-in by a per-user scheduled task. It supervises short-lived CLI pumps per ready target, refreshes the Hyper-V inventory and never claims work itself. See [agent](docs/AGENT.md) and [Hyper-V](docs/HYPERV.md).
- CLI maintenance: supported UIA event recording, model drafting with immutable explicit acceptance, and whole-value test-data materialization.
- CLI worker: configuration/desktop preflight, fresh owned target and CLI child, Windows job containment, bounded watchdog, durable terminal result and factual benchmark export.
- `Testy.Tests`: dependency-free deterministic contract and runner tests.
- `Testy.WindowsChecks`: physical input, modal, target-boundary, and screenshot-quality checks against the owned fixture.

Windows UI Automation attaches without injecting code. The optional probe requires a one-line startup integration in the owned WPF application. This does not claim to inject arbitrary CLR runtimes into arbitrary binaries.

Shared core features are independent of model hosting: exact composite/ancestor selectors, typed observations for failed assertions and interrupted actions, explicit standard control operations, read-only logical item lookup, and sequential suites with frozen test definitions and per-attempt artifacts. Bound local tools accept saved step IDs; native OpenAI execution combines physical computer input with explicitly named saved assertion and advanced-control tools. The CLI suite command selects the existing provider adapter or requires an explicit replay flag. JSON, HTML and JUnit preserve terminal failures and cancellation even when all previously observed steps passed. See [suites](docs/SUITES.md), [WPF capabilities](docs/WPF-CAPABILITIES.md), and [failure diagnostics](docs/FAILURE-DIAGNOSTICS.md).

Only the explicitly attached process may receive UI actions. Coordinate input is window-relative and checks foreground ownership. Model-supplied code, executable paths and shell strings are not accepted. When explicitly enabled, project tools also send bounded, redacted observations from the selected local repository and results of host-configured command IDs to the provider. Configured programs run with the user's privileges; a Windows job controls their lifecycle but is not a security sandbox.

ProjectToolSession provides bounded listing, UTF-8 reading, literal search and one-shot configured commands. The command executor creates an owned Windows job, constrains inherited environment/handles, drains output, applies deadlines, and verifies descendant cleanup. The model must choose required command IDs before UI execution; failures block further UI actions. Every command invalidates the observed UI state. ProjectAwarePlanner runs a tool loop before accepting a structured draft; project evidence remains distinct from executed UI steps in all run coverage checks. WorkspaceStore retains explicit selection across existing Studio connection saves. Workers compare target build fingerprints before/after execution and reject changed attached binaries; prelaunch AI build orchestration is not implemented.

For approved selector recovery, the LLM chooses one authored alternative before execution. The engine proves that the original target is absent in a complete relevant scope, verifies the alternate's exact exposed identity, and dispatches under a fresh driver guard. The result retains the original saved step plus separate recovery evidence. Failed or uncertain mutations cannot be retried through another alternative. Native physical mode currently rejects tests containing recovery alternatives.

The probe's editable grid path is an explicit integration: standard DataGrid, registered column keys, stable IGridRowIdentity keys and IEditableObject rows. Editing, committing and cancelling are separate decisions. Validation and classified application diagnostics are observations; a successful commit operation alone cannot prove business persistence. Independently authored assertions provide that evidence.

Each worker receives immutable test/configuration copies and owns only the processes it creates. The target is assigned to its Windows job at process creation; the CLI child waits for a start signal until containment is established. On cancellation or deadline, the worker signals cooperative stop, allows bounded grace, then terminates owned processes and verifies the job is empty. An interrupted operation remains uncertain. Worker success requires terminal, matching persisted evidence and canonical workflow coverage. This is interactive execution on this PC or inside Hyper-V guests of this PC, coordinated by the background agent; there is no multi-host scheduler or unattended Session0 desktop.
