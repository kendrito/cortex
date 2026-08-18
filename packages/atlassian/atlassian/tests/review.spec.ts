import { afterEach, describe, expect, it, vi } from 'vitest'
import { Context } from '@cortex/cordis'
import type { Agent } from '@cortex/agent'
import { CallId } from '@cortex/llm'
import SessionStore, { SessionId } from '@cortex/session'
import SystemPrompt from '@cortex/system-prompt'
import ToolRuntime from '@cortex/tools'
import { OVERLAP_LINES, overlappingComments, registerReviewTools, reviewInstructions, type ActiveReview, type ReviewTracker } from '../src/review.ts'
import { REVIEW_COMPLETE_TOOL, REVIEW_FINDING_TOOL } from '../src/tools.ts'
import { containing } from './fixtures.ts'
import type { ExistingPrComment, ReviewFinding } from '../src/types.ts'

const signal = new AbortController().signal
const PR = { project: 'PROJ', repo: 'webapp', id: 42 }

interface Bench {
  ctx: Context
  agent: Agent
  tracker: ReviewTracker & { findings: unknown[]; completions: unknown[]; activeReview: ActiveReview | undefined }
  dispose: () => void
}

function existing(id: number, extra: Partial<ExistingPrComment> = {}): ExistingPrComment {
  return { id, author: { name: `Reviewer ${String(id)}` }, text: `Comment ${String(id)}`, file: 'src/a.ts', line: 3, side: 'ADDED', replies: 0, ...extra }
}

/** The comment without some optional anchor fields, without writing explicit `undefined`s. */
function omit(comment: ExistingPrComment, keys: ('file' | 'line' | 'side')[]): ExistingPrComment {
  const kept = Object.entries(comment).filter(([key]) => !(keys as string[]).includes(key))
  return Object.fromEntries(kept) as unknown as ExistingPrComment
}

function recordedFinding(id: string, extra: Partial<ReviewFinding> = {}): ReviewFinding {
  return {
    id, at: 1, file: 'src/a.ts', line: 3, side: 'ADDED', severity: 'major', category: 'security', title: 'T', comment: 'c',
    evidence: 'e', rationale: 'r', ...extra,
  }
}

async function bench(): Promise<Bench> {
  const ctx = new Context()
  await ctx.plugin(SessionStore)
  await ctx.plugin(SystemPrompt)
  await ctx.plugin(ToolRuntime)
  const session = ctx.sessions.create(SessionId('review'))
  const agent = { id: session.id, session } as unknown as Agent
  let ticks = 0
  const tracker: Bench['tracker'] = {
    findings: [],
    completions: [],
    activeReview: { reviewId: 'r1', pr: PR, findingCount: 2, findings: [], existing: [] },
    active: () => tracker.activeReview,
    finding: (_session, reviewId, finding) => { tracker.findings.push({ reviewId, finding }) },
    complete: (_session, reviewId, summary, verdict) => { tracker.completions.push({ reviewId, summary, verdict }) },
    now: () => 1000,
    nextId: () => `f-${String(++ticks)}`,
  }
  const dispose = registerReviewTools(ctx, tracker)
  return { ctx, agent, tracker, dispose }
}

let live: Bench | undefined
afterEach(async () => {
  await live?.ctx.fiber.dispose()
  live = undefined
})

function run(ctx: Context, name: string, args: unknown, agent?: Agent) {
  return ctx.tools.execute({ callId: CallId('c1'), name, arguments: args, signal, ...agent === undefined ? {} : { agent } })
}

const FINDING_ARGS = {
  file: ' src/a.ts ', line: 3, severity: 'major', category: 'security', title: ' Title ', comment: ' Comment ',
  evidence: 'const x', rationale: ' because ',
}

