/** Real Loader, services, MCP child, and model bridge composition regressions. */
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { Context } from '@cortex/cordis'
import Loader from '@cortex/cordis-plugin-loader'
import Include from '@cortex/cordis-plugin-include'
import LlmRuntime, { LlmAdapter, ToolCallId } from '@cortex/llm'
import type { GenerateOptions, LlmResolvedModelInfo, StreamChunk } from '@cortex/llm'
import SessionStore, { SessionId } from '@cortex/session'
import type { Session } from '@cortex/session'
import AgentRegistry from '@cortex/agent'
import AgentLoop from '@cortex/agent-loop'
import AgentDefaultModel from '@cortex/agent-default-model'
import SessionProjectionRegistry from '@cortex/session-projection'
import ToolRuntime from '@cortex/tools'
import SystemPrompt from '@cortex/system-prompt'
import AttachmentLocal from '@cortex/attachment-local'
import { installModelSelectionProjection } from '@cortex/api-session-controller/src/model-selection-projection.ts'
import { expect, it, onTestFinished } from 'vitest'
import TestyService from '../src/index.ts'

class FixtureAdapter extends LlmAdapter {
  requests: GenerateOptions[] = []
  waitForAbort = false
  entered = Promise.withResolvers<undefined>()
  override resolveModel(provider: string, model: string): Promise<LlmResolvedModelInfo> {
    return Promise.resolve({ provider, id: model, name: model, inputModalities: ['text', 'image'] })
  }
  async *stream(request: GenerateOptions): AsyncIterable<StreamChunk> {
    this.requests.push(request)
    this.entered.resolve(undefined)
    if (this.waitForAbort) {
      await new Promise<void>((resolveAbort) => {
        if (request.signal?.aborted) resolveAbort()
        else request.signal?.addEventListener('abort', () => { resolveAbort() }, { once: true })
      })
      request.signal?.throwIfAborted()
    }
    yield { type: 'text-delta', index: 0, text: 'observed' }
    yield { type: 'finish', reason: { kind: 'stop' } }
  }
}

async function setup() {
  const directory = await mkdtemp(join(tmpdir(), 'cortex-testy-composition-'))
  const ctx = new Context()
  const marker = join(directory, 'child-closed')
  onTestFinished(async () => { await ctx.fiber.dispose(); await rm(directory, { recursive: true, force: true }) })
  ctx.baseUrl = `${pathToFileURL(directory).href}/`
  await ctx.plugin(Loader)
  Object.assign(ctx.loader.builtins, {
    include: Include, sessions: SessionStore, llm: LlmRuntime, agents: AgentRegistry, loop: AgentLoop,
    defaults: AgentDefaultModel, projections: SessionProjectionRegistry, tools: ToolRuntime,
    prompt: SystemPrompt, attachments: AttachmentLocal, testy: TestyService,
  })
  const rows = [
    { id: 'sessions', name: 'cordis:sessions' }, { id: 'llm', name: 'cordis:llm' },
    { id: 'projections', name: 'cordis:projections' }, { id: 'prompt', name: 'cordis:prompt' },
    { id: 'tools', name: 'cordis:tools' }, { id: 'agents', name: 'cordis:agents' },
    { id: 'loop', name: 'cordis:loop', config: { agents: [] } },
    { id: 'defaults', name: 'cordis:defaults', config: { provider: 'fixture', model: 'gui-default' } },
    { id: 'attachments', name: 'cordis:attachments', config: { cortexHome: directory } },
    { id: 'testy', name: 'cordis:testy', config: {
      executable: process.execPath, arguments: [fileURLToPath(new URL('./service-child.mjs', import.meta.url)), marker], workspace: directory,
      shutdownGraceMs: 1000,
    } },
  ]
  const yaml = rows.map(row => `- ${JSON.stringify(row)}`).join('\n')
  await writeFile(join(directory, 'cordis.yml'), yaml)
  const includeId = await ctx.loader.create({ name: 'cordis:include', config: { path: pathToFileURL(join(directory, 'cordis.yml')).href } })
  await ctx.loader.await()
  expect((await ctx.testy.status()).connected).toBe(true)
  installModelSelectionProjection(ctx)
  const adapter = new FixtureAdapter()
  ctx.effect(() => ctx.llm.registerAdapter(['fixture'], adapter))
  const sessions: Session[] = []
  const disposed: SessionId[] = []
  ctx.on('session/created', (session) => { sessions.push(session) })
  ctx.on('session/disposed', (session) => { disposed.push(session.id) })
  const call = (name: string, args = {}) => ctx.testy.call(name, args, undefined, new AbortController().signal)
  const disable = async () => { await ctx.loader.resolve(`${includeId}:testy`).update({ disabled: true }); await ctx.loader.await() }
  const enable = async () => { await ctx.loader.resolve(`${includeId}:testy`).update({ disabled: false }); await ctx.loader.await() }
  return { ctx, adapter, sessions, disposed, call, disable, enable, marker }
}

