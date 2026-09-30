// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { bindSnapshotSelector, makeTranslate } from '@cortex/client-test-runtime'
import { TestyPage, type TestyPageProps } from '../src/client/TestyPage.tsx'
import type {} from '../src/client/index.ts'
import { createTestyViewStore } from '../src/client/store.ts'
import { en } from '../src/client/locales.ts'
import type { TestySnapshot } from '../src/client/source.ts'
import type { JsonValue, TestyToolResult } from '../src/types.ts'
import { SchemaField } from '../src/client/SchemaFields.tsx'
import * as wire from '../src/client/wire.ts'

afterEach(() => { cleanup(); vi.restoreAllMocks() })
const output = (value: JsonValue): TestyToolResult => ({ content: [], structuredContent: value })
const document = { name: 'Check application', intent: 'Verify title', category: 'Smoke', targetPath: 'C:\\Apps\\Target.exe',
  steps: [{ id: 'step-a', title: 'Check heading', action: 'assertText', selector: 'id:Title', value: 'Welcome', timeoutMs: 5000 }] }

function mount(initial: Partial<TestySnapshot> = {}) {
  const store = createTestyViewStore().create()
  store.actions.open('test-one', document, 'original-revision')
  let snapshot: TestySnapshot = {
    status: { supported: true, connected: true, workspace: 'C:\\Testy', model: { provider: 'openrouter', model: 'Current model' }, activeRuns: 0, revision: 0 },
    tools: [], apps: [{ pid: 42, title: 'Target' }], runs: [], runTotal: initial.runs?.length ?? 0, nextRunOffset: null, tasks: [], loading: false, loaded: true, error: null,
    tests: [{ testId: 'test-one', name: document.name, intent: document.intent, revision: 'original-revision', stepCount: 1 }], ...initial,
  }
  const invoke = vi.fn<TestyPageProps['invoke']>(async (name, args) => {
    if (name === 'get_test') return output({ ...document, testId: args.testId ?? '', revision: 'saved-revision' })
    if (name === 'create_test') return output({ testId: 'copy-id' })
    if (name === 'update_test') return output({ testId: 'test-one' })
    if (name === 'run_test') return output({ runId: 'run-one', running: true })
    if (name === 'get_run') return output({ runId: 'run-one', testName: document.name, steps: [], running: true })
    return output({})
  })
  const props: TestyPageProps = {
    t: makeTranslate(en), useTesty: select => select(snapshot), useStore: bindSnapshotSelector(store), actions: store.actions,
    invoke, refresh: vi.fn(async () => {}), loadMoreRuns: vi.fn(async () => {}), openStudio: vi.fn(async () => {}),
    useSessions: () => { throw new Error('Testy reads its own saved tests') },
    useWorkspaces: () => { throw new Error('Testy workspace comes from its engine status') },
    usePanelInfo: select => select({ activePanelId: null }),
    useSessionStatus: select => select(new Map()), useSessionRetainInfo: () => undefined,
    useResource: () => { throw new Error('Testy evidence uses its native MCP image tool') },
  }
  const page = render(<TestyPage {...props} />)
  return { store, invoke, loadMoreRuns: props.loadMoreRuns, ...page,
    update(next: Partial<TestySnapshot>) { snapshot = { ...snapshot, ...next }; page.rerender(<TestyPage {...props} />) },
  }
}

