/** Integrated Testy engine, MCP tools, authenticated UI API, and Cortex model ownership. */
import { randomUUID } from 'node:crypto'
import { spawn, type ChildProcess } from 'node:child_process'
import { mkdir, access } from 'node:fs/promises'
import { dirname, join, resolve } from 'node:path'
import { Context, Service } from '@cortex/cordis'
import z from '@cortex/schemastery'
import { z as wire } from 'zod'
import { Remote, TypertRemoteService } from '@cortex/typert-protocol'
import { SessionId, type Session } from '@cortex/session'
import type { Agent, ModelSelection } from '@cortex/agent'
import type {} from '@cortex/agent-default-model'
import type {} from '@cortex/session-title'
import type { SessionHandle } from '@cortex/session-persistence'
import type {} from '@cortex/session-query'
import type {} from '@cortex/api-session-controller/types'
import { ReasoningEffortId } from '@cortex/llm'
import type {} from '@cortex/app-boot'
import type {} from '@cortex/tools'
import { scrubbedParentEnv } from '@cortex/subprocess'
import { createMcpToolDefinition } from '@cortex/mcp-client'
import { TestyBackend, type BackendTool } from './backend.ts'
import { createTestyModelBridge, type TestyModelBridge, type TestyModelRunHandle } from './model-bridge.ts'
import { stopOwnedProcessTree } from './native-process.ts'
import { resolveTestyExecutable } from './native-runtime.ts'
import type { JsonValue, TestyPluginStatus, TestyToolResult } from './types.ts'

export type * from './types.ts'

/** Deployment paths and resource limits; no separate model or provider credentials. */
export interface Config {
  /** Native Testy CLI path; empty selects the packaged Windows runtime. */
  executable: string
  /** Fixed arguments prepended before the owned MCP command. */
  arguments: string[]
  /** Testy data directory; empty selects the current Cortex profile's workspace. */
  workspace: string
  /** Deadline for one native MCP operation, in milliseconds. */
  toolCallTimeoutMs: number
  /** Maximum bytes retained for one MCP message or private model request. */
  maxMessageBytes: number
  /** Maximum lifetime of an operation's model capability, in milliseconds. */
  modelContextTtlMs: number
  /** Maximum simultaneously registered Testy model operations. */
  maxModelContexts: number
  /** Output-token ceiling applied to every Testy model request. */
  maxOutputTokens: number
  /** Grace period after closing native stdin before terminating its process tree. */
  shutdownGraceMs: number
}

declare module '@cortex/cordis' {
  interface Context {
    /** Local Testy engine shared by chats and the testing pane. */
    testy: TestyService
  }
}

interface OwnedContext {
  handle: TestyModelRunHandle
  timer: ReturnType<typeof setTimeout>
  session: Session
  detach: () => void
  writer?: SessionHandle
  runId?: string
}

interface OperationContext {
  model: ModelSelection
  sessionId?: SessionId
  workspace?: string | undefined
}

interface OwnedStudio {
  child: ChildProcess
  closed: Promise<void>
}

/** Whether one validated JSON value is an object. */
function record(value: JsonValue | undefined): value is Record<string, JsonValue> {
  return value !== undefined && value !== null && typeof value === 'object' && !Array.isArray(value)
}

/** Resolve the route already powering the initiating chat, or the current GUI default. */
function modelFor(agent: Agent | undefined, fallback: ModelSelection): ModelSelection {
  const selected = agent?.session.requestHeader()?.config ?? agent?.options
  if (selected?.provider === undefined || selected.model === undefined) return modelRoute(fallback)
  return modelRoute({ provider: selected.provider, model: selected.model,
    ...selected.reasoningEffort === undefined ? {} : { reasoningEffort: selected.reasoningEffort } })
}

/** Copy only public routing values from a chat configuration. */
function modelRoute(selected: { provider: string; model: string; reasoningEffort?: string }): ModelSelection {
  return {
    provider: selected.provider, model: selected.model,
    ...selected.reasoningEffort === undefined ? {} : { reasoningEffort: ReasoningEffortId(selected.reasoningEffort) },
  }
}

