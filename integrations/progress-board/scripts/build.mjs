import { build } from 'esbuild';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';

const packageRoot = fileURLToPath(new URL('../', import.meta.url));
const lib = resolve(packageRoot, 'lib');

// Host: plain ESM for Node, one file per source so the board model stays importable by tests.
await build({
  absWorkingDir: packageRoot, entryPoints: ['src/host.ts', 'src/board.ts'], outdir: lib,
  bundle: false, format: 'esm', platform: 'node', target: 'node22',
});
// Client: one browser module the Cortex loader serves from /plugins, React left to the page.
await build({
  absWorkingDir: packageRoot, entryPoints: ['src/client.ts'], outfile: resolve(lib, 'client.js'),
  bundle: true, format: 'cjs', platform: 'browser', target: 'es2022', minify: true,
  external: ['react'], define: { 'process.env.NODE_ENV': '"production"' },
  banner: { js: 'window.__ModuleLoader__.load({id:"@cortex/progress-board",factory:(require)=>{var module={exports:{}};var exports=module.exports;' },
  footer: { js: 'return module.exports;}});' },
});
