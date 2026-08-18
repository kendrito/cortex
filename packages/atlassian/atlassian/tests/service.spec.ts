import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Agent } from '@cortex/agent'
import Loader from '@cortex/cordis-plugin-loader'
import type { ToolExecution, ToolExecutionResult } from '@cortex/tools'
import * as AtlassianModule from '../src/index.ts'
import AtlassianService, { ATLASSIAN_SETTINGS_NAMESPACE, DEFAULT_ATLASSIAN_SETTINGS } from '../src/index.ts'
import type { AtlassianSnapshotEvent, ReviewFinding } from '../src/types.ts'
import { CONNECTED_SETTINGS, DEFAULT_FETCH, DEFAULT_ROUTES, REST, TOKENS, boot, type Bench } from './bench.ts'
import { BITBUCKET_PR, JIRA_ISSUE, containing, rejectWith, sentBody } from './fixtures.ts'

const GET_ISSUE = 'mcp__atlassian__jira_get_issue'
const SEARCH = 'mcp__atlassian__jira_search'
const CREATE_ISSUE = 'mcp__atlassian__jira_create_issue'
const DELETE_ISSUE = 'mcp__atlassian__jira_delete_issue'
const GET_PAGE = 'mcp__atlassian__confluence_get_page'
const GET_PR = 'mcp__bitbucket__bitbucket_get_pull_request_details'
const PR = { project: 'PROJ', repo: 'webapp', id: 42 }
const signal = new AbortController().signal

let live: Bench | undefined
afterEach(async () => {
  await live?.ctx.fiber.dispose()
  live = undefined
  vi.restoreAllMocks()
})

function finding(id: string, extra: Partial<ReviewFinding> = {}): ReviewFinding {
  return {
    id, at: 1, file: 'src/auth/redirect.ts', line: 3, side: 'ADDED', severity: 'critical', category: 'correctness',
    title: 'Open redirect', comment: 'Validate the target.', evidence: 'return decodeURIComponent(state)', rationale: 'because', ...extra,
  }
}

async function startReview(bench: Bench, reviewId = 'r1'): Promise<void> {
  bench.session.append('atlassian/review', { op: 'start', reviewId, pr: PR, at: 1, existing: [] })
  bench.session.append('atlassian/review', { op: 'finding', reviewId, finding: finding('f1') })
  bench.session.append('atlassian/review', { op: 'finding', reviewId, finding: finding('f2', { file: 'nowhere.ts', line: 900 }) })
  await Promise.resolve()
}

describe('AtlassianService composition', () => {
  it('has the Loader-safe service export shape', () => {
    expect(AtlassianModule.default).toBe(AtlassianService)
    expect(AtlassianService.inject).toEqual(['tools'])
    const loader = Object.create(Loader.prototype) as Loader
    expect(loader.unwrapExports(AtlassianModule)).toBe(AtlassianService)
    expect(String(ATLASSIAN_SETTINGS_NAMESPACE)).toBe('atlassian')
  })

  it('serves schema defaults without a settings provider and the section with one', async () => {
    live = await boot({ settings: false, credentials: false })
    expect(live.service.settings()).toEqual(DEFAULT_ATLASSIAN_SETTINGS)
    expect(live.service.status()).toEqual({
      atlassian: { phase: 'off', toolCount: 0, missing: ['url', 'token'] },
      bitbucket: { phase: 'off', toolCount: 0, missing: ['url', 'token'] },
      rest: { jira: false, confluence: false, bitbucket: false },
    })
    await live.ctx.fiber.dispose()
    live = await boot()
    expect(live.service.settings()).toMatchObject({ jiraUrl: REST.jira, writes: 'allow' })
    // The launch command cannot be found, so both mounts report an error without spawning anything.
    expect(live.service.status()).toEqual({
      atlassian: { phase: 'error', toolCount: 0, error: 'command not found: definitely-missing-binary-xyz' },
      bitbucket: { phase: 'error', toolCount: 0, error: 'command not found: definitely-missing-binary-xyz' },
      rest: { jira: true, confluence: true, bitbucket: true },
    })
  })

  it('re-plans on settings and credential changes', async () => {
    live = await boot()
    const reconcile = vi.spyOn(live.service, 'reconcile')
    await live.settings?.update(ATLASSIAN_SETTINGS_NAMESPACE, { bitbucketUrl: '' })
    await vi.waitFor(() => { expect(reconcile).toHaveBeenCalled() })
    await vi.waitFor(() => { expect(live?.service.status().bitbucket).toEqual({ phase: 'off', toolCount: 0, missing: ['url'] }) })
    reconcile.mockClear()
    await live.credentials?.set('ATLASSIAN_BITBUCKET_TOKEN' as never, 'new')
    expect(reconcile).toHaveBeenCalledTimes(1)
    await live.credentials?.set('UNRELATED' as never, 'x')
    expect(reconcile).toHaveBeenCalledTimes(1)
    // A malformed reference name and a blank token both read as no token.
    await live.settings?.update(ATLASSIAN_SETTINGS_NAMESPACE, { bitbucketUrl: REST.bitbucket, bitbucketTokenRef: 'ALSO_VALID' })
    await live.credentials?.set('ALSO_VALID' as never, '   ')
    await vi.waitFor(() => { expect(live?.service.status().bitbucket).toEqual({ phase: 'off', toolCount: 0, missing: ['token'] }) })
    // Reconnect recomputes plans and retries the errored cell.
    const status = await live.service.reconnect()
    expect(status.atlassian.phase).toBe('error')
    // Disposal stops later reconciles cold.
    await live.ctx.fiber.dispose()
    await expect(live.service.reconcile()).resolves.toBeUndefined()
  })

  it('boots through the real Loader with the shipped row shape', async () => {
    const { mkdtemp, rm, writeFile } = await import('node:fs/promises')
    const { tmpdir } = await import('node:os')
    const path = await import('node:path')
    const { pathToFileURL } = await import('node:url')
    const { Context } = await import('@cortex/cordis')
    const { default: Include } = await import('@cortex/cordis-plugin-include')
    const { default: SessionStore, SessionId } = await import('@cortex/session')
    const { default: SystemPrompt } = await import('@cortex/system-prompt')
    const { default: ToolRuntime } = await import('@cortex/tools')
    const { default: SessionProjectionRegistry } = await import('@cortex/session-projection')
    const root = await mkdtemp(path.join(tmpdir(), 'cortex-atlassian-loader-'))
    const configPath = path.join(root, 'cordis.yml')
    await writeFile(configPath, [
      "- name: '@cortex/session'",
      "- name: '@cortex/system-prompt'",
      "- name: '@cortex/tools'",
      "- name: '@cortex/session-projection'",
      "- name: '@cortex/atlassian'",
      '',
    ].join('\n'))
    const context = new Context()
    context.baseUrl = `${pathToFileURL(root).href}/`
    await context.plugin(Loader)
    context.loader.builtins.include = Include
    const modules = new Map<string, unknown>([
      ['@cortex/session', SessionStore],
      ['@cortex/system-prompt', SystemPrompt],
      ['@cortex/tools', ToolRuntime],
      ['@cortex/session-projection', SessionProjectionRegistry],
      ['@cortex/atlassian', AtlassianModule],
    ])
    context.loader.internal = {
      version: 'v2',
      import(specifier: string) {
        if (!modules.has(specifier)) throw new Error(`unexpected Loader import: ${specifier}`)
        return Promise.resolve(modules.get(specifier))
      },
    } as unknown as NonNullable<typeof context.loader.internal>
    try {
      await context.loader.create({ name: 'cordis:include', config: { path: pathToFileURL(configPath).href } })
      await context.loader.await()
      const unloaded = [...context.loader.entries()]
        .filter(entry => entry.fiber === undefined && !entry.disabled)
        .map(entry => entry.options.name)
      expect(unloaded).toEqual([])
      const session = context.sessions.create(SessionId('composed'))
      expect(context.sessionProjections.snapshot(session).values.atlassian).toMatchObject({ rev: 0, recent: [], activeReviewId: null })
      expect(context.tools.get('atlassian_review_finding')).toBeDefined()
      expect(context.atlassian.settings()).toEqual(DEFAULT_ATLASSIAN_SETTINGS)
    } finally {
      await context.fiber.dispose()
      await rm(root, { recursive: true, force: true })
    }
  })
})

