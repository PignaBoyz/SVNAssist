# Piano: UI WebView2 identica alla demo

> Prodotto da un agente di pianificazione (sessione cloud). API da confermare nello **spike (passo 0)**.

## 1. Hosting — scelta: ibrido "H"
- **Host in-proc sottile (net48, in devenv):** estensione VisualStudio.Extensibility in-proc "mixed" (`ExtensionConfiguration.RequiresInProcessHosting = true`) → restano comandi/menu/settings `[VisualStudioContribution]`. + `Microsoft.VisualStudio.SDK`, `Microsoft.VSSDK.BuildTools`, un `AsyncPackage` e due `ToolWindowPane` (Changes, Log) che ospitano `Microsoft.Web.WebView2.Wpf.WebView2`. L'host fa solo relay di JSON.
- **Backend `SVNAssist.Backend.exe` (net8):** codice attuale (Services/Models/VM → "store"). Avviato dall'host, **StreamJsonRpc su stdio**. Job Object `KILL_ON_JOB_CLOSE`; riavvio su crash + banner.

Perché: Remote UI non ospita WebView2 (AUDIT §4.1); port tutto a net472 rischioso (`GitHub.Copilot.SDK`, `System.Formats.Tar`, `Process.Kill(tree)`); due estensioni = due VSIX + canale (brokered service, da verificare). H mantiene isolamento crash di SVN/AI e test xUnit net8.

Scartate: (A) tutto in-proc net48 = fallback; (B) out-of-proc + in-proc via brokered service = solo se H fallisce; (C) finestra WPF esterna creata da out-of-proc = non si aggancia ai pannelli VS.

Insidie:
- **User data folder**: default accanto a devenv.exe non scrivibile → `CoreWebView2Environment.CreateAsync(null, %LOCALAPPDATA%\SVNAssist\WebView2)`, ambiente **condiviso** tra i 2 pane.
- **Runtime assente**: `GetAvailableBrowserVersionString()` → `WebView2RuntimeNotFoundException` → fallback WPF con link Evergreen.
- **Assembly duplicati**: VS carica già `Microsoft.Web.WebView2.*` → `ExcludeAssets="runtime"` o binding redirect (spike).
- **Sicurezza**: `SetVirtualHostNameToFolderMapping("svnassist.ui", …, DenyCors)`; `NavigationStarting`/`NewWindowRequested` bloccano esterni; DevTools off in release, menu default off, statusbar off, `AreBrowserAcceleratorKeysEnabled=false`; CSP stretta; controllo `Source` in `WebMessageReceived`; JS manda solo `rowKey`, mai percorsi.
- **Focus/tastiera**: VS ruba Del, Ctrl+C/V/A, frecce → override `ToolWindowPane.PreProcessMessage` con focus nella WebView; `MoveFocusRequested` per Tab; `Focus()` su attivazione frame (`IVsWindowFrameNotify3`). Testare docked/floating/undock.
- **DPI**: per-monitor v2, WebView2 WPF scala da sola (`ShouldDetectMonitorScaleChanges`); CSS px/rem; font da `VsFonts.EnvironmentFontFamily/Size`.
- **Airspace**: niente overlay WPF sopra la WebView; dialoghi nativi (`ShowPromptAsync`).
- **Flash bianco**: `DefaultBackgroundColor` = sfondo tool window.

## 2. UI condivisa
```
src/SVNAssist.UI.Web/            (Content, IncludeInVSIX)
  index.html?view=changes|log
  css/tokens.css  css/panel.css
  js/bridge.js    (chrome.webview | window.__mockBridge)
  js/changes.js js/tree.js js/prereq.js js/log.js js/diff.js js/ctxmenu.js
docs/demo/src/shell.html, sim.js, mock-bridge.js, tour.js
tools/build-demo.mjs   -> inlinea UI.Web + demo in docs/demo/svnassist-demo.html (file unico)
```
Render puri `render(state)` estratti dalla demo (`statusHTML`, `treeHTML`, `prereqBoxHTML`, `logHTML`, `diffHTML`, `ctxMenu`). `mock-bridge.js` riusa la simulazione (`S`, `svnCommit`, `svnUpdate`…) e produce lo **stesso snapshot** del backend. Shell VS finta, tour e pannello simulazione solo nella demo.

## 3. Protocollo C# ⇄ JS (v1)
- JS→host: `bridge.send(name,args)` → `{v:1,id,type:"cmd",name,args}`; all'avvio `{type:"ready",view}`.
- Host→JS (`bridge.onState`): `{type:"state",view,rev,state}` (snapshot; patch solo se serve), `{type:"reply",id,ok,result|error}`, `{type:"theme",kind,vars}`, `{type:"event",name:"toast"|"focus"|"backendRestarted"}`.

Comandi Changes: `refresh`, `update`, `commit`, `setCommitText{text}` (debounce), `genAi`, `improveAi`, `setLanguage{lang}`, `setWorkingCopy{path}`, `toggleFile{key}`, `toggleGroup{key}`, `toggleSection{sec}`, `selectAll` (solo sezione Modifiche, AUDIT §2.3), `unselectAll`, `toggleFolder{key}`, `expandAll`, `collapseAll`, `setView{tree|list}`, `showDiff{key}`, `showFolderDiff{key}`, `closeDiff`, `rowOp{op:add|delete|revert|resolve|ignore,key}`, `prereqToggle`, `prereqClose`, `prereqRecheck`, `prereqRun{id,primary}`.
Comandi Log: `logLoad{path}`, `logMore`, `logSelect{rev}`.

