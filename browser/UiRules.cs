using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;

namespace Recognition.Browser
{
    // Small pure rules behind the window's chrome (executed by browser.tests). Audit 2026-10-31, F14-F17.

    /// <summary>What the address bar's site icon says about the page (audit F15: it used to be a green lock on every page, http included).</summary>
    internal enum SiteSecurity { Internal, Secure, NotSecure, File, Other }

    internal static class SiteSecurityRules
    {
        public static SiteSecurity Of(string? url, bool isInternal)
        {
            if (isInternal) return SiteSecurity.Internal;
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return SiteSecurity.Other;
            if (u.Scheme == Uri.UriSchemeHttps) return SiteSecurity.Secure;
            if (u.Scheme == Uri.UriSchemeHttp) return SiteSecurity.NotSecure;
            if (u.Scheme == Uri.UriSchemeFile) return SiteSecurity.File;
            return SiteSecurity.Other;
        }

        /// <summary>Segoe Fluent / MDL2 glyph, colour key (ok / warn / muted) and the words for the tooltip.</summary>
        public static (string Glyph, string Tone, string Words) Look(SiteSecurity s) => s switch
        {
            SiteSecurity.Secure => ("", "ok", "Secure connection (https)"),
            SiteSecurity.NotSecure => ("", "warn", "Not secure: this page is not encrypted (http). Do not type passwords or card numbers here."),
            SiteSecurity.Internal => ("", "muted", "A Recognition page (stays on this computer)"),
            SiteSecurity.File => ("", "muted", "A file on this computer"),
            _ => ("", "muted", "Page"),
        };
    }

    /// <summary>
    /// The automatic SoteriaVault check behind the key button (audit F16). It used to ask SoteriaVault on every page load, and every ask
    /// is an exchange: one integration receipt, one action-ledger entry and two NFL processes per navigation, most of them
    /// "SoteriaVault is not running". Now: an answer for a site is reused for a minute; after a refusal or no answer the automatic check
    /// waits 30 seconds. Clicking the key button always asks (it is not throttled), and saving or moving logins clears what is known.
    /// </summary>
    internal sealed class SoteriaProbeThrottle
    {
        public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan Backoff = TimeSpan.FromSeconds(30);
        private readonly Dictionary<string, (DateTime At, bool Has)> _known = new(StringComparer.Ordinal);
        private DateTime _quietUntil = DateTime.MinValue;

        /// <summary>True: ask SoteriaVault now. False: do not; <paramref name="has"/> is the answer still known for this site, if any.</summary>
        public bool ShouldAsk(string origin, DateTime nowUtc, out bool? has)
        {
            has = null;
            if (_known.TryGetValue(origin, out var k) && nowUtc - k.At < Fresh && nowUtc >= k.At) { has = k.Has; return false; }
            if (nowUtc < _quietUntil) return false;
            return true;
        }

        public void Answered(string origin, DateTime nowUtc, bool has)
        {
            if (_known.Count > 256) _known.Clear();
            _known[origin] = (nowUtc, has);
        }

        public void NoAnswer(DateTime nowUtc) => _quietUntil = nowUtc + Backoff;

        public void Forget() { _known.Clear(); _quietUntil = DateTime.MinValue; }
    }

    internal static class JsonNum
    {
        /// <summary>A whole number from a bridge answer, or 0 when missing or not a number (audit F17: GetValue&lt;int&gt; threw and crashed the window).</summary>
        public static int Int(JsonObject o, string key)
        {
            if (!o.TryGetPropertyValue(key, out var n) || n is not JsonValue v) return 0;
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<long>(out var l)) return l > int.MaxValue ? int.MaxValue : l < int.MinValue ? int.MinValue : (int)l;
            if (v.TryGetValue<double>(out var d) && !double.IsNaN(d) && !double.IsInfinity(d)) return (int)Math.Clamp(Math.Truncate(d), int.MinValue, int.MaxValue);
            return 0;
        }
    }

    /// <summary>
    /// The last line of defence (audit F14). There was no handler for errors that escape a click or an async step, so any one of them
    /// (47 async void handlers) closed the whole browser and every tab without a word. Now such an error is written to
    /// runtime\crash_logs, the status bar says what failed, and the browser keeps running. Errors the process cannot survive are not swallowed.
    /// </summary>
    internal static class CrashLog
    {
        public static bool IsFatal(Exception ex) =>
            ex is OutOfMemoryException or StackOverflowException or AccessViolationException or InvalidProgramException or BadImageFormatException
            || (ex is AggregateException ag && ag.InnerExceptions.Count > 0 && ag.InnerExceptions[0] is { } first && IsFatal(first));

        public static string FileName(DateTime nowUtc) => "crash_" + nowUtc.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) + ".log";

        /// <summary>One log entry: when, where, the error chain and its stack. No page addresses, form values or other user data are added here.</summary>
        public static string Entry(Exception ex, string where, DateTime nowUtc)
        {
            var sb = new StringBuilder();
            sb.Append("=== ").Append(nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture)).Append("  ").Append(where).Append('\n');
            int depth = 0;
            for (var e = ex; e != null && depth < 8; e = e.InnerException, depth++)
                sb.Append(depth == 0 ? "" : "--- inner: ").Append(e.GetType().FullName).Append(": ").Append(e.Message).Append('\n').Append(e.StackTrace ?? "").Append('\n');
            return sb.ToString().Replace("\r\n", "\n");
        }

        /// <summary>The short sentence for the status bar.</summary>
        public static string StatusLine(Exception ex)
        {
            var m = (ex is AggregateException ag && ag.InnerExceptions.Count > 0 ? ag.InnerExceptions[0] : ex).Message.Replace('\r', ' ').Replace('\n', ' ');
            if (m.Length > 140) m = m.Substring(0, 140) + "...";
            return "Something went wrong, but the browser kept running: " + m + " (details in runtime\\crash_logs)";
        }
    }
}