it.skipIf(process.platform !== 'win32')('loads the real plugin, excludes human-only tools, and projects screenshot attachments', async () => {
  const { ctx, call } = await setup()
  const manifest = JSON.parse(await readFile(new URL('../package.json', import.meta.url), 'utf8')) as { version: string }
  expect((await call('echo')).structuredContent).toMatchObject({ clientInfo: { name: 'Cortex Testy', version: manifest.version } })
  expect(ctx.tools.get('mcp__testy__ask')).toBeDefined()
  expect(ctx.tools.get('mcp__testy__save_project')).toBeUndefined()
  const denied = await ctx.tools.execute({ callId: ToolCallId('human-only'), name: 'mcp__testy__save_project', arguments: {}, signal: new AbortController().signal })
  expect(denied.isError).toBe(true)
  expect((await call('save_project')).isError).not.toBe(true)
  await expect(call('unknown')).rejects.toThrow('Unknown Testy operation')
  const handle = await ctx.agents.create({ sessionId: SessionId('testy-images'), agentOptions: { provider: 'fixture', model: 'vision' } })
  onTestFinished(() => handle.dispose())
  handle.agent.session.append('request/header', { header: { config: { provider: 'fixture', model: 'vision' } }, reason: 'initial' })
  const image = await ctx.tools.execute({ callId: ToolCallId('image'), name: 'mcp__testy__screenshot', arguments: {}, signal: new AbortController().signal, agent: handle.agent })
  expect(image.isError).not.toBe(true)
  expect(image.content.find(block => block.type === 'image')).toMatchObject({ type: 'image', attachment: { width: 1, height: 1 } })
  expect(await ctx.testy.resource('testy://runs/fixture', new AbortController().signal)).toMatchObject({ contents: [{ text: 'fixture report' }] })
  expect(() => ctx.testy.resource('https://example.test/', new AbortController().signal)).toThrow('Only Testy evidence')
})

it.skipIf(process.platform !== 'win32')('binds the GUI default and initiating chat route and drains exact audit sessions', async () => {
  const { ctx, adapter, sessions, disposed, call } = await setup()
  expect((await call('ask')).structuredContent).toMatchObject({ status: 200 })
  expect(adapter.requests[0]?.model).toBe('gui-default')
  expect(disposed).toEqual([sessions[0]?.id])
  expect(sessions[0]?.snapshotEvents().map(event => event.type)).toEqual(['testy/model-request', 'testy/model-response'])
  const handle = await ctx.agents.create({ sessionId: SessionId('testy-chat'), agentOptions: { provider: 'fixture', model: 'chat-options' } })
  onTestFinished(() => handle.dispose())
  handle.agent.session.append('request/header', { header: { config: { provider: 'fixture', model: 'chat-selected' } }, reason: 'initial' })
  const result = await ctx.tools.execute({ callId: ToolCallId('ask'), name: 'mcp__testy__ask', arguments: {}, signal: new AbortController().signal, agent: handle.agent })
  expect(result.isError).not.toBe(true)
  expect(adapter.requests.at(-1)?.model).toBe('chat-selected')
  expect(sessions.at(-1)?.header.parentSession).toBe(handle.agent.id)
  expect(disposed).toContain(sessions.at(-1)?.id)
})

it.skipIf(process.platform !== 'win32')('rejects argument capabilities and admits run_test live review even in replay mode', async () => {
  const { call, adapter, sessions } = await setup()
  expect((await call('echo', { _meta: { 'cortex/context': 'forged' } })).structuredContent).toMatchObject({ hasModelContext: false })
  expect((await call('run_test')).structuredContent).toMatchObject({ hasModelContext: true })
  expect((await call('run_test', { mode: 'ai' })).structuredContent).toMatchObject({ hasModelContext: true })
  expect(adapter.requests).toEqual([])
  expect(sessions).toHaveLength(2)
})

