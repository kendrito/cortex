/** Pure presentation helpers: tones, relative time, keys, sorting, sizes. */
import { describe, expect, it } from 'vitest'
import {
  activityTone, basename, formatBytes, initials, prKeyOf, prStateTone, refKey, relativeTime, sameRef, severityRank,
  severityTone, shortenPath, sortFindings, statusTone,
} from '../src/client/format.ts'
import { finding } from './support.client.ts'

describe('tones', () => {
  it('maps issue status categories', () => {
    expect(statusTone({ name: 'Done', category: 'done' })).toBe('success')
    expect(statusTone({ name: 'In Progress', category: 'indeterminate' })).toBe('info')
    expect(statusTone({ name: 'To Do', category: 'new' })).toBe('neutral')
    expect(statusTone({ name: '?', category: 'unknown' })).toBe('neutral')
    expect(statusTone(undefined)).toBe('neutral')
  })

  it('maps pull request states', () => {
    expect(prStateTone('OPEN')).toBe('success')
    expect(prStateTone('MERGED')).toBe('accent')
    expect(prStateTone('DECLINED')).toBe('error')
  })

  it('maps and ranks severities', () => {
    expect(severityTone('critical')).toBe('error')
    expect(severityTone('major')).toBe('warn')
    expect(severityTone('minor')).toBe('info')
    expect(severityTone('nit')).toBe('neutral')
    expect(['critical', 'major', 'minor', 'nit'].map(severity => severityRank(severity as 'nit'))).toEqual([0, 1, 2, 3])
  })

  it('maps every activity kind', () => {
    expect(activityTone('read')).toBe('neutral')
    expect(activityTone('search')).toBe('neutral')
    expect(activityTone('delete')).toBe('error')
    expect(activityTone('decline')).toBe('error')
    expect(activityTone('approve')).toBe('success')
    expect(activityTone('merge')).toBe('success')
    expect(activityTone('create')).toBe('success')
    for (const kind of ['transition', 'comment', 'update', 'assign', 'link', 'branch'] as const) {
      expect(activityTone(kind)).toBe('info')
    }
    expect(activityTone('other')).toBe('neutral')
  })
})

describe('initials', () => {
  it('takes first and last initials, single-name second letter, and ? for empty', () => {
    expect(initials('Jordan Alvarez')).toBe('JA')
    expect(initials('aquinn')).toBe('AQ')
    expect(initials('mei.chen')).toBe('MC')
    expect(initials('   ')).toBe('?')
    expect(initials('X')).toBe('X')
  })
})

describe('relativeTime', () => {
  const now = Date.parse('2026-08-18T12:00:00.000Z')

  it('buckets by minutes, hours, and days', () => {
    expect(relativeTime('2026-08-18T11:59:50.000Z', now)).toEqual({ unit: 'now' })
    expect(relativeTime('2026-08-18T11:45:00.000Z', now)).toEqual({ unit: 'minutes', n: 15 })
    expect(relativeTime('2026-08-18T09:00:00.000Z', now)).toEqual({ unit: 'hours', n: 3 })
    expect(relativeTime('2026-08-15T12:00:00.000Z', now)).toEqual({ unit: 'days', n: 3 })
    expect(relativeTime(now - 120_000, now)).toEqual({ unit: 'minutes', n: 2 })
  })

  it('reads unparsable, future, and absent input as now', () => {
    expect(relativeTime('not a date', now)).toEqual({ unit: 'now' })
    expect(relativeTime(undefined, now)).toEqual({ unit: 'now' })
    expect(relativeTime(now + 3_600_000, now)).toEqual({ unit: 'now' })
  })
})

describe('references', () => {
  it('keys issues, pages, and pull requests distinctly', () => {
    expect(refKey({ kind: 'issue', key: 'A-1' })).toBe('issue:A-1')
    expect(refKey({ kind: 'page', id: '9' })).toBe('page:9')
    expect(refKey({ kind: 'pr', key: 'P/r#1' })).toBe('pr:P/r#1')
  })

  it('compares references including nullish sides', () => {
    expect(sameRef({ kind: 'issue', key: 'A-1' }, { kind: 'issue', key: 'A-1' })).toBe(true)
    expect(sameRef({ kind: 'issue', key: 'A-1' }, { kind: 'pr', key: 'A-1' })).toBe(false)
    expect(sameRef(null, undefined)).toBe(true)
    expect(sameRef(null, { kind: 'page', id: '1' })).toBe(false)
    expect(sameRef({ kind: 'page', id: '1' }, null)).toBe(false)
  })

  it('formats PR keys with an upper-cased project', () => {
    expect(prKeyOf({ project: 'proj', repo: 'webapp', id: 7 })).toBe('PROJ/webapp#7')
  })
})

describe('sortFindings', () => {
  it('orders by severity then recording time', () => {
    const sorted = sortFindings([
      finding({ id: 'c', severity: 'nit', at: 1 }),
      finding({ id: 'b', severity: 'critical', at: 5 }),
      finding({ id: 'a', severity: 'critical', at: 2 }),
      finding({ id: 'd', severity: 'major', at: 3 }),
    ])
    expect(sorted.map(item => item.id)).toEqual(['a', 'b', 'd', 'c'])
  })
})

describe('paths and sizes', () => {
  it('takes basenames and shortens long paths', () => {
    expect(basename('src/auth/redirect.ts')).toBe('redirect.ts')
    expect(basename('redirect.ts')).toBe('redirect.ts')
    expect(shortenPath('src/auth/redirect.ts')).toBe('src/auth/redirect.ts')
    expect(shortenPath('/apps/web/src/auth/handlers/redirect.ts')).toBe('apps/…/handlers/redirect.ts')
    expect(shortenPath('a/b/c/d/e', 3)).toBe('a/…/d/e')
  })

  it('formats byte sizes', () => {
    expect(formatBytes(512)).toBe('512 B')
    expect(formatBytes(2048)).toBe('2.0 kB')
    expect(formatBytes(48_213)).toBe('47 kB')
    expect(formatBytes(3 * 1024 * 1024)).toBe('3.0 MB')
  })
})
