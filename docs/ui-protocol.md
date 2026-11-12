# Protocollo UI ⇄ host (v1)

La UI di SVNAssist è una pagina web (`src/SVNAssist.UI.Web`) ospitata in WebView2. Stessa pagina per l'estensione e per la demo:
nella demo l'host è `docs/demo/src/mock-bridge.js`, nell'estensione è `SVNAssist.Host` che fa da relay verso il backend.
La UI è **senza stato di dominio**: riceve snapshot completi e invia comandi. Gli schemi sono in
`src/SVNAssist.UI.Web/contract/*.schema.json`, le fixture in `contract/fixtures/`, i test in `tests/ui-web/` (`node --test "tests/ui-web/*.test.mjs"`).

## Trasporto

| Direzione | Come |
|---|---|
| JS → host | `window.chrome.webview.postMessage(obj)` (host: `WebMessageAsJson`) |
| host → JS | `CoreWebView2.PostWebMessageAsJson(json)` → evento `message` (`e.data` è l'oggetto) |

Ogni messaggio ha `v: 1`. L'host deve controllare `Source` in `WebMessageReceived` (solo `https://svnassist.ui/…`).
La UI non invia **mai percorsi** del filesystem (eccetto il campo "working copy" digitato dall'utente): usa solo le *chiavi di riga* (sotto).

Una pagina = una vista: `index.html?view=changes|log|diff` (`&id=…` per distinguere più istanze della vista `diff`).

### Messaggi (`envelope.schema.json`)

| Tipo | Direzione | Campi |
|---|---|---|
| `ready` | JS → host | `view`, `id?` — la pagina è pronta; l'host risponde con `theme` e `state` |
| `cmd` | JS → host | `id` (progressivo), `name`, `args` — vedi comandi |
| `state` | host → JS | `view`, `id?`, `lang` (`it`/`en`), `rev` (cresce), `state` — **snapshot completo** della vista |
| `reply` | host → JS | `id`, `ok`, `result?`, `error?` — esito di un `cmd` (se `ok:false` la UI mostra un toast) |
| `theme` | host → JS | `kind` (`light`/`dark`/`blue`), `vars` — variabili CSS che sovrascrivono `tokens.css` |
| `event` | host → JS | `name`: `toast` (`text`), `focus`, `backendRestarted` |

Regole: l'host manda `state` ad ogni cambiamento (con coalescenza ~16 ms); non esistono patch in v1. Se due `cmd` si sovrappongono, vince l'ordine di arrivo.
Il testo del messaggio di commit è tenuto dalla UI e inviato con `setCommitText` (debounce 150 ms) e prima di `commit`; l'host non deve riscriverlo mentre l'utente digita (la UI ignora `commit.text` se la textarea ha il focus).

## Chiavi di riga

`"<sezione>:<percorso/con/slash>"` con sezione `staged | changes | unversioned`, percorso relativo alla working copy (`/` come separatore).
Vale per file e cartelle (la cartella ha il percorso della cartella; le catene a figlio unico sono compattate: `Core/Services`).
`"section:<id>"` indica l'intera sezione. L'host risolve la chiave e **rivalida** che esista ancora nella working copy.

## Comandi (`commands.schema.json`)

### Changes
| Comando | Args | Effetto |
|---|---|---|
| `refresh` | – | `svn status` |
| `update` | – | `svn update` |
| `updateToRevision` | – | **SVN Update to revision**: apre in Changes la scelta guidata (`revPicker.open`): le ultime revisioni del repository (`svn log`, 30 alla volta), `HEAD` in cima, campo "oppure numero". Da Solution Explorer parte lo stesso su quei percorsi (l'host mostra prima Changes) |
| `revPickerMore` | – | "Carica altre": altre 30 revisioni |
| `revPickerCancel` | – | chiude la scelta senza aggiornare |
| `revPickerOk` | `rev` | intero positivo o `HEAD`: chiude ed esegue `svn update -r N` (esito come l'update: revisione, conflitti). Se non valido la scelta resta aperta con `revPicker.error` |
| `switchOpen` | – | **SVN Switch...**: apre il selettore (`switchPicker.open`) con trunk, branch e tag. Il backend legge `svn info --xml` sulla radice della working copy, deduce la **base** e il **ramo corrente** dall'URL con il layout (`repoLayout`, default `trunk/branches/tags`) e fa `svn list --xml` su `<base>/branches` e `<base>/tags` (e sulla base, per sapere se esiste `trunk`). Un percorso inesistente = elenco vuoto, non errore. Gli elenchi stanno in cache per la sessione. Anche da Extensions > SVNAssist > Switch... |
| `switchRefresh` | – | "Aggiorna": scarta la cache e rilegge branch e tag dal server (le tre liste `svn list --xml` partono in parallelo) |
| `switchCancel` | – | chiude il selettore |
| `switchOk` | `name` | `name` = voce dell'elenco (`branches/x`, `tags/1.0`, `trunk`); l'URL lo costruisce il backend dall'elenco (radice del repository + percorso codificato), mai dalla UI: un nome non in elenco = errore nel selettore. Il ramo corrente si rifiuta. Con modifiche locali ai file versionati chiede conferma (dialogo nativo; annullare lascia il selettore aperto). Poi `svn switch --accept postpone --non-interactive <URL>` dalla radice della working copy, refresh dello stato e messaggio "Switch a X completato (revisione N). Se Visual Studio lo chiede, ricarica la solution." I conflitti compaiono in Modifiche come dopo un update |
| `mergeOpen` | – | **SVN Merge...**: apre lo stesso selettore di Switch in modo `merge` (`switchPicker.mode = "merge"`), con trunk, branch e tag **escluso il ramo corrente**. `switchOk{name}` in questo modo non fa uno switch: chiude il selettore, passa il **Log** in modalita' merge sulla sorgente scelta (vedi "Log: modalita' merge") e lo apre come documento (`IHost.ShowLogWindowAsync`, titolo "Merge da branches/x"). Anche da Extensions > SVNAssist > Merge... e dal menu "Altre azioni" (nascosto con svn < 1.8) |
| `mergeUndo` | – | **Annulla merge** (pulsante del banner): `svn revert --depth infinity` della radice della working copy, con conferma che elenca i file. Il messaggio del merge resta nei Recenti (non si ritira), il banner sparisce e il Log in modalita' merge rilegge le revisioni da unire |
| `mergeDismiss` | – | nasconde il banner del merge senza annullarlo |
| `conflictOp` | `op`, `key` | **Sezione Conflitti**: azione su un file in conflitto (`key` = `conflicts[].key`). `edit` = editor di merge a 3 vie (`vsDiffMerge.exe theirs mine base merged /m` di VS, altrimenti `TortoiseMerge.exe /base /theirs /mine /merged`, altrimenti il file con i marcatori nell'editor di VS; solo conflitti di testo); `mine` / `theirs` = `svn resolve --accept mine-full` / `theirs-full` **con conferma** (scartano l'altra parte); `resolved` = `svn resolve --accept working` (rifiutato se restano i marcatori; per un conflitto ad albero chiede conferma). Un conflitto ad albero ammette solo `resolved`. Il menu contestuale della riga (`rowOp resolve`, scelta in un dialogo) resta |
| `rowOp` su una **sezione** | `op: revert`, `key: section:changes\|section:staged` | **SVN Revert di tutto…** (menu contestuale dell'intestazione "Changes" o "Staged", non per i Non versionati): annulla tutte le modifiche della sezione, anche in piu' cartelle, con conferma che elenca i file (conferma negata = nessun effetto) e gli stessi percorsi espliciti del Revert di file/cartella; i non versionati non si toccano. Sulle cartelle il menu con Revert c'era gia' |
| `showLog` | – | apre il Log della working copy (`IHost.ShowLogAsync`, come Extensions > Log) |
| `cleanup` | – | **SVN Cleanup** della working copy (`svn cleanup`). Compare anche come pulsante "Esegui Cleanup" accanto a un errore di working copy bloccata (`status.action`) |
| `commit` | – | come Git: se **Staged** ha file, commit dei soli quelli (pulsante "Commit Staged (n)"); se è vuoto, commit di **tutte** le modifiche dei file versionati M/A/D/R/mancanti (pulsante "Commit All", come `git commit -a`; i non versionati non entrano). Gli aggiunti non versionati in Staged si aggiungono, i mancanti si eliminano; i conflitti bloccano |
| `setCommitText` | `text` | aggiorna il messaggio |
| `resolveTreeConflicts` | – | **Segna tutti come risolti** (gruppo "Conflitti ad albero (N)" della sezione Conflitti): conferma con l'elenco dei file, poi `svn resolve --accept working` su ognuno dei conflitti ad albero. Un errore su uno non ferma gli altri: il messaggio di stato riporta quanti sono riusciti e quali sono falliti (con il motivo). I conflitti di testo e di proprieta' non vengono toccati |
| `useRecentMessage` | `i` | pannello **Recenti** (come TortoiseSVN, largo quanto la finestra; solo UI): campo di ricerca in cima (contiene, senza maiuscole, su tutto il messaggio), per ogni voce la prima riga in grassetto + anteprima di 3 righe (messaggio intero sulla voce selezionata o sotto il mouse, e nel tooltip), altezza massima ~60% della finestra con scorrimento, frecce / PgSu / PgGiu / Invio / Esc. Inserisce nel campo `commit.recent[i]` (`i` e' l'indice nell'elenco intero, anche con la ricerca attiva; si rivalida contro l'elenco corrente). Il backend ripulisce i messaggi (`AiServiceBase.CleanAnswer`: toglie i blocchi di codice markdown e le virgolette che avvolgono tutto, anche dalle voci gia' salvate; scarta le vuote e i doppioni). I messaggi si salvano a ogni commit riuscito, per repository (radice dell'URL), in `%LOCALAPPDATA%SVNAssistecent-messages.json`: al massimo 25, un duplicato risale in cima |
| `genAi` / `improveAi` | – | genera / migliora il messaggio dal diff degli stessi file del commit (Staged, o tutte le modifiche versionate se Staged è vuoto) |
| `setLanguage` | `lang`: `auto|italiano|english` | lingua del messaggio AI |
| `setWorkingCopy` | `path` | carica una working copy |
| **`stage`** / **`unstage`** | `key` | sposta file, cartella o sezione tra Staged e Modifiche/Non versionati (i conflitti non si possono mettere in Staged) |
| `toggleFolder` | `key` | espandi/comprimi cartella |
| `toggleSection` | `sec` | espandi/comprimi sezione |
| `expandAll` / `collapseAll` | – | tutte le cartelle |
| `setView` | `view`: `tree|list` | albero per cartelle o elenco piatto |
| **`showDiff`** | `key` | **l'host apre il diff nell'editor di VS** (BASE ⇄ working copy), come Git Changes |
| `showFolderDiff` | `key` | diff di tutti i file modificati della cartella |
| `rowOp` | `op`: `add|ignore|revert|resolve|`**`deleteFile`**`|lock|unlock, key` | operazioni sulla riga; le conferme sono dialoghi nativi dell'host. `lock` (file modificato): chiede un messaggio facoltativo, poi `svn lock`; se il file è già di un altro chiede **conferma esplicita** prima di rubare il lock (`--force`). `unlock`: `svn unlock` |
| **`setTheme`** | `mode`: `auto|light|dark|blue` | tema della finestra; `auto` segue Visual Studio. L'host lo salva nelle impostazioni e rispedisce `theme` |
| `prereqToggle` / `prereqRecheck` | – | pannello Prerequisiti |
| `prereqRun` | `id`, `primary` | azione principale (`true`) o secondaria (`false`) di un prerequisito |

### Procedura guidata (AI e prerequisiti)
Stato e logica nel backend (`WizardStore`); la UI mostra `wizard` e invia solo questi comandi.
| Comando | Args | Effetto |
|---|---|---|
| `wizardOpen` | – | apre la procedura AI (passo 1; preseleziona il provider impostato). Parte da sola da "Genera con AI" senza provider |
| `wizardOpenPrereq` | – | apre la procedura sui prerequisiti mancanti (svn, provider AI); la riga `ai` porta alla procedura AI |
| `wizardPick` | `provider`: `copilot|claudecode|claude|openai` | sceglie il provider (passo 1) |
| `wizardNext` / `wizardBack` / `wizardCancel` | – | avanti (solo se il passo e' completo, ricontrolla prima) / indietro / chiude |
| `wizardRecheck` | – | rilegge prerequisiti e accesso (la UI lo manda da sola ogni 4 s mentre manca qualcosa e al ritorno del focus) |
| `wizardRun` | `id`, `primary` | azione di un prerequisito del passo 2 (stesse azioni della checklist) |
| `wizardLogin` | – | login nel terminale (`gh auth login --web`, o Claude Code; se manca gh lo installa) |
| `wizardSetAuth` | `endpoint`, `key` | salva la chiave (Claude API / OpenAI-compatibile) e l'endpoint (https, o http su localhost); vuoto = non cambiare. La chiave non torna mai nello snapshot |
| `wizardFinish` | `model` | salva `aiProvider` + modello con le chiavi di Tools > Options e chiude; se la procedura veniva da "Genera con AI", genera |
| `openOptions` | – | "Faccio io": chiude la procedura e apre Tools > Options > SVNAssist (`IHost.OpenSettingsAsync`) |

Menu di VS: `Extensions > SVNAssist > Change AI Provider...` (comando `CmdAiSetup` 0x0104) mostra Changes e invia `wizardOpen`.

### Log
`logLoad{path}` (ultime 50), `logMore` (altre 50), `logSelect{rev}` (carica il diff della revisione; chiude l'elenco `files`).

Filtri e diff nativo (tutti i diff si aprono nell'editor di VS, a due pannelli, con file temporanei da `svn export -r`; TortoiseSVN non serve):

| Comando | Args | Effetto |
|---|---|---|
| `logFilter` | `author, text, path, from, to` (tutti facoltativi, stringhe; date `aaaa-mm-gg`) | imposta i filtri e ricarica da capo (50); `logMore` li mantiene. Tutto vuoto = nessun filtro. Date invertite si scambiano |
| `logOpenFile` | `rev, i` | diff `r-1 ↔ r` del file `entries[rev].paths[i]` (file aggiunto: sinistra vuota; eliminato: destra vuota; cartelle rifiutate). Titolo: `Program.cs r12 ↔ r13` |
| `logCompare` | `a, b` | (Ctrl/Maiusc+clic su un'altra revisione) riempie `files` con i file cambiati tra le due (`svn diff --summarize`) |
| `logFolder` | `path` | diff di cartella (lo invia l'host da Solution Explorer, `IHost.ShowFolderDiffAsync`): riempie `files` con i file modificati (`svn status`) |
| `logFileOpen` | `i` | diff nativo del file `files.items[i]`: BASE ↔ working copy (`folder`) oppure `a ↔ b` (`compare`) |
| `logFilesPatch` | – | `files.kind = folder`: apre il vecchio `.diff` unico come documento |
| `logFilesClose` | – | chiude `files` |

Filtri: `svn log --xml -v -l N` con `-r {data}:{data}` per le date e `--search`/`--search-and` (svn ≥ 1.8, glob con `*` e `\` `?` `[` protetti) per autore, testo e percorso; poi il backend applica comunque il filtro esatto (sottostringa senza maiuscole) sulle revisioni lette, quindi `--search` è solo una riduzione. `-l` conta le revisioni già filtrate: "Altre 50" funziona anche con i filtri.

### Selettore Switch/Merge su repository grandi
Lo stesso modale serve a `switchOpen` e `mergeOpen`. **Selettore veloce**: campo di ricerca (contiene, senza maiuscole, subito), gruppo **Usati di recente** (`recent`, memorizzati per repository in `%LOCALAPPDATA%\SVNAssist\recent-targets.json`, al massimo 8, scritti a ogni `switchOk` valido in entrambi i modi) e al massimo **50 righe**; se ce ne sono di piu' compare "Mostrati N di M" e **Mostra tutti / vista estesa…**. **Vista estesa**: tabella Nome | Tipo | Rev. | Autore | Data (da `svn list --xml`: revisione, autore e data dell'ultima modifica), filtro per nome, per tipo, per **autore** (elenco degli autori presenti, senza maiuscole) e per **date** (preset "Ultimi 7/30/90 giorni" che riempie "Da", oppure Da / A, estremi inclusi; le voci senza data sono escluse), ordine per nome (numerico), revisione, autore o data (clic sull'intestazione, `aria-sort`), contatore "N di M" e **Azzera filtri** (spento se non ci sono filtri); il dialogo occupa quasi tutta la finestra (fino a 1500 px e 92% dell'altezza); **lista virtuale** (righe alte 28 px, nel DOM solo quelle visibili + margine: 500+ voci scorrono senza blocchi; stili via CSSOM perche' la CSP non ammette `style=""`); sotto i 720 px di larghezza le colonne si restringono (l'autore resta). Il focus resta nel campo (`aria-activedescendant`; frecce, PgSu/PgGiu, Invio). Ricerca, filtri e ordine sono solo della UI (nessun comando): l'elenco intero arriva con lo snapshot. Funzioni pure provate in `tests/ui-web/switch-large.test.mjs` con 500 voci finte: `U.swFilter` (nome, tipo, autore, from/to, sort name/rev/author/date), `U.swAuthors`, `U.swPreset`, `U.swQuick`, `U.swWindow`, `U.swRowsHTML`.

### Sezione Conflitti (Changes)
Sopra l'albero (e sotto il banner del merge) c'e' un riquadro **Conflitti (N)**, ampio (padding, testi e pulsanti piu' grandi), che si comprime/espande dalla freccia in testa (stato solo UI) e, con molti conflitti, scorre al suo interno (altezza massima 50% della finestra) senza schiacciare il resto. Un blocco per file di testo o di proprieta': nome, tipo, e i pulsanti **Apri editor di merge**, **Usa la mia**, **Usa la loro**, **Segna come risolto** (`conflictOp`). I **conflitti ad albero** sono raggruppati in un solo gruppo **Conflitti ad albero (N)** con una breve spiegazione, il pulsante **Segna tutti come risolti** (`resolveTreeConflicts`, con conferma e riepilogo degli errori) e un elenco espandibile (**Mostra elenco**, chiuso di default) con il **Segna come risolto** della singola voce (`conflictOp resolved`). "Usa la mia / Usa la loro per tutti" sui conflitti di testo non c'e': scarta una delle due parti e non e' un'operazione da fare in blocco. Sotto il file, "File di appoggio di svn (spariscono dopo il resolve)": `files`. Il backend li legge con `svn info --xml <file in conflitto>` (una chiamata per refresh, solo se ci sono conflitti; elementi `prev-base-file` = base, `prev-wc-file` = mia, `cur-base-file` = loro, `prop-file`) e li toglie dai Non versionati; se `svn info` fallisce restano dove sono. I conflitti restano anche nell'albero (menu contestuale "Risolvi conflitto…").

### Log: modalita' merge
`merge` nello snapshot del Log (`log-state.schema.json`) c'e' solo in modalita' merge (assente nel log normale). Il Log mostra le revisioni **ancora da unire** nella working copy: `svn mergeinfo --show-revs eligible SRC .` (testo, una `rNNN` per riga) + `svn log --xml -v -r max:min SRC` per autore, data e messaggio, filtrato sulle eligible. `entries` = quelle revisioni (piu' recente in alto), `sel` e' sempre `null`, la scelta multipla e' `merge.selected`. Al posto della working copy e dei filtri la UI mostra "Merge da branches/x" e **Chiudi merge**; clic su una riga (o Spazio/Invio) sceglie/toglie la revisione; il pannello sotto ha le azioni e l'esito. Richiede svn >= 1.8 (altrimenti `merge.error`).

| Comando | Args | Effetto |
|---|---|---|
| `mergeToggle` | `rev` | sceglie/toglie la revisione (solo se e' in elenco; azzera l'esito mostrato) |
| `mergeClear` | – | toglie la scelta a tutte |
| `mergePreview` | – | **Anteprima**: `svn merge --dry-run --accept postpone [-c N,M] SRC` (le scelte, o tutto se non c'e' scelta) dalla radice della working copy; `merge.result` con `mode:"preview"`: conteggi U/A/D/G/C e file. Non modifica nulla. La UI avverte che i conflitti ad albero e di proprieta' possono non essere previsti |
| `mergeAll` | – | **Merge tutto**: `svn merge --accept postpone SRC` (merge automatico, 1.8+) |
| `mergeSelected` | – | **Merge selezionate** (cherry-pick): `svn merge --accept postpone -c N,M SRC` |
| `mergeClose` | – | esce dal merge e ricarica il log della working copy (titolo del documento "Log: cartella") |
| `mergeResolve` | – | **Risolvi i conflitti** (pulsante del risultato con conflitti) e **Vai a Changes** (avviso della working copy sporca): porta in primo piano la finestra Changes (`IHost.ShowChangesWindowAsync`), dove c'e' la sezione Conflitti |
| `mergeRevertAll` | – | **SVN Revert di tutto…** (avviso giallo "la working copy ha N elementi con modifiche locali"): rilegge lo stato, chiede conferma con l'elenco dei file (come il Revert di sezione; negata = nessun effetto), annulla ogni modifica locale (non versionati esclusi) e ricalcola avviso e revisioni da unire: il merge si puo' rifare |
| `mergeUpdate` | – | **SVN Update ora** (errore che suggerisce l'update, es. revisioni miste `E195020`; `merge.needsUpdate`): `svn update` della working copy, poi ricalcola le revisioni da unire e toglie l'errore. Lo stesso schema in Changes: un errore di stato che suggerisce l'update (`E195020`, `E155011`, `E160024`, "out of date") mostra in `status.action` il pulsante **SVN Update ora** (`update`) |

Regole: il merge parte **solo da una working copy senza modifiche locali** (non versionati esclusi): altrimenti `merge.blocked` spiega "fai prima Commit oppure Revert" e il merge non parte (la condizione si ricontrolla a ogni azione, i pulsanti restano attivi). Cosi' "Annulla merge" (revert ricorsivo) non porta via lavoro non correlato. Nell'avviso `merge.blocked` la UI mette i pulsanti **SVN Revert di tutto…** (`mergeRevertAll`) e **Vai a Changes** (`mergeResolve`). Se la working copy e' a revisioni miste svn rifiuta (`E195020`) e l'errore compare in chiaro con il pulsante **SVN Update ora** (`mergeUpdate`, `merge.needsUpdate`). Il merge non committa e non usa Staged.
Dopo il merge (e dopo SVN Update, Update to revision e Switch) se restano conflitti **non c'e' nessun dialogo**: la finestra Changes viene solo portata in primo piano, con il riquadro Conflitti gia' li' (postpone: restano come sono); refresh di Changes (i conflitti bloccano il commit). Il messaggio di commit **non si precompila piu'**: il messaggio in stile TortoiseSVN (vedi sotto) va **in cima ai Recenti** del repository (`commit.recent`), cosi' resta anche dopo un revert e lo si inserisce da li' se serve. **Banner** in Changes (`merge {text, conflicts}`, "... Messaggio del merge disponibile nei Recenti.") con Annulla merge / Nascondi. Il banner sparisce da solo dopo un commit, un cambio di working copy o se non restano modifiche; con conflitti dice "N conflitti: risolvili qui sotto, poi committa" (N e' il numero **attuale**, scende a ogni resolve). Nel Log `merge.pending` e' vero finche' il merge non e' committato o annullato: la UI scrive "Merge eseguito, da committare" al posto di "0 revisioni da unire" (le eligible sono gia' ricalcolate) e il risultato con conflitti ha **Risolvi i conflitti** (`mergeResolve`). Le righe `--- Recording mergeinfo` / `Eliding` di svn non sono file e non si contano.
Parser (`MergeParser`, `tests/SVNAssist.Tests/MergeTests.cs`): righe d'azione = 4 colonne (file `U A D C G E R`, proprieta' `U C G`, lock, albero `C`) + spazio + percorso; riepilogo `Summary of conflicts` (Text/Property/Tree/Skipped); `Skipped '...'`.

### Barra di stato, revisione e controllo delle nuove revisioni
- **Messaggi**: un messaggio di successo (verde) sparisce dalla UI dopo circa 5 secondi (`status.seq` riparte il timer anche per un testo ripetuto); gli errori restano finché non sono sostituiti. I messaggi di sola informazione (riepilogo "2 modificati.") restano.
- **Indicatore della revisione** (sempre in basso, riga dei prerequisiti): `workingCopy.rev` = revisione della working copy (da `svn info`, almeno la revisione del nostro ultimo commit, perché un commit non porta la radice a quella revisione); `workingCopy.head` = ultima revisione del ramo sul server. `revState`: **current** (verde, `head <= rev`), **behind** (arancio, `head > rev`: "r1234 · disponibile r1240 — SVN Update", il clic manda `update`), **unknown** (neutro, `revNote` = perché: controllo spento, in corso, errore di rete o di autenticazione).
- **Controllo (watcher)**: ogni N minuti (impostazione `headCheckMinutes`, default 5, 0 = spento) `svn info --show-item last-changed-revision --non-interactive <URL della working copy>` (una richiesta piccola, in sola lettura, nessun dato oltre all'URL, credenziali già in cache di svn). Non è mai interattivo: un errore (rete, autenticazione, certificato) **non apre il login**, non è un errore di stato e non mostra popup, rende solo l'indicatore neutro. Parte alla prima apertura della finestra Changes, non parte se un'operazione dell'utente (commit, update, merge...) è in corso (riprova dopo 15 s) e due controlli non si sovrappongono. Si rifà subito dopo uno switch o un cambio di working copy e quando cambia l'impostazione. Limite noto: un nostro commit conta come revisione minima, quindi un commit altrui arrivato prima del nostro si vede solo dopo SVN Update.
- **Doppio clic / Invio su una riga** (`showDiff`): file modificato/conflitto = diff BASE ⇄ working copy; **file aggiunto (A) o non versionato (?) = il file intero nell'editor di VS** (`IHost.OpenFileAsync`, come Git Changes, senza diff contro il vuoto); file eliminato (D) o mancante (!) = diff con il contenuto BASE a sinistra e vuoto a destra (come Git).
- **Messaggio dopo un merge** (formato TortoiseSVN): `Unite le revisioni 4-6 da branches/feature-demo:` (inglese: `Merged revision(s) 4-6 from branches/feature-demo:`), poi per ogni revisione una riga `........` e `rN | autore | messaggio originale`; gli intervalli consecutivi si uniscono (`1,3-5`). Usa i dati del log già caricati nella vista merge. Si aggiunge ai Recenti (non al campo del messaggio). Il banner di Changes conserva il testo breve ("Merge r4-r6 da ^/branches/x: ...").

### Novità rispetto al piano (`webview2-plan.md` §3)
- **Staged** (stile Git Changes): SVN non ha uno staging, è un insieme tenuto dall'estensione. Sostituisce le caselle di selezione:
  `toggleFile`, `toggleGroup`, `selectAll`, `unselectAll` → `stage` / `unstage` (anche su cartella e su sezione intera).
  Come Git, **nulla entra in Staged da solo**: le modifiche restano in Modifiche e Staged contiene solo ciò che l'utente ci mette (la scelta resta tra un refresh e l'altro).
- **`rowOp{op:"deleteFile"}`**: tasto **Canc** su un file = elimina dal filesystem (se versionato: `svn delete`), con conferma dell'host e avviso se ci sono modifiche locali. Sostituisce `op:"delete"`. Non si applica a cartelle.
- **`showDiff`** non produce più un campo `diff` nello snapshot: il diff si apre nell'editor di VS (`closeDiff` è sparito).
  Facoltativo: l'host può ospitare la vista `diff` (pagina di diff affiancato con evidenziazione) in un documento WebView2; è ciò che fa la demo.
- **`setTheme`** + campo `theme` nello snapshot.
- `rowOp` perde `delete` e il folder-diff diventa il comando `showFolderDiff`.
- `prereqClose` assorbito da `prereqToggle`.

## Snapshot

### `changes` (`changes-state.schema.json`)
```
workingCopy {path, showInput, branch, rev?, svnVersion?, head?, revState?: current|behind|unknown, revNote?}   // rev/head/revState = indicatore della revisione in basso (verde current, arancio behind, neutro unknown con il perche' in revNote); head = last-changed-revision del ramo sul server   // branch = ramo dedotto dall'URL (trunk, branches/x, tags/y, "/ (radice)"); svnVersion = es. "1.14.2" (la UI nasconde le funzioni che richiedono svn piu' recente)
commit      {text, aiStatus, aiLevel: ""|busy|ok|warn|err|info, aiSeq, busy, lang, trace[{name,state,detail}], download{pct,label}|null,
             count?, all?, recent?[]}   // count = elementi del prossimo commit; all = Staged vuoto -> "Commit All"; recent = ultimi messaggi committati nel repository (max 25, il più recente per primo)
view        "tree" | "list"
theme       {mode: auto|light|dark|blue, kind: light|dark|blue}
sections[3] {id: staged|changes|unversioned, title, count, expanded, rows[]}
  row       {key, kind: file|folder, depth, name, detail, dir, expanded, count, status, statusText, can{…, lock?, unlock?}, tooltip, locked?}
              status "K" = file solo bloccato (lock tenuto da questa working copy, nessuna modifica); `locked:true` = lucchetto accanto al nome
status      {msg, err, seq?, action?{label, cmd:"cleanup"}}   // seq cresce a ogni messaggio d'azione: un successo (verde) sparisce dopo ~5 s, un errore resta
prereq      {open, summary, ai, feedback, items[{id, group, title, ok, detail, instructions, primary, secondary}]}
loading     bool
wizard      {open, mode: ai|prereq, step 1-4, fromGenerate, provider, busy, feedback, providers[{id,title,cost}], items[prereq item],
             auth: ""|github|claudecode|key|openai, authOk, authDetail, endpoint, models[{value,label}], model, canNext}
revPicker?  {open, target, loading, error, canMore, items[{rev, author, date (ISO), msg (prima riga)}]}   // modale "Update to revision"
switchPicker? {open, mode: switch|merge, loading, error, items[{name, kind: trunk|branch|tag, current, rev, author, date}], recent[name]}   // modale "SVN Switch" / sorgente di "SVN Merge" (senza il ramo corrente); name relativo alla base (trunk, branches/x, tags/1.0); rev/author/date = ultima modifica da svn list --xml; recent = ultimi usati per repository
merge?      {text, conflicts}   // banner dopo un merge non committato (Annulla merge); conflicts = conflitti ancora aperti; assente se non c'e'
conflicts?  [{key (changes:percorso), path, name, kind: text|property|tree, files[]}]   // sezione Conflitti: files = file di appoggio di svn (.mine, .rNN, .merge-left/right.rN, .working, .prej) da `svn info --xml` (mai indovinati dal nome), NON compaiono tra i non versionati
```
`aiLevel` colora il messaggio AI (avviso giallo, errore rosso, esito verde); `aiSeq` cresce a ogni messaggio, cosi' anche uno ripetuto si ridisegna.
Le righe sono **già piatte e ordinate** (albero calcolato dal backend con `ChangesTreeBuilder`): la UI non ricostruisce nulla.
`depth` 0 = figli diretti della sezione. `can.*` dice quali azioni sono valide; la UI disabilita/nasconde di conseguenza.
`sections` contiene sempre le tre sezioni; la UI nasconde quelle vuote tranne Staged.

### `log` (`log-state.schema.json`)
`{path, msg, err, loading, canMore, sel, entries[{rev, author, date (ISO 8601), msg, paths[{a,p,k?}]}], diff, filter{author,text,path,from,to}, files, merge?}`
`merge?` (solo in modalita' merge, vedi sopra): `{source, title, loading, busy, error, blocked, selected[rev], eligible, pending?, needsUpdate?, result: null | {mode: preview|merge, revs, counts{updated,added,deleted,merged,conflicted}, items[{a: A|M|D|R|C, p}], skipped}}`.
Layout (solo UI, il contratto non cambia): filtri in alto; tabella ARIA `grid` (Rev. | Autore | Data | Messaggio = prima riga, intestazioni sticky, colonne ridimensionabili, `ArrowUp/Down/Home/End` selezionano via `logSelect`, `Enter` conferma, Ctrl/Maiusc+clic = `logCompare`) con "Carica altre 50" (`logMore`) in fondo; dettaglio sotto, o a destra se il contenitore e' largo >= 1100px, con messaggio completo e file (azione + percorso, clic = `logOpenFile`). Nell'host la vista e' un documento dell'area editor.
(`paths[].k = "dir"` per le cartelle; `files` = `null` oppure `{kind: "folder"|"compare", title, items[{a,p}]}`: elenco di file con diff a due pannelli, al posto del dettaglio revisione. `filter`/`files` sono facoltativi nello schema: una UI li tratta come assenti.)

### `diff` (`diff-state.schema.json`)
`{title, subtitle, diff}` — `diff` è il testo di `svn diff` (uno o più file, righe `Index:`); la pagina lo mostra affiancato.

## Lingua dell'interfaccia

Impostazione `uiLanguage` = `auto` (default) | `it` | `en` (Tools > Options > SVNAssist > Generale, oppure Extensions > SVNAssist > Language / Lingua). `auto` = italiano se la cultura UI di Visual Studio è `it-*`, altrimenti inglese. **La risolve l'host** (ha la cultura di VS) e la manda al backend come `uiLanguageEffective` (`it`/`en`) insieme a `uiLanguage` in `GetSettingsAsync`; senza valori (test) il backend resta in italiano.
Il backend tiene la lingua effettiva in `Loc.Lang` (processo unico) e ogni `state` porta `lang`: la UI chiama `U.setLang(lang)` **prima** di ridisegnare, così i testi propri della UI (`js/i18n.js`) e quelli già tradotti dal backend (stato, errori, prerequisiti, procedura guidata, nomi degli stati) sono sempre nella stessa lingua.
**Cambio a caldo**: l'host aggiorna `Loc.Lang` e chiama `IBackend.SettingsChangedAsync()`; il backend rilegge la lingua, ricalcola checklist/procedura guidata/riepilogo e rimanda lo snapshot a tutte le viste (la firma dello snapshot include `lang`, quindi parte anche se il resto è identico). I messaggi di stato già scritti (es. "Commit completato…") restano nella lingua in cui sono nati fino al prossimo messaggio.
Convenzione dei testi (UI e backend): la **chiave è il testo italiano**, con segnaposto `{0}`, `{1}`; la traduzione inglese sta in `js/i18n.js` (`EN`) e in `src/Shared/LocEn.cs`. In italiano `t()`/`Loc.T()` ritornano la chiave. I test (`tests/ui-web/i18n.test.mjs`, `LocalizationTests`) verificano che ogni testo usato nel codice abbia la voce inglese con gli stessi segnaposto e che non restino voci inutilizzate.
Non si traducono: messaggi di `svn.exe`, log, prompt inviati all'AI, i nomi dei comandi SVN ("SVN Update", "SVN Commit", "SVN Cleanup", Lock/Unlock) e le voci di menu di VS (sono in inglese standard SVN, uguali per entrambe le lingue). La lingua dei **commit message generati dall'AI** è un'altra impostazione (`defaultLanguage` + scelta nel pannello) e non cambia.

## Tema

`tokens.css` definisce tre temi (`:root[data-theme=dark|light|blue]`); l'unico posto con colori fissi. Tutto il resto usa `var(--…)`.
Il test `tests/ui-web/contrast.test.mjs` verifica **WCAG AA (≥ 4.5:1)** per tutte le coppie testo/sfondo usate, in tutti i temi.
Modalità **Auto**: l'host manda `theme{kind, vars}` con le variabili derivate dai colori di VS (`--bg0…--bg4`, `--bd`, `--fg…`, `--acc…`, `--st-*`);
la UI le applica con `style.setProperty` (ammesso dalla CSP). Alto contrasto di Windows: l'host passa i colori di sistema direttamente in `vars`.
Variabili: `--bg0..4 --bd --bd2 --fg --fg2 --fg3 --acc --acc-fg --acc-h --acc2 --acc-bg --focus --ok --warn --err --st-{M,A,D,C,U,X,R} --add/del/hunk-* --syn-* --chip-* --warn-bg/fg --shadow --scrim --font --mono --fs`.

## Tastiera (albero, ARIA `tree`)
↑ ↓ Home End: muovi · → espandi / vai al primo figlio · ← comprimi / vai al padre · Invio: diff (file) o espandi/comprimi · Spazio: Staged ⇄ Modifiche ·
**Canc**: elimina file (con conferma) · Menu/Maiusc+F10: menu contestuale · doppio clic: diff · Ctrl+Invio nel messaggio: commit.
Roving tabindex: un solo elemento nel tab order. Le icone senza testo hanno `aria-label` e `title`.

## Sicurezza (CSP in `index.html`)
`default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'none'`. Niente script/stile inline, niente `eval`, niente CDN
(verificato da `render.test.mjs`). Tutto ciò che viene dai dati passa da `esc()`; `data-args` contiene JSON escapato.

## Build e verifica
- `node tools/build-demo.mjs` → `docs/demo/svnassist-demo.html` (file unico, funziona da `file://`; la UI è incorporata come `srcdoc` negli iframe della shell VS finta).
- `node --test "tests/ui-web/*.test.mjs"` → contrasto, fixture vs schema, mock-bridge vs schema (snapshot di ogni passo di uno scenario completo), comandi della UI vs schema, render, CSP.
- `dotnet test tests/SVNAssist.Tests` → store del backend, snapshot reali vs gli stessi schemi (`SnapshotSchemaTests`), backend completo con host finto e repository `file://` (`BackendIntegrationTests`), eseguibile vero su StreamJsonRpc (`BackendProcessTests`).

## Backend (`src/SVNAssist.Backend`) e contratto RPC

`SVNAssist.Backend.exe` (net8, apphost, framework-dependent) parla **StreamJsonRpc su stdin/stdout** (formatter e opzioni di default, nomi dei metodi = nomi C# delle interfacce di `src/Shared/RpcContract.cs`, es. `"DispatchAsync"`).
stdout è solo canale RPC (le scritture accidentali sulla console sono deviate su stderr); i log vanno in `%LOCALAPPDATA%\SVNAssist\logs\backend-aaaammgg.log`. Il processo termina quando l'host chiude la pipe (anche se VS muore).

| Direzione | Metodo | Note |
|---|---|---|
| host → backend | `DispatchAsync(view, json)` | `view` = `changes` \| `log` \| `diff`; `json` = messaggio JS→host (`ready`/`cmd`) inoltrato così com'è. Un `ready` fa partire il caricamento e riceve subito uno `state` |
| host → backend | `SettingsChangedAsync()` | il backend scarta la cache e rilegge con `IHost.GetSettingsAsync` (tema, provider AI, working copy) |
| host → backend | `SolutionChangedAsync(dir?)` | cartella della solution (null = nessuna): il backend risale fino a `.svn`. Il setting `workingCopyPath`, se valorizzato, vince |
| host → backend | **`ThemeChangedAsync(kind)`** *(metodo nuovo)* | `light`\|`dark`\|`blue` = tema di VS; chiamarlo all'avvio e a ogni cambio. Riempie `theme.kind` dello snapshot in modo `auto` |
| backend → host | `PushAsync(view, json)` | `state` (coalescenza 16 ms, solo se cambiato, `rev` crescente per vista) e `reply` (sempre **dopo** lo `state` che ne mostra gli effetti) |
| backend → host | `PromptAsync(msg, buttons)` | indice del pulsante, -1 = chiuso. Convenzione: conferme = `["OK","Annulla"]` (0 = sì); risoluzione conflitto = 5 pulsanti |
| backend → host | `GetSettingsAsync` / `SetSettingAsync("windowTheme", mode)` | vedi "Impostazioni" |
| backend → host | `OpenDiffAsync(left, right, leftLabel, rightLabel, caption)` | per `showDiff`: `left` = BASE esportato (`svn export -r BASE`) in `%TEMP%\SVNAssist\diff\<guid>\BASE\<nome>`, `right` = il file della working copy (file vuoto temporaneo se mancante/eliminato). Il lato sinistro è vuoto per i file senza BASE (aggiunti) |
| backend → host | `OpenFileAsync(path)` | per `showFolderDiff`: un unico file `Diff <cartella>.diff` temporaneo (max 100 file) |
| backend → host | `RunInTerminalAsync(command, dir)` | comandi in **sintassi PowerShell** (`gh auth login ...`, `winget install ...`, `irm https://claude.ai/install.ps1 \| iex`, `& "C:\...\claude.exe"`) |
| backend → host | `OpenUrlAsync(url)` | solo `https` |

Messaggi `theme` (variabili CSS): li manda **l'host** (all'`ready`, a ogni cambio di tema VS, e quando vede `SetSettingAsync("windowTheme", …)` da `setTheme`). Il backend riporta solo `theme: {mode, kind}` nello snapshot. `rev` del `workingCopy` non è valorizzato (facoltativo nello schema).

La vista `diff` (documento WebView2 di diff affiancato) è facoltativa: il backend risponde a `ready{view:"diff"}` con uno stato vuoto, perché `showDiff` apre l'editor di VS.

### Validazione
Tutto ciò che arriva dalla UI è non fidato: `v`, `type`, `id`, `name`, `args` e ogni argomento sono controllati (errore → `reply{ok:false,error}`, mai eccezioni verso l'host). Le **chiavi di riga** sono rivalidate contro lo stato corrente di `svn status` (se l'elemento non c'è più: errore "aggiorna l'elenco"); i percorsi usati per operare vengono sempre da `svn status`, mai dalla chiave; un elemento fuori dalla working copy viene scartato; `deleteFile` ricontrolla che il file sia sotto la radice della working copy. Gli unici percorsi che la UI invia sono quelli digitati (`setWorkingCopy`, `logLoad`): devono essere una cartella esistente dentro una working copy (o, per il log, un URL di repository).

### Comportamenti da conoscere (rispetto al piano)
- **Staged**: insieme in memoria per working copy. Nulla ci entra da solo (come Git): lo riempie l'utente con `stage`; i percorsi spariti dalla working copy ne escono (un conflitto non si può mettere in Staged; se un file in Staged diventa conflitto il commit si rifiuta).
- `commit`: insieme = Staged, o con Staged vuoto tutte le modifiche versionate (`CommitSet`). Aggiunge i non versionati in Staged (con le cartelle padre nuove) e cancella i mancanti, poi `svn commit --depth empty` sui soli percorsi elencati.
- `rowOp deleteFile` (Canc su file, non cartelle): conferma con `PromptAsync`; versionato → `svn delete --force`; non versionato → cestino (se non disponibile, eliminazione diretta). Mancante → `svn delete`.
- **Lock**: lo stato dei lock è solo quello **locale** (`<lock>` in `svn status --xml`, la "K" di svn), a costo zero (nessuna rete, nessun `-u`). I lock di altri (O/T/B) non si leggono; si scoprono quando `svn lock` fallisce ("già bloccato da X") e si offre di rubarlo con conferma. Un file bloccato ma non modificato compare in Modifiche con stato `K` (non si può mettere in Staged, né diff/revert). Il `commit` rilascia i lock dei file committati (default di svn).
- **Login SVN**: il backend esegue sempre svn con `--non-interactive`. Solo per un errore di autenticazione (`E170001`, `E215004`, `Authentication failed`, `No more credentials`…) chiama `IHost.PromptCredentialsAsync` (utente, password mascherata, "Ricorda" di default sì) e **ripete il comando una sola volta** con `--username U --password-from-stdin` (svn ≥ 1.10; con svn più vecchio `--password` e un avviso nel log), senza `--no-auth-cache` se "Ricorda". Annulla = errore normale. Le credenziali non vanno in nessun log né nello stato UI. Con l'accesso integrato di Windows il dialogo non dovrebbe mai comparire.
- Working copy bloccata (`E155004`/`E155037`, "run 'cleanup'"): il messaggio d'errore ha il pulsante "Esegui Cleanup" (`status.action`).
- Il log usa `svn log -v` (percorsi toccati) 50 revisioni alla volta; `logSelect` accetta solo revisioni già in elenco.
- AI: il diff dei file del commit (Staged, o tutte le modifiche versionate se Staged è vuoto) va ai provider elencati; conferma `PromptAsync` una volta per sessione (e per ogni file sospetto). `commit.trace` è ricostruita dalla catena (`FallbackAiService`); `commit.download` è valorizzato (pct 0) durante il download del runtime Copilot.

## Impostazioni

L'host le espone in Tools > Options > SVNAssist (categoria "SVNAssist") e le restituisce in `IHost.GetSettingsAsync()` come `Dictionary<string,string>` con **queste chiavi** (camelCase; il backend ignora maiuscole/minuscole e usa il default se manca la chiave). `SetSettingAsync` scrive solo `windowTheme`. Dopo ogni modifica l'host chiama `IBackend.SettingsChangedAsync()`. Le etichette (italiano) e i valori delle tendine sono in `docs/legacy-commands.md`.

| Chiave | Tipo | Default | Valori ammessi |
|---|---|---|---|
| `aiProvider` | enum | `auto` | `auto`, `copilot`, `claudecode`, `claude`, `openai` |
| `claudeModel` | enum | `claude-sonnet-5-5` | `claude-sonnet-5-5`, `claude-haiku-5-5`, `claude-opus-5-5` |
| `anthropicApiKey` | string (segreto) | `` | vuota = variabile `ANTHROPIC_API_KEY` |
| `copilotModel` | enum | `default` | `default`, `gpt-4.1`, `gpt-5-mini`, `claude-sonnet-4.5` |
| `aiEndpoint` | string | `https://models.github.ai/inference/chat/completions` | URL Chat Completions OpenAI-compatibile |
| `aiApiKey` | string (segreto) | `` | chiave o GitHub PAT |
| `aiModel` | enum | `openai/gpt-4o-mini` | `openai/gpt-4o-mini`, `openai/gpt-4o`, `openai/gpt-4.1-mini`, `openai/gpt-4.1` |
| `defaultLanguage` | enum | `auto` | `auto`, `italiano`, `english` |
| `workingCopyPath` | string | `` | se valorizzato vince sull'auto-rilevamento dalla solution |
| `repoLayout` | string | `trunk/branches/tags` | nomi delle cartelle tronco/branch/tag separati da `/` (o `,` `;`); se non sono tre nomi ricade sul default. Funziona anche con il progetto alla radice (base vuota) o sotto un sottopercorso (`^/progetto/trunk` -> branch in `^/progetto/branches`) |
| `headCheckMinutes` | int | `5` | ogni quanti minuti chiedere al server l'ultima revisione del ramo (`svn info --show-item last-changed-revision --non-interactive <URL>`, sola lettura) per l'indicatore della revisione; `0` = spento; massimo 1440 |
| `windowTheme` | enum | `auto` | `auto`, `light`, `dark`, `blue` (scritta dal backend con `SetSettingAsync`) |
| `uiLanguage` | enum | `auto` | `auto`, `it`, `en` — lingua dell'interfaccia (vedi "Lingua dell'interfaccia"); l'host aggiunge `uiLanguageEffective` (`it`/`en`, già risolta, non è un'impostazione) |

Un valore fuori elenco ricade sul default (come prima). Le chiavi segrete non vanno mai loggate.
