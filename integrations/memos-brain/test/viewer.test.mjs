import assert from 'node:assert/strict';
import test from 'node:test';
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { build } from 'esbuild';

const stage = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const checkout = process.env.CORTEX_CHECKOUT ?? resolve(stage, '../..');
const rootRequire = createRequire(join(checkout, 'package.json'));
const { JSDOM } = rootRequire('jsdom');
const memory = resolve(stage, '../memos');
const bundle = await build({
  stdin: {
    contents: `import { h, render } from 'preact';
import { BrainView } from './viewer/src/views/BrainView.tsx';
window.mountBrain = embedded => render(h(BrainView, { embedded }), document.getElementById('app'));
window.unmountBrain = () => render(null, document.getElementById('app'));`,
    resolveDir: memory,
    sourcefile: 'brain-viewer-acceptance.tsx',
    loader: 'tsx',
  },
  bundle: true, write: false, format: 'iife', platform: 'browser', target: 'es2022',
  jsx: 'automatic', jsxImportSource: 'preact', loader: { '.css': 'empty' }, logLevel: 'silent',
});

async function waitFor(assertion) {
  const deadline = Date.now() + 3000;
  while (true) {
    try { assertion(); return; } catch (error) {
      if (Date.now() >= deadline) throw error;
      await new Promise(resolveWait => setTimeout(resolveWait, 20));
    }
  }
}

for (const embedded of [false, true]) {
  test(`${embedded ? 'native' : 'standalone'} Brain reads the local store for legacy demo URLs, refreshes stored records and reports read errors`, async () => {
    const mount = embedded ? '/api/memos-brain' : '';
    const pathname = embedded ? '/api/memos-brain/view/brain.html' : '/';
    const dom = new JSDOM('<!doctype html><html><body><main id="app"></main></body></html>', {
      url: `http://127.0.0.1:3094${pathname}?demo=1#/brain?demo=1`,
      runScripts: 'outside-only', pretendToBeVisual: true,
    });
    const calls = [];
    let populated = false;
    let failure = false;
    const namespace = { ownerAgentKind: 'cortex', ownerProfileId: 'ui-test', ownerWorkspaceId: 'workspace' };
    const stored = {
      id: 'memory:stored-id', kind: 'memory', rawId: 'stored-id', title: 'Stored release decision',
      content: 'Recorded by the memory store for this test.', contentTruncated: false,
      namespace, createdAt: 1, updatedAt: 1,
      source: { view: 'memories', id: 'stored-id' }, evidence: { total: 0, shown: 0, nextCursor: null },
    };
    dom.window.HTMLCanvasElement.prototype.getContext = () => null;
    dom.window.fetch = async (url, options) => {
      calls.push({ url, method: options.method });
      assert.equal(options.method, 'GET');
      assert.ok(url.startsWith(`${mount}/api/v1/brain/`), `Unexpected request: ${url}`);
      if (failure) return new Response(JSON.stringify({ error: { message: 'Local store unavailable' } }), { status: 500 });
      const parsed = new URL(url, dom.window.location.origin);
      const totals = { memory: populated ? 1 : 0, episode: 0, policy: 0, world: 0, skill: 0, trace: 0 };
      const body = parsed.pathname.endsWith('/summary')
        ? { generatedAt: 1, consistency: 'live', totals, namespaces: populated ? [namespace] : [], namespacesNextCursor: null, storage: { status: populated ? 'ready' : 'missing' } }
        : { generatedAt: 1, consistency: 'live', kind: parsed.searchParams.get('kind'), total: populated && parsed.searchParams.get('kind') === 'memory' ? 1 : 0,
          nodes: populated && parsed.searchParams.get('kind') === 'memory' ? [stored] : [], edges: [], nextCursor: null };
      return new Response(JSON.stringify(body));
    };
    const click = label => {
      const button = [...dom.window.document.querySelectorAll('button')].find(item => item.textContent === label);
      assert.ok(button, `Missing ${label} control`);
      button.click();
    };
    const assertNoExamples = () => {
      assert.doesNotMatch(dom.window.document.body.textContent, /Explore sample|Synthetic example|Local-first memory|Return to local data/);
      assert.equal(dom.window.document.querySelectorAll('.brain__sample,.brain__badge--sample').length, 0);
    };
    try {
      dom.window.eval(bundle.outputFiles[0].text);
      dom.window.mountBrain(embedded);
      await waitFor(() => {
        assert.match(dom.window.document.body.textContent, /No saved learning is available/);
        assert.match(dom.window.document.body.textContent, /0 stored records/);
      });
      assert.equal(calls.length, 6, 'legacy demo URL still loads the summary and every stored record category');
      assertNoExamples();
      click('List');
      populated = true;
      click('Refresh');
      await waitFor(() => {
        assert.match(dom.window.document.body.textContent, /Stored release decision/);
        assert.match(dom.window.document.body.textContent, /1 stored records/);
      });
      assert.equal(dom.window.document.querySelectorAll('.brain__row').length, 1);
      dom.window.document.querySelector('.brain__row').click();
      await waitFor(() => assert.match(dom.window.document.querySelector('.brain__drawer').textContent, /stored-id/));
      const record = dom.window.document.querySelector('.brain__record-link');
      assert.equal(record?.getAttribute('href') ?? null, embedded ? null : '#/memories?id=stored-id');
      assertNoExamples();
      failure = true;
      click('Refresh');
      await waitFor(() => assert.match(dom.window.document.querySelector('[role="alert"]').textContent, /Local store unavailable/));
      assert.equal(dom.window.document.querySelectorAll('.brain__row').length, 0);
      assertNoExamples();
      assert.ok(calls.every(call => call.method === 'GET'), 'viewing and refreshing never write memories');
    } finally {
      dom.window.unmountBrain?.();
      dom.window.close();
    }
  });
}
