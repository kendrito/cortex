/** Synthetic SQLite records for Brain API tests; never seed a real memory home. */
import type { Repos } from "../../core/storage/index.js";

export const BRAIN_SAMPLE_TIME = Date.UTC(2026, 0, 15, 12);

/** Seed two isolated namespaces with grouped traces and grounded evidence links. */
export function seedBrainFixture(repos: Repos) {
  const projects = ["sample/cortex", "sample/design"];
  const fixtures = projects.map((profile, projectIndex) => {
    const prefix = `SAMPLE-${projectIndex}`;
    const owner = {
      ownerAgentKind: "cortex",
      ownerProfileId: profile,
      ownerWorkspaceId: `${prefix}-workspace`,
    };
    const sessionId = `${prefix}-session`;
    const base = BRAIN_SAMPLE_TIME + projectIndex * 86_400_000;
    repos.sessions.upsert({
      id: sessionId, agent: "cortex", ...owner,
      startedAt: base, lastSeenAt: base + 180_000,
      meta: { title: "SAMPLE — local project maintenance", synthetic: true },
    });
    const episodeIds: string[] = [];
    const traceIds: string[] = [];
    for (let episode = 0; episode < 3; episode++) {
      const id = `${prefix}-episode-${episode}`;
      const turn = base + episode * 60_000;
      const traces = [0, 1].map((step) => `${prefix}-trace-${episode}-${step}`);
      episodeIds.push(id);
      traceIds.push(...traces);
      repos.episodes.insert({
        id, sessionId, ...owner, startedAt: turn, endedAt: turn + 30_000,
        status: "closed", traceIds: traces, rTask: 0.8,
        meta: { title: `SAMPLE — maintenance task ${episode + 1}`, synthetic: true },
      });
      traces.forEach((traceId, step) => repos.traces.insert({
        id: traceId, episodeId: id, sessionId, ...owner, ts: turn + step * 1_000,
        turnId: turn, userText: "SAMPLE — inspect a local database migration safely.",
        agentText: step === 0
          ? "SAMPLE — inspected the schema and created a local backup."
          : "SAMPLE — checked the backup and verified the migration result.",
        summary: `SAMPLE — migration ${episode + 1}, step ${step + 1}`,
        toolCalls: [], reflection: "SAMPLE — verify a backup before changing stored data.",
        value: 0.8, alpha: 0.7, rHuman: null, priority: 0,
        tags: ["SAMPLE", "sqlite"], vecSummary: null, vecAction: null, schemaVersion: 1,
      }));
    }
    const policyIds = [0, 1].map((policy) => `${prefix}-policy-${policy}`);
    policyIds.forEach((id, index) => {
      repos.policies.insert({
        id, ...owner, title: `SAMPLE — ${index ? "verify restore" : "backup before migration"}`,
        trigger: "SAMPLE — before changing a local schema",
        procedure: "SAMPLE — create a backup, verify it, then apply the migration.",
        verification: "SAMPLE — the restored copy passes local integrity checks.",
        boundary: "SAMPLE — this fixture describes no real project.",
        support: 2, gain: 0.25, status: "active", confidence: 0.85,
        sourceEpisodeIds: episodeIds.slice(index, index + 2),
        sourceTraceIds: [traceIds[index * 2]!], inducedBy: "synthetic-test-fixture",
        decisionGuidance: { preference: ["SAMPLE — inspect evidence before acting"], antiPattern: [] },
        vec: null, createdAt: base + 200_000, updatedAt: base + 210_000,
      });
      repos.tracePolicyLinks.link({
        traceId: traceIds[index * 2]!, policyId: id,
        episodeId: episodeIds[index]!, now: base + 210_000,
      });
    });
    const worldId = `${prefix}-world`;
    repos.worldModel.insert({
      id: worldId, ...owner, title: "SAMPLE — local persistence model",
      body: "SAMPLE — a local SQLite store has explicit migration and recovery steps.",
      structure: {
        environment: [{ label: "SAMPLE store", description: "A synthetic local SQLite database", evidenceIds: [traceIds[0]!] }],
        inference: [{ label: "SAMPLE recovery", description: "A verified copy enables recovery", evidenceIds: [policyIds[0]!] }],
        constraints: [{ label: "SAMPLE constraint", description: "Do not treat fixtures as learned facts" }],
      },
      domainTags: ["SAMPLE", "sqlite"], confidence: 0.8,
      policyIds, sourceEpisodeIds: episodeIds, inducedBy: "synthetic-test-fixture",
      vec: null, createdAt: base + 220_000, updatedAt: base + 230_000,
      version: 1, status: "active",
    });
    const skillId = `${prefix}-skill`;
    repos.skills.insert({
      id: skillId, ...owner, name: "SAMPLE — migration checklist",
      status: "candidate", invocationGuide: "SAMPLE ONLY: inspect, back up, verify, migrate, check.",
      procedureJson: null, eta: 0.75, support: 2, gain: 0.25,
      trialsAttempted: 0, trialsPassed: 0, sourcePolicyIds: policyIds,
      sourceWorldModelIds: [worldId], evidenceAnchors: [traceIds[0]!, traceIds[2]!],
      vec: null, createdAt: base + 240_000, updatedAt: base + 250_000,
      version: 1, usageCount: 0, lastUsedAt: null,
    });
    return { profile, sessionId, episodeIds, traceIds, policyIds, worldId, skillId };
  });
  return { synthetic: true as const, projects: fixtures };
}
