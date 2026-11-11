using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Recognition.Browser
{
    // "Export for another device" carries more than the Setup snapshot used to (proposal 0011): on top of appearance, preferences,
    // bookmarks and site decisions, it can also carry your saved logins, your live sign-in cookies, and a list of the extensions and
    // themes you kept. These three categories are the credential-bearing / heavier ones, so their serialize-and-re-validate rules live
    // here as pure logic (no WPF, no WebView2, no files) and are executed directly by browser.tests.
    //
    // Everything that comes back out of a bundle is treated as hostile and re-validated, exactly like the rest of SetupSnapshot:
    //   * logins  — origin must normalise to an https origin, fields are length-capped and stripped of control characters, an empty
    //               password is dropped. The import adds them to the vault; it never overwrites a different saved password.
    //   * cookies — re-validated with the same CookieImport.Importable rule the browser-to-browser import uses, and expired ones are
    //               dropped. The import writes them to the live cookie store (additive), exactly like importing from another browser.
    //   * kept    — carried as a LIST only (what you had + where it came from). Extensions and themes are NOT smuggled as bytes: on the
    //               other device they are re-installed through that device's own governance. So this category is informational: it is
    //               shown on preview and never silently applied.
    //
    // The bundle itself is sealed by SetupBlob (AES-256-GCM, key = PBKDF2 of the one-time code), so these plaintext fields only ever
    // exist inside the encrypted envelope. The honest limits in SetupSnapshot.cs apply here too: anyone holding BOTH the file and the
    // code can read it, so a bundle that carries credentials is treated as a password and kept to a short expiry by the caller.

    // ---- saved logins -------------------------------------------------------------------------------------------------------
    internal readonly record struct BundleLogin(string Origin, string Username, string Password, string Note);

    internal static class LoginRules
    {
        public const int Max = 10_000;

        // The category value (a JSON array) for the bundle. Only entries whose origin normalises and whose password is non-empty are
        // carried; everything is length-capped so a hand-edited vault cannot bloat the bundle.
        public static List<object> Serialize(IEnumerable<BundleLogin> logins)
        {
            var res = new List<object>();
            foreach (var l in logins ?? Array.Empty<BundleLogin>())
            {
                if (res.Count >= Max) break;
                var origin = PasswordRules.NormalizeOrigin(l.Origin);
                if (origin == null) continue;
                if (string.IsNullOrEmpty(l.Password) || !PasswordRules.CleanField(l.Password, 4096)) continue;
                var user = PasswordRules.CleanField(l.Username, 1024) ? l.Username : "";
                var note = PasswordRules.CleanField(l.Note, 4096, allowNewline: true) ? l.Note : "";
                res.Add(new { origin, username = user, password = l.Password, note });
            }
            return res;
        }

        // Re-validate a logins category coming out of a bundle. Invalid rows are dropped and counted. Never throws.
        public static List<BundleLogin> Parse(JsonElement arr, out int rejected)
        {
            rejected = 0;
            var res = new List<BundleLogin>();
            if (arr.ValueKind != JsonValueKind.Array) return res;
            foreach (var e in arr.EnumerateArray())
            {
                if (res.Count >= Max) break;
                if (e.ValueKind != JsonValueKind.Object) { rejected++; continue; }
                string S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                var origin = PasswordRules.NormalizeOrigin(S("origin"));
                var pass = S("password");
                var user = S("username");
                var note = S("note");
                if (origin == null || string.IsNullOrEmpty(pass) || !PasswordRules.CleanField(pass, 4096)
                    || !PasswordRules.CleanField(user, 1024) || !PasswordRules.CleanField(note, 4096, allowNewline: true))
                { rejected++; continue; }
                res.Add(new BundleLogin(origin, user, pass, note));
            }
            return res;
        }

        // Which logins a bundle actually adds to a vault that already holds <existing>. A row whose (origin, username, password) is
        // already saved is a duplicate and skipped; a same (origin, username) with a DIFFERENT password is NOT an update here (the
        // bundle never overwrites a password you have) — it is added as a separate entry so nothing you rely on is lost.
        public static List<BundleLogin> ToAdd(IEnumerable<BundleLogin> incoming, IEnumerable<(string Origin, string Username, string Password)> existing, out int duplicates)
        {
            duplicates = 0;
            var have = new HashSet<string>(StringComparer.Ordinal);
            foreach (var x in existing ?? Array.Empty<(string, string, string)>())
            {
                var o = PasswordRules.NormalizeOrigin(x.Origin) ?? x.Origin;
                have.Add(o + "\n" + x.Username + "\n" + x.Password);
            }
            var res = new List<BundleLogin>();
            foreach (var l in incoming ?? Array.Empty<BundleLogin>())
            {
                var key = l.Origin + "\n" + l.Username + "\n" + l.Password;
                if (!have.Add(key)) { duplicates++; continue; }   // Add() returns false if already present (either in the vault or earlier in this bundle)
                res.Add(l);
            }
            return res;
        }
    }

    // ---- live cookies -------------------------------------------------------------------------------------------------------
    internal static class CookieBundleRules
    {
        public const int Max = CookieImport.MaxCookies;   // same safety bound as the browser-to-browser import

        public static List<object> Serialize(IEnumerable<ImportedCookie> cookies, DateTime nowUtc)
        {
            var res = new List<object>();
            foreach (var c in cookies ?? Array.Empty<ImportedCookie>())
            {
                if (res.Count >= Max) break;
                if (!CookieImport.Importable(c) || c.Expired(nowUtc)) continue;
                res.Add(new
                {
                    host = c.Host,
                    name = c.Name,
                    value = c.Value,
                    path = c.Path,
                    secure = c.Secure,
                    http_only = c.HttpOnly,
                    samesite = c.SameSite,
                    expires = c.ExpiresUtc is { } ex ? ex.ToUniversalTime().ToString("o") : null
                });
            }
            return res;
        }

        // Re-validate a cookies category coming out of a bundle. Invalid or already-expired rows are dropped and counted.
        public static List<ImportedCookie> Parse(JsonElement arr, DateTime nowUtc, out int rejected)
        {
            rejected = 0;
            var res = new List<ImportedCookie>();
            if (arr.ValueKind != JsonValueKind.Array) return res;
            foreach (var e in arr.EnumerateArray())
            {
                if (res.Count >= Max) break;
                if (e.ValueKind != JsonValueKind.Object) { rejected++; continue; }
                string S(string k, string dflt = "") => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? dflt : dflt;
                bool B(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
                var ss = S("samesite", "lax").ToLowerInvariant();
                if (ss != "none" && ss != "lax" && ss != "strict") ss = "lax";
                DateTime? exp = null;
                if (e.TryGetProperty("expires", out var ev) && ev.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(ev.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var pd)) exp = pd.ToUniversalTime();
                var path = S("path", "/");
                if (path.Length == 0 || path[0] != '/' || path.Length > 1024 || path.Any(char.IsControl)) path = "/";
                var c = new ImportedCookie
                {
                    Host = S("host"),
                    Name = S("name"),
                    Value = S("value"),
                    Path = path,
                    Secure = B("secure"),
                    HttpOnly = B("http_only"),
                    SameSite = ss,
                    ExpiresUtc = exp
                };
                if (!CookieImport.Importable(c) || c.Expired(nowUtc)) { rejected++; continue; }
                res.Add(c);
            }
            return res;
        }
    }

    // ---- kept library (extensions / themes) — informational manifest only --------------------------------------------------
    internal readonly record struct KeptManifestItem(string Kind, string Id, string Name, string Version, string Source);

    internal static class KeptManifestRules
    {
        public const int Max = 20_000;

        static bool Clean(string s, int max) => s.Length <= max && !s.Any(char.IsControl);
        static string Trim(string? s, int max) { var t = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()); return t.Length > max ? t.Substring(0, max) : t; }

        // Only the things you still have (not the Removed ones) are carried, as metadata the other device shows and re-installs itself.
        public static List<object> Serialize(IEnumerable<KeptManifestItem> items)
        {
            var res = new List<object>();
            foreach (var i in items ?? Array.Empty<KeptManifestItem>())
            {
                if (res.Count >= Max) break;
                if (!Kept.Kinds.Contains(i.Kind) || !Kept.ValidId(i.Kind, i.Id)) continue;
                res.Add(new { kind = i.Kind, id = i.Id, name = Trim(i.Name, 120), version = Trim(i.Version, 64), source = Trim(i.Source, 200) });
            }
            return res;
        }

        public static List<KeptManifestItem> Parse(JsonElement arr, out int rejected)
        {
            rejected = 0;
            var res = new List<KeptManifestItem>();
            if (arr.ValueKind != JsonValueKind.Array) return res;
            foreach (var e in arr.EnumerateArray())
            {
                if (res.Count >= Max) break;
                if (e.ValueKind != JsonValueKind.Object) { rejected++; continue; }
                string S(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                var kind = S("kind"); var id = S("id");
                if (!Kept.Kinds.Contains(kind) || !Kept.ValidId(kind, id)) { rejected++; continue; }
                var name = S("name"); var version = S("version"); var source = S("source");
                if (!Clean(name, 120) || !Clean(version, 64) || !Clean(source, 200)) { rejected++; continue; }
                res.Add(new KeptManifestItem(kind, id, name, version, source));
            }
            return res;
        }

        // A re-installable source is an https store/library address; a local/uploaded source cannot be reconstructed on the other
        // device from a text bundle, so it is shown as "carry the files yourself".
        public static bool HasReinstallSource(KeptManifestItem i) =>
            i.Source.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && SetupValidate.IsSafeUrl(i.Source);
    }
}
