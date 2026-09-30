# Preparation, launch and guarded UI execution

A lifecycle lets the model inspect a selected project and choose host-configured preparation commands **before the target process exists**. Only successful, terminal preparation evidence permits launch. The existing isolated worker then starts the newly built executable, captures its build fingerprint, executes the unchanged saved test, validates its evidence, and cleans up owned processes.

The preparation and UI stages have separate evidence. Preparation exposes file inspection and fixed command IDs, without a fabricated screenshot or attached target. The UI stage retains read-only project access; it cannot repeat preparation commands or rebuild the already loaded application. Every saved UI step still requires a model decision unless the profile explicitly selects replay.

Use a profile like this, replacing paths with your project and selected provider settings. Relative file paths resolve against the profile directory. The executable can be absent initially; the repository, SDK command executable, test and provider settings must already be valid.

```json
{
  "schema": "testy.lifecycle.v1",
  "id": "customer-lifecycle",
  "name": "Build and test Customer Desk",
  "testFile": "tests/create-customer.json",
  "settingsFile": "provider.json",
  "projectFile": "project-tools.json",
  "executable": "project/app/CustomerDesk.exe",
  "probe": false,
  "replay": false,
  "preparationInstructions": "Inspect the source, run the required build command once, and complete only after the observed build succeeds.",
  "maximumPreparationTurns": 20,
  "preparationTimeoutSeconds": 600,
  "workerTimeoutSeconds": 900,
  "startupTimeoutSeconds": 20,
  "shutdownGraceSeconds": 3
}
```

Profiles saved before the product was renamed from Axiom use `"schema": "axiom.lifecycle.v1"` and still load unchanged.

The project configuration must explicitly enable access and list at least one `requiredBeforeUiCommands` ID. Each configured command supplies a fixed executable, argument array, working directory and timeout. The model chooses when to invoke it; it cannot supply a shell command or change those arguments. Preparation also requires a successful file inspection. Failed, cancelled or uncertain commands cannot be retried within that session.

```powershell
.\Testy.Cli.exe lifecycle-run --profile .\lifecycle.json --artifacts .\artifacts
.\Testy.Cli.exe lifecycle-inspect --directory .\artifacts\lifecycle-...
.\Testy.Cli.exe operations --operation enqueue --workspace .\workspace --profile .\lifecycle.json
.\Testy.Cli.exe operations --operation pump --workspace .\workspace --once --seconds 1800
```

Direct lifecycle, direct worker and queued lifecycle execution share a desktop lease. A busy desktop does not authorize another worker. Owned target and child process identities are durably registered before UI execution; surviving registered processes prevent immediate lease reuse after a host crash. This coordinates Testy processes, not unrelated desktop applications.

`request.json`, preparation decisions/results, project evidence and checksum sidecars, stage checkpoints, worker reports, screenshots, and agent checkpoints remain under the lifecycle artifact directory. A terminal lifecycle passes only after preparation and worker acceptance pass, cleanup completes, and no action outcome remains unknown. Interruption inspection marks unfinished work for review; it does not restart a command, resume UI input, or turn incomplete evidence into success. Start a new explicit run after reviewing the interrupted operation and cleanup.

Preparation allows 2–80 model turns and needs at least required-command count + 2 turns for inspection and completion. Local/Codex UI execution allows up to 240 turns; native Responses mode allows up to 80. Saved tests remain limited to 200 steps. UI execution needs at least one turn per saved step plus completion, with additional turns for optional inspection and focus work. Preparation and worker deadlines are independently bounded at 1–7200 seconds. These are ceilings, not a promise that every long workflow fits a provider's context window.

HTTP adapters retry only unsuccessful 429, 502, 503 and 504 model responses, before any returned host tool is dispatched. `maximumProviderRetries` is 0–3 (default 2), with a bounded `providerRetryDelayMs` (default 500). The identical request is retried; commands and UI input are never retried by this policy. Transport errors, invalid model output and other HTTP errors stop normally. Retry metadata is stored in `provider-requests.json` for execution and preparation agents. Credentials are resolved through the configured credential store/environment slot and are not included in that metadata.

Compatible Chat Completions planning omits only the remote `steps.maxItems` keyword for schema portability. Strict field and action constraints remain in the request, and the host still rejects plans above 200 steps. This applies to the compatible adapter as a whole, without provider-name or model-name exceptions. A successful connection check establishes structured planning only; it does not execute a UI test.

`scripts/verify-lifecycle-live.ps1` performs two harmless structured provider connection checks and qualifies a fresh, owned WPF source fixture against those configured compatible providers: a direct build/run, a queued build/run, a compiler failure that must never launch a target, and cancellation during an active preparation command. It requires an unused artifact directory and an explicit SDK, CLI and two provider-setting paths. This script makes live provider calls and controls its own fixture when deliberately invoked; it is not part of a passive file audit. Direct OpenAI native input is not qualified by these checks.
