// Selezione multipla nella finestra Changes: Ctrl/Shift, ridisegno dello stato, menu con conteggio, schema dei comandi con keys.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readJson, contract, validate, loadUi } from './helpers.mjs';

const U = loadUi(['i18n', 'util', 'diff', 'tree', 'prereq', 'changes']);
const st = readJson('src/SVNAssist.UI.Web/contract/fixtures/changes.json');

// "DOM" minimo: righe con dataset/attributi, lista con querySelectorAll
const mkRow = key => { const a = {}; return { dataset: { key, kind: key.startsWith('section:') ? 'section' : 'file' }, tabIndex: -1, setAttribute: (k, v) => { a[k] = v; }, getAttribute: k => a[k], focus() { }, scrollIntoView() { } }; };
const mkList = keys => { const rows = keys.map(mkRow); return { rows, __sel: new Set(), querySelectorAll: () => rows }; };
const KEYS = ['section:staged', 'staged:a', 'staged:b', 'staged:c', 'section:changes', 'changes:d', 'changes:e'];
const J = v => JSON.parse(JSON.stringify(v));
const rowOf = (l, k) => l.rows.find(r => r.dataset.key === k);

test('selezione: clic seleziona uno, Ctrl alterna, Shift fa l\'intervallo anche tra sezioni (sezioni escluse)', () => {
  const l = mkList(KEYS);
  U.focusRow(l, rowOf(l, 'staged:b'), 'one');
  assert.deepEqual(J(U.selection(l)), ['staged:b']);
  U.focusRow(l, rowOf(l, 'changes:e'), 'add');
  assert.deepEqual(J(U.selection(l)), ['staged:b', 'changes:e']);
  U.focusRow(l, rowOf(l, 'staged:b'), 'add');   // toglie
  assert.deepEqual(J(U.selection(l)), ['changes:e']);
  U.focusRow(l, rowOf(l, 'staged:a'), 'one');
  U.focusRow(l, rowOf(l, 'changes:d'), 'range');
  assert.deepEqual(J(U.selection(l)), ['staged:a', 'staged:b', 'staged:c', 'changes:d']);
  U.focusRow(l, rowOf(l, 'staged:c'), 'range');   // l'ancora resta staged:a
  assert.deepEqual(J(U.selection(l)), ['staged:a', 'staged:b', 'staged:c']);
  assert.equal(rowOf(l, 'staged:b').getAttribute('aria-selected'), 'true');
  assert.equal(rowOf(l, 'changes:d').getAttribute('aria-selected'), 'false');
  U.focusRow(l, rowOf(l, 'section:changes'), 'one');   // clic su una sezione: nessuna selezione di righe
  assert.deepEqual(J(U.selection(l)), []);
});

test('selezione: dopo lo snapshot si perdono le chiavi sparite e si segue il file che cambia sezione', () => {
  const l = mkList(KEYS);
  l.__sel = new Set(['staged:a', 'changes:d', 'changes:e']); l.__anchor = 'staged:a';
  const remap = U.syncSel(l, ['section:staged', 'changes:a', 'section:changes', 'changes:d']);   // a e' passato in Modifiche, e sparito
  assert.deepEqual(J([...l.__sel].sort()), ['changes:a', 'changes:d']);
  assert.equal(l.__anchor, 'changes:a');
  assert.equal(remap('changes:e'), undefined);
  assert.equal(remap('section:staged'), 'section:staged');
});

test('albero: aria-selected segue la selezione (piu righe) o il cursore se vuota', () => {
  const sel = new Set(['staged:Core/Services/OrderService.cs', 'unversioned:docs/notes.txt']);
  const h = U.treeHTML(st.sections, 'staged:Core/Services', '', sel);
  const row = k => h.split(`data-key="${k}"`)[0].split('role="treeitem"').pop() + h.split(`data-key="${k}"`)[1].split('>')[0];
  assert.match(row('staged:Core/Services/OrderService.cs'), /aria-selected="true"/);
  assert.match(row('unversioned:docs/notes.txt'), /aria-selected="true"/);
  assert.match(row('staged:Core/Services'), /aria-selected="false"/);
  assert.match(U.treeHTML(st.sections, 'staged:Core/Services'), /aria-selected="true"[^>]*data-key="staged:Core\/Services"/);
});

test('menu multiplo: conteggio, voci valide per tutti, disabilitate con motivo altrimenti', () => {
  const m = U.changesHTML.multiMenu(['staged:Core/Services/OrderService.cs', 'staged:Core/Services/Coupon.cs'], st);
  const by = re => m.find(i => re.test(i.label || ''));
  assert.match(m[0].label, /Selezione: 2 elementi/);
  assert.deepEqual(J(by(/Rimuovi dai file/).args), { keys: ['staged:Core/Services/OrderService.cs', 'staged:Core/Services/Coupon.cs'] });
  assert.ok(!by(/Rimuovi dai file/).disabled && by(/Rimuovi dai file/).label.includes('(2)'));
  assert.ok(by(/Aggiungi ai file/).disabled && by(/Aggiungi ai file/).hint);
  assert.ok(by(/ignore list/).disabled && by(/ignore list/).hint);   // non sono non versionati
  assert.ok(!by(/SVN Revert/).disabled && by(/SVN Revert/).args.op === 'revert');
  assert.ok(!by(/Elimina/).disabled);
  // misto staged + non versionato: ne' stage ne' unstage, ne' ignore valgono per tutti
  const x = U.changesHTML.multiMenu(['staged:Core/Services/OrderService.cs', 'unversioned:docs/notes.txt'], st);
  assert.ok(x.filter(i => /Rimuovi dai file|Aggiungi ai file|ignore list|Revert/.test(i.label || '')).every(i => i.disabled));
  const u = U.changesHTML.multiMenu(['unversioned:docs/notes.txt', 'unversioned:docs/notes.txt'], st);
  assert.ok(!u.find(i => /ignore list/.test(i.label || '')).disabled);
});

test('contratto: stage/unstage/rowOp accettano key oppure keys (non entrambi)', () => {
  const sch = contract('commands.schema.json');
  const ok = (name, args) => validate(sch, { name, args }).length === 0;
  for (const n of ['stage', 'unstage']) { assert.ok(ok(n, { key: 'changes:a' })); assert.ok(ok(n, { keys: ['changes:a', 'changes:b'] })); assert.ok(!ok(n, {})); }
  assert.ok(ok('rowOp', { op: 'revert', keys: ['staged:a', 'staged:b'] }));
  assert.ok(!ok('rowOp', { op: 'revert', key: 'staged:a', keys: ['staged:b'] }));
});
