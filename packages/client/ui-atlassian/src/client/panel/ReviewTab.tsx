/**
 * Review tab: the pull request picker (inbox or one repository) and the live
 * review run — status, severity histogram, filters, bulk post, and the
 * findings list that grows as the agent records them.
 */
import { useEffect, useMemo, useState } from 'react'
import { Button, IconRefreshOutline14, Input, StateDot } from '@cortex/client-ui-primitives'
import type {
  DiffContextResult, ListPullRequestsResult, PostFindingResult, PrRecord, PrRef, PrSummary, ReviewFinding, ReviewRecord,
} from '@cortex/atlassian/client'
import type { TranslateNS } from '@cortex/client-ui-slots'
import { severityTone, sortFindings } from '../format.ts'
import type { NS } from '../locales.ts'
import type { ReviewFilter } from '../store.ts'
import { Avatar, Chip, SectionTitle } from '../atoms.tsx'
import { ExistingCommentList } from './ExistingComments.tsx'
import { FindingCard } from './FindingCard.tsx'
import { ago } from './IssueView.tsx'
import css from './review.module.css'

/** Props of the review tab. */
export interface ReviewTabProps {
  /** The review to show (active or latest), when any. */
  review: ReviewRecord | undefined
  /** PR record of that review when the panel has it. */
  pr: PrRecord | undefined
  filter: ReviewFilter
  now: number
  t: TranslateNS<typeof NS>
  defaultProject: string
  onFilter: (filter: ReviewFilter) => void
  onList: (scope: 'inbox' | 'repo', project: string, repo: string) => Promise<ListPullRequestsResult>
  onStart: (pr: PrRef, focus: string) => Promise<{ ok: true } | { ok: false; message: string }>
  onCancel: (reviewId: string) => Promise<unknown>
  onPost: (reviewId: string, findingId: string, comment: string | undefined) => Promise<PostFindingResult>
  onDismiss: (reviewId: string, findingId: string) => Promise<unknown>
  onDiffContext: (pr: PrRef, finding: ReviewFinding) => Promise<DiffContextResult>
  onOpenPr: (pr: PrRef) => void
}

type ListState =
  | { status: 'idle' }
  | { status: 'loading' }
  | { status: 'ready'; items: PrSummary[] }
  | { status: 'error'; message: string }

/**
 * Pull request picker.
 * @param props - listing verbs and the start callback.
 * @returns the picker.
 */
