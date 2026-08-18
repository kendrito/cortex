/**
 * Full Jira issue view for the panel: identity, status, people, planning
 * facts, agent-routed actions (transition / comment / assign), description,
 * comments, links, subtasks, and attachments.
 */
import { useState } from 'react'
import {
  Button, IconChevronDownOutline14, IconRefreshOutline14, IconRightUpOutline14, MarkdownText, Menu, type MenuEntry,
} from '@cortex/client-ui-primitives'
import type { IssueRecord } from '@cortex/atlassian/client'
import type { TranslateNS } from '@cortex/client-ui-slots'
import { formatBytes, relativeTime, statusTone } from '../format.ts'
import type { NS } from '../locales.ts'
import { Avatar, Chip, InlineComposer, Meta, Person, SectionTitle } from '../atoms.tsx'
import css from './entity.module.css'

/** Translate seat of this namespace. */
type T = TranslateNS<typeof NS>

/** Comments created within this window of the fetch read as fresh. */
const FRESH_MS = 3 * 60_000

/**
 * Localized relative time.
 * @param t - translate seat.
 * @param iso - timestamp.
 * @param now - current epoch ms.
 * @returns copy.
 */
export function ago(t: T, iso: string | number | undefined, now: number): string {
  const rel = relativeTime(iso, now)
  switch (rel.unit) {
    case 'now': return t('time.justNow')
    case 'minutes': return t('time.minutes', { n: String(rel.n) })
    case 'hours': return t('time.hours', { n: String(rel.n) })
    default: return t('time.days', { n: String(rel.n) })
  }
}

/** Props of the issue view. */
export interface IssueViewProps {
  issue: IssueRecord
  pinned: boolean
  now: number
  t: T
  onPin: (key: string | null) => void
  onRefresh: () => void
  onPrompt: (text: string) => Promise<unknown>
  onOpenIssue: (key: string) => void
}

/**
 * Render one issue.
 * @param props - the issue and its verbs.
 * @returns the view.
 */
