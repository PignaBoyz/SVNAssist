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
| `rowOp` | `op`: `add|ignore|revert|resolve|`**`deleteFile`**`, key` | operazioni sulla riga; le conferme sono dialoghi nativi dell'host |
| **`setTheme`** | `mode`: `auto|light|dark|blue` | tema della finestra; `auto` segue Visual Studio. L'host lo salva nelle impostazioni e rispedisce `theme` |
| `prereqToggle` / `prereqRecheck` | – | pannello Prerequisiti |
| `prereqRun` | `id`, `primary` | azione principale (`true`) o secondaria (`false`) di un prerequisito |

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
commit      {text, aiStatus, busy, lang, trace[{name,state,detail}], download{pct,label}|null}
view        "tree" | "list"
theme       {mode: auto|light|dark|blue, kind: light|dark|blue}
sections[3] {id: staged|changes|unversioned, title, count, expanded, rows[]}
  row       {key, kind: file|folder, depth, name, detail, dir, expanded, count, status, statusText, can{…}, tooltip}
status      {msg, err}
prereq      {open, summary, ai, feedback, items[{id, group, title, ok, detail, instructions, primary, secondary}]}
loading     bool
```
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
