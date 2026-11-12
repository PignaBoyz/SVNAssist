'use strict';
/* Host finto per la UI.Web (demo e test). Parla lo stesso protocollo del backend reale (docs/ui-protocol.md):
   riceve {type:"cmd"|"ready"} dai frame e risponde con {type:"state"|"theme"|"reply"|"event"}.
   Gli snapshot sono costruiti dalla simulazione (S, vm, lvm, prereq) definita in demo.js. */
(function (w) {
  const frames = [];            // frame registrati: {win, view, id, last}
  const handlers = new Map();   // win -> fn (registrato da bridge.js: attach)
  const theme = { mode: 'auto', vs: 'dark' };
  let lastTheme = '', rev = 0;

  // ---------- snapshot ----------
  const kindOf = () => theme.mode === 'auto' ? theme.vs : theme.mode;
  const SECS = [['staged', 'Staged'], ['changes', 'Modifiche'], ['unversioned', 'Non versionati']];
  const key = (sec, p) => sec + ':' + fwd(p);
  function can(sec, it) {
    return { diff: !!it.canDiff, stage: sec !== 'staged' && it.st !== 'C', unstage: sec === 'staged', add: !!it.canAdd, ignore: !!it.canIgn, revert: !!it.canRev, resolve: !!it.canRes, deleteFile: !it.dir };
  }
  function fileRow(sec, it, depth, list) {
    const par = parentDir(it.path);
    return { key: key(sec, it.path), kind: 'file', depth, name: it.path === '.' ? '. (cartella radice)' : base(it.path), detail: list && par !== '.' ? fwd(par) : '', dir: !!it.dir,
      status: it.st, statusText: STNAME[it.st] + (sec === 'staged' && it.st === '?' ? ' (verrà aggiunto con il commit)' : ''), can: can(sec, it), tooltip: fwd(it.path) + ' — ' + STNAME[it.st] };
  }
  function rowsOf(sec, items) {
    const rows = [], v = S.vm;
    if (v.view === 'list') {
      items.slice().sort((a, b) => base(a.path).localeCompare(base(b.path), 'it', { sensitivity: 'base' }) || a.path.localeCompare(b.path)).forEach(it => rows.push(fileRow(sec, it, 0, true)));
      return rows;
    }
    const walk = (node, depth) => {
      node.dirs.slice().sort(byLabel).forEach(d => {
        const { cur, label } = compactNode(d), ck = 's:' + sec + ':' + cur.path, exp = !v.collapsed[ck];
        const its = under(items, cur.path);
        rows.push({ key: key(sec, cur.path), kind: 'folder', depth, name: fwd(label), detail: '', expanded: exp, count: its.length, status: null, statusText: '',
          can: { diff: its.some(f => f.st !== '?' && !f.dir), stage: sec !== 'staged' && its.some(f => f.st !== 'C'), unstage: sec === 'staged', add: sec === 'unversioned', ignore: sec === 'unversioned', revert: sec === 'changes', resolve: false, deleteFile: false }, tooltip: fwd(cur.path) });
        if (exp) walk(cur, depth + 1);
      });
      node.files.slice().sort((a, b) => fname(a).localeCompare(fname(b), 'it', { sensitivity: 'base' })).forEach(it => rows.push(fileRow(sec, it, depth, false)));
    };
    walk(buildTree(items), 0);
    return rows;
  }
  function prereq() {
    const P = prqSnap();
    return { open: !!S.ui.prereq, summary: S.prq.init ? P.summary : 'Prerequisiti', ai: P.ai, feedback: S.prq.feedback || '',
      items: P.items.map(i => ({ id: i.id, group: i.g, title: i.title, ok: !!i.ok, detail: i.detail, instructions: i.ok ? '' : (i.instr || ''), primary: i.p1 || '', secondary: i.p2 || '' })) };
  }
  function aiLevel(v) {
    const m = v.aiMsg || '';
    return v.aiBusy ? 'busy' : !m ? '' : /^Errore/.test(m) ? 'err' : /^(Nessun|Metti almeno|Scrivi prima|Operazione)/.test(m) ? 'warn' : /^Messaggio/.test(m) ? 'ok' : 'info';
  }
  function snapshotChanges() {
    const v = S.vm;
    const sections = SECS.map(([id, title]) => { const items = secItems(id); return { id, title, count: items.length, expanded: !v.collapsed['sec:' + id], rows: rowsOf(id, items) }; });
    const msg = v.statusMsg || '';
    return {
      workingCopy: { path: v.wcPath || '', showInput: !!v.showInput, branch: v.branch || '', rev: S.wcRev || 0 },
      commit: { text: v.commitMsg || '', aiStatus: v.aiMsg || '', aiLevel: aiLevel(v), aiSeq: v.aiSeq || 0, busy: !!v.aiBusy, lang: String(v.lang || S.settings.defaultlanguage || 'auto').toLowerCase(),
        trace: (v.trace || []).map(t => ({ name: t.name, state: t.state, detail: t.detail || '' })), download: v.dl ? { pct: v.dl.pct, label: v.dl.label } : null },
      view: v.view, theme: { mode: theme.mode, kind: kindOf() }, sections, status: { msg, err: !!v.err || /^Errore/.test(msg) }, prereq: prereq(), loading: !!v.loading, wizard: wizardSnap(),
    };
  }
  function snapshotLog() {
    const l = S.lvm;
    return { path: l.path || '', msg: l.msg || '', err: /^Errore/.test(l.msg || ''), loading: /^Caricamento/.test(l.msg || ''), canMore: !!l.canMore, sel: l.sel == null ? null : l.sel,
      entries: l.entries.map(e => ({ rev: e.rev, author: e.author, date: e.date.toISOString(), msg: e.msg, paths: e.paths.map(p => ({ a: p.a, p: p.p })) })), diff: l.diff || '' };
  }
  function snapshotDiff(id) {
    const d = S.ui.diffs.find(x => x.id === id); if (!d) return { title: 'Diff', subtitle: '', diff: '' };
    return { title: d.title, subtitle: d.subtitle, diff: d.paths.map(p => fileDiffText(p).trimEnd()).filter(Boolean).join('\n\n') };
  }
  const stateFor = f => f.view === 'changes' ? snapshotChanges() : f.view === 'log' ? snapshotLog() : snapshotDiff(f.id);

  // ---------- trasporto ----------
  const deliver = (f, m) => { const fn = handlers.get(f.win); if (fn) queueMicrotask(() => { try { fn(JSON.parse(JSON.stringify(m))); } catch (e) { console.error(e); } }); };
  function pushAll() {
    const t = JSON.stringify({ kind: kindOf() });
    for (let i = frames.length - 1; i >= 0; i--) {   // scarta i frame distrutti
      const f = frames[i]; let alive = false; try { alive = f.win.frameElement && f.win.frameElement.isConnected; } catch (e) { alive = false; }
      if (!alive) { frames.splice(i, 1); handlers.delete(f.win); }
    }
    for (const f of frames) {
      if (f.theme !== t) { f.theme = t; deliver(f, { v: 1, type: 'theme', kind: kindOf(), vars: {} }); }
      const st = stateFor(f), s = JSON.stringify(st);
      if (f.last !== s) { f.last = s; deliver(f, { v: 1, type: 'state', view: f.view, id: f.id, rev: ++rev, state: st }); }
    }
    lastTheme = t;
    setTimeout(() => { try { applyTarget(); } catch (e) { } }, 0);
  }

  // ---------- comandi ----------
  function stageKey(k, on) {
    const row = getRow(k); if (!row) return;
    row.entries.filter(f => !on || f.st !== 'C').forEach(f => { f.sel = on; });
    render();
  }
  const folderCk = k => { const r = getRow(k); return 's:' + r.section + ':' + r.key; };
  function allFolderCks() {
    const out = [];
    const walk = (node, sec) => node.dirs.forEach(d => { const { cur } = compactNode(d); out.push('s:' + sec + ':' + cur.path); walk(cur, sec); });
    SECS.forEach(([id]) => walk(buildTree(secItems(id)), id));
    return out;
  }
  function openDiffTab(id, title, subtitle, paths) {
    if (!S.ui.diffs.some(d => d.id === id)) S.ui.diffs.push({ id, title, subtitle, paths });
    S.ui.active = id; S.ui.view = 'doc'; S.ui.seOpen = false; render();
  }
  async function showDiff(k, folder) {
    const row = getRow(k);
    if (folder) {
      const fs = row.entries.filter(f => f.st !== '?' && !f.dir);
      if (!fs.length) { setStatus('Nessuna modifica da confrontare in questa cartella.'); return render(); }
      await svn(`diff "${fwd(row.key || '.')}"`, 300);
      return openDiffTab('__diff:' + k, 'Diff ' + (row.key ? base(row.key) : row.name), `${fwd(row.key || '.')} · ${fs.length} file`, fs.map(f => f.path));
    }
    const it = row.file; if (!it || !it.canDiff) return;
    if (S.env.tortoise) { await sleep(250); toast('Diff aperto in TortoiseSVN: ' + it.path); logCmd('TortoiseProc.exe /command:diff /path:"' + WC + '\\' + it.path + '"'); return; }
    await svn(`diff "${fwd(it.path)}"`, 300);
    openDiffTab('__diff:' + it.path, 'Diff ' + base(it.path), `${fwd(it.path)} · BASE ⇄ working copy`, [it.path]);
  }
  const C = {
    refresh: () => { S.vm.statusAuto = true; return vmRefresh(undefined, true); },
    update: () => vmUpdate(),
    commit: () => vmCommit(),
    setCommitText: a => { S.vm.commitMsg = String(a.text || ''); },   // niente render: e' la UI a tenere il testo
    genAi: () => vmAi('gen'),
    improveAi: () => vmAi('improve'),
    setLanguage: a => { S.vm.lang = a.lang; render(); },
    setWorkingCopy: a => { S.vm.wcPath = String(a.path || ''); return vmRefresh(undefined, true); },
    stage: a => stageKey(a.key, true),
    unstage: a => stageKey(a.key, false),
    toggleFolder: a => { const ck = folderCk(a.key); S.vm.collapsed[ck] = !S.vm.collapsed[ck]; render(); },
    toggleSection: a => { S.vm.collapsed['sec:' + a.sec] = !S.vm.collapsed['sec:' + a.sec]; render(); },
    expandAll: () => { S.vm.collapsed = {}; render(); },
    collapseAll: () => { allFolderCks().forEach(k => { S.vm.collapsed[k] = true; }); render(); },
    setView: a => { S.vm.view = a.view === 'list' ? 'list' : 'tree'; render(); },
    showDiff: a => showDiff(a.key, false),
    showFolderDiff: a => showDiff(a.key, true),
    rowOp: a => rowOp(a.op, a.key),
    setTheme: a => { theme.mode = ['auto', 'light', 'dark', 'blue'].includes(a.mode) ? a.mode : 'auto'; render(); },
    prereqToggle: () => ACT.prereqToggle(),
    prereqRecheck: () => prqRecheck(),
    prereqRun: a => prqRun(a.id + '|' + (a.primary ? 1 : 2)),
    ...Object.fromEntries(['wizardOpen', 'wizardOpenPrereq', 'wizardCancel', 'wizardPick', 'wizardBack', 'wizardNext', 'wizardRecheck', 'wizardRun', 'wizardLogin', 'wizardSetAuth', 'wizardFinish', 'openOptions'].map(n => [n, a => WIZ[n](a)])),   // WIZ e' definito in demo.js (caricato dopo)
    logLoad: a => { S.lvm.path = String(a.path || ''); return lvmLoad(50); },
    logMore: () => lvmMore(),
    logSelect: a => lvmSelect(+a.rev),
  };

  w.__mockBridge = {
    theme, commands: Object.keys(C), snapshotChanges, snapshotLog, snapshotDiff, pushAll,
    attach(win, fn) { handlers.set(win, fn); },
    postMessage(m, win) {
      if (!m || m.v !== 1) return;
      if (m.type === 'ready') {
        const f = { win, view: m.view, id: m.id || null, last: '', theme: '' };
        frames.push(f); pushAll(); return;
      }
      if (m.type !== 'cmd') return;
      const f = frames.find(x => x.win === win), fn = C[m.name];
      const reply = (ok, extra) => f && deliver(f, Object.assign({ v: 1, type: 'reply', id: m.id, ok }, extra));
      if (!fn) return reply(false, { error: 'Comando sconosciuto: ' + m.name });
      Promise.resolve().then(() => fn(m.args || {})).then(() => reply(true, { result: null }), e => reply(false, { error: String(e && e.message || e) }));
    },
    // iframe della UI.Web (srcdoc = pagina incorporata dal build; in sviluppo non c'e')
    makeFrame(host, view, id) {
      const fr = document.createElement('iframe');
      fr.className = 'uiframe'; fr.dataset.view = view; if (id) fr.dataset.id = id; fr.title = view === 'changes' ? 'SVN Changes' : view === 'log' ? 'SVN Log' : 'Diff';
      fr.srcdoc = w.__UI_DOC || '<p>UI non incorporata: usa tools/build-demo.mjs</p>';
      host.appendChild(fr); return fr;
    },
    syncFrames(host, wanted, active) {
      if (!host) return;
      const have = [...host.querySelectorAll('iframe')];
      have.forEach(fr => { if (!wanted.some(([id]) => id === (fr.dataset.id || '__log'))) fr.remove(); });
      for (const [id, view] of wanted) {
        let fr = have.find(x => (x.dataset.id || '__log') === id);
        if (!fr || !fr.isConnected) fr = this.makeFrame(host, view, id === '__log' ? null : id);
        fr.hidden = id !== active;
      }
    },
  };
})(window);
