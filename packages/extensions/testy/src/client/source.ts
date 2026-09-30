/** One mounted workbench catalog, with refreshes that cannot overwrite newer reads. */
import type { HostObservable } from '@cortex/client-ui-slots'
import type { JsonValue, TestyPluginStatus, TestyToolResult } from '../types.ts'
import { checkedResult, number, record, resultValue, rows, toolCatalog, type RecordValue, type TestyTool } from './wire.ts'

/** Framework-owned snapshot of native catalog availability. */
export interface TestySnapshot {
  status: TestyPluginStatus | null
  tools: TestyTool[]
  tests: RecordValue[]
  runs: RecordValue[]
  runTotal: number
  nextRunOffset: number | null
  apps: RecordValue[]
  tasks: RecordValue[]
  loading: boolean
  error: string | null
  loaded: boolean
}

/** Transport-independent callbacks used by the client source. */
export interface TestyTransport {
  status: () => Promise<TestyPluginStatus>
  tools: () => Promise<JsonValue[]>
  call: (name: string, args: RecordValue) => Promise<TestyToolResult>
  nativeStudio: () => Promise<void>
}

/** Plain actions and observable data injected into the main panel. */
export interface TestyInjected {
  hooks: { testy: HostObservable<TestySnapshot> }
  refresh: () => Promise<void>
  loadMoreRuns: () => Promise<void>
  invoke: (name: string, args: RecordValue) => Promise<TestyToolResult>
  openStudio: () => Promise<void>
}

function mergeRuns(first: RecordValue[], second: RecordValue[]): RecordValue[] {
  const seen = new Set<JsonValue | undefined>()
  return [...first, ...second].filter((run) => {
    if (seen.has(run.runId)) return false
    seen.add(run.runId)
    return true
  })
}

/**
 * Create a mounted-only poller. Mutation readbacks supersede reads begun before the write.
 * @param transport - typed remote wrappers; each rejects transport failures.
 * @returns shared catalog and native operations, without React dependencies.
 */
export function createTestySource(transport: TestyTransport): TestyInjected {
  let snapshot: TestySnapshot = {
    status: null, tools: [], tests: [], runs: [], runTotal: 0, nextRunOffset: null,
    apps: [], tasks: [], loading: true, loaded: false, error: null,
  }
  const listeners = new Set<() => void>()
  let epoch = 0
  let olderEpoch = 0
  let olderPending = false
  let timer: ReturnType<typeof setTimeout> | undefined
  const publish = (next: TestySnapshot): void => {
    snapshot = next
    for (const listener of listeners) listener()
  }
  const refresh = async (): Promise<void> => {
    const current = ++epoch
    if (timer !== undefined) clearTimeout(timer)
    try {
      const status = await transport.status()
      if (current !== epoch) return
      if (!status.supported) {
        publish({ ...snapshot, status, loading: false, loaded: true, error: null })
        return
      }
      const [tools, tests, runs, apps, tasks] = await Promise.all([
        snapshot.tools.length ? Promise.resolve(snapshot.tools) : transport.tools().then(toolCatalog),
        transport.call('list_tests', {}).then(checkedResult).then(resultValue),
        transport.call('list_runs', { limit: 100 }).then(checkedResult).then(resultValue),
        transport.call('list_apps', {}).then(checkedResult).then(resultValue),
        transport.call('list_tasks', {}).then(checkedResult).then(resultValue),
      ])
      if (current !== epoch) return
      const allRuns = mergeRuns(rows(record(runs).runs), snapshot.runs)
      const runTotal = Math.max(number(record(runs).total), allRuns.length)
      publish({ status, tools, tests: rows(record(tests).tests), runs: allRuns, runTotal,
        nextRunOffset: allRuns.length < runTotal ? allRuns.length : null,
        apps: rows(record(apps).apps), tasks: rows(record(tasks).tasks), loading: false, loaded: true, error: null })
    } catch (error) {
      if (current === epoch) publish({ ...snapshot, loading: false, error: error instanceof Error ? error.message : String(error) })
    } finally {
      if (listeners.size && current === epoch) timer = setTimeout(() => { void refresh() }, 2000)
    }
  }
  return {
    hooks: { testy: {
      getSnapshot: () => snapshot,
      subscribe(listener) {
        listeners.add(listener)
        if (listeners.size === 1) void refresh()
        return () => {
          listeners.delete(listener)
          if (listeners.size) return
          epoch++
          olderEpoch++; olderPending = false
          if (timer !== undefined) clearTimeout(timer)
          timer = undefined
        }
      },
    } },
    refresh,
    async loadMoreRuns() {
      if (olderPending || snapshot.nextRunOffset === null) return
      olderPending = true
      const current = ++olderEpoch
      try {
        const page = record(resultValue(checkedResult(await transport.call('list_runs', { limit: 100, offset: snapshot.nextRunOffset }))))
        if (current !== olderEpoch) return
        const runs = mergeRuns(snapshot.runs, rows(page.runs))
        const runTotal = Math.max(number(page.total), snapshot.runTotal, runs.length)
        publish({ ...snapshot, runs, runTotal, nextRunOffset: runs.length < runTotal ? runs.length : null, error: null })
      } finally {
        if (current === olderEpoch) olderPending = false
      }
    },
    async invoke(name, args) {
      const result = checkedResult(await transport.call(name, args))
      if (!/^(get_|list_|inspect_|screenshot_|validate_)/.test(name)) await refresh()
      return result
    },
    openStudio: transport.nativeStudio,
  }
}
