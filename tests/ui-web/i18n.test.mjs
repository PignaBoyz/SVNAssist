// Lingua dell'interfaccia: ogni t('...') ha la voce inglese, il cambio lingua cambia i testi, la demo manda `lang`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync } from 'node:fs';
import { read, readJson, loadUi, loadDemo } from './helpers.mjs';

const dir = new URL('../../src/SVNAssist.UI.Web/js/', import.meta.url);
const files = readdirSync(dir).filter(f => f.endsWith('.js') && f !== 'i18n.js');
const unescape = s => s.replace(/\\(.)/g, (m, c) => (c === 'n' ? '\n' : c === 't' ? '\t' : c));
const placeholders = s => (s.match(/\{\d+\}/g) || []).sort().join(',');

// chiamate t('...') / U.t("...") con primo argomento letterale; le altre (non letterali) vengono segnalate
function calls() {
  const used = new Set(), bad = [];
  for (const f of files) {
    const src = read(`src/SVNAssist.UI.Web/js/${f}`);
    for (const m of src.matchAll(/(?<![\w.$])(?:U\.)?t\(\s*(?:'((?:[^'\\]|\\.)*)'|"((?:[^"\\]|\\.)*)")?/g)) {
      if (m[1] == null && m[2] == null && !(f === 'util.js' && /^U\.t\(SN\[/.test(src.slice(m.index, m.index + 12)))) bad.push(`${f}: ${src.slice(m.index, m.index + 40).split('\n')[0]}`);
      else if (m[1] != null || m[2] != null) used.add(unescape(m[1] != null ? m[1] : m[2]));
    }
  }
  return { used, bad };
}

test('ogni t(...) ha un primo argomento letterale e la sua voce inglese (stessi segnaposto)', () => {
  const U = loadUi();
  const { used, bad } = calls();
  assert.ok(used.size > 100, 'estrazione fallita: ' + used.size);
  // `var t = ...` locali (es. nome di variabile) non sono chiamate: ammesse solo se non seguite da "(" -> qui bad contiene solo t( non letterali
  assert.deepEqual(bad, [], 'chiamate t() non letterali (la chiave deve essere un testo fisso):\n' + bad.join('\n'));
  const missing = [...used].filter(k => U.EN[k] == null);
  assert.deepEqual(missing, [], 'voci inglesi mancanti:\n' + missing.join('\n'));
  const wrong = [...used].filter(k => placeholders(k) !== placeholders(U.EN[k]));
  assert.deepEqual(wrong, [], 'segnaposto diversi:\n' + wrong.join('\n'));
});

test('le voci inglesi senza uso nel codice sono solo i nomi degli stati (util.js)', () => {
  const U = loadUi();
  const { used } = calls();
  const status = ['Modificato', 'Aggiunto', 'Eliminato', 'Conflitto', 'Non versionato', 'Mancante', 'Sostituito', 'Bloccato (lock)'];
  const stale = Object.keys(U.EN).filter(k => !used.has(k) && !status.includes(k));
  assert.deepEqual(stale, [], 'voci inglesi non usate:\n' + stale.join('\n'));
  for (const k of status) assert.ok(U.EN[k], 'manca lo stato ' + k);
});

test('t(): italiano = chiave, inglese = traduzione, segnaposto sostituiti, chiave ignota = chiave', () => {
  const U = loadUi();
  assert.equal(U.lang(), 'it');
  assert.equal(U.t('Committa {0} file (Ctrl+Invio)', 3), 'Committa 3 file (Ctrl+Invio)');
  assert.equal(U.setLang('en'), true);
  assert.equal(U.setLang('en'), false);
  assert.equal(U.t('Committa {0} file (Ctrl+Invio)', 3), 'Commit 3 file(s) (Ctrl+Enter)');
  assert.equal(U.t('testo mai tradotto'), 'testo mai tradotto');
  assert.equal(U.setLang('xx'), true, 'lingua sconosciuta = italiano');
  assert.equal(U.lang(), 'it');
});

test('il cambio lingua cambia i testi resi da prereq, tree, log, diff', () => {
  const U = loadUi();
  const changes = readJson('src/SVNAssist.UI.Web/contract/fixtures/changes.json');
  const log = readJson('src/SVNAssist.UI.Web/contract/fixtures/log.json');
  const render = () => [U.prereqHTML({ ...changes.prereq, open: true }), U.treeHTML(changes.sections, null, ''), U.logHTML.top(log), U.logHTML.detail({ ...log, sel: null }), U.diffHTML('')].join('\n');
  const it = render();
  U.setLang('en');
  const en = render();
  assert.notEqual(it, en);
  assert.match(it, /Prerequisiti/);
  assert.match(en, /Prerequisites/);
  assert.doesNotMatch(en, /Prerequisiti|Carica log|Nessuna revisione|Nessuna differenza/);
  assert.match(en, /Load log/);
  assert.match(en, /No textual difference/);
  assert.equal(U.statusName('M'), 'Modified');
  U.setLang('it');
  assert.equal(U.statusName('M'), 'Modificato');
  assert.equal(render(), it, 'tornando in italiano il rendering e identico');
});

test('la demo manda lang negli state e traduce i testi simulati', async () => {
  const { bridge } = await loadDemo();
  const b = bridge();
  assert.equal(b.getLang(), 'it');
  const itSecs = b.snapshotChanges().sections.map(s => s.title);
  assert.equal(itSecs.join('|'), 'Staged|Modifiche|Non versionati');
  const got = [];
  b.attach({ __probe: 1 }, m => got.push(m));
  b.setLang('en');
  assert.equal(b.getLang(), 'en');
  assert.equal(b.snapshotChanges().sections.map(s => s.title).join('|'), 'Staged|Changes|Unversioned');
  b.setLang('it');
});
