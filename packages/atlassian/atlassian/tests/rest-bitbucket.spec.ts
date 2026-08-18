import { describe, expect, it } from 'vitest'
import {
  BitbucketRest, bitbucketPerson, diffWindow, existingCommentsFromActivities, findDiffFile, normalizeDiff, parsePrRef, prKey,
  prRecordFromRest, prSummaryFromRest, prUrl, resolveAnchor,
} from '../src/rest/bitbucket.ts'
import { BITBUCKET_DIFF, BITBUCKET_PR, fakeFetch, sentBody, type Routes } from './fixtures.ts'

const BASE = 'https://bitbucket.example.com'
const REF = { project: 'PROJ', repo: 'webapp', id: 42 }

describe('keys and refs', () => {
  it('builds keys, URLs, and parses keys or URLs', () => {
    expect(prKey({ project: 'proj', repo: 'webapp', id: 42 })).toBe('PROJ/webapp#42')
    expect(prUrl(BASE, REF)).toBe(`${BASE}/projects/PROJ/repos/webapp/pull-requests/42/overview`)
    expect(parsePrRef(' PROJ/webapp#42 ')).toEqual({ project: 'PROJ', repo: 'webapp', id: 42 })
    expect(parsePrRef('~user/repo.git#3')).toEqual({ project: '~user', repo: 'repo.git', id: 3 })
    expect(parsePrRef(`${BASE}/projects/PROJ/repos/webapp/pull-requests/42/overview?commentId=1`)).toEqual(REF)
    expect(parsePrRef('nonsense')).toBeUndefined()
    expect(parsePrRef('PROJ/webapp')).toBeUndefined()
  })

  it('reads people', () => {
    expect(bitbucketPerson({ displayName: 'D', slug: 's', avatarUrl: 'a' })).toEqual({ name: 'D', id: 's', avatar: 'a' })
    expect(bitbucketPerson({ name: 'n' })).toEqual({ name: 'n', id: 'n' })
    expect(bitbucketPerson({ slug: 'only' })).toEqual({ name: 'only', id: 'only' })
    expect(bitbucketPerson({})).toBeUndefined()
  })
})

describe('prRecordFromRest / prSummaryFromRest', () => {
  it('converts a full pull request', () => {
    const record = prRecordFromRest(BASE, BITBUCKET_PR, 9)
    expect(record).toEqual({
      kind: 'pr',
      ref: REF,
      key: 'PROJ/webapp#42',
      title: 'Fix SSO redirect loop after IdP callback',
      description: 'Reads the redirect target **after** the session cookie is set.',
      state: 'OPEN',
      author: { name: 'Avery Quinn', id: 'aquinn', avatar: 'http://b/k.png' },
      reviewers: [
        { user: { name: 'Jordan Alvarez', id: 'jalvarez' }, role: 'REVIEWER', status: 'APPROVED' },
        { user: { name: 'Mei Chen', id: 'mchen' }, role: 'REVIEWER', status: 'NEEDS_WORK' },
        { user: { name: 'ghost', id: 'ghost' }, role: 'REVIEWER', status: 'UNAPPROVED' },
        { user: { name: 'Observer', id: 'obs' }, role: 'PARTICIPANT', status: 'UNAPPROVED' },
      ],
      from: { branch: 'feature/PROJ-123-sso', commit: 'a1b2c3' },
      to: { branch: 'main', commit: '0f9e8d' },
      created: new Date(1_787_000_000_000).toISOString(),
      updated: new Date(1_787_060_000_000).toISOString(),
      version: 3,
      url: prUrl(BASE, REF),
      fetchedAt: 9,
    })
  })

  it('falls back to the caller ref and tolerates a bare payload', () => {
    expect(prRecordFromRest(BASE, { id: 7, state: 'MERGED' }, 1, REF)).toMatchObject({ ref: REF, state: 'MERGED', author: { name: '' }, version: 0 })
    expect(prRecordFromRest(BASE, { id: 7, state: 'DECLINED' }, 1)).toMatchObject({ ref: { project: '', repo: '', id: 7 }, state: 'DECLINED' })
    expect(prRecordFromRest(BASE, undefined, 1)).toMatchObject({ ref: { project: '', repo: '', id: 0 }, key: '/#0', from: { branch: '' } })
  })

  it('summarizes rows for the picker', () => {
    expect(prSummaryFromRest(BASE, BITBUCKET_PR, 'REVIEWER')).toEqual({
      ref: REF,
      key: 'PROJ/webapp#42',
      title: 'Fix SSO redirect loop after IdP callback',
      author: { name: 'Avery Quinn', id: 'aquinn', avatar: 'http://b/k.png' },
      state: 'OPEN',
      updated: new Date(1_787_060_000_000).toISOString(),
      approvals: 1,
      reviewers: 3,
      url: prUrl(BASE, REF),
      role: 'REVIEWER',
    })
    expect(prSummaryFromRest(BASE, { ...BITBUCKET_PR, updatedDate: undefined, reviewers: undefined, author: undefined })).toMatchObject({ approvals: 0, reviewers: 0, author: { name: '' } })
    expect(prSummaryFromRest(BASE, { id: 1 })).toBeUndefined()
    expect(prSummaryFromRest(BASE, 'nope')).toBeUndefined()
  })
})

