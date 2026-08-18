/**
 * `@cortex/atlassian` — the Jira / Confluence / Bitbucket integration of the
 * Cortex harness for Atlassian Data Center.
 *
 * One service (`ctx.atlassian`) owns the whole seam:
 *
 * - mounts the two MCP servers (`sooperset/mcp-atlassian` as `mcp__atlassian__*`,
 *   `n11techhub/mcp-bitbucket` as `mcp__bitbucket__*`) as child plugins from
 *   the `atlassian` settings namespace + `ctx.credentials`, remounting on change;
 * - gates their write tools through `tools/pre-execute` (`writes: ask|allow|deny`);
 * - observes every Atlassian tool result, records activity/search rows, and
 *   re-fetches each touched Jira issue / Confluence page / Bitbucket pull
 *   request over REST into `atlassian/snapshot` log events, which the
 *   `atlassian` session projection folds for the browser panel;
 * - registers `/ticket` (pin the session's ticket) and `/pr-review`
 *   (start a review run) commands plus the two review tools the agent uses to
 *   stream findings; the pinned ticket and running review ride the system prompt;
 * - serves the panel's Remote API: status, probes, open/pin, PR listing,
 *   inline comment posting with diff-resolved anchors, and diff context.
 *
 * Service plugin (default-exported class). Config carries no deployment
 * tunables: every runtime choice is a settings field.
 *
 * @module @cortex/atlassian
 */

import type { Context } from '@cortex/cordis'
import z from '@cortex/schemastery'
import type { Agent } from '@cortex/agent'
import type { CredentialRef } from '@cortex/credentials'
import { credentialRef } from '@cortex/credentials'
import { createUserMessage } from '@cortex/llm'
import type { ContentBlock } from '@cortex/llm'
import type { Session } from '@cortex/session'
import type { SettingsScope } from '@cortex/settings'
import type { ToolExecution, ToolExecutionResult } from '@cortex/tools'
import { Remote, TypertRemoteService } from '@cortex/typert-protocol'
// Type-only: pulls the optional seams' Context merges (ctx.settings, ctx.credentials, ctx.commands, ...).
import type {} from '@cortex/commands'
import type {} from '@cortex/session-projection'
import type {} from '@cortex/system-prompt'
// Typert-generated ./typert and ./remote artifacts import Zod at runtime.
import type {} from 'zod'
import { gateDecision } from './gate.ts'
import { McpMounts, planMounts, type MountPlan, type ResolvedTokens, type ResolvedUrls } from './mounts.ts'
import { applyAtlassianEvent, atlassianProjectionDefinition, emptyState, type AtlassianUnitState } from './projection.ts'
import { BitbucketRest, diffWindow, parsePrRef, prKey, resolveAnchor, type NormalizedDiff } from './rest/bitbucket.ts'
import { ConfluenceRest } from './rest/confluence.ts'
import { RestError, type FetchLike, type RestTarget } from './rest/http.ts'
import { JiraRest } from './rest/jira.ts'
import { registerReviewTools, reviewInstructions } from './review.ts'
import {
  ATLASSIAN_SETTINGS_NAMESPACE, AtlassianSettingsSchema, DEFAULT_ATLASSIAN_SETTINGS, normalizeBaseUrl,
} from './settings.ts'
import {
  ATLASSIAN_SERVER, BITBUCKET_SERVER, REVIEW_COMPLETE_TOOL, REVIEW_FINDING_TOOL,
  activitySummary, classifyTool, isAtlassianTool, rawToolName, searchRecord, touchedEntities, type TouchRef,
} from './tools.ts'
import type {
  AckResult, AtlassianSettings, AtlassianStatus, CancelReviewRequest, DiffContextRequest, DiffContextResult,
  DismissFindingRequest, EntityRecord, EntityRef, ExistingPrComment, ListPullRequestsRequest, ListPullRequestsResult, OpenRequest,
  OpenResult, PinRequest, PostFindingRequest, PostFindingResult, PrRef, ProbeRequest, ProbeResult, ReviewFinding,
  ReviewVerdict,
} from './types.ts'

