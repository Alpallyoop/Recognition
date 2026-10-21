using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Recognition.Browser
{
    // Extension developer area (recognition:extensions, Developer tab; docs\proposals\0003-extension-developer-area.md).
    //
    // Pure logic, no WPF or WebView2, executed by browser.tests:
    //   * ExtDevCheck  - reads a build's manifest.json and reports problems before the engine ever sees it (missing files the manifest
    //                    points to, bad version strings, unsafe paths, locale mistakes, what the governance policy will say, and the
    //                    parts of the extension system the WebView2 engine may not support).
    //   * ExtDevDiff   - what changed between two builds: files, version, permissions and site access added or removed.
    //   * ExtDevPack   - a deterministic .zip of a build (sorted entries, fixed timestamps, stored, no extra fields), so the same files
    //                    always give the same package bytes and the same SHA-256.
    //   * ExtDevState  - the list of builds under development (runtime\extensions_dev.v1.json), validated on load.
    //
    // Trust model, unchanged: a developer build never enters the main profile from here. It runs only in a separate, temporary developer
    // profile (deleted when Recognition closes). Installing it for everyday use goes through the normal review, policy and ledger.
    internal enum ExtDevLevel { Error, Warning, Info }

    internal sealed record ExtDevFinding(ExtDevLevel Level, string Code, string Text);

    internal sealed class ExtDevReport
    {
        public List<ExtDevFinding> Findings { get; } = new();
        public string Name = "", Version = "";
        public int ManifestVersion;
        public int Errors => Findings.Count(f => f.Level == ExtDevLevel.Error);
        public int Warnings => Findings.Count(f => f.Level == ExtDevLevel.Warning);
        public bool Runnable => Errors == 0;
        internal void Add(ExtDevLevel l, string code, string text) { if (!Findings.Any(f => f.Code == code && f.Text == text)) Findings.Add(new ExtDevFinding(l, code, text)); }
    }

    internal static class ExtDevCheck
    {
        // Parts of the Chromium extension system that WebView2 may not support (Microsoft documents a subset). Reported as information,
        // never as an error: the engine is the judge, and Test/Run shows what really happens.
        private static readonly string[] EngineMayNotSupport =
            { "side_panel", "devtools_page", "chrome_url_overrides", "omnibox", "commands", "tts_engine", "file_browser_handlers", "input_components" };

        public static bool IsValidVersion(string? v)
        {
            if (string.IsNullOrEmpty(v) || v.Length > 64) return false;
            var parts = v.Split('.');
            if (parts.Length < 1 || parts.Length > 4) return false;
            foreach (var p in parts)
            {
                if (p.Length == 0 || p.Length > 5 || !p.All(c => c >= '0' && c <= '9')) return false;
                if (p.Length > 1 && p[0] == '0') return false;
                if (int.Parse(p, CultureInfo.InvariantCulture) > 65535) return false;
            }
            return true;
        }

        // A path the manifest points at must stay inside the build and name a real file. Returns null when fine, else the problem.
        public static string? PathProblem(string root, string rel)
        {
            if (string.IsNullOrWhiteSpace(rel)) return "empty path";
            var r = rel.Split('?', '#')[0];
            if (r.Contains(':') || r.StartsWith("//", StringComparison.Ordinal)) return "not a path inside the extension (" + rel + ")";
            if (r.Contains('\\')) return "uses a backslash; extension paths use '/' (" + rel + ")";
            var parts = r.TrimStart('/').Split('/');
            if (parts.Any(s => s == "..")) return "points outside the extension (" + rel + ")";
            var full = Path.GetFullPath(Path.Combine(root, r.TrimStart('/')));
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return "points outside the extension (" + rel + ")";
            if (!File.Exists(full)) return "missing file: " + r.TrimStart('/');
            return null;
        }

        private static IEnumerable<string> Str(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.String) { var s = e.GetString(); if (!string.IsNullOrEmpty(s)) yield return s!; }
            else if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) if (x.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(x.GetString())) yield return x.GetString()!;
        }

        private static IEnumerable<string> IconPaths(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.String) { foreach (var s in Str(e)) yield return s; }
            else if (e.ValueKind == JsonValueKind.Object) foreach (var p in e.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(p.Value.GetString())) yield return p.Value.GetString()!;
        }

        private static bool Get(JsonElement o, string k, out JsonElement v) { v = default; return o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out v); }

        // Checks one unpacked build. Never throws for a bad build: everything wrong becomes a finding. policyJson may be null (no preview).
        public static ExtDevReport Run(string root, string? policyJson)
        {
            var rep = new ExtDevReport();
            var mp = Path.Combine(root, "manifest.json");
            if (!Directory.Exists(root)) { rep.Add(ExtDevLevel.Error, "no_folder", "the build folder does not exist"); return rep; }
            if (!File.Exists(mp)) { rep.Add(ExtDevLevel.Error, "no_manifest", "manifest.json is missing (it must be at the top of the build)"); return rep; }
            byte[] raw;
            try { raw = File.ReadAllBytes(mp); } catch (Exception ex) { rep.Add(ExtDevLevel.Error, "manifest_unreadable", "manifest.json cannot be read: " + ex.Message); return rep; }
            if (raw.Length > 1024 * 1024) { rep.Add(ExtDevLevel.Error, "manifest_too_large", "manifest.json is larger than 1 MB"); return rep; }
            if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) rep.Add(ExtDevLevel.Warning, "manifest_bom", "manifest.json starts with a byte-order mark; Chrome accepts it, some tools do not");
            JsonDocument doc;
            try { doc = JsonDocument.Parse(Encoding.UTF8.GetString(raw).TrimStart('﻿'), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
            catch (JsonException ex) { rep.Add(ExtDevLevel.Error, "manifest_json", "manifest.json is not valid JSON (line " + ((ex.LineNumber ?? 0) + 1) + ")"); return rep; }
            using (doc)
            {
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object) { rep.Add(ExtDevLevel.Error, "manifest_json", "manifest.json must be a JSON object"); return rep; }

                // identity fields
                string S(string k) => Get(r, k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                rep.Name = S("name"); rep.Version = S("version");
                if (rep.Name.Trim().Length == 0) rep.Add(ExtDevLevel.Error, "name", "\"name\" is missing");
                else if (rep.Name.Length > 75 && !rep.Name.StartsWith("__MSG_", StringComparison.Ordinal)) rep.Add(ExtDevLevel.Warning, "name_long", "\"name\" is longer than 75 characters; stores cut it");
                if (rep.Version.Length == 0) rep.Add(ExtDevLevel.Error, "version", "\"version\" is missing");
                else if (!IsValidVersion(rep.Version)) rep.Add(ExtDevLevel.Error, "version", "\"version\" must be 1 to 4 dot-separated whole numbers from 0 to 65535 without leading zeros (got \"" + rep.Version + "\")");
                if (Get(r, "manifest_version", out var mv) && mv.ValueKind == JsonValueKind.Number && mv.TryGetInt32(out var mvi)) rep.ManifestVersion = mvi;
                if (rep.ManifestVersion == 2) rep.Add(ExtDevLevel.Warning, "mv2", "manifest_version 2 is retired in Chromium; the engine may refuse it. Move to manifest_version 3");
                else if (rep.ManifestVersion != 3) rep.Add(ExtDevLevel.Error, "manifest_version", "\"manifest_version\" must be 3 (or the retired 2)");

                // locales
                var defaultLocale = S("default_locale");
                bool usesMsg = raw.Length > 0 && Encoding.UTF8.GetString(raw).Contains("__MSG_", StringComparison.Ordinal);
                if (defaultLocale.Length > 0)
                {
                    if (defaultLocale.IndexOfAny(new[] { '/', '\\', '.', ':' }) >= 0) rep.Add(ExtDevLevel.Error, "locale", "\"default_locale\" is not a locale name");
                    else if (!File.Exists(Path.Combine(root, "_locales", defaultLocale, "messages.json"))) rep.Add(ExtDevLevel.Error, "locale", "\"default_locale\" is \"" + defaultLocale + "\" but _locales/" + defaultLocale + "/messages.json is missing");
                }
                else if (usesMsg) rep.Add(ExtDevLevel.Error, "locale", "the manifest uses __MSG_ names but has no \"default_locale\"");
                else if (Directory.Exists(Path.Combine(root, "_locales"))) rep.Add(ExtDevLevel.Error, "locale", "a _locales folder exists but \"default_locale\" is not set");

                // every file the manifest points to
                var refs = new List<(string Where, string Path)>();
                if (Get(r, "background", out var bg))
                {
                    if (Get(bg, "service_worker", out var sw)) foreach (var s in Str(sw)) refs.Add(("background.service_worker", s));
                    if (Get(bg, "scripts", out var bs)) foreach (var s in Str(bs)) refs.Add(("background.scripts", s));
                    if (Get(bg, "page", out var bp)) foreach (var s in Str(bp)) refs.Add(("background.page", s));
                    if (rep.ManifestVersion == 3 && (Get(bg, "scripts", out _) || Get(bg, "page", out _)) && !Get(bg, "service_worker", out _))
                        rep.Add(ExtDevLevel.Error, "mv3_background", "manifest_version 3 needs \"background.service_worker\"; background pages and scripts are manifest_version 2 only");
                    if (Get(bg, "persistent", out _) && rep.ManifestVersion == 3) rep.Add(ExtDevLevel.Warning, "mv3_persistent", "\"background.persistent\" has no meaning in manifest_version 3");
                }
                if (Get(r, "content_scripts", out var cs) && cs.ValueKind == JsonValueKind.Array)
                {
                    int i = 0;
                    foreach (var c in cs.EnumerateArray())
                    {
                        if (!Get(c, "matches", out var mt) || !Str(mt).Any()) rep.Add(ExtDevLevel.Error, "content_matches", "content_scripts[" + i + "] has no \"matches\"");
                        if (Get(c, "js", out var js)) foreach (var s in Str(js)) refs.Add(("content_scripts[" + i + "].js", s));
                        if (Get(c, "css", out var css)) foreach (var s in Str(css)) refs.Add(("content_scripts[" + i + "].css", s));
                        i++;
                    }
                }
                foreach (var key in new[] { "action", "browser_action", "page_action" })
                    if (Get(r, key, out var a))
                    {
                        if (Get(a, "default_popup", out var pp)) foreach (var s in Str(pp)) refs.Add((key + ".default_popup", s));
                        if (Get(a, "default_icon", out var di)) foreach (var s in IconPaths(di)) refs.Add((key + ".default_icon", s));
                        if (key != "action" && rep.ManifestVersion == 3) rep.Add(ExtDevLevel.Error, "mv3_action", "\"" + key + "\" is manifest_version 2 only; use \"action\"");
                    }
                if (Get(r, "icons", out var ic)) foreach (var s in IconPaths(ic)) refs.Add(("icons", s));
                else rep.Add(ExtDevLevel.Info, "no_icons", "no \"icons\": stores and the Extensions page show a generic picture");
                if (Get(r, "options_page", out var op)) foreach (var s in Str(op)) refs.Add(("options_page", s));
                if (Get(r, "options_ui", out var ou) && Get(ou, "page", out var oup)) foreach (var s in Str(oup)) refs.Add(("options_ui.page", s));
                if (Get(r, "side_panel", out var sp) && Get(sp, "default_path", out var spp)) foreach (var s in Str(spp)) refs.Add(("side_panel.default_path", s));
                if (Get(r, "devtools_page", out var dp)) foreach (var s in Str(dp)) refs.Add(("devtools_page", s));
                if (Get(r, "chrome_url_overrides", out var co) && co.ValueKind == JsonValueKind.Object) foreach (var p in co.EnumerateObject()) foreach (var s in Str(p.Value)) refs.Add(("chrome_url_overrides." + p.Name, s));
                if (Get(r, "web_accessible_resources", out var war) && war.ValueKind == JsonValueKind.Array)
                    foreach (var w in war.EnumerateArray())
                    {
                        var list = w.ValueKind == JsonValueKind.String ? Str(w) : (Get(w, "resources", out var rs) ? Str(rs) : Enumerable.Empty<string>());
                        if (rep.ManifestVersion == 3 && w.ValueKind == JsonValueKind.String) rep.Add(ExtDevLevel.Error, "mv3_war", "manifest_version 3 \"web_accessible_resources\" entries must be objects with \"resources\" and \"matches\"");
                        foreach (var s in list) if (!s.Contains('*')) refs.Add(("web_accessible_resources", s));
                    }
                foreach (var (where, rel) in refs)
                {
                    var prob = PathProblem(root, rel);
                    if (prob != null) rep.Add(ExtDevLevel.Error, "file_ref", where + ": " + prob);
                }

                // engine support notes
                foreach (var k in EngineMayNotSupport) if (Get(r, k, out _)) rep.Add(ExtDevLevel.Info, "engine_" + k, "\"" + k + "\" may not be supported by the WebView2 engine; check it with Run");
                if (Get(r, "action", out var act) && Get(act, "default_popup", out _)) rep.Add(ExtDevLevel.Info, "engine_popup", "toolbar popups are not shown by the WebView2 engine; Run opens the popup page in a tab instead");
                if (Get(r, "key", out _)) rep.Add(ExtDevLevel.Info, "key", "\"key\" is set: the engine id stays fixed across builds");

                // development leftovers that end up in the package (and in its identity)
                foreach (var junk in new[] { "node_modules", ".vscode", ".idea" })
                    if (Directory.Exists(Path.Combine(root, junk))) rep.Add(ExtDevLevel.Warning, "junk_" + junk, "the build contains " + junk + "/; it is hashed and shipped with the extension. Build into a separate folder");
                if (Directory.EnumerateFiles(root, "*.map", SearchOption.AllDirectories).Take(1).Any()) rep.Add(ExtDevLevel.Info, "sourcemaps", "the build contains source maps (*.map)");

                // what the governance policy will say (the same function the install review uses)
                if (policyJson != null)
                {
                    try
                    {
                        var man = ExtGovernance.ReadManifest(root);
                        var dec = ExtGovernance.Decide(man, new string('0', 64), ExtGovernance.ParsePolicy(policyJson));
                        var why = dec.Reasons.Count > 0 ? " (" + string.Join("; ", dec.Reasons) + ")" : "";
                        if (dec.Decision == "deny") rep.Add(ExtDevLevel.Error, "policy_deny", "the governance policy will refuse this build" + why + ". It cannot be run or installed unless the policy (config\\extension_policy.v1.json) is changed by its owner");
                        else if (dec.Decision == "review") rep.Add(ExtDevLevel.Warning, "policy_review", "installing it will need an explicit review of its permissions" + why);
                        else rep.Add(ExtDevLevel.Info, "policy_allow", "the governance policy allows this build" + why);
                    }
                    catch (ExtGovException ex) { rep.Add(ExtDevLevel.Error, "policy", "the policy check failed: " + ex.Message); }
                }
            }
            return rep;
        }
    }

    internal sealed class ExtDevChange
    {
        public string OldVersion = "", NewVersion = "";
        public List<string> FilesAdded = new(), FilesRemoved = new(), FilesChanged = new();
        public List<string> PermsAdded = new(), PermsRemoved = new(), HostsAdded = new(), HostsRemoved = new();
        public bool Same => FilesAdded.Count + FilesRemoved.Count + FilesChanged.Count == 0;
        public bool AccessGrew => PermsAdded.Count + HostsAdded.Count > 0;
    }

    internal static class ExtDevDiff
    {
        public static ExtDevChange Compare(IReadOnlyList<ExtFile> oldFiles, ExtManifest oldMan, IReadOnlyList<ExtFile> newFiles, ExtManifest newMan)
        {
            var c = new ExtDevChange { OldVersion = oldMan.Version, NewVersion = newMan.Version };
            var o = oldFiles.ToDictionary(f => f.Path, f => f.Sha256, StringComparer.Ordinal);
            var n = newFiles.ToDictionary(f => f.Path, f => f.Sha256, StringComparer.Ordinal);
            foreach (var p in n.Keys.OrderBy(x => x, StringComparer.Ordinal)) { if (!o.TryGetValue(p, out var h)) c.FilesAdded.Add(p); else if (h != n[p]) c.FilesChanged.Add(p); }
            foreach (var p in o.Keys.OrderBy(x => x, StringComparer.Ordinal)) if (!n.ContainsKey(p)) c.FilesRemoved.Add(p);
            static List<string> Minus(IEnumerable<string> a, IEnumerable<string> b) { var bs = new HashSet<string>(b, StringComparer.Ordinal); return a.Distinct(StringComparer.Ordinal).Where(x => !bs.Contains(x)).OrderBy(x => x, StringComparer.Ordinal).ToList(); }
            c.PermsAdded = Minus(newMan.Permissions, oldMan.Permissions); c.PermsRemoved = Minus(oldMan.Permissions, newMan.Permissions);
            c.HostsAdded = Minus(newMan.HostPermissions, oldMan.HostPermissions); c.HostsRemoved = Minus(oldMan.HostPermissions, newMan.HostPermissions);
            return c;
        }
    }

    internal static class ExtDevPack
    {
        // 1980-01-01 00:00:00 is the earliest time a ZIP entry can hold; using it for every entry keeps the bytes independent of when
        // and where the files were written.
        public static readonly DateTimeOffset FixedTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // A deterministic package of exactly the files that make up the build's identity (same order, same paths, .git excluded).
        // Entries are stored (no compression), so the bytes do not depend on the compressor's version.
        public static byte[] Zip(string root)
        {
            var id = ExtGovernance.ComputeIdentity(root);   // refuses links, enforces size and count limits
            using var ms = new MemoryStream();
            using (var za = new ZipArchive(ms, ZipArchiveMode.Create, true, new UTF8Encoding(false)))
            {
                foreach (var f in id.Files)
                {
                    if (!ExtPackage.IsSafeEntryName(f.Path)) throw new ExtGovException("unsafe file name for a package: " + f.Path);
                    var bytes = File.ReadAllBytes(Path.Combine(root, f.Path.Replace('/', Path.DirectorySeparatorChar)));
                    if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant() != f.Sha256)
                        throw new ExtGovException("a file changed while it was being packaged: " + f.Path);
                    var e = za.CreateEntry(f.Path, CompressionLevel.NoCompression);
                    e.LastWriteTime = FixedTime;
                    e.ExternalAttributes = 0;
                    using var s = e.Open(); s.Write(bytes, 0, bytes.Length);
                }
            }
            return ms.ToArray();
        }
    }

    internal sealed class ExtDevBuild
    {
        public string Gid { get; set; } = "";
        public string Version { get; set; } = "";
        public string Utc { get; set; } = "";
        public int Errors { get; set; }
        public int Warnings { get; set; }
    }

    internal sealed class ExtDevProject
    {
        public string Id { get; set; } = "";        // 16 hex: stable handle for the page
        public string Label { get; set; } = "";
        public string Folder { get; set; } = "";    // absolute path of the developer's folder, or the uploaded copy under runtime\extensions_dev
        public bool Uploaded { get; set; }
        public bool Watch { get; set; }
        public List<ExtDevBuild> Builds { get; set; } = new();
    }

    internal sealed class ExtDevStateFile
    {
        public string Schema { get; set; } = ExtDevState.Schema;
        public List<ExtDevProject> Projects { get; set; } = new();
    }

    internal static class ExtDevState
    {
        public const string Schema = "recognition.extensions_dev.v1";
        public const int MaxProjects = 20, MaxBuilds = 30;

        static bool Hex(string s, int len) => s.Length == len && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        static bool Clean(string s, int max) => s.Length <= max && !s.Any(char.IsControl);

        // uploadRoot: the only place an uploaded build may live (runtime\extensions_dev). A damaged or hand-edited file can only lose
        // entries, never point an "uploaded" project somewhere else.
        public static ExtDevStateFile Parse(string? json, string uploadRoot)
        {
            var st = new ExtDevStateFile();
            if (string.IsNullOrWhiteSpace(json)) return st;
            ExtDevStateFile? f;
            try { f = JsonSerializer.Deserialize<ExtDevStateFile>(json); } catch (JsonException) { return st; }
            if (f == null || f.Schema != Schema || f.Projects == null) return st;
            var up = Path.GetFullPath(uploadRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var p in f.Projects)
            {
                if (p == null || !Hex(p.Id ?? "", 16) || !Clean(p.Label ?? "", 120) || string.IsNullOrEmpty(p.Folder) || !Clean(p.Folder, 400) || !Path.IsPathFullyQualified(p.Folder)) continue;
                if (st.Projects.Any(x => x.Id == p.Id)) continue;
                if (p.Uploaded && !Path.GetFullPath(p.Folder).StartsWith(up, StringComparison.OrdinalIgnoreCase)) continue;
                p.Builds = (p.Builds ?? new()).Where(b => b != null && Hex(b.Gid ?? "", 64) && Clean(b.Version ?? "", 64) && Clean(b.Utc ?? "", 40) && b.Errors >= 0 && b.Warnings >= 0).TakeLast(MaxBuilds).ToList();
                st.Projects.Add(p);
                if (st.Projects.Count >= MaxProjects) break;
            }
            return st;
        }

        public static string ToJson(ExtDevStateFile st) => JsonSerializer.Serialize(st, new JsonSerializerOptions { WriteIndented = false });

        public static void AddBuild(ExtDevProject p, ExtDevBuild b)
        {
            if (p.Builds.Count > 0 && p.Builds[^1].Gid == b.Gid) { p.Builds[^1] = b; return; }   // the same bytes again: refresh, not a new build
            p.Builds.Add(b);
            if (p.Builds.Count > MaxBuilds) p.Builds.RemoveRange(0, p.Builds.Count - MaxBuilds);
        }

        public static string NewId(string seed) => ExtGovernance.Sha256Hex(seed + "|" + Guid.NewGuid().ToString("N")).Substring(0, 16);
    }
}
