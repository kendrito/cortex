import { describe, expect, it } from 'vitest'
import type { NpmPackageLock, RegistryIndex } from './benchmark-npm-resolution.ts'
import {
  assertDualCortexInstallLayout,
  assertResolutionWorkBudget,
  buildDualCortexRegistry,
  MAX_RESOLUTION_WORK_UNITS,
} from './verify-npm-install-layout.ts'

function validLayout(): NpmPackageLock {
  return {
    lockfileVersion: 3,
    packages: {
      '': { dependencies: { '@cortex/cortex': '0.2.0', 'cortex-previous': 'npm:@cortex/cortex@0.1.0' } },
      'node_modules/@cortex/cordis': { version: '4.0.1' },
      'node_modules/@cortex/cortex': {
        version: '0.2.0',
        dependencies: { '@cortex/child': '^0.2.0' },
        peerDependencies: { '@cortex/cordis': '^4.0.1' },
      },
      'node_modules/@cortex/child': {
        version: '0.2.0',
        dependencies: { '@cortex/leaf': '^0.2.0' },
      },
      'node_modules/@cortex/leaf': { version: '0.2.0' },
      'node_modules/cortex-previous': {
        name: '@cortex/cortex',
        version: '0.1.0',
        dependencies: { '@cortex/child': '^0.1.0' },
        peerDependencies: { '@cortex/cordis': '^4.0.1' },
      },
      'node_modules/cortex-previous/node_modules/@cortex/child': {
        version: '0.1.0',
        dependencies: { '@cortex/leaf': '^0.1.0' },
      },
      'node_modules/cortex-previous/node_modules/@cortex/leaf': { version: '0.1.0' },
    },
  }
}

describe('npm install layout verifier', () => {
  it('creates two incompatible versions of every CORTEX package', () => {
    const index: RegistryIndex = new Map([
      ['@cortex/cortex', new Map([['0.1.1-rc.2', {
        name: '@cortex/cortex',
        version: '0.1.1-rc.2',
        dependencies: { '@cortex/child': '^0.1.1-rc.2' },
        peerDependencies: { '@cortex/cordis': '^4.0.1' },
      }]])],
      ['@cortex/child', new Map([['0.1.1-rc.2', {
        name: '@cortex/child',
        version: '0.1.1-rc.2',
      }]])],
      ['@cortex/cordis', new Map([['4.0.1', {
        name: '@cortex/cordis',
        version: '4.0.1',
      }]])],
    ])

    const dual = buildDualCortexRegistry(index, '0.1.1-rc.2')

    expect([...dual.get('@cortex/cortex')?.keys() ?? []]).toEqual(['0.1.0', '0.2.0'])
    expect(dual.get('@cortex/cortex')?.get('0.1.0')).toMatchObject({
      version: '0.1.0',
      dependencies: { '@cortex/child': '^0.1.0' },
      peerDependencies: { '@cortex/cordis': '^4.0.1' },
    })
    expect(dual.get('@cortex/cortex')?.get('0.2.0')).toMatchObject({
      version: '0.2.0',
      dependencies: { '@cortex/child': '^0.2.0' },
    })
    expect(dual.get('@cortex/cordis')).toBe(index.get('@cortex/cordis'))
  })

  it('accepts isolated CORTEX releases with one shared Cordis installation', () => {
    expect(assertDualCortexInstallLayout(validLayout())).toEqual({
      cortexPackagesPerVersion: 3,
      checkedCortexEdges: 4,
    })
  })

  it.each([
    ['react', 'node_modules/react'],
    ['react-dom', 'node_modules/react-dom'],
    ['react', 'node_modules/cortex-previous/node_modules/react'],
    ['react-dom', 'node_modules/cortex-previous/node_modules/react-dom'],
  ])('rejects browser runtime %s installed at %s in the CORTEX-only consumer', (name, path) => {
    const layout = validLayout()
    const packages = { ...layout.packages, [path]: { version: '18.3.1' } }
    expect(() => assertDualCortexInstallLayout({ ...layout, packages })).toThrow(
      `${path}: ${name} is a browser build input`,
    )
  })

  it('rejects an internal edge that crosses release versions', () => {
    const layout = validLayout()
    const packages = { ...layout.packages }
    Reflect.deleteProperty(packages, 'node_modules/cortex-previous/node_modules/@cortex/leaf')

    expect(() => assertDualCortexInstallLayout({ ...layout, packages })).toThrow(
      'node_modules/cortex-previous/node_modules/@cortex/child: dependencies '
      + '@cortex/leaf resolves to node_modules/@cortex/leaf@0.2.0, expected 0.1.0',
    )
  })

  it('rejects a second Cordis installation', () => {
    const layout = validLayout()
    const packages = {
      ...layout.packages,
      'node_modules/cortex-previous/node_modules/@cortex/cordis': { version: '4.0.1' },
    }

    expect(() => assertDualCortexInstallLayout({ ...layout, packages })).toThrow(
      'expected one shared @cortex/cordis',
    )
  })
})

describe('resolution work budget', () => {
  it('accepts the measured dual-release graph', () => {
    expect(assertResolutionWorkBudget({ cortexPackagesPerVersion: 277, checkedCortexEdges: 2524 }))
      .toBe(699_148)
  })

  it('accepts a graph exactly at the budget and rejects one internal edge above it', () => {
    const packages = 350
    const edgesAtBudget = Math.floor(MAX_RESOLUTION_WORK_UNITS / packages)

    expect(packages * edgesAtBudget).toBe(MAX_RESOLUTION_WORK_UNITS)
    expect(assertResolutionWorkBudget({ cortexPackagesPerVersion: packages, checkedCortexEdges: edgesAtBudget }))
      .toBe(MAX_RESOLUTION_WORK_UNITS)
    expect(() => assertResolutionWorkBudget({
      cortexPackagesPerVersion: packages,
      checkedCortexEdges: edgesAtBudget + 1,
    })).toThrow(`= ${String(MAX_RESOLUTION_WORK_UNITS + packages)} unit(s), budget ${String(MAX_RESOLUTION_WORK_UNITS)} unit(s)`)
  })

  it('accepts the headroom and rejects a runaway graph with its own measured counts', () => {
    expect(assertResolutionWorkBudget({ cortexPackagesPerVersion: 300, checkedCortexEdges: 2800 }))
      .toBe(840_000)
    expect(() => assertResolutionWorkBudget({ cortexPackagesPerVersion: 400, checkedCortexEdges: 3000 }))
      .toThrow('400 package(s) per release x 3000 internal edge(s) = 1200000 unit(s), budget 875000 unit(s)')
  })
})
