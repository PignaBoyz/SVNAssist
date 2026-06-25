# SVNAssist — Estensione SVN per Visual Studio 2026

Estensione VSIX per Visual Studio 2026 che integra **Subversion (SVN)** nell'IDE con un pannello stile **Git Changes**, generazione automatica dei messaggi di commit tramite AI e supporto multi-provider.

![.NET 8](https://img.shields.io/badge/.NET-8.0-purple)
![VS 2026](https://img.shields.io/badge/Visual%20Studio-2026-blue)
![SVN](https://img.shields.io/badge/SVN-CLI-orange)

---

## ✨ Funzionalità

- **Pannello SVN Changes** — Tool window dockabile (stile Git Changes) con:
  - Header con nome del branch SVN corrente e bottone Update
  - Messaggio di commit inline con supporto multiriga
  - Lista file compatta con checkbox, stato (M/A/D/?/!) e path relativo
  - Pannello diff espandibile per ogni file
  - Select All / Deselect All
- **Generazione commit message con AI** — Analizza il diff e genera automaticamente il messaggio
- **Miglioramento commit message con AI** — Migliora una bozza scritta dall'utente
- **Auto-detect SVN** — Rileva automaticamente la working copy SVN dalla solution aperta
- **Operazioni SVN** — Status, Commit, Update, Log/History, Diff
- **Multi-provider AI** — GitHub Models (default), OpenAI, Azure OpenAI, Ollama, o qualsiasi endpoint compatibile OpenAI

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
>   (al primo clic su 🤖 l'estensione apre direttamente la pagina del token).
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
| **AI Endpoint** | `https://models.inference.ai.azure.com/chat/completions` *(già impostato di default)* |
| **AI API Key** | Incolla il tuo GitHub PAT (`ghp_...`) |
| **AI Model** | `gpt-4o-mini` *(default, gratuito e veloce)* |
| **Default Language** | `auto` *(rileva la lingua dal codice)* oppure `italiano` / `english` |

3. Clicca **OK**

### Step 3 — Testa la generazione AI

1. Apri una solution che si trova in una working copy SVN
2. Modifica un file e salva
3. Apri il pannello SVN Changes (**Extensions → Mostra Status**)
4. Clicca **🤖 AI** per generare il messaggio di commit
5. Il messaggio apparirà nella TextBox di commit

> **Modelli consigliati per GitHub Models:**
> - `gpt-4o-mini` — Veloce e gratuito, ottimo per commit message
> - `gpt-4o` — Più potente, per diff complessi
> - `Phi-3-medium-128k-instruct` — Open source, buone prestazioni

---

## 🔄 Cambiare provider AI

SVNAssist supporta qualsiasi endpoint compatibile con il formato **OpenAI Chat Completions** (`/v1/chat/completions`). Puoi cambiare provider in qualsiasi momento da **Tools → Options → SVNAssist**.

### OpenAI

| Impostazione | Valore |
|---|---|
| AI Endpoint | `https://api.openai.com/v1/chat/completions` |
| AI API Key | La tua API key OpenAI (`sk-...`) |
| AI Model | `gpt-4o-mini`, `gpt-4o`, `gpt-4-turbo`, `gpt-3.5-turbo` |

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

### Anthropic Claude

| Impostazione | Valore |
|---|---|
| AI Endpoint | `https://api.anthropic.com/v1/messages` |
| AI API Key | La tua API key Anthropic (`sk-ant-...`) |
| AI Model | `claude-sonnet-4-20250514`, `claude-3.5-haiku` |

> ⚠️ **Nota:** L'API di Anthropic usa un formato leggermente diverso — potrebbe richiedere adattamenti futuri per il pieno supporto.

### Google Gemini

| Impostazione | Valore |
|---|---|
| AI Endpoint | `https://generativelanguage.googleapis.com/v1beta/openai/chat/completions` |
| AI API Key | La tua API key Google AI Studio |
| AI Model | `gemini-2.0-flash`, `gemini-1.5-pro` |

---

## 🖥️ Utilizzo

### Pannello SVN Changes

Il pannello SVN Changes è il cuore dell'estensione. Si apre tramite **Extensions → Mostra Status** e si ancora tipicamente a destra, accanto a Solution Explorer (come Git Changes).

#### Layout

```
┌─ SVN Changes ─────────────────────────┐
│ ⟳    branches/feature-x    ↑ Update  │  ← Header con branch e update
│───────────────────────────────────────│
│ Messaggio di commit...                │  ← TextBox multiriga
│ [Commit] [🤖 AI] [✨]                │  ← Bottoni azione
│───────────────────────────────────────│
│ Changes (3)          [✓ All] [✗ None] │  ← Conteggio + selezione
│  ☑ M  src/File1.cs             [diff] │  ← Lista file
│  ☑ A  src/File2.cs             [diff] │
│  ☐ ?  NuovoFile.txt            [diff] │
│───────────────────────────────────────│
│ Diff: src/File1.cs                 ✕  │  ← Pannello diff
└───────────────────────────────────────┘
```

#### Workflow tipico

1. **Apri** il pannello SVN Changes
2. L'estensione **rileva automaticamente** la working copy SVN dalla solution
3. I file modificati appaiono nella lista con il loro stato:
   - **M** = Modificato
   - **A** = Aggiunto
   - **D** = Cancellato
   - **?** = Non versionato
   - **!** = Mancante
4. **Seleziona** i file da committare con le checkbox
5. **Scrivi** il messaggio di commit, oppure clicca **🤖 AI** per generarlo automaticamente
6. Clicca **Commit** per eseguire il commit
7. Usa **↑ Update** per aggiornare la working copy

#### Bottoni

| Bottone | Azione |
|---|---|
| ⟳ | Refresh — ricarica la lista file e il branch |
| ↑ Update | Esegue `svn update` sulla working copy |
| Commit | Committa i file selezionati con il messaggio scritto |
| 🤖 AI | Genera il messaggio di commit analizzando il diff con AI |
| ✨ | Migliora il messaggio di commit esistente con AI |
| ✓ All | Seleziona tutti i file |
| ✗ None | Deseleziona tutti i file |
| diff | Mostra il diff del singolo file nel pannello inferiore |

### Log / History

Apri la cronologia delle revisioni SVN tramite **Extensions → Log / History**:
- Lista revisioni con numero, autore, data e messaggio
- Click su una revisione per vedere il diff completo
- Pannello ridimensionabile con GridSplitter

---

## ⚙️ Impostazioni

Tutte le impostazioni sono in **Tools → Options → SVNAssist**:

| Impostazione | Default | Descrizione |
|---|---|---|
| AI Endpoint | `https://models.inference.ai.azure.com/chat/completions` | URL dell'endpoint AI (formato OpenAI Chat Completions) |
| AI API Key | *(vuoto)* | API key o GitHub PAT per l'autenticazione |
| AI Model | `gpt-4o-mini` | Nome del modello AI da usare |
| Default Language | `auto` | Lingua dei commit message (`auto`, `italiano`, `english`) |
| Working Copy Path | *(vuoto)* | Percorso della working copy SVN (auto-rilevato se vuoto) |

---

## 🏗️ Architettura

- **Modello**: VisualStudio.Extensibility (out-of-process, .NET 8)
- **UI**: Remote UI (DataTemplate XAML + DataContract ViewModel)
- **SVN**: `svn.exe` via `System.Diagnostics.Process` (no SharpSVN)
- **AI**: `HttpClient` con formato OpenAI Chat Completions
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

I test richiedono `svn.exe` nel PATH e creano repository SVN temporanei per i test di integrazione.

### Debug

1. Apri `SVNAssist.sln` in Visual Studio 2026
2. Premi **F5** — si apre un'istanza sperimentale di VS con l'estensione caricata
3. Nell'istanza sperimentale, apri una solution in una working copy SVN

---

## 📄 Licenza

Progetto didattico — vedi il file LICENSE per i dettagli.
