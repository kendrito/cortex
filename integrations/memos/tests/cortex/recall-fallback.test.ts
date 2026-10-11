import { describe, expect, it, vi } from "vitest";
import { llmFilterCandidates, type FilterDeps } from "../../core/retrieval/llm-filter.js";
import type { RankedCandidate } from "../../core/retrieval/ranker.js";
import type { SkillCandidate, TraceCandidate } from "../../core/retrieval/types.js";
import type { LlmClient } from "../../core/llm/index.js";
import { initTestLogger, rootLogger } from "../../core/logger/index.js";

initTestLogger();

function trace(id: string, score: number): RankedCandidate {
  const candidate: TraceCandidate = {
    tier: "tier2", refKind: "trace", refId: id as never,
    cosine: score, ts: 1 as never, vec: null, value: 0.8 as never,
    priority: 0.8 as never, episodeId: "episode" as never,
    sessionId: "session" as never, vecKind: "summary",
    userText: "The synthetic release codename is Indigo Otter 581.",
    agentText: "SAVED", summary: "release codename", reflection: null,
    tags: [], channels: [{ channel: "vec_summary", rank: 0, score }],
  };
  return { candidate, relevance: score, score, rrf: 0, normSq: null };
}

function skill(): RankedCandidate {
  return {
    candidate: {
      tier: "tier1", refKind: "skill", refId: "save-acknowledgment",
      skillName: "exact_save_confirmation", invocationGuide: "Reply SAVED to save requests",
      cosine: 1, vec: null, channels: [{ channel: "pattern", rank: 0, score: 1 }],
    } as SkillCandidate,
    relevance: 1.1059, score: 1.1059, rrf: 0, normSq: null,
  };
}

function deps(completeJson: unknown, maxKeep = 4): FilterDeps {
  return {
    llm: { completeJson } as LlmClient,
    log: rootLogger,
    config: { llmFilterEnabled: true, llmFilterMaxKeep: maxKeep, llmFilterMinCandidates: 1 },
  };
}

describe("Cortex recall when the relevance model cannot finish", () => {
  const query = "What is this synthetic project release codename?";

  it("keeps the admitted fact despite a higher boosted skill, and still prunes weak same-kind matches", async () => {
    const ranked = [skill(), trace("fact", 0.5204), trace("weak", 0.15)];
    const result = await llmFilterCandidates({ query, ranked }, deps(vi.fn().mockRejectedValue(new Error("model timeout"))));
    expect(result.outcome).toBe("llm_failed_safe_cutoff");
    expect(result.kept.map(r => r.candidate.refId)).toEqual(["save-acknowledgment", "fact"]);
    expect(result.dropped.map(r => r.candidate.refId)).toEqual(["weak"]);
    expect(result.sufficient).toBeNull();
  });

  it("uses the same local fallback on an expired foreground deadline without another model request", async () => {
    const completeJson = vi.fn();
    const result = await llmFilterCandidates({ query, ranked: [skill(), trace("fact", 0.5204)] }, {
      ...deps(completeJson), signal: AbortSignal.abort(),
    });
    expect(result.kept).toHaveLength(2);
    expect(completeJson).not.toHaveBeenCalled();
  });

  it("retains the overall output cap, including zero", async () => {
    const ranked = [skill(), trace("fact", 0.5204)];
    const fail = vi.fn().mockRejectedValue(new Error("model timeout"));
    expect((await llmFilterCandidates({ query, ranked }, deps(fail, 1))).kept).toEqual([ranked[0]]);
    expect((await llmFilterCandidates({ query, ranked }, deps(fail, 0))).kept).toEqual([]);
  });

  it("keeps a successful relevance model's empty selection authoritative", async () => {
    const ranked = [skill(), trace("fact", 0.5204)];
    const result = await llmFilterCandidates({ query, ranked }, deps(vi.fn().mockResolvedValue({ value: { selected: [], sufficient: false } })));
    expect(result.outcome).toBe("llm_filtered_empty");
    expect(result.kept).toEqual([]);
    expect(result.dropped).toEqual(ranked);
  });
});
