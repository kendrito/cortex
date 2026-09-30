/**
 * Historical records retained solely for lossless restoration of saved sessions.
 * Cortex has no Atlassian service, MCP launcher, panel, or live event writer.
 * @module @cortex/session/historical-atlassian-events
 */

// ---- Entity records --------------------------------------------------------

/** One person as Jira/Confluence/Bitbucket present them. */
export interface PersonRef {
  /** Display name; the empty string when the service returned none. */
  name: string
  /** Login/slug/account id when known. */
  id?: string
  /** Avatar URL when the service exposes one. */
  avatar?: string
}

/** Jira status with its category color role. */
export interface IssueStatus {
  name: string
  /** Jira status-category key: `new` (to do), `indeterminate` (in progress), `done`, or `unknown`. */
  category: 'new' | 'indeterminate' | 'done' | 'unknown'
}

/** One issue comment, body already converted to markdown. */
export interface IssueComment {
  id: string
  author: PersonRef
  /** ISO 8601 timestamp. */
  created: string
  /** Markdown body (Jira wiki markup converted; bounded). */
  body: string
}

/** One issue link as seen from the issue. */
export interface IssueLink {
  /** Relationship phrase, e.g. `blocks`, `is blocked by`. */
  relation: string
  key: string
  summary: string
  status?: IssueStatus
}

/** One transition currently available on the issue. */
export interface IssueTransition {
  id: string
  name: string
  to: string
}

/** Attachment metadata (content is never fetched). */
export interface AttachmentRef {
  filename: string
  size: number
  url?: string
  mimeType?: string
}

/** Compact Jira issue record shown by the panel and the chat cards. */
export interface IssueRecord {
  kind: 'issue'
  key: string
  summary: string
  status: IssueStatus
  type: string
  priority?: string
  assignee?: PersonRef
  reporter?: PersonRef
  labels: string[]
  components: string[]
  fixVersions: string[]
  /** Markdown description (bounded). */
  description: string
  created?: string
  updated?: string
  dueDate?: string
  resolution?: string
  project?: string
  parent?: { key: string; summary: string }
  subtasks: { key: string; summary: string; status?: IssueStatus }[]
  epic?: { key: string; name?: string }
  sprint?: string
  storyPoints?: number
  comments: IssueComment[]
  links: IssueLink[]
  attachments: AttachmentRef[]
  transitions: IssueTransition[]
  /** Browse URL on the Jira instance. */
  url: string
  /** Wall-clock ms when the record was fetched. */
  fetchedAt: number
}

/** Compact Confluence page record. */
export interface PageRecord {
  kind: 'page'
  id: string
  title: string
  space: { key: string; name?: string }
  version: number
  /** ISO 8601 timestamp of the current version. */
  versionAt?: string
  versionBy?: PersonRef
  created?: string
  author?: PersonRef
  ancestors: { id: string; title: string }[]
  labels: string[]
  /** Markdown body (converted from the rendered view; bounded). */
  body: string
  /** True when `body` was cut at the size bound. */
  bodyTruncated: boolean
  url: string
  fetchedAt: number
}

/** Bitbucket pull request identity. */
export interface PrRef {
  /** Project key (Bitbucket Server) — the MCP server calls it `workspaceSlug`/`project`. */
  project: string
  /** Repository slug. */
  repo: string
  /** Numeric pull request id. */
  id: number
}

/** One reviewer or participant with approval state. */
export interface PrReviewer {
  user: PersonRef
  status: 'APPROVED' | 'NEEDS_WORK' | 'UNAPPROVED'
  role: 'REVIEWER' | 'PARTICIPANT' | 'AUTHOR'
}

/** Compact Bitbucket pull request record. */
export interface PrRecord {
  kind: 'pr'
  ref: PrRef
  /** Stable key `PROJECT/repo#id`, also the map key inside the projection. */
  key: string
  title: string
  /** Markdown description (Bitbucket already stores markdown; bounded). */
  description: string
  state: 'OPEN' | 'MERGED' | 'DECLINED'
  author: PersonRef
  reviewers: PrReviewer[]
  from: { branch: string; commit?: string }
  to: { branch: string; commit?: string }
  created?: string
  updated?: string
  /** Optimistic-lock version Bitbucket expects on approve/merge. */
  version: number
  url: string
  fetchedAt: number
}

/** Any entity the panel tracks. */
export type EntityRecord = IssueRecord | PageRecord | PrRecord

/** Address of one tracked entity. */
export type EntityRef =
  | { kind: 'issue'; key: string }
  | { kind: 'page'; id: string }
  | { kind: 'pr'; key: string }

// ---- Search rows ------------------------------------------------------------

/** One row of a Jira search/board/sprint result. */
export interface JiraSearchRow {
  key: string
  summary: string
  status?: IssueStatus
  type?: string
  priority?: string
  assignee?: string
  updated?: string
}

/** One row of a Confluence CQL search. */
export interface ConfluenceSearchRow {
  id: string
  title: string
  space?: string
  excerpt?: string
  url?: string
  updated?: string
}

/** One search result set, keyed by the tool call that produced it. */
export type SearchRecord =
  | { service: 'jira'; callId: string; query: string; total: number; rows: JiraSearchRow[] }
  | { service: 'confluence'; callId: string; query: string; total: number; rows: ConfluenceSearchRow[] }

