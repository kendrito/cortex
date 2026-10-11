/** Geometry and camera acceptance with real Three math; no GPU, network or model runtime. */
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { BrainEdge, BrainNode } from "../../../agent-contract/brain";
import { createBrainFixture } from "../../helpers/brain-graph-fixture";
import { layoutBrain3D, placeBrainLabels, type BrainProjection } from "../../../viewer/src/components/brain/brain-space";
import { BrainRenderer, type BrainFrame } from "../../../viewer/src/components/brain/brain-renderer";
import { Material, SphereGeometry, Texture, Vector3 } from "three";

const gpu = vi.hoisted(() => ({ render: vi.fn(), dispose: vi.fn(), forceContextLoss: vi.fn() }));
vi.mock("three", async importOriginal => {
  const actual = await importOriginal<typeof import("three")>();
  return { ...actual, WebGLRenderer: class {
    setPixelRatio() {} setClearColor() {} setSize() {}
    render = gpu.render; dispose = gpu.dispose; forceContextLoss = gpu.forceContextLoss;
  } };
});
const demo = createBrainFixture(Date.UTC(2026, 9, 9));
const sample = (id: string): BrainNode => ({ ...demo.nodes[0], id, title: id, kind: "memory" });
const projection = (id: string, x: number, y: number, depth = 100): BrainProjection => ({ id, x, y, depth, radius: 8, visible: true, degree: 3 });

describe("Brain 3D stored-evidence layout", () => {
  it("keeps page-order-independent finite volume within the camera fitting radius", () => {
    const original = JSON.stringify(demo);
    const a = layoutBrain3D(demo.nodes, demo.edges);
    const b = layoutBrain3D([...demo.nodes].reverse(), [...demo.edges].reverse());
    expect(a.size).toBe(50);
    for (const [id, p] of a) {
      expect(p).toEqual(b.get(id));
      expect(Math.hypot(p.x, p.y, p.z)).toBeLessThanOrEqual(220.000001);
      expect([p.x, p.y, p.z].every(Number.isFinite)).toBe(true);
    }
    for (const axis of ["x", "y", "z"] as const) {
      const values = [...a.values()].map(p => p[axis]);
      expect(Math.max(...values) - Math.min(...values)).toBeGreaterThan(100);
      expect(values.reduce((sum, value) => sum + value, 0)).toBeCloseTo(0, 6);
    }
    expect(JSON.stringify(demo)).toBe(original);
  });

  it("uses only loaded available evidence and counts distinct actual neighbors", () => {
    const nodes = [sample("a"), sample("b"), sample("c")];
    const edge: BrainEdge = { ...demo.edges[0], id: "ab", source: "a", target: "b" };
    const refs: BrainEdge[] = [edge, { ...edge, id: "other-reference" },
      { ...edge, id: "missing", target: "c", targetStatus: "missing" },
      { ...edge, id: "outside", target: "c", targetStatus: "outside-filter" },
      { ...edge, id: "unloaded", target: "unloaded" }];
    const points = layoutBrain3D(nodes, refs);
    expect([...points.values()].map(p => p.degree)).toEqual([1, 1, 0]);
    expect(points.has("unloaded")).toBe(false);
    const onlyAvailable = layoutBrain3D(nodes, refs.slice(0, 2));
    expect(points).toEqual(onlyAvailable);
    expect(layoutBrain3D([], [])).toEqual(new Map());
    expect(layoutBrain3D([sample("alone")], []).get("alone")).toEqual({ x: 0, y: 0, z: 0, degree: 0 });
  });

  it("prioritizes the selected label, excludes hidden nodes and leaves a crowded graph readable", () => {
    const nodes = Array.from({ length: 15 }, (_, i) => sample(`node-${i}`));
    const points = nodes.map((node, i) => projection(node.id, 100 + i % 5 * 150, 100 + Math.floor(i / 5) * 125));
    points[0].visible = false;
    const labels = placeBrainLabels(nodes, points, "node-14", 900, 500);
    expect(labels[0].id).toBe("node-14");
    expect(labels.length).toBeGreaterThan(1);
    expect(labels.length).toBeLessThanOrEqual(6);
    expect(labels.some(label => label.id === "node-0")).toBe(false);
    const boxes = labels.map(label => ({ left: label.anchor === "start" ? label.x : label.x - label.text.length * 6.3,
      right: label.anchor === "start" ? label.x + label.text.length * 6.3 : label.x, top: label.y - 12, bottom: label.y + 5 }));
    for (let i = 0; i < boxes.length; i++) {
      const box = boxes[i];
      expect(box.left).toBeGreaterThanOrEqual(14); expect(box.right).toBeLessThanOrEqual(886);
      expect(box.top).toBeGreaterThanOrEqual(14); expect(box.bottom).toBeLessThanOrEqual(486);
      for (const other of boxes.slice(i + 1)) {
        expect(box.left < other.right && box.right > other.left && box.top < other.bottom && box.bottom > other.top).toBe(false);
      }
    }
  });
});

