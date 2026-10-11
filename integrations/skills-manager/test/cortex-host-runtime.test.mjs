import assert from 'node:assert/strict';
import test from 'node:test';
import { promises as fs } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { EventEmitter } from 'node:events';
import { Readable } from 'node:stream';

const checkout = process.env.CORTEX_CHECKOUT;
assert.ok(checkout, 'Set CORTEX_CHECKOUT to the built Cortex checkout');
const load = relative => import(pathToFileURL(join(checkout, relative)).href);
const { Context } = await load('vendor/cordis/lib/index.js');
const connectionPlugin = await load('packages/client/connection/lib/index.js');
const { SkillRegistry } = await load('packages/skill/skill/lib/index.js');
const filesystem = await load('packages/skill/skill-filesystem/lib/index.js');
const { createScope } = await load('packages/core/scope/lib/index.js');
const plugin = await import('../lib/index.js');
const API = '/api/dsh-skills-manager';
const posts = ['browse', 'repositories/refresh', 'repositories/add', 'repositories/remove', 'repositories/detail',
  'repositories/install', 'repositories/uninstall', 'repositories/preview', 'repositories/update', 'repositories/rollback',
  'enable', 'disable', 'source-enable', 'source-disable', 'delete', 'trash-restore', 'trash-delete', 'detail', 'create', 'import', 'upload'];
function response() {
  const chunks = [];
  const state = {};
  return { state, res: Object.assign(new EventEmitter(), {
    writableEnded: false, destroyed: false,
    writeHead(status, headers) { state.status = status; state.headers = headers ?? {}; return this; },
    write(value) { chunks.push(Buffer.from(value)); return true; },
    end(value) { if (value !== undefined) chunks.push(Buffer.from(value)); state.text = Buffer.concat(chunks).toString(); this.writableEnded = true; return this; },
  }) };
}
function request(path, method = 'GET', headers = {}, body = '') {
  return Object.assign(Readable.from(body ? [Buffer.from(body)] : []), {
    url: path, method, headers: { host: 'localhost', ...headers },
  });
}

