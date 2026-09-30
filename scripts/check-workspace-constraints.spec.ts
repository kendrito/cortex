/** Workspace dependency ranges and package publication constraints. */

import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { describe, expect, it, onTestFinished } from 'vitest'
import {
  isPublicExperimentalPackageDirectory,
  PRIVATE_EXPERIMENTAL_PACKAGE_DIRECTORIES,
} from './experimental-package-policy.ts'
import {
  checkCortexFamilyVersion,
  checkWorkspaceManifest,
  checkWorkspaceProtocol,
  checkExperimentalDependencyIsolation,
  checkExperimentalManifest,
  expectedCortexPackageFiles,
  readWorkspaceManifests,
  type WorkspaceManifest,
} from './check-workspace-constraints.ts'

const experimental = {
  dir: 'packages/experimental/prototype',
  manifest: {
    name: '@cortex/experimental-prototype',
    publishConfig: { access: 'public' },
  },
} satisfies WorkspaceManifest

describe('workspace dependency ranges', () => {
  const dependency = { dir: 'packages/core/runtime', manifest: { name: '@cortex/runtime' } }
  const cli = { dir: 'apps/cli', manifest: { name: '@cortex/cortex' } }
  const vendor = { dir: 'vendor/cordis', manifest: { name: '@cortex/cordis' } }
  const native = { dir: 'native/system', manifest: { name: '@deepseek-ai/node-addon-system' } }
  const platform = { dir: 'native/system/packages/darwin-arm64', manifest: { name: '@deepseek-ai/node-addon-system-darwin-arm64' } }
  const unrelated = { dir: 'tools/helper', manifest: { name: '@other/helper' } }

  describe.each([
    '.', 'packages/core/probe', 'packages/experimental/probe', 'apps/cli', 'apps/web',
    'apps/desktop', 'apps/desktop-host', 'benchmarks', 'website', 'python/sdk-runtime', 'tools/probe',
    'vendor/loader', 'native/system', 'native/system/packages/entry',
  ])('consumer %s', (dir) => {
    it.each(['dependencies', 'devDependencies', 'peerDependencies', 'optionalDependencies'] as const)(
      'requires exact CORTEX and tilde vendor/native %s independently of the consumer name',
      (section) => {
        const consumer = (name: string, range: string): WorkspaceManifest => ({
          dir, manifest: { name: 'consumer', [section]: { [name]: range } },
        })
        const check = (name: string, range: string): string[] =>
          checkWorkspaceProtocol([dependency, cli, vendor, native, platform, unrelated, consumer(name, range)])
        for (const name of ['@cortex/cortex', '@cortex/runtime']) {
          expect(check(name, 'workspace:*')).toEqual([])
          for (const range of ['workspace:^', 'workspace:~', 'workspace:^0.1.7', '^0.1.7', '*']) {
            expect(check(name, range)).toEqual([
              `consumer: ${section}.${name} must use workspace:*, got ${range}`,
            ])
          }
        }
        for (const name of ['@cortex/cordis', '@deepseek-ai/node-addon-system', '@deepseek-ai/node-addon-system-darwin-arm64']) {
          expect(check(name, 'workspace:~')).toEqual([])
          for (const range of ['workspace:*', 'workspace:^', '^4.0.3', '~4.0.3']) {
            expect(check(name, range)).toEqual([
              `consumer: ${section}.${name} must use workspace:~, got ${range}`,
            ])
          }
        }
        for (const range of ['workspace:*', 'workspace:^']) {
          expect(check('@other/helper', range)).toEqual([])
        }
        expect(check('@other/helper', '^0.1.0')).toEqual([
          `consumer: ${section}.@other/helper must use the workspace: protocol, got ^0.1.0`,
        ])
        expect(check('external', '^1.2.3')).toEqual([])
      },
    )
  })
})

