import { afterEach, describe, expect, it, vi } from 'vitest'
import { Context } from '@cortex/cordis'
import { defineTool } from '@cortex/tools'
import SystemPrompt from '@cortex/system-prompt'
import ToolRuntime from '@cortex/tools'
import { MCP_TOOL_TIMEOUT_MS, McpMounts, commandExists, planMounts, type MountPlan, type StdioMountConfig } from '../src/mounts.ts'
import { containing, rejectWith } from './fixtures.ts'
import { DEFAULT_ATLASSIAN_SETTINGS } from '../src/settings.ts'
import { ATLASSIAN_SERVER, BITBUCKET_SERVER } from '../src/tools.ts'

const URLS = { jira: 'https://jira', confluence: 'https://confluence', bitbucket: 'https://bitbucket' }
const TOKENS = { jira: 'jt', confluence: 'ct', bitbucket: 'bt' }

describe('planMounts', () => {
  it('reports what each mount is missing', () => {
    const [atlassian, bitbucket] = planMounts(DEFAULT_ATLASSIAN_SETTINGS, {}, {})
    expect(atlassian).toEqual({ server: ATLASSIAN_SERVER, config: undefined, missing: ['url', 'token'] })
    expect(bitbucket).toEqual({ server: BITBUCKET_SERVER, config: undefined, missing: ['url', 'token'] })
    const noLaunch = planMounts({ ...DEFAULT_ATLASSIAN_SETTINGS, atlassianLaunch: ' ', bitbucketLaunch: '' }, URLS, TOKENS)
    expect(noLaunch[0].missing).toEqual(['launch'])
    expect(noLaunch[1].missing).toEqual(['launch'])
    // URLs without tokens, and a token for a service whose URL is unset, both leave the pair incomplete.
    expect(planMounts(DEFAULT_ATLASSIAN_SETTINGS, URLS, {})[0].missing).toEqual(['token'])
    expect(planMounts(DEFAULT_ATLASSIAN_SETTINGS, { jira: URLS.jira }, { confluence: TOKENS.confluence })[0].missing).toEqual(['token'])
    expect(planMounts(DEFAULT_ATLASSIAN_SETTINGS, {}, TOKENS)[0].missing).toEqual(['url'])
    expect(planMounts(DEFAULT_ATLASSIAN_SETTINGS, { bitbucket: URLS.bitbucket }, {})[1].missing).toEqual(['token'])
  })

  it('builds the child configs with the forwarded environment', () => {
    const settings = {
      ...DEFAULT_ATLASSIAN_SETTINGS,
      jiraProjectsFilter: ' PROJ,OPS ',
      confluenceSpacesFilter: 'ENG',
      bitbucketDefaultProject: ' PROJ ',
      toolsets: '  ',
      enabledTools: 'jira_get_issue,jira_search',
    }
    const [atlassian, bitbucket] = planMounts(settings, URLS, TOKENS)
    expect(atlassian.missing).toEqual([])
    expect(atlassian.config).toEqual({
      transport: 'stdio',
      serverName: ATLASSIAN_SERVER,
      command: 'uvx',
      args: ['mcp-atlassian'],
      env: {
        TOOLSETS: 'default',
        MCP_VERBOSE: 'false',
        ENABLED_TOOLS: 'jira_get_issue,jira_search',
        JIRA_URL: URLS.jira,
        JIRA_PERSONAL_TOKEN: TOKENS.jira,
        JIRA_PROJECTS_FILTER: 'PROJ,OPS',
        CONFLUENCE_URL: URLS.confluence,
        CONFLUENCE_PERSONAL_TOKEN: TOKENS.confluence,
        CONFLUENCE_SPACES_FILTER: 'ENG',
      },
      cwd: '',
      toolCallTimeoutMs: MCP_TOOL_TIMEOUT_MS,
      failOnStartupError: false,
    })
    expect(bitbucket.config).toMatchObject({
      serverName: BITBUCKET_SERVER,
      command: 'docker',
      env: { BITBUCKET_URL: URLS.bitbucket, BITBUCKET_TOKEN: TOKENS.bitbucket, BITBUCKET_DEFAULT_PROJECT: 'PROJ' },
    })
    // Only one of the two Atlassian services configured still mounts the server.
    const jiraOnly = planMounts({ ...DEFAULT_ATLASSIAN_SETTINGS, toolsets: 'all' }, { jira: URLS.jira }, { jira: TOKENS.jira })[0]
    expect(jiraOnly.config?.env).toEqual({ TOOLSETS: 'all', MCP_VERBOSE: 'false', JIRA_URL: URLS.jira, JIRA_PERSONAL_TOKEN: TOKENS.jira })
    const confluenceOnly = planMounts(DEFAULT_ATLASSIAN_SETTINGS, { confluence: URLS.confluence }, { confluence: TOKENS.confluence })[0]
    expect(confluenceOnly.config?.env).toEqual({
      TOOLSETS: 'default', MCP_VERBOSE: 'false', CONFLUENCE_URL: URLS.confluence, CONFLUENCE_PERSONAL_TOKEN: TOKENS.confluence,
    })
    const bare = planMounts(DEFAULT_ATLASSIAN_SETTINGS, URLS, TOKENS)[1]
    expect(bare.config?.env).toEqual({ BITBUCKET_URL: URLS.bitbucket, BITBUCKET_TOKEN: TOKENS.bitbucket })
  })
})