describe('write gate', () => {
  it('asks (denied without an approval channel), allows, or denies Atlassian writes', async () => {
    live = await boot({ settings: { writes: 'ask' } })
    live.fakeTool(DELETE_ISSUE, 'deleted')
    live.fakeTool(GET_ISSUE, JSON.stringify({ key: 'PROJ-123' }))
    const asked = await live.run(DELETE_ISSUE, { issue_key: 'PROJ-123' }) as ToolExecutionResult
    expect(asked.isError).toBe(true)
    expect(JSON.stringify(asked.content)).toContain('Atlassian delete: delete issue on PROJ-123')
    const read = await live.run(GET_ISSUE, { issue_key: 'PROJ-123' }) as ToolExecutionResult
    expect(read.isError).toBe(false)
    await live.settings?.update(ATLASSIAN_SETTINGS_NAMESPACE, { writes: 'deny' })
    await vi.waitFor(() => { expect(live?.service.settings().writes).toBe('deny') })
    const denied = await live.run(DELETE_ISSUE, { issue_key: 'PROJ-123' }) as ToolExecutionResult
    expect(denied.isError).toBe(true)
    expect(JSON.stringify(denied.content)).toContain('disabled in settings')
    await live.settings?.update(ATLASSIAN_SETTINGS_NAMESPACE, { writes: 'allow' })
    await vi.waitFor(() => { expect(live?.service.settings().writes).toBe('allow') })
    const allowed = await live.run(DELETE_ISSUE, { issue_key: 'PROJ-123' }) as ToolExecutionResult
    expect(allowed.isError).toBe(false)
  })
})