describe('workspace manifest discovery', () => {
  it('checks root, app, runtime, tooling, and newly declared members while honoring exclusions', () => {
    const root = mkdtempSync(join(tmpdir(), 'cortex-workspace-ranges-'))
    onTestFinished(() => { rmSync(root, { recursive: true, force: true }) })
    const consumers = ['.', 'apps/cli', 'apps/web', 'apps/desktop', 'apps/desktop-host', 'benchmarks', 'website', 'python/sdk-runtime', 'tools/probe']
    writeFileSync(join(root, 'pnpm-workspace.yaml'), [
      'packages:', '  - packages/*/*', '  - apps/*', '  - apps/cli', '  - benchmarks',
      '  - website', '  - python/sdk-runtime', '  - tools/*', '  - "!tools/excluded"',
    ].join('\n'))
    for (const dir of [...consumers, 'tools/excluded', 'unlisted/probe', 'packages/core/runtime']) {
      mkdirSync(join(root, dir), { recursive: true })
      writeFileSync(join(root, dir, 'package.json'), JSON.stringify(dir === 'packages/core/runtime'
        ? { name: '@cortex/runtime' }
        : { dependencies: { '@cortex/runtime': 'workspace:^' } }))
    }
    const manifests = readWorkspaceManifests(root)
    expect(manifests.map(entry => entry.dir).sort()).toEqual([...consumers, 'packages/core/runtime'].sort())
    expect(checkWorkspaceProtocol(manifests).sort()).toEqual(consumers.map(dir =>
      `${dir}: dependencies.@cortex/runtime must use workspace:*, got workspace:^`).sort())
  })

  it.each(['', 'null', '{}', 'packages: []', 'packages: [false]', 'packages: [""]', 'packages: wrong'])(
    'rejects an invalid workspace declaration: %s', (contents) => {
      const root = mkdtempSync(join(tmpdir(), 'cortex-workspace-ranges-'))
      onTestFinished(() => { rmSync(root, { recursive: true, force: true }) })
      writeFileSync(join(root, 'pnpm-workspace.yaml'), contents)
      expect(() => readWorkspaceManifests(root)).toThrow('packages must be a non-empty list of workspace patterns')
    },
  )

  it('rejects a declaration that matches no workspace members', () => {
    const root = mkdtempSync(join(tmpdir(), 'cortex-workspace-ranges-'))
    onTestFinished(() => { rmSync(root, { recursive: true, force: true }) })
    writeFileSync(join(root, 'pnpm-workspace.yaml'), 'packages: [missing/*]')
    expect(() => readWorkspaceManifests(root)).toThrow('packages matched no workspace manifests')
  })
})

