import { afterEach, describe, expect, it } from 'vitest'
import { Context } from '@cortex/cordis'
import { SessionId } from '@cortex/session'

import AgentLoop from '@cortex/agent-loop'
import { mountAgentLoopTestDependencies } from '@cortex/agent-loop-testkit'
import * as LlmPiAi from '@cortex/llm-pi-ai'
import SubagentRuntime from '@cortex/subagent'
import * as Spawn from '@cortex/subagent-spawn-in-process'
import PtcWorkflowEngine from '../src/index.ts'
import { mountPtcRuntime } from './setup.ts'

const baseURL = process.env.CORTEX_TEST_BASE_URL
const model = process.env.CORTEX_TEST_MODEL
const hasConfiguredTestModel = Boolean(baseURL && model)

let ctx: Context | undefined

afterEach(async () => {
  await ctx?.fiber.dispose()
  ctx = undefined
})

async function harness(): Promise<Context> {
  const built = new Context()
  await mountAgentLoopTestDependencies(built)
  await mountPtcRuntime(built)
  await built.plugin(AgentLoop, { agents: [] })
  if (!baseURL || !model) throw new Error('Real-model tests require CORTEX_TEST_BASE_URL and CORTEX_TEST_MODEL')
  const endpoint = new URL(baseURL)
  if (!['http:', 'https:'].includes(endpoint.protocol) || !['localhost', '127.0.0.1', '[::1]'].includes(endpoint.hostname)
    || endpoint.username || endpoint.password || endpoint.search || endpoint.hash) {
    throw new Error('CORTEX_TEST_BASE_URL must be a loopback HTTP(S) endpoint without credentials, query, or fragment')
  }
  await built.plugin(LlmPiAi, { providers: { 'test-local': {
    api: 'openai-completions', baseURL: endpoint.href, ...process.env.CORTEX_TEST_API_KEY ? { apiKeyEnv: 'CORTEX_TEST_API_KEY' } : { headers: { Authorization: 'Bearer cortex-local-test' } },
    models: [{ id: model, contextWindow: 128_000, maxTokens: 8192 }], retryPolicy: { mode: 'normal', maxRetries: 0 },
  } } })
  await built.plugin(SubagentRuntime)
  await built.plugin(Spawn, { providerName: 'spawn' })
  await built.plugin(PtcWorkflowEngine, { provider: 'spawn' })
  return built
}

const META = {
  name: 'e2e-ptc-arithmetic',
  description: 'two real children through PTC: one prose, one structured',
  phases: [{ title: 'Ask' }, { title: 'Judge' }],
}
const SCRIPT = `phase('Ask')
log('asking the prose child')
const prose = await agent('Reply with exactly one short sentence: what is 2 + 2?')
phase('Judge')
const judged = await agent(
  'Here is an answer to the question "what is 2+2": ' + prose
  + ' — report whether it contains the number 4 and your confidence between 0 and 1.',
  { schema: { type: 'object', properties: { containsFour: { type: 'boolean' }, confidence: { type: 'number' } }, required: ['containsFour'] } },
)
return { prose, containsFour: judged === null ? null : judged.containsFour }`

describe.skipIf(!hasConfiguredTestModel)('PTC workflow engine with-key e2e', () => {
  it('runs a two-phase script in the Node PTC runtime over real children, one through the structured runtime', async () => {
    ctx = await harness()
    const parentHandle = await ctx.agents.create({
      sessionId: 'wf-ptc-e2e-session' as never,
      agentOptions: { provider: 'test-local', model: model! },
    })

    const events: string[] = []
    const childIds: string[] = []
    for (const name of ['workflow/start', 'workflow/phase', 'workflow/log', 'workflow/agent-start', 'workflow/agent-end', 'workflow/end'] as const) {
      ctx.on(name, (...payload: unknown[]) => {
        events.push(name)
        if (name === 'workflow/agent-start') childIds.push((payload[1] as { childId: string }).childId)
      })
    }

    const run = ctx.workflowEngine.start({ script: SCRIPT, meta: META, parent: parentHandle.agent })
    const result = await run.result
    await run.dispose()

    expect(result.stopReason).toBe('completed')
    expect(result.agentsStarted).toBe(2)
    const value = result.value as { prose: string; containsFour: boolean | null }
    // World checks: the prose child really answered (a real completion), and
    // the structured child judged it against the REAL schema-forced tool.
    expect(value.prose.length).toBeGreaterThan(0)
    expect(value.containsFour).toBe(true)

    expect(events[0]).toBe('workflow/start')
    expect(events.at(-1)).toBe('workflow/end')
    expect(events.filter(name => name === 'workflow/phase').length).toBe(2)
    expect(events.filter(name => name === 'workflow/agent-start').length).toBe(2)
    expect(childIds.length).toBe(2)
    // The children were disposed to quiescence after collection.
    for (const childId of childIds) {
      expect(ctx.agents.get(SessionId(childId))).toBeUndefined()
    }
    await parentHandle.dispose()
  }, 240_000)
})
