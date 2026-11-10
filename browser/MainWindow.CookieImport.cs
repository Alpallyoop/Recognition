using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Recognition.Browser
{
    // Importing your own cookies from another browser on this computer (rules and formats: CookieImport.cs; SQLite reading:
    // SqliteReader.cs). This is a migration helper, like Import bookmarks: cookies are your sign-ins, so Recognition reads
    // them only from browsers installed under your own Windows account, asks you before importing a source, writes a receipt,
    // and never imports into a private tab. It only reads the other browser's files; it never changes them.
    public partial class MainWindow
    {
        private sealed class CookieSource
        {
            public string Id = "", Label = "", Browser = "";
            public Recognition.Browser.CookieSource Kind;
            public string DbPath = "", LocalStatePath = "";
            public List<ImportedCookie> Cookies = new();
            public CookieSummary Summary;
            public string Problem = "";
        }

        private List<CookieSource>? _cookieScan;

        private static byte[]? DpapiUnprotect(byte[] blob)
        {
            try { return ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser); } catch { return null; }
        }

        // Reads a file another program may have open (the browser keeps its cookie database locked).
        private static byte[] ReadShared(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > CookieImport.MaxDbBytes) throw new IOException("that cookie database is larger than " + CookieImport.MaxDbBytes / 1024 / 1024 + " MB");
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            return ms.ToArray();
        }

        // ---- finding the installed browsers ------------------------------------------------------------------------------

        private List<CookieSource> DiscoverCookieSources()
        {
            var found = new List<CookieSource>();
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            // Chromium family: <base>\User Data\<Profile>\{Network\Cookies | Cookies}, key in <base>\User Data\Local State
            var chromium = new (string Browser, string Rel)[]
            {
                ("Google Chrome", @"Google\Chrome\User Data"),
                ("Microsoft Edge", @"Microsoft\Edge\User Data"),
                ("Brave", @"BraveSoftware\Brave-Browser\User Data"),
                ("Chromium", @"Chromium\User Data"),
                ("Vivaldi", @"Vivaldi\User Data"),
                ("Opera", @"..\Roaming\Opera Software\Opera Stable"),
                ("Opera GX", @"..\Roaming\Opera Software\Opera GX Stable"),
            };
            foreach (var (browser, rel) in chromium)
            {
                var userData = Path.GetFullPath(Path.Combine(local, rel));
                if (!Directory.Exists(userData)) continue;
                var localState = Path.Combine(userData, "Local State");
                foreach (var profile in ChromiumProfiles(userData))
                {
                    var db = new[] { Path.Combine(profile, "Network", "Cookies"), Path.Combine(profile, "Cookies") }.FirstOrDefault(File.Exists);
                    if (db == null) continue;
                    found.Add(new CookieSource { Browser = browser, Kind = Recognition.Browser.CookieSource.Chromium, DbPath = db, LocalStatePath = localState, Label = browser + " — " + Path.GetFileName(profile) });
                }
            }

            // Firefox: %APPDATA%\Mozilla\Firefox\Profiles\<name>\cookies.sqlite
            var ffProfiles = Path.Combine(roaming, @"Mozilla\Firefox\Profiles");
            if (Directory.Exists(ffProfiles))
                foreach (var profile in SafeDirs(ffProfiles))
                {
                    var db = Path.Combine(profile, "cookies.sqlite");
                    if (File.Exists(db)) found.Add(new CookieSource { Browser = "Firefox", Kind = Recognition.Browser.CookieSource.Firefox, DbPath = db, Label = "Firefox — " + Path.GetFileName(profile) });
                }

            for (int i = 0; i < found.Count; i++) found[i].Id = "src" + i;
            return found;
        }

        private static IEnumerable<string> ChromiumProfiles(string userData)
        {
            foreach (var d in SafeDirs(userData))
            {
                var name = Path.GetFileName(d);
                if (name == "Default" || name.StartsWith("Profile ", StringComparison.Ordinal))
                    yield return d;
            }
            // Opera and some builds keep the profile at the base
            if (File.Exists(Path.Combine(userData, "Cookies")) || File.Exists(Path.Combine(userData, "Network", "Cookies"))) yield return userData;
        }

        private static IEnumerable<string> SafeDirs(string parent)
        {
            string[] dirs; try { dirs = Directory.GetDirectories(parent); } catch { yield break; }
            foreach (var d in dirs) yield return d;
        }

        // ---- reading a source --------------------------------------------------------------------------------------------

        private List<ImportedCookie> ReadSource(CookieSource s)
        {
            var bytes = ReadShared(s.DbPath);
            if (s.Kind == Recognition.Browser.CookieSource.Firefox)
                return CookieImport.FromFirefox(SqliteReader.Read(bytes, "moz_cookies"));

            byte[]? aesKey = null;
            try
            {
                if (File.Exists(s.LocalStatePath))
                {
                    var blob = CookieImport.KeyBlobFromLocalState(File.ReadAllText(s.LocalStatePath, Encoding.UTF8));
                    if (blob != null) aesKey = DpapiUnprotect(CookieImport.StripDpapiPrefix(blob));
                }
            }
            catch { }
            return CookieImport.FromChromium(SqliteReader.Read(bytes, "cookies"), aesKey, DpapiUnprotect);
        }

        // ---- the Overview page: scan, then import on your click ----------------------------------------------------------

        private void PostToOverview(object payload)
        {
            var t = Active; if (t == null || t.Internal != "overview") return;
            try { t.Web.CoreWebView2.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(payload)); } catch { }
        }

        private async Task ScanCookieSourcesAsync()
        {
            PostToOverview(new { type = "import-scan", scanning = true });
            List<CookieSource> sources = new();
            await Task.Run(() =>
            {
                try { sources = DiscoverCookieSources(); } catch { sources = new(); }
                foreach (var s in sources)
                {
                    try { s.Cookies = ReadSource(s); s.Summary = CookieImport.Summarize(s.Cookies, DateTime.UtcNow); }
                    catch (Exception ex) { s.Problem = ex is SqliteException ? "its cookie database could not be read" : ex.Message; }
                }
            });
            _cookieScan = sources;
            PostScan();
        }

        private void PostScan()
        {
            var sources = _cookieScan ?? new();
            PostToOverview(new
            {
                type = "import-scan",
                categories = CookieImport.AllCategories.Select(c => new { key = CookieImport.CategoryKey(c), label = CookieImport.CategoryLabel(c), on = CookieImport.OnByDefault(c) }).ToList(),
                sources = sources.Select(s => new
                {
                    id = s.Id, label = s.Label, browser = s.Browser,
                    total = s.Summary.Total, sites = s.Summary.Sites, live = s.Summary.Live, failed = s.Summary.Failed,
                    cats = CookieImport.AllCategories.ToDictionary(c => CookieImport.CategoryKey(c), c => s.Summary.Cat(CookieImport.CategoryKey(c))),
                    top = CookieImport.TopSites(s.Cookies, 6), problem = s.Problem,
                }).ToList()
            });
        }

        // Scans once per session on its own; the page asks for this when it opens, so there is nothing to navigate to.
        private async Task EnsureCookieScanAsync(bool force)
        {
            if (_cookieScan != null && !force) { PostScan(); return; }
            await ScanCookieSourcesAsync();
        }

        private async Task ImportCookieSourceAsync(string id, ISet<string> categories)
        {
            var s = _cookieScan?.FirstOrDefault(x => x.Id == id);
            if (s == null) { PostToOverview(new { type = "import-done", ok = false, text = "that browser is no longer in the list; press Rescan." }); return; }
            var core = MainCore();
            if (core == null) { PostToOverview(new { type = "import-done", ok = false, text = "open a normal tab first, then import." }); return; }
            var now = DateTime.UtcNow;
            var live = CookieImport.Selected(s.Cookies, now, categories);
            if (live.Count == 0) { PostToOverview(new { type = "import-done", ok = false, text = "nothing to import from " + s.Label + " in the chosen categories." }); return; }

            var chosen = categories.Select(k => CookieImport.AllCategories.FirstOrDefault(c => CookieImport.CategoryKey(c) == k)).Distinct();
            var catWords = string.Join(", ", CookieImport.AllCategories.Where(c => categories.Contains(CookieImport.CategoryKey(c))).Select(c => CookieImport.CategoryLabel(c).ToLowerInvariant()));
            var confirm = MessageBox.Show(this,
                "Import " + live.Count + " cookie(s) from " + s.Label + " into Recognition?\n\n" +
                "Categories: " + catWords + ".\n" +
                "They cover " + live.Select(c => c.DomainKey).Distinct().Count() + " site(s). Cookies are your sign-ins, so this can sign you in to those sites in Recognition. " +
                "Only current cookies are imported; expired ones are skipped. The other browser is not changed." +
                (s.Summary.Failed > 0 ? "\n\n" + s.Summary.Failed + " cookie(s) could not be read and will be skipped." : ""),
                "Recognition — import cookies", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) { PostToOverview(new { type = "import-done", ok = false, text = "import cancelled." }); return; }

            int ok = 0, failed = 0;
            foreach (var c in live)
            {
                try
                {
                    var cookie = core.CookieManager.CreateCookie(c.Name, c.Value, c.Host, c.Path);
                    cookie.IsSecure = c.Secure;
                    cookie.IsHttpOnly = c.HttpOnly;
                    cookie.SameSite = c.SameSite switch { "strict" => CoreWebView2CookieSameSiteKind.Strict, "none" => CoreWebView2CookieSameSiteKind.None, _ => CoreWebView2CookieSameSiteKind.Lax };
                    if (c.ExpiresUtc is { } e) cookie.Expires = e.ToLocalTime();
                    core.CookieManager.AddOrUpdateCookie(cookie);
                    ok++;
                }
                catch { failed++; }
            }
            _actions?.Append("cookies.import", s.Browser + ":" + ok + ":[" + catWords.Replace(", ", "+") + "]" + (failed > 0 ? ":" + failed + "failed" : ""));
            Status("imported " + ok + " cookie(s) from " + s.Label);
            PostToOverview(new { type = "import-done", ok = true, text = "Imported " + ok + " cookie(s) from " + s.Label + "." + (failed > 0 ? " " + failed + " could not be added." : "") + " Reload a site to see that you are signed in." });
            await SendOverviewAsync(Active!);
        }

        private void MenuImportCookies_Click(object sender, RoutedEventArgs e)
        {
            OpenInternalInActiveTab("overview");
            _ = ScanAfterOpenAsync();
        }
        private async Task ScanAfterOpenAsync() { await Task.Delay(350); if (Active?.Internal == "overview") await EnsureCookieScanAsync(false); }

        static HashSet<string> ParseCats(string csv) =>
            new(csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(k => CookieImport.AllCategories.Any(c => CookieImport.CategoryKey(c) == k)), StringComparer.Ordinal);

        // Called from HandleOverviewMessage.
        private async Task<bool> HandleCookieImportMessageAsync(string msg)
        {
            if (msg == "ov-cookie-import-scan") { await EnsureCookieScanAsync(false); return true; }
            if (msg == "ov-cookie-rescan") { await EnsureCookieScanAsync(true); return true; }
            if (msg.StartsWith("ov-cookie-import:", StringComparison.Ordinal))
            {
                // ov-cookie-import:<id>:<cat,cat,...>
                var rest = msg.Substring("ov-cookie-import:".Length);
                var i = rest.IndexOf(':');
                var id = i < 0 ? rest : rest.Substring(0, i);
                var cats = i < 0 ? new HashSet<string>() : ParseCats(rest.Substring(i + 1));
                await ImportCookieSourceAsync(id, cats);
                return true;
            }
            return false;
        }
    }
}
