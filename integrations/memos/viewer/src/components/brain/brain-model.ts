/** Pure filtering and deterministic graph geometry for the Brain viewer. */
import type { BrainEdge, BrainKind, BrainNamespace, BrainNode } from "../../../../agent-contract/brain";

export const BRAIN_KINDS: readonly BrainKind[] = ["memory", "episode", "policy", "world", "skill"];
export const BRAIN_COLORS: Record<BrainKind, string> = { memory: "#e6c17a", episode: "#77cbd6", policy: "#e49385", world: "#afa4de", skill: "#93c9ac" };
export const GRAPH_LIMIT = 400;
export interface BrainPoint { x: number; y: number; degree: number }

/** @returns Stable selection value for a complete owner namespace. */
export function brainNamespaceKey(ns: BrainNamespace): string {
  return JSON.stringify([ns.ownerAgentKind, ns.ownerProfileId, ns.ownerWorkspaceId]);
}

/** @returns Local endpoint parameters for an already selected namespace. */
export function brainNamespaceParams(key: string): URLSearchParams {
  const params = new URLSearchParams();
  if (key) {
    const [kind, profile, workspace] = JSON.parse(key) as [string, string, string | null];
    params.set("ownerAgentKind", kind);
    params.set("ownerProfileId", profile);
    if (workspace !== null) params.set("ownerWorkspaceId", workspace);
    else params.set("ownerWorkspaceIsNull", "1");
  }
  return params;
}

/** @returns Loaded nodes matching the visible controls; dates without evidence never match a recent range. */
export function filterBrainNodes(nodes: readonly BrainNode[], kinds: ReadonlySet<BrainKind>, query: string, days: number, now: number, namespace = ""): BrainNode[] {
  const text = query.trim().toLocaleLowerCase();
  const cutoff = days ? now - days * 86_400_000 : 0;
  return nodes.filter(node => kinds.has(node.kind)
    && (!namespace || brainNamespaceKey(node.namespace) === namespace)
    && (!days || node.createdAt > 0 && node.createdAt >= cutoff)
    && (!text || `${node.title}\n${node.content}`.toLocaleLowerCase().includes(text)));
}

/** @returns Only recorded edges with both ends in this loaded subset. */
export function visibleBrainEdges(edges: readonly BrainEdge[], nodes: readonly BrainNode[]): BrainEdge[] {
  const ids = new Set(nodes.map(node => node.id));
  return edges.filter(edge => edge.targetStatus === "available" && ids.has(edge.source) && ids.has(edge.target));
}

/** @returns A node and its directly recorded neighbors, never inferred similarity. */
export function brainNeighbors(id: string, edges: readonly BrainEdge[]): Set<string> {
  const result = new Set([id]);
  for (const edge of edges) {
    if (edge.targetStatus !== "available") continue;
    if (edge.source === id) result.add(edge.target);
    if (edge.target === id) result.add(edge.source);
  }
  return result;
}

