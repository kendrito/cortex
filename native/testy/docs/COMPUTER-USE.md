# AI-directed computer use

Testy separates the model's decision from the local operating-system action. The AI-directed runner is a closed feedback loop: observe → request one model decision → validate and execute one action → capture the actual result → request the next decision. It does not generate a script once and then label ordinary playback as continuous AI control.

## Provider routes

| Provider | Decision interface | Local execution |
| --- | --- | --- |
| OpenAI Responses with native mode enabled | Native `computer` calls plus canonical assertion and WPF control functions | Physical input through `NativeComputerActionExecutor`; explicitly named semantic WPF operations through the selected driver |
| OpenAI Responses with native mode disabled | `observe_application` / `perform_saved_step` for saved tests | Testy UI Automation or WPF probe |
| Compatible Chat Completions | The same local function tools in Chat Completions format | Testy UI Automation or WPF probe |
| Codex development bridge | Strict one-decision JSON from authenticated Codex CLI | The same local tool dispatcher |
| Offline | No model | Explicit deterministic replay only |

Native computer use must be supported by the chosen model. Configuring an arbitrary GPT model does not establish tool support. The Responses endpoint is configurable, and model/service errors are surfaced without silently changing the model.

## Native OpenAI protocol

Native mode advertises `{"type":"computer"}`. The model receives the attached application's screenshot and accessibility or WPF tree. It returns a `computer_call` with exactly one action. Testy validates that action, executes it locally, captures another screenshot, and returns `computer_call_output` with the matching `call_id` and `computer_screenshot` image. The loop preserves model response context and reasoning items as required by the Responses protocol.

The screenshot is the selected application window, not the whole desktop. Coordinates are physical pixels relative to that window image. The local executor validates process ownership, foreground/focus, bounds, action type, and argument limits. Only supported input operations are available; no model-supplied script, shell command, or process-launch operation is executed.

Native mouse/keyboard actions do not prove a test outcome. The additional assertion function checks expected text or control state against the actual target. Bound workflows use `verify_saved_assertion(stepId)`; unbound sessions retain the restricted assertion payload tool.

Bound native workflows also expose `perform_saved_control_step(stepId)` for explicit expansion, collapse, item realization, scrolling into view, percentage scrolling, and supported grid edit/commit/cancel operations. It accepts only the corresponding canonical saved operation. It cannot substitute for a saved TypeText, Click, Toggle, or Select operation. Before either bound function executes, the independently verified full workflow prefix must identify that exact next step. A missing physical receipt therefore cannot be bypassed by proceeding with a WPF operation. Generic `perform_ui_action` is not advertised in native mode.

## Local computer tools

The local interface works with models that have ordinary structured tool calling rather than native OpenAI computer use. `observe_application` returns current controls and a screenshot. For a saved workflow, `perform_saved_step` accepts only a step ID; the engine retrieves the immutable action, selector, value, and timeout. Unknown, repeated, and out-of-order IDs are rejected before input. The model receives fresh evidence after each step and chooses whether to proceed, inspect again, or report a problem.

When a saved test includes explicit selector alternatives, local mode additionally offers `perform_saved_step_alternate(stepId, alternativeId)`. The model may choose only a preauthored equivalent, with the original action and acceptance unchanged. The driver guards the observed identity, and the result keeps the canonical step plus separate recovery evidence. This is not a retry after a failed or uncertain mutation. See [recovery contracts](RECOVERY.md); native physical mode currently rejects tests with alternatives.

Unbound tool sessions retain `perform_ui_action` for one constrained action such as click, type, select, toggle, expand, realize, scroll, or assert. Both interfaces resolve controls only within the attached process. Neither accepts arbitrary executable code. [WPF operations](WPF-CAPABILITIES.md) describe the available typed state and virtualization tools.

The local HTTP loop sends only the latest full screenshot and control tree, retaining compact prior action results, step IDs, lookup facts, and diagnostics. Assistant/tool-call pairs and Responses reasoning items remain intact. Complete screenshots and snapshots stay in local artifacts. Codex likewise receives current application evidence plus action history. The native protocol currently retains its full observation transcript and may reach a model's context limit on large runs.

