// @vitest-environment jsdom
/**
 * QueueDock rendering and operations: authoritative rows, inline editing,
 * collapse state, removal, strict steering, failure notices, and live retirement.
 */
import { afterEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render, waitFor } from '@testing-library/react'
import { useSyncExternalStore } from 'react'
import {
  EMPTY_CHAT_SNAPSHOT, EMPTY_CONVERSATION_VIEWS,
} from '@cortex/client-runtime/client'
import type {
  ConversationSnapshot, QueuedMessage, SessionId, SessionListState,
} from '@cortex/client-runtime/client'
import type { SnapshotSelectorHook } from '@cortex/client-ui-slots'
import { makeTranslate } from '@cortex/client-test-runtime'
import type { QueueItemId } from '../src/client/contract/queue.ts'
import type { InputState } from '../src/client/input/contract.ts'
import { QueueDock, queueDockEntry, type QueueDockInjected, type QueueDockProps } from '../src/client/queue/QueueDock.tsx'
import { en as commonEn } from '@cortex/client-locale/src/locales/index.ts'
import { en } from '../src/client/locales.ts'

afterEach(cleanup)

const SID = 's1' as SessionId
const iid = (id: string): QueueItemId => id as QueueItemId

function row(id: string, text: string | null, preview = text ?? '[image]'): QueuedMessage {
  return {
    id: iid(id), messageId: `message-${id}` as never, placement: 'queued',
    content: text === null ? [{ type: 'image', data: 'x' } as never] : [{ type: 'text', text }],
    preview, text,
  }
}

function snapshotWith(queue: QueuedMessage[]): ConversationSnapshot {
  return {
    sessionId: SID, views: EMPTY_CONVERSATION_VIEWS, chat: EMPTY_CHAT_SNAPSHOT,
    nodes: [], turnTimings: new Map(), turnEnds: new Map(), partial: null, runningCalls: [],
    pending: [], queue, running: true, composerPhase: 'active', removed: false, openState: 'open', openError: null,
    hasMore: false, loadingOlder: false, promptError: null, blank: false, subagent: null, lastAgentError: null,
  }
}

/** Minimal live source backing the useSession stub. */
function liveSession(initial: ConversationSnapshot) {
  let snapshot = initial
  const listeners = new Set<() => void>()
  const useSession: SnapshotSelectorHook<ConversationSnapshot> = selector =>
    useSyncExternalStore(
      (listener) => {
        listeners.add(listener)
        return () => listeners.delete(listener)
      },
      () => selector(snapshot),
    )
  return {
    useSession,
    push(next: ConversationSnapshot): void {
      snapshot = next
      for (const listener of [...listeners]) listener()
    },
  }
}

/** InputZone owner stub (the dock reads useSession only; the zone fields satisfy the owner share). */
const INPUT_STATE: InputState = { draft: '', imageIds: [], draftRev: 0, phase: 'plain', occurrences: [], queue: [] }

// Standard locale seat stub mirroring the real ns → common → key chain.
const t: QueueDockProps['t'] = makeTranslate(en, commonEn)

function kitFor(snapshot: ConversationSnapshot, injected: Partial<QueueDockInjected> = {}) {
  return {
    sessionId: SID,
    t,
    useSessions: (() => { throw new Error('unused') }) as unknown as SnapshotSelectorHook<SessionListState>,
    useWorkspaces: (() => { throw new Error('unused') }) as never,
    useProjection: (() => undefined) as never,
    useInput: (() => { throw new Error('unused') }) as never,
    inputActions: { setDraft: () => {}, submit: () => {} } as never,
    session: snapshot,
    input: INPUT_STATE,
    updateQueue: vi.fn(() => Promise.resolve()),
    notify: vi.fn(),
    ...injected,
  }
}

