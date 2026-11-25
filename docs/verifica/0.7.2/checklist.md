# Collaudo 0.7.2

Collaudo solo automatico, per scelta dell'utente (nessuna prova manuale in Visual Studio prima del rilascio: eventuali problemi arrivano come bug).

## Verifiche automatiche (CI Windows, `windows-latest`, run 45589519110, 2026-11-25)

| # | Area | Verifica | Esito |
|---|------|----------|-------|
| 1 | Build Backend e test | `dotnet build tests/SVNAssist.Tests/SVNAssist.Tests.csproj -c Release` | ✅ |
| 2 | Pacchetto VSIX | `msbuild src/SVNAssist.Host` Release, `SVNAssist.vsix` prodotto | ✅ |
| 3 | Test xUnit | `dotnet test` | ✅ 996/996 |
| 4 | Test UI web | `node --test "tests/ui-web/*.test.mjs"` | ✅ 200/200 |

## Coperto dai nuovi test

| Voce | Test |
|------|------|
| `%USERPROFILE%\copilot-instructions.md` letto | `AiBugFixTests.Regole_FilePersonaleDiVisualStudioNelProfilo_VieneLetto` |
| Stesso testo in `~/.copilot` e `~/`: una volta sola | `AiBugFixTests.Regole_StessoFileInDotCopilotENelProfilo_UnaVoltaSola` |
| Testi diversi: entrambi, prima delle regole di progetto | `AiBugFixTests.Regole_DotCopilotENelProfiloDiversi_EntrambiPrimaDelProgetto` |

## Non provato (nessuna prova manuale)

- Genera con AI in Visual Studio con il solo `%USERPROFILE%\copilot-instructions.md`.
