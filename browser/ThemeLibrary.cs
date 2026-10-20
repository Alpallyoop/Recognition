using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Recognition.Browser
{
    // Pure rules (no WPF, files or network; executed by browser.tests) for:
    //  * TopBarStyles: finishes for the top area (toolbar, address field, tabs, bookmarks bar) that sit on top of any theme.
    //  * ThemeLibrary: reading theme packs (one theme as a small JSON file) and the downloadable library of them.
    // A pack can only carry three colours, a start page background NAME (or a #rrggbb colour) and a top bar style NAME. Everything is
    // validated here, so a pack can never bring script, CSS, pictures or addresses into the window.

    internal sealed record TopBarStyle(string Id, string Name, string? Tint, double Amount, string? Accent, string Field);

    internal static class TopBarStyles
    {
        public const string MatchId = "match";

        public static readonly TopBarStyle[] All =
        {
            new("match",    "Match theme", null,      0,    null,      "auto"),
            new("ocean",    "Ocean",       "#1e78c8", 0.30, "#3aa0ee", "contrast"),
            new("violet",   "Violet",      "#7b5be0", 0.28, "#9b7bf0", "auto"),
            new("emerald",  "Emerald",     "#1f9d6e", 0.28, "#35c68d", "auto"),
            new("teal",     "Teal",        "#1aa6a6", 0.28, "#2fd0cf", "auto"),
            new("rose",     "Rose",        "#d9486f", 0.26, "#ef6b92", "auto"),
            new("crimson",  "Crimson",     "#c0392b", 0.28, "#e0584a", "auto"),
            new("amber",    "Amber",       "#e0902b", 0.26, "#f0a43c", "auto"),
            new("mono",     "Mono",        "#808080", 0.12, null,      "flat"),
            new("frosted",  "Frosted",     null,      0,    null,      "contrast"),
            new("deep",     "Deep",        null,      0,    null,      "deep"),
            new("flat",     "Flat",        null,      0,    null,      "flat"),
        };

        public static TopBarStyle? Find(string? id) => All.FirstOrDefault(s => string.Equals(s.Id, (id ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        public static string Normalize(string? id) { var s = Find(id); return s == null || s.Id == MatchId ? "" : s.Id; }

        // Changes only the colours of the top area in a colour table made by ChromeColors.Derive (or the defaults). The text colour and the
        // light or dark feel of the window never change, so everything stays readable.
        public static void Apply(Dictionary<string, string> c, string? id)
        {
            var s = Find(id);
            if (s == null || s.Id == MatchId || !c.TryGetValue("Bar", out var bar0) || !c.TryGetValue("Text", out var text) || !c.TryGetValue("Accent", out var acc0)) return;
            bool dark = Rgb.Luminance(bar0) < 0.5;
            var bar = bar0;
            if (s.Tint != null)
            {
                double amt = s.Amount;
                bar = Rgb.Mix(bar0, s.Tint, amt);
                for (int i = 0; i < 4 && (Rgb.Luminance(bar) < 0.5) != dark; i++) { amt /= 2; bar = Rgb.Mix(bar0, s.Tint, amt); }
                if ((Rgb.Luminance(bar) < 0.5) != dark) bar = bar0;
            }
            var accent = s.Accent ?? acc0;
            var d = ChromeColors.Derive(bar, text, accent);
            c["Bar"] = bar; c["Tab"] = d["Tab"]; c["TabSel"] = d["TabSel"]; c["TabHover"] = d["TabHover"]; c["Hover"] = d["Hover"];
            c["Accent"] = accent; c["AccentHover"] = d["AccentHover"];
            const string black = "#000000", white = "#ffffff";
            c["Field"] = s.Field switch
            {
                "flat" => Rgb.Mix(bar, dark ? black : white, dark ? 0.12 : 0.10),
                "contrast" => dark ? Rgb.Mix(bar, white, 0.14) : Rgb.Mix(bar, white, 0.85),
                "deep" => dark ? Rgb.Mix(bar, black, 0.55) : Rgb.Mix(bar, white, 0.92),
                _ => d["Field"]
            };
        }
    }

    internal static class ThemeLibrary
    {
        public const string PackSchema = "recognition.theme.v1";
        public const string LibrarySchema = "recognition.theme.library.v1";
        public const string RemoteUrl = "https://raw.githubusercontent.com/Alpallyoop/Recognition/main/themes/library.v1.json";
        public const int MaxPackBytes = 20_000, MaxLibraryBytes = 400_000, MaxPacks = 60;

        private static readonly Regex IdRx = new("^[a-z0-9][a-z0-9-]{0,30}$", RegexOptions.Compiled);

        public static bool IsAllowedHost(Uri? u) => u != null && u.Scheme == Uri.UriSchemeHttps && u.UserInfo.Length == 0 && u.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase);

        public static string CleanName(string? s)
        {
            var t = new string((s ?? "").Where(c => !char.IsControl(c) && c != '<' && c != '>' && c != '&' && c != '"' && c != '\'' && c != '\\').ToArray()).Trim();
            return t.Length > 40 ? t.Substring(0, 40).Trim() : t;
        }

        public static bool ValidId(string? id) => id != null && IdRx.IsMatch(id) && ThemePacks.Find(id) == null;   // a pack cannot take a built-in look's id

        public static ThemePack? ParsePack(JsonElement e)
        {
            try
            {
                if (e.ValueKind != JsonValueKind.Object) return null;
                string? S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var id = (S("id") ?? "").Trim().ToLowerInvariant();
                var name = CleanName(S("name"));
                var bar = S("bar"); var text = S("text"); var accent = S("accent");
                if (!ValidId(id) || name.Length == 0 || !Rgb.Valid(bar) || !Rgb.Valid(text) || !Rgb.Valid(accent)) return null;
                var b = Rgb.Norm(bar); var t = Rgb.Norm(text);
                if (Math.Abs(Rgb.Luminance(t) - Rgb.Luminance(b)) < 0.3) return null;   // text that cannot be read on the bar
                var start = StartBackground.Normalize(S("start_bg") ?? "default");
                if (start == "image") start = "default";
                return new ThemePack(id, name, b, t, Rgb.Norm(accent), start, Rgb.Luminance(b) < 0.5, null, TopBarStyles.Normalize(S("top")));
            }
            catch { return null; }
        }

        public static ThemePack? ParsePack(string? json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json) || json.Length > MaxPackBytes) return null;
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("schema", out var sc) || sc.GetString() != PackSchema) return null;
                return ParsePack(r);
            }
            catch { return null; }
        }

        public static List<ThemePack> ParseLibrary(string? json)
        {
            var list = new List<ThemePack>();
            try
            {
                if (string.IsNullOrWhiteSpace(json) || json.Length > MaxLibraryBytes) return list;
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("schema", out var sc) || sc.GetString() != LibrarySchema) return list;
                if (!r.TryGetProperty("themes", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var e in arr.EnumerateArray())
                {
                    if (list.Count >= MaxPacks) break;
                    var p = ParsePack(e);
                    if (p != null && seen.Add(p.Id)) list.Add(p);
                }
            }
            catch { list.Clear(); }
            return list;
        }

        // The file kept for an added theme (UTF-8, LF, fixed key order).
        public static string ToJson(ThemePack p)
        {
            string J(string s) => JsonSerializer.Serialize(s);
            return "{\"schema\":" + J(PackSchema) + ",\"id\":" + J(p.Id) + ",\"name\":" + J(p.Name) + ",\"bar\":" + J(p.Bar) + ",\"text\":" + J(p.Text) + ",\"accent\":" + J(p.Accent) +
                   ",\"start_bg\":" + J(p.StartBg) + ",\"top\":" + J(p.Top) + "}\n";
        }
    }
}
