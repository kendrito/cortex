/**
 * Activity tab: the session's Atlassian activity feed, newest first, with the
 * touched entity as a jump target.
 */
import type { ActivityEntry, EntityRef } from '@cortex/atlassian/client'
import type { TranslateNS } from '@cortex/client-ui-slots'
import { activityTone } from '../format.ts'
import type { NS } from '../locales.ts'
import { Empty, Chip } from '../atoms.tsx'
import { ago } from './IssueView.tsx'
import css from './drawer.module.css'

/** Props of the activity tab. */
export interface ActivityTabProps {
  activity: readonly ActivityEntry[]
  now: number
  t: TranslateNS<typeof NS>
  onSelect: (ref: EntityRef) => void
}

/**
 * Render the activity feed.
 * @param props - entries and the jump verb.
 * @returns the tab.
 */
export function ActivityTab({ activity, now, t, onSelect }: ActivityTabProps) {
  if (activity.length === 0) return <Empty title={t('activity.empty')} />
  return (
    <ol className={css.timeline}>
      {activity.map(entry => (
        <li key={entry.id} className={css.timelineRow} data-failed={entry.ok ? undefined : true}>
          <span className={css.timelineDot} data-tone={activityTone(entry.kind)} aria-hidden />
          <div className={css.timelineBody}>
            <div className={css.timelineSummary}>
              {entry.entity !== undefined
                ? <button type="button" className={css.timelineLink} onClick={() => { /* v8 ignore next -- the button renders only for an entity-bearing entry */ if (entry.entity !== undefined) onSelect(entry.entity) }}>{entry.summary}</button>
                : <span>{entry.summary}</span>}
              {!entry.ok ? <Chip tone="error">{t('activity.failed')}</Chip> : null}
            </div>
            <div className={css.timelineMeta}>
              <span className={css.timelineTool}>{entry.tool.replace(/^mcp__(?:atlassian|bitbucket)__/, '')}</span>
              <span>· {ago(t, entry.at, now)}</span>
            </div>
          </div>
        </li>
      ))}
    </ol>
  )
}
