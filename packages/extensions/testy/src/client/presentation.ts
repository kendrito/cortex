/** Human-facing names and result shapes for the native workflow catalog. */
import type { Translate } from '@cortex/client-ui-slots'
import type { JsonValue } from '../types.ts'
import type { TestyKey } from './locales.ts'
import { record, text, type RecordValue } from './wire.ts'

const fieldNames: Record<string, TestyKey> = {
  pid: 'field.pid', testId: 'field.testId', testIds: 'field.testIds', suiteId: 'field.suiteId',
  runId: 'field.runId', jobId: 'field.jobId', taskId: 'field.taskId', machineId: 'field.machineId',
  scheduleId: 'field.scheduleId', recordingId: 'field.recordingId', lifecycleId: 'field.lifecycleId',
  exe: 'field.exe', args: 'field.args', mode: 'mode', probe: 'probe', csv: 'field.csv',
  supportsImages: 'field.supportsImages', liveReview: 'field.liveReview', aiDirectedExecution: 'field.aiDirectedExecution',
}

/**
 * Turn catalog identifiers into sentence-case labels while preserving familiar acronyms.
 * @param name - engine-owned property or operation identifier.
 * @returns readable fallback label without changing its wire identifier.
 */
export function readableName(name: string): string {
  const words = name.replace(/([a-z\d])([A-Z])/g, '$1 $2').replaceAll('_', ' ')
    .replace(/\b(id|ids|pid|vm|csv|json|uia|wpf|ai)\b/gi, word => word.toUpperCase())
    .replace(/\bMs\b/g, '(ms)')
  return words.charAt(0).toUpperCase() + words.slice(1)
}

/**
 * Prefer engine-authored titles, then localized common fields and a readable fallback.
 * @param name - original schema property name.
 * @param schema - field schema which may carry a title.
 * @param t - workbench translator.
 * @returns label shared by forms and result columns.
 */
export function fieldLabel(name: string, schema: RecordValue, t: Translate<TestyKey>): string {
  return text(schema.title) || (fieldNames[name] ? t(fieldNames[name]) : readableName(name))
}

/**
 * Keep app names concise while making otherwise identical windows distinguishable.
 * @param app - observed candidate or running application.
 * @param apps - the choices displayed together.
 * @param t - workbench translator.
 * @param technical - whether process identifiers were explicitly requested.
 * @returns unique human-readable label for duplicate titles.
 */
export function applicationLabel(app: RecordValue, apps: RecordValue[], t: Translate<TestyKey>, technical = false): string {
  const title = text(app.title) || text(app.processName) || t('target')
  const duplicate = apps.filter(item => (text(item.title) || text(item.processName) || t('target')) === title).length > 1
  if (typeof app.pid === 'number' && (duplicate || technical)) return t('candidateProcess', { title, pid: app.pid })
  if (duplicate) return `${title} (${text(app.exe) || text(app.id)})`
  return title
}

/**
 * The app's own name from its window title: "Untitled - Notepad" → "Notepad", "TestLab — Customer Desk" → "Customer Desk".
 * @param app - running application.
 * @returns the last title segment, or the process name.
 */
export function appDisplayName(app: RecordValue): string {
  const title = text(app.title).trim()
  const segments = title.split(/\s+[—–-]\s+/).map(part => part.trim()).filter(Boolean)
  return segments.at(-1) || text(app.processName) || title
}

/**
 * Two-letter badge for the app card, as in native Studio ("Customer Desk" → "CD").
 * @param name - app display name.
 * @returns up to two uppercase initials.
 */
export function appInitials(name: string): string {
  const words = name.split(/[\s._-]+/).filter(word => /[a-z0-9]/i.test(word))
  return (words.length > 1 ? `${words[0]?.[0] ?? ''}${words[1]?.[0] ?? ''}` : name.replace(/[^a-z0-9]/gi, '').slice(0, 2)).toUpperCase()
}

