// @vitest-environment jsdom
// The session-header action and its portal drawer: trigger state, the
// auto-open bookkeeping over projection revisions, tab routing, and the
// face wiring of every panel verb.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import type { AtlassianProjection, OpenResult } from '@cortex/atlassian/client'
import type { PanelActionProps, PanelFace } from '../src/client/contract.ts'
import { AtlassianAction, reviewToShow } from '../src/client/panel/AtlassianAction.tsx'
import type { PanelState } from '../src/client/store.ts'
import { NOW, finding, issue, kit, page, panelActions, panelState, pr, projection, review, t } from './support.client.ts'

afterEach(() => {
  cleanup()
  vi.useRealTimers()
})

function face(overrides: Partial<PanelFace> = {}): PanelFace & Record<keyof PanelFace, ReturnType<typeof vi.fn>> {
  return {
    open: vi.fn(() => Promise.resolve<OpenResult>({ ok: true, entity: { kind: 'issue', key: 'PROJ-123' } })),
    pin: vi.fn(() => Promise.resolve({ ok: true as const })),
    sendPrompt: vi.fn(() => Promise.resolve({ ok: true as const })),
    listPullRequests: vi.fn(() => Promise.resolve({ ok: true as const, items: [] })),
    startReview: vi.fn(() => Promise.resolve({ ok: true as const })),
    postFinding: vi.fn(() => Promise.resolve({ ok: true as const, commentId: 1, mode: 'inline' as const })),
    dismissFinding: vi.fn(() => Promise.resolve({ ok: true as const })),
    cancelReview: vi.fn(() => Promise.resolve({ ok: true as const })),
    diffContext: vi.fn(() => Promise.resolve({ ok: true as const, file: 'x', found: true, lines: [] })),
    status: vi.fn(() => Promise.resolve({ atlassian: { phase: 'off' as const, toolCount: 0 }, bitbucket: { phase: 'off' as const, toolCount: 0 }, rest: { jira: false, confluence: false, bitbucket: false } })),
    ...overrides,
  } as never
}

function props(state: PanelState, value: AtlassianProjection | undefined, faceOverrides: Partial<PanelFace> = {}) {
  const actions = panelActions()
  const f = face(faceOverrides)
  const p = {
    ...kit,
    useProjection: (() => value) as never,
    useStore: ((selector: (s: PanelState) => unknown) => selector(state)) as never,
    actions,
    t,
    ...f,
  } as unknown as PanelActionProps
  return { p, actions, f }
}

const populated = () => projection({
  rev: 5,
  focus: { kind: 'issue', key: 'PROJ-123' },
  issues: { 'PROJ-123': issue() },
  pages: { 98765: page() },
  prs: { 'PROJ/webapp#42': pr() },
  recent: [{ kind: 'issue', key: 'PROJ-123' }, { kind: 'pr', key: 'PROJ/webapp#42' }],
  activity: [{ id: 'a', at: NOW, kind: 'read', tool: 'mcp__atlassian__jira_get_issue', summary: 'Read PROJ-123', ok: true }],
})

describe('reviewToShow', () => {
  it('prefers the running review, then the most recent', () => {
    const reviews = { a: review({ id: 'a', startedAt: 1 }), b: review({ id: 'b', startedAt: 2 }) }
    expect(reviewToShow(reviews, 'a')?.id).toBe('a')
    expect(reviewToShow(reviews, null)?.id).toBe('b')
    expect(reviewToShow(reviews, 'missing')?.id).toBe('b')
    expect(reviewToShow({}, null)).toBeUndefined()
  })
})

