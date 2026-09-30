import { Context } from '@cortex/cordis'
import type { SessionEvent } from '@cortex/session'
import type { Agent } from '@cortex/agent'
import AgentLoop from '@cortex/agent-loop'
import { mountAgentLoopTestDependencies } from '@cortex/agent-loop-testkit'
import { LocalBashExecutor } from '@cortex/bash-local'
import * as BashEnvPlugin from '@cortex/shell-env'
import LocalSubprocessRuntime from '@cortex/subprocess-local'
import * as ToolBash from '@cortex/tool-bash'
import * as ToolTodo from '@cortex/tool-todo'
import * as LlmPiAi from '@cortex/llm-pi-ai'
import TokenMeter from '@cortex/token-meter'
import ToolResultPruner from '@cortex/compaction-tool-result-pruner'
import JsonlSessionPersistence from '@cortex/session-persistence-jsonl'
import * as SessionCheckpointPolicy from '@cortex/session-checkpoint-policy'
import { BasicCompactionEngine } from '@cortex/compaction-basic'
import type { BasicCompactionConfig } from '@cortex/compaction-basic'

/**
 * Shared harness for the headless-agent e2e suites: the full plugin stack
 * with an explicitly configured local model adapter and the real bash + todo_write tools. Lives
 * outside the *.e2e.ts pattern so importing it never re-registers another
 * file's tests.
 */

/** Real-model runs require both variables; ambient cloud API keys never opt in. */
export const hasConfiguredTestModel = Boolean(process.env.CORTEX_TEST_BASE_URL && process.env.CORTEX_TEST_MODEL)
export const testModelOptions = { provider: 'test-local', model: process.env.CORTEX_TEST_MODEL ?? 'unconfigured-test-model' }

/** Mount an explicit loopback OpenAI-compatible endpoint without catalog or cloud defaults. */
export async function mountTestModel(ctx: Context, options: Pick<CodingHarnessOptions, 'modelContextWindow' | 'modelMaxTokens'> = {}): Promise<void> {
  if (!hasConfiguredTestModel) throw new Error('Real-model tests require CORTEX_TEST_BASE_URL and CORTEX_TEST_MODEL')
  const endpoint = new URL(process.env.CORTEX_TEST_BASE_URL!)
  if (!['http:', 'https:'].includes(endpoint.protocol) || !['localhost', '127.0.0.1', '[::1]'].includes(endpoint.hostname)
    || endpoint.username || endpoint.password || endpoint.search || endpoint.hash) {
    throw new Error('CORTEX_TEST_BASE_URL must be a loopback HTTP(S) endpoint without credentials, query, or fragment')
  }
  await ctx.plugin(LlmPiAi, { providers: { [testModelOptions.provider]: {
    api: 'openai-completions', baseURL: endpoint.href,
    ...process.env.CORTEX_TEST_API_KEY ? { apiKeyEnv: 'CORTEX_TEST_API_KEY' } : { headers: { Authorization: 'Bearer cortex-local-test' } },
    retryPolicy: { mode: 'normal', maxRetries: 0 },
    models: [{
      id: testModelOptions.model, contextWindow: options.modelContextWindow ?? 128_000,
      maxTokens: options.modelMaxTokens ?? 8192,
    }],
  } } })
}

export const SYSTEM_PROMPT = 'You are a coding agent. Use bash for file operations '
  + 'with cat/grep/heredocs; check [exit code: N] markers, '
  + 'and report results briefly.'

/** System prompt for the todo_write e2e: nudges the model to plan with the tool. */
export const TODO_SYSTEM_PROMPT = 'You are a coding agent. For multi-step work, '
  + 'use the todo_write tool to track a task list: send the WHOLE list each call, '
  + 'mark every task being actively worked on in_progress (several at once when '
  + 'work runs in parallel, at least one while work remains), and mark a task '
  + 'completed as soon as it is done.'

/** Options for {@link codingHarness}. */
export interface CodingHarnessOptions {
  /**
   * Deployment persona prefix for the tree (the system-prompt plugin's `personaPrefix`
   * config — per-context, not per-agent). Omitted ⇒ no persona prefix section.
   */
  personaPrefix?: string
  /** Durable JSONL persistence root (the resume suite needs it; others stay file-free). */
  persistenceRoot?: string
  /**
   * Load {@link BasicCompactionEngine} with this config so the compaction e2e can
   * trigger compaction at a small, controlled history size. Omitted ⇒ no
   * compaction plugin (the default suites run without it).
   */
  compact?: BasicCompactionConfig
  /**
   * Test-only context capacity advertised for the configured model. Automatic
   * pressure scales the message budget left after the request's reserved
   * output tokens, so this capacity must exceed the advertised output cap.
   */
  modelContextWindow?: number
  /**
   * Test-only per-request output cap advertised for the configured model, which
   * the adapter forwards as `max_tokens` and pressure excludes from the
   * capacity above.
   */
  modelMaxTokens?: number
}

export async function codingHarness(workdir: string, options: CodingHarnessOptions = {}): Promise<Context> {
  const ctx = new Context()
  await mountAgentLoopTestDependencies(ctx, {
    systemPrompt: { personaPrefix: options.personaPrefix ?? '' },
  })
  await ctx.plugin(AgentLoop, { agents: [] })
  await mountTestModel(ctx, options)
  await ctx.plugin(LocalSubprocessRuntime)
  await ctx.plugin(BashEnvPlugin)
  await ctx.plugin(LocalBashExecutor, { cwd: workdir, timeoutMs: 30_000 })
  await ctx.plugin(ToolBash)
  await ctx.plugin(ToolTodo, { allowParallelInProgress: true })
  // Compaction is opt-in: only the compaction e2e loads the reusable meter and backend.
  if (options.compact !== undefined) {
    await ctx.plugin(TokenMeter)
    await ctx.plugin(ToolResultPruner)
    await ctx.plugin(BasicCompactionEngine, options.compact)
  }
  // Durable JSONL persistence is opt-in: only the resume e2e needs it, and the
  // other suites stay file-free. Loaded last so a resume's deferred
  // `ctx.inject(['sessionPersistence'])` resolves once this is present.
  if (options.persistenceRoot !== undefined) {
    await ctx.plugin(JsonlSessionPersistence, { root: options.persistenceRoot })
    await ctx.plugin(SessionCheckpointPolicy)
  }
  return ctx
}

export function waitForIdle(ctx: Context, agent: Agent): Promise<void> {
  return new Promise((resolve) => {
    const dispose = ctx.on('agent/status', ({ agent: subject, status }) => {
      if (subject === agent && status === 'idle') {
        dispose()
        resolve()
      }
    })
  })
}

export function finalText(events: readonly SessionEvent[]): string {
  const message = events.findLast(event => event.type === 'assistant/message')
  if (message?.type !== 'assistant/message') return ''
  return message.data.message.content
    .filter(block => block.type === 'text')
    .map(block => block.text)
    .join('')
}
