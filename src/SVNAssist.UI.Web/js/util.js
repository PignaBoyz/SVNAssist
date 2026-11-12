'use strict';
/* Utilita' condivise. Nessuna dipendenza esterna, nessun eval, nessun inline script (CSP stretta). */
(function (w) {
  const U = w.SVNUI = w.SVNUI || { views: {} };
  U.esc = s => String(s == null ? '' : s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  // icona dallo sprite SVG in index.html
  U.icon = (name, cls) => `<svg class="ic${cls ? ' ' + cls : ''}" aria-hidden="true" focusable="false"><use href="#i-${name}"/></svg>`;
  // attributi per un elemento che invia un comando: data-cmd + data-args (JSON)
  U.cmd = (name, args) => `data-cmd="${name}"${args ? ` data-args="${U.esc(JSON.stringify(args))}"` : ''}`;
  U.btn = (cls, label, icon, cmd, args, tip, extra) =>
    `<button type="button" class="${cls}" ${cmd ? U.cmd(cmd, args) : ''} title="${U.esc(tip || label)}" ${icon && !label ? `aria-label="${U.esc(tip)}"` : ''} ${extra || ''}>${icon ? U.icon(icon) : ''}${label ? `<span>${U.esc(label)}</span>` : ''}</button>`;
  // aggiorna innerHTML solo se cambia (evita di perdere focus/selezione)
  U.setHTML = (el, html) => { if (el && el.__h !== html) { el.__h = html; el.innerHTML = html; return true; } return false; };
  U.fmtDate = iso => { const d = new Date(iso); if (isNaN(d)) return String(iso || ''); const p = n => String(n).padStart(2, '0'); return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`; };
  U.STATUS = { M: 'Modificato', A: 'Aggiunto', D: 'Eliminato', C: 'Conflitto', '?': 'Non versionato', '!': 'Mancante', R: 'Sostituito', K: 'Bloccato (lock)' };
  U.stClass = s => 'st-' + (s === '?' ? 'U' : s === '!' ? 'X' : s);
})(typeof window !== 'undefined' ? window : globalThis);
