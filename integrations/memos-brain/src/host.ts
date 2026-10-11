/** Read-only Cortex Brain: authenticated local files and bounded SQLite reads. */
import type { Context } from '@cortex/cordis';
import type {} from '@cortex/client-connection';
import { resolveCortexHome } from '@cortex/home-paths';
import Schema from '@cortex/schemastery';
import { lstat, readFile, readdir, realpath } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { basename, dirname, isAbsolute, join, relative, resolve, sep } from 'node:path';
import { createBrainReader, BrainQueryError, isKind } from '@cortex/memos-local-memory/dist/core/brain/read.js';
import { openDb } from '@cortex/memos-local-memory/dist/core/storage/connection.js';
import type { BrainFilter, BrainKind } from '@cortex/memos-local-memory/dist/agent-contract/brain.js';
import type { MemoryControlRequest, MemoryControlStatus } from '@cortex/memos-local-memory/dist/adapters/deepseek-harness/index.js';

export const name = 'memos-brain';
export const inject = ['connection'];
export interface Config { home?: string }
export const Config: Schema<Config> = Schema.object({ home: Schema.string().default('') });
export const BRAIN_API = '/api/memos-brain/api/v1/brain';
export const BRAIN_VIEW = '/api/memos-brain/view';
export const MEMORY_API = '/api/memos-brain/api/v1/memory';
/** MemOS's control event; the literal keeps this module free of a runtime MemOS import. */
const MEMORY_CONTROL_EVENT = 'memos-local-memory/control';
const KINDS: BrainKind[] = ['memory', 'episode', 'policy', 'world', 'skill'];
const TYPES: Record<string, string> = { '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.html': 'text/html; charset=utf-8' };
const FRAME_CSP = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; font-src 'self'; object-src 'none'; frame-src 'none'; frame-ancestors 'self'; base-uri 'none'; form-action 'none'";

function localPath(value: unknown): string {
  if (typeof value !== 'string' || !value.trim() || !isAbsolute(value) || /[\x00\r\n]/.test(value) || /^[\\/]{2}/.test(value) || /^\\[?.]\\/.test(value)) {
    throw new TypeError('Brain memory home must be an absolute local directory');
  }
  return resolve(value);
}

/** Forbid links and junctions at every existing path component; never create a path. */
export async function assertUnlinkedPath(filename: string): Promise<void> {
  let target = resolve(filename);
  for (;;) {
    const info = await lstat(target).catch(error => {
      if (error.code === 'ENOENT') return null;
      throw error;
    });
    if (info?.isSymbolicLink()) throw new Error('Brain refuses symbolic links or junctions');
    const parent = dirname(target);
    if (parent === target) return;
    target = parent;
  }
}

/** Match MemOS's owning-home precedence; invalid ambient overrides fail visibly. */
export async function resolveBrainHome(config: Config = {}, env: NodeJS.ProcessEnv = process.env): Promise<string> {
  if (config.home !== undefined && typeof config.home !== 'string') throw new TypeError('Brain home must be a string');
  if (env.MEMOS_HOME?.trim()) {
    const candidate = localPath(env.MEMOS_HOME);
    await assertUnlinkedPath(candidate);
    if (!(await lstat(candidate)).isDirectory()) throw new TypeError('MEMOS_HOME must name an existing local directory');
    return candidate;
  }
  if (env.MEMOS_CONFIG_FILE?.trim()) {
    const filename = localPath(env.MEMOS_CONFIG_FILE), home = dirname(filename);
    await assertUnlinkedPath(filename);
    if (!(await lstat(home)).isDirectory()) throw new TypeError('MEMOS_CONFIG_FILE must name a file in an existing local directory');
    return home;
  }
  if (config.home?.trim()) {
    const home = localPath(config.home);
    await assertUnlinkedPath(home);
    return home;
  }
  const home = localPath(join(resolveCortexHome(undefined, env), 'memos-plugin'));
  await assertUnlinkedPath(home);
  return home;
}

function json(status: number, value: unknown, head = false): Response {
  const text = JSON.stringify(value);
  return new Response(head ? null : text, { status, headers: {
    'content-type': 'application/json; charset=utf-8', 'content-length': String(Buffer.byteLength(text)),
    'cache-control': 'no-store', 'x-content-type-options': 'nosniff',
  } });
}

