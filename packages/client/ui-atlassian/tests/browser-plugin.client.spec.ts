/**
 * ui-atlassian browser half on a real SlotRegistry: the header action with its
 * store, one keyed tool card per Atlassian tool plus the two review tools, and
 * the settings tab; every injected face routes through the generated Remote
 * faces / session binding / connection API and folds failures into the panel
 * vocabulary; teardown empties the seats (HMR safety).
 */
import { Context } from '@cortex/cordis'
import { describe, expect, it, vi } from 'vitest'
import { SlotRegistry } from '@cortex/client-runtime/client'
import type { SessionId } from '@cortex/client-runtime/client'
import { LocaleRuntime } from '@cortex/client-locale/client'
import { AtlassianCard } from '../src/client/cards/AtlassianCard.tsx'
import { FindingRow, ReviewCompleteRow } from '../src/client/cards/FindingRow.tsx'
import type { CardFace, PanelFace, SettingsFace } from '../src/client/contract.ts'
import { CARD_TOOLS, apply, inject } from '../src/client/index.ts'
import { AtlassianAction } from '../src/client/panel/AtlassianAction.tsx'
import { AtlassianSettingsTab } from '../src/client/settings/AtlassianSettingsTab.tsx'

const SID = 's-atl' as SessionId
const OK = { ok: true as const, value: { ok: true } }
const FAIL = { ok: false as const, error: { code: 'internal', message: 'boom', details: {} } }

function remotes() {
  const atlassian = {
    open: vi.fn(() => Promise.resolve({ ok: true, value: { ok: true, entity: { kind: 'issue', key: 'PROJ-1' } } })),
    pin: vi.fn(() => Promise.resolve(OK)),
    listPullRequests: vi.fn(() => Promise.resolve({ ok: true, value: { ok: true, items: [] } })),
    postFinding: vi.fn(() => Promise.resolve({ ok: true, value: { ok: true, commentId: 1, mode: 'inline' } })),
    dismissFinding: vi.fn(() => Promise.resolve(OK)),
    cancelReview: vi.fn(() => Promise.resolve(OK)),
    diffContext: vi.fn(() => Promise.resolve({ ok: true, value: { ok: true, file: 'x', found: true, lines: [] } })),
    status: vi.fn(() => Promise.resolve({ ok: true, value: { atlassian: { phase: 'off', toolCount: 0 }, bitbucket: { phase: 'off', toolCount: 0 }, rest: {} } })),
    probe: vi.fn(() => Promise.resolve({ ok: true, value: { service: 'jira', ok: true, user: 'K' } })),
    reconnect: vi.fn(() => Promise.resolve({ ok: true, value: { atlassian: { phase: 'ready', toolCount: 3 }, bitbucket: { phase: 'off', toolCount: 0 }, rest: {} } })),
  }
  const commands = {
    execute: vi.fn(() => Promise.resolve({ ok: true, value: { commandId: 'c1', result: { kind: 'success' as const } } })),
  }
  return { atlassian, commands }
}

async function bench(options: { loopback?: boolean; binding?: boolean } = {}) {
  const ctx = new Context()
  await ctx.plugin(SlotRegistry).await()
  const slots = ctx.get('slots') as SlotRegistry
  slots.register({
    name: 'root',
    children: {
      'conversation.session.header.actions': { kind: 'list', scope: 'session' },
      'tool.call.toolview': { kind: 'keyed', scope: 'session' },
      'settings.plugins.tab': { kind: 'list', scope: 'root' },
    },
  } as never, () => null)
  const remote = remotes()
  ctx.provide('remote', remote)
  ctx.provide('remote.atlassian', remote.atlassian)
  ctx.provide('remote.commands', remote.commands)
  ctx.provide('locale', new LocaleRuntime(ctx))
  const prompt = vi.fn(() => Promise.resolve({ ok: true as const, value: { accepted: true } }))
  const sessions = {
    binding: vi.fn((id: SessionId) => (options.binding === false ? undefined : (id === SID ? { session: { prompt } } : undefined))),
  }
  ctx.provide('sessions', sessions)
  const credentials = {
    describe: vi.fn(() => Promise.resolve({ result: { ok: true, value: { credentials: { A: { configured: true, writable: true, source: 'file' } } } } })),
    set: vi.fn(() => Promise.resolve({ result: { ok: true, value: {} } })),
  }
  ctx.provide('connection', { isLoopback: options.loopback ?? true, api: { credentials } })
  const scope = { getSnapshot: vi.fn(() => ({ status: 'ready' })), subscribe: vi.fn(() => () => {}), set: vi.fn(() => Promise.resolve()), unset: vi.fn() }
  const settingsScope = { bind: vi.fn(() => scope) }
  ctx.provide('settingsScope', settingsScope)
  const fiber = ctx.plugin({ inject: [...inject], apply })
  await fiber.await()
  return { ctx, slots, remote, prompt, sessions, credentials, scope, settingsScope, fiber }
}

