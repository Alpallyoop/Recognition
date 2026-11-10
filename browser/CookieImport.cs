using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Recognition.Browser
{
    // Importing your own cookies from another browser on this computer, so you stay signed in to sites after switching to
    // Recognition. Pure rules only (no files, no WPF, no DPAPI call: executed by browser.tests). The WPF glue
    // (MainWindow.CookieImport.cs) finds the browsers, copies the locked database, unwraps the key with Windows DPAPI and
    // adds the cookies to the engine; everything it relies on to be correct is here and tested.
    //
    //   * Chromium browsers (Chrome, Edge, Brave): the "Cookies" SQLite database; values are AES-256-GCM, with a key kept in
    //     "Local State" and wrapped by DPAPI for your Windows account. Only your own account can unwrap it.
    //   * Firefox: "cookies.sqlite"; values are stored in the clear for your account.
    //   * A Netscape "cookies.txt" file you exported, as a browser-independent fallback.
    //
    // A cookie is a credential: importing one signs you in to that site. So this never runs on its own; the glue asks first,
    // writes a receipt, and imports only into your normal (non-private) profile.
    internal enum CookieSource { Chromium, Firefox, NetscapeText }

    internal sealed class ImportedCookie
    {
        public string Host = "";          // host_key as stored: a leading "." means a domain cookie
        public string Name = "";
        public string Value = "";
        public string Path = "/";
        public DateTime? ExpiresUtc;      // null = a session cookie (gone when the browser closes)
        public bool Secure;
        public bool HttpOnly;
        public string SameSite = "lax";   // none | lax | strict
        public bool DecryptFailed;        // Chromium value we could not read (e.g. newer app-bound encryption)

        public bool Expired(DateTime nowUtc) => ExpiresUtc is { } e && e <= nowUtc;
        public string DomainKey => Host.TrimStart('.').ToLowerInvariant();
    }

    internal static class CookieImport
    {
        public const long MaxDbBytes = 512L * 1024 * 1024;
        public const int MaxCookies = 200_000;

        // ---- Chromium key handling ---------------------------------------------------------------------------------------

        /// <summary>The wrapped AES key from Local State (base64 "os_crypt.encrypted_key", still DPAPI-wrapped), or null.</summary>
        public static byte[]? KeyBlobFromLocalState(string localStateJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(localStateJson);
                if (doc.RootElement.TryGetProperty("os_crypt", out var oc) && oc.TryGetProperty("encrypted_key", out var ek) && ek.ValueKind == JsonValueKind.String)
                    return Convert.FromBase64String(ek.GetString()!);
            }
            catch { }
            return null;
        }

        /// <summary>Removes the 5-byte "DPAPI" marker in front of the wrapped key; returns the input unchanged if it is absent.</summary>
        public static byte[] StripDpapiPrefix(byte[] blob) =>
            blob.Length >= 5 && blob[0] == (byte)'D' && blob[1] == (byte)'P' && blob[2] == (byte)'A' && blob[3] == (byte)'P' && blob[4] == (byte)'I'
                ? blob.Skip(5).ToArray() : blob;

        /// <summary>
        /// Decrypts one Chromium cookie value. "v10"/"v11" blobs are AES-256-GCM under <paramref name="aesKey"/>; an older blob
        /// has no such prefix and is DPAPI-wrapped, so it is handed to <paramref name="dpapi"/>. Returns null when it cannot be
        /// read (a missing key, or a newer scheme such as "v20" app-bound encryption), so the caller can count and skip it.
        /// </summary>
        public static string? DecryptChromiumValue(byte[] encrypted, byte[]? aesKey, Func<byte[], byte[]?>? dpapi)
        {
            if (encrypted.Length == 0) return "";
            bool v1x = encrypted.Length >= 3 && encrypted[0] == (byte)'v' && encrypted[1] == (byte)'1' && (encrypted[2] == (byte)'0' || encrypted[2] == (byte)'1');
            if (v1x)
            {
                if (aesKey == null || aesKey.Length != 32 || encrypted.Length < 3 + 12 + 16) return null;
                var nonce = new byte[12]; Array.Copy(encrypted, 3, nonce, 0, 12);
                int ctLen = encrypted.Length - 3 - 12 - 16;
                var ct = new byte[ctLen]; Array.Copy(encrypted, 15, ct, 0, ctLen);
                var tag = new byte[16]; Array.Copy(encrypted, encrypted.Length - 16, tag, 0, 16);
                var plain = new byte[ctLen];
                try { using var gcm = new AesGcm(aesKey, 16); gcm.Decrypt(nonce, ct, tag, plain); }
                catch { return null; }
                return Encoding.UTF8.GetString(plain);
            }
            // "v20" (app-bound) and anything else we do not support: let the caller skip it rather than guess
            if (encrypted.Length >= 3 && encrypted[0] == (byte)'v' && encrypted[1] == (byte)'2') return null;
            if (dpapi == null) return null;
            try { var p = dpapi(encrypted); return p == null ? null : Encoding.UTF8.GetString(p); }
            catch { return null; }
        }

        // ---- time and same-site conversions ------------------------------------------------------------------------------

        static readonly DateTime WebkitEpoch = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Chromium expires_utc: microseconds since 1601-01-01. 0 = a session cookie. Out-of-range clamps, never throws.</summary>
        public static DateTime? ChromiumExpiry(long microseconds)
        {
            if (microseconds <= 0) return null;
            try { return WebkitEpoch.AddTicks(microseconds * 10); }     // 10 ticks per microsecond
            catch (ArgumentOutOfRangeException) { return DateTime.MaxValue.ToUniversalTime(); }
        }

        /// <summary>Firefox expiry: seconds since 1970-01-01. 0 or missing = session.</summary>
        public static DateTime? UnixExpiry(long seconds)
        {
            if (seconds <= 0) return null;
            try { return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime; }
            catch { return DateTime.MaxValue.ToUniversalTime(); }
        }

        public static string SameSite(long n) => n switch { 2 => "strict", 1 => "lax", 0 => "none", _ => "lax" };   // -1 unspecified -> lax (the modern default)

        // ---- turning database rows into cookies --------------------------------------------------------------------------

        static long AsLong(object? v) => v switch { long l => l, double d => (long)d, byte[] b when b.Length <= 8 => b.Aggregate(0L, (a, x) => (a << 8) | x), string s when long.TryParse(s, out var p) => p, _ => 0 };
        static string AsText(object? v) => v switch { string s => s, byte[] b => Encoding.UTF8.GetString(b), null => "", _ => v.ToString() ?? "" };
        static byte[] AsBlob(object? v) => v switch { byte[] b => b, string s => Encoding.UTF8.GetBytes(s), _ => Array.Empty<byte>() };

        /// <summary>Reads a Chromium "cookies" table (columns looked up by name, so a changed column order is fine).</summary>
        public static List<ImportedCookie> FromChromium(SqliteTable t, byte[]? aesKey, Func<byte[], byte[]?>? dpapi)
        {
            int cHost = t.ColumnIndex("host_key"), cName = t.ColumnIndex("name"), cEnc = t.ColumnIndex("encrypted_value"),
                cVal = t.ColumnIndex("value"), cPath = t.ColumnIndex("path"), cExp = t.ColumnIndex("expires_utc"),
                cSec = t.ColumnIndex("is_secure"), cHttp = t.ColumnIndex("is_httponly"), cSs = t.ColumnIndex("samesite");
            var outList = new List<ImportedCookie>();
            foreach (var r in t.Rows)
            {
                if (cHost < 0 || cName < 0) break;
                var c = new ImportedCookie
                {
                    Host = AsText(r[cHost]).Trim(),
                    Name = AsText(r[cName]),
                    Path = cPath >= 0 ? Norm(AsText(r[cPath])) : "/",
                    ExpiresUtc = cExp >= 0 ? ChromiumExpiry(AsLong(r[cExp])) : null,
                    Secure = cSec >= 0 && AsLong(r[cSec]) != 0,
                    HttpOnly = cHttp >= 0 && AsLong(r[cHttp]) != 0,
                    SameSite = cSs >= 0 ? SameSite(AsLong(r[cSs])) : "lax",
                };
                string? value = cVal >= 0 ? AsText(r[cVal]) : "";
                if (string.IsNullOrEmpty(value) && cEnc >= 0)
                {
                    var blob = AsBlob(r[cEnc]);
                    if (blob.Length > 0) { value = DecryptChromiumValue(blob, aesKey, dpapi); if (value == null) c.DecryptFailed = true; }
                }
                c.Value = value ?? "";
                outList.Add(c);
                if (outList.Count >= MaxCookies) break;
            }
            return outList;
        }

        /// <summary>Reads a Firefox "moz_cookies" table.</summary>
        public static List<ImportedCookie> FromFirefox(SqliteTable t)
        {
            int cHost = t.ColumnIndex("host"), cName = t.ColumnIndex("name"), cVal = t.ColumnIndex("value"),
                cPath = t.ColumnIndex("path"), cExp = t.ColumnIndex("expiry"), cSec = t.ColumnIndex("isSecure"),
                cHttp = t.ColumnIndex("isHttpOnly"), cSs = t.ColumnIndex("sameSite");
            var outList = new List<ImportedCookie>();
            foreach (var r in t.Rows)
            {
                if (cHost < 0 || cName < 0) break;
                outList.Add(new ImportedCookie
                {
                    Host = AsText(r[cHost]).Trim(),
                    Name = AsText(r[cName]),
                    Value = cVal >= 0 ? AsText(r[cVal]) : "",
                    Path = cPath >= 0 ? Norm(AsText(r[cPath])) : "/",
                    ExpiresUtc = cExp >= 0 ? UnixExpiry(AsLong(r[cExp])) : null,
                    Secure = cSec >= 0 && AsLong(r[cSec]) != 0,
                    HttpOnly = cHttp >= 0 && AsLong(r[cHttp]) != 0,
                    SameSite = cSs >= 0 ? SameSite(AsLong(r[cSs])) : "lax",
                });
                if (outList.Count >= MaxCookies) break;
            }
            return outList;
        }

        /// <summary>Netscape cookies.txt: tab-separated domain, include-subdomains, path, secure, expiry, name, value. "#HttpOnly_" marks a line.</summary>
        public static List<ImportedCookie> FromNetscape(string text)
        {
            var outList = new List<ImportedCookie>();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                bool httpOnly = false;
                if (line.StartsWith("#HttpOnly_", StringComparison.Ordinal)) { httpOnly = true; line = line.Substring("#HttpOnly_".Length); }
                else if (line.StartsWith("#", StringComparison.Ordinal) || line.Trim().Length == 0) continue;
                var f = line.Split('\t');
                if (f.Length < 7) continue;
                outList.Add(new ImportedCookie
                {
                    Host = f[0].Trim(),
                    Path = Norm(f[2]),
                    Secure = f[3].Trim().Equals("TRUE", StringComparison.OrdinalIgnoreCase),
                    ExpiresUtc = long.TryParse(f[4].Trim(), out var ex) ? UnixExpiry(ex) : null,
                    Name = f[5],
                    Value = string.Join("\t", f.Skip(6)),
                    HttpOnly = httpOnly,
                    SameSite = "lax",
                });
                if (outList.Count >= MaxCookies) break;
            }
            return outList;
        }

        static string Norm(string p) { p = (p ?? "").Trim(); return p.Length == 0 ? "/" : (p[0] == '/' ? p : "/" + p); }

        // ---- which cookies may be imported -------------------------------------------------------------------------------

        static bool HostOk(string h)
        {
            h = h.TrimStart('.');
            if (h.Length is 0 or > 253) return false;
            if (h.StartsWith('.') || h.EndsWith('.') || h.Contains("..")) return false;
            return h.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
        }

        /// <summary>True when this cookie is safe to hand to the engine (a real host, a usable name, no control characters, decrypted).</summary>
        public static bool Importable(ImportedCookie c)
        {
            if (c.DecryptFailed) return false;
            if (!HostOk(c.Host)) return false;
            if (c.Name.Length == 0 && c.Value.Length == 0) return false;
            if (c.Name.Length > 4096 || c.Value.Length > 8192) return false;
            if (c.Name.Any(IsBadChar) || c.Value.Any(IsBadChar)) return false;
            if (c.Name.Length > 0 && char.IsWhiteSpace(c.Name[0])) return false;   // the engine rejects a name starting with whitespace
            return true;
        }

        static bool IsBadChar(char c) => c is '\n' or '\r' or '\0' or ';' || char.IsControl(c);

        // ---- sorting cookies into kinds, so you can bring in your sign-ins and skip trackers ------------------------------

        // Known tracker and analytics cookie names (lower-cased). Matched exactly or as a prefix (a name ending in "*").
        static readonly string[] AdNames = { "_fbp", "_fbc", "fr", "ide", "dsid", "anid", "nid", "muid", "mr", "_gcl_*", "test_cookie", "personalization_id", "uuid2", "tuuid", "_ttp", "_pin_unauth", "guest_id_ads" };
        static readonly string[] AnalyticsNames = { "_ga", "_ga_*", "_gid", "_gat", "_gat_*", "__utma", "__utmb", "__utmc", "__utmt", "__utmz", "_hj*", "ajs_*", "mp_*", "amplitude_*", "amp_*", "_pk_*", "_clck", "_clsk", "fs_uid", "optimizelyenduserid", "_uetsid", "_uetvid", "s_cc", "s_sq", "__qca" };
        // Known sign-in / session cookie names.
        static readonly string[] SignInNames = { "sid", "sessionid", "session_id", "session", "sess", "phpsessid", "jsessionid", "asp.net_sessionid", "connect.sid", "auth", "authtoken", "auth_token", "access_token", "refresh_token", "id_token", "jwt", "token", "login", "logged_in", "remember", "remember_token", "rememberme", "csrftoken", "csrf_token", "xsrf-token", "_csrf", "c_user", "xs", "li_at", "ds_user_id", "wordpress_logged_in_*", "wordpress_sec_*" };
        static readonly string[] PrefNames = { "lang", "language", "locale", "tz", "timezone", "theme", "darkmode", "pref", "prefs", "preferences", "consent", "cookieconsent", "cookie_consent", "gdpr", "ccpa", "euconsent", "euconsent-v2", "currency", "country", "region" };

        /// <summary>What a cookie is for, worked out from its name (and, as a last hint, its flags). Used to group the import and leave trackers out by default.</summary>
        public static CookieCategory Classify(ImportedCookie c)
        {
            var n = c.Name.ToLowerInvariant();
            if (MatchAny(n, AdNames)) return CookieCategory.Advertising;
            if (MatchAny(n, AnalyticsNames)) return CookieCategory.Analytics;
            if (n.StartsWith("__secure-") || n.StartsWith("__host-")) return CookieCategory.SignIn;   // these prefixes are reserved for secure/host-bound cookies, almost always sign-in
            if (MatchAny(n, SignInNames) || n.Contains("session") || n.Contains("token") || n.Contains("auth")) return CookieCategory.SignIn;
            if (MatchAny(n, PrefNames)) return CookieCategory.Preferences;
            if (c.HttpOnly && c.Secure) return CookieCategory.SignIn;   // a site kept it server-only and https-only: treat it as a sign-in, not a tracker
            return CookieCategory.Other;
        }

        static bool MatchAny(string name, string[] table)
        {
            foreach (var t in table)
            {
                if (t.EndsWith("*", StringComparison.Ordinal)) { if (name.StartsWith(t[..^1], StringComparison.Ordinal)) return true; }
                else if (name == t) return true;
            }
            return false;
        }

        public static string CategoryKey(CookieCategory c) => c switch
        {
            CookieCategory.SignIn => "signin", CookieCategory.Preferences => "prefs",
            CookieCategory.Analytics => "analytics", CookieCategory.Advertising => "ads", _ => "other"
        };
        public static string CategoryLabel(CookieCategory c) => c switch
        {
            CookieCategory.SignIn => "Sign-ins and sessions", CookieCategory.Preferences => "Preferences and settings",
            CookieCategory.Analytics => "Analytics", CookieCategory.Advertising => "Advertising", _ => "Other"
        };
        // On by default: what keeps you signed in and keeps your settings. Off by default: trackers you gain nothing by carrying over.
        public static bool OnByDefault(CookieCategory c) => c is CookieCategory.SignIn or CookieCategory.Preferences or CookieCategory.Other;

        public static readonly CookieCategory[] AllCategories = { CookieCategory.SignIn, CookieCategory.Preferences, CookieCategory.Analytics, CookieCategory.Advertising, CookieCategory.Other };

        /// <summary>The live, importable cookies whose category is in <paramref name="categoryKeys"/>.</summary>
        public static List<ImportedCookie> Selected(IEnumerable<ImportedCookie> cookies, DateTime nowUtc, ISet<string> categoryKeys) =>
            cookies.Where(c => Importable(c) && !c.Expired(nowUtc) && categoryKeys.Contains(CategoryKey(Classify(c)))).ToList();

        /// <summary>A summary of a source: totals, and the live importable count in each category.</summary>
        public static CookieSummary Summarize(IEnumerable<ImportedCookie> cookies, DateTime nowUtc)
        {
            int total = 0, live = 0, failed = 0; var sites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var byCat = new Dictionary<string, int>();
            foreach (var c in cookies)
            {
                total++;
                if (c.DecryptFailed) { failed++; continue; }
                if (!Importable(c)) continue;
                sites.Add(c.DomainKey);
                if (c.Expired(nowUtc)) continue;
                live++;
                var k = CategoryKey(Classify(c));
                byCat[k] = byCat.TryGetValue(k, out var v) ? v + 1 : 1;
            }
            return new CookieSummary(total, sites.Count, live, failed, byCat);
        }

        public static string TopSites(IEnumerable<ImportedCookie> cookies, int n)
        {
            var top = cookies.Where(c => !c.DecryptFailed && Importable(c)).GroupBy(c => c.DomainKey)
                .OrderByDescending(g => g.Count()).Take(n).Select(g => g.Key);
            return string.Join(", ", top);
        }
    }

    internal enum CookieCategory { SignIn, Preferences, Analytics, Advertising, Other }

    internal readonly record struct CookieSummary(int Total, int Sites, int Live, int Failed, IReadOnlyDictionary<string, int> ByCategory)
    {
        public int Cat(string key) => ByCategory != null && ByCategory.TryGetValue(key, out var v) ? v : 0;
    }
}
