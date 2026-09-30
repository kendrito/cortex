/** Authenticated loopback model dispatch for the Cortex-owned Testy child. @module */
import { randomBytes, randomUUID, timingSafeEqual } from 'node:crypto'
import { createServer } from 'node:http'
import type { IncomingMessage, ServerResponse } from 'node:http'
import type { Context } from '@cortex/cordis'
import type { ModelSelection } from '@cortex/agent'
import { brandString } from '@cortex/brand'
import type { Branded } from '@cortex/brand'
import { BlockAssembler } from '@cortex/llm'
import type { GenerateOptions, StreamChunk } from '@cortex/llm'
import type { Session } from '@cortex/session'
import { deepFreeze } from '@cortex/util-values'
import { ModelBridgeError, TestyCompletionRequest, testyCompletionResponse, translateTestyRequest } from './model-wire.ts'
import type { TestyModelRequestId, TestyModelResponseData } from './model-events.ts'

/** Bearer capability selecting one registered Testy operation. */
export type TestyModelContextId = Branded<'TestyModelContextId'>

/** Deployment-owned limits for the private model bridge. */
export interface TestyModelBridgeConfig {
  maxRequestBytes: number
  maxOutputTokens: number
}

/** Host-owned model and audit session pinned before the child starts an operation. */
export interface TestyModelRun {
  session: Session
  model: ModelSelection
  /** Native Studio reads the host route once at each request; ordinary runs stay pinned. */
  resolveModel?: () => ModelSelection
  signal?: AbortSignal
}

/** Revocable per-operation capability; it contains no provider credentials. */
export interface TestyModelRunHandle {
  contextId: TestyModelContextId
  /** Revoke access, abort current calls, and wait for their audit settlements. */
  dispose(): Promise<void>
}

/** One private listener and the capabilities issued to its child. */
export interface TestyModelBridge {
  endpoint: string
  token: string
  /**
   * Pin one trusted operation before sending its context id to the child.
   * @param run - session, selected route, and optional owner cancellation.
   * @returns revocable capability retained until the Testy operation settles.
   */
  registerRun(run: TestyModelRun): TestyModelRunHandle
  /** Close admission, revoke every operation, and await all calls and sockets. */
  dispose(): Promise<void>
}

interface RegisteredRun {
  session: Session
  model: ModelSelection
  resolveModel?: () => ModelSelection
  controller: AbortController
  signal: AbortSignal
  pending: Set<Promise<void>>
}

/**
 * Start a loopback-only bridge; callers register its disposer as a Cordis effect.
 * No model or endpoint value from a child can replace the pinned host route.
 * @param ctx - host LLM, attachment, and Session services.
 * @param config - bounded body and output-token policy.
 * @returns listener address, child token, and operation registration.
 */
