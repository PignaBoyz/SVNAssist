<#
    Collaudo mirato 0.3.0: installa il VSIX appena compilato in SvnHost, ricrea la simulazione e apre VS.
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
    Write-Host 'PARTE A — wc (Extensions > SVNAssist > Changes):'
    Write-Host ' 1. Doppio clic su Catalog.cs: Ctrl+Alt+Giu/Su saltano tra le 4 modifiche.'
    Write-Host ' 2. Accanto a ogni blocco modificato c''e'' il pulsante Annulla: un clic annulla solo quel blocco, nessun dialogo.'
    Write-Host ' 3. Genera con AI: messaggio Conventional Commits senza ``` ; Commit All; tendina Recenti larga, con anteprima e ricerca.'
    Write-Host 'Poi chiudi VS e lancia: .\tools\collaudo-mirato.ps1 -Parte B'
} else {
    Write-Host 'PARTE B — wc-merge:'
    Write-Host ' 1. Switch... > Mostra tutti / vista estesa: grande, filtri nome/tipo/autore/date, ordinamenti, Azzera filtri.'
    Write-Host ' 2. SVN Merge... > feature-coupon > Merge tutto > Risolvi ora.'
    Write-Host ' 3. Riquadro Conflitti ampio: README.txt (testo) singolo; "Conflitti ad albero (2)" con Segna tutti come risolti.'
    Write-Host ' 4. Risolvi README con l''editor di merge, poi Commit All.'
}
