using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using LlmAgents.Llm;

namespace LlmAgents.Agents;

/// <summary>Why the director is asked: an event of one bot, or a routine look.</summary>
public sealed record DirectorEvent(EventKind Kind, string Bot, string Text, double At);

/// <summary>A bot as the director sees it.</summary>
public sealed record DirectedBot(BotAgent Agent, Observation Obs, string? NotAssignable);

/// <summary>
/// The director's prompt, tools and the summary of the whole submarine it decides on. Pure text building, so it is
/// tested without a game; <see cref="AgentManager"/> collects the bots and carries out the assignments.
/// </summary>
public static class Director
{
    public const int MaxNotes = 8;

    // ------------------------------------------------------------------ tools

    static JsonObject Obj(params (string Name, JsonObject Schema, bool Required)[] props)
    {
        var p = new JsonObject();
        var req = new JsonArray();
        foreach (var (name, schema, required) in props)
        {
            p[name] = schema;
            if (required) req.Add(name);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = p, ["required"] = req };
    }

    static JsonObject Str(string desc) => new() { ["type"] = "string", ["description"] = desc };

    public static readonly IReadOnlyList<ToolSpec> ToolSpecs = new[]
    {
        new ToolSpec("assign",
            "Give one bot its work: a plan of bot tool steps, run until done or failed (then you hear about it). Replaces the bot's current plan. "
            + "Steps: {\"do\":\"<tool>\",\"args\":{...}}, {\"if\":\"<cond>\",\"then\":[...],\"else\":[...]}, {\"wait_until\":\"<cond>\",\"timeout\":60}, "
            + "{\"repeat\":[...],\"until\":\"<cond>\",\"max\":3}, {\"try\":[...],\"catch\":[...]}; the conditions are those of the bot's own plans "
            + "(health, leaks, room_fire, threats, flora, in_room('X'), has('item')...).",
            Obj(("bot", Str("The bot's name, as in the crew list"), true),
                ("goal", Str("What it is for, a few words (the bot and the players see it)"), true),
                ("steps", new JsonObject { ["type"] = "array", ["description"] = "The plan", ["items"] = new JsonObject { ["type"] = "object" } }, true))),
        new ToolSpec("note", "A note for your later decisions this round (who is meant for what, what to check later).",
            Obj(("text", Str("One short sentence"), true))),
        new ToolSpec("carry_on", "Nothing to change: everyone goes on with what they do.", Obj()),
    };

