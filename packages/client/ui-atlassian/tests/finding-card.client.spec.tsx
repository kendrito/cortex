// @vitest-environment jsdom
// One review finding card: disclosure, comment editing, evidence + lazily
// loaded diff context, posting/dismissing outcomes, and the settled states.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import type { DiffContextResult, PostFindingResult } from '@cortex/atlassian/client'
import { FindingCard } from '../src/client/panel/FindingCard.tsx'
import { existingComment, finding, t } from './support.client.ts'

afterEach(cleanup)

const PR = { project: 'PROJ', repo: 'webapp', id: 42 }

const verbs = (overrides: Partial<{
  onPost: (id: string, comment: string | undefined) => Promise<PostFindingResult>
  onDiffContext: () => Promise<DiffContextResult>
}> = {}) => ({
  onPost: vi.fn(() => Promise.resolve<PostFindingResult>({ ok: true, commentId: 7, url: 'http://bb/c/7', mode: 'inline' })),
  onDismiss: vi.fn(() => Promise.resolve()),
  onDiffContext: vi.fn(() => Promise.resolve<DiffContextResult>({
    ok: true,
    file: 'src/auth/redirect.ts',
    found: true,
    lines: [
      { type: 'CONTEXT', source: 1, destination: 1, text: 'function f() {' },
      { type: 'REMOVED', source: 2, text: '  old()' },
      { type: 'ADDED', destination: 2, text: '  new()', anchor: true },
    ],
  })),
  ...overrides,
})

