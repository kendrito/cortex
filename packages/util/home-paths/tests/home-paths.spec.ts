import { mkdir, mkdtemp, realpath, rm, symlink, writeFile } from 'node:fs/promises'
import { homedir, tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  DEFAULT_CORTEX_HOME_DISPLAY,
  CORTEX_HOME_DIR_NAME,
  canonicalizeWatchPath,
  defaultCortexHome,
  cortexHomeDisplay,
  cortexHomePath,
  expandHomePath,
  resolveCortexHome,
} from '@cortex/home-paths'

afterEach(() => {
  vi.unstubAllEnvs()
})

describe('cortex path helpers', () => {
  it('owns the shared default Cortex home directory name', () => {
    expect(CORTEX_HOME_DIR_NAME).toBe('.cortex')
    expect(DEFAULT_CORTEX_HOME_DISPLAY).toBe('~/.cortex')
    expect(defaultCortexHome()).toBe(join(homedir(), '.cortex'))
  })

  it('expands tilde paths without changing non-tilde paths', () => {
    expect(expandHomePath('~')).toBe(homedir())
    expect(expandHomePath('~/.cortex')).toBe(join(homedir(), '.cortex'))
    expect(expandHomePath('~\\.cortex')).toBe(join(homedir(), '.cortex'))
    expect(expandHomePath('/tmp/.cortex')).toBe('/tmp/.cortex')
    expect(expandHomePath('~other/.cortex')).toBe('~other/.cortex')
  })

  it('resolves explicit path before CORTEX_HOME and the default', () => {
    const envHome = join(homedir(), 'env-cortex')

    expect(resolveCortexHome('/tmp/explicit-cortex', { CORTEX_HOME: '~/env-cortex' })).toBe(resolve('/tmp/explicit-cortex'))
    expect(resolveCortexHome(undefined, { CORTEX_HOME: '~/env-cortex' })).toBe(envHome)
    expect(resolveCortexHome(undefined, {})).toBe(defaultCortexHome())
  })

  it('treats an empty or whitespace-only CORTEX_HOME as unset', () => {
    expect(resolveCortexHome(undefined, { CORTEX_HOME: '' })).toBe(defaultCortexHome())
    expect(resolveCortexHome(undefined, { CORTEX_HOME: '   ' })).toBe(defaultCortexHome())
  })

  it('joins child segments onto the resolved CORTEX_HOME', () => {
    vi.stubEnv('CORTEX_HOME', '~/env-cortex')
    expect(cortexHomePath()).toBe(join(homedir(), 'env-cortex'))
    expect(cortexHomePath('storages', 'cache')).toBe(join(homedir(), 'env-cortex', 'storages', 'cache'))
  })

  it('labels a resolved home by whether it is the default root', () => {
    expect(cortexHomeDisplay(resolve(defaultCortexHome()))).toBe('~/.cortex')
    expect(cortexHomeDisplay('/some/other/root')).toBe('$CORTEX_HOME')
  })

  it('canonicalizes a watcher ancestor while preserving a missing suffix', async () => {
    const root = await mkdtemp(join(tmpdir(), 'cortex-watch-path-'))
    const target = join(root, 'target')
    const alias = join(root, 'alias')
    try {
      await mkdir(target)
      await symlink(target, alias, process.platform === 'win32' ? 'junction' : 'dir')
      await expect(canonicalizeWatchPath(join(alias, 'later', 'config.yml'))).resolves.toBe(
        join(await realpath(target), 'later', 'config.yml'),
      )
      const file = join(root, 'file')
      await writeFile(file, 'not a directory')
      await expect(canonicalizeWatchPath(join(file, 'child'))).rejects.toMatchObject({ code: 'ENOTDIR' })
    } finally {
      await rm(root, { recursive: true, force: true })
    }
  })
})
