# Regole per i changelog

Regole fisse, valide da ogni nuova versione. Lo stile e il vocabolario delle voci sono in `GUIDA.md` (§1-§7): qui c'è cosa si scrive, dove, in che ordine e cosa si controlla.

## 1. Fonte unica

| File | Ruolo |
|------|-------|
| `docs/release-notes/vX.Y.Z.user.md` (it) e `vX.Y.Z.user.en.md` (en) | **Fonte.** Si scrivono a mano. La CI ne ricava il testo della Release GitHub (`tools/build-release-body.mjs`). |
| `CHANGELOG.md` / `CHANGELOG.en.md` | **Derivati.** Una sezione per versione, copia delle voci dei file `.user`. Mai modificare una voce solo qui. |
| `docs/release-notes/vX.Y.Z.md` | Testo tecnico originale; compare solo nei "Dettagli tecnici". |
| `docs/release-notes/archive/<tag>.github.md` | Testo della Release prima del cambio al formato IT/EN. Non si modifica. |

Si cambia una voce: prima nei file `.user` (it e en), poi la stessa riga nel CHANGELOG. `tests/ui-web/changelog-sync.test.mjs` fallisce se le voci (`- **Titolo.**`) di una versione differiscono tra CHANGELOG e `.user`.

## 2. Formato del CHANGELOG

- Versioni dalla più recente alla più vecchia, intestazione `## X.Y.Z (AAAA-MM-GG)`. Versione non ancora rilasciata: `## X.Y.Z (in preparazione)` / `(in preparation)`; la data si mette il giorno del tag.
- Sotto l'intestazione: `Contenuti:` (una riga, elenco delle funzioni principali), `Installazione:` (una riga). Poi `### Novità`, `### Modifiche`, `### Correzioni` (solo quelle non vuote), nell'ordine fisso.
- Una voce = un cambiamento visibile all'utente: `- **Titolo.** Cosa fa o cosa cambia.` Correzioni: `- **Titolo.** Prima: X. Ora: Y.` Sotto-punti come elenco annidato.
- "(da verificare)" in coda alla voce se non provata in Visual Studio. Si toglie solo dopo la prova, non prima.
- Versione non rilasciata a parte (es. 0.3.2): le sue voci passano alla versione che la include e la sezione dice "Include le modifiche della X.Y.Z, non rilasciata a parte". Nessuna sezione per una versione senza tag.
- Una versione minore (X.Y.Z con Z > 0) ha solo Modifiche/Correzioni e nessuna immagine.
- Nessuna voce interna: refactoring, test, strumenti e documenti stanno solo in "Dettagli tecnici".

## 3. Prima di ogni release

1. Elenco da `git log vA.B.C..HEAD --no-merges --format=%s`: ogni commit visibile all'utente ha la sua voce; ogni voce corrisponde ad almeno un commit.
2. Le voci vengono dalla checklist `docs/verifica/X.Y.Z/` solo per ciò che è stato provato; il resto "(da verificare)".
3. Togliere "Versione in preparazione, in collaudo." da `vX.Y.Z.user.md` e `.user.en.md` e "(in preparazione)" dal CHANGELOG, con la data.
4. Versione inglese uguale in struttura e numero di voci all'italiana; nomi dell'interfaccia identici a `Loc.cs` / `LocEn.cs` / `i18n.js` (non tradotti a memoria).
5. Immagini della versione (solo principali) fatte DOPO il collaudo, con `node tools/screenshots-release.mjs --version X.Y.Z`.
6. `node --test "tests/ui-web/*.test.mjs"` verde (include `release-body` e `changelog-sync`).

## 4. Pubblicazione

- Il codice e il CHANGELOG arrivano su `main` insieme (develop → main), poi tag `vX.Y.Z`; la CI costruisce la Release con VSIX, demo e sorgenti e usa i file `.user`.
- Correggere il testo di una Release già pubblicata: modificare i file `.user`, scrivere il tag in `docs/release-notes/.publish`, push su `main`. Si cambia solo il testo, mai titolo, asset o tag. Mai `v0.1.0`; i tag non si cancellano.
- Dopo ogni push di soli documenti su `main`, riunire `main` in `develop` con `git merge -s ours origin/main`.
- Dopo la pubblicazione: scaricare il testo (`GET /repos/PignaBoyz/SVNAssist/releases`) e confrontarlo con `node tools/build-release-body.mjs <tag> <sha>`: devono coincidere.

## 5. Ortografia

- Si scrive "click" (anche "Ctrl+click", "doppio click"), mai "clic". Vale per note italiane e CHANGELOG.md.
