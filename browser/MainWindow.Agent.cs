using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Recognition.Browser
{
    // Agent control glue (proposal 0012, Phase 2): the ONE executor every transport calls, plus the session/server lifecycle and the
    // consent prompts. The pure decision is AgentRules.Decide; the pure validation is AgentProtocol; this file is where a decided,
    // validated action actually runs on WebView2 — and the only place it can. A transport (AgentServer) holds no WebView2 handle and
    // reaches a page only by awaiting AgentExecuteAsync, which refuses anything AgentRules refuses and asks the user when consent is due.
    public partial class MainWindow
    {
        private bool _agentEnabled;                 // master switch, off by default, per launch unless the user keeps it on
        private bool _agentDevMode;                 // skip the repeat per-origin prompt for an already-granted origin
        private AgentSession? _agentSession;
        private AgentServer? _agentServer;
        private CdpServer? _cdpServer;
        private readonly ConditionalWeakTable<BrowserTab, object> _agentTabIds = new();
        private int _agentTabCounter;
        private readonly HashSet<BrowserTab> _agentConsoleAttached = new();
        private readonly Dictionary<BrowserTab, List<string>> _agentConsole = new();
        private readonly Dictionary<BrowserTab, List<string>> _agentNetwork = new();

        // ---- settings persistence (keys: agent_enabled, agent_devmode; grants are per-launch and never stored) --------------
        private void LoadAgentSettings(JsonElement r)
        {
            if (r.TryGetProperty("agent_enabled", out var a) && (a.ValueKind == JsonValueKind.True || a.ValueKind == JsonValueKind.False)) _agentEnabled = a.GetBoolean();
            if (r.TryGetProperty("agent_devmode", out var d) && (d.ValueKind == JsonValueKind.True || d.ValueKind == JsonValueKind.False)) _agentDevMode = d.GetBoolean();
            if (r.TryGetProperty("assistant_model", out var m) && m.ValueKind == JsonValueKind.String && m.GetString() is { Length: > 0 and < 100 } mv) _assistantModel = mv;
        }

        private string AgentSettingsJson()
            => J("agent_enabled") + ":" + (_agentEnabled ? "true" : "false") + "," + J("agent_devmode") + ":" + (_agentDevMode ? "true" : "false") + "," + J("assistant_model") + ":" + J(_assistantModel) + ",";

        // ---- server lifecycle -------------------------------------------------------------------------------------------
        public void StartAgentIfEnabled()
        {
            if (!_agentEnabled || _agentServer is { Running: true }) return;
            _agentSession ??= new AgentSession(AgentSession.NewToken());
            _agentServer = new AgentServer(_agentSession, (c, a) => AgentExecuteAsync(c, a), m => { try { ReportRecoveredError(new Exception(m)); } catch { } });
            if (!_agentServer.Start()) { _agentServer = null; Status("agent control: could not open a local port"); return; }
            _actions?.Append("agent.server_start", _agentServer.Url);
            // Second frontend over the SAME session and executor (proposal 0012, Phase 3): a governed CDP endpoint. The passthrough
            // delegate forwards read-only structure methods to the real CDP engine, origin-gated.
            _cdpServer = new CdpServer(_agentSession, (c, a) => AgentExecuteAsync(c, a), (m, p) => AgentCdpReadAsync(m, p), m => { try { ReportRecoveredError(new Exception(m)); } catch { } });
            if (_cdpServer.Start()) _actions?.Append("agent.cdp_start", _cdpServer.DiscoveryUrl); else _cdpServer = null;
            // Publish the endpoint for the native-messaging host (Phase 4): DPAPI-protected under runtime/, removed when agent stops.
            try { NativeHost.WriteEndpoint(_repoRoot, _agentServer.Url, _agentSession.Token); } catch { }
            Status("agent control is on — MCP " + _agentServer.Url + (_cdpServer != null ? "  ·  CDP " + _cdpServer.DiscoveryUrl : ""));
        }

        public void StopAgent()
        {
            try { _agentServer?.Stop(); } catch { }
            try { _cdpServer?.Stop(); } catch { }
            try { NativeHost.DeleteEndpoint(_repoRoot); } catch { }
            _agentServer = null; _cdpServer = null;
            _actions?.Append("agent.server_stop", "");
        }

        // The kill switch: revoke every grant, turn the master switch off, stop the server. Reachable from the menu and Settings.
        public void AgentKill()
        {
            _agentSession?.RevokeAll();
            _agentEnabled = false;
            StopAgent();
            SaveSettings();
            _actions?.Append("agent.kill", "");
            Status("agent control stopped and all site grants revoked");
        }

        public void SetAgentEnabled(bool on)
        {
            _agentEnabled = on;
            if (on) { _agentSession ??= new AgentSession(AgentSession.NewToken()); StartAgentIfEnabled(); }
            else StopAgent();
            SaveSettings();
        }

        public void SetAgentDevMode(bool on) { _agentDevMode = on; SaveSettings(); }

        // ---- the executor -----------------------------------------------------------------------------------------------
        // Called by a transport on a background thread. Marshals to the UI thread, decides, asks for consent if due, runs the action,
        // and writes a content-free receipt. Always returns an AgentResult (never throws out to the transport).
        private Task<AgentResult> AgentExecuteAsync(AgentCapability cap, AgentCallArgs a)
        {
            var disp = Application.Current?.Dispatcher;
            if (disp == null) return Task.FromResult(AgentResult.Error("browser is shutting down"));
            return disp.InvokeAsync(() => AgentRunOnUiAsync(cap, a)).Task.Unwrap();
        }

        private async Task<AgentResult> AgentRunOnUiAsync(AgentCapability cap, AgentCallArgs a)
        {
            try
            {
                if (!_agentEnabled || _agentSession == null) return AgentResult.Error("agent control is off");

                var windowLevel = cap is AgentCapability.TabsList or AgentCapability.TabCreate or AgentCapability.TabClose or AgentCapability.Resize;
                var tab = Active;
                string? url = windowLevel ? null : tab?.CurrentUrl;
                bool isInternal = !windowLevel && (tab?.IsInternal ?? true);
                bool isPrivate = (windowLevel ? _tabs.Any(t => t.Private && ReferenceEquals(t, Active)) : (tab?.Private ?? false));

                // rate limit (per origin, or "window" for window-level actions)
                var rateKey = PasswordRules.OriginOf(url) ?? "window";
                if (!_agentSession.WithinRate(rateKey, DateTime.UtcNow)) return AgentResult.Error("rate limit: too many actions on " + rateKey + "; slow down");

                // decide, asking for consent when the pure layer says consent is due
                bool granted = _agentSession.IsGrantedUrl(url);
                bool confirmed = false;
                var req = new AgentRequest(cap, url, isInternal, isPrivate, granted, confirmed);
                var verdict = AgentRules.Decide(_agentEnabled, _agentDevMode, req);

                if (verdict == AgentVerdict.Deny)
                    return AgentResult.Error("refused: " + cap + " is not allowed here (private tabs, Recognition's own pages, the vault and non-web pages are always off-limits)");

                if (verdict == AgentVerdict.NeedConsent)
                {
                    if (AgentRules.NeedsPerCallConfirm(cap))
                    {
                        var preview = cap == AgentCapability.RunScript ? Trunc(a.Script ?? "", 400) : ("file: " + Trunc(a.Path ?? "", 400));
                        if (!AgentConsent("Claude wants to " + (cap == AgentCapability.RunScript ? "run a script" : "upload a file") + " on " + (PasswordRules.OriginOf(url) ?? "this page"),
                                          preview + "\n\nAllow just this one time?"))
                            return AgentResult.Error("you declined this " + cap);
                        confirmed = true;
                    }
                    else
                    {
                        var origin = PasswordRules.OriginOf(url);
                        if (origin == null) return AgentResult.Error("no page to act on");
                        if (!AgentConsent("Allow Claude to act on " + origin + "?",
                                          "Claude will be able to read and act on pages at " + origin + " until you stop agent control. It can never see your private tabs, Recognition's own pages, or your saved passwords."))
                            return AgentResult.Error("you did not grant " + origin);
                        _agentSession.Grant(url);
                        granted = true;
                    }
                    // re-decide now that consent is in hand
                    req = new AgentRequest(cap, url, isInternal, isPrivate, granted, confirmed);
                    if (AgentRules.Decide(_agentEnabled, _agentDevMode, req) != AgentVerdict.Allow)
                        return AgentResult.Error("still refused after consent");
                }

                var result = await AgentPerformAsync(cap, a, tab);
                _actions?.Append("agent." + cap.ToString().ToLowerInvariant(), (PasswordRules.OriginOf(url) ?? (windowLevel ? "window" : "unknown")) + ":" + (result.IsError ? "fail" : "ok"));
                return result;
            }
            catch (Exception ex) { return AgentResult.Error("action error: " + ex.Message); }
        }

        // Governed raw-CDP READ passthrough (Phase 3 limit fix): forwards a read-only structure method to WebView2's real CDP engine so
        // a CDP client gets a faithful DOM/layout/accessibility answer — but only on a granted origin, never a private/internal page, and
        // the origin grant is the gate (prompted once, like any read). The allowlist of methods lives in CdpMap; this never forwards a
        // method that returns a resource body, cookies, storage or network traffic.
        private Task<AgentResult> AgentCdpReadAsync(string method, string paramsJson)
        {
            var disp = Application.Current?.Dispatcher;
            if (disp == null) return Task.FromResult(AgentResult.Error("browser is shutting down"));
            return disp.InvokeAsync(() => AgentCdpReadOnUiAsync(method, paramsJson)).Task.Unwrap();
        }

        private async Task<AgentResult> AgentCdpReadOnUiAsync(string method, string paramsJson)
        {
            try
            {
                if (!_agentEnabled || _agentSession == null) return AgentResult.Error("agent control is off");
                var tab = Active; var core = tab?.Web?.CoreWebView2;
                if (tab == null || core == null) return AgentResult.Error("no active page");
                var url = tab.CurrentUrl;
                if (AgentRules.IsForbiddenTarget(url, tab.IsInternal, tab.Private)) return AgentResult.Error("refused: this page is off-limits to the agent");
                if (!_agentSession.WithinRate(PasswordRules.OriginOf(url) ?? "window", DateTime.UtcNow)) return AgentResult.Error("rate limit: slow down");

                var req = new AgentRequest(AgentCapability.ReadPage, url, tab.IsInternal, tab.Private, _agentSession.IsGrantedUrl(url), false);
                var v = AgentRules.Decide(_agentEnabled, _agentDevMode, req);
                if (v == AgentVerdict.Deny) return AgentResult.Error("refused");
                if (v == AgentVerdict.NeedConsent)
                {
                    var origin = PasswordRules.OriginOf(url); if (origin == null) return AgentResult.Error("no page to read");
                    if (!AgentConsent("Allow Claude to read " + origin + "?", "Claude will read page structure (the DOM, layout and accessibility tree) at " + origin + ", and act on it until you stop agent control. It can never see your private tabs, Recognition's own pages, or your saved passwords."))
                        return AgentResult.Error("you did not grant " + origin);
                    _agentSession.Grant(url);
                }
                var raw = await core.CallDevToolsProtocolMethodAsync(method, string.IsNullOrEmpty(paramsJson) ? "{}" : paramsJson);
                _actions?.Append("agent.cdpread", (PasswordRules.OriginOf(url) ?? "unknown") + ":ok");
                return AgentResult.Ok(raw ?? "{}");
            }
            catch (Exception ex) { return AgentResult.Error("read error: " + ex.Message); }
        }

        // Runs the concrete action. Assumes the decision already allowed it.
        private async Task<AgentResult> AgentPerformAsync(AgentCapability cap, AgentCallArgs a, BrowserTab? tab)
        {
            CoreWebView2? core = tab?.Web?.CoreWebView2;
            switch (cap)
            {
                case AgentCapability.TabsList:
                {
                    var list = _tabs.Where(t => !t.IsInternal && !t.Private).Select(t => new { id = AgentTabId(t), title = t.CurrentTitle, url = t.CurrentUrl, active = ReferenceEquals(t, Active) }).ToList();
                    return AgentResult.Ok(JsonSerializer.Serialize(list));
                }
                case AgentCapability.Navigate:
                    if (tab == null || a.Url == null) return AgentResult.Error("no active tab");
                    NavigateTab(tab, a.Url);
                    return AgentResult.Ok("navigating to " + a.Url);
                case AgentCapability.GetText:
                {
                    if (core == null) return AgentResult.Error("no active page");
                    var txt = await core.ExecuteScriptAsync("document.body?document.body.innerText:''");
                    return AgentResult.Ok(Trunc(Unjson(txt), 20000));
                }
                case AgentCapability.ReadPage:
                {
                    if (core == null) return AgentResult.Error("no active page");
                    var outline = await core.ExecuteScriptAsync(DomOutlineScript);
                    return AgentResult.Ok(Trunc(Unjson(outline), 40000));
                }
                case AgentCapability.Find:
                {
                    if (core == null || a.Query == null) return AgentResult.Error("no active page");
                    var js = "(function(q){try{var n=0,t=document.body?document.body.innerText:'';var i=0;while((i=t.toLowerCase().indexOf(q.toLowerCase(),i))>=0){n++;i+=q.length||1;}return JSON.stringify({count:n});}catch(e){return JSON.stringify({count:0});}})(" + JsonSerializer.Serialize(a.Query) + ")";
                    return AgentResult.Ok(Unjson(await core.ExecuteScriptAsync(js)));
                }
                case AgentCapability.Screenshot:
                {
                    if (core == null) return AgentResult.Error("no active page");
                    using var ms = new MemoryStream();
                    await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                    return AgentResult.Image(Convert.ToBase64String(ms.ToArray()));
                }
                case AgentCapability.Click:
                {
                    if (core == null) return AgentResult.Error("no active page");
                    var p = "{\"type\":\"mousePressed\",\"x\":" + a.X + ",\"y\":" + a.Y + ",\"button\":\"left\",\"clickCount\":1}";
                    var rel = "{\"type\":\"mouseReleased\",\"x\":" + a.X + ",\"y\":" + a.Y + ",\"button\":\"left\",\"clickCount\":1}";
                    await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", p);
                    await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", rel);
                    return AgentResult.Ok("clicked at " + a.X + "," + a.Y);
                }
                case AgentCapability.Type:
                {
                    if (core == null || a.Text == null) return AgentResult.Error("no active page");
                    await core.CallDevToolsProtocolMethodAsync("Input.insertText", "{\"text\":" + JsonSerializer.Serialize(a.Text) + "}");
                    if (a.Submit)
                    {
                        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyDown\",\"key\":\"Enter\",\"code\":\"Enter\",\"windowsVirtualKeyCode\":13}");
                        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyUp\",\"key\":\"Enter\",\"code\":\"Enter\",\"windowsVirtualKeyCode\":13}");
                    }
                    return AgentResult.Ok("typed " + a.Text.Length + " character(s)" + (a.Submit ? " and submitted" : ""));
                }
                case AgentCapability.Scroll:
                    if (core == null) return AgentResult.Error("no active page");
                    await core.ExecuteScriptAsync("window.scrollBy(" + a.Dx + "," + a.Dy + ")");
                    return AgentResult.Ok("scrolled");
                case AgentCapability.FormInput:
                {
                    if (core == null || a.Selector == null) return AgentResult.Error("no active page");
                    var js = "(function(s,v){try{var e=document.querySelector(s);if(!e)return JSON.stringify({ok:false,why:'no element'});e.focus();e.value=v;e.dispatchEvent(new Event('input',{bubbles:true}));e.dispatchEvent(new Event('change',{bubbles:true}));return JSON.stringify({ok:true});}catch(e){return JSON.stringify({ok:false,why:String(e)});}})(" + JsonSerializer.Serialize(a.Selector) + "," + JsonSerializer.Serialize(a.Value ?? "") + ")";
                    return AgentResult.Ok(Unjson(await core.ExecuteScriptAsync(js)));
                }
                case AgentCapability.FileUpload:
                {
                    if (core == null || a.Selector == null || a.Path == null) return AgentResult.Error("no active page");
                    await core.CallDevToolsProtocolMethodAsync("DOM.enable", "{}");
                    var docJson = await core.CallDevToolsProtocolMethodAsync("DOM.getDocument", "{\"depth\":0}");
                    using var dd = JsonDocument.Parse(docJson);
                    var rootId = dd.RootElement.GetProperty("root").GetProperty("nodeId").GetInt32();
                    var qJson = await core.CallDevToolsProtocolMethodAsync("DOM.querySelector", "{\"nodeId\":" + rootId + ",\"selector\":" + JsonSerializer.Serialize(a.Selector) + "}");
                    using var qd = JsonDocument.Parse(qJson);
                    var nodeId = qd.RootElement.TryGetProperty("nodeId", out var nid) ? nid.GetInt32() : 0;
                    if (nodeId == 0) return AgentResult.Error("no file input matched " + a.Selector);
                    await core.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles", "{\"files\":[" + JsonSerializer.Serialize(a.Path) + "],\"nodeId\":" + nodeId + "}");
                    return AgentResult.Ok("attached file to " + a.Selector);
                }
                case AgentCapability.RunScript:
                {
                    if (core == null || a.Script == null) return AgentResult.Error("no active page");
                    var res = await core.ExecuteScriptAsync(a.Script);
                    return AgentResult.Ok(Trunc(res ?? "null", 20000));
                }
                case AgentCapability.ReadConsole:
                {
                    if (tab == null || core == null) return AgentResult.Error("no active page");
                    AgentAttachBuffers(tab, core);
                    var buf = _agentConsole.TryGetValue(tab, out var l) ? l : new List<string>();
                    var items = string.IsNullOrEmpty(a.Pattern) ? buf : buf.Where(x => x.Contains(a.Pattern!, StringComparison.OrdinalIgnoreCase)).ToList();
                    return AgentResult.Ok(JsonSerializer.Serialize(items.TakeLast(200)));
                }
                case AgentCapability.ReadNetwork:
                {
                    if (tab == null || core == null) return AgentResult.Error("no active page");
                    AgentAttachBuffers(tab, core);
                    var buf = _agentNetwork.TryGetValue(tab, out var l) ? l : new List<string>();
                    return AgentResult.Ok(JsonSerializer.Serialize(buf.TakeLast(200)));
                }
                case AgentCapability.TabCreate:
                {
                    var t = await NewTabCoreAsync("New tab");
                    if (t == null) return AgentResult.Error("could not open a tab");
                    Tabs.SelectedItem = t.Item; ShowActiveWebView();
                    if (!string.IsNullOrEmpty(a.Url)) NavigateTab(t, a.Url!);
                    return AgentResult.Ok(JsonSerializer.Serialize(new { id = AgentTabId(t) }));
                }
                case AgentCapability.TabClose:
                {
                    var t = _tabs.FirstOrDefault(x => AgentTabId(x) == a.TabId);
                    if (t == null) return AgentResult.Error("no tab with id " + a.TabId);
                    if (t.Private || t.IsInternal) return AgentResult.Error("that tab cannot be closed by the agent");
                    CloseTab(t);
                    return AgentResult.Ok("closed " + a.TabId);
                }
                case AgentCapability.Resize:
                    Width = Math.Clamp(a.Width, 200, 10000); Height = Math.Clamp(a.Height, 200, 10000);
                    return AgentResult.Ok("resized");
                default:
                    return AgentResult.Error("unsupported");
            }
        }

        // Called on every completed navigation: if agent control is on, make sure console/network capture is attached to this tab and
        // start the page with empty buffers, so a later read reflects the current page rather than everything since the agent came on.
        private void AgentOnNavigated(BrowserTab tab)
        {
            if (!_agentEnabled || tab.IsInternal || tab.Private) return;
            var core = tab.Web?.CoreWebView2; if (core == null) return;
            AgentAttachBuffers(tab, core);
            if (_agentConsole.TryGetValue(tab, out var c)) c.Clear();
            if (_agentNetwork.TryGetValue(tab, out var n)) n.Clear();
        }

        private void AgentAttachBuffers(BrowserTab tab, CoreWebView2 core)
        {
            if (!_agentConsoleAttached.Add(tab)) return;
            _agentConsole[tab] = new List<string>();
            _agentNetwork[tab] = new List<string>();
            try
            {
                _ = core.CallDevToolsProtocolMethodAsync("Log.enable", "{}");
                _ = core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
                _ = core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
                var con = core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled");
                con.DevToolsProtocolEventReceived += (_, e) => { var l = _agentConsole[tab]; if (l.Count < 500) l.Add(Trunc(e.ParameterObjectAsJson, 2000)); };
                var log = core.GetDevToolsProtocolEventReceiver("Log.entryAdded");
                log.DevToolsProtocolEventReceived += (_, e) => { var l = _agentConsole[tab]; if (l.Count < 500) l.Add(Trunc(e.ParameterObjectAsJson, 2000)); };
                var net = core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
                net.DevToolsProtocolEventReceived += (_, e) =>
                {
                    try { using var d = JsonDocument.Parse(e.ParameterObjectAsJson); var rq = d.RootElement.GetProperty("request"); var l = _agentNetwork[tab]; if (l.Count < 500) l.Add(JsonSerializer.Serialize(new { method = rq.GetProperty("method").GetString(), url = Trunc(rq.GetProperty("url").GetString() ?? "", 400) })); }
                    catch { }
                };
            }
            catch { }
        }

        private string AgentTabId(BrowserTab t)
        {
            if (_agentTabIds.TryGetValue(t, out var v)) return (string)v;
            var id = "t" + (++_agentTabCounter);
            _agentTabIds.Add(t, id);
            return id;
        }

        private bool AgentConsent(string title, string body)
            => MessageBox.Show(this, body, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        // ---- Settings section (appended to the Customize page) + its messages -------------------------------------------
        private string AgentSettingsHtml()
        {
            var on = _agentEnabled; var dev = _agentDevMode;
            var ep = _agentServer is { Running: true } ? _agentServer.Url : "(starts when you turn it on)";
            var sb = new StringBuilder();
            sb.Append("<div class='sec'>Agent control (Claude)</div>");
            sb.Append("<div class='row'><div><div class='t'>Let Claude drive this browser</div><div class='u'>Exposes a local, loopback-only endpoint an external Claude can use to read and act on web pages — the same idea as the Claude extension for Chrome. <b>Off by default.</b> Each site must be granted before Claude reads or acts there; running a page script or uploading a file asks you every time; Claude can never see your private tabs, Recognition's own pages, or your saved passwords.</div></div>" +
                      "<div class='ts'><a class='btn" + (on ? "" : " ghost") + "' onclick=\"send('cz-agent:" + (on ? "off" : "on") + "')\">" + (on ? "On" : "Off") + "</a></div></div>");
            if (on)
            {
                sb.Append("<div class='row'><div><div class='t'>Developer mode</div><div class='u'>Skips the repeat per-site prompt for a site you already granted this session. It never skips the script/upload confirmation and never changes what is off-limits.</div></div>" +
                          "<div class='ts'><a class='btn" + (dev ? "" : " ghost") + "' onclick=\"send('cz-agent:dev-" + (dev ? "off" : "on") + "')\">" + (dev ? "On" : "Off") + "</a></div></div>");
                var cdp = _cdpServer is { Running: true } ? _cdpServer.DiscoveryUrl : "(starts with agent control)";
                sb.Append("<div class='row'><div><div class='t'>Connection</div><div class='u'>Two ways in, both loopback-only and sharing the same token and site grants. <b>MCP</b> (add to your Claude MCP config): <code>" + Esc(ep) + "</code>. <b>CDP</b> (for DevTools-protocol tools; a governed subset — navigate, screenshot, evaluate, click, scroll, insert-text): <code>" + Esc(cdp) + "</code>. The bearer token is shown once here and changes on every launch.</div></div>" +
                          "<div class='ts'><a class='btn ghost' onclick=\"send('cz-agent:token')\">Reveal token</a></div></div>");
                sb.Append("<div class='row'><div><div class='t'>Stop and revoke (kill switch)</div><div class='u'>Turns agent control off and forgets every site grant immediately.</div></div>" +
                          "<div class='ts'><a class='btn' onclick=\"send('cz-agent:kill')\">Stop now</a></div></div>");
            }
            return sb.ToString();
        }

        private void HandleAgentSetting(BrowserTab tab, string cmd, Action<string> note)
        {
            switch (cmd)
            {
                case "on": SetAgentEnabled(true); note(_agentServer is { Running: true } ? "agent control on — " + _agentServer.Url : "agent control on"); break;
                case "off": SetAgentEnabled(false); note("agent control off"); break;
                case "dev-on": SetAgentDevMode(true); note("developer mode on"); break;
                case "dev-off": SetAgentDevMode(false); note("developer mode off"); break;
                case "kill": AgentKill(); note("agent control stopped; all site grants revoked"); break;
                case "token":
                    note(_agentSession != null && _agentServer is { Running: true }
                        ? "MCP " + _agentServer.Url + (_cdpServer is { Running: true } ? "   ·   CDP " + _cdpServer.WsUrl : "") + "   ·   bearer token: " + _agentSession.Token
                        : "turn agent control on first");
                    break;
            }
            RefreshCustomize(tab);
        }

        private static string Trunc(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
        // ExecuteScriptAsync returns a JSON-encoded value; turn a JSON string back into text, else return as-is.
        private static string Unjson(string? json)
        {
            if (string.IsNullOrEmpty(json)) return "";
            try { using var d = JsonDocument.Parse(json); return d.RootElement.ValueKind == JsonValueKind.String ? d.RootElement.GetString() ?? "" : json; }
            catch { return json; }
        }

        // A compact DOM/role outline for ReadPage: interactive and landmark elements with their text, depth-capped.
        private const string DomOutlineScript = @"
(function(){try{
  var out=[],max=1500;
  function label(e){var t=(e.getAttribute('aria-label')||e.getAttribute('placeholder')||e.getAttribute('name')||e.getAttribute('alt')||(e.innerText||'').trim()).slice(0,120);return t;}
  var sel='a,button,input,select,textarea,[role],h1,h2,h3,label,[contenteditable]';
  var els=document.querySelectorAll(sel);
  for(var i=0;i<els.length&&out.length<max;i++){var e=els[i];var r=e.getBoundingClientRect();if(r.width<=0||r.height<=0)continue;var tag=e.tagName.toLowerCase();var role=e.getAttribute('role')||'';out.push((role?('['+role+'] '):('<'+tag+'> '))+label(e));}
  return out.join('\n');
}catch(e){return 'read error: '+e;}})();";
    }
}