function Picker({ t, defaultProject, now, onList, onStart, onOpenPr }: Pick<ReviewTabProps, 't' | 'defaultProject' | 'now' | 'onList' | 'onStart' | 'onOpenPr'>) {
  const [scope, setScope] = useState<'inbox' | 'repo'>('inbox')
  const [project, setProject] = useState(defaultProject)
  const [repo, setRepo] = useState('')
  const [list, setList] = useState<ListState>({ status: 'idle' })
  const [selected, setSelected] = useState<PrSummary | null>(null)
  const [focus, setFocus] = useState('')
  const [starting, setStarting] = useState(false)
  const [startError, setStartError] = useState<string | null>(null)

  const load = async (nextScope: 'inbox' | 'repo' = scope): Promise<void> => {
    setList({ status: 'loading' })
    const result = await onList(nextScope, project, repo)
    setList(result.ok ? { status: 'ready', items: result.items } : { status: 'error', message: result.message })
  }

  useEffect(() => {
    /* v8 ignore next -- mount-time state is always inbox/idle */
    if (scope === 'inbox' && list.status === 'idle') void load('inbox')
    // The inbox loads once when the picker mounts; a repository listing is explicit.
  }, [])

  const start = async (): Promise<void> => {
    /* v8 ignore next -- Start renders only with a selection */
    if (selected === null) return
    setStarting(true)
    setStartError(null)
    const outcome = await onStart(selected.ref, focus)
    if (!outcome.ok) setStartError(outcome.message)
    setStarting(false)
  }

  return (
    <div className={css.picker}>
      <SectionTitle title={t('review.pick')} accessory={(
        <button type="button" className={css.iconButton} title={t('panel.refresh')} aria-label={t('panel.refresh')} onClick={() => { void load() }}>
          <IconRefreshOutline14 />
        </button>
      )} />
      <div className={css.segments} role="tablist">
        <button type="button" role="tab" aria-selected={scope === 'inbox'} className={css.segment} data-active={scope === 'inbox' || undefined} onClick={() => { setScope('inbox'); void load('inbox') }}>{t('review.inbox')}</button>
        <button type="button" role="tab" aria-selected={scope === 'repo'} className={css.segment} data-active={scope === 'repo' || undefined} onClick={() => { setScope('repo'); setList({ status: 'idle' }) }}>{t('review.repo')}</button>
      </div>
      {scope === 'repo' ? (
        <div className={css.repoForm}>
          <Input value={project} placeholder={t('review.project')} aria-label={t('review.project')} onChange={(event) => { setProject(event.target.value) }} />
          <Input value={repo} placeholder={t('review.repoSlug')} aria-label={t('review.repoSlug')} onChange={(event) => { setRepo(event.target.value) }} />
          <Button size="sm" variant="outline" disabled={project.trim() === '' || repo.trim() === ''} onClick={() => { void load('repo') }}>{t('review.load')}</Button>
        </div>
      ) : null}
      {list.status === 'loading' ? <div className={css.muted}>{t('review.loading')}</div> : null}
      {list.status === 'error' ? <div className={css.errorBox}>{list.message}</div> : null}
      {list.status === 'ready' && list.items.length === 0 ? <div className={css.muted}>{t('review.noPrs')}</div> : null}
      {list.status === 'ready' && list.items.length > 0 ? (
        <ul className={css.prList}>
          {list.items.map(item => (
            <li key={item.key} className={css.prRow} data-selected={selected?.key === item.key || undefined}>
              <button type="button" className={css.prSelect} onClick={() => { setSelected(item) }} aria-pressed={selected?.key === item.key}>
                <Avatar person={item.author} size={24} />
                <span className={css.prMain}>
                  <span className={css.prTitle}>{item.title}</span>
                  <span className={css.prMeta}>
                    <span className={css.prKey}>{item.key}</span>
                    <span>· {item.author.name}</span>
                    {item.updated !== undefined ? <span>· {ago(t, item.updated, now)}</span> : null}
                  </span>
                </span>
                <span className={css.prBadges}>
                  {item.role !== undefined ? <Chip tone={item.role === 'REVIEWER' ? 'info' : 'neutral'}>{t(`review.role.${item.role}`)}</Chip> : null}
                  <Chip tone={item.approvals > 0 ? 'success' : 'neutral'}>{`${String(item.approvals)}/${String(item.reviewers)}`}</Chip>
                </span>
              </button>
              <button type="button" className={css.textButton} onClick={() => { onOpenPr(item.ref) }}>{t('card.open')}</button>
            </li>
          ))}
        </ul>
      ) : null}
      {selected !== null ? (
        <div className={css.startBox}>
          <div className={css.startTitle}>{selected.key} · {selected.title}</div>
          <label className={css.focusLabel}>
            {t('review.focus')}
            <textarea className={css.focusInput} rows={2} value={focus} placeholder={t('review.focusPlaceholder')} onChange={(event) => { setFocus(event.target.value) }} />
          </label>
          <div className={css.startActions}>
            <Button size="sm" variant="primary" disabled={starting} onClick={() => { void start() }}>{starting ? t('review.starting') : t('review.start')}</Button>
            {startError !== null ? <span className={css.error}>{startError}</span> : null}
          </div>
        </div>
      ) : null}
    </div>
  )
}

const FILTERS: readonly ReviewFilter[] = ['all', 'pending', 'posted']

/**
 * The review run: header, histogram, filters, findings.
 * @param props - the review and its verbs.
 * @returns the run view.
 */
