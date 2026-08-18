/**
 * Pure presentation helpers shared by the panel and the cards: tones for
 * statuses and severities, relative time, initials, and Atlassian key
 * recognition. No React, no I/O.
 *
 * @module
 */

import type {
  ActivityKind, EntityRef, FindingSeverity, IssueStatus, PrRecord, PrRef, ReviewFinding,
} from '@cortex/atlassian/client'

/** Visual tone of one status/severity chip; maps to `data-tone` in CSS. */
export type Tone = 'neutral' | 'info' | 'success' | 'warn' | 'error' | 'accent'

/**
 * Tone of a Jira status by its category.
 * @param status - issue status.
 * @returns the chip tone.
 */
export function statusTone(status: IssueStatus | undefined): Tone {
  switch (status?.category) {
    case 'done': return 'success'
    case 'indeterminate': return 'info'
    case 'new': return 'neutral'
    default: return 'neutral'
  }
}

/**
 * Tone of a pull request state.
 * @param state - PR state.
 * @returns the chip tone.
 */
export function prStateTone(state: PrRecord['state']): Tone {
  switch (state) {
    case 'MERGED': return 'accent'
    case 'DECLINED': return 'error'
    default: return 'success'
  }
}

/**
 * Tone of a finding severity.
 * @param severity - finding severity.
 * @returns the chip tone.
 */
export function severityTone(severity: FindingSeverity): Tone {
  switch (severity) {
    case 'critical': return 'error'
    case 'major': return 'warn'
    case 'minor': return 'info'
    default: return 'neutral'
  }
}

/**
 * Ordering weight for sorting findings, most severe first.
 * @param severity - finding severity.
 * @returns 0 for critical through 3 for nit.
 */
export function severityRank(severity: FindingSeverity): number {
  switch (severity) {
    case 'critical': return 0
    case 'major': return 1
    case 'minor': return 2
    default: return 3
  }
}

/**
 * Tone of one activity kind (writes stand out).
 * @param kind - activity kind.
 * @returns the dot tone.
 */
export function activityTone(kind: ActivityKind): Tone {
  switch (kind) {
    case 'read':
    case 'search': return 'neutral'
    case 'delete':
    case 'decline': return 'error'
    case 'approve':
    case 'merge':
    case 'create': return 'success'
    case 'transition':
    case 'comment':
    case 'update':
    case 'assign':
    case 'link':
    case 'branch': return 'info'
    default: return 'neutral'
  }
}

/**
 * Two-letter initials of a display name.
 * @param name - display name.
 * @returns initials, `?` for an empty name.
 */
export function initials(name: string): string {
  const parts = name.trim().split(/[\s._-]+/).filter(part => part !== '')
  const first = parts[0]
  if (first === undefined) return '?'
  const last = parts.length > 1 ? parts[parts.length - 1] : undefined
  const tail = last === undefined ? first.slice(1, 2) : last.slice(0, 1)
  return `${first.slice(0, 1)}${tail}`.toUpperCase()
}

/** Relative-time buckets the panel renders; the caller supplies the copy. */
export type RelativeTime =
  | { unit: 'now' }
  | { unit: 'minutes' | 'hours' | 'days'; n: number }

/**
 * Bucket a timestamp relative to now.
 * @param iso - ISO timestamp or epoch ms; unparsable input reads as `now`.
 * @param now - current epoch ms.
 * @returns the bucket.
 */
export function relativeTime(iso: string | number | undefined, now: number): RelativeTime {
  if (iso === undefined) return { unit: 'now' }
  const then = typeof iso === 'number' ? iso : Date.parse(iso)
  if (!Number.isFinite(then)) return { unit: 'now' }
  const minutes = Math.max(0, Math.round((now - then) / 60_000))
  if (minutes < 1) return { unit: 'now' }
  if (minutes < 60) return { unit: 'minutes', n: minutes }
  const hours = Math.round(minutes / 60)
  if (hours < 24) return { unit: 'hours', n: hours }
  return { unit: 'days', n: Math.round(hours / 24) }
}

/**
 * Stable string key of an entity reference (for React keys and equality).
 * @param ref - entity reference.
 * @returns `issue:KEY`, `page:ID`, or `pr:KEY`.
 */
export function refKey(ref: EntityRef): string {
  return ref.kind === 'page' ? `page:${ref.id}` : `${ref.kind}:${ref.key}`
}

/**
 * Whether two references address the same entity.
 * @param a - one reference.
 * @param b - another reference (or none).
 * @returns true when equal.
 */
export function sameRef(a: EntityRef | null | undefined, b: EntityRef | null | undefined): boolean {
  if (a == null || b == null) return a == null && b == null
  return refKey(a) === refKey(b)
}

/**
 * `PROJECT/repo#id` key of a pull request address.
 * @param ref - PR address.
 * @returns the key.
 */
export function prKeyOf(ref: PrRef): string {
  return `${ref.project.toUpperCase()}/${ref.repo}#${String(ref.id)}`
}

/**
 * Sort findings most severe first, then in recording order.
 * @param findings - findings.
 * @returns a sorted copy.
 */
export function sortFindings(findings: readonly ReviewFinding[]): ReviewFinding[] {
  return [...findings].sort((a, b) => severityRank(a.severity) - severityRank(b.severity) || a.at - b.at)
}

/**
 * Basename of a path for compact display.
 * @param path - file path.
 * @returns the last segment.
 */
export function basename(path: string): string {
  const parts = path.split('/')
  /* v8 ignore next -- ?? arm: split always yields at least one segment. */
  return parts[parts.length - 1] ?? path
}

/**
 * Shorten a path for a compact row: keep the first and the last two segments,
 * eliding the middle (`src/…/auth/redirect.ts`).
 * @param path - file path.
 * @param maxSegments - segments shown before eliding (default 4).
 * @returns the shortened path.
 */
export function shortenPath(path: string, maxSegments = 4): string {
  const parts = path.split('/').filter(Boolean)
  if (parts.length <= maxSegments) return parts.join('/')
  /* v8 ignore next -- ?? arm: a path longer than the bound has a first segment. */
  return `${parts[0] ?? ''}/…/${parts.slice(-2).join('/')}`
}

/**
 * Format a byte size.
 * @param size - bytes.
 * @returns e.g. `12 kB`.
 */
export function formatBytes(size: number): string {
  if (size < 1024) return `${String(size)} B`
  if (size < 1024 * 1024) return `${(size / 1024).toFixed(size < 10 * 1024 ? 1 : 0)} kB`
  return `${(size / (1024 * 1024)).toFixed(1)} MB`
}
