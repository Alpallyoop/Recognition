using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Recognition.Browser
{
    // Looks, and the taskbar icon's right-click menu.
    //  * Themes: a one-click look (colours of the window, the start page background, light or dark pages), your own three colours, or a
    //    look read from a Chrome theme (a .crx/.zip/folder, or a Chrome Web Store link). The colours are validated in Customize.cs.
    //  * Taskbar: right-click the icon for "New private tab", "New tab" and your four most recent pages (never from private tabs).
    // Nothing here sends anything anywhere except the store download you start yourself.
    public partial class MainWindow
    {
        private string _chromeBar = "", _chromeText = "", _chromeAccent = "", _themeName = "Recognition";   // persisted: chrome_bar, chrome_text, chrome_accent, theme_name
        private bool _taskbarRecent = true;                                                                  // persisted: taskbar_recent
        private DispatcherTimer? _jumpTimer;

        // the colours the window has when no theme is chosen (these match App.xaml)
        private static readonly Dictionary<string, string> ChromeDefaults = new()
        {
            ["Win"] = "#111318", ["Bar"] = "#1b1e24", ["Side"] = "#15181d", ["Field"] = "#0e1015", ["Line"] = "#2a2e36", ["Line2"] = "#2e333c",
            ["Hover"] = "#2a2f38", ["Tab"] = "#1e222a", ["TabSel"] = "#2b313b", ["TabHover"] = "#262b34", ["Text"] = "#d6dae2", ["Muted"] = "#b8bdc7",
            ["Accent"] = "#2b6cb0", ["AccentHover"] = "#3480ce", ["MenuBg"] = "#14171c", ["MenuSel"] = "#26456e"
        };

        private static SolidColorBrush FrozenBrush(string hex)
        {
            var (r, g, b) = Rgb.Parse(hex);
            var br = new SolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b)); br.Freeze(); return br;
        }

        private void ApplyChromeTheme()
        {
            try
            {
                var colors = new Dictionary<string, string>(_chromeBar.Length == 0 ? ChromeDefaults : ChromeColors.Derive(_chromeBar, _chromeText, _chromeAccent));
                TopBarStyles.Apply(colors, _topStyle);   // the chosen top bar finish, if any
                var res = Application.Current.Resources;
                foreach (var k in ChromeColors.Keys) if (colors.TryGetValue(k, out var hex) && Rgb.Valid(hex)) res["Chrome" + k] = FrozenBrush(hex);
                ApplyCaptionColors(colors["Bar"], colors["Text"], colors["Line"], Rgb.Luminance(colors["Bar"]) < 0.5);
            }
            catch { }
        }

        private void ApplyCaptionColors(string bar, string text, string line, bool dark)
        {
            try
            {
                var h = new WindowInteropHelper(this).Handle; if (h == IntPtr.Zero) return;
                int on = dark ? 1 : 0;
                DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
                var b = Rgb.Parse(bar); var t = Rgb.Parse(text); var l = Rgb.Parse(line);
                int caption = Colorref(b.R, b.G, b.B), txt = Colorref(t.R, t.G, t.B), border = Colorref(l.R, l.G, l.B);
                DwmSetWindowAttribute(h, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
                DwmSetWindowAttribute(h, DWMWA_TEXT_COLOR, ref txt, sizeof(int));
                DwmSetWindowAttribute(h, DWMWA_BORDER_COLOR, ref border, sizeof(int));
            }
            catch { }
        }

        // Applies a look: window colours, light or dark pages, and the start page background. ntpImagePath is a picture already
        // checked to be inside the theme folder.
        private void ApplyPack(ThemePack p, string? ntpImagePath = null)
        {
            if (p.Id == ThemePacks.DefaultId) { _chromeBar = _chromeText = _chromeAccent = ""; _themeName = "Recognition"; _appearance.Set("theme", "system"); }
            else
            {
                _chromeBar = p.Bar; _chromeText = p.Text; _chromeAccent = p.Accent; _themeName = p.Name;
                _appearance.Set("theme", p.Dark ? "dark" : "light");
            }
            if (p.Id != "custom") { _topStyle = TopBarStyles.Normalize(p.Top); ApplyPackMotion(p); }   // your own three colours keep the top bar finish and animation you chose
            var bg = StartBackground.Normalize(p.StartBg);
            if (ntpImagePath != null && File.Exists(ntpImagePath) && ImportStartPicture(ntpImagePath) == null) { _startImageUrl = null; _startBg = "image"; }
            else _startBg = bg;
            SaveSettings(); ApplyChromeTheme(); ApplyAppearanceAll(); ReloadStartTabs();
            _actions?.Append("theme.apply", p.Id);   // the look's name is not recorded for imported themes
            Status("theme: " + _themeName);
        }

        // ---- importing a Chrome theme ---------------------------------------------------------------------------------------
        private async Task<string> ApplyThemeFromPackageAsync(byte[] package)
        {
            var staging = NewStagingDir();
            try
            {
                var root = await Task.Run(() => ExtPackage.Unpack(package, Path.Combine(staging, "x")));
                var mp = Path.Combine(root, "manifest.json");
                if (!File.Exists(mp)) return "that package has no manifest.json";
                var pack = ThemeLibrary.FromPackage(File.ReadAllText(mp, Encoding.UTF8).TrimStart('﻿'), ReadPackageTheme(root, out var tooBig), out var perr);
                if (tooBig) return "that theme's " + ThemeLibrary.PackageThemeFile + " is larger than 20 KB, so the theme was not applied";
                if (pack == null) return perr ?? "that is not a theme";
                string? img = null;
                if (pack.NtpImage != null)
                {
                    var full = Path.GetFullPath(Path.Combine(root, pack.NtpImage.Replace('/', Path.DirectorySeparatorChar)));
                    if (full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(full)) img = full;
                }
                ApplyPack(pack, img);   // ImportStartPicture copies the picture, so the staging folder can go
                return "applied the theme \"" + pack.Name + "\"" + (pack.NtpImage != null && img == null ? " (its picture could not be used)" : "");
            }
            catch (Exception ex) { return "could not use that theme: " + ex.Message; }
            finally { try { Directory.Delete(staging, true); } catch { } }
        }

        // recognition.theme.json at the root of a theme package (ThemeForge ships one): null when absent; tooBig when over 20 KB.
        private static string? ReadPackageTheme(string root, out bool tooBig)
        {
            tooBig = false;
            var f = Path.Combine(root, ThemeLibrary.PackageThemeFile);
            if (!File.Exists(f)) return null;
            if (new FileInfo(f).Length > ThemeLibrary.MaxPackBytes) { tooBig = true; return null; }
            return File.ReadAllText(f, Encoding.UTF8);
        }

        // ---- the Customize page ---------------------------------------------------------------------------------------------
        private void MenuCustomize_Click(object sender, RoutedEventArgs e) => OpenInternalInActiveTab("customize");

        private string CustomizeHtml()
        {
            var sb = new StringBuilder(PageHead);
            var cur = _chromeBar.Length == 0 ? ChromeDefaults["Bar"] : _chromeBar;
            var curText = _chromeBar.Length == 0 ? ChromeDefaults["Text"] : _chromeText;
            var curAcc = _chromeBar.Length == 0 ? ChromeDefaults["Accent"] : _chromeAccent;
            sb.Append("<style>" + ToolbarCss + ".sw{display:flex;flex-direction:column;gap:6px;padding:12px;border:1px solid #333844;border-radius:12px;cursor:pointer;min-width:132px}.sw:hover{border-color:#6fa8f0}.sw.on{border-color:#6fa8f0;box-shadow:0 0 0 1px #6fa8f0 inset}.dot{display:inline-block;width:14px;height:14px;border-radius:50%;margin-right:4px;vertical-align:middle}" + ThemeSwatchCss + "</style>");
            sb.Append("<title>Customize</title><h1>Customize</h1><div class='muted'>Looks for the window, the start page and web pages. Current look: <b>" + Esc(_themeName) + "</b>. Nothing here is sent anywhere.</div>");
            sb.Append("<div class='sec'>Themes</div><div style='display:flex;gap:12px;flex-wrap:wrap'>");
            foreach (var p in ThemePacks.Presets)
            {
                bool on = p.Name == _themeName;
                sb.Append("<div class='sw" + (on ? " on" : "") + "' style='background:" + p.Bar + ";color:" + p.Text + "' onclick=\"send('cz-apply:" + p.Id + "')\"><b>" + Esc(p.Name) + "</b><span><span class='dot' style='background:" + p.Accent + "'></span><span style='font-size:11px;opacity:.8'>" + (p.Dark ? "dark" : "light") + "</span></span></div>");
            }
            sb.Append("</div>");
            sb.Append(TopBarSectionHtml());
            sb.Append("<div class='sec'>Your own colours</div><div class='row'><div><div class='t'>Bar, text and accent</div><div class='u'>The toolbar, tabs, side bar and menus follow these three colours. Light bars get dark text automatically if you pick dark text.</div>" +
                      "<div style='margin-top:8px;display:flex;gap:14px;flex-wrap:wrap;align-items:center'><label>Bar <input type='color' id='cb' value='" + cur + "'></label><label>Text <input type='color' id='ct' value='" + curText + "'></label><label>Accent <input type='color' id='ca' value='" + curAcc + "'></label></div></div>" +
                      "<div class='ts'><a class='btn' onclick=\"send('cz-custom:'+encodeURIComponent(document.getElementById('cb').value)+'|'+encodeURIComponent(document.getElementById('ct').value)+'|'+encodeURIComponent(document.getElementById('ca').value))\">Use these</a> <a class='btn ghost' onclick=\"send('cz-apply:default')\">Reset</a></div></div>");
            sb.Append("<div class='sec'>Backgrounds and home page</div>");
            sb.Append(StartSettingsHtml());
            sb.Append("<div class='sec'>Layout</div>");
            sb.Append(FeelSettingsHtml());
            sb.Append(ThemeLibrarySectionHtml());
            sb.Append("<div class='sec'>Get a theme</div><div class='row'><div><div class='t'>Chrome themes</div><div class='u'>Paste a Chrome Web Store or Edge Add-ons theme link, or choose a theme file you have. Recognition uses the theme's colours and start page picture for its own look; it does not run the theme as an extension.</div>" +
                      "<div style='margin-top:8px;display:flex;gap:8px;flex-wrap:wrap;align-items:center'><input id='sl' class='in' placeholder='https://chromewebstore.google.com/detail/...' style='flex:1;min-width:260px'><a class='btn' onclick=\"var v=document.getElementById('sl').value.trim();if(v)send('cz-store:'+encodeURIComponent(v))\">Get theme</a></div>" +
                      "<div style='margin-top:8px'><a class='btn ghost' onclick=\"send('cz-import')\">Choose a .crx or .zip...</a> <a class='btn ghost' onclick=\"send('cz-import-folder')\">Choose a folder...</a> <a class='btn ghost t' href='https://chromewebstore.google.com/category/themes'>Browse Chrome themes</a> <a class='btn ghost' href='recognition:extensions'>Extensions</a></div>" +
                      "<div id='msg' class='u' style='min-height:18px;margin-top:8px'></div></div></div>");
            sb.Append("<script>window.chrome.webview.addEventListener('message',function(ev){var m=ev.data;if(m&&m.type==='note')document.getElementById('msg').textContent=m.text;});</script>");
            sb.Append(SendScript()).Append(PageFoot);
            return sb.ToString();
        }

        private async void HandleCustomizeMessage(BrowserTab tab, string msg)
        {
            void Note(string t) { Status(t); try { tab.Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "note", text = t })); } catch { } }
            try
            {
                if (msg.StartsWith("cz-top:", StringComparison.Ordinal) || msg == "cz-lib-fetch" || msg.StartsWith("cz-lib-add:", StringComparison.Ordinal) ||
                    msg.StartsWith("cz-mine-", StringComparison.Ordinal) || msg == "cz-pack-import")
                {
                    if (tab.Internal != "customize") return;
                    await HandleThemeExtraAsync(tab, msg, Note);
                    return;
                }
                if (msg == "cz-taskbar:on" || msg == "cz-taskbar:off")
                {
                    _taskbarRecent = msg.EndsWith("on"); SaveSettings(); UpdateJumpList();
                    if (tab.Internal == "settings" && _tabs.Contains(tab)) LoadInternal(tab, "settings");
                    return;
                }
                if (msg.StartsWith("cz-apply:", StringComparison.Ordinal))
                {
                    var p = ThemePacks.Find(msg.Substring("cz-apply:".Length)); if (p == null) return;
                    ApplyPack(p);
                }
                else if (msg.StartsWith("cz-custom:", StringComparison.Ordinal))
                {
                    // the page sends each colour percent-encoded, so decode the pieces
                    var cols = msg.Substring("cz-custom:".Length).Split('|').Select(Dec).ToArray();
                    var p = cols.Length == 3 ? ThemePacks.Custom(cols[0], cols[1], cols[2], _startBg) : null;
                    if (p == null) { Note("those colours cannot be used"); return; }
                    ApplyPack(p);
                }
                else if (msg == "cz-import" || msg == "cz-import-folder")
                {
                    byte[]? data = null;
                    if (msg == "cz-import")
                    {
                        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose a Chrome theme (.crx or .zip)", Filter = "Chrome theme (*.crx;*.zip)|*.crx;*.zip" };
                        if (dlg.ShowDialog(this) != true) return;
                        var fi = new FileInfo(dlg.FileName);
                        if (!fi.Exists || fi.Length > MaxExtDownloadBytes) { Note("that file is missing or too large"); return; }
                        data = await Task.Run(() => File.ReadAllBytes(dlg.FileName));
                    }
                    else
                    {
                        var f = PickFolder("Choose an unpacked Chrome theme folder (it contains manifest.json)"); if (f == null) return;
                        var mp = Path.Combine(f, "manifest.json");
                        if (!File.Exists(mp)) { Note("that folder has no manifest.json"); return; }
                        var pack = ThemeLibrary.FromPackage(File.ReadAllText(mp, Encoding.UTF8).TrimStart('﻿'), ReadPackageTheme(f, out var tooBig), out var perr);
                        if (tooBig) { Note("that theme's " + ThemeLibrary.PackageThemeFile + " is larger than 20 KB, so the theme was not applied"); return; }
                        if (pack == null) { Note(perr ?? "that is not a theme"); return; }
                        string? img = null;
                        if (pack.NtpImage != null) { var full = Path.GetFullPath(Path.Combine(f, pack.NtpImage.Replace('/', Path.DirectorySeparatorChar))); if (full.StartsWith(Path.GetFullPath(f).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(full)) img = full; }
                        ApplyPack(pack, img); Note("applied the theme \"" + pack.Name + "\""); RefreshCustomize(tab); return;
                    }
                    Note(await ApplyThemeFromPackageAsync(data));
                }
                else if (msg.StartsWith("cz-store:", StringComparison.Ordinal))
                {
                    var src = ExtSources.Parse(Dec(msg.Substring("cz-store:".Length)));
                    if (src == null) { Note("paste a Chrome Web Store or Edge Add-ons link (or an extension id)"); return; }
                    Note("downloading the theme from the " + (src.Store == "edge" ? "Edge Add-ons" : "Chrome Web Store") + "...");
                    var bytes = await DownloadExtensionAsync(ExtSources.Url(src, EngineMajor()));
                    Note(await ApplyThemeFromPackageAsync(bytes));
                }
                else return;
                RefreshCustomize(tab);
            }
            catch (Exception ex) { Note("theme failed: " + ex.Message); }
        }

        private void RefreshCustomize(BrowserTab tab) { if (tab.Internal == "customize" && _tabs.Contains(tab)) LoadInternal(tab, "customize"); }

        private string EngineMajor() { var v = CurrentEngineVersion(); var i = v.IndexOf('.'); return i > 0 ? v.Substring(0, i) : ""; }

        // ---- taskbar icon menu --------------------------------------------------------------------------------------------------
        private void ScheduleJumpListUpdate()
        {
            try
            {
                _jumpTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                _jumpTimer.Tick -= JumpTick; _jumpTimer.Tick += JumpTick;
                _jumpTimer.Stop(); _jumpTimer.Start();   // wait until browsing pauses
            }
            catch { }
        }
        private void JumpTick(object? s, EventArgs e) { _jumpTimer?.Stop(); UpdateJumpList(); }

        // The newest four distinct pages from history (private tabs never reach history), shown under "Recent pages".
        private List<(string Url, string Title)> RecentPages(int n)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal); var list = new List<(string, string)>();
            for (int i = _history.Items.Count - 1; i >= 0 && list.Count < n; i--)
            {
                var h = _history.Items[i];
                var u = LaunchArgs.Parse(h.Url); if (u == null || !seen.Add(u)) continue;
                var t = new string((string.IsNullOrWhiteSpace(h.Title) ? (TryHost(u) ?? u) : h.Title).Where(c => !char.IsControl(c)).ToArray()).Trim();
                if (t.Length > 60) t = t.Substring(0, 57) + "...";
                list.Add((u, t));
            }
            return list;
        }

        private void UpdateJumpList()
        {
            try
            {
                var exe = Environment.ProcessPath; if (string.IsNullOrEmpty(exe)) return;
                var jl = new JumpList { ShowFrequentCategory = false, ShowRecentCategory = false };
                jl.JumpItems.Add(new JumpTask { Title = "New private tab", Description = "Open a private tab (nothing is kept)", ApplicationPath = exe, IconResourcePath = exe, Arguments = "--private" });
                jl.JumpItems.Add(new JumpTask { Title = "New tab", Description = "Open a new tab", ApplicationPath = exe, IconResourcePath = exe, Arguments = "--new-tab" });
                if (_taskbarRecent)
                    foreach (var (url, title) in RecentPages(4))
                        jl.JumpItems.Add(new JumpTask { Title = title, Description = url, ApplicationPath = exe, IconResourcePath = exe, Arguments = "\"" + url + "\"", CustomCategory = "Recent pages" });
                JumpList.SetJumpList(Application.Current, jl);
                jl.Apply();
            }
            catch { }
        }

        // from the command line / a second launch: "--private" and "--new-tab"
        internal async void OpenSpecialFromOutside(string what)
        {
            try
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                if (!_startupDone) { App.PendingSpecial = what; return; }
                if (!_preflightOk) { Status("locked: not opening a tab from outside"); return; }
                if (what == "--private") await OpenNewPrivateTabAsync(); else if (what == "--new-tab") await OpenNewTabAsync();
            }
            catch { }
        }

        private async Task DrainSpecialAsync()
        {
            var w = App.PendingSpecial; App.PendingSpecial = null;
            if (w == "--private") await OpenNewPrivateTabAsync(); else if (w == "--new-tab") await OpenNewTabAsync();
        }

        private string CustomizeSettingsRowsHtml() =>
            "<div class='row'><div><div class='t'>Taskbar menu: recent pages</div><div class='u'>Right-click the Recognition icon on the taskbar to open a private tab or one of your four most recent pages. Pages from private tabs are never listed.</div></div><div class='ts'><a class='btn" + (_taskbarRecent ? "" : " ghost") + "' onclick=\"send('cz-taskbar:" + (_taskbarRecent ? "off" : "on") + "')\">" + (_taskbarRecent ? "On" : "Off") + "</a></div></div>" +
            "<div class='row'><div><div class='t'>Look of the window</div><div class='u'>Themes, colours, backgrounds and layout. Now: " + Esc(_themeName) + ".</div></div><div class='ts'><a class='btn' href='recognition:customize'>Customize</a></div></div>";
    }
}
