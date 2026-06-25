# tools

Script di supporto allo sviluppo di SVNAssist.

## setup-local-svn.ps1

Crea un ambiente SVN **locale e usa-e-getta** (protocollo `file:///`, nessun server)
per testare l'estensione senza dipendere dal tuo SVN aziendale.

Cosa crea:
- un repository SVN locale con struttura `trunk` / `branches` / `tags`
- una working copy con una piccola solution .NET demo (`DemoApp.sln`)
- un commit iniziale e un branch di esempio (`branches/feature-demo`)

### Uso

```powershell
# Ambiente di default in %USERPROFILE%\SVNAssistSandbox
./tools/setup-local-svn.ps1

# Percorso custom, ricreando se esiste
./tools/setup-local-svn.ps1 -Root C:\dev\svnsandbox -Force
```

Prerequisito: `svn.exe` e `svnadmin.exe` nel PATH (es. `winget install --id Slik.Subversion -e`).

### Come testare l'estensione

1. Apri `SVNAssist.sln` in Visual Studio e premi **F5** → si apre l'istanza sperimentale.
2. Nell'istanza sperimentale apri la `DemoApp.sln` creata dallo script.
3. SVNAssist rileva automaticamente la working copy. Modifica `Program.cs` per vedere
   lo stato passare a **Modified** nel pannello *SVN Changes*.

> In alternativa puoi puntare l'estensione a una working copy SVN che hai già mappato tu:
> lo script serve solo ad avere un ambiente riproducibile e cancellabile.
