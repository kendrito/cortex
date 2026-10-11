/** Queued learning retains the Cortex model route belonging to each event. */

import { AsyncLocalStorage } from "node:async_hooks";
import { afterEach, describe, expect, it, vi } from "vitest";
import { DEFAULT_CONFIG } from "../../core/config/defaults.js";
import { rootLogger } from "../../core/logger/index.js";
import { createL2EventBus } from "../../core/memory/l2/events.js";
import { attachL2Subscriber } from "../../core/memory/l2/subscriber.js";
import { createRewardEventBus } from "../../core/reward/events.js";
import type { RewardResult } from "../../core/reward/types.js";
import { createSkillEventBus } from "../../core/skill/events.js";
import { attachSkillSubscriber } from "../../core/skill/subscriber.js";
import type { TraceRow } from "../../core/types.js";
import { makeTmpDb, type TmpDbHandle } from "../helpers/tmp-db.js";

const runners = vi.hoisted(() => ({
  l2: vi.fn<(...args: unknown[]) => Promise<void>>(),
  skill: vi.fn<(...args: unknown[]) => Promise<void>>(),
}));
vi.mock("../../core/memory/l2/l2.js", () => ({ runL2: runners.l2 }));
vi.mock("../../core/skill/skill.js", () => ({ runSkill: runners.skill, applySkillFeedback: vi.fn() }));

const log = rootLogger.child({ channel: "cortex.learning-route-test" });
const route = new AsyncLocalStorage<string>();
let handle: TmpDbHandle | undefined;
afterEach(() => {
  handle?.cleanup();
  handle = undefined;
  vi.useRealTimers();
  vi.restoreAllMocks();
  runners.l2.mockReset();
  runners.skill.mockReset();
});

function deferred(): { promise: Promise<void>; resolve: () => void } {
  let complete = (): void => {};
  const promise = new Promise<void>((resolve) => { complete = resolve; });
  return { promise, resolve: complete };
}

function reward(episodeId = "episode"): RewardResult {
  return {
    episodeId, sessionId: "session", rHuman: 0.8,
    humanScore: {
      rHuman: 0.8, axes: { goalAchievement: 1, processQuality: 1, userSatisfaction: 1 },
      reason: "Synthetic test", source: "heuristic", model: null,
    },
    feedbackCount: 0,
    backprop: { updates: [], meanAbsValue: 0, maxPriority: 0,
      echoParams: { gamma: 0.9, decayHalfLifeDays: 30, now: Date.now() } },
    traceIds: ["trace"], timings: { summary: 0, score: 0, backprop: 0, persist: 0, total: 0 },
    warnings: [], startedAt: Date.now(), completedAt: Date.now(),
  };
}

describe("Cortex model routes during queued learning", () => {
  it("keeps independently queued skill jobs on their originating route", async () => {
    handle = makeTmpDb({ agent: "cortex" });
    const l2Bus = createL2EventBus();
    const blocked = deferred();
    const seen: Array<string | undefined> = [];
    runners.skill.mockImplementation(async () => {
      seen.push(route.getStore());
      if (seen.length === 1) await blocked.promise;
      expect(route.getStore()).toBe(seen.at(-1));
    });
    const subscriber = attachSkillSubscriber({
      repos: handle.repos, embedder: null, llm: null, log,
      bus: createSkillEventBus(), l2Bus, rewardBus: createRewardEventBus(),
      config: DEFAULT_CONFIG.algorithm.skill,
    });
    try {
      for (const model of ["first-provider/first-model", "second-provider/second-model", "third-provider/third-model"]) {
        route.run(model, () => l2Bus.emit({
          kind: "l2.policy.updated", episodeId: "episode", policyId: model,
          status: "active", support: 3, gain: 0.9,
        }));
      }
      blocked.resolve();
      await subscriber.flush();
      expect(seen).toEqual(["first-provider/first-model", "second-provider/second-model", "third-provider/third-model"]);
    } finally { subscriber.dispose(); }
  });

  it("keeps cooldown rewards on each pending event's route instead of the timer's route", async () => {
    vi.useFakeTimers();
    handle = makeTmpDb({ agent: "cortex" });
    vi.spyOn(handle.repos.tracePolicyLinks, "getLinkedPolicyIds").mockReturnValue(["policy"]);
    const rewardBus = createRewardEventBus();
    const seen: Array<string | undefined> = [];
    runners.skill.mockImplementation(async () => { seen.push(route.getStore()); });
    const subscriber = attachSkillSubscriber({
      repos: handle.repos, embedder: null, llm: null, log,
      bus: createSkillEventBus(), l2Bus: createL2EventBus(), rewardBus,
      config: { ...DEFAULT_CONFIG.algorithm.skill, cooldownMs: 100 },
    });
    try {
      route.run("original-model", () => rewardBus.emit({ kind: "reward.updated", result: reward() }));
      await subscriber.flush();
      route.run("pending-model", () => rewardBus.emit({ kind: "reward.updated", result: reward() }));
      // Another reward reuses the timer while replacing its pending job route.
      route.run("latest-model", () => rewardBus.emit({ kind: "reward.updated", result: reward() }));
      await vi.advanceTimersByTimeAsync(100);
      await subscriber.flush();
      expect(seen).toEqual(["original-model", "latest-model"]);
    } finally { subscriber.dispose(); }
  });

  it("keeps coalesced L2 rewards on the latest event's captured route", async () => {
    handle = makeTmpDb({ agent: "cortex" });
    const trace: TraceRow = {
      id: "trace", episodeId: "episode", sessionId: "session", ts: 1,
      userText: "Synthetic task", agentText: "Synthetic outcome", toolCalls: [],
      reflection: "Synthetic reflection", value: 1, alpha: 1, rHuman: 1, priority: 1,
      tags: [], vecSummary: null, vecAction: null, turnId: 1, schemaVersion: 1,
    };
    vi.spyOn(handle.repos.traces, "getById").mockReturnValue(trace);
    const rewardBus = createRewardEventBus();
    const blocked = deferred();
    const seen: Array<string | undefined> = [];
    runners.l2.mockImplementation(async () => {
      seen.push(route.getStore());
      if (seen.length === 1) await blocked.promise;
    });
    const subscriber = attachL2Subscriber({
      db: handle.db, repos: handle.repos, rewardBus, l2Bus: createL2EventBus(), llm: null, log,
      config: DEFAULT_CONFIG.algorithm.l2,
      thresholds: { minSupport: 1, minGain: 0, archiveGain: -0.1 },
    });
    try {
      for (const model of ["original-model", "superseded-model", "latest-model"]) {
        route.run(model, () => rewardBus.emit({ kind: "reward.updated", result: reward() }));
      }
      blocked.resolve();
      await subscriber.drain();
      expect(seen).toEqual(["original-model", "latest-model"]);
    } finally { subscriber.detach(); }
  });
});
