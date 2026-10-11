/** Production Cortex lifecycle with real SQLite and preprovisioned local embeddings. */

import { Context } from "@cortex/cordis";
import AgentRegistry, { type Agent, type AgentOptions } from "@cortex/agent";
import LlmRuntime, {
  createUserMessage, LlmAdapter, ToolCallId,
  type GenerateOptions, type LlmResolvedModelInfo, type StreamChunk,
} from "@cortex/llm";
import SessionStore, { SessionId } from "@cortex/session";
import SystemPrompt from "@cortex/system-prompt";
import ToolRuntime from "@cortex/tools";
import Database from "better-sqlite3";
import { createRequire } from "node:module";
import { existsSync } from "node:fs";
import { mkdtemp, mkdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join, resolve, sep } from "node:path";
import { pathToFileURL } from "node:url";
import { describe, expect, it, vi } from "vitest";
import { stringify } from "yaml";

import * as memoryPlugin from "../../adapters/deepseek-harness/index.js";
import { __resetHostLlmBridgeForTests } from "../../core/llm/host-bridge.js";
import { memoryBuffer } from "../../core/logger/index.js";

const modelDirectory = process.env["MEMOS_TEST_MODEL_DIR"]?.trim();
const fact = "Before a Cortex database migration, create a local SQLite backup named migration-violet-73.";
const require = createRequire(import.meta.url);
// Resolve test-only host services from the same built checkout as the adapter's peers.
const cortexRoot = resolve(dirname(require.resolve("@cortex/llm")), "../../../..");
const workingDirectoryModule = join(cortexRoot, "packages/session/working-directory/lib/index.js");
// The standalone memory integration also targets hosts released before directory switching.
const hasWorkingDirectory = existsSync(workingDirectoryModule);
interface ProductionLoop {
  create(id: ReturnType<typeof SessionId>, options: AgentOptions, meta: { cwd: string }): Promise<Agent>;
}

interface ProductionWorkingDirectory {
  set(agent: Agent, path: string): Promise<string>;
}

function textChunks(text: string): StreamChunk[] {
  return [
    { type: "block-start", index: 0, blockType: "text" },
    { type: "text-delta", index: 0, text },
    { type: "block-end", index: 0, block: { type: "text", text } },
    { type: "finish", reason: { kind: "stop" } },
  ];
}

function requestText(options: GenerateOptions): string {
  return options.messages.flatMap((message) => message.content)
    .flatMap((block) => block.type === "text" ? [block.text] : []).join("\n");
}

/** Deterministic generation keeps the real host dispatcher and pipeline repeatable. */
class SelectedModelAdapter extends LlmAdapter {
  readonly requests: GenerateOptions[] = [];
  readonly toolIssued = new Set<string>();

  override async resolveModel(provider: string, model: string): Promise<LlmResolvedModelInfo> {
    return { provider, id: model, name: model };
  }

  override async *stream(options: GenerateOptions): AsyncIterable<StreamChunk> {
    this.requests.push(options);
    if (options.sessionId === undefined) {
      yield* textChunks(JSON.stringify({
        summary: fact, reflection: fact, reflection_text: fact, alpha: 0.95, usable: true,
        kind: "task", relation: "new_task", confidence: 0.99, reason: "Recorded project convention",
        ranked: [1], sufficient: true,
        scores: [{ idx: 0, reflection_text: fact, alpha: 0.95, usable: true, reason: "Durable migration requirement" }],
        title: "Back up SQLite before migration", trigger: "Changing the database schema",
        procedure: fact, verification: "Check the local backup exists and can be opened.",
        rationale: "A restorable backup protects the stored session and memory data.", caveats: [],
        environment: [{ label: "Local SQLite", description: fact, evidenceIds: [] }],
        inference: [], constraints: [], body: fact, domain_tags: ["database"],
        name: "sqlite_migration_backup", display_title: "Back up SQLite",
        steps: [{ title: "Create a local backup", body: fact }],
        policies: [], world_models: [], goal_achievement: 1, process_quality: 1,
        user_satisfaction: 1, label: "success",
      }));
      return;
    }
    if (options.sessionId === "native-memory-reader" && !this.toolIssued.has(options.sessionId)) {
      this.toolIssued.add(options.sessionId);
      const id = ToolCallId("native-memory-search");
      const args = JSON.stringify({ query: "Cortex SQLite migration backup", maxResults: 5 });
      yield { type: "block-start", index: 0, blockType: "tool-call" };
      yield { type: "tool-call-delta", index: 0, id, name: "memos_search", argumentsDelta: args };
      yield { type: "block-end", index: 0, block: { type: "tool-call", id, name: "memos_search", arguments: args } };
      yield { type: "finish", reason: { kind: "tool-calls" } };
      return;
    }
    const text = requestText(options);
    yield* textChunks(options.sessionId === "native-memory-source" || text.includes("migration-violet-73")
      ? fact : "No saved migration convention was supplied.");
  }
}

