import type { IncomingMessage, ServerResponse } from "node:http";
import { writeJson } from "./io.js";

export const LOCAL_VIEWER_CSP = [
  "default-src 'self'",
  "script-src 'self'",
  "style-src 'self' 'unsafe-inline'",
  "connect-src 'self'",
  "img-src 'self' data:",
  "font-src 'self'",
  "object-src 'none'",
  "frame-src 'none'",
  "frame-ancestors 'none'",
  "base-uri 'none'",
  "form-action 'self'",
].join("; ");

/** Only literal loopback addresses are eligible listening interfaces. */
export function resolveLoopbackHost(host = "127.0.0.1"): string {
  if (host === "localhost") return "127.0.0.1";
  if (host === "127.0.0.1" || host === "::1") return host;
  throw new Error("Cortex memory viewer must bind to a loopback address");
}

/** Reject browser cross-origin requests and DNS rebinding before dispatch. */
export function enforceLocalAccess(req: IncomingMessage, res: ServerResponse): boolean {
  const port = req.socket.localPort;
  const host = req.headers.host?.toLowerCase();
  const suffix = port === 80 ? "" : `:${port}`;
  const allowedHosts = new Set([`127.0.0.1${suffix}`, `localhost${suffix}`, `[::1]${suffix}`]);
  const origin = req.headers.origin;
  const site = req.headers["sec-fetch-site"];
  if (!host || !allowedHosts.has(host)
    || (origin !== undefined && origin !== `http://${host}`)
    || (site !== undefined && site !== "same-origin" && site !== "none")) {
    writeJson(res, 403, { error: { code: "forbidden", message: "This memory viewer only accepts local, same-origin requests" } });
    return false;
  }
  return true;
}
