/** Guard every shipped profile and preset against restoring upstream network defaults. */
import { existsSync, readFileSync, readdirSync } from 'node:fs'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import * as yaml from 'js-yaml'
import { entryListSchema } from '@cortex/cordis-plugin-include'
import { describe, expect, it } from 'vitest'

const repoRoot = fileURLToPath(new URL('../../../../', import.meta.url))
const bundleRoot = join(repoRoot, 'packages/bundle')
const forbidden = new RegExp('^@cortex/(?:.*(?:telemetry|analytics)|otel|deepseek-account(?:-.*)?|llm-deepseek(?:-.*)?'
  + '|deepseek-llm-api-extensions|plugin-package-inventory-deepseek|session-log-deepseek'
  + '|web|web-(?:search|fetch)(?:-.*)?|tool-web(?:-.*)?|account-controller|ui-settings-(?:account|session-log|web-search))$')

interface Row {
  id?: string
  name?: string
  config?: Record<string, unknown>
}

function rowsIn(value: unknown): Row[] {
  if (Array.isArray(value)) return value.flatMap(rowsIn)
  if (value === null || typeof value !== 'object') return []
  const record = value as Record<string, unknown>
  return [
    ...(typeof record.id === 'string' || typeof record.name === 'string' ? [record as Row] : []),
    ...Object.values(record).flatMap(rowsIn),
  ]
}

function shippedPatches(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name)
    if (entry.isFile()) return entry.name.endsWith('.yml') ? [path] : []
    if (!entry.isDirectory() || ['node_modules', 'lib', 'tests'].includes(entry.name)) return []
    return shippedPatches(path)
  })
}

const patches = shippedPatches(bundleRoot)
patches.push(join(repoRoot, 'apps/cli/src/sdk-source.cordis.patch.yml'))

describe('shipped Cortex privacy boundary', () => {
  it.each(patches)('keeps hosted defaults and grounding out of %s', (path) => {
    const rows = rowsIn(yaml.load(readFileSync(path, 'utf8'), { schema: entryListSchema }))
    for (const row of rows) {
      expect(row.name ?? '', `${path}: ${row.id ?? row.name ?? 'row'}`).not.toMatch(forbidden)
      if (row.name === '@cortex/llm-pi-ai') expect(row.config?.providers ?? {}).toEqual({})
      if (row.id === 'agent-default-model' || row.name === '@cortex/acp') {
        expect(row.config?.provider ?? '').toBe('')
        expect(row.config?.model ?? '').toBe('')
      }
    }
  })

  it('does not ship grounding or outbound telemetry implementations', () => {
    for (const path of [
      'packages/web', 'packages/telemetry/otel', 'packages/host/product-telemetry-otel',
      'packages/session/session-log-deepseek', 'packages/session/session-telemetry-otel',
      'packages/llm/llm-deepseek', 'packages/llm/llm-deepseek-api-key', 'packages/llm/llm-deepseek-account',
      'packages/llm/deepseek-llm-api-extensions', 'packages/llm/plugin-package-inventory-deepseek',
    ]) expect(existsSync(join(repoRoot, path)), path).toBe(false)
  })
})
