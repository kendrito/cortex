import { describe, expect, it } from 'vitest'
import {
  ATLASSIAN_PREFIX, BITBUCKET_PREFIX, SEARCH_ROW_LIMIT, activitySummary, classifyTool, isAtlassianTool, isAtlassianWrite,
  rawToolName, searchRecord, statusCategory, touchedEntities,
} from '../src/tools.ts'

describe('tool names', () => {
  it('recognizes both mounts and strips their prefixes', () => {
    expect(isAtlassianTool(`${ATLASSIAN_PREFIX}jira_get_issue`)).toBe(true)
    expect(isAtlassianTool(`${BITBUCKET_PREFIX}bitbucket_merge_pull_request`)).toBe(true)
    expect(isAtlassianTool('bash')).toBe(false)
    expect(rawToolName(`${ATLASSIAN_PREFIX}jira_get_issue`)).toBe('jira_get_issue')
    expect(rawToolName(`${BITBUCKET_PREFIX}bitbucket_get_file_content`)).toBe('bitbucket_get_file_content')
    expect(rawToolName('atlassian_review_finding')).toBe('atlassian_review_finding')
  })
})

describe('classifyTool', () => {
  it.each([
    ['jira_get_issue', 'read'], ['jira_get_transitions', 'read'], ['jira_get_worklog', 'read'], ['jira_batch_get_changelogs', 'read'],
    ['jira_download_attachments', 'read'], ['jira_get_issue_development_info', 'read'],
    ['confluence_get_page', 'read'], ['confluence_get_comments', 'read'], ['confluence_check_content_permissions', 'read'],
    ['bitbucket_get_pull_request_details', 'read'], ['bitbucket_get_file_content', 'read'],
    ['jira_search', 'search'], ['jira_search_fields', 'search'], ['jira_get_project_issues', 'search'], ['jira_get_board_issues', 'search'],
    ['jira_get_sprint_issues', 'search'], ['jira_get_agile_boards', 'search'], ['jira_get_sprints_from_board', 'search'],
    ['confluence_search', 'search'], ['confluence_list_page_templates', 'search'],
    ['bitbucket_list_repositories', 'search'], ['bitbucket_search_content', 'search'], ['bitbucket_browse_directory', 'search'],
  ] as const)('reads %s as a %s', (raw, kind) => {
    expect(classifyTool(raw)).toEqual({ kind, write: false })
  })

  it.each([
    ['jira_transition_issue', 'transition'], ['jira_add_comment', 'comment'], ['jira_edit_comment', 'comment'],
    ['confluence_add_inline_comment', 'comment'], ['confluence_reply_to_comment', 'comment'],
    ['bitbucket_add_pull_request_comment', 'comment'], ['bitbucket_add_pull_request_file_line_comment', 'comment'],
    ['jira_assign_issue', 'assign'], ['jira_add_watcher', 'assign'], ['jira_remove_watcher', 'assign'],
    ['jira_link_to_epic', 'link'], ['jira_create_issue_link', 'link'], ['jira_remove_issue_link', 'link'],
    ['jira_move_issues_to_backlog', 'link'], ['jira_add_issues_to_sprint', 'link'],
    ['bitbucket_approve_pull_request', 'approve'], ['bitbucket_merge_pull_request', 'merge'], ['bitbucket_decline_pull_request', 'decline'],
    ['bitbucket_create_branch', 'branch'], ['jira_delete_issue', 'delete'], ['confluence_delete_attachment', 'delete'],
    ['jira_create_issue', 'create'], ['jira_batch_create_issues', 'create'], ['confluence_copy_page', 'create'],
    ['confluence_upload_attachment', 'create'], ['bitbucket_create_pull_request', 'create'],
    ['jira_update_issue', 'update'], ['confluence_move_page', 'update'], ['confluence_set_page_restrictions', 'update'],
    ['confluence_add_label', 'update'], ['jira_add_worklog', 'update'], ['jira_update_sprint', 'update'],
    ['jira_move_issue', 'other'], ['jira_create_customer_request', 'create'], ['some_future_tool', 'other'],
  ] as const)('classifies %s as a %s write', (raw, kind) => {
    expect(classifyTool(raw)).toEqual({ kind, write: true })
  })

  it('exposes the write flag through the wire name', () => {
    expect(isAtlassianWrite(`${ATLASSIAN_PREFIX}jira_transition_issue`)).toBe(true)
    expect(isAtlassianWrite(`${ATLASSIAN_PREFIX}jira_get_issue`)).toBe(false)
    expect(isAtlassianWrite('bash')).toBe(false)
  })
})

