// @vitest-environment jsdom
/**
 * Impact-matrix projection tests (row by row): what each
 * phase projects onto the InputBar — enter routing, visuals (token color /
 * hint / pending), edit freedom, and the published currency's claim seat.
 * React over jsdom per the client testing discipline; the machine is real.
 */
import { afterEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render } from '@testing-library/react'
import { bindSnapshotSelector } from '@cortex/client-web-react'
import {
  createSnapshotStore, EMPTY_CHAT_SNAPSHOT, EMPTY_CONVERSATION_VIEWS,
} from '@cortex/client-runtime/client'
import type { ClientContext, ConversationSnapshot, SessionId } from '@cortex/client-runtime/client'
import type { SubmitOutcome } from '@cortex/client-ui-input-trigger/client'
import { makeTranslate } from '@cortex/client-test-runtime'
import { SessionInputShell } from '../src/client/input/facade.ts'
import { InputBar } from '../src/client/skeleton/InputBar.tsx'
import type { InputBarProps } from '../src/client/skeleton/InputBar.tsx'
import { en as commonEn } from '@cortex/client-locale/src/locales/index.ts'
import { en } from '../src/client/locales.ts'

afterEach(cleanup)

const SCTX = {} as ClientContext
const SID = 's1' as SessionId

/** Standard-props InputBar mount over a real shell (the composer-bar entry shape). */
function mountBar(shell: SessionInputShell, over?: { running?: boolean; disabled?: boolean }) {
  const session = createSnapshotStore<ConversationSnapshot>({
    sessionId: SID, views: EMPTY_CONVERSATION_VIEWS, chat: EMPTY_CHAT_SNAPSHOT,
    nodes: [], turnTimings: new Map(), turnEnds: new Map(), partial: null, runningCalls: [],
    pending: [], queue: [], running: over?.running ?? false, composerPhase: 'active',
    removed: over?.disabled ?? false, openState: 'open', openError: null, hasMore: false,
    loadingOlder: false, promptError: null, blank: false, subagent: null, lastAgentError: null,
  })
  const props: InputBarProps = {
    sessionId: SID,
    SessionProvider: ({ children }) => children(SID),
    useSession: bindSnapshotSelector(session),
    useSessions: bindSnapshotSelector(createSnapshotStore({
      ids: [], byId: {}, current: undefined, phase: 'ready',
      subagentsByParent: {}, jobsBySession: {}, currentAddress: undefined,
    })),
    useWorkspaces: bindSnapshotSelector(createSnapshotStore({
      items: [], archivedSessionIds: [], state: 'idle', phase: 'ready', error: null,
      baselinesReady: true, recentWorkspaceId: undefined,
    })),
    useProjection: (() => undefined),
    useInput: bindSnapshotSelector(shell.state),
    inputActions: shell.actions,
    keyboard: shell,
    addImages: () => null,
    removeImage: () => {},
    draftImages: () => [],
    resolveSubmitMode: () => 'queue',
    toggleCommandMenu: vi.fn(),
    useNotices: bindSnapshotSelector(shell.notices),
    useLexicon: bindSnapshotSelector(shell.lexicon),
    useMenuLauncher: bindSnapshotSelector(createSnapshotStore<string | null>(null)),
    renderSlot: (() => null) as InputBarProps['renderSlot'],
    stop: vi.fn(),
    command: () => Promise.resolve(true),
    // Mirrors the real lookup chain (conversation namespace, then common).
    t: makeTranslate(en, commonEn),
    variant: 'composer',
  }
  return render(<InputBar {...props} />)
}

function bench(over?: { running?: boolean; disabled?: boolean; submit?: (args: string) => Promise<SubmitOutcome> }) {
  const sink = vi.fn()
  const shell = new SessionInputShell({ actx: SCTX, defaultSink: sink })
  const wiring = shell
  const view = mountBar(shell, over)
  const textarea = view.container.querySelector('textarea')!
  const claim = (token = '/goal ', hint = 'objective') => {
    act(() => {
      shell.setDraft(token)
      shell.beginCommand(
        {
          token, hint,
          submit: over?.submit ?? (() => Promise.resolve({ kind: 'success' as const, source: 'command', name: 'goal' })),
        },
        { start: 0, end: token.length, draftRev: shell.snapshot.draftRev },
      )
    })
  }
  return { view, textarea, shell, wiring, sink, claim }
}

describe('matrix row: plain', () => {
  it('enter falls to the default sink; no claim on the currency; edits free', () => {
    const { textarea, shell, sink } = bench()
    fireEvent.change(textarea, { target: { value: 'plain message' } })
    expect(shell.snapshot.claim).toBeUndefined()
    fireEvent.keyDown(textarea, { key: 'Enter' })
    expect(sink).toHaveBeenCalledWith('plain message', [], 'queue')
    expect(shell.snapshot.phase).toBe('plain')
  })
})

