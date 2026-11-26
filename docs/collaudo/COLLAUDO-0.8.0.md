# Collaudo SVNAssist 0.8.0 (beta)

Prove da fare a banco con la VSIX di questo artifact. Per ogni prova segna **OK / KO / Saltato**; nei KO scrivi cosa è successo (meglio con uno screenshot).
Il foglio con le stesse prove da compilare: https://claude.ai/artifact/S4D7xQVTuTz6hW4UHDMcCX

Prima di partire:
- Visual Studio 2026 (VS 2022 non è più supportato).
- Una working copy SVN con almeno un ramo (es. `core19`) e un ramo figlio (es. `core19_feature`).
- Per Azure DevOps: accesso al progetto con un account che può modificare i work item, e almeno un work item assegnato a te.

---

## 1. Installazione e opzioni

| # | Cosa fare | Risultato atteso |
|---|---|---|
| 1 | Disinstalla la versione precedente, installa `SVNAssist.vsix`, riavvia VS. | Si installa senza errori. |
| 2 | Strumenti > Opzioni > SVNAssist. | Opzioni moderne dentro la finestra di VS, nessun popup. Le impostazioni di prima (provider AI, layout repository, ecc.) ci sono ancora. |
| 3 | Cambia un'opzione, chiudi e riapri VS. | L'opzione resta salvata. |

## 2. Show Log della riga

| # | Cosa fare | Risultato atteso |
|---|---|---|
| 4 | Su una riga di codice: tasto destro > **SVN: Show Log for Selected Lines**. | Si apre il Log già sulla revisione che ha toccato la riga per ultima; il diff mostra solo il blocco della riga. |
| 5 | Stessa cosa su una riga nata su un altro ramo ed entrata con un merge. | Compaiono anche le revisioni del ramo di origine: la storia non si ferma alla creazione del ramo. |
| 6 | Seleziona un blocco di più righe e ripeti. | Solo le revisioni che hanno toccato almeno una delle righe. |
| 7 | Riga modificata in locale e non committata. | Il Log lo segnala (modifica solo locale) senza errori. |

## 3. Log

| # | Cosa fare | Risultato atteso |
|---|---|---|
| 8 | Apri lo Show Log globale. | La revisione in cui sei è evidenziata (“sei qui”). |
| 9 | Muoviti tra le revisioni con frecce e clic, anche velocemente. | Selezione immediata, nessun salto né blocco. |
| 10 | Nel Log cambia il tema: segue l'estensione / chiaro / scuro. | Cambia solo la finestra del Log; la scelta resta dopo il riavvio. |

## 4. Blame

| # | Cosa fare | Risultato atteso |
|---|---|---|
| 11 | Clicca il blame nella barra in basso. | Accanto alle righe compaiono autore e revisione; il pulsante appare attivo. |
| 12 | Riclicca. | Il blame si spegne (nessun costo quando è spento). |
| 13 | Tasto destro su una riga > **SVN: Who Changed This Line**. | Mostra chi ha cambiato la riga e il messaggio del commit. |
| 14 | Con il blame acceso modifica il file senza salvare. | Il blame resta allineato alle righe giuste. |
| 15 | Menu del margine: Show Log della riga, Ricalcola, Spegni. | Ogni voce fa quello che dice. |

## 5. Update to revision

| # | Cosa fare | Risultato atteso |
|---|---|---|
| 16 | Nel Log: tasto destro su una revisione vecchia > **Update to revision** (working copy). | La working copy va a quella revisione e “sei qui” si sposta. |
| 17 | Stessa cosa su un singolo file. | Cambia solo quel file. |
| 18 | SVN Update normale. | Si torna all'ultima revisione. |

## 6. Azure DevOps: configurazione

