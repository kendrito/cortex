import assert from 'node:assert/strict';
import test from 'node:test';
import { promises as fs } from 'node:fs';
import { createHash } from 'node:crypto';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { EventEmitter } from 'node:events';
import { Readable } from 'node:stream';
import { openDb } from '@cortex/memos-local-memory/dist/core/storage/connection.js';
import { runMigrations } from '@cortex/memos-local-memory/dist/core/storage/migrator.js';
import * as plugin from '../lib/host.js';

const packageRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const checkout = process.env.CORTEX_CHECKOUT ?? resolve(packageRoot, '../..');
const load = relative => import(pathToFileURL(join(checkout, relative)).href);
const { Context } = await load('vendor/cordis/lib/index.js');
const connectionPlugin = await load('packages/client/connection/lib/index.js');
function response() {
  const chunks = [], state = {};
  return { state, res: Object.assign(new EventEmitter(), {
    writableEnded: false, destroyed: false,
    writeHead(status, headers) { state.status = status; state.headers = headers ?? {}; return this; },
    write(value) { chunks.push(Buffer.from(value)); return true; },
    end(value) { if (value !== undefined) chunks.push(Buffer.from(value)); state.bytes = Buffer.concat(chunks); state.text = state.bytes.toString(); this.writableEnded = true; return this; },
  }) };
}
function request(path, method = 'GET', headers = {}) {
  return Object.assign(Readable.from([]), { url: path, method, headers: { host: 'localhost', ...headers } });
}
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
async function removeFixture(directory) {
  const resolved = await fs.realpath(directory), base = await fs.realpath(tmpdir());
  assert.ok(resolved.startsWith(base + '\\') || resolved.startsWith(base + '/'), 'cleanup stays within the verified temporary directory');
  await fs.rm(resolved, { recursive: true, force: true });
}

