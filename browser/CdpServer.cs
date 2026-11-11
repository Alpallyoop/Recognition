using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Recognition.Browser
{
    // The governed CDP bridge (proposal 0012, Phase 3): a loopback DevTools endpoint a CDP client can attach to. It shares the SAME
    // AgentSession (token, per-site grants, kill switch, rate limit) and the SAME executor as the MCP server — one governed core, a
    // second frontend. Each CDP message is routed through CdpMap: answered locally (handshake/metadata), mapped to one governed
    // capability (which goes through AgentRules + the executor), or refused. Raw CDP is never reachable.
    //
    // Transport: TcpListener on 127.0.0.1 (no urlacl needed); HTTP discovery at /json/version and /json; a WebSocket at /cdp. Both the
    // discovery and the socket require the per-launch token (?token=... ), so a page in an ordinary tab cannot reach it. Frame-level
    // WebSocket handling is the BCL's (WebSocket.CreateFromStream) — only the upgrade handshake is hand-written (WsHandshake).
    internal sealed class CdpServer
    {
        private readonly AgentSession _session;
        private readonly Func<AgentCapability, AgentCallArgs, Task<AgentResult>> _execute;
        private readonly Func<string, string, Task<AgentResult>>? _passthrough;   // (method, paramsJson) -> raw CDP result, origin-gated
        private readonly Action<string>? _log;
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;

        public int Port { get; private set; }
        public bool Running => _listener != null;
        public string WsUrl => "ws://127.0.0.1:" + Port + "/cdp?token=" + _session.Token;
        public string DiscoveryUrl => "http://127.0.0.1:" + Port + "/json/version?token=" + _session.Token;

        public CdpServer(AgentSession session, Func<AgentCapability, AgentCallArgs, Task<AgentResult>> execute, Func<string, string, Task<AgentResult>>? passthrough = null, Action<string>? log = null)
        { _session = session; _execute = execute; _passthrough = passthrough; _log = log; }

        public bool Start(int firstPort = 9222, int tries = 16)
        {
            for (int p = firstPort; p < firstPort + tries; p++)
            {
                try { var l = new TcpListener(IPAddress.Loopback, p); l.Start(); _listener = l; Port = p; _cts = new CancellationTokenSource(); _ = AcceptLoopAsync(_cts.Token); return true; }
                catch (SocketException) { }
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
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var req = await ReadHeadAsync(stream, ct);
                if (req == null) { client.Dispose(); return; }
                var (method, path, headers) = req.Value;
                var token = QueryToken(path) ?? (headers.TryGetValue("authorization", out var au) && au.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? au.Substring(7).Trim() : null);

                if (!_session.AuthOk(token)) { await WriteHttpAsync(stream, 401, "application/json", "{\"error\":\"unauthorized\"}", ct); client.Dispose(); return; }

                var rawPath = path.Split('?')[0];
                bool wantsUpgrade = headers.TryGetValue("upgrade", out var up) && up.Equals("websocket", StringComparison.OrdinalIgnoreCase);

                if (rawPath == "/cdp" && wantsUpgrade && headers.TryGetValue("sec-websocket-key", out var key))
                {
                    var accept = WsHandshake.AcceptKey(key);
                    var resp = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n";
                    var bytes = Encoding.ASCII.GetBytes(resp);
                    await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct);
                    using var ws = WebSocket.CreateFromStream(stream, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
                    await PumpWebSocketAsync(ws, ct);
                    client.Dispose();
                    return;
                }

                if (rawPath == "/json/version")
                    await WriteHttpAsync(stream, 200, "application/json", "{\"Browser\":\"Recognition/agent\",\"Protocol-Version\":\"1.3\",\"webSocketDebuggerUrl\":" + JsonSerializer.Serialize(WsUrl) + "}", ct);
                else if (rawPath is "/json" or "/json/list")
                    await WriteHttpAsync(stream, 200, "application/json", "[{\"id\":\"recognition-page\",\"type\":\"page\",\"title\":\"Recognition\",\"url\":\"\",\"webSocketDebuggerUrl\":" + JsonSerializer.Serialize(WsUrl) + "}]", ct);
                else
                    await WriteHttpAsync(stream, 404, "application/json", "{\"error\":\"not found\"}", ct);
                client.Dispose();
            }
            catch (Exception ex) { _log?.Invoke("cdp server: " + ex.Message); try { client.Dispose(); } catch { } }
        }

        private async Task PumpWebSocketAsync(WebSocket ws, CancellationToken ct)
        {
            var buf = new byte[64 * 1024];
            var msg = new List<byte>();
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult r;
                try { r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct); }
                catch { break; }
                if (r.MessageType == WebSocketMessageType.Close) { try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct); } catch { } break; }
                if (msg.Count + r.Count > 8 * 1024 * 1024) { msg.Clear(); continue; }   // drop an oversized message
                msg.AddRange(new ArraySegment<byte>(buf, 0, r.Count));
                if (!r.EndOfMessage) continue;
                var text = Encoding.UTF8.GetString(msg.ToArray()); msg.Clear();
                var reply = await HandleCdpMessageAsync(text);
                if (reply.Length > 0)
                {
                    var outBytes = Encoding.UTF8.GetBytes(reply);
                    try { await ws.SendAsync(new ArraySegment<byte>(outBytes), WebSocketMessageType.Text, true, ct); } catch { break; }
                }
                // push any proactive lifecycle events this method implies (Target discovery/attach), so a session client can proceed
                foreach (var ev in EventsFor(text))
                {
                    try { await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(ev)), WebSocketMessageType.Text, true, ct); } catch { break; }
                }
            }
        }

        // The CDP router. Pure except for the injected executor; returns the response JSON (""  if there is nothing to send). Never throws.
        public async Task<string> HandleCdpMessageAsync(string json)
        {
            long id = 0; string? sessionId = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt64(out var i)) id = i;
                if (root.TryGetProperty("sessionId", out var sEl) && sEl.ValueKind == JsonValueKind.String) sessionId = sEl.GetString();
                var method = root.TryGetProperty("method", out var mEl) && mEl.ValueKind == JsonValueKind.String ? mEl.GetString() ?? "" : "";
                if (method.Length == 0) return CdpMap.Error(id, "no method", sessionId);
                var prms = root.TryGetProperty("params", out var pEl) ? pEl : default;

                switch (CdpMap.Classify(method))
                {
                    case CdpClass.Local:
                        return CdpMap.Result(id, CdpMap.LocalResultBody(method), sessionId);
                    case CdpClass.Passthrough:
                    {
                        if (_passthrough == null) return CdpMap.Error(id, "read passthrough is unavailable", sessionId);
                        var raw = prms.ValueKind == JsonValueKind.Object ? prms.GetRawText() : "{}";
                        AgentResult pr;
                        try { pr = await _passthrough(method, raw); }
                        catch (Exception ex) { return CdpMap.Error(id, "read failed: " + ex.Message, sessionId); }
                        if (pr.IsError) return CdpMap.Error(id, pr.Text, sessionId);
                        return CdpMap.Result(id, string.IsNullOrEmpty(pr.Text) ? "{}" : pr.Text, sessionId);   // pr.Text is the raw CDP result body
                    }
                    case CdpClass.Mapped:
                    {
                        if (!CdpMap.TryMap(method, prms, out var cap, out var args, out var noop, out var err))
                            return CdpMap.Error(id, err, sessionId);
                        if (noop) return CdpMap.Result(id, "{}", sessionId);
                        AgentResult res;
                        try { res = await _execute(cap, args); }
                        catch (Exception ex) { return CdpMap.Error(id, "action failed: " + ex.Message, sessionId); }
                        if (res.IsError) return CdpMap.Error(id, res.Text, sessionId);
                        return CdpMap.Result(id, CdpMap.ResultBodyFor(cap, res.IsImage, res.Text, res.Base64Png), sessionId);
                    }
                    default:
                        return CdpMap.Error(id, "method not permitted by Recognition's governed CDP bridge: " + method, sessionId);
                }
            }
            catch (JsonException) { return CdpMap.Error(id, "parse error", sessionId); }
            catch (Exception ex) { return CdpMap.Error(id, "internal error: " + ex.Message, sessionId); }
        }

        // The proactive events (if any) implied by a received message's method. Pure; safe on any input.
        public static string[] EventsFor(string json)
        {
            try { using var d = JsonDocument.Parse(json); return d.RootElement.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? CdpMap.EventsAfter(m.GetString() ?? "") : Array.Empty<string>(); }
            catch { return Array.Empty<string>(); }
        }

        private static string? QueryToken(string path)
        {
            var q = path.IndexOf('?'); if (q < 0) return null;
            foreach (var kv in path.Substring(q + 1).Split('&'))
            {
                var e = kv.Split('=', 2);
                if (e.Length == 2 && e[0] == "token") return Uri.UnescapeDataString(e[1]);
            }
            return null;
        }

        // minimal HTTP head reader (request line + headers, no body — discovery and the WS upgrade carry none)
        private static async Task<(string Method, string Path, Dictionary<string, string> Headers)?> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
        {
            var bytes = new List<byte>(1024); var one = new byte[1]; int nl = 0;
            while (bytes.Count < 32 * 1024)
            {
                int r = await stream.ReadAsync(one.AsMemory(0, 1), ct);
                if (r == 0) break;
                bytes.Add(one[0]);
                if (one[0] == (byte)'\n' && bytes.Count >= 2 && bytes[^2] == (byte)'\r') { nl++; if (nl == 2) break; }
                else if (one[0] != (byte)'\r') nl = 0;
            }
            var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n", StringSplitOptions.None);
            if (lines.Length == 0) return null;
            var parts = lines[0].Split(' ');
            if (parts.Length < 2) return null;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines)
            {
                int c = line.IndexOf(':'); if (c <= 0) continue;
                headers[line.Substring(0, c).Trim().ToLowerInvariant()] = line.Substring(c + 1).Trim();
            }
            return (parts[0], parts[1], headers);
        }

        private static async Task WriteHttpAsync(NetworkStream stream, int status, string contentType, string body, CancellationToken ct)
        {
            var b = Encoding.UTF8.GetBytes(body);
            var head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " X\r\nContent-Type: " + contentType + "\r\nContent-Length: " + b.Length + "\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, ct); if (b.Length > 0) await stream.WriteAsync(b, ct); await stream.FlushAsync(ct);
        }
    }
}
