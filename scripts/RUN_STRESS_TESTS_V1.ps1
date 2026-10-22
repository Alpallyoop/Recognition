# RUN_STRESS_TESTS_V1 - stress tests for Recognition (double-click buttons\tests\STRESS_TESTS.cmd).
#
#   pwsh -File scripts\RUN_STRESS_TESTS_V1.ps1 [-Rounds 3] [-Seed 20261021] [-Quick]
#
# What it does, in order, and stops at the first failure:
#   1  checks PowerShell 7 and the .NET SDK, and that every live PowerShell script parses (installer, scripts, browser\build.ps1 ...)
#   2  builds the browser (Release) - a build error is a failure
#   3  runs the browser tests -Rounds times: every run must pass with the SAME number of checks (catches flaky or order-dependent tests)
#   4  runs stress.tests: the browser's real ledger, receipts, extension packages, identity and packaging, developer checks, filter engine,
#      setup snapshots and code viewer under volume, random and hostile input, repetition and concurrency (seeded, replayable)
#   5  runs the page-script tests (shield, passkey, page tools, motion) -Rounds times under node, when node is installed
# Nothing is installed, committed or sent anywhere; the browser is not started. Scratch files go to runtime\tmp and are removed.
# A log is kept in runtime\stress_logs. Final token: RECOGNITION_STRESS_RUN_V1_OK
# NOTE: keep this file ASCII - Windows PowerShell 5.1 reads BOM-less files as ANSI.

