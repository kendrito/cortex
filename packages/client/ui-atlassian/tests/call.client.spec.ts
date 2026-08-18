/** Card facts derived from one frozen tool-call block. */
import { describe, expect, it } from 'vitest'
import { callFacts, parseArgs, parseJsonText, serviceLabel, splitToolName, toolTitle } from '../src/client/call.ts'
import { running, settled } from './card-support.client.ts'

describe('splitToolName', () => {
  it('separates the MCP namespace from the raw name and marks local tools', () => {
    expect(splitToolName('mcp__atlassian__jira_get_issue')).toEqual({ server: 'atlassian', raw: 'jira_get_issue' })
    expect(splitToolName('mcp__bitbucket__bitbucket_get_pull_request_diff')).toEqual({ server: 'bitbucket', raw: 'bitbucket_get_pull_request_diff' })
    expect(splitToolName('atlassian_review_finding')).toEqual({ server: 'local', raw: 'atlassian_review_finding' })
  })
})

describe('parseArgs / parseJsonText', () => {
  it('parses objects and tolerates streaming prefixes and non-objects', () => {
    expect(parseArgs('{"issue_key":"PROJ-1"}')).toEqual({ issue_key: 'PROJ-1' })
    expect(parseArgs('{"issue_key":"PRO')).toEqual({})
    expect(parseArgs('[1,2]')).toEqual({})
    expect(parseArgs('"text"')).toEqual({})
  })

  it('parses JSON result text only when it is JSON', () => {
    expect(parseJsonText('')).toBeUndefined()
    expect(parseJsonText('   ')).toBeUndefined()
    expect(parseJsonText('plain text')).toBeUndefined()
    expect(parseJsonText(' {"a":1} ')).toEqual({ a: 1 })
    expect(parseJsonText('[1]')).toEqual([1])
    expect(parseJsonText('{not json')).toBeUndefined()
  })
})

describe('callFacts', () => {
  it('reads a running call as running with parsed prefix arguments', () => {
    const facts = callFacts('mcp__atlassian__jira_get_issue', running('mcp__atlassian__jira_get_issue', '{"issue_key":"proj-9"}'))
    expect(facts.state).toBe('running')
    expect(facts.text).toBe('')
    expect(facts.json).toBeUndefined()
    expect(facts.issueKey).toBe('PROJ-9')
    expect(facts.entity).toEqual({ kind: 'issue', key: 'PROJ-9' })
  })

  it('reads settled ok / error / interrupted states and only parses JSON when ok', () => {
    const ok = callFacts('mcp__atlassian__jira_get_issue', settled('mcp__atlassian__jira_get_issue', { issue_key: 'PROJ-1' }, '{"key":"PROJ-1"}'))
    expect(ok.state).toBe('ok')
    expect(ok.json).toEqual({ key: 'PROJ-1' })
    const failed = callFacts('mcp__atlassian__jira_get_issue', settled('mcp__atlassian__jira_get_issue', {}, '{"error":"boom"}', { isError: true }))
    expect(failed.state).toBe('error')
    expect(failed.json).toBeUndefined()
    const stopped = callFacts('mcp__atlassian__jira_get_issue', settled('mcp__atlassian__jira_get_issue', {}, '', {
      isError: true, error: { name: 'Interrupted', code: 'interrupted' },
    }))
    expect(stopped.state).toBe('stopped')
    const nullCall = callFacts('x', settled('x', {}, 'a', { call: null, content: [{ type: 'text', text: 'a' }, { type: 'image', mimeType: 'image/png', data: '' } as never] }))
    expect(nullCall.args).toEqual({})
    expect(nullCall.text).toBe('a')
  })

  it('finds issue keys from the linking argument names and rejects malformed keys', () => {
    expect(callFacts('mcp__atlassian__jira_create_issue_link', settled('x', { inward_issue_key: 'ab-2' }, '')).issueKey).toBe('AB-2')
    expect(callFacts('mcp__atlassian__jira_link_to_epic', settled('x', { epic_key: 'EP-7' }, '')).issueKey).toBe('EP-7')
    expect(callFacts('mcp__atlassian__jira_get_issue', settled('x', { issue_key: 'not a key' }, '')).issueKey).toBeUndefined()
    expect(callFacts('mcp__atlassian__jira_get_issue', settled('x', { issue_key: 42 }, '')).issueKey).toBeUndefined()
  })

  it('derives pull request addresses from either argument vocabulary', () => {
    const detail = callFacts('mcp__bitbucket__bitbucket_get_pull_request_details', settled('x', { project: 'proj', repository: 'webapp', prId: 42 }, ''))
    expect(detail.pr).toEqual({ project: 'proj', repo: 'webapp', id: 42 })
    expect(detail.entity).toEqual({ kind: 'pr', key: 'PROJ/webapp#42' })
    const inline = callFacts('mcp__bitbucket__bitbucket_add_pull_request_file_line_comment', settled('x', { workspaceSlug: 'PROJ', repoSlug: 'webapp', prId: '7' }, ''))
    expect(inline.pr).toEqual({ project: 'PROJ', repo: 'webapp', id: 7 })
    const noProject = callFacts('mcp__bitbucket__bitbucket_get_pull_request_details', settled('x', { repository: 'webapp', prId: 3 }, ''))
    expect(noProject.pr).toEqual({ project: '', repo: 'webapp', id: 3 })
    expect(noProject.entity).toBeUndefined()
    expect(callFacts('x', settled('x', { repository: 'webapp' }, '')).pr).toBeUndefined()
    expect(callFacts('x', settled('x', { prId: 0, repository: 'webapp' }, '')).pr).toBeUndefined()
  })

  it('derives page entities from page_id or parent_id for Confluence tools only', () => {
    expect(callFacts('mcp__atlassian__confluence_get_page', settled('x', { page_id: '9' }, '')).entity).toEqual({ kind: 'page', id: '9' })
    expect(callFacts('mcp__atlassian__confluence_get_page_children', settled('x', { parent_id: 8 }, '')).entity).toEqual({ kind: 'page', id: '8' })
    expect(callFacts('mcp__atlassian__jira_get_issue', settled('x', { page_id: '9' }, '')).entity).toBeUndefined()
    expect(callFacts('mcp__atlassian__confluence_get_page', settled('x', { title: 'x' }, '')).entity).toBeUndefined()
  })
})

describe('labels', () => {
  it('titles raw names and labels services', () => {
    expect(toolTitle('jira_get_issue')).toBe('Get issue')
    expect(toolTitle('bitbucket_get_pull_request_diff')).toBe('Get pull request diff')
    expect(toolTitle('atlassian_review_finding')).toBe('Atlassian review finding')
    expect(serviceLabel('jira_search')).toBe('Jira')
    expect(serviceLabel('confluence_search')).toBe('Confluence')
    expect(serviceLabel('bitbucket_search_content')).toBe('Bitbucket')
    expect(serviceLabel('atlassian_review_finding')).toBe('Review')
  })
})
