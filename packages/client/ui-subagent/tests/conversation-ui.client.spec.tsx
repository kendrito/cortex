// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { makeTranslate } from '@cortex/client-test-runtime'
import type {
  SessionId, SessionListState, SessionSummary, SubagentCatalogSnapshot,
} from '@cortex/client-runtime/client'
import {
  SubagentCatalogAction, type SubagentCatalogActionProps,
} from '../src/client/SubagentCatalogAction.tsx'
import { SubagentReadOnlyComposer } from '../src/client/SubagentReadOnlyComposer.tsx'
import { en } from '../src/client/locales.ts'

afterEach(() => {
  cleanup()
  vi.useRealTimers()
  vi.restoreAllMocks()
})

const PARENT = 'parent' as SessionId
const CHILD = 'child' as SessionId
const GRANDCHILD = 'grandchild' as SessionId
const t: SubagentCatalogActionProps['t'] = makeTranslate(en)

function catalog(over: Partial<SubagentCatalogSnapshot> = {}): SubagentCatalogSnapshot {
  return {
    entries: [
      {
        kind: 'child', id: CHILD, mode: 'continuable', label: 'worker',
        activity: 'running', hasChildren: true,
      },
      {
        kind: 'child', id: 'child-2' as SessionId, mode: 'one-shot',
        label: 'reviewer', activity: 'inactive', hasChildren: false,
      },
      { kind: 'diagnostic', id: 'bad' as SessionId, reason: 'corrupt' },
    ],
    parentAvailable: true,
    state: 'ready',
    error: null,
    ...over,
  }
}

function props(
  value: SubagentCatalogSnapshot | undefined,
  nested: Readonly<Record<SessionId, SubagentCatalogSnapshot>> = {},
  summaries?: Readonly<Record<SessionId, SessionSummary>>,
) {
  const state = {
    ids: [CHILD],
    byId: summaries ?? {
      [CHILD]: {
        id: CHILD,
        title: 'Scanning project files',
        displayTitle: 'worker',
        running: true,
        blank: false,
        updatedAt: Date.now(),
      },
    },
    current: PARENT, phase: 'ready',
    subagentsByParent: value === undefined ? nested : { [PARENT]: value, ...nested },
    jobsBySession: {},
    currentAddress: undefined,
  } satisfies SessionListState
  function useSessions<T>(select: (snapshot: SessionListState) => T): T {
    return select(state)
  }
  return {
    sessionId: PARENT,
    useSessions,
    openChild: vi.fn(),
    refresh: vi.fn(),
    setCatalogOpen: vi.fn(),
    t,
  } as unknown as SubagentCatalogActionProps
}

function summary(id: SessionId, updatedAt: number): SessionSummary {
  return {
    id,
    displayTitle: id,
    running: false,
    blank: false,
    updatedAt,
  }
}

