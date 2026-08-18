/**
 * Shared JSON fixtures shaped like the three Data Center REST APIs and a
 * routing fake `fetch` used by the REST adapter and service specs.
 */
import { expect } from 'vitest'
import type { FetchLike } from '../src/rest/http.ts'

/** vitest's stringContaining, typed as the string it matches (its `any` return trips no-unsafe-assignment). */
export const containing = (needle: string): string => expect.stringContaining(needle) as string

/** A promise rejected with a possibly-non-Error reason, for the String() fallback paths. */
export const rejectWith = (reason: unknown): Promise<never> => {
  const raise = (reject: (reason: unknown) => void): void => { reject(reason) }
  return new Promise((_resolve, reject) => { raise(reject) })
}

/** The string body a recorded fake-fetch call sent (the fakes always send strings). */
export const sentBody = (call: { init: RequestInit } | undefined): string => call?.init.body as string

/** Full Jira issue JSON (`GET /rest/api/2/issue/PROJ-123`). */
export const JIRA_ISSUE = {
  id: '10001',
  key: 'PROJ-123',
  names: { customfield_10020: 'Sprint', customfield_10014: 'Epic Link', customfield_10015: 'Epic Name', customfield_10016: 'Story Points', customfield_9: 'Other', plainfield: 'Ignored' },
  fields: {
    summary: 'Login page ignores SSO redirect target',
    description: 'h2. Problem\nWhen the IdP returns the app drops the {{state}} parameter.\n\n* one\n* two',
    status: { name: 'In Progress', statusCategory: { key: 'indeterminate', name: 'In Progress' } },
    issuetype: { name: 'Story' },
    priority: { name: 'High' },
    assignee: { name: 'kendrito', displayName: 'Kendrito', avatarUrls: { '48x48': 'http://j/a48.png' } },
    reporter: { name: 'jalvarez', displayName: 'Jordan Alvarez', avatarUrls: { '32x32': 'http://j/a32.png' } },
    labels: ['auth', 'frontend'],
    components: [{ name: 'web' }, { nope: true }],
    fixVersions: [{ name: '2.4.0' }],
    created: '2026-08-15T10:00:00.000+0000',
    updated: '2026-08-18T10:00:00.000+0000',
    duedate: '2026-08-22',
    resolution: { name: 'Fixed' },
    project: { key: 'PROJ', name: 'Project' },
    parent: { key: 'PROJ-100', fields: { summary: 'Parent story' } },
    subtasks: [
      { key: 'PROJ-124', fields: { summary: 'Add regression test', status: { name: 'To Do', statusCategory: { key: 'new' } } } },
      { key: 'PROJ-125', fields: { summary: 'No status' } },
      { nokey: true },
    ],
    comment: {
      comments: [
        { id: '1001', author: { name: 'jalvarez', displayName: 'Jordan Alvarez' }, created: '2026-08-16T09:12:00.000+0000', body: 'Repro: *bold*' },
        { id: '1002', author: { name: 'kendrito', displayName: 'Kendrito' }, created: '2026-08-17T15:40:00.000+0000', body: 'x'.repeat(2500) },
        'not a comment',
        { body: 'no id' },
      ],
      total: 3,
    },
    attachment: [
      { filename: 'redirect-loop.png', size: 48213, content: 'http://j/attachment/1', mimeType: 'image/png' },
      { filename: 'notes.txt' },
      { nofilename: true },
    ],
    issuelinks: [
      { type: { name: 'Blocks', inward: 'is blocked by', outward: 'blocks' }, outwardIssue: { key: 'PROJ-98', fields: { summary: 'Epic rollout', status: { name: 'In Progress', statusCategory: { key: 'indeterminate' } } } } },
      { type: { name: 'Relates' }, inwardIssue: { key: 'PROJ-50', fields: { summary: 'Related' } } },
      { type: { name: 'Broken' } },
      { notype: true },
    ],
    customfield_10020: ['com.atlassian.greenhopper.service.sprint.Sprint@1[id=42,rapidViewId=3,state=ACTIVE,name=Sprint 42,startDate=2026-08-11]'],
    customfield_10014: 'PROJ-98',
    customfield_10015: 'SSO hardening',
    customfield_10016: 5,
    customfield_9: null,
  },
  transitions: [
    { id: '31', name: 'Ready for review', to: { name: 'In Review' } },
    { id: '41', name: 'Done' },
    { noid: true },
  ],
}

/** Confluence page JSON (`GET /rest/api/content/98765`). */
export const CONFLUENCE_PAGE = {
  id: '98765',
  type: 'page',
  title: 'Auth service runbook',
  space: { key: 'ENG', name: 'Engineering' },
  version: { number: 12, when: '2026-08-17T11:20:00.000Z', by: { displayName: 'Kendrito', username: 'kendrito', profilePicture: { path: '/pic.png' } } },
  history: { createdDate: '2025-11-02T09:00:00.000Z', createdBy: { displayName: 'Jordan Alvarez', username: 'jalvarez' } },
  ancestors: [{ id: '100', title: 'Platform' }, { id: '200' }, { title: 'no id' }],
  metadata: { labels: { results: [{ name: 'runbook' }, { name: 'auth' }, { noname: true }] } },
  body: { view: { value: '<h2>Purpose</h2><p>How to operate the <strong>auth service</strong>.</p>' } },
  _links: { webui: '/display/ENG/Auth+service+runbook' },
}

