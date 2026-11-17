# Changelog

Elenco delle modifiche di SVNAssist per versione, dalla più recente alla più vecchia. I nomi di pulsanti, menu e finestre sono quelli dell'interfaccia, in inglese. Le voci con "(da verificare)" sono dichiarate ma non ancora provate in Visual Studio.

## 0.5.0 (in preparazione)

Contenuti: Branch/Tag, Create Patch, Apply Patch, Shelve, controlli sul server al commit, lock di altri utenti, risoluzione dei conflitti per gruppo, elenco dei modelli AI dal provider, Merge da cartella, Merge applicato una revisione alla volta.

Installazione: come la 0.2.0. Provata su Visual Studio 18 (2026). Visual Studio 2022 (17.x) è dichiarata come supportata ma non provata (da verificare).

### Novità

- **SVN Branch/Tag...** Crea un branch o un tag sul server a partire da HEAD del ramo corrente, da una revisione o dalla working copy (con le modifiche locali). Si apre dal menu SVNAssist, da "Altre azioni" in Changes e dal selettore di Switch.
  - Il messaggio di log è precompilato.
  - L'opzione **Passa subito al nuovo branch** esegue lo switch dopo la creazione.
  - Il nome viene controllato. Se esiste già, compare un errore e il dialogo resta aperto.
- **Layout del repository rilevato da solo.** Se l'URL non corrisponde al layout impostato nelle Options, SVNAssist riconosce le cartelle trunk/main, branches/branch e tags/tag. Il layout rilevato è usato da Switch, Merge, Branch/Tag e dall'etichetta del ramo.
- **Create Patch... e Apply Patch...**
  - Create Patch salva le modifiche di una sezione, di un file o di una selezione in un file .patch. Include anche i file non versionati, come file nuovi. Binari e file vuoti non entrano nella patch e vengono segnalati.
  - Apply Patch mostra un'anteprima con i file toccati, i rifiuti e i conflitti, e applica la patch dopo la conferma.
  - Le patch usano percorsi relativi e si applicano anche su un'altra working copy.
- **Shelve... / Unshelve.** Mette da parte le modifiche, come git stash.
  - Chiede un nome e l'opzione **Tieni le modifiche nella working copy**.
  - La sezione **Shelved (n)** in Changes elenca i salvataggi. Il menu ha Unshelve, Applica e tieni, Salva come .patch ed Elimina.
  - Binari e file vuoti restano dove sono; un avviso compare prima.
- **Backup prima dei revert di massa.** Prima di Revert di tutto e di Annulla merge, le modifiche locali vengono salvate in una cartella nascosta della working copy, che non compare tra i non versionati.
  - Dopo l'operazione il pulsante **Ripristina modifiche** le riapplica.
  - Il backup viene eliminato dopo il ripristino o alla prossima operazione. I backup oltre 7 giorni o 200 MB vengono eliminati all'avvio.
  - Se manca spazio su disco, SVNAssist chiede se procedere senza backup.
- **Controllo del server al commit.** Prima del commit SVNAssist verifica se sul ramo ci sono revisioni di altri utenti. In quel caso avvisa che la working copy non le contiene e propone SVN Update. Se il commit avviene comunque, dopo il commit compaiono un avviso e il pulsante **SVN Update ora**; l'indicatore della revisione resta arancione.
- **Avviso sui marcatori di conflitto.** Al commit, se un file contiene marcatori di conflitto (anche rientrati), compare un avviso con la richiesta di conferma.
- **Lock di altri utenti.** In Changes un lucchetto con "Bloccato da <utente>" indica i file bloccati da altri; il tooltip riporta data e commento. Si aggiorna con il controllo periodico delle nuove revisioni.
- **Usa la mia per tutti / Usa la loro per tutti.** Risolvono insieme i conflitti di testo, con conferma e elenco dei file. Conflitti ad albero e di proprietà sono esclusi.
- **Usa entrambe.** Nella risoluzione di un conflitto di testo, unisce le due versioni: la mia seguita dalla loro, oppure la loro seguita dalla mia. Disponibile anche per tutti i conflitti.
- **Segna come risolto sul file aperto.** Quando in Visual Studio è aperto un file in conflitto, una barra sopra il documento offre Segna come risolto, Usa la mia, Usa la loro, Usa entrambe e Apri editor di merge (da verificare).
- **Menu del tasto destro.** Nel riquadro Conflitti di Changes e nel Log il tasto destro sulle righe selezionate apre un menu di azioni (anche con Maiusc+F10 o il tasto Menu). Nei conflitti include Apri file e Copia percorso. Nel menu di riga di Changes c'è anche l'apertura del file in Esplora file.
- **Modelli AI dal provider.** La scelta del modello mostra l'elenco restituito dal provider, senza voci "consigliato".
  - **Aggiorna elenco** rilegge l'elenco; altrimenti viene riletto dopo 24 ore.
  - **Altro...** permette di scrivere il nome di un modello non in elenco.
  - Con GitHub Copilot sono indicati il costo stimato per commit (moltiplicatore premium), il modello predefinito, il modello usato e l'account collegato.
  - Se il modello non risponde, l'errore riporta il motivo del provider e propone un'azione.
  - Non provato con i provider reali (da verificare).
