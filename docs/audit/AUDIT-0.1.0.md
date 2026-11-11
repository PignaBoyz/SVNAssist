# Audit SVNAssist 0.1.0

> Data: 2026-11-11. Audit di sola lettura (nessun codice modificato). Letti: README, DESIGN, docs/FUNZIONALITA, docs/ROADMAP, docs/demo, tutto `src/`, struttura di `tests/`.
> **Limite:** nessun Visual Studio né .NET SDK in questo ambiente. Build, test e rendering Remote UI **non sono stati eseguiti**. Le affermazioni marcate *(da verificare)* richiedono una prova con F5 o una build su Windows.
> Percorsi relativi a `src/SVNAssist/` salvo dove indicato.

---

## Top 10 priorità

1. **Modello Claude di default = Opus** (`Services/SettingsService.cs:102` e `:238`). Costoso e non sempre disponibile (Claude Code su piano Pro, chiavi API senza accesso a Opus). In `auto` Claude Code viene prima di Claude API e di OpenAI-compatibile (`AiServiceFactory.cs:83`), quindi chi ha `claude` installato consuma quota Opus per ogni commit message. Passare a Sonnet (o Haiku). Vedi sezione 3.
2. **Modelli come testo libero, in 3 campi diversi** (`ClaudeModel`, `CopilotModel`, `AiModel`). Nessun elenco, nessuna validazione, un refuso dà solo "Errore AI" al primo uso. Servono dropdown con voce "Personalizzato...".
3. **Tema: testo nero su nero probabile** nella finestra SVN Log (`ToolWindows/SvnLogToolWindowControl.xaml` non ha alcuno stile tematizzato e usa `Foreground="Gray"` fisso) e nei `TextBlock` senza `Foreground` del pannello Changes. Da verificare con F5 in tema scuro. DESIGN.md §9 già lo prevede come problema atteso.
4. **Default `gpt-4o-mini` probabilmente non valido su `models.github.ai`** (`SettingsService.cs:125`; README). Il catalogo del nuovo endpoint usa id con prefisso editore (`openai/gpt-4o-mini`) *(da verificare con una chiamata reale)*. Se confermato, il percorso "gratuito, consigliato" fallisce. README dice anche "nessuno scope sul PAT": verificare (potrebbe servire `models:read`).
5. **Il setting "Working Copy Path" è quasi inutile e la doc lo descrive male.** L'auto-detect dalla solution vince sempre (`ToolWindows/SvnStatusToolWindowData.cs:322-327`); il setting vale solo se la solution non è in una WC, e la finestra Log non lo legge mai (`SvnLogToolWindowData.cs`).
6. **Il checklist dei prerequisiti non dice nulla su modello, costo, abbonamento.** `provider == "copilot"` risulta "usabile" anche senza login/runtime (`Services/PrerequisitesService.cs:157`). Il login di Claude Code non è verificabile.
7. **Se l'AI non è configurata, il prompt propone sempre la pagina del token GitHub** anche se l'utente ha scelto `claude` o `claudecode` (`AiSetupPrompt.cs`, chiamato da `CommitMessageViewModel.cs:187`). Messaggio non adattato al provider scelto.
8. **Albero come `ListBox` appiattita**: ogni riga istanzia 3 layout sovrapposti + 3 `ContextMenu`, nessuna tastiera (frecce), nessuna selezione di riga reale, e quando cambia la forma della lista `Rows.Clear()+AddRange` (`SvnStatusToolWindowData.cs:481-482`) azzera lo scroll a ogni refresh del watcher. Dettagli in sezione 2.
9. **Flag `Can*` di `ChangeRowViewModel` senza notifica** (`ChangeRowViewModel.cs:86-106`, copiati in `:204-209`): dopo un refresh a forma invariata il client Remote UI non vede i cambi, quindi menu contestuali e pulsante Diff possono restare abilitati/disabilitati in modo errato.
10. **Release/CI assenti**: nessun workflow GitHub Actions; `releases/SVNAssist-0.1.0.zip` (13 MB) è tracciato in git nonostante `.gitignore` (`[Rr]eleases/`); `AssemblyVersion`/`FileVersion` duplicati a mano nel csproj. Vedi sezione 5.

