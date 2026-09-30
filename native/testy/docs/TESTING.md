# Verification

Testy has a dependency-free test executable and real Windows desktop verifiers. They start the included Customer Desk, WinForms OrderLab or advanced WPF fixture, attach only to the owned fixture process, write evidence, and close only the processes they started. Verification results are distinct from the intended behavior described below; see the [current release results](../verification/FUNCTIONALITY-RESULTS.md) for completed checks and environment blocks.

## Run the checks

Use a Windows machine with .NET SDK 9 and an unlocked interactive desktop. In PowerShell from the Testy directory:

```powershell
./scripts/test.ps1
./scripts/test.ps1 -IncludeDesktop
# If dotnet is not on PATH:
./scripts/test.ps1 -DotNet 'C:/path/to/dotnet.exe' -IncludeDesktop
# Build and verify backend/fixture code without building or operating Studio:
./scripts/test.ps1 -BackendOnly -IncludeDesktop
```

The script builds the Studio, CLI, WPF TestLab, WinForms OrderLab, advanced WpfLab and test harnesses. `-BackendOnly` excludes Studio and its UI verifier; this is useful while another developer is editing the frontend. Without `-IncludeDesktop`, it runs the deterministic harness, offline screenshot-quality/composite-publication regressions and authoring/data-integrity checks; it does not operate a desktop application. Test results use nonzero process exits on failure.

After build and offline checks pass, independent desktop verifiers all run even if one fails or encounters a Windows focus block. `verification/desktop-suite-report.json` retains every result, remains unsuccessful until completion, and the script exits unsuccessfully if any check failed. A blocked physical-input check cannot prevent later WPF or Studio checks from running, or be counted as a pass. The WPF phase also audits saved snapshots to verify that read-only logical lookups preserve realized item/cell identities.

## Deterministic checks

The harness covers action ordering and exactly-once dispatch, per-step screenshots and snapshots, assertion failure and fail-fast behavior, eventual assertions, missing-selector timeouts, cancellation, capture failure, HTML escaping, saved tests/runs/provider settings, atomic replacement, identifier path traversal, malformed files, schema validation, structured-plan parsing, hostile generated actions, Unicode round trips, incomplete drafts, step review callbacks, and truncated tree rejection. Mock HTTP checks cover Responses and compatible provider payloads, strict schemas, password redaction, invalid output, credentials and transport restrictions. Local computer-tool tests cover allowed operations, argument validation, target locking and call limits. A fake driver is used for these checks; this evidence is distinct from real desktop verification.

Codex-specific regressions verify that disabling model images omits the image argument while retaining local evidence, and that executable resolution respects explicit configuration, PATH, and bounded official desktop-install fallbacks. These checks do not read authentication files or make model calls.

Additional functionality regressions cover exact composite and ancestor selectors, reordered duplicate controls, suite preflight validation, fail-fast repetitions, terminal CI outcomes, factual failure diagnostics, and uncertain input completion when a driver ignores cancellation. A timed-out invocation may still finish later; the test verifies that the recorded failure is not converted into success and no retry occurs.

After a Release build, `pwsh -File ./scripts/verify-report-guards.ps1` runs nine offline checks against the compiled CLI's verification-report completion gates, including partial success and cancellation during cleanup. This optional helper requires PowerShell 7.5 or newer because it loads .NET 9 assemblies into the host; it launches no desktop application and makes no model calls. The ordinary build/test scripts and saved-evidence realization audit remain usable from Windows PowerShell 5.1.

## Repeated cross-application checks

`-IncludeDesktop` runs `verify-functionality` for three repetitions on two independent owned fixture applications: a WinForms order form through UI Automation, and the WPF Customer Desk through both UI Automation and its probe.

```powershell
.\Testy.Cli.exe verify-functionality --orders .\Testy.OrderLab.exe --lab .\Testy.TestLab.exe --repetitions 3 --artifacts .\verification\functionality
```

