/**
 * Jira Data Center REST v2 adapter: fetch one issue as an {@link IssueRecord}
 * and probe the authenticated user.
 *
 * @module
 */

import { BODY_LIMIT, COMMENT_LIMIT, bound, wikiToMarkdown } from '../markup.ts'
import { statusCategory } from '../tools.ts'
import type {
  AttachmentRef, IssueComment, IssueLink, IssueRecord, IssueStatus, IssueTransition, PersonRef,
} from '../types.ts'
import { query, requestJson, type FetchLike, type RestTarget } from './http.ts'
import { dict, list, text, type Dict } from './json.ts'

/** Fields requested from the issue resource. */
const ISSUE_FIELDS = [
  'summary', 'description', 'status', 'issuetype', 'priority', 'assignee', 'reporter', 'labels',
  'components', 'fixVersions', 'created', 'updated', 'duedate', 'resolution', 'project', 'parent',
  'subtasks', 'comment', 'attachment', 'issuelinks',
].join(',')

/** Comments kept per issue (newest first). */
export const COMMENT_LIMIT_COUNT = 30

/**
 * Person from a Jira user object.
 * @param value - Jira `user` JSON.
 * @returns the person, or `undefined` when absent.
 */
export function jiraPerson(value: unknown): PersonRef | undefined {
  const user = dict(value)
  if (user === undefined) return undefined
  const name = text(user.displayName) ?? text(user.name) ?? text(user.key)
  if (name === undefined) return undefined
  const id = text(user.name) ?? text(user.key) ?? text(user.accountId)
  const avatars = dict(user.avatarUrls)
  const avatar = text(avatars?.['48x48']) ?? text(avatars?.['32x32'])
  return { name, ...id === undefined ? {} : { id }, ...avatar === undefined ? {} : { avatar } }
}

/**
 * Status from a Jira status object.
 * @param value - Jira `status` JSON.
 * @returns the status, or `undefined` when absent.
 */
export function jiraStatus(value: unknown): IssueStatus | undefined {
  const status = dict(value)
  const name = text(status?.name)
  if (name === undefined) return undefined
  const category = dict(status?.statusCategory)
  return { name, category: statusCategory(text(category?.key) ?? text(category?.name)) }
}

function comments(value: unknown): IssueComment[] {
  const container = dict(value)
  const items = list(container?.comments)
  const result: IssueComment[] = []
  for (const item of items) {
    const comment = dict(item)
    if (comment === undefined) continue
    const id = text(comment.id) ?? String(result.length)
    result.push({
      id,
      author: jiraPerson(comment.author) ?? { name: '' },
      created: text(comment.created) ?? '',
      body: bound(wikiToMarkdown(text(comment.body) ?? ''), COMMENT_LIMIT).text,
    })
  }
  return result.slice(-COMMENT_LIMIT_COUNT).reverse()
}

function links(value: unknown): IssueLink[] {
  const result: IssueLink[] = []
  for (const item of list(value)) {
    const link = dict(item)
    const type = dict(link?.type)
    if (link === undefined || type === undefined) continue
    const inward = dict(link.inwardIssue)
    const outward = dict(link.outwardIssue)
    const other = inward ?? outward
    const key = text(other?.key)
    if (key === undefined) continue
    const relation = (inward !== undefined ? text(type.inward) : text(type.outward)) ?? text(type.name) ?? 'relates to'
    const fields = dict(other?.fields)
    const status = jiraStatus(fields?.status)
    result.push({ relation, key, summary: text(fields?.summary) ?? '', ...status === undefined ? {} : { status } })
  }
  return result
}

function attachments(value: unknown): AttachmentRef[] {
  const result: AttachmentRef[] = []
  for (const item of list(value)) {
    const attachment = dict(item)
    const filename = text(attachment?.filename)
    if (attachment === undefined || filename === undefined) continue
    const url = text(attachment.content)
    const mimeType = text(attachment.mimeType)
    result.push({
      filename,
      size: typeof attachment.size === 'number' ? attachment.size : 0,
      ...url === undefined ? {} : { url },
      ...mimeType === undefined ? {} : { mimeType },
    })
  }
  return result
}

function transitions(value: unknown): IssueTransition[] {
  const result: IssueTransition[] = []
  for (const item of list(value)) {
    const transition = dict(item)
    const id = text(transition?.id)
    const name = text(transition?.name)
    if (id === undefined || name === undefined) continue
    result.push({ id, name, to: text(dict(transition?.to)?.name) ?? name })
  }
  return result
}

/** Sprint name from the Greenhopper custom field (string encoded or object). */
function sprintName(value: unknown): string | undefined {
  const entries = list(value)
  const last = entries[entries.length - 1] ?? value
  if (typeof last === 'string') {
    const match = /name=([^,\]]+)/.exec(last)
    return match?.[1]?.trim()
  }
  return text(dict(last)?.name)
}

/** Named custom-field facts (each absent when the field is missing or empty). */
interface CustomFacts {
  epic?: IssueRecord['epic']
  sprint?: string
  storyPoints?: number
}

