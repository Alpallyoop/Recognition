using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Recognition.Browser
{
    // The MCP tool surface for the agent adapter (proposal 0012, Phase 2). Pure: it defines the tools an external Claude sees, parses
    // and VALIDATES a tool call into a typed request, and shapes JSON-RPC results — no WPF, no WebView2, no sockets. The hosted server
    // (AgentServer.cs) is a thin frontend over this; the executor (MainWindow.Agent.cs) runs what this validates, but only after
    // AgentRules.Decide allows it. Parsing here never trusts the caller: every field is bounded and typed, and an unknown tool or a bad
    // argument is an error, not a guess.

    internal readonly record struct AgentCallArgs(
        string? Url, int X, int Y, int Dx, int Dy, string? Text, bool Submit,
        string? Selector, string? Value, string? Script, string? Query, string? Pattern,
        string? Path, string? TabId, int Width, int Height, bool FullPage);

    internal sealed record AgentTool(string Name, AgentCapability Capability, string Description, string InputSchemaJson);

    internal static class AgentProtocol
    {
        public const int MaxUrl = 2048, MaxText = 100_000, MaxSelector = 1024, MaxScript = 100_000, MaxQuery = 1024, MaxPattern = 256, MaxPath = 4096;
        public const string ProtocolVersion = "2024-11-05";
        public const string ServerName = "recognition";

        // The catalogue, one entry per capability. Names are stable and prefixed so they don't collide with other MCP servers.
        private static readonly AgentTool[] Catalog =
        {
            new("recognition_tabs", AgentCapability.TabsList, "List the open tabs Claude may act on (private and internal pages are never listed).", Schema()),
            new("recognition_navigate", AgentCapability.Navigate, "Navigate the active tab to an http(s) URL.", Schema(("url", "string", true))),
            new("recognition_read_page", AgentCapability.ReadPage, "Read the active page's accessibility/DOM tree as structured text.", Schema()),
            new("recognition_get_text", AgentCapability.GetText, "Get the visible text of the active page.", Schema()),
            new("recognition_find", AgentCapability.Find, "Find occurrences of a query string on the active page.", Schema(("query", "string", true))),
            new("recognition_screenshot", AgentCapability.Screenshot, "Capture a screenshot of the active page.", Schema(("full_page", "boolean", false))),
            new("recognition_click", AgentCapability.Click, "Click at viewport coordinates (x, y) on the active page.", Schema(("x", "integer", true), ("y", "integer", true))),
            new("recognition_type", AgentCapability.Type, "Type text into the active element; optionally submit.", Schema(("text", "string", true), ("submit", "boolean", false))),
            new("recognition_scroll", AgentCapability.Scroll, "Scroll the active page by (dx, dy) pixels.", Schema(("dy", "integer", true), ("dx", "integer", false))),
            new("recognition_form_input", AgentCapability.FormInput, "Set a form field (by CSS selector) to a value.", Schema(("selector", "string", true), ("value", "string", true))),
            new("recognition_file_upload", AgentCapability.FileUpload, "Attach a local file (by path) to a file input (by selector). Asks you to confirm every time.", Schema(("selector", "string", true), ("path", "string", true))),
            new("recognition_run_script", AgentCapability.RunScript, "Run JavaScript in the active page and return its result. Asks you to confirm every time.", Schema(("script", "string", true))),
            new("recognition_console", AgentCapability.ReadConsole, "Read recent console messages from the active page, optionally filtered.", Schema(("pattern", "string", false))),
            new("recognition_network", AgentCapability.ReadNetwork, "Read recent network requests from the active page (bodies redacted).", Schema()),
            new("recognition_tab_create", AgentCapability.TabCreate, "Open a new tab, optionally at an http(s) URL.", Schema(("url", "string", false))),
            new("recognition_tab_close", AgentCapability.TabClose, "Close a tab by its id (from recognition_tabs).", Schema(("tab_id", "string", true))),
            new("recognition_resize", AgentCapability.Resize, "Resize the window to width x height.", Schema(("width", "integer", true), ("height", "integer", true))),
        };

        public static IReadOnlyList<AgentTool> Tools() => Catalog;

        public static bool TryCapability(string toolName, out AgentCapability cap)
        {
            foreach (var t in Catalog) if (t.Name == toolName) { cap = t.Capability; return true; }
            cap = default; return false;
        }

        // Parse + validate a tools/call. Returns false with a human error on anything malformed; never throws.
        public static bool TryParse(string toolName, JsonElement args, out AgentCapability cap, out AgentCallArgs parsed, out string error)
        {
            parsed = default; error = "";
            if (!TryCapability(toolName, out cap)) { error = "unknown tool: " + toolName; return false; }
            if (args.ValueKind != JsonValueKind.Object && args.ValueKind != JsonValueKind.Undefined && args.ValueKind != JsonValueKind.Null)
            { error = "arguments must be an object"; return false; }

            string? S(string k, int max, out bool bad) { bad = false; if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String) { var s = v.GetString() ?? ""; if (s.Length > max || s.Any(char.IsControl) && k != "script" && k != "text" && k != "value") { bad = true; return null; } return s; } return null; }
            bool B(string k) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
            bool TryI(string k, out int val) { val = 0; if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out val)) return true; return false; }

            var a = new AgentCallArgs();
            switch (cap)
            {
                case AgentCapability.Navigate:
                {
                    var url = S("url", MaxUrl, out var bad); if (bad || string.IsNullOrEmpty(url)) { error = "url is required"; return false; }
                    if (!IsWebUrl(url)) { error = "url must be http(s)"; return false; }
                    a = a with { Url = url }; break;
                }
                case AgentCapability.TabCreate:
                {
                    var url = S("url", MaxUrl, out var bad); if (bad) { error = "url is invalid"; return false; }
                    if (!string.IsNullOrEmpty(url) && !IsWebUrl(url)) { error = "url must be http(s)"; return false; }
                    a = a with { Url = url }; break;
                }
                case AgentCapability.Find:
                {
                    var q = S("query", MaxQuery, out var bad); if (bad || string.IsNullOrEmpty(q)) { error = "query is required"; return false; }
                    a = a with { Query = q }; break;
                }
                case AgentCapability.Click:
                {
                    if (!TryI("x", out var x) || !TryI("y", out var y)) { error = "x and y are required integers"; return false; }
                    if (!InBounds(x, 0, 100_000) || !InBounds(y, 0, 100_000)) { error = "x/y out of range"; return false; }
                    a = a with { X = x, Y = y }; break;
                }
                case AgentCapability.Type:
                {
                    var t = S("text", MaxText, out var bad); if (bad || t == null) { error = "text is required"; return false; }
                    a = a with { Text = t, Submit = B("submit") }; break;
                }
                case AgentCapability.Scroll:
                {
                    if (!TryI("dy", out var dy)) { error = "dy is required"; return false; }
                    TryI("dx", out var dx);
                    if (!InBounds(dy, -1_000_000, 1_000_000) || !InBounds(dx, -1_000_000, 1_000_000)) { error = "scroll out of range"; return false; }
                    a = a with { Dy = dy, Dx = dx }; break;
                }
                case AgentCapability.FormInput:
                {
                    var sel = S("selector", MaxSelector, out var b1); var val = S("value", MaxText, out var b2);
                    if (b1 || string.IsNullOrEmpty(sel)) { error = "selector is required"; return false; }
                    if (b2 || val == null) { error = "value is required"; return false; }
                    a = a with { Selector = sel, Value = val }; break;
                }
                case AgentCapability.FileUpload:
                {
                    var sel = S("selector", MaxSelector, out var b1); var path = S("path", MaxPath, out var b2);
                    if (b1 || string.IsNullOrEmpty(sel)) { error = "selector is required"; return false; }
                    if (b2 || string.IsNullOrEmpty(path)) { error = "path is required"; return false; }
                    a = a with { Selector = sel, Path = path }; break;
                }
                case AgentCapability.RunScript:
                {
                    var sc = S("script", MaxScript, out var bad); if (bad || string.IsNullOrEmpty(sc)) { error = "script is required"; return false; }
                    a = a with { Script = sc }; break;
                }
                case AgentCapability.ReadConsole:
                {
                    var p = S("pattern", MaxPattern, out var bad); if (bad) { error = "pattern is invalid"; return false; }
                    a = a with { Pattern = p }; break;
                }
                case AgentCapability.Screenshot:
                    a = a with { FullPage = B("full_page") }; break;
                case AgentCapability.TabClose:
                {
                    var id = S("tab_id", 64, out var bad); if (bad || string.IsNullOrEmpty(id) || !id.All(c => char.IsLetterOrDigit(c) || c == '-')) { error = "tab_id is required"; return false; }
                    a = a with { TabId = id }; break;
                }
                case AgentCapability.Resize:
                {
                    if (!TryI("width", out var w) || !TryI("height", out var h)) { error = "width and height are required"; return false; }
                    if (!InBounds(w, 200, 10_000) || !InBounds(h, 200, 10_000)) { error = "size out of range"; return false; }
                    a = a with { Width = w, Height = h }; break;
                }
                // no-argument reads
                case AgentCapability.TabsList:
                case AgentCapability.ReadPage:
                case AgentCapability.GetText:
                case AgentCapability.ReadNetwork:
                    break;
                default:
                    error = "tool not available"; return false;
            }
            parsed = a; return true;
        }

        public static bool IsWebUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || url.Length > MaxUrl) return false;
            return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        static bool InBounds(int v, int lo, int hi) => v >= lo && v <= hi;

        // ---- JSON-RPC 2.0 shaping ------------------------------------------------------------------------------------------
        public static string RpcError(long? id, int code, string message)
        {
            var idTok = id is { } i ? i.ToString() : "null";
            return "{\"jsonrpc\":\"2.0\",\"id\":" + idTok + ",\"error\":{\"code\":" + code + ",\"message\":" + JsonStr(message) + "}}";
        }

        public static string RpcTextResult(long id, string text, bool isError = false)
            => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"isError\":" + (isError ? "true" : "false") + ",\"content\":[{\"type\":\"text\",\"text\":" + JsonStr(text) + "}]}}";

        public static string RpcImageResult(long id, string base64Png)
            => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"content\":[{\"type\":\"image\",\"data\":" + JsonStr(base64Png) + ",\"mimeType\":\"image/png\"}]}}";

        public static string RpcInitializeResult(long id)
            => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"protocolVersion\":" + JsonStr(ProtocolVersion) +
               ",\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":" + JsonStr(ServerName) + ",\"version\":\"1\"}}}";

        public static string RpcToolsListResult(long id)
        {
            var items = string.Join(",", Catalog.Select(t =>
                "{\"name\":" + JsonStr(t.Name) + ",\"description\":" + JsonStr(t.Description) + ",\"inputSchema\":" + t.InputSchemaJson + "}"));
            return "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"tools\":[" + items + "]}}";
        }

        static string Schema(params (string Name, string Type, bool Required)[] props)
        {
            if (props.Length == 0) return "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
            var p = string.Join(",", props.Select(x => JsonStr(x.Name) + ":{\"type\":" + JsonStr(x.Type) + "}"));
            var req = props.Where(x => x.Required).Select(x => JsonStr(x.Name)).ToList();
            var reqJson = req.Count > 0 ? ",\"required\":[" + string.Join(",", req) + "]" : "";
            return "{\"type\":\"object\",\"properties\":{" + p + "}" + reqJson + ",\"additionalProperties\":false}";
        }

        public static string JsonStr(string s) => JsonSerializer.Serialize(s);
    }
}
