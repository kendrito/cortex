/** Runtime reachability ignores declaration-only export roots, not runtime requests. */
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { expect, it, type TestContext } from 'vitest'
import { packVfsImage, type PackOptions } from '../src/pack.ts'

const SUBJECT = '@cortex/image-export-fixture'

function fixture(test: TestContext, face: unknown, source = 'export const value = 1', name = SUBJECT): PackOptions {
  const root = mkdtempSync(join(tmpdir(), 'cortex-image-exports-'))
  test.onTestFinished(() => { rmSync(root, { recursive: true, force: true }) })
  mkdirSync(join(root, 'lib'))
  writeFileSync(join(root, 'package.json'), JSON.stringify({
    name,
    type: 'module',
    files: ['lib'],
    exports: {
      '.': { types: './lib/index.d.ts', default: './lib/index.js' },
      './contract': face,
    },
  }))
  writeFileSync(join(root, 'lib/index.js'), source)
  writeFileSync(join(root, 'lib/index.d.ts'), 'export declare const value: number')
  writeFileSync(join(root, 'lib/contract.js'), 'export const value = 2')
  return {
    config: `- id: subject\n  name: '${name}'\n`,
    profile: 'export-check',
    workspaces: new Map([[name, root]]),
    resolveFrom: root,
    entries: [],
  }
}

it.for([
  { types: './lib/index.d.ts' },
  { 'types@>=5.2': './lib/index.d.ts' },
  { browser: { types: './lib/index.d.ts' } },
  [{ types: './lib/index.d.ts' }],
])('packs a declaration-only export without requesting a runtime entry: %j', (face, test) => {
  const result = packVfsImage(fixture(test, face))
  expect(result.missing).toEqual([])
  expect(Object.keys(result.files)).toContain(`node_modules/${SUBJECT}/lib/index.js`)
  expect(Object.keys(result.files)).not.toContain(`node_modules/${SUBJECT}/lib/index.d.ts`)
  expect(Object.keys(result.files)).not.toContain(`node_modules/${SUBJECT}/lib/contract.js`)
})

it.for([
  { types: './lib/index.d.ts', default: './lib/contract.js' },
  [{ types: './lib/index.d.ts' }, './lib/contract.js'],
])('retains the runtime target beside a types condition: %j', (face, test) => {
  const result = packVfsImage(fixture(test, face))
  expect(Object.keys(result.files)).toContain(`node_modules/${SUBJECT}/lib/contract.js`)
})

it('rejects a runtime import of a declaration-only export', (test) => {
  const options = fixture(test, { types: './lib/index.d.ts' }, `export { value } from '${SUBJECT}/contract'`)
  expect(() => packVfsImage(options)).toThrow('does not export "./contract"')
})

it('rejects an assembly entry that requests a declaration-only export', (test) => {
  const options = fixture(test, { types: './lib/index.d.ts' })
  expect(() => packVfsImage({ ...options, entries: [`${SUBJECT}/contract`] })).toThrow('worker assembly entry')
})

it('rejects a missing runtime target beside a types condition', (test) => {
  const options = fixture(test, { types: './lib/index.d.ts', default: './lib/missing.js' })
  expect(() => packVfsImage(options)).toThrow('cannot resolve')
})

it.for([SUBJECT, '@deepseek-ai/node-addon-system'])('rejects unresolved runtime imports from owned package %s', (name, test) => {
  const options = fixture(test, { types: './lib/index.d.ts' }, "import 'missing-runtime-module'", name)
  expect(() => packVfsImage(options)).toThrow(`node_modules/${name}/lib/index.js: "missing-runtime-module"`)
})

it('keeps third-party platform-dispatch requests deferred until use', (test) => {
  const options = fixture(test, { types: './lib/index.d.ts' }, "require('missing-platform-module')", '@third-party/platform-dispatch')
  expect(packVfsImage(options).unresolvedExternalRequests)
    .toEqual(['node_modules/@third-party/platform-dispatch/lib/index.js: "missing-platform-module"'])
})
