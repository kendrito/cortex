/** The authenticated viewer's read-only, progressively loaded learning graph. */
import type { BrainFilter } from "../../agent-contract/brain.js";
import { BrainQueryError, isKind } from "../../core/brain/read.js";
import type { ServerDeps } from "../types.js";
import { writeError, type RouteContext, type Routes } from "./registry.js";

export function registerBrainRoutes(routes: Routes, deps: ServerDeps): void {
  routes.set("GET /api/v1/brain/summary", ctx => guarded(ctx, () => deps.core.readBrainSummary({
    ...filter(ctx), namespaceCursor: optional(ctx, "namespaceCursor"),
  })));
  routes.set("GET /api/v1/brain/nodes", ctx => guarded(ctx, () => {
    const kind = ctx.url.searchParams.get("kind") ?? "memory";
    if (!isKind(kind)) throw new BrainQueryError("kind must be memory, episode, policy, world, or skill");
    return deps.core.readBrainPage({ ...filter(ctx), kind, limit: limit(ctx), cursor: optional(ctx, "cursor") });
  }));
  routes.set("GET /api/v1/brain/edges", ctx => guarded(ctx, () => {
    const nodeId = optional(ctx, "nodeId");
    if (!nodeId) throw new BrainQueryError("nodeId is required");
    return deps.core.readBrainEdges({ ...filter(ctx), nodeId, limit: limit(ctx), cursor: optional(ctx, "cursor") });
  }));
}
function optional(ctx: RouteContext, key: string): string | undefined {
  const value = ctx.url.searchParams.get(key);
  return value === null ? undefined : value;
}
function filter(ctx: RouteContext): BrainFilter {
  const workspace = optional(ctx, "ownerWorkspaceId");
  const workspaceIsNull = optional(ctx, "ownerWorkspaceIsNull");
  if (workspaceIsNull !== undefined && (workspaceIsNull !== "1"
    || ctx.url.searchParams.getAll("ownerWorkspaceIsNull").length !== 1)) {
    throw new BrainQueryError("ownerWorkspaceIsNull must be 1 when present");
  }
  if (workspaceIsNull !== undefined && workspace !== undefined) {
    throw new BrainQueryError("ownerWorkspaceIsNull and ownerWorkspaceId are mutually exclusive");
  }
  return { ownerAgentKind: optional(ctx, "ownerAgentKind"), ownerProfileId: optional(ctx, "ownerProfileId"),
    ownerWorkspaceId: workspaceIsNull === "1" ? null : workspace };
}
function limit(ctx: RouteContext): number | undefined {
  const value = optional(ctx, "limit");
  if (value === undefined) return undefined;
  if (!/^\d+$/.test(value)) throw new BrainQueryError("limit must be an integer from 1 to 100");
  return Number(value);
}
async function guarded(ctx: RouteContext, read: () => Promise<unknown>): Promise<unknown> {
  ctx.res.setHeader("Cache-Control", "no-store");
  try { return await read(); }
  catch (error) {
    if (!(error instanceof BrainQueryError)) throw error;
    writeError(ctx, error.status, "invalid_argument", error.message);
    return undefined;
  }
}