---

## 1. Bug di correttezza (per gravità)

**Alta**

- **A1. Default Opus + catena `auto`** (vedi Top 10 n.1). Non è un crash ma un bug di costo/disponibilità: `claude -p --model claude-opus-5-5` fallisce con errore di modello non disponibile sui piani senza Opus, e `FallbackAiService` passa silenziosamente al provider successivo (il diff finisce a un altro servizio; la conferma iniziale elenca la catena, ma l'errore del primo non è visibile se il secondo risponde).
- **A2. Default modello OpenAI-compatibile non valido per GitHub Models** *(da verificare)*: `Services/SettingsService.cs:125`, README "AI Model `gpt-4o-mini`", `Phi-3-medium-128k-instruct` senza prefisso editore.

**Media**

- **M1. `max_tokens: 200` hard-coded** (`Services/AiService.cs:91`). Scenario: i modelli OpenAI recenti (serie "o", GPT-5) rifiutano `max_tokens` e vogliono `max_completion_tokens` → HTTP 400 *(da verificare)*; i modelli con ragionamento esauriscono 200 token prima del testo e restituiscono vuoto/troncato; un messaggio con corpo lungo viene tagliato a metà frase.
- **M2. `Effort.Low` sempre inviato** (`Services/AnthropicAiService.cs:41`). Scenario: se l'utente imposta un modello Haiku/Sonnet che non supporta il parametro `effort`, l'API risponde 400 *(da verificare per ogni modello)*. Il README suggerisce proprio `claude-haiku-5-5`.
- **M3. Flag `Can*` stale** (`ToolWindows/ChangeRowViewModel.cs:86-106`, `:204-209`). Auto-property senza `SetProperty`: nel ramo `sameShape` di `ApplyEntries` (`SvnStatusToolWindowData.cs:473-478`) il server aggiorna i valori ma il client non riceve notifiche. Scenario: file `M` che diventa `!` (mancante) nella stessa sezione con la stessa chiave: il menu mostra le voci del vecchio stato.
- **M4. Setting "Working Copy Path" ignorato dall'auto-detect e dal Log** (Top 10 n.5). Scenario: l'utente imposta il percorso in Options, apre una solution in un'altra WC: viene mostrata quella della solution, senza avviso.
- **M5. Operazioni scartate in silenzio** (`SvnStatusToolWindowData.cs:589-604`, `:349-354`). Se un'operazione è in corso, Commit/Update/Revert/Refresh tornano senza messaggio: il click sembra non fare nulla (Update su WC grande, poi l'utente clicca Commit).
- **M6. Provider `copilot` "usabile" a prescindere** (`Services/PrerequisitesService.cs:157`). Il checklist dice "AI ok" anche senza `gh` loggato, senza runtime scaricato e senza abbonamento Copilot; l'errore arriva solo al primo "Genera".
- **M7. Messaggio d'aiuto AI non coerente col provider scelto** (Top 10 n.7).

**Bassa**

