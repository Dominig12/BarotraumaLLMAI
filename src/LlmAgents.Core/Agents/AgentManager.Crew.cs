using System;
using System.Collections.Generic;
using System.Linq;

namespace LlmAgents.Agents;

/// <summary>Bot-to-bot messages (message_crew), orders from a bot under full AI control (order_crew), and what players hear of them.</summary>
public sealed partial class AgentManager
{
    /// <summary>Delivers a bot-to-bot message. Called from the tool executor on the game thread.</summary>
    public string DeliverMessage(BotAgent from, string to, string text)
    {
        double now = clock();
        if (!from.TryConsumeMessageQuota(now, Settings.Global.MaxMessagesPerMinute))
            return $"error: you already sent {Settings.Global.MaxMessagesPerMinute} messages this minute; act instead of talking";
        var targets = ResolveTargets(from, to, out var label, out var error);
        if (targets == null)
        {
            // A human player can't read the AI inbox: say it to them out loud / on the radio instead.
            var player = FindPlayer(from, to);
            if (player == null) return error!;
            game.Speak(from.Id, Tools.Clip($"{player}, {text}", MaxChatEcho));
            from.LogEvent($"told {player}: {text}");
            return $"ok (said to player {player} over the radio; to talk to players you can also use say)";
        }

        string line = $"{from.Name}: {text}";
        foreach (var t in targets)
        {
            t.Receive(line);
            t.LogEvent("message " + line);
            Notify(t, EventKind.Mail, $"message from {from.Name} (see inbox)", now);
        }
        from.LogEvent($"told {label}: {text}");
        Echo(from, label, text);
        return targets.Count == 1 ? "ok" : $"ok (sent to {targets.Count})";
    }

    /// <summary>A bot under full AI control commands other AI crew. Their orders are binding but do not override players.</summary>
    public string OrderCrew(BotAgent from, string to, string order)
    {
        if (!from.AiControl) return "error: order_crew is only available under full AI control; use message_crew to ask";
        double now = clock();
        if (!from.TryConsumeMessageQuota(now, Settings.Global.MaxMessagesPerMinute))
            return $"error: you already sent {Settings.Global.MaxMessagesPerMinute} messages this minute; let them work";
        var targets = ResolveTargets(from, to, out var label, out var error);
        if (targets == null) return error!;

        foreach (var t in targets)
        {
            t.ReceivePlayerOrder($"Order from {from.Name} (in command: full AI control given by {from.ControlBy ?? "a player"}): {order}",
                authority: false, from: from.Name);
            t.AddEvent(EventKind.Order, $"new order from {from.Name}, who is in command (see playerOrders)", now);
            t.LogEvent($"order from {from.Name}: {order}");
        }
        from.LogEvent($"ordered {label}: {order}");
        Echo(from, label, order);
        nextTick = double.NegativeInfinity;
        return targets.Count == 1 ? "ok" : $"ok (ordered {targets.Count})";
    }

    /// <summary>Players hear what bots tell each other (on the radio or nearby, like say).</summary>
    readonly Queue<double> echoes = new();

    void Echo(BotAgent from, string label, string text)
    {
        if (!Settings.Global.ShowBotMessagesInChat) return;
        int cap = Settings.Global.MaxChatEchoPerMinute;
        double now = clock();
        while (echoes.Count > 0 && echoes.Peek() <= now - 60) echoes.Dequeue();
        if (cap > 0 && echoes.Count >= cap) return; // delivered, but not spoken: players are not flooded
        echoes.Enqueue(now);
        game.Speak(from.Id, Tools.Clip($"[→ {label}] {text}", MaxChatEcho));
    }

    /// <summary>A human player aboard whose name matches (exact, part, or transliterated), or null.</summary>
    string? FindPlayer(BotAgent from, string to)
    {
        List<string> players;
        try { players = game.Observe(from.Id).Crew.Where(c => c.IsPlayer).Select(c => c.Name).ToList(); }
        catch { return null; }
        var t = to.Trim();
        return players.FirstOrDefault(p => p.Equals(t, StringComparison.OrdinalIgnoreCase))
            ?? players.FirstOrDefault(p => p.Contains(t, StringComparison.OrdinalIgnoreCase) || t.Contains(p, StringComparison.OrdinalIgnoreCase))
            ?? players.FirstOrDefault(p => Translit.ToLatin(p).Equals(Translit.ToLatin(t), StringComparison.OrdinalIgnoreCase));
    }

    List<BotAgent>? ResolveTargets(BotAgent from, string to, out string label, out string? error)
    {
        var others = agents.Values.Where(a => a.Id != from.Id).ToList();
        error = null;
        label = to;
        if (string.Equals(to, "all", StringComparison.OrdinalIgnoreCase) || string.Equals(to, "everyone", StringComparison.OrdinalIgnoreCase))
        {
            label = "all";
            if (others.Count > 0) return others;
            error = "error: there are no other AI crew members";
            return null;
        }

        var exact = others.FirstOrDefault(a => a.Name.Equals(to, StringComparison.OrdinalIgnoreCase));
        var byJob = others.Where(a => a.Job.Equals(to, StringComparison.OrdinalIgnoreCase)).ToList();
        var partial = others.Where(a => a.Name.Contains(to, StringComparison.OrdinalIgnoreCase)).ToList();
        var targets = exact != null ? new List<BotAgent> { exact }
                    : byJob.Count > 0 ? byJob
                    : partial.Count == 1 ? partial
                    : new List<BotAgent>();
        if (targets.Count == 0)
        {
            error = partial.Count > 1
                ? $"error: '{to}' is ambiguous ({string.Join(", ", partial.Select(p => p.Name))}); use the full name"
                : $"error: no AI crew member named '{to}' (human players do not read private messages; use say)";
            return null;
        }
        if (targets.Count == 1) label = targets[0].Name;
        return targets;
    }

    /// <summary>The character's persona; without one, the game's own personality trait (if enabled).</summary>
    string? PersonaPrompt(BotAgent agent)
    {
        var p = personas.PromptFor(agent.Name);
        if (p != null) return p;
        if (Settings.Bots.UseGamePersonality && !string.IsNullOrWhiteSpace(agent.Trait))
            return $"Your personality trait: {agent.Trait}. Let it colour how you talk and react.";
        return null;
    }
}