async function bootMemory(home: string, adapter: SelectedModelAdapter): Promise<Context & { agentLoop: ProductionLoop; workingDirectory?: ProductionWorkingDirectory }> {
  const ctx = new Context();
  try {
    await ctx.plugin(LlmRuntime);
    await ctx.plugin(SessionStore);
    // These production services are test dependencies, not additional memory runtime peers.
    const projection = await import(pathToFileURL(join(cortexRoot, "packages/session/session-projection/lib/index.js")).href);
    const loop = await import(pathToFileURL(join(cortexRoot, "packages/core/agent-loop/lib/index.js")).href);
    await ctx.plugin(projection.default);
    await ctx.plugin(SystemPrompt);
    if (hasWorkingDirectory) {
      const fsLocal = await import(pathToFileURL(join(cortexRoot, "packages/fs/fs-local/lib/index.js")).href);
      const workingDirectory = await import(pathToFileURL(workingDirectoryModule).href);
      await ctx.plugin(fsLocal.default);
      await ctx.plugin(workingDirectory.default);
    }
    await ctx.plugin(ToolRuntime);
    await ctx.plugin(AgentRegistry);
    ctx.effect(() => ctx.llm.registerAdapter(["selected-primary", "selected-secondary"], adapter));
    await ctx.plugin(memoryPlugin, {
      enabled: true, profileId: "native-test", home,
      recallEnabled: true, captureEnabled: true, toolsEnabled: true, hostLlmEnabled: true,
      viewerEnabled: false, viewerPort: 18801, recallTimeoutMs: 3000,
      contextMaxChars: 6000, toolResultMaxChars: 2400, failOnStartupError: true,
    });
    await ctx.plugin(loop.default, { agents: [] });
    return ctx as Context & { agentLoop: ProductionLoop; workingDirectory?: ProductionWorkingDirectory };
  } catch (error) {
    await ctx.fiber.dispose();
    throw error;
  }
}

async function runTurn(ctx: Context, agent: Agent, text: string): Promise<void> {
  const idle = new Promise<void>((complete) => {
    const dispose = ctx.on("agent/status", ({ agent: subject, status }) => {
      if (subject !== agent || status !== "idle") return;
      dispose();
      complete();
    });
  });
  agent.followup(createUserMessage({ content: [{ type: "text", text }], source: { kind: "user" } }));
  await idle;
  await agent.whenIdle();
  const events = agent.session.snapshotEvents();
  const ended = events.filter((event) => event.type === "turn/end").at(-1);
  expect(ended?.type === "turn/end" && ended.data.reason.kind).toBe("completed");
}