export async function createTestyModelBridge(ctx: Context, config: TestyModelBridgeConfig): Promise<TestyModelBridge> {
  const lifetime = new AbortController()
  const runs = new Map<string, RegisteredRun>()
  const pending = new Set<Promise<void>>()
  const token = randomBytes(32).toString('base64url')
  let host = ''
  let disposal: Promise<void> | undefined
  const server = createServer((request, response) => {
    if (lifetime.signal.aborted) { sendError(response, new ModelBridgeError(503, 'BRIDGE_CLOSED', 'The Testy integration is disabled.')); return }
    if (request.method !== 'POST' || request.url !== '/v1/chat/completions') {
      sendError(response, new ModelBridgeError(404, 'NOT_FOUND', 'Unknown model bridge endpoint.')); return
    }
    if (request.headers.host !== host || request.headers.origin !== undefined
      || request.socket.remoteAddress !== '127.0.0.1' || !authorized(request.headers.authorization, token)) {
      sendError(response, new ModelBridgeError(401, 'UNAUTHORIZED', 'Model bridge authorization failed.')); return
    }
    const contextId = request.headers['x-testy-cortex-context']
    const run = typeof contextId === 'string' ? runs.get(contextId) : undefined
    if (run === undefined || run.signal.aborted) {
      sendError(response, new ModelBridgeError(403, 'UNKNOWN_CONTEXT', 'The Testy operation is missing, expired, or cancelled.')); return
    }
    if (run.pending.size > 0) { sendError(response, new ModelBridgeError(409, 'REQUEST_ACTIVE', 'This Testy operation already has an active model request.')); return }
    if (!request.headers['content-type']?.toLowerCase().startsWith('application/json')) {
      sendError(response, new ModelBridgeError(415, 'CONTENT_TYPE', 'Send a JSON request body.')); return
    }
    const operation = serveCompletion(ctx, config, run, request, response).catch((error: unknown) => {
      sendError(response, publicError(error, run.signal))
    })
    run.pending.add(operation)
    pending.add(operation)
    void operation.finally(() => { run.pending.delete(operation); pending.delete(operation) })
  })
  try {
    await new Promise<void>((resolve, reject) => {
      server.once('error', reject)
      server.listen(0, '127.0.0.1', () => { server.off('error', reject); resolve() })
    })
    const address = server.address()
    if (address === null || typeof address === 'string') throw new Error('Testy model bridge failed to bind loopback.')
    host = `127.0.0.1:${address.port}`
  } catch (error) {
    lifetime.abort()
    server.closeAllConnections()
    server.close()
    throw error
  }
  return {
    endpoint: `http://${host}/v1/chat/completions`, token,
    registerRun(run) {
      lifetime.signal.throwIfAborted()
      run.signal?.throwIfAborted()
      if (run.model.provider.trim() === '' || run.model.model.trim() === '') throw new Error('Select a Cortex model before using Testy AI.')
      const contextId = brandString<TestyModelContextId>(randomUUID())
      const controller = new AbortController()
      const entry: RegisteredRun = {
        session: run.session, model: deepFreeze(structuredClone(run.model)), controller,
        ...run.resolveModel === undefined ? {} : { resolveModel: run.resolveModel },
        signal: AbortSignal.any([controller.signal, lifetime.signal, ...run.signal === undefined ? [] : [run.signal]]),
        pending: new Set(),
      }
      runs.set(contextId, entry)
      return { contextId, async dispose() {
        runs.delete(contextId)
        controller.abort()
        await Promise.allSettled([...entry.pending])
      } }
    },
    dispose() {
      disposal ??= (async () => {
        lifetime.abort()
        runs.clear()
        const closed = new Promise<void>((resolve, reject) => {
          server.close((error) => { if (error === undefined) resolve(); else reject(error) })
        })
        server.closeAllConnections()
        await Promise.allSettled([...pending])
        await closed
      })()
      return disposal
    },
  }
}

/** Compare the private child credential without exposing it in a diagnostic. */
function authorized(header: string | undefined, token: string): boolean {
  const expected = Buffer.from(`Bearer ${token}`)
  const candidate = Buffer.from(header ?? '')
  return candidate.length === expected.length && timingSafeEqual(candidate, expected)
}

/** Read bounded UTF-8 JSON without accepting another routing field. */
async function readRequest(request: IncomingMessage, maxBytes: number): Promise<TestyCompletionRequest> {
  let size = 0
  const chunks: Buffer[] = []
  try {
    for await (const chunk of request.iterator({ destroyOnReturn: false })) {
      if (!Buffer.isBuffer(chunk)) throw new ModelBridgeError(400, 'INVALID_BODY', 'Invalid request encoding.')
      size += chunk.length
      if (size > maxBytes) throw new ModelBridgeError(413, 'BODY_TOO_LARGE', 'The Testy model request exceeds its configured byte limit.')
      chunks.push(chunk)
    }
  } catch (error) {
    // Drain rejected input without destroying the socket carrying the JSON error.
    request.resume()
    throw error
  }
  let value: unknown
  try { value = JSON.parse(Buffer.concat(chunks).toString('utf8')) } catch { throw new ModelBridgeError(400, 'INVALID_JSON', 'The Testy model request is not valid JSON.') }
  const parsed = TestyCompletionRequest.safeParse(value)
  if (!parsed.success) throw new ModelBridgeError(400, 'INVALID_REQUEST', 'The Testy model request contains unsupported or invalid fields.')
  return parsed.data
}

