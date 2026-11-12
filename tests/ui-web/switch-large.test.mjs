// SVN Switch / Merge su repository grandi: selettore veloce (ultimi usati, ricerca, al massimo 50 righe), vista estesa (filtri, ordine), lista virtuale.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { contract, readJson, validate, loadUi, loadDemo } from './helpers.mjs';

const U = loadUi(['i18n', 'util', 'diff', 'tree', 'prereq', 'changes']);

// 500 voci finte: trunk, 460 branch e 39 tag con revisione, autore e data
const big = (() => {
  const day = 864e5, t0 = Date.UTC(2026, 0, 1), items = [{ name: 'trunk', kind: 'trunk', current: false, rev: 1000, author: 'mrossi', date: new Date(t0 + 400 * day).toISOString() }];
  for (let i = 1; i <= 460; i++) items.push({ name: `branches/${i % 2 ? 'feature' : 'bugfix'}-${i % 7 ? 'Area' : 'checkout'}-${i}`, kind: 'branch', current: i === 3, rev: 100 + i, author: ['mrossi', 'lbianchi'][i % 2], date: new Date(t0 + ((i * 37) % 400) * day).toISOString() });
  for (let i = 1; i <= 39; i++) items.push({ name: `tags/v${i}.0`, kind: 'tag', current: false, rev: 50 + i, author: 'mrossi', date: new Date(t0 + i * day).toISOString() });
  return items;
})();
const SW = o => Object.assign({ open: true, mode: 'switch', loading: false, error: '', items: big, recent: [] }, o);

test('500 voci: filtro per nome (contiene, senza maiuscole), per tipo, e i due insieme', () => {
  assert.equal(big.length, 500);
  assert.equal(U.swFilter(big, {}).length, 500);
  const q = U.swFilter(big, { q: 'CHECKOUT' });
  assert.ok(q.length > 50 && q.every(i => i.name.toLowerCase().includes('checkout')));
  assert.equal(U.swFilter(big, { q: '  ' }).length, 500, 'solo spazi = nessun filtro');
  assert.equal(U.swFilter(big, { kind: 'tag' }).length, 39);
  assert.deepEqual(U.swFilter(big, { kind: 'trunk' }).map(i => i.name), ['trunk']);
  assert.deepEqual(U.swFilter(big, { q: 'v1', kind: 'tag' }).map(i => i.name).sort(), ['tags/v1.0', 'tags/v10.0', 'tags/v11.0', 'tags/v12.0', 'tags/v13.0', 'tags/v14.0', 'tags/v15.0', 'tags/v16.0', 'tags/v17.0', 'tags/v18.0', 'tags/v19.0']);
  assert.deepEqual(U.swFilter(big, { q: 'nonesiste' }), []);
});

test('500 voci: ordine per nome (numerico, ignora le maiuscole), per data (piu recente prima) e ordine del server', () => {
  assert.deepEqual(U.swFilter(big, {}).map(i => i.name), big.map(i => i.name), 'senza sort resta l\'ordine del server');
  const byName = U.swFilter(big, { sort: 'name' }).map(i => i.name);
  assert.deepEqual(byName.slice(0, 3), ['branches/bugfix-Area-2', 'branches/bugfix-Area-4', 'branches/bugfix-Area-6']);
  assert.ok(byName.indexOf('tags/v2.0') < byName.indexOf('tags/v10.0'), 'v2 prima di v10');
  assert.deepEqual(U.swFilter(big, { sort: 'name', desc: true }).map(i => i.name), byName.slice().reverse());
  const d = U.swFilter(big, { sort: 'date', desc: true });
  assert.equal(d[0].name, 'trunk');
  for (let i = 1; i < d.length; i++) assert.ok(Date.parse(d[i - 1].date) >= Date.parse(d[i].date), 'data decrescente alla riga ' + i);
  const a = U.swFilter(big, { sort: 'date' });
  for (let i = 1; i < a.length; i++) assert.ok(Date.parse(a[i - 1].date) <= Date.parse(a[i].date));
  assert.equal(big[1].name, 'branches/feature-Area-1', 'l\'elenco in ingresso non viene toccato');
});