describe('integrated Testy editor', () => {
  it('preserves a dirty draft after an external change and requires reload or save copy', async () => {
    const page = mount()
    fireEvent.change(screen.getByLabelText('Test name'), { target: { value: 'My unsaved draft' } })
    await waitFor(() => { expect(page.store.getSnapshot().dirty).toBe(true) })
    page.update({ tests: [{ testId: 'test-one', name: 'Changed outside', revision: 'external-revision' }] })
    expect(screen.getByDisplayValue('My unsaved draft')).toBeTruthy()
    expect(screen.getByText(en.changedOutside)).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Save changes' }).hasAttribute('disabled')).toBe(true)
    fireEvent.click(screen.getByRole('button', { name: 'Reload saved version' }))
    await waitFor(() => { expect(page.store.getSnapshot().dirty).toBe(false) })
    expect(page.invoke).toHaveBeenCalledWith('get_test', { testId: 'test-one' })
  })

  it('keeps Save changes visible, exports from More actions and duplicates the current draft, as native Studio does', async () => {
    const page = mount()
    const download = vi.spyOn(wire, 'downloadJson').mockImplementation(() => {})
    fireEvent.change(screen.getByLabelText('Test name'), { target: { value: 'My unsaved copy' } })
    expect(screen.getByRole('button', { name: 'Save changes' })).toBeTruthy()
    expect(screen.queryByRole('button', { name: 'Export JSON' })).toBeNull()
    expect(screen.queryByRole('button', { name: 'Save a copy' })).toBeNull()
    expect(screen.queryByRole('button', { name: 'Delete test' })).toBeNull()
    const trigger = screen.getByRole('button', { name: 'Test actions' })
    fireEvent.click(trigger)
    expect(trigger.getAttribute('aria-expanded')).toBe('true')
    fireEvent.click(screen.getByRole('menuitem', { name: 'Export JSON' }))
    expect(download).toHaveBeenCalledWith('My unsaved copy', { ...document, name: 'My unsaved copy' })
    expect(screen.queryByRole('menu')).toBeNull()
    expect(page.invoke).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Duplicate' }))
    await waitFor(() => { expect(page.invoke).toHaveBeenCalledWith('create_test', { ...document, name: 'My unsaved copy' }) })
    expect(page.invoke.mock.calls.some(([name]) => name === 'update_test')).toBe(false)
    await waitFor(() => { expect(page.store.getSnapshot().selectedTest).toBe('copy-id') })
    expect(screen.queryByRole('menu')).toBeNull()
  })

  it('requires confirmation before deleting a saved test', async () => {
    const page = mount()
    fireEvent.click(screen.getByRole('button', { name: 'Delete' }))
    const dialog = screen.getByRole('dialog', { name: 'Delete this test?' })
    expect(page.invoke).not.toHaveBeenCalled()
    fireEvent.click(within(dialog).getByRole('button', { name: 'Delete test' }))
    await waitFor(() => { expect(page.invoke).toHaveBeenCalledWith('delete_test', {
      testId: 'test-one', expectedRevision: 'original-revision',
    }) })
    await waitFor(() => { expect(page.store.getSnapshot().draft).toBeNull() })
  })

  it('saves the opened revision before starting an exact replay and shows the run under Last run', async () => {
    const page = mount()
    fireEvent.change(screen.getByRole('combobox', { name: 'Run mode' }), { target: { value: 'replay' } })
    fireEvent.click(screen.getByRole('button', { name: 'Run test' }))
    await waitFor(() => { expect(page.invoke).toHaveBeenCalledWith('run_test', {
      testId: 'test-one', mode: 'replay', wait: false, exe: 'C:\\Apps\\Target.exe',
    }) })
    expect(page.invoke.mock.calls[0]).toEqual(['update_test', { ...document, testId: 'test-one', expectedRevision: 'original-revision' }])
    await waitFor(() => { expect(page.store.getSnapshot()).toMatchObject({ shownRun: 'run-one', selectedRun: 'run-one', tab: 'tests', editorView: 'lastRun' }) })
    expect(screen.getByRole('tab', { name: 'Last run' }).getAttribute('aria-selected')).toBe('true')
    await waitFor(() => { expect(page.invoke).toHaveBeenCalledWith('get_run', { runId: 'run-one' }) })
    expect(screen.getByRole('heading', { name: 'Run trace' })).toBeTruthy()
  })

  it('retains the draft across panel switches and asks before discarding it for a new test', async () => {
    const page = mount()
    fireEvent.change(screen.getByLabelText('Test name'), { target: { value: 'Keep this draft' } })
    await waitFor(() => { expect(page.store.getSnapshot().dirty).toBe(true) })
    fireEvent.click(screen.getByRole('tab', { name: 'Results' }))
    fireEvent.click(screen.getByRole('tab', { name: 'Tests' }))
    await waitFor(() => { expect(screen.getByDisplayValue('Keep this draft')).toBeTruthy() })
    fireEvent.click(screen.getByRole('button', { name: 'New test' }))
    const dialog = screen.getByRole('dialog', { name: en.switchTitle })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Keep editing' }))
    expect(page.store.getSnapshot().draft?.name).toBe('Keep this draft')
  })

  it('imports JSON through More actions only after confirming replacement of a dirty draft', async () => {
    const page = mount()
    fireEvent.change(screen.getByLabelText('Test name'), { target: { value: 'Keep this unsaved draft' } })
    const imported = { ...document, name: 'Imported test' }
    const file = new File([JSON.stringify(imported)], 'test.json', { type: 'application/json' })
    const readFile = vi.fn(async () => JSON.stringify(imported))
    Object.defineProperty(file, 'text', { value: readFile })
    const input = screen.getByLabelText<HTMLInputElement>('Import JSON')
    const chooseFile = vi.spyOn(input, 'click').mockImplementation(() => {})
    expect(input.hidden).toBe(true)
    fireEvent.click(screen.getByRole('button', { name: 'Test actions' }))
    fireEvent.click(screen.getByRole('menuitem', { name: 'Import JSON' }))
    expect(chooseFile).toHaveBeenCalledOnce()
    expect(screen.queryByRole('menu')).toBeNull()
    fireEvent.change(input, { target: { files: [file] } })
    const dialog = await screen.findByRole('dialog', { name: en.switchTitle })
    expect(readFile).toHaveBeenCalledOnce()
    expect(page.store.getSnapshot()).toMatchObject({
      selectedTest: 'test-one', dirty: true, draft: { name: 'Keep this unsaved draft' },
    })
    expect(page.invoke).not.toHaveBeenCalled()
    fireEvent.click(within(dialog).getByRole('button', { name: 'Discard changes' }))
    await waitFor(() => { expect(page.store.getSnapshot()).toMatchObject({
      selectedTest: null, baseRevision: null, dirty: true, draft: imported,
    }) })
    expect(screen.getByDisplayValue('Imported test')).toBeTruthy()
    expect(page.invoke).not.toHaveBeenCalled()
  })

  it('opens the selected test’s latest run instead of a previously viewed run from another test', async () => {
    const page = mount({ runs: [
      { runId: 'other-run', testId: 'other-test' },
      { runId: 'latest-run', testId: 'test-one' },
      { runId: 'older-run', testId: 'test-one' },
    ] })
    act(() => { page.store.actions.run('other-run'); page.store.actions.tab('tests') })
    fireEvent.click(screen.getByRole('tab', { name: 'Last run' }))
    expect(page.store.getSnapshot().editorView).toBe('lastRun')
    await waitFor(() => { expect(page.invoke).toHaveBeenCalledWith('get_run', { runId: 'latest-run' }) })
    expect(page.invoke.mock.calls.some(([, args]) => args.runId === 'other-run')).toBe(false)
    act(() => { page.store.actions.open('not-run-yet', document, null) })
    expect(page.store.getSnapshot().editorView).toBe('steps')
    fireEvent.click(screen.getByRole('tab', { name: 'Last run' }))
    expect(screen.getByText(en.noRunYet)).toBeTruthy()
  })

  it('uses the engine schema to save a suite from selected existing tests', async () => {
    const page = mount({ tools: [{ name: 'save_suite', title: 'Save test set', group: 'suites', description: 'Save a set.',
      inputSchema: { type: 'object', required: ['name', 'testIds'], properties: {
        name: { type: 'string' }, testIds: { type: 'array', items: { type: 'string' } }, repetitions: { type: 'integer', minimum: 1, default: 1 },
      } },
    }] })
    page.invoke.mockResolvedValue(output({ suiteId: 'suite-one', name: 'Smoke suite', testIds: ['test-one'], repetitions: 1 }))
    fireEvent.click(screen.getByRole('tab', { name: 'Automate' }))
    await waitFor(() => { expect(screen.getByRole('button', { name: 'Save test set' })).toBeTruthy() })
    fireEvent.click(screen.getByRole('button', { name: 'Save test set' }))
    fireEvent.change(screen.getByRole('textbox', { name: 'Name' }), { target: { value: 'Smoke suite' } })
    fireEvent.click(screen.getByLabelText('Check application'))
    fireEvent.click(screen.getByRole('button', { name: 'Run operation' }))
    await waitFor(() => { expect(page.invoke).toHaveBeenCalledWith('save_suite', { name: 'Smoke suite', testIds: ['test-one'], repetitions: 1 }) })
    const result = screen.getByRole('region', { name: 'Operation result' })
    expect(within(result).getByRole('status').textContent).toBe('Saved: Smoke suite')
    expect(within(result).getByText('Technical result').parentElement?.hasAttribute('open')).toBe(false)
    fireEvent.click(screen.getByRole('switch', { name: 'Show technical details' }))
    expect(within(result).getByText('Technical result').parentElement?.hasAttribute('open')).toBe(true)
  })

  it('never asks the user to pick an app: Cortex finds it, and Inspect uses the app Cortex connected', () => {
    const page = mount({ tools: [
      { name: 'launch_sample', title: 'Open sample app', description: '', group: 'authoring', inputSchema: { type: 'object' } },
      { name: 'get_preferences', title: 'Read preferences', description: '', group: 'settings', inputSchema: { type: 'object' } },
    ] })
    expect(screen.getByText('No app yet')).toBeTruthy()
    expect(screen.getByText(en.aiPicksApp)).toBeTruthy()
    expect(screen.queryByRole('button', { name: 'Change app' })).toBeNull()
    expect(screen.queryByRole('button', { name: 'Sample app' })).toBeNull()
    expect(screen.queryByRole('combobox', { name: 'Application' })).toBeNull()
    expect(screen.queryByText(en.targetPath)).toBeNull()
    expect(screen.queryByRole('button', { name: 'Native Studio' })).toBeNull()
    expect(screen.getByRole('button', { name: 'Test with AI' })).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Inspect controls' }))
    expect(page.store.getSnapshot().tab).toBe('inspect')
    expect(screen.getByText(en.inspectNoApp)).toBeTruthy()
    expect(screen.queryByRole('button', { name: 'Choose an app manually' })).toBeNull()
    expect(screen.getByRole('button', { name: 'Scan controls' }).hasAttribute('disabled')).toBe(true)
    act(() => { page.store.actions.resolvedTarget(42) })
    expect(screen.getByText(en.chosenByAi)).toBeTruthy()
    expect(screen.getByRole('button', { name: 'Scan controls' }).hasAttribute('disabled')).toBe(false)
    fireEvent.click(screen.getByRole('tab', { name: 'Automate' }))
    fireEvent.click(screen.getByRole('button', { name: 'All operations' }))
    expect(screen.queryByRole('button', { name: 'Open sample app' })).toBeNull()
    expect(screen.getByRole('button', { name: 'Read preferences' })).toBeTruthy()
    expect(page.invoke).not.toHaveBeenCalled()
  })

  it('logs runs started by anyone, including Cortex agents, in Activity as native Studio does', () => {
    const page = mount()
    page.update({ runs: [{ runId: 'agent-run', testId: 'test-one', testName: 'Agent test', running: true, status: 'running' }] })
    page.update({ runs: [{ runId: 'agent-run', testId: 'test-one', testName: 'Agent test', running: false, status: 'passed', summary: '2 of 2 steps passed' }] })
    fireEvent.click(screen.getByRole('button', { name: 'Activity' }))
    const pane = screen.getByRole('complementary', { name: 'Activity' })
    expect(within(pane).getByText('Run started: Agent test')).toBeTruthy()
    expect(within(pane).getByText('Agent test: 2 of 2 steps passed')).toBeTruthy()
    expect(page.invoke).not.toHaveBeenCalled()
  })

  it('adds an inspected control to the existing draft without sending input to the application', async () => {
    const page = mount()
    page.invoke.mockImplementation(async name => output(name === 'inspect_app'
      ? { elements: [{ selector: 'id:Save', name: 'Save', controlType: 'Button', depth: 1 }], hints: [] } : {}))
    act(() => { page.store.actions.target(42); page.store.actions.tab('inspect') })
    await waitFor(() => { expect(screen.getByRole('button', { name: 'Scan controls' })).toBeTruthy() })
    fireEvent.click(screen.getByRole('button', { name: 'Scan controls' }))
    await waitFor(() => { expect(screen.getByRole('button', { name: 'Save (Button)' })).toBeTruthy() })
    fireEvent.click(screen.getByRole('button', { name: 'Save (Button)' }))
    fireEvent.click(screen.getByRole('button', { name: 'Add click to test' }))
    await waitFor(() => { expect(page.store.getSnapshot().tab).toBe('tests') })
    expect(page.store.getSnapshot().draft?.steps).toEqual([...document.steps, {
      action: 'click', selector: 'id:Save', title: 'Save', value: '', timeoutMs: 5000,
    }])
    expect(page.invoke.mock.calls.every(([name]) => name === 'inspect_app')).toBe(true)
  })
})

