using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace Recognition.Browser
{
    // Device sync with no servers and no accounts. You choose a folder that your own file-sync already carries between your devices
    // (OneDrive, Syncthing, a USB stick). Recognition keeps one encrypted file there (bookmarks, start page tiles, home page, search engine),
    // sealed with a 20-character code that is shown once. Recognition itself never talks to any network for this.
    // Sync only adds: it never deletes a bookmark or tile from either side. The format and the merge rules are in Workspace.cs (tested).
    public partial class MainWindow
    {
        private bool _syncOn;
        private string _syncFolder = "", _syncCode = "", _syncNote = "";
        private bool _syncBusy;

        private string SyncStatePath() => Path.Combine(_repoRoot, "runtime", "sync.v1.enc");

        // the folder and code live in one DPAPI-encrypted file, not in browser_settings.json
        private void LoadSyncSettings(JsonElement _)
        {
            try
            {
                var p = SyncStatePath(); if (!File.Exists(p)) return;
                using var d = JsonDocument.Parse(ReadSecure(p));
                var r = d.RootElement;
                var folder = r.TryGetProperty("folder", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() ?? "" : "";
                var code = SetupCode.Normalize(r.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : "");
                if (SoteriaBridge.IsSafeRoot(folder) && code.Length > 0) { _syncFolder = folder; _syncCode = code; _syncOn = true; }
            }
            catch { }
        }

        private string SyncSettingsJson() => "";

        private void SaveSyncState()
        {
            try
            {
                if (!_syncOn) { if (File.Exists(SyncStatePath())) File.Delete(SyncStatePath()); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(SyncStatePath())!);
                WriteSecure(SyncStatePath(), JsonSerializer.Serialize(new { folder = _syncFolder, code = _syncCode }));
            }
            catch { }
        }

        private string SyncSettingsHtml()
        {
            var sb = new StringBuilder();
            sb.Append("<h1 style='font-size:16px'>Device sync</h1>");
            sb.Append("<div class='row'><div><div class='t'>Bookmarks, start page tiles, home page and search engine</div><div class='u'>" +
                      "No servers and no account. You pick a folder that your own file sync already carries between your devices (OneDrive, Syncthing, a USB stick). " +
                      "Recognition keeps one encrypted file there, locked with a code you see once. Sync only adds things: it never deletes a bookmark or tile on either side. " +
                      (_syncOn ? "<br>On, using: <code>" + Esc(_syncFolder) + "</code>" : "<br>Off.") +
                      (_syncNote.Length > 0 ? "<br>" + Esc(_syncNote) : "") + "</div></div><div class='ts'>");
            if (_syncOn)
                sb.Append("<a class='btn' onclick=\"send('sync-now')\">Sync now</a> <a class='btn ghost' onclick=\"send('sync-code')\">Show code</a> <a class='btn ghost' onclick=\"send('sync-off')\">Turn off</a>");
            else
                sb.Append("<a class='btn' onclick=\"send('sync-on')\">Turn on (first device)...</a> <a class='btn ghost' onclick=\"send('sync-join')\">Join (another device)...</a>");
            sb.Append("</div></div>");
            return sb.ToString();
        }

        private static string? PickFolder(string title)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title };
            return dlg.ShowDialog() == true ? dlg.FolderName : null;
        }

        private void HandleSyncMessage(BrowserTab from, string msg)
        {
            try
            {
                switch (msg)
                {
                    case "sync-on": _ = SyncTurnOnAsync(from, false); return;
                    case "sync-join": _ = SyncTurnOnAsync(from, true); return;
                    case "sync-now": _ = SyncNowAsync(false).ContinueWith(_ => Dispatcher.Invoke(() => RefreshSettingsTab(from))); return;
                    case "sync-code":
                        MessageBox.Show(this, "Your sync code:\n\n" + _syncCode + "\n\nEnter it on another device with Join. Keep it private: anyone with the code and the file can read the synced data.", "Device sync code");
                        return;
                    case "sync-off":
                        _syncOn = false; _syncCode = ""; _syncFolder = ""; _syncNote = "Sync is off. The encrypted file in your folder was left alone."; SaveSyncState();
                        _actions?.Append("sync.disabled");
                        RefreshSettingsTab(from); return;
                }
            }
            catch (Exception ex) { Status("sync failed: " + ex.Message); }
        }

        private void RefreshSettingsTab(BrowserTab t) { if (t.Internal == "settings" && _tabs.Contains(t)) LoadInternal(t, "settings"); }

        private async Task SyncTurnOnAsync(BrowserTab from, bool join)
        {
            var folder = PickFolder(join ? "Choose the folder your other device syncs to" : "Choose a folder your file sync carries to your other devices");
            if (folder == null) return;
            if (!SoteriaBridge.IsSafeRoot(folder) || !Directory.Exists(folder)) { _syncNote = "That folder cannot be used. Choose a folder on a local or synced drive."; RefreshSettingsTab(from); return; }
            var file = Path.Combine(folder, SyncRules.FileName);
            string code;
            if (join)
            {
                if (!File.Exists(file)) { _syncNote = "No sync file was found in that folder yet. Turn sync on from your first device, wait for your file sync, then try again."; RefreshSettingsTab(from); return; }
                var typed = PromptText("Join device sync", "Enter the 20-character sync code from your first device", "");
                if (typed == null) return;
                code = SetupCode.Normalize(typed);
                if (code.Length == 0) { _syncNote = "That code is not in the right format (20 letters and digits)."; RefreshSettingsTab(from); return; }
                var blob = await Task.Run(() => { try { return File.ReadAllText(file); } catch { return ""; } });
                var open = await Task.Run(() => SetupBlob.Open(blob, code, DateTime.UtcNow));
                if (!open.Ok) { _syncNote = "Could not join: " + open.Reason; RefreshSettingsTab(from); return; }
            }
            else
            {
                if (File.Exists(file)) { _syncNote = "That folder already has a sync file. Use Join on this device, or choose another folder."; RefreshSettingsTab(from); return; }
                code = SetupCode.Generate();
            }
            _syncFolder = folder; _syncCode = code; _syncOn = true; SaveSyncState();
            _actions?.Append(join ? "sync.joined" : "sync.enabled");
            await SyncNowAsync(false);
            if (!join) MessageBox.Show(this, "Device sync is on.\n\nYour sync code (shown now; Show code displays it again on this device):\n\n" + code + "\n\nTo add another device, install Recognition there, make sure the folder is synced to it, then choose Join and enter this code.", "Device sync");
            RefreshSettingsTab(from);
        }

        private async Task SyncNowAsync(bool quiet)
        {
            if (!_syncOn || _syncBusy) return;
            _syncBusy = true;
            try
            {
                var file = Path.Combine(_syncFolder, SyncRules.FileName);
                if (!Directory.Exists(_syncFolder)) { _syncNote = "The sync folder is not available right now."; if (!quiet) Status(_syncNote); return; }
                var code = _syncCode;
                string blob = ""; bool exists = File.Exists(file);
                if (exists) blob = await Task.Run(() => { try { return File.ReadAllText(file); } catch { return ""; } });
                int addedBm = 0, addedTiles = 0; bool prefsTaken = false;
                if (exists)
                {
                    var open = await Task.Run(() => SetupBlob.Open(blob, code, DateTime.UtcNow));
                    if (!open.Ok) { _syncNote = "Not synced: " + open.Reason + " (nothing was overwritten)."; if (!quiet) Status(_syncNote); return; }
                    var remote = SyncRules.Parse(open.PayloadJson);
                    if (remote == null) { _syncNote = "Not synced: the file in the folder is not a sync file (nothing was overwritten)."; if (!quiet) Status(_syncNote); return; }

                    var mergedBm = SyncRules.MergeByUrl(_bookmarks.Select(b => (b.Url, b.Title)), remote.Bookmarks, out addedBm);
                    if (addedBm > 0)
                    {
                        var have = new HashSet<string>(_bookmarks.Select(b => b.Url), StringComparer.Ordinal);
                        foreach (var m in mergedBm) if (have.Add(m.Url)) _bookmarks.Add(new Bookmark { Url = m.Url, Title = m.Title, Ts = Iso(DateTime.UtcNow) });
                        SaveBookmarks();
                    }
                    var mergedTiles = SyncRules.MergeByUrl(_tilesCustom, remote.Tiles, out addedTiles);
                    if (addedTiles > 0) { _tilesCustom.Clear(); _tilesCustom.AddRange(mergedTiles.Take(TileRules.MaxCustom)); }
                    foreach (var h in remote.Hidden) if (_tilesHidden.Count < TileRules.MaxHidden) _tilesHidden.Add(h);
                    if (SyncRules.RemoteIsNewer(_prefsChangedUtc, remote.PrefsUtc))
                    {
                        if (remote.Home != null) _homeUrl = remote.Home;
                        if (remote.Engine != null) _searchEngine = remote.Engine;
                        _prefsChangedUtc = remote.PrefsUtc; prefsTaken = true;
                    }
                    SaveSettings();
                    if (addedBm > 0) RebuildBookmarksBar();
                    ReloadStartTabs();
                }
                var cats = SyncRules.Categories(_bookmarks.Select(b => (b.Url, b.Title)), _tilesCustom, _tilesHidden, _homeUrl, _searchEngine, _prefsChangedUtc, Environment.MachineName);
                var now = DateTime.UtcNow;
                var payload = SetupPayload.Build("sync-" + Guid.NewGuid().ToString("N"), now, now.AddYears(50), cats);
                var sealedBlob = await Task.Run(() => SetupBlob.Seal(payload, code));
                var tmp = file + ".tmp";
                await Task.Run(() => { File.WriteAllText(tmp, sealedBlob, new UTF8Encoding(false)); File.Move(tmp, file, true); });
                _syncNote = "Synced " + DateTime.Now.ToString("g") + (exists ? ": " + addedBm + " bookmark(s) and " + addedTiles + " tile(s) added from the other device" + (prefsTaken ? ", home page and search engine updated" : "") + "." : ": first copy saved.");
                _actions?.Append("sync.run", "bookmarks+" + addedBm + " tiles+" + addedTiles);
                if (!quiet) Status(_syncNote);
            }
            catch (Exception ex) { _syncNote = "Not synced: " + ex.GetType().Name; if (!quiet) Status(_syncNote); }
            finally { _syncBusy = false; }
        }
    }
}
