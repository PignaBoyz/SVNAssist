# SVNAssist — Roadmap e piano funzionalità

> Aggiornata alla **0.2.0** (2026-11-12). Stato verificato sul codice (`src/SVNAssist.Backend`, `.Host`, `.UI.Web`) e su `docs/release-notes/v0.2.0.md`, `docs/verifica/0.2.0/`.
> Convenzioni: ore = sviluppo + test automatici, senza collaudo manuale in VS; priorità P0 = sicurezza/blocca l'uso, P1 = valore alto, P2 = nice to have.
> Le specifiche di §3 sono quelle scritte prima della 0.2.0: le voci già uscite sono marcate "✅ fatto in 0.2.0" e restano come riferimento (alcuni dettagli tecnici, es. "UI web", sono superati dalla nuova architettura).

---

## 1. Stato attuale (0.3.0)

> 0.3.0 (2026-11-12): Switch, Merge/cherry-pick, conflitti con editor 3-way, annulla righe dal diff, indicatore revisione, AI a blocchi. Le righe sotto descrivono la base 0.2.0; le novità 0.3.0 sono in `docs/release-notes/v0.3.0.md`.

Architettura: host VS net472 in-process (`SVNAssist.Host`: comandi, menu, opzioni, WebView2, diff nativo) + backend .NET 10 su JSON-RPC (`SVNAssist.Backend`: servizi SVN e AI, store per Changes/Log/Wizard) + UI web (`SVNAssist.UI.Web`: HTML/CSS/JS, temi, i18n IT/EN). Vedi `DESIGN.md`.

| Area | Stato in 0.2.0 | Limiti |
|---|---|---|
| Changes (albero, Staged, filtro, tastiera) | F-UI fatto: sezioni Staged / Modifiche / Non versionati / Conflitti, Commit All / Commit Staged (n), feedback sulle azioni | Niente indicatore "in arrivo" (`status -u`) |
| Commit / Add / Delete / Revert / Ignore | Fatti, con conferme (D1, D2), revisione mostrata (D14), bloccato con conflitti | Proprietà multilinea (`svn:ignore`) poco provate |
| Conflitti | Resolve con Working / Mine-full / Theirs-full / Base; tree-conflict con conferma (D10, D11 in parte) | Nessun editor 3-way |
| Update | Esito con revisione e conflitti (D9); **F-UPD**: Update to revision con elenco revisioni | Nessuna anteprima di cosa arriva |
| Cleanup, Lock/Unlock | **F-CLEAN** e **F-LOCK** fatti (cleanup suggerito su WC bloccata; steal lock con conferma) | I lock di altri utenti non sono elencati |
| Log | **F-LOG** fatto: documento nell'area editor, filtri, file per revisione, confronto tra revisioni | Niente revisioni eligible / azioni per revisione |
| Diff | **F-DIFF** fatto: diff nativo di VS (file, cartelle, revisioni), TortoiseSVN non serve | — |
| Esplora soluzioni | **F-SE** fatto: sottomenu SVN su file/cartella/progetto/solution | Selezione singola |
| Login SVN | Dialogo utente/password solo su errore di autenticazione, ripete il comando; cache standard svn | Nessun supporto certificati client |
| Info | `svn info` usato per branch e root (header "/ (radice)" se manca trunk/branches) | Modello F-INFO completo non fatto |
| AI | Procedura guidata, 4 provider, **AI-SEL** (diff dei soli Staged, D3), regole di commit copilot-instructions, limiti diff (`DiffLimiter`) | Limiti per provider (D16), modelli fissi |
| Sicurezza | `--non-interactive` e timeout (D8), `LC_ALL=C`, **D17 DPAPI** per le API key, `SensitiveFiles` | — |
| Piattaforma | Temi, IT/EN a caldo, controllo .NET 10 e proposta di installazione, release GitHub Actions | VS 2022 non provata; nessun VS Code |
| Test | xUnit (unit + integrazione `file://`), test UI web (`tests/ui-web`); collaudo F5 con `docs/verifica/0.2.0/checklist-F5.md` | Menu/WebView2 solo via F5 manuale |

---

## 2. Limiti e debiti noti

