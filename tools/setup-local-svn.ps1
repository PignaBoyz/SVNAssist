<#
.SYNOPSIS
    Crea un ambiente SVN locale usa-e-getta (senza server) per testare SVNAssist.

.DESCRIPTION
    Sfrutta il protocollo file:/// (nessun server SVN necessario) per:
      1. creare un repository SVN locale con svnadmin
      2. creare la struttura standard trunk / branches / tags
      3. fare il checkout di trunk in una working copy
      4. popolare la working copy con una piccola solution .NET demo (DemoApp)
      5. fare il commit iniziale e un branch di esempio

    Al termine stampa il percorso della working copy da aprire nell'istanza
    sperimentale di Visual Studio (F5): SVNAssist la rileverà automaticamente.

    L'ambiente è completamente locale e cancellabile: rilancia con -Force per ricrearlo.

.PARAMETER Root
    Cartella in cui creare l'ambiente. Default: %USERPROFILE%\SVNAssistSandbox.
    Evita percorsi con spazi per semplicità degli URL file:///.

.PARAMETER Force
    Se presente, elimina e ricrea l'ambiente se la cartella Root esiste già.

.EXAMPLE
    ./setup-local-svn.ps1
.EXAMPLE
    ./setup-local-svn.ps1 -Root C:\dev\svnsandbox -Force
#>
[CmdletBinding()]
param(
    [string]$Root = (Join-Path $env:USERPROFILE 'SVNAssistSandbox'),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

function Find-Exe([string]$name) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $known = @(
        (Join-Path $env:ProgramFiles 'SlikSvn\bin'),
        (Join-Path $env:ProgramFiles 'TortoiseSVN\bin'),
        (Join-Path $env:ProgramFiles 'Subversion\bin')
    )
    foreach ($dir in $known) {
        $candidate = Join-Path $dir $name
        if (Test-Path $candidate) { return $candidate }
    }
    return $null
}

# Converte un percorso Windows in URL file:/// valido (con escaping minimo degli spazi).
function To-FileUrl([string]$path) {
    $full = (Resolve-Path $path).Path -replace '\\', '/'
    return 'file:///' + ($full -replace ' ', '%20')
}

# Esegue svn e lancia un errore se il comando fallisce.
function Invoke-Svn {
    param([Parameter(ValueFromRemainingArguments = $true)] [string[]]$Args)
    & $svn @Args
    if ($LASTEXITCODE -ne 0) { throw "svn $($Args -join ' ') fallito (exit $LASTEXITCODE)" }
}

$svn = Find-Exe 'svn.exe'
$svnadmin = Find-Exe 'svnadmin.exe'

if (-not $svn -or -not $svnadmin) {
    Write-Host "[X] svn.exe / svnadmin.exe non trovati." -ForegroundColor Red
    Write-Host "    Installa SlikSVN, ad esempio:  winget install --id Slik.Subversion -e" -ForegroundColor Yellow
    exit 1
}
Write-Host "[ok] svn:      $svn"
Write-Host "[ok] svnadmin: $svnadmin"

if (Test-Path $Root) {
    if (-not $Force) {
        Write-Host "[X] '$Root' esiste gia'. Usa -Force per ricrearlo." -ForegroundColor Red
        exit 1
    }
    Write-Host "[..] Rimozione ambiente esistente..."
    # I file .svn possono essere read-only: togliamo l'attributo prima di cancellare.
    Get-ChildItem $Root -Recurse -Force -ErrorAction SilentlyContinue |
        ForEach-Object { $_.Attributes = 'Normal' }
    Remove-Item $Root -Recurse -Force
}

$repoDir = Join-Path $Root 'repo'
$wcDir = Join-Path $Root 'wc'
New-Item -ItemType Directory -Force -Path $Root | Out-Null

# 1. Repository locale
Write-Host "[..] Creazione repository SVN..."
& $svnadmin create $repoDir
if ($LASTEXITCODE -ne 0) { throw "svnadmin create fallito" }
$repoUrl = To-FileUrl $repoDir

# 2. Struttura standard trunk/branches/tags
Write-Host "[..] Creazione struttura trunk/branches/tags..."
Invoke-Svn mkdir "$repoUrl/trunk" "$repoUrl/branches" "$repoUrl/tags" -m 'Struttura iniziale del repository'

# 3. Checkout di trunk
Write-Host "[..] Checkout della working copy..."
Invoke-Svn checkout "$repoUrl/trunk" $wcDir

# 4. Solution .NET demo dentro la working copy
Write-Host "[..] Creazione applicativo demo (DemoApp)..."
$appDir = Join-Path $wcDir 'DemoApp'
New-Item -ItemType Directory -Force -Path $appDir | Out-Null

@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
'@ | Set-Content -Encoding UTF8 (Join-Path $appDir 'DemoApp.csproj')

@'
// App demo per testare SVNAssist.
// Modifica questa riga e salva: il pannello SVN Changes mostrera' il file come Modified.
Console.WriteLine("Ciao da DemoApp - ambiente SVN di test per SVNAssist!");
'@ | Set-Content -Encoding UTF8 (Join-Path $appDir 'Program.cs')

@'
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "DemoApp", "DemoApp\DemoApp.csproj", "{11111111-2222-3333-4444-555555555555}"
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
		Release|Any CPU = Release|Any CPU
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{11111111-2222-3333-4444-555555555555}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{11111111-2222-3333-4444-555555555555}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{11111111-2222-3333-4444-555555555555}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{11111111-2222-3333-4444-555555555555}.Release|Any CPU.Build.0 = Release|Any CPU
	EndGlobalSection
EndGlobal
'@ | Set-Content -Encoding UTF8 (Join-Path $wcDir 'DemoApp.sln')

@'
Working copy SVN di test per SVNAssist.
Apri DemoApp.sln nell'istanza sperimentale di Visual Studio (F5 sul progetto SVNAssist).
'@ | Set-Content -Encoding UTF8 (Join-Path $wcDir 'README.txt')

# 5. Add + commit iniziale
Write-Host "[..] Add e commit iniziale..."
Invoke-Svn add (Join-Path $wcDir 'DemoApp') (Join-Path $wcDir 'DemoApp.sln') (Join-Path $wcDir 'README.txt')
Invoke-Svn commit $wcDir -m 'Aggiunta solution demo'
Invoke-Svn update $wcDir

# 6. Branch di esempio (per testare in futuro switch/merge)
Write-Host "[..] Creazione branch di esempio 'feature-demo'..."
Invoke-Svn copy "$repoUrl/trunk" "$repoUrl/branches/feature-demo" -m 'Branch di esempio'

Write-Host ''
Write-Host '============================================================' -ForegroundColor Green
Write-Host ' Ambiente SVN di test pronto!' -ForegroundColor Green
Write-Host '============================================================' -ForegroundColor Green
Write-Host " Repository : $repoDir"
Write-Host " URL        : $repoUrl"
Write-Host " Working copy: $wcDir"
Write-Host " Solution   : $(Join-Path $wcDir 'DemoApp.sln')"
Write-Host ''
Write-Host ' Prossimi passi:' -ForegroundColor Cyan
Write-Host '  1. Apri SVNAssist.sln in Visual Studio e premi F5 (istanza sperimentale).'
Write-Host "  2. Nell'istanza sperimentale apri la solution:"
Write-Host "       $(Join-Path $wcDir 'DemoApp.sln')"
Write-Host '  3. SVNAssist rilevera'' la working copy SVN. Modifica Program.cs per vedere lo stato cambiare.'
Write-Host ''
Write-Host " Per ricreare l'ambiente da zero:  ./setup-local-svn.ps1 -Force"
