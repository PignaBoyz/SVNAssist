# Bug e richieste segnalati sulla 0.4.0 (2026-11-17)

Fonte: appunti dell'utente, immagine `bugs.png` in questa cartella.

1. Merge molto lento.
2. Se clicco su "Recent" non mi seleziona e non mi scrive.
3. Quando premo un altro bottone, però, si aggiorna con il testo dei recenti: sembra che non si aggiorni in diretta.
4. Gestione merge da una cartella.
5. Usare entrambe le righe nei merge.
6. Quando faccio il merge e seleziono le revision vorrei vedere i file coinvolti prima di mergiare.
7. Possibilità di mergiare anche con modifiche pendenti.
8. Errore nel commit dopo un merge: generarlo con AI non va perché GitHub Copilot ha levato i modelli. Voglio la possibilità di fare dei refresh quando scelgo i modelli e di capire quale sia il problema, perché così resto bloccato e posso scegliere solo tre modelli. Serve la lista aggiornata ogni volta: così abbiamo bloccato una feature.

## Letture adottate (da confermare nel collaudo)
- 4: "SVN Merge..." da una cartella di Solution Explorer lavora solo su quel sottoalbero.
- 5: "usa entrambe" = mia+loro e loro+mia nella risoluzione dei conflitti.

## Aggiunte dell'utente (2026-11-17, dopo le foto del collaudo aziendale)
9. Tasto destro (menu contestuale) sulle righe selezionate, in Changes e nel Log.
10. Pulsante chiaro "Segna come risolto" sul file aperto in conflitto.
11. Avanzamento del merge doppio (finestra Changes + scheda Log): deve esserci una sola vista completa.
12. Nel Log: vedere quali file sono entrati in ogni revisione (come svn log -v / "solo percorsi interessati"); nell'anteprima del merge, per ogni revisione cosa modifica.
13. Merge interrotto da un conflitto (E155015): applicare le revisioni una alla volta con avanzamento "revisione N di TOT (rNNNNN)"; quando si ferma dirlo chiaramente: cosa è stato applicato, cosa manca e cosa fare (risolvi i conflitti, poi "Continua merge"); NON va rifatto un merge completo, solo le revisioni rimaste; stato salvato su disco (come pending-switch.json), banner con Continua/Annulla, backup patch per annullare.
14. Modelli AI: tutti quelli disponibili dal provider, niente "consigliato", costo stimato per commit con Copilot, stato del collegamento Copilot.
