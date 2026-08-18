/**
 * Bitbucket Data Center REST 1.0 adapter: pull request records, the review
 * inbox and per-repository listings, the structured diff, and comment posting
 * with inline anchors.
 *
 * @module
 */

import { BODY_LIMIT, bound } from '../markup.ts'
import type {
  DiffContextLine, DiffSide, ExistingPrComment, PersonRef, PrRecord, PrRef, PrReviewer, PrSummary,
} from '../types.ts'
import { query, requestJson, type FetchLike, type RestTarget } from './http.ts'
import { dict, isoOf, list, own, text, type Dict } from './json.ts'

/** Rows requested per listing page. */
const LIST_LIMIT = 50
/** Activity pages read when collecting existing comments. */
const ACTIVITY_PAGES = 4
/** Bound applied to one existing comment's text. */
const EXISTING_TEXT_LIMIT = 600

/**
 * Person from a Bitbucket user object.
 * @param value - Bitbucket `user` JSON.
 * @returns the person, or `undefined` when absent.
 */
export function bitbucketPerson(value: unknown): PersonRef | undefined {
  const user = dict(value)
  const name = text(user?.displayName) ?? text(user?.name) ?? text(user?.slug)
  if (name === undefined) return undefined
  const id = text(user?.slug) ?? text(user?.name)
  const avatar = text(user?.avatarUrl)
  return { name, ...id === undefined ? {} : { id }, ...avatar === undefined ? {} : { avatar } }
}

function reviewer(value: unknown, role: PrReviewer['role']): PrReviewer | undefined {
  const participant = dict(value)
  const user = bitbucketPerson(participant?.user)
  if (user === undefined) return undefined
  const status = text(participant?.status)
  return {
    user,
    role,
    status: status === 'APPROVED' || status === 'NEEDS_WORK' ? status : 'UNAPPROVED',
  }
}

/**
 * Stable map key of one pull request.
 * @param ref - pull request address.
 * @returns `PROJECT/repo#id`.
 */
export function prKey(ref: PrRef): string {
  return `${ref.project.toUpperCase()}/${ref.repo}#${String(ref.id)}`
}

/**
 * Parse a `PROJECT/repo#id` key or a Bitbucket pull request URL.
 * @param input - key or URL.
 * @returns the address, or `undefined` when unrecognizable.
 */
export function parsePrRef(input: string): PrRef | undefined {
  const trimmed = input.trim()
  const key = /^([A-Za-z0-9_~.-]+)\/([A-Za-z0-9_.-]+)#(\d+)$/.exec(trimmed)
  /* v8 ignore next -- matched capture groups are always defined */
  if (key !== null) return { project: key[1] ?? '', repo: key[2] ?? '', id: Number(key[3]) }
  const url = /\/projects\/([A-Za-z0-9_~.-]+)\/repos\/([A-Za-z0-9_.-]+)\/pull-requests\/(\d+)/.exec(trimmed)
  /* v8 ignore next -- matched capture groups are always defined */
  if (url !== null) return { project: url[1] ?? '', repo: url[2] ?? '', id: Number(url[3]) }
  return undefined
}

/** Address of one PR from its JSON `toRef.repository`. */
function refOf(json: Dict): PrRef | undefined {
  const repository = dict(dict(json.toRef)?.repository)
  const repo = text(repository?.slug)
  const project = text(dict(repository?.project)?.key)
  const id = typeof json.id === 'number' ? json.id : undefined
  if (repo === undefined || project === undefined || id === undefined) return undefined
  return { project, repo, id }
}

function stateOf(value: unknown): PrRecord['state'] {
  return value === 'MERGED' || value === 'DECLINED' ? value : 'OPEN'
}

/**
 * Convert one Bitbucket pull request JSON into the compact record.
 * @param baseUrl - Bitbucket base URL.
 * @param json - pull request JSON.
 * @param fetchedAt - wall-clock ms.
 * @param fallback - address used when the JSON omits `toRef.repository`.
 * @returns the record.
 */