describe('review tools', () => {
  it('registers both tools and removes them on dispose', async () => {
    live = await bench()
    expect(live.ctx.tools.get(REVIEW_FINDING_TOOL)).toBeDefined()
    expect(live.ctx.tools.get(REVIEW_COMPLETE_TOOL)).toBeDefined()
    live.dispose()
    expect(live.ctx.tools.get(REVIEW_FINDING_TOOL)).toBeUndefined()
    expect(live.ctx.tools.get(REVIEW_COMPLETE_TOOL)).toBeUndefined()
  })

  it('refuses without an owning session or a running review', async () => {
    live = await bench()
    const noAgent = await run(live.ctx, REVIEW_FINDING_TOOL, FINDING_ARGS)
    expect(noAgent.value).toMatchObject({ recorded: false, count: 0, message: containing('requires an owning agent session') })
    live.tracker.activeReview = undefined
    const noReview = await run(live.ctx, REVIEW_FINDING_TOOL, FINDING_ARGS, live.agent)
    expect(noReview.value).toMatchObject({ recorded: false, message: containing('No pull request review is running') })
    const completeNoAgent = await run(live.ctx, REVIEW_COMPLETE_TOOL, { summary: 's', verdict: 'approve' })
    expect(completeNoAgent.value).toMatchObject({ completed: false, findings: 0 })
    const completeNoReview = await run(live.ctx, REVIEW_COMPLETE_TOOL, { summary: 's', verdict: 'approve' }, live.agent)
    expect(completeNoReview.value).toMatchObject({ completed: false, message: containing('No pull request review is running') })
    expect(live.tracker.findings).toEqual([])
    expect(live.tracker.completions).toEqual([])
  })

  it('validates the finding fields', async () => {
    live = await bench()
    const empty = await run(live.ctx, REVIEW_FINDING_TOOL, { ...FINDING_ARGS, title: '  ' }, live.agent)
    expect(empty.value).toMatchObject({ recorded: false, count: 2, message: 'file, title, comment, and evidence must be non-empty.' })
    const line = await run(live.ctx, REVIEW_FINDING_TOOL, { ...FINDING_ARGS, line: 0 }, live.agent)
    expect(line.value).toMatchObject({ recorded: false, count: 2, message: 'line must be a positive line number.' })
    expect(live.tracker.findings).toEqual([])
  })

  it('records findings with defaults, trimming, and bounds', async () => {
    live = await bench()
    const first = await run(live.ctx, REVIEW_FINDING_TOOL, FINDING_ARGS, live.agent)
    expect(first.value).toEqual({
      recorded: true,
      findingId: 'f-1',
      count: 3,
      message: 'Recorded finding 3: [major/security] src/a.ts:3 — Title',
    })
    expect(live.tracker.findings[0]).toEqual({
      reviewId: 'r1',
      finding: {
        id: 'f-1', at: 1000, file: 'src/a.ts', line: 3, side: 'ADDED', severity: 'major', category: 'security',
        title: 'Title', comment: 'Comment', evidence: 'const x', rationale: 'because',
      },
    })
    const second = await run(live.ctx, REVIEW_FINDING_TOOL, {
      ...FINDING_ARGS, side: 'REMOVED', suggestion: 'x'.repeat(7000), comment: 'c'.repeat(7000),
    }, live.agent)
    expect(second.value).toMatchObject({ recorded: true, findingId: 'f-2' })
    const recorded = live.tracker.findings[1] as { finding: { side: string; suggestion: string; comment: string } }
    expect(recorded.finding.side).toBe('REMOVED')
    expect(recorded.finding.suggestion).toHaveLength(6001)
    expect(recorded.finding.comment.endsWith('…')).toBe(true)
    const blankSuggestion = await run(live.ctx, REVIEW_FINDING_TOOL, { ...FINDING_ARGS, suggestion: '  ' }, live.agent)
    expect(blankSuggestion.value).toMatchObject({ recorded: true })
    expect(live.tracker.findings[2]).not.toHaveProperty('finding.suggestion')
    // The pending card names the file and line.
    const definition = live.ctx.tools.get(REVIEW_FINDING_TOOL)
    expect(definition?.presentCall?.({ ...FINDING_ARGS, file: 'src/a.ts', title: 'T' })).toEqual({
      card: 'generic', title: 'Review finding: T', kind: 'other', locations: [{ path: 'src/a.ts', line: 3 }],
    })
  })

  it('refuses duplicates of recorded findings and unacknowledged overlaps with existing comments', async () => {
    live = await bench()
    live.tracker.activeReview = {
      reviewId: 'r1',
      pr: PR,
      findingCount: 1,
      findings: [recordedFinding('f-old', { file: './src/a.ts' })],
      existing: [existing(7, { text: 'x'.repeat(300) }), existing(8, { file: 'b/src/a.ts', line: 3 + OVERLAP_LINES }), existing(9, { file: 'src/other.ts' }),
        existing(10, { file: 'a/src/a.ts', line: 3 - OVERLAP_LINES }), existing(11, { line: 3 + OVERLAP_LINES + 1 })],
    }
    // Same file (prefix-insensitive), line, and category as a recorded finding.
    const duplicate = await run(live.ctx, REVIEW_FINDING_TOOL, FINDING_ARGS, live.agent)
    expect(duplicate.value).toEqual({
      recorded: false, count: 1, message: 'Already recorded as finding f-old (src/a.ts:3, security); do not repeat it.',
    })
    // A different category on the same line is not a duplicate, but overlaps existing comments #7, #8, and #10 (first 3 quoted).
    const overlap = await run(live.ctx, REVIEW_FINDING_TOOL, { ...FINDING_ARGS, category: 'correctness' }, live.agent)
    const message = (overlap.value as { message: string }).message
    expect(overlap.value).toMatchObject({ recorded: false, count: 1 })
    expect(message).toContain('An existing pull request comment already covers src/a.ts:3 — #7 by Reviewer 7 at src/a.ts:3: ')
    expect(message).toContain(`${'x'.repeat(240)}… | #8 by Reviewer 8 at b/src/a.ts:6: Comment 8 | #10 by Reviewer 10 at a/src/a.ts:0: Comment 10. `)
    expect(message).toContain('call again with acknowledgeExisting: true')
    expect(message).not.toContain('#11')
    expect(live.tracker.findings).toEqual([])
    // Acknowledged, the finding records with the overlapping comment ids.
    const acknowledged = await run(live.ctx, REVIEW_FINDING_TOOL, { ...FINDING_ARGS, category: 'correctness', acknowledgeExisting: true }, live.agent)
    expect(acknowledged.value).toMatchObject({ recorded: true, findingId: 'f-1', count: 2 })
    expect(live.tracker.findings[0]).toMatchObject({ finding: { id: 'f-1', category: 'correctness', overlaps: [7, 8, 10] } })
    // A comment without an anchor never overlaps.
    expect(overlappingComments([omit(existing(1), ['file', 'line'])], 'src/a.ts', 3)).toEqual([])
    expect(overlappingComments([omit(existing(1), ['line'])], 'src/a.ts', 3)).toEqual([])
    expect(overlappingComments([existing(2)], 'src/a.ts', 3)).toHaveLength(1)
  })

  it('completes the review', async () => {
    live = await bench()
    const result = await run(live.ctx, REVIEW_COMPLETE_TOOL, { summary: ' All good ', verdict: 'approve' }, live.agent)
    expect(result.value).toEqual({
      completed: true,
      findings: 2,
      message: 'Review complete (approve) with 2 finding(s). The user can now post selected findings to Bitbucket from the Atlassian panel.',
    })
    expect(live.tracker.completions).toEqual([{ reviewId: 'r1', summary: 'All good', verdict: 'approve' }])
    expect(live.ctx.tools.get(REVIEW_COMPLETE_TOOL)?.presentCall?.({ summary: 's', verdict: 'comment' }))
      .toEqual({ card: 'generic', title: 'Review complete: comment', kind: 'other' })
    // The canonical value narrates through its message.
    expect(result.content).toEqual([{ type: 'text', text: containing('Review complete (approve)') }])
  })
})