export type * from './types.ts'
export {
  ATLASSIAN_SETTINGS_NAMESPACE, AtlassianSettingsSchema, DEFAULT_ATLASSIAN_SETTINGS, DEFAULT_ATLASSIAN_LAUNCH,
  DEFAULT_BITBUCKET_LAUNCH, DEFAULT_TOKEN_REFS,
} from './settings.ts'
export { ATLASSIAN_SERVER, BITBUCKET_SERVER, REVIEW_COMPLETE_TOOL, REVIEW_FINDING_TOOL } from './tools.ts'
export { atlassianProjectionDefinition } from './projection.ts'
export { registerReviewTools, reviewInstructions } from './review.ts'
export type { ReviewTracker } from './review.ts'

declare module '@cortex/cordis' {
  interface Context {
    /** The Atlassian integration service. */
    atlassian: AtlassianService
  }
}

/** Plugin configuration: intentionally empty — every runtime choice lives in settings. */
export interface Config {}

/** Bound on entities re-fetched per tool result. */
const TOUCH_LIMIT = 3
/** Diff cache lifetime in ms. */
const DIFF_CACHE_MS = 5 * 60_000
/** Static guidance appended to the system prompt while a server is mounted. */
const GUIDANCE = 'Atlassian tools: `mcp__atlassian__jira_*` and `mcp__atlassian__confluence_*` reach Jira and Confluence; '
  + '`mcp__bitbucket__bitbucket_*` reaches Bitbucket Server (project key + repository slug + numeric prId). '
  + 'Prefer `jira_get_issue` before changing an issue, `jira_get_transitions` before `jira_transition_issue`, '
  + 'and quote issue keys exactly (PROJ-123). Every read and write is mirrored into the user\'s Atlassian panel automatically.'

/** Wall clock + id source (overridable in tests through the class field). */
interface Clock {
  now(): number
  id(prefix: string): string
}

const defaultClock: Clock = {
  now: () => Date.now(),
  id: prefix => `${prefix}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`,
}

/** Fold the whole `atlassian` state from a session log (authoritative for review reads). */
function foldSession(session: Session): AtlassianUnitState {
  let state = emptyState()
  for (const event of session.events) state = applyAtlassianEvent(state, event)
  return state
}

function textOf(content: readonly ContentBlock[]): string {
  return content.filter((block): block is Extract<ContentBlock, { type: 'text' }> => block.type === 'text').map(block => block.text).join('\n')
}

function failure(code: string, message: string): { ok: false; code: string; message: string } {
  return { ok: false, code, message }
}

function failureOf(error: unknown): { ok: false; code: string; message: string } {
  if (error instanceof RestError) return failure(error.code, error.message)
  return failure('internal', error instanceof Error ? error.message : String(error))
}

/** `ctx.atlassian`: the whole Jira/Confluence/Bitbucket seam. */
export class AtlassianService extends TypertRemoteService {
  static inject = ['tools']

  static Config: z<Config> = z.object({})

  /** Fetch implementation used by the REST adapters (tests substitute a fake). */
  fetchImpl: FetchLike = (input, init) => fetch(input, init)

  /** Clock and id source. */
  clock: Clock = defaultClock

  private settingsScope: SettingsScope<AtlassianSettings> | undefined
  private readonly mounts: McpMounts
  private lastPlans: readonly MountPlan[] = []
  private readonly diffCache = new Map<string, { at: number; diff: NormalizedDiff }>()
  private disposed = false

