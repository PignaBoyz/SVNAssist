'use strict';
/* Vista "Impostazioni" (dentro Tools > Options > SVNAssist). Gestita solo dall'host, senza backend.
   state: {values:{chiave:stringa}, hasAnthropicKey, hasAiKey}. Ogni modifica si applica subito (settingsSet / settingsSecret). */
(function (w) {
  const U = w.SVNUI, esc = U.esc, t = U.t;
  let st = null;
  const CH = () => ({
    aiProvider: [['auto', t('Automatico (consigliato)')], ['copilot', t('GitHub Copilot (abbonamento)')], ['claudecode', t('Claude Code (abbonamento Claude)')], ['claude', t('Claude API (API key Anthropic)')], ['openai', t('OpenAI-compatibile / GitHub Models')], ['gemini', t('Gemini (API key Google)')]],
    defaultLanguage: [['auto', t('Automatica')], ['italiano', t('Italiano')], ['english', t('English')]],
    windowTheme: [['auto', t('Automatico (segue Visual Studio)')], ['light', t('Chiaro')], ['dark', t('Scuro')], ['blue', t('Blu')]],
    uiLanguage: [['auto', t('Automatica (lingua di Visual Studio)')], ['it', t('Italiano')], ['en', t('English')]],
  });
  const row = (inner, label, desc) => `<div class="set-r"><div class="set-l"><b>${esc(label)}</b>${desc ? `<small class="muted">${esc(desc)}</small>` : ''}</div><div class="set-c">${inner}</div></div>`;
  const sel = (k, label, desc) => row(`<select data-k="${k}" aria-label="${esc(label)}">${CH()[k].map(([v, l]) => `<option value="${v}"${st.values[k] === v ? ' selected' : ''}>${esc(l)}</option>`).join('')}</select>`, label, desc);
  const txt = (k, label, desc, ph) => row(`<input type="text" data-k="${k}" value="${esc(st.values[k])}" aria-label="${esc(label)}"${ph ? ` placeholder="${esc(ph)}"` : ''}>`, label, desc);
  const num = (k, label, desc) => row(`<input type="number" min="0" max="1440" data-k="${k}" value="${esc(st.values[k])}" aria-label="${esc(label)}">`, label, desc);
  const chk = (k, label, desc) => row(`<input type="checkbox" data-k="${k}"${st.values[k] === 'true' ? ' checked' : ''} aria-label="${esc(label)}">`, label, desc);
  const key = (k, has, label, desc) => row(`<span class="chip ${has ? 'ok' : 'skip'}">${esc(has ? t('Impostata') : t('Non impostata'))}</span> <input type="password" data-secret="${k}" autocomplete="off" placeholder="${esc(t('Incolla la chiave'))}" aria-label="${esc(label)}"> ${U.btn('btn sm', t('Salva'), 'check', null, null, t('Salva la chiave (cifrata, solo per il tuo utente Windows)'), `data-save="${k}"`)}${has ? ' ' + U.btn('btn sm ghost warn', t('Cancella'), 'x', null, null, t('Cancella la chiave'), `data-clear="${k}"`) : ''}`, label, desc);
  const card = (title, body) => `<section class="set-card"><h3>${esc(title)}</h3>${body}</section>`;

  // Solo i campi del provider scelto: ne si configura uno alla volta, gli altri sarebbero fuorvianti. Senza provider (Automatico) un pulsante porta alla procedura guidata.
  function providerCard() {
    const p = st.values.aiProvider, wiz = U.btn('btn sm', t('Cambia provider AI…'), 'spark', 'settingsWizard', null, t('Procedura guidata: provider, accesso, modello'));
    const claudeModel = txt('claudeModel', t('Modello Claude (solo Claude Code e Claude API)'), t("Un id modello. L'elenco aggiornato è nella procedura guidata (Extensions > SVNAssist > Cambia provider AI)."));
    const f = { copilot: txt('copilotModel', t('Modello Copilot (solo Copilot)'), t("Serve un abbonamento Copilot e 'gh auth login'. 'default' = il modello che sceglie Copilot.")),
      claudecode: claudeModel,
      claude: claudeModel + key('anthropicApiKey', st.hasAnthropicKey, t('API key Anthropic (solo Claude API)'), t("Vuota = variabile d'ambiente ANTHROPIC_API_KEY. Salvata cifrata (DPAPI, solo per il tuo utente Windows).")),
      openai: '', gemini: '' }[p];
    const oa = txt('aiEndpoint', t('URL endpoint (solo OpenAI-compatibile)'), t('Endpoint Chat Completions compatibile OpenAI. Default: GitHub Models.')) +
      key('aiApiKey', st.hasAiKey, t('API key o GitHub PAT (solo OpenAI-compatibile)'), t('Salvata cifrata (DPAPI, solo per il tuo utente Windows).')) +
      txt('aiModel', t('Modello (solo OpenAI-compatibile / GitHub Models)'), t("Un id modello. L'elenco aggiornato è nella procedura guidata."));
    const body = p === 'openai' || p === 'gemini' ? oa : f;
    const none = row(wiz, t('Nessun provider scelto'), t('Con Automatico SVNAssist prova i provider in ordine. Scegline uno per vedere qui modello e chiave.'));
    return card(t('Provider collegato'), body ? body + row(wiz, t('Cambia provider'), t('Procedura guidata: provider, accesso, modello. Chiudi le Options per proseguirla.')) : none);
  }

  function render(root) {
    const el = root.querySelector('#setbody'); if (!st) return;
    const keep = document.activeElement && document.activeElement.dataset && (document.activeElement.dataset.k || document.activeElement.dataset.secret);
    const html =
      card(t('Commit message AI'),
        sel('aiProvider', t('Provider AI per i commit message'), t("Automatico prova Copilot, Claude Code, Claude API e OpenAI-compatibile in quest'ordine.")) +
        sel('defaultLanguage', t('Lingua dei commit message')) +
        chk('aiShowUsage', t('Mostra modello e costo nel messaggio generato'), t('Dopo "Genera con AI" la riga di stato scrive modello, token e costo o crediti AI, solo se il provider li dichiara. Non finisce nel messaggio di commit.')) +
        chk('aiExplainIncoming', t('Spiega le revisioni in arrivo prima di SVN Update'), t('Elenca le revisioni in arrivo e, a richiesta, chiede all\'AI cosa è entrato. All\'AI vanno messaggi di log e modifiche (diff); dei file sensibili solo il nome.'))) +
      providerCard() +
      card(t('Generale'),
        txt('workingCopyPath', t('Working Copy Path'), t("Se impostato ha la precedenza sull'auto-rilevamento dalla solution aperta.")) +
        txt('repoLayout', t('Layout del repository'), t('Nomi delle cartelle del tronco, dei branch e dei tag, separati da / (default trunk/branches/tags). Servono a SVN Switch.')) +
        num('headCheckMinutes', t('Controllo nuove revisioni (minuti)'), t('Ogni quanti minuti SVNAssist chiede al server l\'ultima revisione del ramo (sola lettura). 0 = spento.')) +
        sel('windowTheme', t('Tema delle finestre SVNAssist')) +
        sel('uiLanguage', t("Lingua dell'interfaccia"), t('Si applica subito a finestre e messaggi; la lingua dei commit message AI è un\'impostazione separata.')) +
        chk('showSccGlyphs', t('Mostra lo stato SVN in Solution Explorer'), t('Icone accanto ai file (modificato, aggiunto, non versionato, in conflitto). SVNAssist si registra come provider di source control: spegni se ne usi un altro.'))) +
      card(t('Scorciatoie'),
        txt('nextChangeShortcut', t('Prossima modifica nel diff'), t('Scorciatoia globale, es. Alt+F8 o Ctrl+Shift+Down. Vuota = nessuna scorciatoia.')) +
        txt('previousChangeShortcut', t('Modifica precedente nel diff'), t('Scorciatoia globale, es. Shift+Alt+F8. Vuota = nessuna scorciatoia.')));
    if (U.setHTML(el, html) && keep) { const n = el.querySelector(`[data-k="${keep}"],[data-secret="${keep}"]`); if (n) n.focus(); }
  }

  U.views.settings = {
    mount(root) {
      root.innerHTML = `<div class="app settings"><header class="set-h"><h2>${esc(t('SVNAssist'))}</h2><small class="muted" id="setnote">${esc(t('Le modifiche si applicano subito.'))}</small></header><div id="setbody" class="set-body"></div></div>`;
      root.addEventListener('change', e => {
        const k = e.target.dataset && e.target.dataset.k; if (!k) return;
        U.bridge.send('settingsSet', { key: k, value: e.target.type === 'checkbox' ? String(e.target.checked) : e.target.value });
      });
      root.addEventListener('click', e => {
        const b = e.target.closest('[data-save],[data-clear]'); if (!b) return;
        const k = b.dataset.save || b.dataset.clear, inp = root.querySelector(`[data-secret="${k}"]`);
        U.bridge.send('settingsSecret', { key: k, value: b.dataset.save && inp ? inp.value : '' });
        if (inp) inp.value = '';
      });
    },
    update(root, state) { st = state; render(root); },
    relang(root) { const n = root.querySelector('#setnote'); if (n) n.textContent = t('Le modifiche si applicano subito.'); const h = root.querySelector('#setbody'); if (h) h.__h = null; if (st) render(root); },
  };
})(window);  const CH = () => ({
    aiProvider: [['auto', t('Automatico (consigliato)')], ['copilot', t('GitHub Copilot (abbonamento)')], ['claudecode', t('Claude Code (abbonamento Claude)')], ['claude', t('Claude API (API key Anthropic)')], ['openai', t('OpenAI-compatibile / GitHub Models')], ['gemini', t('Gemini (API key Google)')]],
    defaultLanguage: [['auto', t('Automatica')], ['italiano', t('Italiano')], ['english', t('English')]],
    windowTheme: [['auto', t('Automatico (segue Visual Studio)')], ['light', t('Chiaro')], ['dark', t('Scuro')], ['blue', t('Blu')]],
    uiLanguage: [['auto', t('Automatica (lingua di Visual Studio)')], ['it', t('Italiano')], ['en', t('English')]],
  });
  const row = (inner, label, desc) => `<div class="set-r"><div class="set-l"><b>${esc(label)}</b>${desc ? `<small class="muted">${esc(desc)}</small>` : ''}</div><div class="set-c">${inner}</div></div>`;
  const sel = (k, label, desc) => row(`<select data-k="${k}" aria-label="${esc(label)}">${CH()[k].map(([v, l]) => `<option value="${v}"${st.values[k] === v ? ' selected' : ''}>${esc(l)}</option>`).join('')}</select>`, label, desc);
  const txt = (k, label, desc, ph) => row(`<input type="text" data-k="${k}" value="${esc(st.values[k])}" aria-label="${esc(label)}"${ph ? ` placeholder="${esc(ph)}"` : ''}>`, label, desc);
  const num = (k, label, desc) => row(`<input type="number" min="0" max="1440" data-k="${k}" value="${esc(st.values[k])}" aria-label="${esc(label)}">`, label, desc);
  const chk = (k, label, desc) => row(`<input type="checkbox" data-k="${k}"${st.values[k] === 'true' ? ' checked' : ''} aria-label="${esc(label)}">`, label, desc);
  const key = (k, has, label, desc) => row(`<span class="chip ${has ? 'ok' : 'skip'}">${esc(has ? t('Impostata') : t('Non impostata'))}</span> <input type="password" data-secret="${k}" autocomplete="off" placeholder="${esc(t('Incolla la chiave'))}" aria-label="${esc(label)}"> ${U.btn('btn sm', t('Salva'), 'check', null, null, t('Salva la chiave (cifrata, solo per il tuo utente Windows)'), `data-save="${k}"`)}${has ? ' ' + U.btn('btn sm ghost warn', t('Cancella'), 'x', null, null, t('Cancella la chiave'), `data-clear="${k}"`) : ''}`, label, desc);
  const card = (title, body) => `<section class="set-card"><h3>${esc(title)}</h3>${body}</section>`;

  // Solo i campi del provider scelto: ne si configura uno alla volta, gli altri sarebbero fuorvianti. Senza provider (Automatico) un pulsante porta alla procedura guidata.
  function providerCard() {
    const p = st.values.aiProvider, wiz = U.btn('btn sm', t('Cambia provider AI…'), 'spark', 'settingsWizard', null, t('Procedura guidata: provider, accesso, modello'));
    const claudeModel = txt('claudeModel', t('Modello Claude (solo Claude Code e Claude API)'), t("Un id modello. L'elenco aggiornato è nella procedura guidata (Extensions > SVNAssist > Cambia provider AI)."));
    const f = { copilot: txt('copilotModel', t('Modello Copilot (solo Copilot)'), t("Serve un abbonamento Copilot e 'gh auth login'. 'default' = il modello che sceglie Copilot.")),
      claudecode: claudeModel,
      claude: claudeModel + key('anthropicApiKey', st.hasAnthropicKey, t('API key Anthropic (solo Claude API)'), t("Vuota = variabile d'ambiente ANTHROPIC_API_KEY. Salvata cifrata (DPAPI, solo per il tuo utente Windows).")),
      openai: '', gemini: '' }[p];
    const oa = txt('aiEndpoint', t('URL endpoint (solo OpenAI-compatibile)'), t('Endpoint Chat Completions compatibile OpenAI. Default: GitHub Models.')) +
      key('aiApiKey', st.hasAiKey, t('API key o GitHub PAT (solo OpenAI-compatibile)'), t('Salvata cifrata (DPAPI, solo per il tuo utente Windows).')) +
      txt('aiModel', t('Modello (solo OpenAI-compatibile / GitHub Models)'), t("Un id modello. L'elenco aggiornato è nella procedura guidata."));
    const body = p === 'openai' || p === 'gemini' ? oa : f;
    const none = row(wiz, t('Nessun provider scelto'), t('Con Automatico SVNAssist prova i provider in ordine. Scegline uno per vedere qui modello e chiave.'));
    return card(t('Provider collegato'), body ? body + row(wiz, t('Cambia provider'), t('Procedura guidata: provider, accesso, modello. Chiudi le Options per proseguirla.')) : none);
  }

  function render(root) {
    const el = root.querySelector('#setbody'); if (!st) return;
    const keep = document.activeElement && document.activeElement.dataset && (document.activeElement.dataset.k || document.activeElement.dataset.secret);
    const html =
      card(t('Commit message AI'),
        sel('aiProvider', t('Provider AI per i commit message'), t("Automatico prova Copilot, Claude Code, Claude API e OpenAI-compatibile in quest'ordine.")) +
        sel('defaultLanguage', t('Lingua dei commit message')) +
        chk('aiShowUsage', t('Mostra modello e costo nel messaggio generato'), t('Dopo "Genera con AI" la riga di stato scrive modello, token e costo o crediti AI, solo se il provider li dichiara. Non finisce nel messaggio di commit.')) +
        chk('aiExplainIncoming', t('Spiega le revisioni in arrivo prima di SVN Update'), t('Elenca le revisioni in arrivo e, a richiesta, chiede all\'AI cosa è entrato. All\'AI vanno messaggi di log e modifiche (diff); dei file sensibili solo il nome.'))) +
      providerCard() +
      card(t('Generale'),
        txt('workingCopyPath', t('Working Copy Path'), t("Se impostato ha la precedenza sull'auto-rilevamento dalla solution aperta.")) +
        txt('repoLayout', t('Layout del repository'), t('Nomi delle cartelle del tronco, dei branch e dei tag, separati da / (default trunk/branches/tags). Servono a SVN Switch.')) +
        num('headCheckMinutes', t('Controllo nuove revisioni (minuti)'), t('Ogni quanti minuti SVNAssist chiede al server l\'ultima revisione del ramo (sola lettura). 0 = spento.')) +
        sel('windowTheme', t('Tema delle finestre SVNAssist')) +
        sel('uiLanguage', t("Lingua dell'interfaccia"), t('Si applica subito a finestre e messaggi; la lingua dei commit message AI è un\'impostazione separata.')) +
        chk('showSccGlyphs', t('Mostra lo stato SVN in Solution Explorer'), t('Icone accanto ai file (modificato, aggiunto, non versionato, in conflitto). SVNAssist si registra come provider di source control: spegni se ne usi un altro.'))) +
      card(t('Scorciatoie'),
        txt('nextChangeShortcut', t('Prossima modifica nel diff'), t('Scorciatoia globale, es. Alt+F8 o Ctrl+Shift+Down. Vuota = nessuna scorciatoia.')) +
        txt('previousChangeShortcut', t('Modifica precedente nel diff'), t('Scorciatoia globale, es. Shift+Alt+F8. Vuota = nessuna scorciatoia.')));
    if (U.setHTML(el, html) && keep) { const n = el.querySelector(`[data-k="${keep}"],[data-secret="${keep}"]`); if (n) n.focus(); }
  }

  U.views.settings = {
    mount(root) {
      root.innerHTML = `<div class="app settings"><header class="set-h"><h2>${esc(t('SVNAssist'))}</h2><small class="muted" id="setnote">${esc(t('Le modifiche si applicano subito.'))}</small></header><div id="setbody" class="set-body"></div></div>`;
      root.addEventListener('change', e => {
        const k = e.target.dataset && e.target.dataset.k; if (!k) return;
        U.bridge.send('settingsSet', { key: k, value: e.target.type === 'checkbox' ? String(e.target.checked) : e.target.value });
      });
      root.addEventListener('click', e => {
        const b = e.target.closest('[data-save],[data-clear]'); if (!b) return;
        const k = b.dataset.save || b.dataset.clear, inp = root.querySelector(`[data-secret="${k}"]`);
        U.bridge.send('settingsSecret', { key: k, value: b.dataset.save && inp ? inp.value : '' });
        if (inp) inp.value = '';
      });
    },
    update(root, state) { st = state; render(root); },
    relang(root) { const n = root.querySelector('#setnote'); if (n) n.textContent = t('Le modifiche si applicano subito.'); const h = root.querySelector('#setbody'); if (h) h.__h = null; if (st) render(root); },
  };
})(window);
