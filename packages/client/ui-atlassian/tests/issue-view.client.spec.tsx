// @vitest-environment jsdom
// The issue view: identity chrome, planning facts, agent-routed actions,
// description collapse, links/subtasks/comments/attachments, and `ago()`.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { IssueView, ago } from '../src/client/panel/IssueView.tsx'
import { NOW, issue, minimalIssue, t } from './support.client.ts'

afterEach(cleanup)

const verbs = () => ({
  onPin: vi.fn(),
  onRefresh: vi.fn(),
  onPrompt: vi.fn(() => Promise.resolve()),
  onOpenIssue: vi.fn(),
})

describe('ago', () => {
  it('maps every relative-time bucket to copy', () => {
    expect(ago(t, NOW - 5_000, NOW)).toBe('just now')
    expect(ago(t, NOW - 5 * 60_000, NOW)).toBe('5m ago')
    expect(ago(t, NOW - 3 * 3_600_000, NOW)).toBe('3h ago')
    expect(ago(t, NOW - 2 * 86_400_000, NOW)).toBe('2d ago')
    expect(ago(t, undefined, NOW)).toBe('just now')
  })
})

describe('IssueView', () => {
  it('renders the full record and routes every action', async () => {
    const v = verbs()
    render(<IssueView issue={issue()} pinned={false} now={NOW} t={t} {...v} />)
    expect(screen.getByText('PROJ-123')).toBeTruthy()
    expect(screen.getByText('Story')).toBeTruthy()
    expect(screen.getByText('· PROJ')).toBeTruthy()
    expect(screen.getByRole('heading', { name: 'Login page ignores SSO redirect target' })).toBeTruthy()
    expect(screen.getAllByText('In Progress').length).toBe(2)
    expect(screen.getByText('High')).toBeTruthy()
    expect(screen.getByText('auth')).toBeTruthy()
    expect(screen.getAllByText('Avery Quinn').length).toBeGreaterThan(0)
    expect(screen.getAllByText('Jordan Alvarez').length).toBeGreaterThan(0)
    expect(screen.getByText('Sprint 42')).toBeTruthy()
    expect(screen.getAllByText('5').length).toBeGreaterThan(0)
    expect(screen.getByText('2026-08-22')).toBeTruthy()
    expect(screen.getByText('web')).toBeTruthy()
    expect(screen.getByText('2.4.0')).toBeTruthy()
    expect(screen.getByRole('link', { name: 'Open in Jira' }).getAttribute('href')).toBe('http://jira/browse/PROJ-123')

    fireEvent.click(screen.getByRole('button', { name: 'SSO hardening' }))
    expect(v.onOpenIssue).toHaveBeenCalledWith('PROJ-98')
    fireEvent.click(screen.getByRole('button', { name: 'PROJ-100' }))
    expect(v.onOpenIssue).toHaveBeenCalledWith('PROJ-100')
    fireEvent.click(screen.getByRole('button', { name: 'PROJ-124' }))
    expect(v.onOpenIssue).toHaveBeenCalledWith('PROJ-124')
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }))
    expect(v.onRefresh).toHaveBeenCalledTimes(1)
    fireEvent.click(screen.getByRole('button', { name: 'Assign to me' }))
    expect(v.onPrompt).toHaveBeenCalledWith('Assign PROJ-123 to me.')
    fireEvent.click(screen.getByRole('button', { name: "Pin as this session's ticket" }))
    expect(v.onPin).toHaveBeenCalledWith('PROJ-123')

    // Transition menu: Escape closes without a prompt; picking a target status → agent prompt.
    fireEvent.click(screen.getByRole('button', { name: 'Move to' }))
    fireEvent.keyDown(document, { key: 'Escape' })
    expect(screen.queryByRole('menuitem', { name: 'In Review' })).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Move to' }))
    fireEvent.click(screen.getByRole('menuitem', { name: 'In Review' }))
    expect(v.onPrompt).toHaveBeenCalledWith('Transition PROJ-123 to "In Review".')

    // Comment composer: prefix + text.
    fireEvent.click(screen.getByRole('button', { name: 'Comment…' }))
    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'looks good' } })
    fireEvent.click(screen.getByRole('button', { name: 'Comment' }))
    await waitFor(() => { expect(v.onPrompt).toHaveBeenCalledWith('Add a comment to PROJ-123: looks good') })

    // Comments: the fresh one carries the "new" chip; relative times render.
    expect(screen.getByText('new')).toBeTruthy()
    expect(screen.getByText('Fresh comment')).toBeTruthy()
    expect(screen.getByText('Old comment')).toBeTruthy()
    // Attachments with and without a URL.
    expect(screen.getByRole('link', { name: 'redirect-loop.png' }).getAttribute('href')).toBe('http://jira/attach/1')
    expect(screen.getByText('notes.txt').tagName).toBe('SPAN')
    expect(screen.getByText('47 kB')).toBeTruthy()
    expect(screen.getByText('12 B')).toBeTruthy()
    // Links section counts parent + links + subtasks.
    expect(screen.getByText('Linked issues')).toBeTruthy()
    expect(screen.getAllByText('3').length).toBeGreaterThan(0)
  })

  it('shows the pinned chip and unpins, and collapses a long description until "more"', () => {
    const v = verbs()
    const long = issue({ description: 'x'.repeat(800), resolution: 'Fixed' })
    render(<IssueView issue={long} pinned={true} now={NOW} t={t} {...v} />)
    expect(screen.getAllByText('Pinned').length).toBeGreaterThan(0)
    expect(screen.getByText('Fixed')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Unpin' }))
    expect(v.onPin).toHaveBeenCalledWith(null)
    fireEvent.click(screen.getByRole('button', { name: 'more' }))
    expect(screen.queryByRole('button', { name: 'more' })).toBeNull()
  })

  it('renders a minimal record: no optional facts, no links, no comments, no transitions', () => {
    const v = verbs()
    render(<IssueView issue={minimalIssue()} pinned={false} now={NOW} t={t} {...v} />)
    expect(screen.getByText('Unassigned')).toBeTruthy()
    expect(screen.getByText('No comments yet')).toBeTruthy()
    expect(screen.queryByRole('button', { name: 'Move to' })).toBeNull()
    expect(screen.queryByText('Linked issues')).toBeNull()
    expect(screen.queryByText('Description')).toBeNull()
    expect(screen.queryByText('Attachments')).toBeNull()
  })

  it('renders links and subtasks without a status chip and an epic without a name', () => {
    const v = verbs()
    render(
      <IssueView
        issue={issue({
          epic: { key: 'PROJ-77' },
          parent: undefined,
          links: [{ relation: 'relates to', key: 'PROJ-5', summary: 'Other' }],
          subtasks: [{ key: 'PROJ-6', summary: 'Sub' }],
        })}
        pinned={false}
        now={NOW}
        t={t}
        {...v}
      />,
    )
    fireEvent.click(screen.getByRole('button', { name: 'PROJ-77' }))
    expect(v.onOpenIssue).toHaveBeenCalledWith('PROJ-77')
    fireEvent.click(screen.getByRole('button', { name: 'PROJ-5' }))
    expect(v.onOpenIssue).toHaveBeenCalledWith('PROJ-5')
    fireEvent.click(screen.getByRole('button', { name: 'PROJ-6' }))
    expect(v.onOpenIssue).toHaveBeenCalledWith('PROJ-6')
    expect(screen.getByText('Linked issues')).toBeTruthy()
  })
})
