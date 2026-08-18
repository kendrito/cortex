/**
 * Shared fixtures for the ui-atlassian panel specs: entity records, review
 * records, a projection builder, the panel store state, and the translate seat.
 */
import { vi } from 'vitest'
import type {
  AtlassianProjection, ExistingPrComment, IssueRecord, PageRecord, PrRecord, ReviewFinding, ReviewRecord,
} from '@cortex/atlassian/client'
import type { ConversationSnapshot, SessionId, SessionListState, WorkspaceListState } from '@cortex/client-runtime/client'
import type { SnapshotSelectorHook } from '@cortex/client-ui-slots'
import { makeTranslate } from '@cortex/client-test-runtime'
import { en } from '../src/client/locales.ts'
import type { PanelState } from '../src/client/store.ts'

/** Session id used across the specs. */
const SID = 's-atl' as SessionId

/** Loose override map: an explicit `undefined` removes the optional field. */
type Overrides<T> = { [K in keyof T]?: T[K] | undefined }

/**
 * Merge overrides into a base record, dropping keys set to `undefined` so the
 * result stays an exact-optional record.
 * @param base - complete record.
 * @param overrides - fields to replace or remove.
 * @returns the merged record.
 */
function merged<T extends object>(base: T, overrides: Overrides<T>): T {
  const out: Record<string, unknown> = {}
  for (const [key, value] of Object.entries({ ...(base as Record<string, unknown>), ...overrides })) {
    if (value !== undefined) out[key] = value
  }
  return out as T
}

/** Translate seat over the package dictionary. */
export const t = makeTranslate(en) as never

/** Fixed clock for relative times (2026-08-18T12:00:00Z). */
export const NOW = Date.parse('2026-08-18T12:00:00.000Z')

/**
 * Full issue record.
 * @param overrides - fields to override.
 * @returns the record.
 */
export function issue(overrides: Overrides<IssueRecord> = {}): IssueRecord {
  return merged<IssueRecord>({
    kind: 'issue',
    key: 'PROJ-123',
    summary: 'Login page ignores SSO redirect target',
    status: { name: 'In Progress', category: 'indeterminate' },
    type: 'Story',
    priority: 'High',
    assignee: { name: 'Avery Quinn', id: 'aquinn' },
    reporter: { name: 'Jordan Alvarez', id: 'jalvarez' },
    labels: ['auth', 'frontend'],
    components: ['web'],
    fixVersions: ['2.4.0'],
    description: '## Problem\n\nThe app drops the `state` parameter.',
    created: '2026-08-15T10:00:00.000Z',
    updated: '2026-08-18T11:00:00.000Z',
    dueDate: '2026-08-22',
    project: 'PROJ',
    parent: { key: 'PROJ-100', summary: 'Auth epic' },
    subtasks: [{ key: 'PROJ-124', summary: 'Add regression test', status: { name: 'To Do', category: 'new' } }],
    epic: { key: 'PROJ-98', name: 'SSO hardening' },
    sprint: 'Sprint 42',
    storyPoints: 5,
    comments: [
      { id: '2', author: { name: 'Avery Quinn' }, created: new Date(NOW - 60_000).toISOString(), body: 'Fresh comment' },
      { id: '1', author: { name: 'Jordan Alvarez' }, created: '2026-08-16T09:12:00.000Z', body: 'Old comment' },
    ],
    links: [{ relation: 'blocks', key: 'PROJ-98', summary: 'SSO hardening rollout', status: { name: 'In Progress', category: 'indeterminate' } }],
    attachments: [
      { filename: 'redirect-loop.png', size: 48213, url: 'http://jira/attach/1', mimeType: 'image/png' },
      { filename: 'notes.txt', size: 12 },
    ],
    transitions: [{ id: '31', name: 'Ready for review', to: 'In Review' }, { id: '41', name: 'Done', to: 'Done' }],
    url: 'http://jira/browse/PROJ-123',
    fetchedAt: NOW,
  }, overrides)
}

/**
 * Minimal issue record (every optional field absent).
 * @returns the record.
 */
export function minimalIssue(): IssueRecord {
  return {
    kind: 'issue',
    key: 'PROJ-1',
    summary: 'Bare issue',
    status: { name: 'Done', category: 'done' },
    type: 'Bug',
    labels: [],
    components: [],
    fixVersions: [],
    description: '',
    subtasks: [],
    comments: [],
    links: [],
    attachments: [],
    transitions: [],
    url: 'http://jira/browse/PROJ-1',
    fetchedAt: NOW,
    resolution: 'Fixed',
  }
}

/**
 * Full page record.
 * @param overrides - fields to override.
 * @returns the record.
 */
export function page(overrides: Overrides<PageRecord> = {}): PageRecord {
  return merged<PageRecord>({
    kind: 'page',
    id: '98765',
    title: 'Auth service runbook',
    space: { key: 'ENG', name: 'Engineering' },
    version: 12,
    versionAt: new Date(NOW - 3 * 3_600_000).toISOString(),
    versionBy: { name: 'Avery Quinn' },
    created: '2025-11-02T09:00:00.000Z',
    author: { name: 'Jordan Alvarez' },
    ancestors: [{ id: '100', title: 'Platform' }, { id: '200', title: 'Runbooks' }],
    labels: ['runbook', 'auth'],
    body: '## Purpose\n\nOperate the **auth service**.',
    bodyTruncated: false,
    url: 'http://confluence/display/ENG/Auth+service+runbook',
    fetchedAt: NOW,
  }, overrides)
}

