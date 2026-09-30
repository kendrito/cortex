import { afterEach, describe, expect, it, vi } from 'vitest'
import type { JsonValue, TestyPluginStatus, TestyToolResult } from '../src/types.ts'
import { createTestySource, type TestyTransport } from '../src/client/source.ts'
import { checkedResult, redactFields, resultImages, resultValue, testDocument } from '../src/client/wire.ts'
import { executeTask } from '../src/client/task.ts'

const status: TestyPluginStatus = {
  supported: true, connected: true, workspace: 'C:\\Testy', model: { provider: 'openrouter', model: 'chosen-model' },
  activeRuns: 0, revision: 0,
}
const result = (value: JsonValue): TestyToolResult => ({ content: [], structuredContent: value })
function transport(): TestyTransport {
  return {
    status: vi.fn(async () => status), tools: vi.fn(async () => []), nativeStudio: vi.fn(async () => {}),
    call: vi.fn(async name => result(name === 'list_tests' ? { tests: [{ testId: 'one', name: 'Original' }] } : {})),
  }
}
afterEach(() => { vi.useRealTimers() })

describe('Testy mounted catalog', () => {
  it('does not initialize the engine on unsupported hosts', async () => {
    const host = transport()
    host.status = async () => ({ ...status, supported: false, connected: false })
    const source = createTestySource(host)
    await source.refresh()
    expect(host.call).not.toHaveBeenCalled()
    expect(source.hooks.testy.getSnapshot()).toMatchObject({ status: { supported: false }, loaded: true, error: null })
  })

  it('keeps the newer read when an earlier catalog request finishes late', async () => {
    const host = transport()
    let finish: ((value: TestyToolResult) => void) | undefined
    let reads = 0
    host.call = vi.fn(async (name) => {
      if (name !== 'list_tests') return result({})
      reads++
      if (reads === 1) return new Promise<TestyToolResult>((resolve) => { finish = resolve })
      return result({ tests: [{ testId: 'one', name: 'Newer' }] })
    })
    const source = createTestySource(host)
    const old = source.refresh()
    await Promise.resolve()
    await source.refresh()
    finish?.(result({ tests: [{ testId: 'one', name: 'Stale' }] }))
    await old
    expect(source.hooks.testy.getSnapshot().tests[0]?.name).toBe('Newer')
  })

  it('preserves the last good catalog on failure and stops polling after unmount', async () => {
    vi.useFakeTimers()
    const host = transport()
    const source = createTestySource(host)
    const unsubscribe = source.hooks.testy.subscribe(vi.fn())
    await vi.advanceTimersByTimeAsync(0)
    expect(source.hooks.testy.getSnapshot().tests).toHaveLength(1)
    host.call = vi.fn(async () => { throw new Error('Engine disconnected') })
    await vi.advanceTimersByTimeAsync(2000)
    expect(source.hooks.testy.getSnapshot()).toMatchObject({ tests: [{ name: 'Original' }], error: 'Engine disconnected' })
    unsubscribe()
    const count = vi.mocked(host.call).mock.calls.length
    await vi.advanceTimersByTimeAsync(10000)
    expect(vi.mocked(host.call).mock.calls).toHaveLength(count)
  })

  it('rejects a native revision conflict without claiming a successful save', async () => {
    const host = transport()
    host.call = vi.fn(async () => ({ isError: true, content: [], structuredContent: { message: 'Revision conflict' } }))
    const source = createTestySource(host)
    await expect(source.invoke('update_test', { testId: 'one', expectedRevision: 'old' })).rejects.toThrow('Revision conflict')
    expect(host.status).not.toHaveBeenCalled()
  })

  it('retains older history and deduplicates overlapping pages when polling inserts a newer run', async () => {
    const host = transport()
    const history = Array.from({ length: 102 }, (_, index) => ({ runId: `run-${index}`, testName: `Test ${index}` }))
    let finishPage: ((value: TestyToolResult) => void) | undefined
    host.call = vi.fn(async (name: string, args: Record<string, JsonValue>) => {
      if (name !== 'list_runs') return result({})
      if (args.offset === 100) return new Promise<TestyToolResult>((resolve) => { finishPage = resolve })
      return result({ runs: history.slice(0, 100), total: history.length, returned: 100, offset: 0, nextOffset: 100 })
    })
    const source = createTestySource(host)
    await source.refresh()
    expect(source.hooks.testy.getSnapshot().nextRunOffset).toBe(100)
    const pending = source.loadMoreRuns()
    history.unshift({ runId: 'new-run', testName: 'Newest test' })
    await source.refresh()
    finishPage?.(result({ runs: history.slice(100), total: history.length, returned: 3, offset: 100, nextOffset: null }))
    await pending
    await source.refresh()
    const snapshot = source.hooks.testy.getSnapshot()
    expect(snapshot.runs.map(run => run.runId)).toEqual(history.map(run => run.runId))
    expect(snapshot).toMatchObject({ runTotal: 103, nextRunOffset: null })
    expect(host.call).toHaveBeenCalledWith('list_runs', { limit: 100, offset: 100 })
  })
})

