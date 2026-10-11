/** Bounded SQLite read model for the Brain. No model, embedding or network imports. */
import type {
  BrainKind, BrainFilter, BrainNamespace, BrainNode, BrainEdge, BrainSummary,
  BrainPage, BrainEdgePage, BrainPageQuery, BrainEdgeQuery, BrainSummaryQuery,
} from "../../agent-contract/brain.js";
import type { StorageDb } from "../storage/types.js";

const KINDS: readonly BrainKind[] = ["memory", "episode", "policy", "world", "skill"];
const TABLES = { memory: "traces", episode: "episodes", policy: "policies", world: "world_model", skill: "skills" };
const VIEWS = { memory: "memories", episode: "tasks", policy: "policies", world: "world-models", skill: "skills" } as const;
const CONTENT_LIMIT = 4_000;
const PREVIEW_EDGES = 12;
const NAMESPACE_LIMIT = 100;
// Ownership is part of a turn identity, even for malformed legacy cross-owner episodes.
const GROUP_KEY = "json_array(owner_agent_kind, owner_profile_id, owner_workspace_id, episode_id, turn_id)";
const OWNER = "owner_agent_kind, owner_profile_id, owner_workspace_id";
type Params = Record<string, string | number | null>;
interface OwnerRow { owner_agent_kind: string; owner_profile_id: string; owner_workspace_id: string | null }
interface NodeRow extends OwnerRow {
  id: string; title: string; content: string; content_length: number;
  created_at: number; updated_at: number; status?: string; confidence?: number;
  session_id?: string; episode_id?: string; trace_count?: number;
}
interface ReferenceRow { edge_key: string; ref: string; target_kind: BrainKind | "unknown"; relation: BrainEdge["relation"]; field: string }

export class BrainQueryError extends Error {
  constructor(message: string, public readonly status = 400) { super(message); }
}

/**
 * A core-only read facade. Each page uses a deferred read transaction.
 * @param db The already migrated core-owned SQLite connection.
 * @returns Bounded stored-learning pages; these methods never write or run models.
 */
