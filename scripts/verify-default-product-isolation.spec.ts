/** Source, installation, and composition regressions for default-product isolation. */

import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { afterEach, describe, expect, it } from 'vitest'
import { verifyDefaultProductIsolation } from './verify-default-product-isolation.ts'

const roots: string[] = []
const experimental = '@cortex/experimental-prototype'
const core = '@cortex/core'
const base = '@cortex/base'
const profile = 'packages/boot/app-boot/src/profile.ts'
const preset = 'packages/bundle/web-app/presets/standard.patch.yml'
const patch = 'packages/bundle/base/cordis.patch.yml'
const guiPatch = 'packages/bundle/web-app/cordis.patch.yml'
const gui = '@cortex/web-app'
const native = '@cortex/experimental-computer-use-cua-driver-native'
const nativeDirectory = 'packages/experimental/computer-use-cua-driver-native'
const browser = '@cortex/experimental-browser-use-playwright-mcp'
const browserDirectory = 'packages/experimental/browser-use-playwright-mcp'
const browserRuntime = '@cortex/experimental-browser-use-runtime'
const browserRuntimeDirectory = 'packages/experimental/browser-use-runtime'
const guiDisabled = "!['web', 'desktop'].includes(ctx.get('profileContext')?.name ?? '')"

function nativeRow(disabled: unknown = { __jsExpr: `process.platform !== 'win32' || ${guiDisabled}` }): Record<string, unknown> {
  return { id: 'computer-use-cua-driver-native', name: native, disabled }
}

function browserRow(): Record<string, unknown> {
  return { id: 'browser-use-playwright-mcp', name: browser, disabled: { __jsExpr: guiDisabled }, config: { mode: 'launch', headless: true } }
}

function browserFixture(): string {
  const root = guiFixture()
  write(root, `${browserDirectory}/package.json`, { name: browser, dependencies: { [browserRuntime]: 'workspace:*' } })
  write(root, `${browserDirectory}/src/index.ts`, `import '${browserRuntime}/mcp'\n`)
  write(root, `${browserRuntimeDirectory}/package.json`, { name: browserRuntime })
  write(root, `${browserRuntimeDirectory}/src/mcp.ts`, "import './index.ts'\n")
  write(root, `${browserRuntimeDirectory}/src/index.ts`, 'export {}\n')
  manifest(root, 'packages/bundle/web-app/package.json', { dependencies: { [browser]: 'workspace:*' } })
  write(root, guiPatch, [{ insert: [browserRow()] }])
  return root
}

function guiFixture(): string {
  const root = fixture()
  write(root, `${nativeDirectory}/package.json`, { name: native })
  write(root, `${nativeDirectory}/src/index.ts`, "import './helper.ts'\n")
  write(root, `${nativeDirectory}/src/helper.ts`, 'export {}\n')
  write(root, 'packages/bundle/web-app/package.json', {
    name: gui, dependencies: { [native]: 'workspace:*' }, cortex: { bundle: { patch: './cordis.patch.yml' } },
  })
  write(root, guiPatch, [{ insert: [nativeRow()] }])
  write(root, profile, `export const PROFILE_TEMPLATES = { web: { bundles: ['${base}', '${gui}'] } }\n`
    + `export const DEFAULT_PROFILE_BUNDLES = ['${base}']\n`)
  return root
}

function write(root: string, path: string, value: unknown): void {
  const target = join(root, path)
  mkdirSync(dirname(target), { recursive: true })
  writeFileSync(target, typeof value === 'string' ? value : `${JSON.stringify(value)}\n`)
}

function manifest(root: string, path: string, fields: Record<string, unknown>): void {
  const existing = JSON.parse(readFileSync(join(root, path), 'utf8')) as Record<string, unknown>
  write(root, path, { ...existing, ...fields })
}

