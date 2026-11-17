# Collaudo mirato 0.5.0

Solo le funzioni nuove. Prima: VS chiuso, PowerShell nella cartella del repo.

```powershell
.\tools\collaudo-mirato.ps1 -Mini
```

Installa il VSIX in SvnHost, crea la simulazione piccola (`%TEMP%\svnsim`), mette un lock di **mrossi** su `Catalog.cs` e apre `wc\Shop.sln`.

Segna ogni passo: ✅ ok / ❌ + cosa hai visto.

## Parte A — wc (Shop.sln)

| # | Passo | Atteso | Esito |
|---|-------|--------|-------|
| 1 | Options > SVNAssist > General: **New revision check (minutes)** = 1. Aspetta un ciclo. | In Changes `Catalog.cs` ha il lucchetto; tooltip "Bloccato da mrossi … Sto rifacendo il catalogo". | |
| 2 | Tasto destro sull'intestazione della sezione Modifiche > **Create Patch...** > salva sul Desktop. | File `.patch` creato, contiene i diff dei file modificati. | |
| 3 | Stessa sezione > **Revert di tutto**. | Revert fatto; compare **Ripristina modifiche**. In `wc\.svn-assist\backups` c'è un `.patch`. `.svn-assist` NON compare tra i non versionati. | |
| 4 | Clic su **Ripristina modifiche**. | Le modifiche tornano; il backup in `.svn-assist\backups` sparisce. | |
| 5 | Di nuovo Revert di tutto, poi un'altra operazione (es. SVN Update). | Il pulsante sparisce e il backup viene eliminato. | |
| 6 | Extensions > SVNAssist > **Apply Patch...** > il file del passo 2. | Anteprima (file toccati), poi applica: modifiche di nuovo presenti. | |
| 6b | Crea un file nuovo `Nota.txt` (non versionato) e modifica `Catalog.cs`. Tasto destro sulla sezione **Non versionati** > **Create Patch...** > salva; poi su **Modifiche** > **Shelve...**, nome di default, **Tieni** spento, OK. | Il `.patch` contiene `Nota.txt` come file nuovo (`(nonexistent)`). Dopo lo Shelve la working copy è pulita (`Nota.txt` nel Cestino, `Catalog.cs` annullato) e compare **Shelved (1)**, chiusa. In `wc\.svn-assist\shelves` c'è il `.patch`, che NON compare tra i non versionati. | |
| 6c | Apri **Shelved**, tasto destro sulla riga > **Unshelve**. | `Nota.txt` torna come A, `Catalog.cs` modificato; lo shelve sparisce. Rifallo con **Shelve** + spunta **Tieni**: la working copy resta com'è; poi **Applica e tieni**: lo shelve resta. | |
| 6d | Su una riga di Shelved: **Salva come .patch...** sul Desktop, poi **Elimina** (conferma). Shelve con un file binario modificato nella selezione. | Copia salvata; Elimina chiede conferma. Col binario compare l'avviso "non entrano nello shelve" e il binario resta modificato. | |
| 7 | In `Order.cs` scrivi tre righe `<<<<<<< mine`, `=======`, `>>>>>>> theirs`, salva, messaggio, **Commit**. | Avviso "contiene marcatori di conflitto. Committare comunque?". Rispondi **No**: niente commit. Poi togli le righe. | |
| 7b | Con VS aperto, da PowerShell nella cartella del repo: `.\tools\collaudo-mirato.ps1 -Collega` (un collega committa un file nuovo). Poi in VS committa una tua modifica. | Prima del commit: "Sul server ci sono revisioni più recenti…" (Committa comunque / Annulla). Con Committa comunque: messaggio ATTENZIONE + pulsante **SVN Update ora**, indicatore arancio. | |
| 8 | Extensions > SVNAssist > **Branch/Tag...**: Branch, nome `prova-050`, spunta **Passa subito**, Crea. | Branch creato (messaggio precompilato), switch con barra in **percentuale** e "N di circa M elementi". | |
| 9 | Riapri Branch/Tag: stesso nome; poi `  x` (spazio iniziale); poi `a:b`. | "esiste già" / nome non valido, il dialogo resta aperto. | |
| 10 | Changes > Switch... : c'è il pulsante **Crea branch/tag...**. Torna su trunk. | Pulsante presente, switch ok. | |
| 11 | **Update to revision** (menu) su una revisione vecchia. | Finestra di avanzamento con Annulla e riepilogo. | |

## Parte B — wc-merge

Chiudi VS, poi `.\tools\collaudo-mirato.ps1 -Parte B`.

| # | Passo | Atteso | Esito |
|---|-------|--------|-------|
| 12 | SVN Merge... > feature-coupon > Merge tutto. | Conflitti di testo. Sezione Conflitti con "Tutti i conflitti di testo (N)" se N ≥ 2. | |
| 13 | **Usa la loro per tutti**. | Conferma con elenco dei file; poi tutti risolti. | |
| 14 | Banner merge > **Annulla merge**. | Backup creato prima (o domanda se manca spazio), revert, poi **Ripristina modifiche** disponibile. | |

## Parte C — Log e Merge (feedback 0.4.0: bug 1, 6, 7 e richieste collegate)

Repository della Parte B (feature-coupon da unire in trunk). Dopo il collaudo allega `%LOCALAPPDATA%\SVNAssist\logs\backend-aaaammgg.log` (righe `merge load/files/preview/run: ... ms`): servono per capire quale fase e' lenta.

