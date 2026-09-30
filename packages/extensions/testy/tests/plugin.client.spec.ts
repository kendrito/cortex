// @vitest-environment jsdom
import { Context, Service } from '@cortex/cordis'
import { LocaleRuntime } from '@cortex/client-locale/client'
import { SlotRegistry } from '@cortex/client-ui-renderer/client'
import type { SessionId } from '@cortex/session/types'
import { RemoteError } from '@cortex/typert-protocol'
import { describe, expect, it, onTestFinished, vi } from 'vitest'
import { apply, inject } from '../src/client/index.ts'
import type { TestyInjected } from '../src/client/source.ts'

describe('Testy browser plugin lifecycle', () => {
  it('mounts its own namespace before injecting it, then withdraws both UI entries and the remote together', async () => {
    const ctx = new Context()
    onTestFinished(async () => { await ctx.fiber.dispose() })
    await ctx.plugin(SlotRegistry).await()
    ctx.provide('locale', new LocaleRuntime(ctx))
    let sessionId = 'first-chat' as SessionId
    const failures: { current?: RemoteError } = {}
    ctx.provide('uiWorkspace', { currentSessionId: () => sessionId } as never)
    const status = vi.fn(async () => failures.current ? { ok: false, error: failures.current } : ({ ok: true, value: {
      supported: true, connected: true, workspace: 'C:\\Testy',
      model: { provider: 'openrouter', model: 'chosen-model' }, activeRuns: 0, revision: 1,
    } }))
    const call = vi.fn(async (name: string) => failures.current ? { ok: false, error: failures.current } : ({ ok: true, value: {
      content: [], structuredContent:
      name === 'list_tests' ? { tests: [{ testId: 'one', name: 'Saved test' }] } : {},
    } }))
    const unmount = vi.fn()
    const nativeStudio = vi.fn(async () => failures.current ? { ok: false, error: failures.current } : ({ ok: true, value: undefined }))
    class RemoteService extends Service {
      constructor(serviceCtx: Context) { super(serviceCtx, 'remote') }
      async $mount(): Promise<() => Promise<void>> {
        const provider = this.ctx.plugin({ apply(scope: Context) {
          scope.provide('remote.testy', { status, tools: async () => ({ ok: true, value: [] }), call,
            nativeStudio,
          } as never)
        } })
        await provider.await()
        return async () => { unmount(); await provider.dispose() }
      }
    }
    new RemoteService(ctx)
    const slots = ctx.get('slots') as SlotRegistry
    slots.register({ name: 'root', children: {
      main: { kind: 'keyed', scope: 'root' },
      'sidebar.panellist': { kind: 'list', scope: 'root' },
    } } as never, () => null)
    const fiber = ctx.plugin({ inject, apply })
    await fiber.await()
    await vi.waitFor(() => { expect(slots.entries('main')).toHaveLength(1) })
    expect(slots.entries('sidebar.panellist')).toHaveLength(1)
    const injected: object | undefined = slots.entries('main')[0]!.inject?.()
    if (injected === undefined) throw new Error('Testy slot did not inject its data source')
    const face = injected as TestyInjected
    await face.refresh()
    expect(face.hooks.testy.getSnapshot()).toMatchObject({
      loaded: true, error: null, status: { model: { model: 'chosen-model' } }, tests: [{ name: 'Saved test' }],
    })
    expect(status).toHaveBeenCalledOnce()
    expect(call).toHaveBeenCalledWith('list_tests', {}, 'first-chat')
    await face.openStudio()
    expect(nativeStudio).toHaveBeenLastCalledWith('first-chat')
    sessionId = 'second-chat' as SessionId
    await face.invoke('list_tests', {})
    expect(call).toHaveBeenLastCalledWith('list_tests', {}, 'second-chat')
    await face.openStudio()
    expect(nativeStudio).toHaveBeenLastCalledWith('second-chat')
    // RemoteError.message is deliberately non-enumerable, so JSON serialization
    // loses the useful diagnostic that the real remote transport reconstructs.
    const failure = new RemoteError('gateway/internal', 'Testy engine disconnected while checking target readiness.', {})
    failures.current = failure
    expect(JSON.stringify(failure)).not.toContain(failure.message)
    await face.refresh()
    expect(face.hooks.testy.getSnapshot()).toMatchObject({
      error: failure.message, tests: [{ name: 'Saved test' }],
    })
    await expect(face.invoke('ai_test', { testId: 'one', instructions: 'Check readiness' })).rejects.toBe(failure)
    await expect(face.openStudio()).rejects.toBe(failure)
    await fiber.dispose()
    expect(unmount).toHaveBeenCalledOnce()
    expect(slots.entries('main')).toHaveLength(0)
    expect(slots.entries('sidebar.panellist')).toHaveLength(0)
    expect(ctx.get('remote.testy')).toBeUndefined()
  })
})
