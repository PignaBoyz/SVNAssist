'use strict';
/* Azure DevOps (facoltativo): riga "Work item" sotto il messaggio di commit, tendina dopo #, procedura guidata "Collega Azure DevOps".
   state.azure (solo se l'integrazione e' attiva o la procedura e' aperta): {ready, busy, error, linked[], suggest[]?, states[], targetState, wizard?}.
   Funzioni pure (HTML) + wire(): lo stato vero sta nel backend (AzureStore). docs/ui-protocol.md, comandi az*. */
(function (w) {
  const U = w.SVNUI, esc = U.esc, t = U.t;
  const A = U.azure = { sel: 0 };

  const label = wi => (wi.type ? wi.type + ' ' : '#') + wi.id;

  /** Riga sotto il messaggio: casella numero + Cerca, work item collegati (chip), stato dopo il commit, errore. */
  A.commitHTML = az => {
    if (!az || !az.ready) return '';
    const chips = (az.linked || []).map(wi => `<span class="az-chip"><button type="button" class="az-wi" ${U.cmd('azOpen', { id: wi.id })} title="${esc(t('Apre il work item in Azure DevOps: {0}', wi.title || ''))}">${esc(label(wi))}${wi.state ? ` <span class="muted">· ${esc(wi.state)}</span>` : ''}</button><button type="button" class="ib" data-az-unlink="${wi.id}" title="${esc(t('Toglie il work item dal messaggio'))}" aria-label="${esc(t('Scollega {0}', label(wi)))}">${U.icon('x')}</button></span>`).join('');
    const states = az.states || [];
    const sel = states.length ? `<label class="az-st"><span>${t('Stato dopo il commit')}</span><select id="az-state">` +
      `<option value="">${esc(t('Invariato'))}</option>` +
      states.map(s => `<option value="${esc(s.name)}"${s.ok ? '' : ' disabled'}${az.targetState === s.name ? ' selected' : ''}>${esc(s.name)}${s.via.length ? ' ' + esc(t('(passa per {0})', s.via.join(' → '))) : ''}${s.ok ? '' : ' ' + esc(t('— non permesso'))}</option>`).join('') +
      `</select></label>` : '';
    const blocked = states.filter(s => !s.ok && s.note).map(s => `<div class="muted small">${U.icon('warn')} ${esc(s.note)}</div>`).join('');
    return `<div class="az-row"><input id="az-id" type="text" inputmode="numeric" autocomplete="off" spellcheck="false" placeholder="${esc(t('Work item n.'))}" aria-label="${esc(t('Numero del work item'))}">` +
      `<button type="button" class="btn sm" data-az-find ${az.busy ? 'disabled' : ''} title="${esc(t('Cerca il work item in Azure DevOps e mette tipo, numero, titolo e link in cima al messaggio'))}">${az.busy ? '<span class="spin"></span>' : ''}<span>${t('Cerca')}</span></button>` +
      chips + sel + `</div>` + blocked +
      (az.error ? `<div class="az-err" role="alert">${U.icon('warn')}<span>${esc(az.error)}</span></div>` : '');
  };

  /** Tendina dopo #: i miei work item aperti che iniziano con le cifre scritte. */
  A.suggestHTML = az => {
    const list = az && az.ready && az.suggest;
    if (!list || !list.length) return '';
    const i = Math.min(A.sel, list.length - 1);
    return `<div class="az-sug" role="listbox" aria-label="${esc(t('Work item'))}">` + list.map((wi, n) =>
      `<div class="az-opt${n === i ? ' sel' : ''}" role="option" aria-selected="${n === i}" data-az-pick="${wi.id}"><b>#${wi.id}</b><span class="muted">${esc(wi.type)} · ${esc(wi.state)}</span><span class="az-tt">${esc(wi.title)}</span></div>`).join('') +
      `<div class="muted small az-hint">${esc(t('Invio sceglie, Esc chiude'))}</div></div>`;
  };

  const checkRow = c => `<div class="pi ${c.ok === true ? 'ok' : c.ok === false ? 'ko' : ''}"><span class="pm">${c.ok == null ? '<span class="spin"></span>' : U.icon(c.ok ? 'check' : 'x')}<span class="sr">${c.ok === true ? t('A posto') : c.ok === false ? t('Da sistemare') : t('In corso')}</span></span><div class="pt"><b>${esc(c.label)}</b><div class="muted small">${esc(c.detail || '')}</div></div></div>`;

  /** Procedura guidata: organizzazione, progetto, token; "Verifica" fa i controlli; poi si attiva. */
  A.wizardHTML = az => {
    const z = az && az.wizard;
    if (!z) return '';
    const opt = (key, on, text, tip) => `<label class="az-o" title="${esc(tip)}"><input type="checkbox" data-az-opt="${key}"${on ? ' checked' : ''}> <span>${text}</span></label>`;
    return `<div class="wiz-scrim"><div class="wiz az-wiz" role="dialog" aria-modal="true" aria-labelledby="az-t"><div class="wiz-h"><h3 id="az-t" tabindex="-1">${t('Collega Azure DevOps')}</h3><span class="fill"></span>${U.btn('ib', '', 'x', 'azWizardClose', null, t('Chiudi (Esc)'))}</div>` +
      `<div class="wiz-b"><p class="muted">${t('Facoltativo: collega i commit ai work item. Il messaggio resta leggibile anche senza estensione. Se non lo configuri resta spento.')}</p>` +
      `<label for="az-org">${t('Organizzazione')}</label><input id="az-org" type="text" spellcheck="false" autocomplete="off" placeholder="https://dev.azure.com/azienda" value="${esc(z.org)}">` +
      `<label for="az-proj">${t('Progetto')}</label><input id="az-proj" type="text" spellcheck="false" autocomplete="off" value="${esc(z.project)}">` +
      `<label for="az-pat">${t('Token PAT personale')}</label><input id="az-pat" type="password" autocomplete="off" placeholder="${esc(z.hasPat ? t('Salvato (lascia vuoto per tenerlo)') : t('Incolla il token'))}">` +
      `<div class="muted small">${t('Permesso: Work Items (Read & write). Salvato cifrato in Windows, mai in chiaro.')} <button type="button" class="lnk" data-az-patpage>${t('Crea un token in Azure DevOps')}</button></div>` +
      (z.checks.length ? `<div class="az-checks">${z.checks.map(checkRow).join('')}</div>` : '') +
      `<div class="az-opts">${opt('azLinkOnCommit', z.linkOnCommit, t('Link sul work item dopo il commit'), t('Revisione, ramo e ramo di origine nel commento del link'))}${opt('azComment', z.comment, t('Commento nella discussione'), t('Manda una notifica a chi segue il work item'))}</div></div>` +
      `<div class="wiz-f"><button type="button" class="btn" data-az-check ${z.busy ? 'disabled' : ''}>${z.busy ? '<span class="spin"></span>' : U.icon('refresh')}<span>${t('Verifica')}</span></button><span class="fill"></span>` +
      (z.enabled
        ? `<span class="chip ok">${t('Attiva')}</span>${U.btn('btn ghost', t('Disattiva'), null, 'azOption', { key: 'azEnabled', on: false }, t('Spegne l\'integrazione: nessuna chiamata ad Azure'))}`
        : U.btn('btn primary', t('Attiva integrazione'), 'check', 'azOption', { key: 'azEnabled', on: true }, z.canEnable ? t('Organizzazione, token, progetto e lettura verificati') : t('Prima premi Verifica: servono organizzazione, token, progetto e lettura a posto'), z.canEnable && !z.busy ? '' : 'disabled')) +
      `</div></div></div>`;
  };

  /** Eventi della vista Changes. getState() = snapshot corrente; msg() = textarea del messaggio; flush() = manda subito il testo in attesa. */
  A.wire = (root, getState, msg, flush) => {
    const send = U.bridge.send;
    const link = id => { flush(); A.sel = 0; send('azLink', { id: +id, text: msg().value }); };
    const find = () => { const i = root.querySelector('#az-id'); const v = i ? i.value.trim().replace(/^#/, '') : ''; if (/^\d{1,9}$/.test(v)) { i.value = ''; link(v); } };
    root.addEventListener('click', e => {
      if (e.target.closest('[data-az-find]')) { find(); return; }
      const u = e.target.closest('[data-az-unlink]'); if (u) { flush(); send('azUnlink', { id: +u.dataset.azUnlink, text: msg().value }); return; }
      const p = e.target.closest('[data-az-pick]'); if (p) { link(p.dataset.azPick); msg().focus(); return; }
      if (e.target.closest('[data-az-check]')) {
        const v = id => (root.querySelector(id) || { value: '' }).value;
        send('azCheck', { org: v('#az-org'), project: v('#az-proj'), pat: v('#az-pat') });
        const pat = root.querySelector('#az-pat'); if (pat) pat.value = '';
        return;
      }
      if (e.target.closest('[data-az-patpage]')) send('azOpenPatPage', { org: (root.querySelector('#az-org') || { value: '' }).value });
    });
    root.addEventListener('change', e => {
      if (e.target.id === 'az-state') send('azState', { state: e.target.value });
      else if (e.target.dataset && e.target.dataset.azOpt) send('azOption', { key: e.target.dataset.azOpt, on: e.target.checked });
    });
    root.addEventListener('keydown', e => {
      if (e.target.id === 'az-id' && e.key === 'Enter') { e.preventDefault(); find(); return; }
      const az = getState() && getState().azure;
      if (az && az.wizard && e.key === 'Escape') { e.preventDefault(); send('azWizardClose'); return; }
      if (e.target !== msg() || !az || !az.suggest || !az.suggest.length) return;
      const n = az.suggest.length;
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); A.sel = (Math.min(A.sel, n - 1) + (e.key === 'ArrowDown' ? 1 : n - 1)) % n; A.render(root, az); }
      else if (e.key === 'Enter' && !e.ctrlKey && !e.metaKey) { e.preventDefault(); link(az.suggest[Math.min(A.sel, n - 1)].id); }
      else if (e.key === 'Escape') { e.preventDefault(); A.sel = 0; send('azSuggestClose'); }
    }, true);
  };

  /** Ridisegna riga, tendina e procedura (chiamata dal render di Changes). */
  A.render = (root, az) => {
    const row = root.querySelector('#azr'), sug = root.querySelector('#azs'), wiz = document.getElementById('azw');
    if (row) {
      const keep = row.querySelector('#az-id'), v = keep ? keep.value : '', had = keep && document.activeElement === keep;
      if (U.setHTML(row, A.commitHTML(az))) { const n = row.querySelector('#az-id'); if (n) { n.value = v; if (had) n.focus(); } }
    }
    if (sug) { if (!(az && az.suggest && az.suggest.length)) A.sel = 0; U.setHTML(sug, A.suggestHTML(az)); }
    if (wiz) {
      const keep = {}; wiz.querySelectorAll('input[type=text]').forEach(i => { keep[i.id] = i.value; });
      const was = !!wiz.__open, open = !!(az && az.wizard);
      if (U.setHTML(wiz, A.wizardHTML(az)) && was) wiz.querySelectorAll('input[type=text]').forEach(i => { if (keep[i.id] != null) i.value = keep[i.id]; });
      if (open && !was) { const h = wiz.querySelector('#az-org'); if (h) h.focus(); }
      wiz.__open = open;
    }
  };
})(window);
