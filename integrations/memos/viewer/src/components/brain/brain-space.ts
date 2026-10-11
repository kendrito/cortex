/** Deterministic 3D positions derived only from recorded evidence edges. */
import type { BrainEdge, BrainNode } from '../../../../agent-contract/brain';
import { visibleBrainEdges } from './brain-model';

export interface BrainPoint3D { x: number; y: number; z: number; degree: number }
export interface BrainProjection { id: string; x: number; y: number; radius: number; depth: number; visible: boolean; degree: number }
export interface BrainLabel { id: string; x: number; y: number; text: string; anchor: 'start' | 'end' }

/** @returns A repeatable force-directed volume, normalized to a radius of 220 scene units. */
export function layoutBrain3D(nodes: readonly BrainNode[], allEdges: readonly BrainEdge[]): Map<string, BrainPoint3D> {
  const ordered = [...nodes].sort((a, b) => a.id.localeCompare(b.id));
  const edges = visibleBrainEdges(allEdges, ordered).sort((a, b) => a.id.localeCompare(b.id));
  const adjacency = new Map(ordered.map(node => [node.id, new Set<string>()]));
  for (const edge of edges) { adjacency.get(edge.source)!.add(edge.target); adjacency.get(edge.target)!.add(edge.source); }
  const points = new Map<string, BrainPoint3D>();
  for (let i = 0; i < ordered.length; i++) {
    const vertical = 1 - 2 * (i + .5) / Math.max(1, ordered.length);
    const circle = Math.sqrt(1 - vertical * vertical), angle = i * 2.399963229728653;
    const radius = 180 + (i % 4) * 18;
    points.set(ordered[i].id, { x: Math.cos(angle) * circle * radius, y: vertical * radius, z: Math.sin(angle) * circle * radius, degree: adjacency.get(ordered[i].id)!.size });
  }
  const entries = [...points.entries()];
  for (let iteration = 0; iteration < 150; iteration++) {
    const forces = new Map(entries.map(([id]) => [id, { x: 0, y: 0, z: 0 }]));
    for (let i = 0; i < entries.length; i++) for (let j = i + 1; j < entries.length; j++) {
      const [ai, a] = entries[i], [bi, b] = entries[j];
      const dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z, distance = Math.max(1, Math.hypot(dx, dy, dz));
      const force = 7500 / (distance * distance) + Math.max(0, 34 - distance) * .35;
      const fa = forces.get(ai)!, fb = forces.get(bi)!;
      fa.x += dx / distance * force; fa.y += dy / distance * force; fa.z += dz / distance * force;
      fb.x -= dx / distance * force; fb.y -= dy / distance * force; fb.z -= dz / distance * force;
    }
    for (const edge of edges) {
      const a = points.get(edge.source)!, b = points.get(edge.target)!;
      const dx = b.x - a.x, dy = b.y - a.y, dz = b.z - a.z, distance = Math.max(1, Math.hypot(dx, dy, dz));
      const force = (distance - 88) * .045;
      const fa = forces.get(edge.source)!, fb = forces.get(edge.target)!;
      fa.x += dx / distance * force; fa.y += dy / distance * force; fa.z += dz / distance * force;
      fb.x -= dx / distance * force; fb.y -= dy / distance * force; fb.z -= dz / distance * force;
    }
    for (const [id, point] of entries) {
      const force = forces.get(id)!;
      point.x += Math.max(-8, Math.min(8, force.x - point.x * .002));
      point.y += Math.max(-8, Math.min(8, force.y - point.y * .002));
      point.z += Math.max(-8, Math.min(8, force.z - point.z * .002));
    }
  }
  if (!entries.length) return points;
  const center = entries.reduce((p, [, v]) => ({ x: p.x + v.x / entries.length, y: p.y + v.y / entries.length, z: p.z + v.z / entries.length }), { x: 0, y: 0, z: 0 });
  // Force trees may settle into a long strand. Equalize occupied axis spread
  // before fitting: space has no metric meaning here; only the edges are evidence.
  const spread = {
    x: Math.max(1, ...entries.map(([, p]) => Math.abs(p.x - center.x))),
    y: Math.max(1, ...entries.map(([, p]) => Math.abs(p.y - center.y))),
    z: Math.max(1, ...entries.map(([, p]) => Math.abs(p.z - center.z))),
  };
  for (const [, point] of entries) {
    point.x = (point.x - center.x) / spread.x;
    point.y = (point.y - center.y) / spread.y;
    point.z = (point.z - center.z) / spread.z;
  }
  const radius = Math.max(1, ...entries.map(([, p]) => Math.hypot(p.x, p.y, p.z)));
  for (const [, point] of entries) { point.x *= 220 / radius; point.y *= 220 / radius; point.z *= 220 / radius; }
  return points;
}

/** @returns Sparse label positions that avoid one another and projected node hit areas. */
export function placeBrainLabels(nodes: readonly BrainNode[], projections: readonly BrainProjection[], selected: string | null, width: number, height: number): BrainLabel[] {
  const byId = new Map(nodes.map(node => [node.id, node]));
  const candidates = projections.filter(point => point.visible).sort((a, b) => Number(b.id === selected) - Number(a.id === selected)
    || Number(byId.get(b.id)?.kind === 'world') - Number(byId.get(a.id)?.kind === 'world') || b.degree - a.degree || a.depth - b.depth);
  const placed: Array<{ left: number; right: number; top: number; bottom: number }> = [];
  const labels: BrainLabel[] = [];
  for (const p of candidates) {
    if (labels.length >= (selected ? 6 : 9)) break;
    if (p.id !== selected && p.degree < 2 && nodes.length > 12) continue;
    const title = byId.get(p.id)?.title ?? '';
    const text = title.length > 26 ? title.slice(0, 24) + '…' : title;
    const textWidth = [...text].reduce((sum, char) => sum + (/[^\x00-\x7f]/.test(char) ? 12 : 6.3), 0);
    for (const dy of [-18, 25]) for (const direction of [1, -1]) {
      if (labels.some(label => label.id === p.id)) continue;
      const x = p.x + direction * (p.radius + 9), y = p.y + dy;
      const box = { left: direction === 1 ? x : x - textWidth, right: direction === 1 ? x + textWidth : x, top: y - 12, bottom: y + 5 };
      if (box.left < 14 || box.right > width - 14 || box.top < 14 || box.bottom > height - 14) continue;
      if (placed.some(other => box.left < other.right + 10 && box.right > other.left - 10 && box.top < other.bottom + 7 && box.bottom > other.top - 7)) continue;
      if (projections.some(other => other.visible && other.id !== p.id && other.x > box.left - other.radius - 3 && other.x < box.right + other.radius + 3 && other.y > box.top - other.radius - 3 && other.y < box.bottom + other.radius + 3)) continue;
      placed.push(box); labels.push({ id: p.id, x, y, text, anchor: direction === 1 ? 'start' : 'end' });
    }
  }
  return labels;
}
