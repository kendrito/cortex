import { afterEach, describe, expect, it, vi } from 'vitest'
import { REST_TIMEOUT_MS, RestError, describeHttpFailure, query, requestJson, type FetchLike } from '../src/rest/http.ts'
import { containing, rejectWith } from './fixtures.ts'

const target = { baseUrl: 'https://jira.example.com', token: 'tok' }

function respond(status: number, text: string): FetchLike {
  return () => Promise.resolve(new Response(text, { status }))
}

afterEach(() => { vi.useRealTimers() })

describe('describeHttpFailure', () => {
  it('prefers the Atlassian error fields and falls back to raw text', () => {
    expect(describeHttpFailure(400, JSON.stringify({ errorMessages: ['Issue Does Not Exist'] }))).toBe('HTTP 400: Issue Does Not Exist')
    expect(describeHttpFailure(400, JSON.stringify({ errorMessages: [1], errors: [{ message: 'field bad' }] }))).toBe('HTTP 400: field bad')
    expect(describeHttpFailure(400, JSON.stringify({ errors: ['not an object', null], message: 'top level' }))).toBe('HTTP 400: top level')
    expect(describeHttpFailure(400, JSON.stringify({ unrelated: true }))).toBe('HTTP 400: {"unrelated":true}')
    expect(describeHttpFailure(500, 'plain text body')).toBe('HTTP 500: plain text body')
    expect(describeHttpFailure(502, '   ')).toBe('HTTP 502')
    expect(describeHttpFailure(500, JSON.stringify(['array']))).toBe('HTTP 500: ["array"]')
    expect(describeHttpFailure(500, 'x'.repeat(400))).toBe(`HTTP 500: ${'x'.repeat(300)}…`)
  })
})

describe('requestJson', () => {
  it('sends the bearer token, JSON body, and parses the response', async () => {
    const calls: { url: string; init: RequestInit }[] = []
    const fetchImpl: FetchLike = (url, init) => {
      calls.push({ url, init })
      return Promise.resolve(new Response(JSON.stringify({ ok: true }), { status: 200 }))
    }
    await expect(requestJson(fetchImpl, target, 'POST', '/rest/x?y=1', { a: 1 })).resolves.toEqual({ ok: true })
    expect(calls[0]?.url).toBe('https://jira.example.com/rest/x?y=1')
    expect(calls[0]?.init.method).toBe('POST')
    expect(calls[0]?.init.body).toBe('{"a":1}')
    expect(calls[0]?.init.headers).toMatchObject({ 'Authorization': 'Bearer tok', 'Content-Type': 'application/json' })
    const getCalls: RequestInit[] = []
    await requestJson((_url, init) => { getCalls.push(init); return Promise.resolve(new Response('', { status: 200 })) }, target, 'GET', '/x')
    expect(getCalls[0]?.body).toBeUndefined()
    expect(getCalls[0]?.headers).not.toHaveProperty('Content-Type')
  })

  it('reads an empty 2xx body as undefined and rejects non-JSON', async () => {
    await expect(requestJson(respond(200, '  '), target, 'GET', '/x')).resolves.toBeUndefined()
    await expect(requestJson(respond(200, 'not json'), target, 'GET', '/x')).rejects.toMatchObject({ code: 'invalid-json', status: 200 })
  })

  it.each([
    [401, 'unauthorized', 'HTTP 401: nope — check the personal access token'],
    [403, 'forbidden', 'HTTP 403: nope'],
    [404, 'not-found', 'HTTP 404: nope'],
    [500, 'http', 'HTTP 500: nope'],
  ] as const)('maps HTTP %i to %s', async (status, code, message) => {
    const error = await requestJson(respond(status, JSON.stringify({ message: 'nope' })), target, 'GET', '/x').catch((caught: unknown) => caught)
    expect(error).toBeInstanceOf(RestError)
    expect(error).toMatchObject({ code, status, message, name: 'RestError' })
  })

  it('reports network failures', async () => {
    const failing: FetchLike = () => Promise.reject(new Error('ECONNREFUSED'))
    await expect(requestJson(failing, target, 'GET', '/x')).rejects.toMatchObject({ code: 'network', message: containing('ECONNREFUSED') })
    const failingValue: FetchLike = () => rejectWith('boom')
    await expect(requestJson(failingValue, target, 'GET', '/x')).rejects.toMatchObject({ code: 'network', message: containing('boom') })
  })

  it('times out through its own controller', async () => {
    vi.useFakeTimers()
    const hanging: FetchLike = (_url, init) => new Promise((_resolve, reject) => {
      init.signal?.addEventListener('abort', () => { reject(new Error('aborted')) })
    })
    const pending = requestJson(hanging, target, 'GET', '/slow')
    const settled = pending.catch((caught: unknown) => caught)
    await vi.advanceTimersByTimeAsync(REST_TIMEOUT_MS + 1)
    expect(await settled).toMatchObject({ code: 'timeout' })
  })

  it('reports an outer abort as a network failure, not a timeout', async () => {
    const outer = new AbortController()
    const hanging: FetchLike = (_url, init) => new Promise((_resolve, reject) => {
      init.signal?.addEventListener('abort', () => { reject(new Error('aborted by caller')) })
    })
    const pending = requestJson(hanging, target, 'GET', '/slow', undefined, outer.signal)
    outer.abort()
    await expect(pending).rejects.toMatchObject({ code: 'network', message: containing('aborted by caller') })
  })
})

describe('query', () => {
  it('serializes defined values only', () => {
    expect(query({ a: 'x y', b: 2, c: true, d: undefined })).toBe('?a=x+y&b=2&c=true')
    expect(query({ d: undefined })).toBe('')
  })
})
