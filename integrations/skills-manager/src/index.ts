/** Authenticated Cortex skill-management API and opt-in local skill providers. */
import type { Context } from "@cortex/cordis";
import { defineTool } from "@cortex/tools";
import type { Agent } from "@cortex/agent";
import { createScope, type ScopeKey } from "@cortex/scope";
import type { SkillProvider, SkillProviderControl } from "@cortex/skill";
import type { SessionId } from "@cortex/session";
import type {} from "@cortex/session-query";
import type {} from "@cortex/client-connection";
import type {} from "@cortex/agent-preset-registry/types";
import type {} from "@cortex/agent-preset-registry";
import type { Log, Serialize, CreateInput, UploadInput, SkillRequest, RepositoryInput, ScopeOptions, CodedError } from "./types.js";
import type { ProviderCandidate } from "./core.js";
import { appendFile, rename, rm, stat } from "node:fs/promises";
import { state, setSkillEnabled, setSourceEnabled, deleteSkill, permanentlyDeleteTrash,
  importSkill, importUploadedSkill, browseDirectories, createSkill, skillDetail,
  listProviderCandidates, getProviderSkill, userRoots, projectRoots, logPath } from "./core.js";
import { createRepositoryManager } from "./repositories.js";

export const name = "skills-manager";
export const inject = ["connection", "skills", "sessionQuery"];
const CLIENT_MARKER_HEADER = "x-dsh-skills-manager";
const MAX_LOG_BYTES = 1 << 20;
const MAX_UPLOAD_BODY_BYTES = 88 << 20;
const API = "/api/dsh-skills-manager";
const POST_PATHS = ["browse", "repositories/refresh", "repositories/add", "repositories/remove",
  "repositories/detail", "repositories/install", "repositories/uninstall", "repositories/preview",
  "repositories/update", "repositories/rollback", "enable", "disable", "source-enable", "source-disable",
  "delete", "trash-restore", "trash-delete", "detail", "create", "import", "upload"];
/** UI operations are always available; the model tool requires an explicit deployment choice. */
export interface Config { exposeCreateSkillTool?: boolean }

function makeLog(): Log {
  const file = logPath();
  let queue = Promise.resolve();
  return async (event, detail) => {
    queue = queue.then(async () => {
      try {
        const current = await stat(file).catch(() => null);
        if (current && current.size >= MAX_LOG_BYTES) {
          await rm(`${file}.1`, { force: true }); await rename(file, `${file}.1`);
        }
        await appendFile(file, `${JSON.stringify({ ts: new Date().toISOString(), event, detail })}\n`, "utf8");
      } catch { /* A local log failure must not change a completed skill operation. */ }
    });
    await queue;
  };
}

function json(status: number, payload: unknown, headOnly = false): Response {
  const text = JSON.stringify(payload);
  return new Response(headOnly ? null : text, { status, headers: {
    "content-type": "application/json; charset=utf-8", "content-length": String(Buffer.byteLength(text)),
    "cache-control": "no-store",
  } });
}
/** Bound each action while streaming, before allocating its complete JSON payload. */
async function readActionBytes(req: Request, limit: number): Promise<Buffer> {
  const rejectLarge = () => Object.assign(new Error("body too large"), { statusCode: 413, code: "error.proto.bodyTooLarge" });
  if (Number(req.headers.get("content-length")) > limit) throw rejectLarge();
  const reader = req.body?.getReader();
  if (!reader) return Buffer.alloc(0);
  const chunks: Uint8Array[] = [];
  let size = 0;
  try {
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      size += value.byteLength;
      if (size > limit) throw rejectLarge();
      chunks.push(value);
    }
    return Buffer.concat(chunks, size);
  } finally { await reader.cancel(); }
}
function failure(error: unknown, headOnly = false): Response {
  const item = error as CodedError;
  return json(Number.isInteger(item?.statusCode) ? item.statusCode! : 500,
    { ok: false, ...(item?.code ? { code: item.code } : {}), error: item?.message ?? String(error) }, headOnly);
}
async function run(task: () => unknown, afterSuccess?: () => void, headOnly = false): Promise<Response> {
  try {
    const result = await task();
    if (result && typeof result === "object" && "ok" in result && result.ok === false) return json(400, result, headOnly);
    afterSuccess?.();
    return json(200, { ok: true, data: result }, headOnly);
  } catch (error) { return failure(error, headOnly); }
}

