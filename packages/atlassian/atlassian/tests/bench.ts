/**
 * Service bench for `@cortex/atlassian`: a real Context with the session
 * store, tool runtime, system prompt, projection registry, command registry,
 * in-memory settings + credentials providers, and the service itself wired to
 * a routing fake fetch and a deterministic clock.
 */
import { vi, type MockInstance } from 'vitest'
import { Context } from '@cortex/cordis'
import type { Agent } from '@cortex/agent'
import { CredentialProvider, type CredentialInfo, type CredentialRef, type ResolvedCredential } from '@cortex/credentials'
import CommandRuntime from '@cortex/commands'
import { CallId } from '@cortex/llm'
import SessionStore, { SessionId } from '@cortex/session'
import type { Session } from '@cortex/session'
import SessionProjectionRegistry from '@cortex/session-projection'
import { SettingsProvider, type SettingsNamespace } from '@cortex/settings'
import SystemPrompt from '@cortex/system-prompt'
import ToolRuntime, { defineTool } from '@cortex/tools'
import AtlassianService from '../src/index.ts'
import type { AtlassianSettings } from '../src/types.ts'
import { BITBUCKET_DIFF, BITBUCKET_PR, CONFLUENCE_PAGE, JIRA_ISSUE, fakeFetch, type Routes } from './fixtures.ts'

/** In-memory settings provider. */
class MemorySettings extends SettingsProvider {
  doc: Record<string, unknown>

  constructor(ctx: Context, options?: { doc?: Record<string, unknown> }) {
    super(ctx)
    this.doc = structuredClone(options?.doc ?? {})
  }

  get writable(): boolean {
    return true
  }

  protected load(): Promise<Record<string, unknown>> {
    return Promise.resolve(structuredClone(this.doc))
  }

  protected persist(ns: SettingsNamespace, section: Record<string, unknown>): Promise<void> {
    this.doc[ns] = structuredClone(section)
    return Promise.resolve()
  }
}

/** In-memory credentials provider. */
class MemoryCredentials extends CredentialProvider {
  private readonly store = new Map<string, string>()

  constructor(ctx: Context, seed: Record<string, string> = {}) {
    super(ctx)
    for (const [key, value] of Object.entries(seed)) this.store.set(key, value)
  }

  override resolve(ref: CredentialRef): Promise<ResolvedCredential | undefined> {
    const value = this.store.get(ref)
    return Promise.resolve(value === undefined ? undefined : { value, source: 'memory' })
  }

  override describe(ref: CredentialRef): Promise<CredentialInfo> {
    return Promise.resolve({ configured: this.store.has(ref), writable: true })
  }

  override set(ref: CredentialRef, value: string): Promise<void> {
    this.store.set(ref, value)
    this.ctx.emit('credentials/updated', ref)
    return Promise.resolve()
  }

  override unset(ref: CredentialRef): Promise<void> {
    this.store.delete(ref)
    this.ctx.emit('credentials/updated', ref)
    return Promise.resolve()
  }
}

/** Fake REST base URLs (one origin, three path prefixes). */
export const REST = { jira: 'http://rest.test/jira', confluence: 'http://rest.test/confluence', bitbucket: 'http://rest.test/bitbucket' }

/** Settings that make every REST target resolvable while no MCP child can ever spawn. */
export const CONNECTED_SETTINGS: Partial<AtlassianSettings> = {
  jiraUrl: REST.jira,
  confluenceUrl: REST.confluence,
  bitbucketUrl: REST.bitbucket,
  bitbucketDefaultProject: 'PROJ',
  atlassianLaunch: 'definitely-missing-binary-xyz mcp-atlassian',
  bitbucketLaunch: 'definitely-missing-binary-xyz bitbucket',
  writes: 'allow',
}

/** Tokens matching the default credential references. */
export const TOKENS = { ATLASSIAN_JIRA_TOKEN: 'jt', ATLASSIAN_CONFLUENCE_TOKEN: 'ct', ATLASSIAN_BITBUCKET_TOKEN: 'bt' }

const PR_PATH = '/bitbucket/rest/api/1.0/projects/PROJ/repos/webapp/pull-requests'

