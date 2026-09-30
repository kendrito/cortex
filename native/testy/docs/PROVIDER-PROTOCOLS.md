# Model providers and live execution

`ComputerAgentFactory.Create(settings, driver, artifactsDirectory, nativeExecutor)` creates an `IComputerAgent`. The default product entry point is `TestExecutionService` / `AiTestRunner`, which preserves the saved test's acceptance contract and evaluates the recorded workflow independently of the model's final message. Direct agents return `Completed = true, Status = Pending` when the model finishes; they cannot grant themselves a passing test verdict.

Each live loop permits one action per model turn. The next model request contains the resulting application UI tree and screenshot. A failed action stops further mutations and, when the turn budget permits, receives a final model review with the failure evidence. Every action retains timing, observed status, PNG evidence and a JSON snapshot. Automatic observation screenshots do not count as requested workflow actions.

## Codex development bridge

`CodexComputerAgent` uses the installed, authenticated `codex.exe` and no API key. Each bound saved-workflow turn requests a strict decision object containing `done`, `explanation`, `toolName` and `stepId`. The dispatcher retrieves the next immutable saved definition; it rejects rewritten, unknown, repeated or out-of-order steps. Unbound tool sessions use a constrained `step` object instead. The dispatcher collects new evidence before another model request. Per-turn decision files are retained under the run's agent directory.

The child CLI uses `--ignore-user-config`, `--ephemeral`, `--sandbox read-only`, a dedicated job directory, an output schema and the selected application image. Shell, plugin, app, browser and native computer tools are disabled in that CLI invocation. The model does not run shell commands or its own desktop automation. Codex is an optional development provider: the HTTP providers do not depend on it.

## OpenAI Responses native computer tool

`NativeComputerUseAgent` uses `tools: [{"type":"computer"}, ...]`. A `computer_call` must contain one item in its `actions` array. `IComputerActionExecutor` translates that action to guarded input on the attached application. The next response receives `computer_call_output` with the same `call_id` and a `computer_screenshot` image. Full stateless conversation input retains reasoning items and requests `reasoning.encrypted_content`.

Bound saved tests expose `verify_saved_assertion(stepId)` for immutable assertions and `perform_saved_control_step(stepId)` for explicit `Expand`, `Collapse`, `RealizeItem`, `ScrollIntoView` and `ScrollPercent` operations. Neither tool can replace ordinary click, type, toggle, select or key input. Both require the next exact step in the independently verified full workflow, so an unverified physical interaction cannot be skipped by choosing a semantic tool. The unbound `verify_ui_assertion` function accepts only `AssertText`, `AssertExists`, `AssertNotExists`, `AssertEnabled`, `AssertProperty`, `AssertItemExists` and `AssertItemAbsent`; it cannot mutate controls.

Ordinary native mouse/keyboard actions use physical pixels relative to the selected window screenshot, not desktop coordinates. Multiple calls or batched actions are rejected before any action from that response executes. Pending safety checks stop the run visibly; the application never acknowledges them automatically.

Set `NativeComputerUse = false` to use OpenAI function tools instead. A configured OpenAI model must support the chosen API capabilities. Direct OpenAI Responses requests have not been run with a direct OpenAI API key. Live GPT testing through OpenRouter uses the separate compatible local-tool protocol.

Saved native control interactions additionally require fresh UI Automation runtime identities and acknowledged synchronized input on the intended control. A receipt identifies the actual mouse hit or keyboard focus immediately before input; unrelated overlays and independently actionable nested controls cannot stand in for the saved target. Providers without synchronized-input acknowledgement, snapshots without runtime identities, ambiguous overlapping peers, and dropdown options without proven subtree ownership cannot receive a passing semantic workflow verdict. The report explains this limitation; choose local semantic tools or the opt-in WPF probe in that case. Explicit coordinate steps remain coordinate-based, with independent saved assertions still required. Input guards and receipts cannot establish the behavior of invisible custom-canvas controls that expose no accessibility identity.

## Compatible models and custom local tools

`ComputerUseAgent` supports OpenAI-compatible Chat Completions and Responses function calls. Bound saved workflows offer:

- `observe_application`: collect the attached application's state.
- `perform_saved_step`: accept only the next saved step ID, execute its immutable action/selector/value/timeout, then return evidence.

Unbound sessions expose `perform_ui_action` instead of `perform_saved_step`, accepting one validated action or assertion. `LocalComputerTools.SessionResponsesTools` and `.SessionCompatibleTools` select the actual JSON schemas for the bound or unbound session; the corresponding static `BoundResponsesTools` / `BoundCompatibleTools` and `ResponsesTools` / `CompatibleTools` hold those schemas. `DispatchAsync` pins the session to the attached process, restricts tool names and enforces a call budget. Arbitrary scripts, process launch, filesystem access and shell operations are not part of this interface. Truncated or filtered model decisions cannot execute actions.

Saved AI execution requires at least one assertion and a budget of at least the number of saved steps plus one completion turn, within the 240-turn local or 80-turn native limit. Native saved waits must be at most 5,000 ms each. Invalid configurations fail preflight before model requests or target input; additional observations and provider behavior can still exhaust an otherwise valid budget.

For a model without vision, set `SupportsImages = false`. It still receives the structured UI tree and action results; screenshots remain in local evidence. A compatible endpoint must implement the relevant Chat Completions tool-calling format. Structured test authoring additionally uses strict JSON-schema output, which some third-party endpoints do not support.

## Settings and verification

Provider settings store the endpoint, model name and the **name** of an environment variable containing the API key. They do not store the API key itself. Each request first checks the current user's Windows credential vault for a key saved for that exact endpoint/key-variable slot, then the process environment, then the Windows user environment if absent or blank. Changing the endpoint does not forward a stored vault credential to the new destination. HTTPS is required except for loopback HTTP endpoints. Responses requests disable response storage. The configured model receives the selected application evidence, supplied workflow context, and explicitly enabled project-tool results.

OpenRouter is configured as `Compatible` with `https://openrouter.ai/api/v1/chat/completions`. Both GPT and non-OpenAI models on this connection use Testy's local function tools. It does not enable the native Responses computer protocol. See `OPENROUTER.md` and the separate OpenRouter verification record for the tested model configurations.

`OfflinePlanner` is explicitly a deterministic command parser, not AI. Offline mode cannot start an AI-directed run. `TestRunner` remains available for explicitly selected deterministic replay.

Verification covers real authenticated Codex planning and defect analysis, live Windows fixture execution, provider requests and response parsing with an injected HTTP client, native computer call IDs and screenshot outputs, function-call boundaries, missing credentials, cancellation, evidence failures, model turn limits and independent workflow verdicts. Mock API protocol tests establish local behavior; they are not a claim of live API or third-party endpoint compatibility.

Official protocol references, checked September 25, 2026:

- [OpenAI computer use](https://developers.openai.com/api/docs/guides/tools-computer-use)
- [OpenAI function calling](https://developers.openai.com/api/docs/guides/function-calling)
- [Codex non-interactive mode](https://learn.chatgpt.com/docs/non-interactive-mode)