function onlyParams(params: URLSearchParams, allowed: string[]): void {
  for (const key of params.keys()) {
    if (!allowed.includes(key) || params.getAll(key).length !== 1) throw new BrainQueryError(`Invalid or repeated Brain parameter: ${key}`);
  }
}
function filterParams(params: URLSearchParams): BrainFilter {
  const result: BrainFilter = {};
  for (const key of ['ownerAgentKind', 'ownerProfileId', 'ownerWorkspaceId'] as const) {
    const value = params.get(key);
    if (value !== null) {
      if (!value || value.length > 1_024) throw new BrainQueryError(`Invalid ${key}`);
      result[key] = value;
    }
  }
  if (params.has('ownerWorkspaceIsNull')) {
    if (params.get('ownerWorkspaceIsNull') !== '1' || params.has('ownerWorkspaceId')) throw new BrainQueryError('ownerWorkspaceIsNull must be 1 and exclude ownerWorkspaceId');
    result.ownerWorkspaceId = null;
  }
  return result;
}
function limitParam(params: URLSearchParams): number | undefined {
  const value = params.get('limit');
  if (value === null) return undefined;
  if (!/^\d+$/.test(value) || Number(value) < 1 || Number(value) > 100) throw new BrainQueryError('limit must be an integer from 1 to 100');
  return Number(value);
}
function validCursor(value: string | null, kind: string, scope: BrainFilter): string | undefined {
  if (value === null) return undefined;
  try {
    if (value.length > 16_384 || !/^[a-zA-Z0-9_-]+$/.test(value)) throw new Error('encoding');
    const parsed = JSON.parse(Buffer.from(value, 'base64url').toString('utf8'));
    if (!parsed || parsed.version !== 1 || parsed.kind !== kind || JSON.stringify(parsed.scope) !== JSON.stringify(scope) || typeof parsed.after !== 'string') throw new Error('query');
    return value;
  } catch { throw new BrainQueryError('Brain cursor does not match this query; restart pagination'); }
}
function validNodeId(value: string | null): string {
  if (!value || value.length > 8_192) throw new BrainQueryError('nodeId is required and must name a Brain node');
  const colon = value.indexOf(':'), kind = value.slice(0, colon), encoded = value.slice(colon + 1);
  if (!isKind(kind)) throw new BrainQueryError('Invalid Brain node kind');
  try {
    if (kind === 'memory') {
      if (!/^[a-zA-Z0-9_-]+$/.test(encoded)) throw new Error('encoding');
      const decoded = Buffer.from(encoded, 'base64url').toString('utf8');
      if (Buffer.from(decoded).toString('base64url') !== encoded) throw new Error('canonical');
      const group = JSON.parse(decoded);
      if (!Array.isArray(group) || group.length !== 5 || typeof group[0] !== 'string' || typeof group[1] !== 'string' || (group[2] !== null && typeof group[2] !== 'string') || typeof group[3] !== 'string' || !Number.isSafeInteger(group[4])) throw new Error('identity');
    } else if (!encoded || encodeURIComponent(decodeURIComponent(encoded)) !== encoded) throw new Error('canonical');
  } catch { throw new BrainQueryError('Invalid Brain node identity'); }
  return value;
}

/** Validate before opening SQLite, including when the local store does not exist yet. */
export function brainQuery(action: string, params: URLSearchParams) {
  if (!['summary', 'nodes', 'edges'].includes(action)) throw new BrainQueryError('Unknown Brain action');
  const common = ['ownerAgentKind', 'ownerProfileId', 'ownerWorkspaceId', 'ownerWorkspaceIsNull'];
  onlyParams(params, [...common, ...(action === 'summary' ? ['namespaceCursor'] : action === 'nodes' ? ['kind', 'limit', 'cursor'] : ['nodeId', 'limit', 'cursor'])]);
  const filter = filterParams(params);
  if (action === 'summary') return { ...filter, namespaceCursor: validCursor(params.get('namespaceCursor'), 'namespaces', filter) };
  const limit = limitParam(params);
  if (action === 'nodes') {
    const kind = params.get('kind') ?? 'memory';
    if (!isKind(kind)) throw new BrainQueryError('kind must be memory, episode, policy, world, or skill');
    return { ...filter, kind, limit, cursor: validCursor(params.get('cursor'), kind, filter) };
  }
  const nodeId = validNodeId(params.get('nodeId'));
  return { ...filter, nodeId, limit, cursor: validCursor(params.get('cursor'), `edges:${nodeId}`, filter) };
}

