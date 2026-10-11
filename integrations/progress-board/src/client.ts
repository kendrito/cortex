/**
 * Browser half of the progress board: one card in `conversation.input.dock`, pinned above
 * the composer with the rest of the composer stack. It takes the built-in todo card's seat
 * (same id, lower priority) because it shows the same list, plus the turn's estimated
 * progress and a live row per subagent joined from each child session's own board.
 */
import { createElement as h, useEffect, useState, type ReactNode } from 'react'
import type { Board, BoardRow, SessionListState, Statuses, Tone } from './board.js'
import { buildBoard, collectAgents, isRunning, readBoard } from './board.js'

export const name = '@cortex/progress-board'
export const inject = ['slots']

/** The slot props this card reads; the full types live in the Cortex tree. */
export interface ProgressDockProps {
  useProjection(key: string): unknown
  useSessions<T>(select: (state: SessionListState) => T): T
  useSessionStatus<T>(select: (state: Statuses) => T): T
}

export interface ClientContext {
  slots: {
    inject(name: string, register: () => unknown): unknown
    register(options: { name: string; id: string; order: number; priority?: number }, component: (props: ProgressDockProps) => ReactNode): unknown
  }
}

const CLASS = 'cortex-progress-board'

const TONE_COLOR: Record<Tone, string> = {
  accent: 'var(--dsw-alias-state-business-primary)',
  plain: 'var(--dsw-alias-label-secondary)',
  dim: 'var(--dsw-alias-label-tertiary)',
  success: 'var(--dsw-alias-state-success-primary)',
  warning: 'var(--dsw-alias-state-warn-primary)',
  error: 'var(--dsw-alias-state-error-primary)',
}

const MARK: Record<BoardRow['mark'], { glyph: string; tone: Tone; label: string }> = {
  done: { glyph: '✓', tone: 'success', label: 'Done' },
  active: { glyph: '●', tone: 'accent', label: 'In progress' },
  paused: { glyph: '‖', tone: 'warning', label: 'Paused' },
  pending: { glyph: '○', tone: 'dim', label: 'Pending' },
  running: { glyph: '●', tone: 'accent', label: 'Running' },
  stopped: { glyph: '■', tone: 'warning', label: 'Stopped' },
  failed: { glyph: '✕', tone: 'error', label: 'Failed' },
  more: { glyph: '…', tone: 'dim', label: 'More' },
}

/** The card's look, in the composer dock's own measures and the theme's tokens. */
const STYLES = `
.${CLASS} {
  box-sizing: border-box; flex: none; overflow: hidden; margin: 0 auto;
  width: calc(100% - var(--cortex-composer-side-clearance, 0px) * 2 - var(--cortex-composer-dock-inset, 0px) * 4);
  max-width: calc(var(--cortex-composer-card-max-width, 960px) - var(--cortex-composer-dock-inset, 0px) * 4);
  border: 0; border-radius: var(--dsw-radius-lg, 12px);
  background: var(--dsw-specific-menu); backdrop-filter: var(--dsw-menu-backdrop-filter);
  box-shadow: var(--dsw-elevation-panel);
  --dsw-elevation-stroke-color: var(--dsw-alias-border-l1);
  --cortex-scrollbar-thumb: var(--dsw-alias-scrollbar-bg-l2);
  --cortex-scrollbar-thumb-hover: var(--dsw-alias-scrollbar-hover-l2);
}
.${CLASS}__body { display: flex; flex-direction: column; gap: 6px; padding: 8px 12px; }
.${CLASS}__head {
  display: flex; align-items: center; gap: 10px; width: 100%; padding: 0; border: none;
  background: transparent; text-align: left; cursor: pointer; font: inherit; color: inherit;
}
.${CLASS}__track {
  position: relative; flex: none; width: 160px; height: 6px; border-radius: 3px; overflow: hidden;
  background: var(--dsw-alias-border-l2);
}
.${CLASS}__fill { position: absolute; inset: 0 auto 0 0; border-radius: 3px; transition: width 400ms ease, background-color 400ms ease; }
.${CLASS}__fill[data-active="true"]::after {
  content: ""; position: absolute; inset: 0; border-radius: inherit;
  background: linear-gradient(90deg, transparent, rgba(255,255,255,0.35), transparent);
  animation: ${CLASS}-sheen 1.6s linear infinite;
}
.${CLASS}__pct { flex: none; min-width: 36px; font-size: 13px; line-height: 24px; font-weight: 600; font-variant-numeric: tabular-nums; }
.${CLASS}__caption {
  flex: 1 1 auto; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
  font-size: 13px; line-height: 20px; color: var(--dsw-alias-label-primary);
}
.${CLASS}__time, .${CLASS}__tail {
  flex: none; font-size: 12px; line-height: 20px; font-variant-numeric: tabular-nums; color: var(--dsw-alias-label-tertiary);
}
.${CLASS}__chevron { flex: none; width: 16px; text-align: center; font-size: 10px; color: var(--dsw-alias-label-tertiary); }
.${CLASS}__list { display: flex; flex-direction: column; gap: 4px; margin: 0; padding: 0 0 2px; list-style: none; max-height: 220px; overflow-y: auto; }
.${CLASS}__row { display: flex; align-items: center; gap: 10px; min-width: 0; font-size: 13px; line-height: 20px; }
.${CLASS}__mark { display: grid; flex: none; place-items: center; width: 16px; height: 16px; font-size: 11px; }
.${CLASS}__mark[data-pulse="true"] { animation: ${CLASS}-pulse 1.4s ease-in-out infinite; }
.${CLASS}__text { flex: 1 1 auto; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
@keyframes ${CLASS}-sheen { from { transform: translateX(-100%); } to { transform: translateX(100%); } }
@keyframes ${CLASS}-pulse { 0%, 100% { opacity: 1; } 50% { opacity: 0.35; } }
@media (prefers-reduced-motion: reduce) {
  .${CLASS}__fill[data-active="true"]::after, .${CLASS}__mark[data-pulse="true"] { animation: none; }
}
`

