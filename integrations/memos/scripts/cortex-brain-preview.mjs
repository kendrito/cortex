#!/usr/bin/env node
/** Prepare a new, empty preview home; only a Cortex profile may launch its viewer. */
import { mkdir, realpath, stat, writeFile } from "node:fs/promises";
import { isAbsolute, join, resolve } from "node:path";
import { pathToFileURL } from "node:url";

const usage = "node scripts/cortex-brain-preview.mjs --home <new-absolute-home> --model-dir <existing-local-model> --viewer-port <port>";

function localPath(value, label) {
  if (typeof value !== "string" || !isAbsolute(value) || value.startsWith("\\\\") || value.startsWith("//")) {
    throw new Error(`${label} must be an absolute local path`);
  }
  return resolve(value);
}

/** Create isolated configuration, refusing every existing destination. */
export async function prepareBrainPreview({ home, modelDir, viewerPort }) {
  const previewHome = localPath(home, "home");
  const assets = await realpath(localPath(modelDir, "model-dir"));
  if (!Number.isInteger(viewerPort) || viewerPort < 1024 || viewerPort > 65535) {
    throw new Error("viewer-port must be an integer from 1024 to 65535");
  }
  for (const name of ["config.json", "tokenizer.json", "tokenizer_config.json", "onnx/model_quantized.onnx"]) {
    if (!(await stat(join(assets, name))).isFile()) throw new Error(`Missing local embedding asset: ${name}`);
  }
  // An atomic non-recursive mkdir refuses an existing home, including a link.
  await mkdir(previewHome, { mode: 0o700 });
  const memoryHome = join(previewHome, "memos-plugin");
  await mkdir(memoryHome, { mode: 0o700 });
  const config = [
    "# Isolated Brain preview. Empty local database; no memory records are seeded.",
    "version: 1", "embedding:", "  provider: local", `  model: ${JSON.stringify(assets)}`,
    "llm:", "  provider: host", "  fallbackToHost: false",
    "hub:", "  enabled: false", "telemetry:", "  enabled: false",
    "viewer:", '  bindHost: "127.0.0.1"', `  port: ${viewerPort}`, "  openOnFirstTurn: false",
    "algorithm:", "  lightweightMemory:", "    enabled: false", "",
  ].join("\n");
  const patch = [
    "- id: memos-local-memory", "  config:", '    profileId: "isolated/brain-preview"',
    "    recallEnabled: false", "    captureEnabled: false", "    toolsEnabled: false",
    "    hostLlmEnabled: true", "    viewerEnabled: true", `    viewerPort: ${viewerPort}`, "",
  ].join("\n");
  await writeFile(join(memoryHome, "config.yaml"), config, { flag: "wx", mode: 0o600 });
  await writeFile(join(previewHome, "cordis.patch.yml"), patch, { flag: "wx", mode: 0o600 });
  const record = {
    version: 1, createdAt: new Date().toISOString(), purpose: "Isolated empty Brain preview",
    home: previewHome, viewerPort, databaseSeeded: false, providerCredentialsCopied: false,
    authentication: "Unchanged first-run setup; use a fresh disposable password in this preview only.",
    brainPath: "/#/brain",
    launch: "Use the supported Cortex web profile with CORTEX_HOME set to this directory.",
  };
  await writeFile(join(previewHome, "BRAIN-PREVIEW.json"), JSON.stringify(record, null, 2) + "\n", { flag: "wx", mode: 0o600 });
  return record;
}

async function main() {
  const args = process.argv.slice(2);
  if (args.length === 1 && args[0] === "--help") { console.log(usage); return; }
  const options = new Map();
  for (let i = 0; i < args.length; i += 2) {
    const flag = args[i];
    if (!["--home", "--model-dir", "--viewer-port"].includes(flag) || options.has(flag) || args[i + 1] === undefined) {
      throw new Error(usage);
    }
    options.set(flag, args[i + 1]);
  }
  const record = await prepareBrainPreview({
    home: options.get("--home"), modelDir: options.get("--model-dir"),
    viewerPort: Number(options.get("--viewer-port")),
  });
  console.log(`Prepared empty Brain preview home: ${record.home}`);
  console.log("No application launched, credentials copied, model called, or memory records seeded.");
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  main().catch((error) => { console.error(`Brain preview setup failed: ${error.message}`); process.exitCode = 1; });
}
