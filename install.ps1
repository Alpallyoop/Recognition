# Recognition one-command install (Windows 10/11, per-user, no admin).
#
#   irm https://raw.githubusercontent.com/ScrappyHub/Recognition/main/install.ps1 | iex
#
# What it does: finds the latest release on GitHub, downloads Recognition-win-x64.zip over https, checks its SHA-256
# against Recognition-win-x64.zip.sha256 when the release has one (it stops if they differ), unpacks it to a temporary
# folder and runs the installer that is inside it (installer\RECOGNITION_INSTALL_V1.ps1). Nothing else is changed.
# To read it first: open the address above in a browser.
# NOTE: keep this file ASCII - Windows PowerShell 5.1 reads BOM-less files as ANSI.

$ErrorActionPreference = "Stop"
$Repo = "ScrappyHub/Recognition"
$Asset = "Recognition-win-x64.zip"
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13 } catch { }
$ProgressPreference = "SilentlyContinue"

function Fail([string]$m){ Write-Host ("RECOGNITION_INSTALL_FAIL: " + $m) -ForegroundColor Red; throw $m }

Write-Host "Recognition: looking for the latest release..." -ForegroundColor Cyan
$rel = Invoke-RestMethod -Uri ("https://api.github.com/repos/" + $Repo + "/releases/latest") -Headers @{ "User-Agent" = "recognition-installer" }
$zipInfo = @($rel.assets) | Where-Object { $_.name -eq $Asset } | Select-Object -First 1
if(-not $zipInfo){ Fail ("the latest release (" + $rel.tag_name + ") has no " + $Asset + ". Build it with BUILD_EXE.cmd from the source instead.") }
foreach($u in @($zipInfo.browser_download_url)){ if($u -notmatch '^https://github\.com/' + [regex]::Escape($Repo) + '/releases/download/'){ Fail ("unexpected download address: " + $u) } }

$work = Join-Path ([IO.Path]::GetTempPath()) ("recognition_install_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $work | Out-Null
try {
  $zip = Join-Path $work $Asset
  Write-Host ("Downloading " + $rel.tag_name + " (" + [math]::Round($zipInfo.size / 1MB, 1) + " MB)...")
  Invoke-WebRequest -Uri $zipInfo.browser_download_url -OutFile $zip -UseBasicParsing

  $shaInfo = @($rel.assets) | Where-Object { $_.name -eq ($Asset + ".sha256") } | Select-Object -First 1
  if($shaInfo){
    $want = ((Invoke-WebRequest -Uri $shaInfo.browser_download_url -UseBasicParsing).Content.ToString().Trim() -split '\s+')[0].ToLowerInvariant()
    $got = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if($want -ne $got){ Fail ("checksum mismatch (expected " + $want + ", got " + $got + "). Nothing was installed.") }
    Write-Host "Checksum OK." -ForegroundColor Green
  } else { Write-Host "No checksum file on this release; continuing." -ForegroundColor Yellow }

  $out = Join-Path $work "x"
  Expand-Archive -LiteralPath $zip -DestinationPath $out -Force
  $inst = Get-ChildItem -LiteralPath $out -Recurse -Filter "RECOGNITION_INSTALL_V1.ps1" | Select-Object -First 1
  if(-not $inst){ Fail "the download does not contain the installer" }
  $ps = (Get-Command pwsh -ErrorAction SilentlyContinue)
  $exe = if($ps){ $ps.Source } else { "powershell.exe" }
  & $exe -NoProfile -ExecutionPolicy Bypass -File $inst.FullName
  if($LASTEXITCODE -ne 0){ Fail "the installer reported an error" }
} finally {
  Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
