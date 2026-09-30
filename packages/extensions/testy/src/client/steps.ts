/** Steps as native Studio shows them: a verb ("Click", "Type", "Check") and a sentence ("Ada Lovelace into Customer name"). */
import { number, text, type RecordValue } from './wire.ts'

/** One run of text in a step sentence; values render in the monospace face, placeholders dimmed. */
export interface SentencePart { text: string; kind: 'text' | 'control' | 'value' | 'placeholder' }

/** A step as people read it. */
export interface StepSentence { verb: string; check: boolean; parts: SentencePart[]; plain: string }

const MAX_NAME = 48
const MAX_VALUE = 60
const CHECKS = new Set(['assertText', 'assertExists', 'assertNotExists', 'assertEnabled', 'assertProperty', 'assertItemExists', 'assertItemAbsent'])

/**
 * Whether an action checks the app rather than acting on it.
 * @param action - step action identifier.
 * @returns true for assertions.
 */
export function isCheck(action: string): boolean {
  return CHECKS.has(action)
}

function oneLine(value: string): string {
  return value.split(/\s+/).filter(Boolean).join(' ')
}

function clip(value: string, max: number): string {
  return value.length <= max ? value : `${value.slice(0, max - 1)}…`
}

/**
 * Turn an automation id into words: "CustomerName" → "Customer name", "OKButton" → "OK button".
 * @param id - automation id or identifier.
 * @returns sentence-case words, keeping acronyms.
 */
export function humanize(id: string): string {
  const source = id.trim()
  const words: string[] = []
  let current = ''
  for (let i = 0; i < source.length; i++) {
    const c = source[i] ?? ''
    if ('_- .'.includes(c)) {
      if (current) { words.push(current); current = '' }
      continue
    }
    const upper = c !== c.toLowerCase()
    const previous = source[i - 1] ?? ''
    const next = source[i + 1] ?? ''
    const boundary = current.length > 0 && upper && (previous === previous.toLowerCase() || (next !== '' && next !== next.toUpperCase()))
    const digit = /\d/.test(c)
    if (boundary || (current.length > 0 && digit !== /\d/.test(previous))) { words.push(current); current = '' }
    current += c
  }
  if (current) words.push(current)
  return words.map((word, index) => {
    if (word.length > 1 && word === word.toUpperCase() && /[A-Z]/.test(word)) return word
    return index === 0 ? word.charAt(0).toUpperCase() + word.slice(1) : word.charAt(0).toLowerCase() + word.slice(1)
  }).join(' ')
}

/**
 * The control's friendly name from its selector.
 * @param selector - Testy selector (`id:`, `name:`, `path:` or `query:`).
 * @returns a readable name, or an empty string when there is no selector.
 */
export function controlName(selector: string): string {
  const value = selector.trim()
  if (!value) return ''
  if (value.startsWith('id:')) return humanize(value.slice(3))
  if (value.startsWith('name:')) return clip(oneLine(value.slice(5)), MAX_NAME)
  if (value.startsWith('path:')) return 'a control found by its position'
  if (value.startsWith('query:')) {
    try {
      const query = JSON.parse(value.slice(6)) as unknown
      if (query && typeof query === 'object' && !Array.isArray(query)) {
        const fields = query as Record<string, unknown>
        if (typeof fields.label === 'string') return clip(oneLine(fields.label), MAX_NAME)
        if (typeof fields.id === 'string') return humanize(fields.id)
      }
    } catch { /* fall through */ }
    return 'a control found by a query'
  }
  return clip(oneLine(value), MAX_NAME)
}

function details(value: string): Record<string, unknown> | null {
  try {
    const parsed = JSON.parse(value) as unknown
    return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed as Record<string, unknown> : null
  } catch { return null }
}

/**
 * Build the sentence native Studio shows for a step.
 * @param step - saved step.
 * @returns verb, sentence parts and a plain-text form with quoted values.
 */
