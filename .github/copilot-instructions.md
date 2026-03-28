# SVNAssist — Copilot Instructions

## Cos'è questo progetto

Estensione VSIX per Visual Studio 2026 (modello **VisualStudio.Extensibility out-of-process**) in C# 12 / .NET 8.
Integra Subversion (SVN) nell'IDE tramite `svn.exe` via CLI e genera commit message automatici tramite API AI multi-provider.
Progetto didattico — il codice deve essere chiaro e commentato.

---

## Architettura — modello attuale

L'estensione usa il **nuovo modello VisualStudio.Extensibility**, NON il vecchio VSSDK:

| Aspetto | Vecchio (VSSDK) | Attuale (Extensibility) |
|---|---|---|
| Entry point | `AsyncPackage` | `Extension` (`SVNAssistExtension.cs`) |
| Comandi | `.vsct` + GUID + `OleMenuCommandService` | `[VisualStudioContribution]` + `Command` class |
| Tool window | `ToolWindowPane` + WPF code-behind | `ToolWindow` + Remote UI (`DataTemplate` XAML) |
| Settings | `IVsWritableSettingsStore` / `DialogPage` | `SettingCategory` + source-generated observer |
| Processo | In-process (dentro `devenv.exe`) | Out-of-process (processo separato .NET 8) |
| SVN | SharpSVN (nativo, .NET Framework) | `svn.exe` via `Process` (cross-platform) |
| Manifest | `source.extension.vsixmanifest` | Generato automaticamente dal source generator |
| Risorse stringa | `.resx` | `.vsextension/string-resources.json` (formato `{ "key": "value" }`) |

**Non usare** API del vecchio modello: niente `AsyncPackage`, niente `.vsct`, niente `OleMenuCommandService`, niente `IVsWritableSettingsStore`, niente SharpSVN.

---

## Regole generali — seguile sempre

- Usa **C# 12 / .NET 8** con `<TargetFramework>net8.0-windows8.0</TargetFramework>`
- Ogni interfaccia va in un file separato (`ISvnService.cs`, `IAiService.cs`, ecc.)
- Tutto ciò che può essere async **deve** essere async con `Task` e `CancellationToken`
- `HttpClient` è un singleton — non crearne uno per ogni chiamata
- Non hardcodare API key, endpoint o path — tutto passa da `SettingsService`
- Quando usi API specifiche di VisualStudio.Extensibility, aggiungi sempre un commento XML `/// <remarks>` che spiega perché si usa quel meccanismo e quale sarebbe l'alternativa
- Le operazioni `svn.exe` sono bloccanti I/O — wrappale in `Task.Run()` o usa `Process` con redirect asincrono
- Remote UI: i data context devono avere `[DataContract]` e proprietà con `[DataMember]`; i comandi usano `IAsyncCommand`; l'elemento root XAML è `<DataTemplate>`

---

## Struttura del progetto — rispettala

```
SVNAssist/
├── SVNAssist.sln
├── .github/
│   └── copilot-instructions.md
├── src/
│   └── SVNAssist/
│       ├── SVNAssist.csproj
│       ├── SVNAssistExtension.cs              ← entry point (eredita Extension)
│       ├── .vsextension/
│       │   └── string-resources.json          ← risorse stringa (formato dizionario JSON)
│       ├── Commands/
│       │   ├── StatusCommand.cs
│       │   ├── CommitCommand.cs
│       │   ├── UpdateCommand.cs
│       │   └── ShowLogCommand.cs
│       ├── ToolWindows/
│       │   ├── SvnStatusToolWindow.cs
│       │   ├── SvnStatusToolWindowControl.cs
│       │   ├── SvnStatusToolWindowControl.xaml  (EmbeddedResource)
│       │   ├── SvnStatusToolWindowData.cs       (ViewModel)
│       │   ├── SvnLogToolWindow.cs
│       │   ├── SvnLogToolWindowControl.cs
│       │   ├── SvnLogToolWindowControl.xaml     (EmbeddedResource)
│       │   └── SvnLogToolWindowData.cs          (ViewModel)
│       ├── Dialogs/
│       │   ├── CommitDialogControl.cs
│       │   ├── CommitDialogControl.xaml          (EmbeddedResource)
│       │   └── CommitDialogData.cs              (ViewModel)
│       ├── Services/
│       │   ├── ISvnService.cs
│       │   ├── SvnService.cs                    (usa svn.exe via Process)
│       │   ├── IAiService.cs
│       │   ├── AiService.cs
│       │   └── SettingsService.cs               (usa SettingCategory + observer)
│       └── Models/
│           ├── SvnFileStatus.cs
│           ├── SvnLogEntry.cs
│           └── CommitOptions.cs
└── tests/
    └── SVNAssist.Tests/
        ├── SvnServiceTests.cs
        └── AiServiceTests.cs
```