/** Adds the card's stylesheet once per page. */
function ensureStyles(): void {
  if (typeof document === 'undefined' || document.getElementById(`${CLASS}-styles`)) return
  const style = document.createElement('style')
  style.id = `${CLASS}-styles`
  style.textContent = STYLES
  document.head.appendChild(style)
}

export function ProgressDock({ useProjection, useSessions, useSessionStatus }: ProgressDockProps): ReactNode {
  const raw = useProjection('progressBoard')
  const catalog = useProjection('subagentCatalog')
  const projections = useSessions(state => state.projectionsBySession)
  const byId = useSessions(state => state.byId)
  const statuses = useSessionStatus(state => state)
  const [now, setNow] = useState(() => Date.now())
  const [isCollapsed, setCollapsed] = useState(false)

  const state = readBoard(raw)
  const agents = collectAgents(catalog, { byId, projectionsBySession: projections }, statuses, state.startedAt - 1_000)
  const isActive = isRunning(state, agents)

  useEffect(() => {
    if (!isActive) return undefined
    const timer = setInterval(() => setNow(Date.now()), 1_000)
    return () => clearInterval(timer)
  }, [isActive])

  const board = buildBoard(state, agents, isActive ? Math.max(now, Date.now()) : Date.now())
  if (board === undefined) return null

  return h('section', { className: CLASS, 'data-testid': 'progress-board', 'aria-label': 'Progress' },
    h('div', { className: `${CLASS}__body` },
      head(board, isActive, isCollapsed, () => setCollapsed(value => !value)),
      !isCollapsed && board.rows.length > 0 && h('ul', { className: `${CLASS}__list` }, board.rows.map(row)),
    ),
  )
}

function head(board: Board, isActive: boolean, isCollapsed: boolean, toggle: () => void): ReactNode {
  const pct = Math.floor(board.p * 100)
  const color = TONE_COLOR[board.tone]

  return h('button', { type: 'button', className: `${CLASS}__head`, 'aria-expanded': !isCollapsed, onClick: toggle },
    h('span', {
      className: `${CLASS}__track`, role: 'progressbar', 'aria-valuemin': 0, 'aria-valuemax': 100, 'aria-valuenow': pct,
    }, h('span', { className: `${CLASS}__fill`, 'data-active': isActive, style: { width: `${Math.max(pct, 2)}%`, background: color } })),
    h('span', { className: `${CLASS}__pct`, style: { color } }, `${pct}%`),
    h('span', { className: `${CLASS}__caption`, title: board.caption }, board.caption),
    board.elapsed !== '' && h('span', { className: `${CLASS}__time` }, board.elapsed),
    h('span', { className: `${CLASS}__chevron`, 'aria-hidden': true }, isCollapsed ? '▲' : '▼'),
  )
}

function row(item: BoardRow): ReactNode {
  const mark = MARK[item.mark]
  const isLive = item.mark === 'active' || item.mark === 'running'

  return h('li', { key: item.key, className: `${CLASS}__row`, style: { paddingLeft: `${item.depth * 18}px` } },
    h('span', {
      className: `${CLASS}__mark`, role: 'img', 'aria-label': mark.label, 'data-pulse': isLive, style: { color: TONE_COLOR[mark.tone] },
    }, mark.glyph),
    h('span', {
      className: `${CLASS}__text`, title: item.text,
      style: { color: TONE_COLOR[item.tone], fontWeight: item.tone === 'accent' ? 600 : 400 },
    }, item.text),
    item.tail !== '' && h('span', { className: `${CLASS}__tail` }, item.tail),
  )
}

export function apply(ctx: ClientContext): void {
  ensureStyles()
  // Same id as the built-in todo card, ranked ahead of it: the board shows that list too.
  ctx.slots.inject('conversation.input.dock', () =>
    ctx.slots.register({ name: 'conversation.input.dock', id: 'todo', order: 0, priority: -1 }, ProgressDock))
}
