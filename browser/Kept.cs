using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Recognition.Browser
{
    // The kept library (proposal 0008): everything you bring into the browser stays until YOU delete it.
    //
    //  * Themes you import (store link, .crx/.zip/.xpi, folder, or "Use this theme" on the Extensions page) are kept with their pictures,
    //    so picking another look never loses them.
    //  * Removing a theme, a library theme, an extension or a developer build moves it to "Removed": Add back brings it back, Delete
    //    forever deletes its files.
    //  * Adding an extension back is not a short cut past governance: its files are re-hashed and must be byte-for-byte the copy that was
    //    removed, the policy decides again, and the ledger records it again (a review still needs your approval).
    //
    // Pure rules (no WPF; executed by browser.tests). The files live under runtime\kept\<kind>\<id>\, the list in runtime\kept\kept.v1.json.
    // A damaged or hand-edited list can only lose entries, never point outside runtime\kept (and runtime\extensions_dev for uploaded builds).
    internal sealed class KeptEntry
    {
        public string Kind { get; set; } = "";      // extension | theme | pack | devbuild
        public string Id { get; set; } = "";        // extension, theme: identity (64 hex); devbuild: project id (16 hex); pack: theme pack id
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string Source { get; set; } = "";
        public string Folder { get; set; } = "";    // devbuild only: the developer's folder, or the uploaded copy
        public bool Uploaded { get; set; }          // devbuild only
        public long Bytes { get; set; }             // size of the files themselves
        public long Stored { get; set; }            // what they take on disk while kept (removed extensions are kept compressed)
        public string AddedUtc { get; set; } = "";
        public string RemovedUtc { get; set; } = "";   // "" = in use / on your list; set = in Removed
        public bool IsRemoved => RemovedUtc.Length > 0;
    }

    internal sealed class KeptFile
    {
        public string Schema { get; set; } = Kept.Schema;
        public List<KeptEntry> Items { get; set; } = new();
    }

    internal static class Kept
    {
        public const string Schema = "recognition.kept.v1";
        // Nothing is ever dropped to make room: entries leave the list only when you press Delete forever. This bound only stops a damaged
        // or hostile list file from being read without end; a million entries is far beyond anything one person brings in.
        public const int MaxEntries = 1_000_000;
        public static readonly string[] Kinds = { "extension", "theme", "pack", "devbuild" };

        static bool Hex(string s, int len) => s.Length == len && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        static bool Clean(string s, int max) => s.Length <= max && !s.Any(char.IsControl);

        public static bool ValidId(string kind, string? id) => id != null && kind switch
        {
            "extension" or "theme" => Hex(id, 64),
            "devbuild" => Hex(id, 16),
            "pack" => ThemeLibrary.ValidId(id),
            _ => false,
        };

        /// <summary>runtime\kept\&lt;kind&gt;\&lt;id&gt; for a valid kind and id, otherwise null (never anything outside runtime\kept).</summary>
        public static string? Folder(string repoRoot, string kind, string id)
        {
            if (!Kinds.Contains(kind) || !ValidId(kind, id)) return null;
            var root = Path.GetFullPath(Path.Combine(repoRoot, "runtime", "kept"));
            var p = Path.GetFullPath(Path.Combine(root, kind, id));
            return p.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? p : null;
        }

        public static KeptFile Parse(string? json, string uploadRoot)
        {
            var st = new KeptFile();
            if (string.IsNullOrWhiteSpace(json)) return st;
            KeptFile? f;
            try { f = JsonSerializer.Deserialize<KeptFile>(json); } catch (JsonException) { return st; }
            if (f == null || f.Schema != Schema || f.Items == null) return st;
            var up = Path.GetFullPath(uploadRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var e in f.Items)
            {
                if (e == null || !Kinds.Contains(e.Kind ?? "") || !ValidId(e.Kind!, e.Id)) continue;
                e.Name ??= ""; e.Version ??= ""; e.Source ??= ""; e.Folder ??= ""; e.AddedUtc ??= ""; e.RemovedUtc ??= "";
                if (!Clean(e.Name, 120) || !Clean(e.Version, 64) || !Clean(e.Source, 200) || !Clean(e.AddedUtc, 40) || !Clean(e.RemovedUtc, 40) || e.Bytes < 0 || e.Stored < 0) continue;
                if (e.Kind == "devbuild")
                {
                    if (e.Folder.Length == 0 || !Clean(e.Folder, 400) || !Path.IsPathFullyQualified(e.Folder)) continue;
                    if (e.Uploaded && !Path.GetFullPath(e.Folder).StartsWith(up, StringComparison.OrdinalIgnoreCase)) continue;
                }
                else { e.Folder = ""; e.Uploaded = false; }
                if (st.Items.Any(x => x.Kind == e.Kind && x.Id == e.Id)) continue;
                st.Items.Add(e);
                if (st.Items.Count >= MaxEntries) break;
            }
            return st;
        }

        /// <summary>Stable output: same list, same bytes (sorted by kind, then id).</summary>
        public static string Serialize(KeptFile f) =>
            JsonSerializer.Serialize(new KeptFile { Schema = Schema, Items = f.Items.OrderBy(e => e.Kind, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal).ToList() },
                new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";

        public static KeptEntry? Find(KeptFile f, string kind, string id) => f.Items.FirstOrDefault(e => e.Kind == kind && e.Id == id);

        /// <summary>Adds an entry or refreshes its name and source; a removed entry that is brought in again is back on your list.</summary>
        public static KeptEntry Upsert(KeptFile f, string kind, string id, string name, string version, string source, long bytes, DateTime nowUtc)
        {
            if (!ValidId(kind, id)) throw new ArgumentException("invalid kept id");
            var e = Find(f, kind, id);
            if (e == null)
            {
                if (f.Items.Count >= MaxEntries) throw new InvalidOperationException("the kept list file has reached its safety bound");   // never drops an entry
                e = new KeptEntry { Kind = kind, Id = id, AddedUtc = Iso(nowUtc) };
                f.Items.Add(e);
            }
            e.Name = Trim(name, 120); e.Version = Trim(version, 64); e.Source = Trim(source, 200); e.Bytes = Math.Max(0, bytes); e.RemovedUtc = "";
            return e;
        }

        /// <summary>runtime\kept\&lt;kind&gt;\&lt;id&gt;.zip: where a removed extension is kept compressed.</summary>
        public static string? Archive(string repoRoot, string kind, string id) { var f = Folder(repoRoot, kind, id); return f == null ? null : f + ".zip"; }

        /// <summary>
        /// The files a theme package really uses, as safe relative paths with the most of each the browser will read: the manifest, the
        /// optional recognition.theme.json, the start page picture and the header pictures. Keeping just these (not the whole package) means
        /// every theme the browser can apply can also be kept, whatever else the package carries.
        /// </summary>
        public static List<(string Rel, long MaxBytes)> ThemeFiles(ThemePack pack, long maxPicture, long maxHeaderPicture, long maxText = 1024 * 1024)
        {
            var list = new List<(string, long)> { ("manifest.json", maxText), (ThemeLibrary.PackageThemeFile, maxText) };
            void Add(string? rel, long max)
            {
                if (string.IsNullOrWhiteSpace(rel)) return;
                var r = rel.Trim().Replace('\\', '/').TrimStart('/');
                if (r.Length == 0 || r.Length > 260 || r.Contains(':') || r.Split('/').Any(s => s == ".." || s == "." || s.Length == 0) || r.Any(char.IsControl)) return;
                if (!list.Any(x => string.Equals(x.Item1, r, StringComparison.OrdinalIgnoreCase))) list.Add((r, max));
            }
            Add(pack.NtpImage, maxPicture);
            if (pack.Header != null) foreach (var l in pack.Header.Layers) Add(FirefoxThemes.SafeHeaderPath(l.Path), maxHeaderPicture);
            return list;
        }

        public static string SizeWords(long bytes) =>
            bytes < 1024 ? bytes + " B" : bytes < 1024 * 1024 ? (bytes / 1024.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " KB"
            : bytes < 1024L * 1024 * 1024 ? (bytes / 1048576.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " MB"
            : (bytes / 1073741824.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " GB";

        public static bool MarkRemoved(KeptFile f, string kind, string id, DateTime nowUtc)
        {
            var e = Find(f, kind, id); if (e == null) return false;
            e.RemovedUtc = Iso(nowUtc); return true;
        }

        public static bool MarkBack(KeptFile f, string kind, string id)
        {
            var e = Find(f, kind, id); if (e == null || !e.IsRemoved) return false;
            e.RemovedUtc = ""; return true;
        }

        public static bool Forget(KeptFile f, string kind, string id) => f.Items.RemoveAll(e => e.Kind == kind && e.Id == id) > 0;

        public static List<KeptEntry> InUse(KeptFile f, string kind) =>
            f.Items.Where(e => e.Kind == kind && !e.IsRemoved).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();

        /// <summary>Removed entries of the given kinds, newest removal first.</summary>
        public static List<KeptEntry> Removed(KeptFile f, params string[] kinds) =>
            f.Items.Where(e => e.IsRemoved && (kinds.Length == 0 || kinds.Contains(e.Kind))).OrderByDescending(e => e.RemovedUtc, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();

        /// <summary>The words for a removed entry in the list, e.g. "Extension · removed Oct 31" (local date supplied by the caller).</summary>
        public static string KindLabel(string kind) => kind switch { "extension" => "Extension", "theme" => "Imported theme", "pack" => "Library theme", "devbuild" => "Developer build", _ => kind };

        static string Iso(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        static string Trim(string? s, int max) { var t = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()); return t.Length > max ? t.Substring(0, max) : t; }
    }
}
