using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Recognition.Browser
{
    // The everyday-browser parts: bookmarks bar, side bar, tab menu (pin, mute, duplicate, close others, reopen closed tab), search engine choice,
    // bookmark import, and the most-visited tiles and counters on the start page. The decisions (what a search opens, which bookmarks an import
    // accepts, which sites become tiles) are in BrowserFeel.cs and executed by tests. Nothing here makes a network request.
    public partial class MainWindow
    {
        private string _searchEngine = "duckduckgo";     // persisted as search_engine
        private bool _bookmarksBarOn = true;             // persisted as bookmarks_bar
        private bool _sideBarOn = true;                  // persisted as sidebar
        private long _statsBlocked, _statsStripped;      // lifetime counters, persisted as stats_blocked / stats_stripped
        private readonly List<string> _closedTabs = new();   // addresses of closed tabs, newest first, this run only

        private void ApplyLayout()
        {
            try
            {
                BookmarksBar.Visibility = _bookmarksBarOn ? Visibility.Visible : Visibility.Collapsed;
                SideBar.Visibility = _sideBarOn ? Visibility.Visible : Visibility.Collapsed;
            }
            catch { }
        }

        private void MenuReopenTab_Click(object sender, RoutedEventArgs e) { _ = ReopenClosedTabAsync(); }
        private void MenuBookmarksBar_Click(object sender, RoutedEventArgs e) { _bookmarksBarOn = !_bookmarksBarOn; SaveSettings(); ApplyLayout(); }
        private void MenuImportBookmarks_Click(object sender, RoutedEventArgs e) => ImportBookmarks();

        // ---- search -------------------------------------------------------------------------------------------------------
        private string SearchUrl(string query) => SearchEngines.Url(_searchEngine, query);

        // ---- bookmarks bar ------------------------------------------------------------------------------------------------
        private void RebuildBookmarksBar()
        {
            SaveBookmarkIcons();
            try
            {
                BookmarksBarItems.Children.Clear();
                if (_bookmarks.Count == 0)
                {
                    BookmarksBarItems.Children.Add(new TextBlock
                    {
                        Text = "Bookmarks you save with the star appear here.", Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x90, 0x9A)),
                        FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 8, 0)
                    });
                    var imp = new Button { Content = "Import bookmarks", Style = (Style)FindResource("NavBtn"), FontSize = 12, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(2, 0, 2, 0), Foreground = new SolidColorBrush(Color.FromRgb(0x6F, 0xA8, 0xF0)) };
                    imp.Click += (_, __) => ImportBookmarks();
                    BookmarksBarItems.Children.Add(imp);
                    return;
                }
                foreach (var fname in BookmarkFolders.Names(_bookmarks.Select(x => x.Folder))) BookmarksBarItems.Children.Add(MakeFolderChip(fname));
                foreach (var b in _bookmarks.Where(x => string.IsNullOrEmpty(x.Folder)).Take(200)) BookmarksBarItems.Children.Add(MakeBookmarkChip(b));
            }
            catch { }
        }

        private Button MakeFolderChip(string folder)
        {
            var btn = new Button { Content = "\U0001F4C1  " + folder + "  \u25BE", Style = (Style)FindResource("NavBtn"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(2, 0, 2, 0), FontSize = 12, ToolTip = "Folder" };
            btn.Click += (_, __) =>
            {
                var cm = new ContextMenu { PlacementTarget = btn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
                foreach (var bk in _bookmarks.Where(x => string.Equals(x.Folder, folder, StringComparison.OrdinalIgnoreCase)))
                {
                    var url = bk.Url; var mi = new MenuItem { Header = string.IsNullOrWhiteSpace(bk.Title) ? url : (bk.Title.Length > 60 ? bk.Title.Substring(0, 60) + "..." : bk.Title), ToolTip = url };
                    mi.Click += (_, ___) => OpenBookmark(url, false);
                    cm.Items.Add(mi);
                }
                cm.IsOpen = true;
            };
            return btn;
        }

        // ---- bookmark icons ------------------------------------------------------------------------------------------
        // Pages send their own small icon. Icons seen this session are kept in memory; the ones for bookmarked sites are
        // also saved (encrypted, same as the bookmarks) so the bar shows them next time. Private tabs are never recorded.
        private readonly Dictionary<string, byte[]> _favSeen = new();
        private Dictionary<string, byte[]>? _favSaved;
        private string FavPath() => Path.Combine(_repoRoot, "runtime", "bookmark_icons.v1.enc");
        private static string FavKey(string? url)
        {
            try { var h = new Uri(url ?? "").Host.ToLowerInvariant(); return h.StartsWith("www.") ? h.Substring(4) : h; } catch { return ""; }
        }
        private Dictionary<string, byte[]> FavSaved()
        {
            if (_favSaved != null) return _favSaved;
            _favSaved = new Dictionary<string, byte[]>();
            try
            {
                if (File.Exists(FavPath()))
                    using (var d = JsonDocument.Parse(ReadSecure(FavPath())))
                        foreach (var p in d.RootElement.EnumerateObject())
                            if (p.Value.ValueKind == JsonValueKind.String && _favSaved.Count < 500) { var b = Convert.FromBase64String(p.Value.GetString() ?? ""); if (b.Length > 0 && b.Length <= 65536) _favSaved[p.Name] = b; }
            }
            catch { _favSaved = new Dictionary<string, byte[]>(); }
            return _favSaved;
        }
        private void NoteFavicon(string? pageUrl, byte[] png)
        {
            var key = FavKey(pageUrl);
            if (key.Length == 0 || png.Length == 0 || png.Length > 65536) return;
            if (_favSeen.Count < 1000 || _favSeen.ContainsKey(key)) _favSeen[key] = png;
            if (_bookmarks.Any(b => FavKey(b.Url) == key) && !(FavSaved().TryGetValue(key, out var old) && old.AsSpan().SequenceEqual(png))) RebuildBookmarksBar();
        }
        private BitmapSource? BookmarkIcon(string host)
        {
            var key = FavKey("https://" + host);
            byte[]? png = null;
            if (_favSeen.TryGetValue(key, out var seen)) png = seen; else FavSaved().TryGetValue(key, out png);
            if (png == null) return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = new MemoryStream(png); bmp.EndInit(); bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
        private void SaveBookmarkIcons()
        {
            try
            {
                var saved = FavSaved(); bool dirty = false;
                var keep = new HashSet<string>(_bookmarks.Select(b => FavKey(b.Url)).Where(k => k.Length > 0));
                foreach (var k in keep) if (_favSeen.TryGetValue(k, out var png) && !(saved.TryGetValue(k, out var o) && o.AsSpan().SequenceEqual(png))) { saved[k] = png; dirty = true; }
                foreach (var k in saved.Keys.Where(k => !keep.Contains(k)).ToList()) { saved.Remove(k); dirty = true; }
                if (dirty) { Directory.CreateDirectory(Path.GetDirectoryName(FavPath())!); WriteSecure(FavPath(), JsonSerializer.Serialize(saved.ToDictionary(kv => kv.Key, kv => Convert.ToBase64String(kv.Value)))); }
            }
            catch { }
        }

        private Button MakeBookmarkChip(Bookmark b)
        {
            string host = ""; try { host = new Uri(b.Url).Host; } catch { }
            var c = SiteBadge.Color(host);
            var icon = BookmarkIcon(host);
            FrameworkElement badge = icon != null ? new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center } : new Border
            {
                Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 6, 0), Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)),
                Child = new TextBlock { Text = SiteBadge.Letter(host), Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            var label = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(b.Title) ? (host.Length > 0 ? host : b.Url) : b.Title, MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center, FontSize = 12
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(badge); sp.Children.Add(label);
            var btn = new Button { Content = sp, Style = (Style)FindResource("NavBtn"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(2, 0, 2, 0), ToolTip = b.Url, FontSize = 12 };
            var url = b.Url;
            btn.Click += (_, __) => OpenBookmark(url, false);
            btn.MouseUp += (_, ev) => { if (ev.ChangedButton == MouseButton.Middle) OpenBookmark(url, true); };
            var cm = new ContextMenu();
            var open = new MenuItem { Header = "Open in new tab" }; open.Click += (_, __) => OpenBookmark(url, true);
            var rm = new MenuItem { Header = "Remove bookmark" };
            rm.Click += (_, __) =>
            {
                _bookmarks.RemoveAll(x => x.Url == url); _actions?.Append("bookmark.remove", url); SaveBookmarks();
                var a = Active; if (a != null) { UpdateStar(a); if (a.Internal == "bookmarks") LoadInternal(a, "bookmarks"); }
            };
            var priv = new MenuItem { Header = "Open in private tab" }; priv.Click += (_, __) => { _ = OpenBookmarkPrivateAsync(url); };
            var ren = new MenuItem { Header = "Rename..." };
            ren.Click += (_, __) =>
            {
                var bk = _bookmarks.FirstOrDefault(x => x.Url == url); if (bk == null) return;
                var nm = PromptText("Rename bookmark", "Name", string.IsNullOrWhiteSpace(bk.Title) ? url : bk.Title);
                if (nm != null) { RenameBookmark(bk, nm); var a2 = Active; if (a2 != null && a2.Internal == "bookmarks") LoadInternal(a2, "bookmarks"); }
            };
            var cp = new MenuItem { Header = "Copy address" }; cp.Click += (_, __) => { try { Clipboard.SetText(url); Status("address copied"); } catch { } };
            var mv = new MenuItem { Header = "Move to folder..." }; mv.Click += (_, __) => { var bk2 = _bookmarks.FirstOrDefault(x => x.Url == url); if (bk2 != null) ShowFolderMenu(bk2); };
            cm.Items.Add(open); cm.Items.Add(priv); cm.Items.Add(ren); cm.Items.Add(mv); cm.Items.Add(cp); cm.Items.Add(new Separator()); cm.Items.Add(rm);
            btn.ContextMenu = cm;
            return btn;
        }

        private async void OpenBookmark(string url, bool newTab)
        {
            try
            {
                var a = Active;
                if (newTab || a == null)
                {
                    var t = await NewTabCoreAsync("New tab"); if (t == null) return;
                    Tabs.SelectedItem = t.Item; ShowActiveWebView(); NavigateTab(t, url);
                }
                else NavigateTab(a, url);
            }
            catch { }
        }

        private void ImportBookmarks()
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Import bookmarks (the HTML file your old browser exported)", Filter = "Bookmarks file (*.html;*.htm)|*.html;*.htm" };
                if (dlg.ShowDialog(this) != true) return;
                var fi = new FileInfo(dlg.FileName);
                if (!fi.Exists || fi.Length > BookmarkImport.MaxBytes) { Status("that file is missing or larger than 20 MB"); return; }
                var items = BookmarkImport.Parse(File.ReadAllText(dlg.FileName));
                int added = 0;
                foreach (var it in items)
                {
                    if (IsBookmarked(it.Url)) continue;
                    _bookmarks.Add(new Bookmark { Url = it.Url, Title = it.Title, Ts = Iso(DateTime.UtcNow) }); added++;
                }
                if (added > 0) SaveBookmarks();
                _actions?.Append("bookmark.import", added + " of " + items.Count);
                Status(items.Count == 0 ? "no web bookmarks were found in that file" : ("imported " + added + " bookmark(s)" + (items.Count - added > 0 ? "; " + (items.Count - added) + " were already saved" : "")));
                var a = Active; if (a != null && a.Internal == "bookmarks") LoadInternal(a, "bookmarks");
            }
            catch (Exception ex) { Status("import failed: " + ex.Message); }
        }

        // ---- tabs: menu, pin, mute, duplicate, reopen ----------------------------------------------------------------------
        private void AttachTabMenu(BrowserTab tab)
        {
            var cm = new ContextMenu();
            tab.Item.ContextMenu = cm;
            tab.Item.ContextMenuOpening += (_, __) => FillTabMenu(tab, cm);
            FillTabMenu(tab, cm);
        }

        private void FillTabMenu(BrowserTab tab, ContextMenu cm)
        {
            cm.Items.Clear();
            MenuItem Mk(string header, Action act, bool enabled = true)
            {
                var mi = new MenuItem { Header = header, IsEnabled = enabled };
                mi.Click += (_, __) => { try { act(); } catch { } };
                return mi;
            }
            cm.Items.Add(Mk("New tab", () => { _ = OpenNewTabAsync(); }));
            cm.Items.Add(Mk("Reload", () => tab.Web.CoreWebView2.Reload(), tab.Ready));
            cm.Items.Add(Mk("Duplicate tab", () => { _ = DuplicateTabAsync(tab); }, tab.Ready && !tab.Private && !tab.IsInternal));
            cm.Items.Add(Mk(tab.Pinned ? "Unpin tab" : "Pin tab", () => TogglePin(tab)));
            cm.Items.Add(Mk(tab.Muted ? "Unmute tab" : "Mute tab", () => ToggleMute(tab), tab.Ready));
            cm.Items.Add(new Separator());
            FillGroupMenu(tab, cm, Mk);
            cm.Items.Add(new Separator());
            cm.Items.Add(Mk("Close tab", () => CloseTab(tab), !tab.Pinned));
            cm.Items.Add(Mk("Close other tabs", () => CloseOtherTabs(tab), _tabs.Any(t => !ReferenceEquals(t, tab) && !t.Pinned)));
            cm.Items.Add(Mk("Close tabs to the right", () => CloseTabsToTheRight(tab), _tabs.IndexOf(tab) < _tabs.Count - 1));
            cm.Items.Add(new Separator());
            cm.Items.Add(Mk("Reopen closed tab", () => { _ = ReopenClosedTabAsync(); }, _closedTabs.Count > 0));
        }

        private async System.Threading.Tasks.Task DuplicateTabAsync(BrowserTab src)
        {
            if (src.Private || src.IsInternal || string.IsNullOrEmpty(src.CurrentUrl)) return;
            var t = await NewTabCoreAsync("New tab"); if (t == null) return;
            Tabs.SelectedItem = t.Item; ShowActiveWebView(); NavigateTab(t, src.CurrentUrl);
        }

        private void TogglePin(BrowserTab tab)
        {
            tab.Pinned = !tab.Pinned;
            tab.CloseBtn.Visibility = tab.Pinned ? Visibility.Collapsed : Visibility.Visible;
            tab.Header.MaxWidth = tab.Pinned ? 70 : 180;
            if (tab.Pinned) tab.Group = null;
            RefreshTabsUi(); SetHeader(tab, "");
        }

        private void ToggleMute(BrowserTab tab)
        {
            tab.Muted = !tab.Muted;
            try { tab.Web.CoreWebView2.IsMuted = tab.Muted; } catch { }
            SetHeader(tab, "");
        }

        private void CloseOtherTabs(BrowserTab keep)
        {
            Tabs.SelectedItem = keep.Item;
            foreach (var t in _tabs.Where(x => !ReferenceEquals(x, keep) && !x.Pinned).ToList()) CloseTab(t);
        }

        private void CloseTabsToTheRight(BrowserTab from)
        {
            int i = _tabs.IndexOf(from); if (i < 0) return;
            Tabs.SelectedItem = from.Item;
            foreach (var t in _tabs.Skip(i + 1).Where(x => !x.Pinned).ToList()) CloseTab(t);
        }

        private void RememberClosedTab(BrowserTab tab)
        {
            if (tab.Private || tab.IsInternal) return;
            var url = LaunchArgs.Parse(tab.CurrentUrl);
            if (url == null) return;
            _closedTabs.Remove(url); _closedTabs.Insert(0, url);
            if (_closedTabs.Count > 10) _closedTabs.RemoveAt(10);
        }

        private async System.Threading.Tasks.Task ReopenClosedTabAsync()
        {
            if (_closedTabs.Count == 0) { Status("no closed tab to reopen"); return; }
            var url = _closedTabs[0]; _closedTabs.RemoveAt(0);
            var t = await NewTabCoreAsync("New tab"); if (t == null) return;
            Tabs.SelectedItem = t.Item; ShowActiveWebView(); NavigateTab(t, url);
        }

        // ---- settings rows and messages ---------------------------------------------------------------------------------
        private string FeelSettingsHtml()
        {
            var sb = new StringBuilder();
            sb.Append("<h1 style='font-size:16px'>Browser layout and search</h1>");
            string Toggle(bool on, string key) => on
                ? "<a class='btn' onclick=\"send('feel-" + key + ":off')\">On</a>"
                : "<a class='btn ghost' onclick=\"send('feel-" + key + ":on')\">Off</a>";
            sb.Append("<div class='row'><div><div class='t'>Bookmarks bar</div><div class='u'>Your bookmarks under the tab strip. Ctrl+Shift+B shows or hides it. Right-click a bookmark to remove it; middle-click opens it in a new tab.</div></div><div class='ts'>" + Toggle(_bookmarksBarOn, "bar") + "</div></div>");
            sb.Append("<div class='row'><div><div class='t'>Side bar</div><div class='u'>Quick buttons down the left edge for the pages you use most.</div></div><div class='ts'>" + Toggle(_sideBarOn, "side") + "</div></div>");
            sb.Append("<div class='row'><div><div class='t'>Import bookmarks</div><div class='u'>Bring bookmarks from another browser. Export them there as an HTML file (Chrome, Edge, Brave, Opera, Vivaldi and Firefox all can), then choose that file here. Only web addresses are taken.</div></div><div class='ts'><a class='btn' onclick=\"send('feel-import')\">Choose a file...</a></div></div>");
            sb.Append("<div class='row'><div><div class='t'>Search engine</div><div class='u'>Used by the address bar and the start page. Now: " + Esc(SearchEngines.Get(_searchEngine).Name) + ".</div></div><div class='ts'>");
            foreach (var e in SearchEngines.All)
                sb.Append("<a class='btn" + (e.Id == _searchEngine ? "" : " ghost") + "' onclick=\"send('feel-engine:" + e.Id + "')\">" + Esc(e.Name) + "</a> ");
            sb.Append("</div></div>");
            return sb.ToString();
        }

        private void HandleFeelMessage(BrowserTab from, string msg)
        {
            try
            {
                switch (msg)
                {
                    case "feel-bar:on": _bookmarksBarOn = true; break;
                    case "feel-bar:off": _bookmarksBarOn = false; break;
                    case "feel-side:on": _sideBarOn = true; break;
                    case "feel-side:off": _sideBarOn = false; break;
                    case "feel-import": ImportBookmarks(); return;
                    default:
                        if (msg.StartsWith("feel-engine:", StringComparison.Ordinal))
                        {
                            var id = msg.Substring("feel-engine:".Length);
                            if (!SearchEngines.All.Any(x => x.Id == id)) return;
                            _searchEngine = id; _prefsChangedUtc = DateTime.UtcNow; _actions?.Append("search.engine", id);
                            ReloadStartTabs();
                            break;
                        }
                        return;
                }
                SaveSettings(); ApplyLayout();
                if (from.Internal is "settings" or "customize") LoadInternal(from, from.Internal);
            }
            catch (Exception ex) { Status("setting failed: " + ex.Message); }
        }

        // ---- start page: most visited and counters ----------------------------------------------------------------------
        private const string StartExtraCss =
            ".sites{margin-top:30px;display:grid;grid-template-columns:repeat(4,minmax(110px,150px));gap:12px;justify-content:center}" +
            ".site{display:flex;align-items:center;gap:10px;padding:11px 12px;background:var(--panel);border:1px solid var(--line);border-radius:14px;color:var(--txt);text-decoration:none;font-size:12.5px;min-width:0;transition:transform .12s,border-color .12s}" +
            ".site:hover{transform:translateY(-2px);border-color:#3a4150}" +
            ".site .ico{flex:none;width:26px;height:26px;border-radius:50%;display:flex;align-items:center;justify-content:center;color:#fff;font-weight:600;font-size:13px}" +
            ".site .nm{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}" +
            ".stats{margin-top:30px;display:flex;gap:14px;justify-content:center;flex-wrap:wrap}" +
            ".stat{min-width:170px;padding:12px 16px;background:var(--panel);border:1px solid var(--line);border-radius:14px;text-align:center}" +
            ".stat b{display:block;font-size:22px;font-weight:600;color:var(--acc)}.stat span{color:var(--mut);font-size:11.5px}" +
            "@media(max-width:760px){.sites{grid-template-columns:repeat(2,minmax(110px,150px))}}";

        private string StartExtraHtml()
        {
            var sb = new StringBuilder();
            try
            {
                var top = TopSites.Pick(_history.Items.Select(h => (h.Url, h.Title)), 16, null);
                var tiles = TileRules.Build(_tilesCustom, top, _tilesHidden, TileRules.MaxTiles);
                sb.Append("<style>.sites{position:relative}.site{position:relative}.site .rm{position:absolute;top:-6px;right:-6px;width:18px;height:18px;border-radius:50%;background:#2b2f38;color:#c9ced8;border:1px solid #3a4150;font-size:10px;line-height:16px;text-align:center;display:none;text-decoration:none}.site:hover .rm{display:block}.addsite{justify-content:center;color:var(--mut);border-style:dashed}</style>");
                sb.Append(TileScript);
                sb.Append("<div class='sites'>");
                foreach (var s in tiles)
                    sb.Append("<a class='site' href='" + Attr(s.Url) + "' title='" + Attr(s.Host) + "'><span class='ico' style='background:" + SiteBadge.CssColor(s.Host) + "'>" + Esc(SiteBadge.Letter(s.Host)) + "</span><span class='nm'>" + Esc(s.Title) + "</span>" +
                              "<span class='rm' data-h='" + Attr(s.Host) + "' title='Remove' onclick='tileHide(event,this.dataset.h)'>&#x2715;</span></a>");
                if (_tilesCustom.Count < TileRules.MaxCustom)
                    sb.Append("<a class='site addsite' href='#' onclick='tileAdd(event)'><span class='nm'>+ Add a site</span></a>");
                sb.Append("</div>");
                var sitesSeen = _history.Items.Select(h => TryHost(h.Url)).Where(h => !string.IsNullOrEmpty(h)).Distinct().Count();
                sb.Append("<div class='stats'><div class='stat'><b>" + Counts.Pretty(_statsBlocked) + "</b><span>trackers and ads blocked</span></div>" +
                          "<div class='stat'><b>" + Counts.Pretty(_statsStripped) + "</b><span>tracking links cleaned</span></div>" +
                          "<div class='stat'><b>" + Counts.Pretty(sitesSeen) + "</b><span>sites visited</span></div>" +
                          "<div class='stat'><b>" + Counts.Pretty(_bookmarks.Count) + "</b><span>bookmarks</span></div></div>");
                sb.Append("<div class='stats' style='margin-top:14px'><a class='site' href='recognition:overview' style='justify-content:center'><span class='nm'>Overview</span></a>" +
                          "<a class='site' href='#' onclick=\"window.chrome.webview.postMessage('sc:newprivate');return false\" style='justify-content:center'><span class='nm'>Private tab</span></a>" +
                          "<a class='site' href='recognition:downloads' style='justify-content:center'><span class='nm'>Downloads</span></a>" +
                          "<a class='site' href='recognition:history' style='justify-content:center'><span class='nm'>History</span></a></div>");
            }
            catch { }
            return sb.ToString();
        }
    }
}
