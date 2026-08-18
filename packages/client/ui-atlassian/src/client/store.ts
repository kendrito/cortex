/**
 * Per-session Atlassian panel store: whether the drawer is open, which tab and
 * entity it shows, and the auto-open bookkeeping. Shared by the header action
 * (which renders the drawer) and the tool cards (which open it). The live
 * Atlassian data itself never lives here — it is the `atlassian` projection.
 */
import { defineStore, type EngineStoreHandle } from '@cortex/client-runtime/client'
import type { EntityRef } from '@cortex/atlassian/client'

/** Panel tabs. */
export type PanelTab = 'work' | 'review' | 'activity'

/** Filter of the review findings list. */
export type ReviewFilter = 'all' | 'pending' | 'posted'

/** Panel state. */
export interface PanelState {
  open: boolean
  tab: PanelTab
  /** User-selected entity; `null` follows the projection focus. */
  selected: EntityRef | null
  /** Projection revision the auto-open logic acknowledged; `null` before the first frame. */
  seenRev: number | null
  /** Projection focus key the auto-open logic acknowledged. */
  seenFocus: string | null
  /** Review id the auto-open logic acknowledged. */
  seenReview: string | null
  autoOpen: boolean
  reviewFilter: ReviewFilter
}

/** Declared action shape (gives the exported factory a stable return type). */
type PanelActions = {
  open: (draft: PanelState) => void
  close: (draft: PanelState) => void
  toggle: (draft: PanelState) => void
  setTab: (draft: PanelState, tab: PanelTab) => void
  select: (draft: PanelState, ref: EntityRef | null) => void
  acknowledge: (draft: PanelState, seen: { rev: number; focus: string | null; review: string | null }) => void
  setAutoOpen: (draft: PanelState, on: boolean) => void
  setReviewFilter: (draft: PanelState, filter: ReviewFilter) => void
  showEntity: (draft: PanelState, ref: EntityRef) => void
}

/**
 * Declare the per-session panel state and write surface.
 * @returns the store handle.
 */
export function createPanelStore(): EngineStoreHandle<PanelState, PanelActions> {
  return defineStore({
    init: (): PanelState => ({
      open: false,
      tab: 'work',
      selected: null,
      seenRev: null,
      seenFocus: null,
      seenReview: null,
      autoOpen: true,
      reviewFilter: 'all',
    }),
    persist: 'cortex.atlassian.panel',
    actions: {
      open: (d) => { d.open = true },
      close: (d) => { d.open = false },
      toggle: (d) => { d.open = !d.open },
      setTab: (d, tab: PanelTab) => { d.tab = tab },
      select: (d, ref: EntityRef | null) => { d.selected = ref },
      acknowledge: (d, seen: { rev: number; focus: string | null; review: string | null }) => {
        d.seenRev = seen.rev
        d.seenFocus = seen.focus
        d.seenReview = seen.review
      },
      setAutoOpen: (d, on: boolean) => { d.autoOpen = on },
      setReviewFilter: (d, filter: ReviewFilter) => { d.reviewFilter = filter },
      showEntity: (d, ref: EntityRef) => {
        d.selected = ref
        d.tab = ref.kind === 'pr' && d.tab === 'review' ? 'review' : 'work'
        d.open = true
      },
    },
  })
}
