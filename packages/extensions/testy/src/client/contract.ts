/** Typed props shared by the local workbench components. */
import type { PropsStore, Translate } from '@cortex/client-ui-slots'
import type { TestyInjected, TestySnapshot } from './source.ts'
import type { TestyKey } from './locales.ts'
import type { createTestyViewStore, TestyView } from './store.ts'

/** Local components receive data and actions without access to Cordis services. */
export interface WorkbenchProps {
  t: Translate<TestyKey>
  snapshot: TestySnapshot
  view: TestyView
  actions: PropsStore<ReturnType<typeof createTestyViewStore>>['actions']
  invoke: TestyInjected['invoke']
  loadMoreRuns: TestyInjected['loadMoreRuns']
  attempt: (operation: () => Promise<void>) => void
  busy: boolean
}
