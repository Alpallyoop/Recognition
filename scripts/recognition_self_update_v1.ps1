# recognition_self_update_v1 - replaces an INSTALLED Recognition with a downloaded release, keeps your data, rolls back on failure.
#
# Started by the browser (Settings > Updates > Check for updates) after it downloaded Recognition-win-x64.zip and checked its SHA-256.
# It runs as a COPY of itself (the scripts folder it lives in is replaced). Works in Windows PowerShell 5.1 and PowerShell 7.
#
#   powershell -File recognition_self_update_v1.ps1 -InstallRoot <install folder> -ZipPath <zip> -ExpectedSha256 <hex> [-NewVersion 1.4.0] [-WaitPid <pid>] [-Relaunch]
#
# Order: wait for the browser to exit -> re-check the SHA-256 -> check the zip has no unsafe paths -> unpack inside <install>\.scratch ->
#   require: same pinned trust root, strictly newer version, and a valid signed build record when the install has one ->
#   move the old program files to <install>\.update_backup\<time> -> copy the new ones (never runtime\, packets\, payload\, local proofs) ->
#   check the new exe hash -> remove old backups, old copies and scratch -> reopen. Any failure puts the old files back.
# Tokens: RECOGNITION_SELF_UPDATE_V1_OK / RECOGNITION_SELF_UPDATE_V1_FAIL
# NOTE: keep this file ASCII - Windows PowerShell 5.1 reads BOM-less files as ANSI.

param(
  [Parameter(Mandatory=$true)][string]$InstallRoot,
  [Parameter(Mandatory=$true)][string]$ZipPath,
  [Parameter(Mandatory=$true)][string]$ExpectedSha256,
  [string]$NewVersion = "",
  [int]$WaitPid = 0,
  [switch]$Relaunch
)

