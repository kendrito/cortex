import { describe, expect, it } from 'vitest'
import { COMMENT_LIMIT_COUNT, JiraRest, issueRecordFromRest, jiraPerson, jiraStatus } from '../src/rest/jira.ts'
import { JIRA_ISSUE, fakeFetch } from './fixtures.ts'

const BASE = 'https://jira.example.com'

describe('issueRecordFromRest', () => {
  it('converts a full issue', () => {
    const record = issueRecordFromRest(BASE, JIRA_ISSUE, 1234)
    expect(record).toMatchObject({
      kind: 'issue',
      key: 'PROJ-123',
      summary: 'Login page ignores SSO redirect target',
      status: { name: 'In Progress', category: 'indeterminate' },
      type: 'Story',
      priority: 'High',
      assignee: { name: 'Avery Quinn', id: 'aquinn', avatar: 'http://j/a48.png' },
      reporter: { name: 'Jordan Alvarez', id: 'jalvarez', avatar: 'http://j/a32.png' },
      labels: ['auth', 'frontend'],
      components: ['web'],
      fixVersions: ['2.4.0'],
      created: '2026-08-15T10:00:00.000+0000',
      updated: '2026-08-18T10:00:00.000+0000',
      dueDate: '2026-08-22',
      resolution: 'Fixed',
      project: 'PROJ',
      parent: { key: 'PROJ-100', summary: 'Parent story' },
      subtasks: [
        { key: 'PROJ-124', summary: 'Add regression test', status: { name: 'To Do', category: 'new' } },
        { key: 'PROJ-125', summary: 'No status' },
      ],
      epic: { key: 'PROJ-98', name: 'SSO hardening' },
      sprint: 'Sprint 42',
      storyPoints: 5,
      links: [
        { relation: 'blocks', key: 'PROJ-98', summary: 'Epic rollout', status: { name: 'In Progress', category: 'indeterminate' } },
        { relation: 'Relates', key: 'PROJ-50', summary: 'Related' },
      ],
      attachments: [
        { filename: 'redirect-loop.png', size: 48213, url: 'http://j/attachment/1', mimeType: 'image/png' },
        { filename: 'notes.txt', size: 0 },
      ],
      transitions: [{ id: '31', name: 'Ready for review', to: 'In Review' }, { id: '41', name: 'Done', to: 'Done' }],
      url: `${BASE}/browse/PROJ-123`,
      fetchedAt: 1234,
    })
    expect(record.description).toContain('## Problem')
    expect(record.description).toContain('`state`')
    // Newest first, bodies bounded, ids defaulted for anonymous entries.
    expect(record.comments.map(comment => comment.id)).toEqual(['2', '1002', '1001'])
    expect(record.comments[1]?.body.endsWith('…')).toBe(true)
    expect(record.comments[2]?.body).toBe('Repro: **bold**')
    expect(record.comments[0]?.author).toEqual({ name: '' })
  })

  it('keeps the newest bounded window of comments', () => {
    const comments = Array.from({ length: COMMENT_LIMIT_COUNT + 5 }, (_, index) => ({ id: String(index), body: `c${String(index)}` }))
    const record = issueRecordFromRest(BASE, { key: 'A-1', fields: { comment: { comments } } }, 0)
    expect(record.comments).toHaveLength(COMMENT_LIMIT_COUNT)
    expect(record.comments[0]?.id).toBe(String(COMMENT_LIMIT_COUNT + 4))
  })

  it('reads sprint objects and epic names in either order', () => {
    const record = issueRecordFromRest(BASE, {
      key: 'A-1',
      names: { customfield_1: 'Sprint', customfield_2: 'Epic Name', customfield_3: 'Epic Link', customfield_4: 'Story Point Estimate', customfield_5: 'Sprint' },
      fields: { customfield_1: [{ name: 'Sprint 7', state: 'ACTIVE' }], customfield_2: 'Name first', customfield_3: 'EP-1', customfield_4: 'not a number', customfield_5: [] },
    }, 0)
    expect(record.sprint).toBe('Sprint 7')
    expect(record.epic).toEqual({ key: 'EP-1' })
    expect(record.storyPoints).toBeUndefined()
    const bare = issueRecordFromRest(BASE, { key: 'A-2', names: { customfield_1: 'Sprint' }, fields: { customfield_1: 'garbage without name' } }, 0)
    expect(bare.sprint).toBeUndefined()
    const bare2 = issueRecordFromRest(BASE, { key: 'A-3', names: { customfield_1: 'Sprint', customfield_2: 'Epic Link' }, fields: { customfield_1: [42], customfield_2: 7 } }, 0)
    expect(bare2.sprint).toBeUndefined()
    expect(bare2.epic).toBeUndefined()
  })

  it('tolerates a minimal or malformed payload', () => {
    const record = issueRecordFromRest(BASE, 'nonsense', 5)
    expect(record).toEqual({
      kind: 'issue',
      key: '',
      summary: '',
      status: { name: 'Unknown', category: 'unknown' },
      type: 'Issue',
      labels: [],
      components: [],
      fixVersions: [],
      description: '',
      subtasks: [],
      comments: [],
      links: [],
      attachments: [],
      transitions: [],
      url: `${BASE}/browse/`,
      fetchedAt: 5,
    })
    expect(issueRecordFromRest(BASE, { key: 'A-1', fields: { assignee: { emailAddress: 'x@y' }, labels: [1, 'ok'] } }, 0)).toMatchObject({ labels: ['ok'] })
  })
})

