# SVNAssist — Roadmap e piano funzionalità

> Data analisi: 2026-11-10 — branch `develop`, ultimo commit `1648785`.
> Stato verificato leggendo il codice (non solo `DESIGN.md`/`README.md`) e le XML-doc dell'SDK
> `Microsoft.VisualStudio.Extensibility` 17.14 nella cache NuGet. I test **non sono stati rieseguiti** in questa analisi.
> Convenzioni: `file:riga` = riferimento nel codice; ore = sviluppo + test automatici, senza collaudo manuale in VS;
> priorità P0 = sicurezza/blocca l'uso, P1 = valore alto, P2 = nice to have.
> Le affermazioni marcate **(da verificare)** non sono provate né nel codice né nell'SDK locale.

---

## 1. Stato attuale

Percorsi relativi a `src/SVNAssist/`. "Test" = esiste almeno un test automatico che esercita la funzione
(47 metodi `[Fact]/[Theory]` in `tests/SVNAssist.Tests`; nessun test su ViewModel, comandi, XAML, watcher).

### 1.1 SVN

| Feature | Dove nel codice | Test | Note / limiti |
|---|---|---|---|
| Status (`svn status --xml`) | `Services/SvnService.cs:49` | Sì (mock + integrazione `file://`) | Niente `-u` (nessun incoming); `ignored/external/obstructed/incomplete` e tree-conflict non mappati (`ParseStatusKind`, solo attributo `item`, `SvnService.cs:328`) |
| Commit file selezionati | `Services/SvnService.cs:76`; UI `ToolWindows/SvnStatusToolWindowData.cs:370` | Sì (args + integrazione) | Mostra "n file", non la revisione creata; messaggio come singolo argomento (`ArgumentList`) |
| Add (`svn add --force`) | `SvnService.cs:90`; UI `SvnStatusToolWindowData.cs:594` | Sì (solo args) | Da context menu su file `?` |
| Delete | `SvnService.cs:102`; UI `SvnStatusToolWindowData.cs:621` | **No** | Nessuna conferma; niente `--force` (file modificato in locale: `svn delete` rifiuta) **(da verificare)** |
| Revert (`--depth infinity`) | `SvnService.cs:114`; UI `SvnStatusToolWindowData.cs:648` | Sì (solo args) | **Nessuna conferma** (D1) |
| Resolve conflitto | `SvnService.cs:126`; UI `SvnStatusToolWindowData.cs:675` | **No** | Solo `--accept working`: marca risolto il file com'è, anche con i marcatori `<<<<<<<` |
| Add to ignore (`svn:ignore`) | `SvnService.cs:135`; UI `SvnStatusToolWindowData.cs:703` | Sì (mock: lista vuota, propget fallito) | Merge con voci esistenti; valore multilinea come singolo argomento (mai provato su svn reale) |
| Update intera WC | `SvnService.cs:182`; UI `SvnStatusToolWindowData.cs:411`; menu `Commands/UpdateCommand.cs` | Sì (args + integrazione) | Output di svn scartato (revisione/conflitti); `UpdateCommand.cs:52` usa `new SvnService()` e la dir della solution, non la root WC; niente update di cartella/revisione |
| Log / History | `Services/SvnService.cs:188`; `ToolWindows/SvnLogToolWindow*.cs` | Sì (args + integrazione) | Ultime N (50), senza `-v`, paginazione o filtri; path WC da inserire a mano (nessun auto-detect, `SvnLogToolWindowData.cs:24`) |
| Diff di revisione (`svn diff -c`) | `SvnService.cs:212`; UI `SvnLogToolWindowData.cs:140` | No | Testo unificato nel pannello inferiore |
| Diff file locale: TortoiseSVN o inline | `SvnService.cs:233` (`TortoiseProc /command:diff`), fallback `:226`; UI `SvnStatusToolWindowData.cs:526` | No | Senza TortoiseSVN testo unificato inline; il diff di un file `?` è vuoto |
| Nome branch (`svn info --show-item relative-url`) | `SvnService.cs:262` | No | Ritorna `"unknown"` su qualsiasi errore |
| Auto-detect WC dalla solution | `ToolWindows/SvnStatusToolWindow.cs:109`; `SvnService.FindSvnRoot` (`SvnService.cs:287`) | No | Risale max 10 livelli cercando `.svn`; usa `Workspaces().QuerySolutionAsync` |
| Setting "Working Copy Path" | `Services/SettingsService.cs`; letto in `SvnStatusToolWindowData.cs:290` | No | Ignorato da `UpdateCommand` e dalla Log window |
| Ricerca `svn.exe` / `TortoiseProc.exe` | `Services/SvnLocator.cs` | No | PATH + cartelle note (TortoiseSVN, SlikSvn, Subversion, CollabNet) |

### 1.2 UI, comandi e infrastruttura

| Feature | Dove nel codice | Test | Note / limiti |
|---|---|---|---|
| Menu Extensions > SVNAssist | `Commands/SvnAssistMenu.cs` (SVN Changes, Commit..., Update, Log/History) | No | `CommitCommand` è identico a `StatusCommand`. Nessun `Placements`: i comandi non sono in altri menu |
| Tool window "SVN Changes" | `ToolWindows/SvnStatusToolWindow.cs`, `...Data.cs` (910 righe), `...Control.xaml` | No | Lista piatta con checkbox, commit inline, select all/none, pannello diff, context menu (Add/Ignore/Delete/Revert/Resolve). `Placement = Floating` (`SvnStatusToolWindow.cs:77`) |
| Tool window "SVN Log" | `ToolWindows/SvnLogToolWindow*.cs` | No | `DocumentWell`; lista + diff; testo di stato con emoji |
| Prompt "svn.exe mancante" | `SvnSetupAssistant.cs`; `Services/SvnEnvironment.cs` (winget `Slik.Subversion`, fallback pagina download) | No | Una volta per sessione (`Interlocked`); chiamato da `UpdateCommand` e `SvnStatusToolWindow.GetContentAsync` |
| File watcher + refresh | `SvnStatusToolWindow.cs:220` (`FileSystemWatcher` ricorsivo), `:178` (timer 1 s, debounce 800 ms), `:159` (timer 5 s sulla solution) | No | Esclude solo `.svn`; ogni refresh = 2 processi (`status` + `info`) |
| Impostazioni (Tools > Options > SVNAssist) | `Services/SettingsService.cs` (9 setting) | No (serve observer VS) | API key in chiaro nello store VS (DPAPI non fatto, commento nel codice) |
| Esecuzione processi | `Services/ProcessRunner.cs`, `IProcessRunner.cs` | Indiretto (runner finto) | UTF-8, stdout/stderr in parallelo, kill su cancel; **nessun timeout, nessun `--non-interactive`** |
| CommitDialog modale | `Dialogs/CommitDialog*.cs/.xaml` | No | **Codice morto**: nessun riferimento (solo commento in `CommitCommand.cs`), ancora EmbeddedResource nel csproj |

