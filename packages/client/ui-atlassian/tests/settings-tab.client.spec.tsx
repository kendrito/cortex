// @vitest-environment jsdom
// Settings → Plugins → Atlassian: connection status pills, token state,
// probes, field edits saved through the scope, tokens saved to credentials,
// the write policy, and the read-only posture off the host machine.
import { afterEach, describe, expect, it, vi } from 'vitest'
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import type { AtlassianSettings, AtlassianStatus, ProbeResult } from '@cortex/atlassian/client'
import type { SettingsFace, SettingsTabProps } from '../src/client/contract.ts'
import { AtlassianSettingsTab } from '../src/client/settings/AtlassianSettingsTab.tsx'
import { t } from './support.client.ts'

afterEach(() => {
  cleanup()
  vi.useRealTimers()
})

const settings = (overrides: Partial<AtlassianSettings> = {}): AtlassianSettings => ({
  jiraUrl: 'https://jira.example.com',
  jiraTokenRef: 'ATLASSIAN_JIRA_TOKEN',
  jiraProjectsFilter: '',
  confluenceUrl: '',
  confluenceTokenRef: 'ATLASSIAN_CONFLUENCE_TOKEN',
  confluenceSpacesFilter: '',
  bitbucketUrl: 'https://bb.example.com',
  bitbucketTokenRef: 'ATLASSIAN_BITBUCKET_TOKEN',
  bitbucketDefaultProject: 'PROJ',
  atlassianLaunch: 'uvx mcp-atlassian',
  bitbucketLaunch: 'docker run x',
  writes: 'ask',
  toolsets: 'default',
  enabledTools: '',
  ...overrides,
})

const status = (overrides: Partial<AtlassianStatus> = {}): AtlassianStatus => ({
  atlassian: { phase: 'ready', toolCount: 7 },
  bitbucket: { phase: 'off', toolCount: 0, missing: ['url', 'token'] },
  rest: { jira: true, confluence: false, bitbucket: false },
  ...overrides,
})

type Face = {
  [K in keyof SettingsFace]: SettingsFace[K] extends (...args: infer A) => infer R
    ? ReturnType<typeof vi.fn<(...args: A) => R>>
    : SettingsFace[K]
}

function props(overrides: Partial<{ value: AtlassianSettings | undefined; writable: boolean; face: Partial<Face> }> = {}) {
  const value = 'value' in overrides ? overrides.value : settings()
  const face = {
    writable: overrides.writable ?? true,
    setField: vi.fn(() => Promise.resolve({ ok: true as const })),
    describeTokens: vi.fn(() => Promise.resolve({
      ATLASSIAN_JIRA_TOKEN: { configured: true, writable: true },
      ATLASSIAN_BITBUCKET_TOKEN: { configured: false, writable: false },
    })),
    setToken: vi.fn(() => Promise.resolve({ ok: true as const })),
    status: vi.fn(() => Promise.resolve(status())),
    probe: vi.fn((service: 'jira' | 'confluence' | 'bitbucket') => Promise.resolve<ProbeResult>({ service, ok: true, user: 'Kendrito' })),
    reconnect: vi.fn(() => Promise.resolve(status({ atlassian: { phase: 'error', toolCount: 0, error: 'spawn uvx ENOENT' } }))),
    ...overrides.face,
  }
  const p = {
    useSessions: () => { throw new Error('unused') },
    useWorkspaces: () => { throw new Error('unused') },
    useSettings: (selector: (snapshot: unknown) => unknown) => selector({ status: 'ready', value, base: undefined, user: undefined, revision: 1, writable: true, mode: 'host' }),
    t,
    ...face,
  } as unknown as SettingsTabProps
  return { p, face }
}

