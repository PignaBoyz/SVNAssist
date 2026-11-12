# Checklist manuale 0.3.0 — Switch e Merge

VSIX 0.3.0 già installato nell'istanza `SvnHost`. Repo di prova con trunk/branches/tags (`%TEMP%\svnmerge`):
- `trunk` = working copy `%TEMP%\svnmerge\wc` (r7)
- `branches/feature-demo` con 3 revisioni da unire: r4 (Program.cs), r5 (aggiunge Nuovo.cs), r6 (README.txt, **va in conflitto** con r7 del trunk)

Avvio (da PowerShell):

```
& "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe" /rootsuffix SvnHost C:\Users\andre\AppData\Local\Temp\svnmerge\wc\DemoApp.sln
```
Poi Extensions > SVNAssist > Changes. Log host: `%LOCALAPPDATA%\SVNAssist\host-log.txt`.
Per ricominciare da zero: `.\tools\setup-local-svn.ps1 -Root $env:TEMP\svnmerge -Force` (poi vanno ricreate r4-r7: chiedimelo).

Segna ✅ / ❌ (+ una riga su cosa vedi).

**Ramo corrente e Switch**
1. [ ] In Changes si vede il ramo corrente `trunk`.
2. [ ] "Switch..." (in Changes e in Extensions > SVNAssist) apre l'elenco: `trunk`, `branches/feature-demo`, nessun tag; la ricerca filtra; "Aggiorna" ricarica.
3. [ ] Con un file modificato, lo Switch chiede conferma; Annulla lascia tutto com'è.
4. [ ] Switch su `feature-demo` (WC pulita): il ramo diventa `branches/feature-demo`, compaiono `Nuovo.cs` e la riga "BRANCH" nel README; VS può chiedere di ricaricare la solution.
5. [ ] Switch di nuovo su `trunk`: tutto torna come prima.
6. [ ] Tools > Options > SVNAssist > Generale: c'è il layout repository (`trunk/branches/tags`).

**Merge** (su `trunk`, WC pulita)
7. [ ] Extensions > SVNAssist > "SVN Merge..." apre il selettore senza il ramo corrente; scegli `feature-demo`.
8. [ ] Si apre il Log come documento "Merge da branches/feature-demo" con r4, r5, r6 (autore, data, messaggio).
9. [ ] "Anteprima": elenco file (Program.cs U, Nuovo.cs A, README.txt C) con conteggi; la WC NON cambia (Changes resta vuoto).
10. [ ] Con un file modificato in WC, Merge è bloccato con spiegazione (fai prima Commit o Revert).
11. [ ] Seleziona solo r4 e r5 → "Merge selezionate (2)": Changes mostra Program.cs e Nuovo.cs; messaggio di commit precompilato "Merge r4, r5 da ^/branches/feature-demo" (o simile); niente in Staged.
12. [ ] Banner "Annulla merge": chiede conferma con l'elenco dei file, poi la WC torna pulita.
13. [ ] "Merge tutto": README.txt compare nella sezione Conflitti, il commit è bloccato finché non risolvi.
14. [ ] Risolvi il conflitto (con gli strumenti esistenti), poi "Commit All" va a buon fine; riaprendo SVN Merge da `feature-demo` non c'è più nulla da unire.

**Rischi rimasti dalla 0.2.0** (se ti capita, non serve cercarli)
15. [ ] Log come documento MDI: si apre, si chiude, si riapre senza errori.
16. [ ] Chiudi VS: in Task Manager non resta nessun `SVNAssist.Backend.exe`.
17. [ ] (in azienda) Switch/Merge su un repo vero con accesso integrato di Windows: nessun login chiesto se le credenziali vanno.

