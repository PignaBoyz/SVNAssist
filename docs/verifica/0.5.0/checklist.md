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

## Non provabile qui
- Avviso "spazio insufficiente per il backup" (serve un disco quasi pieno).
- VS 2022 (nessuna macchina disponibile).
