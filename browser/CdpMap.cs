using System;
using System.Text.Json;

namespace Recognition.Browser
{
    // The wrapped CDP surface (proposal 0012, Phase 3). Pure: it decides, for each incoming CDP method, whether Recognition answers it
    // locally (handshake/metadata, no page effect), maps it to one governed AgentCapability (routed through AgentRules + the shared
    // executor, exactly like the MCP server), or refuses it. Raw CDP is never exposed: a method that is not on the mapped or local list
    // is an error. This is deliberately a SUBSET — enough to navigate, screenshot, evaluate, click, scroll and insert text under
    // governance — not a faithful puppeteer target. Low-level domains (DOM.*, Network.getResponseBody, Fetch.*, Storage.*,
    // Input.dispatchKeyEvent, …) are refused; use Runtime.evaluate (which is a governed RunScript, so it asks every time) for DOM work.

    internal enum CdpClass { Local, Mapped, Passthrough, Refused }

    internal static class CdpMap
    {
        // Read-only structure methods that are forwarded verbatim to WebView2's REAL CDP engine (so a client gets a faithful DOM /
        // layout / accessibility / computed-style answer), after an origin grant is checked — exactly the read scope the agent already
        // has via get-text and screenshot. Nothing here returns a resource body, a cookie, storage, or network traffic.
        private static readonly HashSet<string> ReadPassthrough = new(StringComparer.Ordinal)
        {
            "DOM.getDocument", "DOM.describeNode", "DOM.getOuterHTML", "DOM.querySelector", "DOM.querySelectorAll",
            "DOM.getBoxModel", "DOM.getContentQuads", "DOM.getAttributes", "DOM.resolveNode", "DOM.getNodeForLocation",
            "CSS.getComputedStyleForNode", "CSS.getMatchedStylesForNode", "CSS.getInlineStylesForNode",
            "Page.getLayoutMetrics", "Page.getFrameTree", "Page.getNavigationHistory",
            "Accessibility.getFullAXTree", "Accessibility.getPartialAXTree", "Accessibility.queryAXTree",
            "DOMSnapshot.captureSnapshot", "Runtime.getProperties",
        };

        public static CdpClass Classify(string method)
        {
            if (ReadPassthrough.Contains(method)) return CdpClass.Passthrough;
            return method switch
            {
                "Browser.getVersion" or "Target.getTargets" or "Target.setDiscoverTargets" or "Target.attachToTarget"
                    or "Target.setAutoAttach" or "Page.enable" or "Runtime.enable" or "DOM.enable" or "Network.enable"
                    or "CSS.enable" or "Accessibility.enable" or "Log.enable" or "Runtime.runIfWaitingForDebugger"
                    or "Page.setLifecycleEventsEnabled" => CdpClass.Local,
                "Page.navigate" or "Page.captureScreenshot" or "Runtime.evaluate" or "Input.insertText"
                    or "Input.dispatchMouseEvent" or "Input.dispatchKeyEvent" => CdpClass.Mapped,
                _ => CdpClass.Refused,
            };
        }

        // Translate a mapped method's params into a governed capability + args. <noop> means "a valid call that needs no page action"
        // (e.g. a mouse press/move half of a click) — the server returns an empty CDP result without touching the page. Never throws.
        public static bool TryMap(string method, JsonElement prms, out AgentCapability cap, out AgentCallArgs args, out bool noop, out string error)
        {
            cap = default; args = default; noop = false; error = "";
            string S(string k) => prms.ValueKind == JsonValueKind.Object && prms.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            int I(string k) => prms.ValueKind == JsonValueKind.Object && prms.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
            double D(string k) => prms.ValueKind == JsonValueKind.Object && prms.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : 0;

            switch (method)
            {
                case "Page.navigate":
                {
                    var url = S("url");
                    if (!AgentProtocol.IsWebUrl(url)) { error = "url must be http(s)"; return false; }
                    cap = AgentCapability.Navigate; args = new AgentCallArgs { Url = url }; return true;
                }
                case "Page.captureScreenshot":
                    cap = AgentCapability.Screenshot; args = new AgentCallArgs(); return true;
                case "Runtime.evaluate":
                {
                    var expr = S("expression");
                    if (string.IsNullOrEmpty(expr) || expr.Length > AgentProtocol.MaxScript) { error = "expression is required"; return false; }
                    cap = AgentCapability.RunScript; args = new AgentCallArgs { Script = expr }; return true;
                }
                case "Input.insertText":
                {
                    var text = S("text");
                    if (text.Length > AgentProtocol.MaxText) { error = "text too long"; return false; }
                    cap = AgentCapability.Type; args = new AgentCallArgs { Text = text }; return true;
                }
                case "Input.dispatchMouseEvent":
                {
                    var type = S("type");
                    if (type == "mouseReleased")
                    {
                        int x = I("x"), y = I("y");
                        if (x < 0 || y < 0 || x > 100000 || y > 100000) { error = "x/y out of range"; return false; }
                        cap = AgentCapability.Click; args = new AgentCallArgs { X = x, Y = y }; return true;
                    }
                    if (type == "mouseWheel")
                    {
                        cap = AgentCapability.Scroll; args = new AgentCallArgs { Dx = (int)Math.Clamp(D("deltaX"), -1_000_000, 1_000_000), Dy = (int)Math.Clamp(D("deltaY"), -1_000_000, 1_000_000) }; return true;
                    }
                    // mousePressed / mouseMoved / other: a valid no-op (the click is performed on release)
                    noop = true; cap = AgentCapability.Click; args = new AgentCallArgs(); return true;
                }
                case "Input.dispatchKeyEvent":
                {
                    var type = S("type");           // keyDown | keyUp | char | rawKeyDown
                    var text = S("text");           // the printable character(s), when any
                    var keyName = S("key");
                    if ((type == "keyDown" || type == "rawKeyDown") && keyName == "Enter")
                    { cap = AgentCapability.Type; args = new AgentCallArgs { Text = "", Submit = true }; return true; }
                    if ((type == "keyDown" || type == "char") && text.Length > 0 && text.Length <= AgentProtocol.MaxText)
                    { cap = AgentCapability.Type; args = new AgentCallArgs { Text = text }; return true; }
                    // keyUp, modifier-only keyDown, etc.: a valid no-op
                    noop = true; cap = AgentCapability.Type; args = new AgentCallArgs(); return true;
                }
                default:
                    error = "not a mapped method"; return false;
            }
        }

