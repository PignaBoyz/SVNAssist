// Shelve: sezione "Shelved (n)" in Changes, comandi validi contro lo schema, voci di menu.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { contract, readJson, validate, loadUi } from './helpers.mjs';

const U = loadUi(['i18n', 'util', 'diff', 'tree', 'prereq', 'changes', 'log']);
const cmds = contract('commands.schema.json'), stSchema = contract('changes-state.schema.json');
const fx = () => structuredClone(readJson('src/SVNAssist.UI.Web/contract/fixtures/changes.json'));
const SH = [{ id: '20261115-103005-Prova', name: 'Prova <b>', date: '2026-11-15T10:30:05', files: 3 }];

test('senza shelve niente sezione; con shelve chiusa di default, aperta mostra nome (neutralizzato), data e file', () => {
  assert.equal(U.changesHTML.shelvedHTML(fx()), '');
  const s = Object.assign(fx(), { shelves: SH });
  assert.deepEqual(validate(stSchema, s), []);
  const closed = U.changesHTML.shelvedHTML(s);
  assert.match(closed, /<b>Shelved<\/b><span class="pill">1<\/span>/); assert.doesNotMatch(closed, /shl-r/);
  const open = U.changesHTML.shelvedHTML(s, true);
  assert.match(open, /data-shelve="20261115-103005-Prova"/); assert.doesNotMatch(open, /<b>Prova/); assert.match(open, /3 file/);
});

test('comandi shelve validi, keep obbligatorio su shelve', () => {
  const ok = [{ name: 'shelve', args: { keys: ['changes:a.txt'], name: 'x', keep: false } }, { name: 'shelve', args: { name: '', keep: true } },
    { name: 'unshelve', args: { id: 'a', keep: true } }, { name: 'shelveSave', args: { id: 'a' } }, { name: 'shelveDelete', args: { id: 'a' } }];
  ok.forEach(c => assert.deepEqual(validate(cmds, c), [], c.name));
  assert.notDeepEqual(validate(cmds, { name: 'shelve', args: { name: 'x' } }), []);
});

test('menu di sezione e selezione: Create Patch anche per i non versionati, Shelve accanto', () => {
  const s = fx(), sec = s.sections.find(x => x.rows.length);
  const m = U.changesHTML.sectionMenu('unversioned', 'section:unversioned', { count: 2 });
  assert.ok(m.some(i => i.cmd === 'createPatch' && !i.disabled)); assert.ok(m.some(i => i.label === 'Shelve…' && !i.disabled));
  const mm = U.changesHTML.multiMenu(sec.rows.slice(0, 2).map(r => r.key), s);
  assert.ok(mm.some(i => i.label === 'Shelve…'));
});
