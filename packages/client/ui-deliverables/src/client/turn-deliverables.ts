/**
 * Turn-scoped produced-file Definition and readers. Client-only and
 * model-free: the vocabulary is the mutation tools' own follow-along
 * `locations`, never the closing prose.
 */
import type {
  ConversationNodeDefinition, ToolResultNode,
} from '@cortex/client-runtime/client'
import { isAppendSurfaceEvent } from '@cortex/client-runtime/client'
import type { MarkdownFileMentions } from '@cortex/client-ui-primitives'
import type { TurnTailOwnerProps } from '@cortex/client-ui-conversation/client'

interface ProducedPath {
  readonly seq: number
  readonly path: string
}

/** What one settled root call did, by declared render intent, never tool name. */
export interface TurnTouch {
  readonly seq: number
  /** Card-declared intent: a terminal is `execute`; a generic card carries its kind. */
  readonly intent: 'read' | 'search' | 'execute' | 'fetch' | 'delete' | 'move' | 'other'
  /** True when the tool result reported an error. */
  readonly failed: boolean
  /** First follow-along location, kept for read tooltips. */
  readonly path?: string
}

/** Immutable produced-file facts published against one Turn. */
export interface DeliverablesTurnData {
  readonly produced: readonly ProducedPath[]
  /** Non-mutation activity settled in this Turn (mutations live in `produced`). */
  readonly touches?: readonly TurnTouch[]
}

declare module '@cortex/client-runtime/client' {
  interface ConversationTurnDataMap {
    /** Successful mutation paths accumulated in this Turn. */
    deliverables: DeliverablesTurnData
  }
}

interface DeliverablesState extends DeliverablesTurnData {
  readonly turn: number
  readonly touches: readonly TurnTouch[]
  readonly calls: ReadonlyMap<string, ToolResultNode['callView']>
}

/**
 * Paths a call view reports having created or changed, by render intent rather
 * than tool name: a diff card, or a generic card whose kind is `edit` (the
 * shape `str_replace_editor`'s insert presents). Every other card produces
 * nothing to open — a read looked, a delete removed, a terminal ran. Only
 * root call views enter this Turn accumulator; nested Code Mode dispatches
 * preserve the pre-assembly behavior and do not contribute independently.
 */
function producedPaths(view: ToolResultNode['callView']): readonly string[] {
  if (view === null) return []
  if (view.card === 'diff') return (view.locations ?? []).map(location => location.path)
  if (view.card === 'generic' && view.kind === 'edit') {
    return (view.locations ?? []).map(location => location.path)
  }
  return []
}

/**
 * Intent of a call that produced nothing to open: a terminal ran, a read
 * looked, a search matched. Mutation intents return undefined — `produced`
 * already carries them and one call must never be reported twice.
 * @param view - the call's render-intent card, when its call was in-window.
 * @returns the touch intent, or undefined for mutations.
 */
function touchIntent(view: ToolResultNode['callView']): TurnTouch['intent'] | undefined {
  if (view === null) return 'other'
  if (view.card === 'terminal') return 'execute'
  if (view.card === 'diff') return undefined
  const kind = view.kind ?? 'other'
  if (kind === 'edit') return undefined
  return kind
}

/** First follow-along location path on a generic card, for read tooltips. */
function touchPath(view: ToolResultNode['callView']): string | undefined {
  if (view === null || view.card !== 'generic') return undefined
  return (view.locations ?? [])[0]?.path
}

/**
 * Files produced by one Turn data value.
 *
 * The source is the mutation tools' own follow-along `locations`, not the
 * closing prose: a produced file must be listed whether or not the model
 * remembered to name it. A mutation is recognized by render intent, not by
 * tool name — a diff card, or a generic card whose `kind` is `edit` (the shape
 * `str_replace_editor`'s insert presents) — so a new mutation tool joins by
 * declaring what it does. Reads contribute nothing (looking at a file does not
 * produce it), and neither do deletes (there is nothing left to open) or
 * failed calls. Paths keep first-seen order and appear once, so a file written
 * and then edited in the same turn is one entry.
 *
 * The Conversation Location index owns turn membership before this function
 * runs, so paths cannot spill across turns and this derivation does not infer
 * boundaries from neighboring presentation Nodes.
 * @param data - engine-published Deliverables data for one Turn.
 * @param seq - closing Assistant seq; later Tool settlements are excluded.
 * @returns Produced paths in first-seen order; empty when the turn wrote nothing.
 */
export function producedForClosing(
  data: Readonly<DeliverablesTurnData> | undefined,
  seq = Number.POSITIVE_INFINITY,
): readonly string[] {
  if (data === undefined) return []
  const paths: string[] = []
  const seen = new Set<string>()
  for (const produced of data.produced) {
    if (produced.seq > seq || seen.has(produced.path)) continue
    seen.add(produced.path)
    paths.push(produced.path)
  }
  return paths
}

/** Aggregated non-mutation activity for one closing turn. */
export interface TurnActivity {
  readonly counts: Readonly<Partial<Record<TurnTouch['intent'], number>>>
  readonly failed: number
  /** Deduped read paths, first-seen order, for the read counter's tooltip. */
  readonly readPaths: readonly string[]
}

