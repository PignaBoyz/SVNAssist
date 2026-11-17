<#
    Collaudo mirato 0.5.0 (docs/verifica/0.5.0/checklist.md): installa il VSIX in SvnHost, prepara la simulazione e apre VS.
    Ogni parte riparte da uno stato noto: A crea la simulazione (e una copia di partenza del repository), B/C/D la ricaricano
    prima di partire, cosi' i merge di una parte non cambiano l'esito della successiva. Le Options di SVNAssist (provider AI,
    lingua, intervalli) stanno nell'istanza SvnHost e NON vengono toccate: non vanno riconfigurate.

    Uso (PowerShell, nella cartella del repo, con VS CHIUSO):
      .\tools\collaudo-mirato.ps1 -Build          # una volta: pubblica il backend e ricompila il VSIX da zero (Rebuild)
      .\tools\collaudo-mirato.ps1                 # parte A: installa il VSIX, crea la simulazione, apre wc (lavoro in corso)
      .\tools\collaudo-mirato.ps1 -Collega        # con VS aperto (passo 7b): un "collega" committa un file nuovo sul trunk
      .\tools\collaudo-mirato.ps1 -Parte B        # B: Merge, conflitti per gruppo, Annulla merge (wc-merge ripartita pulita)
      .\tools\collaudo-mirato.ps1 -Parte C        # C: Log e Merge (file per revisione, anteprima, modifiche pendenti)
      .\tools\collaudo-mirato.ps1 -Parte D        # D: Merge interrotto e Continua merge
    Opzioni: -Mini (simulazione piccola, solo per provare lo script: la D non ha le centinaia di revisioni del passo 31),
             -SoloSim (A senza reinstallare il VSIX), -NoApri (prepara tutto ma non apre VS).