test('vista estesa: filtro per autore (uguale, senza maiuscole), per intervallo di date (estremi inclusi), combinati con nome e tipo', () => {
  const mk = (name, kind, author, date, rev) => ({ name, kind, current: false, rev, author, date: date && new Date(date).toISOString() });
  const L = [mk('trunk', 'trunk', 'mrossi', '2026-03-10T12:00:00Z', 90), mk('branches/a', 'branch', 'lbianchi', '2026-03-01T12:00:00Z', 70), mk('branches/b', 'branch', 'MRossi', '2026-03-05T12:00:00Z', 80),
    mk('tags/v1', 'tag', 'gverdi', '2026-02-20T12:00:00Z', 5), mk('tags/v2', 'tag', '', '', 0)];
  const names = o => U.swFilter(L, o).map(i => i.name);
  assert.deepEqual(names({ author: 'mrossi' }), ['trunk', 'branches/b']); assert.deepEqual(names({ author: ' LBIANCHI ' }), ['branches/a']); assert.deepEqual(names({ author: 'nessuno' }), []);
  assert.deepEqual(names({ from: '2026-03-01' }), ['trunk', 'branches/a', 'branches/b'], 'da incluso; senza data = escluso');
  assert.deepEqual(names({ to: '2026-03-05' }), ['branches/a', 'branches/b', 'tags/v1'], 'a incluso (tutto il giorno)');
  assert.deepEqual(names({ from: '2026-03-02', to: '2026-03-05' }), ['branches/b']);
  assert.deepEqual(names({ from: '2026-03-10', to: '2026-03-01' }), [], 'intervallo invertito: nessun risultato, nessun errore');
  assert.deepEqual(names({ author: 'mrossi', from: '2026-03-06' }), ['trunk']); assert.deepEqual(names({ kind: 'branch', author: 'mrossi' }), ['branches/b']); assert.deepEqual(names({ q: 'tags', to: '2026-12-31' }), ['tags/v1']);
  // ordinamenti per autore e per revisione (numerico), a parita' per nome
  assert.deepEqual(names({ sort: 'author' }), ['tags/v2', 'tags/v1', 'branches/a', 'branches/b', 'trunk'], 'senza autore, gverdi, lbianchi, mrossi/MRossi (stesso autore: per nome)');
  assert.deepEqual(names({ sort: 'author', desc: true }), ['branches/b', 'trunk', 'branches/a', 'tags/v1', 'tags/v2']);
  assert.deepEqual(names({ sort: 'rev' }), ['tags/v2', 'tags/v1', 'branches/a', 'branches/b', 'trunk']); assert.deepEqual(names({ sort: 'rev', desc: true }), ['trunk', 'branches/b', 'branches/a', 'tags/v1', 'tags/v2']);
  assert.deepEqual([...U.swAuthors(L)], ['gverdi', 'lbianchi', 'mrossi'], 'autori distinti (senza maiuscole), senza vuoti');
  // preset "ultimi N giorni": data locale di N giorni prima
  const now = new Date(2026, 5, 15, 12).getTime();
  assert.equal(U.swPreset(7, now), '2026-06-08'); assert.equal(U.swPreset(30, now), '2026-05-16'); assert.equal(U.swPreset(90, now), '2026-03-17');
  // sui 500 voci: i filtri combinati restano coerenti con la lista completa
  const au = U.swFilter(big, { author: 'lbianchi' }); assert.ok(au.length > 200 && au.every(i => i.author === 'lbianchi'));
  const win = U.swFilter(big, { from: '2026-02-01', to: '2026-02-28' }); assert.ok(win.length > 0 && win.every(i => { const d = new Date(i.date); return d >= new Date(2026, 1, 1) && d < new Date(2026, 2, 1); }));
});