### 1.3 AI (commit message)

| Feature | Dove nel codice | Test | Note / limiti |
|---|---|---|---|
| Genera / Migliora messaggio | `Services/AiServiceBase.cs:43/55`; UI `SvnStatusToolWindowData.cs:437/485` | Sì (via `AiService`) | Prompt in italiano; diff **dell'intera WC** (D3) |
| Provider `auto/copilot/claudecode/claude/openai` + fallback a catena | `Services/AiServiceFactory.cs:66`; `FallbackAiService` (`:127`) | Sì (10 factory + 2 fallback) | In `auto`: Copilot (solo se runtime già scaricato) > Claude Code > Claude API > OpenAI-compatibile |
| OpenAI-compatibile (GitHub Models, OpenAI, Azure, Ollama, Gemini) | `Services/AiService.cs` | Sì (9, MockHttp) | `max_tokens = 200` fisso; retry con messaggio dimezzato su limite token |
| Claude API (SDK `Anthropic` 13.8.0) | `Services/AnthropicAiService.cs` | Solo creazione via factory | Default `claude-sonnet-5-5` (elenco Sonnet/Haiku/Opus) |
| Claude Code (`claude -p`, stdin, senza tool/MCP) | `Services/ClaudeCodeAiService.cs` | Sì (2) | Gira in `%TEMP%` per evitare `CLAUDE.md` di progetto |
| GitHub Copilot (`GitHub.Copilot.SDK` 1.0.31) | `Services/CopilotAiService.cs` | No (serve runtime reale) | Token `gh`/env o login della CLI Copilot |
| Runtime Copilot scaricato al primo uso | `Services/CopilotRuntimeInstaller.cs:49` (release GitHub + SHA-256 da `SHA256SUMS.txt`, in `%LOCALAPPDATA%\SVNAssist\copilot-runtime`) | Sì (4) | ~100 MB; versione dal metadato assembly `CopilotCliVersion`; UI di progresso non presente (da verificare) |
| Token GitHub automatico (`gh auth token`, `GITHUB_TOKEN`, `GH_TOKEN`) | `Services/GitHubTokenResolver.cs`; prompt `AiSetupPrompt.cs` | Sì (4) | Il token va solo a `models.github.ai` (`IsGitHubModelsEndpoint`, testato) |
| Chunking dei diff grandi | `Services/AiServiceBase.cs:72-153` | Parziale (`ExtractFilePaths`) | Limite input ~28.000 caratteri, tarato sul provider più stretto (GitHub Models) per tutti |

### 1.4 Test e tooling

| Cosa | Dove | Note |
|---|---|---|
| 47 test xUnit | `tests/SVNAssist.Tests/` (Ai*, CopilotRuntimeInstaller, GitHubTokenResolver, SvnServiceArgs = unit con runner finto, SvnService = integrazione `file://`) | Integrazione richiede `svn`/`svnadmin` nel PATH |
| Ambiente SVN usa-e-getta | `tools/setup-local-svn.ps1`, `tools/README.md` | Repo `file://` con trunk/branches/tags + `DemoApp.sln` + `branches/feature-demo`: già adatto a testare switch/merge |

---

## 2. Limiti e debiti noti

Ogni voce è verificata nel codice. Le correzioni sono assegnate alle fasi in §4.

| # | Problema | Evidenza | Impatto | Fix proposto | Ore |
|---|---|---|---|---|---|
| D1 | **Revert senza conferma**: `svn revert --depth infinity` direttamente dal context menu | `SvnStatusToolWindowData.cs:648-672`, `SvnService.cs:114` | Perdita irreversibile di lavoro locale | Conferma con elenco file via `Shell().ShowPromptAsync(..., PromptOptions.OKCancel)` (già usato in `SvnSetupAssistant.cs:35`) dietro `IDialogService` iniettabile (come `IAiSetupPrompt`) | 2 |
| D2 | **Delete senza conferma** né gestione file modificati | `SvnStatusToolWindowData.cs:621-645`, `SvnService.cs:102` | Cancellazione dal disco di file/cartelle | Conferma; `--force` solo dopo secondo avviso; test integrazione | 2 |
| D3 | **AI usa il diff dell'intera WC**, non dei file selezionati | `SvnStatusToolWindowData.cs:459` e `:507` -> `GetWorkingCopyDiffAsync` (`SvnService.cs:219`, `svn diff` senza path) | Messaggio che descrive file non inclusi; i file nuovi non aggiunti (`?`) sono invisibili a `svn diff` | `GetDiffAsync(paths)` -> `svn diff <path...>`; per i `?` selezionati diff sintetico dal contenuto | 3 |
| D4 | `DateTimeOffset.Parse` dipendente dalla cultura | `SvnService.cs:202` | L'ISO-8601 di svn in pratica si legge quasi ovunque, ma la chiamata è culture-dependent (e il fallback `MinValue.ToString("O")` è inutile) | `Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)` + test con cultura diversa | 0,5 |
| D5 | Pacchetti di test datati | `SVNAssist.Tests.csproj`: Test.Sdk 17.8.0, xunit 2.5.3, runner 2.5.3, coverlet 6.0.0 (anche `System.Text.Json 10.0.14` su net8: capire il motivo) | Compatibilità/sicurezza tooling | Aggiornare alle ultime stabili e rieseguire i 47 test | 1 |
| D6 | **Nessun test UI / ViewModel** | nessun test su `ToolWindows/`, `Commands/`; `DESIGN.md` §6 lo lascia aperto | La logica più importante (VM da 910 righe) non è coperta | Test degli handler con `ISvnService` finto (verificare ergonomia `AsyncCommand`); UI solo con checklist manuale (`DESIGN.md` §9); test di integrazione in VS **(da verificare** fattibilità con modello out-of-proc) | 10 |
| D7 | **Comandi non nel context menu di Solution Explorer** | nessun `Placements` nei 4 comandi; solo `GroupPlacement.KnownPlacements.ExtensionsMenu` (`SvnAssistMenu.cs:44`) | Per ogni azione su un file si passa dal pannello | F-SE (§3.1) | 14 |
| D8 | **Nessun `--non-interactive` né timeout** | grep: assente in `src/`; `ProcessRunner.cs` senza timeout | Se svn chiede credenziali/trust certificato/conflitti, il processo può restare appeso senza console **(da verificare in VS reale)** | `--non-interactive` in `RunSvnAsync`; `CancelAfter` di timeout; mappare `E170013/E215004/E170001` in messaggi chiari | 2 |
| D9 | Update: output scartato, conflitti non segnalati, comando di menu su WC sbagliata | `SvnService.cs:182`; `UpdateCommand.cs:52` | L'utente non sa a che revisione è arrivato né se ci sono conflitti | `UpdateAsync` ritorna `UpdateResult(revision, conflicts)` (parsing `Updated to revision N.` / `Summary of conflicts:`); il comando usa DI + `FindSvnRoot` | 3 |
| D10 | Resolve marca "risolto" senza controllare i marcatori | `SvnService.cs:126-132` (`--accept working`) | Si committa un file con `<<<<<<<` | Offrire `mine-full`/`theirs-full`/`working`; con `working` avvisare se restano marcatori | 3 |
| D11 | Parser status incompleto | `SvnService.cs:49-72`, `:328` | Tree conflict (`tree-conflicted="true"`), `ignored`, `external`, `obstructed`, `incomplete`, `props="conflicted"` non gestiti | Estendere `SvnStatusKind` + parser; test su XML campione | 3 |
| D12 | Refresh ridondante | 2 timer (1 s + 5 s) + `FileSystemWatcher` che filtra solo `.svn` (`SvnStatusToolWindow.cs:34-35, 254-268`) | Build in `bin/`/`obj/` scatenano `status`+`info` a raffica sulle WC grandi | Unico loop con debounce, ignorare `bin/obj/.vs`, `info` solo al cambio WC | 4 |
| D13 | Log window senza auto-detect né paginazione | `SvnLogToolWindowData.cs:24`; `SvnLogToolWindow.GetContentAsync` non rileva la WC | UX peggiore della Status window | Riusare auto-detect; `-v`; "carica altre" | 3 |
| D14 | Commit non mostra la revisione, non gestisce out-of-date | `SvnStatusToolWindowData.cs:395` | Non si sa cosa si è creato | Parsare `Committed revision N.`; su errore out-of-date suggerire Update | 1 |
| D15 | Codice morto e docs non allineate | `Dialogs/CommitDialog*`; `DESIGN.md` §1 ("13 test", AI solo OpenAI-compatibile); `README.md` ("Architettura: AI formato OpenAI") | Confusione | Rimuovere il dialog; aggiornare README/DESIGN | 1,5 |
| D16 | Limiti AI tarati sul provider più stretto | `AiServiceBase.cs:17-26` (8000 token totali), `AiService.cs:58` (`max_tokens=200`) | Blocca AI più ricca (spiegare revisione, assistente conflitti) | Limiti per provider (proprietà virtuale) e `maxTokens` parametrico | 3 |
| D17 | API key in chiaro nello store impostazioni | `SettingsService.cs` (commento sul DPAPI) | Segreti leggibili dal profilo | `ProtectedData` (DPAPI) o Credential Manager | 4 |
| D18 | `ISvnService` e ViewModel crescono senza struttura | `ISvnService.cs` (117 righe, 14 metodi), VM 910 righe | Ogni feature peggiora il God object | Servizi per area (Info/Branch/Merge/Props), un ViewModel per finestra | incluso nelle fasi |