/** One optional plugin owns UI availability, all MCP registrations, and every child capability. */
export class TestyService extends TypertRemoteService {
  static inject = ['tools', 'llm', 'sessions', 'agents', 'agentDefaultModel', 'attachments']
  static Config: z<Config> = z.object({
    executable: z.string().default(''),
    arguments: z.array(String).default([]),
    workspace: z.string().default(''),
    toolCallTimeoutMs: z.number().step(1).min(1000).max(600000).default(180000),
    maxMessageBytes: z.number().step(1).min(1024).max(67108864).default(33554432),
    modelContextTtlMs: z.number().step(1).min(60000).max(2147483647).default(86400000),
    maxModelContexts: z.number().step(1).min(1).max(1024).default(128),
    maxOutputTokens: z.number().step(1).min(256).max(131072).default(16384),
    shutdownGraceMs: z.number().step(1).min(1000).max(120000).default(20000),
  })

  private readonly lifetime = new AbortController()
  private readonly executable: string
  private readonly workspace: string
  private readonly contexts = new Map<string, OwnedContext>()
  private readonly registrations: Array<() => void> = []
  private readonly studios = new Set<OwnedStudio>()
  private readonly pending = new Set<Promise<unknown>>()
  private readonly catalog = new Map<string, BackendTool>()
  private backend: TestyBackend | undefined
  private bridge: TestyModelBridge | undefined
  private startup: Promise<void> | undefined
  private stopping: Promise<void> | undefined
  private connected = false
  private lastError: string | undefined
  private revision = 0
  private creatingContexts = 0

  constructor(ctx: Context, private readonly config: Config) {
    super(ctx, 'testy', { namespace: 'testy' })
    const home = ctx.get('profileContext')?.home
    if (config.workspace.length > 0) this.workspace = resolve(config.workspace)
    else if (home !== undefined) this.workspace = resolve(home, 'testy', 'workspace')
    else throw new Error('Testy requires a Cortex profile or an explicit workspace.')
    this.executable = resolveTestyExecutable(config.executable, import.meta.url)
    ctx.effect(() => () => this.shutdown(), 'testy: owned engine and model capabilities')
  }

  /** Negotiate the bundled engine before declaring this plugin ready. */
  async [Service.init](): Promise<void> {
    this.startup = this.start()
    await this.startup
  }

  /**
   * Read local engine availability and the model selected for the next GUI operation.
   * @param contextSessionId - selected chat whose saved model powers the operation.
   * @returns current engine status and the selected Cortex model.
   */
  @Remote
  async status(contextSessionId?: SessionId): Promise<TestyPluginStatus> {
    const context = await this.resolveContext(contextSessionId, AbortSignal.timeout(this.config.toolCallTimeoutMs))
    return {
      supported: process.platform === 'win32' && process.arch === 'x64', connected: this.connected, workspace: this.workspace,
      model: context.model,
      activeRuns: [...this.contexts.values()].filter(value => value.runId !== undefined).length,
      revision: this.revision,
      ...this.lastError === undefined ? {} : { lastError: this.lastError },
    }
  }

  /**
   * Return the installed engine's complete MCP tool descriptions and JSON schemas.
   * @returns the engine's complete validated tool catalog.
   */
  @Remote
  tools(): JsonValue[] {
    return [...this.catalog.values()].map(tool => wire.json().parse(tool))
  }

  /**
   * Run an engine operation from the authenticated Cortex testing pane.
   * @param name - exact installed tool name.
   * @param args - arguments validated by the backend's tool schema.
   * @param contextSessionId - optional saved or live chat supplying the model and workspace.
   * @param signal - caller cancellation supplied by the API gateway.
   * @returns MCP content and structured data, including explicit operation failures.
   */
  @Remote
  call(
    name: string, args: Record<string, JsonValue>, contextSessionId: SessionId | undefined, signal: AbortSignal,
  ): Promise<TestyToolResult> {
    return this.own((async () => {
      const context = await this.resolveContext(contextSessionId, AbortSignal.any([signal, this.lifetime.signal]))
      return this.executeOperation(name, args, signal, context, true)
    })())
  }

