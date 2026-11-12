# Host `src/SVNAssist.Host` (net472, in-proc)

Package VSSDK classico (`AsyncPackage`) con due tool window ("SVN Changes", "SVN Log") che ospitano la UI web in WebView2, un relay verso `SVNAssist.Backend.exe` (StreamJsonRpc su stdio) e i comandi `.vsct`. Contratto: `src/Shared/RpcContract.cs`.

## Build
```
dotnet publish src/SVNAssist.Backend -c Release -o src/SVNAssist.Backend/bin/publish   # framework-dependent
dotnet restore src/SVNAssist.Host
msbuild src/SVNAssist.Host/SVNAssist.Host.csproj -p:Configuration=Release              # -> bin/Release/net472/SVNAssist.Host.vsix
```
- Il VSIX contiene `ui/` (= `src/SVNAssist.UI.Web` senza `contract/`) e `backend/` (= `BackendPublishDir`, default `src/SVNAssist.Backend/bin/publish`; si cambia con `-p:BackendPublishDir=C:\dir\`). Senza backend la build avvisa e il VSIX non lo contiene.
- Con il MSBuild di VS 18 senza `MSBuild.exe.config` usare il launcher `tools/msb.cs` (compilarlo con `csc`: esegue MSBuild in un AppDomain con la config presa dal template; vedi `webview2-spike.md`). La versione del manifest deve coincidere con `Directory.Build.props` (la build fallisce se no).
- Test in VS: installare il `.vsix` in un'istanza sperimentale (`VSIXInstaller /rootSuffix:...`), poi `devenv /rootsuffix ... /updateconfiguration`. Log dell'host: `%LOCALAPPDATA%\SVNAssist\host-log.txt`. I comandi hanno nome (`SVNAssist.Changes`, `SVNAssist.Log`, ...).

## Comportamento
- Il backend parte alla prima vista/comando (non all'avvio di VS), muore con VS (Job Object KILL_ON_JOB_CLOSE), si riavvia se cade (max 4/min) e l'host rimanda `ready` di ogni vista, `SolutionChangedAsync`, `ThemeChangedAsync` e l'evento `backendRestarted`.
- Relay: pagina -> `IBackend.DispatchAsync(view, json)` (solo `ready` e `cmd`); backend -> `IHost.PushAsync(view, json)`. Il backend deve coalescere gli `state` (~16 ms), l'host non lo fa. Se il backend non risponde l'host manda `reply ok:false` / toast.
- Tema: `ThemeBridge` manda `theme{kind,vars}` al `ready`, a `VSColorTheme.ThemeChanged`, a cambio `windowTheme`; solo le variabili derivabili da VS (sfondi, testi, bordi, selezione, link, font): colori di stato/diff/sintassi restano quelli di `tokens.css` per il `kind`. Alto contrasto: colori di sistema.
- Impostazioni: Tools > Options > SVNAssist (chiavi camelCase del contratto; `GetSettingsAsync` ritorna anche gli alias minuscoli della 0.1.0). Le API key sono cifrate con DPAPI nel registro.
- `IHost.ShowLogAsync(path)` mostra la finestra Log e inoltra `logLoad{path}`.
- Menu: Extensions > SVNAssist (Changes, Commit..., Update, Log); Solution Explorer > SVN (Update, Commit..., Diff, Log, Add, Revert, Ignore). "Update" del menu Extensions invia il cmd `update` alla vista `changes`.

## Aggiunte al contratto (rispetto alla prima versione)
- `IBackend.ThemeChangedAsync(string kind)`.
- `IBackend.RunPathCommandAsync(string command, string[] paths)`: comandi del menu contestuale di Solution Explorer (`update|add|revert|diff|ignore|log|commit`) sui percorsi selezionati (file, cartelle, .sln/.csproj: la risoluzione a cartella e' del backend, come `SvnTarget`). Per `commit` l'host apre prima Changes, per `log` apre Log.
