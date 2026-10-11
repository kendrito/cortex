/**
 * The progress board's pure model, shared by the host fold and the browser card: one
 * session's turn folded from its events, the estimate, and the rows the card draws.
 * No imports, no clock: every time is passed in, so the host fold stays a pure replay.
 */

export type Phase = 'idle' | 'working' | 'done' | 'stopped' | 'failed'

/** One todo_write item, as the todo tool records it. */
export interface TodoItem {
  content: string
  status: 'pending' | 'in_progress' | 'completed'
}

/** One session's board: the turn it is tracking and the work done in it. */
export interface BoardState {
  v: 1
  phase: Phase
  startedAt: number
  endedAt: number
  /** Tool calls this turn. */
  tools: number
  /** Tool calls since the todo list last changed. */
  sinceTaskChange: number
  /** What the agent is doing right now ("Editing app.ts"). */
  activity: string
  /** The latest todo_write list; an unfinished one carries into the next turn. */
  todos: TodoItem[]
  /** Where the todo list (or agents) took over the bar; -1 until one has. */
  taskBase: number
  /** The bar never draws below this while working, so it never slides back. */
  floor: number
  /** Tool calls a turn usually takes in this session. */
  expected: number
  /** Tool counts of this session's recent finished turns. */
  history: number[]
}

/** A subagent as the card sees it: its catalog entry joined with its own board. */
export interface AgentView {
  id: string
  label: string
  depth: number
  isRunning: boolean
  phase: Phase
  activity: string
  tools: number
  startedAt: number
  endedAt: number
  todos: TodoItem[]
}

export type Tone = 'accent' | 'plain' | 'dim' | 'success' | 'warning' | 'error'

export interface BoardRow {
  key: string
  depth: number
  mark: 'done' | 'active' | 'paused' | 'pending' | 'running' | 'stopped' | 'failed' | 'more'
  text: string
  tail: string
  tone: Tone
  isFinished: boolean
  isAgent: boolean
}

export interface Board {
  p: number
  tone: Tone
  caption: string
  elapsed: string
  rows: BoardRow[]
}

/** The bar stops short of full until the work really ends. */
const CEILING = 0.97

export const IDLE: BoardState = {
  v: 1,
  phase: 'idle',
  startedAt: 0,
  endedAt: 0,
  tools: 0,
  sinceTaskChange: 0,
  activity: 'Thinking',
  todos: [],
  taskBase: -1,
  floor: 0,
  expected: 8,
  history: [],
}

/** A session event as the fold reads it: its type, time and payload. */
export interface FoldEvent {
  type: string
  time: number
  data: unknown
}

/**
 * Folds one committed session event into the board. Unrelated events return the
 * same state object, as a session projection's `apply` must.
 */
export function foldEvent(state: BoardState, event: FoldEvent): BoardState {
  const data = (event.data ?? {}) as Record<string, unknown>

  switch (event.type) {
    case 'turn/start': {
      const hasOpen = state.todos.some(t => t.status !== 'completed')

      return {
        ...IDLE,
        phase: 'working',
        startedAt: event.time,
        todos: hasOpen ? state.todos : [],
        taskBase: hasOpen ? 0 : -1,
        expected: expectedTools(state.history),
        history: state.history,
      }
    }
    case 'tool/call': {
      if (state.phase !== 'working') return state
      const name = typeof data.name === 'string' ? data.name : 'a tool'

      return {
        ...state,
        tools: state.tools + 1,
        sinceTaskChange: state.sinceTaskChange + 1,
        activity: describeTool(name, parseArguments(data.arguments)),
      }
    }
    case 'todo/write': {
      const todos = readTodos(data.todos)
      if (state.phase !== 'working') return { ...state, todos }
      // The bar never draws below what it showed before the list changed.
      const floor = estimate(state, [], event.time)
      const hasOpen = todos.some(t => t.status !== 'completed')
      const taskBase = state.taskBase >= 0 || !hasOpen ? state.taskBase : Math.min(0.5, floor)

      return { ...state, todos, sinceTaskChange: 0, taskBase, floor }
    }
    case 'turn/end': {
      if (state.phase !== 'working') return state
      const reason = (data.reason ?? {}) as { kind?: unknown }
      const phase = endPhase(reason.kind)
      const history = phase === 'done' && state.tools > 0 ? [...state.history, state.tools].slice(-10) : state.history

      return { ...state, phase, endedAt: event.time, floor: estimate(state, [], event.time), history }
    }
    default:
      return state
  }
}