  /**
   * Read Testy's workspace-contained screenshot or report resource.
   * @param uri - resource URI returned by Testy.
   * @param signal - caller cancellation supplied by the API gateway.
   * @returns typed text or base64 resource content.
   */
  @Remote
  resource(uri: string, signal: AbortSignal): Promise<JsonValue> {
    if (!uri.startsWith('testy:')) throw new Error('Only Testy evidence resources can be opened here.')
    return this.own(this.requireBackend().resource(uri, AbortSignal.any([signal, this.lifetime.signal])))
  }

  /**
   * Open the packaged native Studio against this workspace and the initiating Cortex model.
   * @param contextSessionId - optional selected chat supplying the model and workspace context.
   * @returns completion once the native Studio process has started.
   */
  @Remote
  nativeStudio(contextSessionId?: SessionId): Promise<void> {
    return this.own(this.openStudio(contextSessionId))
  }

  private async openStudio(contextSessionId: SessionId | undefined): Promise<void> {
    this.requireBackend()
    const executable = join(dirname(this.executable), 'Testy.Studio.exe')
    await access(executable)
    this.requireBackend()
    const bridge = this.bridge
    if (bridge === undefined) throw new Error('The Testy model bridge is unavailable.')
    const operation = await this.resolveContext(contextSessionId, this.lifetime.signal)
    const context = await this.createContext(operation, true)
    let child: ChildProcess
    try {
      this.requireBackend()
      child = spawn(executable, ['--workspace', this.workspace], {
        cwd: this.workspace,
        env: {
          ...scrubbedParentEnv(), TESTY_CORTEX_INTEGRATED: '1',
          TESTY_CORTEX_BRIDGE_URL: bridge.endpoint, TESTY_CORTEX_BRIDGE_TOKEN: bridge.token,
          TESTY_CORTEX_CONTEXT: context.handle.contextId,
        },
        windowsHide: false, stdio: 'ignore',
      }) } catch (error) { await this.release(context.handle.contextId); throw error }
    const studio: OwnedStudio = { child, closed: new Promise<void>((resolveClosed) => {
      child.once('close', () => {
        this.studios.delete(studio)
        void this.release(context.handle.contextId).then(resolveClosed, (error: unknown) => {
          this.ctx.logger.warn('Testy Studio audit could not be flushed:', error)
          resolveClosed()
        })
      })
    }) }
    this.studios.add(studio)
    await new Promise<void>((resolveSpawn, reject) => {
      child.once('spawn', resolveSpawn)
      child.once('error', reject)
    }).catch(async (error: unknown) => { await this.release(context.handle.contextId); throw error })
  }

  private async start(): Promise<void> {
    try {
      if (process.platform !== 'win32' || process.arch !== 'x64') throw new Error('Testy desktop testing requires Windows x64.')
      await access(this.executable)
      await mkdir(this.workspace, { recursive: true })
      this.lifetime.signal.throwIfAborted()
      this.bridge = await createTestyModelBridge(this.ctx, {
        maxRequestBytes: this.config.maxMessageBytes, maxOutputTokens: this.config.maxOutputTokens,
      })
      this.backend = new TestyBackend({
        executable: this.executable, args: this.config.arguments, workspace: this.workspace,
        bridgeUrl: this.bridge.endpoint, bridgeToken: this.bridge.token,
        timeoutMs: this.config.toolCallTimeoutMs, maxMessageBytes: this.config.maxMessageBytes,
        shutdownGraceMs: this.config.shutdownGraceMs,
        diagnostic: (error) => { this.ctx.logger.warn(`Testy protocol diagnostic: ${error.message}`) },
        disconnected: (error) => {
          this.connected = false
          this.lastError = `${error?.message ?? 'The Testy engine disconnected.'} Disable and enable the Testy plugin to restart it.`
          this.ctx.logger.warn(`Testy unavailable: ${this.lastError}`)
          for (const dispose of this.registrations.splice(0)) dispose()
          void this.shutdown(new Error(this.lastError)).catch((error: unknown) => { this.ctx.logger.warn('Testy engine cleanup failed:', error) })
        },
      })
      const tools = await this.backend.connect(this.lifetime.signal)
      this.lifetime.signal.throwIfAborted()
      for (const tool of tools) {
        if (!/^[a-zA-Z0-9_]{1,50}$/.test(tool.name) || this.catalog.has(tool.name)) throw new Error('Testy returned an invalid or duplicate tool name.')
        this.catalog.set(tool.name, tool)
        if (tool._meta?.['cortex/humanOnly'] === true) continue
        this.registrations.push(this.ctx.tools.register(createMcpToolDefinition(this.ctx, {
          name: `mcp__testy__${tool.name}`, rawName: tool.name,
          description: tool.description ?? tool.name, inputSchema: tool.inputSchema,
          outputSchema: tool.outputSchema,
          call: (args, execution) => this.execute(tool.name, args, execution.signal, execution.agent),
        })))
      }
      this.connected = true
      this.lastError = undefined
    } catch (error) {
      this.lastError ??= error instanceof Error ? error.message : String(error)
      for (const dispose of this.registrations.splice(0)) dispose()
      this.catalog.clear()
      await this.backend?.dispose()
      await this.bridge?.dispose()
      this.ctx.logger.warn(`Testy unavailable: ${this.lastError}`)
    }
  }