$ErrorActionPreference = "Stop"
$InstallRoot = (Resolve-Path -LiteralPath $InstallRoot).Path.TrimEnd('\')
$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
$logDir = Join-Path $InstallRoot "runtime\update_logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir ("self_update_" + $stamp + ".log")
function Say([string]$m,[string]$c="Gray"){ Write-Host $m -ForegroundColor $c; Add-Content -LiteralPath $log -Value $m }

$keepTop = @("runtime","packets","payload","dist",".git",".scratch",".update_backup","installed")   # never replaced
$backupRoot = Join-Path $InstallRoot (".update_backup\" + $stamp)
$moved = New-Object System.Collections.ArrayList   # @{ Dst; Bak }
$added = New-Object System.Collections.ArrayList   # paths that did not exist before
$work = Join-Path $InstallRoot (".scratch\update\x_" + $stamp)
$exe = Join-Path $InstallRoot "browser\RecognitionBrowser.exe"
$applied = $false

function Sha256File([string]$p){ (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() }

function Rollback {
  Say "rolling back..." "Yellow"
  for($i = $moved.Count - 1; $i -ge 0; $i--){
    $r = $moved[$i]
    try {
      if(Test-Path -LiteralPath $r.Dst){ Remove-Item -LiteralPath $r.Dst -Recurse -Force }
      Move-Item -LiteralPath $r.Bak -Destination $r.Dst -Force
    } catch { Say ("  could not restore " + $r.Dst + ": " + $_.Exception.Message) "Red" }
  }
  foreach($a in $added){ try { if(Test-Path -LiteralPath $a){ Remove-Item -LiteralPath $a -Recurse -Force } } catch { } }
}

function PlaceItem([string]$src,[string]$dst){
  if(Test-Path -LiteralPath $dst){
    $rel = $dst.Substring($InstallRoot.Length).TrimStart('\')
    $bak = Join-Path $backupRoot $rel
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $bak) | Out-Null
    Move-Item -LiteralPath $dst -Destination $bak -Force
    [void]$moved.Add([pscustomobject]@{ Dst = $dst; Bak = $bak })
  } else { [void]$added.Add($dst) }
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dst) | Out-Null
  Copy-Item -LiteralPath $src -Destination $dst -Recurse -Force
}

try {
  Say ("Recognition self-update  " + (Get-Date -Format "s") + "  install: " + $InstallRoot) "White"
  if(Test-Path -LiteralPath (Join-Path $InstallRoot ".git")){ throw "this is a source folder, not an installed copy; use UPDATE_ALL" }
  if($ExpectedSha256 -notmatch '^[0-9a-fA-F]{64}$'){ throw "bad checksum argument" }

  # 1 wait for the browser to close
  if($WaitPid -gt 0){ $p = Get-Process -Id $WaitPid -ErrorAction SilentlyContinue; if($p){ Say "waiting for Recognition to close..."; [void]$p.WaitForExit(30000) } }
  foreach($p in @(Get-Process -Name "RecognitionBrowser" -ErrorAction SilentlyContinue)){
    $pp = ""; try { $pp = [string]$p.Path } catch { }
    if($pp -and $pp.StartsWith($InstallRoot, [StringComparison]::OrdinalIgnoreCase)){ try { $p.Kill() } catch { } }
  }
  Start-Sleep -Milliseconds 800

  # 2 the zip is what the browser checked
  if(-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)){ throw "the downloaded zip is missing" }
  if((Sha256File $ZipPath) -ne $ExpectedSha256.ToLowerInvariant()){ throw "the zip does not match its SHA-256" }
  Say "zip checksum OK" "Green"

  # 3 no unsafe paths inside the zip
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $za = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
  try {
    foreach($e in $za.Entries){
      $n = $e.FullName
      if($n.StartsWith("/") -or $n.StartsWith("\") -or $n -match '(^|[\\/])\.\.([\\/]|$)' -or $n -match '^[A-Za-z]:'){ throw ("unsafe path inside the zip: " + $n) }
    }
  } finally { $za.Dispose() }

  # 4 unpack inside the install folder's own scratch folder
  New-Item -ItemType Directory -Force -Path $work | Out-Null
  Say "unpacking..."
  Expand-Archive -LiteralPath $ZipPath -DestinationPath $work -Force
  $newExe = Get-ChildItem -LiteralPath $work -Recurse -Filter "RecognitionBrowser.exe" | Select-Object -First 1
  if(-not $newExe){ throw "the zip has no RecognitionBrowser.exe" }
  $newRoot = Split-Path -Parent $newExe.DirectoryName
  if((Split-Path -Leaf $newExe.DirectoryName) -ne "browser"){ throw "unexpected folder layout in the zip" }

  # 5 checks: trust root, newer version, signed build record
  $trustOld = Join-Path $InstallRoot "proofs\trust\allowed_signers"
  $trustNew = Join-Path $newRoot "proofs\trust\allowed_signers"
  if(Test-Path -LiteralPath $trustOld){
    if(-not (Test-Path -LiteralPath $trustNew)){ throw "the new version has no trust root" }
    if((Sha256File $trustOld) -ne (Sha256File $trustNew)){ throw "the new version changes the pinned trust root; install it by hand if you trust it" }
  }
  $verOld = [version]"0.0"; $verNew = [version]"0.0"
  try { $verOld = [version]((Get-Content -LiteralPath (Join-Path $InstallRoot "config\version.v1.json") -Raw | ConvertFrom-Json).version) } catch { }
  try { $verNew = [version]((Get-Content -LiteralPath (Join-Path $newRoot "config\version.v1.json") -Raw | ConvertFrom-Json).version) } catch { throw "the new version has no readable version number" }
  if($verNew -le $verOld){ throw ("the new version (" + $verNew + ") is not newer than the installed one (" + $verOld + ")") }
  Say ("version " + $verOld + " -> " + $verNew) "Green"

  $recNew = Join-Path $newRoot "proofs\software\software_id.json"
  $recOld = Join-Path $InstallRoot "proofs\software\software_id.json"
  if(Test-Path -LiteralPath $recNew){
    $pw = Get-Command pwsh -ErrorAction SilentlyContinue
    if(-not $pw){ throw "this release is signed, but PowerShell 7 (pwsh) is needed to check the signature: winget install Microsoft.PowerShell" }
    $o = & $pw.Source -NoProfile -ExecutionPolicy Bypass -File (Join-Path $newRoot "scripts\recognition_verify_softwareid_v1.ps1") -RepoRoot $newRoot -BinaryPath $newExe.FullName 2>&1 | Out-String
    if($o -notmatch "RECOGNITION_SOFTWAREID_OK"){ throw ("the signed build record did not verify: " + $o.Trim()) }
    Say "signed build record verified against the pinned trust root" "Green"
  } elseif(Test-Path -LiteralPath $recOld){
    throw "your install is signed but this release is not; refusing to go from signed to unsigned"
  } else { Say "this release is not signed; it was accepted on the strength of its SHA-256 from GitHub over https" "Yellow" }

  # 6 swap the files (old ones are kept in .update_backup until the end)
  Say "installing..."
  foreach($it in @(Get-ChildItem -LiteralPath $newRoot -Force)){
    if($keepTop -contains $it.Name){ continue }
    if($it.Name -eq "proofs"){
      foreach($sub in @("trust","software")){
        $s = Join-Path $it.FullName $sub
        if(Test-Path -LiteralPath $s){ PlaceItem $s (Join-Path $InstallRoot ("proofs\" + $sub)) }
      }
      continue
    }
    PlaceItem $it.FullName (Join-Path $InstallRoot $it.Name)
  }
  if(-not (Test-Path -LiteralPath $exe)){ throw "the new program is missing after copying" }
  if((Sha256File $exe) -ne (Sha256File $newExe.FullName)){ throw "the installed program differs from the one in the zip" }
  $applied = $true
  Say "files replaced and verified" "Green"

  # 7 tidy up: keep only this run's backup, drop scratch, remove other old copies
  try {
    $bk = Join-Path $InstallRoot ".update_backup"
    foreach($d in @(Get-ChildItem -LiteralPath $bk -Directory -ErrorAction SilentlyContinue)){ if($d.Name -ne $stamp){ Remove-Item -LiteralPath $d.FullName -Recurse -Force -ErrorAction SilentlyContinue } }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $ZipPath -Force -ErrorAction SilentlyContinue
    $clean = Join-Path $InstallRoot "scripts\RECOGNITION_CLEAN_OLD_V1.ps1"
    if(Test-Path -LiteralPath $clean){
      $o = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $clean -RepoRoot $InstallRoot -KeepInstall $InstallRoot 2>&1 | Out-String
      Add-Content -LiteralPath $log -Value $o
    }
  } catch { Say ("tidy-up note: " + $_.Exception.Message) "Yellow" }

  Say "RECOGNITION_SELF_UPDATE_V1_OK" "Green"
  if($Relaunch -and (Test-Path -LiteralPath $exe)){ Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) }
  Start-Sleep -Seconds 3
  exit 0
}
catch {
  Say ("UPDATE FAILED: " + $_.Exception.Message) "Red"
  Rollback
  try { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue } catch { }
  Say "Nothing was kept from the failed update; the previous version is back." "Yellow"
  Say "RECOGNITION_SELF_UPDATE_V1_FAIL" "Red"
  if($Relaunch -and (Test-Path -LiteralPath $exe)){ Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) }
  Write-Host ("Log: " + $log)
  Start-Sleep -Seconds 15
  exit 1
}
