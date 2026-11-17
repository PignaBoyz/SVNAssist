// Menu contestuale (tasto destro, tasto Menu / Maiusc+F10) per le righe in conflitto di Changes e per la selezione multipla.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { contract, readJson, validate, loadUi } from './helpers.mjs';

const U = loadUi(['i18n', 'util', 'diff', 'tree', 'prereq', 'changes', 'log']);
const cmds = contract('commands.schema.json');
const fx = () => structuredClone(readJson('src/SVNAssist.UI.Web/contract/fixtures/changes.json'));
const cf = (key, path, kind = 'text') => ({ key, path, name: path, kind, files: [] });

test('menu di un conflitto: azioni, apri/Esplora/copia percorso, comandi validi; ad albero solo resolved', () => {
  const m = U.changesHTML.conflictMenu('changes:README.txt', 'text');
  const labels = m.filter(i => i.label).map(i => i.label);
  for (const l of ['Apri editor di merge', 'Usa la mia', 'Usa la loro', 'Usa entrambe (mia poi loro)', 'Usa entrambe (loro poi mia)', 'Segna come risolto', 'Apri file', 'Mostra differenze', 'Apri in Esplora file', 'Copia percorso'])
    assert.ok(labels.includes(l), l);
  m.filter(i => i.cmd).forEach(i => assert.deepEqual(validate(cmds, { name: i.cmd, args: i.args }), [], i.label));
  const tr = U.changesHTML.conflictMenu('changes:ren.txt', 'tree').filter(i => i.label).map(i => i.label);
  assert.ok(tr.includes('Segna come risolto') && !tr.includes('Usa la mia') && !tr.includes('Apri file'));
  assert.ok(!U.changesHTML.conflictMenu('changes:p', 'property').some(i => /entrambe|editor/.test(i.label || '')));
});

test('selezione multipla di conflitti di testo: voci con keys valide; con un file non in conflitto disabilitate', () => {
  const s = Object.assign(fx(), { conflicts: [cf('changes:README.txt', 'README.txt'), cf('changes:B.txt', 'B.txt')] });
  const mk = (key, status) => ({ key, status, can: {} });
  s.sections = [{ id: 'changes', rows: [mk('changes:README.txt', 'C'), mk('changes:B.txt', 'C'), mk('changes:ok.txt', 'M')], count: 3 }];
  const m = U.changesHTML.multiMenu(['changes:README.txt', 'changes:B.txt'], s);
  const both = m.find(i => /Usa entrambe, mia poi loro/.test(i.label || ''));
  assert.ok(both && !both.disabled);
  assert.deepEqual(validate(cmds, { name: both.cmd, args: both.args }), []);
  assert.ok(m.some(i => /Copia percorsi \(2\)/.test(i.label || '')));
  const mix = U.changesHTML.multiMenu(['changes:README.txt', 'changes:ok.txt'], s);
  assert.ok(mix.find(i => /Usa la mia/.test(i.label || '')).disabled);
});

test('Copia percorso: percorso completo costruito dalla chiave', () => {
  assert.match(U.changesHTML.fullPathOf('changes:src/a.txt'), /a\.txt$/);
});
