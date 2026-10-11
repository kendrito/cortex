import { afterEach, describe, expect, it, vi } from "vitest";
import { createBrainFixture } from "../../helpers/brain-graph-fixture";
import { brainRecordPath } from "../../../viewer/src/components/brain/brain-navigation";

afterEach(() => { vi.unstubAllGlobals(); vi.resetModules(); });

describe("embedded Brain navigation", () => {
  const node = { ...createBrainFixture().nodes[0], source: { view: "memories" as const, id: "record/with ? &" } };

  it("retains escaped standalone links for real local records", () => {
    expect(brainRecordPath(node, { embedded: false })).toBe("#/memories?id=record%2Fwith%20%3F%20%26");
  });

  it("omits routes unavailable inside the Brain-only panel", () => {
    expect(brainRecordPath(node, { embedded: true })).toBeNull();
  });
});

describe("native Brain API mount", () => {
  it("recognizes only the native viewer mount while retaining standalone prefixes", async () => {
    const { detectAgentPrefix } = await import("../../../viewer/src/api/client");
    for (const pathname of ["/api/memos-brain/view/", "/api/memos-brain/view/brain.html"]) {
      expect(detectAgentPrefix(pathname)).toBe("/api/memos-brain");
    }
    for (const pathname of ["/api/memos-brain/", "/api/memos-brain/view-other/", "/api/memos-brain/view", "/", "/brain.html"]) {
      expect(detectAgentPrefix(pathname)).toBe("");
    }
    for (const name of ["memos", "openclaw", "hermes"]) {
      expect(detectAgentPrefix(`/${name}/ui/`)).toBe(`/${name}`);
    }
  });

  it("reads Brain data with the Cortex session and without standalone viewer credentials", async () => {
    vi.stubGlobal("location", { pathname: "/api/memos-brain/view/brain.html" });
    const getItem = vi.fn(() => "separate-viewer-key");
    vi.stubGlobal("localStorage", { getItem });
    const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify({ totals: { memory: 0 } })));
    vi.stubGlobal("fetch", fetch);
    const { api, withAgentPrefix } = await import("../../../viewer/src/api/client");

    expect(await api.get("/api/v1/brain/summary?ownerProfileId=work%20%26%20play")).toEqual({ totals: { memory: 0 } });
    expect(fetch).toHaveBeenCalledWith("/api/memos-brain/api/v1/brain/summary?ownerProfileId=work%20%26%20play", expect.objectContaining({ credentials: "same-origin", redirect: "error" }));
    expect(getItem).not.toHaveBeenCalled();
    expect(fetch.mock.calls[0][1].headers).not.toHaveProperty("x-api-key");
    expect(() => withAgentPrefix("https://example.test/api/v1/brain/summary")).toThrow("local memory API");
    expect(() => withAgentPrefix("/api/v1/../../outbound")).toThrow("local memory API");
  });

  it("keeps standalone local viewer authentication unchanged", async () => {
    vi.stubGlobal("location", { pathname: "/memos/ui/" });
    vi.stubGlobal("localStorage", { getItem: vi.fn(() => "standalone-test-key") });
    const fetch = vi.fn().mockResolvedValue(new Response("{}"));
    vi.stubGlobal("fetch", fetch);
    const { api } = await import("../../../viewer/src/api/client");
    await api.get("/api/v1/brain/summary");
    expect(fetch).toHaveBeenCalledWith("/memos/api/v1/brain/summary", expect.objectContaining({ headers: expect.objectContaining({ "x-api-key": "standalone-test-key" }) }));
  });
});
