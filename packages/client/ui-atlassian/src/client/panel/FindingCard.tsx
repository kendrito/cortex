/**
 * One review finding: severity/category, file:line, the proposed comment
 * (editable before posting), the evidence with lazily loaded diff context, the
 * rationale, and the post/dismiss verbs with their outcome.
 */
import { useState } from 'react'
import clsx from 'clsx'
import { Button, CodeBlock, IconChevronDownOutline14, IconRightUpOutline14, MarkdownText } from '@cortex/client-ui-primitives'
import type {
  DiffContextLine, DiffContextResult, ExistingPrComment, PostFindingResult, PrRef, ReviewFinding,
} from '@cortex/atlassian/client'
import type { TranslateNS } from '@cortex/client-ui-slots'
import { disclosure } from '../disclosure.ts'
import { basename, severityTone, shortenPath } from '../format.ts'
import type { NS } from '../locales.ts'
import { Chip } from '../atoms.tsx'
import { ExistingCommentList } from './ExistingComments.tsx'
import css from './review.module.css'

/** Props of one finding card. */
export interface FindingCardProps {
  finding: ReviewFinding
  pr: PrRef
  /** Comments already on the pull request when the review started (for overlap display). */
  existing: readonly ExistingPrComment[]
  t: TranslateNS<typeof NS>
  onPost: (findingId: string, comment: string | undefined) => Promise<PostFindingResult>
  onDismiss: (findingId: string) => Promise<unknown>
  onDiffContext: (finding: ReviewFinding) => Promise<DiffContextResult>
}

/** Language hint from a file extension for the evidence block. */
function langOf(file: string): string {
  /* v8 ignore next -- split() always yields at least one segment, so the fallback never runs */
  const ext = file.split('.').pop()?.toLowerCase() ?? ''
  const known: Record<string, string> = {
    ts: 'typescript', tsx: 'tsx', js: 'javascript', jsx: 'jsx', py: 'python', java: 'java', kt: 'kotlin', go: 'go',
    rs: 'rust', rb: 'ruby', php: 'php', cs: 'csharp', cpp: 'cpp', c: 'c', h: 'c', swift: 'swift', sh: 'bash',
    yml: 'yaml', yaml: 'yaml', json: 'json', sql: 'sql', html: 'html', css: 'css', md: 'markdown', xml: 'xml', tf: 'hcl',
  }
  return known[ext] ?? ''
}

/** Shortened directory part of a path (empty for a bare file name), with its trailing slash. */
function dirname(file: string): string {
  const short = shortenPath(file)
  const index = short.lastIndexOf('/')
  return index === -1 ? '' : short.slice(0, index + 1)
}

/**
 * Render diff context lines with the anchored line highlighted.
 * @param props.lines - the window.
 * @returns the block.
 */
function DiffContext({ lines }: { lines: DiffContextLine[] }) {
  return (
    <pre className={css.diff}>
      {lines.map((line, index) => (
        <div key={index} className={clsx(css.diffLine, line.anchor === true && css.diffAnchor)} data-type={line.type}>
          <span className={css.diffNo}>{line.source ?? ''}</span>
          <span className={css.diffNo}>{line.destination ?? ''}</span>
          <span className={css.diffSign}>{line.type === 'ADDED' ? '+' : line.type === 'REMOVED' ? '−' : ' '}</span>
          <span className={css.diffText}>{line.text}</span>
        </div>
      ))}
    </pre>
  )
}

/**
 * Render one finding.
 * @param props - the finding and its verbs.
 * @returns the card.
 */