export function prRecordFromRest(baseUrl: string, json: unknown, fetchedAt: number, fallback?: PrRef): PrRecord {
  const pr = dict(json) ?? {}
  const ref = refOf(pr) ?? fallback ?? { project: '', repo: '', id: typeof pr.id === 'number' ? pr.id : 0 }
  const from = dict(pr.fromRef)
  const to = dict(pr.toRef)
  const created = isoOf(pr.createdDate)
  const updated = isoOf(pr.updatedDate)
  const fromCommit = text(from?.latestCommit)
  const toCommit = text(to?.latestCommit)
  const reviewers = [
    ...list(pr.reviewers).flatMap(item => reviewer(item, 'REVIEWER') ?? []),
    ...list(pr.participants).flatMap(item => reviewer(item, 'PARTICIPANT') ?? []),
  ]
  return {
    kind: 'pr',
    ref,
    key: prKey(ref),
    title: text(pr.title) ?? '',
    description: bound(text(pr.description) ?? '', BODY_LIMIT).text,
    state: stateOf(pr.state),
    author: bitbucketPerson(dict(pr.author)?.user) ?? { name: '' },
    reviewers,
    from: { branch: text(from?.displayId) ?? '', ...fromCommit === undefined ? {} : { commit: fromCommit } },
    to: { branch: text(to?.displayId) ?? '', ...toCommit === undefined ? {} : { commit: toCommit } },
    ...created === undefined ? {} : { created },
    ...updated === undefined ? {} : { updated },
    version: typeof pr.version === 'number' ? pr.version : 0,
    url: prUrl(baseUrl, ref),
    fetchedAt,
  }
}

/**
 * Browse URL of one pull request.
 * @param baseUrl - Bitbucket base URL.
 * @param ref - pull request address.
 * @returns the overview URL.
 */
export function prUrl(baseUrl: string, ref: PrRef): string {
  return `${baseUrl}/projects/${encodeURIComponent(ref.project)}/repos/${encodeURIComponent(ref.repo)}/pull-requests/${String(ref.id)}/overview`
}

/**
 * Convert one listing row into a picker summary.
 * @param baseUrl - Bitbucket base URL.
 * @param json - pull request JSON.
 * @param role - authenticated user's role when the row came from the dashboard.
 * @returns the summary, or `undefined` when the row lacks an address.
 */
export function prSummaryFromRest(baseUrl: string, json: unknown, role?: PrSummary['role']): PrSummary | undefined {
  const pr = dict(json)
  if (pr === undefined) return undefined
  const ref = refOf(pr)
  if (ref === undefined) return undefined
  const reviewers = list(pr.reviewers).flatMap(item => reviewer(item, 'REVIEWER') ?? [])
  const updated = isoOf(pr.updatedDate)
  return {
    ref,
    key: prKey(ref),
    title: text(pr.title) ?? '',
    author: bitbucketPerson(dict(pr.author)?.user) ?? { name: '' },
    state: stateOf(pr.state),
    ...updated === undefined ? {} : { updated },
    approvals: reviewers.filter(item => item.status === 'APPROVED').length,
    reviewers: reviewers.length,
    url: prUrl(baseUrl, ref),
    ...role === undefined ? {} : { role },
  }
}

// ---- Diff ---------------------------------------------------------------------

/** One hunk of one file of a normalized diff. */
export interface DiffHunkLines {
  sourceLine: number
  sourceSpan: number
  destinationLine: number
  destinationSpan: number
  lines: DiffContextLine[]
}

/** One file of a normalized diff. */
export interface DiffFile {
  /** Destination path (source path for a deletion). */
  path: string
  /** Source path when it differs (rename/move). */
  oldPath?: string
  binary: boolean
  truncated: boolean
  hunks: DiffHunkLines[]
}

/** Whole normalized diff. */
export interface NormalizedDiff {
  files: DiffFile[]
  truncated: boolean
}

function sideOf(value: unknown): DiffSide {
  return value === 'ADDED' || value === 'REMOVED' ? value : 'CONTEXT'
}

/**
 * Normalize the Bitbucket structured diff JSON.
 * @param json - `GET .../pull-requests/{id}/diff` response.
 * @returns file/hunk/line structure with both line numberings.
 */
