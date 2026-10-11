/** Private evidence remains owned throughout L2 association and induction. */

import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { associateTraces } from "../../core/memory/l2/associate.js";
import { makeCandidatePool } from "../../core/memory/l2/candidate-pool.js";
import { runL2 } from "../../core/memory/l2/l2.js";
import type { L2Config } from "../../core/memory/l2/types.js";
import { rootLogger } from "../../core/logger/index.js";
import type { PolicyRow, TraceRow } from "../../core/types.js";
import type { OwnerFields } from "../../core/runtime/namespace.js";
import { gatherEvidence } from "../../core/skill/evidence.js";
import { fakeLlm } from "../helpers/fake-llm.js";
import { makeTmpDb, type TmpDbHandle } from "../helpers/tmp-db.js";

const now = 1_700_000_000_000;
const log = rootLogger.child({ channel: "cortex.l2-owner-test" });
const primary: OwnerFields = { ownerAgentKind: "cortex", ownerProfileId: "profile:primary", ownerWorkspaceId: "primary" };
const foreign: OwnerFields = { ...primary, ownerWorkspaceId: "foreign" };
const config: L2Config = {
  minSimilarity: 0.8, candidateTtlDays: 30, gamma: 0.9, tauSoftmax: 0.4,
  useLlm: true, minTraceValue: 0.1, minEpisodesForInduction: 2,
  inductionTraceCharCap: 2000, gainEmaAlpha: 0.4,
};

function seedTrace(handle: TmpDbHandle, id: string, owner: OwnerFields): TraceRow {
  const sessionId = `session-${id}`;
  const episodeId = `episode-${id}`;
  handle.repos.sessions.upsert({
    id: sessionId, agent: "cortex", startedAt: now, lastSeenAt: now, meta: {}, ...owner,
  });
  handle.repos.episodes.insert({
    id: episodeId, sessionId, startedAt: now, endedAt: now, status: "closed",
    rTask: 1, traceIds: [], meta: {}, ...owner,
  });
  const trace: TraceRow = {
    id, episodeId, sessionId, ts: now, userText: `Private database migration ${id}`,
    agentText: `Back up SQLite for ${id}`, toolCalls: [], reflection: "Back up before schema changes",
    value: 0.9, alpha: 0.9, rHuman: 1, priority: 1, tags: ["database"],
    vecSummary: new Float32Array([1, 0, 0]), vecAction: null, turnId: now, schemaVersion: 1,
    ...owner,
  };
  handle.repos.traces.insert(trace);
  return trace;
}

function policy(id: string, owner: OwnerFields): PolicyRow {
  return {
    id, title: "SQLite backup", trigger: "Database migration", procedure: "Back up before migration",
    verification: "Backup exists", boundary: "Local database", support: 3, gain: 0.5,
    status: "active", sourceEpisodeIds: [], sourceTraceIds: [], inducedBy: "test",
    decisionGuidance: { preference: [], antiPattern: [] }, vec: new Float32Array([1, 0, 0]),
    createdAt: now, updatedAt: now, ...owner,
  };
}

describe("L2 private memory owners", () => {
  let handle: TmpDbHandle;
  beforeEach(() => { handle = makeTmpDb({ agent: "cortex" }); });
  afterEach(() => { handle.cleanup(); });

  it("filters ownership before vector limits and never associates a foreign private policy", () => {
    const trace = seedTrace(handle, "owned-trace", primary);
    for (let index = 0; index < 25; index++) handle.repos.policies.insert(policy(`foreign-${index}`, foreign));
    const first = associateTraces([trace], { repos: handle.repos, log, config: { minSimilarity: 0.6, poolFactor: 1 } });
    expect(first[0]?.matchedPolicyId).toBeNull();
    handle.repos.policies.insert(policy("owned-policy", primary));
    const second = associateTraces([trace], { repos: handle.repos, log, config: { minSimilarity: 0.6, poolFactor: 1 } });
    expect(second[0]?.matchedPolicyId).toBe("owned-policy");
  });

  it("counts distinct evidence episodes independently for each complete owner", () => {
    const pool = makeCandidatePool({ db: handle.db, repos: handle.repos });
    for (const [id, owner] of [["primary-one", primary], ["foreign-one", foreign]] as const) {
      pool.addCandidate({ trace: seedTrace(handle, id, owner), ttlMs: 1000, now });
    }
    expect(pool.bucketsReadyForInduction({ minDistinctEpisodes: 2, now })).toHaveLength(0);
    pool.addCandidate({ trace: seedTrace(handle, "primary-two", primary), ttlMs: 1000, now });
    const buckets = pool.bucketsReadyForInduction({ minDistinctEpisodes: 2, now });
    expect(buckets).toHaveLength(1);
    expect(buckets[0]?.evidenceTraceIds.sort()).toEqual(["primary-one", "primary-two"]);
  });

  it("does not process another owner's ready induction bucket under the current model route", async () => {
    const pool = makeCandidatePool({ db: handle.db, repos: handle.repos });
    const owned = seedTrace(handle, "primary-current", primary);
    const second = seedTrace(handle, "primary-previous", primary);
    const other = seedTrace(handle, "foreign-previous", foreign);
    const otherTwo = seedTrace(handle, "foreign-current", foreign);
    for (const trace of [second, other, otherTwo]) pool.addCandidate({ trace, ttlMs: 1000, now });
    const llm = fakeLlm({ completeJson: {
      "l2.l2.induction.v2": { title: "SQLite backup", trigger: "Migration", procedure: "Back up SQLite", confidence: 1 },
    } });
    await runL2({ episodeId: owned.episodeId, sessionId: owned.sessionId, traces: [owned], now, trigger: "manual" }, {
      db: handle.db, repos: handle.repos, llm, log, config,
      thresholds: { minSupport: 2, minGain: 0.1, archiveGain: -0.1 },
    });
    const learned = handle.repos.policies.list({ limit: 100 });
    expect(learned).toHaveLength(1);
    expect(learned[0]).toMatchObject(primary);
    expect(learned[0]?.sourceTraceIds?.sort()).toEqual(["primary-current", "primary-previous"]);
    const foreignBuckets = pool.bucketsReadyForInduction({ minDistinctEpisodes: 2, now, owners: [foreign] });
    expect(foreignBuckets).toHaveLength(1);
    expect(foreignBuckets[0]?.evidenceTraceIds.sort()).toEqual(["foreign-current", "foreign-previous"]);
  });

  it("rejects a foreign episode accidentally linked as skill evidence", () => {
    const owned = seedTrace(handle, "skill-owned", primary);
    const other = seedTrace(handle, "skill-foreign", foreign);
    const candidate = { ...policy("skill-policy", primary), sourceEpisodeIds: [owned.episodeId, other.episodeId] };
    const evidence = gatherEvidence(candidate, { repos: handle.repos, config: { evidenceLimit: 10, traceCharCap: 500 } });
    expect(evidence.traces.map((trace) => trace.id)).toEqual([owned.id]);
    expect(evidence.episodeIds).toEqual([owned.episodeId]);
  });
});
