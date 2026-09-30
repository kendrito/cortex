/** Shipped browser composition keeps telemetry, account uploads, and grounding absent. */
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import * as yaml from 'js-yaml'
import { entryListSchema } from '@cortex/cordis-plugin-include'
import { describe, expect, it } from 'vitest'

interface Row {
  id: string
  name?: string
  disabled?: unknown
  config?: unknown
}

function rowsIn(value: unknown): Row[] {
  if (Array.isArray(value)) return value.flatMap(rowsIn)
  if (typeof value !== 'object' || value === null) return []
  const record = value as Record<string, unknown>
  const own: Row[] = typeof record.id === 'string' ? [{
    id: record.id,
    ...(typeof record.name === 'string' ? { name: record.name } : {}),
    disabled: record.disabled,
    config: record.config,
  }] : []
  return [...own, ...Object.values(record).flatMap(rowsIn)]
}

function load(path: string): Row[] {
  return rowsIn(yaml.load(readFileSync(fileURLToPath(new URL(path, import.meta.url)), 'utf8'), {
    schema: entryListSchema,
  }))
}

describe('Cortex browser composition privacy', () => {
  it('keeps analytics, account integrations, upload settings, and grounding out of the browser bundle', () => {
    const rows = load('../cordis.patch.yml')
    const forbidden = [
      'desktop-product-telemetry', 'product-analytics', 'account-controller',
      'ui-settings-account', 'ui-settings-session-log', 'ui-settings-web-search', 'tool-web',
      'atlassian', 'ui-atlassian',
    ]
    for (const id of forbidden) expect(rows.find(row => row.id === id), id).toBeUndefined()
    for (const id of ['session-turn-outline']) {
      expect(rows.find(row => row.id === id)?.name, id).toBeDefined()
    }
    const manifest = JSON.parse(readFileSync(fileURLToPath(new URL('../package.json', import.meta.url)), 'utf8')) as {
      dependencies: Record<string, string>
    }
    expect(Object.keys(manifest.dependencies).filter(name =>
      /atlassian|product-analytics|telemetry|account-controller|ui-settings-(account|session-log|web-search)|\/tool-web$/.test(name),
    )).toEqual([])
  })

  it.each(['standard', 'ptc', 'cordis', 'minimal'])('keeps grounding out of the %s preset', (preset) => {
    const rows = load(`../presets/${preset}.patch.yml`)
    expect(rows.find(row => row.id === 'tool-web')).toBeUndefined()
    if (preset === 'minimal') return
    for (const provider of ['codex', 'claude-code']) {
      const row = rows.find(candidate => candidate.id === `tool-subagent-${provider}`)
      expect(row?.name).toBe('@cortex/tool-subagent')
      expect(row?.disabled).toBeUndefined()
      expect(row?.config).toMatchObject({ provider, backgroundMode: 'one-shot', maxDepth: 'provider-managed' })
    }
  })
})
