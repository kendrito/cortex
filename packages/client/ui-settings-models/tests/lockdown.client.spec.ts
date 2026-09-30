/**
 * The provider-UI lockdown policy module: what counts as the approved local
 * LiteLLM gateway, which wire protocols the UI still offers, and when a
 * configured row's mutating affordances are disabled. Component behavior is
 * pinned in the section and form specs; this file pins the policy itself.
 */

import { describe, expect, it } from 'vitest'
import { isApprovedLocalEndpoint, lockedProtocolChoices, rowLocked } from '../src/client/lockdown.ts'

describe('isApprovedLocalEndpoint', () => {
  it.each([
    'http://127.0.0.1:4000/v1',
    'http://localhost:4000/v1',
    'http://localhost/v1',
    'https://127.0.0.1/openai',
    'http://[::1]:4000/v1',
  ])('accepts the local gateway form %s', (url) => {
    expect(isApprovedLocalEndpoint(url)).toBe(true)
  })

  it.each([
    'https://api.openai.com/v1',
    'https://api.mistral.ai/v1',
    'http://192.168.1.5:4000/v1', // LAN is not the local machine
    'http://litellm.internal:4000/v1',
    'ftp://127.0.0.1/v1',
    'file:///etc/passwd',
    '127.0.0.1:4000/v1', // no scheme — does not parse as an absolute URL
    'not a url',
    'http://evil.example/?host=127.0.0.1', // the loopback must be the HOST
  ])('rejects %s', (url) => {
    expect(isApprovedLocalEndpoint(url)).toBe(false)
  })
})

describe('lockedProtocolChoices', () => {
  it('narrows the schema union to the OpenAI-shaped protocols LiteLLM speaks', () => {
    expect(lockedProtocolChoices(['openai-completions', 'openai-responses', 'anthropic-messages']))
      .toEqual(['openai-completions', 'openai-responses'])
  })

  it('never widens: an unknown protocol is dropped, not invented', () => {
    expect(lockedProtocolChoices(['anthropic-messages', 'carrier-pigeon'])).toEqual([])
  })
})

describe('rowLocked', () => {
  it('locks a profile with no endpoint — it rides the vendor catalog default', () => {
    expect(rowLocked({})).toBe(true)
    expect(rowLocked({ apiKeyEnv: 'OPENAI_API_KEY' })).toBe(true)
  })

  it('locks a missing or malformed profile value', () => {
    expect(rowLocked(undefined)).toBe(true)
    expect(rowLocked('not a profile')).toBe(true)
    expect(rowLocked({ baseURL: 42 })).toBe(true)
  })

  it('locks a local non-OpenAI route', () => {
    expect(rowLocked({ baseURL: 'http://localhost:4000', api: 'anthropic-messages' })).toBe(true)
  })

  it('locks an external endpoint and frees the local gateway', () => {
    expect(rowLocked({ baseURL: 'https://api.anthropic.com' })).toBe(true)
    expect(rowLocked({ baseURL: 'http://127.0.0.1:4000/v1' })).toBe(false)
    expect(rowLocked({ baseURL: 'http://localhost:8080' })).toBe(false)
  })
})
