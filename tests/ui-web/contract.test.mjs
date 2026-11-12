// Il contratto (contract/*.schema.json) vs fixture, vs mock-bridge della demo, vs UI.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { contract, readJson, validate, loadDemo, loadUi } from './helpers.mjs';

const S = { env: contract('envelope.schema.json'), cmd: contract('commands.schema.json'), ch: contract('changes-state.schema.json'), log: contract('log-state.schema.json'), diff: contract('diff-state.schema.json') };
const fx = n => readJson('src/SVNAssist.UI.Web/contract/fixtures/' + n + '.json');
const ok = (schema, v) => assert.deepEqual(validate(schema, v), []);
const tick = () => new Promise(r => setImmediate(r));

test('le fixture rispettano gli schemi', () => { ok(S.ch, fx('changes')); ok(S.log, fx('log')); ok(S.diff, fx('diff')); });
test('lo schema rifiuta stati non validi', () => {
  const bad = fx('changes'); bad.view = 'griglia'; bad.sections[0].rows[0].extra = 1; delete bad.status;
  assert.ok(validate(S.ch, bad).length >= 3);
});

test('mock-bridge: ogni snapshot e ogni messaggio sono conformi', async () => {
  const { run, bridge } = await loadDemo();
  const B = bridge(), got = [];
  const win = { frameElement: { isConnected: true } };
  B.attach(win, m => got.push(JSON.parse(JSON.stringify(m))));
  const send = async (name, args) => { B.postMessage({ v: 1, id: send.n = (send.n || 0) + 1, type: 'cmd', name, args }, win); for (let i = 0; i < 6; i++) await tick(); };
  const check = () => { for (const m of got) { ok(S.env, m); if (m.type === 'state') ok(S[m.view === 'changes' ? 'ch' : m.view], m.state); } };
  const last = () => got.filter(m => m.type === 'state').at(-1).state;

  await run('autoDetect()', 'test');                       // rileva la working copy e carica lo stato
  B.postMessage({ v: 1, type: 'ready', view: 'changes', id: null }, win); await tick();
  check();
  let st = last();
  assert.equal(st.sections.map(s => s.id).join(), 'staged,changes,unversioned');
  assert.ok(st.sections[1].count > 0 && st.sections[2].count > 0, 'dati demo presenti');
  assert.equal(st.sections[0].count, 0, 'come Git: Staged parte vuoto');
  assert.equal(st.commit.all, true, 'Staged vuoto = Commit All');
  assert.ok(st.workingCopy.branch, 'branch presente');

  // staged: metti un file, poi spostalo e riportalo
  await send('stage', { key: st.sections[1].rows.find(r => r.kind === 'file' && r.status !== 'C').key }); st = last();
  assert.equal(st.commit.all, false, 'con file in Staged = Commit Staged');
  const k = st.sections[0].rows.find(r => r.kind === 'file').key;
  await send('unstage', { key: k }); st = last();
  assert.ok(!st.sections[0].rows.some(r => r.key === k), 'file uscito da Staged');
  assert.ok(st.sections[1].rows.some(r => r.key === k.replace('staged:', 'changes:')) || st.sections[2].rows.some(r => r.key === k.replace('staged:', 'unversioned:')), 'file tornato tra le modifiche');
  await send('stage', { key: 'section:changes' });
  await send('stage', { key: 'section:unversioned' });
  await send('unstage', { key: 'section:staged' }); st = last();
  assert.equal(st.sections[0].count, 0, 'unstage di sezione');
  await send('stage', { key: 'section:changes' }); st = last();
  assert.ok(st.sections[0].count > 0 && !st.sections[0].rows.some(r => r.status === 'C'), 'stage di sezione, senza conflitti');

  // vista, tema, cartelle, lingua
  await send('setView', { view: 'list' }); st = last();
  assert.equal(st.view, 'list'); assert.ok(st.sections[0].rows.every(r => r.kind === 'file' && r.depth === 0));
  await send('setView', { view: 'tree' });
  await send('collapseAll', {}); st = last();
  await send('toggleSection', { sec: 'changes' }); assert.equal(last().sections[1].expanded, false);
  await send('expandAll', {});
  await send('setTheme', { mode: 'blue' }); st = last();
  assert.deepEqual(st.theme, { mode: 'blue', kind: 'blue' });
  assert.ok(got.some(m => m.type === 'theme' && m.kind === 'blue'), 'messaggio theme inviato');
  await send('setTheme', { mode: 'auto' });
  await send('setLanguage', { lang: 'english' }); assert.equal(last().commit.lang, 'english');
  await send('setCommitText', { text: 'Messaggio di prova' });

  // commit dei file in Staged
  await send('commit', {}); st = last();
  assert.equal(st.sections[0].count, 0, 'commit svuota Staged');
  assert.match(st.status.msg, /Commit completato/);

  // diff in una scheda dell'editor + log
  await send('refresh', {});
  const f = last().sections.flatMap(s => s.rows).find(r => r.kind === 'file' && r.can.diff);
  await send('showDiff', { key: f.key });
  const id = JSON.parse(JSON.stringify(run('S.ui.diffs.map(d => d.id)', 't')))[0];
  assert.ok(id, 'scheda diff aperta');
  B.postMessage({ v: 1, type: 'ready', view: 'diff', id }, win); await tick();
  B.postMessage({ v: 1, type: 'ready', view: 'log', id: null }, win); await tick();
  await send('logLoad', { path: 'C:\\dev\\Acme.Shop' });
  const lg = got.filter(m => m.type === 'state' && m.view === 'log').at(-1).state;
  assert.ok(lg.entries.length > 5);
  await send('logSelect', { rev: lg.entries[0].rev });
  assert.ok(got.filter(m => m.type === 'state' && m.view === 'log').at(-1).state.diff.length > 0, 'diff della revisione');
  check();
  assert.ok(got.filter(m => m.type === 'state').at(-1).rev > 1);

  // comando sconosciuto -> reply ko
  await send('nonEsiste', {});
  assert.equal(got.filter(m => m.type === 'reply').at(-1).ok, false);
  check();
});

test('ogni comando dello schema ha un gestore nel mock, e viceversa', async () => {
  const { bridge } = await loadDemo();
  const names = S.cmd.oneOf.map(c => c.properties.name.const).sort();
  assert.deepEqual([...bridge().commands].sort(), names);
});

test('i comandi emessi dalla UI (data-cmd) rispettano commands.schema.json', () => {
  const U = loadUi();
  const html = U.treeHTML(fx('changes').sections, null) + U.prereqHTML(fx('changes').prereq) + U.logHTML.top(fx('log')) + U.logHTML.detail(fx('log'));
  const found = [...html.matchAll(/data-cmd="(\w+)"(?: data-args="([^"]*)")?/g)];
  assert.ok(found.length > 10, 'attributi trovati: ' + found.length);
  for (const [, name, args] of found) {
    const a = args ? JSON.parse(args.replace(/&quot;/g, '"').replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&#39;/g, "'")) : {};
    ok(S.cmd, { name, args: a });
  }
});
