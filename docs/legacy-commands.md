# Comandi, menu e impostazioni della versione Remote UI (0.1.x)

Estratto dal codice di `src/SVNAssist` (VisualStudio.Extensibility out-of-proc) prima della rimozione, perche' `SVNAssist.Host`
li ricrei (comandi VSSDK `.vsct`, voci di menu, pagina Options). Le etichette sono quelle di `.vsextension/string-resources.json`.

## Menu Extensions > SVNAssist (`GroupPlacement.KnownPlacements.ExtensionsMenu`)

| Gruppo | Comando | Etichetta | Azione |
|---|---|---|---|
| 0 | `StatusCommand` | SVN Changes | apre (attiva) la tool window **SVN Changes** |
| 0 | `CommitCommand` | Commit... | apre la stessa tool window SVN Changes (il commit si fa da li') |
| 1 (separatore) | `UpdateCommand` | Update | `svn update` sulla working copy della solution aperta (root cercata risalendo da `solutionDir` con `.svn`). Se manca svn propone l'installazione; se non c'e' una solution o non e' una working copy mostra un messaggio. Esito: "Aggiornato alla revisione N" (+ " (K file in conflitto)") |
| 1 | `ShowLogCommand` | Log / History | apre la tool window **SVN Log** |

Con il backend: `Update` = `IBackend.DispatchAsync("changes", {cmd update})`; il resto e' solo apertura di finestre.

## Sottomenu "SVN" nei context menu di Solution Explorer

Agganciato a 4 menu contestuali (`guidSHLMainMenu` {D309F791-903F-11D0-9EFC-00A0C911004F}): file `IDM_VS_CTXT_ITEMNODE` 0x0430,
cartella `IDM_VS_CTXT_FOLDERNODE` 0x0431, progetto `IDM_VS_CTXT_PROJNODE` 0x0402, solution `IDM_VS_CTXT_SOLNNODE` 0x0413. Priorita' gruppo 0x0F00. Nome sottomenu: **SVN**.

L'elemento cliccato si risolve con `SvnTarget.Resolve`: cartella -> se stessa; file .sln/.slnx/.csproj/.vbproj/.fsproj/.vcxproj/.proj/.shproj/.wapproj -> la cartella che lo contiene (come TortoiseSVN); altro file -> il file.
Working copy = `SvnService.FindSvnRoot(cartella)`; se `null`: "Non e' una working copy SVN". Se svn.exe manca: proposta di installazione (una volta per sessione).

| Gruppo | Comando | Etichetta | Azione |
|---|---|---|---|
| 0 | `SeUpdateCommand` | SVN Update | `svn update <percorso>` (file o cartella); riepilogo "Aggiornato alla revisione N" + elenco conflitti (max 15 righe) |
| 0 | `SeCommitCommand` | SVN Commit... | apre SVN Changes |
| 1 | `SeDiffCommand` | Diff con BASE | file: diff nativo (TortoiseSVN), altrimenti testo del diff (max 4000 caratteri) in un dialogo; cartella: testo del diff. Ora: `IHost.OpenDiffAsync` con BASE esportato (`svn cat -r BASE`) |
| 1 | `SeLogCommand` | Mostra log | apre SVN Log (il percorso non era ancora passato; ora `IHost.ShowLogAsync(path)` / vista log con `logLoad`) |
| 2 | `SeAddCommand` | SVN Add | file: `svn add`; cartella: elenca i non versionati, chiede conferma (avviso su bin/obj/.vs/packages), poi `svn add` |
| 2 | `SeRevertCommand` | SVN Revert | conferma sempre ("non e' reversibile"; per cartelle elenca le modifiche), poi `svn revert` |
| 2 | `SeIgnoreCommand` | Aggiungi a ignore list | aggiunge il nome a `svn:ignore` della cartella padre; messaggio: la modifica alla proprieta' va committata sulla cartella padre |

Tutti gli errori: dialogo "`<ErrorTitle>`:\n`<messaggio>`" (Errore durante l'update / nell'apertura di SVN Changes / durante il diff / nell'apertura del log / durante l'add / durante il revert / durante l'aggiunta alla ignore list). L'annullamento da parte dell'utente non mostra nulla.

Nota: i comandi del sottomenu SVN di Solution Explorer agivano direttamente con `SvnService` (non passavano dalla tool window). Il backend non li espone ancora come metodi RPC: l'host puo' (a) aprire le finestre e inoltrare `rowOp`/`update` dove ha senso, oppure (b) si aggiungono metodi a `IBackend` (non fatto in questa tappa).

## Tool window

| Nome | Titolo | Posizionamento |
|---|---|---|
| `SvnStatusToolWindow` | **SVN Changes** | `Floating` la prima volta, dock a destra (accanto a Solution Explorer, come Git Changes) |
| `SvnLogToolWindow` | **SVN Log** | `DocumentWell` |

## Impostazioni (Tools > Options > SVNAssist, categoria `svnassist`, "SVNAssist")

Gli ID Extensibility erano minuscoli. Nel contratto host<->backend le chiavi sono camelCase (colonna "Chiave RPC"); il backend le legge senza distinguere maiuscole/minuscole, quindi vanno bene entrambe.

| Chiave RPC | ID old | Tipo | Default | Valori (enum) | Etichetta |
|---|---|---|---|---|---|
| `aiProvider` | `aiprovider` | enum | `auto` | `auto` Automatico (consigliato); `copilot` GitHub Copilot (abbonamento); `claudecode` Claude Code (abbonamento Claude); `claude` Claude API (API key Anthropic); `openai` OpenAI-compatibile / GitHub Models | Provider AI per i commit message |
| `claudeModel` | `claudemodel` | enum | `claude-sonnet-5-5` | `claude-sonnet-5-5` Sonnet (consigliato, equilibrato); `claude-haiku-5-5` Haiku (il piu' economico e veloce); `claude-opus-5-5` Opus (il piu' potente, costoso) | Modello Claude (solo Claude Code e Claude API) |
| `anthropicApiKey` | `anthropicapikey` | string | (vuota) | vuota = variabile `ANTHROPIC_API_KEY` | API key Anthropic (solo Claude API; vuota = variabile ANTHROPIC_API_KEY) |
| `copilotModel` | `copilotmodel` | enum | `default` | `default` Predefinito di Copilot (consigliato); `gpt-4.1` GPT-4.1; `gpt-5-mini` GPT-5 mini; `claude-sonnet-4.5` Claude Sonnet 4.5 | Modello Copilot (solo Copilot). Serve un abbonamento Copilot e 'gh auth login' |
| `aiEndpoint` | `aiendpoint` | string | `https://models.github.ai/inference/chat/completions` | URL Chat Completions OpenAI-compatibile (il vecchio `https://models.inference.ai.azure.com/chat/completions` e' migrato al nuovo dal backend) | URL endpoint (solo OpenAI-compatibile; default GitHub Models) |
| `aiApiKey` | `aiapikey` | string | (vuota) | | API key o GitHub PAT (solo OpenAI-compatibile) |
| `aiModel` | `aimodel` | enum | `openai/gpt-4o-mini` | `openai/gpt-4o-mini` GPT-4o mini (consigliato, gratuito su GitHub Models); `openai/gpt-4o` GPT-4o; `openai/gpt-4.1-mini` GPT-4.1 mini; `openai/gpt-4.1` GPT-4.1 | Modello (solo OpenAI-compatibile / GitHub Models) |
| `defaultLanguage` | `defaultlanguage` | enum | `auto` | `auto` Automatica; `italiano` Italiano; `english` English | Lingua dei commit message |
| `workingCopyPath` | `workingcopypath` | string | (vuota) | se valorizzato vince sull'auto-rilevamento dalla solution | Working Copy Path |
| `windowTheme` | (nuova) | enum | `auto` | `auto`, `light`, `dark`, `blue` | Tema della finestra (scritto dal backend con `IHost.SetSettingAsync`) |

Nota: `anthropicApiKey` e `aiApiKey` sono salvate in chiaro nelle impostazioni di VS (nessuna cifratura disponibile); non vanno mai loggate.

## Avvio e auto-rilevamento (comportamento da replicare nell'host)
- La working copy si rileva dalla cartella della solution aperta risalendo fino a `.svn`; l'host la comunica con `IBackend.SolutionChangedAsync(solutionDir)` (null = nessuna solution). Prima c'era un polling ogni 5 s.
- Il setting `workingCopyPath`, se non vuoto, ha la precedenza.
- Se `svn.exe` manca all'avvio: la checklist Prerequisiti si apre da sola.
- SVN Changes: finestra al primo uso "Floating"; la UI mostra subito "Caricamento in corso..." mentre sonda e primo `svn status` girano in background.
