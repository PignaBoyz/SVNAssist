<#
    Collaudo mirato 0.5.0 (docs/verifica/0.5.0/checklist.md): installa il VSIX appena compilato in SvnHost, ricrea la simulazione e apre VS.
    Uso (PowerShell, nella cartella del repo, con VS CHIUSO):
      .\tools\collaudo-mirato.ps1 -Mini        # parte A con simulazione piccola (pochi secondi)
      .\tools\collaudo-mirato.ps1              # parte A: lavoro in corso (wc)
      .\tools\collaudo-mirato.ps1 -Parte B     # parte B: Switch, Merge, conflitti (wc-merge), senza ricreare
      .\tools\collaudo-mirato.ps1 -SoloSim     # parte A senza reinstallare il VSIX: ricrea solo la simulazione
      .\tools\collaudo-mirato.ps1 -Collega     # con VS aperto: un "collega" committa un file nuovo sul trunk
#>
param([ValidateSet('A', 'B')][string]$Parte = 'A', [switch]$SoloSim, [switch]$Mini, [switch]$Collega)
$ErrorActionPreference = 'Stop'
$ide = 'C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE'
$vsix = Join-Path $PSScriptRoot '..\src\SVNAssist.Host\bin\Release\net472\SVNAssist.vsix'
$sim = Join-Path $env:TEMP 'svnsim'

if ($Collega) {
    # Commit di un altro utente su un file che tu non hai: la tua working copy resta indietro senza saperlo.
    $dir = Join-Path $sim '_collega'; $url = 'file:///' + ("$sim\repo" -replace '\\', '/')
    if (!(Test-Path $dir)) { & svn checkout -q "$url/trunk" $dir } else { & svn update -q $dir }
    $f = Join-Path $dir ('Collega-' + (Get-Date -Format 'HHmmss') + '.txt')
    Set-Content $f 'lavoro del collega'
    & svn add -q $f
    & svn commit -q --username lbianchi -m 'Lavoro del collega' $dir
    Write-Host "[ok] lbianchi ha committato $(Split-Path $f -Leaf) sul trunk. Ora in VS committa una tua modifica."
    return
}

if (Get-Process devenv -ErrorAction SilentlyContinue) { throw 'Chiudi Visual Studio e rilancia lo script.' }

if ($Parte -eq 'A' -and !$SoloSim) {
    Write-Host '[..] Installazione VSIX in SvnHost...'
    # stessa versione gia' installata = errore 1001 anche con /force: prima si disinstalla (codice ignorato se assente)
    Start-Process "$ide\VSIXInstaller.exe" -ArgumentList '/rootSuffix:SvnHost', '/quiet', '/u:SVNAssist' -Wait
    $p = Start-Process "$ide\VSIXInstaller.exe" -ArgumentList '/rootSuffix:SvnHost', '/quiet', '/force', "`"$((Resolve-Path $vsix).Path)`"" -Wait -PassThru
    if ($p.ExitCode) { throw "VSIXInstaller: codice $($p.ExitCode)" }
    Start-Process "$ide\devenv.exe" -ArgumentList '/rootsuffix', 'SvnHost', '/updateconfiguration' -Wait
}
if ($Parte -eq 'A') {
    Write-Host '[..] Ricreo la simulazione...'
    & (Join-Path $PSScriptRoot 'setup-sim.ps1') -Root $sim -Force -Mini:$Mini | Out-Null
    # 0.5.0: lock di un altro utente su un file modificato in wc (lucchetto "Bloccato da mrossi")
    $tmp = Join-Path $sim '_lock'; $url = 'file:///' + ("$sim\repo" -replace '\\', '/')
    & svn checkout -q --depth files "$url/trunk/src/Shop/Core" $tmp
    & svn lock -q --username mrossi -m 'Sto rifacendo il catalogo' "$tmp\Catalog.cs"
}

$sln = if ($Parte -eq 'A') { "$sim\wc\Shop.sln" } else { "$sim\wc-merge\Shop.sln" }
Start-Process "$ide\devenv.exe" -ArgumentList '/rootsuffix', 'SvnHost', "`"$sln`""

Write-Host ''
if ($Parte -eq 'A') {
    Write-Host 'PARTE A - wc (Shop.sln) - novita'' 0.5.0 (dettagli in docs/verifica/0.5.0/checklist.md):'
    Write-Host ' 1. Lock: Options > SVNAssist > General: New revision check (minutes) = 1; dopo il ciclo Catalog.cs in Changes ha il lucchetto "Bloccato da mrossi".'
    Write-Host ' 2. Create Patch: tasto destro sulla sezione Modifiche > Create Patch... > salva. Poi Revert di tutto: compare Ripristina modifiche, cliccalo.'
    Write-Host ' 3. Apply Patch: Extensions > SVNAssist > Apply Patch... sul file salvato prima (dopo un nuovo Revert): anteprima e applica.'
    Write-Host ' 4. Marcatori: scrivi <<<<<<< / ======= / >>>>>>> in Order.cs e premi Commit: avviso marcatori, rispondi No.'
    Write-Host ' 5. Branch/Tag: Extensions > SVNAssist > Branch/Tag... nome prova-050, Passa subito: crea e switcha con barra in percentuale.'
    Write-Host ' 6. Ripeti con lo stesso nome: errore esiste gia''. Prova anche un nome con spazio iniziale o con i due punti: nome non valido.'
    Write-Host ' 7. Update to revision: finestra di avanzamento con Annulla.'
    Write-Host 'Poi chiudi VS e lancia: .\tools\collaudo-mirato.ps1 -Parte B'
} else {
    Write-Host 'PARTE B - wc-merge (pulita):'
    Write-Host ' 1. SVN Merge... > feature-coupon > Merge tutto: conflitti. In Conflitti: Usa la loro per tutti (conferma con elenco).'
    Write-Host ' 2. Rifai il merge (Annulla merge prima: deve chiedere/creare il backup e offrire Ripristina modifiche).'
    Write-Host ' 3. Switch... su un ramo: Calcolo del totale... poi barra con N di circa M elementi.'
}
