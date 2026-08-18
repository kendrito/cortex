/**
 * Rows for comments that were already on the pull request when a review
 * started: shared by the review run's "already on the pull request" list and
 * a finding's overlap section.
 */
import type { ExistingPrComment } from '@cortex/atlassian/client'
import type { TranslateNS } from '@cortex/client-ui-slots'
import type { NS } from '../locales.ts'
import css from './review.module.css'

/**
 * Where an existing comment sits: `file:line` for an inline comment, the
 * localized "general comment" copy otherwise.
 * @param comment - the comment.
 * @param t - translate seat.
 * @returns the location label.
 */
export function existingWhere(comment: ExistingPrComment, t: TranslateNS<typeof NS>): string {
  return comment.file === undefined || comment.line === undefined
    ? t('review.existingGeneral')
    : `${comment.file}:${String(comment.line)}`
}

/**
 * Render a list of existing comments.
 * @param props.comments - the comments.
 * @param props.t - translate seat.
 * @returns the list.
 */
export function ExistingCommentList({ comments, t }: { comments: readonly ExistingPrComment[]; t: TranslateNS<typeof NS> }) {
  return (
    <ul className={css.existingList}>
      {comments.map(comment => (
        <li key={comment.id} className={css.existingRow}>
          <span className={css.existingAuthor}>{comment.author.name}</span>
          <span className={css.existingWhere}>{existingWhere(comment, t)}</span>
          <span className={css.existingText}>{comment.text}</span>
          {comment.replies > 0 ? <span className={css.dim}>{t('review.existingReplies', { count: String(comment.replies) })}</span> : null}
        </li>
      ))}
    </ul>
  )
}
