# Changelog

Elenco delle modifiche di SVNAssist per versione, dalla più recente alla più vecchia. I nomi di pulsanti, menu e finestre sono quelli dell'interfaccia, in inglese. Le voci con "(da verificare)" sono dichiarate ma non ancora provate in Visual Studio.

## 0.4.0 (2026-11-15)

Contenuti: Solution Explorer (icone di stato, SVN Add, Show Log), opzioni di Show Log, selezione multipla in Changes, Cleanup con opzioni, finestra di avanzamento con Annulla, recupero dello switch interrotto. Include le modifiche della 0.3.2, che non è stata rilasciata a parte.

Installazione: come la 0.2.0.

### Novità

- **Selezione multipla in Changes.** Ctrl+click aggiunge o toglie un elemento, Maiusc+click seleziona un intervallo (anche tra sezioni). Da tastiera: Maiusc+frecce, Ctrl+Spazio, Ctrl+A. Il tasto destro mostra le azioni valide per tutta la selezione, con il conteggio. Spazio e Canc valgono per tutti gli elementi selezionati.
- **Canc sulle cartelle.** Una cartella versionata viene eliminata con svn delete (resta da committare); una non versionata va nel cestino. La conferma elenca gli elementi.
- **AI: Più corto e Più dettagliato.** Il menu del messaggio di commit ha queste due voci oltre a Genera.
- **Show Log: Stop on copy/rename.** L'opzione è spenta di default: il Log mostra la storia completa oltre le copie e le rinomine.
- **Show Log: Include merged revisions.** Le revisioni entrate per merge sono annidate sotto il merge; un merge inverso è segnalato.
- **Show Log: diff dei file di una revisione.** Un click su un file di una revisione apre il diff di quella revisione, anche per le revisioni unite. Le opzioni si cambiano mentre il Log è aperto e il Log si ricarica.
- **Icone di stato SVN in Solution Explorer.** Normale, modificato, aggiunto, non versionato, conflitto, eliminato. Sono attive di default e si disattivano da Options. SVNAssist si registra come provider di source control solo per le soluzioni dentro una working copy SVN.
- **Menu di Solution Explorer.**
  - **Show Log** per file, progetto e cartella.
  - **SVN Add** per gli elementi non versionati, anche con selezione multipla.
  - **Check for Modifications** per i file modificati: apre il diff di Visual Studio rispetto a BASE.
- **Avviso prima dello Switch.** Se la working copy ha modifiche, compare un avviso con i conteggi per tipo e tre scelte: Continua comunque, Apri Cleanup, Annulla.
- **Cleanup con opzioni.** Da ogni punto di avvio: Break locks, Fix timestamps, Vacuum pristines, Include externals. Le opzioni distruttive (Delete unversioned, Delete ignored, Revert) sono in rosso e chiedono conferma.
  - Delete unversioned e Delete ignored saltano sempre la cartella .vs e i file tenuti aperti da Visual Studio o dal debug. Il riepilogo indica "Eliminati N, saltati M perché in uso".
  - Con svn precedente alla 1.9 le opzioni oltre il cleanup semplice sono disabilitate.
