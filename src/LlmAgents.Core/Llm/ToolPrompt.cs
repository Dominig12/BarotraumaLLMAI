using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace LlmAgents.Llm;

/// <summary>
/// toolMode = "prompt": instead of the API's native tool calling, the tools are described in the system
/// prompt and the model answers with plain JSON. Works with any chat model, including small local ones
/// whose server has no tool-call template.
/// </summary>
public static class ToolPrompt
{
    public static string Build(string system, IReadOnlyList<ToolSpec> tools)
    {
        var sb = new StringBuilder(system);
        sb.Append("\n\nYou act ONLY by replying with a single JSON object, nothing else (no prose, no markdown):\n");
        sb.Append("{\"calls\": [{\"name\": \"<tool>\", \"arguments\": {<arguments>}}]}\n");
        sb.Append("Use an empty list if you do nothing. Available tools:\n");
        foreach (var t in tools)
        {
            sb.Append("- ").Append(t.Name).Append('(');
            var props = t.Parameters["properties"] as JsonObject;
            var required = (t.Parameters["required"] as JsonArray)?.Select(r => r?.ToString()).ToHashSet() ?? new HashSet<string?>();
            if (props != null)
            {
                sb.Append(string.Join(", ", props.Select(p =>
                {
                    var type = JsonUtil.Str(p.Value?["type"]) ?? "string";
                    var en = p.Value?["enum"] is JsonArray e ? " one of " + string.Join("|", e.Select(x => x?.ToString())) : "";
                    return $"{p.Key}: {type}{en}{(required.Contains(p.Key) ? "" : " (optional)")}";
                })));
            }
            sb.Append("): ").Append(t.Description).Append('\n');
        }
        sb.Append("Example: {\"calls\": [{\"name\": \"say\", \"arguments\": {\"text\": \"On my way!\"}}, {\"name\": \"fix_leaks\", \"arguments\": {}}]}");
        return sb.ToString();
    }
}
