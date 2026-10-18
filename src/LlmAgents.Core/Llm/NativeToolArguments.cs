using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LlmAgents.Llm;

/// <summary>A malformed native call is a failed response, never an invented empty argument object.</summary>
internal static class NativeToolArguments
{
    public static JsonObject Read(JsonNode? node, bool allowJsonString)
    {
        if (node is JsonObject obj) return Copy(obj);
        if (allowJsonString && node is JsonValue value && value.TryGetValue<string>(out var json))
        {
            try
            {
                if (JsonNode.Parse(json) is JsonObject parsed) return Copy(parsed);
            }
            catch (JsonException e) { throw new FormatException("tool arguments must be a complete valid JSON object", e); }
        }
        throw new FormatException("tool arguments must be a JSON object; use {} for a tool with no arguments");
    }

    static JsonObject Copy(JsonObject obj)
    {
        try { ReadContainers(obj); return (JsonObject)obj.DeepClone(); }
        catch (ArgumentException e) { throw new FormatException("tool arguments contain duplicate JSON keys", e); }
    }
    static void ReadContainers(JsonNode? node)
    {
        if (node is JsonObject obj) foreach (var pair in obj) ReadContainers(pair.Value);
        else if (node is JsonArray array) foreach (var item in array) ReadContainers(item);
    }
}