describe('normalizeDiff', () => {
  const diff = normalizeDiff(BITBUCKET_DIFF)

  it('normalizes files, hunks, and both line numberings', () => {
    expect(diff.truncated).toBe(false)
    expect(diff.files.map(file => file.path)).toEqual(['src/auth/SsoCallback.ts', 'src/auth/redirect.ts', 'logo.png', 'lib/util.ts', 'lib/other/util.ts'])
    expect(diff.files[1]).toMatchObject({ oldPath: 'src/old/redirect.ts', truncated: true })
    expect(diff.files[2]).toMatchObject({ binary: true, hunks: [] })
    const first = diff.files[0]?.hunks[0]
    expect(first).toMatchObject({ sourceLine: 10, sourceSpan: 8, destinationLine: 10, destinationSpan: 10 })
    expect(first?.lines).toEqual([
      { type: 'CONTEXT', source: 10, destination: 10, text: 'export async function handleCallback(req, res) {' },
      { type: 'CONTEXT', source: 11, destination: 11, text: '  const session = await sessions.start(req)' },
      { type: 'REMOVED', source: 12, text: '  const target = readRedirect(req)' },
      { type: 'ADDED', destination: 12, text: '  res.cookie(\'sid\', session.id)' },
      { type: 'ADDED', destination: 13, text: '  const target = readRedirect(req) ?? \'/\'' },
      { type: 'ADDED', destination: 14, text: '  logger.info(`callback -> ${target}`)' },
      { type: 'CONTEXT', source: 13, destination: 15, text: '  return res.redirect(302, target)' },
      { type: 'CONTEXT', source: 14, destination: 16, text: '}' },
      { type: 'CONTEXT', text: 'odd' },
    ])
    expect(diff.files[1]?.hunks[0]).toMatchObject({ sourceLine: 0, destinationSpan: 0 })
    expect(normalizeDiff(undefined)).toEqual({ files: [], truncated: false })
    expect(normalizeDiff({ truncated: true, diffs: 'nope' })).toEqual({ files: [], truncated: true })
    expect(normalizeDiff({ diffs: [{ source: {}, destination: {} }] }).files[0]?.path).toBe('')
  })

  it('locates files by exact path, prefixed path, or unique suffix', () => {
    expect(findDiffFile(diff, 'src/auth/redirect.ts')?.path).toBe('src/auth/redirect.ts')
    expect(findDiffFile(diff, 'src/old/redirect.ts')?.path).toBe('src/auth/redirect.ts')
    expect(findDiffFile(diff, './src/auth/redirect.ts')?.path).toBe('src/auth/redirect.ts')
    expect(findDiffFile(diff, 'b/src/auth/redirect.ts')?.path).toBe('src/auth/redirect.ts')
    expect(findDiffFile(diff, 'redirect.ts')?.path).toBe('src/auth/redirect.ts')
    expect(findDiffFile(diff, 'util.ts')).toBeUndefined()
    expect(findDiffFile(diff, 'missing.ts')).toBeUndefined()
  })

  it('resolves anchors on either side and recovers a wrong side', () => {
    expect(resolveAnchor(diff, 'src/auth/SsoCallback.ts', 13, 'ADDED')).toEqual({ path: 'src/auth/SsoCallback.ts', line: 13, lineType: 'ADDED', fileType: 'TO' })
    expect(resolveAnchor(diff, 'src/auth/SsoCallback.ts', 15, 'CONTEXT')).toEqual({ path: 'src/auth/SsoCallback.ts', line: 15, lineType: 'CONTEXT', fileType: 'TO' })
    expect(resolveAnchor(diff, 'src/auth/SsoCallback.ts', 12, 'REMOVED')).toEqual({ path: 'src/auth/SsoCallback.ts', line: 12, lineType: 'REMOVED', fileType: 'FROM' })
    // Line 12 exists on both sides: the named side wins.
    expect(resolveAnchor(diff, 'src/auth/SsoCallback.ts', 12, 'ADDED')).toMatchObject({ lineType: 'ADDED', fileType: 'TO' })
    // A wrong side recovers when the number exists on the other side.
    expect(resolveAnchor(diff, 'src/auth/redirect.ts', 3, 'REMOVED')).toMatchObject({ lineType: 'ADDED', line: 3 })
    expect(resolveAnchor(diff, 'src/auth/redirect.ts', 2, 'REMOVED')).toMatchObject({ lineType: 'REMOVED', line: 2 })
    expect(resolveAnchor(diff, 'src/auth/SsoCallback.ts', 99, 'ADDED')).toBeUndefined()
    expect(resolveAnchor(diff, 'missing.ts', 1, 'ADDED')).toBeUndefined()
  })

  it('windows the hunk around the anchor', () => {
    const window = diffWindow(diff, 'src/auth/SsoCallback.ts', 13, 'ADDED', 1)
    expect(window.found).toBe(true)
    expect(window.file).toBe('src/auth/SsoCallback.ts')
    expect(window.lines.map(line => [line.type, line.anchor ?? false])).toEqual([['ADDED', false], ['ADDED', true], ['ADDED', false]])
    const removed = diffWindow(diff, 'src/auth/redirect.ts', 2, 'REMOVED', 6)
    expect(removed.found).toBe(true)
    expect(removed.lines.find(line => line.anchor)?.type).toBe('REMOVED')
    const missingLine = diffWindow(diff, 'src/auth/redirect.ts', 99, 'ADDED', 1)
    expect(missingLine).toMatchObject({ found: false, file: 'src/auth/redirect.ts' })
    expect(missingLine.lines).toHaveLength(3)
    expect(diffWindow(diff, 'lib/util.ts', 1, 'ADDED', 2)).toEqual({ file: 'lib/util.ts', lines: [], found: false })
    expect(diffWindow(diff, 'missing.ts', 1, 'ADDED', 2)).toEqual({ file: 'missing.ts', lines: [], found: false })
  })
})

