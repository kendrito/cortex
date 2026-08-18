/**
 * Work tab: the entity in focus (issue / page / pull request) and the recent
 * list beneath it. The focus follows the projection unless the user picked
 * something; a "back" affordance returns to the live focus.
 */
import { IconChevronLeftOutline14 } from '@cortex/client-ui-primitives'
import type { AtlassianProjection, EntityRef, PrRef, ReviewRecord } from '@cortex/atlassian/client'
import type { TranslateNS } from '@cortex/client-ui-slots'
import { prStateTone, refKey, sameRef, statusTone } from '../format.ts'
import type { NS } from '../locales.ts'
import { Chip, Empty, SectionTitle, ServiceGlyph } from '../atoms.tsx'
import { IssueView } from './IssueView.tsx'
import { PageView } from './PageView.tsx'
import { PrView } from './PrView.tsx'
import css from './drawer.module.css'

/** Props of the work tab. */
export interface WorkTabProps {
  projection: AtlassianProjection
  /** User-selected entity overriding the projection focus. */
  selected: EntityRef | null
  now: number
  t: TranslateNS<typeof NS>
  onSelect: (ref: EntityRef | null) => void
  onPin: (key: string | null) => void
  onRefresh: (ref: EntityRef) => void
  onOpenIssue: (key: string) => void
  onOpenPr: (pr: PrRef) => void
  onPrompt: (text: string) => Promise<unknown>
  onReview: (pr: PrRef) => void
  onShowReview: () => void
}

/**
 * Latest review of one PR key.
 * @param projection - panel state.
 * @param prKey - PR key.
 * @returns the review, or `undefined`.
 */
export function latestReviewOf(projection: AtlassianProjection, prKey: string): ReviewRecord | undefined {
  return Object.values(projection.reviews)
    .filter(review => review.prKey === prKey)
    .sort((a, b) => b.startedAt - a.startedAt)[0]
}

/**
 * Compact one-line label of an entity for lists.
 * @param projection - panel state.
 * @param ref - entity reference.
 * @returns key + title, or `undefined` when the record is gone.
 */
/** One recent-list row. */
interface RecentRow {
  key: string
  title: string
  chip: { text: string; tone: ReturnType<typeof statusTone> }
  service: 'jira' | 'confluence' | 'bitbucket'
}

function rowOf(projection: AtlassianProjection, ref: EntityRef): RecentRow | undefined {
  switch (ref.kind) {
    case 'issue': {
      const issue = projection.issues[ref.key]
      return issue === undefined ? undefined : { key: issue.key, title: issue.summary, chip: { text: issue.status.name, tone: statusTone(issue.status) }, service: 'jira' }
    }
    case 'page': {
      const page = projection.pages[ref.id]
      return page === undefined ? undefined : { key: page.space.key, title: page.title, chip: { text: `v${String(page.version)}`, tone: 'neutral' }, service: 'confluence' }
    }
    case 'pr': {
      const pr = projection.prs[ref.key]
      return pr === undefined ? undefined : { key: pr.key, title: pr.title, chip: { text: pr.state.toLowerCase(), tone: prStateTone(pr.state) }, service: 'bitbucket' }
    }
    /* v8 ignore next 2 -- closed union backstop */
    default: return undefined
  }
}

/**
 * Render the work tab.
 * @param props - projection, selection, and verbs.
 * @returns the tab.
 */
export function WorkTab(props: WorkTabProps) {
  const { projection, selected, now, t, onSelect, onPin, onRefresh, onOpenIssue, onOpenPr, onPrompt, onReview, onShowReview } = props
  const focus = selected ?? projection.focus
  const focused = focus === null ? undefined : (
    focus.kind === 'issue' ? projection.issues[focus.key]
      : focus.kind === 'page' ? projection.pages[focus.id]
        : projection.prs[focus.key])
  const overriding = selected !== null && !sameRef(selected, projection.focus)
  const others = projection.recent.filter(ref => !sameRef(ref, focus))

  return (
    <div className={css.work}>
      {overriding ? (
        <button type="button" className={css.back} onClick={() => { onSelect(null) }}>
          <IconChevronLeftOutline14 /> {t('panel.back')}
        </button>
      ) : null}
      {focused === undefined ? (
        projection.recent.length === 0
          ? <Empty title={t('panel.empty.title')} body={t('panel.empty.body')} icon={<ServiceGlyph service="jira" size={22} />} />
          : <div className={css.pending}>{t('card.pending')}</div>
      ) : focused.kind === 'issue' ? (
        <IssueView
          issue={focused}
          pinned={projection.pinned === focused.key}
          now={now}
          t={t}
          onPin={onPin}
          onRefresh={() => { onRefresh({ kind: 'issue', key: focused.key }) }}
          onPrompt={onPrompt}
          onOpenIssue={onOpenIssue}
        />
      ) : focused.kind === 'page' ? (
        <PageView page={focused} now={now} t={t} onRefresh={() => { onRefresh({ kind: 'page', id: focused.id }) }} onPrompt={onPrompt} />
      ) : (
        <PrView
          pr={focused}
          review={latestReviewOf(projection, focused.key)}
          now={now}
          t={t}
          onRefresh={() => { onRefresh({ kind: 'pr', key: focused.key }) }}
          onPrompt={onPrompt}
          onReview={() => { onReview(focused.ref) }}
          onShowReview={onShowReview}
        />
      )}
      {others.length > 0 ? (
        <section>
          <SectionTitle title={t('panel.recent')} count={others.length} />
          <ul className={css.recent}>
            {others.map((ref) => {
              const row = rowOf(projection, ref)
              if (row === undefined) return null
              return (
                <li key={refKey(ref)}>
                  <button type="button" className={css.recentRow} onClick={() => { onSelect(ref) }}>
                    <ServiceGlyph service={row.service} size={16} />
                    <span className={css.recentKey}>{row.key}</span>
                    <span className={css.recentTitle}>{row.title}</span>
                    <Chip tone={row.chip.tone}>{row.chip.text}</Chip>
                    {ref.kind === 'issue' && projection.pinned === ref.key ? <Chip tone="accent">{t('panel.pinned')}</Chip> : null}
                  </button>
                </li>
              )
            })}
          </ul>
        </section>
      ) : null}
      {focused === undefined && projection.pinned !== null && projection.issues[projection.pinned] === undefined ? (
        <button type="button" className={css.recentRow} onClick={() => { /* v8 ignore next -- rendered only while pinned is set */ onOpenIssue(projection.pinned ?? '') }}>
          <ServiceGlyph service="jira" size={16} />
          <span className={css.recentKey}>{projection.pinned}</span>
          <Chip tone="accent">{t('panel.pinned')}</Chip>
        </button>
      ) : null}
      {focus !== null && focused === undefined && focus.kind === 'pr' ? (
        <button type="button" className={css.recentRow} onClick={() => { onOpenPr(parsePrKey(focus.key)) }}>
          <ServiceGlyph service="bitbucket" size={16} />
          <span className={css.recentKey}>{focus.key}</span>
        </button>
      ) : null}
    </div>
  )
}

/**
 * Parse a `PROJECT/repo#id` key back into an address.
 * @param key - PR key.
 * @returns the address (empty project when the key is malformed).
 */
export function parsePrKey(key: string): PrRef {
  const match = /^([^/]+)\/([^#]+)#(\d+)$/.exec(key)
  /* v8 ignore next -- capture groups are defined on a match; the fallbacks only satisfy the index signature */
  return match === null ? { project: '', repo: key, id: 0 } : { project: match[1] ?? '', repo: match[2] ?? '', id: Number(match[3]) }
}
