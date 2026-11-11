using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Recognition.Browser
{
    // The hosted MCP server (proposal 0012, Phase 2): a loopback-only JSON-RPC-over-HTTP endpoint an external Claude adds to its MCP
    // config. It is deliberately dependency-light and WebView2-free — the actual page actions are an injected delegate (the executor in
    // MainWindow.Agent.cs), so the request parsing / auth / routing here is testable on its own and cannot reach a page except through
    // that delegate, which itself goes through AgentRules.
    //
    // Security of the transport: it binds 127.0.0.1 only (never a routable address); every request must carry the per-launch bearer
    // token (Authorization: Bearer <token>) and is rejected 401 otherwise, before any routing; and it speaks a tiny, fixed HTTP subset
    // (one POST of a JSON-RPC body) so there is little surface. TcpListener is used rather than HttpListener so no Windows urlacl
    // reservation is needed for a non-admin launch.
    internal sealed class AgentResult
    {
        public bool IsImage;
        public string Text = "";
        public string? Base64Png;
        public bool IsError;
        public static AgentResult Ok(string text) => new() { Text = text };
        public static AgentResult Image(string b64) => new() { IsImage = true, Base64Png = b64 };
        public static AgentResult Error(string text) => new() { Text = text, IsError = true };
    }

    internal sealed class AgentServer
    {
        private readonly AgentSession _session;
        private readonly Func<AgentCapability, AgentCallArgs, Task<AgentResult>> _execute;
        private readonly Action<string>? _log;
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;

        public int Port { get; private set; }
        public bool Running => _listener != null;
        public string Url => "http://127.0.0.1:" + Port + "/mcp";

        public AgentServer(AgentSession session, Func<AgentCapability, AgentCallArgs, Task<AgentResult>> execute, Action<string>? log = null)
        {
            _session = session; _execute = execute; _log = log;
        }

        // Bind the first free port in a small loopback range. Returns false if none is free.
        public bool Start(int firstPort = 8766, int tries = 16)
        {
            for (int p = firstPort; p < firstPort + tries; p++)
            {
                try
                {
                    var l = new TcpListener(IPAddress.Loopback, p);
                    l.Start();
                    _listener = l; Port = p; _cts = new CancellationTokenSource();
                    _ = AcceptLoopAsync(_cts.Token);
                    return true;
                }
                catch (SocketException) { /* port taken, try next */ }
            }
            return false;
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            _listener = null; _cts = null;
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            var l = _listener; if (l == null) return;
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await l.AcceptTcpClientAsync(ct); }
                catch { break; }
                _ = HandleClientAsync(client, ct);
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    using var stream = client.GetStream();
                    var req = await ReadRequestAsync(stream, ct);
                    if (req == null) { await WriteAsync(stream, 400, "application/json", AgentProtocol.RpcError(null, -32700, "bad request"), ct); return; }

                    if (!_session.AuthOk(req.Value.BearerToken))
                    { await WriteAsync(stream, 401, "application/json", AgentProtocol.RpcError(null, -32001, "unauthorized"), ct); return; }

                    if (req.Value.Method != "POST")
                    { await WriteAsync(stream, 405, "application/json", AgentProtocol.RpcError(null, -32600, "use POST"), ct); return; }

                    var response = await HandleRpcAsync(req.Value.Body);
                    if (response.Length == 0) { await WriteAsync(stream, 202, "application/json", "", ct); return; }   // a notification: no body
                    await WriteAsync(stream, 200, "application/json", response, ct);
                }
                catch (Exception ex) { _log?.Invoke("agent server: " + ex.Message); }
            }
        }

        // The JSON-RPC router. Pure except for the injected executor; returns the response body ("" for a notification). Never throws.
        public async Task<string> HandleRpcAsync(string body)
        {
            long? id = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt64(out var i)) id = i;
                var method = root.TryGetProperty("method", out var mEl) && mEl.ValueKind == JsonValueKind.String ? mEl.GetString() : null;
                if (method == null) return AgentProtocol.RpcError(id, -32600, "no method");

                switch (method)
                {
                    case "initialize":
                        return AgentProtocol.RpcInitializeResult(id ?? 0);
                    case "notifications/initialized":
                    case "notifications/cancelled":
                        return "";   // notifications take no response
                    case "ping":
                        return "{\"jsonrpc\":\"2.0\",\"id\":" + (id ?? 0) + ",\"result\":{}}";
                    case "tools/list":
                        return AgentProtocol.RpcToolsListResult(id ?? 0);
                    case "tools/call":
                        return await HandleToolCallAsync(id ?? 0, root);
                    default:
                        return AgentProtocol.RpcError(id, -32601, "method not found: " + method);
                }
            }
            catch (JsonException) { return AgentProtocol.RpcError(id, -32700, "parse error"); }
            catch (Exception ex) { return AgentProtocol.RpcError(id, -32603, "internal error: " + ex.Message); }
        }

        private async Task<string> HandleToolCallAsync(long id, JsonElement root)
        {
            if (!root.TryGetProperty("params", out var pr) || pr.ValueKind != JsonValueKind.Object)
                return AgentProtocol.RpcError(id, -32602, "missing params");
            var name = pr.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
            var args = pr.TryGetProperty("arguments", out var a) ? a : default;

            if (!AgentProtocol.TryParse(name, args, out var cap, out var parsed, out var err))
                return AgentProtocol.RpcTextResult(id, err, isError: true);   // a tool-level error, not a protocol error

            AgentResult res;
            try { res = await _execute(cap, parsed); }
            catch (Exception ex) { res = AgentResult.Error("action failed: " + ex.Message); }

            if (res.IsImage && res.Base64Png != null) return AgentProtocol.RpcImageResult(id, res.Base64Png);
            return AgentProtocol.RpcTextResult(id, res.Text, res.IsError);
        }

        // ---- a tiny HTTP/1.1 request reader ------------------------------------------------------------------------------
        private readonly record struct HttpRequest(string Method, string Path, string? BearerToken, string Body);

        private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
        {
            var header = new List<byte>(1024);
            var one = new byte[1];
            int consecutive = 0;
            // read until CRLFCRLF
            while (header.Count < 64 * 1024)
            {
                int r = await stream.ReadAsync(one.AsMemory(0, 1), ct);
                if (r == 0) break;
                header.Add(one[0]);
                if (one[0] == (byte)'\n' && (header.Count >= 2 && header[^2] == (byte)'\r')) { consecutive++; if (consecutive == 2) break; }
                else if (one[0] != (byte)'\r') consecutive = 0;
            }
            var headerText = Encoding.ASCII.GetString(header.ToArray());
            var lines = headerText.Split("\r\n", StringSplitOptions.None);
            if (lines.Length == 0 || lines[0].Length == 0) return null;
            var parts = lines[0].Split(' ');
            if (parts.Length < 2) return null;
            string method = parts[0], path = parts[1];
            string? bearer = null; int contentLength = 0;
            foreach (var line in lines)
            {
                int c = line.IndexOf(':');
                if (c <= 0) continue;
                var key = line.Substring(0, c).Trim().ToLowerInvariant();
                var val = line.Substring(c + 1).Trim();
                if (key == "authorization" && val.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) bearer = val.Substring(7).Trim();
                else if (key == "content-length") int.TryParse(val, out contentLength);
            }
            string bodyStr = "";
            if (contentLength > 0 && contentLength <= 8 * 1024 * 1024)
            {
                var buf = new byte[contentLength]; int got = 0;
                while (got < contentLength)
                {
                    int r = await stream.ReadAsync(buf.AsMemory(got, contentLength - got), ct);
                    if (r == 0) break; got += r;
                }
                bodyStr = Encoding.UTF8.GetString(buf, 0, got);
            }
            return new HttpRequest(method, path, bearer, bodyStr);
        }

        private static async Task WriteAsync(NetworkStream stream, int status, string contentType, string body, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(status == 200 ? "OK" : "X").Append("\r\n");
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(bytes.Length).Append("\r\n");
            sb.Append("Connection: close\r\n\r\n");
            var head = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(head, ct);
            if (bytes.Length > 0) await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }
    }
}