  /**
   * @param ctx - plugin context (must carry `ctx.tools`).
   * @param _config - empty configuration.
   */
  constructor(ctx: Context, _config: Config = {}) {
    super(ctx, 'atlassian')
    this.mounts = new McpMounts(ctx)

    ctx.effect(() => () => {
      this.disposed = true
      void this.mounts.dispose()
    }, 'atlassian: mounts')

    // Settings: the namespace registers only when a settings provider is
    // composed; without one the schema defaults drive the mounts (nothing).
    ctx.inject(['settings'], (sctx) => {
      const scope = sctx.settings.register(ATLASSIAN_SETTINGS_NAMESPACE, AtlassianSettingsSchema, { base: DEFAULT_ATLASSIAN_SETTINGS })
      this.settingsScope = scope
      sctx.effect(() => {
        const unwatch = scope.watch(() => { void this.reconcile() })
        return () => {
          unwatch()
          this.settingsScope = undefined
        }
      }, 'atlassian: settings watch')
      void this.reconcile()
    })

    // Credentials: tokens resolve per operation; the seam arriving or leaving
    // changes what the mounts can do, as does any write to a referenced token.
    ctx.inject(['credentials'], (cctx) => {
      void this.reconcile()
      cctx.effect(() => () => { void this.reconcile() }, 'atlassian: credentials leave')
    })
    ctx.on('credentials/updated', (ref: CredentialRef) => {
      const settings = this.settings()
      if ([settings.jiraTokenRef, settings.confluenceTokenRef, settings.bitbucketTokenRef].includes(String(ref))) void this.reconcile()
    })

    // Write gate: waterfall listeners delegate through next().
    ctx.on('tools/pre-execute', (exec, next) => {
      const decision = gateDecision(exec.name, exec.arguments, this.settings().writes)
      return decision === undefined ? next() : Promise.resolve(decision)
    })

    // Result observer: activity, searches, and REST snapshots of touched entities.
    ctx.on('tools/result', (exec: ToolExecution, result: ToolExecutionResult) => {
      if (!isAtlassianTool(exec.name) || exec.parent !== undefined || exec.agent === undefined) return
      void this.observe(exec, result)
    })

    ctx.inject(['sessionProjections'], (pctx) => {
      pctx.sessionProjections.register(atlassianProjectionDefinition)
    })

    ctx.effect(() => registerReviewTools(ctx, {
      active: (session) => {
        const state = foldSession(session)
        const review = state.activeReviewId === null ? undefined : state.reviews[state.activeReviewId]
        return review === undefined
          /* v8 ignore next -- the fold always records the active review */
          ? undefined
          : {
            reviewId: review.id,
            pr: review.pr,
            findingCount: review.findings.length,
            findings: review.findings.map(item => ({ id: item.id, file: item.file, line: item.line, category: item.category })),
            existing: review.existing,
          }
      },
      finding: (session, reviewId, finding: ReviewFinding) => { session.append('atlassian/review', { op: 'finding', reviewId, finding }) },
      complete: (session, reviewId, summary, verdict: ReviewVerdict) => {
        session.append('atlassian/review', { op: 'complete', reviewId, summary, verdict, at: this.clock.now() })
      },
      now: () => this.clock.now(),
      nextId: () => this.clock.id('finding'),
    }), 'atlassian: review tools')

    ctx.inject(['commands'], (cctx) => {
      cctx.commands.register({
        name: 'ticket',
        description: 'Pin the Jira ticket this session works on (or clear it)',
        input: { hint: '<PROJ-123|clear>' },
        handler: ({ agent, rawInput }) => this.ticketCommand(agent, rawInput),
      })
      cctx.commands.register({
        name: 'pr-review',
        description: 'Review a Bitbucket pull request; findings stream into the Atlassian panel',
        input: { hint: '<PROJECT/repo#id or URL> [instructions]' },
        handler: ({ agent, rawInput }) => this.prReviewCommand(agent, rawInput),
      })
    })

    ctx.inject(['systemPrompt'], (pctx) => {
      pctx.systemPrompt.section({
        name: 'atlassian:guidance',
        order: 150,
        text: () => (this.mounts.status(ATLASSIAN_SERVER).toolCount > 0 || this.mounts.status(BITBUCKET_SERVER).toolCount > 0) ? GUIDANCE : '',
      })
      pctx.systemPrompt.context({
        name: 'atlassian:session',
        order: 125,
        text: (context) => {
          const agent = context.agent
          if (agent === undefined) return ''
          const state = foldSession(agent.session)
          const parts: string[] = []
          if (state.pinned !== null) {
            const issue = state.issues[state.pinned]
            parts.push(`The active Jira ticket of this session is ${state.pinned}${issue === undefined ? '' : ` ("${issue.summary}", status ${issue.status.name})`}. "This ticket" refers to it.`)
          }
          if (state.activeReviewId !== null) {
            const review = state.reviews[state.activeReviewId]
            /* v8 ignore next -- the fold always records the active review */
            if (review !== undefined) {
              parts.push(`A pull request review of ${review.prKey} is running: record each problem with ${REVIEW_FINDING_TOOL} and finish with ${REVIEW_COMPLETE_TOOL}.`)
            }
          }
          return parts.join(' ')
        },
      })
    })
  }

