/**
 * The keyed tool card for every Atlassian MCP tool call in the chat: a compact
 * accent row (service glyph, verb, target) and an expandable body that shows
 * the entity the projection tracks for the call, the captured search rows, a
 * native diff, file content, or the raw result. Replay-stable: everything is
 * a pure function of the frozen call block and the `atlassian` projection.
 */
import { useState, type ReactNode } from 'react'
import clsx from 'clsx'
import {
  DiffBlock, IconChevronDownOutline14, IconRightUpOutline16, JsonTree, CodeBlock, MarkdownText, StateDot,
} from '@cortex/client-ui-primitives'
import type { AtlassianProjection, EntityRef, IssueRecord, PageRecord, PrRecord, SearchRecord } from '@cortex/atlassian/client'
import type { AtlassianCardProps } from '../contract.ts'
import { callFacts, serviceLabel, toolTitle, type CallFacts } from '../call.ts'
import { disclosure } from '../disclosure.ts'
import { summarizeBitbucketDiff } from '../diff.ts'
import { prStateTone, statusTone } from '../format.ts'
import { Avatar, Chip, ServiceGlyph } from '../atoms.tsx'
import css from './card.module.css'

/** Longest summary shown on the collapsed row. */
const SUMMARY_LIMIT = 96

function clip(text: string, limit = SUMMARY_LIMIT): string {
  const flat = text.replace(/\s+/g, ' ').trim()
  return flat.length > limit ? `${flat.slice(0, limit - 1)}…` : flat
}

function dict(value: unknown): Record<string, unknown> | undefined {
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? value as Record<string, unknown> : undefined
}

function str(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() !== '' ? value : undefined
}

/** The tracked record the call addresses, when the projection holds it. */
function recordOf(
  projection: AtlassianProjection | undefined, entity: EntityRef | undefined,
): IssueRecord | PageRecord | PrRecord | undefined {
  if (projection === undefined || entity === undefined) return undefined
  switch (entity.kind) {
    case 'issue': return projection.issues[entity.key]
    case 'page': return projection.pages[entity.id]
    case 'pr': return projection.prs[entity.key]
    /* v8 ignore next 2 -- closed union backstop */
    default: return undefined
  }
}

/** Panel address of one tracked record. */
function refOfRecord(record: IssueRecord | PageRecord | PrRecord): EntityRef {
  if (record.kind === 'issue') return { kind: 'issue', key: record.key }
  if (record.kind === 'page') return { kind: 'page', id: record.id }
  return { kind: 'pr', key: record.key }
}

/** Collapsed-row summary text for one call. */
function summaryOf(facts: CallFacts, record: IssueRecord | PageRecord | PrRecord | undefined): string {
  const { raw, args, json } = facts
  const result = dict(json)
  if (raw === 'jira_search') return clip(str(args.jql) ?? '')
  if (raw === 'confluence_search') return clip(str(args.query) ?? '')
  if (raw === 'jira_transition_issue') {
    const to = str(dict(dict(result?.issue)?.status)?.name)
    return `${facts.issueKey ?? ''}${to === undefined ? '' : ` → ${to}`}`
  }
  if (raw === 'jira_add_comment') return `${facts.issueKey ?? ''} · ${clip(str(args.comment) ?? '', 60)}`
  if (raw === 'jira_create_issue') {
    const key = str(dict(result?.issue)?.key)
    return `${key === undefined ? '' : `${key} · `}${clip(str(args.summary) ?? '', 60)}`
  }
  if (raw === 'confluence_create_page') return clip(str(args.title) ?? '', 70)
  if (raw === 'bitbucket_create_pull_request') return clip(str(args.title) ?? '', 70)
  if (raw === 'bitbucket_get_file_content') return clip(str(args.filePath) ?? '', 70)
  if (raw === 'bitbucket_browse_directory') return clip(str(args.path) ?? str(args.directoryPath) ?? '/', 70)
  if (raw === 'bitbucket_list_repositories') return clip(str(args.workspaceSlug) ?? str(args.projectKey) ?? '', 70)
  if (record !== undefined) {
    if (record.kind === 'issue') return `${record.key} · ${clip(record.summary, 64)}`
    if (record.kind === 'page') return clip(record.title, 70)
    return `${record.key} · ${clip(record.title, 60)}`
  }
  if (facts.issueKey !== undefined) return facts.issueKey
  if (facts.pr !== undefined) return `${facts.pr.repo}#${String(facts.pr.id)}`
  const pageId = str(args.page_id)
  if (pageId !== undefined) return `page ${pageId}`
  return ''
}

/**
 * Compact strip of the tracked entity inside an expanded card.
 * @param props.record - issue, page, or PR record.
 * @param props.onOpen - focus it in the panel.
 * @param props.label - the open button label.
 * @returns the strip.
 */
