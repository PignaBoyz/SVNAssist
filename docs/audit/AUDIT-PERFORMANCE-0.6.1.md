# Audit prestazioni SVNAssist 0.6.1

> Audit di sola lettura, fatto leggendo il codice (nessun codice modificato). In questo ambiente non ci sono svn, dotnet né Visual Studio: **niente è stato misurato**. I punti sono divisi in *confermato nel codice* (il meccanismo c'è, lo si legge) e *ipotesi* (plausibile, serve una misura sul PC lento).

## Come è fatta l'estensione (rilevante per la pesantezza)

- **Host** in-process dentro `devenv.exe` (net472, VSSDK): provider SCC per i glifi di Solution Explorer, margine `RevertMargin` negli editor, InfoBar dei conflitti, WebView2 per Changes/Log/Options.
- **Backend** = processo separato `SVNAssist.Backend.exe` (.NET 10) che lancia `svn.exe` e tiene lo stato; parla con l'host via JSON-RPC.
- Conseguenza: il backend può rendere il PC lento ma **non può far crashare VS**. Blocchi e crash di `devenv.exe` vengono dal lavoro nell'host (thread UI, memoria, WebView2). Per questo l'ordine sotto parte dall'host.

## A. Confermato nel codice

### A1. Tempesta di aggiornamento dei glifi (priorità alta, host / thread UI)
- `ChangesStore.RefreshAsync` lancia **sempre** `EntriesChanged` (`Stores/ChangesStore.cs`, dopo `_entries = newEntries`), anche se lo stato non è cambiato.
- Il backend avvisa l'host, che dopo 300 ms richiama `GetSccStatesAsync` (copia l'intero dizionario degli stati) e `SvnSccService.SetStates` (`Host/SvnSccService.cs`) crea un nuovo dizionario e **chiama `SccGlyphChanged(0, null, null, null)` su ogni progetto caricato**, sul thread UI. Non c'è alcun confronto con lo stato precedente.
- Il refresh parte da `FileSystemWatcher` ricorsivo su tutta la working copy con quiete di 800 ms (`RefreshQuietPeriod`). Il filtro `WorkingCopyPathFilter` ignora solo `.svn, .vs, .svn-assist, bin, obj, node_modules`: non copre `x64/`, `Debug/`, `Release/`, `packages/`, `.git`, file temporanei, né i rename temp+move che VS fa a ogni salvataggio.
- Effetto: ogni salvataggio/build = un `svn status --xml` sull'intera WC + serializzazione + ridisegno dei glifi di tutta la solution. Su una solution grande e un PC lento è la fonte più probabile di UI che si blocca.
- **Intervento**: (1) confrontare il nuovo dizionario con il vecchio e notificare solo se è diverso; (2) allargare il filtro del rumore (temp, `x64`, `Debug`, `Release`, `packages`, `.git`, `*.tmp`, `~*`); (3) ridisegnare i glifi solo per i progetti/percorsi cambiati, non tutti.

### A2. `svn status -u` ogni 5 minuti su tutta la WC (priorità alta su WC grandi)
- `HeadWatcher` (default `HeadCheckMinutes = 5`) a ogni giro chiama `FetchHeadAsync`, che se la HEAD è nota esegue `FetchRemoteLocksAsync` → `svn status -u --xml` (`SvnService.GetRemoteLocksAsync`).
- `status -u` scorre **tutta** la working copy e interroga il server: è la chiamata svn più costosa che l'estensione fa, ripetuta anche con la finestra Changes chiusa (parte con VS in primo piano).
- Il risultato serve solo a mostrare i lock altrui sui file già presenti nell'albero (commento `shortcut` nel codice).
- **Intervento**: limitarsi ai percorsi già in `_entries` (`svn info --xml` / `svn status -u` con target espliciti, usando `RunSvnWithTargetsAsync` già presente), oppure fare il controllo lock solo quando Changes è visibile o prima di un commit; alzare il default degli intervalli.

### A3. Ogni notifica ricostruisce e serializza tutto lo snapshot (backend, CPU/memoria)
- `ChangesStore.GetSnapshot()` rifà sezioni, righe dell'albero e `GetSnapshot()` di tutti i sotto-store (Prerequisites, Wizard, RevPicker, Switch, Progress, Incoming, …) sotto `_stateLock`; `BackendServer.FlushAsync` lo serializza in JSON e **solo dopo** confronta la firma con l'ultimo invio.
- Ogni `NotifyChanged` (anche solo una riga di progresso, ogni 150 ms nelle operazioni lunghe) paga il costo completo anche se non si invia nulla. Più `GroupBy`/`ToDictionary` in `SccStates()`.
- **Intervento**: dirty-flag per sotto-snapshot (cache della parte "albero" finché `_entries`/selezioni non cambiano), oppure confronto prima di serializzare; non rifare `BuildRows` se cambia solo il progresso.

### A4. Il backend parte da solo con ogni solution SVN
- `HostService.OnSolutionChangedAsync` dice "il backend parte solo quando serve", ma `SvnSccService.SetActive` → `StartSccAsync` → `_backend.GetAsync()` **lo avvia** appena il provider SCC si attiva (default `ShowSccGlyphs = true`, solution sotto una cartella con `.svn`).
- Quindi: processo .NET 10 + runtime + stores + watcher + `svn status` + HEAD check + (con `StartSccAsync`) invio stati, all'apertura della solution, anche se l'utente non apre mai SVNAssist. Su PC lento si somma a tutto il resto che VS sta caricando.
- **Intervento**: avvio ritardato (idle, qualche secondo dopo il caricamento della solution) e opzione "icone SCC" spenta di default sulle WC grandi; oppure avvio solo alla prima richiesta di glifi dopo che la solution è stabile.

