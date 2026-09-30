import { join, resolve } from 'node:path'
import { tmpdir } from 'node:os'
import { pathToFileURL } from 'node:url'
import { expect, it } from 'vitest'
import { resolveTestyExecutable } from '../src/native-runtime.ts'

it('resolves the packaged native runtime beside ASAR instead of an archive path', () => {
  const resources = join(tmpdir(), 'Cortex Testy', 'resources')
  const moduleUrl = pathToFileURL(join(resources, 'app.asar', 'cortex', 'node_modules', '@cortex', 'testy', 'lib', 'index.js')).href
  expect(resolveTestyExecutable('', moduleUrl)).toBe(join(resources, 'app.asar.unpacked', 'cortex', 'node_modules', '@cortex', 'testy', 'runtime', 'win-x64', 'Testy.Cli.exe'))
})

it.each(['src', 'lib'])('resolves the complete source or built runtime from %s', (directory) => {
  const root = join(tmpdir(), 'Cortex Testy', 'packages', 'extensions', 'testy')
  expect(resolveTestyExecutable('', pathToFileURL(join(root, directory, 'index.js')).href))
    .toBe(join(root, 'runtime', 'win-x64', 'Testy.Cli.exe'))
})

it('preserves administrator executable selection instead of rewriting its location', () => {
  const configured = join(tmpdir(), 'custom', 'app.asar', 'Testy.Cli.exe')
  expect(resolveTestyExecutable(configured, import.meta.url)).toBe(resolve(configured))
})
