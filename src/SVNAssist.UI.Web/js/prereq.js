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

  /* Procedura guidata (AI a 4 passi, o prerequisiti mancanti). wizardHTML(w) e' puro: stato e controlli stanno nel backend.
     w: {open, mode:'ai'|'prereq', step, provider, busy, feedback, providers[], items[], auth, authOk, authDetail, endpoint, models[], model, fromGenerate, canNext} */
  const STEPS = ['Provider', 'Prerequisiti', 'Accesso', 'Modello'];
  const row = (ok, title, detail, extra) => `<div class="pi ${ok ? 'ok' : 'ko'}"><span class="pm">${U.icon(ok ? 'check' : 'x')}<span class="sr">${ok ? 'A posto' : 'Da sistemare'}</span></span><div class="pt"><b>${U.esc(title)}</b><div class="muted small">${U.esc(detail || '')}</div>${extra || ''}</div></div>`;
  const items = (list, w) => list.map(it => row(it.ok, it.title, it.detail,
    (!it.ok && it.instructions ? `<div class="muted small">${U.esc(it.instructions).replace(/\n/g, '<br>')}</div>` : '') +
    (!it.ok && (it.primary || it.secondary) ? `<div class="pb">${it.primary ? U.btn('btn sm', it.primary, null, 'wizardRun', { id: it.id, primary: true }, it.primary, w.busy ? 'disabled' : '') : ''}${it.secondary ? U.btn('btn sm', it.secondary, null, 'wizardRun', { id: it.id, primary: false }, it.secondary, w.busy ? 'disabled' : '') : ''}</div>` : ''))).join('');
  const live = w => `<div class="small fb" role="status" aria-live="polite">${w.busy ? '<span class="spin"></span> ' : ''}${U.esc(w.feedback || '')}</div>`;
  const recheck = w => `<div class="pb">${U.btn('btn sm', 'Ricontrolla', 'refresh', 'wizardRecheck', null, 'Rilegge lo stato adesso (avviene anche da solo)', w.busy ? 'disabled' : '')}<span class="muted small">Ricontrollo automatico quando torni qui dal terminale o dal browser.</span></div>`;
  const stepBody = {
    1: w => `<p class="muted">Scegli come far scrivere i commit message all'AI. Puoi cambiarlo quando vuoi da Extensions &gt; SVNAssist &gt; Cambia provider AI.</p><div role="radiogroup" aria-label="Provider AI" class="wiz-cards">` +
      w.providers.map(p => `<button type="button" role="radio" aria-checked="${w.provider === p.id}" class="wiz-card${w.provider === p.id ? ' sel' : ''}" ${U.cmd('wizardPick', { provider: p.id })}><b>${U.esc(p.title)}</b><span class="muted small">${U.esc(p.cost)}</span></button>`).join('') + `</div>`,
    2: w => `<p class="muted">Cosa serve installare sul computer per questo provider.</p>` +
      (w.items.length ? items(w.items, w) + recheck(w) : row(true, 'Nessun programma da installare', 'Per questo provider basta il passo successivo.')) + live(w),
    3: w => {
      const k = w.auth;
      if (k === 'github') return `<p class="muted">Accedi a GitHub: si apre un terminale con <code>gh auth login --web</code>, conferma il codice nel browser.</p>` + row(w.authOk, 'Accesso a GitHub', w.authDetail || (w.authOk ? 'login effettuato' : 'non hai ancora fatto login'), !w.authOk ? `<div class="pb">${U.btn('btn sm primary', 'Accedi a GitHub', null, 'wizardLogin', null, 'Apre il terminale per il login', w.busy ? 'disabled' : '')}</div>` : '') + recheck(w) + live(w);
      if (k === 'claudecode') return `<p class="muted">Claude Code usa il login del tuo abbonamento Claude. Se non l'hai mai fatto, apri Claude Code e digita <code>/login</code>.</p>` + row(true, 'Accesso a Claude Code', w.authDetail, `<div class="pb">${U.btn('btn sm', 'Apri Claude Code per accedere', null, 'wizardLogin', null, 'Apre il terminale con Claude Code')}</div>`) + live(w);
      const key = `<label for="wiz-key">API key</label><input id="wiz-key" type="password" autocomplete="off" spellcheck="false" placeholder="${w.authOk ? 'Già impostata: incolla per sostituirla' : 'Incolla qui la chiave'}">`;
      const ep = k === 'openai' ? `<label for="wiz-ep">Endpoint</label><input id="wiz-ep" type="text" value="${U.esc(w.endpoint)}" spellcheck="false" autocomplete="off">` : '';
      return `<p class="muted">${k === 'key' ? 'Incolla la API key Anthropic (console.anthropic.com &gt; API keys). Uso a pagamento, a consumo.' : 'Con GitHub Models basta il login GitHub o un token; per altri endpoint (OpenAI, Azure, Ollama) inserisci endpoint e key.'}</p>` +
        row(w.authOk, k === 'key' ? 'API key Anthropic' : 'Chiave o token', w.authDetail) +
        `<form id="wiz-form" class="wiz-form">${ep}${key}<div class="pb"><button type="submit" class="btn sm" title="Salva (cifrata) nelle impostazioni di Visual Studio"><span>Salva</span></button>` +
        (k === 'openai' ? U.btn('btn sm', 'Accedi a GitHub', null, 'wizardLogin', null, 'Apre il terminale per il login GitHub (gratis con GitHub Models)') : '') + `</div></form><div class="muted small">La chiave è salvata cifrata (DPAPI) nelle impostazioni di Visual Studio e non viene mai mostrata.</div>` + live(w);
    },
    4: w => `<p class="muted">Scegli il modello: quello consigliato va bene per quasi tutti.</p><label for="wiz-model">Modello</label><select id="wiz-model">` +
      w.models.map(m => `<option value="${U.esc(m.value)}"${m.value === w.model ? ' selected' : ''}>${U.esc(m.label)}</option>`).join('') + `</select>` +
      `<div class="muted small">${w.fromGenerate ? 'Con Fine salvo le impostazioni e genero subito il messaggio.' : 'Con Fine salvo provider e modello (le stesse impostazioni di Tools > Options).'}</div>`,
  };
  const prqBody = w => `<p class="muted">${w.items.some(i => !i.ok) ? 'Per usare SVNAssist manca qualcosa. Sistemalo con i pulsanti qui sotto, oppure con <b>Faccio io</b> se preferisci farlo a mano.' : 'Per usare SVNAssist è tutto a posto.'}</p>` + items(w.items, w) + recheck(w) + live(w);
  U.wizardHTML = w => {
    if (!w || !w.open) return '';
    const prq = w.mode === 'prereq', last = w.step === 4;
    const title = prq ? 'Prerequisiti' : 'Configura l\'AI per i commit message';
    const steps = prq ? '' : `<ol class="wiz-steps" aria-label="Passi">${STEPS.map((n, i) => `<li class="${i + 1 === w.step ? 'cur' : i + 1 < w.step ? 'done' : ''}"${i + 1 === w.step ? ' aria-current="step"' : ''}><span class="n">${i + 1 < w.step ? U.icon('check') : i + 1}</span>${n}</li>`).join('')}</ol>`;
    const why = w.canNext ? '' : w.step === 1 ? 'Scegli un provider' : w.step === 2 ? 'Installa ciò che manca, poi ricontrolla' : 'Completa l\'accesso';
    const next = prq ? U.btn('btn primary', 'Chiudi', null, 'wizardCancel', null, 'Chiude la procedura', w.canNext ? '' : 'disabled')
      : last ? U.btn('btn primary', 'Fine', 'check', null, null, 'Salva e chiudi', 'data-wiz="finish"' + (w.busy ? ' disabled' : ''))
        : U.btn('btn primary', 'Avanti', null, 'wizardNext', null, why || 'Passo successivo', w.canNext ? '' : 'disabled');
    return `<div class="wiz-scrim"><div class="wiz" role="dialog" aria-modal="true" aria-labelledby="wiz-t"><div class="wiz-h"><h3 id="wiz-t" tabindex="-1">${U.esc(title)}</h3><span class="fill"></span>${U.btn('ib', '', 'x', 'wizardCancel', null, 'Annulla (Esc)')}</div>${steps}` +
      `<div class="wiz-b">${prq ? prqBody(w) : stepBody[w.step](w)}</div>` +
      `<div class="wiz-f">${U.btn('btn ghost', 'Annulla', null, 'wizardCancel', null, 'Chiude senza salvare')}${U.btn('btn ghost', 'Faccio io', null, 'openOptions', null, 'Chiude e apre Tools > Options > SVNAssist per configurare a mano')}<span class="fill"></span>` +
      `${!prq && w.step > 1 ? U.btn('btn', 'Indietro', null, 'wizardBack', null, 'Passo precedente') : ''}${next}</div></div></div>`;
  };
})(window);
