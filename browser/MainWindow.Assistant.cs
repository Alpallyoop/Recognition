using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Recognition.Browser
{
    // The embedded assistant (proposal 0012, Phase 5): a Claude panel inside Recognition (recognition:assistant) that reads and acts on
    // the current page using the SAME governed executor (AgentExecuteAsync) as the MCP and CDP frontends. It is just a fourth caller of
    // the core — it gets no extra privilege, every tool it runs goes through AgentRules (so grants, confirmations, the private/vault/
    // internal refusals and receipts all still apply), and the person watches it work. The person's Anthropic API key is stored
    // DPAPI-protected under runtime/ and is never placed in a bundle, a packet, a receipt or the page.
    public partial class MainWindow
    {
        private string _assistantModel = AssistantProtocol.DefaultModel;
        private readonly JsonArray _asstMessages = new();
        private bool _asstBusy;

        private string AssistantKeyPath() => Path.Combine(_repoRoot, "runtime", "assistant_key.v1.enc");

        private void SaveAssistantKey(string key)
        {
            try { Directory.CreateDirectory(Path.Combine(_repoRoot, "runtime")); File.WriteAllBytes(AssistantKeyPath(), ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser)); }
            catch (Exception ex) { Status("could not save the key: " + ex.Message); }
        }

        private string? LoadAssistantKey()
        {
            try { var p = AssistantKeyPath(); return File.Exists(p) ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(p), null, DataProtectionScope.CurrentUser)) : null; }
            catch { return null; }
        }

        private bool HasAssistantKey() => File.Exists(AssistantKeyPath());

        private void AsstPost(object msg) { try { Active?.Web?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(msg)); } catch { } }

        private async void HandleAssistantMessage(BrowserTab tab, string msg)
        {
            try
            {
                if (msg.StartsWith("asst-key:", StringComparison.Ordinal))
                {
                    var k = Uri.UnescapeDataString(msg.Substring("asst-key:".Length)).Trim();
                    if (k.Length is > 10 and < 400) { SaveAssistantKey(k); LoadInternal(tab, "assistant"); } else AsstPost(new { type = "asst", role = "system", text = "that doesn't look like an API key" });
                }
                else if (msg == "asst-clearkey") { try { if (File.Exists(AssistantKeyPath())) File.Delete(AssistantKeyPath()); } catch { } _asstMessages.Clear(); LoadInternal(tab, "assistant"); }
                else if (msg == "asst-clear") { _asstMessages.Clear(); AsstPost(new { type = "cleared" }); }
                else if (msg.StartsWith("asst-model:", StringComparison.Ordinal)) { var m = Uri.UnescapeDataString(msg.Substring("asst-model:".Length)).Trim(); if (m.Length is > 0 and < 100) { _assistantModel = m; SaveSettings(); AsstPost(new { type = "asst", role = "system", text = "model set to " + m }); } }
                else if (msg.StartsWith("asst-send:", StringComparison.Ordinal))
                {
                    if (_asstBusy) return;
                    var text = Uri.UnescapeDataString(msg.Substring("asst-send:".Length)).Trim();
                    if (text.Length == 0) return;
                    await AssistantTurnAsync(text);
                }
            }
            catch (Exception ex) { AsstPost(new { type = "asst", role = "system", text = "error: " + ex.Message }); }
        }

        // One user turn: send the conversation to Claude and run the governed tool loop until it stops asking for tools.
        private async Task AssistantTurnAsync(string userText)
        {
            var key = LoadAssistantKey();
            if (key == null) { AsstPost(new { type = "asst", role = "system", text = "add your Anthropic API key first" }); return; }

            _asstBusy = true;
            AsstPost(new { type = "busy", on = true });
            AsstPost(new { type = "asst", role = "user", text = userText });
            _asstMessages.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = userText }) });

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
                for (int iter = 0; iter < AssistantProtocol.MaxToolIterations; iter++)
                {
                    var body = new JsonObject
                    {
                        ["model"] = _assistantModel,
                        ["max_tokens"] = 4096,
                        ["system"] = AssistantProtocol.SystemPrompt,
                        ["tools"] = JsonNode.Parse(AssistantProtocol.AnthropicToolsJson()),
                        ["messages"] = _asstMessages.DeepClone(),
                    };
                    using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
                    req.Headers.TryAddWithoutValidation("x-api-key", key);
                    req.Headers.TryAddWithoutValidation("anthropic-version", AssistantProtocol.ApiVersion);

                    using var resp = await http.SendAsync(req);
                    var respText = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(respText);
                    var root = doc.RootElement;
                    if (!resp.IsSuccessStatusCode || (root.TryGetProperty("type", out var tp) && tp.GetString() == "error"))
                    {
                        var em = root.TryGetProperty("error", out var er) && er.TryGetProperty("message", out var m) ? m.GetString() : ("HTTP " + (int)resp.StatusCode);
                        AsstPost(new { type = "asst", role = "system", text = "the API returned an error: " + em });
                        break;
                    }

                    var content = root.GetProperty("content");
                    _asstMessages.Add(new JsonObject { ["role"] = "assistant", ["content"] = JsonNode.Parse(content.GetRawText()) });

                    var say = AssistantProtocol.TextFrom(content);
                    if (say.Length > 0) AsstPost(new { type = "asst", role = "assistant", text = say });

                    var stop = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;
                    var uses = AssistantProtocol.ParseToolUses(content);
                    if (uses.Count == 0 || AssistantProtocol.IsEndTurn(stop)) break;

                    var results = new JsonArray();
                    foreach (var (id, name, input) in uses)
                    {
                        if (!AgentProtocol.TryParse(name, input, out var cap, out var args, out var perr))
                        {
                            results.Add(JsonNode.Parse(AssistantProtocol.ToolResultText(id, "invalid tool call: " + perr, true)));
                            continue;
                        }
                        AsstPost(new { type = "tool", name, text = DescribeTool(cap, args) });
                        var r = await AgentExecuteAsync(cap, args);
                        if (r.IsImage && r.Base64Png != null) results.Add(JsonNode.Parse(AssistantProtocol.ToolResultImage(id, r.Base64Png)));
                        else results.Add(JsonNode.Parse(AssistantProtocol.ToolResultText(id, r.Text, r.IsError)));
                    }
                    _asstMessages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
                }
            }
            catch (Exception ex) { AsstPost(new { type = "asst", role = "system", text = "could not reach the API: " + ex.Message }); }
            finally { _asstBusy = false; AsstPost(new { type = "busy", on = false }); }
        }

        private static string DescribeTool(AgentCapability cap, AgentCallArgs a) => cap switch
        {
            AgentCapability.Navigate => "navigating to " + a.Url,
            AgentCapability.RunScript => "running a script (you'll be asked to confirm)",
            AgentCapability.FileUpload => "uploading a file (you'll be asked to confirm)",
            AgentCapability.Click => "clicking at " + a.X + "," + a.Y,
            AgentCapability.Type => "typing",
            _ => cap.ToString().ToLowerInvariant(),
        };

        private string AssistantHtml()
        {
            var sb = new StringBuilder(PageHead).Append("<div class='wrap'>");
            sb.Append("<h1>Claude assistant</h1><div class='muted'>Claude can read and act on the page you're viewing, using the same governed tools as the external adapter — it can't see private tabs, Recognition's own pages, or your saved passwords, and some actions ask you to confirm.</div>");
            if (!HasAssistantKey())
            {
                sb.Append("<div class='row' style='display:block'><div class='t'>Add your Anthropic API key</div><div class='u'>Stored encrypted on this computer only (DPAPI), never in a bundle, a packet or a page. Get a key at console.anthropic.com.</div>" +
                          "<div style='margin-top:8px;display:flex;gap:8px'><input id='k' type='password' class='in' placeholder='sk-ant-...' style='flex:1'><a class='btn' onclick=\"var v=document.getElementById('k').value.trim();if(v)send('asst-key:'+encodeURIComponent(v))\">Save key</a></div></div>");
            }
            else
            {
                sb.Append("<div id='log' style='border:1px solid #262b34;border-radius:12px;padding:12px;min-height:220px;max-height:50vh;overflow:auto;margin-bottom:10px'></div>");
                sb.Append("<div style='display:flex;gap:8px'><textarea id='t' class='in' rows='2' placeholder='Ask Claude to do something on this page…' style='flex:1;resize:vertical'></textarea><a class='btn' id='send' onclick='go()'>Send</a></div>");
                sb.Append("<div class='u' style='margin-top:8px'>Model: <input id='mdl' class='in' value='" + Esc(_assistantModel) + "' style='width:220px'> <a class='btn ghost' onclick=\"send('asst-model:'+encodeURIComponent(document.getElementById('mdl').value))\">Set</a> &nbsp; <a class='btn ghost' onclick=\"send('asst-clear')\">New conversation</a> <a class='btn ghost' onclick=\"send('asst-clearkey')\">Remove key</a></div>");
                sb.Append(@"<script>
var log=document.getElementById('log');
function add(role,text){var d=document.createElement('div');d.style.margin='6px 0';d.innerHTML='<b>'+(role==='user'?'You':role==='assistant'?'Claude':role==='tool'?'·':'')+'</b> ';var s=document.createElement('span');s.textContent=text;d.appendChild(s);if(role==='tool'){d.style.opacity='.7';d.style.fontSize='12px';}log.appendChild(d);log.scrollTop=log.scrollHeight;}
function go(){var t=document.getElementById('t');var v=t.value.trim();if(!v)return;t.value='';send('asst-send:'+encodeURIComponent(v));}
document.getElementById('t').addEventListener('keydown',function(e){if(e.key==='Enter'&&!e.shiftKey){e.preventDefault();go();}});
window.chrome.webview.addEventListener('message',function(ev){var m=ev.data;if(!m)return;if(m.type==='asst')add(m.role,m.text);else if(m.type==='tool')add('tool',m.text);else if(m.type==='busy'){document.getElementById('send').textContent=m.on?'…':'Send';}else if(m.type==='cleared'){log.innerHTML='';}});
</script>");
            }
            sb.Append("</div>").Append(SendScript()).Append(PageFoot);
            return sb.ToString();
        }
    }
}
