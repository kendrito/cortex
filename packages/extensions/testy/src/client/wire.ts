/** Projections of the native Testy MCP JSON used by the integrated workbench. */
import type { JsonValue, TestyToolResult } from '../types.ts'

/** JSON object received from the native engine. */
export type RecordValue = { [key: string]: JsonValue }

/**
 * Replace or omit one optional argument without retaining an undefined JSON value.
 * @param value - current JSON arguments.
 * @param key - field being edited.
 * @param next - replacement value, or undefined to omit the field.
 * @returns a new object with the edit applied.
 */
export function setField(value: RecordValue, key: string, next: JsonValue | undefined): RecordValue {
  return next === undefined ? Object.fromEntries(Object.entries(value).filter(([name]) => name !== key)) : { ...value, [key]: next }
}

/**
 * Hide secret fields in argument summaries while leaving native calls unchanged.
 * @param value - arguments being reviewed.
 * @returns a recursively redacted copy.
 */
export function redactFields(value: JsonValue): JsonValue {
  if (Array.isArray(value)) return value.map(redactFields)
  if (value === null || typeof value !== 'object') return value
  return Object.fromEntries(Object.entries(value).map(([key, item]) =>
    [key, /password|secret|token|apiKey/i.test(key) ? '••••••••' : redactFields(item)]))
}

/**
 * Read a JSON object without treating arrays or null as records.
 * @param value - native JSON value.
 * @returns the object, or an empty object for other JSON kinds.
 */
export function record(value: JsonValue | undefined): RecordValue {
  return value !== null && typeof value === 'object' && !Array.isArray(value) ? value : {}
}

/**
 * Read a string field from engine JSON.
 * @param value - native JSON field.
 * @returns its text, or an empty string when absent or another JSON kind.
 */
export function text(value: JsonValue | undefined): string {
  return typeof value === 'string' ? value : ''
}

/**
 * Read a finite number from engine JSON.
 * @param value - native JSON field.
 * @returns the number, or zero for absent and nonnumeric fields.
 */
export function number(value: JsonValue | undefined): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : 0
}

/**
 * Read object rows from an engine collection.
 * @param value - native JSON collection.
 * @returns its object rows in their original order.
 */
export function rows(value: JsonValue | undefined): RecordValue[] {
  return Array.isArray(value) ? value.filter((item): item is RecordValue => item !== null && typeof item === 'object' && !Array.isArray(item)) : []
}

/**
 * Prefer MCP structured output; older tools return their JSON in a text item.
 * @param result - completed MCP response.
 * @returns structured data or a message object for plain text.
 */
export function resultValue(result: TestyToolResult): JsonValue {
  if (result.structuredContent !== undefined) return result.structuredContent
  const content = result.content.map(record).find(item => item.type === 'text')
  const body = text(content?.text)
  try { return JSON.parse(body) as JsonValue }
  catch (_error) { return { message: body } }
}

/**
 * Only render explicit raster MIME data produced by an MCP image item.
 * @param result - completed MCP response.
 * @returns raster image data URLs in response order.
 */
export function resultImages(result: TestyToolResult): string[] {
  return result.content.map(record).flatMap(item => item.type === 'image'
    && ['image/png', 'image/jpeg', 'image/webp'].includes(text(item.mimeType)) && typeof item.data === 'string'
    ? [`data:${text(item.mimeType)};base64,${item.data}`] : [])
}

/**
 * Throw native operation errors so failed mutations never look successful.
 * @param result - completed MCP response.
 * @returns the successful response unchanged.
 */
export function checkedResult(result: TestyToolResult): TestyToolResult {
  if (result.isError) {
    const value = record(resultValue(result))
    throw new Error(text(value.message) || text(record(value.error).message) || text(value.error) || JSON.stringify(value))
  }
  return result
}

/** Native catalog entry; the input schema is the engine's authoritative form. */
export interface TestyTool {
  name: string
  title: string
  group: string
  description: string
  inputSchema: RecordValue
}

/**
 * Project the MCP catalog into named operations and their argument schemas.
 * @param value - native MCP tool catalog.
 * @returns named operations with engine-owned form schemas.
 */
export function toolCatalog(value: readonly JsonValue[]): TestyTool[] {
  return value.map(record).filter(item => typeof item.name === 'string').map(item => ({
    name: text(item.name), title: text(item.title), group: text(record(item._meta)['cortex/group']),
    description: text(item.description), inputSchema: record(item.inputSchema),
  }))
}

/**
 * Keep test-only fields when copying an imported or generated document.
 * @param value - saved, imported, or generated test record.
 * @returns editable create/update arguments without output-only metadata.
 */
export function testDocument(value: RecordValue): RecordValue {
  return {
    name: text(value.name), intent: text(value.intent), category: text(value.category),
    targetPath: text(value.targetPath), steps: rows(value.steps).map(({ selectorAlternatives, ...step }) => ({
      ...step, ...(Array.isArray(selectorAlternatives) ? { selectorAlternatives } : {}),
    })),
  }
}

/** Every action the native deterministic runner can execute. */
export const STEP_ACTIONS = [
  'click', 'typeText', 'select', 'toggle', 'assertText', 'assertExists', 'assertNotExists', 'assertEnabled',
  'wait', 'screenshot', 'keyPress', 'coordinateClick', 'expand', 'collapse', 'realizeItem', 'scrollIntoView',
  'scrollPercent', 'assertProperty', 'assertItemExists', 'assertItemAbsent', 'gridEditCell', 'gridCommitRow', 'gridCancelRow',
] as const

/**
 * Export user-selected test JSON through a short-lived browser download.
 * @param name - document title used for a sanitized filename.
 * @param value - selected test or run evidence to export.
 */
export function downloadJson(name: string, value: JsonValue): void {
  downloadFile(`${name.replace(/[^a-z0-9_-]/gi, '_') || 'testy'}.json`, JSON.stringify(value, null, 2), 'application/json')
}

/**
 * Download the native engine's standalone report with its embedded evidence.
 * @param filename - native report filename, sanitized for a browser download.
 * @param html - escaped, self-contained HTML produced by the native report renderer.
 */
export function downloadHtmlReport(filename: string, html: string): void {
  downloadFile(filename.replace(/[^a-z0-9_.-]/gi, '_') || 'testy-report.html', html, 'text/html')
}

function downloadFile(filename: string, content: string, mimeType: string): void {
  const url = URL.createObjectURL(new Blob([content], { type: mimeType }))
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = filename
  anchor.click()
  URL.revokeObjectURL(url)
}