function EntityStrip({ record, onOpen, label }: { record: IssueRecord | PageRecord | PrRecord; onOpen: () => void; label: string }) {
  if (record.kind === 'issue') {
    return (
      <div className={css.strip}>
        <span className={css.stripKey}>{record.key}</span>
        <span className={css.stripTitle}>{record.summary}</span>
        <Chip tone={statusTone(record.status)} dot>{record.status.name}</Chip>
        {record.assignee !== undefined ? <Avatar person={record.assignee} size={18} /> : null}
        <button type="button" className={css.openButton} onClick={onOpen} title={label} aria-label={label}><IconRightUpOutline16 /></button>
      </div>
    )
  }
  if (record.kind === 'page') {
    return (
      <div className={css.strip}>
        <span className={css.stripKey}>{record.space.key}</span>
        <span className={css.stripTitle}>{record.title}</span>
        <Chip mono>v{record.version}</Chip>
        <button type="button" className={css.openButton} onClick={onOpen} title={label} aria-label={label}><IconRightUpOutline16 /></button>
      </div>
    )
  }
  const approvals = record.reviewers.filter(reviewer => reviewer.status === 'APPROVED').length
  return (
    <div className={css.strip}>
      <span className={css.stripKey}>{record.key}</span>
      <span className={css.stripTitle}>{record.title}</span>
      <Chip tone={prStateTone(record.state)} dot>{record.state.toLowerCase()}</Chip>
      <Chip>{`${String(approvals)}/${String(record.reviewers.length)} ✓`}</Chip>
      <button type="button" className={css.openButton} onClick={onOpen} title={label} aria-label={label}><IconRightUpOutline16 /></button>
    </div>
  )
}

/**
 * Search rows table.
 * @param props.search - captured rows.
 * @param props.onOpenIssue - focus one issue.
 * @param props.t - translate seat.
 * @returns the table.
 */
