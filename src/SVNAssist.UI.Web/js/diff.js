'use strict';
/* Diff unificato (testo di "svn diff") -> HTML affiancato (side by side), con evidenziazione intra-riga e
   colorazione sintattica C#/XML/JSON di base. Render puro: diffHTML(text) -> string. Sotto ~700px si impila. */
(function (w) {
  const U = w.SVNUI, t = U.t;
  const KW = /^(?:abstract|as|async|await|base|bool|break|case|catch|class|const|continue|decimal|default|delegate|do|double|else|enum|false|finally|float|for|foreach|if|in|int|interface|internal|is|lock|long|namespace|new|null|object|out|override|private|protected|public|readonly|ref|return|sealed|static|string|struct|switch|this|throw|true|try|typeof|using|var|virtual|void|while|param|function)$/;
  const TOK = /(\/\/.*$|"(?:[^"\\]|\\.)*"|'(?:[^'\\]|\\.)*'|\b\d[\d_.]*\b|[A-Za-z_]\w*)/g;
  // tokenizza una riga in [{t:testo, c:classe}]
  function tokens(s) {
    const out = []; let last = 0, m; TOK.lastIndex = 0;
    while ((m = TOK.exec(s))) {
      if (m.index > last) out.push({ t: s.slice(last, m.index) });
      const t = m[0];
      const c = t.startsWith('//') ? 'cm' : t[0] === '"' || t[0] === "'" ? 'str' : /^\d/.test(t) ? 'num' : KW.test(t) ? 'kw' : /^[A-Z]/.test(t) && /[a-z]/.test(t) ? 'ty' : '';
      out.push({ t, c }); last = m.index + t.length;
    }
    if (last < s.length) out.push({ t: s.slice(last) });
    return out;
  }
  // HTML di una riga, con [a,b) evidenziato come "mark" (parte realmente cambiata)
  function lineHTML(s, mark) {
    let pos = 0, h = '';
    for (const k of tokens(s)) {
      const e = pos + k.t.length; let i = 0; const piece = (a, b, inMark) => { if (b <= a) return; const x = U.esc(k.t.slice(a, b)); const y = k.c ? `<i class="tk-${k.c}">${x}</i>` : x; h += inMark ? `<mark>${y}</mark>` : y; };
      if (mark && mark[1] > pos && mark[0] < e) { const a = Math.max(0, mark[0] - pos), b = Math.min(k.t.length, mark[1] - pos); piece(0, a, false); piece(a, b, true); piece(b, k.t.length, false); } else piece(0, k.t.length, false);
      void i; pos = e;
    }
    return h || ' ';
  }
  function changed(a, b) { // parte centrale che differisce
    let p = 0; while (p < a.length && p < b.length && a[p] === b[p]) p++;
    let q = 0; while (q < a.length - p && q < b.length - p && a[a.length - 1 - q] === b[b.length - 1 - q]) q++;
    return [[p, a.length - q], [p, b.length - q]];
  }
  // parsing: [{title, meta:[righe], hunks:[{head, rows:[{l?:{n,s,k},r?:{n,s,k}}]}]}]
  function parse(text) {
    const files = []; let f = null, h = null, o = 0, n = 0, dels = [], adds = [];
    const flush = () => { if (!h) return; const m = Math.max(dels.length, adds.length); for (let i = 0; i < m; i++) h.rows.push({ l: dels[i], r: adds[i] }); dels = []; adds = []; };
    const file = t => { flush(); h = null; f = { title: t, meta: [], hunks: [] }; files.push(f); };
    for (const line of String(text || '').split('\n')) {
      if (line.startsWith('Index: ')) { file(line.slice(7)); continue; }
      if (!f) file('');
      if (/^(={5,}|_{5,})$/.test(line) || line === '') { if (!h) continue; }
      const hm = /^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/.exec(line);
      if (hm) { flush(); h = { head: line, rows: [] }; f.hunks.push(h); o = +hm[1]; n = +hm[2]; continue; }
      if (!h) { if (!/^(---|\+\+\+) /.test(line) && line) f.meta.push(line); continue; }
      const c = line[0], s = line.slice(1);
      if (c === '-') dels.push({ n: o++, s });
      else if (c === '+') adds.push({ n: n++, s });
      else if (c === ' ') { flush(); h.rows.push({ l: { n: o++, s }, r: { n: n++, s }, ctx: true }); }
      else if (c === '\\') { /* "No newline at end of file" */ }
      else if (line) { flush(); h = null; f.meta.push(line); }
    }
    flush();
    return files.filter(x => x.hunks.length || x.meta.length);
  }
  const cell = (c, cls, mark) => c ? `<div class="s ${cls}"><span class="ln">${c.n}</span><span class="cd">${lineHTML(c.s, mark)}</span></div>` : '<div class="s empty"><span class="ln"></span><span class="cd"></span></div>';
  function diffHTML(text) {
    const files = parse(text);
    if (!files.length) return `<div class="df-empty">${t('Nessuna differenza testuale (file binario, non versionato o identico).')}</div>`;
    return files.map(f => `<section class="df">${f.title ? `<h3 class="df-h">${U.icon('file')}<span>${U.esc(f.title)}</span></h3>` : ''}` +
      (f.meta.length ? `<pre class="df-meta">${U.esc(f.meta.join('\n'))}</pre>` : '') +
      f.hunks.map(h => `<div class="df-hunk">${U.esc(h.head)}</div>` + h.rows.map(r => {
        if (r.ctx) return `<div class="r">${cell(r.l, 'ctx')}${cell(r.r, 'ctx')}</div>`;
        const m = r.l && r.r ? changed(r.l.s, r.r.s) : [null, null];
        return `<div class="r">${cell(r.l, 'del', m[0])}${cell(r.r, 'add', m[1])}</div>`;
      }).join('')).join('') + '</section>').join('');
  }
  U.diffHTML = diffHTML; U.parseDiff = parse;
  U.views.diff = {
    mount(root) { root.innerHTML = '<div class="dv"><header class="dv-h"><b id="dvt"></b><span id="dvs" class="muted"></span></header><div class="dv-b" id="dvb"></div></div>'; },
    update(root, st) {
      root.querySelector('#dvt').textContent = st.title || 'Diff'; root.querySelector('#dvs').textContent = st.subtitle || '';
      U.setHTML(root.querySelector('#dvb'), diffHTML(st.diff));
    },
  };
})(window);