Nineteen scenarios per repetition exercise duplicate names/IDs with ancestor-scoped selectors, recreated controls with changed runtime identities, modal confirmation, delayed state, disabled or missing controls, wrong exact expectations, intentional product defects, and cancellation. Negative scenarios pass verification only when the application test fails as expected and later mutations remain unexecuted. Capture checks inspect client pixels and require trees and reports. The JSON aggregate distinguishes planned, completed, expected-failure and cancelled scenarios; a partial verification cannot claim success. These are replay-based driver tests with zero model calls. They do not establish general compatibility with third-party applications or virtualized controls.

The source `scripts/verify-suite.ps1` additionally exercises the actual CLI suite command against an owned WPF fixture, covering successful repetitions, failure exit codes, skipped later runs, report files and unchanged application state after a failed assertion. See [suite usage](SUITES.md).

## Advanced WPF checks

`verify-wpf` launches only the included `Testy.WpfLab.exe`, with 2,000 virtualized grid/list items, duplicate labels, nested trees, sorting/filtering, column reordering, validation and a modal. It runs 27 scenarios per driver per repetition. The default three repetitions schedule 162 checks through UIA and the opt-in probe:

```powershell
.\dist\Testy\Testy.Cli.exe verify-wpf --exe .\dist\Testy\Testy.WpfLab.exe --repetitions 3 --artifacts .\verification\wpf\desktop
.\scripts\verify-wpf-observations.ps1 -ReportPaths .\verification\wpf\desktop\wpf-report.json -OutputPath .\verification\wpf\realization-audit.json
.\dist\Testy\Testy.Cli.exe verify-wpf-hybrid --exe .\dist\Testy\Testy.WpfLab.exe --artifacts .\verification\wpf\hybrid-protocol
.\dist\Testy\Testy.Cli.exe verify-text-boundaries --exe .\dist\Testy\Testy.WpfLab.exe --artifacts .\verification\wpf\text-boundaries
```

The replay scenarios check read-only lookup without focus/selection/scroll changes, explicit far-item realization and scrolling, duplicate rejection, expansion/collapse, percentage scrolling, typed cell value/row/column/read-only properties, recycled container observations, disabled modal owners, unsupported properties and expected failures. The aggregate requires every planned scenario to finish, with exact expectations for negative cases. See [WPF contracts and provider limits](WPF-CAPABILITIES.md).

`verify-wpf-hybrid` uses queued **mock Responses outputs** with the production native adapter and the real WPF driver. Its eight saved steps mix two native screenshot calls, three explicit saved-control operations, and three independent saved assertions. It checks canonical step IDs, full ordered coverage, typed far-cell data and JSON/HTML/JUnit/image evidence. Every subsequent request must contain the actual newly recorded tree and screenshot. It sends no network request and no physical mouse/keyboard input, so it does not establish live OpenAI compatibility or native input delivery.

`verify-text-boundaries` checks full legacy text, clipped-prefix rejection, and the separate typed-property limit against real controls. Its optional fixture mode adds a read-only RichTextBox to exercise UIA TextPattern overflow; the normal WPF fixture tree remains unchanged. All these phases are included in `test.ps1 -IncludeDesktop`. The offline realization audit checks exact realized item/cell identities at each saved observation boundary; it cannot establish what happened between observations or inside an inaccessible provider.

## Desktop checks

Version 0.6 also includes these owned-fixture checks in `test.ps1 -IncludeDesktop`:

```powershell
.\Testy.Cli.exe verify-surfaces --exe .\Testy.WpfLab.exe --artifacts .\verification\surfaces
.\Testy.Cli.exe verify-grid-editors --exe .\Testy.WpfLab.exe --artifacts .\verification\grid-editors
.\Testy.Cli.exe verify-workflows --exe .\Testy.Studio.exe --lab .\Testy.TestLab.exe --artifacts .\verification\studio-workflows
```

The eight surface checks cover separate windows, titleless popup capture, gaps, stale layouts, foreign-process exclusion and acknowledged physical input. The popup scenario separately checks a successful raw coordinate outcome and refusal to count unacknowledged input as a saved semantic Select. Seventeen grid-editor cases exercise standard checkbox/combo and explicitly opted-in template editors. Fifteen Studio workflow cases drive the in-window Automate page (its seven tabs, leaving it by navigation, and the step options dialog) and check persisted profiles, suites, local jobs, schedules, step options and recovery/connection controls without calling a model. These verifiers do not qualify arbitrary third-party controls.