describe('native result projection', () => {
  it('converts native nullable selector alternatives into a valid editable input document', () => {
    expect(testDocument({ name: 'Native test', steps: [{ action: 'click', selector: 'id:Save', selectorAlternatives: null }] }))
      .toEqual({ name: 'Native test', intent: '', category: '', targetPath: '', steps: [{ action: 'click', selector: 'id:Save' }] })
  })
  it('masks nested credential values in review summaries without changing other arguments', () => {
    expect(redactFields({ machineId: 'guest', credentials: { user: 'test-user', password: 'never-display' }, token: 'hidden' }))
      .toEqual({ machineId: 'guest', credentials: { user: 'test-user', password: '••••••••' }, token: '••••••••' })
  })
  it('handles JSON text, plain messages, and raster evidence without rendering arbitrary MIME content', () => {
    expect(resultValue({ content: [{ type: 'text', text: '{"tests":[]}' }] })).toEqual({ tests: [] })
    expect(resultValue({ content: [{ type: 'text', text: 'Native message' }] })).toEqual({ message: 'Native message' })
    expect(resultImages({ content: [
      { type: 'image', mimeType: 'image/png', data: 'evidence' },
      { type: 'image', mimeType: 'text/html', data: 'document' },
    ] })).toEqual(['data:image/png;base64,evidence'])
    expect(() => checkedResult({ content: [], isError: true, structuredContent: { error: { message: 'Failed' } } })).toThrow('Failed')
  })

  it('returns the completed draft from an asynchronous native task and reports cancellation', async () => {
    vi.useFakeTimers()
    const invoke = vi.fn(async (name: string) => result(name === 'draft_test'
      ? { taskId: 'job', running: true, status: 'running' }
      : { taskId: 'job', running: false, status: 'completed', result: { draft: { name: 'Review me', steps: [] } } }))
    const progress = vi.fn()
    const pending = executeTask(invoke, 'draft_test', { pid: 42, instructions: 'Check title' }, progress)
    await vi.advanceTimersByTimeAsync(700)
    expect(await pending).toEqual({ draft: { name: 'Review me', steps: [] } })
    expect(invoke).toHaveBeenLastCalledWith('get_task', { taskId: 'job' })
    expect(progress).toHaveBeenCalledTimes(2)
    expect(progress.mock.calls[0]?.[0]).toMatchObject({ running: true })
    expect(progress.mock.calls[1]?.[0]).toMatchObject({ running: false, status: 'completed' })
    await expect(executeTask(async () => result({ taskId: 'stopped', running: false, status: 'cancelled' }), 'record_demo', {}))
      .rejects.toThrow('cancelled')
  })
})