function endPhase(kind: unknown): Phase {
  if (kind === 'completed' || kind === 'forked') return 'done'
  if (kind === 'aborted' || kind === 'interrupted') return 'stopped'
  return 'failed'
}

/** Whether anything is still at work: this session's turn, or any of its agents. */
export function isRunning(state: BoardState, agents: readonly AgentView[]): boolean {
  return state.phase === 'working' || agents.some(a => a.isRunning)
}

/**
 * How far along the work is, 0 to 1. With no todo list and no agents it rises with
 * tool calls (and slowly with time), scaled by how many calls a turn usually takes;
 * once there is a list (or, failing that, agents) the rest of the bar is split
 * across its items. A turn that ended with items open is only as far as its
 * finished items.
 */
export function estimate(state: BoardState, agents: readonly AgentView[], now: number): number {
  if (!isRunning(state, agents)) {
    if (state.phase !== 'done') return state.floor
    const done = state.todos.filter(t => t.status === 'completed').length

    return done === state.todos.length ? 1 : done / state.todos.length
  }

  const seconds = Math.max(0, now - state.startedAt) / 1000
  const work = state.tools + seconds / 30
  const byWork = 1 - Math.exp((-1.1 * work) / Math.max(1, state.expected))
  const units = state.todos.length > 0
    ? todoProgress(state.todos, state.sinceTaskChange)
    : agents.length > 0 ? agentProgress(agents) : undefined
  const p = state.taskBase >= 0 && units !== undefined ? state.taskBase + (1 - state.taskBase) * units : byWork

  return Math.max(state.floor, Math.min(CEILING, p))
}

/** Finished items count whole; a running one fills as calls are made on it. */
export function todoProgress(todos: readonly TodoItem[], sinceTaskChange: number): number {
  if (todos.length === 0) return 0
  const done = todos.filter(t => t.status === 'completed').length
  const active = todos.filter(t => t.status === 'in_progress').length
  const creep = 1 - Math.exp(-sinceTaskChange / 6)
  const partial = active > 0 ? active * (0.1 + 0.8 * creep) : done < todos.length ? 0.25 * creep : 0

  return Math.min(1, (done + partial) / todos.length)
}

/** Each agent's share: whole once it ends, else by its list or its calls. */
export function agentProgress(agents: readonly AgentView[]): number {
  if (agents.length === 0) return 0
  const shares = agents.map(a =>
    !a.isRunning ? 1 : a.todos.length > 0 ? todoProgress(a.todos, 3) : 0.9 * (1 - Math.exp(-a.tools / 10)))

  return shares.reduce((sum, n) => sum + n, 0) / agents.length
}

export function expectedTools(history: readonly number[]): number {
  if (history.length === 0) return IDLE.expected
  return Math.max(4, history.reduce((sum, n) => sum + n, 0) / history.length)
}

/** The board for this moment, or undefined while there is nothing to show. */
export function buildBoard(
  state: BoardState,
  agents: readonly AgentView[],
  now: number,
  maxRows = 14,
): Board | undefined {
  if (state.phase === 'idle' && agents.length === 0) return undefined

  const isActive = isRunning(state, agents)
  const ended = Math.max(state.endedAt, ...agents.map(a => a.endedAt))
  const elapsed = state.startedAt > 0 ? clockText((isActive ? Math.max(now, state.startedAt) : ended) - state.startedAt) : ''
  const isUnfinished = state.todos.some(t => t.status !== 'completed')
  const rows = [...state.todos.map((t, i) => todoRow(t, i, isActive)), ...agentRows(agents, now)]

  return {
    p: estimate(state, agents, now),
    tone: isActive
      ? 'accent'
      : state.phase === 'failed' ? 'error' : state.phase === 'stopped' || isUnfinished ? 'warning' : 'success',
    caption: caption(state, agents),
    elapsed,
    rows: squeeze(rows, Math.max(0, maxRows - 1)),
  }
}

function caption(state: BoardState, agents: readonly AgentView[]): string {
  const running = agents.filter(a => a.isRunning).length
  const calls = `${state.tools} tool call${state.tools === 1 ? '' : 's'}`
  const done = state.todos.filter(t => t.status === 'completed').length

  if (state.phase === 'working') {
    if (state.todos.length === 0) return state.activity
    const current = state.todos.find(t => t.status === 'in_progress')

    return `Step ${Math.min(state.todos.length, done + 1)}/${state.todos.length} · ${current?.content ?? state.activity}`
  }
  if (running > 0) return `Waiting on ${running} agent${running === 1 ? '' : 's'}`
  if (state.phase === 'stopped') return `Stopped · ${calls}`
  if (state.phase === 'failed') return 'Ended with an error'
  if (done < state.todos.length) return `Turn ended · ${done}/${state.todos.length} tasks done`

  return `Done · ${calls}`
}