/** Default fake REST routes covering the fixtures. */
export const DEFAULT_ROUTES: Routes = {
  'GET /jira/rest/api/2/myself': { body: { displayName: 'Kendrito' } },
  'GET /jira/rest/api/2/issue/PROJ-123*': { body: JIRA_ISSUE },
  'GET /jira/rest/api/2/issue/PROJ-404*': { status: 404, body: { errorMessages: ['Issue Does Not Exist'] } },
  'GET /confluence/rest/api/user/current': { body: { displayName: 'Kendrito' } },
  'GET /confluence/rest/api/content/98765*': { body: CONFLUENCE_PAGE },
  'GET /confluence/rest/api/content?spaceKey=ENG&title=Auth+service+runbook*': { body: { results: [CONFLUENCE_PAGE] } },
  'GET /bitbucket/rest/api/1.0/inbox/pull-requests/count': { body: { count: 2 } },
  [`GET ${PR_PATH}/42`]: { body: BITBUCKET_PR },
  [`GET ${PR_PATH}?state=OPEN&limit=50&order=NEWEST`]: { body: { values: [BITBUCKET_PR] } },
  [`GET ${PR_PATH}/42/diff*`]: { body: BITBUCKET_DIFF },
  [`POST ${PR_PATH}/42/comments`]: { status: 201, body: { id: 777 } },
  [`GET ${PR_PATH}/42/activities?limit=50&start=0`]: {
    body: {
      isLastPage: true,
      values: [
        { action: 'COMMENTED', commentAction: 'ADDED', commentAnchor: { path: 'src/auth/redirect.ts', line: 3, lineType: 'ADDED' },
          comment: { id: 501, text: 'Please validate the redirect target', author: { displayName: 'Mei Chen', slug: 'mchen' }, comments: [] } },
        { action: 'APPROVED' },
      ],
    },
  },
  'GET /bitbucket/rest/api/1.0/dashboard/pull-requests?state=OPEN&role=REVIEWER&limit=50&order=NEWEST': { body: { values: [BITBUCKET_PR] } },
  'GET /bitbucket/rest/api/1.0/dashboard/pull-requests?state=OPEN&role=AUTHOR&limit=50&order=NEWEST': { body: { values: [] } },
}

/** A fake fetch over {@link DEFAULT_ROUTES} for specs that swap the service's fetch mid-test. */
export const DEFAULT_FETCH = fakeFetch(DEFAULT_ROUTES).fetchImpl

/** Options of {@link boot}. */
export interface BootOptions {
  settings?: Partial<AtlassianSettings> | false
  credentials?: Record<string, string> | false
  routes?: Routes
  commands?: boolean
  projections?: boolean
}

/** Everything a service spec touches. */
export interface Bench {
  ctx: Context
  service: AtlassianService
  session: Session
  agent: Agent & { followup: ReturnType<typeof vi.fn> }
  calls: { url: string; init: RequestInit }[]
  settings: MemorySettings | undefined
  credentials: MemoryCredentials | undefined
  warn: MockInstance<(message: string) => void>
  /** Register a fake MCP-named tool that returns the given text (or throws). */
  fakeTool: (name: string, result: string | Error) => () => void
  /** Execute a tool through the registry as the agent (or without one). */
  run: (name: string, args: unknown, options?: { agent?: Agent | undefined; callId?: string }) => Promise<unknown>
  /** Session events of one type. */
  events: (type: string) => unknown[]
  /** Wait until the observer appended the expected number of events of one type. */
  waitEvents: (type: string, count: number) => Promise<unknown[]>
}

/**
 * Boot the bench.
 * @param options - which optional seams to compose and how.
 * @returns the bench.
 */
export async function boot(options: BootOptions = {}): Promise<Bench> {
  const ctx = new Context()
  await ctx.plugin(SessionStore)
  await ctx.plugin(SystemPrompt)
  await ctx.plugin(ToolRuntime)
  if (options.projections !== false) await ctx.plugin(SessionProjectionRegistry)
  if (options.commands !== false) await ctx.plugin(CommandRuntime)
  let settings: MemorySettings | undefined
  if (options.settings !== false) {
    await ctx.plugin(MemorySettings, { doc: { atlassian: { ...CONNECTED_SETTINGS, ...options.settings } } })
    settings = ctx.get('settings') as MemorySettings
  }
  let credentials: MemoryCredentials | undefined
  if (options.credentials !== false) {
    await ctx.plugin(MemoryCredentials, options.credentials ?? TOKENS)
    credentials = ctx.get('credentials') as MemoryCredentials
  }
  const warn = vi.spyOn(ctx.logger, 'warn').mockImplementation(() => undefined) as unknown as MockInstance<(message: string) => void>
  vi.spyOn(ctx.logger, 'error').mockImplementation(() => undefined)
  const { fetchImpl, calls } = fakeFetch(options.routes ?? DEFAULT_ROUTES)
  await ctx.plugin(AtlassianService)
  const service = ctx.atlassian
  service.fetchImpl = fetchImpl
  let ids = 0
  service.clock = { now: () => 1_700_000_000_000, id: prefix => `${prefix}-${String(++ids)}` }
  await service.reconcile()
  const session = ctx.sessions.create(SessionId('bench'))
  const agent = { id: session.id, session, followup: vi.fn() } as unknown as Bench['agent']
  const signal = new AbortController().signal
  const events = (type: string) => session.events.filter(event => event.type === type).map(event => event.data)
  return {
    ctx, service, session, agent, calls, settings, credentials, warn,
    fakeTool: (name, result) => ctx.tools.register(defineTool({
      name,
      description: name,
      parameters: {},
      output: { schema: { type: 'string' }, render: (_args, value) => [{ type: 'text', text: value }] },
      execute: () => result instanceof Error ? Promise.reject(result) : Promise.resolve(result),
    })),
    run: (name, args, runOptions = {}) => ctx.tools.execute({
      callId: CallId(runOptions.callId ?? 'c1'),
      name,
      arguments: args,
      signal,
      ...runOptions.agent === undefined ? {} : { agent: runOptions.agent },
    }),
    events,
    waitEvents: async (type, count) => {
      await vi.waitFor(() => { if (events(type).length < count) throw new Error(`waiting for ${String(count)} ${type}`) })
      return events(type)
    },
  }
}
