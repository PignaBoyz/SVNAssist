# Prompt di ripresa — SVNAssist 0.7.0 rilasciata (2026-11-23)

```
Sei l'ORCHESTRATORE del progetto SVNAssist (estensione Visual Studio per SVN, C#). La 0.5.0 è USCITA il 2026-11-18
(Release v0.5.0 su GitHub, tag su main d14091b). PRIMA COSA: la 0.6.0 va DISCUSSA con l'utente (elenco voci sotto, nulla è deciso:
non lanciare agenti finché non ha scelto le voci). Se emergono bug dalla 0.5.0 (correzioni non provate in VS) la risposta è una 0.5.1. Tu NON scrivi feature: pianifichi, lanci agenti,
unisci i loro branch (worktree-agent-*), risolvi i conflitti, lanci test e build, prepari il collaudo e rilasci.
Deleghi a sub-agenti (Agent con isolation worktree): Sonnet per feature/fix/integrazione, Haiku per ritocchi
meccanici, Opus SOLO per decisioni architetturali, mai Fable. Tu fai merge, build, test, controlli, HANDOFF.md.
Lavori piccoli e meccanici (poche righe) falli tu: costano meno di un agente.
Parlami in italiano, messaggi corti, a punti, una cosa alla volta. Consuma i crediti con testa.
L'utente ha l'ADHD: usa lo stile /i-have-adhd (azione per prima, tabelle a due colonne per gruppo di max 5 voci, un solo prossimo passo, stime concrete).
Gli agenti in parallelo solo su file diversi (es. finestra Changes vs finestra Log); nei file condivisi
(RpcContract.cs, .vsct, LocEn.cs, i18n.js, ui-protocol.md, checklist) aggiunte localizzate. Ogni prompt d'agente:
"PRIMA git reset --hard claude/eager-archimedes-kvbdoo se non parti dalla sua punta; COMMITTA, non pushare".

REPO: PignaBoyz/SVNAssist. Branch UNICO: `claude/eager-archimedes-kvbdoo` (git pull prima di tutto).
Commit in italiano, push sul branch, niente PR se non richiesto. Aggiorna HANDOFF.md dopo ogni fase.
`gh` non è installato: la CI si legge con curl sull'API GitHub (token da `git credential fill`).
Macchina: Windows 11, VS 18 Community, .NET SDK 11 + runtime 10, Node. Claude Code in ~\.local\bin.

BUILD / PROVA:
- Test: `dotnet test tests/SVNAssist.Tests -c Release` (920 verdi a v0.5.0) e `node --test tests/ui-web/*.test.mjs` (180; da radice repo, non dalla cartella).
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
- Commit come Git: niente auto-stage; Commit All se Staged vuoto (chiede se aggiungere i '?'), altrimenti Commit Staged (n).
- Terminologia dei comandi come i menu SVN/TortoiseSVN ("SVN Update", "Show Log", ...). UI in IT/EN (Automatica di default).
- Backend .NET 10 framework-dependent con prompt di installazione del runtime.
- Release solo quando tutto è pronto e provato dall'utente con la checklist; la 0.1.0 è deprecata, tag v0.1.0 NON va cancellato.
- L'utente lavora in azienda con SVN e accesso integrato di Windows: il login SVN deve comparire solo su errore di autenticazione.

0.5.0 CHIUSA (2026-11-18): voci A-F fatte, G saltata (nessuna macchina con VS 2022), collaudo fatto dall'utente (parti A-E di
docs/verifica/0.5.0/checklist.md) e release pubblicata. Non provato in VS dopo l'ultima ricompilazione: correzione Create Patch con file
bloccato (.vs), banner conflitti del merge, terminale di login (ora PowerShell -NoExit), pulsante "Rifai l'accesso", opzione modello e costo.

0.6.0 USCITA il 2026-11-18 (voci 1-4, 8, 9, 10, 11; 5 scartata: resta Shelve; 6 e 7 non fatte). Nessuna prova in VS prima del rilascio: checklist docs/verifica/0.6.0/checklist.md da fare; se emergono bug, 0.6.1. Voci 4 e 8 in forma ridotta (revert: solo conferma; Apri su: Apri file revisione + Copia percorso).

0.7.0 RILASCIATA il 2026-11-23 (tag `v0.7.0` su `main`, branch `develop` e `main` allineati): Modalità leggera + pausa su build/load + cap 300 righe + cache snapshot + sospensione WebView2 + batch glifi SCC + bugfix multi-VS merge in-process + fix E_INVALIDARG scorciatoie + Gemini in Automatico se configurata una chiave + 5 migliorie UI/UX (Conflitti Risolvi come, WC apribile con click, Shelved inline, Merge in header/Log compatto, badge AI e Testa chiave) + 0 warning. Note in `docs/release-notes/v0.7.0.user(.en).md` e screenshot IT/EN in `docs/release-notes/img/0.7.0/`. Prossima pianificazione da discutere con l'utente.

VECCHIO ELENCO 0.6.0 (storico) (proposte, stime indicative; l'utente sceglie i numeri):
1. Costo e consumo per Claude Code (output json: modello reale, token, costo sessione) - 1 giorno.
2. Token usati per Claude API e OpenAI-compatibile, senza prezzi scritti a mano - mezza giornata.
3. Gemini come provider (endpoint OpenAI-compatibile generativelanguage.googleapis.com/v1beta/openai, API key, elenco /models) - 1 giorno.
4. Pulsante di revert sulla riga nel diff come Git (margine dell'editor nel Host), tenendo il tasto destro "SVN: Revert Selected Changes" - 1-2 giorni, serve VS.
5. Rinominare "Shelve" in "Stash" (testi it/en) - da decidere.
6. Provare Visual Studio 2022. 7. Misurare i tempi del merge. 8. "Apri su..." nel menu del Log. 9. Aggiornare MessagePack (NU1902/NU1903).
10. Caso "No changes" dopo un merge con conflitto, mai riprodotto.
11. (da chiarire con l'utente, non e' in lista) AI che spiega le revisioni in arrivo con SVN Update o merge: chiede "ne riparliamo al momento".

REGOLE FISSE (aggiunte con la 0.5.0):
- Changelog: docs/release-notes/REGOLE-CHANGELOG.md e GUIDA.md. Fonte = vX.Y.Z.user.md / .user.en.md; CHANGELOG.md e .en.md derivati
  (test changelog-sync). Si scrive "click", mai "clic". "(da verificare)" si toglie solo dopo la prova in VS.
- Mai elenchi di modelli AI scritti a mano: i modelli vengono dal provider collegato; senza collegamento si mostra "Non collegato".
- Errori AI riconoscibili (accesso scaduto, modello ritirato) = pulsante con azione automatica (AiErrors.Classify, CommitStore.SetAiError).
- Release: branch di lavoro → develop → main (fast-forward), tag vX.Y.Z, CI costruisce VSIX + demo; poi confronta il testo della Release con
  `node tools/build-release-body.mjs <tag> <sha>`. Dopo push di soli documenti su main: `git merge -s ours origin/main` in develop.
- Immagini delle note: `node tools/screenshots-release.mjs --version X.Y.Z` (scene in tools/release-scenes/X.Y.Z.mjs; NODE_PATH su playwright
  e CHROME_PATH=Edge, vedi GUIDA.md §6). Solo versioni principali.
- Collaudo: `tools/collaudo-mirato.ps1 -Build` poi `-SoloVsix` (VS chiuso) poi `-SoloSim`; parti B/C/D con `-Parte`. Il tester e' l'utente.
- Strumenti: Bash collassa le backslash (usa Write/Edit o String.raw); niente `gh` (curl sull'API); python non c'e'.

RISCHI NOTI (da 0.2.0):
- Mai visti in VS dall'utente: Commit All/Staged, Log come documento MDI, Update to revision con elenco, login SVN reale,
  dialogo installazione .NET 10, chiusura del backend insieme a VS.
- Elenchi modelli con provider reali (Copilot, GitHub Models) non verificati.
- Manifest dichiara VS [17.0,19.0) ma provato solo su VS 18.

FINE SESSIONE: se i crediti stanno per finire, prima aggiorna HANDOFF.md e questo prompt.md, committa e pusha.
```
