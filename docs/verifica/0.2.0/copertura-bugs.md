# Copertura di BUGS&MIGLIORIE_0_1_0 nel codice 0.2.0

Verifica statica del codice (non eseguita in VS). Stati: coperta / parziale / mancante. "F5" = istanza sperimentale di VS con il VSIX.

## Bug

| Voce | Stato | Dove nel codice | Come verificarla con F5 |
|---|---|---|---|
| Nero su nero, scritte illeggibili | coperta | `UI.Web/css/tokens.css` (temi WCAG AA, `contrast.test.mjs`); `Host/ThemeBridge.cs` | Passare VS da scuro a chiaro: testi sempre leggibili |
| "Genera con AI" deve fare il login nel terminale, e poterlo scegliere; chiudere il popup non deve zittire tutto | coperta | `Backend/Hosting/HostDialogs.cs` (`IAiSetupPrompt`: scelta, poi `RunInTerminalAsync("gh auth login --web")`), `IAiSetupPrompt.cs` (mostrato a ogni richiesta) | Senza login: Genera con AI, scegliere il terminale, poi chiudere/annullare e ripremere: il popup riappare |
| Dopo login gh e conferma "servizio esterno" non scrive il messaggio | coperta (bug di `AiProvider` corretto, log in questa cartella) | `Backend/Stores/CommitStore.cs` (`RunAiCoreAsync`), `host-log-run1/run2` | Con login gh: Genera con AI, OK, il messaggio compare e lo stato dice il provider usato |
| Commit "staged" scegliendo i file come in git | coperta | sezione Staged: `Backend/Stores/ChangesStore.cs` (`stage`/`unstage`), `UI.Web/js/tree.js`, `commit` solo sugli Staged | Premere + su un file: passa in Staged; Commit committa solo quelli |
| Foreground in base al tema | coperta | `css/tokens.css` (`--fg*` per tema), `ThemeBridge.AutoVars` | Cambiare tema VS con la finestra aperta: cambia a caldo (`VSColorTheme.ThemeChanged` in `HostService.cs`) |
| Diff cartella illeggibile: pagina dedicata come Git Changes, stilizzata | parziale | `showDiff` apre il diff affiancato nativo di VS (`HostService.OpenDiffAsync`); `showFolderDiff` apre un unico `.diff` nell'editor (colorato da VS) invece di un diff per file | Doppio clic su file: diff affiancato in VS con intellisense. Su cartella: file `Diff <cartella>.diff` nell'editor |
| File aggiunto non citato nel messaggio generato | coperta | `SvnService.GetDiffForAiAsync`: elenco file con stato (A/M/D) in testa + diff dei nuovi | Aggiungere un file nuovo, metterlo in Staged, Genera: il file è citato |
| Genera funziona su di me ma non sul collega: seguire copilot-instructions di sistema o di cartella | coperta | `Backend/Services/CommitInstructions.cs` (`~/.copilot/` + `.github/copilot-instructions.md` e `.github/instructions/*.instructions.md` risalendo la WC), stato "Regole di commit del progetto applicate" | Creare `.github/copilot-instructions.md` con una regola nella WC e generare |
| Genera non deve fallire con n file modificati | coperta | `DiffLimiter` (120 KB totali / 20 KB per file), `--targets` e chunking in `SvnService` | Mettere decine di file in Staged e generare |
| Scegliere il tema della finestra SVN Changes | coperta | menu "..." > Tema (`setTheme`), Options > Tema (`WindowThemeConverter`); chiaro/scuro/blu/auto; alto contrasto in `ThemeBridge` (`SystemParameters.HighContrast`) | Menu "..." > Tema: cambio immediato; Auto segue VS |

## Migliorie

| Voce | Stato | Dove nel codice | Come verificarla con F5 |
|---|---|---|---|
| Troppi bottoni poco chiari | coperta | `UI.Web/js/changes.js`: Aggiorna, Aggiorna elenco, menu "...", Commit, Genera con AI + tendina; azioni di riga solo al passaggio/focus, tooltip e `aria-label` | Aprire il pannello: pochi pulsanti con etichetta e tooltip |
| Vista ad albero "orribile" (immagine) | coperta | `UI.Web/js/tree.js` (guide di indentazione, chevron, icone, contatori), `ChangesTreeBuilder` | Confrontare con la demo `docs/demo/svnassist-demo.html` |
| Settings: tendina dei modelli | parziale | `Host/OptionsPage.cs` (`ClaudeModelConverter`, `CopilotModelConverter`, `AiModelConverter`): elenco fisso, non scaricato dal provider; l'elenco Copilot è una stima | Tools > Options > SVNAssist: i modelli sono tendine. Elenco dinamico non fatto |
| Diff nell'editor di VS come Git, leggibile | coperta | `HostService.OpenDiffAsync` (BASE esportato vs working copy) | Invio / doppio clic su un file |
| Canc su un file lo elimina dal filesystem | coperta | `UI.Web/js/tree.js` (`Delete` > `rowOp deleteFile`), `ChangesStore.DeleteFileCoreAsync` (conferma nativa, avviso se modificato) | Selezionare un file, Canc, confermare |

## Rifiniture del piano (passo 10), fatte in questa sessione o già presenti

| Voce | Stato prima | Esito |
|---|---|---|
| Tastiera albero (frecce, sinistra/destra, Spazio, Invio, Canc con conferma) | già presente in `tree.js` | test aggiunto |
| ARIA tree/treeitem, aria-expanded, aria-selected | già presenti | aggiunto `aria-checked` (vero in Staged) |
| Filtro testuale | mancante | aggiunto: campo "Filtra i file…" in `changes.js`, `filterRows` in `tree.js` (Esc svuota, freccia giù va all'albero); solo lato UI |
| Commit (n) | già presente (`pill` con n file in Staged) | test aggiunto |
| Scroll mantenuto dopo refresh | già presente (`list.scrollTop` ripristinato in `render`) | invariato |