---

## 3. Funzionalità future

### 3.0 Premesse tecniche (valide per tutte le feature)

**Cosa permette il modello out-of-process 17.14** (verificato nelle XML-doc in `%USERPROFILE%\.nuget\packages\microsoft.visualstudio.extensibility*\17.14.*`):

| Capacità | API verificata | Uso |
|---|---|---|
| Comando in un gruppo VSCT esistente | `CommandPlacement.VsctParent(Guid, uint id, ushort priority)`; `GroupPlacement.VsctParent(...)` | Context menu Solution Explorer (F-SE) |
| Path dell'elemento selezionato | `IClientContext.GetSelectedPathAsync(ct)` (extension method, **un solo** path); `ClientContextKey.Shell.ActiveSelectionPath/ActiveSelectionUri/ActiveSelectionFileName` | Comandi "sul file cliccato" |
| Visibilità/abilitazione comandi | `CommandConfiguration.VisibleWhen` / `EnabledWhen`; `ActivationConstraint.ClientContext(key, regex)` | Solo vincoli su pattern di path, non "è versionato" (servirebbe `.svn`) |
| Prompt modali | `ShowPromptAsync(string, PromptOptions<T>)` (scelte custom); `ShowPromptAsync(string, InputPromptOptions)` (testo libero) | Conferme, nome branch, numero revisione |
| Dialog modale XAML | `ShowDialogAsync(IRemoteUserControl, title, DialogOption, ct)` | Branch picker, merge wizard, properties |
| Dialog file di sistema | `ShowOpenFileDialogAsync`, `ShowSaveAsFileDialogAsync`, `ShowOpenFolderDialogAsync` | Salva/applica patch |
| Progresso | `StartProgressReportingAsync` / `ProgressReporter` | Update/merge lunghi, download runtime |
| Output Window | `OutputWindowExtensibility.CreateOutputChannelAsync`, `OutputChannel.WriteLineAsync` | Log dei comandi svn (supporto) |
| Aprire documenti | `DocumentsExtensibility.OpenTextDocumentAsync(Uri)` | Aprire il file dopo la risoluzione |
| Editor | `TextViewMarginProvider`, `CodeLens`/`InvokableCodeLens` (Editor.Contracts) | Blame inline (P2) |
| Tool window | `ToolWindow`, `ShowToolWindowAsync<T>`, `ToolWindowConfiguration.VisibleWhen` | Già in uso |

**Non risulta nell'SDK locale:** API per aprire il diff viewer di VS, API Language Model/Copilot (già noto, `DESIGN.md` §7), info bar. Per i diff si resta su TortoiseProc/TortoiseMerge o su `vsdiffmerge.exe` di VS **(da verificare percorso/argomenti)**, con fallback inline.

**Limiti Remote UI (XAML standard, niente controlli custom, niente code-behind):** già usati nel repo `Grid/StackPanel/DockPanel`, `ListBox` + `DataTemplate`, `TextBox`, `Button`, `CheckBox`, `ComboBox`, `ContextMenu/MenuItem`, `Style/DataTrigger`, `ObservableList<T>`, `IAsyncCommand`. Pattern obbligati: niente converter -> proprietà già pronte nel VM (`StatusColor` come stringa, flag `CanXxx`); visibilità via `DataTrigger`. **Non verificati** (spike prima di progettare UI su di essi): `TreeView`, `Expander`, `DataGrid`, `Popup`, `KeyBinding`, drag&drop.

