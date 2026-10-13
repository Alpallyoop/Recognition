using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace Recognition.Browser
{
    // Pure rules behind the everyday-browser features: search engines, importing a bookmarks file, the most-visited tiles on the start page,
    // and the small coloured badge used where a site has no icon. No WPF, no files, no network: tests execute all of it.
    internal sealed record SearchEngine(string Id, string Name, string Template);

    internal static class SearchEngines
    {
        public static readonly SearchEngine[] All =
        {
            new("duckduckgo", "DuckDuckGo", "https://duckduckgo.com/?q={q}"),
            new("brave",      "Brave Search", "https://search.brave.com/search?q={q}"),
            new("startpage",  "Startpage", "https://www.startpage.com/do/search?q={q}"),
            new("ecosia",     "Ecosia", "https://www.ecosia.org/search?q={q}"),
            new("qwant",      "Qwant", "https://www.qwant.com/?q={q}"),
            new("bing",       "Bing", "https://www.bing.com/search?q={q}"),
            new("google",     "Google", "https://www.google.com/search?q={q}"),
        };

        public static SearchEngine Get(string? id) => All.FirstOrDefault(e => e.Id == (id ?? "").Trim().ToLowerInvariant()) ?? All[0];
        public static string Normalize(string? id) => Get(id).Id;

        // the address that a search for `query` opens
        public static string Url(string? id, string? query) => Get(id).Template.Replace("{q}", Uri.EscapeDataString(query ?? ""));

        // everything in the template before the query, for pages that add the query themselves
        public static string Prefix(string? id) { var t = Get(id).Template; return t.Substring(0, t.IndexOf("{q}", StringComparison.Ordinal)); }

        // where an empty address bar goes
        public static string Home(string? id) { var t = new Uri(Get(id).Template.Replace("{q}", "")); return t.Scheme + "://" + t.Host + "/"; }
    }

    internal sealed record ImportedBookmark(string Url, string Title);

    internal static class BookmarkImport
    {
        public const int MaxItems = 5000, MaxBytes = 20_000_000;

        // The bookmarks file every browser can export (Netscape format: Chrome, Edge, Brave, Opera, Vivaldi, Firefox, Safari).
        // Only http and https addresses are taken; titles are decoded and cut to 200 characters; duplicates are dropped.
        public static List<ImportedBookmark> Parse(string? html)
        {
            var res = new List<ImportedBookmark>();
            if (string.IsNullOrEmpty(html) || html.Length > MaxBytes) return res;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(html, "<a\\s[^>]*?href\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)')[^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)))
            {
                var raw = WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
                var url = LaunchArgs.Parse(raw);
                if (url == null || !seen.Add(url)) continue;
                var title = WebUtility.HtmlDecode(Regex.Replace(m.Groups[3].Value, "<[^>]*>", "")).Trim();
                title = new string(title.Where(c => !char.IsControl(c)).ToArray());
                if (title.Length > 200) title = title.Substring(0, 200);
                res.Add(new ImportedBookmark(url, title));
                if (res.Count >= MaxItems) break;
            }
            return res;
        }
    }

    internal sealed record TopSite(string Url, string Host, string Title, int Visits);

    internal static class TopSites
    {
        // The sites you open most, from visits that are already stored (nothing is looked up). One tile per site; http and https only.
        public static List<TopSite> Pick(IEnumerable<(string Url, string Title)>? visits, int max, ISet<string>? hiddenHosts = null)
        {
            var res = new List<TopSite>();
            if (visits == null || max <= 0) return res;
            var count = new Dictionary<string, int>(StringComparer.Ordinal);
            var last = new Dictionary<string, int>(StringComparer.Ordinal);
            var title = new Dictionary<string, string>(StringComparer.Ordinal);
            int i = 0;
            foreach (var (url, t) in visits)
            {
                i++;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) || string.IsNullOrEmpty(u.Host) || u.UserInfo.Length > 0) continue;
                var host = u.Host.ToLowerInvariant(); if (host.StartsWith("www.")) host = host.Substring(4);
                if (hiddenHosts != null && hiddenHosts.Contains(host)) continue;
                count[host] = count.TryGetValue(host, out var c) ? c + 1 : 1;
                last[host] = i;
                if (!string.IsNullOrWhiteSpace(t) && u.AbsolutePath == "/") title[host] = t.Trim();   // the home page's title names the site best
                else if (!title.ContainsKey(host) && !string.IsNullOrWhiteSpace(t)) title[host] = t.Trim();
            }
            foreach (var host in count.Keys.OrderByDescending(h => count[h]).ThenByDescending(h => last[h]).ThenBy(h => h, StringComparer.Ordinal).Take(max))
                res.Add(new TopSite("https://" + host + "/", host, title.TryGetValue(host, out var tt) ? (tt.Length > 60 ? tt.Substring(0, 60) : tt) : host, count[host]));
            return res;
        }
    }

    internal static class SiteBadge
    {
        public static string Letter(string? host)
        {
            var h = (host ?? "").Trim().ToLowerInvariant(); if (h.StartsWith("www.")) h = h.Substring(4);
            foreach (var c in h) if (char.IsLetterOrDigit(c)) return char.ToUpperInvariant(c).ToString();
            return "?";
        }

        // a stable colour per site, so the same site always looks the same
        public static (byte R, byte G, byte B) Color(string? host)
        {
            uint hash = 2166136261;
            foreach (var c in (host ?? "").ToLowerInvariant()) { hash ^= c; hash *= 16777619; }
            double h = (hash % 360) / 360.0, s = 0.45, l = 0.42;
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            double Ch(double t) { if (t < 0) t += 1; if (t > 1) t -= 1; return t < 1.0 / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p; }
            return ((byte)Math.Round(Ch(h + 1.0 / 3) * 255), (byte)Math.Round(Ch(h) * 255), (byte)Math.Round(Ch(h - 1.0 / 3) * 255));
        }

        public static string CssColor(string? host) { var c = Color(host); return "#" + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2"); }
    }

    internal static class Counts
    {
        // 1234 -> "1,234"; large numbers stay readable on the start page
        public static string Pretty(long n) => (n < 0 ? 0 : n).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
    }

    // Address bar completion: as you type "git", the bar fills in "github.com" (the extra letters are selected, so Backspace removes
    // them and Enter or the right arrow accepts them). The suggestions come from, in order: the sites you visit most, your bookmarks,
    // then a short built-in list of very popular sites. The list is static text inside the program: nothing is looked up or sent.
    internal static class AddressComplete
    {
        public static readonly string[] Popular =
        {
            "google.com", "youtube.com", "facebook.com", "instagram.com", "wikipedia.org", "amazon.com", "x.com", "twitter.com", "reddit.com", "linkedin.com",
            "netflix.com", "github.com", "microsoft.com", "apple.com", "bing.com", "duckduckgo.com", "yahoo.com", "whatsapp.com", "tiktok.com", "twitch.tv",
            "stackoverflow.com", "outlook.com", "office.com", "live.com", "gmail.com", "mail.google.com", "drive.google.com", "docs.google.com", "maps.google.com", "translate.google.com",
            "news.google.com", "calendar.google.com", "photos.google.com", "paypal.com", "ebay.com", "walmart.com", "target.com", "bestbuy.com", "etsy.com", "aliexpress.com",
            "cnn.com", "bbc.com", "nytimes.com", "washingtonpost.com", "theguardian.com", "reuters.com", "apnews.com", "foxnews.com", "forbes.com", "bloomberg.com",
            "weather.com", "imdb.com", "spotify.com", "soundcloud.com", "pinterest.com", "tumblr.com", "quora.com", "medium.com", "discord.com", "slack.com",
            "zoom.us", "dropbox.com", "onedrive.live.com", "icloud.com", "adobe.com", "canva.com", "figma.com", "notion.so", "trello.com", "atlassian.com",
            "salesforce.com", "oracle.com", "ibm.com", "nvidia.com", "amd.com", "intel.com", "samsung.com", "sony.com", "dell.com", "hp.com",
            "mozilla.org", "brave.com", "opera.com", "vivaldi.com", "proton.me", "protonmail.com", "signal.org", "archive.org", "gitlab.com", "bitbucket.org",
            "npmjs.com", "pypi.org", "nuget.org", "docker.com", "kubernetes.io", "python.org", "nodejs.org", "rust-lang.org", "go.dev", "dotnet.microsoft.com",
            "learn.microsoft.com", "developer.mozilla.org", "w3schools.com", "coursera.org", "udemy.com", "khanacademy.org", "edx.org", "duolingo.com", "openai.com", "anthropic.com",
            "claude.ai", "chatgpt.com", "huggingface.co", "kaggle.com", "arxiv.org", "nature.com", "sciencedirect.com", "nih.gov", "who.int", "cdc.gov",
            "irs.gov", "usa.gov", "gov.uk", "europa.eu", "un.org", "nasa.gov", "ups.com", "fedex.com", "usps.com", "dhl.com",
            "chase.com", "bankofamerica.com", "wellsfargo.com", "capitalone.com", "americanexpress.com", "booking.com", "expedia.com", "tripadvisor.com", "airbnb.com", "uber.com",
            "lyft.com", "doordash.com", "yelp.com", "zillow.com", "indeed.com", "glassdoor.com", "craigslist.org", "homedepot.com", "lowes.com", "costco.com",
            "steampowered.com", "epicgames.com", "roblox.com", "minecraft.net", "ea.com", "playstation.com", "xbox.com", "nintendo.com", "gog.com", "itch.io",
            "cloudflare.com", "wordpress.com", "wix.com", "squarespace.com", "shopify.com", "godaddy.com", "namecheap.com", "vercel.com", "netlify.com", "heroku.com"
        };

        private static string HostOf(string url)
        {
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) || u.UserInfo.Length > 0 || string.IsNullOrEmpty(u.Host)) return "";
            var h = u.Host.ToLowerInvariant();
            return h.StartsWith("www.", StringComparison.Ordinal) ? h.Substring(4) : h;
        }

        // visits newest first is not required: counts decide, and the newest of equal counts wins (visits are given oldest first).
        // Returns the whole completed text (what to put in the bar), or null when nothing sensible completes what was typed.
        public static string? Complete(string? typed, IEnumerable<string>? visitedUrls, IEnumerable<string>? bookmarkUrls)
        {
            var t = typed ?? "";
            if (t.Length == 0 || t.Length > 100 || t.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '/' || c == ':' || c == '@' || c == '\\' || c == '?' || c == '#')) return null;
            var lower = t.ToLowerInvariant();
            if (lower.StartsWith("www.", StringComparison.Ordinal)) lower = lower.Substring(4);
            if (lower.Length == 0) return null;

            string? Pick(IEnumerable<string> hosts) => hosts.FirstOrDefault(h => h.Length > lower.Length && h.StartsWith(lower, StringComparison.Ordinal));

            var order = new Dictionary<string, (int Count, int Last)>(StringComparer.Ordinal);
            int i = 0;
            foreach (var u in visitedUrls ?? Array.Empty<string>())
            {
                var h = HostOf(u); i++; if (h.Length == 0) continue;
                order[h] = order.TryGetValue(h, out var o) ? (o.Count + 1, i) : (1, i);
            }
            var visited = order.OrderByDescending(kv => kv.Value.Count).ThenByDescending(kv => kv.Value.Last).Select(kv => kv.Key);
            var hit = Pick(visited);
            if (hit == null) hit = Pick((bookmarkUrls ?? Array.Empty<string>()).Select(HostOf).Where(h => h.Length > 0));
            if (hit == null) hit = Pick(Popular);
            if (hit == null) return null;
            // keep what was typed exactly (including its capitals), add the rest
            var typedNoWww = t.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? t.Substring(4) : t;
            return t.Substring(0, t.Length - typedNoWww.Length) + typedNoWww + hit.Substring(lower.Length);
        }
    }
}
