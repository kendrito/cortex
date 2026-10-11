import { describe, expect, it } from "vitest";
import { createBrainFixture } from "../helpers/brain-graph-fixture";
import { BRAIN_KINDS, brainNamespaceKey, brainNamespaceParams, brainNeighbors, filterBrainNodes, fitBrainGeometry, layoutBrain, visibleBrainEdges } from "../../viewer/src/components/brain/brain-model";

const now = Date.UTC(2026, 9, 9);
const demo = createBrainFixture(now);

describe("Brain loaded-record explorer", () => {
  it("explores every knowledge type with stored evidence endpoints", () => {
    expect(demo.nodes).toHaveLength(50);
    expect(visibleBrainEdges(demo.edges, demo.nodes)).toHaveLength(demo.edges.length);
    expect([...new Set(demo.nodes.map(node => node.kind))].sort()).toEqual([...BRAIN_KINDS].sort());
  });

  it("combines category, loaded text, namespace and time filters", () => {
    const result = filterBrainNodes(demo.nodes, new Set(["memory"]), "LOCAL-FIRST", 10, now, brainNamespaceKey(demo.nodes[0].namespace));
    expect(result.map(node => node.id)).toEqual(["sample-0-6", "sample-0-7", "sample-0-8", "sample-0-9"]);
    expect(filterBrainNodes(demo.nodes, new Set(BRAIN_KINDS), "a phrase absent from the store", 0, now)).toEqual([]);
    expect(filterBrainNodes([{ ...demo.nodes[0], createdAt: 0 }], new Set(BRAIN_KINDS), "", 7, now)).toEqual([]);
  });

  it("keeps opaque owner values intact in local API parameters", () => {
    const ns = { ownerAgentKind: "cortex", ownerProfileId: "work & play", ownerWorkspaceId: "C:/my project" };
    const params = brainNamespaceParams(brainNamespaceKey(ns));
    expect(params.get("ownerProfileId")).toBe(ns.ownerProfileId);
    expect(params.get("ownerWorkspaceId")).toBe(ns.ownerWorkspaceId);
    expect(brainNamespaceParams("").size).toBe(0);
    const noWorkspace = brainNamespaceParams(brainNamespaceKey({ ...ns, ownerWorkspaceId: null }));
    expect(noWorkspace.get("ownerWorkspaceIsNull")).toBe("1");
    expect(noWorkspace.has("ownerWorkspaceId")).toBe(false);
  });

  it("does not draw a missing, filtered, or unloaded reference as a connection", () => {
    const original = demo.edges[0];
    const variants = [original, { ...original, id: "missing", targetStatus: "missing" as const }, { ...original, id: "outside", targetStatus: "outside-filter" as const }, { ...original, id: "unloaded", target: "another-record" }];
    expect(visibleBrainEdges(variants, demo.nodes).map(edge => edge.id)).toEqual([original.id]);
  });

  it("focuses only actual direct neighbors and excludes unresolved references", () => {
    const edge = demo.edges[0];
    const found = brainNeighbors(edge.source, [edge, { ...edge, id: "missing", target: "absent", targetStatus: "missing" }]);
    expect([...found]).toEqual([edge.source, edge.target]);
  });

  it("keeps geometry stable when input pages arrive in a different order", () => {
    const first = layoutBrain(demo.nodes, demo.edges);
    const second = layoutBrain([...demo.nodes].reverse(), [...demo.edges].reverse());
    for (const [id, point] of first) {
      expect(point.x).toBeCloseTo(second.get(id)!.x, 6);
      expect(point.y).toBeCloseTo(second.get(id)!.y, 6);
      expect(point.x).toBeGreaterThanOrEqual(55); expect(point.x).toBeLessThanOrEqual(1145);
      expect(point.y).toBeGreaterThanOrEqual(55); expect(point.y).toBeLessThanOrEqual(745);
    }
  });

  it("uses the visible canvas and keeps hit targets separated", () => {
    const fitted = [...fitBrainGeometry(layoutBrain(demo.nodes, demo.edges), 500, 320).values()];
    expect(Math.max(...fitted.map(p => p.x)) - Math.min(...fitted.map(p => p.x))).toBeGreaterThan(380);
    expect(Math.max(...fitted.map(p => p.y)) - Math.min(...fitted.map(p => p.y))).toBeGreaterThan(230);
    for (let i = 0; i < fitted.length; i++) {
      expect(fitted[i].x).toBeGreaterThanOrEqual(34); expect(fitted[i].x).toBeLessThanOrEqual(466);
      expect(fitted[i].y).toBeGreaterThanOrEqual(34); expect(fitted[i].y).toBeLessThanOrEqual(286);
      for (let j = i + 1; j < fitted.length; j++) expect(Math.hypot(fitted[i].x - fitted[j].x, fitted[i].y - fitted[j].y)).toBeGreaterThan(29);
    }
  });

  it("retains recorded learning paths without presenting proximity as evidence", () => {
    const id = "sample-0-6";
    expect(demo.edges.filter(edge => edge.source === id).map(edge => ({ relation: edge.relation, target: edge.target }))).toMatchInlineSnapshot(`
      [
        {
          "relation": "part_of",
          "target": "sample-0-3",
        },
      ]
    `);
  });
});
