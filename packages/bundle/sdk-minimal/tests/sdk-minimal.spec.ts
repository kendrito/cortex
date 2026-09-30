/** The standalone SDK-minimal bundle's complete declared Cordis tree. */

import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import * as yaml from 'js-yaml'
import { describe, expect, it } from 'vitest'
import { entryListSchema } from '@cortex/cordis-plugin-include'

function packageName(specifier: string): string {
  return specifier.startsWith('@') ? specifier.split('/').slice(0, 2).join('/') : specifier.split('/')[0]!
}

describe('cortex-sdk-minimal bundle', () => {
  it('declares one standalone allowlisted tree with every row dependency', () => {
    const root = fileURLToPath(new URL('..', import.meta.url))
    const manifest = JSON.parse(readFileSync(resolve(root, 'package.json'), 'utf8')) as {
      dependencies?: Record<string, string>
      cortex?: { bundle?: { patch?: string } }
    }
    expect(manifest.cortex?.bundle?.patch).toBe('./cordis.patch.yml')
    const patches = yaml.load(
      readFileSync(resolve(root, manifest.cortex!.bundle!.patch!), 'utf8'),
      { schema: entryListSchema },
    ) as Array<{ insert?: Array<{ id?: string; inject?: string[]; name?: string; config?: Record<string, unknown>; disabled?: unknown }> }>
    expect(patches).toHaveLength(1)
    const rows = patches[0]?.insert ?? []
    expect(rows.map(row => [row.id, row.name])).toEqual([
      ['sdk-app-startup', '@cortex/sdk-app'],
      ['sdk-jsonrpc-server', '@cortex/sdk-jsonrpc-server'],
      ['llm-pi-ai', '@cortex/llm-pi-ai'],
      ['credentials', '@cortex/credentials-local'],
      ['sandbox', '@cortex/sandbox-local'],
      ['session-projection', '@cortex/session-projection'],
      ['sandbox-policy', '@cortex/sandbox-policy'],
      ['subprocess', '@cortex/subprocess-local'],
      ['pty', '@cortex/terminal'],
      ['terminal-bash', '@cortex/terminal-bash'],
      ['terminal-pwsh', '@cortex/terminal-bash'],
      ['timer', '@cortex/cordis-plugin-timer'],
      ['llm', '@cortex/llm'],
      ['session', '@cortex/session'],
      ['session-title', '@cortex/session-title'],
      ['system-prompt', '@cortex/system-prompt'],
      ['tools', '@cortex/tools'],
      ['mcp-resources', '@cortex/mcp-resources'],
      ['agent', '@cortex/agent'],
      ['llm-retry', '@cortex/llm-retry'],
      ['jobs', '@cortex/jobs-local'],
      ['invariants', '@cortex/invariants'],
      ['session-invariant', '@cortex/session/invariant'],
      ['agent-invariant', '@cortex/agent/invariant'],
      ['scope-invariant', '@cortex/scope/invariant'],
      ['agent-loop-invariant', '@cortex/agent-loop/invariant'],
      ['agent-loop', '@cortex/agent-loop'],
      ['persistent-bash', '@cortex/tool-bash-persistent'],
      ['persistent-pwsh', '@cortex/tool-pwsh-persistent'],
      ['sessions', '@cortex/session-persistence-jsonl'],
    ])
    expect(rows.find(row => row.id === 'llm-pi-ai')?.config).toBeUndefined()
    expect(rows.find(row => row.id === 'sdk-app-startup')?.config).toEqual({ profile: 'sdk-minimal' })
    expect(rows.find(row => row.id === 'sdk-jsonrpc-server')).toMatchObject({
      inject: ['sdkAppStartup', 'loader'],
      config: { maxTokensAsSuccess: false },
    })
    expect(rows.find(row => row.id === 'llm-deepseek')).toBeUndefined()
    expect(rows.find(row => row.id === 'system-prompt')?.config).toEqual({
      includeHarnessIdentity: false,
      includeRuntimeContext: false,
      personaPrefix: { __jsExpr: "process.env.CORTEX_SYSTEM_PROMPT ?? 'You are a helpful software engineer assistant.'" },
    })
    expect(rows.find(row => row.id === 'agent-loop')?.config).toEqual({ agents: [] })
    expect(rows.find(row => row.id === 'terminal-bash')).toMatchObject({
      disabled: { __jsExpr: "process.platform === 'win32'" },
    })
    expect(rows.find(row => row.id === 'terminal-pwsh')).toMatchObject({
      disabled: { __jsExpr: "process.platform !== 'win32'" },
      config: { shellDialect: 'pwsh', timeoutMs: 300000 },
    })
    expect(Object.keys(manifest.dependencies ?? {}).sort()).toEqual(
      [...new Set(rows.map(row => row.name).filter((name): name is string => name !== undefined).map(packageName))].sort(),
    )
  })
})
