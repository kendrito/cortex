// @vitest-environment jsdom
// The activity feed: newest-first rows with a jump target when the entry
// names an entity, a failure chip for failed calls, and an empty state.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import type { ActivityEntry } from '@cortex/atlassian/client'
import { ActivityTab } from '../src/client/panel/ActivityTab.tsx'
import { NOW, t } from './support.client.ts'

afterEach(cleanup)

const entries: ActivityEntry[] = [
  {
    id: 'a1', at: NOW - 20_000, kind: 'transition', tool: 'mcp__atlassian__jira_transition_issue',
    entity: { kind: 'issue', key: 'PROJ-123' }, summary: 'Transitioned PROJ-123', ok: true, callId: 'c1',
  },
  { id: 'a2', at: NOW - 3_600_000 * 2, kind: 'search', tool: 'mcp__atlassian__jira_search', summary: 'Searched project = PROJ', ok: true },
  {
    id: 'a3', at: NOW - 86_400_000 * 3, kind: 'merge', tool: 'mcp__bitbucket__bitbucket_merge_pull_request',
    entity: { kind: 'pr', key: 'PROJ/webapp#42' }, summary: 'Merged webapp#42 — failed', ok: false,
  },
]

describe('ActivityTab', () => {
  it('shows the empty state without entries', () => {
    render(<ActivityTab activity={[]} now={NOW} t={t} onSelect={vi.fn()} />)
    expect(screen.getByText('No Atlassian activity in this session yet.')).toBeTruthy()
  })

  it('renders rows with stripped tool names, relative times, jump buttons, and the failure chip', () => {
    const onSelect = vi.fn()
    render(<ActivityTab activity={entries} now={NOW} t={t} onSelect={onSelect} />)
    expect(screen.getByText('jira_transition_issue')).toBeTruthy()
    expect(screen.getByText('· just now')).toBeTruthy()
    expect(screen.getByText('· 2h ago')).toBeTruthy()
    expect(screen.getByText('· 3d ago')).toBeTruthy()
    // An entry without an entity is plain text (no button).
    expect(screen.getByText('Searched project = PROJ').tagName).toBe('SPAN')
    expect(screen.getByText('failed')).toBeTruthy()
    fireEvent.click(screen.getByRole('button', { name: 'Transitioned PROJ-123' }))
    expect(onSelect).toHaveBeenCalledWith({ kind: 'issue', key: 'PROJ-123' })
    fireEvent.click(screen.getByRole('button', { name: 'Merged webapp#42 — failed' }))
    expect(onSelect).toHaveBeenLastCalledWith({ kind: 'pr', key: 'PROJ/webapp#42' })
  })
})
