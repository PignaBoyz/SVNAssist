# Collaudo 0.7.0

Verifiche eseguite al `2026-11-23` su Visual Studio 18 (2026) e suite automatiche, più i controlli consigliati su solution aziendale reale.

## Verifiche automatiche e collaudo live VS 18 (`/rootSuffix:SvnHost`)

| # | Area | Verifica eseguita | Esito |
|---|------|-------------------|-------|
| 1 | Test UI Web | `node --test "tests/ui-web/*.test.mjs"` (contrasto WCAG, schemi JSON, render, scorciatoie, CSP, changelog-sync, release-body) | ✅ 193/193 passati |
| 2 | Compilazione Backend & Test | `dotnet build tests/SVNAssist.Tests/SVNAssist.Tests.csproj -c Release` e `dotnet test` | ✅ 0 Warning(s), 983/983 passati |
| 3 | Pacchetto VSIX Host | `MSBuild` Rebuild Release `SVNAssist.Host` + verifica manifest `0.7.0` nello zip `.vsix` | ✅ 0 warning, `SVNAssist.vsix` valido |
| 4 | Avvio VS 18 live (`/rootSuffix:SvnHost`) | Installazione VSIX, `/updateconfiguration`, avvio backend .NET `10.0.15`, WebView2 `157.0.4341.60` | ✅ Avvio pulito |
| 5 | Scorciatoie diff (`Alt+F8` / `Shift+Alt+F8`) | Verifica file log `host-*.log` per assenza di `E_INVALIDARG` / scorciatoia non valida | ✅ 0 errori `E_INVALIDARG` |
| 6 | Watcher & commit live in VS 18 | Commit `r2 selftest` e modifica esterna su file rilevata in ~3 s, uscita VS con 0 processi residui | ✅ Passato |

## Controlli su progetto reale (opzionali)

| # | Passo | Atteso | Esito |
|---|-------|--------|-------|
| A | Due istanze di Visual Studio aperte su due repository diversi: aprire editor di merge su un conflitto da `SVN Changes` | L'editor di merge si apre nell'istanza corrente di VS; salvando e chiudendo il file viene aggiornato nella working copy corretta | |
| B | `Tools > Options > SVNAssist > Provider collegato`: incollare API key Gemini/Claude/OpenAI e premere **Testa chiave** | La chiave viene salvata cifrata (DPAPI) e compare il conteggio dei modelli live disponibili (o il messaggio d'errore del provider) | |
| C | `Tools > Options > SVNAssist > Generale`: attivare **Modalità leggera (PC poco potenti)** su una solution grande | Nessuna scansione continua o durante la build; lo stato si aggiorna salvando un file in VS, tornando sulla finestra di VS (intervallo min 5 s) o con Refresh manuale | |
| D | Barra di commit & intestazione Changes: click sul percorso Working Copy, pulsante **Merge**, badge AI e azioni inline su **Shelved** | Il percorso apre/chiude la modifica con `Esc`, **Merge** apre il selettore merge, il badge apre la procedura AI, **Shelved** applica/elimina con le icone inline | |