  // ---- Settings and connections ------------------------------------------------

  /**
   * Current settings (schema defaults until a provider is composed).
   * @returns the resolved section.
   */
  settings(): AtlassianSettings {
    return this.settingsScope?.get() ?? DEFAULT_ATLASSIAN_SETTINGS
  }

  private urls(settings = this.settings()): ResolvedUrls {
    const jira = normalizeBaseUrl(settings.jiraUrl)
    const confluence = normalizeBaseUrl(settings.confluenceUrl)
    const bitbucket = normalizeBaseUrl(settings.bitbucketUrl)
    return {
      ...jira === undefined ? {} : { jira },
      ...confluence === undefined ? {} : { confluence },
      ...bitbucket === undefined ? {} : { bitbucket },
    }
  }

  private async token(ref: string): Promise<string | undefined> {
    const credentials = this.ctx.get('credentials')
    if (credentials === undefined) return undefined
    let branded: CredentialRef
    try {
      branded = credentialRef(ref)
    /* v8 ignore start -- the settings schema enforces the same reference pattern; this guards an unvalidated future caller */
    } catch {
      // A malformed reference name resolves to nothing; the mount reports the missing token.
      return undefined
    }
    /* v8 ignore stop */
    const hit = await credentials.resolve(branded)
    return hit === undefined || hit.value.trim() === '' ? undefined : hit.value.trim()
  }

  private async tokens(settings = this.settings()): Promise<ResolvedTokens> {
    const [jira, confluence, bitbucket] = await Promise.all([
      this.token(settings.jiraTokenRef), this.token(settings.confluenceTokenRef), this.token(settings.bitbucketTokenRef),
    ])
    return {
      ...jira === undefined ? {} : { jira },
      ...confluence === undefined ? {} : { confluence },
      ...bitbucket === undefined ? {} : { bitbucket },
    }
  }

  private async plans(): Promise<readonly MountPlan[]> {
    const settings = this.settings()
    return planMounts(settings, this.urls(settings), await this.tokens(settings))
  }

  /**
   * Recompute both mount plans and bring the children in line.
   * @returns completion once the mounts settled.
   */
  async reconcile(): Promise<void> {
    if (this.isDisposed()) return
    this.lastPlans = await this.plans()
    if (this.isDisposed()) return
    await this.mounts.reconcile(this.lastPlans)
  }

  private isDisposed(): boolean {
    return this.disposed
  }

  private async target(service: 'jira' | 'confluence' | 'bitbucket'): Promise<RestTarget | undefined> {
    const settings = this.settings()
    const url = this.urls(settings)[service]
    const token = await this.token(service === 'jira' ? settings.jiraTokenRef : service === 'confluence' ? settings.confluenceTokenRef : settings.bitbucketTokenRef)
    return url === undefined || token === undefined ? undefined : { baseUrl: url, token }
  }

  private async jira(): Promise<JiraRest | undefined> {
    const target = await this.target('jira')
    return target === undefined ? undefined : new JiraRest(this.fetchImpl, target, () => this.clock.now())
  }

  private async confluence(): Promise<ConfluenceRest | undefined> {
    const target = await this.target('confluence')
    return target === undefined ? undefined : new ConfluenceRest(this.fetchImpl, target, () => this.clock.now())
  }

  private async bitbucket(): Promise<BitbucketRest | undefined> {
    const target = await this.target('bitbucket')
    return target === undefined ? undefined : new BitbucketRest(this.fetchImpl, target, () => this.clock.now())
  }

  // ---- Observation ---------------------------------------------------------------

