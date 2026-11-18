# Collaudo 0.6.0

Solo le funzioni nuove. Prima: VS chiuso, PowerShell nella cartella del repo (`C:\dev\SVNAssist`). La simulazione è quella della 0.5.0 (`%TEMP%\svnsim`, wc `Shop.sln`). Le Options restano nell'istanza SvnHost.

```powershell
.\tools\collaudo-mirato.ps1 -Build     # backend + VSIX da zero (serve: il codice è cambiato)
.\tools\collaudo-mirato.ps1            # installa il VSIX in SvnHost, crea la simulazione, apre Shop.sln
```

Segna ogni passo: ✅ ok / ❌ + cosa hai visto. Tutti i passi sono **nuovi e mai provati in VS**.

## Parte A — Options (Tools > Options > SVNAssist > Commit message AI)

| # | Passo | Atteso | Esito |
|---|-------|--------|-------|
| 1 | Apri le Options. | Ci sono **Mostra modello e costo nel messaggio generato** (spento) e **Spiega le revisioni in arrivo prima di SVN Update** (spento). Il provider elenca anche **Gemini**. | |
| 2 | Accendi entrambi i flag, OK, riapri le Options. | I valori restano salvati. | |

## Parte B — Modello e consumo (voci 1-3)

Con "Mostra modello e costo" acceso, in Changes scrivi una modifica e premi **Genera con AI**.

| # | Passo | Atteso | Esito |
|---|-------|--------|-------|
| 3 | Provider **Claude Code** (loggato). | Nella riga di stato: `Sonnet … · N token in / M out · ≈ X $`. Il testo del commit è normale, mai JSON. | |
| 4 | Claude Code con accesso scaduto (es. `claude logout`). | Messaggio d'errore con pulsante **Rifai l'accesso**. | |
| 5 | Claude Code con un modello inesistente nelle Options. | Pulsante **Aggiorna elenco modelli**. | |
| 6 | Provider **Claude API** (chiave Anthropic). | Riga con modello e soli token, **senza costo**. | |
| 7 | Provider **OpenAI-compatibile / GitHub Models**. | Riga con modello e soli token, senza costo. | |
| 8 | Provider **Gemini**: wizard AI o Options, chiave da aistudio.google.com. | L'elenco modelli compare senza il prefisso `models/`. La generazione riesce, riga con token. Senza chiave: **Non collegato**. | |
| 9 | Lingua inglese (Options > Language). | Numeri e testo in formato inglese (`1,240 tokens`). | |

## Parte C — Spiegazione delle revisioni in arrivo (voce 11)

Serve la wc indietro: da un secondo PowerShell `.\tools\collaudo-mirato.ps1 -Collega` (un collega committa un file nuovo).

| # | Passo | Atteso | Esito |
|---|-------|--------|-------|
| 10 | Flag **spento**. Changes > **SVN Update**. | Parte subito, nessun dialogo, nessuna chiamata AI. | |
| 11 | Rifai `-Collega`, flag **acceso**, **SVN Update**. | Dialogo "Sul server ci sono N revisioni nuove": l'elenco (autore, data, messaggio, file A/M/D) c'è subito; il riassunto NON parte da solo (nessuna chiamata AI nel log) e c'è il pulsante **Genera riassunto AI**. | |
| 12 | **Annulla** (o Esc). | Non cambia nulla. | |
| 13 | **Aggiorna ora**. | Parte l'Update con la barra di avanzamento. | |
| 14 | Provider scollegato o accesso scaduto, di nuovo `-Collega`. | Elenco visibile; avviso o pulsante **Rifai l'accesso**; Update funziona comunque. | |
| 15 | Wc già aggiornata, oppure rete spenta. | Update parte come prima, senza dialogo. | |
| 16 | Controlla `logs\backend-*.log`. | All'AI sono arrivati solo messaggi e nomi file, mai diff. | |
| 11a | Click su **Genera riassunto AI**. | "Riassunto AI in preparazione…", poi il riassunto in parole semplici, sotto la riga **modello · token · costo**. Con log enorme la prima riga dice "sulle prime N revisioni di M". Compare **Rigenera**. | |
| 11b | Nel pannello Changes cambia la lingua dei commit message (italiano/English), poi in SVN Update click su **Rigenera**. | Il nuovo riassunto è nella lingua scelta nel pannello (con auto, quella dell'interfaccia). | |
| 11c | Wc indietro di molte revisioni (50 o più), **Genera riassunto AI**. | Il dialogo cresce col contenuto fino a circa l'80% della finestra; il riassunto oltre ~12 righe ha la sua scrollbar; l'elenco revisioni ha la sua scrollbar; **Annulla** e **Aggiorna ora** restano sempre visibili. | |
| 11d | Click su **Aggiorna ora** mentre il riassunto è in preparazione. | Aggiorna ora è attivo, l'Update parte e la chiamata AI viene annullata (nessun errore, nessun riassunto tardivo). **Annulla** durante il riassunto: chiude e annulla l'AI. | |
| 17 | Tema chiaro/scuro e finestra stretta. | Il dialogo scorre, i pulsanti restano visibili. | |

## Parte D — Log (voce 8)

| # | Passo | Atteso | Esito |
|---|-------|--------|-------|
| 18 | Show Log > tasto destro su una revisione che tocca un solo file > **Apri file di questa revisione nell'editor**. | Si apre nell'editor con colori corretti, in sola lettura, titolo `Nome.ext`. | |
| 19 | Revisione con più file: tasto destro su una riga file nel dettaglio. | Ci sono **Diff con la precedente** e **Apri file di questa revisione nell'editor**. | |
| 20 | Revisione in cui il file è cancellato. | Apre la versione precedente e la barra di stato lo dice. | |
| 21 | File binario (png). | Messaggio gentile, non si apre. | |
| 22 | Tasto destro su una revisione > **Copia percorso nel repository**, incolla. | Righe `/trunk/.../File.cs@123`, una per file. Voce grigia se la revisione non ha file; assente in modalità merge. | |
| 23 | Chiudi VS. | Le cartelle in `%TEMP%\SVNAssist\diff` si cancellano. | |

## Parte E — Revert nel diff (voce 4)

| # | Passo | Atteso | Esito |
|---|-------|--------|-------|
| 24 | Modifica un file, **Compare with Unmodified**. Nel pannello destro, a sinistra di ogni blocco: icona annulla. | L'icona c'è. Se manca: `logs\host-*.log`, righe `RevertMargin:`. | |
| 25 | Click sull'icona. | Dialogo "Annullare questa modifica (righe a-b)?". **Annulla** non cambia nulla. **Annulla modifica** riporta il blocco a BASE, il file si ricarica. | |
| 26 | Blocco di sole righe cancellate; file modificato e non salvato. | Il pulsante sta sulla riga che segue il punto; il file viene salvato prima. | |
| 27 | Tasto destro **SVN: Revert Selected Changes**. | Funziona come prima. | |

## Parte F — Altro

| # | Passo | Atteso | Esito |
|---|-------|--------|-------|
| 28 | Dopo un merge fermato da conflitto (parte B della 0.5.0), guarda il pannello. | Mai "No changes": se capita, conserva `logs\backend-*.log` (la voce 10 non è riprodotta). | |