describe('touchedEntities', () => {
  it('reads Jira issue keys from arguments and creation results', () => {
    expect(touchedEntities('jira_get_issue', { issue_key: 'proj-1' })).toEqual([{ kind: 'issue', key: 'PROJ-1' }])
    expect(touchedEntities('jira_get_issue', { issue_key: 'nonsense' })).toEqual([])
    expect(touchedEntities('jira_get_issue', 'not-an-object')).toEqual([])
    expect(touchedEntities('jira_create_issue', {}, JSON.stringify({ message: 'ok', issue: { key: 'PROJ-9' } })))
      .toEqual([{ kind: 'issue', key: 'PROJ-9' }])
    expect(touchedEntities('jira_create_issue', {}, undefined)).toEqual([])
    expect(touchedEntities('jira_batch_create_issues', {}, JSON.stringify({ issues: [{ key: 'AB-1' }, { key: 'AB-2' }, {}] })))
      .toEqual([{ kind: 'issue', key: 'AB-1' }, { kind: 'issue', key: 'AB-2' }])
    expect(touchedEntities('jira_batch_create_issues', {}, JSON.stringify({ issues: 'nope' }))).toEqual([])
    expect(touchedEntities('jira_move_issue', { issue_key: 'AB-1' }, JSON.stringify({ issue: { key: 'CD-7' } })))
      .toEqual([{ kind: 'issue', key: 'CD-7' }, { kind: 'issue', key: 'AB-1' }])
    expect(touchedEntities('jira_move_issue', { issue_key: 'AB-1' }, JSON.stringify({ new_key: 'CD-8' })))
      .toEqual([{ kind: 'issue', key: 'CD-8' }, { kind: 'issue', key: 'AB-1' }])
    expect(touchedEntities('jira_move_issue', { issue_key: 'AB-1' }, JSON.stringify({ key: 'CD-9' })))
      .toEqual([{ kind: 'issue', key: 'CD-9' }, { kind: 'issue', key: 'AB-1' }])
    expect(touchedEntities('jira_move_issue', { issue_key: 'AB-1' }, 'garbage')).toEqual([{ kind: 'issue', key: 'AB-1' }])
    expect(touchedEntities('jira_create_issue_link', { inward_issue_key: 'AB-1', outward_issue_key: 'AB-2' }))
      .toEqual([{ kind: 'issue', key: 'AB-1' }, { kind: 'issue', key: 'AB-2' }])
    expect(touchedEntities('jira_link_to_epic', { issue_key: 'AB-1', epic_key: 'AB-1' })).toEqual([{ kind: 'issue', key: 'AB-1' }])
    expect(touchedEntities('jira_link_to_epic', { issue_key: 'AB-1', epic_key: 'EP-1' }))
      .toEqual([{ kind: 'issue', key: 'AB-1' }, { kind: 'issue', key: 'EP-1' }])
  })

  it('reads Confluence page addresses by id, by space + title, and from creations', () => {
    expect(touchedEntities('confluence_get_page', { page_id: 123 })).toEqual([{ kind: 'page', id: '123' }])
    expect(touchedEntities('confluence_get_page_children', { parent_id: '55' })).toEqual([{ kind: 'page', id: '55' }])
    expect(touchedEntities('confluence_get_page', { title: 'Runbook', space_key: 'ENG' }))
      .toEqual([{ kind: 'page', space: 'ENG', title: 'Runbook' }])
    expect(touchedEntities('confluence_get_page', { title: 'Runbook' })).toEqual([])
    expect(touchedEntities('confluence_delete_page', { page_id: '1' })).toEqual([])
    expect(touchedEntities('confluence_create_page', {}, JSON.stringify({ message: 'ok', page: { id: '77' } })))
      .toEqual([{ kind: 'page', id: '77' }])
    expect(touchedEntities('confluence_copy_page', {}, JSON.stringify({ id: '78' }))).toEqual([{ kind: 'page', id: '78' }])
    expect(touchedEntities('confluence_create_page_from_template', {}, 'Created!')).toEqual([])
    expect(touchedEntities('confluence_create_page', {}, 'Created page: {"page": {"id": "79"}}')).toEqual([{ kind: 'page', id: '79' }])
    expect(touchedEntities('confluence_create_page', {}, 'prose { not json')).toEqual([])
    expect(touchedEntities('confluence_create_page', {}, '   ')).toEqual([])
  })

  it('reads Bitbucket pull request addresses from both argument vocabularies and creations', () => {
    expect(touchedEntities('bitbucket_get_pull_request_details', { project: 'PROJ', repository: 'webapp', prId: 42 }))
      .toEqual([{ kind: 'pr', project: 'PROJ', repo: 'webapp', id: 42 }])
    expect(touchedEntities('bitbucket_add_pull_request_file_line_comment', { workspaceSlug: 'PROJ', repoSlug: 'webapp', prId: '7' }))
      .toEqual([{ kind: 'pr', project: 'PROJ', repo: 'webapp', id: 7 }])
    expect(touchedEntities('bitbucket_get_pull_request_diff', { repository: 'webapp', prId: 3 }))
      .toEqual([{ kind: 'pr', repo: 'webapp', id: 3 }])
    expect(touchedEntities('bitbucket_get_pull_request_diff', { repository: 'webapp', prId: 0 })).toEqual([])
    expect(touchedEntities('bitbucket_get_pull_request_diff', { repository: 'webapp', prId: 'x' })).toEqual([])
    expect(touchedEntities('bitbucket_list_repositories', {})).toEqual([])
    expect(touchedEntities('bitbucket_create_pull_request', { project: 'PROJ', repository: 'webapp' }, JSON.stringify({ id: 9 })))
      .toEqual([{ kind: 'pr', project: 'PROJ', repo: 'webapp', id: 9 }])
    expect(touchedEntities('bitbucket_create_pull_request', { repository: 'webapp' }, JSON.stringify({ id: 9 })))
      .toEqual([{ kind: 'pr', repo: 'webapp', id: 9 }])
    expect(touchedEntities('bitbucket_create_pull_request', { repository: 'webapp' }, undefined)).toEqual([])
    expect(touchedEntities('bitbucket_create_pull_request', {}, JSON.stringify({ id: 9 }))).toEqual([])
    expect(touchedEntities('unknown_tool', {})).toEqual([])
  })
})