export function FindingCard({ finding, pr, existing, t, onPost, onDismiss, onDiffContext }: FindingCardProps) {
  const overlapping = (finding.overlaps ?? [])
    .map(id => existing.find(comment => comment.id === id))
    .filter((comment): comment is ExistingPrComment => comment !== undefined)
  const [expanded, setExpanded] = useState(finding.posted === undefined && finding.dismissed !== true)
  const [editing, setEditing] = useState(false)
  const [comment, setComment] = useState(finding.comment)
  const [busy, setBusy] = useState<'post' | 'dismiss' | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [context, setContext] = useState<
    { status: 'idle' } | { status: 'loading' } | { status: 'ready'; value: DiffContextResult }
  >({ status: 'idle' })
  const settled = finding.posted !== undefined || finding.dismissed === true

  const post = async (): Promise<void> => {
    setBusy('post')
    setError(null)
    const result = await onPost(finding.id, editing || comment !== finding.comment ? comment : undefined)
    if (!result.ok) setError(result.message)
    setBusy(null)
  }
  const dismiss = async (): Promise<void> => {
    setBusy('dismiss')
    await onDismiss(finding.id)
    setBusy(null)
  }
  const loadContext = async (): Promise<void> => {
    /* v8 ignore next -- the trigger is disabled once loading started */
    if (context.status !== 'idle') return
    setContext({ status: 'loading' })
    setContext({ status: 'ready', value: await onDiffContext(finding) })
  }

  return (
    <li
      className={css.finding}
      data-severity={finding.severity}
      data-settled={settled || undefined}
      data-dismissed={finding.dismissed === true || undefined}
    >
      <div className={css.findingHead} {...disclosure(expanded, () => { setExpanded(value => !value) })}>
        <span className={css.severityBar} data-tone={severityTone(finding.severity)} aria-hidden />
        <div className={css.findingSummary}>
          <div className={css.findingChips}>
            <Chip tone={severityTone(finding.severity)} dot>{t(`severity.${finding.severity}`)}</Chip>
            <Chip>{t(`category.${finding.category}`)}</Chip>
            {overlapping.length > 0 ? <Chip tone="warn" title={t('finding.overlapsHint')}>{t('finding.overlaps')}</Chip> : null}
            <span className={css.location} title={`${finding.file}:${String(finding.line)}`}>
              <span className={css.locationDir}>{dirname(finding.file)}</span>
              <span className={css.locationFile}>{basename(finding.file)}</span>
              <span className={css.locationLine}>:{finding.line}</span>
            </span>
          </div>
          <div className={css.findingTitle}>{finding.title}</div>
        </div>
        <span className={css.findingState}>
          {finding.posted !== undefined ? <Chip tone="success" dot>{t('review.posted')}</Chip>
            : finding.dismissed === true ? <Chip>{t('review.dismissed')}</Chip> : null}
          <IconChevronDownOutline14 className={clsx(css.chevron, expanded && css.chevronOpen)} />
        </span>
      </div>
      {expanded ? (
        <div className={css.findingBody}>
          <div className={css.block}>
            <div className={css.blockLabel}>
              <span>{t('finding.comment')}</span>
              {!settled ? (
                <button type="button" className={css.textButton} onClick={() => { setEditing(value => !value) }}>
                  {editing ? t('finding.done') : t('finding.edit')}
                </button>
              ) : null}
            </div>
            {editing
              ? <textarea className={css.editor} value={comment} rows={5} onChange={(event) => { setComment(event.target.value) }} />
              : <div className={css.commentPreview}><MarkdownText text={comment} /></div>}
          </div>

          <div className={css.block}>
            <div className={css.blockLabel}>
              <span>{t('finding.evidence')}</span>
              <button type="button" className={css.textButton} onClick={() => { void loadContext() }} disabled={context.status !== 'idle'}>
                {context.status === 'loading' ? t('finding.diffLoading') : t('finding.diff')}
              </button>
            </div>
            {context.status === 'ready' && context.value.ok && context.value.lines.length > 0
              ? (
                <>
                  <DiffContext lines={context.value.lines} />
                  {!context.value.found ? <div className={css.note}>{t('finding.diffMissing')}</div> : null}
                </>
              )
              : <CodeBlock code={finding.evidence} lang={langOf(finding.file)} />}
            {context.status === 'ready' && !context.value.ok
              ? <div className={css.note}>{t('finding.diffError', { message: context.value.message })}</div>
              : null}
            {context.status === 'ready' && context.value.ok && context.value.lines.length === 0
              ? <div className={css.note}>{t('finding.diffMissing')}</div>
              : null}
          </div>

          <div className={css.block}>
            <div className={css.blockLabel}><span>{t('finding.rationale')}</span></div>
            <div className={css.rationale}>{finding.rationale}</div>
          </div>

          {overlapping.length > 0 ? (
            <div className={css.block}>
              <div className={css.blockLabel}><span>{t('finding.overlaps')}</span></div>
              <ExistingCommentList comments={overlapping} t={t} />
            </div>
          ) : null}

          {finding.suggestion !== undefined ? (
            <div className={css.block}>
              <div className={css.blockLabel}><span>{t('finding.suggestion')}</span></div>
              <CodeBlock code={finding.suggestion} lang={langOf(finding.file)} />
            </div>
          ) : null}

          <div className={css.findingActions}>
            {finding.posted !== undefined ? (
              <>
                <span className={css.postedNote}>{finding.posted.mode === 'inline' ? t('review.postedInline') : t('review.postedGeneral')}</span>
                {finding.posted.url !== undefined ? (
                  <a className={css.postedLink} href={finding.posted.url} target="_blank" rel="noreferrer">
                    {t('review.viewComment')} <IconRightUpOutline14 />
                  </a>
                ) : null}
              </>
            ) : finding.dismissed === true ? (
              <span className={css.postedNote}>{t('review.dismissed')}</span>
            ) : (
              <>
                <Button size="sm" variant="primary" disabled={busy !== null || comment.trim() === ''} onClick={() => { void post() }}>
                  {busy === 'post' ? t('review.posting') : t('finding.post')}
                </Button>
                <Button size="sm" variant="ghost" disabled={busy !== null} onClick={() => { void dismiss() }}>{t('finding.dismiss')}</Button>
                <span className={css.prHint}>{`${pr.repo}#${String(pr.id)}`}</span>
              </>
            )}
            {error !== null ? <span className={css.error}>{t('review.error', { message: error })}</span> : null}
          </div>
        </div>
      ) : null}
    </li>
  )
}