function emptyResult(action: string, query: ReturnType<typeof brainQuery>): unknown {
  if (action === 'summary') return { generatedAt: Date.now(), consistency: 'live', totals: { memory: 0, trace: 0, episode: 0, policy: 0, world: 0, skill: 0 }, namespaces: [], namespacesNextCursor: null,
    storage: { status: 'missing', readOnly: true, captureEnabledByViewer: false } };
  if (action === 'nodes') return { generatedAt: Date.now(), consistency: 'live', kind: 'kind' in query ? query.kind : 'memory', total: 0, nodes: [], edges: [], nextCursor: null };
  throw new BrainQueryError('Brain node is absent from the local memory store', 404);
}

/** Open only for this read; never bootstrap memory, migrate schema, or retain a SQLite handle. */
export async function readStoredBrain(home: string, action: string, params: URLSearchParams): Promise<unknown> {
  const query = brainQuery(action, params);
  const filename = join(home, 'data', 'memos.db');
  await assertUnlinkedPath(filename);
  const exists = await lstat(filename).catch(error => { if (error.code === 'ENOENT') return null; throw error; });
  if (!exists) return emptyResult(action, query);
  if (!exists.isFile()) throw new Error('Memory store must be a regular SQLite file');
  const db = openDb({ filepath: filename, agent: 'cortex', readonly: true, busyTimeoutMs: 1_000 });
  try {
    db.raw.pragma('query_only = ON');
    const reader = createBrainReader(db);
    if (action === 'summary') return { ...reader.summary(query), storage: { status: 'ready', readOnly: true, captureEnabledByViewer: false } };
    if (action === 'nodes') return reader.page(query as Parameters<typeof reader.page>[0]);
    return reader.edges(query as Parameters<typeof reader.edges>[0]);
  } finally { db.close(); }
}

export function viewerStaticRoot(): string {
  const require = createRequire(import.meta.url);
  return join(dirname(require.resolve('@cortex/memos-local-memory/package.json')), 'viewer', 'dist');
}

/** Static URLs are enumerated from reviewed build output, never derived from a request path. */
export async function collectBrainStaticAssets(root: string): Promise<Map<string, { file: string; type: string }>> {
  const directory = localPath(root);
  await assertUnlinkedPath(directory);
  const canonical = await realpath(directory);
  const assets = new Map<string, { file: string; type: string }>();
  const entry = join(directory, 'brain.html');
  await assertUnlinkedPath(entry);
  if (!(await lstat(entry)).isFile()) throw new Error('The built Cortex Brain entry is missing');
  assets.set(`${BRAIN_VIEW}/brain.html`, { file: entry, type: TYPES['.html']! });
  const assetDir = join(directory, 'assets');
  await assertUnlinkedPath(assetDir);
  for (const item of await readdir(assetDir, { withFileTypes: true })) {
    if (item.isSymbolicLink()) throw new Error('Brain static assets cannot contain symbolic links');
    if (!item.isFile() || !/^[A-Za-z0-9._-]+\.(?:js|css)$/.test(item.name)) continue;
    const file = join(assetDir, item.name);
    const target = await realpath(file), child = relative(canonical, target);
    if (child.startsWith(`..${sep}`) || child === '..' || isAbsolute(child)) throw new Error('Brain asset escapes its local build directory');
    assets.set(`${BRAIN_VIEW}/assets/${item.name}`, { file, type: TYPES[item.name.endsWith('.js') ? '.js' : '.css']! });
  }
  return assets;
}

function apiFailure(error: unknown, head: boolean): Response {
  if (error instanceof BrainQueryError) return json(error.status, { error: { code: 'invalid_argument', message: error.message } }, head);
  const message = error instanceof Error ? error.message : '';
  if (/no such (?:table|column)|has no column|not a database|malformed/i.test(message)) return json(503, { error: { code: 'memory_schema_unavailable', message: 'Stored memory is not ready for this Brain viewer. Enable or update the memory plugin to prepare it.' } }, head);
  return json(503, { error: { code: 'memory_store_unavailable', message: 'The local memory store is unavailable. No memory data was changed.' } }, head);
}