/** Invalidate this plugin's registered catalogs without fabricating preset-selection events. */
export function notifyChatCatalog(_ctx: Context, invalidateSkills?: () => void): void { invalidateSkills?.(); }

/** Read only the selected persisted Session; never activate its Agent or select another Session. */
export async function activeSessionCwds(ctx: Context, sessionId: unknown): Promise<string[]> {
  if (typeof sessionId !== "string" || !sessionId.trim()) return [];
  try {
    using observation = await ctx.sessionQuery.observeSession(sessionId as SessionId);
    const cwd = observation.header.cwd;
    return typeof cwd === "string" && cwd.trim() ? [cwd] : [];
  } catch { return []; }
}

function externalSkillProvider(control: SkillProviderControl, invalidators: Set<() => void>): SkillProvider {
  invalidators.add(control.invalidate);
  control.signal.addEventListener("abort", () => invalidators.delete(control.invalidate), { once: true });
  return {
    name: "dsh-skills-manager-external",
    list: async (options) => listProviderCandidates(options),
    get: async (candidate, options) => getProviderSkill(candidate as ProviderCandidate, options),
  };
}

/** Install overlays in each preset layer, preserving rank and cold-session visibility. */
export async function registerAgentSkillProviders(ctx: Context, invalidators: Set<() => void>): Promise<() => Promise<void>> {
  const presets = ctx.get("agentPresets");
  if (!presets) return async () => {};
  const registrations = new Map<ScopeKey, { dispose: () => Promise<void>; lease: AsyncDisposable }>();
  let stopped = false;
  let pending = Promise.resolve();
  const install = (preset?: string): Promise<void> => {
    const operation = async () => {
      if (stopped) return;
      const lease = await presets.acquireScope(preset);
      if (stopped || registrations.has(lease.key)) { await lease[Symbol.asyncDispose](); return; }
      const scoped = createScope(ctx, lease.key);
      try {
        scoped.ctx.skills.registerProvider(control => externalSkillProvider(control, invalidators));
        registrations.set(lease.key, { dispose: scoped.dispose, lease });
      } catch (error) { await scoped.dispose(); await lease[Symbol.asyncDispose](); throw error; }
    };
    const next = pending.then(operation, operation);
    pending = next.catch(error => { ctx.logger.warn(`skills-manager preset registration: ${String(error)}`); });
    return pending;
  };
  const stopCreated = ctx.on("agent/created", async ({ agent }) => { await install(presets.composedPreset(agent.ctx)); });
  const stopSelected = ctx.on("agent-preset/selected", (_session, preset) => { void install(preset); });
  // Registry diagnostics wait for the Host Loader to settle. Never await them
  // from this Host row's activation: that would wait on our own apply().
  // A late result checks stopped before acquiring/registering anything.
  void presets.list().then(async roster => {
    for (const preset of roster) if (!stopped && !preset.broken) await install(preset.id);
  }).catch(error => { if (!stopped) ctx.logger.warn(`skills-manager preset roster: ${String(error)}`); });
  return async () => {
    // A pending diagnostic can itself be waiting for teardown. Do not join it;
    // install() releases any late lease without exposing another provider.
    stopped = true; stopCreated(); stopSelected();
    for (const { dispose, lease } of registrations.values()) { await dispose(); await lease[Symbol.asyncDispose](); }
    registrations.clear();
  };
}

