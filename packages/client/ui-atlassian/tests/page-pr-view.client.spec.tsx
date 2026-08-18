// @vitest-environment jsdom
// Page and pull request views: identity chrome, version/reviewer facts, the
// agent-routed prompts, and the review entry points.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { PageView } from '../src/client/panel/PageView.tsx'
import { PrView } from '../src/client/panel/PrView.tsx'
import { NOW, page, pr, review, t } from './support.client.ts'

afterEach(cleanup)

describe('PageView', () => {
  it('renders breadcrumb, version chips, labels, body, and the actions', async () => {
    const onRefresh = vi.fn()
    const onPrompt = vi.fn(() => Promise.resolve())
    render(<PageView page={page()} now={NOW} t={t} onRefresh={onRefresh} onPrompt={onPrompt} />)
    expect(screen.getByText('Engineering')).toBeTruthy()
    expect(screen.getByText('Platform')).toBeTruthy()
    expect(screen.getByText('Runbooks')).toBeTruthy()
    expect(screen.getByRole('heading', { name: 'Auth service runbook' })).toBeTruthy()
    expect(screen.getByText('v12')).toBeTruthy()
    expect(screen.getByText('by Kendrito')).toBeTruthy()
    expect(screen.getByText('3h ago')).toBeTruthy()
    expect(screen.getByText('runbook')).toBeTruthy()
    expect(screen.getByRole('link', { name: 'Open in Confluence' }).getAttribute('href')).toBe(page().url)
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }))
    expect(onRefresh).toHaveBeenCalledTimes(1)
    // Edit composer routes through the agent with the page identity prefixed.
    fireEvent.click(screen.getByRole('button', { name: 'Edit…' }))
    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'add a rollback section' } })
    fireEvent.click(screen.getByRole('button', { name: 'Edit' }))
    await waitFor(() => { expect(onPrompt).toHaveBeenCalledWith('Update the Confluence page "Auth service runbook" (id 98765): add a rollback section') })
    expect(screen.queryByText('Content shortened for display — open the page for everything.')).toBeNull()
  })

  it('renders a minimal page (no version facts, no author, no ancestors) and the truncation notice', () => {
    render(
      <PageView
        page={page({ versionAt: undefined, versionBy: undefined, author: undefined, ancestors: [], labels: [], space: { key: 'ENG' }, bodyTruncated: true })}
        now={NOW}
        t={t}
        onRefresh={vi.fn()}
        onPrompt={vi.fn(() => Promise.resolve())}
      />,
    )
    expect(screen.getByText('ENG')).toBeTruthy()
    expect(screen.queryByText(/^by /)).toBeNull()
    expect(screen.getByText('Content shortened for display — open the page for everything.')).toBeTruthy()
  })
})

describe('PrView', () => {
  const props = () => ({
    now: NOW,
    t,
    onRefresh: vi.fn(),
    onPrompt: vi.fn(() => Promise.resolve()),
    onReview: vi.fn(),
    onShowReview: vi.fn(),
  })

  it('renders identity, branches, reviewer states, and the actions of an open PR', async () => {
    const p = props()
    render(<PrView pr={pr()} review={undefined} {...p} />)
    expect(screen.getByText('PROJ/webapp#42')).toBeTruthy()
    expect(screen.getByRole('heading', { name: 'Fix SSO redirect loop after IdP callback' })).toBeTruthy()
    expect(screen.getByText('Open')).toBeTruthy()
    expect(screen.getByText('1/2 approved')).toBeTruthy()
    expect(screen.getByText('feature/PROJ-123-sso')).toBeTruthy()
    expect(screen.getByText('2d ago')).toBeTruthy()
    expect(screen.getByText('1 / 3')).toBeTruthy()
    expect(screen.getByText('Approved')).toBeTruthy()
    expect(screen.getByText('Needs work')).toBeTruthy()
    expect(screen.getByText('Pending')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }))
    expect(p.onRefresh).toHaveBeenCalledTimes(1)
    fireEvent.click(screen.getByRole('button', { name: 'Review this PR' }))
    expect(p.onReview).toHaveBeenCalledTimes(1)
    fireEvent.click(screen.getByRole('button', { name: 'Approve' }))
    expect(p.onPrompt).toHaveBeenCalledWith('Approve pull request PROJ/webapp#42.')
    fireEvent.click(screen.getByRole('button', { name: 'Merge' }))
    expect(p.onPrompt).toHaveBeenCalledWith('Merge pull request PROJ/webapp#42.')
    fireEvent.click(screen.getByRole('button', { name: 'Comment…' }))
    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'LGTM' } })
    fireEvent.click(screen.getByRole('button', { name: 'Comment' }))
    await waitFor(() => { expect(p.onPrompt).toHaveBeenCalledWith('Add a comment to pull request PROJ/webapp#42: LGTM') })
    expect(screen.getByText('Description')).toBeTruthy()
    expect(screen.queryByText('Review in progress')).toBeNull()
  })

  it('shows the review callout per status and disables Review while running', () => {
    const p = props()
    const { rerender } = render(<PrView pr={pr()} review={review({ status: 'running', findings: [] })} {...p} />)
    expect(screen.getByRole('button', { name: 'Reviewing…' }).hasAttribute('disabled')).toBe(true)
    fireEvent.click(screen.getByText('Review in progress'))
    expect(p.onShowReview).toHaveBeenCalledTimes(1)
    rerender(<PrView pr={pr()} review={review({ status: 'complete', completedAt: NOW })} {...p} />)
    expect(screen.getByText('Review complete')).toBeTruthy()
    expect(screen.getByText('· 0 finding(s)')).toBeTruthy()
    rerender(<PrView pr={pr()} review={review({ status: 'cancelled' })} {...p} />)
    expect(screen.getByText('Review cancelled')).toBeTruthy()
  })

  it('handles a merged PR without reviewers or description', () => {
    render(<PrView pr={pr({ state: 'MERGED', reviewers: [], description: '', updated: undefined })} review={undefined} {...props()} />)
    expect(screen.getByText('Merged')).toBeTruthy()
    expect(screen.getByText('No reviewers')).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Review this PR' }).hasAttribute('disabled')).toBe(true)
    expect(screen.getByRole('button', { name: 'Approve' }).hasAttribute('disabled')).toBe(true)
    expect(screen.queryByText('Description')).toBeNull()
  })
})
