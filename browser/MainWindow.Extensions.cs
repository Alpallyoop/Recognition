using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Recognition.Browser
{
    // recognition:extensions — install, review, test, enable/disable and remove Chromium extensions.
    //
    // Trust model (the same one the PowerShell governance tool uses; see ExtensionGovernance.cs, which is executed by the tests):
    //   * Identity   = SHA-256 of the extension's files. Any changed byte is a different extension.
    //   * Decision   = deterministic policy (config\extension_policy.v1.json): allow / review / deny.
    //   * Ledger     = hash-chained record of every install, approval and removal (proofs\receipts\...extension_governance.v1.ndjson).
    //   * Load gate  = at startup and on enable, the identity is recomputed from the files on disk and the extension loads ONLY if the
    //                  latest ledger record for those exact bytes says "allow". Extensions the browser did not approve are removed
    //                  from the engine profile at startup.
    //   * Deny       = cannot be installed from here. Review = installable only after an explicit "I reviewed these permissions" click.
    //
    // Honest limits: WebView2 supports a subset of the Chromium extension system (content scripts and background workers generally work;
    // toolbar buttons, popups and some APIs may not). Use Test before relying on one. The policy scores API and host permissions; content
    // script match patterns and optional permissions are shown to you but not scored. A reviewed extension still runs with the access
    // it declared. Chrome Web Store downloads are not built in: use a file, a folder, or an https link to the developer's .crx/.zip.
    public partial class MainWindow
    {
        private sealed class ExtItem
        {
            public string Gid { get; set; } = "";
            public string Name { get; set; } = "";
            public string Version { get; set; } = "";
            public string Dir { get; set; } = "";            // relative to the repo root
            public bool Enabled { get; set; } = true;
            public string InstalledUtc { get; set; } = "";
            public string Source { get; set; } = "";
            public string ChromiumId { get; set; } = "";
        }
        private sealed class ExtState { public bool Enabled { get; set; } public List<ExtItem> Items { get; set; } = new(); }
        private sealed class ExtPending
        {
            public string Staging = "", Root = "", Source = "";
            public ExtIdentity Identity = null!; public ExtManifest Manifest = null!; public ExtDecision Decision = null!;
            public bool IsTheme;   // a Chrome theme: Recognition can use its colours, but there is nothing to install into the engine
        }

        private ExtState _extState = new();
        private readonly Dictionary<string, string> _extStatus = new();
        private ExtPending? _extPending;
        private bool _extEnabledAtStart, _extConfigEnabled;
        private CoreWebView2Environment? _extTestEnv;
        private string? _extTestDir;
        private const long MaxExtDownloadBytes = 100L * 1024 * 1024;

        private void MenuExtensions_Click(object sender, RoutedEventArgs e) => OpenInternalInActiveTab("extensions");

        private string ExtStatePath => Path.Combine(_repoRoot, "runtime", "extensions.v1.json");
        private string ExtLedgerPath => Path.Combine(_repoRoot, "proofs", "receipts", "recognition.extension_governance.v1.ndjson");
        private string ExtPolicyPath => Path.Combine(_repoRoot, "config", "extension_policy.v1.json");
        private string ExtDirAbs(ExtItem i) => Path.GetFullPath(Path.Combine(_repoRoot, i.Dir));

        private void LoadExtState()
        {
            _extState = new ExtState();
            try
            {
                if (File.Exists(ExtStatePath))
                {
                    var s = JsonSerializer.Deserialize<ExtState>(File.ReadAllText(ExtStatePath));
                    if (s != null)
                    {
                        s.Items = s.Items.Where(i => i != null && i.Gid.Length == 64 && i.Gid.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
                                                     && i.Dir == Path.Combine("runtime", "extensions", i.Gid)).ToList();   // a state file can only point inside runtime\extensions\<id>
                        _extState = s;
                    }
                }
            }
            catch { }
        }

        private void SaveExtState()
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(ExtStatePath)!); File.WriteAllText(ExtStatePath, JsonSerializer.Serialize(_extState), new UTF8Encoding(false)); } catch { }
        }

        private CoreWebView2Profile? AnyMainProfile()
            => _tabs.FirstOrDefault(t => !t.Private && t.Ready && t.Web?.CoreWebView2 != null)?.Web.CoreWebView2.Profile;

        // ---- startup loader (replaces the old PowerShell-based gate) ----------------------------------------------------------------

        private (bool Ok, string Gid, string Note) GateDirectory(string dir)
        {
            try
            {
                var id = ExtGovernance.ComputeIdentity(dir);
                var (ok, note) = ExtGovernance.Gate(ExtLedgerPath, id.Id);
                return (ok, id.Id, note);
            }
            catch (Exception ex) { return (false, "", ex.Message); }
        }

        private async Task LoadExtensionsAsync(BrowserTab tab)
        {
            if (_extLoaded || !_extEnabled || tab.Web.CoreWebView2 == null) return;
            _extLoaded = true;
            int ok = 0, refused = 0;
            try
            {
                var profile = tab.Web.CoreWebView2.Profile;
                var desired = new List<(string Label, string Dir, ExtItem? Item)>();
                foreach (var rel in _extPaths)
                {
                    var source = Path.IsPathRooted(rel) ? rel : Path.Combine(_repoRoot, rel);
                    try { var dir = PrepareExtensionSource(source); if (dir != null) desired.Add((rel, dir, null)); }
                    catch (Exception ex) { Status("extension package error (" + rel + "): " + ex.Message); }
                }
                foreach (var it in _extState.Items.Where(i => i.Enabled)) desired.Add((it.Name, ExtDirAbs(it), it));
                foreach (var it in _extState.Items.Where(i => !i.Enabled)) _extStatus[it.Gid] = "disabled";

                var approved = new List<(string Label, string Dir, ExtItem? Item, string Gid)>();
                foreach (var d in desired)
                {
                    var dd = d;
                    var (good, gid, note) = await Task.Run(() => GateDirectory(dd.Dir));
                    if (!good)
                    {
                        refused++; if (d.Item != null) _extStatus[d.Item.Gid] = "refused: " + note;
                        _actions?.Append("extension.refused", d.Item != null ? d.Item.Gid : d.Label);
                        Status("extension refused by governance: " + d.Label + " — " + note);
                        continue;
                    }
                    approved.Add((d.Label, d.Dir, d.Item, gid));
                }

                // anything in the engine profile that is not an approved extension we already track is removed
                var keep = new HashSet<string>(approved.Where(a => a.Item != null && a.Item.ChromiumId.Length > 0).Select(a => a.Item!.ChromiumId), StringComparer.Ordinal);
                var present = new HashSet<string>(StringComparer.Ordinal);
                foreach (var e in await profile.GetBrowserExtensionsAsync())
                {
                    if (keep.Contains(e.Id)) { present.Add(e.Id); continue; }
                    try { await e.RemoveAsync(); _actions?.Append("extension.removed_unapproved", e.Id); } catch { }
                }
                foreach (var a in approved)
                {
                    if (a.Item != null && a.Item.ChromiumId.Length > 0 && present.Contains(a.Item.ChromiumId)) { _extStatus[a.Item.Gid] = "loaded"; ok++; continue; }
                    try
                    {
                        var ext = await profile.AddBrowserExtensionAsync(a.Dir);
                        if (a.Item != null) { a.Item.ChromiumId = ext.Id; _extStatus[a.Item.Gid] = "loaded"; }
                        ok++; _actions?.Append("extension.loaded", a.Gid);
                    }
                    catch (Exception ex) { if (a.Item != null) _extStatus[a.Item.Gid] = "error: " + ex.Message; Status("extension load error (" + a.Label + "): " + ex.Message); }
                }
                SaveExtState();
            }
            catch (Exception ex) { Status("extension loading failed: " + ex.Message); }
            if (ok > 0) Status($"loaded {ok} governed extension(s)" + (refused > 0 ? $", refused {refused}" : ""));
            else if (refused > 0) Status($"all {refused} configured extension(s) refused by governance");
        }

        // ---- staging, review ---------------------------------------------------------------------------------------------------------

        private string NewStagingDir()
        {
            var dir = Path.Combine(_repoRoot, "runtime", "extensions_staging", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir); return dir;
        }

        private void DiscardPending()
        {
            var p = _extPending; _extPending = null;
            if (p != null) { try { Directory.Delete(p.Staging, true); } catch { } }
        }

        private ExtPending Review(string staging, string root, string source)
        {
            var id = ExtGovernance.ComputeIdentity(root);
            var man = ExtGovernance.ReadManifest(root);
            var pol = ExtGovernance.ParsePolicy(File.ReadAllText(ExtPolicyPath));
            var dec = ExtGovernance.Decide(man, id.Id, pol);
            bool isTheme = false;
            try { isTheme = ThemePacks.FromChromeTheme(File.ReadAllText(Path.Combine(root, "manifest.json"), Encoding.UTF8).TrimStart('\uFEFF')) != null; } catch { }
            return new ExtPending { Staging = staging, Root = root, Source = source, Identity = id, Manifest = man, Decision = dec, IsTheme = isTheme };
        }

        private ExtPending StageBytes(byte[] package, string source)
        {
            var staging = NewStagingDir();
            try { var root = ExtPackage.Unpack(package, Path.Combine(staging, "x")); return Review(staging, root, source); }
            catch { try { Directory.Delete(staging, true); } catch { } throw; }
        }

        private ExtPending StageFolder(string folder, string source)
        {
            long total = 0; int count = 0;
            foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (++count > ExtGovernance.MaxFiles) throw new ExtGovException("too many files in the folder");
                total += new FileInfo(f).Length; if (total > ExtGovernance.MaxTotalBytes) throw new ExtGovException("folder is larger than " + ExtGovernance.MaxTotalBytes / 1024 / 1024 + " MB");
            }
            var staging = NewStagingDir();
            try { var dst = Path.Combine(staging, "x"); ExtPackage.CopyDirectory(folder, dst); return Review(staging, ExtPackage.ResolveRoot(dst), source); }
            catch { try { Directory.Delete(staging, true); } catch { } throw; }
        }

        private async Task<byte[]> DownloadExtensionAsync(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) throw new ExtGovException("only https addresses are accepted");
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 4 }) { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Recognition-extension-installer/1.1");
            using var resp = await http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) throw new ExtGovException("server answered " + (int)resp.StatusCode);
            if (resp.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps) throw new ExtGovException("redirected away from https; refused");
            if (resp.Content.Headers.ContentLength is long cl && cl > MaxExtDownloadBytes) throw new ExtGovException("download is larger than " + MaxExtDownloadBytes / 1024 / 1024 + " MB");
            using var ms = new MemoryStream();
            using (var net = await resp.Content.ReadAsStreamAsync())
            {
                var buf = new byte[81920]; int n;
                while ((n = await net.ReadAsync(buf, 0, buf.Length)) > 0) { ms.Write(buf, 0, n); if (ms.Length > MaxExtDownloadBytes) throw new ExtGovException("download is larger than " + MaxExtDownloadBytes / 1024 / 1024 + " MB"); }
            }
            return ms.ToArray();
        }

        private static string CleanLabel(string? s) => new string((s ?? "").Where(c => !char.IsControl(c)).Take(120).ToArray());

        private void PostToExtPage(object payload)
        {
            var t = Active; if (t == null || t.Internal != "extensions") return;
            try { t.Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload)); } catch { }
        }
        private void ExtReport(string text, bool ok = true) { Status(text); PostToExtPage(new { type = "action", ok, text }); }

        private object? PendingView()
        {
            var p = _extPending; if (p == null) return null;
            return new
            {
                name = CleanLabel(p.Manifest.Name), version = CleanLabel(p.Manifest.Version), description = CleanLabel(p.Manifest.Description), mv = p.Manifest.ManifestVersion,
                id = p.Identity.Id, files = p.Identity.Files.Count, bytes = p.Identity.TotalBytes, source = CleanLabel(p.Source),
                permissions = p.Manifest.Permissions.Select(CleanLabel).ToList(), hosts = p.Manifest.HostPermissions.Select(CleanLabel).ToList(),
                optional = p.Manifest.OptionalPermissions.Select(CleanLabel).ToList(), contentScripts = p.Manifest.ContentScriptMatches.Select(CleanLabel).ToList(),
                decision = p.Decision.Decision, reasons = p.Decision.Reasons.Select(CleanLabel).ToList(),
                already = _extState.Items.Any(i => i.Gid == p.Identity.Id), theme = p.IsTheme
            };
        }

        private void SendExtSnapshot()
        {
            (int Count, string Head, string? Error) ledger;
            try { var v = ExtGovernance.VerifyLedger(ExtLedgerPath); ledger = (v.Count, v.Head, null); } catch (Exception ex) { ledger = (0, "", ex.Message); }
            PostToExtPage(new
            {
                type = "snapshot",
                support = _extEnabled, supportConfig = _extConfigEnabled, supportSaved = _extState.Enabled, restartNeeded = (_extConfigEnabled || _extState.Enabled) != _extEnabledAtStart,
                items = _extState.Items.OrderBy(i => i.Name).Select(i => new
                {
                    gid = i.Gid, name = CleanLabel(i.Name), version = CleanLabel(i.Version), enabled = i.Enabled, installed = i.InstalledUtc, source = CleanLabel(i.Source),
                    status = _extStatus.TryGetValue(i.Gid, out var s) ? s : (i.Enabled ? (_extEnabled ? "not loaded yet" : "extension support is off") : "disabled"),
                    chromiumId = i.ChromiumId
                }).ToList(),
                pending = PendingView(),
                ledger = new { count = ledger.Count, head = ledger.Head, error = ledger.Error },
                dev = ExtDevView()
            });
        }

        // ---- messages from the page ---------------------------------------------------------------------------------------------------

        private async void HandleExtMessage(string msg)
        {
            string Dec(string s) { try { return Uri.UnescapeDataString(s); } catch { return ""; } }
            try
            {
                if (msg == "ext-state") { SendExtSnapshot(); return; }
                if (msg.StartsWith("ext-dev-")) { await HandleExtDevMessage(msg); return; }
                if (msg.StartsWith("ext-support:"))
                {
                    var on = msg.EndsWith(":on"); _extState.Enabled = on; SaveExtState(); _actions?.Append("extensions.support", on ? "on" : "off");
                    ExtReport("extension support " + (on ? "ON" : "OFF") + " — restart Recognition to apply."); SendExtSnapshot(); return;
                }
                if (msg == "ext-restart") { RestartToApply(); return; }
                if (msg == "ext-open-policy") { OpenFolder(Path.Combine(_repoRoot, "config")); return; }
                if (msg == "ext-pick-file")
                {
                    var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose an extension package", Filter = "Extension package (*.crx;*.zip;*.xpi)|*.crx;*.zip;*.xpi|All files|*.*" };
                    if (dlg.ShowDialog(this) != true) return;
                    var fi = new FileInfo(dlg.FileName);
                    if (fi.Length > ExtPackage.MaxPackageBytes) { ExtReport("that file is larger than " + ExtPackage.MaxPackageBytes / 1024 / 1024 + " MB", false); return; }
                    DiscardPending();
                    _extPending = await Task.Run(() => StageBytes(File.ReadAllBytes(dlg.FileName), fi.Name));
                    SendExtSnapshot(); return;
                }
                if (msg == "ext-pick-folder")
                {
                    var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose an unpacked extension folder (it contains manifest.json)" };
                    if (dlg.ShowDialog(this) != true) return;
                    DiscardPending();
                    _extPending = await Task.Run(() => StageFolder(dlg.FolderName, Path.GetFileName(dlg.FolderName.TrimEnd('\\', '/')) + " (folder)"));
                    SendExtSnapshot(); return;
                }
                if (msg.StartsWith("ext-open:"))
                {
                    // store pages only, opened in a new tab so this page stays where it is
                    var link = Dec(msg.Substring("ext-open:".Length)).Trim();
                    if (Uri.TryCreate(link, UriKind.Absolute, out var lu) && lu.Scheme == Uri.UriSchemeHttps && lu.UserInfo.Length == 0 &&
                        (lu.Host is "chromewebstore.google.com" or "microsoftedge.microsoft.com" or "addons.mozilla.org")) OpenBookmark(lu.AbsoluteUri, true);
                    return;
                }
                if (msg.StartsWith("ext-demo:"))
                {
                    var name = msg.Substring("ext-demo:".Length);
                    if (name is not ("hello-good" or "hello-review")) return;
                    var dir = Path.Combine(_repoRoot, "samples", "extensions", name);
                    if (!Directory.Exists(dir)) { ExtReport("the demo files are not in this install (samples\\extensions\\" + name + ")", false); return; }
                    DiscardPending();
                    _extPending = await Task.Run(() => StageFolder(dir, "demo: " + name));
                    ExtReport("demo loaded for review: read what it asks for below, then install it or discard it.");
                    SendExtSnapshot(); return;
                }
                if (msg.StartsWith("ext-url:"))
                {
                    var url = Dec(msg.Substring("ext-url:".Length)).Trim();
                    var store = ExtSources.Parse(url);   // a Chrome Web Store or Edge Add-ons link, or an extension id
                    var ffSlug = store == null ? ExtSources.ParseFirefox(url) : null;
                    if (store != null) url = ExtSources.Url(store, EngineMajor());
                    else if (ffSlug != null)
                    {
                        ExtReport("looking up \"" + ffSlug + "\" on Firefox Add-ons...");
                        var api = Encoding.UTF8.GetString(await DownloadExtensionAsync(ExtSources.FirefoxApiUrl(ffSlug)));
                        var file = ExtSources.FirefoxFileUrl(api);
                        if (file == null) throw new ExtGovException("Mozilla did not return a download for \"" + ffSlug + "\" (the add-on may not exist, or is not public)");
                        url = file;
                    }
                    ExtReport(store != null ? "downloading from the " + (store.Store == "edge" ? "Edge Add-ons" : "Chrome Web Store") + "..." : ffSlug != null ? "downloading the Firefox add-on..." : "downloading...");
                    var bytes = await DownloadExtensionAsync(url);
                    DiscardPending();
                    var host = new Uri(url).Host;
                    _extPending = await Task.Run(() => StageBytes(bytes, host));
                    ExtReport("downloaded " + (bytes.Length / 1024) + " KB (sha256 " + Convert.ToHexString(SHA256.HashData(bytes)).Substring(0, 12).ToLowerInvariant() + "...). Review it below before installing." + (ffSlug != null ? " Note: Firefox add-ons are built for Firefox, so many will not run in this engine. Use Test first." : ""));
                    SendExtSnapshot(); return;
                }
                if (msg == "ext-cancel") { DiscardPending(); SendExtSnapshot(); return; }
                if (msg == "ext-theme-apply")
                {
                    var tp = _extPending;
                    if (tp == null || !tp.IsTheme) { ExtReport("nothing to apply", false); return; }
                    var pack = ThemePacks.FromChromeTheme(File.ReadAllText(Path.Combine(tp.Root, "manifest.json"), Encoding.UTF8).TrimStart('\uFEFF'));   // Chrome or Firefox
                    if (pack == null) { ExtReport("that theme could not be read", false); return; }
                    string? img = null;
                    if (pack.NtpImage != null)
                    {
                        var full = Path.GetFullPath(Path.Combine(tp.Root, pack.NtpImage.Replace('/', Path.DirectorySeparatorChar)));
                        if (full.StartsWith(Path.GetFullPath(tp.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(full)) img = full;
                    }
                    ApplyPack(pack, img, tp.Root); DiscardPending();   // pictures are copied before the staging folder goes
                    ExtReport("applied the theme \"" + pack.Name + "\"" + (pack.Header != null && _header != null ? " with its header picture" : "") + ". Change it any time on the Customize page.");
                    SendExtSnapshot(); return;
                }
                if (msg.StartsWith("ext-install:")) { ExtInstall(msg.EndsWith(":approve")); return; }
                if (msg == "ext-verify-chain")
                {
                    try { var v = ExtGovernance.VerifyLedger(ExtLedgerPath); ExtReport("ledger verified: " + v.Count + " records, head " + (v.Head.Length >= 12 ? v.Head.Substring(0, 12) : v.Head) + "…"); }
                    catch (Exception ex) { ExtReport("LEDGER PROBLEM: " + ex.Message + ". Extensions will not load until this is resolved.", false); }
                    return;
                }
                if (msg == "ext-test-pending")
                {
                    var p = _extPending; if (p == null) { ExtReport("nothing to test", false); return; }
                    if (p.Decision.Decision == "deny") { ExtReport("refused: policy denies this extension, so it cannot be tested either.", false); return; }
                    await ExtTestAsync(p.Root, p.Identity.Id); return;
                }
                if (msg.StartsWith("ext-test:"))
                {
                    var it = _extState.Items.FirstOrDefault(i => i.Gid == msg.Substring("ext-test:".Length));
                    if (it == null) return;
                    var (good, _, note) = GateDirectory(ExtDirAbs(it));
                    if (!good) { ExtReport("refused by governance: " + note, false); return; }
                    await ExtTestAsync(ExtDirAbs(it), it.Gid); return;
                }
                if (msg.StartsWith("ext-toggle:")) { await ExtToggleAsync(msg.Substring("ext-toggle:".Length)); return; }
                if (msg.StartsWith("ext-remove:")) { await ExtRemoveAsync(msg.Substring("ext-remove:".Length)); return; }
            }
            catch (ExtGovException ex) { ExtReport("not accepted: " + ex.Message + (ex.Message.Contains("not a CRX or ZIP") ? ". That address is a web page or another kind of file, not an extension package. Paste a store page link (Chrome Web Store, Edge Add-ons or Firefox Add-ons), an extension id, or a direct .crx, .zip or .xpi address." : ""), false); }
            catch (Exception ex) { ExtReport("error: " + ex.Message, false); }
        }

        private void ExtInstall(bool approved)
        {
            var p = _extPending;
            if (p == null) { ExtReport("nothing to install", false); return; }
            if (p.IsTheme) { ExtReport("this is a theme: use Use this theme (it has nothing to install)", false); return; }
            var decision = p.Decision.Decision;
            if (decision == "deny") { ExtReport("refused: policy denies this extension (" + string.Join("; ", p.Decision.Reasons) + "). To change the policy, edit config\\extension_policy.v1.json yourself.", false); return; }
            if (decision == "review" && !approved) { ExtReport("this extension needs your explicit approval of the permissions listed", false); return; }
            var gid = p.Identity.Id;
            var rel = Path.Combine("runtime", "extensions", gid); var dst = Path.Combine(_repoRoot, rel);
            try
            {
                if (!Directory.Exists(dst)) ExtPackage.CopyDirectory(p.Root, dst);
                var fin = ExtGovernance.ComputeIdentity(dst);
                if (fin.Id != gid) { try { Directory.Delete(dst, true); } catch { } ExtReport("the installed copy did not match the reviewed files; nothing was installed", false); return; }
                var reasons = p.Decision.Reasons.ToList();
                string recDecision = decision;
                if (decision == "review") { recDecision = "allow"; reasons.Add("user_approved_review"); } else reasons.Add("user_installed");
                ExtGovernance.Record(ExtLedgerPath, gid, p.Manifest, fin.Files, recDecision, reasons, DateTime.UtcNow);
                _extState.Items.RemoveAll(i => i.Gid == gid);
                _extState.Items.Add(new ExtItem { Gid = gid, Name = CleanLabel(p.Manifest.Name), Version = CleanLabel(p.Manifest.Version), Dir = rel, Enabled = true, InstalledUtc = DateTime.UtcNow.ToString("o"), Source = CleanLabel(p.Source) });
                SaveExtState();
                _actions?.Append("extension.install", gid + ":" + (decision == "review" ? "approved" : "allowed"));
                DiscardPending();
                ExtReport("installed " + CleanLabel(p.Manifest.Name) + (_extEnabled ? "." : ". Extension support is off: turn it on and restart to load it."));
                if (_extEnabled) _ = LoadOneAsync(_extState.Items.First(i => i.Gid == gid));
            }
            catch (Exception ex) { ExtReport("install failed: " + ex.Message, false); }
            SendExtSnapshot();
        }

        private async Task LoadOneAsync(ExtItem it)
        {
            try
            {
                var profile = AnyMainProfile(); if (profile == null) { _extStatus[it.Gid] = "will load at next start"; return; }
                var (good, _, note) = await Task.Run(() => GateDirectory(ExtDirAbs(it)));
                if (!good) { _extStatus[it.Gid] = "refused: " + note; _actions?.Append("extension.refused", it.Gid); return; }
                var ext = await profile.AddBrowserExtensionAsync(ExtDirAbs(it));
                it.ChromiumId = ext.Id; _extStatus[it.Gid] = "loaded"; SaveExtState();
                _actions?.Append("extension.loaded", it.Gid);
            }
            catch (Exception ex) { _extStatus[it.Gid] = "error: " + ex.Message; }
            SendExtSnapshot();
        }

        private async Task RemoveFromProfileAsync(ExtItem it)
        {
            var profile = AnyMainProfile(); if (profile == null || it.ChromiumId.Length == 0) return;
            try { foreach (var e in await profile.GetBrowserExtensionsAsync()) if (e.Id == it.ChromiumId) await e.RemoveAsync(); } catch { }
            it.ChromiumId = "";
        }

        private async Task ExtToggleAsync(string gid)
        {
            var it = _extState.Items.FirstOrDefault(i => i.Gid == gid); if (it == null) return;
            it.Enabled = !it.Enabled; SaveExtState(); _actions?.Append("extension." + (it.Enabled ? "enable" : "disable"), gid);
            if (!it.Enabled) { await RemoveFromProfileAsync(it); _extStatus[gid] = "disabled"; SaveExtState(); }
            else if (_extEnabled && _extLoaded) await LoadOneAsync(it);
            else _extStatus[gid] = _extEnabled ? "will load at next start" : "extension support is off";
            SendExtSnapshot();
        }

        private async Task ExtRemoveAsync(string gid)
        {
            var it = _extState.Items.FirstOrDefault(i => i.Gid == gid); if (it == null) return;
            if (MessageBox.Show(this, "Remove \"" + it.Name + "\"?\n\nIts files are deleted and the removal is recorded in the governance ledger. Its stored data in the browser engine is removed with it.", "Recognition — Extensions", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await RemoveFromProfileAsync(it);
            var dir = ExtDirAbs(it);
            try
            {
                if (Directory.Exists(dir))
                {
                    var man = ExtGovernance.ReadManifest(dir); var id = ExtGovernance.ComputeIdentity(dir);
                    ExtGovernance.Record(ExtLedgerPath, id.Id, man, id.Files, "deny", new[] { "user_removed" }, DateTime.UtcNow);
                }
            }
            catch { /* a damaged copy is still removed below; the earlier ledger records stay as they are */ }
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception ex) { ExtReport("removed from the list, but some files could not be deleted: " + ex.Message, false); }
            _extState.Items.Remove(it); _extStatus.Remove(gid); SaveExtState();
            _actions?.Append("extension.remove", gid);
            ExtReport("removed " + it.Name);
            SendExtSnapshot();
        }

        // ---- test in an isolated, throw-away profile -------------------------------------------------------------------------------------

        private async Task<CoreWebView2Environment> EnsureExtTestEnvAsync()
        {
            if (_extTestEnv != null) return _extTestEnv;
            _extTestDir = Path.Combine(TmpRoot, "rb-exttest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_extTestDir);
            var o = await PrivateEnvOptsAsync();
            o.AreBrowserExtensionsEnabled = true;
            _extTestEnv = await CoreWebView2Environment.CreateAsync(null, _extTestDir, o);
            return _extTestEnv;
        }

        // Loads the extension into a separate temporary profile (no history, cookies or passwords of yours; deleted when Recognition closes)
        // in a private tab, opens the extension's own options/popup page when it has one, and reports what the engine said.
        private async Task ExtTestAsync(string dir, string gid)
        {
            try
            {
                var env = await EnsureExtTestEnvAsync();
                var tab = await NewTabCoreAsync("Extension test", true, env);
                if (tab == null) { ExtReport("could not open the test tab", false); return; }
                Tabs.SelectedItem = tab.Item; ShowActiveWebView();
                var core = tab.Web.CoreWebView2;
                foreach (var old in await core.Profile.GetBrowserExtensionsAsync()) { try { await old.RemoveAsync(); } catch { } }
                var ext = await core.Profile.AddBrowserExtensionAsync(dir);
                string? page = null;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")).TrimStart('﻿'), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                    var r = doc.RootElement;
                    string? Pick(JsonElement e, string a, string b) => e.TryGetProperty(a, out var x) && x.ValueKind == JsonValueKind.Object && x.TryGetProperty(b, out var y) && y.ValueKind == JsonValueKind.String ? y.GetString() : null;
                    page = Pick(r, "options_ui", "page") ?? (r.TryGetProperty("options_page", out var op) && op.ValueKind == JsonValueKind.String ? op.GetString() : null)
                           ?? Pick(r, "action", "default_popup") ?? Pick(r, "browser_action", "default_popup");
                }
                catch { }
                bool safePage = page != null && !page.Contains("..") && !page.Contains(':') && !page.Contains('\\');
                if (page != null && safePage) { tab.Internal = ""; core.Navigate("chrome-extension://" + ext.Id + "/" + page.TrimStart('/')); }
                else LoadInternal(tab, "start");
                _actions?.Append("extension.test", gid);
                ExtReport("loaded in a temporary test profile (engine id " + ext.Id + ", enabled: " + ext.IsEnabled + ")." + (page != null && safePage ? " Opened its page." : " It has no page of its own: browse to a site where it should act in the test tab.") + " Nothing from this test is kept.");
            }
            catch (Exception ex) { ExtReport("the engine could not load this extension: " + ex.Message, false); }
        }

        private void CleanupExtensionsOnExit()
        {
            DiscardPending();
            CleanupExtDevOnExit();
            if (_extTestDir != null) { try { if (Directory.Exists(_extTestDir)) Directory.Delete(_extTestDir, true); } catch { } }
            try { var s = Path.Combine(_repoRoot, "runtime", "extensions_staging"); if (Directory.Exists(s)) Directory.Delete(s, true); } catch { }
        }

        // ---- page ---------------------------------------------------------------------------------------------------------------------------

        private string ExtensionsHtml()
        {
            var sb = new StringBuilder(PageHead);
            sb.Append(@"<title>Extensions</title><style>
.card{background:var(--card);border:1px solid var(--cardline);border-radius:14px;padding:18px 20px;margin-bottom:12px;display:flex;gap:18px;align-items:center;flex-wrap:wrap}
.card .info{flex:1 1 320px;min-width:0}.card h3{margin:0 0 4px;font-size:15px;font-weight:600}.card p{margin:0;color:#8a92a0;font-size:12.5px;line-height:1.5}
.card .acts{display:flex;gap:8px;flex-wrap:wrap;align-items:center}
.tag{display:inline-block;font-size:10.5px;border:1px solid #333a46;border-radius:999px;padding:2px 9px;margin-left:8px;color:#9aa3b2;vertical-align:middle}
.tag.good{border-color:#2c7a4b;color:#7fd6a0}.tag.warn{border-color:#8a6a1f;color:#e0b050}.tag.bad{border-color:#8a2f2f;color:#e57373}
.field{display:flex;gap:8px;flex:1 1 100%;flex-wrap:wrap}.field input{flex:1 1 320px}
.steps{margin:10px 0 0;color:#8a92a0;font-size:12.5px;line-height:1.6}
</style><h1>Extensions</h1>
<div class='muted'>Add features to the browser. Every extension is checked before it runs: identified by a hash of its files, judged against a written policy, and recorded in a tamper-evident ledger. If a single byte changes after you approve it, it stops loading.</div>
<div id='msg' class='u' style='min-height:18px;margin:0 0 10px'></div>
<div id='support'></div>
<div id='pending'></div>

<h1 style='font-size:16px'>Get extensions</h1>
<div class='card'><div class='info'><h3>Chrome Web Store <span class='tag good'>best match</span></h3><p>The largest catalogue, and the closest fit because Recognition runs on the Chromium engine. Open an extension's page, copy its address, then paste it in Add by link.</p></div>
 <div class='acts'><a class='btn' data-c='ext-open:https%3A%2F%2Fchromewebstore.google.com%2Fcategory%2Fextensions'>Browse extensions</a></div></div>
<div class='card'><div class='info'><h3>Edge Add-ons <span class='tag good'>works well</span></h3><p>Microsoft's store uses the same extension format as Chrome. Copy the address of an add-on's page and paste it in Add by link.</p></div>
 <div class='acts'><a class='btn' data-c='ext-open:https%3A%2F%2Fmicrosoftedge.microsoft.com%2Faddons'>Browse add-ons</a></div></div>
<div class='card'><div class='info'><h3>Firefox Add-ons <span class='tag warn'>may not run</span></h3><p>Paste a Firefox add-on page address and Recognition will look it up and download it. Firefox add-ons are built for Firefox, so many will not work in this engine. You get a review and a Test button before anything is installed.</p></div>
 <div class='acts'><a class='btn ghost' data-c='ext-open:https%3A%2F%2Faddons.mozilla.org%2Fen-US%2Ffirefox%2Fextensions%2F'>Browse add-ons</a></div></div>
<div class='card'><div class='info'><h3>Themes</h3><p>A theme is not run as an extension. Recognition reads its colours and pictures and applies them as its own look (see Customize). Firefox themes bring their header picture across the top of the window, and animated ones play. Paste a theme's page address in Add by link.</p></div>
 <div class='acts'><a class='btn ghost' data-c='ext-open:https%3A%2F%2Faddons.mozilla.org%2Ffirefox%2Fthemes%2F'>Firefox themes</a><a class='btn ghost' data-c='ext-open:https%3A%2F%2Fchromewebstore.google.com%2Fcategory%2Fthemes'>Chrome themes</a></div></div>
<div class='steps'>How it works: 1. Open a store above. 2. Copy the address of the extension's page. 3. Open the Add by link tab and paste it. 4. Review what it asks for, then install.</div>

<h1 style='font-size:16px'>Add by link or file</h1>
<div class='card'><div class='info'><h3>Paste an address</h3><p>A Chrome Web Store, Edge Add-ons or Firefox Add-ons page address, a 32-letter extension id, or a direct .crx, .zip or .xpi address.</p></div>
 <div class='field'><input id='url' placeholder='https://chromewebstore.google.com/detail/...'><a class='btn' id='dl'>Download and review</a></div></div>
<div class='card'><div class='info'><h3>From a file</h3><p>Choose an extension package you already have (.crx, .zip or .xpi).</p></div><div class='acts'><a class='btn ghost' data-c='ext-pick-file'>Choose a file...</a></div></div>
<div class='card'><div class='info'><h3>From a folder</h3><p>Choose an unpacked extension folder (the one that contains manifest.json) to install it. Writing your own? Use the Developer tab to check, run, reload and package builds first.</p></div><div class='acts'><a class='btn ghost' data-c='ext-pick-folder'>Choose a folder...</a></div></div>

" + ExtDevSectionHtml + @"

<h1 style='font-size:16px'>My installed</h1>
<div id='list'></div>

<h1 style='font-size:16px'>Try a demo</h1>
<div class='muted'>Two small built-in examples so you can see the whole flow without installing anything real.</div>
<div class='card'><div class='info'><h3>Demo: safe extension <span class='tag good'>no review needed</span></h3><p>Asks for almost nothing. Shows what an easy install looks like.</p></div><div class='acts'><a class='btn' data-c='ext-demo:hello-good'>Load for review</a></div></div>
<div class='card'><div class='info'><h3>Demo: asks for sensitive access <span class='tag warn'>needs your approval</span></h3><p>Asks to see your tabs. Shows how the review step explains what an extension wants before you approve it.</p></div><div class='acts'><a class='btn ghost' data-c='ext-demo:hello-review'>Load for review</a></div></div>

<h1 style='font-size:16px'>Safety and ledger</h1>
<div class='card'><div class='info'><h3>Governance ledger</h3><p id='ledger'>Checking...</p></div><div class='acts'><a class='btn ghost' data-c='ext-verify-chain'>Verify ledger</a><a class='btn ghost' data-c='ext-open-policy'>Open the policy folder</a></div></div>
<div class='card'><div class='info'><h3>What is refused</h3><p>Extensions that ask for broad access (all sites, debugging, proxy control, native messaging) are refused. Ones that ask for sensitive access (tabs, cookies, history, downloads, request blocking) need your explicit approval. Extensions the browser did not approve are removed at startup.</p></div></div>
<div class='card'><div class='info'><h3>Limits</h3><p>Recognition uses Chromium through Windows WebView2, which supports only part of the extension system. Content scripts and background workers generally work; toolbar buttons, popups and some APIs may not. Use Test before relying on an extension.</p></div></div>
<script>
function $(i){return document.getElementById(i)}
function el(t,txt,cls){var e=document.createElement(t);if(txt!=null)e.textContent=txt;if(cls)e.className=cls;return e}
var enc=encodeURIComponent;
function fromUrl(){var u=$('url').value.trim();if(u)send('ext-url:'+enc(u))}
document.addEventListener('click',function(e){var a=e.target.closest('[data-c]');if(a)send(a.getAttribute('data-c'))});
$('dl').onclick=fromUrl;$('url').addEventListener('keydown',function(e){if(e.key==='Enter')fromUrl()});
function btn(t,cmd,ghost){var a=el('a',t,'btn'+(ghost?' ghost':''));a.onclick=function(){send(cmd)};return a}
function kb(n){return n>1048576?(n/1048576).toFixed(1)+' MB':Math.max(1,Math.round(n/1024))+' KB'}
function listBlock(parent,title,items){if(!items||!items.length)return;parent.appendChild(el('div',title,'t'));var d=el('div',items.join('   '),'u');d.style.wordBreak='break-all';parent.appendChild(d)}
function card(title,text,tag,tagcls){var c=el('div',null,'card');var i=el('div',null,'info');var h=el('h3',title);if(tag){h.appendChild(el('span',tag,'tag '+(tagcls||'')))}i.appendChild(h);if(text)i.appendChild(el('p',text));c.appendChild(i);return c}
function render(m){
  var s=$('support');s.textContent='';
  var sc=card('Extension support: '+(m.support?'ON':'OFF'),m.restartNeeded?'Restart Recognition to apply your change.':(m.support?'Approved extensions load when the browser starts.':'No extensions run while this is off.'),m.support?'on':'off',m.support?'good':'warn');
  var sa=el('div',null,'acts');sa.appendChild(btn(m.supportSaved||m.supportConfig?'Turn off':'Turn on','ext-support:'+(m.supportSaved||m.supportConfig?'off':'on'),true));
  if(m.restartNeeded)sa.appendChild(btn('Restart Recognition','ext-restart'));sc.appendChild(sa);s.appendChild(sc);
  var P=$('pending');P.textContent='';var p=m.pending;
  if(p){var r=card('Review: '+p.name+'  '+p.version,p.description,p.decision.toUpperCase(),p.decision==='deny'?'bad':(p.decision==='review'?'warn':'good'));r.style.display='block';r.style.borderColor='#3b6bff';var i=r.firstChild;
    i.appendChild(el('div','Source: '+p.source+'  ·  manifest v'+p.mv+'  ·  '+p.files+' files, '+kb(p.bytes)+'  ·  identity '+p.id.slice(0,16)+'…','u'));
    if(p.reasons.length)i.appendChild(el('div','Why: '+p.reasons.join('; '),'u'));
    listBlock(i,'Permissions',p.permissions);listBlock(i,'Site access (host permissions)',p.hosts);listBlock(i,'Content scripts will run on (not scored by the policy)',p.contentScripts);listBlock(i,'Optional permissions it may ask for later (not scored)',p.optional);
    var a=el('div',null,'acts');a.style.marginTop='14px';
    if(p.theme){i.appendChild(el('div','This is a theme. Recognition cannot run Chrome themes in the engine, but it can use the theme colours and picture for its own look.','t'));a.appendChild(btn('Use this theme','ext-theme-apply'));}
    else if(p.decision==='deny'){a.appendChild(el('span','Refused by policy: it cannot be installed here.','u'));}
    else{ if(p.decision==='review')a.appendChild(btn('I reviewed these permissions: install','ext-install:approve'));else a.appendChild(btn('Install','ext-install:plain'));
          a.appendChild(btn('Test first (temporary profile)','ext-test-pending',true)); }
    a.appendChild(btn('Discard','ext-cancel',true));r.appendChild(a);P.appendChild(r);}
  var L=$('list');L.textContent='';
  if(!m.items.length)L.appendChild(card('Nothing installed yet','Find one under Get extensions, or try a demo.'));
  m.items.forEach(function(x){var c=card(x.name+'  '+x.version,x.status+'  ·  from '+x.source,x.enabled?'on':'off',x.enabled?'good':'warn');
    var acts=el('div',null,'acts');acts.appendChild(btn(x.enabled?'Disable':'Enable','ext-toggle:'+x.gid,true));acts.appendChild(btn('Test','ext-test:'+x.gid,true));acts.appendChild(btn('Remove','ext-remove:'+x.gid,true));c.appendChild(acts);L.appendChild(c)});
  renderDev(m.dev);
  $('ledger').textContent=m.ledger.error?('PROBLEM: '+m.ledger.error+'. Extensions will not load until this is resolved.'):(m.ledger.count+' records, head '+(m.ledger.head||'').slice(0,16)+'…')}
" + ExtDevScript + @"
window.chrome.webview.addEventListener('message',function(ev){var m=ev.data;if(!m)return;
  if(m.type==='snapshot')render(m);else if(m.type==='action'){$('msg').textContent=m.text;$('msg').style.color=m.ok?'':'#e57373'}});
window.addEventListener('DOMContentLoaded',function(){send('ext-state')});
</script>");
            sb.Append(SendScript()).Append(PageFoot);
            return sb.ToString();
        }
    }
}
