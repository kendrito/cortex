/**
 * PR review mode: the two model-facing tools that stream findings into the
 * session log (`atlassian_review_finding`, `atlassian_review_complete`) and
 * the instruction prompt `/pr-review` hands the agent.
 *
 * @module
 */

import type { Context } from '@cortex/cordis'
import type { Session } from '@cortex/session'
import { defineTool } from '@cortex/tools'
import { REVIEW_COMPLETE_TOOL, REVIEW_FINDING_TOOL } from './tools.ts'
import type {
  DiffSide, ExistingPrComment, FindingCategory, FindingSeverity, PrRef, ReviewFinding, ReviewVerdict,
} from './types.ts'

/** The running review as the tools see it. */
export interface ActiveReview {
  reviewId: string
  pr: PrRef
  findingCount: number
  /** Findings recorded so far (for duplicate detection). */
  findings: readonly Pick<ReviewFinding, 'id' | 'file' | 'line' | 'category'>[]
  /** Comments already on the pull request when the review started. */
  existing: readonly ExistingPrComment[]
}

/** Lines of distance within which an existing inline comment counts as covering a finding. */
export const OVERLAP_LINES = 3

/** Bound on existing comments quoted in the instruction prompt. */
const EXISTING_PROMPT_LIMIT = 40
/** Bound on quoted existing-comment text in the instruction prompt. */
const EXISTING_PROMPT_TEXT = 240