---

## Entry point — SVNAssistExtension.cs

- Eredita da `Extension` (namespace `Microsoft.VisualStudio.Extensibility`)
- Ha l'attributo `[VisualStudioContribution]`
- Override di `ExtensionConfiguration` per definire metadata (id, version, displayName)
- Override di `InitializeServices(IServiceCollection)` per registrare servizi DI e `AddSettingsObservers()`
- **Non** registra comandi manualmente — il source generator li scopre automaticamente

---

## Comandi — Commands/

Ogni comando eredita da `Command` con `[VisualStudioContribution]`.
Il placement attuale è `CommandPlacement.KnownPlacements.ExtensionsMenu`.
Le label usano il pattern `%ResourceKey%` che risolve da `string-resources.json`.

---

## SvnService — usa svn.exe, NON SharpSVN

Implementa `ISvnService` con questi metodi:

```csharp
Task<IReadOnlyList<SvnFileStatus>> GetStatusAsync(string workingCopyPath, CancellationToken ct);
Task CommitAsync(IEnumerable<string> paths, string message, CancellationToken ct);
Task UpdateAsync(string workingCopyPath, CancellationToken ct);
Task<IReadOnlyList<SvnLogEntry>> GetLogAsync(string targetPath, int maxEntries, CancellationToken ct);
Task<string> GetDiffAsync(string targetPath, long revision, CancellationToken ct);
Task<string> GetWorkingCopyDiffAsync(string workingCopyPath, CancellationToken ct);
```

Regole:
- Usa `Process` con `RedirectStandardOutput`, `RedirectStandardError`, `CreateNoWindow = true`
- Leggi stdout e stderr in parallelo per evitare deadlock
- `svn status --xml` per lo status, `svn log --xml -l N` per il log
- `svn diff` (unified diff testuale) per i diff
- Cerca `svn.exe` nel PATH e nelle posizioni note (TortoiseSVN, SlikSVN, CollabNet)

---

## AiService

Implementa `IAiService`:

```csharp
Task<string> GenerateCommitMessageAsync(string diff, string language, CancellationToken ct);
Task<string> ImproveCommitMessageAsync(string draft, string diff, string language, CancellationToken ct);
```

- Usa `HttpClient` singleton iniettato dal costruttore
- Formato richiesta: OpenAI Chat Completions (`/v1/chat/completions`)
- `max_tokens: 200`

---

## SettingsService

Usa il sistema `SettingCategory` di VisualStudio.Extensibility con source-generated observer.
Le impostazioni appaiono automaticamente in **Tools → Options → SVNAssist**.

| Proprietà | Tipo | Default |
|---|---|---|
| `AiEndpoint` | string | `https://api.openai.com/v1/chat/completions` |
| `AiApiKey` | string | `""` |
| `AiModel` | string | `gpt-4o-mini` |
| `DefaultLanguage` | string | `auto` |
| `WorkingCopyPath` | string | `""` (auto-rilevato) |

---

## Test

- `SVNAssist.Tests` con xUnit
- `SvnService`: test con repository SVN locale (`svnadmin create`)
- `AiService`: test con mock HTTP (`RichardSzalay.MockHttp`)

---

## Stato attuale — cosa è stato completato ✅

1. ✅ Setup VSIX con VisualStudio.Extensibility — l'estensione si carica nell'istanza sperimentale
2. ✅ `ISvnService` + `SvnService` con tutti i metodi (status, commit, update, log, diff) via `svn.exe`
3. ✅ `ISvnService` + test base
4. ✅ Tool window Status (`SvnStatusToolWindow`) con lista file, checkbox, Refresh, Commit selezionati
5. ✅ `CommitDialog` modale con lista file, messaggio, bottoni AI, selettore lingua
6. ✅ `IAiService` + `AiService` con generazione e miglioramento commit message
7. ✅ Tool window Log (`SvnLogToolWindow`) con lista revisioni e pannello diff
8. ✅ `UpdateCommand`
9. ✅ `SettingsService` con settings observer (endpoint, API key, modello, lingua, working copy path)
10. ✅ Fix `string-resources.json` — convertito da array `[{key,value}]` a dizionario `{key: value}`
11. ✅ Repository SVN di test creato in `C:\dev\svn-test-repo` con branch `feature-test-svnassist`

---

## Backlog — cosa deve essere implementato 🔧

### P0 — BUG CRITICO: il comando Commit non produce output visibile

