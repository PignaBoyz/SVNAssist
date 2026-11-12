'use strict';
/* Vista "SVN Changes". update(root, state) ridisegna a regioni, senza distruggere textarea/focus. */
(function (w) {
  const U = w.SVNUI, esc = U.esc;
  const t = U.t;
  const LANGS = () => [['auto', t('Auto (lingua del codice)')], ['italiano', 'Italiano'], ['english', 'English']];
  const THEMES = () => [['auto', t('Auto (segue Visual Studio)')], ['light', t('Chiaro')], ['dark', t('Scuro')], ['blue', t('Blu')]];
  let st = null, forceInput = false, timer = 0, filter = '';

  function header(s) {
    const wc = s.workingCopy, rev = wc.rev ? `<span class="muted">r${wc.rev}</span>` : '';
    return `<button type="button" class="branch" ${U.cmd('switchOpen')} title="${esc(t('SVN Switch…: cambia branch o tag (ramo attuale: {0})', wc.branch || '—'))}" aria-label="${esc(t('SVN Switch…: cambia branch o tag (ramo attuale: {0})', wc.branch || '—'))}">${U.icon('branch')}<b>${esc(wc.branch || '—')}</b>${rev}${U.icon('chev-d')}</button><span class="fill"></span>` +
      U.btn('btn', 'SVN Update', 'down', 'update', null, t('Scarica le modifiche dal server (svn update)')) +
      U.btn('btn', 'Show Log', 'file', 'showLog', null, t('Apre il Log della working copy (cronologia delle revisioni)')) +
      U.btn('ib', '', 'refresh', 'refresh', null, t("Ricarica l'elenco delle modifiche (svn status)")) +
      `<button type="button" class="ib" data-menu="more" title="${esc(t('Altre azioni: vista, tema, prerequisiti'))}" aria-label="${esc(t('Altre azioni'))}" aria-haspopup="menu">${U.icon('more')}</button>`;
  }
  function wcForm(s) {
    return (s.workingCopy.showInput || forceInput) ? `<form class="wc" id="wcform"><label for="wcp">Working copy</label><input id="wcp" type="text" value="${esc(s.workingCopy.path)}" placeholder="${esc(t('C:\\percorso\\della\\working copy'))}" spellcheck="false"><button class="btn sm" type="submit" title="${esc(t('Carica questa working copy'))}">${t('Apri')}</button></form>` : '';
  }
  function commitBar(s) {
    // Come Git: con Staged vuoto "Commit All" prende tutte le modifiche dei file versionati; con file in Staged "Commit Staged (n)".
    const c = s.commit, busy = c.busy || s.loading, staged = (s.sections.find(x => x.id === 'staged') || { count: 0 }).count;
    const n = c.count != null ? c.count : staged, all = c.count != null ? !!c.all : false;
    const why = !n ? t('Nessuna modifica da committare')
      : all ? t('Staged è vuoto: committa tutte le {0} modifiche dei file versionati; i file non versionati restano fuori (Ctrl+Invio)', n)
        : t('Committa i {0} file in Staged (Ctrl+Invio)', n);
    return `<button type="button" class="btn primary" ${U.cmd('commit')} ${n && !busy ? '' : 'disabled'} title="${esc(why)}">${U.icon('check')}<span>${all || !n ? 'Commit All' : 'Commit Staged'}</span>${n ? `<span class="pill">${n}</span>` : ''}</button><span class="fill"></span>` +
      `<button type="button" class="btn ai" ${U.cmd('genAi')} ${c.busy ? 'disabled aria-busy="true"' : ''} title="${esc(t('Scrive il messaggio dal diff dei file da committare'))}">${c.busy ? '<span class="spin"></span>' : U.icon('spark')}<span>${c.busy ? t('Generazione in corso…') : t('Genera con AI')}</span></button>` +
      `<button type="button" class="ib" data-menu="ai" ${c.busy ? 'disabled' : ''} title="${esc(t('Migliora il messaggio, lingua'))}" aria-label="${esc(t('Altre opzioni AI'))}" aria-haspopup="menu">${U.icon('chev-d')}</button>`;
  }
  function aiStat(c) {
    // Ogni messaggio ha colore e icona per gravita' (aviso/errore/esito); data-seq cambia a ogni messaggio, cosi' anche uno ripetuto si ridisegna e lampeggia.
    const lv = c.aiLevel || 'info', ic = lv === 'busy' ? '<span class="spin"></span>' : lv === 'ok' ? U.icon('check') : lv === 'warn' ? U.icon('warn') : lv === 'err' ? U.icon('x') : '';
    let h = c.aiStatus ? `<div class="ai-s lv-${lv}" role="${lv === 'warn' || lv === 'err' ? 'alert' : 'status'}" data-seq="${+c.aiSeq || 0}">${ic}<span>${esc(c.aiStatus).replace(/\n/g, '<br>')}</span></div>` : '';
    if (c.download) h += `<div class="dl"><b>${t('GitHub Copilot: primo utilizzo')}</b> <span class="muted">${t('(runtime ~100 MB, una sola volta)')}</span><progress max="100" value="${+c.download.pct || 0}"></progress>${esc(c.download.label)}</div>`;
    if (c.trace && c.trace.length) h += `<div class="trace">` + c.trace.map((x, i) => {
      const lab = x.state === 'ok' ? t('{0}: OK', x.name) : x.state === 'fail' ? t('{0}: errore', x.name) : x.state === 'skip' ? t('{0}: saltato', x.name) : x.state === 'run' ? `<span class="spin"></span>${esc(x.name)}…` : esc(x.name);
      return (i ? '<span class="muted">›</span>' : '') + `<span class="chip ${x.state}" title="${esc(x.detail || '')}">${x.state === 'ok' || x.state === 'fail' || x.state === 'skip' ? esc(lab) : lab}</span>`;
    }).join('') + `</div>`;
    return h;
  }
  function foot(s) {
    const p = s.prereq, bad = !!(p.summary && /da sistemare|to fix/.test(p.summary)), m = s.status.msg || '';
    // Esito: icona + colore + semibold (stessa dimensione del testo); i messaggi di sola informazione restano normali.
    const kind = s.status.err ? 'err' : /(completat|aggiornat|annullat|aggiunt|eliminat|risolt|ignorat|salvat|completed|updated|cancel|added|deleted|resolved|ignore|saved)/i.test(m) ? 'ok' : '';
    const act = s.status.action;
    // Riga 1: messaggio a tutta larghezza (ellissi + tooltip col testo completo); riga 2: prerequisiti, a destra.
    return `<div class="foot-r1"><span class="msg ${kind}" role="status" title="${esc(m)}">${kind ? U.icon(kind === 'err' ? 'x' : 'check') : ''}<span>${esc(m)}</span></span>` +
      (act ? `<button type="button" class="btn sm" ${U.cmd(act.cmd)} title="${esc(t('Esegue svn cleanup sulla working copy'))}">${esc(act.label)}</button>` : '') + `</div><div class="foot-r2"><span class="fill"></span>` +
      (bad ? `<button type="button" class="btn sm prq-st bad" ${U.cmd('wizardOpenPrereq')} title="${esc(t('Manca qualcosa: apre la procedura guidata per sistemarlo'))}">${U.icon('warn')}<span>${esc(p.summary)}</span></button>`
        : `<button type="button" class="btn sm prq-st ok" ${U.cmd('prereqToggle')} aria-expanded="${!!p.open}" title="${esc(t('Tutto a posto. Mostra/nasconde la checklist: svn, TortoiseSVN, provider AI'))}">${U.icon('check')}<span>${esc((p.summary || t('Prerequisiti')).replace(/\s*✓/, ''))}</span></button>`) + `</div>`;
  }
  const isEmpty = s => s.sections.every(x => !x.count);

  // l'unione automatica e il mergeinfo server-side richiedono svn 1.8+ (versione sconosciuta = si prova)
  const mergeOk = () => { const v = (st.workingCopy.svnVersion || '').split('.').map(Number); return !v[0] || v[0] > 1 || (v[0] === 1 && v[1] >= 8); };
  function mergeBanner(s) {
    const m = s.merge; if (!m) return '';
    return `<div class="mb${m.conflicts ? ' warn' : ''}" role="status">${U.icon(m.conflicts ? 'warn' : 'branch')}<span>${esc(m.text)}${m.conflicts ? ' ' + esc(t('{0} elementi in conflitto: risolvili prima di committare.', m.conflicts)) : ''}</span><span class="fill"></span>` +
      U.btn('btn sm', t('Annulla merge'), null, 'mergeUndo', null, t("Annulla il merge: ripristina la working copy com'era (svn revert)")) + U.btn('ib', '', 'x', 'mergeDismiss', null, t('Nascondi questo avviso')) + `</div>`;
  }
  function moreMenu() {
    return [
      { header: true, label: 'SVN' },
      { label: 'SVN Switch…', cmd: 'switchOpen', hint: t('Porta la working copy su un altro branch o tag (svn switch)') },
      { label: 'SVN Merge…', cmd: 'mergeOpen', hint: t('Unisce nella working copy le modifiche di un altro branch (svn merge)'), disabled: !mergeOk() },
      { label: 'SVN Update to revision…', cmd: 'updateToRevision', hint: t('Aggiorna a una revisione precisa (svn update -r)') },
      { label: 'SVN Cleanup', cmd: 'cleanup', hint: t("Ripulisce la working copy da un'operazione interrotta (svn cleanup)") }, { sep: true },
      { header: true, label: t('Vista') },
      { label: t('Albero per cartelle'), cmd: 'setView', args: { view: 'tree' }, checked: st.view === 'tree' },
      { label: t('Elenco semplice'), cmd: 'setView', args: { view: 'list' }, checked: st.view === 'list' },
      { label: t('Espandi tutto'), cmd: 'expandAll' }, { label: t('Comprimi tutto'), cmd: 'collapseAll' }, { sep: true },
      { header: true, label: t('Tema della finestra') },
      ...THEMES().map(([m, l]) => ({ label: l, cmd: 'setTheme', args: { mode: m }, checked: st.theme.mode === m })), { sep: true },
      { label: t('Cambia provider AI…'), cmd: 'wizardOpen' },
      { label: t('Prerequisiti…'), cmd: 'prereqToggle' },
      { label: t('Cambia working copy…'), run: () => { forceInput = true; render(); const i = document.getElementById('wcp'); if (i) i.focus(); } },
    ];
  }
  function aiMenu() {
    return [{ label: t('Migliora il messaggio scritto'), cmd: 'improveAi', hint: t('Riscrive la bozza nel campo sopra') }, { label: t('Cambia provider AI…'), cmd: 'wizardOpen', hint: t('Procedura guidata: provider, accesso, modello') }, { sep: true }, { header: true, label: t('Lingua del messaggio') },
      ...LANGS().map(([l, t]) => ({ label: t, cmd: 'setLanguage', args: { lang: l }, checked: st.commit.lang === l }))];
  }
  function rowMenu(row, x, y) {
    const key = row.dataset.key, kind = row.dataset.kind, sec = row.dataset.sec;
    const s = st.sections.find(z => z.id === sec); const r = s && s.rows.find(z => z.key === key);
    const A = (op, label, can, kbd) => ({ label, cmd: 'rowOp', args: { op, key }, disabled: !can, kbd });
    if (kind === 'section') return U.menu.open(x, y, [{ label: sec === 'staged' ? t('Rimuovi tutto dai file da committare') : t('Aggiungi tutto ai file da committare'), cmd: sec === 'staged' ? 'unstage' : 'stage', args: { key } }, { sep: true }, { label: t('Espandi tutto'), cmd: 'expandAll' }, { label: t('Comprimi tutto'), cmd: 'collapseAll' }], row);
    if (!r) return;
    const c = r.can, isF = kind === 'file';
    U.menu.open(x, y, [
      { label: isF ? t('Mostra differenze') : t('Mostra differenze della cartella'), cmd: isF ? 'showDiff' : 'showFolderDiff', args: { key }, disabled: !c.diff, kbd: isF ? t('Invio') : '' },
      c.unstage || sec === 'staged' ? { label: t('Rimuovi dai file da committare'), cmd: 'unstage', args: { key }, disabled: !c.unstage, kbd: t('Spazio') } : { label: t('Aggiungi ai file da committare'), cmd: 'stage', args: { key }, disabled: !c.stage, kbd: t('Spazio'), hint: c.stage ? '' : t('Risolvi prima il conflitto') },
      { sep: true }, A('add', t('SVN Add (aggiungi al controllo di versione)'), c.add), A('ignore', t('Aggiungi alla ignore list'), c.ignore),
      A('revert', isF ? t('SVN Revert (annulla le modifiche)…') : t('SVN Revert della cartella (annulla le modifiche)…'), c.revert), isF ? A('resolve', t('Risolvi conflitto…'), c.resolve) : null,
      isF && (c.lock || c.unlock) ? { sep: true } : null, isF && c.lock ? A('lock', 'SVN Lock…', true) : null, isF && c.unlock ? A('unlock', 'SVN Unlock', true) : null,
      { sep: true }, isF ? A('deleteFile', t('Elimina file…'), c.deleteFile, t('Canc')) : { label: t('Espandi tutto'), cmd: 'expandAll' }, isF ? null : { label: t('Comprimi tutto'), cmd: 'collapseAll' },
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
    U.setHTML(root.querySelector('#mrg'), mergeBanner(st));
    const ta = root.querySelector('#msg');
    if (ta.value !== st.commit.text && document.activeElement !== ta) ta.value = st.commit.text;
    const list = root.querySelector('#list'), had = list.contains(document.activeElement), cur = list.__cursor, top = list.scrollTop;
    const keys = []; st.sections.forEach(s => { keys.push('section:' + s.id); s.rows.forEach(r => keys.push(r.key)); });
    const cursor = keys.includes(cur) ? cur : keys[0];
    const html = isEmpty(st) && !st.loading ? `<div class="empty-state">${U.icon('check', 'big')}<b>${t('Nessuna modifica')}</b><span class="muted">${t('La working copy è pulita.')}</span></div>` + '' : '';
    const changed = U.setHTML(list, (html ? html : '') + U.treeHTML(st.sections.filter(s => s.id === 'staged' || s.count), cursor, filter));
    if (changed) { list.scrollTop = top; if (had) { const r = list.querySelector(`.tr[data-key="${CSS.escape(cursor)}"]`); if (r) r.focus(); } }
    list.setAttribute('aria-busy', !!st.loading);
    U.setHTML(root.querySelector('#foot'), foot(st));
    const anyModal = !!(st.wizard && st.wizard.open) || !!(st.revPicker && st.revPicker.open) || !!(st.switchPicker && st.switchPicker.open);
    renderWizard(root, anyModal);
    renderRev(root, anyModal);
    renderSw(root, anyModal);
  }

  // Procedura guidata: dialogo modale (il resto e' inert), il testo digitato e il focus sopravvivono ai ridisegni.
  let wizTimer = 0;
  function wizNeedsPolling(wz) {
    return !!(wz && wz.open && !wz.busy && (wz.mode === 'prereq' ? !wz.canNext : (wz.step === 2 && !wz.canNext) || (wz.step === 3 && (wz.auth === 'github' || wz.auth === 'openai') && !wz.authOk)));
  }
  function renderWizard(app, anyModal) {
    const box = document.getElementById('wiz'), wz = st.wizard; if (!box) return;
    const open = !!(wz && wz.open), was = !!box.__open, keep = {};
    box.querySelectorAll('input,select').forEach(e => { keep[e.id] = e.value; });
    const sameStep = box.__step === (wz && wz.step) && box.__mode === (wz && wz.mode);
    if (open && !was) box.__prev = document.activeElement;
    const changed = U.setHTML(box, U.wizardHTML(wz));
    if (changed) box.querySelectorAll('input,select').forEach(e => { if (keep[e.id] != null && sameStep) e.value = keep[e.id]; });
    if (open || !anyModal) { app.classList.toggle('modal-open', anyModal); app.inert = anyModal; }
    if (open && (!was || !sameStep || !box.contains(document.activeElement))) { const t = box.querySelector(!was || !sameStep ? '#wiz-t' : '.wiz [data-cmd]:not(:disabled),.wiz input,.wiz select'); if (t) t.focus(); }
    if (!open && was && box.__prev && box.__prev.isConnected) box.__prev.focus();
    box.__open = open; box.__step = wz && wz.step; box.__mode = wz && wz.mode;
    const need = wizNeedsPolling(wz);   // ricontrollo leggero finche' manca qualcosa (il terminale e' fuori dalla pagina)
    if (need && !wizTimer) wizTimer = setInterval(() => U.bridge.send('wizardRecheck'), 4000);
    else if (!need && wizTimer) { clearInterval(wizTimer); wizTimer = 0; }
  }
  // Scelta guidata della revisione (Update to revision): modale con elenco, HEAD in cima e campo "oppure numero".
  let revSel = 'HEAD', revTyped = '';
  const revEff = () => revTyped.trim() || revSel;
  function revHTML(r) {
    if (!r || !r.open) return '';
    const row = (rev, body) => `<div class="rp-row${!revTyped.trim() && revSel === String(rev) ? ' sel' : ''}" role="option" tabindex="${revSel === String(rev) ? 0 : -1}" aria-selected="${!revTyped.trim() && revSel === String(rev)}" data-rev="${rev}">${body}</div>`;
    const rows = row('HEAD', `<b class="rv">HEAD</b><span class="m">${esc(t('HEAD (ultima revisione)'))}</span>`) +
      r.items.map(e => row(e.rev, `<b class="rv">r${e.rev}</b><span class="au">${esc(e.author)}</span><span class="dtm muted">${U.fmtDate(e.date)}</span><span class="m" title="${esc(e.msg)}">${esc(e.msg)}</span>`)).join('');
    const more = r.loading ? `<div class="rp-note muted"><span class="spin"></span> ${t('Caricamento delle revisioni…')}</div>`
      : r.canMore ? `<div class="rp-note">${U.btn('btn sm', t('Carica altre'), null, 'revPickerMore', null, t('Mostra altre revisioni più vecchie'))}</div>` : '';
    return `<div class="wiz-scrim"><div class="wiz rp" role="dialog" aria-modal="true" aria-labelledby="rp-t"><div class="wiz-h"><h3 id="rp-t" tabindex="-1">SVN Update to revision</h3><span class="fill"></span>${U.btn('ib', '', 'x', 'revPickerCancel', null, t('Annulla (Esc)'))}</div>` +
      `<div class="wiz-b"><p class="muted">${t('Scegli la revisione a cui aggiornare:')} <code class="rp-t" title="${esc(r.target)}">${esc(r.target)}</code></p>` +
      `<div class="rp-list" id="rp-list" role="listbox" aria-label="${esc(t('Revisioni del repository'))}">${rows}${more}</div>` +
      (r.error ? `<div class="rp-err" role="alert">${U.icon('warn')}<span>${esc(r.error)}</span></div>` : '') +
      `<label for="rp-num">${t('oppure numero di revisione')}</label><input id="rp-num" type="text" inputmode="numeric" autocomplete="off" spellcheck="false" placeholder="${esc(t('es. 1234'))}" value="${esc(revTyped)}"></div>` +
      `<div class="wiz-f">${U.btn('btn ghost', t('Annulla'), null, 'revPickerCancel', null, t('Chiude senza aggiornare'))}<span class="fill"></span><button type="button" class="btn primary" data-rp="ok" title="${esc(t('Aggiorna la working copy a questa revisione (svn update -r)'))}">${U.icon('down')}<span>OK</span></button></div></div></div>`;
  }
  function renderRev(app, anyModal) {
    const box = document.getElementById('rev'), r = st.revPicker; if (!box) return;
    const open = !!(r && r.open), was = !!box.__open, a = document.activeElement;
    if (open && !was) { revSel = 'HEAD'; revTyped = ''; box.__prev = a; }
    const inNum = box.contains(a) && a.id === 'rp-num', inRow = box.contains(a) && a.classList && a.classList.contains('rp-row');
    const caret = inNum ? a.selectionStart : 0, top = box.querySelector('#rp-list') ? box.querySelector('#rp-list').scrollTop : 0;
    const changed = U.setHTML(box, revHTML(r));
    app.classList.toggle('modal-open', anyModal); app.inert = anyModal;
    if (open && changed) {
      const list = box.querySelector('#rp-list'); if (list) list.scrollTop = top;
      if (!was) { const f = box.querySelector('.rp-row[tabindex="0"]'); if (f) f.focus(); }
      else if (inNum) { const n = box.querySelector('#rp-num'); n.focus(); n.setSelectionRange(caret, caret); }
      else if (inRow || !box.contains(document.activeElement)) { const f = box.querySelector('.rp-row[tabindex="0"]'); if (f) f.focus(); }
    }
    if (!open && was && box.__prev && box.__prev.isConnected) box.__prev.focus();
    box.__open = open;
  }
  function revPick(box, rev, focus) {
    revSel = String(rev); revTyped = '';
    box.querySelectorAll('.rp-row').forEach(x => { const on = x.dataset.rev === revSel; x.classList.toggle('sel', on); x.setAttribute('aria-selected', on); x.tabIndex = on ? 0 : -1; if (on && focus) { x.focus(); x.scrollIntoView({ block: 'nearest' }); } });
    const n = box.querySelector('#rp-num'); if (n) n.value = '';
  }
  const revOk = () => U.bridge.send('revPickerOk', { rev: revEff() });
  function revKeys(e) {
    const box = document.getElementById('rev'); if (!box || !box.__open) return;
    const a = document.activeElement;
    if (e.key === 'Escape') { e.preventDefault(); U.bridge.send('revPickerCancel'); return; }
    if (a && a.classList && a.classList.contains('rp-row')) {
      const rows = [...box.querySelectorAll('.rp-row')], i = rows.indexOf(a);
      const to = e.key === 'ArrowDown' ? i + 1 : e.key === 'ArrowUp' ? i - 1 : e.key === 'Home' ? 0 : e.key === 'End' ? rows.length - 1 : -1;
      if (to >= 0) { e.preventDefault(); if (rows[Math.max(0, Math.min(rows.length - 1, to))]) revPick(box, rows[Math.max(0, Math.min(rows.length - 1, to))].dataset.rev, true); return; }
      if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); revPick(box, a.dataset.rev, true); if (e.key === 'Enter') revOk(); return; }
    }
    if (e.key === 'Enter' && a && a.id === 'rp-num') { e.preventDefault(); revOk(); return; }
    if (e.key !== 'Tab') return;
    const f = [...box.querySelectorAll('button:not(:disabled),input,.rp-row[tabindex="0"]')]; if (!f.length) return;
    const first = f[0], last = f[f.length - 1];
    if (e.shiftKey && (a === first || !box.contains(a))) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && (a === last || !box.contains(a))) { e.preventDefault(); first.focus(); }
  }
  // SVN Switch: modale con ricerca e elenco di trunk/branch/tag (l'elenco arriva dal backend; la ricerca e' solo della UI).
  let swSel = '', swQ = '';
  const swVisible = s => { const q = swQ.trim().toLowerCase(); return s.items.filter(i => !q || i.name.toLowerCase().includes(q)); };
  const swSelOk = s => s.items.some(i => i.name === swSel && !i.current);
  function switchHTML(s) {
    if (!s || !s.open) return '';
    const vis = swVisible(s), merge = s.mode === 'merge';
    const row = i => `<div class="rp-row sw-row${swSel === i.name ? ' sel' : ''}" role="option" tabindex="${swSel === i.name ? 0 : -1}" aria-selected="${swSel === i.name}" data-sw="${esc(i.name)}"><b class="rv">${esc(i.kind)}</b><span class="m" title="${esc(i.name)}">${esc(i.name)}</span>${i.current ? `<span class="muted">${esc(t('(corrente)'))}</span>` : ''}</div>`;
    const note = s.loading ? `<div class="rp-note muted"><span class="spin"></span> ${t('Caricamento di branch e tag…')}</div>`
      : vis.length ? '' : `<div class="rp-note muted">${s.items.length ? t('Nessun elemento corrisponde alla ricerca.') : t('Nessun branch o tag trovato. Controlla il layout del repository in Tools > Options > SVNAssist.')}</div>`;
    return `<div class="wiz-scrim"><div class="wiz rp" role="dialog" aria-modal="true" aria-labelledby="sw-t"><div class="wiz-h"><h3 id="sw-t" tabindex="-1">${merge ? 'SVN Merge' : 'SVN Switch'}</h3><span class="fill"></span>${U.btn('ib', '', 'x', 'switchCancel', null, t('Annulla (Esc)'))}</div>` +
      `<div class="wiz-b"><p class="muted">${merge ? t('Scegli il branch, il trunk o il tag da cui unire le modifiche nella working copy.') : t('Scegli il branch o il tag su cui portare la working copy.')}</p>` +
      `<input id="sw-q" type="text" autocomplete="off" spellcheck="false" placeholder="${esc(t('Cerca…'))}" aria-label="${esc(t('Cerca un branch o un tag'))}" value="${esc(swQ)}">` +
      `<div class="rp-list" id="sw-list" role="listbox" aria-label="${esc(t('Branch e tag del repository'))}">${vis.map(row).join('')}${note}</div>` +
      (s.error ? `<div class="rp-err" role="alert">${U.icon('warn')}<span>${esc(s.error)}</span></div>` : '') + `</div>` +
      `<div class="wiz-f">${U.btn('btn ghost', t('Annulla'), null, 'switchCancel', null, merge ? t('Chiude senza unire nulla') : t('Chiude senza cambiare ramo'))}${U.btn('btn sm', t('Aggiorna'), 'refresh', 'switchRefresh', null, t("Rilegge l'elenco di branch e tag dal server"))}<span class="fill"></span>` +
      `<button type="button" class="btn primary" data-sw-ok ${swSelOk(s) && !s.loading ? '' : 'disabled'} title="${esc(merge ? t('Mostra le revisioni di questo ramo ancora da unire (svn mergeinfo)') : t('Porta la working copy sul ramo scelto (svn switch)'))}">${U.icon('branch')}<span>${merge ? 'Merge…' : 'Switch'}</span></button></div></div></div>`;
  }
  function renderSw(app, anyModal) {
    const box = document.getElementById('sw'), s = st.switchPicker; if (!box) return;
    const open = !!(s && s.open), was = !!box.__open, a = document.activeElement;
    if (open && !was) { swSel = ''; swQ = ''; box.__prev = a; }
    if (open && swSel && !s.items.some(i => i.name === swSel)) swSel = '';
    const inQ = box.contains(a) && a.id === 'sw-q', inRow = box.contains(a) && a.classList && a.classList.contains('sw-row');
    const caret = inQ ? a.selectionStart : 0, top = box.querySelector('#sw-list') ? box.querySelector('#sw-list').scrollTop : 0;
    const changed = U.setHTML(box, switchHTML(s));
    app.classList.toggle('modal-open', anyModal); app.inert = anyModal;
    if (open && changed) {
      const list = box.querySelector('#sw-list'); if (list) list.scrollTop = top;
      if (!was || !box.contains(document.activeElement) || inRow) { const f = !was || !swSel ? box.querySelector('#sw-q') : box.querySelector('.sw-row[tabindex="0"]') || box.querySelector('#sw-q'); if (f) f.focus(); }
      else if (inQ) { const n = box.querySelector('#sw-q'); n.focus(); n.setSelectionRange(caret, caret); }
    }
    if (!open && was && box.__prev && box.__prev.isConnected) box.__prev.focus();
    box.__open = open;
  }
  const swOk = () => { if (st.switchPicker && swSelOk(st.switchPicker)) U.bridge.send('switchOk', { name: swSel }); };
  function swKeys(e) {
    const box = document.getElementById('sw'); if (!box || !box.__open) return;
    const a = document.activeElement;
    if (e.key === 'Escape') { e.preventDefault(); U.bridge.send('switchCancel'); return; }
    const rows = [...box.querySelectorAll('.sw-row')], i = rows.indexOf(a);
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      const to = i < 0 ? (e.key === 'ArrowDown' ? 0 : rows.length - 1) : i + (e.key === 'ArrowDown' ? 1 : -1);
      if (rows[to]) { e.preventDefault(); swSel = rows[to].dataset.sw; renderSw(document.querySelector('.app'), true); const f = [...box.querySelectorAll('.sw-row')].find(x => x.dataset.sw === swSel); if (f) { f.focus(); f.scrollIntoView({ block: 'nearest' }); } }
      return;
    }
    if (e.key === 'Enter' && a && (a.id === 'sw-q' || a.classList.contains('sw-row'))) {
      e.preventDefault();
      if (a.classList.contains('sw-row')) swSel = a.dataset.sw;
      else if (!swVisible(st.switchPicker).some(x => x.name === swSel) && rows[0]) swSel = rows[0].dataset.sw;
      if (swSelOk(st.switchPicker)) swOk(); else renderSw(document.querySelector('.app'), true);
      return;
    }
    if (e.key !== 'Tab') return;
    const f = [...box.querySelectorAll('button:not(:disabled),input,.sw-row[tabindex="0"]')]; if (!f.length) return;
    const first = f[0], last = f[f.length - 1];
    if (e.shiftKey && (a === first || !box.contains(a))) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && (a === last || !box.contains(a))) { e.preventDefault(); first.focus(); }
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

  U.changesHTML = { header, commitBar, foot, revHTML, switchHTML, mergeBanner };   // parti pure, provate senza DOM (tests/ui-web)

  U.views.changes = {
    mount(root) {
      root.innerHTML = `<div class="app"><div class="prog" aria-hidden="true"></div><header class="bar" id="hdr"></header><div id="wcbox"></div>
        <section class="commit"><textarea id="msg" rows="3"></textarea>
        <div class="bar" id="cbar"></div><div id="aist"></div></section><div id="prq"></div><div id="mrg"></div>
        <div class="flt"><input id="flt" type="search" spellcheck="false" autocomplete="off"></div>
        <div class="list" id="list" role="tree"></div>
        <footer class="foot" id="foot"></footer></div><div id="wiz"></div><div id="rev"></div><div id="sw"></div>`;
      U.views.changes.relang(root);
      const ta = root.querySelector('#msg');
      ta.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(() => U.bridge.send('setCommitText', { text: ta.value }), 150); });
      ta.addEventListener('keydown', e => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); clearTimeout(timer); U.bridge.send('setCommitText', { text: ta.value }); U.bridge.send('commit'); } });
      root.addEventListener('keydown', wizKeys);
      root.addEventListener('keydown', revKeys);
      root.addEventListener('keydown', swKeys);
      root.addEventListener('click', e => {
        const row = e.target.closest('.sw-row');
        if (row) { swSel = row.dataset.sw; renderSw(root.querySelector('.app'), true); const f = [...root.querySelectorAll('#sw .sw-row')].find(x => x.dataset.sw === swSel); if (f) f.focus(); return; }
        if (e.target.closest('[data-sw-ok]')) swOk();
      });
      root.addEventListener('dblclick', e => { const row = e.target.closest('.sw-row'); if (row) { swSel = row.dataset.sw; swOk(); } });
      root.addEventListener('input', e => { if (e.target.id === 'sw-q') { swQ = e.target.value; renderSw(root.querySelector('.app'), true); } });
      root.addEventListener('click', e => {
        const box = document.getElementById('rev'), row = e.target.closest('.rp-row');
        if (row && box && box.contains(row)) { revPick(box, row.dataset.rev, true); return; }
        if (e.target.closest('[data-rp="ok"]')) revOk();
      });
      root.addEventListener('dblclick', e => { const row = e.target.closest('.rp-row'); if (row) { revPick(document.getElementById('rev'), row.dataset.rev, false); revOk(); } });
      root.addEventListener('input', e => {
        if (e.target.id !== 'rp-num') return;
        revTyped = e.target.value;
        document.querySelectorAll('#rev .rp-row').forEach(x => { const on = !revTyped.trim() && x.dataset.rev === revSel; x.classList.toggle('sel', on); x.setAttribute('aria-selected', on); });
      });
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
    // testi statici dello scheletro (attributi): da rifare a ogni cambio di lingua
    relang(root) {
      const set = (sel, attrs) => { const e = root.querySelector(sel); if (e) for (const k in attrs) e.setAttribute(k, attrs[k]); };
      set('#msg', { 'aria-label': t('Messaggio di commit'), placeholder: t('Messaggio di commit (Ctrl+Invio per committare)') });
      set('#flt', { placeholder: t('Filtra i file…'), 'aria-label': t('Filtra i file per nome') });
      set('#list', { 'aria-label': t('Modifiche della working copy') });
    },
  };
})(window);
