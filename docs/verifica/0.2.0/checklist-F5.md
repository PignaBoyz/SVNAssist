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