**Regole comuni per ogni comando svn nuovo:**
1. Sempre `--non-interactive` (D8) e `ArgumentList` (niente escaping a mano).
2. `--xml` dove esiste (`status, info, log, list, blame, proplist, propget`); `merge, mergeinfo, update, switch, commit` hanno solo testo -> parser dedicati con test su output campione; l'output è localizzato: forzare `LC_ALL=C` nell'ambiente del processo (`ProcessRunner` oggi non imposta l'ambiente, **da verificare**).
3. Operazioni distruttive: conferma via `IDialogService`; operazioni lunghe: `ProgressReporter` + `CancellationToken` (il runner uccide già il processo su cancel).
4. Test: unit con `IProcessRunner` finto (args) + integrazione sul repo `file://` di `tools/setup-local-svn.ps1` (già con `branches/feature-demo`).
5. Spezzare `ISvnService` per area prima di aggiungere metodi (D18).

---

### 3.1 Integrazione IDE

#### F-SE — Context menu di Solution Explorer
- **Cosa fa:** tasto destro su file/cartella/progetto -> sottomenu *SVNAssist* con Diff, Log del file, Blame, Update, Commit di questo file, Revert, Add, Lock/Unlock, Cleanup, Proprietà.
- **Comandi svn:** quelli delle singole feature sul path selezionato (`svn diff <p>`, `svn log -v <p>`, `svn blame --xml <p>`, `svn update <p>`, `svn revert <p>`).
- **UX:** sottomenu unico "SVNAssist"; Revert/Delete sempre con conferma.
- **Note tecniche:** `CommandConfiguration.Placements = [CommandPlacement.VsctParent(guidSHLMainMenu, <id gruppo>, priority)]`. I gruppi dei context menu di Solution Explorer (item, folder, project) sono in `vsshlids.h`: gli ID esatti vanno trovati con uno **spike (4 h)**; servono ID di *gruppo*, non di menu **(da verificare)**. In alternativa un `MenuConfiguration` proprio agganciato con `GroupPlacement.VsctParent`. Il path si ottiene con `await context.GetSelectedPathAsync(ct)`: **selezione singola**; la multi-selezione richiederebbe la Project Query API **(da verificare)**. `VisibleWhen` non sa se il path è in una WC: comando sempre visibile, con messaggio chiaro se il path non è versionato.
- **Effort:** spike 4 h + 10 h (5-6 comandi, `IDialogService`, WC dal path con `FindSvnRoot`) = **14 h**. **Rischio:** medio. **Priorità: P1.**

#### F-UI — UI moderna stile Git Changes
- **Cosa fa:** sezioni raggruppate (Conflitti / Modifiche / Aggiunti / Non versionati / Mancanti) con contatori; header con branch + revisione + indicatore incoming; azione primaria "Commit (n)"; conferme per Revert/Delete; backup `.patch` automatico prima di un Revert (undo).
- **Comandi svn:** `svn status -u --xml` (incoming via `<repos-status>`), `svn info --xml`.
- **UX:** una `ObservableList` per sezione con titolo comprimibile (toggle + `DataTrigger`, non `Expander` finché non verificato); icone vettoriali invece delle emoji; colori dai `VsResourceKeys` (già usati per i ComboBox) per tema chiaro/scuro; stato vuoto "Nessuna modifica".
- **Note tecniche:** spezzare `SvnStatusToolWindowData` in `ChangesSectionViewModel` + `CommitBoxViewModel` + `HeaderViewModel`; `-u` va in rete: refresh incoming separato e meno frequente (60 s o a richiesta).
- **Effort:** 18 h. **Rischio:** medio (limiti controlli). **Priorità: P1** (dopo la Fase 0).

#### F-AUTOCOMPLETE — Autocompletamento del messaggio di commit
- **Cosa fa:** suggerisce (a) nomi dei file modificati, (b) simboli (classi/metodi) dei file cambiati, (c) messaggi recenti dell'utente, (d) riferimenti ticket (`#123`, regex configurabile).
- **Comandi svn:** (a) già in `Files`; (b) `svn diff -x -p <paths>` (hunk header con nome funzione: buono per C/C++, meno per C#) oppure regex sulle righe aggiunte (`class|interface|void|\w+\(`); (c) `svn log --search <utente> -l 30 --xml` (`--search` da svn 1.8) + cache in `%LOCALAPPDATA%\SVNAssist`.
- **UX:** lista cliccabile sotto la TextBox quando si digita `#`/`@` o dopo 3 caratteri; ComboBox "messaggi recenti".
- **Note tecniche:** in Remote UI non c'è controllo autocomplete né accesso al caret: solo lista separata (`HasSuggestions` + `DataTrigger`) con inserimento in coda al testo. Accettare con Tab/Enter richiede `KeyBinding` **(non verificato)**; niente completamento "a metà parola".
- **Effort:** 14 h (6 h per i soli messaggi recenti). **Rischio:** alto (UX limitata). **Priorità: P2.**

---

### 3.2 Branch, switch e merge

Si presume il layout `trunk/ branches/ tags/` come in `tools/setup-local-svn.ps1`. Per layout diversi (es. `/progetto/trunk`) ricavare la root con `svn info --xml` (`<repository><root>`, `<url>`) e prevedere override in impostazioni (`TrunkPath`, `BranchesPath`, `TagsPath`): altrimenti `^/branches` non esiste.

#### F-INFO — Servizio `svn info --xml` (prerequisito)
- **Cosa fa:** modello `SvnInfo` (URL, relative-url, repo root, UUID, revisione WC, ultimo commit, depth, `<lock>`, `<conflict>`) per header, switch, merge, lock, repo browser. Sostituisce `GetBranchNameAsync` (`SvnService.cs:262`) che inghiotte gli errori.
- **Comandi:** `svn info --xml [path|url]`.
- **Effort:** 3 h. **Rischio:** basso. **Priorità: P0** (abilita il resto).

#### F-SWITCH — Switch a un branch/tag
- **Cosa fa:** sposta la WC su un altro branch (o torna a trunk) senza nuovo checkout; mostra sempre il branch corrente.
- **Comandi svn:**
  - elenco: `svn ls --xml ^/branches` e `svn ls --xml ^/tags` (`<entry kind="dir"><name>`, `<commit revision>`, autore, data); trunk fisso `^/trunk`
  - WC pulita: `svn status --xml` senza `modified/added/deleted/conflicted`; revisioni miste: `svn info -R --xml` o `svn status -u --xml` e confronto delle revisioni
  - switch: `svn switch ^/branches/x --non-interactive --accept postpone` (`--ignore-ancestry` solo per branch non correlati, con avviso)
  - branch corrente: `svn info --xml` -> `relative-url`
- **UX:** nell'header di *SVN Changes* il nome del branch diventa un pulsante "Switch..." -> dialog (`ShowDialogAsync`) con ricerca, voci raggruppate trunk/branches/tags, ultimi usati in cima, data/autore dell'ultimo commit. Se la WC è sporca: "Annulla / Procedi (le modifiche restano)" con spiegazione dei possibili conflitti. A fine switch: `At revision N.`, conflitti nella sezione dedicata, refresh.
- **Note tecniche:** filtro della `ListBox` nel VM (nessun filtro nativo); `svn ls` è remoto: cache per sessione + refresh; `ProgressReporter`; dopo lo switch VS potrebbe dover ricaricare la solution: nessuna API out-of-proc nota per forzarlo **(da verificare)**, avvisare l'utente.
- **Effort:** 14 h (ls+parser 3, dialog 5, check dirty + switch + parser 4, test su `feature-demo` 2). **Rischio:** medio. **Priorità: P1.**

#### F-BRANCH — Creazione branch/tag
- **Cosa fa:** crea un branch o un tag dalla WC corrente (o da una revisione) con messaggio, e opzionalmente fa lo switch.
- **Comandi svn:** copia server-side `svn copy ^/trunk ^/branches/x -m "msg"` (sorgente = URL corrente, oppure `-r REV`); idem per `^/tags/x`; `--parents` se manca la cartella contenitore; poi opzionale `svn switch ^/branches/x`. Esistenza: `svn info ^/branches/x` (exit code != 0 se manca).
- **UX:** dialog: tipo (Branch/Tag), nome (validazione: niente spazi), sorgente (corrente/trunk/revisione), messaggio (anche generabile con AI), "Passa al nuovo branch"; per i tag avviso "per convenzione non si committa sui tag".
- **Note tecniche:** è un commit vero (crea una revisione): niente dry-run, quindi conferma prima. Da URL non include le modifiche locali: dirlo nel dialog.
- **Effort:** 8 h. **Rischio:** basso. **Priorità: P1.**

#### F-MERGE — Merge
Premessa: dalla 1.8 `svn merge` usa il merge-tracking automatico (`svn:mergeinfo`) e `--reintegrate` è deprecato. Ogni merge opera sulla WC corrente (target) e **non committa**: l'utente rivede e poi committa. Pre-condizione: WC pulita e a revisione singola (`svn update` prima), altrimenti l'abort (`revert`) si porta via lavoro non correlato.

**M1 — Sync trunk -> branch**
- Pre: WC su un branch, pulita, aggiornata.
- Anteprima: `svn merge --dry-run ^/trunk` (righe `U/A/D/C/G path`); revisioni candidate: `svn mergeinfo --show-revs eligible ^/trunk .` (testo, una `rNNN` per riga) + log `svn log --xml -r <min>:<max> ^/trunk` filtrato lato client.
- Esecuzione: `svn merge ^/trunk --non-interactive --accept postpone`; poi sezione Conflitti + commit con messaggio precompilato `Merge trunk -> branches/x (r120-r135)`.

**M2 — Reintegrate branch -> trunk**
- Pre: WC su trunk (se è su un branch proporre switch a trunk via F-SWITCH), aggiornata a HEAD, pulita.
- Controllo: prima va fatto il sync M1; verifica con `svn mergeinfo --show-revs eligible ^/trunk ^/branches/x` (non vuoto = il branch non è allineato: avvisare).
- Esecuzione: `svn merge ^/branches/x --non-interactive --accept postpone` (senza `--reintegrate`), dry-run con `--dry-run`, commit standard; a fine lavoro proposta (non automatica) `svn delete ^/branches/x -m "..."`.

**M3 — Cherry-pick di singole revisioni**
- Elenco: stesso di M1; l'utente spunta 1..N revisioni.
- Esecuzione: `svn merge -c 123 ^/trunk` (o `-c 123,125,130`, `-r 122:123`).
- Undo di un commit già fatto: `svn merge -c -123 .` (da menu del Log: "Annulla questa revisione", alto valore e rischio basso). Dal Log anche "Merge questa revisione nella WC" (sorgente = URL dell'elemento).

**M4 — Dry-run / anteprima**
- `svn merge --dry-run <sorgente>` è il primo passo obbligatorio di ogni procedura: pannello con conteggi (aggiornati, aggiunti, in conflitto) ed elenco, poi "Esegui"/"Annulla". Avviso: il dry-run non prevede sempre i conflitti di albero/proprietà.

**M5 — Flusso di risoluzione conflitti**
- Rilevazione: `svn status --xml` (`item="conflicted"`, `tree-conflicted="true"`, `props="conflicted"`): richiede D11.
- Opzioni per file (dialog con 3 azioni + 2 esterne):
  - Usa il mio: `svn resolve --accept mine-full <path>`
  - Usa il loro: `svn resolve --accept theirs-full <path>`
  - Risolto a mano: `svn resolve --accept working <path>` (solo se non restano marcatori, D10)
  - Editor 3-way: `TortoiseProc.exe /command:conflicteditor /path:<file>` (`SvnLocator.FindTortoiseProc` esiste già) oppure TortoiseMerge sui file `.mine`, `.rOLD`, `.rNEW` generati accanto al file **(da verificare nomi/argomenti con la versione installata)**; fallback: aprire il file con i marcatori in VS (`OpenTextDocumentAsync`).
  - Tree conflict: spiegazione testuale e `resolve --accept working` solo dopo scelta esplicita; nessun automatismo.
- Dopo ogni risoluzione refresh; il commit va bloccato finché restano conflitti (oggi non lo è).

**M6 — Abort**
- Non esiste `svn merge --abort`: equivalente `svn revert -R .` (valido solo se la WC era pulita prima: controllo all'avvio). Conferma con elenco file e **backup** preventivo `svn diff > backup.patch` in `%LOCALAPPDATA%\SVNAssist\undo` (richiede F-PATCH).

- **UX complessiva:** wizard modale in 3 passi (Sorgente/tipo -> Anteprima dry-run + revisioni eligible -> Esecuzione e conflitti); poi il pannello *SVN Changes* passa in "modalità merge" con banner "Merge in corso da ^/trunk — Commit / Annulla merge".
- **Note tecniche:** serve parser del testo di `svn merge`/`svn mergeinfo` (nessun `--xml`) e `LC_ALL=C`; `mergeinfo` richiede il server; le revisioni eligible possono essere migliaia (paginazione client); `svn:externals`, sparse depth, rename/tree conflict e subtree mergeinfo fuori scope iniziale (documentare).
- **Effort:** M1+M4 12 h, M2 8 h, M3 10 h, M5 12 h, M6 3 h, test integrazione su repo locale con conflitto forzato 6 h = **~50 h**. **Rischio:** alto (output testuale, casi limite, localizzazione, WC non pulita). **Priorità: P1** per M1/M3/M4/M5, **P2** per M2 (il reintegro in trunk spesso si fa con Tortoise/CI).
- **Scorciatoia a basso rischio (spike 3 h):** per la prima release lanciare `TortoiseProc.exe /command:merge|switch /path:<wc>` quando TortoiseSVN è installato e rimandare la UI nativa.

---

### 3.3 Storia e analisi

#### F-BLAME — Blame / Annotate
- **Cosa fa:** per ogni riga di un file mostra chi/quando/quale revisione l'ha scritta; click su una riga apre la revisione nel Log.
- **Comandi svn:** `svn blame --xml [-g] [-r BASE] <file>` (`-g` attribuisce al branch sorgente delle righe mergiate) -> `<entry line-number><commit revision><author><date>`. **L'XML non contiene il testo delle righe**: leggerlo dal file locale (se non modificato) o con `svn cat -r <rev> <file>`.
- **UX:** (1) tool window con `ListBox` (rev, autore, data, testo) e filtro autore; (2) P2: `TextViewMarginProvider`/`CodeLens` nell'editor (Editor.Contracts presente; fattibilità del margine Remote UI su file grandi **da verificare**).
- **Note tecniche:** oltre ~5.000 righe virtualizzare/paginare; blame di file binari -> errore da gestire (`E195013`). Voce in F-SE.
- **Effort:** tool window 12 h; margine editor +14 h. **Rischio:** medio. **Priorità: P1** (tool window), P2 (margine).

#### F-LOG — History più ricca (già nel backlog)
- **Cosa fa:** paginazione, filtri autore/testo/path/date, elenco file per revisione, azioni per revisione (copia numero, diff vs working, annulla revisione, merge, update to revision, branch da qui).
- **Comandi svn:** `svn log --xml -v -l 50 -r HEAD:1 [--search txt] [-r {data}:{data}] <target>`; pagina successiva con `-r <ultima-1>:1`; `svn diff -c <rev> <file>` per file singolo; `--stop-on-copy` per limitarsi alla vita del branch.
- **UX:** auto-detect WC come nella Status window, filtro in cima, dettaglio con elenco file (doppio click = diff).
- **Effort:** 12 h (include D13). **Rischio:** basso. **Priorità: P1.**

---

### 3.4 Manutenzione e lavoro quotidiano

#### F-UPD — Update mirato e "Update to revision"
- **Cosa fa:** update di singolo file/cartella e a una revisione specifica (es. per riprodurre un bug), ritorno a HEAD, riepilogo con conflitti.
- **Comandi:** `svn update <path> --non-interactive --accept postpone`; `svn update -r 1234 <path>`; ritorno `svn update -r HEAD`; `--set-depth infinity` per cartelle sparse (P2). Pre-check `svn status -u --xml`.
- **UX:** *Update* e *Update to revision...* (prompt con `InputPromptOptions` per il numero, o scelta dal Log); avviso che una WC a revisione vecchia non è committabile se il file è out-of-date; riepilogo "Aggiornato alla r1234 — 3 aggiornati, 1 conflitto".
- **Note tecniche:** include D9; `UpdateCommand` su DI + root WC.
- **Effort:** 6 h. **Rischio:** basso. **Priorità: P1.**

#### F-CLEAN — Cleanup
- **Cosa fa:** sblocca una WC (`E155004 working copy locked`, operazione precedente non terminata) e ripulisce non versionati/ignorati.
- **Comandi:** `svn cleanup`; `--remove-unversioned`, `--remove-ignored`, `--vacuum-pristines` (1.9+), `--include-externals`.
- **UX:** comando *Cleanup...* con checkbox; le opzioni distruttive spente di default, con conteggio/elenco dei file (da `svn status --xml`) e conferma. Suggerimento automatico quando un comando fallisce con `E155004`.
- **Effort:** 3 h. **Rischio:** basso (opzioni distruttive protette). **Priorità: P1.**

#### F-LOCK — Lock / Unlock
- **Cosa fa:** blocca un file (tipico per binari) e mostra chi detiene il lock.
- **Comandi:** `svn lock -m "motivo" <file>`, `svn unlock <file>`, `--force` per rubare/rompere; visibilità: `svn status -u --xml` (`<lock>` in `wc-status`/`repos-status`), `svn info --xml`; `svn propset svn:needs-lock '*' <file>` per file read-only finché non si fa lock.
- **UX:** colonna/icona lucchetto, voci Lock/Unlock nel context menu e in F-SE, dialog con motivo, conferma per `--force`.
- **Effort:** 6 h. **Rischio:** basso. **Priorità: P2** (P1 se il team ha asset binari).

#### F-CL — Changelist
- **Cosa fa:** raggruppa le modifiche in liste nominate e committa una lista alla volta.
- **Comandi:** `svn changelist <nome> <path...>`, `svn changelist --remove <path...>`, `svn commit --changelist <nome> -m ... [--keep-changelists]`, `svn status --xml` (le entry sono annidate in `<changelist name="...">`: il parser attuale usa `Descendants("entry")` quindi funziona ma ignora il nome).
- **UX:** sezioni per changelist (si integra con F-UI), "Sposta in changelist...", selezione per lista; `ignore-on-commit` come lista convenzionale.
- **Note tecniche:** un file sta in una sola changelist; definire cosa significa selezionare una lista.
- **Effort:** 8 h. **Rischio:** medio (UI). **Priorità: P2.**

#### F-PATCH — Crea / applica patch
- **Cosa fa:** esporta le modifiche selezionate in `.patch` (review via mail/ticket, backup prima di operazioni rischiose) e applica una patch ricevuta.
- **Comandi:** `svn diff <paths> --patch-compatible > file.patch` (`--git` per rename/binari); per i `?` prima `svn add`; applicazione `svn patch file.patch [--dry-run] [--strip N]`; range: `svn diff -r N:M <url>`.
- **UX:** "Crea patch dai file selezionati..." (`ShowSaveAsFileDialogAsync`), "Applica patch..." (`ShowOpenFileDialogAsync`, dry-run con elenco `U/A/D/C/G`, poi conferma). Lo stesso meccanismo alimenta il backup di Revert, Abort merge e Shelve.
- **Note tecniche:** salvare UTF-8 senza BOM; per diff molto grandi estendere `IProcessRunner` con output su file per non tenerli in memoria.
- **Effort:** 7 h. **Rischio:** basso. **Priorità: P1** (alto valore, abilita l'undo sicuro).

#### F-SHELVE — Shelve / stash
- **Cosa fa:** mette da parte le modifiche locali e ripulisce la WC (come `git stash`).
- **Opzione A (sconsigliata):** `svn shelve <nome>`, `svn unshelve`, `svn shelves`: comandi **sperimentali** (1.10-1.14), richiedono `SVN_EXPERIMENTAL_COMMANDS` e il formato cambia tra versioni.
- **Opzione B (consigliata):** F-PATCH + `svn revert -R` + registro in `%LOCALAPPDATA%\SVNAssist\shelf`; ripristino con `svn patch`. Stabile su ogni versione.
- **Effort:** 8 h (dopo F-PATCH). **Rischio:** medio (perdita dati se la patch è incompleta: `--dry-run` prima del revert). **Priorità: P2.**

#### F-PROP — Editor di proprietà
- **Cosa fa:** vede/modifica le proprietà SVN (`svn:ignore`, `svn:externals`, `svn:keywords`, `svn:eol-style`, `svn:mime-type`, `svn:executable`, `svn:needs-lock`).
- **Comandi:** `svn proplist -v --xml <path>`, `svn propget --xml <nome> <path>`, `svn propset <nome> <valore> <path>` (multilinea: `-F file` temporaneo; oggi `AddToIgnoreListAsync` passa il valore come argomento, mai provato su svn reale), `svn propdel <nome> <path>`; `svn propedit` non usabile (editor esterno).
- **UX:** dialog nome/valore con TextBox multilinea, template per `svn:ignore` (un pattern per riga) e `svn:externals`; avviso "le proprietà vanno committate sulla cartella".
- **Effort:** 12 h. **Rischio:** medio (multilinea, sintassi `svn:externals` 1.5+). **Priorità: P2.**

#### F-REPO — Repository browser
- **Cosa fa:** naviga l'URL del repository senza checkout, apre un file in sola lettura, esporta o fa checkout di una cartella.
- **Comandi:** `svn ls --xml -v <url>`, `svn cat <url>@REV`, `svn export <url> <dest>`, `svn checkout <url> <dest>`, `svn info --xml <url>`; `svn ls -R --xml` solo su richiesta.
- **UX:** finestra con breadcrumb + lista (non albero: `TreeView` non verificato), caricamento lazy, menu "Checkout...", "Copia URL", "Log", "Crea branch qui".
- **Note tecniche:** operazioni di rete, sempre async/annullabili; credenziali (domanda 3). `DESIGN.md` lo dava "probabilmente fuori scope".
- **Effort:** 16 h. **Rischio:** medio. **Priorità: P2.**

#### F-RELOC — Relocate
- **Cosa fa:** aggiorna l'URL della WC quando il server cambia indirizzo.
- **Comandi:** `svn info --xml` (URL attuale), `svn relocate <nuovo-url>` (1.7+); verifica prima `svn info <nuovo-url>`.
- **UX:** suggerimento dopo errore di connessione + dialog URL attuale/nuovo.
- **Effort:** 3 h. **Rischio:** basso. **Priorità: P2.**

---

### 3.5 Diff

#### F-DIFF — Diff senza dipendere da TortoiseSVN
- **Cosa fa:** oggi il diff è TortoiseSVN oppure testo unificato (`SvnService.cs:233, 226`). Obiettivo: diff utilizzabile con il solo `svn.exe`.
- **Comandi:** `svn cat -r BASE <file>` su file temporaneo + `vsdiffmerge.exe <base> <working> <labelBase> <labelWorking>` **(da verificare percorso e argomenti; nessuna API out-of-proc nota)**; fallback: diff unificato colorato (una `ListBox` di righe con colore calcolato nel VM, perché la `TextBox` non colora).
- **Effort:** 6 h (spike diff VS 2 h + colorato inline 4 h). **Rischio:** medio. **Priorità: P1.**

---

### 3.6 VS Code

#### F-VSCODE — Versione per Visual Studio Code (già prioritaria in `DESIGN.md` §8)
- **Cosa fa:** stessa esperienza (status, commit, update, log, diff, AI) in VS Code, con **commit message AI tramite il Copilot dell'utente (`vscode.lm`)**, impossibile oggi in VS.
- **Architettura proposta:** progetto `SVNAssist.Core` (net8.0, senza riferimenti VS) con Models, `SvnService`, `IProcessRunner`, parser, `AiServiceBase` e provider; CLI `svnassist-core` (JSON-RPC su stdio o `--json`) che espone `status/diff/commit/log/...`. L'estensione VS referenzia il Core; quella VS Code (TypeScript) lo avvia e usa `vscode.scm.createSourceControl` (SCM API), `vscode.lm.selectChatModels({ vendor: 'copilot' })` per l'AI, `vscode.commands` per update/log. Oggi `Services/` è quasi già VS-free: da staccare `SettingsService`, `AiSetupPrompt`, `SvnSetupAssistant`, `CopilotRuntimeInstaller` (in VS Code il runtime Copilot **non serve**).
- **Alternativa:** reimplementare in TypeScript (spawn `svn`, parser XML): meno infrastruttura, ma logica SVN e test duplicati.
- **Spike (16 h):** estrazione del Core (8 h, utile comunque per D18) + estensione minimale status/commit + `vscode.lm` (8 h) -> decisione Core condiviso vs TS.
- **Effort:** spike 16 h + MVP ~60 h (SCM provider, commit, update, log/diff, packaging) + parità ~40 h. **Rischio:** medio-alto (due ecosistemi, manutenzione doppia, distribuzione del Core .NET: self-contained pesante oppure richiede runtime). **Priorità: P1** per lo spike, P2 per il resto finché lo spike non decide.

---

### 3.7 AI extras

Prerequisiti comuni: D16 (limiti e `maxTokens` per provider) e un metodo pubblico generico `IAiService.AskAsync(system, user, maxTokens, ct)` (oggi `CompleteAsync` è `protected`, `AiServiceBase.cs:40`). Privacy: ogni funzione che invia contenuto di file (non solo diff) richiede consenso esplicito e una impostazione dedicata.

| ID | Cosa fa per l'utente | Dati / comandi svn | UX | Note tecniche | Ore | Rischio | Prio |
|---|---|---|---|---|---|---|---|
| AI-SEL | Commit message generato **solo dai file selezionati** (fix D3) | `svn diff <selezionati>`; per i `?` diff sintetico dal contenuto | stesso pulsante AI, badge "su 3 file" | riusa chunking; test su subset | 3 (Fase 0) | Basso | **P0** |
| AI-EXPLAIN | "Spiega questa revisione": intento e rischi a parole | `svn log -c REV -v --xml` + `svn diff -c REV` (`GetDiffAsync`, `SvnService.cs:212`) | pulsante nel Log, risposta in pannello | risposta > 200 token (D16) | 6 | Basso | P1 |
| AI-SUMMARY | "Cosa sta per arrivare" prima dell'Update | `svn status -u --xml`, `svn log -r BASE:HEAD -v --xml`, `svn diff -r BASE:HEAD <paths>` | banner "5 file in arrivo" -> riassunto AI + lista, poi Update | il diff incoming può essere enorme: riassumere log+path, diff solo su richiesta | 6 | Basso | P1 |
| AI-CONFLICT | Assistente ai conflitti: propone il file unito e spiega la scelta | file con marcatori + `svn cat -r BASE`, `.mine/.rOLD/.rNEW` | nel dialog conflitti (M5) "Proponi risoluzione": mostra il diff proposto, **applica solo con conferma**, mai `resolve` automatico | limite token: file piccoli o singoli hunk; backup `.patch` prima; poi `resolve --accept working` manuale | 14 | Alto (correttezza, privacy) | P2 |
| AI-REVIEW | Pre-commit check: file di debug, segreti, TODO nel diff | `svn diff <selezionati>` | warning prima del commit | euristiche locali (regex chiavi) prima dell'AI | 6 | Medio | P2 |

---

## 4. Roadmap proposta

Ordine: **sicurezza e quick win -> mancanze quotidiane -> storia/analisi -> branching -> UI/AI -> avanzate -> VS Code (dopo lo spike).** Ore indicative (±30%), comprensive di test automatici, non del collaudo manuale in VS.

| Fase | Contenuto | Dipende da | Ore | Perché in quest'ordine |
|---|---|---|---|---|
| **Fase 0 — Sicurezza e quick win** | D1, D2 conferme + `IDialogService`; D8 `--non-interactive`/timeout; D3/AI-SEL diff dei soli selezionati; D10 resolve sicuro; D9 update con esito + DI; D14 revisione nel commit; D4 date; D11 parser status; D12 watcher/timer; D13 Log auto-detect; D5 pacchetti test; D15 pulizia/docs; D6 test VM di base | — | **~35** | Elimina rischi di perdita dati e possibili hang prima di aggiungere funzioni; tutte voci piccole e già individuate |
| **Fase 1 — Fondamenta e quotidiano** | F-INFO; refactor di `ISvnService` per area (D18); F-UPD; F-CLEAN; F-SE (spike + comandi); F-DIFF; F-LOCK; **spike F-VSCODE 16 h (con estrazione Core)** | Fase 0 | **~60** | Chiude il backlog "priorità alta" di `DESIGN.md`; F-INFO e il refactor servono a tutto il resto; lo spike decide l'architettura prima di scrivere codice che andrebbe spostato |
| **Fase 2 — Storia e portabilità del lavoro** | F-LOG; F-BLAME (tool window); F-PATCH; F-CL; D16; AI-EXPLAIN | Fase 1 | **~45** | Valore alto, rischio basso, poca rete; F-PATCH abilita il backup "undo" usato da merge e shelve |
| **Fase 3 — Branching** | F-SWITCH; F-BRANCH; F-MERGE (M1, M3, M4, M5, M6, poi M2); AI-SUMMARY | Fasi 0-1 (D8, F-INFO, D11, F-PATCH) | **~85** | La parte più rischiosa e costosa: dopo la messa in sicurezza e con test d'integrazione su `file://` (`setup-local-svn.ps1` ha già `branches/feature-demo`). Prima iterazione opzionale con delega a TortoiseProc (3 h) |
| **Fase 4 — UI moderna e AI** | F-UI; F-AUTOCOMPLETE (almeno "messaggi recenti"); AI-REVIEW; D17 (DPAPI) | Fasi 1-3 (le sezioni usano changelist/merge/lock) | **~45** | Rifare la UI a feature stabili evita di rifarla due volte |
| **Fase 5 — Avanzate (a richiesta)** | F-PROP; F-REPO; F-SHELVE (variante patch); F-RELOC; AI-CONFLICT; blame inline nell'editor | Fasi 2-3 | **~75** | Valore di nicchia o rischio alto: solo se richiesti da utenti reali |
| **Fase 6 — VS Code (MVP)** | Esito dello spike: SCM provider + commit + update + log/diff + AI via `vscode.lm` + packaging | Fase 1 (Core estratto) | **~100** | Può partire in parallelo alla Fase 2 se lo spike è positivo e c'è un secondo sviluppatore; è la priorità dichiarata in `DESIGN.md` |

**Totale indicativo:** ~445 h (Fasi 0-5 ~345 h, Fase 6 ~100 h).

**Quick win (< 1 giornata ciascuno):** D1, D2, D4, D5, D8, D14, D15, F-INFO (3 h), F-CLEAN (3 h), F-RELOC (3 h).

**Rischi trasversali:**
1. Comportamento in VS reale non verificato (tool window, Remote UI, DI, watcher: `DESIGN.md` §8-§9): prevedere una sessione F5 a fine di ogni fase.
2. Output testuale di svn localizzato: forzare `LC_ALL=C` o usare solo `--xml` dove possibile.
3. Credenziali di rete: nessuna UI di autenticazione, oggi si dipende dalla cache di svn/TortoiseSVN.
4. Versioni di svn diverse (1.8-1.14): `mergeinfo --show-revs`, `cleanup --vacuum-pristines`, `svn shelve` hanno requisiti minimi diversi; rilevare con `svn --version --quiet` e nascondere le funzioni non supportate.

---

## 5. Domande aperte per il proprietario

1. **Ordine VS Code vs VS:** `DESIGN.md` mette VS Code come "prossimo step"; qui propongo solo lo *spike* in Fase 1 e il porting dopo Switch/Merge. Preferisci anticipare la Fase 6 prima del branching?
2. **Uso reale dei repository:** i repo seguono `trunk/branches/tags` alla radice? Si lavora con una WC che cambia branch (switch) o una WC per branch? Il reintegro verso trunk oggi si fa con TortoiseSVN? (determina la priorità di F-SWITCH e M2.)
3. **Autenticazione:** i server SVN reali richiedono credenziali non in cache (basic/NTLM/certificati)? Se sì serve una UI di login (e un posto dove salvarle) prima di imporre `--non-interactive` ovunque.
4. **Dipendenza da TortoiseSVN:** è accettabile delegare diff/merge/conflitti a TortoiseSVN quando è installato (costo basso), o l'estensione deve funzionare con il solo `svn.exe`? (differenza di ~25 h tra F-DIFF/M5 e UI custom.)
5. **Modello Claude di default e privacy AI:** Il default per Claude Code/API (`SettingDefinitions`) è ora `claude-sonnet-5-5` (risolto; Haiku è più economico, selezionabile dall'elenco). Le funzioni che inviano contenuto di file (AI-CONFLICT, AI-REVIEW) vanno abilitate solo con consenso esplicito?
