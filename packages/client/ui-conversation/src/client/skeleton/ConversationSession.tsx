/** Strict per-session header/body content inserted into the resident conversation layout. */

import { useEffect, useRef, useState, useSyncExternalStore } from 'react'
import clsx from 'clsx'
import type { SessionId, SessionListState, SessionSummary } from '@cortex/client-runtime/client'
import type {
  ConversationSessionHeaderSlotProps, ConversationSessionSlotProps,
} from '../contract/slots.ts'
import type { ViewTab } from '../contract/views.ts'
import css from './ConversationRoot.module.css'
import {
  editorPinned, editorSplitRatio, setEditorSplitRatio, subscribeEditorPinned, toggleEditorPinned,
} from './editor-pin-store.ts'

/** Full props composed from the strict session body contract. */
export type ConversationSessionProps = ConversationSessionSlotProps

/** Full props composed from the strict session header contract. */
export type ConversationSessionHeaderProps = ConversationSessionHeaderSlotProps

interface Breadcrumb {
  readonly id: SessionId
  readonly displayTitle: string
}

const DEFAULT_VIEW_ID = 'chat'

/** Resolve by id and keep stale persisted selections on the stable Chat fallback. */
function resolveActiveView(tabs: readonly ViewTab[], selectedId: string | null): ViewTab | undefined {
  const requestedId = selectedId ?? DEFAULT_VIEW_ID
  return tabs.find(view => view.id === requestedId)
    ?? tabs.find(view => view.id === DEFAULT_VIEW_ID)
}

function deriveAncestry(list: SessionListState, id: SessionId): readonly Breadcrumb[] {
  const chain: Breadcrumb[] = []
  const seen = new Set<SessionId>()
  let cursor: SessionId | undefined = id
  while (cursor !== undefined) {
    if (seen.has(cursor)) break
    seen.add(cursor)
    const summary: SessionSummary | undefined = list.byId[cursor]
    if (summary === undefined) break
    chain.unshift({ id: summary.id, displayTitle: summary.displayTitle })
    if (summary.origin !== 'subagent') break
    cursor = summary.parentId
  }
  return chain
}

function equalBreadcrumbs(left: readonly Breadcrumb[], right: readonly Breadcrumb[]): boolean {
  return left.length === right.length
    && left.every((item, index) => {
      const other = right.at(index)
      return other !== undefined && item.id === other.id && item.displayTitle === other.displayTitle
    })
}

/**
 * Renders Session header chrome above the resident conversation scrollport.
 * @param props - Strict Session store, view ledger, navigation, render, and locale shares.
 * @returns the hidden blank-session header or visible title and tabs.
 */
export function ConversationSessionHeader({
  sessionId, useSession, useSessions, useStore, actions,
  renderSlot, views, open, t,
}: ConversationSessionHeaderProps) {
  useSyncExternalStore(views.subscribe, views.version)
  const pinned = useSyncExternalStore(subscribeEditorPinned, editorPinned)
  const allTabs = views.list()
  const hasCode = allTabs.some(viewTab => viewTab.id === 'code')
  // A pinned editor is permanently visible, so its tab leaves the ring.
  const tabs = pinned && hasCode ? allTabs.filter(viewTab => viewTab.id !== 'code') : allTabs
  const selectedId = useStore(s => s.view)
  const active = resolveActiveView(tabs, selectedId)
  const ancestry = useSessions(s => deriveAncestry(s, sessionId), equalBreadcrumbs)
  const composerPhase = useSession(s => s.composerPhase)
  const blank = useSession(s => s.blank)
  const hideChrome = blank && composerPhase === 'blank'

  return (
    <header
      className={clsx(css.header, hideChrome && css.headerHidden)}
      aria-hidden={hideChrome || undefined}
    >
      {!hideChrome && (
        <>
          <div className={css.titleRow}>
            <div className={css.titleCluster}>
              <nav className={css.crumbs} aria-label={t('session.hierarchy')}>
                {ancestry.map((summary, index) => {
                  const last = index === ancestry.length - 1
                  return (
                    <span key={summary.id} className={css.crumbSeg}>
                      {index > 0 && <span className={css.crumbSep}>/</span>}
                      <button
                        type="button"
                        className={clsx(css.crumb, last && css.crumbCurrent)}
                        disabled={last}
                        onClick={() => { open(summary.id) }}
                      >
                        {summary.displayTitle}
                      </button>
                    </span>
                  )
                })}
                {ancestry.length === 0 && <span className={css.crumbCurrent}>{sessionId}</span>}
              </nav>
              <div className={css.headerActions}>
                {renderSlot('conversation.session.header.actions', {})}
              </div>
            </div>
            <div className={css.headerUtilities}>
              {renderSlot('conversation.session.header.utilities', {})}
            </div>
          </div>
          {(tabs.length > 1 || (pinned && hasCode)) && (
            <div className={css.tabs} role="tablist">
              {tabs.map(viewTab => (
                <button
                  key={viewTab.id}
                  type="button"
                  role="tab"
                  aria-selected={viewTab.id === active?.id}
                  className={clsx(css.tab, viewTab.id === active?.id && css.tabActive)}
                  onClick={() => { actions.setView(viewTab.id) }}
                >
                  {viewTab.label}
                </button>
              ))}
              {hasCode && (
                <button
                  type="button"
                  className={clsx(css.pinEditor, pinned && css.pinEditorOn)}
                  aria-pressed={pinned}
                  title={pinned ? t('view.unpinEditor') : t('view.pinEditor')}
                  onClick={() => { toggleEditorPinned() }}
                >
                  {pinned ? t('view.unpinEditor') : t('view.pinEditor')}
                </button>
              )}
            </div>
          )}
        </>
      )}
    </header>
  )
}

