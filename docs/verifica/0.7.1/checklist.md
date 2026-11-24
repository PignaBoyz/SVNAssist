# Collaudo 0.7.1

Collaudo solo automatico, per scelta dell'utente (nessuna prova manuale in Visual Studio prima del rilascio: eventuali problemi arrivano come bug).

## Verifiche automatiche (CI Windows, `windows-latest`, run 45336121831, 2026-11-24)

| # | Area | Verifica | Esito |
|---|------|----------|-------|
| 1 | Build Backend e test | `dotnet build tests/SVNAssist.Tests/SVNAssist.Tests.csproj -c Release` | ✅ 0 Warning(s), 0 Error(s) |
| 2 | Pacchetto VSIX | `msbuild src/SVNAssist.Host` Release, `SVNAssist.vsix` prodotto | ✅ |
| 3 | Test xUnit (svn vero con SlikSvn) | `dotnet test` | ✅ 993/993 |
| 4 | Test UI web | `node --test "tests/ui-web/*.test.mjs"` (anche in locale) | ✅ 198/198 |

## Coperto dai nuovi test

| Voce | Test |
|------|------|
| Watcher: solo `.svn` / `.svn-assist` scartati d'ufficio | `RefreshDebouncerTests.IsNoise_SoloSvnESvnAssist` (Debug, Release, x64, bin, obj, .vs, packages, .log non piu' scartati) |
| Cartelle ignorate imparate da svn, una volta, con tutta la catena | `ChangesStoreIgnoredTests` (raffica sotto `bin` ignorata: nessun `svn status`; `x64` / `Release` versionate: refresh; raffica mista; errore di svn; riclassificazione dopo un refresh manuale) |
| Gemini mai in Automatico | `AiServiceFactoryTests` (chiave dedicata e variabili d'ambiente: `auto` -> nessun provider, `gemini` -> Gemini), `PrerequisitesTests.Gemini_MaiInAuto_AncheConChiave_SoloSeScelto` |
| Merge: nessun `svn resolve` alla chiusura | `ConflictsTests.MergeInProcess_AllaChiusuraNonRisolveMai_DiceCosaFare` |
| Righe oltre 300 non inviate | `ChangesStoreTests.RigheOltre300_NonVannoAllaUi_FinoAMostraTuttiOFiltro`, `SnapshotSchemaTests.SnapshotChanges_ConRigheTagliate_RispettaLoSchema`, `render.test.mjs` (pulsante Mostra tutti con `hidden`) |

## Non provato (nessuna prova manuale)

- Comportamento in Visual Studio reale del watcher su una solution con `bin` / `obj` non in `svn:ignore`.
- Editor di merge di Visual Studio: testo del messaggio alla chiusura.
- `svn status --no-ignore --depth empty` su working copy grandi (provato solo su repository di test).