describe('FindingCard', () => {
  it('renders a pending finding expanded and posts the recorded comment', async () => {
    const v = verbs()
    render(<ul><FindingCard existing={[]} finding={finding()} pr={PR} t={t} {...v} /></ul>)
    expect(screen.getByText('Critical')).toBeTruthy()
    expect(screen.getByText('Correctness')).toBeTruthy()
    expect(screen.getByText('redirect.ts')).toBeTruthy()
    expect(screen.getByText('src/auth/')).toBeTruthy()
    expect(screen.getByText(':4')).toBeTruthy()
    expect(screen.getByText('Open redirect')).toBeTruthy()
    expect(screen.getByText('Validate the target before redirecting.')).toBeTruthy()
    expect(screen.getByText('The caller redirects to whatever this returns.')).toBeTruthy()
    expect(screen.queryByText('Suggested fix')).toBeNull()
    expect(screen.getByText('webapp#42')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Post comment' }))
    await waitFor(() => { expect(v.onPost).toHaveBeenCalledWith('f-1', undefined) })
  })

  it('sends the edited comment, then reports a post failure', async () => {
    const v = verbs({ onPost: vi.fn(() => Promise.resolve<PostFindingResult>({ ok: false, code: 'http', message: 'HTTP 500' })) })
    render(<ul><FindingCard existing={[]} finding={finding({ suggestion: 'return safe(state)' })} pr={PR} t={t} {...v} /></ul>)
    expect(screen.getByText('Suggested fix')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Edit comment' }))
    fireEvent.change(screen.getByRole('textbox'), { target: { value: 'Edited body' } })
    fireEvent.click(screen.getByRole('button', { name: 'Done editing' }))
    fireEvent.click(screen.getByRole('button', { name: 'Post comment' }))
    await waitFor(() => { expect(v.onPost).toHaveBeenCalledWith('f-1', 'Edited body') })
    await screen.findByText('Could not post: HTTP 500')
    // An emptied comment disables posting.
    fireEvent.click(screen.getByRole('button', { name: 'Edit comment' }))
    fireEvent.change(screen.getByRole('textbox'), { target: { value: '   ' } })
    expect(screen.getByRole('button', { name: 'Post comment' }).hasAttribute('disabled')).toBe(true)
  })

  it('dismisses through the verb', async () => {
    const v = verbs()
    render(<ul><FindingCard existing={[]} finding={finding()} pr={PR} t={t} {...v} /></ul>)
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss' }))
    await waitFor(() => { expect(v.onDismiss).toHaveBeenCalledWith('f-1') })
  })

  it('loads diff context once and marks the anchored line', async () => {
    const v = verbs()
    render(<ul><FindingCard existing={[]} finding={finding()} pr={PR} t={t} {...v} /></ul>)
    fireEvent.click(screen.getByRole('button', { name: 'Diff context' }))
    await screen.findByText('new()')
    expect(v.onDiffContext).toHaveBeenCalledTimes(1)
    expect(screen.getByText('old()')).toBeTruthy()
    expect(screen.getByText('function f() {')).toBeTruthy()
    // The button is spent after loading (no second fetch).
    expect(screen.getByRole('button', { name: 'Diff context' }).hasAttribute('disabled')).toBe(true)
    expect(screen.queryByText(/not part of the current diff/)).toBeNull()
  })

  it('notes a window whose line is not in the diff, an empty window, and a failed lookup', async () => {
    const notFound = verbs({
      onDiffContext: vi.fn(() => Promise.resolve<DiffContextResult>({ ok: true, file: 'x', found: false, lines: [{ type: 'CONTEXT', text: 'ctx' }] })),
    })
    const { unmount } = render(<ul><FindingCard existing={[]} finding={finding()} pr={PR} t={t} {...notFound} /></ul>)
    fireEvent.click(screen.getByRole('button', { name: 'Diff context' }))
    await screen.findByText(/not part of the current diff/)
    unmount()

    const empty = verbs({ onDiffContext: vi.fn(() => Promise.resolve<DiffContextResult>({ ok: true, file: 'x', found: false, lines: [] })) })
    const second = render(<ul><FindingCard existing={[]} finding={finding()} pr={PR} t={t} {...empty} /></ul>)
    fireEvent.click(screen.getByRole('button', { name: 'Diff context' }))
    await screen.findByText(/not part of the current diff/)
    second.unmount()

    const failed = verbs({ onDiffContext: vi.fn(() => Promise.resolve<DiffContextResult>({ ok: false, code: 'not-configured', message: 'no token' })) })
    render(<ul><FindingCard existing={[]} finding={finding()} pr={PR} t={t} {...failed} /></ul>)
    fireEvent.click(screen.getByRole('button', { name: 'Diff context' }))
    await screen.findByText('Diff context unavailable: no token')
  })

  it('shows the loading label while the context resolves', async () => {
    let resolve!: (value: DiffContextResult) => void
    const pending = verbs({ onDiffContext: vi.fn(() => new Promise<DiffContextResult>((done) => { resolve = done })) })
    render(<ul><FindingCard existing={[]} finding={finding()} pr={PR} t={t} {...pending} /></ul>)
    fireEvent.click(screen.getByRole('button', { name: 'Diff context' }))
    expect(screen.getByText('Loading diff context…')).toBeTruthy()
    // A second click while loading is a no-op.
    fireEvent.click(screen.getByRole('button', { name: 'Loading diff context…' }))
    expect(pending.onDiffContext).toHaveBeenCalledTimes(1)
    await act(async () => { resolve({ ok: true, file: 'x', found: true, lines: [{ type: 'CONTEXT', text: 'z' }] }) })
    expect(screen.getByText('z')).toBeTruthy()
  })

  it('renders posted findings collapsed with inline/general notes and the view link', () => {
    const { unmount } = render(<ul><FindingCard existing={[]} finding={finding({ posted: { commentId: 7, url: 'http://bb/c/7', mode: 'inline', at: 1 } })} pr={PR} t={t} {...verbs()} /></ul>)
    expect(screen.getByText('Posted')).toBeTruthy()
    expect(screen.queryByText('Proposed comment')).toBeNull()
    fireEvent.click(screen.getByRole('button', { expanded: false }))
    expect(screen.getByText('Posted inline')).toBeTruthy()
    expect(screen.getByRole('link', { name: /View on Bitbucket/ }).getAttribute('href')).toBe('http://bb/c/7')
    expect(screen.queryByRole('button', { name: 'Edit comment' })).toBeNull()
    unmount()
    render(<ul><FindingCard existing={[]} finding={finding({ posted: { commentId: 8, mode: 'general', at: 1 } })} pr={PR} t={t} {...verbs()} /></ul>)
    fireEvent.click(screen.getByRole('button', { expanded: false }))
    expect(screen.getByText('Posted as general comment (line not in diff)')).toBeTruthy()
    expect(screen.queryByRole('link', { name: /View on Bitbucket/ })).toBeNull()
  })

  it('renders a dismissed finding collapsed and toggles through the keyboard', () => {
    render(<ul><FindingCard existing={[]} finding={finding({ dismissed: true, file: 'README', severity: 'nit', category: 'style' })} pr={PR} t={t} {...verbs()} /></ul>)
    const head = screen.getByRole('button', { expanded: false })
    expect(screen.getAllByText('Dismissed').length).toBe(1)
    fireEvent.keyDown(head, { key: 'x' })
    expect(head.getAttribute('aria-expanded')).toBe('false')
    fireEvent.keyDown(head, { key: 'Enter' })
    expect(head.getAttribute('aria-expanded')).toBe('true')
    expect(screen.getAllByText('Dismissed').length).toBe(2)
    fireEvent.keyDown(head, { key: ' ' })
    expect(head.getAttribute('aria-expanded')).toBe('false')
    // A bare file name has no directory part.
    expect(screen.getByText('README')).toBeTruthy()
  })

  it('marks a finding recorded near an existing comment and lists that comment', () => {
    const existing = [existingComment({ id: 501 }), existingComment({ id: 502, file: undefined, line: undefined, text: 'General remark' })]
    render(<ul><FindingCard existing={existing} finding={finding({ overlaps: [501, 502, 999] })} pr={PR} t={t} {...verbs()} /></ul>)
    expect(screen.getAllByText('Near an existing comment').length).toBe(2)
    expect(screen.getByText('Please validate the redirect target here.')).toBeTruthy()
    expect(screen.getByText('src/auth/redirect.ts:3')).toBeTruthy()
    expect(screen.getByText('general comment')).toBeTruthy()
    expect(screen.getByText('General remark')).toBeTruthy()
  })

  it('shortens deep paths and picks a language per extension', () => {
    const files = ['a/b/c/d/e/f.py', 'x.tsx', 'x.unknownext', 'Makefile']
    for (const file of files) {
      const { unmount } = render(<ul><FindingCard existing={[]} finding={finding({ file })} pr={PR} t={t} {...verbs()} /></ul>)
      unmount()
    }
    render(<ul><FindingCard existing={[]} finding={finding({ file: 'a/b/c/d/e/f.py' })} pr={PR} t={t} {...verbs()} /></ul>)
    expect(screen.getByText('a/…/e/')).toBeTruthy()
    expect(screen.getByText('f.py')).toBeTruthy()
  })
})