/** Path comparison tolerant of `./`, `a/`, `b/` prefixes. */
function samePath(a: string, b: string): boolean {
  const normalize = (path: string): string => path.replace(/^\.?\//, '').replace(/^[ab]\//, '')
  return normalize(a) === normalize(b)
}

/**
 * Existing comments anchored on the same file within {@link OVERLAP_LINES} of a line.
 * @param existing - comments on the PR when the review started.
 * @param file - finding file path.
 * @param line - finding line.
 * @returns the overlapping comments.
 */
export function overlappingComments(existing: readonly ExistingPrComment[], file: string, line: number): ExistingPrComment[] {
  return existing.filter(comment => comment.file !== undefined && comment.line !== undefined
    && samePath(comment.file, file) && Math.abs(comment.line - line) <= OVERLAP_LINES)
}

/** Host-side review facts the tools consult and mutate. */
export interface ReviewTracker {
  /**
   * The running review of a session, when any.
   * @param session - agent session.
   * @returns review id and PR address, or `undefined`.
   */
  active(session: Session): ActiveReview | undefined
  /**
   * Record one finding into the running review.
   * @param session - agent session.
   * @param reviewId - review id.
   * @param finding - complete finding.
   */
  finding(session: Session, reviewId: string, finding: ReviewFinding): void
  /**
   * Complete the running review.
   * @param session - agent session.
   * @param reviewId - review id.
   * @param summary - closing summary.
   * @param verdict - verdict.
   */
  complete(session: Session, reviewId: string, summary: string, verdict: ReviewVerdict): void
  /** Wall clock. */
  now(): number
  /**
   * Fresh finding id.
   * @returns an id unique within the process.
   */
  nextId(): string
}

const SEVERITIES: readonly FindingSeverity[] = ['critical', 'major', 'minor', 'nit']
const CATEGORIES: readonly FindingCategory[] = ['security', 'correctness', 'readability', 'performance', 'testing', 'style']
const SIDES: readonly DiffSide[] = ['ADDED', 'REMOVED', 'CONTEXT']
const VERDICTS: readonly ReviewVerdict[] = ['approve', 'request-changes', 'comment']

const NO_REVIEW = 'No pull request review is running in this session. Start one with the /pr-review command (or the Review button in the Atlassian panel) before recording findings.'

const FINDING_DESCRIPTION = 'Record one pull request review finding while a review started by /pr-review is running. '
  + 'Call it once per distinct problem, most important first. `file` is the path exactly as the diff names it; '
  + '`line` is the line number on the destination side for ADDED/CONTEXT lines or the source side for REMOVED lines; '
  + '`evidence` quotes the exact code the finding is about; `comment` is the review comment to post to Bitbucket (markdown, '
  + 'addressed to the author, concrete and actionable); `rationale` explains why the evidence proves the problem. '
  + 'A finding that repeats one you already recorded at the same file, line, and category is refused. A finding within '
  + `${String(OVERLAP_LINES)} lines of a comment that is already on the pull request is refused unless `
  + '`acknowledgeExisting: true` states that it is materially new. '
  + 'The user decides in the panel which findings are posted; never post review comments yourself.'

const COMPLETE_DESCRIPTION = 'Finish the running pull request review with a short summary and a verdict. Call it exactly once, '
  + 'after every finding is recorded, even when nothing was found.'

/** Canonical value of `atlassian_review_finding`. */
const FINDING_OUTPUT = {
  type: 'object',
  properties: {
    recorded: { type: 'boolean', required: true },
    findingId: { type: 'string' },
    count: { type: 'integer', required: true },
    message: { type: 'string', required: true },
  },
  additionalProperties: false,
} as const

/** Canonical value of `atlassian_review_complete`. */
const COMPLETE_OUTPUT = {
  type: 'object',
  properties: {
    completed: { type: 'boolean', required: true },
    findings: { type: 'integer', required: true },
    message: { type: 'string', required: true },
  },
  additionalProperties: false,
} as const

/** Both tools narrate through their `message` field. */
const renderMessage = (_args: unknown, value: { message: string }): [{ type: 'text'; text: string }] =>
  [{ type: 'text', text: value.message }]

/** Bounded string fields the tools accept. */
const FIELD_LIMITS = {
  file: 500, title: 200, comment: 6_000, evidence: 4_000, rationale: 3_000, suggestion: 6_000, summary: 6_000,
} as const

function clip(value: string, limit: number): string {
  return value.length > limit ? `${value.slice(0, limit)}…` : value
}

/** The running review of the caller's session, or the refusal text the tool answers with. */
function reviewContext(
  tracker: ReviewTracker, session: Session | undefined, tool: string,
): { session: Session; active: NonNullable<ReturnType<ReviewTracker['active']>> } | { refusal: string } {
  if (session === undefined) return { refusal: `${tool} requires an owning agent session.` }
  const active = tracker.active(session)
  return active === undefined ? { refusal: NO_REVIEW } : { session, active }
}

/**
 * Register the review tools on the given context.
 * @param ctx - context carrying `ctx.tools`.
 * @param tracker - review facts.
 * @returns disposer removing both tools.
 */
export function registerReviewTools(ctx: Context, tracker: ReviewTracker): () => void {
  const disposers: (() => void)[] = []
  disposers.push(ctx.tools.register(defineTool({
    name: REVIEW_FINDING_TOOL,
    description: FINDING_DESCRIPTION,
    parameters: {
      file: { type: 'string', required: true, description: 'File path exactly as the diff names it.' },
      line: { type: 'integer', required: true, description: 'Line number (destination side for ADDED/CONTEXT, source side for REMOVED).' },
      side: { type: 'string', enum: SIDES, description: 'Which side of the diff the line is on; defaults to ADDED.' },
      severity: { type: 'string', enum: SEVERITIES, required: true, description: 'critical = must fix before merge; major = should fix; minor = worth fixing; nit = optional polish.' },
      category: { type: 'string', enum: CATEGORIES, required: true },
      title: { type: 'string', required: true, description: 'One-line headline of the finding.' },
      comment: { type: 'string', required: true, description: 'Review comment to post (markdown, addressed to the author).' },
      evidence: { type: 'string', required: true, description: 'Verbatim code the finding is about.' },
      rationale: { type: 'string', required: true, description: 'Why the evidence proves the problem.' },
      suggestion: { type: 'string', description: 'Concrete replacement code or fix, when one exists.' },
      acknowledgeExisting: {
        type: 'boolean',
        description: 'Set true only when an existing pull request comment covers the same lines and this finding is materially new.',
      },
    },
    output: { schema: FINDING_OUTPUT, render: renderMessage },
    execute(args, exec) {
      const context = reviewContext(tracker, exec.agent?.session, REVIEW_FINDING_TOOL)
      if ('refusal' in context) return Promise.resolve({ recorded: false, count: 0, message: context.refusal })
      const { session, active } = context
      if (args.file.trim() === '' || args.title.trim() === '' || args.comment.trim() === '' || args.evidence.trim() === '') {
        return Promise.resolve({ recorded: false, count: active.findingCount, message: 'file, title, comment, and evidence must be non-empty.' })
      }
      if (args.line < 1) return Promise.resolve({ recorded: false, count: active.findingCount, message: 'line must be a positive line number.' })
      const file = args.file.trim()
      const duplicate = active.findings.find(item =>
        samePath(item.file, file) && item.line === args.line && item.category === args.category)
      if (duplicate !== undefined) {
        return Promise.resolve({
          recorded: false,
          count: active.findingCount,
          message: `Already recorded as finding ${duplicate.id} (${file}:${String(args.line)}, ${args.category}); do not repeat it.`,
        })
      }
      const overlaps = overlappingComments(active.existing, file, args.line)
      if (overlaps.length > 0 && args.acknowledgeExisting !== true) {
        /* v8 ignore start -- overlappingComments only returns comments with a file and a line */
        const quoted = overlaps.slice(0, 3).map(comment =>
          `#${String(comment.id)} by ${comment.author.name} at ${comment.file ?? ''}:${String(comment.line ?? 0)}: ${clip(comment.text, EXISTING_PROMPT_TEXT)}`)
        /* v8 ignore stop */
        return Promise.resolve({
          recorded: false,
          count: active.findingCount,
          message: `An existing pull request comment already covers ${file}:${String(args.line)} — ${quoted.join(' | ')}. `
            + 'Skip it, or call again with acknowledgeExisting: true only if your finding is materially new.',
        })
      }
      const finding: ReviewFinding = {
        id: tracker.nextId(),
        at: tracker.now(),
        file: clip(file, FIELD_LIMITS.file),
        line: args.line,
        side: args.side ?? 'ADDED',
        severity: args.severity,
        category: args.category,
        title: clip(args.title.trim(), FIELD_LIMITS.title),
        comment: clip(args.comment.trim(), FIELD_LIMITS.comment),
        evidence: clip(args.evidence, FIELD_LIMITS.evidence),
        rationale: clip(args.rationale.trim(), FIELD_LIMITS.rationale),
        ...args.suggestion === undefined || args.suggestion.trim() === '' ? {} : { suggestion: clip(args.suggestion, FIELD_LIMITS.suggestion) },
        ...overlaps.length === 0 ? {} : { overlaps: overlaps.map(comment => comment.id) },
      }
      tracker.finding(session, active.reviewId, finding)
      const count = active.findingCount + 1
      return Promise.resolve({
        recorded: true,
        findingId: finding.id,
        count,
        message: `Recorded finding ${String(count)}: [${finding.severity}/${finding.category}] ${finding.file}:${String(finding.line)} — ${finding.title}`,
      })
    },
    presentCall: args => ({
      card: 'generic',
      title: `Review finding: ${args.title}`,
      kind: 'other',
      locations: [{ path: args.file, line: args.line }],
    }),
  })))

  disposers.push(ctx.tools.register(defineTool({
    name: REVIEW_COMPLETE_TOOL,
    description: COMPLETE_DESCRIPTION,
    parameters: {
      summary: { type: 'string', required: true, description: 'Two to five sentences: what the change does, overall quality, and what must happen before merge.' },
      verdict: { type: 'string', enum: VERDICTS, required: true, description: 'approve = mergeable as is; request-changes = blocking findings exist; comment = observations only.' },
    },
    output: { schema: COMPLETE_OUTPUT, render: renderMessage },
    execute(args, exec) {
      const context = reviewContext(tracker, exec.agent?.session, REVIEW_COMPLETE_TOOL)
      if ('refusal' in context) return Promise.resolve({ completed: false, findings: 0, message: context.refusal })
      const { session, active } = context
      tracker.complete(session, active.reviewId, clip(args.summary.trim(), FIELD_LIMITS.summary), args.verdict)
      return Promise.resolve({
        completed: true,
        findings: active.findingCount,
        message: `Review complete (${args.verdict}) with ${String(active.findingCount)} finding(s). The user can now post selected findings to Bitbucket from the Atlassian panel.`,
      })
    },
    presentCall: args => ({ card: 'generic', title: `Review complete: ${args.verdict}`, kind: 'other' }),
  })))

  return () => {
    for (const dispose of disposers.reverse()) dispose()
  }
}

/**
 * The instruction prompt `/pr-review` queues for the agent.
 * @param pr - pull request under review.
 * @param bitbucketProjectFallback - default project when the PR address omits one.
 * @param focus - extra reviewer instructions from the user.
 * @param existing - comments already on the pull request when the review started.
 * @returns prompt text.
 */
export function reviewInstructions(
  pr: PrRef, bitbucketProjectFallback: string, focus?: string, existing: readonly ExistingPrComment[] = [],
): string {
  const project = pr.project === '' ? bitbucketProjectFallback : pr.project
  const existingLines = existing.slice(0, EXISTING_PROMPT_LIMIT).map((comment) => {
    const where = comment.file === undefined ? 'general' : `${comment.file}:${String(comment.line ?? 0)}`
    return `- #${String(comment.id)} ${comment.author.name} (${where}): ${clip(comment.text.replace(/\s+/g, ' '), EXISTING_PROMPT_TEXT)}`
  })
  const existingBlock = existing.length === 0
    ? ['There are no review comments on this pull request yet.']
    : [
      `Comments already on this pull request (${String(existing.length)}${existing.length > EXISTING_PROMPT_LIMIT ? `, first ${String(EXISTING_PROMPT_LIMIT)} shown` : ''}). Do not raise a point one of them already makes; build on it or skip it. A finding near one of these lines is refused unless you set acknowledgeExisting: true because it is materially new:`,
      ...existingLines,
    ]
  return [
    '<pr_review>',
    `Review Bitbucket pull request ${project}/${pr.repo}#${String(pr.id)} as a careful senior engineer.`,
    '',
    'Procedure:',
    `1. Fetch the pull request with mcp__bitbucket__bitbucket_get_pull_request_details (project "${project}", repository "${pr.repo}", prId ${String(pr.id)}) and its diff with mcp__bitbucket__bitbucket_get_pull_request_diff. Read the description and understand the intent before judging the code.`,
    '2. Read the diff hunk by hunk. When a change depends on surrounding code you cannot see, fetch the file at the source branch with mcp__bitbucket__bitbucket_get_file_content instead of guessing.',
    '3. For every distinct problem, call atlassian_review_finding immediately when you find it (do not batch them at the end). Look for: security issues (injection, auth/authz gaps, secrets, unsafe deserialization, path traversal), correctness bugs (wrong logic, unhandled errors, races, off-by-one, null/undefined paths, broken edge cases), things that will not work as intended, performance traps, missing or weak tests, and readability problems that hurt maintainability (misleading names, dead code, tangled control flow, missing invariants). Prefer fewer, well-evidenced findings over volume; skip pure formatting.',
    '4. Each finding must name the file path exactly as the diff does, the exact line number (destination side for added/context lines, source side for removed lines), quote the code as evidence, explain the rationale, and propose a concrete fix in the comment. Do not report problems in code the pull request does not touch.',
    '5. When done, call atlassian_review_complete with a short summary and a verdict (approve / request-changes / comment). Mention which existing comments you agree with or consider resolved.',
    '',
    ...existingBlock,
    '',
    'Never post comments, approve, merge, or decline the pull request yourself: the user posts selected findings from the Atlassian panel.',
    ...focus === undefined || focus.trim() === '' ? [] : ['', `Additional reviewer instructions from the user: ${focus.trim()}`],
    '</pr_review>',
  ].join('\n')
}