- **B1. `ProcessRunner`: scrittura su stdin dopo avvio** (`Services/ProcessRunner.cs:85-90`). Se `claude` termina subito (non loggato, argomento rifiutato), `WriteAsync` può lanciare `IOException` (pipe chiusa) e l'utente vede un errore di I/O invece dello stderr di claude. Vale per tutti i provider via CLI.
- **B2. Eccezione non osservata in `ReadCappedAsync`** (`ProcessRunner.cs:76-77`): se lo stdout supera il tetto e lancia, il task dello stderr può restare non osservato.
- **B3. `NewParentFolders` legge `_entries` senza lock** (`SvnStatusToolWindowData.cs:686`) mentre il watcher può sostituire la lista: corsa teorica, esito raro (cartella padre mancante nel commit → errore svn chiaro).
- **B4. `GetFileDiffAsync`/`GetDiffAsync`/`GetLogAsync` non applicano `Peg()`** (`Services/SvnService.cs:396`, `:601`, `:363`): un file con `@` nel nome (es. `logo@2x.png`) è interpretato come peg revision. Le altre operazioni sono protette (`:689`). *(da verificare con svn reale)*.
- **B5. Diff di file non versionato**: il pulsante è disabilitato (`CanDiff` falso), ma il diff di cartella e `SeDiffCommand` su un non versionato mostrano un errore svn grezzo invece di un messaggio guidato (`SvnService.cs:601`).
- **B6. Diff di cartella = N processi svn in serie** (fino a 100, `SvnStatusToolWindowData.cs:1044-1049`), senza progresso: UI ferma su "Caricamento diff...".
- **B7. Watcher: `Dispose` fuori dal lock** (`SvnStatusToolWindow.cs:232`) e polling `QuerySolutionAsync` ogni 5 s anche a finestra nascosta (`:31`, `:150`).
- **B8. Console Claude**: `ConsoleLauncher` con `claudeExePath` che contiene `&` o `%` non è riparato dal doppio apice (`Services/ConsoleLauncher.cs:162`); improbabile, nota.
- **B9. Log**: "Carica altre 50" ricarica tutto da zero e azzera selezione e diff (`SvnLogToolWindowData.cs` `LoadAsync`); il messaggio multiriga viene mostrato su una riga sola.

**Verificati e OK** (per non rifarlo): precedenza `item is "added" or "deleted"` a `SvnService.cs:137` è corretta; `--non-interactive` aggiunto a ogni sottocomando (`:890`); messaggio di commit via file UTF-8; `ArgumentList` ovunque; regex dell'output di `update` coerente con il formato svn 1.8+.

**Documentazione non allineata al codice**

- `docs/ROADMAP.md` è datata 2026-11-10 e descrive come mancanti cose ora presenti (conferma di Revert/Delete, esito di Update, test dei ViewModel: esistono `StatusViewModelTests.cs` e `HardeningTests.cs`).
- `DESIGN.md` §7 dice che Copilot non è utilizzabile (ora implementato via `GitHub.Copilot.SDK`); §8 ha voci già fatte; mancano gli ultimi punti in §6.
- README: tabella "Impostazioni" elenca 5 settings su 9 (mancano Provider, Claude Model, Anthropic API Key, Copilot Model); dice "si ancora di solito a destra" ma `Placement = Floating` (`SvnStatusToolWindow.cs:73`); non cita la conferma di invio dati prima dell'AI.
- `.github/copilot-instructions.md:333` cita modelli Claude vecchi.

---

## 2. UX

### 2.1 WPF (Remote UI) vs docs/demo

La demo (`docs/demo/svnassist-demo.html`, 156 KB, un solo file) replica fedelmente struttura e testi del pannello. Differenze reali:

| Aspetto | Demo HTML | Estensione WPF |
|---|---|---|
| Diff | Colorato (add/del/hunk), mono 11.5px | `TextBox` monocromatico, nessuna colorazione |
| Intestazioni di sezione | Sticky durante lo scroll | Scorrono via |
| Righe | Hover, riga attiva, riga col menu contestuale evidenziate | Nessun hover; selezione `ListBoxItem` separata dalle checkbox |
| Pulsante Commit | Primario (blu) | Pulsante normale in grassetto |
| Pulsante Diff | Il nome del file apre il diff; nessun pulsante dedicato | Pulsante "Diff" su ogni riga file (rumore visivo) |
| Larghezza stretta | Container query: nasconde colonne | Nessun adattamento |
| Catena provider, barra download Copilot | Chip `ok/fail/skip` e progress bar (marcati "dettaglio demo") | Solo testo `AiStatus`; nessuna barra di progresso |
| Tema | Palette scura fissa | Tema VS (se i colori sono impostati, vedi 2.2) |
| Menu contestuali | HTML posizionati | `ContextMenu` WPF, associati con il trucco `Tag` |
| Placement | Dock a destra | Floating (`SvnStatusToolWindow.cs:73`) |
| Tab "Prossimamente", tour, pannello simulazione | Presenti | Non applicabile |