The runtime has no tool for browsing arbitrary local files, reading a repository, running a shell command, compiling a project or launching model-selected processes. The host reads explicitly supplied test/provider files and may launch a configured worker EXE. Those host capabilities are not exposed as model tools. A project-aware investigation/build loop would require separate implementation and a chosen workspace; it is not part of this release.

This path can use semantic controls such as `id:CustomerName`, which is especially useful for WPF testing. A native screenshot-driven model also receives the tree as context. The WPF probe is optional; it runs in applications that explicitly reference and start the probe library.

## Preserving the test's meaning

`AiTestRunner` receives a saved `TestCase`. Its workflow and acceptance assertions are immutable for that run. In bound local execution, the AI requests each next saved step and may add observations; it cannot rewrite selectors, values, assertions, or ordering. The authoring/refinement assistant can propose changes to the saved test for review outside the run. Native execution continues to use physical input with independent saved-workflow verification.

A run passes only if all of the following hold:

1. The model completes within its turn budget.
2. Every actual action and its evidence collection succeeds.
3. Every saved workflow step and assertion appears in the observed passing sequence in its original order, including repeated steps. Local tools must match the saved action, selector, and value; a saved wait cannot be shortened. Native physical input mapped to saved controls requires the intended control's runtime identity, a fresh synchronized-input delivery acknowledgement, the corresponding pre-action bounds or focus, and post-action values where applicable. Explicit advanced-control tools execute and verify their canonical saved operation separately.
4. The session has not failed or been cancelled.

One convenient assertion, a visual guess, a textual success claim, or a weakened check such as substituting `AssertExists` for `AssertText` cannot grant a pass. Failure evidence and missing acceptance coverage are represented in the final JSON, HTML, and JUnit reports.

The initial automatic screenshot is setup evidence and cannot substitute for a saved Screenshot step. Native interactions that cannot be mapped to the saved workflow fail coverage rather than being guessed successful. In this preview, reselecting an already-selected value with the native mouse cannot be proven reliably; use semantic local-tool mode for that case. Native waits are bounded to five seconds per action; longer saved waits are rejected before input. Use saved waits within that limit or an assertion that polls for the desired state.

Mapping native physical input to saved semantic controls requires UI Automation attachment and a target provider that supports synchronized-input acknowledgement. The opt-in WPF visual tree's `wpf:` identities are not UIA runtime identities; use local tools for ordinary probe interactions. Explicit advanced-control tools verify their semantic operation without claiming physical input. A missing delivery acknowledgement, a changed hit target, or unsupported provider cannot be converted into a passing saved physical interaction. Explicit coordinate actions remain coordinate dispatch checks and require independent assertions for their intended effect. See [PROBE.md](PROBE.md) for delivery evidence and limitations.

## Limits and cancellation

Every model response may request only one action. Multi-action responses are rejected before executing them. Saved AI workflows require at least one assertion and a configured budget of at least one turn per saved step plus a completion turn, within the 240-turn local or 80-turn native maximum. Extra observations consume additional turns, so this minimum does not guarantee completion. Stopping cancels pending HTTP requests or terminates the associated Codex child process and prevents later actions. Native Windows/UI Automation provider calls already dispatched may not be forcibly interruptible; their outcome can be uncertain if the target hangs. Such operations are never automatically retried. Bounded retries for explicit transient HTTP responses repeat only the model request before any returned tool is dispatched; they never repeat a UI input or project command. Durable action journals preserve interrupted outcomes. See [Lifecycle](LIFECYCLE.md) and [Studio workflows](PRODUCT-WORKFLOWS.md).

Provider-triggered safety checks must be surfaced without automatically acknowledging them. Unsupported actions fail explicitly. A changed, exited, minimized, or inaccessible target does not cause attachment to another app.

## Verification level

The local drivers are verified on a real WPF fixture. Protocol fixtures verify both API message shapes and action/result sequencing without API credentials. The authenticated Codex bridge is exercised with actual model calls. A native API protocol test is distinct from a real paid model call; consult the verification summary for the exact checks completed.

Official protocol reference: [OpenAI computer use](https://developers.openai.com/api/docs/guides/tools-computer-use).
