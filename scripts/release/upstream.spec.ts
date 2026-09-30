/** A Cortex release identifies its upstream snapshot and versions private and public workspaces together. */
import { spawnSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { expect, it, onTestFinished } from 'vitest'
import { readUpstreamBaseline, verifyCortexBaseline } from './upstream.ts'

const verify = fileURLToPath(new URL('./verify.ts', import.meta.url))
const loader = import.meta.resolve('tsx/esm')
const baseline = {
  repository: 'https://github.com/deepseek-ai/deepseek-harness.git',
  version: '0.2.0-rc.2',
  tag: 'dsh-v0.2.0-rc.2',
  commit: 'a'.repeat(40),
  importedAt: '2026-09-29',
}

function fixture(version = '0.2.0-rc.2.cortex.1') {
  const root = mkdtempSync(join(tmpdir(), 'cortex-upstream-verify-'))
  onTestFinished(() => { rmSync(root, { recursive: true, force: true }) })
  const write = (path: string, content: unknown): void => {
    mkdirSync(dirname(join(root, path)), { recursive: true })
    writeFileSync(join(root, path), `${JSON.stringify(content)}\n`)
  }
  const manifests = ['package.json', 'apps/desktop/package.json', 'packages/core/public/package.json', 'packages/core/private/package.json']
  for (const [index, path] of manifests.entries()) {
    write(path, { name: `@cortex/fixture-${String(index)}`, version, private: index !== 2 })
  }
  write('upstream.json', baseline)
  write('vendor/probe/package.json', { name: '@cortex/vendor-probe', version: '4.0.0' })
  const invoke = (family = 'cortex') => spawnSync(process.execPath, ['--import', loader, verify, '--family', family], {
    cwd: root, encoding: 'utf8', env: { ...process.env, RELEASE_PUBLISH: 'false' },
  })
  return { root, write, manifests, invoke }
}

it('reports the pinned upstream identity when the Cortex release CLI validates all manifests', () => {
  const { root, invoke } = fixture()
  expect(readUpstreamBaseline(root)).toEqual(baseline)
  const result = invoke()
  expect(result.error).toBeUndefined()
  expect(result.status, result.stderr).toBe(0)
  expect(result.stdout).toContain(`upstream ${baseline.tag} (${baseline.version}), commit ${baseline.commit}`)
  expect(result.stdout).toContain('family cortex, 1 member(s), 0.2.0-rc.2.cortex.1')
})

it.each([
  ['0.2.0-rc.2', '0.2.0-rc.2.cortex.12'],
  ['0.2.0', '0.2.0-stable.cortex.1'],
] as const)('accepts upstream %s with Cortex version %s', (upstreamVersion, cortexVersion) => {
  const { root, write } = fixture(cortexVersion)
  const expected = { ...baseline, version: upstreamVersion, tag: `dsh-v${upstreamVersion}` }
  write('upstream.json', expected)
  expect(verifyCortexBaseline(root)).toEqual(expected)
})

it.each([
  '0.2.0-rc.2', '0.2.0-rc.2.cortex.0', '0.2.0-rc.2.cortex.01', '0.2.0-rc.2.cortex.-1',
  '0.2.0-rc.3.cortex.1', '0.2.0-rc.2.cortex.1+build', '0.2.0-rc.2.cortex.1.extra',
])('rejects a Cortex version without the matching baseline and positive revision: %s', (version) => {
  const { root } = fixture(version)
  expect(() => verifyCortexBaseline(root)).toThrow('package.json version must be 0.2.0-rc.2.cortex.<positive integer>')
})

it.each(['package.json', 'apps/desktop/package.json', 'packages/core/public/package.json', 'packages/core/private/package.json'])('rejects a mismatched %s through the release CLI', (path) => {
  const { write, invoke } = fixture()
  write(path, { name: '@cortex/mismatch', private: true, version: '0.2.0-rc.2.cortex.2' })
  const result = invoke()
  expect(result.status).not.toBe(0)
  expect(result.stderr).toContain('version must match package.json')
})

it.each([
  ['repository', 'https://example.test/other.git'],
  ['version', '0.02.0-rc.2'],
  ['version', '0.2.0-rc.02'],
  ['version', '0.2.0-rc..2'],
  ['version', '0.2.0-rc.2+build'],
  ['version', 2],
  ['tag', 'dsh-v0.2.0-rc.3'],
  ['commit', '639ed01'],
  ['commit', 'g'.repeat(40)],
  ['importedAt', '2026-02-30'],
  ['importedAt', '2026-9-29'],
  ['importedAt', 'not-a-date'],
  ['importedAt', null],
] as const)('rejects invalid upstream %s: %s', (field, value) => {
  const { root, write } = fixture()
  write('upstream.json', { ...baseline, [field]: value })
  expect(() => readUpstreamBaseline(root)).toThrow(`upstream.json ${field} must`)
})

it.each([null, [], 'baseline'])('rejects an upstream JSON value that is not an object: %s', (content) => {
  const { root, write } = fixture()
  write('upstream.json', content)
  expect(() => readUpstreamBaseline(root)).toThrow('upstream.json must contain a JSON object')
})

it('requires upstream.json for Cortex verification and leaves vendor verification independent', () => {
  const { root, invoke } = fixture()
  rmSync(join(root, 'upstream.json'))
  const cortex = invoke()
  expect(cortex.status).not.toBe(0)
  expect(cortex.stderr).toContain('upstream.json')
  const vendor = invoke('vendor')
  expect(vendor.error).toBeUndefined()
  expect(vendor.status, vendor.stderr).toBe(0)
  expect(vendor.stdout).toContain('family vendor, 1 member(s), 4.0.0')
})

it('rejects a root-only checkout instead of silently verifying no workspace manifests', () => {
  const { root, manifests } = fixture()
  for (const path of manifests.slice(1)) rmSync(join(root, path))
  expect(() => verifyCortexBaseline(root)).toThrow('found no apps or packages manifests')
})
