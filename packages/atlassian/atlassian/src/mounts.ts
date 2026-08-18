/**
 * MCP child mounts: computes the `@cortex/mcp-client` configuration of the
 * Jira/Confluence and Bitbucket servers from settings + credentials, mounts
 * them as child plugins, remounts on change, and reports each mount's phase.
 *
 * @module
 */

import { existsSync } from 'node:fs'
import { delimiter, isAbsolute, join } from 'node:path'
import type { Context } from '@cortex/cordis'
import type { Config as McpClientConfig } from '@cortex/mcp-client'
import * as McpClient from '@cortex/mcp-client'
import { parseLaunchLine } from './settings.ts'
import { ATLASSIAN_PREFIX, ATLASSIAN_SERVER, BITBUCKET_PREFIX, BITBUCKET_SERVER } from './tools.ts'
import type { AtlassianSettings, MountStatus } from './types.ts'

/** Which of the two servers a mount is. */
export type MountServer = typeof ATLASSIAN_SERVER | typeof BITBUCKET_SERVER

/** Resolved secrets by service. */
export interface ResolvedTokens {
  jira?: string
  confluence?: string
  bitbucket?: string
}

/** Resolved base URLs by service (normalized, absent when unset/invalid). */
export interface ResolvedUrls {
  jira?: string
  confluence?: string
  bitbucket?: string
}

/** The stdio child configuration both mounts use. */
export type StdioMountConfig = Extract<McpClientConfig, { transport: 'stdio' }>

/** What one mount needs: either a config or the list of missing facts. */
export type MountPlan =
  | { server: MountServer; config: StdioMountConfig; missing: [] }
  | { server: MountServer; config: undefined; missing: ('url' | 'token' | 'launch')[] }

/** Per-tool-call timeout passed to both mounts. */
export const MCP_TOOL_TIMEOUT_MS = 90_000

/**
 * Compute both mount plans from settings, URLs, and tokens.
 * @param settings - current settings.
 * @param urls - normalized base URLs.
 * @param tokens - resolved tokens.
 * @returns the two plans.
 */
export function planMounts(settings: AtlassianSettings, urls: ResolvedUrls, tokens: ResolvedTokens): [MountPlan, MountPlan] {
  const atlassian = ((): MountPlan => {
    const launch = parseLaunchLine(settings.atlassianLaunch)
    const haveJira = urls.jira !== undefined && tokens.jira !== undefined
    const haveConfluence = urls.confluence !== undefined && tokens.confluence !== undefined
    if (launch === undefined || (!haveJira && !haveConfluence)) {
      const missing: ('url' | 'token' | 'launch')[] = []
      if (launch === undefined) missing.push('launch')
      if (urls.jira === undefined && urls.confluence === undefined) missing.push('url')
      if (tokens.jira === undefined && tokens.confluence === undefined) missing.push('token')
      if (missing.length === 0) missing.push('token')
      return { server: ATLASSIAN_SERVER, config: undefined, missing }
    }
    const env: Record<string, string> = {
      TOOLSETS: settings.toolsets.trim() === '' ? 'default' : settings.toolsets.trim(),
      MCP_VERBOSE: 'false',
    }
    if (settings.enabledTools.trim() !== '') env.ENABLED_TOOLS = settings.enabledTools.trim()
    if (haveJira) {
      /* v8 ignore start -- haveJira proves url and token are defined */
      env.JIRA_URL = urls.jira ?? ''
      env.JIRA_PERSONAL_TOKEN = tokens.jira ?? ''
      /* v8 ignore stop */
      if (settings.jiraProjectsFilter.trim() !== '') env.JIRA_PROJECTS_FILTER = settings.jiraProjectsFilter.trim()
    }
    if (haveConfluence) {
      /* v8 ignore start -- haveConfluence proves url and token are defined */
      env.CONFLUENCE_URL = urls.confluence ?? ''
      env.CONFLUENCE_PERSONAL_TOKEN = tokens.confluence ?? ''
      /* v8 ignore stop */
      if (settings.confluenceSpacesFilter.trim() !== '') env.CONFLUENCE_SPACES_FILTER = settings.confluenceSpacesFilter.trim()
    }
    return {
      server: ATLASSIAN_SERVER,
      config: {
        transport: 'stdio',
        serverName: ATLASSIAN_SERVER,
        command: launch.command,
        args: launch.args,
        env,
        cwd: '',
        toolCallTimeoutMs: MCP_TOOL_TIMEOUT_MS,
        // Startup failures must not fail the child fiber: a failed mcp-client
        // fiber leaks a rejected promise that the host's fail-loud guard turns
        // into a process exit. The mount reports failure through its tool count.
        failOnStartupError: false,
      },
      missing: [],
    }
  })()

  const bitbucket = ((): MountPlan => {
    const launch = parseLaunchLine(settings.bitbucketLaunch)
    if (launch === undefined || urls.bitbucket === undefined || tokens.bitbucket === undefined) {
      const missing: ('url' | 'token' | 'launch')[] = []
      if (launch === undefined) missing.push('launch')
      if (urls.bitbucket === undefined) missing.push('url')
      if (tokens.bitbucket === undefined) missing.push('token')
      return { server: BITBUCKET_SERVER, config: undefined, missing }
    }
    const env: Record<string, string> = {
      BITBUCKET_URL: urls.bitbucket,
      BITBUCKET_TOKEN: tokens.bitbucket,
    }
    if (settings.bitbucketDefaultProject.trim() !== '') env.BITBUCKET_DEFAULT_PROJECT = settings.bitbucketDefaultProject.trim()
    return {
      server: BITBUCKET_SERVER,
      config: {
        transport: 'stdio',
        serverName: BITBUCKET_SERVER,
        command: launch.command,
        args: launch.args,
        env,
        cwd: '',
        toolCallTimeoutMs: MCP_TOOL_TIMEOUT_MS,
        failOnStartupError: false,
      },
      missing: [],
    }
  })()

  return [atlassian, bitbucket]
}