describe('tool result observer', () => {
  it('records activity, search rows, and REST snapshots for reads and creations', async () => {
    live = await boot()
    live.fakeTool(GET_ISSUE, JSON.stringify({ key: 'PROJ-123', summary: 'x' }))
    live.fakeTool(SEARCH, JSON.stringify({ total: 1, issues: [{ key: 'PROJ-123', summary: 'x', status: { name: 'Done', category: 'Done' } }] }))
    live.fakeTool(CREATE_ISSUE, JSON.stringify({ message: 'created', issue: { key: 'PROJ-123' } }))
    live.fakeTool(GET_PAGE, JSON.stringify({ metadata: { id: '98765' } }))
    live.fakeTool(GET_PR, JSON.stringify(BITBUCKET_PR))
    await live.run(GET_ISSUE, { issue_key: 'PROJ-123' }, { agent: live.agent, callId: 'c-issue' })
    const activity = await live.waitEvents('atlassian/activity', 1)
    expect(activity[0]).toEqual({
      id: 'act-1', at: 1_700_000_000_000, kind: 'read', tool: GET_ISSUE, entity: { kind: 'issue', key: 'PROJ-123' },
      summary: 'Read PROJ-123', ok: true, callId: 'c-issue',
    })
    const snapshots = await live.waitEvents('atlassian/snapshot', 1) as AtlassianSnapshotEvent[]
    expect(snapshots[0]).toMatchObject({ focus: true, reason: 'tool', callId: 'c-issue', entity: { kind: 'issue', key: 'PROJ-123', summary: JIRA_ISSUE.fields.summary } })

    await live.run(SEARCH, { jql: 'project = PROJ' }, { agent: live.agent, callId: 'c-search' })
    const searches = await live.waitEvents('atlassian/search', 1)
    expect(searches[0]).toMatchObject({ service: 'jira', callId: 'c-search', query: 'project = PROJ', total: 1 })
    expect(await live.waitEvents('atlassian/activity', 2)).toHaveLength(2)

    await live.run(CREATE_ISSUE, { project_key: 'PROJ', summary: 'x' }, { agent: live.agent, callId: 'c-create' })
    await live.waitEvents('atlassian/snapshot', 2)
    await live.run(GET_PAGE, { page_id: '98765' }, { agent: live.agent, callId: 'c-page' })
    await live.run(GET_PAGE, { title: 'Auth service runbook', space_key: 'ENG' }, { agent: live.agent, callId: 'c-page2' })
    await live.run(GET_PR, { repository: 'webapp', prId: 42 }, { agent: live.agent, callId: 'c-pr' })
    const all = await live.waitEvents('atlassian/snapshot', 5) as AtlassianSnapshotEvent[]
    expect(all.map(event => event.entity.kind)).toEqual(['issue', 'issue', 'page', 'page', 'pr'])
    expect(all[4]?.entity).toMatchObject({ kind: 'pr', key: 'PROJ/webapp#42' })
    // The projection registry folds the log into the panel value.
    const projection = live.ctx.sessionProjections.snapshot(live.session).values.atlassian
    expect(projection?.recent).toEqual([{ kind: 'pr', key: 'PROJ/webapp#42' }, { kind: 'page', id: '98765' }, { kind: 'issue', key: 'PROJ-123' }])
    expect(projection?.searches).toHaveLength(1)
    expect(projection?.activity).toHaveLength(6)
  })

  it('records a failed call without fetching, and skips nested or agent-less executions', async () => {
    live = await boot()
    live.fakeTool(GET_ISSUE, new Error('server exploded'))
    await live.run(GET_ISSUE, { issue_key: 'PROJ-123' }, { agent: live.agent })
    const activity = await live.waitEvents('atlassian/activity', 1)
    expect(activity[0]).toMatchObject({ ok: false, summary: 'Read PROJ-123 — failed' })
    await new Promise(resolve => setTimeout(resolve, 20))
    expect(live.events('atlassian/snapshot')).toEqual([])
    expect(live.calls.filter(call => call.url.includes('/issue/'))).toHaveLength(0)
    // Agent-less and nested executions never reach the observer.
    live.fakeTool('mcp__atlassian__jira_get_transitions', '[]')
    await live.run('mcp__atlassian__jira_get_transitions', { issue_key: 'PROJ-123' })
    const nested = { name: GET_ISSUE, arguments: { issue_key: 'PROJ-123' }, agent: live.agent, parent: {}, callId: 'nested', signal } as unknown as ToolExecution
    live.ctx.emit('tools/result', nested, { isError: false, value: '', content: [] } as ToolExecutionResult)
    await new Promise(resolve => setTimeout(resolve, 20))
    expect(live.events('atlassian/activity')).toHaveLength(1)
  })

  it('warns when the REST fetch fails or the service is unconfigured, and survives a closed session', async () => {
    live = await boot()
    live.fakeTool(GET_ISSUE, JSON.stringify({ key: 'PROJ-404' }))
    await live.run(GET_ISSUE, { issue_key: 'PROJ-404' }, { agent: live.agent })
    await live.waitEvents('atlassian/activity', 1)
    await vi.waitFor(() => { expect(live?.warn).toHaveBeenCalledWith(expect.stringContaining('could not fetch')) })
    expect(live.events('atlassian/snapshot')).toEqual([])
    // Unconfigured Confluence: activity lands, no snapshot, no warning.
    await live.settings?.update(ATLASSIAN_SETTINGS_NAMESPACE, { confluenceUrl: '' })
    await vi.waitFor(() => { expect(live?.service.settings().confluenceUrl).toBe('') })
    live.warn.mockClear()
    live.fakeTool(GET_PAGE, '{}')
    await live.run(GET_PAGE, { page_id: '98765' }, { agent: live.agent, callId: 'c2' })
    await live.waitEvents('atlassian/activity', 2)
    await new Promise(resolve => setTimeout(resolve, 20))
    expect(live.events('atlassian/snapshot')).toEqual([])
    expect(live.warn).not.toHaveBeenCalled()
    // A REST rejection that is not an Error still lands in the warning.
    live.service.fetchImpl = () => rejectWith('plain rejection')
    live.fakeTool(GET_PR, '{}')
    await live.run(GET_PR, { repository: 'webapp', prId: 42 }, { agent: live.agent, callId: 'c3' })
    await vi.waitFor(() => { expect(live?.warn).toHaveBeenCalledWith(expect.stringContaining('plain rejection')) })
    // A session that refuses appends is contained.
    const closed = {
      id: live.session.id,
      session: {
        events: [],
        append: (type: string) => {
          if (type.startsWith('atlassian/')) throw new Error('closed')
          return { type, seq: 0, time: 0, data: {} }
        },
      },
      followup: vi.fn(),
    } as unknown as Agent
    live.fakeTool('mcp__atlassian__jira_get_worklog', '{}')
    await live.run('mcp__atlassian__jira_get_worklog', { issue_key: 'PROJ-123' }, { agent: closed })
    await vi.waitFor(() => { expect(live?.warn).toHaveBeenCalledWith(expect.stringContaining('session append failed: closed')) })
  })
})

