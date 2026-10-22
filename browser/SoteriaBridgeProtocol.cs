// SoteriaVault <-> Recognition local bridge, protocol v1.
//
// This file is self-contained (BCL only) so Recognition can carry a byte-identical copy
// (Recognition\browser\SoteriaBridgeProtocol.cs). Keep both copies equal; the SHA-256 of this file is checked by
// the tests on both sides.
//
// Transport: a local named pipe created by the SoteriaVault desktop app, PipeOptions.CurrentUserOnly on both ends
// (only your Windows account can open it, and the client refuses a pipe owned by anyone else). Nothing touches the
// network. Frames are a 4-byte little-endian length followed by that many bytes of UTF-8 JSON (at most 1 MiB).
//
// Requests:  {"v":1,"id":"<n>","op":"<op>", ...}      Responses: {"v":1,"id":"<n>","ok":true|false,"error":"TOKEN"?, ...}
//   hello   {client, client_version}                  -> {server_version, state: locked|unlocked, paired: bool}
//   status  {}                                        -> {state, entry_count}
//   list    {page_url}                                -> {entries:[{id,title,username,origin}]}        never passwords
//   fill    {page_url, entry_id}                      -> {username, password}   only after you approve it in SoteriaVault
//   check   {page_url, username, password}            -> {duplicates:[{kind,message}]}
//   save    {page_url, username, password, title, on_conflict?: update|separate}
//                                                     -> {outcome: saved|updated|already_saved|needs_decision|invalid, message, duplicates}
//   import  {entries:[{origin,username,password,note,created_utc,updated_utc}]}  -> {added, merged, conflicts, invalid}
//   unlock_prompt {}                                  -> {} (brings SoteriaVault to the front and asks you to unlock)
// Errors: LOCKED, NOT_PAIRED, DENIED, TIMEOUT, NO_MATCH, BAD_REQUEST, ORIGIN_MISMATCH, BUSY.

using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace SoteriaVault.Bridge
{
    public static class SoteriaBridgeProtocol
    {
        public const int Version = 1;
        public const string PipeName = "soteriavault-bridge-v1";
        public const int MaxFrameBytes = 1 << 20;

        public static async Task WriteFrameAsync(Stream s, JsonObject msg, CancellationToken ct)
        {
            var body = Encoding.UTF8.GetBytes(msg.ToJsonString());
            if (body.Length > MaxFrameBytes) throw new IOException("frame too large");
            var len = BitConverter.GetBytes(body.Length);
            if (!BitConverter.IsLittleEndian) Array.Reverse(len);
            var buf = new byte[4 + body.Length];
            Buffer.BlockCopy(len, 0, buf, 0, 4);
            Buffer.BlockCopy(body, 0, buf, 4, body.Length);
            await s.WriteAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
            await s.FlushAsync(ct).ConfigureAwait(false);
            Array.Clear(body, 0, body.Length);
            Array.Clear(buf, 0, buf.Length);
        }

        /// <summary>Null at end of stream.</summary>
        public static async Task<JsonObject?> ReadFrameAsync(Stream s, CancellationToken ct)
        {
            var len = new byte[4];
            if (!await ReadExactAsync(s, len, ct).ConfigureAwait(false)) return null;
            if (!BitConverter.IsLittleEndian) Array.Reverse(len);
            var n = BitConverter.ToInt32(len, 0);
            if (n <= 0 || n > MaxFrameBytes) throw new IOException("bad frame length");
            var body = new byte[n];
            if (!await ReadExactAsync(s, body, ct).ConfigureAwait(false)) throw new IOException("truncated frame");
            try { return JsonNode.Parse(body) as JsonObject ?? throw new IOException("frame is not an object"); }
            catch (JsonException) { throw new IOException("frame is not JSON"); }
            finally { Array.Clear(body, 0, body.Length); }
        }

        static async Task<bool> ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
        {
            int off = 0;
            while (off < buf.Length)
            {
                var r = await s.ReadAsync(buf, off, buf.Length - off, ct).ConfigureAwait(false);
                if (r == 0) { if (off == 0) return false; throw new IOException("truncated frame"); }
                off += r;
            }
            return true;
        }

        public static string Str(JsonObject o, string k) => o.TryGetPropertyValue(k, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : "";
        public static bool Bool(JsonObject o, string k) => o.TryGetPropertyValue(k, out var v) && v is JsonValue jv && jv.TryGetValue<bool>(out var b) && b;
    }

    /// <summary>One connection to the SoteriaVault desktop app. Not thread-safe: one request at a time.</summary>
    public sealed class SoteriaBridgeClient : IDisposable
    {
        readonly NamedPipeClientStream _pipe;
        int _next;

        SoteriaBridgeClient(NamedPipeClientStream p) { _pipe = p; }

        /// <summary>Null when SoteriaVault is not running (or the pipe belongs to someone else).</summary>
        public static async Task<SoteriaBridgeClient?> TryConnectAsync(string clientName, string clientVersion, int timeoutMs = 1500, string? pipeName = null)
        {
            var p = new NamedPipeClientStream(".", pipeName ?? SoteriaBridgeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await p.ConnectAsync(timeoutMs).ConfigureAwait(false);
                var c = new SoteriaBridgeClient(p);
                var hello = await c.RequestAsync(new JsonObject { ["op"] = "hello", ["client"] = clientName, ["client_version"] = clientVersion }, 120000).ConfigureAwait(false);
                if (!SoteriaBridgeProtocol.Bool(hello, "ok")) { c.Dispose(); return null; }
                c.Hello = hello;
                return c;
            }
            catch { p.Dispose(); return null; }
        }

        public JsonObject Hello { get; private set; } = new JsonObject();

        /// <summary>Sends one request and waits for its response (fill can wait for your approval: default 2 minutes).</summary>
        public async Task<JsonObject> RequestAsync(JsonObject req, int timeoutMs = 120000)
        {
            req["v"] = SoteriaBridgeProtocol.Version;
            req["id"] = (++_next).ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await SoteriaBridgeProtocol.WriteFrameAsync(_pipe, req, cts.Token).ConfigureAwait(false);
                var res = await SoteriaBridgeProtocol.ReadFrameAsync(_pipe, cts.Token).ConfigureAwait(false);
                if (res == null) return new JsonObject { ["ok"] = false, ["error"] = "DISCONNECTED" };
                if (SoteriaBridgeProtocol.Str(res, "id") != (string)req["id"]!) return new JsonObject { ["ok"] = false, ["error"] = "BAD_RESPONSE" };
                return res;
            }
            catch (OperationCanceledException) { return new JsonObject { ["ok"] = false, ["error"] = "TIMEOUT" }; }
            catch (IOException) { return new JsonObject { ["ok"] = false, ["error"] = "DISCONNECTED" }; }
            finally { if (req.ContainsKey("password")) req["password"] = ""; }
        }

        public bool Connected => _pipe.IsConnected;
        public void Dispose() => _pipe.Dispose();
    }
}