Raccomandazione: dichiarare nella demo cosa è "solo demo" in modo uniforme, oppure (meglio) eliminare la divergenza adottando WebView2 (sezione 4).

### 2.2 Testo illeggibile / tema

- `SvnLogToolWindowControl.xaml`: nessuno stile `ThemedDialog*` su `Button`, `TextBox`, `ListBox`; `Foreground="Gray"` fisso alla colonna data; ogni `TextBlock` senza foreground. Probabile finestra "chiara a metà" in tema scuro.
- `SvnStatusToolWindowControl.xaml`: gli stili tematizzati ci sono per `TextBox/Button/CheckBox/ComboBox/ListBox` (righe 14-22) ma **non per `TextBlock`** né per la radice `Grid` (nessun `Background` né `TextElement.Foreground`). Senza `Foreground`, questi testi ereditano il default WPF *(da verificare se Remote UI lo tematizza da solo)*: "Working Copy:" (65), branch (76), titolo e righe del checklist (98-135), `AiStatus` (216), "Nessuna modifica." (442), `DiffTitle` (473), `StatusMessage` (483).
- Soluzione senza toccare la logica: `TextElement.Foreground="{DynamicResource {x:Static styles:VsBrushes.WindowTextKey}}"` e `Background` `VsBrushes.WindowKey` sulla radice; stesso trattamento (stili tematizzati) nel Log.
- Colori degli stati (`StatusPresentation.Color`: `CornflowerBlue`, `DarkOrange`, `MediumSeaGreen`...) accettabili su scuro, da verificare su chiaro; `DarkOrange`/`MediumSeaGreen` nel checklist sono contrasto basso su bianco. Meglio token tematici o palette con contrasto verificato (WCAG).
- Alto contrasto: nessun supporto esplicito.

### 2.3 Albero

- `ListBox` piatta con tre template sovrapposti (sezione/cartella/file) tenuti nascosti con `DataTrigger`: ogni riga costa 3 pannelli e 3 `ContextMenu`; con 1000+ file (non versionati di `bin/obj`) il pannello rallenta *(da misurare)*.
- Nessuna tastiera: frecce, Spazio per spuntare, Invio per il diff, Tab ordinato. Accessibilità scarsa (chevron è un `Button` con glifo, senza nome accessibile).
- Cliccare il nome di una cartella la espande/comprime (non seleziona); nessuna selezione multipla (le azioni contestuali sono su una riga alla volta).
- Perdita dello scroll quando la lista cambia forma (`Rows.Clear()`).
- "Seleziona tutto" spunta **anche i non versionati** (`SetAllSelectedAsync`, `:526-539`): un clic e un commit possono aggiungere `bin/obj`. All'apertura i non versionati non sono spuntati, quindi il comportamento è incoerente. Meglio: "Seleziona tutto" solo su Modifiche, avviso se si committano non versionati.
- Vista Lista: la colonna cartella grigia (`Detail`) può sparire per troncamento senza tooltip utile oltre al percorso completo.
- Nessuna ricerca/filtro per nome, nessun contatore dei selezionati vicino al pulsante Commit ("Commit (3)").

### 2.4 Impostazioni confuse