  private async observe(exec: ToolExecution, result: ToolExecutionResult): Promise<void> {
    const agent = exec.agent
    if (agent === undefined) return
    const session = agent.session
    const raw = rawToolName(exec.name)
    const text = textOf(result.content)
    const refs = touchedEntities(raw, exec.arguments, result.isError ? undefined : text)
    const { kind } = classifyTool(raw)
    const at = this.clock.now()
    const first = refs[0]
    const entity = first === undefined ? undefined : this.entityRefOf(first)
    this.appendSafely(session, () => {
      session.append('atlassian/activity', {
        id: this.clock.id('act'),
        at,
        kind,
        tool: exec.name,
        ...entity === undefined ? {} : { entity },
        summary: activitySummary(raw, exec.arguments, refs, !result.isError),
        ok: !result.isError,
        callId: String(exec.callId),
      })
    })
    if (result.isError) return
    const search = searchRecord(raw, exec.arguments, text, String(exec.callId))
    if (search !== undefined) this.appendSafely(session, () => { session.append('atlassian/search', search) })
    const settled = await Promise.allSettled(refs.slice(0, TOUCH_LIMIT).map(ref => this.fetchEntity(ref)))
    settled.forEach((outcome, index) => {
      if (outcome.status === 'rejected') {
        this.ctx.logger.warn(`atlassian: could not fetch ${JSON.stringify(refs[index])} after ${exec.name}: ${outcome.reason instanceof Error ? outcome.reason.message : String(outcome.reason)}`)
        return
      }
      const record = outcome.value
      if (record === undefined) return
      this.appendSafely(session, () => {
        session.append('atlassian/snapshot', { entity: record, focus: index === 0, reason: 'tool', callId: String(exec.callId) })
      })
    })
  }

  /** Run one session append, containing a closed-session throw; reports whether it landed. */
  private appendSafely(_session: Session, append: () => void): boolean {
    try {
      append()
      return true
    } catch (error: unknown) {
      this.ctx.logger.warn(`atlassian: session append failed: ${error instanceof Error ? error.message : String(error)}`)
      return false
    }
  }

  private entityRefOf(ref: TouchRef): EntityRef {
    switch (ref.kind) {
      case 'issue': return { kind: 'issue', key: ref.key }
      /* v8 ignore next -- page refs without an id always carry space and title */
      case 'page': return { kind: 'page', id: ref.id ?? `${ref.space ?? ''}:${ref.title ?? ''}` }
      case 'pr': return { kind: 'pr', key: prKey({ project: ref.project ?? this.settings().bitbucketDefaultProject, repo: ref.repo, id: ref.id }) }
      /* v8 ignore next 2 -- closed union backstop */
      default: return { kind: 'issue', key: '' }
    }
  }

  /**
   * Fetch one entity over REST. Resolves to `undefined` when the service is not configured.
   * @param ref - touched entity.
   * @returns the record.
   */
  private async fetchEntity(ref: TouchRef): Promise<EntityRecord | undefined> {
    switch (ref.kind) {
      case 'issue': {
        const jira = await this.jira()
        return jira === undefined ? undefined : jira.getIssue(ref.key)
      }
      case 'page': {
        const confluence = await this.confluence()
        if (confluence === undefined) return undefined
        if (ref.id !== undefined) return confluence.getPage(ref.id)
        return ref.space !== undefined && ref.title !== undefined ? confluence.findPage(ref.space, ref.title) : undefined
      }
      case 'pr': {
        const bitbucket = await this.bitbucket()
        if (bitbucket === undefined) return undefined
        const project = ref.project ?? this.settings().bitbucketDefaultProject.trim()
        if (project === '') return undefined
        return bitbucket.getPullRequest({ project, repo: ref.repo, id: ref.id })
      }
      /* v8 ignore next 2 -- closed union backstop */
      default: return undefined
    }
  }

  private async openInto(session: Session, ref: TouchRef, reason: 'open' | 'pin' | 'review' | 'refresh'): Promise<OpenResult> {
    let record: EntityRecord | undefined
    try {
      record = await this.fetchEntity(ref)
    } catch (error: unknown) {
      return failureOf(error)
    }
    if (record === undefined) return failure('not-configured', `${ref.kind === 'issue' ? 'Jira' : ref.kind === 'page' ? 'Confluence' : 'Bitbucket'} URL or token is not configured`)
    const entity = record
    this.appendSafely(session, () => { session.append('atlassian/snapshot', { entity, focus: true, reason }) })
    return { ok: true, entity: this.entityRefOf(ref) }
  }

  // ---- Commands ------------------------------------------------------------------

