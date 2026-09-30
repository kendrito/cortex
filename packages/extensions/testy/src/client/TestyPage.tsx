/** Testy Studio inside Cortex: the native Studio layout (navigation, app bar, grouped tests, editor) over the native engine. */
import { useEffect, useRef, useState, type KeyboardEvent, type ReactNode } from 'react'
import clsx from 'clsx'
import { Button, Modal } from '@cortex/client-ui-primitives'
import type { InjectFace, PropsLocale, PropsRuntime, PropsStore } from '@cortex/client-ui-slots'
import type { JsonValue } from '../types.ts'
import type { TestyInjected } from './source.ts'
import { createTestyViewStore, type TestyTab } from './store.ts'
import { TestEditor } from './TestEditor.tsx'
import { ResultsPanel } from './ResultsPanel.tsx'
import { InspectPanel } from './InspectPanel.tsx'
import { AutomationPanel, type AutomationGroup } from './AutomationPanel.tsx'
import { AiComposer } from './AiComposer.tsx'
import { HelpPage, SettingsPage } from './StudioPages.tsx'
import { StudioIcon, type StudioIconKind } from './StudioIcon.tsx'
import { executeTask } from './task.ts'
import { appDisplayName, appInitials, applicationLabel, friendlyTime, readableName, runStatusKey } from './presentation.ts'
import { number, record, resultValue, rows, testDocument, text, type RecordValue } from './wire.ts'
import css from './Testy.module.css'

/** Main panel props assembled by the framework. */
export type TestyPageProps = PropsRuntime<'main'> & PropsLocale<'testy'> & InjectFace<TestyInjected>
  & PropsStore<ReturnType<typeof createTestyViewStore>>

const NAVIGATION: { tab: TestyTab; icon: StudioIconKind }[] = [
  { tab: 'tests', icon: 'tests' }, { tab: 'results', icon: 'results' }, { tab: 'automate', icon: 'automate' },
]

