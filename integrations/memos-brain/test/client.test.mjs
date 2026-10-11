import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { join, dirname, resolve } from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
import vm from 'node:vm';

const stage = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const checkout = process.env.CORTEX_CHECKOUT ?? resolve(stage, '../..');
const rootRequire = createRequire(join(checkout, 'package.json'));
const webRequire = createRequire(join(checkout, 'apps/web/package.json'));
const loadHost = relative => import(pathToFileURL(join(checkout, relative)).href);
const cordis = await loadHost('vendor/cordis/lib/index.js');
const store = await loadHost('packages/client/store/lib/index.js');
const slots = await loadHost('packages/client/ui-slots/lib/index.js');
const { JSDOM } = rootRequire('jsdom');
const React = webRequire('react');
const { act } = React;

test('native Brain registers a matching sidebar/main pair, loads only its local frame, and releases every seat', async () => {
  const dom = new JSDOM('<!doctype html><html lang="en"><head></head><body><main id="app"></main></body></html>', {
    url: 'http://127.0.0.1:3094/', pretendToBeVisual: true,
  });
  const saved = new Map();
  const install = (name, value) => {
    saved.set(name, Object.getOwnPropertyDescriptor(globalThis, name));
    Object.defineProperty(globalThis, name, { configurable: true, writable: true, value });
  };
  for (const name of ['window', 'document', 'navigator', 'HTMLElement', 'Element', 'SVGElement', 'Node', 'MutationObserver', 'Event', 'MouseEvent', 'KeyboardEvent', 'ShadowRoot']) {
    install(name, name === 'window' ? dom.window : dom.window[name]);
  }
  install('getComputedStyle', dom.window.getComputedStyle.bind(dom.window));
  install('IS_REACT_ACT_ENVIRONMENT', true);
  const network = [];
  const forbid = kind => (...args) => { network.push([kind, ...args]); throw new Error(`Unexpected ${kind}`); };
  install('fetch', forbid('fetch')); dom.window.fetch = globalThis.fetch;
  for (const kind of ['XMLHttpRequest', 'WebSocket', 'EventSource']) {
    const deny = function (...args) { forbid(kind)(...args); };
    install(kind, deny); dom.window[kind] = deny;
  }
  dom.window.navigator.sendBeacon = forbid('sendBeacon');
  const factories = new Map();
  const modules = new Map([
    ['react', React], ['react/jsx-runtime', webRequire('react/jsx-runtime')],
    ['react-dom', webRequire('react-dom')], ['react-dom/client', webRequire('react-dom/client')],
    ['@cortex/cordis', cordis], ['@cortex/client-store', store], ['@cortex/client-ui-slots', slots],
  ]);
  dom.window.__ModuleLoader__ = { load: ({ id, factory }) => factories.set(id, factory) };
  const loadClient = (filename, id) => {
    vm.runInThisContext(readFileSync(filename, 'utf8'), { filename });
    assert.ok(factories.has(id), `Missing native module loader factory ${id}`);
    const result = factories.get(id)(name => {
      assert.ok(modules.has(name), `Unexpected runtime dependency: ${name}`);
      return modules.get(name);
    });
    modules.set(id, result); return result;
  };
  const renderer = loadClient(join(checkout, 'packages/client/ui-renderer/lib/client.js'), '@cortex/client-ui-renderer');
  const plugin = loadClient(join(stage, 'lib/client.js'), '@cortex/memos-brain');
  assert.equal(plugin.name, '@cortex/memos-brain');
  assert.deepEqual(plugin.inject, ['slots'], 'Brain must not acquire Session, model, remote, or memory-writing services');
  const ctx = new cordis.Context();
  let unmount;
  let releaseShell;
  try {
    await ctx.plugin(renderer).await();
    // Cortex's renderer always establishes the optional current-Session scope;
    // this global Brain fixture deliberately has no selected Session or services.
    const noSession = { key: undefined, hooks: {}, keyedHooks: {}, props: {} };
    const absent = { getSnapshot: () => noSession, subscribe: () => () => {} };
    ctx.slots.installScope('session', { current: absent, bindingSource: () => absent });
    let fiber = ctx.plugin(plugin);
    await fiber.await();
    assert.deepEqual(network, [], 'activation does not call the network');
    assert.equal(ctx.slots.entriesOfSlot('main').length, 0, 'registrations wait for the native shell declarations');
    assert.equal(ctx.slots.entriesOfSlot('sidebar.panellist').length, 0);

    let selected = 'conversation';
    const listeners = new Set();
    const subscribe = fn => { listeners.add(fn); return () => listeners.delete(fn); };
    const select = id => { selected = id; for (const fn of listeners) fn(); };
    function Shell(props) {
      const panel = React.useSyncExternalStore(subscribe, () => selected, () => selected);
      // The actual slot registry supplies panel metadata and renders the native icon/main seats.
      const nav = ctx.slots.entriesOfSlot('sidebar.panellist').map(entry => React.createElement('button', {
        key: entry.options.id, type: 'button', onClick: () => select(entry.options.id),
        'aria-label': slots.resolveSlotLabel(entry.options.label),
      }, props.renderSlot('sidebar.panellist', { size: 24, active: panel === entry.options.id }, { only: entry.options.id })));
      return React.createElement('section', null, React.createElement('nav', null, nav),
        props.renderSlot('main', {}, { entryKey: panel }));
    }
    const registerShell = () => ctx.slots.register({ name: 'root', children: {
      'sidebar.panellist': { kind: 'list', scope: 'root' }, 'main': { kind: 'keyed', scope: 'root' },
    } }, Shell);
    releaseShell = registerShell();
    const assertPair = () => {
      const nav = ctx.slots.entriesOfSlot('sidebar.panellist');
      const main = ctx.slots.entriesOfSlot('main');
      assert.equal(nav.length, 1); assert.equal(main.length, 1);
      assert.equal(nav[0].options.id, main[0].options.key);
      assert.equal(nav[0].options.id, 'memos-brain');
      assert.equal(slots.resolveSlotLabel(nav[0].options.label), 'Brain');
    };
    assertPair();
    await act(async () => { unmount = ctx.uiRenderer.mount(document.getElementById('app')); });
    assert.equal(document.querySelectorAll('iframe').length, 0, 'inactive Brain does not load a viewer');
    const brainButton = document.querySelector('button[aria-label="Brain"]');
    assert.ok(brainButton); assert.ok(brainButton.querySelector('svg[aria-hidden="true"]'));
    await act(async () => { brainButton.dispatchEvent(new MouseEvent('click', { bubbles: true })); });
    const assertFrame = () => {
      assert.equal(document.querySelectorAll('iframe').length, 1);
      const frame = document.querySelector('iframe');
      assert.equal(frame.getAttribute('src'), '/api/memos-brain/view/brain.html');
      assert.equal(new URL(frame.src).origin, dom.window.location.origin);
      assert.equal(new URL(frame.src).search, '', 'real memory is the default; no implicit sample mode');
      assert.equal(frame.title, 'MemOS Brain');
      assert.equal(frame.getAttribute('sandbox'), 'allow-scripts allow-same-origin');
      assert.equal(document.querySelectorAll('img[src^="http"],script[src^="http"],link[href^="http"]').length, 0);
      return frame;
    };
    const firstFrame = assertFrame();
    await act(async () => { await fiber.dispose(); });
    assert.equal(firstFrame.isConnected, false, 'unloading destroys the iframe and its document lifecycle');
    assert.equal(document.querySelectorAll('iframe').length, 0);
    assert.equal(ctx.slots.entriesOfSlot('main').length, 0);
    assert.equal(ctx.slots.entriesOfSlot('sidebar.panellist').length, 0);

    await act(async () => { fiber = ctx.plugin(plugin); await fiber.await(); });
    assertPair(); assertFrame();
    await act(async () => { unmount(); unmount = undefined; });
    releaseShell(); releaseShell = undefined;
    assert.equal(document.querySelectorAll('iframe').length, 0, 'shell declaration teardown removes the embedded renderer');
    assert.equal(ctx.slots.entriesOfSlot('main').length, 0);
    assert.equal(ctx.slots.entriesOfSlot('sidebar.panellist').length, 0);
    releaseShell = registerShell();
    assertPair();
    await act(async () => { unmount = ctx.uiRenderer.mount(document.getElementById('app')); });
    assertFrame();
    await act(async () => { unmount(); unmount = undefined; });
    assert.equal(document.querySelectorAll('iframe').length, 0);
    assert.equal(listeners.size, 0, 'unmount releases the selected-panel subscription');
    assert.deepEqual(network, []);
  } finally {
    if (unmount) await act(async () => { unmount(); });
    if (releaseShell) releaseShell();
    await ctx.fiber.dispose();
    dom.window.close();
    for (const [name, descriptor] of saved) {
      if (descriptor) Object.defineProperty(globalThis, name, descriptor);
      else delete globalThis[name];
    }
  }
});