**Problema**: cliccando "Commit..." dal menu Extensions non succede nulla di visibile.
Il comando chiama `ShowToolWindowAsync<SvnStatusToolWindow>` ma la tool window potrebbe non apparire
(placement sbagliato, errore silente nell'inizializzazione, o la finestra si apre in un tab nascosto).

**Azioni richieste**:
- Verificare che `SvnStatusToolWindow` si inizializzi senza eccezioni
- Aggiungere logging diagnostico nel costruttore della tool window e nei comandi
- Verificare che il placement `ToolWindowPlacement.DocumentWell` sia corretto (potrebbe servire `Floating` o un dock specifico)
- Testare che `GetContentAsync` restituisca il controllo correttamente

---

### P1 — Auto-detect SVN working copy dalla solution caricata

**Requisito**: quando l'utente apre la tool window Status o clicca Commit, l'estensione deve:

1. Usare l'API `VisualStudioExtensibility` per ottenere il percorso della solution attualmente aperta
   (es. `Extensibility.Workspaces()` o query del ProjectSystem)
2. Dal percorso della solution, navigare verso l'alto nel filesystem cercando la cartella `.svn`
   (massimo **10 livelli** verso l'alto per evitare di risalire fino alla root del disco)
3. Se trova `.svn` → popolare automaticamente `WorkingCopyPath` e triggerare il refresh
4. Se **non** trova `.svn` entro il limite → mostrare il campo di input per il percorso manuale
5. Se nessuna solution è caricata → mostrare il campo di input manuale

**Implementazione suggerita**: creare un metodo statico `FindSvnRoot(string startPath, int maxLevels = 10)` in `SvnService`
che restituisce `string?` (null se non trovato).

---

### P2 — Menu a tendina dedicato "SVNAssist" sotto Extensions

**Requisito**: invece di avere i 4 comandi flat nel menu Extensions, creare un **sottomenu**:

```
Extensions
  └── SVNAssist ▸
        ├── Mostra Status
        ├── Commit...
        ├── Update
        └── Log / History
```

**Implementazione**: nel modello VisualStudio.Extensibility, usare `CommandPlacement` con un menu parent custom
oppure la proprietà `Placements` con un `CommandPlacement` che specifica un parent group definito dall'estensione.

---

### P3 — Auto-detect al momento dell'apertura del sottomenu

**Requisito**: quando l'utente clicca sul sottomenu SVNAssist, l'estensione deve lanciare la ricerca
della root SVN in background (se non già rilevata). I comandi devono essere:
- **Abilitati** se una working copy SVN è stata trovata
- **Disabilitati con tooltip** se nessuna repo SVN è stata trovata (chiedendo all'utente il path)

**Nota**: nel modello Extensibility, lo stato enabled/disabled si controlla con `CommandEnabledStatus`
e il metodo `GetCommandStateAsync()`.

---

### P4 — UI compatta stile "SVN Changes" (ispirata a Git Changes di VS)

**Requisito**: la tool window Status deve avere un layout compatto simile al pannello "Git Changes"
di Visual Studio, non una finestra piena con troppo spazio vuoto.

**Layout richiesto**:
```
┌─ SVN Changes ──────────────────────┐
│ 🔄  branch/feature-test  ↑Update  │  ← header compatto: icona refresh, nome branch, bottone update
│─────────────────────────────────────│
│ ✏️ Messaggio di commit...          │  ← TextBox messaggio inline (non in un dialog separato)
│ [Commit ▼] [🤖 AI]                │  ← bottone commit con dropdown, bottone genera AI
│─────────────────────────────────────│
│ Changes (3)                        │  ← header sezione con conteggio
│  ☑ M  src/File1.cs                 │  ← lista compatta: checkbox, stato, path relativo
│  ☑ M  src/File2.cs                 │
│  ☐ ?  NuovoFile.txt                │
└─────────────────────────────────────┘
```

**Differenze rispetto alla UI attuale**:
- Rimuovere il campo "Working Copy" visibile (auto-detected, mostrarlo solo se non trovato)
- Messaggio di commit **inline** nella tool window, non in un dialog modale separato
- Lista file più compatta: stato abbreviato (M, A, D, ?, !) con colore, path relativo alla working copy root
- Il pulsante "Commit" diretto nella tool window con dropdown per opzioni aggiuntive
- Mostrare il nome del branch SVN corrente nell'header

---

### P5 — Diff su doppio click di un file

**Requisito**: nella lista file della tool window Status, il doppio click su un file deve mostrare
il diff delle modifiche locali non committate per quel singolo file.

**Implementazione**:
1. Aggiungere un comando `ShowFileDiffCommand` nel ViewModel (tipo `IAsyncCommand`)
2. Il comando riceve il `FileStatusItem` come parametro
3. Eseguire `svn diff "percorso/file"` per ottenere il diff del singolo file
4. Mostrare il risultato in una nuova tool window o in un pannello inline sotto la lista
5. **Opzione preferita**: usare `ITextDocumentSnapshot` o aprire il file nell'editor VS — ma nel modello
   out-of-process il modo più semplice è mostrare il diff testuale in un `TextBox` read-only

**Aggiungere a `ISvnService`**:
```csharp
Task<string> GetFileDiffAsync(string filePath, CancellationToken ct);
```

---

### P6 — Selezione provider AI da dropdown (non inserimento manuale)

**Requisito**: invece di scrivere a mano l'endpoint AI nelle opzioni, fornire un sistema a cascata:

1. **ComboBox "AI Provider"** con provider pre-configurati:
   - OpenAI (`https://api.openai.com/v1/chat/completions`)
   - Anthropic Claude (`https://api.anthropic.com/v1/messages`)
   - Azure OpenAI (`https://{instance}.openai.azure.com/openai/deployments/{model}/chat/completions`)
   - GitHub Copilot (via Language Model API se disponibile)
   - Ollama locale (`http://localhost:11434/v1/chat/completions`)
   - Google Gemini (`https://generativelanguage.googleapis.com/v1beta/chat/completions`)
   - Mistral (`https://api.mistral.ai/v1/chat/completions`)
   - Personalizzato (campo endpoint manuale)

2. **ComboBox "Modello"** che si popola dinamicamente in base al provider selezionato:
   - Per OpenAI: `gpt-4o`, `gpt-4o-mini`, `gpt-4-turbo`, `gpt-3.5-turbo`
   - Per Claude: `claude-sonnet-4-20250514`, `claude-3.5-haiku`, `claude-3-opus`
   - Per Ollama: fetch dinamico da `GET /api/tags` per listare i modelli installati
   - Per provider custom: campo di testo libero

3. Se possibile, al cambio provider fare una chiamata `GET /v1/models` (endpoint standard OpenAI-compatible)
   per popolare la lista modelli in tempo reale

**Implementazione**: creare un modello `AiProviderDefinition` con proprietà `Name`, `EndpointTemplate`,
`DefaultModels[]`, `AuthHeaderFormat`, `SupportsModelListing`.
Salvare il provider selezionato nelle impostazioni di VS.

---

### P7 — Integrazione GitHub Copilot come provider AI

**Requisito**: se GitHub Copilot è attivo in Visual Studio, offrire la possibilità di usarlo
come provider per la generazione dei commit message, **senza richiedere API key separata**.

**Approccio da investigare**:
- Verificare se `Microsoft.VisualStudio.Extensibility.Sdk` espone un'API Language Model
  (es. `LanguageModelService` o simile) per invocare il modello Copilot dall'estensione
- Se disponibile, aggiungere "GitHub Copilot" come provider nella dropdown che non richiede
  endpoint né API key — usa le credenziali già configurate in VS
- Se l'API non è disponibile nel modello out-of-process, documentare il limite

---

### P8 — Gestione credenziali per provider con prompt automatico

**Requisito**: ogni provider AI può richiedere credenziali diverse (API key, token OAuth, ecc.).
Il sistema deve:

1. **Verificare** se le credenziali per il provider selezionato sono configurate
2. Se **mancano** → mostrare un dialog/prompt che chiede all'utente di inserirle
   (es. "Per usare Anthropic Claude, inserisci la tua API key:")
3. **Salvare** le credenziali in modo sicuro (DPAPI / `ProtectedData` o VS Settings Store)
4. Supportare **credenziali diverse per provider diversi** (non una sola API key globale)
   — es. l'utente può avere sia OpenAI che Claude configurati e switchare con la dropdown

**Modello dati suggerito**:
```csharp
record AiProviderCredentials(string ProviderId, string ApiKey, string? AdditionalConfig);
```

---

### P9 — UI moderna e coerente con il design system di Visual Studio

**Requisito**: tutta la UI deve usare i colori e gli stili del tema VS corrente (chiaro/scuro).
In Remote UI (VisualStudio.Extensibility) i controlli standard ereditano automaticamente il tema,
ma verificare che:
- I colori degli stati SVN (M=blu, A=verde, D=rosso, ?=grigio, !=arancione) siano visibili sia in tema chiaro che scuro
- I bottoni abbiano padding e margini coerenti con le altre tool window di VS
- Le icone/emoji vengano sostituite con icone vettoriali se possibile (o almeno con caratteri Unicode leggibili)

---

## Priorità di sviluppo — ordine consigliato

1. **P0** — Fix bug Commit (senza questo non si può testare nulla)
2. **P1** — Auto-detect SVN root (prerequisito per una UX accettabile)
3. **P5** — Diff su doppio click (funzionalità core SVN)
4. **P4** — UI compatta stile Git Changes (redesign layout)
5. **P2** — Sottomenu SVNAssist
6. **P3** — Auto-detect al menu hover + enable/disable comandi
7. **P6** — Dropdown provider AI con modelli
8. **P8** — Gestione credenziali multi-provider
9. **P7** — Integrazione GitHub Copilot
10. **P9** — Polish UI temi e icone
