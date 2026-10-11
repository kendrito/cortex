#!/usr/bin/env node
/**
 * Build the integrations the web bundle ships built in (MemOS, Brain, the progress
 * board and Skills Manager). Each is a standalone npm project under integrations/
 * with its own lockfile and toolchain, so the workspace build never reaches it.
 *
 * For each one, in dependency order: install its third-party packages when
 * node_modules is missing, link the `@cortex/*` packages it imports from this
 * checkout (run `pnpm run build` first), then run its own build. Installing reaches
 * the npm registry; every other step is local. Profiles and memory homes are never
 * touched.
 *
 * Usage: node scripts/build-integrations.mjs [--skip-install] [name...]
 */
import { spawnSync } from 'node:child_process'
import { existsSync, lstatSync, mkdirSync, readFileSync, readdirSync, symlinkSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')

/** Build order: Brain reads MemOS's build output. */
const INTEGRATIONS = [
  { name: 'memos', install: 'ci', rebuild: ['better-sqlite3', 'onnxruntime-node'], build: 'build:package' },
  { name: 'memos-brain', install: 'ci', build: 'build' },
  { name: 'progress-board', install: 'ci', build: 'build' },
  // Its upstream lockfile is pnpm's and names unpublished `@cortex/*` packages, so
  // only its pinned third-party packages are installed; Cortex ones are linked.
  { name: 'skills-manager', install: 'pinned', build: 'build' },
]

const args = process.argv.slice(2)
const skipInstall = args.includes('--skip-install')
const only = args.filter(arg => !arg.startsWith('--'))
const unknown = only.filter(name => !INTEGRATIONS.some(item => item.name === name))
if (unknown.length > 0) throw new Error(`Unknown integration: ${unknown.join(', ')}`)

/** Runs a command in `cwd`, failing the whole build on a non-zero exit. */
function run(cwd, command, commandArgs) {
  console.log(`> (${cwd.slice(root.length + 1)}) ${command} ${commandArgs.join(' ')}`)
  const result = spawnSync(command, commandArgs, { cwd, stdio: 'inherit', shell: process.platform === 'win32' })
  if (result.status !== 0) throw new Error(`${command} ${commandArgs.join(' ')} failed in ${cwd} (exit ${result.status})`)
}

/** Workspace package directories by package name: vendor/* and packages/*\/*. */
function workspacePackages() {
  const found = new Map()
  const groups = [join(root, 'vendor'), ...readdirSync(join(root, 'packages'), { withFileTypes: true })
    .filter(entry => entry.isDirectory()).map(entry => join(root, 'packages', entry.name))]
  const candidates = groups.flatMap(group => readdirSync(group, { withFileTypes: true })
    .filter(entry => entry.isDirectory()).map(entry => join(group, entry.name)))
  for (const directory of candidates) {
    const manifest = join(directory, 'package.json')
    if (!existsSync(manifest)) continue
    const { name } = JSON.parse(readFileSync(manifest, 'utf8'))
    if (typeof name === 'string' && !found.has(name)) found.set(name, directory)
  }
  return found
}

/** Every `@cortex/*` name an integration declares, except its own `file:`/`link:` dependencies. */
function cortexNames(manifest) {
  const names = new Set()
  for (const field of ['dependencies', 'devDependencies', 'peerDependencies']) {
    for (const [name, range] of Object.entries(manifest[field] ?? {})) {
      if (name.startsWith('@cortex/') && !/^(?:file|link):/.test(String(range))) names.add(name)
    }
  }
  return [...names]
}

/** Links each declared Cortex package from this checkout, leaving existing entries alone. */
function linkCortexPackages(directory, manifest, packages) {
  const scope = join(directory, 'node_modules', '@cortex')
  mkdirSync(scope, { recursive: true })
  for (const name of cortexNames(manifest)) {
    const destination = join(scope, name.slice('@cortex/'.length))
    if (existsSync(destination) || isLink(destination)) continue
    const target = packages.get(name)
    if (target === undefined) {
      console.warn(`  ${name} is not in this checkout; skipped`)
      continue
    }
    symlinkSync(target, destination, process.platform === 'win32' ? 'junction' : 'dir')
  }
}

function isLink(path) {
  try { return lstatSync(path).isSymbolicLink() } catch { return false }
}

/** The pinned third-party packages of a manifest, as `name@version` install specs. */
function pinnedThirdParty(manifest) {
  return Object.entries({ ...manifest.dependencies, ...manifest.devDependencies })
    .filter(([name]) => !name.startsWith('@cortex/'))
    .map(([name, version]) => `${name}@${version}`)
}

const packages = workspacePackages()
for (const integration of INTEGRATIONS) {
  if (only.length > 0 && !only.includes(integration.name)) continue
  const directory = join(root, 'integrations', integration.name)
  const manifest = JSON.parse(readFileSync(join(directory, 'package.json'), 'utf8'))
  console.log(`\n== ${manifest.name}`)
  if (!skipInstall && !existsSync(join(directory, 'node_modules'))) {
    if (integration.install === 'ci') {
      run(directory, 'npm', ['ci', '--ignore-scripts', '--legacy-peer-deps', '--no-audit', '--no-fund'])
    } else {
      run(directory, 'npm', ['install', '--no-save', '--no-package-lock', '--ignore-scripts', '--legacy-peer-deps', '--no-audit', '--no-fund', ...pinnedThirdParty(manifest)])
    }
    if (integration.rebuild) run(directory, 'npm', ['rebuild', ...integration.rebuild])
  }
  linkCortexPackages(directory, manifest, packages)
  run(directory, 'npm', ['run', integration.build])
}
console.log('\nIntegrations built.')