test('vista estesa: filtri autore/periodo/date, "Azzera filtri" (attivo solo con filtri), contatore, intestazioni ordinabili per autore e rev', () => {
  const none = U.changesHTML.switchHTML(SW(), { ext: true, sel: '' });
  assert.match(none, /id="sw-a"[\s\S]*Tutti gli autori[\s\S]*<option value="lbianchi">lbianchi<\/option>[\s\S]*<option value="mrossi">mrossi<\/option>/);
  assert.match(none, /id="sw-p"[\s\S]*Qualsiasi data[\s\S]*Ultimi 7 giorni[\s\S]*Ultimi 30 giorni[\s\S]*Ultimi 90 giorni/);
  assert.match(none, /id="sw-df" type="date" value=""/); assert.match(none, /id="sw-dt" type="date" value=""/);
  assert.match(none, /data-sw-reset disabled/, 'senza filtri il pulsante e spento'); assert.match(none, /500 di 500/);
  for (const k of ['name', 'rev', 'author', 'date']) assert.match(none, new RegExp(`data-sw-sort="${k}"`));
  const on = U.changesHTML.switchHTML(SW(), { ext: true, sel: '', author: 'mrossi', from: '2026-02-01', to: '2026-02-28', preset: '' });
  assert.match(on, /<option value="mrossi" selected>/); assert.match(on, /id="sw-df" type="date" value="2026-02-01"/); assert.match(on, /id="sw-dt" type="date" value="2026-02-28"/);
  assert.match(on, /data-sw-reset\s+title/); assert.doesNotMatch(on, /data-sw-reset disabled/); assert.match(on, /Azzera filtri/);
  assert.match(on, new RegExp(`${U.swFilter(big, { author: 'mrossi', from: '2026-02-01', to: '2026-02-28' }).length} di 500`));
  const srt = U.changesHTML.switchHTML(SW(), { ext: true, sort: 'author', desc: false });
  assert.match(srt, /data-sw-sort="author"[^>]*aria-sort="ascending"/);
  U.setLang('en');
  try { const e = U.changesHTML.switchHTML(SW(), { ext: true }); for (const l of ['All authors', 'Any date', 'Last 30 days', 'Clear filters', 'From', 'To']) assert.match(e, new RegExp(l)); } finally { U.setLang('it'); }
});

test('500 voci: filtro e ordine veloci (nessun blocco della UI)', () => {
  const t = performance.now();
  for (let i = 0; i < 200; i++) U.swFilter(big, { q: 'feature', kind: 'branch', sort: i % 2 ? 'date' : 'name', desc: !!(i % 3) });
  assert.ok(performance.now() - t < 1500, 'troppo lento: ' + (performance.now() - t) + ' ms per 200 filtri');
});

