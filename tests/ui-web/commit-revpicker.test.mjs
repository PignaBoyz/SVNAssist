// Flusso commit come Git (Commit All / Commit Staged), pulsante Show Log, barra di stato su due righe,
// scelta guidata della revisione (Update to revision): render puro + flusso nel mock della demo.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { contract, readJson, validate, loadUi, loadDemo, read } from './helpers.mjs';

const U = loadUi(['i18n', 'util', 'diff', 'tree', 'prereq', 'changes']);
const H = U.changesHTML;
const fx = () => structuredClone(readJson('src/SVNAssist.UI.Web/contract/fixtures/changes.json'));
const commitBtn = h => h.match(/<button[^>]*data-cmd="commit"[^>]*>[\s\S]*?<\/button>/)[0];

test('Staged con file: "Commit Staged" con il numero, abilitato', () => {
  const s = fx(); s.commit.count = 3; s.commit.all = false;
  const b = commitBtn(H.commitBar(s));
  assert.match(b, /Commit Staged/); assert.match(b, /class="pill">3</); assert.doesNotMatch(b, /disabled/);
  assert.match(b, /3 file in Staged/);
});

test('Staged vuoto: "Commit All" con il numero delle modifiche versionate', () => {
  const s = fx(); s.commit.count = 5; s.commit.all = true;
  const b = commitBtn(H.commitBar(s));
  assert.match(b, /Commit All/); assert.match(b, /class="pill">5</); assert.doesNotMatch(b, /disabled/);
  assert.match(b, /ti chiede se aggiungerli/);
});

test('niente da committare: pulsante disabilitato; durante il caricamento pure', () => {
  const s = fx(); s.commit.count = 0; s.commit.all = false;
  assert.match(commitBtn(H.commitBar(s)), /disabled/);
  s.commit.count = 2; s.loading = true;
  assert.match(commitBtn(H.commitBar(s)), /disabled/);
});

test('in inglese le etichette dei tooltip sono tradotte', () => {
  const s = fx(); s.commit.count = 5; s.commit.all = true;
  U.setLang('en');
  try { assert.match(commitBtn(H.commitBar(s)), /Staged is empty: commits all 5 changes/); } finally { U.setLang('it'); }
});

test('header: Show Log accanto a SVN Update, invia showLog', () => {
  const h = H.header(fx());
  assert.match(h, /data-cmd="update"[\s\S]*data-cmd="showLog"/); assert.match(h, /Show Log/);
});

