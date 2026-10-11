/** Real local-model and SQLite smoke; opt in with preprovisioned model assets. */

import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve, sep } from "node:path";
import { describe, expect, it, vi } from "vitest";

import type { MemoryCore } from "../../agent-contract/memory-core.js";
import { DEFAULT_CONFIG, resolveHome } from "../../core/config/index.js";
import { createEmbedder } from "../../core/embedding/index.js";
import type { Embedder } from "../../core/embedding/types.js";
import type { HostLlmBridge, HostLlmCompleteInput } from "../../core/llm/host-bridge.js";
import { __resetHostLlmBridgeForTests } from "../../core/llm/host-bridge.js";
import { bootstrapMemoryCore } from "../../core/pipeline/index.js";

const modelDirectory = process.env["MEMOS_TEST_MODEL_DIR"]?.trim();
const fact = "The Cortex release checklist requires a local SQLite backup before every database migration.";

describe.skipIf(!modelDirectory)("Cortex memory with real offline embeddings", () => {
  it("embeds, captures, and recalls through SQLite and the host bridge without runtime fetches", async () => {
    const fetchSpy = vi.spyOn(globalThis, "fetch").mockImplementation(async () => {
      throw new Error("Offline memory smoke attempted a network fetch");
    });
    const tempBase = resolve(tmpdir());
    const tempHome = await mkdtemp(join(tempBase, "cortex-memos-offline-"));
    const home = resolveHome("cortex", tempHome);
    const hostCalls: HostLlmCompleteInput[] = [];
    const host: HostLlmBridge = {
      id: "cortex.offline-smoke.fake-host",
      async complete(input) {
        hostCalls.push(input);
        // All output stays deterministic; generation is exercised without a model API.
        return {
          model: "selected-cortex-test-model",
          durationMs: 1,
          text: JSON.stringify({
            summary: fact,
            kind: "task",
            relation: "new_task",
            confidence: 0.99,
            reason: "Durable project convention",
            ranked: [1],
            sufficient: true,
            scores: [{ idx: 0, reflection_text: fact, alpha: 0.9, usable: true, reason: "Recorded migration requirement" }],
            policies: [],
            world_models: [],
            goal_achievement: 1,
            process_quality: 1,
            user_satisfaction: 1,
            label: "success",
          }),
        };
      },
    };
    const namespace = {
      agentKind: "cortex",
      profileId: "offline-smoke-project",
      workspacePath: join(tempHome, "project"),
    };
    const config = {
      ...DEFAULT_CONFIG,
      embedding: { ...DEFAULT_CONFIG.embedding, model: modelDirectory!, dimensions: 384 },
      algorithm: {
        ...DEFAULT_CONFIG.algorithm,
        retrieval: { ...DEFAULT_CONFIG.algorithm.retrieval, llmFilterMinCandidates: 1 },
      },
    };
    let core: MemoryCore | undefined;
    let embedder: Embedder | undefined;
    try {
      core = await bootstrapMemoryCore({
        agent: "cortex",
        namespace,
        home,
        config,
        hostLlmBridge: host,
        autoRecovery: false,
        initLogging: false,
      });
      await core.init();
      const health = await core.health();
      expect(health.ok).toBe(true);
      expect(health.llm.provider).toBe("host");
      expect(health.embedder).toMatchObject({ available: true, provider: "local", dim: 384 });

      embedder = createEmbedder({ ...config.embedding, provider: "local", dimensions: 384 });
      const [migration, related, unrelated] = await embedder.embedMany([
        "Make a SQLite backup before database migrations.",
        "Before changing the database schema, save a backup of SQLite.",
        "A whale swims through the deep blue ocean.",
      ]);
      expect(migration).toBeInstanceOf(Float32Array);
      expect(migration).toHaveLength(384);
      expect(Array.from(migration!).every(Number.isFinite)).toBe(true);
      expect(Math.hypot(...migration!)).toBeCloseTo(1, 4);
      const similarity = (left: Float32Array, right: Float32Array): number =>
        left.reduce((sum, value, index) => sum + value * right[index]!, 0);
      expect(similarity(migration!, related!)).toBeGreaterThan(similarity(migration!, unrelated!) + 0.2);

      const started = await core.prepareTurn!({
        agent: "cortex", namespace, sessionId: "offline-source-session",
        turnKey: "turn-1", userText: `Remember this project convention: ${fact}`, ts: Date.now(),
      });
      const recorded = await core.onTurnEnd({
        agent: "cortex", namespace, sessionId: started.sessionId, episodeId: started.episodeId,
        agentText: fact, toolCalls: [], ts: Date.now(),
      });
      expect(recorded.traceId).not.toBe("");
      const trace = await core.getTrace(recorded.traceId, namespace);
      expect(trace?.agentText).toContain("SQLite backup");
      expect(trace?.summary).toBe(fact);
      const vectors = await core.embeddingMaintenanceStats();
      expect(vectors.dimension).toBe(384);
      expect(vectors.byKind.trace.ready).toBeGreaterThan(0);

      const recalled = await core.searchMemory({
        agent: "cortex", namespace, sessionId: "offline-next-session",
        query: "What must we do before a Cortex database migration?", reason: "tool_driven",
      });
      expect(recalled.hits.some((hit) => hit.refId === recorded.traceId)).toBe(true);
      expect(recalled.injectedContext).toContain("SQLite backup");
      expect(hostCalls.length).toBeGreaterThan(0);
      expect(hostCalls.some((call) => call.messages.some((message) => message.content.includes("CANDIDATES:")))).toBe(true);
      expect(fetchSpy).not.toHaveBeenCalled();
    } finally {
      try {
        await core?.shutdown();
        await embedder?.close();
        expect(fetchSpy).not.toHaveBeenCalled();
      } finally {
        fetchSpy.mockRestore();
        __resetHostLlmBridgeForTests();
        // Only remove the uniquely created fixture; model assets are outside it.
        if (!resolve(tempHome).startsWith(tempBase + sep)) throw new Error("Temporary memory home escaped its fixture directory");
        await rm(tempHome, { recursive: true, force: true });
      }
    }
  }, 120_000);
});
