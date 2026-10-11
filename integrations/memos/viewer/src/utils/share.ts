/** Historical share values remain readable; Cortex memory always stays private. */
export type ShareScope = "private" | "public" | "hub";
export type LegacyShareScope = ShareScope | "local";

/** Return the only supported scope for Cortex-managed memory. */
export function effectiveShareScope(_scope: LegacyShareScope | null | undefined): ShareScope {
  return "private";
}

/** Normalize historical sharing metadata without enabling external sharing. */
export function normalizeShareScope(_scope: LegacyShareScope | null | undefined): ShareScope {
  return "private";
}