Fatti in 0.2.0 (non più debiti): D1, D2 (conferme), D3 (AI sui soli selezionati), D4 (date), D7 (menu Esplora soluzioni), D8 (non-interactive/timeout), D9 (update con esito), D12 (refresh con debounce, `RefreshDebouncer`), D13 (Log completo), D14 (revisione nel commit), D15 (dialog morto rimosso, nuova architettura), D17 (DPAPI).

Ancora aperti:

| # | Problema | Impatto | Fix proposto | Ore |
|---|---|---|---|---|
| D5 | Pacchetti di test datati (xunit, Test.Sdk, coverlet) | Compatibilità tooling | Aggiornare alle ultime stabili | 1 |
| D6 | Nessun test automatico su host/WebView2/menu | Regressioni scoperte solo con F5 | Checklist F5 a ogni release; valutare test di integrazione dell'host | 10 |
| D10/D11 | Resolve "working" con marcatori residui e parser status incompleto (`ignored`, `external`, `obstructed`, `props="conflicted"`) | Commit di file con `<<<<<<<`; conflitti di proprietà non visti | Avviso marcatori + estendere stato e parser (serve al merge, vedi §4) | 3 + 3 |
| D16 | Limiti AI tarati sul provider più stretto (`max_tokens`, soglie token) | Blocca AI più ricca (spiega revisione, review) | Limiti per provider, `maxTokens` parametrico | 3 |
| D18 | `ISvnService` cresce senza struttura per area | Ogni feature peggiora il servizio | Servizi per area (Info/Branch/Merge) a partire dalla 0.3.0 | incluso in §4 |
| D19 | Elenco modelli AI fisso (Copilot stimato) | Modelli nuovi mancanti | Elenco dinamico dal provider | 5 |
| D20 | Layout repository assunto `trunk/branches/tags` | Branch picker/merge inutili su layout diversi | Impostazioni `TrunkPath`/`BranchesPath`/`TagsPath` | 2 |
| D21 | Copilot: runtime ~100 MB scaricato al primo uso | Primo uso lento | Barra di avanzamento verificata | 3 |

---

## 3. Funzionalità future (specifiche; le voci ✅ sono uscite)

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

**Limiti UI web (XAML standard, niente controlli custom, niente code-behind):** già usati nel repo `Grid/StackPanel/DockPanel`, `ListBox` + `DataTemplate`, `TextBox`, `Button`, `CheckBox`, `ComboBox`, `ContextMenu/MenuItem`, `Style/DataTrigger`, `ObservableList<T>`, `IAsyncCommand`. Pattern obbligati: niente converter -> proprietà già pronte nel VM (`StatusColor` come stringa, flag `CanXxx`); visibilità via `DataTrigger`. **Non verificati** (spike prima di progettare UI su di essi): `TreeView`, `Expander`, `DataGrid`, `Popup`, `KeyBinding`, drag&drop.

