// SVN Merge: selettore in modo merge, Log in modalita merge (tabella con scelta multipla, pannello, esito), banner in Changes, comandi nello schema, flusso nel mock della demo.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { contract, readJson, validate, loadUi, loadDemo } from './helpers.mjs';

const U = loadUi(['i18n', 'util', 'diff', 'tree', 'prereq', 'changes', 'log']);
const j = x => JSON.parse(JSON.stringify(x));
const cmds = contract('commands.schema.json');
const fxChanges = () => structuredClone(readJson('src/SVNAssist.UI.Web/contract/fixtures/changes.json'));
const fxLog = () => structuredClone(readJson('src/SVNAssist.UI.Web/contract/fixtures/log.json'));
const args = h => [...h.matchAll(/data-cmd="(\w+)"(?: data-args="([^"]*)")?/g)].map(([, name, a]) => ({ name, args: a ? JSON.parse(a.replace(/&quot;/g, '"')) : {} }));
const MERGE = o => Object.assign({
  source: 'branches/x', title: 'Merge da branches/x', loading: false, busy: false, error: '', blocked: '', selected: [], eligible: 2, result: null,
}, o);
const logState = m => { const s = fxLog(); s.sel = null; s.merge = MERGE(m); return s; };
const result = o => Object.assign({
  mode: 'preview', revs: 'r120-r135', counts: { updated: 2, added: 1, deleted: 0, merged: 0, conflicted: 1 },
  items: [{ a: 'M', p: 'src/a.cs' }, { a: 'A', p: 'src/b.cs' }, { a: 'C', p: 'src/<img src=x>.cs' }], skipped: 0,
}, o);

test('banner: assente = niente markup; con conflitti avvisa; Annulla merge e Nascondi inviano comandi validi', () => {
  const s = fxChanges();
  assert.equal(U.changesHTML.mergeBanner(s), '');
  s.merge = { text: 'Merge r120-r135 da ^/branches/x: rivedi.', conflicts: 2 };
  const h = U.changesHTML.mergeBanner(s);
  assert.match(h, /class="mb warn"/); assert.match(h, /2 conflitti: risolvili qui sotto, poi committa/); assert.match(h, /Annulla merge/);
  s.merge.conflicts = 1; assert.match(U.changesHTML.mergeBanner(s), /1 conflitto: risolvilo qui sotto, poi committa/); s.merge.conflicts = 2;
  assert.deepEqual(args(h).map(c => c.name), ['mergeUndo', 'mergeDismiss']);
  args(h).forEach(c => assert.deepEqual(validate(cmds, c), [], c.name));
  s.merge.conflicts = 0;
  assert.doesNotMatch(U.changesHTML.mergeBanner(s), /warn|in conflitto/);
});

test('selettore in modo merge: titolo, testo e pulsante propri; si invia sempre switchOk', () => {
  const sw = { open: true, mode: 'merge', loading: false, error: '', items: [{ name: 'branches/x', kind: 'branch', current: false }] };
  const h = U.changesHTML.switchHTML(sw);
  assert.match(h, /SVN Merge/); assert.match(h, /da cui unire le modifiche/); assert.match(h, /Merge…/); assert.doesNotMatch(h, /<span>Switch<\/span>/);
  assert.match(U.changesHTML.switchHTML({ ...sw, mode: 'switch' }), /<span>Switch<\/span>/);
});

test('Log merge: righe con casella e aria-selected per le revisioni scelte, tabella a selezione multipla', () => {
  const s = logState({ selected: [123] });
  const h = U.logHTML.list(s);
  assert.match(h, /data-rev="124"[^>]*>(?:(?!data-rev)[\s\S])*?class="ck"/);
  assert.match(h, /class="lr sel" role="row" aria-selected="true"[^>]*data-rev="123"/);
  assert.match(h, /aria-selected="false"[^>]*data-rev="124"/);
  assert.match(h, /class="ck on"/);
  assert.doesNotMatch(U.logHTML.list(fxLog()), /class="ck/);   // log normale: nessuna casella
});

test('Log merge: intestazione al posto di working copy e filtri; elenco vuoto o in lettura', () => {
  const s = logState({});
  const top = U.logHTML.top(s);
  assert.match(top, /Merge da branches\/x/); assert.match(top, /2 revisioni da unire/); assert.doesNotMatch(top, /logform|logfilter/);
  assert.deepEqual(args(top).map(c => c.name), ['mergeClose']);
  s.entries = [];
  assert.match(U.logHTML.list(s), /Nessuna revisione da unire/);
  s.merge.loading = true;
  assert.match(U.logHTML.list(s), /Lettura delle revisioni da unire/);
});

test('pannello merge: Merge selezionate si abilita solo con una scelta; occupato disabilita tutto; avviso e errore in chiaro', () => {
  let h = U.logHTML.detail(logState({}));
  assert.match(h, /data-cmd="mergeSelected"[^>]*disabled/); assert.doesNotMatch(h, /data-cmd="mergeAll"[^>]*disabled/); assert.doesNotMatch(h, /mergeClear/);
  h = U.logHTML.detail(logState({ selected: [123, 124] }));
  assert.doesNotMatch(h, /data-cmd="mergeSelected"[^>]*disabled/); assert.match(h, /Merge selezionate \(2\)/); assert.match(h, /mergeClear/); assert.match(h, /2 scelte/);
  h = U.logHTML.detail(logState({ busy: true, selected: [1], blocked: 'WC sporca', error: 'Errore: boom <b>' }));
  assert.match(h, /data-cmd="mergePreview"[^>]*disabled/); assert.match(h, /data-cmd="mergeAll"[^>]*disabled/);
  assert.match(h, /role="alert"[\s\S]*WC sporca/); assert.match(h, /Errore: boom &lt;b&gt;/);
  assert.deepEqual(args(U.logHTML.detail(logState({ selected: [1] }))).map(c => c.name), ['mergePreview', 'mergeAll', 'mergeSelected', 'mergeClear']);
});

test('esito: anteprima con avviso sui conflitti non previsti; merge con rimando a Changes; file scappati e conteggi', () => {
  const p = U.logHTML.detail(logState({ result: result() }));
  assert.match(p, /Anteprima del merge di r120-r135/); assert.match(p, /2 aggiornati · 1 aggiunti · 0 eliminati · 0 uniti · 1 in conflitto/);
  assert.match(p, /conflitti ad albero e di proprietà/); assert.doesNotMatch(p, /<img/); assert.match(p, /Conflitto/);
  const m = U.logHTML.detail(logState({ result: result({ mode: 'merge', skipped: 2 }) }));
  assert.match(m, /Merge di r120-r135 eseguito/); assert.match(m, /rivedile in Changes/); assert.match(m, /2 percorsi saltati/);
});

test('in inglese i testi del merge sono tradotti', () => {
  U.setLang('en');
  try {
    const h = U.logHTML.detail(logState({ selected: [1], result: result() }));
    assert.match(h, /Merge all/); assert.match(h, /Merge selected \(1\)/); assert.match(h, /Preview of the merge of r120-r135/); assert.match(h, /1 in conflict/);
    assert.match(U.changesHTML.mergeBanner({ merge: { text: 'x', conflicts: 1 } }), /Undo merge/);
  } finally { U.setLang('it'); }
});

test('comandi: schema dei comandi merge; mergeToggle richiede rev', () => {
  for (const [name, a] of [['mergeOpen', {}], ['mergeUndo', {}], ['mergeDismiss', {}], ['mergeToggle', { rev: 7 }], ['mergeClear', {}], ['mergePreview', {}], ['mergeAll', {}], ['mergeSelected', {}], ['mergeClose', {}]])
    assert.deepEqual(validate(cmds, { name, args: a }), [], name);
  assert.notDeepEqual(validate(cmds, { name: 'mergeToggle', args: {} }), []);
  assert.notDeepEqual(validate(cmds, { name: 'mergeToggle', args: { rev: 0 } }), []);
  assert.notDeepEqual(validate(cmds, { name: 'mergeAll', args: { url: 'file:///x' } }), []);
});

test('schema: merge nel Log e nel banner validi; azione sconosciuta o campo in piu rifiutati', () => {
  const L = contract('log-state.schema.json'), C = contract('changes-state.schema.json');
  assert.deepEqual(validate(L, logState({ selected: [123], result: result() })), []);
  assert.deepEqual(validate(L, fxLog()), []);   // senza "merge" = log normale
  const bad = logState({ result: result({ items: [{ a: 'Z', p: 'x' }] }) });
  assert.notDeepEqual(validate(L, bad), []);
  assert.notDeepEqual(validate(L, logState({ extra: 1 })), []);
  const s = fxChanges(); s.merge = { text: 'x', conflicts: 0 };
  assert.deepEqual(validate(C, s), []);
  s.merge.conflicts = -1;
  assert.notDeepEqual(validate(C, s), []);
});

test('mock: apri (senza il ramo corrente), scegli, anteprima, cherry-pick con conflitto, banner, messaggio precompilato, annulla', async () => {
  const { run, bridge } = await loadDemo();
  const B = bridge(), got = { changes: [], log: [] }, tick = () => new Promise(r => setImmediate(r));
  const wins = { changes: { frameElement: { isConnected: true } }, log: { frameElement: { isConnected: true } } };
  for (const v of ['changes', 'log']) { B.attach(wins[v], m => got[v].push(j(m))); }
  let n = 0;
  const send = async (view, name, a) => { B.postMessage({ v: 1, id: ++n, type: 'cmd', name, args: a }, wins[view]); for (let i = 0; i < 40; i++) await tick(); };
  const last = v => got[v].filter(m => m.type === 'state').at(-1).state;
  const ok = () => { assert.deepEqual(validate(contract('changes-state.schema.json'), last('changes')), []); assert.deepEqual(validate(contract('log-state.schema.json'), last('log')), []); };
  await run('autoDetect()', 't');
  for (const v of ['changes', 'log']) B.postMessage({ v: 1, type: 'ready', view: v, id: null }, wins[v]);
  await tick();
  await send('changes', 'refresh', {}); ok();

  await send('changes', 'mergeOpen', {}); ok();
  assert.equal(last('changes').switchPicker.mode, 'merge');
  assert.ok(!last('changes').switchPicker.items.some(i => i.current), 'il ramo corrente non e tra le sorgenti');
  await send('changes', 'switchOk', { name: 'trunk' }); ok();
  assert.match(last('changes').switchPicker.error, /non valido/);
  await send('changes', 'switchOk', { name: 'branches/feature-coupon' }); ok();
  assert.equal(last('changes').switchPicker.open, false);
  let m = last('log').merge;
  assert.equal(m.title, 'Merge da branches/feature-coupon'); assert.equal(m.eligible, 4);
  assert.deepEqual(last('log').entries.map(e => e.rev), [131, 129, 128, 126]);
  assert.match(m.blocked, /modifiche locali/, 'la working copy della demo parte sporca: il merge e bloccato');

  await run("S.files.forEach(f => { if (f.st !== '?') { f.st = 'N'; f.text = f.base; } })", 't');   // working copy pulita
  await run('modal = async () => true', 't');   // dialoghi (proposta "Risolvi ora", conferma dell'annullamento): senza DOM non esistono
  await send('log', 'mergePreview', {}); ok();
  m = last('log').merge;
  assert.equal(m.blocked, ''); assert.equal(m.result.mode, 'preview'); assert.equal(m.result.revs, 'r126-r131');
  assert.deepEqual(m.result.counts, { updated: 3, added: 1, deleted: 1, merged: 0, conflicted: 1 });
  assert.ok(!last('changes').merge, 'l\'anteprima non tocca la working copy');

  await send('log', 'mergeSelected', {}); ok();
  assert.match(last('log').merge.error, /almeno una revisione/);
  await send('log', 'mergeToggle', { rev: 129 }); await send('log', 'mergeToggle', { rev: 128 }); ok();
  assert.deepEqual(last('log').merge.selected, [128, 129]);
  await send('log', 'mergeToggle', { rev: 999 }); assert.deepEqual(last('log').merge.selected, [128, 129], 'una revisione non in elenco si ignora');
  await send('log', 'mergeSelected', {}); ok();
  m = last('log').merge;
  assert.equal(m.result.mode, 'merge'); assert.equal(m.result.revs, 'r128, r129'); assert.equal(m.result.counts.conflicted, 1);
  assert.deepEqual(m.selected, []); assert.deepEqual(last('log').entries.map(e => e.rev), [131, 126], 'le revisioni unite non sono piu da unire');
  const c = last('changes');
  assert.equal(c.merge.text, 'Merge r128, r129 da ^/branches/feature-coupon: rivedi le modifiche e committa, oppure annulla il merge.'); assert.equal(c.merge.conflicts, 1);
  assert.equal(c.commit.text, 'Merge r128, r129 da ^/branches/feature-coupon');
  assert.ok(c.sections.some(s => s.rows.some(r => r.status === 'C')), 'il conflitto compare in Changes');
  assert.deepEqual(c.conflicts.map(x => [x.name, x.kind]), [['CheckoutController.cs', 'text']]);
  assert.deepEqual(c.conflicts[0].files, ['Web/Controllers/CheckoutController.cs.working', 'Web/Controllers/CheckoutController.cs.merge-left.r127', 'Web/Controllers/CheckoutController.cs.merge-right.r129'], 'file di appoggio del merge, raggruppati sotto il file');
  assert.equal(m.pending, true, 'merge eseguito e non committato: "da committare", non "0 da unire"');
  assert.match(last('log').msg, /Merge eseguito, da committare. Conflitti da risolvere in Changes: 1/);
  await send('log', 'mergeResolve', {}); ok();

  await send('changes', 'mergeUndo', {}); ok();
  assert.ok(!last('changes').merge); assert.equal(last('changes').commit.text, '');
  assert.match(last('changes').status.msg, /Merge annullato/);
  assert.equal(last('log').merge.eligible, 4, 'dopo l\'annullamento le revisioni tornano da unire');
  assert.equal(last('log').merge.pending, false);

  await send('log', 'mergeClose', {}); ok();
  assert.ok(!last('log').merge);
});