describe('AtlassianAction trigger', () => {
  it('shows the label, tracked count, and running dot; toggles the store', () => {
    const { p, actions } = props(panelState(), populated().activeReviewId === null ? { ...populated(), activeReviewId: 'r' } : populated())
    render(<AtlassianAction {...p} />)
    const trigger = screen.getByRole('button', { name: 'Open the Atlassian panel' })
    expect(trigger.textContent).toContain('Atlassian')
    expect(trigger.textContent).toContain('2')
    fireEvent.click(trigger)
    expect(actions.toggle).toHaveBeenCalledTimes(1)
    expect(document.querySelector('[role=dialog]')).toBeNull()
  })

  it('acknowledges the first frame silently and only opens on a later focus change while auto-open is on', () => {
    const state = panelState({ seenRev: null })
    const { p, actions } = props(state, populated())
    const { rerender } = render(<AtlassianAction {...p} />)
    expect(actions.acknowledge).toHaveBeenCalledWith({ rev: 5, focus: 'issue:PROJ-123', review: null })
    expect(actions.open).not.toHaveBeenCalled()
    // A frame at or below the acknowledged revision (a replayed baseline) changes nothing.
    const stale = props(panelState({ seenRev: 7, seenFocus: 'issue:PROJ-123' }), { ...populated(), rev: 4 })
    rerender(<AtlassianAction {...stale.p} />)
    expect(stale.actions.acknowledge).not.toHaveBeenCalled()
    expect(stale.actions.open).not.toHaveBeenCalled()
    // Higher rev but same focus and no review: acknowledged, not opened.
    const next = props(panelState({ seenRev: 5, seenFocus: 'issue:PROJ-123', tab: 'activity' }), { ...populated(), rev: 6 })
    rerender(<AtlassianAction {...next.p} />)
    expect(next.actions.acknowledge).toHaveBeenCalledWith({ rev: 6, focus: 'issue:PROJ-123', review: null })
    expect(next.actions.open).not.toHaveBeenCalled()
    // New focus: selection resets, tab returns to work, drawer opens.
    const moved = props(panelState({ seenRev: 6, seenFocus: 'issue:PROJ-123', tab: 'activity' }), { ...populated(), rev: 7, focus: { kind: 'pr', key: 'PROJ/webapp#42' } })
    rerender(<AtlassianAction {...moved.p} />)
    expect(moved.actions.select).toHaveBeenCalledWith(null)
    expect(moved.actions.setTab).toHaveBeenCalledWith('work')
    expect(moved.actions.open).toHaveBeenCalledTimes(1)
  })

  it('opens on a review start (review tab), stays quiet with auto-open off, and keeps the work tab', () => {
    const first = props(panelState({ seenRev: 5, seenFocus: 'issue:PROJ-123' }), populated())
    const { rerender } = render(<AtlassianAction {...first.p} />)
    const started = props(panelState({ seenRev: 5, seenFocus: 'issue:PROJ-123', tab: 'work' }), { ...populated(), rev: 8, activeReviewId: 'r-1', reviews: { 'r-1': review() } })
    rerender(<AtlassianAction {...started.p} />)
    expect(started.actions.setTab).toHaveBeenCalledWith('review')
    expect(started.actions.open).toHaveBeenCalledTimes(1)
    const quiet = props(panelState({ seenRev: 8, seenFocus: 'issue:PROJ-123', seenReview: 'r-1', autoOpen: false }), { ...populated(), rev: 9, focus: { kind: 'page', id: '98765' } })
    rerender(<AtlassianAction {...quiet.p} />)
    expect(quiet.actions.acknowledge).toHaveBeenCalledWith({ rev: 9, focus: 'page:98765', review: null })
    expect(quiet.actions.open).not.toHaveBeenCalled()
    // Focus change while already on the work tab keeps the tab (no setTab).
    const onWork = props(panelState({ seenRev: 9, seenFocus: 'page:98765', tab: 'work' }), { ...populated(), rev: 10, focus: { kind: 'issue', key: 'PROJ-123' } })
    rerender(<AtlassianAction {...onWork.p} />)
    expect(onWork.actions.setTab).not.toHaveBeenCalled()
    expect(onWork.actions.open).toHaveBeenCalledTimes(1)
  })

  it('does nothing without a projection and shows the loading drawer when open', () => {
    const { p, actions } = props(panelState({ open: true }), undefined)
    render(<AtlassianAction {...p} />)
    expect(actions.acknowledge).not.toHaveBeenCalled()
    expect(screen.getByRole('dialog', { name: 'Atlassian' })).toBeTruthy()
    expect(screen.getByText('Loading…')).toBeTruthy()
    expect(screen.getByText('Nothing touched yet')).toBeTruthy()
  })
})