### A5. `RevertMargin` in ogni editor (host, thread UI; peso medio-basso)
- Esportato con ruoli `Document` e `Editable` (`Host/RevertMargin.cs`): viene creato in **ogni** editor di testo. `Refresh()` gira a ogni `LayoutChanged` (scroll, digitazione): `Children.Clear()`, `TryGetViewerForTextView`, `string.Join(",", _view.Roles)` e costruzione del messaggio di log, anche se la vista non è un diff.
- Nel diff destro ricrea tutti i pulsanti a ogni layout e fa `Enumerable.Count(diff.LineDifferences)` ogni volta.
- Non fa chiamate RPC (verificato): il costo è CPU/allocazioni sul thread UI, non svn.
- **Intervento**: decidere una volta "non è un diff" e disiscriversi da `LayoutChanged`; non costruire stringhe di log fuori dal ramo che serve; nel diff ridisegnare i pulsanti solo se cambiano differenze o viewport.

### A6. Altri costi del watcher
- `FileSystemWatcher` ricorsivo con buffer di default (8 KB): su PC lenti o durante build/checkout enormi va in overflow e l'handler `Error` richiede un refresh completo (altro `svn status`). `OnFsChanged` per ogni evento fa `Split` del percorso e `Any` (allocazioni).
- **Intervento**: `InternalBufferSize` più grande (64 KB), filtro più economico (`IndexOf` su segmenti noti), niente refresh pieno su overflow se ce n'è già uno in coda.

### A7. UI / WebView2
- Ogni vista (Changes, Log, Options) è un WebView2 (un processo `msedgewebview2.exe` ciascuno, ~100–200 MB l'uno: **ipotesi sul peso reale**, da misurare). L'ambiente è condiviso (`WebViewHost.GetEnvironment`), bene.
- `changes.js` ridisegna a ogni stato con `innerHTML` (`render()`), quindi un refresh = riscrittura del DOM della lista; il selettore Switch/Merge è già virtuale, la lista delle modifiche no (per gli 'unversioned' svn mostra le cartelle come una sola voce `?`, quindi il caso grave è solo molte modifiche/non versionati in cartelle versionate).
- **Intervento**: lista virtuale anche per l'albero delle modifiche e render incrementale se lo snapshot è invariato.

### A8. Bassa priorità
- `ConflictInfoBar.cs:131`: `Delay(1500)` è una pausa una tantum dopo il clic su un'azione, **non** un polling. Nessun problema.
- `MessagePack 2.5.192` ha avvisi di sicurezza (già annotato nell'HANDOFF).

## B. Ipotesi sul crash (non dimostrabili dal codice)
Non si può diagnosticare un crash senza dati. Candidati, in ordine di plausibilità:
1. **Memoria**: `devenv.exe` (32/64-bit? in VS 2022/2026 è 64-bit) + `msedgewebview2.exe` ×3 + `SVNAssist.Backend.exe` + `svn.exe` su un PC con poca RAM → OOM / swap → crash di VS. Più probabile se la solution è grande.
2. **Thread UI bloccato** da A1/A5 fino al watchdog di VS ("Visual Studio non risponde") e chiusura forzata.
3. **Crash di WebView2** (runtime mancante/vecchio o profilo in `DataFolder` bloccato) che trascina la tool window.
4. **Backend che muore** e riavvii in loop (`BackendProcess` ha il riavvio automatico: se `svn` o l'avvio falliscono si può ciclare).

## C. Cosa serve dal PC lento (per passare da ipotesi a numeri)
- `%LOCALAPPDATA%\SVNAssist\logs\host-*.log` e `backend-*.log` (con la sessione in cui crasha).
- Visualizzatore eventi → Registri di Windows → Applicazione: eventi `.NET Runtime` / `Application Error` per `devenv.exe`, `SVNAssist.Backend.exe`, `msedgewebview2.exe`.
- ActivityLog di VS: `devenv /log`, poi `%APPDATA%\Microsoft\VisualStudio\<versione>\ActivityLog.xml`.
- RAM del PC, versione di VS, e dimensioni della working copy: numero di file e di file `?`; durata di `svn status` lanciato a mano nella cartella.

## D. Strumentazione proposta (piccola)
Scrivere nel `backend-*.log` (esiste già `BackendLog`): durata di `svn status`, numero di voci, dimensione in byte dello snapshot serializzato, frequenza dei refresh; nell'host: durata di `SetStates` e dei glifi. Così il prossimo log dal PC lento contiene i numeri.

## E. Ordine di intervento suggerito
1. A1 (confronto stati + filtro rumore + glifi mirati) — piccolo e a basso rischio.
2. A2 (lock solo sui percorsi in elenco / solo quando serve).
3. A5 (RevertMargin: disiscrizione se non è un diff).
4. A4 (avvio ritardato del backend / glifi opzionali).
5. A3, A6, A7 (snapshot a cache, buffer watcher, lista virtuale).
6. D (strumentazione) in parallelo, per misurare davvero.