describe("Brain camera projection and lifecycle", () => {
  let jobs: Map<number, FrameRequestCallback>;
  let nextJob: number;
  let frames: BrainFrame[];
  let renderer: BrainRenderer | undefined;
  const flush = () => { const queued = [...jobs.values()]; jobs.clear(); for (const job of queued) job(0); };
  beforeEach(() => {
    jobs = new Map(); nextJob = 0; frames = [];
    vi.clearAllMocks();
    vi.stubGlobal("window", { devicePixelRatio: 2 });
    vi.stubGlobal("document", { createElement: () => ({ getContext: () => ({ createRadialGradient: () => ({ addColorStop() {} }), fillRect() {} }) }) });
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => { jobs.set(++nextJob, callback); return nextJob; });
    vi.stubGlobal("cancelAnimationFrame", (id: number) => jobs.delete(id));
    vi.spyOn(globalThis, "fetch").mockRejectedValue(new Error("Brain rendering must remain local"));
  });
  afterEach(() => {
    renderer?.dispose(); renderer = undefined;
    expect(globalThis.fetch).not.toHaveBeenCalled();
    vi.restoreAllMocks(); vi.unstubAllGlobals();
  });
  function create() {
    renderer = new BrainRenderer({} as HTMLCanvasElement, frame => frames.push(frame));
    renderer.resize(800, 450); flush();
    return renderer;
  }

  it("fits wide and portrait viewports and projects the origin at the viewport center", () => {
    const view = create();
    view.setGraph([sample("origin")], [], new Map([["origin", { x: 0, y: 0, z: 0, degree: 0 }]]));
    for (const [width, height] of [[1200, 400], [320, 700]]) {
      view.resize(width, height); flush();
      const frame = frames.at(-1)!;
      expect(frame.points[0]).toMatchObject({ id: "origin", visible: true });
      expect(frame.points[0].x).toBeCloseTo(width / 2, 6);
      expect(frame.points[0].y).toBeCloseTo(height / 2, 6);
      expect(frame.points[0].radius).toBeGreaterThan(0);
    }
    view.setGraph(demo.nodes, demo.edges, layoutBrain3D(demo.nodes, demo.edges));
    for (const [width, height] of [[1200, 400], [320, 700]]) {
      view.resize(width, height); flush();
      for (const point of frames.at(-1)!.points) {
        expect(point.x - point.radius).toBeGreaterThanOrEqual(0);
        expect(point.x + point.radius).toBeLessThanOrEqual(width);
        expect(point.y - point.radius).toBeGreaterThanOrEqual(0);
        expect(point.y + point.radius).toBeLessThanOrEqual(height);
      }
    }
  });

  it("gives the foreground sphere precedence over an aligned hidden sphere", () => {
    const view = create();
    const axes = frames.at(-1)!.axes;
    const right = new Vector3(...axes.map(axis => axis.x) as [number, number, number]);
    const up = new Vector3(...axes.map(axis => -axis.y) as [number, number, number]);
    const towardViewer = right.cross(up).normalize();
    // Face the z axis using the rendered compass; aspect-fitting leaves this ray aligned.
    view.orbit(-Math.atan2(towardViewer.x, towardViewer.z), -Math.asin(towardViewer.y));
    view.setGraph([sample("back"), sample("front")], [], new Map([
      ["back", { x: 0, y: 0, z: -100, degree: 0 }],
      ["front", { x: 0, y: 0, z: 100, degree: 0 }],
    ]));
    flush();
    const points = frames.at(-1)!.points;
    expect(points[0].id).toBe("front");
    expect(points[0].visible).toBe(true); expect(points[1].visible).toBe(false);
    expect(points[0].radius).toBeGreaterThan(points[1].radius);
    expect(points[0].x).toBeCloseTo(points[1].x, 5); expect(points[0].y).toBeCloseTo(points[1].y, 5);
  });

  it("coalesces interactions, does no idle animation, clamps zoom and releases resources on unmount", () => {
    const materialDispose = vi.spyOn(Material.prototype, "dispose");
    const sphereDispose = vi.spyOn(SphereGeometry.prototype, "dispose");
    const textureDispose = vi.spyOn(Texture.prototype, "dispose");
    const view = create();
    view.setGraph(demo.nodes, demo.edges, layoutBrain3D(demo.nodes, demo.edges));
    view.orbit(.2, .1); view.pan(10, 15); view.zoomBy(100);
    expect(jobs.size).toBe(1); flush();
    expect(frames.at(-1)!.zoom).toBe(3.5); expect(jobs.size).toBe(0);
    view.zoomBy(.0001); flush(); expect(frames.at(-1)!.zoom).toBe(.5);
    view.reset(); flush(); expect(frames.at(-1)!.zoom).toBe(1);
    view.orbit(.1, .1); expect(jobs.size).toBe(1);
    const renders = gpu.render.mock.calls.length;
    view.dispose(); renderer = undefined;
    expect(jobs.size).toBe(0); flush(); expect(gpu.render).toHaveBeenCalledTimes(renders);
    expect(materialDispose).toHaveBeenCalled(); expect(sphereDispose).toHaveBeenCalledTimes(1);
    expect(textureDispose).toHaveBeenCalledTimes(1);
    expect(gpu.dispose).toHaveBeenCalledTimes(1); expect(gpu.forceContextLoss).toHaveBeenCalledTimes(1);
  });
});
