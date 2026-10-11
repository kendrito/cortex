import { join } from "node:path";

import type { Context } from "@cortex/cordis";
import { describe, expect, it, vi } from "vitest";

import {
  configureDeepSeekHarnessHostLlm,
  deepSeekHarnessAutoRecoveryEnabled,
  deepSeekHarnessMemoryGuidance,
  defaultDeepSeekHarnessHome,
  inject,
  registerDeepSeekHarnessHostLlmCapabilityInvalidation,
} from "../../../adapters/deepseek-harness/index.js";
import type {
  DeepSeekHarnessHostLlmBridge,
} from "../../../adapters/deepseek-harness/host-llm.js";
import { DEFAULT_CONFIG } from "../../../core/config/index.js";

describe("DeepSeek Harness adapter runtime defaults", () => {
  it("injects the DSH LLM service and uses it for an otherwise unconfigured MemOS LLM", () => {
    expect(inject).toContain("llm");

    const configured = configureDeepSeekHarnessHostLlm(DEFAULT_CONFIG, true);
    expect(configured.llm.provider).toBe("host");
    expect(configured.llm.model).toBe("");
    expect(DEFAULT_CONFIG.llm.provider).toBe("");
  });

  it("removes an explicit standalone provider and refuses to disable the host bridge", () => {
    const direct = {
      ...DEFAULT_CONFIG,
      llm: {
        ...DEFAULT_CONFIG.llm,
        provider: "anthropic" as const,
        model: "claude-direct",
      },
    };

    const secured = configureDeepSeekHarnessHostLlm(direct, true);
    expect(secured.llm.provider).toBe("host");
    expect(secured.llm.model).toBe("");
    expect(configureDeepSeekHarnessHostLlm.bind(null, DEFAULT_CONFIG, false)).toThrow("hostLlmEnabled: true");
  });

  it("disables autonomous full-memory recovery when only a turn-scoped host route exists", () => {
    const host = configureDeepSeekHarnessHostLlm(DEFAULT_CONFIG, true);
    expect(deepSeekHarnessAutoRecoveryEnabled(host)).toBe(false);
    expect(deepSeekHarnessAutoRecoveryEnabled({
      ...host,
      algorithm: {
        ...host.algorithm,
        lightweightMemory: {
          ...host.algorithm.lightweightMemory,
          enabled: false,
        },
      },
    })).toBe(false);

    const direct = {
      ...host,
      llm: { ...host.llm, provider: "anthropic" as const },
      algorithm: {
        ...host.algorithm,
        lightweightMemory: {
          ...host.algorithm.lightweightMemory,
          enabled: false,
        },
      },
    };
    expect(deepSeekHarnessAutoRecoveryEnabled(direct)).toBe(false);
  });

  it("places memory under CORTEX_HOME when the adapter home is not explicit", () => {
    expect(defaultDeepSeekHarnessHome("", {
      CORTEX_HOME: "/tmp/isolated-dsh",
    }, "/users/example")).toBe(join("/tmp/isolated-dsh", "memos-plugin"));
    expect(defaultDeepSeekHarnessHome(
      "/data/explicit-memos",
      { CORTEX_HOME: "/tmp/ignored" },
      "/users/example",
    )).toBe("/data/explicit-memos");
    expect(defaultDeepSeekHarnessHome("", { DSH_HOME: "/ignored-upstream-home" }, "/users/example"))
      .toBe(join("/users/example", ".cortex", "memos-plugin"));
  });

  it("does not advertise a disabled tool and marks recalled text as untrusted", () => {
    const withoutTools = deepSeekHarnessMemoryGuidance(false);
    expect(withoutTools).not.toContain("memos_search");
    expect(withoutTools).toContain("untrusted historical data");
    expect(deepSeekHarnessMemoryGuidance(true)).toContain("memos_search");
  });

  it("invalidates host LLM capabilities when DSH adapters are updated", () => {
    let listener: (() => void) | undefined;
    const unregister = vi.fn();
    const ctx = {
      on(event: string, callback: () => void): () => void {
        expect(event).toBe("llm/adapters-updated");
        listener = callback;
        return unregister;
      },
    } as unknown as Context;
    const invalidateModelCapabilities = vi.fn();
    const bridge = {
      invalidateModelCapabilities,
    } as unknown as DeepSeekHarnessHostLlmBridge;

    const registered = registerDeepSeekHarnessHostLlmCapabilityInvalidation(
      ctx,
      bridge,
    );
    listener?.();

    expect(registered).toBe(unregister);
    expect(invalidateModelCapabilities).toHaveBeenCalledOnce();
  });
});
