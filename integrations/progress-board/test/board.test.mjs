import { test } from 'node:test';
import assert from 'node:assert/strict';
import { IDLE, buildBoard, collectAgents, estimate, foldEvent, readBoard, squeeze } from '../lib/board.js';
import { GUIDANCE, PROJECTION_KEY, apply } from '../lib/host.js';

const at = (type, time, data = {}) => ({ type, time, data });
const fold = events => events.reduce(foldEvent, IDLE);
const todos = (...statuses) => statuses.map((status, i) => ({ content: `Task ${i + 1}`, status }));

test('a turn folds into a working board that climbs with tool calls', () => {
  const start = fold([at('turn/start', 1_000, { turn: 1 })]);
  const later = fold([
    at('turn/start', 1_000, { turn: 1 }),
    at('tool/call', 2_000, { name: 'read', arguments: JSON.stringify({ file_path: 'C:\\src\\app.ts' }) }),
    at('tool/call', 3_000, { name: 'bash', arguments: JSON.stringify({ command: 'pnpm test', description: 'Run the tests' }) }),
  ]);

  assert.equal(start.phase, 'working');
  assert.equal(later.tools, 2);
  assert.equal(later.activity, 'Run the tests');
  assert.ok(estimate(later, [], 3_000) > estimate(start, [], 1_000));
  assert.ok(estimate(later, [], 3_600_000) < 1);
});

test('unrelated events keep the same state object', () => {
  const state = fold([at('turn/start', 1_000)]);
  assert.equal(foldEvent(state, at('assistant/message', 2_000)), state);
});

test('a todo list splits the bar and names the current step', () => {
  const state = fold([
    at('turn/start', 1_000),
    at('todo/write', 2_000, { todos: todos('completed', 'in_progress', 'pending') }),
  ]);
  const board = buildBoard(state, [], 2_000);

  assert.equal(board.caption, 'Step 2/3 · Task 2');
  assert.deepEqual(board.rows.map(r => r.mark), ['done', 'active', 'pending']);
  assert.ok(board.p > 0.33 && board.p < 0.67);
});

test('a finished turn with every task done fills the bar', () => {
  const state = fold([
    at('turn/start', 1_000),
    at('tool/call', 2_000, { name: 'edit', arguments: '{}' }),
    at('tool/call', 3_000, { name: 'todo_write', arguments: '{}' }),
    at('todo/write', 3_000, { todos: todos('completed', 'completed') }),
    at('turn/end', 9_000, { turn: 1, reason: { kind: 'completed' } }),
  ]);
  const board = buildBoard(state, [], 60_000);

  assert.equal(board.p, 1);
  assert.equal(board.tone, 'success');
  assert.equal(board.caption, 'Done · 2 tool calls');
  assert.equal(board.elapsed, '0:08');
});

test('a turn that ends with tasks open shows how far it got, not done', () => {
  const state = fold([
    at('turn/start', 1_000),
    at('todo/write', 2_000, { todos: todos('completed', 'in_progress', 'pending') }),
    at('turn/end', 5_000, { reason: { kind: 'completed' } }),
  ]);
  const board = buildBoard(state, [], 10_000);

  assert.equal(board.p, 1 / 3);
  assert.equal(board.tone, 'warning');
  assert.equal(board.caption, 'Turn ended · 1/3 tasks done');
  assert.deepEqual(board.rows.map(r => r.mark), ['done', 'paused', 'pending']);
});

test('an interrupted turn reads as stopped and an unfinished list carries over', () => {
  const stopped = fold([
    at('turn/start', 1_000),
    at('todo/write', 2_000, { todos: todos('in_progress', 'pending') }),
    at('turn/end', 3_000, { reason: { kind: 'aborted', reason: { kind: 'user' } } }),
  ]);
  const next = foldEvent(stopped, at('turn/start', 4_000));

  assert.equal(stopped.phase, 'stopped');
  assert.equal(next.todos.length, 2);
  assert.equal(next.taskBase, 0);
});

test('subagents join from their own sessions, running ones first, nested one level', () => {
  const child = fold([
    at('turn/start', 2_000),
    at('tool/call', 3_000, { name: 'grep', arguments: '{}' }),
  ]);
  const done = fold([at('turn/start', 2_000), at('turn/end', 4_000, { reason: { kind: 'completed' } })]);
  const list = {
    byId: {},
    projectionsBySession: {
      c1: { values: { progressBoard: child, subagentCatalog: [{ id: 'g1', createdAt: 3_000, label: 'Check tokens' }] } },
      c2: { values: { progressBoard: done } },
      g1: { values: {} },
    },
  };
  const statuses = new Map([['c1', { running: true }], ['c2', { running: false }], ['g1', { running: true }]]);
  const catalog = [{ id: 'c2', createdAt: 2_000, label: 'Review' }, { id: 'c1', createdAt: 2_000, label: 'Explore auth' }];
  const agents = collectAgents(catalog, list, statuses, 1_000);
  const parent = fold([at('turn/start', 1_000)]);
  const board = buildBoard(parent, agents, 5_000);

  assert.deepEqual(agents.map(a => [a.label, a.depth, a.isRunning]), [['Review', 0, false], ['Explore auth', 0, true], ['Check tokens', 1, true]]);
  assert.deepEqual(board.rows.map(r => r.text), ['Explore auth · Searching the code', 'Check tokens · Starting', 'Review · 0 tool calls']);
});

test('agents from before this turn are left off unless still running', () => {
  const list = { byId: {}, projectionsBySession: {} };
  const statuses = new Map([['old', { running: false }]]);

  assert.equal(collectAgents([{ id: 'old', createdAt: 500, label: 'Old' }], list, statuses, 1_000).length, 0);
});

test('a tight board folds finished rows and counts the rest', () => {
  const row = (i, isFinished) => ({ key: `r${i}`, depth: 0, mark: isFinished ? 'done' : 'pending', text: `Task ${i}`, tail: '', tone: 'plain', isFinished, isAgent: false });
  const rows = [row(1, true), row(2, true), row(3, true), row(4, false), row(5, false)];

  assert.deepEqual(squeeze(rows, 3).map(r => r.text), ['3 done', 'Task 4', 'Task 5']);
  assert.deepEqual(squeeze(rows, 2).map(r => r.text), ['3 done', '2 more']);
});

test('a malformed stored board starts over idle', () => {
  assert.equal(readBoard({ v: 2 }), IDLE);
  assert.equal(readBoard(null), IDLE);
});

test('the host registers the projection and the guidance', () => {
  const calls = { projections: [], sections: [] };
  apply({
    sessionProjections: { register: definition => { calls.projections.push(definition); return () => {}; } },
    systemPrompt: { section: definition => { calls.sections.push(definition); return () => {}; } },
  });
  const [projection] = calls.projections;

  assert.equal(projection.key, PROJECTION_KEY);
  assert.equal(projection.apply(projection.init(), at('turn/start', 1_000)).phase, 'working');
  assert.equal(projection.stateSchema.parse(projection.init()).v, 1);
  assert.equal(calls.sections[0].text, GUIDANCE);
});
