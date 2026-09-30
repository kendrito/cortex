/** Opt-in Windows integration: real Loader, native engine, sample app, persistence and model bridge. */
import { access, mkdtemp, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { Context } from '@cortex/cordis'
import Loader from '@cortex/cordis-plugin-loader'
import Include from '@cortex/cordis-plugin-include'
import LlmRuntime, { ToolCallId } from '@cortex/llm'
import SessionStore, { SessionId } from '@cortex/session'
import type { Session } from '@cortex/session'
import JsonlSessionPersistence from '@cortex/session-persistence-jsonl'
import SqliteSessionQuery from '@cortex/session-query-sqlite'
import AgentRegistry from '@cortex/agent'
import AgentLoop from '@cortex/agent-loop'
import AgentDefaultModel from '@cortex/agent-default-model'
import SessionProjectionRegistry from '@cortex/session-projection'
import ToolRuntime from '@cortex/tools'
import SystemPrompt from '@cortex/system-prompt'
import AttachmentLocal from '@cortex/attachment-local'
import { installModelSelectionProjection } from '@cortex/api-session-controller/src/model-selection-projection.ts'
import type {} from '@cortex/api-session-controller/types'
import { z } from 'zod'
import { describe, expect, it, onTestFinished } from 'vitest'
import TestyService from '../src/index.ts'
import type { JsonValue, TestyToolResult } from '../src/types.ts'
import { NativeTestyAdapter, explanation } from './native-model-fixture.ts'

const runtime = process.env.CORTEX_TESTY_NATIVE_RUNTIME === undefined
  ? fileURLToPath(new URL('../runtime/win-x64/', import.meta.url)) : resolve(process.env.CORTEX_TESTY_NATIVE_RUNTIME)
function data(result: TestyToolResult): Record<string, JsonValue> {
  expect(result.isError, JSON.stringify(result.content)).not.toBe(true)
  return z.record(z.string(), z.json()).parse(result.structuredContent)
}

async function setup() {
  await access(join(runtime, 'Testy.Cli.exe'))
  await access(join(runtime, 'Testy.TestLab.exe'))
  const directory = await mkdtemp(join(tmpdir(), 'cortex-testy-native-'))
  const ctx = new Context()
  const adapter = new NativeTestyAdapter()
  onTestFinished(async () => {
    adapter.continue()
    await ctx.fiber.dispose()
    await rm(directory, { recursive: true, force: true })
  })
  ctx.baseUrl = `${pathToFileURL(directory).href}/`
  await ctx.plugin(Loader)
  Object.assign(ctx.loader.builtins, {
    include: Include, sessions: SessionStore, persistence: JsonlSessionPersistence, query: SqliteSessionQuery, llm: LlmRuntime,
    agents: AgentRegistry, loop: AgentLoop, defaults: AgentDefaultModel, projections: SessionProjectionRegistry,
    tools: ToolRuntime, prompt: SystemPrompt, attachments: AttachmentLocal, testy: TestyService,
    selection: { inject: ['sessionProjections'], apply: installModelSelectionProjection },
  })
  const rows = [
    { id: 'sessions', name: 'cordis:sessions' },
    { id: 'persistence', name: 'cordis:persistence', config: { root: join(directory, 'sessions'), compression: 'none' } },
    { id: 'query', name: 'cordis:query', config: { path: ':memory:', openAt: 'never' } },
    { id: 'llm', name: 'cordis:llm' }, { id: 'projections', name: 'cordis:projections' },
    { id: 'selection', name: 'cordis:selection' },
    { id: 'prompt', name: 'cordis:prompt' }, { id: 'tools', name: 'cordis:tools' },
    { id: 'agents', name: 'cordis:agents' }, { id: 'loop', name: 'cordis:loop', config: { agents: [] } },
    { id: 'defaults', name: 'cordis:defaults', config: { provider: 'native-fixture', model: 'gui-default' } },
    { id: 'attachments', name: 'cordis:attachments', config: { cortexHome: directory } },
    { id: 'testy', name: 'cordis:testy', config: {
      executable: join(runtime, 'Testy.Cli.exe'), workspace: join(directory, 'workspace'),
      toolCallTimeoutMs: 60000, shutdownGraceMs: 20000,
    } },
  ]
  const path = join(directory, 'cordis.yml')
  await writeFile(path, rows.map(row => `- ${JSON.stringify(row)}`).join('\n'))
  const includeId = await ctx.loader.create({ name: 'cordis:include', config: { path: pathToFileURL(path).href } })
  await ctx.loader.await()
  expect(await ctx.testy.status(), 'Real bundled native MCP must be connected.').toMatchObject({ supported: true, connected: true })
  ctx.effect(() => ctx.llm.registerAdapter(['native-fixture'], adapter))
  const sessions: Session[] = []
  const disposed: SessionId[] = []
  ctx.on('session/created', (session) => { sessions.push(session) })
  ctx.on('session/disposed', (session) => { disposed.push(session.id) })
  const call = async (name: string, args: Record<string, JsonValue> = {}) =>
    data(await ctx.testy.call(name, args, undefined, AbortSignal.timeout(60000)))
  const disable = async () => { await ctx.loader.resolve(`${includeId}:testy`).update({ disabled: true }); await ctx.loader.await() }
  const persisted = async (id: SessionId) => {
    const handle = await ctx.sessionPersistence.open(id, 'read')
    try { return { header: handle.header, events: (await handle.read()).events } } finally { await handle.close() }
  }
  const finishTask = async (taskId: string) => {
    let result: Record<string, JsonValue> = {}
    await expect.poll(async () => { result = await call('get_task', { taskId }); return result.running }, { timeout: 30000 }).toBe(false)
    expect(result.status, JSON.stringify(result)).toBe('completed')
    return result
  }
  await call('save_preferences', { supportsImages: true, liveReview: false, maximumProviderRetries: 0 })
  return { ctx, adapter, sessions, disposed, call, disable, persisted, finishTask }
}

// Explicit opt-in avoids launching desktop windows during ordinary keyless unit runs.
describe.skipIf(process.platform !== 'win32' || process.arch !== 'x64' || process.env.CORTEX_TESTY_NATIVE !== '1')('real Testy native composition', () => {
  it('runs the real WPF app and retains routed model capabilities until audited tasks settle', { timeout: 120000 }, async () => {
    const { ctx, adapter, sessions, disposed, call, disable, persisted, finishTask } = await setup()
    const saved = await call('create_test', {
      name: 'Native bridge ready-state check', intent: 'Verify the packaged WPF Customer Desk ready state.',
      targetPath: join(runtime, 'Testy.TestLab.exe'),
      steps: [{ title: 'Customer Desk is ready', action: 'assertText', selector: 'id:StatusMessage', value: 'Ready for a new customer.', timeoutMs: 5000 }],
    })
    const testId = z.string().parse(saved.testId)
    const run = await call('run_test', { testId, mode: 'replay', exe: join(runtime, 'Testy.TestLab.exe'), probe: true, wait: true, waitSeconds: 45, timeoutSeconds: 60 })
    expect(run, JSON.stringify(run)).toMatchObject({ running: false, passed: true, status: 'passed' })
    expect(adapter.requests).toEqual([])
    const runId = z.string().parse(run.runId)

    adapter.hold()
    const task = await call('explain_run', { runId })
    const taskId = z.string().parse(task.taskId)
    expect(task).toMatchObject({ running: true })
    await expect.poll(() => adapter.requests.length, { timeout: 30000 }).toBe(1)
    const audit = sessions.at(-1)!
    expect(adapter.requests[0]).toMatchObject({ provider: 'native-fixture', model: 'gui-default', sessionId: audit.id })
    expect((await ctx.testy.status()).activeRuns).toBe(1)
    expect(disposed).not.toContain(audit.id)
    expect((await call('get_task', { taskId })).running).toBe(true)
    const admitted = await persisted(audit.id)
    expect(admitted.events.map(event => event.type)).toContain('testy/model-request')
    const image = adapter.requests[0]?.messages.flatMap(message => message.content).find(block => block.type === 'image')
    expect(image?.type).toBe('image')
    if (image?.type !== 'image') throw new Error('Native explanation did not send recorded screenshot evidence.')
    expect((await ctx.attachments.readImage(image.attachment)).data.length).toBeGreaterThan(100)
    adapter.continue()
    expect((await finishTask(taskId)).result).toMatchObject({ explanation, verdict: 'passed' })
    expect((await ctx.testy.status()).activeRuns).toBe(0)
    expect(disposed).toContain(audit.id)
    expect((await persisted(audit.id)).events).toEqual(audit.snapshotEvents())

    const chat = await ctx.agents.create({ sessionId: SessionId('native-testy-chat'), agentOptions: { provider: 'native-fixture', model: 'chat-options' } })
    onTestFinished(() => chat.dispose())
    chat.agent.session.append('turn/start', { turn: 1 })
    chat.agent.session.append('request/header', { header: { config: { provider: 'native-fixture', model: 'chat-selected' } }, reason: 'initial' })
    chat.agent.session.append('turn/end', { turn: 1, reason: { kind: 'completed' } })
    const selected = await ctx.tools.execute({ callId: ToolCallId('native-explanation'), name: 'mcp__testy__explain_run', arguments: { runId }, signal: AbortSignal.timeout(60000), agent: chat.agent })
    expect(selected.isError, JSON.stringify(selected)).not.toBe(true)
    await expect.poll(() => adapter.requests.length, { timeout: 30000 }).toBe(2)
    const active = z.object({ tasks: z.array(z.object({ taskId: z.string() })) }).parse(await call('list_tasks'))
    await finishTask(active.tasks[0]!.taskId)
    expect(adapter.requests[1]).toMatchObject({ model: 'chat-selected' })
    const chatAudit = sessions.find(session => session.id === adapter.requests[1]?.sessionId)!
    expect((await persisted(chatAudit.id)).header.parentSession).toBe(chat.agent.id)

    const chatId = chat.agent.session.id
    chat.agent.session.append('model/selection', { provider: 'native-fixture', model: 'pending-selection' })
    expect(await ctx.testy.status(chatId)).toMatchObject({ model: { provider: 'native-fixture', model: 'pending-selection' } })
    await chat.dispose()
    expect(ctx.sessions.get(chatId)).toBeUndefined()
    expect(await ctx.testy.status(chatId)).toMatchObject({ model: { provider: 'native-fixture', model: 'pending-selection' } })
    const cold = data(await ctx.testy.call('explain_run', { runId }, chatId, AbortSignal.timeout(60000)))
    await finishTask(z.string().parse(cold.taskId))
    expect(adapter.requests[2]).toMatchObject({ model: 'pending-selection' })
    expect((await persisted(sessions.at(-1)!.id)).header.parentSession).toBe(chatId)

    adapter.hold()
    const cancelled = await call('explain_run', { runId })
    await expect.poll(() => adapter.requests.length, { timeout: 30000 }).toBe(4)
    const cancelAudit = sessions.at(-1)!
    const stopped = await call('cancel_task', { taskId: z.string().parse(cancelled.taskId) })
    expect(stopped).toMatchObject({ running: false, status: 'cancelled' })
    expect((await ctx.testy.status()).activeRuns).toBe(0)
    expect((await persisted(cancelAudit.id)).events.at(-1)?.data).toMatchObject({ status: 'cancelled' })
    await disable()
    expect(ctx.get('testy')).toBeUndefined()
    expect(ctx.tools.get('mcp__testy__explain_run')).toBeUndefined()
    expect(disposed).toContain(cancelAudit.id)
  })

  it('asks before ambiguous target input, then selects observed app, drafts and verifies an AI test', { timeout: 120000 }, async () => {
    const { ctx, adapter, sessions, call, disable, persisted, finishTask } = await setup()
    const sample = await call('launch_sample', { sample: 'testlab' })
    const samplePid = z.number().int().positive().parse(sample.pid)
    adapter.workflow = 'ai-test'
    adapter.clarify = true
    adapter.targetPid = samplePid
    const phases = adapter.phases
    const before = await call('list_tests')
    const ambiguous = await call('ai_test', { instructions: 'Test the application.' })
    expect((await finishTask(z.string().parse(ambiguous.taskId))).result).toMatchObject({ kind: 'needsTarget', question: 'Which application should I test?' })
    expect(phases).toEqual(['target'])
    expect(await call('list_tests')).toEqual(before)
    expect((await ctx.testy.status()).activeRuns).toBe(0)
    const ambiguityAudit = sessions.at(-1)!
    expect((await persisted(ambiguityAudit.id)).events.map(event => event.type)).toEqual(['testy/model-request', 'testy/model-response'])

    adapter.clarify = false
    const started = await call('ai_test', { instructions: 'Verify the Testy Customer Desk application is ready for a new customer.' })
    const completed = await finishTask(z.string().parse(started.taskId))
    expect(completed.result, JSON.stringify(completed)).toMatchObject({ kind: 'completed', status: 'passed', target: { pid: samplePid } })
    expect(phases).toEqual(['target', 'target', 'draft', 'assert', 'complete'])
    const audit = sessions.at(-1)!
    const requests = adapter.requests.filter(request => request.sessionId === audit.id)
    expect(requests).toHaveLength(4)
    expect(requests.every(request => request.provider === 'native-fixture' && request.model === 'gui-default')).toBe(true)
    expect(requests.some(request => request.messages.some(message => message.content.some(block => block.type === 'image')))).toBe(true)
    const restored = await persisted(audit.id)
    expect(restored.events).toEqual(audit.snapshotEvents())
    expect(restored.events.map(event => event.type)).toEqual(Array.from({ length: 4 }, () => ['testy/model-request', 'testy/model-response']).flat())
    await disable()
    await expect.poll(() => {
      try { process.kill(samplePid, 0); return true } catch { return false }
    }, { timeout: 10000 }).toBe(false)
  })

  it('cancels discovery without input and closes an app owned by cancelled drafting', { timeout: 90000 }, async () => {
    const { ctx, adapter, sessions, call, disable, persisted } = await setup()
    const sample = await call('launch_sample', { sample: 'testlab' })
    const samplePid = z.number().int().positive().parse(sample.pid)
    const before = await call('list_tests')
    adapter.hold()
    const discovering = await call('ai_test', { instructions: 'Verify the Customer Desk ready state.' })
    const discoveryId = z.string().parse(discovering.taskId)
    await expect.poll(() => adapter.requests.length, { timeout: 30000 }).toBe(1)
    expect(await call('get_task', { taskId: discoveryId })).toMatchObject({ running: true, phase: 'discovering', target: null })
    expect(await call('cancel_task', { taskId: discoveryId })).toMatchObject({ running: false, status: 'cancelled' })
    expect((await ctx.testy.status()).activeRuns).toBe(0)
    expect((await persisted(sessions.at(-1)!.id)).events.at(-1)?.data).toMatchObject({ status: 'cancelled' })
    expect(() => process.kill(samplePid, 0)).not.toThrow()
    expect(await call('list_tests')).toEqual(before)

    const drafting = await call('ai_test', {
      instructions: 'Verify the Customer Desk ready state.', exe: join(runtime, 'Testy.TestLab.exe'),
    })
    const draftingId = z.string().parse(drafting.taskId)
    await expect.poll(() => adapter.requests.length, { timeout: 30000 }).toBe(2)
    const progress = await call('get_task', { taskId: draftingId })
    expect(progress).toMatchObject({ running: true, phase: 'drafting' })
    const ownedPid = z.object({ pid: z.number().int().positive() }).parse(progress.target).pid
    expect(ownedPid).not.toBe(samplePid)
    expect(await call('cancel_task', { taskId: draftingId })).toMatchObject({ running: false, status: 'cancelled' })
    await expect.poll(() => {
      try { process.kill(ownedPid, 0); return true } catch { return false }
    }, { timeout: 10000 }).toBe(false)
    expect((await ctx.testy.status()).activeRuns).toBe(0)
    expect((await persisted(sessions.at(-1)!.id)).events.at(-1)?.data).toMatchObject({ status: 'cancelled' })
    expect(() => process.kill(samplePid, 0)).not.toThrow()
    expect(await call('list_tests')).toEqual(before)
    await disable()
  })
})
