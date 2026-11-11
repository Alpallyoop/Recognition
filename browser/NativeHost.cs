using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Recognition.Browser
{
    // The native-messaging host (proposal 0012, Phase 4). When recognition.exe is launched with --agent-native-host (as a browser's
    // native-messaging manifest specifies), it does NOT open the UI: it runs this stdin/stdout bridge instead, forwarding each framed
    // JSON-RPC message to the ALREADY-RUNNING Recognition instance's loopback MCP endpoint and framing the reply back. So the host is a
    // thin bridge — it holds no privilege of its own; the running instance's AgentSession, grants, kill switch and AgentRules still
    // decide everything. The running instance publishes its endpoint (url + per-launch token) to a DPAPI-protected file under runtime/
    // while agent control is on; the host reads it. No token ever lives in the manifest or on the command line.
    internal static class NativeHost
    {
        // kept under the repo's runtime/ folder (AGENT_POLICY: work stays in the repo, never AppData), DPAPI-protected to this user.
        public static string EndpointPath(string repoRoot) => Path.Combine(repoRoot, "runtime", "agent_native_endpoint.v1.enc");

        public static void WriteEndpoint(string repoRoot, string url, string token)
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(repoRoot, "runtime"));
                var json = JsonSerializer.Serialize(new { url, token });
                var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(EndpointPath(repoRoot), blob);
            }
            catch { }
        }

        public static void DeleteEndpoint(string repoRoot)
        {
            try { var p = EndpointPath(repoRoot); if (File.Exists(p)) File.Delete(p); } catch { }
        }

        public static (string Url, string Token)? ReadEndpoint(string repoRoot)
        {
            try
            {
                var p = EndpointPath(repoRoot);
                if (!File.Exists(p)) return null;
                var json = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(p), null, DataProtectionScope.CurrentUser));
                using var d = JsonDocument.Parse(json);
                var url = d.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;
                var token = d.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
                return url != null && token != null ? (url, token) : null;
            }
            catch { return null; }
        }

        // The bridge loop. Runs until stdin closes (the browser closed the host). Each request is forwarded to the running instance's
        // MCP endpoint with its bearer token; if agent control is off (no endpoint file), every request gets a clean JSON-RPC error.
        public static async Task RunAsync()
        {
            string repoRoot;
            try { repoRoot = MainWindow.FindRepoRoot(AppContext.BaseDirectory); } catch { repoRoot = AppContext.BaseDirectory; }
            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

            while (true)
            {
                string? req;
                try { req = NativeMessaging.ReadFrame(stdin); } catch { break; }
                if (req == null) break;

                var ep = ReadEndpoint(repoRoot);   // re-read each time: agent control may be turned on/off while the host runs
                string resp;
                if (ep == null) resp = AgentProtocol.RpcError(null, -32001, "agent control is off in Recognition — turn it on in Customize → Agent control");
                else
                {
                    try
                    {
                        using var msg = new HttpRequestMessage(HttpMethod.Post, ep.Value.Url) { Content = new StringContent(req, Encoding.UTF8, "application/json") };
                        msg.Headers.TryAddWithoutValidation("Authorization", "Bearer " + ep.Value.Token);
                        using var r = await http.SendAsync(msg);
                        resp = await r.Content.ReadAsStringAsync();
                        if (string.IsNullOrEmpty(resp)) resp = "";   // a notification: forward nothing back
                    }
                    catch (Exception ex) { resp = AgentProtocol.RpcError(null, -32603, "bridge error: " + ex.Message); }
                }
                if (resp.Length > 0)
                {
                    try { NativeMessaging.WriteFrame(stdout, resp); } catch { break; }
                }
            }
        }
    }
}
