import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { afterEach, beforeEach, expect, it } from 'vitest'
import { assertNoWebGrounding } from './fixtures/runtime-grounding-check.mjs'

let root: string
beforeEach(() => { root = mkdtempSync(join(tmpdir(), 'desktop-grounding-check-')) })
afterEach(() => { rmSync(root, { recursive: true, force: true }) })

function installed(name: string, nested = false): { path: string } {
  const path = `${nested ? 'node_modules/owner/' : ''}node_modules/${name}/package.json`
  const absolute = join(root, path)
  mkdirSync(dirname(absolute), { recursive: true })
  writeFileSync(absolute, JSON.stringify({ name, version: '1.0.0' }))
  return { path }
}

it('admits interactive browser and Web application packages without grounding', () => {
  expect(() => { assertNoWebGrounding(root, [
    installed('@cortex/web-app'), installed('@cortex/client-ui-sidebar-browser'),
    installed('@cortex/browser-use'), installed('@cortex/experimental-browser-use-playwright-mcp'),
    installed('sharp'), installed('node-pty', true),
  ]) }).not.toThrow()
})

it.each([
  '@cortex/web', '@cortex/web-fetch', '@cortex/web-fetch-http', '@cortex/web-search',
  '@cortex/web-search-deepseek', '@cortex/tool-web', '@cortex/tool-web-fetch',
  'turndown', '@joplin/turndown-plugin-gfm', '@mixmark-io/domino', 'domino',
])('rejects %s even when installed only below another package', (name) => {
  expect(() => { assertNoWebGrounding(root, [installed('@cortex/web-app'), installed(name, true)]) })
    .toThrow(`Removed web-grounding dependency is packaged: ${name}`)
})

it('rejects an empty or unresolvable inventory instead of silently skipping it', () => {
  expect(() => { assertNoWebGrounding(root, []) }).toThrow('must contain installed packages')
  expect(() => { assertNoWebGrounding(root, [{ path: 'node_modules/missing/package.json' }]) }).toThrow('ENOENT')
})
