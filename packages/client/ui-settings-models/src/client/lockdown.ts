/**
 * Provider-configuration lockdown for the Models settings page.
 *
 * DEPLOYMENT POLICY (UI-only, by explicit decision): end users must not be
 * able to configure EXTERNAL LLM providers from this UI — every affordance
 * that would add or edit one renders disabled ("not clickable") — with a
 * single exception: an approved LOCAL LiteLLM gateway (an OpenAI-compatible
 * server on localhost, e.g. `http://127.0.0.1:4000/v1`).
 *
 * What this deliberately does NOT do:
 * - It does not change the backend. The `llm-pi-ai` settings schema, the
 *   settings-write gateway, and `settings.yaml` still accept every provider
 *   shape (openai-completions, openai-responses, anthropic-messages, catalog
 *   vendors, custom gateways). Those routes are kept fully functional so that
 *   administrator-provisioned configuration — the `settings.yaml` file on the
 *   host — keeps working unchanged. See the companion comments in
 *   `packages/llm/llm-pi-ai/src/{provider,catalog,config,index,discovery}.ts`.
 * - It does not hide administrator-provisioned rows. An external provider
 *   configured in `settings.yaml` still renders and still serves models; its
 *   Edit/Delete buttons are simply disabled here.
 *
 * Because the restriction lives in the client only, it is a UX guard, not a
 * security boundary: the settings API itself remains permissive by design.
 */

/**
 * Master switch for the Models-page lockdown. UI scope only — flipping this
 * back to `false` restores the previous fully-open configuration surface
 * without touching any other code path.
 */
export const PROVIDER_UI_LOCKDOWN: boolean = true

/**
 * The wire protocols the locked-down UI still offers when declaring or
 * editing a route. LiteLLM speaks the OpenAI wire format, so only the two
 * OpenAI-shaped protocols are offered here. `anthropic-messages` (and every
 * catalog/gateway route) remains fully supported by the adapter for
 * file-configured routes — the UI just stops offering it.
 */
const UI_PROTOCOLS = new Set(['openai-completions', 'openai-responses'])

/** Hostnames that count as the local machine for the approved gateway. */
const LOCAL_HOSTNAMES = new Set(['localhost', '127.0.0.1', '[::1]', '::1'])

/**
 * Whether a base URL points at the approved local LiteLLM gateway: it must
 * parse, be http(s), and target the local machine. Port and path are free —
 * LiteLLM commonly serves `http://127.0.0.1:4000/v1`, but any local port
 * works.
 * @param baseURL - the candidate endpoint text.
 * @returns true when the endpoint is local and therefore configurable here.
 */
export function isApprovedLocalEndpoint(baseURL: string): boolean {
  let url: URL
  try {
    url = new URL(baseURL)
  } catch {
    return false
  }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') return false
  return LOCAL_HOSTNAMES.has(url.hostname.toLowerCase())
}

/**
 * Filter the schema-derived protocol union down to what the locked-down UI
 * offers. The input comes from `protocolChoices` (the adapter's own schema),
 * so nothing here can offer a protocol the adapter would refuse; the filter
 * only narrows the offer, never widens it.
 * @param all - the protocol identifiers the schema names.
 * @returns the subset the UI selects may list.
 */
export function lockedProtocolChoices(all: readonly string[]): string[] {
  if (!PROVIDER_UI_LOCKDOWN) return [...all]
  return all.filter(choice => UI_PROTOCOLS.has(choice))
}

/**
 * Whether a configured provider row is locked (Edit/Delete not clickable).
 * A row is editable only when its effective profile names a base URL that
 * passes {@link isApprovedLocalEndpoint}. A profile with NO base URL uses the
 * vendor's catalog default endpoint — external by definition — so it locks.
 * @param profile - the row's effective profile value (from the merged
 *   settings namespace at the row's settings path), or undefined.
 * @returns true when the row's mutating affordances must be disabled.
 */
export function rowLocked(profile: unknown): boolean {
  if (!PROVIDER_UI_LOCKDOWN) return false
  if (typeof profile !== 'object' || profile === null) return true
  const protocol = (profile as { api?: unknown }).api
  if (protocol !== undefined && (typeof protocol !== 'string' || !UI_PROTOCOLS.has(protocol))) return true
  const baseURL = (profile as { baseURL?: unknown }).baseURL
  if (typeof baseURL !== 'string' || baseURL.length === 0) return true
  return !isApprovedLocalEndpoint(baseURL)
}
