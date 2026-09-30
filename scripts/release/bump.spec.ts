/** The real version-preparation CLI owns file edits, never staging or committing them. */
import { execFileSync, spawnSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { expect, it, onTestFinished } from 'vitest'

const bump = fileURLToPath(new URL('./bump.ts', import.meta.url))
const loader = import.meta.resolve('tsx/esm')

function fixture() {
  const root = mkdtempSync(join(tmpdir(), 'cortex-version-prepare-'))
  onTestFinished(() => { rmSync(root, { recursive: true, force: true }) })
  const gitEnvironment = Object.fromEntries(Object.entries(process.env).filter(([key]) => !key.toUpperCase().startsWith('GIT_')))
  const git = (...args: string[]): string => execFileSync('git', args, { cwd: root, encoding: 'utf8', env: gitEnvironment }).trim()
  const write = (path: string, content: string): void => {
    mkdirSync(dirname(join(root, path)), { recursive: true })
    writeFileSync(join(root, path), content)
  }
  const manifests = ['package.json', 'packages/core/probe/package.json', 'packages/core/private/package.json', 'apps/desktop/package.json']
  for (const [index, path] of manifests.entries()) write(path, `${JSON.stringify({
    name: `@cortex/fixture-${String(index)}`, version: '0.2.0-rc.2', ...(index === 1 ? {} : { private: true }),
  }, null, 2)}\n`)
  write('vendor/probe/package.json', '{"name":"@cortex/vendor-probe","version":"4.0.0"}\n')
  write('README.md', 'fixture\n')
  write('pnpm-lock.yaml', 'lockfileVersion: 9\n')
  const pnpm = join(root, 'pnpm/pnpm.cjs')
  write('pnpm/pnpm.cjs', 'const fs = require("node:fs"); fs.writeFileSync("pnpm-lock.yaml", "lockfileVersion: 9\\n# arguments: " + JSON.stringify(process.argv.slice(2)) + "\\n");\n')
  git('init', '--quiet')
  git('config', 'core.autocrlf', 'false')
  git('config', 'core.hooksPath', join(root, '.git/unused-hooks'))
  git('add', '.')
  git('-c', 'user.name=Cortex fixture', '-c', 'user.email=fixture@example.test', '-c', 'commit.gpgsign=false', 'commit', '--quiet', '-m', 'Fixture baseline')
  const head = git('rev-parse', 'HEAD')
  const environment: NodeJS.ProcessEnv = { ...gitEnvironment, npm_execpath: pnpm }
  const invoke = (...args: string[]) => spawnSync(process.execPath, ['--import', loader, bump, '--family', 'cortex', '0.3.0-beta.1', ...args], {
    cwd: root, encoding: 'utf8', env: environment,
  })
  const versions = () => manifests.map(path => readFileSync(join(root, path), 'utf8'))
  return { root, git, write, head, invoke, versions, manifests, environment }
}

it('prepares every Cortex manifest and lockfile without staging, committing, tagging, or running install scripts', () => {
  const { root, git, head, invoke, versions, manifests } = fixture()
  const result = invoke()
  expect(result.error).toBeUndefined()
  expect(result.status, result.stderr).toBe(0)
  expect(versions().every(manifest => manifest.includes('"version": "0.3.0-beta.1"'))).toBe(true)
  expect(readFileSync(join(root, 'vendor/probe/package.json'), 'utf8')).toContain('4.0.0')
  expect(readFileSync(join(root, 'pnpm-lock.yaml'), 'utf8')).toContain('["install","--lockfile-only","--ignore-scripts"]')
  expect(git('rev-parse', 'HEAD')).toBe(head)
  expect(git('diff', '--cached', '--name-only')).toBe('')
  expect(git('tag', '--list')).toBe('')
  expect(git('diff', '--name-only').split('\n').sort()).toEqual([...manifests, 'pnpm-lock.yaml'].sort())
  expect(result.stdout).toContain('prepared unstaged changes')
  expect(result.stdout).toContain('merges to main')
  expect(result.stdout).toContain('git tag -a cortex-v0.3.0-beta.1 <tested-main-commit>')
})

it.each(['tracked', 'staged', 'untracked'] as const)('rejects %s changes before writing manifests or invoking pnpm', (kind) => {
  const { root, git, write, head, invoke, versions } = fixture()
  write(kind === 'untracked' ? 'scratch.txt' : 'README.md', 'existing user work\n')
  if (kind === 'staged') git('add', 'README.md')
  const before = { versions: versions(), status: git('status', '--porcelain=v1'), index: git('diff', '--cached'), lock: readFileSync(join(root, 'pnpm-lock.yaml'), 'utf8') }
  const result = invoke()
  expect(result.error).toBeUndefined()
  expect(result.status).not.toBe(0)
  expect(result.stderr).toContain('start from a clean working tree and index')
  expect(versions()).toEqual(before.versions)
  expect(git('status', '--porcelain=v1')).toBe(before.status)
  expect(git('diff', '--cached')).toBe(before.index)
  expect(readFileSync(join(root, 'pnpm-lock.yaml'), 'utf8')).toBe(before.lock)
  expect(git('rev-parse', 'HEAD')).toBe(head)
})

it('allows a dry run over existing work without changing files, the index, or history', () => {
  const { root, git, write, head, invoke, versions } = fixture()
  write('README.md', 'staged user work\n')
  git('add', 'README.md')
  write('scratch.txt', 'untracked user work\n')
  const before = { versions: versions(), status: git('status', '--porcelain=v1'), index: git('diff', '--cached'), lock: readFileSync(join(root, 'pnpm-lock.yaml'), 'utf8') }
  const result = invoke('--dry-run')
  expect(result.error).toBeUndefined()
  expect(result.status, result.stderr).toBe(0)
  expect(result.stdout).toContain('dry run, nothing written')
  expect(result.stdout).toContain('0.2.0-rc.2 -> 0.3.0-beta.1')
  expect(versions()).toEqual(before.versions)
  expect(git('status', '--porcelain=v1')).toBe(before.status)
  expect(git('diff', '--cached')).toBe(before.index)
  expect(readFileSync(join(root, 'pnpm-lock.yaml'), 'utf8')).toBe(before.lock)
  expect(git('rev-parse', 'HEAD')).toBe(head)
})

it.each(['GIT_DIR', 'GIT_COMMON_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE'])('rejects inherited %s instead of inspecting a different repository or index', (key) => {
  const { root, git, head, invoke, versions, environment } = fixture()
  environment[key] = join(root, 'different-git-location')
  const before = versions()
  const result = invoke()
  expect(result.status).not.toBe(0)
  expect(result.stderr).toContain(`unset ${key} before preparing a version`)
  expect(versions()).toEqual(before)
  expect(readFileSync(join(root, 'pnpm-lock.yaml'), 'utf8')).toBe('lockfileVersion: 9\n')
  expect(git('status', '--porcelain=v1')).toBe('')
  expect(git('rev-parse', 'HEAD')).toBe(head)
})
