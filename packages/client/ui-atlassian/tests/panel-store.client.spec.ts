// @vitest-environment jsdom
/** Panel store: init shape, every declared action, and per-session persistence isolation. */
import { beforeEach, describe, expect, it } from 'vitest'
import { createPanelStore } from '../src/client/store.ts'

beforeEach(() => {
  localStorage.clear()
})

describe('createPanelStore', () => {
  it('starts closed on the work tab following the projection focus', () => {
    const store = createPanelStore().create()
    expect(store.getSnapshot()).toEqual({
      open: false,
      tab: 'work',
      selected: null,
      seenRev: null,
      seenFocus: null,
      seenReview: null,
      autoOpen: true,
      reviewFilter: 'all',
    })
  })

  it('actions cover the declared write set', () => {
    const store = createPanelStore().create()
    store.actions.open()
    expect(store.getSnapshot().open).toBe(true)
    store.actions.close()
    expect(store.getSnapshot().open).toBe(false)
    store.actions.toggle()
    expect(store.getSnapshot().open).toBe(true)
    store.actions.toggle()
    expect(store.getSnapshot().open).toBe(false)

    store.actions.setTab('activity')
    expect(store.getSnapshot().tab).toBe('activity')

    store.actions.select({ kind: 'issue', key: 'PROJ-1' })
    expect(store.getSnapshot().selected).toEqual({ kind: 'issue', key: 'PROJ-1' })
    store.actions.select(null)
    expect(store.getSnapshot().selected).toBeNull()

    store.actions.acknowledge({ rev: 12, focus: 'issue:PROJ-1', review: 'r-1' })
    expect(store.getSnapshot()).toMatchObject({ seenRev: 12, seenFocus: 'issue:PROJ-1', seenReview: 'r-1' })

    store.actions.setAutoOpen(false)
    expect(store.getSnapshot().autoOpen).toBe(false)

    store.actions.setReviewFilter('pending')
    expect(store.getSnapshot().reviewFilter).toBe('pending')
  })

  it('showEntity selects, opens, and lands on the work tab except for a PR while reviewing', () => {
    const store = createPanelStore().create()
    store.actions.setTab('activity')
    store.actions.showEntity({ kind: 'page', id: '9' })
    expect(store.getSnapshot()).toMatchObject({ open: true, tab: 'work', selected: { kind: 'page', id: '9' } })

    store.actions.close()
    store.actions.setTab('review')
    store.actions.showEntity({ kind: 'pr', key: 'PROJ/webapp#42' })
    expect(store.getSnapshot()).toMatchObject({ open: true, tab: 'review', selected: { kind: 'pr', key: 'PROJ/webapp#42' } })

    store.actions.showEntity({ kind: 'issue', key: 'PROJ-2' })
    expect(store.getSnapshot().tab).toBe('work')
  })

  it('persists per scope key and clears its own entry', () => {
    const handle = createPanelStore()
    const first = handle.create('session-a')
    first.actions.open()
    expect(localStorage.getItem('cortex.atlassian.panel.session-a')).toContain('"open":true')
    const second = handle.create('session-b')
    expect(second.getSnapshot().open).toBe(false)
    first.clearPersisted()
    expect(localStorage.getItem('cortex.atlassian.panel.session-a')).toBeNull()
  })
})
