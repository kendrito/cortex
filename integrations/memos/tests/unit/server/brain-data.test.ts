import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createBrainReader, BrainQueryError } from "../../../core/brain/read.js";
import { openDb } from "../../../core/storage/connection.js";
import { runMigrations } from "../../../core/storage/migrator.js";
import type { StorageDb } from "../../../core/storage/types.js";
import type { BrainKind } from "../../../agent-contract/brain.js";

let db: StorageDb;
let brain: ReturnType<typeof createBrainReader>;

function task(id: string, profile = "project-a") {
  db.raw.prepare(`INSERT INTO sessions(id,agent,owner_agent_kind,owner_profile_id,owner_workspace_id,started_at,last_seen_at)
    VALUES(?, 'cortex','cortex',?,'workspace',1,1)`).run(`session-${id}`, profile);
  db.raw.prepare(`INSERT INTO episodes(id,session_id,owner_agent_kind,owner_profile_id,owner_workspace_id,started_at,status)
    VALUES(?,?,'cortex',?,'workspace',1,'closed')`).run(id, `session-${id}`, profile);
}
function trace(id: string, episode = "ep-1", turn = 1, profile = "project-a", text = "Remember local tests") {
  db.raw.prepare(`INSERT INTO traces(id,episode_id,session_id,owner_agent_kind,owner_profile_id,owner_workspace_id,
    ts,user_text,agent_text,summary,turn_id,agent_thinking,tool_calls_json)
    VALUES(?,?,?,'cortex',?,'workspace',?,?,'Stored reply','Local test learning',?,'PRIVATE_MODEL_THINKING',?)`)
    .run(id, episode, `session-${episode}`, profile, turn, text, turn,
      JSON.stringify([{ arguments: "PRIVATE_RAW_TOOL_DATA" }]));
}
function policy(id: string, profile = "project-a", traces: string[] = [], episodes: string[] = []) {
  db.raw.prepare(`INSERT INTO policies(id,owner_agent_kind,owner_profile_id,owner_workspace_id,title,trigger,procedure,
    verification,boundary,status,source_trace_ids_json,source_episodes_json,created_at,updated_at,confidence)
    VALUES(?,'cortex',?,'workspace','Test policy','On change','Run tests','Tests pass','Local only','archived',?,?,2,3,0.8)`)
    .run(id, profile, JSON.stringify(traces), JSON.stringify(episodes));
}
function world(id: string, policies = ["po-1"], structure = {}) {
  db.raw.prepare(`INSERT INTO world_model(id,owner_agent_kind,owner_profile_id,owner_workspace_id,title,body,policy_ids_json,
    structure_json,created_at,updated_at,status) VALUES(?,'cortex','project-a','workspace','Repository knowledge','Use local tests',?,?,4,5,'active')`)
    .run(id, JSON.stringify(policies), JSON.stringify(structure));
}
function skill(id: string, policies: string[] = [], worlds: string[] = [], evidence: string[] = [], profile = "project-a") {
  db.raw.prepare(`INSERT INTO skills(id,owner_agent_kind,owner_profile_id,owner_workspace_id,name,status,invocation_guide,
    source_policies_json,source_world_json,evidence_anchors_json,created_at,updated_at)
    VALUES(?,'cortex',?,'workspace',?,'active','Run the local suite',?,?,?,6,7)`)
    .run(id, profile, `Skill ${id}`, JSON.stringify(policies), JSON.stringify(worlds), JSON.stringify(evidence));
}

beforeEach(() => {
  db = openDb({ filepath: ":memory:", agent: "cortex", wal: false });
  runMigrations(db);
  brain = createBrainReader(db);
});
afterEach(() => { db.close(); vi.restoreAllMocks(); });