describe.skipIf(!modelDirectory)("MemOS through the production Cortex host", () => {
  it("automatically captures, survives restart, recalls into durable context, and runs native memory tools", async () => {
    const fetchSpy = vi.spyOn(globalThis, "fetch").mockImplementation(async () => {
      throw new Error("Native memory test attempted network access");
    });
    const fixtureBase = resolve(tmpdir());
    const fixture = await mkdtemp(join(fixtureBase, "cortex-memos-native-"));
    const previousHome = process.env["MEMOS_HOME"];
    delete process.env["MEMOS_HOME"];
    const home = join(fixture, "memory");
    const workspace = join(fixture, "project");
    const workspaceChild = join(workspace, "src");
    const otherWorkspace = join(fixture, "other-project");
    let ctx: Awaited<ReturnType<typeof bootMemory>> | undefined;
    try {
      await Promise.all([mkdir(home), mkdir(workspaceChild, { recursive: true }), mkdir(otherWorkspace)]);
      await writeFile(join(home, "config.yaml"), stringify({
        embedding: { model: modelDirectory, dimensions: 384 },
        algorithm: { retrieval: { llmFilterMinCandidates: 1 } },
      }));
      const firstAdapter = new SelectedModelAdapter();
      ctx = await bootMemory(home, firstAdapter);
      const source = await ctx.agentLoop.create(SessionId("native-memory-source"), {
        provider: "selected-primary", model: "first-selected-model",
      }, { cwd: workspace });
      await runTurn(ctx, source, `Remember this project convention: ${fact}`);
      // The idle checkpoint drains learning before Cordis retires providers.
      await ctx.fiber.dispose();
      ctx = undefined;
      expect(firstAdapter.requests.some((request) => request.sessionId === undefined),
        JSON.stringify(memoryBuffer().tail({ level: "warn", limit: 10 }))).toBe(true);
      expect(firstAdapter.requests.every((request) => request.provider === "selected-primary"
        && request.model === "first-selected-model")).toBe(true);

      const db = new Database(join(home, "data/memos.db"), { readonly: true });
      try {
        const traces = db.prepare("SELECT user_text, agent_text, summary, reflection, alpha, length(vec_summary) AS vector_bytes FROM traces").all();
        expect(traces).toEqual(expect.arrayContaining([expect.objectContaining({
          user_text: expect.stringContaining("migration-violet-73"),
          agent_text: expect.stringContaining("migration-violet-73"), vector_bytes: 384 * 4,
          reflection: expect.stringContaining("migration-violet-73"), alpha: 0.95,
        })]));
      } finally {
        db.close();
      }

      const secondAdapter = new SelectedModelAdapter();
      ctx = await bootMemory(home, secondAdapter);
      const reader = await ctx.agentLoop.create(SessionId("native-memory-reader"), {
        provider: "selected-secondary", model: "second-selected-model",
      }, { cwd: workspace });
      if (hasWorkingDirectory) {
        const workingDirectory = ctx.get("workingDirectory");
        if (workingDirectory === undefined) throw new Error("Built host did not activate its working-directory service");
        expect(await workingDirectory.set(reader, workspaceChild)).toBe(workspaceChild);
      }
      await runTurn(ctx, reader, "What is the SQLite database migration backup convention for this Cortex project?");
      const events = reader.session.snapshotEvents();
      expect(reader.session.header.cwd).toBe(workspace);
      if (hasWorkingDirectory) {
        expect(events.some((event) => event.type === "working-directory/change"
          && event.data.cwd === workspaceChild)).toBe(true);
      }
      expect(events.some((event) => event.type === "user/message"
        && event.data.source.kind === "memos-local-memory"
        && event.data.content.some((block) => block.type === "text"
          && block.text.includes("<memos_context>") && block.text.includes("migration-violet-73")))).toBe(true);
      expect(events.some((event) => event.type === "tool/call" && event.data.name === "memos_search")).toBe(true);
      expect(events.some((event) => event.type === "assistant/message" && event.data.message.content.some(
        (block) => block.type === "text" && block.text.includes("migration-violet-73"),
      ))).toBe(true);
      expect(secondAdapter.requests.filter((request) => request.sessionId === reader.id)
        .some((request) => requestText(request).includes("<memos_context>"))).toBe(true);
      expect(secondAdapter.requests.every((request) => request.provider === "selected-secondary"
        && request.model === "second-selected-model")).toBe(true);
      expect(secondAdapter.requests.filter((request) => request.sessionId === undefined)
        .every((request) => request.messages.every((message) => message.role !== "user"
          || message.source.kind === "memos-local-memory"))).toBe(true);
      const learnedDb = new Database(join(home, "data/memos.db"), { readonly: true });
      try {
        expect(learnedDb.prepare("SELECT count(*) AS count FROM policies").get()).toMatchObject({ count: 1 });
      } finally {
        learnedDb.close();
      }

      const outsider = await ctx.agentLoop.create(SessionId("native-memory-outsider"), {
        provider: "selected-secondary", model: "second-selected-model",
      }, { cwd: otherWorkspace });
      await runTurn(ctx, outsider, "What is our SQLite migration backup convention?");
      expect(outsider.session.snapshotEvents().some((event) => event.type === "user/message"
        && event.data.source.kind === "memos-local-memory")).toBe(false);
      await ctx.fiber.dispose();
      ctx = undefined;
      expect(fetchSpy).not.toHaveBeenCalled();
    } finally {
      await ctx?.fiber.dispose();
      if (previousHome === undefined) delete process.env["MEMOS_HOME"];
      else process.env["MEMOS_HOME"] = previousHome;
      fetchSpy.mockRestore();
      __resetHostLlmBridgeForTests();
      if (!resolve(fixture).startsWith(fixtureBase + sep)) throw new Error("Native fixture escaped temporary base");
      await rm(fixture, { recursive: true, force: true });
    }
  }, 120_000);
});