/** A todo's line; one left in progress when the work stopped shows as paused. */
function todoRow(todo: TodoItem, i: number, isActive: boolean): BoardRow {
  const base = { key: `todo-${i}`, depth: 0, text: todo.content, tail: '', isAgent: false }

  if (todo.status === 'completed') return { ...base, mark: 'done', tone: 'dim', isFinished: true }
  if (todo.status === 'in_progress') {
    return isActive
      ? { ...base, mark: 'active', tone: 'accent', isFinished: false }
      : { ...base, mark: 'paused', tone: 'warning', isFinished: false }
  }

  return { ...base, mark: 'pending', tone: 'plain', isFinished: false }
}

/** The agents, running ones first, each with its list's count and what it is doing. */
function agentRows(agents: readonly AgentView[], now: number): BoardRow[] {
  const ordered = [...agents.filter(a => a.isRunning), ...agents.filter(a => !a.isRunning)]

  return ordered.map(a => {
    const done = a.todos.filter(t => t.status === 'completed').length
    const current = a.todos.find(t => t.status === 'in_progress')
    const steps = a.todos.length > 0 ? ` · ${done}/${a.todos.length}` : ''
    const start = a.startedAt
    const tail = start > 0 ? clockText((a.isRunning ? Math.max(now, start) : Math.max(a.endedAt, start)) - start) : ''
    const base = { key: `agent-${a.id}`, depth: a.depth, tail, isAgent: true }

    if (a.isRunning) {
      return { ...base, mark: 'running', text: `${a.label}${steps} · ${current?.content ?? a.activity}`, tone: 'plain', isFinished: false }
    }
    if (a.phase === 'stopped') return { ...base, mark: 'stopped', text: `${a.label} · stopped`, tone: 'dim', isFinished: true }
    if (a.phase === 'failed') return { ...base, mark: 'failed', text: `${a.label} · failed`, tone: 'dim', isFinished: true }

    return { ...base, mark: 'done', text: `${a.label}${steps} · ${a.tools} tool call${a.tools === 1 ? '' : 's'}`, tone: 'dim', isFinished: true }
  })
}

/**
 * Fits the rows in `limit`: finished todos and finished agents fold into one line
 * each, and what still does not fit ends in an `n more` line.
 */
export function squeeze(rows: readonly BoardRow[], limit: number): BoardRow[] {
  if (rows.length <= limit) return [...rows]
  if (limit <= 0) return []

  const fold = (isAgent: boolean): BoardRow[] => {
    const mine = rows.filter(r => r.isAgent === isAgent)
    const finished = mine.filter(r => r.isFinished).length
    const summary: BoardRow = {
      key: isAgent ? 'agents-done' : 'todos-done',
      depth: 0,
      mark: 'done',
      text: isAgent ? `${finished} agent${finished === 1 ? '' : 's'} finished` : `${finished} done`,
      tail: '',
      tone: 'dim',
      isFinished: true,
      isAgent,
    }

    return finished > 1 ? [summary, ...mine.filter(r => !r.isFinished)] : mine
  }
  const folded = [...fold(false), ...fold(true)]
  if (folded.length <= limit) return folded

  const more: BoardRow = {
    key: 'more', depth: 0, mark: 'more', text: `${folded.length - limit + 1} more`, tail: '', tone: 'dim', isFinished: false, isAgent: false,
  }

  return [...folded.slice(0, limit - 1), more]
}

/** A subagent catalog entry, as the subagent package projects it. */
interface CatalogEntry { id: string; createdAt: number; label?: string }

/** The client's session list, as far as the card reads it. */
export interface SessionListState {
  byId: Readonly<Record<string, { running?: boolean } | undefined>>
  projectionsBySession: Readonly<Record<string, { values: Readonly<Record<string, unknown>> } | undefined>>
}

export type Statuses = ReadonlyMap<string, { running: boolean }>

/**
 * The subagents worth a row: running ones, and ones started since this turn began,
 * each joined with its own session's board; a child's own agents nest one level.
 */