describe('AtlassianAction drawer', () => {
  it('renders the work tab, closes on Escape and the close button, and toggles auto-open', () => {
    const { p, actions } = props(panelState({ open: true }), populated())
    render(<AtlassianAction {...p} />)
    expect(screen.getByText('2 tracked')).toBeTruthy()
    expect(screen.getByRole('heading', { name: 'Login page ignores SSO redirect target' })).toBeTruthy()
    fireEvent.keyDown(window, { key: 'Escape' })
    expect(actions.close).toHaveBeenCalledTimes(1)
    fireEvent.keyDown(window, { key: 'a' })
    fireEvent.click(screen.getByRole('button', { name: 'Close the Atlassian panel' }))
    fireEvent.click(screen.getByRole('button', { name: 'Close' }))
    expect(actions.close).toHaveBeenCalledTimes(2)
    fireEvent.click(screen.getByRole('checkbox'))
    expect(actions.setAutoOpen).toHaveBeenCalledWith(false)
    fireEvent.click(screen.getByRole('tab', { name: /Activity/ }))
    expect(actions.setTab).toHaveBeenCalledWith('activity')
  })

  it('routes the work tab verbs through the face and the store', async () => {
    const { p, actions, f } = props(panelState({ open: true }), populated())
    render(<AtlassianAction {...p} />)
    fireEvent.click(screen.getByRole('button', { name: "Pin as this session's ticket" }))
    expect(f.pin).toHaveBeenCalledWith('PROJ-123')
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }))
    expect(f.open).toHaveBeenCalledWith({ kind: 'issue', key: 'PROJ-123' })
    fireEvent.click(screen.getByRole('button', { name: 'SSO hardening' }))
    await waitFor(() => { expect(actions.showEntity).toHaveBeenCalledWith({ kind: 'issue', key: 'PROJ-123' }) })
    fireEvent.click(screen.getByRole('button', { name: 'Assign to me' }))
    expect(f.sendPrompt).toHaveBeenCalledWith('Assign PROJ-123 to me.')
    // Recent PR row selects it; its view routes review + refresh by kind.
    fireEvent.click(screen.getByText('Fix SSO redirect loop after IdP callback'))
    expect(actions.select).toHaveBeenCalledWith({ kind: 'pr', key: 'PROJ/webapp#42' })
  })

  it('refreshes page and PR records through open() and ignores failed opens', async () => {
    const failing = props(panelState({ open: true, selected: { kind: 'page', id: '98765' } }), populated(), {
      open: vi.fn(() => Promise.resolve<OpenResult>({ ok: false, code: 'x', message: 'no' })),
    })
    const { rerender } = render(<AtlassianAction {...failing.p} />)
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }))
    expect(failing.f.open).toHaveBeenCalledWith({ kind: 'page', id: '98765' })
    const prSelected = props(panelState({ open: true, selected: { kind: 'pr', key: 'PROJ/webapp#42' } }), populated(), {
      open: vi.fn(() => Promise.resolve<OpenResult>({ ok: false, code: 'x', message: 'no' })),
    })
    rerender(<AtlassianAction {...prSelected.p} />)
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }))
    expect(prSelected.f.open).toHaveBeenCalledWith({ kind: 'pr', pr: { project: 'PROJ', repo: 'webapp', id: 42 } })
    fireEvent.click(screen.getByRole('button', { name: 'Review this PR' }))
    await waitFor(() => { expect(prSelected.f.startReview).toHaveBeenCalledWith({ project: 'PROJ', repo: 'webapp', id: 42 }, '') })
    await waitFor(() => { expect(prSelected.actions.setTab).toHaveBeenCalledWith('review') })
    await Promise.resolve()
    expect(prSelected.actions.showEntity).not.toHaveBeenCalled()
  })

  it('renders the review tab wired to the face, with the default project from the review or the PR focus', async () => {
    const withReview = props(
      panelState({ open: true, tab: 'review' }),
      { ...populated(), activeReviewId: 'r-1', reviews: { 'r-1': review({ findings: [finding()] }) } },
    )
    const { rerender } = render(<AtlassianAction {...withReview.p} />)
    expect(screen.getByText('Review in progress')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Post comment' }))
    await waitFor(() => { expect(withReview.f.postFinding).toHaveBeenCalledWith({ reviewId: 'r-1', findingId: 'f-1' }) })
    fireEvent.click(screen.getByRole('button', { name: 'Diff context' }))
    await waitFor(() => {
      expect(withReview.f.diffContext).toHaveBeenCalledWith({ pr: { project: 'PROJ', repo: 'webapp', id: 42 }, file: 'src/auth/redirect.ts', line: 4, side: 'ADDED' })
    })
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss' }))
    await waitFor(() => { expect(withReview.f.dismissFinding).toHaveBeenCalledWith({ reviewId: 'r-1', findingId: 'f-1' }) })
    fireEvent.click(screen.getByRole('button', { name: 'Cancel review' }))
    await waitFor(() => { expect(withReview.f.cancelReview).toHaveBeenCalledWith('r-1') })
    fireEvent.click(screen.getByRole('tab', { name: /^Pending/ }))
    expect(withReview.actions.setReviewFilter).toHaveBeenCalledWith('pending')
    // Edited comment travels in the request.
    const edited = props(panelState({ open: true, tab: 'review' }), { ...populated(), reviews: { 'r-2': review({ id: 'r-2', status: 'complete', findings: [finding({ id: 'f-9' })] }) } })
    rerender(<AtlassianAction {...edited.p} />)
    fireEvent.click(screen.getByRole('button', { name: 'Edit comment' }))
    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'changed' } })
    fireEvent.click(screen.getByRole('button', { name: 'Post comment' }))
    await waitFor(() => { expect(edited.f.postFinding).toHaveBeenCalledWith({ reviewId: 'r-2', findingId: 'f-9', comment: 'changed' }) })
    // Review another → picker with the inbox listing through the face.
    fireEvent.click(screen.getByRole('button', { name: 'Review another pull request' }))
    await waitFor(() => { expect(edited.f.listPullRequests).toHaveBeenCalledWith({ scope: 'inbox', project: 'PROJ', repo: '', state: 'OPEN' }) })
  })

  it('derives the picker default project from the PR focus, or none, and opens picker rows through the face', async () => {
    // No review, PR in focus: the picker's default project comes from the focus key.
    const focusPr = props(panelState({ open: true, tab: 'review' }), { ...populated(), focus: { kind: 'pr', key: 'PROJ/webapp#42' } })
    const first = render(<AtlassianAction {...focusPr.p} />)
    await waitFor(() => { expect(focusPr.f.listPullRequests).toHaveBeenCalledWith({ scope: 'inbox', project: 'PROJ', repo: '', state: 'OPEN' }) })
    first.unmount()
    // No review, no PR focus: empty default project.
    const bare = props(panelState({ open: true, tab: 'review' }), projection({ rev: 1 }))
    const second = render(<AtlassianAction {...bare.p} />)
    await waitFor(() => { expect(bare.f.listPullRequests).toHaveBeenCalledWith({ scope: 'inbox', project: '', repo: '', state: 'OPEN' }) })
    second.unmount()
    // Picker rows open the PR through the face.
    const listed = props(panelState({ open: true, tab: 'review' }), projection({ rev: 1 }), {
      listPullRequests: vi.fn(() => Promise.resolve({
        ok: true as const,
        items: [{ ref: { project: 'PROJ', repo: 'webapp', id: 42 }, key: 'PROJ/webapp#42', title: 'PR', author: { name: 'K' }, state: 'OPEN' as const, approvals: 0, reviewers: 0, url: 'u' }],
      })),
    })
    render(<AtlassianAction {...listed.p} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Open in panel' }))
    expect(listed.f.open).toHaveBeenCalledWith({ kind: 'pr', pr: { project: 'PROJ', repo: 'webapp', id: 42 } })
    await waitFor(() => { expect(listed.actions.showEntity).toHaveBeenCalledWith({ kind: 'issue', key: 'PROJ-123' }) })
  })

  it('routes the PR review callout of the work tab to the review tab', () => {
    const { p, actions } = props(
      panelState({ open: true, selected: { kind: 'pr', key: 'PROJ/webapp#42' } }),
      { ...populated(), reviews: { 'r-1': review({ status: 'complete' }) } },
    )
    render(<AtlassianAction {...p} />)
    fireEvent.click(screen.getByText('Review complete'))
    expect(actions.setTab).toHaveBeenCalledWith('review')
  })

  it('leaves the panel alone when an issue or PR open fails, and stays on the tab when a review fails to start', async () => {
    const failing = props(panelState({ open: true }), populated(), {
      open: vi.fn(() => Promise.resolve<OpenResult>({ ok: false, code: 'x', message: 'no' })),
      startReview: vi.fn(() => Promise.resolve({ ok: false as const, message: 'not connected' })),
    })
    render(<AtlassianAction {...failing.p} />)
    fireEvent.click(screen.getByRole('button', { name: 'SSO hardening' }))
    await waitFor(() => { expect(failing.f.open).toHaveBeenCalledWith({ kind: 'issue', key: 'PROJ-98' }) })
    fireEvent.click(screen.getByText('Fix SSO redirect loop after IdP callback'))
    const selected = props(panelState({ open: true, selected: { kind: 'pr', key: 'PROJ/webapp#42' } }), populated(), {
      open: vi.fn(() => Promise.resolve<OpenResult>({ ok: false, code: 'x', message: 'no' })),
      startReview: vi.fn(() => Promise.resolve({ ok: false as const, message: 'not connected' })),
    })
    cleanup()
    render(<AtlassianAction {...selected.p} />)
    fireEvent.click(screen.getByRole('button', { name: 'Review this PR' }))
    await waitFor(() => { expect(selected.f.startReview).toHaveBeenCalled() })
    await Promise.resolve()
    expect(selected.actions.setTab).not.toHaveBeenCalled()
    // Picker rows opening a PR that fails to load leave the selection alone.
    cleanup()
    const listed = props(panelState({ open: true, tab: 'review' }), projection({ rev: 1 }), {
      open: vi.fn(() => Promise.resolve<OpenResult>({ ok: false, code: 'x', message: 'no' })),
      listPullRequests: vi.fn(() => Promise.resolve({
        ok: true as const,
        items: [{ ref: { project: 'PROJ', repo: 'webapp', id: 42 }, key: 'PROJ/webapp#42', title: 'PR', author: { name: 'K' }, state: 'OPEN' as const, approvals: 0, reviewers: 0, url: 'u' }],
      })),
    })
    render(<AtlassianAction {...listed.p} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Open in panel' }))
    await waitFor(() => { expect(listed.f.open).toHaveBeenCalled() })
    await Promise.resolve()
    expect(listed.actions.showEntity).not.toHaveBeenCalled()
  })

  it('renders the activity tab and jumps to an entity', () => {
    const { p, actions } = props(panelState({ open: true, tab: 'activity' }), { ...populated(), activity: [{ id: 'a', at: NOW, kind: 'read', tool: 'x', summary: 'Read PROJ-123', ok: true, entity: { kind: 'issue', key: 'PROJ-123' } }] })
    render(<AtlassianAction {...p} />)
    fireEvent.click(screen.getByRole('button', { name: 'Read PROJ-123' }))
    expect(actions.showEntity).toHaveBeenCalledWith({ kind: 'issue', key: 'PROJ-123' })
  })

  it('ticks the clock every minute without breaking the render', () => {
    vi.useFakeTimers()
    const { p } = props(panelState({ open: true }), populated())
    render(<AtlassianAction {...p} />)
    act(() => { vi.advanceTimersByTime(61_000) })
    expect(screen.getByRole('dialog', { name: 'Atlassian' })).toBeTruthy()
  })
})
