import { defineConfig } from 'tsdown'
import { existsSync, readdirSync } from 'node:fs'
import { typertPlugin } from './packages/typert/generator/lib/types/tsdown-plugin.js'

function isBuildFaceClient(value: unknown): boolean {
  if (value === undefined || value === 'host') return false
  if (value === 'client') return true
  throw new Error(`tsdown: --env.CORTEX_BUILD_FACE must be host or client, received ${String(value)}`)
}

/**
 * The ordinary workspace build consumes JavaScript emitted by the Host
 * TypeScript project and runs Typert. The Client pass selects packages that
 * declare a browser bundle and lets their package-local configs emit both
 * their Node loader entry and browser artifact. `apps/desktop` bundles after
 * this pass (root package.json `build:lib:host`): its main bundle inlines
 * workspace devDependencies from their lib/ output, and tsdown builds
 * workspace members concurrently without ordering them.
 */
export default defineConfig(({ env }) => {
  const client = isBuildFaceClient(env?.CORTEX_BUILD_FACE)
  // A source update can leave empty obsolete directories in an existing checkout.
  // Only package manifests define workspace build units.
  const groups = ['vendor', ...readdirSync('packages', { withFileTypes: true })
    .filter(entry => entry.isDirectory()).map(entry => `packages/${entry.name}`)]
  const packages = groups.flatMap(group => readdirSync(group, { withFileTypes: true })
    .filter(entry => entry.isDirectory() && existsSync(`${group}/${entry.name}/package.json`))
    .map(entry => `${group}/${entry.name}`))
  return {
    workspace: client
      ? [...packages, 'apps/cli']
      : [...packages, 'apps/cli', 'apps/desktop-host'],
    entry: client ? '' : ['lib/types/{index,invariant,startup}.js'],
    outDir: 'lib',
    format: ['esm'],
    platform: 'node',
    target: 'es2024',
    fixedExtension: false,
    dts: false,
    clean: false,
    plugins: client ? [] : [typertPlugin({ mode: 'workspace', faces: ['host'] })],
  }
})
