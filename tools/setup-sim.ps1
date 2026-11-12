<#
    Simulazione realistica per il collaudo 0.3.0 (Switch, Merge, conflitti, undo righe, diff, storico).
    Crea in $Root:
      repo\                 repository file:// con trunk/branches/tags, storico con piu' autori
      wc\                   working copy di trunk con LAVORO IN CORSO (modifiche in piu' punti, A, ?, D) e NON alla HEAD
      wc-merge\             working copy di trunk PULITA, per Switch e Merge
    Branch: feature-coupon (3 revisioni da unire: una pulita, una in conflitto di testo, una in tree conflict),
            hotfix-login (1 revisione, cherry-pick), 150 branch feature-<area>-NNN, 20 tag v1.N.0.
    Uso (PowerShell):  .\tools\setup-sim.ps1 [-Root C:\percorso] [-Force]
#>
param(
    [string]$Root = (Join-Path $env:TEMP 'svnsim'),
    [switch]$Force
)
$ErrorActionPreference = 'Stop'

function Svn { & svn.exe @args --non-interactive; if ($LASTEXITCODE) { throw "svn $args fallito" } }
function As($user) { $script:U = $user }
function Ci($msg, $path) { Svn commit -q --username $script:U -m $msg $path }
function W($path, [string[]]$lines) { $d = Split-Path $path; if (!(Test-Path $d)) { New-Item -ItemType Directory -Force $d | Out-Null }; [IO.File]::WriteAllLines($path, $lines) }
function Edit($path, [scriptblock]$change) { $l = [Collections.Generic.List[string]](Get-Content $path); & $change $l; [IO.File]::WriteAllLines($path, $l) }

if (Test-Path $Root) {
    if (!$Force) { throw "$Root esiste gia': usa -Force per ricrearlo." }
    Remove-Item -Recurse -Force $Root
}
New-Item -ItemType Directory -Force $Root | Out-Null
$repoDir = Join-Path $Root 'repo'; & svnadmin create $repoDir
$repo = 'file:///' + ($repoDir -replace '\\', '/')
# file:// accetta qualsiasi autore: serve l'hook pre-revprop-change solo per cambiare autori dopo, qui non serve.
Svn mkdir -q --username admin "$repo/trunk" "$repo/branches" "$repo/tags" -m 'Struttura iniziale trunk/branches/tags'

# ---- trunk: progetto con cartelle e storico ----
$wc = Join-Path $Root 'wc'; Svn checkout -q "$repo/trunk" $wc
As 'mrossi'
W "$wc\Shop.sln" @(
    'Microsoft Visual Studio Solution File, Format Version 12.00', '# Visual Studio Version 17',
    'Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Shop", "src\Shop\Shop.csproj", "{22222222-3333-4444-5555-666666666666}"',
    'EndProject', 'Global', 'EndGlobal')
W "$wc\src\Shop\Shop.csproj" @('<Project Sdk="Microsoft.NET.Sdk">', '  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>', '</Project>')
W "$wc\README.txt" @('Shop - progetto di prova SVNAssist', 'Versione: 1.0', '', 'Istruzioni: compila e avvia.')
W "$wc\src\Shop\Core\Order.cs" @('namespace Shop.Core;', '', 'public class Order', '{', '    public decimal Total { get; set; }', '', '    public decimal Discount() => 0m;', '}')
W "$wc\src\Shop\Core\Customer.cs" @('namespace Shop.Core;', '', 'public record Customer(string Name, string Email);')
W "$wc\src\Shop\UI\MainWindow.cs" @('namespace Shop.UI;', '', 'public class MainWindow', '{', '    public string Title => "Shop";', '}')
W "$wc\.github\copilot-instructions.md" @('# Regole del progetto', '', 'Messaggi di commit in formato Conventional Commits: `tipo(ambito): descrizione` (feat, fix, refactor, docs, chore), poi elenco puntato.')
W "$wc\docs\old-setup.md" @('# Vecchio setup', '', 'Passi obsoleti.')
W "$wc\docs\old-faq.md" @('# Vecchie FAQ', '', 'Domande obsolete.')
$big = @('namespace Shop.Core;', '', '// File lungo per provare diff, Alt+Giu'' e undo delle singole righe', 'public static class Catalog', '{')
for ($i = 1; $i -le 400; $i++) { $big += "    public const string Item$($i.ToString('000')) = ""Articolo $i"";" }
$big += '}'
W "$wc\src\Shop\Core\Catalog.cs" $big
Svn add -q "$wc\Shop.sln" "$wc\README.txt" "$wc\src" "$wc\docs" "$wc\.github"
Ci 'Prima versione del negozio' $wc

As 'lbianchi'; Edit "$wc\src\Shop\Core\Order.cs" { param($l) $l.Insert(5, '    public string Code { get; set; } = "";') }; Ci 'Order: aggiunto il codice ordine' $wc
As 'gverdi';   Edit "$wc\src\Shop\UI\MainWindow.cs" { param($l) $l[4] = '    public string Title => "Shop 1.0";' }; Ci 'UI: titolo con versione' $wc
As 'mrossi';   W "$wc\src\Shop\Core\Invoice.cs" @('namespace Shop.Core;', '', 'public class Invoice { }'); Svn add -q "$wc\src\Shop\Core\Invoice.cs"; Ci 'Core: classe Invoice' $wc

# ---- branch feature-coupon e hotfix-login ----
Svn copy -q --username mrossi "$repo/trunk" "$repo/branches/feature-coupon" -m 'Branch per i coupon'
Svn copy -q --username lbianchi "$repo/trunk" "$repo/branches/hotfix-login" -m 'Branch hotfix login'
$bw = Join-Path $Root '_branch'; Svn checkout -q "$repo/branches/feature-coupon" $bw
As 'mrossi'
W "$bw\src\Shop\Core\Coupon.cs" @('namespace Shop.Core;', '', 'public record Coupon(string Code, decimal Percent);'); Svn add -q "$bw\src\Shop\Core\Coupon.cs"
Edit "$bw\src\Shop\Core\Order.cs" { param($l) $l[$l.IndexOf('    public decimal Discount() => 0m;')] = '    public decimal Discount(Coupon? c = null) => c is null ? 0m : Total * c.Percent / 100m;' }
Ci 'Coupon: sconto percentuale sull''ordine' $bw
As 'lbianchi'; Edit "$bw\README.txt" { param($l) $l[1] = 'Versione: 1.1-coupon' }; Ci 'README: versione coupon (andra'' in conflitto)' $bw
As 'mrossi';   Svn delete -q "$bw\docs\old-setup.md" "$bw\docs\old-faq.md"; Ci 'Rimossa documentazione obsoleta (tree conflict)' $bw
Remove-Item -Recurse -Force $bw
$hw = Join-Path $Root '_hotfix'; Svn checkout -q "$repo/branches/hotfix-login" $hw
As 'gverdi'; Edit "$hw\src\Shop\Core\Customer.cs" { param($l) $l[2] = 'public record Customer(string Name, string Email, bool Locked = false);' }; Ci 'Login: utente bloccabile' $hw
Remove-Item -Recurse -Force $hw

# ---- trunk prosegue: conflitti con il branch ----
Svn update -q $wc
As 'gverdi'; Edit "$wc\README.txt" { param($l) $l[1] = 'Versione: 1.0.1' }; Ci 'README: versione 1.0.1' $wc
As 'lbianchi'; Edit "$wc\docs\old-setup.md" { param($l) $l.Add('Aggiunta nota.') }; Edit "$wc\docs\old-faq.md" { param($l) $l.Add('Nuova FAQ.') }; Ci 'Docs: nota sul setup' $wc

# ---- molti branch e tag ----
Write-Host '[..] Creazione di 150 branch e 20 tag (qualche minuto)...'
$areas = 'checkout', 'login', 'report', 'export', 'api', 'ui', 'perf', 'fix', 'release', 'search'
for ($i = 1; $i -le 150; $i++) { Svn copy -q --username ('dev' + ($i % 7)) "$repo/trunk" "$repo/branches/feature-$($areas[$i % 10])-$($i.ToString('000'))" -m "Branch $i" }
for ($i = 1; $i -le 20; $i++) { Svn copy -q --username release "$repo/trunk" "$repo/tags/v1.$i.0" -m "Tag v1.$i.0" }

# ---- un collega committa dopo: wc NON e' piu' alla HEAD (indicatore arancio) ----
Svn update -q $wc
$cw = Join-Path $Root '_collega'; Svn checkout -q "$repo/trunk" $cw
As 'collega'; Edit "$cw\src\Shop\UI\MainWindow.cs" { param($l) $l.Insert(5, '    public int Width => 1024;') }; Ci 'UI: larghezza finestra (commit di un collega)' $cw
Remove-Item -Recurse -Force $cw

# ---- wc-merge: pulita, alla HEAD ----
$wm = Join-Path $Root 'wc-merge'; Svn checkout -q "$repo/trunk" $wm

# ---- wc: lavoro in corso ----
Edit "$wc\src\Shop\Core\Catalog.cs" { param($l)
    $l[20] = '    public const string Item016 = "Articolo 16 (modificato)";'
    $l.Insert(120, '    public const string Nuovo1 = "Riga aggiunta A";'); $l.Insert(121, '    public const string Nuovo2 = "Riga aggiunta B";')
    $l.RemoveAt(300)
    $l[380] = '    public const string Item375 = "Articolo 375 (modificato)";' }
Edit "$wc\src\Shop\Core\Order.cs" { param($l) $l.Add('// TODO: arrotondamento') }
W "$wc\src\Shop\Core\Payment.cs" @('namespace Shop.Core;', '', 'public class Payment { }'); Svn add -q "$wc\src\Shop\Core\Payment.cs"
W "$wc\src\Shop\Core\Note.txt" @('appunti non versionati')
Svn delete -q "$wc\src\Shop\Core\Invoice.cs"

Write-Host ''
Write-Host "Pronto. Repository: $repo"
Write-Host "  wc       (lavoro in corso, non alla HEAD): $wc\Shop.sln"
Write-Host "  wc-merge (pulita, per Switch e Merge):     $wm\Shop.sln"
