# Checklist manuale 0.2.0

VSIX aggiornato già installato nell'istanza `SvnHost`. Avvio (da PowerShell):

```
& "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe" /rootsuffix SvnHost C:\Users\andre\AppData\Local\Temp\svnwc\wc\Demo.sln
```
Poi Extensions > SVNAssist > Changes. Log host: `%LOCALAPPDATA%\SVNAssist\host-log.txt`.

Segna ✅ / ❌ (+ una riga su cosa vedi).

**Tema**
1. [ ] Tema chiaro, scuro, blu: la finestra Changes e Log seguono i colori di VS.
2. [ ] Cambio tema a caldo (Tools > Theme) senza riaprire la finestra.
3. [ ] Alto contrasto (Windows) leggibile.

**Tastiera nella finestra** (prova con finestra agganciata E flottante)
4. [ ] Frecce su/giù/sinistra/destra nell'albero; Spazio spunta; Invio apre il diff.
5. [ ] Canc su un file chiede conferma ed elimina.
6. [ ] Ctrl+A / Ctrl+C / Ctrl+V nel messaggio di commit; Tab passa tra i controlli.
7. [ ] Filtro "Filtra i file…": filtra, Esc svuota.

**Funzioni**
8. [ ] Pulsante "Commit (n)" mostra il numero giusto; il commit va a buon fine.
9. [ ] Diff di un file si apre nell'editor di VS.
10. [ ] Esplora soluzioni > tasto destro > SVN: Update, Diff, Log funzionano; Revert chiede conferma.
11. [ ] Genera con AI (GitHub Models via `gh`): messaggio generato, anche con molti file.
12. [ ] Genera con AI con Claude (se hai Claude Code / chiave).
13. [ ] Tools > Options > SVNAssist: le impostazioni si salvano e valgono al riavvio.

**Chiusura e runtime**
14. [ ] Chiudi VS: in Task Manager non resta nessun `SVNAssist.Backend.exe`.
15. [ ] (facoltativo) Su una macchina/VM senza .NET 10: compare il dialogo "Installa automaticamente / Apri pagina download" e dopo l'installazione la finestra funziona.
16. [ ] (facoltativo) VS 2022, se lo hai: installa `src\SVNAssist.Host\bin\Release\net472\SVNAssist.vsix` e apri Changes.

**Procedura guidata AI e feedback** (con un provider NON configurato: Tools > Options > Provider AI = "Automatico", niente `gh`/Claude/chiavi)
17. [ ] "Genera con AI" apre la procedura guidata (non un prompt); Esc la chiude, Tab resta dentro il dialogo; Avanti è disabilitato finché non scegli un provider.
18. [ ] Scegli un provider: al passo 2 compaiono solo i suoi prerequisiti; installa ciò che manca dal pulsante (si apre il terminale) e, tornando in VS, lo stato diventa verde da solo (o con Ricontrolla). Passo 3: login o API key; passo 4: modello consigliato preselezionato; Fine salva (verifica in Tools > Options) e genera il messaggio.
19. [ ] Extensions > SVNAssist > "Change AI Provider..." apre Changes con la procedura; "Faccio io" apre Tools > Options > SVNAssist; Annulla non cambia nessuna impostazione.
20. [ ] "Genera con AI" senza nulla da committare (nessuna modifica versionata): banner giallo ben visibile con icona (a ogni clic si ridisegna e lampeggia); durante la generazione il pulsante è disabilitato con spinner e "Generazione in corso…"; un errore del provider compare in rosso.
21. [ ] Pulsante in alto "SVN Update"; dopo l'update il messaggio in basso ("Aggiornato alla revisione N.") è verde con icona e un breve alone che sfuma.
22. [ ] Barra in basso su due righe: il messaggio di stato (es. "Commit completato: revisione 4") sta da solo sulla prima, a tutta larghezza (se è troppo lungo termina con "…" e il tooltip mostra il testo intero); "Prerequisiti" sta sulla riga sotto, a destra, verde se tutto ok; senza svn o senza provider AI è rosso con icona di avviso e, cliccandolo, apre la procedura sui prerequisiti mancanti (con "Configura l'AI" e "Faccio io").
23. [ ] Repo `file://` senza trunk/branches: l'header mostra "/ (radice)" invece di "unknown".