- 9 settings in una pagina piatta: 3 modelli (Claude, Copilot, OpenAI), 2 chiavi (Anthropic, AI API Key), endpoint, provider, lingua, working copy. Etichette tipo "(solo OpenAI-compatibile)" aiutano ma non nascondono i campi irrilevanti.
- "AI API Key" vs "API key Anthropic": nomi quasi uguali. La prima serve anche per token GitHub.
- Chiavi in chiaro nello store (noto, D17); `Setting.String` non ha tipo password.
- "Working Copy Path": vedi M4.
- Stringhe in `.vsextension/string-resources.json`; il README usa nomi inglesi ("AI Model", "AI Endpoint") diversi dalle etichette italiane reali.
- Il pannello non mostra quale provider/modello verrà usato prima di cliccare "Genera con AI" (lo dice solo dopo, e nel dialogo di conferma la prima volta).

---

## 3. Configurazione modelli AI

### 3.1 Dove si configura

| Cosa | Dove | Default | Tipo |
|---|---|---|---|
| Provider | `SettingDefinitions.AiProvider` (`SettingsService.cs:84`) | `auto` | **Dropdown** (`Setting.Enum`): auto, copilot, claudecode, claude, openai |
| Modello Claude (Claude Code **e** Claude API) | `ClaudeModel` (`:98-102`); fallback `:238` | **`claude-opus-5-5`** | **Testo libero** |
| Modello Copilot | `CopilotModel` (`:115`) | vuoto = predefinito Copilot | Testo libero |
| Modello OpenAI-compatibile | `AiModel` (`:125`) | `gpt-4o-mini` | Testo libero |
| Endpoint | `AiEndpoint` (`:55`) | `https://models.github.ai/inference/chat/completions` | Testo libero |
| Chiavi | `AiApiKey` (`:73`), `AnthropicApiKey` (`:107`) | vuote | Testo in chiaro |
| Lingua | `DefaultLanguage` | `auto` | Dropdown |

Altre occorrenze del default Opus da cambiare insieme: `Services/SettingsService.cs:102,238`, `Services/AnthropicAiService.cs:20` (commento) e `:14` (elenco fallback), `.vsextension/string-resources.json:17`, README riga 115, `docs/demo/svnassist-demo.html:708,1813,1826`, `docs/FUNZIONALITA.md:154`, test `AiProvidersTests.cs` e `AiServiceFactoryTests.cs:23`.

**Conferma: il default è Opus e non è ragionevole** per un commit message (output breve, input fino a ~100.000 caratteri inviati al modello, `Effort.Low` già richiesto). Per l'API si vuole Sonnet (id `claude-sonnet-5-5`) o Haiku (`claude-haiku-5-5`, citato nel README) come default; per Claude Code l'alias `sonnet`/`haiku` è più stabile degli id completi *(da verificare per la versione installata della CLI)*.

### 3.2 Raccomandazioni

- Default: Sonnet. Il README stesso dice che Haiku "costa molto meno": valutare Haiku come default e Sonnet come "qualità".
- Dropdown (`Setting.Enum` esiste già in uso) con elenco curato per provider + voce "Personalizzato..." che attiva un campo testo. Un unico setting "Modello" con valori diversi per provider crea ambiguità; meglio mantenere i campi separati ma come dropdown.
- Un solo punto per il default (costante condivisa), non 3 stringhe duplicate.
- Mostrare nel pannello, vicino a "Genera con AI", il provider e il modello effettivi (tooltip o etichetta).
- Eliminare `Effort.Low` per i modelli che non lo supportano, oppure gestirlo per modello.
- Aggiornare `max_tokens` del provider OpenAI-compatibile (M1).

### 3.3 Prerequisiti per provider e informazione all'utente

