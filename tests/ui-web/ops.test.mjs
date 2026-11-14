// 0.4.0: finestre modali di avanzamento (righe svn in streaming), avviso switch su working copy sporca, opzioni del Cleanup.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { contract, validate, loadUi, loadDemo } from './helpers.mjs';

const U = loadUi(['i18n', 'util', 'ops']);
const O = U.ops;
const prog = (o = {}) => ({ op: 'switch', title: 'Switch a trunk', phase: 'Switch in corso...', running: true, ok: false, processed: 3, conflicts: 1, omitted: 0, lines: [{ k: 'U', t: 'U    a.cs' }, { k: 'C', t: 'C    b.cs' }, { k: 'i', t: "Updating '.':" }], summary: '', ...o });

test('avanzamento in corso: barra indeterminata, contatore, conflitto evidenziato, nessun pulsante di chiusura', () => {
  const h = O.progressHTML(prog());
  assert.match(h, /class="obar"/);
  assert.match(h, /3 elementi elaborati/);
  assert.match(h, /class="ol c"/);
  assert.doesNotMatch(h, /progressClose/);
});

test('avanzamento finito: riepilogo, Chiudi e "Apri i conflitti" solo con conflitti', () => {
  const h = O.progressHTML(prog({ running: false, ok: true, summary: 'Switch completato con 1 conflitti.' }));
  assert.match(h, /obar ok/);
  assert.match(h, /data-cmd="progressClose" data-args="\{&quot;conflicts&quot;:true\}"/);
  assert.match(h, /data-cmd="progressClose" data-args="\{&quot;conflicts&quot;:false\}"/);
  assert.doesNotMatch(O.progressHTML(prog({ running: false, ok: true, conflicts: 0, summary: 'ok' })), /conflicts&quot;:true/);
});

test('avviso switch: conteggi per tipo e tre azioni; pulita = solo "Esegui lo switch"', () => {
  const w = { target: 'trunk', total: 3, counts: { modified: 2, added: 0, deleted: 0, conflicted: 1, unversioned: 0 } };
  const h = O.warnHTML(w);
  assert.match(h, /<b>2<\/b> modificati/); assert.match(h, /<b>1<\/b> in conflitto/); assert.doesNotMatch(h, /aggiunti/);
  for (const c of ['switchWarnContinue', 'switchWarnCleanup', 'switchWarnCancel']) assert.ok(h.includes(`data-cmd="${c}"`), c);
  const clean = O.warnHTML({ ...w, total: 0, counts: { modified: 0, added: 0, deleted: 0, conflicted: 0, unversioned: 0 } });
  assert.doesNotMatch(clean, /switchWarnCleanup/); assert.match(clean, /Esegui lo switch/);
});

test('cleanup: opzioni distruttive richiedono la conferma esplicita; svn vecchio disabilita le opzioni extra', () => {
  O.resetCleanup();
  assert.equal(O.cleanupValid(), true);
  O.toggle('unversioned');
  assert.equal(O.cleanupValid(), false);   // distruttiva senza conferma
  assert.match(O.cleanupHTML({ extras: true, svnVersion: '1.14.3', from: '' }), /data-ock="confirmed"/);
  O.toggle('confirmed');
  assert.equal(O.cleanupValid(), true);
  assert.equal(O.cleanupOpts().confirmed, true);
  O.toggle('unversioned');   // tolta l'ultima distruttiva: la conferma si azzera
  assert.equal(O.cleanupOpts().confirmed, false);
  O.toggle('status'); assert.equal(O.cleanupValid(), false);   // niente da fare
  O.toggle('revert'); O.toggle('confirmed'); assert.equal(O.cleanupValid(), true);   // revert da solo
  O.resetCleanup();
  const old = O.cleanupHTML({ extras: false, svnVersion: '1.8.11', from: 'trunk' });
  assert.match(old, /data-ock="vacuum"\s+disabled/); assert.match(old, /1\.8\.11/); assert.match(old, /trunk/);
});

test('demo: switch su working copy sporca -> avviso -> Continua -> avanzamento con righe e riepilogo; schema valido', async () => {
  const { run, bridge } = await loadDemo();
  const B = bridge(), got = [], win = { frameElement: { isConnected: true } }, tick = () => new Promise(r => setImmediate(r));
  B.attach(win, m => got.push(JSON.parse(JSON.stringify(m))));
  const send = async (name, args = {}) => { B.postMessage({ v: 1, id: send.n = (send.n || 0) + 1, type: 'cmd', name, args }, win); for (let i = 0; i < 80; i++) await tick(); };
  const last = () => got.filter(m => m.type === 'state').at(-1).state;
  const ok = () => assert.deepEqual(validate(contract('changes-state.schema.json'), last()), []);
  await run('autoDetect()', 't');
  B.postMessage({ v: 1, type: 'ready', view: 'changes', id: null }, win); await tick();
  await send('switchOpen'); await send('switchOk', { name: 'branches/feature-coupon' }); ok();
  assert.ok(last().switchWarn.total > 0);
  await send('switchWarnContinue'); ok();
  const p = last().progress;
  assert.equal(p.op, 'switch'); assert.equal(p.running, false); assert.ok(p.lines.length > 10); assert.equal(p.conflicts, 1);
  await send('progressClose', { conflicts: false });
  assert.equal(last().progress, undefined);
});
