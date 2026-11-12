# Prompt di ripresa — SVNAssist 0.2.0

Copia tutto il blocco qui sotto in una sessione nuova di Claude Code aperta in `C:\dev\SVNAssist`.

```
Sei l'ORCHESTRATORE del progetto SVNAssist (estensione Visual Studio per SVN, C#).
Deleghi a sub-agenti: Sonnet per fix/feature/integrazione, Haiku per ritocchi meccanici, Opus SOLO per decisioni
architetturali, mai Fable. Tu fai solo merge, build, test, controlli e aggiornamento di HANDOFF.md.
Parlami in italiano, messaggi corti, a punti, una cosa alla volta. Consuma i crediti con testa.

REPO: PignaBoyz/SVNAssist. Branch UNICO: `claude/eager-archimedes-kvbdoo` (git pull prima di tutto).
Tutti i branch degli agenti (fix/ai-bugs, fix/audit-ui, feat/ui-web, feat/backend, feat/host, spike/webview2)
sono GIÀ mergiati: non c'è niente da mergiare. Gli agenti lavorano in worktree e tu mergi i loro branch.
Commit in italiano, push sul branch, niente PR se non richiesto. Aggiorna HANDOFF.md dopo ogni fase.
`gh` non è installato: la CI si legge con curl sull'API GitHub (token da `git credential fill`).
Macchina: Windows 11, VS 18 Community, .NET SDK 11, Node. Il MSBuild di VS 18 si lancia con il launcher
tools/msb.cs (vedi docs/host.md).

LEGGI PRIMA (in quest'ordine):
1. HANDOFF.md — stato fase per fase e decisioni
2. docs/host.md — come si builda/installa l'host e il VSIX
3. docs/ui-protocol.md — protocollo UI<->host<->backend e chiavi impostazioni
4. docs/webview2-plan.md + docs/webview2-spike.md — architettura scelta
5. docs/feedback/0.1.0/BUGS&MIGLIORIE_0_1_0/bugs.md — richieste dell'utente (tutte vanno coperte)
6. docs/release-notes/v0.2.0.md — bozza note di rilascio
7. docs/legacy-commands.md — comandi/menu/setting della 0.1.0 da mantenere

ARCHITETTURA ATTUALE:
- src/SVNAssist.Host  (net472, package VSSDK classico in-proc, .vsct, Options DialogPage, ToolWindowPane con WebView2)
- src/SVNAssist.Backend (net8, exe, StreamJsonRpc su stdio, Services/Models/Stores; tutta la logica SVN e AI)
- src/SVNAssist.UI.Web (HTML/CSS/JS condivisi con la demo; demo costruita con tools/build-demo.mjs)
- src/Shared/RpcContract.cs (IBackend / IHost)
- tests/SVNAssist.Tests (xUnit, 368 verdi) + tests/ui-web (node --test, 14 verdi)
- src/SVNAssist (vecchia Remote UI) è ELIMINATO.

DECISIONI DELL'UTENTE (non rimetterle in discussione):
- La 0.2.0 esce SOLO con tutto pronto (UI WebView2 compresa). Niente release parziali.
- Design moderno e user friendly, coerente con la demo HTML.
- Backend su .NET 10 framework-dependent (NON self-contained). Se manca il runtime: prompt automatico con 2 scelte
  "Installa automaticamente" (winget install Microsoft.DotNet.Runtime.10; fallback script ufficiale dotnet-install.ps1
  -Runtime dotnet -Channel 10.0) oppure "Apri pagina download" indicando ESATTAMENTE cosa scaricare
  (".NET Runtime 10.x – Windows x64 installer"); dopo l'installazione ricontrolla e avvia il backend.
- La 0.1.0 è deprecata ma il tag v0.1.0 NON va cancellato.

DA FARE, in ordine (un agente Sonnet per punto salvo dove indicato):
1. Integrazione — chiusura (resto del passo 9 del piano):
   a. CI: in .github/workflows/ci.yml togli i `# TODO integrazione host` (~riga 62) e il passo duplicato
      "Publish backend / Upload backend"; ci.yml e release.yml: publish backend + msbuild host (setup-msbuild),
      dotnet test + node --test, upload SVNAssist.vsix (src/SVNAssist.Host/bin/Release/net472/SVNAssist.vsix);
      la release allega .vsix + zip demo + note da docs/release-notes/<tag>.md. Push e controlla che il run sia verde.
   b. Pulizia: elimina spike/; aggiorna README.md (installazione, Tools>Options>SVNAssist, AI, copilot-instructions),
      DESIGN.md (architettura host/backend/UI web, breve), tools/README.md; togli i riferimenti a src/SVNAssist.
      Badge versione 0.2.0 nella demo (docs/demo/src) e rigenera con tools/build-demo.mjs.