Snapshot Changes: working copy (`path`, `showInput`, `branch`); commit (`text`, `aiStatus`, `busy`, `lang`); `tree`; `sections[]` (`title`, `count`, `sel` 0/1/2, `rows[]`: `key`, `kind`, `depth`, `name`, `detail`, `status`, `sel`, `expanded`, `can{…}`, `tooltip`); `diff`; `status{msg,err}`; `prereq{…}`; `loading`. Righe albero calcolate in C# col `ChangesTreeBuilder` esistente.

Host⇄backend RPC: host→backend `ui.dispatch(view,json)`, `settings.changed`, `solution.changed(dir)`; backend→host `ui.push(view,json)`, `host.prompt(msg,options)`, `host.getSettings`, `host.openUrl`, `host.showLog(path)`.

## 4. Tema (chiaro, scuro, blu, alto contrasto)
Host legge `VSColorTheme.GetThemedColor` (`EnvironmentColors.ToolWindowBackground/Text/Border`, `CommonControlsButton*/TextBox*/CheckBox*`, `TreeViewColors.SelectedItemActive*`, `EnvironmentColors.ControlLinkText`, `CommonDocumentColors`) → CSS vars `--bg0…--bd2`, `--fg…`, `--acc…`; riapplica su `VSColorTheme.ThemeChanged`. `kind` dalla luminanza dello sfondo. Colori stato `--st-*`: due palette WCAG. Alto contrasto (`SystemParameters.HighContrast`): colori VS diretti. `tokens.css` ha default scuro/chiaro/blu per la demo (selettore tema).

## 5. Passi (uno per agente)
0. **Spike** (Windows+VS, gate per 6-9): in-proc mixed minima + WebView2 + pagina locale + tema + tastiera + sidecar net8 "echo" via StreamJsonRpc. Esito: checklist F5; decisione runtime .NET (framework-dependent vs self-contained) e binding WebView2.
1. **UI.Web dalla demo** — verifica senza VS: index.html con fixture JSON in Playwright, screenshot 3 temi.
2. **Demo ricostruita** (`docs/demo/src/*`, `mock-bridge.js`, `tools/build-demo.mjs`) — diff screenshot vs demo attuale; tour/simulazione funzionano.
3. **Contratto**: `docs/ui-protocol.md`, `src/SVNAssist.UI.Web/contract/*.schema.json` + fixture; test JS che valida il mock.
4. **Backend** `src/SVNAssist.Backend/` (net8): `SvnStatusToolWindowData`, `CommitMessageViewModel`, `PrerequisitesViewModel`, `ChangeRowViewModel`, `SvnLogToolWindowData` → `ChangesStore`/`LogStore`/`PrereqStore` con `GetSnapshot()`/`DispatchAsync`; `SettingsService` → `ISvnAssistSettings` + DTO. `dotnet test` + test snapshot vs schema.
5. **Server RPC**: `Program.cs` stdio, `IHostCallbacks` al posto di `VsDialogService`/`AiSetupPrompt`/`SvnSetupAssistant`; FileSystemWatcher + debouncer qui. Test integrazione con host finto.
6. **Host** `src/SVNAssist.Host/` (net48): `SVNAssistExtension`, `SVNAssistPackage`, `ChangesToolWindowPane`, `LogToolWindowPane`, `WebViewHost`, `BackendProcess`. Build msbuild in CI + F5.
7. **Tema**: `ThemeBridge.cs` + font. F5 con cambio temi e alto contrasto.
8. **Comandi/menu** migrati nell'host; comandi Solution Explorer → backend.
9. **Packaging/pulizia**: VSIX con `ui/` + publish backend; rimuovere `src/SVNAssist/` (XAML/Remote UI); aggiornare CI, README, DESIGN, FUNZIONALITA; 0.2.0.
10. **Rifiniture**: tastiera + ARIA sull'albero, filtro, "Commit (n)", scroll mantenuto. Playwright sulla demo + F5.

Ordine: 0 in parallelo a 1-5; poi 6→7→8→9→10.

## 6. Fallback
- **F1** problema col modello in-proc "mixed": package VSSDK classico (`AsyncPackage` + `.vsct`), stesso sidecar e UI.Web.
- **F2** problema sidecar/runtime: tutto in-proc, backend multi-target `net48;net8.0`, Copilot SDK isolato o rimosso.
- **F3** ultima istanza: resta Remote UI, ristilato con `VsBrushes`/`ThemedDialog*` su ogni TextBlock e root → risolve il nero su nero, ma non è identico alla demo.

## File critici
- docs/demo/svnassist-demo.html
- src/SVNAssist/ToolWindows/SvnStatusToolWindowData.cs
- src/SVNAssist/ToolWindows/SvnStatusToolWindow.cs
- src/SVNAssist/SVNAssist.csproj
- src/SVNAssist/Services/SettingsService.cs
- tests/SVNAssist.Tests/SVNAssist.Tests.csproj
