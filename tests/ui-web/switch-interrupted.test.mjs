// 0.3.2: switch interrotto a meta' (E155009). Errore intero su piu' righe, pulsante "Completa switch" con il suo tooltip,
// voce "(switch incompleto)" nel selettore (sceglibile) e conformita' allo schema.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { contract, readJson, validate, loadUi } from './helpers.mjs';

const U = loadUi(['i18n', 'util', 'diff', 'tree', 'prereq', 'changes', 'log']);
const H = U.changesHTML;
const fx = () => structuredClone(readJson('src/SVNAssist.UI.Web/contract/fixtures/changes.json'));
const ch = contract('changes-state.schema.json');
const cmds = contract('commands.schema.json');

const LONG = "Errore: lo switch a branches/x si è interrotto a metà.\nsvn: E155009: Failed to run the WC DB work queue associated with 'C:\\Progetti\\wc\\managed', work item 19759 (dir-remove managed/Views/CommandBar)";

test('errore lungo: riga d\'errore a capo, testo intero (con l\'a capo), nessuna ellissi', () => {
  const s = fx();
  s.status = { msg: LONG, err: true, seq: 3, action: { label: 'Completa switch', cmd: 'switchResume' } };
  validate(ch, s);
  const h = H.foot(s);
  assert.match(h, /class="foot-r1 err"/);
  assert.ok(h.includes('work item 19759 (dir-remove managed/Views/CommandBar)'));
  assert.ok(h.includes('\n'), 'l\'a capo resta nel testo (pre-wrap)');
  assert.match(h, /data-cmd="switchResume"[^>]*title="Esegue svn cleanup e ripete lo switch/);
});

test('messaggio non d\'errore: riga normale', () => {
  const s = fx();
  s.status = { msg: 'Nessuna modifica.', err: false };
  assert.doesNotMatch(H.foot(s), /foot-r1 err/);
});

test('pulsante "SVN Update ora": tooltip dell\'update, non del cleanup', () => {
  const s = fx();
  s.status = { msg: 'Errore commit: E155011 out of date', err: true, seq: 1, action: { label: 'SVN Update ora', cmd: 'update' } };
  validate(ch, s);
  assert.match(H.foot(s), /title="Esegue svn update sulla working copy"/);
});

test('switchResume e\' un comando dello schema senza argomenti', () => {
  const c = cmds.oneOf.find(x => x.properties.name.const === 'switchResume');
  assert.ok(c);
  assert.deepEqual(c.properties.args.properties, {});
});

test('selettore: la destinazione interrotta mostra "(switch incompleto)" e non "(corrente)"', () => {
  const items = [
    { name: 'trunk', kind: 'trunk', current: false, rev: 1, author: '', date: '' },
    { name: 'branches/x', kind: 'branch', current: false, incomplete: true, rev: 2, author: '', date: '' },
  ];
  const s = fx();
  s.switchPicker = { open: true, mode: 'switch', loading: false, error: '', items, recent: [] };
  validate(ch, s);
  const h = U.swRowsHTML(items, 0, 2, 'branches/x');
  assert.ok(h.includes('(switch incompleto)'));
  assert.ok(!h.includes('(corrente)'));
});