  private ticketCommand(agent: Agent, rawInput: string): Promise<{ kind: 'success'; text: string } | { kind: 'error'; text: string }> {
    const input = rawInput.trim()
    if (input === '') {
      const state = foldSession(agent.session)
      return Promise.resolve({ kind: 'success', text: state.pinned === null ? 'No ticket is pinned. Usage: /ticket <PROJ-123|clear>' : `Pinned ticket: ${state.pinned}` })
    }
    if (input.toLowerCase() === 'clear' || input.toLowerCase() === 'off') {
      this.appendSafely(agent.session, () => { agent.session.append('atlassian/pin', { key: null }) })
      return Promise.resolve({ kind: 'success', text: 'Ticket pin cleared.' })
    }
    const key = input.toUpperCase()
    if (!/^[A-Z][A-Z0-9_]+-\d+$/.test(key)) return Promise.resolve({ kind: 'error', text: `"${input}" is not a Jira issue key. Usage: /ticket <PROJ-123|clear>` })
    this.appendSafely(agent.session, () => { agent.session.append('atlassian/pin', { key }) })
    return this.openInto(agent.session, { kind: 'issue', key }, 'pin').then(result => ({
      kind: 'success' as const,
      text: result.ok ? `Pinned ${key}. "This ticket" now refers to it.` : `Pinned ${key} (details unavailable: ${result.message}).`,
    }))
  }

  private async prReviewCommand(agent: Agent, rawInput: string): Promise<{ kind: 'success'; text: string } | { kind: 'error'; text: string }> {
    const input = rawInput.trim()
    const [head, ...rest] = input.split(/\s+/)
    const parsed = head === undefined || head === '' ? undefined : parsePrRef(head)
    if (parsed === undefined) return { kind: 'error', text: 'Usage: /pr-review <PROJECT/repo#id or pull request URL> [instructions]' }
    const result = await this.beginReview(agent, parsed, rest.join(' ').trim() || undefined)
    return result.ok
      ? { kind: 'success', text: `Reviewing ${prKey(parsed)}. Findings stream into the Atlassian panel.` }
      : { kind: 'error', text: result.message }
  }

  private async beginReview(
    agent: Agent, pr: PrRef, focus: string | undefined,
  ): Promise<{ ok: true; reviewId: string } | { ok: false; code: string; message: string }> {
    const settings = this.settings()
    const project = pr.project === '' ? settings.bitbucketDefaultProject.trim() : pr.project
    if (project === '') return failure('invalid-pr', 'The pull request needs a project key (PROJECT/repo#id).')
    const ref: PrRef = { project, repo: pr.repo, id: pr.id }
    if (this.mounts.status(BITBUCKET_SERVER).toolCount === 0) {
      return failure('bitbucket-unavailable', 'The Bitbucket MCP server is not connected; configure it in Settings → Atlassian first.')
    }
    // Comments already on the pull request: the reviewer must not repeat them,
    // and the panel shows them beside the findings. A REST failure here only
    // costs that awareness, never the review.
    let existing: ExistingPrComment[] = []
    const bitbucket = await this.bitbucket()
    if (bitbucket !== undefined) {
      try {
        existing = await bitbucket.getComments(ref)
      } catch (error: unknown) {
        this.ctx.logger.warn(`atlassian: could not read existing comments of ${prKey(ref)}: ${error instanceof Error ? error.message : String(error)}`)
      }
    }
    const reviewId = this.clock.id('review')
    const at = this.clock.now()
    const appended = this.appendSafely(agent.session, () => {
      agent.session.append('atlassian/review', { op: 'start', reviewId, pr: ref, at, existing })
    })
    if (!appended) return failure('session-closed', 'The session no longer accepts events.')
    void this.openInto(agent.session, { kind: 'pr', project: ref.project, repo: ref.repo, id: ref.id }, 'review')
    try {
      agent.followup(createUserMessage({
        content: [{ type: 'text', text: reviewInstructions(ref, settings.bitbucketDefaultProject, focus, existing) }],
        source: { kind: 'plugin', plugin: 'atlassian', form: 'instructions' },
      }))
    } catch (error: unknown) {
      this.appendSafely(agent.session, () => { agent.session.append('atlassian/review', { op: 'cancel', reviewId, at: this.clock.now() }) })
      return failure('queue-failed', `Could not queue the review turn: ${error instanceof Error ? error.message : String(error)}`)
    }
    return { ok: true, reviewId }
  }

  // ---- Remote API ------------------------------------------------------------------

