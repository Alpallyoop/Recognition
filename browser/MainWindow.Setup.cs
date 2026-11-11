using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Recognition.Browser
{
    // Setup snapshot page (recognition:setup): copy how this Recognition is set up to another device with a
    // temporary code. No account, no server. The snapshot is an encrypted text blob (or file); the code is shown
    // once and never stored. Crypto, validation and expiry logic live in SetupSnapshot.cs (executed by browser.tests).
    // Nothing is applied until you have previewed what it contains and clicked Apply.
    public partial class MainWindow
    {
        private sealed class PendingSetup { public SetupPlan Plan = new(); public string Id = ""; public DateTime Expires; }
        private PendingSetup? _setupPending;
        private static readonly string[] SetupCategories = { "appearance", "preferences", "bookmarks", "site_permissions", "network_labels", "cert_trust", "logins", "cookies", "kept" };
        // The categories that carry sign-in power. Creating a bundle with any of them requires the vault unlocked and caps the bundle's
        // lifetime (SetupCreateAsync), because a bundle that holds credentials is as sensitive as the passwords themselves.
        private static readonly string[] CredentialCategories = { "logins", "cookies" };

        private void MenuSetup_Click(object sender, RoutedEventArgs e) => OpenInternalInActiveTab("setup");

        private void PostToSetupPage(object payload)
        {
            var t = Active; if (t == null || t.Internal != "setup") return;
            try { t.Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload)); } catch { }
        }

        private Dictionary<string, object> BuildSetupCategories(ISet<string> chosen, IReadOnlyList<ImportedCookie>? liveCookies = null)
        {
            var cats = new Dictionary<string, object>();
            if (chosen.Contains("appearance")) cats["appearance"] = JsonSerializer.Deserialize<JsonElement>(_appearance.ToJson());
            if (chosen.Contains("preferences")) cats["preferences"] = new { home_url = _homeUrl, blocking_enabled = _blockingEnabled };
            if (chosen.Contains("bookmarks")) cats["bookmarks"] = _bookmarks.Select(b => new { url = b.Url, title = b.Title }).ToList();
            foreach (var cat in new[] { "site_permissions", "network_labels", "cert_trust" })
            {
                if (!chosen.Contains(cat)) continue;
                var list = new List<object>();
                foreach (var kv in _sitePolicyState)
                {
                    var i = kv.Key.IndexOf('|'); if (i <= 0) continue;
                    var key = kv.Key.Substring(0, i); var origin = kv.Key.Substring(i + 1);
                    if (SetupValidate.SiteCategory(key) == cat && SetupValidate.IsValidSiteEntry(key, origin, kv.Value))
                        list.Add(new { key, origin, value = kv.Value });
                }
                cats[cat] = list;
            }
            // Saved logins: only the normal vault is carried. SoteriaVault manages its own store and is not exported here (the caller
            // notes that on create). The caller guarantees the vault is unlocked before this runs.
            if (chosen.Contains("logins") && !SoteriaPmActive)
            {
                EnsureVault();
                cats["logins"] = LoginRules.Serialize(_vault.Entries.Select(e => new BundleLogin(e.Origin, e.Username, e.Password, e.Note)));
            }
            // Live cookies: read asynchronously by the caller and passed in (BuildSetupCategories is otherwise synchronous).
            if (chosen.Contains("cookies") && liveCookies != null)
                cats["cookies"] = CookieBundleRules.Serialize(liveCookies, DateTime.UtcNow);
            // Kept extensions / themes: a list only, never the files (proposal 0011).
            if (chosen.Contains("kept"))
            {
                var items = new List<KeptManifestItem>();
                foreach (var kind in new[] { "extension", "theme", "pack", "devbuild" })
                    foreach (var e in Kept.InUse(KeptList, kind))
                        items.Add(new KeptManifestItem(e.Kind, e.Id, e.Name, e.Version, e.Source));
                cats["kept"] = KeptManifestRules.Serialize(items);
            }
            return cats;
        }

        // Read every cookie in the normal profile's store (null host = all). Returns the bundle shape; empty when there is no normal
        // profile open yet. Mirrors the read the Library page uses.
        private async Task<List<ImportedCookie>> ReadLiveCookiesAsync()
        {
            var res = new List<ImportedCookie>();
            var core = MainCore(); if (core == null) return res;
            var list = await core.CookieManager.GetCookiesAsync(null);
            foreach (var c in list)
            {
                res.Add(new ImportedCookie
                {
                    Host = c.Domain ?? "",
                    Name = c.Name ?? "",
                    Value = c.Value ?? "",
                    Path = string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
                    Secure = c.IsSecure,
                    HttpOnly = c.IsHttpOnly,
                    SameSite = c.SameSite switch { CoreWebView2CookieSameSiteKind.Strict => "strict", CoreWebView2CookieSameSiteKind.None => "none", _ => "lax" },
                    ExpiresUtc = c.IsSession ? null : c.Expires.ToUniversalTime()
                });
                if (res.Count >= CookieBundleRules.Max) break;
            }
            return res;
        }

        private async Task SetupCreateAsync(string json)
        {
            try
            {
                using var d = JsonDocument.Parse(json);
                var chosen = new HashSet<string>(d.RootElement.GetProperty("categories").EnumerateArray().Select(x => x.GetString() ?? "").Where(x => SetupCategories.Contains(x)));
                int minutes = d.RootElement.GetProperty("minutes").GetInt32();
                if (chosen.Count == 0) { PostToSetupPage(new { type = "action", ok = false, text = "choose at least one category" }); return; }
                if (minutes != 15 && minutes != 60 && minutes != 1440 && minutes != 10080) { PostToSetupPage(new { type = "action", ok = false, text = "invalid expiry" }); return; }

                var hasCredentials = chosen.Overlaps(CredentialCategories);
                // A bundle that carries credentials is as sensitive as the passwords in it: cap its lifetime, and never build one while
                // the vault is locked (so a bundle is only ever made by someone who can already see those logins).
                if (hasCredentials && minutes > 1440) { PostToSetupPage(new { type = "action", ok = false, text = "a bundle that includes logins or cookies can be valid for at most 24 hours — choose a shorter expiry" }); return; }
                if (hasCredentials && _pin.IsSet && !EnsureUnlocked("Create a bundle that includes your saved logins and sign-in cookies.")) { PostToSetupPage(new { type = "action", ok = false, text = "the vault stayed locked; nothing was exported" }); return; }

                var notes = new List<string>();
                if (chosen.Contains("logins") && SoteriaPmActive) { chosen.Remove("logins"); notes.Add("your logins are managed by SoteriaVault and are not carried in a bundle"); }

                List<ImportedCookie>? liveCookies = null;
                if (chosen.Contains("cookies"))
                {
                    if (MainCore() == null) { PostToSetupPage(new { type = "action", ok = false, text = "open a normal tab first, then create the bundle (cookies are read from the open profile)" }); return; }
                    liveCookies = await ReadLiveCookiesAsync();
                }

                var cats = BuildSetupCategories(chosen, liveCookies);
                if (cats.Count == 0) { PostToSetupPage(new { type = "action", ok = false, text = "nothing to carry: " + (notes.Count > 0 ? string.Join("; ", notes) : "choose a category with content") }); return; }
                var now = DateTime.UtcNow;
                var id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
                var payload = SetupPayload.Build(id, now, now.AddMinutes(minutes), cats);
                var code = SetupCode.Generate();
                var blob = await Task.Run(() => SetupBlob.Seal(payload, code));
                _actions?.Append("setup.create", id + ":" + string.Join(",", cats.Keys.OrderBy(x => x)));   // receipt is hash-only; code and values are never logged
                PostToSetupPage(new { type = "created", code, blob, expires = now.AddMinutes(minutes).ToString("o"), categories = cats.Keys.OrderBy(x => x).ToList(), note = string.Join("; ", notes) });
            }
            catch (Exception ex) { PostToSetupPage(new { type = "action", ok = false, text = "could not create snapshot: " + ex.Message }); }
        }

        private async Task SetupPreviewAsync(string json)
        {
            try
            {
                using var d = JsonDocument.Parse(json);
                var blob = d.RootElement.GetProperty("blob").GetString() ?? "";
                var code = d.RootElement.GetProperty("code").GetString() ?? "";
                var res = await Task.Run(() => SetupBlob.Open(blob, code, DateTime.UtcNow));
                if (!res.Ok) { _setupPending = null; PostToSetupPage(new { type = "preview", ok = false, text = res.Reason }); return; }
                var plan = SetupPlan.Parse(res.PayloadJson);
                _setupPending = new PendingSetup { Plan = plan, Id = res.SnapshotId, Expires = res.ExpiresUtc };
                PostToSetupPage(new
                {
                    type = "preview", ok = true, created = res.CreatedUtc.ToString("o"), expires = res.ExpiresUtc.ToString("o"),
                    appearance = plan.Appearance != null, home = plan.HomeUrl, blocking = plan.BlockingEnabled,
                    bookmarks = plan.Bookmarks.Count, bookmarksRejected = plan.BookmarksRejected,
                    site_permissions = plan.SiteEntries.Count(x => x.Category == "site_permissions"),
                    network_labels = plan.SiteEntries.Count(x => x.Category == "network_labels"),
                    cert_trust = plan.SiteEntries.Count(x => x.Category == "cert_trust"),
                    logins = plan.Logins.Count, loginsRejected = plan.LoginsRejected,
                    cookies = plan.Cookies.Count, cookiesRejected = plan.CookiesRejected,
                    cookieSites = plan.Cookies.Select(c => c.DomainKey).Distinct().Count(),
                    kept = plan.KeptItems.Count, keptReinstallable = plan.KeptItems.Count(KeptManifestRules.HasReinstallSource),
                    rejected = plan.SiteRejected, unknown = plan.UnknownCategories
                });
            }
            catch (Exception ex) { PostToSetupPage(new { type = "preview", ok = false, text = "could not read snapshot: " + ex.Message }); }
        }

        private async Task SetupApplyAsync(string json)
        {
            var p = _setupPending;
            if (p == null) { PostToSetupPage(new { type = "action", ok = false, text = "nothing to apply: preview a snapshot first" }); return; }
            try
            {
                if (DateTime.UtcNow > p.Expires) { _setupPending = null; PostToSetupPage(new { type = "action", ok = false, text = "snapshot expired" }); return; }
                using var d = JsonDocument.Parse(json);
                var chosen = new HashSet<string>(d.RootElement.GetProperty("categories").EnumerateArray().Select(x => x.GetString() ?? ""));
                var done = new List<string>();
                var plan = p.Plan;
                if (chosen.Contains("appearance") && plan.Appearance != null)
                {
                    var a = plan.Appearance;
                    _appearance.Set("theme", a.Theme); _appearance.Set("darkstyle", a.DarkStyle); _appearance.Set("bg", a.PageBg);
                    _appearance.Set("text", a.PageText); _appearance.Set("link", a.LinkColor); _appearance.Set("font", a.Font);
                    _appearance.Set("overridefonts", a.OverrideSiteFonts ? "on" : "off");
                    done.Add("appearance");
                }
                if (chosen.Contains("preferences"))
                {
                    if (plan.HomeUrl != null) _homeUrl = plan.HomeUrl;
                    if (plan.BlockingEnabled.HasValue) _blockingEnabled = plan.BlockingEnabled.Value;
                    done.Add("preferences");
                }
                SaveSettings(); ApplyAppearanceAll(); UpdateShield();
                int added = 0;
                if (chosen.Contains("bookmarks"))
                {
                    foreach (var (url, title) in plan.Bookmarks)
                        if (!IsBookmarked(url)) { _bookmarks.Add(new Bookmark { Url = url, Title = title, Ts = Iso(DateTime.UtcNow) }); added++; }
                    SaveBookmarks(); done.Add("bookmarks(+" + added + ")");
                }
                foreach (var cat in new[] { "site_permissions", "network_labels", "cert_trust" })
                {
                    if (!chosen.Contains(cat)) continue;
                    var es = plan.SiteEntries.Where(x => x.Category == cat).ToList();
                    foreach (var (key, origin, value, _) in es) SitePolicySet(origin, key, value);
                    done.Add(cat + "(" + es.Count + ")");
                }
                // Saved logins: added to the vault (never overwriting a password you already have). Requires the vault unlocked, like any
                // other write to it. A login already saved with the same password is a duplicate and skipped.
                if (chosen.Contains("logins") && plan.Logins.Count > 0)
                {
                    if (SoteriaPmActive) done.Add("logins(skipped: SoteriaVault manages logins)");
                    else if (_pin.IsSet && !EnsureUnlocked("Add " + plan.Logins.Count + " saved login(s) from the bundle.")) done.Add("logins(skipped: vault locked)");
                    else
                    {
                        EnsureVault();
                        var existing = _vault.Entries.Select(e => (e.Origin, e.Username, e.Password)).ToList();
                        var toAdd = LoginRules.ToAdd(plan.Logins, existing, out var dups);
                        int la = 0;
                        foreach (var l in toAdd)
                            if (_vault.Add(l.Origin, l.Username, l.Password, l.Note, DateTime.UtcNow, out var addedE) == null && addedE != null)
                            { _actions?.Append("vault.add", addedE.Origin + ":" + addedE.Id); la++; }
                        done.Add("logins(+" + la + (dups > 0 ? ", " + dups + " already saved" : "") + ")");
                    }
                }
                // Live cookies: written to the open normal profile (additive). Mirrors importing cookies from another browser.
                if (chosen.Contains("cookies") && plan.Cookies.Count > 0)
                {
                    var core = MainCore();
                    if (core == null) done.Add("cookies(skipped: open a normal tab first)");
                    else
                    {
                        int ok = 0, failed = 0;
                        foreach (var c in plan.Cookies)
                        {
                            try
                            {
                                var cookie = core.CookieManager.CreateCookie(c.Name, c.Value, c.Host, c.Path);
                                cookie.IsSecure = c.Secure; cookie.IsHttpOnly = c.HttpOnly;
                                cookie.SameSite = c.SameSite switch { "strict" => CoreWebView2CookieSameSiteKind.Strict, "none" => CoreWebView2CookieSameSiteKind.None, _ => CoreWebView2CookieSameSiteKind.Lax };
                                if (c.ExpiresUtc is { } e) cookie.Expires = e.ToLocalTime();
                                core.CookieManager.AddOrUpdateCookie(cookie); ok++;
                            }
                            catch { failed++; }
                        }
                        _actions?.Append("cookies.bundle_apply", ok + (failed > 0 ? ":" + failed + "failed" : ""));
                        done.Add("cookies(+" + ok + (failed > 0 ? ", " + failed + " failed" : "") + ") — reload a site to see you are signed in");
                    }
                }
                await Task.CompletedTask;
                _actions?.Append("setup.apply", p.Id + ":" + string.Join(",", done));
                _setupPending = null;   // plan is dropped after applying; preview again to re-apply
                Status("setup applied: " + string.Join(", ", done));
                PostToSetupPage(new { type = "applied", text = "Applied: " + string.Join(", ", done) + ". Open Settings to review." });
            }
            catch (Exception ex) { PostToSetupPage(new { type = "action", ok = false, text = "apply failed: " + ex.Message }); }
        }

        private void HandleSetupMessage(string msg)
        {
            string Dec(string s) { try { return Uri.UnescapeDataString(s); } catch { return ""; } }
            if (msg.StartsWith("setup-create:")) _ = SetupCreateAsync(Dec(msg.Substring("setup-create:".Length)));
            else if (msg.StartsWith("setup-preview:")) _ = SetupPreviewAsync(Dec(msg.Substring("setup-preview:".Length)));
            else if (msg.StartsWith("setup-apply:")) _ = SetupApplyAsync(Dec(msg.Substring("setup-apply:".Length)));
            else if (msg == "setup-openfile")
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Open a Recognition setup snapshot", Filter = "Recognition setup (*.rsetup;*.txt)|*.rsetup;*.txt|All files|*.*" };
                if (dlg.ShowDialog(this) == true)
                {
                    try
                    {
                        var fi = new FileInfo(dlg.FileName);
                        if (fi.Length > 12 * 1024 * 1024) { PostToSetupPage(new { type = "action", ok = false, text = "file too large to be a setup snapshot" }); return; }
                        PostToSetupPage(new { type = "loaded", blob = File.ReadAllText(dlg.FileName).Trim() });
                    }
                    catch (Exception ex) { PostToSetupPage(new { type = "action", ok = false, text = ex.Message }); }
                }
            }
            else if (msg.StartsWith("setup-savefile:"))
            {
                var blob = Dec(msg.Substring("setup-savefile:".Length));
                if (!blob.StartsWith(SetupBlob.Prefix, StringComparison.Ordinal)) return;
                var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Save setup snapshot", FileName = "recognition-setup.rsetup", Filter = "Recognition setup (*.rsetup)|*.rsetup" };
                if (dlg.ShowDialog(this) == true)
                {
                    try { File.WriteAllText(dlg.FileName, blob, new UTF8Encoding(false)); PostToSetupPage(new { type = "action", ok = true, text = "saved. Send the code separately." }); }
                    catch (Exception ex) { PostToSetupPage(new { type = "action", ok = false, text = ex.Message }); }
                }
            }
        }

        private string SetupHtml()
        {
            var sb = new StringBuilder(PageHead);
            sb.Append(@"<title>Setup snapshot</title><h1>Setup snapshot</h1>
<div class='muted'>Copy how Recognition is set up — and, if you choose, what you brought into it — to another device. No account and no server: you get an encrypted bundle and a temporary code. Send them by <b>different routes</b>. A bundle is only made when you click Create; nothing is kept ready, and the code is shown once and never stored. It can be applied on any device, any number of times, until it expires. Saved logins and sign-in cookies are <b>off by default</b> and carry sign-in power: a bundle that includes them is as sensitive as your passwords, so it expires within 24 hours and anyone holding both the file and the code can use it. Treat that file like a password.</div>
<div id='msg' class='u' style='min-height:18px;margin:8px 0'></div>

<h1 style='font-size:16px'>Create a snapshot on this device</h1>
<div id='cats'></div>
<div style='margin:8px 0'>Valid for
 <select id='mins' style='background:#0e1015;color:#e8e8e8;border:1px solid #333844;border-radius:6px;padding:5px 6px'>
  <option value='15'>15 minutes</option><option value='60' selected>1 hour</option><option value='1440'>24 hours</option><option value='10080'>7 days</option></select>
 &nbsp;<a class='btn' onclick='create()'>Create snapshot</a></div>
<div class='u' style='margin:-2px 0 8px'>A bundle that includes saved logins or sign-in cookies is limited to 24 hours.</div>
<div id='made' style='display:none'>
 <div class='row'><div><div class='t'>One-time code</div><div class='u'>Write this down or read it out. It is not stored anywhere.</div></div><div class='ts'><span class='big' id='code' style='letter-spacing:2px'></span></div></div>
 <div class='u' style='margin:6px 0'>Snapshot (encrypted text, safe to paste or send):</div>
 <textarea id='blobout' readonly style='width:100%;height:80px;background:#0e1015;color:#e8e8e8;border:1px solid #333844;border-radius:6px;padding:8px'></textarea>
 <div style='margin:6px 0 18px'><a class='btn ghost' onclick='copyBlob()'>Copy</a> <a class='btn ghost' onclick='saveBlob()'>Save as file&hellip;</a> <span class='u' id='exp'></span></div>
</div>

<h1 style='font-size:16px'>Apply a snapshot from another device</h1>
<div class='muted'>Paste the snapshot (or open the file) and enter the code. You will see what it contains before anything changes.</div>
<textarea id='blobin' placeholder='RSETUP1:...' style='width:100%;height:70px;background:#0e1015;color:#e8e8e8;border:1px solid #333844;border-radius:6px;padding:8px;margin-top:8px'></textarea>
<div style='margin:6px 0;display:flex;gap:10px;flex-wrap:wrap;align-items:center'>
 <a class='btn ghost' onclick=""send('setup-openfile')"">Open file&hellip;</a>
 <input id='codein' placeholder='XXXXX-XXXXX-XXXXX-XXXXX' style='background:#0e1015;color:#e8e8e8;border:1px solid #333844;border-radius:6px;padding:8px 10px;width:250px;letter-spacing:1px'>
 <a class='btn' onclick='preview()'>Preview</a></div>
<div id='prev'></div>
<script>
function $(i){return document.getElementById(i)}
function el(t,txt,cls){var e=document.createElement(t);if(txt!=null)e.textContent=txt;if(cls)e.className=cls;return e}
var enc=encodeURIComponent;
var CATS=[['appearance','Appearance (theme, colours, fonts)',true],['preferences','Preferences (home page, tracker blocking)',true],['bookmarks','Bookmarks',true],
 ['site_permissions','Per-site permissions and tracker exemptions',true],['network_labels','Network labels (trusted / guest / blocked)',true],['cert_trust','Pinned certificate exceptions (sensitive)',false],
 ['logins','Saved logins — passwords (sensitive: signs in as you)',false],['cookies','Sign-in cookies — current sessions (sensitive: signs in as you)',false],
 ['kept','Kept extensions &amp; themes (a list; re-installed on the other device)',false]];
function drawCats(){var c=$('cats');CATS.forEach(function(x){var l=el('label');l.style.display='block';l.style.margin='4px 0';var i=el('input');i.type='checkbox';i.id='c_'+x[0];i.checked=x[2];l.appendChild(i);l.appendChild(document.createTextNode(' '+x[1]));c.appendChild(l)})}
drawCats();
function create(){var cs=CATS.filter(function(x){return $('c_'+x[0]).checked}).map(function(x){return x[0]});
  send('setup-create:'+enc(JSON.stringify({categories:cs,minutes:parseInt($('mins').value,10)})))}
function copyBlob(){var t=$('blobout');t.select();try{document.execCommand('copy');$('msg').textContent='copied'}catch(e){}}
function saveBlob(){send('setup-savefile:'+enc($('blobout').value))}
function preview(){send('setup-preview:'+enc(JSON.stringify({blob:$('blobin').value,code:$('codein').value})))}
function applySel(){var cs=CATS.filter(function(x){var b=$('a_'+x[0]);return b&&b.checked}).map(function(x){return x[0]});
  send('setup-apply:'+enc(JSON.stringify({categories:cs})))}
function showPrev(m){var p=$('prev');p.textContent='';
  if(!m.ok){p.appendChild(el('div',m.text,'empty'));return}
  p.appendChild(el('div','Created '+new Date(m.created).toLocaleString()+' · expires '+new Date(m.expires).toLocaleString(),'u'));
  var rows=[['appearance',m.appearance?'included':'',m.appearance],['preferences',(m.home||m.blocking!==null)?('home: '+(m.home||'unchanged')+(m.blocking===null?'':' · blocking '+(m.blocking?'on':'off'))):'',!!(m.home||m.blocking!==null)],
   ['bookmarks',m.bookmarks+' to merge'+(m.bookmarksRejected?' ('+m.bookmarksRejected+' invalid skipped)':''),m.bookmarks>0],
   ['site_permissions',m.site_permissions+' entries',m.site_permissions>0],['network_labels',m.network_labels+' entries',m.network_labels>0],['cert_trust',m.cert_trust+' entries (grants trust to those certificates)',m.cert_trust>0],
   ['logins',m.logins+' saved login(s) — added to your vault, never overwriting a password you have'+(m.loginsRejected?' ('+m.loginsRejected+' invalid skipped)':''),m.logins>0],
   ['cookies',m.cookies+' sign-in cookie(s) across '+m.cookieSites+' site(s) — can sign you in to those sites'+(m.cookiesRejected?' ('+m.cookiesRejected+' invalid/expired skipped)':''),m.cookies>0]];
  var optIn={cert_trust:1,logins:1,cookies:1};
  rows.forEach(function(r){var cat=CATS.filter(function(x){return x[0]===r[0]})[0];if(!r[2])return;
    var l=el('label');l.style.display='block';l.style.margin='4px 0';var i=el('input');i.type='checkbox';i.id='a_'+r[0];i.checked=!optIn[r[0]];l.appendChild(i);l.appendChild(document.createTextNode(' '+cat[1]+' — '+r[1]));p.appendChild(l)});
  if(m.kept>0){p.appendChild(el('div','Kept extensions & themes: '+m.kept+' listed ('+m.keptReinstallable+' can be re-installed from their source here; the rest you carry the files yourself). Open the Extensions and Customize pages to add them — they are not installed automatically.','u'))}
  if(m.rejected)p.appendChild(el('div',m.rejected+' invalid entr'+(m.rejected===1?'y':'ies')+' dropped','u'));
  if(m.unknown&&m.unknown.length)p.appendChild(el('div','Ignored unknown categories: '+m.unknown.join(', '),'u'));
  var b=el('a','Apply selected','btn');b.style.marginTop='8px';b.onclick=applySel;p.appendChild(b)}
window.chrome.webview.addEventListener('message',function(e){var m=e.data;if(!m)return;
  if(m.type==='created'){$('made').style.display='block';$('code').textContent=m.code;$('blobout').value=m.blob;$('exp').textContent=' expires '+new Date(m.expires).toLocaleString();$('msg').textContent='Bundle created'+(m.categories&&m.categories.length?' ('+m.categories.join(', ')+')':'')+'.'+(m.note?' Note: '+m.note+'.':'')}
  else if(m.type==='loaded'){$('blobin').value=m.blob}
  else if(m.type==='preview')showPrev(m);
  else if(m.type==='applied'){$('prev').textContent='';$('msg').textContent=m.text}
  else if(m.type==='action'){$('msg').textContent=m.text}});
</script>");
            sb.Append(SendScript()).Append(PageFoot);
            return sb.ToString();
        }
    }
}
