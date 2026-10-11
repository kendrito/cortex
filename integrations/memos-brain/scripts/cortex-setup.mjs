#!/usr/bin/env node
/** Link declared peers from a built Cortex checkout without changing a profile or memory home. */
import { lstat, mkdir, readFile, readdir, realpath, stat, symlink, unlink } from 'node:fs/promises';
import { dirname, isAbsolute, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const packageRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const usage = 'node scripts/cortex-setup.mjs [--cortex-root <absolute-built-checkout>]';

async function existing(path) {
  try { return await lstat(path); }
  catch (error) {
    if (error.code === 'ENOENT') return undefined;
    throw error;
  }
}

async function requireFile(path) {
  if (!(await stat(path)).isFile()) throw new Error(`Required built file is unavailable: ${path}`);
}

async function directories(path) {
  return (await readdir(path, { withFileTypes: true }))
    .filter(entry => entry.isDirectory()).map(entry => join(path, entry.name));
}

function runtimeExport(value) {
  if (typeof value === 'string') return value;
  if (value === null || typeof value !== 'object') return undefined;
  return runtimeExport(value.import) ?? runtimeExport(value.node) ?? runtimeExport(value.default);
}

async function main() {
  const args = process.argv.slice(2);
  if (args.length === 1 && args[0] === '--help') { console.log(usage); return; }
  if (args.length !== 0 && (args.length !== 2 || args[0] !== '--cortex-root')) throw new Error(usage);
  const suppliedRoot = args[1] ?? resolve(packageRoot, '../..');
  if (!isAbsolute(suppliedRoot) || /^[\\/]{2}/.test(suppliedRoot)) throw new Error('--cortex-root must be an absolute local checkout');
  const cortexRoot = await realpath(suppliedRoot);
  const manifest = JSON.parse(await readFile(join(packageRoot, 'package.json'), 'utf8'));
  const peers = Object.keys(manifest.peerDependencies).filter(name => name.startsWith('@cortex/'));
  const candidates = await directories(join(cortexRoot, 'vendor'));
  for (const group of await directories(join(cortexRoot, 'packages'))) candidates.push(...await directories(group));
  const found = new Map();
  for (const directory of candidates) {
    const path = join(directory, 'package.json');
    if (!await existing(path)) continue;
    const candidate = JSON.parse(await readFile(path, 'utf8'));
    if (!peers.includes(candidate.name)) continue;
    if (found.has(candidate.name)) throw new Error(`Duplicate Cortex package: ${candidate.name}`);
    const entry = runtimeExport(candidate.exports?.['.']) ?? candidate.module ?? candidate.main;
    const types = candidate.types ?? candidate.exports?.['.']?.types;
    if (!entry || !types) throw new Error(`Cortex package lacks runtime or declarations: ${candidate.name}`);
    await requireFile(resolve(directory, entry));
    await requireFile(resolve(directory, types));
    found.set(candidate.name, await realpath(directory));
  }

  const nodeModules = join(packageRoot, 'node_modules'), scope = join(nodeModules, '@cortex');
  if (!await existing(nodeModules)) throw new Error('Run npm ci --ignore-scripts --legacy-peer-deps before setup');
  for (const directory of [nodeModules, scope]) {
    const info = await existing(directory);
    if (info && (!info.isDirectory() || info.isSymbolicLink())) throw new Error(`Refusing non-directory or link: ${directory}`);
  }
  await requireFile(join(nodeModules, 'typescript', 'bin', 'tsc'));
  await requireFile(join(nodeModules, '@cortex', 'memos-local-memory', 'dist', 'core', 'brain', 'read.js'));
  await requireFile(join(nodeModules, '@cortex', 'memos-local-memory', 'viewer', 'dist', 'brain.html'));
  const links = peers.map(peer => {
    const target = found.get(peer);
    if (!target) throw new Error(`Built Cortex peer is missing: ${peer}`);
    return { path: join(scope, peer.slice('@cortex/'.length)), target };
  });
  for (const link of links) {
    if (await existing(link.path)) throw new Error(`Refusing existing peer destination: ${link.path}`);
  }
  const created = [];
  try {
    await mkdir(scope, { recursive: true });
    for (const link of links) {
      await symlink(link.target, link.path, process.platform === 'win32' ? 'junction' : 'dir');
      created.push(link);
    }
  } catch (error) {
    for (const link of created.reverse()) {
      try {
        if ((await lstat(link.path)).isSymbolicLink() && await realpath(link.path) === link.target) await unlink(link.path);
      } catch (cleanup) { console.error(`Could not remove new peer link ${link.path}: ${cleanup.message}`); }
    }
    throw error;
  }
  console.log(`Linked ${links.length} built Cortex peers. No network access, profile activation, or memory changes were performed.`);
}

main().catch(error => { console.error(`Cortex Brain setup failed: ${error.message}`); process.exitCode = 1; });
