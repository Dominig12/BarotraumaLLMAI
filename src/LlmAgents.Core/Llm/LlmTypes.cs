using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LlmAgents.Llm;

public sealed record ToolSpec(string Name, string Description, JsonObject Parameters);

public sealed record ToolCall(string Name, JsonObject Arguments);

/// <summary>Role is "user" or "assistant".</summary>
public sealed record LlmTurn(string Role, string Text);

public sealed record LlmRequest(
    string System,
    IReadOnlyList<LlmTurn> Turns,
    IReadOnlyList<ToolSpec> Tools,
    int MaxOutputTokens,
    double Temperature);

public sealed record LlmResponse(
    string Text,
    IReadOnlyList<ToolCall> ToolCalls,
    int InputTokens,
    int OutputTokens);

public interface ILlmProvider
{
    string Name { get; }
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct);
}

internal static class JsonUtil
{
    public static int Int(JsonNode? n)
    {
        if (n is not JsonValue v) return 0;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return (int)Math.Clamp(l, 0, int.MaxValue);
        if (v.TryGetValue<double>(out var d)) return (int)d;
        return 0;
    }

    public static string? Str(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static string Trim(string s, int max = 300) => s.Length <= max ? s : s.Substring(0, max) + "...";
}

/// <summary>
/// Fallback for models without native tool calling (common with small local models):
/// extracts {"name": ..., "arguments": {...}} objects (or an array of them) from plain text.
/// </summary>
public static class TextToolCallParser
{
    public static List<ToolCall> Parse(string? text, ICollection<string> knownTools)
    {
        var result = new List<ToolCall>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        foreach (var candidate in JsonCandidates(text!))
        {
            JsonNode? node;
            try { node = JsonNode.Parse(candidate); }
            catch (JsonException) { continue; }

            if (node is JsonArray arr)
            {
                foreach (var item in arr) TryAdd(item as JsonObject, knownTools, result);
            }
            else if (node is JsonObject obj)
            {
                if (obj["tool_calls"] is JsonArray calls)
                    foreach (var item in calls) TryAdd(item as JsonObject, knownTools, result);
                else
                    TryAdd(obj, knownTools, result);
            }
            if (result.Count > 0) break;
        }
        return result;
    }

    static void TryAdd(JsonObject? o, ICollection<string> known, List<ToolCall> into)
    {
        if (o == null) return;
        if (o["function"] is JsonObject fn) o = fn; // OpenAI-shaped
        var name = JsonUtil.Str(o["name"]) ?? JsonUtil.Str(o["tool"]);
        if (string.IsNullOrEmpty(name) || !known.Contains(name!)) return;
        var args = o["arguments"] ?? o["parameters"] ?? o["input"] ?? o["args"];
        JsonObject parsed = args switch
        {
            JsonObject a => (JsonObject)a.DeepClone(),
            JsonValue v when v.TryGetValue<string>(out var s) => SafeObject(s),
            _ => new JsonObject(),
        };
        into.Add(new ToolCall(name!, parsed));
    }

    static JsonObject SafeObject(string s)
    {
        try { return JsonNode.Parse(s) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    /// <summary>Yields balanced {...} / [...] substrings, outermost first.</summary>
    static IEnumerable<string> JsonCandidates(string text)
    {
        for (int start = 0; start < text.Length; start++)
        {
            char open = text[start];
            if (open != '{' && open != '[') continue;
            char close = open == '{' ? '}' : ']';
            int depth = 0;
            bool inString = false, escaped = false;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == open) depth++;
                else if (c == close && --depth == 0)
                {
                    yield return text.Substring(start, i - start + 1);
                    start = i;
                    break;
                }
            }
        }
    }
}
