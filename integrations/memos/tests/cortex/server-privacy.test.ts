import { request } from "node:http";
import { mkdtemp, mkdir, rm, symlink, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { startHttpServer } from "../../server/http.js";
import type { ServerHandle } from "../../server/types.js";
import type { MemoryCore } from "../../agent-contract/memory-core.js";
import { initTestLogger } from "../../core/logger/index.js";

function call(url: string, method = "GET", headers: Record<string, string> = {}, body = "") {
  return new Promise<{ status: number; body: string; headers: Record<string, string | string[] | undefined> }>((resolve, reject) => {
    const req = request(url, { method, headers }, (res) => {
      let text = "";
      res.setEncoding("utf8");
      res.on("data", (chunk: string) => { text += chunk; });
      res.on("end", () => resolve({ status: res.statusCode ?? 0, body: text, headers: res.headers }));
    });
    req.on("error", reject);
    req.end(body);
  });
}

describe("Cortex local memory viewer boundary", () => {
  let root = "";
  let server: ServerHandle | null = null;
  const patch = vi.fn(async () => ({ version: 1 }));
  const core = {
    getConfig: vi.fn(async () => ({ version: 1 })),
    patchConfig: patch,
  } as MemoryCore;

  beforeEach(async () => {
    initTestLogger();
    patch.mockClear();
    root = await mkdtemp(join(tmpdir(), "cortex-memos-http-"));
    await mkdir(join(root, "viewer"));
    await writeFile(join(root, "viewer", "index.html"), "<h1>Local memory</h1>");
    server = await startHttpServer({ core }, {
      agent: "cortex", staticRoot: join(root, "viewer"), closeActiveSseOnShutdown: true,
    });
  });
  afterEach(async () => {
    await server?.close();
    await rm(root, { recursive: true, force: true });
  });

  it.each([
    ["POST", "/api/v1/models/test"],
    ["POST", "/api/v1/telemetry/viewer-opened"],
    ["GET", "/api/v1/hub/admin"],
    ["GET", "/api/v1/hub/status"],
    ["POST", "/api/v1/hub/admin/approve-user"],
    ["GET", "/api/v1/migrate/legacy/scan"],
    ["POST", "/api/v1/migrate/legacy/run"],
    ["POST", "/api/v1/admin/restart"],
    ["POST", "/api/v1/admin/clear-data"],
  ])("does not expose %s %s", async (method, path) => {
    const response = await call(`${server!.url}${path}`, method);
    expect(response.status).toBe(404);
    expect(patch).not.toHaveBeenCalled();
  });

  it.each([
    { origin: "https://evil.example", "sec-fetch-site": "cross-site" },
    { origin: "null" },
    { "sec-fetch-site": "same-site" },
    { host: "rebinding.example" },
    { host: "127.0.0.1:1" },
  ])("rejects external requests before mutations: %j", async (headers) => {
    const response = await call(`${server!.url}/api/v1/config`, "PATCH", {
      "content-type": "application/json", ...headers,
    }, '{"algorithm":{"retrieval":{"tier2TopK":8}}}');
    expect(response.status).toBe(403);
    expect(patch).not.toHaveBeenCalled();
  });

  it("accepts the viewer's own origin and preserves local editing", async () => {
    const response = await call(`${server!.url}/api/v1/config`, "PATCH", {
      "content-type": "application/json", origin: server!.url, "sec-fetch-site": "same-origin",
    }, '{"algorithm":{"retrieval":{"tier2TopK":8}}}');
    expect(response.status).toBe(200);
    expect(patch).toHaveBeenCalledWith({ algorithm: { retrieval: { tier2TopK: 8 } } });
  });

  it("applies a same-origin content policy to HTML and API responses", async () => {
    for (const path of ["/", "/api/v1/config"]) {
      const response = await call(`${server!.url}${path}`);
      expect(response.status).toBe(200);
      expect(response.headers["content-security-policy"]).toContain("script-src 'self'");
      expect(response.headers["content-security-policy"]).toContain("connect-src 'self'");
      expect(response.headers["content-security-policy"]).toContain("frame-ancestors 'none'");
      expect(response.headers["x-content-type-options"]).toBe("nosniff");
    }
  });

  it("refuses non-loopback listeners", async () => {
    await expect(startHttpServer({ core }, { host: "0.0.0.0" })).rejects.toThrow(/loopback/);
  });

  it("blocks encoded traversal into a sibling with the same name prefix", async () => {
    await mkdir(join(root, "viewer-secret"));
    await writeFile(join(root, "viewer-secret", "secret.txt"), "private");
    const response = await call(`${server!.url}/..%2fviewer-secret/secret.txt`);
    expect(response.status).toBe(403);
    expect(response.body).not.toContain("private");
  });

  it("blocks a static directory junction pointing outside the viewer root", async () => {
    await mkdir(join(root, "private"));
    await writeFile(join(root, "private", "secret.txt"), "private");
    await symlink(join(root, "private"), join(root, "viewer", "outside"), "junction");
    const response = await call(`${server!.url}/outside/secret.txt`);
    expect(response.status).toBe(403);
    expect(response.body).not.toContain("private");
  });
});