| Provider | Serve | Il pannello lo dice? |
|---|---|---|
| `copilot` | Abbonamento GitHub Copilot, `gh auth login` (o `GITHUB_TOKEN`), download runtime ~100 MB al primo uso (con `auto` solo se già scaricato); le richieste possono consumare "premium requests" | Parziale: il checklist mostra gh, login, runtime; **non** cita l'abbonamento né le premium requests; il provider è "usabile" anche senza nulla (M6) |
| `claudecode` | CLI `claude` installata **e** login (`/login`) con abbonamento; il modello scelto deve essere incluso nel piano | Mostra se `claude.exe` c'è; **login non verificabile** (dichiarato); nessun avviso sul piano/modello |
| `claude` (API) | Chiave Anthropic (impostazione o `ANTHROPIC_API_KEY`) con credito a pagamento; accesso al modello | Mostra la fonte della chiave; **non** avvisa del costo né dell'accesso al modello (Opus!) |
| `openai` | Endpoint + chiave; per GitHub Models un token GitHub (anche da `gh auth token`) e un id modello valido; limite ~8.000 token/richiesta | Mostra "usabile" se c'è chiave o token; non spiega l'id modello né i limiti |
| `auto` | Il primo disponibile in ordine Copilot, Claude Code, Claude API, OpenAI; se uno fallisce passa al successivo | Il dialogo di conferma elenca la catena; il checklist riassume "ne basta uno" |

Lacune: nessun test di connessione ("Prova provider" con un diff finto), nessuna indicazione di costo/quota, il messaggio di errore finale riporta le cause di tutti i provider ma non guida alla correzione, `AiSetupPrompt` propone solo il token GitHub (M7).

---

## 4. Fattibilità: UI WebView2 che riusa docs/demo

### 4.1 Vincolo di fondo

L'estensione è **out-of-process** (`VisualStudio.Extensibility`, .NET 8). Il modello Remote UI renderizza solo XAML in un `DataTemplate`, senza controlli personalizzati: **non può ospitare un `WebView2`** *(da verificare sulla documentazione SDK 17.14; non esiste un controllo web nel set elencato in `docs/ROADMAP.md` §"Limiti Remote UI")*. Servono quindi o un pezzo in-process o una finestra che si apre fuori da VS.

### 4.2 Opzioni

- **A. Estensione in-process (net472) con `ToolWindowPane` WPF + `WebView2`.** Strada collaudata da molte estensioni. Costi: ritarget del progetto a .NET Framework, perdita del vantaggio "VS non crasha"; `GitHub.Copilot.SDK`, `System.Formats.Tar`, `Process.Kill(entireProcessTree)` e C# 12 vanno verificati su net472 *(da verificare, rischio alto)*.
- **B. Ibrida (consigliata da provare in uno spike).** Si tiene il backend out-of-process (net8: `ISvnService`, AI, view model, test) e si aggiunge un piccolo componente in-process che crea la tool window con il solo `WebView2`. Il ponte tra i due è una pipe con nome o un WebSocket su loopback con token casuale. Oppure, se `ExtensionConfiguration.RequiresInProcessHosting` e i servizi in-process dell'SDK lo permettono *(da verificare)*, un'unica estensione in-proc con la logica in una libreria `netstandard2.0`.
- **C. Server locale + browser esterno.** Il più semplice, ma esce da VS: non adatto a un pannello Git Changes.

### 4.3 File e viste che cambierebbero

- Rimossi/sostituiti: `ToolWindows/SvnStatusToolWindowControl.xaml/.cs`, `SvnLogToolWindowControl.xaml/.cs`, il modo di costruire `IRemoteUserControl` in `SvnStatusToolWindow.cs:81-89` e `SvnLogToolWindow.cs`.
- Da adattare ma in gran parte riusabili: `SvnStatusToolWindowData.cs`, `CommitMessageViewModel.cs`, `PrerequisitesViewModel.cs`, `ChangeRowViewModel.cs` perdono `[DataContract]`/`NotifyPropertyChangedObject`/`IAsyncCommand` e diventano uno "store" con `GetSnapshot()` JSON e `Dispatch(name, args)`. I gestori (`CommitCoreAsync`, `RevertCoreAsync`...) sono già metodi `internal` testati con servizi finti, quindi cambia solo il collegamento.
- `IDialogService`/`VsDialogService`: le conferme possono diventare modali HTML (la demo ha già `#overlay/.modal`) con richiesta/risposta via id.
- `.csproj`: nuova dipendenza `Microsoft.Web.WebView2`, rimozione degli `EmbeddedResource` XAML, aggiunta della cartella `ui/` come contenuto del VSIX.
- Demo: spezzare `svnassist-demo.html` in `ui/` (pannello reale: `statusHTML`, `treeHTML`, `prereqBoxHTML`, vista log, impostazioni) e `demo/` (finta VS, svn simulato, tour, pannello simulazione), con un'interfaccia `bridge.send(cmd,args)` / `bridge.onState(cb)`. Nella demo `bridge` parla con lo `S` in memoria; nell'estensione con il backend. Una build copia `ui/` in entrambi: demo ed estensione usano **gli stessi file**.