/**
 * Aggregate a Turn's touch trace up to the closing assistant.
 * @param data - engine-published Deliverables data for one Turn.
 * @param seq - closing Assistant seq; later settlements are excluded.
 * @returns intent counts, failure count, and the read-path list.
 */
export function activityForClosing(
  data: Readonly<DeliverablesTurnData> | undefined,
  seq = Number.POSITIVE_INFINITY,
): TurnActivity {
  const counts: Partial<Record<TurnTouch['intent'], number>> = {}
  const readPaths: string[] = []
  const seen = new Set<string>()
  let failed = 0
  for (const touch of data?.touches ?? []) {
    if (touch.seq > seq) continue
    if (touch.failed) { failed += 1; continue }
    counts[touch.intent] = (counts[touch.intent] ?? 0) + 1
    if (touch.intent === 'read' && touch.path !== undefined && !seen.has(touch.path)) {
      seen.add(touch.path)
      readPaths.push(touch.path)
    }
  }
  return { counts, failed, readPaths }
}

/**
 * Claim the turn-tail chain only when its closing turn produced files.
 * @param owner - Turn-tail owner currency for the closing assistant.
 * @returns Produced paths as the component's match, or null to decline before mount.
 */
export function selectProducedFiles(owner: TurnTailOwnerProps): readonly string[] | null {
  const data = owner.turn.data.get('deliverables')
  const paths = producedForClosing(data, owner.seq)
  if (paths.length > 0) return paths
  const activity = activityForClosing(data, owner.seq)
  const touched = Object.values(activity.counts).some(count => count > 0) || activity.failed > 0
  return touched ? paths : null
}

/** Turn-local successful mutation accumulator; it publishes no view Node. */
export const deliverablesDefinition: ConversationNodeDefinition<DeliverablesState> = {
  kind: 'deliverables',
  match: (event) => {
    if (event.type === 'turn/start') return { id: String(event.data.turn), role: 'start' }
    if (event.type === 'tool/call') return { id: String(event.data.turn), role: 'update' }
    if (event.type === 'tool/result' && isAppendSurfaceEvent(event)) {
      return { id: String(event.data.turn), role: 'update' }
    }
    return null
  },
  start: (_context, match) => {
    if (match.event.type !== 'turn/start') throw new Error('deliverables start requires turn/start')
    return { turn: match.event.data.turn, calls: new Map(), produced: [], touches: [] }
  },
  update: (context, match) => {
    if (match.event.type === 'tool/call') {
      const calls = new Map(context.state.calls)
      calls.set(
        String(match.event.data.callId),
        match.view?.for === 'call' ? match.view.view : null,
      )
      return { ...context.state, calls }
    }
    if (match.event.type !== 'tool/result') return context.state
    const result = match.event.data.message.content[0]
    const callId = String(match.event.data.message.source.callId)
    const view = context.state.calls.get(callId) ?? null
    if (result.isError === true) {
      const touch: TurnTouch = { seq: match.event.seq, intent: touchIntent(view) ?? 'other', failed: true }
      return { ...context.state, touches: [...context.state.touches, touch] }
    }
    const additions = producedPaths(view).map(path => ({ seq: match.event.seq, path }))
    if (additions.length > 0) {
      return { ...context.state, produced: [...context.state.produced, ...additions] }
    }
    const intent = touchIntent(view)
    if (intent === undefined) return context.state
    const path = touchPath(view)
    const touch: TurnTouch = { seq: match.event.seq, intent, failed: false, ...(path === undefined ? {} : { path }) }
    return { ...context.state, touches: [...context.state.touches, touch] }
  },
  buildLocationData: (context, scope) => scope !== 'turn' || context.state === undefined
    ? null
    : {
      kind: 'turn',
      turn: context.state.turn,
      key: 'deliverables',
      value: { produced: context.state.produced, touches: context.state.touches },
    },
}

/**
 * Trailing path segment, the part that identifies the file at a glance.
 * @param path - Slash- or backslash-separated path.
 * @returns The final segment, or the whole string when separator-free.
 */
export function basename(path: string): string {
  const at = Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\'))
  return at === -1 ? path : path.slice(at + 1)
}

/**
 * File-mention vocabulary over one turn's produced paths, for the closing
 * message's prose: an inline-code token opens the file it names. A token
 * resolves by exact path, or by being exactly the basename of exactly one
 * produced path — a basename two paths share stays inert rather than
 * guessing, so a mention link can never open the wrong file or 404.
 * @param paths - The turn's produced paths (tool order, already deduped).
 * @param openFile - The chat view's file opener.
 * @param label - Localizes the accessible open-label for a resolved path.
 * @returns The resolver MarkdownText consumes; the full path rides `title`,
 * the same disambiguator the row's chips carry.
 */
export function producedFileMentions(
  paths: readonly string[],
  openFile: (path: string) => void,
  label: (path: string) => string,
): MarkdownFileMentions {
  return {
    resolve(value) {
      const path = paths.includes(value) ? value : onlyPathWithBasename(paths, value)
      if (path === undefined) return undefined
      return { open: () => { openFile(path) }, label: label(path), title: path }
    },
  }
}

/** The single produced path whose basename is exactly `value`, else undefined. */
function onlyPathWithBasename(paths: readonly string[], value: string): string | undefined {
  const matches = paths.filter(path => basename(path) === value)
  return matches.length === 1 ? matches[0] : undefined
}
