# Testy · Windows test studio

A native Windows desktop application for authoring UI tests with AI, attaching to applications, running repeatable assertions, and explaining failures from recorded evidence.

## Formerly Axiom

Testy was previously named Axiom. The apps (`Testy.Studio.exe`, `Testy.Cli.exe`, `Testy.Agent.exe`), `%LOCALAPPDATA%\Testy`, `C:\Program Files\Testy` and the VM folder `C:\ProgramData\TestyAgent` all use the new name. Data from before the rename stays where it was: the old default workspace remains at `%LOCALAPPDATA%\Axiom\Workspace` (open it with **Automate → Data & backup → Open another data folder…**). Lifecycle profiles, workspace backups and benchmark entries saved as Axiom still load, and API keys and VM sign-ins stored under the old name are still found. Reports in `verification/` that were recorded before the rename keep the old name.

## Start here

The redesigned Studio is connected to the live AI runner. **Run uses AI-guided execution by default:** the model chooses one action, receives fresh application evidence, and decides again. See [current verification results](verification/FUNCTIONALITY-RESULTS.md) for completed checks and remaining provider and desktop limitations.

[How the agent loop works](docs/AGENT-LOOP.md) explains test creation, execution, acceptance checks and the current boundary around local project and command access.

Version 0.6 adds [Automate](docs/PRODUCT-WORKFLOWS.md) to Studio: project setup, AI preparation before launching the target EXE, suites and CSV data, demonstration drafts, durable local jobs/schedules, secure credential onboarding, and verified workspace backup/restore. [Lifecycle evidence](docs/LIFECYCLE.md) keeps preparation results separate from UI acceptance. [Installation and update tooling](docs/INSTALLATION.md) verifies sealed packages and preserves a prior version for rollback. Studio has the native Windows 11 look (Fluent controls, Mica and your Windows accent color) and follows the system light or dark theme.

The [project-aware AI tools](docs/PROJECT-TOOLS.md) support selected-repository listing, source reading/search, and explicitly configured build/test commands. The model chooses each tool, receives its actual result, and still has to execute the saved UI workflow. Required commands and independent UI assertions prevent false passes. Project settings saved in **Automate → Source code access** apply as soon as you leave Automate.

Version 0.4 introduced supported WPF grid editing and commit/cancel operations, [explicit selector recovery](docs/RECOVERY.md), [isolated interactive workers](docs/WORKERS.md), and [demonstration-based authoring and test data](docs/AUTHORING-WORKFLOWS.md). See the current verification report for measured coverage and limits. The [earlier verification report](verification/FINAL-RESULTS.md) describes the historical baseline release.

Launch **`dist/Testy/Testy.Studio.exe`**. The portable x64 release includes the .NET runtime; installation is not required. Keep the files in its folder together. The executable is unsigned, so Windows may identify it as an unrecognized application.

1. Click **Sample app** in the bar at the top. Testy opens the included Customer Desk WPF app and connects to it.
2. Select **Create a customer** in **Tests**, then **Run** (F5). This exercises real text fields and a button, then checks the actual success text. The dropdown beside Run switches between **AI-guided** and **Exact replay** (the same setting as in Settings).
3. The test opens on **Last run** while it runs: the run trace shows where the time went, each step says what Testy found and did ("Found Add customer button, clicked it"), and the screenshot of the selected step outlines the control Testy used. **Results** in the navigation pane lists every run; **View run** opens one on Last run.
4. Describe a workflow under **New test from a description**, below the test list, and click **Create test**. The default service is the authenticated Codex CLI. Review the created steps and run them.
5. Use **Improve…** above the steps to revise the current test. If it has a recorded run, the assistant also receives its result and failure messages. On Last run, **AI notes › Explain this run** adds an explanation of that run.

**Activity** in the top bar lists every message, save, run and error of the session (each entry is also written to `%LOCALAPPDATA%\Testy\Logs\studio-YYYYMMDD.log`). **Ctrl+K** searches test names, purposes and steps; **F1** lists all keyboard shortcuts.