/** Reads a small JSON request body; anything else is refused before memory is touched. */
async function readControlBody(request: Request): Promise<Record<string, unknown>> {
  if (!/^application\/json\b/i.test(request.headers.get('content-type') ?? '')) throw new BrainQueryError('Memory controls take a JSON body');
  if (request.headers.get('sec-fetch-site') === 'cross-site') throw new BrainQueryError('Memory controls only answer the Cortex page');
  const text = await request.text();
  if (text.length > 1_024) throw new BrainQueryError('Memory control body is too large');
  let value: unknown;
  try { value = JSON.parse(text || '{}'); } catch { throw new BrainQueryError('Memory control body must be JSON'); }
  if (typeof value !== 'object' || value === null || Array.isArray(value)) throw new BrainQueryError('Memory control body must be an object');
  return value as Record<string, unknown>;
}

/**
 * The person's memory controls: whether memory is on, and wiping what it learned.
 * MemOS owns its store, so each request goes to it as one Cordis event and is
 * answered with the memory's status afterwards.
 */
export function registerMemoryControls(ctx: Context): void {
  const ask = async (request: MemoryControlRequest): Promise<Response> => {
    const status = await ctx.serial(MEMORY_CONTROL_EVENT, request) as MemoryControlStatus | undefined;
    return status === undefined
      ? json(503, { error: { code: 'memory_unavailable', message: 'Cortex memory is not loaded, so it cannot be switched or reset.' } })
      : json(200, status);
  };
  ctx.effect(() => ctx.connection.fetch.register({ path: MEMORY_API, methods: ['GET'], requestBody: 'buffered', fetch: async () => {
    try { return await ask({ action: 'status' }); } catch (error) { return apiFailure(error, false); }
  } }), 'memos-brain memory status');
  ctx.effect(() => ctx.connection.fetch.register({ path: `${MEMORY_API}/enabled`, methods: ['POST'], requestBody: 'buffered', fetch: async request => {
    try {
      const body = await readControlBody(request);
      if (typeof body.enabled !== 'boolean') throw new BrainQueryError('enabled must be true or false');
      return await ask({ action: 'set-enabled', enabled: body.enabled });
    } catch (error) { return apiFailure(error, false); }
  } }), 'memos-brain memory switch');
  ctx.effect(() => ctx.connection.fetch.register({ path: `${MEMORY_API}/reset`, methods: ['POST'], requestBody: 'buffered', fetch: async request => {
    try {
      const body = await readControlBody(request);
      if (body.confirm !== 'reset') throw new BrainQueryError('A reset must be confirmed with confirm: "reset"');
      return await ask({ action: 'reset' });
    } catch (error) { return apiFailure(error, false); }
  } }), 'memos-brain memory reset');
}

/** Testable registration boundary; all physical admission remains owned by Cortex Connection. */
export async function registerBrainHost(ctx: Context, options: { home: string; staticRoot: string }): Promise<void> {
  const home = localPath(options.home);
  await assertUnlinkedPath(home);
  const files = await collectBrainStaticAssets(options.staticRoot);
  registerMemoryControls(ctx);
  for (const action of ['summary', 'nodes', 'edges']) {
    ctx.effect(() => ctx.connection.fetch.register({ path: `${BRAIN_API}/${action}`, methods: ['GET', 'HEAD'], requestBody: 'buffered', fetch: async request => {
      const head = request.method === 'HEAD';
      try { return json(200, await readStoredBrain(home, action, new URL(request.url).searchParams), head); }
      catch (error) { return apiFailure(error, head); }
    } }), `memos-brain ${action}`);
  }
  for (const [path, asset] of files) {
    ctx.effect(() => ctx.connection.fetch.register({ path, methods: ['GET', 'HEAD'], requestBody: 'buffered', fetch: async request => {
      try {
        await assertUnlinkedPath(asset.file);
        const bytes = await readFile(asset.file);
        return new Response(request.method === 'HEAD' ? null : bytes, { headers: {
          'content-type': asset.type, 'content-length': String(bytes.byteLength), 'cache-control': 'no-store',
          'content-security-policy': FRAME_CSP, 'x-frame-options': 'SAMEORIGIN', 'x-content-type-options': 'nosniff',
          'referrer-policy': 'no-referrer', 'cross-origin-resource-policy': 'same-origin',
        } });
      } catch { return json(404, { error: { code: 'not_found', message: 'Brain asset is unavailable' } }, request.method === 'HEAD'); }
    } }), `memos-brain ${basename(asset.file)}`);
  }
}

/** Add the viewer only: memory capture and model providers are separate Cortex components. */
export async function apply(ctx: Context, config: Config = {}): Promise<void> {
  await registerBrainHost(ctx, { home: await resolveBrainHome(config), staticRoot: viewerStaticRoot() });
}
