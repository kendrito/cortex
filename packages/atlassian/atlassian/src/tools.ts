/**
 * Atlassian tool-name vocabulary: the fixed MCP server namespaces, the
 * read/write classification the ask gate relies on, and the pure extraction of
 * touched entities, search rows, and activity summaries from a tool call's
 * arguments and result text.
 *
 * Every function here is pure. The tool names come from the two MCP servers
 * this package mounts (`sooperset/mcp-atlassian` under `atlassian`,
 * `n11techhub/mcp-bitbucket` under `bitbucket`).
 *
 * @module
 */

import type {
  ActivityKind, ConfluenceSearchRow, IssueStatus, JiraSearchRow, SearchRecord,
} from './types.ts'

/** Fixed `serverName` of the Jira/Confluence MCP mount; the browser keys its cards on it. */
export const ATLASSIAN_SERVER = 'atlassian'
/** Fixed `serverName` of the Bitbucket MCP mount; the browser keys its cards on it. */
export const BITBUCKET_SERVER = 'bitbucket'

/** Public-name prefix of every Jira/Confluence tool. */
export const ATLASSIAN_PREFIX = `mcp__${ATLASSIAN_SERVER}__`
/** Public-name prefix of every Bitbucket tool. */
export const BITBUCKET_PREFIX = `mcp__${BITBUCKET_SERVER}__`

/** Names of the review tools this package registers itself. */
export const REVIEW_FINDING_TOOL = 'atlassian_review_finding'
/** Name of the review completion tool this package registers itself. */
export const REVIEW_COMPLETE_TOOL = 'atlassian_review_complete'

/**
 * Whether a wire tool name belongs to one of the two Atlassian MCP mounts.
 * @param name - wire tool name.
 * @returns true for `mcp__atlassian__*` and `mcp__bitbucket__*`.
 */
export function isAtlassianTool(name: string): boolean {
  return name.startsWith(ATLASSIAN_PREFIX) || name.startsWith(BITBUCKET_PREFIX)
}

/**
 * Strip the MCP namespace prefix.
 * @param name - wire tool name.
 * @returns the server's own tool name, or the input when it carries no known prefix.
 */
export function rawToolName(name: string): string {
  if (name.startsWith(ATLASSIAN_PREFIX)) return name.slice(ATLASSIAN_PREFIX.length)
  if (name.startsWith(BITBUCKET_PREFIX)) return name.slice(BITBUCKET_PREFIX.length)
  return name
}

/** Raw tool-name fragments that always denote a read. */
const READ_PATTERN
  = /^(?:jira|confluence|bitbucket)_(?:get|search|list|browse|download|check|batch_get)(?:_|$)|^bitbucket_(?:get|list|search|browse)_/

/** Verb → activity kind for write tools, first match wins. */
const WRITE_KINDS: readonly (readonly [RegExp, ActivityKind])[] = [
  [/transition_issue/, 'transition'],
  [/_(?:add|edit)_(?:comment|inline_comment)|reply_to_comment|add_pull_request(?:_file_line)?_comment/, 'comment'],
  [/assign_issue|add_watcher|remove_watcher/, 'assign'],
  [/link|move_issues_to_backlog|add_issues_to_sprint/, 'link'],
  [/approve_pull_request/, 'approve'],
  [/merge_pull_request/, 'merge'],
  [/decline_pull_request/, 'decline'],
  [/create_branch/, 'branch'],
  [/delete_|remove_/, 'delete'],
  [/create_|batch_create_|copy_page|upload_/, 'create'],
  [/update_|move_page|set_page_restrictions|add_label|add_worklog|add_issues|edit_/, 'update'],
]

/**
 * Classify one raw tool name into an activity kind and a write flag. Unknown
 * names default to `other`/write so a future mutating tool is never silently
 * admitted by the ask gate.
 * @param raw - server tool name (prefix already stripped).
 * @returns activity kind and whether the tool mutates remote state.
 */
