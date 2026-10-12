using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Recognition.Browser
{
    // Your library and what the browser has done for you: bookmarks (rename, open in a private tab), downloads (where they go, progress,
    // pause), history with times and clearing by time range, and the Overview page with live numbers. Everything is computed on this
    // computer from data the browser already keeps; nothing here makes a web request.
    public partial class MainWindow
    {
        private string _downloadDir = "";                         // "" = the Windows default; persisted as download_dir
        private bool _downloadAsk;                                // ask where to save each time; persisted as download_ask
        private readonly Dictionary<string, long> _blockedByHost = new(StringComparer.Ordinal);   // this session, for the Overview page
        private readonly DateTime _sessionStartUtc = DateTime.UtcNow;
        private DateTime _dlLastRefreshUtc = DateTime.MinValue;

        // ---- small formatting helpers ----------------------------------------------------------------------------------
        private static string LocalTime(string iso)
        {
            if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)) return iso ?? "";
            var l = d.ToLocalTime();
            return l.ToString("MMM d, yyyy h:mm tt", CultureInfo.CurrentCulture);
        }
        private static DateTime? LocalOf(string iso) =>
            DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d.ToLocalTime() : (DateTime?)null;
        private static string DayLabel(DateTime local)
        {
            var today = DateTime.Now.Date;
            if (local.Date == today) return "Today, " + local.ToString("dddd MMM d", CultureInfo.CurrentCulture);
            if (local.Date == today.AddDays(-1)) return "Yesterday, " + local.ToString("dddd MMM d", CultureInfo.CurrentCulture);
            return local.ToString("dddd, MMM d, yyyy", CultureInfo.CurrentCulture);
        }
        private static string SizeText(long b)
        {
            if (b <= 0) return "";
            if (b < 1024) return b + " B";
            if (b < 1024 * 1024) return (b / 1024.0).ToString("0.#", CultureInfo.CurrentCulture) + " KB";
            if (b < 1024L * 1024 * 1024) return (b / 1048576.0).ToString("0.#", CultureInfo.CurrentCulture) + " MB";
            return (b / 1073741824.0).ToString("0.##", CultureInfo.CurrentCulture) + " GB";
        }
        private static bool HostOk(string h) => h.Length is > 0 and <= 253 && h.All(c => char.IsAsciiLetterOrDigit(c) || c == '.' || c == '-' || c == '_');
        private static string Dec(string s) { try { return Uri.UnescapeDataString(s); } catch { return ""; } }

        private const string ToolbarCss =
            ".bar{display:flex;gap:8px;flex-wrap:wrap;align-items:center;margin:0 0 16px}" +
            ".in{background:#0e1015;color:#e8e8e8;border:1px solid #333844;border-radius:6px;padding:7px 9px;font-size:13px}" +
            ".day{margin:22px 0 6px;font-size:12px;letter-spacing:.4px;text-transform:uppercase;color:#8b93a2}" +
            ".pill2{display:inline-block;border:1px solid #333844;border-radius:999px;padding:1px 8px;font-size:11px;color:#aab1bd}" +
            ".bartrack{height:6px;background:#242933;border-radius:4px;overflow:hidden;margin-top:6px}.barfill{height:100%;background:#4c9bf0}" +
            ".cards{display:grid;grid-template-columns:repeat(auto-fill,minmax(190px,1fr));gap:12px;margin:10px 0 6px}" +
            ".card{padding:14px 16px;background:#1b1f27;border:1px solid #262b34;border-radius:12px}" +
            ".card b{display:block;font-size:26px;font-weight:600;color:#6fa8f0}.card span{color:#8b93a2;font-size:12px}" +
            ".sec{font-size:15px;font-weight:600;margin:26px 0 8px}";

        // ---- bookmarks ---------------------------------------------------------------------------------------------------
        private string BookmarksHtml()
        {
            var sb = new StringBuilder(PageHead);
            sb.Append("<style>" + ToolbarCss + "</style><title>Bookmarks</title><h1>Bookmarks</h1><div class='muted'>" + _bookmarks.Count +
                      " saved, encrypted on this computer. Right-click a bookmark on the bar for the same actions.</div>");
            sb.Append("<div class='bar'><input id='q' class='in' placeholder='Search bookmarks' style='flex:1;min-width:220px' oninput='flt()'>" +
                      "<a class='btn ghost' onclick=\"send('feel-import')\">Import bookmarks</a><a class='btn ghost' onclick=\"send('bm-bar')\">Show or hide the bar</a></div>");
            if (_bookmarks.Count == 0) sb.Append("<div class='empty'>No bookmarks yet. Click the star in the address bar to save a page.</div>");
            else
            {
                void Row(Bookmark b)
                {
                    var title = string.IsNullOrEmpty(b.Title) ? b.Url : b.Title;
                    sb.Append("<div class='row' data-s='" + Attr((title + " " + b.Url + " " + b.Folder).ToLowerInvariant()) + "'><div style='min-width:0'><a class='t' href='" + Attr(b.Url) + "'>" + Esc(title) +
                              "</a><div class='u' style='overflow:hidden;text-overflow:ellipsis'>" + Esc(b.Url) + "</div>" +
                              "<div class='u'>Saved " + Esc(LocalTime(b.Ts)) + "</div></div><div class='ts' style='white-space:nowrap'>" +
                              "<a class='btn ghost' data-u='" + Attr(b.Url) + "' data-t='" + Attr(title) + "' onclick=\"bm('rename',this)\">Rename</a> " +
                              "<a class='btn ghost' data-u='" + Attr(b.Url) + "' onclick=\"bm('folder',this)\">Folder</a> " +
                              "<a class='btn ghost' data-u='" + Attr(b.Url) + "' onclick=\"bm('tab',this)\">New tab</a> " +
                              "<a class='btn ghost' data-u='" + Attr(b.Url) + "' onclick=\"bm('private',this)\">Private tab</a> " +
                              "<a class='btn ghost' data-u='" + Attr(b.Url) + "' onclick=\"bm('copy',this)\">Copy</a> " +
                              "<a class='btn ghost' data-u='" + Attr(b.Url) + "' onclick=\"bm('remove',this)\">Remove</a></div></div>");
                }
                var folders = BookmarkFolders.Names(_bookmarks.Select(x => x.Folder));
                foreach (var f in folders)
                {
                    var inF = _bookmarks.Where(x => string.Equals(x.Folder, f, StringComparison.OrdinalIgnoreCase)).Reverse().ToList();
                    sb.Append("<div class='day'>&#128193; " + Esc(f) + " (" + inF.Count + ") &nbsp; <a class='btn ghost' data-f='" + Attr(f) + "' onclick='fr(this)'>Rename folder</a> <a class='btn ghost' data-f='" + Attr(f) + "' onclick='fd(this)'>Delete folder</a></div>");
                    foreach (var b in inF) Row(b);
                }
                var loose = _bookmarks.Where(x => string.IsNullOrEmpty(x.Folder)).Reverse().ToList();
                if (folders.Count > 0 && loose.Count > 0) sb.Append("<div class='day'>No folder</div>");
                foreach (var b in loose) Row(b);
            }
            sb.Append("<script>function bm(a,el){var u=el.dataset.u;if(a==='rename'){var n=prompt('Rename bookmark',el.dataset.t||'');if(n===null)return;send('bm-rename:'+encodeURIComponent(u)+'|'+encodeURIComponent(n));}else send('bm-'+a+':'+encodeURIComponent(u));}" +
                      "function fr(el){var n=prompt('Rename folder',el.dataset.f||'');if(n===null)return;send('bm-frename:'+encodeURIComponent(el.dataset.f)+'|'+encodeURIComponent(n));}function fd(el){send('bm-fdelete:'+encodeURIComponent(el.dataset.f));}"+
                      "function flt(){var q=document.getElementById('q').value.toLowerCase();document.querySelectorAll('.row[data-s]').forEach(function(r){r.style.display=r.dataset.s.indexOf(q)<0?'none':''});}</script>");
            sb.Append(SendScript()).Append(PageFoot);
            return sb.ToString();
        }

        private async void HandleBookmarkMessage(BrowserTab from, string msg)
        {
            try
            {
                if (msg == "bm-bar") { _bookmarksBarOn = !_bookmarksBarOn; SaveSettings(); ApplyLayout(); return; }
                var colon = msg.IndexOf(':'); if (colon < 0) return;
                var verb = msg.Substring(0, colon); var rest = msg.Substring(colon + 1);
                if (verb == "bm-frename")
                {
                    var bar0 = rest.IndexOf('|'); if (bar0 < 0) return;
                    var oldName = Dec(rest.Substring(0, bar0)); var newName = BookmarkFolders.Clean(Dec(rest.Substring(bar0 + 1)));
                    if (newName.Length == 0) { Status("a folder needs a name"); return; }
                    var target = BookmarkFolders.Resolve(_bookmarks.Where(x => !string.Equals(x.Folder, oldName, StringComparison.OrdinalIgnoreCase)).Select(x => x.Folder), newName) ?? newName;
                    foreach (var bk in _bookmarks.Where(x => string.Equals(x.Folder, oldName, StringComparison.OrdinalIgnoreCase))) bk.Folder = target;
                    SaveBookmarks(); _actions?.Append("bookmark.folder_rename");
                    if (from.Internal == "bookmarks" && _tabs.Contains(from)) LoadInternal(from, "bookmarks");
                    return;
                }
                if (verb == "bm-fdelete")
                {
                    var fname = Dec(rest);
                    var n = _bookmarks.Count(x => string.Equals(x.Folder, fname, StringComparison.OrdinalIgnoreCase));
                    if (n == 0) return;
                    if (MessageBox.Show(this, "Delete the folder \"" + fname + "\"? Its " + n + " bookmark(s) are kept, just not in a folder.", "Delete folder", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    foreach (var bk in _bookmarks.Where(x => string.Equals(x.Folder, fname, StringComparison.OrdinalIgnoreCase))) bk.Folder = "";
                    SaveBookmarks(); _actions?.Append("bookmark.folder_delete");
                    if (from.Internal == "bookmarks" && _tabs.Contains(from)) LoadInternal(from, "bookmarks");
                    return;
                }
                string url, title = "";
                if (verb == "bm-rename") { var bar = rest.IndexOf('|'); if (bar < 0) return; url = Dec(rest.Substring(0, bar)); title = Dec(rest.Substring(bar + 1)); }
                else url = Dec(rest);
                var b = _bookmarks.FirstOrDefault(x => x.Url == url); if (b == null) return;
                switch (verb)
                {
                    case "bm-rename": RenameBookmark(b, title); break;
                    case "bm-folder": ShowFolderMenu(b); return;
                    case "bm-tab": OpenBookmark(b.Url, true); break;
                    case "bm-private": await OpenBookmarkPrivateAsync(b.Url); break;
                    case "bm-copy": try { Clipboard.SetText(b.Url); Status("address copied"); } catch { Status("could not copy"); } break;
                    case "bm-remove":
                        _bookmarks.Remove(b); _actions?.Append("bookmark.remove", b.Url); SaveBookmarks();
                        var a = Active; if (a != null) UpdateStar(a);
                        break;
                    default: return;
                }
                if (from.Internal == "bookmarks" && _tabs.Contains(from)) LoadInternal(from, "bookmarks");
            }
            catch (Exception ex) { Status("bookmark action failed: " + ex.Message); }
        }

        // A menu at the mouse: the existing folders, a new folder, or no folder.
        private void ShowFolderMenu(Bookmark b)
        {
            var cm = new System.Windows.Controls.ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
            void Add(string header, Action act, bool enabled = true)
            {
                var mi = new System.Windows.Controls.MenuItem { Header = header, IsEnabled = enabled };
                mi.Click += (_, __) => { try { act(); } catch { } };
                cm.Items.Add(mi);
            }
            var names = BookmarkFolders.Names(_bookmarks.Select(x => x.Folder));
            foreach (var n in names) { var name = n; Add((string.Equals(b.Folder, name, StringComparison.OrdinalIgnoreCase) ? "\u2713 " : "") + name, () => MoveBookmark(b, name)); }
            if (names.Count > 0) cm.Items.Add(new System.Windows.Controls.Separator());
            Add("New folder...", () => { var nm = PromptText("New folder", "Folder name", ""); if (nm != null) MoveBookmark(b, nm); }, names.Count < BookmarkFolders.MaxFolders);
            if (!string.IsNullOrEmpty(b.Folder)) Add("Remove from folder", () => MoveBookmark(b, ""));
            cm.IsOpen = true;
        }

        private void MoveBookmark(Bookmark b, string folder)
        {
            string target = "";
            if (folder.Length > 0)
            {
                var r = BookmarkFolders.Resolve(_bookmarks.Select(x => x.Folder), folder);
                if (r == null) { Status("that folder name cannot be used (or there are already " + BookmarkFolders.MaxFolders + " folders)"); return; }
                target = r;
            }
            b.Folder = target; SaveBookmarks(); _actions?.Append("bookmark.folder");
            Status(target.Length == 0 ? "removed from its folder" : "moved to " + target);
            var a = Active; if (a != null && a.Internal == "bookmarks") LoadInternal(a, "bookmarks");
        }

        private void RenameBookmark(Bookmark b, string title)
        {
            var clean = new string((title ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (clean.Length > 200) clean = clean.Substring(0, 200);
            if (clean.Length == 0) { Status("a bookmark needs a name"); return; }
            b.Title = clean; SaveBookmarks(); _actions?.Append("bookmark.rename");
            Status("bookmark renamed");
        }

        private async Task OpenBookmarkPrivateAsync(string url)
        {
            var t = await NewTabCoreAsync("Private", true); if (t == null) return;
            Tabs.SelectedItem = t.Item; ShowActiveWebView(); NavigateTab(t, url);
        }

        // ---- downloads: where they go, progress, pause --------------------------------------------------------------------
        private bool DownloadDirOk() => _downloadDir.Length > 0 && SoteriaBridge.IsSafeRoot(_downloadDir) && Directory.Exists(_downloadDir);

        private static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path) ?? ""; var name = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path);
            for (int i = 1; i < 1000; i++) { var p = Path.Combine(dir, name + " (" + i + ")" + ext); if (!File.Exists(p)) return p; }
            return path;
        }

        // Called for every download after the risk question. Returns false when the download was cancelled here.
        private bool ApplyDownloadLocation(CoreWebView2DownloadStartingEventArgs e)
        {
            try
            {
                var name = Path.GetFileName(e.ResultFilePath);
                if (_downloadAsk)
                {
                    var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Save download as", FileName = name, OverwritePrompt = true };
                    if (DownloadDirOk()) dlg.InitialDirectory = _downloadDir;
                    if (dlg.ShowDialog(this) != true) { e.Cancel = true; Status("download cancelled"); return false; }
                    e.ResultFilePath = dlg.FileName;
                }
                else if (DownloadDirOk()) e.ResultFilePath = UniquePath(Path.Combine(_downloadDir, name));
            }
            catch { }
            return true;
        }

        private void TrackDownload(DownloadRec rec, CoreWebView2DownloadOperation op)
        {
            rec.Op = op;
            op.BytesReceivedChanged += (o, __) => Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    rec.Received = Convert.ToInt64(op.BytesReceived);
                    var tot = op.TotalBytesToReceive; rec.Total = tot == null ? 0 : Convert.ToInt64(tot);
                    if (rec.Total > 0) Status("downloading " + Path.GetFileName(rec.Path) + "  " + (int)(100.0 * rec.Received / rec.Total) + "%  (" + SizeText(rec.Received) + " of " + SizeText(rec.Total) + ")");
                    var a = Active;
                    if (a != null && a.Internal == "downloads" && (DateTime.UtcNow - _dlLastRefreshUtc).TotalMilliseconds > 250) { _dlLastRefreshUtc = DateTime.UtcNow; PostDownloadProgress(a); }
                }
                catch { }
            }));
        }

        // Progress is pushed into the open page so it does not reload (a reload would flicker and lose the scroll position).
        private void PostDownloadProgress(BrowserTab page)
        {
            try
            {
                var items = new List<object>();
                for (int i = 0; i < _downloads.Count; i++)
                {
                    var d = _downloads[i]; if (d.State != "InProgress") continue;
                    int pct = d.Total > 0 ? (int)Math.Min(100, 100.0 * d.Received / d.Total) : 0;
                    items.Add(new { i, pct, text = pct + "%  " + SizeText(d.Received) + (d.Total > 0 ? " of " + SizeText(d.Total) : "") });
                }
                page.Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "dlprog", items }));
            }
            catch { }
        }

        private string DownloadsHtml()
        {
            var sb = new StringBuilder(PageHead);
            var active = _downloads.Count(d => d.State == "InProgress");
            var folder = DownloadDirOk() ? _downloadDir : "the Windows Downloads folder";
            sb.Append("<style>" + ToolbarCss + "</style><title>Downloads</title><h1>Downloads</h1><div class='muted'>" + _downloads.Count + " in the list" + (active > 0 ? ", " + active + " in progress" : "") +
                      ". The list is encrypted on this computer.</div>");
            sb.Append("<div class='row'><div><div class='t'>Save files to</div><div class='u'>" + Esc(folder) + "</div></div><div class='ts'><a class='btn' onclick=\"send('dl-dir')\">Change...</a> " +
                      (DownloadDirOk() ? "<a class='btn ghost' onclick=\"send('dl-dir-default')\">Use default</a> " : "") + "<a class='btn ghost' onclick=\"send('dl-opendir')\">Open folder</a></div></div>");
            sb.Append("<div class='row'><div><div class='t'>Ask where to save each time</div><div class='u'>A Save As window appears for every download.</div></div><div class='ts'><a class='btn" + (_downloadAsk ? "" : " ghost") + "' onclick=\"send('dl-ask:" + (_downloadAsk ? "off" : "on") + "')\">" + (_downloadAsk ? "On" : "Off") + "</a></div></div>");
            sb.Append("<div class='bar' style='margin-top:14px'><input id='q' class='in' placeholder='Search downloads' style='flex:1;min-width:220px' oninput='flt()'>" +
                      "<a class='btn ghost' onclick=\"send('dl-clear:done')\">Clear finished</a><a class='btn ghost' onclick=\"send('dl-clear:all')\">Clear list</a></div>");
            sb.Append("<div class='muted'>Clearing removes entries from this list only; the files stay where they are.</div>");
            if (_downloads.Count == 0) sb.Append("<div class='empty'>No downloads yet.</div>");
            else
                for (int i = _downloads.Count - 1, shown = 0; i >= 0 && shown < 500; i--, shown++)
                {
                    var d = _downloads[i]; var name = Path.GetFileName(d.Path); if (string.IsNullOrEmpty(name)) name = d.Url;
                    var running = d.State == "InProgress";
                    int pct = d.Total > 0 ? (int)Math.Min(100, 100.0 * d.Received / d.Total) : 0;
                    var stateWord = d.State switch { "Completed" => "Finished", "InProgress" => "Downloading", "Interrupted" => "Stopped", _ => d.State };
                    if (running && d.Op != null) { try { if (d.Op.CanResume) stateWord = "Paused"; } catch { } }
                    sb.Append("<div class='row' data-s='" + Attr((name + " " + d.Url).ToLowerInvariant()) + "'><div style='min-width:0;flex:1'><div class='t'>" + Esc(name) + " <span class='pill2'>" + Esc(stateWord) + "</span></div>" +
                              "<div class='u'>" + Esc(TryHost(d.Url) ?? d.Url) + "  &middot;  " + Esc(LocalTime(d.Ts)) + (d.Total > 0 ? "  &middot;  " + Esc(SizeText(running ? d.Received : d.Total)) + (running ? " of " + Esc(SizeText(d.Total)) : "") : "") + "</div>" +
                              (running ? "<div class='bartrack'><div class='barfill' id='pf" + i + "' style='width:" + pct + "%'></div></div><div class='u' id='pt" + i + "'>" + pct + "%  " + Esc(SizeText(d.Received)) + (d.Total > 0 ? " of " + Esc(SizeText(d.Total)) : "") + "</div>" : "") +
                              "<div class='u' style='overflow:hidden;text-overflow:ellipsis'>" + Esc(d.Path) + "</div></div><div class='ts' style='white-space:nowrap'>");
                    if (running) sb.Append("<a class='btn ghost' onclick=\"send('dl-pause:" + i + "')\">Pause</a> <a class='btn ghost' onclick=\"send('dl-resume:" + i + "')\">Resume</a> <a class='btn ghost' onclick=\"send('dl-cancel:" + i + "')\">Cancel</a> ");
                    else
                    {
                        if (d.State == "Completed" && DownloadRules.Assess(name) == DownloadRisk.None) sb.Append("<a class='btn' onclick=\"send('dl-open:" + i + "')\">Open</a> ");
                        sb.Append("<a class='btn ghost' onclick=\"send('dl-show:" + i + "')\">Show in folder</a> <a class='btn ghost' onclick=\"send('dl-rm:" + i + "')\">Remove</a>");
                    }
                    sb.Append("</div></div>");
                }
            sb.Append("<script>function flt(){var q=document.getElementById('q').value.toLowerCase();document.querySelectorAll('.row[data-s]').forEach(function(r){r.style.display=r.dataset.s.indexOf(q)<0?'none':''});}" +
                      "window.chrome.webview.addEventListener('message',function(ev){var m=ev.data;if(!m||m.type!=='dlprog')return;m.items.forEach(function(x){var f=document.getElementById('pf'+x.i),t=document.getElementById('pt'+x.i);if(f)f.style.width=x.pct+'%';if(t)t.textContent=x.text;});});</script>");
            sb.Append(SendScript()).Append(PageFoot);
            return sb.ToString();
        }

        private void HandleDownloadMessage(BrowserTab from, string msg)
        {
            try
            {
                int Idx(string p) => int.TryParse(msg.Substring(p.Length), out var n) && n >= 0 && n < _downloads.Count ? n : -1;
                if (msg == "dl-dir")
                {
                    var f = PickFolder("Choose where downloads are saved");
                    if (f != null && SoteriaBridge.IsSafeRoot(f) && Directory.Exists(f)) { _downloadDir = f; SaveSettings(); _actions?.Append("download.folder"); Status("downloads will be saved to " + f); }
                    else if (f != null) Status("that folder cannot be used");
                }
                else if (msg == "dl-dir-default") { _downloadDir = ""; SaveSettings(); Status("downloads use the Windows default folder"); }
                else if (msg == "dl-opendir")
                {
                    var dir = DownloadDirOk() ? _downloadDir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    OpenFolder(dir);
                    return;
                }
                else if (msg == "dl-ask:on") { _downloadAsk = true; SaveSettings(); }
                else if (msg == "dl-ask:off") { _downloadAsk = false; SaveSettings(); }
                else if (msg == "dl-clear:done") { _downloads.RemoveAll(d => d.State != "InProgress"); SaveDownloads(); _actions?.Append("downloads.clear", "finished"); }
                else if (msg == "dl-clear:all") { _downloads.RemoveAll(d => d.State != "InProgress"); SaveDownloads(); _actions?.Append("downloads.clear", "all"); }
                else if (msg.StartsWith("dl-rm:", StringComparison.Ordinal)) { var i = Idx("dl-rm:"); if (i >= 0 && _downloads[i].State != "InProgress") { _downloads.RemoveAt(i); SaveDownloads(); } }
                else if (msg.StartsWith("dl-pause:", StringComparison.Ordinal)) { var i = Idx("dl-pause:"); if (i >= 0) _downloads[i].Op?.Pause(); }
                else if (msg.StartsWith("dl-resume:", StringComparison.Ordinal)) { var i = Idx("dl-resume:"); if (i >= 0) _downloads[i].Op?.Resume(); }
                else if (msg.StartsWith("dl-cancel:", StringComparison.Ordinal)) { var i = Idx("dl-cancel:"); if (i >= 0) _downloads[i].Op?.Cancel(); }
                else if (msg.StartsWith("dl-show:", StringComparison.Ordinal))
                {
                    var i = Idx("dl-show:"); if (i < 0) return;
                    var p = _downloads[i].Path;
                    if (File.Exists(p)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + p + "\"") { UseShellExecute = true });
                    else { var dir = Path.GetDirectoryName(p); if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) OpenFolder(dir); else Status("that file is no longer there"); }
                    return;
                }
                else if (msg.StartsWith("dl-open:", StringComparison.Ordinal))
                {
                    var i = Idx("dl-open:"); if (i < 0) return;
                    var d = _downloads[i];
                    // only finished files of types that cannot run code (the same check as the download warning)
                    if (d.State != "Completed" || DownloadRules.Assess(Path.GetFileName(d.Path)) != DownloadRisk.None) { Status("use Show in folder for this file"); return; }
                    if (!File.Exists(d.Path)) { Status("that file is no longer there"); return; }
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(d.Path) { UseShellExecute = true });
                    _actions?.Append("download.open");
                    return;
                }
                else return;
                if (from.Internal == "downloads" && _tabs.Contains(from)) LoadInternal(from, "downloads");
            }
            catch (Exception ex) { Status("download action failed: " + ex.Message); }
        }

        private string DownloadsSettingsHtml()
        {
            var folder = DownloadDirOk() ? _downloadDir : "the Windows Downloads folder";
            return "<h1 style='font-size:16px'>Downloads</h1>" +
                "<div class='row'><div><div class='t'>Save files to</div><div class='u'>" + Esc(folder) + "</div></div><div class='ts'><a class='btn' onclick=\"send('dl-dir')\">Change...</a> " +
                (DownloadDirOk() ? "<a class='btn ghost' onclick=\"send('dl-dir-default')\">Use default</a> " : "") + "</div></div>" +
                "<div class='row'><div><div class='t'>Ask where to save each time</div><div class='u'>A Save As window appears for every download.</div></div><div class='ts'><a class='btn" + (_downloadAsk ? "" : " ghost") + "' onclick=\"send('dl-ask:" + (_downloadAsk ? "off" : "on") + "')\">" + (_downloadAsk ? "On" : "Off") + "</a></div></div>" +
                "<div class='row'><div><div class='t'>Downloads list</div><div class='u'>Progress, pause, open and show in folder are on the Downloads page.</div></div><div class='ts'><a class='btn ghost' href='recognition:downloads'>Open Downloads</a> <a class='btn ghost' href='recognition:overview'>Overview and clear data</a></div></div>";
        }

        // ---- history with times, cleared by time range ----------------------------------------------------------------------
        private string HistoryHtml()
        {
            var sb = new StringBuilder(PageHead);
            var items = _history.Items;
            sb.Append("<style>" + ToolbarCss + "</style><title>History</title><h1>History</h1><div class='muted'>" + items.Count + " page" + (items.Count == 1 ? "" : "s") +
                      " visited, each with its time. Stored encrypted on this computer, never in private tabs.</div>");
            sb.Append("<div class='bar'><input id='q' class='in' placeholder='Search history' style='flex:1;min-width:220px' oninput='flt()'>" +
                      "<span class='u'>Clear</span><select id='rg' class='in'><option value='hour'>the last hour</option><option value='day'>the last 24 hours</option><option value='week'>the last 7 days</option><option value='month'>the last 4 weeks</option><option value='all'>everything</option></select>" +
                      "<a class='btn' onclick=\"send('hist-clear:'+document.getElementById('rg').value)\">Clear history</a></div>");
            if (items.Count == 0) sb.Append("<div class='empty'>No history yet.</div>");
            else
            {
                DateTime? lastDay = null;
                foreach (var h in Enumerable.Reverse(items).Take(1000))
                {
                    var l = LocalOf(h.Ts);
                    if (l != null && lastDay != l.Value.Date) { lastDay = l.Value.Date; sb.Append("<div class='day'>" + Esc(DayLabel(l.Value)) + "</div>"); }
                    var title = string.IsNullOrEmpty(h.Title) ? h.Url : h.Title;
                    sb.Append("<div class='row' data-s='" + Attr((title + " " + h.Url).ToLowerInvariant()) + "'><div style='min-width:0'><a class='t' href='" + Attr(h.Url) + "'>" + Esc(title) +
                              "</a><div class='u' style='overflow:hidden;text-overflow:ellipsis'>" + Esc(h.Url) + "</div></div><div class='ts'>" + Esc(l == null ? h.Ts : l.Value.ToString("h:mm:ss tt", CultureInfo.CurrentCulture)) + "</div></div>");
                }
                if (items.Count > 1000) sb.Append("<div class='muted' style='margin-top:14px'>Showing the newest 1,000 entries.</div>");
            }
            sb.Append("<script>function flt(){var q=document.getElementById('q').value.toLowerCase();document.querySelectorAll('.row[data-s]').forEach(function(r){r.style.display=r.dataset.s.indexOf(q)<0?'none':''});}</script>");
            sb.Append(SendScript()).Append(PageFoot);
            return sb.ToString();
        }

        private static DateTime? RangeStartUtc(string range) => range switch
        {
            "hour" => DateTime.UtcNow.AddHours(-1),
            "day" => DateTime.UtcNow.AddDays(-1),
            "week" => DateTime.UtcNow.AddDays(-7),
            "month" => DateTime.UtcNow.AddDays(-28),
            "all" => DateTime.MinValue,
            _ => null
        };
        private static string RangeWords(string range) => range switch
        {
            "hour" => "the last hour", "day" => "the last 24 hours", "week" => "the last 7 days", "month" => "the last 4 weeks", _ => "everything"
        };

        private int ClearHistoryRange(string range)
        {
            var from = RangeStartUtc(range); if (from == null) return -1;
            int n;
            if (from == DateTime.MinValue) { n = _history.Items.Count; _history.Clear(); }
            else n = _history.ClearSince(from.Value);
            _actions?.Append("history.clear", range + ":" + n);
            return n;
        }

        private void HandleHistoryMessage(BrowserTab from, string msg)
        {
            var range = msg.Substring("hist-clear:".Length);
            if (RangeStartUtc(range) == null) return;
            if (MessageBox.Show(this, "Clear history from " + RangeWords(range) + "?", "Clear history", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var n = ClearHistoryRange(range);
            Status("history cleared for " + RangeWords(range) + " (" + n + " entr" + (n == 1 ? "y" : "ies") + ")");
            if (from.Internal == "history" && _tabs.Contains(from)) LoadInternal(from, "history");
        }

        // ---- Overview ---------------------------------------------------------------------------------------------------------
        private string OverviewHtml()
        {
            var sb = new StringBuilder(PageHead);
            sb.Append("<style>" + ToolbarCss + @"</style><title>Overview</title><h1>Overview</h1>
<div class='muted'>What Recognition has done for you, and what it holds. Everything is counted on this computer. It updates while this page is open.</div>
<div class='bar'><a class='btn' onclick=""sc('newprivate')"">New private tab</a><a class='btn ghost' href='recognition:shield'>Shield</a><a class='btn ghost' href='recognition:downloads'>Downloads</a><a class='btn ghost' href='recognition:history'>History</a><a class='btn ghost' href='recognition:bookmarks'>Bookmarks</a><a class='btn ghost' href='recognition:settings'>Settings</a></div>
<div class='sec'>Protection</div><div class='cards' id='c_prot'></div>
<div class='u' id='prot_note'></div>
<div class='sec'>Most blocked trackers and ads this session</div><div id='trk'></div>
<div class='sec'>Cookies and sites</div><div class='cards' id='c_cook'></div><div id='ck'></div>
<div style='margin:8px 0'><a class='btn ghost' onclick=""send('ov-cookies-all')"">Delete all cookies</a></div>
<div class='sec'>Your library</div><div class='cards' id='c_lib'></div>
<div class='sec'>Most visited</div><div id='top'></div>
<div class='sec'>Filter lists</div><div id='lists'></div>
<div class='sec'>Connection and session</div><div class='cards' id='c_net'></div>
<div class='sec'>Clear browsing data</div>
<div class='row'><div><div class='t'>Choose what and how far back</div><div class='u'>Cookies and site data signs you out of sites. History here is Recognition's own list.</div>
<div style='margin-top:8px;display:flex;gap:12px;flex-wrap:wrap;align-items:center'><select id='rg' class='in'><option value='hour'>the last hour</option><option value='day'>the last 24 hours</option><option value='week'>the last 7 days</option><option value='month'>the last 4 weeks</option><option value='all'>everything</option></select>
<label><input type='checkbox' id='k_h' checked> History</label><label><input type='checkbox' id='k_c'> Cookies and site data</label><label><input type='checkbox' id='k_k'> Cached files</label><label><input type='checkbox' id='k_d'> Download list</label></div></div>
<div class='ts'><a class='btn' onclick='clr()'>Clear</a></div></div>
<div id='msg' class='u' style='min-height:18px;margin:8px 0'></div>
<script>
function $(i){return document.getElementById(i)}
function el(t,txt,cls){var e=document.createElement(t);if(txt!=null)e.textContent=txt;if(cls)e.className=cls;return e}
function sc(n){window.chrome.webview.postMessage('sc:'+n)}
function fmt(n){return Number(n).toLocaleString()}
function card(parent,num,label){var c=el('div',null,'card');c.appendChild(el('b',num));c.appendChild(el('span',label));parent.appendChild(c)}
function clr(){var f=($('k_h').checked?'h':'')+($('k_c').checked?'c':'')+($('k_k').checked?'k':'')+($('k_d').checked?'d':'');if(!f){$('msg').textContent='Choose something to clear.';return}send('ov-clear:'+$('rg').value+':'+f)}
function bars(parent,rows,empty,cmd){parent.textContent='';if(!rows.length){parent.appendChild(el('div',empty,'empty'));return}
  var max=Math.max.apply(null,rows.map(function(r){return r.n}))||1;
  rows.forEach(function(r){var row=el('div',null,'row');var d=el('div');d.style.flex='1';d.appendChild(el('div',r.name,'t'));var tr=el('div',null,'bartrack');var f=el('div',null,'barfill');f.style.width=Math.max(3,100*r.n/max)+'%';tr.appendChild(f);d.appendChild(tr);row.appendChild(d);
    var s=el('div',fmt(r.n),'ts');if(cmd){var a=el('a','Delete','btn ghost');a.style.marginLeft='10px';a.onclick=function(){send(cmd+encodeURIComponent(r.name))};s.appendChild(a)}row.appendChild(s);parent.appendChild(row)})}
function render(m){
  var p=m.prot;$('c_prot').textContent='';
  card($('c_prot'),fmt(p.blockedSession),'trackers and ads blocked this session');
  card($('c_prot'),fmt(p.blockedLifetime),'blocked since you started using Recognition');
  card($('c_prot'),fmt(p.blockedPage),'blocked on the tab you came from');
  card($('c_prot'),fmt(p.stripped),'tracking links cleaned this session');
  card($('c_prot'),fmt(p.fp),'fingerprint attempts intercepted');
  card($('c_prot'),fmt(p.popups+p.external),'pop-ups and program launches stopped');
  $('prot_note').textContent='Blocking is '+(p.blockingEnabled?'on':'OFF')+' · '+fmt(p.rules)+' filter rules loaded · fingerprint shield: '+p.shield+' · engine tracking prevention: '+p.tracking+' · HTTPS-first';
  bars($('trk'),m.trackers,'Nothing blocked yet this session.');
  var c=m.cookies;$('c_cook').textContent='';
  if(c.ok){card($('c_cook'),fmt(c.total),'cookies stored');card($('c_cook'),fmt(c.sites),'sites with cookies');card($('c_cook'),fmt(c.session),'end when you close the browser');card($('c_cook'),fmt(c.persistent),'stay until they expire');card($('c_cook'),fmt(c.tracker),'from known tracker domains');if(c.priv&&c.priv.ok)card($('c_cook'),fmt(c.priv.total),'in private tabs, across '+fmt(c.priv.sites)+' sites (erased when they close)');
    bars($('ck'),c.top.map(function(x){return {name:x.domain,n:x.count}}),'No cookies.','ov-cookie:')}
  else{$('ck').textContent='';$('ck').appendChild(el('div','Cookie details are not available yet. Open a page and look again.','empty'))}
  var l=m.lib;$('c_lib').textContent='';
  card($('c_lib'),fmt(l.bookmarks),'bookmarks');card($('c_lib'),fmt(l.history),'pages in history');card($('c_lib'),fmt(l.sites),'different sites visited');
  card($('c_lib'),fmt(l.downloads),'downloads in the list');card($('c_lib'),l.downloadBytes,'downloaded and still listed');card($('c_lib'),fmt(l.tabs),'tabs open'+(l.groups?' ('+l.groups+' group'+(l.groups==1?'':'s')+')':''));
  var t=$('top');t.textContent='';if(!m.top.length)t.appendChild(el('div','Visit a few sites and they show up here.','empty'));
  m.top.forEach(function(s){var r=el('div',null,'row');var d=el('div');var a=el('a',s.title,'t');a.setAttribute('href',s.url);d.appendChild(a);d.appendChild(el('div',s.host,'u'));r.appendChild(d);r.appendChild(el('div',fmt(s.visits)+' visits','ts'));t.appendChild(r)});
  var L=$('lists');L.textContent='';
  m.lists.forEach(function(x){var r=el('div',null,'row');var d=el('div');d.appendChild(el('div',x.name+(x.enabled?'':'  (off)'),'t'));d.appendChild(el('div',x.rules?fmt(x.rules)+' rules · updated '+(x.updated?x.updated.slice(0,10):'?'):'not downloaded','u'));r.appendChild(d);L.appendChild(r)});
  var n=m.net;$('c_net').textContent='';card($('c_net'),n.vpn,'connection');card($('c_net'),n.uptime,'this session');card($('c_net'),n.engine,'web engine');
}
window.chrome.webview.addEventListener('message',function(ev){var m=ev.data;if(!m)return;if(m.type==='ov')render(m);else if(m.type==='note')$('msg').textContent=m.text});
send('ov-state');setInterval(function(){send('ov-state')},4000);
</script>");
            sb.Append(SendScript()).Append(PageFoot);
            return sb.ToString();
        }

        private bool _ovBusy;
        private async Task SendOverviewAsync(BrowserTab tab)
        {
            if (_ovBusy) return;
            _ovBusy = true;
            try
            {
                object cookies = new { ok = false };
                try { cookies = await CookieStatsAsync(); } catch { }
                var hosts = _history.Items.Select(h => TryHost(h.Url)).Where(h => !string.IsNullOrEmpty(h)).Distinct().Count();
                var done = _downloads.Where(d => d.State == "Completed").ToList();
                var up = DateTime.UtcNow - _sessionStartUtc;
                var dlBytes = SizeText(done.Sum(d => d.Total)); if (dlBytes.Length == 0) dlBytes = "0 B";
                var payload = new
                {
                    type = "ov",
                    prot = new
                    {
                        blockedSession = _blockedSession, blockedLifetime = _statsBlocked, blockedPage = Active?.Blocked ?? 0,
                        stripped = _paramsStripped, fp = _fpTotals.Values.Sum(), popups = _popupsBlocked, external = _externalBlocked,
                        blockingEnabled = _blockingEnabled, rules = _filters.NetworkRules + _filters.CosmeticRules, shield = _shieldLevel, tracking = _trackingLevel
                    },
                    trackers = _blockedByHost.OrderByDescending(kv => kv.Value).Take(10).Select(kv => new { name = kv.Key, n = kv.Value }).ToList(),
                    cookies,
                    lib = new
                    {
                        bookmarks = _bookmarks.Count, history = _history.Items.Count, sites = hosts, downloads = _downloads.Count,
                        downloadBytes = dlBytes, tabs = _tabs.Count, groups = _groups.Count
                    },
                    top = TopSites.Pick(_history.Items.Select(h => (h.Url, h.Title)), 8).Select(s => new { url = s.Url, host = s.Host, title = s.Title, visits = s.Visits }).ToList(),
                    lists = _listState.OrderBy(kv => kv.Key).Select(kv => new { name = kv.Value.Name, enabled = kv.Value.Enabled, rules = kv.Value.Rules, updated = kv.Value.Updated }).ToList(),
                    net = new
                    {
                        vpn = NetActive() ? ("Proxy exit" + (string.IsNullOrEmpty(_netExitRegion) ? "" : ": " + _netExitRegion)) : (_sysVpn != null ? VpnDetect.Describe(_sysVpn) : "Direct"),
                        uptime = up.TotalHours >= 1 ? (int)up.TotalHours + " h " + up.Minutes + " min" : up.Minutes + " min",
                        engine = CurrentEngineVersion()
                    }
                };
                if (_tabs.Contains(tab) && tab.Internal == "overview") tab.Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
            }
            catch { }
            finally { _ovBusy = false; }
        }

        private string CurrentEngineVersion()
        {
            try { var t = _tabs.FirstOrDefault(x => x.Ready && x.Web?.CoreWebView2 != null); return t == null ? "" : t.Web.CoreWebView2.Environment.BrowserVersionString; } catch { return ""; }
        }

        private CoreWebView2? MainCore() =>
            _tabs.FirstOrDefault(t => !t.Private && t.Ready && t.Web?.CoreWebView2 != null)?.Web.CoreWebView2;

        private async Task<object> CookieStatsAsync()
        {
            var core = MainCore(); if (core == null) return new { ok = false };
            var list = await core.CookieManager.GetCookiesAsync(null);
            var byDomain = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int session = 0, tracker = 0;
            foreach (var c in list)
            {
                var d = (c.Domain ?? "").TrimStart('.').ToLowerInvariant(); if (d.Length == 0) continue;
                byDomain[d] = byDomain.TryGetValue(d, out var n) ? n + 1 : 1;
                if (c.IsSession) session++;
                try { if (_filters.Match("https://" + d + "/", null, ResType.Script).Blocked) tracker++; } catch { }
            }
            // private tabs: counts only (no site names), so the Overview does not list what you browsed privately
            object priv = new { ok = false };
            try
            {
                var pcore = _tabs.FirstOrDefault(t => t.Private && t.Ready && t.Web?.CoreWebView2 != null)?.Web.CoreWebView2;
                if (pcore != null)
                {
                    var plist = await pcore.CookieManager.GetCookiesAsync(null);
                    priv = new { ok = true, total = plist.Count, sites = plist.Select(c => (c.Domain ?? "").TrimStart('.').ToLowerInvariant()).Distinct().Count() };
                }
            }
            catch { }
            return new
            {
                ok = true, priv, total = list.Count, sites = byDomain.Count, session, persistent = list.Count - session, tracker,
                top = byDomain.OrderByDescending(kv => kv.Value).Take(12).Select(kv => new { domain = kv.Key, count = kv.Value }).ToList()
            };
        }

        private async void HandleOverviewMessage(BrowserTab tab, string msg)
        {
            try
            {
                if (msg == "ov-state") { await SendOverviewAsync(tab); return; }
                void Note(string t) { try { tab.Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "note", text = t })); } catch { } }
                var core = MainCore();
                if (msg == "ov-cookies-all")
                {
                    if (core == null) return;
                    if (MessageBox.Show(this, "Delete all cookies? You will be signed out of sites.", "Delete cookies", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    core.CookieManager.DeleteAllCookies();
                    try { _tabs.FirstOrDefault(t => t.Private && t.Ready && t.Web?.CoreWebView2 != null)?.Web.CoreWebView2.CookieManager.DeleteAllCookies(); } catch { }
                    _actions?.Append("cookies.clear", "all"); Note("All cookies deleted."); await SendOverviewAsync(tab); return;
                }
                if (msg.StartsWith("ov-cookie:", StringComparison.Ordinal))
                {
                    var dom = Dec(msg.Substring("ov-cookie:".Length)).ToLowerInvariant();
                    if (core == null || !HostOk(dom)) return;
                    var list = await core.CookieManager.GetCookiesAsync(null);
                    int n = 0;
                    foreach (var c in list) if (string.Equals((c.Domain ?? "").TrimStart('.'), dom, StringComparison.OrdinalIgnoreCase)) { core.CookieManager.DeleteCookie(c); n++; }
                    try
                    {
                        var pc = _tabs.FirstOrDefault(t => t.Private && t.Ready && t.Web?.CoreWebView2 != null)?.Web.CoreWebView2;
                        if (pc != null) foreach (var c in await pc.CookieManager.GetCookiesAsync(null)) if (string.Equals((c.Domain ?? "").TrimStart('.'), dom, StringComparison.OrdinalIgnoreCase)) { pc.CookieManager.DeleteCookie(c); n++; }
                    }
                    catch { }
                    _actions?.Append("cookies.clear", dom + ":" + n); Note("Deleted " + n + " cookie(s) for " + dom + "."); await SendOverviewAsync(tab); return;
                }
                if (msg.StartsWith("ov-clear:", StringComparison.Ordinal))
                {
                    var p = msg.Substring("ov-clear:".Length).Split(':');
                    if (p.Length != 2 || RangeStartUtc(p[0]) == null) return;
                    var range = p[0]; var flags = p[1];
                    if (MessageBox.Show(this, "Clear the selected data from " + RangeWords(range) + "?", "Clear browsing data", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    var did = new List<string>();
                    var start = RangeStartUtc(range)!.Value;
                    if (flags.Contains('h')) { var n = ClearHistoryRange(range); did.Add("history (" + n + ")"); }
                    if (flags.Contains('d')) { _downloads.RemoveAll(d => d.State != "InProgress" && (start == DateTime.MinValue || (LocalOf(d.Ts)?.ToUniversalTime() ?? DateTime.MinValue) >= start)); SaveDownloads(); did.Add("download list"); }
                    if ((flags.Contains('c') || flags.Contains('k')) && core != null)
                    {
                        var kinds = (CoreWebView2BrowsingDataKinds)0;
                        if (flags.Contains('c')) kinds |= CoreWebView2BrowsingDataKinds.AllSite;
                        if (flags.Contains('k')) kinds |= CoreWebView2BrowsingDataKinds.DiskCache;
                        var from = start == DateTime.MinValue ? new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc) : start;
                        await core.Profile.ClearBrowsingDataAsync(kinds, from, DateTime.UtcNow.AddMinutes(1));
                        if (flags.Contains('c')) did.Add("cookies and site data");
                        if (flags.Contains('k')) did.Add("cached files");
                    }
                    _actions?.Append("browsing_data.clear", range + ":" + flags);
                    Note("Cleared " + string.Join(", ", did) + " from " + RangeWords(range) + ".");
                    await SendOverviewAsync(tab);
                }
            }
            catch (Exception ex) { Status("overview action failed: " + ex.Message); }
        }

        public static readonly System.Windows.Input.RoutedUICommand PrivateTab = new("New private tab", "PrivateTab", typeof(MainWindow));
        private void MenuOverview_Click(object sender, RoutedEventArgs e) => OpenInternalInActiveTab("overview");
        private void MenuPrivateBtn_Click(object sender, RoutedEventArgs e) { _ = OpenNewPrivateTabAsync(); }
        private void PrivateTabCmd_Executed(object sender, System.Windows.Input.ExecutedRoutedEventArgs e) { _ = OpenNewPrivateTabAsync(); }
    }
}