  /**
   * Whole integration status.
   * @returns mount phases, tool counts, and REST readiness.
   */
  @Remote('status')
  status(): AtlassianStatus {
    const settings = this.settings()
    const urls = this.urls(settings)
    return {
      atlassian: this.mounts.status(ATLASSIAN_SERVER),
      bitbucket: this.mounts.status(BITBUCKET_SERVER),
      rest: { jira: urls.jira !== undefined, confluence: urls.confluence !== undefined, bitbucket: urls.bitbucket !== undefined },
    }
  }

  /**
   * Retry failed mounts and recompute plans after a settings/credential change.
   * @returns status after the retry settled.
   */
  @Remote('reconnect')
  async reconnect(): Promise<AtlassianStatus> {
    this.lastPlans = await this.plans()
    await this.mounts.retry(this.lastPlans)
    return this.status()
  }

  /**
   * Probe one service with the stored URL and token.
   * @param request - which service.
   * @returns the probe outcome.
   */
  @Remote('probe')
  async probe(request: ProbeRequest): Promise<ProbeResult> {
    const service = request.service
    try {
      switch (service) {
        case 'jira': {
          const jira = await this.jira()
          if (jira === undefined) return { service, ok: false, error: 'URL or token is not configured' }
          return { service, ok: true, user: await jira.myself() }
        }
        case 'confluence': {
          const confluence = await this.confluence()
          if (confluence === undefined) return { service, ok: false, error: 'URL or token is not configured' }
          return { service, ok: true, user: await confluence.myself() }
        }
        case 'bitbucket': {
          const bitbucket = await this.bitbucket()
          if (bitbucket === undefined) return { service, ok: false, error: 'URL or token is not configured' }
          const count = await bitbucket.probe()
          return { service, ok: true, user: `${String(count)} pull request(s) in your review inbox` }
        }
        /* v8 ignore next 2 -- closed union backstop */
        default: return { service, ok: false, error: 'unknown service' }
      }
    } catch (error: unknown) {
      return { service, ok: false, error: failureOf(error).message }
    }
  }

  /**
   * Fetch one entity, record it, and focus the panel on it.
   * @param agent - owning live agent.
   * @param request - which entity.
   * @returns the entity reference or a failure.
   */
  @Remote('open')
  open(agent: Agent, request: OpenRequest): Promise<OpenResult> {
    switch (request.kind) {
      case 'issue': return this.openInto(agent.session, { kind: 'issue', key: request.key.toUpperCase() }, 'open')
      case 'page': return this.openInto(agent.session, { kind: 'page', id: request.id }, 'open')
      case 'pr': return this.openInto(agent.session, { kind: 'pr', project: request.pr.project, repo: request.pr.repo, id: request.pr.id }, 'open')
      /* v8 ignore next 2 -- closed union backstop */
      default: return Promise.resolve(failure('invalid-request', 'unknown entity kind'))
    }
  }

  /**
   * Pin (or clear) the session's ticket.
   * @param agent - owning live agent.
   * @param request - key or `null`.
   * @returns acknowledgement.
   */
  @Remote('pin')
  async pin(agent: Agent, request: PinRequest): Promise<AckResult> {
    const result = await this.ticketCommand(agent, request.key === null ? 'clear' : request.key)
    return result.kind === 'success' ? { ok: true } : failure('invalid-key', result.text)
  }

  /**
   * List pull requests for the picker.
   * @param request - inbox or one repository.
   * @returns picker rows.
   */
  @Remote('listPullRequests')
  async listPullRequests(request: ListPullRequestsRequest): Promise<ListPullRequestsResult> {
    const bitbucket = await this.bitbucket()
    if (bitbucket === undefined) return failure('not-configured', 'Bitbucket URL or token is not configured')
    const state = request.state ?? 'OPEN'
    try {
      if (request.scope === 'repo') {
        const project = (request.project ?? this.settings().bitbucketDefaultProject).trim()
        const repo = (request.repo ?? '').trim()
        if (project === '' || repo === '') return failure('invalid-request', 'project and repository are required')
        return { ok: true, items: await bitbucket.listPullRequests(project, repo, state) }
      }
      return { ok: true, items: await bitbucket.inbox(state) }
    } catch (error: unknown) {
      return failureOf(error)
    }
  }

