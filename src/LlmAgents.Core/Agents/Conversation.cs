using System;
using System.Collections.Generic;
using System.Linq;

namespace LlmAgents.Agents;

/// <summary>
/// What was said aboard this round: players' chat lines and what the AI crew said (say, message_crew, order_crew).
/// The dispatcher sees it to tell who a message without a name continues to talk to, every bot sees what it heard
/// lately (so "and after that, tell me what is there" makes sense), and recall() searches all of it.
/// </summary>
public sealed class ConversationLog
{
    /// <param name="HeardBy">bots that heard it; empty = everybody (radio, or a bot's own line)</param>
    /// <param name="To">who it was meant for (filled in when the dispatcher decided, or the crew member a bot wrote to)</param>
    public sealed record Line(double At, string Speaker, bool IsPlayer, string Text, bool Radio, IReadOnlyCollection<int> HeardBy, string? To = null)
    {
        public bool HeardByBot(int id) => HeardBy.Count == 0 || HeardBy.Contains(id);
    }

    public const int MaxLines = 400;
    readonly List<Line> lines = new();

    public IReadOnlyList<Line> All => lines;

    public Line Add(double at, string speaker, bool isPlayer, string text, bool radio, IReadOnlyCollection<int>? heardBy = null, string? to = null)
    {
        var l = new Line(at, speaker, isPlayer, Tools.Clip(text.Trim(), 300), radio, heardBy ?? Array.Empty<int>(), to);
        lines.Add(l);
        if (lines.Count > MaxLines) lines.RemoveRange(0, lines.Count - MaxLines);
        return l;
    }

    /// <summary>The dispatcher decided who a player's line was for.</summary>
    public void SetRecipients(Line line, IEnumerable<string> names)
    {
        int i = lines.LastIndexOf(line);
        var to = string.Join(", ", names);
        if (i >= 0) lines[i] = line with { To = to.Length == 0 ? "nobody of the AI crew" : to };
    }

    public void Clear() => lines.Clear();

    /// <summary>Recent lines this bot heard (bot = null: everybody's), newest last.</summary>
    public List<Line> Recent(int? bot, double now, double maxAgeSeconds, int max) =>
        lines.Where(l => now - l.At <= maxAgeSeconds && (bot == null || l.HeardByBot(bot.Value))).TakeLast(Math.Max(0, max)).ToList();

    public static string Format(Line l, double now, string? me = null)
    {
        var who = me != null && l.Speaker == me ? "you" : l.Speaker + (l.IsPlayer ? " (player)" : "");
        var to = l.To != null ? " -> " + (me != null && l.To.Split(", ").Contains(me) ? "you" : l.To) : "";
        return $"({ModStatus.Ago(now - l.At)}) {who}{to}{(l.Radio ? " [radio]" : "")}: \"{l.Text}\"";
    }
}