export function collectAgents(
  catalog: unknown,
  list: SessionListState,
  statuses: Statuses,
  since: number,
  depth = 0,
): AgentView[] {
  if (!Array.isArray(catalog)) return []

  return (catalog as CatalogEntry[]).flatMap(entry => {
    if (typeof entry?.id !== 'string') return []
    const values = list.projectionsBySession[entry.id]?.values ?? {}
    const child = readBoard(values.progressBoard)
    const isRunning = statuses.get(entry.id)?.running ?? list.byId[entry.id]?.running ?? child.phase === 'working'
    if (!isRunning && entry.createdAt < since) return []

    const agent: AgentView = {
      id: entry.id,
      label: entry.label?.trim() || 'Agent',
      depth,
      isRunning,
      phase: child.phase === 'idle' ? (isRunning ? 'working' : 'done') : child.phase,
      activity: child.phase === 'idle' ? 'Starting' : child.activity,
      tools: child.tools,
      startedAt: child.startedAt || entry.createdAt,
      endedAt: child.endedAt,
      todos: child.todos,
    }
    const nested = depth < 1 ? collectAgents(values.subagentCatalog, list, statuses, since, depth + 1) : []

    return [agent, ...nested]
  })
}

/** A short phrase for what a tool call is doing. */
export function describeTool(name: string, args: Readonly<Record<string, unknown>>): string {
  const file = basename(text(args.file_path) || text(args.path))

  switch (name) {
    case 'read':
    case 'read_image':
      return file ? `Reading ${file}` : 'Reading'
    case 'edit':
    case 'str_replace_editor':
      return file ? `Editing ${file}` : 'Editing'
    case 'write':
      return file ? `Writing ${file}` : 'Writing'
    case 'bash':
    case 'pwsh':
      return text(args.description) || 'Running a command'
    case 'glob':
    case 'grep':
      return 'Searching the code'
    case 'subagent':
      return text(args.description) ? `Delegating: ${text(args.description)}` : 'Delegating to an agent'
    case 'spawn_teammate':
      return 'Starting a teammate'
    case 'workflow':
      return 'Running a workflow'
    case 'todo_write':
      return 'Planning'
    case 'run_code':
      return 'Running code'
    case 'skill':
      return 'Loading a skill'
    case 'ask_user_question':
      return 'Asking you a question'
    case 'session_search':
    case 'session_event_search':
      return 'Searching past sessions'
  }
  if (name.startsWith('stagehand_')) return 'Using the browser'
  if (name.startsWith('memos_')) return 'Recalling memory'
  if (name.startsWith('terminal_')) return 'Using a terminal'
  if (name.startsWith('team_task_')) return 'Updating team tasks'

  return `Using ${name.replace(/[_-]+/g, ' ')}`
}

/** `m:ss`, or `h:mm:ss` past an hour. */
export function clockText(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000))
  const h = Math.floor(total / 3600)
  const m = Math.floor((total % 3600) / 60)
  const s = String(total % 60).padStart(2, '0')

  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${s}` : `${m}:${s}`
}

/** Reads a stored board defensively: anything malformed starts over idle. */
export function readBoard(value: unknown): BoardState {
  if (typeof value !== 'object' || value === null || (value as { v?: unknown }).v !== 1) return IDLE
  const raw = value as Partial<BoardState>

  return {
    ...IDLE,
    ...raw,
    todos: readTodos(raw.todos),
    history: Array.isArray(raw.history) ? raw.history.filter((n): n is number => typeof n === 'number') : [],
  }
}

function readTodos(value: unknown): TodoItem[] {
  if (!Array.isArray(value)) return []

  return value.flatMap(item => {
    const t = (item ?? {}) as Record<string, unknown>
    const content = text(t.content)
    if (!content) return []
    const status = t.status === 'completed' || t.status === 'in_progress' ? t.status : 'pending'

    return [{ content, status }]
  })
}

function parseArguments(raw: unknown): Record<string, unknown> {
  if (typeof raw === 'object' && raw !== null) return raw as Record<string, unknown>
  if (typeof raw !== 'string') return {}
  try {
    const parsed: unknown = JSON.parse(raw)
    return typeof parsed === 'object' && parsed !== null ? parsed as Record<string, unknown> : {}
  } catch {
    // Arguments still streaming or malformed: describe by name alone.
    return {}
  }
}

function text(value: unknown): string {
  return typeof value === 'string' ? value.trim() : ''
}

function basename(path: string): string {
  return path.split(/[\\/]/).pop() ?? path
}
