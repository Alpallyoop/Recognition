using System;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Recognition.Browser
{
    // Pure rules for "Update from GitHub" in Settings: no network, files or processes here (the window code does that, and only on a click).
    internal sealed record ReleaseInfo(string Tag, Version Version, string ZipUrl, string ShaUrl, long Size);

    internal static class SelfUpdateRules
    {
        public const string ApiUrl = "https://api.github.com/repos/Alpallyoop/Recognition/releases/latest";
        public const string ZipName = "Recognition-win-x64.zip";
        public const long MaxZipBytes = 600L * 1024 * 1024;

        // Only GitHub itself (and the hosts it redirects release downloads to) may serve the update.
        public static bool IsAllowedHost(Uri? u)
        {
            if (u == null || u.Scheme != Uri.UriSchemeHttps) return false;
            var h = u.Host.ToLowerInvariant();
            return h == "api.github.com" || h == "github.com" || h.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
        }

        public static bool TryParseVersion(string? text, out Version v)
        {
            v = new Version(0, 0);
            var t = (text ?? "").Trim();
            if (t.StartsWith("v", StringComparison.OrdinalIgnoreCase)) t = t.Substring(1);
            var m = Regex.Match(t, @"^\d+(\.\d+){1,3}");
            if (!m.Success || !Version.TryParse(m.Value, out var parsed) || parsed == null) return false;
            v = parsed;
            return true;
        }

        public static bool IsNewer(string? installed, Version candidate) => TryParseVersion(installed, out var cur) && candidate > cur;

        // The release must carry the zip AND its .sha256 file, both on an allowed host; anything else is refused.
        public static ReleaseInfo? ParseRelease(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
                if (root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
                var tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
                if (!TryParseVersion(tag, out var ver)) return null;
                if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
                string zip = "", sha = ""; long size = 0;
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                    var url = a.TryGetProperty("browser_download_url", out var bu) ? (bu.GetString() ?? "") : "";
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsAllowedHost(uri)) continue;
                    if (name == ZipName) { zip = url; size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0; }
                    else if (name == ZipName + ".sha256") sha = url;
                }
                if (zip.Length == 0 || sha.Length == 0) return null;
                if (size <= 0 || size > MaxZipBytes) return null;
                return new ReleaseInfo(tag, ver, zip, sha, size);
            }
            catch { return null; }
        }

        // "<64 hex>  Recognition-win-x64.zip" or just the hash.
        public static string? ParseSha256(string? text)
        {
            var first = (text ?? "").Trim().Split(new[] { ' ', '\t', '\r', '\n', '*' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            return Regex.IsMatch(first, "^[0-9a-fA-F]{64}$") ? first.ToLowerInvariant() : null;
        }
    }
}