export function createBrainReader(db: StorageDb) {
  function where(filter: BrainFilter, alias = ""): { sql: string; args: Params } {
    const parts: string[] = [];
    const args: Params = {};
    for (const [field, column] of [
      ["ownerAgentKind", "owner_agent_kind"], ["ownerProfileId", "owner_profile_id"], ["ownerWorkspaceId", "owner_workspace_id"],
    ] as const) {
      const value = filter[field];
      if (value !== undefined) {
        if (!(field === "ownerWorkspaceId" && value === null)
          && (typeof value !== "string" || value.length === 0 || value.length > 1_024)) throw new BrainQueryError(`Invalid ${field}`);
        parts.push(`${alias}${column} IS @${field}`);
        args[field] = value;
      }
    }
    return { sql: parts.length ? ` WHERE ${parts.join(" AND ")}` : "", args };
  }
  function keyQuery(kind: BrainKind, filter: BrainFilter): { sql: string; args: Params } {
    const scope = where(filter);
    return {
      sql: kind === "memory"
        ? `SELECT ${GROUP_KEY} AS node_key FROM traces${scope.sql} GROUP BY ${OWNER}, episode_id, turn_id`
        : `SELECT id AS node_key FROM ${TABLES[kind]}${scope.sql}`,
      args: scope.args,
    };
  }
  function total(kind: BrainKind, filter: BrainFilter): number {
    const query = keyQuery(kind, filter);
    return count(`SELECT COUNT(*) AS n FROM (${query.sql})`, query.args);
  }
  function count(sql: string, args: Params): number {
    return db.prepare<Params, { n: number }>(sql).get(args)!.n;
  }
  function namespace(row: OwnerRow): BrainNamespace {
    return { ownerAgentKind: row.owner_agent_kind, ownerProfileId: row.owner_profile_id, ownerWorkspaceId: row.owner_workspace_id };
  }
  function inFilter(row: OwnerRow, filter: BrainFilter): boolean {
    return (filter.ownerAgentKind === undefined || filter.ownerAgentKind === row.owner_agent_kind)
      && (filter.ownerProfileId === undefined || filter.ownerProfileId === row.owner_profile_id)
      && (filter.ownerWorkspaceId === undefined || filter.ownerWorkspaceId === row.owner_workspace_id);
  }
  function getRow(kind: BrainKind, key: string): NodeRow | undefined {
    if (kind === "memory") {
      const [agent, profile, workspace, episode, turn] = parseGroup(key);
      return db.prepare<Params, NodeRow>(`
        WITH members AS (SELECT id, ${OWNER}, session_id, episode_id, ts,
          substr(COALESCE(NULLIF(summary,''), NULLIF(user_text,''), NULLIF(agent_text,''), 'Stored memory'),1,120) AS title,
          substr(user_text || char(10) || agent_text,1,${CONTENT_LIMIT}) AS content,
          length(user_text || char(10) || agent_text) AS content_length
          FROM traces WHERE episode_id=@episode AND turn_id=@turn AND owner_agent_kind=@agent
            AND owner_profile_id=@profile AND owner_workspace_id IS @workspace)
        SELECT *, (SELECT MIN(ts) FROM members) AS created_at, (SELECT MAX(ts) FROM members) AS updated_at,
          (SELECT COUNT(*) FROM members) AS trace_count FROM members ORDER BY ts, id LIMIT 1
      `).get({ agent, profile, workspace, episode, turn });
    }
    const taskText = "COALESCE((SELECT NULLIF(user_text,'') FROM traces WHERE episode_id=episodes.id AND owner_agent_kind=episodes.owner_agent_kind AND owner_profile_id=episodes.owner_profile_id AND owner_workspace_id IS episodes.owner_workspace_id ORDER BY ts,id LIMIT 1),'')";
    const expressions: Record<Exclude<BrainKind, "memory">, { title: string; body: string; extra: string }> = {
      episode: {
        title: `COALESCE(NULLIF(${taskText},''),'Stored task')`,
        body: taskText,
        extra: "started_at AS created_at, COALESCE(ended_at,started_at) AS updated_at, status, session_id",
      },
      policy: { title: "title", body: "trigger || char(10) || procedure || char(10) || verification || char(10) || boundary", extra: "created_at, updated_at, status, confidence" },
      world: { title: "title", body: "body", extra: "created_at, updated_at, status, confidence" },
      skill: { title: "name", body: "invocation_guide", extra: "created_at, updated_at, status" },
    };
    const e = expressions[kind];
    return db.prepare<Params, NodeRow>(`SELECT id, ${OWNER}, substr(${e.title},1,120) AS title,
      substr(${e.body},1,${CONTENT_LIMIT}) AS content, length(${e.body}) AS content_length, ${e.extra}
      FROM ${TABLES[kind]} WHERE id=@id`).get({ id: key });
  }
  function node(kind: BrainKind, key: string, row: NodeRow): BrainNode {
    return {
      id: nodeId(kind, key), kind, rawId: row.id, title: row.title, content: row.content,
      contentTruncated: row.content_length > CONTENT_LIMIT, namespace: namespace(row),
      createdAt: row.created_at, updatedAt: row.updated_at,
      ...(row.status === undefined ? {} : { status: row.status }),
      ...(row.confidence === undefined ? {} : { confidence: row.confidence }),
      ...(row.trace_count === undefined ? {} : { traceCount: row.trace_count }),
      source: { view: VIEWS[kind], id: row.id,
        ...(row.session_id === undefined ? {} : { sessionId: row.session_id }),
        ...(row.episode_id === undefined ? {} : { episodeId: row.episode_id }) },
      evidence: { total: 0, shown: 0, nextCursor: null },
    };
  }
  function referenceQuery(kind: BrainKind): string {
    const jsonRefs = (column: string, target: string, relation: BrainEdge["relation"], field: string) =>
      `SELECT j.value AS ref, '${target}' AS target_kind, '${relation}' AS relation, '${field}' AS field
       FROM ${TABLES[kind]} s, json_each(s.${column}) j WHERE s.id=@id AND j.type='text' AND length(j.value)>0`;
    switch (kind) {
      case "memory": return "SELECT @episode AS ref, 'episode' AS target_kind, 'part_of' AS relation, 'episodeId' AS field";
      case "episode": return jsonRefs("trace_ids_json", "memory", "supported_by", "traceIds");
      case "policy": return [
        jsonRefs("source_episodes_json", "episode", "supported_by", "sourceEpisodeIds"),
        jsonRefs("source_trace_ids_json", "memory", "supported_by", "sourceTraceIds"),
        "SELECT trace_id AS ref, 'memory' AS target_kind, 'supported_by' AS relation, 'tracePolicyLinks' AS field FROM trace_policy_links WHERE policy_id=@id",
      ].join(" UNION ALL ");
      case "world": return [
        jsonRefs("policy_ids_json", "policy", "abstracts", "policyIds"),
        jsonRefs("source_episodes_json", "episode", "supported_by", "sourceEpisodeIds"),
        `SELECT r.value AS ref, 'unknown' AS target_kind, 'supported_by' AS relation,
          'structure.' || facet.key || '.' || entry.key || '.evidenceIds' AS field
          FROM world_model s, json_each(s.structure_json) facet, json_each(facet.value) entry,
          json_each(entry.value, '$.evidenceIds') r
          WHERE s.id=@id AND facet.key IN ('environment','inference','constraints') AND r.type='text' AND length(r.value)>0`,
      ].join(" UNION ALL ");
      case "skill": return [
        jsonRefs("source_policies_json", "policy", "derived_from", "sourcePolicyIds"),
        jsonRefs("source_world_json", "world", "derived_from", "sourceWorldModelIds"),
        jsonRefs("evidence_anchors_json", "memory", "supported_by", "evidenceAnchors"),
      ].join(" UNION ALL ");
    }
  }
  function resolveTarget(ref: ReferenceRow, filter: BrainFilter): Pick<BrainEdge, "target" | "targetKind" | "targetStatus"> {
    let kind = ref.target_kind;
    let key = ref.ref;
    let owner: OwnerRow | undefined;
    if (kind === "memory" || kind === "unknown") {
      const trace = db.prepare<Params, OwnerRow & { group_key: string }>(`SELECT ${OWNER}, ${GROUP_KEY} AS group_key FROM traces WHERE id=@id`).get({ id: key });
      if (trace) { kind = "memory"; key = trace.group_key; owner = trace; }
    }
    if (kind === "unknown") {
      owner = db.prepare<Params, OwnerRow>(`SELECT ${OWNER} FROM policies WHERE id=@id`).get({ id: key });
      if (owner) kind = "policy";
    } else if (kind !== "memory") {
      owner = db.prepare<Params, OwnerRow>(`SELECT ${OWNER} FROM ${TABLES[kind]} WHERE id=@id`).get({ id: key });
    }
    return {
      target: owner ? nodeId(kind, key) : `missing:${kind}:${encodeURIComponent(ref.ref)}`,
      targetKind: kind,
      targetStatus: !owner ? "missing" : inFilter(owner, filter) ? "available" : "outside-filter",
    };
  }
  function edgePage(query: BrainEdgeQuery): BrainEdgePage {
    const { kind, key } = parseNodeId(query.nodeId);
    const scope = where(query);
    const after = decodeCursor(query.cursor, `edges:${query.nodeId}`, scope.args);
    const limit = pageLimit(query.limit, 50);
    const row = getRow(kind, key);
    if (!row || !inFilter(row, query)) throw new BrainQueryError("Brain node is absent or outside the selected namespace", 404);
    const raw = referenceQuery(kind);
    const refs = `SELECT DISTINCT ref, target_kind, relation, field, json_array(field,target_kind,ref,relation) AS edge_key FROM (${raw})`;
    const args: Params = { id: row.id, ...(kind === "memory" ? { episode: row.episode_id! } : {}) };
    // SQLite named bindings reject unused values, so each branch gets only its own parameters.
    if (kind === "memory") delete args.id;
    const total = count(`SELECT COUNT(*) AS n FROM (${refs})`, args);
    const rows = db.prepare<Params, ReferenceRow>(`SELECT * FROM (${refs}) WHERE edge_key>@after ORDER BY edge_key LIMIT @limit`)
      .all({ ...args, after, limit: limit + 1 });
    const selected = rows.slice(0, limit);
    return {
      generatedAt: Date.now(), nodeId: query.nodeId, total,
      edges: selected.map(ref => ({
        id: `edge:${Buffer.from(JSON.stringify([query.nodeId, ref.edge_key])).toString("base64url")}`,
        source: query.nodeId, ...resolveTarget(ref, query), relation: ref.relation,
        sourceField: ref.field, referenceId: ref.ref,
      })),
      nextCursor: rows.length > limit ? encodeCursor(`edges:${query.nodeId}`, scope.args, selected.at(-1)!.edge_key) : null,
    };
  }
  function page(query: BrainPageQuery): BrainPage {
    if (!KINDS.includes(query.kind)) throw new BrainQueryError("Unknown Brain kind");
    const keys = keyQuery(query.kind, query);
    const after = decodeCursor(query.cursor, query.kind, keys.args);
    const limit = pageLimit(query.limit, 25);
    const rows = db.prepare<Params, { node_key: string }>(`SELECT node_key FROM (${keys.sql}) WHERE node_key>@after ORDER BY node_key LIMIT @limit`)
      .all({ ...keys.args, after, limit: limit + 1 });
    const selected = rows.slice(0, limit);
    const nodes: BrainNode[] = [];
    const edges: BrainEdge[] = [];
    for (const { node_key: key } of selected) {
      const row = getRow(query.kind, key)!;
      const entry = node(query.kind, key, row);
      const evidence = edgePage({ ...query, nodeId: entry.id, cursor: undefined, limit: PREVIEW_EDGES });
      entry.evidence = { total: evidence.total, shown: evidence.edges.length, nextCursor: evidence.nextCursor };
      nodes.push(entry);
      edges.push(...evidence.edges);
    }
    return { generatedAt: Date.now(), consistency: "live", kind: query.kind, total: total(query.kind, query), nodes, edges,
      nextCursor: rows.length > limit ? encodeCursor(query.kind, keys.args, selected.at(-1)!.node_key) : null };
  }
  function summary(query: BrainSummaryQuery): BrainSummary {
    const scope = where(query);
    const totals: BrainSummary["totals"] = { memory: 0, trace: 0, episode: 0, policy: 0, world: 0, skill: 0 };
    for (const kind of KINDS) totals[kind] = total(kind, query);
    totals.trace = count(`SELECT COUNT(*) AS n FROM traces${scope.sql}`, scope.args);
    const union = KINDS.map(kind => `SELECT ${OWNER} FROM ${TABLES[kind]}${scope.sql}`).join(" UNION ");
    const keys = `SELECT *, json_array(${OWNER}) AS namespace_key FROM (${union})`;
    const after = decodeCursor(query.namespaceCursor, "namespaces", scope.args);
    const rows = db.prepare<Params, OwnerRow & { namespace_key: string }>(
      `SELECT * FROM (${keys}) WHERE namespace_key>@after ORDER BY namespace_key LIMIT @limit`,
    ).all({ ...scope.args, after, limit: NAMESPACE_LIMIT + 1 });
    const selected = rows.slice(0, NAMESPACE_LIMIT);
    return { generatedAt: Date.now(), consistency: "live", totals, namespaces: selected.map(namespace),
      namespacesNextCursor: rows.length > NAMESPACE_LIMIT ? encodeCursor("namespaces", scope.args, selected.at(-1)!.namespace_key) : null };
  }
  return {
    summary: (query: BrainSummaryQuery = {}) => db.raw.transaction(() => summary(query)).deferred(),
    page: (query: BrainPageQuery) => db.raw.transaction(() => page(query)).deferred(),
    edges: (query: BrainEdgeQuery) => db.raw.transaction(() => edgePage(query)).deferred(),
  };
}

