/**
 * Minimal authenticated JSON HTTP client shared by the three Data Center REST
 * adapters. Bearer personal-access-token auth, one bounded timeout per call,
 * typed failures, and no retries — the host calls are user-visible actions
 * whose failure should surface, not repeat.
 *
 * @module
 */

/** Failure of one REST call, safe to relay to the browser. */
export class RestError extends Error {
  /** Stable classification for UI copy. */
  readonly code: 'unauthorized' | 'forbidden' | 'not-found' | 'http' | 'network' | 'timeout' | 'invalid-json'
  /** HTTP status when the server answered. */
  readonly status: number | undefined

  /**
   * @param code - stable classification.
   * @param message - human-readable account.
   * @param status - HTTP status when the server answered.
   */
  constructor(code: RestError['code'], message: string, status?: number) {
    super(message)
    this.name = 'RestError'
    this.code = code
    this.status = status
  }
}

/** Fetch-compatible function; injectable for tests. */
export type FetchLike = (input: string, init: RequestInit) => Promise<Response>

/** One configured REST endpoint. */
export interface RestTarget {
  /** Base URL without trailing slash. */
  baseUrl: string
  /** Personal access token sent as `Authorization: Bearer`. */
  token: string
}

/** Default per-call timeout in milliseconds. */
export const REST_TIMEOUT_MS = 20_000

/** Bound applied to error bodies relayed in messages. */
const ERROR_BODY_LIMIT = 300

/**
 * Human account of one failed HTTP response, using the JSON error fields the
 * three Atlassian servers emit before falling back to raw text.
 * @param status - HTTP status.
 * @param body - response text.
 * @returns the message.
 */
export function describeHttpFailure(status: number, body: string): string {
  let detail = body.trim()
  try {
    const parsed = JSON.parse(body) as unknown
    if (typeof parsed === 'object' && parsed !== null) {
      const object = parsed as Record<string, unknown>
      const messages = Array.isArray(object.errorMessages) ? object.errorMessages : undefined
      const errors = Array.isArray(object.errors) ? object.errors : undefined
      const first = messages?.find((item): item is string => typeof item === 'string')
        ?? errors?.map(item => (typeof item === 'object' && item !== null ? (item as Record<string, unknown>).message : undefined))
          .find((item): item is string => typeof item === 'string')
        ?? (typeof object.message === 'string' ? object.message : undefined)
      if (first !== undefined) detail = first
    }
  } catch {
    // Not JSON: the raw text is the best account available.
  }
  const shown = detail.length > ERROR_BODY_LIMIT ? `${detail.slice(0, ERROR_BODY_LIMIT)}…` : detail
  return shown === '' ? `HTTP ${String(status)}` : `HTTP ${String(status)}: ${shown}`
}

/**
 * Perform one JSON request against a target.
 * @param fetchImpl - fetch implementation.
 * @param target - base URL and token.
 * @param method - HTTP method.
 * @param path - path starting with `/`, may carry a query string.
 * @param body - JSON body for POST/PUT.
 * @param signal - optional caller cancellation.
 * @returns the parsed JSON body (`undefined` for an empty 2xx body).
 */
export async function requestJson(
  fetchImpl: FetchLike,
  target: RestTarget,
  method: 'GET' | 'POST' | 'PUT' | 'DELETE',
  path: string,
  body?: unknown,
  signal?: AbortSignal,
): Promise<unknown> {
  const controller = new AbortController()
  const timer = setTimeout(() => { controller.abort() }, REST_TIMEOUT_MS)
  const onOuterAbort = (): void => { controller.abort() }
  signal?.addEventListener('abort', onOuterAbort, { once: true })
  try {
    let response: Response
    try {
      response = await fetchImpl(`${target.baseUrl}${path}`, {
        method,
        headers: {
          'Authorization': `Bearer ${target.token}`,
          'Accept': 'application/json',
          ...body === undefined ? {} : { 'Content-Type': 'application/json' },
          'X-Atlassian-Token': 'no-check',
        },
        ...body === undefined ? {} : { body: JSON.stringify(body) },
        signal: controller.signal,
      })
    } catch (error: unknown) {
      if (controller.signal.aborted && signal?.aborted !== true) {
        throw new RestError('timeout', `request to ${target.baseUrl}${path} timed out after ${String(REST_TIMEOUT_MS)} ms`)
      }
      throw new RestError('network', `request to ${target.baseUrl}${path} failed: ${error instanceof Error ? error.message : String(error)}`)
    }
    const text = await response.text()
    if (!response.ok) {
      const message = describeHttpFailure(response.status, text)
      if (response.status === 401) throw new RestError('unauthorized', `${message} — check the personal access token`, 401)
      if (response.status === 403) throw new RestError('forbidden', message, 403)
      if (response.status === 404) throw new RestError('not-found', message, 404)
      throw new RestError('http', message, response.status)
    }
    if (text.trim() === '') return undefined
    try {
      return JSON.parse(text) as unknown
    } catch {
      throw new RestError('invalid-json', `response from ${target.baseUrl}${path} was not JSON`, response.status)
    }
  } finally {
    clearTimeout(timer)
    signal?.removeEventListener('abort', onOuterAbort)
  }
}

/**
 * Build a query string from defined values.
 * @param params - key/value pairs; `undefined` values are skipped.
 * @returns `?a=b&c=d` or the empty string.
 */
export function query(params: Record<string, string | number | boolean | undefined>): string {
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined) search.set(key, String(value))
  }
  const rendered = search.toString()
  return rendered === '' ? '' : `?${rendered}`
}
