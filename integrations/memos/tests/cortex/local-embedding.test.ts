import { mkdtemp, mkdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { LocalEmbeddingProvider, __resetLocalExtractorForTests } from "../../core/embedding/providers/local.js";

const transformers = vi.hoisted(() => ({
  env: { allowRemoteModels: true, allowLocalModels: false },
  pipeline: vi.fn(async () => async () => ({ data: new Float32Array([0.5, 0.5]) })),
}));
vi.mock("@huggingface/transformers", () => transformers);

describe("local embeddings without runtime downloads", () => {
  let directory: string;
  beforeEach(async () => {
    directory = await mkdtemp(join(tmpdir(), "cortex-memos-model-"));
    __resetLocalExtractorForTests();
    transformers.pipeline.mockClear();
    transformers.env.allowRemoteModels = true;
    await mkdir(join(directory, "onnx"));
    for (const file of ["config.json", "tokenizer.json", "tokenizer_config.json", "onnx/model_quantized.onnx"]) {
      await writeFile(join(directory, file), "fixture");
    }
  });
  afterEach(async () => { await rm(directory, { recursive: true, force: true }); });

  it("loads existing q8 assets and explicitly forbids remote model resolution", async () => {
    const provider = new LocalEmbeddingProvider();
    const noop = () => {};
    const result = await provider.embed(["remember the architecture"], "document", {
      config: {
        provider: "local", model: directory, dimensions: 2, openRouter: false,
        cache: { enabled: false, maxItems: 0 },
      },
      log: { trace: noop, debug: noop, info: noop, warn: noop, error: noop },
    });
    expect(result).toEqual([[0.5, 0.5]]);
    expect(transformers.env.allowRemoteModels).toBe(false);
    expect(transformers.env.allowLocalModels).toBe(true);
    expect(transformers.pipeline).toHaveBeenCalledWith("feature-extraction", directory, {
      dtype: "q8", device: "cpu", local_files_only: true,
    });
  });

  it("rejects a hub model ID before loading the inference runtime", async () => {
    const noop = () => {};
    await expect(new LocalEmbeddingProvider().embed(["test"], "query", {
      config: {
        provider: "local", model: "Xenova/all-MiniLM-L6-v2", dimensions: 384,
        openRouter: false, cache: { enabled: false, maxItems: 0 },
      },
      log: { trace: noop, debug: noop, info: noop, warn: noop, error: noop },
    })).rejects.toThrow(/downloads are disabled/);
    expect(transformers.pipeline).not.toHaveBeenCalled();
  });
});