The editable WPF increment has three additional verification entry points:

```powershell
.\Testy.Cli.exe verify-enterprise --exe .\Testy.WpfLab.exe --repetitions 3 --artifacts .\verification\enterprise\desktop
.\Testy.Cli.exe verify-worker --exe .\Testy.TestLab.exe --artifacts .\verification\enterprise\workers
.\scripts\verify-authoring-desktop.ps1 -DotNet dotnet -Artifacts .\verification\enterprise\authoring
```

`verify-enterprise` runs 31 scenarios per repetition through UIA and the opted-in probe: grid editing, separate commit/cancel, invalid values, vetoed commits, delayed/failed/wrong persistence, stable row identity after view changes, explicit guarded recovery and negative identity cases. Its fixture is `Testy.WpfLab.exe --grid-workflows`; these results do not qualify third-party grids. Unsupported UIA transaction operations must reject before input. The worker verifier exercises isolated success/failure, early target exit, cancellation, watchdog termination and owned-process cleanup. The authoring helper records supported UIA events and then executes a three-case parameterized customer suite. These all use owned fixtures without model calls and are included in `test.ps1 -IncludeDesktop`.

`verify-maintenance` separately checks literal parameter binding, immutable acceptance, strict JSON, incomplete demonstrations, recorder callback draining and event-time identity. Real provider drafting and AI-governed grid/recovery execution are additional qualification steps, recorded separately in the release results.

## MCP server checks

The deterministic harness includes 54 MCP checks that drive the server in-process over in-memory streams and the HTTP transport on a free loopback port (plus the write ledger and debounce rules Studio's workspace watcher uses to tell its own saves from outside changes): version negotiation for every handshake revision and the stateless 2026-07-28 revision (its required `_meta` fields, the methods it removed), `tools/list` schema, side-effect and open-world hints, JSON-RPC error codes, id echoing and in-flight duplicate ids, tool errors versus protocol errors, argument rules and error wording, the create/get/update/delete/validate round trip against a temporary workspace, validation of keys, chords and toggle values that cannot run, output-schema conformance of every result (a shared validator checks types, enums, ranges and undeclared top-level properties), compact literal text content, observation views (paging, scoping, redaction), resources, prompts, the launch policy (synthetic PE headers, refused shells, script hosts and Windows' program launchers such as explorer and pcalua, other programs of Windows itself unless `--allow-exe` names them, a copied Windows program recognized by its version resource, app execution aliases, arguments that name a refused program, renamed copies, network and device paths that are never touched; nothing is started), annotations that mark every tool able to start a program (launch true included) as not read-only, cancellation without a response, arrival-order registration, progress rules, batches answered as one array, stdin framing (byte order mark, CRLF, split writes, invalid UTF-8, an oversized line), subscriptions/listen, background runs with a stand-in driver (polling, the busy message, `cancel_run`, a run that fails after it started, cancelling the waiting call, the progress scale, a run reported finished as soon as its result is recorded while it still closes its app), desktop-lease contention (steps, runs, `launch_app` and `launch: true`), stored network and relative program paths that are never opened, test and run files whose id differs from their name (refused), evidence-file containment, the run index and evidence retention, clean shutdown, launched apps ending with a killed server (job object) unless `keepOpen`, the `mcp` command line (its own options, `--help`, errors on stderr only, the token from `TESTY_MCP_TOKEN`, `--parent-pid`), HTTP session/origin/host/token/content-type/method rules, sessions (replacement, the limit of 64, idle expiry), stateless header validation, SSE streaming and disconnect cancellation, the 32 MiB body limit, log hygiene (no whole session id, no token), the `--describe` manifest and `mcp-server.json` shape, `find_app` and the `app` argument against synthetic candidates (the schemas, ambiguity answered as `isError` with the candidates, the app stored by `create_test`/`update_test`/`validate_test`, `run_test` with a stored app, `--no-launch`), backward compatibility (every earlier tool, argument, range, default, required field, result field, resource URI and prompt is still there, and nothing newly required), and the time limits (`run_test` `waitSeconds` 0 answers `status: "running"` with `completedSteps`, `get_run` `waitSeconds` waits no longer than asked and returns as soon as the run ends, `cancel_run` stops a handed-over run, the instructions state the limits and the one-input-at-a-time rule). Four more checks (`App names: …`) cover the pure resolver: exact > prefix > whole words > fuzzy with reasons, the 0.7 threshold, the 0.1 margin and a running instance winning a tie only when it is named as a program or all contenders are that program (a window whose title merely matches stays ambiguous), merging per program, and the leading-clause parser. They launch no desktop application: slow launches use the windowless GUI-subsystem fixture `tests\Testy.IdleFixture` and runs use a stand-in driver and execution. `Testy.Tests.dll --only "MCP "` runs just these checks.

