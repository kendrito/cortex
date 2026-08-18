/**
 * The session-header Atlassian action: a compact trigger (glyph, tracked
 * count, live-review dot) and the right-hand drawer it opens, rendered through
 * a portal so it floats over every column. Live data arrives through
 * `useProjection('atlassian')`; panel state through the shared store; verbs
 * through the injected face.
 */
import { useEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import clsx from 'clsx'
import { IconCloseOutline16, IconSettingsOutline14, StateDot } from '@cortex/client-ui-primitives'
import type { EntityRef, PrRef, ReviewFinding, ReviewRecord } from '@cortex/atlassian/client'
import type { PanelActionProps } from '../contract.ts'
import { refKey } from '../format.ts'
import type { PanelTab } from '../store.ts'
import { ActivityTab } from './ActivityTab.tsx'
import { ServiceGlyph } from '../atoms.tsx'
import { ReviewTab } from './ReviewTab.tsx'
import { WorkTab, parsePrKey } from './WorkTab.tsx'
import css from './drawer.module.css'

const TABS: readonly PanelTab[] = ['work', 'review', 'activity']

/** Clock tick for relative times. */
const TICK_MS = 60_000

/**
 * The review the Review tab shows: the running one, else the most recent.
 * @param reviews - all reviews of the session.
 * @param activeId - running review id.
 * @returns the review, or `undefined`.
 */
export function reviewToShow(reviews: Record<string, ReviewRecord>, activeId: string | null): ReviewRecord | undefined {
  if (activeId !== null && reviews[activeId] !== undefined) return reviews[activeId]
  return Object.values(reviews).sort((a, b) => b.startedAt - a.startedAt)[0]
}

/**
 * Render the header action and its drawer.
 * @param props - the composed slot props.
 * @returns the trigger (and the portal drawer while open).
 */
export function AtlassianAction(props: PanelActionProps) {
  const {
    useProjection, useStore, actions, t,
    open, pin, sendPrompt, listPullRequests, startReview, postFinding, dismissFinding, cancelReview, diffContext,
  } = props
  const projection = useProjection('atlassian')
  const state = useStore(s => s)
  const [now, setNow] = useState(() => Date.now())
  const mounted = useRef(false)

  useEffect(() => {
    const timer = setInterval(() => { setNow(Date.now()) }, TICK_MS)
    return () => { clearInterval(timer) }
  }, [])

  // Auto-open bookkeeping: acknowledge the first frame silently, then open on
  // a new focus or a review start while auto-open is on.
  const rev = projection?.rev
  const focusKey = projection?.focus == null ? null : refKey(projection.focus)
  const activeReview = projection?.activeReviewId ?? null
  useEffect(() => {
    if (projection === undefined || rev === undefined) return
    const first = !mounted.current
    mounted.current = true
    if (first || state.seenRev === null) {
      actions.acknowledge({ rev, focus: focusKey, review: activeReview })
      return
    }
    if (rev <= state.seenRev) return
    const focusChanged = focusKey !== null && focusKey !== state.seenFocus
    const reviewStarted = activeReview !== null && activeReview !== state.seenReview
    actions.acknowledge({ rev, focus: focusKey, review: activeReview })
    if (!state.autoOpen || (!focusChanged && !reviewStarted)) return
    if (reviewStarted) actions.setTab('review')
    else {
      actions.select(null)
      if (state.tab !== 'work') actions.setTab('work')
    }
    actions.open()
    // Only a projection revision change drives this effect.
  }, [rev])

  useEffect(() => {
    if (!state.open) return
    const onKey = (event: KeyboardEvent): void => {
      if (event.key === 'Escape') actions.close()
    }
    window.addEventListener('keydown', onKey)
    return () => { window.removeEventListener('keydown', onKey) }
  }, [state.open, actions])

  const tracked = projection?.recent.length ?? 0
  const running = activeReview !== null
  const setNowSoon = (): void => { setNow(Date.now()) }

  const showEntity = (ref: EntityRef): void => { actions.showEntity(ref) }
  const openIssue = (key: string): void => {
    setNowSoon()
    void open({ kind: 'issue', key }).then((result) => { if (result.ok) actions.showEntity(result.entity) })
  }
  const openPr = (pr: PrRef): void => {
    setNowSoon()
    void open({ kind: 'pr', pr }).then((result) => { if (result.ok) actions.showEntity(result.entity) })
  }
  const refresh = (ref: EntityRef): void => {
    setNowSoon()
    if (ref.kind === 'issue') void open({ kind: 'issue', key: ref.key })
    else if (ref.kind === 'page') void open({ kind: 'page', id: ref.id })
    else void open({ kind: 'pr', pr: parsePrKey(ref.key) })
  }
  const review = projection === undefined ? undefined : reviewToShow(projection.reviews, projection.activeReviewId)
  const startReviewOf = async (pr: PrRef, focus: string): Promise<{ ok: true } | { ok: false; message: string }> => {
    const outcome = await startReview(pr, focus)
    if (outcome.ok) actions.setTab('review')
    return outcome
  }

  const drawer = state.open && typeof document !== 'undefined' ? createPortal(
    <aside className={css.drawer} role="dialog" aria-label={t('panel.title')} data-tab={state.tab}>
      <header className={css.drawerHead}>
        <div className={css.brand}>
          <span className={css.brandMark}><ServiceGlyph service="jira" size={20} /></span>
          <div className={css.brandText}>
            <span className={css.brandTitle}>{t('panel.title')}</span>
            <span className={css.brandSub}>{tracked === 0 ? t('panel.subtitle.empty') : t('panel.subtitle.count', { count: String(tracked) })}</span>
          </div>
        </div>
        <label className={css.autoOpen} title={t('panel.autoOpen')}>
          <input type="checkbox" checked={state.autoOpen} onChange={(event) => { actions.setAutoOpen(event.target.checked) }} />
          <IconSettingsOutline14 />
        </label>
        <button type="button" className={css.close} aria-label={t('panel.close')} onClick={() => { actions.close() }}>
          <IconCloseOutline16 />
        </button>
      </header>
      <nav className={css.tabs} role="tablist">
        {TABS.map(tab => (
          <button
            key={tab}
            type="button"
            role="tab"
            aria-selected={state.tab === tab}
            className={clsx(css.tab, state.tab === tab && css.tabActive)}
            onClick={() => { actions.setTab(tab) }}
          >
            {t(`panel.tab.${tab}`)}
            {tab === 'review' && running ? <StateDot state="ongoing" size={8} className={css.tabDot} /> : null}
            {tab === 'activity' && projection !== undefined && projection.activity.length > 0 ? <span className={css.tabCount}>{projection.activity.length}</span> : null}
          </button>
        ))}
      </nav>
      <div className={css.drawerBody}>
        {projection === undefined ? <div className={css.pending}>{t('panel.pending')}</div> : state.tab === 'work' ? (
          <WorkTab
            projection={projection}
            selected={state.selected}
            now={now}
            t={t}
            onSelect={(ref) => { actions.select(ref) }}
            onPin={(key) => { void pin(key) }}
            onRefresh={refresh}
            onOpenIssue={openIssue}
            onOpenPr={openPr}
            onPrompt={text => sendPrompt(text)}
            onReview={(pr) => { void startReviewOf(pr, '') }}
            onShowReview={() => { actions.setTab('review') }}
          />
        ) : state.tab === 'review' ? (
          <ReviewTab
            review={review}
            pr={review === undefined ? undefined : projection.prs[review.prKey]}
            filter={state.reviewFilter}
            now={now}
            t={t}
            defaultProject={review?.pr.project ?? (projection.focus?.kind === 'pr' ? parsePrKey(projection.focus.key).project : '')}
            onFilter={(filter) => { actions.setReviewFilter(filter) }}
            onList={(scope, project, repo) => listPullRequests({ scope, project, repo, state: 'OPEN' })}
            onStart={startReviewOf}
            onCancel={reviewId => cancelReview(reviewId)}
            onPost={(reviewId, findingId, comment) => postFinding({ reviewId, findingId, ...comment === undefined ? {} : { comment } })}
            onDismiss={(reviewId, findingId) => dismissFinding({ reviewId, findingId })}
            onDiffContext={(pr: PrRef, finding: ReviewFinding) =>
              diffContext({ pr, file: finding.file, line: finding.line, side: finding.side })}
            onOpenPr={openPr}
          />
        ) : (
          <ActivityTab activity={projection.activity} now={now} t={t} onSelect={showEntity} />
        )}
      </div>
    </aside>,
    document.body,
  ) : null

  return (
    <>
      <button
        type="button"
        className={clsx(css.trigger, state.open && css.triggerOpen)}
        aria-label={state.open ? t('action.close') : t('action.open')}
        aria-expanded={state.open}
        title={t('action.label')}
        onClick={() => { actions.toggle() }}
      >
        <ServiceGlyph service="jira" size={16} />
        <span className={css.triggerLabel}>{t('action.label')}</span>
        {tracked > 0 ? <span className={css.triggerCount}>{tracked}</span> : null}
        {running ? <StateDot state="ongoing" size={8} className={css.triggerDot} /> : null}
      </button>
      {drawer}
    </>
  )
}