export function classifyTool(raw: string): { kind: ActivityKind; write: boolean } {
  if (READ_PATTERN.test(raw)) {
    return { kind: /_(?:search|list|browse)|_get_(?:project|board|sprint)_issues|get_agile_boards|get_sprints/.test(raw) ? 'search' : 'read', write: false }
  }
  for (const [pattern, kind] of WRITE_KINDS) {
    if (pattern.test(raw)) return { kind, write: true }
  }
  return { kind: 'other', write: true }
}

/**
 * Whether a wire tool name is a mutating Atlassian tool.
 * @param name - wire tool name.
 * @returns true when the ask gate should consider it.
 */
export function isAtlassianWrite(name: string): boolean {
  return isAtlassianTool(name) && classifyTool(rawToolName(name)).write
}

// ---- Argument access ---------------------------------------------------------

/** Read one string-valued argument. */
function str(args: unknown, key: string): string | undefined {
  if (typeof args !== 'object' || args === null) return undefined
  const value = (args as Record<string, unknown>)[key]
  if (typeof value === 'string') return value.trim() === '' ? undefined : value.trim()
  if (typeof value === 'number' && Number.isFinite(value)) return String(value)
  return undefined
}

/** Read one integer-valued argument. */
function int(args: unknown, key: string): number | undefined {
  const value = str(args, key)
  if (value === undefined) return undefined
  const parsed = Number.parseInt(value, 10)
  return Number.isFinite(parsed) && parsed > 0 ? parsed : undefined
}

/** Parse tool result text as JSON, tolerating leading prose. */
function parseJson(text: string | undefined): unknown {
  if (text === undefined) return undefined
  const trimmed = text.trim()
  if (trimmed === '') return undefined
  try {
    return JSON.parse(trimmed)
  } catch {
    // Some tools prefix prose; retry from the first structural character.
    const start = Math.min(...['{', '['].map(char => trimmed.indexOf(char)).filter(index => index >= 0))
    if (!Number.isFinite(start)) return undefined
    try {
      return JSON.parse(trimmed.slice(start))
    } catch {
      return undefined
    }
  }
}

function record(value: unknown): Record<string, unknown> | undefined {
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? value as Record<string, unknown> : undefined
}

// ---- Touched entities ------------------------------------------------------------

/** One entity a tool call touched, with enough address to fetch it. */
export type TouchRef =
  | { kind: 'issue'; key: string }
  | { kind: 'page'; id?: string; space?: string; title?: string }
  | { kind: 'pr'; project?: string; repo: string; id: number }

const ISSUE_KEY = /^[A-Z][A-Z0-9_]+-\d+$/i

function issueRef(key: string | undefined): TouchRef | undefined {
  return key !== undefined && ISSUE_KEY.test(key) ? { kind: 'issue', key: key.toUpperCase() } : undefined
}

/**
 * Entities a completed Atlassian tool call touched, derived from its
 * arguments and (for creations) its result text.
 * @param raw - server tool name (prefix stripped).
 * @param args - parsed call arguments.
 * @param resultText - concatenated result text, when the call succeeded.
 * @returns touched entity addresses, most relevant first.
 */
