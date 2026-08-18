/**
 * Pure type outlet of `@cortex/atlassian`: entity records, the session events
 * the host appends, the `atlassian` projection view, and the Remote
 * request/response vocabulary. Zero value imports so the browser program can
 * import this module through `@cortex/atlassian/client` without pulling host
 * Context merges.
 *
 * @module @cortex/atlassian/types
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
    /** Host-fetched entity snapshot for the Atlassian panel (log-only, non-surface). */
    'atlassian/snapshot': AtlassianSnapshotEvent
    /** One Atlassian activity feed entry (log-only, non-surface). */
    'atlassian/activity': AtlassianActivityEvent
    /** One captured search result set (log-only, non-surface). */
    'atlassian/search': AtlassianSearchEvent
    /** Pinned ticket change (log-only, non-surface). */
    'atlassian/pin': AtlassianPinEvent
    /** Review lifecycle transition (log-only, non-surface). */
    'atlassian/review': AtlassianReviewEvent
  }
}

// ---- Projection -------------------------------------------------------------

/** Wire value of the `atlassian` projection. */
export interface AtlassianProjection {
  /** Seq of the last folded event; 0 for an untouched session. */
  rev: number
  pinned: string | null
  focus: EntityRef | null
  issues: Record<string, IssueRecord>
  pages: Record<string, PageRecord>
  prs: Record<string, PrRecord>
  /** Most recently touched first. */
  recent: EntityRef[]
  /** Newest first. */
  searches: SearchRecord[]
  /** Newest first. */
  activity: ActivityEntry[]
  reviews: Record<string, ReviewRecord>
  activeReviewId: string | null
}

declare module '@cortex/session-projection/types' {
  interface SessionProjectionMap {
    /** Atlassian entities touched in the session, activity, and PR reviews. */
    atlassian: AtlassianProjection
  }
}

// ---- Settings ---------------------------------------------------------------

/** The `atlassian` settings namespace (flat: every field is a scalar the settings scope can set by name). */
export interface AtlassianSettings {
  /** Jira Data Center base URL, e.g. `https://jira.example.com`. */
  jiraUrl: string
  /** Credential reference (env-var style name) of the Jira personal access token. */
  jiraTokenRef: string
  /** Comma-separated project keys the MCP server may touch; empty = all. */
  jiraProjectsFilter: string
  /** Confluence Data Center base URL. */
  confluenceUrl: string
  /** Credential reference of the Confluence personal access token. */
  confluenceTokenRef: string
  /** Comma-separated space keys the MCP server may touch; empty = all. */
  confluenceSpacesFilter: string
  /** Bitbucket Server base URL. */
  bitbucketUrl: string
  /** Credential reference of the Bitbucket personal access token. */
  bitbucketTokenRef: string
  /** Default project key used when the model or a PR reference omits one. */
  bitbucketDefaultProject: string
  /** Launch line of the Jira/Confluence MCP server (`uvx mcp-atlassian`). */
  atlassianLaunch: string
  /** Launch line of the Bitbucket MCP server. */
  bitbucketLaunch: string
  /** How write tools are admitted: ask the user, allow silently, or deny. */
  writes: 'ask' | 'allow' | 'deny'
  /** mcp-atlassian `TOOLSETS` value. */
  toolsets: string
  /** mcp-atlassian `ENABLED_TOOLS` value; empty = every tool of the selected toolsets. */
  enabledTools: string
}

// ---- Remote vocabulary ------------------------------------------------------

/** Lifecycle of one mounted MCP server. */
export type MountPhase = 'off' | 'starting' | 'ready' | 'error'

/** Status of one MCP server mount. */
export interface MountStatus {
  phase: MountPhase
  /** Number of `mcp__<server>__*` tools currently registered. */
  toolCount: number
  /** Last startup error, when `phase` is `error`. */
  error?: string
  /** What the mount is missing when `phase` is `off`. */
  missing?: ('url' | 'token' | 'launch')[]
}