test('installed Brain resolves the sibling MemOS viewer and serves its built embedded entry', async () => {
  const expected = await fs.realpath(resolve(packageRoot, '../memos/viewer/dist'));
  const root = plugin.viewerStaticRoot();
  assert.equal(await fs.realpath(root), expected);
  const assets = await plugin.collectBrainStaticAssets(root);
  const html = await fs.readFile(assets.get(`${plugin.BRAIN_VIEW}/brain.html`).file, 'utf8');
  assert.match(html, /type="module"/);
  assert.doesNotMatch(html, /(?:src|href)=["']https?:\/\//);
  const references = [...html.matchAll(/(?:src|href)="\.\/assets\/([^"?#]+\.(?:js|css))"/g)];
  assert.ok(references.length > 0, 'embedded HTML references its local build assets');
  for (const [, name] of references) assert.ok(assets.has(`${plugin.BRAIN_VIEW}/assets/${name}`), name);
});

test('native Cortex Brain authenticates local reads, only changes memory through MemOS, and unloads cleanly', async t => {
  const sandbox = await fs.mkdtemp(join(await fs.realpath(tmpdir()), 'cortex-brain-host-'));
  const home = join(sandbox, 'memory'), staticRoot = join(sandbox, 'viewer');
  await fs.mkdir(join(staticRoot, 'assets'), { recursive: true });
  await fs.writeFile(join(staticRoot, 'brain.html'), '<!doctype html><script type="module" src="./assets/brain-test.js"></script>');
  await fs.writeFile(join(staticRoot, 'assets', 'brain-test.js'), 'export const synthetic = true;');
  await fs.writeFile(join(staticRoot, 'assets', 'brain-test.css'), 'body{background:#101010}');
  await fs.writeFile(join(staticRoot, 'assets', 'private.map'), 'never serve this');
  const ctx = new Context(), routes = [];
  const credentials = { record: undefined, async readRecord() { return this.record; }, async modifyRecord(_key, mutate) { this.record = await mutate(this.record); return this.record; } };
  ctx.provide('credentials', credentials);
  ctx.provide('webServer', { register(route) { routes.push(route); return () => routes.splice(routes.indexOf(route), 1); }, registerUpgrade() { return () => {}; }, tapIndex() { return () => {}; }, port: 0 });
  const priorFetch = globalThis.fetch, fetches = [];
  globalThis.fetch = async (...args) => { fetches.push(args); throw new Error('Unexpected external network request'); };
  try {
    await ctx.plugin(connectionPlugin).await();
    let fiber = ctx.plugin({ inject: ['connection'], apply: owner => plugin.registerBrainHost(owner, { home, staticRoot }) });
    await fiber.await();
    assert.equal(routes.length, 1, 'all entries share the existing native API carrier');
    const call = async req => { const result = response(); await routes[0].handler(req, result.res); return result.state; };
    const login = new URL(ctx.connection.authenticatedUrl('http://localhost/'));
    const authResponse = response();
    ctx.connection.authorizeIndex(request(login.pathname + login.search), authResponse.res);
    const auth = { cookie: authResponse.state.headers['set-cookie'].split(';', 1)[0] };
    const api = path => `${plugin.BRAIN_API}/${path}`;
    const staticPaths = [`${plugin.BRAIN_VIEW}/brain.html`, `${plugin.BRAIN_VIEW}/assets/brain-test.js`, `${plugin.BRAIN_VIEW}/assets/brain-test.css`];

    await t.test('every API and asset GET/HEAD is admitted before body transfer or local data access', async () => {
      for (const path of [api('summary'), api('nodes'), api('edges?nodeId=episode:synthetic'), ...staticPaths]) {
        for (const method of ['GET', 'HEAD']) {
          let reads = 0;
          const unreadable = Object.assign(new Readable({ read() { reads++; this.destroy(new Error('Denied body was read')); } }), { url: path, method, headers: { host: 'localhost' } });
          assert.equal((await call(unreadable)).status, 401, path);
          assert.equal(reads, 0);
          assert.equal((await call(request(path, method, { ...auth, origin: 'https://untrusted.example', 'sec-fetch-site': 'cross-site' }))).status, 403, path);
          assert.equal((await call(request(path, method, { ...auth, host: 'untrusted.example' }))).status, 403, path);
        }
      }
      assert.deepEqual(fetches, []);
      await assert.rejects(fs.stat(home), { code: 'ENOENT' }, 'denied/activation requests never create a memory home');
    });
    await t.test('absent stores return truthful empty pages without creating SQLite or enabling capture', async () => {
      const result = await call(request(api('summary'), 'GET', auth));
      assert.equal(result.status, 200);
      const summary = JSON.parse(result.text);
      assert.deepEqual(summary.totals, { memory: 0, trace: 0, episode: 0, policy: 0, world: 0, skill: 0 });
      assert.deepEqual(summary.storage, { status: 'missing', readOnly: true, captureEnabledByViewer: false });
      for (const kind of ['memory', 'episode', 'policy', 'world', 'skill']) {
        const page = JSON.parse((await call(request(api(`nodes?kind=${kind}`), 'GET', auth))).text);
        assert.equal(page.kind, kind); assert.equal(page.total, 0); assert.deepEqual(page.nodes, []); assert.equal(page.nextCursor, null);
      }
      assert.equal((await call(request(api('edges?nodeId=episode:synthetic'), 'GET', auth))).status, 404);
      await assert.rejects(fs.stat(home), { code: 'ENOENT' });
    });
    await t.test('query mistakes are rejected even when the store is absent', async () => {
      for (const query of ['nodes?kind=unknown', 'nodes?limit=0', 'nodes?limit=101', 'nodes?limit=1.5', 'nodes?kind=memory&kind=world', 'nodes?cursor=invalid',
        'summary?ownerProfileId=', 'summary?ownerWorkspaceIsNull=0', 'summary?ownerWorkspaceIsNull=1&ownerWorkspaceId=x', 'summary?typo=value',
        'summary?namespaceCursor=invalid', 'edges', 'edges?nodeId=invalid', 'edges?nodeId=memory:invalid', 'edges?nodeId=episode:a%252fb']) {
        assert.equal((await call(request(api(query), 'GET', auth))).status, 400, query);
      }
      assert.equal((await call(request(api('nodes'), 'POST', auth))).status, 404, 'no mutation method is registered');
    });
    await t.test('assets are same-origin frames, authenticated, bounded to reviewed files, and do not expose source maps', async () => {
      for (const path of staticPaths) {
        const result = await call(request(path, 'GET', auth));
        assert.equal(result.status, 200); assert.ok(result.bytes.length > 0);
        assert.equal(result.headers['x-frame-options'], 'SAMEORIGIN');
        assert.match(result.headers['content-security-policy'], /frame-ancestors 'self'/);
        assert.match(result.headers['content-security-policy'], /connect-src 'self'/);
        assert.equal(result.headers['cache-control'], 'no-store');
        const head = await call(request(path, 'HEAD', auth));
        assert.equal(head.status, 200); assert.equal(head.bytes.length, 0); assert.equal(Number(head.headers['content-length']), result.bytes.length);
      }
      for (const path of [`${plugin.BRAIN_VIEW}/assets/private.map`, `${plugin.BRAIN_VIEW}/assets/unknown.js`, `${plugin.BRAIN_VIEW}/%2e%2e/package.json`, `${plugin.BRAIN_VIEW}/assets%2fbrain-test.js`]) {
        assert.equal((await call(request(path, 'GET', auth))).status, 404, path);
      }
      assert.equal((await call(request(staticPaths[0], 'POST', auth))).status, 404);
    });
    await t.test('existing memory is read using real migrations/SQLite fixtures without changing any DB bytes', async () => {
      const filename = join(home, 'data', 'memos.db');
      const fixture = openDb({ filepath: filename, agent: 'synthetic-test', wal: false });
      runMigrations(fixture);
      fixture.raw.prepare("INSERT INTO sessions(id,agent,owner_agent_kind,owner_profile_id,owner_workspace_id,started_at,last_seen_at) VALUES('s','cortex','cortex','SAMPLE','workspace',1,1)").run();
      fixture.raw.prepare("INSERT INTO episodes(id,session_id,owner_agent_kind,owner_profile_id,owner_workspace_id,started_at,status,trace_ids_json) VALUES('e','s','cortex','SAMPLE','workspace',1,'closed','[\"t\"]')").run();
      fixture.raw.prepare("INSERT INTO episodes(id,session_id,owner_agent_kind,owner_profile_id,owner_workspace_id,started_at,status,trace_ids_json) VALUES('e2','s','cortex','SAMPLE','workspace',2,'closed','[\"t2\"]')").run();
      fixture.raw.prepare("INSERT INTO traces(id,episode_id,session_id,owner_agent_kind,owner_profile_id,owner_workspace_id,ts,user_text,agent_text,summary,turn_id,agent_thinking,tool_calls_json) VALUES('t','e','s','cortex','SAMPLE','workspace',1,'Synthetic local memory','Synthetic reply','SAMPLE only',1,'PRIVATE_THINKING','[{\"secret\":\"PRIVATE_TOOL\"}]')").run();
      fixture.raw.prepare("INSERT INTO traces(id,episode_id,session_id,owner_agent_kind,owner_profile_id,owner_workspace_id,ts,user_text,agent_text,summary,turn_id,agent_thinking,tool_calls_json) VALUES('t2','e2','s','cortex','SAMPLE','workspace',2,'Synthetic second memory','Synthetic reply','SAMPLE second',2,'PRIVATE_THINKING','[]')").run();
      fixture.raw.prepare("INSERT INTO policies(id,owner_agent_kind,owner_profile_id,owner_workspace_id,title,trigger,procedure,verification,boundary,status,source_trace_ids_json,source_episodes_json,created_at,updated_at,confidence) VALUES('p','cortex','SAMPLE','workspace','SAMPLE policy','On change','Inspect evidence','Sources agree','Synthetic only','active','[\"t\",\"t2\"]','[\"e\",\"e2\"]',2,3,0.8)").run();
      fixture.close();
      const before = digest(await fs.readFile(filename));
      const summary = JSON.parse((await call(request(api('summary'), 'GET', auth))).text);
      assert.equal(summary.totals.memory, 2); assert.equal(summary.totals.trace, 2); assert.equal(summary.totals.episode, 2);
      assert.equal(summary.storage.status, 'ready');
      const pageResult = await call(request(api('nodes?kind=memory&ownerProfileId=SAMPLE'), 'GET', auth));
      const page = JSON.parse(pageResult.text);
      assert.equal(page.total, 2); assert.ok(page.nodes.some(node => node.title === 'SAMPLE only'));
      assert.equal(page.nodes[0].namespace.ownerProfileId, 'SAMPLE');
      assert.ok(!pageResult.text.includes('PRIVATE_THINKING')); assert.ok(!pageResult.text.includes('PRIVATE_TOOL'));
      const edges = JSON.parse((await call(request(api(`edges?nodeId=${encodeURIComponent(page.nodes[0].id)}`), 'GET', auth))).text);
      assert.equal(edges.total, 1); assert.equal(edges.edges[0].relation, 'part_of');
      const first = JSON.parse((await call(request(api('nodes?kind=memory&limit=1&ownerProfileId=SAMPLE'), 'GET', auth))).text);
      assert.equal(first.nodes.length, 1); assert.ok(first.nextCursor);
      const second = JSON.parse((await call(request(api(`nodes?kind=memory&limit=1&ownerProfileId=SAMPLE&cursor=${first.nextCursor}`), 'GET', auth))).text);
      assert.equal(second.nodes.length, 1); assert.notEqual(first.nodes[0].id, second.nodes[0].id); assert.equal(second.nextCursor, null);
      assert.equal((await call(request(api(`nodes?kind=memory&limit=1&ownerProfileId=OTHER&cursor=${first.nextCursor}`), 'GET', auth))).status, 400, 'cursor cannot be reused across namespace filters');
      const firstEvidence = JSON.parse((await call(request(api('edges?nodeId=policy:p&limit=1&ownerProfileId=SAMPLE'), 'GET', auth))).text);
      assert.equal(firstEvidence.total, 4); assert.equal(firstEvidence.edges.length, 1); assert.ok(firstEvidence.nextCursor);
      const secondEvidence = JSON.parse((await call(request(api(`edges?nodeId=policy:p&limit=1&ownerProfileId=SAMPLE&cursor=${firstEvidence.nextCursor}`), 'GET', auth))).text);
      assert.equal(secondEvidence.edges.length, 1); assert.notEqual(firstEvidence.edges[0].id, secondEvidence.edges[0].id);
      assert.equal((await call(request(api(`edges?nodeId=policy:p&limit=1&ownerProfileId=OTHER&cursor=${firstEvidence.nextCursor}`), 'GET', auth))).status, 400);
      assert.equal(JSON.parse((await call(request(api('nodes?kind=memory&ownerProfileId=OTHER'), 'GET', auth))).text).total, 0);
      assert.equal((await call(request(api('summary'), 'HEAD', auth))).bytes.length, 0);
      assert.equal(digest(await fs.readFile(filename)), before, 'all readonly queries preserve the exact SQLite bytes');
      assert.deepEqual(fetches, []);
      // A new writer can reopen immediately: every request closed its read handle.
      const reopened = openDb({ filepath: filename, agent: 'synthetic-test', wal: false }); reopened.close();
    });
    await t.test('unprepared schemas report a clear error and are never migrated', async () => {
      const badHome = join(sandbox, 'old-memory');
      const filename = join(badHome, 'data', 'memos.db');
      const fixture = openDb({ filepath: filename, agent: 'synthetic-old', wal: false }); fixture.raw.exec('CREATE TABLE untouched (id INTEGER)'); fixture.close();
      const before = digest(await fs.readFile(filename));
      await fiber.dispose();
      const badFiber = ctx.plugin({ inject: ['connection'], apply: owner => plugin.registerBrainHost(owner, { home: badHome, staticRoot }) });
      await badFiber.await();
      const result = await call(request(api('summary'), 'GET', auth));
      assert.equal(result.status, 503); assert.equal(JSON.parse(result.text).error.code, 'memory_schema_unavailable');
      assert.match(JSON.parse(result.text).error.message, /prepare/);
      await badFiber.dispose();
      await assert.rejects(plugin.readStoredBrain(badHome, 'summary', new URLSearchParams()), /no such table/);
      assert.equal(digest(await fs.readFile(filename)), before);
      fiber = ctx.plugin({ inject: ['connection'], apply: owner => plugin.registerBrainHost(owner, { home, staticRoot }) });
      await fiber.await();
    });
    await t.test('memory controls are authenticated, need a JSON body and a confirmed reset, and go to MemOS', async () => {
      const memory = `${plugin.MEMORY_API}`;
      const post = (path, body, headers = {}) => Object.assign(Readable.from([Buffer.from(JSON.stringify(body))]), {
        url: path, method: 'POST', headers: { host: 'localhost', 'content-type': 'application/json', ...auth, ...headers },
      });
      assert.equal((await call(request(memory, 'GET'))).status, 401, 'status needs the Cortex session');
      assert.equal((await call(post(`${memory}/enabled`, { enabled: false }, { cookie: '' }))).status, 401);
      assert.equal((await call(post(`${memory}/reset`, { confirm: 'reset' }, { origin: 'https://untrusted.example', 'sec-fetch-site': 'cross-site' }))).status, 403);

      const absent = await call(request(memory, 'GET', auth));
      assert.equal(absent.status, 503); assert.equal(JSON.parse(absent.text).error.code, 'memory_unavailable');

      const received = [];
      let state = { enabled: true, running: true };
      const stop = ctx.on('memos-local-memory/control', async requestBody => {
        received.push(requestBody);
        if (requestBody.action === 'set-enabled') state = { enabled: requestBody.enabled, running: requestBody.enabled };
        return state;
      });
      try {
        assert.deepEqual(JSON.parse((await call(request(memory, 'GET', auth))).text), { enabled: true, running: true });
        const off = await call(post(`${memory}/enabled`, { enabled: false }));
        assert.equal(off.status, 200); assert.deepEqual(JSON.parse(off.text), { enabled: false, running: false });
        assert.equal((await call(post(`${memory}/enabled`, { enabled: 'no' }))).status, 400);
        assert.equal((await call(post(`${memory}/enabled`, { enabled: true }, { 'content-type': 'text/plain' }))).status, 400);
        assert.equal((await call(post(`${memory}/reset`, {}))).status, 400, 'a reset must be confirmed');
        assert.equal((await call(post(`${memory}/reset`, { confirm: 'reset' }))).status, 200);
        assert.deepEqual(received.map(item => item.action), ['status', 'set-enabled', 'reset']);
      } finally { stop(); }
    });
    await t.test('junctions and late replaced linked assets are refused', async () => {
      const outside = join(sandbox, 'outside'); await fs.mkdir(outside);
      const linked = join(sandbox, 'linked'); await fs.symlink(outside, linked, 'junction');
      await assert.rejects(plugin.resolveBrainHome({ home: linked }), /symbolic links|junctions/);
      const asset = join(staticRoot, 'assets', 'brain-test.js'), target = join(outside, 'brain.js');
      await fs.writeFile(target, 'private outside file'); await fs.unlink(asset);
      try { await fs.symlink(target, asset, 'file'); }
      catch (error) { if (error.code === 'EPERM') { await fs.writeFile(asset, 'restored'); return; } throw error; }
      assert.equal((await call(request(staticPaths[1], 'GET', auth))).status, 404);
      await fs.unlink(asset); await fs.writeFile(asset, 'restored');
    });
    await t.test('unloading removes both data and asset routes from the shared connection', async () => {
      await fiber.dispose();
      for (const path of [api('summary'), plugin.MEMORY_API, ...staticPaths]) assert.equal((await call(request(path, 'GET', auth))).status, 404, path);
      assert.deepEqual(fetches, []);
    });
  } finally {
    globalThis.fetch = priorFetch;
    await ctx.fiber.dispose();
    await removeFixture(sandbox);
  }
});

test('ambient homes must be existing local directories and match the owning MemOS precedence', async () => {
  const sandbox = await fs.mkdtemp(join(await fs.realpath(tmpdir()), 'cortex-brain-home-'));
  try {
    const cortex = join(sandbox, 'cortex'), memory = join(sandbox, 'memos'); await fs.mkdir(memory);
    assert.equal(await plugin.resolveBrainHome({}, { CORTEX_HOME: cortex, MEMOS_HOME: memory }), memory);
    await assert.rejects(plugin.resolveBrainHome({}, { CORTEX_HOME: cortex, MEMOS_HOME: join(sandbox, 'missing') }), { code: 'ENOENT' });
    await assert.rejects(plugin.resolveBrainHome({}, { CORTEX_HOME: cortex, MEMOS_HOME: '\\\\server\\share' }), /absolute local/);
    assert.equal(await plugin.resolveBrainHome({ home: join(sandbox, 'explicit') }, { CORTEX_HOME: cortex, MEMOS_HOME: memory }), memory);
    assert.equal(await plugin.resolveBrainHome({ home: join(sandbox, 'explicit') }, { CORTEX_HOME: cortex, MEMOS_CONFIG_FILE: join(memory, 'config.yaml') }), memory);
    assert.equal(await plugin.resolveBrainHome({}, { CORTEX_HOME: cortex }), join(cortex, 'memos-plugin'));
    await assert.rejects(plugin.resolveBrainHome({ home: 'relative/path' }), /absolute local/);
    await assert.rejects(plugin.resolveBrainHome({ home: '\\\\server\\share' }), /absolute local/);
  } finally { await removeFixture(sandbox); }
});
