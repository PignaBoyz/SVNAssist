// SVN Switch: ramo corrente come pulsante, selettore modale (render puro), comandi nello schema, flusso nel mock della demo.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { contract, readJson, validate, loadUi, loadDemo } from './helpers.mjs';

const U = loadUi(['i18n', 'util', 'diff', 'tree', 'prereq', 'changes']);
const H = U.changesHTML;
const fx = () => structuredClone(readJson('src/SVNAssist.UI.Web/contract/fixtures/changes.json'));
const SW = o => Object.assign({
  open: true, loading: false, error: '',
  items: [{ name: 'trunk', kind: 'trunk', current: true }, { name: 'branches/feature-x', kind: 'branch', current: false }, { name: 'tags/1.0', kind: 'tag', current: false }],
}, o);

test('header: il ramo corrente e un pulsante che invia switchOpen', () => {
  const s = fx(); s.workingCopy.branch = 'branches/feature-x';
  const h = H.header(s);
  assert.match(h, /<button[^>]*class="branch"[^>]*data-cmd="switchOpen"[\s\S]*?branches\/feature-x/);
  assert.match(h, /SVN Switch…: cambia branch o tag \(ramo attuale: branches\/feature-x\)/);
});

test('selettore: chiuso = niente markup; aperto = dialogo accessibile con ricerca, elenco e Switch disabilitato', () => {
  assert.equal(H.switchHTML(SW({ open: false })), '');
  const h = H.switchHTML(SW());
  assert.match(h, /role="dialog"[^>]*aria-modal="true"/); assert.match(h, /role="listbox"/); assert.match(h, /id="sw-q"/);
  assert.deepEqual([...h.matchAll(/data-sw="([^"]+)"/g)].map(m => m[1]), ['trunk', 'branches/feature-x', 'tags/1.0']);
  assert.match(h, /\(corrente\)/);
  assert.match(h, /data-sw-ok disabled/);
  assert.match(h, /data-cmd="switchRefresh"/); assert.match(h, /data-cmd="switchCancel"/);
});

test('selettore: caricamento, elenco vuoto (suggerisce il layout) ed errore', () => {
  assert.match(H.switchHTML(SW({ loading: true, items: [] })), /Caricamento di branch e tag/);
  assert.match(H.switchHTML(SW({ items: [] })), /Controlla il layout del repository/);
  const e = H.switchHTML(SW({ error: 'La working copy è già su trunk.' }));
  assert.match(e, /role="alert"[\s\S]*già su trunk/);
});

test('selettore: nomi con HTML neutralizzati; in inglese testi tradotti', () => {
  const h = H.switchHTML(SW({ items: [{ name: 'branches/<img src=x>', kind: 'branch', current: false }] }));
  assert.doesNotMatch(h, /<img/);
  U.setLang('en');
  try { assert.match(H.switchHTML(SW()), /Choose the branch or tag to switch the working copy to/); } finally { U.setLang('it'); }
});

test('comandi: switchOpen/Refresh/Cancel/Ok nello schema; switchOk richiede name', () => {
  const S = contract('commands.schema.json');
  for (const [name, args] of [['switchOpen', {}], ['switchRefresh', {}], ['switchCancel', {}], ['switchOk', { name: 'tags/1.0' }]])
    assert.deepEqual(validate(S, { name, args }), [], name);
  assert.notDeepEqual(validate(S, { name: 'switchOk', args: {} }), []);
  assert.notDeepEqual(validate(S, { name: 'switchOk', args: { name: 'x', url: 'file:///x' } }), []);
});

test('schema: switchPicker e svnVersion validi; kind sconosciuto rifiutato', () => {
  const s = fx(), S = contract('changes-state.schema.json');
  s.workingCopy.svnVersion = '1.14.2'; s.switchPicker = SW();
  assert.deepEqual(validate(S, s), []);
  s.switchPicker.items[0].kind = 'folder';
  assert.notDeepEqual(validate(S, s), []);
});

test('mock: apri, elenco con il corrente, errore sul corrente, switch, stato e ramo aggiornati', async () => {
  const { run, bridge } = await loadDemo();
  const B = bridge(), got = [], win = { frameElement: { isConnected: true } }, tick = () => new Promise(r => setImmediate(r));
  B.attach(win, m => got.push(JSON.parse(JSON.stringify(m))));
  const send = async (name, args) => { B.postMessage({ v: 1, id: send.n = (send.n || 0) + 1, type: 'cmd', name, args }, win); for (let i = 0; i < 20; i++) await tick(); };
  const last = () => got.filter(m => m.type === 'state').at(-1).state;
  const ok = () => assert.deepEqual(validate(contract('changes-state.schema.json'), last()), []);
  await run('autoDetect()', 't');
  B.postMessage({ v: 1, type: 'ready', view: 'changes', id: null }, win); await tick();
  await run("S.files.forEach(f => { f.st = 'N'; })", 't');   // working copy pulita: lo switch non chiede conferma (il dialogo non esiste senza DOM)
  await send('refresh', {}); ok();
  assert.equal(last().workingCopy.branch, 'trunk');

  await send('switchOpen', {}); ok();
  assert.equal(last().switchPicker.open, true);
  assert.deepEqual(last().switchPicker.items.filter(i => i.current).map(i => i.name), ['trunk']);
  await send('switchOk', { name: 'trunk' }); ok();
  assert.match(last().switchPicker.error, /già su trunk/);
  await send('switchOk', { name: 'branches/inesistente' }); ok();
  assert.match(last().switchPicker.error, /non valido/);
  await send('switchCancel', {}); assert.equal(last().switchPicker.open, false);

  await send('switchOpen', {});
  await send('switchOk', { name: 'branches/feature-coupon' }); ok();
  assert.equal(last().switchPicker.open, false);
  assert.equal(last().workingCopy.branch, 'branches/feature-coupon');
  assert.match(last().status.msg, /Switch a branches\/feature-coupon completato.*ricarica la solution/);
  await send('switchOpen', {});
  assert.deepEqual(last().switchPicker.items.filter(i => i.current).map(i => i.name), ['branches/feature-coupon']);
});
