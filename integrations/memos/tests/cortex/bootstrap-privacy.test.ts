import { access, mkdir, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { makeTmpHome, type TmpHomeContext } from "../helpers/tmp-home.js";
import { bootstrapMemoryCoreFull } from "../../core/pipeline/memory-core.js";
import { initTestLogger } from "../../core/logger/index.js";
import { __resetLocalExtractorForTests } from "../../core/embedding/providers/local.js";
import { registerHostLlmBridge } from "../../core/llm/host-bridge.js";
import type { MemoryCore } from "../../agent-contract/memory-core.js";

const transforms = vi.hoisted(() => ({
  env: { allowRemoteModels: true, allowLocalModels: false },
  pipeline: vi.fn(async () => async () => ({ data: new Float32Array(384).fill(1) })),
}));
vi.mock("@huggingface/transformers", () => transforms);

describe("Cortex memory startup keeps both inference capabilities", () => {
  let home: TmpHomeContext;
  let core: MemoryCore | undefined;
  beforeEach(async () => {
    initTestLogger();
    __resetLocalExtractorForTests();
    home = await makeTmpHome({ agent: "cortex" });
    const model = join(home.root, "model");
    await mkdir(join(model, "onnx"), { recursive: true });
    for (const file of ["config.json", "tokenizer.json", "tokenizer_config.json", "onnx/model_quantized.onnx"]) {
      await writeFile(join(model, file), "fixture");
    }
    home.config = structuredClone(home.config);
    home.config.embedding.model = model;
    vi.stubGlobal("fetch", vi.fn(() => { throw new Error("unexpected network"); }));
  });
  afterEach(async () => {
    await core?.shutdown();
    core = undefined;
    registerHostLlmBridge(null);
    vi.unstubAllGlobals();
    await home.cleanup();
  });

  it("requires an explicit host bridge before creating the database", async () => {
    await expect(bootstrapMemoryCoreFull({ agent: "cortex", home: home.home, config: home.config }))
      .rejects.toThrow(/requires.*host model bridge/);
    await expect(access(home.home.dbFile)).rejects.toThrow();
  });

  it("loads local semantic inference and retains all evolution stages", async () => {
    const complete = vi.fn(async () => ({ text: "{}", model: "selected-by-cortex", durationMs: 1 }));
    const result = await bootstrapMemoryCoreFull({
      agent: "cortex", home: home.home, config: home.config, autoRecovery: false,
      hostLlmBridge: { id: "cortex-test", complete },
    });
    core = result.core;
    await core.init();
    const health = await core.health();
    expect(health.embedder).toMatchObject({ available: true, provider: "local", dim: 384 });
    expect(health.llm).toMatchObject({ available: true, provider: "host" });
    expect(result.config.algorithm.lightweightMemory.enabled).toBe(false);
    expect(result.config.algorithm.l2Induction.useLlm).toBe(true);
    expect(result.config.algorithm.l3Abstraction.useLlm).toBe(true);
    expect(result.config.algorithm.skill.useLlm).toBe(true);
    expect(complete).not.toHaveBeenCalled();
    expect(fetch).not.toHaveBeenCalled();
  });

  it("fails startup on a damaged local model instead of continuing without vectors", async () => {
    transforms.pipeline.mockRejectedValueOnce(new Error("corrupt model"));
    await expect(bootstrapMemoryCoreFull({
      agent: "cortex", home: home.home, config: home.config,
      hostLlmBridge: { id: "cortex-test", complete: async () => ({ text: "{}", model: "host", durationMs: 1 }) },
    })).rejects.toThrow(/semantic embeddings could not initialize.*corrupt model/);
    expect(fetch).not.toHaveBeenCalled();
  });
});