describe('commands', () => {
  it('pins, shows, and clears the session ticket', async () => {
    live = await boot()
    const run = (line: string) => live!.ctx.commands.execute(live!.agent, line, signal)
    expect((await run('/ticket'))?.result).toEqual({ kind: 'success', text: 'No ticket is pinned. Usage: /ticket <PROJ-123|clear>' })
    expect((await run('/ticket nonsense'))?.result).toEqual({ kind: 'error', text: '"nonsense" is not a Jira issue key. Usage: /ticket <PROJ-123|clear>' })
    expect((await run('/ticket proj-123'))?.result).toEqual({ kind: 'success', text: 'Pinned PROJ-123. "This ticket" now refers to it.' })
    expect(live.events('atlassian/pin')).toEqual([{ key: 'PROJ-123' }])
    expect(live.events('atlassian/snapshot')).toHaveLength(1)
    expect((await run('/ticket'))?.result).toEqual({ kind: 'success', text: 'Pinned ticket: PROJ-123' })
    expect((await run('/ticket PROJ-404'))?.result).toEqual({
      kind: 'success', text: 'Pinned PROJ-404 (details unavailable: HTTP 404: Issue Does Not Exist).',
    })
    expect((await run('/ticket off'))?.result).toEqual({ kind: 'success', text: 'Ticket pin cleared.' })
    expect(live.events('atlassian/pin')).toEqual([{ key: 'PROJ-123' }, { key: 'PROJ-404' }, { key: null }])
    // The pin Remote rides the same path.
    await expect(live.service.pin(live.agent, { key: 'PROJ-123' })).resolves.toEqual({ ok: true })
    await expect(live.service.pin(live.agent, { key: null })).resolves.toEqual({ ok: true })
    await expect(live.service.pin(live.agent, { key: 'nope' })).resolves.toMatchObject({ ok: false, code: 'invalid-key' })
  })

  it('starts a review through /pr-review', async () => {
    live = await boot()
    const run = (line: string) => live!.ctx.commands.execute(live!.agent, line, signal)
    expect((await run('/pr-review'))?.result).toEqual({ kind: 'error', text: 'Usage: /pr-review <PROJECT/repo#id or pull request URL> [instructions]' })
    expect((await run('/pr-review garbage'))?.result.kind).toBe('error')
    expect((await run('/pr-review PROJ/webapp#42'))?.result).toEqual({
      kind: 'error', text: 'The Bitbucket MCP server is not connected; configure it in Settings → Atlassian first.',
    })
    // A connected Bitbucket mount (any registered bitbucket tool) admits the review.
    live.fakeTool(GET_PR, JSON.stringify(BITBUCKET_PR))
    expect((await run('/pr-review PROJ/webapp#42 focus on auth'))?.result).toEqual({
      kind: 'success', text: 'Reviewing PROJ/webapp#42. Findings stream into the Atlassian panel.',
    })
    expect(live.events('atlassian/review')).toEqual([{
      op: 'start', reviewId: 'review-1', pr: PR, at: 1_700_000_000_000,
      existing: [{ id: 501, author: { name: 'Mei Chen', id: 'mchen' }, text: 'Please validate the redirect target', file: 'src/auth/redirect.ts', line: 3, side: 'ADDED', replies: 0 }],
    }])
    expect(live.agent.followup).toHaveBeenCalledTimes(1)
    const message = live.agent.followup.mock.calls[0]?.[0] as { content: { text: string }[]; source: unknown }
    expect(message.source).toEqual({ kind: 'plugin', plugin: 'atlassian', form: 'instructions' })
    expect(message.content[0]?.text).toContain('Additional reviewer instructions from the user: focus on auth')
    expect(message.content[0]?.text).toContain('- #501 Mei Chen (src/auth/redirect.ts:3): Please validate the redirect target')
    await live.waitEvents('atlassian/snapshot', 1)
    // Existing comments that cannot be read only cost the awareness, and the reviewer is told there are none.
    live.service.fetchImpl = (url, init) => url.includes('/activities') ? Promise.reject(new Error('activities down')) : DEFAULT_FETCH(url, init)
    expect((await run('/pr-review PROJ/webapp#42'))?.result.kind).toBe('success')
    expect(live.warn).toHaveBeenCalledWith(expect.stringContaining('could not read existing comments of PROJ/webapp#42'))
    expect(live.warn).toHaveBeenCalledWith(expect.stringContaining('activities down'))
    expect((live.events('atlassian/review').slice(-1)[0] as { existing: unknown[] }).existing).toEqual([])
    expect((live.agent.followup.mock.calls[1]?.[0] as { content: { text: string }[] }).content[0]?.text).toContain('There are no review comments on this pull request yet.')
    // The default project fills a repo-only reference; a missing one is an error.
    await live.settings?.update(ATLASSIAN_SETTINGS_NAMESPACE, { bitbucketDefaultProject: '' })
    await vi.waitFor(() => { expect(live?.service.settings().bitbucketDefaultProject).toBe('') })
    expect((await run('/pr-review /repo#1'))?.result.kind).toBe('error')
    // A queue failure cancels the review it just started.
    live.agent.followup.mockImplementationOnce(() => { throw new Error('agent gone') })
    expect((await run('/pr-review PROJ/webapp#42'))?.result).toEqual({ kind: 'error', text: 'Could not queue the review turn: agent gone' })
    expect(live.events('atlassian/review').slice(-1)[0]).toMatchObject({ op: 'cancel', reviewId: 'review-3' })
    live.agent.followup.mockImplementationOnce(() => { throw 'string throw' })
    expect((await run('/pr-review PROJ/webapp#42'))?.result).toEqual({ kind: 'error', text: 'Could not queue the review turn: string throw' })
    // A closed session cannot start a review.
    const closed = {
      id: live.session.id,
      session: {
        events: [],
        append: (type: string) => {
          if (type.startsWith('atlassian/')) throw new Error('closed')
          return { type, seq: 0, time: 0, data: {} }
        },
      },
      followup: vi.fn(),
    } as unknown as Agent
    expect((await live.ctx.commands.execute(closed, '/pr-review PROJ/webapp#42', signal))?.result).toEqual({ kind: 'error', text: 'The session no longer accepts events.' })
  })
})