| # | Cosa fare | Risultato atteso |
|---|---|---|
| 19 | Con `az login` già fatto: Changes > menu > **Collega Azure DevOps…** | Sceglie da sola Azure CLI, mostra “Accesso come …”, organizzazione e progetto in tendina (scelti da soli se ce n'è uno solo). |
| 20 | Su un PC senza Azure CLI: scegli Azure CLI e premi Verifica. | Controllo rosso con “Come sistemare”: `winget install --id Microsoft.AzureCLI -e` con **Esegui** e **Copia**. |
| 21 | Premi Esegui, completa l'installazione, poi **Accedi (az login)** e **Ho fatto, ricarica**. | Accesso riconosciuto, tendine piene. |
| 22 | Prova con un token PAT (Work Items: Read & write). | Quattro controlli verdi. |
| 23 | PAT scaduto o sbagliato. | “Come sistemare”: pulsante per creare un nuovo token. |
| 24 | Accesso Windows senza client id. | Spiega di chiedere il client id all'IT e che intanto si può usare Azure CLI. |
| 25 | Utente che può leggere ma non modificare. | Controllo scrittura rosso: cosa chiedere all'amministratore del progetto. |
| 26 | **Attiva integrazione**. | Sotto il messaggio di commit compare la riga Work item. |

## 7. Azure DevOps: commit

| # | Cosa fare | Risultato atteso |
|---|---|---|
| 27 | Scrivi un numero nella casella Work item e premi Cerca. | In cima al messaggio: `Bug N - titolo` e il link. |
| 28 | Scrivi `#` nel messaggio. | Tendina con i tuoi work item aperti; Invio sceglie, Esc chiude. |
| 29 | Genera o migliora il messaggio con l'AI dopo aver collegato il work item. | L'intestazione del work item resta intatta. |
| 30 | Apri “Stato dopo il commit”. | Stati letti da Azure, con gli stati intermedi; quelli non permessi sono disattivati con il motivo. |
| 31 | Commit con uno stato scelto. | Su Azure: link alla revisione (sostituisce quello precedente) e stato cambiato; riepilogo nella barra di stato. |
| 32 | Commit con uno stato bloccato da una regola. | Il link viene creato lo stesso, con un avviso. |
| 33 | Apri il link dal work item in Azure. | Porta alla revisione nel repository. |
| 34 | Collega Bug A, committa; poi ricollega Bug A da un'altra revisione. | Il link su Azure punta alla revisione nuova (sostituito, non duplicato). |

## 8. Azure DevOps: rami collegati

| # | Cosa fare | Risultato atteso |
|---|---|---|
| 35 | **SVN Branch/Tag…**: crea un branch con il numero di una feature in “Work item del ramo” e “Passa subito al nuovo branch”. | Dopo lo switch, accanto al nome del ramo: `Feature N · stato · da committare`. In Changes compare il file `.svnassist` (nascosto in Esplora risorse). |
| 36 | Fai un commit qualsiasi sul ramo, anche scegliendo solo alcuni file. | `.svnassist` entra nello stesso commit, nessun commit apposta. Il messaggio non riceve “Feature N” automatico. |
| 37 | Clic sull'etichetta del ramo. | Dialogo “Collega il ramo”: numero, i tuoi work item, Ramo di rilascio, Nessun collegamento. |
| 38 | Collega a mano un ramo esistente (creato senza estensione). | Etichetta aggiornata; il collegamento entra nel prossimo commit. |
| 39 | Segna `core19` (o trunk) come **Ramo di rilascio**. | Etichetta “ramo di rilascio”. |
| 40 | Sul ramo collegato, collega nel commit un **altro** work item (Bug). | Il commit ha intestazione, link e stato del bug; la feature del ramo non viene toccata. |
| 41 | Porta la feature a Resolved/Closed su Azure, poi fai un commit sul ramo. | Avviso “la feature è già chiusa” con Committa comunque / Annulla. |
| 42 | Torna sul ramo di origine, **Merge** dell'intero ramo feature (rientro completo). | Sopra il messaggio: “Rientro completo di … chiudere Feature N?” con **Prepara la chiusura** / **No**. |
| 43 | Prepara la chiusura e committa. | Messaggio con intestazione e link; stato di consegna preselezionato (Resolved/To Test o simile). Su Azure: link alla revisione del merge e stato cambiato. |
| 44 | Merge di **singole revisioni** del ramo feature. | Nessuna proposta di chiusura. |
| 45 | Merge dal ramo principale **verso** il ramo feature (allineamento). | Nessuna proposta di chiusura. |
| 46 | Merge del ramo rientrato verso un terzo ramo (es. core19 → core20). | Nessuna proposta e nessun link aggiunto ai work item delle revisioni portate. |
| 47 | Crea `core20` da `core19` (che è ramo di rilascio). | `core20` non eredita il ruolo: etichetta “Collega work item”. |
| 48 | Merge fatto da un collega senza estensione. | Nessun errore; il messaggio resta leggibile, nessun link creato. |

## 9. Chi non ha l'estensione

| # | Cosa fare | Risultato atteso |
|---|---|---|
| 49 | Apri in TortoiseSVN il log di un commit fatto con Azure. | Messaggio leggibile: `Bug N - titolo`, link, testo. |
| 50 | Apri `.svnassist` con un editor. | Una riga per ramo, comprensibile (`/branches/x = 81018`, `= rilascio`). |

---

Note per chi collauda: la configurazione automatica di Azure (prove 19–25) e il rientro dei rami (42–46) non sono ancora stati provati contro un Azure DevOps vero: sono i primi punti da guardare.
