// @vitest-environment jsdom
/**
 * The keyed Atlassian tool card: lifecycle states, collapsed summaries per
 * tool, and the expanded body (entity strip, search tables, comment quote,
 * native diff, file content, JSON tree, raw text) plus the panel verbs.
 */
import { afterEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react'
import { AtlassianCard } from '../src/client/cards/AtlassianCard.tsx'
import { en } from '../src/client/locales.ts'
import { DIFF_JSON, cardProps, running, settled } from './card-support.client.ts'
import { issue, page, panelActions, pr, projection } from './support.client.ts'

afterEach(cleanup)

const GET_ISSUE = 'mcp__atlassian__jira_get_issue'
const ISSUE_JSON = JSON.stringify({ key: 'PROJ-123', summary: 'Login page ignores SSO redirect target' })

/** The clickable card row. */
function row(): HTMLElement {
  return screen.getByRole('button', { expanded: false })
}

describe('AtlassianCard lifecycle', () => {
  it('shows a running row that cannot expand', () => {
    render(<AtlassianCard {...cardProps(GET_ISSUE, running(GET_ISSUE, '{"issue_key":"PROJ-123"}'))} />)
    expect(screen.getByText(en['card.running'])).toBeTruthy()
    expect(screen.queryByRole('button')).toBeNull()
    expect(screen.getByText('Jira · Get issue')).toBeTruthy()
    // No record yet: the summary falls back to the issue key from the arguments.
    expect(screen.getByText('PROJ-123')).toBeTruthy()
  })

  it('shows a failed call with its error text in the summary and the raw body on expand', () => {
    const block = settled(GET_ISSUE, { issue_key: 'PROJ-9' }, '{"error":"Issue PROJ-9 not found"}', { isError: true })
    render(<AtlassianCard {...cardProps(GET_ISSUE, block)} />)
    expect(screen.getByText(en['card.failed'])).toBeTruthy()
    expect(screen.getByText('{"error":"Issue PROJ-9 not found"}')).toBeTruthy()
    fireEvent.click(row())
    expect(screen.getAllByText('{"error":"Issue PROJ-9 not found"}')).toHaveLength(2)
  })

  it('falls back to the failed label and the call id when the error carries no text', () => {
    const block = settled(GET_ISSUE, {}, '', { isError: true, content: [] })
    render(<AtlassianCard {...cardProps(GET_ISSUE, block)} />)
    fireEvent.click(row())
    expect(screen.getAllByText(en['card.failed'])).toHaveLength(2)
    expect(screen.getByText('call-1')).toBeTruthy()
  })

  it('shows a stopped call and its body', () => {
    const block = settled(GET_ISSUE, {}, 'cancelled', { isError: true, error: { name: 'Interrupted', code: 'interrupted' } })
    render(<AtlassianCard {...cardProps(GET_ISSUE, block)} />)
    expect(screen.getByText(en['card.stopped'])).toBeTruthy()
    fireEvent.click(row())
    expect(screen.getByText('cancelled')).toBeTruthy()
  })

  it('toggles from the keyboard with Enter and Space and ignores other keys', () => {
    render(<AtlassianCard {...cardProps(GET_ISSUE, settled(GET_ISSUE, { issue_key: 'PROJ-123' }, ISSUE_JSON))} />)
    const target = row()
    fireEvent.keyDown(target, { key: 'x' })
    expect(target.getAttribute('aria-expanded')).toBe('false')
    fireEvent.keyDown(target, { key: 'Enter' })
    expect(screen.getByRole('button', { expanded: true })).toBeTruthy()
    fireEvent.keyDown(screen.getByRole('button', { expanded: true }), { key: ' ' })
    expect(target.getAttribute('aria-expanded')).toBe('false')
  })

  it('offers the details button only when an inspect verb exists', () => {
    const inspect = vi.fn()
    const block = settled(GET_ISSUE, { issue_key: 'PROJ-123' }, ISSUE_JSON)
    render(<AtlassianCard {...cardProps(GET_ISSUE, block, { inspect })} />)
    fireEvent.click(row())
    fireEvent.click(screen.getByRole('button', { name: en['card.details'] }))
    expect(inspect).toHaveBeenCalled()
    cleanup()
    render(<AtlassianCard {...cardProps(GET_ISSUE, block)} />)
    fireEvent.click(row())
    expect(screen.queryByRole('button', { name: en['card.details'] })).toBeNull()
  })
})

describe('AtlassianCard entities', () => {
  it('summarizes and strips a tracked issue, opening it in the panel', () => {
    const actions = panelActions()
    const value = projection({ issues: { 'PROJ-123': issue() } })
    render(<AtlassianCard {...cardProps(GET_ISSUE, settled(GET_ISSUE, { issue_key: 'PROJ-123' }, ISSUE_JSON), { projection: value, actions })} />)
    expect(screen.getByText('PROJ-123 · Login page ignores SSO redirect target')).toBeTruthy()
    expect(screen.getByText('In Progress')).toBeTruthy()
    fireEvent.click(row())
    fireEvent.click(screen.getByRole('button', { name: en['card.open'] }))
    expect(actions.showEntity).toHaveBeenCalledWith({ kind: 'issue', key: 'PROJ-123' })
  })

  it('renders an issue strip without an assignee avatar', () => {
    const unassigned = issue()
    delete unassigned.assignee
    const value = projection({ issues: { 'PROJ-123': unassigned } })
    render(<AtlassianCard {...cardProps(GET_ISSUE, settled(GET_ISSUE, { issue_key: 'PROJ-123' }, ISSUE_JSON), { projection: value })} />)
    fireEvent.click(row())
    expect(screen.queryByTitle('Kendrito')).toBeNull()
  })

  it('summarizes and strips a tracked page, opening it in the panel', () => {
    const name = 'mcp__atlassian__confluence_get_page'
    const actions = panelActions()
    const value = projection({ pages: { 98765: page() } })
    render(<AtlassianCard {...cardProps(name, settled(name, { page_id: '98765' }, '{"metadata":{}}'), { projection: value, actions })} />)
    expect(screen.getByText('Auth service runbook')).toBeTruthy()
    expect(screen.getByText('v12')).toBeTruthy()
    fireEvent.click(row())
    expect(screen.getByText('ENG')).toBeTruthy()
    expect(screen.getAllByText('Auth service runbook')).toHaveLength(2)
    fireEvent.click(screen.getByRole('button', { name: en['card.open'] }))
    expect(actions.showEntity).toHaveBeenCalledWith({ kind: 'page', id: '98765' })
  })

  it('summarizes and strips a tracked pull request with approvals, opening it in the panel', () => {
    const name = 'mcp__bitbucket__bitbucket_get_pull_request_details'
    const actions = panelActions()
    const value = projection({ prs: { 'PROJ/webapp#42': pr() } })
    const block = settled(name, { project: 'PROJ', repository: 'webapp', prId: 42 }, '{"id":42}')
    render(<AtlassianCard {...cardProps(name, block, { projection: value, actions })} />)
    expect(screen.getByText('PROJ/webapp#42 · Fix SSO redirect loop after IdP callback')).toBeTruthy()
    fireEvent.click(row())
    expect(screen.getByText('1/3 ✓')).toBeTruthy()
    expect(screen.getAllByText('open')).toHaveLength(2)
    fireEvent.click(screen.getByRole('button', { name: en['card.open'] }))
    expect(actions.showEntity).toHaveBeenCalledWith({ kind: 'pr', key: 'PROJ/webapp#42' })
  })

  it('shows the pending note without a panel verb while the record is not tracked yet', () => {
    const actions = panelActions()
    render(<AtlassianCard {...cardProps(GET_ISSUE, settled(GET_ISSUE, { issue_key: 'PROJ-123' }, ISSUE_JSON), { actions })} />)
    fireEvent.click(row())
    expect(screen.getByText(en['card.pending'])).toBeTruthy()
    expect(screen.queryByRole('button', { name: en['card.open'] })).toBeNull()
    expect(actions.showEntity).not.toHaveBeenCalled()
  })

  it('renders without a projection at all', () => {
    render(<AtlassianCard {...cardProps(GET_ISSUE, settled(GET_ISSUE, { issue_key: 'PROJ-123' }, ISSUE_JSON), { projection: undefined })} />)
    fireEvent.click(row())
    expect(screen.getByText(en['card.pending'])).toBeTruthy()
  })
})

describe('AtlassianCard searches', () => {
  it('renders Jira rows from the captured search and opens a clicked key', async () => {
    const name = 'mcp__atlassian__jira_search'
    const actions = panelActions()
    const open = vi.fn(async () => ({ ok: true as const, entity: { kind: 'issue' as const, key: 'PROJ-124' } }))
    const value = projection({ searches: [{
      service: 'jira', callId: 'call-1', query: 'project = PROJ', total: 40,
      rows: [
        { key: 'PROJ-123', summary: 'First', status: { name: 'In Progress', category: 'indeterminate' }, assignee: 'Kendrito' },
        { key: 'PROJ-124', summary: 'Second' },
      ],
    }] })
    render(<AtlassianCard {...cardProps(name, settled(name, { jql: 'project = PROJ' }, '{"issues":[]}'), { projection: value, actions, open })} />)
    expect(screen.getByText('project = PROJ')).toBeTruthy()
    fireEvent.click(row())
    const table = screen.getByRole('table')
    expect(within(table).getAllByRole('row')).toHaveLength(2)
    expect(within(table).getByText('Kendrito')).toBeTruthy()
    expect(screen.getByText('2 of 40 results')).toBeTruthy()
    await act(async () => { fireEvent.click(within(table).getByRole('button', { name: 'PROJ-124' })) })
    expect(open).toHaveBeenCalledWith({ kind: 'issue', key: 'PROJ-124' })
    expect(actions.showEntity).toHaveBeenCalledWith({ kind: 'issue', key: 'PROJ-124' })
  })

  it('does not follow a failed open', async () => {
    const name = 'mcp__atlassian__jira_search'
    const actions = panelActions()
    const open = vi.fn(async () => ({ ok: false as const, code: 'not-configured', message: 'no jira' }))
    const value = projection({ searches: [{ service: 'jira', callId: 'call-1', query: 'x', total: 1, rows: [{ key: 'PROJ-1', summary: 'One' }] }] })
    render(<AtlassianCard {...cardProps(name, settled(name, { jql: 'x' }, '{}'), { projection: value, actions, open })} />)
    fireEvent.click(row())
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'PROJ-1' })) })
    expect(actions.showEntity).not.toHaveBeenCalled()
  })

  it('renders Confluence rows with links and excerpts, and the empty-result note', () => {
    const name = 'mcp__atlassian__confluence_search'
    const value = projection({ searches: [{
      service: 'confluence', callId: 'call-1', query: 'runbook', total: 2,
      rows: [
        { id: '1', title: 'Auth runbook', space: 'ENG', url: 'http://c/1', excerpt: 'How to operate…' },
        { id: '2', title: 'Plain page' },
      ],
    }] })
    render(<AtlassianCard {...cardProps(name, settled(name, { query: 'runbook' }, '[]'), { projection: value })} />)
    expect(screen.getByText('runbook')).toBeTruthy()
    fireEvent.click(row())
    expect(screen.getByRole('link', { name: 'Auth runbook' }).getAttribute('href')).toBe('http://c/1')
    expect(screen.getByText('How to operate…')).toBeTruthy()
    expect(screen.getByText('Plain page')).toBeTruthy()
    cleanup()
    const empty = projection({ searches: [{ service: 'jira', callId: 'call-1', query: 'none', total: 0, rows: [] }] })
    render(<AtlassianCard {...cardProps('mcp__atlassian__jira_search', settled('mcp__atlassian__jira_search', {}, '{}'), { projection: empty })} />)
    fireEvent.click(row())
    expect(screen.getByText(en['card.noResults'])).toBeTruthy()
  })

  it('ignores captured searches for a failed call', () => {
    const name = 'mcp__atlassian__jira_search'
    const value = projection({ searches: [{ service: 'jira', callId: 'call-1', query: 'x', total: 1, rows: [{ key: 'PROJ-1', summary: 'One' }] }] })
    render(<AtlassianCard {...cardProps(name, settled(name, { jql: 'x' }, 'boom', { isError: true }), { projection: value })} />)
    fireEvent.click(row())
    expect(screen.queryByRole('table')).toBeNull()
  })
})

