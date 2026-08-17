/**
 * Registry tests for `@cortex/shell-env`: built-in facts, contributor
 * ownership and validation, collection ordering, effect-scoped disposal, and
 * the explicit disposer contract.
 */

import { homedir } from 'node:os'
import { join, resolve } from 'node:path'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Context } from '@cortex/cordis'
import { CallId } from '@cortex/llm'
import type { Agent } from '@cortex/agent'
import type { ToolExecution } from '@cortex/tools'
import { ShellEnvRegistry } from '@cortex/shell-env'
import * as BashEnvPlugin from '@cortex/shell-env'

const testToolSignal = new AbortController().signal

afterEach(() => vi.unstubAllEnvs())

function execution(sessionId?: string): ToolExecution {
  return {
    signal: testToolSignal,
    token: Symbol('bash-env-test') as ToolExecution['token'],
    callId: CallId('bash-env-call'),
    rootCallId: CallId('bash-env-call'),
    name: 'bash',
    arguments: { command: 'true' },
    ...(sessionId === undefined
      ? {}
      : { agent: { session: { header: { version: 0, id: sessionId, createdAt: 0 } } } as Agent }),
  }
}

describe('ShellEnvRegistry', () => {
  it('collects unconditional shell facts and the current agent session id', () => {
    const ctx = new Context()
    const registry = new ShellEnvRegistry(ctx, { cortexHome: './test-cortex-home' })

    expect(registry.collect(execution())).toEqual({
      CORTEX_HOME: resolve('./test-cortex-home'),
      CORTEX_SHELL: '1',
    })
    expect(registry.collect(execution('session-a'))).toEqual({
      CORTEX_HOME: resolve('./test-cortex-home'),
      CORTEX_SESSION_ID: 'session-a',
      CORTEX_SHELL: '1',
    })
  })

  it('resolves CORTEX_HOME from the ambient override or the user-home default', () => {
    vi.stubEnv('CORTEX_HOME', './ambient-cortex-home')
    const fromEnvironment = new ShellEnvRegistry(new Context())
    expect(fromEnvironment.collect(execution()).CORTEX_HOME).toBe(resolve('./ambient-cortex-home'))

    vi.stubEnv('CORTEX_HOME', undefined)
    const fromDefault = new ShellEnvRegistry(new Context())
    expect(fromDefault.collect(execution()).CORTEX_HOME).toBe(join(homedir(), '.cortex'))
  })

  it('collects declared contributor variables and omits unavailable values', () => {
    const ctx = new Context()
    const registry = new ShellEnvRegistry(ctx, { cortexHome: './test-cortex-home' })
    registry.register({
      name: 'optional-session-fact',
      variables: {
        CORTEX_SESSION_OPTIONAL: { description: 'Optional session-scoped test fact.' },
      },
      resolve: exec => exec.agent === undefined ? {} : { CORTEX_SESSION_OPTIONAL: exec.agent.session.header.id },
    })
    registry.register({
      name: 'always-available-fact',
      variables: {
        CORTEX_ALWAYS_AVAILABLE: { description: 'Always-available test fact.' },
      },
      resolve: () => ({ CORTEX_ALWAYS_AVAILABLE: 'yes' }),
    })

    expect(registry.collect(execution())).not.toHaveProperty('CORTEX_SESSION_OPTIONAL')
    expect(registry.collect(execution()).CORTEX_ALWAYS_AVAILABLE).toBe('yes')
    expect(registry.collect(execution('session-b')).CORTEX_SESSION_OPTIONAL).toBe('session-b')
    expect(registry.list()).toEqual([
      {
        contributor: 'always-available-fact',
        description: 'Always-available test fact.',
        key: 'CORTEX_ALWAYS_AVAILABLE',
      },
      {
        contributor: 'optional-session-fact',
        description: 'Optional session-scoped test fact.',
        key: 'CORTEX_SESSION_OPTIONAL',
      },
    ])
  })

  it('rejects duplicate variable ownership at registration time', () => {
    const ctx = new Context()
    const registry = new ShellEnvRegistry(ctx, { cortexHome: './test-cortex-home' })
    registry.register({
      name: 'first',
      variables: { CORTEX_SHARED: { description: 'First owner.' } },
      resolve: () => ({ CORTEX_SHARED: 'first' }),
    })

    expect(() => registry.register({
      name: 'second',
      variables: { CORTEX_SHARED: { description: 'Second owner.' } },
      resolve: () => ({ CORTEX_SHARED: 'second' }),
    })).toThrow(/CORTEX_SHARED.*first.*second|CORTEX_SHARED.*second.*first/)
  })

  it('rejects duplicate contributor names and malformed declarations', () => {
    const registry = new ShellEnvRegistry(new Context(), { cortexHome: './test-cortex-home' })
    registry.register({
      name: 'declared',
      variables: { CORTEX_DECLARED: { description: 'Declared fact.' } },
      resolve: () => ({}),
    })

    expect(() => registry.register({
      name: 'declared',
      variables: { CORTEX_ANOTHER: { description: 'Another fact.' } },
      resolve: () => ({}),
    })).toThrow(/already registered/)
    expect(() => registry.register({
      name: ' ',
      variables: { CORTEX_BLANK_NAME: { description: 'Blank owner.' } },
      resolve: () => ({}),
    })).toThrow(/name must be non-empty/)
    expect(() => registry.register({
      name: 'invalid-key',
      variables: { cortex_invalid: { description: 'Invalid key.' } } as unknown as Record<'CORTEX_INVALID', { description: string }>,
      resolve: () => ({}),
    })).toThrow(/invalid key/)
    expect(() => registry.register({
      name: 'reserved-key',
      variables: { CORTEX_HOME: { description: 'Reserved key.' } },
      resolve: () => ({}),
    })).toThrow(/reserved key/)
    expect(() => registry.register({
      name: 'blank-description',
      variables: { CORTEX_BLANK_DESCRIPTION: { description: ' ' } },
      resolve: () => ({}),
    })).toThrow(/must describe/)
  })

  it('rejects undeclared variables returned by a contributor', () => {
    const ctx = new Context()
    const registry = new ShellEnvRegistry(ctx, { cortexHome: './test-cortex-home' })
    registry.register({
      name: 'drifted-provider',
      variables: { CORTEX_DECLARED: { description: 'Declared fact.' } },
      resolve: () => ({ CORTEX_UNDECLARED: 'bad' }),
    })

    expect(() => registry.collect(execution())).toThrow(/drifted-provider.*CORTEX_UNDECLARED/)
  })

  it('rejects non-string values returned by a contributor', () => {
    const registry = new ShellEnvRegistry(new Context(), { cortexHome: './test-cortex-home' })
    registry.register({
      name: 'wrong-value-type',
      variables: { CORTEX_STRING: { description: 'String fact.' } },
      resolve: () => ({ CORTEX_STRING: 42 }) as unknown as Record<'CORTEX_STRING', string>,
    })

    expect(() => registry.collect(execution())).toThrow(/wrong-value-type.*non-string.*CORTEX_STRING/)
  })

  it('removes an effect-scoped contributor when its plugin is disposed', async () => {
    const ctx = new Context()
    const registry = new ShellEnvRegistry(ctx, { cortexHome: './test-cortex-home' })
    const fiber = await ctx.plugin({
      inject: ['shellEnv'],
      apply(inner: Context) {
        inner.shellEnv.register({
          name: 'temporary',
          variables: { CORTEX_TEMPORARY: { description: 'Temporary fact.' } },
          resolve: () => ({ CORTEX_TEMPORARY: 'present' }),
        })
      },
    })

    expect(registry.collect(execution()).CORTEX_TEMPORARY).toBe('present')
    await fiber.dispose()
    expect(registry.collect(execution())).not.toHaveProperty('CORTEX_TEMPORARY')
  })

  it('returns an explicit contributor disposer', () => {
    const registry = new ShellEnvRegistry(new Context(), { cortexHome: './test-cortex-home' })
    const dispose = registry.register({
      name: 'explicit-disposal',
      variables: { CORTEX_EXPLICIT_DISPOSAL: { description: 'Explicitly disposed fact.' } },
      resolve: () => ({ CORTEX_EXPLICIT_DISPOSAL: 'present' }),
    })

    expect(registry.collect(execution()).CORTEX_EXPLICIT_DISPOSAL).toBe('present')
    dispose()
    expect(registry.collect(execution())).not.toHaveProperty('CORTEX_EXPLICIT_DISPOSAL')
  })

  it('the plugin registers the service and the persistence contributor on load', async () => {
    const ctx = new Context()
    await ctx.plugin(BashEnvPlugin)
    expect(ctx.shellEnv).toBeInstanceOf(ShellEnvRegistry)
    expect(ctx.shellEnv.list()).toEqual([
      {
        contributor: 'session-persistence',
        description: 'Absolute target path of the current session JSONL when the active persistence backend provides one.',
        key: 'CORTEX_SESSION_JSONL',
      },
    ])
  })

  it('the persistence contributor resolves CORTEX_SESSION_JSONL only for a jsonl backend', async () => {
    const ctx = new Context()
    await ctx.plugin(BashEnvPlugin)
    ctx.provide('sessionPersistence', {
      locate: () => ({ kind: 'jsonl' as const, path: 'C:\\sessions\\s.jsonl' }),
    })
    expect(ctx.shellEnv.collect(execution('sess-p')).CORTEX_SESSION_JSONL).toBe('C:\\sessions\\s.jsonl')
  })

  it('the persistence contributor omits the variable for a non-jsonl backend', async () => {
    const ctx = new Context()
    await ctx.plugin(BashEnvPlugin)
    ctx.provide('sessionPersistence', {
      locate: () => ({ kind: 'sqlite' as const, path: 'C:\\sessions\\s.db' }),
    })
    expect(ctx.shellEnv.collect(execution('sess-p'))).not.toHaveProperty('CORTEX_SESSION_JSONL')
  })

  it('the persistence contributor omits the variable without a persistence backend', async () => {
    const ctx = new Context()
    await ctx.plugin(BashEnvPlugin)
    expect(ctx.shellEnv.collect(execution('sess-p'))).not.toHaveProperty('CORTEX_SESSION_JSONL')
  })
})