describe('experimental workspace constraints', () => {
  it.each([
    ['native', '@cortex/experimental-computer-use-cua-driver-native', 'packages/experimental/computer-use-cua-driver-native'],
    ['browser', '@cortex/experimental-browser-use-playwright-mcp', 'packages/experimental/browser-use-playwright-mcp'],
  ])('admits only the named %s dependency of the GUI bundle', (_label, native, directory) => {
    const provider = { dir: directory, manifest: { name: native } }
    const gui = { dir: 'packages/bundle/web-app', manifest: { name: '@cortex/web-app', dependencies: { [native]: 'workspace:*' } } }
    expect(checkExperimentalDependencyIsolation([provider, gui])).toEqual([])
    for (const section of ['peerDependencies', 'optionalDependencies']) {
      const consumer = { ...gui, manifest: { name: gui.manifest.name, [section]: { [native]: 'workspace:*' } } }
      expect(checkExperimentalDependencyIsolation([provider, consumer])).toEqual([
        `${gui.manifest.name}: ${section}.${native} must not reference an experimental package`,
      ])
    }
    for (const consumer of [
      { ...gui, dir: 'packages/bundle/base' },
      { ...gui, dir: 'apps/cli' },
      { ...gui, manifest: { ...gui.manifest, name: '@cortex/renamed-gui' } },
    ]) {
      expect(checkExperimentalDependencyIsolation([provider, consumer])).toEqual([
        `${consumer.manifest.name}: dependencies.${native} must not reference an experimental package`,
      ])
    }
    expect(checkExperimentalDependencyIsolation([experimental, {
      ...gui, manifest: { name: gui.manifest.name, dependencies: { [experimental.manifest.name]: 'workspace:*' } },
    }])).toEqual([
      `${gui.manifest.name}: dependencies.${experimental.manifest.name} must not reference an experimental package`,
    ])
  })

  it('rejects a direct release dependency on the internal browser lifecycle library', () => {
    const name = '@cortex/experimental-browser-use-runtime'
    expect(checkExperimentalDependencyIsolation([
      { dir: 'packages/experimental/browser-use-runtime', manifest: { name } },
      { dir: 'packages/bundle/web-app', manifest: { name: '@cortex/web-app', dependencies: { [name]: 'workspace:*' } } },
    ])).toEqual([`@cortex/web-app: dependencies.${name} must not reference an experimental package`])
  })

  it('requires the experimental package-name prefix', () => {
    expect(checkExperimentalManifest({
      ...experimental,
      manifest: { ...experimental.manifest, name: '@cortex/prototype' },
    })).toEqual([
      '@cortex/prototype: experimental package name must start with "@cortex/experimental-"',
    ])
  })

  it('requires public metadata for unlisted experimental packages', () => {
    expect(checkExperimentalManifest(experimental)).toEqual([])
    expect(checkExperimentalManifest({
      ...experimental,
      manifest: { name: experimental.manifest.name, private: true },
    })).toEqual([
      '@cortex/experimental-prototype: public experimental package must not set "private": true',
      '@cortex/experimental-prototype: public experimental package must set publishConfig.access to "public"',
    ])
  })

  it('requires private metadata for an explicitly excluded prototype', () => {
    const { dir, manifest: { name } } = experimental
    const privateDirectories = [dir]
    expect(isPublicExperimentalPackageDirectory(dir, privateDirectories)).toBe(false)
    expect(checkExperimentalManifest({ dir, manifest: { name, private: true } }, privateDirectories)).toEqual([])
    expect(checkExperimentalManifest(experimental, privateDirectories)).toEqual([
      `${name}: experimental package must set "private": true`,
      `${name}: experimental package must omit publishConfig`,
    ])
  })

  it('keeps the current experimental publication set unrestricted', () => {
    expect(PRIVATE_EXPERIMENTAL_PACKAGE_DIRECTORIES).toEqual([])
  })

  it('limits the public default to experimental package directories', () => {
    expect(isPublicExperimentalPackageDirectory(experimental.dir)).toBe(true)
    for (const dir of [
      'packages/core/session',
      'apps/cli',
      'vendor/cordis',
      'packages/experimental',
      'packages/experimental/prototype/src',
      ...PRIVATE_EXPERIMENTAL_PACKAGE_DIRECTORIES,
    ]) {
      expect(isPublicExperimentalPackageDirectory(dir)).toBe(false)
    }
  })

  it.each(['dependencies', 'optionalDependencies', 'peerDependencies'] as const)(
    'rejects release %s on an experimental package',
    (section) => {
      expect(checkExperimentalDependencyIsolation([experimental, {
        dir: 'packages/core/consumer',
        manifest: {
          name: '@cortex/consumer',
          [section]: { '@cortex/experimental-prototype': 'workspace:^' },
        },
      }])).toEqual([
        `@cortex/consumer: ${section}.@cortex/experimental-prototype must not reference an experimental package`,
      ])
    },
  )

  it('allows the cortex installation to ship the optional bundles the launcher names, and nothing else experimental', () => {
    const listed = { '@cortex/experimental-prototype': 'workspace:^' }
    const installation = { dir: 'apps/cli', manifest: { name: '@cortex/cortex', dependencies: listed } } satisfies WorkspaceManifest
    expect(checkExperimentalDependencyIsolation([experimental, installation], ['@cortex/experimental-prototype'])).toEqual([])
    expect(checkExperimentalDependencyIsolation([experimental, installation], [])).toEqual([
      '@cortex/cortex: dependencies.@cortex/experimental-prototype must not reference an experimental package',
    ])
    // Only a plain dependency edge is offered; a peer would make the bundle a requirement of every consumer.
    expect(checkExperimentalDependencyIsolation([experimental, {
      dir: 'apps/cli',
      manifest: { name: '@cortex/cortex', peerDependencies: listed },
    }], ['@cortex/experimental-prototype'])).toEqual([
      '@cortex/cortex: peerDependencies.@cortex/experimental-prototype must not reference an experimental package',
    ])
  })

  it('allows development and experimental consumers but rejects the Python release runtime', () => {
    const manifests: WorkspaceManifest[] = [experimental, {
      dir: 'packages/core/test-only',
      manifest: {
        name: '@cortex/test-only',
        devDependencies: { '@cortex/experimental-prototype': 'workspace:^' },
      },
    }, {
      dir: 'packages/experimental/consumer',
      manifest: {
        name: '@cortex/experimental-consumer',
        dependencies: { '@cortex/experimental-prototype': 'workspace:^' },
      },
    }, {
      dir: 'python/sdk-runtime',
      manifest: {
        name: '@cortex/python-runtime',
        dependencies: { '@cortex/experimental-prototype': 'workspace:^' },
      },
    }]

    expect(checkExperimentalDependencyIsolation(manifests)).toEqual([
      '@cortex/python-runtime: dependencies.@cortex/experimental-prototype must not reference an experimental package',
    ])
  })
})

