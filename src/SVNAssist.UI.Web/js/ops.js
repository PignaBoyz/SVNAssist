'use strict';
/* Finestre modali delle operazioni lunghe, condivise da Changes e Log: avanzamento (switch, merge, update, cleanup) con le righe
   di notifica di svn in streaming, avviso di switch su working copy sporca, opzioni del Cleanup (come TortoiseSVN).
   state.progress / state.switchWarn / state.cleanup: docs/ui-protocol.md. Le opzioni spuntate restano nella UI fino a "cleanupOk". */
(function (w) {
  const U = w.SVNUI, esc = U.esc, t = U.t;
  const O = U.ops = {};
  const DEF = { status: true, vacuum: false, externals: false, unversioned: false, ignored: false, revert: false, confirmed: false };
  let opt = { ...DEF };
  const DANGER = ['unversioned', 'ignored', 'revert'];
  const destructive = () => DANGER.some(k => opt[k]);
  const runsCleanup = () => opt.status || opt.vacuum || opt.externals || opt.unversioned || opt.ignored;
  O.cleanupValid = () => (runsCleanup() || opt.revert) && (!destructive() || opt.confirmed);
  O.cleanupOpts = () => ({ ...opt, confirmed: destructive() && opt.confirmed });
  O.resetCleanup = () => { opt = { ...DEF }; };
  O.toggle = k => { if (k in opt) { opt[k] = !opt[k]; if (!destructive()) opt.confirmed = false; } };

  const kcls = k => 'st-' + (k === 'G' || k === 'U' ? 'M' : k);
  O.lineHTML = l => `<li class="ol ${l.k === 'C' ? 'c' : l.k === 'i' ? 'i' : ''}"><span class="st ${l.k === 'i' ? '' : kcls(l.k)}">${l.k === 'i' ? '' : esc(l.k)}</span><span class="pt">${esc(l.t)}</span></li>`;

  O.progressHTML = p => {
    const done = !p.running, bar = `<div class="obar${done ? (p.ok ? ' ok' : ' err') : ''}" role="progressbar" aria-label="${esc(p.title)}"${done ? ' aria-valuenow="100" aria-valuemin="0" aria-valuemax="100"' : ''}><i></i></div>`;
    const counts = `<span class="muted">${t('{0} elementi elaborati', p.processed)}</span>` + (p.conflicts ? ` <span class="chip fail">${U.icon('warn')}${t('{0} in conflitto', p.conflicts)}</span>` : '');
    const lines = p.lines.length ? `<ul class="olines" id="ops-lines" tabindex="0" aria-label="${esc(t('Righe di notifica di svn'))}">${p.omitted ? `<li class="ol i"><span class="st"></span><span class="pt muted">${esc(t('… altre righe precedenti non mostrate'))}</span></li>` : ''}${p.lines.map(O.lineHTML).join('')}</ul>`
      : `<div class="olines empty muted">${esc(t('In attesa delle prime righe da svn…'))}</div>`;
    const summary = done ? `<div class="${p.ok ? (p.conflicts ? 'rp-err warnb' : 'osum ok') : 'rp-err'}" role="alert">${U.icon(p.ok && !p.conflicts ? 'check' : p.ok ? 'warn' : 'x')}<span>${esc(p.summary)}</span></div>` : '';
    return `<div class="wiz-scrim"><div class="wiz ops" role="dialog" aria-modal="true" aria-labelledby="ops-t"><div class="wiz-h"><h3 id="ops-t" tabindex="-1">${esc(p.title)}</h3></div>` +
      `<div class="wiz-b"><div class="ophase">${done ? '' : '<span class="spin" aria-hidden="true"></span>'}<b>${esc(done ? (p.ok ? t('Completato') : t('Operazione non riuscita')) : p.phase)}</b><span class="fill"></span>${counts}</div>${bar}${lines}${summary}</div>` +
      `<div class="wiz-f"><span class="muted">${done ? '' : esc(t('Operazione in corso: la finestra si sblocca da sola al termine.'))}</span><span class="fill"></span>` +
      (done && p.conflicts ? U.btn('btn', t('Apri i conflitti'), 'diff', 'progressClose', { conflicts: true }, t('Chiude e porta in primo piano Changes, sezione Conflitti')) : '') +
      (done ? U.btn('btn primary', t('Chiudi'), null, 'progressClose', { conflicts: false }, t('Chiude la finestra')) : '') + `</div></div></div>`;
  };

  O.warnHTML = s => {
    const c = s.counts, chip = (n, label, cls) => n ? `<span class="chip ${cls || ''}"><b>${n}</b> ${esc(label)}</span>` : '';
    const clean = !s.total;
    return `<div class="wiz-scrim"><div class="wiz" role="dialog" aria-modal="true" aria-labelledby="ops-t"><div class="wiz-h"><h3 id="ops-t" tabindex="-1">${esc(t('Switch a {0}', s.target))}</h3></div><div class="wiz-b">` +
      (clean ? `<p>${esc(t('La working copy ora è pulita: puoi fare lo switch.'))}</p>`
        : `<p>${U.icon('warn')} ${esc(t('La working copy ha modifiche locali.'))}</p><div class="ochips">` +
          chip(c.modified, t('modificati')) + chip(c.added, t('aggiunti')) + chip(c.deleted, t('eliminati')) + chip(c.conflicted, t('in conflitto'), 'fail') + chip(c.unversioned, t('non versionati'), 'skip') + `</div>` +
          `<p class="muted">${esc(t('Lo switch porta le modifiche locali sul nuovo ramo e può generare conflitti. I file non versionati non vengono toccati. Puoi fare prima un Cleanup (anche con opzioni) oppure continuare comunque.'))}</p>`) +
      `</div><div class="wiz-f">${U.btn('btn ghost', t('Annulla'), null, 'switchWarnCancel', null, t('Non cambia ramo'))}<span class="fill"></span>` +
      (clean ? '' : U.btn('btn', t('Apri Cleanup…'), 'refresh', 'switchWarnCleanup', null, t('Apre le opzioni del Cleanup; poi torna questo avviso'))) +
      U.btn('btn primary', clean ? t('Esegui lo switch') : t('Continua comunque'), 'branch', 'switchWarnContinue', null, t('Esegue svn switch')) + `</div></div></div>`;
  };

  O.commitWarnHTML = s => `<div class="wiz-scrim"><div class="wiz" role="dialog" aria-modal="true" aria-labelledby="ops-t"><div class="wiz-h"><h3 id="ops-t" tabindex="-1">Commit All</h3></div><div class="wiz-b">` +
    `<p>${U.icon('warn')} ${esc(t('Ci sono {0} file non versionati. Aggiungerli al commit?', s.count))}</p><ul class="olines">${s.items.map(p => `<li class="ol"><span class="st st-?">?</span><span class="pt">${esc(p)}</span></li>`).join('')}${s.more ? `<li class="ol i"><span class="st"></span><span class="pt muted">${esc(t('… e altri {0}', s.more))}</span></li>` : ''}</ul>` +
    `</div><div class="wiz-f">${U.btn('btn ghost', t('Annulla'), null, 'commitWarnCancel', null, t('Non committa nulla'))}<span class="fill"></span>` +
    U.btn('btn', t('No, solo i versionati'), null, 'commitWarnNo', null, t('Committa solo le modifiche dei file versionati; i non versionati restano fuori')) +
    U.btn('btn primary', t('Sì, aggiungi e committa'), 'check', 'commitWarnAdd', null, t('Esegue svn add sui file non versionati e li committa insieme al resto')) + `</div></div></div>`;

  O.cleanupHTML = c => {
    const ck = (k, label, hint, o = {}) => {
      const on = o.mirror != null ? o.mirror : opt[k], off = o.mirror != null || (o.extra && !c.extras);
      return `<label class="ock${o.danger ? ' danger' : ''}${off ? ' off' : ''}"><input type="checkbox" data-ock="${k}" ${on ? 'checked' : ''} ${off ? 'disabled' : ''}><span><b>${esc(label)}</b><small class="muted">${esc(hint)}</small></span></label>`;
    };
    const runs = runsCleanup(), bad = destructive();
    return `<div class="wiz-scrim"><div class="wiz" role="dialog" aria-modal="true" aria-labelledby="ops-t"><div class="wiz-h"><h3 id="ops-t" tabindex="-1">SVN Cleanup</h3><span class="fill"></span>${U.btn('ib', '', 'x', 'cleanupCancel', null, t('Annulla (Esc)'))}</div><div class="wiz-b">` +
      `<p class="muted">${esc(t('Scegli cosa fare sulla working copy.'))}${c.from ? ' ' + esc(t('Dopo tornerai all\'avviso dello switch a {0}.', c.from)) : ''}</p>` +
      ck('status', t('Clean up working copy status'), t('Riprende le operazioni interrotte e ripara lo stato (svn cleanup)')) +
      ck('', t('Break write locks'), t('Sblocca i lock di scrittura: svn cleanup lo fa sempre insieme al ripristino dello stato'), { mirror: runs }) +
      ck('', t('Fix time stamps'), t('Ripara i time stamp registrati: anche questo è incluso in svn cleanup'), { mirror: runs }) +
      ck('vacuum', t('Vacuum pristine copies'), t('Elimina le copie originali non più usate (--vacuum-pristines)'), { extra: true }) +
      ck('externals', t('Include externals'), t('Applica il cleanup anche agli external (--include-externals)'), { extra: true }) +
      `<div class="osep ${bad ? 'on' : ''}">${U.icon('warn')} ${esc(t('Azioni distruttive: non recuperabili'))}</div>` +
      ck('unversioned', t('Delete unversioned files and folders'), t('Cancella dal disco tutto ciò che SVN non conosce (--remove-unversioned)'), { danger: true, extra: true }) +
      ck('ignored', t('Delete ignored files and folders'), t('Cancella dal disco i file ignorati, ad es. bin e obj (--remove-ignored)'), { danger: true, extra: true }) +
      ck('revert', t('Revert all changes recursively'), t('Annulla tutte le modifiche locali di tutta la working copy (svn revert -R)'), { danger: true }) +
      (c.extras ? '' : `<div class="rp-err" role="note">${U.icon('warn')}<span>${esc(t('Con svn {0} le opzioni oltre il cleanup semplice, e l\'eliminazione dei non versionati/ignorati, non sono disponibili: servono svn 1.9 o successivo.', c.svnVersion || '?'))}</span></div>`) +
      (bad ? `<label class="ock danger confirm"><input type="checkbox" data-ock="confirmed" ${opt.confirmed ? 'checked' : ''}><span><b>${esc(t('Confermo: file eliminati e modifiche annullate non si recuperano.'))}</b></span></label>` : '') +
      `</div><div class="wiz-f">${U.btn('btn ghost', t('Annulla'), null, 'cleanupCancel', null, t('Chiude senza fare nulla'))}<span class="fill"></span>` +
      `<button type="button" class="btn ${bad ? 'danger' : 'primary'}" data-ops-ok ${O.cleanupValid() ? '' : 'disabled'} title="${esc(t('Esegue il Cleanup con le opzioni scelte'))}">${U.icon('refresh')}<span>${bad ? esc(t('Conferma ed esegui')) : 'Cleanup'}</span></button></div></div></div>`;
  };

  // Un solo contenitore (#ops) per vista; inert/modal-open sull'app li imposta la vista.
  O.render = (box, st) => {
    const open = !!(st.progress || st.switchWarn || st.cleanup || st.commitWarn), was = !!box.__open, a = document.activeElement;
    const kind = st.progress ? 'p' : st.switchWarn ? 'w' : st.cleanup ? 'c' : st.commitWarn ? 'k' : '';
    if (open && (!was || box.__kind !== kind)) { if (kind === 'c') O.resetCleanup(); if (!was) box.__prev = a; }
    const lines = box.querySelector('#ops-lines'), stick = !lines || lines.scrollTop + lines.clientHeight >= lines.scrollHeight - 8, top = lines ? lines.scrollTop : 0;
    const focusKey = box.contains(a) ? (a.dataset && (a.dataset.ock || ('opsOk' in a.dataset ? 'ok' : ''))) : '';
    const changed = U.setHTML(box, st.progress ? O.progressHTML(st.progress) : st.switchWarn ? O.warnHTML(st.switchWarn) : st.cleanup ? O.cleanupHTML(st.cleanup) : st.commitWarn ? O.commitWarnHTML(st.commitWarn) : '');
    const l2 = box.querySelector('#ops-lines'); if (l2) l2.scrollTop = stick ? l2.scrollHeight : top;
    if (open && changed) {
      const f = (!was || box.__kind !== kind) ? box.querySelector('#ops-t') : focusKey === 'ok' ? box.querySelector('[data-ops-ok]') : focusKey ? box.querySelector(`[data-ock="${focusKey}"]`) : box.querySelector('.wiz-f .btn.primary,.wiz-f .btn.danger');
      if (f && !(was && box.contains(a) && !focusKey && a.matches('button'))) f.focus();
    }
    if (!open && was && box.__prev && box.__prev.isConnected) box.__prev.focus();
    box.__open = open; box.__kind = kind; box.__st = st;
    return open;
  };
  // Eventi (una volta per vista): spunte, OK del Cleanup, Esc, Tab dentro il modale.
  O.wire = (box, getState) => {
    box.addEventListener('change', e => { const k = e.target.dataset && e.target.dataset.ock; if (k) { O.toggle(k); O.render(box, getState()); } });
    box.addEventListener('click', e => { if (e.target.closest('[data-ops-ok]') && O.cleanupValid()) U.bridge.send('cleanupOk', O.cleanupOpts()); });
    document.addEventListener('keydown', e => {
      if (!box.__open) return; const st = getState();
      if (e.key === 'Escape') { if (st.cleanup && !st.progress) { e.preventDefault(); U.bridge.send('cleanupCancel'); } else if (st.switchWarn && !st.progress) { e.preventDefault(); U.bridge.send('switchWarnCancel'); } else if (st.commitWarn && !st.progress) { e.preventDefault(); U.bridge.send('commitWarnCancel'); } else if (st.progress && !st.progress.running) { e.preventDefault(); U.bridge.send('progressClose', { conflicts: false }); } return; }
      if (e.key !== 'Tab') return;
      const f = [...box.querySelectorAll('button:not(:disabled),input:not(:disabled),[tabindex="0"]')]; if (!f.length) return;
      const a = document.activeElement, first = f[0], last = f[f.length - 1];
      if (e.shiftKey && (a === first || !box.contains(a))) { e.preventDefault(); last.focus(); } else if (!e.shiftKey && (a === last || !box.contains(a))) { e.preventDefault(); first.focus(); }
    });
  };
})(window);
