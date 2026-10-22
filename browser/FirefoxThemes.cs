using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Recognition.Browser
{
    // Firefox themes (addons.mozilla.org "Themes": static themes in an .xpi, including the animated ones). Pure: no WPF, files or network;
    // compiled into browser.tests and stress.tests.
    //
    // A Firefox theme is a manifest.json with a "theme" object, like a Chrome theme, but:
    //   * colours are CSS strings ("#0b1a2c", "#fff8", "rgb(1,2,3)", "rgba(0,0,0,0.4)", "hsl(...)", "white") or [r,g,b(,a)] arrays;
    //   * its main picture is a HEADER image (images.theme_frame, legacy headerURL) drawn across the top of the window from the right,
    //     with optional images.additional_backgrounds layered in front of it (properties.additional_backgrounds_alignment / _tiling);
    //   * "animated" themes use an animated PNG or GIF as that header (decoded by AnimatedImage.cs).
    // Mapping to Recognition: bar = toolbar over frame, text = toolbar_text, accent = tab_line (or a focus/attention colour), start page
    // background = ntp_background, and the header layers become ThemeHeader. Every colour that leaves this file is #rrggbb; every
    // picture path is a relative path inside the package (ThemePacks.SafeImagePath). Anything not understood is ignored, never guessed.
    internal sealed record HeaderLayer(string Path, string AlignX, string AlignY, string Tile);

    // FrameColor: under the header picture. ToolbarOpacity: 0..100, how opaque the toolbar is drawn over the picture.
    internal sealed record ThemeHeader(string FrameColor, int ToolbarOpacity, IReadOnlyList<HeaderLayer> Layers)
    {
        public bool Equals(ThemeHeader? o) => o != null && FrameColor == o.FrameColor && ToolbarOpacity == o.ToolbarOpacity && Layers.SequenceEqual(o.Layers);
        public override int GetHashCode() => HashCode.Combine(FrameColor, ToolbarOpacity, Layers.Count);
    }

    internal static class CssColor
    {
        // The CSS named colours themes actually use; an unknown name is not guessed (the colour is ignored).
        private static readonly Dictionary<string, string> Named = new(StringComparer.OrdinalIgnoreCase)
        {
            ["black"] = "#000000", ["white"] = "#ffffff", ["red"] = "#ff0000", ["green"] = "#008000", ["lime"] = "#00ff00", ["blue"] = "#0000ff",
            ["yellow"] = "#ffff00", ["cyan"] = "#00ffff", ["aqua"] = "#00ffff", ["magenta"] = "#ff00ff", ["fuchsia"] = "#ff00ff", ["gray"] = "#808080",
            ["grey"] = "#808080", ["silver"] = "#c0c0c0", ["maroon"] = "#800000", ["olive"] = "#808000", ["navy"] = "#000080", ["purple"] = "#800080",
            ["teal"] = "#008080", ["orange"] = "#ffa500", ["pink"] = "#ffc0cb", ["gold"] = "#ffd700", ["indigo"] = "#4b0082", ["violet"] = "#ee82ee",
            ["brown"] = "#a52a2a", ["crimson"] = "#dc143c", ["darkgray"] = "#a9a9a9", ["darkgrey"] = "#a9a9a9", ["lightgray"] = "#d3d3d3",
            ["lightgrey"] = "#d3d3d3", ["dimgray"] = "#696969", ["dimgrey"] = "#696969", ["darkblue"] = "#00008b", ["darkred"] = "#8b0000",
            ["darkgreen"] = "#006400", ["darkslategray"] = "#2f4f4f", ["darkslategrey"] = "#2f4f4f", ["midnightblue"] = "#191970",
            ["rebeccapurple"] = "#663399", ["whitesmoke"] = "#f5f5f5", ["gainsboro"] = "#dcdcdc", ["ivory"] = "#fffff0", ["beige"] = "#f5f5dc",
            ["coral"] = "#ff7f50", ["tomato"] = "#ff6347", ["salmon"] = "#fa8072", ["orchid"] = "#da70d6", ["plum"] = "#dda0dd", ["khaki"] = "#f0e68c",
            ["turquoise"] = "#40e0d0", ["skyblue"] = "#87ceeb", ["steelblue"] = "#4682b4", ["royalblue"] = "#4169e1", ["slategray"] = "#708090",
            ["slategrey"] = "#708090", ["chocolate"] = "#d2691e", ["tan"] = "#d2b48c", ["hotpink"] = "#ff69b4", ["deeppink"] = "#ff1493",
            ["darkorange"] = "#ff8c00", ["firebrick"] = "#b22222", ["seagreen"] = "#2e8b57", ["forestgreen"] = "#228b22", ["limegreen"] = "#32cd32",
            ["dodgerblue"] = "#1e90ff", ["deepskyblue"] = "#00bfff", ["darkviolet"] = "#9400d3", ["blueviolet"] = "#8a2be2", ["lavender"] = "#e6e6fa",
            ["transparent"] = "#000000",
        };

        // (#rrggbb, alpha 0..1) or null. Accepts a JSON string or a [r,g,b] / [r,g,b,a] array.
        public static (string Hex, double Alpha)? Parse(JsonElement v)
        {
            if (v.ValueKind == JsonValueKind.String) return Parse(v.GetString());
            if (v.ValueKind != JsonValueKind.Array) return null;
            var p = v.EnumerateArray().ToList();
            if (p.Count < 3 || p.Count > 4) return null;
            var n = new int[3];
            for (int i = 0; i < 3; i++) { if (p[i].ValueKind != JsonValueKind.Number || !p[i].TryGetDouble(out var d) || d < 0 || d > 255) return null; n[i] = (int)Math.Round(d); }
            double a = 1;
            if (p.Count == 4) { if (p[3].ValueKind != JsonValueKind.Number || !p[3].TryGetDouble(out a) || a < 0 || a > 1) return null; }
            return (Rgb.From(n[0], n[1], n[2]), a);
        }

        public static (string Hex, double Alpha)? Parse(string? s)
        {
            if (string.IsNullOrWhiteSpace(s) || s.Length > 64) return null;
            s = s.Trim().ToLowerInvariant();
            if (s == "transparent") return ("#000000", 0);
            if (Named.TryGetValue(s, out var nm)) return (nm, 1);
            if (s[0] == '#')
            {
                var h = s.Substring(1);
                if (!h.All(Uri.IsHexDigit)) return null;
                if (h.Length is 3 or 4) h = string.Concat(h.Select(c => new string(c, 2)));
                if (h.Length is not (6 or 8)) return null;
                double a = h.Length == 8 ? int.Parse(h.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0 : 1;
                return ("#" + h.Substring(0, 6), a);
            }
            int open = s.IndexOf('('), close = s.LastIndexOf(')');
            if (open < 3 || close != s.Length - 1) return null;
            var fn = s.Substring(0, open).Trim();
            var args = s.Substring(open + 1, close - open - 1).Replace("/", " / ").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Where(x => x != "/").ToArray();
            if (args.Length is < 3 or > 4) return null;
            double Num(string x, double scaleIfPercent, out bool ok)
            {
                ok = false;
                bool pct = x.EndsWith("%");
                if (!double.TryParse(pct ? x[..^1] : x, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || double.IsNaN(d) || double.IsInfinity(d)) return 0;
                ok = true; return pct ? d / 100.0 * scaleIfPercent : d;
            }
            double alpha = 1;
            if (args.Length == 4) { alpha = Num(args[3], 1, out var ok4); if (!ok4) return null; alpha = Math.Clamp(alpha, 0, 1); }
            if (fn is "rgb" or "rgba")
            {
                var c = new int[3];
                for (int i = 0; i < 3; i++) { var d = Num(args[i], 255, out var ok); if (!ok) return null; c[i] = (int)Math.Round(Math.Clamp(d, 0, 255)); }
                return (Rgb.From(c[0], c[1], c[2]), alpha);
            }
            if (fn is "hsl" or "hsla")
            {
                var hs = args[0].EndsWith("deg") ? args[0][..^3] : args[0];
                if (!double.TryParse(hs, NumberStyles.Float, CultureInfo.InvariantCulture, out var hue) || double.IsNaN(hue) || double.IsInfinity(hue)) return null;
                var sat = Num(args[1], 1, out var ok1); var lig = Num(args[2], 1, out var ok2);
                if (!ok1 || !ok2 || !args[1].EndsWith("%") || !args[2].EndsWith("%")) return null;
                sat = Math.Clamp(sat, 0, 1); lig = Math.Clamp(lig, 0, 1); hue = ((hue % 360) + 360) % 360;
                double C = (1 - Math.Abs(2 * lig - 1)) * sat, X = C * (1 - Math.Abs(hue / 60 % 2 - 1)), m = lig - C / 2;
                var (r, g, b) = hue < 60 ? (C, X, 0.0) : hue < 120 ? (X, C, 0.0) : hue < 180 ? (0.0, C, X) : hue < 240 ? (0.0, X, C) : hue < 300 ? (X, 0.0, C) : (C, 0.0, X);
                return (Rgb.From((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255)), alpha);
            }
            return null;
        }
    }

    internal static class FirefoxThemes
    {
        public const int MaxLayers = 6;
        public const string HeaderSchema = "recognition.theme_header.v1";

        // A header picture: the same rules as ThemePacks.SafeImagePath, plus SVG patterns (read by SvgLite, never by a full SVG engine).
        public static string? SafeHeaderPath(string? p)
        {
            if (p != null && p.Trim().EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                var asPng = ThemePacks.SafeImagePath(p.Trim()[..^4] + ".png");
                return asPng == null ? null : asPng[..^4] + ".svg";
            }
            return ThemePacks.SafeImagePath(p);
        }

        // A theme manifest that only Firefox would write: string colours, Firefox-only colour names, or a header picture.
        public static bool LooksLikeFirefox(JsonElement theme)
        {
            if (theme.TryGetProperty("images", out var im) && im.ValueKind == JsonValueKind.Object &&
                (im.TryGetProperty("theme_frame", out _) || im.TryGetProperty("headerURL", out _) || im.TryGetProperty("additional_backgrounds", out _))) return true;
            if (theme.TryGetProperty("colors", out var c) && c.ValueKind == JsonValueKind.Object)
                foreach (var p in c.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String || p.Name is "tab_background_text" or "accentcolor" or "textcolor" or "toolbar_field" or "tab_line" or "popup") return true;
            return false;
        }

        // CSS background-position keywords -> (x, y). Unknown words fall back to Firefox's default "right top".
        public static (string X, string Y) Alignment(string? css)
        {
            string x = "right", y = "top";
            var words = (css ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || words.Length > 2) return (x, y);
            bool sawX = false, sawY = false;
            foreach (var w in words)
            {
                if (w is "left" or "right") { x = w; sawX = true; }
                else if (w is "top" or "bottom") { y = w; sawY = true; }
                else if (w == "center") { if (!sawX) { x = "center"; sawX = true; } else { y = "center"; sawY = true; } }
                else return ("right", "top");
            }
            if (words.Length == 1 && words[0] == "center") { x = "center"; y = "center"; }
            else if (sawX && !sawY) y = "center";
            else if (sawY && !sawX) x = "center";
            return (x, y);
        }

        public static string Tiling(string? css) => (css ?? "").Trim().ToLowerInvariant() switch
        {
            "repeat" => "repeat", "repeat-x" => "repeat-x", "repeat-y" => "repeat-y", _ => "no-repeat",
        };

        private static (string Hex, double Alpha)? Col(JsonElement colors, params string[] keys)
        {
            if (colors.ValueKind != JsonValueKind.Object) return null;
            foreach (var k in keys) if (colors.TryGetProperty(k, out var v)) { var c = CssColor.Parse(v); if (c != null) return c; }
            return null;
        }

        // null when the manifest is not a theme or has neither a usable colour nor a picture.
        public static ThemePack? FromManifest(JsonElement root, string fallbackName = "Firefox theme")
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("theme", out var th) || th.ValueKind != JsonValueKind.Object) return null;
            th.TryGetProperty("colors", out var colors);
            th.TryGetProperty("images", out var images);
            th.TryGetProperty("properties", out var props);

            var frame = Col(colors, "frame", "accentcolor");
            var toolbar = Col(colors, "toolbar");
            // the bar as it is actually seen: the toolbar colour laid over the frame by its own alpha
            string? bar = null;
            if (toolbar is { } tb) bar = frame is { } fr ? Rgb.Mix(fr.Hex, tb.Hex, tb.Alpha) : (tb.Alpha >= 0.5 ? tb.Hex : null);
            bar ??= frame?.Hex;

            // header layers: additional_backgrounds are in front of theme_frame; the first listed is on top (CSS order)
            var layers = new List<HeaderLayer>();
            if (images.ValueKind == JsonValueKind.Object)
            {
                string? main = null;
                if (images.TryGetProperty("theme_frame", out var tf) && tf.ValueKind == JsonValueKind.String) main = SafeHeaderPath(tf.GetString());
                else if (images.TryGetProperty("headerURL", out var hu) && hu.ValueKind == JsonValueKind.String) main = SafeHeaderPath(hu.GetString());
                if (main != null) layers.Add(new HeaderLayer(main, "right", "top", "no-repeat"));
                if (images.TryGetProperty("additional_backgrounds", out var ab) && ab.ValueKind == JsonValueKind.Array)
                {
                    var align = props.ValueKind == JsonValueKind.Object && props.TryGetProperty("additional_backgrounds_alignment", out var al) && al.ValueKind == JsonValueKind.Array ? al.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null).ToList() : new List<string?>();
                    var tile = props.ValueKind == JsonValueKind.Object && props.TryGetProperty("additional_backgrounds_tiling", out var ti) && ti.ValueKind == JsonValueKind.Array ? ti.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null).ToList() : new List<string?>();
                    var extra = new List<HeaderLayer>();
                    int i = 0;
                    foreach (var e in ab.EnumerateArray())
                    {
                        var p = e.ValueKind == JsonValueKind.String ? SafeHeaderPath(e.GetString()) : null;
                        if (p != null) { var (x, y) = Alignment(i < align.Count ? align[i] : null); extra.Add(new HeaderLayer(p, x, y, Tiling(i < tile.Count ? tile[i] : null))); }
                        i++;
                    }
                    extra.Reverse();   // drawn bottom to top: the last listed first
                    layers.AddRange(extra);
                }
                if (layers.Count > MaxLayers) layers = layers.Take(1).Concat(layers.Skip(layers.Count - (MaxLayers - 1))).ToList();
            }

            string? ntpImage = null;
            if (images.ValueKind == JsonValueKind.Object && images.TryGetProperty("theme_ntp_background", out var ni) && ni.ValueKind == JsonValueKind.String) ntpImage = ThemePacks.SafeImagePath(ni.GetString());

            if (bar == null && layers.Count == 0 && ntpImage == null) return null;
            bar ??= "#1b1e24";
            bool dark = Rgb.Luminance(bar) < 0.5;
            var text = Col(colors, "toolbar_text", "bookmark_text", "tab_background_text", "textcolor")?.Hex ?? (dark ? "#e6e8ee" : "#1f2430");
            if (Math.Abs(Rgb.Luminance(text) - Rgb.Luminance(bar)) < 0.3) text = dark ? "#e6e8ee" : "#1f2430";
            var accent = Col(colors, "tab_line", "toolbar_field_border_focus", "icons_attention", "popup_highlight", "button_background_active")?.Hex ?? (dark ? "#5b8def" : "#2b6cb0");
            if (Math.Abs(Rgb.Luminance(accent) - Rgb.Luminance(bar)) < 0.08) accent = dark ? "#5b8def" : "#2b6cb0";   // an accent you cannot see on the bar
            var ntp = Col(colors, "ntp_background");
            var start = ntp is { Alpha: >= 0.5 } n ? n.Hex : "default";

            ThemeHeader? header = null;
            if (layers.Count > 0)
            {
                // Firefox draws the header behind both the tabs and the toolbar; here the toolbar sits on top, so it is drawn see-through
                // over the picture: the theme's own toolbar alpha, at most 75 %, and 60 % when the theme gives no toolbar colour.
                int opacity = toolbar is { } t2 ? (int)Math.Round(Math.Min(t2.Alpha, 0.75) * 100) : 60;
                header = new ThemeHeader(frame?.Hex ?? bar, opacity, layers);
            }

            string name = fallbackName;
            if (root.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
            {
                var s = new string((nm.GetString() ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
                if (s.Length > 0 && !s.StartsWith("__MSG_", StringComparison.Ordinal)) name = s.Length > 40 ? s.Substring(0, 40) : s;
            }
            return new ThemePack("imported", name, bar, text, accent, start, dark, ntpImage, Header: header);
        }

        // ---- the header kept for the window (runtime\theme_header\header.v1.json) -------------------------------------------------
        // Files are content-addressed copies: "<sha256>.<ext>". Integers only, fixed key order, LF.
        public static bool IsStoredFileName(string? f)
        {
            if (f == null) return false;
            int dot = f.IndexOf('.');
            return dot == 64 && f.Take(64).All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')) && f.Substring(65) is "png" or "jpg" or "jpeg" or "gif" or "bmp" or "svg";
        }

        public static string StateToJson(ThemeHeader h, bool animate)
        {
            string J(string s) => JsonSerializer.Serialize(s);
            var sb = new StringBuilder("{\"schema\":" + J(HeaderSchema) + ",\"frame\":" + J(h.FrameColor) + ",\"toolbar_opacity\":" + h.ToolbarOpacity + ",\"animate\":" + (animate ? "true" : "false") + ",\"layers\":[");
            sb.Append(string.Join(",", h.Layers.Select(l => "{\"file\":" + J(l.Path) + ",\"x\":" + J(l.AlignX) + ",\"y\":" + J(l.AlignY) + ",\"tile\":" + J(l.Tile) + "}")));
            return sb.Append("]}\n").ToString();
        }

        // null for anything that is not exactly a valid saved header.
        public static ThemeHeader? StateFromJson(string? json) => StateFromJson(json, out _);

        public static ThemeHeader? StateFromJson(string? json, out bool animate)
        {
            animate = true;
            try
            {
                if (string.IsNullOrWhiteSpace(json) || json.Length > 16_000) return null;
                using var d = JsonDocument.Parse(json);
                var r = d.RootElement;
                if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("schema", out var sc) || sc.GetString() != HeaderSchema) return null;
                var frame = r.GetProperty("frame").GetString();
                if (!Rgb.Valid(frame)) return null;
                if (!r.GetProperty("toolbar_opacity").TryGetInt32(out var op) || op < 0 || op > 100) return null;
                var an = r.GetProperty("animate"); if (an.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
                var layers = new List<HeaderLayer>();
                foreach (var l in r.GetProperty("layers").EnumerateArray())
                {
                    var f = l.GetProperty("file").GetString(); var x = l.GetProperty("x").GetString(); var y = l.GetProperty("y").GetString(); var t = l.GetProperty("tile").GetString();
                    if (!IsStoredFileName(f) || x is not ("left" or "center" or "right") || y is not ("top" or "center" or "bottom") || Tiling(t) != t) return null;
                    layers.Add(new HeaderLayer(f!, x, y, t!));
                }
                if (layers.Count == 0 || layers.Count > MaxLayers) return null;
                animate = an.ValueKind == JsonValueKind.True;
                return new ThemeHeader(Rgb.Norm(frame), op, layers);
            }
            catch { animate = true; return null; }
        }
    }

    // The SVG patterns Firefox's theme maker puts in themes (a few <path>s with a fill colour and opacity on a small tile). Read as data,
    // never run: XML without DTDs or external references; only <svg>, <path>, <rect> and attribute-free <g>; only fill, fill-opacity,
    // opacity and fill-rule; integer size up to 1024; path data limited to the path-command alphabet. Anything else: null (layer dropped).
    internal sealed record SvgPath(string Data, string Fill, int OpacityPct, bool EvenOdd);

    internal sealed record SvgLite(int Width, int Height, IReadOnlyList<SvgPath> Paths)
    {
        public const int MaxSide = 1024, MaxPaths = 64, MaxData = 8000;

        public static SvgLite? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 64_000) return null;
            try
            {
                var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreProcessingInstructions = true, IgnoreWhitespace = true, MaxCharactersInDocument = 64_000 };
                using var r = System.Xml.XmlReader.Create(new System.IO.StringReader(text), settings);
                int w = 0, h = 0; var paths = new List<SvgPath>(); int depth = 0; bool root = false;
                const string ns = "http://www.w3.org/2000/svg";
                while (r.Read())
                {
                    if (r.NodeType == System.Xml.XmlNodeType.EndElement) { depth--; continue; }
                    if (r.NodeType == System.Xml.XmlNodeType.Text || r.NodeType == System.Xml.XmlNodeType.CDATA) return null;
                    if (r.NodeType != System.Xml.XmlNodeType.Element) continue;
                    if (r.NamespaceURI.Length > 0 && r.NamespaceURI != ns) return null;
                    var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (r.HasAttributes)
                        for (int i = 0; i < r.AttributeCount; i++)
                        {
                            r.MoveToAttribute(i);
                            if (r.Prefix == "xmlns" || r.Name == "xmlns") { if (r.Value != ns) return null; continue; }
                            if (r.NamespaceURI.Length > 0) return null;   // xlink:href and friends
                            attrs[r.LocalName] = r.Value;
                        }
                    r.MoveToElement();
                    bool empty = r.IsEmptyElement;
                    var name = r.LocalName;
                    if (!root)
                    {
                        if (name != "svg") return null;
                        root = true;
                        if (attrs.Keys.Any(k => k is not ("width" or "height" or "viewBox" or "version"))) return null;
                        int Dim(string k) => attrs.TryGetValue(k, out var v) && int.TryParse(v.EndsWith("px") ? v[..^2] : v, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
                        w = Dim("width"); h = Dim("height");
                        if ((w == 0 || h == 0) && attrs.TryGetValue("viewBox", out var vb))
                        {
                            var p = vb.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                            if (p.Length == 4 && p[0] == "0" && p[1] == "0" && int.TryParse(p[2], out var vw) && int.TryParse(p[3], out var vh)) { w = vw; h = vh; }
                        }
                        if (w < 1 || h < 1 || w > MaxSide || h > MaxSide) return null;
                    }
                    else if (name == "g") { if (attrs.Count > 0) return null; }
                    else if (name is "path" or "rect")
                    {
                        if (attrs.Keys.Any(k => k is not ("d" or "fill" or "fill-opacity" or "opacity" or "fill-rule" or "x" or "y" or "width" or "height"))) return null;
                        string? d;
                        if (name == "path") d = attrs.TryGetValue("d", out var dv) ? dv : null;
                        else
                        {
                            double N(string k) => attrs.TryGetValue(k, out var v) && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) && x >= 0 && x <= MaxSide ? x : -1;
                            double rx = attrs.ContainsKey("x") ? N("x") : 0, ry = attrs.ContainsKey("y") ? N("y") : 0, rw = N("width"), rh = N("height");
                            if (rx < 0 || ry < 0 || rw <= 0 || rh <= 0) return null;
                            string F(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                            d = "M" + F(rx) + " " + F(ry) + "h" + F(rw) + "v" + F(rh) + "h-" + F(rw) + "z";
                        }
                        if (d == null || d.Length == 0 || d.Length > MaxData || !d.All(c => "MmLlHhVvCcSsQqTtAaZz0123456789.,-+eE ".IndexOf(c) >= 0 || c == '\n' || c == '\r' || c == '\t')) return null;
                        var fillText = attrs.TryGetValue("fill", out var fv) ? fv : "#000000";
                        double op = 1;
                        foreach (var k in new[] { "fill-opacity", "opacity" })
                            if (attrs.TryGetValue(k, out var ov)) { if (!double.TryParse(ov, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var o) || double.IsNaN(o)) return null; op *= Math.Clamp(o, 0, 1); }
                        if (fillText.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) { if (!empty) depth++; continue; }
                        var col = CssColor.Parse(fillText); if (col == null) return null;
                        op *= col.Value.Alpha;
                        var rule = attrs.TryGetValue("fill-rule", out var fr) ? fr : "nonzero";
                        if (rule is not ("evenodd" or "nonzero")) return null;
                        if (paths.Count >= MaxPaths) return null;
                        paths.Add(new SvgPath(d.Trim(), col.Value.Hex, (int)Math.Round(op * 100), rule == "evenodd"));
                    }
                    else return null;   // style, script, image, use, foreignObject, defs, pattern, text ... are not read
                    if (!empty) depth++;
                    if (depth > 8) return null;
                }
                return root && paths.Count > 0 ? new SvgLite(w, h, paths) : null;
            }
            catch (System.Xml.XmlException) { return null; }
        }

        public bool Equals(SvgLite? o) => o != null && Width == o.Width && Height == o.Height && Paths.SequenceEqual(o.Paths);
        public override int GetHashCode() => HashCode.Combine(Width, Height, Paths.Count);
    }
}
