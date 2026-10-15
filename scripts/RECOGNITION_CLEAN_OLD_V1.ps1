# RECOGNITION_CLEAN_OLD_V1 - removes old Recognition copies so only the current one is left.
#
#   powershell -File scripts\RECOGNITION_CLEAN_OLD_V1.ps1 -RepoRoot <project or install folder> [-KeepInstall <folder to keep>] [-DryRun]
#
# Runs automatically from BUILD_EXE.cmd, RUN_BROWSER.cmd, UPDATE_ALL and the in-browser updater. It:
#   1  closes every running RecognitionBrowser (an old one locks the exe and the profile)
#   2  retires an old per-user install in %LOCALAPPDATA%\Recognition (and its Start Menu / Desktop shortcuts and registry entries):
#      its runtime\ and packets\ (your data) are MOVED first to <RepoRoot>\.scratch\old_install_backup\<time>\, never deleted
#      (skipped when that folder is the one being kept)
#   3  deletes old extracted builds in dist\ (dist\Recognition-win-x64 and similar); dist\recognition and the zip are rebuilt each time
#   4  keeps only the newest .update_backup\ and clears .scratch\update\
# Token: RECOGNITION_CLEAN_OLD_V1_OK
# NOTE: keep this file ASCII - Windows PowerShell 5.1 reads BOM-less files as ANSI.

param(
  [Parameter(Mandatory=$true)][string]$RepoRoot,
  [string]$KeepInstall = "",
  [switch]$DryRun
)

$ErrorActionPreference = "Continue"
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path.TrimEnd('\')
if([string]::IsNullOrWhiteSpace($KeepInstall)){ $KeepInstall = $RepoRoot }
$KeepInstall = $KeepInstall.TrimEnd('\')
$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$did = 0
function Act([string]$what,[scriptblock]$act){
  $script:did++
  if($DryRun){ Write-Host ("  would: " + $what) -ForegroundColor DarkGray; return }
  Write-Host ("  " + $what)
  try { & $act } catch { Write-Host ("    could not: " + $_.Exception.Message) -ForegroundColor Yellow }
}
function Same([string]$a,[string]$b){ [string]::Equals($a.TrimEnd('\'), $b.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) }

Write-Host "Recognition: cleaning up old copies" -ForegroundColor Cyan

# 1 running browsers
foreach($p in @(Get-Process -Name "RecognitionBrowser" -ErrorAction SilentlyContinue)){
  Act ("close running RecognitionBrowser (pid " + $p.Id + ")") { $p.Kill(); [void]$p.WaitForExit(5000) }
}
if($did -gt 0 -and -not $DryRun){ Start-Sleep -Milliseconds 800 }

# 2 an old per-user install under AppData
$old = if($env:LOCALAPPDATA){ Join-Path $env:LOCALAPPDATA "Recognition" } else { "" }
if($old -and (Test-Path -LiteralPath $old) -and -not (Same $old $KeepInstall) -and -not (Same $old $RepoRoot)){
  $bk = Join-Path $RepoRoot (".scratch\old_install_backup\" + $stamp)
  foreach($d in @("runtime","packets","payload")){
    $src = Join-Path $old $d
    if(Test-Path -LiteralPath $src){
      Act ("keep your data: move " + $src + " to " + $bk) { New-Item -ItemType Directory -Force -Path $bk | Out-Null; Move-Item -LiteralPath $src -Destination (Join-Path $bk $d) -Force }
    }
  }
  $un = Join-Path $RepoRoot "installer\RECOGNITION_UNINSTALL_V1.ps1"
  if(Test-Path -LiteralPath $un){
    Act ("remove the old install at " + $old + " (program files, shortcuts, registry entries)") { & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $un -InstallDir $old | Out-Null }
  } else {
    Act ("remove the old install folder " + $old) { Remove-Item -LiteralPath $old -Recurse -Force }
  }
}

# 3 old extracted builds in dist\
$dist = Join-Path $RepoRoot "dist"
if(Test-Path -LiteralPath $dist){
  foreach($d in @(Get-ChildItem -LiteralPath $dist -Directory -ErrorAction SilentlyContinue)){
    if($d.Name -ieq "recognition"){ continue }
    Act ("delete old extracted build dist\" + $d.Name) { Remove-Item -LiteralPath $d.FullName -Recurse -Force }
  }
}

# 4 update leftovers
$bkRoot = Join-Path $RepoRoot ".update_backup"
if(Test-Path -LiteralPath $bkRoot){
  $dirs = @(Get-ChildItem -LiteralPath $bkRoot -Directory | Sort-Object Name -Descending)
  for($i = 1; $i -lt $dirs.Count; $i++){ $x = $dirs[$i]; Act ("delete older update backup " + $x.Name) { Remove-Item -LiteralPath $x.FullName -Recurse -Force } }
}
$upd = Join-Path $RepoRoot ".scratch\update"
if(Test-Path -LiteralPath $upd){
  foreach($f in @(Get-ChildItem -LiteralPath $upd -Force -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -lt (Get-Date).AddMinutes(-10) })){
    Act ("delete update leftover " + $f.Name) { Remove-Item -LiteralPath $f.FullName -Recurse -Force }
  }
}

if($did -eq 0){ Write-Host "  nothing to clean" -ForegroundColor DarkGray }
Write-Host "RECOGNITION_CLEAN_OLD_V1_OK" -ForegroundColor Green
exit 0