### 4.4 Flusso messaggi C# / JS

- JS → C#: `chrome.webview.postMessage({id, type:"cmd", name:"commit", args:{rowKey, ...}})`; C# `CoreWebView2.WebMessageReceived` → `Dispatch`.
- C# → JS: `PostWebMessageAsJson({type:"state", rev, state})` per gli aggiornamenti (snapshot, o patch per le righe), `{type:"event", name:"toast"|"confirm"|"theme"}` per eventi; risposte `{type:"reply", id, ok, result|error}`.
- Il `render()` della demo ricostruisce già l'HTML da uno stato e salva/ripristina il focus (`saveFocus/restoreFocus`): si adatta bene al modello "snapshot dallo store".
- Sicurezza: JS non deve mai mandare percorsi; manda `rowKey` e C# li risolve da `_entries` (oggi i comandi ricevono già `ChangeRowViewModel`). Contenuto servito da cartella locale con `SetVirtualHostNameToFolderMapping`, navigazione esterna bloccata (`NavigationStarting`), CSP restrittiva, controllo dell'origine dei messaggi.
- Tema: C# invia i colori VS (`EnvironmentColors`/`VSColorTheme`) come variabili CSS; la demo oggi ha una palette scura fissa (`:root` in cima al file), da trasformare in token.

### 4.5 Rischi

- Possibilità reale di dover passare a in-process (A/B): è il rischio maggiore, da chiarire con uno spike di 1-2 giorni.
- Runtime WebView2: presente su Windows 11 e normalmente installato con VS *(da verificare)*; gestire l'assenza con un messaggio. Cartella dati utente scrivibile (`%LOCALAPPDATA%\SVNAssist\webview2`).
- Focus e scorciatoie: VS può "mangiare" tasti, tab-order tra finestre, `Ctrl+C/V` nella textarea; problemi noti di WebView2 in tool window dock/float.
- Prestazioni e memoria: un processo WebView2 aggiuntivo per finestra (~decine di MB).
- Duplicazione di test: la UI HTML non è coperta; opportunità di test con Playwright sulla pagina con `bridge` finto (la demo diventa fixture).
- Perdita di integrazione nativa (accessibilità di sistema, scrollbar tematizzate, DPI): curare CSS e ARIA (la demo ha già parte degli ARIA).
- Doppio sviluppo durante la migrazione: tenere Remote UI come fallback finché la nuova UI non è pari.

---

## 5. Build e release

### 5.1 Stato attuale