  private requireBackend(): TestyBackend {
    if (!this.connected || this.backend === undefined) throw new Error(this.lastError ?? 'The Testy engine is starting.')
    this.lifetime.signal.throwIfAborted()
    return this.backend
  }

  private contextForAgent(agent?: Agent): OperationContext {
    return {
      model: modelFor(agent, this.ctx.agentDefaultModel.currentSelection()),
      ...agent === undefined ? {} : { sessionId: agent.id, workspace: agent.session.header.cwd },
    }
  }

  private async resolveContext(sessionId: SessionId | undefined, signal: AbortSignal): Promise<OperationContext> {
    if (sessionId === undefined) return this.contextForAgent()
    signal.throwIfAborted()
    const agent = this.ctx.agents.get(sessionId)
    const session = this.ctx.sessions.get(sessionId)
    if (session !== undefined) {
      const selected = this.ctx.get('sessionProjections')?.snapshot(session, ['modelSelection']).values.modelSelection?.next
      const fallback = modelFor(agent, session.requestHeader()?.config ?? this.ctx.agentDefaultModel.currentSelection())
      return { sessionId, workspace: session.header.cwd,
        model: selected === null || selected === undefined ? fallback : modelRoute(selected),
      }
    }
    const query = this.ctx.get('sessionQuery')
    if (query === undefined) throw new Error('The selected Cortex chat could not be loaded. Open it again to continue testing.')
    using observation = await query.observeSession(sessionId, { signal })
    const header = observation.events.findLast(event => event.type === 'request/header')
    const selected = observation.projections?.values.modelSelection?.next
    return {
      sessionId, workspace: observation.header.cwd,
      model: modelRoute(selected ?? header?.data.header.config ?? this.ctx.agentDefaultModel.currentSelection()),
    }
  }

  private async createContext(selection: OperationContext, followDefault = false): Promise<OwnedContext> {
    if (this.bridge === undefined) throw new Error('The Testy model bridge is unavailable.')
    this.lifetime.signal.throwIfAborted()
    if (this.contexts.size + this.creatingContexts >= this.config.maxModelContexts) throw new Error('Too many active Testy model operations. Finish or cancel an existing run.')
    const session = this.ctx.sessions.prepare(SessionId(`testy-${randomUUID()}`), {
      meta: { cwd: selection.workspace ?? this.workspace, ...selection.sessionId === undefined ? {} : { parentSession: selection.sessionId, origin: 'subagent' } },
    })
    const detach = this.ctx.sessions.enter(session)
    this.creatingContexts += 1
    let writer: SessionHandle | undefined
    let handle: TestyModelRunHandle
    try {
      writer = await this.ctx.get('sessionPersistence')?.create(session.header, {
        inheritedEventCount: session.inheritedEventCount, signal: this.lifetime.signal,
      })
      this.lifetime.signal.throwIfAborted()
      this.ctx.sessions.announce(session)
      this.ctx.get('sessionTitle')?.rename(session, 'Testy model activity')
      handle = this.bridge.registerRun({
        session,
        model: selection.model, signal: this.lifetime.signal,
        ...followDefault ? { resolveModel: () => this.ctx.agentDefaultModel.currentSelection() } : {},
      })
    } catch (error) {
      try { await writer?.close() } finally { detach() }
      throw error
    } finally { this.creatingContexts -= 1 }
    const timer = setTimeout(() => {
      void this.release(handle.contextId).catch((error: unknown) => { this.ctx.logger.warn('Testy operation audit could not be flushed:', error) })
    }, this.config.modelContextTtlMs)
    timer.unref()
    const owned: OwnedContext = { handle, timer, session, detach, ...writer === undefined ? {} : { writer } }
    this.contexts.set(handle.contextId, owned)
    return owned
  }