test('selettore veloce: al massimo 50 righe, ultimi usati in cima solo senza ricerca, nota "N di M" e vista estesa', () => {
  const q0 = U.swQuick(big, ['tags/v7.0', 'branches/feature-Area-1', 'non-esiste'], '', 50);
  assert.deepEqual(q0.recent.map(i => i.name), ['tags/v7.0', 'branches/feature-Area-1'], 'solo i recenti ancora in elenco');
  assert.equal(q0.recent.length + q0.rest.length, 50); assert.equal(q0.total, 500); assert.equal(q0.more, 450);
  assert.ok(!q0.rest.some(i => q0.recent.includes(i)), 'un recente non e ripetuto');
  const q1 = U.swQuick(big, ['tags/v7.0'], 'v7', 50);
  assert.equal(q1.recent.length, 0, 'con una ricerca niente gruppo "usati di recente"'); assert.ok(q1.rest.some(i => i.name === 'tags/v7.0'));
  assert.equal(U.swQuick(big, [], 'zzz', 50).total, 0);
  const h = U.changesHTML.switchHTML(SW({ recent: ['tags/v7.0'] }), { q: '', ext: false });
  assert.equal([...h.matchAll(/data-sw="/g)].length, 50, 'il DOM del selettore veloce ha al massimo 50 righe');
  assert.match(h, /Usati di recente/); assert.match(h, /Tutti i branch e i tag/); assert.match(h, /Mostrati 50 di 500: scrivi per filtrare oppure apri la vista estesa/);
  assert.match(h, /data-sw-ext[\s\S]*Mostra tutti \/ vista estesa…/);
  const f = U.changesHTML.switchHTML(SW({ recent: ['tags/v7.0'] }), { q: 'v7', ext: false });
  assert.doesNotMatch(f, /Usati di recente/); assert.doesNotMatch(f, /Mostrati/, 'meno di 50 risultati: nessuna nota');
});

test('vista estesa: intestazioni ordinabili, filtro per tipo, contatore; le righe non sono nel markup (lista virtuale)', () => {
  const h = U.changesHTML.switchHTML(SW(), { ext: true, q: 'checkout', kind: 'branch', sort: 'date', desc: true, sel: '' });
  assert.match(h, /sw-wide/); assert.match(h, /id="sw-k"/); assert.match(h, /<option value="tag">tag<\/option>/); assert.match(h, /<option value="branch" selected>/);
  assert.match(h, /data-sw-sort="name"[^>]*aria-sort="none"/); assert.match(h, /data-sw-sort="date"[^>]*aria-sort="descending"/);
  assert.equal([...h.matchAll(/data-sw="/g)].length, 0, 'il markup non contiene righe: le disegna swFill nella finestra visibile');
  assert.match(h, /id="sw-sp"/); assert.match(h, /\d+ di 500/); assert.match(h, /Vista rapida/);
  assert.match(U.changesHTML.switchHTML(SW(), { ext: true, q: 'zzz' }), /Nessun elemento corrisponde/);
  assert.match(U.changesHTML.switchHTML(SW(), { ext: true, q: 'a' }).replace(/\n/g, ''), /Nome[\s\S]*Tipo[\s\S]*Rev\.[\s\S]*Autore[\s\S]*Data/);
});

test('lista virtuale: finestra visibile + margine, righe disegnate sempre poche anche con 500 voci', () => {
  assert.deepEqual({ ...U.swWindow(500, 0, 380, 28, 0) }, { start: 0, end: 14 });
  assert.deepEqual({ ...U.swWindow(500, 28 * 100, 380, 28, 6) }, { start: 94, end: 120 });
  assert.deepEqual({ ...U.swWindow(500, 28 * 495, 380, 28, 6) }, { start: 489, end: 500 }, 'in fondo non va oltre la fine');
  assert.deepEqual({ ...U.swWindow(0, 0, 380, 28) }, { start: 0, end: 0 });
  const list = U.swFilter(big, { sort: 'name' }), w = U.swWindow(list.length, 28 * 200, 380, 28);
  const rows = U.swRowsHTML(list, w.start, w.end, list[205].name);
  const n = [...rows.matchAll(/data-sw="/g)].length;
  assert.equal(n, w.end - w.start); assert.ok(n < 30, 'righe nel DOM: ' + n);
  assert.match(rows, new RegExp(`id="sw-o${w.start}"`)); assert.match(rows, /aria-setsize="500"/); assert.match(rows, /aria-selected="true"/);
  assert.match(rows, /<span class="rvn muted">r\d+<\/span>/); assert.match(rows, /<span class="au" title="[^"]+">/);
  assert.doesNotMatch(U.swRowsHTML([{ name: '<img src=x>', kind: 'branch', author: '<b>', rev: 1, date: '' }], 0, 1, ''), /<img|<b>/, 'nomi neutralizzati');
});

test('selettore in modo merge: lo stesso selettore, titolo e pulsante di merge anche in vista estesa', () => {
  const h = U.changesHTML.switchHTML(SW({ mode: 'merge', items: big.filter(i => !i.current) }), { ext: true });
  assert.match(h, /SVN Merge/); assert.match(h, /Merge…/); assert.match(h, /da cui unire le modifiche/);
});

test('schema: switchPicker con rev/author/date e recent validi; rev negativa rifiutata', () => {
  const s = structuredClone(readJson('src/SVNAssist.UI.Web/contract/fixtures/changes.json')), S = contract('changes-state.schema.json');
  s.switchPicker = SW({ recent: ['trunk'] });
  assert.deepEqual(validate(S, s), []);
  s.switchPicker.items = [{ ...big[0], rev: -1 }]; assert.notDeepEqual(validate(S, s), []);
});

test('mock: la demo ha 200+ branch con revisione, autore e data; gli ultimi usati si ricordano', async () => {
  const { run, bridge } = await loadDemo();
  const B = bridge(), got = [], win = { frameElement: { isConnected: true } }, tick = () => new Promise(r => setImmediate(r));
  B.attach(win, m => got.push(JSON.parse(JSON.stringify(m))));
  const send = async (name, a) => { B.postMessage({ v: 1, id: send.n = (send.n || 0) + 1, type: 'cmd', name, args: a }, win); for (let i = 0; i < 30; i++) await tick(); };
  const last = () => got.filter(m => m.type === 'state').at(-1).state;
  await run('autoDetect()', 't');
  B.postMessage({ v: 1, type: 'ready', view: 'changes', id: null }, win); await tick();
  await send('refresh', {});
  await send('switchOpen', {});
  const p = last().switchPicker;
  assert.deepEqual(validate(contract('changes-state.schema.json'), last()), []);
  assert.ok(p.items.filter(i => i.kind === 'branch').length >= 200, 'branch: ' + p.items.length);
  assert.ok(p.items.every(i => i.rev > 0 && i.author && i.date), 'ogni voce ha revisione, autore e data');
  assert.deepEqual(p.recent, []);
  await send('mergeOpen', {});
  await send('switchOk', { name: 'branches/feature-coupon' });
  await send('mergeClose', {});
  await send('switchOpen', {});
  assert.deepEqual(last().switchPicker.recent, ['branches/feature-coupon']);
});
