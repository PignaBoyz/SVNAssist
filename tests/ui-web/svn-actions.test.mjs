// Nuove azioni: SVN Cleanup, Update to revision, Lock/Unlock (badge, menu, comandi) e pulsante "Esegui Cleanup".
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { read, readJson, contract, validate, loadDemo, loadUi } from './helpers.mjs';

const state = () => readJson('src/SVNAssist.UI.Web/contract/fixtures/changes.json');

test('albero: file bloccato = lucchetto, K = solo bloccato, schema valido', () => {
  const U = loadUi(), s = state();
  const row = s.sections.find(x => x.rows.some(r => r.kind === 'file')).rows.find(r => r.kind === 'file');
  row.locked = true; row.can.unlock = true; row.can.lock = false;
  assert.deepEqual(validate(contract('changes-state.schema.json'), s), []);
  assert.match(U.treeHTML([s.sections.find(x => x.rows.includes(row))], null), /i-lock/);

  const k = { ...row, key: 'changes:solo-lock.txt', name: 'solo-lock.txt', status: 'K', statusText: 'Bloccato (lock)', locked: true };
  s.sections[1].rows.push(k);
  assert.deepEqual(validate(contract('changes-state.schema.json'), s), []);
  const html = U.treeHTML([s.sections[1]], null);
  assert.match(html, /st-K[^>]*>K</);
  assert.equal((html.match(/i-lock/g) || []).length, 0, 'con la K non serve anche il lucchetto');
});

test('status.action: pulsante Esegui Cleanup valido nello schema, altri comandi rifiutati', () => {
  const s = state();
  s.status = { msg: 'Errore update: svn: E155004: locked', err: true, action: { label: 'Esegui Cleanup', cmd: 'cleanup' } };
  assert.deepEqual(validate(contract('changes-state.schema.json'), s), []);
  s.status.action.cmd = 'commit';
  assert.notDeepEqual(validate(contract('changes-state.schema.json'), s), []);
});

test('comandi: cleanup, updateToRevision, rowOp lock/unlock sono nello schema', () => {
  const S = contract('commands.schema.json');
  for (const name of ['cleanup', 'updateToRevision']) assert.deepEqual(validate(S, { name, args: {} }), [], name);
  for (const op of ['lock', 'unlock']) assert.deepEqual(validate(S, { name: 'rowOp', args: { op, key: 'changes:a.cs' } }), [], op);
  assert.notDeepEqual(validate(S, { name: 'rowOp', args: { op: 'steal', key: 'changes:a.cs' } }), []);
});

test('changes.js: voci di menu SVN Update to revision, SVN Cleanup, SVN Lock, SVN Unlock e pulsante dell\'azione di stato', () => {
  const js = read('src/SVNAssist.UI.Web/js/changes.js');
  assert.match(js, /SVN Update to revision…/); assert.match(js, /cmd: 'updateToRevision'/);
  assert.match(js, /label: 'SVN Cleanup', cmd: 'cleanup'/);
  assert.match(js, /A\('lock', 'SVN Lock…'/); assert.match(js, /A\('unlock', 'SVN Unlock'/);
  assert.match(js, /s\.status\.action/);
});

test('mock: lock e unlock di un file modificato, cleanup', async () => {
  const { run, bridge } = await loadDemo();
  const B = bridge(), got = [], win = { frameElement: { isConnected: true } }, tick = () => new Promise(r => setImmediate(r));
  B.attach(win, m => got.push(JSON.parse(JSON.stringify(m))));
  const send = async (name, args) => { B.postMessage({ v: 1, id: send.n = (send.n || 0) + 1, type: 'cmd', name, args }, win); for (let i = 0; i < 12; i++) await tick(); };
  const last = () => got.filter(m => m.type === 'state').at(-1).state;
  const ok = () => assert.deepEqual(validate(contract('changes-state.schema.json'), last()), []);
  const find = () => last().sections.flatMap(s => s.rows).find(r => r.kind === 'file' && (r.can.lock || r.can.unlock));
  await run('autoDetect()', 't');
  B.postMessage({ v: 1, type: 'ready', view: 'changes', id: null }, win); await tick();

  const f = find(); assert.ok(f, 'un file modificato bloccabile');
  assert.equal(f.can.lock, true);
  await send('rowOp', { op: 'lock', key: f.key }); ok();
  const locked = last().sections.flatMap(s => s.rows).find(r => r.key === f.key);
  assert.equal(locked.locked, true); assert.equal(locked.can.unlock, true); assert.equal(locked.can.lock, false);
  await send('rowOp', { op: 'unlock', key: f.key }); ok();
  assert.equal(last().sections.flatMap(s => s.rows).find(r => r.key === f.key).locked, undefined);

  await send('cleanup', {}); ok();
  assert.match(last().status.msg, /Cleanup completato/);
});