describe('ui-atlassian browser apply', () => {
  it('declares every service it binds', () => {
    expect(inject).toEqual(['slots', 'locale', 'sessions', 'remote', 'remote.atlassian', 'remote.commands', 'connection', 'settingsScope'])
  })

  it('registers the header action, one card per tool, and the settings tab; teardown removes them', async () => {
    const b = await bench()
    const [action] = b.slots.entries('conversation.session.header.actions')
    expect(action?.component).toBe(AtlassianAction)
    expect(action?.locale).toBe('atlassian')
    expect(action?.store).toBeDefined()
    expect(action?.options.id).toBe('atlassian')

    const cards = b.slots.entries('tool.call.toolview')
    expect(cards).toHaveLength(CARD_TOOLS.length + 2)
    const keys = new Set(cards.map(entry => entry.options.key))
    expect(keys.has('mcp__atlassian__jira_get_issue')).toBe(true)
    expect(keys.has('mcp__bitbucket__bitbucket_get_pull_request_diff')).toBe(true)
    expect(cards.filter(entry => entry.component === AtlassianCard)).toHaveLength(CARD_TOOLS.length)
    expect(cards.find(entry => entry.options.key === 'atlassian_review_finding')?.component).toBe(FindingRow)
    expect(cards.find(entry => entry.options.key === 'atlassian_review_complete')?.component).toBe(ReviewCompleteRow)
    // Every card shares the panel store handle with the action.
    for (const card of cards) expect(card.store).toBe(action?.store)

    const [tab] = b.slots.entries('settings.plugins.tab')
    expect(tab?.component).toBe(AtlassianSettingsTab)
    expect(typeof tab?.options.label === 'function' ? tab.options.label() : tab?.options.label).toBe('Atlassian')
    expect(b.settingsScope.bind).toHaveBeenCalledWith({ namespace: 'atlassian' })

    await b.fiber.dispose()
    expect(b.slots.entries('conversation.session.header.actions')).toHaveLength(0)
    expect(b.slots.entries('tool.call.toolview')).toHaveLength(0)
    expect(b.slots.entries('settings.plugins.tab')).toHaveLength(0)
  })

  it('routes the panel face through the Remote faces and folds failures', async () => {
    const b = await bench()
    const [action] = b.slots.entries('conversation.session.header.actions')
    const face = (action?.inject as unknown as (id: SessionId) => PanelFace)(SID)

    await expect(face.open({ kind: 'issue', key: 'PROJ-1' })).resolves.toEqual({ ok: true, entity: { kind: 'issue', key: 'PROJ-1' } })
    expect(b.remote.atlassian.open).toHaveBeenCalledWith(SID, { kind: 'issue', key: 'PROJ-1' })
    b.remote.atlassian.open.mockResolvedValueOnce(FAIL as never)
    await expect(face.open({ kind: 'page', id: '1' })).resolves.toEqual({ ok: false, code: 'internal', message: 'boom' })

    await expect(face.pin('PROJ-1')).resolves.toEqual({ ok: true })
    expect(b.remote.atlassian.pin).toHaveBeenCalledWith(SID, { key: 'PROJ-1' })
    b.remote.atlassian.pin.mockResolvedValueOnce(FAIL as never)
    await expect(face.pin(null)).resolves.toEqual({ ok: false, code: 'internal', message: 'boom' })

    await expect(face.sendPrompt('hello')).resolves.toEqual({ ok: true })
    expect(b.prompt).toHaveBeenCalledWith([{ type: 'text', text: 'hello' }], 'queue')
    b.prompt.mockResolvedValueOnce({ ok: false, error: { code: 'busy', message: 'no' } } as never)
    await expect(face.sendPrompt('again')).resolves.toEqual({ ok: false, message: 'busy: no' })

    await expect(face.listPullRequests({ scope: 'inbox' })).resolves.toEqual({ ok: true, items: [] })
    b.remote.atlassian.listPullRequests.mockResolvedValueOnce(FAIL as never)
    await expect(face.listPullRequests({ scope: 'inbox' })).resolves.toEqual({ ok: false, code: 'internal', message: 'boom' })

    await expect(face.startReview({ project: 'PROJ', repo: 'webapp', id: 42 }, '')).resolves.toEqual({ ok: true })
    expect(b.remote.commands.execute).toHaveBeenLastCalledWith(SID, '/pr-review PROJ/webapp#42')
    await face.startReview({ project: 'PROJ', repo: 'webapp', id: 42 }, '  security  ')
    expect(b.remote.commands.execute).toHaveBeenLastCalledWith(SID, '/pr-review PROJ/webapp#42 security')
    b.remote.commands.execute.mockResolvedValueOnce({ ok: false, error: { code: 'session-not-found', message: 'gone', details: {} } } as never)
    await expect(face.startReview({ project: 'P', repo: 'r', id: 1 }, '')).resolves.toEqual({ ok: false, message: 'gone (session-not-found)' })
    b.remote.commands.execute.mockResolvedValueOnce({ ok: true, value: undefined } as never)
    await expect(face.startReview({ project: 'P', repo: 'r', id: 1 }, '')).resolves.toEqual({ ok: false, message: 'unknown command: /pr-review' })
    b.remote.commands.execute.mockResolvedValueOnce({ ok: true, value: { commandId: 'c', result: { kind: 'error', text: 'Usage: /pr-review' } } } as never)
    await expect(face.startReview({ project: 'P', repo: 'r', id: 1 }, '')).resolves.toEqual({ ok: false, message: 'Usage: /pr-review' })

    await expect(face.postFinding({ reviewId: 'r', findingId: 'f' })).resolves.toEqual({ ok: true, commentId: 1, mode: 'inline' })
    expect(b.remote.atlassian.postFinding).toHaveBeenCalledWith(SID, { reviewId: 'r', findingId: 'f' })
    b.remote.atlassian.postFinding.mockResolvedValueOnce(FAIL as never)
    await expect(face.postFinding({ reviewId: 'r', findingId: 'f' })).resolves.toEqual({ ok: false, code: 'internal', message: 'boom' })

    await expect(face.dismissFinding({ reviewId: 'r', findingId: 'f' })).resolves.toEqual({ ok: true })
    b.remote.atlassian.dismissFinding.mockResolvedValueOnce(FAIL as never)
    await expect(face.dismissFinding({ reviewId: 'r', findingId: 'f' })).resolves.toEqual({ ok: false, code: 'internal', message: 'boom' })

    await expect(face.cancelReview('r')).resolves.toEqual({ ok: true })
    expect(b.remote.atlassian.cancelReview).toHaveBeenCalledWith(SID, { reviewId: 'r' })
    b.remote.atlassian.cancelReview.mockResolvedValueOnce(FAIL as never)
    await expect(face.cancelReview('r')).resolves.toEqual({ ok: false, code: 'internal', message: 'boom' })

    const request = { pr: { project: 'P', repo: 'r', id: 1 }, file: 'a.ts', line: 3, side: 'ADDED' as const }
    await expect(face.diffContext(request)).resolves.toEqual({ ok: true, file: 'x', found: true, lines: [] })
    expect(b.remote.atlassian.diffContext).toHaveBeenCalledWith(request)
    b.remote.atlassian.diffContext.mockResolvedValueOnce(FAIL as never)
    await expect(face.diffContext(request)).resolves.toEqual({ ok: false, code: 'internal', message: 'boom' })

    await expect(face.status()).resolves.toMatchObject({ atlassian: { phase: 'off' } })
    b.remote.atlassian.status.mockResolvedValueOnce(FAIL as never)
    await expect(face.status()).rejects.toThrow('atlassian.status failed: internal: boom')
  })

  it('reports a missing session binding for prompts', async () => {
    const b = await bench({ binding: false })
    const [action] = b.slots.entries('conversation.session.header.actions')
    const face = (action?.inject as unknown as (id: SessionId) => PanelFace)(SID)
    await expect(face.sendPrompt('x')).resolves.toEqual({ ok: false, message: 'session is not open' })
    expect(b.prompt).not.toHaveBeenCalled()
  })

  it('gives every card an open verb bound to its session', async () => {
    const b = await bench()
    const card = b.slots.entries('tool.call.toolview').find(entry => entry.options.key === 'mcp__atlassian__jira_search')
    const face = (card?.inject as unknown as (id: SessionId) => CardFace)(SID)
    await expect(face.open({ kind: 'issue', key: 'PROJ-1' })).resolves.toEqual({ ok: true, entity: { kind: 'issue', key: 'PROJ-1' } })
    expect(b.remote.atlassian.open).toHaveBeenCalledWith(SID, { kind: 'issue', key: 'PROJ-1' })
  })

  it('routes the settings face through the scope, credentials API, and Remote faces', async () => {
    const b = await bench()
    const [tab] = b.slots.entries('settings.plugins.tab')
    const face = (tab?.inject as unknown as () => SettingsFace)()
    expect(face.writable).toBe(true)
    expect(face.hooks.settings).toBe(b.scope)

    await expect(face.setField('jiraUrl', 'https://j')).resolves.toEqual({ ok: true })
    expect(b.scope.set).toHaveBeenCalledWith('jiraUrl', 'https://j')
    b.scope.set.mockRejectedValueOnce(new Error('conflict'))
    await expect(face.setField('jiraUrl', 'x')).resolves.toEqual({ ok: false, message: 'conflict' })
    b.scope.set.mockRejectedValueOnce('raw')
    await expect(face.setField('jiraUrl', 'x')).resolves.toEqual({ ok: false, message: 'raw' })

    await expect(face.describeTokens(['A'])).resolves.toEqual({ A: { configured: true, writable: true } })
    expect(b.credentials.describe).toHaveBeenCalledWith({ refs: ['A'] })
    b.credentials.describe.mockResolvedValueOnce({ result: { ok: false, error: { code: 'x', message: 'y' } } } as never)
    await expect(face.describeTokens(['A'])).resolves.toEqual({})

    await expect(face.setToken('A', 'secret')).resolves.toEqual({ ok: true })
    expect(b.credentials.set).toHaveBeenCalledWith({ ref: 'A', value: 'secret' })
    b.credentials.set.mockResolvedValueOnce({ result: { ok: false, error: { code: 'credential-rejected', message: 'shadowed' } } } as never)
    await expect(face.setToken('A', 'secret')).resolves.toEqual({ ok: false, message: 'shadowed' })

    await expect(face.status()).resolves.toMatchObject({ atlassian: { phase: 'off' } })
    b.remote.atlassian.status.mockResolvedValueOnce(FAIL as never)
    await expect(face.status()).rejects.toThrow('atlassian.status failed')

    await expect(face.probe('jira')).resolves.toEqual({ service: 'jira', ok: true, user: 'K' })
    expect(b.remote.atlassian.probe).toHaveBeenCalledWith({ service: 'jira' })
    b.remote.atlassian.probe.mockResolvedValueOnce(FAIL as never)
    await expect(face.probe('bitbucket')).resolves.toEqual({ service: 'bitbucket', ok: false, error: 'internal: boom' })

    await expect(face.reconnect()).resolves.toMatchObject({ atlassian: { phase: 'ready', toolCount: 3 } })
    b.remote.atlassian.reconnect.mockResolvedValueOnce(FAIL as never)
    await expect(face.reconnect()).rejects.toThrow('atlassian.reconnect failed')
  })

  it('marks the settings face read-only off loopback', async () => {
    const b = await bench({ loopback: false })
    const [tab] = b.slots.entries('settings.plugins.tab')
    const face = (tab?.inject as unknown as () => SettingsFace)()
    expect(face.writable).toBe(false)
  })

  it('waits until the parent slots are declared', async () => {
    const ctx = new Context()
    await ctx.plugin(SlotRegistry).await()
    const remote = remotes()
    ctx.provide('remote', remote)
    ctx.provide('remote.atlassian', remote.atlassian)
    ctx.provide('remote.commands', remote.commands)
    ctx.provide('locale', new LocaleRuntime(ctx))
    ctx.provide('sessions', { binding: () => undefined })
    ctx.provide('connection', { isLoopback: true, api: { credentials: {} } })
    ctx.provide('settingsScope', { bind: () => ({ getSnapshot: () => ({}), subscribe: () => () => {}, set: () => Promise.resolve(), unset: () => Promise.resolve() }) })
    const fiber = ctx.plugin({ inject: [...inject], apply })
    await fiber.await()
    expect(ctx.slots.entries('settings.plugins.tab')).toHaveLength(0)
    ctx.slots.register({ name: 'root', children: { 'settings.plugins.tab': { kind: 'list', scope: 'root' } } } as never, () => null)
    await Promise.resolve()
    expect(ctx.slots.entries('settings.plugins.tab')).toHaveLength(1)
  })
})
