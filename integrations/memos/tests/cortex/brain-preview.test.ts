import { mkdtemp, mkdir, readFile, readdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve, sep } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { parse } from "yaml";
import { prepareBrainPreview } from "../../scripts/cortex-brain-preview.mjs";

describe("isolated Brain preview preparation", () => {
  let root = "";
  let modelDir = "";
  beforeEach(async () => {
    root = await mkdtemp(join(tmpdir(), "cortex-brain-preview-test-"));
    modelDir = join(root, "model");
    await mkdir(join(modelDir, "onnx"), { recursive: true });
    for (const asset of ["config.json", "tokenizer.json", "tokenizer_config.json", "onnx/model_quantized.onnx"]) {
      await writeFile(join(modelDir, asset), "fixture asset; never executed");
    }
  });
  afterEach(async () => {
    vi.restoreAllMocks();
    if (!resolve(root).startsWith(resolve(tmpdir()) + sep)) throw new Error("Preview test escaped temporary storage");
    await rm(root, { recursive: true, force: true });
  });

  it("creates an empty local-only home without credentials, auth overrides or network calls", async () => {
    const network = vi.spyOn(globalThis, "fetch").mockRejectedValue(new Error("No network in preview preparation"));
    const home = join(root, "preview");
    const result = await prepareBrainPreview({ home, modelDir, viewerPort: 18829 });
    expect(result).toMatchObject({ databaseSeeded: false, providerCredentialsCopied: false, brainPath: "/#/brain" });
    expect((await readdir(home)).sort()).toEqual(["BRAIN-PREVIEW.json", "cordis.patch.yml", "memos-plugin"]);
    expect(await readdir(join(home, "memos-plugin"))).toEqual(["config.yaml"]);
    const config = parse(await readFile(join(home, "memos-plugin/config.yaml"), "utf8"));
    expect(config).toMatchObject({
      embedding: { provider: "local", model: modelDir }, llm: { provider: "host", fallbackToHost: false },
      hub: { enabled: false }, telemetry: { enabled: false }, viewer: { bindHost: "127.0.0.1", port: 18829 },
    });
    const patch = parse(await readFile(join(home, "cordis.patch.yml"), "utf8"));
    expect(patch).toEqual([{ id: "memos-local-memory", config: {
      profileId: "isolated/brain-preview", recallEnabled: false, captureEnabled: false,
      toolsEnabled: false, hostLlmEnabled: true, viewerEnabled: true, viewerPort: 18829,
    } }]);
    expect(network).not.toHaveBeenCalled();
  });

  it("refuses an existing home without touching its files", async () => {
    const home = join(root, "existing");
    await mkdir(home);
    await writeFile(join(home, "sentinel.txt"), "keep existing data");
    await expect(prepareBrainPreview({ home, modelDir, viewerPort: 18829 })).rejects.toThrow();
    expect(await readdir(home)).toEqual(["sentinel.txt"]);
    expect(await readFile(join(home, "sentinel.txt"), "utf8")).toBe("keep existing data");
  });

  it("rejects invalid configuration before creating a destination", async () => {
    const home = join(root, "invalid");
    await expect(prepareBrainPreview({ home, modelDir, viewerPort: 0 })).rejects.toThrow("viewer-port");
    await expect(prepareBrainPreview({ home, modelDir: join(root, "missing-model"), viewerPort: 18829 })).rejects.toThrow();
    await expect(readdir(home)).rejects.toThrow();
  });
});
