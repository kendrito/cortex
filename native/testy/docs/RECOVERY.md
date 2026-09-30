# Explicit AI selector alternatives

Saved steps remain immutable. By default a missing control fails the step. A test author can additionally save up to four alternate selectors for a step, each with an ID, an exact expected control type, and an exact expected automation ID and/or accessible name.

```json
{
  "id": "save",
  "title": "Save the record",
  "action": "click",
  "selector": "id:OldSaveButton",
  "value": "",
  "timeoutMs": 5000,
  "selectorAlternatives": [
    {
      "id": "renamed-save",
      "selector": "id:NewSaveButton",
      "expectedControlType": "Button",
      "expectedAutomationId": "NewSaveButton",
      "expectedName": "Save"
    }
  ]
}
```

During local-tool AI execution the model may request `perform_saved_step_alternate(stepId, alternativeId)`. The model does not supply a replacement action, value or arbitrary selector. It must choose the alternate before a failed step ends the run; there is no automatic retry after input or an uncertain outcome.

The engine requires a fresh complete observation, proven absence of the original selector, exactly one alternate match, and exact saved identity. An existing, disabled or ambiguous original cannot be substituted. Virtualized collection coverage that cannot prove absence also refuses recovery. Password controls, absence assertions, logical lookup assertions and selector-free operations cannot use this route.

The Windows/probe driver revalidates the same process and live control identity immediately before the operation. Results retain the original saved step and add separate `SelectorRecovery` evidence with original/resolved selectors, the guarded tree and the actual input outcome. All original assertions still apply. External UI state can change between observations; driver checks constrain dispatch but do not create an atomic transaction across the application.

This is deliberate support for authored UI variations. It does not infer that a visually similar control is equivalent, synthesize new selectors, or silently change a saved test. Native computer-use mode rejects workflows containing these alternatives during preflight; use a compatible local-tool model or Codex bridge for this feature. Standard deterministic replay remains strict.

See [WPF capabilities](WPF-CAPABILITIES.md) for controls and [verification](../verification/FUNCTIONALITY-RESULTS.md) for the cases actually exercised.
