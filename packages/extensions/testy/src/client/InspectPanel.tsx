/** Native Studio's Inspect controls page: UI Automation inspection and explicit transfer of observed controls into a test. */
import { useState } from 'react'
import clsx from 'clsx'
import { writeClipboard } from '@cortex/client-ui-primitives'
import type { WorkbenchProps } from './contract.ts'
import { EvidenceImage } from './EvidenceImage.tsx'
import { StudioIcon } from './StudioIcon.tsx'
import { number, record, resultImages, resultValue, rows, text, type RecordValue } from './wire.ts'
import css from './Testy.module.css'

/** Inspect the app Cortex connected to; the user never picks it by hand. */
export function InspectPanel({ t, snapshot, view, actions, invoke, attempt, busy }: WorkbenchProps) {
  const [observation, setObservation] = useState<{ pid: number; value: RecordValue; image: string | undefined } | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const [filter, setFilter] = useState('')
  const [offscreen, setOffscreen] = useState(false)
  const [probe, setProbe] = useState(false)
  const [copied, setCopied] = useState(false)
  const data = observation?.pid === view.targetPid ? observation : null
  const controls = rows(data?.value.elements)
  const control = controls.find(item => item.selector === selected)
  const app = snapshot.apps.find(item => number(item.pid) === view.targetPid)
  const inspect = async (more = false): Promise<void> => {
    if (!view.targetPid) return
    const result = await invoke('inspect_app', {
      pid: view.targetPid, probe, includeScreenshot: !more, includeOffscreen: offscreen, details: true,
      filter, ...(more && data?.value.nextOffset != null ? { offset: data.value.nextOffset } : {}),
    })
    const value = record(resultValue(result))
    setObservation({
      pid: view.targetPid, value: { ...value, elements: more ? [...controls, ...rows(value.elements)] : rows(value.elements) },
      image: resultImages(result)[0] ?? data?.image,
    })
    if (!more) setSelected(null)
  }
  const add = (action: string): void => {
    if (!view.draft || !control) return
    actions.edit({ ...view.draft, steps: [...rows(view.draft.steps), {
      action, selector: text(control.selector), title: text(control.name) || text(control.controlType),
      value: action === 'assertText' ? text(control.value) : '', timeoutMs: 5000,
    }] })
    actions.tab('tests')
  }
  return <section className={clsx(css.pageScroll, css.inspectPage)}>
    <header className={css.pageHeader}>
      <div><h1>{t('inspectControls')}</h1><p>{t('inspectSubtitle')}</p></div>
      <div className={css.actions}>
        <span className={css.caption}>{text(app?.title) || text(app?.processName) || t('noAppConnected')}</span>
      </div>
    </header>
    <div className={css.inspectToolbar}>
      <label className={css.searchBox}>
        <input type="search" aria-label={t('searchControls')} placeholder={t('searchControls')} value={filter}
          onChange={(event) => { setFilter(event.target.value) }} onKeyDown={(event) => { if (event.key === 'Enter') attempt(() => inspect()) }} />
        <StudioIcon kind="search" size={16} />
      </label>
      <label className={css.check}><input type="checkbox" checked={offscreen} onChange={(event) => { setOffscreen(event.target.checked) }} />{t('includeOffscreen')}</label>
      <label className={css.check}><input type="checkbox" checked={probe} onChange={(event) => { setProbe(event.target.checked) }} />{t('probe')}</label>
      <button type="button" className={css.accentButton} disabled={busy || !view.targetPid} onClick={() => { attempt(() => inspect()) }}>
        <StudioIcon kind="inspect" size={16} />{t('scan')}</button>
      <button type="button" className={css.button} disabled={busy || !view.targetPid} onClick={() => { attempt(async () => {
        const result = await invoke('screenshot_app', { pid: view.targetPid, probe })
        setObservation({ pid: view.targetPid ?? 0, value: data?.value ?? {}, image: resultImages(result)[0] })
      }) }}><StudioIcon kind="view" size={16} />{t('capture')}</button>
    </div>
    {!data ? <div className={css.emptyState}><StudioIcon kind="inspect" size={28} /><strong>{t('controls')}</strong>
      <p>{view.targetPid ? t('scanHint') : t('inspectNoApp')}</p></div> : <div className={css.inspectSplit}>
      <div className={css.controlList}>
        {controls.length === 0 && <p className={css.hint}>{t('noControls')}</p>}
        {controls.map((item, index) => <button key={`${text(item.selector)}:${index}`} type="button"
          aria-label={t('controlEntry', { name: text(item.name) || text(item.selector), type: text(item.controlType) })}
          className={clsx(css.controlRow, selected === item.selector && css.selected)}
          style={{ paddingInlineStart: `${12 + Math.min(8, number(item.depth)) * 10}px` }} onClick={() => { setSelected(text(item.selector)); setCopied(false) }}>
          <strong>{text(item.name) || text(item.selector)}</strong><span>{text(item.controlType)}</span>
          {item.value && <span className={css.rowExcerpt}>{text(item.value)}</span>}
        </button>)}
        {data.value.nextOffset != null && <button type="button" className={css.subtleButton} disabled={busy} onClick={() => { attempt(() => inspect(true)) }}>{t('loadMore')}</button>}
      </div>
      <div className={css.evidence}>
        {control && <section className={clsx(css.card, css.inspectDetail)}>
          <div className={css.sectionHeading}><h2>{text(control.name) || text(control.controlType)}</h2></div>
          <code className={css.selector}>{text(control.selector)}</code>
          <div className={css.actions}>
            <button type="button" className={css.button} disabled={!view.draft} onClick={() => { add('click') }}><StudioIcon kind="plus" size={16} />{t('addClick')}</button>
            <button type="button" className={css.button} disabled={!view.draft} onClick={() => { add(control.value !== undefined ? 'assertText' : 'assertExists') }}><StudioIcon kind="check" size={16} />{t('addAssertion')}</button>
            <button type="button" className={css.subtleButton} onClick={() => { attempt(async () => { setCopied(await writeClipboard(text(control.selector))) }) }}>
              <StudioIcon kind={copied ? 'check' : 'copy'} size={16} />{copied ? t('copied') : t('copySelector')}</button>
          </div>
          {!view.draft && <p className={css.hint}>{t('noDraft')}</p>}
        </section>}
        {data.image && <div className={css.shotFrame}><EvidenceImage image={data.image} label={t('appScreenshot')} control={control?.bounds} capture={data.value.screenshotBounds} /></div>}
        {Array.isArray(data.value.hints) && <ul className={css.hint}>
          {data.value.hints.map((hint, index) => <li key={index}>{text(hint)}</li>)}
        </ul>}
        {control && <details className={css.technical} open><summary>{t('properties')}</summary><dl className={css.properties}>
          {Object.entries(control).map(([key, value]) => <div key={key}><dt>{key}</dt><dd>{typeof value === 'object' ? JSON.stringify(value) : String(value)}</dd></div>)}
        </dl></details>}
      </div>
    </div>}
  </section>
}
