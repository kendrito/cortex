// @vitest-environment jsdom
// The review tab: the pull request picker (inbox / repository) and the live
// run (status, verdict, histogram, filters, bulk post, cancel, findings), plus
// the picker ↔ run switch.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import type { ListPullRequestsResult, PostFindingResult, PrSummary } from '@cortex/atlassian/client'
import { ReviewTab, type ReviewTabProps } from '../src/client/panel/ReviewTab.tsx'
import { NOW, existingComment, finding, pr, review, t } from './support.client.ts'

afterEach(cleanup)

const summary = (overrides: { [K in keyof PrSummary]?: PrSummary[K] | undefined } = {}): PrSummary => {
  const base: Record<string, unknown> = {
    ref: { project: 'PROJ', repo: 'webapp', id: 42 },
    key: 'PROJ/webapp#42',
    title: 'Fix SSO redirect loop',
    author: { name: 'Kendrito' },
    state: 'OPEN',
    updated: new Date(NOW - 3_600_000).toISOString(),
    approvals: 1,
    reviewers: 2,
    url: 'http://bb/pr/42',
    role: 'REVIEWER',
  }
  const out: Record<string, unknown> = {}
  for (const [key, value] of Object.entries({ ...base, ...overrides })) {
    if (value !== undefined) out[key] = value
  }
  return out as unknown as PrSummary
}

function props(overrides: Partial<ReviewTabProps> = {}): ReviewTabProps {
  return {
    review: undefined,
    pr: undefined,
    filter: 'all',
    now: NOW,
    t,
    defaultProject: 'PROJ',
    onFilter: vi.fn(),
    onList: vi.fn(() => Promise.resolve<ListPullRequestsResult>({
      ok: true,
      items: [
        summary(),
        summary({ key: 'PROJ/api#7', ref: { project: 'PROJ', repo: 'api', id: 7 }, title: 'API tweak', role: undefined, updated: undefined, approvals: 0 }),
        summary({ key: 'PROJ/api#8', ref: { project: 'PROJ', repo: 'api', id: 8 }, title: 'Mine', role: 'AUTHOR' }),
      ],
    })),
    onStart: vi.fn(() => Promise.resolve<{ ok: true } | { ok: false; message: string }>({ ok: true })),
    onCancel: vi.fn(() => Promise.resolve()),
    onPost: vi.fn(() => Promise.resolve<PostFindingResult>({ ok: true, commentId: 1, mode: 'inline' })),
    onDismiss: vi.fn(() => Promise.resolve()),
    onDiffContext: vi.fn(() => Promise.resolve({ ok: true as const, file: 'x', found: true, lines: [] })),
    onOpenPr: vi.fn(),
    ...overrides,
  }
}