describe('SubagentCatalogAction', () => {
  it('aggregates live descendant activity onto the closed trigger', () => {
    const summaries: Record<SessionId, SessionSummary> = {
      [CHILD]: {
        ...summary(CHILD, Date.now()),
        parentId: PARENT,
        origin: 'subagent',
      },
      [GRANDCHILD]: {
        ...summary(GRANDCHILD, Date.now()),
        parentId: CHILD,
        origin: 'subagent',
        running: true,
      },
      ['child-2' as SessionId]: {
        ...summary('child-2' as SessionId, Date.now()),
        parentId: PARENT,
        origin: 'subagent',
      },
    }
    const view = render(<SubagentCatalogAction {...props(catalog(), {}, summaries)} />)

    const trigger = screen.getByRole('button', { name: '1 subagent running' })
    expect(trigger.querySelector('[data-state="ongoing"]')).not.toBeNull()

    view.rerender(<SubagentCatalogAction {...props(catalog(), {}, {
      ...summaries,
      [GRANDCHILD]: { ...summaries[GRANDCHILD]!, running: false },
    })} />)
    expect(screen.getByRole('button', { name: '3 subagents' })
      .querySelector('[data-state="ongoing"]')).toBeNull()
  })

  it('does not aggregate subagents reached through an ordinary fork', () => {
    const fork = 'fork' as SessionId
    const forkChild = 'fork-child' as SessionId
    render(<SubagentCatalogAction {...props(catalog(), {}, {
      [CHILD]: { ...summary(CHILD, 1), parentId: PARENT, origin: 'subagent' },
      ['child-2' as SessionId]: {
        ...summary('child-2' as SessionId, 1), parentId: PARENT, origin: 'subagent',
      },
      [fork]: { ...summary(fork, 1), parentId: PARENT },
      [forkChild]: { ...summary(forkChild, 1), parentId: fork, origin: 'subagent', running: true },
    })} />)

    const trigger = screen.getByRole('button', { name: '2 subagents' })
    expect(trigger.querySelector('[data-state="ongoing"]')).toBeNull()
  })

  it('renders healthy counts, stable rows, diagnostics, and catalog-addressed navigation', () => {
    const input = props(catalog())
    render(<SubagentCatalogAction {...input} />)
    const trigger = screen.getByRole('button', { name: /2 subagents/ })
    fireEvent.click(trigger)

    expect(input.setCatalogOpen).toHaveBeenCalledWith(PARENT, true)
    expect(screen.getAllByRole('treeitem')).toHaveLength(3)
    expect(screen.getByText('Scanning project files · continuable · running')).toBeTruthy()
    expect(screen.getByText('one-shot · not running')).toBeTruthy()
    const diagnostic = screen.getByRole('treeitem', { name: /corrupted session record/ })
    expect(diagnostic.getAttribute('aria-disabled')).toBe('true')
    expect(screen.getByRole('button', { name: 'Expand worker descendants' })).toBeTruthy()
    expect(screen.queryByRole('button', { name: 'Expand reviewer descendants' })).toBeNull()
    expect(screen.getByRole('treeitem', { name: /reviewer/ }).children).toHaveLength(2)

    fireEvent.click(screen.getByRole('treeitem', { name: /worker/ }))
    expect(input.openChild).toHaveBeenCalledWith({
      parentSessionId: PARENT, childSessionId: CHILD, mode: 'continuable',
    })
    expect(input.setCatalogOpen).toHaveBeenLastCalledWith(PARENT, false)
  })

  it('selects singular count keys for one descendant', () => {
    const base = props(catalog({
      entries: [{
        kind: 'child', id: CHILD, mode: 'continuable', label: 'worker',
        activity: 'running', hasChildren: false,
      }],
    }), {}, {
      [CHILD]: {
        ...summary(CHILD, Date.now()), parentId: PARENT, origin: 'subagent', running: true,
      },
    })
    const translate = vi.fn(base.t)
    render(<SubagentCatalogAction {...base} t={translate} />)

    expect(translate).toHaveBeenCalledWith('count.running.one', { count: 1 })
    expect(translate).toHaveBeenCalledWith('count.total.one', { count: 1 })
  })

  it('removes the disclosure column from branchless catalog levels', () => {
    const input = props(catalog({
      entries: [{
        kind: 'child', id: CHILD, mode: 'continuable', label: 'worker',
        activity: 'running', hasChildren: false,
      }],
    }))
    render(<SubagentCatalogAction {...input} />)
    fireEvent.click(screen.getByRole('button', { name: /1 subagent/ }))

    expect(screen.getByRole('treeitem', { name: /worker/ }).children).toHaveLength(1)
  })

  it('supports trigger/menu keyboard traversal, Escape focus restore, and outside close', async () => {
    const input = props(catalog())
    render(<SubagentCatalogAction {...input} />)
    const trigger = screen.getByRole('button', { name: /2 subagents/ })
    fireEvent.keyDown(trigger, { key: 'ArrowDown' })
    await Promise.resolve()
    expect(document.activeElement).toBe(screen.getByRole('treeitem', { name: /worker/ }))

    fireEvent.keyDown(document.activeElement as Element, { key: 'End' })
    expect(document.activeElement).toBe(screen.getByRole('treeitem', { name: /reviewer/ }))
    fireEvent.keyDown(document.activeElement as Element, { key: 'Home' })
    expect(document.activeElement).toBe(screen.getByRole('treeitem', { name: /worker/ }))
    fireEvent.keyDown(document.activeElement as Element, { key: 'ArrowUp' })
    expect(document.activeElement).toBe(screen.getByRole('treeitem', { name: /reviewer/ }))
    fireEvent.keyDown(document.activeElement as Element, { key: 'Escape' })
    await Promise.resolve()
    expect(screen.queryByRole('tree')).toBeNull()
    expect(document.activeElement).toBe(trigger)

    fireEvent.click(trigger)
    fireEvent.pointerDown(screen.getByRole('tree'))
    expect(screen.getByRole('tree')).toBeTruthy()
    fireEvent.pointerDown(document.body)
    expect(screen.queryByRole('tree')).toBeNull()
  })

  it('covers diagnostic variants, fallback labels, and keyboard row activation', () => {
    const unsupported = 'unsupported' as SessionId
    const unavailable = 'unavailable' as SessionId
    const unlabeled = 'unlabeled' as SessionId
    const input = props(catalog({
      entries: [
        { kind: 'diagnostic', id: unsupported, reason: 'unsupported' },
        { kind: 'diagnostic', id: unavailable, reason: 'unavailable' },
        {
          kind: 'child', id: CHILD, mode: 'continuable', label: 'worker',
          activity: 'running', hasChildren: false,
        },
        {
          kind: 'child', id: unlabeled, mode: 'one-shot',
          activity: 'inactive', hasChildren: false,
        },
      ],
    }))
    render(<SubagentCatalogAction {...input} />)
    const trigger = screen.getByRole('button', { name: /2 subagents/ })
    fireEvent.keyDown(trigger, { key: 'Tab' })
    expect(screen.queryByRole('tree')).toBeNull()
    fireEvent.click(trigger)
    expect(screen.getByRole('treeitem', { name: /unsupported subagent record version/ })).toBeTruthy()
    expect(screen.getByRole('treeitem', { name: /session record temporarily unavailable/ })).toBeTruthy()

    fireEvent.keyDown(screen.getByRole('treeitem', { name: /worker/ }), { key: 'Enter' })
    expect(input.openChild).toHaveBeenLastCalledWith({
      parentSessionId: PARENT, childSessionId: CHILD, mode: 'continuable',
    })
    fireEvent.click(trigger)
    fireEvent.keyDown(screen.getByRole('treeitem', { name: /unlabeled/ }), { key: ' ' })
    expect(input.openChild).toHaveBeenLastCalledWith({
      parentSessionId: PARENT, childSessionId: unlabeled, mode: 'one-shot',
    })
  })

  it('shows durable token totals, ticks active duration by seconds, and freezes inactive rows', async () => {
    const now = 2_000_000_000_000
    const minute = 60_000
    const hour = 60 * minute
    const day = 24 * hour
    vi.useFakeTimers()
    vi.setSystemTime(now)
    const rows = [
      ['running', 'running', 65_000, now - 5_000, now - 1_000, now],
      ['finished', 'inactive', 3_723_000, undefined, undefined, now - 60_000],
      ['interrupted', 'inactive', 2_000, now - 7_000, now - 3_000, now + 60_000],
      ['days', 'inactive', 12 * day + 5 * hour + 6 * minute + 7_000, undefined, undefined, now],
      ['whole-day', 'inactive', day, undefined, undefined, now],
      ['months', 'inactive', 192 * day, undefined, undefined, now],
      ['whole-month', 'inactive', 30 * day, undefined, undefined, now],
      ['years', 'inactive', 832 * day, undefined, undefined, now],
      ['whole-year', 'inactive', 365 * day, undefined, undefined, now],
    ] as const
    const usageById = {
      running: {
        uncachedInputTokens: 1_000,
        outputTokens: 200,
        cacheReadTokens: 3_000,
        cacheWriteTokens: 400,
      },
      finished: {
        uncachedInputTokens: 123,
        outputTokens: 0,
        cacheReadTokens: 0,
        cacheWriteTokens: 0,
      },
      interrupted: {
        uncachedInputTokens: 123_000_000,
        outputTokens: 0,
        cacheReadTokens: 0,
        cacheWriteTokens: 0,
      },
    } as const
    const entries = rows.map(([id, activity]) => ({
      kind: 'child' as const,
      id: id as SessionId,
      mode: 'continuable' as const,
      label: id,
      activity,
      hasChildren: false,
    }))
    const summaries = Object.fromEntries(rows.map(([
      id, activity, settledMs, activeSince, activeThrough, updatedAt,
    ]) => {
      const childId = id as SessionId
      return [id, {
        ...summary(childId, updatedAt),
        parentId: PARENT,
        origin: 'subagent' as const,
        running: activity === 'running',
        projectionValues: {
          subagentTiming: {
            settledMs,
            ...(activeSince === undefined || activeThrough === undefined
              ? {}
              : { active: { since: activeSince, through: activeThrough } }),
          },
          tokenUsage: id in usageById
            ? usageById[id as keyof typeof usageById]
            : undefined,
        },
      }]
    })) as Record<SessionId, SessionSummary>
    const input = props(catalog({ entries }), {}, summaries)
    render(<SubagentCatalogAction {...input} />)
    const trigger = screen.getByRole('button', { name: '1 subagent running' })
    expect(within(trigger).getByText('9 subagents')).toBeTruthy()
    fireEvent.click(trigger)

    const runningRow = screen.getByRole('treeitem', { name: /running.*4\.6K tok · 1m 10s/ })
    const runningMetrics = within(runningRow)
    const tokenMetric = runningMetrics.getByText('4.6K tok')
    const durationMetric = runningMetrics.getByText('1m 10s')
    expect(tokenMetric.parentElement).toBe(durationMetric.parentElement)
    expect(tokenMetric.nextElementSibling).toBe(durationMetric)
    expect(screen.getByRole('treeitem', { name: /finished.*123 tok · 1h 02m 03s/ })).toBeTruthy()
    expect(screen.getByRole('treeitem', { name: /interrupted.*123M tok · 6s/ })).toBeTruthy()
    expect(screen.getByRole('treeitem', { name: /days.*12d 05h 06m 07s/ })).toBeTruthy()
    expect(screen.getByText('12d 5h').getAttribute('title'))
      .toBe('Total active duration: 12d 05h 06m 07s')
    expect(screen.getByText('1d')).toBeTruthy()
    expect(screen.getByText('~6mo 12d')).toBeTruthy()
    expect(screen.getByText('~1mo')).toBeTruthy()
    expect(screen.getByText('~2y 3mo')).toBeTruthy()
    expect(screen.getByText('~1y')).toBeTruthy()

    await vi.advanceTimersByTimeAsync(1_000)
    expect(screen.getByRole('treeitem', { name: /running.*4\.6K tok · 1m 11s/ })).toBeTruthy()
    expect(screen.getByRole('treeitem', { name: /finished.*123 tok · 1h 02m 03s/ })).toBeTruthy()
    expect(screen.getByRole('treeitem', { name: /interrupted.*123M tok · 6s/ })).toBeTruthy()
  })

  it('lazily expands and collapses descendant catalogs with direct-parent navigation', () => {
    const childCatalog = catalog({
      entries: [
        {
          kind: 'child', id: GRANDCHILD, mode: 'continuable',
          label: 'indexer', activity: 'inactive', hasChildren: false,
        },
      ],
    })
    const grandchildCatalog = catalog({ entries: [] })
    const input = props(catalog(), {
      [CHILD]: childCatalog,
      [GRANDCHILD]: grandchildCatalog,
    })
    render(<SubagentCatalogAction {...input} />)
    fireEvent.click(screen.getByRole('button', { name: /2 subagents/ }))

    fireEvent.click(screen.getByRole('button', { name: 'Expand worker descendants' }))
    expect(input.setCatalogOpen).toHaveBeenCalledWith(CHILD, true)
    const nested = screen.getByRole('treeitem', { name: /indexer/ })
    expect(nested.getAttribute('aria-level')).toBe('2')

    fireEvent.click(nested)
    expect(input.openChild).toHaveBeenCalledWith({
      parentSessionId: CHILD, childSessionId: GRANDCHILD, mode: 'continuable',
    })
    expect(input.setCatalogOpen).toHaveBeenCalledWith(PARENT, false)
    expect(input.setCatalogOpen).toHaveBeenCalledWith(CHILD, false)
  })

  it('shows known descendant rows while their catalog loads', () => {
    const secondGrandchild = 'grandchild-2' as SessionId
    const summaries = {
      [GRANDCHILD]: {
        ...summary(GRANDCHILD, 1), parentId: CHILD, origin: 'subagent' as const,
      },
      [secondGrandchild]: {
        ...summary(secondGrandchild, 1), parentId: CHILD, origin: 'subagent' as const,
        running: true,
      },
    }
    const deferred = props(catalog(), {}, summaries)
    const view = render(<SubagentCatalogAction {...deferred} />)
    fireEvent.click(screen.getByRole('button', { name: /2 subagents/ }))
    fireEvent.click(screen.getByRole('button', { name: 'Expand worker descendants' }))

    expect(deferred.setCatalogOpen).toHaveBeenCalledWith(CHILD, true)
    expect(screen.getByRole('group').getAttribute('aria-busy')).toBe('true')
    const loadingRows = screen.getAllByRole('treeitem', { name: 'Loading subagents' })
    expect(loadingRows).toHaveLength(2)
    expect(loadingRows.every(row => row.getAttribute('aria-level') === '2')).toBe(true)
    expect(loadingRows[1]?.querySelector('[data-state="ongoing"]')).not.toBeNull()

    const loading = props(catalog(), {
      [CHILD]: catalog({ entries: [], state: 'loading' }),
    }, summaries)
    view.rerender(<SubagentCatalogAction {...loading} />)
    expect(screen.getAllByRole('treeitem', { name: 'Loading subagents' })).toHaveLength(2)

    const ready = props(catalog(), {
      [CHILD]: catalog({
        entries: [
          {
            kind: 'child', id: GRANDCHILD, mode: 'continuable',
            label: 'indexer', activity: 'inactive', hasChildren: false,
          },
          {
            kind: 'child', id: secondGrandchild, mode: 'one-shot',
            label: 'critic', activity: 'running', hasChildren: false,
          },
        ],
      }),
    }, summaries)
    view.rerender(<SubagentCatalogAction {...ready} />)
    expect(screen.getByRole('group').getAttribute('aria-busy')).toBeNull()
    expect(screen.getByRole('treeitem', { name: /indexer/ })).toBeTruthy()
    expect(screen.getByRole('treeitem', { name: /critic/ })).toBeTruthy()
    expect(screen.queryByRole('treeitem', { name: 'Loading subagents' })).toBeNull()
  })

  it('uses ArrowRight and ArrowLeft for branch disclosure', async () => {
    const input = props(catalog(), {
      [CHILD]: catalog({
        entries: [{
          kind: 'child', id: GRANDCHILD, mode: 'continuable',
          label: 'indexer', activity: 'running', hasChildren: false,
        }],
      }),
    })
    render(<SubagentCatalogAction {...input} />)
    const trigger = screen.getByRole('button', { name: /2 subagents/ })
    fireEvent.keyDown(trigger, { key: 'ArrowDown' })
    await Promise.resolve()
    const worker = screen.getByRole('treeitem', { name: /worker/ })
    fireEvent.keyDown(worker, { key: 'ArrowRight' })
    expect(screen.getByRole('treeitem', { name: /indexer/ })).toBeTruthy()
    fireEvent.keyDown(worker, { key: 'ArrowLeft' })
    expect(screen.queryByRole('treeitem', { name: /indexer/ })).toBeNull()
    expect(input.setCatalogOpen).toHaveBeenCalledWith(CHILD, false)
  })

  it('closes expanded descendants even when their own catalogs have not arrived', () => {
    const input = props(catalog(), {
      [CHILD]: catalog({
        entries: [
          {
            kind: 'child', id: GRANDCHILD, mode: 'continuable',
            label: 'indexer', activity: 'running', hasChildren: true,
          },
          { kind: 'diagnostic', id: 'nested-bad' as SessionId, reason: 'corrupt' },
        ],
      }),
    })
    render(<SubagentCatalogAction {...input} />)
    fireEvent.click(screen.getByRole('button', { name: /2 subagents/ }))
    fireEvent.click(screen.getByRole('button', { name: 'Expand worker descendants' }))
    fireEvent.click(screen.getByRole('button', { name: 'Expand indexer descendants' }))
    fireEvent.click(screen.getByRole('button', { name: 'Collapse worker descendants' }))

    expect(input.setCatalogOpen).toHaveBeenCalledWith(GRANDCHILD, false)
    expect(input.setCatalogOpen).toHaveBeenCalledWith(CHILD, false)
    expect(screen.queryByRole('treeitem', { name: /indexer/ })).toBeNull()
  })

  it('hides an arrived empty catalog and exposes retry for a failed one', () => {
    const absent = render(<SubagentCatalogAction {...props(undefined)} />)
    expect(screen.queryByRole('button')).toBeNull()
    absent.unmount()

    const empty = props(catalog({ entries: [] }))
    const view = render(<SubagentCatalogAction {...empty} />)
    expect(screen.queryByRole('button')).toBeNull()
    view.unmount()

    const failed = props(catalog({
      entries: [],
      state: 'error',
      error: { code: 'internal', message: 'index down', details: {} },
    }))
    render(<SubagentCatalogAction {...failed} />)
    fireEvent.click(screen.getByRole('button', { name: /0 subagents/ }))
    expect(screen.getByText('index down')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: /Retry/ }))
    expect(failed.refresh).toHaveBeenCalledWith(PARENT)
  })

  it('keeps known descendants reachable while their catalog is absent or stale-empty', () => {
    const second = 'child-2' as SessionId
    const summaries = {
      [CHILD]: {
        ...summary(CHILD, 1), parentId: PARENT, origin: 'subagent' as const,
      },
      [second]: {
        ...summary(second, 1), parentId: PARENT, origin: 'subagent' as const, running: true,
      },
    }
    const absent = props(undefined, {}, summaries)
    const view = render(<SubagentCatalogAction {...absent} />)

    const trigger = screen.getByRole('button', { name: '1 subagent running' })
    expect(within(trigger).getByText('2 subagents')).toBeTruthy()
    fireEvent.click(trigger)
    expect(absent.setCatalogOpen).toHaveBeenCalledWith(PARENT, true)
    expect(screen.getAllByRole('treeitem', { name: 'Loading subagents' })).toHaveLength(2)
    expect(absent.openChild).not.toHaveBeenCalled()

    const staleEmpty = props(catalog({ entries: [] }), {}, summaries)
    view.rerender(<SubagentCatalogAction {...staleEmpty} />)
    expect(screen.getByRole('button', { name: '1 subagent running' })).toBeTruthy()
    expect(screen.getAllByRole('treeitem', { name: 'Loading subagents' })).toHaveLength(2)
    expect(staleEmpty.openChild).not.toHaveBeenCalled()
  })

  it('hides a bare loading catalog and keeps the error fallback without focusable rows', async () => {
    // Selecting any session schedules a catalog refresh; a loading snapshot
    // with no other evidence of children must not flash the action in.
    const loading = props(catalog({ entries: [], state: 'loading' }))
    const view = render(<SubagentCatalogAction {...loading} />)
    expect(screen.queryByRole('button')).toBeNull()
    view.unmount()

    const failed = props(catalog({ entries: [], state: 'error', error: null }))
    render(<SubagentCatalogAction {...failed} />)
    const trigger = screen.getByRole('button', { name: /0 subagents/ })
    fireEvent.click(trigger)
    expect(screen.getByText('Unable to load subagents')).toBeTruthy()
    fireEvent.keyDown(trigger, { key: 'ArrowDown' })
    await Promise.resolve()
    expect(screen.getByRole('tree')).toBeTruthy()
    fireEvent.keyDown(screen.getByRole('tree'), { key: 'ArrowUp' })
  })

  it('navigates from outside the tree and tolerates a deferred focus after unmount', async () => {
    const input = props(catalog())
    const view = render(<SubagentCatalogAction {...input} />)
    const trigger = screen.getByRole('button', { name: /2 subagents/ })
    fireEvent.click(trigger)
    fireEvent.keyDown(screen.getByRole('tree'), { key: 'ArrowUp' })
    expect(document.activeElement).toBe(screen.getByRole('treeitem', { name: /reviewer/ }))
    fireEvent.keyDown(trigger, { key: 'ArrowDown' })
    view.unmount()
    await Promise.resolve()
  })

  it('closes every observed catalog when the root becomes empty', () => {
    const populated = props(catalog(), {
      [CHILD]: catalog({
        entries: [{
          kind: 'child', id: GRANDCHILD, mode: 'continuable',
          label: 'indexer', activity: 'inactive', hasChildren: false,
        }],
      }),
    })
    const view = render(<SubagentCatalogAction {...populated} />)
    fireEvent.click(screen.getByRole('button', { name: /2 subagents/ }))
    fireEvent.click(screen.getByRole('button', { name: 'Expand worker descendants' }))

    const empty = props(catalog({ entries: [] }))
    view.rerender(<SubagentCatalogAction {...empty} />)
    expect(screen.queryByRole('button')).toBeNull()
    expect(empty.setCatalogOpen).toHaveBeenCalledWith(PARENT, false)
    expect(empty.setCatalogOpen).toHaveBeenCalledWith(CHILD, false)
  })
})

describe('SubagentReadOnlyComposer', () => {
  it('explains the exact missing-parent recovery path', () => {
    render(<SubagentReadOnlyComposer matched={{ reason: 'parent-unavailable' }} t={t} />)
    expect(screen.getByRole('status').textContent).toContain('The parent session is offline')
  })

  it('explains that one-shot histories never accept follow-ups', () => {
    render(<SubagentReadOnlyComposer matched={{ reason: 'one-shot' }} t={t} />)
    expect(screen.getByRole('status').textContent).toContain('One-shot tasks do not accept follow-ups')
  })
})
