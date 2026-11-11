'use strict';
/* Vista "SVN Changes". update(root, state) ridisegna a regioni, senza distruggere textarea/focus. */
(function (w) {
  const U = w.SVNUI, esc = U.esc;
  const LANGS = [['auto', 'Auto (lingua del codice)'], ['italiano', 'Italiano'], ['english', 'English']];
  const THEMES = [['auto', 'Auto (segue Visual Studio)'], ['light', 'Chiaro'], ['dark', 'Scuro'], ['blue', 'Blu']];
  let st = null, forceInput = false, timer = 0;

  function header(s) {
    const wc = s.workingCopy, rev = wc.rev ? `<span class="muted">r${wc.rev}</span>` : '';
    return `<div class="branch" title="Branch della working copy: ${esc(wc.path)}">${U.icon('branch')}<b>${esc(wc.branch || '—')}</b>${rev}</div><span class="fill"></span>` +
      U.btn('btn', 'Aggiorna', 'down', 'update', null, 'Scarica le modifiche dal server (svn update)') +
      U.btn('ib', '', 'refresh', 'refresh', null, 'Ricarica l\'elenco delle modifiche (svn status)') +
      `<button type="button" class="ib" data-menu="more" title="Altre azioni: vista, tema, prerequisiti" aria-label="Altre azioni" aria-haspopup="menu">${U.icon('more')}</button>`;
  }
  function wcForm(s) {
    return (s.workingCopy.showInput || forceInput) ? `<form class="wc" id="wcform"><label for="wcp">Working copy</label><input id="wcp" type="text" value="${esc(s.workingCopy.path)}" placeholder="C:\\percorso\\della\\working copy" spellcheck="false"><button class="btn sm" type="submit" title="Carica questa working copy">Apri</button></form>` : '';
  }
  function commitBar(s) {
    const n = (s.sections.find(x => x.id === 'staged') || { count: 0 }).count, c = s.commit, busy = c.busy || s.loading;
    const why = n ? `Committa ${n} ${n === 1 ? 'file' : 'file'} (Ctrl+Invio)` : 'Aggiungi almeno un file dalla lista (+) per committare';
    return `<button type="button" class="btn primary" ${U.cmd('commit')} ${n && !busy ? '' : 'disabled'} title="${esc(why)}">${U.icon('check')}<span>Commit</span>${n ? `<span class="pill">${n}</span>` : ''}</button><span class="fill"></span>` +
      `<button type="button" class="btn ai" ${U.cmd('genAi')} ${c.busy ? 'disabled' : ''} title="Scrive il messaggio dal diff dei file da committare">${c.busy ? '<span class="spin"></span>' : U.icon('spark')}<span>Genera con AI</span></button>` +
      `<button type="button" class="ib" data-menu="ai" ${c.busy ? 'disabled' : ''} title="Migliora il messaggio, lingua" aria-label="Altre opzioni AI" aria-haspopup="menu">${U.icon('chev-d')}</button>`;
  }
  function aiStat(c) {
    let h = c.aiStatus ? `<div class="ai-s" role="status">${esc(c.aiStatus).replace(/\n/g, '<br>')}</div>` : '';
    if (c.download) h += `<div class="dl"><b>GitHub Copilot: primo utilizzo</b> <span class="muted">(runtime ~100 MB, una sola volta)</span><progress max="100" value="${+c.download.pct || 0}"></progress>${esc(c.download.label)}</div>`;
    if (c.trace && c.trace.length) h += `<div class="trace">` + c.trace.map((t, i) => {
      const lab = t.state === 'ok' ? `${t.name}: OK` : t.state === 'fail' ? `${t.name}: errore` : t.state === 'skip' ? `${t.name}: saltato` : t.state === 'run' ? `<span class="spin"></span>${esc(t.name)}…` : esc(t.name);
      return (i ? '<span class="muted">›</span>' : '') + `<span class="chip ${t.state}" title="${esc(t.detail || '')}">${t.state === 'ok' || t.state === 'fail' || t.state === 'skip' ? esc(lab) : lab}</span>`;
    }).join('') + `</div>`;
    return h;
  }
  function foot(s) {
    const p = s.prereq;
    return `<span class="msg ${s.status.err ? 'err' : ''}" role="status">${esc(s.status.msg)}</span><span class="fill"></span>` +
      `<button type="button" class="btn sm ghost ${p.summary && /da sistemare/.test(p.summary) ? 'warn' : ''}" ${U.cmd('prereqToggle')} aria-expanded="${!!p.open}" title="Mostra/nasconde la checklist: svn, TortoiseSVN, provider AI">${esc(p.summary || 'Prerequisiti')}</button>`;
  }
  const isEmpty = s => s.sections.every(x => !x.count);

  function moreMenu() {
    return [
      { header: true, label: 'Vista' },
      { label: 'Albero per cartelle', cmd: 'setView', args: { view: 'tree' }, checked: st.view === 'tree' },
      { label: 'Elenco semplice', cmd: 'setView', args: { view: 'list' }, checked: st.view === 'list' },
      { label: 'Espandi tutto', cmd: 'expandAll' }, { label: 'Comprimi tutto', cmd: 'collapseAll' }, { sep: true },
      { header: true, label: 'Tema della finestra' },
      ...THEMES.map(([m, l]) => ({ label: l, cmd: 'setTheme', args: { mode: m }, checked: st.theme.mode === m })), { sep: true },
      { label: 'Prerequisiti…', cmd: 'prereqToggle' },
      { label: 'Cambia working copy…', run: () => { forceInput = true; render(); const i = document.getElementById('wcp'); if (i) i.focus(); } },
    ];
  }
  function aiMenu() {
    return [{ label: 'Migliora il messaggio scritto', cmd: 'improveAi', hint: 'Riscrive la bozza nel campo sopra' }, { sep: true }, { header: true, label: 'Lingua del messaggio' },
      ...LANGS.map(([l, t]) => ({ label: t, cmd: 'setLanguage', args: { lang: l }, checked: st.commit.lang === l }))];
  }
  function rowMenu(row, x, y) {
    const key = row.dataset.key, kind = row.dataset.kind, sec = row.dataset.sec;
    const s = st.sections.find(z => z.id === sec); const r = s && s.rows.find(z => z.key === key);
    const A = (op, label, can, kbd) => ({ label, cmd: 'rowOp', args: { op, key }, disabled: !can, kbd });
    if (kind === 'section') return U.menu.open(x, y, [{ label: sec === 'staged' ? 'Rimuovi tutto dai file da committare' : 'Aggiungi tutto ai file da committare', cmd: sec === 'staged' ? 'unstage' : 'stage', args: { key } }, { sep: true }, { label: 'Espandi tutto', cmd: 'expandAll' }, { label: 'Comprimi tutto', cmd: 'collapseAll' }], row);
    if (!r) return;
    const c = r.can, isF = kind === 'file';
    U.menu.open(x, y, [
      { label: isF ? 'Mostra differenze' : 'Mostra differenze della cartella', cmd: isF ? 'showDiff' : 'showFolderDiff', args: { key }, disabled: !c.diff, kbd: isF ? 'Invio' : '' },
      c.unstage || sec === 'staged' ? { label: 'Rimuovi dai file da committare', cmd: 'unstage', args: { key }, disabled: !c.unstage, kbd: 'Spazio' } : { label: 'Aggiungi ai file da committare', cmd: 'stage', args: { key }, disabled: !c.stage, kbd: 'Spazio', hint: c.stage ? '' : 'Risolvi prima il conflitto' },
      { sep: true }, A('add', 'Aggiungi a SVN', c.add), A('ignore', 'Aggiungi alla ignore list', c.ignore),
      A('revert', isF ? 'Annulla le modifiche (revert)…' : 'Annulla le modifiche nella cartella…', c.revert), isF ? A('resolve', 'Risolvi conflitto…', c.resolve) : null,
      { sep: true }, isF ? A('deleteFile', 'Elimina file…', c.deleteFile, 'Canc') : { label: 'Espandi tutto', cmd: 'expandAll' }, isF ? null : { label: 'Comprimi tutto', cmd: 'collapseAll' },
    ].filter(Boolean), row);
  }

  function render() {
    if (!st) return;
    const root = document.querySelector('.app'); if (!root) return;
    root.classList.toggle('loading', !!st.loading);
    U.setHTML(root.querySelector('#hdr'), header(st));
    U.setHTML(root.querySelector('#wcbox'), wcForm(st));
    U.setHTML(root.querySelector('#cbar'), commitBar(st));
    U.setHTML(root.querySelector('#aist'), aiStat(st.commit));
    U.setHTML(root.querySelector('#prq'), U.prereqHTML(st.prereq));
    const ta = root.querySelector('#msg');
    if (ta.value !== st.commit.text && document.activeElement !== ta) ta.value = st.commit.text;
    const list = root.querySelector('#list'), had = list.contains(document.activeElement), cur = list.__cursor, top = list.scrollTop;
    const keys = []; st.sections.forEach(s => { keys.push('section:' + s.id); s.rows.forEach(r => keys.push(r.key)); });
    const cursor = keys.includes(cur) ? cur : keys[0];
    const html = isEmpty(st) && !st.loading ? `<div class="empty-state">${U.icon('check', 'big')}<b>Nessuna modifica</b><span class="muted">La working copy è pulita.</span></div>` + '' : '';
    const changed = U.setHTML(list, (html ? html : '') + U.treeHTML(st.sections.filter(s => s.id === 'staged' || s.count), cursor));
    if (changed) { list.scrollTop = top; if (had) { const r = list.querySelector(`.tr[data-key="${CSS.escape(cursor)}"]`); if (r) r.focus(); } }
    list.setAttribute('aria-busy', !!st.loading);
    U.setHTML(root.querySelector('#foot'), foot(st));
  }

  U.views.changes = {
    mount(root) {
      root.innerHTML = `<div class="app"><div class="prog" aria-hidden="true"></div><header class="bar" id="hdr"></header><div id="wcbox"></div>
        <section class="commit"><textarea id="msg" rows="3" aria-label="Messaggio di commit" placeholder="Messaggio di commit (Ctrl+Invio per committare)"></textarea>
        <div class="bar" id="cbar"></div><div id="aist"></div></section><div id="prq"></div>
        <div class="list" id="list" role="tree" aria-label="Modifiche della working copy"></div>
        <footer class="foot" id="foot"></footer></div>`;
      const ta = root.querySelector('#msg');
      ta.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(() => U.bridge.send('setCommitText', { text: ta.value }), 150); });
      ta.addEventListener('keydown', e => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); clearTimeout(timer); U.bridge.send('setCommitText', { text: ta.value }); U.bridge.send('commit'); } });
      root.addEventListener('submit', e => { if (e.target.id === 'wcform') { e.preventDefault(); forceInput = false; U.bridge.send('setWorkingCopy', { path: e.target.querySelector('input').value }); } });
      root.addEventListener('click', e => {
        const b = e.target.closest('[data-menu]'); if (!b || b.disabled) return;
        const r = b.getBoundingClientRect(); U.menu.open(Math.max(0, r.right - 220), r.bottom + 2, b.dataset.menu === 'ai' ? aiMenu() : moreMenu(), b);
      });
      U.bindTree(root.querySelector('#list'), { rowMenu });
    },
    update(root, state) { st = state; render(); },
  };
})(window);