describe('reviewInstructions', () => {
  it('names the pull request, the procedure, and optional focus', () => {
    const text = reviewInstructions(PR, 'FALLBACK', 'watch the auth flow')
    expect(text).toContain('<pr_review>')
    expect(text).toContain('PROJ/webapp#42')
    expect(text).toContain('project "PROJ", repository "webapp", prId 42')
    expect(text).toContain('Additional reviewer instructions from the user: watch the auth flow')
    expect(text).toContain('</pr_review>')
    const fallback = reviewInstructions({ project: '', repo: 'webapp', id: 1 }, 'FALLBACK')
    expect(fallback).toContain('FALLBACK/webapp#1')
    expect(fallback).not.toContain('Additional reviewer instructions')
    expect(reviewInstructions(PR, '', '   ')).not.toContain('Additional reviewer instructions')
    expect(vi.isMockFunction(reviewInstructions)).toBe(false)
    expect(text).toContain('There are no review comments on this pull request yet.')
    expect(text).not.toContain('Comments already on this pull request')
  })

  it('quotes existing comments, bounded, and marks general ones', () => {
    const few = reviewInstructions(PR, '', undefined, [
      existing(1, { text: 'Line one\n\n  spaced   out' }),
      omit(existing(2, { author: { name: 'Bot' } }), ['file', 'line']),
      omit(existing(3), ['line']),
    ])
    expect(few).toContain('Comments already on this pull request (3). Do not raise a point one of them already makes')
    expect(few).toContain('- #1 Reviewer 1 (src/a.ts:3): Line one spaced out')
    expect(few).toContain('- #2 Bot (general): Comment 2')
    expect(few).toContain('- #3 Reviewer 3 (src/a.ts:0): Comment 3')
    expect(few).not.toContain('There are no review comments')
    const many = reviewInstructions(PR, '', undefined, Array.from({ length: 45 }, (_, index) => existing(index + 1, { text: 'y'.repeat(500) })))
    expect(many).toContain('Comments already on this pull request (45, first 40 shown).')
    expect(many).toContain('- #40 Reviewer 40 (src/a.ts:3): ')
    expect(many).not.toContain('- #41 ')
    expect(many).toContain(`${'y'.repeat(240)}…`)
  })
})
