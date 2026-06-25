# SVNAssist — Analisi del progetto e visione finale

> Documento di lavoro. Cosa fa oggi l'estensione, dove il codice può migliorare
> (Clean Code / SOLID / testing) e dove vogliamo arrivare.

---

## 1. Cos'è il progetto (come l'ho capito)

**SVNAssist** è un'estensione **VSIX per Visual Studio 2026** che porta **Subversion** dentro l'IDE
con un'esperienza moderna in stile *Git Changes*, più la generazione automatica dei commit message via AI.

- **Modello**: nuovo `VisualStudio.Extensibility` **out-of-process** (.NET 8), non il vecchio VSSDK.
  Vantaggio chiave: se l'estensione crasha, **VS non crasha**.
- **SVN**: invocato tramite `svn.exe` come processo esterno (no SharpSVN → niente dipendenze native).
- **AI**: chiamate HTTP generiche in formato **OpenAI Chat Completions**, quindi multi-provider
  (GitHub Models di default, OpenAI, Azure, Ollama, ecc.).
- **UI**: Remote UI (XAML `DataTemplate` + ViewModel `[DataContract]`), sincronizzata via JSON-RPC
  tra processo estensione e processo VS.
- **Natura**: progetto **didattico** → il codice deve restare chiaro e commentato.

### Mappa dei componenti

| Componente | File | Responsabilità |
|---|---|---|
| Entry point | `SVNAssistExtension.cs` | Registra servizi DI + settings observer |
| Menu | `Commands/SvnAssistMenu.cs` | Sottomenu *Extensions → SVNAssist* |
| Comandi | `Commands/*.cs` | Status, Commit, Update, Log → aprono tool window |
| SVN | `Services/SvnService.cs` (+ `ISvnService`) | Wrapper su `svn.exe`: status/commit/update/log/diff/add/delete/revert/resolve/ignore |
| AI | `Services/AiService.cs` (+ `IAiService`) | Genera/migliora commit message, con chunking dei diff grandi |
| Settings | `Services/SettingsService.cs` | Legge endpoint/key/model/lingua/path da Tools→Options |
| Tool window Status | `ToolWindows/SvnStatus*` | Pannello *SVN Changes*: lista file, commit inline, diff, AI, watcher |
| Tool window Log | `ToolWindows/SvnLog*` | Cronologia revisioni + diff |
| Modelli | `Models/*.cs` | `SvnFileStatus`, `SvnStatusKind`, `SvnLogEntry`, `CommitOptions` |
| Test | `tests/SVNAssist.Tests` | xUnit: integrazione su `SvnService`, mock HTTP su `AiService` (13 test) |

### Cosa funziona già (dal backlog)

Auto-detect della working copy dalla solution, pannello *SVN Changes* compatto, commit inline,
diff per file (con fallback TortoiseSVN), menu contestuale (add/delete/revert/resolve/ignore),
watcher filesystem + polling solution per refresh automatico, tool window Log, settings multi-provider.

---

## 2. Dove il codice può migliorare (Clean Code / SOLID / testing)

Punti concreti emersi dalla lettura del codice. Sono opportunità, non bocciature.

### 2.1 SOLID

1. **DIP violata — `new SvnService()` dentro il ViewModel** (`SvnStatusToolWindowData.cs:61`).
   Il ViewModel istanzia direttamente il servizio concreto invece di ricevere `ISvnService` via DI.
   Conseguenze: **non testabile** (non posso iniettare un mock) e **fragile** — se `svn.exe`
   non c'è, il costruttore di `SvnService` lancia `FileNotFoundException` e la tool window non si apre.

2. **`AiService` creato a mano nel ViewModel** (`CreateAiServiceAsync`).
   Stesso problema di accoppiamento. Inoltre usa un `HttpClient` **statico condiviso** di cui
   muta `DefaultRequestHeaders.Authorization` a ogni chiamata → **bug di concorrenza**: due
   operazioni AI in parallelo possono sovrascriversi l'header di autenticazione.

3. **SRP — il ViewModel è un "God object"** (~930 righe).
   Gestisce refresh, commit, update, AI, diff, add/delete/revert/resolve/ignore, selezione,
   normalizzazione path, mappatura colori/stati. Troppe responsabilità in una classe.

### 2.2 Correttezza / robustezza

4. **Escaping degli argomenti `svn.exe` fragile.** `RunSvnAsync` costruisce una stringa
   `Arguments` concatenando path e messaggi con escaping manuale delle virgolette
   (`message.Replace("\"","\\\"")`). Un messaggio di commit con virgolette/backslash, o un path
   con caratteri speciali, può rompere il comando. Meglio `ProcessStartInfo.ArgumentList`
   (escaping gestito dal runtime).