#>
param([ValidateSet('A', 'B', 'C', 'D')][string]$Parte = 'A', [switch]$SoloSim, [switch]$Mini, [switch]$Collega, [switch]$Build, [switch]$NoApri)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot
$ide = 'C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE'
$msbuild = 'C:\Program Files\Microsoft Visual Studio\18\MSBuild\Current\Bin\MSBuild.exe'
if (!(Test-Path $msbuild)) { $msbuild = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' }
$vsix = Join-Path $repoRoot 'src\SVNAssist.Host\bin\Release\net472\SVNAssist.vsix'
$sim = Join-Path $env:TEMP 'svnsim'
$repoDir = Join-Path $sim 'repo'
$repoUrl = 'file:///' + ($repoDir -replace '\\', '/')
$data = Join-Path $env:LOCALAPPDATA 'SVNAssist'

function Revs($branch) {
    # revisioni del ramo dopo la sua creazione, in ordine
    $l = & svn log -q "$repoUrl/branches/$branch" --stop-on-copy 2>$null | Select-String '^r(\d+)' | ForEach-Object { [int]$_.Matches[0].Groups[1].Value } | Sort-Object
    @($l | Select-Object -Skip 1)
}

if ($Collega) {
    # Commit di un altro utente su un file che tu non hai: la tua working copy resta indietro senza saperlo.
    $dir = Join-Path $sim '_collega'
    if (!(Test-Path $dir)) { & svn checkout -q "$repoUrl/trunk" $dir } else { & svn update -q $dir }
    $f = Join-Path $dir ('Collega-' + (Get-Date -Format 'HHmmss') + '.txt')
    Set-Content $f 'lavoro del collega'
    & svn add -q $f
    & svn commit -q --username lbianchi -m 'Lavoro del collega' $dir
    Write-Host "[ok] lbianchi ha committato $(Split-Path $f -Leaf) sul trunk. Ora in VS committa una tua modifica."
    return
}

if (Get-Process devenv -ErrorAction SilentlyContinue) { throw 'Chiudi Visual Studio e rilancia lo script.' }

if ($Build) {
    Write-Host '[..] Backend (publish) e VSIX (Rebuild: il build incrementale puo'' lasciare il manifest vecchio)...'
    & dotnet publish (Join-Path $repoRoot 'src\SVNAssist.Backend') -c Release -o (Join-Path $repoRoot 'src\SVNAssist.Backend\bin\publish') --nologo -v q | Out-Null
    if ($LASTEXITCODE) { throw 'dotnet publish fallito' }
    & $msbuild (Join-Path $repoRoot 'src\SVNAssist.Host\SVNAssist.Host.csproj') -p:Configuration=Release -t:Rebuild -restore -v:m -clp:ErrorsOnly
    if ($LASTEXITCODE) { throw 'MSBuild fallito' }
}
if (!(Test-Path $vsix)) { throw "VSIX non trovato: lancia prima con -Build ($vsix)" }
$newest = Get-ChildItem (Join-Path $repoRoot 'src') -Recurse -File -Include *.cs, *.js, *.css, *.html, *.vsct, *.vsixmanifest -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Sort-Object LastWriteTime | Select-Object -Last 1
if ($newest.LastWriteTime -gt (Get-Item $vsix).LastWriteTime) { Write-Warning "Il VSIX e' piu' vecchio di $($newest.Name): lancia con -Build." }

# stato dei dati di SVNAssist: nessun merge/switch "interrotto" lasciato da prove precedenti
foreach ($f in 'pending-merge.json', 'pending-switch.json') { Remove-Item (Join-Path $data $f) -ErrorAction SilentlyContinue }

if ($Parte -eq 'A') {
    if (!$SoloSim) {
        Write-Host '[..] Installazione VSIX in SvnHost...'
        # stessa versione gia' installata = errore 1001 anche con /force: prima si disinstalla (codice ignorato se assente)
        Start-Process "$ide\VSIXInstaller.exe" -ArgumentList '/rootSuffix:SvnHost', '/quiet', '/u:SVNAssist' -Wait
        $p = Start-Process "$ide\VSIXInstaller.exe" -ArgumentList '/rootSuffix:SvnHost', '/quiet', '/force', "`"$((Resolve-Path $vsix).Path)`"" -Wait -PassThru
        if ($p.ExitCode) { throw "VSIXInstaller: codice $($p.ExitCode)" }
        Start-Process "$ide\devenv.exe" -ArgumentList '/rootsuffix', 'SvnHost', '/updateconfiguration' -Wait
    }
    # log vecchi da parte: il backend-*.log del collaudo (Parte C) contiene solo questa sessione
    $logs = Join-Path $data 'logs'
    if (Test-Path $logs) {
        $old = Join-Path $logs ('prima-collaudo-' + (Get-Date -Format 'yyyyMMdd-HHmm')); New-Item -ItemType Directory -Force $old | Out-Null
        Get-ChildItem $logs -File | Move-Item -Destination $old
    }
    Write-Host "[..] Ricreo la simulazione$(if ($Mini) { ' (Mini)' } else { ' (qualche minuto: 150 branch, 20 tag, 300 revisioni)' })..."
    & (Join-Path $PSScriptRoot 'setup-sim.ps1') -Root $sim -Force -Mini:$Mini | Out-Null
    # 0.5.0: lock di un altro utente su un file modificato in wc (lucchetto "Bloccato da mrossi")
    $tmp = Join-Path $sim '_lock'
    & svn checkout -q --depth files "$repoUrl/trunk/src/Shop/Core" $tmp
    & svn lock -q --username mrossi -m 'Sto rifacendo il catalogo' "$tmp\Catalog.cs"
    $sln = "$sim\wc\Shop.sln"
} else {
    $dump = Join-Path $sim 'baseline.dump'
    if (!(Test-Path $dump)) { throw "Manca ${dump}: lancia prima la parte A (anche con -SoloSim)." }
    Write-Host '[..] Riparto dal repository di partenza (trunk pulito, nessun merge fatto)...'
    Remove-Item -Recurse -Force $repoDir, (Join-Path $sim 'wc-merge') -ErrorAction SilentlyContinue
    & svnadmin create $repoDir
    cmd /c "svnadmin load -q ""$repoDir"" < ""$dump"""
    if ($LASTEXITCODE) { throw 'svnadmin load fallito' }
    & svn checkout -q "$repoUrl/trunk" (Join-Path $sim 'wc-merge')
    $sln = "$sim\wc-merge\Shop.sln"
}

if ($NoApri) { Write-Host "[ok] Pronto, VS non aperto. Per aprire: & '$ide\devenv.exe' /rootsuffix SvnHost '$sln'" }
else { Start-Process "$ide\devenv.exe" -ArgumentList '/rootsuffix', 'SvnHost', "`"$sln`"" }

Write-Host ''
if ($Parte -eq 'A') {
    Write-Host "PARTE A - wc (Shop.sln), passi 1-11 (docs/verifica/0.5.0/checklist.md)."
    Write-Host ' Prima: Options > SVNAssist > General: New revision check (minutes) = 1 (passo 1).'
    Write-Host ' Passo 7b (con VS aperto): .\tools\collaudo-mirato.ps1 -Collega'
    Write-Host ' Alla fine chiudi VS e lancia: .\tools\collaudo-mirato.ps1 -Parte B'
    Write-Host ' ATTENZIONE: B, C e D ricaricano il repository: la working copy "wc" della parte A non va piu'' usata dopo.'
    return
}
$s = Revs 'feature-sconti'; $c = Revs 'feature-coupon'; $st = Revs 'feature-storico'
Write-Host "PARTE $Parte - wc-merge (pulita, alla HEAD). Repository ricaricato dalla copia di partenza."
switch ($Parte) {
    'B' {
        Write-Host " Passi 12-14. Usa feature-sconti: r$($s -join ' r'); la 3a (r$($s[2])) va in conflitto di testo su Order.cs e Invoice.cs (2 conflitti: compare 'per tutti')."
        Write-Host " feature-coupon (r$($c -join ' r')) ha 1 conflitto di testo (README) e 2 tree conflict: serve per la Parte C."
        Write-Host ' Alla fine: chiudi VS e .\tools\collaudo-mirato.ps1 -Parte C'
    }
    'C' {
        Write-Host " Passi 15-23 con feature-coupon (r$($c -join ' r')). Passo 23 (due revisioni non adiacenti, la prima in conflitto): feature-sconti r$($s[2]) e r$($s[3])."
        Write-Host " Dopo il collaudo allega $data\logs\backend-aaaammgg.log (righe 'merge load/files/preview/run: ... ms')."
        Write-Host ' Alla fine: chiudi VS e .\tools\collaudo-mirato.ps1 -Parte D'
    }
    'D' {
        Write-Host " Passi 24-33. Usa feature-sconti: r$($s -join ' r'); si ferma alla 3a (r$($s[2])) per conflitto in 2 file; resta r$($s[3])."
        Write-Host " Passo 31 (merge lungo, Annulla a meta'): feature-storico, $($st.Count) revisioni pulite$(if ($st.Count -lt 100) { ' (troppo poche: la simulazione e'' Mini, rilancia la parte A senza -Mini)' })."
        Write-Host " Passo 33 (merge da cartella): Solution Explorer > cartella src\Shop\Core > SVN Merge... > feature-sconti: tutte le sue revisioni toccano quella cartella."
        Write-Host ' Ripartire da capo la D: rilancia -Parte D (ricarica il repository e svuota lo stato del merge interrotto).'
    }
}
