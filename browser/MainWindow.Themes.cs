using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Recognition.Browser
{
    // More looks: top bar styles, the theme library you can download from, themes you have added, and the backgrounds grid.
    // The rules (what a theme file may contain, which host the library may come from) are in ThemeLibrary.cs and are executed by tests.
    //  * Top bar styles change only the colours of the top area (toolbar, address field, tabs, bookmarks bar) on top of the current theme.
    //  * "Browse the library" asks GitHub (raw.githubusercontent.com only) for the list ONLY when you click; offline it shows the list that
    //    came with Recognition (themes\library.v1.json). A theme is just three colours and two names, checked before it is used.
    //  * Themes you add are kept as small files in runtime\themes.
    public partial class MainWindow
    {
        private string _topStyle = "";                                   // persisted: top_style ("" = match the theme)
        private List<ThemePack> _libraryPacks = new();                    // the list shown after "Browse the library" (not kept between runs)
        private string _libraryNote = "";

        private void LoadThemeExtraSettings(JsonElement r)
        {
            _topStyle = TopBarStyles.Normalize(r.TryGetProperty("top_style", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "");
        }

        private string ThemeExtraSettingsJson() => J("top_style") + ":" + J(_topStyle) + ",";

        private string ThemesDir => Path.Combine(_repoRoot, "runtime", "themes");

        // ---- themes you have added ---------------------------------------------------------------------------------------------
        private List<ThemePack> InstalledThemes()
        {
            var list = new List<ThemePack>();
            try
            {
                if (!Directory.Exists(ThemesDir)) return list;
                foreach (var f in Directory.EnumerateFiles(ThemesDir, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(ThemeLibrary.MaxPacks))
                {
                    try
                    {
                        if (new FileInfo(f).Length > ThemeLibrary.MaxPackBytes) continue;
                        var p = ThemeLibrary.ParsePack(File.ReadAllText(f, Encoding.UTF8));
                        if (p != null && string.Equals(Path.GetFileNameWithoutExtension(f), p.Id, StringComparison.Ordinal)) list.Add(p);
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        private bool SaveInstalledTheme(ThemePack p)
        {
            try
            {
                if (!ThemeLibrary.ValidId(p.Id)) return false;
                Directory.CreateDirectory(ThemesDir);
                File.WriteAllText(Path.Combine(ThemesDir, p.Id + ".json"), ThemeLibrary.ToJson(p), new UTF8Encoding(false));
                return true;
            }
            catch { return false; }
        }

        private void RemoveInstalledTheme(string id)
        {
            try { if (ThemeLibrary.ValidId(id)) { var f = Path.Combine(ThemesDir, id + ".json"); if (File.Exists(f)) File.Delete(f); } } catch { }
        }

        // ---- fetching the library (only on your click) -------------------------------------------------------------------------
        private async Task<string> FetchLibraryTextAsync()
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 3 }) { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Recognition-themes/1.0");
            using var resp = await http.GetAsync(ThemeLibrary.RemoteUrl, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("GitHub answered " + (int)resp.StatusCode);
            if (!ThemeLibrary.IsAllowedHost(resp.RequestMessage?.RequestUri)) throw new InvalidOperationException("the answer came from an address that is not allowed");
            if (resp.Content.Headers.ContentLength is long cl && cl > ThemeLibrary.MaxLibraryBytes) throw new InvalidOperationException("the list is larger than allowed");
            using var ms = new MemoryStream();
            using (var net = await resp.Content.ReadAsStreamAsync())
            {
                var buf = new byte[16384]; int n;
                while ((n = await net.ReadAsync(buf, 0, buf.Length)) > 0) { ms.Write(buf, 0, n); if (ms.Length > ThemeLibrary.MaxLibraryBytes) throw new InvalidOperationException("the list is larger than allowed"); }
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private string? LocalLibraryText()
        {
            try
            {
                var f = Path.Combine(_repoRoot, "themes", "library.v1.json");
                return File.Exists(f) && new FileInfo(f).Length <= ThemeLibrary.MaxLibraryBytes ? File.ReadAllText(f, Encoding.UTF8) : null;
            }
            catch { return null; }
        }

        // ---- messages from the Customize page ----------------------------------------------------------------------------------
        private async Task HandleThemeExtraAsync(BrowserTab tab, string msg, Action<string> note)
        {
            try
            {
                if (msg.StartsWith("cz-top:", StringComparison.Ordinal))
                {
                    var id = msg.Substring("cz-top:".Length);
                    if (TopBarStyles.Find(id) == null) return;
                    _topStyle = TopBarStyles.Normalize(id); SaveSettings(); ApplyChromeTheme();
                    _actions?.Append("theme.top", _topStyle.Length == 0 ? "match" : _topStyle);
                    note("top bar: " + TopBarStyles.Find(id)!.Name);
                }
                else if (msg == "cz-lib-fetch")
                {
                    note("asking GitHub for the theme list...");
                    string? text = null;
                    try { text = await FetchLibraryTextAsync(); _libraryNote = "Downloaded from GitHub just now."; }
                    catch (Exception ex) { _libraryNote = "Could not reach GitHub (" + ex.Message + "), showing the list that came with Recognition."; text = LocalLibraryText(); }
                    _libraryPacks = ThemeLibrary.ParseLibrary(text);
                    if (_libraryPacks.Count == 0) _libraryNote += " No usable themes were found in it.";
                    _actions?.Append("theme.library", _libraryPacks.Count.ToString());
                    note(_libraryPacks.Count + " themes available");
                }
                else if (msg.StartsWith("cz-lib-add:", StringComparison.Ordinal))
                {
                    var p = _libraryPacks.FirstOrDefault(x => x.Id == msg.Substring("cz-lib-add:".Length));
                    if (p == null || !SaveInstalledTheme(p)) { note("that theme could not be added"); return; }
                    ApplyPack(p); note("added and using \"" + p.Name + "\"");
                }
                else if (msg.StartsWith("cz-mine-apply:", StringComparison.Ordinal))
                {
                    var p = InstalledThemes().FirstOrDefault(x => x.Id == msg.Substring("cz-mine-apply:".Length));
                    if (p == null) { note("that theme is no longer there"); return; }
                    ApplyPack(p);
                }
                else if (msg.StartsWith("cz-mine-rm:", StringComparison.Ordinal))
                {
                    RemoveInstalledTheme(msg.Substring("cz-mine-rm:".Length)); note("theme removed (the current look stays until you pick another)");
                }
                else if (msg == "cz-pack-import")
                {
                    var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose a Recognition theme file", Filter = "Recognition theme (*.rtheme;*.json)|*.rtheme;*.json" };
                    if (dlg.ShowDialog(this) != true) return;
                    var fi = new FileInfo(dlg.FileName);
                    if (!fi.Exists || fi.Length > ThemeLibrary.MaxPackBytes) { note("that file is missing or too large for a theme"); return; }
                    var p = ThemeLibrary.ParsePack(await Task.Run(() => File.ReadAllText(dlg.FileName, Encoding.UTF8)));
                    if (p == null) { note("that is not a valid Recognition theme (or its text would be hard to read)"); return; }
                    if (!SaveInstalledTheme(p)) { note("that theme could not be added"); return; }
                    ApplyPack(p); note("added and using \"" + p.Name + "\"");
                }
                else return;
                RefreshCustomize(tab);
            }
            catch (Exception ex) { note("theme action failed: " + ex.Message); }
        }

        // ---- page pieces ----------------------------------------------------------------------------------------------------------
        private const string ThemeSwatchCss =
            ".sw{position:relative}.sw .go{margin-top:2px;font-size:11px;opacity:.85}" +
            ".tbs{display:flex;gap:10px;flex-wrap:wrap}.tb{width:128px;border:2px solid #333844;border-radius:12px;padding:8px;cursor:pointer;background:#15181d}.tb:hover{border-color:#6fa8f0}.tb.on{border-color:#6fa8f0}" +
            ".tb .mini{height:30px;border-radius:8px;display:flex;align-items:center;gap:6px;padding:0 8px}.tb .fld{flex:1;height:14px;border-radius:7px}.tb .dt{width:10px;height:10px;border-radius:50%}.tb .nm{font-size:11.5px;margin-top:6px;color:#c7ccd4}" +
            ".bgs{display:flex;flex-wrap:wrap;gap:10px;margin:2px 0 6px}.bgx{width:112px;height:68px;border-radius:10px;border:2px solid #333844;cursor:pointer;display:flex;align-items:flex-end;padding:6px;font-size:11px;color:#fff;text-shadow:0 1px 3px #000;box-sizing:border-box}.bgx:hover{border-color:#6fa8f0}.bgx.on{border-color:#6fa8f0;box-shadow:0 0 0 1px #6fa8f0 inset}";

        private string TopBarSectionHtml()
        {
            var baseColors = _chromeBar.Length == 0 ? ChromeDefaults : ChromeColors.Derive(_chromeBar, _chromeText, _chromeAccent);
            var sb = new StringBuilder("<div class='sec'>Top bar</div><div class='muted' style='margin-bottom:8px'>A finish for the toolbar, address field, tabs and bookmarks bar. It sits on top of the theme and never changes whether the window is light or dark.</div><div class='tbs'>");
            foreach (var s in TopBarStyles.All)
            {
                var c = new Dictionary<string, string>(baseColors);
                TopBarStyles.Apply(c, s.Id);
                bool on = (s.Id == TopBarStyles.MatchId ? "" : s.Id) == _topStyle;
                sb.Append("<div class='tb" + (on ? " on" : "") + "' onclick=\"send('cz-top:" + s.Id + "')\"><div class='mini' style='background:" + c["Bar"] + "'><span class='dt' style='background:" + c["Accent"] + "'></span><span class='fld' style='background:" + c["Field"] + ";border:1px solid " + c["Line"] + "'></span></div><div class='nm'>" + Esc(s.Name) + "</div></div>");
            }
            return sb.Append("</div>").ToString();
        }

        private string BackgroundGridHtml()
        {
            var sb = new StringBuilder("<style>" + ThemeSwatchCss + "</style><div class='bgs'>");
            foreach (var p in StartBackground.Presets)
            {
                var prev = StartBackground.Preview(p) ?? "background:#111318";
                var label = p == "default" ? "Default" : char.ToUpperInvariant(p[0]) + p.Substring(1);
                sb.Append("<div class='bgx" + (_startBg == p ? " on" : "") + "' style='" + prev + (p == "mint" || p == "sand" || p == "ice" || p == "paper" || p == "light" ? ";color:#1b2230;text-shadow:none" : "") + "' onclick=\"send('bg-set:" + p + "')\">" + label + "</div>");
            }
            if (_startBg.StartsWith("#")) sb.Append("<div class='bgx on' style='background:" + _startBg + "'>Your colour</div>");
            if (_startBg == "image" && File.Exists(StartImagePath())) sb.Append("<div class='bgx on' style='background:#222'>Your picture</div>");
            return sb.Append("</div>").ToString();
        }

        private string ThemeLibrarySectionHtml()
        {
            var sb = new StringBuilder("<div class='sec'>Theme library</div>");
            sb.Append("<div class='row'><div><div class='t'>Download more themes</div><div class='u'>Press Browse to fetch the list from GitHub (raw.githubusercontent.com). Nothing is requested before you press it. A theme is only three colours and a few names, checked before it is used; offline you get the list that came with Recognition.</div></div>" +
                      "<div class='ts'><a class='btn' onclick=\"send('cz-lib-fetch')\">Browse the library</a> <a class='btn ghost' onclick=\"send('cz-pack-import')\">Add a theme file...</a></div></div>");
            var mine = InstalledThemes();
            if (_libraryPacks.Count > 0)
            {
                sb.Append("<div class='muted' style='margin:4px 0 8px'>" + Esc(_libraryNote) + "</div><div style='display:flex;gap:12px;flex-wrap:wrap'>");
                foreach (var p in _libraryPacks)
                {
                    bool have = mine.Any(m => m.Id == p.Id);
                    sb.Append(PackCard(p, "cz-lib-add:" + p.Id, have ? "Added - use again" : "Add and use"));
                }
                sb.Append("</div>");
            }
            if (mine.Count > 0)
            {
                sb.Append("<div class='sec'>My themes</div><div style='display:flex;gap:12px;flex-wrap:wrap'>");
                foreach (var p in mine) sb.Append(PackCard(p, "cz-mine-apply:" + p.Id, "Use", "cz-mine-rm:" + p.Id));
                sb.Append("</div>");
            }
            return sb.ToString();
        }

        private static string PackCard(ThemePack p, string action, string label, string? removeAction = null) =>
            "<div class='sw' style='background:" + p.Bar + ";color:" + p.Text + "' onclick=\"send('" + action + "')\"><b>" + Esc(p.Name) + "</b><span><span class='dot' style='background:" + p.Accent + "'></span><span style='font-size:11px;opacity:.8'>" + (p.Dark ? "dark" : "light") + "</span></span>" +
            "<span class='go'>" + Esc(label) + "</span>" +
            (removeAction != null ? "<a href='#' style='font-size:11px;color:" + p.Text + ";opacity:.8' onclick=\"event.stopPropagation();send('" + removeAction + "');return false\">Remove</a>" : "") + "</div>";
    }
}
