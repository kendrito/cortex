/** Generator locale selection follows explicit repository configuration. */
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'
import { catalogPageVariants } from './gen-cordis-catalog.ts'
import { computeModuleGraphOutputs } from './gen-module-graph.ts'

const roots: string[] = []

function fixture(manifest?: string): string {
  const root = mkdtempSync(join(tmpdir(), 'cortex-catalog-locales-'))
  roots.push(root)
  mkdirSync(join(root, 'scripts'))
  const packagePath = join(root, 'packages', 'core', 'fixture')
  mkdirSync(packagePath, { recursive: true })
  writeFileSync(join(packagePath, 'package.json'), JSON.stringify({ name: '@cortex/fixture' }))
  if (manifest !== undefined) writeFileSync(join(root, 'scripts', 'translation-pairing.manifest.json'), manifest)
  return root
}

afterEach(() => {
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true })
})

describe('catalog translation configuration', () => {
  it('generates English alone when the checkout has no translation manifest', () => {
    const root = fixture()
    expect(catalogPageVariants('session.md', root)).toEqual(['session.md'])
    expect([...computeModuleGraphOutputs(root).keys()]).toEqual(['docs/module-graph.md'])
  })

  it('generates both languages and pairing metadata when explicitly configured', () => {
    const root = fixture('{"excluded": []}')
    expect(catalogPageVariants('session.md', root)).toEqual(['session.md', 'session.zh.md'])
    expect([...computeModuleGraphOutputs(root).keys()]).toEqual([
      'docs/module-graph.md', 'docs/module-graph.zh.md', 'docs/module-graph.i18n.yaml',
    ])
  })

  it.each(['{', '{"excluded": false}'])('rejects malformed configured translation metadata: %s', (manifest) => {
    const root = fixture(manifest)
    expect(() => catalogPageVariants('session.md', root)).toThrow()
    expect(() => computeModuleGraphOutputs(root)).toThrow()
  })
})
