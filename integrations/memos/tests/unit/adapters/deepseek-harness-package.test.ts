import { readFile } from "node:fs/promises";
import { resolve } from "node:path";

import { parse } from "yaml";
import { describe, expect, it } from "vitest";

const root = resolve(import.meta.dirname, "../../..");

describe("DeepSeek Harness bundle package", () => {
  it("declares a DSH bundle patch and mounts the compiled adapter", async () => {
    const packageJson = JSON.parse(
      await readFile(resolve(root, "package.json"), "utf8"),
    ) as {
      cortex?: { bundle?: { patch?: string } };
      files?: string[];
    };

    expect(packageJson.cortex?.bundle?.patch).toBe(
      "./adapters/deepseek-harness/cordis.patch.yml",
    );
    expect(packageJson.files).toContain(
      "adapters/deepseek-harness/cordis.patch.yml",
    );

    const patchPath = resolve(root, packageJson.cortex!.bundle!.patch!);
    const rows = parse(await readFile(patchPath, "utf8")) as Array<{
      insert?: Array<{ id?: string; name?: string; config?: Record<string, unknown> }>;
    }>;
    expect(rows).toEqual([
      {
        insert: [
          expect.objectContaining({
            id: "memos-local-memory",
            name: "@cortex/memos-local-memory/dist/adapters/deepseek-harness/index.js",
            config: expect.objectContaining({
              enabled: true,
              recallEnabled: true,
              captureEnabled: true,
              toolsEnabled: true,
              hostLlmEnabled: true,
              viewerEnabled: true,
              viewerPort: 18_801,
              recallTimeoutMs: 3_000,
            }),
          }),
        ],
      },
    ]);
  });
});
