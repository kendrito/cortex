/**
 * Host half of the progress board: a `progressBoard` session projection that folds each
 * session's turn, tool calls, todo_write lists and outcome into board state, plus one
 * prompt section asking agents to keep their todo list current to the end. Subagent
 * sessions fold the same way, so the card joins each child's board under its parent.
 */
import type { BoardState, FoldEvent } from './board.js'
import { IDLE, foldEvent, readBoard } from './board.js'

export const name = 'progress-board'
export const inject = ['sessionProjections', 'systemPrompt']

/** The projection key the card reads with `useProjection('progressBoard')`. */
export const PROJECTION_KEY = 'progressBoard'

export const GUIDANCE = [
  '# Progress board',
  'The person follows your work on a progress board above the message box. For work that takes more than two or three steps, write the plan with todo_write before you start and keep it current as each step starts and finishes.',
  'Before you end your reply, update todo_write one last time: mark every finished task completed, and leave a task you could not finish (or that waits on the person) as it is, so the board never shows stale progress.',
].join('\n')

/** What this plugin needs of the host context; the full types live in the Cortex tree. */
interface ProjectionDefinition {
  key: string
  stateSchema: { parse(value: unknown): unknown }
  init(): unknown
  apply(state: unknown, event: FoldEvent): unknown
  wire: { viewSchema: { parse(value: unknown): unknown }; view(state: unknown): unknown }
  stateVersion: number
}

export interface HostContext {
  sessionProjections: { register(definition: ProjectionDefinition): () => void }
  systemPrompt: { section(definition: { name: string; order: number; text: string }): () => void }
}

/** Accepts any stored board, normalizing what an older build wrote. */
const boardSchema = { parse: (value: unknown): BoardState => readBoard(value) }

export function apply(ctx: HostContext): void {
  ctx.sessionProjections.register({
    key: PROJECTION_KEY,
    stateSchema: boardSchema,
    init: () => IDLE,
    apply: (state, event) => foldEvent(state as BoardState, event),
    wire: { viewSchema: boardSchema, view: state => state },
    stateVersion: 1,
  })
  // Beside the subagent section (2800), before reporting (2900).
  ctx.systemPrompt.section({ name: 'tool:progress-board', order: 2850, text: GUIDANCE })
}
