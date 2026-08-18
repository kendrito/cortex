/**
 * Pure derivations from one logged Atlassian tool call: lifecycle state,
 * parsed arguments, result text/JSON, and the entity the call addresses. Every
 * function tolerates streaming (truncated) arguments and non-JSON results.
 *
 * @module
 */

import type { ToolCallBlock } from '@cortex/client-runtime/client'
import type { EntityRef, PrRef } from '@cortex/atlassian/client'

/** Lifecycle of one logged call. */
export type CallState = 'running' | 'ok' | 'error' | 'stopped'

/** Everything a card needs, derived once per render from the frozen block. */
export interface CallFacts {
  /** Server tool name without the `mcp__<server>__` prefix. */
  raw: string
  /** `atlassian` or `bitbucket`, or `local` for this package's own tools. */
  server: 'atlassian' | 'bitbucket' | 'local'
  state: CallState
  args: Record<string, unknown>
  /** Concatenated text of a settled result; empty while running. */
  text: string
  /** Parsed JSON of the result text, when it is JSON. */
  json: unknown
  /** The entity the call addresses, when derivable from the arguments. */
  entity: EntityRef | undefined
  /** PR address when the call addresses one (project may be empty when omitted). */
  pr: PrRef | undefined
  issueKey: string | undefined
}

const ISSUE_KEY = /^[A-Z][A-Z0-9_]+-\d+$/i

/**
 * Split a wire tool name into server and raw name.
 * @param name - wire tool name.
 * @returns server namespace and raw name.
 */
export function splitToolName(name: string): { server: CallFacts['server']; raw: string } {
  const match = /^mcp__(atlassian|bitbucket)__(.+)$/.exec(name)
  /* v8 ignore next -- ?? arm: both capture groups are non-optional in the pattern. */
  if (match !== null) return { server: match[1] as 'atlassian' | 'bitbucket', raw: match[2] ?? name }
  return { server: 'local', raw: name }
}

/**
 * Parse call arguments, tolerating a streaming JSON prefix.
 * @param argsRaw - raw argument text.
 * @returns the parsed object, or `{}`.
 */
export function parseArgs(argsRaw: string): Record<string, unknown> {
  try {
    const parsed = JSON.parse(argsRaw) as unknown
    return typeof parsed === 'object' && parsed !== null && !Array.isArray(parsed) ? parsed as Record<string, unknown> : {}
  } catch {
    // A truncated streaming prefix has no readable arguments yet.
    return {}
  }
}

/**
 * Parse result text as JSON when it is JSON.
 * @param text - result text.
 * @returns the parsed value, or `undefined`.
 */
export function parseJsonText(text: string): unknown {
  const trimmed = text.trim()
  if (trimmed === '' || (trimmed[0] !== '{' && trimmed[0] !== '[')) return undefined
  try {
    return JSON.parse(trimmed) as unknown
  } catch {
    return undefined
  }
}

function str(args: Record<string, unknown>, key: string): string | undefined {
  const value = args[key]
  if (typeof value === 'string' && value.trim() !== '') return value.trim()
  if (typeof value === 'number' && Number.isFinite(value)) return String(value)
  return undefined
}

/**
 * Derive the card facts of one frozen call block.
 * @param toolName - wire tool name.
 * @param block - running call or settled result node.
 * @returns the facts.
 */
export function callFacts(toolName: string, block: ToolCallBlock): CallFacts {
  const settled = 'kind' in block
  const argsRaw = (settled ? block.call?.argsRaw : block.argsRaw) ?? ''
  const state: CallState = !settled
    ? 'running'
    : block.error?.code === 'interrupted'
      ? 'stopped'
      : block.isError ? 'error' : 'ok'
  const text = settled
    ? block.content.map(item => item.type === 'text' ? item.text : '').filter(Boolean).join('\n')
    : ''
  const { server, raw } = splitToolName(toolName)
  const args = parseArgs(argsRaw)
  const issueKey = [str(args, 'issue_key'), str(args, 'inward_issue_key'), str(args, 'epic_key')]
    .find((key): key is string => key !== undefined && ISSUE_KEY.test(key))?.toUpperCase()
  const prId = Number.parseInt(str(args, 'prId') ?? '', 10)
  const repo = str(args, 'repository') ?? str(args, 'repoSlug')
  const pr: PrRef | undefined = Number.isFinite(prId) && prId > 0 && repo !== undefined
    ? { project: str(args, 'project') ?? str(args, 'workspaceSlug') ?? '', repo, id: prId }
    : undefined
  const pageId = str(args, 'page_id') ?? str(args, 'parent_id')
  const entity: EntityRef | undefined = issueKey !== undefined && raw.startsWith('jira_')
    ? { kind: 'issue', key: issueKey }
    : pageId !== undefined && raw.startsWith('confluence_')
      ? { kind: 'page', id: pageId }
      : pr !== undefined && pr.project !== ''
        ? { kind: 'pr', key: `${pr.project.toUpperCase()}/${pr.repo}#${String(pr.id)}` }
        : undefined
  return {
    raw,
    server,
    state,
    args,
    text,
    json: state === 'ok' ? parseJsonText(text) : undefined,
    entity,
    pr,
    issueKey,
  }
}

/**
 * Human title of one raw tool name: `jira_get_issue` → `Get issue`.
 * @param raw - raw tool name.
 * @returns the title.
 */
export function toolTitle(raw: string): string {
  const stripped = raw.replace(/^(?:jira|confluence|bitbucket)_/, '').replace(/_/g, ' ')
  return stripped.charAt(0).toUpperCase() + stripped.slice(1)
}

/**
 * Service label of one raw tool name.
 * @param raw - raw tool name.
 * @returns `Jira`, `Confluence`, `Bitbucket`, or `Review`.
 */
export function serviceLabel(raw: string): 'Jira' | 'Confluence' | 'Bitbucket' | 'Review' {
  if (raw.startsWith('jira_')) return 'Jira'
  if (raw.startsWith('confluence_')) return 'Confluence'
  if (raw.startsWith('bitbucket_')) return 'Bitbucket'
  return 'Review'
}
