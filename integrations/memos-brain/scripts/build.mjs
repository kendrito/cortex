import { build } from 'esbuild';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';

const packageRoot = fileURLToPath(new URL('../', import.meta.url));
await build({ absWorkingDir: packageRoot, entryPoints: ['src/host.ts'], outdir: resolve(packageRoot, 'lib'), bundle: false, format: 'esm', platform: 'node', target: 'node22' });
await build({
  absWorkingDir: packageRoot, entryPoints: ['src/client.ts'], outfile: resolve(packageRoot, 'lib/client.js'),
  bundle: true, format: 'cjs', platform: 'browser', target: 'es2022', minify: true,
  external: ['react'], define: { 'process.env.NODE_ENV': '"production"' },
  banner: { js: 'window.__ModuleLoader__.load({id:"@cortex/memos-brain",factory:(require)=>{var module={exports:{}};var exports=module.exports;' },
  footer: { js: 'return module.exports;}});' },
});