describe('matrix row: claimed', () => {
  it('publishes the claim currency, colors the token, hints while args are blank, and edits stay free', () => {
    const { view, textarea, shell, claim } = bench()
    claim()
    expect(shell.snapshot.claim).toEqual({ token: '/goal ', hint: 'objective' })
    expect(view.container.querySelector('[data-decoration="token"]')?.textContent).toBe('/goal ')
    // The en dictionary owns a hint.goal entry, which overrides the raw claim hint (production behavior).
    expect(view.container.querySelector('[data-decoration="hint"]')?.textContent).toBe('describe the objective for a long-running task')
    expect((textarea).readOnly).toBe(false)
    // Free editing beyond the token: hint drops, claim holds.
    fireEvent.change(textarea, { target: { value: '/goal ship the release' } })
    expect(shell.snapshot.phase).toBe('claimed')
    expect(view.container.querySelector('[data-decoration="hint"]')).toBeNull()
  })

  it('enter routes to claim.submit (command lane, never the queue sink)', async () => {
    const submit = vi.fn(() => Promise.resolve({ kind: 'success' as const, text: 'Finished', source: 'command', name: 'goal' }))
    const { view, textarea, sink, claim } = bench({ submit })
    claim()
    fireEvent.change(textarea, { target: { value: '/goal ship' } })
    fireEvent.keyDown(textarea, { key: 'Enter' })
    expect(sink).not.toHaveBeenCalled()
    await vi.waitFor(() => { expect(submit).toHaveBeenCalledWith('ship', SCTX) })
    // Commit: draft cleared, notice surfaced, back to plain.
    await vi.waitFor(() => { expect((textarea).value).toBe('') })
    expect(view.getByText('Finished')).toBeTruthy()
  })

  it('backspacing the token auto-releases to plain and the visuals vanish (scenario H)', () => {
    const { view, textarea, shell, claim } = bench()
    claim()
    fireEvent.change(textarea, { target: { value: '/goa ship' } }) // token broken
    expect(shell.snapshot.phase).toBe('plain')
    expect(shell.snapshot.claim).toBeUndefined()
    expect(view.container.querySelector('[data-decoration="token"]')).toBeNull()
  })
})

describe('matrix row: submitting', () => {
  it('locks enter, renders pending + read-only, keeps the claim snapshot on the currency', async () => {
    const submit = vi.fn(() => new Promise<SubmitOutcome>(() => {})) // never settles
    const { textarea, shell, sink, claim } = bench({ submit })
    claim()
    fireEvent.keyDown(textarea, { key: 'Enter' })
    expect(shell.snapshot.phase).toBe('submitting')
    expect(shell.snapshot.claim).toBeDefined()
    expect((textarea).readOnly).toBe(true)
    // Enter is dead inside the lock (submit dispatch is microtask-deferred).
    await vi.waitFor(() => { expect(submit).toHaveBeenCalledTimes(1) })
    fireEvent.keyDown(textarea, { key: 'Enter' })
    await Promise.resolve()
    expect(submit).toHaveBeenCalledTimes(1)
    expect(sink).not.toHaveBeenCalled()
  })

  it('rollback with unchanged draft returns to claimed with the notice; drifted draft only notices', async () => {
    let rejectSubmit!: (e: Error) => void
    const submit = vi.fn(() => new Promise<SubmitOutcome>((_res, rej) => { rejectSubmit = rej }))
    const first = bench({ submit })
    first.claim()
    fireEvent.keyDown(first.textarea, { key: 'Enter' })
    await vi.waitFor(() => { expect(submit).toHaveBeenCalled() })
    act(() => { rejectSubmit(new Error('execution failed')) })
    await vi.waitFor(() => { expect(first.shell.snapshot.phase).toBe('claimed') })
    expect((first.textarea).value).toBe('/goal ')
    expect(first.view.getByText('execution failed')).toBeTruthy()
    cleanup()
    // Drift: typing during flight wins; no restore, plain, notice only.
    const submit2 = vi.fn(() => new Promise<SubmitOutcome>((_res, rej) => { rejectSubmit = rej }))
    const second = bench({ submit: submit2 })
    second.claim()
    fireEvent.keyDown(second.textarea, { key: 'Enter' })
    await vi.waitFor(() => { expect(submit2).toHaveBeenCalled() })
    act(() => { second.shell.setDraft('new draft typed in flight') })
    act(() => { rejectSubmit(new Error('late failure')) })
    await vi.waitFor(() => { expect(second.shell.snapshot.phase).toBe('plain') })
    expect((second.textarea).value).toBe('new draft typed in flight')
    expect(second.view.getByText('late failure')).toBeTruthy()
  })
})

describe('matrix row: locked (session disabled)', () => {
  it('disables the textarea and chrome; the machine currency is untouched', () => {
    const { view, textarea, shell } = bench({ disabled: true })
    expect((textarea).disabled).toBe(true)
    expect((view.getByLabelText('Commands') as HTMLButtonElement).disabled).toBe(true)
    expect(shell.snapshot.phase).toBe('plain')
  })

  it('running does NOT lock: typing and enter-queue stay live', () => {
    const { textarea, sink } = bench({ running: true })
    expect((textarea).disabled).toBe(false)
    fireEvent.change(textarea, { target: { value: 'queued' } })
    fireEvent.keyDown(textarea, { key: 'Enter' })
    expect(sink).toHaveBeenCalledWith('queued', [], 'queue')
  })
})

describe('matrix row: takeover (orthogonal axis)', () => {
  it('the machine state survives outside the render tree (claim lives on the shell, not the DOM)', () => {
    const { view, shell, claim } = bench()
    claim()
    // Takeover hides the composer (overlay chain keeps it mounted-but-hidden);
    // even a full unmount keeps the claim: state lives on the resident shell.
    view.unmount()
    expect(shell.snapshot.phase).toBe('claimed')
    expect(shell.snapshot.claim?.token).toBe('/goal ')
    expect(shell.snapshot.draft).toBe('/goal ')
  })
})