    /// <summary>The bot tools a plan may use, one line each: "fix_leaks(priority?) - Weld leaks".</summary>
    public static string BotToolList()
    {
        var sb = new StringBuilder();
        foreach (var d in Tools.Defs)
        {
            if (d.ControlOnly || d.Kind is ToolKind.Plan or ToolKind.Memory or ToolKind.Lookup || d.Name is "carry_on" or "set_goal") continue;
            var props = d.Schema["properties"] as JsonObject;
            var required = (d.Schema["required"] as JsonArray)?.Select(n => n?.GetValue<string>()).ToHashSet() ?? new HashSet<string?>();
            var args = props == null ? "" : string.Join(", ", props.Select(kv => kv.Key + (required.Contains(kv.Key) ? "" : "?")));
            var desc = d.Description;
            int dot = desc.IndexOf(". ", StringComparison.Ordinal);
            if (dot > 0) desc = desc[..dot];
            sb.Append("- ").Append(d.Name).Append('(').Append(args).Append(") - ").Append(Tools.Clip(desc, 90)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>No plan, no task, no order, and the game's AI is idle: the bot waits for work.</summary>
    public static bool HasNoWork(BotAgent a, Observation? o) =>
        !a.Busy && (o == null || o.CurrentTask == null && o.CurrentOrder == "none" && (o.Doing == null || o.Doing.StartsWith("idle", StringComparison.Ordinal)));

    // ------------------------------------------------------------------ prompt

    public static string SystemPrompt(string? language) =>
        "You are the DIRECTOR of the AI crew of a submarine in Barotrauma. You have no body: you see the whole submarine and every AI bot, "
        + "and you give each bot its work with assign(bot, goal, steps). The bots carry out your plans step by step with the game's own AI and do not think for themselves, "
        + "so a bot without work stands idle until you give it some.\n"
        + "Rules:\n"
        + "- Danger first: breaches and flooding, fire, intruders and monsters, ballast flora, a failing reactor, injured crew. Send the bots whose job fits "
        + "(mechanics and engineers repair and weld, security and the captain fight, medics heal), the nearest ones, enough of them, not everyone.\n"
        + "- In danger, bots that go into water or fight wear diving gear: equip(item='diving suit') and keep_gear(on=true) as the first steps.\n"
        + "- Stations matter: keep someone at the reactor, the navigation and the guns when it is needed (operate). The vanilla orders (fix_leaks, extinguish_fires, "
        + "repair_systems, fight_intruders, rescue_injured...) keep a bot busy on its own; a plan of one such step is often all it takes.\n"
        + "- Do not assign bots marked 'not assignable': a human player gave them an order, or they are under full AI control. Plan around them.\n"
        + "- Change a bot's work only when the situation asks for it; a bot in the middle of a good plan keeps it. Nothing to change: carry_on().\n"
        + "- Room names and item names exactly as in the summary. Short plans; a failed plan comes back to you with the reason.\n"
        + "- say steps are for speaking in the game: short, in character, in the language the players use"
        + (string.IsNullOrWhiteSpace(language) ? "" : $" ({language})") + ".\n"
        + "Reply only with tool calls.\n\n"
        + "Bot tools for the steps:\n" + BotToolList();

    /// <summary>The user message: why now, the submarine, the crew, earlier decisions.</summary>
    public static string Summary(IReadOnlyList<DirectorEvent> why, IReadOnlyList<DirectedBot> bots, IReadOnlyList<string> notes,
        IReadOnlyList<string> results, double now, IReadOnlyList<string>? talk = null)
    {
        var sb = new StringBuilder();
        sb.Append("Why you are asked now:\n");
        if (why.Count == 0) sb.Append("- routine look\n");
        foreach (var e in why.OrderBy(e => EventPolicy.Tier(e.Kind)).ThenBy(e => e.At).Take(12))
            sb.Append("- [").Append(EventPolicy.Label(e.Kind)).Append("] ").Append(e.Bot.Length > 0 ? e.Bot + ": " : "").Append(Tools.Clip(e.Text, 200)).Append('\n');
        if (results.Count > 0)
        {
            sb.Append("\nResults of your last assignments:\n");
            foreach (var r in results.TakeLast(8)) sb.Append("- ").Append(Tools.Clip(r, 200)).Append('\n');
        }

        var all = bots.Select(b => b.Obs).ToList();
        var sub = all.Select(o => o.Submarine).FirstOrDefault(s => s != null);
        sb.Append("\nSubmarine: ").Append(sub == null ? "unknown" : string.Join(", ", new[]
        {
            sub.ReactorPercent is int output ? $"reactor output {output}%" : null,
            sub.ReactorFuelPercent is int f ? $"reactor fuel {f}%" : null,
            sub.ReactorTemperaturePercent is int t ? $"reactor temperature {t}%" : null,
            sub.FuelRodsAboard is int rods ? $"{rods} spare fuel rods" : null,
            sub.OxygenPercent is int o2 ? $"oxygen {o2}%" : null,
            sub.Power,
            sub.DepthMeters is int d ? $"depth {d} m" : null,
            $"hull breaches {sub.HullBreaches}",
        }.Where(x => x != null))).Append('\n');

        // what any bot knows about the sub: the same room or threat once
        var rooms = all.SelectMany(o => o.Rooms).GroupBy(r => r.Id).Select(g => g.First()).ToList();
        var alerts = rooms.Where(r => r.WaterPercent >= 5 || r.Fires > 0)
            .Select(r => r.Id + ": " + string.Join(", ", new[] { r.WaterPercent >= 5 ? $"water {r.WaterPercent}%" : null, r.Fires > 0 ? $"fire x{r.Fires}" : null }.Where(x => x != null)))
            .ToList();
        Line(sb, "Rooms with water or fire", alerts);
        Line(sb, "Leaks", all.SelectMany(o => o.Leaks).Distinct().ToList());
        // every bot sees the same monster: count per bot, take the largest count
        Line(sb, "Threats", all.SelectMany(o => o.Threats.GroupBy(t => (t.Kind, t.Room)).Select(g => (g.Key, N: g.Count())))
            .GroupBy(x => x.Key).Select(g => (g.Key, N: g.Max(x => x.N)))
            .Select(x => $"{x.Key.Kind}{(x.N > 1 ? " x" + x.N : "")} in {x.Key.Room ?? "outside"}").ToList());
        Line(sb, "Ballast flora", all.SelectMany(o => o.Infestations).Distinct().ToList());
        Line(sb, "Marked by players", all.SelectMany(o => o.Marks).Distinct().ToList());
        var humans = all.SelectMany(o => o.Crew).Where(c => c.IsPlayer).GroupBy(c => c.Name).Select(g => g.First())
            .Select(c => $"{c.Name} ({c.Job}) in {c.Room ?? "outside"}" + (c.HealthPercent < 100 ? $", health {c.HealthPercent}%" : "") + (c.Injuries != null ? $", injuries: {c.Injuries}" : ""))
            .ToList();
        Line(sb, "Human players", humans);
        var hurtBots = bots.Where(b => b.Obs.HealthPercent < 70).Select(b => $"{b.Agent.Name} {b.Obs.HealthPercent}%").ToList();
        Line(sb, "Hurt bots", hurtBots);
        if (rooms.Count > 0) sb.Append("All rooms: ").Append(string.Join(", ", rooms.Select(r => r.Id))).Append('\n');

        sb.Append("\nAI bots:\n");
        foreach (var b in bots)
        {
            var o = b.Obs;
            var a = b.Agent;
            sb.Append("- ").Append(a.Name).Append(" (").Append(a.Job).Append(") in ").Append(o.Room ?? "outside");
            if (o.HealthPercent < 100) sb.Append(", health ").Append(o.HealthPercent).Append('%');
            if (o.Injuries != null) sb.Append(", injuries: ").Append(o.Injuries);
            if (o.Gear != null) sb.Append("; gear: ").Append(Tools.Clip(o.Gear, 80));
            if (o.InventorySpace != null) sb.Append("; ").Append(o.InventorySpace);
            if (o.Equipped.Count > 0) sb.Append("; holds: ").Append(Tools.Clip(string.Join(", ", o.Equipped), 120));
            if (o.CurrentOrder != "none") sb.Append("; order: ").Append(o.CurrentOrder);
            if (o.Doing != null) sb.Append("; doing: ").Append(o.Doing);
            if (o.CurrentTask != null) sb.Append("; task: ").Append(o.CurrentTask);
            if (a.CurrentGoal is { } g) sb.Append("; goal: ").Append(Tools.Clip(g.Text, 100)).Append(" (").Append(g.Source).Append(", ").Append(ModStatus.Ago(now - g.SetAt)).Append(')');
            if (a.PlanText() is { } plan) sb.Append("; plan left: ").Append(Tools.Clip(plan, 140));
            else if (HasNoWork(a, o)) sb.Append("; NO WORK");
            if (b.NotAssignable != null) sb.Append(" — NOT ASSIGNABLE: ").Append(b.NotAssignable);
            sb.Append('\n');
        }
        if (talk is { Count: > 0 })
        {
            sb.Append("\nRecent conversation (players' orders go to the bots themselves; plan around them):\n");
            foreach (var l in talk) sb.Append("- ").Append(l).Append('\n');
        }
        if (notes.Count > 0)
        {
            sb.Append("\nYour notes:\n");
            foreach (var n in notes) sb.Append("- ").Append(n).Append('\n');
        }
        return sb.ToString();
    }

    static void Line(StringBuilder sb, string label, List<string> items)
    {
        if (items.Count == 0) return;
        sb.Append(label).Append(": ").Append(string.Join("; ", items.Take(12))).Append('\n');
    }

    /// <summary>One line for the director's history: what it was asked about and what it did.</summary>
    public static string HistoryLine(IReadOnlyList<DirectorEvent> why) =>
        why.Count == 0 ? "routine look" : Tools.Clip(string.Join(" | ", why.Select(e => $"{EventPolicy.Label(e.Kind)} {e.Bot}: {Tools.Clip(e.Text, 80)}")), 600);

    /// <summary>The bot a name means: exact, then the first name, then a part of it.</summary>
    public static BotAgent? FindBot(IEnumerable<BotAgent> bots, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim();
        var list = bots.ToList();
        return list.FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(b => b.Name.Split(' ')[0].Equals(name.Split(' ')[0], StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(b => b.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(b => Translit.ToLatin(b.Name).Contains(Translit.ToLatin(name), StringComparison.OrdinalIgnoreCase));
    }
}