describe('system prompt', () => {
  it('contributes guidance only while a mount has tools, and the session context from the log', async () => {
    live = await boot()
    const assemble = () => live!.ctx.systemPrompt.assemble({ agent: live!.agent, scope: live!.agent })
    let assembly = await assemble()
    expect(assembly.sections.find(section => section.name === 'atlassian:guidance')?.text).toBe('')
    expect(assembly.contexts.find(context => context.name === 'atlassian:session')?.text).toBe('')
    live.fakeTool(GET_ISSUE, '{}')
    live.session.append('atlassian/pin', { key: 'PROJ-9' })
    live.session.append('atlassian/review', { op: 'start', reviewId: 'r1', pr: PR, at: 1, existing: [] })
    assembly = await assemble()
    expect(assembly.sections.find(section => section.name === 'atlassian:guidance')?.text).toContain('Atlassian tools:')
    const context = assembly.contexts.find(item => item.name === 'atlassian:session')?.text ?? ''
    expect(context).toContain('The active Jira ticket of this session is PROJ-9. "This ticket" refers to it.')
    expect(context).toContain('A pull request review of PROJ/webapp#42 is running')
    // A pinned ticket with a known record names its summary and status; a bare assemble has no session.
    await live.service.open(live.agent, { kind: 'issue', key: 'proj-123' })
    live.session.append('atlassian/pin', { key: 'PROJ-123' })
    assembly = await assemble()
    expect(assembly.contexts.find(item => item.name === 'atlassian:session')?.text).toContain('("Login page ignores SSO redirect target", status In Progress)')
    const bare = await live.ctx.systemPrompt.assemble()
    expect(bare.contexts.find(item => item.name === 'atlassian:session')?.text).toBe('')
    // A review whose record vanished (event without a start) contributes nothing.
    live.session.append('atlassian/review', { op: 'complete', reviewId: 'r1', summary: 's', verdict: 'approve', at: 2 })
    assembly = await assemble()
    expect(assembly.contexts.find(item => item.name === 'atlassian:session')?.text).not.toContain('is running')
  })
})

