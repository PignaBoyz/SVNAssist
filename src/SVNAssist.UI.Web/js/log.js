'use strict';
/* Vista "Log / History". state: {path, showInput, msg, err, loading, canMore, sel, entries:[{rev,author,date,msg,paths:[{a,p}]}], diff} */
(function (w) {
  const U = w.SVNUI, esc = U.esc;
  const ACT = { A: 'Aggiunto', M: 'Modificato', D: 'Eliminato', R: 'Sostituito' };
  let st = null;
  U.logHTML = {
    top(s) {
      return `<form class="wc" id="logform"><label for="logp">Working copy</label><input id="logp" type="text" value="${esc(s.path)}" placeholder="C:\\percorso\\della\\working copy" spellcheck="false">` +
        `<button class="btn" type="submit" title="Carica le ultime revisioni (svn log)">${U.icon('refresh')}<span>Carica log</span></button>` +
        (s.canMore ? U.btn('btn', 'Altre 50', null, 'logMore', null, 'Carica altre 50 revisioni più vecchie') : '') + `</form>`;
    },
    list(s) {
      if (!s.entries.length) return `<div class="empty-state"><b>Nessuna revisione caricata</b><span class="muted">Inserisci il percorso e premi Carica log.</span></div>`;
      return s.entries.map(e => `<div class="lr${s.sel === e.rev ? ' sel' : ''}" role="option" aria-selected="${s.sel === e.rev}" tabindex="${(s.sel == null ? s.entries[0].rev : s.sel) === e.rev ? 0 : -1}" data-rev="${e.rev}">` +
        `<b class="rv">r${e.rev}</b><span class="au">${esc(e.author)}</span><span class="dtm muted">${U.fmtDate(e.date)}</span><span class="m" title="${esc(e.msg)}">${esc(e.msg)}</span></div>`).join('');
    },
    detail(s) {
      const e = s.entries.find(x => x.rev === s.sel);
      if (!e) return `<div class="empty-state"><span class="muted">Seleziona una revisione per vederne le modifiche.</span></div>`;
      return `<div class="ld-h"><b>r${e.rev}</b> · ${esc(e.author)} · <span class="muted">${U.fmtDate(e.date)}</span><p>${esc(e.msg)}</p>` +
        `<ul class="paths">${e.paths.map(p => `<li><span class="st ${U.stClass(p.a)}" title="${ACT[p.a] || p.a}">${esc(p.a)}</span><span>${esc(p.p)}</span></li>`).join('')}</ul></div>` + `<div class="ld-d">${U.diffHTML(s.diff)}</div>`;
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
      root.innerHTML = `<div class="app log"><div class="prog" aria-hidden="true"></div><div id="ltop"></div><div class="llist" id="llist" role="listbox" aria-label="Revisioni"></div><div class="ldet" id="ldet"></div><footer class="foot" id="lmsg" role="status"></footer></div>`;
      root.addEventListener('submit', e => { if (e.target.id === 'logform') { e.preventDefault(); U.bridge.send('logLoad', { path: e.target.querySelector('input').value }); } });
      const list = root.querySelector('#llist');
      list.addEventListener('click', e => { const r = e.target.closest('.lr'); if (r) U.bridge.send('logSelect', { rev: +r.dataset.rev }); });
      list.addEventListener('keydown', e => {
        const rs = [...list.querySelectorAll('.lr')], i = rs.indexOf(document.activeElement); if (i < 0) return;
        const to = { ArrowDown: i + 1, ArrowUp: i - 1, Home: 0, End: rs.length - 1 }[e.key];
        if (to != null) { e.preventDefault(); const r = rs[Math.max(0, Math.min(rs.length - 1, to))]; U.bridge.send('logSelect', { rev: +r.dataset.rev }); r.focus(); }
        else if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); U.bridge.send('logSelect', { rev: +rs[i].dataset.rev }); }
      });
    },
    update(root, state) { st = state; render(root); },
  };
})(window);
