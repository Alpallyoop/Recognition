using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Recognition.Browser
{
    // The embedded assistant's protocol glue (proposal 0012, Phase 5), kept pure so it is tested without a network or a key. It turns the
    // governed agent tools (AgentProtocol) into the Anthropic Messages "tools" array, reads tool-use blocks out of a model response, and
    // shapes tool-result blocks to send back. The assistant is just another caller of the SAME governed executor — it gets no capability
    // the MCP/CDP frontends don't, and every tool it calls still goes through AgentRules.
    internal static class AssistantProtocol
    {
        public const string DefaultModel = "claude-sonnet-4-5";
        public const string ApiVersion = "2023-06-01";
        public const int MaxToolIterations = 8;

        public const string SystemPrompt =
            "You are Claude, acting inside Recognition, a governed browser, as an assistant to the person at the keyboard. " +
            "Use the recognition_* tools to see and act on the page they are viewing. You cannot see private tabs, Recognition's own " +
            "pages, or their saved passwords, and some actions ask them to confirm — that is expected; explain what you are doing. " +
            "Prefer reading (get text, screenshot, read page) before acting, and keep the person informed in plain language.";

        // The Anthropic "tools" array, one entry per governed capability, reusing each tool's existing JSON Schema.
        public static string AnthropicToolsJson()
            => "[" + string.Join(",", AgentProtocol.Tools().Select(t =>
                   "{\"name\":" + JsonSerializer.Serialize(t.Name) + ",\"description\":" + JsonSerializer.Serialize(t.Description) + ",\"input_schema\":" + t.InputSchemaJson + "}")) + "]";

        // The tool_use blocks in a model response's content array.
        public static List<(string Id, string Name, JsonElement Input)> ParseToolUses(JsonElement content)
        {
            var res = new List<(string, string, JsonElement)>();
            if (content.ValueKind != JsonValueKind.Array) return res;
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                if (block.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String && ty.GetString() == "tool_use")
                {
                    var id = block.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                    var name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var input = block.TryGetProperty("input", out var inp) ? inp : default;
                    if (id.Length > 0 && name.Length > 0) res.Add((id, name, input));
                }
            }
            return res;
        }

        // The concatenated text blocks in a response's content (what we show the person).
        public static string TextFrom(JsonElement content)
        {
            if (content.ValueKind != JsonValueKind.Array) return "";
            var parts = new List<string>();
            foreach (var block in content.EnumerateArray())
                if (block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var ty) && ty.GetString() == "text" && block.TryGetProperty("text", out var tx))
                    parts.Add(tx.GetString() ?? "");
            return string.Join("\n", parts);
        }

        // A tool_result content block (text) for a tool_use id.
        public static string ToolResultText(string toolUseId, string text, bool isError)
            => "{\"type\":\"tool_result\",\"tool_use_id\":" + JsonSerializer.Serialize(toolUseId) +
               ",\"is_error\":" + (isError ? "true" : "false") +
               ",\"content\":[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(string.IsNullOrEmpty(text) ? "(no output)" : text) + "}]}";

        // A tool_result content block carrying a screenshot image.
        public static string ToolResultImage(string toolUseId, string base64Png)
            => "{\"type\":\"tool_result\",\"tool_use_id\":" + JsonSerializer.Serialize(toolUseId) +
               ",\"content\":[{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":" + JsonSerializer.Serialize(base64Png) + "}}]}";

        public static bool IsEndTurn(string? stopReason) => stopReason is null or "end_turn" or "max_tokens" or "stop_sequence";
    }
}
