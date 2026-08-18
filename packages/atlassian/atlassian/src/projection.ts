/**
 * The `atlassian` session projection: a pure fold of the `atlassian/*` log
 * events into the whole-value panel state (entities, focus, pin, activity,
 * searches, reviews). Every bound lives here so the wire payload stays small
 * regardless of session length.
 *
 * @module
 */

import { z } from 'zod'
import type { ProjectionDefinition } from '@cortex/session-projection'
import type { SessionEvent } from '@cortex/session'
import type {
  ActivityEntry, AtlassianProjection, AtlassianReviewEvent, EntityRecord, EntityRef, ReviewRecord, SearchRecord,
} from './types.ts'

/** Entities kept in the panel (most recent first). */
export const RECENT_LIMIT = 30
/** Search result sets kept. */
export const SEARCH_LIMIT = 5
/** Activity entries kept. */
export const ACTIVITY_LIMIT = 40
/** Findings kept per review. */
export const FINDING_LIMIT = 200

// ---- Wire schema ------------------------------------------------------------

const person = z.object({ name: z.string(), id: z.string().optional(), avatar: z.string().optional() })
const status = z.object({ name: z.string(), category: z.enum(['new', 'indeterminate', 'done', 'unknown']) })
const issueRef = z.object({ kind: z.literal('issue'), key: z.string() })
const pageRef = z.object({ kind: z.literal('page'), id: z.string() })
const prRefKey = z.object({ kind: z.literal('pr'), key: z.string() })
const entityRef = z.union([issueRef, pageRef, prRefKey])
const prRef = z.object({ project: z.string(), repo: z.string(), id: z.number() })

const issueRecord = z.object({
  kind: z.literal('issue'),
  key: z.string(),
  summary: z.string(),
  status,
  type: z.string(),
  priority: z.string().optional(),
  assignee: person.optional(),
  reporter: person.optional(),
  labels: z.array(z.string()),
  components: z.array(z.string()),
  fixVersions: z.array(z.string()),
  description: z.string(),
  created: z.string().optional(),
  updated: z.string().optional(),
  dueDate: z.string().optional(),
  resolution: z.string().optional(),
  project: z.string().optional(),
  parent: z.object({ key: z.string(), summary: z.string() }).optional(),
  subtasks: z.array(z.object({ key: z.string(), summary: z.string(), status: status.optional() })),
  epic: z.object({ key: z.string(), name: z.string().optional() }).optional(),
  sprint: z.string().optional(),
  storyPoints: z.number().optional(),
  comments: z.array(z.object({ id: z.string(), author: person, created: z.string(), body: z.string() })),
  links: z.array(z.object({ relation: z.string(), key: z.string(), summary: z.string(), status: status.optional() })),
  attachments: z.array(z.object({ filename: z.string(), size: z.number(), url: z.string().optional(), mimeType: z.string().optional() })),
  transitions: z.array(z.object({ id: z.string(), name: z.string(), to: z.string() })),
  url: z.string(),
  fetchedAt: z.number(),
})

const pageRecord = z.object({
  kind: z.literal('page'),
  id: z.string(),
  title: z.string(),
  space: z.object({ key: z.string(), name: z.string().optional() }),
  version: z.number(),
  versionAt: z.string().optional(),
  versionBy: person.optional(),
  created: z.string().optional(),
  author: person.optional(),
  ancestors: z.array(z.object({ id: z.string(), title: z.string() })),
  labels: z.array(z.string()),
  body: z.string(),
  bodyTruncated: z.boolean(),
  url: z.string(),
  fetchedAt: z.number(),
})

const prRecord = z.object({
  kind: z.literal('pr'),
  ref: prRef,
  key: z.string(),
  title: z.string(),
  description: z.string(),
  state: z.enum(['OPEN', 'MERGED', 'DECLINED']),
  author: person,
  reviewers: z.array(z.object({
    user: person,
    status: z.enum(['APPROVED', 'NEEDS_WORK', 'UNAPPROVED']),
    role: z.enum(['REVIEWER', 'PARTICIPANT', 'AUTHOR']),
  })),
  from: z.object({ branch: z.string(), commit: z.string().optional() }),
  to: z.object({ branch: z.string(), commit: z.string().optional() }),
  created: z.string().optional(),
  updated: z.string().optional(),
  version: z.number(),
  url: z.string(),
  fetchedAt: z.number(),
})

const jiraRow = z.object({
  key: z.string(),
  summary: z.string(),
  status: status.optional(),
  type: z.string().optional(),
  priority: z.string().optional(),
  assignee: z.string().optional(),
  updated: z.string().optional(),
})

const confluenceRow = z.object({
  id: z.string(),
  title: z.string(),
  space: z.string().optional(),
  excerpt: z.string().optional(),
  url: z.string().optional(),
  updated: z.string().optional(),
})

const searchRecord = z.union([
  z.object({ service: z.literal('jira'), callId: z.string(), query: z.string(), total: z.number(), rows: z.array(jiraRow) }),
  z.object({ service: z.literal('confluence'), callId: z.string(), query: z.string(), total: z.number(), rows: z.array(confluenceRow) }),
])