/** @returns A repeatable force layout whose clusters follow connected components. */
export function layoutBrain(nodes: readonly BrainNode[], allEdges: readonly BrainEdge[]): Map<string, BrainPoint> {
  const ordered = [...nodes].sort((a, b) => a.id.localeCompare(b.id));
  const edges = visibleBrainEdges(allEdges, ordered).sort((a, b) => a.id.localeCompare(b.id));
  const adjacency = new Map(ordered.map(node => [node.id, new Set<string>()]));
  for (const edge of edges) { adjacency.get(edge.source)!.add(edge.target); adjacency.get(edge.target)!.add(edge.source); }
  const visited = new Set<string>();
  const groups: string[][] = [];
  for (const node of ordered) {
    if (visited.has(node.id)) continue;
    const group = [node.id]; visited.add(node.id);
    for (let index = 0; index < group.length; index++) for (const next of adjacency.get(group[index])!) {
      if (!visited.has(next)) { visited.add(next); group.push(next); }
    }
    groups.push(group);
  }
  groups.sort((a, b) => b.length - a.length || a[0].localeCompare(b[0]));
  const points = new Map<string, BrainPoint>();
  const anchors = new Map<string, { x: number; y: number }>();
  for (let g = 0; g < groups.length; g++) {
    const angle = g * 2.399963;
    const radius = groups.length === 1 ? 0 : Math.min(360, 115 + Math.sqrt(g) * 90);
    const anchor = { x: 600 + Math.cos(angle) * radius, y: 400 + Math.sin(angle) * radius * .72 };
    groups[g].forEach((id, i) => {
      const orbit = 35 + Math.sqrt(i + 1) * 32;
      points.set(id, { x: anchor.x + Math.cos(i * 2.399963) * orbit, y: anchor.y + Math.sin(i * 2.399963) * orbit, degree: adjacency.get(id)!.size });
      anchors.set(id, anchor);
    });
  }
  const entries = [...points.entries()];
  for (let iteration = 0; iteration < 70; iteration++) {
    const forces = new Map(entries.map(([id]) => [id, { x: 0, y: 0 }]));
    for (let i = 0; i < entries.length; i++) for (let j = i + 1; j < entries.length; j++) {
      const [aId, a] = entries[i], [bId, b] = entries[j];
      const dx = a.x - b.x, dy = a.y - b.y;
      const distance = Math.max(12, Math.hypot(dx, dy));
      const force = 1850 / (distance * distance);
      forces.get(aId)!.x += dx / distance * force; forces.get(aId)!.y += dy / distance * force;
      forces.get(bId)!.x -= dx / distance * force; forces.get(bId)!.y -= dy / distance * force;
    }
    for (const edge of edges) {
      const a = points.get(edge.source)!, b = points.get(edge.target)!;
      const dx = b.x - a.x, dy = b.y - a.y;
      const distance = Math.max(1, Math.hypot(dx, dy)), pull = (distance - 102) * .025;
      forces.get(edge.source)!.x += dx / distance * pull; forces.get(edge.source)!.y += dy / distance * pull;
      forces.get(edge.target)!.x -= dx / distance * pull; forces.get(edge.target)!.y -= dy / distance * pull;
    }
    for (const [id, point] of entries) {
      const anchor = anchors.get(id)!, force = forces.get(id)!;
      point.x = Math.max(55, Math.min(1145, point.x + Math.max(-9, Math.min(9, force.x + (anchor.x - point.x) * .007))));
      point.y = Math.max(55, Math.min(745, point.y + Math.max(-9, Math.min(9, force.y + (anchor.y - point.y) * .007))));
    }
  }
  return points;
}

/** @returns Geometry fitted to the real canvas, with a collision pass for readable targets. */
export function fitBrainGeometry(points: ReadonlyMap<string, BrainPoint>, width: number, height: number): Map<string, BrainPoint> {
  const entries = [...points.entries()];
  if (!entries.length) return new Map();
  const margin = 34;
  const xs = entries.map(([, p]) => p.x), ys = entries.map(([, p]) => p.y);
  const minX = Math.min(...xs), maxX = Math.max(...xs), minY = Math.min(...ys), maxY = Math.max(...ys);
  const result = new Map(entries.map(([id, p]) => [id, { ...p,
    x: maxX === minX ? width / 2 : margin + (p.x - minX) / (maxX - minX) * Math.max(1, width - margin * 2),
    y: maxY === minY ? height / 2 : margin + (p.y - minY) / (maxY - minY) * Math.max(1, height - margin * 2),
  }]));
  const fitted = [...result.values()];
  const separation = Math.min(38, Math.sqrt(width * height / entries.length) * .55);
  for (let pass = 0; pass < 45; pass++) {
    for (let i = 0; i < fitted.length; i++) for (let j = i + 1; j < fitted.length; j++) {
      const a = fitted[i], b = fitted[j], dx = b.x - a.x, dy = b.y - a.y;
      const distance = Math.hypot(dx, dy);
      if (distance >= separation) continue;
      const ux = distance ? dx / distance : Math.cos(i + j), uy = distance ? dy / distance : Math.sin(i + j);
      const push = (separation - distance) / 2;
      a.x -= ux * push; a.y -= uy * push; b.x += ux * push; b.y += uy * push;
    }
    for (const point of fitted) {
      point.x = Math.max(margin, Math.min(width - margin, point.x));
      point.y = Math.max(margin, Math.min(height - margin, point.y));
    }
  }
  return result;
}
