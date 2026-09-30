/** Workbench drafts and selection survive panel switches without disk persistence. */
import { defineStore, type EngineStoreHandle } from '@cortex/client-store'
import type { RecordValue } from './wire.ts'

/** Main workbench destinations, as in native Studio (Inspect controls is opened from the app bar). */
export type TestyTab = 'tests' | 'results' | 'inspect' | 'automate' | 'settings' | 'help'

/** Views of the open test: its steps, the run shown under Last run, or its JSON (technical details only). */
export type EditorView = 'steps' | 'lastRun' | 'json'

/** In-memory editor state, including the revision the user actually opened. */
export interface TestyView {
  tab: TestyTab
  selectedTest: string | null
  /** Run highlighted in the Results table. */
  selectedRun: string | null
  /** Run shown under the open test's Last run view (a live run, or one opened from Results). */
  shownRun: string | null
  editorView: EditorView
  targetPid: number | null
  manualTarget: boolean
  draft: RecordValue | null
  baseRevision: string | null
  dirty: boolean
  prompt: string
  technical: boolean
}

type Actions = {
  tab: (state: TestyView, tab: TestyTab) => void
  run: (state: TestyView, id: string | null) => void
  showRun: (state: TestyView, id: string) => void
  editorView: (state: TestyView, view: EditorView) => void
  target: (state: TestyView, pid: number | null) => void
  resolvedTarget: (state: TestyView, pid: number) => void
  open: (state: TestyView, id: string | null, value: RecordValue, revision: string | null) => void
  edit: (state: TestyView, value: RecordValue) => void
  close: (state: TestyView) => void
  prompt: (state: TestyView, value: string) => void
  technical: (state: TestyView, value: boolean) => void
}

/**
 * Create an isolated declared store for one Testy plugin instance.
 * @returns the store handle used by the main-panel registration.
 */
export function createTestyViewStore(): EngineStoreHandle<TestyView, Actions> {
  return defineStore({
    init: (): TestyView => ({
      tab: 'tests', selectedTest: null, selectedRun: null, shownRun: null, editorView: 'steps', targetPid: null, manualTarget: false,
      draft: null, baseRevision: null, dirty: false, prompt: '', technical: false,
    }),
    actions: {
      tab: (state, tab) => { state.tab = tab },
      // Select a run in Results.
      run: (state, id) => { state.selectedRun = id; state.tab = 'results' },
      // Show a run the way native Studio does: in Tests, on the open test's Last run view.
      showRun: (state, id) => { state.shownRun = id; state.selectedRun = id; state.tab = 'tests'; state.editorView = 'lastRun' },
      editorView: (state, view) => { state.editorView = view },
      target: (state, pid) => { state.targetPid = pid; state.manualTarget = pid !== null },
      resolvedTarget: (state, pid) => { state.targetPid = pid },
      open: (state, id, value, revision) => {
        if (state.selectedTest !== id) { state.shownRun = null; state.editorView = 'steps' }
        state.selectedTest = id; state.draft = value; state.baseRevision = revision; state.dirty = false; state.tab = 'tests'
      },
      edit: (state, value) => { state.draft = value; state.dirty = true },
      close: (state) => { state.selectedTest = null; state.draft = null; state.dirty = false; state.baseRevision = null; state.shownRun = null; state.editorView = 'steps' },
      prompt: (state, value) => { state.prompt = value },
      technical: (state, value) => { state.technical = value; if (!value && state.editorView === 'json') state.editorView = 'steps' },
    },
  })
}