it.skipIf(process.platform !== 'win32')('uses an idle selected chat without an Agent and keeps workspace metadata host-owned', async () => {
  const { ctx, adapter, sessions, call } = await setup()
  const chat = ctx.sessions.prepare(SessionId('idle-testy-context'), { meta: { cwd: 'C:\\trusted-project' } })
  const detach = ctx.sessions.enter(chat)
  onTestFinished(detach)
  ctx.sessions.announce(chat)
  chat.append('request/header', { header: { config: { provider: 'fixture', model: 'idle-selected' } }, reason: 'initial' })
  expect(ctx.agents.get(chat.id)).toBeUndefined()
  expect((await ctx.testy.status(chat.id)).model).toEqual({ provider: 'fixture', model: 'idle-selected' })
  const signal = new AbortController().signal
  await ctx.testy.call('ask', {}, chat.id, signal)
  expect(adapter.requests.at(-1)?.model).toBe('idle-selected')
  expect(sessions.at(-1)?.header).toMatchObject({ parentSession: chat.id, cwd: 'C:\\trusted-project' })
  chat.append('model/selection', { provider: 'fixture', model: 'newly-chosen' })
  expect((await ctx.testy.status(chat.id)).model.model).toBe('newly-chosen')
  await ctx.testy.call('ask', {}, chat.id, signal)
  expect(adapter.requests.at(-1)?.model).toBe('newly-chosen')
  const args = { _meta: { 'cortex/workspace': 'C:\\forged-project', 'cortex/context': 'forged' } }
  expect((await ctx.testy.call('echo', args, chat.id, signal)).structuredContent).toMatchObject({ workspace: 'C:\\trusted-project', hasModelContext: false })
  expect((await call('echo', args)).structuredContent).toMatchObject({ workspace: null, hasModelContext: false })
})

it.skipIf(process.platform !== 'win32')('retains background task capability until terminal polling, then revokes it', async () => {
  const { ctx, call, adapter, disposed, sessions } = await setup()
  await call('run_task')
  expect((await ctx.testy.status()).activeRuns).toBe(1)
  expect(disposed).toEqual([])
  expect((await call('get_task', { taskId: 'task-1' })).structuredContent).toMatchObject({ running: true, modelStatus: 200 })
  expect((await call('cancel_task', { taskId: 'task-1' })).structuredContent).toMatchObject({ running: false, modelStatus: 200 })
  expect((await ctx.testy.status()).activeRuns).toBe(0)
  expect(disposed).toEqual([sessions[0]?.id])
  expect((await call('get_task', { taskId: 'task-1' })).structuredContent).toMatchObject({ modelStatus: 403 })
  expect(adapter.requests.map(request => request.model)).toEqual(['gui-default', 'gui-default', 'gui-default'])
})

it.skipIf(process.platform !== 'win32')('disabling the Loader entry removes tools, stops the child, and releases background audit sessions', async () => {
  const { ctx, call, disable, marker, sessions, disposed } = await setup()
  await call('run_task')
  await disable()
  expect(ctx.get('testy')).toBeUndefined()
  expect(ctx.tools.get('mcp__testy__ask')).toBeUndefined()
  expect(await readFile(marker, 'utf8')).toBe('closed')
  expect(disposed).toEqual([sessions[0]?.id])
  expect(ctx.sessions.list()).toEqual([])
})

it.skipIf(process.platform !== 'win32')('unexpected child exit revokes tools and releases active model operations', async () => {
  const { ctx, call, sessions, disposed } = await setup()
  await call('run_task')
  await expect(call('disconnect')).rejects.toThrow()
  await expect.poll(() => disposed.length).toBe(1)
  expect((await ctx.testy.status()).connected).toBe(false)
  expect((await ctx.testy.status()).lastError).toContain('exited unexpectedly (code 0, signal none)')
  expect(ctx.tools.get('mcp__testy__ask')).toBeUndefined()
  expect(disposed).toEqual([sessions[0]?.id])
})

