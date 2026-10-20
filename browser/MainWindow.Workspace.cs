using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Recognition.Browser
{
    // Tab groups, split view, restoring your tabs, editable start page tiles, reader mode, picture-in-picture and translate.
    // The rules (group order, session validation, tile building, translate addresses) are in Workspace.cs; the page scripts for reader mode and
    // picture-in-picture are shield\reader.js and shield\pip.js. Both are tested under node. Nothing here reaches the network on its own:
    // translate opens a link only after you confirm it.
    public partial class MainWindow
    {
        private readonly Dictionary<string, GroupInfo> _groups = new();
        private readonly HashSet<string> _collapsedGroups = new(StringComparer.Ordinal);
        private bool _reordering;
        private string _startupMode = "start";                       // start | restore, persisted as startup_mode
        private DispatcherTimer? _sessionTimer;
        private readonly List<(string Url, string Title)> _tilesCustom = new();   // persisted in browser_settings.json as tiles
        private readonly HashSet<string> _tilesHidden = new(StringComparer.Ordinal);
        private string _translateLang = "en", _translateService = "google";
        private DateTime _prefsChangedUtc = DateTime.MinValue;       // when home page or search engine last changed (settings sync: the later one wins)
        private BrowserTab? _splitA, _splitB;
        private DispatcherTimer? _splitTimer;
        private string? _readerJs, _pipJs;

        // ---- small text prompt -------------------------------------------------------------------------------------------
        private string? PromptText(string title, string label, string initial)
        {
            string? result = null;
            var box = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 12), Padding = new Thickness(6), Width = 300, Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x10, 0x15)), Foreground = Brushes.White, CaretBrush = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x33, 0x3C)) };
            var ok = new Button { Content = "OK", Width = 74, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancel = new Button { Content = "Cancel", Width = 74, IsCancel = true };
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            row.Children.Add(ok); row.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White });
            panel.Children.Add(box); panel.Children.Add(row);
            var win = new Window { Title = title, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1E, 0x24)), Content = panel };
            ok.Click += (_, __) => { result = box.Text; win.DialogResult = true; };
            win.Loaded += (_, __) => { box.Focus(); box.SelectAll(); };
            return win.ShowDialog() == true ? result : null;
        }

        // ---- tab groups ---------------------------------------------------------------------------------------------------
        private void NewGroupFor(BrowserTab tab)
        {
            if (tab.Pinned || _groups.Count >= TabGroups.MaxGroups) return;
            var name = PromptText("New group", "Name this group", "");
            if (name == null) return;
            var id = TabGroups.NextId(_groups.Keys);
            var clean = TabGroups.CleanName(name); if (clean.Length == 0) clean = "Group " + id.Substring(1);
            _groups[id] = new GroupInfo(id, clean, TabGroups.NextColor(_groups.Values.Select(g => g.Color)));
            AssignGroup(tab, id);
        }

        private void AssignGroup(BrowserTab tab, string id)
        {
            if (tab.Pinned || !_groups.ContainsKey(id)) return;
            tab.Group = id; RefreshTabsUi();
        }

        private void RemoveFromGroup(BrowserTab tab) { tab.Group = null; RefreshTabsUi(); }

        private void RenameGroup(string id)
        {
            if (!_groups.TryGetValue(id, out var g)) return;
            var name = PromptText("Rename group", "Group name", g.Name); if (name == null) return;
            var clean = TabGroups.CleanName(name); if (clean.Length == 0) return;
            _groups[id] = g with { Name = clean }; RefreshTabsUi();
        }

        private void ToggleGroupCollapsed(string id)
        {
            if (!_collapsedGroups.Remove(id))
            {
                _collapsedGroups.Add(id);
                var first = _tabs.FirstOrDefault(t => t.Group == id);
                var a = Active;
                if (first != null && a != null && a.Group == id && !ReferenceEquals(a, first)) Tabs.SelectedItem = first.Item;
            }
            RefreshTabsUi();
        }

        private void UngroupAll(string id) { foreach (var t in _tabs.Where(x => x.Group == id)) t.Group = null; RefreshTabsUi(); }

        private void CloseGroup(string id)
        {
            var members = _tabs.Where(t => t.Group == id).ToList();
            if (members.Count == 0) return;
            var keep = _tabs.FirstOrDefault(t => t.Group != id);
            if (keep != null) Tabs.SelectedItem = keep.Item;
            foreach (var t in members) CloseTab(t);
        }

        private void ApplyTabOrder(List<BrowserTab> order)
        {
            _reordering = true;
            try
            {
                var sel = Tabs.SelectedItem;
                Tabs.Items.Clear(); _tabs.Clear();
                foreach (var t in order) { _tabs.Add(t); Tabs.Items.Add(t.Item); }
                if (sel != null) Tabs.SelectedItem = sel;
            }
            finally { _reordering = false; }
            ShowActiveWebView();
        }

        // Keeps groups together and pinned tabs first, and redraws the group marks and collapsed state.
        private void RefreshTabsUi()
        {
            try
            {
                foreach (var id in _groups.Keys.ToList()) if (!_tabs.Any(t => t.Group == id)) { _groups.Remove(id); _collapsedGroups.Remove(id); }
                var items = _tabs.Select((t, i) => new TabGroups.ArrangeItem(i, t.Pinned ? null : t.Group, t.Pinned)).ToList();
                var order = TabGroups.Arrange(items);
                if (!order.SequenceEqual(Enumerable.Range(0, _tabs.Count))) ApplyTabOrder(order.Select(i => _tabs[i]).ToList());

                foreach (var t in _tabs)
                {
                    t.GroupMark.Visibility = Visibility.Collapsed; t.Item.Visibility = Visibility.Visible;
                }
                foreach (var g in _groups.Values)
                {
                    var members = _tabs.Where(t => t.Group == g.Id).ToList();
                    bool collapsed = _collapsedGroups.Contains(g.Id);
                    var color = (Color)ColorConverter.ConvertFromString(g.Color);
                    for (int i = 0; i < members.Count; i++)
                    {
                        var m = members[i];
                        m.GroupMark.Foreground = new SolidColorBrush(color);
                        m.GroupMark.Text = i == 0 ? ((collapsed ? "▸ " : "● ") + g.Name + (collapsed ? " (" + members.Count + ")" : "")) : "●";
                        m.GroupMark.ToolTip = g.Name;
                        m.GroupMark.Visibility = Visibility.Visible;
                        if (collapsed && i > 0) m.Item.Visibility = Visibility.Collapsed;
                    }
                }
                var a = Active;
                if (a != null && a.Item.Visibility != Visibility.Visible) Tabs.SelectedItem = a.Group != null ? _tabs.First(t => t.Group == a.Group).Item : a.Item;
                foreach (var t in _tabs) SetHeader(t, "");
            }
            catch { }
        }

        // ---- split view ---------------------------------------------------------------------------------------------------
        private BrowserTab? SplitPartner(BrowserTab? t) =>
            t == null ? null : ReferenceEquals(t, _splitA) ? _splitB : ReferenceEquals(t, _splitB) ? _splitA : null;

        private void StartSplit(BrowserTab a, BrowserTab b)
        {
            if (ReferenceEquals(a, b)) return;
            _splitA = a; _splitB = b;
            Tabs.SelectedItem = a.Item; ShowActiveWebView();
            _splitTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _splitTimer.Tick -= SplitTimer_Tick; _splitTimer.Tick += SplitTimer_Tick; _splitTimer.Start();
            Status("split view: click a side to work in it; drag the bar to resize");
        }

        private void EndSplit()
        {
            _splitA = _splitB = null; _splitTimer?.Stop();
            ShowActiveWebView(); Status("split view closed");
        }

        // the side you click becomes the active tab (address bar, star, zoom, find follow it)
        private void SplitTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                var a = Active; var p = SplitPartner(a);
                if (a == null || p == null) return;
                if (p.Web.IsKeyboardFocusWithin && !a.Web.IsKeyboardFocusWithin) Tabs.SelectedItem = p.Item;
            }
            catch { }
        }

        private void ApplySplitLayout(BrowserTab? active, BrowserTab? partner)
        {
            try
            {
                WebHost.ColumnDefinitions.Clear();
                foreach (var t in _tabs) Grid.SetColumn(t.Web, 0);
                if (active != null && partner != null)
                {
                    var left = _tabs.IndexOf(active) < _tabs.IndexOf(partner) ? active : partner;
                    var right = ReferenceEquals(left, active) ? partner : active;
                    WebHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    WebHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
                    WebHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    Grid.SetColumn(left.Web, 0); Grid.SetColumn(right.Web, 2);
                    Grid.SetColumn(SplitBar, 1); SplitBar.Visibility = Visibility.Visible;
                }
                else SplitBar.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        // ---- restoring your tabs ------------------------------------------------------------------------------------------
        private string SessionPath() => Path.Combine(_repoRoot, "runtime", "session.v1.enc");

        private SessionData? SnapshotSession()
        {
            var saved = _tabs.Where(t => !t.Private && !t.IsInternal && LaunchArgs.Parse(t.CurrentUrl) != null).Take(SessionRules.MaxTabs).ToList();
            if (saved.Count == 0) return null;
            var tabs = saved.Select(t => new SessionTab(LaunchArgs.Parse(t.CurrentUrl)!, t.Pinned, t.Pinned ? null : t.Group)).ToList();
            var used = new HashSet<string>(tabs.Where(t => t.Group != null).Select(t => t.Group!));
            var groups = _groups.Values.Where(g => used.Contains(g.Id)).Select(g => new SessionGroup(g.Id, g.Name, g.Color, _collapsedGroups.Contains(g.Id))).ToList();
            var a = Active; int active = a == null ? 0 : Math.Max(0, saved.IndexOf(a));
            return new SessionData(tabs, groups, active);
        }

        // Only when you chose "continue where I left off". Encrypted like history and bookmarks (Windows DPAPI, this user only).
        private void SaveSession(bool force = false)
        {
            try
            {
                // saved while the start-up choice keeps tabs ("continue" or "ask me"), or once when the close window said "reopen these tabs"
                if ((!force && _startupMode == "start") || !_preflightOk) return;
                var d = SnapshotSession();
                if (d == null) ClearSession(); else { WriteSecure(SessionPath(), SessionRules.Serialize(d)); SaveBookmarkIcons(); }
            }
            catch { }
        }

        private void ClearSession() { try { if (File.Exists(SessionPath())) File.Delete(SessionPath()); } catch { } }

        private async Task OpenStartupTabsAsync()
        {
            var d = ChooseStartupSession();   // reopen last time's tabs, ask, or start fresh (see MainWindow.CloseGuard.cs)
            if (d != null) { await RestoreSessionAsync(d); if (_tabs.Count > 0) { StartSessionTimer(); return; } }
            await OpenNewTabAsync();
            StartSessionTimer();
        }

        private void StartSessionTimer()
        {
            if (_syncOn) _ = SyncNowAsync(true);
            UpdateJumpList();
            if (_sessionTimer != null) return;
            _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
            _sessionTimer.Tick += (_, __) => SaveSession();
            _sessionTimer.Start();
        }

        // Tabs are created at once but only the one you were on loads its page; the others load when you open them.
        private async Task RestoreSessionAsync(SessionData d)
        {
            foreach (var g in d.Groups) { _groups[g.Id] = new GroupInfo(g.Id, g.Name, g.Color); if (g.Collapsed) _collapsedGroups.Add(g.Id); }
            BrowserTab? sel = null; int i = 0;
            foreach (var st in d.Tabs)
            {
                var t = await NewTabCoreAsync("New tab");
                if (t == null) { i++; continue; }
                t.Internal = ""; t.CurrentUrl = st.Url; t.PendingUrl = st.Url; t.Pinned = st.Pinned; t.Group = st.Group;
                t.CurrentTitle = TileRules.HostOf(st.Url) ?? st.Url;
                try { t.Fav.Source = BookmarkIcon(TileRules.HostOf(st.Url) ?? ""); } catch { }   // the saved icon until the page loads
                if (st.Pinned) { t.CloseBtn.Visibility = Visibility.Collapsed; t.Header.MaxWidth = 70; }
                if (i == d.Active) sel = t;
                i++;
            }
            RefreshTabsUi();
            if (sel != null) Tabs.SelectedItem = sel.Item;
            ShowActiveWebView();
            if (sel != null) LoadPending(sel);   // selecting the tab that was already selected (the last one) does not fire SelectionChanged
        }

        // ---- reader mode and picture-in-picture ---------------------------------------------------------------------------
        private async void MenuReader_Click(object sender, RoutedEventArgs e)
        {
            var a = Active;
            if (a == null || a.IsInternal || !a.Ready) { Status("reader mode works on web pages"); return; }
            try
            {
                _readerJs ??= ReadResource("reader.js") ?? "";
                if (_readerJs.Length == 0) { Status("reader mode is not available in this build"); return; }
                var r = (await a.Web.CoreWebView2.ExecuteScriptAsync("(function(g){" + _readerJs + "\n})(window)")).Trim('"');
                Status(r == "on" ? "reader mode on (Esc to close)" : r == "off" ? "reader mode off" : "no article text found on this page");
            }
            catch (Exception ex) { Status("reader mode failed: " + ex.Message); }
        }

        private async void MenuPip_Click(object sender, RoutedEventArgs e)
        {
            var a = Active;
            if (a == null || a.IsInternal || !a.Ready) { Status("picture-in-picture works on pages with a video"); return; }
            try
            {
                _pipJs ??= ReadResource("pip.js") ?? "";
                if (_pipJs.Length == 0) { Status("picture-in-picture is not available in this build"); return; }
                // run as if you had clicked in the page: browsers only allow picture-in-picture after a click
                var args = JsonSerializer.Serialize(new { expression = "(async function(g){" + _pipJs + "\n})(window)", userGesture = true, awaitPromise = true, returnByValue = true });
                var raw = await a.Web.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", args);
                string res = "";
                using (var d = JsonDocument.Parse(raw)) if (d.RootElement.TryGetProperty("result", out var r) && r.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String) res = v.GetString() ?? "";
                Status(res == "opened" ? "picture-in-picture opened" : res == "closed" ? "picture-in-picture closed" : res == "novideo" ? "no video found on this page (videos inside other sites' frames are not reachable)" : res == "error:unsupported" ? "this page does not allow picture-in-picture" : "picture-in-picture failed (" + res + ")");
            }
            catch (Exception ex) { Status("picture-in-picture failed: " + ex.Message); }
        }

        // ---- translate ---------------------------------------------------------------------------------------------------
        private async void MenuTranslate_Click(object sender, RoutedEventArgs e)
        {
            var a = Active;
            var url = a == null || a.IsInternal ? null : TranslateRules.Url(_translateService, _translateLang, a.CurrentUrl);
            if (a == null || url == null) { Status("translate works on web pages"); return; }
            var svc = TranslateRules.ServiceName(_translateService);
            var q = "Translate this page into " + TranslateRules.LangName(_translateLang) + "?\n\nTo do this, " + svc + " opens a translated copy of the page, so " + svc +
                    " learns the address of this page. Nothing is sent until you press Yes. This tab is not changed; the translation opens in a new tab." +
                    (a.Private ? "\n\nThis is a private tab: the address still goes to " + svc + "." : "");
            if (MessageBox.Show(this, q, "Translate", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try
            {
                var t = await NewTabCoreAsync(a.Private ? "Private" : "New tab", a.Private);
                if (t == null) return;
                Tabs.SelectedItem = t.Item; ShowActiveWebView(); NavigateTab(t, url);
                _actions?.Append("translate.open", _translateService + ":" + _translateLang);   // the page address is not recorded
            }
            catch (Exception ex) { Status("translate failed: " + ex.Message); }
        }

        // ---- tab menu entries -----------------------------------------------------------------------------------------
        private void LoadPending(BrowserTab t)
        {
            if (t.PendingUrl == null || !t.Ready) return;
            var u = t.PendingUrl; t.PendingUrl = null;
            NavigateTab(t, u);
        }

        private void FillGroupMenu(BrowserTab tab, ContextMenu cm, Func<string, Action, bool, MenuItem> mk)
        {
            var add = new MenuItem { Header = "Add to group", IsEnabled = !tab.Pinned };
            foreach (var g in _groups.Values)
            {
                var id = g.Id;
                var mi = mk((tab.Group == id ? "\u2713 " : "") + g.Name, () => AssignGroup(tab, id), true);
                add.Items.Add(mi);
            }
            if (_groups.Count > 0) add.Items.Add(new Separator());
            add.Items.Add(mk("New group...", () => NewGroupFor(tab), _groups.Count < TabGroups.MaxGroups));
            cm.Items.Add(add);
            if (tab.Group != null && _groups.ContainsKey(tab.Group))
            {
                var gid = tab.Group;
                bool collapsed = _collapsedGroups.Contains(gid);
                cm.Items.Add(mk("Remove from group", () => RemoveFromGroup(tab), true));
                cm.Items.Add(mk("Rename group...", () => RenameGroup(gid), true));
                cm.Items.Add(mk(collapsed ? "Expand group" : "Collapse group", () => ToggleGroupCollapsed(gid), true));
                cm.Items.Add(mk("Ungroup all", () => UngroupAll(gid), true));
                cm.Items.Add(mk("Close group", () => CloseGroup(gid), true));
            }
            cm.Items.Add(new Separator());
            if (_splitA != null) cm.Items.Add(mk("Close split view", () => EndSplit(), true));
            var split = new MenuItem { Header = "Split view with", };
            foreach (var o in _tabs.Where(x => !ReferenceEquals(x, tab) && x.Private == tab.Private && x.Ready))
            {
                var other = o;
                split.Items.Add(mk(other.CurrentTitle.Length > 40 ? other.CurrentTitle.Substring(0, 40) + "..." : other.CurrentTitle, () => { LoadPending(other); StartSplit(tab, other); }, true));
            }
            if (split.Items.Count == 0) split.IsEnabled = false;
            cm.Items.Add(split);
        }

        // ---- menu glue ---------------------------------------------------------------------------------------------------
        private void MenuCloseSplit_Click(object sender, RoutedEventArgs e) { if (_splitA != null) EndSplit(); }

        // ---- start page tiles ---------------------------------------------------------------------------------------------
        private void HandleTileMessage(BrowserTab from, string msg)
        {
            try
            {
                if (msg.StartsWith("tile-hide:", StringComparison.Ordinal)) TileRules.Remove(_tilesCustom, _tilesHidden, msg.Substring("tile-hide:".Length));
                else if (msg.StartsWith("tile-add:", StringComparison.Ordinal))
                {
                    string raw; try { raw = Uri.UnescapeDataString(msg.Substring("tile-add:".Length)); } catch { raw = ""; }
                    if (!TileRules.AddCustom(_tilesCustom, _tilesHidden, raw, out var err)) { Status(err); return; }
                }
                else if (msg == "tile-reset") { _tilesCustom.Clear(); _tilesHidden.Clear(); Status("start page tiles reset"); }
                else return;
                SaveSettings(); ReloadStartTabs();
                if (from.Internal == "settings") LoadInternal(from, "settings");
            }
            catch (Exception ex) { Status("tile change failed: " + ex.Message); }
        }

        // ---- settings: startup, translate, tiles --------------------------------------------------------------------------
        private string WorkspaceSettingsHtml()
        {
            var sb = new StringBuilder();
            sb.Append("<h1 style='font-size:16px'>Tabs, startup and tools</h1>");
            sb.Append(StartupSettingsRowsHtml());
            sb.Append("<div class='row'><div><div class='t'>Translate</div><div class='u'>Menu, Page tools, Translate this page opens a translated copy in a new tab and asks first, because the service you choose sees the page address. Translating happens in that service, not on this computer.</div></div><div class='ts'>" +
                      "<select onchange=\"send('ws-lang:'+this.value)\" style='background:#0e1015;color:#e8e8e8;border:1px solid #333844;border-radius:6px;padding:6px'>" +
                      string.Concat(TranslateRules.Languages.Select(l => "<option value='" + l.Code + "'" + (l.Code == _translateLang ? " selected" : "") + ">" + Esc(l.Name) + "</option>")) + "</select> " +
                      string.Concat(TranslateRules.Services.Select(s => "<a class='btn" + (s.Id == _translateService ? "" : " ghost") + "' onclick=\"send('ws-svc:" + s.Id + "')\">" + Esc(s.Name) + "</a> ")) + "</div></div>");
            sb.Append("<div class='row'><div><div class='t'>Start page tiles</div><div class='u'>Hover a tile on the start page to remove it, or use Add a site. " + _tilesCustom.Count + " added by you, " + _tilesHidden.Count + " hidden.</div></div><div class='ts'><a class='btn ghost' onclick=\"send('tile-reset')\">Reset tiles</a></div></div>");
            sb.Append(CustomizeSettingsRowsHtml());
            sb.Append("<div class='row'><div><div class='t'>Tab groups, split view, reader mode</div><div class='u'>Right-click a tab for groups (name, colour, collapse) and split view. Menu, Page tools has Reader mode and Picture-in-picture.</div></div></div>");
            return sb.ToString();
        }

        private void HandleWorkspaceMessage(BrowserTab from, string msg)
        {
            try
            {
                if (msg == "ws-startup:start") { _startupMode = "start"; ClearSession(); }
                else if (msg == "ws-startup:restore") { _startupMode = "restore"; SaveSession(); }
                else if (msg == "ws-startup:ask") { _startupMode = "ask"; SaveSession(); }
                else if (msg == "ws-closeask:on") _closeAsk = "on";
                else if (msg == "ws-closeask:off") _closeAsk = "off";
                else if (msg.StartsWith("ws-lang:", StringComparison.Ordinal)) _translateLang = TranslateRules.NormalizeLang(msg.Substring("ws-lang:".Length));
                else if (msg.StartsWith("ws-svc:", StringComparison.Ordinal)) _translateService = TranslateRules.NormalizeService(msg.Substring("ws-svc:".Length));
                else return;
                SaveSettings();
                if (from.Internal == "settings") LoadInternal(from, "settings");
            }
            catch (Exception ex) { Status("setting failed: " + ex.Message); }
        }

        private const string TileScript =
            "<script>function tileHide(e,h){e.preventDefault();e.stopPropagation();window.chrome.webview.postMessage('tile-hide:'+h)}" +
            "function tileAdd(e){e.preventDefault();var v=prompt('Site address, for example example.com');if(v)window.chrome.webview.postMessage('tile-add:'+encodeURIComponent(v))}</script>";
    }
}
