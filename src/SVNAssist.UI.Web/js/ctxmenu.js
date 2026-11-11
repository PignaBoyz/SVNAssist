'use strict';
/* Menu a comparsa (contestuale, overflow, AI). items: {label, cmd, args, run, checked, disabled, sep, header, kbd, hint} */
(function (w) {
  const U = w.SVNUI; let el = null, back = null;
  U.menu = {
    open(x, y, items, returnFocus) {
      U.menu.close();
      back = returnFocus || document.activeElement;
      el = document.createElement('div'); el.className = 'menu'; el.setAttribute('role', 'menu');
      el.innerHTML = items.map((it, i) => it.sep ? '<hr role="separator">' : it.header ? `<div class="mh" role="presentation">${U.esc(it.label)}</div>` :
        `<button type="button" role="${it.checked != null ? 'menuitemradio' : 'menuitem'}" ${it.checked != null ? `aria-checked="${!!it.checked}"` : ''} data-i="${i}" ${it.disabled ? 'disabled' : ''} ${it.hint ? `title="${U.esc(it.hint)}"` : ''}>` +
        `<span class="mk">${it.checked ? U.icon('check') : ''}</span><span class="ml">${U.esc(it.label)}</span>${it.kbd ? `<kbd>${U.esc(it.kbd)}</kbd>` : ''}</button>`).join('');
      el.__items = items;
      document.body.appendChild(el);
      const r = el.getBoundingClientRect();
      el.style.left = Math.max(2, Math.min(x, innerWidth - r.width - 2)) + 'px';
      el.style.top = Math.max(2, Math.min(y, innerHeight - r.height - 2)) + 'px';
      const f = el.querySelector('button:not(:disabled)'); if (f) f.focus();
    },
    close() { if (!el) return; el.remove(); el = null; if (back && back.isConnected) back.focus(); back = null; },
    get isOpen() { return !!el; },
  };
  document.addEventListener('click', e => {
    if (!el) return;
    const b = e.target.closest('.menu button');
    if (!b) { if (!e.target.closest('[data-menu]')) U.menu.close(); return; }
    const it = el.__items[+b.dataset.i]; U.menu.close();
    if (it.run) it.run(); else if (it.cmd) U.bridge.send(it.cmd, it.args);
  }, true);
  document.addEventListener('keydown', e => {
    if (!el) return;
    const bs = [...el.querySelectorAll('button:not(:disabled)')], i = bs.indexOf(document.activeElement);
    if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); U.menu.close(); }
    else if (e.key === 'ArrowDown') { e.preventDefault(); bs[(i + 1) % bs.length].focus(); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); bs[(i - 1 + bs.length) % bs.length].focus(); }
    else if (e.key === 'Tab') { e.preventDefault(); U.menu.close(); }
  }, true);
  w.addEventListener('blur', () => U.menu.close());
})(window);
