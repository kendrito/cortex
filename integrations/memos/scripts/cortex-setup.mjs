#!/usr/bin/env node
/** Link built Cortex peers and create a new memory home without network access. */
import { lstat, mkdir, open, readFile, readdir, realpath, stat, symlink, unlink } from "node:fs/promises";
import { dirname, isAbsolute, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const packageRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const usage = "node scripts/cortex-setup.mjs --cortex-root <absolute-built-checkout> --home <absolute-new-memory-home> --model-dir <absolute-local-model-directory>";

async function existing(path) {
  try { return await lstat(path); }
  catch (error) {
    if (error.code === "ENOENT") return null;
    throw error;
  }
}

async function requireFile(path) {
  if (!(await stat(path)).isFile()) throw new Error(`Required file is unavailable: ${path}`);
}

async function directories(path) {
  return (await readdir(path, { withFileTypes: true }))
    .filter((entry) => entry.isDirectory()).map((entry) => join(path, entry.name));
}

function absoluteOption(value, flag) {
  if (!value || !isAbsolute(value) || value.startsWith("\\\\") || value.startsWith("//")) {
    throw new Error(`${flag} requires an absolute local filesystem path`);
  }
  return resolve(value);
}

function runtimeExport(value) {
  if (typeof value === "string") return value;
  if (!value || typeof value !== "object") return undefined;
  return runtimeExport(value.import) ?? runtimeExport(value.node) ?? runtimeExport(value.default);
}

async function main() {
  const args = process.argv.slice(2);
  if (args.length === 1 && args[0] === "--help") { console.log(usage); return; }
  const options = new Map();
  for (let index = 0; index < args.length; index += 2) {
    const flag = args[index];
    if (!["--cortex-root", "--home", "--model-dir"].includes(flag) || options.has(flag)) {
      throw new Error(`Unknown or duplicate option: ${flag}\n${usage}`);
    }
    options.set(flag, absoluteOption(args[index + 1], flag));
  }
  for (const flag of ["--cortex-root", "--home", "--model-dir"]) {
    if (!options.has(flag)) throw new Error(`Missing ${flag}\n${usage}`);
  }

  const cortexRoot = absoluteOption(await realpath(options.get("--cortex-root")), "--cortex-root");
  const modelDir = absoluteOption(await realpath(options.get("--model-dir")), "--model-dir");
  const home = options.get("--home");
  if (!(await stat(modelDir)).isDirectory()) throw new Error(`Model path is not a directory: ${modelDir}`);
  for (const asset of ["config.json", "tokenizer.json", "tokenizer_config.json", "onnx/model_quantized.onnx"]) {
    const path = absoluteOption(await realpath(join(modelDir, asset)), "Embedding asset");
    await requireFile(path);
  }

  const manifest = JSON.parse(await readFile(join(packageRoot, "package.json"), "utf8"));
  const peers = Object.keys(manifest.peerDependencies ?? {})
    .filter((name) => name.startsWith("@cortex/") && manifest.peerDependenciesMeta?.[name]?.optional === true);
  if (!peers.length) throw new Error("This package declares no optional Cortex peers");
  const candidates = await directories(join(cortexRoot, "vendor"));
  for (const group of await directories(join(cortexRoot, "packages"))) candidates.push(...await directories(group));
  const found = new Map();
  for (const directory of candidates) {
    const path = join(directory, "package.json");
    if (!await existing(path)) continue;
    const candidate = JSON.parse(await readFile(path, "utf8"));
    if (!peers.includes(candidate.name)) continue;
    if (found.has(candidate.name)) throw new Error(`Duplicate Cortex package: ${candidate.name}`);
    const entry = runtimeExport(candidate.exports?.["."]) ?? candidate.module ?? candidate.main;
    const types = candidate.types ?? candidate.exports?.["."]?.types;
    if (!entry || !types) throw new Error(`Cortex package lacks runtime or declaration entry: ${candidate.name}`);
    await requireFile(resolve(directory, entry));
    await requireFile(resolve(directory, types));
    found.set(candidate.name, directory);
  }

  const nodeModules = join(packageRoot, "node_modules");
  const scope = join(nodeModules, "@cortex");
  for (const directory of [nodeModules, scope, home]) {
    const info = await existing(directory);
    if (info && (!info.isDirectory() || info.isSymbolicLink())) {
      throw new Error(`Refusing existing non-directory or link: ${directory}`);
    }
  }
  if (!await existing(nodeModules)) throw new Error("Install package dependencies with npm ci before setup");
  const configFile = join(home, "config.yaml");
  if (await existing(configFile)) throw new Error(`Refusing existing configuration: ${configFile}`);
  const links = [];
  for (const peer of peers) {
    const target = found.get(peer);
    if (!target) throw new Error(`Optional Cortex peer is missing from the supplied checkout: ${peer}`);
    const path = join(scope, peer.slice("@cortex/".length));
    if (await existing(path)) throw new Error(`Refusing existing peer destination: ${path}`);
    links.push({ path, target });
  }

  const config = [
    "# Cortex memory only. Cortex owns the selected language model and credentials.",
    "version: 1",
    "embedding:", "  provider: local", `  model: ${JSON.stringify(modelDir)}`,
    "llm:", "  provider: host", "  fallbackToHost: false",
    "hub:", "  enabled: false",
    "telemetry:", "  enabled: false",
    "viewer:", '  bindHost: "127.0.0.1"', "  port: 18801", "  openOnFirstTurn: false",
    "algorithm:", "  lightweightMemory:", "    enabled: false", "",
  ].join("\n");
  const created = [];
  let wroteConfig = false;
  try {
    await mkdir(scope, { recursive: true });
    await mkdir(home, { recursive: true });
    for (const link of links) {
      await symlink(link.target, link.path, process.platform === "win32" ? "junction" : "dir");
      created.push(link);
    }
    const handle = await open(configFile, "wx", 0o600);
    wroteConfig = true;
    try { await handle.writeFile(config, "utf8"); }
    finally { await handle.close(); }
    console.log(`Linked ${links.length} built Cortex peers. Created ${configFile}`);
    console.log("No model download, provider change, or profile activation was performed.");
  } catch (error) {
    if (wroteConfig) await unlink(configFile).catch((cleanup) => console.error(`Could not remove new configuration: ${cleanup.message}`));
    for (const link of created.reverse()) {
      try {
        if ((await lstat(link.path)).isSymbolicLink() && await realpath(link.path) === await realpath(link.target)) {
          await unlink(link.path);
        }
      } catch (cleanup) { console.error(`Could not remove new peer link ${link.path}: ${cleanup.message}`); }
    }
    throw error;
  }
}

main().catch((error) => { console.error(`Cortex memory setup failed: ${error.message}`); process.exitCode = 1; });
