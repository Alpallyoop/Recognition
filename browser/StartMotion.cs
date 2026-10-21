using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Recognition.Browser
{
    // Animated start page backgrounds (pure rules, no WPF, files or network; executed by browser.tests).
    // The animation itself is ThemeForge's motion runtime, vendored byte-for-byte as shield\motion.js (an embedded resource).
    // A recipe is four values: an effect from a fixed list and three bounded integers. Everything that reaches the page is built here
    // from validated values, and the runtime is used only when its bytes hash to RuntimeSha256, so nothing a theme file or a damaged
    // settings file says can bring script, CSS or addresses into the start page. Anything invalid becomes "none" (no animation).
    internal sealed record MotionRecipe(string Effect, int DurationMs, int IntensityPct, int Seed)
    {
        public bool IsOn => Effect != StartMotion.NoneId;
    }

    // How the canvas sits over the existing CSS background. Without a picture the runtime clears every frame with a single colour, so the
    // canvas is blended: black with "screen" on dark backgrounds and white with "multiply" on light ones leave the CSS background exactly as it
    // is and only the moving parts show. With your picture the runtime draws the picture itself (with the same darkening as the CSS).
    internal sealed record MotionLook(string Accent, string Text, bool LightBackground, bool UsePicture);

    internal static class StartMotion
    {
        public const string NoneId = "none";
        public const int MinDuration = 4000, MaxDuration = 120000, DefaultDuration = 24000, DefaultIntensity = 50, DefaultSeed = 1;
        public const string RuntimeResource = "motion.js";
        // SHA-256 of ThemeForge motion runtime 2.0.0 (themeforge outputs/constellation_night/newtab_extension/motion.js), see docs\proposals\0002.
        public const string RuntimeSha256 = "074198d978e3f01bb4729775c5643659dba2b06906838152591c1f992f2679ce";

        // id, plain name shown in Settings
        public static readonly (string Id, string Name)[] Effects =
        {
            ("none", "Still"), ("kenburns", "Slow zoom"), ("pan", "Pan"), ("drift", "Drift"), ("breathe", "Breathe"), ("parallax", "Parallax"),
            ("particles", "Particles"), ("aurora", "Aurora"), ("rain", "Rain"), ("snow", "Snow"), ("shimmer", "Shimmer"), ("fireflies", "Fireflies"),
        };

        // effects that only move or shade a picture (without one they show next to nothing)
        public static bool MovesPicture(string effect) => effect is "kenburns" or "pan" or "drift" or "breathe" or "parallax";

        // speed and strength choices in Settings
        public static readonly (string Id, string Name, int Value)[] Speeds = { ("slow", "Slow", 60000), ("normal", "Normal", 24000), ("fast", "Fast", 8000) };
        public static readonly (string Id, string Name, int Value)[] Strengths = { ("low", "Low", 25), ("medium", "Medium", 50), ("high", "High", 85) };

        public static readonly MotionRecipe None = new(NoneId, DefaultDuration, DefaultIntensity, DefaultSeed);

        public static bool ValidEffect(string? e) => e != null && Effects.Any(x => x.Id == e);
        public static string EffectName(string effect) => Effects.FirstOrDefault(x => x.Id == effect).Name ?? "Still";

        // A JSON integer only: no fraction, no exponent, no leading "+", within the range.
        private static bool Int(JsonElement v, int min, int max, out int value)
        {
            value = 0;
            if (v.ValueKind != JsonValueKind.Number) return false;
            var raw = v.GetRawText();
            if (raw.Length == 0 || raw.Length > 11 || !raw.All(c => char.IsAsciiDigit(c) || c == '-')) return false;
            if (!long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l) || l < min || l > max) return false;
            value = (int)l;
            return true;
        }

        // Strict: null when anything is wrong (used for theme files, where a bad recipe refuses the whole theme).
        // Allowed keys: effect (required), duration_ms, intensity_pct, seed (each optional, default 24000 / 50 / 1). Any other key refuses it.
        public static MotionRecipe? Parse(JsonElement e)
        {
            try
            {
                if (e.ValueKind != JsonValueKind.Object) return null;
                string? effect = null; int d = DefaultDuration, i = DefaultIntensity, s = DefaultSeed;
                var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                foreach (var p in e.EnumerateObject())
                {
                    if (!seen.Add(p.Name)) return null;
                    switch (p.Name)
                    {
                        case "effect": if (p.Value.ValueKind != JsonValueKind.String) return null; effect = p.Value.GetString(); break;
                        case "duration_ms": if (!Int(p.Value, MinDuration, MaxDuration, out d)) return null; break;
                        case "intensity_pct": if (!Int(p.Value, 0, 100, out i)) return null; break;
                        case "seed": if (!Int(p.Value, 0, int.MaxValue, out s)) return null; break;
                        default: return null;
                    }
                }
                if (!ValidEffect(effect)) return null;
                return effect == NoneId ? None : new MotionRecipe(effect!, d, i, s);
            }
            catch { return null; }
        }

        public static MotionRecipe? Parse(string? json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json) || json.Length > 2000) return null;
                using var doc = JsonDocument.Parse(json);
                return Parse(doc.RootElement);
            }
            catch { return null; }
        }

        // Fail closed: anything that is not a valid recipe is "none". Never throws.
        public static MotionRecipe Normalize(JsonElement? e) => e.HasValue ? Parse(e.Value) ?? None : None;
        public static MotionRecipe Normalize(string? json) => Parse(json) ?? None;
        public static MotionRecipe Normalize(MotionRecipe? r) =>
            r == null || !ValidEffect(r.Effect) || r.Effect == NoneId || r.DurationMs < MinDuration || r.DurationMs > MaxDuration || r.IntensityPct < 0 || r.IntensityPct > 100 || r.Seed < 0
                ? None : r;

        // Canonical form (fixed key order, integers only, no spaces).
        public static string ToJson(MotionRecipe? r)
        {
            var n = Normalize(r);
            return "{\"effect\":\"" + n.Effect + "\",\"duration_ms\":" + n.DurationMs.ToString(CultureInfo.InvariantCulture) + ",\"intensity_pct\":" +
                   n.IntensityPct.ToString(CultureInfo.InvariantCulture) + ",\"seed\":" + n.Seed.ToString(CultureInfo.InvariantCulture) + "}";
        }

        public static MotionRecipe WithEffect(MotionRecipe cur, string? effect)
        {
            if (!ValidEffect(effect)) return Normalize(cur);
            if (effect == NoneId) return None;
            var b = Normalize(cur);
            return new MotionRecipe(effect!, b.DurationMs, b.IntensityPct, b.Seed);
        }
        public static MotionRecipe WithSpeed(MotionRecipe cur, string? id)
        {
            var b = Normalize(cur); var s = Speeds.FirstOrDefault(x => x.Id == id);
            return !b.IsOn || s.Id == null ? b : b with { DurationMs = s.Value };
        }
        public static MotionRecipe WithStrength(MotionRecipe cur, string? id)
        {
            var b = Normalize(cur); var s = Strengths.FirstOrDefault(x => x.Id == id);
            return !b.IsOn || s.Id == null ? b : b with { IntensityPct = s.Value };
        }

        // The look of the motion from the current theme colours and start background. Colours that are not #rrggbb fall back to the start
        // page's own; the text colour must stand out from the background (light on dark, dark on light) or the start page's own text is used.
        public static MotionLook Look(string? accent, string? text, string? startBg, bool hasPicture)
        {
            var bg = StartBackground.Normalize(startBg);
            bool pic = bg == "image" && hasPicture;
            bool light = !pic && StartBackground.IsLight(bg);
            var acc = Rgb.Valid(accent) ? Rgb.Norm(accent) : "#4c9bf0";
            var t = Rgb.Valid(text) ? Rgb.Norm(text) : "";
            if (t.Length == 0 || (light ? Rgb.Luminance(t) > 0.45 : Rgb.Luminance(t) < 0.55)) t = light ? "#1b2230" : "#e9ebef";
            return new MotionLook(acc, t, light, pic);
        }

        // The configuration given to ThemeForgeMotion.create (fixed key order, integers only). The picture is not in it: the page script takes
        // it from the CSS background that is already on the page, so the picture is embedded once.
        public static string ConfigJson(MotionRecipe? r, MotionLook look)
        {
            var n = Normalize(r);
            var bg = look.UsePicture ? "#0a0c10" : look.LightBackground ? "#ffffff" : "#000000";
            string C(string c) => Rgb.Valid(c) ? Rgb.Norm(c) : "#000000";
            return "{\"effect\":\"" + n.Effect + "\",\"duration_ms\":" + n.DurationMs.ToString(CultureInfo.InvariantCulture) + ",\"intensity_pct\":" + n.IntensityPct.ToString(CultureInfo.InvariantCulture) +
                   ",\"seed\":" + n.Seed.ToString(CultureInfo.InvariantCulture) + ",\"fit\":\"cover\",\"position\":\"center\",\"overlay_color\":\"#0a0c10\",\"overlay_opacity_pct\":" + (look.UsePicture ? "48" : "0") +
                   ",\"blur_px\":0,\"respect_reduced_motion\":true,\"colors\":{\"accent\":\"" + C(look.Accent) + "\",\"text\":\"" + C(look.Text) + "\",\"background\":\"" + bg + "\"}}";
        }

        public static bool RuntimeOk(string? runtimeJs) =>
            runtimeJs != null && runtimeJs.Length < 200_000 && runtimeJs.IndexOf("</script", StringComparison.OrdinalIgnoreCase) < 0 &&
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runtimeJs))).ToLowerInvariant() == RuntimeSha256;

        public const string CanvasId = "rec-motion";

        // Adds the canvas (behind everything) and the runtime to the start page. Returns the page unchanged when the recipe is "none", the
        // runtime is missing or not the expected bytes, or the page has no <body>/</body>. The CSS background stays underneath as the fallback:
        // if anything fails in the page, the canvas is removed.
        public static string Inject(string html, MotionRecipe? r, MotionLook look, string? runtimeJs)
        {
            var n = Normalize(r);
            if (!n.IsOn || !RuntimeOk(runtimeJs) || html == null) return html ?? "";
            int b = html.IndexOf("<body>", StringComparison.Ordinal), e = html.LastIndexOf("</body>", StringComparison.Ordinal);
            if (b < 0 || e < b) return html;
            var blend = look.UsePicture ? "normal" : look.LightBackground ? "multiply" : "screen";
            var canvas = "<canvas id='" + CanvasId + "' aria-hidden='true' style='position:fixed;left:0;top:0;width:100vw;height:100vh;z-index:-1;pointer-events:none;mix-blend-mode:" + blend + "'></canvas>";
            var boot =
                "<script>(function(){var c=document.getElementById('" + CanvasId + "');function off(){try{if(c&&c.parentNode)c.parentNode.removeChild(c);}catch(x){}}" +
                "try{var cfg=" + ConfigJson(n, look) + ";if(!window.ThemeForgeMotion){off();return;}" +
                "var go=function(img){try{cfg.image=img||null;var m=window.ThemeForgeMotion.create(c,cfg);m.start();" +
                "window.addEventListener('resize',function(){if(m.reduced)m.renderAt(0);});}catch(x){off();}};" +
                (look.UsePicture
                    ? "var bi=getComputedStyle(document.body).backgroundImage||'';var mm=/url\\(\"?(data:image\\/jpeg;base64,[A-Za-z0-9+\\/=]+)\"?\\)/.exec(bi);" +
                      "if(!mm){off();return;}var im=new Image();im.onload=function(){go(im);};im.onerror=off;im.src=mm[1];"
                    : "go(null);") +
                "}catch(x){off();}})();</script>";
            return html.Substring(0, b + 6) + canvas + html.Substring(b + 6, e - b - 6) + "<script>" + runtimeJs + "</script>" + boot + html.Substring(e);
        }
    }
}
