// Sezione Conflitti di Changes (azioni esplicite, file di appoggio raggruppati) e Log in modalita merge dopo un merge con conflitti.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { contract, readJson, validate, loadUi, loadDemo } from './helpers.mjs';

const U = loadUi(['i18n', 'util', 'diff', 'tree', 'prereq', 'changes', 'log']);
const cmds = contract('commands.schema.json');
const fxChanges = () => structuredClone(readJson('src/SVNAssist.UI.Web/contract/fixtures/changes.json'));
const fxLog = () => structuredClone(readJson('src/SVNAssist.UI.Web/contract/fixtures/log.json'));
const args = h => [...h.matchAll(/data-cmd="(\w+)"(?: data-args="([^"]*)")?/g)].map(([, name, a]) => ({ name, args: a ? JSON.parse(a.replace(/&quot;/g, '"')) : {} }));
const C = (o) => Object.assign({ key: 'changes:README.txt', path: 'README.txt', name: 'README.txt', kind: 'text', files: ['README.txt.merge-left.r3', 'README.txt.merge-right.r6', 'README.txt.working'] }, o);
const state = (...c) => Object.assign(fxChanges(), { conflicts: c });

test('senza conflitti niente sezione (anche con "conflicts" assente)', () => {
  assert.equal(U.changesHTML.conflictsHTML(fxChanges()), '');
  assert.equal(U.changesHTML.conflictsHTML(state()), '');
});

test('conflitto di testo: 4 azioni esplicite con comandi validi, file di appoggio sotto il file', () => {
  const h = U.changesHTML.conflictsHTML(state(C()));
  assert.match(h, /<section class="cfl"[^>]*aria-label="Conflitti"/); assert.match(h, /<b>Conflitti<\/b><span class="pill">1<\/span>/);
  for (const l of ['Apri editor di merge', 'Usa la mia', 'Usa la loro', 'Segna come risolto']) assert.match(h, new RegExp('<span>' + l + '</span>'));
  const a = args(h);
  assert.deepEqual(a.map(c => c.args.op), ['edit', 'mine', 'theirs', 'resolved']);
  a.forEach(c => { assert.equal(c.name, 'conflictOp'); assert.equal(c.args.key, 'changes:README.txt'); assert.deepEqual(validate(cmds, c), [], c.args.op); });
  assert.match(h, /File di appoggio di svn \(spariscono dopo il resolve\)/);
  assert.deepEqual([...h.matchAll(/<code>([^<]+)<\/code>/g)].map(m => m[1]), ['README.txt.merge-left.r3', 'README.txt.merge-right.r6', 'README.txt.working']);
  assert.match(h, /Conflitto di testo/);
});

test('conflitto ad albero: solo spiegazione e "Segna come risolto"; sulle proprieta niente editor', () => {
  const t = U.changesHTML.conflictsHTML(state(C({ kind: 'tree', files: [] })), { open: true, treeOpen: true });
  assert.deepEqual(args(t).map(c => c.args.op), [undefined, 'resolved'], 'gruppo (Segna tutti) + la voce');
  assert.match(t, /Conflitti ad albero \(1\)/); assert.match(t, /non ci sono versioni da scegliere/); assert.doesNotMatch(t, /File di appoggio/);
  const p = U.changesHTML.conflictsHTML(state(C({ kind: 'property' })));
  assert.deepEqual(args(p).map(c => c.args.op), ['mine', 'theirs', 'resolved']);
});

test('conflitti: nomi neutralizzati, percorso mostrato se diverso dal nome, pulsanti fermi durante il caricamento, inglese', () => {
  const s = state(C({ name: '<img src=x>.txt', path: 'a/<img src=x>.txt', files: ['<b>x</b>'] }));
  const h = U.changesHTML.conflictsHTML(s);
  assert.doesNotMatch(h, /<img/); assert.doesNotMatch(h, /<b>x<\/b>/); assert.match(h, /cf-p/);
  s.loading = true; const busy = U.changesHTML.conflictsHTML(s);
  const acts = busy.replace(/<button[^>]*data-cfl-toggle[^>]*>/g, '');   // il pulsante comprimi/espandi e' solo della UI: resta attivo
  assert.equal((acts.match(/<button/g) || []).length, (acts.match(/<button[^>]*disabled/g) || []).length, 'durante il caricamento nessuna azione e attiva');
  U.setLang('en');
  try {
    const e = U.changesHTML.conflictsHTML(state(C()));
    for (const l of ['Open merge editor', 'Use mine', 'Use theirs', 'Mark as resolved', 'Conflicts']) assert.match(e, new RegExp(l));
  } finally { U.setLang('it'); }
});

test('schema: conflicts valido; kind sconosciuto o campo in piu rifiutati; comando conflictOp con op noto', () => {
  const S = contract('changes-state.schema.json');
  assert.deepEqual(validate(S, state(C(), C({ key: 'changes:t', kind: 'tree', files: [] }))), []);
  assert.notDeepEqual(validate(S, state(C({ kind: 'binary' }))), []);
  assert.notDeepEqual(validate(S, state(C({ extra: 1 }))), []);
  for (const op of ['edit', 'mine', 'theirs', 'resolved']) assert.deepEqual(validate(cmds, { name: 'conflictOp', args: { op, key: 'changes:a' } }), [], op);
  assert.notDeepEqual(validate(cmds, { name: 'conflictOp', args: { op: 'base', key: 'changes:a' } }), []);
  assert.notDeepEqual(validate(cmds, { name: 'conflictOp', args: { op: 'edit' } }), []);
  assert.deepEqual(validate(cmds, { name: 'mergeResolve', args: {} }), []);
});

const MERGE = o => Object.assign({
  source: 'branches/x', title: 'Merge da branches/x', loading: false, busy: false, error: '', blocked: '', selected: [], eligible: 0, result: null,
}, o);
const logState = m => { const s = fxLog(); s.sel = null; s.entries = []; s.merge = MERGE(m); return s; };
const result = o => Object.assign({ mode: 'merge', revs: 'r4-r6', counts: { updated: 2, added: 1, deleted: 0, merged: 0, conflicted: 1 }, items: [{ a: 'C', p: 'README.txt' }], skipped: 0 }, o);

test('Log merge: dopo il merge "0 da unire" diventa "Merge eseguito, da committare" finche non si committa o annulla', () => {
  const pend = logState({ pending: true, result: result() });
  assert.match(U.logHTML.top(pend), /Merge eseguito, da committare/); assert.doesNotMatch(U.logHTML.top(pend), /0 revisioni da unire/);
  assert.match(U.logHTML.list(pend), /Merge eseguito, da committare/); assert.doesNotMatch(U.logHTML.list(pend), /Nessuna revisione da unire/);
  assert.match(U.logHTML.detail(pend), /Merge eseguito, da committare/);
  const idle = logState({ pending: false });
  assert.match(U.logHTML.top(idle), /0 revisioni da unire/); assert.match(U.logHTML.list(idle), /Nessuna revisione da unire/);
  assert.deepEqual(validate(contract('log-state.schema.json'), pend), []);
  U.setLang('en');
  try { assert.match(U.logHTML.top(pend), /Merge done, ready to commit/); } finally { U.setLang('it'); }
});

test('Log merge: il risultato con conflitti ha "Risolvi i conflitti" (mergeResolve); senza conflitti o in anteprima no', () => {
  const h = U.logHTML.detail(logState({ pending: true, result: result() }));
  assert.match(h, /1 in conflitto: vanno risolti prima di committare/);
  const a = args(h).filter(c => c.name === 'mergeResolve'); assert.equal(a.length, 1); assert.deepEqual(validate(cmds, a[0]), []);
  assert.match(h, /<span>Risolvi i conflitti<\/span>/);
  assert.doesNotMatch(U.logHTML.detail(logState({ result: result({ counts: { updated: 2, added: 0, deleted: 0, merged: 0, conflicted: 0 } }) })), /mergeResolve/);
  assert.doesNotMatch(U.logHTML.detail(logState({ result: result({ mode: 'preview' }) })), /mergeResolve/);
});

test('mock: i conflitti della demo con file di appoggio; "Segna come risolto" rifiuta i marcatori; mergeResolve va in Changes', async () => {
  const { run, bridge } = await loadDemo();
  const B = bridge(), got = [], win = { frameElement: { isConnected: true } }, tick = () => new Promise(r => setImmediate(r));
  B.attach(win, m => got.push(JSON.parse(JSON.stringify(m))));
  const send = async (name, a) => { B.postMessage({ v: 1, id: send.n = (send.n || 0) + 1, type: 'cmd', name, args: a }, win); for (let i = 0; i < 30; i++) await tick(); };
  const last = () => got.filter(m => m.type === 'state').at(-1).state;
  const ok = () => assert.deepEqual(validate(contract('changes-state.schema.json'), last()), []);
  await run('autoDetect()', 't');
  B.postMessage({ v: 1, type: 'ready', view: 'changes', id: null }, win); await tick();
  await send('refresh', {}); ok();
  const cf = last().conflicts.filter(c => c.kind === 'text');   // i tree conflict hanno la loro prova piu' sotto
  assert.equal(cf.length, 1); assert.equal(cf[0].kind, 'text'); assert.equal(cf[0].name, 'CheckoutController.cs');
  assert.deepEqual(cf[0].files, ['Web/Controllers/CheckoutController.cs.mine', 'Web/Controllers/CheckoutController.cs.r124', 'Web/Controllers/CheckoutController.cs.r125']);
  await send('conflictOp', { op: 'resolved', key: cf[0].key }); ok();
  assert.match(last().status.msg, /marcatori di conflitto/, 'con i marcatori ancora nel file non si segna come risolto');
  assert.equal(last().conflicts.filter(c => c.kind === 'text').length, 1);
  await send('mergeResolve', {}); ok();
  await run("modal = async () => true", 't');
  await send('conflictOp', { op: 'mine', key: cf[0].key }); ok();
  assert.deepEqual(last().conflicts.filter(c => c.kind === 'text'), []); assert.match(last().status.msg, /Conflitto risolto/);
});

test('tree conflict raggruppati: un gruppo con spiegazione e "Segna tutti come risolti"; elenco espandibile con "Segna come risolto" per voce', () => {
  const trees = [C({ key: 'changes:a.cs', path: 'Core/a.cs', name: 'a.cs', kind: 'tree', files: [] }), C({ key: 'changes:b.cs', path: 'b.cs', name: 'b.cs', kind: 'tree', files: [] })];
  const s = state(C(), ...trees), h = U.changesHTML.conflictsHTML(s);
  assert.match(h, /<b>Conflitti<\/b><span class="pill">3<\/span>/);
  assert.match(h, /Conflitti ad albero \(2\)/); assert.match(h, /non ci sono versioni da scegliere/); assert.match(h, /<span>Segna tutti come risolti<\/span>/);
  const a = args(h); assert.deepEqual(a.map(c => c.name === 'conflictOp' ? c.args.op : c.name), ['edit', 'mine', 'theirs', 'resolved', 'resolveTreeConflicts'], 'a elenco chiuso: solo il testo e il pulsante di gruppo');
  a.forEach(c => assert.deepEqual(validate(cmds, c), []));
  assert.doesNotMatch(h, /Conflitto ad albero<\/span>/, 'le voci ad albero non hanno il riquadro singolo');
  const open = U.changesHTML.conflictsHTML(s, { open: true, treeOpen: true });
  assert.deepEqual(args(open).filter(c => c.name === 'conflictOp' && c.args.op === 'resolved').map(c => c.args.key), ['changes:README.txt', 'changes:a.cs', 'changes:b.cs']);
  assert.match(open, /data-cfl-tree[^>]*aria-expanded="true"/); assert.match(open, /Nascondi elenco/);
  assert.match(open, /<b class="cf-n" title="Core\/a.cs">a.cs<\/b>/);
  const closed = U.changesHTML.conflictsHTML(s, { open: false });
  assert.doesNotMatch(closed, /cfl-l/); assert.match(closed, /data-cfl-toggle[^>]*aria-expanded="false"/);
  assert.equal(U.changesHTML.conflictsHTML(state(C()), { open: true }).includes('resolveTreeConflicts'), false, 'senza tree conflict niente gruppo');
  U.setLang('en');
  try { const e = U.changesHTML.conflictsHTML(s); assert.match(e, /Tree conflicts \(2\)/); assert.match(e, /Mark all as resolved/); } finally { U.setLang('it'); }
  assert.deepEqual(validate(contract('changes-state.schema.json'), s), []);
  assert.deepEqual(validate(cmds, { name: 'resolveTreeConflicts', args: {} }), []);
  s.loading = true; assert.match(U.changesHTML.conflictsHTML(s), /data-cmd="resolveTreeConflicts"[^>]*disabled/);
});

test('mock: tree conflict della demo raggruppati; "Segna tutti come risolti" li risolve in un colpo (con conferma) e lascia il testo', async () => {
  const { run, bridge } = await loadDemo();
  const B = bridge(), got = [], win = { frameElement: { isConnected: true } }, tick = () => new Promise(r => setImmediate(r));
  B.attach(win, m => got.push(JSON.parse(JSON.stringify(m))));
  const send = async (name, a) => { B.postMessage({ v: 1, id: send.n = (send.n || 0) + 1, type: 'cmd', name, args: a }, win); for (let i = 0; i < 30; i++) await tick(); };
  const last = () => got.filter(m => m.type === 'state').at(-1).state;
  await run('autoDetect()', 't');
  B.postMessage({ v: 1, type: 'ready', view: 'changes', id: null }, win); await tick();
  await send('refresh', {});
  assert.deepEqual(last().conflicts.map(c => c.kind).sort(), ['text', 'tree', 'tree'], 'almeno due tree conflict nella demo');
  await run("modal = async () => false", 't');
  await send('resolveTreeConflicts', {});
  assert.equal(last().conflicts.filter(c => c.kind === 'tree').length, 2, 'conferma negata: nulla cambia');
  await run("modal = async () => true", 't');
  await send('resolveTreeConflicts', {});
  assert.deepEqual(last().conflicts.map(c => c.kind), ['text']); assert.match(last().status.msg, /Conflitti ad albero risolti: 2/);
  assert.deepEqual(validate(contract('changes-state.schema.json'), last()), []);
});
