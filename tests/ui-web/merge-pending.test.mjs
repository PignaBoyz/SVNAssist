// Merge interrotto (conflitto, Annulla, errore, VS chiuso): banner in Changes, blocco nel Log con revisioni "applicate in questo merge",
// avanzamento "Applico rN (k di TOT)", comandi e schemi, flusso nel mock della demo (stato "merge fermo").
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { contract, readJson, validate, loadUi, loadDemo } from './helpers.mjs';

const U = loadUi(['i18n', 'util', 'diff', 'tree', 'prereq', 'ops', 'changes', 'log']);
const j = x => JSON.parse(JSON.stringify(x));
const cmds = contract('commands.schema.json');
const fxChanges = () => structuredClone(readJson('src/SVNAssist.UI.Web/contract/fixtures/changes.json'));
const fxLog = () => structuredClone(readJson('src/SVNAssist.UI.Web/contract/fixtures/log.json'));
const args = h => [...h.matchAll(/data-cmd="(\w+)"(?: data-args="([^"]*)")?/g)].map(([, name, a]) => ({ name, args: a ? JSON.parse(a.replace(/&quot;/g, '"')) : {} }));
const PEND = o => Object.assign({
  source: 'branches/x', kind: 'stopped', total: 7, applied: 3, remaining: 4,
  appliedText: 'Applicate 3 di 7: r14040, r14041, r14042; ferma alla r14042 per conflitto in 2 file.', remainingText: 'Rimaste 4: r14043…r14046.',
  steps: ['Risolvi i conflitti nei file elencati qui sotto (Usa la mia / Usa la loro / Usa entrambe / editor di merge).', 'Segna ogni file come risolto.',
    'Premi "Continua merge": vengono applicate solo le 4 revisioni rimaste (r14043…r14046); non serve rifare la selezione né un merge completo.'],
  alternative: 'Alternativa: "Annulla merge" riporta la working copy com\'era prima del merge.', conflictRev: 14042, unresolved: 2, canContinue: false,
  whyNot: 'Restano 2 file in conflitto: risolvili e segnali come risolti per poter continuare.', incompatible: false, warning: '',
  appliedRevs: [14040, 14041, 14042], remainingRevs: [14043, 14044, 14045, 14046], hasBackup: false, busy: false,
}, o);
const changesWith = p => { const s = fxChanges(); s.merge = { text: p.appliedText, conflicts: p.unresolved, pending: p }; return s; };
const MERGE = o => Object.assign({ source: 'branches/x', title: 'Merge da branches/x', loading: false, busy: false, error: '', blocked: '', selected: [], eligible: 4, result: null }, o);

test('banner Merge interrotto: applicate, rimaste, 3 passi numerati, alternativa; Continua disabilitato col motivo finche restano conflitti', () => {
  const s = changesWith(PEND());
  assert.deepEqual(validate(contract('changes-state.schema.json'), s), []);
  const h = U.changesHTML.mergeBanner(s);
  assert.match(h, /Merge interrotto/); assert.match(h, /Applicate 3 di 7: r14040, r14041, r14042; ferma alla r14042 per conflitto in 2 file\./);
  assert.match(h, /Rimaste 4: r14043…r14046\./);
  assert.equal((h.match(/<ol class="mb-steps">[\s\S]*?<\/ol>/)[0].match(/<li>/g) || []).length, 3);
  assert.match(h, /non serve rifare la selezione né un merge completo/); assert.match(h, /Alternativa: &quot;Annulla merge&quot;/);
  assert.match(h, /<button[^>]*data-cmd="mergeContinue"[^>]*disabled/);                    // disabilitato...
  assert.match(h, /title="Restano 2 file in conflitto[^"]*"/);                              // ...col motivo nel tooltip
  assert.match(h, /class="mb-l mb-why"[^>]*>Restano 2 file in conflitto/);                  // e scritto sotto
  assert.deepEqual(args(h).map(c => c.name), ['mergeContinue', 'mergeUndo']);               // niente Nascondi, niente Scarta
  args(h).forEach(c => assert.deepEqual(validate(cmds, c), [], c.name));
});

test('banner: conflitti risolti = Continua merge abilitato; incompatibile = solo Annulla merge e Scarta stato, niente passi; avviso del passaggio a meta', () => {
  let h = U.changesHTML.mergeBanner(changesWith(PEND({ unresolved: 0, canContinue: true, whyNot: '', kind: 'interrupted', conflictRev: null, appliedText: 'Applicate 3 di 7: r1, r2, r3; il merge si è interrotto prima della fine.', warning: 'Il merge è stato terminato mentre applicava r5-r9: attenzione.' })));
  assert.doesNotMatch(h, /data-cmd="mergeContinue"[^>]*disabled/); assert.match(h, /class="mb-l mb-warn"[^>]*>Il merge è stato terminato/);
  h = U.changesHTML.mergeBanner(changesWith(PEND({ incompatible: true, canContinue: false, whyNot: 'Il merge non si può più continuare: la working copy è stata aggiornata (da r10 a r12).' })));
  assert.deepEqual(args(h).map(c => c.name), ['mergeUndo', 'mergeDiscard']); assert.doesNotMatch(h, /mb-steps/); assert.match(h, /la working copy è stata aggiornata/);
  args(h).forEach(c => assert.deepEqual(validate(cmds, c), [], c.name));
  assert.match(U.changesHTML.mergeBanner(changesWith(PEND({ text: '<img src=x>', appliedText: '<img src=x onerror=1>' }))), /&lt;img src=x onerror=1&gt;/);   // testi scappati
});

test('Log merge interrotto: stesso blocco una volta sola, applicate marcate, rimaste evidenziate, Merge tutto/selezionate disabilitati, errore non ripetuto', () => {
  const s = fxLog(); s.sel = null;
  s.entries = [14046, 14045, 14044, 14043].map(r => ({ rev: r, author: 'ab', date: '2026-11-01T10:00:00', msg: 'm' + r, paths: [] }));
  s.merge = MERGE({ interrupted: PEND(), appliedInfo: [{ rev: 14040, author: 'lbianchi', msg: 'prima' }, { rev: 14041, author: 'mrossi', msg: 'seconda' }, { rev: 14042, author: 'gverdi', msg: 'terza' }] });
  assert.deepEqual(validate(contract('log-state.schema.json'), s), []);
  const h = U.logHTML.mergePanel(s);
  assert.equal((h.match(/Applicate 3 di 7/g) || []).length, 1);
  assert.match(h, /Applicate in questo merge \(3\)/); assert.equal((h.match(/applicata in questo merge/g) || []).length, 3);
  assert.match(h, /<b>r14041<\/b> <span class="muted">mrossi/);
  assert.match(h, /data-cmd="mergeAll"[^>]*disabled/); assert.match(h, /data-cmd="mergeSelected"[^>]*disabled/);
  assert.deepEqual(args(h).map(c => c.name).filter(n => /^merge(Continue|Undo|Discard|Resolve)$/.test(n)), ['mergeContinue', 'mergeUndo', 'mergeResolve']);
  assert.match(U.logHTML.list(s), /class="lr mrem"[^>]*data-rev="14046"/);
  assert.match(U.logHTML.top(s), /Merge interrotto: applicate 3 di 7/);
  // senza appliedInfo (log non ancora letto) le revisioni si elencano lo stesso
  delete s.merge.appliedInfo; assert.equal((U.logHTML.mergePanel(s).match(/applicata in questo merge/g) || []).length, 3);
});

test('avanzamento merge: "Applico rN (k di TOT)", barra a revisioni, gruppo applicato in un colpo; italiano e inglese', () => {
  const base = { op: 'merge', title: 'Merge da branches/x', phase: 'Merge in corso...', running: true, ok: false, processed: 12, conflicts: 0, omitted: 0, lines: [], summary: '', origin: 'log', cancellable: true };
  const one = { ...base, step: { label: 'r14043', first: 4, last: 4, done: 3, total: 7 } };
  assert.deepEqual(validate(contract('log-state.schema.json').properties.progress, one, contract('log-state.schema.json')), []);
  let h = U.ops.progressHTML(one);
  assert.match(h, /<b>Applico r14043 \(4 di 7\)<\/b>/); assert.match(h, /<progress class="obar det" max="7" value="3"/); assert.match(h, /3 di 7 revisioni applicate/);
  h = U.ops.progressHTML({ ...base, step: { label: 'r14041-r14090', first: 3, last: 52, done: 2, total: 420 } });
  assert.match(h, /Applico r14041-r14090 \(3-52 di 420\)/);
  assert.match(h, /Si ferma al termine del passaggio in corso/);
  assert.doesNotMatch(U.ops.progressHTML(base), /Applico/);                                     // senza passo (controllo dei conflitti previsti): fase e barra indeterminata
  U.setLang('en');
  try {
    assert.match(U.ops.progressHTML(one), /Applying r14043 \(4 of 7\)/);
    const b = U.changesHTML.mergeBanner(changesWith(PEND()));
    assert.match(b, /Merge interrupted/); assert.match(b, /Continue merge/); assert.match(b, /Undo merge/);
  } finally { U.setLang('it'); }
});

test('comandi mergeContinue / mergeDiscard nello schema', () => {
  assert.deepEqual(validate(cmds, { name: 'mergeContinue', args: {} }), []);
  assert.deepEqual(validate(cmds, { name: 'mergeDiscard', args: {} }), []);
  assert.notDeepEqual(validate(cmds, { name: 'mergeContinue', args: { x: 1 } }), []);
});

test('mock: Merge tutto con conflitto a meta si ferma dopo la revisione in conflitto; Continua rifiutato con conflitti, poi applica solo le rimaste; Scarta e Annulla', async () => {
  const { run, bridge } = await loadDemo();
  const B = bridge(), got = { changes: [], log: [] }, tick = () => new Promise(r => setImmediate(r));
  const wins = { changes: { frameElement: { isConnected: true } }, log: { frameElement: { isConnected: true } } };
  for (const v of ['changes', 'log']) B.attach(wins[v], m => got[v].push(j(m)));
  let n = 0;
  const send = async (view, name, a) => { B.postMessage({ v: 1, id: ++n, type: 'cmd', name, args: a }, wins[view]); for (let i = 0; i < 60; i++) await tick(); };
  const last = v => got[v].filter(m => m.type === 'state').at(-1).state;
  const ok = () => { assert.deepEqual(validate(contract('changes-state.schema.json'), last('changes')), []); assert.deepEqual(validate(contract('log-state.schema.json'), last('log')), []); };
  await run('autoDetect()', 't');
  for (const v of ['changes', 'log']) B.postMessage({ v: 1, type: 'ready', view: v, id: null }, wins[v]);
  await tick();
  await send('changes', 'refresh', {});
  await send('changes', 'mergeOpen', {});
  await send('changes', 'switchOk', { name: 'branches/feature-coupon' });
  await run('modal = async () => true', 't');
  await run("S.files.forEach(f => { if (f.st !== '?') { f.st = 'N'; f.text = f.base; } })", 't');   // working copy pulita (la demo parte con conflitti e file mancanti)
  await send('changes', 'refresh', {});
  await send('log', 'mergeAll', {}); ok();

  const p = last('changes').merge.pending;
  assert.equal(p.total, 4); assert.equal(p.applied, 3); assert.equal(p.remaining, 1); assert.equal(p.conflictRev, 129);
  assert.deepEqual(p.appliedRevs, [126, 128, 129]); assert.deepEqual(p.remainingRevs, [131]);
  assert.match(p.appliedText, /Applicate 3 di 4: r126, r128, r129; ferma alla r129 per conflitto in 1 file/);
  assert.equal(p.canContinue, false);
  assert.deepEqual(last('log').merge.interrupted.appliedRevs, [126, 128, 129]);
  assert.deepEqual(last('log').entries.map(e => e.rev), [131]);                                   // le applicate non sono piu "da unire"
  assert.equal(last('log').merge.pending, false);

  await send('log', 'mergeContinue', {}); ok();                                                   // conflitto ancora aperto: niente
  assert.equal(last('changes').merge.pending.applied, 3);
  assert.match(last('changes').status.msg, /file in conflitto/);

  await send('changes', 'conflictOp', { op: 'theirs', key: last('changes').conflicts[0].key }); ok();
  assert.equal(last('changes').merge.pending.canContinue, true);
  await send('log', 'mergeContinue', {}); ok();
  assert.equal(last('changes').merge.pending, undefined);                                         // concluso: banner normale "da committare"
  assert.match(last('changes').merge.text, /Merge r126-r131 da \^\/branches\/feature-coupon/);
  assert.match(last('changes').commit.recent[0], /r126[\s\S]*r128[\s\S]*r129[\s\S]*r131/);        // messaggio di commit con TUTTE le revisioni
  assert.deepEqual(last('log').entries.map(e => e.rev), []);

  await send('changes', 'mergeUndo', {}); ok();
  assert.equal(last('changes').merge, undefined);
});
