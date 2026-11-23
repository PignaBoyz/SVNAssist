'use strict';
/* Albero delle modifiche (Staged / Modifiche / Non versionati). ARIA tree "piatto": ogni riga e' role=treeitem con aria-level.
   treeHTML(sections, cursor) e' puro. Tastiera in bindTree(). */
(function (w) {
  const U = w.SVNUI, t = U.t;
  const SEC_ACTS = () => ({   // azioni di gruppo visibili sulla riga di sezione
    staged: [['minus', 'unstage', t('Rimuovi tutto dai file da committare')]],
    changes: [['plus', 'stage', t('Aggiungi tutto ai file da committare')]],
    unversioned: [['plus', 'stage', t('Aggiungi tutto ai file da committare')]],
  });
  const guides = depth => '<span class="gd"></span>'.repeat(Math.max(0, depth));
  function rowActs(sec, r) {
    const a = [], k = { key: r.key };
    if (r.kind === 'file' && r.can.diff) a.push([r.status === 'A' || r.status === '?' ? 'file' : 'diff', 'showDiff', k, r.status === 'A' || r.status === '?' ? t("Apri il file nell'editor (Invio)") : t("Mostra le differenze nell'editor (Invio)")]);
    if (r.kind === 'folder' && r.can.diff) a.push(['diff', 'showFolderDiff', k, t('Mostra le differenze della cartella')]);
    if (sec !== 'staged' && r.can.revert) a.push(['undo', 'rowOp', { op: 'revert', key: r.key }, t('Annulla le modifiche (revert)')]);
    if (r.can.unstage) a.push(['minus', 'unstage', k, t('Rimuovi dai file da committare (Spazio)')]);
    if (r.can.stage) a.push(['plus', 'stage', k, t('Aggiungi ai file da committare (Spazio)')]);
    return a.map(([i, c, g, t]) => `<button type="button" class="ib" tabindex="-1" ${U.cmd(c, g)} title="${U.esc(t)}" aria-label="${U.esc(t)}">${U.icon(i)}</button>`).join('');
  }
  // filtro testuale: tiene i file il cui percorso contiene `q` e le cartelle che ne hanno almeno uno sotto (righe piatte, depth crescente)
  function filterRows(rows, q) {
    q = (q || '').trim().toLowerCase(); if (!q) return rows;
    const keep = rows.map(r => r.kind === 'file' && (r.key.slice(r.key.indexOf(':') + 1) + ' ' + r.name).toLowerCase().includes(q));
    for (let i = rows.length - 1; i >= 0; i--) if (keep[i]) for (let j = i - 1; j >= 0; j--) if (rows[j].depth < rows[i].depth && rows[j].kind === 'folder') { keep[j] = true; if (rows[j].depth === 0) break; }
    return rows.filter((r, i) => keep[i]);
  }
  U.filterRows = filterRows;
  const ROW_CAP = 300;
  function sectionHTML(s, cursor, q, sel, expandedCaps) {
    const key = 'section:' + s.id, open = s.expanded !== false;
    let h = `<div class="tr sec" role="treeitem" aria-level="1" aria-expanded="${open}" data-key="${U.esc(key)}" data-kind="section" data-sec="${s.id}" tabindex="${cursor === key ? 0 : -1}">` +
      `<span class="chev">${U.icon(open ? 'chev-d' : 'chev-r')}</span><span class="nm">${U.esc(s.title)}</span><span class="cnt" title="${U.esc(t('{0} elementi', s.count))}">${s.count}</span><span class="fill"></span>` +
      `<span class="acts">${s.count ? (SEC_ACTS()[s.id] || []).map(([i, c, t]) => `<button type="button" class="ib" tabindex="-1" ${U.cmd(c, { key })} title="${U.esc(t)}" aria-label="${U.esc(t)}">${U.icon(i)}</button>`).join('') : ''}</span></div>`;
    if (!open) return h;
    const rows = filterRows(s.rows, q);
    if (!rows.length) return h + `<div class="tr empty" role="none">${q ? t('Nessun risultato.') : s.id === 'staged' ? t('Nessun file da committare. Usa + sulle modifiche per aggiungerli qui.') : t('Nessun elemento.')}</div>`;
    const truncated = !q && rows.length > ROW_CAP && !(expandedCaps && expandedCaps.has(s.id));
    const shown = truncated ? rows.slice(0, ROW_CAP) : rows;
    for (const r of shown) {
      const lvl = r.depth + 2, isF = r.kind === 'folder', st = r.status;
      h += `<div class="tr ${isF ? 'fol' : 'fil'}" role="treeitem" aria-level="${lvl}" ${isF ? `aria-expanded="${!!r.expanded}"` : ''} aria-selected="${sel && sel.size ? sel.has(r.key) : cursor === r.key}" aria-checked="${s.id === 'staged'}" data-key="${U.esc(r.key)}" data-kind="${r.kind}" data-sec="${s.id}" tabindex="${cursor === r.key ? 0 : -1}" title="${U.esc(r.tooltip || r.name)}">` +
        guides(r.depth + 1) + `<span class="chev">${isF ? U.icon(r.expanded ? 'chev-d' : 'chev-r') : ''}</span>` +
        U.icon(isF || r.dir ? (r.expanded ? 'folder-open' : 'folder') : 'file', 'fi ' + (isF || r.dir ? 'fld' : (st ? U.stClass(st) : ''))) +
        `<span class="nm">${U.esc(r.name)}</span>${r.lockedBy ? `<span title="${U.esc(r.lockedBy)}">${U.icon('lock', 'lk')}</span>` : r.locked && st !== 'K' ? U.icon('lock', 'lk') : ''}${r.detail ? `<span class="dt">${U.esc(r.detail)}</span>` : ''}${isF ? `<span class="cnt">${r.count}</span>` : ''}<span class="fill"></span>` +
        `<span class="acts">${rowActs(s.id, r)}</span>${st ? `<span class="st ${U.stClass(st)}" title="${U.esc(r.statusText || U.statusName(st))}" aria-label="${U.esc(r.statusText || U.statusName(st))}">${U.esc(st)}</span>` : ''}</div>`;
    }
    if (truncated)
      h += `<div class="tr more" role="none"><button type="button" class="btn sm ghost" data-more-sec="${U.esc(s.id)}">${U.esc(t('Mostra tutti ({0} nascosti)…', rows.length - ROW_CAP))}</button></div>`;
    return h;
  }
  U.treeHTML = (sections, cursor, q, sel, expandedCaps) => sections.map(s => sectionHTML(s, cursor, q, sel, expandedCaps)).join('');

  // ---- interazione ----
  const rowsOf = list => [...list.querySelectorAll('.tr[role=treeitem]')];
  const level = r => +r.getAttribute('aria-level');
  // Selezione multipla: list.__sel (Set di chiavi di file/cartelle, mai di sezioni) e list.__anchor (punto di partenza di Shift). Resta nella UI e sopravvive agli snapshot (syncSel).
  const selectable = r => r.dataset.kind !== 'section';
  function paint(list) { const sel = list.__sel || (list.__sel = new Set()); rowsOf(list).forEach(x => x.setAttribute('aria-selected', String(sel.size ? sel.has(x.dataset.key) : x.dataset.key === list.__cursor))); }
  // mode: 'one' (clic semplice), 'add' (Ctrl: alterna), 'range' (Shift: dall'ancora a r)
  function focusRow(list, r, mode) {
    if (!r) return; const sel = list.__sel || (list.__sel = new Set()), rs = rowsOf(list), k = r.dataset.key;
    rs.forEach(x => { x.tabIndex = -1; }); r.tabIndex = 0;
    if (mode === 'range' && selectable(r)) {
      const a = rs.findIndex(x => x.dataset.key === list.__anchor), b = rs.indexOf(r); sel.clear();
      if (a < 0) sel.add(k); else rs.slice(Math.min(a, b), Math.max(a, b) + 1).filter(selectable).forEach(x => sel.add(x.dataset.key));
    } else if (mode === 'add' && selectable(r)) { if (sel.has(k)) sel.delete(k); else sel.add(k); list.__anchor = k; }
    else if (mode !== 'range' && mode !== 'add') { sel.clear(); if (selectable(r)) sel.add(k); list.__anchor = k; }
    list.__cursor = k; paint(list); r.focus(); r.scrollIntoView({ block: 'nearest' });
  }
  U.selection = list => rowsOf(list).filter(x => selectable(x) && (list.__sel || new Set()).has(x.dataset.key)).map(x => x.dataset.key);
  // dopo uno snapshot: toglie le chiavi sparite; un file passato da una sezione all'altra (Staged <-> Modifiche) resta selezionato col nuovo prefisso. Ritorna il rimappatore.
  U.syncSel = (list, keys) => {
    const have = new Set(keys), byPath = new Map(), pth = k => k.slice(k.indexOf(':') + 1);
    keys.forEach(k => { if (!k.startsWith('section:') && !byPath.has(pth(k))) byPath.set(pth(k), k); });
    const m = k => k == null ? k : have.has(k) ? k : k.startsWith('section:') ? undefined : byPath.get(pth(k));
    const sel = new Set(); for (const k of list.__sel || []) { const n = m(k); if (n) sel.add(n); }
    list.__sel = sel; list.__anchor = m(list.__anchor); return m;
  };
  U.bindTree = function (list, ctx) {   // ctx.rowMenu(row, x, y) apre il menu contestuale
    list.setAttribute('aria-multiselectable', 'true'); list.__sel = new Set();
    list.addEventListener('mousedown', e => { if (e.shiftKey) e.preventDefault(); });   // niente selezione di testo con Shift+clic
    const toggleCmd = r => r.dataset.kind === 'section' ? ['toggleSection', { sec: r.dataset.sec }] : ['toggleFolder', { key: r.dataset.key }];
    list.addEventListener('click', e => {
      const r = e.target.closest('.tr[role=treeitem]'); if (!r || e.target.closest('.ib')) return;
      const mod = e.ctrlKey || e.metaKey ? 'add' : e.shiftKey ? 'range' : 'one';
      focusRow(list, r, mod);
      if (mod === 'one' && r.dataset.kind !== 'file' && e.target.closest('.chev, .nm, .cnt')) U.bridge.send(...toggleCmd(r));
    });
    list.addEventListener('dblclick', e => {
      const r = e.target.closest('.tr[role=treeitem]'); if (!r || e.target.closest('.ib')) return;
      if (r.dataset.kind === 'file') U.bridge.send('showDiff', { key: r.dataset.key });
      else if (r.dataset.kind === 'folder') U.bridge.send('showFolderDiff', { key: r.dataset.key });
    });
    list.addEventListener('contextmenu', e => { const r = e.target.closest('.tr[role=treeitem]'); if (!r) return; e.preventDefault(); if (!(list.__sel && list.__sel.has(r.dataset.key))) focusRow(list, r); ctx.rowMenu(r, e.clientX, e.clientY); });
    list.addEventListener('keydown', e => {
      const r = e.target.closest('.tr[role=treeitem]'); if (!r || e.altKey || e.metaKey) return;
      const rs = rowsOf(list), i = rs.indexOf(r), kind = r.dataset.kind, open = r.getAttribute('aria-expanded');
      if (e.ctrlKey) {   // Ctrl+A tutto, Ctrl+Spazio alterna la riga
        if (e.key.toLowerCase() === 'a') { e.preventDefault(); const sel = list.__sel; sel.clear(); rs.filter(selectable).forEach(x => sel.add(x.dataset.key)); paint(list); }
        else if (e.key === ' ') { e.preventDefault(); focusRow(list, r, 'add'); }
        return;
      }
      const go = x => { e.preventDefault(); focusRow(list, x, e.shiftKey ? 'range' : 'one'); };
      switch (e.key) {
        case 'ArrowDown': return go(rs[Math.min(rs.length - 1, i + 1)]);
        case 'ArrowUp': return go(rs[Math.max(0, i - 1)]);
        case 'Home': return go(rs[0]);
        case 'End': return go(rs[rs.length - 1]);
        case 'ArrowRight':
          e.preventDefault();
          if (open === 'false') U.bridge.send(...toggleCmd(r)); else if (open === 'true' && rs[i + 1] && level(rs[i + 1]) > level(r)) focusRow(list, rs[i + 1]);
          return;
        case 'ArrowLeft':
          e.preventDefault();
          if (open === 'true') U.bridge.send(...toggleCmd(r));
          else { for (let j = i - 1; j >= 0; j--) if (level(rs[j]) < level(r)) { focusRow(list, rs[j]); break; } }
          return;
        case 'Enter': e.preventDefault();
          if (kind === 'file') U.bridge.send('showDiff', { key: r.dataset.key }); else U.bridge.send(...toggleCmd(r)); return;
        case ' ': { e.preventDefault(); const ks = U.selection(list);
          if (ks.length) U.bridge.send(ks.every(k => k.startsWith('staged:')) ? 'unstage' : 'stage', { keys: ks }); else U.bridge.send(r.dataset.sec === 'staged' ? 'unstage' : 'stage', { key: r.dataset.key }); return; }
        case 'Delete': if (kind !== 'section') { e.preventDefault(); const ks = U.selection(list); U.bridge.send('rowOp', ks.length > 1 ? { op: 'deleteFile', keys: ks } : { op: 'deleteFile', key: r.dataset.key }); } return;   // come Git Changes: conferma a cura dell'host
        case 'ContextMenu': { e.preventDefault(); const b = r.getBoundingClientRect(); ctx.rowMenu(r, b.left + 24, b.bottom); return; }
        case 'F10': if (e.shiftKey) { e.preventDefault(); const b = r.getBoundingClientRect(); ctx.rowMenu(r, b.left + 24, b.bottom); } return;
      }
    });
  };
  U.focusRow = focusRow;
})(window);
