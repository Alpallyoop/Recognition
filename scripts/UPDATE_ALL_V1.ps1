# UPDATE_ALL_V1 - one command to bring Recognition and what it is connected to up to date.
#
#   pwsh -File scripts\UPDATE_ALL_V1.ps1            (from the source checkout; or double-click UPDATE_ALL.cmd)
#
# What it does, in order, and stops at the first failure (nothing is installed unless everything before it passed):
#   1  checks pwsh 7, git and the .NET SDK
#   2  pulls the source repositories that have a remote (Recognition, SoteriaVault, Constellation) - fast-forward only,
#      never on a repository with uncommitted changes, never a forced update
#   3  asks Constellation to validate the ecosystem map (read only)
#   4  reports .NET and WebView2 versions; with -IncludeSystem asks winget to update the WebView2 runtime
#   5  runs the browser tests, the release gate and the packager
#   6  closes the running browser (only with -CloseApp), installs, and reopens it (only with -Relaunch)
# It never pushes, never commits, and never changes a repository you did not already have. A log is kept in runtime\update_logs.
# NOTE: keep this file ASCII - Windows PowerShell 5.1 reads BOM-less files as ANSI.

param(
  [string]$RepoRoot = "",
  [string]$DevRoot = "",
  [switch]$CloseApp,
  [switch]$Relaunch,
  [switch]$SkipInstall,
  [switch]$IncludeSystem
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if($PSVersionTable.PSVersion.Major -lt 7){ Write-Host "UPDATE_ALL_V1_FAIL: needs PowerShell 7 (winget install Microsoft.PowerShell)" -ForegroundColor Red; exit 2 }

if([string]::IsNullOrWhiteSpace($RepoRoot)){ $RepoRoot = Split-Path -Parent $PSScriptRoot }
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
if([string]::IsNullOrWhiteSpace($DevRoot)){ $DevRoot = Split-Path -Parent $RepoRoot }
$S = Join-Path $RepoRoot "scripts"
$LogDir = Join-Path $RepoRoot "runtime\update_logs"
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$Log = Join-Path $LogDir ("update_" + (Get-Date -Format "yyyyMMdd_HHmmss") + ".log")
$InstallDir = Join-Path $env:LOCALAPPDATA "Recognition"

$script:rows = @()
$script:failed = $false
function Say([string]$m,[string]$c="Gray"){ Write-Host $m -ForegroundColor $c; Add-Content -LiteralPath $Log -Value $m }
function Row([string]$step,[string]$status,[string]$note){
  $script:rows += [pscustomobject]@{ Step=$step; Status=$status; Note=$note }
  $c = switch($status){ "PASS"{"Green"} "SKIP"{"DarkGray"} "WARN"{"Yellow"} "INFO"{"Cyan"} default{"Red"} }
  Say ("  [{0}] {1} - {2}" -f $status,$step,$note) $c
  if($status -eq "FAIL"){ $script:failed = $true }
}
function Have([string]$n){ [bool](Get-Command $n -ErrorAction SilentlyContinue) }

# run a child script, keep everything in the log, and require its success token in the output
function RunStep([string]$name,[string]$file,[string[]]$args2,[string]$token){
  if($script:failed){ Row $name "SKIP" "an earlier step failed"; return }
  Say ("=== " + $name + " ===") "Cyan"
  $out = & pwsh -NoProfile -ExecutionPolicy Bypass -File $file @args2 *>&1 | ForEach-Object { "$_" }
  Add-Content -LiteralPath $Log -Value $out
  if(($out -join "`n") -match [regex]::Escape($token)){ Row $name "PASS" $token }
  else {
    Row $name "FAIL" ("token not found: " + $token)
    ($out | Select-Object -Last 12) | ForEach-Object { Write-Host ("      " + $_) -ForegroundColor DarkGray }
  }
}

Say ("UPDATE_ALL_V1 started " + (Get-Date -Format "s") + "  repo: " + $RepoRoot) "White"

# ---- 1 preflight -----------------------------------------------------------------------------------------------------------
Say "=== 1 preflight ===" "Cyan"
if(-not (Test-Path -LiteralPath (Join-Path $RepoRoot ".git"))){ Row "preflight" "FAIL" "this is not a source checkout (no .git). An installed copy updates with 'Apply an update package' in Settings."; }
elseif(-not (Have "git")){ Row "preflight" "FAIL" "git was not found" }
elseif(-not (Have "dotnet")){ Row "preflight" "FAIL" ".NET SDK was not found (winget install Microsoft.DotNet.SDK.8)" }
else {
  if([string]::IsNullOrEmpty($env:RECOGNITION_PASSPHRASE)){
    $sec = Read-Host "Recognition passphrase (used for this run only, never saved)" -AsSecureString
    $env:RECOGNITION_PASSPHRASE = [System.Net.NetworkCredential]::new("", $sec).Password
  }
  if([string]::IsNullOrEmpty($env:RECOGNITION_PASSPHRASE)){ Row "preflight" "FAIL" "no passphrase given" }
  else { Row "preflight" "PASS" ("pwsh " + $PSVersionTable.PSVersion + ", git, dotnet " + (& dotnet --version)) }
}

# ---- 2 pull ------------------------------------------------------------------------------------------------------------------
function Pull([string]$label,[string]$path){
  if($script:failed){ Row ("update " + $label) "SKIP" "an earlier step failed"; return }
  if(-not (Test-Path -LiteralPath (Join-Path $path ".git"))){ Row ("update " + $label) "SKIP" "not found at $path"; return }
  $remote = (& git -C $path remote 2>$null) -join ""
  if([string]::IsNullOrWhiteSpace($remote)){ Row ("update " + $label) "SKIP" "no remote configured (local only)"; return }
  $dirty = (& git -C $path status --porcelain 2>$null) -join ""
  if($dirty){ Row ("update " + $label) "WARN" "has uncommitted changes - left alone (commit or stash, then run again)"; return }
  & git -C $path fetch --quiet 2>&1 | Out-Null
  if($LASTEXITCODE -ne 0){ Row ("update " + $label) "WARN" "could not reach the remote (offline?) - using what is on disk"; return }
  $behind = [int]((& git -C $path rev-list --count "HEAD..@{u}" 2>$null) -join "")
  $ahead  = [int]((& git -C $path rev-list --count "@{u}..HEAD" 2>$null) -join "")
  if($behind -eq 0){ Row ("update " + $label) "PASS" ("already current" + $(if($ahead -gt 0){ " ($ahead local commit(s) not pushed)" } else { "" })); return }
  if($ahead -gt 0){ Row ("update " + $label) "WARN" "local and remote have both moved - merge it yourself"; return }
  & git -C $path merge --ff-only --quiet "@{u}" 2>&1 | Out-Null
  if($LASTEXITCODE -eq 0){ Row ("update " + $label) "PASS" ("pulled $behind new commit(s)") } else { Row ("update " + $label) "WARN" "fast-forward failed - left as it was" }
}
if(-not $script:failed){ Say "=== 2 update the source repositories ===" "Cyan" }
Pull "Recognition" $RepoRoot
Pull "SoteriaVault (privacy-sector)" (Join-Path $DevRoot "privacy-sector")
Pull "Constellation" (Join-Path $DevRoot "Constellation")

# ---- 3 ecosystem check (read only) -------------------------------------------------------------------------------------------
if(-not $script:failed){
  Say "=== 3 ecosystem check ===" "Cyan"
  $con = Join-Path $DevRoot "Constellation\constellation.ps1"
  if(Test-Path -LiteralPath $con){
    $o = & pwsh -NoProfile -ExecutionPolicy Bypass -File $con validate *>&1 | ForEach-Object { "$_" }
    Add-Content -LiteralPath $Log -Value $o
    if($LASTEXITCODE -eq 0){ Row "ecosystem map (Constellation validate)" "PASS" "valid" } else { Row "ecosystem map (Constellation validate)" "WARN" "reported problems - see the log" }
  } else { Row "ecosystem map" "SKIP" "Constellation not found" }
}

# ---- 4 platform parts --------------------------------------------------------------------------------------------------------
if(-not $script:failed){
  Say "=== 4 platform ===" "Cyan"
  $wv = $null
  foreach($k in @("HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}","HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}")){
    try { $wv = (Get-ItemProperty -LiteralPath $k -ErrorAction Stop).pv; if($wv){ break } } catch { }
  }
  Row "WebView2 runtime" "INFO" ($(if($wv){ "version $wv" } else { "version not found" }) + " (Windows keeps it current)")
  if($IncludeSystem){
    if(Have "winget"){
      $o = & winget upgrade --id Microsoft.EdgeWebView2Runtime --silent --accept-source-agreements --accept-package-agreements 2>&1 | ForEach-Object { "$_" }
      Add-Content -LiteralPath $Log -Value $o
      Row "WebView2 runtime (winget)" "INFO" (($o | Select-Object -Last 1) -as [string])
    } else { Row "WebView2 runtime (winget)" "SKIP" "winget not found" }
  } else { Row "system packages" "SKIP" "pass -IncludeSystem to ask winget to update the WebView2 runtime" }
}

# ---- 5 prove it --------------------------------------------------------------------------------------------------------------
if(-not $script:failed){
  Say "=== 5 tests, gate, package ===" "Cyan"
  Push-Location $RepoRoot
  try {
    Say "--- browser tests ---" "Cyan"
    $o = & dotnet run --project (Join-Path $RepoRoot "browser.tests") -c Release *>&1 | ForEach-Object { "$_" }
    Add-Content -LiteralPath $Log -Value $o
    if(($o -join "`n") -match "SELFTEST_RECOGNITION_BROWSER_GOVERNED_ACTIONS_V1_OK"){ Row "browser tests" "PASS" (($o | Where-Object { $_ -match "checks passed" } | Select-Object -Last 1) -as [string]) }
    else { Row "browser tests" "FAIL" "not green"; ($o | Select-Object -Last 12) | ForEach-Object { Write-Host ("      " + $_) -ForegroundColor DarkGray } }
  } finally { Pop-Location }
}
RunStep "release gate" (Join-Path $S "RUN_RELEASE_GATE_V1.ps1") @("-RepoRoot",$RepoRoot) "RECOGNITION_RELEASE_GATE_V1_OK"
RunStep "package" (Join-Path $S "RUN_PACKAGE_DIST_V1.ps1") @("-RepoRoot",$RepoRoot) "RECOGNITION_PACKAGE_DIST_V1_OK"

# ---- 6 install ---------------------------------------------------------------------------------------------------------------
if($script:failed){ Row "install" "SKIP" "an earlier step failed - the installed copy was not touched" }
elseif($SkipInstall){ Row "install" "SKIP" "-SkipInstall" }
else {
  Say "=== 6 install ===" "Cyan"
  $procs = @(Get-Process -Name "RecognitionBrowser" -ErrorAction SilentlyContinue)
  if($procs.Count -gt 0){
    if(-not $CloseApp){ Row "install" "FAIL" "Recognition is running. Close it, or run again with -CloseApp." }
    else {
      foreach($p in $procs){ try { [void]$p.CloseMainWindow() } catch { } }
      $deadline = (Get-Date).AddSeconds(20)
      while((Get-Date) -lt $deadline -and @(Get-Process -Name "RecognitionBrowser" -ErrorAction SilentlyContinue).Count -gt 0){ Start-Sleep -Milliseconds 400 }
      if(@(Get-Process -Name "RecognitionBrowser" -ErrorAction SilentlyContinue).Count -gt 0){ Row "install" "FAIL" "Recognition did not close in 20 seconds (it is never force-killed). Close it and run again." }
    }
  }
  if(-not $script:failed){
    RunStep "install" (Join-Path $RepoRoot "dist\recognition\installer\RECOGNITION_INSTALL_V1.ps1") @() "RECOGNITION_INSTALL_V1_OK"
    if(-not $script:failed){
      try {   # lets the installed app's Settings button find this checkout
        New-Item -ItemType Directory -Force -Path (Join-Path $InstallDir "runtime") | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $InstallDir "runtime\source_root.txt"), $RepoRoot, (New-Object System.Text.UTF8Encoding($false)))
      } catch { }
      if($Relaunch){
        $exe = Join-Path $InstallDir "browser\RecognitionBrowser.exe"
        if(Test-Path -LiteralPath $exe){ Start-Process -FilePath $exe; Row "reopen" "PASS" "Recognition started" } else { Row "reopen" "WARN" "installed exe not found" }
      }
    }
  }
}

# ---- what is checked inside the app, not here --------------------------------------------------------------------------------
Row "SoteriaVault link" "INFO" "re-reads the SoteriaVault contract each time the page opens (Menu: SoteriaVault link)"
Row "filter lists" "INFO" "update from Shield, Filter lists (they never download without your click)"
Row "extensions" "INFO" "updated from Extensions (each change needs your approval and a governance decision)"

Say "" "Gray"
Say "Summary" "White"
$script:rows | Format-Table -AutoSize | Out-String | ForEach-Object { Say $_ "Gray" }
Say ("Log: " + $Log) "DarkGray"
if($script:failed){ Say "UPDATE_ALL_V1_FAIL" "Red"; exit 1 }
Say "UPDATE_ALL_V1_OK" "Green"
exit 0