5. **`svn:ignore` con valore multilinea passato sulla command line** — i `\n` nel valore
   difficilmente sopravvivono come argomento. Andrebbe passato via stdin o file temporaneo.

6. **Polling aggressivo**: timer da 1s (live refresh) + 5s (solution) + FileSystemWatcher.
   Funziona ma è ridondante e consuma; valutare un'unica pipeline di refresh con debounce.

### 2.3 Testing

7. **La logica più importante (il ViewModel) non è testata**, proprio per il `new SvnService()`.
   Risolvendo il punto 1 (iniettando `ISvnService`/`IAiService`) diventa tutto unit-testabile.

8. **`SvnServiceTests` sono test di integrazione** che richiedono `svn.exe`+`svnadmin.exe` nel PATH:
   ottimi per validare l'integrazione reale, ma non girano in CI senza SVN. Manca un livello
   per **unit-testare la costruzione degli argomenti** (astraendo l'esecuzione del processo).

### 2.4 Igiene repo

9. **`pp.xml` (1.6MB) committato per errore** — è output di preprocessing MSBuild. Rimuovere e
   aggiungere a `.gitignore`.

---

## 3. Dove vogliamo arrivare (visione finale)

Un client SVN **moderno, integrato in VS, con UX vicina a Git ma fedele a SVN**, che permette di
fare *tutte le operazioni SVN che ha senso fare dentro l'IDE* — non un sostituto completo di
TortoiseSVN, ma il 90% del lavoro quotidiano senza uscire da Visual Studio.

### 3.1 Operazioni SVN "da IDE" (proposta di scope)

| Categoria | Comandi | In scope IDE? |
|---|---|---|
| **Quotidiane** | status, commit, update (intero / singola folder), revert, add, delete, diff | ✅ Sì (core) |
| **Sincronizzazione** | update to revision, cleanup, resolve conflitti | ✅ Sì |
| **Storia** | log/history, blame/annotate, show changes of revision | ✅ Sì |
| **Branch/Tag** | switch, merge, copy (branch/tag), lista branch | ⚠️ Utile ma avanzato |
| **Proprietà** | svn:ignore, svn:externals, propset/propget | ⚠️ Parziale |
| **Lock** | lock/unlock | ⚠️ Opzionale |
| **Repo-browser** | navigare l'URL del repository | ❌ Probabilmente fuori scope IDE |

### 3.2 UX moderna (stile Git, fedele a SVN)

- Pannello *SVN Changes* come oggi, ma con: raggruppamento per stato, commit di selezione parziale,
  amend del messaggio, indicatore di sync (incoming/outgoing) verso il repo.
- Update **mirato**: pulsante update sull'intera working copy **e** su singola folder/file dal
  menu contestuale.
- Vista **History** più ricca: filtri per autore/path, diff a doppio pannello.
- Tutto coerente col tema VS (chiaro/scuro), icone vettoriali invece di emoji.

### 3.3 Architettura target (per renderlo pulito + testabile)

```
Commands  ──▶  ToolWindow  ──▶  ViewModel (sottile, solo UI state)
                                   │  (DI)
                                   ▼
                         ISvnService / IAiService / ISettings
                                   │
                  ┌────────────────┴───────────────┐
            SvnService                         AiService
        (IProcessRunner astratto)         (HttpClient iniettato)
```

- ViewModel **sottile**: solo stato UI e binding; la logica SVN/AI vive nei servizi iniettati.
- `IProcessRunner` astrae l'esecuzione di `svn.exe` → **arg-building unit-testabile** senza SVN reale.
- Servizi registrati nel container DI di `VisualStudio.Extensibility` e iniettati ovunque.

### 3.4 Come testarlo facilmente in locale (punto chiave per te)

Idea: uno **script di bootstrap** che crea un ambiente SVN locale completo **senza server**,
sfruttando il protocollo `file:///` (lo stesso già usato dai test):

```
tools/setup-local-svn.ps1
  1. svnadmin create  <repo locale>           → un repository SVN finto sul disco
  2. crea struttura standard trunk/branches/tags
  3. svn checkout file:///<repo>  <sample-app> → una working copy con una piccola app demo
  4. commit iniziale
  5. stampa il path della working copy da aprire nell'istanza sperimentale di VS
```

Workflow di test:
1. Lanci lo script una volta → ottieni una working copy SVN locale con dentro una mini-solution demo.
2. **F5** in VS → si apre l'istanza sperimentale con SVNAssist caricata.
3. Apri la solution demo (che è dentro la working copy) → l'estensione fa auto-detect.
4. Modifichi file, provi commit/update/diff/log/branch su un repo **vero ma 100% locale**.

Così puoi vedere il comportamento reale senza dipendere dal tuo SVN aziendale, e ripristinare
l'ambiente in un secondo cancellando la cartella. In alternativa, l'estensione può semplicemente
puntare a una working copy SVN che **mappi tu** (come chiedevi): lo script è solo per avere un
ambiente usa-e-getta riproducibile.

---

## 4. Roadmap proposta (ordine consigliato)

1. **Pulizia base**: rimuovere `pp.xml` + `.gitignore`; introdurre DI di `ISvnService`/`IAiService`
   nel ViewModel; correggere il bug dell'`HttpClient` condiviso.
2. **Testabilità**: astrarre `IProcessRunner`; unit test su arg-building + sui comandi del ViewModel.
3. **Ambiente di test locale**: script `setup-local-svn.ps1` + mini app demo nel repo.
4. **Hardening**: `ArgumentList`, fix `svn:ignore`, refresh unificato con debounce.
5. **Feature UX**: update su singola folder, history più ricca, branch/switch/merge (incrementale).
6. **Polish**: icone vettoriali, temi, dropdown provider AI, credenziali per provider.

---

## 5. Decisioni prese

- **Si parte da**: pulizia + testabilità (DI, fix bug, astrazione del process runner).
- **Test locale**: script di bootstrap che crea un repo SVN `file:///` usa-e-getta + mini app demo.
- **Scope SVN**: core quotidiano (status, commit, update intero+folder, revert, add, delete, diff,
  log, resolve, cleanup).
- **AI**: multi-provider OpenAI-compatibile **+ supporto nativo Claude** (formato `/v1/messages`).

## 6. Stato avanzamento

- [x] `pp.xml` rimosso dal versionamento + `.gitignore`
- [x] Fix bug concorrenza `HttpClient` (auth per-richiesta in `AiService`) + test
- [x] Astrazione `IProcessRunner` + `ProcessRunner` + `ArgumentList` in `SvnService` (no escaping manuale)
- [x] `SvnLocator` (ricerca svn.exe centralizzata) — rimossa duplicazione
- [x] Unit test arg-building con runner finto (8 test, girano senza SVN)
- [x] `IAiServiceFactory` + `ISvnAssistSettings` (DIP); ViewModel inietta `ISvnService`/factory/settings
- [x] Rimosso `new SvnService()` dai ViewModel; tool window = composition root
- [x] Verifica `svn.exe` all'avvio + proposta di installazione (winget/SlikSVN o pagina download)
- [x] `InternalsVisibleTo` + test factory AI
- [x] **Build + 24/24 test verdi** (verificato con .NET 8 SDK + SlikSVN installati in locale)
- [x] **Commit message "zero setup"**: token GitHub auto-rilevato (`gh auth token` → `GITHUB_TOKEN`/`GH_TOKEN`),
      endpoint+modello di default, Options solo come override avanzato (`GitHubTokenResolver` + test)
- [ ] Script `tools/setup-local-svn.ps1` + mini app demo
- [ ] Test handler ViewModel (ergonomia `AsyncCommand` da verificare)
- [ ] Provider AI da dropdown (P6) + Claude nativo (`/v1/messages`)
- [ ] Feature core: update su singola folder, history più ricca

## 7. AI e GitHub Copilot — cosa è fattibile (indagine sull'SDK 17.14)

Verificato ispezionando le assembly dell'SDK installato (`microsoft.visualstudio.extensibility.*`):

- **Nessuna API `LanguageModel` / `ChatCompletion` / `ChatRequest`** è esposta dal modello
  out-of-process. → Non è possibile invocare i modelli di GitHub Copilot dell'utente loggato
  in modo programmatico/trasparente da questa estensione.
- Esiste solo un riferimento `GitHubCopilot` (un accessor di proprietà nei contracts), che
  appare come **marker/metadati**, non come API di chat utilizzabile.

**Conclusione pratica per i commit message:**
- Strada gratuita e supportata = **GitHub Models** con un PAT GitHub (gratuito); `gpt-4o-mini`
  è più che sufficiente per i commit. È già il default del progetto.
- Per ridurre l'attrito di setup, opzione: **auto-rilevare un token via `gh auth token`** se
  GitHub CLI è installato e l'utente è loggato → niente PAT da incollare a mano.
- "Riusare il Copilot di VS senza chiavi" resta **non disponibile** finché l'SDK non espone
  un'API Language Model; da rivalutare a ogni aggiornamento dell'SDK.

> ⚠️ **Nota toolchain**: la macchina di sviluppo non aveva .NET SDK né SVN; sono stati
> installati .NET 8 SDK e SlikSVN via winget per buildare e testare. Build/test girano con
> SDK .NET 8+ e i command-line tools SVN nel PATH.
