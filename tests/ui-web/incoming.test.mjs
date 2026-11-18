// Anteprima di SVN Update (opzione aiExplainIncoming): render puro del dialogo.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadUi } from './helpers.mjs';

const base = ai => ({ branch: 'trunk', count: 2, more: 0, ai,
  items: [{ rev: 9, author: 'bruno', date: '2026-11-18T10:00:00Z', message: 'Nuova pagina', files: [{ action: 'A', path: '/trunk/W/O.cs' }], moreFiles: 3 }] });
const html = ai => loadUi(['i18n', 'util', 'ops']).ops.incomingHTML(base(ai));

test('incoming: elenco, Update e Annulla sempre presenti; riassunto con riga di consumo', () => {
  const h = html({ state: 'done', text: 'Cambi in Core', usage: 'Sonnet 5.5 · 10 token in / 5 out' });
  for (const c of ['incomingUpdate', 'incomingCancel']) assert.ok(h.includes(`data-cmd="${c}"`), c);
  assert.ok(h.includes('r9') && h.includes('bruno') && h.includes('Cambi in Core') && h.includes('Sonnet 5.5'));
  assert.ok(h.includes('altri 3 file'));
});

test('incoming: AI non collegata o in errore, l\'elenco e Update restano; errore riconoscibile = pulsante', () => {
  for (const ai of [{ state: 'none', text: '', usage: '' }, { state: 'loading', text: '', usage: '' }]) {
    const h = html(ai); assert.ok(h.includes('bruno') && h.includes('data-cmd="incomingUpdate"'));
  }
  const h = html({ state: 'error', text: 'accesso scaduto', usage: '', action: { label: 'Rifai l\'accesso', cmd: 'incomingFixAuth' } });
  assert.ok(h.includes('data-cmd="incomingFixAuth"') && h.includes('data-cmd="incomingUpdate"'));
});
