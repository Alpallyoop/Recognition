using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Recognition.Browser
{
    // recognition:extensions, Developer tab: a place to try extension builds while you write them (docs\proposals\0003).
    //
    //   Add a build folder or upload a .zip/.crx/.xpi build -> every build is checked (ExtDevCheck), hashed (the same identity the
    //   governance uses), compared with the previous build (ExtDevDiff) and receipted -> Run loads an exact snapshot of those bytes into
    //   a separate developer profile in its own tab (deleted when Recognition closes) -> Reload, Watch for changes, DevTools ->
    //   Package makes a deterministic .zip -> Send to review hands the build to the normal review, policy and ledger to install it.
    //
    // What this never does: load a developer build into your everyday profile, write to the governance ledger, or run a build the
    // policy denies. A build runs from a content-addressed snapshot (runtime\tmp\extdev-run\<identity>), so the bytes that were checked
    // are the bytes the engine loads, even if the folder changes a moment later.
    public partial class MainWindow
    {
        private ExtDevStateFile? _extDevState;
        private CoreWebView2Environment? _extDevEnv;
        private string? _extDevEnvDir;
        private BrowserTab? _extDevTab;
        private string? _extDevRunning;                                          // project id loaded in the developer tab
        private string? _extDevEngineId;                                         // its engine id
        private readonly Dictionary<string, ExtDevReport> _extDevReports = new();
        private readonly Dictionary<string, ExtDevChange> _extDevChanges = new();
        private readonly Dictionary<string, FileSystemWatcher> _extDevWatchers = new();
        private readonly Dictionary<string, DispatcherTimer> _extDevDebounce = new();
        private string _extDevPackNote = "";

        private string ExtDevStatePath => Path.Combine(_repoRoot, "runtime", "extensions_dev.v1.json");
        private string ExtDevUploadRoot => Path.Combine(_repoRoot, "runtime", "extensions_dev");
        private string ExtDevRunRoot => Path.Combine(TmpRoot, "extdev-run");

        private ExtDevStateFile ExtDev
        {
            get
            {
                if (_extDevState != null) return _extDevState;
                string? json = null;
                try { if (File.Exists(ExtDevStatePath)) json = File.ReadAllText(ExtDevStatePath); } catch { }
                _extDevState = ExtDevState.Parse(json, ExtDevUploadRoot);
                foreach (var p in _extDevState.Projects.Where(p => p.Watch && !p.Uploaded)) StartWatch(p);
                return _extDevState;
            }
        }

        private void SaveExtDev()
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(ExtDevStatePath)!); File.WriteAllText(ExtDevStatePath, ExtDevState.ToJson(ExtDev), new UTF8Encoding(false)); } catch { }
        }

        private string? PolicyJsonOrNull() { try { return File.ReadAllText(ExtPolicyPath); } catch { return null; } }

        // ---- build: check, hash, snapshot, compare, receipt ------------------------------------------------------------------------

        private sealed record DevBuildResult(ExtDevReport Report, string? Gid, string? Snapshot);

        private static string SnapshotDirFor(string runRoot, string gid) => Path.Combine(runRoot, gid);

        // Runs off the UI thread. Never throws: problems become findings.
        private DevBuildResult BuildDevProject(ExtDevProject p, string? policyJson)
        {
            string root;
            try { root = ExtPackage.ResolveRoot(p.Folder); }
            catch (Exception ex)
            {
                var r0 = new ExtDevReport(); r0.Add(ExtDevLevel.Error, Directory.Exists(p.Folder) ? "no_manifest" : "no_folder", Directory.Exists(p.Folder) ? ex.Message : "the build folder is gone: " + p.Folder);
                return new DevBuildResult(r0, null, null);
            }
            var rep = ExtDevCheck.Run(root, policyJson);
            ExtIdentity id;
            try { id = ExtGovernance.ComputeIdentity(root); }
            catch (ExtGovException ex) { rep.Add(ExtDevLevel.Error, "identity", "the build cannot be identified: " + ex.Message); return new DevBuildResult(rep, null, null); }

            // snapshot the exact bytes (content-addressed); re-hash the copy so a folder that changed mid-copy is caught
            var snap = SnapshotDirFor(ExtDevRunRoot, id.Id);
            if (!Directory.Exists(snap))
            {
                var tmp = Path.Combine(ExtDevRunRoot, "_copy-" + Guid.NewGuid().ToString("N"));
                try
                {
                    ExtPackage.CopyDirectory(root, tmp);
                    var gitDir = Path.Combine(tmp, ".git"); if (Directory.Exists(gitDir)) Directory.Delete(gitDir, true);
                    if (ExtGovernance.ComputeIdentity(tmp).Id != id.Id) { rep.Add(ExtDevLevel.Error, "changed", "files changed while the build was being copied; save your work and build again"); Directory.Delete(tmp, true); return new DevBuildResult(rep, id.Id, null); }
                    Directory.Move(tmp, snap);
                }
                catch (Exception ex)
                {
                    try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
                    if (!Directory.Exists(snap)) { rep.Add(ExtDevLevel.Error, "snapshot", "the build could not be copied for running: " + ex.Message); return new DevBuildResult(rep, id.Id, null); }
                }
            }

            // compare with the previous build when its snapshot is still here (same session)
            var prev = p.Builds.LastOrDefault(b => b.Gid != id.Id);
            if (prev != null)
            {
                var prevSnap = SnapshotDirFor(ExtDevRunRoot, prev.Gid);
                try
                {
                    if (Directory.Exists(prevSnap))
                        lock (_extDevChanges) _extDevChanges[p.Id] = ExtDevDiff.Compare(ExtGovernance.ComputeIdentity(prevSnap).Files, ExtGovernance.ReadManifest(prevSnap), id.Files, ExtGovernance.ReadManifest(snap));
                }
                catch { }
            }
            return new DevBuildResult(rep, id.Id, snap);
        }

        private async Task<DevBuildResult> BuildAndRecordAsync(ExtDevProject p)
        {
            var pol = PolicyJsonOrNull();
            var res = await Task.Run(() => BuildDevProject(p, pol));
            _extDevReports[p.Id] = res.Report;
            if (res.Gid != null)
            {
                bool isNew = p.Builds.Count == 0 || p.Builds[^1].Gid != res.Gid;
                ExtDevState.AddBuild(p, new ExtDevBuild { Gid = res.Gid, Version = CleanLabel(res.Report.Version), Utc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), Errors = res.Report.Errors, Warnings = res.Report.Warnings });
                if (!isNew) lock (_extDevChanges) _extDevChanges.Remove(p.Id);
                SaveExtDev();
                _actions?.Append("extension.dev_build", res.Gid + ":" + (res.Report.Runnable ? "ok" : "errors"));
            }
            return res;
        }

        // ---- run in the developer profile ------------------------------------------------------------------------------------------------

        private async Task<CoreWebView2Environment> EnsureExtDevEnvAsync()
        {
            if (_extDevEnv != null) return _extDevEnv;
            _extDevEnvDir = Path.Combine(TmpRoot, "rb-extdev-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_extDevEnvDir);
            var o = await PrivateEnvOptsAsync();
            o.AreBrowserExtensionsEnabled = true;
            _extDevEnv = await CoreWebView2Environment.CreateAsync(null, _extDevEnvDir, o);
            return _extDevEnv;
        }

        private static string? ExtOwnPage(string root)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")).TrimStart('﻿'), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                var r = doc.RootElement;
                string? Pick(string a, string b) => r.TryGetProperty(a, out var x) && x.ValueKind == JsonValueKind.Object && x.TryGetProperty(b, out var y) && y.ValueKind == JsonValueKind.String ? y.GetString() : null;
                var page = Pick("options_ui", "page") ?? (r.TryGetProperty("options_page", out var op) && op.ValueKind == JsonValueKind.String ? op.GetString() : null)
                           ?? Pick("action", "default_popup") ?? Pick("browser_action", "default_popup");
                if (page == null || page.Contains("..") || page.Contains(':') || page.Contains('\\')) return null;
                return page.TrimStart('/');
            }
            catch { return null; }
        }

        private async Task ExtDevRunAsync(ExtDevProject p, bool reload)
        {
            var res = await BuildAndRecordAsync(p);
            if (!res.Report.Runnable || res.Snapshot == null || res.Gid == null)
            {
                ExtReport("not run: the build has " + res.Report.Errors + " problem(s). Fix them (listed under the build) and build again.", false);
                return;
            }
            try
            {
                var env = await EnsureExtDevEnvAsync();
                bool fresh = _extDevTab == null || !_tabs.Contains(_extDevTab) || _extDevTab.Web?.CoreWebView2 == null;
                if (fresh)
                {
                    _extDevTab = await NewTabCoreAsync("Extension developer", true, env);
                    if (_extDevTab == null) { ExtReport("could not open the developer tab", false); return; }
                }
                var tab = _extDevTab!;
                var core = tab.Web?.CoreWebView2;
                if (core == null) { ExtReport("the developer tab is not ready yet; press Run again", false); return; }
                // one developer build at a time: unload whatever ran before, then load the snapshot
                foreach (var old in await core.Profile.GetBrowserExtensionsAsync()) { try { await old.RemoveAsync(); } catch { } }
                var ext = await core.Profile.AddBrowserExtensionAsync(res.Snapshot);
                bool sameProject = _extDevRunning == p.Id;
                _extDevRunning = p.Id; _extDevEngineId = ext.Id;
                _actions?.Append("extension.dev_run", res.Gid);
                var page = ExtOwnPage(res.Snapshot);
                if (reload && sameProject && !fresh && tab.Internal.Length == 0) core.Reload();
                else if (page != null) { tab.Internal = ""; core.Navigate("chrome-extension://" + ext.Id + "/" + page); }
                else LoadInternal(tab, "start");
                ExtReport((reload ? "reloaded " : "running ") + CleanLabel(res.Report.Name) + " " + CleanLabel(res.Report.Version) + " in the developer tab (engine id " + ext.Id + ")." +
                          (page == null ? " It has no page of its own: browse to a site where it should act." : "") + " Use DevTools to see its console. Nothing from this profile is kept.");
            }
            catch (Exception ex) { ExtReport("the engine could not load this build: " + ex.Message, false); }
        }

        private async Task ExtDevUnloadAsync()
        {
            try
            {
                if (_extDevTab != null && _tabs.Contains(_extDevTab) && _extDevTab.Web?.CoreWebView2 != null)
                    foreach (var old in await _extDevTab.Web.CoreWebView2.Profile.GetBrowserExtensionsAsync()) { try { await old.RemoveAsync(); } catch { } }
            }
            catch { }
            _extDevRunning = null; _extDevEngineId = null;
        }

        // ---- watching a folder -----------------------------------------------------------------------------------------------------------

        private void StartWatch(ExtDevProject p)
        {
            StopWatch(p.Id);
            if (p.Uploaded || !Directory.Exists(p.Folder)) return;
            try
            {
                var w = new FileSystemWatcher(p.Folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
                var pid = p.Id;
                void Fire(object? s, FileSystemEventArgs e)
                {
                    var rel = e.FullPath.Length > p.Folder.Length ? e.FullPath.Substring(p.Folder.Length).Replace('\\', '/') : "";
                    if (rel.Contains("/.git/") || rel.EndsWith("/.git")) return;
                    Dispatcher.BeginInvoke(() => Debounce(pid));
                }
                w.Changed += Fire; w.Created += Fire; w.Deleted += Fire; w.Renamed += (s, e) => Fire(s, e);
                w.EnableRaisingEvents = true;
                _extDevWatchers[pid] = w;
            }
            catch (Exception ex) { Status("cannot watch " + p.Folder + ": " + ex.Message); }
        }

        private void StopWatch(string pid)
        {
            if (_extDevWatchers.Remove(pid, out var w)) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
            if (_extDevDebounce.Remove(pid, out var t)) t.Stop();
        }

        private void Debounce(string pid)
        {
            if (!_extDevDebounce.TryGetValue(pid, out var t))
            {
                t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                t.Tick += async (_, __) =>
                {
                    t.Stop();
                    var p = ExtDev.Projects.FirstOrDefault(x => x.Id == pid); if (p == null) return;
                    if (_extDevRunning == pid) await ExtDevRunAsync(p, true);
                    else { await BuildAndRecordAsync(p); ExtReport("rebuilt " + p.Label + " after a change on disk."); }
                    SendExtSnapshot();
                };
                _extDevDebounce[pid] = t;
            }
            t.Stop(); t.Start();
        }

        // ---- uploads ---------------------------------------------------------------------------------------------------------------------

        // Unpacks an uploaded build into runtime\extensions_dev\<identity> (content-addressed). Runs off the UI thread.
        private string StoreUpload(byte[] package)
        {
            Directory.CreateDirectory(ExtDevUploadRoot);
            var staging = Path.Combine(ExtDevUploadRoot, "_staging-" + Guid.NewGuid().ToString("N"));
            try
            {
                var root = ExtPackage.Unpack(package, staging);
                var id = ExtGovernance.ComputeIdentity(root);
                var dst = Path.Combine(ExtDevUploadRoot, id.Id);
                if (!Directory.Exists(dst)) { if (root == staging) Directory.Move(staging, dst); else Directory.Move(root, dst); }
                return dst;
            }
            finally { try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { } }
        }

        private bool IsUnderUploads(string path)
        {
            var up = Path.GetFullPath(ExtDevUploadRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(up, StringComparison.OrdinalIgnoreCase);
        }

        // ---- messages --------------------------------------------------------------------------------------------------------------------

        private async Task HandleExtDevMessage(string msg)
        {
            ExtDevProject? P(string prefix) => ExtDev.Projects.FirstOrDefault(x => x.Id == msg.Substring(prefix.Length));

            if (msg == "ext-dev-add-folder")
            {
                if (ExtDev.Projects.Count >= ExtDevState.MaxProjects) { ExtReport("the developer list is full (" + ExtDevState.MaxProjects + "); remove one first", false); return; }
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the folder your extension is built into (it contains manifest.json)" };
                if (dlg.ShowDialog(this) != true) return;
                var folder = Path.GetFullPath(dlg.FolderName);
                var existing = ExtDev.Projects.FirstOrDefault(x => !x.Uploaded && string.Equals(Path.GetFullPath(x.Folder), folder, StringComparison.OrdinalIgnoreCase));
                var p = existing ?? new ExtDevProject { Id = ExtDevState.NewId(folder), Label = CleanLabel(Path.GetFileName(folder.TrimEnd('\\', '/'))), Folder = folder };
                if (existing == null) ExtDev.Projects.Add(p);
                var res = await BuildAndRecordAsync(p);
                if (res.Report.Name.Length > 0 && !res.Report.Name.StartsWith("__MSG_")) p.Label = CleanLabel(res.Report.Name);
                SaveExtDev(); _actions?.Append("extension.dev_add", p.Id);
                ExtReport("added " + p.Label + ": " + (res.Report.Runnable ? "ready to run." : res.Report.Errors + " problem(s) to fix first."), res.Report.Runnable);
                SendExtSnapshot(); return;
            }
            if (msg == "ext-dev-upload" || msg.StartsWith("ext-dev-upload:"))
            {
                var target = msg.StartsWith("ext-dev-upload:") ? P("ext-dev-upload:") : null;
                if (target == null && ExtDev.Projects.Count >= ExtDevState.MaxProjects) { ExtReport("the developer list is full (" + ExtDevState.MaxProjects + "); remove one first", false); return; }
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Upload an extension build", Filter = "Extension build (*.zip;*.crx;*.xpi)|*.zip;*.crx;*.xpi|All files|*.*" };
                if (dlg.ShowDialog(this) != true) return;
                var fi = new FileInfo(dlg.FileName);
                if (fi.Length > ExtPackage.MaxPackageBytes) { ExtReport("that file is larger than " + ExtPackage.MaxPackageBytes / 1024 / 1024 + " MB", false); return; }
                var bytes = await File.ReadAllBytesAsync(dlg.FileName);
                var dir = await Task.Run(() => StoreUpload(bytes));
                var p = target;
                if (p == null) { p = new ExtDevProject { Id = ExtDevState.NewId(dir), Label = CleanLabel(fi.Name), Folder = dir, Uploaded = true }; ExtDev.Projects.Add(p); }
                else
                {
                    var old = p.Folder; p.Folder = dir;
                    if (!string.Equals(old, dir, StringComparison.OrdinalIgnoreCase) && IsUnderUploads(old) && !ExtDev.Projects.Any(x => x != p && string.Equals(x.Folder, old, StringComparison.OrdinalIgnoreCase)))
                        try { Directory.Delete(old, true); } catch { }
                }
                var res = await BuildAndRecordAsync(p);
                if (target == null && res.Report.Name.Length > 0 && !res.Report.Name.StartsWith("__MSG_")) p.Label = CleanLabel(res.Report.Name);
                SaveExtDev();
                _actions?.Append("extension.dev_upload", (res.Gid ?? "none") + ":" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
                ExtReport("uploaded " + fi.Name + " (" + (bytes.Length / 1024) + " KB, sha256 " + Convert.ToHexString(SHA256.HashData(bytes)).Substring(0, 12).ToLowerInvariant() + "...): " +
                          (res.Report.Runnable ? "ready to run." : res.Report.Errors + " problem(s) to fix first."), res.Report.Runnable);
                if (target != null && _extDevRunning == target.Id && res.Report.Runnable) await ExtDevRunAsync(target, true);
                SendExtSnapshot(); return;
            }
            if (msg.StartsWith("ext-dev-build:")) { var p = P("ext-dev-build:"); if (p == null) return; var r = await BuildAndRecordAsync(p); ExtReport("checked " + p.Label + ": " + r.Report.Errors + " problem(s), " + r.Report.Warnings + " warning(s).", r.Report.Runnable); SendExtSnapshot(); return; }
            if (msg.StartsWith("ext-dev-run:")) { var p = P("ext-dev-run:"); if (p == null) return; await ExtDevRunAsync(p, _extDevRunning == p.Id); SendExtSnapshot(); return; }
            if (msg == "ext-dev-stop") { await ExtDevUnloadAsync(); ExtReport("the developer build was unloaded."); SendExtSnapshot(); return; }
            if (msg == "ext-dev-devtools")
            {
                if (_extDevTab != null && _tabs.Contains(_extDevTab) && _extDevTab.Web?.CoreWebView2 != null) { Tabs.SelectedItem = _extDevTab.Item; ShowActiveWebView(); _extDevTab.Web.CoreWebView2.OpenDevToolsWindow(); }
                else ExtReport("run a build first; DevTools opens for the developer tab", false);
                return;
            }
            if (msg == "ext-dev-show")
            {
                if (_extDevTab != null && _tabs.Contains(_extDevTab)) { Tabs.SelectedItem = _extDevTab.Item; ShowActiveWebView(); }
                else ExtReport("the developer tab is not open; press Run", false);
                return;
            }
            if (msg.StartsWith("ext-dev-watch:"))
            {
                var p = P("ext-dev-watch:"); if (p == null) return;
                if (p.Uploaded) { ExtReport("an uploaded build cannot be watched; upload a new build instead", false); return; }
                p.Watch = !p.Watch; SaveExtDev();
                if (p.Watch) StartWatch(p); else StopWatch(p.Id);
                ExtReport(p.Watch ? "watching " + p.Label + ": saving a file rebuilds it (and reloads it when it is running)." : "stopped watching " + p.Label + ".");
                SendExtSnapshot(); return;
            }
            if (msg.StartsWith("ext-dev-pack:"))
            {
                var p = P("ext-dev-pack:"); if (p == null) return;
                var res = await BuildAndRecordAsync(p);
                if (!res.Report.Runnable || res.Snapshot == null) { ExtReport("not packaged: fix the build's problems first", false); SendExtSnapshot(); return; }
                var zip = await Task.Run(() => ExtDevPack.Zip(res.Snapshot));
                var safeName = new string((res.Report.Name + "-" + res.Report.Version).Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' or '_' ? c : '-').ToArray()).Trim('-');
                var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Save the package", FileName = (safeName.Length > 0 ? safeName : "extension") + ".zip", Filter = "ZIP package (*.zip)|*.zip" };
                if (dlg.ShowDialog(this) != true) return;
                await File.WriteAllBytesAsync(dlg.FileName, zip);
                var sha = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant();
                _extDevPackNote = p.Id + "|" + sha;
                _actions?.Append("extension.dev_pack", res.Gid + ":" + sha);
                ExtReport("packaged " + (zip.Length / 1024) + " KB, sha256 " + sha + ". The same files always give the same package.");
                SendExtSnapshot(); return;
            }
            if (msg.StartsWith("ext-dev-review:"))
            {
                var p = P("ext-dev-review:"); if (p == null) return;
                var res = await BuildAndRecordAsync(p);
                if (!res.Report.Runnable || res.Snapshot == null) { ExtReport("fix the build's problems before sending it to review", false); SendExtSnapshot(); return; }
                DiscardPending();
                _extPending = await Task.Run(() => StageFolder(res.Snapshot, p.Label + " (developer build)"));
                ExtReport("sent to review: read the card at the top, then install it like any other extension (policy and ledger apply).");
                SendExtSnapshot(); return;
            }
            if (msg.StartsWith("ext-dev-remove:"))
            {
                var p = P("ext-dev-remove:"); if (p == null) return;
                StopWatch(p.Id);
                if (_extDevRunning == p.Id) await ExtDevUnloadAsync();
                if (p.Uploaded && IsUnderUploads(p.Folder)) { try { Directory.Delete(p.Folder, true); } catch { } }
                ExtDev.Projects.Remove(p); _extDevReports.Remove(p.Id); lock (_extDevChanges) _extDevChanges.Remove(p.Id); SaveExtDev();
                _actions?.Append("extension.dev_remove", p.Id);
                ExtReport("removed " + p.Label + " from the developer list" + (p.Uploaded ? " (its uploaded copy was deleted)." : " (your folder was not touched)."));
                SendExtSnapshot(); return;
            }
        }

        // ---- what the page shows -----------------------------------------------------------------------------------------------------------

        private object ExtDevView()
        {
            var pol = PolicyJsonOrNull();
            return new
            {
                running = _extDevRunning, engineId = _extDevEngineId,
                tabOpen = _extDevTab != null && _tabs.Contains(_extDevTab),
                projects = ExtDev.Projects.Select(p =>
                {
                    if (!_extDevReports.TryGetValue(p.Id, out var rep))
                    {
                        string root; try { root = ExtPackage.ResolveRoot(p.Folder); } catch { root = p.Folder; }
                        rep = ExtDevCheck.Run(root, pol); _extDevReports[p.Id] = rep;
                    }
                    ExtDevChange? ch; lock (_extDevChanges) _extDevChanges.TryGetValue(p.Id, out ch);
                    var last = p.Builds.LastOrDefault();
                    return new
                    {
                        id = p.Id, label = CleanLabel(p.Label), folder = p.Uploaded ? "uploaded build" : CleanLabel(p.Folder), uploaded = p.Uploaded, watch = p.Watch,
                        running = _extDevRunning == p.Id, builds = p.Builds.Count,
                        last = last == null ? null : new { gid = last.Gid, version = last.Version, utc = last.Utc, errors = last.Errors, warnings = last.Warnings },
                        history = p.Builds.AsEnumerable().Reverse().Take(8).Select(b => new { gid = b.Gid.Substring(0, 12), version = b.Version, utc = b.Utc, errors = b.Errors, warnings = b.Warnings }).ToList(),
                        findings = rep.Findings.OrderBy(f => f.Level).Select(f => new { level = f.Level.ToString().ToLowerInvariant(), text = new string(f.Text.Where(c => !char.IsControl(c)).Take(400).ToArray()) }).ToList(),
                        errors = rep.Errors, warnings = rep.Warnings,
                        change = ch == null ? null : new
                        {
                            from = CleanLabel(ch.OldVersion), to = CleanLabel(ch.NewVersion),
                            added = ch.FilesAdded.Take(30).Select(CleanLabel).ToList(), removed = ch.FilesRemoved.Take(30).Select(CleanLabel).ToList(), changed = ch.FilesChanged.Take(30).Select(CleanLabel).ToList(),
                            permsAdded = ch.PermsAdded.Select(CleanLabel).ToList(), permsRemoved = ch.PermsRemoved.Select(CleanLabel).ToList(),
                            hostsAdded = ch.HostsAdded.Select(CleanLabel).ToList(), hostsRemoved = ch.HostsRemoved.Select(CleanLabel).ToList(), accessGrew = ch.AccessGrew
                        },
                        pack = _extDevPackNote.StartsWith(p.Id + "|") ? _extDevPackNote.Substring(p.Id.Length + 1) : null
                    };
                }).ToList()
            };
        }

        private void CleanupExtDevOnExit()
        {
            foreach (var id in _extDevWatchers.Keys.ToList()) StopWatch(id);
            if (_extDevEnvDir != null) { try { if (Directory.Exists(_extDevEnvDir)) Directory.Delete(_extDevEnvDir, true); } catch { } }
            try { if (Directory.Exists(ExtDevRunRoot)) Directory.Delete(ExtDevRunRoot, true); } catch { }
        }

        // Developer tab of the Extensions page (inserted by ExtensionsHtml). Rendered from the snapshot's "dev" field.
        private const string ExtDevSectionHtml = @"
<h1 style='font-size:16px'>Developer</h1>
<style>.dv{font-size:12.5px;line-height:1.6;color:#aab2bf;word-break:break-word}.dv>div{margin:1px 0}</style>
<div class='muted'>Try extension builds while you write them. Add the folder your build goes into, or upload a .zip, .crx or .xpi build. Every build is checked, hashed and compared with the previous one, and runs only in a separate developer tab whose profile is deleted when Recognition closes. Nothing here touches your everyday profile or the governance ledger: to keep a build, send it to review and install it like any other extension.</div>
<div class='card'><div class='info'><h3>Add a build</h3><p>A folder is read where it is (turn on Watch to rebuild and reload on every save). An upload is stored as its own copy.</p></div>
 <div class='acts'><a class='btn' data-c='ext-dev-add-folder'>Add a build folder...</a><a class='btn ghost' data-c='ext-dev-upload'>Upload a build...</a></div></div>
<div id='devrun'></div>
<div id='devlist'></div>
<div class='card'><div class='info'><h3>How a developer build is handled</h3><p>Checks: the manifest, every file it points to, version format, locales, retired manifest_version 2 parts, and what the governance policy will decide. A build the policy denies is not run. Run loads an exact copy of the checked bytes, so later edits never slip in unchecked. Package writes a deterministic .zip (same files, same bytes, same SHA-256) ready for a store. Builds, runs, uploads and packages are recorded in the action receipts by identity hash.</p></div></div>";

        private const string ExtDevScript = @"
function renderDev(d){var R=$('devrun'),L=$('devlist');if(!R||!L||!d)return;R.textContent='';L.textContent='';
  if(d.running){var rc=card('Running in the developer tab','engine id '+(d.engineId||''),'running','good');var ra=el('div',null,'acts');
    ra.appendChild(btn('Show the tab','ext-dev-show'));ra.appendChild(btn('DevTools','ext-dev-devtools',true));ra.appendChild(btn('Unload','ext-dev-stop',true));rc.appendChild(ra);R.appendChild(rc);}
  if(!d.projects.length){L.appendChild(card('No builds yet','Add a build folder or upload a build above.'));return;}
  d.projects.forEach(function(p){
    var bad=p.errors>0;var c=card(p.label+(p.last?'  '+p.last.version:''),p.folder+'  ·  '+p.builds+' build(s)'+(p.last?'  ·  identity '+p.last.gid.slice(0,16)+'…':''),bad?p.errors+' problem(s)':(p.warnings?p.warnings+' warning(s)':'ready'),bad?'bad':(p.warnings?'warn':'good'));
    c.style.display='block';var i=c.firstChild;
    if(p.findings.length){var ul=el('div',null,'dv');ul.style.marginTop='8px';p.findings.forEach(function(f){var line=el('div',(f.level==='error'?'✖ ':f.level==='warning'?'! ':'· ')+f.text);if(f.level==='error')line.style.color='#e57373';else if(f.level==='warning')line.style.color='#e0b050';ul.appendChild(line)});i.appendChild(ul);}
    var ch=p.change;if(ch){var cd=el('div',null,'dv');cd.style.marginTop='8px';
      cd.appendChild(el('div','Since the previous build'+(ch.from!==ch.to?' ('+ch.from+' → '+ch.to+')':'')+': '+ch.added.length+' added, '+ch.changed.length+' changed, '+ch.removed.length+' removed'));
      if(ch.changed.length)cd.appendChild(el('div','changed: '+ch.changed.join('  ')));if(ch.added.length)cd.appendChild(el('div','added: '+ch.added.join('  ')));if(ch.removed.length)cd.appendChild(el('div','removed: '+ch.removed.join('  ')));
      var acc=[];ch.permsAdded.forEach(function(x){acc.push('+ '+x)});ch.hostsAdded.forEach(function(x){acc.push('+ '+x)});ch.permsRemoved.forEach(function(x){acc.push('− '+x)});ch.hostsRemoved.forEach(function(x){acc.push('− '+x)});
      if(acc.length){var a2=el('div','access: '+acc.join('  '));if(ch.accessGrew)a2.style.color='#e0b050';cd.appendChild(a2)}
      i.appendChild(cd);}
    if(p.pack)i.appendChild(el('div','last package sha256 '+p.pack,'dv'));
    if(p.history.length>1){var h=el('div','History: '+p.history.map(function(b){return b.version+' '+b.gid+(b.errors?' ✖'+b.errors:'')}).join('   '),'dv');h.style.marginTop='6px';h.style.wordBreak='break-all';i.appendChild(h)}
    var a=el('div',null,'acts');a.style.marginTop='12px';
    a.appendChild(btn(p.running?'Reload':'Run','ext-dev-run:'+p.id));a.appendChild(btn('Check again','ext-dev-build:'+p.id,true));
    if(!p.uploaded)a.appendChild(btn(p.watch?'Stop watching':'Watch for changes','ext-dev-watch:'+p.id,true));else a.appendChild(btn('Upload a new build','ext-dev-upload:'+p.id,true));
    a.appendChild(btn('Package (.zip)','ext-dev-pack:'+p.id,true));a.appendChild(btn('Send to review','ext-dev-review:'+p.id,true));a.appendChild(btn('Remove','ext-dev-remove:'+p.id,true));
    c.appendChild(a);L.appendChild(c)})}
";
    }
}
