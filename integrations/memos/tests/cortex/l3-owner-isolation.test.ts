/** World-model learning keeps private evidence and queued model routes within one owner. */
import { AsyncLocalStorage } from "node:async_hooks";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { L3_ABSTRACTION_PROMPT } from "../../core/llm/prompts/l3-abstraction.js";
import { rootLogger } from "../../core/logger/index.js";
import { clusterPolicies } from "../../core/memory/l3/cluster.js";
import { createL3EventBus, runL3, type L3Config } from "../../core/memory/l3/index.js";
import { chooseMergeTarget, gatherMergeCandidates } from "../../core/memory/l3/merge.js";
import { attachL3Subscriber } from "../../core/memory/l3/subscriber.js";
import { createL2EventBus } from "../../core/memory/l2/events.js";
import { memoryOwnerKey, type OwnerFields } from "../../core/runtime/namespace.js";
import type { EpisodeId, PolicyId, WorldModelId } from "../../core/types.js";
import { fakeLlm } from "../helpers/fake-llm.js";
import { makeTmpDb, type TmpDbHandle } from "../helpers/tmp-db.js";
import { NOW, seedPolicy, seedTrace, seedWorldModel } from "../unit/memory/l3/_helpers.js";

const OP = `${L3_ABSTRACTION_PROMPT.id}.v${L3_ABSTRACTION_PROMPT.version}`;
const log = rootLogger.child({ channel: "test.cortex.l3-owner-isolation" });
const baseOwner: OwnerFields = { ownerAgentKind: "cortex", ownerProfileId: "profile-A", ownerWorkspaceId: "workspace-A" };
const owners = [baseOwner,
  { ...baseOwner, ownerWorkspaceId: "workspace-B" },
  { ...baseOwner, ownerProfileId: "profile-B" },
  { ...baseOwner, ownerAgentKind: "hermes" },
];
const draft = {
  title: "Private docker environment", domain_tags: ["docker"],
  environment: [{ label: "Local image", description: "Use the owning workspace image" }],
  inference: [], constraints: [], body: "Local docker environment", confidence: 0.75,
  supersedes_world_ids: [],
};
const config: L3Config = {
  minPolicies: 1, minPolicyGain: 0.1, minPolicySupport: 1, clusterMinSimilarity: 0.6,
  policyCharCap: 800, traceCharCap: 500, traceEvidencePerPolicy: 2, useLlm: true,
  cooldownDays: 1, confidenceDelta: 0.05, minConfidenceForRetrieval: 0.2,
};

