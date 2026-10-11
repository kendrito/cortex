import assert from "node:assert/strict";
import { test } from "node:test";
import { localApiPath } from "./local-api.ts";

test("local memory queries preserve escaped ids and search text", () => {
  const path = "/api/v1/traces/a%2Fb?q=https%3A%2F%2Fexample.test&limit=3";
  assert.equal(localApiPath(path), path);
});

test("external and non-API targets are refused before credentials are attached", () => {
  for (const path of [
    "https://example.test/api/v1/health",
    "//example.test/api/v1/health",
    "http://127.0.0.1:18800/api/v1/health",
    "/api/v1/../../outbound",
    "/api/v1/%2e%2e/%2e%2e/outbound",
    "/api/v1/\\\\example.test",
    "/api/v1/health\n",
    "/assets/remote",
  ]) {
    assert.throws(() => localApiPath(path), /local memory API/);
  }
});
