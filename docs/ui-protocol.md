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
| `state` | host → JS | `view`, `id?`, `rev` (cresce), `state` — **snapshot completo** della vista |
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
| `updateToRevision` | – | **SVN Update to revision**: l'host chiede la revisione (`IHost.PromptTextAsync`; intero positivo o `HEAD`, altrimenti richiede) e fa `svn update -r N`. Esito come l'update (revisione, conflitti) |
| `cleanup` | – | **SVN Cleanup** della working copy (`svn cleanup`). Compare anche come pulsante "Esegui Cleanup" accanto a un errore di working copy bloccata (`status.action`) |
| `commit` | – | commit dei soli file in **Staged**; se ci sono non versionati li aggiunge, i mancanti li elimina; rifiuta i conflitti |
| `setCommitText` | `text` | aggiorna il messaggio |
| `genAi` / `improveAi` | – | genera / migliora il messaggio dal diff dei file in Staged |
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

Menu di VS: `Extensions > SVNAssist > Cambia provider AI...` (comando `CmdAiSetup` 0x0104) mostra Changes e invia `wizardOpen`.

### Log
`logLoad{path}` (ultime 50), `logMore` (altre 50), `logSelect{rev}` (carica il diff della revisione).

### Novità rispetto al piano (`webview2-plan.md` §3)
- **Staged** (stile Git Changes): SVN non ha uno staging, è un insieme tenuto dall'estensione. Sostituisce le caselle di selezione:
  `toggleFile`, `toggleGroup`, `selectAll`, `unselectAll` → `stage` / `unstage` (anche su cartella e su sezione intera).
  I file nuovi (M/A/D/R/!) partono in Staged, i conflitti e i non versionati no.
- **`rowOp{op:"deleteFile"}`**: tasto **Canc** su un file = elimina dal filesystem (se versionato: `svn delete`), con conferma dell'host e avviso se ci sono modifiche locali. Sostituisce `op:"delete"`. Non si applica a cartelle.
- **`showDiff`** non produce più un campo `diff` nello snapshot: il diff si apre nell'editor di VS (`closeDiff` è sparito).
  Facoltativo: l'host può ospitare la vista `diff` (pagina di diff affiancato con evidenziazione) in un documento WebView2; è ciò che fa la demo.
- **`setTheme`** + campo `theme` nello snapshot.
- `rowOp` perde `delete` e il folder-diff diventa il comando `showFolderDiff`.
- `prereqClose` assorbito da `prereqToggle`.

## Snapshot

### `changes` (`changes-state.schema.json`)
```
workingCopy {path, showInput, branch, rev}
commit      {text, aiStatus, aiLevel: ""|busy|ok|warn|err|info, aiSeq, busy, lang, trace[{name,state,detail}], download{pct,label}|null}
view        "tree" | "list"
theme       {mode: auto|light|dark|blue, kind: light|dark|blue}
sections[3] {id: staged|changes|unversioned, title, count, expanded, rows[]}
  row       {key, kind: file|folder, depth, name, detail, dir, expanded, count, status, statusText, can{…, lock?, unlock?}, tooltip, locked?}
              status "K" = file solo bloccato (lock tenuto da questa working copy, nessuna modifica); `locked:true` = lucchetto accanto al nome
status      {msg, err, action?{label, cmd:"cleanup"}}
prereq      {open, summary, ai, feedback, items[{id, group, title, ok, detail, instructions, primary, secondary}]}
loading     bool
wizard      {open, mode: ai|prereq, step 1-4, fromGenerate, provider, busy, feedback, providers[{id,title,cost}], items[prereq item],
             auth: ""|github|claudecode|key|openai, authOk, authDetail, endpoint, models[{value,label}], model, canNext}
```
`aiLevel` colora il messaggio AI (avviso giallo, errore rosso, esito verde); `aiSeq` cresce a ogni messaggio, cosi' anche uno ripetuto si ridisegna.
Le righe sono **già piatte e ordinate** (albero calcolato dal backend con `ChangesTreeBuilder`): la UI non ricostruisce nulla.
`depth` 0 = figli diretti della sezione. `can.*` dice quali azioni sono valide; la UI disabilita/nasconde di conseguenza.
`sections` contiene sempre le tre sezioni; la UI nasconde quelle vuote tranne Staged.

### `log` (`log-state.schema.json`)
`{path, msg, err, loading, canMore, sel, entries[{rev, author, date (ISO 8601), msg, paths[{a,p}]}], diff}`

### `diff` (`diff-state.schema.json`)
`{title, subtitle, diff}` — `diff` è il testo di `svn diff` (uno o più file, righe `Index:`); la pagina lo mostra affiancato.

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
- **Staged**: insieme in memoria per working copy. I nuovi elementi `M A D R !` partono in Staged; conflitti e non versionati no (un conflitto non si può mettere in Staged; se un file in Staged diventa conflitto il commit si rifiuta).
- `commit`: aggiunge i non versionati in Staged (con le cartelle padre nuove) e cancella i mancanti, poi `svn commit --depth empty` sui soli percorsi elencati.
- `rowOp deleteFile` (Canc su file, non cartelle): conferma con `PromptAsync`; versionato → `svn delete --force`; non versionato → cestino (se non disponibile, eliminazione diretta). Mancante → `svn delete`.
- **Lock**: lo stato dei lock è solo quello **locale** (`<lock>` in `svn status --xml`, la "K" di svn), a costo zero (nessuna rete, nessun `-u`). I lock di altri (O/T/B) non si leggono; si scoprono quando `svn lock` fallisce ("già bloccato da X") e si offre di rubarlo con conferma. Un file bloccato ma non modificato compare in Modifiche con stato `K` (non si può mettere in Staged, né diff/revert). Il `commit` rilascia i lock dei file committati (default di svn).
- **Login SVN**: il backend esegue sempre svn con `--non-interactive`. Solo per un errore di autenticazione (`E170001`, `E215004`, `Authentication failed`, `No more credentials`…) chiama `IHost.PromptCredentialsAsync` (utente, password mascherata, "Ricorda" di default sì) e **ripete il comando una sola volta** con `--username U --password-from-stdin` (svn ≥ 1.10; con svn più vecchio `--password` e un avviso nel log), senza `--no-auth-cache` se "Ricorda". Annulla = errore normale. Le credenziali non vanno in nessun log né nello stato UI. Con l'accesso integrato di Windows il dialogo non dovrebbe mai comparire.
- Working copy bloccata (`E155004`/`E155037`, "run 'cleanup'"): il messaggio d'errore ha il pulsante "Esegui Cleanup" (`status.action`).
- Il log usa `svn log -v` (percorsi toccati) 50 revisioni alla volta; `logSelect` accetta solo revisioni già in elenco.
- AI: il diff dei soli file in Staged va ai provider elencati; conferma `PromptAsync` una volta per sessione (e per ogni file sospetto). `commit.trace` è ricostruita dalla catena (`FallbackAiService`); `commit.download` è valorizzato (pct 0) durante il download del runtime Copilot.

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
| `windowTheme` | enum | `auto` | `auto`, `light`, `dark`, `blue` (scritta dal backend con `SetSettingAsync`) |

Un valore fuori elenco ricade sul default (come prima). Le chiavi segrete non vanno mai loggate.