function fixture(): string {
  const root = mkdtempSync(join(tmpdir(), 'cortex.default-isolation-'))
  roots.push(root)
  write(root, 'apps/cli/package.json', { name: '@cortex/cortex', dependencies: { [core]: 'workspace:^' } })
  write(root, 'apps/cli/src/bin.ts', 'export {}\n')
  write(root, 'apps/web/package.json', { name: '@cortex/web-frontend' })
  write(root, 'apps/web/index.html', '<script type="module" src="/src/main.ts"></script>')
  write(root, 'apps/web/src/main.ts', 'export {}\n')
  write(root, 'python/sdk-runtime/package.json', { name: '@cortex/python-runtime' })
  write(root, 'packages/core/core/package.json', { name: core })
  write(root, 'packages/core/core/src/index.ts', 'export {}\n')
  write(root, 'packages/bundle/base/package.json', { name: base, cortex: { bundle: { patch: './cordis.patch.yml' } } })
  write(root, patch, [{ insert: [{ name: core }] }])
  write(root, preset, [{ insert: [{ name: '@cortex/agent-preset', config: { id: 'standard', plugins: [{ name: core }] } }] }])
  write(root, 'packages/preset/agent-preset/package.json', { name: '@cortex/agent-preset' })
  write(root, profile, `export const PROFILE_TEMPLATES = { web: { bundles: ['${base}'] } }\n`
    + `export const DEFAULT_PROFILE_BUNDLES = ['${base}']\n`)
  write(root, 'packages/experimental/prototype/package.json', { name: experimental })
  return root
}

afterEach(() => {
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true })
})