function nodeId(kind: BrainKind | "unknown", key: string): string {
  return `${kind}:${kind === "memory" ? Buffer.from(key).toString("base64url") : encodeURIComponent(key)}`;
}
function parseNodeId(value: string): { kind: BrainKind; key: string } {
  if (typeof value !== "string" || value.length > 8_192) throw new BrainQueryError("Invalid Brain node identity");
  const colon = value.indexOf(":");
  const kind = value.slice(0, colon);
  if (!isKind(kind)) throw new BrainQueryError("Invalid Brain node kind");
  try {
    const encoded = value.slice(colon + 1);
    const key = kind === "memory" ? Buffer.from(encoded, "base64url").toString("utf8") : decodeURIComponent(encoded);
    if (!key || nodeId(kind, key) !== value) throw new Error("non-canonical identity");
    return { kind, key };
  } catch { throw new BrainQueryError("Invalid Brain node identity"); }
}
function parseGroup(key: string): [string, string, string | null, string, number] {
  let value: unknown;
  try { value = JSON.parse(key); } catch { throw new BrainQueryError("Invalid memory group identity"); }
  if (!Array.isArray(value) || value.length !== 5 || typeof value[0] !== "string" || typeof value[1] !== "string"
    || (value[2] !== null && typeof value[2] !== "string") || typeof value[3] !== "string"
    || typeof value[4] !== "number" || !Number.isSafeInteger(value[4])) throw new BrainQueryError("Invalid memory group identity");
  return [value[0], value[1], value[2], value[3], value[4]];
}
function pageLimit(value: number | undefined, fallback: number): number {
  if (value === undefined) return fallback;
  if (!Number.isInteger(value) || value < 1 || value > 100) throw new BrainQueryError("limit must be an integer from 1 to 100");
  return value;
}
function encodeCursor(kind: string, scope: Params, after: string): string {
  return Buffer.from(JSON.stringify({ version: 1, kind, scope, after })).toString("base64url");
}
function decodeCursor(cursor: string | undefined, kind: string, scope: Params): string {
  if (cursor === undefined) return "";
  if (cursor.length > 16_384 || !/^[a-zA-Z0-9_-]+$/.test(cursor)) throw new BrainQueryError("Invalid Brain cursor");
  try {
    const value: unknown = JSON.parse(Buffer.from(cursor, "base64url").toString("utf8"));
    if (!value || typeof value !== "object" || Array.isArray(value)) throw new Error("shape");
    if (!("version" in value && value.version === 1 && "kind" in value && value.kind === kind
      && "scope" in value && JSON.stringify(value.scope) === JSON.stringify(scope)
      && "after" in value && typeof value.after === "string")) throw new Error("scope");
    return value.after;
  } catch { throw new BrainQueryError("Brain cursor does not match this query; restart pagination"); }
}
/** @param value The received category. @returns Whether it names a stored learning category. */
export function isKind(value: string): value is BrainKind {
  return value === "memory" || value === "episode" || value === "policy" || value === "world" || value === "skill";
}
