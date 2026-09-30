import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { expect, it } from 'vitest'
import { Context } from '@cortex/cordis'
import AgentLoop from '@cortex/agent-loop'
import { mountAgentLoopTestDependencies } from '@cortex/agent-loop-testkit'
import { createUserMessage } from '@cortex/llm'
import { PiAiAdapter } from '@cortex/llm-pi-ai'
import { resolveProfiles } from '../../../llm/llm-pi-ai/src/config.ts'
import { memoryAuth } from '../../../llm/llm-pi-ai/tests/auth-double.ts'
import { SessionId } from '@cortex/session'
import JsonlSessionPersistence from '@cortex/session-persistence-jsonl'
import * as SubagentSpawn from '@cortex/subagent-spawn-in-process'
import { closeMockServers, mockServer } from '../../../llm/llm-pi-ai/tests/mock-server.ts'
import SubagentRuntime, { type SubagentRunEndInfo } from '../src/index.ts'
import { loadStoredSession } from './persistence-helpers.ts'

const MODEL = 'continuation-fixture'

it('continues the parent through a local pi-ai route after a reasoning-bearing continuable child settles', async () => {
  const root = mkdtempSync(join(tmpdir(), 'cortex-settlement-messages-'))
  const ctx = new Context()
  try {
    const responseEvents = (text: string, reasoning?: string) => [
      JSON.stringify({ choices: [{ index: 0, delta: { role: 'assistant', ...(reasoning ? { reasoning_content: reasoning } : {}) } }] }),
      JSON.stringify({ choices: [{ index: 0, delta: { content: text } }] }),
      JSON.stringify({ choices: [{ index: 0, delta: {}, finish_reason: 'stop' }] }),
      '[DONE]',
    ]
    const http = await mockServer([
      { events: responseEvents('child answer', 'child reasoning') },
      { events: responseEvents('parent answer') },
      { events: responseEvents('parent answer') },
    ])
    const { requests } = http
    const profiles = resolveProfiles({
      local: {
        api: 'openai-completions', baseURL: http.url,
        models: [{ id: MODEL, contextWindow: 32768, maxTokens: 4096 }],
      },
    })
    const adapter = new PiAiAdapter({
      profiles: () => profiles,
      resolveApiKey: () => Promise.resolve('test-key'),
      auth: memoryAuth(),
    })
    await mountAgentLoopTestDependencies(ctx)
    await ctx.plugin(JsonlSessionPersistence, { root })
    await ctx.plugin(AgentLoop, { agents: [] })
    await ctx.plugin(SubagentRuntime)
    await ctx.plugin(SubagentSpawn, { providerName: 'spawn' })
    ctx.llm.registerAdapter(['local'], adapter)
    const parent = await ctx.agentLoop.create(SessionId('parent'), { provider: 'local', model: MODEL })
    const ends: SubagentRunEndInfo[] = []
    const settled = Promise.withResolvers<undefined>()
    ctx.on('subagent/end', (info) => {
      ends.push(info)
      settled.resolve(undefined)
    })

    const started = await ctx.subagents.startContinuable({
      provider: 'spawn',
      label: 'child task',
      request: { parent, prompt: [{ type: 'text', text: 'child task' }] },
      signal: new AbortController().signal,
    })
    await settled.promise
    await parent.whenIdle()

    const output = [{ type: 'reasoning', text: 'child reasoning' }, { type: 'text', text: 'child answer' }]
    expect(ends).toHaveLength(1)
    expect(ends[0]?.lastAssistantMessage).toEqual(output)
    const child = await loadStoredSession(ctx.sessionPersistence, started.childId)
    expect(child.events.filter(event => event.type === 'assistant/message').at(-1))
      .toMatchObject({ data: { message: { content: output } } })
    expect(parent.session.snapshotEvents().at(-1))
      .toMatchObject({ type: 'turn/end', data: { reason: { kind: 'completed' } } })
    expect(requests).toHaveLength(2)
    expect(http.paths).toEqual(['/chat/completions', '/chat/completions'])
    const notice = parent.session.deriveMessages().find(message => message.source.kind === 'subagent-settled')
    expect(notice?.content).toEqual([
      { type: 'text', text: `Background subagent ${started.childId} finished and will do no further work unless you send it more.` },
      { type: 'text', text: 'Its closing message:' },
      { type: 'text', text: 'child answer' },
    ])
    const noticeText = notice?.content.filter(block => block.type === 'text').map(block => block.text).join('')
    expect((requests[1] as { messages: unknown[] }).messages).toContainEqual({ role: 'user', content: noticeText })

    parent.followup(createUserMessage({ source: { kind: 'user' }, content: [{ type: 'text', text: 'continue' }] }))
    await parent.whenIdle()
    expect(parent.session.snapshotEvents().filter(event => event.type === 'turn/end'))
      .toMatchObject([{ data: { reason: { kind: 'completed' } } }, { data: { reason: { kind: 'completed' } } }])
    expect(requests).toHaveLength(3)
    expect((requests[2] as { messages: unknown[] }).messages).toEqual(expect.arrayContaining([
      { role: 'user', content: noticeText },
      expect.objectContaining({ role: 'assistant', content: 'parent answer' }),
      { role: 'user', content: 'continue' },
    ]))
  } finally {
    try {
      await ctx.fiber.dispose()
    } finally {
      try {
        await closeMockServers()
      } finally {
        rmSync(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 })
      }
    }
  }
})