/** Custom fields located by their human names through the `names` expansion. */
function customFields(fields: Dict, names: Dict | undefined): CustomFacts {
  const result: CustomFacts = {}
  if (names === undefined) return result
  for (const [id, label] of Object.entries(names)) {
    if (typeof label !== 'string' || !id.startsWith('customfield_')) continue
    const value = fields[id]
    if (value === null || value === undefined) continue
    const lower = label.toLowerCase()
    if (lower === 'sprint') {
      const name = sprintName(value)
      if (name !== undefined) result.sprint = name
    } else if (lower === 'epic link') {
      const key = text(value)
      if (key !== undefined) result.epic = { key }
    } else if (lower === 'epic name' && result.epic !== undefined) {
      const name = text(value)
      if (name !== undefined) result.epic = { ...result.epic, name }
    } else if (lower === 'story points' || lower === 'story point estimate') {
      if (typeof value === 'number') result.storyPoints = value
    }
  }
  return result
}

/**
 * Convert one Jira REST issue JSON into the compact record.
 * @param baseUrl - Jira base URL (for the browse link).
 * @param json - `GET /rest/api/2/issue/{key}` response.
 * @param fetchedAt - wall-clock ms.
 * @returns the record.
 */
export function issueRecordFromRest(baseUrl: string, json: unknown, fetchedAt: number): IssueRecord {
  const issue = dict(json) ?? {}
  const fields = dict(issue.fields) ?? {}
  const key = text(issue.key) ?? ''
  const status = jiraStatus(fields.status) ?? { name: 'Unknown', category: 'unknown' }
  const priority = text(dict(fields.priority)?.name)
  const assignee = jiraPerson(fields.assignee)
  const reporter = jiraPerson(fields.reporter)
  const created = text(fields.created)
  const updated = text(fields.updated)
  const dueDate = text(fields.duedate)
  const resolution = text(dict(fields.resolution)?.name)
  const project = text(dict(fields.project)?.key)
  const parentDict = dict(fields.parent)
  const parentKey = text(parentDict?.key)
  const subtasks = list(fields.subtasks).flatMap((item) => {
    const subtask = dict(item)
    const subKey = text(subtask?.key)
    if (subKey === undefined) return []
    const subFields = dict(subtask?.fields)
    const subStatus = jiraStatus(subFields?.status)
    return [{ key: subKey, summary: text(subFields?.summary) ?? '', ...subStatus === undefined ? {} : { status: subStatus } }]
  })
  const custom = customFields(fields, dict(issue.names))
  return {
    kind: 'issue',
    key,
    summary: text(fields.summary) ?? '',
    status,
    type: text(dict(fields.issuetype)?.name) ?? 'Issue',
    ...priority === undefined ? {} : { priority },
    ...assignee === undefined ? {} : { assignee },
    ...reporter === undefined ? {} : { reporter },
    labels: list(fields.labels).filter((label): label is string => typeof label === 'string'),
    components: list(fields.components).flatMap(item => text(dict(item)?.name) ?? []),
    fixVersions: list(fields.fixVersions).flatMap(item => text(dict(item)?.name) ?? []),
    description: bound(wikiToMarkdown(text(fields.description) ?? ''), BODY_LIMIT).text,
    ...created === undefined ? {} : { created },
    ...updated === undefined ? {} : { updated },
    ...dueDate === undefined ? {} : { dueDate },
    ...resolution === undefined ? {} : { resolution },
    ...project === undefined ? {} : { project },
    ...parentKey === undefined ? {} : { parent: { key: parentKey, summary: text(dict(parentDict?.fields)?.summary) ?? '' } },
    subtasks,
    ...custom.epic === undefined ? {} : { epic: custom.epic },
    ...custom.sprint === undefined ? {} : { sprint: custom.sprint },
    ...custom.storyPoints === undefined ? {} : { storyPoints: custom.storyPoints },
    comments: comments(fields.comment),
    links: links(fields.issuelinks),
    attachments: attachments(fields.attachment),
    transitions: transitions(issue.transitions),
    url: `${baseUrl}/browse/${key}`,
    fetchedAt,
  }
}

/** Jira REST adapter over one target. */
export class JiraRest {
  /**
   * @param fetchImpl - fetch implementation.
   * @param target - Jira base URL and token.
   * @param now - clock.
   */
  constructor(
    private readonly fetchImpl: FetchLike,
    private readonly target: RestTarget,
    /* v8 ignore start -- production default; tests inject the clock */
    private readonly now: () => number = () => Date.now(),
    /* v8 ignore stop */
  ) {}

  /**
   * Fetch one issue with comments, links, attachments, transitions, and named
   * custom fields.
   * @param key - issue key.
   * @param signal - optional cancellation.
   * @returns the compact record.
   */
  async getIssue(key: string, signal?: AbortSignal): Promise<IssueRecord> {
    const json = await requestJson(
      this.fetchImpl,
      this.target,
      'GET',
      `/rest/api/2/issue/${encodeURIComponent(key)}${query({ fields: `${ISSUE_FIELDS},*navigable`, expand: 'names,transitions' })}`,
      undefined,
      signal,
    )
    return issueRecordFromRest(this.target.baseUrl, json, this.now())
  }

  /**
   * Probe the token: the authenticated user's display name.
   * @param signal - optional cancellation.
   * @returns display name.
   */
  async myself(signal?: AbortSignal): Promise<string> {
    const json = dict(await requestJson(this.fetchImpl, this.target, 'GET', '/rest/api/2/myself', undefined, signal))
    return text(json?.displayName) ?? text(json?.name) ?? 'authenticated'
  }
}
