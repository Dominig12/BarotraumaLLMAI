using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
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
        sb.Append("\n\nTOOL CALL WIRE FORMAT\n");
        sb.Append("Reply with exactly one complete valid JSON object covering your entire response. No prose, markdown fences, prefixes, suffixes or simulated function-call text.\n");
        sb.Append("{\"calls\": [{\"name\": \"<tool>\", \"arguments\": {<arguments>}}]}\n");
        sb.Append("The only top-level key is calls, an array. Each call has exactly name (an offered tool's exact name) and arguments (a JSON object, not a quoted JSON string).\n");
        sb.Append("Follow each arguments JSON Schema below, including property descriptions, nested schemas, required fields, enum values, numeric bounds and additionalProperties.\n");
        sb.Append("Use JSON numbers for numeric fields and true/false for boolean fields. Omit unused optional fields; do not use null unless the schema permits it. Never invent an alias or an undeclared argument.\n");
        sb.Append("Order calls as they must execute. Task completion is awaited; lookup results are available only next turn. Use only tools listed below, including inside plan steps.\n");
        sb.Append("Even an argument-free tool needs arguments:{}; a missing arguments object invalidates the entire reply.\n");
        if (tools.Any(t => t.Name == "plan"))
        {
            sb.Append("For plan, put steps INSIDE arguments, never beside name. Within steps, actions use do/args; branches use arrays of nodes.\n");
            // These are executable examples of the wire grammar, built only from the offered tool set.
            // Their arguments are illustrative, not a command to craft an undiscovered recipe.
            if (tools.Any(t => t.Name == "craft") && tools.Any(t => t.Name == "inspect"))
            {
                sb.Append("Plan format example for a previously discovered railgunshell recipe (adapt to the current order; diagnostics in catch do not complete the goal):\n");
                sb.Append("{\"calls\":[{\"name\":\"plan\",\"arguments\":{\"steps\":[{\"try\":[{\"do\":\"craft\",\"args\":{\"item\":\"railgunshell\",\"amount\":2}},{\"do\":\"inspect\",\"args\":{\"kind\":\"items\",\"query\":\"railgunshell\"}}],\"catch\":[{\"do\":\"inspect\",\"args\":{\"kind\":\"machines\"}}]}]}}]}\n");
            }
            else
            {
                sb.Append("Plan format example (adapt the conditions to the current order):\n");
                sb.Append("{\"calls\":[{\"name\":\"plan\",\"arguments\":{\"steps\":[{\"if\":\"health < 50\",\"then\":[{\"wait_until\":\"health >= 50\",\"timeout\":30}],\"else\":[]}]}}]}\n");
            }
        }
        sb.Append("If no action is needed, the valid empty response is {\"calls\":[]}. AVAILABLE TOOLS:\n");
        foreach (var t in tools)
        {
            sb.Append("\nTool name: ").Append(t.Name).Append('\n');
            sb.Append("Description: ").Append(t.Description).Append('\n');
            sb.Append("Arguments JSON Schema: ").Append(t.Parameters.ToJsonString(ReadableJson)).Append('\n');
        }
        // Examples must remain legal for a filtered/allowlisted tool set. Do not teach an unavailable action.
        if (tools.Any(t => t.Name == "carry_on"))
            sb.Append("Example response: {\"calls\":[{\"name\":\"carry_on\",\"arguments\":{}}]}");
        else if (tools.Any(t => t.Name == "say"))
            sb.Append("Example response: {\"calls\":[{\"name\":\"say\",\"arguments\":{\"text\":\"Understood.\"}}]}");
        return sb.ToString();
    }

    static readonly JsonSerializerOptions ReadableJson = new(JsonSerializerOptions.Default)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
