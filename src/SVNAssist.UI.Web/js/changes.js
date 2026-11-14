'use strict';
/* Vista "SVN Changes". update(root, state) ridisegna a regioni, senza distruggere textarea/focus. */
(function (w) {
  const U = w.SVNUI, esc = U.esc;
  const t = U.t;
  const LANGS = () => [['auto', t('Auto (lingua del codice)')], ['italiano', 'Italiano'], ['english', 'English']];
  const THEMES = () => [['auto', t('Auto (segue Visual Studio)')], ['light', t('Chiaro')], ['dark', t('Scuro')], ['blue', t('Blu')]];
  let st = null, forceInput = false, timer = 0, filter = '';

  function header(s) {
    const wc = s.workingCopy;
    return `<button type="button" class="branch" ${U.cmd('switchOpen')} title="${esc(t('SVN Switch…: cambia branch o tag (ramo attuale: {0})', wc.branch || '—'))}" aria-label="${esc(t('SVN Switch…: cambia branch o tag (ramo attuale: {0})', wc.branch || '—'))}">${U.icon('branch')}<b>${esc(wc.branch || '—')}</b>${U.icon('chev-d')}</button><span class="fill"></span>` +
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
      `<button type="button" class="btn sm" data-recent title="${esc(t('Messaggi recenti: riusa un messaggio già committato in questo repository'))}" aria-haspopup="dialog">${U.icon('chev-d')}<span>${t('Recenti')}</span></button>` +
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
  // Esito: successo (verde), errore (rosso) o sola informazione. I successi spariscono dopo OK_TTL, gli errori restano finche' non sono sostituiti.
  const statusKind = (m, err) => err ? 'err' : /(completat|aggiornat|annullat|aggiunt|eliminat|risolt|ignorat|salvat|completed|updated|cancel|added|deleted|resolved|ignore|saved)/i.test(m || '') ? 'ok' : '';
  const OK_TTL = 5000;
  const statusKey = s => (s.status.seq || 0) + '|' + (s.status.msg || '');
  let goneKey = '', trackKey = '', trackTimer = 0;
  function trackStatus(s) {   // a ogni nuovo messaggio di successo parte il timer che lo nasconde
    const k = statusKey(s); if (k === trackKey) return;
    trackKey = k; clearTimeout(trackTimer);
    if (statusKind(s.status.msg, s.status.err) === 'ok') trackTimer = setTimeout(() => { goneKey = k; render(); }, OK_TTL);
  }
  // Revisione della working copy: verde = e' l'ultima del ramo, arancio = il server ne ha una piu' recente (clic = SVN Update), neutro = non si sa (tooltip col perche')
  function revIndicator(s) {
    const wc = s.workingCopy, r = wc.rev; if (!r) return '';
    if (wc.revState === 'behind' && wc.head) {
      const tip = t('Qualcuno ha committato: sul server c\'è la r{0}, la tua working copy è alla r{1}. Clic per fare SVN Update.', wc.head, r);
      return `<button type="button" class="rev bad" ${U.cmd('update')} title="${esc(tip)}" aria-label="${esc(tip)}">${U.icon('warn')}<span>r${r} · ${esc(t('disponibile r{0} — SVN Update', wc.head))}</span></button>`;
    }
    const ok = wc.revState === 'current';
    const tip = ok ? t('La working copy è all\'ultima revisione del ramo (r{0}).', r) : (wc.revNote || t('Revisione della working copy: r{0}.', r));
    return `<span class="rev${ok ? ' ok' : ''}" title="${esc(tip)}">${ok ? U.icon('check') : ''}<span>r${r}</span></span>`;
  }
  const actTip = cmd => cmd === 'update' ? t('Esegue svn update sulla working copy')
    : cmd === 'switchResume' ? t('Esegue svn cleanup e ripete lo switch: svn riprende da dove si era fermato')
    : cmd === 'switchBack' ? t('Esegue svn cleanup e riporta la working copy al ramo e alla revisione di prima dello switch; le modifiche locali restano')
    : t('Esegue svn cleanup sulla working copy');
  function foot(s) {
    const p = s.prereq, bad = !!(p.summary && /da sistemare|to fix/.test(p.summary));
    const m = statusKey(s) === goneKey ? '' : (s.status.msg || ''), kind = statusKind(m, s.status.err);
    const act = s.status.action;
    // Riga 1 (solo se c'e' un messaggio): messaggio a tutta larghezza (ellissi + tooltip col testo completo; un errore va a capo per intero,
    // selezionabile e copiabile, con scorrimento se lunghissimo); riga 2: revisione a sinistra, prerequisiti a destra.
    return `<div class="foot-r1${kind === 'err' ? ' err' : ''}">` + (m ? `<span class="msg ${kind}" role="status" title="${esc(m)}">${kind ? U.icon(kind === 'err' ? 'x' : 'check') : ''}<span>${esc(m)}</span></span>` : '') +
      (m && act ? `<button type="button" class="btn sm" ${U.cmd(act.cmd)} title="${esc(actTip(act.cmd))}">${esc(act.label)}</button>` : '') +
      (m && act && s.status.action2 ? `<button type="button" class="btn sm" ${U.cmd(s.status.action2.cmd)} title="${esc(actTip(s.status.action2.cmd))}">${esc(s.status.action2.label)}</button>` : '') + `</div><div class="foot-r2">${revIndicator(s)}<span class="fill"></span>` +
      (bad ? `<button type="button" class="btn sm prq-st bad" ${U.cmd('wizardOpenPrereq')} title="${esc(t('Manca qualcosa: apre la procedura guidata per sistemarlo'))}">${U.icon('warn')}<span>${esc(p.summary)}</span></button>`
        : `<button type="button" class="btn sm prq-st ok" ${U.cmd('prereqToggle')} aria-expanded="${!!p.open}" title="${esc(t('Tutto a posto. Mostra/nasconde la checklist: svn, TortoiseSVN, provider AI'))}">${U.icon('check')}<span>${esc((p.summary || t('Prerequisiti')).replace(/\s*✓/, ''))}</span></button>`) + `</div>`;
  }
  const isEmpty = s => s.sections.every(x => !x.count);

  // l'unione automatica e il mergeinfo server-side richiedono svn 1.8+ (versione sconosciuta = si prova)
  const mergeOk = () => { const v = (st.workingCopy.svnVersion || '').split('.').map(Number); return !v[0] || v[0] > 1 || (v[0] === 1 && v[1] >= 8); };
  function mergeBanner(s) {
    const m = s.merge; if (!m) return '';
    return `<div class="mb${m.conflicts ? ' warn' : ''}" role="status">${U.icon(m.conflicts ? 'warn' : 'branch')}<span>${esc(m.text)}${m.conflicts ? ' ' + esc(m.conflicts === 1 ? t('{0} conflitto: risolvilo qui sotto, poi committa.', m.conflicts) : t('{0} conflitti: risolvili qui sotto, poi committa.', m.conflicts)) : ''}</span><span class="fill"></span>` +
      U.btn('btn sm', t('Annulla merge'), null, 'mergeUndo', null, t("Annulla il merge: ripristina la working copy com'era (svn revert)")) + U.btn('ib', '', 'x', 'mergeDismiss', null, t('Nascondi questo avviso')) + `</div>`;
  }
  // Sezione Conflitti: un riquadro per file con le azioni esplicite (editor di merge a 3 vie, mia, loro, segna come risolto) e, sotto,
  // i file di appoggio di svn (da svn info: .mine, .rNN, .merge-left/right, .working), che non compaiono tra i non versionati.
  // Stato solo-UI: riquadro aperto/chiuso e, dentro, l'elenco dei conflitti ad albero (chiuso: di solito si risolvono tutti insieme).
  let cflOpen = true, cflTreeOpen = false;
  function conflictsHTML(s, ui) {
    const c = s.conflicts || []; if (!c.length) return '';
    const u = Object.assign({ open: cflOpen, treeOpen: cflTreeOpen }, ui), off = s.loading ? 'disabled' : '';
    const one = f => {
      const A = (cls, label, icon, op, tip) => U.btn('btn' + cls, label, icon, 'conflictOp', { op, key: f.key }, tip, off);
      const tree = f.kind === 'tree', text = f.kind === 'text';
      const kind = tree ? t('Conflitto ad albero') : f.kind === 'property' ? t('Conflitto sulle proprietà') : t('Conflitto di testo');
      return `<li class="cf"><div class="cf-t">${U.icon('warn')}<b class="cf-n" title="${esc(f.path)}">${esc(f.name)}</b><span class="chip">${esc(kind)}</span>${f.path !== f.name ? `<span class="muted small cf-p" title="${esc(f.path)}">${esc(f.path)}</span>` : ''}</div>` +
        (tree ? `<p class="muted">${t('Rinomina o cancellazione in conflitto: non ci sono versioni da scegliere. Sistema la struttura a mano, poi segna il conflitto come risolto.')}</p>` : '') +
        `<div class="cf-a">` +
        (text ? A(' primary', t('Apri editor di merge'), 'diff', 'edit', t('Apre un editor a 3 vie (base, mia, loro) con il risultato; salva, poi segna come risolto')) : '') +
        (tree ? '' : A('', t('Usa la mia'), null, 'mine', t('Tiene la tua versione dell\'intero file e scarta le modifiche del server (resolve --accept mine-full)')) +
          A('', t('Usa la loro'), null, 'theirs', t('Prende la versione del server dell\'intero file e scarta le tue modifiche (resolve --accept theirs-full)'))) +
        A('', t('Segna come risolto'), 'check', 'resolved', tree ? t('Conferma di aver sistemato la struttura (resolve)') : t('Dopo aver sistemato il file a mano: segna il conflitto come risolto (resolve --accept working)')) + `</div>` +
        (f.files && f.files.length ? `<p class="cf-f muted small">${t('File di appoggio di svn (spariscono dopo il resolve):')} ${f.files.map(x => `<code>${esc(x)}</code>`).join(' ')}</p>` : '') + `</li>`;
    };
    const trees = c.filter(f => f.kind === 'tree'), others = c.filter(f => f.kind !== 'tree');
    // gruppo "Conflitti ad albero": spiegazione, un solo pulsante per segnarli tutti risolti, elenco espandibile con il pulsante per ogni voce
    const group = !trees.length ? '' : `<li class="cf cf-g"><div class="cf-t">${U.icon('warn')}<b>${t('Conflitti ad albero ({0})', trees.length)}</b>` +
      `<button type="button" class="btn sm ghost" data-cfl-tree aria-expanded="${!!u.treeOpen}" title="${esc(t('Mostra o nasconde l\'elenco dei conflitti ad albero'))}">${U.icon(u.treeOpen ? 'chev-d' : 'chev-r')}<span>${u.treeOpen ? t('Nascondi elenco') : t('Mostra elenco')}</span></button></div>` +
      `<p class="muted">${t('Rinomine o cancellazioni in conflitto: non ci sono versioni da scegliere. Dopo aver sistemato la struttura a mano si segnano risolti, di solito tutti insieme.')}</p>` +
      `<div class="cf-a">${U.btn('btn primary', t('Segna tutti come risolti'), 'check', 'resolveTreeConflicts', null, t('Conferma con l\'elenco, poi resolve --accept working su ognuno; un errore su uno non ferma gli altri'), off)}</div>` +
      (u.treeOpen ? `<ul class="cfl-l cf-sub">${trees.map(f => `<li class="cf-s"><b class="cf-n" title="${esc(f.path)}">${esc(f.name)}</b>${f.path !== f.name ? `<span class="muted small cf-p" title="${esc(f.path)}">${esc(f.path)}</span>` : ''}<span class="fill"></span>${U.btn('btn sm', t('Segna come risolto'), 'check', 'conflictOp', { op: 'resolved', key: f.key }, t('Conferma di aver sistemato la struttura (resolve)'), off)}</li>`).join('')}</ul>` : '') + `</li>`;
    return `<section class="cfl" aria-label="${esc(t('Conflitti'))}"><div class="cfl-h"><button type="button" class="ib" data-cfl-toggle aria-expanded="${!!u.open}" title="${esc(u.open ? t('Comprimi il riquadro dei conflitti') : t('Espandi il riquadro dei conflitti'))}" aria-label="${esc(u.open ? t('Comprimi il riquadro dei conflitti') : t('Espandi il riquadro dei conflitti'))}">${U.icon(u.open ? 'chev-d' : 'chev-r')}</button>${U.icon('warn')}<b>${t('Conflitti')}</b><span class="pill">${c.length}</span><span class="muted cfl-n">${t('Svn non committa file in conflitto: risolvili qui.')}</span></div>` +
      (u.open ? `<ul class="cfl-l">${others.map(one).join('')}${group}</ul>` : '') + `</section>`;
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
  // Pannello "Recenti" (come TortoiseSVN, ma largo quanto la finestra): ultimi messaggi committati per repository, con ricerca in cima.
  // Ogni voce: prima riga in grassetto + anteprima (3 righe; intero messaggio sulla voce selezionata o sotto il mouse). Il clic o Invio lo inserisce nel campo.
  let rcOpen = false, rcQ = '', rcSel = 0;
  // voci che contengono la ricerca (senza maiuscole), con l'indice originale: e' quello che si manda a useRecentMessage
  U.recentFilter = (list, q) => { const k = (q || '').trim().toLowerCase(); return (list || []).map((m, i) => ({ i, m })).filter(x => !k || x.m.toLowerCase().includes(k)); };
  const rcLines = m => m.split('\n').map(l => l.trim()).filter(Boolean);
  function recentListHTML(s, q, sel) {
    const all = (s.commit && s.commit.recent) || [], r = U.recentFilter(all, q);
    if (!all.length) return `<div class="rp-note muted">${t('Nessun messaggio recente')}</div>`;
    if (!r.length) return `<div class="rp-note muted">${t('Nessun messaggio corrisponde alla ricerca.')}</div>`;
    return r.map((x, n) => { const l = rcLines(x.m), rest = l.slice(1).join('\n');
      return `<div class="rc-row${n === sel ? ' sel' : ''}" role="option" id="rc-o${n}" aria-selected="${n === sel}" data-i="${x.i}" data-n="${n}" title="${esc(x.m)}"><b class="rc-1">${esc(l[0] || x.m)}</b>${rest ? `<span class="rc-p">${esc(rest)}</span>` : ''}</div>`; }).join('');
  }
  function recentHTML(s, ui) {
    const u = Object.assign({ q: rcQ, sel: rcSel }, ui), all = (s.commit && s.commit.recent) || [];
    return `<div class="rc" role="dialog" aria-label="${esc(t('Messaggi recenti'))}"><input id="rc-q" type="text" role="combobox" aria-expanded="true" aria-controls="rc-list" autocomplete="off" spellcheck="false" placeholder="${esc(t('Cerca nei messaggi recenti…'))}" aria-label="${esc(t('Cerca nei messaggi recenti'))}" value="${esc(u.q)}">` +
      `<div class="rc-list" id="rc-list" role="listbox" aria-label="${esc(t('Messaggi recenti'))}">${recentListHTML(s, u.q, u.sel)}</div>` +
      `<div class="muted small rc-f" role="status">${recentFootHTML(s, u.q)}</div></div>`;
  }
  function recentFootHTML(s, q) {
    const all = (s.commit && s.commit.recent) || [];
    return (all.length ? t('{0} di {1}', U.recentFilter(all, q).length, all.length) + ' · ' : '') + esc(t('Frecce per scegliere, Invio per usare, Esc per chiudere'));
  }
  function rcSetSel(n, reveal) {
    const box = document.getElementById('rec'), rows = [...box.querySelectorAll('.rc-row')]; if (!rows.length) return;
    rcSel = Math.max(0, Math.min(rows.length - 1, n));
    rows.forEach((r, k) => { r.classList.toggle('sel', k === rcSel); r.setAttribute('aria-selected', k === rcSel); });
    const q = box.querySelector('#rc-q'); if (q) q.setAttribute('aria-activedescendant', rows[rcSel].id);
    if (reveal) rows[rcSel].scrollIntoView({ block: 'nearest' });
  }
  function rcRefresh() {   // solo l'elenco e il pie': il campo di ricerca e il focus restano
    const box = document.getElementById('rec'); if (!box || !rcOpen) return;
    const n = U.recentFilter((st.commit && st.commit.recent) || [], rcQ).length; rcSel = Math.min(rcSel, Math.max(0, n - 1));
    U.setHTML(box.querySelector('#rc-list'), recentListHTML(st, rcQ, rcSel)); U.setHTML(box.querySelector('.rc-f'), recentFootHTML(st, rcQ));
    const q = box.querySelector('#rc-q'), on = box.querySelector('.rc-row.sel'); if (q) { if (on) q.setAttribute('aria-activedescendant', on.id); else q.removeAttribute('aria-activedescendant'); }
  }
  function rcOpenAt(btn) {
    const box = document.getElementById('rec'); if (!box) return;
    rcOpen = true; rcQ = ''; rcSel = 0; box.__prev = btn;
    box.innerHTML = recentHTML(st); box.__h = null; box.hidden = false;
    box.style.top = Math.round(btn.getBoundingClientRect().bottom + 2) + 'px';
    const q = box.querySelector('#rc-q'); if (q) q.focus(); rcSetSel(0);
  }
  function rcClose(back) {
    const box = document.getElementById('rec'); if (!box || !rcOpen) return;
    rcOpen = false; box.hidden = true; box.innerHTML = '';
    if (back && box.__prev && box.__prev.isConnected) box.__prev.focus();
  }
  function rcChoose(i) { rcClose(false); U.bridge.send('useRecentMessage', { i }); const ta = document.getElementById('msg'); if (ta) ta.focus(); }
  function rcKeys(e) {
    if (!rcOpen) return;
    const box = document.getElementById('rec'), rows = box.querySelectorAll('.rc-row');
    if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); rcClose(true); return; }
    if (e.key === 'Tab') { e.preventDefault(); rcClose(true); return; }
    const step = { ArrowDown: 1, ArrowUp: -1, PageDown: 5, PageUp: -5 }[e.key];
    if (step) { e.preventDefault(); rcSetSel(rcSel + step, true); return; }
    if (e.key === 'Enter' && rows[rcSel]) { e.preventDefault(); rcChoose(+rows[rcSel].dataset.i); }
  }
  function aiMenu() {
    return [{ label: t('Migliora il messaggio scritto'), cmd: 'improveAi', hint: t('Riscrive la bozza nel campo sopra') }, { label: t('Cambia provider AI…'), cmd: 'wizardOpen', hint: t('Procedura guidata: provider, accesso, modello') }, { sep: true }, { header: true, label: t('Lingua del messaggio') },
      ...LANGS().map(([l, t]) => ({ label: t, cmd: 'setLanguage', args: { lang: l }, checked: st.commit.lang === l }))];
  }
  // menu della riga di sezione (Changes / Staged / Non versionati): SVN Revert di tutto dove ci sono modifiche annullabili
  function sectionMenu(sec, key, s) {
    return [{ label: sec === 'staged' ? t('Rimuovi tutto dai file da committare') : t('Aggiungi tutto ai file da committare'), cmd: sec === 'staged' ? 'unstage' : 'stage', args: { key } }, { sep: true },
      sec === 'unversioned' ? null : { label: t('SVN Revert di tutto…'), cmd: 'rowOp', args: { op: 'revert', key }, disabled: !(s && s.count), hint: t('Annulla tutte le modifiche di questa sezione (anche in più cartelle), dopo conferma con l’elenco dei file; i non versionati non si toccano') },
      sec === 'unversioned' ? null : { sep: true }, { label: t('Espandi tutto'), cmd: 'expandAll' }, { label: t('Comprimi tutto'), cmd: 'collapseAll' }].filter(Boolean);
  }
  function rowMenu(row, x, y) {
    const key = row.dataset.key, kind = row.dataset.kind, sec = row.dataset.sec;
    const s = st.sections.find(z => z.id === sec); const r = s && s.rows.find(z => z.key === key);
    const A = (op, label, can, kbd) => ({ label, cmd: 'rowOp', args: { op, key }, disabled: !can, kbd });
    if (kind === 'section') return U.menu.open(x, y, sectionMenu(sec, key, s), row);
    if (!r) return;
    const c = r.can, isF = kind === 'file';
    U.menu.open(x, y, [
      { label: isF ? (r.status === 'A' || r.status === '?' ? t('Apri file') : t('Mostra differenze')) : t('Mostra differenze della cartella'), cmd: isF ? 'showDiff' : 'showFolderDiff', args: { key }, disabled: !c.diff, kbd: isF ? t('Invio') : '' },
      c.unstage || sec === 'staged' ? { label: t('Rimuovi dai file da committare'), cmd: 'unstage', args: { key }, disabled: !c.unstage, kbd: t('Spazio') } : { label: t('Aggiungi ai file da committare'), cmd: 'stage', args: { key }, disabled: !c.stage, kbd: t('Spazio'), hint: c.stage ? '' : t('Risolvi prima il conflitto') },
      { sep: true }, A('add', t('SVN Add (aggiungi al controllo di versione)'), c.add), A('ignore', t('Aggiungi alla ignore list'), c.ignore),
      A('revert', isF ? t('SVN Revert (annulla le modifiche)…') : t('SVN Revert della cartella (annulla le modifiche)…'), c.revert), isF ? A('resolve', t('Risolvi conflitto…'), c.resolve) : null,
      isF && (c.lock || c.unlock) ? { sep: true } : null, isF && c.lock ? A('lock', 'SVN Lock…', true) : null, isF && c.unlock ? A('unlock', 'SVN Unlock', true) : null,
      { sep: true }, isF ? A('deleteFile', t('Elimina file…'), c.deleteFile, t('Canc')) : { label: t('Espandi tutto'), cmd: 'expandAll' }, isF ? null : { label: t('Comprimi tutto'), cmd: 'collapseAll' },
    ].filter(Boolean), row);
  }

  // Come Git Changes: prima le sezioni (Staged c'e' sempre), lo stato vuoto sta sotto.
  function listHTML(s, cursor, flt) {
    const empty = isEmpty(s) && !s.loading ? `<div class="empty-state">${U.icon('check', 'big')}<b>${t('Nessuna modifica')}</b><span class="muted">${t('La working copy è pulita.')}</span></div>` : '';
    return U.treeHTML(s.sections.filter(x => x.id === 'staged' || x.count), cursor, flt) + empty;
  }

  function render() {
    if (!st) return;
    trackStatus(st);
    const root = document.querySelector('.app'); if (!root) return;
    root.classList.toggle('loading', !!st.loading);
    U.setHTML(root.querySelector('#hdr'), header(st));
    U.setHTML(root.querySelector('#wcbox'), wcForm(st));
    U.setHTML(root.querySelector('#cbar'), commitBar(st));
    U.setHTML(root.querySelector('#aist'), aiStat(st.commit));
    U.setHTML(root.querySelector('#prq'), U.prereqHTML(st.prereq));
    U.setHTML(root.querySelector('#mrg'), mergeBanner(st));
    U.setHTML(root.querySelector('#cfl'), conflictsHTML(st));
    const ta = root.querySelector('#msg');
    if (ta.value !== st.commit.text && document.activeElement !== ta) ta.value = st.commit.text;
    const list = root.querySelector('#list'), had = list.contains(document.activeElement), cur = list.__cursor, top = list.scrollTop;
    const keys = []; st.sections.forEach(s => { keys.push('section:' + s.id); s.rows.forEach(r => keys.push(r.key)); });
    const cursor = keys.includes(cur) ? cur : keys[0];
    // Come Git Changes: prima le sezioni (Staged c'e' sempre), lo stato vuoto sta sotto.
    const html = isEmpty(st) && !st.loading ? `<div class="empty-state">${U.icon('check', 'big')}<b>${t('Nessuna modifica')}</b><span class="muted">${t('La working copy è pulita.')}</span></div>` : '';
    const changed = U.setHTML(list, U.treeHTML(st.sections.filter(s => s.id === 'staged' || s.count), cursor, filter) + html);
    if (changed) { list.scrollTop = top; if (had) { const r = list.querySelector(`.tr[data-key="${CSS.escape(cursor)}"]`); if (r) r.focus(); } }
    list.setAttribute('aria-busy', !!st.loading);
    U.setHTML(root.querySelector('#foot'), foot(st));
    const anyModal = !!(st.wizard && st.wizard.open) || !!(st.revPicker && st.revPicker.open) || !!(st.switchPicker && st.switchPicker.open) || !!(st.progress || st.switchWarn || st.cleanup);
    if (anyModal) rcClose(false); else rcRefresh();
    renderWizard(root, anyModal);
    renderRev(root, anyModal);
    renderSw(root, anyModal);
    U.ops.render(root.querySelector("#ops"), st);
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
  // SVN Switch / Merge. Selettore veloce: ultimi usati + ricerca che filtra subito, al massimo SW_LIMIT risultati. Vista estesa: tabella con filtro per nome
  // (contiene, senza maiuscole), filtro per tipo e ordine per nome o data; lista virtuale (nel DOM solo le righe visibili: 500+ voci scorrono senza blocchi).
  // L'elenco arriva dal backend; ricerca, filtri e ordine sono solo della UI. Il focus resta nel campo di ricerca (aria-activedescendant).
  const SW_LIMIT = 50, SW_ROW = 28;
  let swSel = '', swQ = '', swExt = false, swKind = '', swSort = 'name', swDesc = false, swAuthor = '', swFrom = '', swTo = '', swPreset = '';
  const swColl = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' }), swCmp = (a, b) => swColl.compare(a.name, b.name);
  // Filtri: q = nome (contiene), kind = tipo, author = autore (uguale, senza maiuscole), from/to = intervallo di date 'AAAA-MM-GG' (estremi inclusi; senza data = escluso).
  // sort: 'name' | 'date' | 'rev' | 'author' | '' (ordine del server: trunk, branch, tag); desc inverte
  U.swFilter = (items, o) => {
    const q = (o.q || '').trim().toLowerCase(), au = (o.author || '').trim().toLowerCase();
    const from = o.from ? Date.parse(o.from + 'T00:00:00') : NaN, to = o.to ? Date.parse(o.to + 'T00:00:00') + 864e5 : NaN, byDate = !isNaN(from) || !isNaN(to);
    const inRange = i => { const d = Date.parse(i.date); return !isNaN(d) && (isNaN(from) || d >= from) && (isNaN(to) || d < to); };
    const r = items.filter(i => (!q || i.name.toLowerCase().includes(q)) && (!o.kind || i.kind === o.kind) && (!au || (i.author || '').toLowerCase() === au) && (!byDate || inRange(i)));
    const t = o.sort === 'date' ? new Map(r.map(i => [i, Date.parse(i.date) || 0])) : null, k = o.desc ? -1 : 1;
    if (o.sort === 'name') r.sort((a, b) => k * swCmp(a, b));
    else if (o.sort === 'rev') r.sort((a, b) => k * ((a.rev || 0) - (b.rev || 0)) || swCmp(a, b));
    else if (o.sort === 'author') r.sort((a, b) => k * swColl.compare(a.author || '', b.author || '') || swCmp(a, b));
    else if (t) r.sort((a, b) => k * (t.get(a) - t.get(b)) || swCmp(a, b));
    return r;
  };
  U.swAuthors = items => [...new Map(items.filter(i => i.author).reverse().map(i => [i.author.toLowerCase(), i.author])).values()].sort((a, b) => swColl.compare(a, b));
  // 'AAAA-MM-GG' di `days` giorni prima di `now` (per i preset "ultimi 7/30/90 giorni")
  U.swPreset = (days, now) => { const d = new Date((now == null ? Date.now() : now) - days * 864e5), p = n => String(n).padStart(2, '0'); return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`; };
  // selettore veloce: usati di recente (solo senza ricerca) + il resto, al massimo `limit` righe in tutto; more = quante ne restano fuori
  U.swQuick = (items, recent, q, limit) => {
    const matches = U.swFilter(items, { q }), rec = q.trim() ? [] : (recent || []).map(n => items.find(i => i.name === n)).filter(Boolean);
    const rest = matches.filter(i => !rec.includes(i)), room = Math.max(0, limit - rec.length);
    return { recent: rec, rest: rest.slice(0, room), total: matches.length, more: Math.max(0, rest.length - room) };
  };
  // righe da disegnare in una lista alta n*row con la finestra visibile [top, top+h): start incluso, end escluso
  U.swWindow = (n, top, h, row, over) => { const o = over == null ? 6 : over; return { start: Math.max(0, Math.floor(top / row) - o), end: Math.min(n, Math.ceil((top + h) / row) + o) }; };
  const swCur = i => i.current ? `<span class="muted">${esc(t('(corrente)'))}</span>`
    : i.incomplete ? `<span class="muted" title="${esc(t('Lo switch verso questo ramo si è interrotto: sceglilo per completarlo'))}">${esc(t('(switch incompleto)'))}</span>` : '';
  const swRowQ = (i, sel, n) => `<div class="rp-row sw-row${sel === i.name ? ' sel' : ''}" id="sw-o${n}" role="option" aria-selected="${sel === i.name}" data-sw="${esc(i.name)}"><b class="rv">${esc(i.kind)}</b><span class="m" title="${esc(i.name)}">${esc(i.name)}</span>${swCur(i)}${i.rev ? `<span class="dtm muted">r${i.rev}</span>` : ''}</div>`;
  U.swRowsHTML = (list, start, end, sel) => list.slice(start, end).map((i, k) => `<div class="rp-row sw-row sw-x${sel === i.name ? ' sel' : ''}" id="sw-o${start + k}" role="option" aria-selected="${sel === i.name}" aria-posinset="${start + k + 1}" aria-setsize="${list.length}" data-sw="${esc(i.name)}"><span class="m" title="${esc(i.name)}">${esc(i.name)} ${swCur(i)}</span><span class="kd">${esc(i.kind)}</span><span class="rvn muted">${i.rev ? 'r' + i.rev : ''}</span><span class="au" title="${esc(i.author)}">${esc(i.author)}</span><span class="dtm muted">${i.date ? U.fmtDate(i.date) : ''}</span></div>`).join('');
  const swUi = () => ({ q: swQ, ext: swExt, kind: swKind, sort: swSort, desc: swDesc, sel: swSel, author: swAuthor, from: swFrom, to: swTo, preset: swPreset });
  const swFiltered = u => !!(u.q.trim() || u.kind || u.author || u.from || u.to);
  // elenco su cui lavorano frecce e Invio: lo stesso che e' disegnato
  const swList = (s, u) => { u = u || swUi(); return u.ext ? U.swFilter(s.items, u) : (q => q.recent.concat(q.rest))(U.swQuick(s.items, s.recent, u.q, SW_LIMIT)); };
  const swSelOk = s => s.items.some(i => i.name === swSel && !i.current);
  function switchHTML(s, ui) {
    if (!s || !s.open) return '';
    const u = Object.assign(swUi(), ui), merge = s.mode === 'merge';
    const note = s.loading ? `<div class="rp-note muted"><span class="spin"></span> ${t('Caricamento di branch e tag…')}</div>` : '';
    const none = s.items.length ? t('Nessun elemento corrisponde alla ricerca.') : t('Nessun branch o tag trovato. Controlla il layout del repository in Tools > Options > SVNAssist.');
    const sortBtn = (k, label) => `<button type="button" class="sw-hb" data-sw-sort="${k}" aria-sort="${u.sort === k ? (u.desc ? 'descending' : 'ascending') : 'none'}" title="${esc(t('Ordina per {0}', label))}"><span>${esc(label)}</span>${u.sort === k ? U.icon(u.desc ? 'chev-d' : 'chev-r') : ''}</button>`;
    let body;
    if (u.ext) {
      const list = swList(s, u), kinds = [['', t('Tutti i tipi')], ['trunk', 'trunk'], ['branch', 'branch'], ['tag', 'tag']];
      const opts = (arr, cur) => arr.map(([v, l]) => `<option value="${esc(v)}"${cur === v ? ' selected' : ''}>${esc(l)}</option>`).join('');
      const authors = U.swAuthors(s.items), presets = [['', t('Qualsiasi data')], ['7', t('Ultimi 7 giorni')], ['30', t('Ultimi 30 giorni')], ['90', t('Ultimi 90 giorni')]];
      body = `<div class="sw-bar"><input id="sw-q" type="text" role="combobox" aria-expanded="true" aria-controls="sw-list" autocomplete="off" spellcheck="false" placeholder="${esc(t('Filtra per nome…'))}" aria-label="${esc(t('Filtra per nome'))}" value="${esc(u.q)}">` +
        `<select id="sw-k" aria-label="${esc(t('Filtra per tipo'))}">${opts(kinds, u.kind)}</select>` +
        `<select id="sw-a" aria-label="${esc(t('Filtra per autore'))}">${opts([['', t('Tutti gli autori')], ...authors.map(a => [a, a])], u.author)}</select></div>` +
        `<div class="sw-bar sw-dates"><select id="sw-p" aria-label="${esc(t('Periodo'))}">${opts(presets, u.preset)}</select>` +
        `<label for="sw-df">${t('Da')}</label><input id="sw-df" type="date" value="${esc(u.from)}"><label for="sw-dt">${t('A')}</label><input id="sw-dt" type="date" value="${esc(u.to)}">` +
        `<span class="fill"></span><span class="muted" role="status">${s.loading ? '' : t('{0} di {1}', list.length, s.items.length)}</span>` +
        `<button type="button" class="btn sm" data-sw-reset ${swFiltered(u) ? '' : 'disabled'} title="${esc(t('Toglie tutti i filtri (nome, tipo, autore, date)'))}"><span>${t('Azzera filtri')}</span></button></div>` +
        `<div class="sw-h sw-x" role="presentation">${sortBtn('name', t('Nome'))}<span>${t('Tipo')}</span>${sortBtn('rev', t('Rev.'))}${sortBtn('author', t('Autore'))}${sortBtn('date', t('Data'))}</div>` +
        (list.length ? `<div class="rp-list sw-vl" id="sw-list" role="listbox" aria-label="${esc(t('Branch e tag del repository'))}"><div id="sw-sp"><div id="sw-rows"></div></div></div>`
          : `<div class="rp-list" id="sw-list" role="listbox" aria-label="${esc(t('Branch e tag del repository'))}">${note || `<div class="rp-note muted">${none}</div>`}</div>`);
    } else {
      const q = U.swQuick(s.items, s.recent, u.q, SW_LIMIT);
      let n = 0;
      const grp = (label, arr) => (arr.length ? `<div class="sw-g muted" role="presentation">${esc(label)}</div>` : '') + arr.map(i => swRowQ(i, u.sel, n++)).join('');
      const rows = q.recent.length ? grp(t('Usati di recente'), q.recent) + grp(t('Tutti i branch e i tag'), q.rest) : grp('', q.rest);
      body = `<input id="sw-q" type="text" role="combobox" aria-expanded="true" aria-controls="sw-list" autocomplete="off" spellcheck="false" placeholder="${esc(t('Cerca…'))}" aria-label="${esc(t('Cerca un branch o un tag'))}" value="${esc(u.q)}">` +
        `<div class="rp-list" id="sw-list" role="listbox" aria-label="${esc(t('Branch e tag del repository'))}">${rows}${note}${!s.loading && !n ? `<div class="rp-note muted">${none}</div>` : ''}` +
        (q.more ? `<div class="rp-note muted">${t('Mostrati {0} di {1}: scrivi per filtrare oppure apri la vista estesa.', q.recent.length + q.rest.length, q.total)}</div>` : '') + `</div>`;
    }
    return `<div class="wiz-scrim"><div class="wiz rp${u.ext ? ' sw-wide' : ''}" role="dialog" aria-modal="true" aria-labelledby="sw-t"><div class="wiz-h"><h3 id="sw-t" tabindex="-1">${merge ? 'SVN Merge' : 'SVN Switch'}</h3><span class="fill"></span>${U.btn('ib', '', 'x', 'switchCancel', null, t('Annulla (Esc)'))}</div>` +
      `<div class="wiz-b"><p class="muted">${merge ? t('Scegli il branch, il trunk o il tag da cui unire le modifiche nella working copy.') : t('Scegli il branch o il tag su cui portare la working copy.')}</p>${body}` +
      (s.error ? `<div class="rp-err" role="alert">${U.icon('warn')}<span>${esc(s.error)}</span></div>` : '') + `</div>` +
      `<div class="wiz-f">${U.btn('btn ghost', t('Annulla'), null, 'switchCancel', null, merge ? t('Chiude senza unire nulla') : t('Chiude senza cambiare ramo'))}${U.btn('btn sm', t('Aggiorna'), 'refresh', 'switchRefresh', null, t("Rilegge l'elenco di branch e tag dal server"))}` +
      (s.items.length ? `<button type="button" class="btn sm" data-sw-ext aria-pressed="${!!u.ext}" title="${esc(u.ext ? t('Torna al selettore veloce') : t('Tabella con filtri per nome e tipo, revisione, autore e data'))}"><span>${u.ext ? t('Vista rapida') : t('Mostra tutti / vista estesa…')}</span></button>` : '') + `<span class="fill"></span>` +
      `<button type="button" class="btn primary" data-sw-ok ${swSelOk(s) && !s.loading ? '' : 'disabled'} title="${esc(merge ? t('Mostra le revisioni di questo ramo ancora da unire (svn mergeinfo)') : t('Porta la working copy sul ramo scelto (svn switch)'))}">${U.icon('branch')}<span>${merge ? 'Merge…' : 'Switch'}</span></button></div></div></div>`;
  }
  // vista estesa: disegna solo le righe nella finestra visibile (il resto e' lo spaziatore); stili via CSSOM (la CSP non ammette lo stile inline negli attributi)
  function swFill() {
    const box = document.getElementById('sw'), s = st && st.switchPicker; if (!box || !s || !s.open || !swExt) return;
    const vl = box.querySelector('#sw-list'), sp = box.querySelector('#sw-sp'), rows = box.querySelector('#sw-rows'), q = box.querySelector('#sw-q'); if (!vl || !sp || !rows) return;
    const list = swList(s), win = U.swWindow(list.length, vl.scrollTop, vl.clientHeight || 340, SW_ROW);
    sp.style.height = list.length * SW_ROW + 'px'; rows.style.top = win.start * SW_ROW + 'px';
    box.style.setProperty('--sbw', Math.max(0, vl.offsetWidth - vl.clientWidth - 2) + 'px');   // barra di scorrimento (meno i due bordi): l'intestazione resta allineata
    U.setHTML(rows, U.swRowsHTML(list, win.start, win.end, swSel));
    const on = rows.querySelector('.sel'); if (q) { if (on) q.setAttribute('aria-activedescendant', on.id); else q.removeAttribute('aria-activedescendant'); }
  }
  // porta la riga i in vista (vista estesa: scorre la lista; veloce: scrollIntoView)
  function swReveal(i) {
    const box = document.getElementById('sw'), vl = box && box.querySelector('#sw-list'); if (!vl) return;
    if (swExt) { const top = i * SW_ROW; if (top < vl.scrollTop) vl.scrollTop = top; else if (top + SW_ROW > vl.scrollTop + vl.clientHeight) vl.scrollTop = top + SW_ROW - vl.clientHeight; swFill(); }
    else { const r = box.querySelector('.sw-row.sel'); if (r) { r.scrollIntoView({ block: 'nearest' }); const q = box.querySelector('#sw-q'); if (q) q.setAttribute('aria-activedescendant', r.id); } }
  }
  function renderSw(app, anyModal) {
    const box = document.getElementById('sw'), s = st.switchPicker; if (!box) return;
    const open = !!(s && s.open), was = !!box.__open, a = document.activeElement;
    if (open && !was) { swSel = ''; swQ = ''; swExt = false; swKind = ''; swSort = 'name'; swDesc = false; swAuthor = ''; swFrom = ''; swTo = ''; swPreset = ''; box.__prev = a; }
    if (open && swSel && !s.items.some(i => i.name === swSel)) swSel = '';
    const inBox = box.contains(a), focusKey = inBox ? (a.id || (a.dataset && ('swSort' in a.dataset ? 'sort:' + a.dataset.swSort : 'swExt' in a.dataset ? 'ext' : ''))) : '';
    const caret = inBox && a.id === 'sw-q' ? a.selectionStart : 0, vl0 = box.querySelector('#sw-list'), top = vl0 ? vl0.scrollTop : 0;
    const changed = U.setHTML(box, switchHTML(s));
    app.classList.toggle('modal-open', anyModal); app.inert = anyModal;
    if (open && changed) {
      const vl = box.querySelector('#sw-list'); if (vl) vl.scrollTop = top;
      const f = !was || !focusKey ? box.querySelector('#sw-q') : focusKey.startsWith('sort:') ? box.querySelector(`[data-sw-sort="${focusKey.slice(5)}"]`) : focusKey === 'ext' ? box.querySelector('[data-sw-ext]') : box.querySelector('#' + CSS.escape(focusKey));
      if (f) { f.focus(); if (f.id === 'sw-q' && was) f.setSelectionRange(caret, caret); }
    }
    if (open) swFill();
    if (!open && was && box.__prev && box.__prev.isConnected) box.__prev.focus();
    box.__open = open;
  }
  const swOk = () => { if (st.switchPicker && swSelOk(st.switchPicker)) U.bridge.send('switchOk', { name: swSel }); };
  function swKeys(e) {
    const box = document.getElementById('sw'); if (!box || !box.__open) return;
    const s = st.switchPicker, a = document.activeElement;
    if (e.key === 'Escape') { e.preventDefault(); U.bridge.send('switchCancel'); return; }
    if (a && a.id === 'sw-q' && ['ArrowDown', 'ArrowUp', 'PageDown', 'PageUp'].includes(e.key)) {
      const list = swList(s); if (!list.length) return; e.preventDefault();
      const i = list.findIndex(x => x.name === swSel), step = e.key.startsWith('Page') ? 10 : 1, dir = e.key.endsWith('Down') ? 1 : -1;
      const to = i < 0 ? (dir > 0 ? 0 : list.length - 1) : Math.max(0, Math.min(list.length - 1, i + dir * step));
      swSel = list[to].name; renderSw(document.querySelector('.app'), true); swReveal(to);
      return;
    }
    if (e.key === 'Enter' && a && a.id === 'sw-q') {
      e.preventDefault();
      const list = swList(s);
      if (!list.some(x => x.name === swSel) && list.length) swSel = list[0].name;
      if (swSelOk(s)) swOk(); else renderSw(document.querySelector('.app'), true);
      return;
    }
    if (e.key !== 'Tab') return;
    const f = [...box.querySelectorAll('button:not(:disabled),input,select')]; if (!f.length) return;
    const first = f[0], last = f[f.length - 1];
    if (e.shiftKey && (a === first || !box.contains(a))) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && (a === last || !box.contains(a))) { e.preventDefault(); first.focus(); }
  }
  function swClick(e, root) {
    const app = root.querySelector('.app'), row = e.target.closest('.sw-row');
    if (row) { swSel = row.dataset.sw; renderSw(app, true); const q = document.querySelector('#sw #sw-q'); if (q) q.focus(); return true; }
    if (e.target.closest('[data-sw-ok]')) { swOk(); return true; }
    if (e.target.closest('[data-sw-ext]')) { swExt = !swExt; renderSw(app, true); const l = document.querySelector('#sw #sw-list'); if (l) l.scrollTop = 0; swFill(); return true; }
    const h = e.target.closest('[data-sw-sort]');
    if (h) { const k = h.dataset.swSort; if (swSort === k) swDesc = !swDesc; else { swSort = k; swDesc = k === 'date' || k === 'rev'; } renderSw(app, true); return true; }   // data e revisione partono dalla piu' recente
    if (e.target.closest('[data-sw-reset]')) { swQ = ''; swKind = ''; swAuthor = ''; swFrom = ''; swTo = ''; swPreset = ''; renderSw(app, true); const l = document.querySelector('#sw #sw-list'); if (l) l.scrollTop = 0; swFill(); const q = document.querySelector('#sw #sw-q'); if (q) q.focus(); return true; }
    return false;
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

  U.changesHTML = { sectionMenu, header, commitBar, foot, revHTML, switchHTML, mergeBanner, conflictsHTML, revIndicator, statusKind, OK_TTL, recentHTML, trackStatus, listHTML };   // parti pure, provate senza DOM (tests/ui-web)

  U.views.changes = {
    mount(root) {
      root.innerHTML = `<div class="app"><div class="prog" aria-hidden="true"></div><header class="bar" id="hdr"></header><div id="wcbox"></div>
        <section class="commit"><textarea id="msg" rows="3"></textarea>
        <div class="bar" id="cbar"></div><div id="aist"></div></section><div id="prq"></div><div id="mrg"></div><div id="cfl"></div>
        <div class="flt"><input id="flt" type="search" spellcheck="false" autocomplete="off"></div>
        <div class="list" id="list" role="tree"></div>
        <footer class="foot" id="foot"></footer></div><div id="wiz"></div><div id="rev"></div><div id="sw"></div><div id="ops"></div><div id="rec" hidden></div>`;
      U.views.changes.relang(root);
      const ta = root.querySelector('#msg');
      ta.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(() => U.bridge.send('setCommitText', { text: ta.value }), 150); });
      ta.addEventListener('keydown', e => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); clearTimeout(timer); U.bridge.send('setCommitText', { text: ta.value }); U.bridge.send('commit'); } });
      root.addEventListener('keydown', wizKeys);
      root.addEventListener('keydown', revKeys);
      root.addEventListener('keydown', swKeys);
      U.ops.wire(root.querySelector("#ops"), () => st);
      root.addEventListener('keydown', rcKeys);
      root.addEventListener('click', e => {   // pannello Recenti: apri/chiudi dal pulsante, scegli una voce, chiudi con un clic fuori
        const b = e.target.closest('[data-recent]'), row = e.target.closest('#rec .rc-row');
        if (b) { if (b.disabled) return; if (rcOpen) rcClose(true); else rcOpenAt(b); }
        else if (row) rcChoose(+row.dataset.i);
        else if (rcOpen && !e.target.closest('#rec')) rcClose(false);
      });
      root.addEventListener('click', e => {   // riquadro Conflitti: comprimi/espandi, elenco dei conflitti ad albero
        const k = e.target.closest('[data-cfl-toggle]') ? 'data-cfl-toggle' : e.target.closest('[data-cfl-tree]') ? 'data-cfl-tree' : '';
        if (!k) return;
        if (k === 'data-cfl-toggle') cflOpen = !cflOpen; else cflTreeOpen = !cflTreeOpen;
        render(); const b = root.querySelector('[' + k + ']'); if (b) b.focus();   // il riquadro e' stato ridisegnato: il focus resta sul pulsante
      });
      root.addEventListener('mouseover', e => { const row = e.target.closest('#rec .rc-row'); if (row && rcOpen && +row.dataset.n !== rcSel) rcSetSel(+row.dataset.n); });
      root.addEventListener('input', e => { if (e.target.id === 'rc-q') { rcQ = e.target.value; rcSel = 0; rcRefresh(); rcSetSel(0); } });
      w.addEventListener('blur', () => rcClose(false));
      root.addEventListener('click', e => { swClick(e, root); });
      root.addEventListener('dblclick', e => { const row = e.target.closest('.sw-row'); if (row) { swSel = row.dataset.sw; swOk(); } });
      root.addEventListener('input', e => { if (e.target.id === 'sw-q') { swQ = e.target.value; renderSw(root.querySelector('.app'), true); const l = document.querySelector('#sw #sw-list'); if (l) l.scrollTop = 0; swFill(); } });
      root.addEventListener('change', e => {
        const id = e.target.id, v = e.target.value; if (!id.startsWith('sw-')) return;
        if (id === 'sw-k') swKind = v; else if (id === 'sw-a') swAuthor = v;
        else if (id === 'sw-p') { swPreset = v; swFrom = v ? U.swPreset(+v) : ''; swTo = ''; }   // il preset riempie "Da" (e svuota "A")
        else if (id === 'sw-df') { swFrom = v; swPreset = ''; } else if (id === 'sw-dt') { swTo = v; swPreset = ''; } else return;
        renderSw(root.querySelector('.app'), true); const l = document.querySelector('#sw #sw-list'); if (l) l.scrollTop = 0; swFill();
      });
      w.addEventListener('resize', () => swFill());   // la vista estesa segue la finestra: ricalcola le righe visibili
      root.addEventListener('scroll', e => { if (e.target.id === 'sw-list') swFill(); }, true);   // lo scroll non risale: ascolto in cattura
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
        const r = b.getBoundingClientRect(); U.menu.open(Math.max(0, r.right - 220), r.bottom + 2, b.dataset.menu === 'ai' ? aiMenu() : b.dataset.menu === 'recent' ? recentMenu() : moreMenu(), b);
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
