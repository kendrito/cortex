import { describe, expect, it } from 'vitest'
import type { SessionEvent } from '@cortex/session'
import type { EntityRecord, IssueRecord, PageRecord, PrRecord, ReviewFinding } from '../src/types.ts'
import {
  ACTIVITY_LIMIT, FINDING_LIMIT, RECENT_LIMIT, SEARCH_LIMIT, applyAtlassianEvent, atlassianProjectionDefinition,
  atlassianProjectionSchema, emptyState, refOf,
} from '../src/projection.ts'

let seq = 0
function event<T extends SessionEvent['type']>(type: T, data: Extract<SessionEvent, { type: T }>['data']): SessionEvent {
  seq += 1
  return { type, seq, time: seq, data } as SessionEvent
}

export function issue(key: string, summary = `Issue ${key}`): IssueRecord {
  return {
    kind: 'issue', key, summary, status: { name: 'To Do', category: 'new' }, type: 'Story', labels: [], components: [],
    fixVersions: [], description: '', subtasks: [], comments: [], links: [], attachments: [], transitions: [],
    url: `http://j/browse/${key}`, fetchedAt: 1,
  }
}

export function page(id: string): PageRecord {
  return { kind: 'page', id, title: `Page ${id}`, space: { key: 'ENG' }, version: 1, ancestors: [], labels: [], body: '', bodyTruncated: false, url: 'http://c', fetchedAt: 1 }
}

export function pr(id: number): PrRecord {
  return {
    kind: 'pr', ref: { project: 'PROJ', repo: 'webapp', id }, key: `PROJ/webapp#${String(id)}`, title: `PR ${String(id)}`, description: '',
    state: 'OPEN', author: { name: 'K' }, reviewers: [], from: { branch: 'f' }, to: { branch: 'main' }, version: 1, url: 'http://b', fetchedAt: 1,
  }
}

export function finding(id: string, extra: Partial<ReviewFinding> = {}): ReviewFinding {
  return {
    id, at: 1, file: 'src/a.ts', line: 3, side: 'ADDED', severity: 'major', category: 'security', title: `Finding ${id}`,
    comment: 'fix it', evidence: 'const x', rationale: 'because', ...extra,
  }
}

const REVIEW_PR = { project: 'proj', repo: 'webapp', id: 42 }
const EXISTING = { id: 5, author: { name: 'Mei' }, text: 'Please validate the target', file: 'src/auth/redirect.ts', line: 3, side: 'ADDED' as const, replies: 1 }

