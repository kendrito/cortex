import { describe, expect, it } from 'vitest'
import { createUserMessage } from '@cortex/llm'
import type { UserMessage } from '@cortex/llm'
import {
  deriveBrowserTimeZoneContext,
  renderBrowserTimeZoneContext,
} from '../src/request-zone.ts'

function browserMessage(timeZone: string): UserMessage {
  return createUserMessage({
    content: [{ type: 'text', text: timeZone }],
    source: { kind: 'user', rpcId: `rpc-${timeZone}`, clientTimeZone: timeZone } as never,
  })
}

describe('browser request-zone context', () => {
  it('derives missing, unique, and sorted mixed zones from user-rpc messages only', () => {
    const plugin = createUserMessage({
      content: [{ type: 'text', text: 'plugin' }],
      source: { kind: 'plugin', plugin: 'test' },
    })
    expect(deriveBrowserTimeZoneContext([plugin])).toEqual({ kind: 'missing' })
    expect(deriveBrowserTimeZoneContext([
      browserMessage('Asia/Singapore'),
      browserMessage('Asia/Singapore'),
    ])).toEqual({ kind: 'resolved', timeZone: 'Asia/Singapore' })
    expect(deriveBrowserTimeZoneContext([
      browserMessage('Asia/Singapore'),
      browserMessage('America/New_York'),
    ])).toEqual({
      kind: 'mixed',
      timeZones: ['America/New_York', 'Asia/Singapore'],
    })
  })

  it('validates every browser zone before classifying a mixed turn', () => {
    expect(() => deriveBrowserTimeZoneContext([
      browserMessage('+08:00'),
    ])).toThrow(/canonical UTC or IANA Area\/Location/)
    expect(() => deriveBrowserTimeZoneContext([
      browserMessage('Asia/Singapore'),
      browserMessage('Not/A_Real_Zone'),
    ])).toThrow(/browser time zone is unsupported/)
    expect(() => deriveBrowserTimeZoneContext([
      browserMessage('Etc/UTC'),
    ])).toThrow(/browser time zone must be canonical/)
  })

  it('renders one explicit model policy for every context', () => {
    expect(renderBrowserTimeZoneContext({ kind: 'resolved', timeZone: 'Asia/Singapore' }))
      .toContain('Interpret otherwise-unqualified dates and times in this zone.')
    expect(renderBrowserTimeZoneContext({
      kind: 'mixed', timeZones: ['America/New_York', 'Asia/Singapore'],
    })).toContain('mixed ["America/New_York","Asia/Singapore"]')
    expect(renderBrowserTimeZoneContext({ kind: 'missing' })).toContain('unavailable')
  })
})