`-IncludeDesktop` also runs `verify-mcp`, which spawns the real `Testy.Cli mcp` child process (stdio, then `--transport http --port 0` with the token in `TESTY_MCP_TOKEN`, then further servers for the process-lifetime and app-name checks) against a fresh workspace and talks to it as a generic MCP client:

```powershell
.\Testy.Cli.exe verify-mcp --lab .\Testy.TestLab.exe --artifacts .\verification\mcp
```

Over stdio (25 checks) it initializes, lists every tool (the native ones, `find_app` and Cortex's), reads the workspace info and launch policy, launches the sample Customer Desk (and requires a console program, explorer and pcalua, notepad from the Windows folder, and the sample app with an argument naming cmd to be refused without anything starting; every tool that can start a program must not be annotated read-only), resolves "Customer Desk" with `find_app` to that running sample (and inspects it by that name), inspects it (the `id:CustomerDeskWindow` root, its controls with bounds and click points, and a real non-uniform PNG; then filtering, paging, `within` and `selector`), takes a screenshot (and a reduced one with its scale), minimizes the app and requires `list_apps` to say so, `inspect_app` to refuse with guidance and `activate_app` to restore it, performs a click and a typeText step (the observation lists only what changed), creates the five-step customer test, replays it with a progress token (progress rising to the total, a passing run and `run.json` in the workspace), reads the run, its resource and `list_tests`' last run, reads a step screenshot (and the refusal of a step number beyond the run), lists runs, renames the test and replaces its steps keeping ids, starts a run with `wait: false` (polled as running, keeping `perform_step` out with a message that names the run and `cancel_run`, stopped by `cancel_run` and read back from its file), checks that an unknown tool is a `-32602` protocol error and a failing call sets `isError`, cancels a running test with `notifications/cancelled` (no response, no further progress, the run recorded as cancelled with the following step skipped, the app still usable), runs a test that stores the sample app with just its `testId` (it connects to the running instance and leaves it open) and then with `exe` (the app launched for the run, progress on one scale, the app closed afterwards), closes the launched app with `close_app` and launches it again, deletes the test, closes stdin (exit code 0, launched app gone) and asserts that stdout carried only JSON objects and stderr no JSON-RPC. Over HTTP (11 checks) it checks the startup line, `endpoint.json` and `405` for GET, the `Mcp-Session-Id` rules (`401`/`400`/`404`, initialize replacing a session), `403` for a foreign or look-alike `Origin` and `415` for a body that is not JSON, a replay run streamed as SSE with progress events, `get_run`, a background run stopped by `cancel_run`, cancellation through the session (the stream ends without a response), cancellation by closing the SSE stream, the stateless 2026-07-28 requests (`server/discover`, `resultType`, header validation, `-32020`, `-32022`, `404`/`-32601`, `400`/`-32602`, a streamed call and an acknowledged subscription), a server started with stdin from the NUL device that keeps serving, and `DELETE` followed by a clean exit that removes `endpoint.json`. A lifetime check launches the sample app with and without `keepOpen` and requires it to survive the server's exit or a killed server only in the first case. A last server (6 checks, 43 in all) starts with no Customer Desk running (another one from the same program fails the first check with its pid): `find_app("Customer Desk")` must resolve uniquely to the sample app; `run_test` by app name with no pid must launch it, pass and close it; `create_test` with `app` must store it and `run_test` with just the `testId` must launch it and pass; `run_test` with `waitSeconds: 1` on a run with a 5-second wait must answer `status: "running"` at once and `get_run` with `waitSeconds` must return the passed run; with two instances launched by name, `find_app` must be ambiguous and `inspect_app`/`run_test` with the name must answer `isError` listing both pids; closing stdin must exit with code 0 and close both. The report is `mcp-verification.json`; the child processes' stderr and the workspaces are kept beside it.

The CLI command is:

```powershell
dotnet ./src/Testy.Cli/bin/Release/net9.0-windows/Testy.Cli.dll verify-lab `
  --exe ./src/Testy.TestLab/bin/Release/net9.0-windows/Testy.TestLab.exe `
  --artifacts ./verification/desktop
```

Each backend verifies the required control tree, creates a customer, validates required name and invalid email, filters records by search, resets fields, awaits a delayed asynchronous status, detects an absent selector, and catches the deliberate defect in the fixture. The absent-selector and defect scenarios are **expected failed test runs**: verifier success requires those tests to fail with a diagnostic. They are not hidden or reclassified as product passes. The verifier checks screenshot PNG signatures and the presence of JSON and HTML reports. Actual screenshots should also be visually inspected.

`-IncludeDesktop` also runs `verify-studio --exe <Studio.exe> --lab <TestLab.exe> --artifacts <directory>`. This drives the real Studio UI in a new isolated workspace, opens its included sample app, runs a persisted sample and checks its Last run view (the run trace lists the run's five steps, the step list shows a composed runner message for each, and choosing step 4 on the trace moves the screenshot caption and the step list to it), checks that the Activity panel lists the run, checks the Inspect controls and Results pages, saves provider settings, creates/renames/duplicates/deletes a draft, turns Show technical details on and off (the steps' Selector column appears and disappears), opens a test through global search, generates and executes an offline command test, and then checks agent access: 19 checks, or 21 with `--settings`. Every verifier starts Studio through `StudioLaunch` with the session-only `--technical-details off` and `--mcp-listen off`, so the checks never start a listener on a scratch workspace and never change the user's saved preferences; verify-studio adds `--mcp-token` and `--mcp-port 0` for its own listener. It uses only fixture data. Close existing TestLab windows before this verifier so it can reliably own the fixture process it creates.

The agent-access checks use the real MCP server against the same workspace Studio is open on:

- *Tests created by an outside agent appear in Studio* starts `Testy.Cli mcp --workspace <the verifier workspace>` as a child (the local dotnet host and the same dll the verifier runs from), speaks stdio like any MCP client (`initialize`, `notifications/initialized`, `tools/call`), creates "Outside agent test" (two valid steps on `id:ResetButton` and `id:StatusMessage`) and requires it in `TestLibrary` within 3 s (a targeted UI Automation find, then a complete snapshot) without moving the selection, and an Activity entry mentioning it. It then selects the test and renames it with `update_test` (the list and the open editor follow).
- *Outside changes to a test with unsaved edits: Reload and Keep my edits* types into the purpose, changes the test with `update_test`, requires `ExternalChangeBar` (named "Changed outside Studio") with `ReloadExternalChange` and `KeepMyEdits` exactly once each, the edit and the file both untouched, and a refused switch to another test; Reload must load the outside version; after another edit and update, Keep my edits must keep the text and leave the file alone until Save replaces it.
- *Outside deletion of the open test: Save a copy and Discard* deletes the test with `delete_test`, requires the bar (named "Deleted outside Studio") with `SaveExternalDeleted` and `DiscardExternalDeleted` once each and neither changed-case button, saves a copy (a new id on disk, the deleted file not restored), then deletes a second outside test while it is open and discards it.
- *Studio's own saves are not outside changes; an outside run reaches Results and Activity* saves a test in Studio and requires no outside-tool entry for it, then runs the read-only "Inspect customer form" test through `run_test` against Studio's own fixture process and requires the run's Activity entry ("Run of “…” finished (Passed) from an outside tool."), its file, and its row under Results; the server's stdin is then closed and exit code 0 required. The server's stderr is kept as `outside-agent-stderr.txt`.
- *Agent access settings* opens Settings and requires `McpCommand` to contain ` mcp ` and `--workspace` for the workspace, all thirteen card controls once (`McpStatus`, `McpCommand`, `CopyMcpCommand`, `CopyMcpJson`, `McpListen`, `McpUrl`, `CopyMcpUrl`, `CopyMcpHttpJson`, `McpPort`, `McpToken`, `CopyMcpToken`, `TestMcpConnection`, `OpenMcpGuide`), `McpToken` showing only the end of the token, `McpStatus` containing "answered with N tools" (every tool this build lists) within 15 s of Test the connection, and, after turning `McpListen` on, `McpUrl` starting with `http://127.0.0.1` within 15 s, `McpPort` showing that port, the named server process alive, `endpoint.json` in the workspace, `401` for HTTP requests without or with a wrong token, and `initialize`, `tools/list` (every tool) and `get_workspace_info` (Studio's workspace) answered with the token; turning the switch off must clear `McpUrl`, end the process and remove `endpoint.json`.
- *Closing Studio ends the listener* turns the listener on again, closes Studio's window and requires both Studio and the server process to end.

Studio verification starts in explicit replay mode. To additionally verify that the redesigned GUI invokes a real live model, provide `--settings <provider.json>` to `verify-studio`, or run `./scripts/test.ps1 -IncludeDesktop -StudioSettings <provider.json>`. The optional phase changes provider/execution settings through the GUI, runs the saved sample, then checks successive recorded model decisions, exact workflow/assertion coverage, and screenshot/tree evidence. It uses the configured provider and can incur its normal usage; mock protocol checks remain separate.

`verify-studio-regressions --exe <Studio.exe> --artifacts <directory>` independently tests filtered-library creation, history navigation with a conflicting filter, and preservation of invalid unsaved edits across selection and close. Its history record is explicitly synthetic navigation data, not an executed application test. Optional `--lab <TestLab.exe> --settings <provider.json>` adds real natural-language GUI authoring: three requested assertions must be generated against observed Lab selectors, persisted, selected, and not automatically executed. The full script runs these focused checks too. Completed GUI operations must release the Run button before final screenshots and shutdown.

`verify-lab` rejects any executable filename other than `Testy.TestLab.exe`. `verify-functionality` also supports the included `Testy.OrderLab.exe`. Both own their fixture processes and never close unrelated applications. Test inputs use synthetic names and `example.test` email addresses.

The desktop script also runs `Testy.WindowsChecks`: semantic keyboard/coordinate input, same-process modal inspection/capture, ambiguous-selector rejection, target bounds and cancellation, plus dispatch smoke checks for move/double-click/scroll/drag. A dispatch smoke check alone does not prove a meaningful UI effect for a control without that behavior.

`verify-native-protocol` uses queued **mock model responses** with the actual Responses computer-use adapter and **real Windows mouse/keyboard input**. It verifies application-relative coordinates, Unicode-capable typing, post-action snapshots/screenshots, the computer-call output protocol, and the exact saved assertion. It makes no external model request and is explicitly distinct from live AI verification.

## Live AI-directed execution

The regular CLI `run` command deliberately replays saved steps without an LLM. `run-ai` asks the selected model to decide each action, supplies fresh evidence after every action, and accepts a pass only after all saved assertions were independently verified in order. Missing/changed/weakened assertions, duplicate-coverage shortcuts, incomplete models, or missing evidence cannot grant a pass. The `AiTestRunner` harness checks these boundaries and the default AI/replay routing.

```powershell
dotnet ./src/Testy.Cli/bin/Release/net9.0-windows/Testy.Cli.dll run-ai `
  --test ./my-test.json --pid 1234 --settings ./provider-settings.json --artifacts ./ai-artifacts

# Launch and close only a fresh owned TestLab fixture, using existing provider configuration:
./scripts/verify-ai.ps1 -Settings ./verification/ai-live/settings.json `
  -Test ./verification/ai-live/requested-test.json
```

Live Codex uses the locally installed, already authenticated CLI and records each actual model decision. OpenAI native mode uses the Responses computer tool; compatible models use bounded local functions. Provider tests with mock HTTP validate protocol behavior but do not claim that an unconfigured paid API or arbitrary third-party model was tested live.

The optional `Testy.LiveChecks` executable verifies an actual provider's intentionally failing exact assertion and explanation, then cancellation after initial application evidence with no subsequent assertion. It launches and closes only its own TestLab. It is deliberately excluded from the default test script because it makes real provider requests. Configure credentials through the named environment variable before running it; the application also reads Windows User scope when the process variable is absent.

```powershell
dotnet run --project ./tests/Testy.LiveChecks/Testy.LiveChecks.csproj -c Release -- `
  ./examples/provider-openrouter.json `
  ./src/Testy.TestLab/bin/Release/net9.0-windows/Testy.TestLab.exe `
  ./verification/live-provider-safety
```

Use a turn budget between 3 and 12 for this optional harness. Its failed/cancelled test runs are expected negative cases; `live-safety-report.json` passes only when their exact failure, explanation, preserved evidence, and prompt cancellation checks succeed. Actual OpenRouter results for two model families are documented in `verification/OPENROUTER-RESULTS.md`.

## Evidence

- `verification/unit-report.json`: individual deterministic check outcomes and timing.
- `verification/desktop/integration-report.json`: each real backend/scenario, expected and actual outcome, and evidence directory.
- `verification/studio/studio-report.json`: Studio workflows exercised through UI Automation; screenshots, snapshots and an isolated workspace accompany it.
- `verification/studio-regressions/studio-regression-report.json`: filtered navigation, invalid-edit preservation, and optional actual natural-language authoring.
- `verification/supplemental/supplemental-results.json`: native input and modal/target-boundary checks.
- `verification/native-protocol/native-protocol-report.json`: real native UI execution with queued mock Responses outputs.
- `verification/wpf/desktop/wpf-report.json`: repeated advanced WPF checks through UIA and the opt-in probe, including expected failures and complete-run guards.
- `verification/wpf/hybrid-protocol/hybrid-wpf-protocol-report.json`: queued native screenshot protocol plus actual saved WPF semantic operations and assertions; no physical input or live model call.
- `verification/screenshot-content-audit.json`: offline fixture client-pixel audit, including previously saved images whose black clients invalidate visual evidence despite functional assertions passing.
- `verification/capture-quality.json`: production capture-validator regression on preserved readable-client and painted-frame/black-client PNG fixtures, without desktop access.
- `verification/ai-live/execution/`: authenticated live model turns and independently verified action evidence, when the optional live check has been run.
- Each run directory: final `run.json`, per-step tree JSON and PNG, `report.html`, and `junit.xml` for CI.

Provider integration needs separate credentials or a signed-in Codex CLI. An offline command parser does not count as AI verification. An API request that has not been sent to a configured service is not considered verified. See the delivery verification summary for checks actually run in this workspace.

## CI usage

Run existing JSON tests against an explicitly chosen process:

```powershell
dotnet ./src/Testy.Cli/bin/Release/net9.0-windows/Testy.Cli.dll run `
  --test ./my-test.json --pid 1234 --artifacts ./ci-artifacts
```

Exit code 0 means the test passed; 1 means the test failed or was cancelled; 2 means invalid input, an unavailable target or an execution setup error. A Windows service or locked desktop cannot provide a reliable interactive UI testing session. Use a dedicated logged-in Windows test worker.

## Published package smoke test

After publishing the self-contained application, run `./scripts/verify-package.ps1`. It defaults to `dist/Testy`; pass `-PackageDirectory ./dist/Testy.Backend` for an explicitly published backend-only bundle. The helper intentionally rejects framework-dependent packages. It clears external runtime-root variables for its child processes, validates the bundled runtime manifests and CLI help, opens only the bundled Customer Desk fixture, and runs the packaged example through both UIA and the WPF probe. It verifies six steps per run, PNG signatures, detailed nonblack fixture client pixels excluding OS chrome, snapshot files, JSON/HTML/JUnit reports, and closes only the fixture it launched. The fixture-specific pixel check catches blank capture failures; inspect screenshots to verify their actual semantic content too. Results are saved to `verification/package-smoke.json`. It does not build or operate Studio or execute advanced WPF scenarios; run the bundled CLI's `verify-studio`, `verify-wpf` and `verify-wpf-hybrid` separately for those package checks.