describe('QueueDock', () => {
  it('renders null while the queue is empty', () => {
    const snap = snapshotWith([])
    const source = liveSession(snap)
    const { container } = render(<QueueDock {...kitFor(snap)} useSession={source.useSession} />)
    expect(container.innerHTML).toBe('')
  })

  it('leaves pending steering to the conversation flow', () => {
    const steering = { ...row('s-1', 'interrupt'), placement: 'steering' as const }
    const snap = snapshotWith([steering])
    const source = liveSession(snap)
    const { container } = render(<QueueDock {...kitFor(snap)} useSession={source.useSession} />)
    expect(container.innerHTML).toBe('')
  })

  it('renders one row directly and defaults multiple rows to a collapsible count header', () => {
    const single = snapshotWith([row('i-1', 'one')])
    const source = liveSession(single)
    const view = render(<QueueDock {...kitFor(single)} useSession={source.useSession} />)
    expect(view.queryByRole('button', { name: '1 queued messages' })).toBeNull()
    expect(view.getByText('one')).toBeTruthy()

    act(() => { source.push(snapshotWith([row('i-1', 'one'), row('i-2', 'two')])) })
    const header = view.getByRole('button', { name: '2 queued messages' })
    expect(header.getAttribute('aria-expanded')).toBe('false')
    expect(document.getElementById(header.getAttribute('aria-controls')!)).toBeTruthy()
    expect(view.queryByText('one')).toBeNull()
    expect(view.queryByText('two')).toBeNull()

    fireEvent.click(header)
    expect(header.getAttribute('aria-expanded')).toBe('true')
    expect(view.getByText('one')).toBeTruthy()
    expect(view.getByText('two')).toBeTruthy()

    fireEvent.click(header)
    expect(header.getAttribute('aria-expanded')).toBe('false')
    expect(view.queryByText('one')).toBeNull()
  })

  it('keeps an active single-row editor visible when another item arrives', () => {
    const single = snapshotWith([row('i-edit', 'before')])
    const source = liveSession(single)
    const view = render(<QueueDock {...kitFor(single)} useSession={source.useSession} />)

    fireEvent.click(view.getByLabelText('Edit queued message'))
    fireEvent.change(view.getByLabelText('Edit queued message'), { target: { value: 'draft' } })
    act(() => {
      source.push(snapshotWith([row('i-edit', 'before'), row('i-2', 'second')]))
    })

    const header = view.getByRole('button', { name: '2 queued messages' })
    expect(header).toHaveProperty('disabled', true)
    expect(header.getAttribute('aria-expanded')).toBe('true')
    expect(view.getByRole('textbox', { name: 'Edit queued message' })).toHaveProperty('value', 'draft')
    expect(view.getByText('second')).toBeTruthy()

    fireEvent.click(view.getByLabelText('Cancel editing'))
    expect(header).toHaveProperty('disabled', false)
    expect(header.getAttribute('aria-expanded')).toBe('false')
    expect(view.queryByText('second')).toBeNull()
  })

  it('keeps an in-flight row action visible when another item arrives', async () => {
    const single = snapshotWith([row('i-remove', 'remove me')])
    const source = liveSession(single)
    let finishUpdate: (() => void) | undefined
    const updateQueue = vi.fn(() => new Promise<void>((resolve) => { finishUpdate = resolve }))
    const view = render(
      <QueueDock {...kitFor(single, { updateQueue })} useSession={source.useSession} />,
    )

    fireEvent.click(view.getByLabelText('Remove queued message'))
    act(() => {
      source.push(snapshotWith([row('i-remove', 'remove me'), row('i-2', 'second')]))
    })

    const header = view.getByRole('button', { name: '2 queued messages' })
    expect(header).toHaveProperty('disabled', true)
    expect(header.getAttribute('aria-expanded')).toBe('true')
    expect(view.getByText('remove me')).toBeTruthy()
    expect(view.getByText('second')).toBeTruthy()

    expect(updateQueue).toHaveBeenCalledOnce()
    await act(async () => {
      finishUpdate?.()
      await Promise.resolve()
    })
    await waitFor(() => {
      expect(header).toHaveProperty('disabled', false)
      expect(header.getAttribute('aria-expanded')).toBe('false')
    })
  })

  it('defaults a new multi-row queue to collapsed after the prior queue empties', () => {
    const first = snapshotWith([row('i-1', 'one'), row('i-2', 'two')])
    const source = liveSession(first)
    const view = render(<QueueDock {...kitFor(first)} useSession={source.useSession} />)
    fireEvent.click(view.getByRole('button', { name: '2 queued messages' }))
    expect(view.getByText('one')).toBeTruthy()

    act(() => { source.push(snapshotWith([])) })
    expect(view.container.innerHTML).toBe('')
    act(() => {
      source.push(snapshotWith([row('i-3', 'three'), row('i-4', 'four')]))
    })

    const header = view.getByRole('button', { name: '2 queued messages' })
    expect(header.getAttribute('aria-expanded')).toBe('false')
    expect(view.queryByText('three')).toBeNull()
  })

  it('renders active actions and disables editing for mixed-content rows', () => {
    const snap = snapshotWith([
      row('i-1', 'first queued message'),
      row('i-2', null, 'image [image]'),
    ])
    const source = liveSession(snap)
    const { container, getByRole } = render(<QueueDock {...kitFor(snap)} useSession={source.useSession} />)
    fireEvent.click(getByRole('button', { name: '2 queued messages' }))
    expect([...container.querySelectorAll('li')].map(item => item.textContent))
      .toEqual(['first queued message', 'image [image]'])
    expect(container.querySelectorAll('button')).toHaveLength(7)
    expect(container.querySelectorAll('[aria-label="Edit queued message"]')).toHaveLength(2)
    expect(container.querySelectorAll('[aria-label="Remove queued message"]')).toHaveLength(2)
    expect(container.querySelectorAll('[aria-label="Steer queued message"]')).toHaveLength(2)
    expect((container.querySelectorAll('[aria-label="Edit queued message"]')[0] as HTMLButtonElement).disabled).toBe(false)
    expect((container.querySelectorAll('[aria-label="Edit queued message"]')[1] as HTMLButtonElement).disabled).toBe(true)
    expect(container.querySelectorAll('[aria-label="Edit queued message"]')[1]?.getAttribute('title'))
      .toBe('Contains non-text content; editing is not supported yet')
  })

  it('edits text inline with save and cancel controls, then saves with the same item identity', async () => {
    const snap = snapshotWith([row('i-edit', 'before')])
    const source = liveSession(snap)
    const updateQueue = vi.fn(() => Promise.resolve())
    const { getByLabelText, queryByLabelText } = render(
      <QueueDock {...kitFor(snap, { updateQueue })} useSession={source.useSession} />,
    )

    fireEvent.click(getByLabelText('Edit queued message'))
    const editor = getByLabelText('Edit queued message') as HTMLInputElement
    expect(getByLabelText('Save queued message')).toBeTruthy()
    expect(getByLabelText('Cancel editing')).toBeTruthy()
    expect(queryByLabelText('Remove queued message')).toBeNull()
    fireEvent.change(editor, { target: { value: 'after' } })
    fireEvent.keyDown(editor, { key: 'Enter' })

    await waitFor(() => {
      expect(updateQueue).toHaveBeenCalledWith(iid('i-edit'), {
        kind: 'edit',
        content: [{ type: 'text', text: 'after' }],
      })
    })
  })

  it('cancels an edit by button or Escape without mutating the queue', () => {
    const snap = snapshotWith([row('i-edit', 'before')])
    const source = liveSession(snap)
    const updateQueue = vi.fn(() => Promise.resolve())
    const { getByLabelText, getByText } = render(
      <QueueDock {...kitFor(snap, { updateQueue })} useSession={source.useSession} />,
    )

    fireEvent.click(getByLabelText('Edit queued message'))
    fireEvent.change(getByLabelText('Edit queued message'), { target: { value: 'abandoned' } })
    fireEvent.click(getByLabelText('Cancel editing'))
    expect(getByText('before')).toBeTruthy()

    fireEvent.click(getByLabelText('Edit queued message'))
    fireEvent.keyDown(getByLabelText('Edit queued message'), { key: 'Escape' })
    expect(getByText('before')).toBeTruthy()
    expect(updateQueue).not.toHaveBeenCalled()
  })

  it('keeps editing during IME composition and disables a blank save', () => {
    const snap = snapshotWith([row('i-edit', 'before')])
    const source = liveSession(snap)
    const updateQueue = vi.fn(() => Promise.resolve())
    const { getByLabelText } = render(
      <QueueDock {...kitFor(snap, { updateQueue })} useSession={source.useSession} />,
    )

    fireEvent.click(getByLabelText('Edit queued message'))
    const editor = getByLabelText('Edit queued message')
    fireEvent.change(editor, { target: { value: '   ' } })
    expect(getByLabelText('Save queued message')).toHaveProperty('disabled', true)
    fireEvent.change(editor, { target: { value: 'typing' } })
    fireEvent.keyDown(editor, { key: 'Enter', isComposing: true })
    expect(updateQueue).not.toHaveBeenCalled()
    expect(getByLabelText('Edit queued message')).toBeTruthy()
  })

  it('removes the addressed row', async () => {
    const snap = snapshotWith([row('i-1', 'one'), row('i-2', 'two')])
    const source = liveSession(snap)
    const updateQueue = vi.fn(() => Promise.resolve())
    const { getAllByLabelText, getByRole } = render(
      <QueueDock {...kitFor(snap, { updateQueue })} useSession={source.useSession} />,
    )

    fireEvent.click(getByRole('button', { name: '2 queued messages' }))
    fireEvent.click(getAllByLabelText('Remove queued message')[0]!)
    await waitFor(() => {
      expect(updateQueue).toHaveBeenCalledWith(iid('i-1'), { kind: 'remove' })
    })
  })

  it('strictly steers complete row content only while the agent is running', async () => {
    const running = snapshotWith([row('i-steer', null, 'image [image]')])
    const source = liveSession(running)
    const updateQueue = vi.fn(() => Promise.resolve())
    const rendered = render(
      <QueueDock {...kitFor(running, { updateQueue })} useSession={source.useSession} />,
    )

    const button = rendered.getByLabelText('Steer queued message')
    expect(button).toHaveProperty('disabled', false)
    fireEvent.click(button)
    await waitFor(() => {
      expect(updateQueue).toHaveBeenCalledWith(iid('i-steer'), { kind: 'steer' })
    })

    act(() => { source.push({ ...running, running: false }) })
    expect(rendered.getByLabelText('Steer queued message')).toHaveProperty('disabled', true)
    expect(rendered.getByLabelText('Steer queued message').getAttribute('title')).toBe('Steering is available only while the agent is running')
  })

  it('renders a session-backed subagent Queue without unsupported actions', () => {
    const snap = {
      ...snapshotWith([row('i-subagent', 'pending child follow-up')]),
      subagent: {
        address: {
          parentSessionId: 'parent' as SessionId,
          childSessionId: SID,
          mode: 'continuable' as const,
        },
        parentAvailable: true,
      },
    }
    const source = liveSession(snap)
    const view = render(
      <QueueDock {...kitFor(snap)} useSession={source.useSession} />,
    )

    expect(view.getByText('pending child follow-up')).toBeTruthy()
    expect(view.queryByLabelText('Edit queued message')).toBeNull()
    expect(view.queryByLabelText('Remove queued message')).toBeNull()
    expect(view.queryByLabelText('Steer queued message')).toBeNull()
  })

  it('keeps the row and reports a genuine steer failure', async () => {
    const snap = snapshotWith([row('i-steer-race', 'pending steer')])
    const source = liveSession(snap)
    const notify = vi.fn()
    const updateQueue = vi.fn(() => Promise.reject(new Error('transport failed')))
    const { getByLabelText, getByText } = render(
      <QueueDock {...kitFor(snap, { updateQueue, notify })} useSession={source.useSession} />,
    )

    fireEvent.click(getByLabelText('Steer queued message'))
    await waitFor(() => {
      expect(notify).toHaveBeenCalledWith(
        'error',
        'Steering failed. Try again.',
      )
    })
    expect(getByText('pending steer')).toBeTruthy()
  })

  it('keeps the row and surfaces a notice when an operation loses the claim race', async () => {
    const snap = snapshotWith([row('i-race', 'pending')])
    const source = liveSession(snap)
    const notify = vi.fn()
    const updateQueue = vi.fn(() => Promise.reject(new Error('not found')))
    const { getByLabelText, getByText } = render(
      <QueueDock {...kitFor(snap, { updateQueue, notify })} useSession={source.useSession} />,
    )

    fireEvent.click(getByLabelText('Remove queued message'))
    await waitFor(() => {
      expect(notify).toHaveBeenCalledWith('error', 'Removal failed: this message may have already started sending.')
    })
    expect(getByText('pending')).toBeTruthy()
  })

  it('follows authoritative retirement back to null', () => {
    const snap = snapshotWith([row('i-1', 'present')])
    const source = liveSession(snap)
    const { container } = render(<QueueDock {...kitFor(snap)} useSession={source.useSession} />)
    expect(container.textContent).toContain('present')
    act(() => { source.push(snapshotWith([])) })
    expect(container.innerHTML).toBe('')
  })

  it('registers as the terminal composer-context entry', () => {
    expect(queueDockEntry.name).toBe('conversation-queue-dock')
    expect(queueDockEntry.inject).toEqual(['slots', 'conversation', 'sessions'])
    const register = vi.fn(() => () => undefined)
    const inject = vi.fn((_name: string, callback: () => () => void) => callback())
    queueDockEntry.apply({ slots: { inject, register } } as never)
    expect(inject).toHaveBeenCalledWith('conversation.input.dock', expect.any(Function))
    expect(register).toHaveBeenCalledWith(
      expect.objectContaining({ name: 'conversation.input.dock', id: 'queue', order: 20 }),
      QueueDock,
    )
  })
})