**Failure evidence example** intentionally fails. Its purpose is to demonstrate a real failed assertion, screenshot, observed text, and explanation. Testy does not rewrite the expected result to make a failing test pass.

For your own app, open **Change app** at the top and choose **Open an app…**, or pick a running window and click **Connect**. Use a dedicated test instance. Only the connected app receives test actions. Standard attachment does not require changes to the target application.

## What is implemented

- WPF desktop studio with a persistent test library (grouped, with each test's latest result), natural-language authoring, steps shown as sentences with a step editor (action, control, value, reordering, timeouts), JSON import/export, a **Last run** view (run trace, per-step runner messages and outlined screenshots), an **Inspect controls** page, **Results** (run history), an **Activity** log and global search. **Show technical details** reveals selectors, raw actions, process IDs, run IDs, JSON and raw runner messages and diagnostics.
- Process-scoped Windows UI Automation with automation-ID, name, structural-path and exact composite selectors scoped by ancestor controls. See [selector syntax](docs/PROBE.md).
- An opt-in in-process WPF probe for visual-tree inspection and actions through the target's dispatcher.
- Explicit expansion, item realization, scrolling, typed property assertions, and read-only logical item lookup for supported WPF/UIA controls. Unsupported and unavailable observations remain distinct from actual values.
- Real screenshots and control snapshots are captured after each executed step, including failures, when Windows exposes the evidence. Unavailable capture fails the run. Studio's Last run view (run trace and screenshots) plus HTML, JSON, and JUnit reports preserve the results.
- Structured [failure diagnostics](docs/FAILURE-DIAGNOSTICS.md) preserve observed expected/actual values, evidence, last successful steps and uncertain input outcomes, separately from model hypotheses.
- An AI-directed runner: the LLM chooses one action, receives its actual screenshot and control state, and decides the next action. Saved acceptance assertions cannot be weakened or skipped to obtain a pass.
- A separate deterministic replay runner with assertion retries, one-shot input actions, cooperative cancellation, fail-fast behavior, and explicit skipped steps. Missing evidence is a failure, not a silent success.
- Codex CLI planning and explanations using its existing authentication, without an API key.
- OpenAI's native Responses `computer` tool protocol for supported models, with guarded local Windows input, independent assertions, and an explicit saved-control tool for advanced WPF operations.
- Local computer-use loops for compatible Chat Completions models, OpenAI custom-tool mode, and the authenticated Codex development bridge. The model governs every next action from fresh observations.
- Optional per-step AI reviews in replay mode. This commentary-only feature is separate from AI-directed execution.
- A CLI suitable for CI workers with an interactive Windows desktop and a real WPF fixture for verification.
- Sequential, repeatable [functional suites](docs/SUITES.md), with model-directed execution or explicit replay, fail-fast behavior, and aggregate HTML/JSON/JUnit reports.
- Opt-in standard WPF DataGrid edit/commit/cancel transactions with stable application-provided row keys, exact column keys, validation observations and separately authored persistence assertions.
- AI-selected, preauthored selector alternatives with same-process identity guards and explicit recovery evidence. The saved action and acceptance remain unchanged.
- CLI recording of supported UI Automation events, AI drafting with explicit acceptance, and literal parameterized test matrices.
- A bounded local worker that owns its test processes, reports terminal outcomes, cleans up its Windows job and exports measured benchmark records.
- Scoped project evidence with source line/file identity/hash citations, redacted command output, fixed command catalogs, required prerequisites, and separate durable evidence in authoring/run reports.

## Model connections

Open **Settings** at the bottom of the navigation pane.

**Let the AI guide each run** is on by default. Turn it off explicitly for exact (deterministic) replay. The same page configures OpenAI computer-use mode, screenshots sent to the AI, the most AI actions per run (up to 240 local / 80 native), and optional AI comments during replay. Click **Save** to apply changes. Tests, target selection, and provider settings are locked during an active operation; **Stop** cancels it cooperatively. Keys may be stored in Windows Credential Manager for the configured endpoint, with an environment-variable fallback.

**Codex** is the development bridge. The default executable setting checks PATH and the Codex desktop installation under `%LOCALAPPDATA%\OpenAI\Codex\bin`; select an explicit `codex.exe` path for another installation. Sign in with `codex login` outside the studio if necessary. Leave the model blank to use the CLI default. Planning and next-action calls run in fresh job directories with tools disabled and explicit JSON schemas. The live loop sends the current control tree and completed action results before every next decision, plus the latest screenshot when **Send screenshots to the AI** is on. Disabling that option retains screenshots locally for evidence. Testy executes the returned local tool action. Codex is replaceable; test files and desktop drivers have no dependency on it.

**OpenAI** uses a configurable Responses endpoint, model, and API-key environment variable. The default endpoint is `https://api.openai.com/v1/responses`. No key is bundled or written to settings. Set the environment variable before launching Testy. With `nativeComputerUse: true`, a supported model returns native `computer_call` actions and Testy returns matching `computer_call_output` screenshots. Testy translates actions into scoped Windows input. With native mode disabled, the same provider uses Testy's semantic local computer tools. Structured planning is also available for authoring saved tests.

**Compatible** uses an OpenAI-compatible Chat Completions endpoint with structured JSON and local function calling. Configure its endpoint and model. Set `supportsImages: false` for a text-only model; it still receives the actual control tree and action results. HTTPS is required except for loopback servers. A key is optional for local servers. Support for a provider is not a claim that every model supports the same features.

**OpenRouter** uses the Compatible provider with endpoint `https://openrouter.ai/api/v1/chat/completions` and API variable `OPENROUTER_API_KEY`. The supplied `examples/provider-openrouter.json` selects GPT-4.1 Mini with AI-directed local tools and screenshots. Testy reads the named process environment variable first, then the Windows user variable when the process value is absent. See [OpenRouter setup](docs/OPENROUTER.md) for model options and the distinction from native Responses computer use.

**Offline** is a limited command parser, clearly labeled as non-AI. Disable **AI decides every action** to run its saved steps as replay. Use one command per line:

```text
click id:ResetButton
type "Ada Lovelace" into id:CustomerName
type "ada@example.test" into id:CustomerEmail
click id:AddCustomer
assert text id:StatusMessage = "Customer added: Ada Lovelace"
screenshot
```

AI requests include the selected application's observed control tree and, where supported, screenshot evidence. Treat test input and screenshots as potentially sensitive. Password fields are redacted in control snapshots and cannot be recorded as text-entry or text-assertion steps. Screenshots may still show other application data.

See [COMPUTER-USE.md](docs/COMPUTER-USE.md) for the native and local protocols, one-action feedback loop, immutable assertion verification, budgets, and cancellation. [FRONTEND-HANDOFF.md](docs/FRONTEND-HANDOFF.md) documents the backend interface used by Studio.

## Let your AI tools use Testy (MCP)

`Testy.Cli.exe mcp` is a built-in [Model Context Protocol](https://modelcontextprotocol.io/) server. Any MCP-capable agent harness on the same PC can start it (stdio, or `--transport http` on loopback) and do what Testy's own AI does: inspect an app's controls and screenshot, perform single UI steps, create and run saved tests, and read the evidence. An agent can name the app the way a task does ("In Customer Desk, add a customer…"): `find_app` and the `app` argument resolve the name among running windows, installed apps, earlier tests' apps and the sample apps (an ambiguous name is answered with the candidates, never guessed), and a test created with `app` remembers it, as a test created in Studio from a description that names its app does. Long runs answer within about 45 s (`status: "running"`), and `get_run` with `waitSeconds` waits for the result. Tests and runs land in the same workspace, so they show up in Studio, which also shows the command, tests the connection and can keep an HTTP listener running (Settings › Agent access). The server can start desktop programs and drive them with the user's rights, so treat any agent that reaches it as an operator of this desktop; it starts only GUI `.exe` programs from local paths, refuses shells and script hosts, binds loopback only, and takes a bearer token (`TESTY_MCP_TOKEN`) over HTTP. `Testy.Cli.exe mcp --describe` prints a machine-readable manifest, and the bundle ships `mcp-server.json` and `AGENT-ACCESS.md` for discovery. See [docs/MCP.md](docs/MCP.md).

## WPF attachment and probe

Windows UI Automation works externally with accessibility controls exposed by WPF, Win32, and other compatible applications. It cannot guarantee access to every custom-drawn control or privileged application.

For a WPF application you own, reference `Testy.WpfProbe` and start `Testy.WpfProbe.ProbeServer.Start()` after application startup. Then turn on **Use the WPF helper** under **Change app** before connecting. See [PROBE.md](docs/PROBE.md) for the full integration and exact fixture controls.

This release does **not** inject a CLR or arbitrary DLL into an unmodified executable. It offers external attachment and an explicit in-process integration. Applications using older .NET Framework runtimes need a compatible probe build; the supplied probe targets .NET 9.

## Tests, selectors, and reports

Selectors are case-sensitive: `id:CustomerName`, `name:Add customer`, the inspector's `path:…`, or an exact ancestor-scoped `query:`. Prefer unique automation IDs; use [composite selector syntax](docs/PROBE.md) for repeated controls. Ambiguous matches fail. Structural paths can change when layouts change.

Actions: `Click`, `TypeText`, `Select`, `Toggle`, `KeyPress`, `CoordinateClick`, `Wait`, `Screenshot`, `AssertExists`, `AssertNotExists`, `AssertEnabled`, `AssertText`, `Expand`, `Collapse`, `RealizeItem`, `ScrollIntoView`, `ScrollPercent`, `AssertProperty`, `AssertItemExists`, `AssertItemAbsent`, `GridEditCell`, `GridCommitRow`, and `GridCancelRow`. Advanced value contracts are described in [WPF capabilities](docs/WPF-CAPABILITIES.md).

`TypeText` replaces a field's value. `AssertText` compares exact case-sensitive text; use `contains:` before the expected text for substring matching. `Toggle` accepts `On`/`Off`. Coordinate input uses physical pixels relative to the composed screenshot of the target process's visible windows. Empty gaps and foreign windows are rejected. Assertions poll for the configured timeout; mutations are never retried automatically.

Each run folder includes `run.json`, `report.html`, `junit.xml`, and step PNG/JSON files. Open an HTML report in any browser. The JSON schema is represented by the public Core models and the examples folder.

By default, the library and evidence live under `%LOCALAPPDATA%\Testy\Workspace`. Launch with `--workspace "C:\path\to\workspace"` to choose another location. Drafts can be saved before all steps are valid; execution requires validation. Library/settings and run JSON use atomic replacement; screenshot and HTML evidence are separate files.

## Build and verify

Source requires Windows and the .NET 9 SDK. There are no third-party NuGet dependencies in the application or test harness.

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
.\scripts\publish.ps1
```

Use `-DotNet "C:\path\to\dotnet.exe"` to choose a specific SDK. `publish.ps1` defaults to a portable Windows x64 release; use `-FrameworkDependent` for a smaller build requiring the .NET 9 Windows Desktop Runtime.

`build.ps1`, `test.ps1`, and `publish.ps1` accept `-BackendOnly` to leave Studio untouched. Backend publishing writes to `dist/Testy.Backend`; adjust the CLI paths below accordingly. Add `-IncludeDesktop` to `test.ps1` to run the real fixture suites, including the native-input protocol integration. A Windows focus refusal is reported as a failure and makes that script exit unsuccessfully.

After a self-contained publish, `scripts/verify-package.ps1` launches the packaged CLI and fixture without an external runtime path and verifies customer workflows with both drivers. It intentionally rejects framework-dependent packages. `scripts/archive-package.ps1` creates the full ZIP with a SHA-256 sidecar for distribution. Both scripts default to `dist/Testy`; use `-PackageDirectory ./dist/Testy.Backend` for the backend-only bundle.

CLI examples:

```powershell
.\dist\Testy\Testy.Cli.exe list-targets
.\dist\Testy\Testy.Cli.exe inspect --pid 1234
.\dist\Testy\Testy.Cli.exe run --pid 1234 --test .\examples\create-customer.json --artifacts .\artifacts
.\dist\Testy\Testy.Cli.exe run-ai --pid 1234 --test .\examples\create-customer.json --settings .\examples\provider-codex.json --artifacts .\artifacts
.\dist\Testy\Testy.Cli.exe verify-lab --exe .\dist\Testy\Testy.TestLab.exe --artifacts .\verification\desktop
```

Exit codes: `0` passed, `1` failed/cancelled, `2` configuration or driver error. Desktop execution requires an unlocked interactive session with matching privilege level; a Windows service in Session 0 is not a desktop test worker, which is why the background agent is a sign-in app. `enqueue-test` queues a saved test for this PC or a VM (`--target vm:GUID`); `vms` and `vm-run` manage and run Hyper-V guests (elevated). See [TESTING.md](docs/TESTING.md) and the included `verification` reports for executed checks.

## Background agent and VMs

Optional: **Automate → Background runs → Enable at sign-in…** installs the [background agent](docs/AGENT.md). It starts elevated at sign-in, sits in the tray, and runs queued and scheduled tests with Studio closed. It waits for an unlocked, idle desktop before local runs, and never fails a job because the screen was locked. The **Virtual machines** tab lists Hyper-V VMs; after a one-time setup per VM it runs saved tests on the VM's signed-in desktop, with the AI working inside the VM, and copies the evidence back ([Hyper-V testing](docs/HYPERV.md)).

## Current boundaries

This is a working preview and an extensible foundation, not a claim of complete TestComplete parity. There is no arbitrary runtime injection, browser DOM driver, multi-host worker scheduler, general desktop gesture recorder, or visual-diff baseline management. The background agent reaches only VMs on its own Hyper-V host. Installation/update/signing tools are supplied, but this development release is unsigned. The AI can inspect an explicitly selected repository and choose configured commands; it cannot browse arbitrary local files or invent shell commands. Coordinate operations and screenshot capture depend on the target's Windows behavior; protected, minimized, custom-rendered, or higher-privilege windows may reject them. UI Automation calls inside third-party native providers cannot always be interrupted after dispatch; the worker watchdog bounds the owned child process without retrying uncertain input. See the [dynamic compatibility boundary](docs/TESTCOMPLETE-CAPABILITY-GAPS.md).

Codex and the selected OpenRouter configurations have live authenticated verification. See [OpenRouter results](verification/OPENROUTER-RESULTS.md) for the exact models and workflows exercised. Direct OpenAI Responses/native computer use has protocol fixtures and historical real local input checks, but has not been called with a direct OpenAI API key. Current physical-input outcomes, including environment blocks, are recorded separately in the [current verification report](verification/FUNCTIONALITY-RESULTS.md). Other models and endpoints need their own compatibility checks.

## Reference documentation

- [OpenAI computer use](https://developers.openai.com/api/docs/guides/tools-computer-use): the application supplies the environment and executes model requests, including custom UI-tool interfaces.
- [Codex non-interactive mode](https://learn.chatgpt.com/docs/non-interactive-mode): structured outputs and automated CLI invocation.
- [Windows UI Automation](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-overview): programmatic access to desktop UI elements.

## Source map

`Testy.Core` owns contracts, storage, AI adapters, tools, and the runner. `Testy.Windows` owns native attachment. `Testy.WpfProbe` runs inside an opted-in WPF app. `Testy.Studio` is the GUI. `Testy.Cli` provides CI commands and integration verification. `Testy.TestLab`, `Testy.OrderLab`, and `Testy.WpfLab` are the owned fixtures; `Testy.Tests` is the deterministic test harness.
