# Sequential functional suites

The CLI can run a collection of saved tests repeatedly against one attached application. AI decides each next action when a provider settings file is supplied. The same suite works with a corporate compatible endpoint, direct OpenAI, or the Codex development adapter; no OpenRouter-specific behavior is required.

Create a manifest that references existing test JSON files. Relative paths are resolved against the manifest directory, not the shell working directory:

```json
{
  "name": "Customer creation stability",
  "repetitions": 3,
  "testFiles": ["create-customer.json"]
}
```

Alternatively, provide a `tests` array containing complete TestCase objects. `materialize-template` writes this inline form directly, ready for `run-suite`. A manifest must contain exactly one of `tests` or `testFiles`; mixed, null or empty forms are rejected. Both forms validate every test and duplicate test identity before any test action. See [parameterized authoring](AUTHORING-WORKFLOWS.md).

Run with an existing provider configuration:

```powershell
.\Testy.Cli.exe run-suite --pid 1234 --suite .\examples\customer-suite.json --settings .\provider-settings.json --artifacts .\artifacts
```

The settings must enable `aiDirectedExecution` and select a model provider. There is no silent fallback to replay. To run without a model, explicitly choose:

```powershell
.\Testy.Cli.exe run-suite --pid 1234 --suite .\examples\customer-suite.json --replay --artifacts .\artifacts
```

Add `--probe` only when the target has enabled the opt-in WPF probe. Each test is responsible for establishing its starting state, usually with an explicit reset action. Testy does not restart an arbitrary target or infer a cleanup operation.

`examples/orders-suite.json` targets the included WinForms `Testy.OrderLab.exe`. Its five-step saved test applies a Shipping recipient using ancestor-scoped selectors, then checks both the exact Shipping result and that Billing remains untouched. Select that application's PID; do not run the order suite against Customer Desk.

All test definitions are loaded and validated before the first test executes. The suite supports up to 200 distinct tests, 100 repetitions, and 1000 total planned runs. Execution is sequential in manifest order, repeated in complete cycles. Each attempt receives a fresh copy of the saved test and its own evidence directory. Tests cannot silently overwrite the acceptance criteria for the next repetition.

The first failure or cancellation stops the suite. Remaining entries are recorded as skipped; skipped means untested, never passed. This prevents a failed or partially completed mutation from being repeated against an unknown state. Ctrl+C cancels cooperatively. An already-dispatched native action may finish later and cannot be undone. A process crash can leave the incremental report marked running; it must not be interpreted as a pass.

Each suite directory contains:

- `suite-definition.json`: the frozen definitions used for this run.
- `suite.json`: incremental and final outcomes, elapsed time per attempt, counts, and execution mode.
- `suite.html`: a readable summary linking to individual run reports.
- `suite-junit.xml`: CI test outcomes, including failures and skipped/cancelled attempts.
- `case-0001/`, etc.: the runner's screenshot, tree, trace and report artifacts.

Exit code 0 means the entire suite passed; 1 means failure or cancellation; 2 means invalid input or attachment/configuration failure outside an individual run. A cancelled suite also adds a synthetic `Suite completion` error to JUnit so passing earlier cases cannot conceal interruption. CI should use the exit code as well as the JUnit report. No actual model invocation is implied by a replay suite or a provider protocol fixture.

This command executes a local suite against its already attached process. It does not restart the app per case, provide a distributed scheduler, perform automatic rollback, or provision desktop sessions. The separate [worker command](WORKERS.md) runs one saved test in fresh owned processes. Use an unlocked dedicated interactive desktop with the same privilege level as the target. Studio's **Automate → Test sets & data** tab saves and runs the same suite files against the connected app.