| # | Passo | Atteso | Esito |
|---|-------|--------|-------|
| 15 | SVN Merge... > feature-coupon. Nel Log clic su 2-3 revisioni. | Sotto la tabella compare **File coinvolti da r..** (prima "Lettura dei file...", poi A/M/D, `x2` se un file e' in piu' revisioni); la tabella resta fluida. | |
| 16 | Clic su un file dell'elenco; poi su uno aggiunto e uno eliminato. | Diff a due pannelli in VS (aggiunto: sinistra vuota; eliminato: destra vuota). | |
| 17 | **Anteprima** (con scelta, poi senza). | Un gruppo per revisione con i suoi file; sui file in conflitto previsto la **C** rossa e "N possibili conflitti"; sotto "Esito complessivo del dry-run". | |
| 18 | Tasto destro (e tasto Menu / Maiusc+F10) su una riga. | Menu: Seleziona/Deseleziona, Mostra file modificati, Anteprima di questa revisione, Merge di questa revisione, Copia numero revisione, Copia messaggio. Nel log normale (Show Log) anche Diff con la precedente e Confronta con rN. | |
| 19 | Show Log da una cartella o da un file: scegli una revisione. | **Percorsi modificati** con conteggi per azione; spunta **Solo percorsi interessati** (nasconde i percorsi fuori dalla cartella/file); clic su un file = diff r-1:r. | |
| 20 | Modifica `Catalog.cs` (non committare), SVN Merge... > Merge tutto. | Nessun blocco: avviso "N elementi con modifiche non committate", con i file in comune ("rischio di conflitti"). Annulla = nulla cambia. Conferma = merge fatto, banner con "le tue N modifiche pendenti sono nella working copy". | |
| 21 | Banner > **Annulla merge**. | Conferma che parla delle tue modifiche pendenti; poi `Catalog.cs` e ogni altra tua modifica (anche file aggiunti/eliminati, proprieta') identici a prima: "le tue N modifiche pendenti sono state ripristinate". Cartella `.svn-assist\backups\...-pre-merge` sparita. | |
| 22 | Come 20, poi **Commit** del merge. | Commit ok (le tue modifiche pendenti sono incluse) e la cartella `...-pre-merge` sparisce. | |
| 23 | Merge di due revisioni NON adiacenti dove la prima va in conflitto. | Il merge si ferma: Changes mostra la sezione **Conflitti**, un solo messaggio in italiano (revisione in conflitto, "le successive non sono state applicate", cosa fare), banner Annulla merge. Le revisioni non applicate restano in elenco. | |

## Parte D — Merge interrotto: Continua merge (bug 13)

Repository della Parte B: feature-coupon ha 4 revisioni da unire e la terza va in conflitto. Per una prova su molte revisioni usa un ramo con centinaia di revisioni (qui sotto, passo 31).

| # | Passo | Atteso | Esito |
|---|-------|--------|-------|
| 24 | SVN Merge... > feature-coupon > **Merge tutto**. | Finestra con "Applico rN (k di 4)" e barra a revisioni; si ferma alla revisione in conflitto. | |
| 25 | Leggi il banner in Changes. | **Merge interrotto**: "Applicate 3 di 4: r..., r..., r...; ferma alla r... per conflitto in N file.", "Rimaste 1: r....", 3 passi numerati e l'alternativa Annulla merge. Nessun errore in inglese ripetuto. | |
| 26 | Guarda **Continua merge**. | Disabilitato, con il motivo ("Restano N file in conflitto...") sia sotto il pulsante sia nel suggerimento. Premendo il comando non parte nulla. | |
| 27 | Nel Log merge: le 3 applicate e la rimasta. | Blocco **Merge interrotto** identico a Changes; "Applicate in questo merge (3)" con la dicitura "applicata in questo merge"; nella tabella solo la rimasta, evidenziata; Merge tutto / Merge selezionate disabilitati. | |
| 28 | Chiudi Visual Studio (anche da Gestione attivita'), riaprilo sulla stessa solution. | Il banner **Merge interrotto** ricompare con gli stessi numeri (stato salvato in `%LOCALAPPDATA%\SVNAssist\pending-merge.json`). | |
| 29 | Risolvi il conflitto (Usa la loro), poi **Continua merge**. | Abilitato appena non restano conflitti. Parte con "Applico r... (4 di 4)" e applica SOLO la rimasta; al termine banner "da committare" e, nei Recenti, il messaggio con TUTTE le 4 revisioni. | |
| 30 | Ripeti 24-25, poi **Annulla merge**. | Conferma; la working copy torna com'era (con modifiche pendenti ripristinate se c'erano); il banner e lo stato spariscono; le 4 revisioni tornano da unire. | |
| 31 | Ramo con centinaia di revisioni: Merge tutto; durante l'avanzamento premi **Annulla**. | Il pulsante diventa "Annullamento…": si ferma alla fine del passaggio in corso, il banner dice "Applicate N di TOT", **Continua merge** riprende dalle rimaste (numerazione sull'intero merge). | |
| 32 | Con un merge interrotto, fai SVN Update (o Switch) e riapri Changes. | Banner senza Continua merge: spiega che la working copy e' cambiata; restano **Annulla merge** e **Scarta stato**. | |
| 33 | Merge da una cartella (Solution Explorer > SVN Merge...) con conflitto a meta'. | Stesso banner; Continua e Annulla agiscono solo su quella cartella. | |

## Non provabile qui
- Avviso "spazio insufficiente per il backup" (serve un disco quasi pieno); col merge su modifiche pendenti il merge non parte affatto (provato nei test con spazio simulato).
- VS 2022 (nessuna macchina disponibile).