/** Live child fiber wrapper as `ctx.plugin()` returns it. */
interface ChildHandle {
  await(): Promise<unknown>
  dispose(): Promise<unknown>
}

interface MountCell {
  fingerprint: string | undefined
  handle: ChildHandle | undefined
  phase: MountStatus['phase']
  error: string | undefined
  missing: MountStatus['missing']
}

/**
 * Whether a launch command resolves to an existing file: an absolute or
 * relative path is checked directly, a bare name is looked up on `PATH`.
 * @param command - the launch command.
 * @returns true when the command can be spawned.
 */
export function commandExists(command: string): boolean {
  if (command.includes('/') || isAbsolute(command)) return existsSync(command)
  return (process.env.PATH ?? '').split(delimiter).some(dir => dir !== '' && existsSync(join(dir, command)))
}

/** Message reported when a mounted server registered no tools. */
const NO_TOOLS_MESSAGE = 'the MCP server started but registered no tools — check the launch command, the URL, and the token (the host log has the server\'s own error); the mount keeps retrying in the background'

/**
 * Owner of both MCP child mounts. One instance per plugin fiber; `dispose()`
 * unmounts whatever is live.
 */
export class McpMounts {
  private readonly cells: Record<MountServer, MountCell> = {
    [ATLASSIAN_SERVER]: { fingerprint: undefined, handle: undefined, phase: 'off', error: undefined, missing: [] },
    [BITBUCKET_SERVER]: { fingerprint: undefined, handle: undefined, phase: 'off', error: undefined, missing: [] },
  }

  private queue: Promise<void> = Promise.resolve()

  /**
   * @param ctx - context the children mount under (must carry `ctx.tools`).
   * @param mount - child mounting function; defaults to `ctx.plugin(McpClient, config)`.
   * @param resolveCommand - launch-command existence check; defaults to {@link commandExists}.
   */
  constructor(
    private readonly ctx: Context,
    /* v8 ignore start -- production seam: mounts a real MCP client child */
    private readonly mount: (config: StdioMountConfig) => ChildHandle = config => ctx.plugin(McpClient, config),
    /* v8 ignore stop */
    private readonly resolveCommand: (command: string) => boolean = commandExists,
  ) {}

  private toolCount(server: MountServer): number {
    const prefix = server === ATLASSIAN_SERVER ? ATLASSIAN_PREFIX : BITBUCKET_PREFIX
    return this.ctx.tools.schemas().filter(schema => schema.name.startsWith(prefix)).length
  }

