using System;
using System.Collections.Generic;
using System.Linq;

namespace LlmAgents.Llm;

/// <summary>
/// Keeps a request inside the model's context window. Local servers silently cut an oversized prompt from the front
/// (the system prompt goes first), and the bot then acts without its rules, so the oldest history is dropped here
/// instead. Token counts are estimated: about 3.2 characters per token is a safe side for mixed Russian/English JSON.
/// </summary>
public static class ContextGuard
{
    public const double CharsPerToken = 3.2;
    public const double WarnShare = 0.85;

    public static int Estimate(string text) => (int)Math.Ceiling((text?.Length ?? 0) / CharsPerToken);

    public static int Estimate(ToolSpec t) => Estimate(t.Name) + Estimate(t.Description) + Estimate(t.Parameters.ToJsonString()) + 8;

    public static int Estimate(string system, IReadOnlyList<LlmTurn> turns, IReadOnlyList<ToolSpec> tools) =>
        Estimate(system) + turns.Sum(t => Estimate(t.Text) + 4) + tools.Sum(Estimate);

    /// <summary>Drops the oldest user/assistant pairs until the prompt plus the answer fit. The last turn (the current
    /// observation) always stays. Returns how many turns were dropped. contextLength 0 means unknown: nothing to do.</summary>
    public static int Fit(string system, List<LlmTurn> turns, IReadOnlyList<ToolSpec> tools, int contextLength, int maxOutput)
    {
        if (contextLength <= 0) return 0;
        int budget = contextLength - Math.Max(0, maxOutput);
        int size = Estimate(system, turns, tools);
        int dropped = 0;
        while (size > budget && turns.Count > 1)
        {
            int n = turns.Count >= 3 && turns[0].Role == "user" && turns[1].Role == "assistant" ? 2 : 1;
            for (int i = 0; i < n; i++) size -= Estimate(turns[i].Text) + 4;
            turns.RemoveRange(0, n);
            dropped += n;
        }
        return dropped;
    }
}
