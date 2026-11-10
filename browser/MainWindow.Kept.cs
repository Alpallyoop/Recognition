using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace Recognition.Browser
{
    // The kept library glue (rules and limits: Kept.cs; docs\proposals\0008-kept-library.md). Nothing you bring in is lost by removing it:
    // it waits under Removed (Customize page for themes, Extensions page for extensions and developer builds) until you add it back or
    // delete it forever. Every action here is recorded in the action ledger; extensions coming back go through governance again.
    public partial class MainWindow
    {
        private KeptFile? _keptState;
        private string KeptListPath => Path.Combine(_repoRoot, "runtime", "kept", "kept.v1.json");

        private KeptFile KeptList
        {
            get
            {
                if (_keptState != null) return _keptState;
                string? json = null;
                try { if (File.Exists(KeptListPath)) json = File.ReadAllText(KeptListPath, Encoding.UTF8); }
                catch { _keptUnreadable = true; }   // could not read it: never overwrite it with an empty list
                _keptState = Kept.Parse(json, ExtDevUploadRoot);
                if (!string.IsNullOrWhiteSpace(json) && _keptState.Items.Count == 0 && !json.Contains("\"Items\": []") && !json.Contains("\"Items\":[]")) _keptUnreadable = true;
                return _keptState;
            }
        }

        private bool _keptUnreadable;   // the list file exists but could not be read: it is left as it is, so nothing in it is lost

        private void SaveKept()
        {
            if (_keptUnreadable) { Status("the kept list (runtime\\kept\\kept.v1.json) could not be read, so it was not changed; the kept files are all still there"); return; }
            try { Directory.CreateDirectory(Path.GetDirectoryName(KeptListPath)!); File.WriteAllText(KeptListPath, Kept.Serialize(KeptList), new UTF8Encoding(false)); } catch { }
        }

        // ---- themes from packages: kept with their pictures -------------------------------------------------------------------------

        /// <summary>
        /// Keeps the files a theme just applied really uses (manifest, recognition.theme.json, start page picture, header pictures), so every
        /// theme the browser can apply is kept, whatever else its package carries. Never blocks the theme itself: returns a note on failure.
        /// </summary>
        private string? KeepThemeFolder(string root, ThemePack pack, string source)
        {
            string? tmp = null;
            try
            {
                var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var keptDir = Path.Combine(_repoRoot, "runtime", "kept", "theme"); Directory.CreateDirectory(keptDir);
                tmp = Path.Combine(keptDir, "_part-" + Guid.NewGuid().ToString("N"));
                foreach (var (rel, max) in Kept.ThemeFiles(pack, StartPictureMaxBytes, MaxHeaderFileBytes))
                {
                    var src = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
                    if (!src.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(src) || new FileInfo(src).Length > max) continue;   // the browser could not use it either
                    var to = Path.Combine(tmp, rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!); File.Copy(src, to, true);
                }
                if (!File.Exists(Path.Combine(tmp, "manifest.json"))) { Directory.Delete(tmp, true); return "it has no manifest.json to keep"; }
                var id = ExtGovernance.ComputeIdentity(tmp);
                var dst = Kept.Folder(_repoRoot, "theme", id.Id); if (dst == null) { Directory.Delete(tmp, true); return "it could not be kept"; }
                if (Directory.Exists(dst)) Directory.Delete(tmp, true); else Directory.Move(tmp, dst);
                tmp = null;
                bool isNew = Kept.Find(KeptList, "theme", id.Id) == null;
                var te = Kept.Upsert(KeptList, "theme", id.Id, pack.Name, "", source, id.TotalBytes, DateTime.UtcNow); te.Stored = id.TotalBytes; SaveKept();
                if (isNew) _actions?.Append("kept.add", "theme:" + id.Id);
                return null;
            }
            catch (Exception ex) { return "no copy was kept (" + ex.Message + ")"; }
            finally { if (tmp != null) { try { Directory.Delete(tmp, true); } catch { } } }
        }

        /// <summary>Applies a kept theme again, after checking its files are exactly the ones that were kept.</summary>
        private string UseKeptTheme(string id)
        {
            var e = Kept.Find(KeptList, "theme", id); var dir = Kept.Folder(_repoRoot, "theme", id);
            if (e == null || dir == null || !Directory.Exists(dir)) return "that theme is no longer kept";
            try
            {
                if (ExtGovernance.ComputeIdentity(dir).Id != id) return "the kept copy of \"" + e.Name + "\" changed on disk, so it was not used";
                var root = ExtPackage.ResolveRoot(dir);
                var mp = Path.Combine(root, "manifest.json");
                var pack = ThemeLibrary.FromPackage(File.ReadAllText(mp, Encoding.UTF8).TrimStart('﻿'), ReadPackageTheme(root, out _), out var perr);
                if (pack == null) return perr ?? "that theme could not be read";
                string? img = null;
                if (pack.NtpImage != null)
                {
                    var full = Path.GetFullPath(Path.Combine(root, pack.NtpImage.Replace('/', Path.DirectorySeparatorChar)));
                    if (full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(full)) img = full;
                }
                ApplyPack(pack, img, root);
                _actions?.Append("kept.use", "theme:" + id);
                return "using \"" + pack.Name + "\" again";
            }
            catch (Exception ex) { return "could not use that theme: " + ex.Message; }
        }

        // ---- library themes ("My themes"): Remove moves the file aside --------------------------------------------------------------

        private string RemovePackToKept(string id)
        {
            if (!ThemeLibrary.ValidId(id)) return "that theme is not on your list";
            var f = Path.Combine(ThemesDir, id + ".json");
            if (!File.Exists(f)) return "that theme is not on your list";
            var p = InstalledThemes().FirstOrDefault(x => x.Id == id);
            var dst = Kept.Folder(_repoRoot, "pack", id); if (dst == null) return "that theme could not be moved";
            Directory.CreateDirectory(dst);
            File.Copy(f, Path.Combine(dst, "pack.json"), true); File.Delete(f);
            Kept.Upsert(KeptList, "pack", id, p?.Name ?? id, "", "theme library", new FileInfo(Path.Combine(dst, "pack.json")).Length, DateTime.UtcNow);
            Kept.MarkRemoved(KeptList, "pack", id, DateTime.UtcNow); SaveKept();
            _actions?.Append("kept.remove", "pack:" + id);
            return "\"" + (p?.Name ?? id) + "\" moved to Removed (the current look stays until you pick another)";
        }

        // ---- extensions: Remove keeps the files (ExtRemoveAsync), Add back goes through governance again ------------------------------

        /// <summary>
        /// Moves a removed extension into the kept library, compressed: runtime\kept\extension\&lt;id&gt;.zip. The archive is unpacked once
        /// and re-hashed before the folder goes, so it is only used when it gives back exactly the same files; otherwise the folder itself is
        /// kept. False: it could not be kept at all (the caller deletes it as before).
        /// </summary>
        private bool KeepRemovedExtension(string dir, string gid, string name, string version, string source)
        {
            try
            {
                var dst = Kept.Folder(_repoRoot, "extension", gid); var zip = Kept.Archive(_repoRoot, "extension", gid);
                if (dst == null || zip == null || !Directory.Exists(dir)) return false;
                var id = ExtGovernance.ComputeIdentity(dir);
                if (id.Id != gid) return false;                       // a damaged copy is not kept: it could never be added back
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                long stored;
                if (File.Exists(zip) || Directory.Exists(dst)) { Directory.Delete(dir, true); stored = File.Exists(zip) ? new FileInfo(zip).Length : id.TotalBytes; }   // the same bytes are already kept
                else if (CompressVerified(dir, zip, gid)) { Directory.Delete(dir, true); stored = new FileInfo(zip).Length; }
                else { Directory.Move(dir, dst); stored = id.TotalBytes; }
                var ke = Kept.Upsert(KeptList, "extension", gid, name, version, source, id.TotalBytes, DateTime.UtcNow); ke.Stored = stored;
                Kept.MarkRemoved(KeptList, "extension", gid, DateTime.UtcNow); SaveKept();
                _actions?.Append("kept.remove", "extension:" + gid);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Writes dir as a compressed archive and keeps it only if unpacking it gives back exactly the same identity.</summary>
        private bool CompressVerified(string dir, string zip, string gid)
        {
            var part = zip + ".part"; string? check = null;
            try
            {
                if (File.Exists(part)) File.Delete(part);
                ZipFile.CreateFromDirectory(dir, part, CompressionLevel.SmallestSize, false);
                if (new FileInfo(part).Length > ExtPackage.MaxPackageBytes) { File.Delete(part); return false; }
                check = NewStagingDir();
                var root = ExtPackage.Unpack(File.ReadAllBytes(part), Path.Combine(check, "x"));   // the same hostile-package checks as any download
                if (ExtGovernance.ComputeIdentity(root).Id != gid) { File.Delete(part); return false; }
                File.Move(part, zip);
                return true;
            }
            catch { try { if (File.Exists(part)) File.Delete(part); } catch { } return false; }
            finally { if (check != null) { try { Directory.Delete(check, true); } catch { } } }
        }

        /// <summary>Called after an install succeeds: a kept copy of the same bytes is no longer needed.</summary>
        private void ForgetKeptExtension(string gid)
        {
            var e = Kept.Find(KeptList, "extension", gid); if (e == null) return;
            var dir = Kept.Folder(_repoRoot, "extension", gid); var zip = Kept.Archive(_repoRoot, "extension", gid);
            try { if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            try { if (zip != null && File.Exists(zip)) File.Delete(zip); } catch { }
            Kept.Forget(KeptList, "extension", gid); SaveKept();
        }

        private async Task AddExtensionBackAsync(KeptEntry e)
        {
            var dir = Kept.Folder(_repoRoot, "extension", e.Id); var zip = Kept.Archive(_repoRoot, "extension", e.Id);
            bool hasZip = zip != null && File.Exists(zip), hasDir = dir != null && Directory.Exists(dir);
            if (!hasZip && !hasDir) { ExtReport("the kept files of " + e.Name + " are gone; delete it from Removed", false); return; }
            DiscardPending();
            ExtPending staged;
            try { staged = await Task.Run(() => hasZip ? StageBytes(File.ReadAllBytes(zip!), "added back: " + e.Name) : StageFolder(dir!, "added back: " + e.Name)); }
            catch (Exception ex) { ExtReport("the kept copy of " + e.Name + " could not be read (" + ex.Message + "), so it cannot be added back.", false); return; }
            if (staged.Identity.Id != e.Id)   // the files must be exactly the ones you removed
            {
                try { Directory.Delete(staged.Staging, true); } catch { }
                ExtReport("the kept copy of " + e.Name + " changed on disk since it was removed, so it cannot be added back. Delete it forever and install it again.", false); return;
            }
            _extPending = staged;
            _actions?.Append("kept.back", "extension:" + e.Id);
            var decision = _extPending.Decision.Decision;
            if (decision == "allow") { ExtInstall(false); return; }   // ExtInstall forgets the kept copy once it is installed
            ExtReport(decision == "deny"
                ? e.Name + " is refused by the current policy, so it cannot be added back. It stays under Removed."
                : e.Name + " needs your approval again (the policy asks for a review). Read what it asks for below.", decision != "deny");
            SendExtSnapshot();
        }

        // ---- developer builds -----------------------------------------------------------------------------------------------------

        private void KeepRemovedDevBuild(ExtDevProject p)
        {
            try
            {
                var e = Kept.Upsert(KeptList, "devbuild", p.Id, p.Label, p.Builds.LastOrDefault()?.Version ?? "", p.Uploaded ? "uploaded build" : "your folder", 0, DateTime.UtcNow);
                e.Folder = p.Folder; e.Uploaded = p.Uploaded;
                Kept.MarkRemoved(KeptList, "devbuild", p.Id, DateTime.UtcNow); SaveKept();
                _actions?.Append("kept.remove", "devbuild:" + p.Id);
            }
            catch { }
        }

        private async Task AddDevBuildBackAsync(KeptEntry e)
        {
            if (!Directory.Exists(e.Folder)) { ExtReport("the folder of " + e.Name + " is no longer there (" + e.Folder + ")", false); return; }
            if (e.Uploaded && !IsUnderUploads(e.Folder)) { ExtReport("that build is not where uploaded builds live, so it was not added back", false); return; }
            if (ExtDev.Projects.Count >= ExtDevState.MaxProjects) { ExtReport("the developer list is full (" + ExtDevState.MaxProjects + "); remove one first", false); return; }
            var p = ExtDev.Projects.FirstOrDefault(x => x.Id == e.Id);
            if (p == null) { p = new ExtDevProject { Id = e.Id, Label = e.Name, Folder = e.Folder, Uploaded = e.Uploaded }; ExtDev.Projects.Add(p); }
            Kept.Forget(KeptList, "devbuild", e.Id); SaveKept(); SaveExtDev();
            _actions?.Append("kept.back", "devbuild:" + e.Id);
            var res = await BuildAndRecordAsync(p); SaveExtDev();
            ExtReport("added " + p.Label + " back: " + (res.Report.Runnable ? "ready to run." : res.Report.Errors + " problem(s) to fix first."), res.Report.Runnable);
        }

        // ---- Delete forever -----------------------------------------------------------------------------------------------------

        private bool DeleteKeptForever(KeptEntry e)
        {
            if (MessageBox.Show(this, "Delete \"" + e.Name + "\" forever?\n\nIts kept files are deleted from this computer. To use it again you would have to add it from scratch.",
                    "Recognition - Removed", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
            try
            {
                if (e.Kind == "devbuild")
                {
                    if (e.Uploaded && IsUnderUploads(e.Folder) && Directory.Exists(e.Folder) && !ExtDev.Projects.Any(x => string.Equals(x.Folder, e.Folder, StringComparison.OrdinalIgnoreCase))
                        && !KeptList.Items.Any(k => k != e && k.Kind == "devbuild" && string.Equals(k.Folder, e.Folder, StringComparison.OrdinalIgnoreCase)))
                        Directory.Delete(e.Folder, true);   // your own folder is never touched
                }
                else
                {
                    var dir = Kept.Folder(_repoRoot, e.Kind, e.Id); if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true);
                    var zip = Kept.Archive(_repoRoot, e.Kind, e.Id); if (zip != null && File.Exists(zip)) File.Delete(zip);
                }
            }
            catch (Exception ex) { Status("some files could not be deleted: " + ex.Message); }
            Kept.Forget(KeptList, e.Kind, e.Id); SaveKept();
            _actions?.Append("kept.delete", e.Kind + ":" + e.Id);
            return true;
        }

        // ---- messages ---------------------------------------------------------------------------------------------------------------

        // "<prefix><kind>:<id>" -> the entry, or null
        private KeptEntry? KeptFromMsg(string msg, string prefix)
        {
            var rest = msg.Substring(prefix.Length); var i = rest.IndexOf(':'); if (i <= 0) return null;
            return Kept.Find(KeptList, rest.Substring(0, i), rest.Substring(i + 1));
        }

        /// <summary>cz-kept-* from the Customize page. True when handled.</summary>
        private bool HandleKeptCustomize(BrowserTab tab, string msg, Action<string> note)
        {
            if (msg.StartsWith("cz-kept-use:", StringComparison.Ordinal)) { note(UseKeptTheme(msg.Substring("cz-kept-use:".Length))); }
            else if (msg.StartsWith("cz-kept-rm:", StringComparison.Ordinal))
            {
                var id = msg.Substring("cz-kept-rm:".Length); var e = Kept.Find(KeptList, "theme", id); if (e == null) return true;
                Kept.MarkRemoved(KeptList, "theme", id, DateTime.UtcNow); SaveKept(); _actions?.Append("kept.remove", "theme:" + id);
                note("\"" + e.Name + "\" moved to Removed (the current look stays until you pick another)");
            }
            else if (msg.StartsWith("cz-kept-back:", StringComparison.Ordinal))
            {
                var e = KeptFromMsg(msg, "cz-kept-back:"); if (e == null || (e.Kind != "theme" && e.Kind != "pack")) return true;
                if (e.Kind == "theme") { Kept.MarkBack(KeptList, "theme", e.Id); SaveKept(); _actions?.Append("kept.back", "theme:" + e.Id); note("\"" + e.Name + "\" is back under Themes you added"); }
                else
                {
                    var src = Path.Combine(Kept.Folder(_repoRoot, "pack", e.Id) ?? "", "pack.json");
                    if (!File.Exists(src)) { note("the kept file of \"" + e.Name + "\" is gone"); return true; }
                    var pack = ThemeLibrary.ParsePack(File.ReadAllText(src, Encoding.UTF8));
                    if (pack == null || pack.Id != e.Id || !SaveInstalledTheme(pack)) { note("\"" + e.Name + "\" could not be added back"); return true; }
                    try { Directory.Delete(Path.GetDirectoryName(src)!, true); } catch { }
                    Kept.Forget(KeptList, "pack", e.Id); SaveKept(); _actions?.Append("kept.back", "pack:" + e.Id);
                    note("\"" + e.Name + "\" is back under My themes");
                }
            }
            else if (msg.StartsWith("cz-kept-del:", StringComparison.Ordinal))
            {
                var e = KeptFromMsg(msg, "cz-kept-del:"); if (e == null || (e.Kind != "theme" && e.Kind != "pack")) return true;
                if (DeleteKeptForever(e)) note("\"" + e.Name + "\" deleted forever");
            }
            else return false;
            RefreshCustomize(tab);
            return true;
        }

        /// <summary>ext-kept-* from the Extensions page.</summary>
        private async Task HandleKeptExtMessage(string msg)
        {
            if (msg.StartsWith("ext-kept-back:", StringComparison.Ordinal))
            {
                var e = KeptFromMsg(msg, "ext-kept-back:"); if (e == null) return;
                if (e.Kind == "extension") await AddExtensionBackAsync(e);
                else if (e.Kind == "devbuild") await AddDevBuildBackAsync(e);
            }
            else if (msg.StartsWith("ext-kept-del:", StringComparison.Ordinal))
            {
                var e = KeptFromMsg(msg, "ext-kept-del:"); if (e == null || (e.Kind != "extension" && e.Kind != "devbuild")) return;
                if (DeleteKeptForever(e)) ExtReport(e.Name + " deleted forever.");
            }
            SendExtSnapshot();
        }

        private object KeptExtView() => Kept.Removed(KeptList, "extension", "devbuild").Select(e => new
        {
            kind = e.Kind, id = e.Id, name = CleanLabel(e.Name), version = CleanLabel(e.Version), label = Kept.KindLabel(e.Kind), source = CleanLabel(e.Source),
            removed = LocalTime(e.RemovedUtc), bytes = e.Bytes, stored = e.Stored,
            missing = e.Kind == "devbuild" ? !Directory.Exists(e.Folder) : !Directory.Exists(Kept.Folder(_repoRoot, e.Kind, e.Id) ?? "") && !File.Exists(Kept.Archive(_repoRoot, e.Kind, e.Id) ?? ""),
        }).ToList();

        // Customize page: themes you imported (in use), and everything removed from the theme lists
        private string KeptThemesHtml()
        {
            var sb = new StringBuilder();
            var mine = Kept.InUse(KeptList, "theme");
            if (mine.Count > 0)
            {
                sb.Append("<div class='sec'>Themes you added</div><div class='muted' style='margin:0 0 8px'>Chrome and Firefox themes you brought in, kept with their pictures so you can switch back any time.</div>");
                foreach (var e in mine)
                    sb.Append("<div class='row'><div><div class='t'>" + Esc(e.Name) + "</div><div class='u'>" + Esc(e.Source) + " · added " + Esc(LocalTime(e.AddedUtc)) + " · " + Esc(SizeText(e.Bytes)) + "</div></div>" +
                              "<div class='ts'><a class='btn' onclick=\"send('cz-kept-use:" + e.Id + "')\">Use</a> <a class='btn ghost' onclick=\"send('cz-kept-rm:" + e.Id + "')\">Remove</a></div></div>");
            }
            var gone = Kept.Removed(KeptList, "theme", "pack");
            if (gone.Count > 0)
            {
                sb.Append("<div class='sec'>Removed themes</div><div class='muted' style='margin:0 0 8px'>Kept until you delete them. Add back puts a theme on your list again.</div>");
                foreach (var e in gone)
                    sb.Append("<div class='row'><div><div class='t'>" + Esc(e.Name) + "</div><div class='u'>" + Esc(Kept.KindLabel(e.Kind)) + " · removed " + Esc(LocalTime(e.RemovedUtc)) + "</div></div>" +
                              "<div class='ts'><a class='btn' onclick=\"send('cz-kept-back:" + e.Kind + ":" + e.Id + "')\">Add back</a> <a class='btn ghost' onclick=\"send('cz-kept-del:" + e.Kind + ":" + e.Id + "')\">Delete forever</a></div></div>");
            }
            return sb.ToString();
        }
    }
}
