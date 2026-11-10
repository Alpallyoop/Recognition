using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SoteriaVault.Bridge;

namespace Recognition.Browser
{
    // SoteriaVault as Recognition's password manager: the pure rules (executed by browser.tests). The WPF glue is
    // MainWindow.SoteriaPasswords.cs; the wire protocol is SoteriaBridgeProtocol.cs (byte-identical copy of SoteriaVault's).
    //
    //  * Used only when you switched it on AND SoteriaVault's contract allows per_fill_release_to_paired_client.
    //  * Recognition never stores a password it got from SoteriaVault: it goes straight into the one matching page and is dropped.
    //  * Recognition's own vault is used only while SoteriaVault is not the password manager ("import, then defer").
    //  * Every exchange writes a constellation.integration.receipt.v1 (recognition > soteriavault); request/response hashes are
    //    taken with the password field blanked, so a receipt can never confirm a password.
    internal static class SoteriaPasswords
    {
        public const string ClientName = "Recognition";
        public const string ReceiptContract = "soteriavault.connector_contract.v1";

        /// <summary>SHA-256 of a request or response with any password blanked (for receipts).</summary>
        public static string RedactedSha(JsonObject o)
        {
            var c = (JsonObject)o.DeepClone();
            foreach (var k in new[] { "password", "passphrase" }) if (c.ContainsKey(k)) c[k] = "";
            if (c["entries"] is JsonArray a) foreach (var e in a.OfType<JsonObject>()) if (e.ContainsKey("password")) e["password"] = "";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(c.ToJsonString()))).ToLowerInvariant();
        }

        /// <summary>Receipt outcome for a bridge answer (protocol error tokens -> integration outcomes).</summary>
        public static (string Outcome, string? Error) Outcome(JsonObject res)
        {
            if (SoteriaBridgeProtocol.Bool(res, "ok")) return ("ok", null);
            var t = SoteriaBridgeProtocol.Str(res, "error");
            return t switch
            {
                "NOT_RUNNING" => ("unavailable", "SOTERIA_APP_NOT_RUNNING"),
                "LOCKED" => ("deferred", "SOTERIA_LOCKED"),
                "DENIED" => ("refused", "SOTERIA_DENIED"),
                "NOT_PAIRED" => ("refused", "SOTERIA_NOT_PAIRED"),
                "ORIGIN_MISMATCH" => ("refused", "SOTERIA_ORIGIN_MISMATCH"),
                "TIMEOUT" => ("failed", "SOTERIA_TIMEOUT"),
                "NO_MATCH" => ("refused", "SOTERIA_NO_MATCH"),
                _ => ("failed", "SOTERIA_" + new string((t.Length == 0 ? "ERROR" : t).ToUpperInvariant().Where(ch => (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_').Take(40).ToArray())),
            };
        }

        /// <summary>What to tell you about a bridge answer that was not ok.</summary>
        public static string Explain(JsonObject res) => SoteriaBridgeProtocol.Str(res, "error") switch
        {
            "NOT_RUNNING" => "SoteriaVault is not running. Start it (SoteriaVault page, Open SoteriaVault) and try again.",
            "LOCKED" => "SoteriaVault is locked. Unlock it in the SoteriaVault window, then try again.",
            "DENIED" => "You did not allow it in SoteriaVault.",
            "NOT_PAIRED" => "SoteriaVault has not allowed this copy of Recognition. Allow it in the SoteriaVault window.",
            "ORIGIN_MISMATCH" => "That login is not for this exact site, so SoteriaVault refused it.",
            "TIMEOUT" => "SoteriaVault did not answer in time (nobody allowed or denied the request).",
            "NO_MATCH" => "SoteriaVault has no such login for this site any more.",
            "BUSY" => "SoteriaVault is busy (it is waiting for another answer from you, or got too many requests this minute). Try again in a moment.",
            var t => "SoteriaVault said: " + (t.Length == 0 ? "no answer" : t),
        };

        /// <summary>The bridge request that moves Recognition's saved logins into SoteriaVault.</summary>
        public static JsonObject ImportRequest(IEnumerable<VaultEntry> entries)
        {
            var arr = new JsonArray();
            foreach (var e in entries)
                arr.Add(new JsonObject { ["origin"] = e.Origin, ["username"] = e.Username, ["password"] = e.Password, ["note"] = e.Note, ["created_utc"] = e.CreatedUtc, ["updated_utc"] = e.UpdatedUtc });
            return new JsonObject { ["op"] = "import", ["entries"] = arr };
        }

        // SoteriaVault's bridge takes at most 10000 logins per import and 1 MiB per frame; batches stay well under both.
        public const int ImportBatchEntries = 500;
        public const int ImportBatchBytes = 512 * 1024;

        /// <summary>
        /// The move split into bridge-sized batches. Each batch carries the ids of the logins in it, so Recognition removes
        /// only what SoteriaVault confirmed it received (see ImportAccounted).
        /// </summary>
        public static List<(JsonObject Request, List<string> Ids)> ImportBatches(IReadOnlyList<VaultEntry> entries)
        {
            var batches = new List<(JsonObject, List<string>)>();
            var cur = new List<VaultEntry>(); int bytes = 0;
            void Flush() { if (cur.Count == 0) return; batches.Add((ImportRequest(cur), cur.Select(e => e.Id).ToList())); cur = new List<VaultEntry>(); bytes = 0; }
            foreach (var e in entries)
            {
                var size = 160 + Encoding.UTF8.GetByteCount(e.Origin + e.Username + e.Password + e.Note + e.CreatedUtc + e.UpdatedUtc) * 6;   // worst case: every character escaped
                if (cur.Count > 0 && (cur.Count >= ImportBatchEntries || bytes + size > ImportBatchBytes)) Flush();
                cur.Add(e); bytes += size;
            }
            Flush();
            return batches;
        }

        /// <summary>
        /// Did SoteriaVault account for every login of a batch (read all of them, none invalid, each added, merged or kept
        /// for review)? Only then may Recognition delete its copies: a login SoteriaVault skipped stays here.
        /// </summary>
        public static bool ImportAccounted(JsonObject res, int sent) =>
            SoteriaBridgeProtocol.Bool(res, "ok") && sent > 0 && JsonNum.Int(res, "read") == sent && JsonNum.Int(res, "invalid") == 0
            && JsonNum.Int(res, "added") + JsonNum.Int(res, "merged") + JsonNum.Int(res, "conflicts") == sent;

        /// <summary>
        /// The SoteriaVault app to start, or null. The installed copy (SoteriaVault-Setup.exe, its own folder, where the vault
        /// then lives) comes first; the app built inside the linked folder (its Build button) is the fallback. Either has to
        /// be an absolute local path and has to exist.
        /// </summary>
        public static string? AppPath(string soteriaRoot, Func<string, bool> exists, string? installedRoot = null)
        {
            foreach (var root in new[] { installedRoot, soteriaRoot })
            {
                if (root == null || !SoteriaBridge.IsSafeRoot(root)) continue;
                var p = Path.Combine(root.Trim(), "app", "SoteriaVault.exe");
                if (exists(p)) return p;
            }
            return null;
        }

        /// <summary>A short, safe label for a login in a menu, with the SoteriaVault section it is filed under (when any),
        /// so two accounts on one site (say, a ROOTED one and a personal one) can be told apart.</summary>
        public static string MenuLabel(JsonObject e)
        {
            var u = SoteriaBridgeProtocol.Str(e, "username"); var t = SoteriaBridgeProtocol.Str(e, "title"); var sec = SoteriaBridgeProtocol.Str(e, "section");
            var s = (u.Length > 0 ? u : "(no user name)") + (t.Length > 0 && !string.Equals(t, u, StringComparison.OrdinalIgnoreCase) ? "  -  " + t : "")
                + (sec.Length > 0 ? "  [" + (sec.Length > 40 ? sec.Substring(0, 40) : sec) + "]" : "");
            s = new string(s.Where(ch => !char.IsControl(ch)).ToArray());
            return s.Length > 80 ? s.Substring(0, 80) + "..." : s;
        }
    }
}
