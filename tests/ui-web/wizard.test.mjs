// Procedura guidata (AI e prerequisiti): render puro, navigazione passi, Avanti disabilitato, flusso completo nel mock della demo.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { contract, readJson, validate, loadUi, loadDemo, read } from './helpers.mjs';

const U = loadUi();
const base = () => structuredClone(readJson('src/SVNAssist.UI.Web/contract/fixtures/changes.json').wizard);
const PROV = [{ id: 'copilot', title: 'GitHub Copilot', cost: 'c1' }, { id: 'claude', title: 'Claude API', cost: 'c2' }];
const W = o => Object.assign(base(), { open: true, providers: PROV }, o);
const btn = (h, label) => [...h.matchAll(/<button[^>]*>/g)].map(m => m[0]).find(b => new RegExp(`title="[^"]*"`).test(b) && h.slice(h.indexOf(b)).startsWith(b) && h.slice(h.indexOf(b), h.indexOf('</button>', h.indexOf(b))).includes(`<span>${label}</span>`));
const disabled = (h, label) => /\sdisabled/.test(btn(h, label) || '');

test('chiusa: nessun markup', () => assert.equal(U.wizardHTML(base()), ''));

test('dialogo modale accessibile: role=dialog, aria-modal, titolo collegato, passi con aria-current', () => {
  const h = U.wizardHTML(W({ step: 1 }));
  assert.match(h, /role="dialog" aria-modal="true" aria-labelledby="wiz-t"/);
  assert.match(h, /id="wiz-t"/);
  assert.equal((h.match(/aria-current="step"/g) || []).length, 1);
  assert.equal((h.match(/role="radio"/g) || []).length, 2);
});

test('passo 1: Avanti disabilitato senza provider, abilitato con il provider scelto; Indietro assente', () => {
  let h = U.wizardHTML(W({ step: 1, canNext: false }));
  assert.ok(disabled(h, 'Avanti'));
  assert.ok(!h.includes('wizardBack'));
  h = U.wizardHTML(W({ step: 1, provider: 'claude', canNext: true }));
  assert.ok(!disabled(h, 'Avanti'));
  assert.match(h, /aria-checked="true"[^>]*class="wiz-card sel"/);
});

test('passo 2: prerequisito mancante disabilita Avanti e mostra il pulsante di installazione; tutto ok = verde', () => {
  const item = { id: 'gh-cli', group: 'g', title: 'GitHub CLI (gh)', ok: false, detail: 'gh.exe non trovato', instructions: 'winget install', primary: 'Installa GitHub CLI', secondary: '' };
  let h = U.wizardHTML(W({ step: 2, provider: 'copilot', items: [item], canNext: false }));
  assert.ok(disabled(h, 'Avanti'));
  assert.match(h, /data-cmd="wizardRun" data-args="[^"]*gh-cli/);
  assert.match(h, /Installa GitHub CLI/);
  assert.match(h, /data-cmd="wizardRecheck"/);
  assert.match(h, /wizardBack/);
  h = U.wizardHTML(W({ step: 2, provider: 'copilot', items: [{ ...item, ok: true, primary: '' }], canNext: true }));
  assert.ok(!disabled(h, 'Avanti'));
  assert.match(h, /class="pi ok"/);
  assert.ok(!h.includes('wizardRun'));
});

test('passo 3: campo API key (password, mai in chiaro) e login GitHub', () => {
  let h = U.wizardHTML(W({ step: 3, provider: 'claude', auth: 'key' }));
  assert.match(h, /<input id="wiz-key" type="password" autocomplete="off"/);
  assert.ok(disabled(h, 'Avanti'));
  h = U.wizardHTML(W({ step: 3, provider: 'copilot', auth: 'github', authOk: false, authDetail: 'non hai fatto login' }));
  assert.match(h, /data-cmd="wizardLogin"/);
  h = U.wizardHTML(W({ step: 3, provider: 'openai', auth: 'openai', endpoint: 'https://x/"><b>' }));
  assert.ok(!h.includes('<b>"'), 'endpoint con HTML neutralizzato');
  assert.match(h, /id="wiz-ep"/);
});