export function touchedEntities(raw: string, args: unknown, resultText?: string): TouchRef[] {
  const refs: TouchRef[] = []
  const push = (ref: TouchRef | undefined): void => {
    if (ref !== undefined && !refs.some(existing => JSON.stringify(existing) === JSON.stringify(ref))) refs.push(ref)
  }
  if (raw.startsWith('jira_')) {
    if (raw === 'jira_create_issue') {
      const created = record(record(parseJson(resultText))?.issue)
      push(issueRef(str(created, 'key')))
      return refs
    }
    if (raw === 'jira_batch_create_issues') {
      const parsed = record(parseJson(resultText))
      const issues = parsed?.issues
      if (Array.isArray(issues)) for (const issue of issues.slice(0, 20)) push(issueRef(str(issue, 'key')))
      return refs
    }
    if (raw === 'jira_move_issue') {
      const moved = record(parseJson(resultText))
      push(issueRef(str(record(moved?.issue), 'key') ?? str(moved, 'new_key') ?? str(moved, 'key')))
      push(issueRef(str(args, 'issue_key')))
      return refs
    }
    push(issueRef(str(args, 'issue_key')))
    push(issueRef(str(args, 'inward_issue_key')))
    push(issueRef(str(args, 'outward_issue_key')))
    push(issueRef(str(args, 'epic_key')))
    return refs
  }
  if (raw.startsWith('confluence_')) {
    if (raw === 'confluence_create_page' || raw === 'confluence_create_page_from_template' || raw === 'confluence_copy_page') {
      const parsed = record(parseJson(resultText))
      const page = record(parsed?.page) ?? parsed
      const id = str(page, 'id')
      if (id !== undefined) push({ kind: 'page', id })
      return refs
    }
    if (raw === 'confluence_delete_page') return refs
    const id = str(args, 'page_id') ?? str(args, 'parent_id')
    if (id !== undefined) push({ kind: 'page', id })
    else {
      const title = str(args, 'title')
      const space = str(args, 'space_key')
      if (title !== undefined && space !== undefined) push({ kind: 'page', space, title })
    }
    return refs
  }
  if (raw.startsWith('bitbucket_')) {
    if (raw === 'bitbucket_create_pull_request') {
      const created = record(parseJson(resultText))
      const id = created === undefined ? undefined : int(created, 'id')
      const repo = str(args, 'repository')
      if (id !== undefined && repo !== undefined) {
        const project = str(args, 'project')
        push({ kind: 'pr', ...project === undefined ? {} : { project }, repo, id })
      }
      return refs
    }
    const id = int(args, 'prId')
    const repo = str(args, 'repository') ?? str(args, 'repoSlug')
    if (id !== undefined && repo !== undefined) {
      const project = str(args, 'project') ?? str(args, 'workspaceSlug')
      push({ kind: 'pr', ...project === undefined ? {} : { project }, repo, id })
    }
  }
  return refs
}

// ---- Search rows -----------------------------------------------------------------

/** Tools whose result is a Jira issue list under `issues`. */
const JIRA_LIST_TOOLS = new Set([
  'jira_search', 'jira_get_project_issues', 'jira_get_board_issues', 'jira_get_sprint_issues',
])

function statusOf(value: unknown): IssueStatus | undefined {
  const status = record(value)
  const name = str(status, 'name')
  if (name === undefined) return undefined
  return { name, category: statusCategory(str(status, 'category')) }
}

/**
 * Map a Jira status-category name to its stable key.
 * @param name - status category name (`To Do`, `In Progress`, `Done`, `new`, ...).
 * @returns the category key.
 */
export function statusCategory(name: string | undefined): IssueStatus['category'] {
  const lower = (name ?? '').toLowerCase()
  if (lower === 'done' || lower === 'complete') return 'done'
  if (lower === 'in progress' || lower === 'indeterminate') return 'indeterminate'
  if (lower === 'to do' || lower === 'new' || lower === 'undefined') return 'new'
  return 'unknown'
}

function jiraRow(value: unknown): JiraSearchRow | undefined {
  const issue = record(value)
  const key = str(issue, 'key')
  if (key === undefined) return undefined
  const status = statusOf(issue?.status)
  const type = str(record(issue?.issue_type), 'name')
  const priority = str(record(issue?.priority), 'name')
  const assignee = str(record(issue?.assignee), 'display_name')
  const updated = str(issue, 'updated')
  return {
    key,
    summary: str(issue, 'summary') ?? '',
    ...status === undefined ? {} : { status },
    ...type === undefined ? {} : { type },
    ...priority === undefined ? {} : { priority },
    ...assignee === undefined || assignee === 'Unassigned' ? {} : { assignee },
    ...updated === undefined ? {} : { updated },
  }
}

function confluenceRow(value: unknown): ConfluenceSearchRow | undefined {
  const page = record(value)
  const id = str(page, 'id')
  const title = str(page, 'title')
  if (id === undefined || title === undefined) return undefined
  const space = str(record(page?.space), 'key')
  const url = str(page, 'url')
  const updated = str(page, 'updated')
  const content = record(page?.content)
  const excerptSource = str(page, 'excerpt') ?? str(content, 'value')
  const excerpt = excerptSource === undefined ? undefined : excerptSource.replace(/\s+/g, ' ').slice(0, 240)
  return {
    id,
    title,
    ...space === undefined ? {} : { space },
    ...url === undefined ? {} : { url },
    ...updated === undefined ? {} : { updated },
    ...excerpt === undefined ? {} : { excerpt },
  }
}