const activityEntry = z.object({
  id: z.string(),
  at: z.number(),
  kind: z.enum([
    'read', 'search', 'create', 'update', 'comment', 'transition', 'assign',
    'link', 'approve', 'merge', 'decline', 'branch', 'delete', 'other',
  ]),
  tool: z.string(),
  entity: entityRef.optional(),
  summary: z.string(),
  ok: z.boolean(),
  callId: z.string().optional(),
})

const finding = z.object({
  id: z.string(),
  at: z.number(),
  file: z.string(),
  line: z.number(),
  side: z.enum(['ADDED', 'REMOVED', 'CONTEXT']),
  severity: z.enum(['critical', 'major', 'minor', 'nit']),
  category: z.enum(['security', 'correctness', 'readability', 'performance', 'testing', 'style']),
  title: z.string(),
  comment: z.string(),
  evidence: z.string(),
  rationale: z.string(),
  suggestion: z.string().optional(),
  overlaps: z.array(z.number()).optional(),
  posted: z.object({
    commentId: z.number(),
    url: z.string().optional(),
    mode: z.enum(['inline', 'general']),
    at: z.number(),
  }).optional(),
  dismissed: z.boolean().optional(),
})

const existingComment = z.object({
  id: z.number(),
  author: person,
  text: z.string(),
  file: z.string().optional(),
  line: z.number().optional(),
  side: z.enum(['ADDED', 'REMOVED', 'CONTEXT']).optional(),
  created: z.string().optional(),
  replies: z.number(),
})

const reviewRecord = z.object({
  id: z.string(),
  pr: prRef,
  prKey: z.string(),
  status: z.enum(['running', 'complete', 'cancelled']),
  startedAt: z.number(),
  completedAt: z.number().optional(),
  summary: z.string().optional(),
  verdict: z.enum(['approve', 'request-changes', 'comment']).optional(),
  findings: z.array(finding),
  existing: z.array(existingComment),
})

/**
 * Wire schema of the `atlassian` projection value. The object schema mirrors
 * {@link AtlassianProjection} field for field; the cast only bridges Zod's
 * `?: T | undefined` optionals to the record types' exact optionals (an
 * absent key stays absent through `parse`).
 */
export const atlassianProjectionSchema = z.object({
  rev: z.number(),
  pinned: z.string().nullable(),
  focus: entityRef.nullable(),
  issues: z.record(z.string(), issueRecord),
  pages: z.record(z.string(), pageRecord),
  prs: z.record(z.string(), prRecord),
  recent: z.array(entityRef),
  searches: z.array(searchRecord),
  activity: z.array(activityEntry),
  reviews: z.record(z.string(), reviewRecord),
  activeReviewId: z.string().nullable(),
}) as unknown as z.ZodType<AtlassianProjection>

// ---- Fold -------------------------------------------------------------------

/** Unit state: identical to the wire value (the view is the identity). */
export type AtlassianUnitState = AtlassianProjection

/**
 * Empty state.
 * @returns the state of an untouched session.
 */
export function emptyState(): AtlassianUnitState {
  return {
    rev: 0,
    pinned: null,
    focus: null,
    issues: {},
    pages: {},
    prs: {},
    recent: [],
    searches: [],
    activity: [],
    reviews: {},
    activeReviewId: null,
  }
}

/**
 * Address of one entity record.
 * @param entity - record.
 * @returns its reference.
 */
export function refOf(entity: EntityRecord): EntityRef {
  switch (entity.kind) {
    case 'issue': return { kind: 'issue', key: entity.key }
    case 'page': return { kind: 'page', id: entity.id }
    case 'pr': return { kind: 'pr', key: entity.key }
    /* v8 ignore next 2 -- closed union backstop */
    default: return assertNever(entity)
  }
}

/* v8 ignore next 3 -- closed-union exhaustiveness fence */
function assertNever(value: never): never {
  throw new Error(`unhandled entity ${JSON.stringify(value)}`)
}

function sameRef(a: EntityRef, b: EntityRef): boolean {
  if (a.kind !== b.kind) return false
  return a.kind === 'page' ? a.id === (b as { id: string }).id : a.key === (b as { key: string }).key
}

/** Insert `ref` at the front of `recent`, dropping duplicates and the tail beyond the bound. */
function touch(recent: readonly EntityRef[], ref: EntityRef): EntityRef[] {
  return [ref, ...recent.filter(existing => !sameRef(existing, ref))].slice(0, RECENT_LIMIT)
}

/** Drop records no longer referenced by `recent`. */
function evict<T>(map: Record<string, T>, keep: (key: string) => boolean): Record<string, T> {
  const next: Record<string, T> = {}
  for (const [key, value] of Object.entries(map)) if (keep(key)) next[key] = value
  return next
}

