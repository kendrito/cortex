/** Complete native workflow catalog, organized around Studio's automation tasks. */
import { useEffect, useState } from 'react'
import clsx from 'clsx'
import { Button, Modal } from '@cortex/client-ui-primitives'
import type { TestyToolResult } from '../types.ts'
import type { WorkbenchProps } from './contract.ts'
import type { TestyKey } from './locales.ts'
import { SchemaField, schemaDefault } from './SchemaFields.tsx'
import { displayValue, fieldLabel, operationData, workflowRecord } from './presentation.ts'
import { record, redactFields, resultImages, resultValue, rows, setField, testDocument, text, type RecordValue, type TestyTool } from './wire.ts'
import css from './Testy.module.css'

const groups = ['suites', 'jobs', 'schedules', 'machines', 'recording', 'lifecycle', 'project', 'backup', 'tools'] as const
type Group = typeof groups[number]
/** Automate pages, as in native Studio's Automate tabs. */
export type AutomationGroup = Group
const descriptions: Record<Group, TestyKey> = {
  suites: 'suitesHelp', jobs: 'jobsHelp', schedules: 'schedulesHelp', machines: 'machinesHelp', recording: 'recordingHelp',
  lifecycle: 'lifecycleHelp', project: 'projectHelp', backup: 'backupHelp', tools: 'toolsHelp',
}

/** Group existing and future native operations by their declared workflow metadata. */
export function operationGroup(tool: TestyTool): Group {
  if (tool.group === 'data') return 'backup'
  if (groups.some(group => group === tool.group)) return tool.group as Group
  if (/suite|template/.test(tool.name)) return 'suites'
  if (/job|worker|agent|task/.test(tool.name)) return 'jobs'
  if (/schedule/.test(tool.name)) return 'schedules'
  if (/machine/.test(tool.name)) return 'machines'
  if (/record|demo/.test(tool.name)) return 'recording'
  if (/lifecycle/.test(tool.name)) return 'lifecycle'
  if (/project/.test(tool.name)) return 'project'
  if (/workspace/.test(tool.name)) return 'backup'
  return 'tools'
}

function defaults(tool: TestyTool): RecordValue {
  return Object.fromEntries(Object.entries(record(tool.inputSchema.properties)).flatMap(([key, field]) => {
    const schema = record(field)
    return schema.default === undefined ? [] : [[key, schemaDefault(schema)]]
  }))
}

