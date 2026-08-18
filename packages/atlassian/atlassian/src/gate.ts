/**
 * The Atlassian write gate: a `tools/pre-execute` decision for mutating
 * `mcp__atlassian__*` / `mcp__bitbucket__*` calls driven by the `writes`
 * setting — ask the user, allow, or deny. Read tools always delegate.
 *
 * @module
 */

import type { PreToolDecision } from '@cortex/tools'
import { classifyTool, isAtlassianWrite, rawToolName } from './tools.ts'
import type { AtlassianSettings } from './types.ts'

/**
 * Human account of one write call for the approval prompt.
 * @param name - wire tool name.
 * @param args - parsed call arguments.
 * @returns one line naming the operation and its target.
 */
export function describeWrite(name: string, args: unknown): string {
  const raw = rawToolName(name)
  const { kind } = classifyTool(raw)
  const record = typeof args === 'object' && args !== null ? args as Record<string, unknown> : {}
  const target = [record.issue_key, record.page_id, record.title, record.prId, record.repository, record.repoSlug]
    .find((value): value is string | number => typeof value === 'string' || typeof value === 'number')
  const verb = raw.replace(/^(?:jira|confluence|bitbucket)_/, '').replace(/_/g, ' ')
  return `Atlassian ${kind}: ${verb}${target === undefined ? '' : ` on ${String(target)}`}`
}

/**
 * Decide one pre-execute call under the current write policy.
 * @param name - wire tool name.
 * @param args - parsed call arguments.
 * @param policy - the `writes` setting.
 * @returns the decision, or `undefined` to delegate to the next listener.
 */
export function gateDecision(name: string, args: unknown, policy: AtlassianSettings['writes']): PreToolDecision | undefined {
  if (!isAtlassianWrite(name)) return undefined
  switch (policy) {
    case 'allow': return undefined
    case 'deny': return { kind: 'deny', reason: `${describeWrite(name, args)} — Atlassian writes are disabled in settings` }
    case 'ask': return { kind: 'ask', reason: describeWrite(name, args) }
    /* v8 ignore next 2 -- closed union backstop */
    default: return undefined
  }
}
