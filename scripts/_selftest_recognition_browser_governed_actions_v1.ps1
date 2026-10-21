# (Also executes the real AppearanceSettings class: validated colours / allowlisted fonts only.)
# Selftest — executes the browser's REAL GovernedActions C# class (browser.tests project; the same
# browser\GovernedActions.cs source the browser compiles) against golden + negative vectors.
# This closes the "PowerShell mirror only" gap for the governance ledger used by action receipts,
# cookies, site policy and certificate trust. Requires the .NET 8 SDK (same as the browser build).
# Token: SELFTEST_RECOGNITION_BROWSER_GOVERNED_ACTIONS_V1_OK

param([string]$RepoRoot = "")

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if([string]::IsNullOrWhiteSpace($RepoRoot)){ $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path }
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if(-not $dotnet){ Write-Host "BROWSER_TESTS_FAIL: .NET SDK not found (needed to execute the C# governance tests)" -ForegroundColor Red; exit 1 }

$proj = Join-Path (Join-Path $RepoRoot "browser.tests") "Recognition.Browser.Tests.csproj"
if(-not (Test-Path -LiteralPath $proj -PathType Leaf)){ Write-Host ("BROWSER_TESTS_FAIL: missing " + $proj) -ForegroundColor Red; exit 1 }

# Static source invariants for the password vault + setup snapshot (the parts the C# tests cannot execute: WPF glue).
$bdir = Join-Path $RepoRoot "browser"
$pwGlue = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.Passwords.cs") -Raw
$setupGlue = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.Setup.cs") -Raw
$setupCore = Get-Content -LiteralPath (Join-Path $bdir "SetupSnapshot.cs") -Raw
$bad = @()
foreach($m in [regex]::Matches($pwGlue, '_actions\?\.Append\([^;]*;')){ if($m.Value -match '(?i)\.Password|\bpw\b|secret'){ $bad += ("receipt carries a secret: " + $m.Value) } }
foreach($m in [regex]::Matches($setupGlue, '_actions\?\.Append\([^;]*;')){ if($m.Value -match '(?i)\bcode\b|\bblob\b|\bpayload\b'){ $bad += ("setup receipt carries code/blob: " + $m.Value) } }
if($setupGlue -match '_vault|PasswordVault|VaultEntry' -or $setupCore -match '_vault|PasswordVault|VaultEntry'){ $bad += "setup snapshot code must never reference the password vault" }
if($pwGlue -notmatch 'PasswordRules\.MayFill'){ $bad += "Fill must be gated by PasswordRules.MayFill" }
if(([regex]::Matches($pwGlue, 'MayFill')).Count -lt 2){ $bad += "Fill must re-check the origin at injection time" }
if($pwGlue -match 'NavigationCompleted|DOMContentLoaded|WebResourceRequested'){ $bad += "vault must not auto-fill on page events" }
# Local-only invariant: the privacy/tools/viewer/vault/setup modules must not open sockets, spawn processes or load code.
foreach($f in @("PdfTools.cs","ImageMath.cs","CodeHighlighter.cs","PrivacyRules.cs","PasswordVault.cs","SetupSnapshot.cs","MainWindow.Tools.cs","MainWindow.Passwords.cs","MainWindow.Setup.cs")){
  $p = Join-Path $bdir $f
  if(-not (Test-Path -LiteralPath $p -PathType Leaf)){ $bad += ("missing source file: " + $f); continue }
  $src = Get-Content -LiteralPath $p -Raw
  if($src -match 'HttpClient|WebClient|WebRequest|TcpClient|UdpClient|Process\.Start|ProcessStartInfo|Assembly\.Load|Activator\.Create'){ $bad += ($f + " must stay local-only (no network clients, process launch or dynamic code loading)") }
}
$toolsGlue = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.Tools.cs") -Raw
if(([regex]::Matches($toolsGlue, 'would overwrite the original')).Count -lt 2){ $bad += "PDF and image tools must refuse to overwrite the original file" }
foreach($m in [regex]::Matches($toolsGlue, '_actions\?\.Append\([^;]*;')){ if($m.Value -match '(?i)\btext\b|\bhtml\b|\bdata\b\s*\)'){ $bad += ("tools receipt must carry names/hashes only: " + $m.Value) } }
$navGlue = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.xaml.cs") -Raw
$iGate = $navGlue.IndexOf('MessageGate.Classify(tab.IsInternal')
$iOpen = $navGlue.IndexOf('msg.StartsWith("open:")')
$iSite = $navGlue.IndexOf('msg.StartsWith("site-perm:")')
if($iGate -lt 0){ $bad += "OnWebMessage must gate every message with MessageGate.Classify(tab.IsInternal, source, msg) (web pages can call window.chrome.webview.postMessage)" }
elseif(($iOpen -gt 0 -and $iGate -gt $iOpen) -or ($iSite -gt 0 -and $iGate -gt $iSite)){ $bad += "the MessageGate check must run BEFORE any command handler" }
if($navGlue -notmatch 'DownloadRules\.Assess'){ $bad += "downloads must be assessed with DownloadRules.Assess" }
if($navGlue -notmatch 'isTrusted'){ $bad += "keyboard shortcut script must ignore synthetic (untrusted) events" }
if($navGlue -notmatch 'PrivacyRules\.HttpsUpgrade'){ $bad += "navigation must use PrivacyRules.HttpsUpgrade (parsed-host loopback check)" }
if($navGlue -match 'StartsWith\("http://localhost"|StartsWith\("http://127\.0\.0\.1"'){ $bad += "regression: prefix-based localhost exemption (http://localhost.evil.com bypass) is back" }
# Shield + filter engine invariants.
$fe = Get-Content -LiteralPath (Join-Path $bdir "FilterEngine.cs") -Raw
$fg = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.Filters.cs") -Raw
$sj = Get-Content -LiteralPath (Join-Path $bdir "shield\fingerprint_shield.js") -Raw
# SoteriaVault link: optional, read-only, no secrets, no processes, no network (docs\proposals\SOTERIA_INTEGRATION_V1.md).
$sb = Get-Content -LiteralPath (Join-Path $bdir "SoteriaBridge.cs") -Raw
$sw = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.Soteria.cs") -Raw
if($sb -match 'HttpClient|WebClient|WebRequest|TcpClient|UdpClient|Process\.Start|ProcessStartInfo|Assembly\.Load|Activator\.Create|System\.Text\.RegularExpressions|File\.Write|File\.Delete|Directory\.Delete'){ $bad += "SoteriaBridge.cs must stay pure: no network, processes, writes, deletes or regex" }
if($sw -match 'HttpClient|WebClient|WebRequest|Process\.Start|ProcessStartInfo|File\.Write|File\.Delete|Directory\.Delete|File\.Copy|File\.Move|PasswordVault|Decrypt|Unprotect'){ $bad += "MainWindow.Soteria.cs may only read the two contract files: no processes, writes, network or secret handling" }
# Everyday features: pure rules stay pure; settings messages only from internal pages; no web requests from the feature code.
$bfe = Get-Content -LiteralPath (Join-Path $bdir "BrowserFeel.cs") -Raw
$mfe = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.Feel.cs") -Raw
$mwf = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.xaml.cs") -Raw
if($bfe -match 'HttpClient|WebClient|WebRequest|Process\.Start|File\.|Directory\.'){ $bad += "BrowserFeel.cs must stay pure: no network, processes or files" }
if($mfe -match 'HttpClient|WebClient|WebRequest|Process\.Start|ProcessStartInfo'){ $bad += "MainWindow.Feel.cs must not make web requests or start processes" }
if($mwf -notmatch 'StartsWith\("feel-"\) && \(tab\.Internal == "settings" \|\| tab\.Internal == "bookmarks"( \|\| tab\.Internal == "customize")?\)'){ $bad += "feel- settings messages must only be accepted from the settings and bookmarks pages" }
if($bfe -notmatch 'LaunchArgs\.Parse'){ $bad += "bookmark import must validate addresses with LaunchArgs.Parse" }
# Update everything: fixed command line, validated source folder, user click only, script never pushes or force-updates.
$uaw = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.UpdateAll.cs") -Raw
$uas = Get-Content -LiteralPath (Join-Path $RepoRoot "scripts\UPDATE_ALL_V1.ps1") -Raw
if($uaw -notmatch 'MessageBox\.Show'){ $bad += "Update everything must ask before it runs" }
if($uaw -match 'msg|Dec\('){ $bad += "MainWindow.UpdateAll.cs must not use text that came from a page" }
if($uas -match 'git[^\r\n]*(push|--force|reset --hard|clean -)'){ $bad += "UPDATE_ALL_V1.ps1 must never push, force, reset or clean" }
if($uas -notmatch '--ff-only'){ $bad += "UPDATE_ALL_V1.ps1 must only fast-forward" }
if($uas -match '[^\x00-\x7F]'){ $bad += "UPDATE_ALL_V1.ps1 must stay ASCII (Windows PowerShell 5.1 reads BOM-less files as ANSI)" }
# Update from GitHub: user click only, GitHub hosts only, checksum required, helper keeps data and rolls back; scripts stay ASCII.
$sus = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.SelfUpdate.cs") -Raw
$sur = Get-Content -LiteralPath (Join-Path $bdir "SelfUpdate.cs") -Raw
$suh = Get-Content -LiteralPath (Join-Path $RepoRoot "scripts\recognition_self_update_v1.ps1") -Raw
$suc = Get-Content -LiteralPath (Join-Path $RepoRoot "scripts\RECOGNITION_CLEAN_OLD_V1.ps1") -Raw
if($sus -notmatch 'MessageBox\.Show' -or $sus -notmatch 'IsAllowedHost' -or $sus -notmatch 'got != want'){ $bad += "the GitHub update must ask first, accept GitHub hosts only and require a matching SHA-256" }
if($sus -match 'msg|Dec\('){ $bad += "MainWindow.SelfUpdate.cs must not use text that came from a page" }
if($sur -match 'HttpClient|WebClient|Process\.Start|File\.|Directory\.'){ $bad += "SelfUpdate.cs must stay pure: no network, processes or files" }
if($suh -notmatch 'Rollback' -or $suh -notmatch 'trust root' -or $suh -notmatch 'not newer'){ $bad += "the self-update helper must roll back, pin the trust root and refuse non-newer versions" }
if($suh -match '\\runtime[^\r\n]*(Remove-Item|Move-Item)' -and $suh -notmatch 'keepTop'){ $bad += "the self-update helper must never touch runtime\" }
foreach($t in @($suh,$suc)){ if($t -match '[^\x00-\x7F]'){ $bad += "update helper scripts must stay ASCII (Windows PowerShell 5.1)" } }
# Closing and start-up, weather radar, themes: pure rules stay pure; settings messages come only from internal pages; nothing is requested without a click.
foreach($pf in @("CloseGuard.cs","WeatherRadar.cs","WeatherForecast.cs","ThemeLibrary.cs","SelfUpdate.cs")){
  $pt = Get-Content -LiteralPath (Join-Path $bdir $pf) -Raw
  if($pt -cmatch 'HttpClient|WebClient|WebRequest|Process\.Start|File\.|Directory\.'){ $bad += ($pf + " must stay pure: no network, processes or files") }
}
$mwx = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.Weather.cs") -Raw
$mwd = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.WeatherData.cs") -Raw
if($mwd -cmatch 'WebClient|WebRequest' -or $mwd -notmatch 'api\.open-meteo|WeatherForecast\.Host' -or ([regex]::Matches($mwd,'new HttpClient\(')).Count -ne 1){ $bad += "MainWindow.WeatherData.cs may make only the one Open-Meteo forecast request" }
if($mwf -notmatch 'msg == "wxf-get" \|\| msg == "wxf-here" \|\| msg == "wxf-auto" \|\| msg == "wxf-min:0" \|\| msg == "wxf-min:1"\) && tab\.Internal == "start"'){ $bad += "the forecast may be requested only by the start page" }
$mcg = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.CloseGuard.cs") -Raw
$mth = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.Themes.cs") -Raw
if($mwx -cmatch 'HttpClient|WebClient|WebRequest|Process\.Start|File\.'){ $bad += "MainWindow.Weather.cs must not make web requests, start processes or touch files (the card loads only on a click)" }
if($mcg -cmatch 'HttpClient|WebClient|WebRequest|Process\.Start'){ $bad += "MainWindow.CloseGuard.cs must not make web requests or start processes" }
if($mwf -notmatch 'StartsWith\("wx-"\) && \(tab\.Internal == "settings" \|\| tab\.Internal == "customize" \|\| \(tab\.Internal == "start" && msg == "wx-mode:off"\)\)'){ $bad += "wx- radar messages must only be accepted from settings, customize (and Hide from the start page)" }
if($mth -notmatch 'IsAllowedHost' -or $mth -notmatch 'ParsePack'){ $bad += "the theme library download must check the host and validate every theme" }
if($mcg -notmatch 'Task\.WhenAny' -or $mcg -notmatch 'catch \{ proceed = true; \}'){ $bad += "the close question must have a time limit and must never trap the person in the window" }
# Home page, start background, VPN-app hint: pure rules stay pure, the start page makes no web requests.
$sbg = Get-Content -LiteralPath (Join-Path $bdir "StartBackground.cs") -Raw
$vdt = Get-Content -LiteralPath (Join-Path $bdir "VpnDetect.cs") -Raw
$mst = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.Start.cs") -Raw
if($sbg -match 'HttpClient|WebClient|WebRequest|Process\.Start|File\.|Directory\.'){ $bad += "StartBackground.cs must stay pure: no network, processes or files" }
if($vdt -match 'HttpClient|WebClient|WebRequest|Process\.Start|File\.|Directory\.|NetworkInterface'){ $bad += "VpnDetect.cs must stay pure: no network, processes, files or adapter reads" }
if($mst -match 'HttpClient|WebClient|WebRequest|Process\.Start|ProcessStartInfo'){ $bad += "MainWindow.Start.cs must not make web requests or start processes" }
if($sbg -notmatch 'base64,\[A-Za-z0-9\+/=\]\+'){ $bad += "start background pictures must be checked as plain base64 data URLs" }
if($sb -notmatch 'secret_release_to_external_runtime' -or $sb -notmatch 'hard_runtime_dependency' -or $sb -notmatch 'must_not_fail_if_absent'){ $bad += "the SoteriaVault link must refuse a contract that allows secret release or hard dependencies" }
if($sw -notmatch 'soteria\.check'){ $bad += "SoteriaVault checks must be receipted (soteria.check)" }
if($sw -notmatch 'IsSafeRoot'){ $bad += "the SoteriaVault folder must pass SoteriaBridge.IsSafeRoot before use" }
# Launch handling: addresses from other programs are validated, the pipe is current-user only, internal pages cannot be reached from the web.
$la = Get-Content -LiteralPath (Join-Path $bdir "LaunchArgs.cs") -Raw
$ap = Get-Content -LiteralPath (Join-Path $bdir "App.xaml.cs") -Raw
$ml = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.Launch.cs") -Raw
if($la -match 'HttpClient|Process\.Start|System\.Text\.RegularExpressions'){ $bad += "LaunchArgs.cs must stay pure" }
if($ap -notmatch 'PipeOptions\.CurrentUserOnly'){ $bad += "the single-instance pipe must be restricted to the current user (PipeOptions.CurrentUserOnly)" }
if($ap -notmatch 'LaunchArgs\.Parse'){ $bad += "addresses received over the pipe must be validated with LaunchArgs.Parse" }
if($ml -notmatch 'App\.PendingUrls'){ $bad += "addresses that arrive before start-up finishes must be queued" }
if($navGlue -notmatch '(?s)StartsWith\("recognition:".{0,200}?e\.Cancel = true;.{0,40}?if \(tab\.IsInternal\)'){ $bad += "recognition: links must work only from the browser's own pages" }
if($fe -match 'HttpClient|WebClient|WebRequest|TcpClient|UdpClient|Process\.Start|ProcessStartInfo|Assembly\.Load|Activator\.Create|System\.Text\.RegularExpressions'){ $bad += "FilterEngine.cs must stay local-only and regex-free (no network, processes, dynamic code, or backtracking regex on hostile lists)" }
if($fg -match 'Process\.Start|ProcessStartInfo|Assembly\.Load|Activator\.Create|TcpClient|UdpClient|WebClient'){ $bad += "MainWindow.Filters.cs may only use HttpClient for the list updater" }
if(([regex]::Matches($fg, 'new HttpClient\(')).Count -ne 1){ $bad += "exactly one HttpClient (the user-initiated list updater) is allowed in MainWindow.Filters.cs" }
if($fg -notmatch 'Scheme != Uri\.UriSchemeHttps'){ $bad += "list updater must refuse non-https URLs" }
if($fg -notmatch 'MaxListBytes'){ $bad += "list updater must cap download size" }
if($fg -notmatch 'filters\.update'){ $bad += "list updates must be receipted (filters.update with SHA-256)" }
if($sj -match '(?m)^[^/\r\n]*postMessage\('){ $bad += "fingerprint_shield.js must not use postMessage (the host refuses commands from web content)" }
if($sj -match '\beval\s*\(|new Function|document\.write'){ $bad += "fingerprint_shield.js must not use eval/new Function/document.write" }
$pk = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.Passkeys.cs") -Raw
$pg = Get-Content -LiteralPath (Join-Path $bdir "shield\passkey_guard.js") -Raw
if($pk -match 'HttpClient|WebClient|WebRequest|Assembly\.Load|Activator\.Create'){ $bad += "MainWindow.Passkeys.cs must not make outbound requests or load code" }
if(([regex]::Matches($pk, 'Prefixes\.Add\(')).Count -ne 1 -or $pk -notmatch 'Prefixes\.Add\("http://localhost:"'){ $bad += "the passkey test server must listen on exactly one http://localhost:<port>/ prefix" }
if($pk -match 'http://\+|http://\*|0\.0\.0\.0|IPAddress\.Any'){ $bad += "the passkey test server must never bind beyond loopback" }
if($pk -notmatch 'UserHostName'){ $bad += "the passkey test server must check the Host header (DNS rebinding)" }
if($pk -notmatch 'RandomNumberGenerator'){ $bad += "the passkey test path must contain an unguessable token" }
if($pg -match '(?m)^[^/\r\n]*postMessage\(' -or $pg -match '\beval\s*\(|new Function'){ $bad += "passkey_guard.js must not use postMessage/eval" }
if($pk -match '_actions\?\.Append\([^;]*(challenge|rawId|credential|signature)'){ $bad += "passkey receipts must never carry credential data" }
$eg = Get-Content -LiteralPath (Join-Path $bdir "ExtensionGovernance.cs") -Raw
$ex = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.Extensions.cs") -Raw
if($eg -match 'HttpClient|WebClient|WebRequest|TcpClient|Process\.Start|ProcessStartInfo|Assembly\.Load|Activator\.Create'){ $bad += "ExtensionGovernance.cs must stay local-only" }
if($ex -match 'Process\.Start|ProcessStartInfo|Assembly\.Load|Activator\.Create|TcpClient|WebClient'){ $bad += "MainWindow.Extensions.cs must not spawn processes or load code" }
if(([regex]::Matches($ex, 'new HttpClient\(')).Count -ne 1 -or $ex -notmatch 'Scheme != Uri\.UriSchemeHttps' -or $ex -notmatch 'MaxExtDownloadBytes'){ $bad += "extension download must be one https-only, size-capped HttpClient" }
if(([regex]::Matches($ex, 'AddBrowserExtensionAsync\(')).Count -ne 3){ $bad += "AddBrowserExtensionAsync may only be called by the gated loader, the gated enable path, and the isolated test profile" }
if($navGlue -match 'AddBrowserExtensionAsync'){ $bad += "MainWindow.xaml.cs must not register extensions itself" }
if(([regex]::Matches($ex, 'GateDirectory\(')).Count -lt 4){ $bad += "every extension load path must pass GateDirectory (identity recomputed + ledger allow)" }
$iDeny = $ex.IndexOf('decision == "deny"'); $iCopy = $ex.IndexOf('CopyDirectory(p.Root, dst)')
if($iDeny -lt 0 -or $iCopy -lt 0 -or $iDeny -gt $iCopy){ $bad += "a policy-denied extension must be refused before anything is copied into runtime\extensions" }
if($ex -notmatch 'user_approved_review' -or $ex -notmatch 'ExtGovernance\.Record\('){ $bad += "installs and approvals must be recorded in the governance ledger" }
if($ex -notmatch 'removed_unapproved'){ $bad += "extensions the browser did not approve must be removed from the engine profile at startup" }
$hd = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.Hardening.cs") -Raw
if($hd -notmatch 'PopupRules\.Decide' -or $hd -notmatch 'LaunchingExternalUriScheme' -or $hd -notmatch 'ExternalUriRules\.Allowed'){ $bad += "pop-up and external-program rules must be wired in MainWindow.Hardening.cs" }
if($hd -notmatch 'origin\?\.Private'){ $bad += "a pop-up opened from a private tab must stay private" }
if($navGlue -match 'private void OnNewWindowRequested'){ $bad += "the old unconditional pop-up handler must not return" }
if($navGlue -notmatch "Content-Security-Policy"){ $bad += "internal pages must carry a Content-Security-Policy" }
if($navGlue -notmatch '_filters\.Match\('){ $bad += "OnResourceRequested must use the filter engine" }
if($navGlue -notmatch 'RegisterShieldAsync'){ $bad += "every tab must register the fingerprint shield" }
if($bad.Count -gt 0){ $bad | ForEach-Object { Write-Host ("BROWSER_TESTS_FAIL: " + $_) -ForegroundColor Red }; exit 1 }
Write-Host "  ok  - static: filter engine is local-only and regex-free; list updater is https-only, size-capped, receipted; shield never uses postMessage"
Write-Host "  ok  - static: receipts carry no secrets; setup never touches the vault; Fill is origin-gated twice and never event-driven"
# workspace: groups, session, tiles, reader, picture-in-picture, translate, device sync
$wsr = Get-Content -LiteralPath (Join-Path $bdir "Workspace.cs") -Raw
$wsw = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.Workspace.cs") -Raw
$wsy = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.Sync.cs") -Raw
$wxa = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.xaml.cs") -Raw
if($wsr -match 'HttpClient|WebClient|WebRequest|Process\.Start|File\.|Directory\.'){ $bad += "Workspace.cs must stay pure: no network, processes or files" }
if($wsw -match 'HttpClient|WebClient|WebRequest|Process\.Start|ProcessStartInfo'){ $bad += "MainWindow.Workspace.cs must not make web requests or start processes" }
if($wsy -match 'HttpClient|WebClient|WebRequest|Process\.Start|ProcessStartInfo'){ $bad += "MainWindow.Sync.cs must not make web requests or start processes" }
if($wsw -notmatch 'WriteSecure\(SessionPath'){ $bad += "the session file must be written encrypted (WriteSecure)" }
if($wsw -notmatch '!t\.Private'){ $bad += "private tabs must never be saved in the session" }
if($wsw -notmatch 'MessageBox\.Show\(this, q, "Translate"'){ $bad += "translate must ask before the page address goes to a service" }
if($wxa -notmatch 'StartsWith\("tile-"\) && \(tab\.Internal == "start"'){ $bad += "tile messages must come from internal pages only" }
if($wxa -notmatch 'StartsWith\("sync-"\) && tab\.Internal == "settings"'){ $bad += "sync messages must come from the settings page only" }
if($wsy -notmatch 'SoteriaBridge\.IsSafeRoot'){ $bad += "the sync folder must be validated as an absolute local path" }
if($wsy -notmatch 'SetupBlob\.Seal'){ $bad += "the sync file must be sealed with the setup code" }
if($wsy -match 'File\.Delete\(file\)'){ $bad += "sync must never delete the shared sync file" }
if($bad.Count -gt 0){ $bad | ForEach-Object { Write-Host ("BROWSER_TESTS_FAIL: " + $_) -ForegroundColor Red }; exit 1 }
# themes, extension links, taskbar menu
$cus = Get-Content -LiteralPath (Join-Path $bdir "Customize.cs") -Raw
$cuw = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.Customize.cs") -Raw
if($cus -match 'HttpClient|WebClient|WebRequest|Process\.Start|File\.|Directory\.'){ $bad += "Customize.cs must stay pure: no network, processes or files" }
if($cuw -match 'HttpClient|WebClient|WebRequest|Process\.Start|ProcessStartInfo'){ $bad += "MainWindow.Customize.cs must not make web requests itself or start processes" }
if($cuw -notmatch 'DownloadExtensionAsync'){ $bad += "store downloads must use the governed https downloader" }
if($wxa -notmatch 'StartsWith\("cz-"\) && \(tab\.Internal == "customize"'){ $bad += "customize messages must come from internal pages only" }
if($cuw -notmatch 'SafeImagePath' -and $cus -notmatch 'SafeImagePath'){ $bad += "theme pictures must stay inside the theme folder" }
if($cuw -notmatch '--private' -or $cuw -match 'Arguments = url'){ $bad += "the taskbar menu must use fixed switches and quoted addresses" }
$apx = Get-Content -LiteralPath (Join-Path $bdir "App.xaml.cs") -Raw
if($apx -notmatch 'text == "--private" \|\| text == "--new-tab"'){ $bad += "the pipe may only accept the two fixed switches besides validated addresses" }
# address autofill: local data only, hosts only
$bf = Get-Content -LiteralPath (Join-Path $bdir "BrowserFeel.cs") -Raw
$mwx = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.xaml.cs") -Raw
if($bf -notmatch 'class AddressComplete'){ $bad += "address autofill rules (AddressComplete) are missing" }
if($bf -match 'class AddressComplete[\s\S]*(HttpClient|WebClient|WebRequest)'){ $bad += "address autofill must not make web requests" }
if($mwx -notmatch 'AddressComplete\.Complete' -or $mwx -notmatch 'Address_PreviewKeyDown'){ $bad += "address autofill must be wired into the address bar" }
# toolbar layout: Back, Forward, Home, Private, Reload, one icon style, no emoji buttons
$xm = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.xaml") -Raw
if($xm -notmatch 'x:Name="HomeBtn"[\s\S]*x:Name="PrivateBtn"[\s\S]*x:Name="ReloadBtn"'){ $bad += "the private tab button must sit between Home and Reload" }
if($xm -match 'NavBtn\}" FontFamily="Segoe UI Emoji"'){ $bad += "toolbar and side bar buttons must use the IconBtn style" }
# shared page frame: every menu entry is a real internal page; the frame script makes no requests
$shl = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.Shell.cs") -Raw
$mwx2 = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.xaml.cs") -Raw
foreach($mm in [regex]::Matches($shl, '\("([a-z]+)", "[A-Za-z ]+"\)')){
  $pid2 = $mm.Groups[1].Value
  if($mwx2 -notmatch ('"' + $pid2 + '"\s*=>')){ $bad += "the page menu lists '$pid2' but LoadInternal has no such page" }
}
if($shl -match 'fetch\(|XMLHttpRequest|WebSocket|http://|https://'){ $bad += "the shared page frame must not make requests" }
if($mwx2 -notmatch 'private string PageFoot => ShellClose\(\)'){ $bad += "PageFoot must close the shared frame" }
# cards and the unlock PIN: no CVC stored, no requests, gates in front of every fill/copy/reveal, PIN typed in a native masked box, receipts without numbers
$cvt = Get-Content -LiteralPath (Join-Path $bdir "CardVault.cs") -Raw
$cgl = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.Cards.cs") -Raw
$pwg = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.Passwords.cs") -Raw
if($cvt -match 'HttpClient|WebClient|WebRequest|TcpClient'){ $bad += "CardVault.cs must not make web requests" }
if($cvt -match 'public (string|int)[^;{]*\b(Cvc|Cvv|Csc|SecurityCode)\b'){ $bad += "a card must never carry a security code field" }
if(([regex]::Matches($pwg, 'EnsureUnlocked\(')).Count -lt 3){ $bad += "login Fill, Copy and Reveal must all go through EnsureUnlocked" }
if($cgl -notmatch 'new PasswordBox'){ $bad += "the PIN must be typed in a masked native box" }
if($cgl -match 'Append\("card\.[a-z]+",[^;]*\.Number'){ $bad += "receipts must never contain a card number" }
if($cgl -notmatch 'Uri\.UriSchemeHttps'){ $bad += "cards may only be filled on https pages" }
if($cgl -notmatch 'RequirePinSet\(\)'){ $bad += "cards must need a PIN" }
# library: bookmarks, downloads, history ranges, overview
$lib = Get-Content -LiteralPath (Join-Path $bdir "MainWindow.Library.cs") -Raw
if($lib -match 'HttpClient|WebClient|WebRequest|TcpClient'){ $bad += "MainWindow.Library.cs must not make web requests" }
if($lib -notmatch 'DownloadRules\.Assess\(Path\.GetFileName\(d\.Path\)\) != DownloadRisk\.None'){ $bad += "Open on a download must refuse files that can run code" }
if($lib -notmatch 'MessageBox\.Show\(this, "Clear history from'){ $bad += "clearing history must ask first" }
if($wxa -notmatch 'StartsWith\("dl-"\) && \(tab\.Internal == "downloads"'){ $bad += "download messages must come from internal pages only" }
if($wxa -notmatch 'StartsWith\("ov-"\) && tab\.Internal == "overview"'){ $bad += "overview messages must come from the overview page only" }
if($wxa -notmatch 'StartsWith\("bm-"\) && tab\.Internal == "bookmarks"'){ $bad += "bookmark messages must come from the bookmarks page only" }
if($lib -notmatch 'IsSafeRoot'){ $bad += "the download folder must be validated" }
if($bad.Count -gt 0){ $bad | ForEach-Object { Write-Host ("BROWSER_TESTS_FAIL: " + $_) -ForegroundColor Red }; exit 1 }
Write-Host "  ok  - static: library pages are local-only; risky downloads cannot be opened from the list; clearing asks first; messages are gated"
Write-Host "  ok  - static: workspace rules are pure; session encrypted and never holds private tabs; translate asks first; sync is local-folder only, sealed and never deletes"
Write-Host "  ok  - static: privacy/tools/viewer/vault/setup modules are local-only; tools never overwrite originals; no prefix-based localhost exemption"

# Fingerprint shield maths run under node when it is installed (they are JavaScript, not C#).
$node = Get-Command node -ErrorAction SilentlyContinue
if($node){
  $shieldOut = & node (Join-Path $RepoRoot "browser.tests\shield\shield.test.js") 2>&1 | Out-String
  Write-Host $shieldOut
  if($LASTEXITCODE -ne 0){ Write-Host "BROWSER_TESTS_FAIL: fingerprint shield tests failed" -ForegroundColor Red; exit 1 }
  $ptOut = & node (Join-Path $RepoRoot "browser.tests\shield\pagetools.test.js") 2>&1 | Out-String
  Write-Host $ptOut
  if($LASTEXITCODE -ne 0){ Write-Host "BROWSER_TESTS_FAIL: reader/picture-in-picture script tests failed" -ForegroundColor Red; exit 1 }
  $pkOut = & node (Join-Path $RepoRoot "browser.tests\shield\passkey.test.js") 2>&1 | Out-String
  Write-Host $pkOut
  if($LASTEXITCODE -ne 0){ Write-Host "BROWSER_TESTS_FAIL: passkey guard/test-page tests failed" -ForegroundColor Red; exit 1 }
} else { Write-Host "  note - node not installed: fingerprint shield JS tests were skipped (install Node.js to run them)" -ForegroundColor Yellow }

$out = & dotnet run --project $proj -c Release 2>&1 | Out-String
Write-Host $out
if($LASTEXITCODE -ne 0){ Write-Host "BROWSER_TESTS_FAIL: dotnet run exited with code $LASTEXITCODE" -ForegroundColor Red; exit 1 }
if($out -notmatch "SELFTEST_RECOGNITION_BROWSER_GOVERNED_ACTIONS_V1_OK"){ Write-Host "BROWSER_TESTS_FAIL: success token not emitted" -ForegroundColor Red; exit 1 }
# Start page animation: the pages written by the C# tests run in a simulated browser under node (ThemeForge runtime + boot script).
if($node){
  $mtOut = & node (Join-Path $RepoRoot "browser.tests\shield\motion.test.js") 2>&1 | Out-String
  Write-Host $mtOut
  if($LASTEXITCODE -ne 0){ Write-Host "BROWSER_TESTS_FAIL: start page animation tests failed" -ForegroundColor Red; exit 1 }
}
Write-Host "SELFTEST_RECOGNITION_BROWSER_GOVERNED_ACTIONS_V1_OK" -ForegroundColor Green
