'use strict';
/* Vista "SVN Changes". update(root, state) ridisegna a regioni, senza distruggere textarea/focus. */
(function (w) {
  const U = w.SVNUI, esc = U.esc;
  const LANGS = [['auto', 'Auto (lingua del codice)'], ['italiano', 'Italiano'], ['english', 'English']];
  const THEMES = [['auto', 'Auto (segue Visual Studio)'], ['light', 'Chiaro'], ['dark', 'Scuro'], ['blue', 'Blu']];
  let st = null, forceInput = false, timer = 0, filter = '';

  function header(s) {
    const wc = s.workingCopy, rev = wc.rev ? `<span class="muted">r${wc.rev}</span>` : '';
    return `<div class="branch" title="Branch della working copy: ${esc(wc.path)}">${U.icon('branch')}<b>${esc(wc.branch || '—')}</b>${rev}</div><span class="fill"></span>` +
      U.btn('btn', 'SVN Update', 'down', 'update', null, 'Scarica le modifiche dal server (svn update)') +
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
      `<button type="button" class="btn ai" ${U.cmd('genAi')} ${c.busy ? 'disabled aria-busy="true"' : ''} title="Scrive il messaggio dal diff dei file da committare">${c.busy ? '<span class="spin"></span>' : U.icon('spark')}<span>${c.busy ? 'Generazione in corso…' : 'Genera con AI'}</span></button>` +
      `<button type="button" class="ib" data-menu="ai" ${c.busy ? 'disabled' : ''} title="Migliora il messaggio, lingua" aria-label="Altre opzioni AI" aria-haspopup="menu">${U.icon('chev-d')}</button>`;
  }
  function aiStat(c) {
    // Ogni messaggio ha colore e icona per gravita' (aviso/errore/esito); data-seq cambia a ogni messaggio, cosi' anche uno ripetuto si ridisegna e lampeggia.
    const lv = c.aiLevel || 'info', ic = lv === 'busy' ? '<span class="spin"></span>' : lv === 'ok' ? U.icon('check') : lv === 'warn' ? U.icon('warn') : lv === 'err' ? U.icon('x') : '';
    let h = c.aiStatus ? `<div class="ai-s lv-${lv}" role="${lv === 'warn' || lv === 'err' ? 'alert' : 'status'}" data-seq="${+c.aiSeq || 0}">${ic}<span>${esc(c.aiStatus).replace(/\n/g, '<br>')}</span></div>` : '';
    if (c.download) h += `<div class="dl"><b>GitHub Copilot: primo utilizzo</b> <span class="muted">(runtime ~100 MB, una sola volta)</span><progress max="100" value="${+c.download.pct || 0}"></progress>${esc(c.download.label)}</div>`;
    if (c.trace && c.trace.length) h += `<div class="trace">` + c.trace.map((t, i) => {
      const lab = t.state === 'ok' ? `${t.name}: OK` : t.state === 'fail' ? `${t.name}: errore` : t.state === 'skip' ? `${t.name}: saltato` : t.state === 'run' ? `<span class="spin"></span>${esc(t.name)}…` : esc(t.name);
      return (i ? '<span class="muted">›</span>' : '') + `<span class="chip ${t.state}" title="${esc(t.detail || '')}">${t.state === 'ok' || t.state === 'fail' || t.state === 'skip' ? esc(lab) : lab}</span>`;
    }).join('') + `</div>`;
    return h;
  }
  function foot(s) {
    const p = s.prereq, bad = !!(p.summary && /da sistemare/.test(p.summary)), m = s.status.msg || '';
    // Esito: icona + colore + semibold (stessa dimensione del testo); i messaggi di sola informazione restano normali.
    const kind = s.status.err ? 'err' : /(completat|aggiornat|annullat|aggiunt|eliminat|risolt|ignorat|salvat)/i.test(m) ? 'ok' : '';
    const act = s.status.action;
    return `<span class="msg ${kind}" role="status">${kind ? U.icon(kind === 'err' ? 'x' : 'check') : ''}<span>${esc(m)}</span></span>` +
      (act ? `<button type="button" class="btn sm" ${U.cmd(act.cmd)} title="Esegue svn cleanup sulla working copy">${esc(act.label)}</button>` : '') + `<span class="fill"></span>` +
      (bad ? `<button type="button" class="btn sm prq-st bad" ${U.cmd('wizardOpenPrereq')} title="Manca qualcosa: apre la procedura guidata per sistemarlo">${U.icon('warn')}<span>${esc(p.summary)}</span></button>`
        : `<button type="button" class="btn sm prq-st ok" ${U.cmd('prereqToggle')} aria-expanded="${!!p.open}" title="Tutto a posto. Mostra/nasconde la checklist: svn, TortoiseSVN, provider AI">${U.icon('check')}<span>${esc((p.summary || 'Prerequisiti').replace(/\s*✓/, ''))}</span></button>`);
  }
  const isEmpty = s => s.sections.every(x => !x.count);

  function moreMenu() {
    return [
      { header: true, label: 'SVN' },
      { label: 'SVN Update to revision…', cmd: 'updateToRevision', hint: 'Aggiorna a una revisione precisa (svn update -r)' },
      { label: 'SVN Cleanup', cmd: 'cleanup', hint: 'Ripulisce la working copy da un\'operazione interrotta (svn cleanup)' }, { sep: true },
      { header: true, label: 'Vista' },
      { label: 'Albero per cartelle', cmd: 'setView', args: { view: 'tree' }, checked: st.view === 'tree' },
      { label: 'Elenco semplice', cmd: 'setView', args: { view: 'list' }, checked: st.view === 'list' },
      { label: 'Espandi tutto', cmd: 'expandAll' }, { label: 'Comprimi tutto', cmd: 'collapseAll' }, { sep: true },
      { header: true, label: 'Tema della finestra' },
      ...THEMES.map(([m, l]) => ({ label: l, cmd: 'setTheme', args: { mode: m }, checked: st.theme.mode === m })), { sep: true },
      { label: 'Cambia provider AI…', cmd: 'wizardOpen' },
      { label: 'Prerequisiti…', cmd: 'prereqToggle' },
      { label: 'Cambia working copy…', run: () => { forceInput = true; render(); const i = document.getElementById('wcp'); if (i) i.focus(); } },
    ];
  }
  function aiMenu() {
    return [{ label: 'Migliora il messaggio scritto', cmd: 'improveAi', hint: 'Riscrive la bozza nel campo sopra' }, { label: 'Cambia provider AI…', cmd: 'wizardOpen', hint: 'Procedura guidata: provider, accesso, modello' }, { sep: true }, { header: true, label: 'Lingua del messaggio' },
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
      { sep: true }, A('add', 'SVN Add (aggiungi al controllo di versione)', c.add), A('ignore', 'Aggiungi alla ignore list', c.ignore),
      A('revert', isF ? 'SVN Revert (annulla le modifiche)…' : 'SVN Revert della cartella (annulla le modifiche)…', c.revert), isF ? A('resolve', 'Risolvi conflitto…', c.resolve) : null,
      isF && (c.lock || c.unlock) ? { sep: true } : null, isF && c.lock ? A('lock', 'SVN Lock…', true) : null, isF && c.unlock ? A('unlock', 'SVN Unlock', true) : null,
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
    const changed = U.setHTML(list, (html ? html : '') + U.treeHTML(st.sections.filter(s => s.id === 'staged' || s.count), cursor, filter));
    if (changed) { list.scrollTop = top; if (had) { const r = list.querySelector(`.tr[data-key="${CSS.escape(cursor)}"]`); if (r) r.focus(); } }
    list.setAttribute('aria-busy', !!st.loading);
    U.setHTML(root.querySelector('#foot'), foot(st));
    renderWizard(root);
  }

  // Procedura guidata: dialogo modale (il resto e' inert), il testo digitato e il focus sopravvivono ai ridisegni.
  let wizTimer = 0;
  function wizNeedsPolling(wz) {
    return !!(wz && wz.open && !wz.busy && (wz.mode === 'prereq' ? !wz.canNext : (wz.step === 2 && !wz.canNext) || (wz.step === 3 && (wz.auth === 'github' || wz.auth === 'openai') && !wz.authOk)));
  }
  function renderWizard(app) {
    const box = document.getElementById('wiz'), wz = st.wizard; if (!box) return;
    const open = !!(wz && wz.open), was = !!box.__open, keep = {};
    box.querySelectorAll('input,select').forEach(e => { keep[e.id] = e.value; });
    const sameStep = box.__step === (wz && wz.step) && box.__mode === (wz && wz.mode);
    if (open && !was) box.__prev = document.activeElement;
    const changed = U.setHTML(box, U.wizardHTML(wz));
    if (changed) box.querySelectorAll('input,select').forEach(e => { if (keep[e.id] != null && sameStep) e.value = keep[e.id]; });
    app.classList.toggle('modal-open', open); app.inert = open;
    if (open && (!was || !sameStep || !box.contains(document.activeElement))) { const t = box.querySelector(!was || !sameStep ? '#wiz-t' : '.wiz [data-cmd]:not(:disabled),.wiz input,.wiz select'); if (t) t.focus(); }
    if (!open && was && box.__prev && box.__prev.isConnected) box.__prev.focus();
    box.__open = open; box.__step = wz && wz.step; box.__mode = wz && wz.mode;
    const need = wizNeedsPolling(wz);   // ricontrollo leggero finche' manca qualcosa (il terminale e' fuori dalla pagina)
    if (need && !wizTimer) wizTimer = setInterval(() => U.bridge.send('wizardRecheck'), 4000);
    else if (!need && wizTimer) { clearInterval(wizTimer); wizTimer = 0; }
  }
  function wizKeys(e) {
    const box = document.getElementById('wiz'); if (!box || !box.__open) return;
    if (e.key === 'Escape') { e.preventDefault(); U.bridge.send('wizardCancel'); return; }
    if (e.key !== 'Tab') return;
    const f = [...box.querySelectorAll('button:not(:disabled),input,select,[tabindex="0"]')]; if (!f.length) return;
    const a = document.activeElement, first = f[0], last = f[f.length - 1];
    if (e.shiftKey && (a === first || !box.contains(a) || a.id === 'wiz-t')) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && (a === last || !box.contains(a))) { e.preventDefault(); first.focus(); }
  }

  U.views.changes = {
    mount(root) {
      root.innerHTML = `<div class="app"><div class="prog" aria-hidden="true"></div><header class="bar" id="hdr"></header><div id="wcbox"></div>
        <section class="commit"><textarea id="msg" rows="3" aria-label="Messaggio di commit" placeholder="Messaggio di commit (Ctrl+Invio per committare)"></textarea>
        <div class="bar" id="cbar"></div><div id="aist"></div></section><div id="prq"></div>
        <div class="flt"><input id="flt" type="search" placeholder="Filtra i file…" aria-label="Filtra i file per nome" spellcheck="false" autocomplete="off"></div>
        <div class="list" id="list" role="tree" aria-label="Modifiche della working copy"></div>
        <footer class="foot" id="foot"></footer></div><div id="wiz"></div>`;
      const ta = root.querySelector('#msg');
      ta.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(() => U.bridge.send('setCommitText', { text: ta.value }), 150); });
      ta.addEventListener('keydown', e => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); clearTimeout(timer); U.bridge.send('setCommitText', { text: ta.value }); U.bridge.send('commit'); } });
      root.addEventListener('keydown', wizKeys);
      w.addEventListener('focus', () => { if (wizNeedsPolling(st && st.wizard)) U.bridge.send('wizardRecheck'); });   // di ritorno dal terminale/browser
      root.addEventListener('click', e => {
        const b = e.target.closest('[data-wiz="finish"]'); if (!b || b.disabled) return;
        const sel = root.querySelector('#wiz-model'); U.bridge.send('wizardFinish', { model: sel ? sel.value : '' });
      });
      root.addEventListener('submit', e => {
        if (e.target.id !== 'wiz-form') return;
        e.preventDefault(); const ep = e.target.querySelector('#wiz-ep'), k = e.target.querySelector('#wiz-key');
        U.bridge.send('wizardSetAuth', { endpoint: ep ? ep.value : '', key: k ? k.value : '' }); if (k) k.value = '';
      });
      root.addEventListener('submit', e => { if (e.target.id === 'wcform') { e.preventDefault(); forceInput = false; U.bridge.send('setWorkingCopy', { path: e.target.querySelector('input').value }); } });
      root.addEventListener('click', e => {
        const b = e.target.closest('[data-menu]'); if (!b || b.disabled) return;
        const r = b.getBoundingClientRect(); U.menu.open(Math.max(0, r.right - 220), r.bottom + 2, b.dataset.menu === 'ai' ? aiMenu() : moreMenu(), b);
      });
      const fl = root.querySelector('#flt');
      fl.addEventListener('input', () => { filter = fl.value; render(); });
      fl.addEventListener('keydown', e => { if (e.key === 'Escape' && fl.value) { e.preventDefault(); fl.value = filter = ''; render(); } else if (e.key === 'ArrowDown') { e.preventDefault(); const r = root.querySelector('#list .tr[tabindex="0"]'); if (r) r.focus(); } });
      U.bindTree(root.querySelector('#list'), { rowMenu });
    },
    update(root, state) { st = state; render(); },
  };
})(window);