function applySnapshot(state: AtlassianUnitState, entity: EntityRecord, focus: boolean): AtlassianUnitState {
  const ref = refOf(entity)
  const recent = touch(state.recent, ref)
  const keep = (kind: EntityRef['kind']) => (key: string) =>
    recent.some(item => item.kind === kind && (item.kind === 'page' ? item.id : item.key) === key)
  const next: AtlassianUnitState = {
    ...state,
    recent,
    issues: evict(entity.kind === 'issue' ? { ...state.issues, [entity.key]: entity } : state.issues, keep('issue')),
    pages: evict(entity.kind === 'page' ? { ...state.pages, [entity.id]: entity } : state.pages, keep('page')),
    prs: evict(entity.kind === 'pr' ? { ...state.prs, [entity.key]: entity } : state.prs, keep('pr')),
  }
  if (focus) next.focus = ref
  return next
}

function applyReview(state: AtlassianUnitState, event: AtlassianReviewEvent): AtlassianUnitState {
  switch (event.op) {
    case 'start': {
      const review: ReviewRecord = {
        id: event.reviewId,
        pr: event.pr,
        prKey: `${event.pr.project.toUpperCase()}/${event.pr.repo}#${String(event.pr.id)}`,
        status: 'running',
        startedAt: event.at,
        findings: [],
        existing: event.existing,
      }
      const reviews = { ...state.reviews }
      for (const [id, existing] of Object.entries(reviews)) {
        if (existing.status === 'running') reviews[id] = { ...existing, status: 'cancelled', completedAt: event.at }
      }
      reviews[event.reviewId] = review
      return { ...state, reviews, activeReviewId: event.reviewId, focus: { kind: 'pr', key: review.prKey } }
    }
    case 'finding': {
      const review = state.reviews[event.reviewId]
      if (review === undefined || review.findings.length >= FINDING_LIMIT) return state
      const findings = [...review.findings.filter(item => item.id !== event.finding.id), event.finding]
      return { ...state, reviews: { ...state.reviews, [review.id]: { ...review, findings } } }
    }
    case 'complete': {
      const review = state.reviews[event.reviewId]
      if (review === undefined) return state
      const updated: ReviewRecord = { ...review, status: 'complete', completedAt: event.at, summary: event.summary, verdict: event.verdict }
      return {
        ...state,
        reviews: { ...state.reviews, [review.id]: updated },
        activeReviewId: state.activeReviewId === review.id ? null : state.activeReviewId,
      }
    }
    case 'cancel': {
      const review = state.reviews[event.reviewId]
      if (review === undefined) return state
      return {
        ...state,
        reviews: { ...state.reviews, [review.id]: { ...review, status: 'cancelled', completedAt: event.at } },
        activeReviewId: state.activeReviewId === review.id ? null : state.activeReviewId,
      }
    }
    case 'posted': {
      const review = state.reviews[event.reviewId]
      if (review === undefined) return state
      const posted = { commentId: event.commentId, ...event.url === undefined ? {} : { url: event.url }, mode: event.mode, at: event.at }
      const findings = review.findings.map(item => item.id === event.findingId ? { ...item, posted } : item)
      return { ...state, reviews: { ...state.reviews, [review.id]: { ...review, findings } } }
    }
    case 'dismiss': {
      const review = state.reviews[event.reviewId]
      if (review === undefined) return state
      const findings = review.findings.map(item => item.id === event.findingId ? { ...item, dismissed: true } : item)
      return { ...state, reviews: { ...state.reviews, [review.id]: { ...review, findings } } }
    }
    /* v8 ignore next 2 -- closed union backstop */
    default: return state
  }
}

/**
 * Pure transition over one committed session event.
 * @param state - state covering all prior events.
 * @param event - the next committed event.
 * @returns the next state; the same reference when the event is not this unit's.
 */
export function applyAtlassianEvent(state: AtlassianUnitState, event: SessionEvent): AtlassianUnitState {
  switch (event.type) {
    case 'atlassian/snapshot':
      return { ...applySnapshot(state, event.data.entity, event.data.focus), rev: event.seq }
    case 'atlassian/activity': {
      const entry: ActivityEntry = event.data
      const activity = [entry, ...state.activity.filter(item => item.id !== entry.id)].slice(0, ACTIVITY_LIMIT)
      return { ...state, activity, rev: event.seq }
    }
    case 'atlassian/search': {
      const search: SearchRecord = event.data
      const searches = [search, ...state.searches.filter(item => item.callId !== search.callId)].slice(0, SEARCH_LIMIT)
      return { ...state, searches, rev: event.seq }
    }
    case 'atlassian/pin':
      return { ...state, pinned: event.data.key, rev: event.seq }
    case 'atlassian/review':
      return { ...applyReview(state, event.data), rev: event.seq }
    default:
      return state
  }
}

/** The `atlassian` unit registered on `ctx.sessionProjections`. */
export const atlassianProjectionDefinition: ProjectionDefinition<'atlassian', AtlassianUnitState> = {
  key: 'atlassian',
  schema: atlassianProjectionSchema,
  init: emptyState,
  apply: applyAtlassianEvent,
  view: state => state,
  stateVersion: 2,
}