**Cleanup, Update to revision, Lock e login SVN** (repo `file://` o con server; per il login serve un server che chieda utente/password)
24. [ ] Changes > ⋯ > "SVN Cleanup": messaggio verde "Cleanup completato." Stesso comando da Extensions > SVNAssist > Cleanup e da Solution Explorer > SVN > SVN Cleanup.
25. [ ] Blocca la working copy (es. `svn cleanup` interrotto, o una cartella `.svn` con lock): Update dà errore rosso con il pulsante "Esegui Cleanup"; cliccandolo l'errore sparisce e Update funziona.
26. [ ] Changes > ⋯ > "SVN Update to revision…": si apre una modale con HEAD in cima e le ultime revisioni (numero, autore, data, prima riga); frecce/clic selezionano, "Carica altre" aggiunge revisioni, Esc/Annulla chiude senza fare nulla. Scrivi `abc` o `0` in "oppure numero" e OK mostra l'errore nella modale; scegli una revisione precedente e OK: il messaggio dice "Aggiornato alla revisione N."; con HEAD torni all'ultima. Idem da Extensions > SVNAssist e da Solution Explorer (su una cartella/file: si apre Changes con la modale riferita a quel percorso).
26b. [ ] Commit come Git: all'apertura Staged è vuoto (le modifiche stanno in Modifiche) e il pulsante è "Commit All (n)"; metti un file in Staged (+): il pulsante diventa "Commit Staged (n)" e la scelta resta dopo un refresh. Con file in Staged il pulsante è "Commit Staged (n)" e committa solo quelli; togli tutto da Staged (menu sulla sezione) e diventa "Commit All (n)": committa tutte le modifiche dei file versionati (anche i mancanti `!` come delete), non i non versionati; con un conflitto rifiuta; senza modifiche è disabilitato. "Genera con AI" usa lo stesso insieme.
26c. [ ] In alto, accanto a "SVN Update", "Show Log" apre il Log della working copy.
27. [ ] Su un file modificato: tasto destro > "SVN Lock…" (messaggio facoltativo) > compare il lucchetto accanto al nome; `svn status` da terminale mostra la K. "SVN Unlock" lo toglie. Da Solution Explorer > SVN > Lock/Unlock su un file non modificato: il file compare in Modifiche con la lettera K.
28. [ ] File bloccato da un altro utente (blocca da un secondo checkout): "SVN Lock…" chiede una conferma esplicita ("già bloccato da…, rubare il lock?"); Annulla non cambia nulla, OK ruba il lock.
29. [ ] Login SVN: con credenziali non salvate su un server che le richiede, Update mostra il dialogo utente/password (password mascherata, "Ricorda" attivo); inserite le credenziali il comando riesce una volta sola; con credenziali sbagliate compare l'errore normale (nessun secondo dialogo); Annulla = errore normale. Con "Ricorda" i comandi successivi non chiedono più nulla. Nei log (`%LOCALAPPDATA%\SVNAssist`) non compare la password.
30. [ ] Con l'accesso integrato di Windows (il tuo caso abituale) il dialogo di login non compare mai.

