/** Native Studio's Settings and Help pages, adapted to Testy running inside Cortex. */
import type { ReactNode } from 'react'
import type { Translate } from '@cortex/client-ui-slots'
import type { WorkbenchProps } from './contract.ts'
import type { TestyKey } from './locales.ts'
import { StudioIcon, type StudioIconKind } from './StudioIcon.tsx'
import css from './Testy.module.css'

function Setting({ icon, title, description, children }: {
  icon: StudioIconKind
  title: string
  description: string
  children?: ReactNode
}) {
  return <div className={css.settingCard}>
    <StudioIcon kind={icon} />
    <div><strong>{title}</strong><p>{description}</p></div>
    {children && <div className={css.settingControl}>{children}</div>}
  </div>
}

/** How Testy runs in Cortex: the model, the data folder, technical details, and native Studio. */
export function SettingsPage({ t, snapshot, view, actions, busy, openStudio, refresh }: WorkbenchProps & {
  openStudio: () => void
  refresh: () => void
}) {
  const model = snapshot.status?.model
  return <section className={css.pageScroll}>
    <header className={css.pageHeader}><div><h1>{t('settings')}</h1><p>{t('settingsSubtitle')}</p></div></header>
    <div className={css.settingsBody}>
      <h2 className={css.settingsGroup}>{t('aiService')}</h2>
      <Setting icon="spark" title={model?.model || '—'} description={t('aiServiceHelp')}>
        <span className={css.caption}>{model?.provider}</span>
      </Setting>
      <h2 className={css.settingsGroup}>{t('displayOptions')}</h2>
      <Setting icon="settings" title={t('showTechnical')} description={t('technicalHelp')}>
        <label className={css.toggleSwitch}>
          <input type="checkbox" role="switch" aria-label={t('showTechnical')} checked={view.technical} onChange={(event) => { actions.technical(event.target.checked) }} />
          <i aria-hidden="true" /><span className={css.toggleState} aria-hidden="true">{view.technical ? t('on') : t('off')}</span>
        </label>
      </Setting>
      <h2 className={css.settingsGroup}>{t('dataFolder')}</h2>
      <Setting icon="folder" title={snapshot.status?.workspace || '—'} description={t('dataFolderHelp')} />
      <Setting icon="refresh" title={t('connection')} description={t('connectionHelp')}>
        <button type="button" className={css.button} disabled={busy} onClick={refresh}><StudioIcon kind="refresh" size={16} />{t('refresh')}</button>
      </Setting>
      <h2 className={css.settingsGroup}>{t('studioSettings')}</h2>
      <Setting icon="sample" title={t('studio')} description={t('nativeStudioHelp')}>
        <button type="button" className={css.button} disabled={busy} onClick={openStudio}><StudioIcon kind="sample" size={16} />{t('studio')}</button>
      </Setting>
    </div>
  </section>
}

/** What Testy does, in three steps, with the shortcuts that work here. */
export function HelpPage({ t }: { t: Translate<TestyKey> }) {
  const steps: [TestyKey, TestyKey][] = [['helpConnect', 'helpConnectText'], ['helpDescribe', 'helpDescribeText'], ['helpRun', 'helpRunText']]
  return <section className={css.pageScroll}>
    <header className={css.pageHeader}><div><h1>{t('help')}</h1><p>{t('helpSubtitle')}</p></div></header>
    <div className={css.settingsBody}>
      <ol className={css.helpSteps}>{steps.map(([title, body], index) => <li key={title} className={css.settingCard}>
        <span className={css.startMark} aria-hidden="true">{index + 1}</span><div><strong>{t(title)}</strong><p>{t(body)}</p></div>
      </li>)}</ol>
      <h2 className={css.settingsGroup}>{t('shortcuts')}</h2>
      <dl className={css.shortcuts}>
        <div><dt><kbd>Ctrl</kbd>+<kbd>K</kbd></dt><dd>{t('shortcutSearch')}</dd></div>
        <div><dt><kbd>Ctrl</kbd>+<kbd>S</kbd></dt><dd>{t('shortcutSave')}</dd></div>
        <div><dt><kbd>←</kbd> <kbd>→</kbd></dt><dd>{t('shortcutSteps')}</dd></div>
      </dl>
      <h2 className={css.settingsGroup}>{t('agentAccess')}</h2>
      <p className={css.settingsText}>{t('agentAccessText')}</p>
    </div>
  </section>
}
