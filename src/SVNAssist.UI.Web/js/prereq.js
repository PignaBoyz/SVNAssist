'use strict';
/* Pannello Prerequisiti. prereqHTML(prereq) e' puro. prereq: {open, summary, ai, feedback, items:[{id,group,title,ok,detail,instructions,primary,secondary}]} */
(function (w) {
  const U = w.SVNUI;
  U.prereqHTML = p => {
    if (!p || !p.open) return '';
    let last = null;
    return `<div class="prq" role="region" aria-label="Prerequisiti"><div class="prq-h"><b>Prerequisiti</b><span class="fill"></span>` +
      U.btn('btn sm', 'Ricontrolla', 'refresh', 'prereqRecheck', null, 'Rilegge lo stato di svn, TortoiseSVN e provider AI') + U.btn('ib', '', 'x', 'prereqToggle', null, 'Chiudi i prerequisiti') + `</div>` +
      `<div class="muted small">${U.esc(p.ai || '')}</div>${p.feedback ? `<div class="small fb" role="status">${U.esc(p.feedback)}</div>` : ''}` +
      p.items.map(it => {
        const g = it.group !== last ? `<h4>${U.esc(it.group)}</h4>` : ''; last = it.group;
        return g + `<div class="pi ${it.ok ? 'ok' : 'ko'}"><span class="pm" title="${it.ok ? 'Presente' : 'Mancante'}">${U.icon(it.ok ? 'check' : 'x')}<span class="sr">${it.ok ? 'Presente' : 'Mancante'}</span></span>` +
          `<div class="pt"><b>${U.esc(it.title)}</b><div class="muted small">${U.esc(it.detail)}</div>` +
          (!it.ok && it.instructions ? `<details><summary>Come si sistema</summary><pre class="cmd">${U.esc(it.instructions)}</pre></details>` : '') +
          (it.primary || it.secondary ? `<div class="pb">${it.primary ? U.btn('btn sm', it.primary, null, 'prereqRun', { id: it.id, primary: true }, it.primary) : ''}${it.secondary ? U.btn('btn sm', it.secondary, null, 'prereqRun', { id: it.id, primary: false }, it.secondary) : ''}</div>` : '') + `</div></div>`;
      }).join('') + `</div>`;
  };
})(window);
