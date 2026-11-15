// 0.5.0: dialogo SVN Branch/Tag: stato conforme allo schema, markup, argomenti di branchTagOk e voci di accesso.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { contract, readJson, validate, loadUi } from './helpers.mjs';

const U = loadUi(['i18n', 'util', 'diff', 'tree', 'prereq', 'ops', 'changes', 'log']);
const ch = contract('changes-state.schema.json');
const cmds = contract('commands.schema.json');
const bt = (o = {}) => ({ kind: 'branch', from: 'trunk', rev: 42, dirty: false, branches: 'branches', tags: 'tags', error: '', busy: false, ...o });

test('stato branchTag e comandi rispettano gli schemi', () => {
  const s = structuredClone(readJson('src/SVNAssist.UI.Web/contract/fixtures/changes.json'));
  s.branchTag = bt({ dirty: true, error: 'x' });
  validate(ch, s);
  U.ops.btReset(bt());
  validate(cmds, { name: 'branchTagOk', args: U.ops.btArgs(bt()) });
  validate(cmds, { name: 'branchTagOpen', args: { kind: 'tag' } });
});

test('dialogo: destinazione dal layout, OK spento senza nome, working copy solo con modifiche locali', () => {
  U.ops.btReset(bt());
  let h = U.ops.branchTagHTML(bt());
  assert.match(h, /\^\/branches\/…/);
  assert.match(h, /data-bt-ok disabled/);
  assert.doesNotMatch(h, /value="wc"/);
  assert.match(U.ops.branchTagHTML(bt({ dirty: true })), /value="wc"/);
  assert.match(U.ops.branchTagHTML(bt({ branches: 'progetto/branches' })), /\^\/progetto\/branches\//);
  assert.match(U.ops.branchTagHTML(bt({ error: 'esiste già <b>' })), /rp-err[^]*esiste già &lt;b&gt;/);
});

test('messaggio precompilato e argomenti inviati; "wc" senza modifiche locali ricade su head', () => {
  U.ops.btReset(bt());
  assert.equal(U.ops.btMsg(bt()), 'Crea branch … da trunk');
  const a = U.ops.btArgs(bt());
  assert.deepEqual(Object.keys(a).sort(), ['kind', 'message', 'name', 'rev', 'source', 'switch']);
  assert.equal(a.switch, true);
  assert.equal(a.source, 'head');
});

test('in inglese i testi del dialogo sono tradotti', () => {
  U.setLang('en');
  U.ops.btReset(bt());
  assert.equal(U.ops.btMsg(bt()), 'Create branch … from trunk');
  assert.match(U.ops.branchTagHTML(bt()), /Switch to the new branch right away/);
  U.setLang('it');
});