interface FakeChild {
  config: StdioMountConfig
  await: (() => Promise<unknown>) & ReturnType<typeof vi.fn>
  dispose: (() => Promise<unknown>) & ReturnType<typeof vi.fn>
}

function plansWith(): [MountPlan, MountPlan] {
  return planMounts(DEFAULT_ATLASSIAN_SETTINGS, URLS, TOKENS)
}

/** Register one fake `mcp__<server>__probe` tool so a mounted child counts as connected; returns its disposer. */
function fakeTool(ctx: Context, server: string): () => void {
  return ctx.tools.register(defineTool({
    name: `mcp__${server}__probe`, description: 'probe', parameters: {}, output: { schema: { type: 'null' }, render: () => [] },
    execute: () => Promise.resolve(null),
  }))
}

interface Behavior {
  awaitResult?: Promise<unknown>
  disposeResult?: Promise<unknown>
  /** Whether the child registers a tool while awaiting (the connected outcome). */
  registers?: boolean
}

async function bench(
  behavior: (config: StdioMountConfig) => Behavior = () => ({}),
  resolveCommand: (command: string) => boolean = () => true,
) {
  const ctx = new Context()
  await ctx.plugin(SystemPrompt)
  await ctx.plugin(ToolRuntime)
  const children: FakeChild[] = []
  const mount = (config: StdioMountConfig): FakeChild => {
    const script = behavior(config)
    let unregister: (() => void) | undefined
    const child: FakeChild = {
      config,
      await: vi.fn(() => {
        if (script.registers !== false) unregister = fakeTool(ctx, config.serverName)
        return script.awaitResult ?? Promise.resolve()
      }) as FakeChild['await'],
      dispose: vi.fn(() => {
        unregister?.()
        return script.disposeResult ?? Promise.resolve()
      }) as FakeChild['dispose'],
    }
    children.push(child)
    return child
  }
  const mounts = new McpMounts(ctx, mount, resolveCommand)
  return { ctx, mounts, children }
}

let live: Context | undefined
afterEach(async () => {
  await live?.fiber.dispose()
  live = undefined
})