describe('Remote API', () => {
  it('probes each service', async () => {
    live = await boot()
    await expect(live.service.probe({ service: 'jira' })).resolves.toEqual({ service: 'jira', ok: true, user: 'Avery Quinn' })
    await expect(live.service.probe({ service: 'confluence' })).resolves.toEqual({ service: 'confluence', ok: true, user: 'Avery Quinn' })
    await expect(live.service.probe({ service: 'bitbucket' })).resolves.toEqual({ service: 'bitbucket', ok: true, user: '2 pull request(s) in your review inbox' })
    live.service.fetchImpl = () => Promise.resolve(new Response('{"message":"bad token"}', { status: 401 }))
    await expect(live.service.probe({ service: 'jira' })).resolves.toEqual({ service: 'jira', ok: false, error: 'HTTP 401: bad token — check the personal access token' })
    await live.ctx.fiber.dispose()
    live = await boot({ settings: { jiraUrl: '', confluenceUrl: '', bitbucketUrl: '' } })
    for (const service of ['jira', 'confluence', 'bitbucket'] as const) {
      await expect(live.service.probe({ service })).resolves.toEqual({ service, ok: false, error: 'URL or token is not configured' })
    }
  })

  it('opens entities into the session log', async () => {
    live = await boot()
    await expect(live.service.open(live.agent, { kind: 'issue', key: 'proj-123' })).resolves.toEqual({ ok: true, entity: { kind: 'issue', key: 'PROJ-123' } })
    await expect(live.service.open(live.agent, { kind: 'page', id: '98765' })).resolves.toEqual({ ok: true, entity: { kind: 'page', id: '98765' } })
    await expect(live.service.open(live.agent, { kind: 'pr', pr: PR })).resolves.toEqual({ ok: true, entity: { kind: 'pr', key: 'PROJ/webapp#42' } })
    expect((live.events('atlassian/snapshot') as AtlassianSnapshotEvent[]).map(event => [event.entity.kind, event.focus, event.reason]))
      .toEqual([['issue', true, 'open'], ['page', true, 'open'], ['pr', true, 'open']])
    await expect(live.service.open(live.agent, { kind: 'issue', key: 'PROJ-404' })).resolves.toEqual({ ok: false, code: 'not-found', message: 'HTTP 404: Issue Does Not Exist' })
    live.service.fetchImpl = () => Promise.reject(new Error('down'))
    await expect(live.service.open(live.agent, { kind: 'issue', key: 'PROJ-123' })).resolves.toEqual({ ok: false, code: 'network', message: containing('down') })
    live.service.fetchImpl = () => { throw 'sync failure' }
    await expect(live.service.open(live.agent, { kind: 'issue', key: 'PROJ-123' })).resolves.toEqual({ ok: false, code: 'network', message: containing('sync failure') })
    await live.ctx.fiber.dispose()
    live = await boot({ settings: { jiraUrl: '', confluenceUrl: '', bitbucketUrl: '' } })
    await expect(live.service.open(live.agent, { kind: 'issue', key: 'PROJ-1' })).resolves.toEqual({ ok: false, code: 'not-configured', message: 'Jira URL or token is not configured' })
    await expect(live.service.open(live.agent, { kind: 'page', id: '1' })).resolves.toEqual({ ok: false, code: 'not-configured', message: 'Confluence URL or token is not configured' })
    await expect(live.service.open(live.agent, { kind: 'pr', pr: PR })).resolves.toEqual({ ok: false, code: 'not-configured', message: 'Bitbucket URL or token is not configured' })
    await live.ctx.fiber.dispose()
    // A Bitbucket target with no project anywhere cannot address the pull request.
    live = await boot({ settings: { bitbucketDefaultProject: '' } })
    await expect(live.service.open(live.agent, { kind: 'pr', pr: { ...PR, project: '' } })).resolves.toMatchObject({ ok: false, code: 'not-configured' })
    // Credentials absent as a seam: every target is unconfigured.
    await live.ctx.fiber.dispose()
    live = await boot({ credentials: false })
    await expect(live.service.open(live.agent, { kind: 'issue', key: 'PROJ-1' })).resolves.toMatchObject({ ok: false, code: 'not-configured' })
    // A page reference without id or title cannot be fetched.
    await live.ctx.fiber.dispose()
    live = await boot()
    live.fakeTool(GET_PAGE, '{}')
    await live.run(GET_PAGE, { title: 'only-title' }, { agent: live.agent })
    await live.waitEvents('atlassian/activity', 1)
    await new Promise(resolve => setTimeout(resolve, 20))
    expect(live.events('atlassian/snapshot')).toEqual([])
  })

  it('lists pull requests', async () => {
    live = await boot()
    await expect(live.service.listPullRequests({ scope: 'inbox' })).resolves.toMatchObject({ ok: true, items: [{ key: 'PROJ/webapp#42', role: 'REVIEWER' }] })
    await expect(live.service.listPullRequests({ scope: 'repo', repo: 'webapp' })).resolves.toMatchObject({ ok: true, items: [{ key: 'PROJ/webapp#42' }] })
    await expect(live.service.listPullRequests({ scope: 'repo', project: 'PROJ', repo: ' ' })).resolves.toMatchObject({ ok: false, code: 'invalid-request' })
    await expect(live.service.listPullRequests({ scope: 'repo', repo: 'other', state: 'MERGED' })).resolves.toMatchObject({ ok: false, code: 'not-found' })
    await live.ctx.fiber.dispose()
    live = await boot({ settings: { bitbucketUrl: '' } })
    await expect(live.service.listPullRequests({ scope: 'inbox' })).resolves.toMatchObject({ ok: false, code: 'not-configured' })
  })

  it('posts, dismisses, and cancels review findings', async () => {
    live = await boot()
    await expect(live.service.postFinding(live.agent, { reviewId: 'r1', findingId: 'f1' })).resolves.toMatchObject({ ok: false, code: 'not-found' })
    await startReview(live)
    const inline = await live.service.postFinding(live.agent, { reviewId: 'r1', findingId: 'f1' })
    expect(inline).toEqual({ ok: true, commentId: 777, url: `${REST.bitbucket}/projects/PROJ/repos/webapp/pull-requests/42/overview?commentId=777`, mode: 'inline' })
    const posted = JSON.parse(sentBody(live.calls.find(call => call.init.method === 'POST'))) as { text: string; anchor: unknown }
    expect(posted).toEqual({ text: 'Validate the target.', anchor: { path: 'src/auth/redirect.ts', line: 3, lineType: 'ADDED', fileType: 'TO', diffType: 'EFFECTIVE' } })
    await expect(live.service.postFinding(live.agent, { reviewId: 'r1', findingId: 'f1' })).resolves.toMatchObject({ ok: false, code: 'already-posted' })
    // A line the diff no longer holds posts as a general comment with an override body; the diff is served from cache.
    const diffCalls = () => live!.calls.filter(call => call.url.includes('/diff')).length
    expect(diffCalls()).toBe(1)
    const general = await live.service.postFinding(live.agent, { reviewId: 'r1', findingId: 'f2', comment: ' edited body ' })
    expect(general).toMatchObject({ ok: true, mode: 'general' })
    expect(diffCalls()).toBe(1)
    const generalBody = JSON.parse(sentBody(live.calls.filter(call => call.init.method === 'POST')[1])) as { text: string; anchor?: unknown }
    expect(generalBody).toEqual({ text: '**nowhere.ts:900** — edited body' })
    expect(live.events('atlassian/review').filter(event => (event as { op: string }).op === 'posted')).toHaveLength(2)
    // Dismiss and cancel.
    live.session.append('atlassian/review', { op: 'finding', reviewId: 'r1', finding: finding('f3') })
    expect(live.service.dismissFinding(live.agent, { reviewId: 'r1', findingId: 'f3' })).toEqual({ ok: true })
    expect(live.service.dismissFinding(live.agent, { reviewId: 'r1', findingId: 'nope' })).toMatchObject({ ok: false, code: 'not-found' })
    expect(live.service.cancelReview(live.agent, { reviewId: 'nope' })).toMatchObject({ ok: false, code: 'not-found' })
    expect(live.service.cancelReview(live.agent, { reviewId: 'r1' })).toEqual({ ok: true })
    expect(live.service.cancelReview(live.agent, { reviewId: 'r1' })).toEqual({ ok: true })
    expect(live.events('atlassian/review').filter(event => (event as { op: string }).op === 'cancel')).toHaveLength(1)
    // REST failure while posting.
    await startReview(live, 'r2')
    live.service.fetchImpl = () => Promise.resolve(new Response('{"message":"nope"}', { status: 500 }))
    await expect(live.service.postFinding(live.agent, { reviewId: 'r2', findingId: 'f1' })).resolves.toMatchObject({ ok: false, code: 'http' })
    // The diff cache expires.
    live.service.fetchImpl = live.calls.length > 0 ? (await import('./fixtures.ts')).fakeFetch(DEFAULT_ROUTES).fetchImpl : live.service.fetchImpl
    const later = 1_700_000_000_000 + 6 * 60_000
    live.service.clock = { now: () => later, id: prefix => `${prefix}-late` }
    await expect(live.service.postFinding(live.agent, { reviewId: 'r2', findingId: 'f2' })).resolves.toMatchObject({ ok: true, mode: 'general' })
    // Unconfigured Bitbucket.
    await live.ctx.fiber.dispose()
    live = await boot({ settings: { bitbucketUrl: '' } })
    await startReview(live)
    await expect(live.service.postFinding(live.agent, { reviewId: 'r1', findingId: 'f1' })).resolves.toMatchObject({ ok: false, code: 'not-configured' })
  })

  it('serves diff context with a clamped window', async () => {
    live = await boot()
    const found = await live.service.diffContext({ pr: PR, file: 'src/auth/redirect.ts', line: 2, side: 'ADDED', context: 1 })
    expect(found).toMatchObject({ ok: true, found: true, file: 'src/auth/redirect.ts' })
    const lines = (found as { lines: { anchor?: boolean }[] }).lines
    expect(lines).toHaveLength(3)
    expect(lines.map(item => item.anchor === true)).toEqual([false, true, false])
    const wide = await live.service.diffContext({ pr: PR, file: 'src/auth/redirect.ts', line: 3, side: 'ADDED', context: 999 })
    expect((wide as { lines: unknown[] }).lines.length).toBeGreaterThan(3)
    const defaulted = await live.service.diffContext({ pr: PR, file: 'src/auth/redirect.ts', line: 3, side: 'ADDED' })
    expect(defaulted).toMatchObject({ ok: true })
    live.service.fetchImpl = () => Promise.reject(new Error('down'))
    live.service.clock = { now: () => 9_999_999_999_999, id: () => 'x' }
    await expect(live.service.diffContext({ pr: PR, file: 'x', line: 1, side: 'ADDED' })).resolves.toMatchObject({ ok: false, code: 'network' })
    await live.ctx.fiber.dispose()
    live = await boot({ settings: { bitbucketUrl: '' } })
    await expect(live.service.diffContext({ pr: PR, file: 'x', line: 1, side: 'ADDED' })).resolves.toMatchObject({ ok: false, code: 'not-configured' })
    expect(CONNECTED_SETTINGS.writes).toBe('allow')
    expect(TOKENS.ATLASSIAN_JIRA_TOKEN).toBe('jt')
  })
})

