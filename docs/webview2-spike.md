# Spike WebView2 (passo 0): esito

Codice in `spike/` (**cartella rimossa**: storico; il launcher e' ora `tools/msb.cs`, host e backend in `src/`) (era fuori da `SVNAssist.sln` e dalla CI). Provato su Windows 11, VS 18.12 Community, istanza `/rootsuffix Exp`, .NET SDK 11.

## Verdetto: SI per l'ibrido "H", con una correzione sui comandi

| Punto | Esito | Prova |
|---|---|---|
| Package in-proc net472 si carica in VS 18 senza errori | SI | ActivityLog: `Begin/End package load [SpikePackage]`, nessun errore |
| ToolWindowPane con `Microsoft.Web.WebView2.Wpf.WebView2` | SI | screenshot: pagina renderizzata nel pannello |
| User data folder in `%LOCALAPPDATA%\SVNAssist\WebView2` | SI | `CoreWebView2Environment.CreateAsync(null, dir)`, runtime 157.0.4341.46 |
| `SetVirtualHostNameToFolderMapping("svnassist.ui", ui/, DenyCors)` + CSP | SI | pagina caricata da `https://svnassist.ui/index.html` |
| Tema VS come CSS vars (`VSColorTheme.GetThemedColor`) + `ThemeChanged` | SI per il tema scuro (bg `#282828`, swatch resi). Chiaro/blu e cambio a caldo: DA VERIFICARE A MANO | log `theme -> dark` |
| Round-trip JS -> host -> backend net8 -> host -> JS (StreamJsonRpc su stdio) | SI | `ROUNDTRIP OK: echo: ciao 1 (backend pid 28840, .NET 8.0.34)`, ripetuto 2 volte |
| Nessun conflitto assembly WebView2 | SI | si compila contro 1.0.3967.48 (stessa versione di `PrivateAssemblies` di VS 18.12) con `ExcludeAssets="runtime"`: i DLL gestiti non vanno nel VSIX, li carica VS |
| Comando `[VisualStudioContribution]` in-proc "mixed" | NO / NON RISOLTO | build ok, `extension.json` generato (`allowHostingInProcess: true`), ma la voce **non compare** nel menu Extensions. Non indagato oltre |
| Tastiera (Del, Ctrl+A, frecce, Tab), docked/floating | DA VERIFICARE A MANO | `PreProcessMessage` e' implementato ma non testabile senza input |
| Backend si chiude con VS | SI (parziale) | all'uccisione di devenv il sidecar termina (EOF su stdin); Job Object non ancora implementato |

Conseguenza: ToolWindow, WebView2, tema e sidecar funzionano in-proc. Per i comandi/menu NON contare sul mixed Extensibility: usare i comandi VSSDK classici (`.vsct`, fallback F1 del piano, solo per i comandi) oppure approfondire il mixed in un secondo momento.

## Pacchetti e API

Host (`spike/WebView2Host`, net472, csproj SDK-style):
- `Microsoft.VisualStudio.SDK` 17.14.40265 (`ExcludeAssets="runtime"`), `Microsoft.VSSDK.BuildTools` 17.14.2120
- `Microsoft.VisualStudio.Extensibility.Sdk` 17.14.40608 (con `VssdkCompatibleExtension=true`; con `RequiresInProcessHosting=true` `Metadata` deve essere null e l'identita' viene dal `.vsixmanifest`)
- `Microsoft.Web.WebView2` 1.0.3967.48 (`ExcludeAssets="runtime"`), `StreamJsonRpc` 2.22.23 (`ExcludeAssets="runtime"`, lo fornisce VS)
- API: `AsyncPackage`, `[ProvideToolWindow]`, `ToolWindowPane`, `CoreWebView2Environment.CreateAsync`, `EnsureCoreWebView2Async`, `SetVirtualHostNameToFolderMapping`, `WebMessageReceived` (controllo `e.Source`), `PostWebMessageAsJson`, `VSColorTheme.GetThemedColor/ThemeChanged`, `EnvironmentColors/CommonControlsColors/TreeViewColors`, `JsonRpc.Attach(stdin, stdout)`.

Backend (`spike/EchoBackend`, net8): `StreamJsonRpc` 2.22.23, `JsonRpc.Attach(stdout, stdin, target)`; stdout e' il canale RPC, i log vanno su stderr.

## Problemi e soluzioni

1. **MSBuild di VS 18 non parte** ("Could not load System.Memory 4.0.2.0"): manca `MSBuild.exe.config` (c'e' solo `.config.template`). Non si modifica l'installazione: `spike/tools/msb.cs` e' un launcher da compilare con `csc` che esegue MSBuild in un AppDomain con la config presa dal template. Usare `msb.exe` al posto di `MSBuild.exe`.
2. **`/restore` con quel MSBuild fallisce** (TLS di NuGet .NET Framework): fare `dotnet restore` e poi msbuild senza `/restore`.
3. **VSIX non prodotto da csproj SDK-style**: serve `<Project>` con `Sdk.props`/`Sdk.targets` importati esplicitamente, poi `$(VSToolsPath)\VSSDK\Microsoft.VsSDK.targets` (altrimenti `IntermediateOutputPath` e' vuoto e i task scrivono in `C:\`), piu' un target `MakeVsix AfterTargets=Build DependsOnTargets=CreateVsixContainer`.
4. **Package non trovato** (`Could not load SVNAssist.Spike.WebView2Host, Version=0.2.0.0`): pkgdef senza CodeBase. Soluzione: `<UseCodebase>true</UseCodebase>`.
5. **VS si blocca (deadlock)** se si fa `await ShowToolWindowAsync` dentro `InitializeAsync`: farlo in un `JoinableTaskFactory.RunAsync` non atteso.
6. Dopo reinstall nell'istanza Exp serve `devenv /rootsuffix Exp /updateconfiguration`, altrimenti VS usa il pkgdef vecchio.
7. La Start Window di VS 2026 blocca la finestra principale: nel test e' stato premuto "Continue without code" via UI Automation.
8. `ExtensionType="VisualStudio.Extensibility"` nel manifest rompe l'installazione (VSIXInstaller exit 1015): non usarlo con asset VsPackage.
9. Font: `VsFonts.EnvironmentFontFamily` non esiste in questa versione; nello spike si usa `SystemFonts.MessageFontFamily/Size`.
10. Nel VSIX finiscono anche i `WebView2Loader.dll` nativi in `runtimes/` (inutili, VS ha i suoi): escluderli nel progetto vero.

## Runtime .NET del backend

Scelta: **framework-dependent** per la 0.2.0 (VSIX piccolo, ~1 MB di dipendenze contro ~70 MB self-contained). Richiede il runtime .NET 8 sulla macchina: VS 18 lo installa con i suoi componenti, e l'host deve comunque verificare `dotnet --list-runtimes`/avvio fallito e mostrare un messaggio con link. Se in test emergono macchine senza runtime, passare a self-contained trimmed (`PublishSingleFile`), cambia solo la riga di publish. Per l'host: usare l'apphost `.exe` come nello spike (`backend\EchoBackend.exe`, `SatelliteResourceLanguages=en` evita le cartelle di lingue).

## Checklist F5 a mano

Ricostruzione: `dotnet publish spike/EchoBackend -c Release -o spike/EchoBackend/bin/publish`, `dotnet restore spike/WebView2Host`, build con `spike/tools/msb.exe`, installare il `.vsix` in `bin/Debug/net472` con `VSIXInstaller /rootSuffix:Exp`, poi `devenv /rootsuffix Exp /updateconfiguration`. Per aprire il pannello senza comando impostare `SVNASSIST_SPIKE_AUTO=1` (si apre dopo 5 s). Oppure F5 su un progetto VSIX equivalente. Log: `%LOCALAPPDATA%\SVNAssist\spike-log.txt`.

- [ ] Tema Chiaro, Scuro, Blu: aprire il pannello, i colori e il testo "tema: ..." corrispondono; Tools > Options > Environment > Color theme per cambiare a caldo (la pagina deve aggiornarsi senza ricaricare, nessun flash bianco).
- [ ] Alto contrasto: i colori seguono Windows.
- [ ] Click nella pagina, poi Del, Ctrl+A, Ctrl+C/V, frecce, Home/End nel campo "testo" e nella lista: la pagina li riceve (vedi riquadro "keydown") e VS non esegue i propri comandi.
- [ ] Tab esce dalla WebView verso gli altri pannelli e Shift+Tab rientra.
- [ ] Pulsante round-trip: compare `OK echo: ...`.
- [ ] Docked, flottante, tab nel document well, undock e redock: la pagina resta viva, tastiera ok, nessuna finestra bianca.
- [ ] Monitor con DPI diversi (trascinare il pannello flottante).
- [ ] Chiudere VS: nessun `EchoBackend.exe` residuo in Gestione attivita'.
- [ ] Facoltativo: provare a far comparire un comando `[VisualStudioContribution]` nel menu Extensions (non e' apparso nello spike).
