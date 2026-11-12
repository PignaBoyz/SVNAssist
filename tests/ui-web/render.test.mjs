// Render puri della UI.Web: ARIA dell'albero, escaping, diff affiancato.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readJson, loadUi, read } from './helpers.mjs';

const U = loadUi();
const st = readJson('src/SVNAssist.UI.Web/contract/fixtures/changes.json');

test('albero: role=tree/treeitem, livelli, expanded e chiavi', () => {
  const h = U.treeHTML(st.sections, 'staged:Core/Services');
  assert.equal((h.match(/role="treeitem"/g) || []).length, 8);   // 3 sezioni + 5 righe
  assert.match(h, /aria-level="1"[^>]*aria-expanded="true"[^>]*data-key="section:staged"/);
  assert.match(h, /aria-level="2"[^>]*aria-expanded="true"[^>]*data-key="staged:Core\/Services"/);
  assert.match(h, /aria-level="3"[^>]*data-key="staged:Core\/Services\/OrderService.cs"/);
  assert.equal((h.match(/tabindex="0"/g) || []).length, 1, 'un solo elemento nel tab order (roving tabindex)');
});
test('albero: i file in conflitto non hanno il + e hanno Risolvi', () => {
  const h = U.treeHTML(st.sections, null);
  const row = h.split('data-key="changes:Web/CheckoutController.cs"')[1].split('role="treeitem"')[0];
  assert.ok(!row.includes('Aggiungi ai file da committare'));
});
test('albero: nomi con HTML vengono neutralizzati', () => {
  const evil = structuredClone(st); evil.sections[0].rows[1].name = '<img src=x onerror=alert(1)>'; evil.sections[0].rows[1].key = 'staged:"><script>';
  const h = U.treeHTML(evil.sections, null);
  assert.ok(!h.includes('<img') && !h.includes('<script'));
});
test('diff affiancato: righe, evidenziazione intra-riga, colori sintassi', () => {
  const h = U.diffHTML(readJson('src/SVNAssist.UI.Web/contract/fixtures/diff.json').diff);
  assert.match(h, /class="s del"/); assert.match(h, /class="s add"/); assert.match(h, /<mark>/); assert.match(h, /tk-kw/);
  assert.match(U.diffHTML(''), /Nessuna differenza/);
});
test('CSP: nessuno script o stile inline, nessuna risorsa esterna in index.html', () => {
  const html = read('src/SVNAssist.UI.Web/index.html');
  assert.ok(!/<script(?![^>]*\ssrc=)/i.test(html), 'script inline');
  assert.ok(!/\sstyle=/i.test(html.replace(/<defs>[\s\S]*<\/defs>/, '')), 'attributo style');
  assert.ok(!/https?:\/\//.test(html.replace(/<meta http-equiv[^>]*>/, '').replace(/xmlns="[^"]*"/g, '')), 'URL esterni');
  for (const f of ['util', 'bridge', 'ctxmenu', 'diff', 'tree', 'prereq', 'changes', 'log', 'main']) {
    const js = read(`src/SVNAssist.UI.Web/js/${f}.js`);
    assert.ok(!/\beval\s*\(|new Function|\sstyle="|\.innerHTML\s*=\s*[^;]*\bstyle=/.test(js), f + ': eval/style inline');
  }
});
test('filtro: tiene i file che corrispondono e le loro cartelle, aria-checked su Staged', () => {
  const f = st.sections.flatMap(s => s.rows).filter(r => r.kind === 'file')[0], q = f.name.toUpperCase();
  const sec = st.sections.find(s => s.rows.includes(f));
  const rows = U.filterRows(sec.rows, q);
  assert.ok(rows.some(r => r.key === f.key));
  assert.ok(rows.every(r => r.kind === 'folder' || (r.key + r.name).toLowerCase().includes(q.toLowerCase())));
  assert.equal(U.filterRows(sec.rows, 'zzz-nessuno').length, 0);
  assert.match(U.treeHTML(st.sections, null, 'zzz-nessuno'), /Nessun risultato/);
  assert.match(U.treeHTML(st.sections, null), /aria-checked="true"/);
});
test('contatore Commit (n) e tastiera: Canc/Spazio/Invio inviano i comandi giusti', () => {
  const js = read('src/SVNAssist.UI.Web/js/changes.js') + read('src/SVNAssist.UI.Web/js/tree.js');
  assert.match(js, /<span class="pill">\$\{n\}<\/span>/);
  for (const k of ["'Delete'", "' '", "'Enter'", "'ArrowLeft'", "'ArrowRight'", "'ArrowDown'", "'ArrowUp'"]) assert.ok(js.includes(`case ${k}`), k);
  assert.match(js, /op: 'deleteFile'/);
});