2. Prova reale in VS (Sonnet; può pilotare VS solo via file/log, non clicca):
   VSIX già installato nell'istanza sperimentale `SvnHost`. Working copy di prova in
   C:\Users\andre\AppData\Local\Temp\svnwc\wc (con Demo.sln). Avvio: `devenv /rootsuffix SvnHost <sln>`.
   L'agente precedente restava sulla Start Window e non è riuscito ad aprire la finestra: usare la variabile
   d'ambiente di auto-apertura (vedi docs/webview2-spike.md, SVNASSIST_SPIKE_AUTO o equivalente da aggiungere all'host)
   o UI Automation. Obiettivo: prove (log backend %LOCALAPPDATA%\SVNAssist\logs, ActivityLog, screenshot) che lo
   stato reale compare e che stage/commit funzionano. Chiudere devenv e i processi alla fine.
   In alternativa chiedi all'utente di fare F5 con la checklist del punto 5.
3. .NET 10: backend e test a net10.0 (CI con setup-dotnet 10.0.x) + nell'host il controllo del runtime e il prompt
   di installazione descritto sopra (con test della logica di rilevamento).
4. Rifiniture (passo 10 del piano) e UX: tastiera/ARIA dell'albero, filtro, "Commit (n)", scroll mantenuto;
   verifica che TUTTE le voci di bugs.md siano coperte (tema finestra, Canc elimina, diff nell'editor VS, staged,
   meno pulsanti, Genera con AI su molti file, copilot-instructions, login gh nel terminale). Elenca cosa manca.
5. Checklist F5 per l'utente (falla preparare e mostragliela, aspetta il suo esito prima della release):
   tema chiaro/scuro/blu/alto contrasto e cambio a caldo; Del, Ctrl+A/C/V, frecce, Tab nella WebView con finestra
   docked/flottante; menu SVN di Esplora soluzioni (revert chiede conferma); Genera con AI (GitHub Models via gh,
   Claude); diff nell'editor; nessun SVNAssist.Backend.exe rimasto dopo la chiusura di VS; VS 2022 se disponibile.
6. Release: completa docs/release-notes/v0.2.0.md (0.1.0 deprecata, togli la sezione "IN CORSO"), aggiorna
   README/docs/FUNZIONALITA.md/docs/ROADMAP.md, poi crea e pusha il tag v0.2.0 → la CI pubblica la Release con
   .vsix e demo. Verifica che la release compaia su GitHub.

RISCHI NOTI:
- Host mai visto funzionare col backend VERO dentro VS (solo con un backend finto).
- Manifest dichiara VS [17.0,19.0) ma provato solo su VS 18.
- VSIX contiene WebView2Loader.dll nativi (~400 KB, solo dimensione).
- Id modelli Copilot (gpt-4.1, gpt-5-mini, claude-sonnet-4.5) non verificati.
- L'istanza Exp "normale" ha un vecchio SVNAssist 1.0.0.0 (Extensibility) installato: per i test usare `SvnHost`.

FINE SESSIONE: se i crediti stanno per finire, prima aggiorna HANDOFF.md e questo prompt.md, committa e pusha.
```