  /**
   * Post one review finding to Bitbucket, inline on its diff line when the
   * line is part of the diff, as a general comment otherwise.
   * @param agent - owning live agent.
   * @param request - review, finding, optional comment override.
   * @returns the posted comment.
   */
  @Remote('postFinding')
  async postFinding(agent: Agent, request: PostFindingRequest): Promise<PostFindingResult> {
    const state = foldSession(agent.session)
    const review = state.reviews[request.reviewId]
    const finding = review?.findings.find(item => item.id === request.findingId)
    if (review === undefined || finding === undefined) return failure('not-found', 'finding not found in this session')
    if (finding.posted !== undefined) return failure('already-posted', 'this finding was already posted')
    const bitbucket = await this.bitbucket()
    if (bitbucket === undefined) return failure('not-configured', 'Bitbucket URL or token is not configured')
    try {
      const diff = await this.diff(bitbucket, review.pr)
      const anchor = resolveAnchor(diff, finding.file, finding.line, finding.side)
      const body = (request.comment ?? finding.comment).trim()
      const text = anchor === undefined ? `**${finding.file}:${String(finding.line)}** — ${body}` : body
      const posted = await bitbucket.postComment(review.pr, text, anchor)
      const mode = anchor === undefined ? 'general' : 'inline'
      this.appendSafely(agent.session, () => {
        agent.session.append('atlassian/review', {
          op: 'posted', reviewId: review.id, findingId: finding.id, commentId: posted.id, url: posted.url, mode, at: this.clock.now(),
        })
      })
      return { ok: true, commentId: posted.id, url: posted.url, mode }
    } catch (error: unknown) {
      return failureOf(error)
    }
  }

  /**
   * Dismiss one finding (never posted).
   * @param agent - owning live agent.
   * @param request - review and finding.
   * @returns acknowledgement.
   */
  @Remote('dismissFinding')
  dismissFinding(agent: Agent, request: DismissFindingRequest): AckResult {
    const state = foldSession(agent.session)
    const review = state.reviews[request.reviewId]
    if (review === undefined || !review.findings.some(item => item.id === request.findingId)) return failure('not-found', 'finding not found in this session')
    this.appendSafely(agent.session, () => {
      agent.session.append('atlassian/review', { op: 'dismiss', reviewId: request.reviewId, findingId: request.findingId })
    })
    return { ok: true }
  }

  /**
   * Cancel the running review of a session.
   * @param agent - owning live agent.
   * @param request - review to cancel.
   * @returns acknowledgement.
   */
  @Remote('cancelReview')
  cancelReview(agent: Agent, request: CancelReviewRequest): AckResult {
    const reviewId = request.reviewId
    const state = foldSession(agent.session)
    const review = state.reviews[reviewId]
    if (review === undefined) return failure('not-found', 'review not found in this session')
    if (review.status !== 'running') return { ok: true }
    this.appendSafely(agent.session, () => {
      agent.session.append('atlassian/review', { op: 'cancel', reviewId, at: this.clock.now() })
    })
    return { ok: true }
  }

  /**
   * Diff lines around one finding for the evidence view.
   * @param request - PR, file, line, side.
   * @returns the window.
   */
  @Remote('diffContext')
  async diffContext(request: DiffContextRequest): Promise<DiffContextResult> {
    const bitbucket = await this.bitbucket()
    if (bitbucket === undefined) return failure('not-configured', 'Bitbucket URL or token is not configured')
    try {
      const diff = await this.diff(bitbucket, request.pr)
      const context = Math.min(30, Math.max(1, request.context ?? 6))
      return { ok: true, ...diffWindow(diff, request.file, request.line, request.side, context) }
    } catch (error: unknown) {
      return failureOf(error)
    }
  }

  private async diff(bitbucket: BitbucketRest, pr: PrRef): Promise<NormalizedDiff> {
    const key = prKey(pr)
    const cached = this.diffCache.get(key)
    const now = this.clock.now()
    if (cached !== undefined && now - cached.at < DIFF_CACHE_MS) return cached.diff
    const diff = await bitbucket.getDiff(pr)
    this.diffCache.set(key, { at: now, diff })
    return diff
  }
}

export default AtlassianService
