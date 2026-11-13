'use strict';
/* Host finto per la UI.Web (demo e test). Parla lo stesso protocollo del backend reale (docs/ui-protocol.md):
   riceve {type:"cmd"|"ready"} dai frame e risponde con {type:"state"|"theme"|"reply"|"event"}.
   Gli snapshot sono costruiti dalla simulazione (S, vm, lvm, prereq) definita in demo.js. */
(function (w) {
  const frames = [];            // frame registrati: {win, view, id, last}
  const handlers = new Map();   // win -> fn (registrato da bridge.js: attach)
  const theme = { mode: 'auto', vs: 'dark' };
  let lastTheme = '', rev = 0;

  // ---------- lingua ----------
  // Come in VS: "auto" = lingua del browser. Il campo lang di ogni messaggio state fa cambiare al volo i testi della UI.Web (js/i18n.js).
  // I testi simulati dal backend finto (stati, messaggi) si traducono qui con un dizionario minimo; la cornice della demo (menu, tour,
  // pannello simulazione) resta in italiano.
  let lang = 'it';
  try {
    const saved = w.localStorage.getItem('svnassist.demoLang');
    lang = saved === 'it' || saved === 'en' ? saved : (typeof navigator !== 'undefined' && !/^it/i.test(navigator.language || 'it') ? 'en' : 'it');
  } catch (e) { /* storage non disponibile */ }
  const TRM = {
    'Staged': 'Staged', 'Modifiche': 'Changes', 'Non versionati': 'Unversioned',
    'Modificato': 'Modified', 'Aggiunto': 'Added', 'Eliminato': 'Deleted', 'Conflitto': 'Conflict', 'Non versionato': 'Unversioned', 'Mancante': 'Missing', 'Sostituito': 'Replaced', 'Normale': 'Normal',
    '(verrà aggiunto con il commit)': '(will be added with the commit)', '. (cartella radice)': '. (root folder)',
    'Caricamento in corso...': 'Loading...', 'Prerequisiti': 'Prerequisites', 'Esegui Cleanup': 'Run Cleanup',
    'Inserisci il percorso della working copy.': 'Enter the path of the working copy.',
    'Inserisci il percorso della working copy e premi Refresh.': 'Enter the path of the working copy and press Refresh.',
    'Inserisci il percorso della working copy e premi Carica log.': 'Enter the path of the working copy and press Load log.',
    'La solution corrente non è una working copy SVN.': 'The current solution is not an SVN working copy.',
    'Working copy SVN rilevata automaticamente, aggiornamento...': 'SVN working copy detected automatically, updating...',
    'Nessuna modifica.': 'No changes.', 'Nessuna working copy configurata.': 'No working copy configured.', 'Nessuno switch da completare.': 'No switch to complete.',
    'Nessuna modifica da committare.': 'No changes to commit.', 'Scrivi un messaggio di commit.': 'Write a commit message.',
    'Commit in corso...': 'Commit in progress...', 'Update in corso...': 'Update in progress...', 'Cleanup in corso...': 'Cleanup in progress...',
    'Cleanup completato.': 'Cleanup completed.',
    'Aggiunta a SVN in corso...': 'Adding to SVN...', 'Eliminazione in corso...': 'Deleting...', 'Lock in corso...': 'Locking...', 'Unlock in corso...': 'Unlocking...', 'Revert in corso...': 'Revert in progress...',
    'Risoluzione conflitto in corso...': 'Resolving the conflict...', 'Risoluzione conflitti ad albero in corso...': 'Resolving tree conflicts...', 'Nessun conflitto ad albero da risolvere.': 'No tree conflicts to resolve.', 'Aggiornamento ignore list in corso...': 'Updating the ignore list...',
    'Nessun elemento non versionato da aggiungere.': 'No unversioned item to add.', 'Seleziona un file da eliminare.': 'Select a file to delete.',
    'Seleziona un file modificato non ancora bloccato.': 'Select a modified file that is not locked yet.', 'Seleziona un file bloccato da te.': 'Select a file locked by you.',
    'Nessun elemento idoneo per SVN Revert.': 'No item suitable for SVN Revert.', 'Seleziona un file in conflitto da risolvere.': 'Select a conflicted file to resolve.',
    'Add to Ignore List è disponibile solo per elementi non versionati.': 'Add to Ignore List is only available for unversioned items.',
    'Nessuna modifica da confrontare in questa cartella.': 'No changes to compare in this folder.',
    'Caricamento log...': 'Loading log...', 'Nessuna revisione trovata.': 'No revision found.', 'Caricamento diff...': 'Loading diff...',
    '(nessuna differenza testuale per questa revisione)': '(no textual difference for this revision)',
    'Generazione in corso...': 'Generating...', 'Miglioramento in corso...': 'Improving...',
    'Messaggio generato con AI.': 'Message generated with AI.', 'Messaggio migliorato.': 'Message improved.',
    'Scrivi prima una bozza del messaggio da migliorare.': 'First write a draft of the message to improve.',
    'Nessun provider AI configurato: completa la procedura guidata.': 'No AI provider configured: complete the setup wizard.',
    'Download del runtime Copilot in corso (~100 MB)...': 'Downloading the Copilot runtime (~100 MB)...', 'Runtime Copilot scaricato.': 'Copilot runtime downloaded.',
  };
  const TRX = [   // testi con numeri/nomi: [regex italiana, sostituzione inglese]
    [/^Errore update: /, 'Error during update: '], [/^Errore cleanup: /, 'Error during cleanup: '], [/^Errore diff: /, 'Error showing diff: '], [/^Errore commit: /, 'Error during commit: '], [/^Errore: /, 'Error: '], [/^Errore (\w+): /, 'Error during $1: '],
    [/^Commit completato: revisione (\d+) \((\d+) elementi\)\.$/, 'Commit completed: revision $1 ($2 items).'], [/^Aggiornato alla revisione (\d+)\.?$/, 'Updated to revision $1.'], [/^Update completato\.?$/, 'Update completed.'],
    [/^(\d+) revisioni caricate( \(filtro attivo\))?\.$/, '$1 revisions loaded$2.'], [/^Diff revisione (\d+) caricato\.$/, 'Diff of revision $1 loaded.'], [/^Caricamento diff per revisione (\d+)\.\.\.$/, 'Loading diff for revision $1...'],
    [/^Risolvi prima i conflitti \((\d+) file\): .*$/, 'Resolve the conflicts first ($1 files): svn does not commit conflicted files.'],
    [/^Update alla revisione (\w+) in corso\.\.\.$/, 'Updating to revision $1...'], [/^Switch a (.+) in corso\.\.\.$/, 'Switching to $1...'],
    [/^Switch a (.+) completato \(revisione (\d+)\)\. Se Visual Studio lo chiede, ricarica la solution\.$/, 'Switched to $1 (revision $2). If Visual Studio asks, reload the solution.'], [/^La working copy è già su (.+)\.$/, 'The working copy is already on $1.'],
    [/^Elemento non valido: .*$/, 'Invalid item: refresh the list and try again.'], [/^"(.*)" non è una revisione valida: .*$/, '"$1" is not a valid revision: type a positive integer (e.g. 1234) or HEAD.'],
    [/^File aperto: (.+)$/, 'File opened: $1'], [/^Merge da (.+)$/, 'Merge from $1'], [/^Lettura delle revisioni da unire\.\.\.$/, 'Reading the revisions to merge...'], [/^(\d+) revisioni da unire\.$/, '$1 revisions to merge.'],
    [/^Nessuna revisione da unire: (.+) è già allineato\.$/, 'No revisions to merge: $1 is already up to date.'], [/^Anteprima completata\.$/, 'Preview completed.'], [/^Merge eseguito, da committare\.$/, 'Merge done, ready to commit.'], [/^Merge eseguito, da committare\. Conflitti da risolvere in Changes: (\d+)\.$/, 'Merge done, ready to commit. Conflicts to resolve in Changes: $1.'],
    [/^(\d+) file in conflitto: risolvili nella sezione Conflitti, poi committa\.$/, '$1 conflicted files: resolve them in the Conflicts section, then commit.'], [/^Conflitti ad albero risolti: (\d+)\.$/, 'Tree conflicts resolved: $1.'], [/^Conflitto risolto: (.+)$/, 'Conflict resolved: $1'],
    [/^Editor di merge aperto \(vsDiffMerge\): .*$/, 'Merge editor opened (vsDiffMerge): save the result, then press "Mark as resolved".'], [/^Editor di merge chiuso senza salvare: .*$/, 'Merge editor closed without saving: the file still has the conflict markers.'],
    [/^Merge completato\.$/, 'Merge completed.'],
    [/^Seleziona almeno una revisione da unire\.$/, 'Select at least one revision to merge.'], [/^Merge completato: rivedi le modifiche e committa\.$/, 'Merge completed: review the changes and commit.'],
    [/^Merge completato: (\d+) file in conflitto da risolvere\.$/, 'Merge completed: $1 conflicted files to resolve.'], [/^Merge annullato: (\d+) elementi ripristinati\.$/, 'Merge undone: $1 items restored.'],
    [/^Annullamento del merge in corso\.\.\.$/, 'Undoing the merge...'], [/^Merge (.+) da (\^\/.+): rivedi le modifiche e committa, oppure annulla il merge\. Messaggio del merge disponibile nei Recenti\.$/, 'Merge $1 from $2: review the changes and commit, or undo the merge. Merge message available in Recent.'], [/^Errore: La working copy è a revisioni miste: [\s\S]*$/, 'Error: The working copy has mixed revisions: run SVN Update and try again.\nsvn: E195020: Cannot merge into mixed-revision working copy'],
    [/^Merge (r[\d, r-]+?)( \(selezione di (\d+) revisioni\))? da (\^\/.+)$/, 'Merge $1$2 from $4'],
    [/^La working copy ha (\d+) elementi con modifiche locali\. Per unire .*$/, 'The working copy has $1 items with local changes. Merging needs a clean working copy (so "Undo merge" does not touch other work): Commit or Revert first, then try again.'],
    [/^(\d+) file cambiati tra r(\d+) e r(\d+)\.$/, '$1 files changed between r$2 and r$3.'], [/^(\d+) file modificati\.$/, '$1 files modified.'],
  ];
  const tr = s => {
    if (lang !== 'en' || typeof s !== 'string' || !s) return s;
    if (TRM[s] != null) return TRM[s];
    for (const [re, to] of TRX) if (re.test(s)) return s.replace(re, to);
    return s;
  };

  // ---------- snapshot ----------
  const kindOf = () => theme.mode === 'auto' ? theme.vs : theme.mode;
  const SECS = [['staged', 'Staged'], ['changes', 'Modifiche'], ['unversioned', 'Non versionati']];
  const STN = c => tr(STNAME[c]);
  const key = (sec, p) => sec + ':' + fwd(p);
  function can(sec, it) {
    return { diff: !!it.canDiff, stage: sec !== 'staged' && it.st !== 'C', unstage: sec === 'staged', add: !!it.canAdd, ignore: !!it.canIgn, revert: !!it.canRev, resolve: !!it.canRes, deleteFile: !it.dir, lock: !it.dir && !it.locked && it.st === 'M', unlock: !!it.locked };
  }
  function fileRow(sec, it, depth, list) {
    const par = parentDir(it.path);
    return { key: key(sec, it.path), kind: 'file', depth, name: it.path === '.' ? tr('. (cartella radice)') : base(it.path), detail: list && par !== '.' ? fwd(par) : '', dir: !!it.dir,
      status: it.st, statusText: STN(it.st) + (sec === 'staged' && it.st === '?' ? ' ' + tr('(verrà aggiunto con il commit)') : ''), can: can(sec, it), tooltip: fwd(it.path) + ' — ' + STN(it.st), ...(it.locked ? { locked: true } : {}) };
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
    return { open: !!S.ui.prereq, summary: tr(S.prq.init ? P.summary : 'Prerequisiti'), ai: P.ai, feedback: tr(S.prq.feedback || ''),
      items: P.items.map(i => ({ id: i.id, group: i.g, title: i.title, ok: !!i.ok, detail: i.detail, instructions: i.ok ? '' : (i.instr || ''), primary: i.p1 || '', secondary: i.p2 || '' })) };
  }
  function aiLevel(v) {
    const m = v.aiMsg || '';
    return v.aiBusy ? 'busy' : !m ? '' : /^Errore/.test(m) ? 'err' : /^(Nessun|Metti almeno|Scrivi prima|Operazione)/.test(m) ? 'warn' : /^Messaggio/.test(m) ? 'ok' : 'info';
  }
  function rpSnap() {   // scelta della revisione: stesse regole del backend (ultime N revisioni dal log)
    const r = S.vm.rp;
    if (!r) return { open: false, target: '', loading: false, error: '', canMore: false, items: [] };
    return { open: true, target: WC, loading: !!r.loading, error: tr(r.error || ''), canMore: S.log.length > r.limit,
      items: S.log.slice(0, r.limit).map(e => ({ rev: e.rev, author: e.author, date: e.date.toISOString(), msg: String(e.msg).split('\n')[0] })) };
  }
  function swSnap() {   // selettore di SVN Switch (stesse regole del backend)
    const s = S.vm.sw;
    if (!s) return { open: false, mode: 'switch', loading: false, error: '', items: [], recent: [] };
    const merge = s.mode === 'merge';   // in modo merge il ramo corrente non e' tra le voci
    return { open: true, mode: s.mode || 'switch', loading: !!s.loading, error: tr(s.error || ''),
      items: s.loading ? [] : SW_ITEMS.filter(([name]) => !merge || name !== S.branch).map(([name, kind, rev, author, days]) => ({ name, kind, current: name === S.branch, rev, author, date: new Date(Date.now() - days * 864e5).toISOString() })),
      recent: (S.swRecent || []).filter(n => SW_ITEMS.some(i => i[0] === n && (!merge || n !== S.branch))) };
  }
  function snapshotChanges() {
    const v = S.vm;
    const sections = SECS.map(([id, title]) => { const items = secItems(id); return { id, title: tr(title), count: items.length, expanded: !v.collapsed['sec:' + id], rows: rowsOf(id, items) }; });
    const msg = v.statusMsg || '';
    return {
      workingCopy: { path: v.wcPath || '', showInput: !!v.showInput, branch: v.branch || '', rev: S.wcRev || 0, svnVersion: '1.14.3', ...(S.wcRev ? { head: S.headRev, revState: S.headRev > S.wcRev ? 'behind' : 'current' } : {}) },
      commit: { text: v.commitMsg || '', aiStatus: tr(v.aiMsg || ''), aiLevel: aiLevel(v), aiSeq: v.aiSeq || 0, busy: !!v.aiBusy, lang: String(v.lang || S.settings.defaultlanguage || 'auto').toLowerCase(),
        trace: (v.trace || []).map(t => ({ name: t.name, state: t.state, detail: t.detail || '' })), download: v.dl ? { pct: v.dl.pct, label: v.dl.label } : null,
        count: commitSet().files.length, all: commitSet().all, recent: (S.recentMsgs || []).slice() },
      view: v.view, theme: { mode: theme.mode, kind: kindOf() }, sections, status: { msg: tr(msg), err: !!v.err || /^Errore/.test(msg), seq: v.statusSeq || 0, ...(/E155004/.test(msg) ? { action: { label: tr('Esegui Cleanup'), cmd: 'cleanup' } } : {}) }, prereq: prereq(), loading: !!v.loading, wizard: wizardSnap(), revPicker: rpSnap(), switchPicker: swSnap(),
      ...(v.mergeBanner && commitSet().files.length ? { merge: { text: tr(v.mergeBanner.text), conflicts: S.files.filter(f => f.st === 'C').length } } : {}),
      conflicts: S.files.filter(f => f.st === 'C').map(f => ({ key: key('changes', f.path), path: fwd(f.path), name: base(f.path), kind: f.tree ? 'tree' : 'text', files: f.tree ? [] : cflAux(f).map(fwd) })),
    };
  }
  function logMatch(e) {   // stessi filtri del backend: sottostringhe senza maiuscole, date incluse
    const f = S.lvm.filter; if (!f) return true;
    const has = (h, n) => !n || String(h).toLowerCase().includes(String(n).toLowerCase()), day = e.date.toISOString().slice(0, 10);
    return has(e.author, f.author) && has(e.msg, f.text) && (!f.path || e.paths.some(p => has(p.p, f.path.replace(/\\/g, '/')))) && (!f.from || day >= f.from) && (!f.to || day <= f.to);
  }
  function mergeSnap(m) {   // il Log in modalita merge (come MergeStore)
    const el = mgEligible(m.name);
    return { source: m.name, title: tr('Merge da ' + m.name), loading: !!m.loading, busy: !!m.busy, error: tr(m.error || ''), blocked: tr(m.blocked || ''), selected: m.sel.slice().sort((a, b) => a - b), eligible: el.length, result: m.result || null, pending: !!(S.vm.mergeBanner && commitSet().files.length), needsUpdate: /E195020/.test(m.error || '') };
  }
  function snapshotLog() {
    const l = S.lvm;
    if (l.merge) {
      const m = l.merge, d = r => { const x = new Date(); x.setDate(x.getDate() - r.days); x.setHours(10, 30, 0, 0); return x.toISOString(); };
      return { path: l.path || '', msg: tr(m.error || m.msg || ''), err: !!m.error, loading: !!(m.loading || m.busy), canMore: false, sel: null,
        entries: mgEligible(m.name).slice().reverse().map(r => ({ rev: r.rev, author: r.author, date: d(r), msg: r.msg, paths: r.files.map(([k, p]) => ({ a: k === 'U' || k === 'C' ? 'M' : k, p: '/' + m.name + '/' + fwd(p) })) })), diff: '',
        filter: l.filter || { author: '', text: '', path: '', from: '', to: '' }, files: null, merge: mergeSnap(m) };
    }
    return { path: l.path || '', msg: tr(l.msg || ''), err: /^Errore/.test(l.msg || ''), loading: /^Caricamento/.test(l.msg || ''), canMore: !!l.canMore, sel: l.sel == null ? null : l.sel,
      entries: l.entries.filter(logMatch).map(e => ({ rev: e.rev, author: e.author, date: e.date.toISOString(), msg: e.msg, paths: e.paths.map(p => ({ a: p.a, p: p.p })) })), diff: tr(l.diff || ''),
      filter: l.filter || { author: '', text: '', path: '', from: '', to: '' }, files: l.files || null };
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
      const sig = lang + s;
      if (f.last !== sig) { f.last = sig; deliver(f, { v: 1, type: 'state', view: f.view, id: f.id, lang, rev: ++rev, state: st }); }
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
    // come Git Changes: un file nuovo (A) o non versionato (?) si apre intero nell'editor, senza diff contro il vuoto
    if (it.st === 'A' || it.st === '?') { await sleep(150); toast('File aperto nell\'editor di Visual Studio: ' + it.path); setStatus('File aperto: ' + fwd(it.path)); return render(); }
    if (S.env.tortoise) { await sleep(250); toast('Diff aperto in TortoiseSVN: ' + it.path); logCmd('TortoiseProc.exe /command:diff /path:"' + WC + '\\' + it.path + '"'); return; }
    await svn(`diff "${fwd(it.path)}"`, 300);
    openDiffTab('__diff:' + it.path, 'Diff ' + base(it.path), `${fwd(it.path)} · BASE ⇄ working copy`, [it.path]);
  }
  const C = {
    refresh: () => { S.vm.statusAuto = true; return vmRefresh(undefined, true); },
    update: () => vmUpdate(),
    updateToRevision: () => vmUpdateRev(),
    revPickerMore: () => rpMore(),
    revPickerCancel: () => { S.vm.rp = null; render(); },
    revPickerOk: a => rpOk(a),
    switchOpen: () => vmSwitchOpen(false),
    switchRefresh: () => vmSwitchOpen(true, S.vm.sw && S.vm.sw.mode),
    mergeOpen: () => vmSwitchOpen(false, 'merge'),
    mergeUndo: () => mgUndo(),
    mergeRevertAll: () => mgRevertAll(),
    mergeUpdate: () => mgUpdate(),
    mergeResolve: () => { S.ui.statusOpen = true; S.ui.view = 'dock'; S.ui.seOpen = false; S.ui.menu = null; render(); },
    conflictOp: a => conflictOp(a.op, a.key),
    resolveTreeConflicts: () => resolveTreeConflicts(),
    mergeDismiss: () => { S.vm.mergeBanner = null; render(); },
    mergeToggle: a => { const m = S.lvm.merge; if (m && !m.busy && mgEligible(m.name).some(r => r.rev === +a.rev)) { m.sel = m.sel.includes(+a.rev) ? m.sel.filter(x => x !== +a.rev) : m.sel.concat(+a.rev); m.result = null; render(); } },
    mergeClear: () => { const m = S.lvm.merge; if (m) { m.sel = []; m.result = null; render(); } },
    mergePreview: () => mgPreview(),
    mergeAll: () => mgRun(true),
    mergeSelected: () => mgRun(false),
    mergeClose: () => { S.lvm.merge = null; return lvmLoad(50); },
    switchCancel: () => { S.vm.sw = null; render(); },
    switchOk: a => swOk(a),
    switchResume: () => { setStatus('Nessuno switch da completare.'); render(); },   // la demo non simula switch interrotti
    showLog: () => {
      if (!S.vm.wcPath.trim()) { setStatus('Nessuna working copy configurata.'); return render(); }
      return openLog().then(() => { if (!S.lvm.entries.length) { S.lvm.path = S.vm.wcPath; return lvmLoad(50); } });
    },
    cleanup: () => vmCleanup(),
    commit: () => vmCommit(),
    useRecentMessage: a => { const m = (S.recentMsgs || [])[+a.i]; if (m != null) { S.vm.commitMsg = m; render(); } },
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
    logLoad: a => { S.lvm.merge = null; S.lvm.path = String(a.path || ''); return lvmLoad(50); },
    logMore: () => lvmMore(),
    logSelect: a => { S.lvm.files = null; return lvmSelect(+a.rev); },
    logFilter: a => {
      const f = { author: '', text: '', path: '', from: '', to: '', ...Object.fromEntries(Object.entries(a || {}).filter(([k]) => ['author', 'text', 'path', 'from', 'to'].includes(k)).map(([k, v]) => [k, String(v).trim()])) };
      if (f.from && f.to && f.from > f.to) [f.from, f.to] = [f.to, f.from];
      S.lvm.filter = f; S.lvm.msg = S.lvm.entries.filter(logMatch).length + ' revisioni caricate' + (Object.values(f).some(Boolean) ? ' (filtro attivo).' : '.'); render();
    },
    logOpenFile: a => { const e = S.lvm.entries.find(x => x.rev === +a.rev), p = e && e.paths[+a.i]; if (p) toast('Diff a due pannelli aperto in Visual Studio: ' + p.p.split('/').pop() + ' r' + (e.rev - 1) + ' ↔ r' + e.rev); },
    logCompare: a => {
      const lo = Math.min(+a.a, +a.b), hi = Math.max(+a.a, +a.b), seen = new Set(), items = [];
      for (const e of S.lvm.entries) if (e.rev > lo && e.rev <= hi) for (const p of e.paths) if (!seen.has(p.p)) { seen.add(p.p); items.push({ a: p.a, p: p.p }); }
      S.lvm.files = { kind: 'compare', title: `File cambiati tra r${lo} e r${hi}`, items }; S.lvm.msg = items.length + ` file cambiati tra r${lo} e r${hi}.`; render();
    },
    logFolder: a => { S.lvm.files = { kind: 'folder', title: 'Modifiche in ' + String(a.path || ''), items: [{ a: 'M', p: 'Services\\OrderService.cs' }, { a: 'A', p: 'Services\\PaymentLog.cs' }] }; S.lvm.msg = '2 file modificati.'; render(); },
    logFileOpen: a => { const p = S.lvm.files && S.lvm.files.items[+a.i]; if (p) toast('Diff a due pannelli aperto in Visual Studio: ' + p.p.split('/').pop()); },
    logFilesPatch: () => toast('Patch aperta come documento (.diff).'),
    logFilesClose: () => { S.lvm.files = null; render(); },
  };

  w.__mockBridge = {
    theme, commands: Object.keys(C), snapshotChanges, snapshotLog, snapshotDiff, pushAll,
    getLang: () => lang,
    setLang(l) { lang = l === 'en' ? 'en' : 'it'; try { w.localStorage.setItem('svnassist.demoLang', lang); } catch (e) { /* storage non disponibile */ } pushAll(); },
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
