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
