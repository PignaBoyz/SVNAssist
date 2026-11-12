# Checklist 0.3.0 — collaudo sulla simulazione

## Preparazione (da PowerShell, nella cartella del repo SVNAssist)

```
.\tools\setup-sim.ps1 -Force
```
Crea `%TEMP%\svnsim` (circa 1 minuto):
- `wc` — trunk con **lavoro in corso** (Catalog.cs modificato in 4 punti, Order.cs modificato, Payment.cs aggiunto, Invoice.cs cancellato, Note.txt non versionato) e **una revisione indietro** (un collega ha committato dopo).
- `wc-merge` — trunk **pulita**, per Switch e Merge.
- Branch `feature-coupon` (r8 aggiunge Coupon.cs e modifica Order.cs; r9 README **in conflitto**; r10 cancella docs/old-setup.md → **tree conflict**), `hotfix-login` (1 revisione), 150 branch `feature-<area>-NNN`, 20 tag `v1.N.0`. Storico con 4 autori.

Avvio:
```
& "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe" /rootsuffix SvnHost $env:TEMP\svnsim\wc\Shop.sln
```
Extensions > SVNAssist > Changes. Log host: `%LOCALAPPDATA%\SVNAssist\host-log.txt`. Per ricominciare: chiudi VS e rilancia `setup-sim.ps1 -Force`.

Segna ✅ / ❌ (+ una riga su cosa vedi).

## A. Lavoro in corso (`wc\Shop.sln`)

**Finestra Changes e revisione**
1. [ ] Changes mostra il ramo `trunk`, i file M/A/D e `Note.txt` tra i non versionati. Le finestre di SVNAssist si aprono senza errori anche se erano aperte alla chiusura di VS.
2. [ ] In basso a sinistra l'indicatore della revisione è **arancio**: `rN · disponibile rN+1 — SVN Update` (il commit del collega). Tooltip chiaro.
3. [ ] Tools > Options > SVNAssist > Generale: "Controllo nuove revisioni (minuti)" = 5; con `0` l'indicatore diventa neutro. Rimetti 5.
4. [ ] Doppio clic su `Payment.cs` (A) e su `Note.txt` (?): si apre il **file intero** nell'editor. Su `Invoice.cs` (D): diff con il contenuto BASE a sinistra. Su `Order.cs` (M): diff.

**Diff, navigazione e annulla righe**
5. [ ] Doppio clic su `Catalog.cs`: diff nell'area documenti (non sopra la finestra Changes). **Alt+F8 / Shift+Alt+F8** (anche le voci "Next/Previous Change in Diff" nel menu contestuale e in Extensions > SVNAssist) spostano il cursore e centrano la modifica successiva/precedente nel pannello attivo, con giro tra le 4 modifiche. In un editor normale di un file modificato saltano al blocco successivo/precedente del `svn diff`. (Ctrl+Alt+Giù/Su non va più: è di VS per l'elenco dei documenti.)
6. [ ] Nel pannello destro, a sinistra di ogni blocco modificato c'è un pulsante con l'icona annulla (come Git): clic su quello delle 2 righe aggiunte (`Nuovo1`, `Nuovo2`) = spariscono solo quelle, **nessuna message box**, in Changes il messaggio "Annullate 2 righe"; le altre 3 modifiche restano e i pulsanti si riallineano. In alternativa: seleziona le righe > clic destro > **SVN: Revert Selected Changes**.
7. [ ] Pulsante annulla sulla riga `Item016 (modificato)`: torna `Articolo 16`. Ripeti sulla riga cancellata (dove era `Item296`): ricompare. Stesso risultato col cursore sulla riga + menu contestuale, sempre senza dialoghi.
8. [ ] Annulla anche l'ultima modifica: `Catalog.cs` sparisce da Changes (nessuna modifica rimasta).
9. [ ] Clic destro su file/cartella in Solution Explorer: **nessun** sottomenu "SVN".