  private release(id: string): Promise<void> {
    const context = this.contexts.get(id)
    if (context === undefined) return Promise.resolve()
    this.contexts.delete(id)
    clearTimeout(context.timer)
    return this.own((async () => {
      try {
        await context.handle.dispose()
        await this.ctx.sessions.flush(context.session)
      } finally {
        try { await context.writer?.close() } finally { context.detach() }
      }
    })())
  }

  private execute(
    name: string, args: Record<string, unknown>, signal: AbortSignal, agent?: Agent, human = false,
  ): Promise<TestyToolResult> {
    return this.own(this.executeOperation(name, args, signal, this.contextForAgent(agent), human))
  }

  private async executeOperation(
    name: string, args: Record<string, unknown>, signal: AbortSignal, selection: OperationContext, human: boolean,
  ): Promise<TestyToolResult> {
    const backend = this.requireBackend()
    const tool = this.catalog.get(name)
    if (tool === undefined) throw new Error(`Unknown Testy operation: ${name}`)
    if (tool._meta?.['cortex/humanOnly'] === true && !human) throw new Error('This Testy operation requires the human-controlled testing pane.')
    signal.throwIfAborted()
    const usesModel = tool._meta?.['cortex/usesModel'] === true
    const context = usesModel ? await this.createContext(selection) : undefined
    let retained = false
    try {
      const result = await backend.call(
        name, args, AbortSignal.any([signal, this.lifetime.signal]), context?.handle.contextId, selection.workspace,
      )
      if (tool.annotations?.readOnlyHint !== true) this.revision += 1
      const data = result.structuredContent
      if (context !== undefined && !result.isError && record(data) && data.running === true) {
        const id = operationId(data)
        if (id === undefined) throw new Error('Testy returned a background operation without a run or task id.')
        context.runId = id
        retained = true
      }
      if (!result.isError && record(data)) {
        const outcomes = Array.isArray(data.tasks) ? data.tasks : Array.isArray(data.runs) ? data.runs : [data]
        for (const outcome of outcomes) {
          if (!record(outcome) || outcome.running !== false) continue
          const finishedId = operationId(outcome)
          if (finishedId === undefined) continue
          for (const [id, owned] of this.contexts) if (owned.runId === finishedId) await this.release(id)
        }
      }
      return result
    } finally {
      if (context !== undefined && !retained) await this.release(context.handle.contextId)
    }
  }

  private own<T>(operation: Promise<T>): Promise<T> {
    this.pending.add(operation)
    void operation.then(() => { this.pending.delete(operation) }, () => { this.pending.delete(operation) })
    return operation
  }

  private shutdown(reason = new Error('The Testy plugin was disabled.')): Promise<void> {
    this.stopping ??= (async () => {
      this.lifetime.abort(reason)
      this.connected = false
      for (const dispose of this.registrations.splice(0)) dispose()
      await this.startup
      const studios = [...this.studios]
      const cleanup = await Promise.allSettled([
        ...studios.map(async (studio) => { await stopOwnedProcessTree(studio.child); await studio.closed }),
        this.backend?.dispose(),
        ...[...this.contexts.keys()].map(id => this.release(id)),
        this.bridge?.dispose(),
      ])
      await Promise.allSettled([...this.pending])
      this.catalog.clear()
      const failures: unknown[] = []
      for (const result of cleanup) if (result.status === 'rejected') failures.push(result.reason)
      if (failures.length > 0) throw new AggregateError(failures, 'Testy shutdown could not settle every owned resource.')
    })()
    return this.stopping
  }
}

/** Distinguish independently numbered native task and run registries. */
function operationId(value: Record<string, JsonValue>): string | undefined {
  if (typeof value.taskId === 'string') return `task:${value.taskId}`
  if (typeof value.runId === 'string') return `run:${value.runId}`
  return undefined
}

export default TestyService