**Regole comuni per ogni comando svn nuovo:**
1. Sempre `--non-interactive` (D8) e `ArgumentList` (niente escaping a mano).
2. `--xml` dove esiste (`status, info, log, list, blame, proplist, propget`); `merge, mergeinfo, update, switch, commit` hanno solo testo -> parser dedicati con test su output campione; l'output è localizzato: forzare `LC_ALL=C` nell'ambiente del processo (`ProcessRunner` oggi non imposta l'ambiente, **da verificare**).
3. Operazioni distruttive: conferma via `IDialogService`; operazioni lunghe: `ProgressReporter` + `CancellationToken` (il runner uccide già il processo su cancel).
4. Test: unit con `IProcessRunner` finto (args) + integrazione sul repo `file://` di `tools/setup-local-svn.ps1` (già con `branches/feature-demo`).
5. Spezzare `ISvnService` per area prima di aggiungere metodi (D18).

---

### 3.1 Integrazione IDE

#### F-SE — Context menu di Solution Explorer — ✅ fatto in 0.2.0
- **Cosa fa:** tasto destro su file/cartella/progetto -> sottomenu *SVNAssist* con Diff, Log del file, Blame, Update, Commit di questo file, Revert, Add, Lock/Unlock, Cleanup, Proprietà.
- **Comandi svn:** quelli delle singole feature sul path selezionato (`svn diff <p>`, `svn log -v <p>`, `svn blame --xml <p>`, `svn update <p>`, `svn revert <p>`).
- **UX:** sottomenu unico "SVNAssist"; Revert/Delete sempre con conferma.
- **Note tecniche:** `CommandConfiguration.Placements = [CommandPlacement.VsctParent(guidSHLMainMenu, <id gruppo>, priority)]`. I gruppi dei context menu di Solution Explorer (item, folder, project) sono in `vsshlids.h`: gli ID esatti vanno trovati con uno **spike (4 h)**; servono ID di *gruppo*, non di menu **(da verificare)**. In alternativa un `MenuConfiguration` proprio agganciato con `GroupPlacement.VsctParent`. Il path si ottiene con `await context.GetSelectedPathAsync(ct)`: **selezione singola**; la multi-selezione richiederebbe la Project Query API **(da verificare)**. `VisibleWhen` non sa se il path è in una WC: comando sempre visibile, con messaggio chiaro se il path non è versionato.
- **Effort:** spike 4 h + 10 h (5-6 comandi, `IDialogService`, WC dal path con `FindSvnRoot`) = **14 h**. **Rischio:** medio. **Priorità: P1.**

#### F-UI — UI moderna stile Git Changes — ✅ fatto in 0.2.0
- **Cosa fa:** sezioni raggruppate (Conflitti / Modifiche / Aggiunti / Non versionati / Mancanti) con contatori; header con branch + revisione + indicatore incoming; azione primaria "Commit (n)"; conferme per Revert/Delete; backup `.patch` automatico prima di un Revert (undo).
- **Comandi svn:** `svn status -u --xml` (incoming via `<repos-status>`), `svn info --xml`.
- **UX:** una `ObservableList` per sezione con titolo comprimibile (toggle + `DataTrigger`, non `Expander` finché non verificato); icone vettoriali invece delle emoji; colori dai `VsResourceKeys` (già usati per i ComboBox) per tema chiaro/scuro; stato vuoto "Nessuna modifica".
- **Note tecniche:** spezzare `SvnStatusToolWindowData` in `ChangesSectionViewModel` + `CommitBoxViewModel` + `HeaderViewModel`; `-u` va in rete: refresh incoming separato e meno frequente (60 s o a richiesta).
- **Effort:** 18 h. **Rischio:** medio (limiti controlli). **Priorità: P1** (dopo la Fase 0).

#### F-AUTOCOMPLETE — Autocompletamento del messaggio di commit
- **Cosa fa:** suggerisce (a) nomi dei file modificati, (b) simboli (classi/metodi) dei file cambiati, (c) messaggi recenti dell'utente, (d) riferimenti ticket (`#123`, regex configurabile).
- **Comandi svn:** (a) già in `Files`; (b) `svn diff -x -p <paths>` (hunk header con nome funzione: buono per C/C++, meno per C#) oppure regex sulle righe aggiunte (`class|interface|void|\w+\(`); (c) `svn log --search <utente> -l 30 --xml` (`--search` da svn 1.8) + cache in `%LOCALAPPDATA%\SVNAssist`.
- **UX:** lista cliccabile sotto la TextBox quando si digita `#`/`@` o dopo 3 caratteri; ComboBox "messaggi recenti".
- **Note tecniche:** in UI web non c'è controllo autocomplete né accesso al caret: solo lista separata (`HasSuggestions` + `DataTrigger`) con inserimento in coda al testo. Accettare con Tab/Enter richiede `KeyBinding` **(non verificato)**; niente completamento "a metà parola".
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
- **UX:** (1) tool window con `ListBox` (rev, autore, data, testo) e filtro autore; (2) P2: `TextViewMarginProvider`/`CodeLens` nell'editor (Editor.Contracts presente; fattibilità del margine UI web su file grandi **da verificare**).
- **Note tecniche:** oltre ~5.000 righe virtualizzare/paginare; blame di file binari -> errore da gestire (`E195013`). Voce in F-SE.
- **Effort:** tool window 12 h; margine editor +14 h. **Rischio:** medio. **Priorità: P1** (tool window), P2 (margine).

#### F-LOG — History più ricca (già nel backlog) — ✅ fatto in 0.2.0
- **Cosa fa:** paginazione, filtri autore/testo/path/date, elenco file per revisione, azioni per revisione (copia numero, diff vs working, annulla revisione, merge, update to revision, branch da qui).
- **Comandi svn:** `svn log --xml -v -l 50 -r HEAD:1 [--search txt] [-r {data}:{data}] <target>`; pagina successiva con `-r <ultima-1>:1`; `svn diff -c <rev> <file>` per file singolo; `--stop-on-copy` per limitarsi alla vita del branch.
- **UX:** auto-detect WC come nella Status window, filtro in cima, dettaglio con elenco file (doppio click = diff).
- **Effort:** 12 h (include D13). **Rischio:** basso. **Priorità: P1.**

---

### 3.4 Manutenzione e lavoro quotidiano

#### F-UPD — Update mirato e "Update to revision" — ✅ fatto in 0.2.0
- **Cosa fa:** update di singolo file/cartella e a una revisione specifica (es. per riprodurre un bug), ritorno a HEAD, riepilogo con conflitti.
- **Comandi:** `svn update <path> --non-interactive --accept postpone`; `svn update -r 1234 <path>`; ritorno `svn update -r HEAD`; `--set-depth infinity` per cartelle sparse (P2). Pre-check `svn status -u --xml`.
- **UX:** *Update* e *Update to revision...* (prompt con `InputPromptOptions` per il numero, o scelta dal Log); avviso che una WC a revisione vecchia non è committabile se il file è out-of-date; riepilogo "Aggiornato alla r1234 — 3 aggiornati, 1 conflitto".
- **Note tecniche:** include D9; `UpdateCommand` su DI + root WC.
- **Effort:** 6 h. **Rischio:** basso. **Priorità: P1.**

#### F-CLEAN — Cleanup — ✅ fatto in 0.2.0
- **Cosa fa:** sblocca una WC (`E155004 working copy locked`, operazione precedente non terminata) e ripulisce non versionati/ignorati.
- **Comandi:** `svn cleanup`; `--remove-unversioned`, `--remove-ignored`, `--vacuum-pristines` (1.9+), `--include-externals`.
- **UX:** comando *Cleanup...* con checkbox; le opzioni distruttive spente di default, con conteggio/elenco dei file (da `svn status --xml`) e conferma. Suggerimento automatico quando un comando fallisce con `E155004`.
- **Effort:** 3 h. **Rischio:** basso (opzioni distruttive protette). **Priorità: P1.**

#### F-LOCK — Lock / Unlock — ✅ fatto in 0.2.0
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

#### F-DIFF — Diff senza dipendere da TortoiseSVN — ✅ fatto in 0.2.0
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

---

## 4. Piano 0.3.0 — Merge da Visual Studio (USCITA il 2026-11-12)

> **Ambito DECISO dall'utente (2026-11-12): 0.3.0 minima, ~30-40 h.** Dentro: elenco branch (`svn ls ^/branches`, `^/tags`) + **Switch**; **Merge da un branch** con revisioni da unire (`svn mergeinfo --show-revs eligible`) nella tabella del Log, anteprima `--dry-run`, merge vero; **cherry-pick** (`-c REV` sulle revisioni selezionate); conflitti nel pannello Changes esistente; annulla = Revert esistente (con conferma); messaggio di commit precompilato. **Fuori (rimandato):** Crea branch/tag, procedura guidata dedicata al merge (basta un pannello semplice), avanzamento/annullamento per repo lenti, test di integrazione estesi, D10/D11. Lo studio sotto resta come riferimento; le ore di 4.x valgono per l'ambito completo.

Obiettivo: fare da Visual Studio il flusso quotidiano "porto le modifiche tra trunk e branch" senza uscire dall'IDE né aprire TortoiseSVN. Sviluppo in tre blocchi: fondamenta (info, switch, branch/tag), merge, rifinitura (avanzamento, test).

### 4.1 Studio di fattibilità

Tutto si appoggia a `svn.exe` già in uso; nessuna dipendenza nuova. Output da parsare: `--xml` dove esiste (`ls`, `log`, `info`, `status`); testo per `merge` e `mergeinfo` (nessun `--xml`), con `LC_ALL=C` già forzato.

| Funzione | Comando svn | Note di fattibilità |
|---|---|---|
| Elenco branch / tag | `svn ls --xml ^/branches`, `svn ls --xml ^/tags` | Remoto ma leggero; cache per sessione + Aggiorna. Layout da impostazioni (D20) |
| Revisioni da unire | `svn mergeinfo --show-revs eligible SRC TGT` | Server-side, veloce anche su repo grandi; una `rNNN` per riga. Poi `svn log --xml -r` sul sorgente per autore/messaggio, filtrato lato client. Mostrate nella tabella del Log |
| Anteprima | `svn merge --dry-run SRC` | Righe `U/A/D/C/G`; non prevede sempre tree conflict e conflitti di proprietà: dirlo nella UI |
| Merge automatico (svn >= 1.8) | sync: `svn merge ^/trunk`; reintegrazione: `svn merge ^/branches/x` (senza `--reintegrate`, deprecato) | `--non-interactive --accept postpone`; non committa: l'utente rivede e committa |
| Cherry-pick | `svn merge -c N[,M] SRC`; undo di un commit: `svn merge -c -N .` | Dal menu del Log |
| Conflitti | `svn status --xml` (`conflicted`, `tree-conflicted`, `props="conflicted"`) | Pannello Changes già mostra la sezione Conflitti e blocca il commit; serve D10/D11 |
| Messaggio precompilato | — | "Merge r120-r135 da ^/trunk" generato dal merge; modificabile; si può affinare con AI |
| Annulla merge | `svn revert -R .` | Solo se la WC era pulita all'avvio (controllo obbligatorio); conferma con elenco e backup `.patch` in `%LOCALAPPDATA%\SVNAssist\undo` |
| Switch | `svn switch ^/branches/x --accept postpone` | WC pulita consigliata; avviso se sporca |
| Crea branch / tag | `svn copy ^/trunk ^/branches/x -m "..."` (`--parents` se manca la cartella) | Commit server-side: niente dry-run, chiedere conferma |

**Requisiti versione svn:** merge automatico, `mergeinfo --show-revs` e `log --search` richiedono svn >= 1.8 (1.8 è del 2013: praticamente ovunque); la versione si legge con `svn --version --quiet` e le funzioni non supportate si nascondono. I server più vecchi senza mergeinfo server-side rendono lente le `eligible`: mostrare un avanzamento.

**Rischi:**
1. **Mergeinfo su sottocartelle**: merge di una sola cartella crea `svn:mergeinfo` sul sottoalbero e rende i merge successivi più lenti e confusi. Mitigazione: il merge automatico parte sempre dalla radice della WC; se la WC è su una sottocartella, avviso.
2. **Merge di sottoalberi / sparse depth / externals**: fuori scope iniziale, documentato.
3. **Rinomine e tree conflict**: i più frequenti nei merge reali e i più difficili da spiegare; mostrati nel pannello Conflitti con spiegazione testuale, risoluzione solo per scelta esplicita, mai automatica.
4. **WC non pulita o a revisioni miste**: prima del merge richiedere WC pulita e `svn update` (altrimenti "Annulla" porta via lavoro non correlato).
5. **Repo lenti**: `ls`, `mergeinfo`, `log` su rete: ogni operazione con avanzamento e Annulla (kill del processo svn, già supportato dal timeout).
6. **Output testuale** di merge/mergeinfo: parser con test su campioni reali e su repo `file://` (`tools/setup-local-svn.ps1` ha già `branches/feature-demo`).
7. Dopo uno switch VS può aver caricato file della solution diversi: avvisare di ricaricare la solution (nessuna API nota per forzarlo).

### 4.2 Pezzi e stime

| Blocco | Pezzo | Ore |
|---|---|---|
| Fondamenta | F-INFO completo (modello `SvnInfo`) + rilevamento versione svn | 5 |
| | Layout repository personalizzabile (D20) | 2 |
| | **Switch** con branch picker (elenco, ricerca, ultimi usati, check WC sporca) | 14 |
| | **Crea branch / tag** (da WC o revisione, "passa al nuovo") | 8 |
| | D10/D11 (marcatori residui, parser status, tree/props conflict) | 6 |
| Merge | Elenco branch/tag come sorgenti e revisioni **eligible** nella tabella del Log | 8 |
| | Anteprima `--dry-run` con conteggi | 6 |
| | Merge automatico (sync da trunk, reintegrazione) con controllo WC pulita | 8 |
| | Cherry-pick `-c` e "Annulla questa revisione" dal Log | 8 |
| | Conflitti nel pannello Changes (modalità merge, banner Commit / Annulla merge) | 8 |
| | Messaggio di commit precompilato | 2 |
| | Annulla merge (revert con conferma + backup `.patch`) | 3 |
| | Wizard UI (sorgente, anteprima, esecuzione) | 10 |
| Rifinitura | Avanzamento e annullamento per repo lenti | 6 |
| | Test di integrazione (repo locale con conflitto forzato, cherry-pick, annulla) | 8 |
| | Collaudo F5 e documentazione | (non stimato) |
| **Totale** | | **~102 h** |

Ordine consigliato: F-INFO e versione, switch, branch/tag, poi merge (eligible, dry-run, automatico, cherry-pick, conflitti), poi annulla e avanzamento. Scorciatoia a basso rischio se serve una release intermedia: delegare `merge`/`switch` a `TortoiseProc /command:...` (spike 3 h), ma contraddice l'obiettivo di non dipendere da TortoiseSVN.

---

## 5. Candidati 0.4.0 (ordinati per valore/costo)

| # | Candidato | Ore | Motivazione |
|---|---|---|---|
| 1 | Crea / applica patch (F-PATCH) | 7 | Poco costo, abilita undo sicuro del revert e dell'annulla merge |
| 2 | Lock di altri utenti visibili | 4 | Evita sorprese al commit sui binari; poco codice sopra F-LOCK |
| 3 | Lettura sezione `## Commit` di CLAUDE.md per i provider Claude | 3 | Allinea i commit AI alle regole del team come già per Copilot |
| 4 | Elenco modelli AI dinamico | 5 | Elimina l'elenco fisso che invecchia |
| 5 | Blame / Annotate (F-BLAME) | 12 | Funzione molto richiesta, rischio basso, solo `svn blame --xml` |
| 6 | AI: spiega questa revisione | 6 | Riusa il Log: ottimo valore per il costo |
| 7 | AI: cosa sta per arrivare (+ indicatore "n in arrivo") | 10 | Previene conflitti prima dell'Update; richiede `status -u` |
| 8 | Verifica su VS 2022 | 8 | Dichiarata ma mai provata: allarga gli utenti |
| 9 | AI: review pre-commit | 6 | Valore alto ma da fare con consenso esplicito (invia diff) |
| 10 | Changelist (F-CL) | 8 | Utile su commit separati, poi serve UI nell'albero |
| 11 | Autocompletamento messaggi | 6 (solo recenti) / 14 | UX limitata Prima solo i messaggi recenti |
| 12 | Versione VS Code (F-VSCODE) | 16 spike / 60 MVP | Alto costo: prima lo spike con Core condiviso |

Restano in backlog (P2, vedi `docs/FUNZIONALITA.md`): shelve, editor proprietà, repository browser, relocate, assistente AI ai conflitti, firma VSIX e Marketplace.

---

## 6. Domande aperte per il proprietario

1. **Repository reali**: seguono `trunk/branches/tags` alla radice? Una WC per branch o switch sulla stessa WC? (decide la priorità di switch e reintegrazione.)
2. **Autenticazione**: serve altro oltre alla cache svn e all'accesso integrato Windows (certificati client, NTLM)?
3. **Privacy AI**: le funzioni che inviano contenuto di file (review pre-commit, spiega revisione, conflitti) vanno abilitate solo con consenso esplicito?
4. **VS Code vs merge**: confermi merge in 0.3.0 e VS Code solo come spike nella 0.4.0?
