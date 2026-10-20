using System;
using System.Text.RegularExpressions;

namespace Recognition.Browser
{
    // The start page background and the home page address. Everything that reaches the page is checked here: only a fixed preset name,
    // a #rrggbb colour or the user's own saved image can become CSS, and only http/https addresses (or the start page) can become the home page.
    internal static class StartBackground
    {
        public static readonly string[] Presets =
        {
            "default", "aurora", "dusk", "forest", "slate", "light",
            "ocean", "sunset", "nebula", "midnight", "graphite", "rose", "lava", "horizon", "stars", "dots", "stripes", "blueprint",
            "mint", "sand", "ice", "paper"
        };
        public const string StartPage = "recognition:start";

        // Backgrounds that ship with the program. Plain CSS gradients only (no pictures, addresses or scripts), so they work offline and cost nothing to load.
        // The second value says the page needs dark text (a light background).
        private static readonly System.Collections.Generic.Dictionary<string, (string Css, bool Light)> Built = new()
        {
            ["ocean"] = ("background:radial-gradient(900px 520px at 20% -10%,rgba(40,140,220,.38),transparent 70%),radial-gradient(800px 520px at 90% 110%,rgba(20,90,170,.34),transparent 70%),#07131f", false),
            ["sunset"] = ("background:linear-gradient(180deg,#2a1b3d 0%,#7a2f5a 48%,#e8743b 100%) fixed", false),
            ["nebula"] = ("background:radial-gradient(700px 500px at 15% 20%,rgba(160,80,240,.35),transparent 65%),radial-gradient(700px 500px at 85% 80%,rgba(240,80,160,.28),transparent 65%),#0d0a17", false),
            ["midnight"] = ("background:radial-gradient(1000px 600px at 50% -20%,#1d2a4d,transparent 70%),#080b14", false),
            ["graphite"] = ("background:linear-gradient(180deg,#22252b,#14161a) fixed", false),
            ["rose"] = ("background:radial-gradient(900px 520px at 80% -10%,rgba(229,82,127,.30),transparent 70%),radial-gradient(800px 520px at 5% 105%,rgba(160,60,110,.26),transparent 70%),#1a1015", false),
            ["lava"] = ("background:radial-gradient(800px 500px at 50% 120%,rgba(255,100,40,.45),transparent 70%),radial-gradient(600px 400px at 10% -10%,rgba(200,50,30,.22),transparent 70%),#140b0a", false),
            ["horizon"] = ("background:linear-gradient(180deg,#0a1a2f 0%,#17406b 55%,#f0a45a 100%) fixed", false),
            ["stars"] = ("background:radial-gradient(1.2px 1.2px at 8% 18%,#fff,transparent),radial-gradient(1px 1px at 22% 64%,#cfe3ff,transparent),radial-gradient(1.4px 1.4px at 37% 32%,#fff,transparent),radial-gradient(1px 1px at 51% 78%,#fff,transparent),radial-gradient(1.2px 1.2px at 63% 14%,#cfe3ff,transparent),radial-gradient(1px 1px at 76% 52%,#fff,transparent),radial-gradient(1.4px 1.4px at 88% 26%,#fff,transparent),radial-gradient(1px 1px at 94% 84%,#cfe3ff,transparent),#060810", false),
            ["dots"] = ("background:radial-gradient(rgba(255,255,255,.09) 1.2px,transparent 1.5px) 0 0/22px 22px,#12151b", false),
            ["stripes"] = ("background:repeating-linear-gradient(135deg,rgba(255,255,255,.035) 0 2px,transparent 2px 14px),#14171d", false),
            ["blueprint"] = ("background:linear-gradient(rgba(140,190,255,.12) 1px,transparent 1px) 0 0/32px 32px,linear-gradient(90deg,rgba(140,190,255,.12) 1px,transparent 1px) 0 0/32px 32px,#0b2545", false),
            ["mint"] = ("background:linear-gradient(180deg,#f2fbf6,#d9f0e5) fixed", true),
            ["sand"] = ("background:linear-gradient(180deg,#f7f1e4,#eadfc6) fixed", true),
            ["ice"] = ("background:linear-gradient(180deg,#eef6ff,#cfe3f7) fixed", true),
            ["paper"] = ("background:linear-gradient(rgba(0,0,0,.045) 1px,transparent 1px) 0 0/28px 28px,linear-gradient(90deg,rgba(0,0,0,.045) 1px,transparent 1px) 0 0/28px 28px,#f7f7f4", true),
        };

