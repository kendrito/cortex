/** Read-only projections of stored learning. Drawing these never runs a model. */
export type BrainKind = "memory" | "episode" | "policy" | "world" | "skill";
export interface BrainNamespace {
  ownerAgentKind: string;
  ownerProfileId: string;
  ownerWorkspaceId: string | null;
}
export interface BrainFilter {
  ownerAgentKind?: string;
  ownerProfileId?: string;
  /** Omitted selects all workspaces; null selects records with no workspace. */
  ownerWorkspaceId?: string | null;
}
export interface BrainNode {
  /** Opaque graph identity, independent of the label or the current page. */
  id: string;
  kind: BrainKind;
  rawId: string;
  title: string;
  /** Bounded stored text; full records remain available via source. */
  content: string;
  contentTruncated: boolean;
  namespace: BrainNamespace;
  createdAt: number;
  updatedAt: number;
  status?: string;
  confidence?: number;
  traceCount?: number;
  source: {
    view: "memories" | "tasks" | "policies" | "world-models" | "skills";
    id: string;
    sessionId?: string;
    episodeId?: string;
  };
  evidence: { total: number; shown: number; nextCursor: string | null };
}
export interface BrainEdge {
  id: string;
  source: string;
  target: string;
  /** Untyped world-model evidence can be unresolved; never invent its type. */
  targetKind: BrainKind | "unknown";
  relation: "part_of" | "supported_by" | "abstracts" | "derived_from";
  sourceField: string;
  referenceId: string;
  /** Available means stored, not necessarily loaded in the browser. */
  targetStatus: "available" | "missing" | "outside-filter";
}
export interface BrainSummary {
  generatedAt: number;
  /** Pages are individually consistent reads; refresh after concurrent writes. */
  consistency: "live";
  totals: Record<BrainKind | "trace", number>;
  namespaces: BrainNamespace[];
  namespacesNextCursor: string | null;
}
export interface BrainPage {
  generatedAt: number;
  consistency: "live";
  kind: BrainKind;
  total: number;
  nodes: BrainNode[];
  edges: BrainEdge[];
  nextCursor: string | null;
}
export interface BrainEdgePage {
  generatedAt: number;
  nodeId: string;
  total: number;
  edges: BrainEdge[];
  nextCursor: string | null;
}
export interface BrainPageQuery extends BrainFilter {
  kind: BrainKind;
  limit?: number;
  cursor?: string;
}
export interface BrainEdgeQuery extends BrainFilter {
  nodeId: string;
  limit?: number;
  cursor?: string;
}
export interface BrainSummaryQuery extends BrainFilter { namespaceCursor?: string }