describe('saved run evidence', () => {
  it('loads older history without changing the selected run and downloads the native standalone report', async () => {
    const page = mount({ runs: [{ runId: 'run-one', testId: 'test-one', testName: document.name }], runTotal: 101, nextRunOffset: 100 })
    const html = '<!doctype html><html><body>Saved evidence<img src="data:image/png;base64,aW1hZ2U="></body></html>'
    page.invoke.mockImplementation(async name => output(name === 'get_run_report'
      ? { filename: 'testy-run-one.html', mimeType: 'text/html', html }
      : { runId: 'run-one', testName: document.name, status: 'passed', steps: [], running: false }))
    const download = vi.spyOn(wire, 'downloadHtmlReport').mockImplementation(() => {})
    act(() => { page.store.actions.run('run-one') })
    await waitFor(() => { expect(screen.getByRole('button', { name: 'Download report' })).toBeTruthy() })
    fireEvent.click(screen.getByRole('button', { name: 'Load older runs' }))
    await waitFor(() => { expect(page.loadMoreRuns).toHaveBeenCalledOnce() })
    expect(page.store.getSnapshot().selectedRun).toBe('run-one')
    fireEvent.click(screen.getByRole('button', { name: 'Download report' }))
    await waitFor(() => { expect(download).toHaveBeenCalledWith('testy-run-one.html', html) })
    expect(page.invoke).toHaveBeenCalledWith('get_run_report', { runId: 'run-one' })
    expect(screen.queryByRole('button', { name: 'Export run report' })).toBeNull()
  })
})

