/** The open test as native Studio shows it: header, run commands, and the Steps, Last run and JSON views. */
import { useEffect, useState, type ReactNode } from 'react'
import clsx from 'clsx'
import { Menu } from '@cortex/client-ui-primitives'
import type { WorkbenchProps } from './contract.ts'
import { GettingStarted } from './GettingStarted.tsx'
import { JsonField } from './SchemaFields.tsx'
import { RunView } from './RunView.tsx'
import { StudioIcon } from './StudioIcon.tsx'
import { stepSentence } from './steps.ts'
import { appDisplayName } from './presentation.ts'
import { downloadJson, number, record, resultValue, rows, STEP_ACTIONS, testDocument, text, type RecordValue } from './wire.ts'
import css from './Testy.module.css'

/** Editor props extend the local workbench with saved-document operations. */
export interface TestEditorProps extends WorkbenchProps {
  save: (copy?: boolean) => Promise<string>
  remove: () => void
  conflict: boolean
  deleted: boolean
  reload: () => void
  runAi: (args: RecordValue) => Promise<void>
  draftAi: (args: RecordValue, review: (draft: RecordValue) => void) => Promise<void>
  canStop: boolean
  stop: () => void
  importTest: () => void
  record: () => void
  /** Focus "New test from a description": the app is named there, and Cortex finds it. */
  describe: () => void
  notices: ReactNode
}

