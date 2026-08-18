/**
 * Keyed tool cards for this package's own review tools: one finding row
 * (severity, category, file:line, title, comment + evidence on expand) and
 * the review-complete row (verdict + summary). Both share the accent-row
 * shell; the body differs.
 */
import { useState, type ReactNode } from 'react'
import clsx from 'clsx'
import { CodeBlock, IconChevronDownOutline14, MarkdownText, StateDot } from '@cortex/client-ui-primitives'
import type { FindingCategory, FindingSeverity, ReviewVerdict } from '@cortex/atlassian/client'
import type { AtlassianCardProps } from '../contract.ts'
import { callFacts, type CallState } from '../call.ts'
import { disclosure } from '../disclosure.ts'
import { basename, severityTone } from '../format.ts'
import { Chip, ServiceGlyph } from '../atoms.tsx'
import css from './card.module.css'

const SEVERITIES = new Set<string>(['critical', 'major', 'minor', 'nit'])
const CATEGORIES = new Set<string>(['security', 'correctness', 'readability', 'performance', 'testing', 'style'])
const VERDICTS = new Set<string>(['approve', 'request-changes', 'comment'])

function str(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() !== '' ? value : undefined
}

/** Shared accent-row shell of the two review cards. */
function ReviewRow({ tool, state, warn, title, chips, summary, expandBody, actions, t }: {
  tool: string
  state: CallState
  /** Settled successfully but the tool refused (no running review). */
  warn: boolean
  title: string
  chips: ReactNode
  summary: ReactNode
  expandBody: ReactNode
  actions: AtlassianCardProps['actions']
  t: AtlassianCardProps['t']
}) {
  const [expanded, setExpanded] = useState(false)
  const onOpen = (): void => { actions.setTab('review'); actions.open() }
  const toggle = (): void => { setExpanded(value => !value) }
  return (
    <div className={css.card} data-tool={tool} data-state={state} data-service="review">
      <div className={css.row} data-expandable {...disclosure(expanded, toggle)}>
        <span className={css.leading}>
          {state === 'running' ? <StateDot state="ongoing" size={10} />
            : state === 'error' ? <StateDot state="error" />
              : warn ? <StateDot state="warning" /> : <ServiceGlyph service="review" size={16} />}
        </span>
        <span className={css.title}>{title}</span>
        <span className={css.separator} aria-hidden />
        {chips}
        <span className={css.summary}>{summary}</span>
        <IconChevronDownOutline14 className={clsx(css.chevron, expanded && css.chevronOpen)} />
      </div>
      {expanded ? (
        <div className={css.body}>
          <div className={css.bodyStack}>
            {expandBody}
            <button type="button" className={css.inspect} onClick={onOpen}>{t('card.open')}</button>
          </div>
        </div>
      ) : null}
    </div>
  )
}

/**
 * Render one `atlassian_review_finding` call.
 * @param props - keyed toolview props.
 * @returns the row.
 */
export function FindingRow(props: AtlassianCardProps) {
  const { toolName, block, actions, t } = props
  const facts = callFacts(toolName, block)
  const severity = str(facts.args.severity)
  const category = str(facts.args.category)
  const tone = severity !== undefined && SEVERITIES.has(severity) ? severityTone(severity as FindingSeverity) : 'neutral'
  const file = str(facts.args.file) ?? ''
  const line = typeof facts.args.line === 'number' ? facts.args.line : undefined
  const title = str(facts.args.title) ?? t('card.finding')
  const recorded = facts.state === 'ok' && (facts.json as { recorded?: boolean } | undefined)?.recorded === true
  const refused = facts.state === 'ok' && !recorded
  const comment = str(facts.args.comment)
  const evidence = str(facts.args.evidence)
  const rationale = str(facts.args.rationale)
  return (
    <ReviewRow
      tool="atlassian_review_finding"
      state={facts.state}
      warn={refused}
      title={t('card.finding')}
      chips={(
        <>
          {severity !== undefined ? <Chip className={css.rowChip} tone={tone} dot>{SEVERITIES.has(severity) ? t(`severity.${severity as FindingSeverity}`) : severity}</Chip> : null}
          {category !== undefined && CATEGORIES.has(category) ? <Chip className={css.rowChip}>{t(`category.${category as FindingCategory}`)}</Chip> : null}
        </>
      )}
      summary={(
        <>
          {file !== '' ? <span className={css.mono}>{basename(file)}{line !== undefined ? `:${String(line)}` : ''}</span> : null}
          {file !== '' ? ' — ' : ''}{title}
        </>
      )}
      expandBody={(
        <>
          {comment !== undefined ? <div className={css.quote}><MarkdownText text={comment} /></div> : null}
          {evidence !== undefined ? <CodeBlock code={evidence} /> : null}
          {rationale !== undefined ? <div className={css.muted}>{rationale}</div> : null}
          {refused || facts.state === 'error' ? <pre className={css.raw} data-error>{facts.text}</pre> : null}
        </>
      )}
      actions={actions}
      t={t}
    />
  )
}

/**
 * Render one `atlassian_review_complete` call.
 * @param props - keyed toolview props.
 * @returns the row.
 */
export function ReviewCompleteRow(props: AtlassianCardProps) {
  const { toolName, block, actions, t } = props
  const facts = callFacts(toolName, block)
  const verdict = str(facts.args.verdict)
  const summary = str(facts.args.summary) ?? ''
  const tone = verdict === 'approve' ? 'success' : verdict === 'request-changes' ? 'error' : 'info'
  return (
    <ReviewRow
      tool="atlassian_review_complete"
      state={facts.state}
      warn={false}
      title={t('card.reviewComplete')}
      chips={verdict !== undefined && VERDICTS.has(verdict) ? <Chip className={css.rowChip} tone={tone} dot>{t(`review.verdict.${verdict as ReviewVerdict}`)}</Chip> : null}
      summary={summary.replace(/\s+/g, ' ').slice(0, 96)}
      expandBody={(
        <>
          {summary !== '' ? <div className={css.quote}><MarkdownText text={summary} /></div> : null}
          {facts.state === 'error' ? <pre className={css.raw} data-error>{facts.text}</pre> : null}
        </>
      )}
      actions={actions}
      t={t}
    />
  )
}