/** Whole integration status served to the browser. */
export interface AtlassianStatus {
  atlassian: MountStatus
  bitbucket: MountStatus
  /** Which REST-backed panels can work: true when URL and token are both present. */
  rest: { jira: boolean; confluence: boolean; bitbucket: boolean }
}

/** Request of `probe`. */
export interface ProbeRequest {
  service: 'jira' | 'confluence' | 'bitbucket'
}

/** Result of one connection probe. */
export interface ProbeResult {
  service: 'jira' | 'confluence' | 'bitbucket'
  ok: boolean
  /** Display name of the authenticated user when known. */
  user?: string
  error?: string
}

/** Generic remote failure. */
export interface RemoteFailureView {
  ok: false
  code: string
  message: string
}

/** Request of `open`: which entity to fetch and focus. */
export type OpenRequest =
  | { kind: 'issue'; key: string }
  | { kind: 'page'; id: string }
  | { kind: 'pr'; pr: PrRef }

/** Result of `open`/`refresh`: the fetched entity or a failure. */
export type OpenResult = { ok: true; entity: EntityRef } | RemoteFailureView

/** Request of `pin`: the ticket key, or `null` to clear. */
export interface PinRequest {
  key: string | null
}

/** Plain acknowledgement. */
export type AckResult = { ok: true } | RemoteFailureView

/** One PR row of the picker. */
export interface PrSummary {
  ref: PrRef
  key: string
  title: string
  author: PersonRef
  state: 'OPEN' | 'MERGED' | 'DECLINED'
  updated?: string
  approvals: number
  reviewers: number
  url: string
  /** Role of the authenticated user on the PR when the row came from the dashboard. */
  role?: 'REVIEWER' | 'AUTHOR' | 'PARTICIPANT'
}

/** Request of `listPullRequests`. */
export interface ListPullRequestsRequest {
  /** `inbox` = PRs where the token's user reviews or authored; `repo` = one repository. */
  scope: 'inbox' | 'repo'
  project?: string
  repo?: string
  state?: 'OPEN' | 'MERGED' | 'DECLINED' | 'ALL'
}

/** Result of `listPullRequests`. */
export type ListPullRequestsResult = { ok: true; items: PrSummary[] } | RemoteFailureView

/** Request of `startReview`. */
export interface StartReviewRequest {
  pr: PrRef
  /** Extra reviewer instructions, e.g. team conventions. */
  focus?: string
}

/** Result of `startReview`. */
export type StartReviewResult = { ok: true; reviewId: string } | RemoteFailureView

/** Request of `postFinding`. */
export interface PostFindingRequest {
  reviewId: string
  findingId: string
  /** Comment text override; the recorded comment when omitted. */
  comment?: string
}

/** Result of `postFinding`. */
export type PostFindingResult =
  | { ok: true; commentId: number; url?: string; mode: 'inline' | 'general' }
  | RemoteFailureView

/** Request of `dismissFinding`. */
export interface DismissFindingRequest {
  reviewId: string
  findingId: string
}

/** Request of `cancelReview`. */
export interface CancelReviewRequest {
  reviewId: string
}

/** One line of diff context around a finding. */
export interface DiffContextLine {
  type: DiffSide
  /** Line number in the source (FROM) file, when the line exists there. */
  source?: number
  /** Line number in the destination (TO) file, when the line exists there. */
  destination?: number
  text: string
  /** True for the anchored line itself. */
  anchor?: boolean
}

/** Request of `diffContext`. */
export interface DiffContextRequest {
  pr: PrRef
  file: string
  line: number
  side: DiffSide
  /** Context lines on each side; defaults to 6, capped at 30. */
  context?: number
}

/** Result of `diffContext`. */
export type DiffContextResult =
  | { ok: true; file: string; lines: DiffContextLine[]; found: boolean }
  | RemoteFailureView