describe('cortex family version coherence', () => {
  it('rejects a package carrying a stale shared version', () => {
    expect(checkCortexFamilyVersion(
      { name: '@cortex/http-proxy', version: '0.1.2-alpha.5' },
      '0.1.2-rc.1',
    )).toBe('@cortex/http-proxy: package.json version must match root version 0.1.2-rc.1')
  })

  it('rejects the root-named CLI app on a stale shared version', () => {
    expect(checkCortexFamilyVersion(
      { name: '@cortex/cortex', version: '0.1.2-alpha.5' },
      '0.1.2-rc.1',
    )).toBe('@cortex/cortex: package.json version must match root version 0.1.2-rc.1')
  })

  it('accepts a manifest carrying the shared version', () => {
    expect(checkCortexFamilyVersion(
      { name: '@cortex/http-proxy', version: '0.1.2-rc.1' },
      '0.1.2-rc.1',
    )).toBeUndefined()
  })

  it('leaves other sequences to their own version lines', () => {
    expect(checkCortexFamilyVersion({ name: '@cortex/cordis', version: '4.0.1' }, '0.1.2-rc.1')).toBeUndefined()
    expect(checkCortexFamilyVersion(
      { name: '@deepseek-ai/node-addon-system', version: '0.1.1' },
      '0.1.2-rc.1',
    )).toBeUndefined()
    expect(checkCortexFamilyVersion({ version: '0.1.2-alpha.5' }, '0.1.2-rc.1')).toBeUndefined()
  })
})