export function normalizeDiff(json: unknown): NormalizedDiff {
  const root = dict(json) ?? {}
  const files: DiffFile[] = []
  for (const item of list(root.diffs)) {
    const file = dict(item)
    if (file === undefined) continue
    const source = text(own(dict(file.source), 'toString'))
    const destination = text(own(dict(file.destination), 'toString'))
    const path = destination ?? source ?? ''
    const hunks: DiffHunkLines[] = []
    for (const hunkItem of list(file.hunks)) {
      const hunk = dict(hunkItem)
      if (hunk === undefined) continue
      const lines: DiffContextLine[] = []
      for (const segmentItem of list(hunk.segments)) {
        const segment = dict(segmentItem)
        const type = sideOf(segment?.type)
        for (const lineItem of list(segment?.lines)) {
          const line = dict(lineItem)
          if (line === undefined) continue
          const sourceNo = typeof line.source === 'number' ? line.source : undefined
          const destinationNo = typeof line.destination === 'number' ? line.destination : undefined
          lines.push({
            type,
            ...type === 'ADDED' || sourceNo === undefined ? {} : { source: sourceNo },
            ...type === 'REMOVED' || destinationNo === undefined ? {} : { destination: destinationNo },
            text: typeof line.line === 'string' ? line.line : '',
          })
        }
      }
      hunks.push({
        sourceLine: typeof hunk.sourceLine === 'number' ? hunk.sourceLine : 0,
        sourceSpan: typeof hunk.sourceSpan === 'number' ? hunk.sourceSpan : 0,
        destinationLine: typeof hunk.destinationLine === 'number' ? hunk.destinationLine : 0,
        destinationSpan: typeof hunk.destinationSpan === 'number' ? hunk.destinationSpan : 0,
        lines,
      })
    }
    files.push({
      path,
      ...source !== undefined && destination !== undefined && source !== destination ? { oldPath: source } : {},
      binary: file.binary === true,
      truncated: file.truncated === true,
      hunks,
    })
  }
  return { files, truncated: root.truncated === true }
}

/** Resolved inline anchor for one finding. */
export interface ResolvedAnchor {
  path: string
  line: number
  lineType: DiffSide
  fileType: 'FROM' | 'TO'
}