**Log e diff**
31. [ ] Extensions > Log: il Log si apre come **documento nell'area editor** (tab "Log: <cartella>", non più la finestrella laterale), con tabella Rev. | Autore | Data | Messaggio (righe alternate, intestazioni fisse, colonne ridimensionabili trascinando il bordo dell'intestazione, frecce/Home/End/Invio) e sotto il messaggio completo e i file con azione A/M/D/R colorata; con la finestra larga il dettaglio passa a destra. I campi Autore / Messaggio / Percorso / Dal / al filtrano le revisioni (Filtra); Azzera le ripristina; "Carica altre 50" (in fondo alla tabella) con filtro attivo carica altre revisioni che lo rispettano. Prova almeno un filtro per testo e uno per percorso con la tua versione di svn (`--search` richiede svn >= 1.8: con una più vecchia il caricamento dà errore).
32. [ ] Clic su una revisione e poi su un file dell'elenco: si apre nell'editor di VS il diff a due pannelli nativo, titolo "Program.cs r12 ↔ r13"; un file aggiunto ha il pannello sinistro vuoto, uno eliminato il destro; una cartella nell'elenco non è cliccabile.
33. [ ] Ctrl+clic su un'altra revisione: compare "File cambiati tra rA e rB" con l'elenco; clic su un file = diff nativo rA ↔ rB; Chiudi (o clic semplice su una revisione) torna al dettaglio.
34. [ ] Esplora soluzioni > cartella (o file) > SVN > Log: stesso documento, titolo "Log: <nome>" aggiornato al nuovo percorso (anche da pulsante Log di Changes). Cartella > SVN > Diff: il documento Log mostra "Modifiche in <cartella>" con i file modificati; clic su un file = diff nativo BASE ↔ working copy; "Apri come patch" apre un unico .diff.
35. [ ] Diff di un file (Changes o Esplora soluzioni) = diff nativo di VS anche con TortoiseSVN disinstallato; dopo aver chiuso VS la cartella %TEMP%\SVNAssist\diff non contiene più le sottocartelle di questa sessione.

**Lingua dell'interfaccia (Italiano / English)** (tieni aperte Changes e Log, con la procedura guidata AI a portata di mano)
36. [ ] Extensions > SVNAssist > "Language / Lingua" è un sottomenu con tre voci ("Automatica / Automatic (lingua di Visual Studio)", "Italiano", "English") a scelta esclusiva: la spunta è su una sola, in modo coerente con Tools > Options > SVNAssist > Generale > "Lingua dell'interfaccia" (default Automatica).
37. [ ] Con Visual Studio in italiano e "Automatica": finestre e messaggi in italiano. Scegli "English": **subito** (senza riavviare VS) Changes e Log passano all'inglese — pulsanti, tooltip, menu ⋯ e ▾, sezioni (Changes / Unversioned), nomi degli stati (Modified, Added...), barra di stato, "Prerequisites", il messaggio iniziale del Log; riscegli "Italiano" e tutto torna in italiano.
38. [ ] In inglese: apri la procedura guidata (Extensions > SVNAssist > Change AI Provider...) e i prerequisiti: titoli, istruzioni, dettagli e pulsanti (Install with winget, Sign in to GitHub...) sono in inglese; cambiando lingua con la procedura aperta si riscrivono al volo (testo digitato e passo corrente restano).
39. [ ] In inglese: provoca un errore (Update su una cartella non working copy, commit senza messaggio, "Genera con AI" senza modifiche): i messaggi sono in inglese e hanno gli stessi colori/icone di prima (errore rosso, avviso giallo, esito verde); il messaggio di svn.exe resta com'è. Un messaggio già scritto resta nella lingua in cui è nato fino al successivo.
40. [ ] In inglese: i dialoghi nativi (conferma Revert/Delete, scelta risoluzione conflitto, "Update to revision", Lock, login SVN con "Remember"/"User") sono in inglese, con "Cancel" al posto di "Annulla".
41. [ ] Tools > Options > SVNAssist: nomi e descrizioni delle impostazioni e le voci delle tendine sono nella lingua scelta (le categorie "Commit message AI"/"Generale" si aggiornano al riavvio di VS); cambia "Lingua dell'interfaccia" da qui e dopo OK le finestre cambiano al volo.
42. [ ] La lingua dei commit message è indipendente: con interfaccia inglese e "Lingua dei commit message" = Italiano, il messaggio generato è in italiano (e viceversa); non cambia con il menu Lingua.
43. [ ] Con Visual Studio in inglese (o un'altra lingua) e "Automatica": l'interfaccia è in inglese; con VS in italiano è in italiano.
44. [ ] Dopo aver chiuso e riaperto VS la scelta è mantenuta.
