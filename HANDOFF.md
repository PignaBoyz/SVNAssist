# HANDOFF — SVNAssist 0.2.0

## Prompt per nuova sessione locale (Windows)

```
Sei l'orchestratore del progetto SVNAssist (estensione Visual Studio, C#, VisualStudio.Extensibility, out-of-proc + Remote UI WPF).
Delega il lavoro a sub-agenti scegliendo modello ed effort: Sonnet per audit/fix/feature, Haiku per ritocchi meccanici, Opus SOLO per decisioni architetturali. Mai Fable.
Comunicami in italiano, messaggi corti, a punti, una cosa alla volta.

Repo: pignaboyz/svnassist. Lavora sul branch `claude/eager-archimedes-kvbdoo` (git pull prima di iniziare).
Leggi prima: HANDOFF.md, docs/audit/AUDIT-0.1.0.md, docs/release-notes/v0.2.0.md, DESIGN.md, docs/demo/svnassist-demo.html.
Bug e migliorie dell'utente: C:\Users\andre\Downloads\BUGS&MIGLIORIE_0_1_0 — copiali in docs/feedback/0.1.0/ e committali.

GIÀ FATTO (sul branch, NON ancora compilato né testato — qui sei su Windows: compila e testa per primo):
1. Audit completo: docs/audit/AUDIT-0.1.0.md (Top 10 priorità in cima).
2. Modelli AI: default Claude ora claude-sonnet-5-5 (era Opus); modelli Claude/Copilot/OpenAI-compat sono tendine (Setting.Enum); prerequisiti filtrati per provider + nota login/chiave/costo (AiProviderGuide); Copilot "usabile" solo con token GitHub; prompt "nessuna AI" apre la pagina del provider scelto; GitHub Models usa openai/gpt-4o-mini (NormalizeOpenAiModel).
   Da verificare: valori enum con "/" o "." accettati da EnumSettingEntry; comportamento di vecchi valori salvati fuori lista; id modelli Copilot (gpt-4.1, gpt-5-mini, claude-sonnet-4.5) sono ipotesi; la factory prova ancora Copilot esplicito senza token (incoerente col checklist); niente "modello personalizzato" (Ollama/Azure persi).
3. CI: .github/workflows/ci.yml (build+test su windows-latest, artifact .vsix) e release.yml (su tag v*: build, test, GitHub Release con .vsix + zip della demo + note da docs/release-notes/<tag>.md). Versione unica 0.2.0 in Directory.Build.props. releases/*.zip tolto da git.
   Da verificare: percorso dell'output .vsix (src/SVNAssist/bin/Release/**/*.vsix), install choco sliksvn, esistenza di softprops/action-gh-release@v2.2.2. Controlla il primo run CI su GitHub.

DA FARE, in ordine:
A. Build + test locali; sistemare errori di compilazione dei punti 2-3.
B. Fix dei bug in docs/feedback/0.1.0/ (un agente Sonnet per gruppo di bug).
C. Fix dall'audit non ancora fatti: tema scuro (Log window con colori fissi, Changes window senza Foreground → nero su nero); "Seleziona tutto" spunta anche unversioned (bin/obj); flag Can* nel ChangeRowViewModel senza notifica (righe 86-106); "Working Copy Path" ignorato; albero è una ListBox piatta senza tastiera.
D. UI in WebView2, IDENTICA alla demo HTML (stesso HTML/CSS/JS per estensione e demo; la demo usa un bridge finto con dati mock). Vincolo: WebView2 non si può ospitare in Remote UI out-of-proc → serve un host in-proc (ToolWindowPane VSSDK) con backend net8 esistente, oppure riscrittura in-proc. Prima uno spike/piano con Opus (effort alto) — se esiste docs/webview2-plan.md usalo. Tema: variabili CSS dai colori VS (chiaro/scuro/blu). Albero vero, leggibile.
E. UX: qualsiasi altra miglioria per l'esperienza utente → delegare.
F. Release: completa docs/release-notes/v0.2.0.md (scrivi che la 0.1.0 è deprecata, NON cancellare il tag v0.1.0), aggiorna README/FUNZIONALITA/ROADMAP, poi crea e pusha il tag v0.2.0 → la CI pubblica la Release con .vsix e demo. Verifica che la release compaia su GitHub.

Regole: commit in italiano, push sul branch indicato, niente PR se non richiesto. Aggiorna HANDOFF.md dopo ogni fase.
```

## Stato
- [x] Audit
- [x] Tendina modelli + default Sonnet + prerequisiti per provider (non compilato)
- [x] CI + workflow release (non ancora eseguito con successo)
- [x] Piano WebView2 → docs/webview2-plan.md (scelta ibrido H; spike passo 0 su Windows è il primo gate)
- [x] Fase A (2026-11-11, Windows): build Release ok, 285 test verdi, .vsix in src/SVNAssist/bin/Release/net8.0-windows8.0/. CI falliva per test debouncer instabile → corretto. softprops/action-gh-release@v2.2.2 esiste. Feedback utente in docs/feedback/0.1.0/.
- [x] Fase B (bug AI) e C (audit/tema XAML) mergiate: 318 test verdi. Da provare con F5: tema, popup login, Genera con GitHub Models.
- [x] D1-3 UI web condivisa: src/SVNAssist.UI.Web + docs/demo/src + tools/build-demo.mjs + docs/ui-protocol.md + tests/ui-web (node --test, anche in CI). Screenshot in docs/demo/screenshots. Ritocco: badge demo dice ancora 0.1.0.
- [ ] Fase D0 spike WebView2 (branch locale spike/webview2) in corso
- DECISIONE UTENTE: la 0.2.0 esce SOLO con tutte le feature, UI WebView2 compresa (no release parziale). Note: docs/release-notes/v0.2.0.md (bozza)
- [ ] UI WebView2 completa, UX, release v0.2.0