/**
 * When something happened, the way native Studio says it: "Today 8:12 AM", "Yesterday 4:05 PM", or a date.
 * @param iso - ISO timestamp.
 * @param now - the current time.
 * @returns a short local time, or an empty string for an invalid timestamp.
 */
export function friendlyTime(iso: string, now = new Date()): string {
  const when = new Date(iso)
  if (Number.isNaN(when.getTime())) return ''
  const time = when.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })
  const day = (value: Date) => new Date(value.getFullYear(), value.getMonth(), value.getDate()).getTime()
  const days = Math.round((day(now) - day(when)) / 86_400_000)
  if (days === 0) return `Today ${time}`
  if (days === 1) return `Yesterday ${time}`
  return when.toLocaleDateString(undefined, { month: 'short', day: 'numeric', ...(when.getFullYear() === now.getFullYear() ? {} : { year: 'numeric' }) }) + ` ${time}`
}

const STATUS_KEYS: Record<string, TestyKey> = {
  passed: 'status.passed', failed: 'status.failed', cancelled: 'status.cancelled', running: 'status.running',
  pending: 'status.pending', skipped: 'status.skipped',
}

/**
 * Dictionary key for a run status sentence ("Passed · {when}").
 * @param status - native run status.
 * @returns the matching key, or a neutral one for unknown statuses.
 */
export function runStatusKey(status: string): TestyKey {
  return STATUS_KEYS[status] ?? 'status.other'
}

const WORD_KEYS: Record<string, TestyKey> = {
  passed: 'word.passed', failed: 'word.failed', cancelled: 'word.cancelled', running: 'word.running', pending: 'word.pending', skipped: 'word.skipped',
}

/**
 * One word for a status, as in native Studio's result pills ("Passed", "Failed", "Stopped").
 * @param status - native run or step status.
 * @returns the dictionary key for the word.
 */
export function statusWordKey(status: string): TestyKey {
  return WORD_KEYS[status] ?? 'word.pending'
}

/**
 * Extract completed task and CLI results, retaining a nonzero native exit code.
 * @param value - structured native tool result or a polled workflow task.
 * @returns operation data and the native exit code when present.
 */
export function operationData(value: JsonValue): { data: RecordValue; exitCode?: number } {
  const envelope = record(value)
  const task = typeof envelope.taskId === 'string'
  const result = task && envelope.result !== null && envelope.result !== undefined ? record(envelope.result) : envelope
  if (typeof result.exitCode === 'number' && result.result !== undefined) {
    return { data: record(result.result), exitCode: result.exitCode }
  }
  return { data: result }
}

/**
 * Bring saved build profiles and virtual machines into the operation form's named fields.
 * @param row - native workflow record.
 * @returns stable visible fields and reusable identifiers, with nested details retained.
 */
export function workflowRecord(row: RecordValue): RecordValue {
  const profile = record(row.profile)
  if (!Object.keys(profile).length) return row.vmId === undefined ? row : { ...row, machineId: row.vmId }
  return { ...row, ...profile, lifecycleId: profile.id ?? row.lifecycleId ?? '', testId: record(row.test).id ?? '',
    exe: profile.executable ?? '', instructions: profile.preparationInstructions ?? '',
    mode: profile.replay === true ? 'replay' : 'ai', args: row.targetArguments ?? [],
  }
}

/**
 * Summarize values in result tables without embedding raw JSON into cells.
 * @param value - native JSON field.
 * @param t - workbench translator.
 * @returns a compact display value; full JSON remains available separately.
 */
export function displayValue(value: JsonValue | undefined, t: Translate<TestyKey>): string {
  if (value === null || value === undefined || value === '') return '—'
  if (typeof value === 'boolean') return t(value ? 'yes' : 'no')
  if (Array.isArray(value)) return t('itemsCount', { count: value.length })
  if (typeof value === 'object') return text(value.name) || text(value.title) || t('details')
  return String(value)
}
