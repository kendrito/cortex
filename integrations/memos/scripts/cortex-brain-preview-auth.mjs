#!/usr/bin/env node
/** Initialize normal password authentication in a marked disposable Brain preview. */
import { randomBytes } from "node:crypto";
import { execFileSync } from "node:child_process";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { isAbsolute, join, resolve } from "node:path";
import { pathToFileURL } from "node:url";

/** Configure a generated preview-only password through the ordinary setup API. */
export async function initializeBrainPreviewAuth(home) {
  if (!isAbsolute(home) || home.startsWith("\\\\") || home.startsWith("//")) throw new Error("Use an absolute local preview home");
  const root = resolve(home);
  const record = JSON.parse(await readFile(join(root, "BRAIN-PREVIEW.json"), "utf8"));
  if (record.version !== 1 || record.purpose !== "Isolated empty Brain preview" || resolve(record.home) !== root || record.databaseSeeded !== false) {
    throw new Error("This is not a marked, empty Brain preview home");
  }
  if (!Number.isInteger(record.viewerPort) || record.viewerPort < 1024 || record.viewerPort > 65535) throw new Error("Invalid preview port");
  const origin = `http://127.0.0.1:${record.viewerPort}`;
  const status = await fetch(origin + "/api/v1/auth/status", { redirect: "error" });
  if (!status.ok || (await status.json()).needsSetup !== true) throw new Error("Preview auth is already initialized or unavailable");
  const privateDir = join(root, ".preview-test-auth");
  await mkdir(privateDir, { mode: 0o700 });
  if (process.platform === "win32") {
    const user = execFileSync("whoami.exe", [], { encoding: "utf8", windowsHide: true }).trim();
    execFileSync("icacls.exe", [privateDir, "/inheritance:r", "/grant:r", `${user}:(OI)(CI)F`], { windowsHide: true, stdio: "pipe" });
  }
  const password = randomBytes(32).toString("base64url");
  const credentialFile = join(privateDir, "credential.json");
  await writeFile(credentialFile, JSON.stringify({
    purpose: "Disposable Brain preview login only", synthetic: true, origin,
    createdAt: new Date().toISOString(), password,
  }, null, 2) + "\n", { flag: "wx", mode: 0o600 });
  const response = await fetch(origin + "/api/v1/auth/setup", {
    method: "POST", redirect: "error",
    headers: { "content-type": "application/json", origin },
    body: JSON.stringify({ password }),
  });
  if (!response.ok) throw new Error(`Preview auth setup returned HTTP ${response.status}; credential remains only in its restricted fixture file`);
  const cookie = response.headers.getSetCookie().map(value => value.split(";")[0]).join("; ");
  const verify = await fetch(origin + "/api/v1/auth/status", { headers: { cookie }, redirect: "error" });
  if (!verify.ok || (await verify.json()).authenticated !== true) throw new Error("Preview auth verification failed");
  return { origin, credentialFile, authenticated: true };
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  if (process.argv.length !== 3) {
    console.error("Usage: node scripts/cortex-brain-preview-auth.mjs <absolute-preview-home>");
    process.exitCode = 1;
  } else {
    initializeBrainPreviewAuth(process.argv[2]).then(({ origin, credentialFile }) => {
      console.log(`Initialized disposable preview authentication at ${origin}`);
      console.log(`Restricted fixture credential: ${credentialFile}`);
    }).catch(error => { console.error(`Preview authentication failed: ${error.message}`); process.exitCode = 1; });
  }
}
