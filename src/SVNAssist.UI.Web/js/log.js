'use strict';
/* Vista "Log / History". state: {path, msg, err, loading, canMore, sel, entries:[{rev,author,date,msg,paths:[{a,p,k?}]}], diff,
   filter:{author,text,path,from,to}, files:{kind:'folder'|'compare',title,items:[{a,p}]}|null} */
(function (w) {
  const U = w.SVNUI, esc = U.esc, t = U.t;
  let st = null;
  // Clic su una revisione: Ctrl/Maiusc+clic su un'altra revisione la confronta con quella selezionata.
  U.logClick = (sel, rev, multi) => multi && sel != null && sel !== rev ? { cmd: 'logCompare', args: { a: sel, b: rev } } : { cmd: 'logSelect', args: { rev } };
  const fld = (name, label, val, ph, type) => `<label for="lf-${name}">${label}</label><input id="lf-${name}" name="${name}" type="${type || 'text'}" value="${esc(val)}"${ph ? ` placeholder="${esc(ph)}"` : ''} spellcheck="false">`;
  U.logHTML = {
    top(s) {
      return `<form class="wc" id="logform"><label for="logp">Working copy</label><input id="logp" type="text" value="${esc(s.path)}" placeholder="${esc(t('C:\\percorso\\della\\working copy'))}" spellcheck="false">` +
        `<button class="btn" type="submit" title="${esc(t('Carica le ultime revisioni (svn log)'))}">${U.icon('refresh')}<span>${t('Carica log')}</span></button>` +
        (s.canMore ? U.btn('btn', t('Altre 50'), null, 'logMore', null, t('Carica altre 50 revisioni più vecchie')) : '') + `</form>` + U.logHTML.filter(s);
    },
    filter(s) {
      const f = s.filter || {}, on = !!(f.author || f.text || f.path || f.from || f.to);
      return `<form class="lf${on ? ' on' : ''}" id="logfilter" role="search" aria-label="${esc(t('Filtra il log'))}">` +
        fld('author', t('Autore'), f.author, 'mrossi') + fld('text', t('Messaggio'), f.text, t('testo nel messaggio')) + fld('path', t('Percorso'), f.path, 'src/Core') +
        fld('from', t('Dal'), f.from, '', 'date') + fld('to', t('al'), f.to, '', 'date') +
        `<button class="btn sm" type="submit" title="${esc(t('Applica i filtri (svn log --search)'))}">${U.icon('check')}<span>${t('Filtra')}</span></button>` +
        (on ? U.btn('btn sm', t('Azzera'), null, 'logFilter', {}, t('Mostra di nuovo tutte le revisioni')) : '') + `</form>`;
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
      const st = `<span class="st ${U.stClass(p.a)}" title="${esc(U.statusName(p.a))}">${esc(p.a)}</span>`;
      return p.k === 'dir' ? `<li>${st}<span class="muted" title="${esc(t('Cartella'))}">${esc(p.p)}</span></li>`
        : `<li>${st}<button type="button" class="pl" ${U.cmd(cmd, args)} title="${esc(t('Apri il diff in Visual Studio'))}">${esc(p.p)}</button></li>`;
    },
    list(s) {
      if (!s.entries.length) return `<div class="empty-state"><b>${t('Nessuna revisione caricata')}</b><span class="muted">${t('Inserisci il percorso e premi Carica log.')}</span></div>`;
      return s.entries.map(e => `<div class="lr${s.sel === e.rev ? ' sel' : ''}" role="option" aria-selected="${s.sel === e.rev}" tabindex="${(s.sel == null ? s.entries[0].rev : s.sel) === e.rev ? 0 : -1}" data-rev="${e.rev}">` +
        `<b class="rv">r${e.rev}</b><span class="au">${esc(e.author)}</span><span class="dtm muted">${U.fmtDate(e.date)}</span><span class="m" title="${esc(e.msg)}">${esc(e.msg)}</span></div>`).join('');
    },
    detail(s) {
      if (s.files) return U.logHTML.files(s);
      const e = s.entries.find(x => x.rev === s.sel);
      if (!e) return `<div class="empty-state"><span class="muted">${t("Seleziona una revisione per vederne le modifiche. Ctrl+clic su un'altra revisione per confrontarle.")}</span></div>`;
      return `<div class="ld-h"><b>r${e.rev}</b> · ${esc(e.author)} · <span class="muted">${U.fmtDate(e.date)}</span><p>${esc(e.msg)}</p>` +
        `<ul class="paths">${e.paths.map((p, i) => U.logHTML.pathRow(p, 'logOpenFile', { rev: e.rev, i })).join('')}</ul></div>` + `<div class="ld-d">${U.diffHTML(s.diff)}</div>`;
    },
  };
  function render(root) {
    root.classList.toggle('loading', !!st.loading);
    U.setHTML(root.querySelector('#ltop'), U.logHTML.top(st));
    const list = root.querySelector('#llist'), had = list.contains(document.activeElement), top = list.scrollTop;
    if (U.setHTML(list, U.logHTML.list(st))) { list.scrollTop = top; if (had) { const f = list.querySelector('[tabindex="0"]'); if (f) f.focus(); } }
    U.setHTML(root.querySelector('#ldet'), U.logHTML.detail(st));
    const m = root.querySelector('#lmsg'); m.textContent = st.msg || ''; m.className = 'foot msg' + (st.err ? ' err' : '');
  }
  U.views.log = {
    mount(root) {
      root.innerHTML = `<div class="app log"><div class="prog" aria-hidden="true"></div><div id="ltop"></div><div class="llist" id="llist" role="listbox" aria-label="${esc(t('Revisioni'))}"></div><div class="ldet" id="ldet"></div><footer class="foot" id="lmsg" role="status"></footer></div>`;
      root.addEventListener('submit', e => {
        if (e.target.id === 'logform') { e.preventDefault(); U.bridge.send('logLoad', { path: e.target.querySelector('input').value }); }
        else if (e.target.id === 'logfilter') { e.preventDefault(); const a = {}; for (const i of e.target.querySelectorAll('input')) a[i.name] = i.value; U.bridge.send('logFilter', a); }
      });
      const list = root.querySelector('#llist');
      list.addEventListener('click', e => { const r = e.target.closest('.lr'); if (r) { const c = U.logClick(st && st.sel, +r.dataset.rev, e.ctrlKey || e.shiftKey || e.metaKey); U.bridge.send(c.cmd, c.args); } });
      list.addEventListener('keydown', e => {
        const rs = [...list.querySelectorAll('.lr')], i = rs.indexOf(document.activeElement); if (i < 0) return;
        const to = { ArrowDown: i + 1, ArrowUp: i - 1, Home: 0, End: rs.length - 1 }[e.key];
        if (to != null) { e.preventDefault(); const r = rs[Math.max(0, Math.min(rs.length - 1, to))]; U.bridge.send('logSelect', { rev: +r.dataset.rev }); r.focus(); }
        else if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); U.bridge.send('logSelect', { rev: +rs[i].dataset.rev }); }
      });
    },
    update(root, state) { st = state; render(root); },
    relang(root) { const l = root.querySelector('#llist'); if (l) l.setAttribute('aria-label', t('Revisioni')); },
  };
})(window);