**SVN Update e commit**
10. [ ] Clic sull'indicatore arancio (o SVN Update): arriva il commit del collega (`MainWindow.cs`), l'indicatore diventa **verde**; il messaggio verde di esito sparisce dopo ~5 s.
11. [ ] Genera con AI (Copilot): durante la generazione vedi l'avanzamento; il messaggio ha un titolo e un elenco puntato che descrive **davvero** le modifiche (Order.cs TODO, Payment.cs, Invoice.cs rimosso) e segue le regole di `.github/copilot-instructions.md` della simulazione: formato **Conventional Commits** (`feat(core): ...`). Vale anche per `CLAUDE.md`/`AGENTS.md` e per i file globali `~/.copilot/copilot-instructions.md`, `~/.claude/CLAUDE.md`.
12. [ ] **Commit All**: va a buon fine; il messaggio di esito sparisce da solo; con la WC pulita lo stato vuoto "Nessuna modifica" sta **sotto** la sezione Staged.
13. [ ] Pulsante **Recenti** accanto al messaggio: si apre un pannello largo quanto la finestra, con il campo di ricerca in cima e, per ogni messaggio, la prima riga in grassetto + 2-3 righe di anteprima (intero sulla voce selezionata o sotto il mouse). Nessuna voce "```": se Genera con AI ha messaggi salvati in blocco di codice, compaiono ripuliti. Scrivi parte di un messaggio: l'elenco si filtra; frecce su/giù + Invio (o un clic) lo reinseriscono nel campo; Esc chiude. Il pannello non supera ~60% dell'altezza (scorre).

## B. Switch e Merge (`wc-merge\Shop.sln`)

**Switch**
14. [ ] **Switch...**: in cima "Usati di recente" (vuoto al primo uso), la ricerca filtra subito (scrivi `login`), al massimo 50 righe con "Mostrati N di M".
15. [ ] **Mostra tutti / vista estesa…**: la finestra occupa quasi tutta l'area; tabella Nome / Tipo / Rev. / Autore / Data con 152 branch + 20 tag. Filtri: nome, tipo, **autore** (elenco), **periodo** (Ultimi 7/30/90 giorni o Da/A) e il contatore "N di M" si aggiorna; clic su Nome, Rev., Autore o Data ordina (anche inverso); **Azzera filtri** li toglie tutti (spento se non c'è nulla da azzerare); scorrimento fluido.
16. [ ] Switch su `branches/hotfix-login`: il ramo cambia, `Customer.cs` ha `Locked`; torna su `trunk` e verifica che compaia tra i recenti.

**Merge con conflitti**
17. [ ] Extensions > SVNAssist > **SVN Merge...** > `feature-coupon`: il Log si apre **come documento** "Merge da branches/feature-coupon" con r8, r9, r10 (autore, data, messaggio).
18. [ ] **Anteprima**: Coupon.cs A, Order.cs U, README.txt C, docs/old-setup.md conflitto ad albero; la WC non cambia.
19. [ ] Seleziona solo r8 > **Merge selezionate (1)**: Coupon.cs e Order.cs in Changes, nessun conflitto; messaggio precompilato in formato TortoiseSVN (`Unite le revisioni 8 da branches/feature-coupon:` + `........` + `r8 | mrossi | ...`). **Annulla merge** (conferma con elenco) riporta la WC pulita.
20. [ ] **Merge tutto**: dialogo "2 elementi in conflitto: risolverli ora?" → **Risolvi ora** porta in primo piano Changes.
21. [ ] Riquadro **Conflitti (2)** (più grande e leggibile, con il pulsante freccia per comprimerlo/espanderlo): README.txt (testo) con **Apri editor di merge / Usa la mia / Usa la loro / Segna come risolto**; sotto, il gruppo **Conflitti ad albero (1)** (docs/old-setup.md) con la spiegazione, **Segna tutti come risolti** e **Mostra elenco** che apre la singola voce con il suo Segna come risolto.
22. [ ] **Apri editor di merge**: si apre l'editor a 3 vie di VS; sistema il file, salva, **Segna come risolto**: il conflitto sparisce e i file di appoggio pure. (Con i marcatori ancora nel file deve rifiutare.)
23. [ ] Tree conflict: **Segna tutti come risolti** chiede conferma con l'elenco dei file, poi li risolve (in alternativa **Mostra elenco** > **Segna come risolto** sulla singola voce). Se uno fallisce il messaggio lo riporta e gli altri restano risolti. Il banner dice "senza conflitti"; nel Log la vista merge dice **"Merge eseguito, da committare"** (non "0 revisioni da unire").
24. [ ] Commit All: va a buon fine. Riaprendo SVN Merge da `feature-coupon` non c'è più nulla da unire.
25. [ ] (facoltativo) Rifai il setup e prova **Usa la mia** / **Usa la loro** su README.txt: chiedono conferma e tengono la versione giusta (trunk `1.0.1` / branch `1.1-coupon`).

## C. In azienda (se puoi)
26. [ ] Repo vero con accesso integrato di Windows: Switch, Merge e controllo revisione senza richieste di login; indicatore neutro (senza popup) se la rete non c'è.
27. [ ] Genera con AI su un merge grande (decine di file): nessun errore "No response" / "invalid start of a value"; messaggio completo.
28. [ ] Chiudi VS: in Task Manager non resta nessun `SVNAssist.Backend.exe`.
