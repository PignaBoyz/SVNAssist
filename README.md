# SVNAssist — Estensione SVN per Visual Studio 2026

Estensione VSIX per Visual Studio 2026 che integra **Subversion (SVN)** nell'IDE con un pannello stile **Git Changes**, generazione automatica dei messaggi di commit tramite AI e supporto multi-provider.

![.NET 8](https://img.shields.io/badge/.NET-8.0-purple)
![VS 2026](https://img.shields.io/badge/Visual%20Studio-2026-blue)
![SVN](https://img.shields.io/badge/SVN-CLI-orange)

---

## ✨ Funzionalità

- **Pannello SVN Changes** — Tool window dockabile, stile Git Changes:
  - Branch corrente, Refresh e Update in alto
  - Messaggio di commit multiriga con **Commit**, **Genera con AI**, **Migliora** e scelta della lingua (Italiano / English / Auto)
  - Sezioni comprimibili **Conflitti**, **Modifiche** e **Non versionati**, con conteggio; vista **Albero** (cartelle compattate, come Git) o **Lista**
  - Checkbox tri-state su file, cartelle e sezioni: ciò che è spuntato è ciò che viene committato (nessuno "staging")
  - Menu contestuali: Add, Revert, Delete, Ignore, Risolvi conflitto, Diff (file e cartella) — Revert e Delete chiedono conferma elencando cosa cambia
  - Pannello diff inline (o TortoiseSVN se installato)
  - **Checklist dei prerequisiti** in-panel: svn, TortoiseSVN, provider AI, con pulsanti di installazione/login a un clic
- **Commit message con AI** — Genera dal diff **minimo** dei soli file selezionati, o migliora la bozza; multi-provider con fallback
- **Auto-detect SVN** — Rileva la working copy dalla solution aperta; refresh automatico (FileSystemWatcher con debounce)
- **Log / History** — Cronologia con diff per revisione e "Carica altre 50"
- **Comportamento fedele a svn/TortoiseSVN** — i non versionati spuntati vengono aggiunti prima del commit, i mancanti cancellati, i conflitti bloccano il commit

---

## 📦 Installazione

### Prerequisiti

1. **Visual Studio 2026** (Community, Professional o Enterprise)
2. **svn.exe** nel PATH di sistema — installabile tramite:
   - [TortoiseSVN](https://tortoisesvn.net/) (con l'opzione "command line client tools")
   - [SlikSVN](https://sliksvn.com/download/)
   - [CollabNet SVN](https://www.collab.net/downloads/subversion)

### Installazione dell'estensione

1. Scarica il file `.vsix` dalla sezione [Releases](https://github.com/PignaBoyz/SVNAssist/releases)
2. Fai doppio click sul file `.vsix` per installarlo
3. Riavvia Visual Studio

L'estensione comparirà nel menu **Extensions** con i comandi:
- **Mostra Status** — Apre il pannello SVN Changes
- **Commit...** — Apre il pannello SVN Changes per il commit
- **Update** — Esegue `svn update`
- **Log / History** — Mostra la cronologia delle revisioni

---

## 🤖 Configurazione AI — GitHub Models (Consigliato)

> **Unico prerequisito per l'AI: un token GitHub gratuito.**
> Endpoint e modello sono **già preimpostati** — non devi configurarli. L'unica cosa da fare,
> una volta sola, è fornire un token:
> - **Hai GitHub CLI?** Se sei loggato (`gh auth login`), il token viene rilevato **in automatico**: niente da fare.
> - **Altrimenti** crea un Personal Access Token gratuito e incollalo in *Tools → Options → SVNAssist → AI API Key*
>   (al primo clic su "Genera con AI" l'estensione apre direttamente la pagina del token).
>
> In alternativa SVNAssist legge anche la variabile d'ambiente `GITHUB_TOKEN` / `GH_TOKEN`.

SVNAssist usa di default **GitHub Models**, un servizio gratuito di GitHub che offre accesso a modelli AI (GPT-4o, GPT-4o-mini, Phi-3, Llama, ecc.) tramite il tuo account GitHub.

### Step 1 — Crea un GitHub Personal Access Token (PAT)

1. Vai su [github.com/settings/tokens](https://github.com/settings/tokens)
2. Clicca **"Generate new token"** → **"Generate new token (classic)"**
3. Dai un nome al token (es. `SVNAssist AI`)
4. **Non serve selezionare nessuno scope** — il token base è sufficiente per GitHub Models
5. Clicca **"Generate token"**
6. **Copia il token** (inizia con `ghp_...`) — non potrai vederlo di nuovo!

### Step 2 — Configura SVNAssist in Visual Studio

1. Vai su **Tools → Options → SVNAssist**
2. Compila i campi:

| Impostazione | Valore |
|---|---|
| **AI Endpoint** | `https://models.github.ai/inference/chat/completions` *(già impostato di default)* |
| **AI API Key** | Incolla il tuo GitHub PAT (`ghp_...`) |
| **AI Model** | `openai/gpt-4o-mini` *(default, gratuito e veloce; scelta da elenco)* |
| **Default Language** | `auto` *(rileva la lingua dal codice)* oppure `italiano` / `english` |

3. Clicca **OK**

### Step 3 — Testa la generazione AI

1. Apri una solution che si trova in una working copy SVN
2. Modifica un file e salva
3. Apri il pannello SVN Changes (**Extensions → Mostra Status**)
4. Clicca **Genera con AI** per generare il messaggio di commit
5. Il messaggio apparirà nella TextBox di commit

> **Modelli consigliati per GitHub Models:**
> - `openai/gpt-4o-mini` — Veloce e gratuito, ottimo per commit message
> - `openai/gpt-4o` — Più potente, per diff complessi
> - Il modello si sceglie da un elenco curato (GPT-4o mini, GPT-4o, GPT-4.1 mini, GPT-4.1); l'id con prefisso `openai/` è quello richiesto da GitHub Models e viene adattato in automatico per gli altri endpoint

### Regole di commit (copilot-instructions)

Il messaggio generato segue le regole che scrivi in questi file (se esistono), aggiunte al prompt dell'AI:

- **Progetto**: `.github/copilot-instructions.md` nella working copy o in una cartella sopra (vale la più vicina), più i file
  `.github/instructions/*.instructions.md` con `applyTo: "**"` (o senza `applyTo`).
- **Utente (tutti i progetti)**: `%USERPROFILE%\.copilot\copilot-instructions.md`.

Scrivi per esempio: *"Messaggi di commit in inglese, prima riga max 72 caratteri, prefisso `[PROJ-123]`"*. Vengono usate solo le parti
sui messaggi di commit; ogni file è limitato a 4.000 caratteri (8.000 in totale). Quando sono applicate, lo stato sotto il campo
messaggio lo dice. Il contenuto dei file viene inviato al provider AI insieme al diff.

Se non hai alcun provider pronto, "Genera con AI" propone il login nel terminale (`gh auth login --web`; per Claude Code `claude`),
oppure, annullando, la pagina per creare un token.

---

## 🔄 Cambiare provider AI

L'impostazione **AI Provider** (*Tools → Options → SVNAssist*) sceglie chi genera il commit:

| Valore | Cosa usa | Cosa serve |
|---|---|---|
| `auto` *(default)* | Il primo disponibile nell'ordine qui sotto; se uno fallisce passa al successivo | — |
| `copilot` | Il tuo abbonamento **GitHub Copilot** | `gh auth login`; al primo uso scarica il runtime Copilot (vedi nota) |
| `claudecode` | Il tuo abbonamento **Claude** tramite Claude Code (`claude -p`) | Claude Code installato e loggato |
| `claude` | **Claude API** (SDK ufficiale Anthropic) | *Anthropic API Key* o variabile `ANTHROPIC_API_KEY` |
| `openai` | Qualsiasi endpoint compatibile OpenAI (sotto) | *AI API Key*, oppure token GitHub per GitHub Models |

Il modello Claude si sceglie in **Claude Model** (elenco: Sonnet, default; Haiku, il più economico; Opus, il più potente e costoso). Il **Modello Copilot** è un elenco con il modello predefinito di Copilot come default.
Ogni credenziale va solo al proprio servizio: il token GitHub rilevato in automatico non viene mai inviato a endpoint di terzi.

> **Nota Copilot:** il runtime Copilot non è incluso nel VSIX. Scegliendo `copilot` viene scaricato **una volta** (~100 MB, ~200 MB su disco) dalla release ufficiale GitHub, verificato con SHA-256, in `%LOCALAPPDATA%\SVNAssist\copilot-runtime`. Con `auto` Copilot viene usato solo se il runtime è già stato scaricato.

Per il provider `openai` puoi usare qualsiasi endpoint compatibile con il formato **OpenAI Chat Completions** (`/v1/chat/completions`):

### OpenAI

| Impostazione | Valore |
|---|---|
| AI Endpoint | `https://api.openai.com/v1/chat/completions` |
| AI API Key | La tua API key OpenAI (`sk-...`) |
| AI Model | `GPT-4o mini`, `GPT-4o`, `GPT-4.1 mini`, `GPT-4.1` (l'estensione toglie il prefisso `openai/` per OpenAI) |

### Azure OpenAI

| Impostazione | Valore |
|---|---|
| AI Endpoint | `https://{tuo-instance}.openai.azure.com/openai/deployments/{modello}/chat/completions?api-version=2024-02-01` |
| AI API Key | La tua Azure API key |
| AI Model | Il nome del deployment |

### Ollama (locale, gratis, offline)

1. Installa [Ollama](https://ollama.ai/)
2. Scarica un modello: `ollama pull llama3.2`
3. Configura:

| Impostazione | Valore |
|---|---|
| AI Endpoint | `http://localhost:11434/v1/chat/completions` |
| AI API Key | `ollama` *(qualsiasi valore, Ollama non richiede autenticazione)* |
| AI Model | `llama3.2`, `codellama`, `mistral`, ecc. |

### Google Gemini

| Impostazione | Valore |
|---|---|
| AI Endpoint | `https://generativelanguage.googleapis.com/v1beta/openai/chat/completions` |
| AI API Key | La tua API key Google AI Studio |
| AI Model | `gemini-2.0-flash`, `gemini-1.5-pro` |

---

## 🖥️ Utilizzo

### Pannello SVN Changes

Si apre da **Extensions → Mostra Status** e si ancora di solito a destra, accanto a Solution Explorer.

```
┌─ SVN Changes ─────────────────────────┐
│ trunk                  [Refresh][Update]│
│ ┌ Messaggio di commit ───────────────┐ │
│ └────────────────────────────────────┘ │
│ [Commit] [Genera con AI] [Migliora] [Italiano ▾]
│ Seleziona tutto  Deseleziona   [Albero][Lista]
│ ▾ ☑ Modifiche (3)                      │
│   ▾ ☑ src                          2   │
│       ☑ M Program.cs                   │
│       ☐ M Util.cs                      │
│ ▾ ☐ Non versionati (1)                 │
│       ☐ ? notes.txt                    │
│ Stato · [Prerequisiti (1 da sistemare)]│
└───────────────────────────────────────┘
```

#### Workflow tipico

1. Apri il pannello: la working copy viene rilevata dalla solution e le modifiche appaiono raggruppate per sezione e cartella
2. Spunta ciò che vuoi committare (una checkbox su una cartella o sezione spunta tutti i discendenti; lo stato parziale è indeterminato)
3. Scrivi il messaggio o premi **Genera con AI** (la lingua si sceglie nel menu accanto ai bottoni; vale per la sessione, il default viene da *Default Language*)
4. **Commit**: i non versionati spuntati vengono aggiunti, i mancanti cancellati; con un conflitto selezionato il commit viene rifiutato
5. **Update** aggiorna la working copy e segnala eventuali conflitti (sezione *Conflitti*, menu *Risolvi conflitto...*)

Stati: **M** modificato, **A** aggiunto, **D** eliminato, **R** sostituito, **C** conflitto (anche ad albero), **?** non versionato, **!** mancante/incompleto, **~** ostruito.

#### Menu contestuali

| Dove | Voci |
|---|---|
| File | SVN Add, Aggiungi a ignore list, SVN Delete (avvisa se perde modifiche locali), SVN Revert (conferma), Risolvi conflitto (Working / Mio / Server / Base), Mostra diff |
| Cartella | SVN Add (contenuto non versionato), SVN Revert cartella, Aggiungi a ignore list, Diff cartella, Espandi/Comprimi tutto |
| Sezione | Aggiungi tutti a SVN (Non versionati), Revert tutto (Modifiche), Espandi/Comprimi tutto |

#### Prerequisiti

Il pulsante in basso apre la checklist: **svn.exe** (obbligatorio; "Installa con winget" o pagina di download), **TortoiseSVN** (opzionale, diff nativo) e lo stato dei provider AI (GitHub CLI + login, Claude Code, Anthropic API Key, runtime Copilot). I pulsanti aprono una finestra console visibile (`winget`, `gh auth login`, `claude`, installer di Claude Code): al termine premi **Ricontrolla**. La checklist si apre da sola al primo uso se manca svn.

### Log / History

**Extensions → Log / History**: rileva la working copy dalla solution, mostra le ultime 50 revisioni (autore, data, messaggio), il diff al clic su una revisione e **Carica altre 50**.

---

## ⚙️ Impostazioni

Tutte le impostazioni sono in **Tools → Options → SVNAssist**:

| Impostazione | Default | Descrizione |
|---|---|---|
| AI Endpoint | `https://models.github.ai/inference/chat/completions` | URL dell'endpoint AI (formato OpenAI Chat Completions) |
| AI API Key | *(vuoto)* | API key o GitHub PAT per l'autenticazione |
| AI Model | `openai/gpt-4o-mini` | Modello AI (elenco) |
| Default Language | `auto` | Lingua dei commit message (`auto`, `italiano`, `english`) |
| Working Copy Path | *(vuoto)* | Percorso della working copy SVN (auto-rilevato se vuoto) |

---

## 🏗️ Architettura

- **Modello**: VisualStudio.Extensibility (out-of-process, .NET 8)
- **UI**: Remote UI (XAML + ViewModel `[DataContract]`, senza code-behind né convertitori). L'albero è una `ListBox` appiattita di righe (sezione/cartella/file); raggruppamento e tri-state sono in `ChangesTreeBuilder` (puro, testato)
- **ViewModel**: `SvnStatusToolWindowData` (orchestrazione), `CommitMessageViewModel` (messaggio + AI + lingua), `PrerequisitesViewModel`, righe `ChangeRowViewModel`; dipendono solo da `ISvnService`, `IAiServiceFactory`, `IDialogService`
- **SVN**: `svn.exe` via `Process` (no SharpSVN), output forzato in inglese e non interattivo; `ReloadableSvnService` ritrova svn se installato a VS aperto
- **AI multi-provider**: `AiServiceFactory` costruisce la catena Copilot → Claude Code → Claude API → OpenAI-compatibile (con fallback); il diff inviato è minimo (`GetDiffForAiAsync`: niente contesto, binari e cancellati omessi)
- **Refresh**: un solo ciclo con debounce (`RefreshDebouncer`) alimentato dal FileSystemWatcher (ignora `bin/ obj/ .vs/ .svn/`); `svn info` solo quando cambia la working copy o dopo update/commit
- **Settings**: `SettingCategory` con source-generated observer

---

## 🧑‍💻 Sviluppo

### Build

```bash
dotnet build src/SVNAssist/SVNAssist.csproj
```

### Test

```bash
dotnet test tests/SVNAssist.Tests/SVNAssist.Tests.csproj
```

I test di integrazione richiedono `svn.exe` nel PATH (repository temporanei `file://`); i test dei ViewModel usano servizi finti.

### Debug

1. Apri `SVNAssist.sln` in Visual Studio 2026
2. Premi **F5** — si apre un'istanza sperimentale di VS con l'estensione caricata
3. Nell'istanza sperimentale, apri una solution in una working copy SVN

---

## 📄 Licenza

Progetto didattico — vedi il file LICENSE per i dettagli.
