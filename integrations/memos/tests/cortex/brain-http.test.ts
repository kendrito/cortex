/** Real SQLite + authenticated HTTP acceptance, with no model or embedding runtime. */
import { request } from "node:http";
import { randomBytes } from "node:crypto";
import { mkdtemp, mkdir, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve, sep } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { BrainPage, BrainSummary, BrainEdgePage } from "../../agent-contract/brain.js";
import type { MemoryCore } from "../../agent-contract/memory-core.js";
import { createBrainReader } from "../../core/brain/read.js";
import { initTestLogger } from "../../core/logger/index.js";
import { openDb, runMigrations, makeRepos, type StorageDb } from "../../core/storage/index.js";
import { startHttpServer } from "../../server/index.js";
import type { ServerHandle } from "../../server/types.js";
import { seedBrainFixture } from "../helpers/brain-fixture.js";

function call<T>(url: string, headers: Record<string, string> = {}, method = "GET", body = "") {
  return new Promise<{ status: number; value: T; headers: Record<string, string | string[] | undefined> }>((resolveCall, reject) => {
    const req = request(url, { method, headers }, (res) => {
      let text = "";
      res.setEncoding("utf8");
      res.on("data", (chunk: string) => { text += chunk; });
      res.on("end", () => resolveCall({ status: res.statusCode ?? 0, value: JSON.parse(text) as T, headers: res.headers }));
    });
    req.on("error", reject);
    req.end(body);
  });
}