describe('searchRecord', () => {
  const issues = [
    {
      key: 'A-1', summary: 'First', status: { name: 'Done', category: 'Done' }, issue_type: { name: 'Bug' },
      priority: { name: 'High' }, assignee: { display_name: 'Kendrito' }, updated: '2026-08-18',
    },
    { key: 'A-2', summary: 'Second', assignee: { display_name: 'Unassigned' }, status: { name: 'Odd' } },
    { summary: 'no key' },
    'not an object',
  ]

  it('captures Jira issue lists with the query, total, and compact rows', () => {
    const record = searchRecord('jira_search', { jql: 'project = A' }, JSON.stringify({ total: 12, issues }), 'c1')
    expect(record).toEqual({
      service: 'jira',
      callId: 'c1',
      query: 'project = A',
      total: 12,
      rows: [
        { key: 'A-1', summary: 'First', status: { name: 'Done', category: 'done' }, type: 'Bug', priority: 'High', assignee: 'Kendrito', updated: '2026-08-18' },
        { key: 'A-2', summary: 'Second', status: { name: 'Odd', category: 'unknown' } },
      ],
    })
    expect(searchRecord('jira_get_sprint_issues', { sprint_id: 42 }, JSON.stringify({ issues: [] }), 'c2'))
      .toEqual({ service: 'jira', callId: 'c2', query: '42', total: 0, rows: [] })
    expect(searchRecord('jira_get_board_issues', { board_id: 3 }, JSON.stringify({ issues: [] }), 'c3')?.query).toBe('3')
    expect(searchRecord('jira_get_project_issues', { project_key: 'A' }, JSON.stringify({ issues: [] }), 'c4')?.query).toBe('A')
    expect(searchRecord('jira_get_project_issues', {}, JSON.stringify({ issues: [] }), 'c5')?.query).toBe('jira_get_project_issues')
    expect(searchRecord('jira_search', {}, JSON.stringify({ nope: true }), 'c6')).toBeUndefined()
    expect(searchRecord('jira_search', {}, 'not json', 'c7')).toBeUndefined()
  })

  it('caps rows at the bound', () => {
    const many = Array.from({ length: SEARCH_ROW_LIMIT + 5 }, (_, index) => ({ key: `A-${String(index)}`, summary: 's' }))
    const record = searchRecord('jira_search', { jql: 'x' }, JSON.stringify({ issues: many }), 'c8')
    expect(record?.rows).toHaveLength(SEARCH_ROW_LIMIT)
    expect(record?.total).toBe(SEARCH_ROW_LIMIT)
  })

  it('captures Confluence search arrays and ignores other tools', () => {
    const pages = [
      { id: '1', title: 'One', space: { key: 'ENG' }, url: 'http://c/1', updated: '2026-08-01', excerpt: 'a  b\n c' },
      { id: '2', title: 'Two', content: { value: 'x'.repeat(300) } },
      { title: 'no id' },
    ]
    const record = searchRecord('confluence_search', { query: 'runbook' }, JSON.stringify(pages), 'c9')
    expect(record).toEqual({
      service: 'confluence',
      callId: 'c9',
      query: 'runbook',
      total: 2,
      rows: [
        { id: '1', title: 'One', space: 'ENG', url: 'http://c/1', updated: '2026-08-01', excerpt: 'a b c' },
        { id: '2', title: 'Two', excerpt: 'x'.repeat(240) },
      ],
    })
    expect(searchRecord('confluence_search', {}, JSON.stringify({ not: 'array' }), 'c10')).toBeUndefined()
    expect(searchRecord('confluence_search', {}, JSON.stringify([]), 'c11')?.query).toBe('')
    expect(searchRecord('jira_get_issue', {}, '{}', 'c12')).toBeUndefined()
  })
})

