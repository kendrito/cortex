/** Native Studio's "Getting started" bar: choose an AI, describe a test, run it. Cortex finds the app, so there is no app step. */
import { useState } from 'react'
import clsx from 'clsx'
import type { Translate } from '@cortex/client-ui-slots'
import type { TestyKey } from './locales.ts'
import { StudioIcon } from './StudioIcon.tsx'
import css from './Testy.module.css'

const DISMISSED = 'cortex.testy.gettingStarted.dismissed'

function readDismissed(): boolean {
  try { return globalThis.localStorage.getItem(DISMISSED) === '1' } catch { return false }
}

/** Three first steps; the bar hides itself once all are done or it is dismissed. */
export function GettingStarted({ t, ai, described, ran, onAi, onDescribe, onRun }: {
  t: Translate<TestyKey>
  ai: boolean
  described: boolean
  ran: boolean
  onAi: () => void
  onDescribe: () => void
  onRun: () => void
}) {
  const [dismissed, setDismissed] = useState(readDismissed)
  const steps: { done: boolean; label: TestyKey; help: TestyKey; act: () => void }[] = [
    { done: ai, label: 'gsAi', help: 'gsAiHelp', act: onAi },
    { done: described, label: 'gsDescribe', help: 'gsDescribeHelp', act: onDescribe },
    { done: ran, label: 'gsRun', help: 'gsRunHelp', act: onRun },
  ]
  const done = steps.filter(step => step.done).length
  if (dismissed || done === steps.length) return null
  return <section className={css.infoBar} aria-label={t('gettingStarted')}>
    <StudioIcon kind="info" size={18} />
    <div>
      <p><strong>{t('gettingStarted')}</strong><span className={css.caption}>{t('gettingStartedCount', { done })}</span></p>
      <ol className={css.startSteps}>
        {steps.map((step, index) => <li key={step.label} className={clsx(step.done && css.done)}>
          {index > 0 && <StudioIcon kind="chevronRight" size={12} />}
          <span className={css.startMark} aria-hidden="true">{step.done ? <StudioIcon kind="check" size={12} /> : index + 1}</span>
          <button type="button" className={css.linkButton} title={t(step.help)} onClick={step.act}>{t(step.label)}</button>
        </li>)}
      </ol>
    </div>
    <button type="button" className={css.iconButton} aria-label={t('dismissGettingStarted')} onClick={() => {
      setDismissed(true)
      try { globalThis.localStorage.setItem(DISMISSED, '1') } catch { /* private mode: dismissed for this session */ }
    }}><StudioIcon kind="close" size={14} /></button>
  </section>
}