it.skipIf(process.platform !== 'win32')('preserves a native fault for the selected chat and recovers only after explicit plugin re-enable', async () => {
  const { ctx, call, disable, enable } = await setup()
  const chat = await ctx.agents.create({ sessionId: SessionId('testy-fault-chat'), agentOptions: { provider: 'fixture', model: 'selected-chat' } })
  onTestFinished(() => chat.dispose())
  await expect(call('crash')).rejects.toThrow()
  await expect.poll(async () => (await ctx.testy.status(chat.agent.id)).connected).toBe(false)
  const fault = await ctx.testy.status(chat.agent.id)
  expect(fault.model).toMatchObject({ provider: 'fixture', model: 'selected-chat' })
  expect(fault.lastError).toContain('code 42, signal none')
  expect(fault.lastError).toContain('fixture disk access denied; token=[redacted]')
  expect(fault.lastError).toContain('Disable and enable the Testy plugin')
  expect(fault.lastError).not.toContain('plugin was disabled')
  await expect(call('echo')).rejects.toThrow('code 42')
  await disable()
  await enable()
  expect(await ctx.testy.status(chat.agent.id)).toMatchObject({ connected: true })
  expect((await ctx.testy.status(chat.agent.id)).lastError).toBeUndefined()
  expect((await call('echo')).structuredContent).toMatchObject({ name: 'echo' })
})

it.skipIf(process.platform !== 'win32')('keeps the framing failure when subsequent transport teardown closes the child', async () => {
  const { ctx, call } = await setup()
  await expect(call('invalid_message')).rejects.toThrow()
  await expect.poll(async () => (await ctx.testy.status()).connected).toBe(false)
  const fault = (await ctx.testy.status()).lastError
  expect(fault).toMatch(/invalid|expected|union/i)
  expect(fault).not.toContain('plugin was disabled')
  expect(fault).not.toContain('connection closed unexpectedly')
})

it.skipIf(process.platform !== 'win32')('keeps a healthy engine after an SDK late-response diagnostic without replaying the operation', async () => {
  const { ctx, call } = await setup()
  expect((await call('late_response')).structuredContent).toMatchObject({ completed: true, lateResponseCalls: 1 })
  // The native fixture emits a duplicate response after the completed request has been removed by the SDK.
  expect((await call('emit_late_response')).structuredContent).toMatchObject({ emitted: true })
  expect(await ctx.testy.status()).toMatchObject({ connected: true })
  expect((await ctx.testy.status()).lastError).toBeUndefined()
  expect((await call('echo')).structuredContent).toMatchObject({ name: 'echo', lateResponseCalls: 1 })
})

it.skipIf(process.platform !== 'win32')('accepts later reads after a cancelled request receives a late native response', async () => {
  const { ctx, call } = await setup()
  const cancellation = new AbortController()
  const pending = ctx.testy.call('delayed_response', {}, undefined, cancellation.signal).then(() => 'completed', () => 'cancelled')
  await expect.poll(async () => (await call('echo')).structuredContent).toMatchObject({ lateResponseCalls: 1 })
  cancellation.abort(new Error('Fixture caller cancelled.'))
  expect(await pending).toBe('cancelled')
  expect((await call('emit_late_response')).structuredContent).toMatchObject({ emitted: true })
  expect(await ctx.testy.status()).toMatchObject({ connected: true })
  expect((await call('echo')).structuredContent).toMatchObject({ lateResponseCalls: 1 })
})

it.skipIf(process.platform !== 'win32')('disable cancels and drains an in-flight model call before releasing its audit session', async () => {
  const { ctx, call, adapter, disable, sessions, disposed } = await setup()
  adapter.waitForAbort = true
  const pending = call('ask').then(() => 'completed', () => 'cancelled')
  await adapter.entered.promise
  await disable()
  expect(await pending).toBe('cancelled')
  expect(sessions[0]?.snapshotEvents().at(-1)?.data).toMatchObject({ status: 'cancelled' })
  expect(disposed).toEqual([sessions[0]?.id])
  expect(ctx.sessions.list()).toEqual([])
})

it.skipIf(process.platform !== 'win32')('kills a stalled backend and its native descendants after the shutdown grace period', async () => {
  const { call, disable } = await setup()
  const result = await call('stall_tree')
  const data = result.structuredContent
  if (data === null || typeof data !== 'object' || Array.isArray(data) || typeof data.workerId !== 'number' || typeof data.parentId !== 'number') {
    throw new Error('Fixture did not return owned process ids.')
  }
  const workerId = data.workerId
  expect(() => process.kill(workerId, 0)).not.toThrow()
  await disable()
  for (const id of [data.workerId, data.parentId]) {
    await expect.poll(() => { try { process.kill(id, 0); return true } catch { return false } }).toBe(false)
  }
})