describe("Brain over authenticated local HTTP", () => {
  let root = "";
  let db: StorageDb;
  let server: ServerHandle;
  let cookie = "";
  let writesBefore = 0;
  let fetchSpy: ReturnType<typeof vi.spyOn>;
  beforeEach(async () => {
    initTestLogger();
    root = await mkdtemp(join(tmpdir(), "cortex-brain-http-"));
    const memoryHome = join(root, "memos-plugin");
    await mkdir(memoryHome);
    db = openDb({ filepath: join(memoryHome, "memos.db"), agent: "cortex" });
    runMigrations(db);
    seedBrainFixture(makeRepos(db));
    // One namespace without a workspace shares the same agent/profile as the remaining records.
    for (const table of ["traces", "episodes", "policies", "world_model", "skills"]) {
      db.raw.prepare(`UPDATE ${table} SET owner_workspace_id=NULL WHERE id IN (
        'SAMPLE-0-trace-0-0','SAMPLE-0-trace-0-1','SAMPLE-0-episode-0',
        'SAMPLE-0-policy-0','SAMPLE-0-world','SAMPLE-0-skill')`).run();
    }
    writesBefore = db.prepare<Record<string, never>, { count: number }>("SELECT total_changes() AS count").get({})!.count;
    const reader = createBrainReader(db);
    const core = {
      readBrainSummary: async (query) => reader.summary(query),
      readBrainPage: async (query) => reader.page(query),
      readBrainEdges: async (query) => reader.edges(query),
    } as MemoryCore;
    server = await startHttpServer({ core, home: { root: memoryHome } }, { agent: "cortex", closeActiveSseOnShutdown: true });
    // Seeded HTTP fixtures use ordinary authentication directly. The empty-only
    // preview launcher must never be relaxed to accept a seeded database.
    const password = randomBytes(32).toString("base64url");
    const setup = await call(`${server.url}/api/v1/auth/setup`, { "content-type": "application/json", origin: server.url }, "POST", JSON.stringify({ password }));
    expect(setup.status).toBe(200);
    const login = await call(`${server.url}/api/v1/auth/login`, { "content-type": "application/json" }, "POST", JSON.stringify({ password }));
    expect(login.status).toBe(200);
    const cookies = login.headers["set-cookie"];
    cookie = (Array.isArray(cookies) ? cookies : [cookies ?? ""]).map(value => value.split(";")[0]).join("; ");
    fetchSpy = vi.spyOn(globalThis, "fetch").mockRejectedValue(new Error("Brain reads must not invoke model/network services"));
  });
  afterEach(async () => {
    try {
      if (fetchSpy) expect(fetchSpy).not.toHaveBeenCalled();
      if (db) expect(db.prepare<Record<string, never>, { count: number }>("SELECT total_changes() AS count").get({})!.count).toBe(writesBefore);
    } finally {
      vi.restoreAllMocks();
      await server?.close();
      db?.close();
      if (!resolve(root).startsWith(resolve(tmpdir()) + sep)) throw new Error("Brain fixture escaped temporary storage");
      await rm(root, { recursive: true, force: true });
    }
  });

  it("requires the viewer cookie and rejects cross-origin requests", async () => {
    for (const path of ["summary", "nodes?kind=memory", "edges?nodeId=missing"]) {
      expect((await call(`${server.url}/api/v1/brain/${path}`)).status).toBe(401);
      expect((await call(`${server.url}/api/v1/brain/${path}`, { cookie, origin: "https://example.invalid", "sec-fetch-site": "cross-site" })).status).toBe(403);
    }
    const summary = await call<BrainSummary>(`${server.url}/api/v1/brain/summary`, { cookie, origin: server.url });
    expect(summary.status).toBe(200);
    expect(summary.headers["cache-control"]).toBe("no-store");
    expect(summary.value.totals).toEqual({ memory: 6, trace: 12, episode: 6, policy: 4, world: 2, skill: 2 });
  });

  it("pages grouped memories and exposes stored evidence in the selected namespace", async () => {
    const filter = "ownerAgentKind=cortex&ownerProfileId=sample%2Fcortex";
    const page = await call<BrainPage>(`${server.url}/api/v1/brain/nodes?kind=memory&limit=2&${filter}`, { cookie });
    expect(page.status).toBe(200);
    expect(page.value.total).toBe(3);
    expect(page.value.nodes).toHaveLength(2);
    expect(page.value.nodes.every(node => node.traceCount === 2 && node.namespace.ownerProfileId === "sample/cortex")).toBe(true);
    expect(page.value.nextCursor).toBeTruthy();
    const next = await call<BrainPage>(`${server.url}/api/v1/brain/nodes?kind=memory&limit=2&${filter}&cursor=${encodeURIComponent(page.value.nextCursor!)}`, { cookie });
    expect(next.status).toBe(200);
    expect(next.value.nodes).toHaveLength(1);
    expect(new Set([...page.value.nodes, ...next.value.nodes].map(node => node.id)).size).toBe(3);
    for (const kind of ["episode", "policy", "world", "skill"]) {
      const result = await call<BrainPage>(`${server.url}/api/v1/brain/nodes?kind=${kind}&${filter}`, { cookie });
      expect(result.status).toBe(200);
      expect(result.value.nodes.length).toBeGreaterThan(0);
      expect(result.value.nodes.every(node => node.namespace.ownerProfileId === "sample/cortex")).toBe(true);
      expect(result.value.nodes.every(node => node.title.includes("SAMPLE") || node.content.includes("SAMPLE"))).toBe(true);
      if (kind === "skill") {
        const evidence = await call<BrainEdgePage>(`${server.url}/api/v1/brain/edges?nodeId=${encodeURIComponent(result.value.nodes[0]!.id)}&${filter}`, { cookie });
        expect(evidence.status).toBe(200);
        expect(evidence.value.total).toBeGreaterThanOrEqual(5);
        expect(evidence.value.edges.every(edge => edge.targetStatus === "available")).toBe(true);
      }
    }
  });

  it.each([
    "nodes?kind=invalid", "nodes?limit=0", "nodes?limit=101",
    "nodes?cursor=not-a-valid-cursor", "edges", "summary?namespaceCursor=invalid",
    "summary?ownerWorkspaceIsNull=0", "summary?ownerWorkspaceIsNull=",
    "summary?ownerWorkspaceIsNull=true", "summary?ownerWorkspaceIsNull=1&ownerWorkspaceIsNull=1",
    "summary?ownerWorkspaceIsNull=1&ownerWorkspaceId=workspace",
    "nodes?ownerWorkspaceIsNull=1&ownerWorkspaceId=",
    "edges?nodeId=skill%3ASAMPLE-0-skill&ownerWorkspaceIsNull=1&ownerWorkspaceId=workspace",
  ])("returns a client error for an invalid Brain query: %s", async path => {
    const response = await call(`${server.url}/api/v1/brain/${path}`, { cookie });
    expect(response.status).toBe(400);
  });

  it("selects null workspaces explicitly without matching the same profile's other workspaces", async () => {
    const profile = "ownerAgentKind=cortex&ownerProfileId=sample%2Fcortex";
    const nullFilter = `${profile}&ownerWorkspaceIsNull=1`;
    const nullSummary = await call<BrainSummary>(`${server.url}/api/v1/brain/summary?${nullFilter}`, { cookie });
    expect(nullSummary.status).toBe(200);
    expect(nullSummary.value.totals).toEqual({ memory: 1, trace: 2, episode: 1, policy: 1, world: 1, skill: 1 });
    expect(nullSummary.value.namespaces).toEqual([{ ownerAgentKind: "cortex", ownerProfileId: "sample/cortex", ownerWorkspaceId: null }]);
    const workspaceSummary = await call<BrainSummary>(`${server.url}/api/v1/brain/summary?${profile}&ownerWorkspaceId=SAMPLE-0-workspace`, { cookie });
    expect(workspaceSummary.value.totals.memory).toBe(2);
    const allSummary = await call<BrainSummary>(`${server.url}/api/v1/brain/summary?${profile}`, { cookie });
    expect(allSummary.value.totals.memory).toBe(3);
    for (const kind of ["memory", "episode", "policy", "world", "skill"]) {
      const page = await call<BrainPage>(`${server.url}/api/v1/brain/nodes?kind=${kind}&${nullFilter}`, { cookie });
      expect(page.status).toBe(200);
      expect(page.value.total).toBe(1);
      expect(page.value.nodes).toHaveLength(1);
      expect(page.value.nodes[0]!.namespace.ownerWorkspaceId).toBeNull();
    }
    const evidenceUrl = `${server.url}/api/v1/brain/edges?nodeId=skill%3ASAMPLE-0-skill`;
    const evidence = await call<BrainEdgePage>(`${evidenceUrl}&${nullFilter}&limit=1`, { cookie });
    const rest = await call<BrainEdgePage>(`${evidenceUrl}&${nullFilter}&cursor=${encodeURIComponent(evidence.value.nextCursor!)}`, { cookie });
    expect(evidence.status).toBe(200);
    expect(rest.status).toBe(200);
    expect([...evidence.value.edges, ...rest.value.edges].some(edge => edge.targetStatus === "outside-filter")).toBe(true);
    expect((await call(`${evidenceUrl}&${profile}&cursor=${encodeURIComponent(evidence.value.nextCursor!)}`, { cookie })).status).toBe(400);
    const allPage = await call<BrainPage>(`${server.url}/api/v1/brain/nodes?kind=memory&${profile}&limit=1`, { cookie });
    expect((await call(`${server.url}/api/v1/brain/nodes?kind=memory&${nullFilter}&cursor=${encodeURIComponent(allPage.value.nextCursor!)}`, { cookie })).status).toBe(400);
  });
});
