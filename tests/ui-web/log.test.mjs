// Vista Log: filtri, elenco file cliccabili (diff nativo), confronto tra revisioni, diff di cartella.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { contract, readJson, validate, loadUi } from './helpers.mjs';

const U = loadUi();
const j = x => JSON.parse(JSON.stringify(x)); // oggetti creati nel contesto vm: niente confronto per realm
const S = contract('log-state.schema.json');
const fx = () => structuredClone(readJson('src/SVNAssist.UI.Web/contract/fixtures/log.json'));
const cmds = contract('commands.schema.json');
const args = h => [...h.matchAll(/data-cmd="(\w+)"(?: data-args="([^"]*)")?/g)].map(([, name, a]) => ({ name, args: a ? JSON.parse(a.replace(/&quot;/g, '"')) : {} }));
const valid = c => assert.deepEqual(validate(cmds, c), [], JSON.stringify(c));

test('barra filtri: autore, messaggio, percorso, date; valori riportati e scappati', () => {
  const s = fx(); s.filter = { author: 'm"rossi', text: 'bug', path: 'src/Core', from: '2024-01-02', to: '' };
  const h = U.logHTML.filter(s);
  for (const n of ['author', 'text', 'path', 'from', 'to']) assert.match(h, new RegExp(`name="${n}"`));
  assert.match(h, /type="date"[^>]*value="2024-01-02"/);
  assert.match(h, /value="m&quot;rossi"/);
  assert.match(h, /class="lf on"/);
  assert.match(h, /Azzera/);
});

test('senza filtri attivi non c\'e\' Azzera; lo stato senza "filter" (vecchi snapshot) non rompe la UI', () => {
  const s = fx();
  assert.doesNotMatch(U.logHTML.filter(s), /Azzera/);
  s.filter = { author: '', text: '', path: '', from: '', to: '' };
  assert.doesNotMatch(U.logHTML.filter(s), /Azzera|class="lf on"/);
});

test('Azzera invia logFilter con args vuoti (valido per lo schema)', () => {
  const s = fx(); s.filter = { author: 'x', text: '', path: '', from: '', to: '' };
  const c = args(U.logHTML.filter(s)).find(x => x.name === 'logFilter');
  assert.ok(c); valid(c);
});

test('ogni file della revisione e\' un pulsante logOpenFile; le cartelle no', () => {
  const s = fx(); s.sel = 123; s.entries[1].paths.push({ a: 'M', p: '/trunk/Core', k: 'dir' });
  const h = U.logHTML.detail(s);
  const found = args(h).filter(x => x.name === 'logOpenFile');
  assert.deepEqual(found.map(x => x.args), [{ rev: 123, i: 0 }, { rev: 123, i: 1 }]);
  found.forEach(valid);
  assert.match(h, /<span class="muted pt" title="Cartella">\/trunk\/Core/);
});

test('Ctrl/Maiusc+clic su un\'altra revisione = confronto; clic semplice o stessa revisione = selezione', () => {
  assert.deepEqual(j(U.logClick(124, 120, true)), { cmd: 'logCompare', args: { a: 124, b: 120 } });
  assert.deepEqual(j(U.logClick(124, 120, false)), { cmd: 'logSelect', args: { rev: 120 } });
  assert.deepEqual(j(U.logClick(124, 124, true)), { cmd: 'logSelect', args: { rev: 124 } });
  assert.deepEqual(j(U.logClick(null, 120, true)), { cmd: 'logSelect', args: { rev: 120 } });
  valid({ name: 'logCompare', args: { a: 1, b: 2 } });
});

test('elenco file di cartella: titolo, conteggio, Apri come patch, Chiudi, un pulsante per file', () => {
  const s = fx(); s.files = { kind: 'folder', title: 'Modifiche in C:\\wc\\src', items: [{ a: 'M', p: 'a.cs' }, { a: 'A', p: 'b.cs' }] };
  const h = U.logHTML.detail(s);
  assert.match(h, /Modifiche in C:\\wc\\src/);
  assert.match(h, /2 file/);
  const c = args(h);
  assert.deepEqual(c.map(x => x.name), ['logFilesPatch', 'logFilesClose', 'logFileOpen', 'logFileOpen']);
  assert.deepEqual(c.filter(x => x.name === 'logFileOpen').map(x => x.args), [{ i: 0 }, { i: 1 }]);
  c.forEach(valid);
});

test('confronto tra revisioni: niente "Apri come patch"; elenco vuoto = messaggio', () => {
  const s = fx(); s.files = { kind: 'compare', title: 'File cambiati tra r1 e r3', items: [] };
  const h = U.logHTML.detail(s);
  assert.doesNotMatch(h, /logFilesPatch/);
  assert.match(h, /Nessun file da mostrare/);
});

test('lo stato con filter e files rispetta lo schema', () => {
  const s = fx(); s.filter = { author: 'a', text: '', path: '', from: '2024-01-01', to: '2024-02-01' };
  s.files = { kind: 'compare', title: 't', items: [{ a: 'M', p: 'x.cs' }] };
  s.entries[0].paths[0].k = 'file';
  assert.deepEqual(validate(S, s), []);
  s.files.kind = 'altro';
  assert.notDeepEqual(validate(S, s), []);
});

test('tabella revisioni: grid con intestazioni Rev./Autore/Data/Messaggio, una riga per revisione, solo prima riga del messaggio', () => {
  const s = fx(); s.sel = s.entries[1].rev; s.entries[0].msg = 'Prima riga\nseconda riga';
  const h = U.logHTML.list(s);
  assert.equal((h.match(/role="columnheader"/g) || []).length, 4);
  assert.equal((h.match(/class="lr/g) || []).length, s.entries.length);
  assert.match(h, /<span class="m" role="gridcell" title="Prima riga\nseconda riga">Prima riga<\/span>/);
  assert.match(h, new RegExp(`class="lr sel" role="row" aria-selected="true" tabindex="0" data-rev="${s.sel}"`));
  assert.equal((h.match(/tabindex="0"/g) || []).length, 1);
});

test('"Carica altre 50" in fondo alla tabella solo se canMore; il dettaglio mostra messaggio completo e azione dei file', () => {
  const s = fx(); s.canMore = false;
  assert.doesNotMatch(U.logHTML.list(s), /logMore/);
  s.canMore = true;
  const h = U.logHTML.list(s);
  assert.ok(h.indexOf('logMore') > h.lastIndexOf('class="lr'));
  valid(args(h).find(x => x.name === 'logMore'));
  s.sel = s.entries[0].rev; s.entries[0].msg = 'riga1\nriga2';
  const d = U.logHTML.detail(s);
  assert.match(d, /class="lmsg">riga1\nriga2</);
  assert.match(d, /class="sn">/);
});

test('tastiera: frecce/Home/End spostano la selezione entro i limiti, Invio la conferma, altri tasti ignorati', () => {
  const r = [124, 123, 120];
  assert.equal(U.logKey('ArrowDown', r, 124), 123);
  assert.equal(U.logKey('ArrowDown', r, 120), 120);
  assert.equal(U.logKey('ArrowUp', r, 124), 124);
  assert.equal(U.logKey('End', r, 124), 120);
  assert.equal(U.logKey('Home', r, 120), 124);
  assert.equal(U.logKey('Enter', r, 123), 123);
  assert.equal(U.logKey('x', r, 123), null);
  assert.equal(U.logKey('ArrowDown', r, 999), null);
});

test('opzioni Show Log: due checkbox (stop on copy spento di default), logOptions valido', () => {
  const s = fx(); const h = U.logHTML.opts(s);
  assert.doesNotMatch(h, /name="stopOnCopy" checked/);
  assert.doesNotMatch(h, /name="merged" checked/);
  s.stopOnCopy = true; s.includeMerged = true;
  const h2 = U.logHTML.opts(s);
  assert.match(h2, /name="stopOnCopy" checked/);
  assert.match(h2, /name="merged" checked/);
  valid({ name: 'logOptions', args: { stopOnCopy: false, merged: true } });
});

test('revisioni unite: riga indentata (classe mg/d1) e merge inverso segnalato; schema ok', () => {
  const s = fx(); s.sel = null;
  s.entries.splice(1, 0, { ...s.entries[0], rev: 99, depth: 1 }, { ...s.entries[0], rev: 98, depth: 1, reverse: true });
  assert.deepEqual(validate(S, s), []);
  const h = U.logHTML.list(s);
  assert.match(h, /class="lr mg d1" role="row"[^>]*data-rev="99"/);
  assert.match(h, /class="lr mg d1 rvm"[^>]*data-rev="98"/);
  assert.match(h, /merge inverso/);
  assert.match(h, /aria-level="2"/);
});