/** Bitbucket pull request JSON. */
export const BITBUCKET_PR = {
  id: 42,
  version: 3,
  title: 'Fix SSO redirect loop after IdP callback',
  description: 'Reads the redirect target **after** the session cookie is set.',
  state: 'OPEN',
  createdDate: 1_787_000_000_000,
  updatedDate: 1_787_060_000_000,
  fromRef: { id: 'refs/heads/feature/PROJ-123-sso', displayId: 'feature/PROJ-123-sso', latestCommit: 'a1b2c3', repository: { slug: 'webapp', project: { key: 'PROJ' } } },
  toRef: { id: 'refs/heads/main', displayId: 'main', latestCommit: '0f9e8d', repository: { slug: 'webapp', project: { key: 'PROJ' } } },
  author: { user: { name: 'kendrito', displayName: 'Kendrito', slug: 'kendrito', avatarUrl: 'http://b/k.png' }, role: 'AUTHOR', approved: false, status: 'UNAPPROVED' },
  reviewers: [
    { user: { name: 'jalvarez', displayName: 'Jordan Alvarez', slug: 'jalvarez' }, role: 'REVIEWER', approved: true, status: 'APPROVED' },
    { user: { name: 'mchen', displayName: 'Mei Chen', slug: 'mchen' }, role: 'REVIEWER', approved: false, status: 'NEEDS_WORK' },
    { user: { name: 'ghost' }, role: 'REVIEWER', status: 'WEIRD' },
    { nouser: true },
  ],
  participants: [{ user: { name: 'obs', displayName: 'Observer' }, role: 'PARTICIPANT', status: 'UNAPPROVED' }],
}

/** Bitbucket structured diff JSON. */
export const BITBUCKET_DIFF = {
  fromHash: '0f9e8d',
  toHash: 'a1b2c3',
  truncated: false,
  diffs: [
    {
      source: { toString: 'src/auth/SsoCallback.ts' },
      destination: { toString: 'src/auth/SsoCallback.ts' },
      binary: false,
      truncated: false,
      hunks: [{
        sourceLine: 10, sourceSpan: 8, destinationLine: 10, destinationSpan: 10,
        segments: [
          { type: 'CONTEXT', lines: [{ source: 10, destination: 10, line: 'export async function handleCallback(req, res) {' }, { source: 11, destination: 11, line: '  const session = await sessions.start(req)' }] },
          { type: 'REMOVED', lines: [{ source: 12, destination: 12, line: '  const target = readRedirect(req)' }] },
          { type: 'ADDED', lines: [{ source: 12, destination: 12, line: '  res.cookie(\'sid\', session.id)' }, { source: 12, destination: 13, line: '  const target = readRedirect(req) ?? \'/\'' }, { source: 12, destination: 14, line: '  logger.info(`callback -> ${target}`)' }] },
          { type: 'CONTEXT', lines: [{ source: 13, destination: 15, line: '  return res.redirect(302, target)' }, { source: 14, destination: 16, line: '}' }, 'garbage'] },
          { type: 'UNKNOWN', lines: [{ line: 'odd' }] },
        ],
      }, 'not a hunk'],
    },
    {
      source: { toString: 'src/old/redirect.ts' },
      destination: { toString: 'src/auth/redirect.ts' },
      binary: false,
      truncated: true,
      hunks: [{
        segments: [
          { type: 'CONTEXT', lines: [{ source: 1, destination: 1, line: 'export function readRedirect(req) {' }] },
          { type: 'REMOVED', lines: [{ source: 2, destination: 2, line: '  return req.query.state' }] },
          { type: 'ADDED', lines: [{ source: 2, destination: 2, line: '  const state = req.query.state' }, { source: 2, destination: 3, line: '  return decodeURIComponent(state)' }] },
        ],
      }],
    },
    { source: { toString: 'logo.png' }, destination: null, binary: true, truncated: false, hunks: [] },
    { source: { toString: 'lib/util.ts' }, destination: { toString: 'lib/util.ts' }, hunks: [] },
    { source: { toString: 'lib/other/util.ts' }, destination: { toString: 'lib/other/util.ts' }, hunks: [] },
    'not a file',
  ],
}

/** One fake response. */
export interface FakeResponse {
  status?: number
  body?: unknown
  text?: string
}

/** Route table entry: exact `METHOD path` (query included) or a `METHOD path*` prefix. */
export type Routes = Record<string, FakeResponse | ((init: RequestInit, url: string) => FakeResponse)>

/**
 * Build a routing fake fetch. Records every call.
 * @param routes - route table.
 * @returns the fake and its call log.
 */
export function fakeFetch(routes: Routes): { fetchImpl: FetchLike; calls: { url: string; init: RequestInit }[] } {
  const calls: { url: string; init: RequestInit }[] = []
  const fetchImpl: FetchLike = (input, init) => {
    calls.push({ url: input, init })
    const method = init.method ?? 'GET'
    const url = new URL(input)
    const path = `${url.pathname}${url.search}`
    let hit = routes[`${method} ${path}`] ?? routes[`${method} ${url.pathname}`]
    if (hit === undefined) {
      const prefix = Object.keys(routes).find(key => key.endsWith('*') && `${method} ${path}`.startsWith(key.slice(0, -1)))
      if (prefix !== undefined) hit = routes[prefix]
    }
    if (hit === undefined) return Promise.resolve(new Response(JSON.stringify({ errors: [{ message: `no route ${method} ${path}` }] }), { status: 404 }))
    const response = typeof hit === 'function' ? hit(init, input) : hit
    const body = response.text ?? (response.body === undefined ? '' : JSON.stringify(response.body))
    return Promise.resolve(new Response(body, { status: response.status ?? 200, headers: { 'content-type': 'application/json' } }))
  }
  return { fetchImpl, calls }
}
