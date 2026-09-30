/** Native Studio's Results page: every run in a table; View run opens it in Tests, on Last run. */
import clsx from 'clsx'
import type { WorkbenchProps } from './contract.ts'
import { StudioIcon } from './StudioIcon.tsx'
import { friendlyTime, statusWordKey } from './presentation.ts'
import { downloadHtmlReport, record, resultValue, text } from './wire.ts'
import css from './Testy.module.css'

/** Render run history without replacing the selected run when newer runs arrive. */
export function ResultsPanel({ t, snapshot, view, actions, invoke, loadMoreRuns, attempt, busy, viewRun, refresh }: WorkbenchProps & {
  viewRun: (runId: string, testId: string) => void
  refresh: () => void
}) {
  const id = view.selectedRun ?? null
  const selected = snapshot.runs.find(item => item.runId === id)
  return <section className={css.pageScroll}>
    <header className={css.pageHeader}>
      <div><h1>{t('results')}</h1><p>{t('resultsSubtitle')}</p></div>
      <div className={css.actions}>
        <button type="button" className={css.subtleButton} disabled={busy} onClick={refresh}><StudioIcon kind="refresh" size={16} />{t('refresh')}</button>
        <button type="button" className={css.button} disabled={busy || !selected || selected.running === true} onClick={() => { attempt(async () => {
          const report = record(resultValue(await invoke('get_run_report', { runId: id })))
          if (report.mimeType !== 'text/html' || !text(report.html)) throw new Error(t('invalidReport'))
          downloadHtmlReport(text(report.filename), text(report.html))
        }) }}><StudioIcon kind="report" size={16} />{t('downloadReport')}</button>
        <button type="button" className={css.accentButton} disabled={busy || !selected} title={t('viewRunHelp')}
          onClick={() => { if (selected) viewRun(text(selected.runId), text(selected.testId)) }}><StudioIcon kind="view" size={16} />{t('viewRun')}</button>
      </div>
    </header>
    <div className={clsx(css.card, css.runsTable)}>
      <div className={css.runsHead} aria-hidden="true">
        <span>{t('colResult')}</span><span>{t('colTest')}</span><span>{t('colStarted')}</span><span>{t('colSummary')}</span>
        {view.technical && <span>{t('colRunId')}</span>}
      </div>
      <div className={css.runsBody} aria-label={t('results')}>
        {snapshot.runs.map((item) => {
          const status = item.running === true ? 'running' : text(item.status)
          return <button key={text(item.runId)} type="button" aria-pressed={id === item.runId}
            className={clsx(css.runsRow, id === item.runId && css.selected, view.technical && css.withId)}
            onClick={() => { actions.run(text(item.runId)) }} onDoubleClick={() => { viewRun(text(item.runId), text(item.testId)) }}>
            <span className={css.resultPill} data-status={status}>
              <span className={css.statusNode} data-status={status} aria-hidden="true">
                {status === 'passed' ? <StudioIcon kind="check" size={10} /> : status === 'failed' || status === 'cancelled' ? <StudioIcon kind="close" size={9} /> : null}
              </span>{t(statusWordKey(status))}</span>
            <span className={css.cellStrong}>{text(item.testName)}</span>
            <span className={css.caption}>{friendlyTime(text(item.startedAt))}</span>
            <span className={css.cellSecondary}>{text(item.summary)}</span>
            {view.technical && <span className={css.mono}>{text(item.runId)}</span>}
          </button>
        })}
        {!snapshot.runs.length && <div className={css.emptyState}><StudioIcon kind="results" size={28} />
          <strong>{t('noResults')}</strong><p>{t('noResultsHelp')}</p></div>}
      </div>
    </div>
    <footer className={css.pageFooter}>
      <span className={css.caption}>{t('loadedRuns', { count: snapshot.runs.length, total: snapshot.runTotal })}</span>
      {snapshot.nextRunOffset !== null && <button type="button" className={css.subtleButton} disabled={busy} onClick={() => { attempt(loadMoreRuns) }}>{t('loadOlderRuns')}</button>}
    </footer>
  </section>
}