        private const string TargetInfo = "{\"targetId\":\"recognition-page\",\"type\":\"page\",\"title\":\"Recognition\",\"url\":\"\",\"attached\":true,\"canAccessOpener\":false,\"browserContextId\":\"default\"}";

        // The body (the "result" object) for a locally-answered method. Flat session model: one synthetic page target.
        public static string LocalResultBody(string method) => method switch
        {
            "Browser.getVersion" => "{\"protocolVersion\":\"1.3\",\"product\":\"Recognition/agent\",\"revision\":\"\",\"userAgent\":\"Recognition\",\"jsVersion\":\"\"}",
            "Target.getTargets" => "{\"targetInfos\":[" + TargetInfo + "]}",
            "Target.attachToTarget" => "{\"sessionId\":\"recognition\"}",
            _ => "{}",
        };

        // Proactive CDP events to push AFTER a locally-answered method, so a session-based client (e.g. one that discovers and attaches
        // to targets) can proceed. Best-effort toward broad CDP-client support — not a guarantee of full puppeteer page automation.
        public static string[] EventsAfter(string method) => method switch
        {
            "Target.setDiscoverTargets" => new[] { "{\"method\":\"Target.targetCreated\",\"params\":{\"targetInfo\":" + TargetInfo + "}}" },
            "Target.attachToTarget" => new[] { "{\"method\":\"Target.attachedToTarget\",\"params\":{\"sessionId\":\"recognition\",\"targetInfo\":" + TargetInfo + ",\"waitingForDebugger\":false}}" },
            "Target.setAutoAttach" => new[] { "{\"method\":\"Target.attachedToTarget\",\"params\":{\"sessionId\":\"recognition\",\"targetInfo\":" + TargetInfo + ",\"waitingForDebugger\":false}}" },
            _ => Array.Empty<string>(),
        };

        // Shape a successful capability result into the CDP result body the method returns.
        public static string ResultBodyFor(AgentCapability cap, bool isImage, string text, string? base64)
            => cap switch
            {
                AgentCapability.Screenshot => "{\"data\":" + JsonSerializer.Serialize(base64 ?? "") + "}",
                AgentCapability.RunScript => "{\"result\":{\"type\":\"string\",\"value\":" + JsonSerializer.Serialize(text) + "}}",
                AgentCapability.Navigate => "{\"frameId\":\"0\",\"loaderId\":\"0\"}",
                _ => "{}",
            };

        // ---- CDP JSON envelopes ------------------------------------------------------------------------------------------
        public static string Result(long id, string resultBody, string? sessionId = null)
            => "{\"id\":" + id + (sessionId != null ? ",\"sessionId\":" + JsonSerializer.Serialize(sessionId) : "") + ",\"result\":" + resultBody + "}";

        public static string Error(long id, string message, string? sessionId = null)
            => "{\"id\":" + id + (sessionId != null ? ",\"sessionId\":" + JsonSerializer.Serialize(sessionId) : "") + ",\"error\":{\"code\":-32000,\"message\":" + JsonSerializer.Serialize(message) + "}}";
    }
}