/** One HTTP request owns cancellation, exact audit records, and provider settlement. */
async function serveCompletion(
  ctx: Context, config: TestyModelBridgeConfig, run: RegisteredRun, request: IncomingMessage, response: ServerResponse,
): Promise<void> {
  const disconnected = new AbortController()
  const signal = AbortSignal.any([run.signal, disconnected.signal])
  const close = (): void => { if (!response.writableFinished) disconnected.abort() }
  const abort = (): void => { if (!request.complete) request.destroy() }
  response.once('close', close)
  signal.addEventListener('abort', abort, { once: true })
  const chunks: StreamChunk[] = []
  let requestId: TestyModelRequestId | undefined
  let settled = false
  try {
    signal.throwIfAborted()
    const model = run.resolveModel === undefined ? run.model : deepFreeze(structuredClone(run.resolveModel()))
    const wire = await readRequest(request, config.maxRequestBytes)
    const { messages, tools } = await translateTestyRequest(ctx, wire, model, signal)
    const requestedTokens = wire.max_completion_tokens ?? wire.max_tokens ?? config.maxOutputTokens
    const prepared = await ctx.llm.prepareCall({
      ...model, maxTokens: Math.min(requestedTokens, config.maxOutputTokens),
      ...wire.temperature === undefined ? {} : { temperature: wire.temperature },
    }, signal)
    signal.throwIfAborted()
    requestId = brandString<TestyModelRequestId>(randomUUID())
    const options: GenerateOptions = deepFreeze({ ...prepared.config, messages, tools, sessionId: run.session.id, signal })
    run.session.append('testy/model-request', { requestId, config: prepared.config, messages, tools })
    await ctx.sessions.flush(run.session)
    signal.throwIfAborted()
    const assembler = new BlockAssembler()
    for await (const chunk of prepared.stream(options)) {
      chunks.push(structuredClone(chunk))
      assembler.push(chunk)
      signal.throwIfAborted()
    }
    signal.throwIfAborted()
    const result = testyCompletionResponse(assembler, wire, requestId)
    run.session.append('testy/model-response', { requestId, status: 'completed', chunks, response: result })
    settled = true
    await ctx.sessions.flush(run.session)
    sendJson(response, 200, result)
  } catch (error) {
    const failure = publicError(error, signal)
    if (requestId !== undefined && !settled) {
      const record: TestyModelResponseData = {
        requestId, status: signal.aborted ? 'cancelled' : 'failed', chunks,
        error: { code: failure.code, message: failure.message },
      }
      run.session.append('testy/model-response', record)
      await ctx.sessions.flush(run.session)
    }
    throw failure
  } finally {
    response.off('close', close)
    signal.removeEventListener('abort', abort)
  }
}

/** Do not return adapter diagnostics that might contain credential or endpoint material. */
function publicError(error: unknown, signal: AbortSignal): ModelBridgeError {
  if (signal.aborted) return new ModelBridgeError(499, 'CANCELLED', 'The Testy model request was cancelled.')
  return error instanceof ModelBridgeError ? error : new ModelBridgeError(502, 'MODEL_FAILED', 'The selected Cortex model request failed.')
}

function sendError(response: ServerResponse, error: ModelBridgeError): void {
  sendJson(response, error.status, { error: { message: error.message, type: 'testy_model_bridge_error', code: error.code } })
}

function sendJson(response: ServerResponse, status: number, value: Record<string, unknown>): void {
  if (response.destroyed || response.writableEnded) return
  response.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' })
  response.end(JSON.stringify(value))
}
