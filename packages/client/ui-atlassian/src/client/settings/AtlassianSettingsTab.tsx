/**
 * Settings → Plugins → Atlassian: three service cards (URL, token, scope
 * field, live mount status, connection test), the launch commands, the write
 * policy, and the toolset bounds. Settings write through the settings scope
 * one field at a time; tokens go to the credentials store.
 */
import { useEffect, useState } from 'react'
import { Button, StateDot } from '@cortex/client-ui-primitives'
import type { AtlassianSettings, AtlassianStatus, MountStatus, ProbeResult } from '@cortex/atlassian/client'
import type { SettingsTabProps } from '../contract.ts'
import { Chip } from '../atoms.tsx'
import css from './settings.module.css'

type ServiceKey = 'jira' | 'confluence' | 'bitbucket'

const SERVICES: readonly { key: ServiceKey; url: keyof AtlassianSettings; token: keyof AtlassianSettings; extra: keyof AtlassianSettings; extraLabel: 'settings.projectsFilter' | 'settings.spacesFilter' | 'settings.defaultProject'; extraHint: 'settings.projectsHint' | 'settings.spacesHint' | 'settings.defaultProjectHint'; mount: 'atlassian' | 'bitbucket' }[] = [
  { key: 'jira', url: 'jiraUrl', token: 'jiraTokenRef', extra: 'jiraProjectsFilter', extraLabel: 'settings.projectsFilter', extraHint: 'settings.projectsHint', mount: 'atlassian' },
  { key: 'confluence', url: 'confluenceUrl', token: 'confluenceTokenRef', extra: 'confluenceSpacesFilter', extraLabel: 'settings.spacesFilter', extraHint: 'settings.spacesHint', mount: 'atlassian' },
  { key: 'bitbucket', url: 'bitbucketUrl', token: 'bitbucketTokenRef', extra: 'bitbucketDefaultProject', extraLabel: 'settings.defaultProject', extraHint: 'settings.defaultProjectHint', mount: 'bitbucket' },
]

const TEXT_FIELDS: readonly (keyof AtlassianSettings)[] = [
  'jiraUrl', 'jiraProjectsFilter', 'confluenceUrl', 'confluenceSpacesFilter', 'bitbucketUrl', 'bitbucketDefaultProject',
  'atlassianLaunch', 'bitbucketLaunch', 'toolsets', 'enabledTools',
]

/** Poll cadence while a mount is starting. */
const POLL_MS = 2_000

type Draft = Partial<Record<keyof AtlassianSettings, string>>

/**
 * One mount's status pill.
 * @param props.status - mount status.
 * @param props.t - translate seat.
 * @returns the pill.
 */
function MountPill({ status, t }: { status: MountStatus | undefined; t: SettingsTabProps['t'] }) {
  if (status === undefined) return <Chip>{t('panel.pending')}</Chip>
  switch (status.phase) {
    case 'ready': return <Chip tone="success" dot>{t('settings.phase.ready', { count: String(status.toolCount) })}</Chip>
    case 'starting': return <Chip tone="info"><StateDot state="ongoing" size={8} /> {t('settings.phase.starting')}</Chip>
    case 'error': return <Chip tone="error" dot title={status.error}>{t('settings.phase.error')}</Chip>
    default: return <Chip title={(status.missing ?? []).map(item => t(`settings.missing.${item}`)).join(', ')}>{t('settings.phase.off')}{status.missing !== undefined && status.missing.length > 0 ? ` · ${status.missing.map(item => t(`settings.missing.${item}`)).join(', ')}` : ''}</Chip>
  }
}

/**
 * Render the Atlassian settings tab.
 * @param props - settings scope hook, verbs, and copy.
 * @returns the tab.
 */