describe("Brain stored learning projection", () => {
  it("reports an empty store without fabricated nodes or links", () => {
    expect(brain.summary()).toMatchObject({ consistency: "live", totals: { memory: 0, trace: 0, episode: 0, policy: 0, world: 0, skill: 0 }, namespaces: [], namespacesNextCursor: null });
    for (const kind of ["memory", "episode", "policy", "world", "skill"] as const) {
      expect(brain.page({ kind })).toMatchObject({ total: 0, nodes: [], edges: [], nextCursor: null });
    }
  });

  it("groups stored turns and exposes real evidence, dates, ownership, confidence and full-view sources", () => {
    task("ep-1"); trace("tr-1"); trace("tr-2");
    db.raw.prepare("UPDATE episodes SET trace_ids_json=? WHERE id='ep-1'").run('["tr-1","tr-2"]');
    policy("po-1", "project-a", ["tr-1"], ["ep-1"]);
    world("wm-1", ["po-1"], { environment: [{ label: "tests", description: "local", evidenceIds: ["tr-2", "po-1"] }], inference: [], constraints: [] });
    skill("sk-1", ["po-1"], ["wm-1"], ["tr-1"]);
    expect(brain.summary().totals).toEqual({ memory: 1, trace: 2, episode: 1, policy: 1, world: 1, skill: 1 });
    const memory = brain.page({ kind: "memory" });
    expect(memory.nodes[0]).toMatchObject({ kind: "memory", traceCount: 2, content: "Remember local tests\nStored reply", createdAt: 1, updatedAt: 1,
      namespace: { ownerAgentKind: "cortex", ownerProfileId: "project-a", ownerWorkspaceId: "workspace" },
      source: { view: "memories", id: "tr-1", episodeId: "ep-1", sessionId: "session-ep-1" } });
    expect(memory.edges[0]).toMatchObject({ source: memory.nodes[0].id, target: "episode:ep-1", relation: "part_of", targetStatus: "available" });
    const policies = brain.page({ kind: "policy" });
    expect(policies.nodes[0]).toMatchObject({ confidence: 0.8, status: "archived", createdAt: 2, updatedAt: 3 });
    expect(policies.edges.map(edge => edge.target)).toContain(memory.nodes[0].id);
    const worlds = brain.page({ kind: "world" });
    expect(worlds.edges).toHaveLength(3);
    expect(worlds.edges.find(edge => edge.referenceId === "tr-2")).toMatchObject({ target: memory.nodes[0].id, sourceField: "structure.environment.0.evidenceIds" });
    expect(brain.page({ kind: "skill" }).edges.map(edge => edge.target).sort()).toEqual([memory.nodes[0].id, "policy:po-1", "world:wm-1"].sort());
  });

  it("does not confuse unloaded, deleted or cross-namespace references", () => {
    task("ep-1"); trace("tr-1"); policy("po-1", "project-a", ["tr-1", "tr-gone", "tr-gone"]);
    policy("po-other", "project-b");
    world("wm-1", ["po-1", "po-other", "po-gone"], { constraints: [{ label: "unknown", evidenceIds: ["untyped-gone"] }] });
    const page = brain.page({ kind: "world", ownerProfileId: "project-a" });
    expect(page.edges.find(edge => edge.referenceId === "po-1")).toMatchObject({ targetStatus: "available", target: "policy:po-1" });
    expect(page.edges.find(edge => edge.referenceId === "po-other")).toMatchObject({ targetStatus: "outside-filter" });
    expect(page.edges.find(edge => edge.referenceId === "po-gone")).toMatchObject({ targetStatus: "missing", targetKind: "policy" });
    expect(page.edges.find(edge => edge.referenceId === "untyped-gone")).toMatchObject({ targetStatus: "missing", targetKind: "unknown" });
    expect(brain.page({ kind: "policy", ownerProfileId: "project-a" }).edges).toHaveLength(2);
    db.raw.prepare("DELETE FROM traces WHERE id='tr-1'").run();
    expect(brain.page({ kind: "policy", ownerProfileId: "project-a" }).edges.every(edge => edge.targetStatus === "missing")).toBe(true);
    expect(() => brain.edges({ nodeId: "world:deleted" })).toThrow(BrainQueryError);
    expect(() => brain.edges({ nodeId: "policy:po-other", ownerProfileId: "project-a" })).toThrow(/outside/);
  });

  it("keeps turn identities separate by namespace and reads actual policy trace links", () => {
    task("ep-1");
    trace("tr-a", "ep-1", 1, "project-a", "Project A text");
    trace("tr-b", "ep-1", 1, "project-b", "Project B text");
    trace("tr-null", "ep-1", 1, "project-a", "Unscoped workspace text");
    db.raw.prepare("UPDATE traces SET owner_workspace_id=NULL WHERE id='tr-null'").run();
    policy("po-1");
    db.raw.prepare("INSERT INTO trace_policy_links(trace_id,policy_id,episode_id,created_at) VALUES('tr-b','po-1','ep-1',10)").run();
    const memory = brain.page({ kind: "memory" });
    expect(memory.total).toBe(3);
    expect(new Set(memory.nodes.map(entry => entry.id)).size).toBe(3);
    expect(memory.nodes.every(entry => entry.traceCount === 1)).toBe(true);
    const scoped = brain.page({ kind: "memory", ownerProfileId: "project-a", ownerWorkspaceId: "workspace" });
    expect(scoped.nodes).toHaveLength(1);
    expect(scoped.nodes[0].content).toBe("Project A text\nStored reply");
    expect(brain.page({ kind: "episode" }).nodes[0].content).toBe("Project A text");
    const links = brain.page({ kind: "policy", ownerProfileId: "project-a" }).edges;
    expect(links).toHaveLength(1);
    expect(links[0]).toMatchObject({ referenceId: "tr-b", sourceField: "tracePolicyLinks", targetStatus: "outside-filter" });
    db.raw.prepare("DELETE FROM traces WHERE id='tr-a'").run();
    expect(brain.page({ kind: "episode" }).nodes[0].content).toBe("");
  });

  it("bounds turn payloads without losing the raw trace count or evidence continuation", () => {
    task("ep-1");
    db.raw.transaction(() => { for (let i = 0; i < 1_500; i++) trace(`tr-${String(i).padStart(4, "0")}`, "ep-1", 1, "project-a", "A".repeat(5_000)); })();
    policy("po-1", "project-a", Array.from({ length: 31 }, (_, i) => `tr-${String(i).padStart(4, "0")}`));
    const memories = brain.page({ kind: "memory" });
    expect(memories.nodes).toHaveLength(1);
    expect(memories.nodes[0]).toMatchObject({ traceCount: 1_500, contentTruncated: true });
    expect(memories.nodes[0].content).toHaveLength(4_000);
    const page = brain.page({ kind: "policy" });
    expect(page.edges).toHaveLength(12);
    expect(page.nodes[0].evidence).toMatchObject({ total: 31, shown: 12 });
    const remainder = brain.edges({ nodeId: page.nodes[0].id, cursor: page.nodes[0].evidence.nextCursor!, limit: 100 });
    expect(remainder.edges).toHaveLength(19);
    expect(remainder.nextCursor).toBeNull();
    expect(new Set([...page.edges, ...remainder.edges].map(edge => edge.referenceId)).size).toBe(31);
  });

  it("reaches every skill beyond the old export limit with stable keyset pages", () => {
    db.raw.transaction(() => { for (let i = 0; i < 5_003; i++) skill(`sk-${String(i).padStart(5, "0")}`); })();
    let cursor: string | undefined;
    const ids = new Set<string>();
    do {
      const page = brain.page({ kind: "skill", limit: 100, cursor });
      expect(page.total).toBe(5_003);
      for (const entry of page.nodes) ids.add(entry.id);
      cursor = page.nextCursor ?? undefined;
    } while (cursor);
    expect(ids.size).toBe(5_003);
    expect(brain.summary().totals.skill).toBe(5_003);
  });

  it("keeps keyset progress after deletes and rejects reused or malformed cursors", () => {
    skill("a"); skill("b"); skill("c");
    const first = brain.page({ kind: "skill", limit: 1 });
    db.raw.prepare("DELETE FROM skills WHERE id='a'").run();
    skill("d");
    expect(brain.page({ kind: "skill", cursor: first.nextCursor!, limit: 100 }).nodes.map(n => n.rawId)).toEqual(["b", "c", "d"]);
    expect(() => brain.page({ kind: "policy", cursor: first.nextCursor! })).toThrow(/cursor/);
    expect(() => brain.page({ kind: "skill", ownerProfileId: "other", cursor: first.nextCursor! })).toThrow(/cursor/);
    expect(() => brain.page({ kind: "skill", cursor: "not%base64" })).toThrow(/cursor/);
    expect(() => brain.page({ kind: "skill", limit: 101 })).toThrow(/limit/);
    expect(() => brain.edges({ nodeId: "memory:not-a-group" })).toThrow(/identity/);
  });

  it("paginates namespace discovery and keeps exact filtered counts", () => {
    db.raw.transaction(() => { for (let i = 0; i < 105; i++) skill(`sk-${i}`, [], [], [], `project-${i}`); })();
    const first = brain.summary();
    expect(first.namespaces).toHaveLength(100);
    const rest = brain.summary({ namespaceCursor: first.namespacesNextCursor! });
    expect(rest.namespaces).toHaveLength(5);
    expect(rest.namespacesNextCursor).toBeNull();
    expect(brain.summary({ ownerProfileId: "project-4" }).totals.skill).toBe(1);
    expect(brain.summary({ ownerProfileId: "' OR 1=1 --" }).totals.skill).toBe(0);
  });

  it("distinguishes null workspace from all workspaces in totals, pages, evidence and cursors", () => {
    task("ep-1"); trace("tr-null"); trace("tr-workspace", "ep-1", 2);
    policy("po-null", "project-a", ["tr-null", "tr-workspace"]);
    policy("po-workspace");
    skill("a"); skill("b"); skill("c");
    db.raw.prepare("UPDATE traces SET owner_workspace_id=NULL WHERE id='tr-null'").run();
    db.raw.prepare("UPDATE policies SET owner_workspace_id=NULL WHERE id='po-null'").run();
    db.raw.prepare("UPDATE skills SET owner_workspace_id=NULL WHERE id IN ('a','b')").run();
    const nullScope = { ownerAgentKind: "cortex", ownerProfileId: "project-a", ownerWorkspaceId: null };
    const workspaceScope = { ...nullScope, ownerWorkspaceId: "workspace" };
    const allScope = { ownerAgentKind: "cortex", ownerProfileId: "project-a" };
    expect(brain.summary(nullScope).totals).toEqual({ memory: 1, trace: 1, episode: 0, policy: 1, world: 0, skill: 2 });
    expect(brain.summary(workspaceScope).totals).toEqual({ memory: 1, trace: 1, episode: 1, policy: 1, world: 0, skill: 1 });
    expect(brain.summary(allScope).totals).toEqual({ memory: 2, trace: 2, episode: 1, policy: 2, world: 0, skill: 3 });
    expect(brain.summary(nullScope).namespaces).toEqual([nullScope]);
    expect(brain.page({ kind: "memory", ...nullScope }).nodes.map(node => node.rawId)).toEqual(["tr-null"]);
    const first = brain.page({ kind: "skill", ...nullScope, limit: 1 });
    expect(first.nodes.map(node => node.rawId)).toEqual(["a"]);
    expect(brain.page({ kind: "skill", ...nullScope, cursor: first.nextCursor! }).nodes.map(node => node.rawId)).toEqual(["b"]);
    for (const scope of [allScope, workspaceScope]) {
      expect(() => brain.page({ kind: "skill", ...scope, cursor: first.nextCursor! })).toThrow(/cursor/);
    }
    const evidence = brain.edges({ nodeId: "policy:po-null", ...nullScope, limit: 1 });
    const remainder = brain.edges({ nodeId: "policy:po-null", ...nullScope, cursor: evidence.nextCursor! });
    expect([...evidence.edges, ...remainder.edges].map(edge => [edge.referenceId, edge.targetStatus])).toEqual([
      ["tr-null", "available"], ["tr-workspace", "outside-filter"],
    ]);
    expect(() => brain.edges({ nodeId: "policy:po-null", ...allScope, cursor: evidence.nextCursor! })).toThrow(/cursor/);
    expect(brain.edges({ nodeId: "policy:po-null", ...allScope }).edges.every(edge => edge.targetStatus === "available")).toBe(true);
    expect(() => brain.edges({ nodeId: "policy:po-workspace", ...nullScope })).toThrow(/outside/);
  });

  it("executes with SQLite query-only and no fetch while omitting raw tool data and native thinking", () => {
    task("ep-1"); trace("tr-1"); policy("po-1", "project-a", ["tr-1"]);
    const fetch = vi.spyOn(globalThis, "fetch").mockImplementation(() => { throw new Error("Network forbidden"); });
    db.raw.pragma("query_only=ON");
    const before = db.raw.prepare("SELECT total_changes() AS n").get();
    brain.summary();
    for (const kind of ["memory", "episode", "policy", "world", "skill"] satisfies BrainKind[]) {
      const page = brain.page({ kind });
      expect(JSON.stringify(page)).not.toContain("PRIVATE_MODEL_THINKING");
      expect(JSON.stringify(page)).not.toContain("PRIVATE_RAW_TOOL_DATA");
    }
    expect(db.raw.prepare("SELECT total_changes() AS n").get()).toEqual(before);
    expect(fetch).not.toHaveBeenCalled();
  });
});
