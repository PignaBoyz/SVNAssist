param([int]$KillPid = 0, [int]$WaitSec = 90)
$ErrorActionPreference = 'Continue'
$s = 'C:\Users\andre\AppData\Local\Temp\claude\76b8ab5b50f502c1\955d1390-43aa-4345-b25b-5daa846b3c11\scratchpad'
$proj = 'C:\dev\SVNAssist\.claude\worktrees\agent-a9fcfe80f864b8c83\spike\WebView2Host'
$ide = 'C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE'
if ($KillPid) { Stop-Process -Id $KillPid -Force -ErrorAction SilentlyContinue; Start-Sleep 3 }
Set-Location $proj
$env:MSBUILDDISABLENODEREUSE = 1
& "$s\msb.exe" /v:m /nologo WebView2Host.csproj 2>&1 | ?{ $_ -match 'error' } | %{ $_ -replace '\[C:.*','' }
$id = 'SVNAssist.WebView2Spike.6d1f5c0e-6c0a-4a43-9b0e-5a1d0a7b2e11'
(Start-Process "$ide\VSIXInstaller.exe" -ArgumentList '/quiet','/rootSuffix:Exp',"/uninstall:$id" -PassThru -Wait).ExitCode | Out-Null
$v = (Resolve-Path bin\Debug\net472\SVNAssist.Spike.WebView2Host.vsix).Path
"install exit: " + (Start-Process "$ide\VSIXInstaller.exe" -ArgumentList '/quiet','/rootSuffix:Exp',"`"$v`"" -PassThru -Wait).ExitCode
(Start-Process "$ide\devenv.exe" -ArgumentList '/rootsuffix','Exp','/updateconfiguration' -PassThru -Wait -NoNewWindow).ExitCode | Out-Null
$log = "$env:LOCALAPPDATA\SVNAssist\spike-log.txt"
Remove-Item $log -ErrorAction SilentlyContinue
$env:SVNASSIST_SPIKE_AUTO = '1'
$p = Start-Process "$ide\devenv.exe" -ArgumentList '/rootsuffix','Exp','/log' -PassThru
"devenv pid " + $p.Id
for ($k = 0; $k -lt ($WaitSec / 5); $k++) {
  Start-Sleep 5
  if ((Test-Path $log) -and ((Get-Content $log -Raw) -match 'ROUNDTRIP|ERRORE')) { break }
}
Get-Content $log -ErrorAction SilentlyContinue