describe('jiraPerson / jiraStatus', () => {
  it('reads people and statuses from partial objects', () => {
    expect(jiraPerson({ name: 'k' })).toEqual({ name: 'k', id: 'k' })
    expect(jiraPerson({ key: 'k1', accountId: 'acc' })).toEqual({ name: 'k1', id: 'k1' })
    expect(jiraPerson({ displayName: 'D', accountId: 'acc' })).toEqual({ name: 'D', id: 'acc' })
    expect(jiraPerson({})).toBeUndefined()
    expect(jiraPerson(null)).toBeUndefined()
    expect(jiraStatus({ name: 'Done', statusCategory: { name: 'Done' } })).toEqual({ name: 'Done', category: 'done' })
    expect(jiraStatus({ name: 'Odd' })).toEqual({ name: 'Odd', category: 'unknown' })
    expect(jiraStatus({})).toBeUndefined()
  })
})

describe('JiraRest', () => {
  it('fetches an issue with the field list and expansions', async () => {
    const { fetchImpl, calls } = fakeFetch({ 'GET /rest/api/2/issue/PROJ-123*': { body: JIRA_ISSUE } })
    const jira = new JiraRest(fetchImpl, { baseUrl: BASE, token: 't' }, () => 99)
    const record = await jira.getIssue('PROJ-123')
    expect(record.key).toBe('PROJ-123')
    expect(record.fetchedAt).toBe(99)
    expect(calls[0]?.url).toContain('/rest/api/2/issue/PROJ-123?fields=')
    expect(calls[0]?.url).toContain('expand=names%2Ctransitions')
    expect(calls[0]?.init.headers).toMatchObject({ Authorization: 'Bearer t' })
  })

  it('probes the authenticated user', async () => {
    const named = new JiraRest(fakeFetch({ 'GET /rest/api/2/myself': { body: { displayName: 'Avery Quinn' } } }).fetchImpl, { baseUrl: BASE, token: 't' })
    await expect(named.myself()).resolves.toBe('Avery Quinn')
    const login = new JiraRest(fakeFetch({ 'GET /rest/api/2/myself': { body: { name: 'aquinn' } } }).fetchImpl, { baseUrl: BASE, token: 't' })
    await expect(login.myself()).resolves.toBe('aquinn')
    const anonymous = new JiraRest(fakeFetch({ 'GET /rest/api/2/myself': { body: {} } }).fetchImpl, { baseUrl: BASE, token: 't' })
    await expect(anonymous.myself()).resolves.toBe('authenticated')
    const denied = new JiraRest(fakeFetch({ 'GET /rest/api/2/myself': { status: 401, body: {} } }).fetchImpl, { baseUrl: BASE, token: 't' })
    await expect(denied.myself()).rejects.toMatchObject({ code: 'unauthorized' })
  })
})

describe('loose-shape branches', () => {
  it('keeps a person without name, key, or accountId id-less', () => {
    expect(jiraPerson({ displayName: 'Display Only' })).toEqual({ name: 'Display Only' })
  })

  it('tolerates comments, links, subtasks, versions, and parents with absent fields', () => {
    const record = issueRecordFromRest(BASE, {
      key: 'PROJ-9',
      names: { customfield_1: 'Epic Link', customfield_2: 'Epic Name' },
      fields: {
        comment: { comments: [{ id: 'c1', author: { displayName: 'A' }, created: 'x' }] },
        issuelinks: [
          { type: {}, outwardIssue: { key: 'PROJ-10', fields: {} } },
          { type: { inward: 'is blocked by' }, inwardIssue: { key: 'PROJ-11' } },
        ],
        subtasks: [{ key: 'PROJ-12', fields: {} }],
        fixVersions: [{}, { name: 'v1' }],
        parent: { key: 'PROJ-13', fields: {} },
        customfield_1: 'PROJ-98',
        customfield_2: 42,
      },
    }, 1)
    expect(record.comments[0]?.body).toBe('')
    expect(record.links).toEqual([
      { relation: 'relates to', key: 'PROJ-10', summary: '' },
      { relation: 'is blocked by', key: 'PROJ-11', summary: '' },
    ])
    expect(record.subtasks).toEqual([{ key: 'PROJ-12', summary: '' }])
    expect(record.fixVersions).toEqual(['v1'])
    expect(record.parent).toEqual({ key: 'PROJ-13', summary: '' })
    // The Epic Name field with a non-string value leaves the linked epic name-less.
    expect(record.epic).toEqual({ key: 'PROJ-98' })
  })
})