/** A reply chain nesting one comment per id. */
function deepThread(ids: number[]): unknown[] {
  const head = ids[0]
  return head === undefined ? [] : [{ id: head, text: String(head), comments: deepThread(ids.slice(1)) }]
}

describe('existingCommentsFromActivities', () => {
  it('flattens comment threads with inherited anchors and skips everything else', () => {
    const rows = existingCommentsFromActivities({
      values: [
        'junk',
        { action: 'APPROVED' },
        { action: 'COMMENTED', commentAction: 'DELETED', comment: { id: 1, text: 'gone' } },
        {
          action: 'COMMENTED',
          commentAction: 'ADDED',
          commentAnchor: { path: 'src/a.ts', line: 3, lineType: 'ADDED' },
          comment: {
            id: 2, text: 'x'.repeat(650), author: { displayName: 'Mei', slug: 'mei' }, createdDate: 1_700_000_000_000,
            comments: [
              { id: 3, text: 'reply', comments: deepThread([4, 5, 6, 7, 8, 9]) },
              { text: 'no id' },
              'junk reply',
            ],
          },
        },
        { action: 'COMMENTED', comment: { id: 10, text: 'general', anchor: { path: 'README.md', line: 1, lineType: 'CONTEXT' } } },
        { action: 'COMMENTED', comment: { id: 11, text: 'file only', anchor: { path: 'README.md', lineType: 'WEIRD' } } },
        { action: 'COMMENTED', comment: { id: 12, text: 'unanchored', anchor: 'nope' } },
        { action: 'COMMENTED', comment: { text: 'missing id' } },
        { action: 'COMMENTED', comment: { id: 13 } },
      ],
    })
    expect(rows.map(row => [row.id, row.file, row.line, row.side, row.replies])).toEqual([
      [2, 'src/a.ts', 3, 'ADDED', 3],
      [3, 'src/a.ts', 3, 'ADDED', 1],
      [4, 'src/a.ts', 3, 'ADDED', 1],
      [5, 'src/a.ts', 3, 'ADDED', 1],
      [6, 'src/a.ts', 3, 'ADDED', 1],
      [7, 'src/a.ts', 3, 'ADDED', 1],
      [8, 'src/a.ts', 3, 'ADDED', 1],
      [10, 'README.md', 1, 'CONTEXT', 0],
      [11, 'README.md', undefined, undefined, 0],
      [12, undefined, undefined, undefined, 0],
    ])
    expect(rows[0]).toMatchObject({ author: { name: 'Mei', id: 'mei' }, created: '2023-11-14T22:13:20.000Z' })
    expect(rows[0]?.text).toBe(`${'x'.repeat(600)}…`)
    expect(rows[1]).toMatchObject({ author: { name: '' }, text: 'reply' })
    expect(rows[1]).not.toHaveProperty('created')
    expect(existingCommentsFromActivities(null)).toEqual([])
    expect(existingCommentsFromActivities({ values: 'nope' })).toEqual([])
  })
})