test('real Cortex admission fences every route and native skill policy survives enable/disable/unload', async t => {
  const sandbox = await fs.mkdtemp(join(await fs.realpath(tmpdir()), 'cortex-skills-host-'));
  const oldEnv = { ...process.env };
  process.env.HOME = process.env.USERPROFILE = join(sandbox, 'user');
  process.env.CORTEX_HOME = join(sandbox, 'cortex');
  process.env.CORTEX_AGENTS_HOME = join(sandbox, 'agents');
  const ctx = new Context();
  const routes = [];
  const credentials = { record: undefined,
    async readRecord() { return this.record; },
    async modifyRecord(_key, mutate) { this.record = await mutate(this.record); return this.record; },
  };
  ctx.provide('credentials', credentials);
  ctx.provide('webServer', {
    register(route) { routes.push(route); return () => routes.splice(routes.indexOf(route), 1); },
    registerUpgrade() { return () => {}; }, tapIndex() { return () => {}; }, port: 0,
  });
  let observedSessions = 0;
  ctx.provide('sessionQuery', { async observeSession() { observedSessions++; throw new Error('No synthetic Session requested'); } });
  const priorFetch = globalThis.fetch;
  const fetches = [];
  globalThis.fetch = async (...args) => { fetches.push(args); throw new Error('Unexpected external request'); };
  try {
    await ctx.plugin(connectionPlugin).await();
    await ctx.plugin(owner => { new SkillRegistry(owner); }).await();
    await ctx.plugin(filesystem, { cortexHome: process.env.CORTEX_HOME, agentsHome: process.env.CORTEX_AGENTS_HOME, watch: false }).await();
    const fiber = ctx.plugin(plugin);
    await fiber.await();
    assert.equal(routes.length, 1, 'only the native /api carrier owns an HTTP route');
    const call = async req => { const result = response(); await routes[0].handler(req, result.res); return result.state; };
    const login = new URL(ctx.connection.authenticatedUrl('http://localhost/'));
    const authResponse = response();
    ctx.connection.authorizeIndex(request(login.pathname + login.search), authResponse.res);
    const cookie = authResponse.state.headers['set-cookie'].split(';', 1)[0];
    const auth = { cookie };
    const mutation = { ...auth, 'content-type': 'application/json', 'x-dsh-skills-manager': '1' };

    await t.test('all GET/HEAD/POST routes refuse auth and cross-origin requests before reading a body or Session', async () => {
      for (const [path, method] of [...['state', 'repositories'].flatMap(path => [[path, 'GET'], [path, 'HEAD']]), ...posts.map(path => [path, 'POST'])]) {
        let bodyReads = 0;
        const unreadable = Object.assign(new Readable({ read() { bodyReads++; this.destroy(new Error('Admission read a denied body')); } }), {
          url: `${API}/${path}`, method, headers: { host: 'localhost', 'content-type': 'application/json', 'x-dsh-skills-session': 'synthetic-id' },
        });
        assert.equal((await call(unreadable)).status, 401, path);
        assert.equal(bodyReads, 0, path);
        assert.equal((await call(request(`${API}/${path}`, method, { ...mutation, origin: 'https://untrusted.example', 'sec-fetch-site': 'cross-site' }, '{}'))).status, 403, path);
      }
      assert.equal(observedSessions, 0);
      assert.deepEqual(fetches, []);
    });
    await t.test('opening cached state and repositories uses no network; mutations require their marker and JSON', async () => {
      for (const path of ['state', 'repositories']) {
        const result = await call(request(`${API}/${path}`, 'GET', auth));
        assert.equal(result.status, 200);
        assert.equal(JSON.parse(result.text).ok, true);
        assert.equal(result.headers['cache-control'], 'no-store');
      }
      for (const path of posts) assert.equal((await call(request(`${API}/${path}`, 'POST', auth, '{}'))).status, 403, path);
      assert.equal((await call(request(`${API}/create`, 'POST', { ...auth, 'x-dsh-skills-manager': '1' }, '{}'))).status, 415);
      assert.equal((await call(request(`${API}/create`, 'POST', mutation, '[]'))).status, 400);
      assert.equal((await call(request(`${API}/create`, 'POST', mutation, '{bad'))).status, 400);
      assert.equal((await call(request(`${API}/create`, 'POST', mutation, JSON.stringify({ body: 'x'.repeat(1 << 20) })))).status, 413);
      for (const path of ['plugin-update', 'plugin-update/check', 'plugin-update/install']) {
        assert.equal((await call(request(`${API}/${path}`, 'POST', mutation, '{}'))).status, 404, 'no automatic updater route is mounted');
      }
      assert.deepEqual(fetches, []);
    });
    await t.test('authorized bodies are bounded during streaming and excessive upload lengths are refused', async () => {
      const shared = ctx.connection.createSharedFetchHandler('/api');
      assert.equal(shared.requestBodyMode({ method: 'POST', url: new URL(`http://localhost${API}/create`) }), 'streaming');
      let pulled = 0, cancelled = false;
      const body = new ReadableStream({ pull(controller) { pulled++; controller.enqueue(new Uint8Array(65536)); }, cancel() { cancelled = true; } });
      const result = await shared.fetch(new Request(`http://localhost${API}/create`, { method: 'POST', headers: mutation, body, duplex: 'half' }));
      assert.equal(result.status, 413);
      assert.equal(cancelled, true);
      assert.ok(pulled <= 20, 'read stops near1MiB instead of buffering an unbounded request');
      const upload = await shared.fetch(new Request(`http://localhost${API}/upload`, { method: 'POST', headers: { ...mutation, 'content-length': String((88 << 20) + 1) } }));
      assert.equal(upload.status, 413);
    });
    await t.test('native filesystem winner is masked when disabled and restricted flags survive reenabling', async () => {
      const filename = join(process.env.CORTEX_HOME, 'skills', 'manual-sample', 'SKILL.md');
      await fs.mkdir(dirname(filename), { recursive: true });
      const content = '---\nname: manual-sample\ndescription: Synthetic manual skill\ndisable-model-invocation: true\n---\nSynthetic body.\n';
      await fs.writeFile(filename, content);
      // Native provider was not previously queried; discovery observes this fixture.
      let skill = (await ctx.skills.list()).find(item => item.name === 'manual-sample');
      assert.ok(skill);
      assert.equal(skill.provider, 'filesystem');
      assert.deepEqual(skill.invocation, { modelInvocable: false, userInvocable: true });
      for (const [action, expected] of [['disable', { modelInvocable: false, userInvocable: false }], ['enable', { modelInvocable: false, userInvocable: true }]]) {
        const result = await call(request(`${API}/${action}`, 'POST', mutation, JSON.stringify({ root: 'dsh', name: 'manual-sample' })));
        assert.equal(result.status, 200, result.text);
        skill = (await ctx.skills.list()).find(item => item.name === 'manual-sample');
        assert.equal(skill.provider, 'dsh-skills-manager-external');
        assert.deepEqual(skill.invocation, expected);
        assert.deepEqual((await ctx.skills.get('manual-sample')).invocation, expected);
      }
      assert.equal(await fs.readFile(filename, 'utf8'), content);
      await fiber.dispose();
      assert.equal((await call(request(`${API}/state`, 'GET', auth))).status, 404);
      skill = (await ctx.skills.list()).find(item => item.name === 'manual-sample');
      assert.equal(skill.provider, 'filesystem');
      assert.deepEqual(skill.invocation, { modelInvocable: false, userInvocable: true });
      const reloaded = ctx.plugin(plugin);
      await reloaded.await();
      assert.equal((await call(request(`${API}/state`, 'GET', auth))).status, 200);
      await reloaded.dispose();
    });
    await t.test('preset policies apply to cold catalog reads without outranking custom filesystem sources', async () => {
      const firstKey = {}, secondKey = {};
      const first = createScope(ctx, firstKey), second = createScope(ctx, secondKey);
      const custom = join(sandbox, 'custom-skills');
      await fs.mkdir(join(custom, 'manual-sample'), { recursive: true });
      await fs.writeFile(join(custom, 'manual-sample/SKILL.md'), '---\nname: manual-sample\ndescription: Higher-ranked custom copy\n---\nSynthetic custom body.\n');
      await first.ctx.plugin(filesystem, { cortexHome: process.env.CORTEX_HOME, agentsHome: process.env.CORTEX_AGENTS_HOME, customSkillDirs: [custom], watch: false }).await();
      await second.ctx.plugin(filesystem, { cortexHome: process.env.CORTEX_HOME, agentsHome: process.env.CORTEX_AGENTS_HOME, watch: false }).await();
      let held = 0;
      ctx.provide('agentPresets', {
        async list() { return [{ id: 'custom' }, { id: 'native' }]; },
        async acquireScope(id) { held++; return { key: id === 'custom' ? firstKey : secondKey, async [Symbol.asyncDispose]() { held--; } }; },
        composedPreset() { return 'native'; },
      });
      const scopedPlugin = ctx.plugin(plugin);
      try {
        await scopedPlugin.await();
        // Roster discovery deliberately runs after apply so native Loader can settle.
        for (let attempt = 0; held < 2 && attempt < 20; attempt++) await new Promise(resolve => setImmediate(resolve));
        assert.equal(held, 2);
        const result = await call(request(`${API}/disable`, 'POST', mutation, JSON.stringify({ root: 'dsh', name: 'manual-sample' })));
        assert.equal(result.status, 200, result.text);
        const customCopy = (await ctx.skills.list({ scope: firstKey })).find(item => item.name === 'manual-sample');
        assert.equal(customCopy.provider, 'filesystem');
        assert.equal(customCopy.description, 'Higher-ranked custom copy');
        assert.deepEqual(customCopy.invocation, { modelInvocable: true, userInvocable: true });
        const nativeCopy = (await ctx.skills.list({ scope: secondKey })).find(item => item.name === 'manual-sample');
        assert.equal(nativeCopy.provider, 'dsh-skills-manager-external');
        assert.deepEqual(nativeCopy.invocation, { modelInvocable: false, userInvocable: false });
        await scopedPlugin.dispose();
        assert.equal(held, 0, 'unload releases all preset leases');
        assert.deepEqual((await ctx.skills.get('manual-sample', { scope: secondKey })).invocation, { modelInvocable: false, userInvocable: true });
      } finally { await scopedPlugin.dispose(); await first.dispose(); await second.dispose(); }
    });
    assert.deepEqual(fetches, []);
  } finally {
    await ctx.fiber.dispose();
    globalThis.fetch = priorFetch;
    for (const key of Object.keys(process.env)) if (!(key in oldEnv)) delete process.env[key];
    Object.assign(process.env, oldEnv);
    assert.equal(dirname(sandbox), await fs.realpath(tmpdir()));
    await fs.rm(sandbox, { recursive: true, force: true });
  }
});

test('preset roster discovery cannot deadlock Host activation or disposal', async () => {
  const ctx = new Context();
  let releaseRoster;
  const roster = new Promise(resolve => { releaseRoster = resolve; });
  let acquired = 0, registered = 0;
  ctx.provide('agentPresets', {
    list: () => roster,
    async acquireScope() { acquired++; return { key: {}, async [Symbol.asyncDispose]() {} }; },
  });
  ctx.provide('skills', { registerProvider() { registered++; return () => {}; } });
  try {
    const dispose = await Promise.race([
      plugin.registerAgentSkillProviders(ctx, new Set()),
      new Promise((_, reject) => { const timer = setTimeout(() => reject(new Error('activation blocked on preset roster')), 1000); timer.unref(); }),
    ]);
    await Promise.race([
      dispose(),
      new Promise((_, reject) => { const timer = setTimeout(() => reject(new Error('disposal blocked on preset roster')), 1000); timer.unref(); }),
    ]);
    releaseRoster([{ id: 'deferred-sample' }]);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(acquired, 0);
    assert.equal(registered, 0);
  } finally { releaseRoster([]); await ctx.fiber.dispose(); }
});
