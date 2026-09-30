# Failure diagnostics

Every replay or AI-directed run can contain `failureDiagnostics` on the run and its recorded steps. These are deterministic observations, separate from `aiAnalysis`. They never change the saved expectation, the assertion verdict, or the run status.

Each diagnostic records:

- A category describing what failed: assertion mismatch, missing or ambiguous selector, control not ready, target unavailable, automation deadline/error, evidence unavailable, provider/agent failure, workflow not verified, cancellation, or unknown.
- The observed fact and, when the runner actually observed them, expected value, actual value, and comparison method. An unavailable observation remains absent; the model does not supply it.
- The selector, observation timestamp, recorded step index, and last successful recorded step.
- Paths to available evidence, including a separate snapshot used to decide an assertion/readiness failure. A later post-step snapshot does not replace that earlier observation.
- An explicit statement of uncertainty and suggested next checks. An assertion mismatch does not establish whether the product, test data, timing, or expectation caused it.

Indices are zero-based entries in the recorded run. AI runs include inspection observations, so these indices are not necessarily the positions in the saved test. `requested-test.json` remains the immutable saved workflow for comparison.

## Input completion

`actionOutcome` is independent of the test verdict:

- `notDispatched`: no application input was issued for this diagnostic, such as a failed assertion or readiness check.
- `completed`: the driver returned from input before a later evidence failure. This does not mean the application produced the expected result.
- `unknown`: execution stopped while input was being dispatched. It may have been partially or fully delivered. Inspect the application before deciding whether to run again; do not automatically retry the input.

Caller deadlines bound how long Testy waits. They do not forcibly terminate a driver or an operating-system operation that ignores cancellation. The test runner stops later steps, and the suite stops later cases on failure or cancellation. An uncooperative operation may still finish later; diagnostics explicitly retain this uncertainty.

## Reports and explanations

`run.json`, the HTML report, and JUnit include the diagnostics. Existing Studio summaries include a short diagnosis without adding a new UI layout. HTML labels AI review as supplementary hypotheses. Explanation requests provide the deterministic diagnostics and ask the model to separate facts from possible causes; prior model commentary is not treated as new observed evidence.

JUnit preserves the recorded step results. A failed run with no failed step gets a synthetic terminal failure testcase. Cancellation gets a synthetic terminal error testcase even when earlier steps passed, so CI cannot interpret an interrupted run as fully successful. The `testy.run.status` property records the overall status.

Old run files remain readable. If a legacy failure lacks structured observations, report generation marks its cause `unknown` rather than reverse-engineering a cause or an actual value from prose.