describe('package payload constraints', () => {
  it.each(['./art/icon.svg', 'art/icon.svg'])('includes declared icon %s in the canonical payload', (icon) => {
    expect(expectedCortexPackageFiles({ icon, exports: { './locale/*.json': './locale/*.json' } })).toEqual([
      'art/icon.svg', 'locale/*.json', 'lib/index.js', 'lib/types/**/*.d.ts',
    ])
  })

  it('accepts the Agent Team icon payload and rejects its omission', () => {
    const dir = 'packages/experimental/agent-team-profile'
    const manifest = JSON.parse(readFileSync(new URL(`../${dir}/package.json`, import.meta.url), 'utf8')) as WorkspaceManifest['manifest']
    expect(checkWorkspaceManifest({ dir, manifest })).toEqual([])
    expect(checkWorkspaceManifest({ dir, manifest: { ...manifest, files: manifest.files!.filter(file => file !== 'icon.svg') } }))
      .toEqual([expect.stringContaining('package.json files must be')])
  })

  it.each([
    { exports: { './locale/*.json': './locale/*.json' }, resources: ['locale/*.json'] },
    { exports: { './search/locale/*.json': './resources/search/*.json' }, resources: ['resources/search/*.json'] },
    { exports: { './locale/en.json': './locale/en.json', './locale/zh.json': './locale/zh.json' }, resources: ['locale/en.json', 'locale/zh.json'] },
    { exports: { './locale/*.json': { default: './locale/*.json' } }, resources: ['locale/*.json'] },
    { exports: { './locale/*.json': './locale/*.json', './search/locale/*.json': './locale/*.json' }, resources: ['locale/*.json'] },
    { exports: { './search/locale/*.json': './z/*.json', './locale/*.json': './a/*.json' }, resources: ['a/*.json', 'z/*.json'] },
  ])('includes declared locale resources in the canonical payload: $exports', ({ exports, resources }) => {
    expect(expectedCortexPackageFiles({ name: '@cortex/localized', exports })).toEqual([
      ...resources, 'lib/index.js', 'lib/types/**/*.d.ts',
    ])
  })

  it('does not infer locale payloads from unrelated or non-JSON exports', () => {
    expect(expectedCortexPackageFiles({
      exports: {
        './config.json': './config.json',
        './locale/README.md': './locale/README.md',
        './locale/en.json': './metadata.js',
        './locale/zh.json': null,
        './locale/fr.json': { types: './locale/fr.d.ts' },
      },
    })).toEqual(['lib/index.js', 'lib/types/**/*.d.ts'])
  })

  it.each(['agent-team', 'agent-team-profile', 'auto-review', 'client-ui-agent-team', 'tool-agent-team'])(
    'accepts the published locale files for %s and rejects their omission', (name) => {
      const dir = `packages/experimental/${name}`
      const manifest = JSON.parse(readFileSync(new URL(`../${dir}/package.json`, import.meta.url), 'utf8')) as WorkspaceManifest['manifest']
      expect(checkWorkspaceManifest({ dir, manifest })).toEqual([])
      expect(checkWorkspaceManifest({ dir, manifest: {
        ...manifest, files: manifest.files!.filter(file => file !== 'locale/*.json'),
      } })).toEqual([expect.stringContaining('package.json files must be')])
    },
  )

  it('rejects locale publication entries without their resource exports', () => {
    const dir = 'packages/experimental/auto-review'
    const manifest = JSON.parse(readFileSync(new URL(`../${dir}/package.json`, import.meta.url), 'utf8')) as WorkspaceManifest['manifest']
    const exports = { ...manifest.exports }
    delete exports['./locale/*.json']
    expect(checkWorkspaceManifest({ dir, manifest: { ...manifest, exports } }))
      .toEqual([expect.stringContaining('package.json files must be')])
  })

  it('includes a declared profile patch without a package-name allowlist', () => {
    expect(expectedCortexPackageFiles({
      name: '@cortex/private-profile',
      cortex: { bundle: { patch: './cordis.patch.yml' } },
    })).toEqual([
      'lib/index.js',
      'cordis.patch.yml',
      'lib/types/**/*.d.ts',
    ])
    expect(expectedCortexPackageFiles({
      name: '@cortex/private-profile',
      cortex: { bundle: { patch: ['./cordis.patch.yml', './layers/web.patch.yml'] } },
    })).toEqual([
      'lib/index.js',
      'cordis.patch.yml',
      'layers/web.patch.yml',
      'lib/types/**/*.d.ts',
    ])
  })

  it.each([
    'packages/client/ui-sidebar-documentpreview',
    'packages/client/ui-sidebar-terminal',
  ])('accepts package-local Client chunks from %s', (dir) => {
    const manifest = JSON.parse(readFileSync(new URL(`../${dir}/package.json`, import.meta.url), 'utf8')) as WorkspaceManifest['manifest']
    expect(checkWorkspaceManifest({ dir, manifest })).toEqual([])
  })
})

it('publishes CLI runtime declarations and rejects a payload that omits them', () => {
  const manifest = JSON.parse(readFileSync(new URL('../apps/cli/package.json', import.meta.url), 'utf8')) as WorkspaceManifest['manifest']
  expect(checkWorkspaceManifest({ dir: 'apps/cli', manifest })).toEqual([])
  expect(checkWorkspaceManifest({ dir: 'apps/cli', manifest: { ...manifest, files: ['lib/*.js'] } }))
    .toEqual([expect.stringContaining('@cortex/cortex: package.json files must be ["lib/*.js","lib/types/*.d.ts"]')])
})

it.each(['apps/desktop-host', 'apps/web'])('requires the declared application runtime payload for %s', (dir) => {
  const manifest = JSON.parse(readFileSync(new URL(`../${dir}/package.json`, import.meta.url), 'utf8')) as WorkspaceManifest['manifest']
  expect(checkWorkspaceManifest({ dir, manifest: { ...manifest, files: [] } }))
    .toContainEqual(expect.stringContaining(`${manifest.name}: package.json files must be`))
})

