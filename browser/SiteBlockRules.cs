using System;
using System.Collections.Generic;
using System.Linq;

namespace Recognition.Browser
{
    // User site blocking (the person's own "don't let me open these" list), separate from the ad/tracker filter and the governed host
    // blocklist. Pure matching so it is tested directly: an entry blocks that exact host AND its subdomains (blocking "example.com" also
    // blocks "www.example.com" and "mail.example.com"), case-insensitively, and never a look-alike ("notexample.com" is not blocked by
    // "example.com"). Internal pages and non-web schemes are never blocked here.
    internal static class SiteBlockRules
    {
        public const int MaxEntries = 5000;

        // What to STORE from what the person typed: a bare host, lowercased, scheme/path/port/userinfo stripped. Returns null if it is
        // not a usable host (empty, has spaces/control chars, no dot and not localhost, too long).
        public static string? CleanEntry(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = s.Trim();
            if (Uri.TryCreate(t, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                t = u.Host;
            else
            {
                var slash = t.IndexOf('/'); if (slash >= 0) t = t.Substring(0, slash);   // drop any path a bare "example.com/x" carried
                var at = t.IndexOf('@'); if (at >= 0) t = t.Substring(at + 1);
                var colon = t.IndexOf(':'); if (colon >= 0) t = t.Substring(0, colon);    // drop a port
            }
            t = t.Trim('.').ToLowerInvariant();
            if (t.Length == 0 || t.Length > 253 || t.Any(c => c <= ' ' || c == '/' || c == '\\' || c == '?' || c == '#' || c == '@' || c == ':')) return null;
            if (!t.Contains('.') && t != "localhost") return null;
            return t;
        }

        // The host of a URL, lowercased, for matching. Null for a non-web or malformed URL.
        public static string? HostOf(string? url)
        {
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
            return u.Host.ToLowerInvariant();
        }

        // Is <url> blocked by the person's list? Matches the host and any subdomain of a listed host.
        public static bool IsBlocked(string? url, IReadOnlySet<string>? blocked)
        {
            if (blocked == null || blocked.Count == 0) return false;
            var host = HostOf(url);
            if (host == null) return false;
            if (blocked.Contains(host)) return true;
            foreach (var b in blocked)
                if (host.Length > b.Length && host.EndsWith("." + b, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