/** Bound on captured search rows per result set. */
export const SEARCH_ROW_LIMIT = 50

/**
 * Capture a search-style result as compact rows.
 * @param raw - server tool name (prefix stripped).
 * @param args - parsed call arguments.
 * @param resultText - result text of the successful call.
 * @param callId - tool call id the record is keyed by.
 * @returns the search record, or `undefined` when the tool is not a search or the text is not the expected JSON.
 */
export function searchRecord(raw: string, args: unknown, resultText: string, callId: string): SearchRecord | undefined {
  if (JIRA_LIST_TOOLS.has(raw)) {
    const parsed = record(parseJson(resultText))
    const issues = parsed?.issues
    if (!Array.isArray(issues)) return undefined
    const rows = issues.map(jiraRow).filter((row): row is JiraSearchRow => row !== undefined).slice(0, SEARCH_ROW_LIMIT)
    const total = typeof parsed?.total === 'number' ? parsed.total : rows.length
    const query = str(args, 'jql') ?? str(args, 'project_key') ?? str(args, 'sprint_id') ?? str(args, 'board_id') ?? raw
    return { service: 'jira', callId, query, total, rows }
  }
  if (raw === 'confluence_search') {
    const parsed = parseJson(resultText)
    if (!Array.isArray(parsed)) return undefined
    const rows = parsed.map(confluenceRow).filter((row): row is ConfluenceSearchRow => row !== undefined).slice(0, SEARCH_ROW_LIMIT)
    return { service: 'confluence', callId, query: str(args, 'query') ?? '', total: rows.length, rows }
  }
  return undefined
}

// ---- Activity summaries ------------------------------------------------------------

/** Human label of one touched entity. */
function label(ref: TouchRef | undefined): string {
  if (ref === undefined) return ''
  switch (ref.kind) {
    case 'issue': return ref.key
    case 'page': return ref.id !== undefined ? `page ${ref.id}` : `"${ref.title ?? ''}"`
    case 'pr': return `${ref.repo}#${String(ref.id)}`
    /* v8 ignore next -- closed union backstop */
    default: return ''
  }
}

/**
 * One-line account of a completed Atlassian tool call for the activity feed.
 * @param raw - server tool name (prefix stripped).
 * @param args - parsed call arguments.
 * @param refs - touched entities from {@link touchedEntities}.
 * @param ok - whether the call succeeded.
 * @returns the summary line.
 */
export function activitySummary(raw: string, args: unknown, refs: readonly TouchRef[], ok: boolean): string {
  const target = label(refs[0])
  const suffix = ok ? '' : ' — failed'
  const { kind } = classifyTool(raw)
  switch (kind) {
    case 'transition': return `Transitioned ${target}${suffix}`
    case 'comment': return `Commented on ${target}${suffix}`
    /* v8 ignore next -- the second read repeats a call just proven defined */
    case 'assign': return `Assigned ${target}${str(args, 'assignee') === undefined ? '' : ` to ${str(args, 'assignee') ?? ''}`}${suffix}`
    case 'link': return `Linked ${refs.map(label).filter(Boolean).join(' ↔ ') || target}${suffix}`
    case 'approve': return `Approved ${target}${suffix}`
    case 'merge': return `Merged ${target}${suffix}`
    case 'decline': return `Declined ${target}${suffix}`
    case 'branch': return `Created branch ${str(args, 'branchName') ?? str(args, 'name') ?? ''}${suffix}`
    case 'delete': return `Deleted ${target || raw.replace(/_/g, ' ')}${suffix}`
    case 'create': return `Created ${target || raw.replace(/^(?:jira|confluence|bitbucket)_create_/, '').replace(/_/g, ' ')}${suffix}`
    case 'update': return `Updated ${target}${suffix}`
    case 'search': return `Searched ${str(args, 'jql') ?? str(args, 'query') ?? raw.replace(/_/g, ' ')}${suffix}`
    case 'read': return `Read ${target || raw.replace(/_/g, ' ')}${suffix}`
    default: return `${raw.replace(/_/g, ' ')}${suffix}`
  }
}