/**
 * Full pull request record.
 * @param overrides - fields to override.
 * @returns the record.
 */
export function pr(overrides: Overrides<PrRecord> = {}): PrRecord {
  return merged<PrRecord>({
    kind: 'pr',
    ref: { project: 'PROJ', repo: 'webapp', id: 42 },
    key: 'PROJ/webapp#42',
    title: 'Fix SSO redirect loop after IdP callback',
    description: 'Reads the redirect target **after** the cookie is set.',
    state: 'OPEN',
    author: { name: 'Avery Quinn', id: 'aquinn' },
    reviewers: [
      { user: { name: 'Jordan Alvarez', id: 'jalvarez' }, status: 'APPROVED', role: 'REVIEWER' },
      { user: { name: 'Mei Chen', id: 'mchen' }, status: 'UNAPPROVED', role: 'REVIEWER' },
      { user: { name: 'Sam Lee' }, status: 'NEEDS_WORK', role: 'PARTICIPANT' },
    ],
    from: { branch: 'feature/PROJ-123-sso', commit: 'a1b2c3' },
    to: { branch: 'main', commit: '0f9e8d' },
    created: '2026-08-17T10:00:00.000Z',
    updated: new Date(NOW - 2 * 86_400_000).toISOString(),
    version: 3,
    url: 'http://bitbucket/projects/PROJ/repos/webapp/pull-requests/42/overview',
    fetchedAt: NOW,
  }, overrides)
}

/**
 * One review finding.
 * @param overrides - fields to override.
 * @returns the finding.
 */
export function finding(overrides: Overrides<ReviewFinding> = {}): ReviewFinding {
  return merged<ReviewFinding>({
    id: 'f-1',
    at: NOW - 30_000,
    file: 'src/auth/redirect.ts',
    line: 4,
    side: 'ADDED',
    severity: 'critical',
    category: 'correctness',
    title: 'Open redirect',
    comment: 'Validate the target before redirecting.',
    evidence: '  return decodeURIComponent(state)',
    rationale: 'The caller redirects to whatever this returns.',
  }, overrides)
}

/**
 * One review record.
 * @param overrides - fields to override.
 * @returns the review.
 */
export function review(overrides: Overrides<ReviewRecord> = {}): ReviewRecord {
  return merged<ReviewRecord>({
    id: 'r-1',
    pr: { project: 'PROJ', repo: 'webapp', id: 42 },
    prKey: 'PROJ/webapp#42',
    status: 'running',
    startedAt: NOW - 120_000,
    findings: [],
    existing: [],
  }, overrides)
}

/**
 * One comment already on the pull request.
 * @param overrides - fields to override.
 * @returns the existing comment.
 */
export function existingComment(overrides: Overrides<ExistingPrComment> = {}): ExistingPrComment {
  return merged<ExistingPrComment>({
    id: 501,
    author: { name: 'Mei Chen', id: 'mchen' },
    text: 'Please validate the redirect target here.',
    file: 'src/auth/redirect.ts',
    line: 3,
    side: 'ADDED',
    created: '2026-08-17T12:00:00.000Z',
    replies: 1,
  }, overrides)
}

/**
 * Projection value builder.
 * @param overrides - fields to override.
 * @returns the projection.
 */
export function projection(overrides: Partial<AtlassianProjection> = {}): AtlassianProjection {
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
    ...overrides,
  }
}

/**
 * Panel store state builder.
 * @param overrides - fields to override.
 * @returns the state.
 */
export function panelState(overrides: Partial<PanelState> = {}): PanelState {
  return {
    open: false,
    tab: 'work',
    selected: null,
    seenRev: null,
    seenFocus: null,
    seenReview: null,
    autoOpen: true,
    reviewFilter: 'all',
    ...overrides,
  }
}

/** Panel store actions as spies. */
export interface PanelActionSpies {
  open: ReturnType<typeof vi.fn>
  close: ReturnType<typeof vi.fn>
  toggle: ReturnType<typeof vi.fn>
  setTab: ReturnType<typeof vi.fn>
  select: ReturnType<typeof vi.fn>
  acknowledge: ReturnType<typeof vi.fn>
  setAutoOpen: ReturnType<typeof vi.fn>
  setReviewFilter: ReturnType<typeof vi.fn>
  showEntity: ReturnType<typeof vi.fn>
}

/**
 * Store actions as spies.
 * @returns the action set.
 */
export function panelActions(): PanelActionSpies {
  return {
    open: vi.fn(),
    close: vi.fn(),
    toggle: vi.fn(),
    setTab: vi.fn(),
    select: vi.fn(),
    acknowledge: vi.fn(),
    setAutoOpen: vi.fn(),
    setReviewFilter: vi.fn(),
    showEntity: vi.fn(),
  }
}

/** Framework standard-kit stubs for session-scoped components. */
export const kit = {
  sessionId: SID,
  session: undefined,
  useSession: (() => { throw new Error('unused') }) as unknown as SnapshotSelectorHook<ConversationSnapshot>,
  useSessions: (() => { throw new Error('unused') }) as unknown as SnapshotSelectorHook<SessionListState>,
  useWorkspaces: (() => { throw new Error('unused') }) as unknown as SnapshotSelectorHook<WorkspaceListState>,
  useInput: (() => { throw new Error('unused') }) as never,
  inputActions: { setDraft: () => { throw new Error('unused') }, submit: () => { throw new Error('unused') } } as never,
}
