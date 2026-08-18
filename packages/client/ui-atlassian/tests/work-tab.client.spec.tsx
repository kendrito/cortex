// @vitest-environment jsdom
// The work tab: focus resolution (selection over projection focus), the three
// entity views with their refresh verbs, the recent list, and the fallback
// buttons for a pinned-but-unfetched ticket and an unfetched PR focus.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { WorkTab, latestReviewOf, parsePrKey } from '../src/client/panel/WorkTab.tsx'
import { NOW, issue, page, pr, projection, review, t } from './support.client.ts'

afterEach(cleanup)

const verbs = () => ({
  onSelect: vi.fn(),
  onPin: vi.fn(),
  onRefresh: vi.fn(),
  onOpenIssue: vi.fn(),
  onOpenPr: vi.fn(),
  onPrompt: vi.fn(() => Promise.resolve()),
  onReview: vi.fn(),
  onShowReview: vi.fn(),
})

const full = () => projection({
  issues: { 'PROJ-123': issue() },
  pages: { 98765: page() },
  prs: { 'PROJ/webapp#42': pr() },
  recent: [
    { kind: 'issue', key: 'PROJ-123' }, { kind: 'page', id: '98765' }, { kind: 'pr', key: 'PROJ/webapp#42' },
    { kind: 'issue', key: 'GONE-1' }, { kind: 'page', id: 'gone-page' }, { kind: 'pr', key: 'GONE/x#1' },
  ],
  focus: { kind: 'issue', key: 'PROJ-123' },
  pinned: 'PROJ-123',
  reviews: {
    old: review({ id: 'old', status: 'complete', startedAt: NOW - 500_000 }),
    'r-1': review({ id: 'r-1', status: 'running', startedAt: NOW - 1_000 }),
    other: review({ id: 'other', prKey: 'PROJ/api#7', pr: { project: 'PROJ', repo: 'api', id: 7 } }),
  },
  activeReviewId: 'r-1',
})

describe('helpers', () => {
  it('latestReviewOf picks the most recent review of the PR key', () => {
    expect(latestReviewOf(full(), 'PROJ/webapp#42')?.id).toBe('r-1')
    expect(latestReviewOf(full(), 'NOPE')).toBeUndefined()
  })

  it('parsePrKey parses a key and tolerates garbage', () => {
    expect(parsePrKey('PROJ/webapp#42')).toEqual({ project: 'PROJ', repo: 'webapp', id: 42 })
    expect(parsePrKey('garbage')).toEqual({ project: '', repo: 'garbage', id: 0 })
  })
})

describe('WorkTab', () => {
  it('shows the empty state for an untouched session', () => {
    render(<WorkTab projection={projection()} selected={null} now={NOW} t={t} {...verbs()} />)
    expect(screen.getByText('Your Jira, Confluence, and Bitbucket work lands here')).toBeTruthy()
  })

  it('follows the projection focus, lists the other recent entities, and refreshes an issue', () => {
    const v = verbs()
    render(<WorkTab projection={full()} selected={null} now={NOW} t={t} {...v} />)
    expect(screen.getByRole('heading', { name: 'Login page ignores SSO redirect target' })).toBeTruthy()
    // Recent excludes the focused entity and the row whose record is gone.
    expect(screen.getByText('Recent')).toBeTruthy()
    expect(screen.getByText('Auth service runbook')).toBeTruthy()
    expect(screen.getByText('Fix SSO redirect loop after IdP callback')).toBeTruthy()
    expect(screen.queryByText('GONE-1')).toBeNull()
    expect(screen.queryByRole('button', { name: 'Back' })).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }))
    expect(v.onRefresh).toHaveBeenCalledWith({ kind: 'issue', key: 'PROJ-123' })
    fireEvent.click(screen.getByText('Auth service runbook'))
    expect(v.onSelect).toHaveBeenCalledWith({ kind: 'page', id: '98765' })
  })

  it('renders a selected page over the focus with a Back affordance and the pinned chip on the issue row', () => {
    const v = verbs()
    render(<WorkTab projection={full()} selected={{ kind: 'page', id: '98765' }} now={NOW} t={t} {...v} />)
    expect(screen.getByRole('heading', { name: 'Auth service runbook' })).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }))
    expect(v.onRefresh).toHaveBeenCalledWith({ kind: 'page', id: '98765' })
    expect(screen.getByText('Pinned')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Back' }))
    expect(v.onSelect).toHaveBeenCalledWith(null)
  })

  it('renders a selected PR with its latest review and routes review verbs', () => {
    const v = verbs()
    render(<WorkTab projection={full()} selected={{ kind: 'pr', key: 'PROJ/webapp#42' }} now={NOW} t={t} {...v} />)
    expect(screen.getByRole('heading', { name: 'Fix SSO redirect loop after IdP callback' })).toBeTruthy()
    expect(screen.getByText('Review in progress')).toBeTruthy()
    fireEvent.click(screen.getByText('Review in progress'))
    expect(v.onShowReview).toHaveBeenCalledTimes(1)
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }))
    expect(v.onRefresh).toHaveBeenCalledWith({ kind: 'pr', key: 'PROJ/webapp#42' })
  })

  it('routes the Review button of a PR without a running review', () => {
    const v = verbs()
    const p = projection({ prs: { 'PROJ/webapp#42': pr() }, recent: [{ kind: 'pr', key: 'PROJ/webapp#42' }], focus: { kind: 'pr', key: 'PROJ/webapp#42' } })
    render(<WorkTab projection={p} selected={null} now={NOW} t={t} {...v} />)
    fireEvent.click(screen.getByRole('button', { name: 'Review this PR' }))
    expect(v.onReview).toHaveBeenCalledWith({ project: 'PROJ', repo: 'webapp', id: 42 })
  })

  it('shows the waiting note when the focused record is not fetched yet, plus the pinned and PR fallbacks', () => {
    const v = verbs()
    const p = projection({
      recent: [{ kind: 'pr', key: 'PROJ/webapp#42' }],
      focus: { kind: 'pr', key: 'PROJ/webapp#42' },
      pinned: 'PROJ-9',
    })
    render(<WorkTab projection={p} selected={null} now={NOW} t={t} {...v} />)
    expect(screen.getByText('Waiting for details…')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: /PROJ-9/ }))
    expect(v.onOpenIssue).toHaveBeenCalledWith('PROJ-9')
    fireEvent.click(screen.getByRole('button', { name: 'PROJ/webapp#42' }))
    expect(v.onOpenPr).toHaveBeenCalledWith({ project: 'PROJ', repo: 'webapp', id: 42 })
  })

  it('shows the empty state when the selection points at a missing record and nothing is recent', () => {
    render(<WorkTab projection={projection()} selected={{ kind: 'issue', key: 'X-1' }} now={NOW} t={t} {...verbs()} />)
    expect(screen.getByRole('button', { name: 'Back' })).toBeTruthy()
    expect(screen.getByText('Your Jira, Confluence, and Bitbucket work lands here')).toBeTruthy()
  })
})