describe('BitbucketRest', () => {
  const prPath = '/rest/api/1.0/projects/PROJ/repos/webapp/pull-requests'
  const routes = {
    [`GET ${prPath}/42`]: { body: BITBUCKET_PR },
    [`GET ${prPath}?state=OPEN&limit=50&order=NEWEST`]: { body: { values: [BITBUCKET_PR, { id: 1 }] } },
    'GET /rest/api/1.0/dashboard/pull-requests?state=OPEN&role=REVIEWER&limit=50&order=NEWEST': { body: { values: [BITBUCKET_PR] } },
    'GET /rest/api/1.0/dashboard/pull-requests?state=OPEN&role=AUTHOR&limit=50&order=NEWEST': { body: { values: [BITBUCKET_PR, { ...BITBUCKET_PR, id: 43 }] } },
    'GET /rest/api/1.0/dashboard/pull-requests?state=ALL&role=REVIEWER&limit=50&order=NEWEST': { body: 'nope' },
    'GET /rest/api/1.0/dashboard/pull-requests?state=ALL&role=AUTHOR&limit=50&order=NEWEST': { body: {} },
    [`GET ${prPath}/42/diff?withComments=false&contextLines=6`]: { body: BITBUCKET_DIFF },
    [`POST ${prPath}/42/comments`]: (init: RequestInit) =>
      ({ status: 201, body: { id: 777, text: (JSON.parse(init.body as string) as { text: string }).text } }),
    'GET /rest/api/1.0/inbox/pull-requests/count': { body: { count: 3 } },
  }
  const activity = (id: number, path = 'src/a.ts') => ({
    action: 'COMMENTED', commentAnchor: { path, line: id }, comment: { id, text: `c${String(id)}` },
  })

  it('pages through activities, dedupes, and returns oldest first', async () => {
    const pages: Routes = {
      [`GET ${prPath}/42/activities?limit=50&start=0`]: { body: { isLastPage: false, nextPageStart: 2, values: [activity(1), activity(2)] } },
      [`GET ${prPath}/42/activities?limit=50&start=2`]: { body: { isLastPage: false, nextPageStart: 4, values: [activity(2), activity(3)] } },
      [`GET ${prPath}/42/activities?limit=50&start=4`]: { body: { isLastPage: false, nextPageStart: 'later', values: [activity(4)] } },
    }
    const { fetchImpl, calls } = fakeFetch(pages)
    const bitbucket = new BitbucketRest(fetchImpl, { baseUrl: BASE, token: 't' })
    const rows = await bitbucket.getComments(REF)
    expect(rows.map(row => row.id)).toEqual([4, 3, 2, 1])
    expect(calls).toHaveLength(3)
    // Never more than the page cap, even when the server always has more.
    const endless = new BitbucketRest(fakeFetch({
      [`GET ${prPath}/42/activities*`]: { body: { isLastPage: false, nextPageStart: 1, values: [activity(9)] } },
    }).fetchImpl, { baseUrl: BASE, token: 't' })
    await expect(endless.getComments(REF)).resolves.toEqual([expect.objectContaining({ id: 9 })])
    // A non-object page ends the walk with nothing.
    const bare = new BitbucketRest(fakeFetch({ [`GET ${prPath}/42/activities*`]: { body: [] } }).fetchImpl, { baseUrl: BASE, token: 't' })
    await expect(bare.getComments(REF)).resolves.toEqual([])
  })

  it('fetches, lists, and dedupes the inbox', async () => {
    const { fetchImpl, calls } = fakeFetch(routes)
    const bitbucket = new BitbucketRest(fetchImpl, { baseUrl: BASE, token: 't' }, () => 5)
    await expect(bitbucket.getPullRequest(REF)).resolves.toMatchObject({ key: 'PROJ/webapp#42', fetchedAt: 5 })
    const listed = await bitbucket.listPullRequests('PROJ', 'webapp', 'OPEN')
    expect(listed.map(item => item.key)).toEqual(['PROJ/webapp#42'])
    const inbox = await bitbucket.inbox('OPEN')
    expect(inbox.map(item => [item.key, item.role])).toEqual([['PROJ/webapp#42', 'REVIEWER'], ['PROJ/webapp#43', 'AUTHOR']])
    await expect(bitbucket.inbox('ALL')).resolves.toEqual([])
    expect(calls.some(call => call.url.endsWith('role=AUTHOR&limit=50&order=NEWEST'))).toBe(true)
  })

  it('normalizes the diff and posts comments', async () => {
    const { fetchImpl, calls } = fakeFetch(routes)
    const bitbucket = new BitbucketRest(fetchImpl, { baseUrl: BASE, token: 't' })
    const diff = await bitbucket.getDiff(REF)
    expect(diff.files).toHaveLength(5)
    const inline = await bitbucket.postComment(REF, 'hello', { path: 'src/auth/redirect.ts', line: 3, lineType: 'ADDED', fileType: 'TO' })
    expect(inline).toEqual({ id: 777, url: `${prUrl(BASE, REF)}?commentId=777` })
    expect(JSON.parse(sentBody(calls[calls.length - 1]))).toEqual({
      text: 'hello',
      anchor: { path: 'src/auth/redirect.ts', line: 3, lineType: 'ADDED', fileType: 'TO', diffType: 'EFFECTIVE' },
    })
    await bitbucket.postComment(REF, 'general', undefined)
    expect(JSON.parse(sentBody(calls[calls.length - 1]))).toEqual({ text: 'general' })
    const noId = new BitbucketRest(fakeFetch({ [`POST ${prPath}/42/comments`]: { body: {} } }).fetchImpl, { baseUrl: BASE, token: 't' })
    await expect(noId.postComment(REF, 'x', undefined)).resolves.toEqual({ id: 0, url: `${prUrl(BASE, REF)}?commentId=0` })
  })

  it('probes the inbox counter', async () => {
    const bitbucket = new BitbucketRest(fakeFetch(routes).fetchImpl, { baseUrl: BASE, token: 't' })
    await expect(bitbucket.probe()).resolves.toBe(3)
    const empty = new BitbucketRest(fakeFetch({ 'GET /rest/api/1.0/inbox/pull-requests/count': { body: {} } }).fetchImpl, { baseUrl: BASE, token: 't' })
    await expect(empty.probe()).resolves.toBe(0)
  })
})

