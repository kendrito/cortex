---
description: "Cortex Models settings: local OpenAI-compatible gateway configuration and read-only administrator routes."
kind: "package-reference"
---

# @cortex/client-ui-settings-models

## Summary

The Models page joins the provider directory, settings namespaces, and credential descriptions. Cortex limits end-user configuration to local OpenAI-compatible gateways such as LiteLLM. Administrator-provisioned external routes remain visible and usable, with Edit and Delete disabled. The plugin collects no product analytics and registers no external-provider onboarding dialog.

## Use this package

Add a model provider to configure an HTTP or HTTPS endpoint on `localhost`, `127.0.0.1`, or IPv6 loopback. The creation form offers `openai-completions` and `openai-responses`; catalog adoption and other wire protocols are unavailable. Enter at least one model or fetch the local endpoint's available models, select candidates, and save. Model discovery remains disabled for external endpoints, including an unsaved change to a previously local route.

The local route's editor supports its display name, endpoint, wire protocol, model list, and write-only API key. Clearing a required local endpoint or changing it to an external URL prevents both discovery and saving. Saving preserves the selected model; the composer owns model selection. API keys use the profile's credential reference, deriving `<ROUTE>_API_KEY` when necessary. Blank keys preserve existing or native authentication.

Configured external providers and providers without an explicit local endpoint are administrator-managed rows. Local routes using a non-OpenAI wire protocol are also locked. The policy is a client-side UX restriction, not a server authorization boundary: backend settings APIs and administrator configuration retain their full provider capabilities.

Deleting an editable route requires confirmation. The page removes a credential only when it owns the exact derived reference and the credential is writable. Other credentials remain unchanged.

The versioned welcome notice remains available in the browser. Its acknowledgement is persistent for a local settings scope and process-local for a remote browser scope.

## Understand the implementation

`ModelsSettingsStore` owns one observable snapshot and joins Remote results with the shared settings mirror. Namespace revisions fence writes so a stale editor cannot overwrite a newer settings change. Provider cards receive injected operation callbacks and synchronous schema operations, never a client context. Successful writes refresh the shared snapshot; pushed settings, credential, and adapter updates refresh only after the page has first loaded.

`lockdown.ts` owns the allowed endpoint and protocol predicates. `ModelsSection` disables locked rows and omits catalog adoption. The creation and edit cards apply the same restrictions before saving or endpoint interrogation. Model candidates are copied into a draft and written only after an explicit save; existing fields that the editor does not expose survive edits.

The section declares `settings.models.provider-card` for keyed provider contributions and `settings.models.footer` for ordered additions. Slot registration and dictionaries follow the owning plugin's disposal lifecycle. Product analytics callbacks are absent.

## Further Exploration

- [LLM adapters](../../llm/README.md) own model routing and administrator-provisioned capabilities.
- [Client settings](../ui-settings/README.md) owns configuration forms and immutable schema operations.
- [Credentials](../../credentials/credentials/README.md) owns write-only secret storage.

## Model Experience

None. This browser plugin registers no model-facing tools or prompt sections.

#### KV Cache effect

None. Settings display and draft editing do not construct model requests.

## Known Limitations and Deferred Work

- The local-only policy is enforced by the UI; administrator APIs remain available by design.
- The curated editor does not expose every adapter option. Administrators configure remaining options in the profile document.
- The loopback URL identifies the machine running the provider request. Remote browser deployments must provision their routes administratively.

## Dev Note

**Runtime invariant:** No companion is published. The plugin renders joined Host state and routes mutations through existing configuration and credential owners.