describe('native workflow selections', () => {
  const workflows: { group: string; category: string; list: string; action: string; field: string; value: string; payload: JsonValue }[] = [
    { group: 'lifecycle', category: 'Build then test', list: 'list_lifecycles', action: 'run_lifecycle', field: 'lifecycleId', value: 'build-one',
      payload: { lifecycles: [{ profile: { id: 'build-one', name: 'Build Customer Desk', executable: 'C:\\Apps\\Target.exe', preparationInstructions: 'Build the app', replay: false }, test: { id: 'test-one' }, targetArguments: [] }] },
    },
    { group: 'machines', category: 'Virtual machines', list: 'list_machines', action: 'check_machine', field: 'machineId', value: 'guest-one',
      payload: { taskId: 'inventory-task', running: false, status: 'completed', result: { result: { hyperVAvailable: true, machines: [{ vmId: 'guest-one', name: 'Test guest', state: 'Running' }] }, exitCode: 0 } },
    },
  ]
  it.each(workflows)('populates $action from the selected native $group record', async ({ group, category, list, action, field, value, payload }) => {
    const page = mount({ tools: [
      { name: list, title: 'List saved records', description: '', group, inputSchema: { type: 'object', properties: {} } },
      { name: action, title: 'Use selected record', description: '', group, inputSchema: { type: 'object', required: [field], properties: { [field]: { type: 'string' } } } },
    ] })
    page.invoke.mockImplementation(async name => output(name === list ? payload : { completed: true }))
    fireEvent.click(screen.getByRole('tab', { name: 'Automate' }))
    fireEvent.click(screen.getByRole('button', { name: category }))
    fireEvent.click(screen.getByRole('button', { name: 'List saved records' }))
    fireEvent.click(screen.getByRole('button', { name: 'Run operation' }))
    await waitFor(() => { expect(screen.getByRole('button', { name: 'Use this record' })).toBeTruthy() })
    fireEvent.click(screen.getByRole('button', { name: 'Use this record' }))
    fireEvent.click(screen.getByRole('button', { name: 'Use selected record' }))
    expect(screen.getByRole('textbox').getAttribute('value')).toBe(value)
    fireEvent.click(screen.getByRole('button', { name: 'Run operation' }))
    await waitFor(() => { expect(page.invoke).toHaveBeenCalledWith(action, { [field]: value }) })
  })
})