/** Register exact handlers beneath Cortex's shared authenticated /api carrier. */
export async function apply(ctx: Context, config: Config = {}): Promise<void> {
  if (config.exposeCreateSkillTool !== undefined && typeof config.exposeCreateSkillTool !== "boolean") throw new TypeError("exposeCreateSkillTool must be boolean");
  const log = makeLog();
  const repositories = createRepositoryManager({ log });
  const rootByKey = Object.fromEntries(userRoots().map(root => [root.key, root]));
  const invalidators = new Set<() => void>();
  const afterWrite = () => notifyChatCatalog(ctx, () => { for (const invalidate of invalidators) invalidate(); });
  let mutationQueue: Promise<unknown> = Promise.resolve();
  const enqueueMutation: Serialize = task => {
    const queued = mutationQueue.then(task, task);
    mutationQueue = queued.catch(() => undefined);
    return queued;
  };
  ctx.effect(() => ctx.skills.registerProvider(control => externalSkillProvider(control, invalidators)), "skills-manager global policy");
  const disposePolicies = await registerAgentSkillProviders(ctx, invalidators);
  ctx.effect(() => disposePolicies, "skills-manager preset policies");

  if (config.exposeCreateSkillTool) {
    const tools = ctx.get("tools");
    if (tools === undefined) throw new Error("exposeCreateSkillTool requires Cortex tools");
    ctx.effect(() => tools.register(defineTool({
      name: "create_skill", description: "Create a local Cortex skill only when the user explicitly asks to save reusable instructions.",
      parameters: {
        name: { type: "string", required: true, description: "Skill name." },
        description: { type: "string", required: true, description: "When to use the skill." },
        body: { type: "string", required: true, description: "Markdown instructions." },
      },
      output: { schema: { type: "string" }, render: (_args, value) => [{ type: "text", text: value }] },
      execute: args => enqueueMutation(async () => { const result = await createSkill(args, log); if (result.ok !== false) afterWrite(); return JSON.stringify(result); }),
      presentCall: args => ({ card: "generic", title: "Create Cortex skill", kind: "edit", rawInput: args.name }),
    })), "skills-manager optional create tool");
    ctx.on("tools/pre-execute", async (exec, next) => exec.name === "create_skill"
      ? { kind: "ask", reason: "Create a skill under the selected Cortex home" } : next());
  }

  const handler = async (req: Request): Promise<Response> => {
    const path = new URL(req.url).pathname;
    const headOnly = req.method === "HEAD";
    try {
      const projectOptions: ScopeOptions = { projectCwds: await activeSessionCwds(ctx, req.headers.get("x-dsh-skills-session")) };
      const requestRoot = async (key: string) => rootByKey[key] || (await projectRoots(projectOptions.projectCwds)).find(root => root.key === key);
      const stateWithSources = async () => {
        const snapshot = await state(projectOptions);
        try {
          const sources = await repositories.sources();
          for (const root of snapshot.roots) for (const skill of root.skills) {
            const source = sources[skill.name];
            skill.installSource = source && (source.root || "dsh") === root.key ? source : null;
          }
        } catch (error) { snapshot.warnings.push({ code: "error.repo.state", error: String(error) }); }
        return snapshot;
      };
      if (req.method === "GET" || headOnly) return path === `${API}/state`
        ? run(stateWithSources, undefined, headOnly) : run(() => repositories.list(), undefined, headOnly);
      if (req.headers.get(CLIENT_MARKER_HEADER) !== "1") return json(403, { ok: false, code: "error.proto.forbidden", error: "Missing skill-manager action marker" });
      if (req.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase() !== "application/json") return json(415, { ok: false, error: "content-type must be application/json" });
      const limit = path === `${API}/upload` ? MAX_UPLOAD_BODY_BYTES : 1 << 20;
      const bytes = await readActionBytes(req, limit);
      if (bytes.byteLength > limit) return json(413, { ok: false, code: "error.proto.bodyTooLarge", error: "body too large" });
      let body: RequestBody;
      try {
        const parsed: unknown = JSON.parse(new TextDecoder().decode(bytes) || "{}");
        if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) throw new Error("body must be an object");
        body = parsed as RequestBody;
      } catch { return json(400, { ok: false, code: "error.proto.invalidJson", error: "invalid JSON object" }); }
      if (path === `${API}/browse`) return run(() => browseDirectories(body.path));
      if (path === `${API}/repositories/refresh`) return run(() => repositories.refresh(body));
      return enqueueMutation(async () => {
          switch (path) {
            case "/api/dsh-skills-manager/repositories/add":
              return run(() => repositories.add(body));
            case "/api/dsh-skills-manager/repositories/remove":
              return run(() => repositories.remove(body));
            case "/api/dsh-skills-manager/repositories/detail":
              return run(() => repositories.detail(body));
            case "/api/dsh-skills-manager/repositories/install":
              return run(() => repositories.install(body), afterWrite);
            case "/api/dsh-skills-manager/repositories/uninstall":
              return run(() => repositories.uninstall(body), afterWrite);
            case "/api/dsh-skills-manager/repositories/preview":
              return run(() => repositories.preview(body));
            case "/api/dsh-skills-manager/repositories/update":
              return run(() => repositories.update(body), afterWrite);
            case "/api/dsh-skills-manager/repositories/rollback":
              return run(() => repositories.rollback(body), afterWrite);
            case "/api/dsh-skills-manager/enable":
              return run(async () => setSkillEnabled(await requestRoot(String(body.root || "dsh")), String(body.name || ""), true, log), afterWrite);
            case "/api/dsh-skills-manager/disable":
              return run(async () => setSkillEnabled(await requestRoot(String(body.root || "dsh")), String(body.name || ""), false, log), afterWrite);
            case "/api/dsh-skills-manager/source-enable":
              return run(() => setSourceEnabled(String(body.root || ""), true, log, projectOptions), afterWrite);
            case "/api/dsh-skills-manager/source-disable":
              return run(() => setSourceEnabled(String(body.root || ""), false, log, projectOptions), afterWrite);
            case "/api/dsh-skills-manager/delete":
              return run(async () => deleteSkill(await requestRoot(String(body.root || "dsh")), String(body.name || ""), log), afterWrite);
            case "/api/dsh-skills-manager/trash-restore":
              return run(() => repositories.restoreTrash(String(body.id || ""), projectOptions), afterWrite);
            case "/api/dsh-skills-manager/trash-delete":
              return run(() => permanentlyDeleteTrash(String(body.id || ""), log));
            case "/api/dsh-skills-manager/detail":
              return run(() => skillDetail(String(body.root || "dsh"), String(body.name || ""), projectOptions));
            case "/api/dsh-skills-manager/create":
              return run(async () => createSkill(
                { name: body.name, description: body.description, body: body.body },
                log,
                { root: await requestRoot(String(body.root || "dsh")) },
              ), afterWrite);
            case "/api/dsh-skills-manager/import":
              return run(() => importSkill(String(body.source || ""), log, {
                conflict: body.conflict === "overwrite" ? "overwrite" : "skip",
                dryRun: body.dryRun === true,
              }), body.dryRun === true ? undefined : afterWrite);
            case "/api/dsh-skills-manager/upload":
              return run(() => importUploadedSkill({ name: body.name, entries: body.entries, zip: body.zip }, log, {
                conflict: body.conflict === "overwrite" ? "overwrite" : "skip",
              }), afterWrite);
            default:
              return json(404, { ok: false, code: "error.proto.unknownAction", error: `unknown action: ${path}` });
          }
      });
    } catch (error) { return failure(error, headOnly); }
  };
  for (const suffix of ["state", "repositories", ...POST_PATHS]) {
    ctx.effect(() => ctx.connection.fetch.register({
      path: `${API}/${suffix}`, methods: suffix === "state" || suffix === "repositories" ? ["GET", "HEAD"] : ["POST"],
      requestBody: suffix === "state" || suffix === "repositories" ? "buffered" : "streaming", fetch: handler,
    }), `skills-manager API ${suffix}`);
  }
}
type RequestBody = CreateInput & UploadInput & SkillRequest & RepositoryInput & { root?: string; source?: string; conflict?: string; dryRun?: boolean };
