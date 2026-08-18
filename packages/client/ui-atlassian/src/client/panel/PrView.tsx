/**
 * Bitbucket pull request view for the panel: identity, state, branches,
 * author, reviewers with approval state, description, agent-routed actions,
 * and the entry point into review mode.
 */
import { Button, IconRefreshOutline14, IconRightUpOutline14, MarkdownText, StateDot } from '@cortex/client-ui-primitives'
import type { PrRecord, ReviewRecord } from '@cortex/atlassian/client'
import type { TranslateNS } from '@cortex/client-ui-slots'
import { prStateTone } from '../format.ts'
import type { NS } from '../locales.ts'
import { Avatar, Chip, InlineComposer, Meta, Person, SectionTitle } from '../atoms.tsx'
import { ago } from './IssueView.tsx'
import css from './entity.module.css'

/** Props of the pull request view. */
export interface PrViewProps {
  pr: PrRecord
  /** Latest review of this PR in the session, when any. */
  review: ReviewRecord | undefined
  now: number
  t: TranslateNS<typeof NS>
  onRefresh: () => void
  onPrompt: (text: string) => Promise<unknown>
  onReview: () => void
  onShowReview: () => void
}

/**
 * Render one pull request.
 * @param props - the PR and its verbs.
 * @returns the view.
 */
export function PrView({ pr, review, now, t, onRefresh, onPrompt, onReview, onShowReview }: PrViewProps) {
  const approvals = pr.reviewers.filter(reviewer => reviewer.status === 'APPROVED').length
  const running = review?.status === 'running'
  return (
    <article className={css.entity} data-kind="pr">
      <header className={css.head}>
        <div className={css.eyebrow}>
          <span className={css.key}>{pr.key}</span>
          <span className={css.spacer} />
          {pr.updated !== undefined ? <span className={css.dim}>{ago(t, pr.updated, now)}</span> : null}
          <button type="button" className={css.iconButton} title={t('panel.refresh')} aria-label={t('panel.refresh')} onClick={onRefresh}>
            <IconRefreshOutline14 />
          </button>
          <a className={css.iconButton} href={pr.url} target="_blank" rel="noreferrer" title={t('panel.openIn', { service: 'Bitbucket' })} aria-label={t('panel.openIn', { service: 'Bitbucket' })}>
            <IconRightUpOutline14 />
          </a>
        </div>
        <h2 className={css.title}>{pr.title}</h2>
        <div className={css.chips}>
          <Chip tone={prStateTone(pr.state)} dot>{t(`pr.state.${pr.state}`)}</Chip>
          <Chip tone={approvals > 0 ? 'success' : 'neutral'}>{`${String(approvals)}/${String(pr.reviewers.filter(reviewer => reviewer.role === 'REVIEWER').length)} ${t('pr.approved').toLowerCase()}`}</Chip>
        </div>
        <div className={css.branches}>
          <span className={css.branch} title={pr.from.branch}>{pr.from.branch}</span>
          <span className={css.arrow}>→</span>
          <span className={css.branch} title={pr.to.branch}>{pr.to.branch}</span>
        </div>
      </header>

      <div className={css.metaGrid}>
        <Meta label="Author"><Person person={pr.author} fallback="" /></Meta>
        <Meta label={t('pr.reviewers')}>{pr.reviewers.length === 0 ? <span className={css.dim}>{t('pr.noReviewers')}</span> : `${String(approvals)} / ${String(pr.reviewers.length)}`}</Meta>
      </div>
      {pr.reviewers.length > 0 ? (
        <ul className={css.reviewers}>
          {pr.reviewers.map(reviewer => (
            <li key={`${reviewer.role}:${reviewer.user.id ?? reviewer.user.name}`} className={css.reviewer}>
              <Avatar person={reviewer.user} size={20} />
              <span className={css.reviewerName}>{reviewer.user.name}</span>
              <StateDot state={reviewer.status === 'APPROVED' ? 'done' : reviewer.status === 'NEEDS_WORK' ? 'error' : 'warning'} size={8} />
              <span className={css.dim}>{reviewer.status === 'APPROVED' ? t('pr.approved') : reviewer.status === 'NEEDS_WORK' ? t('pr.needsWork') : t('pr.unapproved')}</span>
            </li>
          ))}
        </ul>
      ) : null}

      <div className={css.actions}>
        <Button size="sm" variant="primary" disabled={running || pr.state !== 'OPEN'} onClick={onReview}>
          {running ? t('pr.reviewing') : t('pr.review')}
        </Button>
        <Button size="sm" variant="outline" disabled={pr.state !== 'OPEN'} onClick={() => { void onPrompt(t('pr.approvePrompt', { key: pr.key })) }}>{t('pr.approve')}</Button>
        <Button size="sm" variant="outline" disabled={pr.state !== 'OPEN'} onClick={() => { void onPrompt(t('pr.mergePrompt', { key: pr.key })) }}>{t('pr.merge')}</Button>
        <InlineComposer
          label={t('pr.comment')}
          prefix={t('pr.commentPrompt', { key: pr.key })}
          onSend={onPrompt}
          sendLabel={t('pr.comment').replace('…', '')}
        />
      </div>

      {review !== undefined ? (
        <button type="button" className={css.reviewCallout} onClick={onShowReview}>
          <StateDot state={running ? 'ongoing' : review.status === 'complete' ? 'done' : 'warning'} size={10} />
          <span>{running ? t('review.running') : review.status === 'complete' ? t('review.complete') : t('review.cancelled')}</span>
          <span className={css.dim}>· {t('review.findings', { count: String(review.findings.length) })}</span>
        </button>
      ) : null}

      {pr.description !== '' ? (
        <section>
          <SectionTitle title={t('pr.description')} />
          <div className={css.body}>
            <MarkdownText text={pr.description} />
          </div>
        </section>
      ) : null}
    </article>
  )
}
