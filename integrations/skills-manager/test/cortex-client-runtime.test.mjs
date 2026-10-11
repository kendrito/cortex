import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { join, dirname } from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
import vm from 'node:vm';

const checkout = process.env.CORTEX_CHECKOUT;
assert.ok(checkout, 'Set CORTEX_CHECKOUT to the built Cortex checkout');
const rootRequire = createRequire(join(checkout, 'package.json'));
const webRequire = createRequire(join(checkout, 'apps/web/package.json'));
const loadHost = relative => import(pathToFileURL(join(checkout, relative)).href);
const cordis = await loadHost('vendor/cordis/lib/index.js');
const store = await loadHost('packages/client/store/lib/index.js');
const slots = await loadHost('packages/client/ui-slots/lib/index.js');
const { JSDOM } = rootRequire('jsdom');
const React = webRequire('react');
const { act } = React;
const stage = join(dirname(fileURLToPath(import.meta.url)), '..');

test('built settings component uses current Cortex slots, local requests, and scoped cleanup', async () => {
  const dom = new JSDOM('<!doctype html><html lang="en"><head></head><body><main id="app"></main></body></html>', {
    url: 'http://127.0.0.1:3093/', pretendToBeVisual: true,
  });
  const saved = new Map();
  const install = (name, value) => {
    saved.set(name, Object.getOwnPropertyDescriptor(globalThis, name));
    Object.defineProperty(globalThis, name, { configurable: true, writable: true, value });
  };
  for (const name of ['window', 'document', 'navigator', 'HTMLElement', 'HTMLInputElement', 'Element', 'SVGElement', 'Node', 'MutationObserver', 'Event', 'MouseEvent', 'KeyboardEvent', 'ShadowRoot']) {
    install(name, name === 'window' ? dom.window : dom.window[name]);
  }
  install('getComputedStyle', dom.window.getComputedStyle.bind(dom.window));
  install('IS_REACT_ACT_ENVIRONMENT', true);
  dom.window.matchMedia = query => ({ matches: false, media: query, onchange: null,
    addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent() { return true; } });
  const external = [];
  const reject = name => (...args) => { external.push([name, ...args]); throw new Error(`Unexpected ${name}`); };
  for (const name of ['XMLHttpRequest', 'WebSocket', 'EventSource']) {
    const deny = function (...args) { reject(name)(...args); };
    install(name, deny); dom.window[name] = deny;
  }
  dom.window.navigator.sendBeacon = reject('sendBeacon');
  const requests = [];
  let holdRequests = false;
  const localFetch = async (url, options) => {
    assert.ok(String(url).startsWith('/api/dsh-skills-manager/'), `unexpected URL: ${url}`);
    assert.equal(options.credentials, 'same-origin');
    assert.equal(options.redirect, 'error');
    assert.ok(options.signal instanceof AbortSignal);
    requests.push({ url, options });
    if (holdRequests) return new Promise((resolve, reject) => {
      options.signal.addEventListener('abort', () => reject(new DOMException('closed', 'AbortError')), { once: true });
    });
    const data = String(url).endsWith('/repositories') ? { repositories: [], installRoots: [] } : {
      roots: [], projects: [], trash: [], warnings: [], summary: { total: 0, enabled: 0, disabled: 0, issues: 0 },
    };
    return Response.json({ ok: true, data });
  };
  install('fetch', localFetch); dom.window.fetch = localFetch;
  const factories = new Map();
  const modules = new Map([
    ['react', React], ['react/jsx-runtime', webRequire('react/jsx-runtime')],
    ['react-dom', webRequire('react-dom')], ['react-dom/client', webRequire('react-dom/client')],
    ['@cortex/cordis', cordis], ['@cortex/client-store', store], ['@cortex/client-ui-slots', slots],
    // The locale module's own language icon is not mounted by this fixture.
    ['@cortex/client-ui-primitives', new Proxy({}, { get: () => () => null })],
  ]);
  dom.window.__ModuleLoader__ = { load: ({ id, factory }) => factories.set(id, factory) };
  const loadClient = (file, id) => {
    vm.runInThisContext(readFileSync(file, 'utf8'), { filename: file });
    const result = factories.get(id)(name => {
      assert.ok(modules.has(name), `unresolved Cortex module: ${name}`);
      return modules.get(name);
    });
    modules.set(id, result); return result;
  };
  const renderer = loadClient(join(checkout, 'packages/client/ui-renderer/lib/client.js'), '@cortex/client-ui-renderer');
  const localeModule = loadClient(join(checkout, 'packages/client/locale/lib/client.js'), '@cortex/client-locale');
  const plugin = loadClient(join(stage, 'lib/client.js'), '@michengai/dsh-skills-manager');
  const ctx = new cordis.Context();
  let unmount;
  try {
    await ctx.plugin(renderer).await();
    const locale = new localeModule.LocaleRuntime(ctx);
    locale.setLocale('en'); ctx.provide('locale', locale); ctx.slots.installLocale(locale);
    let snapshot = { byId: { 'session-sample': { retainedBy: { mainView: 1 } } } };
    const listeners = new Set();
    ctx.slots.provideRoot({ hooks: { sessions: { getSnapshot: () => snapshot, subscribe(fn) { listeners.add(fn); return () => listeners.delete(fn); } } } });
    const noSession = { key: undefined, hooks: {}, keyedHooks: {}, props: {} };
    const absent = { getSnapshot: () => noSession, subscribe: () => () => {} };
    ctx.slots.installScope('session', { current: absent, bindingSource: () => absent });
    ctx.slots.register({ name: 'root', children: { 'settings.section': { kind: 'list', scope: 'root' } } },
      props => React.createElement('section', { 'data-settings-shell': true }, props.renderSlot('settings.section')));
    let fiber = ctx.plugin(plugin);
    await fiber.await();
    assert.equal(requests.length, 0, 'activation must not query data or check for updates');
    const entry = ctx.slots.entriesOfSlot('settings.section').find(item => item.options.id === 'skills-manager');
    assert.ok(entry);
    assert.equal(entry.options.label(), 'Skills');
    await act(async () => { unmount = ctx.uiRenderer.mount(document.getElementById('app')); });
    assert.deepEqual(requests.map(item => item.url), ['/api/dsh-skills-manager/state']);
    assert.equal(new Headers(requests[0].options.headers).get('x-dsh-skills-session'), 'session-sample');
    assert.ok(document.querySelector('.dssm-section'));
    assert.ok(document.body.textContent.includes('Skills'));
    const repoTab = [...document.querySelectorAll('label')].find(node => node.textContent.trim() === 'Repositories')?.querySelector('input');
    assert.ok(repoTab);
    await act(async () => { repoTab.dispatchEvent(new MouseEvent('click', { bubbles: true })); });
    assert.deepEqual(requests.map(item => item.url), ['/api/dsh-skills-manager/state', '/api/dsh-skills-manager/repositories']);
    assert.ok(document.body.textContent.includes('No repositories yet'));
    assert.equal(document.querySelectorAll('img[src^="http"],script[src^="http"],link[href^="http"]').length, 0);

    holdRequests = true;
    await act(async () => {
      snapshot = { byId: { 'session-next': { retainedBy: { mainView: 1 } } } };
      for (const listener of listeners) listener();
    });
    assert.ok(requests.slice(0, 2).every(item => item.options.signal.aborted), 'Session changes abort requests from the previous selection');
    assert.equal(new Headers(requests.at(-1).options.headers).get('x-dsh-skills-session'), 'session-next');
    const pending = requests.filter(item => !item.options.signal.aborted);
    assert.ok(pending.length > 0);
    await act(async () => { await fiber.dispose(); });
    assert.ok(pending.every(item => item.options.signal.aborted), 'unload aborts all active component requests');
    assert.equal(ctx.slots.entriesOfSlot('settings.section').length, 0);
    assert.equal(document.querySelectorAll('.dssm-section').length, 0);
    assert.equal([...document.querySelectorAll('style')].some(node => node.textContent.includes('.dssm-section')), false, 'React-owned manager CSS is removed');
    holdRequests = false;
    await act(async () => { fiber = ctx.plugin(plugin); await fiber.await(); });
    assert.equal(ctx.slots.entriesOfSlot('settings.section').length, 1, 'reenabling does not duplicate the settings section');
    assert.ok(document.querySelector('.dssm-section'));
    await act(async () => { unmount(); unmount = undefined; });
    assert.ok(requests.every(item => item.options.signal.aborted));
    assert.equal(listeners.size, 0, 'unmount releases the Cortex Session subscription');
    assert.deepEqual(external, []);
  } finally {
    if (unmount) await act(async () => { unmount(); });
    await ctx.fiber.dispose();
    dom.window.close();
    for (const [name, descriptor] of saved) {
      if (descriptor) Object.defineProperty(globalThis, name, descriptor);
      else delete globalThis[name];
    }
  }
});