describe('defaults and defensive arms', () => {
  it('ships a wall clock, a random id source, and a real fetch by default', async () => {
    const { Context } = await import('@cortex/cordis')
    const { default: ToolRuntime } = await import('@cortex/tools')
    const { default: SessionStore } = await import('@cortex/session')
    const { default: SystemPrompt } = await import('@cortex/system-prompt')
    const ctx = new Context()
    await ctx.plugin(SessionStore)
    await ctx.plugin(SystemPrompt)
    await ctx.plugin(ToolRuntime)
    await ctx.plugin(AtlassianService).await()
    try {
      const service = ctx.atlassian
      expect(typeof service.clock.now()).toBe('number')
      expect(service.clock.id('x')).toMatch(/^x-/)
      // The default fetch is the platform fetch: a closed local port refuses the connection.
      await expect(service.fetchImpl('http://127.0.0.1:9/', { method: 'GET' })).rejects.toThrow()
    } finally {
      await ctx.fiber.dispose()
    }
  })

  it('maps non-REST failures (Error and not) to internal failures', async () => {
    live = await boot()
    const broken = (reason: unknown) =>
      Promise.resolve({ ok: true, status: 200, text: () => rejectWith(reason) } as unknown as Response)
    live.service.fetchImpl = () => broken(new Error('text exploded'))
    await expect(live.service.open(live.agent, { kind: 'issue', key: 'PROJ-123' }))
      .resolves.toEqual({ ok: false, code: 'internal', message: 'text exploded' })
    live.service.fetchImpl = () => broken('raw text failure')
    await expect(live.service.open(live.agent, { kind: 'issue', key: 'PROJ-123' }))
      .resolves.toEqual({ ok: false, code: 'internal', message: 'raw text failure' })
  })

  it('returns before mounting when disposal lands while the plans are being computed', async () => {
    live = await boot()
    const credentials = live.credentials
    if (credentials === undefined) throw new Error('bench composes credentials')
    let release!: () => void
    const gate = new Promise<void>((resolve) => { release = resolve })
    const original = credentials.resolve.bind(credentials)
    vi.spyOn(credentials, 'resolve').mockImplementation(async (ref) => {
      await gate
      return original(ref)
    })
    const pending = live.service.reconcile()
    await live.ctx.fiber.dispose()
    release()
    await expect(pending).resolves.toBeUndefined()
  })

  it('ignores a direct observation without an agent and a page reference without an address', async () => {
    live = await boot()
    const service = live.service as unknown as {
      observe(exec: ToolExecution, result: ToolExecutionResult): Promise<void>
      fetchEntity(ref: { kind: string }): Promise<unknown>
    }
    await service.observe(
      { name: GET_ISSUE, arguments: {}, callId: 'x', signal } as unknown as ToolExecution,
      { isError: false, content: [] } as unknown as ToolExecutionResult,
    )
    expect(live.events('atlassian/activity')).toEqual([])
    await expect(service.fetchEntity({ kind: 'page' })).resolves.toBeUndefined()
  })

  it('warns with the raw reason when a snapshot fetch or an append rejects with a non-Error', async () => {
    live = await boot()
    const broken = (reason: unknown) =>
      Promise.resolve({ ok: true, status: 200, text: () => rejectWith(reason) } as unknown as Response)
    live.service.fetchImpl = (url, init) => url.includes('/issue/') ? broken('issue raw string') : DEFAULT_FETCH(url, init)
    live.fakeTool(GET_ISSUE, JSON.stringify({ key: 'PROJ-123' }))
    await live.run(GET_ISSUE, { issue_key: 'PROJ-123' }, { agent: live.agent })
    await vi.waitFor(() => { expect(live?.warn).toHaveBeenCalledWith(expect.stringContaining('issue raw string')) })
    const throwing = {
      id: live.session.id,
      session: { events: [], append: () => { throw 'append raw string' } },
      followup: vi.fn(),
    } as unknown as Agent
    await live.service.pin(throwing, { key: null })
    expect(live.warn).toHaveBeenCalledWith(expect.stringContaining('session append failed: append raw string'))
  })
})