export function IssueView({ issue, pinned, now, t, onPin, onRefresh, onPrompt, onOpenIssue }: IssueViewProps) {
  const [transitionsOpen, setTransitionsOpen] = useState(false)
  const [descriptionOpen, setDescriptionOpen] = useState(false)
  const transitionItems: MenuEntry[] = issue.transitions.map(transition => ({ id: transition.id, label: transition.to }))
  const longDescription = issue.description.length > 700
  return (
    <article className={css.entity} data-kind="issue">
      <header className={css.head}>
        <div className={css.eyebrow}>
          <span className={css.key}>{issue.key}</span>
          <span className={css.type}>{issue.type}</span>
          {issue.project !== undefined ? <span className={css.dim}>· {issue.project}</span> : null}
          <span className={css.spacer} />
          {pinned ? <Chip tone="accent" title={t('panel.pinned')}>{t('panel.pinned')}</Chip> : null}
          <button type="button" className={css.iconButton} title={t('panel.refresh')} aria-label={t('panel.refresh')} onClick={onRefresh}>
            <IconRefreshOutline14 />
          </button>
          <a className={css.iconButton} href={issue.url} target="_blank" rel="noreferrer" title={t('panel.openIn', { service: 'Jira' })} aria-label={t('panel.openIn', { service: 'Jira' })}>
            <IconRightUpOutline14 />
          </a>
        </div>
        <h2 className={css.title}>{issue.summary}</h2>
        <div className={css.chips}>
          <Chip tone={statusTone(issue.status)} dot>{issue.status.name}</Chip>
          {issue.priority !== undefined ? <Chip>{issue.priority}</Chip> : null}
          {issue.resolution !== undefined ? <Chip tone="success">{issue.resolution}</Chip> : null}
          {issue.labels.map(label => <Chip key={label} mono>{label}</Chip>)}
        </div>
      </header>

      <div className={css.metaGrid}>
        <Meta label={t('issue.assignee')}><Person person={issue.assignee} fallback={t('issue.unassigned')} /></Meta>
        <Meta label={t('issue.reporter')}>{issue.reporter !== undefined ? <Person person={issue.reporter} fallback="" /> : null}</Meta>
        <Meta label={t('issue.sprint')}>{issue.sprint}</Meta>
        <Meta label={t('issue.epic')}>{issue.epic !== undefined ? (
          <button type="button" className={css.linkButton} onClick={() => { /* v8 ignore next -- rendered only while epic is set */ onOpenIssue(issue.epic?.key ?? '') }}>
            {issue.epic.name ?? issue.epic.key}
          </button>
        ) : null}</Meta>
        <Meta label={t('issue.points')}>{issue.storyPoints !== undefined ? String(issue.storyPoints) : null}</Meta>
        <Meta label={t('issue.due')}>{issue.dueDate}</Meta>
        <Meta label={t('issue.components')}>{issue.components.length > 0 ? issue.components.map(name => <Chip key={name}>{name}</Chip>) : null}</Meta>
        <Meta label={t('issue.fixVersions')}>{issue.fixVersions.length > 0 ? issue.fixVersions.map(name => <Chip key={name}>{name}</Chip>) : null}</Meta>
      </div>

      <div className={css.actions}>
        {issue.transitions.length > 0 ? (
          <Menu
            open={transitionsOpen}
            items={transitionItems}
            onSelect={(id) => {
              setTransitionsOpen(false)
              const target = issue.transitions.find(transition => transition.id === id)
              /* v8 ignore next -- the menu offers only ids from the transitions list */
              if (target !== undefined) void onPrompt(t('issue.transitionPrompt', { key: issue.key, status: target.to }))
            }}
            onClose={() => { setTransitionsOpen(false) }}
            anchor={(
              <Button size="sm" variant="outline" onClick={() => { setTransitionsOpen(value => !value) }} icon={<IconChevronDownOutline14 />}>
                {t('issue.transitions')}
              </Button>
            )}
          />
        ) : null}
        <InlineComposer
          label={t('issue.comment')}
          prefix={t('issue.commentPrompt', { key: issue.key })}
          onSend={onPrompt}
          sendLabel={t('issue.comment').replace('…', '')}
        />
        <Button size="sm" variant="ghost" onClick={() => { void onPrompt(t('issue.assignPrompt', { key: issue.key })) }}>{t('issue.assign')}</Button>
        <span className={css.spacer} />
        <Button size="sm" variant="ghost" onClick={() => { onPin(pinned ? null : issue.key) }}>{pinned ? t('panel.unpin') : t('panel.pin')}</Button>
      </div>

      {issue.description !== '' ? (
        <section>
          <SectionTitle title={t('issue.description')} />
          <div className={css.body} data-collapsed={longDescription && !descriptionOpen ? true : undefined}>
            <MarkdownText text={issue.description} />
            {longDescription && !descriptionOpen ? (
              <button type="button" className={css.more} onClick={() => { setDescriptionOpen(true) }}>
                <IconChevronDownOutline14 /> more
              </button>
            ) : null}
          </div>
        </section>
      ) : null}

      {issue.parent !== undefined || issue.subtasks.length > 0 || issue.links.length > 0 ? (
        <section>
          <SectionTitle title={t('issue.links')} count={issue.links.length + issue.subtasks.length + (issue.parent === undefined ? 0 : 1)} />
          <ul className={css.list}>
            {issue.parent !== undefined ? (
              <li className={css.row}>
                <span className={css.relation}>parent</span>
                <button type="button" className={css.rowKey} onClick={() => { /* v8 ignore next -- rendered only while parent is set */ onOpenIssue(issue.parent?.key ?? '') }}>{issue.parent.key}</button>
                <span className={css.rowText}>{issue.parent.summary}</span>
              </li>
            ) : null}
            {issue.links.map(link => (
              <li key={`${link.relation}:${link.key}`} className={css.row}>
                <span className={css.relation}>{link.relation}</span>
                <button type="button" className={css.rowKey} onClick={() => { onOpenIssue(link.key) }}>{link.key}</button>
                <span className={css.rowText}>{link.summary}</span>
                {link.status !== undefined ? <Chip tone={statusTone(link.status)}>{link.status.name}</Chip> : null}
              </li>
            ))}
            {issue.subtasks.map(subtask => (
              <li key={subtask.key} className={css.row}>
                <span className={css.relation}>{t('issue.subtasks').toLowerCase()}</span>
                <button type="button" className={css.rowKey} onClick={() => { onOpenIssue(subtask.key) }}>{subtask.key}</button>
                <span className={css.rowText}>{subtask.summary}</span>
                {subtask.status !== undefined ? <Chip tone={statusTone(subtask.status)}>{subtask.status.name}</Chip> : null}
              </li>
            ))}
          </ul>
        </section>
      ) : null}

      <section>
        <SectionTitle title={t('issue.comments')} count={issue.comments.length} />
        {issue.comments.length === 0 ? <div className={css.muted}>{t('issue.noComments')}</div> : (
          <ul className={css.comments}>
            {issue.comments.map((comment) => {
              const created = Date.parse(comment.created)
              const fresh = Number.isFinite(created) && issue.fetchedAt - created < FRESH_MS
              return (
                <li key={comment.id} className={css.comment} data-fresh={fresh || undefined}>
                  <Avatar person={comment.author} size={24} />
                  <div className={css.commentBody}>
                    <div className={css.commentHead}>
                      <span className={css.commentAuthor}>{comment.author.name}</span>
                      <span className={css.dim}>{ago(t, comment.created, now)}</span>
                      {fresh ? <Chip tone="info">new</Chip> : null}
                    </div>
                    <MarkdownText text={comment.body} />
                  </div>
                </li>
              )
            })}
          </ul>
        )}
      </section>

      {issue.attachments.length > 0 ? (
        <section>
          <SectionTitle title={t('issue.attachments')} count={issue.attachments.length} />
          <ul className={css.list}>
            {issue.attachments.map(attachment => (
              <li key={attachment.filename} className={css.row}>
                {attachment.url !== undefined
                  ? <a className={css.rowKey} href={attachment.url} target="_blank" rel="noreferrer">{attachment.filename}</a>
                  : <span className={css.rowKey}>{attachment.filename}</span>}
                <span className={css.dim}>{formatBytes(attachment.size)}</span>
              </li>
            ))}
          </ul>
        </section>
      ) : null}
    </article>
  )
}
