using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Recognition.Browser
{
    // constellation.integration.receipt.v1 (Constellation proposal 0013; schema copy in .constellation\constellation.integration.receipt.v1.json).
    //
    // Every registered integration writes one receipt per exchange, including failed ones, locally first:
    //   * one object per line, keys in the schema's order, no insignificant whitespace, UTF-8, LF between lines;
    //   * receipt hash = SHA-256 over the line's bytes without the LF;
    //   * prev_sha256 = hash of this emitter's previous receipt for the same integration (64 zeros for the first);
    //   * no timestamps (the witness, NFL, records when it saw the hash).
    // Before appending, the existing file is verified; a broken chain is never extended (fail closed), and the caller reports it.
    internal sealed record IntegrationCarried(string Contract, string Sha256);

    internal sealed class IntegrationReceiptException : Exception { public IntegrationReceiptException(string m) : base(m) { } }

    internal static class IntegrationReceipts
    {
        public const string Schema = "constellation.integration.receipt.v1";
        public static readonly string Genesis = new string('0', 64);
        public static readonly string[] Outcomes = { "ok", "refused", "unavailable", "unknown", "deferred", "failed" };

        static bool ServiceId(string? s) => !string.IsNullOrEmpty(s) && IsLowerAlnum(s[0]) && s.All(c => IsLowerAlnum(c) || c == '-');
        static bool IsLowerAlnum(char c) => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
        public static bool ContractId(string? s)
        {
            if (string.IsNullOrEmpty(s) || !IsLowerAlnum(s[0]) || !s.All(c => IsLowerAlnum(c) || c is '_' or '.' or '-')) return false;
            var i = s.LastIndexOf(".v", StringComparison.Ordinal);
            return i > 0 && i + 2 < s.Length && s.Substring(i + 2).All(c => c >= '0' && c <= '9');
        }
        public static bool Sha(string? s) => s != null && s.Length == 64 && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        static bool Token(string? s) => !string.IsNullOrEmpty(s) && s[0] >= 'A' && s[0] <= 'Z' && s.All(c => (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_');

        // All values are restricted to plain ASCII by the checks above, so quoting needs no escaping.
        static string Q(string? s) => s == null ? "null" : "\"" + s + "\"";

        // The exact line bytes (without LF). Throws for anything the schema does not allow.
        public static string Line(string consumer, string provider, string emittedBy, string? contract, string? requestSha256, string? responseSha256,
                                  IReadOnlyList<IntegrationCarried> carried, string outcome, string? errorToken, string prevSha256)
        {
            if (!ServiceId(consumer) || !ServiceId(provider)) throw new IntegrationReceiptException("bad service id");
            if (emittedBy is not ("consumer" or "provider")) throw new IntegrationReceiptException("bad emitted_by");
            if (contract != null && !ContractId(contract)) throw new IntegrationReceiptException("bad contract id");
            if (requestSha256 != null && !Sha(requestSha256)) throw new IntegrationReceiptException("bad request_sha256");
            if (responseSha256 != null && !Sha(responseSha256)) throw new IntegrationReceiptException("bad response_sha256");
            if (!Outcomes.Contains(outcome)) throw new IntegrationReceiptException("bad outcome");
            if (outcome == "ok" ? errorToken != null : !Token(errorToken)) throw new IntegrationReceiptException("error_token must be null exactly when outcome is ok, else a named token");
            if (!Sha(prevSha256)) throw new IntegrationReceiptException("bad prev_sha256");
            foreach (var c in carried) if (!ContractId(c.Contract) || !Sha(c.Sha256)) throw new IntegrationReceiptException("bad carried entry");
            var sb = new StringBuilder();
            sb.Append("{\"schema\":").Append(Q(Schema))
              .Append(",\"consumer\":").Append(Q(consumer))
              .Append(",\"provider\":").Append(Q(provider))
              .Append(",\"emitted_by\":").Append(Q(emittedBy))
              .Append(",\"contract\":").Append(Q(contract))
              .Append(",\"request_sha256\":").Append(Q(requestSha256))
              .Append(",\"response_sha256\":").Append(Q(responseSha256))
              .Append(",\"carried\":[").Append(string.Join(",", carried.Select(c => "{\"contract\":" + Q(c.Contract) + ",\"sha256\":" + Q(c.Sha256) + "}"))).Append(']')
              .Append(",\"outcome\":").Append(Q(outcome))
              .Append(",\"error_token\":").Append(Q(errorToken))
              .Append(",\"prev_sha256\":").Append(Q(prevSha256))
              .Append('}');
            return sb.ToString();
        }

        public static string Hash(string line) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line))).ToLowerInvariant();

        // Recomputes the whole chain: every line well-formed in the canonical byte form, every prev link right, LF-terminated.
        // Returns (count, head hash); throws IntegrationReceiptException on any problem.
        public static (int Count, string Head) Verify(string path)
        {
            if (!File.Exists(path)) return (0, Genesis);
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) return (0, Genesis);
            if (bytes[^1] != (byte)'\n') throw new IntegrationReceiptException("the last receipt is not terminated (partial write?)");
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) throw new IntegrationReceiptException("byte-order mark");
            var text = Encoding.UTF8.GetString(bytes);
            if (text.Contains('\r')) throw new IntegrationReceiptException("CR in the receipt file (LF only)");
            var lines = text.Substring(0, text.Length - 1).Split('\n');
            string head = Genesis; int n = 0;
            foreach (var l in lines)
            {
                n++;
                var r = Parse(l) ?? throw new IntegrationReceiptException("receipt " + n + " is not in the canonical form");
                if (r.Prev != head) throw new IntegrationReceiptException("receipt " + n + " does not link to the previous one (missing, reordered or edited)");
                head = Hash(l);
            }
            return (n, head);
        }

        private sealed record Parsed(string Prev);

        // Re-serialising the parsed fields must give back the very same bytes; anything else is not a canonical receipt.
        private static Parsed? Parse(string line)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                var r = doc.RootElement;
                string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
                var carried = new List<IntegrationCarried>();
                if (!r.TryGetProperty("carried", out var ca) || ca.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
                foreach (var c in ca.EnumerateArray())
                    carried.Add(new IntegrationCarried(c.TryGetProperty("contract", out var cc) ? cc.GetString() ?? "" : "", c.TryGetProperty("sha256", out var cs) ? cs.GetString() ?? "" : ""));
                if (S("schema") != Schema) return null;
                var again = Line(S("consumer") ?? "", S("provider") ?? "", S("emitted_by") ?? "", S("contract"), S("request_sha256"), S("response_sha256"), carried, S("outcome") ?? "", S("error_token"), S("prev_sha256") ?? "");
                return again == line ? new Parsed(S("prev_sha256")!) : null;
            }
            catch (Exception) { return null; }
        }

        // Verifies, then appends one receipt. Returns its hash. A broken chain is not extended.
        public static string Append(string path, string consumer, string provider, string emittedBy, string? contract, string? requestSha256, string? responseSha256,
                                    IReadOnlyList<IntegrationCarried> carried, string outcome, string? errorToken)
        {
            var (_, head) = Verify(path);
            var line = Line(consumer, provider, emittedBy, contract, requestSha256, responseSha256, carried, outcome, errorToken, head);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                var b = Encoding.UTF8.GetBytes(line + "\n");
                fs.Write(b, 0, b.Length);
                fs.Flush(true);
            }
            var (_, after) = Verify(path);   // success only after the receipt is durably written and verifies
            if (after != Hash(line)) throw new IntegrationReceiptException("the receipt did not verify after writing");
            return after;
        }

        // recognition > soteriavault: what one check of the link means as a receipt (null: nothing was exchanged).
        public static (string Outcome, string? Error)? SoteriaOutcome(SoteriaState s) => s switch
        {
            SoteriaState.NotConfigured => null,
            SoteriaState.Ready => ("ok", null),
            SoteriaState.ContractOnly => ("deferred", "SOTERIA_LINK_NOT_ENABLED"),
            SoteriaState.Unavailable => ("unavailable", "SOTERIA_NOT_FOUND"),
            _ => ("refused", "SOTERIA_CONTRACT_REFUSED"),
        };
    }
}