- `src/SVNAssist/SVNAssist.csproj`: SDK-style, `net8.0-windows8.0`, `UseWPF`, `LangVersion 12`. Pacchetti: `Microsoft.VisualStudio.Extensibility.Sdk` e `.Build` 17.14.40608, `Anthropic` 13.8.0, `GitHub.Copilot.SDK` 1.0.31, `MessagePack` 2.5.307 (solo risoluzione, `ExcludeAssets=runtime`). Il VSIX è prodotto dal pacchetto `.Build` durante `dotnet build` *(da verificare dove finisce: tipicamente `bin/<config>/net8.0-windows8.0/*.vsix`)*; `releases/SVNAssist-0.1.0.zip` contiene un `.vsix` da 13 MB, quindi la build locale funziona.
- `CopilotSkipCliDownload=true` evita il runtime da ~100 MB nel VSIX; la versione richiesta (`CopilotCliVersion`) viene letta a runtime da metadata dell'assembly (`CopilotRuntimeInstaller`) e confrontata con hash fissati nel sorgente. Quando si aggiorna `GitHub.Copilot.SDK` bisogna aggiornare anche `PinnedHashes` (altrimenti il test/ il download falliscono).
- Versione: `<Version>`, `<AssemblyVersion>`, `<FileVersion>` scritte a mano (0.1.0, 3 punti da cambiare); `SVNAssistExtension.cs` usa `ExtensionAssemblyVersion`.
- Progetto test: `net8.0-windows8.0`, xUnit, riferimenti a pacchetti Extensibility; i test di integrazione richiedono `svn` e `svnadmin` nel PATH.
- Soluzione con configurazioni x86/x64 mappate su "Any CPU": rumorose ma innocue.
- Nessun workflow: `.github/` contiene solo `copilot-instructions.md`.

### 5.2 Si può costruire su `windows-latest`?

Molto probabilmente sì *(non provato)*: il progetto è puro MSBuild + NuGet (nessun VSSDK/`.vsct`/Visual Studio installato richiesto dal modello Extensibility), il runner ha .NET 8 disponibile o installabile con `actions/setup-dotnet`, e `UseWPF` richiede solo Windows. Da verificare alla prima esecuzione: che il pacchetto `.Build` produca il `.vsix` senza VS, e la risoluzione NuGet (il progetto test cita versioni NU1603 già sistemate).

### 5.3 Cosa serve a un workflow

1. Runner `windows-latest`; `actions/checkout`; `actions/setup-dotnet` con `8.0.x`; cache NuGet (`actions/cache` su `~/.nuget/packages`, chiave sui `.csproj`).
2. Installare Subversion per i test (`choco install sliksvn -y` o winget; i runner Windows non lo garantiscono) e aggiungerlo al PATH.
3. `dotnet restore`, `dotnet build -c Release --no-restore`, `dotnet test -c Release --no-build --logger trx`; pubblicare i risultati.
4. Rendere la versione unica: eliminare `AssemblyVersion`/`FileVersion` duplicati e passare `-p:Version=${GITHUB_REF_NAME#v}` (altrimenti il tag non cambia il manifest).
5. Artifact: `actions/upload-artifact` del `.vsix` (percorso da confermare dopo la prima build).
6. Release su tag `v*`: `gh release create` o `softprops/action-gh-release` allegando `.vsix`, demo HTML e il testo di LEGGIMI; smettere di tracciare `releases/*.zip`.
7. Opzionale: firma del VSIX (`OpenVsixSignTool` con certificato in secrets) e pubblicazione su Marketplace (manifest di pubblicazione, `VsixPublisher` con PAT) — già nella roadmap come P1/P2.
8. Opzionale: job separato che pubblica la demo su GitHub Pages (utile se la UI diventa condivisa con l'estensione).

### 5.4 Igiene repo

- `releases/SVNAssist-0.1.0.zip` tracciato pur con `[Rr]eleases/` in `.gitignore`: aggiunto con `-f`; gonfia la storia (13 MB per release). Spostare su GitHub Releases.
- Il nome editore `Andrea Pinelli` è nel codice (`SVNAssistExtension.cs`): verificare che sia quello voluto per il Marketplace.

---

## Appendice: cosa non è stato verificato

- Nessuna build, nessun test eseguito; nessun avvio in VS.
- Rendering effettivo dei colori in Remote UI (2.2) e comportamento dell'albero su WC grandi.
- Validità degli id modello e dei limiti su GitHub Models; parametri supportati per modello su API Claude/OpenAI.
- Percorso reale del `.vsix` prodotto e possibilità di build senza VS installato.
- Disponibilità di un controllo web in Remote UI o di `RequiresInProcessHosting` nell'SDK 17.14.
