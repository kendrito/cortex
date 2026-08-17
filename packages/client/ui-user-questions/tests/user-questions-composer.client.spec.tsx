// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import type {
  ConversationSnapshot, SessionId, SessionListState, WorkspaceListState,
} from '@cortex/client-runtime/client'
import { PendingWait } from '@cortex/client-runtime/client'
import type { RpcReceipt } from '@cortex/api-remotes/client'
import { RpcId } from '@cortex/client-connection/client'
import type { SnapshotSelectorHook } from '@cortex/client-ui-slots'
import { PendingQuestion, type QuestionComposerProps } from '../src/client/contract/slots.ts'
import { QuestionComposer, parseRecommendedLabel } from '../src/client/QuestionComposer.tsx'
import { en } from '../src/client/locales.ts'
import { en as commonEn } from '@cortex/client-locale/src/locales/en.ts'

afterEach(cleanup)

const SID = 's1' as SessionId

/** Seat stub over a dictionary pair mirroring the real lookup chain: package dictionary, then common vocabulary, then the key. */
const seatOver = (dict: Record<string, string>, common: Record<string, string>): QuestionComposerProps['t'] =>
  (key => dict[key] ?? common[key] ?? key)

/** Framework standard-kit stubs: the composer consumes only the locale seat;
 *  the composed props type mandates delivery of the rest (framework hooks are
 *  plain stubs per the client testing discipline). */
const kit = {
  session: undefined,
  sessionId: SID,
  useSession: (() => { throw new Error('unused') }) as unknown as SnapshotSelectorHook<ConversationSnapshot>,
  useSessions: (() => { throw new Error('unused') }) as unknown as SnapshotSelectorHook<SessionListState>,
  useWorkspaces: (() => { throw new Error('unused') }) as unknown as SnapshotSelectorHook<WorkspaceListState>,
  useProjection: (() => undefined) as never,
  useInput: (() => { throw new Error('unused') }) as never,
  inputActions: { setDraft: () => { throw new Error('unused') }, submit: () => { throw new Error('unused') } } as never,
  // The seat's key domain is question ∪ common.
  t: seatOver(en, commonEn),
}

const QUESTIONS = [
  {
    id: 'profile', header: 'Preference', question: 'Choose a candidate profile',
    detail: 'Choose by the priority of the current openings.',
    options: [
      { label: 'Engineering-focused (Recommended)', description: 'Prioritizes engineering delivery.' },
      { label: 'Research-focused', description: 'Prioritizes research ability.' },
    ],
  },
  {
    id: 'detail', question: 'Add your requirements',
  },
  {
    id: 'signals', question: 'Choose the important signals (multi-select)', multiSelect: true,
    options: [{ label: 'System design' }, { label: 'Code quality' }, { label: 'Product judgment' }],
  },
]

/** Carrier fixture: a real PendingWait over a scripted respond carrier. */
function wait(rpcId = 'question-1', respond = vi.fn(() => Promise.resolve<RpcReceipt>({ accepted: true }))) {
  const carrier = new PendingWait(
    'question', RpcId(rpcId), SID, { questions: QUESTIONS }, respond)
  return { carrier, respond }
}

/** The client-response envelope respond must have received for an answer batch. */
function answeredEnvelope(rpcId: string, answers: object[]) {
  return {
    type: 'client-response', rpcId: RpcId(rpcId),
    result: { ok: true, value: { sessionId: SID, answer: { answers } } },
  }
}

