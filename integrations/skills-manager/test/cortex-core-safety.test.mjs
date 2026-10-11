import assert from 'node:assert/strict';
import test from 'node:test';
import { promises as fs } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve, sep } from 'node:path';
import { zipSync, strToU8 } from 'fflate';

// Published-artifact acceptance. All discovery roots belong to this fixture.
test('local skills keep invocation restrictions, source consent, and filesystem containment', async t => {
  const sandbox = await fs.mkdtemp(join(await fs.realpath(tmpdir()), 'cortex-skills-safety-'));
  const oldEnv = { ...process.env };
  process.env.HOME = process.env.USERPROFILE = join(sandbox, 'user');
  process.env.CORTEX_HOME = join(sandbox, 'cortex');
  process.env.CORTEX_AGENTS_HOME = join(sandbox, 'agents');
  for (const agent of ['CODEX', 'CLAUDE', 'GEMINI', 'OPENCODE', 'CURSOR', 'COPILOT', 'WINDSURF', 'WINDSURF_USER', 'TRAE', 'TRAE_CN', 'OPENCLAW', 'CLAWDBOT', 'ROO', 'CODEBUDDY', 'WORKBUDDY', 'QODER', 'QODER_CN', 'LINGMA']) {
    process.env[`DSH_${agent}_HOME`] = join(sandbox, agent.toLowerCase());
  }
  const core = await import('../lib/core.js');
  const { createRepositoryManager } = await import('../lib/repositories.js');
  const roots = core.userRoots();
  const root = key => roots.find(item => item.key === key);
  for (const item of roots) assert.ok(resolve(item.path).startsWith(resolve(sandbox) + sep), `fixture root escaped: ${item.key}`);
  const doc = (name, flags = '') => `---\nname: ${name}\ndescription: Synthetic acceptance skill\n${flags}---\nOnly synthetic fixture instructions.\n`;
  const makeSkill = async (base, name, flags) => {
    const target = join(base, name, 'SKILL.md');
    await fs.mkdir(dirname(target), { recursive: true });
    await fs.writeFile(target, doc(name, flags));
    return target;
  };
  const exists = path => fs.lstat(path).then(() => true, () => false);
  const priorFetch = globalThis.fetch;
  const calls = [];
  globalThis.fetch = async (...args) => { calls.push(args); throw new Error('Unexpected network access'); };
  try {
    await t.test('foreign skills are not read into the catalog until their source is enabled', async () => {
      const foreign = await makeSkill(root('codex').path, 'foreign-only');
      const before = await fs.readFile(foreign, 'utf8');
      const initial = await core.state();
      assert.equal(initial.roots.find(item => item.key === 'codex').enabled, false);
      assert.equal(initial.roots.find(item => item.key === 'codex').skills.length, 0);
      assert.equal((await core.listProviderCandidates()).some(item => item.name === 'foreign-only'), false);
      await core.setSourceEnabled('codex', true);
      assert.equal((await core.listProviderCandidates()).find(item => item.name === 'foreign-only').invocation.modelInvocable, true);
      assert.equal((await core.deleteSkill(root('codex'), 'foreign-only')).ok, false);
      assert.equal((await core.createSkill({ name: 'denied', description: 'test', body: 'test' }, undefined, { root: root('codex') })).ok, false);
      assert.equal(await fs.readFile(foreign, 'utf8'), before);
      await core.setSourceEnabled('codex', false);
    });
    await t.test('native defaults stay with Cortex and manager toggles never broaden invocation permissions', async () => {
      for (const [name, flags] of [['manual-only', 'disable-model-invocation: true\n'], ['model-only', 'user-invocable: false\n']]) {
        const path = await makeSkill(root('dsh').path, name, flags);
        const original = await fs.readFile(path, 'utf8');
        assert.equal((await core.listProviderCandidates()).some(item => item.name === name), false);
        await core.setSkillEnabled(root('dsh'), name, false);
        let candidate = (await core.listProviderCandidates()).find(item => item.name === name);
        assert.deepEqual(candidate.invocation, { modelInvocable: false, userInvocable: false });
        await core.setSkillEnabled(root('dsh'), name, true);
        candidate = (await core.listProviderCandidates()).find(item => item.name === name);
        assert.deepEqual(candidate.invocation, { modelInvocable: name !== 'manual-only', userInvocable: name !== 'model-only' });
        assert.equal(await fs.readFile(path, 'utf8'), original);
      }
    });
    await t.test('ZIP uploads reject traversal, absolute paths, and Windows path aliases', async () => {
      for (const badPath of ['../escaped.txt', '/escaped.txt', 'C:/escaped.txt', 'safe/../../escaped.txt', 'safe\\..\\..\\escaped.txt', 'safe/CON.txt', 'safe/name.']) {
        const archive = zipSync({ 'SKILL.md': strToU8(doc('archive')), [badPath]: strToU8('escape') });
        const result = await core.importUploadedSkill({ name: 'archive.zip', zip: Buffer.from(archive).toString('base64') });
        assert.equal(result.ok, false, `must reject ${badPath}`);
      }
      assert.equal(await exists(join(sandbox, 'escaped.txt')), false);
      assert.equal(await exists(join(root('dsh').path, 'archive')), false);
    });
    await t.test('directory imports refuse junctions and do not read their target', async () => {
      const outside = join(sandbox, 'outside');
      await makeSkill(outside, 'external');
      const source = join(sandbox, 'import-source');
      await fs.mkdir(source);
      await fs.writeFile(join(source, 'SKILL.md'), doc('linked-import'));
      await fs.symlink(outside, join(source, 'resources'), process.platform === 'win32' ? 'junction' : 'dir');
      const result = await core.importSkill(source);
      assert.equal(result.ok, false);
      assert.equal(result.code, 'error.import.failed');
      assert.equal(await exists(join(root('dsh').path, 'linked-import')), false);
      assert.ok(await exists(join(outside, 'external', 'SKILL.md')));
    });
    await t.test('trash restores the original root and refuses forged metadata destinations', async () => {
      const skillFile = await makeSkill(root('dsh').path, 'trash-demo');
      const content = await fs.readFile(skillFile, 'utf8');
      const removed = await core.deleteSkill(root('dsh'), 'trash-demo');
      assert.equal(await exists(skillFile), false);
      const item = (await core.listTrash()).find(item => item.name === 'trash-demo');
      assert.ok(item);
      await core.restoreTrash(item.id);
      assert.equal(await fs.readFile(skillFile, 'utf8'), content);
      await core.deleteSkill(root('dsh'), 'trash-demo');
      const second = (await core.listTrash()).find(item => item.name === 'trash-demo');
      const metaPath = join(core.trashRootPath(), second.id, 'metadata.json');
      const metadata = JSON.parse(await fs.readFile(metaPath, 'utf8'));
      metadata.root.key = 'codex';
      metadata.root.path = root('codex').path;
      await fs.writeFile(metaPath, JSON.stringify(metadata));
      assert.equal((await core.restoreTrash(second.id)).ok, false);
      assert.equal(await exists(join(root('codex').path, 'trash-demo')), false);
      assert.equal((await core.permanentlyDeleteTrash('../outside')).ok, false);
      assert.equal(removed.ok, undefined);
    });
    await t.test('explicit repository updates preserve backups and reject stale previews and foreign destinations', async () => {
      let version = 'v1';
      const remoteCalls = [];
      const manager = createRepositoryManager({ fetchImpl: async (url, options) => {
        remoteCalls.push({ url, options });
        assert.ok(String(url).startsWith('https://codeload.github.com/example/synthetic/zip/'));
        assert.equal(options.redirect, 'error');
        return new Response(zipSync({ 'synthetic/skill/SKILL.md': strToU8(doc('repo-demo')), 'synthetic/skill/value.txt': strToU8(version) }));
      } });
      await manager.list();
      assert.equal(remoteCalls.length, 0);
      for (const url of ['http://127.0.0.1/private', 'https://evil.example/repo', 'https://github.com@evil.example/repo', 'example/synthetic?token=x']) {
        await assert.rejects(manager.add({ url }));
      }
      assert.equal(remoteCalls.length, 0, 'invalid repository addresses are rejected without a request');
      const repo = await manager.add({ url: 'example/synthetic' });
      await manager.refresh({ id: repo.id });
      await assert.rejects(manager.install({ id: repo.id, path: 'skill', root: 'codex' }));
      await manager.install({ id: repo.id, path: 'skill' });
      const valueFile = join(root('dsh').path, 'repo-demo/value.txt');
      assert.equal(await fs.readFile(valueFile, 'utf8'), 'v1');
      version = 'v2';
      await manager.refresh({ id: repo.id });
      const stale = await manager.preview({ id: repo.id, path: 'skill' });
      await fs.writeFile(valueFile, 'user edits');
      await assert.rejects(manager.update({ id: repo.id, path: 'skill', token: stale.token }));
      assert.equal(await fs.readFile(valueFile, 'utf8'), 'user edits');
      const preview = await manager.preview({ id: repo.id, path: 'skill' });
      await assert.rejects(manager.update({ id: repo.id, path: 'skill', token: preview.token }), error => error.code === 'error.repo.modified');
      await manager.update({ id: repo.id, path: 'skill', token: preview.token, overwrite: true });
      assert.equal(await fs.readFile(valueFile, 'utf8'), 'v2');
      const rollback = await manager.preview({ id: repo.id, path: 'skill', rollback: true });
      await manager.rollback({ id: repo.id, path: 'skill', token: rollback.token });
      assert.equal(await fs.readFile(valueFile, 'utf8'), 'user edits');
    });
    assert.deepEqual(calls, []);
  } finally {
    globalThis.fetch = priorFetch;
    for (const key of Object.keys(process.env)) if (!(key in oldEnv)) delete process.env[key];
    Object.assign(process.env, oldEnv);
    assert.equal(dirname(sandbox), await fs.realpath(tmpdir()));
    await fs.rm(sandbox, { recursive: true, force: true });
  }
});
