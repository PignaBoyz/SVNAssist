'use strict';
/* Albero delle modifiche (Staged / Modifiche / Non versionati). ARIA tree "piatto": ogni riga e' role=treeitem con aria-level.
   treeHTML(sections, cursor) e' puro. Tastiera in bindTree(). */
(function (w) {
  const U = w.SVNUI;
  const SEC_ACTS = {   // azioni di gruppo visibili sulla riga di sezione
    staged: [['minus', 'unstage', 'Rimuovi tutto dai file da committare']],
    changes: [['plus', 'stage', 'Aggiungi tutto ai file da committare']],
    unversioned: [['plus', 'stage', 'Aggiungi tutto ai file da committare']],
  };
  const guides = depth => '<span class="gd"></span>'.repeat(Math.max(0, depth));
  function rowActs(sec, r) {
    const a = [], k = { key: r.key };
    if (r.kind === 'file' && r.can.diff) a.push(['diff', 'showDiff', k, 'Mostra le differenze nell\'editor (Invio)']);
    if (r.kind === 'folder' && r.can.diff) a.push(['diff', 'showFolderDiff', k, 'Mostra le differenze della cartella']);
    if (sec !== 'staged' && r.can.revert) a.push(['undo', 'rowOp', { op: 'revert', key: r.key }, 'Annulla le modifiche (revert)']);
    if (r.can.unstage) a.push(['minus', 'unstage', k, 'Rimuovi dai file da committare (Spazio)']);
    if (r.can.stage) a.push(['plus', 'stage', k, 'Aggiungi ai file da committare (Spazio)']);
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
  function sectionHTML(s, cursor, q) {
    const key = 'section:' + s.id, open = s.expanded !== false;
    let h = `<div class="tr sec" role="treeitem" aria-level="1" aria-expanded="${open}" data-key="${U.esc(key)}" data-kind="section" data-sec="${s.id}" tabindex="${cursor === key ? 0 : -1}">` +
      `<span class="chev">${U.icon(open ? 'chev-d' : 'chev-r')}</span><span class="nm">${U.esc(s.title)}</span><span class="cnt" title="${s.count} elementi">${s.count}</span><span class="fill"></span>` +
      `<span class="acts">${s.count ? (SEC_ACTS[s.id] || []).map(([i, c, t]) => `<button type="button" class="ib" tabindex="-1" ${U.cmd(c, { key })} title="${U.esc(t)}" aria-label="${U.esc(t)}">${U.icon(i)}</button>`).join('') : ''}</span></div>`;
    if (!open) return h;
    const rows = filterRows(s.rows, q);
    if (!rows.length) return h + `<div class="tr empty" role="none">${q ? 'Nessun risultato.' : s.id === 'staged' ? 'Nessun file da committare. Usa + sulle modifiche per aggiungerli qui.' : 'Nessun elemento.'}</div>`;
    for (const r of rows) {
      const lvl = r.depth + 2, isF = r.kind === 'folder', st = r.status;
      h += `<div class="tr ${isF ? 'fol' : 'fil'}" role="treeitem" aria-level="${lvl}" ${isF ? `aria-expanded="${!!r.expanded}"` : ''} aria-selected="${cursor === r.key}" aria-checked="${s.id === 'staged'}" data-key="${U.esc(r.key)}" data-kind="${r.kind}" data-sec="${s.id}" tabindex="${cursor === r.key ? 0 : -1}" title="${U.esc(r.tooltip || r.name)}">` +
        guides(r.depth + 1) + `<span class="chev">${isF ? U.icon(r.expanded ? 'chev-d' : 'chev-r') : ''}</span>` +
        U.icon(isF || r.dir ? (r.expanded ? 'folder-open' : 'folder') : 'file', 'fi ' + (isF || r.dir ? 'fld' : (st ? U.stClass(st) : ''))) +
        `<span class="nm">${U.esc(r.name)}</span>${r.detail ? `<span class="dt">${U.esc(r.detail)}</span>` : ''}${isF ? `<span class="cnt">${r.count}</span>` : ''}<span class="fill"></span>` +
        `<span class="acts">${rowActs(s.id, r)}</span>${st ? `<span class="st ${U.stClass(st)}" title="${U.esc(r.statusText || U.STATUS[st] || st)}" aria-label="${U.esc(r.statusText || U.STATUS[st] || st)}">${U.esc(st)}</span>` : ''}</div>`;
    }
    return h;
  }
  U.treeHTML = (sections, cursor, q) => sections.map(s => sectionHTML(s, cursor, q)).join('');

  // ---- interazione ----
  const rowsOf = list => [...list.querySelectorAll('.tr[role=treeitem]')];
  const level = r => +r.getAttribute('aria-level');
  function focusRow(list, r) { if (!r) return; rowsOf(list).forEach(x => { x.tabIndex = -1; x.setAttribute('aria-selected', 'false'); }); r.tabIndex = 0; r.setAttribute('aria-selected', 'true'); r.focus(); r.scrollIntoView({ block: 'nearest' }); list.__cursor = r.dataset.key; }
  U.bindTree = function (list, ctx) {   // ctx.rowMenu(row, x, y) apre il menu contestuale
    const toggleCmd = r => r.dataset.kind === 'section' ? ['toggleSection', { sec: r.dataset.sec }] : ['toggleFolder', { key: r.dataset.key }];
    list.addEventListener('click', e => {
      const r = e.target.closest('.tr[role=treeitem]'); if (!r || e.target.closest('.ib')) return;
      focusRow(list, r);
      if (r.dataset.kind !== 'file' && e.target.closest('.chev, .nm, .cnt')) U.bridge.send(...toggleCmd(r));
    });
    list.addEventListener('dblclick', e => {
      const r = e.target.closest('.tr[role=treeitem]'); if (!r || e.target.closest('.ib')) return;
      if (r.dataset.kind === 'file') U.bridge.send('showDiff', { key: r.dataset.key });
      else if (r.dataset.kind === 'folder') U.bridge.send('showFolderDiff', { key: r.dataset.key });
    });
    list.addEventListener('contextmenu', e => { const r = e.target.closest('.tr[role=treeitem]'); if (!r) return; e.preventDefault(); focusRow(list, r); ctx.rowMenu(r, e.clientX, e.clientY); });
    list.addEventListener('keydown', e => {
      const r = e.target.closest('.tr[role=treeitem]'); if (!r || e.altKey || e.ctrlKey || e.metaKey) return;
      const rs = rowsOf(list), i = rs.indexOf(r), kind = r.dataset.kind, open = r.getAttribute('aria-expanded');
      const go = x => { e.preventDefault(); focusRow(list, x); };
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
        case ' ': e.preventDefault(); U.bridge.send(r.dataset.sec === 'staged' ? 'unstage' : 'stage', { key: r.dataset.key }); return;
        case 'Delete': if (kind === 'file') { e.preventDefault(); U.bridge.send('rowOp', { op: 'deleteFile', key: r.dataset.key }); } return;   // come Git Changes: conferma a cura dell'host
        case 'ContextMenu': { e.preventDefault(); const b = r.getBoundingClientRect(); ctx.rowMenu(r, b.left + 24, b.bottom); return; }
        case 'F10': if (e.shiftKey) { e.preventDefault(); const b = r.getBoundingClientRect(); ctx.rowMenu(r, b.left + 24, b.bottom); } return;
      }
    });
  };
  U.focusRow = focusRow;
})(window);
