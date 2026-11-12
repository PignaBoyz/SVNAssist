'use strict';
/* Avvio: sceglie la vista (?view=changes|log|diff, oppure data-view dell'iframe nella demo), applica tema e stato ricevuti dall'host. */
(function (w) {
  const U = w.SVNUI;
  const q = new URLSearchParams(location.search);
  let fe = null; try { fe = w.frameElement; } catch (e) { /* cross-origin */ }
  const view = q.get('view') || (fe && fe.dataset.view) || 'changes';
  const vid = q.get('id') || (fe && fe.dataset.id) || null;
  const V = U.views[view]; if (!V) { document.body.textContent = U.t('Vista sconosciuta: {0}', view); return; }
  const root = document.getElementById('root');
  document.documentElement.dataset.view = view;
  V.mount(root);

  function toast(msg) {
    let t = document.getElementById('toast'); if (!t) { t = document.createElement('div'); t.id = 'toast'; t.setAttribute('role', 'status'); document.body.appendChild(t); }
    t.textContent = msg; t.classList.add('on'); clearTimeout(t.__t); t.__t = setTimeout(() => t.classList.remove('on'), 3500);
  }
  function theme(m) {   // {kind:'dark|light|blue', vars:{'--bg0':'#..',...}}
    document.documentElement.dataset.theme = m.kind;
    const s = document.documentElement.style;
    if (w.__themeVars) w.__themeVars.forEach(k => s.removeProperty(k));
    w.__themeVars = Object.keys(m.vars || {}); w.__themeVars.forEach(k => { if (/^--[\w-]+$/.test(k)) s.setProperty(k, m.vars[k]); });   // setProperty (CSSOM) e' ammesso dalla CSP
  }
  U.bridge.onMessage(m => {
    if (!m || typeof m !== 'object') return;
    if (m.type === 'state' && m.view === view && (!m.id || !vid || m.id === vid)) {
      if (m.lang && U.setLang(m.lang) && V.relang) V.relang(root);   // cambio lingua a caldo: i testi statici si rifanno, lo stato ridisegna il resto
      V.update(root, m.state);
    }
    else if (m.type === 'theme') theme(m);
    else if (m.type === 'event') { if (m.name === 'toast') toast(m.text || ''); else if (m.name === 'focus') { const f = root.querySelector('[tabindex="0"],textarea'); if (f) f.focus(); } else if (m.name === 'backendRestarted') toast(U.t('Il servizio SVNAssist è stato riavviato.')); }
    else if (m.type === 'reply' && !m.ok) toast(m.error || U.t('Operazione non riuscita'));
  });
  // comandi dichiarativi: <button data-cmd="nome" data-args='{"k":1}'>
  document.addEventListener('click', e => {
    const b = e.target.closest('[data-cmd]'); if (!b || b.disabled) return;
    let a; try { a = b.dataset.args ? JSON.parse(b.dataset.args) : undefined; } catch (x) { a = undefined; }
    U.bridge.send(b.dataset.cmd, a);
  });
  U.bridge.ready(view, vid);
})(window);