describe('AtlassianSettingsTab', () => {
  it('shows the pending note before the settings arrive', () => {
    render(<AtlassianSettingsTab {...props({ value: undefined }).p} />)
    expect(screen.getByText('Loading…')).toBeTruthy()
  })

  it('renders status pills, token state, and probes each service', async () => {
    const { p, face } = props()
    render(<AtlassianSettingsTab {...p} />)
    // Jira and Confluence share the atlassian mount, so its pill renders twice.
    await waitFor(() => { expect(screen.getAllByText('Connected · 7 tools').length).toBe(2) })
    expect(screen.getByText('Off · URL missing, token missing')).toBeTruthy()
    expect(face.describeTokens).toHaveBeenCalledWith(['ATLASSIAN_JIRA_TOKEN', 'ATLASSIAN_CONFLUENCE_TOKEN', 'ATLASSIAN_BITBUCKET_TOKEN'])
    await waitFor(() => { expect(screen.getAllByText('Configured').length).toBe(1) })
    expect(screen.getAllByText('Not set').length).toBe(2)
    // The Bitbucket token input is locked when the store says its reference is not writable.
    const passwords = document.querySelectorAll('input[type=password]')
    expect(passwords[2]?.hasAttribute('disabled')).toBe(true)
    expect(passwords[0]?.hasAttribute('disabled')).toBe(false)
    const tests = screen.getAllByRole('button', { name: 'Test connection' })
    fireEvent.click(tests[0]!)
    await screen.findByText('Connected as Kendrito')
    expect(face.probe).toHaveBeenCalledWith('jira')
    face.probe.mockResolvedValueOnce({ service: 'confluence', ok: false, error: 'no url' })
    fireEvent.click(tests[1]!)
    await screen.findByText('Failed: no url')
    face.probe.mockResolvedValueOnce({ service: 'bitbucket', ok: true })
    fireEvent.click(tests[2]!)
    await waitFor(() => { expect(screen.getAllByText(/^Connected as/).length).toBe(2) })
    face.probe.mockResolvedValueOnce({ service: 'confluence', ok: false })
    fireEvent.click(tests[1]!)
    await waitFor(() => { expect(screen.getByText(/^Failed:/)).toBeTruthy() })
  })

  it('shows the testing label while a probe is in flight', async () => {
    let resolve!: (value: ProbeResult) => void
    const { p } = props({ face: { probe: vi.fn(() => new Promise<ProbeResult>((done) => { resolve = done })) } })
    render(<AtlassianSettingsTab {...p} />)
    fireEvent.click(screen.getAllByRole('button', { name: 'Test connection' })[0]!)
    expect(screen.getByRole('button', { name: 'Testing…' }).hasAttribute('disabled')).toBe(true)
    await act(async () => { resolve({ service: 'jira', ok: true, user: 'K' }) })
    expect(screen.getByText('Connected as K')).toBeTruthy()
  })

  it('polls status while a mount is starting and shows the error text of a failed mount', async () => {
    vi.useFakeTimers()
    const statuses = [
      status({ atlassian: { phase: 'starting', toolCount: 0 } }),
      status({ atlassian: { phase: 'error', toolCount: 0, error: 'spawn uvx ENOENT' } }),
    ]
    const { p, face } = props({ face: { status: vi.fn(() => Promise.resolve(statuses.shift() ?? status())) } })
    render(<AtlassianSettingsTab {...p} />)
    await act(async () => { await Promise.resolve() })
    expect(screen.getAllByText('Starting…').length).toBe(2)
    await act(async () => { vi.advanceTimersByTime(2_100); await Promise.resolve() })
    expect(face.status).toHaveBeenCalledTimes(2)
    await act(async () => { await Promise.resolve() })
    expect(screen.getAllByText('Failed').length).toBe(2)
    expect(screen.getAllByText('spawn uvx ENOENT').length).toBe(2)
  })

  it('saves only changed fields plus entered tokens, then reconnects and refreshes token state', async () => {
    const { p, face } = props()
    render(<AtlassianSettingsTab {...p} />)
    const save = screen.getByRole('button', { name: 'Save' })
    expect(save.hasAttribute('disabled')).toBe(true)
    const urls = document.querySelectorAll('input[type=url]')
    fireEvent.change(urls[1]!, { target: { value: 'https://wiki.example.com' } })
    fireEvent.change(urls[0]!, { target: { value: 'https://jira.example.com' } })
    fireEvent.click(screen.getByLabelText('Deny all writes (read-only)'))
    fireEvent.change(document.querySelectorAll('input[type=password]')[0]!, { target: { value: 'secret-1' } })
    fireEvent.change(document.querySelectorAll('input[type=password]')[1]!, { target: { value: '   ' } })
    expect(save.hasAttribute('disabled')).toBe(false)
    fireEvent.click(save)
    await waitFor(() => { expect(face.reconnect).toHaveBeenCalledTimes(1) })
    expect(face.setField).toHaveBeenCalledTimes(2)
    expect(face.setField).toHaveBeenCalledWith('confluenceUrl', 'https://wiki.example.com')
    expect(face.setField).toHaveBeenCalledWith('writes', 'deny')
    expect(face.setToken).toHaveBeenCalledTimes(1)
    expect(face.setToken).toHaveBeenCalledWith('ATLASSIAN_JIRA_TOKEN', 'secret-1')
    await screen.findByText('Saved')
    expect(face.describeTokens).toHaveBeenCalledTimes(2)
    // The reconnect status lands: failed atlassian mount with its error text.
    await waitFor(() => { expect(screen.getAllByText('spawn uvx ENOENT').length).toBe(2) })
  })

  it('reports a field save failure and a token save failure, and Discard resets the drafts', async () => {
    const failing = props({ face: { setField: vi.fn(() => Promise.resolve({ ok: false as const, message: 'settings-rejected' })) } })
    const { unmount } = render(<AtlassianSettingsTab {...failing.p} />)
    fireEvent.change(document.querySelectorAll('input[type=text]')[0]!, { target: { value: 'PROJ,OPS' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))
    await screen.findByText('Could not save: settings-rejected')
    fireEvent.click(screen.getByRole('button', { name: 'Discard' }))
    expect(screen.getByRole('button', { name: 'Save' }).hasAttribute('disabled')).toBe(true)
    expect((document.querySelectorAll('input[type=text]')[0] as HTMLInputElement).value).toBe('')
    unmount()

    const tokenFail = props({ face: { setToken: vi.fn(() => Promise.resolve({ ok: false as const, message: 'credential-rejected' })) } })
    render(<AtlassianSettingsTab {...tokenFail.p} />)
    fireEvent.change(document.querySelectorAll('input[type=password]')[0]!, { target: { value: 'tok' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))
    await screen.findByText('Could not save: credential-rejected')
  })

  it('edits the launch lines and toolset fields and reconnects on demand', async () => {
    const { p, face } = props()
    render(<AtlassianSettingsTab {...p} />)
    const mono = document.querySelectorAll('input[type=text]')
    // Order: jira filter, confluence filter, bitbucket project, launch×2, toolsets, enabledTools.
    fireEvent.change(mono[3]!, { target: { value: 'uvx mcp-atlassian@latest' } })
    fireEvent.change(mono[4]!, { target: { value: 'node bb.js' } })
    fireEvent.change(mono[5]!, { target: { value: 'all' } })
    fireEvent.change(mono[6]!, { target: { value: 'jira_get_issue' } })
    fireEvent.change(mono[2]!, { target: { value: 'OPS' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))
    await waitFor(() => { expect(face.setField).toHaveBeenCalledTimes(5) })
    expect(face.setField).toHaveBeenCalledWith('atlassianLaunch', 'uvx mcp-atlassian@latest')
    expect(face.setField).toHaveBeenCalledWith('enabledTools', 'jira_get_issue')
    fireEvent.click(screen.getByRole('button', { name: 'Reconnect' }))
    await waitFor(() => { expect(face.reconnect).toHaveBeenCalledTimes(2) })
  })

  it('locks every input off the host machine', async () => {
    const { p } = props({ writable: false })
    render(<AtlassianSettingsTab {...p} />)
    expect(screen.getByText('Open Cortex on the host machine to change these settings.')).toBeTruthy()
    expect(screen.getAllByText(/Tokens can only be stored from the host machine/).length).toBe(3)
    for (const input of document.querySelectorAll('input')) expect(input.hasAttribute('disabled')).toBe(true)
    await waitFor(() => { expect(screen.getAllByText('Connected · 7 tools').length).toBe(2) })
  })

  it('shows a bare Off pill, ignores a radio pick equal to the stored value, and stringifies a raw rejection', async () => {
    const { p, face } = props({
      face: {
        status: vi.fn(() => Promise.resolve(status({ atlassian: { phase: 'off', toolCount: 0 }, bitbucket: { phase: 'off', toolCount: 0, missing: [] } }))),
        // oxlint-disable-next-line prefer-promise-reject-errors -- the component must stringify a non-Error rejection
        setField: vi.fn(() => Promise.reject('raw failure')),
      },
    })
    render(<AtlassianSettingsTab {...p} />)
    await waitFor(() => { expect(screen.getAllByText('Off').length).toBe(3) })
    fireEvent.click(screen.getByLabelText('Deny all writes (read-only)'))
    fireEvent.click(screen.getByLabelText('Ask before every write'))
    expect(screen.getByRole('button', { name: 'Save' }).hasAttribute('disabled')).toBe(true)
    fireEvent.change(document.querySelectorAll('input[type=text]')[0]!, { target: { value: 'X' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))
    await screen.findByText('Could not save: raw failure')
    expect(face.setField).toHaveBeenCalledTimes(1)
  })

  it('survives a status or token lookup failure', async () => {
    const { p, face } = props({
      face: {
        status: vi.fn(() => Promise.reject(new Error('down'))),
        describeTokens: vi.fn(() => Promise.reject(new Error('down'))),
        reconnect: vi.fn(() => Promise.reject(new Error('down'))),
      },
    })
    render(<AtlassianSettingsTab {...p} />)
    await waitFor(() => { expect(face.status).toHaveBeenCalled() })
    fireEvent.click(screen.getByRole('button', { name: 'Reconnect' }))
    await waitFor(() => { expect(face.reconnect).toHaveBeenCalled() })
    expect(screen.getAllByText('Loading…').length).toBe(3)
    // A save still lands its fields; the failing token lookup and reconnect surface as the save error.
    fireEvent.change(document.querySelectorAll('input[type=text]')[0]!, { target: { value: 'X' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))
    await screen.findByText('Could not save: down')
    expect(face.describeTokens).toHaveBeenCalledTimes(2)
  })
})