- **Finestra di avanzamento** per Switch, Update e Merge. Mostra la fase corrente, le righe di svn in sequenza e i conflitti evidenziati. Il riepilogo finale ha il pulsante **Apri i conflitti**. L'avanzamento non ha percentuale: barra indeterminata e contatore.
- **Annulla in Switch, Update e Merge.**
  - Switch o update annullato: esegue un cleanup, avvisa che la working copy può essere a metà e offre Cleanup..., Completa switch o Torna a prima dello switch (per l'update, Riprova update).
  - Merge annullato: offre il revert delle modifiche del merge (con conferma) oppure Lascia così.
- **Torna a prima dello switch.** Ripristina l'URL e la revisione di partenza, salvati prima dello switch. Le modifiche locali restano.
- **Prerequisiti senza riavvio del PC.** Dopo l'installazione di svn, .NET o degli strumenti AI da riga di comando, SVNAssist rilegge il PATH aggiornato. Se un programma è presente ma non parte, il messaggio chiede di riavviare Visual Studio.

### Modifiche

- **Cartella .vs.** Resta tra i non versionati, per poterla ignorare, ma non rende modificata la working copy. Commit All e l'avviso dello Switch non la considerano.
- **Commit All con file nuovi.** Se ci sono file non versionati, chiede "Aggiungerli al commit?" con le scelte: Sì, aggiungi e committa; No, solo i versionati; Annulla. La domanda compare anche quando ci sono solo file nuovi. Commit Staged non cambia.
- **Show Log.** L'elenco parte da HEAD, come TortoiseSVN.
- **Errori dello switch.** Gli errori su più righe si mostrano per intero e si possono copiare.

### Correzioni

- **Show Log dopo un commit.** Prima: il commit appena fatto non compariva senza un update. Ora: compare subito.
- **"Switch incompleto".** Prima: la voce restava anche quando la working copy era coerente. Ora: viene tolta da sola se l'URL è quello di partenza o di destinazione e non ci sono cartelle switched o incomplete né lock.
- **Switch interrotto da E155009** (inclusa dalla 0.3.2). Prima: svn si fermava a metà quando una cartella era tenuta aperta (da Visual Studio, dal designer o da un antivirus); l'errore compariva su una riga troncata, l'intestazione mostrava il ramo vecchio, il selettore segnava il nuovo come "(corrente)" e rifiutava di rifare lo switch. Ora:
  - SVNAssist esegue cleanup, attende 2 secondi e ripete lo switch una volta.
  - Se lo switch resta a metà, l'errore lo dice e indica di non usare Revert. Il pulsante **Completa switch** esegue cleanup e lo stesso switch.
  - L'intestazione mostra "ramo (switch incompleto)" e il selettore indica la destinazione come "(switch incompleto)". Lo stato resta dopo la chiusura di Visual Studio.
  - Uno switch fallito senza toccare la working copy (URL sbagliato, credenziali) resta un errore normale.
  - L'errore E155009 propone Esegui Cleanup anche fuori dallo switch.
- **Errori su una riga.** Prima: gli errori lunghi comparivano troncati su una riga. Ora: vanno a capo nella barra in basso.

## 0.3.1 (2026-11-12)

Contenuti: correzioni su Annulla righe, controllo delle nuove revisioni e file di log. Si installa sopra la 0.3.0.

Installazione: come la 0.2.0.

### Modifiche

- **Log dell'host.** Il log viene scritto nella cartella dei log di SVNAssist, un file per giorno. Più istanze di Visual Studio possono scriverlo insieme. Il vecchio file di log non è più usato.
- **Diagnostica del pulsante Annulla nel diff.** Il log riporta i ruoli della vista, se il visualizzatore è stato trovato, il numero di differenze e di pulsanti creati.

### Correzioni

- **Ctrl+Z dopo Annulla le righe.** Prima: Ctrl+Z non annullava l'operazione se il file era aperto. Ora: se il file è aperto (editor o diff) la modifica viene applicata al buffer con un'unica operazione annullabile e poi salvata. Se il file non è aperto, viene scritto su disco come prima.
- **Controllo delle nuove revisioni.** Prima: partiva anche con Visual Studio in secondo piano. Ora: non parte in secondo piano; tornando in primo piano controlla subito se l'ultimo controllo è più vecchio dell'intervallo.

Limiti noti:

- Il pulsante Annulla accanto ai blocchi del diff non è verificato in Visual Studio (da verificare).
- Un file aperto solo come pannello di un diff, senza finestra editor propria, viene ancora riscritto su disco: Ctrl+Z non lo annulla.
- Con modifiche non salvate, le righe di "modifica successiva/precedente" possono spostarsi.

## 0.3.0 (2026-11-12)

Contenuti: Switch, Merge da un branch, risoluzione dei conflitti, Annulla righe nel diff, indicatore della revisione, Recenti, Genera con AI su tutte le modifiche.

Installazione: come la 0.2.0. Se è installata la 0.2.0, viene aggiornata. Client svn 1.8 o successivo.

### Novità

- **Switch.** Il ramo corrente è sempre visibile in SVN Changes. **Switch...** si apre da Changes o da Extensions > SVNAssist.
  - Il selettore mostra gli ultimi usati e ha la ricerca.
  - La vista estesa, per repository con centinaia di branch e tag, ha filtri per nome, tipo, autore e periodo e l'ordinamento per nome, data, revisione o autore.
  - Il layout del repository (trunk/branches/tags) è configurabile, anche con il progetto alla radice o in una sottocartella.
- **SVN Merge...** Si sceglie il branch e il Log si apre come documento con le sole revisioni da unire.
  - **Anteprima** esegue una simulazione senza modificare i file.
  - **Merge tutto** e **Merge selezionate** (cherry-pick).
  - Se la working copy ha modifiche o revisioni miste, un messaggio lo spiega e offre **SVN Revert di tutto…** e **SVN Update ora**.
  - **Annulla merge** dal banner, con conferma ed elenco dei file.
  - Il messaggio nello stile di TortoiseSVN ("Merged revision(s) 8-10 from …") viene salvato nei Recenti.
- **Conflitti.** Il riquadro Conflitti in SVN Changes offre per ogni file: **Apri editor di merge** (editor a 3 vie di Visual Studio), **Usa la mia**, **Usa la loro**, **Segna come risolto**.
  - I conflitti ad albero sono raggruppati, con **Segna tutti come risolti**.
  - I file di appoggio creati da svn stanno sotto il file in conflitto e non tra i non versionati.
- **Annulla le singole righe.** Un pulsante Annulla accanto a ogni blocco del diff, oppure tasto destro > **SVN: Revert Selected Changes** su una selezione. Il pulsante non è stato verificato in tutte le viste diff di Visual Studio (da verificare).
- **Modifica successiva / precedente nel diff.** Alt+F8 e Shift+Alt+F8. Le scorciatoie si cambiano nelle opzioni.
- **Indicatore della revisione.** In basso in SVN Changes: verde se la working copy è all'ultima revisione; arancione, con "disponibile rN — SVN Update", se altri hanno committato. Il controllo è di sola lettura, ogni 5 minuti; l'intervallo si cambia o si disattiva in Tools > Options.
- **Recenti.** Pannello con ricerca e anteprima degli ultimi 25 messaggi di commit per repository.
- **Revert di tutto per sezione.** Il tasto destro su Changes o su Staged offre **SVN Revert di tutto…**, con conferma.
- **Genera con AI su tutte le modifiche.** I diff grandi vengono divisi in blocchi, riassunti e ricomposti in un unico messaggio (titolo e elenco per area), anche con centinaia di file. Il messaggio segue le regole di commit del progetto e quelle globali, per esempio Conventional Commits.

### Modifiche

- Il Log si apre sempre nell'area documenti, accanto ai diff.
- Il doppio click su un file aggiunto o non versionato apre il file intero.
- I messaggi di esito scompaiono dopo qualche secondo; gli errori restano.
- Il sottomenu SVN di Esplora soluzioni è stato rimosso, perché le azioni sono disponibili in SVN Changes.

### Correzioni

- **Genera con AI, Copilot.** Prima: errore "No response was returned". Ora: nuovo tentativo con una richiesta più piccola e messaggio di errore.
- **Genera con AI, provider compatibili con OpenAI.** Prima: errore "'0' is an invalid start of a value". Ora: nuovo tentativo con una richiesta più piccola e messaggio di errore.
- **Genera con AI, formato.** Prima: il messaggio generato era racchiuso in un blocco di codice. Ora: il blocco non viene aggiunto.
- **Finestre ripristinate all'avvio.** Prima: una finestra di SVNAssist ripristinata da Visual Studio poteva dare l'errore "Failed to find the tool window". Ora: se la finestra non si crea, il motivo compare nella finestra stessa.

Limiti noti:

- Crea branch/tag e una procedura guidata per il merge non sono presenti.
- Su repository molto lenti Switch e Merge non hanno l'avanzamento.
- Provata su Visual Studio 2026 (18.x); Visual Studio 2022 dichiarata, non provata (da verificare).

## 0.2.0 (2026-11-12)

Contenuti: pannello SVN Changes, Log come documento, diff di Visual Studio, Update to revision, Cleanup, Lock / Unlock, login SVN, procedura guidata per l'AI, temi, lingua.

### Novità

- **Pannello SVN Changes in stile Git.** Mostra i file in un albero, con le sezioni Staged, Modifiche e Non versionati. Il pannello ha un filtro e si usa da tastiera (frecce, Spazio, Invio; Canc elimina con conferma). Il pulsante di commit è "Commit All" se Staged è vuoto, altrimenti "Commit Staged (n)", dove n è il numero di file in Staged. Ogni azione mostra un messaggio di stato.
- **Log come documento.** Show Log si apre come documento nell'area editor di Visual Studio, con una tabella delle revisioni. Filtri per autore, testo, percorso e date. Per ogni revisione sono elencati i file cambiati. Ctrl+click su due revisioni le confronta. Il comando è disponibile anche per file e cartelle da Esplora soluzioni.
- **Diff di Visual Studio.** Le differenze tra working copy, revisioni e cartelle si aprono nel visualizzatore di Visual Studio. Per il diff non è richiesto TortoiseSVN.
- **Update a revisione, Cleanup, Lock / Unlock.**
  - "SVN Update to revision": si sceglie la revisione da un elenco.
  - "SVN Cleanup": comando del menu di Visual Studio. Se la working copy risulta bloccata, compare anche il pulsante Cleanup.
  - "Lock / Unlock": blocca e sblocca i file.
- **Login SVN.** La password viene chiesta solo se quella salvata nella cache di Subversion manca o è scaduta. Con l'autenticazione integrata di Windows non viene mai chiesta. La cache è la stessa usata da TortoiseSVN.
- **Procedura guidata per l'AI.** Passi: scelta del servizio (GitHub Copilot, Claude Code, Claude API, GitHub Models), installazione dei prerequisiti mancanti, login, scelta del modello. Si riapre da Extensions > SVNAssist > "Change AI Provider...". I prerequisiti sono indicati in verde o in rosso e sono cliccabili. Per GitHub Models il modello usato è openai/gpt-4o-mini.
- **Temi.** Light, Dark, Blue o High Contrast. Il cambio si applica senza riavviare Visual Studio.
- **Lingua.** Italiano o English, da Extensions > SVNAssist > Language / Lingua oppure da Tools > Options. "Automatica" segue la lingua di Visual Studio. Il cambio si applica senza riavviare Visual Studio e riguarda finestre, messaggi del servizio, dialoghi e impostazioni. I nomi dei comandi SVN e i messaggi del programma svn non cambiano. La lingua dei messaggi di commit generati con AI è un'impostazione separata.

### Modifiche

- **Modelli AI da menu a tendina.** Il modello si sceglie da un menu a tendina (Claude, Copilot, GitHub Models). Il modello predefinito di Claude è Sonnet. Prima il predefinito era Opus.
- **Prerequisiti per provider.** Nella procedura guidata sono mostrati solo i prerequisiti del servizio scelto, con note su login, chiave e costo.
- **Modello salvato non più in elenco.** Se il modello salvato non compare più nell'elenco, si usa il modello predefinito.
- **Voci di menu in inglese.** "Change AI Provider...", "Diff with BASE", "Show Log" e "Add to Ignore List" sono in inglese standard di SVN.

### Correzioni

- **Temi Dark e Blue.** Prima: testo nero su sfondo nero. Ora: le finestre Changes e Log usano i colori di Visual Studio.
- **Genera con AI.**
  - Errori e annullamenti: prima venivano mostrati "OK" senza messaggio. Ora il motivo compare nella barra di stato.
  - Login: prima, dopo la chiusura del popup una volta, non si apriva più nulla. Ora il popup ricompare a ogni tentativo.
  - AI non configurata: propone il login dal terminale (con GitHub CLI o con Claude Code), in alternativa un token.
  - File aggiunti, eliminati, vuoti o binari compaiono nel messaggio di commit generato.
  - Con molti file modificati la generazione funziona, perché il diff viene compattato.
  - Segue le regole di commit definite nelle istruzioni del progetto per GitHub Copilot e in quelle personali per Copilot.
- **Seleziona tutto.** Prima selezionava anche i file non versionati delle cartelle di build (bin, obj). Ora seleziona solo la sezione Modifiche.
- **Working Copy Path.** Il percorso impostato in Working Copy Path ha la precedenza sul rilevamento automatico, anche nel Log.
- **Operazione già in corso.** Prima un secondo click non faceva nulla. Ora mostra "Operazione in corso…".
- **Menu del tasto destro.** Le voci si aggiornano quando cambia lo stato di un file.

### Requisiti e installazione

- Visual Studio 2026 (18.x).
- Visual Studio 2022: dichiarata, non ancora provata (da verificare).
- Un client SVN sul PC che fornisca il comando svn da riga di comando: SlikSVN, TortoiseSVN con gli strumenti da riga di comando, o altro.
- Runtime .NET 10. Se manca, SVNAssist propone di installarlo, oppure apre la pagina da cui scaricarlo (.NET Runtime 10.x, Windows x64).
- GitHub Models con il token di GitHub CLI: con errore 401 o 403, creare un token con il permesso `models:read`.
- La 0.1.0 è deprecata: disinstallarla e installare la 0.2.0.

Installazione:

1. Chiudere Visual Studio.
2. Aprire con doppio click il pacchetto .vsix di SVNAssist.
3. Riavviare Visual Studio.

La demo interattiva è in un archivio separato.

## Versione 0.1.0

La 0.1.0 è deprecata e sostituita dalla 0.2.0: disinstallarla e installare la 0.2.0. Non ha una sezione propria.

## Dettagli tecnici

Il testo tecnico di ogni versione è nei file seguenti.

- 0.5.0: docs/release-notes/v0.5.0.md
- 0.4.0: docs/release-notes/v0.4.0.md (include la 0.3.2: docs/release-notes/v0.3.2.md)
- 0.3.1: docs/release-notes/v0.3.1.md
- 0.3.0: docs/release-notes/v0.3.0.md
- 0.2.0: docs/release-notes/v0.2.0.md