param(
  [string]$RepoRoot = "",
  [ValidateRange(1,50)][int]$Rounds = 3,
  [int]$Seed = 20261021,
  [switch]$Quick
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if($PSVersionTable.PSVersion.Major -lt 7){ Write-Host "RECOGNITION_STRESS_RUN_V1_FAIL: needs PowerShell 7 (winget install Microsoft.PowerShell)" -ForegroundColor Red; exit 2 }
if([string]::IsNullOrWhiteSpace($RepoRoot)){ $RepoRoot = Split-Path -Parent $PSScriptRoot }
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path

$LogDir = Join-Path $RepoRoot "runtime\stress_logs"
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$Log = Join-Path $LogDir ("stress_" + (Get-Date -Format "yyyyMMdd_HHmmss") + ".log")
$script:rows = @(); $script:failed = $false

function Say([string]$m,[string]$c="Gray"){ Write-Host $m -ForegroundColor $c; Add-Content -LiteralPath $Log -Value $m }
function Row([string]$step,[string]$status,[string]$note){
  $script:rows += [pscustomobject]@{ Step=$step; Status=$status; Note=$note }
  $c = switch($status){ "PASS"{"Green"} "SKIP"{"DarkGray"} "WARN"{"Yellow"} "INFO"{"Cyan"} default{"Red"} }
  Say ("  [{0}] {1} - {2}" -f $status,$step,$note) $c
  if($status -eq "FAIL"){ $script:failed = $true }
}
function Have([string]$n){ [bool](Get-Command $n -ErrorAction SilentlyContinue) }

# runs a command, streams nothing but keeps everything in the log, returns @(exitCode, outputText)
function Run([string]$exe,[string[]]$argv){
  Add-Content -LiteralPath $Log -Value ("> " + $exe + " " + ($argv -join " "))
  $out = & $exe @argv 2>&1 | Out-String
  $code = $LASTEXITCODE
  Add-Content -LiteralPath $Log -Value $out
  return @($code, $out)
}

$started = Get-Date
Say ("RUN_STRESS_TESTS_V1 started " + (Get-Date -Format "s") + "  repo: " + $RepoRoot + "  rounds: " + $Rounds + "  seed: " + $Seed + $(if($Quick){"  (quick)"}else{""})) "White"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"; $env:DOTNET_NOLOGO = "1"

# ---- 1 preflight -------------------------------------------------------------------------------------------------------------
Say "=== 1 preflight ===" "Cyan"
if(-not (Have "dotnet")){ Row "preflight" "FAIL" ".NET SDK was not found (winget install Microsoft.DotNet.SDK.8)" }
else { Row "preflight" "PASS" ("pwsh " + $PSVersionTable.PSVersion + ", dotnet " + (& dotnet --version)) }

# ---- 1b every live PowerShell script parses ---------------------------------------------------------------------------------
# (an install script with two statements merged on one line passed every test and broke only at install time, 2026-10-21)
Say "=== 1b scripts parse ===" "Cyan"
if(-not $script:failed){
  $live = @(Get-ChildItem -LiteralPath $RepoRoot -Recurse -Filter "*.ps1" -File | Where-Object {
    $rel = $_.FullName.Substring($RepoRoot.Length).TrimStart('\','/') -replace '\\','/'
    -not ($rel -match '^(runtime|dist|tmp|installed|packets|proofs|\.scratch|\.update_backup)/' -or $rel -match '(^|/)_scratch/' -or $_.Name -like '_PATCH_*')
  })
  $broken = @()
  foreach($f in $live){ $tk = $null; $er = $null; [System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$tk, [ref]$er) | Out-Null; if($er.Count -gt 0){ $broken += ($f.FullName.Substring($RepoRoot.Length).TrimStart('\','/') + ":" + $er[0].Extent.StartLineNumber + " " + $er[0].Message) } }
  if($broken.Count -eq 0){ Row "scripts parse" "PASS" ($live.Count.ToString() + " scripts (old _PATCH_ and _scratch files are not checked)") }
  else { foreach($b in $broken){ Say ("    " + $b) "Red" }; Row "scripts parse" "FAIL" ($broken.Count.ToString() + " script(s) do not parse") }
} else { Row "scripts parse" "SKIP" "an earlier step failed" }

# ---- 2 build ----------------------------------------------------------------------------------------------------------------
Say "=== 2 build the browser ===" "Cyan"
if(-not $script:failed){
  $r = Run "dotnet" @("build", (Join-Path $RepoRoot "browser"), "-c", "Release", "-nologo")
  if($r[0] -eq 0){ Row "browser build" "PASS" "Release build succeeded" } else { Row "browser build" "FAIL" "build failed - see the log" }
} else { Row "browser build" "SKIP" "an earlier step failed" }

# ---- 3 browser tests, repeated ------------------------------------------------------------------------------------------------
Say ("=== 3 browser tests x" + $Rounds + " ===") "Cyan"
if(-not $script:failed){
  $counts = @()
  for($i = 1; $i -le $Rounds; $i++){
    $first = ($i -eq 1)
    $argv = @("run", "--project", (Join-Path $RepoRoot "browser.tests"), "-c", "Release")
    if(-not $first){ $argv += "--no-build" }
    $r = Run "dotnet" $argv
    $m = [regex]::Match($r[1], "checks passed: (\d+)\s+failed: (\d+)")
    if($r[0] -ne 0 -or -not $m.Success -or $r[1] -notmatch "SELFTEST_RECOGNITION_BROWSER_GOVERNED_ACTIONS_V1_OK"){
      $note = if($m.Success){ "run " + $i + ": " + $m.Groups[2].Value + " failed" } else { "run " + $i + ": no result line" }
      Row "browser tests" "FAIL" ($note + " - see the log"); break
    }
    $counts += [int]$m.Groups[1].Value
    Say ("    run " + $i + ": " + $m.Groups[1].Value + " checks passed") "DarkGray"
  }
  if(-not $script:failed){
    if(@($counts | Select-Object -Unique).Count -eq 1){ Row "browser tests" "PASS" ($Rounds.ToString() + " runs, " + $counts[0] + " checks each, identical") }
    else { Row "browser tests" "FAIL" ("check counts differ between runs: " + ($counts -join ", ")) }
  }
} else { Row "browser tests" "SKIP" "an earlier step failed" }

# ---- 4 stress tests -----------------------------------------------------------------------------------------------------------
Say "=== 4 stress tests ===" "Cyan"
if(-not $script:failed){
  $argv = @("run", "--project", (Join-Path $RepoRoot "stress.tests"), "-c", "Release", "--", ("rounds=" + $Rounds), ("seed=" + $Seed))
  if($Quick){ $argv += "quick" }
  $r = Run "dotnet" $argv
  $m = [regex]::Match($r[1], "stress checks passed: (\d+)\s+failed: (\d+)")
  foreach($line in @(($r[1] -split "`n") | Where-Object { $_ -match "^\s+NOTE - " } | ForEach-Object { $_.Trim() } | Select-Object -Unique)){ Row "stress note" "WARN" ($line -replace "^NOTE - ","") }
  foreach($line in ($r[1] -split "`n")){ if($line -match "^\s+FAIL- "){ Say ("    " + $line.Trim()) "Red" } }
  if($r[0] -eq 0 -and $m.Success -and $r[1] -match "RECOGNITION_STRESS_TESTS_V1_OK"){ Row "stress tests" "PASS" ($m.Groups[1].Value + " checks over " + $Rounds + " round(s)") }
  else {
    $note = if($m.Success){ $m.Groups[2].Value + " failed" } else { "did not finish" }
    Row "stress tests" "FAIL" ($note + " - replay with: dotnet run -c Release --project stress.tests -- seed=" + $Seed + " rounds=" + $Rounds + $(if($Quick){" quick"}else{""}))
  }
  $t = [regex]::Match($r[1], "(?s)== timings \(all rounds\) ==(.*?)\r?\n\r?\n")
  if($t.Success){ Say "    timings:" "DarkGray"; foreach($line in ($t.Groups[1].Value -split "`n")){ if($line.Trim().Length -gt 0){ Say ("      " + $line.Trim()) "DarkGray" } } }
} else { Row "stress tests" "SKIP" "an earlier step failed" }

# ---- 5 page-script tests, repeated --------------------------------------------------------------------------------------------
Say ("=== 5 page-script tests x" + $Rounds + " ===") "Cyan"
if($script:failed){ Row "page scripts" "SKIP" "an earlier step failed" }
elseif(-not (Have "node")){ Row "page scripts" "SKIP" "node is not installed (winget install OpenJS.NodeJS.LTS); these also run in the release gate on CI" }
else {
  $tests = @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot "browser.tests\shield") -Filter "*.test.js" | Sort-Object Name)
  $bad = @()
  for($i = 1; $i -le $Rounds; $i++){
    foreach($f in $tests){
      $r = Run "node" @($f.FullName)
      if($r[0] -ne 0 -or $r[1] -match "\b[1-9]\d* failed"){ $bad += ($f.Name + " (run " + $i + ")") }
    }
  }
  if($bad.Count -eq 0){ Row "page scripts" "PASS" ($tests.Count.ToString() + " files x " + $Rounds + " runs") } else { Row "page scripts" "FAIL" ("failed: " + ($bad -join ", ")) }
}

# ---- summary ---------------------------------------------------------------------------------------------------------------------
Say ""
Say "Summary" "White"
$table = $script:rows | Format-Table -AutoSize -Wrap | Out-String -Width 220   # explicit width: no console (CI, a log) otherwise gives an empty table
Write-Host $table; Add-Content -LiteralPath $Log -Value $table
Say ("Time: " + [int]((Get-Date) - $started).TotalSeconds + " s    Log: " + $Log) "DarkGray"
if($script:failed){ Say "RECOGNITION_STRESS_RUN_V1_FAIL" "Red"; exit 1 }
Say "RECOGNITION_STRESS_RUN_V1_OK" "Green"
exit 0