describe('ReviewTab picker', () => {
  it('loads the inbox on mount, lists rows with roles/approvals, and starts a review with focus text', async () => {
    const p = props()
    render(<ReviewTab {...p} />)
    expect(p.onList).toHaveBeenCalledWith('inbox', 'PROJ', '')
    await screen.findByText('Fix SSO redirect loop')
    expect(screen.getByText('Reviewer')).toBeTruthy()
    expect(screen.getByText('Author')).toBeTruthy()
    expect(screen.getAllByText('1/2').length).toBe(2)
    expect(screen.getByText('0/2')).toBeTruthy()
    expect(screen.getAllByText('· 1h ago').length).toBe(2)
    // Open in panel routes to onOpenPr.
    fireEvent.click(screen.getAllByRole('button', { name: 'Open in panel' })[0]!)
    expect(p.onOpenPr).toHaveBeenCalledWith({ project: 'PROJ', repo: 'webapp', id: 42 })
    // Select → start box → focus → start.
    fireEvent.click(screen.getByRole('button', { name: /Fix SSO redirect loop/ }))
    expect(screen.getByText('PROJ/webapp#42 · Fix SSO redirect loop')).toBeTruthy()
    fireEvent.change(screen.getByRole('textbox', { name: /Extra instructions/ }), { target: { value: 'security first' } })
    fireEvent.click(screen.getByRole('button', { name: 'Start review' }))
    await waitFor(() => { expect(p.onStart).toHaveBeenCalledWith({ project: 'PROJ', repo: 'webapp', id: 42 }, 'security first') })
  })

  it('reports a start failure and keeps the box', async () => {
    const p = props({ onStart: vi.fn(() => Promise.resolve({ ok: false as const, message: 'not connected' })) })
    render(<ReviewTab {...p} />)
    await screen.findByText('Fix SSO redirect loop')
    fireEvent.click(screen.getByRole('button', { name: /Fix SSO redirect loop/ }))
    fireEvent.click(screen.getByRole('button', { name: 'Start review' }))
    await screen.findByText('not connected')
  })

  it('shows loading, error, and empty listing states and refreshes', async () => {
    let resolve!: (value: ListPullRequestsResult) => void
    const p = props({ onList: vi.fn(() => new Promise<ListPullRequestsResult>((done) => { resolve = done })) })
    render(<ReviewTab {...p} />)
    expect(screen.getByText('Loading pull requests…')).toBeTruthy()
    await act(async () => { resolve({ ok: false, code: 'not-configured', message: 'no bitbucket' }) })
    expect(screen.getByText('no bitbucket')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }))
    await act(async () => { resolve({ ok: true, items: [] }) })
    expect(screen.getByText('No open pull requests found.')).toBeTruthy()
    expect(p.onList).toHaveBeenCalledTimes(2)
  })

  it('lists one repository once both fields are filled and switches back to the inbox', async () => {
    const p = props()
    render(<ReviewTab {...p} />)
    await screen.findByText('Fix SSO redirect loop')
    fireEvent.click(screen.getByRole('tab', { name: 'Repository' }))
    const load = screen.getByRole('button', { name: 'Load' })
    fireEvent.change(screen.getByRole('textbox', { name: 'Repository slug' }), { target: { value: '' } })
    expect(load.hasAttribute('disabled')).toBe(true)
    fireEvent.change(screen.getByRole('textbox', { name: 'Project key' }), { target: { value: 'OPS' } })
    fireEvent.change(screen.getByRole('textbox', { name: 'Repository slug' }), { target: { value: 'infra' } })
    expect(load.hasAttribute('disabled')).toBe(false)
    fireEvent.click(load)
    await waitFor(() => { expect(p.onList).toHaveBeenLastCalledWith('repo', 'OPS', 'infra') })
    fireEvent.click(screen.getByRole('tab', { name: 'Your inbox' }))
    await waitFor(() => { expect(p.onList).toHaveBeenLastCalledWith('inbox', 'OPS', 'infra') })
  })
})