function Run({
  review, pr, filter, now, t, onFilter, onCancel, onPost, onDismiss, onDiffContext, onOpenPr,
}: Omit<ReviewTabProps, 'onList' | 'onStart' | 'defaultProject'> & { review: ReviewRecord }) {
  const [bulk, setBulk] = useState(false)
  const findings = useMemo(() => sortFindings(review.findings), [review.findings])
  const pending = findings.filter(finding => finding.posted === undefined && finding.dismissed !== true)
  const posted = findings.filter(finding => finding.posted !== undefined)
  const shown = filter === 'pending' ? pending : filter === 'posted' ? posted : findings
  const counts = { critical: 0, major: 0, minor: 0, nit: 0 }
  for (const finding of findings) counts[finding.severity] += 1
  const running = review.status === 'running'
  const postAll = async (): Promise<void> => {
    setBulk(true)
    for (const finding of pending) await onPost(review.id, finding.id, undefined)
    setBulk(false)
  }
  return (
    <div className={css.run}>
      <div className={css.runHead}>
        <StateDot state={running ? 'ongoing' : review.status === 'complete' ? 'done' : 'warning'} size={12} />
        <div className={css.runTitle}>
          <span className={css.runStatus}>{running ? t('review.running') : review.status === 'complete' ? t('review.complete') : t('review.cancelled')}</span>
          <button type="button" className={css.runPr} onClick={() => { onOpenPr(review.pr) }}>
            <span className={css.prKey}>{review.prKey}</span>
            {pr !== undefined ? <span className={css.runPrTitle}>{pr.title}</span> : null}
          </button>
        </div>
        <span className={css.dim}>{ago(t, review.startedAt, now)}</span>
        {running ? <Button size="sm" variant="ghost" onClick={() => { void onCancel(review.id) }}>{t('review.cancel')}</Button> : null}
      </div>

      {review.verdict !== undefined ? (
        <div className={css.verdict} data-verdict={review.verdict}>
          <Chip tone={review.verdict === 'approve' ? 'success' : review.verdict === 'request-changes' ? 'error' : 'info'} dot>{t(`review.verdict.${review.verdict}`)}</Chip>
          {review.summary !== undefined ? <div className={css.summary}>{review.summary}</div> : null}
        </div>
      ) : null}

      <div className={css.histogram} aria-label={t('review.findings', { count: String(findings.length) })}>
        {(['critical', 'major', 'minor', 'nit'] as const).map(severity => (
          <span key={severity} className={css.histoCell} data-tone={severityTone(severity)} data-empty={counts[severity] === 0 || undefined} title={t(`severity.${severity}`)}>
            <span className={css.histoCount}>{counts[severity]}</span>
            <span className={css.histoLabel}>{t(`severity.${severity}`)}</span>
          </span>
        ))}
      </div>

      <div className={css.toolbar}>
        <div className={css.segments} role="tablist">
          {FILTERS.map(candidate => (
            <button key={candidate} type="button" role="tab" aria-selected={filter === candidate} className={css.segment} data-active={filter === candidate || undefined} onClick={() => { onFilter(candidate) }}>
              {t(`review.filter.${candidate}`)}
              <span className={css.segmentCount}>{candidate === 'all' ? findings.length : candidate === 'pending' ? pending.length : posted.length}</span>
            </button>
          ))}
        </div>
        <span className={css.spacer} />
        {pending.length > 1 ? (
          <Button size="sm" variant="outline" disabled={bulk} onClick={() => { void postAll() }}>{bulk ? t('review.posting') : t('review.postAll')}</Button>
        ) : null}
      </div>

      {review.existing.length > 0 ? (
        <details className={css.existing}>
          <summary className={css.existingSummary}>
            <span>{t('review.existing')}</span>
            <span className={css.segmentCount}>{review.existing.length}</span>
            <span className={css.existingHint}>{t('review.existingHint')}</span>
          </summary>
          <ExistingCommentList comments={review.existing} t={t} />
        </details>
      ) : null}

      {shown.length === 0
        ? <div className={css.muted}>{running && findings.length === 0 ? t('review.noFindingsYet') : t('review.noFindings')}</div>
        : (
          <ul className={css.findings}>
            {shown.map(finding => (
              <FindingCard
                key={finding.id}
                finding={finding}
                pr={review.pr}
                existing={review.existing}
                t={t}
                onPost={(findingId, comment) => onPost(review.id, findingId, comment)}
                onDismiss={findingId => onDismiss(review.id, findingId)}
                onDiffContext={candidate => onDiffContext(review.pr, candidate)}
              />
            ))}
          </ul>
        )}
    </div>
  )
}

/**
 * Render the review tab: the run when one exists (with a link to review
 * another PR), the picker otherwise.
 * @param props - review facts and verbs.
 * @returns the tab.
 */
export function ReviewTab(props: ReviewTabProps) {
  const [pickAnother, setPickAnother] = useState(false)
  const showPicker = props.review === undefined || pickAnother
  useEffect(() => {
    // A newly started review replaces the picker.
    if (props.review?.status === 'running') setPickAnother(false)
  }, [props.review?.id, props.review?.status])
  return (
    <div className={css.tab}>
      {props.review !== undefined && !showPicker ? <Run {...props} review={props.review} /> : null}
      {showPicker ? (
        <Picker
          t={props.t}
          defaultProject={props.defaultProject}
          now={props.now}
          onList={props.onList}
          onStart={props.onStart}
          onOpenPr={props.onOpenPr}
        />
      ) : null}
      {props.review !== undefined && !showPicker && props.review.status !== 'running' ? (
        <div className={css.another}>
          <Button size="sm" variant="ghost" onClick={() => { setPickAnother(true) }}>{props.t('review.another')}</Button>
        </div>
      ) : null}
    </div>
  )
}
