// Barra di stato (messaggi di successo che spariscono, indicatore della revisione), ordine dello stato vuoto,
// tendina "Recenti" e apertura dei file nuovi/non versionati. Parti pure della UI + demo.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { contract, readJson, validate, loadUi, loadDemo } from './helpers.mjs';

// setTimeout finto: i timer si fanno scattare a mano (nessuna attesa reale)
const timers = [];
const U = loadUi(['i18n', 'util', 'diff', 'tree', 'prereq', 'changes', 'log'], { setTimeout: (f, ms) => { timers.push({ f, ms }); return timers.length; }, clearTimeout: id => { if (timers[id - 1]) timers[id - 1].f = null; } });
const H = U.changesHTML;
const fx = () => structuredClone(readJson('src/SVNAssist.UI.Web/contract/fixtures/changes.json'));
const cmds = contract('commands.schema.json');
const fire = () => timers.splice(0).forEach(t => t.f && t.f());
const msg = h => (h.match(/class="msg[^"]*"[^>]*title="([^"]*)"/) || [])[1];

test('statusKind: successo, errore, informazione', () => {
  assert.equal(H.statusKind('Commit completato: revisione 5 (1 elementi).', false), 'ok');
  assert.equal(H.statusKind('Aggiornato alla revisione 7.', false), 'ok');
  assert.equal(H.statusKind('Errore commit: x', true), 'err');
  assert.equal(H.statusKind('2 modificati, 1 non versionati.', false), '');
  assert.equal(H.OK_TTL, 5000);
});

test('un messaggio di successo sparisce dopo ~5 s e lascia solo la revisione; un errore resta', () => {
  const s = fx(); s.status = { msg: 'Commit completato: revisione 125 (2 elementi).', err: false, seq: 3 };
  H.trackStatus(s);
  assert.equal(timers.at(-1).ms, 5000, 'timer di 5 secondi');
  assert.match(msg(H.foot(s)), /Commit completato/);
  fire();
  const h = H.foot(s);
  assert.equal(msg(h), undefined, 'messaggio sparito');
  assert.match(h, /class="foot-r1"><\/div>/, 'riga del messaggio vuota (nascosta dal CSS)');
  assert.match(h, /class="rev ok"[^>]*>[\s\S]*r124/, 'la revisione resta');

  // lo stesso testo ma un nuovo messaggio (seq diverso) riparte da capo
  s.status.seq = 4; H.trackStatus(s);
  assert.match(msg(H.foot(s)), /Commit completato/);
  assert.equal(timers.length, 1);

  // un errore non ha timer e non sparisce
  timers.length = 0;
  const e = fx(); e.status = { msg: 'Errore update: rete non raggiungibile', err: true, seq: 9 };
  H.trackStatus(e); assert.equal(timers.length, 0);
  fire(); assert.match(msg(H.foot(e)), /Errore update/);
  // un messaggio di sola informazione (riepilogo) resta
  const i = fx(); i.status = { msg: '2 modificati.', err: false, seq: 9 };
  H.trackStatus(i); assert.equal(timers.length, 0);
});

test('indicatore: verde alla HEAD, arancio se il server e avanti (clic = SVN Update), neutro se non si sa', () => {
  const s = fx(); const wc = s.workingCopy;
  Object.assign(wc, { rev: 1234, head: 1234, revState: 'current' });
  let h = H.revIndicator(s);
  assert.match(h, /^<span class="rev ok" title="La working copy è all&#39;ultima revisione del ramo \(r1234\)\."/); assert.match(h, /<span>r1234<\/span>/);

  Object.assign(wc, { head: 1240, revState: 'behind' });
  h = H.revIndicator(s);
  assert.match(h, /^<button type="button" class="rev bad" data-cmd="update"/);
  assert.match(h, /<span>r1234 · disponibile r1240 — SVN Update<\/span>/);
  assert.deepEqual(validate(cmds, { name: 'update', args: {} }), []);

  Object.assign(wc, { revState: 'unknown', revNote: 'Impossibile controllare le nuove revisioni: E170013' }); delete wc.head;
  h = H.revIndicator(s);
  assert.match(h, /^<span class="rev" title="Impossibile controllare le nuove revisioni: E170013"/);
  assert.doesNotMatch(h, /rev ok|rev bad|<svg/, 'neutro: nessun colore e nessuna icona');

  delete wc.rev; assert.equal(H.revIndicator(s), '', 'senza revisione niente indicatore');
});

test('indicatore: tradotto in inglese e testi escapati', () => {
  const s = fx(); Object.assign(s.workingCopy, { rev: 1234, head: 1240, revState: 'behind' });
  U.setLang('en');
  try {
    const h = H.revIndicator(s);
    assert.match(h, /r1234 · r1240 available — SVN Update/); assert.match(h, /Someone has committed: the server is at r1240, your working copy is at r1234/);
  } finally { U.setLang('it'); }
  Object.assign(s.workingCopy, { revState: 'unknown', revNote: '<img src=x onerror=1>' });
  assert.doesNotMatch(H.revIndicator(s), /<img/);
});

test('header: la revisione sta solo nella barra di stato', () => {
  const s = fx(); s.workingCopy.rev = 1234;
  assert.doesNotMatch(H.header(s), /r1234/);
});

test('stato vuoto: prima le sezioni (Staged c\'e\' sempre), poi "Nessuna modifica"', () => {
  const s = fx(); s.sections.forEach(x => { x.count = 0; x.rows = []; });
  const h = H.listHTML(s, null, '');
  assert.ok(h.indexOf('data-key="section:staged"') >= 0 && h.indexOf('Nessuna modifica') > h.indexOf('data-key="section:staged"'), 'stato vuoto sotto la sezione Staged');
  assert.equal(H.listHTML(Object.assign(fx(), { loading: true }), null, '').includes('Nessuna modifica'), false, 'non durante il caricamento');
  assert.equal(H.listHTML(fx(), null, '').includes('empty-state'), false, 'con modifiche niente stato vuoto');
});

test('pannello Recenti: bottone nella barra del commit, voci con prima riga in grassetto + anteprima + testo intero nel tooltip, ricerca, indici originali', () => {
  const s = fx(); s.commit.count = 1;
  assert.match(H.commitBar(s), /data-recent[^>]*aria-haspopup="dialog"[\s\S]*?<span>Recenti<\/span>/);
  s.commit.recent = ['Prima riga\n\nseconda riga\nterza <b>riga</b>', 'x'.repeat(100), 'Corregge il totale', 'fix: ARROTONDAMENTO iva\n\ndettaglio'];
  const h = H.recentHTML(s);
  assert.match(h, /<input id="rc-q"[^>]*role="combobox"[^>]*placeholder="Cerca nei messaggi recenti…"/); assert.match(h, /id="rc-list" role="listbox"/);
  const rows = [...h.matchAll(/<div class="rc-row[^"]*" role="option"[^>]*data-i="(\d+)"[^>]*title="([^"]*)"><b class="rc-1">([^<]*)<\/b>(?:<span class="rc-p">([^<]*)<\/span>)?/g)];
  assert.equal(rows.length, 4); assert.deepEqual(rows.map(r => +r[1]), [0, 1, 2, 3]);
  assert.equal(rows[0][3], 'Prima riga'); assert.equal(rows[0][4], 'seconda riga\nterza &lt;b&gt;riga&lt;/b&gt;', 'anteprima: le righe dopo la prima, senza vuote, neutralizzate');
  assert.match(rows[0][2], /Prima riga\n\nseconda riga/, 'tooltip = messaggio intero'); assert.equal(rows[2][4], undefined, 'una sola riga: niente anteprima');
  assert.match(h, /class="rc-row sel"[^>]*data-i="0"/, 'la prima voce e selezionata'); assert.match(h, /4 di 4/);
  // ricerca: senza maiuscole, su tutto il messaggio (anche oltre la prima riga); l'indice e quello dell'elenco intero (e' quello che arriva al backend)
  assert.deepEqual(U.recentFilter(s.commit.recent, 'IVA').map(x => x.i), [3]);
  assert.deepEqual(U.recentFilter(s.commit.recent, 'dettaglio').map(x => x.i), [3]);
  assert.deepEqual(U.recentFilter(s.commit.recent, '  ').map(x => x.i), [0, 1, 2, 3]);
  const f = H.recentHTML(s, { q: 'totale', sel: 0 }); assert.match(f, /data-i="2"/); assert.doesNotMatch(f, /data-i="0"/); assert.match(f, /1 di 4/);
  assert.match(H.recentHTML(s, { q: 'zzz', sel: 0 }), /Nessun messaggio corrisponde alla ricerca/);
  [0, 1, 2, 3].forEach(i => assert.deepEqual(validate(cmds, { name: 'useRecentMessage', args: { i } }), []));
  assert.notDeepEqual(validate(cmds, { name: 'useRecentMessage', args: { i: -1 } }), [], 'indice negativo rifiutato dallo schema');
  delete s.commit.recent;
  assert.match(H.recentHTML(s), /Nessun messaggio recente/); assert.doesNotMatch(H.recentHTML(s), /rc-row/);
  U.setLang('en');
  try { assert.match(H.recentHTML(Object.assign(fx(), { commit: Object.assign(fx().commit, { recent: ['a'] }) })), /Search recent messages…[\s\S]*1 of 1/); } finally { U.setLang('it'); }
});

test('file nuovo o non versionato: azione "Apri il file", non "differenze"', () => {
  const s = fx(), n = h => (h.match(/Apri il file nell&#39;editor \(Invio\)/g) || []).length;
  const a = n(U.treeHTML(s.sections, null));
  assert.ok(a >= 1, 'il file aggiunto (A) ha "Apri il file"');
  const un = s.sections.flatMap(x => x.rows).find(r => r.status === '?' && r.kind === 'file'); un.can.diff = true;
  assert.ok(n(U.treeHTML(s.sections, null)) > a, 'anche il non versionato (?)');
});

// ---- demo ----
const j = x => JSON.parse(JSON.stringify(x));
async function demo() {
  const { run, bridge } = await loadDemo();
  const B = bridge(), got = [], tick = () => new Promise(r => setImmediate(r));
  const win = { frameElement: { isConnected: true } };
  B.attach(win, m => got.push(j(m)));
  let n = 0;
  const send = async (name, a) => { B.postMessage({ v: 1, id: ++n, type: 'cmd', name, args: a }, win); for (let i = 0; i < 40; i++) await tick(); };
  const last = () => got.filter(m => m.type === 'state').at(-1).state;
  await run('autoDetect()', 't');
  B.postMessage({ v: 1, type: 'ready', view: 'changes', id: null }, win); await tick();
  await send('refresh', {});
  return { run, send, last, ok: () => assert.deepEqual(validate(contract('changes-state.schema.json'), last()), []) };
}

test('demo: indicatore verde alla HEAD, arancio con "un collega ha committato", di nuovo verde dopo SVN Update', async () => {
  const d = await demo(); d.ok();
  assert.equal(d.last().workingCopy.revState, 'current');
  await d.run("S.env.colleague = true; S.headRev = S.wcRev + 1", 't');
  await d.send('refresh', {}); d.ok();
  assert.equal(d.last().workingCopy.revState, 'behind'); assert.ok(d.last().workingCopy.head > d.last().workingCopy.rev);
  await d.run('modal = async () => true', 't');
  await d.send('update', {}); d.ok();
  assert.equal(d.last().workingCopy.revState, 'current');
  assert.ok(d.last().status.seq > 0, 'ogni messaggio ha il suo seq');
});

test('demo: Recenti dopo il commit (max 25, duplicati in cima) e useRecentMessage inserisce il testo', async () => {
  const d = await demo();
  assert.ok(d.last().commit.recent.length >= 3);
  await d.run('modal = async () => true', 't');
  await d.send('stage', { key: d.last().sections.flatMap(s => s.rows).find(r => r.kind === 'file' && r.status === 'M').key });
  await d.send('setCommitText', { text: 'Corregge il totale' });
  await d.send('commit', {}); d.ok();
  assert.equal(d.last().commit.recent[0], 'Corregge il totale');
  const rec = d.last().commit.recent;
  await d.send('useRecentMessage', { i: 2 });
  assert.equal(d.last().commit.text, rec[2]);
});

test('demo: doppio clic su un file non versionato lo apre intero (nessuna scheda di diff)', async () => {
  const d = await demo();
  const un = d.last().sections.flatMap(s => s.rows).find(r => r.kind === 'file' && r.status === '?');
  assert.ok(un && un.can.diff, 'il non versionato ha l\'azione di apertura');
  await d.send('showDiff', { key: un.key }); d.ok();
  assert.match(d.last().status.msg, /^File aperto: /);
  assert.deepEqual(j(d.run('S.ui.diffs', 't')), [], 'nessuna scheda di diff');
});
