import { describe, expect, it } from "vitest";
import {
  effectiveShareScope,
  normalizeShareScope,
  type LegacyShareScope,
} from "../../../viewer/src/utils/share";

describe("Cortex private memory scopes", () => {
  const historicalScopes: Array<LegacyShareScope | null | undefined> = [
    "hub", "public", "private", "local", null, undefined,
  ];

  it.each(historicalScopes)("displays %s as private without a sharing switch", (scope) => {
    expect(effectiveShareScope(scope)).toBe("private");
  });

  it.each(historicalScopes)("normalizes %s to private for grouped memory views", (scope) => {
    expect(normalizeShareScope(scope)).toBe("private");
  });
});
