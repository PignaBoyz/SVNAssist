// Bug 8 (0.4.0): elenco modelli dal provider, "Altro...", Aggiorna elenco, costo, account, errore del modello con azione.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { contract, readJson, validate, loadUi, loadDemo } from './helpers.mjs';

const U = loadUi();
const base = () => structuredClone(readJson('src/SVNAssist.UI.Web/contract/fixtures/changes.json').wizard);
const PROV = [{ id: 'copilot', title: 'GitHub Copilot', cost: 'c1' }, { id: 'claude', title: 'Claude API', cost: 'c2' }];
const W = o => Object.assign(base(), { open: true, providers: PROV }, o);
const buttonOf = (h, label) => { const i = h.indexOf(`<span>${label}</span>`); return i < 0 ? '' : h.slice(h.lastIndexOf('<button', i), i); };

const M4 = o => W({ step: 4, provider: 'copilot', model: 'm1', canNext: true, modelsUpdated: '2026-11-17T10:30:00.000Z', modelsSource: 'live', modelSaved: 'default',
  models: [{ value: 'default', label: 'Predefinito di Copilot', cost: '' }, { value: 'm1', label: 'Modello 1', cost: '1× richieste premium per richiesta' }, { value: 'm2', label: 'Modello 2', cost: '' }], ...o });

test('passo 4: elenco dal provider con costo (Copilot), "Altro...", Aggiorna elenco, data e account', () => {
  U.wizOther = { on: false, text: '' };
  const h = U.wizardHTML(M4({ modelsAccount: 'Account GitHub: mrossi (accesso: gh-cli)' }));
  assert.match(h, /<option value="m1" selected>Modello 1 · 1× richieste premium per richiesta<\/option>/);
  assert.match(h, /<option value="m2">Modello 2 · costo non disponibile<\/option>/, 'mai un costo inventato');
  assert.match(h, /<option value="default">Predefinito di Copilot<\/option>/, 'il predefinito non ha costo');
  assert.match(h, /<option value="__other__">Altro…<\/option>/);
  assert.match(h, /id="wiz-model-other"[^>]*hidden/);
  assert.match(h, /data-cmd="wizardModelsRefresh"/);
  assert.match(h, /Elenco aggiornato il 2026-11-17/);
  assert.match(h, /mrossi/); assert.match(h, /Collegato come/);
  assert.match(h, /In uso adesso/);
  assert.ok(!/consigliato/i.test(h), 'nessun modello consigliato');
  assert.ok(!/\sdisabled/.test(buttonOf(h, 'Aggiorna elenco')));
});

test('passo 4: "Altro..." mostra il campo di testo; il caricamento disabilita Aggiorna', () => {
  U.wizOther = { on: true, text: 'modello-aziendale' };
  let h = U.wizardHTML(M4());
  assert.match(h, /<option value="__other__" selected>/);
  assert.ok(!/id="wiz-model-other"[^>]*hidden/.test(h));
  assert.match(h, /value="modello-aziendale"/);
  assert.ok(!/<option value="m1" selected>/.test(h));
  U.wizOther = { on: false, text: '' };
  h = U.wizardHTML(M4({ modelsLoading: true, modelsUpdated: '', modelsSource: '' }));
  assert.match(buttonOf(h, 'Aggiorna elenco'), /\sdisabled/);
});

test('passo 4: errore del provider e valore salvato fuori elenco visibili; fuori da Copilot nessun costo', () => {
  U.wizOther = { on: false, text: '' };
  const h = U.wizardHTML(M4({ provider: 'claude', modelsError: true, modelsNote: 'Elenco non aggiornato: HTTP 403\nIl modello salvato "x" non è più nell\'elenco', modelsUpdated: '', modelsSource: 'fallback',
    models: [{ value: 'a', label: 'Modello A', cost: '' }] }));
  assert.match(h, /class="small wiz-note err" role="alert"/); assert.match(h, /HTTP 403<br>/);
  assert.match(h, /Elenco predefinito dell(&#39;|')app/);
  assert.match(h, /<option value="a">Modello A<\/option>/);
});

test('errore AI con azione: pulsante "Aggiorna elenco modelli" accanto al messaggio, assente senza azione', () => {
  const s = readJson('src/SVNAssist.UI.Web/contract/fixtures/changes.json');
  s.commit.aiStatus = 'Errore AI: il modello non esiste'; s.commit.aiLevel = 'err'; s.commit.aiSeq = 3;
  const A = loadUi(['i18n', 'util', 'diff', 'tree', 'prereq', 'changes', 'log'], { setTimeout: () => 0, clearTimeout: () => {} }).changesHTML;
  assert.ok(!/wizardOpenModels/.test(A.aiStat(s.commit)));
  s.commit.aiAction = { label: 'Aggiorna elenco modelli', cmd: 'wizardOpenModels' };
  const h = A.aiStat(s.commit);
  assert.match(h, /data-cmd="wizardOpenModels"/); assert.match(h, /Aggiorna elenco modelli/); assert.match(h, /lv-err/);
  assert.deepEqual(validate(contract('changes-state.schema.json'), s), []);
});

test('mock: wizardOpenModels apre il passo Modello, Aggiorna elenco cambia l\'elenco, "Altro..." accetta qualsiasi id', async () => {
  const { run, bridge } = await loadDemo();
  const B = bridge(), got = [], win = { frameElement: { isConnected: true } }, tick = () => new Promise(r => setImmediate(r));
  B.attach(win, m => got.push(JSON.parse(JSON.stringify(m))));
  const send = async (name, args) => { B.postMessage({ v: 1, id: send.n = (send.n || 0) + 1, type: 'cmd', name, args }, win); for (let i = 0; i < 12; i++) await tick(); };
  const last = () => got.filter(m => m.type === 'state').at(-1).state;
  const okSchema = () => assert.deepEqual(validate(contract('changes-state.schema.json'), last()), []);
  await run("S.settings.aiprovider = 'copilot'", 't');
  B.postMessage({ v: 1, type: 'ready', view: 'changes', id: null }, win); await tick();

  await send('wizardOpenModels', {}); okSchema();
  const w = last().wizard;
  assert.equal(w.open, true); assert.equal(w.step, 4); assert.equal(w.provider, 'copilot'); assert.equal(w.fromGenerate, true);
  assert.ok(w.models.some(m => m.cost.includes('premium')), 'costi (finti) mostrati'); assert.ok(w.modelsUpdated);
  const n = w.models.length;
  await send('wizardModelsRefresh', {}); okSchema();
  assert.equal(last().wizard.models.length, n - 1, 'un modello sparisce dopo l\'aggiornamento (come con Copilot)');
  await send('wizardFinish', { model: 'modello-aziendale-5.6' }); okSchema();
  assert.equal(last().wizard.open, false);
  assert.equal(JSON.parse(JSON.stringify(run('S.settings.copilotmodel', 't'))), 'modello-aziendale-5.6');
});