describe('ReviewTab run', () => {
  const findings = () => [
    finding({ id: 'nit', severity: 'nit', category: 'style', title: 'Nit one', at: NOW - 40_000 }),
    finding({ id: 'crit', severity: 'critical', title: 'Critical one', at: NOW - 30_000 }),
    finding({ id: 'posted', severity: 'major', category: 'security', title: 'Posted one', at: NOW - 20_000, posted: { commentId: 3, mode: 'inline', at: NOW } }),
    finding({ id: 'gone', severity: 'minor', category: 'testing', title: 'Dismissed one', at: NOW - 10_000, dismissed: true }),
  ]

  it('renders a running review with findings sorted by severity, filters, and cancel', async () => {
    const p = props({ review: review({ findings: findings() }), pr: pr() })
    render(<ReviewTab {...p} />)
    expect(screen.getByText('Review in progress')).toBeTruthy()
    expect(screen.getByText('Fix SSO redirect loop after IdP callback')).toBeTruthy()
    expect(screen.getByText('2m ago')).toBeTruthy()
    const titles = screen.getAllByText(/one$/).map(node => node.textContent)
    expect(titles).toEqual(['Critical one', 'Posted one', 'Dismissed one', 'Nit one'])
    // Histogram counts per severity.
    expect(screen.getByTitle('Critical').textContent).toContain('1')
    expect(screen.getByTitle('Major').textContent).toContain('1')
    expect(screen.getByTitle('Minor').textContent).toContain('1')
    expect(screen.getByTitle('Nit').textContent).toContain('1')
    // Filter tabs carry counts and route to onFilter.
    expect(screen.getByRole('tab', { name: /^All/ }).textContent).toContain('4')
    expect(screen.getByRole('tab', { name: /^Pending/ }).textContent).toContain('2')
    expect(screen.getByRole('tab', { name: /^Posted/ }).textContent).toContain('1')
    fireEvent.click(screen.getByRole('tab', { name: /^Pending/ }))
    expect(p.onFilter).toHaveBeenCalledWith('pending')
    fireEvent.click(screen.getByRole('button', { name: 'Cancel review' }))
    await waitFor(() => { expect(p.onCancel).toHaveBeenCalledWith('r-1') })
    fireEvent.click(screen.getByRole('button', { name: /PROJ\/webapp#42/ }))
    expect(p.onOpenPr).toHaveBeenCalledWith({ project: 'PROJ', repo: 'webapp', id: 42 })
    // Bulk post covers the two pending findings.
    fireEvent.click(screen.getByRole('button', { name: 'Post all pending' }))
    await waitFor(() => { expect(p.onPost).toHaveBeenCalledTimes(2) })
    expect(p.onPost).toHaveBeenCalledWith('r-1', 'crit', undefined)
    expect(p.onPost).toHaveBeenCalledWith('r-1', 'nit', undefined)
    // Card verbs are bound to the review id.
    fireEvent.click(screen.getAllByRole('button', { name: 'Dismiss' })[0]!)
    await waitFor(() => { expect(p.onDismiss).toHaveBeenCalledWith('r-1', 'crit') })
    fireEvent.click(screen.getAllByRole('button', { name: 'Diff context' })[0]!)
    await waitFor(() => { expect(p.onDiffContext).toHaveBeenCalledWith({ project: 'PROJ', repo: 'webapp', id: 42 }, expect.objectContaining({ id: 'crit' })) })
    fireEvent.click(screen.getAllByRole('button', { name: 'Post comment' })[0]!)
    await waitFor(() => { expect(p.onPost).toHaveBeenCalledWith('r-1', 'crit', undefined) })
  })

  it('lists the comments already on the pull request and passes them to the cards', () => {
    const existing = [
      existingComment({ id: 501, replies: 2 }),
      existingComment({ id: 502, file: undefined, line: undefined, replies: 0, text: 'Looks good overall.', author: { name: 'Sam Lee' } }),
      existingComment({ id: 503, line: undefined, replies: 0, text: 'File-level remark.', author: { name: 'Sam Lee' } }),
    ]
    render(<ReviewTab {...props({ review: review({ existing, findings: [finding({ overlaps: [501] })] }) })} />)
    const details = screen.getByText('Already on the pull request').closest('details')
    expect(details).toBeTruthy()
    expect(screen.getByText('The reviewer read these before starting and will not repeat them.')).toBeTruthy()
    expect(screen.getAllByText('Please validate the redirect target here.').length).toBe(2)
    expect(screen.getAllByText('2 repl(y/ies)').length).toBe(2)
    expect(screen.getByText('Looks good overall.')).toBeTruthy()
    expect(screen.getAllByText('general comment').length).toBe(2)
    expect(screen.getAllByText('Near an existing comment').length).toBeGreaterThan(0)
  })

  it('applies the pending and posted filters', () => {
    const base = props({ review: review({ findings: findings() }) })
    const { rerender } = render(<ReviewTab {...base} filter="pending" />)
    expect(screen.getAllByText(/one$/).map(node => node.textContent)).toEqual(['Critical one', 'Nit one'])
    rerender(<ReviewTab {...base} filter="posted" />)
    expect(screen.getAllByText(/one$/).map(node => node.textContent)).toEqual(['Posted one'])
    // A single pending finding hides the bulk button.
    rerender(<ReviewTab {...props({ review: review({ findings: [finding()] }) })} />)
    expect(screen.queryByRole('button', { name: 'Post all pending' })).toBeNull()
  })

  it('renders each verdict, the empty copies, and the review-another flow', async () => {
    const complete = review({ status: 'complete', verdict: 'request-changes', summary: 'Fix the open redirect.', completedAt: NOW })
    const p = props({ review: complete })
    const { rerender } = render(<ReviewTab {...p} />)
    expect(screen.getByText('Review complete')).toBeTruthy()
    expect(screen.getByText('Request changes')).toBeTruthy()
    expect(screen.getByText('Fix the open redirect.')).toBeTruthy()
    expect(screen.getByText('No findings recorded.')).toBeTruthy()
    expect(screen.queryByRole('button', { name: 'Cancel review' })).toBeNull()
    rerender(<ReviewTab {...props({ review: review({ status: 'complete', verdict: 'approve' }) })} />)
    expect(screen.getByText('Approve')).toBeTruthy()
    rerender(<ReviewTab {...props({ review: review({ status: 'cancelled', verdict: 'comment' }) })} />)
    expect(screen.getByText('Review cancelled')).toBeTruthy()
    expect(screen.getByText('Comment')).toBeTruthy()
    rerender(<ReviewTab {...props({ review: review({ status: 'running' }) })} />)
    expect(screen.getByText('The reviewer is reading the diff — findings appear here as they are recorded.')).toBeTruthy()
    // Review another: the picker replaces the run; a newly running review hides it again.
    rerender(<ReviewTab {...props({ review: complete })} />)
    fireEvent.click(screen.getByRole('button', { name: 'Review another pull request' }))
    await screen.findByText('Pick a pull request')
    expect(screen.queryByText('Review complete')).toBeNull()
    rerender(<ReviewTab {...props({ review: review({ id: 'r-2', status: 'running' }) })} />)
    expect(screen.getByText('Review in progress')).toBeTruthy()
    expect(screen.queryByText('Pick a pull request')).toBeNull()
  })
})
