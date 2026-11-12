# SVNAssist — Funzionalità: cosa c'è, cosa manca

**Versione:** 0.2.0 · **Data:** 2026-11-12

## Come usare questo file

Questo file è pensato per essere **modificato da te** e restituito per pianificare la 0.3.0 e la 0.4.0.

- **Priorità:** spunta `- [x]` le voci della sezione "Cosa manca" che vuoi nella prossima release.
- **Scarta:** cancella la riga oppure barrala (`~~testo~~`) se non ti serve.
- **Commenta:** aggiungi sotto la voce una riga `> nota: ...`.
- **Idee nuove:** scrivile nell'ultima sezione, "Le mie richieste".
- Legenda: **P0** = sicurezza/blocca l'uso · **P1** = valore alto · **P2** = nice to have. Le ore sono stime (sviluppo + test automatici, senza collaudo manuale in VS); dettagli in `docs/ROADMAP.md`.

---

## ✅ Cosa c'è nella 0.2.0

### Architettura
- Finestre in **WebView2** (stesso design della demo) dentro VS; logica SVN e AI in un **backend .NET 10** separato (JSON-RPC): se cade, VS no. Host net472 in-process. Dettagli in `DESIGN.md`.
- Richiede VS 2026 (18.x) e .NET 10 Runtime (proposto in automatico se manca). VS 2022 dichiarata, non provata.
- Temi chiaro / scuro / blu / alto contrasto con cambio a caldo; lingua **Italiano / English** a caldo.