describe('default product isolation', () => {
  it('admits the Windows GUI native provider and scans its local sources', () => {
    const root = guiFixture()
    expect(verifyDefaultProductIsolation(root)).toMatchObject({ failures: [], packageCount: 8 })
  })

  it.each([
    null, false, true, { __jsExpr: 'false' }, { __jsExpr: "process.platform !== 'darwin'" },
    { __jsExpr: "process.platform != 'win32'" }, { __jsExpr: "process.platform !== 'win32' && false" },
    { __jsExpr: "process.platform !== 'win32'" }, { __jsExpr: guiDisabled },
    { __jsExpr: `process.platform !== 'win32' && ${guiDisabled}` },
  ])('rejects a native provider with guard %j', (disabled) => {
    const root = guiFixture()
    write(root, guiPatch, [{ insert: [nativeRow(disabled)] }])
    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(native)
  })

  it('rejects a native provider whose Windows guard is removed', () => {
    const root = guiFixture()
    const row = nativeRow()
    delete row.disabled
    write(root, guiPatch, [{ insert: [row] }])
    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(native)
  })

  it('checks the effective guard after a later override', () => {
    const root = guiFixture()
    write(root, guiPatch, [{ insert: [nativeRow()] }, { id: 'computer-use-cua-driver-native', disabled: false }])
    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(`${profile} -> ${native}`)
  })

  it.each([patch, preset])('rejects a guarded native provider authored in %s', (file) => {
    const root = guiFixture()
    write(root, guiPatch, [])
    write(root, file, [{ insert: [nativeRow()] }])
    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(`${file} -> ${native}`)
    if (file === patch) {
      expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(`${profile} -> ${native}`)
    }
  })

  it('rejects the guarded native provider nested inside a GUI agent preset', () => {
    const root = guiFixture()
    write(root, guiPatch, [{ insert: [{ name: '@cortex/agent-preset', config: { id: 'native', plugins: [nativeRow()] } }] }])
    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(`${profile} -> ${native}`)
  })

  it('requires the named GUI dependency and its composed guarded row together', () => {
    const root = guiFixture()
    manifest(root, 'packages/bundle/web-app/package.json', { dependencies: {} })
    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(native)
    manifest(root, 'packages/bundle/web-app/package.json', { dependencies: { [native]: 'workspace:*' } })
    write(root, guiPatch, [])
    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(`${gui} dependencies -> ${native}`)
  })

  it.each(['dependencies', 'optionalDependencies', 'peerDependencies'])(
    'rejects the native provider as a non-GUI %s', (section) => {
      const root = guiFixture()
      manifest(root, 'packages/core/core/package.json', { [section]: { [native]: 'workspace:*' } })
      expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(`${core} ${section} -> ${native}`)
    },
  )

  it.each(['optionalDependencies', 'peerDependencies'])(
    'rejects the native provider as a GUI %s', (section) => {
      const root = guiFixture()
      manifest(root, 'packages/bundle/web-app/package.json', { [section]: { [native]: 'workspace:*' } })
      expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(`${gui} ${section} -> ${native}`)
    },
  )

  it.each([
    `import '${native}'`, `await import('${native}/client')`,
    "import '../../../experimental/computer-use-cua-driver-native/src/index.ts'",
  ])('rejects the GUI bundle runtime import %s', (source) => {
    const root = guiFixture()
    write(root, 'packages/bundle/web-app/src/index.ts', source)
    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(`packages/bundle/web-app/src/index.ts -> ${native}`)
  })

  it('continues through the native provider dependency graph', () => {
    const root = guiFixture()
    manifest(root, `${nativeDirectory}/package.json`, { dependencies: { [experimental]: 'workspace:*' } })
    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(`${native} dependencies -> ${experimental}`)
  })

  it('continues through the native provider runtime sources', () => {
    const root = guiFixture()
    write(root, `${nativeDirectory}/src/helper.ts`, `import '${experimental}'\n`)
    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(`${nativeDirectory}/src/helper.ts -> ${experimental}`)
  })

  it('admits the GUI browser provider and its internal lifecycle library', () => {
    const root = browserFixture()
    expect(verifyDefaultProductIsolation(root)).toMatchObject({ failures: [], packageCount: 9 })
  })

  it.each([
    undefined, false, true, { __jsExpr: 'false' }, { __jsExpr: "process.platform !== 'win32'" },
    { __jsExpr: "!['web', 'desktop', 'headless'].includes(ctx.get('profileContext')?.name ?? '')" },
    { __jsExpr: "!['web', 'desktop'].includes(ctx.get('profileContext')?.name ?? 'web')" },
    { __jsExpr: `${guiDisabled} && false` },
  ])('rejects a browser provider with guard %j', (disabled) => {
    const root = browserFixture()
    write(root, guiPatch, [{ insert: [{ ...browserRow(), disabled }] }])
    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(browser)
  })

  it.each([
    undefined, {}, { mode: 'launch' }, { mode: 'launch', headless: false },
    { mode: 'attach', headless: true, endpoint: 'http://localhost:9222' },
    { mode: 'launch', headless: true, executablePath: 'custom-browser' },
  ])('rejects a browser provider with launch settings %j', (config) => {
    const root = browserFixture()
    write(root, guiPatch, [{ insert: [{ ...browserRow(), config }] }])
    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(browser)
  })

  it.each([
    { id: 'wrong-browser-id' }, { name: '@cortex/experimental-browser-use-stagehand' },
    { name: browserRuntime }, { name: `${browser}/client` },
  ])('rejects another browser provider identity %j', (fields) => {
    const root = browserFixture()
    write(root, guiPatch, [{ insert: [{ ...browserRow(), ...fields }] }])
    expect(verifyDefaultProductIsolation(root).failures.length).toBeGreaterThan(0)
  })

  it.each([patch, preset])('rejects a guarded browser provider authored in %s', (file) => {
    const root = browserFixture()
    write(root, guiPatch, [])
    write(root, file, [{ insert: [{ ...browserRow(), __productIsolationSource: guiPatch }] }])
    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(`${file} -> ${browser}`)
    if (file === patch) expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(`${profile} -> ${browser}`)
  })

  it('rejects the browser provider nested in a GUI agent preset', () => {
    const root = browserFixture()
    write(root, guiPatch, [{ insert: [{ name: '@cortex/agent-preset', config: { id: 'browser', plugins: [browserRow()] } }] }])
    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(`${profile} -> ${browser}`)
  })

  it.each([
    { id: 'browser-use-playwright-mcp', disabled: false },
    { id: 'browser-use-playwright-mcp', config: { mode: 'attach', endpoint: 'http://localhost:9222' } },
    { id: 'computer-use-cua-driver-native', disabled: { __jsExpr: "process.platform !== 'win32'" } },
  ])('rejects shipped non-GUI overrides of the provider guard or mode: %j', (override) => {
    const root = browserFixture()
    const headless = '@cortex/headless'
    write(root, 'packages/bundle/headless/package.json', { name: headless, cortex: { bundle: { patch: './cordis.patch.yml' } } })
    write(root, 'packages/bundle/headless/cordis.patch.yml', [override])
    write(root, profile, readFileSync(join(root, profile), 'utf8')
      + `export const INSTALLATION_OWNED_PROFILE_TUPLES = { headless: ['${base}', '${gui}', '${headless}'] }\n`)
    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain('GUI provider override must preserve its guarded host configuration')
  })

  it('checks the composed browser guard after a later override', () => {
    const root = browserFixture()
    write(root, guiPatch, [{ insert: [browserRow()] }, { id: 'browser-use-playwright-mcp', disabled: false }])
    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(`${profile} -> ${browser}`)
  })

  it('requires the browser dependency and its composed guarded row together', () => {
    const root = browserFixture()
    manifest(root, 'packages/bundle/web-app/package.json', { dependencies: {} })
    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(browser)
    manifest(root, 'packages/bundle/web-app/package.json', { dependencies: { [browser]: 'workspace:*' } })
    write(root, guiPatch, [])
    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(`${gui} dependencies -> ${browser}`)
  })

  it.each([browser, browserRuntime])('rejects stable imports and dependencies on %s', (name) => {
    const root = browserFixture()
    manifest(root, 'packages/core/core/package.json', { dependencies: { [name]: 'workspace:*' } })
    write(root, 'packages/bundle/web-app/src/index.ts', `import '${name}/mcp'\n`)
    const failures = verifyDefaultProductIsolation(root).failures.join('\n')
    expect(failures).toContain(`${core} dependencies -> ${name}`)
    expect(failures).toContain(`packages/bundle/web-app/src/index.ts -> ${name}/mcp`)
  })

  it('rejects a direct GUI dependency on the internal browser runtime', () => {
    const root = browserFixture()
    manifest(root, 'packages/bundle/web-app/package.json', { dependencies: { [browser]: 'workspace:*', [browserRuntime]: 'workspace:*' } })
    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(`${gui} dependencies -> ${browserRuntime}`)
  })

  it.each([browserDirectory, browserRuntimeDirectory])('continues through the %s dependency graph and sources', (directory) => {
    const root = browserFixture()
    const name = directory === browserDirectory ? browser : browserRuntime
    manifest(root, `${directory}/package.json`, { dependencies: { [experimental]: 'workspace:*' } })
    write(root, `${directory}/src/extra.ts`, `import '${experimental}'\n`)
    const failures = verifyDefaultProductIsolation(root).failures.join('\n')
    expect(failures).toContain(`${name} dependencies -> ${experimental}`)
    expect(failures).toContain(`${directory}/src/extra.ts -> ${experimental}`)
  })

  it('rejects aliases from an admitted browser dependency to another experimental package', () => {
    const root = browserFixture()
    manifest(root, `${browserDirectory}/package.json`, { dependencies: { [browserRuntime]: `npm:${experimental}@1.0.0` } })
    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(experimental)
  })

  it('requires the exact source directory for the admitted browser runtime', () => {
    const root = browserFixture()
    manifest(root, `${browserRuntimeDirectory}/package.json`, { name: '@fixture/renamed-runtime' })
    write(root, 'packages/experimental/other-runtime/package.json', { name: browserRuntime })
    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(`${browser} dependencies -> ${browserRuntime}`)
  })

  it.each(['@deepseek-ai/libreoffice-kit'])(
    'accepts independently published %s but rejects unknown workspace packages', (name) => {
      const root = fixture()
      const file = 'packages/core/core/package.json'
      write(root, 'packages/core/core/src/index.ts', `import '${name}'\n`)
      manifest(root, file, { dependencies: { [name]: '0.0.1' } })
      expect(verifyDefaultProductIsolation(root).failures).toEqual([])
      manifest(root, file, { dependencies: { [`${name}-unknown`]: '0.0.1' } })
      expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain('unknown workspace package')
    },
  )

  it.each(['dependency', 'runtime import'])('rejects an unknown Cortex %s', (kind) => {
    const root = fixture()
    const name = '@cortex/missing-workspace'
    if (kind === 'dependency') manifest(root, 'packages/core/core/package.json', { dependencies: { [name]: 'workspace:*' } })
    else write(root, 'packages/core/core/src/index.ts', `import '${name}/entry'\n`)
    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(`unknown workspace package ${name}`)
  })

  it.each(['wasm', 'darwin-arm64', 'darwin-x64', 'win32-arm64', 'win32-x64'])(
    'rejects a direct dependency on the %s engine', (engine) => {
      const root = fixture()
      const name = `@deepseek-ai/libreoffice-kit-${engine}`
      manifest(root, 'packages/core/core/package.json', { dependencies: { [name]: '0.0.1' } })
      expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(`unknown workspace package ${name}`)
    },
  )

  it('allows development dependencies, type imports, and separate experimental preview entries', () => {
    const root = fixture()
    manifest(root, 'apps/cli/package.json', { devDependencies: { [experimental]: 'workspace:^' } })
    write(root, 'apps/cli/src/bin.ts', `import type { Options } from '${experimental}'\nexport type { Options }\n`)
    write(root, 'apps/web/src/preview.ts', `import '${experimental}'\n`)
    write(root, 'packages/experimental/prototype/cordis.patch.yml', [{ name: experimental }])

    expect(verifyDefaultProductIsolation(root)).toMatchObject({ failures: [], packageCount: 6, configCount: 2 })
  })

  it('ignores dependency trees and directories whose names end in a source extension', () => {
    const root = fixture()
    mkdirSync(join(root, 'python/sdk-runtime/src/vendor.js'), { recursive: true })
    write(root, 'python/sdk-runtime/src/node_modules/vendor/index.js', `import '${experimental}'\n`)

    expect(verifyDefaultProductIsolation(root).failures).toEqual([])
  })

  it('ships an optional bundle switched off: its graph is outside the product, its name stays out of imports and defaults', () => {
    const root = fixture()
    const layer = '@cortex/experimental-layer'
    write(root, 'packages/experimental/layer/package.json', {
      name: layer, icon: './icon.svg', exports: { './locale/*.json': './locale/*.json' },
      dependencies: { [experimental]: 'workspace:^' }, cortex: { bundle: { patch: './cordis.patch.yml' } },
    })
    write(root, 'packages/experimental/layer/cordis.patch.yml', [{ insert: [{ name: experimental }] }])
    manifest(root, 'apps/cli/package.json', { dependencies: { [core]: 'workspace:^', [layer]: 'workspace:^' } })
    write(root, profile, `export const PROFILE_TEMPLATES = { web: { bundles: ['${base}'] } }\n`
      + `export const DEFAULT_PROFILE_BUNDLES = ['${base}']\n`
      + `export const OPTIONAL_BUNDLES = ['${layer}']\n`)
    expect(verifyDefaultProductIsolation(root)).toMatchObject({ failures: [], packageCount: 6 })

    // The exception covers the dependency edge alone: a runtime import or a default template still names the product.
    write(root, 'apps/cli/src/bin.ts', `import '${layer}'\n`)
    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(`apps/cli/src/bin.ts -> ${layer}`)
    write(root, 'apps/cli/src/bin.ts', 'export {}\n')
    write(root, profile, `export const PROFILE_TEMPLATES = { web: { bundles: ['${base}', '${layer}'] } }\n`
      + `export const DEFAULT_PROFILE_BUNDLES = ['${base}']\n`
      + `export const OPTIONAL_BUNDLES = ['${layer}']\n`)
    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(`optional bundle ${layer} must not be a default bundle`)
  })

  it('requires each optional bundle to be a runtime dependency that declares a bundle patch, an icon, and locale metadata', () => {
    const root = fixture()
    write(root, profile, `export const PROFILE_TEMPLATES = { web: { bundles: ['${base}'] } }\n`
      + `export const DEFAULT_PROFILE_BUNDLES = ['${base}']\n`
      + `export const OPTIONAL_BUNDLES = ['${experimental}']\n`)
    const failures = verifyDefaultProductIsolation(root).failures.join('\n')
    expect(failures).toContain(`optional bundle ${experimental} must be a runtime dependency of apps/cli`)
    expect(failures).toContain(`optional bundle ${experimental} must declare cortex.bundle.patch`)
    expect(failures).toContain(`optional bundle ${experimental} must declare an icon`)
    expect(failures).toContain(`optional bundle ${experimental} must export ./locale/*.json display metadata`)

    // An experimental runtime dependency the list does not name is still a product requirement.
    write(root, profile, `export const PROFILE_TEMPLATES = { web: { bundles: ['${base}'] } }\n`
      + `export const DEFAULT_PROFILE_BUNDLES = ['${base}']\n`)
    manifest(root, 'apps/cli/package.json', { dependencies: { [core]: 'workspace:^', [experimental]: 'workspace:^' } })
    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(`@cortex/cortex dependencies -> ${experimental}`)
  })

  it.each(['dependencies', 'optionalDependencies', 'peerDependencies'])(
    'rejects transitive experimental %s',
    (section) => {
      const root = fixture()
      manifest(root, 'packages/core/core/package.json', { [section]: { [experimental]: 'workspace:^' } })

      expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(`${core} ${section} -> ${experimental}`)
    },
  )

  it.each([
    `npm:${experimental}@1.0.0`, `workspace:${experimental}@*`,
    'file:../../experimental/prototype', 'link:../../experimental/prototype',
    'workspace:../../experimental/prototype',
  ])('rejects a safe-looking dependency alias targeting %s', (range) => {
    const root = fixture()
    manifest(root, 'packages/core/core/package.json', { dependencies: { safe: range } })

    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(experimental)
  })

  it('rejects experimental names absent from the inventory and paths with another package name', () => {
    const root = fixture()
    manifest(root, 'apps/cli/package.json', { dependencies: { '@cortex/experimental-missing': '*' } })
    manifest(root, 'packages/experimental/prototype/package.json', { name: '@fixture/innocent' })
    manifest(root, 'python/sdk-runtime/package.json', { dependencies: { '@fixture/innocent': '*' } })

    const failures = verifyDefaultProductIsolation(root).failures.join('\n')
    expect(failures).toContain('@cortex/experimental-missing')
    expect(failures).toContain('@fixture/innocent')
  })

  it('follows private application intermediaries and terminates cycles', () => {
    const root = fixture()
    write(root, 'apps/desktop/package.json', { name: '@fixture/desktop', private: true,
      dependencies: { [core]: '*', [experimental]: '*' } })
    manifest(root, 'packages/core/core/package.json', { peerDependencies: { '@fixture/desktop': '*' } })

    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(experimental)
  })

  it.each([
    `import '${experimental}'`,
    `export * from '${experimental}'`,
    `await import('${experimental}/client')`,
    `const x = require('${experimental}')`,
    'import \'./../../../packages/experimental/prototype/src/index.ts\'',
  ])('rejects runtime source reference %s', (source) => {
    const root = fixture()
    write(root, 'apps/cli/src/bin.ts', source)
    write(root, 'packages/experimental/prototype/src/index.ts', 'export {}\n')

    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(experimental)
  })

  it.each([
    "import './preview.ts'", "import './preview.ts?worker'", "new Worker(new URL('./preview.ts', import.meta.url))",
  ])('rejects default Web entry reaching preview via %s', (source) => {
    const root = fixture()
    write(root, 'apps/web/src/main.ts', source)
    write(root, 'apps/web/src/preview.ts', `import '${experimental}'\n`)

    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(experimental)
  })

  it.each([
    "import '/src/preview.ts'",
    "export * from '/src/preview.ts'",
    "await import('/src/preview.ts')",
    "new Worker(new URL('/src/preview.ts', import.meta.url))",
  ])('follows Web-root runtime reference %s', (source) => {
    const root = fixture()
    write(root, 'apps/web/src/main.ts', source)
    write(root, 'apps/web/src/preview.ts', `import '${experimental}'\n`)

    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(experimental)
  })

  it('does not follow type-only Web-root references', () => {
    const root = fixture()
    write(root, 'apps/web/src/main.ts', "import type { Value } from '/src/preview.ts'\nexport type { Value } from '/src/preview.ts'\n")
    write(root, 'apps/web/src/preview.ts', `import '${experimental}'\n`)

    expect(verifyDefaultProductIsolation(root).failures).toEqual([])
  })

  it.each(['.js', '.jsx', '.mjs', '.ts', '.tsx', '.mts'])('follows extensionless Web imports to %s in a dotted checkout', (extension) => {
    const root = fixture()
    write(root, 'apps/web/src/main.ts', "import './preview'\n")
    write(root, `apps/web/src/preview${extension}`, `import '${experimental}'\n`)

    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(experimental)
  })

  it.each(['.js', '.jsx', '.mjs'])('follows Web directory imports to index%s', (extension) => {
    const root = fixture()
    write(root, 'apps/web/src/main.ts', "import './preview'\n")
    write(root, `apps/web/src/preview/index${extension}`, `import '${experimental}'\n`)

    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(experimental)
  })

  it.each([['.mjs', '.mts'], ['.cjs', '.cts']])('follows %s runtime requests to their %s source', (request, source) => {
    const root = fixture()
    write(root, 'apps/web/src/main.ts', `import './preview${request}'\n`)
    write(root, `apps/web/src/preview${source}`, `import '${experimental}'\n`)

    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(experimental)
  })

  it('follows a Web directory import whose directory name contains a dot', () => {
    const root = fixture()
    write(root, 'apps/web/src/main.ts', "import './preview.v1'\n")
    write(root, 'apps/web/src/preview.v1/index.js', `import '${experimental}'\n`)

    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(experimental)
  })

  it('identifies a relative package-directory import using its exact directory owner', () => {
    const root = fixture()
    write(root, 'apps/web/src/main.ts', "import '../../../packages/experimental/prototype'\n")
    manifest(root, 'packages/experimental/prototype/package.json', { main: 'src/entry.js' })
    write(root, 'packages/experimental/prototype/src/entry.js', 'export {}\n')

    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(experimental)
  })

  it.each([
    [{ name: experimental, disabled: true }],
    [{ group: true, config: [{ name: experimental }] }],
    [{ insert: [{ name: experimental }] }],
    [{ name: '@cortex/cordis-plugin-group', config: [{ name: experimental }] }],
    [{ name: '@cortex/cordis-plugin-include', config: { patches: [{ insert: [{ name: experimental }] }] } }],
  ].map(entries => ({ entries })))('rejects experimental plugin rows in $entries', ({ entries }) => {
    const root = fixture()
    write(root, patch, entries)

    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(experimental)
  })

  it('follows Include files while ignoring ordinary plugin config data', () => {
    const root = fixture()
    write(root, patch, [{ insert: [{ name: core, config: { name: experimental, insert: [{ name: experimental }] } }] }])
    expect(verifyDefaultProductIsolation(root).failures).toEqual([])
    write(root, patch, [{ name: '@cortex/cordis-plugin-include', config: { path: './nested.yml' } }])
    write(root, 'packages/bundle/base/nested.yml', [{ name: experimental }])
    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(experimental)
  })

  it.each([false, true])('checks Include.initial when its file exists: %s', (existing) => {
    const root = fixture()
    const included = join(root, 'included.yml')
    if (existing) write(root, 'included.yml', [{ name: core }])
    write(root, patch, [{ insert: [{ name: 'cordis:include', config: {
      path: pathToFileURL(included).href,
      initial: [{ name: experimental }],
    } }] }])

    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(experimental)
  })

  it('accepts a missing Include file initialized with stable entries and applies its patches', () => {
    const root = fixture()
    write(root, patch, [{ insert: [{ name: 'cordis:include', config: {
      path: pathToFileURL(join(root, 'included.yml')).href,
      initial: [{ id: 'feature', group: true, config: [{ name: core }] }],
    } }] }])
    expect(verifyDefaultProductIsolation(root).failures).toEqual([])
    write(root, patch, [{ insert: [{ name: 'cordis:include', config: {
      path: pathToFileURL(join(root, 'included.yml')).href,
      initial: [{ id: 'feature', group: true, config: [{ name: core }] }],
      patches: [{ id: 'feature', config: [{ name: experimental }] }],
    } }] }])
    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(experimental)
  })

  it('rejects an Include.initial tree that includes its own generated file', () => {
    const root = fixture()
    const path = pathToFileURL(join(root, 'included.yml')).href
    write(root, patch, [{ insert: [{ name: 'cordis:include', config: {
      path, initial: [{ name: 'cordis:include', config: { path } }],
    } }] }])

    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain('cyclic Include path included.yml')
  })

  it('checks default profile bundle names and declared config trees', () => {
    const root = fixture()
    write(root, profile, `export const PROFILE_TEMPLATES = { web: { bundles: ['${experimental}'] } }\n`
      + `export const DEFAULT_PROFILE_BUNDLES = ['${base}']\n`)
    manifest(root, 'apps/cli/package.json', { cortex: { configTrees: [{ path: './config' }] } })
    write(root, 'apps/cli/config/extra.cordis.yml', [{ name: experimental }])
    const failures = verifyDefaultProductIsolation(root).failures.join('\n')
    expect(failures).toContain(`${profile} -> ${experimental}`)
    expect(failures).toContain('apps/cli/config/extra.cordis.yml')
  })

  it.each([false, true])('rejects a declared config tree without composition files when its directory exists: %s', (existing) => {
    const root = fixture()
    manifest(root, 'apps/cli/package.json', { cortex: { configTrees: [{ path: './config' }] } })
    if (existing) write(root, 'apps/cli/config/README.i18n.yaml', 'en: test\n')

    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain('apps/cli/config')
  })

  it('does not classify ordinary YAML as a Cordis config because of an ancestor directory name', () => {
    const root = fixture()
    write(root, 'packages/core/core/src/cordis/index.ts', "new URL('./ordinary.yml', import.meta.url)\n")
    write(root, 'packages/core/core/src/cordis/ordinary.yml', 'ordinary: data\n')

    expect(verifyDefaultProductIsolation(root).failures).toEqual([])
  })

  it('checks desktop configuration reached through a source URL', () => {
    const root = fixture()
    write(root, 'apps/desktop-host/package.json', { name: '@fixture/desktop-host', private: true })
    write(root, 'apps/desktop-host/src/index.ts', "new URL('../config/desktop.cordis.patch.yml', import.meta.url)")
    write(root, 'apps/desktop-host/config/desktop.cordis.patch.yml', [{ insert: [{ name: experimental }] }])

    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(experimental)
  })

  it('checks group contents after an id-only patch changes the composed Web tree', () => {
    const root = fixture()
    const web = '@cortex/web-app'
    write(root, 'packages/bundle/web-app/package.json', { name: web, cortex: { bundle: { patch: './cordis.patch.yml' } } })
    write(root, patch, [{ insert: [{ id: 'feature-group', group: true, config: [{ name: core }] }] }])
    write(root, 'packages/bundle/web-app/cordis.patch.yml', [
      { id: 'feature-group', config: [{ name: experimental }] },
    ])
    write(root, profile, `export const PROFILE_TEMPLATES = { web: { bundles: ['${base}', '${web}'] } }\n`
      + `export const DEFAULT_PROFILE_BUNDLES = ['${base}']\n`)

    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(`${profile} -> ${experimental}`)
  })

  it('composes Include patches before checking their effective plugin rows', () => {
    const root = fixture()
    const included = join(root, 'included tree', 'cordis.yml')
    write(root, 'included tree/cordis.yml', [{ id: 'feature-group', group: true, config: [{ name: core }] }])
    write(root, patch, [{ insert: [{ name: 'cordis:include', config: {
      path: pathToFileURL(included).href,
      patches: [{ id: 'feature-group', config: [{ name: experimental }] }],
    } }] }])

    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(`included tree/cordis.yml -> ${experimental}`)
  })

  it('checks the replacement Include path after an id-only Web patch', () => {
    const root = fixture()
    write(root, 'original.yml', [{ name: core }])
    write(root, 'replacement.yml', [{ name: experimental }])
    write(root, patch, [
      { insert: [{ id: 'feature-include', name: 'cordis:include', config: { path: pathToFileURL(join(root, 'original.yml')).href } }] },
      { id: 'feature-include', config: { path: pathToFileURL(join(root, 'replacement.yml')).href } },
    ])

    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(`replacement.yml -> ${experimental}`)
  })

  it('reports an empty effective Web tree even when the raw patch names a package', () => {
    const root = fixture()
    write(root, patch, [{ id: 'missing-target', name: core }])

    expect(verifyDefaultProductIsolation(root).failures).toContain(`${profile}: composed Web profile contains no plugins`)
  })

  it('rejects a file-URL plugin name anchored by the real patch loader', () => {
    const root = fixture()
    write(root, patch, [{ insert: [{ name: '../../experimental/prototype/src/index.ts' }] }])
    write(root, 'packages/experimental/prototype/src/index.ts', 'export {}\n')

    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(`${profile} -> ${experimental}`)
  })

  it('checks installation-owned profile tuples as well as new-profile defaults', () => {
    const root = fixture()
    const source = readFileSync(join(root, profile), 'utf8')
    write(root, profile, `${source}\nconst INSTALLATION_OWNED_PROFILE_TUPLES = { headless: ['${experimental}'] }\n`)

    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(experimental)
  })

  it('checks inline modules alongside the default Web script', () => {
    const root = fixture()
    write(root, 'apps/web/index.html', '<script type="module" src="/src/main.ts"></script>'
      + `<script type="module">import '${experimental}'</script>`)

    expect(verifyDefaultProductIsolation(root).failures.join('\n')).toContain(experimental)
  })

  it.each(['apps/cli/package.json', 'apps/web/index.html', 'python/sdk-runtime/package.json', preset])(
    'fails closed when required input %s is missing',
    (path) => {
      const root = fixture()
      rmSync(join(root, path))
      expect(verifyDefaultProductIsolation(root).failures.length).toBeGreaterThan(0)
    },
  )

  it.each([
    '',
    'export const PROFILE_TEMPLATES = {}; export const DEFAULT_PROFILE_BUNDLES = []',
    'export const PROFILE_TEMPLATES = computedProfiles(); export const DEFAULT_PROFILE_BUNDLES = []',
  ])('rejects absent, empty, or uninspectable default profile definitions', (source) => {
    const root = fixture()
    write(root, profile, source)
    expect(() => verifyDefaultProductIsolation(root)).toThrow('profile')
  })
})
