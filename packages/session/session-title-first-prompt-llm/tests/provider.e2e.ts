import { createUserMessage } from '@cortex/llm'
import { afterEach, describe, expect, it } from 'vitest'
import { Context } from '@cortex/cordis'
import LlmRuntime from '@cortex/llm'
import * as LlmPiAi from '@cortex/llm-pi-ai'
import SessionStore, { SessionId } from '@cortex/session'
import SessionTitleService from '@cortex/session-title'
import SessionProjectionRegistry from '@cortex/session-projection'
import * as FirstMessageTitleProvider from '@cortex/session-title-first-prompt-llm'

const baseURL = process.env.CORTEX_TEST_BASE_URL
const model = process.env.CORTEX_TEST_MODEL
const contexts: Context[] = []

afterEach(async () => {
  await Promise.all(contexts.splice(0).map(ctx => ctx.fiber.dispose()))
})

describe.skipIf(!baseURL || !model)('first-prompt title provider with an explicitly configured local model', () => {
  it('replaces the fallback with a short model title', async () => {
    const ctx = new Context()
    contexts.push(ctx)
    await ctx.plugin(LlmRuntime)
    const endpoint = new URL(baseURL!)
    if (!['http:', 'https:'].includes(endpoint.protocol) || !['localhost', '127.0.0.1', '[::1]'].includes(endpoint.hostname)
      || endpoint.username || endpoint.password || endpoint.search || endpoint.hash) {
      throw new Error('CORTEX_TEST_BASE_URL must be a loopback HTTP(S) endpoint without credentials, query, or fragment')
    }
    await ctx.plugin(LlmPiAi, { providers: { 'test-local': {
      api: 'openai-completions', baseURL: endpoint.href,
      ...process.env.CORTEX_TEST_API_KEY ? { apiKeyEnv: 'CORTEX_TEST_API_KEY' } : { headers: { Authorization: 'Bearer cortex-local-test' } },
      models: [{ id: model!, contextWindow: 128_000, maxTokens: 8192 }],
      retryPolicy: { mode: 'normal', maxRetries: 0 },
    } } })
    await ctx.plugin(SessionStore)
    await ctx.plugin(SessionProjectionRegistry)
    await ctx.plugin(SessionTitleService, {
      fallbackMaxWords: 5,
      fallbackMaxBytes: 40,
      maxTitleBytes: 80,
    })
    await ctx.plugin(FirstMessageTitleProvider, {
      targetWords: 5,
      targetCjkCharacters: 10,
      maxInputBytes: 4_096,
      maxOutputTokens: 64,
      timeoutMs: 60_000,
      provider: 'test-local',
      model: model!,
    })
    const session = ctx.sessions.create(SessionId('real-title-provider'))
    session.append('turn/start', {
      turn: 1,
    })
    const message = session.append('user/message', createUserMessage({
      content: [{ type: 'text', text: 'Explain why append-only logs make session titles durable.' }],
      source: { kind: 'user' },
    }), { surfaceOp: 'append' })

    const title = await ctx.sessionTitle.refresh(session)

    expect(title).toMatchObject({
      messageSeqs: [message.seq],
      source: {
        kind: 'provider',
        provider: 'session-title-first-prompt-llm',
        model: { provider: 'test-local', model: model! },
      },
    })
    expect(title?.title.length).toBeGreaterThan(0)
    expect(Buffer.byteLength(title?.title ?? '', 'utf8')).toBeLessThanOrEqual(80)
  })
})