it('keeps optional analytics compatibility declarations private and without runtime dependencies', () => {
  const dir = 'packages/client/product-analytics'
  const manifest = JSON.parse(readFileSync(new URL(`../${dir}/package.json`, import.meta.url), 'utf8')) as WorkspaceManifest['manifest']
  expect(checkWorkspaceManifest({ dir, manifest })).toEqual([])
  expect(checkWorkspaceManifest({ dir, manifest: { ...manifest, private: false } }))
    .toContainEqual(expect.stringContaining('compatibility declarations must remain private'))
  expect(checkWorkspaceManifest({ dir, manifest: { ...manifest, publishConfig: { access: 'public' } } }))
    .toContainEqual(expect.stringContaining('compatibility declarations must not declare publishConfig'))
  for (const fields of [{ dependencies: { collector: '1' } }, { optionalDependencies: { collector: '1' } }, { bin: './collector.js' }]) {
    expect(checkWorkspaceManifest({ dir, manifest: { ...manifest, ...fields } }))
      .toContainEqual(expect.stringContaining('compatibility declarations must not install runtime dependencies or register a plugin'))
  }
  expect(checkWorkspaceManifest({ dir: 'packages/client/other', manifest }))
    .toContainEqual(expect.stringContaining('release member must not set "private": true'))
})

it('requires the shared Web injection entry in the published payload', () => {
  const manifest = JSON.parse(readFileSync(new URL('../packages/client/web/package.json', import.meta.url), 'utf8')) as WorkspaceManifest['manifest']
  expect(checkWorkspaceManifest({ dir: 'packages/client/web', manifest })).toEqual([])
  expect(checkWorkspaceManifest({ dir: 'packages/client/web', manifest: {
    ...manifest, files: ['lib/index.js', 'lib/**/*.css', 'lib/types/**/*.d.ts'],
  } })).toEqual([expect.stringContaining('package.json files must be')])
})

it('requires Office skill bodies and helpers in the published payload', () => {
  const manifest = JSON.parse(readFileSync(new URL('../packages/skill/skill-office/package.json', import.meta.url), 'utf8')) as WorkspaceManifest['manifest']
  expect(checkWorkspaceManifest({ dir: 'packages/skill/skill-office', manifest })).toEqual([])
  expect(checkWorkspaceManifest({ dir: 'packages/skill/skill-office', manifest: {
    ...manifest, files: ['lib/index.js', 'lib/types/**/*.d.ts'],
  } })).toEqual([expect.stringContaining('package.json files must be')])
})

it('requires the local speech worker and locked runtime in the published payload', () => {
  const dir = 'packages/experimental/speech-to-text-sensevoice'
  const manifest = JSON.parse(readFileSync(new URL(`../${dir}/package.json`, import.meta.url), 'utf8')) as WorkspaceManifest['manifest']
  expect(checkWorkspaceManifest({ dir, manifest })).toEqual([])
  for (const omitted of ['lib/worker.js', 'runtime/assets.json']) {
    expect(checkWorkspaceManifest({ dir, manifest: { ...manifest, files: manifest.files!.filter(file => file !== omitted) } }))
      .toEqual([expect.stringContaining('package.json files must be')])
  }
})

it('requires the standalone shortcut protocol and rejects unrelated runtime files', () => {
  const dir = 'packages/client/shortcuts'
  const manifest = JSON.parse(readFileSync(new URL(`../${dir}/package.json`, import.meta.url), 'utf8')) as WorkspaceManifest['manifest']
  expect(checkWorkspaceManifest({ dir, manifest })).toEqual([])
  for (const files of [
    ['lib/index.js', 'lib/client.js', 'lib/types/**/*.d.ts'],
    ['lib/index.js', 'lib/client.js', 'lib/protocol.js', 'lib/extra.js', 'lib/types/**/*.d.ts'],
  ]) {
    expect(checkWorkspaceManifest({ dir, manifest: { ...manifest, files } }))
      .toEqual([expect.stringContaining('package.json files must be')])
  }
})