test('passo 4: tendina modelli con il consigliato selezionato e Fine', () => {
  const h = U.wizardHTML(W({ step: 4, provider: 'claude', model: 'claude-sonnet-5-5', canNext: true, models: [{ value: 'claude-sonnet-5-5', label: 'Sonnet' }, { value: 'claude-haiku-5-5', label: 'Haiku' }] }));
  assert.match(h, /<option value="claude-sonnet-5-5" selected>/);
  assert.match(h, /data-wiz="finish"/);
});

test('Annulla e "Faccio io" sempre presenti, in entrambe le modalita', () => {
  for (const w of [W({ step: 1 }), W({ step: 3, auth: 'key' }), W({ mode: 'prereq', items: [] })]) {
    const h = U.wizardHTML(w);
    assert.match(h, /data-cmd="wizardCancel"/); assert.match(h, /data-cmd="openOptions"/); assert.match(h, /Faccio io/);
  }
});

test('modalita prerequisiti: riga AI con "Configura l\'AI", Chiudi disabilitato finche manca qualcosa', () => {
  const items = [{ id: 'svn', group: 'g', title: 'svn', ok: true, detail: 'C:\\svn.exe', instructions: '', primary: '', secondary: '' },
    { id: 'ai', group: 'g', title: 'Provider AI', ok: false, detail: 'nessuno', instructions: 'guida', primary: "Configura l'AI", secondary: '' }];
  const h = U.wizardHTML(W({ mode: 'prereq', items, canNext: false }));
  assert.ok(!h.includes('wiz-steps'));
  assert.ok(disabled(h, 'Chiudi'));
  assert.match(h, /Configura l&#39;AI/);
});

test('tutti i comandi emessi rispettano commands.schema.json', () => {
  const S = contract('commands.schema.json'), out = [];
  const items = [{ id: 'gh-cli', group: 'g', title: 't', ok: false, detail: 'd', instructions: 'i', primary: 'P', secondary: 'S' }];
  for (const w of [W({ step: 1 }), W({ step: 2, items }), W({ step: 3, auth: 'github' }), W({ step: 3, auth: 'claudecode' }), W({ step: 3, auth: 'openai' }), W({ step: 4, models: [{ value: 'a', label: 'a' }] }), W({ mode: 'prereq', items })])
    out.push(...U.wizardHTML(w).matchAll(/data-cmd="(\w+)"(?: data-args="([^"]*)")?/g));
  assert.ok(out.length > 12);
  for (const [, name, args] of out) assert.deepEqual(validate(S, { name, args: args ? JSON.parse(args.replace(/&quot;/g, '"')) : {} }), [], name);
});

test('changes.js: etichette SVN, footer prerequisiti rosso/verde, focus trap, Esc e ricontrollo al ritorno del focus', () => {
  const js = read('src/SVNAssist.UI.Web/js/changes.js');
  assert.match(js, /'SVN Update'/);
  assert.match(js, /wizardOpenPrereq/); assert.match(js, /prq-st bad/); assert.match(js, /prq-st ok/);
  assert.match(js, /e\.key === 'Escape'/); assert.match(js, /e\.key !== 'Tab'/); assert.match(js, /.inert = anyModal/);
  assert.match(js, /addEventListener\('focus'/);
  assert.match(js, /Generazione in corso…/);
  assert.match(js, /lv-\$\{lv\}/);
});

test('mock: flusso completo da "Genera con AI" senza provider fino al salvataggio e alla generazione', async () => {
  const { run, bridge } = await loadDemo();
  const B = bridge(), got = [], win = { frameElement: { isConnected: true } }, tick = () => new Promise(r => setImmediate(r));
  B.attach(win, m => got.push(JSON.parse(JSON.stringify(m))));
  const send = async (name, args) => { B.postMessage({ v: 1, id: send.n = (send.n || 0) + 1, type: 'cmd', name, args }, win); for (let i = 0; i < 12; i++) await tick(); };
  const last = () => got.filter(m => m.type === 'state').at(-1).state;
  const okSchema = () => assert.deepEqual(validate(contract('changes-state.schema.json'), last()), []);

  await run("S.env.claudeInstalled = false; S.env.anthropicKey = false; S.settings.aiprovider = 'auto'; S.env.gh = false; S.env.ghInstalled = false; autoDetect()", 't');
  B.postMessage({ v: 1, type: 'ready', view: 'changes', id: null }, win); await tick();
  await send('wizardOpen', {}); okSchema();
  assert.equal(last().wizard.open, true); assert.equal(last().wizard.step, 1); assert.equal(last().wizard.canNext, false);

  await send('wizardNext', {}); assert.equal(last().wizard.step, 1, 'senza provider non si avanza');
  await send('wizardPick', { provider: 'claude' }); assert.equal(last().wizard.canNext, true);
  await send('wizardNext', {}); okSchema();
  assert.equal(last().wizard.step, 2); assert.deepEqual(last().wizard.items, []); assert.equal(last().wizard.canNext, true);
  await send('wizardNext', {});
  assert.equal(last().wizard.step, 3); assert.equal(last().wizard.auth, 'key'); assert.equal(last().wizard.canNext, false);
  await send('wizardNext', {}); assert.equal(last().wizard.step, 3, 'senza chiave non si avanza');

  await send('wizardSetAuth', { endpoint: '', key: 'sk-ant-segreta' });
  assert.equal(last().wizard.authOk, true); assert.ok(!JSON.stringify(last()).includes('sk-ant-segreta'), 'la chiave non torna alla UI');
  await send('wizardNext', {}); okSchema();
  assert.equal(last().wizard.step, 4); assert.equal(last().wizard.model, 'claude-sonnet-5-5'); assert.equal(last().wizard.models.length, 3);

  await send('wizardBack', {}); assert.equal(last().wizard.step, 3);
  await send('wizardNext', {});
  await send('wizardFinish', { model: 'claude-haiku-5-5' });
  assert.equal(last().wizard.open, false);
  assert.equal(JSON.parse(JSON.stringify(run('[S.settings.aiprovider, S.settings.claudemodel]', 't'))).join(), 'claude,claude-haiku-5-5');

  await send('wizardOpen', {}); await send('wizardCancel', {}); assert.equal(last().wizard.open, false);
  await send('wizardPick', { provider: 'inventato' }); assert.equal(got.filter(m => m.type === 'reply').at(-1).ok, false);
});

test('mock: prerequisiti mancanti aprono la procedura e il pulsante AI porta ai 4 passi', async () => {
  const { run, bridge } = await loadDemo();
  const B = bridge(), got = [], win = { frameElement: { isConnected: true } }, tick = () => new Promise(r => setImmediate(r));
  B.attach(win, m => got.push(JSON.parse(JSON.stringify(m))));
  const send = async (name, args) => { B.postMessage({ v: 1, id: send.n = (send.n || 0) + 1, type: 'cmd', name, args }, win); for (let i = 0; i < 12; i++) await tick(); };
  const last = () => got.filter(m => m.type === 'state').at(-1).state;
  await run("S.env.claudeInstalled = false; S.env.anthropicKey = false; S.settings.aiprovider = 'auto'; S.env.gh = false; S.env.ghInstalled = false; autoDetect()", 't');
  B.postMessage({ v: 1, type: 'ready', view: 'changes', id: null }, win); await tick();
  await send('wizardOpenPrereq', {});
  assert.equal(last().wizard.mode, 'prereq'); assert.deepEqual(last().wizard.items.map(i => i.id), ['svn', 'ai']);
  assert.equal(last().wizard.items.find(i => i.id === 'ai').ok, false); assert.equal(last().wizard.canNext, false);
  await send('wizardRun', { id: 'ai', primary: true });
  assert.equal(last().wizard.mode, 'ai'); assert.equal(last().wizard.step, 1);
});
