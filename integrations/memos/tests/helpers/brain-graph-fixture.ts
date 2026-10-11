/** Deterministic synthetic graph fixture; never imported by the viewer or packaged. */
import type { BrainEdge, BrainKind, BrainNode, BrainSummary } from "../../agent-contract/brain";

const TOPICS = [
  ["Local-first memory", "Keep knowledge on this device", "Recall project decisions", "Trace a saved preference", "Local recall is explainable"],
  ["Interface craft", "Use calm, readable surfaces", "Test narrow layouts", "Make focus visible", "Clarity before decoration"],
  ["Reliable delivery", "Check behavior at the boundary", "Keep builds reproducible", "Review the final artifact", "Evidence makes changes reviewable"],
  ["Working together", "Carry decisions across sessions", "Name the next useful step", "Keep user intent intact", "Context connects the work"],
  ["Learning from work", "Save the reason behind a fix", "Compare outcomes", "Retain useful procedures", "Learning needs traceable examples"],
];
const VIEWS = { memory: "memories", episode: "tasks", policy: "policies", world: "world-models", skill: "skills" } as const;

/** @returns Fifty labeled synthetic records and their explicitly authored relationships. */
export function createBrainFixture(now = Date.now()): { summary: BrainSummary; nodes: BrainNode[]; edges: BrainEdge[] } {
  const nodes: BrainNode[] = [], edges: BrainEdge[] = [];
  const kinds: BrainKind[] = ["world", "policy", "policy", "episode", "episode", "skill", "memory", "memory", "memory", "memory"];
  TOPICS.forEach((topic, group) => {
    const namespace = { ownerAgentKind: "sample", ownerProfileId: ["Cortex", "Design", "Delivery", "Collaboration", "Learning"][group], ownerWorkspaceId: "synthetic-example" };
    const titles = [topic[0], topic[4], "Keep the supporting evidence", `Explore ${topic[0].toLowerCase()}`, "Review what changed", `${topic[0]} checklist`, topic[1], topic[2], topic[3], "Remember the original decision"];
    kinds.forEach((kind, index) => {
      const id = `sample-${group}-${index}`;
      nodes.push({ id, kind, rawId: id, title: titles[index], content: `Synthetic example for ${topic[0].toLowerCase()}.\n\n${topic[1]}. ${topic[2]}. ${topic[3]}.\n\nThis record demonstrates how a ${kind} can be inspected alongside its stored evidence. It was not learned from your conversations.`, contentTruncated: false, namespace, createdAt: now - (group * 6 + index + 1) * 86400000, updatedAt: now - (index + 1) * 3600000, status: "sample", source: { view: VIEWS[kind], id }, evidence: { total: 0, shown: 0, nextCursor: null } });
    });
    const connect = (source: number, target: number, relation: BrainEdge["relation"]) => edges.push({ id: `sample-edge-${group}-${source}-${target}`, source: `sample-${group}-${source}`, target: `sample-${group}-${target}`, targetKind: kinds[target], relation, sourceField: "synthetic_evidence", referenceId: `sample-${group}-${target}`, targetStatus: "available" });
    connect(0, 1, "abstracts"); connect(0, 2, "abstracts"); connect(1, 3, "supported_by"); connect(2, 4, "supported_by"); connect(5, 1, "derived_from");
    connect(6, 3, "part_of"); connect(7, 3, "part_of"); connect(8, 4, "part_of"); connect(9, 4, "part_of");
  });
  // A small shared evidence path joins three topic clusters; the other two remain independent.
  for (const [a, b] of [[0, 2], [2, 4]]) edges.push({ id: `sample-bridge-${a}-${b}`, source: `sample-${a}-0`, target: `sample-${b}-1`, targetKind: "policy", relation: "abstracts", sourceField: "synthetic_evidence", referenceId: `sample-${b}-1`, targetStatus: "available" });
  for (const node of nodes) { const count = edges.filter(edge => edge.source === node.id).length; node.evidence = { total: count, shown: count, nextCursor: null }; }
  return { nodes, edges, summary: { generatedAt: now, consistency: "live", totals: { memory: 20, episode: 10, policy: 10, world: 5, skill: 5, trace: 20 }, namespaces: TOPICS.map((_, i) => nodes[i * 10].namespace), namespacesNextCursor: null } };
}
