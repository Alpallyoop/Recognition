using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Recognition.Browser
{
    // Pure rules for themes and for finding extensions (compiled into browser.tests too; no WPF, files or network).
    //  * ThemePacks: the built-in looks, and reading the colours (and start-page picture name) out of a Chrome theme.
    //  * ChromeColors: from three colours (bar, text, accent) to every colour the window uses, for dark and light bars alike.
    //  * ExtSources: turning a Chrome Web Store or Edge Add-ons link (or a bare extension id) into the official download address.
    // Every colour that leaves this file is a validated #rrggbb value, so nothing a theme file says can reach the window as anything else.

    internal sealed record ThemePack(string Id, string Name, string Bar, string Text, string Accent, string StartBg, bool Dark, string? NtpImage = null, string Top = "");

    internal static class Rgb
    {
        private static readonly Regex Rx = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);
        public static bool Valid(string? s) => s != null && Rx.IsMatch(s);
        public static string Norm(string? s) => Valid(s) ? s!.ToLowerInvariant() : "";

        public static (int R, int G, int B) Parse(string hex) =>
            (int.Parse(hex.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
             int.Parse(hex.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
             int.Parse(hex.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

        public static string From(int r, int g, int b) =>
            "#" + Math.Clamp(r, 0, 255).ToString("x2", CultureInfo.InvariantCulture) + Math.Clamp(g, 0, 255).ToString("x2", CultureInfo.InvariantCulture) + Math.Clamp(b, 0, 255).ToString("x2", CultureInfo.InvariantCulture);

        // 0 (black) .. 1 (white), by the usual weights
        public static double Luminance(string hex)
        {
            var (r, g, b) = Parse(hex);
            return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
        }

        // a toward b by t (0..1)
        public static string Mix(string a, string b, double t)
        {
            var (ar, ag, ab) = Parse(a); var (br, bg, bb) = Parse(b);
            t = Math.Clamp(t, 0, 1);
            return From((int)Math.Round(ar + (br - ar) * t), (int)Math.Round(ag + (bg - ag) * t), (int)Math.Round(ab + (bb - ab) * t));
        }
    }

    internal static class ThemePacks
    {
        public const string DefaultId = "default";

        // The look Recognition starts with is pack "default": choosing it removes every override.
        public static readonly ThemePack[] Presets =
        {
            new("default",  "Recognition",   "#1b1e24", "#d6dae2", "#2b6cb0", "default", true),
            new("midnight", "Midnight",      "#161922", "#e6e8ee", "#5b8def", "slate",   true),
            new("aurora",   "Aurora",        "#1a2430", "#e6f0f5", "#35c6b1", "aurora",  true),
            new("dusk",     "Dusk",          "#26203a", "#ece8f7", "#9b7bf0", "dusk",    true),
            new("forest",   "Forest",        "#18261f", "#e4f0e8", "#4fb477", "forest",  true),
            new("ember",    "Ember",         "#2a1f1c", "#f3e8e2", "#e8743b", "default", true),
            new("paper",    "Paper",         "#f1f3f6", "#1f2430", "#2b6cb0", "light",   false),
            new("sand",     "Sand",          "#efe7d8", "#2b2418", "#b7791f", "sand",    false),
            new("graphite", "Graphite",      "#202226", "#dcdfe4", "#8a9ba8", "graphite", true),
            new("ink",      "Ink",           "#0a0b0d", "#e4e6ea", "#6aa9e9", "midnight", true),
            new("ocean",    "Ocean",         "#10202e", "#e2eef7", "#2f9de0", "ocean",   true),
            new("sunset",   "Sunset",        "#2b1d33", "#f6e9ee", "#f2784b", "sunset",  true),
            new("nebula",   "Nebula",        "#1a1428", "#ece6f8", "#b266f0", "nebula",  true),
            new("rose",     "Rose",          "#2a1a21", "#f6e6ec", "#e5527f", "rose",    true),
            new("lava",     "Lava",          "#241512", "#f6e7e2", "#ff6a2f", "lava",    true),
            new("nord",     "Nord",          "#2e3440", "#e5e9f0", "#88c0d0", "slate",   true),
            new("solar-dark", "Solar dark",  "#073642", "#d6e0e0", "#2aa198", "default", true),
            new("mint",     "Mint",          "#e6f4ec", "#16301f", "#1f9d6e", "mint",    false),
            new("ice",      "Ice",           "#e4eef8", "#13263a", "#2b78c2", "ice",     false),
            new("snow",     "Snow",          "#fafbfc", "#1d2430", "#3b82f6", "paper",   false),
            new("rosewater", "Rosewater",    "#f8e9ee", "#3a1a26", "#d9486f", "light",   false),
            new("solar-light", "Solar light", "#fdf6e3", "#2a3a3d", "#268bd2", "sand",   false),
        };

        public static ThemePack? Find(string? id) => Presets.FirstOrDefault(p => string.Equals(p.Id, (id ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

        // A pack made from a bar colour, a text colour and an accent you chose yourself.
        public static ThemePack? Custom(string? bar, string? text, string? accent, string startBg)
        {
            if (!Rgb.Valid(bar) || !Rgb.Valid(text) || !Rgb.Valid(accent)) return null;
            var b = Rgb.Norm(bar);
            return new ThemePack("custom", "Custom", b, Rgb.Norm(text), Rgb.Norm(accent), startBg, Rgb.Luminance(b) < 0.5);
        }

        // Colours in a Chrome theme are [r,g,b] or [r,g,b,a] with 0..255 (alpha 0..1); a float anywhere makes it invalid.
        private static string? Color(JsonElement colors, string key)
        {
            if (colors.ValueKind != JsonValueKind.Object || !colors.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array) return null;
            var parts = v.EnumerateArray().ToList();
            if (parts.Count < 3 || parts.Count > 4) return null;
            var n = new int[3];
            for (int i = 0; i < 3; i++)
            {
                if (parts[i].ValueKind != JsonValueKind.Number || !parts[i].TryGetInt32(out var x) || x < 0 || x > 255) return null;
                n[i] = x;
            }
            return Rgb.From(n[0], n[1], n[2]);
        }

        // null when the manifest is not a theme (no "theme" object) or has no usable colour at all.
        public static ThemePack? FromChromeTheme(string? manifestJson, string fallbackName = "Imported theme")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(manifestJson) || manifestJson.Length > 1_000_000) return null;
                using var doc = JsonDocument.Parse(manifestJson, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("theme", out var th) || th.ValueKind != JsonValueKind.Object) return null;
                th.TryGetProperty("colors", out var colors);
                var bar = Color(colors, "toolbar") ?? Color(colors, "frame") ?? Color(colors, "ntp_background");
                string? ntpImage = null;
                if (th.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Object && imgs.TryGetProperty("theme_ntp_background", out var ni) && ni.ValueKind == JsonValueKind.String)
                    ntpImage = SafeImagePath(ni.GetString());
                if (bar == null && ntpImage == null) return null;
                bar ??= "#1b1e24";
                bool dark = Rgb.Luminance(bar) < 0.5;
                var text = Color(colors, "toolbar_text") ?? Color(colors, "bookmark_text") ?? Color(colors, "tab_text") ?? (dark ? "#e6e8ee" : "#1f2430");
                // text that cannot be read on the bar is replaced
                if (Math.Abs(Rgb.Luminance(text) - Rgb.Luminance(bar)) < 0.3) text = dark ? "#e6e8ee" : "#1f2430";
                var accent = Color(colors, "button_background") ?? Color(colors, "ntp_link") ?? "#2b6cb0";
                var start = Color(colors, "ntp_background") ?? "default";
                string name = fallbackName;
                if (r.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String) { var s = new string((nm.GetString() ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim(); if (s.Length > 0) name = s.Length > 40 ? s.Substring(0, 40) : s; }
                return new ThemePack("imported", name, bar, text, accent, start, dark, ntpImage);
            }
            catch { return null; }
        }

        // a relative path inside the theme folder, forward or back slashes, no drive, no "..", a picture extension
        public static string? SafeImagePath(string? p)
        {
            if (string.IsNullOrWhiteSpace(p) || p.Length > 200) return null;
            var s = p.Replace('\\', '/').Trim();
            if (s.StartsWith("/") || s.Contains(':') || s.Split('/').Any(x => x == ".." || x == "." || x.Length == 0 || x.Any(char.IsControl))) return null;
            var ext = s.Substring(s.LastIndexOf('.') + 1).ToLowerInvariant();
            return s.LastIndexOf('.') > 0 && (ext is "png" or "jpg" or "jpeg" or "gif" or "bmp") ? s : null;
        }
    }

    // Every colour the window chrome uses, from the three the person chose.
    internal static class ChromeColors
    {
        public static readonly string[] Keys =
            { "Win", "Bar", "Side", "Field", "Line", "Line2", "Hover", "Tab", "TabSel", "TabHover", "Text", "Muted", "Accent", "AccentHover", "MenuBg", "MenuSel" };

        public static Dictionary<string, string> Derive(string bar, string text, string accent)
        {
            bar = Rgb.Valid(bar) ? Rgb.Norm(bar) : "#1b1e24";
            text = Rgb.Valid(text) ? Rgb.Norm(text) : "#d6dae2";
            accent = Rgb.Valid(accent) ? Rgb.Norm(accent) : "#2b6cb0";
            bool dark = Rgb.Luminance(bar) < 0.5;
            const string black = "#000000", white = "#ffffff";
            var d = new Dictionary<string, string>();
            if (dark)
            {
                d["Win"] = Rgb.Mix(bar, black, .40); d["Side"] = Rgb.Mix(bar, black, .22); d["Field"] = Rgb.Mix(bar, black, .35);
                d["Line"] = Rgb.Mix(bar, white, .10); d["Line2"] = Rgb.Mix(bar, white, .14); d["Hover"] = Rgb.Mix(bar, white, .09);
                d["Tab"] = Rgb.Mix(bar, white, .04); d["TabSel"] = Rgb.Mix(bar, white, .12); d["TabHover"] = Rgb.Mix(bar, white, .08);
                d["MenuBg"] = Rgb.Mix(bar, black, .25); d["MenuSel"] = Rgb.Mix(accent, black, .45);
            }
            else
            {
                d["Win"] = Rgb.Mix(bar, black, .08); d["Side"] = Rgb.Mix(bar, black, .04); d["Field"] = Rgb.Mix(bar, white, .70);
                d["Line"] = Rgb.Mix(bar, black, .14); d["Line2"] = Rgb.Mix(bar, black, .20); d["Hover"] = Rgb.Mix(bar, black, .08);
                d["Tab"] = Rgb.Mix(bar, black, .05); d["TabSel"] = Rgb.Mix(bar, white, .55); d["TabHover"] = Rgb.Mix(bar, black, .09);
                d["MenuBg"] = Rgb.Mix(bar, white, .45); d["MenuSel"] = Rgb.Mix(accent, white, .65);
            }
            d["Bar"] = bar; d["Text"] = text; d["Accent"] = accent; d["AccentHover"] = Rgb.Mix(accent, white, .14);
            d["Muted"] = Rgb.Mix(text, bar, .38);
            return d;
        }
    }

    // Finding extensions: a store link or a bare id becomes the store's own download address. The download itself is the governed one
    // (https only, size limit, review before install); this only builds the address.
    internal static class ExtSources
    {
        private static readonly Regex IdRx = new("^[a-p]{32}$", RegexOptions.Compiled);

        public sealed record Source(string Store, string Id);

        public static bool IsId(string? s) => s != null && IdRx.IsMatch(s);

        // chrome: chromewebstore.google.com/detail/<name>/<id> or chrome.google.com/webstore/detail/<name>/<id>
        // edge:   microsoftedge.microsoft.com/addons/detail/<name>/<id>
        // a bare 32-letter id is taken as a Chrome Web Store id
        public static Source? Parse(string? input)
        {
            var s = (input ?? "").Trim();
            if (s.Length == 0 || s.Length > 500 || s.Any(char.IsControl)) return null;
            if (IsId(s)) return new Source("chrome", s);
            if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || u.UserInfo.Length > 0) return null;
            var host = u.Host.ToLowerInvariant();
            string? store = host switch
            {
                "chromewebstore.google.com" or "chrome.google.com" => "chrome",
                "microsoftedge.microsoft.com" => "edge",
                _ => null
            };
            if (store == null) return null;
            var path = u.AbsolutePath;
            if (store == "chrome" && !(path.StartsWith("/detail/", StringComparison.Ordinal) || path.StartsWith("/webstore/detail/", StringComparison.Ordinal))) return null;
            if (store == "edge" && !path.StartsWith("/addons/detail/", StringComparison.Ordinal)) return null;
            var last = path.TrimEnd('/').Split('/').LastOrDefault() ?? "";
            return IsId(last) ? new Source(store, last) : null;
        }

        // Firefox Add-ons (addons.mozilla.org/<language>/firefox/addon/<name>/): the add-on's name in the address (its "slug")
        private static readonly Regex SlugRx = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.Compiled);
        public static string? ParseFirefox(string? input)
        {
            var s = (input ?? "").Trim();
            if (s.Length == 0 || s.Length > 500 || s.Any(char.IsControl)) return null;
            if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || u.UserInfo.Length > 0 || !u.Host.Equals("addons.mozilla.org", StringComparison.OrdinalIgnoreCase)) return null;
            var parts = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int i = Array.FindIndex(parts, p => p == "addon");
            if (i < 1 || parts[i - 1] != "firefox" || i + 1 >= parts.Length) return null;
            return SlugRx.IsMatch(parts[i + 1]) ? parts[i + 1] : null;
        }

        public static string FirefoxApiUrl(string slug) => "https://addons.mozilla.org/api/v5/addons/addon/" + Uri.EscapeDataString(slug) + "/";

        // the add-on file address inside Mozilla's answer; only Mozilla's own https hosts are accepted
        public static string? FirefoxFileUrl(string? apiJson)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(apiJson) || apiJson.Length > 2_000_000) return null;
                using var d = JsonDocument.Parse(apiJson);
                if (!d.RootElement.TryGetProperty("current_version", out var cv) || !cv.TryGetProperty("file", out var f) || !f.TryGetProperty("url", out var u) || u.ValueKind != JsonValueKind.String) return null;
                var url = u.GetString() ?? "";
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0) return null;
                var h = uri.Host.ToLowerInvariant();
                return h == "addons.mozilla.org" || h == "addons.cdn.mozilla.net" ? uri.AbsoluteUri : null;
            }
            catch { return null; }
        }

        public static string Url(Source s, string engineMajor)
        {
            var major = new string((engineMajor ?? "").Where(char.IsAsciiDigit).Take(4).ToArray());
            if (major.Length == 0) major = "120";
            var x = "id%3D" + s.Id + "%26installsource%3Dondemand%26uc";
            return s.Store == "edge"
                ? "https://edge.microsoft.com/extensionwebstorebase/v1/crx?response=redirect&prodversion=" + major + "&x=" + x
                : "https://clients2.google.com/service/update2/crx?response=redirect&prodversion=" + major + "&acceptformat=crx2,crx3&x=" + x;
        }
    }

    // Where the window was when it was closed, so it opens the same way next time (size, place, maximized).
    internal sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized)
    {
        public const double MinWidth = 640, MinHeight = 420;

        public static WindowPlacement? Parse(string? json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json) || json.Length > 2000) return null;
                using var d = JsonDocument.Parse(json);
                var r = d.RootElement;
                if (r.ValueKind != JsonValueKind.Object) return null;
                double N(string k) { if (!r.TryGetProperty(k, out var v) || v.ValueKind != JsonValueKind.Number) throw new FormatException(); var x = v.GetDouble(); if (double.IsNaN(x) || double.IsInfinity(x) || Math.Abs(x) > 100000) throw new FormatException(); return x; }
                bool max = r.TryGetProperty("maximized", out var m) && m.ValueKind == JsonValueKind.True;
                return new WindowPlacement(N("left"), N("top"), N("width"), N("height"), max);
            }
            catch { return null; }
        }

        public string ToJson() => string.Create(CultureInfo.InvariantCulture, $"{{\"left\":{Left:0.##},\"top\":{Top:0.##},\"width\":{Width:0.##},\"height\":{Height:0.##},\"maximized\":{(Maximized ? "true" : "false")}}}");

        // Make the saved place usable on today's screens: never smaller than the minimum or bigger than the screens, and its
        // title bar must be reachable; otherwise it is centred on the first screen area given (a monitor may have been unplugged).
        public WindowPlacement Fit(IReadOnlyList<(double L, double T, double W, double H)> areas)
        {
            if (areas == null || areas.Count == 0) return this;
            var main = areas[0];
            double bigW = areas.Max(a => a.W), bigH = areas.Max(a => a.H);
            double w = Math.Min(Math.Max(Width, MinWidth), Math.Max(bigW, MinWidth)), h = Math.Min(Math.Max(Height, MinHeight), Math.Max(bigH, MinHeight));
            // the strip along the top (at least 120 wide, 40 tall) has to overlap some screen area
            bool reachable = areas.Any(a => Left + w - 120 > a.L && Left + 120 < a.L + a.W && Top >= a.T - 1 && Top + 40 < a.T + a.H);
            if (!reachable) return new WindowPlacement(main.L + Math.Max(0, (main.W - w) / 2), main.T + Math.Max(0, (main.H - h) / 2), w, h, Maximized);
            return new WindowPlacement(Left, Top, w, h, Maximized);
        }
    }
}
