import { realpathSync, statSync } from "node:fs";
import { isAbsolute, join } from "node:path";
import { MemosError } from "../../agent-contract/errors.js";
import type { ResolvedConfig } from "../config/schema.js";

/** The Cortex build delegates all language-model requests to its host. */
export function applyCortexPrivacyPolicy(config: ResolvedConfig): ResolvedConfig {
  const locked = structuredClone(config);
  locked.llm = {
    ...locked.llm,
    provider: "host", model: "", endpoint: "", apiKey: "",
    fallbackToHost: false, openRouter: false,
    providerIgnore: [], providerOrder: [], headers: {},
  };
  for (const slot of [locked.skillEvolver, locked.l3Llm]) {
    // Empty dedicated slots use the main host client, preserving all reasoning.
    slot.provider = "";
    slot.model = "";
    slot.endpoint = "";
    slot.apiKey = "";
    slot.openRouter = false;
    slot.providerIgnore = [];
    slot.providerOrder = [];
    slot.headers = {};
  }
  locked.embedding = {
    ...locked.embedding, provider: "local", endpoint: "", apiKey: "",
    openRouter: false, providerIgnore: [], providerOrder: [],
  };
  locked.hub = {
    ...locked.hub, enabled: false, address: "", teamToken: "", userToken: "",
  };
  locked.telemetry.enabled = false;
  locked.viewer.bindHost = "127.0.0.1";
  locked.algorithm.lightweightMemory.enabled = false;
  return freezeConfig(locked);
}

function freezeConfig<T>(value: T): T {
  if (typeof value === "object" && value !== null) {
    for (const child of Object.values(value)) freezeConfig(child);
    Object.freeze(value);
  }
  return value;
}

/** Reject external provider and sharing settings before any config is written. */
export function assertCortexConfigPatch(patch: Record<string, unknown>): void {
  const allowed: Record<string, Record<string, unknown>> = {
    llm: { provider: "host", model: "", endpoint: "", apiKey: "", fallbackToHost: false, openRouter: false },
    skillEvolver: { provider: "", model: "", endpoint: "", apiKey: "", openRouter: false },
    l3Llm: { provider: "", model: "", endpoint: "", apiKey: "", openRouter: false },
    embedding: { provider: "local", endpoint: "", apiKey: "", openRouter: false },
    hub: { enabled: false, address: "", teamToken: "", userToken: "" },
    telemetry: { enabled: false },
    viewer: { bindHost: "127.0.0.1" },
  };
  for (const [blockName, invariants] of Object.entries(allowed)) {
    const block = patch[blockName];
    if (block === undefined) continue;
    if (!isRecord(block)) throw privacyError(`${blockName} must be a settings object`);
    for (const [key, required] of Object.entries(invariants)) {
      if (Object.hasOwn(block, key) && block[key] !== required) {
        throw privacyError(`${blockName}.${key} is locked by the Cortex memory privacy policy`);
      }
    }
    for (const key of ["providerIgnore", "providerOrder", "headers"]) {
      const value = block[key];
      if (value !== undefined && (!value || typeof value !== "object" || Object.keys(value).length > 0)) {
        throw privacyError(`${blockName}.${key} cannot configure a separate provider`);
      }
    }
  }
  const algorithm = patch.algorithm;
  if (isRecord(algorithm) && isRecord(algorithm.lightweightMemory)
    && algorithm.lightweightMemory.enabled !== undefined
    && algorithm.lightweightMemory.enabled !== false) {
    throw privacyError("Cortex memory uses the full reasoning and skill pipeline");
  }
  if (isRecord(patch.embedding) && patch.embedding.model !== undefined) {
    if (typeof patch.embedding.model !== "string") throw privacyError("embedding.model must be a local directory");
    assertLocalEmbeddingAssets(patch.embedding.model);
  }
}

/** Validate the q8 model before startup; runtime must never provision assets. */
export function assertLocalEmbeddingAssets(model: string): string {
  if (!isAbsolute(model) || isNetworkPath(model)) {
    throw privacyError("embedding.model must be an absolute local model directory; downloads are disabled");
  }
  try {
    const directory = realpathSync(model);
    if (isNetworkPath(directory) || !statSync(directory).isDirectory()) {
      throw privacyError("embedding.model must be on the local filesystem");
    }
    for (const file of ["config.json", "tokenizer.json", "tokenizer_config.json", "onnx/model_quantized.onnx"]) {
      const asset = realpathSync(join(directory, file));
      if (isNetworkPath(asset) || !statSync(asset).isFile()) {
        throw privacyError(`Missing local embedding asset: ${file}`);
      }
    }
    return directory;
  } catch (error) {
    if (MemosError.is(error)) throw error;
    throw privacyError(`Local embedding assets are unavailable at ${model}; provision them before starting Cortex memory`);
  }
}

function isNetworkPath(path: string): boolean {
  return path.startsWith("\\\\") || path.startsWith("//");
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

export function privacyError(message: string): MemosError {
  return new MemosError("config_invalid", message);
}

/** Used only by this vendored memory package, never by Cortex's networking. */
export function denyMemoryNetwork(feature: string): never {
  throw privacyError(`${feature} is disabled in Cortex memory; model requests must use the Cortex host bridge`);
}

/** Kept as a void guard so upstream transport code remains type-checkable. */
export function assertMemoryNetworkAllowed(feature: string): void {
  denyMemoryNetwork(feature);
}
