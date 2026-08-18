/**
 * Confluence page view for the panel: breadcrumb, title, version facts,
 * labels, agent-routed edit prompt, and the converted body.
 */
import { IconRefreshOutline14, IconRightUpOutline14, MarkdownText } from '@cortex/client-ui-primitives'
import type { PageRecord } from '@cortex/atlassian/client'
import type { TranslateNS } from '@cortex/client-ui-slots'
import type { NS } from '../locales.ts'
import { Chip, InlineComposer, Person, SectionTitle } from '../atoms.tsx'
import { ago } from './IssueView.tsx'
import css from './entity.module.css'

/** Props of the page view. */
export interface PageViewProps {
  page: PageRecord
  now: number
  t: TranslateNS<typeof NS>
  onRefresh: () => void
  onPrompt: (text: string) => Promise<unknown>
}

/**
 * Render one page.
 * @param props - the page and its verbs.
 * @returns the view.
 */
export function PageView({ page, now, t, onRefresh, onPrompt }: PageViewProps) {
  return (
    <article className={css.entity} data-kind="page">
      <header className={css.head}>
        <div className={css.eyebrow}>
          <span className={css.breadcrumb}>
            <span className={css.crumb}>{page.space.name ?? page.space.key}</span>
            {page.ancestors.map(ancestor => (
              <span key={ancestor.id} className={css.breadcrumb}>
                <span className={css.crumbSep}>›</span>
                <span className={css.crumb}>{ancestor.title}</span>
              </span>
            ))}
          </span>
          <span className={css.spacer} />
          <button type="button" className={css.iconButton} title={t('panel.refresh')} aria-label={t('panel.refresh')} onClick={onRefresh}>
            <IconRefreshOutline14 />
          </button>
          <a className={css.iconButton} href={page.url} target="_blank" rel="noreferrer" title={t('panel.openIn', { service: 'Confluence' })} aria-label={t('panel.openIn', { service: 'Confluence' })}>
            <IconRightUpOutline14 />
          </a>
        </div>
        <h2 className={css.title}>{page.title}</h2>
        <div className={css.chips}>
          <Chip tone="info" mono>{t('page.version', { version: String(page.version) })}</Chip>
          {page.versionBy !== undefined ? <Chip>{t('page.by', { name: page.versionBy.name })}</Chip> : null}
          {page.versionAt !== undefined ? <Chip>{ago(t, page.versionAt, now)}</Chip> : null}
          {page.labels.map(label => <Chip key={label} mono>{label}</Chip>)}
        </div>
      </header>

      <div className={css.actions}>
        <InlineComposer
          label={t('page.edit')}
          prefix={t('page.editPrompt', { title: page.title, id: page.id })}
          onSend={onPrompt}
          sendLabel={t('page.edit').replace('…', '')}
        />
        {page.author !== undefined ? <span className={css.dim}><Person person={page.author} fallback="" /></span> : null}
      </div>

      <section>
        <SectionTitle title={t('page.body')} />
        <div className={css.body}>
          <MarkdownText text={page.body} />
        </div>
        {page.bodyTruncated ? <div className={css.muted}>{t('page.truncated')}</div> : null}
      </section>
    </article>
  )
}