  /**
   * Status of one server mount.
   * @param server - which server.
   * @returns phase, live tool count, and error/missing facts.
   */
  status(server: MountServer): MountStatus {
    const cell = this.cells[server]
    const toolCount = this.toolCount(server)
    return {
      // A background reconnect that eventually registers tools heals an errored mount.
      phase: cell.phase === 'error' && toolCount > 0 ? 'ready' : cell.phase,
      toolCount,
      ...cell.error === undefined ? {} : { error: cell.error },
      ...cell.missing === undefined || cell.missing.length === 0 ? {} : { missing: cell.missing },
    }
  }

  /**
   * Bring both mounts in line with the plans: unchanged plans keep their live
   * child, changed plans remount, absent plans unmount. Serialized: a second
   * call waits for the previous reconciliation.
   * @param plans - the two mount plans.
   * @returns completion once every child settled (mounted, failed, or unmounted).
   */
  reconcile(plans: readonly MountPlan[]): Promise<void> {
    this.queue = this.queue.then(() => this.apply(plans)).catch(() => undefined)
    return this.queue
  }

  private async apply(plans: readonly MountPlan[]): Promise<void> {
    for (const plan of plans) {
      const cell = this.cells[plan.server]
      const fingerprint = plan.config === undefined ? undefined : JSON.stringify(plan.config)
      if (fingerprint === cell.fingerprint && cell.phase !== 'error') {
        if (plan.config === undefined) cell.missing = plan.missing
        continue
      }
      await this.unmount(cell)
      cell.fingerprint = fingerprint
      if (plan.config === undefined) {
        cell.phase = 'off'
        cell.error = undefined
        cell.missing = plan.missing
        continue
      }
      cell.missing = []
      cell.phase = 'starting'
      cell.error = undefined
      if (!this.resolveCommand(plan.config.command)) {
        cell.phase = 'error'
        cell.error = `command not found: ${plan.config.command}`
        continue
      }
      const handle = this.mount(plan.config)
      cell.handle = handle
      try {
        await handle.await()
        if (this.toolCount(plan.server) > 0) {
          cell.phase = 'ready'
        } else {
          cell.phase = 'error'
          cell.error = NO_TOOLS_MESSAGE
        }
      } catch (error: unknown) {
        cell.phase = 'error'
        cell.error = errorMessage(error)
        try {
          await handle.dispose()
        } catch {
          // A failed child's teardown failure is unreachable to the user; the startup error is the actionable one.
        }
        cell.handle = undefined
      }
    }
  }

  private async unmount(cell: MountCell): Promise<void> {
    const handle = cell.handle
    cell.handle = undefined
    if (handle === undefined) return
    try {
      await handle.dispose()
    } catch (error: unknown) {
      this.ctx.logger.warn(`atlassian: MCP child teardown failed: ${errorMessage(error)}`)
    }
  }

  /**
   * Retry a failed mount with its last plan.
   * @param plans - the current plans.
   * @returns completion.
   */
  retry(plans: readonly MountPlan[]): Promise<void> {
    for (const plan of plans) {
      const cell = this.cells[plan.server]
      if (cell.phase === 'error') cell.fingerprint = undefined
    }
    return this.reconcile(plans)
  }

  /**
   * Unmount both children.
   * @returns completion.
   */
  dispose(): Promise<void> {
    this.queue = this.queue.then(async () => {
      for (const cell of Object.values(this.cells)) {
        await this.unmount(cell)
        cell.phase = 'off'
        cell.fingerprint = undefined
      }
    /* v8 ignore start -- unmount and the assignments above never throw */
    }).catch(() => undefined)
    /* v8 ignore stop */
    return this.queue
  }
}

/** Human message of a thrown value, unwrapping one `cause` level. */
function errorMessage(error: unknown): string {
  if (error instanceof Error) {
    const cause = error.cause
    const causeText = cause instanceof Error ? cause.message : typeof cause === 'string' ? cause : undefined
    return causeText === undefined || error.message.includes(causeText) ? error.message : `${error.message}: ${causeText}`
  }
  return String(error)
}
