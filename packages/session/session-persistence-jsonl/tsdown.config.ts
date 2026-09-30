import { defineConfig } from 'tsdown'

/** Build the backend and its path-loaded verifier as separate bundles. */
export default defineConfig(({ env }) => env?.CORTEX_BUILD_FACE === 'client' ? [] : [
  {
    entry: ['lib/types/index.js'],
    outDir: 'lib',
    format: ['esm'],
    platform: 'node',
    target: 'es2024',
    fixedExtension: false,
    dts: false,
    clean: false,
  },
  {
    entry: ['lib/types/worker.js'],
    // Fresh verifiers need no host singleton identity. Inline their JavaScript
    // closure to avoid resolving and compiling workspace packages on every open.
    deps: { alwaysBundle: [/^@cortex\//] },
    // Resolve emitted package exports; source aliases still contain decorators.
    inputOptions: { tsconfig: false },
    outDir: 'lib',
    format: ['cjs'],
    platform: 'node',
    target: 'es2024',
    fixedExtension: false,
    dts: false,
    clean: false,
  },
])