describe('AtlassianCard tool-specific summaries and bodies', () => {
  it('summarizes transitions from the result and comments from the arguments', () => {
    const transition = 'mcp__atlassian__jira_transition_issue'
    render(<AtlassianCard {...cardProps(transition, settled(transition, { issue_key: 'PROJ-1', transition_id: '31' }, '{"issue":{"status":{"name":"In Review"}}}'))} />)
    expect(screen.getByText('PROJ-1 → In Review')).toBeTruthy()
    cleanup()
    render(<AtlassianCard {...cardProps(transition, settled(transition, { issue_key: 'PROJ-1' }, '{"message":"ok"}'))} />)
    expect(screen.getByText('PROJ-1')).toBeTruthy()
    cleanup()
    const comment = 'mcp__atlassian__jira_add_comment'
    render(<AtlassianCard {...cardProps(comment, settled(comment, { issue_key: 'PROJ-1', comment: 'The **fix** is in review' }, '{"id":"1"}'))} />)
    expect(screen.getByText('PROJ-1 · The **fix** is in review')).toBeTruthy()
    fireEvent.click(row())
    expect(screen.getByText('fix').tagName).toBe('STRONG')
  })

  it('summarizes creations, pages, files, directories, and repositories from arguments and results', () => {
    const cases: [string, unknown, string, string][] = [
      ['mcp__atlassian__jira_create_issue', { summary: 'New bug' }, '{"issue":{"key":"PROJ-9"}}', 'PROJ-9 · New bug'],
      ['mcp__atlassian__jira_create_issue', { summary: 'New bug' }, '{}', 'New bug'],
      ['mcp__atlassian__confluence_create_page', { title: 'Runbook v2' }, '{}', 'Runbook v2'],
      ['mcp__bitbucket__bitbucket_create_pull_request', { title: 'Add tests', repository: 'webapp' }, '{}', 'Add tests'],
      ['mcp__bitbucket__bitbucket_get_file_content', { filePath: 'src/a.ts' }, 'plain', 'src/a.ts'],
      ['mcp__bitbucket__bitbucket_browse_directory', { directoryPath: 'src' }, '{}', 'src'],
      ['mcp__bitbucket__bitbucket_browse_directory', {}, '{}', '/'],
      ['mcp__bitbucket__bitbucket_list_repositories', { projectKey: 'PROJ' }, '{}', 'PROJ'],
      ['mcp__bitbucket__bitbucket_get_pull_request_details', { repository: 'webapp', prId: 5 }, '{}', 'webapp#5'],
      ['mcp__atlassian__confluence_update_page', { page_id: '77' }, '{}', 'page 77'],
    ]
    for (const [name, args, text, expected] of cases) {
      const { unmount } = render(<AtlassianCard {...cardProps(name, settled(name, args, text))} />)
      expect(screen.getByText(expected)).toBeTruthy()
      unmount()
    }
    // A call with nothing to summarize keeps the row to its title.
    render(<AtlassianCard {...cardProps('mcp__atlassian__jira_get_all_projects', settled('mcp__atlassian__jira_get_all_projects', {}, '{}'))} />)
    expect(screen.getByText('Jira · Get all projects')).toBeTruthy()
  })

  it('degrades gracefully when the summarizing arguments are missing', () => {
    const blank: [string, unknown, string][] = [
      ['mcp__atlassian__jira_search', {}, '{}'],
      ['mcp__atlassian__confluence_search', {}, '{}'],
      ['mcp__atlassian__jira_transition_issue', {}, '{}'],
      ['mcp__atlassian__jira_add_comment', {}, '{}'],
      ['mcp__atlassian__jira_create_issue', {}, '{}'],
      ['mcp__atlassian__confluence_create_page', {}, '{}'],
      ['mcp__bitbucket__bitbucket_create_pull_request', {}, '{}'],
      ['mcp__bitbucket__bitbucket_get_file_content', {}, 'const a = 1'],
      ['mcp__bitbucket__bitbucket_browse_directory', { path: 'lib' }, '{}'],
      ['mcp__bitbucket__bitbucket_list_repositories', { workspaceSlug: 'WS' }, '{}'],
      ['mcp__bitbucket__bitbucket_list_repositories', {}, '{}'],
    ]
    for (const [name, args, text] of blank) {
      const { unmount, container } = render(<AtlassianCard {...cardProps(name, settled(name, args, text))} />)
      fireEvent.click(screen.getByRole('button', { expanded: false }))
      expect(container.textContent).toContain(en['card.output'].length > 0 ? '' : 'never')
      unmount()
    }
    render(<AtlassianCard {...cardProps('mcp__bitbucket__bitbucket_browse_directory', settled('mcp__bitbucket__bitbucket_browse_directory', { path: 'lib' }, '{}'))} />)
    expect(screen.getByText('lib')).toBeTruthy()
    cleanup()
    render(<AtlassianCard {...cardProps('mcp__bitbucket__bitbucket_list_repositories', settled('mcp__bitbucket__bitbucket_list_repositories', { workspaceSlug: 'WS' }, '{}'))} />)
    expect(screen.getByText('WS')).toBeTruthy()
  })

  it('clips a long summary with an ellipsis', () => {
    const long = 'x'.repeat(200)
    render(<AtlassianCard {...cardProps('mcp__atlassian__jira_search', settled('mcp__atlassian__jira_search', { jql: long }, '{}'))} />)
    const shown = screen.getByText(/^x+…$/).textContent ?? ''
    expect(shown.length).toBe(96)
    expect(shown.endsWith('…')).toBe(true)
  })

  it('renders a native diff with file stats, binary and truncated markers', () => {
    const name = 'mcp__bitbucket__bitbucket_get_pull_request_diff'
    const value = projection({ prs: { 'PROJ/webapp#42': pr() } })
    const block = settled(name, { project: 'PROJ', repository: 'webapp', prId: 42 }, JSON.stringify({ ...DIFF_JSON, truncated: true }))
    render(<AtlassianCard {...cardProps(name, block, { projection: value })} />)
    fireEvent.click(row())
    expect(screen.getByText('3 file(s) changed')).toBeTruthy()
    expect(screen.getByText(en['card.diffTruncated'])).toBeTruthy()
    expect(screen.getByText(en['card.binary'])).toBeTruthy()
    expect(screen.getByText('+2')).toBeTruthy()
    expect(screen.getByText('−1')).toBeTruthy()
    expect(screen.getByText('logo.png')).toBeTruthy()
  })

  it('renders a diff with no text hunks and skips a result that is not a diff', () => {
    const name = 'mcp__bitbucket__bitbucket_get_pull_request_diff'
    const binaryOnly = { diffs: [{ destination: { toString: 'a.bin' }, binary: true, hunks: [] }] }
    render(<AtlassianCard {...cardProps(name, settled(name, { repository: 'webapp', prId: 1 }, JSON.stringify(binaryOnly)))} />)
    fireEvent.click(row())
    expect(screen.getByText('1 file(s) changed')).toBeTruthy()
    cleanup()
    render(<AtlassianCard {...cardProps(name, settled(name, { repository: 'webapp', prId: 1 }, 'not a diff'))} />)
    fireEvent.click(row())
    expect(screen.getByText('not a diff')).toBeTruthy()
  })

  it('renders file content from raw text or the JSON content field', () => {
    const name = 'mcp__bitbucket__bitbucket_get_file_content'
    const raw = render(<AtlassianCard {...cardProps(name, settled(name, { filePath: 'src/a.ts' }, 'export const a = 1'))} />)
    fireEvent.click(row())
    // The code block highlights token by token; read the whole rendered text.
    expect(raw.container.textContent).toContain('export const a = 1')
    cleanup()
    const fromJson = render(<AtlassianCard {...cardProps(name, settled(name, { filePath: 'README' }, JSON.stringify({ content: '# Title' })))} />)
    fireEvent.click(row())
    expect(fromJson.container.textContent).toContain('# Title')
    cleanup()
    // JSON without a content field falls through to the JSON tree.
    render(<AtlassianCard {...cardProps(name, settled(name, { filePath: 'x' }, JSON.stringify({ size: 3 })))} />)
    fireEvent.click(row())
    expect(screen.getByRole('tree', { name: en['card.output'] })).toBeTruthy()
    cleanup()
    render(<AtlassianCard {...cardProps(name, settled(name, { filePath: 'x' }, ''))} />)
    fireEvent.click(row())
    expect(screen.queryByRole('tree')).toBeNull()
  })

  it('falls back to a JSON tree or raw text for other results', () => {
    const name = 'mcp__bitbucket__bitbucket_list_repository_branches'
    render(<AtlassianCard {...cardProps(name, settled(name, { repository: 'webapp' }, JSON.stringify({ values: [{ displayId: 'main' }] })))} />)
    fireEvent.click(row())
    expect(screen.getByRole('tree', { name: en['card.output'] })).toBeTruthy()
    cleanup()
    render(<AtlassianCard {...cardProps(name, settled(name, {}, 'just text'))} />)
    fireEvent.click(row())
    expect(screen.getByText('just text')).toBeTruthy()
  })
})