export function AtlassianSettingsTab(props: SettingsTabProps) {
  const { useSettings, writable, setField, describeTokens, setToken, status: loadStatus, probe, reconnect, t } = props
  const snapshot = useSettings(s => s)
  const value = snapshot.value
  const [draft, setDraft] = useState<Draft>({})
  const [tokens, setTokens] = useState<Record<ServiceKey, string>>({ jira: '', confluence: '', bitbucket: '' })
  const [tokenState, setTokenState] = useState<Record<string, { configured: boolean; writable: boolean }>>({})
  const [status, setStatus] = useState<AtlassianStatus | undefined>(undefined)
  const [probes, setProbes] = useState<Partial<Record<ServiceKey, ProbeResult | 'testing'>>>({})
  const [saving, setSaving] = useState(false)
  const [saved, setSaved] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const refreshStatus = (): void => { void loadStatus().then(setStatus, () => undefined) }
  useEffect(() => { refreshStatus() }, [])
  useEffect(() => {
    if (status === undefined) return
    if (status.atlassian.phase !== 'starting' && status.bitbucket.phase !== 'starting') return
    const timer = setTimeout(refreshStatus, POLL_MS)
    return () => { clearTimeout(timer) }
  }, [status])
  useEffect(() => {
    if (value === undefined) return
    const refs = [value.jiraTokenRef, value.confluenceTokenRef, value.bitbucketTokenRef]
    void describeTokens(refs).then(setTokenState, () => undefined)
  }, [value?.jiraTokenRef, value?.confluenceTokenRef, value?.bitbucketTokenRef])

  if (value === undefined) return <div className={css.pending}>{t('panel.pending')}</div>

  const current = (field: keyof AtlassianSettings): string => draft[field] ?? value[field]
  const dirty = TEXT_FIELDS.some(field => draft[field] !== undefined && draft[field] !== value[field])
    || (draft.writes !== undefined && draft.writes !== value.writes)
    || Object.values(tokens).some(token => token.trim() !== '')
  const disabled = !writable || saving

  const save = async (): Promise<void> => {
    setSaving(true)
    setSaved(false)
    setError(null)
    try {
      for (const field of [...TEXT_FIELDS, 'writes' as const]) {
        const next = draft[field]
        if (next === undefined || next === value[field]) continue
        const outcome = await setField(field, next)
        if (!outcome.ok) throw new Error(outcome.message)
      }
      for (const service of SERVICES) {
        const token = tokens[service.key].trim()
        if (token === '') continue
        const outcome = await setToken(value[service.token], token)
        if (!outcome.ok) throw new Error(outcome.message)
      }
      setDraft({})
      setTokens({ jira: '', confluence: '', bitbucket: '' })
      setSaved(true)
      const refs = [value.jiraTokenRef, value.confluenceTokenRef, value.bitbucketTokenRef]
      void describeTokens(refs).then(setTokenState, () => undefined)
      setStatus(await reconnect())
    } catch (caught: unknown) {
      setError(caught instanceof Error ? caught.message : String(caught))
    } finally {
      setSaving(false)
    }
  }

  const runProbe = async (service: ServiceKey): Promise<void> => {
    setProbes(previous => ({ ...previous, [service]: 'testing' }))
    const result = await probe(service)
    setProbes(previous => ({ ...previous, [service]: result }))
  }

  return (
    <div className={css.tab}>
      <header className={css.header}>
        <div>
          <h3 className={css.title}>{t('settings.title')}</h3>
          <p className={css.intro}>{t('settings.intro')}</p>
        </div>
        <Button size="sm" variant="outline" onClick={() => { void reconnect().then(setStatus, () => undefined) }}>{t('settings.reconnect')}</Button>
      </header>
      {!writable ? <div className={css.notice}>{t('settings.readOnly')}</div> : null}

      <div className={css.grid}>
        {SERVICES.map((service) => {
          const tokenRef = value[service.token]
          const known = tokenState[tokenRef]
          const probeResult = probes[service.key]
          return (
            <section key={service.key} className={css.card}>
              <div className={css.cardHead}>
                <span className={css.cardTitle}>{t(`settings.${service.key}`)}</span>
                <MountPill status={status?.[service.mount]} t={t} />
              </div>
              <label className={css.field}>
                <span className={css.label}>{t('settings.url')}</span>
                <input className={css.input} type="url" placeholder={t('settings.urlHint')} value={current(service.url)} disabled={disabled} onChange={(event) => { setDraft(previous => ({ ...previous, [service.url]: event.target.value })) }} />
              </label>
              <label className={css.field}>
                <span className={css.label}>
                  {t('settings.token')}
                  <Chip tone={known?.configured === true ? 'success' : 'neutral'}>{known?.configured === true ? t('settings.tokenSet') : t('settings.tokenUnset')}</Chip>
                </span>
                <input className={css.input} type="password" autoComplete="off" value={tokens[service.key]} disabled={disabled || known?.writable === false} onChange={(event) => { setTokens(previous => ({ ...previous, [service.key]: event.target.value })) }} />
                <span className={css.hint}>{writable ? t('settings.tokenHint') : t('settings.tokenReadOnly')} <code className={css.code}>{tokenRef}</code></span>
              </label>
              <label className={css.field}>
                <span className={css.label}>{t(service.extraLabel)}</span>
                <input className={css.input} type="text" value={current(service.extra)} disabled={disabled} onChange={(event) => { setDraft(previous => ({ ...previous, [service.extra]: event.target.value })) }} />
                <span className={css.hint}>{t(service.extraHint)}</span>
              </label>
              <div className={css.cardFoot}>
                <Button size="sm" variant="ghost" disabled={probeResult === 'testing'} onClick={() => { void runProbe(service.key) }}>
                  {probeResult === 'testing' ? t('settings.testing') : t('settings.test')}
                </Button>
                {probeResult !== undefined && probeResult !== 'testing' ? (
                  <span className={probeResult.ok ? css.ok : css.fail}>
                    {probeResult.ok ? t('settings.testOk', { user: probeResult.user ?? '' }) : t('settings.testFail', { error: probeResult.error ?? '' })}
                  </span>
                ) : null}
              </div>
              {status?.[service.mount].phase === 'error' && status[service.mount].error !== undefined ? (
                <div className={css.errorText}>{status[service.mount].error}</div>
              ) : null}
            </section>
          )
        })}
      </div>

      <section className={css.card}>
        <div className={css.cardHead}><span className={css.cardTitle}>{t('settings.writes')}</span></div>
        <div className={css.radios} role="radiogroup" aria-label={t('settings.writes')}>
          {(['ask', 'allow', 'deny'] as const).map(policy => (
            <label key={policy} className={css.radio} data-active={(draft.writes ?? value.writes) === policy || undefined}>
              <input type="radio" name="atlassian-writes" value={policy} disabled={disabled} checked={(draft.writes ?? value.writes) === policy} onChange={() => { setDraft(previous => ({ ...previous, writes: policy })) }} />
              <span>{t(`settings.writes.${policy}`)}</span>
            </label>
          ))}
        </div>
      </section>

      <section className={css.card}>
        <div className={css.cardHead}><span className={css.cardTitle}>MCP</span></div>
        <label className={css.field}>
          <span className={css.label}>{t('settings.launch')} · Jira/Confluence</span>
          <input className={css.inputMono} type="text" value={current('atlassianLaunch')} disabled={disabled} onChange={(event) => { setDraft(previous => ({ ...previous, atlassianLaunch: event.target.value })) }} />
          <span className={css.hint}>{t('settings.launchAtlassianHint')}</span>
        </label>
        <label className={css.field}>
          <span className={css.label}>{t('settings.launch')} · Bitbucket</span>
          <input className={css.inputMono} type="text" value={current('bitbucketLaunch')} disabled={disabled} onChange={(event) => { setDraft(previous => ({ ...previous, bitbucketLaunch: event.target.value })) }} />
          <span className={css.hint}>{t('settings.launchBitbucketHint')}</span>
        </label>
        <div className={css.pair}>
          <label className={css.field}>
            <span className={css.label}>{t('settings.toolsets')}</span>
            <input className={css.input} type="text" value={current('toolsets')} disabled={disabled} onChange={(event) => { setDraft(previous => ({ ...previous, toolsets: event.target.value })) }} />
            <span className={css.hint}>{t('settings.toolsetsHint')}</span>
          </label>
          <label className={css.field}>
            <span className={css.label}>{t('settings.enabledTools')}</span>
            <input className={css.input} type="text" value={current('enabledTools')} disabled={disabled} onChange={(event) => { setDraft(previous => ({ ...previous, enabledTools: event.target.value })) }} />
            <span className={css.hint}>{t('settings.enabledToolsHint')}</span>
          </label>
        </div>
      </section>

      <footer className={css.saveBar} data-dirty={dirty || undefined}>
        {error !== null ? <span className={css.fail}>{t('settings.saveError', { message: error })}</span> : saved && !dirty ? <span className={css.ok}>{t('settings.saved')}</span> : <span />}
        <span className={css.spacer} />
        <Button size="sm" variant="ghost" disabled={!dirty || saving} onClick={() => { setDraft({}); setTokens({ jira: '', confluence: '', bitbucket: '' }) }}>{t('settings.discard')}</Button>
        <Button size="sm" variant="primary" disabled={!dirty || disabled} onClick={() => { void save() }}>{saving ? t('settings.saving') : t('settings.save')}</Button>
      </footer>
    </div>
  )
}
