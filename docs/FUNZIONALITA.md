# SVNAssist — Funzionalità: cosa c'è, cosa manca

**Versione:** 0.1.0 · **Data:** 2026-11-10

## Come usare questo file

Questo file è pensato per essere **modificato da te** e restituito per pianificare la 0.2.0.

- **Priorità:** spunta `- [x]` le voci della sezione "Cosa manca" che vuoi nella 0.2.0.
- **Scarta:** cancella la riga oppure barrala (`~~testo~~`) se non ti serve.
- **Commenta:** aggiungi sotto la voce una riga `> nota: ...` (es. `> nota: serve solo il sync da trunk`).
- **Idee nuove:** scrivile nell'ultima sezione, "Le mie richieste".
- Legenda priorità: **P0** = sicurezza/blocca l'uso · **P1** = valore alto · **P2** = nice to have. Le ore sono stime (sviluppo + test automatici, senza collaudo manuale in VS), fonte `docs/ROADMAP.md`; dove non c'è una stima in ROADMAP è marcata `(stima nuova)`.

---

## ✅ Cosa c'è nella 0.1.0

### Pannello SVN Changes
- Finestra "SVN Changes" (Extensions > SVNAssist > Mostra Status / Commit...), stile Git Changes; si apre **fluttuante** (`Placement = Floating`), da agganciare a mano.
- Rileva da sola la working copy dalla solution aperta (cerca `.svn` risalendo le cartelle); header con branch corrente, **Refresh** e **Update**. Limite: se la solution non è in una WC il pannello resta vuoto (c'è il setting "Working Copy Path").
- Sezioni comprimibili **Conflitti / Modifiche / Non versionati** con contatori; vista **Albero** (cartelle compattate) o **Lista**; Espandi/Comprimi tutto.
- Checkbox tri-state su file, cartelle e sezioni: ciò che è spuntato viene committato; Seleziona/Deseleziona tutti.
- Refresh automatico (FileSystemWatcher con debounce). Limite: monitora tutto l'albero, build in `bin/obj` possono causare refresh frequenti su WC grandi (D12 in ROADMAP).
- Pannello **diff inline** per file e per cartella; con TortoiseSVN installato il diff di file si apre in TortoiseSVN.

### Operazioni SVN
- **Commit** dei soli elementi spuntati, come TortoiseSVN (non versionati spuntati aggiunti prima, mancanti cancellati, cartelle a `--depth empty`); mostra la revisione creata; **bloccato** se ci sono conflitti selezionati.
- **Add**, **Delete** (con conferma), **Revert** (con conferma e elenco di cosa cambia), **Add to ignore** (`svn:ignore`) dal menu contestuale del pannello.
- **Risolvi conflitto**: scelta Working / Mine-full / Theirs-full / Base; i conflitti ad albero chiedono solo conferma di "segnato come risolto". Limite: nessun editor di merge, si sceglie solo una versione intera; con "Working" il file non deve più contenere i marcatori `<<<<<<<`.
- **Update** della working copy (pulsante in header): mostra revisione raggiunta ed elenco dei conflitti. Limite: aggiorna sempre l'intera WC (o la cartella, vedi sotto); niente update a revisione, niente switch, niente merge.
- Il parser dello status riconosce anche tree-conflict (marcati nella riga).

### Tasto destro in Esplora soluzioni
- Sottomenu **SVN** su file, cartella, progetto e solution (`Commands/SolutionExplorerMenu.cs`): **Update, Commit..., Diff con BASE, Log, Add, Revert, Aggiungi a ignore list**.
- Limite: selezione singola (un solo elemento per volta); **Update su un file aggiorna la cartella che lo contiene**; "Log" e "Commit..." aprono solo le finestre generali (non filtrate sul percorso); il menu è sempre visibile, anche fuori da una WC (in quel caso mostra un errore).
- Limite: non ancora collaudato in un VS reale a questo release (vedi "Limiti noti").

### AI per i commit
- **Genera con AI** e **Migliora** il messaggio, partendo dal diff **minimo dei soli file spuntati** (niente contesto, file nuovi come diff sintetico, binari omessi, diff grandi spezzati).
- **Lingua del commit** scelta con un combo nel pannello (Italiano / English / Auto), predefinita dall'impostazione "Default Language".
- Provider: `auto` (default, prova in ordine Copilot > Claude Code > Claude API > OpenAI-compatibile, con fallback se uno fallisce), `copilot`, `claudecode`, `claude`, `openai` (GitHub Models, OpenAI, Azure, Ollama, Gemini...).
- Token GitHub rilevato in automatico (`gh auth token`, `GITHUB_TOKEN`, `GH_TOKEN`) e inviato solo a GitHub Models.
- Limiti: **Copilot scarica un runtime di ~100 MB al primo uso** (SHA-256 verificato, ~200 MB su disco in `%LOCALAPPDATA%\SVNAssist\copilot-runtime`); in `auto` Copilot è usato solo se il runtime è già presente; il modello Claude di default è `claude-sonnet-5-5` (elenco: Sonnet, Haiku, Opus); la risposta AI è corta (`max_tokens=200` sul provider OpenAI-compatibile).

### Prerequisiti e installazione
- **Checklist dei prerequisiti** dentro il pannello: svn (obbligatorio), TortoiseSVN (opzionale), provider AI (ne basta uno), ciascuno con stato e istruzioni.
- Pulsanti a un clic che aprono una **console** con il comando giusto: installa svn (winget `Slik.Subversion`), installa GitHub CLI, login GitHub, installa Claude Code, login Claude.
- Prompt "svn.exe non trovato" con installazione guidata (una volta per sessione); ricerca di `svn.exe` / `TortoiseProc.exe` in PATH e cartelle note.
- Limite: la console resta aperta e dopo l'installazione serve un refresh della checklist (il PATH del processo VS può non vedere subito il nuovo programma).

### Impostazioni (Tools > Options > SVNAssist)
- **AI Provider** (tendina), **Default Language** (tendina), Claude Model, Copilot Model, AI Model, AI Endpoint, AI API Key, Anthropic API Key, **Working Copy Path**.
- Limite: le API key sono salvate **in chiaro** nello store impostazioni di VS (D17, cifratura non fatta).

### Log / History
- Finestra "SVN Log" (Extensions > SVNAssist > Log / History): ultime 50 revisioni, **Carica altre 50**, diff per revisione nel pannello inferiore.
- Limite: senza elenco dei file modificati per revisione (`-v`), senza filtri/ricerca, e il percorso della WC va inserito a mano (D13).

### Sicurezza e affidabilità
- Tutti i comandi svn con `--non-interactive` (niente prompt di credenziali che bloccano), **timeout di 5 minuti** per invocazione, output forzato in inglese (`LC_ALL=C`) per un parsing stabile, argomenti passati senza escaping manuale, liste file via `--targets`, peg revision gestita (file con `@` nel nome), nomi con accenti.
- Revert e Delete sempre con conferma; Commit bloccato in presenza di conflitti.
- Il pacchetto Copilot scaricato è verificato con SHA-256; il token GitHub non esce verso endpoint di terzi.
- Test automatici xUnit (unit con runner finto + integrazione su repo `file://` creato da `tools/setup-local-svn.ps1`; serve `svn`/`svnadmin` nel PATH).

---

## ❌ Cosa manca

Formato: `- [ ] **Nome** — descrizione · Priorità · ~ore`. Spunta `[x]` per la 0.2.0.
Le ore di F-MERGE sono quelle del piano (~50 h totali, ripartite sotto).

### Merge e conflitti
- [ ] **Sync da trunk al branch** — merge di `^/trunk` nella WC del branch, senza commit automatico, con messaggio precompilato · P1 · ~12 h (insieme al dry-run)
- [ ] **Dry-run / anteprima merge** — `svn merge --dry-run` con conteggi (aggiornati/aggiunti/in conflitto) prima di eseguire · P1 · incluso nelle 12 h del sync
- [ ] **Revisioni "eligible" via mergeinfo** — elenco delle revisioni ancora da portare (`svn mergeinfo --show-revs eligible`) · P1 · incluso in sync/cherry-pick
- [ ] **Cherry-pick di singole revisioni** — merge di `-c N` scelte dal Log; "Annulla questa revisione" (merge inverso) · P1 · ~10 h
- [ ] **Reintegrate branch → trunk** — merge di un branch su trunk con controllo che sia allineato (senza `--reintegrate`, deprecato) · P2 · ~8 h
- [ ] **Risoluzione conflitti con interfaccia** — scelta mine/theirs per file con 3 azioni + editor 3-way esterno (TortoiseMerge/`conflicteditor`), bloccando il commit finché restano conflitti · P1 · ~12 h
- [ ] **Abort merge** — annulla un merge in corso (`svn revert -R` con controllo WC pulita e backup `.patch`) · P1 · ~3 h
- [ ] **Scorciatoia TortoiseSVN per merge/switch** — lanciare `TortoiseProc /command:merge|switch` in attesa della UI nativa · P2 · ~3 h (spike)
- [ ] **Resolve più sicuro** — avviso se restano i marcatori `<<<<<<<` anche in casi non coperti, supporto a `props="conflicted"` e altri stati (ignored/external/obstructed) · P1 · ~3 h (stima nuova: parte di D10/D11)

### Branch / Switch / Tag
- [ ] **Servizio `svn info --xml`** — URL, root, revisione, lock, conflitti (prerequisito di switch/merge, sostituisce il nome branch "unknown" su errore) · P0 · ~3 h
- [ ] **Switch con branch picker** — elenco da `^/branches`, `^/tags` e trunk, ricerca, ultimi usati; controllo WC sporca prima dello switch · P1 · ~14 h
- [ ] **Crea branch / tag** — `svn copy` server-side da WC o da revisione, con opzione "passa al nuovo branch" · P1 · ~8 h
- [ ] **Layout repository personalizzabile** — impostazioni per `TrunkPath` / `BranchesPath` / `TagsPath` se non è `trunk/branches/tags` alla radice · P1 · (stima nuova) ~2 h

### Storia
- [ ] **Blame / Annotate** — chi/quando/quale revisione per ogni riga; tool window · P1 · ~12 h (margine nell'editor: P2, +14 h)
- [ ] **Log più ricco** — elenco file cambiati per revisione (`-v`), paginazione, auto-detect della WC, azioni per revisione · P1 · ~12 h
- [ ] **Ricerca nel log** — filtri per autore/testo/path/date (`--search`) · P1 · incluso nelle 12 h del Log
- [ ] **Confronto tra revisioni** — diff tra due revisioni scelte (`svn diff -r N:M`) · P2 · (stima nuova) ~4 h

### Manutenzione
- [ ] **Cleanup** — `svn cleanup` con opzioni (rimuovi non versionati/ignorati, vacuum), conferme e suggerimento automatico su `E155004` · P1 · ~3 h
- [ ] **Lock / Unlock** — blocco file (utile per binari), visibilità di chi tiene il lock · P2 · ~6 h (P1 se hai asset binari)
- [ ] **Update a revisione / Update mirato** — update di file/cartella e "Update to revision..." con riepilogo · P1 · ~6 h
- [ ] **Relocate** — cambio URL della WC quando il server cambia indirizzo · P2 · ~3 h
- [ ] **Changelist** — raggruppare modifiche in liste nominate e committarle separatamente · P2 · ~8 h
- [ ] **Crea / applica patch** — esporta in `.patch` i file selezionati e applica una patch (`svn patch`, con dry-run); abilita backup/undo sicuro · P1 · ~7 h
- [ ] **Shelve / stash** — mette da parte le modifiche (via patch + revert), ripristino con `svn patch` · P2 · ~8 h
- [ ] **Editor proprietà SVN** — vedi/modifica `svn:ignore`, `externals`, `keywords`, `eol-style`, `needs-lock` · P2 · ~12 h
- [ ] **Repository browser** — naviga l'URL del repo senza checkout, checkout/export di cartelle · P2 · ~16 h

### Interfaccia
- [ ] **Diff integrato in VS senza TortoiseSVN** — diff usabile con il solo `svn.exe` (diff viewer di VS o diff colorato inline) · P1 · ~6 h
- [ ] **Autocompletamento nel messaggio di commit** — file modificati, simboli, messaggi recenti, riferimenti ticket · P2 · ~14 h (~6 h solo "messaggi recenti")
- [ ] **Scorciatoie da tastiera** — tasti per commit, update, refresh, genera AI · P2 · (stima nuova) ~3 h
- [ ] **Posizionamento agganciato accanto a Git Changes** — oggi la finestra è fluttuante, serve un posizionamento predefinito agganciato (se l'SDK lo permette) · P1 · (stima nuova) ~3 h
- [ ] **Incoming (modifiche in arrivo)** — indicatore "n file in arrivo" nell'header (`svn status -u`) · P1 · (stima nuova) ~4 h
- [ ] **Backup `.patch` automatico prima del Revert** — undo del revert · P1 · incluso in "Crea/applica patch"
- [ ] **Icone vettoriali e colori del tema VS** — al posto delle emoji, tema chiaro/scuro coerente · P2 · (stima nuova) ~4 h

### AI
- [ ] **Spiega questa revisione** — intento e rischi di una revisione, dal Log · P1 · ~6 h
- [ ] **Riassunto delle modifiche in arrivo** — "cosa sta per arrivare" prima dell'Update · P1 · ~6 h
- [ ] **Assistente AI ai conflitti** — propone il file unito, applica solo con conferma, mai `resolve` automatico · P2 · ~14 h
- [ ] **Pre-commit check AI** — avvisa su file di debug, segreti, TODO nel diff · P2 · ~6 h
- [ ] **Template messaggio / conventional commits** — formati configurabili (es. `feat: ...`, prefisso ticket) · P2 · (stima nuova) ~4 h
- [ ] **Limiti AI per provider** — soglie token e `max_tokens` per provider (oggi tarati sul più stretto) · P1 · ~3 h
- [x] **Modello Claude di default più economico** — ora Sonnet (`claude-sonnet-5-5`), scelta da elenco
- [ ] **Cifratura delle API key** — DPAPI/Credential Manager al posto del testo in chiaro · P1 · ~4 h

### Piattaforma
- [ ] **Versione Visual Studio Code** — stessa esperienza con AI via `vscode.lm`; prima uno spike con estrazione di un Core condiviso · P1 (spike), P2 (resto) · spike ~16 h, MVP ~60 h
- [ ] **VSIX firmato con certificato** — firma del pacchetto per distribuzione senza avvisi · P1 · (stima nuova) ~3 h + costo certificato
- [ ] **Pubblicazione su Marketplace** — pagina, icona, descrizioni, pipeline di rilascio · P2 · (stima nuova) ~6 h

### Qualità
- [ ] **Test UI in un VS reale** — checklist manuale e, se fattibile, test automatici di integrazione (modello out-of-process) · P1 · ~10 h (test ViewModel inclusi)
- [ ] **Pipeline CI** — build e test automatici ad ogni push · P1 · (stima nuova) ~4 h
- [ ] **Refresh più leggero** — un solo loop con debounce, ignorare `bin/obj/.vs`, `info` solo al cambio WC · P1 · ~4 h
- [ ] **Aggiornamento pacchetti di test** — xunit/Test.Sdk/coverlet alle ultime stabili · P2 · ~1 h

---

## 🐞 Limiti noti della 0.1.0

- **Mai provata su un Visual Studio reale all'atto del rilascio**: tool window, Remote UI, menu di Esplora soluzioni, watcher e DI sono verificati solo da build e test automatici. Previsto un collaudo F5.
- Il pannello **non si aggancia da solo** accanto a Git Changes: si apre fluttuante.
- Menu di Esplora soluzioni: **selezione singola**, Update su file = update della cartella, Log/Commit non filtrati sul percorso, voce sempre visibile anche fuori da una WC.
- Nessun **merge, switch, branch/tag, blame, cleanup, lock, changelist, patch, shelve**.
- **Conflitti**: si sceglie una versione intera (Working/Mine/Theirs/Base); nessun editor 3-way integrato.
- **Update** sempre a HEAD; niente update a revisione, niente anteprima di cosa arriva.
- **Diff**: senza TortoiseSVN è testo unificato non colorato; il diff di un file non versionato è vuoto.
- **Log** senza file per revisione, filtri o ricerca; percorso WC da inserire a mano nella finestra Log.
- **Copilot**: runtime di ~100 MB da scaricare al primo uso, nessuna barra di progresso verificata; con `auto` non viene scaricato da solo.
- **API key in chiaro** nello store impostazioni di VS.
- **Autenticazione di rete**: nessuna UI di login SVN; con `--non-interactive` servono credenziali già in cache (svn/TortoiseSVN), altrimenti il comando fallisce.
- Layout repository assunto `trunk/branches/tags` per le funzioni future; il nome del branch mostra `unknown` se `svn info` fallisce.
- Refresh automatico su WC molto grandi può essere pesante (watcher ricorsivo, 2 processi per refresh).
- Le proprietà multilinea (es. `svn:ignore` con più voci) sono passate come argomento: mai provate a fondo su svn reale.
- Solo VS 2026 (modello out-of-process 17.14); nessuna versione VS Code.

---

## 📝 Le mie richieste

Scrivi qui nuove idee, bug visti usando l'estensione, cose da togliere. Una riga per richiesta; aggiungi `> nota:` per i dettagli.

- [ ]
- [ ]
- [ ]
