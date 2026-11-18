# SVNAssist

Estensione per **Visual Studio** che porta **Subversion (SVN)** nell'IDE: pannello delle modifiche in stile Git Changes, log, switch, merge, conflitti, branch/tag, patch e messaggi di commit scritti da un'AI.

![Visual Studio](https://img.shields.io/badge/Visual%20Studio-2026%20(18.x)-blue)
![SVN](https://img.shields.io/badge/SVN-CLI-orange)
![.NET](https://img.shields.io/badge/backend-.NET%2010-purple)

**Versione attuale: 0.5.0.** Scarica `SVNAssist.vsix` dalla [pagina delle Release](https://github.com/PignaBoyz/SVNAssist/releases). Elenco delle modifiche in [CHANGELOG.md](CHANGELOG.md) ([English](CHANGELOG.en.md)). Prova l'interfaccia nel browser con la demo (zip nella Release).

---

## A chi serve

A chi lavora con SVN (anche con accesso integrato di Windows) e vuole restare in Visual Studio: vedere cosa è cambiato, committare, unire rami e risolvere conflitti senza passare da riga di comando o da TortoiseSVN. SVNAssist usa `svn.exe`: non sostituisce SVN, lo guida.

## Cosa fa

**Changes** (finestra dockabile, stile Git Changes)
- Branch corrente, SVN Update, Show Log, Refresh; indicatore della revisione che avvisa se altri hanno committato.
- Sezioni Conflitti, Modifiche, Non versionati e Shelved, in vista Albero o Lista. Ciò che spunti è ciò che viene committato.
- **Commit All** (nessun file in Staged: tutte le modifiche dei file versionati) o **Commit Staged (n)**. Prima del commit controlla se sul server ci sono revisioni di altri e se un file contiene marcatori di conflitto.
- Lucchetto sui file bloccati da altri ("Bloccato da utente"); SVN Lock / Unlock.
- Diff nativo di Visual Studio, con Alt+F8 / Shift+Alt+F8 tra le modifiche e **SVN: Revert Selected Changes** per annullare una riga o un blocco.
- Menu del tasto destro su file, cartelle, sezioni e righe selezionate: Add, Revert, Delete, Ignore, Diff, Lock...

**Branch, tag, patch, shelve**
- **SVN Branch/Tag...** da HEAD, da una revisione o dalla working copy; **Switch** con elenco dei rami, usati di recente e filtri.
- **Create Patch / Apply Patch** (con anteprima) e **Shelve / Unshelve** per mettere da parte le modifiche.
- Backup automatico prima di Revert di tutto e Annulla merge, con **Ripristina modifiche**.

**Merge e conflitti**
- **SVN Merge...** dal Log o da una cartella di Solution Explorer: anteprima per revisione con i conflitti previsti, Merge tutto o Merge selezionate (cherry-pick), applicazione una revisione alla volta con **Continua merge** se si ferma, **Annulla merge**.
- Riquadro Conflitti: Usa la mia / la loro / entrambe (anche per tutti), editor di merge a 3 vie di Visual Studio, tree conflict in blocco, barra sul file aperto in conflitto.

**Log**
- Cronologia come documento nell'area editor: filtri per autore, testo, percorso e date; file di ogni revisione con diff; confronto tra due revisioni; revisioni unite; percorsi modificati.

**Messaggi di commit con AI**
- **Genera con AI** scrive il messaggio dal diff dei file che stai per committare; **Migliora** rifinisce una bozza; lingua Italiano, English o Auto.
- Provider: GitHub Copilot, Claude Code (abbonamento), Claude API, qualsiasi endpoint compatibile OpenAI (GitHub Models, OpenAI, Azure, Ollama, Gemini). Con `auto` prova i provider in ordine e passa al successivo se uno fallisce.
- L'elenco dei modelli viene sempre dal provider collegato: nessun elenco scritto a mano. Se l'accesso è scaduto compare **Rifai l'accesso**.
- Segue le regole di commit del progetto (`.github/copilot-instructions.md`) e dell'utente.
- Prima del primo invio chiede conferma: il diff dei file in commit lascia il computer.

**Interfaccia**: italiano e inglese (automatico dalla lingua di Visual Studio), temi chiaro, scuro e blu, checklist dei prerequisiti con installazione guidata.

![Branch/Tag](docs/release-notes/img/0.5.0/it/01-branch-tag.png)

## Requisiti

| Cosa | Note |
|------|------|
| Visual Studio 2026 (18.x) | provato. Visual Studio 2022 (17.x) è dichiarato ma non provato |
| `svn.exe` nel PATH | da TortoiseSVN (con command line tools), SlikSVN o CollabNet; la checklist propone l'installazione |
| Runtime .NET 10 | non è nel VSIX; se manca, SVNAssist propone di installarlo (winget) |
| WebView2 Runtime | già presente con Visual Studio e Windows 11 |
| Un provider AI | solo per i messaggi con AI, vedi sotto |

## Installazione

1. Scarica `SVNAssist.vsix` dalla [Release](https://github.com/PignaBoyz/SVNAssist/releases).
2. Doppio click sul file e riavvia Visual Studio.
3. Apri una solution che sta in una working copy SVN e vai in **Extensions > SVNAssist > SVN Changes**. La working copy viene riconosciuta da sola.

Se manca qualcosa (svn, .NET, provider AI) il pulsante **Prerequisiti** in basso nel pannello diventa rosso e apre la procedura per sistemarlo.

## Usare l'AI

La procedura guidata si apre da **Genera con AI** quando manca un provider, o da **Extensions > SVNAssist > Change AI Provider...**. Ha quattro passi: provider, prerequisiti, accesso, modello.

| Provider | Cosa serve |
|----------|-----------|
| GitHub Copilot | abbonamento Copilot e `gh auth login`; al primo uso scarica il runtime Copilot (circa 100 MB) |
| Claude Code | Claude Code installato e con login fatto (abbonamento) |
| Claude API | API key Anthropic (impostazione o `ANTHROPIC_API_KEY`) |
| OpenAI-compatibile | endpoint e API key; di default GitHub Models, con il token di `gh` rilevato da solo |

Endpoint compatibili, da configurare a mano in Options (non tutti provati dall'autore): OpenAI (`https://api.openai.com/v1/chat/completions`), Azure OpenAI, Ollama in locale (`http://localhost:11434/v1/chat/completions`), Google Gemini (`https://generativelanguage.googleapis.com/v1beta/openai/chat/completions`, API key di Google AI Studio). Ogni credenziale va solo al proprio servizio. Le API key sono cifrate con DPAPI.

## Impostazioni

**Tools > Options > SVNAssist**. Le principali:

| Impostazione | Default | A cosa serve |
|--------------|---------|--------------|
| AI Provider | `auto` | `auto`, `copilot`, `claudecode`, `claude`, `openai` |
| Modelli (Claude, Copilot, OpenAI-compatibile) | predefiniti | testo libero; l'elenco vero è nella procedura guidata |
| Lingua dei commit message | `auto` | `auto`, `italiano`, `english` |
| Mostra modello e costo nel messaggio generato | spento | scrive modello e costo dichiarato dal provider (Copilot: richieste premium), altrimenti "costo non disponibile" |
| Repo layout | `trunk/branches/tags` | nomi delle cartelle per Switch, Merge e Branch/Tag; se non corrisponde viene rilevato |
| New revision check (minutes) | 5 | ogni quanti minuti controllare se altri hanno committato (0 = spento) |
| Working Copy Path | vuoto | ha la precedenza sul rilevamento dalla solution |
| Lingua / tema | automatici | interfaccia e finestre |

L'elenco completo con i valori è in [docs/ui-protocol.md](docs/ui-protocol.md).

## Limiti noti

- Visual Studio 2022 non è stato provato.
- I glifi di stato in Solution Explorer sono le icone standard di Visual Studio.
- Elenchi dei modelli con tutti i provider reali non ancora verificati.

## Come è fatto

- **Host** (`src/SVNAssist.Host`, net472): pacchetto Visual Studio, comandi, Options, finestre con WebView2.
- **Backend** (`src/SVNAssist.Backend`, .NET 10): logica SVN (`svn.exe`) e AI; parla con l'Host via StreamJsonRpc.
- **UI web** (`src/SVNAssist.UI.Web`): HTML/CSS/JS condivisi con la demo (`docs/demo`).

Approfondimenti: [DESIGN.md](DESIGN.md), [docs/host.md](docs/host.md), [docs/ui-protocol.md](docs/ui-protocol.md), [docs/FUNZIONALITA.md](docs/FUNZIONALITA.md), [docs/ROADMAP.md](docs/ROADMAP.md).

## Sviluppo

```bash
dotnet test tests/SVNAssist.Tests/SVNAssist.Tests.csproj
node --test tests/ui-web/*.test.mjs
node tools/build-demo.mjs
```

I test di integrazione richiedono `svn.exe` nel PATH (repository temporanei `file://`). Il VSIX si compila come descritto in [docs/host.md](docs/host.md). Per provarlo in un'istanza sperimentale e con una simulazione di repository: `tools/collaudo-mirato.ps1`. Regole per note di rilascio e changelog: [docs/release-notes/GUIDA.md](docs/release-notes/GUIDA.md).

## Licenza

Vedi [LICENSE](LICENSE).
