using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LlmAgents.Llm;

/// <summary>
/// Reads explicit JSON tool calls for providers running in prompt mode. The entire answer must be valid JSON;
/// prose, invented aliases, positional arguments and partial replies never become executable actions.
/// </summary>
public static class TextToolCallParser
{
    public static List<ToolCall> Parse(string? text, IReadOnlyList<ToolSpec> tools)
    {
        if (string.IsNullOrWhiteSpace(text) || tools.Count == 0) return new List<ToolCall>();
        try { return ParseJson(JsonNode.Parse(text!), tools); }
        catch (JsonException) { return new List<ToolCall>(); }
        // JsonNode materializes objects lazily: duplicate JSON keys can fail only when they are read.
        catch (ArgumentException) { return new List<ToolCall>(); }
    }

    static List<ToolCall> ParseJson(JsonNode? node, IReadOnlyList<ToolSpec> tools)
    {
        var result = new List<ToolCall>();
        ReadContainers(node);
        var names = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        JsonArray? calls = node as JsonArray;
        if (node is JsonObject obj)
        {
            var wrappers = new[] { "calls", "actions", "tool_calls" }.Where(obj.ContainsKey).ToList();
            if (wrappers.Count > 0)
            {
                if (wrappers.Count != 1 || obj[wrappers[0]] is not JsonArray list) return result;
                calls = list;
            }
            else
            {
                if (TryRead(obj, names, out var call)) result.Add(call!);
                return result;
            }
        }
        if (calls == null) return result;

        foreach (var item in calls)
        {
            // A malformed entry invalidates the decision. Never run just the convenient fragment of a plan.
            if (item is not JsonObject callObject || !TryRead(callObject, names, out var call)) return new List<ToolCall>();
            result.Add(call!);
        }
        if (calls.Count == 0 && names.Contains("carry_on"))
            result.Add(new ToolCall("carry_on", new JsonObject()));
        return result;
    }

    static bool TryRead(JsonObject obj, HashSet<string> names, out ToolCall? call)
    {
        call = null;
        // These are explicit structured provider formats, rather than guesses at natural language.
        if (obj["function"] is JsonObject fn) obj = fn;
        var name = JsonUtil.Str(obj["name"]) ?? JsonUtil.Str(obj["tool"]);
        if (name == null || !names.Contains(name)) return false;
        var args = obj["arguments"] ?? obj["parameters"] ?? obj["input"] ?? obj["args"];
        JsonObject? parsed = args as JsonObject;
        if (args is JsonValue value && value.TryGetValue<string>(out var json))
        {
            try { parsed = JsonNode.Parse(json) as JsonObject; }
            catch (JsonException) { return false; }
        }
        // Require the argument object, including {} for tools without parameters. Missing or broken arguments
        // must not silently become defaults (e.g. attack() selecting the nearest target).
        if (parsed == null) return false;
        ReadContainers(parsed);
        call = new ToolCall(name, (JsonObject)parsed.DeepClone());
        return true;
    }

    // Force lazy objects to read all keys before returning executable arguments. Duplicate keys anywhere in
    // a JSON decision (including a JSON-encoded arguments object) are ambiguous and must fail inside Parse.
    static void ReadContainers(JsonNode? node)
    {
        if (node is JsonObject obj)
            foreach (var pair in obj) ReadContainers(pair.Value);
        else if (node is JsonArray array)
            foreach (var item in array) ReadContainers(item);
    }
}