test('barra di stato: messaggio su una riga a tutta larghezza con tooltip, prerequisiti sulla riga sotto', () => {
  const s = fx(); s.status = { msg: 'Commit completato: revisione 4 (2 elementi).', err: false };
  const h = H.foot(s);
  const r1 = h.slice(h.indexOf('foot-r1'), h.indexOf('foot-r2')), r2 = h.slice(h.indexOf('foot-r2'));
  assert.match(r1, /class="msg ok"[^>]*title="Commit completato: revisione 4/);
  assert.doesNotMatch(r1, /prq-st/); assert.match(r2, /prq-st/);
  const css = read('src/SVNAssist.UI.Web/css/panel.css');
  assert.match(css, /\.foot\{[^}]*flex-direction:column/); assert.match(css, /\.foot \.msg>span\{[^}]*text-overflow:ellipsis/);
});

const REV = o => Object.assign({ open: true, target: 'C:\\wc', loading: false, error: '', canMore: false, items: [] }, o);

test('revisioni: chiusa = niente markup; aperta = dialogo con HEAD in cima e le revisioni (numero, autore, data, prima riga)', () => {
  assert.equal(H.revHTML(REV({ open: false })), '');
  const h = H.revHTML(REV({ items: [{ rev: 7, author: 'mrossi', date: '2026-11-10T09:30:00.0000000+00:00', msg: 'Correzione carrello' }, { rev: 6, author: 'lbianchi', date: '2026-11-09T08:00:00Z', msg: 'Prima versione' }] }));
  assert.match(h, /role="dialog"[^>]*aria-modal="true"/); assert.match(h, /role="listbox"/);
  const rows = [...h.matchAll(/data-rev="([^"]+)"/g)].map(m => m[1]);
  assert.deepEqual(rows, ['HEAD', '7', '6']);
  assert.match(h, /class="rp-row sel"[^>]*data-rev="HEAD"/, 'HEAD e\' preselezionato');
  assert.match(h, /r7[\s\S]*mrossi[\s\S]*Correzione carrello/);
  assert.match(h, /id="rp-num"/); assert.match(h, /data-rp="ok"/); assert.match(h, /data-cmd="revPickerCancel"/);
});

test('revisioni: "Carica altre" solo se ce ne sono altre, caricamento ed errore visibili, testo escapato', () => {
  assert.doesNotMatch(H.revHTML(REV()), /revPickerMore/);
  assert.match(H.revHTML(REV({ canMore: true })), /data-cmd="revPickerMore"/);
  assert.match(H.revHTML(REV({ loading: true })), /Caricamento delle revisioni/);
  assert.match(H.revHTML(REV({ error: '"x" non è una revisione valida' })), /role="alert"/);
  const h = H.revHTML(REV({ items: [{ rev: 3, author: '<b>x</b>', date: '2026-01-01T00:00:00Z', msg: '<img src=x>' }] }));
  assert.doesNotMatch(h, /<img/); assert.doesNotMatch(h, /<b>x<\/b>/);
});

test('i comandi della scelta revisione e showLog rispettano commands.schema.json', () => {
  const S = contract('commands.schema.json');
  for (const [name, args] of [['updateToRevision', {}], ['revPickerMore', {}], ['revPickerCancel', {}], ['showLog', {}], ['revPickerOk', { rev: 'HEAD' }]])
    assert.deepEqual(validate(S, { name, args }), [], name);
  assert.notDeepEqual(validate(S, { name: 'revPickerOk', args: {} }), []);
});

test('mock: Update to revision apre la modale, valida il numero, esegue svn update -r', async () => {
  const { run, bridge } = await loadDemo();
  const B = bridge(), got = [], win = { frameElement: { isConnected: true } }, tick = () => new Promise(r => setImmediate(r));
  B.attach(win, m => got.push(JSON.parse(JSON.stringify(m))));
  const send = async (name, args) => { B.postMessage({ v: 1, id: send.n = (send.n || 0) + 1, type: 'cmd', name, args }, win); for (let i = 0; i < 20; i++) await tick(); };
  const last = () => got.filter(m => m.type === 'state').at(-1).state;
  const ok = () => assert.deepEqual(validate(contract('changes-state.schema.json'), last()), []);
  await run('autoDetect()', 't');
  B.postMessage({ v: 1, type: 'ready', view: 'changes', id: null }, win); await tick();

  await send('updateToRevision', {}); ok();
  assert.equal(last().revPicker.open, true); assert.ok(last().revPicker.items.length >= 20); assert.equal(last().revPicker.canMore, true);
  const n = last().revPicker.items.length;
  await send('revPickerMore', {}); ok(); assert.ok(last().revPicker.items.length > n);
  await send('revPickerOk', { rev: 'boh' }); ok();
  assert.equal(last().revPicker.open, true); assert.match(last().revPicker.error, /non è una revisione valida/);
  const target = last().revPicker.items[3].rev;
  await send('revPickerOk', { rev: String(target) }); ok();
  assert.equal(last().revPicker.open, false); assert.equal(last().workingCopy.rev, target);
  assert.match(last().status.msg, new RegExp('revisione ' + target));
  await send('updateToRevision', {}); await send('revPickerCancel', {}); assert.equal(last().revPicker.open, false);
});

test('mock: Commit All con Staged vuoto prende le modifiche versionate, non i non versionati', async () => {
  const { run, bridge } = await loadDemo();
  const B = bridge(), got = [], win = { frameElement: { isConnected: true } }, tick = () => new Promise(r => setImmediate(r));
  B.attach(win, m => got.push(JSON.parse(JSON.stringify(m))));
  const send = async (name, args) => { B.postMessage({ v: 1, id: send.n = (send.n || 0) + 1, type: 'cmd', name, args }, win); for (let i = 0; i < 20; i++) await tick(); };
  const last = () => got.filter(m => m.type === 'state').at(-1).state;
  await run('autoDetect()', 't');
  B.postMessage({ v: 1, type: 'ready', view: 'changes', id: null }, win); await tick();
  await run("S.vm.files.forEach(f => { f.sel = false; })", 't');
  await send('refresh', {});
  const versioned = JSON.parse(JSON.stringify(run("S.vm.files.filter(f => ['M','A','D','R','!','C'].includes(f.st)).length", 't')));
  assert.ok(versioned > 0);
  assert.equal(last().commit.all, true); assert.equal(last().commit.count, versioned);
  await run("S.vm.files[0].sel = true", 't'); await send('refresh', {});
  assert.equal(last().commit.all, false); assert.equal(last().commit.count, 1);
});

test('Commit All con non versionati: la domanda elenca i file e offre Sì / No / Annulla', () => {
  const h = loadUi(['i18n', 'util', 'ops']).ops.commitWarnHTML({ count: 12, items: ['a.txt', 'b/c.txt'], more: 10 });
  for (const c of ['commitWarnAdd', 'commitWarnNo', 'commitWarnCancel']) assert.ok(h.includes(`data-cmd="${c}"`), c);
  assert.match(h, /12 file non versionati/); assert.match(h, /b\/c\.txt/); assert.match(h, /e altri 10/);
});

test('mock: Commit All con non versionati chiede prima; Annulla e No non aggiungono nulla', async () => {
  const { run, bridge } = await loadDemo();
  const B = bridge(), got = [], win = { frameElement: { isConnected: true } }, tick = () => new Promise(r => setImmediate(r));
  B.attach(win, m => got.push(JSON.parse(JSON.stringify(m))));
  const send = async (name, args) => { B.postMessage({ v: 1, id: send.n = (send.n || 0) + 1, type: 'cmd', name, args }, win); for (let i = 0; i < 20; i++) await tick(); };
  const last = () => got.filter(m => m.type === 'state').at(-1).state;
  await run('autoDetect()', 't');
  B.postMessage({ v: 1, type: 'ready', view: 'changes', id: null }, win); await tick();
  await run("S.vm.files.forEach(f => { f.sel = false; }); S.vm.commitMsg = 'prova'", 't');
  await send('refresh', {});
  const unv = () => JSON.parse(JSON.stringify(run("S.vm.files.filter(f => f.st === '?').length", 't')));
  const n = unv();
  assert.ok(n > 0, 'la demo ha file non versionati');
  await send('commit', {});
  assert.equal(last().commitWarn.count, n); assert.ok(last().commitWarn.items.length <= 10);
  await send('commitWarnCancel', {}); assert.equal(last().commitWarn, undefined); assert.equal(unv(), n);
  await send('commit', {}); await send('commitWarnNo', {});
  assert.equal(last().commitWarn, undefined); assert.equal(unv(), n, 'No: i non versionati restano fuori');
  await run("S.vm.files = S.vm.files.filter(f => f.st !== 'C'); S.vm.commitMsg = 'ancora'", 't'); await send('commit', {}); await send('commitWarnAdd', {});
  assert.equal(unv(), 0, 'Sì: i non versionati sono stati aggiunti e committati'); assert.match(last().status.msg, /Commit completato/);
});

