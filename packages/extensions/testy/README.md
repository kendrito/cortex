---
description: "Integrated Windows application testing with a shared workspace and Cortex's selected model."
kind: "package-reference"
---

# @cortex/testy

## Summary

Testy provides a testing workspace inside Cortex, native MCP tools in chats, and a packaged Windows automation engine. Tests, application inspection, run evidence, recording, test sets, and automation use the same local workspace from both entry points. The package owns its browser contribution, engine process, model bridge, and tool registrations, so disabling Testy removes them together.

## Use this package

Testy is enabled in the Windows x64 Web and Desktop profiles. Open **Testy** in the sidebar, describe a workflow, and select **Test with AI**. Cortex discovers the relevant application, writes a test, runs it, and opens the evidence. **Create test** produces a draft for review first. When the application is unclear, Testy asks a focused question and offers the discovered candidates before taking action.

The Studio layout retains Testy's test library, persistent description composer, sentence-based steps, focused step editor, results, and automation views. **Options** holds activity, JSON import, application context, technical details, refresh, and Native Studio. **Inspect** also offers manual application selection; an explicit override remains visible in the status bar until cleared through application context. The main testing flow has no demo launcher or application-selection header. Saved tests default to AI-guided execution, with **Exact replay** beside the Run button. **Test actions** holds export, save a copy, and deletion while Save changes stays visible.

Results retain loaded history while refreshing. Use **Load older runs** to continue through earlier evidence and **Download report** to save a readable HTML report with its screenshots included. **Native Studio** opens the packaged Testy interface against the same workspace and selected Cortex model for specialized conveniences such as external recording imports, virtual-machine sign-in, and advanced lifecycle scheduling.

AI runs, drafting, refinements, and explanations use Cortex's configured model. The selected chat supplies its model and workspace even when it is idle or restored from disk. Without a selected chat, the testing pane uses the current Cortex default. Integrated Testy has no separate provider credentials or model configuration.

Use **Settings → Built-in plugins → Testy** to disable or enable the plugin. Disabling stops owned testing work and revokes the private model bridge. Saved tests and evidence remain on disk. Re-enabling starts a fresh engine against the same workspace.

If the engine disconnects, its status retains the failure and any available exit code, signal, and bounded credential-redacted diagnostics. Status remains readable with a selected chat. Disable and re-enable Testy to start a fresh engine; interrupted operations are never replayed automatically.

Recoverable MCP diagnostics, including late responses to settled requests, are logged with the same redaction and size bounds. They do not terminate a healthy engine.

By default, data lives in `$CORTEX_HOME/testy/workspace`. The source Testy project's personal workspace is not copied. Config can select another `workspace` explicitly. Backups and restore are available in the testing pane; restore writes to a new empty directory.

Opening native Studio through Cortex initializes an empty test library without starter examples. Existing saved tests and evidence are retained. Standalone Testy keeps its original first-launch examples; the integrated operation picker omits the demo-app launcher.

### Build and distribution

The complete engine source is under `native/testy`. Windows x64 `pnpm run build` runs `build:testy`, which publishes the Studio, CLI, worker, probe dependencies, and three sample applications into this package's `runtime/win-x64` directory. A release contains the .NET runtime and needs no separate .NET installation. Building from source needs the .NET 9 SDK; set `CORTEX_DOTNET` when it is outside `PATH`. Source fingerprints and release file hashes prevent reusing stale native outputs.

The runtime is a package payload, not a reference to another checkout. The generated `release-manifest.json` records the native release files. No Windows service, scheduled startup task, or elevated supervisor is installed when the plugin starts.

Desktop packages keep the complete native runtime outside ASAR, including its JSON files and scripts, and verify the packaged bytes against the prepared payload.

## Understand the implementation

The host starts one owned stdio MCP process with a scrubbed environment. The UI uses Cortex's authenticated Remote API; model tools use the normal tool registry and persisted tool results. Testy's own process-scoped driver and assertion evaluator remain responsible for UI actions and test verdicts. Revision checks protect saved tests from concurrent UI and chat edits.

The MCP client identifies itself as Cortex Testy with the manifest-derived Cortex product version.

AI requests reach a bearer-authenticated, loopback-only bridge with a host-issued operation context. The bridge dispatches through `ctx.llm`, admits screenshots through Cortex's attachment store, and logs exact requests and settlements in a dedicated, durably stored Cortex Session. Provider credentials stay in Cortex. Child-supplied provider routes, remote image URLs, and unsupported request fields are rejected.

Automatic discovery selects only from a bounded inventory of visible processes and executables found under the trusted initiating workspace. The host supplies that workspace as MCP metadata; tool arguments cannot replace it. The engine validates a selected process's identity again before attaching. Ambiguous or invalid selections perform no application actions.

Configuration controls the executable and workspace paths, operation deadlines, maximum message size, output-token ceiling, and model-context lifetime and count. The executable override is for an administrator-provided engine; model tools cannot change it. Tools that grant project-command access or change privileged configuration belong to the human UI, and the executor rejects model access.

No invariant companion is published: backend lifecycle and registrations share one owning plugin transaction, while model request/settlement behavior is tested through real Session persistence. There is no independent invariant registry projection to reconcile.

## Model Experience

### Enabled Testy operations

#### What the model sees

Without the plugin, chats have no Testy tools. With it, `mcp__testy__*` tools inspect a selected application, author and manage saved tests, execute bounded actions and test runs, and retrieve evidence. Runtime discovery supplies the native engine's tool descriptions and schemas. Results include application observations, saved test definitions, execution outcomes, and requested evidence.

#### Token effect

Testy descriptions and schemas add prompt tokens. Screenshots consume image input on capable routes. AI testing makes additional requests through the initiating Cortex model; deterministic replay requires no model calls unless live AI review is enabled. The configured output ceiling bounds each auxiliary model response.

#### KV Cache effect

The tool catalog stays stable for the engine's lifetime. Testy auxiliary requests have their own ordered observation history and do not rewrite the initiating chat's message prefix. Switching or disabling the plugin changes the available tool declarations through the normal registry lifecycle.

## Known Limitations and Deferred Work

- The native engine requires Windows x64 and an unlocked interactive desktop. The WPF probe requires explicit target integration. Hyper-V operations require the user's existing Hyper-V setup, guest credentials, and an interactive guest desktop. Virtual-machine execution is not exercised by local sample-app checks.

- Testy serializes its own desktop actions. Other computer-use programs can still act on the same desktop; keep unrelated automation idle during tests. Cancelling input cannot undo actions already delivered to an application.

- Evidence and model audit Sessions remain local, while AI test observations are sent to the model provider already configured in Cortex.