        // The "background:..." declaration of a built-in or colour background, for the little previews on the Customize page. null for an image.
        public static string? Preview(string? key)
        {
            var k = Normalize(key);
            if (k == "image") return null;
            if (k == "default") return "background:#111318";
            if (Built.TryGetValue(k, out var b)) return b.Css;
            var css = Css(k, null);
            if (css.StartsWith("body{", StringComparison.Ordinal)) { var end = css.IndexOf('}'); if (end > 5) return css.Substring(5, end - 5); }
            return null;
        }

        public static string Normalize(string? key)
        {
            var k = (key ?? "").Trim().ToLowerInvariant();
            if (k == "image" || Array.IndexOf(Presets, k) >= 0) return k;
            return Regex.IsMatch(k, "^#[0-9a-f]{6}$") ? k : "default";
        }

        private static bool HexIsLight(string hex)
        {
            int r = Convert.ToInt32(hex.Substring(1, 2), 16), g = Convert.ToInt32(hex.Substring(3, 2), 16), b = Convert.ToInt32(hex.Substring(5, 2), 16);
            return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0 > 0.6;
        }

        private const string LightVars = ":root{--panel:#ffffff;--line:#d5dae2;--txt:#1b2230;--mut:#586172}.tile:hover{background:#f1f3f7!important;border-color:#b9c0cc!important}.facts{color:#6b7385!important}input::placeholder{color:#8a93a3!important}.si{filter:invert(.6)}";

        // CSS added after the page's own style. Empty for "default". An image is used only when it is a data: URL made of base64 characters.
        public static string Css(string? key, string? imageDataUrl)
        {
            var k = Normalize(key);
            switch (k)
            {
                case "default": return "";
                case "aurora": return "body{background:radial-gradient(900px 520px at 18% -10%,rgba(72,200,170,.34),transparent 70%),radial-gradient(800px 520px at 92% 110%,rgba(76,155,240,.30),transparent 70%),#0c1317}";
                case "dusk": return "body{background:radial-gradient(900px 520px at 80% -10%,rgba(240,130,90,.28),transparent 70%),radial-gradient(800px 520px at 5% 105%,rgba(140,100,240,.32),transparent 70%),#15121c}";
                case "forest": return "body{background:radial-gradient(900px 520px at 50% -15%,rgba(90,190,110,.28),transparent 70%),radial-gradient(800px 520px at 95% 105%,rgba(60,140,100,.24),transparent 70%),#0e1511}";
                case "slate": return "body{background:#1b1f27}";
                case "light": return "body{background:linear-gradient(180deg,#f6f8fb,#e8ecf3)}" + LightVars;
                case var b when Built.ContainsKey(b): return "body{" + Built[b].Css + "}" + (Built[b].Light ? LightVars : "");
                case "image":
                    if (imageDataUrl != null && imageDataUrl.Length < 3_000_000 && Regex.IsMatch(imageDataUrl, "^data:image/jpeg;base64,[A-Za-z0-9+/=]+$"))
                        return "body{background:linear-gradient(rgba(10,12,16,.38),rgba(10,12,16,.58)),url(" + imageDataUrl + ") center/cover fixed no-repeat}";
                    return "";
                default:
                    return "body{background:" + k + "}" + (HexIsLight(k) ? LightVars : "");
            }
        }

        // A home page the user typed. Returns null when it is not acceptable. Accepts the start page, a full http/https address, a bare
        // site name such as example.com, or an existing local .html file (LaunchArgs rules: no user names in addresses, no control characters).
        public static string? HomeUrl(string? input, Func<string, bool>? fileExists = null)
        {
            var v = (input ?? "").Trim();
            if (v.Length == 0 || v.Length > LaunchArgs.MaxLen) return null;
            if (string.Equals(v, StartPage, StringComparison.OrdinalIgnoreCase) || string.Equals(v, "start", StringComparison.OrdinalIgnoreCase)) return StartPage;
            var direct = LaunchArgs.Parse(v, fileExists);
            if (direct != null) return direct;
            if (v.IndexOf("://", StringComparison.Ordinal) < 0 && v.IndexOf(' ') < 0 && v.IndexOf(':') < 0 && v.IndexOf('.') > 0)
                return LaunchArgs.Parse("https://" + v, fileExists);
            return null;
        }
    }
}
