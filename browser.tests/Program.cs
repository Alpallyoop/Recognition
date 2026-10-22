using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Recognition.Browser;

// Executes the REAL GovernedActions (same source file as the browser). Golden hashes were derived
// independently (Python re-implementation of the record format) and match the PowerShell mirror
// used by _selftest_recognition_action_receipts_v1.ps1, so C# == PS == Python for the chain format.

int pass = 0, fail = 0;
void Check(bool c, string label)
{
    if (c) { pass++; Console.WriteLine("  ok  - " + label); }
    else { fail++; Console.WriteLine("  FAIL- " + label); }
}

var store = new Dictionary<string, string>();
string Read(string p) => store.TryGetValue(p, out var v) ? v : "";
void Write(string p, string t) => store[p] = t;
GovernedActions Fresh(string path = "mem://actions") => new GovernedActions(path, Read, Write);
DateTime T(int ms) => new DateTime(2026, 1, 1, 0, 0, 0, ms, DateTimeKind.Utc);

var golden = new[]
{
    "91f29ead6e10d25d011444e7d862af9e7e14c4ff76ac9f967156b91ae031954e",
    "8dc7214854dff357299bb4d9b59a88d0667fded37fafb8ee97ac262bfb1207eb",
    "e0e9f334f83a35e01b9f969e3f2d7ec031e6b837d467ba0ce79ca747ef71ca6f",
    "35615956e86146cf7aeb6cf5e64c52055b199a8284a95ce3705831e7a24f6291",
};

Console.WriteLine("=== GovernedActions (executed C#) ===");

var g = Fresh();
g.AppendAt("session.start", "", T(1));
g.AppendAt("navigate", "https://example.com/a", T(2));
g.AppendAt("vpn.pick", "socks5://127.0.0.1:9050", T(3));
g.AppendAt("quote\"back\\slash|x", "detail \"q\" \\ end", T(4));   // exercises JSON escaping + the '|' used by site-policy records

for (int i = 0; i < 4; i++) Check(g.Items[i].Hash == golden[i], $"record {i + 1} hash equals the independently derived golden vector");
Check(g.Head == golden[3], "head hash equals the golden head");
Check(g.Verify(out int vcount) && vcount == 4, "4-record chain verifies");

var persisted = store["mem://actions"];
Check(!persisted.Contains("example.com/a") && !persisted.Contains("9050"), "cleartext detail (URL, proxy) is absent from the persisted ledger");

var reloaded = Fresh(); reloaded.Load();
Check(reloaded.Count == 4 && reloaded.Head == golden[3] && reloaded.Verify(out int rcount) && rcount == 4, "reload from storage reproduces the same chain and head");
reloaded.AppendAt("after.reload", "x", T(5));
Check(reloaded.Verify(out _) && reloaded.Count == 5, "appending after a reload keeps the chain valid");

// helpers to build a tampered store from the persisted lines
string[] Lines() => persisted.Split('\n', StringSplitOptions.RemoveEmptyEntries);
bool VerifiesAfter(Func<string[], string[]> mutate)
{
    var s2 = new Dictionary<string, string> { ["p"] = string.Join("\n", mutate(Lines())) + "\n" };
    var x = new GovernedActions("p", p => s2.TryGetValue(p, out var v) ? v : "", (p, t) => s2[p] = t);
    x.Load();
    return x.Verify(out _);
}

Check(!VerifiesAfter(l => { var c = (string[])l.Clone(); c[1] = c[1].Replace("navigate", "evil.exfil"); return c; }), "tampered action content fails verification");
Check(!VerifiesAfter(l => new[] { l[1], l[0], l[2], l[3] }), "reordered records fail verification");
Check(!VerifiesAfter(l => { var c = (string[])l.Clone(); c[2] = System.Text.RegularExpressions.Regex.Replace(c[2], "\"hash\":\"[0-9a-f]{64}\"", "\"hash\":\"" + new string('0', 64) + "\""); return c; }), "forged hash fails verification");
Check(!VerifiesAfter(l => new[] { l[0], l[2], l[3] }), "deleted middle record fails verification");
Check(!VerifiesAfter(l => new[] { l[0], l[1], l[2] }.Concat(new[] { l[3].Replace("\"seq\":4", "\"seq\":9") }).ToArray()), "non-contiguous sequence number fails verification");
Check(VerifiesAfter(l => l), "control: the unmodified lines still verify (the negative vectors are not trivially failing)");

var empty = Fresh("mem://empty"); empty.Load();
Check(empty.Count == 0 && empty.Verify(out int ec) && ec == 0 && empty.Head == new string('0', 64), "empty ledger verifies with the zero head");

// Site-policy / certificate-trust records are ordinary GovernedActions records whose ACTION encodes the decision.
var sp = Fresh("mem://site_policy");
sp.AppendAt("site_policy.set|perm.Camera|example.com|allow", "", T(1));
sp.AppendAt("site_policy.set|perm.Camera|example.com|deny", "", T(2));
Check(sp.Verify(out _) && sp.Items[^1].Action == "site_policy.set|perm.Camera|example.com|deny", "decision records round-trip with their action text intact");

Console.WriteLine();
Console.WriteLine("=== AppearanceSettings (executed C#) ===");

var ap = new AppearanceSettings();
Check(ap.PageCss() == "" && ap.PreferredScheme() == 0 && ap.InternalPageCss() == "", "defaults inject nothing and follow the system scheme");
Check(ap.Set("theme", "dark") && ap.PreferredScheme() == 2, "dark theme asks sites for dark");
Check(ap.Set("theme", "light") && ap.PreferredScheme() == 1 && ap.InternalPageCss().Contains("#f4f5f7"), "light theme restyles internal pages and asks sites for light");
Check(ap.Set("darkstyle", "prefer") && ap.PreferredScheme() == 2 && ap.PageCss() == "", "'prefer dark' only changes the requested scheme, injects no CSS");
Check(ap.Set("darkstyle", "invert") && ap.PageCss().Contains("invert(1)") && ap.PageCss().Contains("img,video"), "smart invert darkens pages and protects media");
Check(ap.Set("darkstyle", "custom") && ap.Set("bg", "#112233") && ap.Set("text", "#EEEEEE") && ap.Set("link", "#00ff88"), "custom colours accepted (#rrggbb)");
var css = ap.PageCss();
Check(css.Contains("#112233") && css.Contains("#eeeeee") && css.Contains("#00ff88"), "custom CSS carries the chosen colours (normalised to lower case)");

// injection / validation negatives: nothing but #rrggbb colours and allowlisted fonts may reach the CSS
var before = ap.ToJson();
Check(!ap.Set("bg", "red;}body{display:none"), "CSS-injection attempt in a colour is rejected");
Check(!ap.Set("bg", "#12345"), "short hex is rejected");
Check(!ap.Set("bg", "#12345g"), "non-hex digit is rejected");
Check(!ap.Set("text", "url(javascript:alert(1))"), "url() in a colour is rejected");
Check(!ap.Set("font", "Arial';}*{display:none}/*"), "font outside the allowlist is rejected");
Check(!ap.Set("theme", "purple") && !ap.Set("darkstyle", "x") && !ap.Set("overridefonts", "maybe") && !ap.Set("nonsense", "1"), "unknown theme / style / toggle / key is rejected");
Check(ap.ToJson() == before, "rejected changes leave the settings byte-identical");

Check(ap.Set("font", "Georgia") && ap.Set("overridefonts", "on") && ap.PageCss().Contains("'Georgia', sans-serif"), "allowlisted font + override applies to pages");
Check(ap.InternalPageCss().Contains("'Georgia'"), "chosen font also applies to internal pages");
Check(ap.Set("overridefonts", "off") && !ap.PageCss().Contains("Georgia"), "override off removes the font from pages");

var js = ap.InjectScript();
Check(js.Contains("__rec_style") && js.Contains("rec-internal") && js.Contains("DOMContentLoaded"), "inject script is idempotent, skips internal pages, waits for the DOM");
Check(!js.Contains("\n"), "inject script is a single line (safe to embed)");
var clear = new AppearanceSettings().InjectScript();
Check(clear.Contains("var css='';"), "with nothing to apply the script carries an empty stylesheet and just removes the style element");

// JSON round-trip, including hostile persisted values being ignored
using (var doc = JsonDocument.Parse(ap.ToJson()))
{
    var back = new AppearanceSettings(); back.FromJson(doc.RootElement);
    Check(back.ToJson() == ap.ToJson(), "settings survive a JSON round-trip");
}
using (var doc = JsonDocument.Parse("{\"theme\":\"dark\",\"page_bg\":\"red;}body{display:none\",\"font\":\"Evil'Font\",\"dark_style\":\"custom\"}"))
{
    var hostile = new AppearanceSettings(); hostile.FromJson(doc.RootElement);
    Check(hostile.Theme == "dark" && hostile.DarkStyle == "custom" && hostile.PageBg == "#1b1d22" && hostile.Font == "default", "a tampered settings file keeps valid fields and ignores the malicious ones");
}
ap.Reset();
Check(ap.PageCss() == "" && ap.Theme == "system" && ap.Font == "default", "reset restores defaults");

Console.WriteLine();
Console.WriteLine("=== Network panel logic (executed C#) ===");

var profilesOut = "\r\nProfiles on interface Wi-Fi:\r\n\r\nGroup policy profiles (read only)\r\n---------------------------------\r\n    <None>\r\n\r\nUser profiles\r\n-------------\r\n    All User Profile     : HomeNet\r\n    All User Profile     : Coffee Shop : Guest\r\n    User Profile         : Office-5G\r\n    All User Profile     : HomeNet\r\n";
var profiles = NetParsers.ParseWlanProfiles(profilesOut);
Check(profiles.Count == 3 && profiles[0] == "HomeNet" && profiles[1] == "Coffee Shop : Guest" && profiles[2] == "Office-5G", "saved networks parsed (names with spaces/colons kept, duplicates removed)");
Check(NetParsers.ParseWlanProfiles("").Count == 0 && NetParsers.ParseWlanProfiles("There is no wireless interface on the system.").Count == 0, "no profiles / no wireless interface parses to an empty list");

var ifOut = "There is 1 interface on the system:\r\n\r\n    Name                   : Wi-Fi\r\n    Description            : Intel(R) Wi-Fi 6 AX201\r\n    Physical address       : aa:bb:cc:dd:ee:ff\r\n    State                  : connected\r\n    SSID                   : HomeNet\r\n    BSSID                  : 11:22:33:44:55:66\r\n    Radio type             : 802.11ax\r\n    Authentication         : WPA2-Personal\r\n    Channel                : 36\r\n    Receive rate (Mbps)    : 866.7\r\n    Transmit rate (Mbps)   : 700\r\n    Signal                 : 94%\r\n    Profile                : HomeNet\r\n\r\n    Name                   : Wi-Fi 2\r\n    State                  : disconnected\r\n";
var wi = NetParsers.ParseWlanInterface(ifOut);
Check(wi["State"] == "connected" && wi["SSID"] == "HomeNet" && wi["BSSID"] == "11:22:33:44:55:66" && wi["Signal"] == "94%", "wireless interface fields parsed (colon-containing BSSID intact)");
Check(wi["Receive rate (Mbps)"] == "866.7" && wi["Transmit rate (Mbps)"] == "700" && wi["Channel"] == "36" && wi["Radio type"] == "802.11ax", "link rates / channel / radio parsed");
Check(wi["Name"] == "Wi-Fi", "only the first interface block is used");
Check(NetParsers.ParseWlanInterface("The Wireless AutoConfig Service (wlansvc) is not running.").Count == 0, "unparseable output yields no fields");

Check(NetParsers.SsidKey("HomeNet") == NetParsers.SsidKey("HomeNet") && NetParsers.SsidKey("HomeNet") != NetParsers.SsidKey("homenet"), "ssid key is deterministic and case-sensitive");
Check(!NetParsers.SsidKey("a|b\"c").Contains("|") && NetParsers.SsidKey("x").StartsWith("net-") && NetParsers.SsidKey("x").Length == 20, "ssid key never contains record separators");
Check(NetParsers.IsSafeNetworkName("Coffee Shop : Guest") && !NetParsers.IsSafeNetworkName("") && !NetParsers.IsSafeNetworkName("a\nb") && !NetParsers.IsSafeNetworkName(new string('x', 256)), "network-name validator: ok / empty / control char / too long");
Check(NetParsers.IsSafeHost("example.com") && NetParsers.IsSafeHost("8.8.8.8") && NetParsers.IsSafeHost("fe80::1") && NetParsers.IsSafeHost("my-host.local"), "host validator accepts hostnames and IP literals");
Check(!NetParsers.IsSafeHost("8.8.8.8; calc") && !NetParsers.IsSafeHost("a b") && !NetParsers.IsSafeHost("host&whoami") && !NetParsers.IsSafeHost("-bad.com") && !NetParsers.IsSafeHost("") && !NetParsers.IsSafeHost("a|b"), "host validator rejects shell metacharacters, spaces, leading hyphen, empty");

var rc = new RateCalc();
var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
var r0 = rc.Sample(1000, 500, t0);
var r1 = rc.Sample(3000, 1500, t0.AddSeconds(2));
var r2 = rc.Sample(100, 100, t0.AddSeconds(3));   // counters went backwards (adapter reset)
var r3 = rc.Sample(1100, 600, t0.AddSeconds(4));
Check(r0 == (0, 0), "first sample has no baseline, so the rate is 0");
Check(r1 == (1000, 500), "2000 bytes rx / 1000 bytes tx over 2 s = 1000 / 500 B/s");
Check(r2 == (0, 0), "a counter that goes backwards gives 0, not a huge bogus rate");
Check(r3 == (1000, 500), "rates resume correctly after a counter reset");

var ps = PingStats.Summarize(new long?[] { 10, 20, null, 40 });
Check(ps.Sent == 4 && ps.Received == 3 && ps.LossPct == 25 && ps.Min == 10 && ps.Max == 40 && Math.Abs(ps.Avg - 23.3) < 0.001, "ping summary: sent/received/loss/min/avg/max");
Check(Math.Abs(ps.Jitter - 15) < 0.001, "jitter is the mean absolute difference of consecutive replies (|20-10|,|40-20| -> 15)");
var allLost = PingStats.Summarize(new long?[] { null, null });
Check(allLost.Received == 0 && allLost.LossPct == 100 && allLost.Avg == 0, "all packets lost -> 100% loss, no bogus averages");
Check(PingStats.Summarize(Array.Empty<long?>()).LossPct == 0, "no pings sent -> 0% (no divide-by-zero)");

var ev = new List<(DateTime, string)>
{
    (t0, "net.connect|HomeNet"),
    (t0.AddSeconds(100), "net.disconnect|HomeNet"),
    (t0.AddSeconds(200), "net.connect|Cafe"),
    (t0.AddSeconds(260), "net.connect|HomeNet"),            // Cafe disconnect was missed: closed at the next connect
    (t0.AddSeconds(300), "net.disconnect|OtherNet"),         // unrelated disconnect is ignored
};
var hist = NetHistory.Summarize(ev, t0.AddSeconds(400));
var home = hist.First(h => h.Ssid == "HomeNet"); var cafe = hist.First(h => h.Ssid == "Cafe");
Check(home.Connections == 2 && Math.Abs(home.TotalSeconds - 240) < 0.01, "HomeNet: 2 connections, 100 s + 140 s still-open session = 240 s");
Check(cafe.Connections == 1 && Math.Abs(cafe.TotalSeconds - 60) < 0.01, "Cafe: a missed disconnect is closed at the next connect (60 s)");
Check(hist[0].Ssid == "HomeNet" && NetHistory.Summarize(new List<(DateTime, string)>(), t0).Count == 0, "history is ordered by time connected; empty history is empty");

Console.WriteLine();
Console.WriteLine("=== Setup snapshot (executed C#) ===");
{
    var now = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
    var code = SetupCode.Generate();
    Check(Regex.IsMatch(code, "^[0-9A-HJKMNP-TV-Z]{5}(-[0-9A-HJKMNP-TV-Z]{5}){3}$"), "generated code is 20 Crockford-base32 symbols in 4 groups");
    Check(Enumerable.Range(0, 50).Select(_ => SetupCode.Generate()).Distinct().Count() == 50, "50 generated codes are all distinct");
    Check(SetupCode.Normalize("abcde-fghjk mnpqr-stvwx") == "ABCDEFGHJKMNPQRSTVWX", "normalize: case, spaces and hyphens are ignored");
    Check(SetupCode.Normalize("0O0O0-1I1L1-00000-00000") == "00000" + "11111" + "00000" + "00000","normalize: O->0 and I/L->1 corrections");
    Check(SetupCode.Normalize("ABCDE-FGHJK-MNPQR-STVW") == "" && SetupCode.Normalize("ABCDE-FGHJK-MNPQR-STVWU") == "" && SetupCode.Normalize(null) == "", "normalize: wrong length / illegal symbol (U) / null rejected");

    var cats = new Dictionary<string, object>
    {
        ["appearance"] = new { theme = "dark", dark_style = "custom", page_bg = "#101010", page_text = "#eeeeee", link_color = "#88aaff", font = "Georgia", override_site_fonts = true },
        ["preferences"] = new { home_url = "https://example.com/", blocking_enabled = false },
        ["bookmarks"] = new object[] { new { url = "https://a.example/x", title = "A" }, new { url = "javascript:alert(1)", title = "evil" }, new { url = "file:///c:/x", title = "f" }, new { url = "https://b.example/", title = "B\u0001\u0002" } },
        ["site_permissions"] = new object[] { new { key = "perm.Camera", origin = "meet.example", value = "allow" }, new { key = "perm.Camera", origin = "x;y", value = "allow" }, new { key = "perm.Camera", origin = "ok.example", value = "maybe" }, new { key = "cert.der-0123456789abcdef", origin = "h.example", value = "allow" } },
        ["network_labels"] = new object[] { new { key = "netlabel", origin = "net-0123456789abcdef", value = "trusted" }, new { key = "netlabel", origin = "HomeNet", value = "trusted" } },
        ["cert_trust"] = new object[] { new { key = "cert.der-0123456789abcdef0123456789abcdef", origin = "h.example", value = "allow" }, new { key = "cert.der-zz", origin = "h.example", value = "allow" } },
        ["mystery"] = new { a = 1 },
    };
    var payload = SetupPayload.Build("0123456789abcdef01234567", now, now.AddHours(1), cats);
    var blob = SetupBlob.Seal(payload, code, 100_000);
    Check(blob.StartsWith("RSETUP1:") && !blob.Contains("example.com") && !blob.Contains("Georgia"), "sealed blob is prefixed and leaks no plaintext");
    var ok = SetupBlob.Open(blob, code, now.AddMinutes(5));
    Check(ok.Ok && ok.SnapshotId == "0123456789abcdef01234567" && ok.PayloadJson == payload, "round trip: correct code opens and returns the exact payload");
    Check(SetupBlob.Open(blob, code.ToLowerInvariant().Replace("-", " "), now).Ok, "code typed in lower case with spaces still opens");
    Check(SetupBlob.Seal(payload, code, 100_000) != blob, "sealing twice gives different ciphertext (fresh salt and nonce)");
    var wrong = SetupBlob.Open(blob, "AAAAA-AAAAA-AAAAA-AAAAA", now);
    Check(!wrong.Ok && wrong.Reason.Contains("wrong code") && wrong.PayloadJson == "", "wrong code fails and reveals nothing");
    Check(!SetupBlob.Open(blob, "short", now).Ok && !SetupBlob.Open("hello", code, now).Ok && !SetupBlob.Open("", code, now).Ok && !SetupBlob.Open(null!, code, now).Ok, "malformed code / non-snapshot / empty / null never throw, all fail");
    var exp = SetupBlob.Open(blob, code, now.AddHours(1).AddSeconds(1));
    Check(!exp.Ok && exp.Reason.Contains("expired"), "expired snapshot is refused");
    Check(SetupBlob.Open(blob, code, now.AddHours(1)).Ok, "snapshot is still valid exactly at its expiry instant");

    // tamper: flip one character at several positions of the base64 body -> every one must fail authentication/parsing
    var body = blob.Substring("RSETUP1:".Length);
    string Rewrap(string env) => "RSETUP1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(env)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    var envJson = Encoding.UTF8.GetString(Convert.FromBase64String(body.Replace('-', '+').Replace('_', '/').PadRight((body.Length + 3) / 4 * 4, '=')));
    int tamperFail = 0, tamperTotal = 0;
    foreach (var field in new[] { "ct", "tag", "nonce", "salt" })
    {
        var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(envJson)!;
        var raw = Convert.FromBase64String(d[field].GetString()!);
        raw[0] ^= 0x01;
        var m = d.ToDictionary(kv => kv.Key, kv => (object)(kv.Key == field ? Convert.ToBase64String(raw) : kv.Value.ValueKind == JsonValueKind.Number ? kv.Value.GetInt32() : kv.Value.GetString()!));
        var tampered = JsonSerializer.Serialize(m);
        tamperTotal++; if (!SetupBlob.Open(Rewrap(tampered), code, now).Ok) tamperFail++;
    }
    Check(tamperFail == tamperTotal, $"flipping one bit in ciphertext, tag, nonce or salt is detected ({tamperFail}/{tamperTotal})");

    // header downgrade: rewrite iter in the envelope (bound as associated data) -> must fail
    var downgraded = envJson.Replace("\"iter\":100000", "\"iter\":100001");
    var dgBlob = Rewrap(downgraded);
    Check(downgraded != envJson && !SetupBlob.Open(dgBlob, code, now).Ok, "changing the KDF iteration count in the header fails (header is authenticated)");
    var weak = envJson.Replace("\"iter\":100000", "\"iter\":1");
    var weakBlob = Rewrap(weak);
    var weakRes = SetupBlob.Open(weakBlob, code, now);
    Check(!weakRes.Ok && weakRes.Reason.Contains("key-derivation"), "absurdly weak or huge iteration counts are rejected before any key derivation");
    Check(SetupBlob.Open("  " + blob.Substring(0, 30) + "\r\n" + blob.Substring(30) + "  ", code, now).Ok, "whitespace and line wrapping from copy/paste or email is tolerated");

    // plan: hostile / invalid content is dropped and counted, valid content is kept
    var plan = SetupPlan.Parse(ok.PayloadJson);
    Check(plan.Appearance != null && plan.Appearance.Theme == "dark" && plan.Appearance.DarkStyle == "custom" && plan.Appearance.PageBg == "#101010" && plan.Appearance.Font == "Georgia" && plan.Appearance.OverrideSiteFonts, "plan: appearance values carried over");
    Check(plan.HomeUrl == "https://example.com/" && plan.BlockingEnabled == false, "plan: home URL and blocking preference carried over");
    Check(plan.Bookmarks.Count == 2 && plan.BookmarksRejected == 2 && plan.Bookmarks.All(b => b.Url.StartsWith("http")), "plan: javascript: and file: bookmarks rejected, http(s) kept");
    Check(plan.Bookmarks[1].Title == "B", "plan: control characters stripped from titles");
    Check(plan.SiteEntries.Count(e => e.Category == "site_permissions") == 1 && plan.SiteEntries.Any(e => e.Key == "perm.Camera" && e.Origin == "meet.example"), "plan: only the valid permission entry survives (bad origin, bad value, wrong-category key dropped)");
    Check(plan.SiteEntries.Count(e => e.Category == "network_labels") == 1 && plan.SiteEntries.Count(e => e.Category == "cert_trust") == 1, "plan: network label needs a net-hash id; cert entry needs a hex fingerprint key");
    Check(plan.SiteRejected == 5 && plan.UnknownCategories.SequenceEqual(new[] { "mystery" }), "plan: rejected entries counted (5) and unknown categories reported, not applied");

    var evilAp = SetupPlan.Parse(SetupPayload.Build("0123456789abcdef", now, now.AddHours(1), new Dictionary<string, object> { ["appearance"] = new { theme = "dark", page_bg = "red;}body{display:none", font = "Evil'Font" }, ["preferences"] = new { home_url = "javascript:alert(1)", blocking_enabled = "yes" } }));
    Check(evilAp.Appearance != null && evilAp.Appearance.PageBg == "#1b1d22" && evilAp.Appearance.Font == "default" && evilAp.Appearance.Theme == "dark", "plan: CSS-injection colour and non-allowlisted font fall back to defaults");
    Check(evilAp.HomeUrl == null && evilAp.BlockingEnabled == null, "plan: javascript: home URL and non-boolean blocking value ignored");
    Check(SetupValidate.IsSafeHome("recognition:start") && !SetupValidate.IsSafeUrl("ftp://x/") && !SetupValidate.IsSafeUrl("https://x/\u0007") && !SetupValidate.IsSafeUrl(new string('a', 3000)), "validators: start page ok; ftp, control chars and over-long URLs rejected");

    // reusable: the same snapshot + code opens any number of times (on any device) until it expires
    Check(Enumerable.Range(0, 3).All(i => SetupBlob.Open(blob, code, now.AddMinutes(i * 10)).Ok), "re-apply: the same snapshot and code open repeatedly until expiry");
}

Console.WriteLine();
Console.WriteLine("=== Code highlighter (executed C#) ===");
{
    string HlJoin(List<List<(string Cls, string Text)>> ls) => string.Join("\n", ls.Select(x => string.Concat(x.Select(t => t.Text))));
    bool HlHas(List<(string Cls, string Text)> line, string cls, string text) => line.Any(t => t.Cls == cls && t.Text == text);
    Check(CodeHighlighter.LanguageFor("Program.CS") == "csharp" && CodeHighlighter.LanguageFor(@"C:\x\run.ps1") == "powershell" && CodeHighlighter.LanguageFor("Makefile") == "text" && CodeHighlighter.LanguageFor(null) == "text" && CodeHighlighter.LanguageFor("a.json") == "json" && CodeHighlighter.IsCodeFile("x.py") && !CodeHighlighter.IsCodeFile("x.exe"), "language detection by extension (case-insensitive, unknown/null -> text)");

    var cs = CodeHighlighter.Highlight("public class Foo { // hi\n  string s = \"a\\\"b\"; int x = 42; Bar(1); }", "csharp");
    Check(cs.Count == 2 && HlHas(cs[0], "kw", "public") && HlHas(cs[0], "kw", "class") && HlHas(cs[0], "type", "Foo") && cs[0].Any(t => t.Cls == "com" && t.Text == "// hi"), "C#: keywords, type name and line comment");
    Check(HlHas(cs[1], "kw", "string") && HlHas(cs[1], "str", "\"a\\\"b\"") && HlHas(cs[1], "num", "42") && HlHas(cs[1], "fn", "Bar"), "C#: string with escaped quote stays one token; number and call recognised");

    var blk = CodeHighlighter.Highlight("/* a\nb */ int x;", "csharp");
    Check(blk.Count == 2 && blk[0].Count == 1 && blk[0][0] == ("com", "/* a") && blk[1][0] == ("com", "b */") && HlHas(blk[1], "kw", "int"), "a block comment spanning lines is cut per line and highlighting resumes after it");

    var unterminated = CodeHighlighter.Highlight("var s = \"abc\nint x = 1;", "csharp");
    Check(unterminated.Count == 2 && HlHas(unterminated[1], "kw", "int") && HlHas(unterminated[1], "num", "1"), "an unterminated string ends at the line end and never swallows the rest of the file");

    var verb = CodeHighlighter.Highlight("var p = @\"C:\\dir\\\" + x;", "csharp");
    Check(verb[0].Any(t => t.Cls == "str" && t.Text == "@\"C:\\dir\\\""), "C# verbatim string: backslash before the closing quote is not an escape");

    var py = CodeHighlighter.Highlight("\"\"\"doc\nstring\"\"\"\nx = 1  # c", "python");
    Check(py.Count == 3 && py[0][0].Cls == "str" && py[1][0].Cls == "str" && HlHas(py[2], "num", "1") && py[2].Any(t => t.Cls == "com"), "Python: triple-quoted string across lines, number, # comment");

    var jsonT = CodeHighlighter.Highlight("{\"name\": \"v\", \"n\": 1, \"ok\": true}", "json");
    Check(HlHas(jsonT[0], "attr", "\"name\"") && HlHas(jsonT[0], "str", "\"v\"") && HlHas(jsonT[0], "num", "1") && HlHas(jsonT[0], "kw", "true"), "JSON: keys vs string values, numbers, literals");

    var psT = CodeHighlighter.Highlight("$x = 'it''s' # c\n<# block\n#> if ($x) {}", "powershell");
    Check(HlHas(psT[0], "var", "$x") && HlHas(psT[0], "str", "'it''s'") && psT[0].Any(t => t.Cls == "com") && psT.Count == 3 && psT[1][0].Cls == "com" && HlHas(psT[2], "kw", "if"), "PowerShell: variables, doubled-quote string, comments, block comment, keywords");

    var sql = CodeHighlighter.Highlight("SELECT * FROM t WHERE id = 1 -- hi", "sql");
    Check(HlHas(sql[0], "kw", "SELECT") && HlHas(sql[0], "kw", "FROM") && HlHas(sql[0], "num", "1") && sql[0].Any(t => t.Cls == "com"), "SQL: keywords are case-insensitive, -- comments");

    var htm = CodeHighlighter.Highlight("<!-- c -->\n<a href=\"x\" id='y'>t</a>", "html");
    Check(htm[0][0].Cls == "com" && HlHas(htm[1], "kw", "a") && HlHas(htm[1], "attr", "href") && HlHas(htm[1], "str", "\"x\"") && HlHas(htm[1], "kw", "a"), "HTML: comment, tag names, attribute names, quoted values");

    var cssT = CodeHighlighter.Highlight("a:hover { color: #fff; margin: 4px; }", "css");
    Check(!HlHas(cssT[0], "attr", "a") && HlHas(cssT[0], "attr", "color") && HlHas(cssT[0], "num", "#fff") && HlHas(cssT[0], "num", "4px"), "CSS: properties are highlighted inside rules, selectors like a:hover are not");

    var crlf = CodeHighlighter.Highlight("a\r\nb\r\n", "text");
    Check(crlf.Count == 3 && crlf.All(x => x.All(t => !t.Text.Contains('\r'))), "CRLF input: no stray carriage returns in any line");
    Check(CodeHighlighter.Render("a\nb\n", "text").Split("class='l'").Length - 1 == 2, "render: a trailing newline does not add an extra empty line");
    Check(CodeHighlighter.Render("", "csharp").Contains("id='L1'") && CodeHighlighter.Render(null, null).Contains("id='L1'"), "empty and null input render one empty line without throwing");

    var evil = "'\"><img src=x onerror=alert(1)></script><svg onload=alert(1)>";
    Check(CodeHighlighter.Languages.All(lg => { var h = CodeHighlighter.Render(evil + "\n/* " + evil + " */\n\"" + evil + "\"", lg); return !h.Contains("<img") && !h.Contains("<svg") && !h.Contains("</script"); }), "render: hostile markup in the source is HTML-encoded for every language (no tag can be injected)");

    var corpus = new[] { "", "\n", "x", "a\n\nb", "\"\"\"\n", "'''a", "/*", "/* a */ b /* c", "<<<", "<a href=\"x", "<!-- ", "@\"abc", "$@\"a\"\"b\"", "`multi\nline`", "// c\n# d\n-- e", "\t \t\n", "é漢字😀 \"ü\"", "{ \"a\": [1, 2.5e3, -4, true, null] }", "a.b.c(d)(e)", "0x1F 1_000 .5 5." };
    bool roundTrip = true;
    foreach (var lg in CodeHighlighter.Languages) foreach (var src in corpus) if (HlJoin(CodeHighlighter.Highlight(src, lg)) != src) { roundTrip = false; Console.WriteLine("    roundtrip mismatch: " + lg + " / " + src.Replace("\n", "\\n")); }
    Check(roundTrip, "round trip: concatenating all tokens reproduces the input exactly for every language and every corpus sample (nothing lost or duplicated)");

    Check(CodeHighlighter.LooksBinary(new byte[] { 0x4D, 0x5A, 0x00, 0x01 }) && !CodeHighlighter.LooksBinary(Encoding.UTF8.GetBytes("plain text\n")) && !CodeHighlighter.LooksBinary(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("hi")).ToArray()) && !CodeHighlighter.LooksBinary(null) && !CodeHighlighter.LooksBinary(new byte[0]), "binary detection: NUL bytes mean binary, UTF-16 with a BOM is still text, null/empty are not binary");
    Check(CodeHighlighter.DecodeText(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("é漢")).ToArray()) == "é漢" && CodeHighlighter.DecodeText(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("é漢")).ToArray()) == "é漢" && CodeHighlighter.DecodeText(Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes("é漢")).ToArray()) == "é漢" && CodeHighlighter.DecodeText(null) == "", "decode: UTF-8, UTF-16 LE and BE byte-order marks are honoured and removed");
    Check(CodeHighlighter.DecodeText(new byte[] { 0x61, 0xFF, 0xFE, 0x62 }.Skip(0).Take(1).Concat(new byte[] { 0xC3 }).ToArray()).StartsWith("a"), "decode: invalid UTF-8 never throws (replacement characters)");
    var big = string.Concat(Enumerable.Repeat("int x = \"a\"; // note\n", 60000));
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var bigHl = CodeHighlighter.Highlight(big, "csharp");
    sw.Stop();
    Check(bigHl.Count == 60001 && sw.ElapsedMilliseconds < 8000, "performance: 60,000 lines (~1.4 MB) highlight in well under 8 s (measured " + sw.ElapsedMilliseconds + " ms)");
    var hostile = string.Concat(Enumerable.Repeat("\"/*'`<#", 40000));
    sw.Restart();
    foreach (var lg in new[] { "csharp", "javascript", "powershell", "html", "python" }) CodeHighlighter.Highlight(hostile, lg);
    sw.Stop();
    Check(sw.ElapsedMilliseconds < 15000, "performance: 280 KB of quote/comment-opener garbage stays linear across 5 languages (measured " + sw.ElapsedMilliseconds + " ms)");
    foreach (var run in new[] { "(", "$", "@", "<", "\"", "\\", "a" })
    {
        var longRun = new string(run[0], 1_000_000);
        sw.Restart();
        foreach (var lg in new[] { "csharp", "powershell", "html", "css" }) CodeHighlighter.Highlight(longRun, lg);
        sw.Stop();
        Check(sw.ElapsedMilliseconds < 20000, "performance: a single 1,000,000-character run of '" + run + "' across 4 languages stays linear (measured " + sw.ElapsedMilliseconds + " ms)");
    }
}

Console.WriteLine();
Console.WriteLine("=== PDF page tools (executed C#, PDFsharp) ===");
{
    // Pages are identified by their MediaBox width (custom dictionary keys are not carried over by PDFsharp's page import,
    // but the page geometry is): prefix 'a' -> widths 101..., 'b' -> 201..., so "a3" is a page 103 points wide.
    byte[] MakePdf(string prefix, int n)
    {
        var d = new PdfSharp.Pdf.PdfDocument();
        int baseW = (prefix[0] - 'a' + 1) * 100;
        for (int i = 1; i <= n; i++) { var p = d.AddPage(); p.Width = PdfSharp.Drawing.XUnit.FromPoint(baseW + i); p.Height = PdfSharp.Drawing.XUnit.FromPoint(200); }
        using var ms = new MemoryStream(); d.Save(ms, false); return ms.ToArray();
    }
    List<string> Markers(byte[] pdf)
    {
        using var d = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(pdf), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        return Enumerable.Range(0, d.PageCount).Select(i => { int w = (int)Math.Round(d.Pages[i].MediaBox.Width); return (char)('a' + w / 100 - 1) + (w % 100).ToString(); }).ToList();
    }
    bool PdfFails(Action a) { try { a(); return false; } catch (PdfToolException) { return true; } }
    string Sha(byte[] b) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b));
    string J(IEnumerable<string> x) => string.Join(",", x);

    var pdfA = MakePdf("a", 5);
    Check(PdfTools.PageCount(pdfA) == 5 && J(Markers(pdfA)) == "a1,a2,a3,a4,a5", "test PDFs: 5 tagged pages read back in order");

    string? perr;
    Check(J(PdfTools.ParseRange("1-3,5", 5, out perr)!.Select(i => i + 1).Select(i => i.ToString())) == "1,2,3,5" && perr == null, "range: '1-3,5' -> pages 1,2,3,5");
    Check(J(PdfTools.ParseRange("2-", 5, out _)!.Select(i => (i + 1).ToString())) == "2,3,4,5" && J(PdfTools.ParseRange("-2", 5, out _)!.Select(i => (i + 1).ToString())) == "1,2", "range: open-ended '2-' and '-2'");
    Check(J(PdfTools.ParseRange("5-3", 5, out _)!.Select(i => (i + 1).ToString())) == "5,4,3" && J(PdfTools.ParseRange(" 1 , 3 ", 5, out _)!.Select(i => (i + 1).ToString())) == "1,3", "range: descending ranges allowed, whitespace tolerated");
    var badSpecs = new[] { "", "  ", "0", "6", "a", "1-2-3", "1,,2", ",", "9999999999", "3-1x", "-", "1--2", "+1", "1.5" };
    var notRejected = badSpecs.Where(s => PdfTools.ParseRange(s, 5, out var e3) != null).ToList();
    Check(notRejected.Count == 0, "range: invalid specs all rejected with a message" + (notRejected.Count > 0 ? " (accepted: " + string.Join(" | ", notRejected) + ")" : ""));

    var pdfSha0 = Sha(pdfA);
    Check(J(Markers(PdfTools.Extract(pdfA, "2,4-5"))) == "a2,a4,a5", "extract: pages 2, 4, 5 in that order");
    Check(J(Markers(PdfTools.Extract(pdfA, "5-1"))) == "a5,a4,a3,a2,a1", "reorder: '5-1' reverses the document");
    Check(J(Markers(PdfTools.Extract(pdfA, "3,1,2,4,5"))) == "a3,a1,a2,a4,a5" && J(Markers(PdfTools.Extract(pdfA, "1,1"))) == "a1,a1", "reorder: arbitrary order; duplicates allowed");
    Check(J(Markers(PdfTools.Delete(pdfA, "2-3"))) == "a1,a4,a5", "delete: pages 2-3 removed, rest keep their order");
    Check(PdfFails(() => PdfTools.Delete(pdfA, "1-5")) && PdfFails(() => PdfTools.Extract(pdfA, "7")) && PdfFails(() => PdfTools.Extract(pdfA, "")), "delete-all, out-of-range and empty page lists are refused");
    var pdfB = MakePdf("b", 2);
    Check(J(Markers(PdfTools.Merge(new[] { MakePdf("a", 3), pdfB }))) == "a1,a2,a3,b1,b2", "merge: documents joined in the order given");
    Check(PdfFails(() => PdfTools.Merge(new[] { pdfB })) && PdfFails(() => PdfTools.Merge(new[] { pdfB, new byte[] { 1, 2, 3 } })), "merge: needs two PDFs; a non-PDF in the set is refused");
    var split = PdfTools.SplitEach(pdfA);
    Check(split.Count == 5 && Enumerable.Range(0, 5).All(i => J(Markers(split[i])) == "a" + (i + 1)), "split: one single-page PDF per page");
    var rot = PdfTools.Rotate(pdfA, "1,3", 90);
    Check(J(PdfTools.Rotations(rot).Select(x => x.ToString())) == "90,0,90,0,0" && J(Markers(rot)) == "a1,a2,a3,a4,a5", "rotate: only the listed pages turn; order and content unchanged");
    Check(J(PdfTools.Rotations(PdfTools.Rotate(rot, "1", 270)).Select(x => x.ToString())) == "0,0,90,0,0" && J(PdfTools.Rotations(PdfTools.Rotate(pdfA, "2", -90)).Select(x => x.ToString())) == "0,270,0,0,0", "rotate: rotation adds to the existing angle and wraps (90+270=0, -90=270)");
    Check(PdfFails(() => PdfTools.Rotate(pdfA, "1", 45)) && PdfFails(() => PdfTools.Rotate(pdfA, "1", 0)), "rotate: only multiples of 90 are accepted");
    Check(Sha(pdfA) == pdfSha0, "operations never modify the input bytes");
    Check(PdfFails(() => PdfTools.PageCount(new byte[0])) && PdfFails(() => PdfTools.PageCount(Encoding.ASCII.GetBytes("this is definitely not a pdf file at all"))) && PdfFails(() => PdfTools.Extract(pdfA.Take(pdfA.Length / 2).ToArray(), "1")), "empty, non-PDF and truncated input fail with a clear PdfToolException instead of crashing");
    var rnd = new Random(7); var junk = new byte[4096]; rnd.NextBytes(junk);
    Check(PdfFails(() => PdfTools.PageCount(junk)), "random bytes are refused");
}

Console.WriteLine();
Console.WriteLine("=== Image maths (executed C#) ===");
{
    int w, h; string? ie;
    Check(ImageMath.TryTarget(1000, 500, "percent", 50, 0, out w, out h, out ie) && w == 500 && h == 250, "resize: 50% of 1000x500 = 500x250");
    Check(ImageMath.TryTarget(1000, 500, "width", 400, 0, out w, out h, out ie) && w == 400 && h == 200 && ImageMath.TryTarget(1000, 500, "height", 100, 0, out w, out h, out ie) && w == 200 && h == 100, "resize: by width or height keeps the aspect ratio");
    Check(ImageMath.TryTarget(4000, 3000, "fit", 800, 800, out w, out h, out ie) && w == 800 && h == 600 && ImageMath.TryTarget(3000, 4000, "fit", 800, 800, out w, out h, out ie) && w == 600 && h == 800, "resize: fit within a box, either orientation");
    Check(ImageMath.TryTarget(100, 100, "fit", 800, 800, out w, out h, out ie) && w == 800 && h == 800, "resize: fit may enlarge when asked");
    Check(ImageMath.TryTarget(1000, 500, "exact", 300, 300, out w, out h, out ie) && w == 300 && h == 300, "resize: exact size ignores the aspect ratio");
    Check(ImageMath.TryTarget(3, 1, "percent", 10, 0, out w, out h, out ie) && w == 1 && h == 1, "resize: never rounds down to zero pixels");
    Check(new[] { ("percent", 0.0, 0.0), ("percent", -5.0, 0.0), ("percent", 100000.0, 0.0), ("width", 0.0, 0.0), ("width", 99999.0, 0.0), ("exact", 10.0, 0.0), ("fit", 0.0, 10.0), ("bogus", 10.0, 10.0), ("width", double.NaN, 0.0), ("height", double.PositiveInfinity, 0.0) }.All(c => !ImageMath.TryTarget(1000, 500, c.Item1, c.Item2, c.Item3, out _, out _, out var e4) && !string.IsNullOrEmpty(e4)), "resize: zero, negative, absurd, NaN, infinite or unknown inputs are refused with a message");
    Check(!ImageMath.TryTarget(0, 500, "percent", 50, 0, out _, out _, out _), "resize: an image with no size is refused");
    Check(!ImageMath.TryTarget(5000, 4000, "percent", 300, 0, out w, out h, out ie) && w == 0 && h == 0 && ie != null && ie.Contains("limit"), "resize: 15000x12000 (180 megapixels) is refused by the pixel limit");
    Check(ImageMath.TryTarget(5000, 4000, "percent", 200, 0, out w, out h, out ie) && w == 10000 && h == 8000, "resize: 10000x8000 (80 megapixels) is within the limit");
    Check(ImageMath.NormalizeRotation(90) == 90 && ImageMath.NormalizeRotation(-90) == 270 && ImageMath.NormalizeRotation(450) == 90 && ImageMath.NormalizeRotation(45) == -1 && ImageMath.NormalizeRotation(0) == 0, "rotation: normalised to 0/90/180/270; non-multiples of 90 rejected");
    Check(ImageMath.AfterRotation(1000, 500, 90) == (500, 1000) && ImageMath.AfterRotation(1000, 500, 180) == (1000, 500) && ImageMath.AfterRotation(1000, 500, 270) == (500, 1000), "rotation: width and height swap on 90/270");
    Check(ImageMath.ClampQuality(0) == 1 && ImageMath.ClampQuality(500) == 100 && ImageMath.ClampQuality(85) == 85, "JPEG quality is clamped to 1..100");
    Check(ImageMath.FormatFor("PNG") == "png" && ImageMath.FormatFor("jpeg") == "jpg" && ImageMath.FormatFor("jpg") == "jpg" && ImageMath.FormatFor("bmp") == "bmp" && ImageMath.FormatFor("tiff") == "tiff" && ImageMath.FormatFor("exe") == null && ImageMath.FormatFor(null) == null, "output formats are an allowlist (png, jpg, bmp, tiff, gif)");
    Check(ImageMath.IsImageFile("a.JPG") && ImageMath.IsImageFile("a.webp") && !ImageMath.IsImageFile("a.pdf") && !ImageMath.IsImageFile("a"), "image file detection by extension");
}

Console.WriteLine();
Console.WriteLine("=== Filter engine (executed C#) ===");
{
    FilterEngine Fe(params string[] lines) { var e = new FilterEngine(); e.AddList(string.Join("\n", lines), "t"); return e; }
    bool Blk(FilterEngine e, string url, string page = "https://page.example/", ResType t = ResType.Script) => e.Match(url, page, t).Blocked;

    var feA = Fe("||ads.example.com^");
    Check(Blk(feA, "https://ads.example.com/x.js") && Blk(feA, "https://sub.ads.example.com/a") && Blk(feA, "https://ads.example.com:8443/a") && Blk(feA, "https://ads.example.com"), "domain rule ||ads.example.com^ blocks the host, its subdomains, explicit ports and the bare host");
    Check(!Blk(feA, "https://example.com/x.js") && !Blk(feA, "https://ads.example.com.evil.com/x.js") && !Blk(feA, "https://notads.example.com/x.js") && !Blk(feA, "https://example.com/ads.example.com/x"), "domain rule does not match the parent, a look-alike suffix, a longer label, or the text appearing in a path");
    Check(Blk(feA, "https://good.com@ads.example.com/x") && !Blk(feA, "https://ads.example.com@good.com/x") && !Blk(feA, "https://good.com:pw@good.com/ads.example.com"), "userinfo tricks: the real host is what is matched");
    var feB = Fe("||example.com/path");
    Check(Blk(feB, "https://example.com/path/x") && Blk(feB, "https://www.example.com/path?q=1") && !Blk(feB, "https://example.com/other"), "domain + path rule");
    var feC = Fe("/banner/ad_", "||tracker.net/*/pixel.gif", "|https://exact.example/ad.js|");
    Check(Blk(feC, "https://x.test/banner/ad_1.png", t: ResType.Image) && !Blk(feC, "https://x.test/banners/ad_1.png", t: ResType.Image), "plain path rule needs the whole token ('/banner/' matches, '/banners/' does not)");
    Check(Blk(feC, "https://tracker.net/a/b/pixel.gif", t: ResType.Image) && !Blk(feC, "https://tracker.net/pixel.gif", t: ResType.Image), "wildcard rule: '*' spans path parts, literal slashes still required");
    Check(Blk(feC, "https://exact.example/ad.js") && !Blk(feC, "https://exact.example/ad.js?x=1") && !Blk(feC, "http://exact.example/ad.js") && !Blk(feC, "https://sub.exact.example/ad.js"), "start and end anchors (|...|) match exactly");
    var feD = Fe("||cdn.example.com^$script", "||tp.example^$third-party", "||fp.example^$~third-party", "||both.example^$image,script", "||notimg.example^$~image");
    Check(Blk(feD, "https://cdn.example.com/a.js", t: ResType.Script) && !Blk(feD, "https://cdn.example.com/a.png", t: ResType.Image), "type option: $script blocks scripts only");
    Check(Blk(feD, "https://tp.example/x", "https://news.test/") && !Blk(feD, "https://tp.example/x", "https://www.tp.example/") && !Blk(feD, "https://tp.example/x", "https://tp.example/"), "$third-party blocks only across sites (same registrable domain is first-party)");
    Check(!Blk(feD, "https://fp.example/x", "https://news.test/") && Blk(feD, "https://fp.example/x", "https://fp.example/"), "$~third-party blocks only first-party requests");
    Check(Blk(feD, "https://both.example/a", t: ResType.Image) && Blk(feD, "https://both.example/a", t: ResType.Script) && !Blk(feD, "https://both.example/a", t: ResType.Font), "several types in one rule");
    Check(Blk(feD, "https://notimg.example/a", t: ResType.Script) && !Blk(feD, "https://notimg.example/a", t: ResType.Image), "negated type $~image");
    var feE = Fe("||widget.test^$domain=news.com|~sports.news.com");
    Check(Blk(feE, "https://widget.test/w.js", "https://news.com/a") && Blk(feE, "https://widget.test/w.js", "https://www.news.com/a") && !Blk(feE, "https://widget.test/w.js", "https://sports.news.com/a") && !Blk(feE, "https://widget.test/w.js", "https://other.com/a"), "domain= option includes, excludes and covers subdomains of the page host");
    var feF = Fe("||ads.example.com^", "@@||ads.example.com/allowed^", "||hard.example^$important", "@@||hard.example^");
    var mAllow = feF.Match("https://ads.example.com/allowed/x.js", "https://p.test/", ResType.Script);
    Check(!mAllow.Blocked && mAllow.Rule != null && mAllow.Rule.StartsWith("@@") && Blk(feF, "https://ads.example.com/other.js"), "exception rule (@@) overrides a block only for what it names");
    Check(Blk(feF, "https://hard.example/x.js"), "$important blocks even when an exception matches");
    var feG = Fe("||page.test^", "||doc.test^$document");
    Check(!Blk(feG, "https://page.test/", t: ResType.Document) && Blk(feG, "https://page.test/", t: ResType.Subdocument) && Blk(feG, "https://doc.test/", t: ResType.Document), "rules do not block top-level navigation unless they say $document");
    var feH = Fe("0.0.0.0 evil.example # comment", "127.0.0.1 localhost", "0.0.0.0 0.0.0.0", "0.0.0.0\ttab.example", "127.0.0.1 two words.example", "0.0.0.0 bad_host");
    Check(Blk(feH, "https://evil.example/x") && Blk(feH, "https://sub.evil.example/x") && Blk(feH, "https://tab.example/x") && !Blk(feH, "https://localhost/x"), "hosts-file lines become host rules; localhost is never blocked");
    Check(feH.Rejected >= 3, "malformed hosts lines are counted as rejected (" + feH.Rejected + ")");
    var feI = Fe("! comment", "[Adblock Plus 2.0]", "/regex\\d+/", "||x.example^$redirect=noopjs", "||y.example^$csp=script-src 'none'", "example.com##+js(abort-on-property-read, x)", "example.com#?#div:has-text(Ad)", "||z.example^$badfilter", "||w.example^$unknownoption", "||ok.example^");
    Check(feI.NetworkRules == 1 && feI.Unsupported >= 6 && Blk(feI, "https://ok.example/x") && !Blk(feI, "https://x.example/x") && !Blk(feI, "https://y.example/x") && !Blk(feI, "https://z.example/x") && !Blk(feI, "https://w.example/x"), "unsupported syntax (regex, redirect, csp, scriptlets, procedural, badfilter, unknown options) is skipped and counted, never guessed (" + feI.Unsupported + " unsupported)");
    var feJ = Fe("*", "||", "$script", "|", "$third-party", "^", "||*", "$image,domain=a.test");
    Check(!Blk(feJ, "https://anything.example/x") && feJ.NetworkRules <= 1, "rules that would block everything (empty or all-wildcard patterns) are refused");
    Check(Blk(feJ, "https://anything.example/x", "https://a.test/", ResType.Image) == (feJ.NetworkRules == 1), "...except when restricted to named domains by domain=");

    // base-list style host entries, including a path entry that the old host-only list could never match
    var feK = new FilterEngine(); foreach (var h in new[] { "doubleclick.net", "facebook.com/tr", "t.co", "bad host!" }) feK.AddHostBlock(h, "base");
    Check(Blk(feK, "https://ad.doubleclick.net/x") && Blk(feK, "https://www.facebook.com/tr?id=1") && !Blk(feK, "https://www.facebook.com/home") && Blk(feK, "https://t.co/abc") && feK.Rejected == 1, "AddHostBlock: host entries and host/path entries both work (the path entry matches only that path); junk is rejected");

    // cosmetic
    var feL = Fe("##.ad-banner", "example.com##.promo", "~example.com##.sponsored", "example.com#@#.ad-banner", "##div[class^=\"ad-\"]", "a{}body{display:none}##x", "##x;y", "##a/*", "##url(x)", "example.com##.x:has-text(Buy)", "##a:-abp-has(b)", "##@import x", "##.ok > .child");
    var cssEx = feL.CosmeticCssFor("www.example.com"); var cssOther = feL.CosmeticCssFor("other.org");
    Check(cssEx.Contains(".promo{display:none!important}") && !cssEx.Contains(".ad-banner") && !cssEx.Contains(".sponsored") && cssEx.Contains("div[class^=\"ad-\"]{display:none!important}"), "cosmetic: domain rules apply to subdomains, #@# unhides, ~domain excludes the generic rule");
    Check(cssOther.Contains(".ad-banner{display:none!important}") && cssOther.Contains(".sponsored{display:none!important}") && !cssOther.Contains(".promo") && cssOther.Contains(".ok > .child{display:none!important}"), "cosmetic: generic rules apply elsewhere; child combinators allowed");
    Check(!cssOther.Contains("body{display:none}") && !cssOther.Contains("x;y") && !cssOther.Contains("/*") && !cssOther.Contains("url(") && !cssOther.Contains("@import") && !cssOther.Contains("has-text") && !cssOther.Contains("-abp-"), "cosmetic: selectors that could escape the CSS rule, or need procedural matching, are rejected");
    Check(feL.CosmeticCssFor("") == "" && feL.CosmeticCssFor(null) == "" && feL.CosmeticCssFor("EXAMPLE.COM").Contains(".promo"), "cosmetic: empty host gives no CSS; host matching is case-insensitive");
    Check(new[] { "div[class^=\"ad-\"]", "a[href*=\"/ads/\"]", "#sidebar > .ad", ".a, .b", "ul li:nth-child(2n+1)" }.All(FilterEngine.IsSafeSelector) && new[] { "a{}", "a;b", "a\\b", "a/*b", "url(javascript:x)", "x:has-text(a)", "", "  ", "a<b", "@media x", "a\nb", new string('a', 500) }.All(s => !FilterEngine.IsSafeSelector(s)), "selector safety: valid selectors accepted, injection attempts and oversized/empty ones rejected");

    // public suffix / third-party
    var psl = new PublicSuffixList();
    Check(psl.Registrable("a.b.example.co.uk") == "example.co.uk" && psl.Registrable("www.example.com") == "example.com" && psl.Registrable("example.com") == "example.com" && psl.Registrable("foo.github.io") == "foo.github.io" && psl.Registrable("x.y.github.io") == "y.github.io", "registrable domain: multi-part suffixes and private suffixes (github.io) are respected");
    Check(psl.Registrable("localhost") == "localhost" && psl.Registrable("127.0.0.1") == "127.0.0.1" && psl.Registrable("[::1]") == "[::1]" && psl.Registrable("co.uk") == "co.uk" && psl.Registrable("") == "" && psl.Registrable(null) == "" && psl.Registrable("Example.COM.") == "example.com", "registrable domain: single labels, IPs, a bare public suffix, empty/null, case and trailing dot");
    Check(psl.Registrable("a.b.compute.amazonaws.com") == "a.b.compute.amazonaws.com", "registrable domain: wildcard suffix rule (*.compute.amazonaws.com)");
    psl.Parse("*.ck !www.ck");
    Check(psl.Registrable("a.b.ck") == "a.b.ck" && psl.Registrable("www.ck") == "www.ck", "registrable domain: PSL wildcard and exception rules");

    // hostile input and pathological matching
    var feM = new FilterEngine();
    var junk = new StringBuilder(); var rng = new Random(11);
    for (int i = 0; i < 3000; i++) { var b = new char[rng.Next(1, 300)]; for (int j = 0; j < b.Length; j++) b[j] = (char)rng.Next(0, 300); junk.Append(new string(b).Replace('\n', ' ')).Append('\n'); }
    junk.Append(new string('a', 100000)).Append('\n').Append("||").Append(new string('*', 5000)).Append('\n').Append("$").Append(new string(',', 1000)).Append('\n');
    bool junkOk = true; try { feM.AddList(junk.ToString(), "junk"); feM.Match("https://a.example/" + new string('x', 3000), "https://b.example/", ResType.Script); } catch { junkOk = false; }
    Check(junkOk, "a hostile list (random characters, 100,000-character line, 5,000 wildcards) loads without throwing");
    var feN = Fe("*a*a*a*a*a*a*a*a*a*a*b", "||x.test/*a*a*a*a*a*a*c");
    var swF = System.Diagnostics.Stopwatch.StartNew(); bool anyBlocked = false;
    for (int i = 0; i < 2000; i++) anyBlocked |= Blk(feN, "https://x.test/" + new string('a', 3000));
    swF.Stop();
    Check(!anyBlocked && swF.ElapsedMilliseconds < 5000, "worst-case wildcard rules against 3,000-character URLs stay fast (2,000 lookups in " + swF.ElapsedMilliseconds + " ms)");
    Check(!Blk(feA, new string('a', 5000)) && !Blk(feA, "") && !Blk(feA, null!) && !Blk(feA, "not a url") && !Blk(feA, "https://"), "malformed, empty, null and over-long URLs never throw and are not blocked");

    // performance at EasyList scale
    var perf = new FilterEngine(); var sbp = new StringBuilder();
    for (int i = 0; i < 60000; i++) sbp.Append("||host").Append(i).Append(".example^\n");
    for (int i = 0; i < 30000; i++) sbp.Append("/ad").Append(i).Append("/banner_$image\n");
    for (int i = 0; i < 20000; i++) sbp.Append("||trk").Append(i).Append(".test/*/track").Append(i).Append(".js\n");
    for (int i = 0; i < 10000; i++) sbp.Append("##.promo-box-").Append(i).Append('\n');
    var swL = System.Diagnostics.Stopwatch.StartNew(); perf.AddList(sbp.ToString(), "perf"); swL.Stop();
    Check(perf.NetworkRules == 110000 && perf.CosmeticRules == 4000 && swL.ElapsedMilliseconds < 20000, "loads a 120,000-rule list (" + perf.NetworkRules + " network, " + perf.CosmeticRules + " cosmetic: 10,000 offered, generic hiding capped at 4,000 per page) in " + swL.ElapsedMilliseconds + " ms");
    var urls = new List<string>(); for (int i = 0; i < 60000; i++) urls.Add(i % 3 == 0 ? "https://host" + (i * 7 % 70000) + ".example/p/" + i + ".js" : i % 3 == 1 ? "https://site" + i + ".org/ad" + (i % 40000) + "/banner_" + i + ".png" : "https://cdn" + i + ".net/assets/app." + i + ".js?v=" + i);
    int hits = 0; var swM = System.Diagnostics.Stopwatch.StartNew();
    foreach (var u in urls) if (perf.Match(u, "https://page.example/", u.EndsWith(".png") ? ResType.Image : ResType.Script).Blocked) hits++;
    swM.Stop();
    Check(hits > 15000 && swM.ElapsedMilliseconds < 8000, "60,000 lookups against 110,000 network rules: " + hits + " blocked, " + swM.ElapsedMilliseconds + " ms total (" + (swM.Elapsed.TotalMilliseconds * 1000 / urls.Count).ToString("0.0") + " µs per request)");
    Check(perf.Match("https://host5.example/x.js", "https://p.test/", ResType.Script).Blocked && perf.Match("https://x.org/ad77/banner_1.png", "https://p.test/", ResType.Image).Blocked && perf.Match("https://trk9.test/a/b/track9.js", "https://p.test/", ResType.Script).Blocked && perf.Match("https://host5.example/x.png", "https://p.test/", ResType.Image).Blocked && !perf.Match("https://host99999999.example/x.js", "https://p.test/", ResType.Script).Blocked, "performance set: spot checks of each rule kind behave correctly");
}

Console.WriteLine();
Console.WriteLine("=== Full-page screenshot to PDF (executed C#) ===");
{
    byte[] FpJpeg(int w, int h, int comps = 3, int precision = 8, int sof = 0xC0)
    {
        var l = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, (byte)sof };
        int len = 8 + 3 * comps; l.Add((byte)(len >> 8)); l.Add((byte)len); l.Add((byte)precision);
        l.Add((byte)(h >> 8)); l.Add((byte)h); l.Add((byte)(w >> 8)); l.Add((byte)w); l.Add((byte)comps);
        for (int i = 0; i < comps; i++) { l.Add((byte)(i + 1)); l.Add(0x11); l.Add(0); }
        l.Add(0xFF); l.Add(0xD9); return l.ToArray();
    }
    // layout planning
    var fpA = PagePlanner.Plan(1190, 5000, "a4", 8000);
    Check(fpA.Tiles.Count == 1 && fpA.Pages.Count == 3, "A4: 1190x5000 css -> 1 tile, 3 pages (got " + fpA.Tiles.Count + "/" + fpA.Pages.Count + ")");
    Check(Math.Abs(fpA.PtPerCss - 0.5002) < 0.001, "A4 scale fits the paper width");
    Check(fpA.Pages.All(p => Math.Abs(p.WidthPt - 595.28) < 0.01 && Math.Abs(p.HeightPt - 841.89) < 0.01), "A4 pages are paper sized");
    Check(fpA.Pages.Sum(p => p.Height) == 5000 && fpA.Pages[0].Top == 0 && fpA.Pages[1].Top == fpA.Pages[0].Height, "page slices tile the page exactly, no gap or overlap");
    var fpL = PagePlanner.Plan(1000, 2000, "letter", 8000);
    Check(Math.Abs(fpL.Pages[0].WidthPt - 612) < 0.01 && Math.Abs(fpL.Pages[0].HeightPt - 792) < 0.01, "Letter paper size");
    var fpS = PagePlanner.Plan(1000, 3000, "single", 8000);
    Check(fpS.Pages.Count == 1 && Math.Abs(fpS.Pages[0].WidthPt - 750) < 0.01 && Math.Abs(fpS.Pages[0].HeightPt - 2250) < 0.01, "single page is actual size (0.75 pt per css px)");
    var fpT = PagePlanner.Plan(1000, 20000, "single", 8000);
    Check(fpT.Pages[0].HeightPt <= PagePlanner.MaxPdfPt + 0.001 && fpT.Pages[0].HeightPt > 14000, "single page is shrunk to stay under the PDF size limit");
    var fpTall = PagePlanner.Plan(1000, 20000, "a4", 8000);
    Check(fpTall.Tiles.Count == 3 && fpTall.Tiles.Sum(t => t.Height) == 20000 && fpTall.Tiles[1].Top == 8000, "20000 px is cut into 3 capture tiles that add up");
    var fpBig = PagePlanner.Plan(9000, 90000, "a4", 8000);
    Check(fpBig.Truncated && fpBig.WidthCss == PagePlanner.MaxWidthCss && fpBig.HeightCss == PagePlanner.MaxHeightCss, "oversized pages are clamped and flagged");
    bool fpThrew = false; try { PagePlanner.Plan(0, 10, "a4", 8000); } catch (ImagePdfException) { fpThrew = true; } Check(fpThrew, "zero-size page refused");
    fpThrew = false; try { PagePlanner.Plan(100, 60000, "a4", 8000); } catch (ImagePdfException) { fpThrew = true; } Check(fpThrew, "a plan needing over 300 pages is refused (use the single layout)");
    Check(PagePlanner.Plan(500, 500, "bogus", 8000).Layout == "a4", "unknown layout falls back to A4");
    // every css pixel is covered by exactly one page slice and by at least one tile placed on that page
    {
        var pl = PagePlanner.Plan(1234, 12345, "a4", 5000); bool covered = true;
        foreach (var pg in pl.Pages)
        {
            var pls = PagePlanner.Placements(pl, pg);
            for (int y = pg.Top; y < pg.Top + pg.Height; y += 37)
                if (!pls.Any(q => pl.Tiles[q.Tile].Top <= y && y < pl.Tiles[q.Tile].Top + pl.Tiles[q.Tile].Height)) covered = false;
        }
        Check(covered, "every row of every page is covered by a placed tile");
    }
    // JPEG header parsing
    Check(ImagePdf.ParseJpeg(FpJpeg(800, 600)) == new JpegInfo(800, 600, 3), "JPEG size read");
    Check(ImagePdf.ParseJpeg(FpJpeg(70, 9000, 1)) == new JpegInfo(70, 9000, 1), "grey JPEG, 16-bit height");
    Check(ImagePdf.ParseJpeg(FpJpeg(10, 10, 3, 8, 0xC2)).Width == 10, "progressive JPEG accepted");
    foreach (var (bad, why) in new (byte[], string)[] { (FpJpeg(10, 10, 4), "CMYK"), (FpJpeg(10, 10, 3, 12), "12-bit"), (FpJpeg(10, 10, 3, 8, 0xC3), "lossless"), (new byte[] { 1, 2, 3, 4, 5 }, "not a JPEG"), (Array.Empty<byte>(), "empty"), (new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, "no frame header"), (FpJpeg(0, 10), "zero width") })
    {
        fpThrew = false; try { ImagePdf.ParseJpeg(bad); } catch (ImagePdfException) { fpThrew = true; } Check(fpThrew, "bad JPEG refused: " + why);
    }
    // the PDF itself
    var fpPlan = PagePlanner.Plan(1190, 5000, "a4", 2000);   // 3 tiles, 3 pages: tile edges fall inside pages
    var fpTiles = fpPlan.Tiles.Select(t => FpJpeg(1190, t.Height)).ToList();
    var fpPdf = ImagePdf.Build(fpPlan, fpTiles, "Täst — page ✓", "https://example.com/", new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc));
    var fpText = System.Text.Encoding.Latin1.GetString(fpPdf);
    Check(fpText.StartsWith("%PDF-1.4") && fpText.TrimEnd().EndsWith("%%EOF"), "PDF header and trailer");
    {
        int sx = fpText.LastIndexOf("startxref", StringComparison.Ordinal);
        long xo = long.Parse(fpText.Substring(sx + 10).Split('\n')[0]);
        Check(fpText.Substring((int)xo, 4) == "xref", "startxref points at the xref table");
        var lines = fpText.Substring((int)xo).Split('\n');
        int count = int.Parse(lines[1].Split(' ')[1]); bool offsetsOk = true;
        for (int n = 1; n < count; n++)
        {
            long off = long.Parse(lines[2 + n].Substring(0, 10));
            if (!fpText.Substring((int)off).StartsWith(n + " 0 obj")) offsetsOk = false;
        }
        Check(offsetsOk && count == 1 + 3 + 3 + 3 * 2, "every xref offset points at its object (" + count + " entries)");
    }
    Check(System.Text.RegularExpressions.Regex.Matches(fpText, "/Subtype /Image").Count == 3, "each tile is embedded once even though tiles span pages");
    Check(fpText.Contains("/Filter /DCTDecode") && fpText.Contains("/DeviceRGB"), "JPEG embedded as DCT data");
    Check(fpText.Contains("/Title <FEFF") && !fpText.Contains("Täst"), "title stored as UTF-16 hex");
    Check(fpText.Contains("(D:20261007120000Z)"), "creation date");
    {
        using var rd = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(fpPdf), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        Check(rd.PageCount == 3, "PDFsharp reads 3 pages");
        Check(Math.Abs(rd.Pages[0].MediaBox.Width - 595.28) < 0.1 && Math.Abs(rd.Pages[0].MediaBox.Height - 841.89) < 0.1, "PDFsharp reads the A4 media box");
        // the new file is a normal PDF for the existing page tools
        Check(PdfTools.PageCount(fpPdf) == 3, "page tools can open a screenshot PDF");
        Check(PdfTools.PageCount(PdfTools.Extract(fpPdf, "2-3")) == 2, "page tools can extract pages from a screenshot PDF");
    }
    fpThrew = false; try { ImagePdf.Build(fpPlan, fpTiles.Take(2).ToList(), "t", "s", DateTime.UtcNow); } catch (ImagePdfException) { fpThrew = true; } Check(fpThrew, "tile count mismatch refused");
    fpThrew = false; try { var junk = fpTiles.ToList(); junk[1] = new byte[] { 9, 9, 9, 9 }; ImagePdf.Build(fpPlan, junk, "t", "s", DateTime.UtcNow); } catch (ImagePdfException) { fpThrew = true; } Check(fpThrew, "non-JPEG tile refused");
    var fpOne = PagePlanner.Plan(800, 1000, "single", 8000);
    var fpOnePdf = ImagePdf.Build(fpOne, new List<byte[]> { FpJpeg(800, 1000) }, "t", "", DateTime.UtcNow);
    using (var rd1 = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(fpOnePdf), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
        Check(rd1.PageCount == 1 && Math.Abs(rd1.Pages[0].MediaBox.Width - 600) < 0.1 && Math.Abs(rd1.Pages[0].MediaBox.Height - 750) < 0.1, "single-page PDF has the actual-size media box");
    var fpTime = System.Diagnostics.Stopwatch.StartNew();
    var fpHuge = PagePlanner.Plan(1200, 60000, "single", 8000);
    var fpHugePdf = ImagePdf.Build(fpHuge, fpHuge.Tiles.Select(t => FpJpeg(1200, t.Height)).ToList(), "t", "", DateTime.UtcNow);
    Check(fpTime.ElapsedMilliseconds < 2000 && fpHuge.Tiles.Count == 8, "a 60000 px page plans and writes quickly (" + fpTime.ElapsedMilliseconds + " ms)");
}

Console.WriteLine();
Console.WriteLine("=== Extension governance (executed C#) ===");
{
    string? egRoot = null;
    for (var egd = new DirectoryInfo(AppContext.BaseDirectory); egd != null; egd = egd.Parent)
        if (File.Exists(Path.Combine(egd.FullName, "samples", "extensions", "hello-good", "manifest.json"))) { egRoot = egd.FullName; break; }
    Check(egRoot != null, "repository root with samples\\extensions found from the test binary");
    if (egRoot != null)
    {
        var egGood = Path.Combine(egRoot, "samples", "extensions", "hello-good"); var egRev = Path.Combine(egRoot, "samples", "extensions", "hello-review");
        // A copy of a ledger written by the PowerShell tool is committed as a fixture (proofs\receipts is git-ignored, so a fresh clone or CI has no real ledger).
        var egLedgerReal = Path.Combine(egRoot, "browser.tests", "fixtures", "extension_governance.powershell_written.v1.ndjson");
        Check(File.Exists(egLedgerReal), "PowerShell-written ledger fixture present");
        // identity must equal what the PowerShell implementation recorded for the same bytes
        var egIdGood = ExtGovernance.ComputeIdentity(egGood); var egIdRev = ExtGovernance.ComputeIdentity(egRev);
        Check(egIdGood.Id == "c719f88b943a8307a5e6f06f6fd0f7b26fdb4ed07c5f170d76f3e40b7c666b11", "C# identity of hello-good equals the PowerShell ledger id");
        Check(egIdRev.Id == "7d2fd7563cba40f93bacaf53040f577f626e0c2fa39a9c117b680641fdf22254", "C# identity of hello-review equals the PowerShell ledger id");
        // the real PowerShell-written ledger verifies in C#
        var egVer = ExtGovernance.VerifyLedger(egLedgerReal);
        Check(egVer.Count >= 4, "PowerShell-written ledger verifies in C# (" + egVer.Count + " records)");
        Check(ExtGovernance.Gate(egLedgerReal, egIdGood.Id).Ok, "load gate: hello-good is allowed");
        var egGateRev = ExtGovernance.Gate(egLedgerReal, egIdRev.Id);
        Check(!egGateRev.Ok && egGateRev.Note.Contains("review"), "load gate: hello-review is refused (decision review)");
        Check(!ExtGovernance.Gate(egLedgerReal, new string('a', 64)).Ok, "load gate: unknown bytes are refused");
        // policy decisions
        var egPol = ExtGovernance.ParsePolicy(File.ReadAllText(Path.Combine(egRoot, "config", "extension_policy.v1.json")));
        Check(ExtGovernance.Decide(ExtGovernance.ReadManifest(egGood), egIdGood.Id, egPol).Decision == "allow", "policy: storage+alarms -> allow");
        var egDecRev = ExtGovernance.Decide(ExtGovernance.ReadManifest(egRev), egIdRev.Id, egPol);
        Check(egDecRev.Decision == "review" && egDecRev.Reasons.SequenceEqual(new[] { "review_permission:tabs" }), "policy: tabs -> review with the exact reason");
        // tampering with a copy of the real ledger
        var egTmp = Path.Combine(AppContext.BaseDirectory, "tmp", "rb-egtest-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(egTmp);
        try
        {
            var egLines = File.ReadAllLines(egLedgerReal).Where(l => l.Trim().Length > 0).ToList();
            void EgExpectBreak(List<string> lines, string label)
            {
                var p = Path.Combine(egTmp, Guid.NewGuid().ToString("N") + ".ndjson"); File.WriteAllLines(p, lines);
                bool broke = false; try { ExtGovernance.VerifyLedger(p); } catch (ExtGovException) { broke = true; }
                Check(broke, "ledger break detected: " + label);
            }
            EgExpectBreak(egLines.Select((l, i) => i == 1 ? l.Replace("\"review\"", "\"allow\"") : l).ToList(), "decision flipped from review to allow");
            EgExpectBreak(egLines.Where((l, i) => i != 1).ToList(), "a record removed");
            EgExpectBreak(new List<string> { egLines[1], egLines[0] }.Concat(egLines.Skip(2)).ToList(), "records reordered");
            EgExpectBreak(egLines.Select((l, i) => i == 2 ? l.Replace("\"name\":\"", "\"name\":\"x") : l).ToList(), "a field edited");
            EgExpectBreak(egLines.Concat(new[] { "{not json" }).ToList(), "a garbage line");
            var egPTamp = Path.Combine(egTmp, "tamp.ndjson"); File.WriteAllLines(egPTamp, egLines.Select((l, i) => i == 1 ? l.Replace("\"review\"", "\"allow\"") : l));
            Check(!ExtGovernance.Gate(egPTamp, egIdGood.Id).Ok, "a tampered ledger refuses everything, even previously allowed bytes");

            // golden fixture: files with unicode, hidden file, nested folder; .git ignored
            var egFx = Path.Combine(egTmp, "fx"); Directory.CreateDirectory(Path.Combine(egFx, "sub", "dir")); Directory.CreateDirectory(Path.Combine(egFx, ".git"));
            File.WriteAllBytes(Path.Combine(egFx, "manifest.json"), System.Text.Encoding.UTF8.GetBytes("{\"manifest_version\":3,\"name\":\"Sample\",\"version\":\"1.2.3\",\"permissions\":[\"storage\",\"tabs\"],\"host_permissions\":[\"https://example.com/*\"],\"content_scripts\":[{\"matches\":[\"https://example.com/*\"],\"js\":[\"cs.js\"]}]}"));
            File.WriteAllBytes(Path.Combine(egFx, "cs.js"), System.Text.Encoding.UTF8.GetBytes("console.log(\"hi \u00e9\");\n"));
            File.WriteAllBytes(Path.Combine(egFx, "sub", "dir", "a.txt"), Enumerable.Repeat((byte)'A', 1000).ToArray());
            File.WriteAllBytes(Path.Combine(egFx, ".hidden"), new[] { (byte)'x' });
            File.WriteAllText(Path.Combine(egFx, ".git", "config"), "ignored");
            var egFxId = ExtGovernance.ComputeIdentity(egFx);
            Check(egFxId.Id == "0358279f2b4109d4937062d5a43e3dc399f2533377d8e651cd7268ba4eb0d088" && egFxId.Files.Count == 4, "golden identity (unicode, hidden file, nested folder, .git ignored)");
            File.AppendAllText(Path.Combine(egFx, "cs.js"), " ");
            Check(ExtGovernance.ComputeIdentity(egFx).Id != egFxId.Id, "one changed byte changes the identity");
            File.WriteAllBytes(Path.Combine(egFx, "cs.js"), System.Text.Encoding.UTF8.GetBytes("console.log(\"hi \u00e9\");\n"));
            Check(ExtGovernance.ComputeIdentity(egFx).Id == egFxId.Id, "restoring the byte restores the identity");

            // manifest facts
            var egMan = ExtGovernance.ReadManifest(egFx);
            Check(egMan.Name == "Sample" && egMan.ManifestVersion == 3 && egMan.Permissions.SequenceEqual(new[] { "storage", "tabs" }) && egMan.HostPermissions.SequenceEqual(new[] { "https://example.com/*" }) && egMan.ContentScriptMatches.Count == 1, "manifest read (permissions, hosts, content script matches)");
            File.WriteAllText(Path.Combine(egFx, "manifest.json"), "\uFEFF{ // comment\n \"manifest_version\": 2, \"name\": \"Old\", \"version\": \"0.1\", \"permissions\": [\"<all_urls>\", \"*://*/*\", \"storage\", \"http://a.test/*\",], }");
            var egOld = ExtGovernance.ReadManifest(egFx);
            Check(egOld.Name == "Old" && egOld.Permissions.SequenceEqual(new[] { "storage" }) && egOld.HostPermissions.Count == 3, "manifest tolerates BOM, comments and trailing commas; host patterns split out of permissions");
            Check(ExtGovernance.Decide(egOld, "x", egPol).Decision == "deny" && ExtGovernance.Decide(egOld, "x", egPol).Reasons.Contains("denied_permission:<all_urls>"), "policy: <all_urls> is denied");
            var egMv4 = new ExtManifest { Name = "n", ManifestVersion = 4 };
            Check(ExtGovernance.Decide(egMv4, "x", egPol).Reasons.Contains("manifest_version_out_of_range:4"), "policy: manifest version out of range is denied");
            var egUnk = new ExtManifest { Name = "n", ManifestVersion = 3, Permissions = { "somethingNew" } };
            Check(ExtGovernance.Decide(egUnk, "x", egPol).Decision == "review", "policy: unknown permission -> review (conservative)");
            var egPol2 = ExtGovernance.ParsePolicy("{\"allowlist\":[\"x\"],\"blocklist\":[\"y\"],\"allowed_permissions\":[\"storage\"],\"review_permissions\":[\"tabs\"],\"denied_permissions\":[\"debugger\"]}");
            Check(ExtGovernance.Decide(egUnk, "x", egPol2).Decision == "allow", "policy: an allowlisted id turns review into allow");
            Check(ExtGovernance.Decide(egUnk, "y", egPol2).Decision == "deny", "policy: blocklisted id is denied");
            Check(ExtGovernance.Decide(new ExtManifest { ManifestVersion = 3, Permissions = { "debugger" } }, "x", egPol2).Decision == "deny", "policy: a denied permission beats the allowlist");
            bool egBadMan = false; File.WriteAllText(Path.Combine(egFx, "manifest.json"), "{ nope"); try { ExtGovernance.ReadManifest(egFx); } catch (ExtGovException) { egBadMan = true; } Check(egBadMan, "unparseable manifest refused");

            // writing records: install, approve, revoke
            var egLedger = Path.Combine(egTmp, "mine", "ledger.ndjson");
            var egM = new ExtManifest { Name = "Sample \"Q\" \\ \u00e9", Version = "1.0", ManifestVersion = 3, Permissions = { "storage", "tabs" } };
            var egFiles = new List<ExtFile> { new("manifest.json", new string('b', 64), 10), new("a.js", new string('c', 64), 5) };
            var egR1 = ExtGovernance.Record(egLedger, new string('e', 64), egM, egFiles, "review", new[] { "review_permission:tabs" }, new DateTime(2026, 10, 7, 1, 2, 3, 456, DateTimeKind.Utc));
            Check(egR1.Seq == 1 && egR1.PrevHash == ExtGovernance.Genesis && egR1.TsUtc == "2026-10-07T01:02:03.456Z", "first record: seq 1, genesis link, timestamp format");
            Check(!ExtGovernance.Gate(egLedger, new string('e', 64)).Ok, "a review record does not open the gate");
            var egR2 = ExtGovernance.Record(egLedger, new string('e', 64), egM, egFiles, "allow", new[] { "review_permission:tabs", "user_approved_review" }, DateTime.UtcNow);
            Check(egR2.Seq == 2 && egR2.PrevHash == egR1.RecordHash && ExtGovernance.Gate(egLedger, new string('e', 64)).Ok, "user approval appends an allow record and opens the gate");
            var egR3 = ExtGovernance.Record(egLedger, new string('e', 64), egM, egFiles, "deny", new[] { "user_removed" }, DateTime.UtcNow);
            Check(!ExtGovernance.Gate(egLedger, new string('e', 64)).Ok && egR3.Seq == 3, "removing appends a deny record and closes the gate again");
            Check(ExtGovernance.VerifyLedger(egLedger).Count == 3, "the ledger written by C# verifies");
            var egText = File.ReadAllText(egLedger);
            Check(!egText.Contains('\r') && egText.EndsWith("\n"), "records are LF terminated like the PowerShell writer");
            Check(egText.Split('\n', StringSplitOptions.RemoveEmptyEntries).All(l => { using var dj = System.Text.Json.JsonDocument.Parse(l); return ExtGovernance.Canon(ExtGovernance.ToObj(dj.RootElement)) == l; }), "every written line is already in canonical form");
            File.WriteAllText(egLedger, egText.Replace("user_removed", "user_kept"));
            bool egRefused = false; try { ExtGovernance.Record(egLedger, new string('e', 64), egM, egFiles, "allow", Array.Empty<string>(), DateTime.UtcNow); } catch (ExtGovException) { egRefused = true; } Check(egRefused, "nothing is appended to a ledger that does not verify");

            // packages
            byte[] EgZip(params (string Name, string Body)[] entries)
            {
                using var ms = new MemoryStream();
                using (var za = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
                    foreach (var (n, b) in entries) { var e = za.CreateEntry(n); using var w = new StreamWriter(e.Open()); w.Write(b); }
                return ms.ToArray();
            }
            var egOk = EgZip(("manifest.json", "{\"manifest_version\":3,\"name\":\"z\",\"version\":\"1\"}"), ("js/a.js", "1"));
            var egOut = Path.Combine(egTmp, "u1"); Check(ExtPackage.Unpack(egOk, egOut) == egOut && File.Exists(Path.Combine(egOut, "js", "a.js")), "ZIP unpacks");
            var egWrap = EgZip(("repo-main/manifest.json", "{}"), ("repo-main/a.js", "1"));
            Check(Path.GetFileName(ExtPackage.Unpack(egWrap, Path.Combine(egTmp, "u2"))) == "repo-main", "a single wrapping folder (GitHub archive) is resolved");
            foreach (var bad in new[] { "../evil.txt", "..\\evil.txt", "/abs.txt", "C:/x.txt", "a/../../b.txt", "a\\b.txt", "x./y", "con .txt " })
            {
                bool refused = false; try { ExtPackage.Unpack(EgZip(("manifest.json", "{}"), (bad, "x")), Path.Combine(egTmp, "u3-" + Guid.NewGuid().ToString("N"))); } catch (ExtGovException) { refused = true; }
                Check(refused, "unsafe package path refused: " + bad);
            }
            Check(!File.Exists(Path.Combine(egTmp, "evil.txt")), "nothing was written outside the destination");
            bool egNoMan = false; try { ExtPackage.Unpack(EgZip(("a.js", "1")), Path.Combine(egTmp, "u4")); } catch (ExtGovException) { egNoMan = true; } Check(egNoMan, "package without a top-level manifest refused");
            bool egNotZip = false; try { ExtPackage.Unpack(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, Path.Combine(egTmp, "u5")); } catch (ExtGovException) { egNotZip = true; } Check(egNotZip, "random bytes refused");
            // CRX3 and CRX2 wrappers
            byte[] EgCrx(int ver, byte[] zip)
            {
                var hdr = new List<byte>(System.Text.Encoding.ASCII.GetBytes("Cr24")); hdr.AddRange(BitConverter.GetBytes((uint)ver));
                if (ver == 3) { hdr.AddRange(BitConverter.GetBytes((uint)5)); hdr.AddRange(new byte[5]); }
                else { hdr.AddRange(BitConverter.GetBytes((uint)3)); hdr.AddRange(BitConverter.GetBytes((uint)4)); hdr.AddRange(new byte[7]); }
                hdr.AddRange(zip); return hdr.ToArray();
            }
            Check(File.Exists(Path.Combine(ExtPackage.Unpack(EgCrx(3, egOk), Path.Combine(egTmp, "u6")), "manifest.json")), "CRX3 unpacks");
            Check(File.Exists(Path.Combine(ExtPackage.Unpack(EgCrx(2, egOk), Path.Combine(egTmp, "u7")), "manifest.json")), "CRX2 unpacks");
            var egBadCrx = EgCrx(3, egOk); BitConverter.GetBytes(uint.MaxValue).CopyTo(egBadCrx, 8);
            bool egCrxBad = false; try { ExtPackage.Unpack(egBadCrx, Path.Combine(egTmp, "u8")); } catch (ExtGovException) { egCrxBad = true; } Check(egCrxBad, "CRX with an out-of-range header length refused");
            Check(ExtPackage.IsSafeEntryName("a/b/c.js") && ExtPackage.IsSafeEntryName("dir/") && !ExtPackage.IsSafeEntryName("") && !ExtPackage.IsSafeEntryName(new string('a', 500)), "entry-name rules");
        }
        finally { try { Directory.Delete(egTmp, true); } catch { } }
    }
}

Console.WriteLine();
Console.WriteLine("=== Engine version, pop-up and external-link rules (executed C#) ===");
{
    Check(RuntimeCheck.Parse("141.0.3537.57")!.SequenceEqual(new[] { 141, 0, 3537, 57 }) && RuntimeCheck.Parse("142.0.3595.3 canary")![0] == 142 && RuntimeCheck.Parse("109")!.Length == 1, "engine version parsing");
    Check(RuntimeCheck.Parse(null) == null && RuntimeCheck.Parse("") == null && RuntimeCheck.Parse("abc") == null && RuntimeCheck.Parse("1.2.x") == null && RuntimeCheck.Parse("-1.2") == null && RuntimeCheck.Parse("1..2") == null && RuntimeCheck.Parse("1.2.3.4.5.6.7") == null, "garbage version strings are rejected, not guessed");
    Check(RuntimeCheck.Compare(new[] { 141, 0, 1, 1 }, new[] { 141, 0, 1, 2 }) < 0 && RuntimeCheck.Compare(new[] { 142 }, new[] { 141, 9, 9, 9 }) > 0 && RuntimeCheck.Compare(new[] { 141, 0 }, new[] { 141, 0, 0, 0 }) == 0, "version comparison");
    Check(RuntimeCheck.Assess("109.0.1518.78") == RuntimeCheck.Verdict.TooOld && RuntimeCheck.Assess("127.9.9.9") == RuntimeCheck.Verdict.TooOld && RuntimeCheck.Assess("128.0.0.0") == RuntimeCheck.Verdict.Ok && RuntimeCheck.Assess("150.1.2.3") == RuntimeCheck.Verdict.Ok && RuntimeCheck.Assess("nonsense") == RuntimeCheck.Verdict.Unknown, "engine freshness verdict around the floor");
    Check(PopupRules.Decide("https://a.test/", true, false) == PopupVerdict.Allow && PopupRules.Decide("http://a.test/", true, false) == PopupVerdict.Allow && PopupRules.Decide("about:blank", true, false) == PopupVerdict.Allow && PopupRules.Decide("", true, false) == PopupVerdict.Allow, "a pop-up from your click to a web address is allowed");
    Check(PopupRules.Decide("https://a.test/", false, false) == PopupVerdict.BlockedNoGesture && PopupRules.Decide("about:blank", false, false) == PopupVerdict.BlockedNoGesture, "a scripted pop-up is blocked");
    Check(PopupRules.Decide("https://a.test/", false, true) == PopupVerdict.Allow, "a scripted pop-up is allowed for a site you allowed");
    foreach (var u in new[] { "file:///C:/Windows/win.ini", "javascript:alert(1)", "data:text/html,<b>x", "ms-msdt:/id", "search-ms:query=x", "chrome-extension://abc/page.html", "blob:https://a.test/1", "ftp://a.test/", "  FILE:///x" })
        Check(PopupRules.Decide(u, true, true) == PopupVerdict.BlockedScheme, "pop-up to a non-web scheme is always blocked: " + u);
    Check(PopupRules.Decide(null, true, false) == PopupVerdict.Allow, "null address is treated as blank");
    Check(ExternalUriRules.Allowed("mailto:a@b.test", true) && ExternalUriRules.Allowed("TEL:+123", true) && !ExternalUriRules.Allowed("mailto:a@b.test", false), "mailto:/tel: pass only when you clicked");
    foreach (var u in new[] { "ms-msdt:/id PCWDiagnostic", "search-ms:query=a", "ms-officecmd:x", "calculator:", "steam://run/1", "vscode://file/x", "javascript:x", "", "nocolon", ":x", "x" })
        Check(!ExternalUriRules.Allowed(u, true), "external program launch is blocked: '" + u + "'");
    Check(ExternalUriRules.SchemeOf("MS-MSDT:/x") == "ms-msdt" && ExternalUriRules.SchemeOf("nocolon") == "unknown" && ExternalUriRules.SchemeOf(null) == "unknown", "scheme extraction for the status line");
}

Console.WriteLine();
Console.WriteLine("=== Closing and start-up choices (executed C#) ===");
{
    var none = new List<string>();
    Check(StartupRules.Effective("start", "", true) == "fresh" && StartupRules.Effective("restore", "", true) == "restore" && StartupRules.Effective("ask", "", true) == "ask", "start-up: home page / continue / ask me behave as named");
    Check(StartupRules.Effective("restore", "", false) == "fresh" && StartupRules.Effective("ask", "", false) == "fresh" && StartupRules.Effective("start", "restore", false) == "fresh", "start-up: with no saved tabs there is nothing to reopen or ask about");
    Check(StartupRules.Effective("start", "restore", true) == "restore" && StartupRules.Effective("restore", "fresh", true) == "fresh" && StartupRules.Effective("ask", "restore", true) == "restore" && StartupRules.Effective("ask", "fresh", true) == "fresh", "start-up: the choice made in the close window wins, once");
    Check(StartupRules.NormalizeMode("RESTORE") == "restore" && StartupRules.NormalizeMode("ask") == "ask" && StartupRules.NormalizeMode("bogus") == "start" && StartupRules.NormalizeMode(null) == "start", "start-up: unknown modes mean the home page");
    Check(StartupRules.NormalizeNext("Fresh") == "fresh" && StartupRules.NormalizeNext("restore") == "restore" && StartupRules.NormalizeNext("x") == "" && StartupRules.NormalizeCloseAsk("OFF") == "off" && StartupRules.NormalizeCloseAsk("whatever") == "on", "start-up: the saved one-time choice and the close-question switch are checked");

    Check(!CloseRules.Plan(new CloseFacts(1, 1, 0, none, none, true)).Prompt, "close: one ordinary tab closes without a question");
    var several = CloseRules.Plan(new CloseFacts(3, 3, 0, none, none, true));
    Check(several.Prompt && several.OfferSession && several.Warnings.Count == 0, "close: several tabs ask and offer 'reopen these tabs' or 'start fresh'");
    Check(!CloseRules.Plan(new CloseFacts(3, 3, 0, none, none, false)).Prompt, "close: with the question switched off several tabs close quietly");
    Check(!CloseRules.Plan(new CloseFacts(3, 0, 0, none, none, true)).OfferSession, "close: nothing restorable means no reopen choice");
    var dl = CloseRules.Plan(new CloseFacts(1, 1, 2, none, none, false));
    Check(dl.Prompt && !dl.OfferSession && dl.Warnings.Count == 1 && dl.Warnings[0].Contains("2 downloads"), "close: a download in progress always asks, even with the switch off");
    var un = CloseRules.Plan(new CloseFacts(2, 2, 0, new[] { "Order form" }, none, true));
    Check(un.Prompt && un.Warnings.Any(x => x.Contains("Order form")), "close: a page with unsaved typing is named");
    var pdfPlan = CloseRules.Plan(new CloseFacts(1, 1, 0, none, new[] { "report.pdf" }, true));
    Check(pdfPlan.Prompt && pdfPlan.Warnings.Any(x => x.Contains("report.pdf") && x.Contains("PDF")), "close: an open PDF asks, naming it");
    Check(!CloseRules.Plan(new CloseFacts(1, 1, 0, new[] { "x" }, new[] { "y.pdf" }, false)).Prompt, "close: unsaved pages and PDFs are ignored when the switch is off");
    var many = CloseRules.Plan(new CloseFacts(9, 9, 0, Enumerable.Range(1, 9).Select(i => "Page " + i).ToList(), none, true));
    Check(many.Warnings.Any(x => x.Contains("and 5 more")) && !many.Warnings.Any(x => x.Contains("Page 9")), "close: a long list of pages is shortened");
    Check(CloseRules.Clean("a\u0007b\r\nc", "x") == "abc" && CloseRules.Clean("", "fallback") == "fallback" && CloseRules.Clean(new string('z', 80), "x").Length == 50, "close: titles shown in the window lose control characters and are shortened");
    Check(CloseRules.LooksLikePdf("https://x.example/a/Report.PDF") && CloseRules.LooksLikePdf("file:///C:/docs/a.pdf") && !CloseRules.LooksLikePdf("https://x.example/a.pdf.html") && !CloseRules.LooksLikePdf("javascript:alert(1)//.pdf") && !CloseRules.LooksLikePdf(null), "close: PDF addresses are recognised (and nothing else)");
    Check(CloseRules.DefaultNext("start") == "fresh" && CloseRules.DefaultNext("restore") == "restore" && CloseRules.DefaultNext("ask") == "restore", "close: the window pre-selects what matches your start-up setting");
    Check(CloseRules.PageScript.Contains("beforeunload") && CloseRules.PageScript.Contains("isTrusted") && !CloseRules.PageScript.Contains("fetch(") && !CloseRules.PageScript.Contains("XMLHttpRequest") && !CloseRules.PageScript.Contains("postMessage") && !CloseRules.PageScript.Contains("http"), "close: the page note-taker only records two flags and sends nothing anywhere");
}

Console.WriteLine();
Console.WriteLine("=== Weather radar rules (executed C#) ===");
{
    Check(WeatherRadar.NormalizeMode("LIVE") == "live" && WeatherRadar.NormalizeMode("click") == "click" && WeatherRadar.NormalizeMode("zzz") == "auto" && WeatherRadar.NormalizeMode(null) == "auto" && WeatherRadar.NormalizeProvider("RainViewer") == "rainviewer" && WeatherRadar.NormalizeProvider("evil") == "windy", "radar: unknown modes fall back to automatic (shows with my location, asks first if location is off) and unknown services to Windy");
    Check(WeatherRadar.TryCoord("39.5", "-98.35", "4", out var la, out var lo, out var z) && la == 39.5 && lo == -98.35 && z == 4, "radar: good coordinates are accepted");
    Check(!WeatherRadar.TryCoord("91", "0", "4", out _, out _, out _) && !WeatherRadar.TryCoord("0", "181", "4", out _, out _, out _) && !WeatherRadar.TryCoord("0", "0", "1", out _, out _, out _) && !WeatherRadar.TryCoord("0", "0", "12", out _, out _, out _) && !WeatherRadar.TryCoord("abc", "0", "4", out _, out _, out _) && !WeatherRadar.TryCoord("NaN", "0", "4", out _, out _, out _) && !WeatherRadar.TryCoord("1e999", "0", "4", out _, out _, out _) && !WeatherRadar.TryCoord("0", "0", "4.5", out _, out _, out _), "radar: out-of-range, non-numeric and infinite values are refused");
    Check(WeatherRadar.Regions.Select(r => r.Id).Distinct().Count() == WeatherRadar.Regions.Length && WeatherRadar.Regions.All(r => WeatherRadar.TryCoord(r.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture), r.Lon.ToString(System.Globalization.CultureInfo.InvariantCulture), r.Zoom.ToString(), out _, out _, out _)), "radar: every built-in region has a unique id and valid coordinates");
    {
        var fu = WeatherForecast.Url(41.88, -87.63, true);
        Check(fu.StartsWith("https://api.open-meteo.com/v1/forecast?latitude=41.88&longitude=-87.63") && fu.Contains("forecast_days=5") && fu.Contains("temperature_unit=fahrenheit") && fu.Contains("wind_speed_unit=mph") && fu.Contains("hourly=") && fu.Contains("daily="), "forecast: the request is https to Open-Meteo with only a latitude and longitude, 5 days, hourly and daily");
        Check(WeatherForecast.Url(0, 0, false).Contains("temperature_unit=celsius") && WeatherForecast.Url(0, 0, false).Contains("wind_speed_unit=kmh"), "forecast: Celsius uses km/h");
        Check(WeatherForecast.Describe(0) == "Clear" && WeatherForecast.Describe(63) == "Rain" && WeatherForecast.Describe(75) == "Snow" && WeatherForecast.Describe(95) == "Thunderstorm" && WeatherForecast.Describe(12345) == "Mixed", "forecast: weather codes become plain words");
        Check(WeatherForecast.Emoji(0, true) == "☀️" && WeatherForecast.Emoji(0, false) == "\U0001F319" && WeatherForecast.Emoji(63, true) == "\U0001F327️" && WeatherForecast.Emoji(95, true) == "⛈️" && WeatherForecast.Emoji(12345, true).Length > 0, "forecast: the hidden-card chip shows an emoji for the weather (moon at night)");
        Check(WeatherForecast.CityFromZone("America/Chicago") == "Chicago" && WeatherForecast.CityFromZone("America/Argentina/Buenos_Aires") == "Buenos Aires" && WeatherForecast.CityFromZone("<b>x</b>") == "" && WeatherForecast.CityFromZone(null) == "", "forecast: a place name is taken from the time zone only when it is plain letters");
        var fj = ("{'timezone':'America/Chicago','current':{'time':'2026-10-20T14:00','temperature_2m':61.4,'weather_code':2,'is_day':1,'wind_speed_10m':9.6}," +
                  "'hourly':{'time':['2026-10-20T13:00','2026-10-20T14:00','2026-10-20T15:00'],'temperature_2m':[60,61.4,62],'weather_code':[2,2,61],'precipitation_probability':[0,5,40],'is_day':[1,1,1]}," +
                  "'daily':{'time':['2026-10-20','2026-10-21','2026-10-22','2026-10-23','2026-10-24'],'weather_code':[2,61,3,0,71],'temperature_2m_max':[64,58,55,60,40],'temperature_2m_min':[48,44,41,39,30],'precipitation_probability_max':[40,80,20,0,60]}}").Replace('\'', '"');
        Check(WeatherForecast.TryParse(fj, out var fc) && fc != null && fc.Now.Temp == 61 && fc.Now.Wind == 10 && fc.City == "Chicago", "forecast: the current weather and the place are read");
        Check(fc != null && WeatherForecast.ChipText(fc) == "⛅ 61°  \U0001F4A7" + "40%", "forecast: the small chip shows the emoji, temperature and the chance of rain in the next hour");
        Check(fc != null && fc.Hours.Length == 2&& fc.Hours[0].Label == "Now" && fc.Hours[1].Label == "3 PM" && fc.Hours[1].Rain == 40, "forecast: the hour by hour list starts at the current hour");
        Check(fc != null && fc.Days.Length == 5 && fc.Days[0].Name == "Today" && fc.Days[1].Name == "Wednesday" && fc.Days[4].Lo == 30 && fc.Days[1].Text == "Rain", "forecast: five days are listed with names, highs, lows and conditions");
        var fh = fc == null ? "" : WeatherForecast.BodyHtml(fc, true, "");
        Check(fh.Contains("Hour by hour") && fh.Contains("Next 5 days") && fh.Contains("61°F") && fh.Contains("10 mph") && !fh.Contains("Eye"), "forecast: the card shows now, hour by hour and 5 days");
        Check(!WeatherForecast.TryParse("{}", out _) && !WeatherForecast.TryParse("not json", out _) && !WeatherForecast.TryParse(null, out _) && !WeatherForecast.TryParse(new string('x', 500_000), out _), "forecast: broken or oversized answers are refused");
        Check(WeatherForecast.BodyHtml(new Forecast(new WxNow(1, "<script>", 1, true, 0), new[] { new WxHour("Now", 1, 0, "<b>", 0, true) }, new[] { new WxDay("Today", 1, 0, 0, "<i>", 0) }, "x"), true, "<img onerror=1>").Contains("<script>") == false, "forecast: text from the answer is encoded before it is shown");
        var weatherFiles = new[] { "MainWindow.Weather.cs", "MainWindow.WeatherData.cs", "WeatherForecast.cs", "WeatherRadar.cs" };
        var dirWx = AppContext.BaseDirectory; string? browserDir = null;
        for (var dd = new DirectoryInfo(dirWx); dd != null; dd = dd.Parent) if (File.Exists(Path.Combine(dd.FullName, "browser", "MainWindow.Weather.cs"))) { browserDir = Path.Combine(dd.FullName, "browser"); break; }
        if (browserDir != null)
            Check(weatherFiles.All(n => !File.ReadAllText(Path.Combine(browserDir, n)).Contains("God's Eye", StringComparison.OrdinalIgnoreCase) && !File.ReadAllText(Path.Combine(browserDir, n)).Contains("GEV")), "weather: no weather file names God's Eye View anywhere");
    }
    var wEmbed = WeatherRadar.EmbedUrl("windy", 39.5, -98.35, 4, null);
    Check(wEmbed != null && wEmbed.StartsWith("https://embed.windy.com/embed2.html?lat=39.5&lon=-98.35&") && wEmbed.Contains("overlay=radar") && wEmbed.Contains("zoom=4"), "radar: the Windy address carries the chosen place and the radar layer");
    var rEmbed = WeatherRadar.EmbedUrl("rainviewer", 51.5, -0.12, 6, null);
    Check(rEmbed != null && rEmbed.StartsWith("https://www.rainviewer.com/map.html?loc=51.5,-0.12,6"), "radar: the RainViewer address carries the chosen place");
    Check(WeatherRadar.OpenUrl("windy", 39.5, -98.35, 4, null) == "https://www.windy.com/-Weather-radar-radar?radar,39.5,-98.35,4", "radar: the 'open in a tab' link is the provider's own page");
    Check(WeatherRadar.EmbedUrl("custom", 0, 0, 4, null) == null && WeatherRadar.EmbedUrl("custom", 0, 0, 4, "https://eye.example/radar") == "https://eye.example/radar", "radar: your own address is used only when set");
    Check(WeatherRadar.CustomUrl("example.com/radar") == "https://example.com/radar" && WeatherRadar.CustomUrl("https://example.com/a?b=1") == "https://example.com/a?b=1", "radar: a bare site name becomes https");
    foreach (var bad in new[] { "http://example.com/", "javascript:alert(1)", "data:text/html,x", "file:///c:/x.html", "https://user:pw@example.com/", "https://exa mple.com/", "https://localhost/", "https://example.com/\"onload=x", "https://example.com/<b>", "", "   ", null })
        Check(WeatherRadar.CustomUrl(bad) == null, "radar: refused address '" + (bad ?? "null") + "'");
    Check(WeatherRadar.CustomUrl("https://example.com/" + new string('a', 450)) == null, "radar: an over-long address is refused");
}

Console.WriteLine();
Console.WriteLine("=== Themes, top bar styles and the theme library (executed C#) ===");
{
    Check(TopBarStyles.All.Select(s => s.Id).Distinct().Count() == TopBarStyles.All.Length && TopBarStyles.All[0].Id == "match", "top bar: unique styles, 'match theme' first");
    Check(TopBarStyles.Normalize("OCEAN") == "ocean" && TopBarStyles.Normalize("match") == "" && TopBarStyles.Normalize("<script>") == "" && TopBarStyles.Normalize(null) == "", "top bar: only a known style name is kept");
    foreach (var (nm, bar, txt) in new[] { ("dark", "#1b1e24", "#d6dae2"), ("light", "#f1f3f6", "#1f2430") })
    {
        var baseC = ChromeColors.Derive(bar, txt, "#2b6cb0");
        bool dk = Rgb.Luminance(baseC["Bar"]) < 0.5;
        bool okAll = true;
        foreach (var s in TopBarStyles.All)
        {
            var c = new Dictionary<string, string>(baseC);
            TopBarStyles.Apply(c, s.Id);
            okAll &= c.Values.All(Rgb.Valid) && c["Text"] == baseC["Text"] && (Rgb.Luminance(c["Bar"]) < 0.5) == dk && Math.Abs(Rgb.Luminance(c["Text"]) - Rgb.Luminance(c["Bar"])) > 0.3 && c.Keys.SequenceEqual(baseC.Keys);
        }
        Check(okAll, "top bar: every style on a " + nm + " theme gives valid colours, keeps the text colour and the light or dark feel");
    }
    var same = new Dictionary<string, string>(ChromeColors.Derive("#1b1e24", "#d6dae2", "#2b6cb0")); var copy = new Dictionary<string, string>(same);
    TopBarStyles.Apply(same, "match"); TopBarStyles.Apply(same, "nonsense");
    Check(same.OrderBy(k => k.Key).SequenceEqual(copy.OrderBy(k => k.Key)), "top bar: 'match theme' and unknown names change nothing");
    var oceanC = new Dictionary<string, string>(ChromeColors.Derive("#1b1e24", "#d6dae2", "#2b6cb0")); TopBarStyles.Apply(oceanC, "ocean");
    Check(oceanC["Bar"] != "#1b1e24" && oceanC["Accent"] == "#3aa0ee", "top bar: a coloured style really changes the bar and accent");

    Check(StartBackground.Presets.Distinct().Count() == StartBackground.Presets.Length && StartBackground.Presets.Length >= 22, "backgrounds: 22 or more built-in, none repeated");
    Check(StartBackground.Presets.All(p => StartBackground.Preview(p) is string pv && pv.StartsWith("background:") && !pv.Contains("url(") && !pv.Contains("<") && !pv.Contains("}") && !pv.Contains("{")), "backgrounds: every preview is a plain background declaration");
    Check(StartBackground.Css("mint", null).Contains("--txt") && StartBackground.Css("paper", null).Contains("--txt") && !StartBackground.Css("ocean", null).Contains("--txt"), "backgrounds: light ones switch the page text to dark, dark ones do not");
    Check(StartBackground.Preview("image") == null && StartBackground.Preview("#aabbcc") == "background:#aabbcc", "backgrounds: previews for a picture and a colour");
    Check(ThemePacks.Presets.Length >= 22 && ThemePacks.Presets.All(p => Math.Abs(Rgb.Luminance(p.Text) - Rgb.Luminance(p.Bar)) > 0.3), "themes: 22 or more built-in looks, all with readable text");

    var good = "{\"schema\":\"recognition.theme.v1\",\"id\":\"my-theme\",\"name\":\"My theme\",\"bar\":\"#1e1e2e\",\"text\":\"#cdd6f4\",\"accent\":\"#cba6f7\",\"start_bg\":\"nebula\",\"top\":\"violet\"}";
    var gp = ThemeLibrary.ParsePack(good);
    Check(gp != null && gp.Id == "my-theme" && gp.Bar == "#1e1e2e" && gp.StartBg == "nebula" && gp.Top == "violet" && gp.Dark, "theme file: a good pack is read");
    Check(ThemeLibrary.ParsePack(gp != null ? ThemeLibrary.ToJson(gp) : "") is ThemePack rt && rt == gp, "theme file: what is saved reads back identical");
    string Variant(string from, string to) => good.Replace(from, to);
    Check(ThemeLibrary.ParsePack(Variant("#1e1e2e", "red")) == null && ThemeLibrary.ParsePack(Variant("#cba6f7", "#cba6f7;x{}")) == null && ThemeLibrary.ParsePack(Variant("\"my-theme\"", "\"Bad Id\"")) == null && ThemeLibrary.ParsePack(Variant("\"my-theme\"", "\"default\"")) == null && ThemeLibrary.ParsePack(Variant("\"my-theme\"", "\"" + new string('a', 40) + "\"")) == null, "theme file: bad colours, bad ids and built-in ids are refused");
    Check(ThemeLibrary.ParsePack(Variant("recognition.theme.v1", "other")) == null && ThemeLibrary.ParsePack("not json") == null && ThemeLibrary.ParsePack(null) == null && ThemeLibrary.ParsePack(good + new string(' ', 25_000)) == null, "theme file: wrong schema, garbage and oversized files are refused");
    Check(ThemeLibrary.ParsePack(Variant("#cdd6f4", "#1f1f2f")) == null, "theme file: text that cannot be read on the bar is refused");
    var odd = ThemeLibrary.ParsePack(Variant("\"nebula\"", "\"image\"").Replace("\"violet\"", "\"nope\"").Replace("My theme", "<b>My\\u0007 theme</b>"));
    Check(odd != null && odd.StartBg == "default" && odd.Top == "" && !odd.Name.Contains('<') && !odd.Name.Contains('>') && !odd.Name.Any(char.IsControl), "theme file: unknown or unsafe values become safe ones (no picture key, no markup in the name)");
    Check(ThemeLibrary.IsAllowedHost(new Uri("https://raw.githubusercontent.com/a/b/main/x.json")) && !ThemeLibrary.IsAllowedHost(new Uri("https://github.com/a")) && !ThemeLibrary.IsAllowedHost(new Uri("http://raw.githubusercontent.com/x")) && !ThemeLibrary.IsAllowedHost(new Uri("https://raw.githubusercontent.com.evil.example/x")) && !ThemeLibrary.IsAllowedHost(null), "theme library: only raw.githubusercontent.com over https");

    var lib70 = "{\"schema\":\"recognition.theme.library.v1\",\"themes\":[" + string.Join(",", Enumerable.Range(0, 70).Select(i => good.Replace("my-theme", "t" + i).Replace("\"schema\":\"recognition.theme.v1\",", ""))) + "]}";
    Check(ThemeLibrary.ParseLibrary(lib70).Count == ThemeLibrary.MaxPacks, "theme library: at most 60 themes are read");
    var dupLib = "{\"schema\":\"recognition.theme.library.v1\",\"themes\":[" + good.Replace("\"schema\":\"recognition.theme.v1\",", "") + "," + good.Replace("\"schema\":\"recognition.theme.v1\",", "") + "]}";
    Check(ThemeLibrary.ParseLibrary(dupLib).Count == 1 && ThemeLibrary.ParseLibrary("{\"schema\":\"x\",\"themes\":[]}").Count == 0 && ThemeLibrary.ParseLibrary("[]").Count == 0 && ThemeLibrary.ParseLibrary(null).Count == 0, "theme library: duplicates are dropped; wrong shapes give an empty list");

    string? tRoot = null;
    for (var td = new DirectoryInfo(AppContext.BaseDirectory); td != null; td = td.Parent)
        if (File.Exists(Path.Combine(td.FullName, "themes", "library.v1.json"))) { tRoot = td.FullName; break; }
    Check(tRoot != null, "theme library: the shipped themes\\library.v1.json is found");
    if (tRoot != null)
    {
        var libText = File.ReadAllText(Path.Combine(tRoot, "themes", "library.v1.json"));
        int declared; using (var ld = JsonDocument.Parse(libText)) declared = ld.RootElement.GetProperty("themes").GetArrayLength();
        var parsed = ThemeLibrary.ParseLibrary(libText);
        Check(declared >= 18 && parsed.Count == declared, "theme library: all " + declared + " shipped themes pass the checks (none silently dropped)");
        Check(libText.Length < ThemeLibrary.MaxLibraryBytes && !libText.Contains("http", StringComparison.OrdinalIgnoreCase), "theme library: the shipped list is small and holds no addresses");
    }
    string? wRoot = null;
    for (var wd = new DirectoryInfo(AppContext.BaseDirectory); wd != null; wd = wd.Parent)
        if (File.Exists(Path.Combine(wd.FullName, "browser", "MainWindow.Weather.cs"))) { wRoot = wd.FullName; break; }
    if (wRoot != null)
    {
        var wsrc = File.ReadAllText(Path.Combine(wRoot, "browser", "MainWindow.Weather.cs"));
        var tsrc = File.ReadAllText(Path.Combine(wRoot, "browser", "MainWindow.Themes.cs"));
        Check(wsrc.Contains("Nothing is requested") && wsrc.Contains("_wxMode == \"live\"") && !wsrc.Contains("HttpClient"), "radar: the card loads nothing until clicked unless 'Always show' is chosen, and the program itself makes no request");
        Check(tsrc.Contains("ThemeLibrary.IsAllowedHost") && tsrc.Contains("cz-lib-fetch") && tsrc.Contains("ThemeLibrary.ParsePack"), "theme library: the download checks the host and every theme is validated");
    }
}

Console.WriteLine();
Console.WriteLine("=== Update from GitHub rules (executed C#) ===");
{
    string UpRel(string tag, string zipUrl, string shaUrl, long size, string extra = "") =>
        "{\"tag_name\":\"" + tag + "\"," + extra + "\"assets\":[{\"name\":\"Recognition-win-x64.zip\",\"size\":" + size + ",\"browser_download_url\":\"" + zipUrl + "\"},{\"name\":\"Recognition-win-x64.zip.sha256\",\"size\":90,\"browser_download_url\":\"" + shaUrl + "\"}]}";
    const string upZip = "https://github.com/Alpallyoop/Recognition/releases/download/v1.4.0/Recognition-win-x64.zip";
    const string upSha = upZip + ".sha256";
    var upOk = SelfUpdateRules.ParseRelease(UpRel("v1.4.0", upZip, upSha, 90_000_000));
    Check(upOk != null && upOk.Version == new Version(1, 4, 0) && upOk.ZipUrl == upZip && upOk.ShaUrl == upSha, "update: a normal release is read (version, zip and checksum addresses)");
    Check(SelfUpdateRules.ParseRelease(UpRel("v1.4.0", "https://evil.example/Recognition-win-x64.zip", upSha, 90_000_000)) == null, "update: a zip that is not on a GitHub host is refused");
    Check(SelfUpdateRules.ParseRelease(UpRel("v1.4.0", "http://github.com/x/Recognition-win-x64.zip", upSha, 90_000_000)) == null, "update: a plain http download address is refused");
    Check(SelfUpdateRules.ParseRelease(UpRel("v1.4.0", upZip, "https://github.com.evil.example/a.sha256", 90_000_000)) == null, "update: a look-alike host (github.com.evil.example) is refused");
    Check(SelfUpdateRules.ParseRelease(UpRel("v1.4.0", upZip, upSha, 700L * 1024 * 1024)) == null, "update: a zip over the size cap is refused");
    Check(SelfUpdateRules.ParseRelease(UpRel("v1.4.0", upZip, upSha, 90_000_000, "\"draft\":true,")) == null && SelfUpdateRules.ParseRelease(UpRel("v1.4.0", upZip, upSha, 90_000_000, "\"prerelease\":true,")) == null, "update: drafts and pre-releases are ignored");
    Check(SelfUpdateRules.ParseRelease("{\"tag_name\":\"v1.4.0\",\"assets\":[{\"name\":\"Recognition-win-x64.zip\",\"size\":5,\"browser_download_url\":\"" + upZip + "\"}]}") == null, "update: a release without the .sha256 file is refused (no unchecked installs)");
    Check(SelfUpdateRules.ParseRelease("not json") == null && SelfUpdateRules.ParseRelease(UpRel("latest", upZip, upSha, 5)) == null, "update: garbage and non-version tags are refused");
    Check(SelfUpdateRules.IsNewer("1.3.9", new Version(1, 3, 10)) && SelfUpdateRules.IsNewer("1.3.9", new Version(1, 4, 0)) && !SelfUpdateRules.IsNewer("1.3.9", new Version(1, 3, 9)) && !SelfUpdateRules.IsNewer("1.4.0", new Version(1, 3, 10)), "update: only a strictly newer version counts (1.3.10 is newer than 1.3.9)");
    Check(!SelfUpdateRules.IsNewer("unknown", new Version(9, 0, 0)), "update: an unreadable installed version never triggers an install");
    Check(SelfUpdateRules.IsAllowedHost(new Uri("https://objects.githubusercontent.com/x")) && SelfUpdateRules.IsAllowedHost(new Uri("https://api.github.com/x")) && !SelfUpdateRules.IsAllowedHost(new Uri("https://evilgithub.com/x")) && !SelfUpdateRules.IsAllowedHost(new Uri("https://x.example/github.com")) && !SelfUpdateRules.IsAllowedHost(null), "update: only GitHub hosts are allowed");
    var upH = new string('a', 64);
    Check(SelfUpdateRules.ParseSha256(upH + "  Recognition-win-x64.zip\n") == upH && SelfUpdateRules.ParseSha256(upH.ToUpperInvariant()) == upH && SelfUpdateRules.ParseSha256("abc") == null && SelfUpdateRules.ParseSha256("") == null && SelfUpdateRules.ParseSha256(new string('g', 64)) == null, "update: the checksum file must be a real 64-digit SHA-256");
    string? uRoot = null;
    for (var ud = new DirectoryInfo(AppContext.BaseDirectory); ud != null; ud = ud.Parent)
        if (File.Exists(Path.Combine(ud.FullName, "browser", "MainWindow.SelfUpdate.cs"))) { uRoot = ud.FullName; break; }
    var uSrc = uRoot == null ? "" : File.ReadAllText(Path.Combine(uRoot, "browser", "MainWindow.SelfUpdate.cs"));
    Check(uSrc.Contains("MessageBox.Show") && !uSrc.Contains("msg") && uSrc.Contains("SelfUpdateRules.IsAllowedHost") && uSrc.Contains("got != want"), "update: asks before installing, uses no page text, checks the host and the checksum");
}

Console.WriteLine();
Console.WriteLine("=== Host message gate and download rules (executed C#) ===");
{
    // The privilege boundary: web content must never reach the command handlers.
    string[] commands = { "site-perm:evil.com:Camera:allow", "cert-trust:evil.com:der-0123456789abcdef:allow", "cookies-clear-all", "update-apply", "update-check", "wx-mode:live", "wx-custom:https%3A%2F%2Fevil.example", "cz-top:ocean", "cz-lib-fetch", "cz-pack-import", "ws-closeask:off", "ws-startup:ask", "net-forget:Home", "toggle-blocking", "vpn-off", "export-session", "open-profile", "appearance-set:bg:#000000", "pw-reveal:abc", "setup-apply:{}", "tools-pdf-run:{}", "rmbookmark:https://x/", "open:https://evil.example/" };
    Check(commands.All(c => MessageGate.Classify(false, "https://evil.example/", c) == MessageVerdict.Deny), "gate: a normal web tab cannot send ANY command, whatever its origin");
    Check(commands.All(c => MessageGate.Classify(true, "https://evil.example/", c) == MessageVerdict.Deny), "gate: even in an 'internal' tab, a message that comes from an http(s) document is refused (tab navigated away, cross-origin frame)");
    Check(commands.All(c => new[] { "http://x/", "file:///c:/x.html", "ftp://x/", "ws://x/", "wss://x/", "blob:https://x/1", "filesystem:https://x/t/a", "HTTPS://X/", "  https://x/" }.All(s => MessageGate.Classify(true, s, c) == MessageVerdict.Deny)), "gate: every web-like source scheme (http, https, file, ftp, ws, wss, blob, filesystem, any case or leading space) is refused");
    Check(commands.All(c => MessageGate.Classify(true, "about:blank", c) == MessageVerdict.Command && MessageGate.Classify(true, "data:text/html;charset=utf-8,x", c) == MessageVerdict.Command && MessageGate.Classify(true, "", c) == MessageVerdict.Command && MessageGate.Classify(true, null, c) == MessageVerdict.Command), "gate: the browser's own internal pages (about:, data: or empty source) can still send commands");
    Check(commands.All(c => MessageGate.Classify(false, "about:blank", c) == MessageVerdict.Deny && MessageGate.Classify(false, null, c) == MessageVerdict.Deny), "gate: a non-internal tab is refused even with an about:blank or empty source");
    Check(MessageGate.Classify(false, "https://evil.example/", "sc:newtab") == MessageVerdict.Shortcut && MessageGate.Classify(true, "about:blank", "sc:reload") == MessageVerdict.Shortcut, "gate: keyboard shortcuts (sc:) are the only thing web content may send; they use a separate allowlist");
    Check(MessageGate.Classify(true, "about:blank", "") == MessageVerdict.Deny && MessageGate.Classify(true, "about:blank", null) == MessageVerdict.Deny, "gate: empty messages are refused");
    Check(MessageGate.Classify(true, "about:blank", "SC:newtab") == MessageVerdict.Command, "gate: prefix matching is case-sensitive ('SC:' is not a shortcut, it is just an unknown command)");
    Check(MessageGate.IsTabShortcut("newtab") && MessageGate.IsTabShortcut("newprivate") && MessageGate.IsTabShortcut("closetab") && !MessageGate.IsTabShortcut("reload") && !MessageGate.IsTabShortcut("find"), "gate: tab-creating/closing shortcuts are the rate-limited set");

    Check(DownloadRules.Assess("setup.exe") == DownloadRisk.Executable && DownloadRules.Assess("INSTALL.MSI") == DownloadRisk.Executable && DownloadRules.Assess("run.ps1") == DownloadRisk.Executable && DownloadRules.Assess("x.bat") == DownloadRisk.Executable && DownloadRules.Assess("a.lnk") == DownloadRisk.Executable && DownloadRules.Assess("a.hta") == DownloadRisk.Executable && DownloadRules.Assess("a.jar") == DownloadRisk.Executable, "downloads: programs, installers, scripts and shortcuts need confirmation");
    Check(DownloadRules.Assess("invoice.pdf.exe") == DownloadRisk.Executable && DownloadRules.Assess("setup.exe.") == DownloadRisk.Executable && DownloadRules.Assess("setup.exe  ") == DownloadRisk.Executable && DownloadRules.Assess("setup.exe . ") == DownloadRisk.Executable, "downloads: double extensions and trailing dots/spaces (which Windows ignores) are still recognised");
    Check(DownloadRules.Assess("report.pdf") == DownloadRisk.None && DownloadRules.Assess("photo.jpg") == DownloadRisk.None && DownloadRules.Assess("data.csv") == DownloadRisk.None && DownloadRules.Assess("archive.zip") == DownloadRisk.None && DownloadRules.Assess("notes") == DownloadRisk.None && DownloadRules.Assess("") == DownloadRisk.None && DownloadRules.Assess(null) == DownloadRisk.None, "downloads: documents, images, data files, archives and extension-less names are not prompted");
    Check(DownloadRules.Assess("photo\u202Egpj.exe") == DownloadRisk.DeceptiveName && DownloadRules.Assess("a\u202Etxt.js") == DownloadRisk.DeceptiveName && DownloadRules.Assess("x\u200Fy.pdf") == DownloadRisk.DeceptiveName, "downloads: right-to-left override and other direction-control characters in a name are flagged as deceptive");
}

Console.WriteLine();
Console.WriteLine("=== Privacy rules (executed C#) ===");
{
    Check(PrivacyRules.HttpsUpgrade("http://example.com/a?b=1#c") == "https://example.com/a?b=1#c", "https upgrade: plain http becomes https, path/query/fragment kept");
    Check(PrivacyRules.HttpsUpgrade("http://example.com:8080/x") == "https://example.com:8080/x", "https upgrade: non-default port is kept");
    Check(PrivacyRules.HttpsUpgrade("https://example.com/") == null && PrivacyRules.HttpsUpgrade("ftp://example.com/") == null && PrivacyRules.HttpsUpgrade("not a url") == null && PrivacyRules.HttpsUpgrade(null) == null, "https upgrade: https, other schemes, garbage and null are left alone");
    Check(PrivacyRules.HttpsUpgrade("http://localhost:3000/") == null && PrivacyRules.HttpsUpgrade("http://127.0.0.1/") == null && PrivacyRules.HttpsUpgrade("http://127.5.5.5:81/") == null && PrivacyRules.HttpsUpgrade("http://[::1]:8080/") == null && PrivacyRules.HttpsUpgrade("http://app.localhost/") == null, "https upgrade: loopback (localhost, 127.x, ::1, *.localhost) is exempt");
    Check(PrivacyRules.HttpsUpgrade("http://localhost.evil.com/") != null && PrivacyRules.HttpsUpgrade("http://127.0.0.1.evil.com/") != null && PrivacyRules.HttpsUpgrade("http://localhostevil.com/") != null, "https upgrade: look-alike hosts (localhost.evil.com, 127.0.0.1.evil.com) are NOT exempt (regression: old prefix check let these through)");
    Check(PrivacyRules.StripTrackingParams("https://a.example/p?utm_source=x&id=7&fbclid=abc#top", out var n1) == "https://a.example/p?id=7#top" && n1 == 2, "strip: tracking params removed, real params and fragment kept");
    Check(PrivacyRules.StripTrackingParams("https://a.example/p?gclid=1&UTM_Medium=m", out var n2) == "https://a.example/p" && n2 == 2, "strip: all params tracking -> no dangling '?'; matching is case-insensitive");
    var same = "https://a.example/p?q=a%20b&x=1&&y";
    Check(PrivacyRules.StripTrackingParams(same, out var n3) == same && n3 == 0, "strip: URL without tracking params is returned byte-for-byte unchanged");
    Check(PrivacyRules.StripTrackingParams("https://a.example/p?%75tm_source=x&k=v", out var n4) == "https://a.example/p?k=v" && n4 == 1, "strip: percent-encoded parameter names are decoded before matching");
    Check(PrivacyRules.StripTrackingParams("https://a.example/p?utmx=1&my_utm_source=2&ref=news&q=gclid", out var n5) == "https://a.example/p?utmx=1&my_utm_source=2&ref=news&q=gclid" && n5 == 0, "strip: near-miss names and values containing tracking words are not touched");
    Check(PrivacyRules.StripTrackingParams("javascript:alert(1)?utm_source=x", out var n6) == "javascript:alert(1)?utm_source=x" && PrivacyRules.StripTrackingParams("file:///c:/a?utm_source=x", out _) == "file:///c:/a?utm_source=x" && PrivacyRules.StripTrackingParams(null, out _) == "" && n6 == 0, "strip: only http(s) URLs are ever rewritten");
    Check(PrivacyRules.StripTrackingParams("https://a.example/p?a=1&mc_eid=9&b=2&_ga=3", out var n7) == "https://a.example/p?a=1&b=2" && n7 == 2, "strip: order of the remaining parameters is preserved");
}

Console.WriteLine();
Console.WriteLine("=== Password vault (executed C#) ===");
{
    var nowV = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
    // generator
    var gen = PasswordGenerator.Generate(24);
    Check(gen.Length == 24 && gen.Any(char.IsLower) && gen.Any(char.IsUpper) && gen.Any(char.IsDigit) && gen.Any(c => PasswordGenerator.Symbols.Contains(c)), "generator: requested length and at least one of each chosen character type");
    Check(Enumerable.Range(0, 200).All(_ => { var x = PasswordGenerator.Generate(8); return x.Any(char.IsLower) && x.Any(char.IsUpper) && x.Any(char.IsDigit) && x.Any(c => PasswordGenerator.Symbols.Contains(c)); }), "generator: 200 minimum-length passwords all contain every chosen type");
    Check(Enumerable.Range(0, 200).All(_ => PasswordGenerator.Generate(40).All(c => PasswordGenerator.Ambiguous.IndexOf(c) < 0)), "generator: ambiguous characters (O 0 o I l 1 | quotes) excluded when asked");
    Check(Enumerable.Range(0, 100).All(_ => PasswordGenerator.Generate(30, true, false, true, false).All(c => char.IsLower(c) || char.IsDigit(c))), "generator: unchecked types never appear");
    Check(Enumerable.Range(0, 100).Select(_ => PasswordGenerator.Generate(20)).Distinct().Count() == 100, "generator: 100 passwords are all distinct");
    var digitsOnly = string.Concat(Enumerable.Range(0, 100).Select(_ => PasswordGenerator.Generate(128, false, false, true, false, false)));
    var counts = digitsOnly.GroupBy(c => c).ToDictionary(x => x.Key, x => x.Count());
    Check(counts.Count == 10 && counts.Values.All(n => n > 1100 && n < 1460), "generator: digit frequencies over 12,800 draws are near-uniform (expected 1280 each)");
    Check(Throws(() => PasswordGenerator.Generate(7)) && Throws(() => PasswordGenerator.Generate(129)) && Throws(() => PasswordGenerator.Generate(16, false, false, false, false)), "generator: length outside 8..128 and no character types are refused");
    bool Throws(Action a) { try { a(); return false; } catch (ArgumentException) { return true; } }

    // origins
    Check(PasswordRules.NormalizeOrigin(" Example.COM ") == "example.com" && PasswordRules.NormalizeOrigin("example.com:8443") == "example.com:8443", "origin: trimmed, lower-cased, port kept");
    Check(new[] { "https://example.com", "example.com/path", "exa mple.com", "", "-bad.com", "example.com:99999", "a@b.com", "ex\u0001.com" }.All(s => PasswordRules.NormalizeOrigin(s) == null), "origin: scheme, path, spaces, userinfo, bad port, control characters all rejected");
    Check(PasswordRules.OriginOf("https://Example.com/login?x=1") == "example.com" && PasswordRules.OriginOf("https://example.com:8443/") == "example.com:8443" && PasswordRules.OriginOf("http://example.com:80/") == "example.com", "origin of URL: default ports dropped, others kept");
    Check(PasswordRules.OriginOf("https://example.com@evil.com/") == null && PasswordRules.OriginOf("javascript:alert(1)") == null && PasswordRules.OriginOf("file:///c:/x") == null && PasswordRules.OriginOf(null) == null, "origin of URL: userinfo trick, javascript:, file:, null refused");
    Check(PasswordRules.MayFill("example.com", "https://example.com/login"), "fill: exact https origin allowed");
    Check(!PasswordRules.MayFill("example.com", "https://login.example.com/") && !PasswordRules.MayFill("example.com", "https://example.com.evil.com/") && !PasswordRules.MayFill("example.com", "https://evilexample.com/") && !PasswordRules.MayFill("example.com", "https://example.com:8443/"), "fill: subdomain, suffix look-alike, prefix look-alike and other port all refused");
    Check(!PasswordRules.MayFill("example.com", "http://example.com/") && !PasswordRules.MayFill("example.com", "https://example.com@evil.com/"), "fill: plain http on the internet and userinfo trick refused");
    Check(PasswordRules.MayFill("localhost:3000", "http://localhost:3000/app") && !PasswordRules.MayFill("localhost:3000", "http://localhost:3001/"), "fill: http allowed only for localhost, and the port must match");

    // vault
    var vs = new Dictionary<string, string>();
    PasswordVault NewVault() => new PasswordVault("mem://vault", p => vs.TryGetValue(p, out var v) ? v : "", (p, t) => vs[p] = t);
    PasswordVault Loaded() { var x = NewVault(); x.Load(); return x; }
    var vault = NewVault();
    Check(vault.Load() == 0 && vault.Entries.Count == 0, "vault: empty store loads as empty");
    Check(vault.Add("Example.com", "alice", "S3cret!pass-word", "note", nowV, out var e1) == null && e1 != null && e1.Origin == "example.com" && e1.Id.Length == 24, "vault: add normalises the origin and assigns a random id");
    Check(vault.Add("example.com", "alice", "other", "", nowV, out _) != null, "vault: duplicate (site, username) refused");
    Check(vault.Add("example.com", "bob", "pw-for-bob-123!", "", nowV, out var e2) == null, "vault: a second username on the same site is fine");
    Check(vault.Add("https://example.com", "x", "y", "", nowV, out _) != null && vault.Add("example.com", "x", "", "", nowV, out _) != null && vault.Add("example.com", "x\u0000", "y", "", nowV, out _) != null && vault.Add("example.com", "x", "y", new string('n', 3000), nowV, out _) != null, "vault: bad origin, empty password, control char, oversized note all refused");
    var v2 = NewVault();
    Check(v2.Load() == 0 && v2.Entries.Count == 2 && v2.Get(e1!.Id)!.Password == "S3cret!pass-word" && v2.Get(e1.Id)!.Note == "note", "vault: entries survive a reload from storage");
    Check(v2.ForPage("https://example.com/login").Count == 2 && v2.ForPage("https://login.example.com/").Count == 0 && v2.ForPage("http://example.com/").Count == 0, "vault: ForPage returns only exact-origin entries");
    Check(v2.Update(e2!.Id, "bob", "NewPass-987654!x", "", nowV.AddMinutes(1)) == null && Loaded().Get(e2.Id)!.Password == "NewPass-987654!x", "vault: update persists");
    Check(v2.Update(e2.Id, "alice", "NewPass-987654!x", "", nowV) != null && v2.Update("nope", "a", "b", "", nowV) != null, "vault: update cannot collide with another username, unknown id refused");
    Check(v2.Remove(e1.Id) && !v2.Remove(e1.Id) && Loaded().Entries.Count == 1, "vault: remove persists, removing twice is a no-op");

    // damaged storage
    vs["mem://vault"] = "{ not json";
    var dmg = NewVault();
    Check(dmg.Load() == 1 && dmg.Entries.Count == 0, "vault: unreadable storage loads empty and reports the damage instead of throwing");
    vs["mem://vault"] = "{\"schema\":\"recognition.vault.v1\",\"entries\":[{\"id\":\"aaaaaaaaaaaaaaaaaaaaaaaa\",\"origin\":\"ok.example\",\"username\":\"u\",\"password\":\"pw\"},{\"id\":\"short\",\"origin\":\"ok.example\",\"password\":\"pw\"},{\"id\":\"bbbbbbbbbbbbbbbbbbbbbbbb\",\"origin\":\"https://bad/\",\"password\":\"pw\"},{\"id\":\"cccccccccccccccccccccccc\",\"origin\":\"ok.example\",\"password\":\"\"},{\"id\":\"aaaaaaaaaaaaaaaaaaaaaaaa\",\"origin\":\"dup.example\",\"password\":\"pw\"}]}";
    var part = NewVault();
    Check(part.Load() == 4 && part.Entries.Count == 1 && part.Entries[0].Origin == "ok.example", "vault: damaged records (short id, bad origin, empty password, duplicate id) skipped, the good one kept");

    // strength and audit
    Check(PasswordRules.Strength("password1!") == "weak" && PasswordRules.Strength("aaaaaaaaaaaa") == "weak" && PasswordRules.Strength("") == "weak", "strength: common words, heavy repetition and empty are weak");
    Check(PasswordRules.Strength("Tr0ub4dor&3") != "weak" && PasswordRules.Strength(PasswordGenerator.Generate(20)) == "strong", "strength: mixed 11-char is not weak, a generated 20-char password is strong");
    var av = new PasswordVault("mem://audit", _ => "", (_, __) => { });
    av.Add("a.example", "u", "Zq7!mK2-vXw9#Lp4", "", nowV, out var a1); av.Add("b.example", "u", "Zq7!mK2-vXw9#Lp4", "", nowV, out var a2);
    av.Add("c.example", "u", "123456", "", nowV, out var a3); av.Add("d.example", "u", PasswordGenerator.Generate(24), "", nowV, out var a4);
    var aud = av.Audit();
    Check(aud.Count(i => i.Kind == "reused") == 2 && aud.Any(i => i.Id == a1!.Id && i.Kind == "reused") && aud.Any(i => i.Id == a2!.Id && i.Kind == "reused"), "audit: reused password flagged on both entries");
    Check(aud.Any(i => i.Id == a3!.Id && i.Kind == "weak") && !aud.Any(i => i.Id == a4!.Id), "audit: weak password flagged, strong unique one is clean");

    // fill script
    var fs = FillScript.Build("al\"ice</script>'", "p\\w'd\u2028\"x");
    Check(!fs.Contains('\n') && fs.Contains("al\\u0022ice") && fs.Contains("\\u003C/script\\u003E") && fs.EndsWith(")"), "fill script: a single line; values are JSON-escaped literals (</script>, quotes, backslash, U+2028 cannot break out)");
    Check(fs.Contains("input[type=password]") && fs.Contains("dispatchEvent"), "fill script: targets password fields and notifies the page");
}

// ---- SoteriaVault link: the contract is checked against SoteriaVault's own rules, and nothing else is trusted --------------------------
{
    const string svContract = "{\"schema\":\"soteriavault.connector_contract.v1\",\"name\":\"recognition\",\"kind\":\"browser_password_manager_bridge\",\"phase\":\"contract_only\",\"enabled\":false,\"required_for_standalone\":false,\"preferred_after_standalone\":false,\"dependency_policy\":\"must_not_fail_if_absent\",\"purpose\":\"x\",\"allowed_now\":[\"declare_contract\",\"emit_readiness\",\"emit_receipts\",\"remain_absent_without_failure\"],\"forbidden_before_standalone_seal\":[\"hard_runtime_dependency\",\"secret_release_to_external_runtime\",\"required_external_verification\",\"engine_failure_if_missing\"],\"future_allowed_after_standalone\":[\"signed_receipt_witness\"]}";
    const string svRegistry = "{\"schema\":\"soteriavault.connector_registry.v1\",\"standalone_first\":true,\"hard_external_dependencies_allowed\":false,\"missing_connectors_must_not_fail_engine\":true,\"connectors\":[]}";
    Func<string?, string?, Func<string, byte[]?>> svFiles = (svc, svr) => svrel => svrel == SoteriaBridge.ContractRel ? (svc == null ? null : System.Text.Encoding.UTF8.GetBytes(svc)) : svrel == SoteriaBridge.RegistryRel ? (svr == null ? null : System.Text.Encoding.UTF8.GetBytes(svr)) : null;
    const string svRoot = "C:\\dev\\privacy-sector";

    var svNone = SoteriaBridge.Evaluate("", svFiles(svContract, svRegistry));
    Check(svNone.State == SoteriaState.NotConfigured, "soteria: no folder chosen -> not configured (nothing is searched for)");
    var svOk = SoteriaBridge.Evaluate(svRoot, svFiles(svContract, svRegistry));
    Check(svOk.State == SoteriaState.ContractOnly && svOk.Capabilities.Contains("emit_receipts") && svOk.ContractSha256.Length == 64, "soteria: the real contract (contract_only, disabled) -> declared, not switched on, with a fingerprint");
    var svOk2 = SoteriaBridge.Evaluate(svRoot, svFiles(svContract, svRegistry));
    Check(svOk.ContractSha256 == svOk2.ContractSha256, "soteria: the fingerprint is deterministic");
    var svLive = SoteriaBridge.Evaluate(svRoot, svFiles(svContract.Replace("\"phase\":\"contract_only\"", "\"phase\":\"active\"").Replace("\"enabled\":false", "\"enabled\":true"), svRegistry));
    Check(svLive.State == SoteriaState.Ready && svLive.ContractSha256 != svOk.ContractSha256, "soteria: switched on by SoteriaVault -> ready, and the fingerprint changes with the file");
    Check(SoteriaBridge.Evaluate(svRoot, svFiles(svContract.Replace("\"enabled\":false", "\"enabled\":true"), svRegistry)).State == SoteriaState.ContractOnly, "soteria: enabled but still contract_only -> still not switched on");
    Check(SoteriaBridge.Evaluate(svRoot, svFiles(null, svRegistry)).State == SoteriaState.Unavailable, "soteria: no contract file -> unavailable (the link fails soft)");
    Check(SoteriaBridge.Evaluate(svRoot, svFiles(svContract, null)).State == SoteriaState.Invalid, "soteria: contract without a registry -> refused");
    Check(SoteriaBridge.Evaluate(svRoot, svrel1 => throw new IOException("denied")).State == SoteriaState.Unavailable, "soteria: unreadable files -> unavailable, never an exception");
    foreach (var (svLabel, svBad) in new[]
    {
        ("required for standalone", svContract.Replace("\"required_for_standalone\":false", "\"required_for_standalone\":true")),
        ("hard dependency policy", svContract.Replace("must_not_fail_if_absent", "fail_if_absent")),
        ("secret release not forbidden", svContract.Replace("\"secret_release_to_external_runtime\",", "")),
        ("hard dependency not forbidden", svContract.Replace("\"hard_runtime_dependency\",", "")),
        ("wrong name", svContract.Replace("\"name\":\"recognition\"", "\"name\":\"rebound\"")),
        ("wrong kind", svContract.Replace("browser_password_manager_bridge", "other")),
        ("future schema", svContract.Replace("connector_contract.v1", "connector_contract.v2")),
        ("missing required_for_standalone", svContract.Replace("\"required_for_standalone\":false,", "")),
        ("not json", "{ nope"),
        ("array root", "[]"),
        ("empty", ""),
    })
        Check(SoteriaBridge.Evaluate(svRoot, svFiles(svBad, svRegistry)).State == SoteriaState.Invalid, "soteria: contract refused when it breaks SoteriaVault's own rules (" + svLabel + ")");
    foreach (var (svLabel2, svBadReg) in new[]
    {
        ("hard external dependencies allowed", svRegistry.Replace("\"hard_external_dependencies_allowed\":false", "\"hard_external_dependencies_allowed\":true")),
        ("not standalone-first", svRegistry.Replace("\"standalone_first\":true", "\"standalone_first\":false")),
        ("unknown registry schema", svRegistry.Replace("registry.v1", "registry.v9")),
        ("registry not json", "oops"),
    })
        Check(SoteriaBridge.Evaluate(svRoot, svFiles(svContract, svBadReg)).State == SoteriaState.Invalid, "soteria: registry refused (" + svLabel2 + ")");
    Check(SoteriaBridge.Evaluate(svRoot, svrel2 => new byte[SoteriaBridge.MaxFileBytes + 1]).State == SoteriaState.Invalid, "soteria: an oversized contract file is not trusted");
    var svOdd = SoteriaBridge.Evaluate(svRoot, svFiles(svContract.Replace("\"emit_receipts\"", "\"Emit Receipts; rm -rf\""), svRegistry));
    Check(svOdd.State == SoteriaState.ContractOnly && !svOdd.Capabilities.Any(svcap => svcap.Contains(' ') || svcap.Contains(';')), "soteria: capability names from the file are filtered before they reach the page");

    foreach (var svGood in new[] { "C:\\dev\\privacy-sector", "D:/tools/sv", "c:\\x\\y\\" }) Check(SoteriaBridge.IsSafeRoot(svGood), "soteria: accepted folder " + svGood);
    var svBadList = new[] { "", "   ", "privacy-sector", "..\\x", "\\\\server\\share\\sv", "\\\\?\\C:\\sv", "C:\\a\\..\\b", "C:\\a\\.\\b", "C:sv", "C:\\a:stream", "C:\\a|b", "C:\\a\"b", "C:\\a\u0000b", "C:\\" + new string('a', 300) };
    foreach (var svBad2 in svBadList)
        Check(!SoteriaBridge.IsSafeRoot(svBad2), "soteria: refused folder #" + Array.IndexOf(svBadList, svBad2));

    // SoteriaVault as the password manager (docs\proposals\0006): only with the explicit capability, and bulk release stays forbidden
    var svPmContract = svContract.Replace("\"phase\":\"contract_only\"", "\"phase\":\"bridge_enabled\"").Replace("\"enabled\":false", "\"enabled\":true")
        .Replace("\"allowed_now\":[", "\"allowed_now\":[\"per_fill_release_to_paired_client\",");
    var svPm = SoteriaBridge.Evaluate(svRoot, svFiles(svPmContract, svRegistry));
    Check(svPm.State == SoteriaState.Ready && SoteriaBridge.IsPasswordManager(svPm), "soteria pm: the per-fill capability makes SoteriaVault the password manager");
    Check(!SoteriaBridge.IsPasswordManager(svLive) && !SoteriaBridge.IsPasswordManager(svOk), "soteria pm: without the capability (or switched off) Recognition keeps its own vault");
    Check(SoteriaBridge.Evaluate(svRoot, svFiles(svPmContract.Replace("\"secret_release_to_external_runtime\",", ""), svRegistry)).State == SoteriaState.Invalid, "soteria pm: a contract that stops forbidding bulk secret release is still refused");
    Check(SoteriaBridge.Evaluate(svRoot, svFiles(svPmContract.Replace("\"enabled\":true", "\"enabled\":false"), svRegistry)).State == SoteriaState.ContractOnly, "soteria pm: disabled again -> nothing is exchanged");
    var pmReq = new System.Text.Json.Nodes.JsonObject { ["op"] = "save", ["page_url"] = "https://example.com/", ["username"] = "a", ["password"] = "secret-1" };
    var pmReq2 = new System.Text.Json.Nodes.JsonObject { ["op"] = "save", ["page_url"] = "https://example.com/", ["username"] = "a", ["password"] = "secret-2" };
    Check(SoteriaPasswords.RedactedSha(pmReq) == SoteriaPasswords.RedactedSha(pmReq2) && (string?)pmReq["password"] == "secret-1", "soteria pm: receipt hashes never depend on (or confirm) a password, and the caller's request is untouched");
    Check(SoteriaPasswords.Outcome(new System.Text.Json.Nodes.JsonObject { ["ok"] = true }) == ("ok", null) &&
          SoteriaPasswords.Outcome(new System.Text.Json.Nodes.JsonObject { ["ok"] = false, ["error"] = "DENIED" }) == ("refused", "SOTERIA_DENIED") &&
          SoteriaPasswords.Outcome(new System.Text.Json.Nodes.JsonObject { ["ok"] = false, ["error"] = "NOT_RUNNING" }) == ("unavailable", "SOTERIA_APP_NOT_RUNNING") &&
          SoteriaPasswords.Outcome(new System.Text.Json.Nodes.JsonObject { ["ok"] = false, ["error"] = "LOCKED" }) == ("deferred", "SOTERIA_LOCKED") &&
          SoteriaPasswords.Outcome(new System.Text.Json.Nodes.JsonObject { ["ok"] = false, ["error"] = "weird token; rm" }).Error == "SOTERIA_WEIRDTOKENRM", "soteria pm: bridge answers map to explicit receipt outcomes with safe tokens");
    var pmImp = SoteriaPasswords.ImportRequest(new[] { new VaultEntry { Id = "abcdefgh", Origin = "example.com", Username = "u", Password = "p1", CreatedUtc = "c", UpdatedUtc = "u" } });
    Check((string?)pmImp["op"] == "import" && pmImp["entries"]!.AsArray().Count == 1 && !SoteriaPasswords.RedactedSha(pmImp).Contains("p1"), "soteria pm: moving the vault sends every login once");
    var pmApp = SoteriaPasswords.AppPath("C:\\sv", pth => pth.StartsWith("C:\\sv") && pth.EndsWith("SoteriaVault.exe"));
    Check(pmApp != null && pmApp.Replace('/', '\\') == "C:\\sv\\app\\SoteriaVault.exe" && SoteriaPasswords.AppPath("relative", _ => true) == null && SoteriaPasswords.AppPath("C:\\sv", _ => false) == null, "soteria pm: the app is started only from the linked folder, and only when it is built");
    Check(SoteriaPasswords.MenuLabel(new System.Text.Json.Nodes.JsonObject { ["username"] = "a\u0007b", ["title"] = "T" }) == "ab  -  T", "soteria pm: menu labels drop control characters");
}

// ---- addresses that arrive from other programs (default browser, second launch) -----------------------------------------------------
{
    Func<string, bool> laExists = pth => pth == "C:\\pages\\a.html" || pth == "C:\\pages\\b.txt" || pth == "C:\\pages\\c.HTM";
    Check(LaunchArgs.Parse("https://example.com/a?b=1#c") == "https://example.com/a?b=1#c", "launch: an https link is accepted unchanged");
    Check(LaunchArgs.Parse("  HTTP://Example.COM  ") == "http://example.com/", "launch: http link is trimmed and normalised");
    foreach (var laBad in new[] { "https://bank.com@evil.com/", "https://someone@example.com/", "javascript:alert(1)", "data:text/html,x", "ftp://example.com/x", "file:///C:/pages/a.html",
                                  "ms-msdt:/id", "search-ms:query=x", "mailto:a@b.c", "\\\\srv\\share\\a.html", "http://", "https:///x", "about:blank", "", "   ", "not a url", "https://a.example/\u0001x", "https://a.example/\u007fx" })
        Check(LaunchArgs.Parse(laBad, laExists) == null, "launch: refused " + (laBad.Length > 28 ? laBad.Substring(0, 28) : laBad).Replace("\u0001", "\\x01").Replace("\u007f", "\\x7f"));
    Check(LaunchArgs.Parse("https://a.example/" + new string('a', 5000)) == null, "launch: an oversized address is refused");
    Check(LaunchArgs.Parse(null) == null, "launch: null is refused");
    Check(LaunchArgs.Parse("C:\\pages\\a.html", laExists) == "file:///C:/pages/a.html", "launch: an existing local .html file opens as a file address");
    Check(LaunchArgs.Parse("C:\\pages\\c.HTM", laExists) != null, "launch: the extension check ignores case");
    Check(LaunchArgs.Parse("C:\\pages\\b.txt", laExists) == null && LaunchArgs.Parse("C:\\pages\\zzz.html", laExists) == null && LaunchArgs.Parse("C:\\pages\\a.html") == null, "launch: wrong extension, missing file, or no file check -> refused");
    Check(LaunchArgs.FromArgs(new[] { "--flag", "/x", "https://ok.example/" }) == "https://ok.example/", "launch: switches are skipped, the first good address wins");
    Check(LaunchArgs.FromArgs(new[] { "javascript:1", "https://a.example/", "https://b.example/" }) == "https://a.example/", "launch: a bad argument is skipped, not trusted");
    Check(LaunchArgs.FromArgs(new string[0]) == null && LaunchArgs.FromArgs(null) == null && LaunchArgs.FromArgs(new[] { "-x", "/y" }) == null, "launch: no address -> null");
    Check(LaunchArgs.FromArgs(Enumerable.Repeat("junk", 40).Concat(new[] { "https://late.example/" }).ToArray()) == null, "launch: only the first 16 arguments are looked at");
}

// ---- QR code encoder: matrices must match the reference implementation exactly (hash of the module grid) ---------------------------
{
    string QrHash(string txt, out int qv, out int qm, out int qn)
    {
        var g = QrCode.Encode(txt, out qv, out qm); qn = g.GetLength(0);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(QrCode.ToText(g)))).ToLowerInvariant();
    }
    var qrPacket = "recognition:packet:v1:bc87ef19a7e5b5fa44f6e918ff2ddab49cccf8997864d9ccd62c005a8c5df366";
    var qh1 = QrHash("hi", out var qv1, out var qm1, out var qn1);
    Check(qh1 == "05d7684a6c56e7dc2ecf2c1270f67ba4cc2f3f45ed6935ea9b3cf71ac3a5ef50" && qv1 == 1 && qn1 == 21 && qm1 == 2, "qr: version 1 symbol matches the reference");
    var qh2 = QrHash(qrPacket, out var qv2, out var qm2, out var qn2);
    Check(qh2 == "5571d30357fd3bf79869b8010bf6ef7ed33e8921e26f83fb364d9beaee40a0ca" && qv2 == 6 && qn2 == 41 && qm2 == 2, "qr: the packet-fingerprint symbol (version 6) matches the reference");
    var qh3 = QrHash(new string('A', 213), out var qv3, out var qm3, out var qn3);
    Check(qh3 == "e8b49c67b8bce64d5bdc3343e6ea0a1846d3fe057a8671da15d3af52044de366" && qv3 == 10 && qn3 == 57 && qm3 == 3, "qr: a full version 10 symbol (multiple blocks, version info) matches the reference");
    var qh4 = QrHash("Привет €", out var qv4, out var qm4, out var qn4);
    Check(qh4 == "a37814387e6855fbba63b99fa1e71583ad9002c56784e0c46c31ac83adb78fca" && qv4 == 2 && qn4 == 25, "qr: non-ASCII text is encoded as UTF-8 and matches the reference");
    bool qrLong = false; try { QrCode.Encode(new string('x', 214), out _, out _); } catch (ArgumentException) { qrLong = true; }
    Check(qrLong, "qr: more than 213 bytes is refused");
    var qg = QrCode.Encode("", out var qv5, out _);
    Check(qv5 == 1 && qg.GetLength(0) == 21, "qr: empty text still produces a valid version 1 symbol");
    var qpath = QrCode.ToSvgPath(QrCode.Encode("hi", out _, out _));
    Check(qpath.Length > 100 && qpath.StartsWith("M") && qpath.All(ch => char.IsDigit(ch) || ch is 'M' or 'h' or 'v' or 'z' or '-' or ' '), "qr: the SVG path holds only drawing commands and numbers");
}

// ---- exits (proxies) typed on the VPN page: validated before they can reach the engine's command line -----------------------------------
{
    Check(ProxyRules.TryBuild("socks5", "Proxy.Example.com", "1080", out var pxA, out _) && pxA == "socks5://proxy.example.com:1080", "proxy: a SOCKS5 exit is built, host lower-cased");
    Check(ProxyRules.TryBuild(" HTTP ", "10.0.0.5", " 8080 ", out var pxB, out _) && pxB == "http://10.0.0.5:8080", "proxy: an HTTP exit on an IPv4 address is built, spaces trimmed");
    foreach (var (pxT, pxH, pxP) in new[]
    {
        ("ftp", "a.example", "21"), ("", "a.example", "1080"), ("socks5", "", "1080"), ("socks5", "a b.example", "1080"), ("socks5", "a.example --no-sandbox", "1080"),
        ("socks5", "a.example\" --evil=1 \"", "1080"), ("socks5", "a.example/path", "1080"), ("socks5", "user@a.example", "1080"), ("socks5", "-a.example", "1080"),
        ("socks5", "a.example-", "1080"), ("socks5", "a..example", "1080"), ("socks5", "::1", "1080"), ("socks5", "[::1]", "1080"), ("socks5", new string('a', 254), "1080"),
        ("socks5", "a.example", "0"), ("socks5", "a.example", "65536"), ("socks5", "a.example", "-1"), ("socks5", "a.example", "10 80"), ("socks5", "a.example", "1e3"), ("socks5", "a.example", ""), ("socks5", "a.example", "99999999999"),
    })
        Check(!ProxyRules.TryBuild(pxT, pxH, pxP, out var pxBad, out var pxErr) && pxBad == "" && pxErr.Length > 0, "proxy: refused type='" + pxT + "' host='" + (pxH.Length > 20 ? pxH.Substring(0, 20) : pxH).Replace("\"", "'") + "' port='" + pxP + "'");
    Check(!ProxyRules.TryBuild(null, null, null, out _, out _), "proxy: nulls are refused");
    Check(ProxyRules.CleanName("My <b>VPN</b>; rm -rf") == "My bVPNb rm -rf" && ProxyRules.CleanName("   ") == "" && ProxyRules.CleanName(new string('x', 100)).Length == 40, "proxy: names keep letters, digits and a few marks only, 40 characters at most");
    Check(ProxyRules.Describe("socks5://127.0.0.1:9050") == "SOCKS5 127.0.0.1:9050" && ProxyRules.Describe("weird") == "weird" && ProxyRules.Describe(null) == "", "proxy: described in plain words");
    Check(ProxyRules.IsLocal("socks5://127.0.0.1:9050") && ProxyRules.IsLocal("http://localhost:8080") && !ProxyRules.IsLocal("socks5://127.0.0.1.evil.com:1") && !ProxyRules.IsLocal("socks5://10.0.0.1:1080") && !ProxyRules.IsLocal(null), "proxy: only 127.0.0.1 and localhost count as on this computer");
    var pxTaken = new HashSet<string> { "Work", "Work 2" };
    Check(ProxyRules.UniqueName("Home", n => pxTaken.Contains(n)) == "Home" && ProxyRules.UniqueName("Work", n => pxTaken.Contains(n)) == "Work 3", "proxy: duplicate names get a number");
}

// ---- stress: the filter engine at real-list scale, adversarial input, and PDF/extension abuse ------------------------------------
{
    var stSw = System.Diagnostics.Stopwatch.StartNew();
    var stSb = new System.Text.StringBuilder(); var stRnd = new Random(12345);
    for (int i = 0; i < 150000; i++)
    {
        switch (i % 4)
        {
            case 0: stSb.Append("||ads").Append(i).Append(".tracker").Append(i % 977).Append(".example^\n"); break;
            case 1: stSb.Append("/banner").Append(i).Append("/*$script,third-party\n"); break;
            case 2: stSb.Append("##.ad-").Append(i).Append("\n"); break;
            default: stSb.Append("@@||ok").Append(i).Append(".example^\n"); break;
        }
    }
    var stE = new FilterEngine(); stE.AddList(stSb.ToString(), "big");
    var stLoad = stSw.ElapsedMilliseconds;
    Check(stE.NetworkRules > 100000, "stress: a 150,000-line list loads (" + stE.NetworkRules + " network rules, " + stLoad + " ms)");
    Check(stLoad < 20000, "stress: loading a 150,000-line list takes under 20 s");
    stSw.Restart(); int stBlocked = 0;
    for (int i = 0; i < 200000; i++)
        if (stE.Match("https://ads" + (i % 150000) + ".tracker" + ((i % 150000) % 977) + ".example/p.js", "https://site.test/", ResType.Script).Blocked) stBlocked++;
    var stMatch = stSw.ElapsedMilliseconds;
    Check(stBlocked > 0, "stress: matching finds blocked requests at scale");
    Check(stMatch < 20000, "stress: 200,000 matches against a 150,000-rule list take under 20 s (" + stMatch + " ms)");
    Check(!stE.Match("https://ok3.example/x.js", "https://site.test/", ResType.Script).Blocked, "stress: an exception rule still works inside a huge list");

    // adversarial: very long urls, pathological patterns, binary junk
    var stAdv = new FilterEngine();
    stAdv.AddList("*a*a*a*a*a*a*a*a*a*b\n||x.example^*a*a*a*a*a*a*a*a*c\n" + new string('*', 5000) + "\n" + new string('a', 100000) + "\n", "adv");
    stSw.Restart();
    var stLong = "https://x.example/" + new string('a', 200000);
    stAdv.Match(stLong, "https://p.test/", ResType.Script);
    stAdv.Match("https://" + new string('a', 100000) + ".example/", "https://p.test/", ResType.Script);
    stAdv.Match("::::" + new string('/', 50000), "", ResType.Other);
    Check(stSw.ElapsedMilliseconds < 5000, "stress: pathological wildcard rules and 200 kB urls do not blow up (" + stSw.ElapsedMilliseconds + " ms)");
    bool stNoThrow = true;
    try { foreach (var junk in new[] { null, "", "\0\0", "http://", "://", "https://[::1", "https://a@b@c/", "data:text/html,x", "javascript:alert(1)" }) stAdv.Match(junk, junk, ResType.Other); }
    catch { stNoThrow = false; }
    Check(stNoThrow, "stress: malformed and hostile urls never throw");
    bool stRuleThrow = false;
    try { var stJ = new FilterEngine(); var jb = new byte[20000]; stRnd.NextBytes(jb); stJ.AddList(System.Text.Encoding.Latin1.GetString(jb), "junk"); }
    catch { stRuleThrow = true; }
    Check(!stRuleThrow, "stress: random binary bytes as a filter list never throw");
    var stCap = new FilterEngine(); stCap.AddList("##a{background:url(x)}\n##a:has(b)\n##a,b{x}\n##}\n##a;color:red\n", "c");
    Check(stCap.CosmeticCssFor("x.example").IndexOf("url(", StringComparison.Ordinal) < 0, "stress: cosmetic rules carrying url() or injected CSS are rejected");

    // PDF: page planner extremes. A plan is produced within the caps or refused with ImagePdfException, never any other failure.
    foreach (var (w, h) in new[] { (1280, 100), (1280, 59999), (1280, 60000), (1280, 1000000), (1, 1), (1, 60000), (8000, 8000), (0, 0), (-5, 10), (int.MaxValue, int.MaxValue) })
        foreach (var lay in new[] { "a4", "letter", "single", "bogus" })
        {
            string res;
            try
            {
                var pl = PagePlanner.Plan(w, h, lay, 4000);
                res = pl.Pages.Count <= PagePlanner.MaxPages && pl.Tiles.Count <= PagePlanner.MaxTiles && pl.Pages.All(q => q.WidthPt <= PagePlanner.MaxPdfPt && q.HeightPt <= PagePlanner.MaxPdfPt) ? "ok" : "over cap";
            }
            catch (ImagePdfException) { res = "ok"; }
            catch (Exception ex) { res = ex.GetType().Name; }
            Check(res == "ok", "stress: page planner " + w + "x" + h + " " + lay + " stays within caps or is refused cleanly (" + res + ")");
        }
}

// ---- VPN app detection, start page background, home page ----
{
    AdapterInfo Ad(string n, string d, string t = "Ethernet", bool up = true) => new AdapterInfo(n, d, t, up);
    Check(VpnDetect.Find(new[] { Ad("Ethernet", "Intel(R) Ethernet Connection"), Ad("Wi-Fi", "Intel(R) Wi-Fi 6 AX201") }) == null, "vpn detect: ordinary adapters are not a VPN");
    Check(VpnDetect.Find(new[] { Ad("Wi-Fi", "Intel Wi-Fi"), Ad("NordLynx", "NordLynx Tunnel", "Tunnel") }) is { Kind: "vpn" } vdA && vdA.Name.Contains("NordLynx"), "vpn detect: NordLynx is a VPN");
    Check(VpnDetect.Find(new[] { Ad("Local Area Connection 2", "TAP-Windows Adapter V9") }) is { Kind: "vpn" }, "vpn detect: OpenVPN's TAP adapter is a VPN");
    Check(VpnDetect.Find(new[] { Ad("wg0", "WireGuard Tunnel") }) is { Kind: "vpn" }, "vpn detect: WireGuard is a VPN");
    Check(VpnDetect.Find(new[] { Ad("Work", "WAN Miniport (IKEv2)", "Ppp") }) == null, "vpn detect: an unused WAN Miniport entry is ignored");
    Check(VpnDetect.Find(new[] { Ad("Office VPN", "Office VPN", "Ppp") }) is { Kind: "vpn" }, "vpn detect: a Windows built-in VPN connection (point-to-point) is a VPN");
    Check(VpnDetect.Find(new[] { Ad("NordLynx", "NordLynx Tunnel", "Tunnel", false) }) == null, "vpn detect: an adapter that is down does not count");
    Check(VpnDetect.Find(new[] { Ad("Teredo Tunneling Pseudo-Interface", "Teredo Tunneling Pseudo-Interface", "Tunnel") }) == null, "vpn detect: Teredo is not a VPN");
    Check(VpnDetect.Find(new[] { Ad("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter"), Ad("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter #2") }) == null, "vpn detect: Hyper-V and WSL adapters are not a VPN");
    Check(VpnDetect.Find(new[] { Ad("VMware Network Adapter VMnet8", "VMware Virtual Ethernet Adapter for VMnet8") }) == null, "vpn detect: VMware adapters are not a VPN");
    Check(VpnDetect.Find(new[] { Ad("Tailscale", "Tailscale Tunnel", "Tunnel") }) is { Kind: "mesh" }, "vpn detect: Tailscale is reported as a mesh network, not a VPN");
    Check(VpnDetect.Find(new[] { Ad("Tailscale", "Tailscale Tunnel", "Tunnel"), Ad("ProtonVPN", "ProtonVPN TUN") }) is { Kind: "vpn" }, "vpn detect: a real VPN wins over a mesh network");
    Check(VpnDetect.Find(null) == null && VpnDetect.Find(new AdapterInfo[0]) == null, "vpn detect: no adapters, no answer");
    Check(VpnDetect.Describe(null).Contains("No VPN") && VpnDetect.Describe(new VpnFound("X", "vpn")).Contains("looks active") && VpnDetect.Describe(new VpnFound("X", "mesh")).Contains("does not hide"), "vpn detect: plain-words descriptions");

    foreach (var sbP in StartBackground.Presets) Check(StartBackground.Normalize(sbP.ToUpperInvariant()) == sbP, "start bg: preset " + sbP + " is accepted");
    Check(StartBackground.Normalize("#A1b2C3") == "#a1b2c3" && StartBackground.Normalize("image") == "image", "start bg: colour and image keys are accepted");
    foreach (var sbBad in new[] { "", "red", "#fff", "#12345g", "#1234567", "url(x)", "default;}body{display:none", "</style><script>", null })
        Check(StartBackground.Normalize(sbBad) == "default", "start bg: refused '" + (sbBad ?? "null") + "'");
    Check(StartBackground.Css("default", null) == "", "start bg: default adds nothing");
    foreach (var sbP in new[] { "aurora", "dusk", "forest", "slate", "light", "#336699", "#f0f0f0" })
        Check(StartBackground.Css(sbP, null).StartsWith("body{background:") && !StartBackground.Css(sbP, null).Contains("url(") && !StartBackground.Css(sbP, null).Contains("<"), "start bg: " + sbP + " is plain CSS with no urls or tags");
    Check(StartBackground.Css("#f0f0f0", null).Contains("--txt") && !StartBackground.Css("#101010", null).Contains("--txt"), "start bg: a light colour switches the page text to dark, a dark one does not");
    var sbOk = "data:image/jpeg;base64,/9j/4AAQSkZJRg==";
    Check(StartBackground.Css("image", sbOk).Contains("url(" + sbOk + ")"), "start bg: a saved picture is used as a data: URL");
    foreach (var sbBadImg in new[] { null, "", "https://evil.example/x.jpg", "data:text/html;base64,AAAA", "data:image/jpeg;base64,AAA\")}body{display:none", "data:image/jpeg;base64,AAAA) , url(https://evil.example/x", "javascript:alert(1)" })
        Check(StartBackground.Css("image", sbBadImg) == "", "start bg: a picture that is not a plain base64 data URL is not used");
    Check(StartBackground.Css("image", "data:image/jpeg;base64," + new string('A', 3_100_000)) == "", "start bg: an oversized picture is not used");

    Check(StartBackground.HomeUrl("recognition:start") == "recognition:start" && StartBackground.HomeUrl(" START ") == "recognition:start", "home: the start page");
    Check(StartBackground.HomeUrl("https://example.com/a?b=1") == "https://example.com/a?b=1", "home: a full https address");
    Check(StartBackground.HomeUrl("example.com") == "https://example.com/", "home: a bare site name becomes https");
    Check(StartBackground.HomeUrl("  duckduckgo.com  ") == "https://duckduckgo.com/", "home: spaces around the site name are ignored");
    foreach (var hmBad in new[] { "", "   ", "javascript:alert(1)", "data:text/html,x", "file:///C:/x.html", "ftp://example.com", "https://a@evil.com/", "https://bank.com@evil.com/", "two words.com", "nodot", "mailto:a@b.c", "\\\\srv\\share\\a.html", null })
        Check(StartBackground.HomeUrl(hmBad) == null, "home: refused '" + (hmBad ?? "null") + "'");
    Check(StartBackground.HomeUrl(@"C:\pages\home.html", p => true) is { } hmF && hmF.StartsWith("file:"), "home: an existing local .html file is accepted");
    Check(StartBackground.HomeUrl(@"C:\pages\home.html", p => false) == null, "home: a missing local file is refused");
}

// ---- everyday browser features (BrowserFeel) ----
{
    Check(SearchEngines.Get(null).Id == "duckduckgo" && SearchEngines.Get("nope").Id == "duckduckgo" && SearchEngines.Get(" BRAVE ").Id == "brave", "search: unknown or empty falls back to DuckDuckGo, ids are case-insensitive");
    Check(SearchEngines.Url("duckduckgo", "a b&c") == "https://duckduckgo.com/?q=a%20b%26c", "search: the query is percent-encoded");
    Check(SearchEngines.Url("google", "cats") == "https://www.google.com/search?q=cats" && SearchEngines.Url("brave", "x") == "https://search.brave.com/search?q=x", "search: engine templates");
    Check(SearchEngines.Url("duckduckgo", "x\"><script>&q=") .IndexOfAny(new[] { '"', '<', '>', ' ' }) < 0, "search: quotes, brackets and spaces never reach the address");
    foreach (var seE in SearchEngines.All)
    {
        Check(seE.Template.StartsWith("https://") && seE.Template.IndexOf("{q}") == seE.Template.LastIndexOf("{q}") && seE.Template.Contains("{q}"), "search: " + seE.Id + " uses https and one query slot");
        Check(SearchEngines.Prefix(seE.Id) + "zz" == SearchEngines.Url(seE.Id, "zz"), "search: " + seE.Id + " prefix + query equals the full address");
        Check(SearchEngines.Home(seE.Id).StartsWith("https://") && SearchEngines.Home(seE.Id).EndsWith("/") && !SearchEngines.Home(seE.Id).Contains("?"), "search: " + seE.Id + " home address");
    }
    Check(SearchEngines.Normalize("GOOGLE") == "google" && SearchEngines.Normalize("x;y") == "duckduckgo", "search: normalize");

    var biHtml = "<!DOCTYPE NETSCAPE-Bookmark-file-1><DL><p>\n<DT><H3>Folder</H3><DL><p>\n" +
        "<DT><A HREF=\"https://example.com/a?x=1&amp;y=2\" ADD_DATE=\"1\">Example &amp; Co</A>\n" +
        "<DT><a href='http://plain.test/'>Single quotes</a>\n" +
        "<DT><A HREF=\"javascript:alert(1)\">bad js</A>\n<DT><A HREF=\"file:///C:/x.html\">bad file</A>\n" +
        "<DT><A HREF=\"https://example.com/a?x=1&amp;y=2\">duplicate</A>\n<DT><A HREF=\"https://user@evil.test/\">bad userinfo</A>\n" +
        "<DT><A HREF=\"https://tags.test/\"><b>Bold</b> title</A>\n</DL><p></DL>";
    var biList = BookmarkImport.Parse(biHtml);
    Check(biList.Count == 3, "import: three good bookmarks found (" + biList.Count + ")");
    Check(biList.Count == 3 && biList[0].Url == "https://example.com/a?x=1&y=2" && biList[0].Title == "Example & Co", "import: entities decoded in address and title");
    Check(biList.Count == 3 && biList[1].Url == "http://plain.test/" && biList[1].Title == "Single quotes", "import: single-quoted attributes and lower-case tags work");
    Check(biList.Count == 3 && biList[2].Url == "https://tags.test/" && biList[2].Title == "Bold title", "import: tags inside a title are dropped");
    Check(!biList.Any(b => b.Url.StartsWith("javascript") || b.Url.StartsWith("file") || b.Url.Contains("evil.test")), "import: javascript:, file: and user-name addresses are refused");
    Check(BookmarkImport.Parse(null).Count == 0 && BookmarkImport.Parse("").Count == 0 && BookmarkImport.Parse("no links here").Count == 0, "import: empty and link-free input give nothing");
    Check(BookmarkImport.Parse("<A HREF=\"https://t.test/\">a\u0001b</A>")[0].Title == "ab", "import: control characters are removed from titles");
    Check(BookmarkImport.Parse("<A HREF=\"https://t.test/\">" + new string('x', 300) + "</A>")[0].Title.Length == 200, "import: titles are cut to 200 characters");
    var biBig = new System.Text.StringBuilder(); for (int biI = 0; biI < 6000; biI++) biBig.Append("<A HREF=\"https://h" + biI + ".test/\">t</A>\n");
    Check(BookmarkImport.Parse(biBig.ToString()).Count == BookmarkImport.MaxItems, "import: at most 5000 bookmarks are taken");
    Check(BookmarkImport.Parse(new string('<', 100000) + "<a href=\"https://t.test/\">x</a>").Count <= 1, "import: hostile input does not hang or throw");

    var tsV = new (string, string)[]
    {
        ("https://www.site-a.test/x", "A page"), ("https://site-a.test/", "Site A Home"), ("https://site-a.test/y", "Y"), ("https://site-b.test/", "B"),
        ("http://site-c.test/", "C"), ("https://site-c.test/z", "C2"), ("ftp://x.test/", "F"), ("https://u@evil.test/", "E"), ("javascript:1", "J"), ("garbage", "G"), ("", "")
    };
    var tsA = TopSites.Pick(tsV, 8);
    Check(tsA.Count == 3 && tsA[0].Host == "site-a.test" && tsA[1].Host == "site-c.test" && tsA[2].Host == "site-b.test", "top sites: ordered by visits, www merged, other schemes and user-name addresses skipped");
    Check(tsA[0].Visits == 3 && tsA[0].Url == "https://site-a.test/" && tsA[0].Title == "Site A Home", "top sites: a tile opens the site's home and uses its home page title");
    Check(TopSites.Pick(tsV, 2).Count == 2 && TopSites.Pick(tsV, 2)[1].Host == "site-c.test", "top sites: the limit is respected");
    Check(TopSites.Pick(tsV, 8, new HashSet<string> { "site-a.test" }).Select(t => t.Host).SequenceEqual(new[] { "site-c.test", "site-b.test" }), "top sites: hidden sites are left out");
    var tsT = TopSites.Pick(new (string, string)[] { ("https://one.test/", ""), ("https://two.test/", "") }, 8);
    Check(tsT[0].Host == "two.test" && tsT[1].Host == "one.test" && tsT[0].Title == "two.test", "top sites: ties go to the most recent; no title falls back to the host");
    Check(TopSites.Pick(null, 8).Count == 0 && TopSites.Pick(tsV, 0).Count == 0 && TopSites.Pick(new (string, string)[0], 8).Count == 0, "top sites: nothing in, nothing out");
    Check(TopSites.Pick(new (string, string)[] { ("https://t.test/", new string('y', 200)) }, 1)[0].Title.Length == 60, "top sites: long titles are cut");

    Check(SiteBadge.Letter("www.example.com") == "E" && SiteBadge.Letter("123.test") == "1" && SiteBadge.Letter("") == "?" && SiteBadge.Letter("--") == "?" && SiteBadge.Letter(null) == "?", "badge: first letter or digit of the site");
    Check(System.Text.RegularExpressions.Regex.IsMatch(SiteBadge.CssColor("example.com"), "^#[0-9a-f]{6}$"), "badge: colour is a plain #rrggbb value");
    Check(SiteBadge.CssColor("Example.COM") == SiteBadge.CssColor("example.com"), "badge: the same site always gets the same colour");
    Check(SiteBadge.CssColor("a.test") != SiteBadge.CssColor("b.test") && SiteBadge.CssColor("example.com") != SiteBadge.CssColor("github.com"), "badge: different sites get different colours");
    Check(SiteBadge.CssColor(null) == SiteBadge.CssColor(""), "badge: no site still gives a colour");
    Check(Counts.Pretty(0) == "0" && Counts.Pretty(1234567) == "1,234,567" && Counts.Pretty(-5) == "0", "counts: thousands separators, never negative");
}

// ---- workspace: tab groups, session, tiles, translate, sync ----
{
    Check(TabGroups.CleanName("  Work   stuff <b>!  ") == "Work stuff b" && TabGroups.CleanName(null) == "" && TabGroups.CleanName(new string('a', 40)).Length == 24, "groups: names are cleaned and cut to 24 characters");
    Check(TabGroups.CleanName("a\u0001b\n") == "ab" && TabGroups.CleanName("<script>") == "script", "groups: control characters and markup characters are dropped");
    Check(TabGroups.ValidColor("#4C9BF0") == "#4c9bf0" && TabGroups.ValidColor("red") == TabGroups.Palette[0] && TabGroups.ValidColor("url(x)") == TabGroups.Palette[0], "groups: only palette colours are accepted");
    Check(TabGroups.NextColor(new string[0]) == TabGroups.Palette[0] && TabGroups.NextColor(new[] { TabGroups.Palette[0] }) == TabGroups.Palette[1] && TabGroups.NextColor(TabGroups.Palette) == TabGroups.Palette[0], "groups: the next unused colour is chosen, then it cycles");
    Check(TabGroups.NextId(new string[0]) == "g1" && TabGroups.NextId(new[] { "g1", "g2" }) == "g3" && TabGroups.NextId(new[] { "g2" }) == "g1", "groups: ids fill the first free number");
    var gaItems = new List<TabGroups.ArrangeItem>
    {
        new(0, null, false), new(1, "g1", false), new(2, null, false), new(3, "g2", false), new(4, "g1", false), new(5, null, true), new(6, "g2", false), new(7, "g9", true)
    };
    Check(string.Join(",", TabGroups.Arrange(gaItems)) == "5,7,0,1,4,2,3,6", "groups: pinned first, group members pulled together at the group's first tab, the rest keep their order");
    Check(TabGroups.Arrange(new List<TabGroups.ArrangeItem>()).Count == 0, "groups: no tabs, no order");
    var gaOnce = TabGroups.Arrange(gaItems); Check(gaOnce.Distinct().Count() == gaItems.Count && gaOnce.Count == gaItems.Count, "groups: every tab appears exactly once");
    var gaAgain = gaOnce.Select(k => gaItems.First(x => x.Key == k)).ToList();
    Check(TabGroups.Arrange(gaAgain).SequenceEqual(gaOnce), "groups: arranging an already arranged list changes nothing");

    var seData = new SessionData(
        new List<SessionTab> { new("https://a.test/", true, null), new("https://b.test/x?y=1", false, "g1"), new("http://c.test/", false, null), new("https://d.test/", false, "g1") },
        new List<SessionGroup> { new("g1", "Work", "#e5736b", true) }, 3);
    var seJson = SessionRules.Serialize(seData); var seBack = SessionRules.Parse(seJson);
    Check(seBack != null && seBack.Tabs.Count == 4 && seBack.Groups.Count == 1 && seBack.Active == 3 && seBack.Tabs[1].Group == "g1" && seBack.Tabs[0].Pinned && seBack.Groups[0].Collapsed && seBack.Groups[0].Name == "Work", "session: a session survives a round trip");
    Check(seBack != null && seBack.Tabs[1].Url == "https://b.test/x?y=1" && seBack.Tabs[2].Url == "http://c.test/", "session: addresses are kept exactly");
    foreach (var seBad in new[] { "", "   ", "not json", "[]", "{}", "{\"schema\":\"other\"}", "{\"schema\":\"recognition.session.v1\"}", "{\"schema\":\"recognition.session.v1\",\"tabs\":[]}", "{\"schema\":\"recognition.session.v1\",\"tabs\":[{\"url\":\"javascript:alert(1)\"}]}" })
        Check(SessionRules.Parse(seBad) == null, "session: refused '" + (seBad.Length > 30 ? seBad.Substring(0, 30) : seBad) + "'");
    Check(SessionRules.Parse(null) == null && SessionRules.Parse(new string(' ', 2_000_000)) == null, "session: null and oversized input are refused");
    var seMixed = SessionRules.Parse("{\"schema\":\"recognition.session.v1\",\"active\":99,\"groups\":[{\"id\":\"g1\",\"name\":\"<b>x</b>\",\"color\":\"url(x)\"},{\"id\":\"bad id\",\"name\":\"n\"},{\"id\":\"g1\",\"name\":\"dup\"},{\"id\":\"g2\",\"name\":\"unused\"}]," +
        "\"tabs\":[{\"url\":\"https://ok.test/\",\"group\":\"g1\"},{\"url\":\"https://u@evil.test/\"},{\"url\":\"file:///C:/x.html\"},{\"url\":\"https://p.test/\",\"pinned\":true,\"group\":\"g1\"},{\"url\":\"https://q.test/\",\"group\":\"g404\"},7,null]}");
    Check(seMixed != null && seMixed.Tabs.Count == 3 && seMixed.Active == 2, "session: bad tabs are dropped, an out-of-range active index is clamped");
    Check(seMixed != null && seMixed.Tabs[0].Group == "g1" && seMixed.Tabs[1].Group == null && seMixed.Tabs[1].Pinned && seMixed.Tabs[2].Group == null, "session: pinned tabs and tabs in unknown groups lose their group");
    Check(seMixed != null && seMixed.Groups.Count == 1 && seMixed.Groups[0].Id == "g1" && seMixed.Groups[0].Name == "bxb" && seMixed.Groups[0].Color == TabGroups.Palette[0], "session: group names are cleaned, bad colours replaced, duplicate, invalid and unused groups dropped");
    var seMany = "{\"schema\":\"recognition.session.v1\",\"tabs\":[" + string.Join(",", Enumerable.Range(0, 200).Select(i => "{\"url\":\"https://h" + i + ".test/\"}")) + "]}";
    Check(SessionRules.Parse(seMany)!.Tabs.Count == SessionRules.MaxTabs, "session: at most 60 tabs are restored");

    Check(TileRules.HostOf("https://www.Example.com/a") == "example.com" && TileRules.HostOf("ftp://x.test") == null && TileRules.HostOf("garbage") == null && TileRules.HostOf(null) == null, "tiles: host of an address");
    Check(TileRules.NormalizeNew("example.com") == "https://example.com/" && TileRules.NormalizeNew("recognition:start") == null && TileRules.NormalizeNew("javascript:1") == null && TileRules.NormalizeNew("") == null && TileRules.NormalizeNew("a b.com") == null, "tiles: what you type becomes a web address or is refused");
    var tlCustom = new List<(string Url, string Title)>(); var tlHidden = new HashSet<string>();
    Check(TileRules.AddCustom(tlCustom, tlHidden, "news.test", out var tlErr) && tlCustom.Count == 1 && tlCustom[0].Url == "https://news.test/" && tlErr == "", "tiles: a site is added");
    Check(TileRules.AddCustom(tlCustom, tlHidden, "https://www.news.test/other", out _) && tlCustom.Count == 1, "tiles: the same site is not added twice");
    Check(!TileRules.AddCustom(tlCustom, tlHidden, "nonsense words", out var tlErr2) && tlErr2.Length > 0, "tiles: nonsense is refused with a reason");
    for (int tlI = 0; tlI < 20; tlI++) TileRules.AddCustom(tlCustom, tlHidden, "s" + tlI + ".test", out _);
    Check(tlCustom.Count == TileRules.MaxCustom && !TileRules.AddCustom(tlCustom, tlHidden, "one-more.test", out var tlErr3) && tlErr3.Contains("12"), "tiles: at most 12 added sites");
    var tlTop = new List<TopSite> { new("https://a.test/", "a.test", "A", 9), new("https://b.test/", "b.test", "B", 5), new("https://news.test/", "news.test", "News again", 4) };
    var tlOne = new List<(string Url, string Title)> { ("https://mine.test/", "Mine") };
    var tlB = TileRules.Build(tlOne, tlTop, new HashSet<string> { "a.test" }, 8);
    Check(string.Join(",", tlB.Select(t => t.Host)) == "mine.test,b.test,news.test" && tlB[0].Custom && !tlB[1].Custom, "tiles: your sites first, then most visited, hidden sites left out");
    Check(string.Join(",", TileRules.Build(tlOne, tlTop, null, 2).Select(t => t.Host)) == "mine.test,a.test", "tiles: the limit is respected");
    Check(TileRules.Build(new List<(string Url, string Title)> { ("https://a.test/", "Mine") }, tlTop, null, 8).Count(t => t.Host == "a.test") == 1, "tiles: a site you added is not shown twice");
    TileRules.Remove(tlOne, tlHidden, "mine.test"); Check(tlOne.Count == 0 && !tlHidden.Contains("mine.test"), "tiles: removing a site you added forgets it");
    TileRules.Remove(tlOne, tlHidden, "b.test"); Check(tlHidden.Contains("b.test"), "tiles: removing a most-visited site hides it");
    TileRules.Remove(tlOne, tlHidden, "bad host;<"); Check(!tlHidden.Any(h => h.Contains("<")), "tiles: a bad host is ignored");
    Check(TileRules.AddCustom(tlOne, tlHidden, "b.test", out _) && !tlHidden.Contains("b.test"), "tiles: adding a hidden site shows it again");
    var tlJson = TileRules.ToJson(new List<(string Url, string Title)> { ("https://x.test/", "X") }, new HashSet<string> { "y.test", "z.test" });
    using (var tlDoc = JsonDocument.Parse(tlJson))
    {
        var tlC2 = new List<(string Url, string Title)>(); var tlH2 = new HashSet<string>();
        TileRules.Load(tlDoc.RootElement.GetProperty("custom"), tlDoc.RootElement.GetProperty("hidden"), tlC2, tlH2);
        Check(tlC2.Count == 1 && tlC2[0].Url == "https://x.test/" && tlC2[0].Title == "X" && tlH2.SetEquals(new[] { "y.test", "z.test" }), "tiles: saved tiles load back");
    }
    using (var tlBad = JsonDocument.Parse("{\"custom\":[{\"url\":\"javascript:1\"},{\"url\":\"https://ok.test/\",\"title\":\"\"},5,{\"url\":\"https://ok.test/again\"}],\"hidden\":[\"ok.test\",\"BAD HOST\",\"<x>\",3,\"fine.test\"]}"))
    {
        var tlC3 = new List<(string Url, string Title)>(); var tlH3 = new HashSet<string>();
        TileRules.Load(tlBad.RootElement.GetProperty("custom"), tlBad.RootElement.GetProperty("hidden"), tlC3, tlH3);
        Check(tlC3.Count == 1 && tlC3[0].Title == "ok.test" && tlH3.SetEquals(new[] { "ok.test", "fine.test" }), "tiles: hostile saved tiles are dropped one by one");
    }
    TileRules.Load(null, null, new List<(string Url, string Title)>(), new HashSet<string>());

    Check(TranslateRules.Url("google", "de", "https://example.com/a?x=1&y=2") == "https://translate.google.com/translate?sl=auto&tl=de&u=https%3A%2F%2Fexample.com%2Fa%3Fx%3D1%26y%3D2", "translate: Google address");
    Check(TranslateRules.Url("bing", "zh-CN", "https://example.com/") == "https://www.translatetheweb.com/?from=&to=zh-Hans&a=https%3A%2F%2Fexample.com%2F", "translate: Microsoft address, Chinese uses its own code");
    Check(TranslateRules.Url("nope", "xx", "https://example.com/") == "https://translate.google.com/translate?sl=auto&tl=en&u=https%3A%2F%2Fexample.com%2F", "translate: unknown service and language fall back to Google and English");
    foreach (var trBad in new[] { "", "recognition:start", "javascript:alert(1)", "file:///C:/x.html", "https://u@evil.test/", "ftp://x.test/" })
        Check(TranslateRules.Url("google", "en", trBad) == null, "translate: refused page '" + trBad + "'");
    Check(TranslateRules.Url("google", "en", null) == null, "translate: no page, no link");
    Check(TranslateRules.Languages.All(l => TranslateRules.NormalizeLang(l.Code) == l.Code) && TranslateRules.NormalizeLang("DE") == "de" && TranslateRules.NormalizeLang("../x") == "en", "translate: language codes come from a fixed list");
    Check(TranslateRules.ServiceName("bing") == "Microsoft Translator" && TranslateRules.ServiceHost("google") == "translate.google.com" && TranslateRules.LangName("fr") == "French", "translate: names for the confirmation text");
    Check(TranslateRules.Languages.Select(l => TranslateRules.Url("google", l.Code, "https://e.test/")).All(u => u != null && u.StartsWith("https://translate.google.com/translate?sl=auto&tl=")), "translate: every language builds an address");

    var syBm = new List<(string Url, string Title)> { ("https://a.test/", "A"), ("https://b.test/", "B") };
    var syTiles = new List<(string Url, string Title)> { ("https://t.test/", "T") };
    var syCats = SyncRules.Categories(syBm, syTiles, new HashSet<string> { "h.test" }, "https://home.test/", "brave", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), "Laptop 1");
    var syPayload = SetupPayload.Build("abcdef0123456789", DateTime.UtcNow, DateTime.UtcNow.AddDays(400), syCats);
    var syD = SyncRules.Parse(syPayload);
    Check(syD != null && syD.Bookmarks.Count == 2 && syD.Tiles.Count == 1 && syD.Hidden.SequenceEqual(new[] { "h.test" }) && syD.Home == "https://home.test/" && syD.Engine == "brave" && syD.Device == "Laptop 1" && syD.PrefsUtc.Year == 2026, "sync: a payload round-trips");
    var syCode = SetupCode.Generate(); var syBlob = SetupBlob.Seal(syPayload, syCode, 100_000);
    var syOpen = SetupBlob.Open(syBlob, syCode, DateTime.UtcNow);
    Check(syOpen.Ok && SyncRules.Parse(syOpen.PayloadJson)!.Bookmarks.Count == 2, "sync: the encrypted file opens with its code and not without it");
    Check(!SetupBlob.Open(syBlob, SetupCode.Generate(), DateTime.UtcNow).Ok, "sync: a wrong code does not open the file");
    Check(!syBlob.Contains("a.test") && !syBlob.Contains("brave"), "sync: the file does not contain readable bookmarks or settings");
    Check(SyncRules.Parse("{}") == null && SyncRules.Parse("not json") == null && SyncRules.Parse(null) == null && SyncRules.Parse("{\"categories\":{\"bookmarks\":[]}}") == null, "sync: a payload without the sync part is refused");
    var syHostile = SyncRules.Parse("{\"categories\":{\"bookmarks\":[{\"url\":\"javascript:alert(1)\",\"title\":\"x\"},{\"url\":\"https://ok.test/\",\"title\":\"<b>ok</b>\\u0001\"},{\"url\":\"https://ok.test/\",\"title\":\"dup\"},{\"url\":\"https://u@evil.test/\"},7],\"sync\":{\"tiles\":[{\"url\":\"file:///x\"}],\"hidden\":[\"<x>\"],\"home_url\":\"javascript:1\",\"search_engine\":\"evil;engine\",\"prefs_utc\":\"yesterday\",\"device\":\"<script>alert(1)</script>\"}}}");
    Check(syHostile != null && syHostile.Bookmarks.Count == 1 && syHostile.Tiles.Count == 0 && syHostile.Hidden.Count == 0 && syHostile.Home == null && syHostile.Engine == "duckduckgo" && syHostile.PrefsUtc == DateTime.MinValue && !syHostile.Device.Contains("<"), "sync: hostile contents are dropped or neutralised");
    var syMerged = SyncRules.MergeByUrl(syBm, new List<(string Url, string Title)> { ("https://b.test/", "B2"), ("https://c.test/", "C") }, out var syAdded);
    Check(syMerged.Count == 3 && syAdded == 1 && syMerged[1].Title == "B" && syMerged[2].Url == "https://c.test/", "sync: merging adds what is missing and keeps what you have");
    Check(SyncRules.MergeByUrl(syBm, syBm, out var syNone).Count == 2 && syNone == 0, "sync: merging the same data changes nothing");
    Check(SyncRules.RemoteIsNewer(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow) && !SyncRules.RemoteIsNewer(DateTime.UtcNow, DateTime.UtcNow.AddHours(-1)) && !SyncRules.RemoteIsNewer(DateTime.MinValue, DateTime.MinValue), "sync: the later settings change wins");
}

{
    // ---- bookmark folders ----
    Check(BookmarkFolders.Clean("  Work   stuff ") == "Work stuff" && BookmarkFolders.Clean("A<b>\"c'&d") == "Abcd" && BookmarkFolders.Clean("x\u0001y") == "xy" && BookmarkFolders.Clean(null) == "" && BookmarkFolders.Clean(new string('a', 80)).Length == 30, "folders: names are cleaned and cut to 30 characters");
    var bfNames = BookmarkFolders.Names(new string?[] { "work", "Work", "", null, "Banks", "  news " });
    Check(string.Join("|", bfNames) == "Banks|news|work", "folders: folders in use, each once, alphabetical, case ignored");
    Check(BookmarkFolders.Resolve(new string?[] { "Work" }, "work") == "Work" && BookmarkFolders.Resolve(new string?[] { "Work" }, "Play") == "Play" && BookmarkFolders.Resolve(null, "  ") == null, "folders: an existing spelling is reused, a new name is cleaned, an empty name is refused");
    var bfMany = Enumerable.Range(0, 50).Select(i => "f" + i).Cast<string?>().ToList();
    Check(BookmarkFolders.Resolve(bfMany, "extra") == null && BookmarkFolders.Resolve(bfMany, "F7") == "f7", "folders: at most 50 folders, but an existing one can always be used");
}

{
    // ---- themes and extension sources ----
    Check(ThemePacks.Presets.Select(x => x.Id).Distinct().Count() == ThemePacks.Presets.Length && ThemePacks.Presets.All(x => Rgb.Valid(x.Bar) && Rgb.Valid(x.Text) && Rgb.Valid(x.Accent)), "themes: built-in looks have unique ids and valid colours");
    Check(ThemePacks.Presets.All(x => x.StartBg == StartBackground.Normalize(x.StartBg)) && ThemePacks.Find("AURORA")?.Name == "Aurora" && ThemePacks.Find("nope") == null, "themes: each look names a start background that exists");
    foreach (var (thName, thBar) in new[] { ("dark", "#1b1e24"), ("light", "#f1f3f6") })
    {
        var thD = ChromeColors.Derive(thBar, thBar == "#1b1e24" ? "#d6dae2" : "#1f2430", "#2b6cb0");
        Check(ChromeColors.Keys.All(k => thD.ContainsKey(k) && Rgb.Valid(thD[k])), "themes: every window colour is derived and valid (" + thName + " bar)");
        Check(thName == "dark" ? Rgb.Luminance(thD["Win"]) < Rgb.Luminance(thD["Bar"]) && Rgb.Luminance(thD["Hover"]) > Rgb.Luminance(thD["Bar"]) : Rgb.Luminance(thD["Field"]) > Rgb.Luminance(thD["Bar"]) && Rgb.Luminance(thD["Hover"]) < Rgb.Luminance(thD["Bar"]), "themes: dark bars get lighter hovers, light bars get darker ones (" + thName + ")");
    }
    Check(ChromeColors.Derive("red", "x", "y")["Bar"] == "#1b1e24" && ChromeColors.Derive("#1b1e24", "#d6dae2", "#2b6cb0")["Bar"] == "#1b1e24", "themes: invalid colours fall back to the default look");
    Check(ThemePacks.Custom("#102030", "#eeeeee", "#33aa66", "default")?.Dark == true && ThemePacks.Custom("#fff", "#eeeeee", "#33aa66", "default") == null && ThemePacks.Custom("#f0f0f0", "#111111", "#33aa66", "light")?.Dark == false, "themes: your own colours make a look, bad colours are refused");
    var thJson = "{\"name\":\"Ocean\\u0001\",\"theme\":{\"colors\":{\"toolbar\":[10,30,60],\"tab_text\":[255,255,255],\"ntp_background\":[5,15,30],\"button_background\":[0,200,150,1]},\"images\":{\"theme_ntp_background\":\"img\\\\bg.png\"}}}";
    var thP = ThemePacks.FromChromeTheme(thJson);
    Check(thP != null && thP.Bar == "#0a1e3c" && thP.Dark && thP.Text == "#ffffff" && thP.StartBg == "#050f1e" && thP.Accent == "#00c896" && thP.NtpImage == "img/bg.png" && thP.Name == "Ocean", "themes: a Chrome theme gives its colours, name and picture");
    Check(ThemePacks.FromChromeTheme("{\"name\":\"x\",\"manifest_version\":3}") == null && ThemePacks.FromChromeTheme("not json") == null && ThemePacks.FromChromeTheme(null) == null && ThemePacks.FromChromeTheme("{\"theme\":{}}") == null, "themes: an extension that is not a theme, or broken input, gives nothing");
    var thBad = ThemePacks.FromChromeTheme("{\"theme\":{\"colors\":{\"toolbar\":[300,0,0],\"frame\":[1.5,2,3],\"tab_text\":[1,2]},\"images\":{\"theme_ntp_background\":\"../x.png\"}}}");
    Check(thBad == null, "themes: out-of-range colours and a picture outside the folder are ignored");
    var thLow = ThemePacks.FromChromeTheme("{\"theme\":{\"colors\":{\"toolbar\":[20,20,20],\"toolbar_text\":[30,30,30]}}}");
    Check(thLow != null && thLow.Text == "#e6e8ee", "themes: text that cannot be read on the bar is replaced");
    Check(new[] { "a.png", "dir/a.JPG", "d\\a.gif" }.All(x => ThemePacks.SafeImagePath(x) != null) && new[] { "../a.png", "/a.png", "C:/a.png", "a.exe", "a", "", "a//b.png", "./a.png" }.All(x => ThemePacks.SafeImagePath(x) == null), "themes: picture paths stay inside the theme folder and must be pictures");
    var esId = new string('a', 16) + new string('p', 16);
    Check(ExtSources.Parse(esId)?.Store == "chrome" && ExtSources.Parse("  " + esId + " ")?.Id == esId, "store links: a bare id is a Chrome Web Store id");
    Check(ExtSources.Parse("https://chromewebstore.google.com/detail/ublock-origin-lite/" + esId + "?hl=en")?.Id == esId && ExtSources.Parse("https://chrome.google.com/webstore/detail/x/" + esId)?.Store == "chrome", "store links: Chrome Web Store links are read");
    Check(ExtSources.Parse("https://microsoftedge.microsoft.com/addons/detail/some-name/" + esId)?.Store == "edge", "store links: Edge Add-ons links are read");
    Check(new[] { "http://chromewebstore.google.com/detail/x/" + esId, "https://evil.test/detail/x/" + esId, "https://chromewebstore.google.com/detail/x/" + esId.Replace('p', 'z'), "https://u@chromewebstore.google.com/detail/x/" + esId, "https://chromewebstore.google.com/search/" + esId, "ABCD", "", "javascript:alert(1)" }.All(x => ExtSources.Parse(x) == null), "store links: other hosts, plain http, user names, wrong ids and other pages are refused");
    var esUrl = ExtSources.Url(new ExtSources.Source("chrome", esId), "154.0.1<x>");
    Check(esUrl.StartsWith("https://clients2.google.com/service/update2/crx?") && esUrl.Contains("prodversion=154") && esUrl.Contains(esId) && !esUrl.Contains("<") && ExtSources.Url(new ExtSources.Source("edge", esId), "").StartsWith("https://edge.microsoft.com/") && ExtSources.Url(new ExtSources.Source("edge", esId), "").Contains("prodversion=120"), "store links: the download address is the store's own, https, with the engine version");
}

{
    // ---- address bar completion ----
    var acV = new[] { "https://github.com/a", "https://www.gitlab.com/x", "https://github.com/b", "https://example.org/", "http://user@evil.test/", "https://gitea.example/" };
    Check(AddressComplete.Complete("git", acV, null) == "github.com", "autofill: the site you visit most wins");
    Check(AddressComplete.Complete("GIT", acV, null) == "GITHUB.COM".Substring(0, 3) + "hub.com", "autofill: what you typed keeps its capitals");
    Check(AddressComplete.Complete("www.gitl", acV, null) == "www.gitlab.com", "autofill: www. is ignored when matching and kept when typed");
    Check(AddressComplete.Complete("gitl", null, new[] { "https://gitlab.com/" }) == "gitlab.com", "autofill: bookmarks are used too");
    Check(AddressComplete.Complete("wikip", null, null) == "wikipedia.org" && AddressComplete.Complete("yout", acV, null) == "youtube.com", "autofill: very popular sites complete even if never visited");
    Check(AddressComplete.Complete("github.com", acV, null) == null && AddressComplete.Complete("zzzzqq", acV, null) == null, "autofill: nothing is added when it already matches or nothing fits");
    Check(new[] { "", "git hub", "https://git", "git/", "a@b", "git?", "git#", "git:" }.All(x => AddressComplete.Complete(x, acV, null) == null), "autofill: only a single word being typed is completed");
    Check(AddressComplete.Complete("evil", acV, null) == null, "autofill: addresses with user names are never offered");
    Check(AddressComplete.Popular.Distinct().Count() == AddressComplete.Popular.Length && AddressComplete.Popular.All(d => d == d.ToLowerInvariant() && d.Contains('.') && !d.Contains(' ')), "autofill: the built-in list has no repeats and only plain lower-case domains");
    var acTie = new[] { "https://one.test/", "https://two.test/" };
    Check(AddressComplete.Complete("t", acTie, null) == "two.test" && AddressComplete.Complete("o", acTie, null) == "one.test", "autofill: equal visits go to the newest");
}

{
    // ---- window placement ----
    var wpA = new[] { (0.0, 0.0, 1920.0, 1040.0) };
    var wp = new WindowPlacement(100, 80, 1000, 700, false);
    Check(WindowPlacement.Parse(wp.ToJson()) == wp, "window: size and place survive a round trip");
    Check(WindowPlacement.Parse(new WindowPlacement(1, 2, 3, 4, true).ToJson())?.Maximized == true, "window: maximized is remembered");
    Check(new[] { null, "", "x", "[]", "{}", "{\"left\":1}", "{\"left\":\"a\",\"top\":0,\"width\":1,\"height\":1}", "{\"left\":1e9,\"top\":0,\"width\":1,\"height\":1}" }.All(x => WindowPlacement.Parse(x) == null), "window: bad or missing saved data is ignored");
    Check(wp.Fit(wpA) == wp, "window: a good place is kept as it is");
    var wpSmall = new WindowPlacement(10, 10, 100, 100, false).Fit(wpA);
    Check(wpSmall.Width == WindowPlacement.MinWidth && wpSmall.Height == WindowPlacement.MinHeight, "window: never smaller than the minimum");
    var wpBig = new WindowPlacement(0, 0, 9000, 9000, false).Fit(wpA);
    Check(wpBig.Width == 1920 && wpBig.Height == 1040, "window: never bigger than the screens");
    var wpGone = new WindowPlacement(3000, 200, 1000, 700, false).Fit(wpA);
    Check(wpGone.Left == 460 && wpGone.Top == 170, "window: a window left on an unplugged screen is centred on the main one");
    var wpTwo = new[] { (0.0, 0.0, 1920.0, 1040.0), (-1920.0, 0.0, 1920.0, 1040.0) };
    Check(new WindowPlacement(-1500, 100, 1000, 700, false).Fit(wpTwo).Left == -1500, "window: a place on a second screen is kept while that screen exists");
    Check(new WindowPlacement(100, -500, 1000, 700, false).Fit(wpA).Top == 170, "window: a title bar above the screen is brought back");
    Check(new WindowPlacement(100, 80, 1000, 700, true).Fit(wpA).Maximized, "window: maximized survives fitting");
}

{
    // ---- cards and the unlock PIN ----
    Check(CardRules.Luhn("4242424242424242") && CardRules.Luhn("378282246310005") && CardRules.Luhn("5555555555554444") && !CardRules.Luhn("4242424242424241") && !CardRules.Luhn("") && !CardRules.Luhn("42a2"), "cards: the check digit test accepts real test numbers and refuses typing mistakes");
    Check(CardRules.Digits("4242 4242-4242 4242") == "4242424242424242" && CardRules.Digits("1234") == null && CardRules.Digits("4242 4242 4242 424x") == null && CardRules.Digits(new string('4', 20)) == null && CardRules.Digits(null) == null, "cards: numbers are cleaned to digits and length checked");
    Check(CardRules.Brand("4242424242424242") == "Visa" && CardRules.Brand("5555555555554444") == "Mastercard" && CardRules.Brand("2223003122003222") == "Mastercard" && CardRules.Brand("378282246310005") == "American Express" && CardRules.Brand("6011111111111117") == "Discover" && CardRules.Brand("9999999999999995") == "Card" && CardRules.Brand("x") == "Card", "cards: brands are recognised from the first digits");
    Check(CardRules.Mask("4242424242424242") == "\u2022\u2022\u2022\u2022 4242" && !CardRules.Mask("4242424242424242").Contains("4242 4242") && CardRules.Mask("12") == "\u2022\u2022\u2022\u2022", "cards: only the last four digits are shown");
    var cvNow = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);
    Check(CardRules.ExpiryOk(10, 2026, cvNow) && CardRules.ExpiryOk(12, 28, cvNow) && !CardRules.ExpiryOk(9, 2026, cvNow) && !CardRules.ExpiryOk(13, 2030, cvNow) && !CardRules.ExpiryOk(0, 2030, cvNow) && !CardRules.ExpiryOk(5, 1999, cvNow) && CardRules.Expiry(3, 2029) == "03/29", "cards: a card is valid through the end of its expiry month");
    var cvMem = new Dictionary<string, string>();
    var cv = new CardVault("c.enc", p => cvMem.TryGetValue(p, out var v) ? v : "", (p, v) => cvMem[p] = v);
    Check(cv.Load() == 0 && cv.Entries.Count == 0, "cards: an empty store loads as empty");
    Check(cv.Add("Main", "A Person", "4242 4242 4242 4242", 12, 2030, cvNow, out var cvA) == null && cvA != null && cvA.Number == "4242424242424242" && cvA.Brand == "Visa" && cvA.Last4 == "4242", "cards: a good card is saved with a cleaned number");
    Check(cv.Add("", "A Person", "4242424242424242", 12, 2030, cvNow, out _) != null, "cards: the same card cannot be saved twice");
    Check(cv.Add("", "A Person", "4242424242424241", 12, 2030, cvNow, out _) != null && cv.Add("", "A Person", "4000056655665556", 1, 2020, cvNow, out _) != null && cv.Add("", "", "4000056655665556", 12, 2030, cvNow, out _) != null && cv.Add("x\u0001", "A Person", "4000056655665556", 12, 2030, cvNow, out _) != null, "cards: a bad number, an expired card, a missing name and control characters are refused");
    Check(!cvMem["c.enc"].Contains("cvc", StringComparison.OrdinalIgnoreCase) && !cvMem["c.enc"].Contains("cvv", StringComparison.OrdinalIgnoreCase) && !cvMem["c.enc"].Contains("security", StringComparison.OrdinalIgnoreCase), "cards: no security code field is ever stored");
    var cv2 = new CardVault("c.enc", p => cvMem.TryGetValue(p, out var v) ? v : "", (p, v) => cvMem[p] = v);
    Check(cv2.Load() == 0 && cv2.Entries.Count == 1 && cv2.Entries[0].Number == "4242424242424242" && cv2.Entries[0].Holder == "A Person" && cv2.Entries[0].Year == 2030, "cards: saved cards come back after a reload");
    cvMem["bad.enc"] = "{\"cards\":[{\"id\":\"short\",\"number\":\"4242424242424242\",\"month\":1,\"year\":2030},{\"id\":\"0123456789abcdef\",\"label\":\"ok\",\"holder\":\"H\",\"number\":\"4242424242424242\",\"month\":13,\"year\":2030},{\"id\":\"fedcba9876543210\",\"holder\":\"H\",\"number\":\"5555555555554444\",\"month\":2,\"year\":2031}]}";
    var cv3 = new CardVault("bad.enc", p => cvMem.TryGetValue(p, out var v) ? v : "", (p, v) => cvMem[p] = v);
    Check(cv3.Load() == 2 && cv3.Entries.Count == 1 && cv3.Entries[0].Last4 == "4444", "cards: damaged records are skipped and the good one kept");
    cvMem["junk.enc"] = "not json";
    Check(new CardVault("junk.enc", p => cvMem[p], (p, v) => cvMem[p] = v).Load() == 1, "cards: an unreadable store loads empty and reports the damage");
    Check(cv.Remove(cvA!.Id) && !cv.Remove(cvA.Id) && cv.Entries.Count == 0, "cards: a card can be removed");
    var cvJs = CardFillScript.Build("Eve \"</script>\\", "4242424242424242", 7, 28);
    Check(!cvJs.Contains('\n') && cvJs.Contains("\"4242424242424242\"") && cvJs.Contains("\"2028\"") && cvJs.Contains("\"07\"") && cvJs.Contains("\"28\"") && cvJs.Contains("\\u003C/script\\u003E") || cvJs.Contains("\\u003C/script"), "cards: the fill script is one line and carries the values as escaped literals");
    Check(cvJs.Contains("cc-number") && cvJs.Contains("cc-exp-month") && cvJs.Contains("cvc|cvv|csc|security") && !cvJs.Contains("cc-csc"), "cards: the fill script targets card fields and avoids security-code fields");

    // PIN gate
    var pgT = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
    Check(new[] { null, "", "123", "1234567890123", "12a456", "1111", "00000" }.All(x => PinGate.ValidatePin(x) != null) && PinGate.ValidatePin("2468") == null && PinGate.ValidatePin("123456789012") == null, "pin: only 4 to 12 digits, not one digit repeated");
    var pg = new PinGate();
    Check(!pg.IsSet && pg.TryUnlock("2468", pgT) == UnlockResult.NotSet && !pg.IsUnlocked(pgT), "pin: with no PIN set nothing is unlocked");
    Check(pg.SetPin("12") != null && !pg.IsSet && pg.SetPin("2468") == null && pg.IsSet && pg.SetPin("1357") != null, "pin: a bad PIN is refused, a good one is set once");
    var pgJson = pg.ToJson();
    Check(!pgJson.Contains("2468") && pgJson.Contains("recognition.pin.v1"), "pin: only a salted hash is kept, never the PIN");
    Check(pg.TryUnlock("0000", pgT) == UnlockResult.Wrong && !pg.IsUnlocked(pgT) && pg.TryUnlock("2468", pgT) == UnlockResult.Ok && pg.IsUnlocked(pgT) && pg.Failures == 0, "pin: a wrong PIN stays locked, the right one unlocks and clears the count");
    Check(pg.IsUnlocked(pgT.AddMinutes(4)) && !pg.IsUnlocked(pgT.AddMinutes(6)), "pin: it relocks after five idle minutes");
    pg.Touch(pgT.AddMinutes(4));
    Check(pg.IsUnlocked(pgT.AddMinutes(8)) && !pg.IsUnlocked(pgT.AddMinutes(10)), "pin: using the vault keeps it unlocked for another five minutes");
    pg.Lock();
    Check(!pg.IsUnlocked(pgT.AddSeconds(1)), "pin: Lock now locks at once");
    var pgL = PinGate.Parse(pgJson)!;
    Check(pgL.IsSet && pgL.TryUnlock("2468", pgT) == UnlockResult.Ok && PinGate.Parse(pgJson)!.TryUnlock("9999", pgT) == UnlockResult.Wrong, "pin: the PIN survives saving and loading");
    var pgF = new PinGate(); pgF.SetPin("2468");
    var r5 = UnlockResult.Wrong; for (int i = 0; i < 5; i++) r5 = pgF.TryUnlock("0001", pgT);
    Check(r5 == UnlockResult.LockedOut && pgF.RetryAfterSeconds(pgT) == 30 && pgF.TryUnlock("2468", pgT.AddSeconds(10)) == UnlockResult.LockedOut, "pin: five wrong tries lock it for 30 seconds, even for the right PIN");
    Check(pgF.TryUnlock("0001", pgT.AddSeconds(31)) == UnlockResult.LockedOut && pgF.RetryAfterSeconds(pgT.AddSeconds(31)) == 60, "pin: the next wrong try doubles the wait");
    Check(pgF.TryUnlock("2468", pgT.AddSeconds(31 + 61)) == UnlockResult.Ok, "pin: after the wait the right PIN works again");
    for (int i = 0; i < 40; i++) pgF.TryUnlock("0002", pgT.AddDays(1 + i));
    Check(pgF.RetryAfterSeconds(pgT.AddDays(41)) <= 0 || pgF.RetryAfterSeconds(pgT.AddDays(41)) <= 900, "pin: the wait never grows past 15 minutes");
    var pgC = new PinGate(); pgC.SetPin("2468");
    Check(pgC.Change("0000", "1357", pgT, out _) == UnlockResult.Wrong && pgC.Change("2468", "12", pgT, out var pgErr) != UnlockResult.Ok && pgErr != null && pgC.Change("2468", "1357", pgT, out _) == UnlockResult.Ok && pgC.TryUnlock("2468", pgT) == UnlockResult.Wrong && pgC.TryUnlock("1357", pgT) == UnlockResult.Ok, "pin: changing needs the old PIN and a valid new one");
    Check(pgC.Remove("0000", pgT) == UnlockResult.Wrong && pgC.IsSet && pgC.Remove("1357", pgT) == UnlockResult.Ok && !pgC.IsSet, "pin: removing needs the PIN");
    Check(PinGate.Parse("not json") == null && PinGate.Parse("{}") == null && PinGate.Parse("{\"schema\":\"recognition.pin.v1\",\"salt\":\"AAAA\",\"hash\":\"AAAA\"}") == null && PinGate.Parse("")!.IsSet == false, "pin: a damaged PIN record is refused (the caller must fail closed), an empty one means no PIN");
}

{
    // ---- Firefox add-on links ----
    Check(ExtSources.ParseFirefox("https://addons.mozilla.org/en-US/firefox/addon/ublock-origin/") == "ublock-origin" && ExtSources.ParseFirefox("https://addons.mozilla.org/firefox/addon/dark-reader/?src=search") == "dark-reader", "firefox links: add-on pages are read");
    Check(new[] { null, "", "ublock-origin", "http://addons.mozilla.org/en-US/firefox/addon/x/", "https://evil.test/en-US/firefox/addon/x/", "https://u@addons.mozilla.org/en-US/firefox/addon/x/", "https://addons.mozilla.org/en-US/firefox/", "https://addons.mozilla.org/en-US/android/addon/x/", "https://addons.mozilla.org/en-US/firefox/addon/../x/", "https://addons.mozilla.org/en-US/firefox/addon/a%20b/" }.All(x => ExtSources.ParseFirefox(x) == null), "firefox links: other hosts, plain http, user names and odd names are refused");
    Check(ExtSources.FirefoxApiUrl("dark-reader") == "https://addons.mozilla.org/api/v5/addons/addon/dark-reader/", "firefox links: the lookup address is Mozilla's own https API");
    Check(ExtSources.FirefoxFileUrl("{\"current_version\":{\"file\":{\"url\":\"https://addons.mozilla.org/firefox/downloads/file/123/x.xpi\"}}}") == "https://addons.mozilla.org/firefox/downloads/file/123/x.xpi" && ExtSources.FirefoxFileUrl("{\"current_version\":{\"file\":{\"url\":\"https://addons.cdn.mozilla.net/user-media/addons/1/x.xpi\"}}}") != null, "firefox links: the file address is taken from Mozilla's answer");
    Check(new[] { null, "", "x", "{}", "{\"current_version\":{}}", "{\"current_version\":{\"file\":{\"url\":\"http://addons.mozilla.org/x.xpi\"}}}", "{\"current_version\":{\"file\":{\"url\":\"https://evil.test/x.xpi\"}}}", "{\"current_version\":{\"file\":{\"url\":\"https://addons.mozilla.org.evil.test/x.xpi\"}}}", "{\"current_version\":{\"file\":{\"url\":5}}}" }.All(x => ExtSources.FirefoxFileUrl(x) == null), "firefox links: a file address on any other host, or a broken answer, is refused");
}

Console.WriteLine();
Console.WriteLine("=== Start page animation (StartMotion, theme v2, ThemeForge runtime) ===");
{
    // ---- recipes ----
    var mGood = StartMotion.Parse("{\"effect\":\"aurora\",\"duration_ms\":30000,\"intensity_pct\":60,\"seed\":42}");
    Check(mGood != null && mGood.Effect == "aurora" && mGood.DurationMs == 30000 && mGood.IntensityPct == 60 && mGood.Seed == 42 && mGood.IsOn, "motion: a good recipe is read");
    Check(StartMotion.Effects.Length == 12 && StartMotion.Effects.Select(e => e.Id).SequenceEqual(new[] { "none", "kenburns", "pan", "drift", "breathe", "parallax", "particles", "aurora", "rain", "snow", "shimmer", "fireflies" }), "motion: exactly the 12 runtime effects (Still plus 11)");
    Check(StartMotion.Effects.All(e => StartMotion.Parse("{\"effect\":\"" + e.Id + "\"}") is MotionRecipe r && r.Effect == e.Id), "motion: every listed effect is accepted");
    Check(StartMotion.Parse("{\"effect\":\"snow\"}") == new MotionRecipe("snow", 24000, 50, 1), "motion: missing numbers take the defaults (24 s, 50%, seed 1)");
    Check(StartMotion.Parse("{\"effect\":\"kenburns\",\"duration_ms\":4000,\"intensity_pct\":0,\"seed\":0}") != null && StartMotion.Parse("{\"effect\":\"kenburns\",\"duration_ms\":120000,\"intensity_pct\":100,\"seed\":2147483647}") != null, "motion: the range ends are accepted");
    var badRecipes = new[]
    {
        "{\"effect\":\"explode\"}", "{\"effect\":\"Aurora\"}", "{\"effect\":\"aurora \"}", "{\"effect\":5}", "{\"duration_ms\":30000}", "{}", "[]", "\"aurora\"", "null", "not json", "",
        "{\"effect\":\"aurora\",\"duration_ms\":3999}", "{\"effect\":\"aurora\",\"duration_ms\":120001}", "{\"effect\":\"aurora\",\"duration_ms\":30000.5}", "{\"effect\":\"aurora\",\"duration_ms\":3e4}",
        "{\"effect\":\"aurora\",\"duration_ms\":30000.0}", "{\"effect\":\"aurora\",\"duration_ms\":\"30000\"}", "{\"effect\":\"aurora\",\"intensity_pct\":-1}", "{\"effect\":\"aurora\",\"intensity_pct\":101}",
        "{\"effect\":\"aurora\",\"intensity_pct\":50.5}", "{\"effect\":\"aurora\",\"seed\":-1}", "{\"effect\":\"aurora\",\"seed\":2147483648}", "{\"effect\":\"aurora\",\"seed\":99999999999999999999}",
        "{\"effect\":\"aurora\",\"seed\":1.0}", "{\"effect\":\"aurora\",\"seed\":true}", "{\"effect\":\"aurora\",\"image\":\"https://evil.example/x.png\"}", "{\"effect\":\"aurora\",\"effect\":\"snow\"}",
        "{\"effect\":\"aurora</script><script>alert(1)//\"}",
    };
    Check(badRecipes.All(j => StartMotion.Parse(j) == null), "motion: unknown effects, floats, exponents, strings, out-of-range, negative and huge seeds, extra or repeated keys are refused (" + badRecipes.Length + " cases)");
    Check(badRecipes.All(j => StartMotion.Normalize(j) == StartMotion.None) && StartMotion.Normalize((string?)null) == StartMotion.None && StartMotion.Normalize((JsonElement?)null) == StartMotion.None, "motion: anything invalid becomes 'none' (fail closed, never throws)");
    Check(StartMotion.Normalize(new MotionRecipe("aurora", 1, 50, 1)) == StartMotion.None && StartMotion.Normalize(new MotionRecipe("bogus", 24000, 50, 1)) == StartMotion.None && StartMotion.Normalize(new MotionRecipe("snow", 24000, 50, -5)) == StartMotion.None, "motion: a recipe built in code with bad values is also 'none'");
    Check(StartMotion.ToJson(mGood) == "{\"effect\":\"aurora\",\"duration_ms\":30000,\"intensity_pct\":60,\"seed\":42}" && StartMotion.Parse(StartMotion.ToJson(mGood)) == mGood && StartMotion.ToJson(null) == "{\"effect\":\"none\",\"duration_ms\":24000,\"intensity_pct\":50,\"seed\":1}", "motion: canonical JSON (fixed order, integers) reads back identical");
    var mw = StartMotion.WithEffect(StartMotion.None, "rain");
    Check(mw.Effect == "rain" && StartMotion.WithSpeed(mw, "fast").DurationMs == 8000 && StartMotion.WithSpeed(mw, "slow").DurationMs == 60000 && StartMotion.WithStrength(mw, "high").IntensityPct == 85, "motion: the Settings buttons set effect, speed and strength");
    Check(StartMotion.WithEffect(mw, "nope") == mw && StartMotion.WithSpeed(mw, "warp") == mw && StartMotion.WithStrength(mw, "max") == mw && StartMotion.WithEffect(mw, "none") == StartMotion.None && StartMotion.WithSpeed(StartMotion.None, "fast") == StartMotion.None, "motion: unknown button values change nothing; Still turns it off");
    Check(StartMotion.Speeds.All(s => s.Value >= StartMotion.MinDuration && s.Value <= StartMotion.MaxDuration) && StartMotion.Strengths.All(s => s.Value >= 0 && s.Value <= 100), "motion: every speed and strength choice is inside the allowed range");

    // ---- the page configuration ----
    var lkDark = StartMotion.Look("#cba6f7", "#cdd6f4", "nebula", false);
    var cfg = StartMotion.ConfigJson(mGood, lkDark);
    Check(cfg == "{\"effect\":\"aurora\",\"duration_ms\":30000,\"intensity_pct\":60,\"seed\":42,\"fit\":\"cover\",\"position\":\"center\",\"overlay_color\":\"#0a0c10\",\"overlay_opacity_pct\":0,\"blur_px\":0,\"respect_reduced_motion\":true,\"colors\":{\"accent\":\"#cba6f7\",\"text\":\"#cdd6f4\",\"background\":\"#000000\"}}", "motion: boot configuration is exact (theme colours, reduced motion always respected)");
    using (var cd = JsonDocument.Parse(cfg)) Check(cd.RootElement.EnumerateObject().All(p => p.Value.ValueKind != JsonValueKind.Number || !p.Value.GetRawText().Contains('.')) && cd.RootElement.GetProperty("respect_reduced_motion").GetBoolean(), "motion: the configuration is valid JSON with integers only");
    var lkLight = StartMotion.Look("#2b6cb0", "#ffffff", "mint", false);
    Check(lkLight.LightBackground && lkLight.Text == "#1b2230" && StartMotion.ConfigJson(mGood, lkLight).Contains("\"background\":\"#ffffff\""), "motion: on a light background the text colour is dark and the canvas blends from white");
    var lkPic = StartMotion.Look("#2b6cb0", "#d6dae2", "image", true);
    Check(lkPic.UsePicture && StartMotion.ConfigJson(mGood, lkPic).Contains("\"overlay_opacity_pct\":48") && !StartMotion.Look(null, null, "image", false).UsePicture, "motion: your picture is drawn by the runtime with the same darkening; no picture saved means no picture");
    var lkBad = StartMotion.Look("red;}", "url(x)", "#zzzzzz", false);
    Check(lkBad.Accent == "#4c9bf0" && lkBad.Text == "#e9ebef" && !lkBad.LightBackground, "motion: colours that are not #rrggbb fall back to the start page's own");

    // ---- the vendored runtime ----
    string? rt = null;
    using (var rs = typeof(StartMotion).Assembly.GetManifestResourceStream("motion.js"))
        if (rs != null) { using var ms = new MemoryStream(); rs.CopyTo(ms); var bytes = ms.ToArray(); rt = Encoding.UTF8.GetString(bytes);
            Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant() == StartMotion.RuntimeSha256 && StartMotion.RuntimeSha256 == "074198d978e3f01bb4729775c5643659dba2b06906838152591c1f992f2679ce", "motion runtime: the embedded motion.js is ThemeForge's runtime byte-for-byte (SHA-256 074198d9...79ce)"); }
    Check(rt != null && StartMotion.RuntimeOk(rt) && rt.Contains("ThemeForgeMotion") && !Regex.IsMatch(rt, "fetch\\(|XMLHttpRequest|importScripts|WebSocket|https?://|eval\\(|new Function"), "motion runtime: it is found, passes the hash check and makes no network request");
    Check(!StartMotion.RuntimeOk(rt + " ") && !StartMotion.RuntimeOk((rt ?? "").Replace("0.16", "0.17")) && !StartMotion.RuntimeOk(null) && !StartMotion.RuntimeOk(""), "motion runtime: a changed or missing runtime is refused");

    // ---- the start page ----
    const string page = "<!doctype html><html><head><style>body{}</style></head><body>\n<h1>Recognition</h1>\n<script>var a=1;</script></body></html>";
    var on = StartMotion.Inject(page, mGood, lkDark, rt);
    Check(on.Contains("<canvas id='rec-motion'") && on.Contains("z-index:-1") && on.Contains("pointer-events:none") && on.Contains("ThemeForgeMotion.create") && on.Contains(cfg) && on.Contains(rt ?? "\u0000"), "start page: with an animation it gets the canvas, the runtime and the validated configuration");
    Check(on.IndexOf("<canvas", StringComparison.Ordinal) < on.IndexOf("<h1>", StringComparison.Ordinal) && on.IndexOf("ThemeForgeMotion.create", StringComparison.Ordinal) > on.IndexOf("<h1>", StringComparison.Ordinal) && on.EndsWith("</body></html>") && on.Contains("mix-blend-mode:screen"), "start page: the canvas sits first behind the content, the scripts at the end, blended over the CSS background");
    Check(!on.Contains("http://") && !on.Contains("https://") && !on.Contains(" src=") && Regex.Matches(on, "<script>").Count == 3, "start page: nothing is loaded from the network; the runtime is inline like the page's own script");
    Check(StartMotion.Inject(page, StartMotion.None, lkDark, rt) == page && StartMotion.Inject(page, null, lkDark, rt) == page, "start page: with no animation the page is unchanged (no canvas, no runtime)");
    Check(StartMotion.Inject(page, mGood, lkDark, rt + "//x") == page && StartMotion.Inject(page, mGood, lkDark, null) == page && StartMotion.Inject("<p>no body</p>", mGood, lkDark, rt) == "<p>no body</p>", "start page: a tampered or missing runtime, or a page without a body, leaves the page still");
    Check(StartMotion.Inject(page, new MotionRecipe("aurora</script>", 24000, 50, 1), lkDark, rt) == page, "start page: an effect name that is not on the list never reaches the page");
    var onPic = StartMotion.Inject(page, StartMotion.Parse("{\"effect\":\"kenburns\"}"), lkPic, rt);
    Check(onPic.Contains("getComputedStyle(document.body).backgroundImage") && onPic.Contains("data:image\\/jpeg;base64,") && onPic.Contains("mix-blend-mode:normal") && !onPic.Contains("go(null)"), "start page: with your picture the script reuses the picture already in the CSS (embedded once)");
    Check(StartMotion.Inject(page, mGood, lkLight, rt).Contains("mix-blend-mode:multiply"), "start page: light backgrounds blend with multiply");
    // the two pages are run in a simulated browser by browser.tests\shield\motion.test.js (node), after this program
    try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "motion_page_dark.html"), on); File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "motion_page_picture.html"), onPic); } catch { }

    // ---- theme files v2 ----
    var v1 = "{\"schema\":\"recognition.theme.v1\",\"id\":\"my-theme\",\"name\":\"My theme\",\"bar\":\"#1e1e2e\",\"text\":\"#cdd6f4\",\"accent\":\"#cba6f7\",\"start_bg\":\"nebula\",\"top\":\"violet\"}";
    var motionJ = "\"motion\":{\"effect\":\"fireflies\",\"duration_ms\":20000,\"intensity_pct\":70,\"seed\":7}";
    var v2 = v1.Replace("recognition.theme.v1", "recognition.theme.v2").Replace("}", "," + motionJ + "}");
    var p2 = ThemeLibrary.ParsePack(v2);
    Check(p2 != null && p2.Motion == new MotionRecipe("fireflies", 20000, 70, 7) && p2.Bar == "#1e1e2e" && p2.StartBg == "nebula" && p2.Top == "violet", "theme v2: a theme with motion is read");
    Check(p2 != null && ThemeLibrary.ToJson(p2) == "{\"schema\":\"recognition.theme.v2\",\"id\":\"my-theme\",\"name\":\"My theme\",\"bar\":\"#1e1e2e\",\"text\":\"#cdd6f4\",\"accent\":\"#cba6f7\",\"start_bg\":\"nebula\",\"top\":\"violet\"," + motionJ + "}\n" && ThemeLibrary.ParsePack(ThemeLibrary.ToJson(p2)) == p2, "theme v2: saved as v2 with motion last, and reads back identical");
    var p1 = ThemeLibrary.ParsePack(v1);
    Check(p1 != null && p1.Motion == null && ThemeLibrary.ToJson(p1) == "{\"schema\":\"recognition.theme.v1\",\"id\":\"my-theme\",\"name\":\"My theme\",\"bar\":\"#1e1e2e\",\"text\":\"#cdd6f4\",\"accent\":\"#cba6f7\",\"start_bg\":\"nebula\",\"top\":\"violet\"}\n", "theme v1: unchanged, still saved as v1 byte-for-byte");
    var v1WithMotion = ThemeLibrary.ParsePack(v1.Replace("}", "," + motionJ.Replace("fireflies", "bogus") + "}"));
    Check(v1WithMotion != null && v1WithMotion.Motion == null, "theme v1: a 'motion' key in a v1 file is ignored, as unknown keys always were");
    Check(ThemeLibrary.ParsePack(v2.Replace("fireflies", "explode")) == null && ThemeLibrary.ParsePack(v2.Replace("20000", "20000.5")) == null && ThemeLibrary.ParsePack(v2.Replace("\"seed\":7", "\"seed\":-7")) == null &&
          ThemeLibrary.ParsePack(v2.Replace("\"seed\":7", "\"seed\":7,\"url\":\"https://x\"")) == null && ThemeLibrary.ParsePack(v2.Replace(motionJ, "\"motion\":\"aurora\"")) == null && ThemeLibrary.ParsePack(v2.Replace("70", "700")) == null, "theme v2: a bad motion refuses the whole theme");
    var v2None = ThemeLibrary.ParsePack(v2.Replace("fireflies", "none"));
    var v2NoMotion = ThemeLibrary.ParsePack(v1.Replace("recognition.theme.v1", "recognition.theme.v2"));
    Check(v2None != null && v2None.Motion == null && ThemeLibrary.ToJson(v2None).Contains("recognition.theme.v1") && v2NoMotion != null && v2NoMotion == p1, "theme v2: motion 'none' or no motion is the same as a v1 theme and is saved as v1");
    Check(ThemeLibrary.ParsePack(v2.Replace("recognition.theme.v2", "recognition.theme.v3")) == null && ThemeLibrary.ParsePack(v2.Replace("#cdd6f4", "#1f1f2f")) == null, "theme v2: other schema versions and unreadable colours are still refused");

    // ---- library entries with motion (ThemeForge publishes into themes\library.v1.json) ----
    string Entry(string id, string extra) => "{\"id\":\"" + id + "\",\"name\":\"T " + id + "\",\"bar\":\"#1e1e2e\",\"text\":\"#cdd6f4\",\"accent\":\"#cba6f7\",\"start_bg\":\"nebula\",\"top\":\"\"" + extra + "}";
    var libM = "{\"schema\":\"recognition.theme.library.v1\",\"themes\":[" + Entry("plain", "") + "," + Entry("moving", "," + motionJ) + "," + Entry("broken", "," + motionJ.Replace("fireflies", "lava-lamp")) + "," + Entry("floaty", "," + motionJ.Replace("70", "70.5")) + "]}";
    var libP = ThemeLibrary.ParseLibrary(libM);
    Check(libP.Count == 2 && libP[0].Id == "plain" && libP[0].Motion == null && libP[1].Id == "moving" && libP[1].Motion == new MotionRecipe("fireflies", 20000, 70, 7), "theme library: an entry with motion is read with its recipe (schema stays recognition.theme.library.v1)");
    Check(!libP.Any(x => x.Id == "broken" || x.Id == "floaty"), "theme library: an entry with a bad motion is dropped like any other invalid entry");
    Check(ThemeLibrary.ToJson(libP[1]).Contains("recognition.theme.v2") && ThemeLibrary.ParsePack(ThemeLibrary.ToJson(libP[1])) == libP[1], "theme library: an added library theme with motion is kept as a v2 file that reads back identical");

    // ---- Chrome theme package with recognition.theme.json ----
    var manifest = "{\"manifest_version\":3,\"name\":\"Chrome name\",\"version\":\"1.0\",\"theme\":{\"images\":{\"theme_ntp_background\":\"images/ntp.png\"},\"colors\":{\"toolbar\":[30,30,46],\"toolbar_text\":[205,214,244]}}}";
    var fp = ThemeLibrary.FromPackage(manifest, v2, out var fperr);
    Check(fperr == null && fp != null && fp.Id == "my-theme" && fp.Name == "My theme" && fp.Accent == "#cba6f7" && fp.Motion == new MotionRecipe("fireflies", 20000, 70, 7) && fp.NtpImage == "images/ntp.png", "theme package: recognition.theme.json supplies colours and motion; the Chrome manifest supplies the picture");
    var fp1 = ThemeLibrary.FromPackage(manifest, "﻿" + v1, out var fperr1);
    Check(fperr1 == null && fp1 != null && fp1.Motion == null && fp1.Name == "My theme" && fp1.NtpImage == "images/ntp.png", "theme package: a v1 recognition.theme.json (with a byte-order mark) works too");
    var fpc = ThemeLibrary.FromPackage(manifest, null, out var fperrc);
    Check(fperrc == null && fpc != null && fpc.Id == "imported" && fpc.Name == "Chrome name" && fpc.Motion == null, "theme package: without recognition.theme.json the Chrome theme is used as before");
    Check(ThemeLibrary.FromPackage(manifest, v2.Replace("fireflies", "explode"), out var e1) == null && e1 != null && ThemeLibrary.FromPackage(manifest, "garbage", out var e2) == null && e2 != null &&
          ThemeLibrary.FromPackage(manifest, v2 + new string(' ', 21_000), out var e3) == null && e3 != null && ThemeLibrary.FromPackage(manifest, v2.Replace("#cdd6f4", "#1f1f2f"), out _) == null, "theme package: a bad, oversized or unreadable recognition.theme.json refuses the import (no silent fall back)");
    Check(ThemeLibrary.FromPackage("{\"name\":\"not a theme\"}", v2, out var e4) is ThemePack np && e4 == null && np.NtpImage == null && ThemeLibrary.FromPackage("{\"name\":\"x\"}", null, out var e5) == null && e5 != null, "theme package: a valid recognition.theme.json works even when the manifest has no picture; neither file usable is refused");
    var fpEvil = ThemeLibrary.FromPackage(manifest.Replace("images/ntp.png", "../../secret.png"), v2, out _);
    Check(fpEvil != null && fpEvil.NtpImage == null, "theme package: the picture path is still checked (no escaping the package)");

    // ---- static checks on the browser glue ----
    string? mRoot = null;
    for (var md = new DirectoryInfo(AppContext.BaseDirectory); md != null; md = md.Parent)
        if (File.Exists(Path.Combine(md.FullName, "browser", "MainWindow.Start.cs"))) { mRoot = md.FullName; break; }
    Check(mRoot != null, "motion glue: the browser sources are found");
    if (mRoot != null)
    {
        var startSrc = File.ReadAllText(Path.Combine(mRoot, "browser", "MainWindow.Start.cs"));
        var mainSrc = File.ReadAllText(Path.Combine(mRoot, "browser", "MainWindow.xaml.cs"));
        var czSrc = File.ReadAllText(Path.Combine(mRoot, "browser", "MainWindow.Customize.cs"));
        var proj = File.ReadAllText(Path.Combine(mRoot, "browser", "Recognition.Browser.csproj"));
        var fileBytes = File.ReadAllBytes(Path.Combine(mRoot, "browser", "shield", "motion.js"));
        Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fileBytes)).ToLowerInvariant() == StartMotion.RuntimeSha256 && proj.Contains("<EmbeddedResource Include=\"shield\\motion.js\" LogicalName=\"motion.js\" />"), "motion glue: browser\\shield\\motion.js has the recorded hash and is embedded in the browser");
        Check(mainSrc.Contains("return StartMotionHtml(html.Replace(") && startSrc.Contains("StartMotion.Inject(html, _startMotion, look, _motionJs)") && startSrc.Contains("ReadResource(StartMotion.RuntimeResource)"), "motion glue: the start page goes through StartMotion.Inject with the embedded runtime");
        Check(startSrc.Contains("StartMotion.Normalize(r.TryGetProperty(\"start_motion\"") && startSrc.Contains("StartMotion.ToJson(_startMotion)") && startSrc.Contains("StartMotion.WithEffect(") && !startSrc.Contains("HttpClient"), "motion glue: the setting is loaded through Normalize, saved canonically, changed only through the checked buttons, no network");
        Check(czSrc.Contains("ThemeLibrary.FromPackage(") && czSrc.Contains("ReadPackageTheme(root") && czSrc.Contains("ReadPackageTheme(f") && czSrc.Contains("ApplyPackMotion(p)") && !czSrc.Contains("ThemePacks.FromChromeTheme("), "motion glue: both theme package imports read recognition.theme.json through ThemeLibrary.FromPackage, and applying a theme sets its motion");
    }
}

// ==== extension developer area (ExtensionDev.cs; docs\proposals\0003) ====
Console.WriteLine();
Console.WriteLine("== extension developer area ==");
{
    string? dvRoot = null;
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "browser", "ExtensionDev.cs"))) { dvRoot = d.FullName; break; }
    Check(dvRoot != null, "dev: repository root found");
    var dvTmp = Path.Combine(Path.GetTempPath(), "rb-extdev-test-" + Guid.NewGuid().ToString("N"));
    try
    {
        var pol = dvRoot == null ? null : File.ReadAllText(Path.Combine(dvRoot, "config", "extension_policy.v1.json"));
        string Mk(string name, string manifest, params (string Path, string Text)[] files)
        {
            var dir = Path.Combine(dvTmp, name); Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "manifest.json"), manifest, new UTF8Encoding(false));
            foreach (var f in files) { var fp = Path.Combine(dir, f.Path.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(fp)!); File.WriteAllText(fp, f.Text, new UTF8Encoding(false)); }
            return dir;
        }
        bool Has(ExtDevReport r, ExtDevLevel l, string code) => r.Findings.Any(f => f.Level == l && f.Code == code);

        // a clean MV3 build
        var good = Mk("good", "{\"manifest_version\":3,\"name\":\"Dev Sample\",\"version\":\"1.0.0\",\"permissions\":[\"storage\"],\"icons\":{\"48\":\"i/48.png\"},\"background\":{\"service_worker\":\"sw.js\"},\"action\":{\"default_popup\":\"popup.html\"},\"options_ui\":{\"page\":\"opt.html\"},\"content_scripts\":[{\"matches\":[\"https://example.com/*\"],\"js\":[\"cs.js\"],\"css\":[\"cs.css\"]}],\"web_accessible_resources\":[{\"resources\":[\"img/*\",\"w.js\"],\"matches\":[\"https://example.com/*\"]}]}",
            ("i/48.png", "png"), ("sw.js", "//sw"), ("popup.html", "<p>"), ("opt.html", "<p>"), ("cs.js", "//cs"), ("cs.css", "p{}"), ("w.js", "//w"));
        var rg = ExtDevCheck.Run(good, pol);
        Check(rg.Errors == 0 && rg.Runnable && rg.Name == "Dev Sample" && rg.Version == "1.0.0" && rg.ManifestVersion == 3, "dev check: a complete MV3 build has no errors");
        Check(Has(rg, ExtDevLevel.Info, "policy_allow") && Has(rg, ExtDevLevel.Info, "engine_popup"), "dev check: the policy preview and the engine popup note are reported as information");

        // every reference to a missing file is reported, by where it is used
        var miss = Mk("miss", "{\"manifest_version\":3,\"name\":\"M\",\"version\":\"1\",\"icons\":{\"16\":\"nope.png\"},\"background\":{\"service_worker\":\"gone.js\"},\"action\":{\"default_popup\":\"pop.html\",\"default_icon\":\"ic.png\"},\"options_page\":\"o.html\",\"content_scripts\":[{\"matches\":[\"https://a.example/*\"],\"js\":[\"x.js\"],\"css\":[\"y.css\"]}],\"web_accessible_resources\":[{\"resources\":[\"r.js\",\"glob/*\"],\"matches\":[\"<all_urls>\"]}]}");
        var rm = ExtDevCheck.Run(miss, pol);
        var missTexts = rm.Findings.Where(f => f.Code == "file_ref").Select(f => f.Text).ToList();
        Check(new[] { "icons: missing file: nope.png", "background.service_worker: missing file: gone.js", "action.default_popup: missing file: pop.html", "action.default_icon: missing file: ic.png", "options_page: missing file: o.html", "content_scripts[0].js: missing file: x.js", "content_scripts[0].css: missing file: y.css", "web_accessible_resources: missing file: r.js" }.All(missTexts.Contains) && missTexts.Count == 8, "dev check: each missing file is named with the manifest key that uses it (globs are not checked)");
        Check(!rm.Runnable, "dev check: a build with missing files cannot run");

        // paths that leave the build, absolute paths and backslashes
        var esc = Mk("esc", "{\"manifest_version\":3,\"name\":\"E\",\"version\":\"1\",\"background\":{\"service_worker\":\"../outside.js\"},\"options_page\":\"C:/x.html\",\"icons\":{\"16\":\"a\\\\b.png\"}}");
        File.WriteAllText(Path.Combine(dvTmp, "outside.js"), "x");
        var re = ExtDevCheck.Run(esc, pol);
        Check(re.Findings.Count(f => f.Code == "file_ref" && (f.Text.Contains("outside the extension") || f.Text.Contains("not a path inside") || f.Text.Contains("backslash"))) == 3, "dev check: '..', drive paths and backslashes are refused even when the target exists");
        Check(ExtDevCheck.PathProblem(good, "sw.js") == null && ExtDevCheck.PathProblem(good, "/sw.js") == null && ExtDevCheck.PathProblem(good, "sw.js?x=1") == null && ExtDevCheck.PathProblem(good, "") != null && ExtDevCheck.PathProblem(good, "https://x/y.js") != null, "dev check: path rules (leading slash and query allowed, empty and URLs refused)");

        // version strings
        Check(new[] { "1", "1.0", "1.2.3.4", "0.0.1", "65535.0" }.All(ExtDevCheck.IsValidVersion) && !new[] { "", "1.", ".1", "1.2.3.4.5", "01.2", "1.65536", "1.0-beta", "v1", "1..2", "１" }.Any(ExtDevCheck.IsValidVersion), "dev check: Chrome version rules (1-4 parts, 0-65535, no leading zeros, digits only)");
        var badv = Mk("badv", "{\"manifest_version\":3,\"name\":\"V\",\"version\":\"1.0-beta\"}");
        Check(Has(ExtDevCheck.Run(badv, pol), ExtDevLevel.Error, "version"), "dev check: a bad version is an error");

        // manifest problems
        var nom = Path.Combine(dvTmp, "nomanifest"); Directory.CreateDirectory(nom);
        Check(Has(ExtDevCheck.Run(nom, pol), ExtDevLevel.Error, "no_manifest") && Has(ExtDevCheck.Run(Path.Combine(dvTmp, "absent"), pol), ExtDevLevel.Error, "no_folder"), "dev check: no manifest / no folder are errors, not exceptions");
        var badj = Mk("badj", "{ \"name\": \"x\",\n \"version\": }");
        var rj = ExtDevCheck.Run(badj, pol);
        Check(Has(rj, ExtDevLevel.Error, "manifest_json") && rj.Findings.Any(f => f.Text.Contains("line 2")), "dev check: invalid JSON is reported with its line");
        var arr = Mk("arr", "[1,2]");
        Check(Has(ExtDevCheck.Run(arr, pol), ExtDevLevel.Error, "manifest_json"), "dev check: a manifest that is not an object is refused");

        // manifest_version 2 parts in a manifest_version 3 build, and plain MV2
        var mix = Mk("mix", "{\"manifest_version\":3,\"name\":\"X\",\"version\":\"1\",\"background\":{\"scripts\":[\"b.js\"],\"persistent\":false},\"browser_action\":{},\"web_accessible_resources\":[\"b.js\"]}", ("b.js", "//"));
        var rx = ExtDevCheck.Run(mix, pol);
        Check(Has(rx, ExtDevLevel.Error, "mv3_background") && Has(rx, ExtDevLevel.Error, "mv3_action") && Has(rx, ExtDevLevel.Error, "mv3_war") && Has(rx, ExtDevLevel.Warning, "mv3_persistent"), "dev check: MV2-only keys in an MV3 build are errors");
        var mv2 = Mk("mv2", "{\"manifest_version\":2,\"name\":\"Old\",\"version\":\"1\",\"background\":{\"scripts\":[\"b.js\"]}}", ("b.js", "//"));
        var rv2 = ExtDevCheck.Run(mv2, pol);
        Check(Has(rv2, ExtDevLevel.Warning, "mv2") && !Has(rv2, ExtDevLevel.Error, "mv3_background"), "dev check: manifest_version 2 is a warning (retired), its own keys are fine");
        Check(Has(ExtDevCheck.Run(Mk("mv4", "{\"manifest_version\":4,\"name\":\"F\",\"version\":\"1\"}"), pol), ExtDevLevel.Error, "manifest_version"), "dev check: manifest_version other than 2 or 3 is an error");

        // locales
        Check(Has(ExtDevCheck.Run(Mk("loc1", "{\"manifest_version\":3,\"name\":\"__MSG_n__\",\"version\":\"1\"}"), pol), ExtDevLevel.Error, "locale"), "dev check: __MSG_ without default_locale is an error");
        Check(Has(ExtDevCheck.Run(Mk("loc2", "{\"manifest_version\":3,\"name\":\"__MSG_n__\",\"version\":\"1\",\"default_locale\":\"en\"}"), pol), ExtDevLevel.Error, "locale"), "dev check: default_locale without _locales/<l>/messages.json is an error");
        Check(!Has(ExtDevCheck.Run(Mk("loc3", "{\"manifest_version\":3,\"name\":\"__MSG_n__\",\"version\":\"1\",\"default_locale\":\"en\"}", ("_locales/en/messages.json", "{}")), pol), ExtDevLevel.Error, "locale"), "dev check: a complete locale is accepted");
        Check(Has(ExtDevCheck.Run(Mk("loc4", "{\"manifest_version\":3,\"name\":\"n\",\"version\":\"1\",\"default_locale\":\"../x\"}"), pol), ExtDevLevel.Error, "locale"), "dev check: a default_locale that is a path is refused");

        // the policy preview is the governance policy itself
        var deny = Mk("deny", "{\"manifest_version\":3,\"name\":\"All\",\"version\":\"1\",\"host_permissions\":[\"<all_urls>\"]}");
        var rd = ExtDevCheck.Run(deny, pol);
        Check(Has(rd, ExtDevLevel.Error, "policy_deny") && !rd.Runnable, "dev check: a build the policy denies is an error, so it is not run");
        var rev = Mk("rev", "{\"manifest_version\":3,\"name\":\"Tabs\",\"version\":\"1\",\"permissions\":[\"tabs\"]}");
        var rr = ExtDevCheck.Run(rev, pol);
        Check(Has(rr, ExtDevLevel.Warning, "policy_review") && rr.Runnable, "dev check: a build that needs review can run in the developer tab (installing still needs the review)");
        Check(!ExtDevCheck.Run(rev, null).Findings.Any(f => f.Code.StartsWith("policy")), "dev check: without a policy file there is no policy preview (and no invented decision)");

        // leftovers
        var junk = Mk("junk", "{\"manifest_version\":3,\"name\":\"J\",\"version\":\"1\"}", ("node_modules/x/index.js", "//"), ("a.js.map", "{}"));
        var rjk = ExtDevCheck.Run(junk, pol);
        Check(Has(rjk, ExtDevLevel.Warning, "junk_node_modules") && Has(rjk, ExtDevLevel.Info, "sourcemaps") && rjk.Runnable, "dev check: node_modules and source maps are pointed out, not refused");
        Check(Has(ExtDevCheck.Run(Mk("eng", "{\"manifest_version\":3,\"name\":\"S\",\"version\":\"1\",\"side_panel\":{\"default_path\":\"s.html\"},\"commands\":{}}", ("s.html", "x")), pol), ExtDevLevel.Info, "engine_side_panel"), "dev check: parts the engine may not support are information, never errors");
        var bom = Path.Combine(dvTmp, "bom"); Directory.CreateDirectory(bom);
        File.WriteAllBytes(Path.Combine(bom, "manifest.json"), new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{\"manifest_version\":3,\"name\":\"B\",\"version\":\"1\"}")).ToArray());
        var rbom = ExtDevCheck.Run(bom, pol);
        Check(Has(rbom, ExtDevLevel.Warning, "manifest_bom") && rbom.Name == "B", "dev check: a byte-order mark is a warning and the manifest is still read");

        // check is deterministic
        Check(string.Join("\n", ExtDevCheck.Run(miss, pol).Findings) == string.Join("\n", ExtDevCheck.Run(miss, pol).Findings), "dev check: the same build gives the same findings in the same order");

        // diff between builds
        var idA = ExtGovernance.ComputeIdentity(good); var manA = ExtGovernance.ReadManifest(good);
        var good2 = Path.Combine(dvTmp, "good2"); ExtPackage.CopyDirectory(good, good2);
        File.WriteAllText(Path.Combine(good2, "manifest.json"), File.ReadAllText(Path.Combine(good, "manifest.json")).Replace("\"version\":\"1.0.0\"", "\"version\":\"1.0.1\"").Replace("[\"storage\"]", "[\"storage\",\"tabs\"],\"host_permissions\":[\"https://b.example/*\"]"));
        File.WriteAllText(Path.Combine(good2, "cs.js"), "//cs changed"); File.Delete(Path.Combine(good2, "w.js")); File.WriteAllText(Path.Combine(good2, "new.js"), "//n");
        var idB = ExtGovernance.ComputeIdentity(good2); var manB = ExtGovernance.ReadManifest(good2);
        var dc = ExtDevDiff.Compare(idA.Files, manA, idB.Files, manB);
        Check(dc.OldVersion == "1.0.0" && dc.NewVersion == "1.0.1" && dc.FilesAdded.SequenceEqual(new[] { "new.js" }) && dc.FilesRemoved.SequenceEqual(new[] { "w.js" }) && dc.FilesChanged.SequenceEqual(new[] { "cs.js", "manifest.json" }), "dev diff: files added, removed and changed (sorted)");
        Check(dc.PermsAdded.SequenceEqual(new[] { "tabs" }) && dc.HostsAdded.SequenceEqual(new[] { "https://b.example/*" }) && dc.AccessGrew && dc.PermsRemoved.Count == 0, "dev diff: new permissions and site access are flagged as more access");
        var back = ExtDevDiff.Compare(idB.Files, manB, idA.Files, manA);
        Check(back.PermsRemoved.SequenceEqual(new[] { "tabs" }) && !back.AccessGrew && ExtDevDiff.Compare(idA.Files, manA, idA.Files, manA).Same, "dev diff: removing access is not 'more access'; identical builds are the same");

        // deterministic package
        var z1 = ExtDevPack.Zip(good);
        File.SetLastWriteTimeUtc(Path.Combine(good, "sw.js"), new DateTime(2031, 5, 6, 7, 8, 9, DateTimeKind.Utc));
        var z2 = ExtDevPack.Zip(good);
        Check(z1.SequenceEqual(z2), "dev package: the same files give byte-identical packages (file times do not leak in)");
        var gitDir = Path.Combine(good, ".git"); Directory.CreateDirectory(gitDir); File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref");
        Check(ExtDevPack.Zip(good).SequenceEqual(z1), "dev package: .git is never packaged (same as the identity)");
        var unz = Path.Combine(dvTmp, "unz"); var unRoot = ExtPackage.Unpack(z1, unz);
        Check(ExtGovernance.ComputeIdentity(unRoot).Id == idA.Id, "dev package: unpacking it gives exactly the build's identity");
        using (var za = new System.IO.Compression.ZipArchive(new MemoryStream(z1)))
            Check(za.Entries.Select(e => e.FullName).SequenceEqual(idA.Files.Select(f => f.Path)) && za.Entries.All(e => e.LastWriteTime.Year == 1980 && e.CompressedLength == e.Length), "dev package: entries in identity order, fixed 1980 time, stored");
        Check(!z1.SequenceEqual(ExtDevPack.Zip(good2)), "dev package: different files give a different package");

        // state file
        var up = Path.Combine(dvTmp, "uploads"); Directory.CreateDirectory(up);
        var st = new ExtDevStateFile();
        var pa = new ExtDevProject { Id = "0123456789abcdef", Label = "A", Folder = good };
        var pb = new ExtDevProject { Id = "fedcba9876543210", Label = "B", Folder = Path.Combine(up, new string('a', 64)), Uploaded = true };
        st.Projects.Add(pa); st.Projects.Add(pb);
        for (int i = 0; i < 40; i++) ExtDevState.AddBuild(pa, new ExtDevBuild { Gid = i.ToString("x64"), Version = "1." + i, Utc = "2026-10-21T00:00:00Z" });
        Check(pa.Builds.Count == ExtDevState.MaxBuilds && pa.Builds[^1].Version == "1.39", "dev state: build history keeps the newest " + ExtDevState.MaxBuilds);
        ExtDevState.AddBuild(pa, new ExtDevBuild { Gid = 39.ToString("x64"), Version = "1.39", Errors = 2 });
        Check(pa.Builds.Count == ExtDevState.MaxBuilds && pa.Builds[^1].Errors == 2, "dev state: the same bytes again refresh the last build instead of adding one");
        var json = ExtDevState.ToJson(st);
        var st2 = ExtDevState.Parse(json, up);
        Check(st2.Projects.Count == 2 && ExtDevState.ToJson(st2) == json, "dev state: round-trips byte for byte");
        var evil = json.Replace(Path.Combine(up, new string('a', 64)).Replace("\\", "\\\\"), Path.Combine(dvTmp, "elsewhere").Replace("\\", "\\\\"));
        Check(ExtDevState.Parse(evil, up).Projects.Count == 1, "dev state: an 'uploaded' build pointing outside the upload folder is dropped");
        Check(ExtDevState.Parse(json.Replace("0123456789abcdef", "../../etc"), up).Projects.Count == 1 && ExtDevState.Parse(json.Replace("\"Label\":\"A\"", "\"Label\":\"A\\u0007\""), up).Projects.Count == 1, "dev state: a bad id or a control character drops only that entry");
        Check(ExtDevState.Parse(json.Replace("recognition.extensions_dev.v1", "recognition.extensions_dev.v9"), up).Projects.Count == 0 && ExtDevState.Parse("{nope", up).Projects.Count == 0 && ExtDevState.Parse(null, up).Projects.Count == 0, "dev state: another schema, garbage or no file give an empty list, never an exception");
        Check(ExtDevState.Parse(json.Replace(good.Replace("\\", "\\\\"), "relative\\\\path"), up).Projects.Count == 1, "dev state: a relative folder is dropped");
        Check(ExtDevState.NewId("x").Length == 16 && ExtDevState.NewId("x") != ExtDevState.NewId("x"), "dev state: project ids are 16 hex and unique");
    }
    finally { try { Directory.Delete(dvTmp, true); } catch { } }

    // regression (stress test, 2026-10-21): a ZIP whose deflate data is damaged was a raw IOException, not a governed refusal
    {
        var dvZ = Path.Combine(Path.GetTempPath(), "rb-dvz-" + Guid.NewGuid().ToString("N"));
        try
        {
            // a valid three-file package (manifest.json, a.js, sub/b.css) with ONE byte changed in the central directory (offset 309, found by
            // the stress fuzzer, seed 20261021): .NET then reports "Zip file corrupt: unexpected end of stream reached" while reading an entry
            var zb = Convert.FromBase64String("UEsDBBQAAAAIAAO5VV2tF2HpLAAAADEAAAANAAAAbWFuaWZlc3QuanNvbqtWyk3My0xLLS6JL0stKs7Mz1OyMtZRykvMTVWyUgpW0lGCCysZ6hko1QIAUEsDBBQAAAAIAAO5VV1ssfnkBQAAAAMAAAAEAAAAYS5qc9PXTwQAUEsDBBQAAAAIAAO5VV3+HM9JBQAAAAMAAAAJAAAAc3ViL2IuY3NzK6iuBQBQSwECFAMUAAAACAADuVVdrRdh6SwAAAAxAAAADQAAAAAAAAAAAAAApIEAAAAAbWFuaWZlc3QuanNvblBLAQIUAxQAAAAIAAO5VV1ssfnkBQAAAAMAAAAEAAAAAAAAAAAAAACkgVcAAABhLmpzUEsBAhQDFAAAAAgAA7lVXf4cz0kFAAAAAwAAAAkAAAAAAAAAAAAAAKSBfgAAAHN1Yi9iLmNzc1BLBQYAAAAAAwADAKQAAACqAAAAAAA=");
            Check(zb.Length == 356 && zb[309] == 0, "packages: regression vector intact");
            zb[309] = 248;
            string kind; try { ExtPackage.Unpack(zb, dvZ); kind = "accepted"; } catch (ExtGovException) { kind = "refused"; } catch (Exception ex) { kind = ex.GetType().Name; }
            Check(kind == "refused", "packages: a ZIP with a damaged file list is a governed refusal (ExtGovException), not a raw I/O error (got " + kind + ")");
        }
        finally { try { Directory.Delete(dvZ, true); } catch { } }
    }

    // ---- static checks on the developer glue: what it must never do ----
    if (dvRoot != null)
    {
        var dg = File.ReadAllText(Path.Combine(dvRoot, "browser", "MainWindow.ExtensionDev.cs"));
        var ex = File.ReadAllText(Path.Combine(dvRoot, "browser", "MainWindow.Extensions.cs"));
        var mw = File.ReadAllText(Path.Combine(dvRoot, "browser", "MainWindow.xaml.cs"));
        Check(!dg.Contains("ExtGovernance.Record(") && !dg.Contains("ExtLedgerPath") && !dg.Contains("_extState.Items.Add"), "dev glue: never writes the governance ledger or the installed list");
        Check(!dg.Contains("AnyMainProfile(") && !dg.Contains("_env") && dg.Contains("EnsureExtDevEnvAsync()") && dg.Contains("NewTabCoreAsync(\"Extension developer\", true, env)"), "dev glue: builds run only in the separate developer profile, in a private tab");
        Check(dg.Contains("AddBrowserExtensionAsync(res.Snapshot)") && dg.Contains("if (!res.Report.Runnable || res.Snapshot == null || res.Gid == null)") && dg.Contains("ExtGovernance.ComputeIdentity(tmp).Id != id.Id"), "dev glue: runs the checked, re-hashed snapshot and refuses a build with errors (including a policy deny)");
        Check(dg.Contains("StageFolder(res.Snapshot") && ex.Contains("if (decision == \"deny\")") , "dev glue: Send to review goes through the normal review card, policy and approval");
        Check(ex.Contains("if (msg.StartsWith(\"ext-dev-\")) { await HandleExtDevMessage(msg); return; }") && mw.Contains("else if (msg.StartsWith(\"ext-\") && tab.Internal == \"extensions\") HandleExtMessage(msg);"), "dev glue: developer commands are accepted only from the Extensions page");
        Check(dg.Contains("CleanupExtDevOnExit") && ex.Contains("CleanupExtDevOnExit();") && dg.Contains("Path.Combine(TmpRoot, \"rb-extdev-\""), "dev glue: the developer profile and snapshots live in runtime\\tmp and are deleted on exit");
        Check(!dg.Contains("HttpClient") && !dg.Contains("DownloadExtensionAsync"), "dev glue: makes no network request");
        Check(dg.Contains("_actions?.Append(\"extension.dev_build\"") && dg.Contains("_actions?.Append(\"extension.dev_run\"") && dg.Contains("_actions?.Append(\"extension.dev_pack\"") && dg.Contains("_actions?.Append(\"extension.dev_upload\""), "dev glue: builds, runs, uploads and packages are receipted");
        Check(ex.Contains("\" + ExtDevSectionHtml + @\"") && ex.Contains("renderDev(m.dev);") && ex.Contains("dev = ExtDevView()"), "dev glue: the Developer tab is part of the Extensions page");
    }
}

// ==== constellation.integration.receipt.v1 (IntegrationReceipts.cs) ====
Console.WriteLine();
Console.WriteLine("== integration receipts ==");
{
    var z = IntegrationReceipts.Genesis; var h1 = new string('a', 64); var h2 = new string('b', 64);
    var irGolden = "{\"schema\":\"constellation.integration.receipt.v1\",\"consumer\":\"recognition\",\"provider\":\"soteriavault\",\"emitted_by\":\"consumer\",\"contract\":\"soteriavault.connector_contract.v1\",\"request_sha256\":null,\"response_sha256\":\"" + h1 + "\",\"carried\":[],\"outcome\":\"deferred\",\"error_token\":\"SOTERIA_LINK_NOT_ENABLED\",\"prev_sha256\":\"" + z + "\"}";
    Check(IntegrationReceipts.Line("recognition", "soteriavault", "consumer", "soteriavault.connector_contract.v1", null, h1, Array.Empty<IntegrationCarried>(), "deferred", "SOTERIA_LINK_NOT_ENABLED", z) == irGolden, "receipt: exact byte form (schema key order, no whitespace, nulls)");
    Check(IntegrationReceipts.Line("a", "b", "provider", null, h1, h2, new[] { new IntegrationCarried("arbiter.policy.decision.v1", h2) }, "ok", null, z).Contains(",\"carried\":[{\"contract\":\"arbiter.policy.decision.v1\",\"sha256\":\"" + h2 + "\"}],"), "receipt: carried entries in contract, sha256 order");
    bool Refused(Func<string> f) { try { f(); return false; } catch (IntegrationReceiptException) { return true; } }
    var irNone = Array.Empty<IntegrationCarried>();
    Check(Refused(() => IntegrationReceipts.Line("Recognition", "b", "consumer", null, null, null, irNone, "ok", null, z)) && Refused(() => IntegrationReceipts.Line("a", "-b", "consumer", null, null, null, irNone, "ok", null, z)) &&
          Refused(() => IntegrationReceipts.Line("a", "b", "both", null, null, null, irNone, "ok", null, z)) && Refused(() => IntegrationReceipts.Line("a", "b", "consumer", "no-version", null, null, irNone, "ok", null, z)) &&
          Refused(() => IntegrationReceipts.Line("a", "b", "consumer", null, "ABC", null, irNone, "ok", null, z)) && Refused(() => IntegrationReceipts.Line("a", "b", "consumer", null, null, null, irNone, "great", null, z)) &&
          Refused(() => IntegrationReceipts.Line("a", "b", "consumer", null, null, null, irNone, "ok", "X", z)) && Refused(() => IntegrationReceipts.Line("a", "b", "consumer", null, null, null, irNone, "failed", null, z)) &&
          Refused(() => IntegrationReceipts.Line("a", "b", "consumer", null, null, null, irNone, "failed", "lower_case", z)) && Refused(() => IntegrationReceipts.Line("a", "b", "consumer", null, null, null, irNone, "ok", null, "0")) &&
          Refused(() => IntegrationReceipts.Line("a", "b", "consumer", null, null, null, new[] { new IntegrationCarried("x", h1) }, "ok", null, z)), "receipt: everything the schema forbids is refused (ids, emitted_by, contract, hashes, outcome/error_token rule, carried)");
    Check(IntegrationReceipts.ContractId("soteriavault.connector_contract.v1") && IntegrationReceipts.ContractId("neverlost.trust_bundle.v12") && !IntegrationReceipts.ContractId("x.v") && !IntegrationReceipts.ContractId("x.va") && !IntegrationReceipts.ContractId(".v1"), "receipt: contract id pattern");
    Check(IntegrationReceipts.SoteriaOutcome(SoteriaState.NotConfigured) == null && IntegrationReceipts.SoteriaOutcome(SoteriaState.Ready) == ("ok", null) &&
          IntegrationReceipts.SoteriaOutcome(SoteriaState.ContractOnly)!.Value.Outcome == "deferred" && IntegrationReceipts.SoteriaOutcome(SoteriaState.Unavailable)!.Value.Outcome == "unavailable" &&
          IntegrationReceipts.SoteriaOutcome(SoteriaState.Invalid)!.Value.Outcome == "refused", "receipt: SoteriaVault link states map to explicit outcomes (nothing chosen = no exchange, no receipt)");

    var irDir = Path.Combine(Path.GetTempPath(), "rb-ir-" + Guid.NewGuid().ToString("N"));
    try
    {
        var f = Path.Combine(irDir, "sub", "r.ndjson");
        Check(IntegrationReceipts.Verify(f) == (0, z), "receipt chain: no file = empty chain at genesis");
        var a1 = IntegrationReceipts.Append(f, "recognition", "soteriavault", "consumer", "soteriavault.connector_contract.v1", null, h1, irNone, "deferred", "SOTERIA_LINK_NOT_ENABLED");
        var a2 = IntegrationReceipts.Append(f, "recognition", "soteriavault", "consumer", "soteriavault.connector_contract.v1", null, null, irNone, "unavailable", "SOTERIA_NOT_FOUND");
        var a3 = IntegrationReceipts.Append(f, "recognition", "soteriavault", "consumer", "soteriavault.connector_contract.v1", null, h2, irNone, "ok", null);
        var irLines = File.ReadAllText(f).Split('\n');
        Check(irLines.Length == 4 && irLines[3] == "" && irLines[0] == irGolden && a1 == IntegrationReceipts.Hash(irGolden), "receipt chain: first line is the golden bytes, LF-terminated; hash = SHA-256 of the line without LF");
        Check(irLines[1].EndsWith("\"prev_sha256\":\"" + a1 + "\"}") && irLines[2].EndsWith("\"prev_sha256\":\"" + a2 + "\"}") && IntegrationReceipts.Verify(f) == (3, a3), "receipt chain: each receipt links to the previous one; verify recomputes the head");
        Check(!File.ReadAllText(f).Contains('\r') && !File.ReadAllText(f).Contains("ts") && !File.ReadAllText(f).Contains("time"), "receipt chain: LF only, no timestamps");
        var irGood = File.ReadAllBytes(f);
        bool Broken(Func<byte[], byte[]> mutate)
        {
            File.WriteAllBytes(f, mutate((byte[])irGood.Clone()));
            bool broke = false; try { IntegrationReceipts.Verify(f); } catch (IntegrationReceiptException) { broke = true; }
            bool refusedAppend = false; try { IntegrationReceipts.Append(f, "recognition", "soteriavault", "consumer", null, null, null, irNone, "failed", "X"); } catch (IntegrationReceiptException) { refusedAppend = true; }
            File.WriteAllBytes(f, irGood); return broke && refusedAppend;
        }
        var irTxt = Encoding.UTF8.GetString(irGood); var irLs = irTxt.TrimEnd('\n').Split('\n');
        Check(Broken(b => Encoding.UTF8.GetBytes(irTxt.Replace("\"unavailable\"", "\"ok\"").Replace("\"SOTERIA_NOT_FOUND\"", "null"))), "receipt chain: an edited receipt breaks the chain, and nothing more is appended");
        Check(Broken(b => Encoding.UTF8.GetBytes(irLs[0] + "\n" + irLs[2] + "\n")), "receipt chain: a removed receipt is detected");
        Check(Broken(b => Encoding.UTF8.GetBytes(irLs[1] + "\n" + irLs[0] + "\n" + irLs[2] + "\n")), "receipt chain: reordered receipts are detected");
        Check(Broken(b => b.Take(b.Length - 1).ToArray()), "receipt chain: a partial (unterminated) last receipt is detected");
        Check(Broken(b => Encoding.UTF8.GetBytes(irTxt.Replace("\n", "\r\n"))) && Broken(b => Encoding.UTF8.GetBytes(irTxt.Replace("{\"schema\":", "{ \"schema\":"))) && Broken(b => new byte[] { 0xEF, 0xBB, 0xBF }.Concat(b).ToArray()), "receipt chain: CRLF, extra whitespace and a byte-order mark are not canonical");
        Check(IntegrationReceipts.Verify(f) == (3, a3), "receipt chain: the original file still verifies after the negative cases");
        // the fast path (audit F11): an unchanged file is not re-parsed, but ANY changed byte forces the full check, even with the same size and time
        var irT = File.GetLastWriteTimeUtc(f);
        var same = Encoding.UTF8.GetString(irGood).Replace("\"SOTERIA_NOT_FOUND\"", "\"SOTERIA_NOT_FOUNE\"");
        File.WriteAllText(f, same, new UTF8Encoding(false)); File.SetLastWriteTimeUtc(f, irT);
        bool sameSizeCaught = false; try { IntegrationReceipts.Append(f, "recognition", "soteriavault", "consumer", null, null, null, irNone, "failed", "X"); } catch (IntegrationReceiptException) { sameSizeCaught = true; }
        Check(sameSizeCaught && File.ReadAllBytes(f).Length == irGood.Length, "receipt chain: an edit that keeps the file size and time is still caught; nothing is appended");
        File.WriteAllBytes(f, irGood);
        var a4 = IntegrationReceipts.Append(f, "recognition", "soteriavault", "consumer", null, null, null, irNone, "failed", "X");
        var a5 = IntegrationReceipts.Append(f, "recognition", "soteriavault", "consumer", null, null, null, irNone, "failed", "Y");
        IntegrationReceipts.ForgetVerified();
        Check(IntegrationReceipts.Verify(f) == (5, a5) && File.ReadAllText(f).Split('\n')[4].EndsWith("\"prev_sha256\":\"" + a4 + "\"}"), "receipt chain: appends on the fast path link exactly as a full verification says");
    }
    finally { try { Directory.Delete(irDir, true); } catch { } }

    // ---- the browser's own witness: closes the newest-receipt gap a chain cannot see ----
    {
        var wDir = Path.Combine(Path.GetTempPath(), "rb-irw-" + Guid.NewGuid().ToString("N"));
        try
        {
            var wf = Path.Combine(wDir, "receipts.ndjson"); var wNone = Array.Empty<IntegrationCarried>();
            // the witness lives in a REAL GovernedActions ledger (in memory here), exactly as in the browser
            var wStore = new Dictionary<string, string>();
            var wLedger = new GovernedActions("mem://w", p => wStore.TryGetValue(p, out var v) ? v : "", (p, t) => wStore[p] = t);
            List<string> W() => IntegrationReceipts.Witnessed(wLedger.Items.Select(i => i.Action), "recognition", "soteriavault");
            void Rec(string h) => wLedger.Append(IntegrationReceipts.WitnessEntry("recognition", "soteriavault", h));
            (string, string?) Add(string outcome, string? tok) => IntegrationReceipts.AppendWitnessed(wf, W(), Rec, null, "recognition", "soteriavault", "consumer", "soteriavault.connector_contract.v1", null, null, wNone, outcome, tok);
            Check(IntegrationReceipts.CheckWitness(wf, W()) == IntegrationReceipts.WitnessState.Empty, "witness: no file and no witness is an empty start");
            var w1 = Add("deferred", "SOTERIA_LINK_NOT_ENABLED"); var w2 = Add("unavailable", "SOTERIA_NOT_FOUND"); var w3 = Add("ok", null);
            var wl = new GovernedActions("mem://w", p => wStore[p], (p, t) => wStore[p] = t); wl.Load();
            Check(W().SequenceEqual(new[] { w1.Item1, w2.Item1, w3.Item1 }) && wl.Verify(out _) && IntegrationReceipts.Witnessed(wl.Items.Select(i => i.Action), "recognition", "soteriavault").Last() == w3.Item1 && w1.Item2 == null,
                  "witness: each receipt's hash is recorded in the hash-chained action ledger and reads back after a reload");
            Check(IntegrationReceipts.CheckWitness(wf, W()) == IntegrationReceipts.WitnessState.Matches, "witness: the file matches what the browser witnessed");
            var wGood = File.ReadAllBytes(wf); var wLines = Encoding.UTF8.GetString(wGood).TrimEnd('\n').Split('\n');
            bool WRefused(string label, string content)
            {
                File.WriteAllText(wf, content, new UTF8Encoding(false)); IntegrationReceipts.ForgetVerified();
                bool chainOk; try { IntegrationReceipts.Verify(wf); chainOk = true; } catch (IntegrationReceiptException) { chainOk = false; }
                bool caught = false; var before = File.ReadAllBytes(wf); int wBefore = W().Count;
                try { Add("ok", null); } catch (IntegrationReceiptException ex) { caught = ex.Message.StartsWith("RECEIPT_WITNESS_MISMATCH"); }
                bool untouched = File.ReadAllBytes(wf).SequenceEqual(before) && W().Count == wBefore;
                File.WriteAllBytes(wf, wGood); IntegrationReceipts.ForgetVerified();
                Check(chainOk && caught && untouched, "witness: " + label + " - the chain alone still verifies, the witness refuses it, nothing is appended or witnessed");
                return caught;
            }
            // the newest receipt rewritten to say something else, still canonical and still linking to the one before
            var lastForged = IntegrationReceipts.Line("recognition", "soteriavault", "consumer", "soteriavault.connector_contract.v1", null, null, wNone, "failed", "FORGED", IntegrationReceipts.Hash(wLines[1]));
            WRefused("the newest receipt rewritten", wLines[0] + "\n" + wLines[1] + "\n" + lastForged + "\n");
            WRefused("the newest receipt removed", wLines[0] + "\n" + wLines[1] + "\n");
            WRefused("a forged receipt added at the end", string.Join("\n", wLines) + "\n" + IntegrationReceipts.Line("recognition", "soteriavault", "consumer", null, null, null, wNone, "ok", null, w3.Item1) + "\n");
            var wr1 = IntegrationReceipts.Line("recognition", "soteriavault", "consumer", null, null, null, wNone, "ok", null, IntegrationReceipts.Genesis);
            WRefused("the whole chain rebuilt from scratch", wr1 + "\n" + IntegrationReceipts.Line("recognition", "soteriavault", "consumer", null, null, null, wNone, "ok", null, IntegrationReceipts.Hash(wr1)) + "\n");
            WRefused("everything deleted", "");
            bool missingCaught = false;
            try { IntegrationReceipts.CheckWitness(wf, new[] { w1.Item1, new string('d', 64), w3.Item1 }); } catch (IntegrationReceiptException ex) { missingCaught = ex.Message.Contains("missing"); }
            Check(missingCaught, "witness: a witnessed receipt that is not in the file is reported");
            Check(IntegrationReceipts.CheckWitness(wf, W()) == IntegrationReceipts.WitnessState.Matches && Add("ok", null).Item2 == null, "witness: after the attacks the real file still matches and appends normally");

            // ---- NFL, the outside witness ----
            IntegrationReceipts.NflView Nfl(int c, string h) => new(true, c, h, "");
            var nfZ = Path.Combine(wDir, "nfl", "receipts.ndjson"); var nfW = new List<string>();
            (string, string?) NAdd(IntegrationReceipts.NflView? v, string tok = "X") => IntegrationReceipts.AppendWitnessed(nfZ, nfW, h => nfW.Add(h), v, "recognition", "soteriavault", "consumer", null, null, null, wNone, "failed", tok);
            var n1 = NAdd(Nfl(0, IntegrationReceipts.Genesis)); var n2 = NAdd(Nfl(1, n1.Item1)); var n3 = NAdd(Nfl(1, n1.Item1));
            Check(IntegrationReceipts.Verify(nfZ).Count == 3, "nfl: NFL's last witnessed receipt still in the file (even an older one, with newer receipts after it) - appends go ahead");
            var nfGood = File.ReadAllBytes(nfZ);
            // the attack the browser's own witness cannot stop: a program running as you rewrites the newest receipt AND the browser's record
            var nLines = Encoding.UTF8.GetString(nfGood).TrimEnd('\n').Split('\n');
            var forgedLast = IntegrationReceipts.Line("recognition", "soteriavault", "consumer", null, null, null, wNone, "ok", null, IntegrationReceipts.Hash(nLines[1]));
            File.WriteAllText(nfZ, nLines[0] + "\n" + nLines[1] + "\n" + forgedLast + "\n", new UTF8Encoding(false));
            var forgedW = new List<string> { n1.Item1, n2.Item1, IntegrationReceipts.Hash(forgedLast) };   // the local record rewritten to match
            Check(IntegrationReceipts.CheckWitness(nfZ, forgedW) == IntegrationReceipts.WitnessState.Matches, "nfl: (setup) the forgery fools both the chain and the rewritten local record");
            bool nflCaught = false; int fwBefore = forgedW.Count; var fBytes = File.ReadAllBytes(nfZ);
            try { IntegrationReceipts.AppendWitnessed(nfZ, forgedW, h => forgedW.Add(h), Nfl(3, n3.Item1), "recognition", "soteriavault", "consumer", null, null, null, wNone, "ok", null); }
            catch (IntegrationReceiptException ex) { nflCaught = ex.Message.StartsWith("RECEIPT_NFL_MISMATCH"); }
            Check(nflCaught && File.ReadAllBytes(nfZ).SequenceEqual(fBytes) && forgedW.Count == fwBefore, "nfl: the same forgery is refused because NFL's last witnessed receipt is gone from the file; nothing written");
            bool unreachableOk = true; try { IntegrationReceipts.AppendWitnessed(nfZ, forgedW, h => forgedW.Add(h), new IntegrationReceipts.NflView(false, 0, IntegrationReceipts.Genesis, "down"), "recognition", "soteriavault", "consumer", null, null, null, wNone, "ok", null); } catch { unreachableOk = false; }
            Check(unreachableOk, "nfl: NFL unreachable never blocks: the local witness alone decides (documented limit)");
            File.WriteAllBytes(nfZ, nfGood); IntegrationReceipts.ForgetVerified();

            // a receipt file the browser has no record of: never trusted blindly
            IntegrationReceipts.ForgetVerified();
            string UFresh(string name) { var f = Path.Combine(wDir, name, "receipts.ndjson"); IntegrationReceipts.Append(f, "recognition", "soteriavault", "consumer", null, null, null, wNone, "ok", null); IntegrationReceipts.Append(f, "recognition", "soteriavault", "consumer", null, null, null, wNone, "failed", "T"); return f; }
            var u1 = UFresh("u1"); var u1All = IntegrationReceipts.VerifyAll(u1); var u1w = new List<string>();
            Check(IntegrationReceipts.CheckWitness(u1, u1w) == IntegrationReceipts.WitnessState.Unanchored, "unrecorded: a file with receipts and no record is 'unanchored'");
            var ua1 = IntegrationReceipts.AppendWitnessed(u1, u1w, h => u1w.Add(h), Nfl(2, u1All[^1]), "recognition", "soteriavault", "consumer", null, null, null, wNone, "ok", null);
            Check(ua1.Item2 != null && ua1.Item2.Contains("on NFL's witness") && u1w.SequenceEqual(new[] { u1All[^1], ua1.Item1 }) && IntegrationReceipts.VerifyAll(u1).Count == 3, "unrecorded: NFL witnessed every receipt in it -> anchored on NFL's word (reported) and continued");
            foreach (var (label, view) in new (string, IntegrationReceipts.NflView?)[] { ("NFL not set up", null), ("NFL witnessed nothing of this integration", Nfl(0, IntegrationReceipts.Genesis)) })
            {
                var uf = UFresh("u-" + label.Length); var ub = File.ReadAllBytes(uf); var uw = new List<string>();
                var ra = IntegrationReceipts.AppendWitnessed(uf, uw, h => uw.Add(h), view, "recognition", "soteriavault", "consumer", null, null, null, wNone, "ok", null);
                var aside = Directory.GetFiles(Path.GetDirectoryName(uf)!, "receipts.unwitnessed-*.ndjson");
                Check(ra.Item2 != null && ra.Item2.Contains("set aside 2") && aside.Length == 1 && File.ReadAllBytes(aside[0]).SequenceEqual(ub) && IntegrationReceipts.Verify(uf).Count == 1 && uw.SequenceEqual(new[] { ra.Item1 }),
                      "unrecorded, " + label + ": the old file is set aside unchanged (never trusted, never deleted) and a new chain starts");
            }
            foreach (var (label, view) in new (string, IntegrationReceipts.NflView)[] { ("NFL could not be asked", new(false, 0, IntegrationReceipts.Genesis, "down")), ("receipts after NFL's last one", Nfl(1, "")) })
            {
                var uf = UFresh("v-" + label.Length); var ub = File.ReadAllBytes(uf); var uw = new List<string>();
                var v = view.Head == "" ? Nfl(1, IntegrationReceipts.VerifyAll(uf)[0]) : view;
                bool refusedU = false; try { IntegrationReceipts.AppendWitnessed(uf, uw, h => uw.Add(h), v, "recognition", "soteriavault", "consumer", null, null, null, wNone, "ok", null); } catch (IntegrationReceiptException ex) { refusedU = ex.Message.StartsWith("RECEIPT_WITNESS_UNANCHORED"); }
                Check(refusedU && File.ReadAllBytes(uf).SequenceEqual(ub) && uw.Count == 0 && Directory.GetFiles(Path.GetDirectoryName(uf)!).Length == 1, "unrecorded, " + label + ": refused, nothing written, nothing moved");
            }

            // NFL's answers, exactly as nfl_cli_v1.ps1 prints them
            var hh = new string('a', 64);
            var headOut = "INTEGRATION=recognition>soteriavault\r\nWITNESSED_COUNT=3\r\nHEAD=" + hh + "\r\nLAST_SEQ=41\r\nNFL_RECEIPT_HEAD_OK\r\n";
            Check(IntegrationReceipts.ParseNflHead(headOut, "recognition>soteriavault") == new IntegrationReceipts.NflView(true, 3, hh, ""), "nfl answer: receipt-head is read (CRLF or LF)");
            Check(IntegrationReceipts.ParseNflHead(headOut.Replace("NFL_RECEIPT_HEAD_OK", ""), "recognition>soteriavault") == null && IntegrationReceipts.ParseNflHead(headOut, "other>x") == null &&
                  IntegrationReceipts.ParseNflHead(headOut.Replace("COUNT=3", "COUNT=0"), "recognition>soteriavault") == null && IntegrationReceipts.ParseNflHead(headOut.Replace(hh, "zz"), "recognition>soteriavault") == null &&
                  IntegrationReceipts.ParseNflHead(headOut.Replace("COUNT=3", "COUNT=-3"), "recognition>soteriavault") == null && IntegrationReceipts.ParseNflHead(null, "recognition>soteriavault") == null,
                  "nfl answer: incomplete, another integration's, inconsistent (count vs zero head), bad hash, negative count or no answer -> not trusted");
            var wOut = "WITNESSED\nRECEIPT_SHA256=" + hh + "\nINTEGRATION=recognition>soteriavault\nSEQ=7\nTIMESTAMP=x\nWITNESSED_COUNT=1\nREFUSED_COUNT=0\nNFL_RECEIPT_WITNESS_OK\n";
            Check(IntegrationReceipts.ParseNflWitness(wOut, hh) == "WITNESSED" && IntegrationReceipts.ParseNflWitness(wOut.Replace("WITNESSED\n", "ALREADY_WITNESSED\n"), hh) == "ALREADY_WITNESSED" &&
                  IntegrationReceipts.ParseNflWitness("REFUSED\nRECEIPT_ERROR=RECEIPT_UNATTRIBUTED\nRECEIPT_SHA256=" + hh + "\nNFL_CLI_FAIL:RECEIPT_REFUSED:1\n", hh) == "REFUSED:RECEIPT_UNATTRIBUTED" &&
                  IntegrationReceipts.ParseNflWitness(wOut, new string('b', 64)) == null && IntegrationReceipts.ParseNflWitness(wOut.Replace("NFL_RECEIPT_WITNESS_OK", ""), hh) == null,
                  "nfl answer: witness-receipt WITNESSED / ALREADY_WITNESSED / REFUSED:<token>; another receipt's answer or no OK line -> not confirmed");

            // finding NFL: an explicit folder, else the dev-root layout above Recognition; never a fixed drive path
            var lay = Path.Combine(wDir, "Constellation"); var recDir = Path.Combine(lay, "_Venture-Systems", "recognition"); var nflDir = Path.Combine(lay, "_Covenant-systems", "nfl");
            Directory.CreateDirectory(recDir); Directory.CreateDirectory(Path.Combine(nflDir, "scripts")); File.WriteAllText(Path.Combine(nflDir, "scripts", "nfl_cli_v1.ps1"), "#");
            // only NFL folders inside this test's sandbox count: the sandbox may itself sit inside a real dev root (UPDATE_ALL keeps temp
            // files in the repository), and the real NFL found above it must not decide this test (seen on Windows, 2026-10-22)
            var sandbox = Path.GetFullPath(wDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            bool IsNfl(string d) => Path.GetFullPath(d).StartsWith(sandbox, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(d, "scripts", "nfl_cli_v1.ps1"));
            Check(IntegrationReceipts.FindNfl(null, recDir, IsNfl) == nflDir && IntegrationReceipts.FindNfl(nflDir, "/", IsNfl) == nflDir && IntegrationReceipts.FindNfl(Path.Combine(wDir, "nope"), recDir, IsNfl) == null && IntegrationReceipts.FindNfl(null, Path.Combine(wDir, "nfl"), IsNfl) == null,
                  "nfl location: the dev-root layout is found by walking up; an explicit folder wins (and a wrong one is not silently replaced); nothing found -> not set up");

            // the witness entries themselves
            Check(IntegrationReceipts.Witnessed(new[] { "integration.receipt|recognition>soteriavault|" + new string('a', 64), "integration.receipt|recognition>other|" + new string('b', 64), "integration.receipt|recognition>soteriavault|nothex", "soteria.check", "integration.receipt|recognition>soteriavault|" + new string('c', 64) }, "recognition", "soteriavault")
                  .SequenceEqual(new[] { new string('a', 64), new string('c', 64) }), "witness: entries of other integrations, malformed hashes and other actions are ignored");
            bool badEntry = false; try { IntegrationReceipts.WitnessEntry("recognition", "soteriavault", "xyz"); } catch (IntegrationReceiptException) { badEntry = true; }
            Check(badEntry, "witness: only a real SHA-256 can be witnessed");
        }
        finally { try { Directory.Delete(wDir, true); } catch { } IntegrationReceipts.ForgetVerified(); }
    }

    string? irRoot = null;
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent) if (File.Exists(Path.Combine(d.FullName, "browser", "MainWindow.Soteria.cs"))) { irRoot = d.FullName; break; }
    if (irRoot != null)
    {
        var sv = File.ReadAllText(Path.Combine(irRoot, "browser", "MainWindow.Soteria.cs")) + File.ReadAllText(Path.Combine(irRoot, "browser", "MainWindow.IntegrationReceipts.cs"));
        var svOnly = File.ReadAllText(Path.Combine(irRoot, "browser", "MainWindow.Soteria.cs"));
        Check(!svOnly.Contains("File.Move") && !svOnly.Contains("File.Write") && !svOnly.Contains("IntegrationReceipts.Append") && svOnly.Contains("if (record) WriteSoteriaReceipt(st);"), "receipt glue: the SoteriaVault link file stays read-only; receipts are written by MainWindow.IntegrationReceipts.cs");
        var schemaPath = Path.Combine(irRoot, ".constellation", "constellation.integration.receipt.v1.json");
        Check(sv.Contains("if (record) WriteSoteriaReceipt(st);") && sv.Contains("IntegrationReceipts.AppendWitnessed(SoteriaReceiptPath, witnessed,") && sv.Contains("_actions.Append(IntegrationReceipts.WitnessEntry(\"recognition\", \"soteriavault\", hash)), nfl,") &&
              sv.Contains("RunNflAsync(nflRoot, \"receipt-head\", \"-Integration\", integration)") && sv.Contains("RunNflAsync(nflRoot, \"witness-receipt\", \"-ReceiptLine\", line)") && sv.Contains("psi.ArgumentList.Add(a)") && !sv.Contains("UseShellExecute = true"),
              "receipt glue: every SoteriaVault link check writes a recognition > soteriavault receipt, witnessed in the encrypted action ledger");
        Check(sv.Contains("Path.Combine(SoteriaReceiptDir, \"receipts.ndjson\")") && sv.Contains("\"recognition__soteriavault\""), "receipt glue: the file is where Constellation's Witness Receipts button (NFL) looks: proofs/integration_receipts/recognition__soteriavault/receipts.ndjson");
        if (File.Exists(schemaPath))
        {
            var sch = File.ReadAllText(schemaPath);
            var order = new[] { "schema", "consumer", "provider", "emitted_by", "contract", "request_sha256", "response_sha256", "carried", "outcome", "error_token", "prev_sha256" };
            Check(sch.Contains("\"required\": [\"" + string.Join("\", \"", order) + "\"]") || sch.Contains("\"required\":[\"" + string.Join("\",\"", order) + "\"]"), "receipt: the key order matches the published schema copy");
        }
    }
}

// ==== Firefox themes and animated header pictures (FirefoxThemes.cs, AnimatedImage.cs) ====
Console.WriteLine();
Console.WriteLine("== Firefox themes ==");
{
    // colours as Firefox writes them
    string? C(string css) => CssColor.Parse(css) is { } c ? c.Hex + "/" + Math.Round(c.Alpha, 2).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    Check(C("#0b1a2c") == "#0b1a2c/1" && C("#FFF") == "#ffffff/1" && C("#fff8") == "#ffffff/0.53" && C("#11223380") == "#112233/0.5" && C("  #ABCDEF ") == "#abcdef/1", "css colour: #rgb, #rgba, #rrggbb, #rrggbbaa, case and spaces");
    Check(C("rgb(1, 2, 3)") == "#010203/1" && C("rgba(0,0,0,0.4)") == "#000000/0.4" && C("rgb(255 0 0 / 50%)") == "#ff0000/0.5" && C("rgb(100%,0%,50%)") == "#ff0080/1" && C("rgba(300,-5,10,2)") == "#ff000a/1", "css colour: rgb()/rgba(), comma and space syntax, percentages, clamping");
    Check(C("hsl(0, 100%, 50%)") == "#ff0000/1" && C("hsl(120deg 100% 25%)") == "#008000/1" && C("hsla(240,100%,50%,0.3)") == "#0000ff/0.3", "css colour: hsl()/hsla()");
    Check(C("white") == "#ffffff/1" && C("Black") == "#000000/1" && C("transparent") == "#000000/0" && C("rebeccapurple") == "#663399/1", "css colour: named colours and transparent");
    Check(new[] { "", "#12", "#12345", "#ggg", "rgb(1,2)", "rgb(a,b,c)", "url(x)", "expression(alert(1))", "notacolour", "rgb(1,2,3", "hsl(10,20,30)", new string('#', 80), "rgb(NaN,1,1)" }.All(x => CssColor.Parse(x) == null), "css colour: anything else is refused, never guessed");
    using (var jd = JsonDocument.Parse("[[10,20,30],[10,20,30,0.5],[1,2],[256,0,0],[1,2,3,4],\"#010203\"]"))
    {
        var a = jd.RootElement.EnumerateArray().Select(e => CssColor.Parse(e)).ToList();
        Check(a[0] == ("#0a141e", 1.0) && a[1] == ("#0a141e", 0.5) && a[2] == null && a[3] == null && a[4] == null && a[5] == ("#010203", 1.0), "css colour: [r,g,b(,a)] arrays as well as strings");
    }

    // a typical animated theme from addons.mozilla.org
    var ffManifest = "{\"manifest_version\":2,\"name\":\"Nyan Cat (Animated)\",\"version\":\"1.0\",\"browser_specific_settings\":{\"gecko\":{\"id\":\"nyan@x\"}}," +
        "\"theme\":{\"images\":{\"theme_frame\":\"images/nyan.png\",\"additional_backgrounds\":[\"img/a.png\",\"img/b.gif\"]}," +
        "\"properties\":{\"additional_backgrounds_alignment\":[\"left top\",\"center bottom\"],\"additional_backgrounds_tiling\":[\"repeat-x\",\"no-repeat\"]}," +
        "\"colors\":{\"frame\":\"#00336a\",\"tab_background_text\":\"#fff\",\"toolbar\":\"rgba(0,0,0,0.4)\",\"toolbar_text\":\"white\",\"tab_line\":\"#ff00ff\",\"ntp_background\":\"#101820\"}}}";
    var ff = ThemePacks.FromChromeTheme(ffManifest);
    Check(ff != null && ff.Name == "Nyan Cat (Animated)" && ff.Header != null, "firefox theme: recognised through the same reader as Chrome themes, with a header");
    if (ff?.Header != null)
    {
        Check(ff.Bar == Rgb.Mix("#00336a", "#000000", 0.4) && ff.Text == "#ffffff" && ff.Accent == "#ff00ff" && ff.StartBg == "#101820" && ff.Dark, "firefox theme: bar = toolbar over frame by its alpha; text, accent (tab_line) and start colour mapped");
        Check(ff.Header.FrameColor == "#00336a" && ff.Header.ToolbarOpacity == 40, "firefox theme: frame colour under the picture; toolbar drawn at the theme's own 40 % over it");
        var L = ff.Header.Layers;
        Check(L.Count == 3 && L[0] == new HeaderLayer("images/nyan.png", "right", "top", "no-repeat") && L[1] == new HeaderLayer("img/b.gif", "center", "bottom", "no-repeat") && L[2] == new HeaderLayer("img/a.png", "left", "top", "repeat-x"),
              "firefox theme: theme_frame at the bottom (right top), additional backgrounds in front, the first listed on top, with their alignment and tiling");
    }
    var legacy = ThemePacks.FromChromeTheme("{\"theme\":{\"images\":{\"headerURL\":\"h.jpg\"},\"colors\":{\"accentcolor\":\"#ffffff\",\"textcolor\":\"#000\"}}}");
    Check(legacy != null && !legacy.Dark && legacy.Text == "#000000" && legacy.Header?.Layers.Single().Path == "h.jpg" && legacy.Header.ToolbarOpacity == 60, "firefox theme: the old headerURL / accentcolor / textcolor names work (no toolbar colour: drawn at 60 %)");
    var chromeOnly = ThemePacks.FromChromeTheme("{\"name\":\"C\",\"theme\":{\"colors\":{\"frame\":[10,20,30],\"toolbar\":[40,50,60]},\"images\":{\"theme_ntp_background\":\"images/ntp.png\"}}}");
    Check(chromeOnly != null && chromeOnly.Header == null && chromeOnly.Bar == "#28323c" && chromeOnly.NtpImage == "images/ntp.png", "chrome theme: still read the Chrome way, no header");
    var evil = ThemePacks.FromChromeTheme("{\"theme\":{\"images\":{\"theme_frame\":\"../../secret.png\",\"additional_backgrounds\":[\"C:/x.png\",\"https://evil.example/a.png\",\"../p.svg\",\"/abs.png\",\"x.webp\",\"ok.png\",\"images/bg-005.svg\"]},\"colors\":{\"frame\":\"#000\"}}}");
    Check(evil?.Header?.Layers.Select(l => l.Path).SequenceEqual(new[] { "images/bg-005.svg", "ok.png" }) == true, "firefox theme: pictures outside the package, absolute, remote or in other formats are dropped; SVG patterns inside it are kept");
    var many = ThemePacks.FromChromeTheme("{\"theme\":{\"images\":{\"theme_frame\":\"f.png\",\"additional_backgrounds\":[" + string.Join(",", Enumerable.Range(0, 15).Select(i => "\"b" + i + ".png\"")) + "]},\"colors\":{\"frame\":\"#000\"}}}");
    Check(many?.Header?.Layers.Count == FirefoxThemes.MaxLayers && many.Header.Layers[0].Path == "f.png" && many.Header.Layers[^1].Path == "b0.png", "firefox theme: at most " + FirefoxThemes.MaxLayers + " layers, keeping the frame picture and the top-most ones");
    Check(ThemePacks.FromChromeTheme("{\"name\":\"__MSG_extName__\",\"theme\":{\"colors\":{\"frame\":\"red\"}}}")?.Name == "Firefox theme" && ThemePacks.FromChromeTheme("{\"theme\":{\"colors\":{\"frame\":\"bogus\"}}}") == null && ThemePacks.FromChromeTheme("{\"theme\":{}}") == null, "firefox theme: a localised name falls back; no usable colour or picture is not a theme");
    var low = ThemePacks.FromChromeTheme("{\"theme\":{\"colors\":{\"frame\":\"#202020\",\"toolbar_text\":\"#222222\",\"tab_line\":\"#212121\"}}}");
    Check(low != null && low.Text == "#e6e8ee" && low.Accent == "#5b8def", "firefox theme: unreadable text and an invisible accent are replaced");
    Check(ThemeLibrary.FromPackage(ffManifest, null, out var fpe)?.Header?.Layers.Count == 3 && fpe == null, "firefox theme: theme packages (Customize, Extensions) carry the header through");
    Check(FirefoxThemes.Alignment("left top") == ("left", "top") && FirefoxThemes.Alignment("center bottom") == ("center", "bottom") && FirefoxThemes.Alignment("right") == ("right", "center") &&
          FirefoxThemes.Alignment("top") == ("center", "top") && FirefoxThemes.Alignment("center") == ("center", "center") && FirefoxThemes.Alignment("10px 20px") == ("right", "top") && FirefoxThemes.Alignment(null) == ("right", "top"),
          "firefox theme: background-position keywords (anything else falls back to right top)");
    Check(FirefoxThemes.Tiling("repeat-x") == "repeat-x" && FirefoxThemes.Tiling("REPEAT") == "repeat" && FirefoxThemes.Tiling("space") == "no-repeat", "firefox theme: tiling values");

    // SVG patterns from Firefox's theme maker (this one is in "Terminal Deluxe" and "orange and black" on addons.mozilla.org)
    var amoSvg = "<svg width=\"16\" height=\"16\" xmlns=\"http://www.w3.org/2000/svg\"><path d=\"M0 0h1v16H0zm1 15h15v1H1z\" fill=\"#FFF\" fill-rule=\"evenodd\" fill-opacity=\".15\"/></svg>";
    var sv = SvgLite.Parse(amoSvg);
    Check(sv != null && sv.Width == 16 && sv.Height == 16 && sv.Paths.Count == 1 && sv.Paths[0] == new SvgPath("M0 0h1v16H0zm1 15h15v1H1z", "#ffffff", 15, true), "svg pattern: the theme maker's tile is read (size, path, fill, opacity, even-odd)");
    var svr = SvgLite.Parse("<svg viewBox=\"0 0 8 4\" xmlns=\"http://www.w3.org/2000/svg\"><g><rect x=\"1\" y=\"1\" width=\"2\" height=\"2\" fill=\"rgba(255,0,0,0.5)\" opacity=\"0.5\"/><path d=\"M0 0h1\" fill=\"none\"/></g></svg>");
    Check(svr != null && svr.Width == 8 && svr.Height == 4 && svr.Paths.Single() == new SvgPath("M1 1h2v2h-2z", "#ff0000", 25, false), "svg pattern: viewBox size, <rect> as a path, opacities multiplied, fill none skipped, plain <g> allowed");
    var hostileSvg = new[]
    {
        "<svg width=\"16\" height=\"16\"><script>alert(1)</script><path d=\"M0 0h1\"/></svg>",
        "<?xml version=\"1.0\"?><!DOCTYPE svg [<!ENTITY x SYSTEM \"file:///c:/windows/win.ini\">]><svg width=\"16\" height=\"16\"><path d=\"M0 0h1\" fill=\"&x;\"/></svg>",
        "<svg width=\"16\" height=\"16\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"><path d=\"M0 0h1\" xlink:href=\"http://evil.example/\"/></svg>",
        "<svg width=\"16\" height=\"16\"><image href=\"http://evil.example/x.png\" width=\"1\" height=\"1\"/></svg>",
        "<svg width=\"16\" height=\"16\"><style>path{fill:url(http://evil.example)}</style><path d=\"M0 0h1\"/></svg>",
        "<svg width=\"16\" height=\"16\"><foreignObject><div/></foreignObject></svg>",
        "<svg width=\"16\" height=\"16\"><g transform=\"scale(1000)\"><path d=\"M0 0h1\"/></g></svg>",
        "<svg width=\"16\" height=\"16\"><path d=\"M0 0h1\" onclick=\"x()\"/></svg>",
        "<svg width=\"16\" height=\"16\"><path d=\"M0 0 url(x)\"/></svg>",
        "<svg width=\"16\" height=\"16\"><path d=\"M0 0h1\" fill=\"url(#g)\"/></svg>",
        "<svg width=\"5000\" height=\"16\"><path d=\"M0 0h1\"/></svg>",
        "<svg width=\"16\"><path d=\"M0 0h1\"/></svg>",
        "<html><path d=\"M0 0h1\"/></html>",
        "<svg width=\"16\" height=\"16\"><path d=\"M0 0h1\"/>text</svg>",
        "<svg width=\"16\" height=\"16\"/>",
        "not xml at all <",
    };
    Check(hostileSvg.All(x => SvgLite.Parse(x) == null), "svg pattern: scripts, DTDs/entities, links, images, styles, foreign objects, transforms, events, url() fills, oversized, sizeless, other roots, text and empty files are all refused");
    Check(SvgLite.Parse("<svg width=\"16\" height=\"16\">" + string.Concat(Enumerable.Repeat("<path d=\"M0 0h1\"/>", 65)) + "</svg>") == null && SvgLite.Parse("<svg width=\"16\" height=\"16\"><path d=\"" + new string('1', 9000) + "\"/></svg>") == null, "svg pattern: too many paths or too much path data is refused");
    var amoTheme = ThemePacks.FromChromeTheme("{\"name\":\"Terminal Deluxe\",\"theme\":{\"images\":{\"additional_backgrounds\":[\"images/bg-005-f5f3cf677b1f132c75ddc7bd319dd1fc.svg\"]},\"properties\":{\"additional_backgrounds_alignment\":[\"top\"],\"additional_backgrounds_tiling\":[\"repeat\"]},\"colors\":{\"toolbar\":\"rgb(74, 40, 120)\",\"toolbar_field\":\"rgb(0, 0, 0)\",\"toolbar_field_text\":\"rgb(0, 255, 0)\",\"tab_line\":\"rgb(0, 255, 0)\",\"bookmark_text\":\"rgb(0, 255, 0)\",\"frame\":\"rgb(74, 40, 120)\",\"tab_background_text\":\"rgb(0, 255, 0)\"}}}");
    Check(amoTheme != null && amoTheme.Bar == "#4a2878" && amoTheme.Text == "#00ff00" && amoTheme.Accent == "#00ff00" && amoTheme.Header?.Layers.Single() == new HeaderLayer("images/bg-005-f5f3cf677b1f132c75ddc7bd319dd1fc.svg", "center", "top", "repeat") && amoTheme.Header.ToolbarOpacity == 75,
          "firefox theme: Terminal Deluxe (real manifest) - purple bar, green text and accent, the grid pattern tiled; an opaque toolbar is still drawn at 75 % over it");
    var laser = ThemePacks.FromChromeTheme("{\"name\":\"Laser\",\"theme\":{\"images\":{\"theme_frame\":\"Laser Chamber Black-Green.jpg\"},\"colors\":{\"frame\":\"rgb(0,0,0,1)\",\"tab_background_text\":\"#CDFFF4\",\"toolbar\":\"rgb(0, 25, 36, 0)\",\"tab_selected\":\"#111111\",\"bookmark_text\":\"#7E7E7E\"}}}");
    Check(laser != null && laser.Bar == "#000000" && laser.Header?.ToolbarOpacity == 0 && laser.Header.Layers.Single().Path == "Laser Chamber Black-Green.jpg", "firefox theme: Laser Chamber (real manifest) - rgb() with four values, a fully see-through toolbar, a file name with spaces");

    // the saved header state
    var hs = new ThemeHeader("#00336a", 40, new[] { new HeaderLayer(new string('a', 64) + ".png", "right", "top", "no-repeat"), new HeaderLayer(new string('b', 64) + ".gif", "left", "bottom", "repeat-x") });
    var hj = FirefoxThemes.StateToJson(hs, false);
    Check(FirefoxThemes.StateFromJson(hj, out var anim) == hs && !anim && FirefoxThemes.StateToJson(FirefoxThemes.StateFromJson(hj)!, false) == hj && hj.EndsWith("]}\n") && !hj.Contains('.' + "5"), "header state: round-trips byte for byte, integers only");
    Check(FirefoxThemes.StateFromJson(hj.Replace(new string('a', 64) + ".png", "../x.png")) == null && FirefoxThemes.StateFromJson(hj.Replace("\"toolbar_opacity\":40", "\"toolbar_opacity\":40.5")) == null &&
          FirefoxThemes.StateFromJson(hj.Replace("\"right\"", "\"middle\"")) == null && FirefoxThemes.StateFromJson(hj.Replace("\"animate\":false,", "")) == null && FirefoxThemes.StateFromJson(hj.Replace("#00336a", "red")) == null &&
          FirefoxThemes.StateFromJson(hj.Replace(".gif", ".webp")) == null && FirefoxThemes.StateFromJson(hj.Replace(".gif", ".svg")) != null && FirefoxThemes.StateFromJson("{nope") == null && FirefoxThemes.StateFromJson(hj.Replace("theme_header.v1", "theme_header.v2")) == null,
          "header state: a path, a float, an unknown alignment, a missing field, a colour name, an unknown picture type, garbage or another schema are refused (SVG patterns are allowed)");
}

Console.WriteLine();
Console.WriteLine("== animated header pictures ==");
{
    string? fxDir = null;
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent) if (File.Exists(Path.Combine(d.FullName, "browser.tests", "fixtures", "header_anim.apng"))) { fxDir = Path.Combine(d.FullName, "browser.tests", "fixtures"); break; }
    Check(fxDir != null, "animation fixtures found (made with Pillow; reference frames in header_anim.expected.json)");
    if (fxDir != null)
    {
        // test-only decoders, so the composed frames can be compared with Pillow's
        static byte[] PngRgba(byte[] png, out int w, out int h)
        {
            int p = 8; w = h = 0; var idat = new MemoryStream();
            while (p < png.Length)
            {
                int len = (png[p] << 24) | (png[p + 1] << 16) | (png[p + 2] << 8) | png[p + 3]; var t = Encoding.ASCII.GetString(png, p + 4, 4);
                if (t == "IHDR") { w = (png[p + 8] << 24) | (png[p + 9] << 16) | (png[p + 10] << 8) | png[p + 11]; h = (png[p + 12] << 24) | (png[p + 13] << 16) | (png[p + 14] << 8) | png[p + 15]; if (png[p + 16] != 8 || png[p + 17] != 6) throw new Exception("test decoder: RGBA8 only"); }
                if (t == "IDAT") idat.Write(png, p + 8, len);
                p += 12 + len;
            }
            idat.Position = 0; var raw = new MemoryStream(); using (var z = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionMode.Decompress)) z.CopyTo(raw);
            var r = raw.ToArray(); int stride = w * 4; var o = new byte[h * stride];
            for (int y = 0; y < h; y++)
            {
                int ft = r[y * (stride + 1)];
                for (int x = 0; x < stride; x++)
                {
                    int a = x >= 4 ? o[y * stride + x - 4] : 0, b = y > 0 ? o[(y - 1) * stride + x] : 0, c = x >= 4 && y > 0 ? o[(y - 1) * stride + x - 4] : 0;
                    int v = r[y * (stride + 1) + 1 + x];
                    int pr = ft switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, _ => Paeth(a, b, c) };
                    o[y * stride + x] = (byte)(v + pr);
                }
            }
            return o;
            static int Paeth(int a, int b, int c) { int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c); return pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
        }
        static List<byte[]> GifFramesRgba(byte[] g)
        {
            var frames = new List<byte[]>(); int pos = 13; byte[]? gct = null;
            if ((g[10] & 0x80) != 0) { int n = 3 * (1 << ((g[10] & 7) + 1)); gct = g.Skip(13).Take(n).ToArray(); pos += n; }
            int trans = -1;
            while (pos < g.Length)
            {
                var tag = g[pos++];
                if (tag == 0x3B) break;
                if (tag == 0x21) { var label = g[pos++]; if (label == 0xF9) trans = (g[pos + 1] & 1) != 0 ? g[pos + 4] : -1; while (g[pos] != 0) pos += g[pos] + 1; pos++; continue; }
                int w = g[pos + 4] | g[pos + 5] << 8, h = g[pos + 6] | g[pos + 7] << 8, pk = g[pos + 8]; pos += 9;
                var pal = gct; if ((pk & 0x80) != 0) { int n = 3 * (1 << ((pk & 7) + 1)); pal = g.Skip(pos).Take(n).ToArray(); pos += n; }
                int minCode = g[pos++]; var data = new List<byte>(); while (g[pos] != 0) { data.AddRange(g.Skip(pos + 1).Take(g[pos])); pos += g[pos] + 1; } pos++;
                // LZW
                var idx = new List<int>(); int clear = 1 << minCode, eoi = clear + 1, size = minCode + 1, next = eoi + 1; var dict = new List<int[]>();
                void Reset() { dict.Clear(); for (int i = 0; i < clear; i++) dict.Add(new[] { i }); dict.Add(Array.Empty<int>()); dict.Add(Array.Empty<int>()); size = minCode + 1; next = eoi + 1; }
                Reset(); int bitPos = 0; int[]? prev = null;
                while (bitPos + size <= data.Count * 8)
                {
                    int code = 0; for (int i = 0; i < size; i++, bitPos++) if ((data[bitPos >> 3] >> (bitPos & 7) & 1) != 0) code |= 1 << i;
                    if (code == clear) { Reset(); prev = null; continue; }
                    if (code == eoi) break;
                    int[] entry = code < dict.Count ? dict[code] : prev!.Concat(new[] { prev![0] }).ToArray();
                    idx.AddRange(entry);
                    if (prev != null && dict.Count < 4096) { dict.Add(prev.Concat(new[] { entry[0] }).ToArray()); if (dict.Count == (1 << size) && size < 12) size++; }
                    prev = entry;
                }
                var o = new byte[w * h * 4];
                for (int i = 0; i < w * h && i < idx.Count; i++) { int k = idx[i]; if (k == trans) continue; o[i * 4] = pal![k * 3]; o[i * 4 + 1] = pal[k * 3 + 1]; o[i * 4 + 2] = pal[k * 3 + 2]; o[i * 4 + 3] = 255; }
                frames.Add(o); trans = -1;
            }
            return frames;
        }
        static byte[] Swap(byte[] x) { var y = (byte[])x.Clone(); for (int i = 0; i < y.Length; i += 4) (y[i], y[i + 2]) = (y[i + 2], y[i]); return y; }   // RGBA <-> BGRA
        static int MaxDiff(byte[] mineBgra, byte[] refRgba)
        {
            var m = Swap(mineBgra); int worst = 0;
            for (int i = 0; i < m.Length; i += 4)
            {
                if (m[i + 3] == 0 && refRgba[i + 3] == 0) continue;   // fully transparent: colour does not matter
                for (int c = 0; c < 4; c++) worst = Math.Max(worst, Math.Abs(m[i + c] - refRgba[i + c]));
            }
            return worst;
        }
        using var expDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(fxDir, "header_anim.expected.json")));
        var refA = expDoc.RootElement.GetProperty("apng_frames_rgba").EnumerateArray().Select(e => Convert.FromBase64String(e.GetString()!)).ToList();
        var refG = expDoc.RootElement.GetProperty("gif_frames_rgba").EnumerateArray().Select(e => Convert.FromBase64String(e.GetString()!)).ToList();

        var apng = File.ReadAllBytes(Path.Combine(fxDir, "header_anim.apng"));
        var ai = AnimatedImage.Probe(apng);
        Check(ai != null && ai.Kind == "apng" && ai.Width == 24 && ai.Height == 10 && ai.Loops == 0 && ai.Frames.Count == 3, "apng: 3 frames, 24x10, loops forever");
        if (ai != null)
        {
            Check(ai.Frames.Select(f => f.DelayMs).SequenceEqual(new[] { 100, 50, 100 }) && ai.Frames[1].BlendOver && ai.Frames[1].Dispose == AnimDispose.Background && ai.Frames[2].Dispose == AnimDispose.Previous, "apng: delays (a 0 ms frame shown as 100 ms like browsers), blend and dispose read");
            Check(ai.Frames[2].X == 15 && ai.Frames[2].Y == 1 && ai.Frames[2].W == 6 && ai.Frames[2].H == 8, "apng: a sub-rectangle frame keeps its place and size");
            var px = ai.Frames.Select(f => { var rgba = PngRgba(f.Png!, out var w, out var h); return (w == f.W && h == f.H) ? Swap(rgba) : null; }).ToList();
            Check(px.All(p => p != null), "apng: every rebuilt frame is a standalone PNG of the frame's own size (decoded by an independent decoder)");
            if (px.All(p => p != null))
            {
                var comp = AnimatedImage.Compose(24, 10, ai.Frames, i => px[i]!, 10);
                var diffs = comp.Select((c, i) => MaxDiff(c, refA[i])).ToList();
                Check(diffs.All(d => d <= 1), "apng: composed frames match Pillow's rendering (largest channel difference " + diffs.Max() + ", rounding only)");
                var top4 = AnimatedImage.Compose(24, 10, ai.Frames, i => px[i]!, 4);
                Check(top4.All(f => f.Length == 24 * 4 * 4) && top4[2].SequenceEqual(comp[2].Take(24 * 4 * 4)), "apng: keeping only the top rows gives exactly those rows");
            }
            // damage
            bool Bad(byte[] x) { try { AnimatedImage.Probe(x); return false; } catch (AnimException) { return true; } }
            var flip = (byte[])apng.Clone(); flip[60] ^= 0x01;
            Check(Bad(flip), "apng: one changed byte fails the chunk CRC");
            Check(Bad(apng.Take(apng.Length - 20).ToArray()) && Bad(apng.Take(40).ToArray()), "apng: a truncated file is refused");
            int fc = -1; for (int i = 8, n = 0; i + 8 < apng.Length; i += 12 + ((apng[i] << 24) | (apng[i + 1] << 16) | (apng[i + 2] << 8) | apng[i + 3])) if (Encoding.ASCII.GetString(apng, i + 4, 4) == "fcTL" && ++n == 3) { fc = i; break; }
            var outside = (byte[])apng.Clone(); outside[fc + 8 + 15] = 30;   // x_offset of the third frame -> 30 (canvas is 24 wide)
            var crc = Apng.Crc(outside, fc + 4, 30); outside[fc + 34] = (byte)(crc >> 24); outside[fc + 35] = (byte)(crc >> 16); outside[fc + 36] = (byte)(crc >> 8); outside[fc + 37] = (byte)crc;
            Check(Bad(outside), "apng: a frame placed outside the picture is refused (even with a correct CRC)");
            using (var still = new MemoryStream())
            {
                still.Write(apng, 0, 8);
                var ihdr = apng.Skip(16).Take(13).ToArray(); Apng.WriteChunk(still, "IHDR", ihdr, 0, 13);
                Apng.WriteChunk(still, "IDAT", new byte[] { 0x78, 0x9c, 3, 0, 0, 0, 0, 1 }, 0, 8); Apng.WriteChunk(still, "IEND", Array.Empty<byte>(), 0, 0);
                Check(AnimatedImage.Probe(still.ToArray()) == null && AnimatedImage.Probe(new byte[] { 0xFF, 0xD8, 0xFF }) == null, "apng: a plain PNG (or a JPEG) is a still picture, not an animation");
            }
        }

        var gif = File.ReadAllBytes(Path.Combine(fxDir, "header_anim.gif"));
        var gi = AnimatedImage.Probe(gif);
        Check(gi != null && gi.Kind == "gif" && gi.Frames.Count == 3 && gi.Loops == 0 && gi.Frames.Select(f => f.DelayMs).SequenceEqual(new[] { 120, 70, 100 }) && gi.Frames[1].Dispose == AnimDispose.Background,
              "gif: 3 frames, loops forever, delays in 1/100 s (0 shown as 100 ms), disposal read");
        if (gi != null)
        {
            var gpx = GifFramesRgba(gif);
            Check(gpx.Count == 3 && gpx.Select((p, i) => p.Length == gi.Frames[i].W * gi.Frames[i].H * 4).All(x => x), "gif: frame places and sizes match the image data");
            if (gpx.Count == 3)
            {
                var gc = AnimatedImage.Compose(gi.Width, gi.Height, gi.Frames, i => Swap(gpx[i]), gi.Height);
                var gd = gc.Select((c, i) => MaxDiff(c, refG[i])).ToList();
                Check(gd.All(d => d == 0), "gif: composed frames match Pillow's rendering exactly (largest difference " + gd.Max() + ")");
            }
            bool GBad(byte[] x) { try { AnimatedImage.Probe(x); return false; } catch (AnimException) { return true; } }
            Check(GBad(gif.Take(30).ToArray()) && GBad(Encoding.ASCII.GetBytes("GIF89a").Concat(new byte[] { 0, 0, 0, 0, 0, 0, 0 }).ToArray()), "gif: truncated or zero-size files are refused");
        }

        // disposal "previous" restores what was there; budget
        var fr = new List<AnimFrame> { new(0, 0, 2, 1, 100, AnimDispose.None, false, null, 0), new(1, 0, 1, 1, 100, AnimDispose.Previous, false, null, 1), new(0, 0, 1, 1, 100, AnimDispose.None, true, null, 2) };
        var pix = new[] { new byte[] { 1, 1, 1, 255, 2, 2, 2, 255 }, new byte[] { 9, 9, 9, 255 }, new byte[] { 0, 0, 0, 0 } };
        var cm = AnimatedImage.Compose(2, 1, fr, i => pix[i], 1);
        Check(cm[1].SequenceEqual(new byte[] { 1, 1, 1, 255, 9, 9, 9, 255 }) && cm[2].SequenceEqual(new byte[] { 1, 1, 1, 255, 2, 2, 2, 255 }), "compose: dispose 'previous' puts back what was under the frame; a transparent 'over' frame changes nothing");
        bool CBad(Func<object> f) { try { f(); return false; } catch (AnimException) { return true; } }
        Check(CBad(() => AnimatedImage.Compose(2, 1, new[] { new AnimFrame(1, 0, 2, 1, 100, AnimDispose.None, false, null, 0) }, _ => new byte[8], 1)) && CBad(() => AnimatedImage.Compose(2, 1, fr, _ => new byte[3], 1)), "compose: a frame outside the canvas or with the wrong pixel count is refused");
        Check(AnimatedImage.Budget(3000, 200, 60) == (200, false) && AnimatedImage.Budget(1000, 600, 30) == (AnimatedImage.MaxRows, true) && AnimatedImage.Budget(500, 100, 1) == (100, false), "budget: at most " + AnimatedImage.MaxRows + " rows kept; too many frames for the memory budget (or a single frame) means still");
        Check(AnimatedImage.NormalizeDelay(0) == 100 && AnimatedImage.NormalizeDelay(10) == 100 && AnimatedImage.NormalizeDelay(20) == 20 && AnimatedImage.NormalizeDelay(int.MaxValue) == 60_000, "delays: 10 ms or less shown as 100 ms, at most a minute");
    }

    // static checks on the window glue
    string? thRoot = null;
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent) if (File.Exists(Path.Combine(d.FullName, "browser", "MainWindow.ThemeHeader.cs"))) { thRoot = d.FullName; break; }
    if (thRoot != null)
    {
        var th = File.ReadAllText(Path.Combine(thRoot, "browser", "MainWindow.ThemeHeader.cs"));
        var cz = File.ReadAllText(Path.Combine(thRoot, "browser", "MainWindow.Customize.cs"));
        var xaml = File.ReadAllText(Path.Combine(thRoot, "browser", "MainWindow.xaml"));
        Check(!th.Contains("HttpClient") && !th.Contains("WebClient") && th.Contains("SystemParameters.ClientAreaAnimation") && th.Contains("WindowState.Minimized"), "header glue: no network; still when Windows reduces animations and while minimised");
        Check(th.Contains("AnimatedImage.Budget(") && th.Contains("AnimatedImage.Compose(") && th.Contains("Task.Run(() => DecodeHeaderLayer(") && th.Contains("Frozen(BitmapSource.Create("), "header glue: frames composed off the UI thread within the memory budget, frozen");
        Check(th.Contains("FirefoxThemes.SafeHeaderPath(l.Path)") && th.Contains("!full.StartsWith(rootFull") && th.Contains("MaxHeaderFileBytes") && th.Contains("FirefoxThemes.IsStoredFileName(name)"), "header glue: only pictures inside the theme, size-capped, stored content-addressed");
        Check(xaml.Contains("x:Name=\"HeaderArt\"") && xaml.Contains("IsHitTestVisible=\"False\"") && xaml.Contains("{DynamicResource ChromeTopBg}") && xaml.Contains("{DynamicResource ChromeTabStrip}"), "header glue: the picture layer sits behind the toolbar and tabs and never takes clicks");
        Check(cz.Contains("if (p.Header != null && themeRoot != null) SetThemeHeader(p.Header, themeRoot);") && cz.Contains("else if (p.Id != \"custom\" && _header != null) SetThemeHeader(null, null);") && cz.Contains("ExtSources.ParseFirefox(link)"), "header glue: a Firefox theme sets the header, any other look clears it, Firefox Add-ons links work on Customize");
    }
}

Console.WriteLine();
Console.WriteLine($"checks passed: {pass}  failed: {fail}");
if (fail > 0) { Console.Error.WriteLine("BROWSER_GOVERNED_ACTIONS_TESTS_FAIL: " + fail); return 1; }
Console.WriteLine("SELFTEST_RECOGNITION_BROWSER_GOVERNED_ACTIONS_V1_OK");
return 0;