describe('schema argument forms', () => {
  it('omits an optional inline draft and validates its required fields only when explicitly included', async () => {
    const page = mount({ tools: [{ name: 'draft_test', title: 'Draft a test', description: '', group: 'authoring',
      inputSchema: { type: 'object', required: ['instructions'], properties: {
        instructions: { type: 'string' }, draft: { type: 'object', required: ['name', 'steps'], properties: {
          name: { type: 'string' }, steps: { type: 'array', items: { type: 'object' } },
        } },
      } },
    }] })
    fireEvent.click(screen.getByRole('tab', { name: 'Automate' }))
    fireEvent.click(screen.getByRole('button', { name: 'All operations' }))
    fireEvent.click(screen.getByRole('button', { name: 'Draft a test' }))
    fireEvent.change(screen.getByRole('textbox', { name: 'Instructions' }), { target: { value: 'Check the welcome screen' } })
    const form = screen.getByRole('button', { name: 'Run operation' }).closest('form')
    expect(form?.checkValidity()).toBe(true)
    expect(screen.queryByRole('textbox', { name: 'Name' })).toBeNull()
    fireEvent.click(screen.getByRole('button', { name: 'Run operation' }))
    await waitFor(() => { expect(page.invoke).toHaveBeenCalledWith('draft_test', { instructions: 'Check the welcome screen' }) })
    fireEvent.click(screen.getByRole('checkbox', { name: 'Include Draft' }))
    expect(form?.checkValidity()).toBe(false)
    fireEvent.change(screen.getByRole('textbox', { name: 'Name' }), { target: { value: 'Welcome test' } })
    expect(form?.checkValidity()).toBe(true)
    fireEvent.click(screen.getByRole('checkbox', { name: 'Include Draft' }))
    expect(screen.queryByRole('textbox', { name: 'Name' })).toBeNull()
    expect(form?.checkValidity()).toBe(true)
  })

  it('renders declared secrets as password fields and does not show their value as text', () => {
    const change = vi.fn()
    render(<SchemaField name="guestPassword" schema={{ type: 'string', writeOnly: true }} value="credential-value" onChange={change} required t={makeTranslate(en)} />)
    const input = screen.getByLabelText('Guest Password') as HTMLInputElement
    expect(input.type).toBe('password')
    expect(input.autocomplete).toBe('off')
    expect(screen.queryByText('credential-value')).toBeNull()
  })
  it('retains invalid JSON for correction without passing it to a native operation', () => {
    const change = vi.fn()
    render(<SchemaField name="project" schema={{ type: 'object' }} value={{ enabled: false }} onChange={change} required t={makeTranslate(en)} />)
    fireEvent.change(screen.getByRole('textbox'), { target: { value: '{ invalid' } })
    fireEvent.click(screen.getByRole('button', { name: 'Apply JSON' }))
    expect(screen.getByRole('alert').textContent).toBe(en.invalidJson)
    expect(change).not.toHaveBeenCalled()
    fireEvent.change(screen.getByRole('textbox'), { target: { value: '{"enabled":true}' } })
    fireEvent.click(screen.getByRole('button', { name: 'Apply JSON' }))
    expect(change).toHaveBeenCalledWith({ enabled: true })
    expect(wire.resultValue(output({ enabled: true }))).toEqual({ enabled: true })
  })
})

