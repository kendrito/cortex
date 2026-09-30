/** Persistence pairs remain complete when general translated documentation is absent. */
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { afterEach, expect, it } from 'vitest'
import { renderPersistencePair } from './persistence-artifacts.ts'

const roots: string[] = []
afterEach(() => { for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true }) })

function fixture(): string {
  const root = mkdtempSync(join(tmpdir(), 'cortex-persistence-pair-'))
  roots.push(root)
  return root
}

function save(root: string, path: string, content: string): void {
  mkdirSync(dirname(join(root, path)), { recursive: true })
  writeFileSync(join(root, path), content)
}

function pair(en: string, zh: string): [string, string] {
  return [
    `# Catalog\n\nEnglish | [中文](persistence-catalog.zh.md)\n\n## Links\n\n${en}\n`,
    `# 目录\n\n[English](persistence-catalog.md) | 中文\n\n## 链接\n\n${zh}\n`,
  ]
}

it('keeps its generated pair and sidecar while absent public translations link to English', () => {
  const root = fixture()
  const input = pair('[Session][session]\n\n[session]: subsystems/session.md?view=all#replay',
    '[Session][session]\n\n[session]: subsystems/session.zh.md?view=all#replay')
  const artifacts = renderPersistencePair(root, 'docs/persistence-catalog.md', ...input)
  expect(artifacts.map(file => file.path)).toEqual([
    'docs/persistence-catalog.md', 'docs/persistence-catalog.zh.md', 'docs/persistence-catalog.i18n.yaml',
  ])
  expect(artifacts[1]?.content).toContain('subsystems/session.md?view=all#replay')
  expect(artifacts[1]?.content).toContain('[English](persistence-catalog.md) | 中文')
  for (const file of artifacts) save(root, file.path, file.content)
  expect(renderPersistencePair(root, 'docs/persistence-catalog.md', ...input)).toEqual(artifacts)
  expect(readFileSync(join(root, 'docs/persistence-catalog.md'), 'utf8')).toBe(input[0])
})

it('retains explicitly present translated targets without requiring a global manifest', () => {
  const root = fixture()
  save(root, 'docs/persistence-changes/previous.zh.md', '# 已有记录\n')
  const input = pair('[Previous](persistence-changes/previous.md)', '[已有记录](persistence-changes/previous.zh.md)')
  const artifacts = renderPersistencePair(root, 'docs/persistence-catalog.md', ...input)
  expect(artifacts[1]?.content).toBe(input[1])
})

it('uses the selected checkout translation manifest when it declares a pair', () => {
  const root = fixture()
  save(root, 'scripts/translation-pairing.manifest.json', JSON.stringify({ excluded: [] }))
  const input = pair('[Session](subsystems/session.md)', '[Session](subsystems/session.zh.md)')
  expect(renderPersistencePair(root, 'docs/persistence-catalog.md', ...input)[1]?.content).toBe(input[1])
})

it('still rejects different document targets, external URLs and code across the pair', () => {
  const root = fixture()
  for (const input of [
    pair('[Session](subsystems/session.md)', '[Session](subsystems/persistence.zh.md)'),
    pair('[Reference](https://example.test/reference.md)', '[Reference](https://example.test/reference.zh.md)'),
    pair('```ts\nconst version = 4\n```', '```ts\nconst version = 5\n```'),
  ]) expect(() => renderPersistencePair(root, 'docs/persistence-catalog.md', ...input)).toThrow('bilingual structure mismatch')
})