- **Merge da una cartella.** SVN Merge... da una cartella di Solution Explorer lavora solo su quel sottoalbero. Un verdetto indica se la cartella è Compatibile con il merge o se il merge non è possibile.
- **File di ogni revisione.** Nella selezione delle revisioni da unire, sotto la tabella compare l'elenco dei file coinvolti (A, M, D). Un clic su un file apre il diff di quella revisione.
- **Anteprima del merge per revisione.** L'anteprima è raggruppata per revisione, con i file di ciascuna. I file con un conflitto previsto sono indicati con una C rossa e dal numero di possibili conflitti.
- **Show Log: percorsi modificati.** Per la revisione scelta, il Log elenca i percorsi modificati con i conteggi per azione. L'opzione **Solo percorsi interessati** nasconde i percorsi fuori dal file o dalla cartella da cui è stato aperto il Log.
- **Merge con modifiche pendenti.** Il merge non è più bloccato da modifiche non committate. Compare un avviso con il numero di elementi modificati e i file in comune con il merge. Prima del merge le modifiche vengono copiate in una cartella di sicurezza. Annulla merge ripristina le modifiche pendenti oltre ad annullare il merge. Dopo il commit del merge la copia viene eliminata (da verificare).
- **Merge applicato una revisione alla volta.** Il merge procede con l'avanzamento "revisione N di TOT (rNNNNN)". Se una revisione va in conflitto, il merge si ferma.
  - Il messaggio indica cosa è stato applicato, cosa manca e cosa fare: risolvere i conflitti, segnarli come risolti, scegliere Continua merge. Non serve rifare il merge.
  - **Continua merge** applica solo le revisioni rimaste.
  - Lo stato è salvato su disco: il merge si può riprendere dopo il riavvio di Visual Studio.
  - Il banner "Merge interrotto" in Changes e nel Log mostra applicate, rimaste e passi da fare, con i pulsanti Continua merge, Annulla merge e Scarta stato (da verificare in Visual Studio).
- **Avanzamento con percentuale.** Switch, Update e Update to revision mostrano una barra con percentuale e "N di circa M elementi" quando il totale si ricava (massimo 10 secondi di attesa). Altrimenti resta la barra indeterminata.
- **Update to revision con finestra di avanzamento** e pulsante Annulla.
- **Stato più completo.** Changes riconosce conflitti di proprietà, elementi ostruiti, elementi incompleti e tree conflict.
- **Limiti dell'AI per provider.** La lunghezza del diff inviato e quella della risposta dipendono dal provider, invece di un unico limite applicato a tutti.

### Modifiche

- **Update to revision.** L'elenco mostra solo le revisioni del ramo corrente.
- **Segna come risolto.** Se il file è uguale alla versione di base (per esempio perché la scelta è stata tolta nell'editor di merge), SVNAssist avvisa prima di procedere.
- **Merge.** Le revisioni vengono lette senza l'elenco dei file, in parallelo e memorizzate. I tempi di ogni fase vengono scritti nel log di SVNAssist. L'efficacia sul tempo di merge non è stata misurata (da verificare).

### Correzioni

- **Recenti.** Prima: cliccando una voce in Recent il testo non veniva selezionato né scritto; compariva dopo aver premuto un altro pulsante. Ora: il testo scelto compare subito nel campo.
- **Avanzamento del merge.** Prima: compariva due volte, nella finestra Changes e nella scheda Log. Ora: la finestra completa si vede solo nella vista da cui parte l'operazione; nell'altra compare una striscia che non blocca.
- **Avanzamento da fermo.** Prima: la finestra di avanzamento mostrava "0 elementi" prima dell'inizio. Ora: mostra un testo che indica l'attesa.
- **Merge fermato da un conflitto.** Prima: l'errore compariva più volte e Changes non si aggiornava. Ora: Changes mostra la sezione Conflitti e compare un solo messaggio.
- **Update to revision.** Prima: scegliendo una revisione precedente alla creazione del ramo si otteneva un errore. Ora: l'elenco contiene solo le revisioni del ramo.

## 0.4.0 (2026-11-15)

Contenuti: Solution Explorer (icone di stato, SVN Add, Show Log), opzioni di Show Log, selezione multipla in Changes, Cleanup con opzioni, finestra di avanzamento con Annulla, recupero dello switch interrotto. Include le modifiche della 0.3.2, che non è stata rilasciata a parte.

Installazione: come la 0.2.0.

### Novità

- **Selezione multipla in Changes.** Ctrl+clic aggiunge o toglie un elemento, Maiusc+clic seleziona un intervallo (anche tra sezioni). Da tastiera: Maiusc+frecce, Ctrl+Spazio, Ctrl+A. Il tasto destro mostra le azioni valide per tutta la selezione, con il conteggio. Spazio e Canc valgono per tutti gli elementi selezionati.
- **Canc sulle cartelle.** Una cartella versionata viene eliminata con svn delete (resta da committare); una non versionata va nel cestino. La conferma elenca gli elementi.
- **AI: Più corto e Più dettagliato.** Il menu del messaggio di commit ha queste due voci oltre a Genera.
- **Show Log: Stop on copy/rename.** L'opzione è spenta di default: il Log mostra la storia completa oltre le copie e le rinomine.
- **Show Log: Include merged revisions.** Le revisioni entrate per merge sono annidate sotto il merge; un merge inverso è segnalato.
- **Show Log: diff dei file di una revisione.** Un clic su un file di una revisione apre il diff di quella revisione, anche per le revisioni unite. Le opzioni si cambiano mentre il Log è aperto e il Log si ricarica.
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
- Il doppio clic su un file aggiunto o non versionato apre il file intero.
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
- **Log come documento.** Show Log si apre come documento nell'area editor di Visual Studio, con una tabella delle revisioni. Filtri per autore, testo, percorso e date. Per ogni revisione sono elencati i file cambiati. Ctrl+clic su due revisioni le confronta. Il comando è disponibile anche per file e cartelle da Esplora soluzioni.
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
- **Operazione già in corso.** Prima un secondo clic non faceva nulla. Ora mostra "Operazione in corso…".
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
2. Aprire con doppio clic il pacchetto .vsix di SVNAssist.
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