/** Render the editable steps and execution controls of the selected test. */
export function TestEditor(props: TestEditorProps) {
  const { t, view, snapshot, actions, attempt, invoke, busy, save, remove, conflict, deleted, reload, runAi, draftAi } = props
  const [mode, setMode] = useState('ai')
  const [selectedStep, setSelectedStep] = useState(0)
  const [prompt, setPrompt] = useState('')
  const [showDraft, setShowDraft] = useState(false)
  const [validation, setValidation] = useState<RecordValue | null>(null)
  const [menuOpen, setMenuOpen] = useState(false)
  const [moreOptions, setMoreOptions] = useState(false)
  useEffect(() => { setSelectedStep(0); setMenuOpen(false); setShowDraft(false); setValidation(null) }, [view.selectedTest])
  const draft = view.draft
  const steps = rows(draft?.steps)
  const run = (): void => {
    if (!draft) return
    attempt(async () => {
      const testId = await save()
      // Cortex decides the app: AI-guided runs get it from the test; replays use the app the test (or the AI) already found.
      if (mode === 'ai') {
        await runAi({ testId, instructions: text(draft.intent) || text(draft.name) })
        return
      }
      const result = record(resultValue(await invoke('run_test', { testId, mode, wait: false,
        ...(view.targetPid ? { pid: view.targetPid } : text(draft.targetPath) ? { exe: text(draft.targetPath) } : {}),
      })))
      const runId = text(result.runId) || text(record(result.run).runId)
      if (runId) actions.showRun(runId)
    })
  }
  const canRun = !!draft && !busy && !conflict && !deleted && steps.length > 0
  const gettingStarted = <GettingStarted t={t} ai={Boolean(snapshot.status?.model.model)} described={snapshot.tests.length > 0}
    ran={snapshot.runs.length > 0} onAi={() => { actions.tab('settings') }} onDescribe={props.describe} onRun={() => { if (canRun) run() }} />
  if (draft === null) {
    // A run opened from Results whose test no longer exists is still shown, as native Studio does.
    const orphan = view.editorView === 'lastRun' && view.shownRun ? snapshot.runs.find(item => item.runId === view.shownRun) : undefined
    return <section className={css.editor}>
      <div className={css.editorHeader}>{gettingStarted}{props.notices}</div>
      {orphan ? <div className={css.editorBody}><RunView {...props} runId={view.shownRun} missingTest={text(orphan.testName) || '—'} /></div> : <div className={css.emptyState}>
        <StudioIcon kind="spark" size={32} />
        <strong>{t('readyToTest')}</strong>
        <p>{t('emptyEditorHelp')}</p>
        <div className={css.actions}>
          <button type="button" className={css.button} disabled={busy} onClick={props.describe}><StudioIcon kind="spark" size={16} />{t('gsDescribe')}</button>
        </div>
      </div>}
    </section>
  }
  const testRuns = view.selectedTest ? snapshot.runs.filter(item => item.testId === view.selectedTest) : []
  const shownRun = view.shownRun ?? (text(testRuns[0]?.runId) || null)
  const currentStep = Math.min(Math.max(0, selectedStep), steps.length - 1)
  const step = steps[currentStep]
  const edit = (patch: RecordValue): void => { actions.edit({ ...draft, ...patch }) }
  const changeStep = (index: number, patch: RecordValue): void => {
    edit({ steps: steps.map((item, at) => at === index ? { ...item, ...patch } : item) })
  }
  const move = (index: number, offset: number): void => {
    const next = [...steps]
    const moved = next.splice(index, 1)[0]
    if (moved !== undefined) next.splice(index + offset, 0, moved)
    edit({ steps: next })
    setSelectedStep(index + offset)
  }
  const addStep = (action: string): void => {
    const at = steps.length ? currentStep + 1 : 0
    const next = [...steps]
    next.splice(at, 0, { action, title: '', selector: '', value: '', timeoutMs: 5000 })
    edit({ steps: next })
    setSelectedStep(at)
    actions.editorView('steps')
  }
  const app = snapshot.apps.find(item => number(item.pid) === view.targetPid)
  const runsIn = app ? t('runsInApp', { app: appDisplayName(app) })
    : text(draft.targetPath) ? t('opensApp', { app: text(draft.targetPath).split(/[\\/]/).at(-1) ?? '' }) : t('runsInConnected')
  const tabs: { id: typeof view.editorView; label: string }[] = [
    { id: 'steps', label: t('stepsTab', { count: steps.length }) }, { id: 'lastRun', label: t('lastRun') },
    ...(view.technical ? [{ id: 'json' as const, label: t('json') }] : []),
  ]
  const editorView = view.editorView === 'json' && !view.technical ? 'steps' : view.editorView
  return <section className={css.editor}>
    <fieldset disabled={busy} className={css.editable}>
      <div className={css.editorHeader}>
        {gettingStarted}
        {props.notices}
        {(conflict || deleted) && <section className={clsx(css.infoBar, css.infoBarWarning)} role="status">
          <StudioIcon kind="warning" size={18} />
          <div><p>{deleted ? t('deletedOutside') : t('changedOutside')}</p></div>
          <div className={css.actions}>
            {!deleted && <button type="button" className={css.button} onClick={reload}><StudioIcon kind="refresh" size={16} />{t('reload')}</button>}
            <button type="button" className={css.button} disabled={busy} onClick={() => { attempt(async () => { await save(true) }) }}><StudioIcon kind="save" size={16} />{t('duplicate')}</button>
          </div>
        </section>}
        <input className={css.titleInput} aria-label={t('name')} placeholder={t('name')} value={text(draft.name)} maxLength={200}
          onChange={(event) => { edit({ name: event.target.value }) }} />
        <textarea aria-label={t('intent')} placeholder={t('intent')} className={css.intentInput} rows={1} value={text(draft.intent)}
          onChange={(event) => { edit({ intent: event.target.value }) }} />
        <div className={css.metaLine}>
          <StudioIcon kind="app" size={14} /><span>{runsIn}</span>
          <span className={css.saveState} data-dirty={view.dirty}>{view.dirty ? <i aria-hidden="true" /> : <StudioIcon kind="check" size={14} />}
            {view.dirty ? t('unsaved') : t('saved')}</span>
          <details className={css.metadata}><summary>{t('testMetadata')}</summary><div className={css.twoFields}>
            <label className={css.field}><span>{t('category')}</span><input className={css.textInput} value={text(draft.category)} onChange={(event) => { edit({ category: event.target.value }) }} /></label>
            {/* Cortex finds the app; its path is an override for technical users only. */}
            {view.technical && <label className={css.field}><span>{t('targetPath')}</span><input className={css.textInput} value={text(draft.targetPath)}
              onChange={(event) => { edit({ targetPath: event.target.value }) }} /></label>}
          </div></details>
        </div>
        <div className={css.commandRow}>
          <div className={css.runSplit}>
            <button type="button" className={css.runMain} aria-label={t('run')} disabled={!canRun} onClick={run}>
              <StudioIcon kind="play" size={14} /><strong>{t('runVerb')}</strong><span>{mode === 'ai' ? t('aiGuided') : t('replay')}</span>
            </button>
            <span className={css.runMode}>
              <select aria-label={t('mode')} value={mode} onChange={(event) => { setMode(event.target.value) }}>
                <option value="ai">{t('aiGuided')}</option><option value="replay">{t('replay')}</option>
              </select>
              <StudioIcon kind="chevronDown" size={14} />
            </span>
          </div>
          <button type="button" className={css.button} disabled={!props.canStop} onClick={props.stop}><StudioIcon kind="stop" size={14} />{t('stop')}</button>
          <button type="button" className={css.button} title={t('recordHelp')} onClick={props.record}><span className={css.recordDot} aria-hidden="true" />{t('record')}</button>
          <span className={css.grow} />
          <button type="button" className={css.button} aria-expanded={showDraft} onClick={() => { setShowDraft(!showDraft) }}><StudioIcon kind="edit" size={16} />{t('improveTest')}</button>
          <button type="button" className={css.button} aria-label={t('save')} disabled={busy || conflict || deleted}
            onClick={() => { attempt(async () => { await save() }) }}><StudioIcon kind="save" size={16} />{t('saveShort')}</button>
          <button type="button" className={css.iconButton} aria-label={t('duplicateTest')} title={t('duplicateTest')} disabled={busy}
            onClick={() => { attempt(async () => { await save(true) }) }}><StudioIcon kind="copy" size={16} /></button>
          {view.selectedTest && <button type="button" className={css.iconButton} aria-label={t('deleteShort')} title={t('deleteShort')} disabled={busy}
            onClick={remove}><StudioIcon kind="delete" size={16} /></button>}
          <Menu open={menuOpen && !busy} onClose={() => { setMenuOpen(false) }} portal align="end" autoFocus
            anchor={<button type="button" className={css.iconButton} disabled={busy} aria-label={t('testActions')} title={t('testActions')}
              aria-haspopup="menu" aria-expanded={menuOpen && !busy} onClick={() => { setMenuOpen(!menuOpen) }}><StudioIcon kind="more" size={16} /></button>}
            items={[
              { id: 'import', label: t('importTest'), disabled: busy },
              { id: 'export', label: t('export'), disabled: busy },
              { id: 'validate', label: t('validateTest'), disabled: busy },
            ]}
            onSelect={(id) => {
              setMenuOpen(false)
              if (id === 'import') props.importTest()
              else if (id === 'export') downloadJson(text(draft.name), testDocument(draft))
              else if (id === 'validate') attempt(async () => { setValidation(record(resultValue(await invoke('validate_test', testDocument(draft))))) })
            }} />
        </div>
        {showDraft && <div className={css.draftForm}>
          <label className={css.field}><span>{t('prompt')}</span><textarea className={css.textarea} rows={3} value={prompt} onChange={(event) => { setPrompt(event.target.value) }} /></label>
          <p className={css.hint}>{t('draftHint')}</p>
          <button type="button" className={css.accentButton} disabled={busy || !prompt.trim()}
            onClick={() => { attempt(async () => {
              await draftAi({
                instructions: prompt,
                ...(view.selectedTest ? { testId: view.selectedTest } : {}),
                ...(view.selectedTest && view.baseRevision ? { expectedRevision: view.baseRevision } : {}),
                ...(text(draft.name).trim() || steps.length ? { draft: testDocument(draft) } : {}),
              }, (generated) => {
                actions.edit({ ...draft, ...generated }); setShowDraft(false)
              })
            }) }}>{view.selectedTest ? t('refine') : t('generate')}</button>
        </div>}
        {validation && <section className={clsx(css.infoBar, validation.valid !== true && css.infoBarWarning)} role="status">
          <StudioIcon kind={validation.valid === true ? 'check' : 'warning'} size={18} />
          <div>{validation.valid === true ? <p>{t('valid')}</p> : <pre className={css.resultJson}>{JSON.stringify(validation, null, 2)}</pre>}</div>
          <button type="button" className={css.iconButton} aria-label={t('close')} onClick={() => { setValidation(null) }}><StudioIcon kind="close" size={14} /></button>
        </section>}
        <div className={css.viewTabs} role="tablist" aria-label={t('viewsLabel')}>
          {tabs.map(tab => <button key={tab.id} type="button" role="tab" aria-selected={editorView === tab.id}
            className={clsx(css.viewTab, editorView === tab.id && css.selected)}
            onClick={() => { actions.editorView(tab.id) }}>{tab.label}</button>)}
        </div>
      </div>
      <div className={css.editorBody}>
        {editorView === 'steps' && <>
          <div className={clsx(css.card, css.stepsCard)}>
            {steps.map((item, index) => {
              const sentence = stepSentence(item)
              const title = text(item.title)
              return <button key={text(item.id) || index} type="button" aria-pressed={index === currentStep} title={sentence.plain}
                className={clsx(css.stepRow, index === currentStep && css.selected)} onClick={() => { setSelectedStep(index) }}>
                <span className={css.stepNo}>{index + 1}</span>
                <span className={css.stepVerb}>{sentence.check ? <StudioIcon kind="check" size={12} /> : <i />}{sentence.verb}</span>
                <span className={css.sentence}>
                  <span>{sentence.parts.map((part, at) => <span key={at} className={css[`part_${part.kind}`]}>{part.text}</span>)}</span>
                  {title && !sentence.plain.toLowerCase().includes(title.toLowerCase()) && <span className={css.stepTitle}>{title}</span>}
                </span>
                {view.technical && <span className={css.stepSelector}>{text(item.selector)}</span>}
              </button>
            })}
            {!steps.length && <div className={css.emptyState}><StudioIcon kind="tests" size={28} /><strong>{t('noStepsYet')}</strong><p>{t('noStepsYetHelp')}</p></div>}
          </div>
          <div className={css.addRow}>
            <button type="button" className={css.subtleButton} onClick={() => { addStep('click') }}><StudioIcon kind="plus" size={16} />{t('addStep')}</button>
            <button type="button" className={css.subtleButton} onClick={() => { addStep('assertExists') }}><StudioIcon kind="plus" size={16} />{t('addCheck')}</button>
          </div>
          <div className={clsx(css.card, css.stepEditor)} role="group" aria-label={t('editStep')}>
            <div className={css.stepEditorHead}>
              <h3>{step ? t('stepHeading', { number: currentStep + 1 }) : t('stepNone')}</h3>
              {step && <div className={css.actions}>
                <button type="button" className={css.iconButton} aria-label={t('moveUp')} title={t('moveUp')} disabled={currentStep === 0} onClick={() => { move(currentStep, -1) }}><StudioIcon kind="up" size={16} /></button>
                <button type="button" className={css.iconButton} aria-label={t('moveDown')} title={t('moveDown')} disabled={currentStep === steps.length - 1} onClick={() => { move(currentStep, 1) }}><StudioIcon kind="down" size={16} /></button>
                <button type="button" className={css.iconButton} aria-label={t('removeStep')} title={t('removeStep')}
                  onClick={() => { edit({ steps: steps.filter((_, at) => at !== currentStep) }); setSelectedStep(Math.max(0, currentStep - 1)) }}><StudioIcon kind="delete" size={16} /></button>
                <span className={css.verticalDivider} aria-hidden="true" />
                <button type="button" className={css.subtleButton} aria-expanded={moreOptions || view.technical} onClick={() => { setMoreOptions(!moreOptions) }}>
                  <StudioIcon kind="more" size={16} />{t('moreOptions')}</button>
              </div>}
            </div>
            {!step ? <p className={css.hint}>{t('selectStepHint')}</p> : <>
              <div className={css.stepFields}>
                <label className={css.field}><span>{t('action')}</span><select aria-label={t('action')} className={css.select} value={text(step.action)}
                  onChange={(event) => { changeStep(currentStep, { action: event.target.value }) }}>
                  {STEP_ACTIONS.map(action => <option key={action} value={action}>{t(`action.${action}`)}</option>)}
                </select></label>
                <label className={css.field}><span>{t('selector')}</span><input className={clsx(css.textInput, css.monoInput)} value={text(step.selector)} spellCheck={false}
                  onChange={(event) => { changeStep(currentStep, { selector: event.target.value }) }} /></label>
                <label className={css.field}><span>{t('value')}</span><input className={css.textInput} value={text(step.value)} onChange={(event) => { changeStep(currentStep, { value: event.target.value }) }} /></label>
              </div>
              <label className={css.field}><span>{t('stepTitle')}</span><input className={css.textInput} value={text(step.title)} onChange={(event) => { changeStep(currentStep, { title: event.target.value }) }} /></label>
              {(moreOptions || view.technical) && <div className={css.stepOptions}>
                <div className={css.threeFields}>
                  <label className={css.field}><span>{t('waitUpTo')}</span><input className={css.textInput} type="number" min={0} max={120000} value={number(step.timeoutMs) || 5000}
                    onChange={(event) => { changeStep(currentStep, { timeoutMs: event.target.valueAsNumber }) }} /></label>
                  {['x', 'y'].map(axis => <label key={axis} className={css.field}><span>{t(axis === 'x' ? 'x' : 'y')}</span><input className={css.textInput} type="number" value={number(step[axis])}
                    onChange={(event) => { changeStep(currentStep, { [axis]: event.target.valueAsNumber }) }} /></label>)}
                </div>
                <JsonField value={step.selectorAlternatives ?? []} label={t('alternatives')} t={t}
                  onChange={(value) => { if (!Array.isArray(value)) throw new Error(t('invalidJson')); changeStep(currentStep, { selectorAlternatives: value }) }} />
              </div>}
            </>}
          </div>
        </>}
        {editorView === 'lastRun' && <RunView {...props} runId={shownRun} missingTest={null} />}
        {editorView === 'json' && <div className={css.jsonView}>
          <JsonField key={`${view.selectedTest ?? 'new'}:${view.baseRevision ?? ''}`} value={testDocument(draft)} label={t('json')} t={t}
            onChange={(value) => {
              const next = record(value)
              if (!text(next.name) || !Array.isArray(next.steps)) throw new Error(t('invalidDocument'))
              actions.edit(testDocument(next))
            }} />
        </div>}
      </div>
    </fieldset>
  </section>
}
