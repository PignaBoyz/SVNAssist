// Avanzamento: la finestra completa solo nella vista che ha avviato l'operazione, nell'altra una striscia non bloccante; niente "0 elementi" da fermo.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { contract, validate, loadUi } from './helpers.mjs';

const U = loadUi(['i18n', 'util', 'diff', 'tree', 'prereq', 'ops']);
const cmds = contract('commands.schema.json');
const P = o => Object.assign({ op: 'merge', title: 'Merge da branches/x', phase: 'Merge in corso...', running: true, ok: false, processed: 0, conflicts: 0, omitted: 0, lines: [], summary: '', origin: 'log' }, o);
const args = h => [...h.matchAll(/data-cmd="(\w+)"/g)].map(m => m[1]);

test('striscia in Changes per un merge partito dal Log: testo, rimando valido, nessun pulsante Annulla', () => {
  const h = U.ops.stripHTML(P(), 'changes');
  assert.match(h, /class="ops-strip"/);
  assert.match(h, /Merge da branches\/x: in corso nella scheda Log…/);
  assert.deepEqual(args(h), ['progressGoto']);
  assert.deepEqual(validate(cmds, { name: 'progressGoto', args: {} }), []);
  assert.doesNotMatch(h, /opCancel|obar/);
});

test('striscia nel Log per un\'operazione partita da Changes: rimanda con mergeResolve; a fine operazione mostra il riepilogo', () => {
  const h = U.ops.stripHTML(P({ origin: 'changes', title: 'SVN Update' }), 'log');
  assert.match(h, /in corso nella scheda Changes/);
  assert.deepEqual(args(h), ['mergeResolve']);
  const d = U.ops.stripHTML(P({ running: false, ok: true, summary: 'Merge completato' }), 'changes');
  assert.match(d, /Merge completato/);
});

test('finestra completa: da fermo niente "0 elementi elaborati" ma un testo onesto; con righe torna il contatore', () => {
  const idle = U.ops.progressHTML(P({ origin: 'log' }));
  assert.doesNotMatch(idle, /0 elementi elaborati/);
  assert.match(idle, /non ha ancora scritto nulla/);
  const busy = U.ops.progressHTML(P({ processed: 3, lines: [{ k: 'U', t: 'U    a.txt' }] }));
  assert.match(busy, /3 elementi elaborati/);
});