/**
 * Renders the active Session view inside the resident scrollport and keeps
 * the input draft mirrored while blank Hero chrome is visible.
 * @param props - Strict Session input/store, view ledger, and render shares.
 * @returns the active view area, or null while the Session remains blank.
 */
export function ConversationSession({
  sessionId, useSession, useInput, inputActions, useStore, actions,
  renderSlot, views, bindDraftMirror, releaseSessionImages,
}: ConversationSessionProps) {
  useSyncExternalStore(views.subscribe, views.version)
  const pinned = useSyncExternalStore(subscribeEditorPinned, editorPinned)
  const allTabs = views.list()
  const hasCode = allTabs.some(viewTab => viewTab.id === 'code')
  const split = pinned && hasCode
  // While split, the ring excludes Code (it owns its own pane); a stored Code
  // selection falls back to the ring default rather than doubling the editor.
  const tabs = split ? allTabs.filter(viewTab => viewTab.id !== 'code') : allTabs
  const selectedId = useStore(s => s.view)
  const active = resolveActiveView(tabs, split && selectedId === 'code' ? null : selectedId)
  const composerPhase = useSession(s => s.composerPhase)
  const blank = useSession(s => s.blank)
  const inputState = useInput(s => s)
  const storedDraft = useStore(s => s.draft)
  // `?? null`: persisted snapshots from before the inspect field rehydrate without it.
  const inspect = useStore(s => s.inspect ?? null)

  useEffect(() => {
    if (inputState.draft === '' && storedDraft !== '') inputActions.setDraft(storedDraft)
    const unmirror = bindDraftMirror(actions.setDraft)
    return () => { unmirror() }
    // Mount-only (deps pinned to inputActions): later store writes come from
    // the machine mirror, not this seed effect.
  }, [inputActions])

  useEffect(() => () => {
    releaseSessionImages(sessionId)
  }, [releaseSessionImages, sessionId])

  if (blank && composerPhase === 'blank') return null
  const viewProps = {
    inspect,
    onInspectDone: () => { actions.setInspect(null) },
  }
  if (split) {
    return (
      <SplitViewArea
        editor={renderSlot('conversation.view', viewProps, { only: 'code' })}
        side={active !== undefined && renderSlot('conversation.view', viewProps, { only: active.id })}
      />
    )
  }
  return (
    <div className={css.viewArea}>
      {active !== undefined && renderSlot('conversation.view', viewProps, { only: active.id })}
    </div>
  )
}

/**
 * The pinned-editor split: editor pane, drag divider, active view beside it.
 * The divider drags the stored ratio (clamped by the store); frames stop
 * receiving pointer events while a drag runs, or the editor iframe would
 * swallow the move stream mid-drag.
 * @param props.editor - the rendered Code view pane.
 * @param props.side - the rendered active ring view.
 * @returns the split layout element.
 */
function SplitViewArea({ editor, side }: { editor: unknown; side: unknown }) {
  const ratio = useSyncExternalStore(subscribeEditorPinned, editorSplitRatio)
  const [dragging, setDragging] = useState(false)
  const areaRef = useRef<HTMLDivElement | null>(null)

  const onDividerPointerDown = (down: { pointerId: number; clientX: number; currentTarget: Element }): void => {
    const area = areaRef.current
    if (area === null) return
    const bounds = area.getBoundingClientRect()
    setDragging(true)
    const divider = down.currentTarget as HTMLElement
    divider.setPointerCapture(down.pointerId)
    const move = (event: PointerEvent): void => {
      setEditorSplitRatio((event.clientX - bounds.left) / bounds.width)
    }
    const up = (): void => {
      setDragging(false)
      divider.removeEventListener('pointermove', move)
      divider.removeEventListener('pointerup', up)
      divider.removeEventListener('pointercancel', up)
    }
    divider.addEventListener('pointermove', move)
    divider.addEventListener('pointerup', up)
    divider.addEventListener('pointercancel', up)
  }

  return (
    <div ref={areaRef} className={clsx(css.viewArea, css.viewSplit, dragging && css.viewSplitDragging)}>
      <div className={css.splitEditor} style={{ flexBasis: `${String(ratio * 100)}%` }}>
        {editor as never}
      </div>
      <div
        className={css.splitDivider}
        role="separator"
        aria-orientation="vertical"
        onPointerDown={onDividerPointerDown}
      />
      <div className={css.splitSide}>
        {side as never}
      </div>
    </div>
  )
}
