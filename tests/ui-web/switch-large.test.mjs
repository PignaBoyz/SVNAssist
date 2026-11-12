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