describe('loose-shape branches', () => {
  it('keeps a person without a slug or name id-less', () => {
    expect(bitbucketPerson({ displayName: 'Display Only' })).toEqual({ name: 'Display Only' })
  })

  it('drops a participant without a usable user and defaults a missing title', () => {
    const record = prRecordFromRest(BASE, {
      ...BITBUCKET_PR,
      title: undefined,
      participants: [{ role: 'PARTICIPANT', status: 'APPROVED' }, ...(BITBUCKET_PR as { participants?: unknown[] }).participants ?? []],
    }, 5, REF)
    expect(record.title).toBe('')
    expect(record.reviewers.every(reviewer => reviewer.user.name !== '')).toBe(true)
    const summary = prSummaryFromRest(BASE, { ...BITBUCKET_PR, title: undefined })
    expect(summary?.title).toBe('')
  })

  it('renders a diff line whose text is not a string as empty', () => {
    const diff = normalizeDiff({
      diffs: [{
        destination: { toString: 'x.ts' },
        hunks: [{ segments: [{ type: 'ADDED', lines: [{ destination: 1, line: 42 }] }] }],
      }],
    })
    expect(diff.files[0]?.hunks[0]?.lines).toEqual([{ type: 'ADDED', destination: 1, text: '' }])
  })
})
