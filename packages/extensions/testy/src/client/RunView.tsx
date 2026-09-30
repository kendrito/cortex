/** Native Studio's Last run view: result bar, run trace, the steps with runner messages, and the screenshot with the control outlined. */
import { useEffect, useState } from 'react'
import clsx from 'clsx'
import { writeClipboard } from '@cortex/client-ui-primitives'
import type { WorkbenchProps } from './contract.ts'
import { EvidenceImage } from './EvidenceImage.tsx'
import { formatDuration, RunTrace } from './RunTrace.tsx'
import { StudioIcon } from './StudioIcon.tsx'
import { executeTask } from './task.ts'
import { statusWordKey } from './presentation.ts'
import { stepSentence } from './steps.ts'
import { downloadHtmlReport, downloadJson, number, record, resultImages, resultValue, rows, text, type RecordValue } from './wire.ts'
import css from './Testy.module.css'

/** Show one run the way a live run is shown: in Tests, under the open test's Last run. */
export function RunView({ t, snapshot, view, invoke, attempt, busy, runId, missingTest }: WorkbenchProps & {
  runId: string | null
  missingTest: string | null
}) {
  const [loaded, setLoaded] = useState<{ id: string; value: RecordValue } | null>(null)
  const [selectedStep, setSelectedStep] = useState<number | null>(null)
  const [capture, setCapture] = useState<{ key: string; images: string[] } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [analysis, setAnalysis] = useState<{ id: string; value: string } | null>(null)
  const [copied, setCopied] = useState(false)
  const run = runId && loaded?.id === runId ? loaded.value : null
  const entry = snapshot.runs.find(item => item.runId === runId)
  // Poll while the run is live (or not loaded yet); a finished run is read once.
  const poll = run === null || run.running === true || entry?.running === true ? snapshot.runs : null
  useEffect(() => { setSelectedStep(null); setCopied(false) }, [runId])
  useEffect(() => {
    if (!runId) return
    let active = true
    void invoke('get_run', { runId }).then((result) => {
      if (!active) return
      setLoaded({ id: runId, value: record(resultValue(result)) }); setError(null)
    }, (cause: unknown) => { if (active) setError(cause instanceof Error ? cause.message : String(cause)) })
    return () => { active = false }
  }, [runId, poll, invoke])
  const steps = rows(run?.steps)
  const chosen = steps.find(step => number(step.number) === selectedStep)
    ?? steps.find(step => ['failed', 'cancelled'].includes(text(step.status))) ?? steps.find(step => text(step.status) === 'running') ?? steps.at(-1)
  const stepNumber = number(chosen?.number)
  const captureKey = `${runId ?? ''}:${stepNumber}`
  const available = chosen?.screenshotAvailable === true
  useEffect(() => {
    if (!runId || !stepNumber || !available) return
    let active = true
    void invoke('get_run_screenshot', { runId, stepNumber }).then((result) => {
      if (active) setCapture({ key: captureKey, images: resultImages(result) })
    }, (cause: unknown) => { if (active) setError(cause instanceof Error ? cause.message : String(cause)) })
    return () => { active = false }
  }, [runId, stepNumber, available, captureKey, invoke])
  if (!runId) {
    return <div className={css.emptyState}><StudioIcon kind="play" size={28} /><strong>{t('noRunYet')}</strong><p>{t('noRunYetHelp')}</p></div>
  }
  if (!run) {
    return <div className={css.runView}>
      {error ? <section className={clsx(css.infoBar, css.infoBarError)} role="alert"><StudioIcon kind="warning" size={18} /><div><p>{error}</p></div></section>
        : <p className={css.hint}>{t('loading')}</p>}
    </div>
  }
  const running = run.running === true
  const status = running ? 'running' : text(run.status)
  const total = steps.length
  const passed = steps.filter(step => text(step.status) === 'passed').length
  const problem = steps.find(step => ['failed', 'cancelled'].includes(text(step.status)))
  const current = steps.find(step => text(step.status) === 'running')
  const outcome = running ? t('outcomeRunning', { number: number(current?.number) || passed + 1, total })
    : status === 'passed' ? t('outcomePassed', { passed, total, time: formatDuration(number(run.durationMs)) })
      : status === 'failed' && problem ? t('outcomeFailed', { number: number(problem.number), total })
        : status === 'cancelled' && problem ? t('outcomeStopped', { number: number(problem.number), total })
          : total === 0 ? t('outcomeEmpty') : text(run.summary)
  const summary = text(run.summary)
  const word = t(statusWordKey(status))
  const image = capture?.key === captureKey ? capture.images[0] : undefined
  const nativeSnapshot = record(chosen?.snapshot)
  const selectedControl = rows(nativeSnapshot.elements).find(item => text(item.selector) === text(chosen?.selector))
  const index = chosen ? steps.indexOf(chosen) : -1
  const notes = analysis?.id === runId ? analysis.value : text(run.aiAnalysis) || (run.aiAnalysis ? JSON.stringify(run.aiAnalysis, null, 2) : '')
  return <div className={css.runView}>
    {missingTest && <section className={clsx(css.infoBar, css.infoBarWarning)} role="status"><StudioIcon kind="warning" size={18} />
      <div><p>{t('runGone', { name: missingTest })}</p></div></section>}
    {error && <section className={clsx(css.infoBar, css.infoBarError)} role="alert"><StudioIcon kind="warning" size={18} /><div><p>{error}</p></div></section>}
    <section className={css.resultBar} data-status={status} aria-label={t('summary')}>
      <span className={css.statusNode} data-status={status} aria-hidden="true">
        {status === 'passed' ? <StudioIcon kind="check" size={12} /> : status === 'failed' || status === 'cancelled' ? <StudioIcon kind="close" size={11} /> : null}
      </span>
      <p title={summary}>
        <strong>{word}</strong><span>{outcome}</span>
        {summary && summary !== outcome && <span className={css.caption}>{summary}</span>}
      </p>
      <div className={css.actions}>
        <button type="button" className={css.button} onClick={() => { attempt(async () => {
          setCopied(await writeClipboard(`${text(run.testName)}: ${word}. ${outcome}.${summary && summary !== outcome ? ` ${summary}` : ''}`))
        }) }}><StudioIcon kind={copied ? 'check' : 'copy'} size={16} />{copied ? t('copied') : t('copySummary')}</button>
        <button type="button" className={css.button} disabled={busy || running} onClick={() => { attempt(async () => {
          const report = record(resultValue(await invoke('get_run_report', { runId })))
          if (report.mimeType !== 'text/html' || !text(report.html)) throw new Error(t('invalidReport'))
          downloadHtmlReport(text(report.filename), text(report.html))
        }) }}><StudioIcon kind="report" size={16} />{t('downloadReport')}</button>
        {view.technical && <button type="button" className={css.subtleButton} onClick={() => { downloadJson(text(run.testName), run) }}>{t('report')}</button>}
      </div>
    </section>
    <section className={clsx(css.card, css.traceCard)} aria-label={t('trace')}>
      <h3>{t('trace')}</h3>
      <RunTrace run={run} runId={runId} label={t('trace')} selected={chosen ? stepNumber : undefined} onSelect={setSelectedStep} invoke={invoke} />
    </section>
    <div className={css.runColumns}>
      <div className={css.runLeft}>
        <div className={css.card}>
          <ol className={css.runSteps} aria-label={t('stepsInRun')}>{steps.map((step) => {
            const sentence = stepSentence(step)
            const stepStatus = text(step.status)
            const message = text(step.message)
            const problemStep = stepStatus === 'failed' || stepStatus === 'cancelled'
            return <li key={number(step.number)}><button type="button" aria-pressed={step === chosen}
              className={clsx(css.runStep, step === chosen && css.selected)} onClick={() => { setSelectedStep(number(step.number)) }}>
              <span className={css.statusNode} data-status={stepStatus} aria-hidden="true">
                {stepStatus === 'passed' ? <StudioIcon kind="check" size={10} /> : problemStep ? <StudioIcon kind="close" size={9} /> : null}
              </span>
              <span className={css.stepNo}>{number(step.number)}</span>
              <span className={css.stepVerb}>{text(step.selector) ? sentence.verb : ''}</span>
              <span className={css.sentence}><span>{text(step.selector)
                ? sentence.parts.map((part, at) => <span key={at} className={css[`part_${part.kind}`]}>{part.text}</span>)
                : text(step.title) || sentence.plain}</span></span>
              <span className={css.duration}>{formatDuration(number(step.durationMs))}</span>
              {message && <span className={clsx(css.stepMessage, problemStep && css.problem)}>{message}</span>}
              {view.technical && text(step.selector) && <span className={css.stepSelector}>{text(step.selector)}</span>}
            </button></li>
          })}</ol>
          {!steps.length && <p className={css.hint}>{t('outcomeEmpty')}</p>}
        </div>
        <details className={clsx(css.card, css.aiNotes)} open={Boolean(notes) || undefined}>
          <summary><StudioIcon kind="notes" size={18} /><span>{t('aiNotes')}</span><span className={css.caption}>{notes ? t('notesCount') : t('noNotes')}</span></summary>
          <p className={clsx(css.prose, !notes && css.hint)}>{notes || t('notesHelp')}</p>
          <button type="button" className={css.button} disabled={busy || running} onClick={() => { attempt(async () => {
            const result = await executeTask(invoke, 'explain_run', { runId })
            setAnalysis({ id: runId, value: text(result.analysis ?? result.explanation ?? result.message) || JSON.stringify(result) })
          }) }}><StudioIcon kind="spark" size={16} />{t('explain')}</button>
        </details>
      </div>
      <div className={css.runRight}>
        <div className={css.shotCaption}>
          <p>{chosen && <><strong>{t('step', { number: stepNumber })}</strong><span>{text(chosen.title) || stepSentence(chosen).plain}</span></>}</p>
          <div className={css.actions}>
            <button type="button" className={css.iconButton} aria-label={t('previousStep')} disabled={index <= 0}
              onClick={() => { setSelectedStep(number(steps[index - 1]?.number)) }}><StudioIcon kind="chevronLeft" size={16} /></button>
            <span className={css.caption}>{index >= 0 ? t('shotPosition', { number: index + 1, total }) : ''}</span>
            <button type="button" className={css.iconButton} aria-label={t('nextStep')} disabled={index < 0 || index >= total - 1}
              onClick={() => { setSelectedStep(number(steps[index + 1]?.number)) }}><StudioIcon kind="chevronRight" size={16} /></button>
          </div>
        </div>
        <div className={css.shotFrame}>
          {image ? <EvidenceImage image={image} label={t('screenshot')} control={selectedControl?.bounds}
            capture={record(chosen?.screenshotEvidence).bounds ?? nativeSnapshot.screenshotBounds} />
            : <div className={css.emptyState}><StudioIcon kind="view" size={28} /><p>{t('noEvidence')}</p></div>}
        </div>
        {image && selectedControl && <p className={css.shotNote}><i aria-hidden="true" />{t('outlineNote')}</p>}
        {rows(chosen?.diagnostics).map((finding, at) => <div key={at} className={css.finding}>
          <strong>{text(finding.label) || text(finding.category)}</strong>
          <p>{text(finding.observedFact)}</p><p>{text(finding.causeAssessment)}</p>
          {Array.isArray(finding.suggestedNextChecks) && <ul>
            {finding.suggestedNextChecks.map((check, item) => <li key={item}>{text(check)}</li>)}
          </ul>}
        </div>)}
        {view.technical && <details className={css.technical} open><summary>{t('stepDetails')}</summary><pre>{JSON.stringify(chosen ?? run, null, 2)}</pre></details>}
      </div>
    </div>
  </div>
}