// ---- Activity ---------------------------------------------------------------

/** What an Atlassian tool call did, classified for the activity feed. */
export type ActivityKind =
  | 'read' | 'search' | 'create' | 'update' | 'comment' | 'transition' | 'assign'
  | 'link' | 'approve' | 'merge' | 'decline' | 'branch' | 'delete' | 'other'

/** One activity feed entry. */
export interface ActivityEntry {
  id: string
  /** Wall-clock ms. */
  at: number
  kind: ActivityKind
  /** Wire tool name that produced the entry. */
  tool: string
  entity?: EntityRef
  /** One-line human account. */
  summary: string
  ok: boolean
  callId?: string
}

// ---- Reviews ----------------------------------------------------------------

/** Severity of one review finding. */
export type FindingSeverity = 'critical' | 'major' | 'minor' | 'nit'

/** Category of one review finding. */
export type FindingCategory = 'security' | 'correctness' | 'readability' | 'performance' | 'testing' | 'style'

/** Which side of the diff a finding's line lives on. */
export type DiffSide = 'ADDED' | 'REMOVED' | 'CONTEXT'

/** One review finding the agent recorded. */
export interface ReviewFinding {
  id: string
  /** Wall-clock ms. */
  at: number
  file: string
  line: number
  side: DiffSide
  severity: FindingSeverity
  category: FindingCategory
  title: string
  /** Markdown comment proposed for Bitbucket. */
  comment: string
  /** Verbatim code the finding is about. */
  evidence: string
  /** Why the evidence supports the finding. */
  rationale: string
  /** Optional replacement code or concrete fix. */
  suggestion?: string
  /** Ids of existing PR comments near the same lines the agent acknowledged before recording. */
  overlaps?: number[]
  posted?: {
    commentId: number
    url?: string
    mode: 'inline' | 'general'
    at: number
  }
  dismissed?: boolean
}

/** One comment already on the pull request when the review started. */
export interface ExistingPrComment {
  id: number
  author: PersonRef
  /** Comment text (bounded). */
  text: string
  /** File path when the comment is inline. */
  file?: string
  /** Line number when the comment is inline. */
  line?: number
  side?: DiffSide
  /** ISO 8601 timestamp. */
  created?: string
  /** Number of replies under the comment. */
  replies: number
}

/** Verdict the agent reaches at the end of a review. */
export type ReviewVerdict = 'approve' | 'request-changes' | 'comment'

/** One PR review run. */
export interface ReviewRecord {
  id: string
  pr: PrRef
  prKey: string
  status: 'running' | 'complete' | 'cancelled'
  startedAt: number
  completedAt?: number
  summary?: string
  verdict?: ReviewVerdict
  findings: ReviewFinding[]
  /** Comments already on the pull request when the review started. */
  existing: ExistingPrComment[]
}

// ---- Session events ---------------------------------------------------------

/** Log-only entity snapshot appended after the host fetched an entity. */
export interface AtlassianSnapshotEvent {
  entity: EntityRecord
  /** Whether the panel should focus this entity. */
  focus: boolean
  reason: 'tool' | 'open' | 'refresh' | 'pin' | 'review'
  /** Tool call that caused the fetch, when any. */
  callId?: string
}

/** Log-only activity entry. */
export type AtlassianActivityEvent = ActivityEntry

/** Log-only search result capture. */
export type AtlassianSearchEvent = SearchRecord

/** Log-only pinned-ticket change; `null` clears the pin. */
export interface AtlassianPinEvent {
  key: string | null
}

/** Log-only review lifecycle transitions. */
export type AtlassianReviewEvent =
  | { op: 'start'; reviewId: string; pr: PrRef; at: number; existing: ExistingPrComment[] }
  | { op: 'finding'; reviewId: string; finding: ReviewFinding }
  | { op: 'complete'; reviewId: string; summary: string; verdict: ReviewVerdict; at: number }
  | { op: 'cancel'; reviewId: string; at: number }
  | { op: 'posted'; reviewId: string; findingId: string; commentId: number; url?: string; mode: 'inline' | 'general'; at: number }
  | { op: 'dismiss'; reviewId: string; findingId: string }

declare module '@cortex/session/types' {
  interface SessionEventMap {
    /** Historical entity snapshot (log-only, non-surface; no live writer). */
    'atlassian/snapshot': AtlassianSnapshotEvent
    /** Historical activity entry (log-only, non-surface; no live writer). */
    'atlassian/activity': AtlassianActivityEvent
    /** Historical search result set (log-only, non-surface; no live writer). */
    'atlassian/search': AtlassianSearchEvent
    /** Historical pinned ticket change (log-only, non-surface; no live writer). */
    'atlassian/pin': AtlassianPinEvent
    /** Historical review transition (log-only, non-surface; no live writer). */
    'atlassian/review': AtlassianReviewEvent
  }
}

declare module '@cortex/llm' {
  interface MessageSourceMap {
    /**
     * Historical review instruction source; Cortex has no PR-review command.
     * @persistenceAttribution
     */
    'atlassian-review': { kind: 'atlassian-review'; form: 'instructions' }
  }
}