describe("Cortex L3 owner isolation", () => {
  let handle: TmpDbHandle;
  beforeEach(() => { handle = makeTmpDb({ agent: "cortex" }); });
  afterEach(() => { handle.cleanup(); });

  function seed(label: string, owner: OwnerFields) {
    const trace = seedTrace(handle, { id: `tr-${label}`, episodeId: `ep-${label}`, userText: `private-evidence-${label}` });
    const episode = handle.repos.episodes.getById(trace.episodeId)!;
    handle.repos.episodes.upsert({ ...episode, ...owner });
    handle.repos.traces.upsert({ ...trace, ...owner });
    const policy = { ...seedPolicy(handle, { id: `po-${label}` as PolicyId, title: `docker-policy-${label}`, sourceEpisodeIds: [trace.episodeId] }), ...owner };
    handle.repos.policies.upsert(policy);
    return { trace: { ...trace, ...owner }, episode, policy };
  }

  it("partitions matching domains and vectors by agent, profile, and workspace", () => {
    const policies = owners.flatMap((owner, index) => [seed(`${index}-1`, owner).policy, seed(`${index}-2`, owner).policy]);
    const clusters = clusterPolicies({ policies }, { config: { ...config, minPolicies: 2, maxPoliciesPerCluster: 20 } });
    expect(clusters).toHaveLength(4);
    expect(new Set(clusters.map(cluster => cluster.key)).size).toBe(4);
    for (const cluster of clusters) {
      expect(cluster.policies).toHaveLength(2);
      expect(new Set(cluster.policies.map(memoryOwnerKey)).size).toBe(1);
    }
  });

  it("restricts trigger policies, evidence, explicit merge targets, and cooldown to the exact owner", async () => {
    const a = seed("A", owners[0]!);
    const b = seed("B", owners[1]!);
    seed("C", owners[2]!); seed("D", owners[3]!);
    handle.repos.policies.upsert({ ...a.policy, sourceEpisodeIds: [a.trace.episodeId, b.trace.episodeId] });
    const foreign = { ...seedWorldModel(handle, { id: "wm-foreign" as WorldModelId, domainTags: ["docker"], policyIds: [a.policy.id] }), ...owners[1]! };
    handle.repos.worldModel.upsert(foreign);
    const before = handle.repos.worldModel.getById(foreign.id);
    const prompts: string[] = [];
    const llm = fakeLlm({ completeJson: { [OP]: input => {
      prompts.push(JSON.stringify(input));
      return { ...draft, supersedes_world_ids: [foreign.id] };
    } } });
    const result = await runL3({ trigger: "l2.policy.induced", episodeId: a.trace.episodeId, now: NOW }, { repos: handle.repos, llm, log, config });
    expect(result.abstractions).toHaveLength(1);
    expect(result.abstractions[0]!.createdNew).toBe(true);
    expect(result.abstractions[0]!.policyIds).toEqual([a.policy.id]);
    expect(result.abstractions[0]!.episodeIds).toEqual([a.trace.episodeId]);
    expect(prompts).toHaveLength(1);
    expect(prompts[0]).toContain("private-evidence-A");
    for (const label of ["B", "C", "D"]) expect(prompts[0]).not.toContain(`private-evidence-${label}`);
    expect(handle.repos.worldModel.getById(foreign.id)).toEqual(before);
    const created = handle.repos.worldModel.getById(result.abstractions[0]!.worldModelId!)!;
    expect(memoryOwnerKey(created)).toBe(memoryOwnerKey(owners[0]!));
    const cluster = clusterPolicies({ policies: [a.policy] }, { config: { ...config, maxPoliciesPerCluster: 20 } })[0]!;
    const mergeDeps = { lookup: handle.repos.worldModel, config };
    expect(gatherMergeCandidates(cluster, mergeDeps).some(row => row.id === foreign.id)).toBe(false);
    expect(chooseMergeTarget(cluster, [foreign], { ...draft, domainTags: ["docker"], supersedesWorldIds: [foreign.id] }, mergeDeps)).toEqual({ kind: "create" });
    const second = await runL3({ trigger: "l2.policy.induced", episodeId: b.trace.episodeId, now: NOW + 1 }, { repos: handle.repos, llm, log, config });
    expect(second.abstractions).toHaveLength(1);
    expect(second.abstractions[0]!.skippedReason).toBeNull();
    expect(second.abstractions[0]!.worldModelId).toBe(foreign.id);
    expect(handle.repos.worldModel.getById(created.id)).toEqual(created);
  });

  it("does not learn from unrelated owners when a triggering episode is missing", async () => {
    seed("A", baseOwner);
    const llm = fakeLlm({ completeJson: { [OP]: draft } });
    const result = await runL3({ trigger: "l2.policy.induced", episodeId: "missing" as EpisodeId }, { repos: handle.repos, llm, log, config });
    expect(result.abstractions).toEqual([]);
    expect(result.warnings).toEqual([expect.objectContaining({ stage: "owner" })]);
    expect(llm.stats().requests).toBe(0);
  });

  it.each(["event", "manual"])("keeps queued owners and model contexts separate during a %s run", async entryPoint => {
    const entries = [seed("A", owners[0]!), seed("B", owners[1]!), seed("C", owners[2]!)];
    const context = new AsyncLocalStorage<string>();
    const calls: Array<{ owner: string | undefined; prompt: string }> = [];
    let release!: () => void, started!: () => void;
    const gate = new Promise<void>(resolve => { release = resolve; });
    const firstStarted = new Promise<void>(resolve => { started = resolve; });
    const llm = fakeLlm({ completeJson: { [OP]: async input => {
      const owner = context.getStore();
      calls.push({ owner, prompt: JSON.stringify(input) });
      if (owner === "A") { started(); await gate; }
      return draft;
    } } });
    const l2Bus = createL2EventBus();
    const subscriber = attachL3Subscriber({ repos: handle.repos, l2Bus, l3Bus: createL3EventBus(), llm, log, config: { ...config, cooldownDays: 0 } });
    const emit = (index: number) => {
      const entry = entries[index]!;
      l2Bus.emit({ kind: "l2.policy.induced", episodeId: entry.trace.episodeId, policyId: entry.policy.id, signature: "docker|pip", evidenceTraceIds: [entry.trace.id], evidenceEpisodeIds: [entry.trace.episodeId], title: entry.policy.title });
    };
    try {
      const firstRun = context.run("A", () => entryPoint === "event" ? emit(0)
        : subscriber.runOnce({ episodeId: entries[0]!.trace.episodeId }));
      await firstStarted;
      context.run("B", () => emit(1));
      context.run("C", () => emit(2));
      release();
      await firstRun;
      await subscriber.drain();
      expect(calls.map(call => call.owner)).toEqual(["A", "B", "C"]);
      for (const call of calls) {
        expect(call.prompt).toContain(`private-evidence-${call.owner}`);
        for (const label of ["A", "B", "C"].filter(label => label !== call.owner)) expect(call.prompt).not.toContain(`private-evidence-${label}`);
      }
    } finally { release(); await subscriber.drain(); subscriber.detach(); }
  });
});
