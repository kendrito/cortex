/**
 * The pinned-editor preference: one browser-local bit shared by the session
 * header (the pin toggle beside the view tabs) and the session body (the
 * split layout it selects). Pinning keeps the Code view permanently open in
 * its own pane with the active view beside it, which is why the bit lives
 * outside the per-session view store: the arrangement is the user's, not a
 * session's.
 */

/** Browser-local storage key; absence means unpinned. */
const STORAGE_KEY = 'cortex.editor.pinned'

/** Browser-local storage key for the split ratio (editor pane fraction). */
const RATIO_KEY = 'cortex.editor.splitRatio'

/** Ratio clamp: the editor keeps the majority range, chat stays readable. */
const MIN_RATIO = 0.25
const MAX_RATIO = 0.78

const listeners = new Set<() => void>()

/** Cached bit so getSnapshot stays referentially stable for the store hook. */
let pinned = readStorage()

/** Cached editor-pane fraction of the split width. */
let ratio = readRatio()

function readStorage(): boolean {
  try {
    return window.localStorage.getItem(STORAGE_KEY) === '1'
  } catch {
    return false
  }
}

function readRatio(): number {
  try {
    const stored = Number(window.localStorage.getItem(RATIO_KEY))
    return Number.isFinite(stored) && stored > 0 ? clampRatio(stored) : 0.58
  } catch {
    return 0.58
  }
}

function clampRatio(value: number): number {
  return Math.min(MAX_RATIO, Math.max(MIN_RATIO, value))
}

/**
 * The editor pane's fraction of the split width.
 * @returns the clamped ratio.
 */
export function editorSplitRatio(): number {
  return ratio
}

/**
 * Set the editor pane's fraction of the split width (clamped) and notify.
 * @param next - the drag-derived fraction.
 */
export function setEditorSplitRatio(next: number): void {
  const clamped = clampRatio(next)
  if (clamped === ratio) return
  ratio = clamped
  try {
    window.localStorage.setItem(RATIO_KEY, String(clamped))
  } catch {
    // Private-mode storage failures keep the in-memory value for this page.
  }
  for (const listener of listeners) listener()
}

/**
 * Whether the editor pane is pinned open beside the active view.
 * @returns the pin bit.
 */
export function editorPinned(): boolean {
  return pinned
}

/**
 * Flip the pinned bit and notify subscribers.
 */
export function toggleEditorPinned(): void {
  pinned = !pinned
  try {
    if (pinned) window.localStorage.setItem(STORAGE_KEY, '1')
    else window.localStorage.removeItem(STORAGE_KEY)
  } catch {
    // Private-mode storage failures keep the in-memory bit for this page.
  }
  for (const listener of listeners) listener()
}

/**
 * Subscribe to pin changes (useSyncExternalStore contract).
 * @param listener - invoked after every toggle.
 * @returns the unsubscriber.
 */
export function subscribeEditorPinned(listener: () => void): () => void {
  listeners.add(listener)
  return () => { listeners.delete(listener) }
}
