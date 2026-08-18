/**
 * Bitbucket structured diff JSON → the client diff primitive's per-file
 * hunks (removed side / added side), plus a compact per-file summary.
 *
 * @module
 */

import type { DiffHunk } from '@cortex/client-ui-primitives'

/** Compact account of one changed file. */
export interface DiffFileSummary {
  path: string
  added: number
  removed: number
  binary: boolean
  truncated: boolean
}

/** Whole diff summary derived from the tool result. */
export interface DiffSummary {
  files: DiffFileSummary[]
  hunks: DiffHunk[]
  truncated: boolean
}

type Dict = Record<string, unknown>

const dict = (value: unknown): Dict | undefined =>
  typeof value === 'object' && value !== null && !Array.isArray(value) ? value as Dict : undefined
const list = (value: unknown): unknown[] => Array.isArray(value) ? value : []
/** Own-property read: `toString` is a Bitbucket diff field name that would otherwise resolve to `Object.prototype`. */
const ownString = (value: Dict | undefined, key: string): string | undefined => {
  const raw: unknown = value === undefined ? undefined : Object.getOwnPropertyDescriptor(value, key)?.value
  return typeof raw === 'string' ? raw : undefined
}

/**
 * Convert the Bitbucket diff JSON (`bitbucket_get_pull_request_diff` result)
 * into per-file summaries and diff-primitive hunks. Each file becomes one hunk
 * whose sides are the concatenation of its removed/context and added/context
 * lines, so the primitive's red/green blocks read as the change.
 * @param json - parsed diff JSON.
 * @returns the summary, or `undefined` when the value is not a Bitbucket diff.
 */
export function summarizeBitbucketDiff(json: unknown): DiffSummary | undefined {
  const root = dict(json)
  if (root === undefined || !Array.isArray(root.diffs)) return undefined
  const files: DiffFileSummary[] = []
  const hunks: DiffHunk[] = []
  for (const item of root.diffs) {
    const file = dict(item)
    if (file === undefined) continue
    const path = ownString(dict(file.destination), 'toString') ?? ownString(dict(file.source), 'toString')
    if (path === undefined) continue
    let added = 0
    let removed = 0
    const oldLines: string[] = []
    const newLines: string[] = []
    for (const hunkItem of list(file.hunks)) {
      const hunk = dict(hunkItem)
      for (const segmentItem of list(hunk?.segments)) {
        const segment = dict(segmentItem)
        const type = segment?.type
        for (const lineItem of list(segment?.lines)) {
          const line = dict(lineItem)
          const text = typeof line?.line === 'string' ? line.line : ''
          if (type === 'ADDED') {
            added += 1
            newLines.push(text)
          } else if (type === 'REMOVED') {
            removed += 1
            oldLines.push(text)
          } else {
            oldLines.push(text)
            newLines.push(text)
          }
        }
      }
      oldLines.push('')
      newLines.push('')
    }
    const binary = file.binary === true
    files.push({ path, added, removed, binary, truncated: file.truncated === true })
    if (!binary && (added > 0 || removed > 0)) {
      hunks.push({ path, oldText: removed === 0 ? null : oldLines.join('\n').trimEnd(), newText: newLines.join('\n').trimEnd() })
    }
  }
  return { files, hunks, truncated: root.truncated === true }
}
