import { mkdir, mkdtemp, realpath, rm, symlink, writeFile } from 'node:fs/promises'
import { homedir, tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  DEFAULT_CORTEX_HOME_DISPLAY,
  CORTEX_HOME_DIR_NAME,
  canonicalizeWatchPath,
  defaultCortexHome,
  cortexCachePath,
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

  it.each([
    [undefined, join(homedir(), '.cortex')],
    ['', join(homedir(), '.cortex')],
    ['   ', join(homedir(), '.cortex')],
    ['~/env-cortex', join(homedir(), 'env-cortex')],
    ['./relative-cortex', resolve('./relative-cortex')],
  ] as const)('resolves cache paths with CORTEX_HOME=%j', (home, expectedHome) => {
    vi.stubEnv('CORTEX_HOME', home)
    try {
      expect(cortexCachePath()).toBe(join(expectedHome, 'cache'))
      expect(cortexCachePath('models', 'index.json')).toBe(join(expectedHome, 'cache', 'models', 'index.json'))
    } finally {
      vi.unstubAllEnvs()
    }
  })

  it('resolves configured cache homes before the environment', () => {
    vi.stubEnv('CORTEX_HOME', '~/env-cortex')
    try {
      expect(cortexCachePath({ cortexHome: '~/explicit-cortex' })).toBe(join(homedir(), 'explicit-cortex', 'cache'))
      expect(cortexCachePath({ cortexHome: './explicit-cortex' }, 'attachments', 'request-images'))
        .toBe(resolve('./explicit-cortex/cache/attachments/request-images'))
      expect(cortexCachePath({}, 'attachments')).toBe(join(homedir(), 'env-cortex', 'cache', 'attachments'))
    } finally {
      vi.unstubAllEnvs()
    }
  })

  it('canonicalizes a watcher ancestor while preserving a missing suffix', async () => {
    const root = await mkdtemp(join(tmpdir(), 'cortex-watch-path-'))
    const target = join(root, 'target')
    const alias = join(root, 'alias')
    try {
      await mkdir(target)
      await symlink(target, alias, process.platform === 'win32' ? 'junction' : 'dir')
      await expect(canonicalizeWatchPath(alias)).resolves.toBe(await realpath(target))
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
