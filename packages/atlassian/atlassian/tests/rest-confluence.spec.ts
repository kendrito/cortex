import { describe, expect, it } from 'vitest'
import { ConfluenceRest, pageRecordFromRest } from '../src/rest/confluence.ts'
import { CONFLUENCE_PAGE, fakeFetch } from './fixtures.ts'

const BASE = 'https://confluence.example.com'

describe('pageRecordFromRest', () => {
  it('converts a full page', () => {
    const record = pageRecordFromRest(BASE, CONFLUENCE_PAGE, 7)
    expect(record).toEqual({
      kind: 'page',
      id: '98765',
      title: 'Auth service runbook',
      space: { key: 'ENG', name: 'Engineering' },
      version: 12,
      versionAt: '2026-08-17T11:20:00.000Z',
      versionBy: { name: 'Kendrito', id: 'kendrito', avatar: '/pic.png' },
      created: '2025-11-02T09:00:00.000Z',
      author: { name: 'Jordan Alvarez', id: 'jalvarez' },
      ancestors: [{ id: '100', title: 'Platform' }, { id: '200', title: '' }],
      labels: ['runbook', 'auth'],
      body: '## Purpose\n\nHow to operate the **auth service**.',
      bodyTruncated: false,
      url: `${BASE}/display/ENG/Auth+service+runbook`,
      fetchedAt: 7,
    })
  })

  it('tolerates a minimal payload and long bodies', () => {
    expect(pageRecordFromRest(BASE, undefined, 1)).toEqual({
      kind: 'page',
      id: '',
      title: '',
      space: { key: '' },
      version: 0,
      ancestors: [],
      labels: [],
      body: '',
      bodyTruncated: false,
      url: `${BASE}/pages/viewpage.action?pageId=`,
      fetchedAt: 1,
    })
    const long = pageRecordFromRest(BASE, { id: '1', body: { view: { value: 'x'.repeat(20_000) } }, version: { by: { userKey: 'uk' } } }, 1)
    expect(long.bodyTruncated).toBe(true)
    expect(long.url).toBe(`${BASE}/pages/viewpage.action?pageId=1`)
    expect(long.versionBy).toBeUndefined()
    const byName = pageRecordFromRest(BASE, { id: '2', version: { by: { username: 'u1', userKey: 'uk' } }, metadata: { labels: { results: 'nope' } } }, 1)
    expect(byName.versionBy).toEqual({ name: 'u1', id: 'u1' })
    expect(byName.labels).toEqual([])
  })
})

describe('ConfluenceRest', () => {
  const routes = {
    'GET /rest/api/content/98765*': { body: CONFLUENCE_PAGE },
    'GET /rest/api/content?spaceKey=ENG&title=Auth+service+runbook&expand=body.view%2Cversion%2Cspace%2Cancestors%2Chistory%2Cmetadata.labels&limit=1': { body: { results: [CONFLUENCE_PAGE] } },
    'GET /rest/api/content?spaceKey=ENG&title=Missing&expand=body.view%2Cversion%2Cspace%2Cancestors%2Chistory%2Cmetadata.labels&limit=1': { body: { results: [] } },
    'GET /rest/api/content?spaceKey=ENG&title=Odd&expand=body.view%2Cversion%2Cspace%2Cancestors%2Chistory%2Cmetadata.labels&limit=1': { body: 'not an object' },
    'GET /rest/api/user/current': { body: { displayName: 'Kendrito' } },
  }

  it('fetches by id and by space + title', async () => {
    const { fetchImpl, calls } = fakeFetch(routes)
    const confluence = new ConfluenceRest(fetchImpl, { baseUrl: BASE, token: 't' }, () => 3)
    await expect(confluence.getPage('98765')).resolves.toMatchObject({ id: '98765', fetchedAt: 3 })
    expect(calls[0]?.url).toContain('/rest/api/content/98765?expand=body.view')
    await expect(confluence.findPage('ENG', 'Auth service runbook')).resolves.toMatchObject({ id: '98765' })
    await expect(confluence.findPage('ENG', 'Missing')).rejects.toMatchObject({ code: 'not-found' })
    await expect(confluence.findPage('ENG', 'Odd')).rejects.toMatchObject({ code: 'not-found' })
  })

  it('probes the authenticated user', async () => {
    const confluence = new ConfluenceRest(fakeFetch(routes).fetchImpl, { baseUrl: BASE, token: 't' })
    await expect(confluence.myself()).resolves.toBe('Kendrito')
    const byUsername = new ConfluenceRest(fakeFetch({ 'GET /rest/api/user/current': { body: { username: 'ken' } } }).fetchImpl, { baseUrl: BASE, token: 't' })
    await expect(byUsername.myself()).resolves.toBe('ken')
    const anonymous = new ConfluenceRest(fakeFetch({ 'GET /rest/api/user/current': { body: {} } }).fetchImpl, { baseUrl: BASE, token: 't' })
    await expect(anonymous.myself()).resolves.toBe('authenticated')
  })
})

describe('person branches', () => {
  it('falls back to the userKey id and tolerates a person without any id', () => {
    const record = pageRecordFromRest(BASE, {
      id: '1',
      title: 'T',
      version: { number: 2, when: '2026-01-01T00:00:00.000Z', by: { displayName: 'Keyed', userKey: 'kk' } },
      history: { createdDate: '2025-01-01T00:00:00.000Z', createdBy: { displayName: 'Only Display' } },
    }, 3)
    expect(record.versionBy).toEqual({ name: 'Keyed', id: 'kk' })
    expect(record.author).toEqual({ name: 'Only Display' })
  })
})