describe('QuestionComposer', () => {
  it('collects single, custom, and multi-select answers before one batch submit', () => {
    const { carrier, respond } = wait()
    render(<QuestionComposer matched={carrier} interactions={[carrier]} {...kit} />)

    expect(screen.getByText('Preference')).toBeTruthy()
    expect(screen.getByText('1 / 3')).toBeTruthy()
    expect(screen.getByText('Recommended')).toBeTruthy()
    expect(screen.getByText('Engineering-focused')).toBeTruthy()
    const detail = screen.getByText('Choose by the priority of the current openings.')
    const scrollRegion = detail.closest('[data-question-scroll]')
    expect(scrollRegion).toBeTruthy()
    expect(scrollRegion?.contains(screen.getByRole('radio', { name: /Engineering-focused/ }))).toBe(true)
    expect(scrollRegion?.contains(screen.getByText('Next').closest('button'))).toBe(false)
    fireEvent.keyDown(screen.getByRole('radio', { name: /Engineering-focused/ }), { key: 'Enter' })
    expect(respond).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('radio', { name: /Engineering-focused/ }))

    expect(screen.getByText('2 / 3')).toBeTruthy()
    // detail is per-question: the second question carries none.
    expect(screen.queryByText('Choose by the priority of the current openings.')).toBeNull()
    const custom = screen.getByPlaceholderText('Type your answer')
    fireEvent.change(custom, { target: { value: 'Must be able to debug production issues independently' } })
    fireEvent.keyDown(custom, { key: 'Enter' })

    expect(screen.getByText('3 / 3')).toBeTruthy()
    // The model's question text renders verbatim — no marker filtering.
    expect(screen.getByText('Choose the important signals (multi-select)')).toBeTruthy()
    fireEvent.click(screen.getByRole('checkbox', { name: 'System design' }))
    fireEvent.click(screen.getByRole('checkbox', { name: 'System design' }))
    fireEvent.click(screen.getByRole('checkbox', { name: 'System design' }))
    fireEvent.click(screen.getByRole('checkbox', { name: 'Code quality' }))
    const multiCustom = screen.getByPlaceholderText('Type your answer')
    fireEvent.change(multiCustom, { target: { value: 'Communication skills' } })
    fireEvent.click(screen.getByRole('checkbox', { name: 'Product judgment' }))
    expect(screen.getByRole('checkbox', { name: 'System design' }).getAttribute('aria-checked')).toBe('true')
    expect(screen.getByRole('checkbox', { name: 'Code quality' }).getAttribute('aria-checked')).toBe('true')
    expect((multiCustom as HTMLInputElement).value).toBe('Communication skills')
    fireEvent.keyDown(multiCustom, { key: 'Enter' })

    // The domain face encoded the whole batch into one carrier envelope.
    expect(respond).toHaveBeenCalledWith(answeredEnvelope('question-1', [
      { id: 'profile', selected: ['Engineering-focused (Recommended)'] },
      { id: 'detail', selected: [], custom: 'Must be able to debug production issues independently' },
      { id: 'signals', selected: ['System design', 'Code quality', 'Product judgment'], custom: 'Communication skills' },
    ]))
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Submitting…' }).disabled).toBe(true)
  })

  it('renders plan detail through the shared assistant Markdown primitive', () => {
    const carrier = new PendingWait(
      'question',
      RpcId('markdown-plan'),
      SID,
      {
        questions: [{
          id: 'plan',
          question: 'Approve this plan?',
          detail: '# Implementation plan\n\n- **Verify first** the current state\n- Edit `QuestionComposer`',
          options: [{ label: 'Approve' }],
        }],
      },
      vi.fn(),
    )
    const view = render(<QuestionComposer matched={carrier} interactions={[carrier]} {...kit} />)

    expect(screen.getByRole('heading', { level: 1, name: 'Implementation plan' })).toBeTruthy()
    expect(view.container.querySelector('strong')?.textContent).toBe('Verify first')
    expect(view.container.querySelector('code')?.textContent).toBe('QuestionComposer')
    expect(view.container.querySelectorAll('li')).toHaveLength(2)
  })

  it('skips individual questions without discarding earlier answers', () => {
    const { carrier, respond } = wait()
    render(<QuestionComposer matched={carrier} interactions={[carrier]} {...kit} />)

    expect((screen.getByText('Next').closest('button') as HTMLButtonElement).disabled).toBe(true)
    fireEvent.click(screen.getByRole('radio', { name: 'Research-focused' }))
    expect(screen.getByText('2 / 3')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Skip this question' }))
    expect(screen.getByText('3 / 3')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Skip this question' }))

    expect(respond).toHaveBeenCalledWith(answeredEnvelope('question-1', [
      { id: 'profile', selected: ['Research-focused'] },
      { id: 'detail', selected: [] },
      { id: 'signals', selected: [] },
    ]))
  })

  it('keeps IME Enter inside the custom input until composition finishes', () => {
    const { carrier, respond } = wait()
    render(<QuestionComposer matched={carrier} interactions={[carrier]} {...kit} />)

    fireEvent.click(screen.getByRole('radio', { name: 'Research-focused' }))
    const custom = screen.getByPlaceholderText('Type your answer')
    fireEvent.change(custom, { target: { value: 'draft via IME' } })

    fireEvent.keyDown(custom, { key: 'Enter', isComposing: true })
    expect(screen.getByText('2 / 3')).toBeTruthy()
    expect(respond).not.toHaveBeenCalled()

    fireEvent.keyDown(custom, { key: 'Enter', keyCode: 229 })
    expect(screen.getByText('2 / 3')).toBeTruthy()
    expect(respond).not.toHaveBeenCalled()

    fireEvent.keyDown(custom, { key: 'Enter' })
    expect(screen.getByText('3 / 3')).toBeTruthy()
  })

  it('shows the inline custom input, reports missing answers, and supports pager navigation', () => {
    const { carrier, respond } = wait()
    render(<QuestionComposer matched={carrier} interactions={[carrier]} {...kit} />)

    expect(screen.getByPlaceholderText('Type your answer')).toBeTruthy()
    fireEvent.click(screen.getByRole('radio', { name: 'Engineering-focused' }))
    const emptyCustom = screen.getByPlaceholderText('Type your answer')
    fireEvent.keyDown(emptyCustom, { key: 'Enter', shiftKey: true })
    expect(screen.getByText('2 / 3')).toBeTruthy()
    fireEvent.keyDown(emptyCustom, { key: 'Enter' })
    expect(screen.getByText('Please select an option or enter a custom answer.')).toBeTruthy()

    fireEvent.click(screen.getByLabelText('Next question'))
    fireEvent.click(screen.getByRole('checkbox', { name: 'Product judgment' }))
    fireEvent.click(screen.getByRole('button', { name: 'Submit' }))
    expect(screen.getByText('Please complete this question first.')).toBeTruthy()
    expect(screen.getByText('2 / 3')).toBeTruthy()
    fireEvent.click(screen.getByLabelText('Previous question'))
    expect(screen.getByText('1 / 3')).toBeTruthy()
    expect(respond).not.toHaveBeenCalled()
  })

  it('surfaces cancellation failures: rejected receipt text and raw transport reasons', async () => {
    const respond = vi.fn()
      .mockResolvedValueOnce({ accepted: false, reason: 'bad-response' })
      .mockRejectedValueOnce(new Error('second cancellation failed'))
    const { carrier } = wait('question-1', respond)
    render(<QuestionComposer matched={carrier} interactions={[carrier]} {...kit} />)

    // Receipt rejection surfaces through the domain face's thrown message.
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss all questions' }))
    expect(await screen.findByText('question cancellation rejected: bad-response')).toBeTruthy()
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Skip this question' }).disabled).toBe(false)

    fireEvent.click(screen.getByRole('button', { name: 'Dismiss all questions' }))
    expect(await screen.findByText('second cancellation failed')).toBeTruthy()
  })

  it('surfaces transport rejection and resets local drafts for a different request', async () => {
    const respond = vi.fn()
      .mockRejectedValueOnce(new Error('network interrupted'))
      .mockRejectedValueOnce('string error')
    const first = wait('first', respond)
    const view = render(<QuestionComposer matched={first.carrier} interactions={[first.carrier]} {...kit} />)

    fireEvent.click(screen.getByRole('radio', { name: /Research-focused/ }))
    expect(screen.getByText('2 / 3')).toBeTruthy()
    const second = wait('second', respond)
    view.rerender(<QuestionComposer matched={second.carrier} interactions={[second.carrier]} {...kit} />)
    expect(screen.getByRole('radio', { name: /Research-focused/ }).getAttribute('aria-checked')).toBe('false')

    fireEvent.click(screen.getByRole('radio', { name: /Engineering-focused/ }))
    const custom = screen.getByPlaceholderText('Type your answer')
    fireEvent.change(custom, { target: { value: 'x' } })
    fireEvent.keyDown(custom, { key: 'Enter' })
    fireEvent.click(screen.getByRole('checkbox', { name: 'System design' }))
    fireEvent.click(screen.getByRole('button', { name: 'Submit' }))
    expect(respond).toHaveBeenNthCalledWith(1, answeredEnvelope('second', [
      { id: 'profile', selected: ['Engineering-focused (Recommended)'] },
      { id: 'detail', selected: [], custom: 'x' },
      { id: 'signals', selected: ['System design'] },
    ]))
    expect(await screen.findByText('network interrupted')).toBeTruthy()
    expect(screen.getByRole<HTMLButtonElement>('button', { name: 'Submit' }).disabled).toBe(false)

    fireEvent.click(screen.getByRole('button', { name: 'Submit' }))
    expect(await screen.findByText('string error')).toBeTruthy()
  })

  it('renders chrome copy through the English dictionary', () => {
    const respond = vi.fn(() => Promise.resolve<RpcReceipt>({ accepted: true }))
    const carrier = new PendingWait(
      'question', RpcId('solo'), SID, { questions: [{ id: 'detail', question: 'Add your requirements' }] }, respond)
    render(<QuestionComposer matched={carrier} interactions={[carrier]} {...kit} t={seatOver(en, commonEn)} />)
    expect(screen.getByLabelText('Dismiss all questions')).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Skip this question' })).toBeTruthy()
    expect(screen.getByPlaceholderText('Type your answer')).toBeTruthy()
  })

  it('same-key carrier replacement (baseline replay) keeps drafts', () => {
    const first = wait('same-id')
    const view = render(<QuestionComposer matched={first.carrier} interactions={[first.carrier]} {...kit} />)
    fireEvent.click(screen.getByRole('radio', { name: /Research-focused/ }))
    expect(screen.getByText('2 / 3')).toBeTruthy()
    // Replay mints a NEW carrier for the same request; same key = no remount.
    const replayed = wait('same-id')
    view.rerender(<QuestionComposer matched={replayed.carrier} interactions={[replayed.carrier]} {...kit} />)
    expect(screen.getByText('2 / 3')).toBeTruthy()
  })
})

describe('PendingQuestion domain face', () => {
  it('encodes the answer batch into the ok envelope and throws on a rejected receipt', async () => {
    const respond = vi.fn()
      .mockResolvedValueOnce({ accepted: true })
      .mockResolvedValueOnce({ accepted: false, reason: 'not-pending' })
    const question = new PendingQuestion(wait('rq', respond).carrier)
    const batch = { answers: [{ id: 'mode', selected: ['Fast'] }] }
    await expect(question.answer(batch)).resolves.toBeUndefined()
    expect(respond).toHaveBeenCalledWith(answeredEnvelope('rq', batch.answers))
    await expect(question.answer(batch)).rejects.toThrow(/question response rejected: not-pending/)
  })

  it('encodes cancellation as the cancelled error envelope and throws on a rejected receipt', async () => {
    const respond = vi.fn()
      .mockResolvedValueOnce({ accepted: true })
      .mockResolvedValueOnce({ accepted: false, reason: 'bad-response' })
    const question = new PendingQuestion(wait('rc', respond).carrier)
    await expect(question.cancel()).resolves.toBeUndefined()
    expect(respond).toHaveBeenCalledWith({
      type: 'client-response', rpcId: RpcId('rc'),
      result: {
        ok: false,
        error: { code: 'cancelled', message: 'the user closed this question request', details: {} },
      },
    })
    await expect(question.cancel()).rejects.toThrow(/question cancellation rejected: bad-response/)
  })

  it('forwards key and questions from the carrier', () => {
    const question = new PendingQuestion(wait('rk').carrier)
    expect(question.key).toBe('q:rk')
    expect(question.questions).toBe(wait('rk').carrier.payload.questions)
  })
})

describe('parseRecommendedLabel', () => {
  it('strips the ASCII (recommended) suffix without changing ordinary labels', () => {
    expect(parseRecommendedLabel('Fast (Recommended)')).toEqual({ label: 'Fast', recommended: true })
    expect(parseRecommendedLabel('Safe (recommended)')).toEqual({ label: 'Safe', recommended: true })
    expect(parseRecommendedLabel('Plain')).toEqual({ label: 'Plain', recommended: false })
    expect(parseRecommendedLabel('Recommended reading')).toEqual({ label: 'Recommended reading', recommended: false })
  })
})
