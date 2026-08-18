import { describe, expect, it } from 'vitest'
import { describeWrite, gateDecision } from '../src/gate.ts'

const WRITE = 'mcp__atlassian__jira_transition_issue'
const READ = 'mcp__atlassian__jira_get_issue'

describe('gateDecision', () => {
  it('delegates reads and non-Atlassian tools regardless of policy', () => {
    expect(gateDecision(READ, { issue_key: 'A-1' }, 'ask')).toBeUndefined()
    expect(gateDecision('bash', {}, 'deny')).toBeUndefined()
  })

  it('asks, allows, or denies writes by policy', () => {
    expect(gateDecision(WRITE, { issue_key: 'A-1' }, 'ask')).toEqual({ kind: 'ask', reason: 'Atlassian transition: transition issue on A-1' })
    expect(gateDecision(WRITE, { issue_key: 'A-1' }, 'allow')).toBeUndefined()
    expect(gateDecision(WRITE, { issue_key: 'A-1' }, 'deny')).toEqual({
      kind: 'deny',
      reason: 'Atlassian transition: transition issue on A-1 — Atlassian writes are disabled in settings',
    })
  })
})

describe('describeWrite', () => {
  it('names the operation and the first recognizable target', () => {
    expect(describeWrite('mcp__atlassian__confluence_update_page', { page_id: '9' })).toBe('Atlassian update: update page on 9')
    expect(describeWrite('mcp__atlassian__confluence_create_page', { title: 'Runbook' })).toBe('Atlassian create: create page on Runbook')
    expect(describeWrite('mcp__bitbucket__bitbucket_merge_pull_request', { prId: 42, repository: 'webapp' })).toBe('Atlassian merge: merge pull request on 42')
    expect(describeWrite('mcp__bitbucket__bitbucket_create_branch', { repository: 'webapp' })).toBe('Atlassian branch: create branch on webapp')
    expect(describeWrite('mcp__bitbucket__bitbucket_get_repository_details', { repoSlug: 'webapp' })).toBe('Atlassian read: get repository details on webapp')
    expect(describeWrite('mcp__atlassian__jira_batch_create_issues', {})).toBe('Atlassian create: batch create issues')
    expect(describeWrite('mcp__atlassian__jira_batch_create_issues', 'not an object')).toBe('Atlassian create: batch create issues')
  })
})