describe('statusCategory', () => {
  it('maps category names and keys', () => {
    expect(statusCategory('Done')).toBe('done')
    expect(statusCategory('complete')).toBe('done')
    expect(statusCategory('In Progress')).toBe('indeterminate')
    expect(statusCategory('indeterminate')).toBe('indeterminate')
    expect(statusCategory('To Do')).toBe('new')
    expect(statusCategory('new')).toBe('new')
    expect(statusCategory('undefined')).toBe('new')
    expect(statusCategory('Something')).toBe('unknown')
    expect(statusCategory(undefined)).toBe('unknown')
  })
})

describe('activitySummary', () => {
  const issue = { kind: 'issue' as const, key: 'A-1' }
  const page = { kind: 'page' as const, id: '9' }
  const titled = { kind: 'page' as const, space: 'ENG', title: 'Runbook' }
  const pr = { kind: 'pr' as const, project: 'P', repo: 'r', id: 4 }

  it('names the operation and its target for every kind', () => {
    expect(activitySummary('jira_transition_issue', {}, [issue], true)).toBe('Transitioned A-1')
    expect(activitySummary('jira_add_comment', {}, [issue], false)).toBe('Commented on A-1 — failed')
    expect(activitySummary('jira_assign_issue', { assignee: 'me' }, [issue], true)).toBe('Assigned A-1 to me')
    expect(activitySummary('jira_assign_issue', {}, [issue], true)).toBe('Assigned A-1')
    expect(activitySummary('jira_create_issue_link', {}, [issue, { kind: 'issue', key: 'A-2' }], true)).toBe('Linked A-1 ↔ A-2')
    expect(activitySummary('jira_link_to_epic', {}, [], true)).toBe('Linked ')
    expect(activitySummary('bitbucket_approve_pull_request', {}, [pr], true)).toBe('Approved r#4')
    expect(activitySummary('bitbucket_merge_pull_request', {}, [pr], true)).toBe('Merged r#4')
    expect(activitySummary('bitbucket_decline_pull_request', {}, [pr], true)).toBe('Declined r#4')
    expect(activitySummary('bitbucket_create_branch', { branchName: 'feat' }, [], true)).toBe('Created branch feat')
    expect(activitySummary('bitbucket_create_branch', { name: 'feat2' }, [], true)).toBe('Created branch feat2')
    expect(activitySummary('bitbucket_create_branch', {}, [], true)).toBe('Created branch ')
    expect(activitySummary('jira_delete_issue', {}, [issue], true)).toBe('Deleted A-1')
    expect(activitySummary('confluence_delete_page', {}, [], true)).toBe('Deleted confluence delete page')
    expect(activitySummary('jira_create_issue', {}, [issue], true)).toBe('Created A-1')
    expect(activitySummary('confluence_create_page', {}, [], true)).toBe('Created page')
    expect(activitySummary('confluence_update_page', {}, [page], true)).toBe('Updated page 9')
    expect(activitySummary('confluence_update_page', {}, [titled], true)).toBe('Updated "Runbook"')
    expect(activitySummary('confluence_update_page', {}, [{ kind: 'page' }], true)).toBe('Updated ""')
    expect(activitySummary('jira_search', { jql: 'project = A' }, [], true)).toBe('Searched project = A')
    expect(activitySummary('confluence_search', { query: 'q' }, [], true)).toBe('Searched q')
    expect(activitySummary('bitbucket_list_repositories', {}, [], true)).toBe('Searched bitbucket list repositories')
    expect(activitySummary('jira_get_issue', {}, [issue], true)).toBe('Read A-1')
    expect(activitySummary('bitbucket_get_user_profile', {}, [], true)).toBe('Read bitbucket get user profile')
    expect(activitySummary('jira_move_issue', {}, [], true)).toBe('jira move issue')
  })
})

describe('loose-shape branches', () => {
  it('ignores blank string targets', () => {
    expect(touchedEntities('jira_get_issue', { issue_key: '   ' })).toEqual([])
  })

  it('tolerates search rows without summaries or excerpts', () => {
    const jira = searchRecord('jira_search', { jql: 'x' }, JSON.stringify({ total: 1, issues: [{ key: 'AB-9' }] }), 'c9')
    expect(jira?.rows).toEqual([{ key: 'AB-9', summary: '' }])
    const confluence = searchRecord('confluence_search', { query: 'x' },
      JSON.stringify([{ id: '7', title: 'Bare' }]), 'c10')
    expect(confluence?.rows).toEqual([{ id: '7', title: 'Bare' }])
  })
})