export function stepSentence(step: RecordValue): StepSentence {
  const action = text(step.action) || 'click'
  const selector = text(step.selector)
  const name = controlName(selector)
  const target: SentencePart = name ? { text: name, kind: 'control' } : { text: 'choose a control', kind: 'placeholder' }
  const secret = /password|passwd|secret|token|apikey/i.test(selector)
  const raw = text(step.value)
  const value = secret && raw ? '••••••' : clip(oneLine(raw), MAX_VALUE)
  const T = (content: string): SentencePart => ({ text: content, kind: 'text' })
  const V = (content: string): SentencePart => ({ text: content, kind: 'value' })
  const P = (content: string): SentencePart => ({ text: content, kind: 'placeholder' })
  const info = details(raw)
  const field = (key: string): string => {
    const item = info?.[key]
    return typeof item === 'string' || typeof item === 'number' || typeof item === 'boolean' ? String(item) : ''
  }
  const sentence = ((): [string, SentencePart[]] => {
    switch (action) {
      case 'click': return ['Click', [target]]
      case 'typeText': return value ? ['Type', [V(value), T(' into '), target]] : ['Clear', [target]]
      case 'select': return ['Select', value ? [V(value), T(' in '), target] : [target]]
      case 'toggle': return ['Toggle', [target]]
      case 'assertText': return ['Check', raw.startsWith('contains:') ? [target, T(' contains '), V(clip(oneLine(raw.slice(9)), MAX_VALUE))]
        : value ? [target, T(' says '), V(value)] : [target, T(' says '), P('the expected text')]]
      case 'assertExists': return ['Check', [target, T(' is there')]]
      case 'assertNotExists': return ['Check', [target, T(' is gone')]]
      case 'assertEnabled': return ['Check', [target, T(' is enabled')]]
      case 'assertProperty': return ['Check', field('property') ? [target, T(` ${field('property')} is `), V(field('expected') || field('value'))] : [target, T(' has '), V(value)]]
      case 'assertItemExists': return ['Check', [target, T(' has '), V(field('item') || field('text') || value)]]
      case 'assertItemAbsent': return ['Check', [target, T(" doesn't have "), V(field('item') || field('text') || value)]]
      case 'wait': return ['Wait', [T(raw ? `${raw} ms` : `${number(step.timeoutMs) || 1000} ms`)]]
      case 'screenshot': return ['Capture', [T('a screenshot')]]
      case 'keyPress': return ['Press', value ? [V(value)] : [P('choose keys')]]
      case 'coordinateClick': return ['Click', [T(`at ${number(step.x)}, ${number(step.y)}`)]]
      case 'expand': return ['Expand', [target]]
      case 'collapse': return ['Collapse', [target]]
      case 'realizeItem': return ['Find', [V(field('item') || field('text') || value), T(' in '), target]]
      case 'scrollIntoView': return ['Scroll', [target, T(' into view')]]
      case 'scrollPercent': return ['Scroll', [target, T(' to '), V(field('vertical') ? `${field('vertical')}%` : value)]]
      case 'gridEditCell': return ['Edit', field('column') ? [T('cell '), V(field('column')), T(' of row '), V(field('row')), T(' in '), target, T(' to '), V(field('text') || field('value'))] : [T('a cell in '), target]]
      case 'gridCommitRow': return ['Save', field('row') ? [T('row '), V(field('row')), T(' in '), target] : [T('the row in '), target]]
      case 'gridCancelRow': return ['Cancel', field('row') ? [T('edits to row '), V(field('row')), T(' in '), target] : [T('the row edit in '), target]]
      default: return ['Do', [target]]
    }
  })()
  const [verb, parts] = sentence
  const plain = `${verb} ${parts.map(part => part.kind === 'value' ? `“${part.text}”` : part.text).join('')}`.trim()
  return { verb, check: isCheck(action), parts, plain }
}
