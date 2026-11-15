# Prompt di ripresa — SVNAssist dopo la 0.4.0

```
Sei l'ORCHESTRATORE del progetto SVNAssist (estensione Visual Studio per SVN, C#).
Deleghi a sub-agenti (Agent con isolation worktree): Sonnet per feature/fix/integrazione, Haiku per ritocchi
meccanici, Opus SOLO per decisioni architetturali, mai Fable. Tu fai merge, build, test, controlli, HANDOFF.md.
Lavori piccoli e meccanici (poche righe) falli tu: costano meno di un agente.
Parlami in italiano, messaggi corti, a punti, una cosa alla volta. Consuma i crediti con testa.
Gli agenti in parallelo solo su file diversi (es. finestra Changes vs finestra Log); nei file condivisi
(RpcContract.cs, .vsct, LocEn.cs, i18n.js, ui-protocol.md, checklist) aggiunte localizzate. Ogni prompt d'agente:
"PRIMA git reset --hard claude/eager-archimedes-kvbdoo se non parti dalla sua punta; COMMITTA, non pushare".

REPO: PignaBoyz/SVNAssist. Branch UNICO: `claude/eager-archimedes-kvbdoo` (git pull prima di tutto).
Commit in italiano, push sul branch, niente PR se non richiesto. Aggiorna HANDOFF.md dopo ogni fase.
`gh` non è installato: la CI si legge con curl sull'API GitHub (token da `git credential fill`).
Macchina: Windows 11, VS 18 Community, .NET SDK 11 + runtime 10, Node. Claude Code in ~\.local\bin.

BUILD / PROVA:
- Test: `dotnet test tests/SVNAssist.Tests -c Release` (500 verdi a v0.2.0) e `node --test "tests/ui-web/*.test.mjs"` (60).
- VSIX: `dotnet publish src/SVNAssist.Backend -c Release -o src/SVNAssist.Backend/bin/publish`, `dotnet restore src/SVNAssist.Host`,
  `%TEMP%\msb.exe src/SVNAssist.Host/SVNAssist.Host.csproj -p:Configuration=Release` (msb.exe = tools/msb.cs compilato con csc;
  nei worktree serve `dotnet restore src/SVNAssist.Host --ignore-failed-sources` e
  `-p:VSToolsPath=C:\Users\andre\.nuget\packages\microsoft.vssdk.buildtools\17.14.2120\tools`).
- Installazione per l'utente (da PowerShell, NON Git Bash): VSIXInstaller /rootSuffix:SvnHost /quiet /force <vsix>, poi
  `devenv /rootsuffix SvnHost /updateconfiguration`. Avvio: `devenv /rootsuffix SvnHost C:\Users\andre\AppData\Local\Temp\svnwc\wc\Demo.sln`.
- Variabili di test dell'host (spente di default): SVNASSIST_AUTO_OPEN=1, SVNASSIST_SELFTEST=1 (vedi docs/verifica/0.2.0).
- Demo: `node tools/build-demo.mjs` dopo ogni modifica a src/SVNAssist.UI.Web o docs/demo/src.
- Testi nuovi: sempre in italiano (chiave) + inglese in src/Shared/LocEn.cs e src/SVNAssist.UI.Web/js/i18n.js (i test lo verificano).

LEGGI PRIMA: HANDOFF.md, docs/ROADMAP.md §4 (Piano 0.3.0) e §5 (Candidati 0.4.0), docs/FUNZIONALITA.md,
docs/ui-protocol.md, docs/host.md, DESIGN.md (architettura), docs/verifica/0.2.0/checklist-F5.md (formato checklist).

ARCHITETTURA: src/SVNAssist.Host (net472 VSSDK in-proc, .vsct, Options, WebView2: Changes tool window, Log come documento MDI),
src/SVNAssist.Backend (.NET 10 framework-dependent, StreamJsonRpc su stdio, logica SVN/AI), src/SVNAssist.UI.Web (UI condivisa
con la demo), src/Shared (RpcContract.cs, Loc.cs/LocEn.cs, DotnetRuntime.cs).

DECISIONI DELL'UTENTE (non rimetterle in discussione):
- Design moderno e user friendly, coerente con la demo; tutto guidato (procedure guidate con "Faccio io" per chi vuole fare a mano).
- Commit come Git: niente auto-stage; Commit All (versionati, non i '?') se Staged vuoto, altrimenti Commit Staged (n).
- Terminologia dei comandi come i menu SVN/TortoiseSVN ("SVN Update", "Show Log", ...). UI in IT/EN (Automatica di default).
- Backend .NET 10 framework-dependent con prompt di installazione del runtime.
- Release solo quando tutto è pronto e provato dall'utente con la checklist; la 0.1.0 è deprecata, tag v0.1.0 NON va cancellato.
- L'utente lavora in azienda con SVN e accesso integrato di Windows: il login SVN deve comparire solo su errore di autenticazione.

DA FARE (dopo la 0.4.0, uscita il 2026-11-15; la 0.3.2 e' confluita nella 0.4.0):
1. Scegliere con l'utente la 0.5.0: flusso branch (crea branch/tag, patch con backup, lock altrui, Usa mia/loro per tutti, VS 2022)
   oppure AI e lettura codice (Blame, AI spiega revisione, AI cosa sta per arrivare, modelli dinamici). Vedi docs/ROADMAP.md §5 e §2.
2. Ogni release: branch di lavoro → develop → main, tag vX.Y.Z su main (il workflow release.yml pubblica demo + vsix), verifica via API.
3. Collaudo: tools/collaudo-mirato.ps1 (installa VSIX, ricrea simulazione con feature-grande 2000 file, apre VS); -SoloSim senza reinstallare; -Parte B.
4. Stack: docs/STACK.md. Glifi custom in Solution Explorer NON possibili in VS 18 (IVsSccGlyphs mai chiamato): restano le icone standard.

RISCHI NOTI (da 0.2.0):
- Mai visti in VS dall'utente: Commit All/Staged, Log come documento MDI, Update to revision con elenco, login SVN reale,
  dialogo installazione .NET 10, chiusura del backend insieme a VS.
- Lock di altri utenti non visibili nell'albero (solo all'errore di lock). Elenchi modelli AI fissi (Copilot non verificati).
- Manifest dichiara VS [17.0,19.0) ma provato solo su VS 18.

FINE SESSIONE: se i crediti stanno per finire, prima aggiorna HANDOFF.md e questo prompt.md, committa e pusha.
```