describe('review tools through the service', () => {
  it('tracks the running review: refusals, overlaps, duplicates, and completion land in the log', async () => {
    live = await boot()
    const FINDING_ARGS = {
      file: 'src/auth/redirect.ts', line: 3, side: 'ADDED', severity: 'critical', category: 'correctness',
      title: 'Open redirect', comment: 'Validate the target.', evidence: 'return x', rationale: 'because',
    }
    // No running review: both tools refuse through the service's tracker.
    const refused = await live.run('atlassian_review_finding', FINDING_ARGS, { agent: live.agent }) as { value: { recorded: boolean; message: string } }
    expect(refused.value).toMatchObject({ recorded: false })
    expect(refused.value.message).toContain('No pull request review is running')
    live.session.append('atlassian/review', {
      op: 'start', reviewId: 'r1', pr: PR, at: 1,
      existing: [{ id: 501, author: { name: 'Mei Chen' }, text: 'Covers this line already', file: 'src/auth/redirect.ts', line: 3, side: 'ADDED', replies: 0 }],
    })
    // Near the existing comment: refused until acknowledged, then recorded with the overlap ids.
    const overlap = await live.run('atlassian_review_finding', FINDING_ARGS, { agent: live.agent }) as { value: { recorded: boolean; message: string } }
    expect(overlap.value.recorded).toBe(false)
    expect(overlap.value.message).toContain('#501 by Mei Chen')
    const recorded = await live.run('atlassian_review_finding', { ...FINDING_ARGS, acknowledgeExisting: true }, { agent: live.agent }) as { value: { recorded: boolean } }
    expect(recorded.value).toMatchObject({ recorded: true, findingId: 'finding-1', count: 1 })
    const findings = live.events('atlassian/review').filter(event => (event as { op: string }).op === 'finding')
    expect(findings[0]).toMatchObject({ finding: { id: 'finding-1', overlaps: [501], at: 1_700_000_000_000 } })
    // The same file, line, and category again is a duplicate of the recorded finding.
    const duplicate = await live.run('atlassian_review_finding', { ...FINDING_ARGS, acknowledgeExisting: true }, { agent: live.agent }) as { value: { recorded: boolean; message: string } }
    expect(duplicate.value.recorded).toBe(false)
    expect(duplicate.value.message).toContain('Already recorded as finding finding-1')
    const complete = await live.run('atlassian_review_complete', { summary: 'Done.', verdict: 'request-changes' }, { agent: live.agent }) as { value: { completed: boolean } }
    expect(complete.value).toMatchObject({ completed: true, findings: 1 })
    expect(live.events('atlassian/review').slice(-1)[0]).toEqual({
      op: 'complete', reviewId: 'r1', summary: 'Done.', verdict: 'request-changes', at: 1_700_000_000_000,
    })
  })
})

describe('review start edges', () => {
  it('fills the project from the default, rejects a project-less reference, and skips comments without REST', async () => {
    live = await boot()
    live.fakeTool(GET_PR, JSON.stringify(BITBUCKET_PR))
    const begin = (live.service as unknown as {
      beginReview(agent: Agent, pr: { project: string; repo: string; id: number }, focus: string | undefined):
      Promise<{ ok: boolean; code?: string }>
    }).beginReview.bind(live.service)
    // The default project fills an empty reference.
    await expect(begin(live.agent, { project: '', repo: 'webapp', id: 42 }, undefined)).resolves.toMatchObject({ ok: true })
    expect(live.events('atlassian/review').slice(-1)[0]).toMatchObject({ op: 'start', pr: PR })
    // No project anywhere is a hard error.
    await live.settings?.update(ATLASSIAN_SETTINGS_NAMESPACE, { bitbucketDefaultProject: '' })
    await vi.waitFor(() => { expect(live?.service.settings().bitbucketDefaultProject).toBe('') })
    await expect(begin(live.agent, { project: '', repo: 'webapp', id: 42 }, undefined)).resolves.toMatchObject({ ok: false, code: 'invalid-pr' })
    // With the Bitbucket REST target unconfigured the review still starts, just without existing comments.
    await live.ctx.fiber.dispose()
    live = await boot({ settings: { bitbucketUrl: '' } })
    live.fakeTool(GET_PR, JSON.stringify(BITBUCKET_PR))
    const command = await live.ctx.commands.execute(live.agent, '/pr-review PROJ/webapp#42', signal)
    expect(command?.result.kind).toBe('success')
    expect((live.events('atlassian/review')[0] as { existing: unknown[] }).existing).toEqual([])
    // A comment read that rejects with a non-Error still lands in the warning verbatim.
    await live.ctx.fiber.dispose()
    live = await boot()
    live.fakeTool(GET_PR, JSON.stringify(BITBUCKET_PR))
    live.service.fetchImpl = (url, init) => url.includes('/activities')
      ? Promise.resolve({ ok: true, status: 200, text: () => rejectWith('activities raw string') } as unknown as Response)
      : DEFAULT_FETCH(url, init)
    expect((await live.ctx.commands.execute(live.agent, '/pr-review PROJ/webapp#42', signal))?.result.kind).toBe('success')
    expect(live.warn).toHaveBeenCalledWith(expect.stringContaining('activities raw string'))
  })

  it('requires a repository for a repo-scoped listing', async () => {
    live = await boot()
    await expect(live.service.listPullRequests({ scope: 'repo', project: 'PROJ' })).resolves.toMatchObject({ ok: false, code: 'invalid-request' })
  })
})