/** Native workflows remain discoverable and executable as the engine expands its catalog. */
export function AutomationPanel(props: WorkbenchProps & { initialGroup?: Group }) {
  const { t, snapshot, view, busy, invoke, attempt } = props
  const [group, setGroup] = useState<Group>(props.initialGroup ?? 'suites')
  const [selected, setSelected] = useState('')
  const [args, setArgs] = useState<RecordValue>({})
  const [result, setResult] = useState<TestyToolResult | null>(null)
  const [taskId, setTaskId] = useState<string | null>(null)
  const [confirm, setConfirm] = useState(false)
  const [recordSeed, setRecordSeed] = useState<RecordValue>({})
  const tools = snapshot.tools.filter(tool => tool.name !== 'launch_sample' && (group === 'tools' || operationGroup(tool) === group))
  const tool = tools.find(item => item.name === selected)
  useEffect(() => {
    if (!taskId) return
    const task = snapshot.tasks.find(item => item.taskId === taskId)
    if (task) setResult({ content: [], structuredContent: task })
  }, [taskId, snapshot.tasks])
  const choose = (entry: TestyTool): void => {
    const properties = record(entry.inputSchema.properties)
    const seed = Object.fromEntries(Object.entries(recordSeed).filter(([key]) => key in properties))
    setSelected(entry.name); setArgs({ ...defaults(entry), ...seed }); setResult(null); setTaskId(null)
  }
  // Like native Studio's Automate pages, a page opens on its first operation rather than an empty chooser.
  const first = tools[0]
  useEffect(() => {
    if (selected === '' && first) choose(first)
  }, [group, selected, first?.name])
  const execute = (): void => {
    if (!tool) return
    setConfirm(false)
    attempt(async () => {
      const output = await invoke(tool.name, args)
      setResult(output)
      setTaskId(text(record(resultValue(output)).taskId) || null)
      setArgs(previous => Object.fromEntries(Object.entries(previous).filter(([key]) => !/password|secret|token|apiKey/i.test(key))))
    })
  }
  const properties = record(tool?.inputSchema.properties)
  const value = result ? resultValue(result) : null
  const data = record(value)
  const operation = operationData(value)
  const completed = operation.data
  const resultRows = rows(Array.isArray(value) ? value : Object.values(completed).find(Array.isArray)).map(workflowRecord)
  const resultColumns = Object.keys(resultRows[0] ?? {}).filter(key => !['steps', 'provider', 'settings'].includes(key)
    && (view.technical || !/(?:^id$|Ids?$|revision)/.test(key))).slice(0, 6)
  const failed = result?.isError || data.status === 'failed' || (operation.exitCode !== undefined && operation.exitCode !== 0)
  const resultName = text(completed.name) || text(completed.title)
  const resultSummary = failed ? t('operationFailed') : data.status === 'cancelled' ? t('operationCancelled')
    : data.running === true ? text(data.progressMessage) || t('operationRunning')
      : tool?.name.startsWith('save_') && resultName ? t('operationSaved', { name: resultName }) : t('operationDone')
  const generated = record(completed.draft)
  const seedRecord = (row: RecordValue): void => {
    const idField = ({ suites: 'suiteId', jobs: 'jobId', schedules: 'scheduleId', machines: 'machineId',
      recording: 'recordingId', lifecycle: 'lifecycleId', project: '', backup: '', tools: '' })[group]
    setRecordSeed({ ...row, ...(idField && row.id !== undefined ? { [idField]: row.id } : {}) })
  }
  return <section className={clsx(css.pageScroll, css.automation)}>
    <header className={css.pageHeader}><div><h1>{t('automate')}</h1><p>{t('automateSubtitle')}</p></div></header>
    <nav className={css.pivots} aria-label={t('automate')}>
      {groups.map(key => <button key={key} type="button" className={clsx(css.pivot, group === key && css.selected)}
        aria-current={group === key ? 'page' : undefined}
        onClick={() => { setGroup(key); setSelected(''); setResult(null); setTaskId(null); setRecordSeed({}) }}>{t(key)}</button>)}
    </nav>
    <div className={css.workflowContent}>
      <p className={css.pageLead}>{t(descriptions[group])}</p>
      {Object.keys(recordSeed).length > 0 && <div className={css.notice} role="status">
        {t('selectedRecord', { name: text(recordSeed.name) || text(recordSeed.title) || text(recordSeed.id)
          || text(recordSeed.suiteId) || text(recordSeed.jobId) || text(recordSeed.scheduleId) || text(recordSeed.machineId) })}
        <button type="button" className={css.subtleButton} onClick={() => { setRecordSeed({}) }}>{t('clear')}</button>
      </div>}
      <div className={css.operationLayout}>
        <div className={css.operationList} aria-label={t('operations')}>
          {tools.length === 0 && <p className={css.hint}>{t('noOperations')}</p>}
          {tools.map(entry => <button key={entry.name} type="button" className={clsx(css.operationLink, selected === entry.name && css.selected)}
            onClick={() => { choose(entry) }}>{entry.title || entry.name.replaceAll('_', ' ')}</button>)}
        </div>
        <div className={css.operationDetail}>
          {!tool ? <div className={css.empty}><p>{t('chooseOperation')}</p></div> : <>
            <h3>{tool.title || tool.name.replaceAll('_', ' ')}</h3><p className={css.hint}>{tool.description}</p>
            {['save_project', 'save_preferences'].includes(tool.name) && <button type="button" className={css.button} disabled={busy}
              onClick={() => { attempt(async () => {
                const current = record(resultValue(await invoke(tool.name === 'save_project' ? 'get_project' : 'get_preferences', {})))
                setArgs(Object.fromEntries(Object.entries(current).filter(([key]) => key in properties)))
              }) }}>{t('loadCurrent')}</button>}
            <form onSubmit={(event) => {
              event.preventDefault()
              if (/^(delete_|forget_|workspace_restore|setup_machine)/.test(tool.name)) setConfirm(true)
              else execute()
            }}>
              {Object.entries(properties).map(([key, schema]) => <div key={`${tool.name}:${key}`}>
                {key !== 'testIds' && (key !== 'suiteId' || tool.name !== 'save_suite' || args.suiteId || view.technical) && <SchemaField name={key} schema={record(schema)} value={args[key]}
                  required={Array.isArray(tool.inputSchema.required) && tool.inputSchema.required.includes(key)} t={t}
                  onChange={(next) => { setArgs(previous => setField(previous, key, next)) }} />}
                {key === 'testId' && view.selectedTest && <button type="button" className={css.subtleButton}
                  onClick={() => { setArgs(previous => ({ ...previous, testId: view.selectedTest })) }}>{t('useSelected')}</button>}
                {key === 'pid' && view.targetPid && <button type="button" className={css.subtleButton}
                  onClick={() => { setArgs(previous => ({ ...previous, pid: view.targetPid })) }}>{t('useCurrentApp')}</button>}
                {key === 'template' && view.draft && <button type="button" className={css.subtleButton}
                  onClick={() => { setArgs(previous => ({ ...previous, template: { version: 1, name: text(view.draft?.name),
                    test: testDocument(view.draft ?? {}), bindings: [],
                  } })) }}>{t('useTemplate')}</button>}
                {key === 'testIds' && <fieldset className={css.fieldset}><legend>{t('tests')}</legend>
                  <p className={css.hint}>{snapshot.tests.length ? t('selectedTests', { count: Array.isArray(args.testIds) ? args.testIds.length : 0 }) : t('noSavedTests')}</p>
                  <div className={css.testChoices}>{snapshot.tests.map(test => <label key={text(test.testId)} className={css.check}>
                    <input type="checkbox" checked={Array.isArray(args.testIds) && args.testIds.includes(test.testId ?? '')}
                      onChange={(event) => { setArgs(previous => ({ ...previous, testIds: event.target.checked
                        ? [...(Array.isArray(previous.testIds) ? previous.testIds : []), text(test.testId)]
                        : (Array.isArray(previous.testIds) ? previous.testIds : []).filter(id => id !== test.testId),
                      })) }} />{text(test.name)}
                  </label>)}</div>
                </fieldset>}
                {key === 'csv' && <label className={css.fileButton}>{t('csv')}<input type="file" accept=".csv,text/csv" onChange={(event) => {
                  const file = event.target.files?.[0]
                  if (file) attempt(async () => { const csv = await file.text(); setArgs(previous => ({ ...previous, csv })) })
                }} /></label>}
              </div>)}
              <button type="submit" className={css.accentButton} disabled={busy}>{busy ? t('executing') : t('execute')}</button>
            </form>
          </>}
          {result && <section className={css.operationResult} aria-label={t('operationResult')}>
            <div className={css.sectionHeading}><h3>{t('operationResult')}</h3>
              {data.running === true && taskId && <button type="button" className={css.button} onClick={() => { attempt(async () => {
                setResult(await invoke('cancel_task', { taskId }))
              }) }}>{t('stop')}</button>}
            </div>
            <p role="status">{resultSummary}</p>
            {failed && text(data.error) && <p className={css.finding}>{text(data.error)}</p>}
            {Array.isArray(generated.steps) && <div className={css.notice}>
              <p>{text(generated.name)}</p>
              <button type="button" className={css.accentButton} disabled={view.dirty} onClick={() => {
                props.actions.open(null, testDocument(generated), null); props.actions.edit(testDocument(generated))
              }}>{t('useDraft')}</button>
              {view.dirty && <p className={css.hint}>{t('replaceDraft')}</p>}
            </div>}
            {resultRows.length > 0 && <div className={css.tableScroll}><table className={css.table}>
              <thead><tr>{resultColumns.map(key => <th key={key}>{fieldLabel(key, {}, t)}</th>)}<th>{t('operation')}</th></tr></thead>
              <tbody>{resultRows.map((row, index) => <tr key={index}>{resultColumns.map(key =>
                <td key={key}>{displayValue(row[key], t)}</td>)}
              <td><button type="button" className={css.subtleButton} onClick={() => { seedRecord(row) }}>{t('useRecord')}</button></td>
              </tr>)}</tbody>
            </table></div>}
            {resultImages(result).map((image, index) => <img key={index} className={css.resultImage} src={image} alt={t('resultImage')} />)}
            <details open={view.technical || undefined}><summary>{t('resultDetails')}</summary><pre className={css.resultJson}>{JSON.stringify(value, null, 2)}</pre></details>
          </section>}
        </div>
      </div>
    </div>
    <Modal open={confirm} onClose={() => { setConfirm(false) }} title={t('operationConfirm')} closeLabel={t('cancel')}
      description={t('operationConfirmDetail')} footer={<><Button variant="ghost" onClick={() => { setConfirm(false) }}>{t('cancel')}</Button><Button variant="primary" onClick={execute}>{t('confirm')}</Button></>}>
      <pre className={css.resultJson}>{JSON.stringify(redactFields(args), null, 2)}</pre>
    </Modal>
  </section>
}