/** Render the complete testing workflow inside the existing Cortex shell. */
export function TestyPage({ t, useTesty, useStore, actions, invoke, refresh, loadMoreRuns, openStudio }: TestyPageProps) {
  const snapshot = useTesty(value => value)
  const view = useStore(value => value)
  const [query, setQuery] = useState('')
  const [search, setSearch] = useState('')
  const [busyCount, setBusyCount] = useState(0)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [failedAction, setFailedAction] = useState<{ retry: () => Promise<void> } | null>(null)
  const [deleteOpen, setDeleteOpen] = useState(false)
  const [pending, setPending] = useState<{ proceed: () => void } | null>(null)
  const [activityOpen, setActivityOpen] = useState(false)
  const [automate, setAutomate] = useState<{ group: AutomationGroup; key: number }>({ group: 'suites', key: 0 })
  const importInput = useRef<HTMLInputElement>(null)
  const searchInput = useRef<HTMLInputElement>(null)
  const composerInput = useRef<HTMLTextAreaElement>(null)
  const [aiChoice, setAiChoice] = useState<{
    request: RecordValue
    operation: string
    question: string
    candidates: RecordValue[]
    review?: (draft: RecordValue) => void
  } | null>(null)
  const request = useRef(0)
  // Native Studio's Activity: every message, save, run and error in this session, newest first.
  const [log, setLog] = useState<{ id: number; at: number; level: 'info' | 'error'; text: string }[]>([])
  const logId = useRef(0)
  const note = (level: 'info' | 'error', entry: string): void => {
    const id = ++logId.current
    setLog(current => [{ id, at: Date.now(), level, text: entry }, ...current].slice(0, 200))
  }
  const seenRuns = useRef<Map<string, string> | null>(null)
  useEffect(() => {
    // History already on disk when Testy opens is not news; only later changes are logged.
    if (!snapshot.loaded) return
    const previous = seenRuns.current
    const next = new Map(snapshot.runs.map(run => [text(run.runId), run.running === true ? 'running' : text(run.status)]))
    seenRuns.current = next
    if (previous === null) return
    for (const run of snapshot.runs) {
      const id = text(run.runId)
      const status = next.get(id) ?? ''
      const before = previous.get(id)
      if (before === undefined) note('info', t('logRunStarted', { name: text(run.testName) }))
      if (before !== status && status && status !== 'running' && status !== 'pending') {
        note(status === 'passed' ? 'info' : 'error', t('logRunFinished', { name: text(run.testName), summary: text(run.summary) || status }))
      }
    }
  }, [snapshot.runs, snapshot.loaded])
  const busy = busyCount > 0
  const selected = snapshot.tests.find(item => item.testId === view.selectedTest)
  const conflict = view.selectedTest !== null && selected !== undefined && typeof selected.revision === 'string'
    && view.baseRevision !== null && selected.revision !== view.baseRevision
  const deleted = snapshot.loaded && !snapshot.error && view.selectedTest !== null && selected === undefined
  const attempt = (operation: () => Promise<void>): void => {
    setBusyCount(count => count + 1); setError(null); setFailedAction(null); setNotice(null)
    void operation().catch((cause: unknown) => {
      const message = cause instanceof Error ? cause.message : String(cause)
      setError(message); setFailedAction({ retry: operation }); note('error', message)
    })
      .finally(() => { setBusyCount(count => count - 1) })
  }
  const loadTest = async (id: string): Promise<void> => {
    const current = ++request.current
    const value = record(resultValue(await invoke('get_test', { testId: id })))
    if (current !== request.current) return
    actions.open(id, testDocument(value), text(value.revision) || null)
  }
  const choose = (proceed: () => void): void => {
    const accept = (): void => { setAiChoice(null); proceed() }
    if (view.dirty) setPending({ proceed: accept })
    else accept()
  }
  const newTest = (): void => { choose(() => { actions.open(null, { name: '', intent: '', category: '', targetPath: '', steps: [] }, null) }) }
  // A clean document follows external writers. A dirty draft remains unchanged
  // and carries its original revision until the user reloads or saves a copy.
  useEffect(() => {
    if (!conflict || view.dirty || view.tab !== 'tests' || !view.selectedTest || busy) return
    const id = view.selectedTest
    let active = true
    void invoke('get_test', { testId: id }).then((result) => {
      if (active) {
        const value = record(resultValue(result))
        actions.open(id, testDocument(value), text(value.revision) || null)
      }
    }, (cause: unknown) => { if (active) setError(cause instanceof Error ? cause.message : String(cause)) })
    return () => { active = false }
  }, [conflict, view.dirty, view.tab, view.selectedTest, selected?.revision, busy, invoke, actions])
  const save = async (copy = false): Promise<string> => {
    if (!view.draft || !text(view.draft.name).trim()) throw new Error(t('invalidDocument'))
    const document = testDocument(view.draft)
    const result = record(resultValue(await invoke(copy || !view.selectedTest ? 'create_test' : 'update_test', {
      ...document, ...(!copy && view.selectedTest ? { testId: view.selectedTest, expectedRevision: view.baseRevision } : {}),
    })))
    const id = text(result.testId) || (!copy ? view.selectedTest : '')
    if (!id) throw new Error(t('invalidDocument'))
    await loadTest(id)
    note('info', t(copy ? 'logCopied' : 'logSaved', { name: text(document.name) }))
    return id
  }
  const runAi = async (args: RecordValue, operation = 'ai_test', review?: (draft: RecordValue) => void): Promise<void> => {
    setAiChoice(null)
    let shownRun = ''
    const result = await executeTask(invoke, operation, args, (task) => {
      const runId = text(task.runId)
      if (runId && runId !== shownRun) { shownRun = runId; actions.showRun(runId) }
      const target = record(task.target)
      if (number(target.pid)) actions.resolvedTarget(number(target.pid))
    })
    if (result.kind === 'needsTarget') {
      setAiChoice({ request: args, operation, question: text(result.question), candidates: rows(result.candidates),
        ...(review ? { review } : {}),
      })
      return
    }
    const target = record(result.target)
    if (number(target.pid)) actions.resolvedTarget(number(target.pid))
    if (result.kind === 'draft' || (result.kind === undefined && result.draft)) {
      const draft = testDocument(record(result.draft))
      if (!Array.isArray(draft.steps)) throw new Error(t('invalidDocument'))
      if (review) review(draft)
      else { actions.open(null, draft, null); actions.edit(draft) }
      return
    }
    const testId = text(result.testId)
    if (testId) await loadTest(testId)
    const runId = text(result.runId)
    if (runId) actions.showRun(runId)
  }
  // The app is never chosen by hand: Cortex reads it from the description, finds it, opens it if needed and connects.
  const startAi = (draftOnly: boolean): void => {
    choose(() => { attempt(() => runAi({ instructions: view.prompt.trim(), draftOnly })) })
  }
  const viewRun = (runId: string, testId: string): void => {
    choose(() => { attempt(async () => {
      if (testId && snapshot.tests.some(item => item.testId === testId)) await loadTest(testId)
      else actions.close()
      actions.showRun(runId)
    }) })
  }
  const activeTasks = snapshot.tasks.filter(item => item.running === true)
  const runningRuns = snapshot.runs.filter(item => item.running === true)
  const stop = (): void => {
    attempt(async () => {
      for (const task of activeTasks) await invoke('cancel_task', { taskId: text(task.taskId) })
      for (const run of runningRuns) await invoke('cancel_run', { runId: text(run.runId) })
    })
  }
  const openAutomate = (group: AutomationGroup): void => {
    setAutomate(current => ({ group, key: current.key + 1 })); actions.tab('automate')
  }
  const matches = (item: RecordValue, value: string): boolean => (
    `${text(item.searchText)} ${text(item.name)} ${text(item.intent)} ${text(item.category)}`
  ).toLowerCase().includes(value.trim().toLowerCase())
  const filtered = snapshot.tests.filter(item => matches(item, query) && matches(item, search))
  const groups = [...new Set(filtered.map(item => text(item.category)))]
    .sort((a, b) => (a ? 0 : 1) - (b ? 0 : 1) || a.localeCompare(b))
  const currentApp = snapshot.apps.find(item => number(item.pid) === view.targetPid)
  const appName = currentApp ? appDisplayName(currentApp) : ''
  const local = { t, snapshot, view, actions, invoke, loadMoreRuns, attempt, busy }
  const canStop = activeTasks.length > 0 || runningRuns.length > 0
  const status = error || snapshot.error || (busy ? t('executing') : activeTasks.length ? text(activeTasks[0]?.progressMessage)
    || readableName(text(activeTasks[0]?.phase) || text(activeTasks[0]?.operation)) : snapshot.loading && !snapshot.loaded ? t('loading')
    : notice ?? (currentApp ? t('readyApp', { app: appName }) : t('describeHint')))
  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>): void => {
    if (!(event.ctrlKey || event.metaKey) || event.altKey) return
    const key = event.key.toLowerCase()
    if (key === 'k') { event.preventDefault(); searchInput.current?.focus(); searchInput.current?.select() }
    else if (key === 's' && view.tab === 'tests' && view.draft && !busy && !conflict && !deleted) {
      event.preventDefault(); attempt(async () => { await save() })
    }
  }
  const tabButton = (tab: TestyTab, icon: StudioIconKind, list: TestyTab[]): ReactNode => <button key={tab} type="button" role="tab"
    id={`testy-tab-${tab}`} aria-controls={`testy-panel-${tab}`} aria-selected={view.tab === tab} tabIndex={view.tab === tab ? 0 : -1}
    className={clsx(css.navItem, view.tab === tab && css.selected)} title={t(tab)}
    onKeyDown={(event) => {
      const offset = ['ArrowDown', 'ArrowRight'].includes(event.key) ? 1 : ['ArrowUp', 'ArrowLeft'].includes(event.key) ? -1 : 0
      if (!offset) return
      event.preventDefault()
      const next = list[(list.indexOf(tab) + offset + list.length) % list.length] ?? 'tests'
      actions.tab(next)
      event.currentTarget.closest('nav')?.querySelector<HTMLButtonElement>(`#testy-tab-${next}`)?.focus()
    }} onClick={() => { actions.tab(tab) }}><StudioIcon kind={icon} /><span>{t(tab)}</span></button>
  const navTabs = NAVIGATION.map(item => item.tab)
  const footerTabs: TestyTab[] = ['help', 'settings']
  return <div className={css.page} onKeyDown={onKeyDown}>
    <nav className={css.navPane} aria-label={t('navigation')}>
      <h1 className={css.appTitle}>
        <svg width="16" height="16" viewBox="0 0 16 16" aria-hidden="true">
          <rect x="1" y="1" width="14" height="14" rx="3" fill="currentColor" />
          <path d="M4.5 8.2l2.3 2.3 4.7-4.9" fill="none" stroke="var(--t-on-accent)" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
        <span>{t('panel')}</span>
      </h1>
      <div role="tablist" aria-orientation="vertical" aria-label={t('navigation')} className={css.navGroup}>
        {NAVIGATION.map(item => tabButton(item.tab, item.icon, navTabs))}
      </div>
      <div role="tablist" aria-orientation="vertical" aria-label={t('studioSettings')} className={clsx(css.navGroup, css.navFooter)}>
        {tabButton('help', 'help', footerTabs)}
        <span className={css.navSeparator} aria-hidden="true" />
        {tabButton('settings', 'settings', footerTabs)}
      </div>
    </nav>
    <div className={css.contentLayer}>
      <header className={css.appBar}>
        <div className={css.appCard} title={currentApp ? applicationLabel(currentApp, snapshot.apps, t, true) : t('noAppHelp')}>
          <span className={css.appBadge} aria-hidden="true">{currentApp ? appInitials(appName) : <StudioIcon kind="app" size={16} />}</span>
          <span className={css.appText}>
            <strong>{currentApp ? appName : t('noAppConnected')}</strong>
            <span className={css.caption}>{currentApp && <i className={css.statusDot} aria-hidden="true" />}
              {currentApp ? t('chosenByAi') : t('aiPicksApp')}
              {currentApp && view.technical && <span className={css.mono}> · {t('processId', { pid: number(currentApp.pid) })}</span>}
            </span>
          </span>
        </div>
        <span className={css.verticalDivider} aria-hidden="true" />
        <button type="button" className={clsx(css.appBarButton, view.tab === 'inspect' && css.selected)} aria-pressed={view.tab === 'inspect'}
          title={t('inspectHelp')} onClick={() => { actions.tab('inspect') }}><StudioIcon kind="inspect" size={16} />{t('inspectControls')}</button>
        <span className={css.appBarSpacer} />
        <label className={css.globalSearch}>
          <input ref={searchInput} type="search" aria-label={t('searchTests')} placeholder={t('searchTests')} value={search}
            onChange={(event) => { setSearch(event.target.value); if (view.tab !== 'tests') actions.tab('tests') }}
            onKeyDown={(event) => { if (event.key === 'Escape') setSearch('') }} />
          <kbd>Ctrl+K</kbd><StudioIcon kind="search" size={16} />
        </label>
        <button type="button" className={clsx(css.appBarButton, activityOpen && css.selected)} aria-pressed={activityOpen}
          onClick={() => { setActivityOpen(!activityOpen) }}><StudioIcon kind="activity" size={16} />{t('activity')}</button>
        <label className={css.toggleSwitch} title={t('technicalHelp')}>
          <span>{t('showTechnical')}</span>
          <input type="checkbox" role="switch" checked={view.technical} onChange={(event) => { actions.technical(event.target.checked) }} />
          <i aria-hidden="true" /><span className={css.toggleState} aria-hidden="true">{view.technical ? t('on') : t('off')}</span>
        </label>
        {canStop && <span className={css.activitySweep} aria-hidden="true" />}
      </header>
      {snapshot.status?.supported === false ? <div className={css.emptyState}><h2>{t('unavailable')}</h2><p>{t('unavailableDetail')}</p></div>
        : <div className={css.panel} data-view={view.tab} id={`testy-panel-${view.tab}`} role="tabpanel"
          aria-labelledby={view.tab === 'inspect' ? undefined : `testy-tab-${view.tab}`} aria-label={view.tab === 'inspect' ? t('inspectControls') : undefined}>
          {view.tab === 'tests' && <div className={css.libraryPage}>
            <aside className={css.library} aria-label={t('tests')}>
              <div className={css.libraryTools}>
                <button type="button" className={css.button} disabled={busy} onClick={newTest}><StudioIcon kind="plus" size={16} />{t('newTest')}</button>
                <label className={css.searchBox}><input type="search" aria-label={t('filterTests')} placeholder={t('filter')} value={query}
                  onChange={(event) => { setQuery(event.target.value) }} /><StudioIcon kind="search" size={16} /></label>
              </div>
              <div className={css.libraryList}>
                {groups.map((group) => {
                  const items = filtered.filter(item => text(item.category) === group)
                    .sort((a, b) => text(a.name).localeCompare(text(b.name)))
                  return <section key={group || '-'} className={css.libraryGroup} aria-label={group || t('otherTests')}>
                    <h3><span>{group || t('otherTests')}</span><span className={css.caption}>{items.length === 1 ? t('oneTest') : t('testsCount', { count: items.length })}</span></h3>
                    {items.map((item) => {
                      const last = record(item.lastRun)
                      const state = text(last.status)
                      const steps = number(item.stepCount)
                      return <button type="button" key={text(item.testId)} disabled={busy} title={text(item.intent) || text(item.name)}
                        className={clsx(css.libraryItem, view.selectedTest === item.testId && css.selected)}
                        onClick={() => {
                          if (view.selectedTest !== item.testId) choose(() => { attempt(() => loadTest(text(item.testId))) })
                        }}>
                        <span className={css.statusGlyph} data-status={state || 'none'} aria-hidden="true">
                          {state === 'passed' ? <StudioIcon kind="check" size={12} /> : state === 'failed' ? <StudioIcon kind="close" size={11} /> : null}
                        </span>
                        <span className={css.itemName}>{text(item.name)}</span>
                        <span className={css.itemStatus} data-status={state}>
                          {state ? t(runStatusKey(state), { when: friendlyTime(text(last.startedAt)) }) : t('notRunYet')}
                        </span>
                        <span className={css.caption}>{steps === 1 ? t('oneStep') : t('stepsCount', { count: steps })}</span>
                      </button>
                    })}
                  </section>
                })}
                {!filtered.length && <div className={css.emptyState}><StudioIcon kind="tests" size={28} />
                  <strong>{snapshot.tests.length ? t('noMatches') : t('noTests')}</strong><p>{t('noTestsDetail')}</p></div>}
              </div>
              <AiComposer t={t} prompt={view.prompt} onChange={actions.prompt} onStart={startAi} busy={busy} inputRef={composerInput} />
            </aside>
            <TestEditor {...local} runAi={runAi} save={save} remove={() => { setDeleteOpen(true) }}
              draftAi={(args, review) => runAi(args, args.testId ? 'refine_test' : 'draft_test', review)}
              conflict={conflict && view.dirty} deleted={deleted} canStop={canStop} stop={stop}
              importTest={() => { importInput.current?.click() }} record={() => { openAutomate('recording') }}
              describe={() => { composerInput.current?.focus() }}
              reload={() => { if (view.selectedTest) attempt(() => loadTest(view.selectedTest ?? '')) }}
              notices={<>
                {aiChoice && <section className={css.infoBar} role="status" aria-label={t('chooseTheApp')}>
                  <StudioIcon kind="info" size={18} />
                  <div><h2 className={css.infoTitle}>{aiChoice.question}</h2><div className={css.actions}>{aiChoice.candidates.map(candidate => <button type="button"
                    key={text(candidate.id) || String(number(candidate.pid))} className={css.button} disabled={busy}
                    title={text(candidate.exe) || text(candidate.processName)}
                    onClick={() => { attempt(() => runAi({ ...aiChoice.request,
                      ...(number(candidate.pid) ? { pid: number(candidate.pid) } : text(candidate.exe) ? { exe: text(candidate.exe) } : {}),
                    }, aiChoice.operation, aiChoice.review)) }}>{applicationLabel(candidate, aiChoice.candidates, t)}</button>)}</div></div>
                  <button type="button" className={css.iconButton} aria-label={t('cancelAppChoice')} onClick={() => { setAiChoice(null) }}><StudioIcon kind="close" size={14} /></button>
                </section>}
                {(error || snapshot.error) && <section className={clsx(css.infoBar, css.infoBarError)} role="alert">
                  <StudioIcon kind="warning" size={18} /><div><p>{error || snapshot.error}</p></div>
                  <button type="button" className={css.button} disabled={busy} onClick={() => { attempt(failedAction?.retry ?? refresh) }}>{t('retry')}</button>
                </section>}
              </>} />
          </div>}
          {view.tab !== 'tests' && (error || snapshot.error) && <div className={clsx(css.infoBar, css.infoBarError, css.pageAlert)} role="alert">
            <StudioIcon kind="warning" size={18} /><div><p>{error || snapshot.error}</p></div>
            <button type="button" className={css.button} disabled={busy} onClick={() => { attempt(failedAction?.retry ?? refresh) }}>{t('retry')}</button>
          </div>}
          {view.tab === 'results' && <ResultsPanel {...local} viewRun={viewRun} refresh={() => { attempt(refresh) }} />}
          {view.tab === 'inspect' && <InspectPanel {...local} />}
          {view.tab === 'automate' && <AutomationPanel key={automate.key} {...local} initialGroup={automate.group} />}
          {view.tab === 'settings' && <SettingsPage {...local} openStudio={() => { attempt(openStudio) }} refresh={() => { attempt(refresh) }} />}
          {view.tab === 'help' && <HelpPage t={t} />}
        </div>}
      <footer className={css.statusBar} role="status">
        <StudioIcon kind={error || snapshot.error ? 'warning' : 'info'} size={14} />
        <span className={clsx((error || snapshot.error) && css.statusError)}>{status}</span>
        <span className={css.statusModel} title={snapshot.status?.workspace}>{t('model')}: <strong>{snapshot.status?.model.model || '—'}</strong></span>
      </footer>
      {activityOpen && <aside className={css.activityPane} aria-label={t('activity')}>
        <div className={css.paneHeading}><h2>{t('activity')}</h2>
          <button type="button" className={css.iconButton} aria-label={t('close')} onClick={() => { setActivityOpen(false) }}><StudioIcon kind="close" size={14} /></button></div>
        {snapshot.tasks.length === 0 && log.length === 0 && <p className={css.hint}>{t('noActivity')}</p>}
        {log.length > 0 && <ol className={css.activityLog} aria-label={t('activity')}>{log.map(entry => <li key={entry.id} data-level={entry.level}>
          <time className={css.caption}>{new Date(entry.at).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit', second: '2-digit' })}</time>
          <span>{entry.text}</span>
        </li>)}</ol>}
        {snapshot.tasks.length > 0 && <h3 className={css.settingsGroup}>{t('jobs')}</h3>}
        {snapshot.tasks.map(task => <div key={text(task.taskId)} className={css.activityEntry}>
          <strong>{readableName(text(task.operation))}</strong>
          <p>{text(task.progressMessage) || readableName(text(task.status))}</p>
          {text(task.error) && <p role="alert">{text(task.error)}</p>}
          {task.running === true && <button type="button" className={css.button} onClick={() => { attempt(async () => { await invoke('cancel_task', { taskId: text(task.taskId) }) }) }}>{t('stop')}</button>}
        </div>)}
      </aside>}
    </div>
    <input ref={importInput} type="file" hidden aria-label={t('import')} accept=".json,application/json" disabled={busy} onChange={(event) => {
      const file = event.target.files?.[0]
      if (!file) return
      attempt(async () => {
        const document = record(JSON.parse(await file.text()) as JsonValue)
        if (!text(document.name) || !Array.isArray(document.steps)) throw new Error(t('invalidDocument'))
        choose(() => { actions.open(null, testDocument(document), null); actions.edit(testDocument(document)) })
      })
      event.target.value = ''
    }} />
    <Modal open={pending !== null} onClose={() => { setPending(null) }} title={t('switchTitle')} closeLabel={t('cancel')} description={t('switchDetail')}
      footer={<><Button variant="ghost" onClick={() => { setPending(null) }}>{t('keepEditing')}</Button><Button variant="primary" onClick={() => { const next = pending; setPending(null); next?.proceed() }}>{t('discard')}</Button></>} />
    <Modal open={deleteOpen} onClose={() => { setDeleteOpen(false) }} title={t('deleteTitle')} closeLabel={t('cancel')} description={t('deleteDetail')}
      footer={<><Button variant="ghost" onClick={() => { setDeleteOpen(false) }}>{t('cancel')}</Button><Button variant="primary" disabled={busy} onClick={() => { attempt(async () => {
        await invoke('delete_test', { testId: view.selectedTest, expectedRevision: view.baseRevision })
        actions.close(); setDeleteOpen(false)
      }) }}>{t('confirmDelete')}</Button></>} />
  </div>
}
