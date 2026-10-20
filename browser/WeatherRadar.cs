using System;
using System.Globalization;
using System.Linq;

namespace Recognition.Browser
{
    // Pure rules for the weather radar card on the start page (no WPF, files or network; executed by browser.tests).
    // Nothing is loaded from a radar service until the person clicks "Show live radar" (or chooses "Always show" in Settings).
    // Only a fixed provider name, validated numbers, or an https address the person typed can become the radar's address.
    internal sealed record RadarRegion(string Id, string Name, double Lat, double Lon, int Zoom);

    internal static class WeatherRadar
    {
        // auto = show with every new tab using my location, and ask first if location is off; click = only after a click; live = always show (the chosen place if location is off); off
        public static readonly string[] Modes = { "auto", "click", "live", "off" };
        public static readonly string[] Providers = { "windy", "rainviewer", "custom" };

        public static readonly RadarRegion[] Regions =
        {
            new("us",        "United States",        39.5,  -98.35, 4),
            new("northeast", "US Northeast",         42.0,  -74.0,  6),
            new("southeast", "US Southeast",         32.5,  -83.0,  6),
            new("midwest",   "US Midwest",           41.5,  -90.0,  6),
            new("plains",    "US Southern Plains",   33.5,  -98.0,  6),
            new("west",      "US West Coast",        37.5,  -121.0, 6),
            new("northwest", "US Northwest",         46.5,  -121.0, 6),
            new("alaska",    "Alaska",               63.5,  -150.0, 4),
            new("hawaii",    "Hawaii",               20.8,  -157.0, 7),
            new("canada",    "Canada",               54.0,  -96.0,  3),
            new("mexico",    "Mexico",               23.5,  -102.0, 5),
            new("brazil",    "Brazil",               -14.0, -52.0,  4),
            new("uk",        "United Kingdom",       54.0,  -2.5,   5),
            new("europe",    "Europe",               50.0,  10.0,   4),
            new("india",     "India",                22.5,  79.0,   5),
            new("japan",     "Japan",                36.5,  138.0,  5),
            new("australia", "Australia",            -26.0, 134.0,  4),
            new("nz",        "New Zealand",          -41.0, 173.0,  5),
        };

        public static string NormalizeMode(string? m) { var k = (m ?? "").Trim().ToLowerInvariant(); return Array.IndexOf(Modes, k) >= 0 ? k : "auto"; }
        public static string NormalizeProvider(string? p) { var k = (p ?? "").Trim().ToLowerInvariant(); return Array.IndexOf(Providers, k) >= 0 ? k : "windy"; }
        public static RadarRegion? FindRegion(string? id) => Regions.FirstOrDefault(r => string.Equals(r.Id, (id ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

        // latitude -85..85, longitude -180..180, zoom 2..11; plain decimal text only
        public static bool TryCoord(string? lat, string? lon, string? zoom, out double la, out double lo, out int z)
        {
            la = lo = 0; z = 0;
            if (!double.TryParse((lat ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out la) || double.IsNaN(la) || double.IsInfinity(la) || la < -85 || la > 85) return false;
            if (!double.TryParse((lon ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out lo) || double.IsNaN(lo) || double.IsInfinity(lo) || lo < -180 || lo > 180) return false;
            if (!int.TryParse((zoom ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out z) || z < 2 || z > 11) return false;
            la = Math.Round(la, 3); lo = Math.Round(lo, 3);
            return true;
        }

        private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        // An address the person typed for their own radar (any radar page that allows being shown in a card): https only, no user name, no spaces or control characters.
        public static string? CustomUrl(string? input)
        {
            var s = (input ?? "").Trim();
            if (s.Length == 0 || s.Length > 400 || s.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '"' || c == '\'' || c == '<' || c == '>')) return null;
            if (s.IndexOf("://", StringComparison.Ordinal) < 0) s = "https://" + s;
            if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || u.UserInfo.Length > 0 || u.Host.IndexOf('.') < 0) return null;
            return u.AbsoluteUri;
        }

        // The address shown inside the card.
        public static string? EmbedUrl(string provider, double lat, double lon, int zoom, string? custom)
        {
            switch (NormalizeProvider(provider))
            {
                case "windy":
                    return "https://embed.windy.com/embed2.html?lat=" + N(lat) + "&lon=" + N(lon) + "&detailLat=" + N(lat) + "&detailLon=" + N(lon) +
                           "&width=650&height=450&zoom=" + zoom + "&level=surface&overlay=radar&product=radar&menu=&message=true&marker=&calendar=now&pressure=&type=map&location=coordinates&detail=&metricWind=default&metricTemp=default&radarRange=-1";
                case "rainviewer":
                    return "https://www.rainviewer.com/map.html?loc=" + N(lat) + "," + N(lon) + "," + zoom + "&layer=radar&sm=1&sn=1";
                default:
                    return CustomUrl(custom);
            }
        }

        // The address of the "Open in a tab" link (the provider's own full page).
        public static string? OpenUrl(string provider, double lat, double lon, int zoom, string? custom)
        {
            switch (NormalizeProvider(provider))
            {
                case "windy": return "https://www.windy.com/-Weather-radar-radar?radar," + N(lat) + "," + N(lon) + "," + zoom;
                case "rainviewer": return EmbedUrl("rainviewer", lat, lon, zoom, null);
                default: return CustomUrl(custom);
            }
        }

        public static string ProviderName(string provider) => NormalizeProvider(provider) switch { "windy" => "Windy", "rainviewer" => "RainViewer", _ => "your address" };
    }
}
