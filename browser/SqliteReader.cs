using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Recognition.Browser
{
    // A small, read-only SQLite reader: enough to read one table out of a browser's cookie store (Chromium "Cookies",
    // Firefox "cookies.sqlite"). No dependency, no writing, no SQL engine. The file format is public and stable
    // (https://www.sqlite.org/fileformat2.html). Everything here is pure and bounded, and browser.tests reads real
    // database files written by Python's sqlite3 through it.
    //
    // What it supports: the main b-tree of a table found through sqlite_master, leaf and interior table pages, record
    // values (NULL, integers, 64-bit floats, text, blobs), INTEGER PRIMARY KEY rowid aliases, and payload that overflows
    // onto overflow pages. It does not read indexes, WAL side files or anything it is not asked for. Anything it cannot
    // make sense of is a SqliteException, never a wrong row.
    internal sealed class SqliteException : Exception { public SqliteException(string m) : base(m) { } }

    internal sealed class SqliteTable
    {
        public List<string> Columns { get; } = new();
        public List<object?[]> Rows { get; } = new();
        public int ColumnIndex(string name) => Columns.FindIndex(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
    }

    internal static class SqliteReader
    {
        public const int MaxPages = 200_000;     // ~ up to a few GB at 16 KiB pages; a cookie store is far smaller
        public const int MaxRecordBytes = 8 * 1024 * 1024;

        public static SqliteTable Read(byte[] file, string tableName, int maxRows = 200_000)
        {
            // Any malformed or hostile database must surface as SqliteException, never a raw IndexOutOfRange/Argument/Overflow
            // exception from reading past a cell or a bad length (audit 2026-11-04, found by fuzzing). The reader never writes and
            // never runs anything; the worst a crafted file can do is be refused here.
            try { return ReadCore(file, tableName, maxRows); }
            catch (SqliteException) { throw; }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException or OverflowException or FormatException)
            {
                throw new SqliteException("corrupt database (" + ex.GetType().Name + ")");
            }
        }

        private static SqliteTable ReadCore(byte[] file, string tableName, int maxRows)
        {
            if (file.Length < 100 || Encoding.ASCII.GetString(file, 0, 16) != "SQLite format 3\0") throw new SqliteException("not a SQLite file");
            int pageSize = (file[16] << 8) | file[17]; if (pageSize == 1) pageSize = 65536;
            if (pageSize < 512 || (pageSize & (pageSize - 1)) != 0) throw new SqliteException("bad page size");
            int reserved = file[20];
            int usable = pageSize - reserved; if (usable < 480) throw new SqliteException("bad reserved size");
            int pageCount = file.Length / pageSize; if (pageCount < 1) throw new SqliteException("truncated file");
            var enc = file[56] == 0 ? 1 : file[56];   // 1 UTF-8, 2 UTF-16le, 3 UTF-16be

            var ctx = new Ctx(file, pageSize, usable, pageCount, enc);

            // 1) sqlite_master lives on page 1: find the table's root page and its CREATE statement
            int? root = null; string? createSql = null;
            foreach (var rec in ctx.WalkTable(1))
            {
                // sqlite_master columns: type, name, tbl_name, rootpage, sql
                if (rec.Values.Length >= 5 && rec.Values[0] as string == "table" && string.Equals(rec.Values[1] as string, tableName, StringComparison.OrdinalIgnoreCase))
                {
                    root = rec.Values[3] is long rp ? (int)rp : (int?)null; createSql = rec.Values[4] as string; break;
                }
            }
            if (root == null) throw new SqliteException("no table named " + tableName);

            var table = new SqliteTable();
            table.Columns.AddRange(ParseColumns(createSql));
            int rowidAlias = RowidAliasIndex(createSql, table.Columns);

            foreach (var rec in ctx.WalkTable(root.Value))
            {
                var row = new object?[table.Columns.Count];
                for (int i = 0; i < row.Length; i++) row[i] = i < rec.Values.Length ? rec.Values[i] : null;
                if (rowidAlias >= 0 && rowidAlias < row.Length && row[rowidAlias] == null) row[rowidAlias] = rec.Rowid;
                table.Rows.Add(row);
                if (table.Rows.Count >= maxRows) break;
            }
            return table;
        }

        // ---- column names from the CREATE TABLE statement ----------------------------------------------------------------
        internal static List<string> ParseColumns(string? createSql)
        {
            var cols = new List<string>();
            if (string.IsNullOrEmpty(createSql)) return cols;
            int open = createSql.IndexOf('('); int close = createSql.LastIndexOf(')');
            if (open < 0 || close <= open) return cols;
            var body = createSql.Substring(open + 1, close - open - 1);
            foreach (var partRaw in SplitTopLevel(body))
            {
                var part = partRaw.Trim();
                if (part.Length == 0) continue;
                var firstWord = FirstToken(part);
                var up = firstWord.ToUpperInvariant();
                if (up is "CONSTRAINT" or "PRIMARY" or "UNIQUE" or "CHECK" or "FOREIGN" or "KEY") continue;   // a table constraint, not a column
                cols.Add(Unquote(firstWord));
            }
            return cols;
        }

        static int RowidAliasIndex(string? createSql, List<string> cols)
        {
            if (string.IsNullOrEmpty(createSql)) return -1;
            int open = createSql.IndexOf('('); int close = createSql.LastIndexOf(')'); if (open < 0 || close <= open) return -1;
            foreach (var partRaw in SplitTopLevel(createSql.Substring(open + 1, close - open - 1)))
            {
                var part = partRaw.Trim(); var def = part.ToUpperInvariant();
                if (def.Contains("INTEGER") && def.Contains("PRIMARY") && def.Contains("KEY"))
                {
                    var name = Unquote(FirstToken(part));
                    var idx = cols.FindIndex(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0) return idx;
                }
            }
            return -1;
        }

        static IEnumerable<string> SplitTopLevel(string s)
        {
            int depth = 0, start = 0; bool inStr = false; char q = '\0';
            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (inStr) { if (c == q) inStr = false; continue; }
                if (c is '\'' or '"' or '`' or '[') { inStr = true; q = c == '[' ? ']' : c; continue; }
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ',' && depth == 0) { yield return s.Substring(start, i - start); start = i + 1; }
            }
            yield return s.Substring(start);
        }

        static string FirstToken(string s)
        {
            s = s.TrimStart();
            if (s.Length == 0) return "";
            if (s[0] is '"' or '`' or '[')
            {
                char close = s[0] == '[' ? ']' : s[0];
                int end = s.IndexOf(close, 1);
                return end < 0 ? s : s.Substring(0, end + 1);
            }
            int i = 0; while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '$')) i++;
            return i == 0 ? s.Substring(0, 1) : s.Substring(0, i);
        }

        static string Unquote(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '`' && s[^1] == '`') || (s[0] == '[' && s[^1] == ']')))
                return s.Substring(1, s.Length - 2).Replace("\"\"", "\"");
            return s;
        }

        // ---- the b-tree walk ---------------------------------------------------------------------------------------------
        private sealed class Ctx
        {
            readonly byte[] _f; readonly int _ps, _usable, _pages, _enc;
            public Ctx(byte[] f, int ps, int usable, int pages, int enc) { _f = f; _ps = ps; _usable = usable; _pages = pages; _enc = enc; }

            int PageBase(int page) => (page - 1) * _ps;

            public IEnumerable<Record> WalkTable(int page)
            {
                var seen = new HashSet<int>();
                foreach (var r in Walk(page, seen, 0)) yield return r;
            }

            IEnumerable<Record> Walk(int page, HashSet<int> seen, int depth)
            {
                if (page < 1 || page > _pages) throw new SqliteException("page out of range");
                if (depth > 64 || !seen.Add(page)) throw new SqliteException("b-tree loops");   // a damaged file can point in circles
                int bas = PageBase(page);
                int hdr = page == 1 ? 100 : 0;
                int o = bas + hdr;
                byte type = _f[o];
                if (type != 5 && type != 13) throw new SqliteException("not a table page");
                int cells = (_f[o + 3] << 8) | _f[o + 4];
                bool interior = type == 5;
                int cellPtrBase = o + (interior ? 12 : 8);
                for (int i = 0; i < cells; i++)
                {
                    int ptr = (_f[cellPtrBase + i * 2] << 8) | _f[cellPtrBase + i * 2 + 1];
                    int cp = bas + ptr;
                    if (cp < bas || cp >= bas + _ps) throw new SqliteException("cell out of page");
                    if (interior)
                    {
                        int child = ReadBE32(cp);
                        foreach (var r in Walk(child, seen, depth + 1)) yield return r;
                    }
                    else
                    {
                        int q = cp;
                        long payloadLen = ReadVarint(ref q);
                        long rowid = ReadVarint(ref q);
                        var payload = ReadPayload(q, payloadLen);
                        yield return new Record(rowid, DecodeRecord(payload));
                    }
                }
                if (interior)
                {
                    int right = ReadBE32(o + 8);
                    foreach (var r in Walk(right, seen, depth + 1)) yield return r;
                }
            }

            // a table-leaf payload, following overflow pages when it does not fit on the page
            byte[] ReadPayload(int start, long payloadLen)
            {
                if (payloadLen < 0 || payloadLen > MaxRecordBytes) throw new SqliteException("record too large");
                int P = (int)payloadLen;
                int U = _usable;
                int X = U - 35;                          // max payload kept entirely on a leaf table page
                if (P <= X)
                {
                    var exact = new byte[P]; Array.Copy(_f, start, exact, 0, P); return exact;
                }
                int M = ((U - 12) * 32 / 255) - 23;
                int K = M + ((P - M) % (U - 4));
                int local = K <= X ? K : M;
                var buf = new byte[P];
                Array.Copy(_f, start, buf, 0, local);
                int filled = local;
                int overflow = ReadBE32(start + local);
                var seen = new HashSet<int>();
                while (overflow != 0 && filled < P)
                {
                    if (overflow < 1 || overflow > _pages || !seen.Add(overflow)) throw new SqliteException("bad overflow chain");
                    int ob = PageBase(overflow);
                    int next = ReadBE32(ob);
                    int take = Math.Min(U - 4, P - filled);
                    Array.Copy(_f, ob + 4, buf, filled, take);
                    filled += take; overflow = next;
                }
                if (filled != P) throw new SqliteException("overflow chain too short");
                return buf;
            }

            object?[] DecodeRecord(byte[] rec)
            {
                int p = 0;
                long headerLen = SqliteReader.ReadVarint(rec, ref p);
                if (headerLen < 1 || headerLen > rec.Length) throw new SqliteException("bad record header");
                var serials = new List<long>();
                while (p < headerLen) serials.Add(SqliteReader.ReadVarint(rec, ref p));
                int body = (int)headerLen;
                var vals = new object?[serials.Count];
                for (int i = 0; i < serials.Count; i++)
                {
                    long st = serials[i];
                    switch (st)
                    {
                        case 0: vals[i] = null; break;
                        case 1: vals[i] = (long)(sbyte)rec[body]; body += 1; break;
                        case 2: vals[i] = (long)ReadIntBE(rec, body, 2); body += 2; break;
                        case 3: vals[i] = ReadIntBE(rec, body, 3); body += 3; break;
                        case 4: vals[i] = ReadIntBE(rec, body, 4); body += 4; break;
                        case 5: vals[i] = ReadIntBE(rec, body, 6); body += 6; break;
                        case 6: vals[i] = ReadIntBE(rec, body, 8); body += 8; break;
                        case 7: vals[i] = BitConverter.Int64BitsToDouble(ReadIntBE(rec, body, 8)); body += 8; break;
                        case 8: vals[i] = 0L; break;
                        case 9: vals[i] = 1L; break;
                        case 10: case 11: vals[i] = null; break;   // reserved
                        default:
                            int len = (int)((st - (st % 2 == 0 ? 12 : 13)) / 2);
                            if (len < 0 || body + len > rec.Length) throw new SqliteException("value runs past the record");
                            if (st % 2 == 0) { var blob = new byte[len]; Array.Copy(rec, body, blob, 0, len); vals[i] = blob; }
                            else vals[i] = DecodeText(rec, body, len);
                            body += len; break;
                    }
                    if (body > rec.Length) throw new SqliteException("value runs past the record");
                }
                return vals;
            }

            string DecodeText(byte[] b, int off, int len) => _enc switch
            {
                2 => Encoding.Unicode.GetString(b, off, len),
                3 => Encoding.BigEndianUnicode.GetString(b, off, len),
                _ => Encoding.UTF8.GetString(b, off, len),
            };

            int ReadBE32(int off) => (_f[off] << 24) | (_f[off + 1] << 16) | (_f[off + 2] << 8) | _f[off + 3];
            long ReadVarint(ref int off) => SqliteReader.ReadVarint(_f, ref off);
        }

        internal readonly struct Record { public readonly long Rowid; public readonly object?[] Values; public Record(long r, object?[] v) { Rowid = r; Values = v; } }

        static long ReadIntBE(byte[] b, int off, int n) { long v = 0; for (int i = 0; i < n; i++) v = (v << 8) | b[off + i]; if (n < 8 && (b[off] & 0x80) != 0) v -= 1L << (n * 8); return v; }

        // SQLite varint: up to 9 bytes, big-endian, high bit = continue (the 9th byte uses all 8 bits)
        internal static long ReadVarint(byte[] b, ref int off)
        {
            long result = 0;
            for (int i = 0; i < 8; i++)   // the first 8 bytes carry 7 bits each
            {
                if (off >= b.Length) throw new SqliteException("varint runs off the end");
                byte c = b[off++];
                result = (result << 7) | (byte)(c & 0x7f);
                if ((c & 0x80) == 0) return result;
            }
            if (off >= b.Length) throw new SqliteException("varint runs off the end");
            return (result << 8) | b[off++];   // a 9th byte carries all 8 bits
        }
    }
}
