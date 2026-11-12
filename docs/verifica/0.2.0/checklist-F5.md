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
19. [ ] Extensions > SVNAssist > "Cambia provider AI..." apre Changes con la procedura; "Faccio io" apre Tools > Options > SVNAssist; Annulla non cambia nessuna impostazione.
20. [ ] "Genera con AI" con 0 file in Staged: banner giallo ben visibile con icona (a ogni clic si ridisegna e lampeggia); durante la generazione il pulsante è disabilitato con spinner e "Generazione in corso…"; un errore del provider compare in rosso.
21. [ ] Pulsante in alto "SVN Update"; dopo l'update il messaggio in basso ("Aggiornato alla revisione N.") è verde con icona e un breve alone che sfuma.
22. [ ] Barra in basso: "Prerequisiti" verde se tutto ok; senza svn o senza provider AI è rosso con icona di avviso e, cliccandolo, apre la procedura sui prerequisiti mancanti (con "Configura l'AI" e "Faccio io").
23. [ ] Repo `file://` senza trunk/branches: l'header mostra "/ (radice)" invece di "unknown".

**Cleanup, Update to revision, Lock e login SVN** (repo `file://` o con server; per il login serve un server che chieda utente/password)
24. [ ] Changes > ⋯ > "SVN Cleanup": messaggio verde "Cleanup completato." Stesso comando da Extensions > SVNAssist > Cleanup e da Solution Explorer > SVN > SVN Cleanup.
25. [ ] Blocca la working copy (es. `svn cleanup` interrotto, o una cartella `.svn` con lock): Update dà errore rosso con il pulsante "Esegui Cleanup"; cliccandolo l'errore sparisce e Update funziona.
26. [ ] Changes > ⋯ > "SVN Update to revision…": scrivi `abc` o `0` e il dialogo ricompare con il messaggio; scrivi un numero valido (es. la revisione precedente) e il messaggio dice "Aggiornato alla revisione N."; con `HEAD` torni all'ultima. Annulla non fa nulla. Idem da Extensions > SVNAssist e da Solution Explorer.
27. [ ] Su un file modificato: tasto destro > "SVN Lock…" (messaggio facoltativo) > compare il lucchetto accanto al nome; `svn status` da terminale mostra la K. "SVN Unlock" lo toglie. Da Solution Explorer > SVN > Lock/Unlock su un file non modificato: il file compare in Modifiche con la lettera K.
28. [ ] File bloccato da un altro utente (blocca da un secondo checkout): "SVN Lock…" chiede una conferma esplicita ("già bloccato da…, rubare il lock?"); Annulla non cambia nulla, OK ruba il lock.
29. [ ] Login SVN: con credenziali non salvate su un server che le richiede, Update mostra il dialogo utente/password (password mascherata, "Ricorda" attivo); inserite le credenziali il comando riesce una volta sola; con credenziali sbagliate compare l'errore normale (nessun secondo dialogo); Annulla = errore normale. Con "Ricorda" i comandi successivi non chiedono più nulla. Nei log (`%LOCALAPPDATA%\SVNAssist`) non compare la password.
30. [ ] Con l'accesso integrato di Windows (il tuo caso abituale) il dialogo di login non compare mai.
