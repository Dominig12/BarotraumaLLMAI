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
    int OutputTokens,
    // the answer hit the output token limit (finish_reason "length" / stop_reason "max_tokens")
    bool Truncated = false,
    // tokens the model spent "thinking" (if the server reports them)
    int ReasoningTokens = 0);

public interface ILlmProvider
{
    string Name { get; }
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct);
}

internal static class JsonUtil
{
    /// <summary>Provider-specific request fields from the config (extraBody), e.g. {"reasoning": {"enabled": false}}.</summary>
    public static void Merge(JsonObject body, JsonObject? extra)
    {
        if (extra == null) return;
        foreach (var (k, v) in extra) body[k] = v?.DeepClone();
    }

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

    public static string OneLine(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();

    public static string Trim(string s, int max = 300) => s.Length <= max ? s : s.Substring(0, max) + "...";
}