**Correzioni dal primo collaudo: conflitti e repository grandi** (VSIX da reinstallare in `SvnHost`)
18. [ ] "Merge tutto" da `feature-demo`: a merge finito compare il dialogo "1 file in conflitto. Vuoi risolverli ora?" con **Risolvi ora** / **Più tardi**. "Più tardi" lascia tutto com'è; "Risolvi ora" porta in primo piano SVN Changes.
19. [ ] In Changes, sopra l'elenco, c'è il riquadro **Conflitti (1)** con `README.txt`, il tipo "Conflitto di testo" e i pulsanti **Apri editor di merge**, **Usa la mia**, **Usa la loro**, **Segna come risolto**. Sotto: "File di appoggio di svn": `README.txt.merge-left.r3`, `.merge-right.r6`, `.working`. Quei file **non** sono nei Non versionati.
20. [ ] Il banner del merge dice "1 conflitto: risolvilo qui sotto, poi committa."
21. [ ] **Apri editor di merge** apre l'editor a 3 vie di Visual Studio (vsDiffMerge) con base, mia, loro e risultato. Dopo aver salvato, **Segna come risolto** toglie il conflitto, il banner passa a "senza conflitti" e i file di appoggio spariscono dal disco. Con i marcatori ancora nel file, Segna come risolto rifiuta e lo dice.
22. [ ] Rifai il merge (Annulla merge, poi di nuovo) e prova **Usa la mia** (tiene il file del trunk) e **Usa la loro** (prende quello del branch): entrambi chiedono conferma.
23. [ ] Nel Log "Merge da branches/feature-demo", dopo il merge l'intestazione dice **"Merge eseguito, da committare"** (non "0 revisioni da unire") e il risultato con conflitti ha **Risolvi i conflitti**, che porta a Changes. Dopo il commit (o Annulla merge) torna "N revisioni da unire" / "Nessuna revisione da unire".
24. [ ] SVN Update che lascia un conflitto propone lo stesso "Risolvi ora / Più tardi".
25. [ ] Switch / Merge: scegli un ramo, richiudi e riapri: compare il gruppo **Usati di recente** con l'ultimo scelto in cima; digitando nel campo l'elenco si filtra subito.
26. [ ] **Mostra tutti / vista estesa…** (con un repository con molti rami): tabella Nome / Tipo / Rev. / Autore / Data, filtro per nome e per tipo, clic su Nome o Data per ordinare, scorrimento fluido. La stessa vista c'è in "SVN Merge...".

**Dal collaudo aziendale: barra di stato, revisione, messaggi recenti, file nuovi** (VSIX da reinstallare in `SvnHost`)
27. [ ] Dopo un commit (o un SVN Update) il messaggio verde "Commit completato..." sparisce da solo dopo circa 5 secondi; in basso resta solo la revisione (`r1234`). Un messaggio d'errore invece resta finché non lo sostituisce un altro.
28. [ ] L'indicatore `r1234` in basso a sinistra è **verde** (con la spunta) quando la working copy è all'ultima revisione del ramo; passando il mouse il tooltip lo conferma.
29. [ ] Da un altro PC (o con un secondo checkout) fai un commit sullo stesso ramo, poi attendi l'intervallo (o riapri la finestra / cambia l'impostazione): l'indicatore diventa **arancio**: `r1234 · disponibile r1240 — SVN Update`. Un clic lo esegue; dopo l'update torna verde. Un commit su un altro ramo non lo cambia.
30. [ ] Tools > Options > SVNAssist > Generale > "Controllo nuove revisioni (minuti)": 5 di default; con `0` l'indicatore diventa neutro (tooltip "Controllo ... spento") e non parte nessuna richiesta; cambiando il valore e premendo OK vale subito.
31. [ ] Senza rete o con le credenziali non in cache: nessun popup e nessun login; l'indicatore resta neutro (grigio) con il motivo nel tooltip e il messaggio di stato non diventa un errore.
32. [ ] Con la working copy pulita lo stato vuoto "Nessuna modifica / La working copy è pulita" compare **sotto** la sezione Staged, non sopra.
33. [ ] Vicino al messaggio di commit c'è la tendina **Recenti**: dopo un commit il messaggio compare in cima; un clic lo inserisce nel campo; i messaggi sono per repository (al massimo 25) e restano dopo aver riavviato VS (`%LOCALAPPDATA%\SVNAssist\recent-messages.json`).
34. [ ] Dopo "Merge tutto" o "Merge selezionate" il messaggio precompilato è nel formato TortoiseSVN: `Unite le revisioni 4-6 da branches/feature-demo:` (con la lingua inglese `Merged revision(s) 4-6 from ...`), poi per ogni revisione una riga `........` e `rN | autore | messaggio originale`.
35. [ ] Doppio clic (o Invio) su un file **aggiunto (A)** o **non versionato (?)** lo apre intero nell'editor di Visual Studio (nessun diff contro il vuoto); su un file **eliminato (D)** si apre il diff con il contenuto BASE a sinistra; su un modificato (M) il diff di sempre. Nel menu contestuale la voce è "Apri file" per A e ?.
