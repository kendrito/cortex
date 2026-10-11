import type { BrainNode } from "../../../../agent-contract/brain";

/** @returns A standalone record route, or null when that route is unavailable. */
export function brainRecordPath(node: BrainNode, options: { embedded: boolean }): string | null {
  if (options.embedded) return null;
  return `#/${node.source.view}?id=${encodeURIComponent(node.source.id)}`;
}