describe('AI-first Studio flow', () => {
  it('starts a described AI test and opens its saved result even when completion includes the generated draft', async () => {
    const page = mount()
    act(() => { page.store.actions.close(); page.store.actions.resolvedTarget(42) })
    page.invoke.mockImplementation(async (name, args) => {
      if (name === 'ai_test') return output({ kind: 'completed', testId: 'ai-test', runId: 'ai-run', draft: document, target: { pid: 42, title: 'Target' } })
      if (name === 'get_test') return output({ ...document, testId: args.testId ?? '', revision: 'ai-revision' })
      if (name === 'get_run') return output({ runId: 'ai-run', testName: document.name, steps: [], running: false })
      return output({})
    })
    expect(screen.queryByRole('combobox', { name: 'Application' })).toBeNull()
    fireEvent.change(screen.getByRole('textbox', { name: 'Describe a test' }), { target: { value: 'Check that Customer Desk saves a customer' } })
    fireEvent.click(screen.getByRole('button', { name: 'Test with AI' }))
    await waitFor(() => { expect(page.store.getSnapshot().selectedRun).toBe('ai-run') })
    expect(page.invoke).toHaveBeenCalledWith('ai_test', { instructions: 'Check that Customer Desk saves a customer', draftOnly: false })
    expect(page.store.getSnapshot()).toMatchObject({
      targetPid: 42, manualTarget: false, tab: 'tests', editorView: 'lastRun', shownRun: 'ai-run', selectedTest: 'ai-test', dirty: false,
    })
  })

  it('shows target clarification without sending a second operation until a candidate is chosen', async () => {
    const page = mount()
    act(() => { page.store.actions.close() })
    page.invoke.mockImplementation(async (name, args) => {
      if (name !== 'ai_test') return output({})
      return output(args.pid ? { kind: 'draft', draft: document, target: { pid: args.pid } } : {
        kind: 'needsTarget', question: 'Which customer app should I test?', candidates: [
          { id: 'app-42', title: 'Customer Desk', pid: 42, exe: 'C:\\Apps\\Target.exe' },
          { id: 'app-43', title: 'Customer Desk', pid: 43, exe: 'C:\\Apps\\Target.exe' },
        ],
      })
    })
    fireEvent.change(screen.getByRole('textbox', { name: 'Describe a test' }), { target: { value: 'Check customer creation' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create test' }))
    await waitFor(() => { expect(screen.getByRole('button', { name: 'Customer Desk (process 42)' })).toBeTruthy() })
    expect(screen.getByRole('button', { name: 'Customer Desk (process 43)' })).toBeTruthy()
    expect(page.invoke).toHaveBeenCalledTimes(1)
    fireEvent.click(screen.getByRole('button', { name: 'Customer Desk (process 42)' }))
    await waitFor(() => { expect(page.store.getSnapshot().draft?.name).toBe(document.name) })
    expect(page.invoke).toHaveBeenLastCalledWith('ai_test', { instructions: 'Check customer creation', draftOnly: true, pid: 42 })
    expect(page.store.getSnapshot()).toMatchObject({ dirty: true, selectedTest: null, manualTarget: false })
  })

  it('defaults saved tests to AI and keeps exact replay as an explicit alternative', async () => {
    const page = mount()
    page.invoke.mockImplementation(async (name, args) => {
      if (name === 'get_test') return output({ ...document, testId: args.testId ?? '', revision: 'saved-revision' })
      return output(name === 'ai_test' ? { kind: 'completed', testId: 'test-one' } : { testId: 'test-one' })
    })
    fireEvent.click(screen.getByRole('button', { name: 'Run test' }))
    await waitFor(() => { expect(page.invoke).toHaveBeenCalledWith('ai_test', { testId: 'test-one', instructions: document.intent }) })
    expect(page.invoke.mock.calls.some(([name]) => name === 'run_test')).toBe(false)
  })

  it('retries a failed requested AI operation only after the user asks', async () => {
    const page = mount()
    act(() => { page.store.actions.close() })
    page.invoke.mockRejectedValueOnce(new Error('The target app closed'))
      .mockResolvedValueOnce(output({ kind: 'draft', draft: document, target: { pid: 42 } }))
    fireEvent.change(screen.getByRole('textbox', { name: 'Describe a test' }), { target: { value: 'Verify the welcome screen' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create test' }))
    await waitFor(() => { expect(screen.getByRole('alert').textContent).toContain('The target app closed') })
    expect(page.invoke).toHaveBeenCalledTimes(1)
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }))
    await waitFor(() => { expect(page.store.getSnapshot().draft?.name).toBe(document.name) })
    expect(page.invoke).toHaveBeenCalledTimes(2)
    expect(page.invoke.mock.calls[1]).toEqual(page.invoke.mock.calls[0])
  })

  it('clarifies the app for refinement and keeps the result as an unsaved revision of the open test', async () => {
    const page = mount()
    fireEvent.change(screen.getByLabelText('Test name'), { target: { value: 'Unsaved test name' } })
    page.invoke.mockImplementation(async (name, args) => {
      if (name !== 'refine_test') return output({})
      return output(args.pid ? { kind: 'draft', draft: { ...document, name: 'Improved test' }, target: { pid: args.pid } } : {
        kind: 'needsTarget', question: 'Which app should I inspect?', candidates: [{ id: 'app-42', title: 'Customer Desk', pid: 42 }],
      })
    })
    fireEvent.click(screen.getByRole('button', { name: 'Improve…' }))
    fireEvent.change(screen.getByLabelText('Describe the behavior to test'), { target: { value: 'Also check the empty state' } })
    fireEvent.click(screen.getByRole('button', { name: 'Improve with Cortex' }))
    await waitFor(() => { expect(screen.getByRole('button', { name: 'Customer Desk' })).toBeTruthy() })
    expect(page.store.getSnapshot().draft?.name).toBe('Unsaved test name')
    expect(page.invoke).toHaveBeenCalledWith('refine_test', { instructions: 'Also check the empty state', testId: 'test-one',
      expectedRevision: 'original-revision', draft: { ...document, name: 'Unsaved test name' },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Customer Desk' }))
    await waitFor(() => { expect(page.store.getSnapshot().draft?.name).toBe('Improved test') })
    expect(page.store.getSnapshot()).toMatchObject({ selectedTest: 'test-one', baseRevision: 'original-revision', dirty: true })
    expect(page.invoke.mock.calls.every(([name]) => name === 'refine_test')).toBe(true)
  })

  it('returns the selected-step editor to the first row when another saved test opens', () => {
    const page = mount()
    act(() => { page.store.actions.edit({ ...document, steps: [...document.steps, { action: 'click', title: 'Second step', selector: 'id:Save' }] }) })
    fireEvent.click(screen.getByRole('button', { name: /Second step/ }))
    expect(screen.getByDisplayValue('Second step')).toBeTruthy()
    act(() => { page.store.actions.open('another-test', document, 'another-revision') })
    expect(screen.getByDisplayValue('Check heading')).toBeTruthy()
    expect(screen.getByRole('button', { name: /Check heading/ }).getAttribute('aria-pressed')).toBe('true')
  })
})
