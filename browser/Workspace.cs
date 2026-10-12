using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Recognition.Browser
{
    // Pure rules for the workspace features: tab groups, restoring your tabs, editable start page tiles, translate links, and the sync data format.
    // No WPF, no files, no network. Everything that is read back from disk or from another device is treated as hostile and re-validated.

    internal sealed record GroupInfo(string Id, string Name, string Color);

    internal static class TabGroups
    {
        public const int MaxGroups = 12;
        public static readonly string[] Palette = { "#4c9bf0", "#e5736b", "#e8b04a", "#5fbf8a", "#a07be8", "#e07bb0", "#4fb8c9", "#9aa3b2" };
        public static readonly Regex IdRx = new("^g[0-9]{1,3}$", RegexOptions.Compiled);

        // letters, digits, space and a few marks; at most 24 characters
        public static string CleanName(string? name)
        {
            var s = new string((name ?? "").Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '.' || c == '&' || c == '\'').ToArray()).Trim();
            s = Regex.Replace(s, " {2,}", " ");
            return s.Length > 24 ? s.Substring(0, 24).TrimEnd() : s;
        }

        public static string ValidColor(string? c)
        {
            var k = (c ?? "").Trim().ToLowerInvariant();
            return Array.IndexOf(Palette, k) >= 0 ? k : Palette[0];
        }

        public static string NextColor(IEnumerable<string>? used)
        {
            var u = new HashSet<string>((used ?? Array.Empty<string>()).Select(x => x.ToLowerInvariant()));
            foreach (var p in Palette) if (!u.Contains(p)) return p;
            return Palette[u.Count % Palette.Length];
        }

        public static string NextId(IEnumerable<string>? existing)
        {
            var e = new HashSet<string>(existing ?? Array.Empty<string>());
            for (int i = 1; i < 1000; i++) { var id = "g" + i; if (!e.Contains(id)) return id; }
            return "g999";
        }

        public sealed record ArrangeItem(int Key, string? Group, bool Pinned);

        // The order tabs should have: pinned tabs first (never in a group), then every other tab, with the members of a group pulled together
        // at the place of the group's first tab. Everything else keeps its order.
        public static List<int> Arrange(IReadOnlyList<ArrangeItem> items)
        {
            var res = new List<int>(items.Count);
            foreach (var it in items) if (it.Pinned) res.Add(it.Key);
            var emitted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var it in items)
            {
                if (it.Pinned) continue;
                if (string.IsNullOrEmpty(it.Group)) { res.Add(it.Key); continue; }
                if (!emitted.Add(it.Group)) continue;
                foreach (var m in items) if (!m.Pinned && m.Group == it.Group) res.Add(m.Key);
            }
            return res;
        }
    }

    internal sealed record SessionTab(string Url, bool Pinned, string? Group);
    internal sealed record SessionGroup(string Id, string Name, string Color, bool Collapsed);
    internal sealed record SessionData(List<SessionTab> Tabs, List<SessionGroup> Groups, int Active);

    internal static class SessionRules
    {
        public const string Schema = "recognition.session.v1";
        public const int MaxTabs = 60, MaxBytes = 1_000_000;

        public static string Serialize(SessionData d) => JsonSerializer.Serialize(new
        {
            schema = Schema,
            active = d.Active,
            groups = d.Groups.Select(g => new { id = g.Id, name = g.Name, color = g.Color, collapsed = g.Collapsed }).ToList(),
            tabs = d.Tabs.Select(t => new { url = t.Url, pinned = t.Pinned, group = t.Group }).ToList()
        });

        // null when the text is not a usable session. Bad tabs are dropped one by one; nothing here can throw.
        public static SessionData? Parse(string? json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json) || json.Length > MaxBytes) return null;
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("schema", out var sc) || sc.GetString() != Schema) return null;
                var groups = new List<SessionGroup>();
                if (r.TryGetProperty("groups", out var gs) && gs.ValueKind == JsonValueKind.Array)
                    foreach (var g in gs.EnumerateArray())
                    {
                        if (groups.Count >= TabGroups.MaxGroups || g.ValueKind != JsonValueKind.Object) continue;
                        var id = g.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() ?? "" : "";
                        if (!TabGroups.IdRx.IsMatch(id) || groups.Any(x => x.Id == id)) continue;
                        var name = TabGroups.CleanName(g.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : "");
                        if (name.Length == 0) name = "Group";
                        var color = TabGroups.ValidColor(g.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null);
                        var col = g.TryGetProperty("collapsed", out var cl) && cl.ValueKind == JsonValueKind.True;
                        groups.Add(new SessionGroup(id, name, color, col));
                    }
                var tabs = new List<SessionTab>();
                if (r.TryGetProperty("tabs", out var ts) && ts.ValueKind == JsonValueKind.Array)
                    foreach (var t in ts.EnumerateArray())
                    {
                        if (tabs.Count >= MaxTabs || t.ValueKind != JsonValueKind.Object) continue;
                        var url = LaunchArgs.Parse(t.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null);
                        if (url == null) continue;
                        var pinned = t.TryGetProperty("pinned", out var p) && p.ValueKind == JsonValueKind.True;
                        string? grp = t.TryGetProperty("group", out var gv) && gv.ValueKind == JsonValueKind.String ? gv.GetString() : null;
                        if (pinned || grp == null || !groups.Any(x => x.Id == grp)) grp = null;
                        tabs.Add(new SessionTab(url, pinned, grp));
                    }
                if (tabs.Count == 0) return null;
                int active = r.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt32(out var av) ? av : 0;
                active = Math.Max(0, Math.Min(active, tabs.Count - 1));
                var used = new HashSet<string>(tabs.Where(x => x.Group != null).Select(x => x.Group!));
                return new SessionData(tabs, groups.Where(x => used.Contains(x.Id)).ToList(), active);
            }
            catch { return null; }
        }
    }

    internal sealed record Tile(string Url, string Host, string Title, bool Custom, int Visits);

    internal static class TileRules
    {
        public const int MaxCustom = 12, MaxHidden = 200, MaxTiles = 8;
        private static readonly Regex HostRx = new("^[a-z0-9]([a-z0-9.\\-]{0,251}[a-z0-9])?$", RegexOptions.Compiled);

        public static string? HostOf(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) || string.IsNullOrEmpty(u.Host)) return null;
            var h = u.Host.ToLowerInvariant(); if (h.StartsWith("www.")) h = h.Substring(4);
            return HostRx.IsMatch(h) ? h : null;
        }

        // what you typed in "Add a site": a bare name or an https/http address. Returns the tile address, or null.
        public static string? NormalizeNew(string? input)
        {
            var home = StartBackground.HomeUrl(input);
            if (home == null || home == StartBackground.StartPage || HostOf(home) == null) return null;
            return home;
        }

        public static bool AddCustom(List<(string Url, string Title)> custom, ISet<string> hidden, string? input, out string error)
        {
            error = "";
            var url = NormalizeNew(input);
            if (url == null) { error = "that is not a web address"; return false; }
            var host = HostOf(url)!;
            if (custom.Any(c => HostOf(c.Url) == host)) { hidden.Remove(host); return true; }   // already a tile: just make sure it shows
            if (custom.Count >= MaxCustom) { error = "you can add up to " + MaxCustom + " sites"; return false; }
            custom.Add((url, host)); hidden.Remove(host);
            return true;
        }

        // Removing a tile you added forgets it; removing a most-visited tile hides that site.
        public static void Remove(List<(string Url, string Title)> custom, ISet<string> hidden, string? host)
        {
            var h = (host ?? "").Trim().ToLowerInvariant();
            if (!HostRx.IsMatch(h)) return;
            int n = custom.RemoveAll(c => HostOf(c.Url) == h);
            if (n == 0 && hidden.Count < MaxHidden) hidden.Add(h);
        }

        // Tiles you added come first, then most-visited sites that are not hidden or already shown.
        public static List<Tile> Build(IEnumerable<(string Url, string Title)>? custom, IEnumerable<TopSite>? top, ISet<string>? hidden, int max = MaxTiles)
        {
            var res = new List<Tile>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in custom ?? Array.Empty<(string, string)>())
            {
                var h = HostOf(c.Url); if (h == null || !seen.Add(h) || res.Count >= max) continue;
                res.Add(new Tile(c.Url, h, string.IsNullOrWhiteSpace(c.Title) ? h : c.Title, true, 0));
            }
            foreach (var t in top ?? Array.Empty<TopSite>())
            {
                if (res.Count >= max) break;
                if ((hidden != null && hidden.Contains(t.Host)) || !seen.Add(t.Host)) continue;
                res.Add(new Tile(t.Url, t.Host, t.Title, false, t.Visits));
            }
            return res;
        }

        public static string ToJson(List<(string Url, string Title)> custom, ISet<string> hidden) => JsonSerializer.Serialize(new
        {
            custom = custom.Select(c => new { url = c.Url, title = c.Title }).ToList(),
            hidden = hidden.OrderBy(x => x, StringComparer.Ordinal).ToList()
        });

        // Reads {"custom":[{url,title}],"hidden":[host]} (also the pieces of a sync file). Invalid entries are dropped.
        public static void Load(JsonElement? custom, JsonElement? hidden, List<(string Url, string Title)> into, ISet<string> hiddenInto)
        {
            if (custom is { ValueKind: JsonValueKind.Array } ca)
                foreach (var e in ca.EnumerateArray())
                {
                    if (into.Count >= MaxCustom || e.ValueKind != JsonValueKind.Object) continue;
                    var url = LaunchArgs.Parse(e.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null);
                    var h = HostOf(url); if (h == null || into.Any(x => HostOf(x.Url) == h)) continue;
                    var title = SetupValidate.CleanTitle(e.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : "");
                    into.Add((url!, title.Length == 0 ? h : title));
                }
            if (hidden is { ValueKind: JsonValueKind.Array } ha)
                foreach (var e in ha.EnumerateArray())
                {
                    if (hiddenInto.Count >= MaxHidden || e.ValueKind != JsonValueKind.String) continue;
                    var h = (e.GetString() ?? "").Trim().ToLowerInvariant(); if (HostRx.IsMatch(h)) hiddenInto.Add(h);
                }
        }
    }

    internal static class TranslateRules
    {
        public static readonly (string Code, string Name)[] Languages =
        {
            ("en", "English"), ("es", "Spanish"), ("fr", "French"), ("de", "German"), ("it", "Italian"), ("pt", "Portuguese"), ("nl", "Dutch"),
            ("sv", "Swedish"), ("pl", "Polish"), ("tr", "Turkish"), ("ru", "Russian"), ("uk", "Ukrainian"), ("ar", "Arabic"), ("hi", "Hindi"),
            ("zh-CN", "Chinese (Simplified)"), ("ja", "Japanese"), ("ko", "Korean")
        };
        public static readonly (string Id, string Name, string Host)[] Services = { ("google", "Google Translate", "translate.google.com"), ("bing", "Microsoft Translator", "www.translatetheweb.com") };

        public static string NormalizeLang(string? code) => Languages.FirstOrDefault(l => string.Equals(l.Code, (code ?? "").Trim(), StringComparison.OrdinalIgnoreCase)).Code ?? "en";
        public static string NormalizeService(string? id) => Services.FirstOrDefault(s => s.Id == (id ?? "").Trim().ToLowerInvariant()).Id ?? "google";
        public static string ServiceName(string? id) => Services.First(s => s.Id == NormalizeService(id)).Name;
        public static string ServiceHost(string? id) => Services.First(s => s.Id == NormalizeService(id)).Host;
        public static string LangName(string? code) => Languages.First(l => l.Code == NormalizeLang(code)).Name;

        // The address that shows `pageUrl` translated into `lang`. Only http and https pages; null otherwise. Opens the page in the chosen
        // service, which therefore sees that address; the caller asks before opening it.
        public static string? Url(string? service, string? lang, string? pageUrl)
        {
            var page = LaunchArgs.Parse(pageUrl);
            if (page == null) return null;
            var l = NormalizeLang(lang); var esc = Uri.EscapeDataString(page);
            return NormalizeService(service) == "bing"
                ? "https://www.translatetheweb.com/?from=&to=" + (l == "zh-CN" ? "zh-Hans" : l) + "&a=" + esc
                : "https://translate.google.com/translate?sl=auto&tl=" + l + "&u=" + esc;
        }
    }

    internal sealed record SyncData(List<(string Url, string Title)> Bookmarks, List<(string Url, string Title)> Tiles, List<string> Hidden, string? Home, string? Engine, DateTime PrefsUtc, string Device);

    internal static class SyncRules
    {
        public const string FileName = "recognition-sync.v1.rsetup";
        public const int MaxBookmarks = 20000;

        public static Dictionary<string, object> Categories(IEnumerable<(string Url, string Title)> bookmarks, List<(string Url, string Title)> tiles, ISet<string> hidden,
                                                             string home, string engine, DateTime prefsUtc, string device) => new()
        {
            ["bookmarks"] = bookmarks.Take(MaxBookmarks).Select(b => new { url = b.Url, title = b.Title }).ToList(),
            ["sync"] = new
            {
                tiles = tiles.Select(t => new { url = t.Url, title = t.Title }).ToList(),
                hidden = hidden.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                home_url = home, search_engine = engine, prefs_utc = prefsUtc.ToUniversalTime().ToString("o"), device
            }
        };

        // null when the payload has no sync part. Every field is re-validated.
        public static SyncData? Parse(string? payloadJson)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(payloadJson)) return null;
                using var doc = JsonDocument.Parse(payloadJson);
                if (!doc.RootElement.TryGetProperty("categories", out var cats) || cats.ValueKind != JsonValueKind.Object) return null;
                var bms = new List<(string, string)>(); var seen = new HashSet<string>(StringComparer.Ordinal);
                if (cats.TryGetProperty("bookmarks", out var b) && b.ValueKind == JsonValueKind.Array)
                    foreach (var e in b.EnumerateArray())
                    {
                        if (bms.Count >= MaxBookmarks || e.ValueKind != JsonValueKind.Object) continue;
                        var url = LaunchArgs.Parse(e.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null);
                        if (url == null || url.Length > 2048 || !SetupValidate.IsSafeUrl(url) || !seen.Add(url)) continue;   // no user names in addresses
                        bms.Add((url, SetupValidate.CleanTitle(e.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : "")));
                    }
                if (!cats.TryGetProperty("sync", out var s) || s.ValueKind != JsonValueKind.Object) return null;
                var tiles = new List<(string, string)>(); var hidden = new HashSet<string>(StringComparer.Ordinal);
                TileRules.Load(s.TryGetProperty("tiles", out var tl) ? tl : (JsonElement?)null, s.TryGetProperty("hidden", out var hd) ? hd : (JsonElement?)null, tiles, hidden);
                string? home = s.TryGetProperty("home_url", out var hu) && hu.ValueKind == JsonValueKind.String ? StartBackground.HomeUrl(hu.GetString()) : null;
                string? engine = s.TryGetProperty("search_engine", out var se) && se.ValueKind == JsonValueKind.String ? SearchEngines.Normalize(se.GetString()) : null;
                var prefs = DateTime.MinValue;
                if (s.TryGetProperty("prefs_utc", out var pu) && pu.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(pu.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var pd)) prefs = pd.ToUniversalTime();
                var device = new string((s.TryGetProperty("device", out var dv) && dv.ValueKind == JsonValueKind.String ? dv.GetString() ?? "" : "").Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ' ').ToArray());
                if (device.Length > 40) device = device.Substring(0, 40);
                return new SyncData(bms, tiles, hidden.OrderBy(x => x, StringComparer.Ordinal).ToList(), home, engine, prefs, device);
            }
            catch { return null; }
        }

        // Union by address: nothing you have is lost, and what the other device has is added. (A bookmark removed on one device comes back
        // from the other; sync never deletes.)
        public static List<(string Url, string Title)> MergeByUrl(IEnumerable<(string Url, string Title)> local, IEnumerable<(string Url, string Title)> remote, out int added)
        {
            var res = local.ToList(); var seen = new HashSet<string>(res.Select(x => x.Url), StringComparer.Ordinal); added = 0;
            foreach (var r in remote) if (res.Count < MaxBookmarks && seen.Add(r.Url)) { res.Add(r); added++; }
            return res;
        }

        // the later change wins for settings
        public static bool RemoteIsNewer(DateTime localUtc, DateTime remoteUtc) => remoteUtc > localUtc;
    }

    // Bookmark folders: a bookmark is in at most one folder, and a folder exists while it holds a bookmark. Names are plain text.
    internal static class BookmarkFolders
    {
        public const int MaxFolders = 50, MaxName = 30;

        public static string Clean(string? name)
        {
            var s = new string((name ?? "").Where(c => !char.IsControl(c) && c != '<' && c != '>' && c != '"' && c != '\'' && c != '&' && c != '\\').ToArray());
            s = string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return s.Length > MaxName ? s.Substring(0, MaxName).TrimEnd() : s;
        }

        // the folders in use, each once (case-insensitive), in alphabetical order
        public static List<string> Names(IEnumerable<string?>? folders)
        {
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in folders ?? Array.Empty<string?>()) { var c = Clean(f); if (c.Length > 0 && !seen.ContainsKey(c)) seen[c] = c; }
            return seen.Values.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // the name to store: an existing folder's spelling if there is one, otherwise the cleaned name; null if it cannot be used
        public static string? Resolve(IEnumerable<string?>? existingInUse, string? wanted)
        {
            var c = Clean(wanted); if (c.Length == 0) return null;
            var names = Names(existingInUse);
            var hit = names.FirstOrDefault(n => string.Equals(n, c, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
            return names.Count >= MaxFolders ? null : c;
        }
    }
}