describe('McpMounts', () => {
  it('mounts configured plans, keeps unchanged ones, and remounts changed ones', async () => {
    const { ctx, mounts, children } = await bench()
    live = ctx
    expect(mounts.status(ATLASSIAN_SERVER)).toEqual({ phase: 'off', toolCount: 0 })
    await mounts.reconcile(plansWith())
    expect(children).toHaveLength(2)
    expect(mounts.status(ATLASSIAN_SERVER)).toEqual({ phase: 'ready', toolCount: 1 })
    expect(mounts.status(BITBUCKET_SERVER).phase).toBe('ready')
    // Same plans: nothing remounts.
    await mounts.reconcile(plansWith())
    expect(children).toHaveLength(2)
    expect(children[0]?.dispose).not.toHaveBeenCalled()
    // A changed Bitbucket plan remounts only that child.
    const changed = planMounts({ ...DEFAULT_ATLASSIAN_SETTINGS, bitbucketDefaultProject: 'X' }, URLS, TOKENS)
    await mounts.reconcile(changed)
    expect(children).toHaveLength(3)
    expect(children[1]?.dispose).toHaveBeenCalledTimes(1)
    expect(children[0]?.dispose).not.toHaveBeenCalled()
    // A plan that lost its config unmounts and reports what is missing.
    const partial = planMounts(DEFAULT_ATLASSIAN_SETTINGS, { jira: URLS.jira }, { jira: TOKENS.jira })
    await mounts.reconcile(partial)
    expect(children[2]?.dispose).toHaveBeenCalledTimes(1)
    expect(mounts.status(BITBUCKET_SERVER)).toEqual({ phase: 'off', toolCount: 0, missing: ['url', 'token'] })
    // The missing list refreshes even when the plan stays absent.
    await mounts.reconcile(planMounts(DEFAULT_ATLASSIAN_SETTINGS, { jira: URLS.jira, bitbucket: URLS.bitbucket }, { jira: TOKENS.jira }))
    expect(mounts.status(BITBUCKET_SERVER).missing).toEqual(['token'])
    expect(mounts.status(ATLASSIAN_SERVER).toolCount).toBe(1)
    expect(mounts.status(BITBUCKET_SERVER).toolCount).toBe(0)
    await mounts.dispose()
    expect(children[0]?.dispose).toHaveBeenCalledTimes(1)
    expect(mounts.status(ATLASSIAN_SERVER)).toEqual({ phase: 'off', toolCount: 0 })
  })

  it('reports startup failures with their cause and retries only errored cells', async () => {
    let attempts = 0
    const { ctx, mounts, children } = await bench((config) => {
      if (config.serverName !== ATLASSIAN_SERVER) return {}
      attempts += 1
      if (attempts === 1) return { awaitResult: Promise.reject(new Error('startup failed', { cause: new Error('spawn uvx ENOENT') })), disposeResult: Promise.reject(new Error('teardown too')) }
      if (attempts === 2) return { awaitResult: Promise.reject(new Error('startup failed', { cause: 'plain cause' })) }
      if (attempts === 3) return { awaitResult: Promise.reject(new Error('startup failed: same text', { cause: 'same text' })) }
      if (attempts === 4) return { awaitResult: rejectWith('string throw') }
      return {}
    })
    live = ctx
    await mounts.reconcile(plansWith())
    expect(mounts.status(ATLASSIAN_SERVER)).toEqual({ phase: 'error', toolCount: 0, error: 'startup failed: spawn uvx ENOENT' })
    expect(mounts.status(BITBUCKET_SERVER).phase).toBe('ready')
    expect(children[0]?.dispose).toHaveBeenCalledTimes(1)
    await mounts.retry(plansWith())
    expect(mounts.status(ATLASSIAN_SERVER).error).toBe('startup failed: plain cause')
    expect(children.filter(child => child.config.serverName === BITBUCKET_SERVER)).toHaveLength(1)
    await mounts.retry(plansWith())
    expect(mounts.status(ATLASSIAN_SERVER).error).toBe('startup failed: same text')
    await mounts.retry(plansWith())
    expect(mounts.status(ATLASSIAN_SERVER).error).toBe('string throw')
    await mounts.retry(plansWith())
    expect(mounts.status(ATLASSIAN_SERVER)).toEqual({ phase: 'ready', toolCount: 1 })
    expect(attempts).toBe(5)
  })

  it('reports a child that registered no tools, and heals when tools arrive later', async () => {
    const { ctx, mounts, children } = await bench(config => config.serverName === ATLASSIAN_SERVER ? { registers: false } : {})
    live = ctx
    await mounts.reconcile(plansWith())
    expect(mounts.status(ATLASSIAN_SERVER)).toEqual({ phase: 'error', toolCount: 0, error: containing('registered no tools') })
    expect(children[0]?.dispose).not.toHaveBeenCalled()
    // The background reconnect of the still-live child registers tools later.
    const unregister = fakeTool(ctx, ATLASSIAN_SERVER)
    expect(mounts.status(ATLASSIAN_SERVER)).toEqual({ phase: 'ready', toolCount: 1, error: containing('registered no tools') })
    unregister()
    await mounts.dispose()
  })

  it('refuses a launch command that cannot be found without mounting', async () => {
    const { ctx, mounts, children } = await bench(() => ({}), command => command !== 'uvx')
    live = ctx
    await mounts.reconcile(plansWith())
    expect(mounts.status(ATLASSIAN_SERVER)).toEqual({ phase: 'error', toolCount: 0, error: 'command not found: uvx' })
    expect(mounts.status(BITBUCKET_SERVER).phase).toBe('ready')
    expect(children.map(child => child.config.serverName)).toEqual([BITBUCKET_SERVER])
  })

  it('looks launch commands up by path or on PATH', () => {
    expect(commandExists(process.execPath)).toBe(true)
    expect(commandExists('/definitely/missing/binary')).toBe(false)
    expect(commandExists('node')).toBe(true)
    expect(commandExists('definitely-missing-binary-xyz')).toBe(false)
    const path = process.env.PATH
    process.env.PATH = ''
    try {
      expect(commandExists('node')).toBe(false)
    } finally {
      process.env.PATH = path
    }
    delete process.env.PATH
    try {
      expect(commandExists('node')).toBe(false)
    } finally {
      process.env.PATH = path
    }
  })

  it('warns when a live child refuses to tear down and swallows a synchronous mount throw', async () => {
    const { ctx, mounts, children } = await bench(config => config.serverName === BITBUCKET_SERVER ? { disposeResult: Promise.reject(new Error('stuck')) } : {})
    live = ctx
    const warn = vi.spyOn(ctx.logger, 'warn').mockImplementation(() => undefined)
    await mounts.reconcile(plansWith())
    await mounts.reconcile(planMounts(DEFAULT_ATLASSIAN_SETTINGS, { jira: URLS.jira }, { jira: TOKENS.jira }))
    expect(children[1]?.dispose).toHaveBeenCalledTimes(1)
    expect(warn).toHaveBeenCalledWith(expect.stringContaining('MCP child teardown failed: stuck'))
    const throwing = new McpMounts(ctx, () => { throw new Error('mount exploded') })
    await expect(throwing.reconcile(plansWith())).resolves.toBeUndefined()
    expect(throwing.status(ATLASSIAN_SERVER).phase).toBe('starting')
    await expect(throwing.dispose()).resolves.toBeUndefined()
  })
})
