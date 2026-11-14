<#
    Collaudo mirato 0.4.0: installa il VSIX appena compilato in SvnHost, ricrea la simulazione e apre VS.
    Uso (PowerShell, nella cartella del repo, con VS CHIUSO):
      .\tools\collaudo-mirato.ps1              # parte A: lavoro in corso (wc)
      .\tools\collaudo-mirato.ps1 -Parte B     # parte B: Switch, Merge, conflitti (wc-merge), senza ricreare
#>
param([ValidateSet('A', 'B')][string]$Parte = 'A')
$ErrorActionPreference = 'Stop'
$ide = 'C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE'
$vsix = Join-Path $PSScriptRoot '..\src\SVNAssist.Host\bin\Release\net472\SVNAssist.vsix'
$sim = Join-Path $env:TEMP 'svnsim'

if (Get-Process devenv -ErrorAction SilentlyContinue) { throw 'Chiudi Visual Studio e rilancia lo script.' }

if ($Parte -eq 'A') {
    Write-Host '[..] Installazione VSIX in SvnHost...'
    $p = Start-Process "$ide\VSIXInstaller.exe" -ArgumentList '/rootSuffix:SvnHost', '/quiet', '/force', "`"$((Resolve-Path $vsix).Path)`"" -Wait -PassThru
    if ($p.ExitCode) { throw "VSIXInstaller: codice $($p.ExitCode)" }
    Start-Process "$ide\devenv.exe" -ArgumentList '/rootsuffix', 'SvnHost', '/updateconfiguration' -Wait
    Write-Host '[..] Ricreo la simulazione...'
    & (Join-Path $PSScriptRoot 'setup-sim.ps1') -Root $sim -Force | Out-Null
}

$sln = if ($Parte -eq 'A') { "$sim\wc\Shop.sln" } else { "$sim\wc-merge\Shop.sln" }
Start-Process "$ide\devenv.exe" -ArgumentList '/rootsuffix', 'SvnHost', "`"$sln`""

Write-Host ''
if ($Parte -eq 'A') {
    Write-Host 'PARTE A - wc (Shop.sln, lavoro in corso):'
    Write-Host ' 1. Solution Explorer: icone di stato (Catalog.cs/Order.cs modificati, Payment.cs aggiunto, gli altri col lucchetto).'
    Write-Host ' 2. Tasto destro su Catalog.cs > Check for Modifications: diff di VS. Su Order.cs e sul progetto > Show Log: log del solo file.'
    Write-Host ' 3. Aggiungi una classe Foo.cs al progetto: icona non versionato > tasto destro > SVN Add: icona aggiunto, in Changes e'' A.'
    Write-Host ' 4. Changes: crea una cartella tmp con un file, Canc sulla cartella: conferma, finisce nel cestino.'
    Write-Host ' 5. Genera con AI, poi menu AI > Piu'' corto e Piu'' dettagliato.'
    Write-Host ' 6. Commit All (Note.txt non versionato): compare la domanda Si'' / No / Annulla. Premi Annulla.'
    Write-Host ' 7. Show Log: spunta Include merged revisions (merge di hotfix-login annidato), clic su un file = diff; spegni/accendi Stop on copy.'
    Write-Host ' 8. Switch... su un ramo: avviso working copy sporca > Apri Cleanup: opzioni rosse chiedono conferma. Annulla tutto.'
    Write-Host 'Poi chiudi VS e lancia: .\tools\collaudo-mirato.ps1 -Parte B'
} else {
    Write-Host 'PARTE B - wc-merge (pulita):'
    Write-Host ' 1. Switch... > feature-grande (2000 file): finestra di avanzamento con righe che scorrono. Premi Annulla a meta''.'
    Write-Host ' 2. Avviso working copy a meta'': prova Torna a prima dello switch (torni su trunk). Poi rifai lo switch e lascialo finire.'
    Write-Host ' 3. Switch di nuovo su trunk. SVN Merge... > feature-coupon > Merge tutto: avanzamento con C evidenziate > Apri i conflitti.'
    Write-Host ' 4. Menu Extensions > SVNAssist > Cleanup con Revert all: il bottone resta disabilitato senza la spunta di conferma.'
}
