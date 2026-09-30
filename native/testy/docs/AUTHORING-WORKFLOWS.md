# Demonstrations and parameterized tests

These commands extend test creation without changing Studio's frontend. They produce ordinary editable test JSON or suite JSON. Drafting does not execute a test or grant it a pass. The model still chooses each next operation when the resulting test is run with `run-ai` or an AI-configured worker.

## Record supported application events

Attach to a dedicated test instance and record a short demonstration:

```powershell
.\Testy.Cli.exe record --pid 1234 --output .\demo\customer.json --duration-seconds 30
```

Wait until the output JSON contains `recordingActive: true`, then perform the workflow in that selected application. The output file is reserved before capture begins. The recorder observes UI Automation Invoke events and editable Value changes within the initial attached window. It resolves fresh unique selectors against event-time control and ancestor identities, combines consecutive edits of the same control within one completed callback batch, and stores initial/final screenshots and trees beside the recording file. Password controls are excluded. Event values are bounded; lost, changed, ambiguous or unresolved events make the recording incomplete and prevent AI drafting. Shutdown waits for in-flight callbacks and marks a callback timeout as incomplete.

This is an accessibility-event demonstration, not a full desktop gesture recorder. Keyboard shortcuts, selections, dragging, separately opened windows and controls that do not publish these events are outside its capture coverage. Programmatic application changes may also emit events. Review the sequence; neither an event nor a completed recording proves the business workflow succeeded. The recording's `Completed` means its supported capture session finalized without detected loss, not that every interaction was observed.

## Generate a draft with explicit acceptance

Write the expected result as an assertion-only TestCase JSON. The included `examples/customer-acceptance.json` and `examples/customer-demo-instructions.txt` illustrate the contract:

```powershell
.\Testy.Cli.exe draft-from-demo --demo .\demo\customer.json --acceptance .\examples\customer-acceptance.json --instructions .\examples\customer-demo-instructions.txt --settings .\examples\provider-openrouter.json --output .\drafts\customer.json
```

The configured model receives the observed actions and explicit acceptance as an existing test, the final sanitized snapshot, and drafting instructions. It must preserve the assertion count, order, selectors, comparison values and timeouts. A changed assertion rejects the draft before saving it. Only the final screenshot is sent when images are enabled; it must be inside the recording directory. These selected application observations are sent to the chosen provider; use dedicated test data.

The saved test is labeled `AI demonstration draft`, with separate provenance marked `UnexecutedDraft`. Import the test JSON through Studio, review its proposed actions, then run it against a clean target. The CLI never runs generated drafts automatically and never overwrites an existing output. Provider support for structured authoring must be qualified separately from saved-test execution.

## Materialize a data matrix

A template contains a valid `test` and explicit `bindings`. Each binding names a parameter and one saved step ID. Only a whole `value` of `TypeText`, `Select`, or `AssertText` can be bound. Actions, selectors, IDs, timeouts, and comparison modes stay fixed. Values remain literal strings; there is no executable expression language or textual JSON substitution.

```powershell
.\Testy.Cli.exe materialize-template --template .\examples\customer-template.json --data .\examples\customer-data.json --output .\generated\customer-suite.json
.\Testy.Cli.exe run-suite --suite .\generated\customer-suite.json --pid 1234 --settings .\examples\provider-openrouter.json --artifacts .\artifacts
```

For explicitly selected deterministic replay, replace `--settings ...` with `--replay`. The supplied matrix creates a valid customer case and two negative cases: invalid email and empty name. Each case resets its own fixture data. An expected validation rejection is a passing test only when the exact validation text and unchanged record count are observed.

The engine validates every row before writing any suite. Missing/extra parameters, duplicate row IDs, conflicting bindings, unsupported fields and switching an assertion between exact and `contains:` comparisons are rejected. Suites support at most 200 rows here. Parameters are resolved before execution, so the AI sees immutable concrete steps and acceptance rules. Existing output/provenance files are preserved.

Run the offline integrity checks with `verify-maintenance --artifacts DIRECTORY`. Current qualification and limitations are in the [verification report](../verification/FUNCTIONALITY-RESULTS.md).