/** Normalize a path for matching (drop leading `./` and `/`). */
function normalizePath(path: string): string {
  return path.replace(/^\.?\//, '')
}

/**
 * Locate one file of the diff by path, tolerating a leading `./`, `a/`, `b/`,
 * or a bare basename when unique.
 * @param diff - normalized diff.
 * @param path - path the model named.
 * @returns the file, or `undefined`.
 */
export function findDiffFile(diff: NormalizedDiff, path: string): DiffFile | undefined {
  const wanted = normalizePath(path).replace(/^[ab]\//, '')
  const exact = diff.files.find(file => normalizePath(file.path) === wanted
    || (file.oldPath !== undefined && normalizePath(file.oldPath) === wanted))
  if (exact !== undefined) return exact
  const suffix = diff.files.filter(file => normalizePath(file.path).endsWith(`/${wanted}`) || normalizePath(file.path) === wanted)
  return suffix.length === 1 ? suffix[0] : undefined
}

/**
 * Resolve the Bitbucket anchor for a finding line. The model names a line on
 * one side; the diff decides which segment actually holds it, so a wrong
 * `side` still lands on the right line when the number exists on that side.
 * @param diff - normalized diff.
 * @param path - file path the finding names.
 * @param line - line number on `side`.
 * @param side - which numbering `line` uses.
 * @returns the anchor, or `undefined` when the line is not part of the diff.
 */
export function resolveAnchor(diff: NormalizedDiff, path: string, line: number, side: DiffSide): ResolvedAnchor | undefined {
  const file = findDiffFile(diff, path)
  if (file === undefined) return undefined
  const lines = file.hunks.flatMap(hunk => hunk.lines)
  const bySide = (wanted: DiffSide): DiffContextLine | undefined => wanted === 'REMOVED'
    ? lines.find(candidate => candidate.type === 'REMOVED' && candidate.source === line)
    : lines.find(candidate => candidate.type !== 'REMOVED' && candidate.destination === line)
  const hit = bySide(side) ?? bySide(side === 'REMOVED' ? 'ADDED' : 'REMOVED')
  if (hit === undefined) return undefined
  /* v8 ignore next 3 -- a hit is only found by matching that very line number */
  return hit.type === 'REMOVED'
    ? { path: file.path, line: hit.source ?? line, lineType: 'REMOVED', fileType: 'FROM' }
    : { path: file.path, line: hit.destination ?? line, lineType: hit.type, fileType: 'TO' }
}

/**
 * Lines around one anchor for the evidence view.
 * @param diff - normalized diff.
 * @param path - file path.
 * @param line - anchored line number.
 * @param side - which numbering `line` uses.
 * @param context - lines kept on each side.
 * @returns the window with the anchored line marked, and whether it was found.
 */
export function diffWindow(
  diff: NormalizedDiff, path: string, line: number, side: DiffSide, context: number,
): { file: string; lines: DiffContextLine[]; found: boolean } {
  const file = findDiffFile(diff, path)
  if (file === undefined) return { file: path, lines: [], found: false }
  const anchor = resolveAnchor(diff, path, line, side)
  for (const hunk of file.hunks) {
    const index = anchor === undefined
      ? -1
      : hunk.lines.findIndex(candidate => anchor.fileType === 'FROM'
        ? candidate.type === 'REMOVED' && candidate.source === anchor.line
        : candidate.type !== 'REMOVED' && candidate.destination === anchor.line)
    if (index === -1) continue
    const start = Math.max(0, index - context)
    const end = Math.min(hunk.lines.length, index + context + 1)
    return {
      file: file.path,
      lines: hunk.lines.slice(start, end).map((candidate, offset) => start + offset === index ? { ...candidate, anchor: true } : candidate),
      found: true,
    }
  }
  const first = file.hunks[0]
  return { file: file.path, lines: first === undefined ? [] : first.lines.slice(0, context * 2 + 1), found: false }
}

// ---- Existing comments -----------------------------------------------------------

/** Flatten one comment thread (comment + nested replies) into existing-comment rows. */
function flattenComment(value: unknown, into: ExistingPrComment[], anchorOf: unknown, depth: number): void {
  const comment = dict(value)
  if (comment === undefined || depth > 6) return
  const id = typeof comment.id === 'number' ? comment.id : undefined
  const rawText = text(comment.text)
  if (id === undefined || rawText === undefined) return
  const anchor = dict(anchorOf)
  const file = text(anchor?.path)
  const line = typeof anchor?.line === 'number' ? anchor.line : undefined
  const sideRaw = text(anchor?.lineType)
  const created = isoOf(comment.createdDate)
  const replies = list(comment.comments)
  into.push({
    id,
    author: bitbucketPerson(comment.author) ?? { name: '' },
    text: rawText.length > EXISTING_TEXT_LIMIT ? `${rawText.slice(0, EXISTING_TEXT_LIMIT)}…` : rawText,
    ...file === undefined ? {} : { file },
    ...line === undefined ? {} : { line },
    ...sideRaw === 'ADDED' || sideRaw === 'REMOVED' || sideRaw === 'CONTEXT' ? { side: sideRaw } : {},
    ...created === undefined ? {} : { created },
    replies: replies.length,
  })
  for (const reply of replies) flattenComment(reply, into, anchorOf, depth + 1)
}

/**
 * Existing comments from one activities page: `COMMENTED` activities with
 * their thread flattened, replies inheriting the root anchor.
 * @param json - `GET .../activities` page.
 * @returns rows in activity order.
 */
export function existingCommentsFromActivities(json: unknown): ExistingPrComment[] {
  const rows: ExistingPrComment[] = []
  for (const item of list(dict(json)?.values)) {
    const activity = dict(item)
    if (activity === undefined || activity.action !== 'COMMENTED') continue
    if (activity.commentAction !== undefined && activity.commentAction !== 'ADDED') continue
    flattenComment(activity.comment, rows, activity.commentAnchor ?? dict(activity.comment)?.anchor, 0)
  }
  return rows
}

// ---- Adapter -----------------------------------------------------------------------

/** Bitbucket REST adapter over one target. */
export class BitbucketRest {
  /**
   * @param fetchImpl - fetch implementation.
   * @param target - Bitbucket base URL and token.
   * @param now - clock.
   */
  constructor(
    private readonly fetchImpl: FetchLike,
    private readonly target: RestTarget,
    /* v8 ignore start -- production default; tests inject the clock */
    private readonly now: () => number = () => Date.now(),
    /* v8 ignore stop */
  ) {}

  private prPath(ref: PrRef): string {
    return `/rest/api/1.0/projects/${encodeURIComponent(ref.project)}/repos/${encodeURIComponent(ref.repo)}/pull-requests/${String(ref.id)}`
  }

  /**
   * Fetch one pull request.
   * @param ref - pull request address.
   * @param signal - optional cancellation.
   * @returns the compact record.
   */
  async getPullRequest(ref: PrRef, signal?: AbortSignal): Promise<PrRecord> {
    const json = await requestJson(this.fetchImpl, this.target, 'GET', this.prPath(ref), undefined, signal)
    return prRecordFromRest(this.target.baseUrl, json, this.now(), ref)
  }

  /**
   * List pull requests of one repository.
   * @param project - project key.
   * @param repo - repository slug.
   * @param state - state filter.
   * @param signal - optional cancellation.
   * @returns picker rows, newest first.
   */
  async listPullRequests(
    project: string, repo: string, state: 'OPEN' | 'MERGED' | 'DECLINED' | 'ALL', signal?: AbortSignal,
  ): Promise<PrSummary[]> {
    const json = dict(await requestJson(
      this.fetchImpl, this.target, 'GET',
      `/rest/api/1.0/projects/${encodeURIComponent(project)}/repos/${encodeURIComponent(repo)}/pull-requests${query({ state, limit: LIST_LIMIT, order: 'NEWEST' })}`,
      undefined, signal,
    ))
    return list(json?.values).flatMap(item => prSummaryFromRest(this.target.baseUrl, item) ?? [])
  }

  /**
   * The authenticated user's review inbox: pull requests they review or authored.
   * @param state - state filter.
   * @param signal - optional cancellation.
   * @returns picker rows, reviewer rows first.
   */
  async inbox(state: 'OPEN' | 'MERGED' | 'DECLINED' | 'ALL', signal?: AbortSignal): Promise<PrSummary[]> {
    const rows: PrSummary[] = []
    for (const role of ['REVIEWER', 'AUTHOR'] as const) {
      const json = dict(await requestJson(
        this.fetchImpl, this.target, 'GET',
        `/rest/api/1.0/dashboard/pull-requests${query({ state, role, limit: LIST_LIMIT, order: 'NEWEST' })}`,
        undefined, signal,
      ))
      for (const item of list(json?.values)) {
        const summary = prSummaryFromRest(this.target.baseUrl, item, role)
        if (summary !== undefined && !rows.some(existing => existing.key === summary.key)) rows.push(summary)
      }
    }
    return rows
  }

  /**
   * Comments already on the pull request (root comments and replies, inline
   * anchors kept), oldest first.
   * @param ref - pull request address.
   * @param signal - optional cancellation.
   * @returns the existing comments, bounded by the activity pages read.
   */
  async getComments(ref: PrRef, signal?: AbortSignal): Promise<ExistingPrComment[]> {
    const rows: ExistingPrComment[] = []
    let start = 0
    for (let page = 0; page < ACTIVITY_PAGES; page += 1) {
      const json = dict(await requestJson(
        this.fetchImpl, this.target, 'GET',
        `${this.prPath(ref)}/activities${query({ limit: LIST_LIMIT, start })}`,
        undefined, signal,
      ))
      rows.push(...existingCommentsFromActivities(json))
      if (json?.isLastPage !== false || typeof json.nextPageStart !== 'number') break
      start = json.nextPageStart
    }
    const seen = new Set<number>()
    return rows.filter(row => (seen.has(row.id) ? false : (seen.add(row.id), true))).reverse()
  }

  /**
   * Fetch and normalize the pull request diff.
   * @param ref - pull request address.
   * @param signal - optional cancellation.
   * @returns the normalized diff.
   */
  async getDiff(ref: PrRef, signal?: AbortSignal): Promise<NormalizedDiff> {
    const json = await requestJson(
      this.fetchImpl, this.target, 'GET',
      `${this.prPath(ref)}/diff${query({ withComments: false, contextLines: 6 })}`,
      undefined, signal,
    )
    return normalizeDiff(json)
  }

  /**
   * Post one comment, inline when an anchor is given.
   * @param ref - pull request address.
   * @param body - comment markdown.
   * @param anchor - resolved inline anchor, or `undefined` for a general comment.
   * @param signal - optional cancellation.
   * @returns comment id and browse URL.
   */
  async postComment(
    ref: PrRef, body: string, anchor: ResolvedAnchor | undefined, signal?: AbortSignal,
  ): Promise<{ id: number; url: string }> {
    const payload = {
      text: body,
      ...anchor === undefined
        ? {}
        : { anchor: { path: anchor.path, line: anchor.line, lineType: anchor.lineType, fileType: anchor.fileType, diffType: 'EFFECTIVE' } },
    }
    const json = dict(await requestJson(this.fetchImpl, this.target, 'POST', `${this.prPath(ref)}/comments`, payload, signal))
    const id = typeof json?.id === 'number' ? json.id : 0
    return { id, url: `${prUrl(this.target.baseUrl, ref)}?commentId=${String(id)}` }
  }

  /**
   * Probe the token through the authenticated inbox counter.
   * @param signal - optional cancellation.
   * @returns the review inbox size.
   */
  async probe(signal?: AbortSignal): Promise<number> {
    const json = dict(await requestJson(this.fetchImpl, this.target, 'GET', '/rest/api/1.0/inbox/pull-requests/count', undefined, signal))
    return typeof json?.count === 'number' ? json.count : 0
  }
}