describe('atlassian projection fold', () => {
  it('starts empty and leaves foreign events untouched by reference', () => {
    const state = emptyState()
    expect(state).toEqual({
      rev: 0, pinned: null, focus: null, issues: {}, pages: {}, prs: {},
      recent: [], searches: [], activity: [], reviews: {}, activeReviewId: null,
    })
    expect(applyAtlassianEvent(state, event('turn/start', { turn: 1 }))).toBe(state)
    expect(atlassianProjectionDefinition.key).toBe('atlassian')
    expect(atlassianProjectionDefinition.init()).toEqual(state)
    expect(atlassianProjectionDefinition.view(state)).toBe(state)
    expect(atlassianProjectionDefinition.stateVersion).toBe(2)
  })

  it('records snapshots of every kind, focus, recency, and eviction', () => {
    let state = emptyState()
    state = applyAtlassianEvent(state, event('atlassian/snapshot', { entity: issue('A-1'), focus: true, reason: 'tool' }))
    state = applyAtlassianEvent(state, event('atlassian/snapshot', { entity: page('9'), focus: false, reason: 'open' }))
    state = applyAtlassianEvent(state, event('atlassian/snapshot', { entity: pr(1), focus: true, reason: 'review' }))
    expect(state.rev).toBe(seq)
    expect(state.focus).toEqual({ kind: 'pr', key: 'PROJ/webapp#1' })
    expect(state.recent).toEqual([{ kind: 'pr', key: 'PROJ/webapp#1' }, { kind: 'page', id: '9' }, { kind: 'issue', key: 'A-1' }])
    expect(Object.keys(state.issues)).toEqual(['A-1'])
    expect(Object.keys(state.pages)).toEqual(['9'])
    expect(Object.keys(state.prs)).toEqual(['PROJ/webapp#1'])
    // Re-touching moves to the front without duplicating.
    state = applyAtlassianEvent(state, event('atlassian/snapshot', { entity: issue('A-1', 'renamed'), focus: false, reason: 'refresh' }))
    expect(state.recent[0]).toEqual({ kind: 'issue', key: 'A-1' })
    expect(state.recent).toHaveLength(3)
    expect(state.issues['A-1']?.summary).toBe('renamed')
    expect(state.focus).toEqual({ kind: 'pr', key: 'PROJ/webapp#1' })
    // Beyond the bound the oldest entity of any kind is evicted.
    for (let index = 0; index < RECENT_LIMIT; index += 1) {
      state = applyAtlassianEvent(state, event('atlassian/snapshot', { entity: issue(`B-${String(index)}`), focus: false, reason: 'tool' }))
    }
    expect(state.recent).toHaveLength(RECENT_LIMIT)
    expect(state.pages).toEqual({})
    expect(state.prs).toEqual({})
    expect(state.issues['A-1']).toBeUndefined()
    expect(Object.keys(state.issues)).toHaveLength(RECENT_LIMIT)
    // A record whose kind matches but whose key differs is not confused with another kind's key.
    const mixed = applyAtlassianEvent(applyAtlassianEvent(emptyState(), event('atlassian/snapshot', { entity: page('X'), focus: false, reason: 'tool' })),
      event('atlassian/snapshot', { entity: pr(5), focus: false, reason: 'tool' }))
    expect(Object.keys(mixed.pages)).toEqual(['X'])
    expect(atlassianProjectionSchema.parse(state)).toEqual(state)
  })

  it('folds activity, searches, and pins with bounds and dedupe', () => {
    let state = emptyState()
    for (let index = 0; index < ACTIVITY_LIMIT + 3; index += 1) {
      state = applyAtlassianEvent(state, event('atlassian/activity', {
        id: `act-${String(index)}`, at: index, kind: 'read', tool: 'mcp__atlassian__jira_get_issue', summary: `Read ${String(index)}`, ok: true,
      }))
    }
    expect(state.activity).toHaveLength(ACTIVITY_LIMIT)
    expect(state.activity[0]?.id).toBe(`act-${String(ACTIVITY_LIMIT + 2)}`)
    state = applyAtlassianEvent(state, event('atlassian/activity', { id: 'act-5', at: 99, kind: 'read', tool: 't', summary: 'again', ok: false, entity: { kind: 'issue', key: 'A-1' }, callId: 'c' }))
    expect(state.activity.filter(item => item.id === 'act-5')).toHaveLength(1)
    expect(state.activity[0]?.summary).toBe('again')
    for (let index = 0; index < SEARCH_LIMIT + 2; index += 1) {
      state = applyAtlassianEvent(state, event('atlassian/search', { service: 'jira', callId: `s-${String(index)}`, query: 'q', total: 0, rows: [] }))
    }
    expect(state.searches).toHaveLength(SEARCH_LIMIT)
    state = applyAtlassianEvent(state, event('atlassian/search', { service: 'confluence', callId: 's-6', query: 'again', total: 1, rows: [{ id: '1', title: 'T' }] }))
    expect(state.searches[0]).toMatchObject({ callId: 's-6', query: 'again' })
    expect(state.searches.filter(item => item.callId === 's-6')).toHaveLength(1)
    state = applyAtlassianEvent(state, event('atlassian/pin', { key: 'A-1' }))
    expect(state.pinned).toBe('A-1')
    state = applyAtlassianEvent(state, event('atlassian/pin', { key: null }))
    expect(state.pinned).toBeNull()
    expect(atlassianProjectionSchema.parse(state)).toEqual(state)
  })

  it('folds the review lifecycle', () => {
    let state = emptyState()
    state = applyAtlassianEvent(state, event('atlassian/review', { op: 'start', reviewId: 'r1', pr: REVIEW_PR, at: 10, existing: [EXISTING] }))
    expect(state.activeReviewId).toBe('r1')
    expect(state.focus).toEqual({ kind: 'pr', key: 'PROJ/webapp#42' })
    expect(state.reviews.r1).toMatchObject({ prKey: 'PROJ/webapp#42', status: 'running', startedAt: 10, findings: [], existing: [EXISTING] })
    // Findings append, dedupe by id, and cap.
    state = applyAtlassianEvent(state, event('atlassian/review', { op: 'finding', reviewId: 'r1', finding: finding('f1') }))
    state = applyAtlassianEvent(state, event('atlassian/review', { op: 'finding', reviewId: 'r1', finding: finding('f1', { title: 'again' }) }))
    state = applyAtlassianEvent(state, event('atlassian/review', { op: 'finding', reviewId: 'r1', finding: finding('f2', { overlaps: [5] }) }))
    expect(state.reviews.r1?.findings.map(item => [item.id, item.title])).toEqual([['f1', 'again'], ['f2', 'Finding f2']])
    expect(state.reviews.r1?.findings[1]?.overlaps).toEqual([5])
    const missing = applyAtlassianEvent(state, event('atlassian/review', { op: 'finding', reviewId: 'nope', finding: finding('f9') }))
    expect(missing.reviews).toEqual(state.reviews)
    // Posted with and without url, dismiss.
    state = applyAtlassianEvent(state, event('atlassian/review', { op: 'posted', reviewId: 'r1', findingId: 'f1', commentId: 7, url: 'http://b/c/7', mode: 'inline', at: 11 }))
    state = applyAtlassianEvent(state, event('atlassian/review', { op: 'posted', reviewId: 'r1', findingId: 'f2', commentId: 8, mode: 'general', at: 12 }))
    state = applyAtlassianEvent(state, event('atlassian/review', { op: 'dismiss', reviewId: 'r1', findingId: 'f2' }))
    expect(state.reviews.r1?.findings[0]?.posted).toEqual({ commentId: 7, url: 'http://b/c/7', mode: 'inline', at: 11 })
    expect(state.reviews.r1?.findings[1]).toMatchObject({ posted: { commentId: 8, mode: 'general', at: 12 }, dismissed: true })
    expect(state.reviews.r1?.findings[1]?.posted).not.toHaveProperty('url')
    expect(applyAtlassianEvent(state, event('atlassian/review', { op: 'posted', reviewId: 'zz', findingId: 'f1', commentId: 1, mode: 'inline', at: 1 })).reviews).toEqual(state.reviews)
    expect(applyAtlassianEvent(state, event('atlassian/review', { op: 'dismiss', reviewId: 'zz', findingId: 'f1' })).reviews).toEqual(state.reviews)
    // A second start cancels the running one and takes over.
    state = applyAtlassianEvent(state, event('atlassian/review', { op: 'start', reviewId: 'r2', pr: { ...REVIEW_PR, id: 43 }, at: 20, existing: [] }))
    expect(state.reviews.r1).toMatchObject({ status: 'cancelled', completedAt: 20 })
    expect(state.activeReviewId).toBe('r2')
    // Completing r1 (already cancelled) does not touch the active id; completing r2 clears it.
    state = applyAtlassianEvent(state, event('atlassian/review', { op: 'complete', reviewId: 'r1', summary: 'late', verdict: 'comment', at: 21 }))
    expect(state.activeReviewId).toBe('r2')
    expect(state.reviews.r1).toMatchObject({ status: 'complete', summary: 'late', verdict: 'comment', completedAt: 21 })
    state = applyAtlassianEvent(state, event('atlassian/review', { op: 'complete', reviewId: 'r2', summary: 'done', verdict: 'approve', at: 22 }))
    expect(state.activeReviewId).toBeNull()
    expect(applyAtlassianEvent(state, event('atlassian/review', { op: 'complete', reviewId: 'zz', summary: 's', verdict: 'approve', at: 1 })).reviews).toEqual(state.reviews)
    // Cancel: unknown no-op, foreign review keeps the active id, own review clears it.
    state = applyAtlassianEvent(state, event('atlassian/review', { op: 'start', reviewId: 'r3', pr: REVIEW_PR, at: 30, existing: [] }))
    expect(applyAtlassianEvent(state, event('atlassian/review', { op: 'cancel', reviewId: 'zz', at: 1 })).reviews).toEqual(state.reviews)
    const foreignCancel = applyAtlassianEvent(state, event('atlassian/review', { op: 'cancel', reviewId: 'r1', at: 31 }))
    expect(foreignCancel.activeReviewId).toBe('r3')
    state = applyAtlassianEvent(state, event('atlassian/review', { op: 'cancel', reviewId: 'r3', at: 32 }))
    expect(state.activeReviewId).toBeNull()
    expect(state.reviews.r3).toMatchObject({ status: 'cancelled', completedAt: 32 })
    expect(atlassianProjectionSchema.parse(state)).toEqual(state)
  })

  it('caps findings per review', () => {
    let state = applyAtlassianEvent(emptyState(), event('atlassian/review', { op: 'start', reviewId: 'r', pr: REVIEW_PR, at: 1, existing: [] }))
    for (let index = 0; index < FINDING_LIMIT + 2; index += 1) {
      state = applyAtlassianEvent(state, event('atlassian/review', { op: 'finding', reviewId: 'r', finding: finding(`f-${String(index)}`) }))
    }
    expect(state.reviews.r?.findings).toHaveLength(FINDING_LIMIT)
  })

  it('addresses records', () => {
    expect(refOf(issue('A-1'))).toEqual({ kind: 'issue', key: 'A-1' })
    expect(refOf(page('9'))).toEqual({ kind: 'page', id: '9' })
    expect(refOf(pr(2) as EntityRecord)).toEqual({ kind: 'pr', key: 'PROJ/webapp#2' })
  })
})