### Pannello SVN Changes
- Stile Git Changes: albero con tastiera (frecce, Spazio, Invio, Canc con conferma), filtro, vista Albero/Lista, sezioni **Staged / Modifiche / Non versionati / Conflitti** con contatori.
- **Commit All** se Staged è vuoto, **Commit Staged (n)** altrimenti; niente auto-stage; mostra la revisione creata; bloccato con conflitti.
- Add, Delete, Revert (con conferma ed elenco; anche **"SVN Revert di tutto…"** dal tasto destro sull'intestazione delle sezioni Changes e Staged, anche su piu' cartelle, non versionati esclusi), Add to Ignore List, Risolvi conflitto (Working / Mine-full / Theirs-full / Base; tree-conflict con conferma).
- **Sezione Conflitti** con pulsanti per file: editor di merge a 3 vie (vsDiffMerge di VS, poi TortoiseMerge, poi il file con i marcatori), Usa la mia, Usa la loro, Segna come risolto; riquadro ampio, comprimibile, che scorre con molti conflitti; **conflitti ad albero raggruppati** in "Conflitti ad albero (N)" con **Segna tutti come risolti** (conferma con elenco; un errore non blocca gli altri) o uno per uno; file di appoggio di svn raggruppati sotto il file (non tra i non versionati); dopo merge, update e switch con conflitti Changes va in primo piano, senza dialoghi.
- **Barra di stato con la revisione** (dal collaudo aziendale): i messaggi di successo spariscono dopo ~5 s e resta `r1234`, verde se è l'ultima del ramo, arancio ("disponibile r1240 — SVN Update", un clic aggiorna) se altri hanno committato, neutro se non si sa. Controllo periodico leggero (`svn info --show-item last-changed-revision`, solo lettura, non interattivo) ogni N minuti, impostazione in Tools > Options (0 = spento).
- **Pannello "Recenti"** accanto al messaggio di commit (ultimi 25 per repository): largo quanto la finestra, con ricerca, prima riga in grassetto + anteprima, tastiera; i messaggi dell'AI arrivano senza blocchi di codice markdown, **messaggio del merge nel formato TortoiseSVN, aggiunto in cima ai Recenti a fine merge (non nel campo)** (`Unite le revisioni 4-6 da branches/x:` + una sezione `........` / `rN | autore | messaggio` per revisione), doppio clic su un file nuovo o non versionato = lo apre intero nell'editor (come Git), stato vuoto sotto le sezioni.
- **Merge e conflitti senza dialoghi**: dopo merge/update/switch con conflitti la finestra Changes viene portata in primo piano (niente "risolvere ora / piu' tardi"). Nella vista Merge del Log l'avviso "working copy con modifiche locali" ha **SVN Revert di tutto…** (conferma con elenco, poi il merge si rifa) e **Vai a Changes**; l'errore di revisioni miste (e in Changes ogni errore che suggerisce l'update) ha **SVN Update ora**.
- **Switch / Merge su repository grandi**: selettore veloce (ultimi usati per repository, ricerca, al massimo 50 righe) e vista estesa a tutta finestra (tabella con revisione, autore, data; filtri per nome, tipo, autore e date con preset 7/30/90 giorni; ordine per nome, rev., autore, data; contatore, "Azzera filtri"; scorrimento virtuale per 500+ rami).
- **Update**, **Update to revision…** (con elenco revisioni), **Cleanup** (anche come pulsante quando la WC è bloccata), **Lock / Unlock** (conferma per rubare il lock, lucchetto sui file tuoi).
- Rilevamento automatico della WC dalla solution, refresh con debounce; "Working Copy Path" nelle impostazioni ha la precedenza.
- **Login SVN** solo se le credenziali in cache mancano; cache standard di Subversion (condivisa con TortoiseSVN); con accesso integrato Windows non compare.
- Feedback visibile su ogni azione; "Operazione in corso…" se se ne lancia una seconda.

### Log e diff
- **Show Log come documento** nell'area editor: tabella revisioni, filtri autore/testo/percorso/date, file cambiati per revisione, confronto tra due revisioni (Ctrl+clic). Anche per file e cartelle da Esplora soluzioni.
- **Diff nativo di VS** ovunque (WC, revisioni, cartelle); TortoiseSVN non serve più.

### Esplora soluzioni
- Nessun menu SVN in Solution Explorer (tolto perche' ridondante con la finestra Changes).
- **Annulla il blocco** dal pannello destro del diff: pulsante con icona annulla (KnownMonikers.Undo) a sinistra di ogni blocco modificato, come Git; clic = riporta a BASE solo quel blocco, senza dialoghi (messaggio di stato in Changes). Resta **SVN: Revert Selected Changes** nel menu contestuale (righe selezionate o riga del cursore). Il documento viene salvato prima, niente Ctrl+Z. Mancante: il pulsante nell'editor normale e nel diff inline.
- **Next/Previous Change in Diff** (Alt+F8 / Shift+Alt+F8, menu contestuale, Extensions > SVNAssist): nel diff sposta il cursore e centra la modifica successiva/precedente (viewer nativo, con giro); in un editor normale di un file modificato salta al blocco successivo/precedente del svn diff (letto da disco: righe non salvate possono slittare). Ctrl+Alt+Giù/Su e' di VS (elenco documenti).

### AI per i commit
- **Procedura guidata** (provider, prerequisiti, login, modello) riapribile da "Change AI Provider...": GitHub Copilot, Claude Code, Claude API, GitHub Models.
- **Genera con AI** / **Migliora** dal diff minimo dei soli file in Staged (aggiunti/eliminati/vuoti/binari citati, diff compattato con molti file); lingua del commit selezionabile.
- Segue le regole di commit in `.github/copilot-instructions.md`, `.github/instructions/*.instructions.md`, `~/.copilot/copilot-instructions.md`.
- Modelli da **tendina** (elenco fisso); default Claude: Sonnet. API key **cifrate con DPAPI**.

### Qualità e distribuzione
- Test automatici (xUnit con runner finto + integrazione su repo `file://`, test UI web); release da GitHub Actions (VSIX + demo interattiva).
- Comandi svn `--non-interactive`, con timeout, `LC_ALL=C`, `--targets`, peg revision, accenti.

---

## ❌ Cosa manca

Formato: `- [ ] **Nome** — descrizione · Priorità · ~ore`. Spunta `[x]` per la release che lo vuole.
Le voci del merge sono dettagliate nel piano 0.3.0 di `docs/ROADMAP.md` (~100 h totali).

### Candidati 0.3.0 — Merge da Visual Studio (e prerequisiti)
- [ ] **`svn info --xml` completo + versione svn** — URL, root, revisione, lock, conflitti; `svn --version` per nascondere funzioni non supportate · P0 · ~5 h
- [ ] **D10/D11: marcatori residui e parser status** — avviso se restano `<<<<<<<`, `props="conflicted"`, ignored/external · P1 · ~6 h
- [ ] **Layout repository personalizzabile** — `TrunkPath` / `BranchesPath` / `TagsPath` se non standard · P1 · ~2 h
- [ ] **Switch con branch picker** — da `^/branches`, `^/tags`, trunk; ricerca, ultimi usati; controllo WC sporca · P1 · ~14 h
- [ ] **Crea branch / tag** — `svn copy` server-side da WC o revisione, opzione "passa al nuovo" · P1 · ~8 h
- [ ] **Revisioni eligible nel Log** — `svn mergeinfo --show-revs eligible` nella tabella · P1 · ~8 h
- [ ] **Anteprima merge (dry-run)** — conteggi ed elenco prima di eseguire · P1 · ~6 h
- [ ] **Merge automatico** — sync trunk → branch e reintegrazione branch → trunk · P1 · ~8 h
- [ ] **Cherry-pick (`-c`) e "Annulla questa revisione"** — dal Log · P1 · ~8 h
- [ ] **Conflitti di merge nel pannello Changes** — tree conflict e props inclusi, commit bloccato · P1 · ~8 h
- [ ] **Messaggio di commit precompilato** — "Merge r..-r.. da ..." · P1 · ~2 h
- [ ] **Annulla merge** — revert della WC con conferma e backup `.patch` · P1 · ~3 h
- [ ] **Avanzamento e annullamento per operazioni lente** — barra di progresso, Annulla · P1 · ~6 h
- [ ] **Wizard UI del merge** — sorgente, anteprima, esecuzione · P1 · ~10 h
- [ ] **Test di integrazione merge** — repo locale con conflitto forzato · P1 · ~8 h

### Candidati 0.4.0 (ordinati per valore/costo)
- [ ] **Blame / Annotate** — chi/quando/revisione per riga · P1 · ~12 h
- [ ] **Crea / applica patch** — export dei selezionati, `svn patch`; abilita undo sicuro · P1 · ~7 h
- [ ] **Lock di altri utenti visibili** — chi tiene il lock (`status -u`) · P1 · ~4 h
- [ ] **AI: spiega questa revisione** — intento e rischi dal Log · P1 · ~6 h
- [ ] **AI: cosa sta per arrivare** — riassunto delle modifiche in arrivo prima dell'Update (+ indicatore "n file in arrivo") · P1 · ~10 h
- [ ] **Elenco modelli AI dinamico** — scaricato dal provider invece che fisso · P1 · ~5 h
- [ ] **Lettura sezione `## Commit` di CLAUDE.md** per i provider Claude · P1 · ~3 h
- [ ] **Verifica su VS 2022** — collaudo e fix di compatibilità · P1 · ~8 h
- [ ] **AI: review pre-commit** — file di debug, segreti, TODO nel diff · P2 · ~6 h
- [ ] **Changelist** — liste nominate committabili separatamente · P2 · ~8 h
- [ ] **Autocompletamento messaggi** — messaggi recenti, file, ticket · P2 · ~14 h (~6 h solo recenti)
- [ ] **Versione VS Code** — spike con Core condiviso · P2 · spike ~16 h, MVP ~60 h

### Altro (non pianificato)
- [ ] **Selezione multipla in Esplora soluzioni** — oggi un solo elemento · P2 · ~4 h
- [ ] **Shelve / stash** (via patch) · P2 · ~8 h
- [ ] **Editor proprietà SVN** — `svn:ignore` multilinea, externals, keywords, needs-lock · P2 · ~12 h
- [ ] **Repository browser** · P2 · ~16 h
- [ ] **Relocate** · P2 · ~3 h
- [ ] **Assistente AI ai conflitti** — propone il file unito, applica solo con conferma · P2 · ~14 h
- [ ] **Editor 3-way per conflitti** (TortoiseMerge/esterno) · P2 · ~8 h
- [ ] **Limiti AI per provider** — soglie token/`max_tokens` per provider · P1 · ~3 h
- [ ] **VSIX firmato e pubblicazione su Marketplace** · P2 · ~9 h + costo certificato
- [ ] **Aggiornamento pacchetti di test** · P2 · ~1 h

---

## 🐞 Limiti noti della 0.2.0

- Nessun **merge, switch, branch/tag, blame, changelist, patch, shelve** (merge/switch/branch pianificati per la 0.3.0).
- **Conflitti**: si sceglie una versione intera; nessun editor di merge. Manca "Usa la mia / Usa la loro per tutti" sui conflitti di testo (scarta una delle due parti: per ora solo un file alla volta).
- **Update** non mostra in anteprima cosa arriva; nessun indicatore di modifiche in arrivo.
- Menu di Esplora soluzioni: selezione singola; Update su un file aggiorna la cartella.
- Elenco modelli AI fisso (l'elenco Copilot è una stima).
- **Copilot**: runtime di ~100 MB da scaricare al primo uso.
- Layout repository assunto `trunk/branches/tags`; senza questo layout l'header mostra "/ (radice)".
- Refresh su WC molto grandi può essere pesante.
- Solo VS 2026 provata; VS 2022 non verificata; nessuna versione VS Code.

---

## 📝 Le mie richieste

Scrivi qui nuove idee, bug visti usando l'estensione, cose da togliere. Una riga per richiesta; aggiungi `> nota:` per i dettagli.

- [ ]
- [ ]
- [ ]
