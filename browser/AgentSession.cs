using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Recognition.Browser
{
    // The agent session state (proposal 0012, Phase 2): the per-launch bearer token that authenticates the local transport, the set of
    // origins the user has granted this launch, a per-origin rate limiter, and the kill switch (revoke everything). Deterministic and
    // testable (the clock is passed in). Holds no secrets beyond the token, and never any page content.
    //
    // Grants are per-origin and per-launch: nothing is remembered across restarts unless the surrounding settings choose to. The token
    // is minted fresh on every launch and shown once in Settings; the transport presents it on every request and it is compared in
    // constant time. RevokeAll() is the toolbar kill switch — it drops every grant and clears the rate state at once.
    internal sealed class AgentSession
    {
        private readonly HashSet<string> _granted = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Queue<DateTime>> _hits = new(StringComparer.Ordinal);

        public string Token { get; }
        public int RateMax { get; }
        public int RateWindowSeconds { get; }

        public AgentSession(string token, int rateMax = 120, int rateWindowSeconds = 60)
        {
            Token = token; RateMax = Math.Max(1, rateMax); RateWindowSeconds = Math.Max(1, rateWindowSeconds);
        }

        // 32 random bytes as lowercase hex — a 256-bit bearer token, minted per launch.
        public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        // Constant-time comparison. Token length is fixed and not secret, so an early length check leaks nothing.
        public static bool TokenMatches(string expected, string? presented)
        {
            if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(presented)) return false;
            var a = Encoding.ASCII.GetBytes(expected);
            var b = Encoding.ASCII.GetBytes(presented);
            if (a.Length != b.Length) return false;
            return CryptographicOperations.FixedTimeEquals(a, b);
        }

        public bool AuthOk(string? presentedToken) => TokenMatches(Token, presentedToken);

        // ---- grants (per origin, per launch) ----------------------------------------------------------------------------
        // Accepts either a page URL (the usual case — the consent flow grants the page's origin) or a bare origin.
        public bool Grant(string? originOrUrl)
        {
            var o = PasswordRules.OriginOf(originOrUrl) ?? PasswordRules.NormalizeOrigin(originOrUrl);
            if (o == null) return false;
            _granted.Add(o);
            return true;
        }

        public bool IsGrantedUrl(string? url)
        {
            var o = PasswordRules.OriginOf(url);
            return o != null && _granted.Contains(o);
        }

        public bool Ungrant(string? originOrUrl)
        {
            var o = PasswordRules.OriginOf(originOrUrl) ?? PasswordRules.NormalizeOrigin(originOrUrl);
            return o != null && _granted.Remove(o);
        }

        public IReadOnlyCollection<string> GrantedOrigins => _granted;

        // The kill switch: forget every grant and all rate state immediately.
        public void RevokeAll() { _granted.Clear(); _hits.Clear(); }

        // ---- rate limiter (sliding window per key) ------------------------------------------------------------------------
        // Returns true if an action keyed by <key> (an origin, or "window" for tab/window actions) is within budget at <now>, and
        // records it. Returns false when the budget for the window is already spent.
        public bool WithinRate(string key, DateTime now)
        {
            if (!_hits.TryGetValue(key, out var q)) { q = new Queue<DateTime>(); _hits[key] = q; }
            var cutoff = now.AddSeconds(-RateWindowSeconds);
            while (q.Count > 0 && q.Peek() < cutoff) q.Dequeue();
            if (q.Count >= RateMax) return false;
            q.Enqueue(now);
            return true;
        }
    }
}
