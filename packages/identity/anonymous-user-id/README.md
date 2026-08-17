# @cortex/anonymous-user-id


Shared anonymous identity for session telemetry and direct feedback acknowledgement. `getOrCreateAnonymousUserId()` returns a random UUID v4 scoped to one harness home, persisted as the bare line `$CORTEX_HOME/.anonymous-user-id` (`~/.cortex/.anonymous-user-id` when `CORTEX_HOME` is unset). The OpenTelemetry backend reports it as Resource `user.id` and `/feedback` includes the same value in its acknowledgement, allowing the receiving systems to correlate records without independently generated identities.

The identity is never derived from the hostname, network address, git remote, or another identifying source. Deleting `.anonymous-user-id` resets the identity on the next process launch. Separate harness homes have separate identities.

## Storage contract

Reads and writes are synchronous because both boot-time telemetry construction and direct command execution need one API. The result is memoized per resolved file path for the process lifetime. A first writer uses exclusive creation and a concurrent loser adopts the persisted winner; a corrupt file is replaced. Persistence is best-effort, so an unwritable home still receives a process-local UUID rather than blocking telemetry or feedback.

## Composition

This package is a shared library, not a Cordis plugin. Consumers import `getOrCreateAnonymousUserId()` directly. Its invariant companion is intentionally empty because the package owns no event stream or public mutable relation that can be checked without creating the identity as a side effect. `CORTEX_TELEMETRY_DISABLED` stops telemetry export only; it does not suppress direct feedback acknowledgement.

## Model Experience

None: the identifier never enters a request body, prompt, or model-visible content.

#### KV Cache effect

None; the identifier changes neither tokens nor the model-visible prefix.

## Known Limitations and Deferred Work

- **No recovery after deletion** — loss mints a new anonymous identity by design; recovery would require stable derivation material that weakens anonymity.
- **Best-effort concurrency** — a reader landing in the narrow interval between a concurrent process's exclusive create and completed write can use a different in-memory UUID for that run; later launches converge on the persisted value.
- **No cross-home identity** — different `$CORTEX_HOME` values cannot be correlated.
