'use strict';
/* Vista "Log / History". state: {path, msg, err, loading, canMore, sel, entries:[{rev,author,date,msg,paths:[{a,p,k?}]}], diff,
   filter:{author,text,path,from,to}, files:{kind:'folder'|'compare',title,items:[{a,p}]}|null} */
(function (w) {
  const U = w.SVNUI, esc = U.esc, t = U.t;
  let st = null;
  // Clic su una revisione: Ctrl/Maiusc+clic su un'altra revisione la confronta con quella selezionata.
  U.logClick = (sel, rev, multi) => multi && sel != null && sel !== rev ? { cmd: 'logCompare', args: { a: sel, b: rev } } : { cmd: 'logSelect', args: { rev } };
  // Tastiera sulla tabella: revisione da selezionare (null = tasto ignorato). Frecce/Home/End spostano, Invio/Spazio confermano la corrente.
  U.logKey = (key, revs, cur) => {
    const i = revs.indexOf(cur); if (i < 0) return null;
    const to = { ArrowDown: i + 1, ArrowUp: i - 1, Home: 0, End: revs.length - 1, Enter: i, ' ': i }[key];
    return to == null ? null : revs[Math.max(0, Math.min(revs.length - 1, to))];
  };
  const fld =(name, label, val, ph, type) => `<label for="lf-${name}">${label}</label><input id="lf-${name}" name="${name}" type="${type || 'text'}" value="${esc(val)}"${ph ? ` placeholder="${esc(ph)}"` : ''} spellcheck="false">`;
  // finche' il merge non e' committato o annullato non e' "0 da unire": le revisioni da unire sono gia' ricalcolate
  const mgState = m => m.pending ? t('Merge eseguito, da committare') : t('{0} revisioni da unire', m.eligible);
  let mcur = null;   // revisione col focus nella tabella del merge (roving tabindex): sopravvive ai ridisegni
  U.logHTML = {
    top(s) {
      if (s.merge) return `<div class="mh"><b>${esc(s.merge.title)}</b><span class="muted">· ${mgState(s.merge)}</span><span class="fill"></span>` +
        U.btn('btn sm', t('Chiudi merge'), 'x', 'mergeClose', null, t('Torna al log della working copy')) + `</div>`;
      return `<form class="wc" id="logform"><label for="logp">Working copy</label><input id="logp" type="text" value="${esc(s.path)}" placeholder="${esc(t('C:\\percorso\\della\\working copy'))}" spellcheck="false">` +
        `<button class="btn" type="submit" title="${esc(t('Carica le ultime revisioni (svn log)'))}">${U.icon('refresh')}<span>${t('Carica log')}</span></button>` +
        `</form>` + U.logHTML.filter(s) + U.logHTML.opts(s);
    },
    filter(s) {
      const f = s.filter || {}, on = !!(f.author || f.text || f.path || f.from || f.to);
      return `<form class="lf${on ? ' on' : ''}" id="logfilter" role="search" aria-label="${esc(t('Filtra il log'))}">` +
        fld('author', t('Autore'), f.author, 'mrossi') + fld('text', t('Messaggio'), f.text, t('testo nel messaggio')) + fld('path', t('Percorso'), f.path, 'src/Core') +
        fld('from', t('Dal'), f.from, '', 'date') + fld('to', t('al'), f.to, '', 'date') +
        `<button class="btn sm" type="submit" title="${esc(t('Applica i filtri (svn log --search)'))}">${U.icon('check')}<span>${t('Filtra')}</span></button>` +
        (on ? U.btn('btn sm', t('Azzera'), null, 'logFilter', {}, t('Mostra di nuovo tutte le revisioni')) : '') + `</form>`;
    },
    // opzioni di Show Log (come TortoiseSVN): lo stato e' nel backend, il clic invia entrambe le opzioni
    opts(s) {
      const o = (n, on, label, tip) => `<label class="lo-c" title="${esc(tip)}"><input type="checkbox" name="${n}"${on ? ' checked' : ''}><span>${label}</span></label>`;
      return `<div class="lo" id="logopts" role="group" aria-label="${esc(t('Opzioni del log'))}">` +
        o('stopOnCopy', s.stopOnCopy !== false, t('Ferma alla copia/rinomina'), t('Mostra la storia solo fino alla creazione del ramo (svn log --stop-on-copy); spento segue la storia oltre copie e rinomine')) +
        o('merged', !!s.includeMerged, t('Includi revisioni unite'), t('Mostra sotto ogni merge le revisioni che ha portato (svn log -g)')) + `</div>`;
    },
    files(s) {
      const f = s.files;
      return `<div class="ld-h"><b>${esc(f.title)}</b> <span class="muted">· ${t('{0} file', f.items.length)}</span> ` +
        (f.kind === 'folder' ? U.btn('btn sm', t('Apri come patch'), null, 'logFilesPatch', null, t('Apre un unico file .diff con tutte le modifiche')) : '') +
        U.btn('btn sm', t('Chiudi'), null, 'logFilesClose', null, t('Torna al dettaglio della revisione')) +
        (f.items.length ? `<ul class="paths">${f.items.map((p, i) => U.logHTML.pathRow(p, 'logFileOpen', { i })).join('')}</ul>` : `<p class="muted">${t('Nessun file da mostrare.')}</p>`) + `</div>`;
    },
    // riga di un file: clic = diff a due pannelli nell'editor di Visual Studio (le cartelle non si aprono)
    pathRow(p, cmd, args) {
      const st = `<span class="st ${U.stClass(p.a)}" title="${esc(U.statusName(p.a))}">${esc(p.a)}</span><span class="sn">${esc(U.statusName(p.a))}</span>`;
      return p.k === 'dir' ? `<li>${st}<span class="muted pt" title="${esc(t('Cartella'))}">${esc(p.p)}</span></li>`
        : `<li>${st}<button type="button" class="pl pt" ${U.cmd(cmd, args)} title="${esc(t('Apri il diff in Visual Studio'))}">${esc(p.p)}</button></li>`;
    },
    // tabella (ARIA grid): intestazioni fisse e ridimensionabili, una riga per revisione, "Carica altre" in fondo
    list(s) {
      if (s.merge && !s.entries.length) return s.merge.pending ? `<div class="empty-state"><b>${t('Merge eseguito, da committare')}</b><span class="muted">${t('Le modifiche sono nella working copy: committale o annulla il merge in Changes.')}</span></div>`
        : `<div class="empty-state"><b>${s.merge.loading ? t('Lettura delle revisioni da unire…') : t('Nessuna revisione da unire')}</b><span class="muted">${s.merge.loading ? '' : t('Il ramo scelto non ha modifiche che la working copy non abbia già.')}</span></div>`;
      if (!s.entries.length) return `<div class="empty-state"><b>${t('Nessuna revisione caricata')}</b><span class="muted">${t('Inserisci il percorso e premi Carica log.')}</span></div>`;
      const h = (c, label) => `<span role="columnheader">${label}<i class="rz" data-c="${c}" aria-hidden="true"></i></span>`;
      const picked = s.merge ? new Set(s.merge.selected) : null, cur = s.merge ? (s.entries.some(e => e.rev === mcur) ? mcur : s.entries[0].rev) : (s.sel == null ? s.entries[0].rev : s.sel);
      return `<div class="lh" role="row">${h(1, t('Rev.'))}${h(2, t('Autore'))}${h(3, t('Data'))}<span role="columnheader">${t('Messaggio')}</span></div>` +
        s.entries.map(e => { const on = picked ? picked.has(e.rev) : s.sel === e.rev; const d = e.depth ? Math.min(e.depth, 3) : 0; return `<div class="lr${on ? ' sel' : ''}${d ? ' mg d' + d : ''}${e.reverse ? ' rvm' : ''}" role="row" aria-selected="${on}"${d ? ` aria-level="${d + 1}"` : ''} tabindex="${cur === e.rev ? 0 : -1}" data-rev="${e.rev}">` +
          `<b class="rv" role="gridcell">${picked ? `<span class="ck${on ? ' on' : ''}" aria-hidden="true">${on ? U.icon('check') : ''}</span>` : ''}${d ? `<i class="mgi" title="${esc(t('Entrata per merge'))}" aria-hidden="true">↳</i>` : ''}r${e.rev}</b><span class="au" role="gridcell" title="${esc(e.author)}">${esc(e.author)}</span><span class="dtm muted" role="gridcell">${U.fmtDate(e.date)}</span><span class="m" role="gridcell" title="${esc(e.msg)}">${e.reverse ? `<em class="rvt">${esc(t('merge inverso'))}</em> ` : ''}${esc((e.msg || '').split('\n')[0])}</span></div>`; }).join('') +
        (s.canMore ? `<div class="lmore">${U.btn('btn', t('Carica altre 50'), null, 'logMore', null, t('Carica altre 50 revisioni più vecchie'))}</div>` : '');
    },
    // riga di un file dell'anteprima/merge (azione + percorso, senza diff: il merge non e' ancora committato)
    mergeRow(p) { return `<li><span class="st ${U.stClass(p.a)}" title="${esc(U.statusName(p.a))}">${esc(p.a)}</span><span class="sn">${esc(U.statusName(p.a))}</span><span class="pt">${esc(p.p)}</span></li>`; },
    mergeResult(r) {
      const c = r.counts;
      return `<div class="mres"><b>${r.mode === 'preview' ? t('Anteprima del merge di {0}', r.revs) : t('Merge di {0} eseguito', r.revs)}</b> ` +
        `<span class="muted">· ${t('{0} aggiornati · {1} aggiunti · {2} eliminati · {3} uniti · {4} in conflitto', c.updated, c.added, c.deleted, c.merged, c.conflicted)}</span>` +
        `<p class="muted">${r.mode === 'preview' ? t("L'anteprima non prevede sempre i conflitti ad albero e di proprietà: possono comparire solo con il merge vero.") : t('Le modifiche sono nella working copy, non ancora committate: rivedile in Changes (i conflitti compaiono lì).')}</p>` +
        (r.skipped ? `<p class="muted">${t('{0} percorsi saltati da svn.', r.skipped)}</p>` : '') +
        (r.mode === 'merge' && r.counts.conflicted ? `<div class="mb warn" role="alert">${U.icon('warn')}<span>${t('{0} in conflitto: vanno risolti prima di committare.', r.counts.conflicted)}</span><span class="fill"></span>${U.btn('btn sm primary', t('Risolvi i conflitti'), 'diff', 'mergeResolve', null, t('Apre Changes, sezione Conflitti, con le azioni per ogni file'))}</div>` : '') +
        (r.items.length ? `<div class="pth">${t('Azione')} / ${t('Percorso')} · ${t('{0} file', r.items.length)}</div><ul class="paths">${r.items.map(U.logHTML.mergeRow).join('')}</ul>` : `<p class="muted">${t('Nessun file da mostrare.')}</p>`) + `</div>`;
    },
    mergePanel(s) {
      const m = s.merge, n = m.selected.length, off = m.busy || m.loading || !m.eligible ? 'disabled' : '';
      return `<div class="ld-h mp"><b>${esc(m.title)}</b> <span class="muted">· ${mgState(m)}${n && !m.pending ? ' · ' + t('{0} scelte', n) : ''}</span>` +
        `<p class="muted">${t('Clic su una revisione per sceglierla (cherry-pick). Senza scelta, Anteprima e Merge tutto valgono per tutte le revisioni da unire.')}</p>` +
        (m.blocked ? `<div class="mb warn" role="alert">${U.icon('warn')}<span>${esc(m.blocked)}</span><span class="fill"></span>` +
          U.btn('btn sm primary', t('SVN Revert di tutto…'), 'undo', 'mergeRevertAll', null, t('Annulla tutte le modifiche locali (conferma con l’elenco dei file; i non versionati non si toccano), poi il merge si può rifare'), m.busy ? 'disabled' : '') +
          U.btn('btn sm', t('Vai a Changes'), 'diff', 'mergeResolve', null, t('Apre Changes per committare o rivedere le modifiche locali')) + `</div>` : '') +
        (m.error ? `<div class="rp-err" role="alert">${U.icon('x')}<span>${esc(m.error)}</span>${m.needsUpdate ? '<span class="fill"></span>' + U.btn('btn sm primary', t('SVN Update ora'), 'refresh', 'mergeUpdate', null, t('Aggiorna la working copy (svn update) e ricalcola le revisioni da unire'), m.busy ? 'disabled' : '') : ''}</div>` : '') +
        `<div class="mact">` +
        U.btn('btn', t('Anteprima'), 'refresh', 'mergePreview', null, n ? t('Simula il merge delle {0} revisioni scelte (svn merge --dry-run)', n) : t('Simula il merge di tutte le revisioni da unire (svn merge --dry-run)'), off) +
        U.btn('btn primary', t('Merge tutto'), 'branch', 'mergeAll', null, t('Unisce tutte le revisioni da unire nella working copy (svn merge); non committa'), off) +
        U.btn('btn', t('Merge selezionate ({0})', n), 'branch', 'mergeSelected', null, t('Cherry-pick: unisce solo le revisioni scelte (svn merge -c)'), n ? off : 'disabled') +
        (n ? U.btn('btn ghost', t('Deseleziona'), null, 'mergeClear', null, t('Toglie la scelta a tutte le revisioni'), m.busy ? 'disabled' : '') : '') + `</div></div>` +
        (m.result ? U.logHTML.mergeResult(m.result) : '');
    },
    detail(s) {
      if (s.merge) return U.logHTML.mergePanel(s);
      if (s.files) return U.logHTML.files(s);
      const e = s.entries.find(x => x.rev === s.sel);
      if (!e) return `<div class="empty-state"><span class="muted">${t("Seleziona una revisione per vederne le modifiche. Ctrl+clic su un'altra revisione per confrontarle.")}</span></div>`;
      return `<div class="ld-h"><b>r${e.rev}</b> · ${esc(e.author)} · <span class="muted">${U.fmtDate(e.date)}</span><p class="lmsg">${esc(e.msg)}</p>` +
        `<div class="pth">${t('Azione')} / ${t('Percorso')} · ${t('{0} file', e.paths.length)}</div><ul class="paths">${e.paths.map((p, i) => U.logHTML.pathRow(p, 'logOpenFile', { rev: e.rev, i })).join('')}</ul></div>` + `<div class="ld-d">${U.diffHTML(s.diff)}</div>`;
    },
  };
  function render(root) {
    root.classList.toggle('loading', !!st.loading);
    root.querySelector('#llist').classList.toggle('mrg', !!st.merge);
    root.querySelector('#llist').setAttribute('aria-multiselectable', !!st.merge);
    U.setHTML(root.querySelector('#ltop'), U.logHTML.top(st));
    const list = root.querySelector('#llist'), had = list.contains(document.activeElement), top = list.scrollTop;
    if (U.setHTML(list, U.logHTML.list(st))) { list.scrollTop = top; if (had) { const f = list.querySelector('[tabindex="0"]'); if (f) f.focus(); } }
    U.setHTML(root.querySelector('#ldet'), U.logHTML.detail(st));
    const m = root.querySelector('#lmsg'); m.textContent = st.msg || ''; m.className = 'foot msg' + (st.err ? ' err' : '');
  }
  U.views.log = {
    mount(root) {
      root.innerHTML = `<div class="app log"><div class="prog" aria-hidden="true"></div><div id="ltop"></div><div class="lbody"><div class="llist" id="llist" role="grid" aria-label="${esc(t('Revisioni'))}"></div><div class="ldet" id="ldet"></div></div><footer class="foot" id="lmsg" role="status"></footer></div>`;
      root.addEventListener('submit', e => {
        if (e.target.id === 'logform') { e.preventDefault(); U.bridge.send('logLoad', { path: e.target.querySelector('input').value }); }
        else if (e.target.id === 'logfilter') { e.preventDefault(); const a = {}; for (const i of e.target.querySelectorAll('input')) a[i.name] = i.value; U.bridge.send('logFilter', a); }
      });
      root.addEventListener('change', e => {
        if (!e.target.closest('#logopts')) return;
        const v = n => e.target.closest('#logopts').querySelector(`[name=${n}]`).checked;
        U.bridge.send('logOptions', { stopOnCopy: v('stopOnCopy'), merged: v('merged') });
      });
      const list = root.querySelector('#llist');
      list.addEventListener('click', e => { const r = e.target.closest('.lr'); if (r && st && st.merge) { mcur = +r.dataset.rev; U.bridge.send('mergeToggle', { rev: mcur }); } else if (r) { const c = U.logClick(st && st.sel, +r.dataset.rev, e.ctrlKey || e.shiftKey || e.metaKey); U.bridge.send(c.cmd, c.args); } });
      list.addEventListener('pointerdown', e => {   // trascina il bordo dell'intestazione per cambiare la larghezza della colonna
        const g = e.target.closest('.rz'); if (!g) return; e.preventDefault(); e.stopPropagation();
        const cell = g.parentElement, x0 = e.clientX, w0 = cell.getBoundingClientRect().width;
        const mv = ev => list.style.setProperty('--c' + g.dataset.c, Math.max(40, Math.round(w0 + ev.clientX - x0)) + 'px');
        const up = () => { document.removeEventListener('pointermove', mv); document.removeEventListener('pointerup', up); };
        document.addEventListener('pointermove', mv); document.addEventListener('pointerup', up);
      });
      list.addEventListener('keydown', e => {
        const rs = [...list.querySelectorAll('.lr')], i = rs.indexOf(document.activeElement); if (i < 0) return;
        const rev = U.logKey(e.key, rs.map(r => +r.dataset.rev), +rs[i].dataset.rev); if (rev == null) return;
        e.preventDefault();
        if (st && st.merge) {   // nel merge le frecce spostano il focus, Spazio/Invio scelgono o tolgono la revisione
          mcur = rev; if (e.key === 'Enter' || e.key === ' ') U.bridge.send('mergeToggle', { rev }); else { rs.forEach(r => r.tabIndex = +r.dataset.rev === rev ? 0 : -1); }
          rs.find(r => +r.dataset.rev === rev).focus(); return;
        }
        U.bridge.send('logSelect', { rev }); rs.find(r => +r.dataset.rev === rev).focus();
      });
    },
    update(root, state) { st = state; render(root); },
    relang(root) { const l = root.querySelector('#llist'); if (l) l.setAttribute('aria-label', t('Revisioni')); },
  };
})(window);
