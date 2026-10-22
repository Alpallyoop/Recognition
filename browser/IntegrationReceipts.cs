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
        public static (int Count, string Head) Verify(string path) { var all = VerifyAll(path); return (all.Count, all.Count == 0 ? Genesis : all[^1]); }

        // Every receipt's hash, in order, after the whole chain verified.
        public static List<string> VerifyAll(string path)
        {
            var hashes = new List<string>();
            if (!File.Exists(path)) return hashes;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) return hashes;
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
                hashes.Add(head);
            }
            return hashes;
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
            lock (AppendGate) return AppendLocked(path, consumer, provider, emittedBy, contract, requestSha256, responseSha256, carried, outcome, errorToken);
        }

        // One writer at a time inside this process: two appends must never both link to the same head (that would fork the chain).
        private static readonly object AppendGate = new();

        // What the last full verification of each file found: the SHA-256 of the file's exact bytes, its receipt count and head.
        // On the next append the file is hashed again (fast: no parsing); only if the bytes are not exactly what was verified (edited,
        // replaced, appended to by someone else, or never seen) is the whole chain parsed and verified again. Any change to any byte
        // therefore still forces the full check; an unchanged file is not re-parsed every time (audit F11).
        private sealed record Verified(string FileSha256, int Count, string Head);
        private static readonly Dictionary<string, Verified> Cache = new(StringComparer.OrdinalIgnoreCase);

        private static string FileSha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

        private static string AppendLocked(string path, string consumer, string provider, string emittedBy, string? contract, string? requestSha256, string? responseSha256,
                                           IReadOnlyList<IntegrationCarried> carried, string outcome, string? errorToken)
        {
            var full = Path.GetFullPath(path);
            var before = File.Exists(full) ? File.ReadAllBytes(full) : Array.Empty<byte>();
            var beforeSha = FileSha(before);
            int count; string head;
            if (Cache.TryGetValue(full, out var v) && v.FileSha256 == beforeSha) { count = v.Count; head = v.Head; }
            else { Cache.Remove(full); (count, head) = Verify(full); }   // throws on a broken chain: nothing is appended
            var line = Line(consumer, provider, emittedBy, contract, requestSha256, responseSha256, carried, outcome, errorToken, head);
            var add = Encoding.UTF8.GetBytes(line + "\n");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using (var fs = new FileStream(full, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                fs.Write(add, 0, add.Length);
                fs.Flush(true);
            }
            // success only after the receipt is durably written: the file must now be exactly the verified bytes plus this receipt
            var after = File.ReadAllBytes(full);
            bool exact = after.Length == before.Length + add.Length && after.AsSpan(0, before.Length).SequenceEqual(before) && after.AsSpan(before.Length).SequenceEqual(add);
            if (!exact) { Cache.Remove(full); Verify(full); throw new IntegrationReceiptException("the receipt file changed while the receipt was written"); }
            var h = Hash(line);
            Cache[full] = new Verified(FileSha(after), count + 1, h);
            return h;
        }

        // ---- the emitter's own witness ---------------------------------------------------------------------------------------------
        // A hash chain cannot see an edit to its NEWEST receipt (nothing links to it yet), nor a forged receipt added at the end, nor the
        // last receipts being cut off. So every receipt's hash is also recorded somewhere independent of the receipt file: in Recognition,
        // the DPAPI-encrypted, hash-chained action ledger (entry "integration.receipt|<consumer>><provider>|<sha256>"). Before every append
        // the file must end exactly at the last witnessed hash, and every witnessed hash must be in the file in the same order.
        // NFL's witnessing (Constellation's Witness Receipts button) is the external witness on top of this; this one needs no other service.
        public const string WitnessAction = "integration.receipt";

        public static string WitnessEntry(string consumer, string provider, string receiptSha256)
        {
            if (!ServiceId(consumer) || !ServiceId(provider) || !Sha(receiptSha256)) throw new IntegrationReceiptException("bad witness entry");
            return WitnessAction + "|" + consumer + ">" + provider + "|" + receiptSha256;
        }

        // The witnessed hashes for one integration, oldest first, from ledger action texts (anything else is ignored).
        public static List<string> Witnessed(IEnumerable<string> ledgerActions, string consumer, string provider)
        {
            var prefix = WitnessAction + "|" + consumer + ">" + provider + "|";
            return ledgerActions.Where(a => a != null && a.StartsWith(prefix, StringComparison.Ordinal) && Sha(a.Substring(prefix.Length))).Select(a => a.Substring(prefix.Length)).ToList();
        }

        public enum WitnessState { Empty, Matches, Unanchored }

        // Throws IntegrationReceiptException (token RECEIPT_WITNESS_MISMATCH) when the file and the witness disagree.
        // Unanchored: the file has receipts but nothing was ever witnessed (receipts written before the witness existed).
        public static WitnessState CheckWitness(string path, IReadOnlyList<string> witnessed)
        {
            var full = Path.GetFullPath(path);
            List<string>? all = null;
            string head;
            var bytes = File.Exists(full) ? File.ReadAllBytes(full) : Array.Empty<byte>();
            if (Cache.TryGetValue(full, out var v) && v.FileSha256 == FileSha(bytes)) head = v.Head;          // unchanged since fully verified
            else { all = VerifyAll(full); head = all.Count == 0 ? Genesis : all[^1]; }
            if (witnessed.Count == 0) return head == Genesis ? WitnessState.Empty : WitnessState.Unanchored;
            if (head != witnessed[^1])
                throw new IntegrationReceiptException("RECEIPT_WITNESS_MISMATCH: the receipt file does not end at the last receipt this browser witnessed (the newest receipt was edited, removed, or a receipt was added outside the browser)");
            if (all != null)
            {
                int i = 0;   // every witnessed hash, in order, must be in the chain
                foreach (var w in witnessed) { while (i < all.Count && all[i] != w) i++; if (i == all.Count) throw new IntegrationReceiptException("RECEIPT_WITNESS_MISMATCH: a witnessed receipt is missing from the file"); i++; }
            }
            return WitnessState.Matches;
        }

        // ---- NFL, the outside witness (NFL proposal 0003, Recognition proposal 0005) ------------------------------------------------
        // Null: NFL is not set up here. Available false: NFL is set up but could not be asked (never blocks the integration).
        public sealed record NflView(bool Available, int Count, string Head, string Note);

        // NFL's "receipt-head" answer, or null when the output is not a complete, well-formed answer.
        public static NflView? ParseNflHead(string? stdout, string integration)
        {
            if (string.IsNullOrEmpty(stdout) || !stdout.Contains("NFL_RECEIPT_HEAD_OK")) return null;
            string? V(string k) { foreach (var l in stdout.Split('\n')) { var t = l.Trim(); if (t.StartsWith(k + "=", StringComparison.Ordinal)) return t.Substring(k.Length + 1); } return null; }
            if (V("INTEGRATION") != integration) return null;
            var head = V("HEAD");
            if (!int.TryParse(V("WITNESSED_COUNT"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) || !Sha(head)) return null;
            if ((count == 0) != (head == Genesis)) return null;
            return new NflView(true, count, head!, "");
        }

        // NFL's "witness-receipt" answer for one line: WITNESSED, ALREADY_WITNESSED, REFUSED:<token>, or null when unreadable.
        public static string? ParseNflWitness(string? stdout, string receiptSha256)
        {
            if (string.IsNullOrEmpty(stdout)) return null;
            var lines = stdout.Split('\n').Select(l => l.Trim()).ToList();
            if (!lines.Contains("RECEIPT_SHA256=" + receiptSha256)) return null;
            if (lines.Contains("REFUSED")) { var e = lines.FirstOrDefault(l => l.StartsWith("RECEIPT_ERROR=", StringComparison.Ordinal)); return "REFUSED:" + (e == null ? "UNKNOWN" : e.Substring(14)); }
            if (lines.Contains("WITNESSED") && lines.Contains("NFL_RECEIPT_WITNESS_OK")) return "WITNESSED";
            if (lines.Contains("ALREADY_WITNESSED") && lines.Contains("NFL_RECEIPT_WITNESS_OK")) return "ALREADY_WITNESSED";
            return null;
        }

        // Where NFL is: an explicit folder (RECOGNITION_NFL_ROOT) or, walking up from Recognition, the dev-root layout
        // (_Covenant-systems\nfl, or nfl beside it). Never a fixed drive path. isNfl(dir) says whether dir holds NFL's CLI.
        public static string? FindNfl(string? explicitRoot, string startDir, Func<string, bool> isNfl)
        {
            if (!string.IsNullOrWhiteSpace(explicitRoot)) return isNfl(explicitRoot) ? explicitRoot : null;
            for (var d = new DirectoryInfo(startDir); d != null; d = d.Parent)
                foreach (var rel in new[] { Path.Combine("_Covenant-systems", "nfl"), Path.Combine("_Covenant-Systems", "nfl"), "nfl" })
                {
                    var c = Path.Combine(d.FullName, rel);
                    if (isNfl(c)) return c;
                }
            return null;
        }

        // Checks the browser's own witness and, when NFL could be asked, NFL's: NFL's newest witnessed receipt must still be a line of
        // the file (the chain then fixes every line before it). Appends, then records the new receipt's hash with recordWitness.
        // A file the browser has no record of is never trusted blindly:
        //   * NFL witnessed all of it (NFL's newest = the file's last line)  -> anchored on NFL's word (note);
        //   * NFL witnessed none of this integration, or NFL is not set up -> the file is set aside (kept, never deleted, named
        //     receipts.unwitnessed-<sha256 prefix>.ndjson) and a new chain starts (note);
        //   * anything else (NFL could not be asked, or receipts after NFL's last one) -> refused, nothing written.
        public static (string Hash, string? Note) AppendWitnessed(string path, IReadOnlyList<string> witnessed, Action<string> recordWitness, NflView? nfl,
                                                                 string consumer, string provider, string emittedBy, string? contract, string? requestSha256, string? responseSha256,
                                                                 IReadOnlyList<IntegrationCarried> carried, string outcome, string? errorToken)
        {
            lock (AppendGate)
            {
                string? note = null;
                var state = CheckWitness(path, witnessed);
                bool nflHasSome = nfl is { Available: true } && nfl.Head != Genesis;
                List<string>? all = null;
                if (nflHasSome)
                {
                    all = VerifyAll(path);
                    if (!all.Contains(nfl!.Head))
                        throw new IntegrationReceiptException("RECEIPT_NFL_MISMATCH: the receipt NFL witnessed last for " + consumer + ">" + provider + " is not in the file (a witnessed receipt was rewritten, removed or replaced)");
                }
                if (state == WitnessState.Unanchored)
                {
                    all ??= VerifyAll(path);
                    if (nflHasSome && all[^1] == nfl!.Head)
                    {
                        recordWitness(all[^1]);
                        note = "anchored " + all.Count + " earlier receipt(s) on NFL's witness";
                    }
                    else if (nfl == null || (nfl.Available && nfl.Head == Genesis))
                    {
                        var full = Path.GetFullPath(path);
                        var aside = Path.Combine(Path.GetDirectoryName(full)!, "receipts.unwitnessed-" + FileSha(File.ReadAllBytes(full)).Substring(0, 16) + ".ndjson");
                        File.Move(full, aside, true);
                        Cache.Remove(full);
                        note = "set aside " + all.Count + " receipt(s) nothing vouches for (kept as " + Path.GetFileName(aside) + "); a new chain starts";
                    }
                    else if (!nfl.Available)
                        throw new IntegrationReceiptException("RECEIPT_WITNESS_UNANCHORED: the browser has no record of these receipts and NFL could not be asked; nothing written");
                    else
                        throw new IntegrationReceiptException("RECEIPT_WITNESS_UNANCHORED: receipts after the one NFL witnessed last have no witness; nothing written");
                }
                var h = AppendLocked(path, consumer, provider, emittedBy, contract, requestSha256, responseSha256, carried, outcome, errorToken);
                recordWitness(h);
                return (h, note);
            }
        }

        // Forget what was verified (tests, or after the file is replaced on purpose).
        public static void ForgetVerified() { lock (AppendGate) Cache.Clear(); }

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
