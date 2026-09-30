/** Private HTTP bridge, pinned routing, durable evidence, and lifetime regressions. */
import { mkdtemp, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { Context } from '@cortex/cordis'
import LlmRuntime, { LlmAdapter, ToolCallId } from '@cortex/llm'
import type { GenerateOptions, LlmResolvedModelInfo, StreamChunk } from '@cortex/llm'
import SessionStore from '@cortex/session'
import AttachmentLocal from '@cortex/attachment-local'
import { expect, it, onTestFinished } from 'vitest'
import { createTestyModelBridge } from '../src/model-bridge.ts'

class RecordingAdapter extends LlmAdapter {
  requests: GenerateOptions[] = []
  output: StreamChunk[] = [{ type: 'text-delta', index: 0, text: 'done' }, { type: 'finish', reason: { kind: 'stop' } }]
  before: (() => void) | undefined
  waitForAbort = false
  started = Promise.withResolvers<undefined>()
  override resolveModel(provider: string, model: string): Promise<LlmResolvedModelInfo> {
    return Promise.resolve({ provider, id: model, name: model, inputModalities: ['text', 'image'] })
  }
  async *stream(options: GenerateOptions): AsyncIterable<StreamChunk> {
    this.requests.push(options)
    this.before?.()
    this.started.resolve(undefined)
    if (this.waitForAbort) {
      await new Promise<void>((resolve) => {
        if (options.signal?.aborted) resolve()
        else options.signal?.addEventListener('abort', () => { resolve() }, { once: true })
      })
      options.signal?.throwIfAborted()
    }
    yield* this.output
  }
}

async function setup() {
  const directory = await mkdtemp(join(tmpdir(), 'cortex-testy-model-'))
  const ctx = new Context()
  await ctx.plugin(SessionStore)
  await ctx.plugin(LlmRuntime)
  await ctx.plugin(AttachmentLocal, { cortexHome: directory })
  const adapter = new RecordingAdapter()
  ctx.effect(() => ctx.llm.registerAdapter(['test'], adapter))
  const bridge = await createTestyModelBridge(ctx, { maxRequestBytes: 1024 * 1024, maxOutputTokens: 1000 })
  const session = ctx.sessions.create()
  const model = { provider: 'test', model: 'pinned-model' }
  const run = bridge.registerRun({ session, model })
  const headers = { 'Content-Type': 'application/json', Authorization: `Bearer ${bridge.token}`, 'X-Testy-Cortex-Context': run.contextId }
  const body = { model: 'cortex', messages: [{ role: 'user', content: 'describe the application' }] }
  const post = (value: unknown = body, extra: Record<string, string> = {}) => fetch(bridge.endpoint, {
    method: 'POST', headers: { ...headers, ...extra }, body: JSON.stringify(value),
  })
  onTestFinished(async () => { await bridge.dispose(); await ctx.fiber.dispose(); await rm(directory, { recursive: true, force: true }) })
  return { ctx, adapter, bridge, session, model, run, body, post }
}

it('pins the selected route and flushes complete inputs before dispatch and output before return', async () => {
  const { ctx, adapter, session, model, body, post } = await setup()
  const flushes: number[] = []
  ctx.on('session/flush', () => { flushes.push(session.snapshotEvents().length) })
  adapter.before = () => { expect(flushes).toEqual([1]) }
  model.model = 'changed-after-registration'
  const response = await post({ ...body, max_tokens: 9000 })
  expect(response.status).toBe(200)
  expect(await response.json()).toMatchObject({ choices: [{ message: { content: 'done' }, finish_reason: 'stop' }] })
  expect(adapter.requests[0]).toMatchObject({ provider: 'test', model: 'pinned-model', maxTokens: 1000, sessionId: session.id })
  expect(flushes).toEqual([1, 2])
  const events = session.snapshotEvents()
  expect(events.map(event => event.type)).toEqual(['testy/model-request', 'testy/model-response'])
  expect(session.deriveMessages()).toEqual([])
})

it('resolves native Studio routes once per request while ordinary run routes remain pinned', async () => {
  const { bridge, adapter, session, post } = await setup()
  let current = { provider: 'test', model: 'studio-first' }
  let resolutions = 0
  const studio = bridge.registerRun({ session, model: current, resolveModel: () => { resolutions += 1; return current } })
  const headers = { 'X-Testy-Cortex-Context': studio.contextId }
  expect((await post(undefined, headers)).status).toBe(200)
  current = { provider: 'test', model: 'studio-next' }
  expect((await post(undefined, headers)).status).toBe(200)
  expect((await post()).status).toBe(200)
  expect(adapter.requests.map(request => request.model)).toEqual(['studio-first', 'studio-next', 'pinned-model'])
  expect(resolutions).toBe(2)
})

it.each([
  { Authorization: 'Bearer wrong' }, { Origin: 'http://127.0.0.1:3080' }, { 'X-Testy-Cortex-Context': 'unregistered' },
])('refuses unauthorized or browser-origin requests: %j', async (headers) => {
  const { adapter, post, session } = await setup()
  expect((await post(undefined, headers)).status).toBeGreaterThanOrEqual(400)
  expect(adapter.requests).toEqual([])
  expect(session.snapshotEvents()).toEqual([])
})

it.each([{ model: 'openrouter/other-model' }, { provider: 'other' }, { endpoint: 'https://example.test' }, { stream: true }])(
  'refuses child routing and unsupported protocol fields: %j', async (extra) => {
    const { adapter, body, post } = await setup()
    expect((await post({ ...body, ...extra })).status).toBe(400)
    expect(adapter.requests).toEqual([])
  },
)

it('admits inline screenshots into durable attachment references and refuses remote image fetches', async () => {
  const { adapter, session, post } = await setup()
  const data = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAACXBIWXMAAAPoAAAD6AG1e1JrAAAADElEQVQImWNgZGIGAAAOAAeCcsnOAAAAAElFTkSuQmCC'
  const imageRequest = (url: string) => ({ model: 'cortex', messages: [{ role: 'user', content: [{ type: 'image_url', image_url: { url } }] }] })
  expect((await post(imageRequest(`data:image/png;base64,${data}`))).status).toBe(200)
  expect(adapter.requests[0]?.messages[0]?.content[0]).toMatchObject({ type: 'image', attachment: { mediaType: 'image/png', width: 1, height: 1 } })
  expect(JSON.stringify(session.snapshotEvents())).not.toContain('data:image')
  expect((await post(imageRequest('https://example.test/screenshot.png'))).status).toBe(400)
})

it('returns a JSON refusal for oversized bodies without dispatching a model request', async () => {
  const { post, adapter, session } = await setup()
  const response = await post({ model: 'cortex', messages: [{ role: 'user', content: '界'.repeat(400_000) }] })
  expect(response.status).toBe(413)
  expect(await response.json()).toMatchObject({ error: { code: 'BODY_TOO_LARGE' } })
  expect(adapter.requests).toEqual([])
  expect(session.snapshotEvents()).toEqual([])
})

const tool = { type: 'function', function: { name: 'click', description: 'Click one observed control', strict: true, parameters: {
  type: 'object', properties: { selector: { type: 'string' } }, required: ['selector'], additionalProperties: false,
} } }

it('round-trips function calls and correlated tool results', async () => {
  const { adapter, body, post } = await setup()
  adapter.output = [
    { type: 'block-end', index: 0, block: { type: 'tool-call', id: ToolCallId('call-1'), name: 'click', arguments: '{"selector":"id:save"}' } },
    { type: 'finish', reason: { kind: 'tool-calls' } },
  ]
  const response = await post({ ...body, tools: [tool], parallel_tool_calls: false })
  expect(response.status).toBe(200)
  const result = await response.json() as { choices: Array<{ message: Record<string, unknown> }> }
  expect(result.choices[0]?.message).toMatchObject({ role: 'assistant', tool_calls: [{ id: 'call-1', function: { name: 'click' } }] })
  adapter.output = [{ type: 'text-delta', index: 0, text: 'saved' }, { type: 'finish', reason: { kind: 'stop' } }]
  const next = await post({ ...body, tools: [tool], messages: [...body.messages, result.choices[0]?.message, { role: 'tool', tool_call_id: 'call-1', content: 'saved successfully' }] })
  expect(next.status).toBe(200)
  expect(adapter.requests[1]?.messages.at(-1)).toMatchObject({ role: 'tool', toolCallId: 'call-1', source: { kind: 'tool', callId: 'call-1' } })
})

it('enforces planner schemas including numeric bounds and logs the format instructions', async () => {
  const { adapter, body, post, session } = await setup()
  const request = { ...body, response_format: { type: 'json_schema', json_schema: { name: 'plan', strict: true, schema: {
    type: 'object', properties: { count: { type: 'integer', minimum: 1, maximum: 2 } }, required: ['count'], additionalProperties: false,
  } } } }
  adapter.output = [{ type: 'text-delta', index: 0, text: '{"count":2}' }, { type: 'finish', reason: { kind: 'stop' } }]
  expect((await post(request)).status).toBe(200)
  expect(JSON.stringify(session.snapshotEvents()[0]?.data)).toContain('Return only a JSON value')
  adapter.output = [{ type: 'text-delta', index: 0, text: '{"count":3}' }, { type: 'finish', reason: { kind: 'stop' } }]
  const rejected = await post(request)
  expect(rejected.status).toBe(502)
  expect(await rejected.json()).toMatchObject({ error: { code: 'INVALID_OUTPUT' } })
  expect(session.snapshotEvents().at(-1)?.data).toMatchObject({ status: 'failed', error: { code: 'INVALID_OUTPUT' } })
})

it.each([
  { output: [{ type: 'text-delta', index: 0, text: 'incomplete' }, { type: 'finish', reason: { kind: 'max-tokens' } }], code: 'MODEL_TRUNCATED' },
  { output: [{ type: 'finish', reason: { kind: 'error', failure: { code: 'HTTP', message: 'private adapter diagnostic' } } }], code: 'MODEL_FAILED' },
  { output: [{ type: 'block-end', index: 0, block: { type: 'tool-call', id: ToolCallId('call-1'), name: 'undeclared', arguments: '{}' } }], code: 'UNKNOWN_TOOL' },
] satisfies Array<{ output: StreamChunk[]; code: string }>)('audits unusable model output and returns a structured refusal: $code', async ({ output, code }) => {
  const { adapter, post, session } = await setup()
  adapter.output = output
  const response = await post()
  expect(response.status).toBe(502)
  const result: unknown = await response.json()
  expect(result).toMatchObject({ error: { code } })
  expect(JSON.stringify(result)).not.toContain('private adapter diagnostic')
  expect(session.snapshotEvents().at(-1)?.data).toMatchObject({ status: 'failed', chunks: output, error: { code } })
})

it('revokes contexts, cancels provider work, and waits for the cancelled settlement', async () => {
  const { adapter, post, run, session } = await setup()
  adapter.waitForAbort = true
  const response = post()
  await adapter.started.promise
  expect((await post()).status).toBe(409)
  await run.dispose()
  expect((await response).status).toBe(499)
  expect(session.snapshotEvents().at(-1)?.data).toMatchObject({ status: 'cancelled' })
  expect((await post()).status).toBe(403)
})

it('closes the listener and settles active calls before plugin disposal returns', async () => {
  const { adapter, post, bridge, session } = await setup()
  adapter.waitForAbort = true
  const pending = post().catch(() => undefined)
  await adapter.started.promise
  await bridge.dispose()
  await pending
  expect(session.snapshotEvents().at(-1)?.data).toMatchObject({ status: 'cancelled' })
  expect(() => bridge.registerRun({ session, model: { provider: 'test', model: 'm' } })).toThrow()
})