function SearchTable({ search, onOpenIssue, t }: {
  search: SearchRecord
  onOpenIssue: (key: string) => void
  t: AtlassianCardProps['t']
}) {
  if (search.rows.length === 0) return <div className={css.muted}>{t('card.noResults')}</div>
  if (search.service === 'jira') {
    return (
      <div className={css.tableWrap}>
        <table className={css.table}>
          <tbody>
            {search.rows.map(row => (
              <tr key={row.key}>
                <td className={css.cellKey}><button type="button" className={css.keyButton} onClick={() => { onOpenIssue(row.key) }}>{row.key}</button></td>
                <td className={css.cellTitle} title={row.summary}>{row.summary}</td>
                <td className={css.cellChip}>
                  {row.status !== undefined ? <Chip tone={statusTone(row.status)}>{row.status.name}</Chip> : null}
                </td>
                <td className={css.cellDim}>{row.assignee ?? ''}</td>
              </tr>
            ))}
          </tbody>
        </table>
        <div className={css.tableFoot}>{t('card.results', { count: String(search.rows.length), total: String(search.total) })}</div>
      </div>
    )
  }
  return (
    <div className={css.tableWrap}>
      <table className={css.table}>
        <tbody>
          {search.rows.map(row => (
            <tr key={row.id}>
              <td className={css.cellKey}>{row.space ?? ''}</td>
              <td className={css.cellTitle} title={row.title}>
                {row.url !== undefined ? <a href={row.url} target="_blank" rel="noreferrer" className={css.keyButton}>{row.title}</a> : row.title}
                {row.excerpt !== undefined ? <div className={css.excerpt}>{row.excerpt}</div> : null}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <div className={css.tableFoot}>{t('card.results', { count: String(search.rows.length), total: String(search.total) })}</div>
    </div>
  )
}

/** Language hint from a path. */
function langOf(path: string | undefined): string {
  const ext = path?.split('.').pop()?.toLowerCase() ?? ''
  const known: Record<string, string> = {
    ts: 'typescript', tsx: 'tsx', js: 'javascript', jsx: 'jsx', py: 'python', java: 'java', kt: 'kotlin', go: 'go',
    rs: 'rust', rb: 'ruby', php: 'php', cs: 'csharp', swift: 'swift', sh: 'bash', yml: 'yaml', yaml: 'yaml', json: 'json',
    sql: 'sql', html: 'html', css: 'css', md: 'markdown', xml: 'xml',
  }
  return known[ext] ?? ''
}

/**
 * Render one Atlassian tool call.
 * @param props - keyed toolview props with the panel store, the card face, and copy.
 * @returns the card.
 */
export function AtlassianCard(props: AtlassianCardProps) {
  const { toolName, block, useProjection, actions, open, t, inspect } = props
  const facts = callFacts(toolName, block)
  const projection = useProjection('atlassian')
  const record = recordOf(projection, facts.entity)
  const search = facts.state === 'ok' ? projection?.searches.find(item => item.callId === block.callId) : undefined
  const [expanded, setExpanded] = useState(false)
  const service = facts.server === 'bitbucket' ? 'bitbucket' : facts.raw.startsWith('confluence_') ? 'confluence' : 'jira'
  const summary = summaryOf(facts, record)
  const expandable = facts.state !== 'running'
  const status = facts.state === 'running' ? t('card.running') : facts.state === 'error' ? t('card.failed') : facts.state === 'stopped' ? t('card.stopped') : null

  const openIssue = (key: string): void => {
    void open({ kind: 'issue', key }).then((result) => { if (result.ok) actions.showEntity(result.entity) })
  }

  const body = ((): ReactNode => {
    if (facts.state === 'error' || facts.state === 'stopped') {
      return <pre className={css.raw} data-error>{facts.text || block.callId}</pre>
    }
    const parts: ReactNode[] = []
    if (record !== undefined) {
      // The strip's own record is the panel target.
      const target = record
      parts.push(<EntityStrip key="entity" record={target} onOpen={() => { actions.showEntity(refOfRecord(target)) }} label={t('card.open')} />)
    }
    else if (facts.entity !== undefined) parts.push(<div key="pending" className={css.muted}>{t('card.pending')}</div>)
    if (search !== undefined) parts.push(<SearchTable key="search" search={search} onOpenIssue={openIssue} t={t} />)
    const comment = facts.raw === 'jira_add_comment' ? str(facts.args.comment) : undefined
    if (comment !== undefined) parts.push(<div key="comment" className={css.quote}><MarkdownText text={comment} /></div>)
    if (facts.raw === 'bitbucket_get_pull_request_diff') {
      const diff = summarizeBitbucketDiff(facts.json)
      if (diff !== undefined) {
        parts.push(
          <div key="diff" className={css.diffWrap}>
            <div className={css.diffHead}>
              <span>{t('card.diffFiles', { count: String(diff.files.length) })}</span>
              {diff.truncated ? <Chip tone="warn">{t('card.diffTruncated')}</Chip> : null}
            </div>
            <ul className={css.fileList}>
              {diff.files.map(file => (
                <li key={file.path} className={css.fileRow}>
                  <span className={css.filePath}>{file.path}</span>
                  {file.binary ? <Chip>{t('card.binary')}</Chip> : (
                    <span className={css.fileStats}>
                      <span className={css.added}>+{file.added}</span>
                      <span className={css.removed}>−{file.removed}</span>
                    </span>
                  )}
                </li>
              ))}
            </ul>
            {diff.hunks.length > 0 ? <DiffBlock diffs={diff.hunks} maxLines={40} /> : null}
          </div>,
        )
      }
    }
    if (facts.raw === 'bitbucket_get_file_content' && facts.text !== '') {
      const content = str(dict(facts.json)?.content) ?? (facts.json === undefined ? facts.text : undefined)
      if (content !== undefined) parts.push(<CodeBlock key="file" code={content} lang={langOf(str(facts.args.filePath))} />)
    }
    if (parts.length === 0) {
      const data = facts.json
      if (typeof data === 'object' && data !== null) parts.push(<JsonTree key="json" data={data} label={t('card.output')} expandTopLevel />)
      else parts.push(<pre key="raw" className={css.raw}>{facts.text}</pre>)
    }
    return <div className={css.bodyStack}>{parts}</div>
  })

  const toggle = (): void => { setExpanded(value => !value) }

  return (
    <div className={css.card} data-tool={facts.raw} data-state={facts.state} data-service={service}>
      <div className={css.row} data-expandable={expandable || undefined} {...expandable ? disclosure(expanded, toggle) : {}}>
        <span className={css.leading}>
          {facts.state === 'running' ? <StateDot state="ongoing" size={10} />
            : facts.state === 'error' ? <StateDot state="error" />
              : facts.state === 'stopped' ? <StateDot state="warning" />
                : <ServiceGlyph service={service} size={16} />}
        </span>
        {status !== null ? <span className={css.visuallyHidden}>{status}</span> : null}
        <span className={css.title}>{serviceLabel(facts.raw)} · {toolTitle(facts.raw)}</span>
        {summary !== '' ? <span className={css.separator} aria-hidden /> : null}
        <span className={clsx(css.summary, facts.state === 'error' && css.summaryError)}>{facts.state === 'error' ? clip(facts.text, 80) || t('card.failed') : summary}</span>
        {record !== undefined ? <Chip className={css.rowChip} tone={record.kind === 'issue' ? statusTone(record.status) : record.kind === 'pr' ? prStateTone(record.state) : 'neutral'} dot={record.kind !== 'page'}>{record.kind === 'issue' ? record.status.name : record.kind === 'pr' ? record.state.toLowerCase() : `v${String(record.version)}`}</Chip> : null}
        {expandable ? <IconChevronDownOutline14 className={clsx(css.chevron, expanded && css.chevronOpen)} /> : null}
      </div>
      {expanded ? (
        <div className={css.body}>
          {body()}
          {inspect !== undefined ? (
            <button type="button" className={css.inspect} onClick={inspect}>{t('card.details')}</button>
          ) : null}
        </div>
      ) : null}
    </div>
  )
}
