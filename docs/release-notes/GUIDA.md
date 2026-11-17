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

- Posizione: `docs/release-notes/img/<versione>/<lang>/NN-nome.png`, con `<lang>` = `it` o `en` (nomi senza spazi, numerazione progressiva uguale nelle due lingue). Le note italiane puntano a `.../img/<versione>/it/...`, quelle inglesi a `.../img/<versione>/en/...`.
- Generazione: `node tools/screenshots-release.mjs --version X.Y.Z` produce tutte le immagini della versione in italiano e in inglese. Opzioni: `--lang it|en` (una lingua sola), `--scenes a,b`, `--list`, `--demo <file>`, `--out`, `--scale` (default 2). La lingua si imposta con il selettore di lingua della demo (stesso campo `lang` che manda l'host) e lo script si ferma con un errore se i testi della UI non risultano nella lingua richiesta. Se esiste il tag `vX.Y.Z` lo script costruisce la demo da quel tag, cosi' le immagini mostrano solo cio' che esisteva in quella versione; senza tag usa `docs/demo/svnassist-demo.html`. Per una nuova release: creare `tools/release-scenes/<versione>.mjs` (vedi sotto); le scene della 0.2.0 restano in `SCENES` / `SETS` dello script.
- Due tipi di immagine:
  - **UI vera** (scene `kind: 'ui'`, di solito 01-06): catturate dalla demo, che usa la stessa UI web dell'estensione; si ritaglia solo il pannello o il documento, senza la cornice finta di Visual Studio. Didascalia neutra.
  - **Illustrazioni** (scene `kind: 'illus'`): per ciò che la demo non mostra (Log come scheda documento, diff nativo, menu di Esplora soluzioni, popup di login SVN, menu di Visual Studio). Sono ricostruzioni, NON screenshot di Visual Studio, disegnate in `docs/demo/src/illustrations.html` (pagina usata solo da questo script, non fa parte della demo pubblica; parametri `?scene=<id>&lang=it|en`). Hanno tema scuro, la parte rilevante nitida e il resto della finestra sfocato e scurito. I testi delle voci di menu, dei titoli e dei dialoghi vanno copiati dalle fonti reali (`SVNAssist.vsct`, `Loc.cs`/`LocEn.cs`, `PromptDialog.cs`, `DiffOpener.cs`, `i18n.js`), mai inventati; l'unico contorno di esempio ammesso e' quello che non e' di SVNAssist (altre voci di menu, nomi di file). Il Log della scena 07 e' la UI vera catturata dalla demo e incorporata nella cornice. Per una nuova illustrazione: aggiungere la scena in `illustrations.html` (funzione in `SCENES`) e la voce in `screenshots-release.mjs` (`kind: 'illus'`, `run: ({ illus }) => illus('<scena>')`).
- Scene per versione: ogni versione con immagini ha un modulo `tools/release-scenes/<versione>.mjs` che esporta `scenes` (stessa forma delle voci di `SCENES`: `id`, `file`, `kind`, `desc`, `run`) e `set` (gli id nell'ordine dei file). Lo script lo carica da solo con `--version`; un id del modulo ha la precedenza su uno di `SCENES`. Le funzioni comuni (ritaglio di un dialogo, comprimere i Conflitti, risolverli, ricerca nel selettore, pannello piu' largo) stanno in `tools/release-scenes/_common.mjs`. Campi facoltativi di una scena: `langs: ['it']` (scena solo italiana, saltata in `en`, quando la demo del tag non traduce quei testi: niente traduzioni inventate nelle immagini) e `needsDemo: true` (illustrazione che incorpora un'immagine della demo). Le immagini si fanno solo per le versioni principali (non per le minori) e solo su cio' che la demo di quel tag o un'illustrazione fedele puo' mostrare. Una nuova illustrazione va aggiunta in `docs/demo/src/illustrations.html` con un id proprio (le scene delle versioni precedenti non si modificano, cosi' le loro immagini restano identiche), con i testi presi dalle stringhe del tag (`SVNAssist.vsct`, `LocEn.cs`, `i18n.js`). Per ogni scena: guardare il PNG in `it` e in `en`, controllare che nessun testo sia nella lingua sbagliata (la cornice della demo e alcuni messaggi del tag restano in italiano) e che non ci siano parti tagliate; per cambiare un solo file usare `--scenes id`.
- Regola "Illustrazione:": ogni immagine che non e' la UI vera ha sotto una didascalia in corsivo che inizia con `Illustrazione:` (in inglese `Illustration:`) e dice che e' una ricostruzione, non uno screenshot di Visual Studio. Le immagini della UI vera (scene `ui`) hanno una didascalia neutra, senza quel prefisso.
- Non usare nelle note funzioni che la demo o le illustrazioni mostrano ma che non appartengono alla versione (es. nelle illustrazioni di una versione compaiono solo le voci di menu che esistevano in quel tag: si controlla `SVNAssist.vsct` del tag).
- Se un'immagine non puo' essere prodotta ne' dalla demo ne' da un'illustrazione, resta il segnaposto:
  `> [IMMAGINE DA FORNIRE: <cosa deve mostrare>] (file: docs/release-notes/img/<versione>/<lang>/NN-nome.png)`
  Una vera immagine di Visual Studio fornita dall'utente va in `docs/release-notes/img/<versione>/<lang>/` con il nome indicato e sostituisce il segnaposto (e la didascalia "Illustrazione:" si toglie).
- Limiti noti: i dati di esempio della demo (messaggi di commit, nomi di file) sono in italiano in entrambe le lingue; i campi data del Log mostrano `mm/dd/yyyy` perche' dipendono dal browser di Chromium; i menu di Visual Studio (voci di `SVNAssist.vsct`) sono in inglese in tutte e due le lingue, quindi 08, 09 e 11 sono uguali (11 cambia solo la voce con il segno di spunta).
- Sintassi: `<img src="https://raw.githubusercontent.com/PignaBoyz/SVNAssist/main/docs/release-notes/img/<versione>/<lang>/NN-nome.png" width="720" alt="...">` (il file e' raggiungibile solo dopo il rilascio su main). Sotto ogni immagine una didascalia in corsivo, neutra: dice cosa mostra, senza aggettivi.
- Dimensione: ogni PNG sotto i 400 KB (scala 2, larghezza finale ~1000-2000 px); se serve, ridurre l'area catturata.

## 7. Lingua

Prima l'italiano, validato dall'utente. Poi la traduzione inglese in `docs/release-notes/vX.Y.Z.user.en.md`, con stessa struttura e le stesse immagini nella versione `en`.

## 8. Processo

1. Note scritte nel repo (`docs/release-notes/vX.Y.Z.user.md`, immagini e segnaposto compresi).
2. Approvazione dell'utente.
3. Aggiornamento di `CHANGELOG.md`.
4. Aggiornamento del testo della Release su GitHub dalla sessione locale, con token. Il tag non si cancella mai.

## Pubblicazione

- Sorgenti per ogni versione: `docs/release-notes/<tag>.user.md` (italiano) e `docs/release-notes/<tag>.user.en.md` (inglese).
- `tools/build-release-body.mjs <tag> <sha> [--out file]` costruisce il testo della Release: due blocchi `<details>`, Italiano (aperto) e English (chiuso). GitHub non esegue JS, quindi il "toggle" di lingua è questo. Dopo `<summary>` e prima di `</details>` ci vuole una riga vuota, altrimenti il markdown interno non viene reso. Se manca il file inglese, il testo contiene solo l'italiano (avviso su stderr); se manca l'italiano il comando fallisce.
- Il titolo `# SVNAssist X.Y.Z` non entra nel testo (lo mostra già il titolo della Release).
- Immagini: gli URL `raw.githubusercontent.com/PignaBoyz/SVNAssist/main/` sono riscritti con lo SHA del commit del workflow (su main i file possono non esistere ancora).
- Segnaposto `> [IMMAGINE DA FORNIRE ...` e `> [IMAGE TO BE PROVIDED ...` diventano commenti HTML, quindi invisibili, riga per riga.
- Aggiornare Release già pubblicate: scrivere i tag in `docs/release-notes/.publish` (uno per riga; righe vuote e `#` ignorate) e fare push. Il workflow `Release notes` modifica solo il testo (`gh release edit --notes-file`), non titolo, asset o tag. Le Release inesistenti sono saltate, non create. Alternativa: avvio manuale con l'input `tags`. Mai `v0.1.0`.
- Nuove Release: `release.yml` usa lo stesso builder se esiste `<tag>.user.md`, altrimenti `<tag>.md`.
- Il tag non si cancella mai.

## 9. Checklist "prima di pubblicare"

- [ ] Nessun aggettivo di valutazione o di marketing, nessuno slogan nel titolo.
- [ ] Nessun nome interno (file, classi, flag svn, JSON, RPC, WebView2, backend, SDK) fuori dai dettagli tecnici.
- [ ] Correzioni nella forma "Prima: X. Ora: Y.".
- [ ] Requisiti verificati; il resto marcato "(da verificare)".
- [ ] Immagini presenti in `it` e `en` oppure segnaposto con nome file e descrizione.
- [ ] Didascalie neutre sotto ogni immagine della UI vera; "Illustrazione:" / "Illustration:" sotto le ricostruzioni.
- [ ] Link raw con il nome utente/repo e il percorso `img/<versione>/<lang>/` corretti (it nelle note italiane, en nelle inglesi).
- [ ] Ogni PNG sotto i 400 KB, guardato a occhio (testi non tagliati ne' sovrapposti).
- [ ] Versione inglese pronta, CHANGELOG.md aggiornato.
