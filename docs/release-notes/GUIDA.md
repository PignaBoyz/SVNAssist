# Guida alle note di rilascio

Procedura per scrivere le note di una nuova versione (X.Y.Z). Modello di riferimento: `docs/release-notes/v0.2.0.user.md`.

## 1. Stile

- Neutro e descrittivo, da documentazione. Vietati gli aggettivi di valutazione e di marketing (veloce, potente, migliorato, semplice, finalmente...) e gli slogan nel titolo: il titolo è solo `# SVNAssist X.Y.Z`.
- Ogni voce dice cosa fa o cosa cambia, con dettagli concreti (nomi di pulsanti, numeri, condizioni).
- Correzioni nella forma "Prima: X. Ora: Y.", senza la causa interna.
- Frasi brevi, una funzione per voce.

## 2. Vocabolario

- Vietati nelle note per l'utente: nomi di file, classi, flag di svn, JSON, RPC, WebView2, backend, SDK. Se servono, vanno solo nel blocco "Dettagli tecnici".
- I termini svn noti vanno bene (working copy, revisione, update, cleanup, lock, commit, diff).
- Nomi di pulsanti, menu e finestre in inglese, come nell'interfaccia (Commit All, Show Log, Change AI Provider...).

## 3. Struttura fissa

```
# SVNAssist X.Y.Z
## Novità
## Modifiche
## Correzioni
## Requisiti e installazione
<details><summary>Dettagli tecnici</summary> ... </details>
```

Le voci lunghe si dividono in elenchi annidati. Il blocco "Dettagli tecnici" può contenere i nomi interni e le note di aggiornamento complete.

## 4. Requisiti

- Si scrive solo ciò che è stato verificato (versione di Visual Studio provata, client svn, runtime richiesto).
- Ciò che è dichiarato ma non provato porta "(da verificare)".
- Si indica se la versione sostituisce la precedente e come installarla.

## 5. Come ricavare le voci

1. `HANDOFF.md`: decisioni e stato della release.
2. `git log vA.B.C..vX.Y.Z --oneline` tra i tag (o fino a HEAD se il tag non c'è ancora).
3. `docs/FUNZIONALITA.md`: descrizione delle funzioni; usare solo ciò che appartiene alla versione.
4. Checklist `docs/verifica/X.Y.Z/`: cosa è stato provato e con quale esito.

Ogni voce deve corrispondere a un cambiamento visibile all'utente. Le voci puramente interne vanno solo nei dettagli tecnici.

## 6. Immagini

- Posizione: `docs/release-notes/img/<versione>/NN-nome.png` (nomi senza spazi, numerazione progressiva).
- Generazione dalla demo: `node tools/screenshots-release.mjs --version X.Y.Z` (opzioni: `--scenes a,b`, `--list`, `--demo <file>`, `--out`, `--scale`, `--locale`). Se esiste il tag `vX.Y.Z` lo script costruisce la demo da quel tag, così le immagini mostrano solo ciò che esisteva in quella versione; senza tag usa `docs/demo/svnassist-demo.html`. Per una nuova release: aggiungere le scene in `SCENES` e l'elenco in `SETS`.
- Non usare nelle note funzioni che la demo mostra ma che non appartengono alla versione.
- Segnaposto per ciò che la demo non può mostrare (diff nativo di Visual Studio, menu di Esplora soluzioni, icone di stato in Esplora soluzioni, popup di login SVN, menu di Visual Studio):
  `> [IMMAGINE DA FORNIRE: <cosa deve mostrare>] (file: docs/release-notes/img/<versione>/NN-nome.png)`
- Le immagini dell'interfaccia vera fornite dall'utente vanno in `docs/release-notes/img/<versione>/` con il nome indicato nel segnaposto; poi si sostituisce il segnaposto con l'immagine.
- Sintassi: `<img src="https://raw.githubusercontent.com/PignaBoyz/SVNAssist/main/docs/release-notes/img/<versione>/NN-nome.png" width="720" alt="...">` (il file è raggiungibile solo dopo il rilascio su main). Sotto ogni immagine una didascalia in corsivo, neutra: dice cosa mostra, senza aggettivi.

## 7. Lingua

Prima l'italiano, validato dall'utente. Poi la traduzione inglese in `docs/release-notes/vX.Y.Z.en.md`, con stessa struttura e stesse immagini.

## 8. Processo

1. Note scritte nel repo (`docs/release-notes/vX.Y.Z.user.md`, immagini e segnaposto compresi).
2. Approvazione dell'utente.
3. Aggiornamento di `CHANGELOG.md`.
4. Aggiornamento del testo della Release su GitHub dalla sessione locale, con token. Il tag non si cancella mai.

## 9. Checklist "prima di pubblicare"

- [ ] Nessun aggettivo di valutazione o di marketing, nessuno slogan nel titolo.
- [ ] Nessun nome interno (file, classi, flag svn, JSON, RPC, WebView2, backend, SDK) fuori dai dettagli tecnici.
- [ ] Correzioni nella forma "Prima: X. Ora: Y.".
- [ ] Requisiti verificati; il resto marcato "(da verificare)".
- [ ] Immagini presenti oppure segnaposto con nome file e descrizione.
- [ ] Didascalie neutre sotto ogni immagine.
- [ ] Link raw con il nome utente/repo e il percorso `img/<versione>/` corretti.
- [ ] Versione inglese pronta, CHANGELOG.md aggiornato.
