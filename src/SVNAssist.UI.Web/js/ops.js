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
    const done = !p.running, det = !done && p.total > 0; // totale stimato da svn diff --summarize: barra vera (<progress>, niente style inline per la CSP)
    const bar = det ? `<progress class="obar det" max="${p.total}" value="${Math.min(p.processed, p.total)}" aria-label="${esc(p.title)}"></progress>`
      : `<div class="obar${done ? (p.ok ? ' ok' : ' err') : ''}" role="progressbar" aria-label="${esc(p.title)}"${done ? ' aria-valuenow="100" aria-valuemin="0" aria-valuemax="100"' : ''}><i></i></div>`;
    const counts = `<span class="muted">${det ? t('{0} di circa {1} elementi', p.processed, p.total) : t('{0} elementi elaborati', p.processed)}</span>` + (p.conflicts ? ` <span class="chip fail">${U.icon('warn')}${t('{0} in conflitto', p.conflicts)}</span>` : '');
    const lines = p.lines.length ? `<ul class="olines" id="ops-lines" tabindex="0" aria-label="${esc(t('Righe di notifica di svn'))}">${p.omitted ? `<li class="ol i"><span class="st"></span><span class="pt muted">${esc(t('… altre righe precedenti non mostrate'))}</span></li>` : ''}${p.lines.map(O.lineHTML).join('')}</ul>`
      : `<div class="olines empty muted">${esc(done ? t('Nessun output da svn') : t('In attesa delle prime righe da svn…'))}</div>`;
    const summary = done ? `<div class="${p.cancelled ? 'rp-err warnb' : p.ok ? (p.conflicts || p.warnings ? 'rp-err warnb' : 'osum ok') : 'rp-err'}" role="alert">${U.icon(p.ok && !p.conflicts && !p.warnings ? 'check' : p.ok || p.cancelled ? 'warn' : 'x')}<span>${esc(p.summary)}</span></div>` : '';
    return `<div class="wiz-scrim"><div class="wiz ops" role="dialog" aria-modal="true" aria-labelledby="ops-t"><div class="wiz-h"><h3 id="ops-t" tabindex="-1">${esc(p.title)}</h3></div>` +
      `<div class="wiz-b"><div class="ophase">${done ? '' : '<span class="spin" aria-hidden="true"></span>'}<b>${esc(done ? (p.cancelled ? t('Operazione annullata') : p.ok ? t('Completato') : t('Operazione non riuscita')) : p.phase)}</b><span class="fill"></span>${counts}</div>${bar}${lines}${summary}</div>` +
      `<div class="wiz-f"><span class="muted">${done ? '' : esc(t('Operazione in corso: la finestra si sblocca da sola al termine.'))}</span><span class="fill"></span>` +
      (p.cancellable ? U.btn('btn', p.cancelling ? t('Annullamento…') : t('Annulla'), 'x', 'opCancel', null, t('Ferma svn: la working copy può restare a metà'), p.cancelling ? 'disabled' : '') : '') +
      (done ? (p.actions || []).map(a => U.btn('btn', a.label, null, a.cmd, a.cmd === 'progressClose' ? { conflicts: false } : null)).join('') : '') +
      (done && p.conflicts ? U.btn('btn', t('Apri i conflitti'), 'diff', 'progressClose', { conflicts: true }, t('Chiude e porta in primo piano Changes, sezione Conflitti')) : '') +
      (done && !(p.actions || []).some(a => a.cmd === 'progressClose') ? U.btn('btn primary', t('Chiudi'), null, 'progressClose', { conflicts: false }, t('Chiude la finestra')) : '') + `</div></div></div>`;
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
      ck('unversioned', t('Delete unversioned files and folders'), t('Cancella dal disco tutto ciò che SVN non conosce (salta .vs e i file in uso)'), { danger: true, extra: true }) +
      ck('ignored', t('Delete ignored files and folders'), t('Cancella dal disco i file ignorati, ad es. bin e obj (salta .vs e i file in uso)'), { danger: true, extra: true }) +
      ck('revert', t('Revert all changes recursively'), t('Annulla tutte le modifiche locali di tutta la working copy (svn revert -R)'), { danger: true }) +
      (c.extras ? '' : `<div class="rp-err" role="note">${U.icon('warn')}<span>${esc(t('Con svn {0} le opzioni oltre il cleanup semplice, e l\'eliminazione dei non versionati/ignorati, non sono disponibili: servono svn 1.9 o successivo.', c.svnVersion || '?'))}</span></div>`) +
      (bad ? `<label class="ock danger confirm"><input type="checkbox" data-ock="confirmed" ${opt.confirmed ? 'checked' : ''}><span><b>${esc(t('Confermo: file eliminati e modifiche annullate non si recuperano.'))}</b></span></label>` : '') +
      `</div><div class="wiz-f">${U.btn('btn ghost', t('Annulla'), null, 'cleanupCancel', null, t('Chiude senza fare nulla'))}<span class="fill"></span>` +
      `<button type="button" class="btn ${bad ? 'danger' : 'primary'}" data-ops-ok ${O.cleanupValid() ? '' : 'disabled'} title="${esc(t('Esegue il Cleanup con le opzioni scelte'))}">${U.icon('refresh')}<span>${bad ? esc(t('Conferma ed esegui')) : 'Cleanup'}</span></button></div></div></div>`;
  };

  // SVN Branch/Tag: i campi stanno nella UI (sopravvivono ai ridisegni) e vanno al backend solo con "branchTagOk".
  const bt = { kind: 'branch', name: '', msg: '', touched: false, src: 'head', rev: '', sw: true };
  O.btReset = b => Object.assign(bt, { kind: b.kind, name: '', msg: '', touched: false, src: 'head', rev: '', sw: true });
  O.btMsg = b => bt.touched ? bt.msg : bt.kind === 'tag' ? t('Crea tag {0} da {1}', bt.name.trim() || '…', b.from) : t('Crea branch {0} da {1}', bt.name.trim() || '…', b.from);
  O.btValid = b => !!bt.name.trim() && !b.busy && (bt.src !== 'rev' || !!bt.rev.trim());
  O.btArgs = b => ({ kind: bt.kind, name: bt.name, message: O.btMsg(b), source: bt.src === 'wc' && !b.dirty ? 'head' : bt.src, rev: bt.rev, switch: bt.sw });
  O.branchTagHTML = b => {
    const tag = bt.kind === 'tag', dest = '^/' + (tag ? b.tags : b.branches) + '/' + (bt.name.trim() || '…'), off = b.busy ? 'disabled' : '';
    const rd = (grp, v, cur, label, hint) => `<label class="ock"><input type="radio" id="bt-${grp}-${v}" name="bt-${grp}" data-bt="${grp}" value="${v}" ${cur === v ? 'checked' : ''} ${off}><span><b>${esc(label)}</b>${hint ? `<small class="muted">${esc(hint)}</small>` : ''}</span></label>`;
    return `<div class="wiz-scrim"><div class="wiz" role="dialog" aria-modal="true" aria-labelledby="ops-t"><div class="wiz-h"><h3 id="ops-t" tabindex="-1">SVN Branch/Tag</h3><span class="fill"></span>${U.btn('ib', '', 'x', 'branchTagCancel', null, t('Annulla (Esc)'))}</div><div class="wiz-b">` +
      `<p class="muted">${esc(t('Crea sul server una copia di {0} (r{1}).', b.from, b.rev))}</p>` +
      `<div class="bt-row">${rd('kind', 'branch', bt.kind, t('Branch'))}${rd('kind', 'tag', bt.kind, t('Tag'))}</div>` +
      `<label for="bt-name">${esc(t('Nome'))}</label><input id="bt-name" type="text" autocomplete="off" spellcheck="false" placeholder="${esc(t('es. feature-x'))}" value="${esc(bt.name)}" ${off}>` +
      `<p class="muted">${esc(t('Destinazione'))}: <code>${esc(dest)}</code></p>` +
      `<label>${esc(t('Copia da'))}</label>` + rd('src', 'head', bt.src, t('Ultima revisione (HEAD) di {0}', b.from), t('Il server non include le modifiche locali')) +
      `<div class="bt-row">${rd('src', 'rev', bt.src, t('Revisione'))}<input id="bt-rev" type="text" inputmode="numeric" autocomplete="off" placeholder="${esc(t('es. 1234'))}" value="${esc(bt.rev)}" ${bt.src === 'rev' && !b.busy ? '' : 'disabled'}></div>` +
      (b.dirty ? rd('src', 'wc', bt.src, t('Working copy'), t('Include le modifiche locali non committate')) : '') +
      `<label for="bt-msg">${esc(t('Messaggio di log'))}</label><textarea id="bt-msg" rows="3" ${off}>${esc(O.btMsg(b))}</textarea>` +
      `<label class="ock"><input id="bt-sw" type="checkbox" data-bt="sw" ${bt.sw ? 'checked' : ''} ${off}><span><b>${esc(tag ? t('Passa subito al nuovo tag') : t('Passa subito al nuovo branch'))}</b></span></label>` +
      (b.error ? `<div class="rp-err" role="alert">${U.icon('warn')}<span>${esc(b.error)}</span></div>` : '') +
      `</div><div class="wiz-f">${U.btn('btn ghost', t('Annulla'), null, 'branchTagCancel', null, t('Chiude senza creare nulla'))}<span class="fill"></span>` +
      `<button type="button" class="btn primary" data-bt-ok ${O.btValid(b) ? '' : 'disabled'} title="${esc(t('Crea la copia sul server (svn copy)'))}">${U.icon('branch')}<span>${b.busy ? esc(t('Creazione…')) : esc(t('Crea'))}</span></button></div></div></div>`;
  };

  // Shelve: il dialogo e' solo della UI (nome e spunta) e va al backend con "shelve". L'HTML non dipende da quanto si digita: i ridisegni non rubano il focus.
  let sh = null;
  O.hasShelve = () => !!sh;
  O.shelveHTML = () => `<div class="wiz-scrim"><div class="wiz" role="dialog" aria-modal="true" aria-labelledby="ops-t"><div class="wiz-h"><h3 id="ops-t" tabindex="-1">Shelve</h3><span class="fill"></span><button type="button" class="ib" data-sh-cancel title="${esc(t('Annulla (Esc)'))}" aria-label="${esc(t('Annulla (Esc)'))}">${U.icon('x')}</button></div><div class="wiz-b">` +
    `<p class="muted">${esc(t('Mette da parte le modifiche in un file .patch dentro la working copy e le toglie da qui: le riprendi con Unshelve.'))}</p>` +
    `<label for="sh-name">${esc(t('Nome'))}</label><input id="sh-name" type="text" autocomplete="off" spellcheck="false" value="${esc(sh.name0)}">` +
    `<label class="ock"><input id="sh-keep" type="checkbox"><span><b>${esc(t('Tieni le modifiche nella working copy'))}</b><small class="muted">${esc(t('Salva solo la patch, senza annullare nulla'))}</small></span></label>` +
    `</div><div class="wiz-f"><button type="button" class="btn ghost" data-sh-cancel title="${esc(t('Chiude senza mettere da parte nulla'))}"><span>${esc(t('Annulla'))}</span></button><span class="fill"></span>` +
    `<button type="button" class="btn primary" data-sh-ok title="${esc(t('Salva la patch nello shelve'))}">${U.icon('check')}<span>Shelve</span></button></div></div></div>`;

  // Un solo contenitore (#ops) per vista; inert/modal-open sull'app li imposta la vista.
  O.render = (box, st) => {
    const open = !!(st.progress || st.switchWarn || st.cleanup || st.commitWarn || st.branchTag || sh), was = !!box.__open, a = document.activeElement;
    const kind = st.progress ? 'p' : st.switchWarn ? 'w' : st.cleanup ? 'c' : st.commitWarn ? 'k' : st.branchTag ? 'b' : sh ? 's' : '';
    if (open && (!was || box.__kind !== kind)) { if (kind === 'c') O.resetCleanup(); if (kind === 'b') O.btReset(st.branchTag); if (!was) box.__prev = a; }
    const btId = kind === 'b' && was && box.contains(a) ? a.id : '', btCaret = btId && 'selectionStart' in a ? a.selectionStart : 0;
    const lines = box.querySelector('#ops-lines'), stick = !lines || lines.scrollTop + lines.clientHeight >= lines.scrollHeight - 8, top = lines ? lines.scrollTop : 0;
    const focusKey = box.contains(a) ? (a.dataset && (a.dataset.ock || ('opsOk' in a.dataset ? 'ok' : ''))) : '';
    const changed = U.setHTML(box, st.progress ? O.progressHTML(st.progress) : st.switchWarn ? O.warnHTML(st.switchWarn) : st.cleanup ? O.cleanupHTML(st.cleanup) : st.commitWarn ? O.commitWarnHTML(st.commitWarn) : st.branchTag ? O.branchTagHTML(st.branchTag) : sh ? O.shelveHTML() : '');
    const l2 = box.querySelector('#ops-lines'); if (l2) l2.scrollTop = stick ? l2.scrollHeight : top;
    if (open && changed && kind === 'b') {
      const f = box.querySelector(btId ? '#' + btId : '#bt-name');   // primo giro: il campo nome; poi il campo in cui si stava scrivendo
      if (f) { f.focus(); if (btId && 'setSelectionRange' in f && btCaret != null && f.type !== 'checkbox' && f.type !== 'radio') f.setSelectionRange(btCaret, btCaret); }
    } else if (open && changed && kind === 's') {
      if (!was || box.__kind !== kind) box.querySelector('#sh-name').select();
    } else if (open && changed) {
      const f = (!was || box.__kind !== kind) ? box.querySelector('#ops-t') : focusKey === 'ok' ? box.querySelector('[data-ops-ok]') : focusKey ? box.querySelector(`[data-ock="${focusKey}"]`) : box.querySelector('.wiz-f .btn.primary,.wiz-f .btn.danger');
      if (f && !(was && box.contains(a) && !focusKey && a.matches('button'))) f.focus();
    }
    if (!open && was && box.__prev && box.__prev.isConnected) box.__prev.focus();
    box.__open = open; box.__kind = kind; box.__st = st;
    return open;
  };
  // Eventi (una volta per vista): spunte, OK del Cleanup, Esc, Tab dentro il modale.
  O.wire = (box, getState) => {
    const shClose = () => { sh = null; O.render(box, getState()); };
    const shOk = () => { const n = box.querySelector('#sh-name'), k = box.querySelector('#sh-keep'); U.bridge.send('shelve', { ...(sh.keys.length ? { keys: sh.keys } : {}), name: n.value, keep: k.checked }); shClose(); };
    // keys vuote = tutte le modifiche; il nome proposto e' "Modifiche del <data ora>"
    O.shelveShow = keys => { sh = { keys, name0: t('Modifiche del {0}', U.fmtDate(new Date().toISOString())) }; O.render(box, getState()); };
    box.addEventListener('click', e => { if (!sh) return; if (e.target.closest('[data-sh-ok]')) shOk(); else if (e.target.closest('[data-sh-cancel]')) shClose(); });
    box.addEventListener('keydown', e => { if (sh && e.key === 'Enter' && e.target.id === 'sh-name') { e.preventDefault(); shOk(); } });
    box.addEventListener('change', e => { const k = e.target.dataset && e.target.dataset.ock; if (k) { O.toggle(k); O.render(box, getState()); } });
    box.addEventListener('click', e => { if (e.target.closest('[data-ops-ok]') && O.cleanupValid()) U.bridge.send('cleanupOk', O.cleanupOpts()); });
    const btOk = () => { const b = getState().branchTag; if (b && O.btValid(b)) U.bridge.send('branchTagOk', O.btArgs(b)); };
    box.addEventListener('click', e => { if (e.target.closest('[data-bt-ok]')) btOk(); });
    box.addEventListener('change', e => {
      const g = e.target.dataset && e.target.dataset.bt; if (!g) return;
      if (g === 'sw') bt.sw = e.target.checked; else if (g === 'kind') bt.kind = e.target.value; else bt.src = e.target.value;
      O.render(box, getState());
    });
    box.addEventListener('input', e => {
      if (!getState().branchTag) return;
      const id = e.target.id;
      if (id === 'bt-name') bt.name = e.target.value; else if (id === 'bt-rev') bt.rev = e.target.value; else if (id === 'bt-msg') { bt.msg = e.target.value; bt.touched = true; } else return;
      O.render(box, getState());
    });
    box.addEventListener('keydown', e => { if (e.key === 'Enter' && (e.target.id === 'bt-name' || e.target.id === 'bt-rev')) { e.preventDefault(); btOk(); } });
    // Annulla alla pressione: le righe di svn ridisegnano la finestra tra mousedown e mouseup e il click andrebbe perso (il backend annulla una volta sola).
    box.addEventListener('pointerdown', e => { const b = e.target.closest('[data-cmd="opCancel"]'); if (b && !b.disabled && e.button === 0) U.bridge.send('opCancel'); });
    document.addEventListener('keydown', e => {
      if (!box.__open) return; const st = getState();
      if (e.key === 'Escape') { if (sh) { e.preventDefault(); shClose(); } else if (st.cleanup && !st.progress) { e.preventDefault(); U.bridge.send('cleanupCancel'); } else if (st.switchWarn && !st.progress) { e.preventDefault(); U.bridge.send('switchWarnCancel'); } else if (st.commitWarn && !st.progress) { e.preventDefault(); U.bridge.send('commitWarnCancel'); } else if (st.branchTag && !st.progress) { e.preventDefault(); if (!st.branchTag.busy) U.bridge.send('branchTagCancel'); } else if (st.progress && !st.progress.running) { e.preventDefault(); U.bridge.send('progressClose', { conflicts: false }); } return; }
      if (e.key !== 'Tab') return;
      const f = [...box.querySelectorAll('button:not(:disabled),input:not(:disabled),textarea:not(:disabled),[tabindex="0"]')]; if (!f.length) return;
      const a = document.activeElement, first = f[0], last = f[f.length - 1];
      if (e.shiftKey && (a === first || !box.contains(a))) { e.preventDefault(); last.focus(); } else if (!e.shiftKey && (a === last || !box.contains(a))) { e.preventDefault(); first.focus(); }
    });
  };
})(window);
