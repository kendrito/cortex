import { mkdtemp, mkdir, readdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { DEFAULT_CONFIG } from "../../core/config/defaults.js";
import { patchConfig } from "../../core/config/writer.js";
import { resolveHome } from "../../core/config/paths.js";
import { applyCortexPrivacyPolicy, assertCortexConfigPatch, assertLocalEmbeddingAssets } from "../../core/privacy/policy.js";
import { createLlmClient, makeProviderFor as llmProvider } from "../../core/llm/client.js";
import { makeProviderFor as embeddingProvider } from "../../core/embedding/embedder.js";
import { httpPostJson, httpPostStream } from "../../core/llm/fetcher.js";
import { httpPostJson as embedPost } from "../../core/embedding/fetcher.js";
import { hubRequestJson } from "../../core/hub/client.js";
import { Telemetry } from "../../core/telemetry/sender.js";
import { initTestLogger, rootLogger } from "../../core/logger/index.js";
import { registerHostLlmBridge, type HostLlmCompleteInput } from "../../core/llm/host-bridge.js";

describe("Cortex memory privacy boundary", () => {
  let root = "";
  const network = vi.fn(() => { throw new Error("unexpected network"); });
  beforeEach(async () => {
    root = await mkdtemp(join(tmpdir(), "cortex-memos-privacy-"));
    initTestLogger();
    vi.stubGlobal("fetch", network);
    network.mockClear();
  });
  afterEach(async () => {
    registerHostLlmBridge(null);
    vi.unstubAllGlobals();
    vi.unstubAllEnvs();
    await rm(root, { recursive: true, force: true });
  });

  it("locks imported provider settings while retaining full reasoning features", () => {
    const imported = structuredClone(DEFAULT_CONFIG);
    imported.llm.provider = "openai_compatible";
    imported.llm.endpoint = "https://example.invalid/llm";
    imported.llm.apiKey = "example";
    imported.skillEvolver.provider = "anthropic";
    imported.skillEvolver.model = "separate-model";
    imported.l3Llm.provider = "gemini";
    imported.l3Llm.model = "another-model";
    imported.hub.enabled = true;
    const locked = applyCortexPrivacyPolicy(imported);
    expect(locked.llm).toMatchObject({ provider: "host", model: "", apiKey: "", endpoint: "" });
    expect(locked.skillEvolver).toMatchObject({ provider: "", model: "", apiKey: "" });
    expect(locked.l3Llm).toMatchObject({ provider: "", model: "", apiKey: "" });
    expect(locked.embedding.provider).toBe("local");
    expect(locked.hub.enabled).toBe(false);
    expect(locked.telemetry.enabled).toBe(false);
    expect(locked.algorithm.lightweightMemory.enabled).toBe(false);
    expect(locked.algorithm.l2Induction.useLlm).toBe(true);
    expect(locked.algorithm.l3Abstraction.useLlm).toBe(true);
    expect(locked.algorithm.skill.useLlm).toBe(true);
    expect(Object.isFrozen(locked.hub)).toBe(true);
    expect(imported.llm.provider).toBe("openai_compatible");
  });

  it.each([
    { llm: { provider: "openai_compatible" } },
    { llm: { model: "different-model" } },
    { embedding: { provider: "gemini" } },
    { skillEvolver: { endpoint: "http://127.0.0.1:11434" } },
    { l3Llm: { apiKey: "key" } },
    { hub: { enabled: true } },
    { telemetry: { enabled: true } },
    { viewer: { bindHost: "0.0.0.0" } },
  ])("rejects privacy changes before config persistence: %j", async (patch) => {
    vi.stubEnv("MEMOS_HOME", root);
    await expect(patchConfig(resolveHome("deepseek-harness"), patch)).rejects.toThrow();
    expect(await readdir(root)).toEqual([]);
  });

  it("allows algorithm tuning without adding another provider", () => {
    expect(() => assertCortexConfigPatch({
      llm: { timeoutMs: 60_000 },
      algorithm: { retrieval: { tier2TopK: 8 } },
    })).not.toThrow();
  });

  it("rejects hub IDs, URLs, UNC paths and incomplete local model directories", async () => {
    for (const model of ["Xenova/all-MiniLM-L6-v2", "https://huggingface.co/model", "\\\\host\\models", root]) {
      expect(() => assertLocalEmbeddingAssets(model)).toThrow();
    }
    await mkdir(join(root, "onnx"));
    for (const file of ["config.json", "tokenizer.json", "tokenizer_config.json", "onnx/model_quantized.onnx"]) {
      await writeFile(join(root, file), "fixture");
    }
    expect(assertLocalEmbeddingAssets(root)).toBe(root);
    expect(network).not.toHaveBeenCalled();
  });

  it("blocks direct provider factories and legacy network helper entrypoints", async () => {
    expect(() => llmProvider("openai_compatible")).toThrow(/Cortex/);
    expect(() => embeddingProvider("gemini")).toThrow(/Cortex/);
    const opts = {
      url: "https://example.invalid/api", body: {}, timeoutMs: 1000,
      maxRetries: 0, provider: "openai_compatible" as const, log: rootLogger,
    };
    await expect(httpPostJson(opts)).rejects.toThrow(/Cortex/);
    await expect(httpPostStream(opts)).rejects.toThrow(/Cortex/);
    await expect(embedPost(opts)).rejects.toThrow(/Cortex/);
    await expect(hubRequestJson("https://example.invalid", "token", "/memory")).rejects.toThrow(/Cortex/);
    expect(network).not.toHaveBeenCalled();
  });

  it("continues model reasoning through the supplied host bridge", async () => {
    const complete = vi.fn(async (_input: HostLlmCompleteInput) => ({ text: '{"summary":"retained"}', model: "host-selected", durationMs: 1 }));
    registerHostLlmBridge({ id: "cortex-test", complete });
    const client = createLlmClient({
      provider: "host", model: "", temperature: 0, fallbackToHost: false,
      timeoutMs: 1000, maxRetries: 0, openRouter: false,
    });
    const result = await client.completeJson<{ summary: string }>("Summarize this memory");
    expect(result.value.summary).toBe("retained");
    expect(complete).toHaveBeenCalledOnce();
    expect(complete.mock.calls[0]?.[0].model).toBeUndefined();
    expect(network).not.toHaveBeenCalled();
    await client.close();
  });

  it("cannot enable telemetry through environment variables or old config", async () => {
    vi.stubEnv("MEMOS_ARMS_ENDPOINT", "https://example.invalid/telemetry");
    const telemetry = new Telemetry({ enabled: true }, root, "test", rootLogger);
    telemetry.trackPluginStarted("cortex");
    telemetry.trackTurnStart("cortex", 1, 2);
    telemetry.trackTurnEnd("cortex", 3);
    telemetry.trackMemorySearch("cortex", 1, 2);
    telemetry.trackFeedback("cortex", "positive");
    telemetry.trackViewerOpened();
    telemetry.trackError("test", "test");
    await telemetry.shutdown();
    expect(await readdir(root)).toEqual([]);
    expect(network).not.toHaveBeenCalled();
  });
});
