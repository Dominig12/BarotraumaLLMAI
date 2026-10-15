using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlmAgents.Llm;

namespace LlmAgents.Agents;

/// <summary>Tool schemas shown to the LLM and the executor that maps calls onto IGameApi. Whitelist only.</summary>
public static class Tools
{
    /// <summary>Tool name -> vanilla order identifier, for orders that need no target.</summary>
    public static readonly IReadOnlyDictionary<string, string> SimpleOrders = new Dictionary<string, string>
    {
        ["fix_leaks"] = "fixleaks",
        ["repair_systems"] = "repairsystems",
        ["extinguish_fires"] = "extinguishfires",
        ["fight_intruders"] = "fightintruders",
        ["rescue_injured"] = "rescue",
        ["clean_up"] = "cleanupitems",
        ["wait"] = "wait",
    };

    public static readonly IReadOnlyList<string> OperateRoles = new[] { "reactor", "navigation", "weapons", "pumps" };

    public static readonly IReadOnlyList<string> InspectKinds = new[] { "patient", "items", "recipe" };

    public const int MaxCraftAmount = 10;

    public const int MaxSayLength = 160;
    public const int MaxMessageLength = 300;
    public const int MaxInspectLength = 700;

    static JsonObject Schema(params (string Name, string Type, string Desc, string[]? Enum, bool Required)[] props)
    {
        var p = new JsonObject();
        var req = new JsonArray();
        foreach (var (name, type, desc, en, required) in props)
        {
            var o = new JsonObject { ["type"] = type, ["description"] = desc };
            if (en != null) o["enum"] = new JsonArray(en.Select(e => (JsonNode)JsonValue.Create(e)).ToArray());
            p[name] = o;
            if (required) req.Add(name);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = p, ["required"] = req };
    }

    static JsonObject NoArgs() => new() { ["type"] = "object", ["properties"] = new JsonObject() };

    public static readonly IReadOnlyList<ToolSpec> All = new List<ToolSpec>
    {
        new("go_to_room", "Walk to a room of the submarine and stay there. Use a room id exactly as listed in 'rooms'.",
            Schema(("room", "string", "Room id from the 'rooms' list, e.g. \"Engine Room [h57]\"", null, true))),
        new("follow", "Follow a crew member (a player or another bot).",
            Schema(("target", "string", "Crew member name from 'crew'", null, true))),
        new("fix_leaks", "Weld hull breaches / leaks anywhere on the sub.", NoArgs()),
        new("repair_systems", "Repair damaged devices and machinery.", NoArgs()),
        new("extinguish_fires", "Put out fires.", NoArgs()),
        new("fight_intruders", "Fight hostile creatures or intruders aboard.", NoArgs()),
        new("rescue_injured", "Treat injured crew and carry unconscious crew to safety.", NoArgs()),
        new("clean_up", "Tidy up loose items.", NoArgs()),
        new("operate", "Take a station and operate it.",
            Schema(("role", "string", "Station to operate", OperateRoles.ToArray(), true),
                   ("option", "string", "Optional mode: reactor 'powerup'|'shutdown'; pumps 'pumpout'|'pumpin'", null, false))),
        new("wait", "Stay where you are.", NoArgs()),
        new("return_to_default", "Drop your orders and let your normal instincts take over. Use when nothing special is needed.", NoArgs()),
        new("say", "Say something out loud in the game chat (players see it). Keep it short and in character.",
            Schema(("text", "string", $"What to say (max {MaxSayLength} chars)", null, true))),
        new("message_crew", "Private message to another AI crew member (not shown in chat). Use for coordination.",
            Schema(("to", "string", "Crew member name, a job (all AI crew with that job, e.g. 'mechanic'), or 'all'", null, true), ("text", "string", "Message", null, true))),
        new("remember", "Save a short note to your own memory (shown to you in later turns).",
            Schema(("note", "string", "One short sentence", null, true))),

        // ---- items and machinery (multi-step tasks; the result is reported in lastActionResults when done)
        new("equip", "Take an item from the sub (or your inventory) and equip / wear it, e.g. a diving suit, a weapon, a welding tool, a headset.",
            Schema(("item", "string", "Item name or identifier, e.g. 'diving suit', 'harpoon gun', 'welding tool'", null, true))),
        new("refuel_reactor", "Bring a fuel rod to the reactor and load it (replaces a spent rod). Use when reactor fuel is low or the reactor is at 0%.",
            NoArgs()),
        new("craft", "Gather the ingredients and make an item at a fabricator (medicine, ammo, tools...). Check what is possible with inspect(kind='recipe') first.",
            Schema(("item", "string", "Item to make, name or identifier", null, true),
                   ("amount", "integer", $"How many (1-{MaxCraftAmount}, default 1)", null, false))),
        new("deconstruct", "Bring an item to the deconstructor and break it down into materials.",
            Schema(("item", "string", "Item name or identifier (the nearest matching loose/stored item is used)", null, true))),
        new("treat", "Treat a wounded crew member (or yourself) with medicine. Without 'item' the cheapest treatment that is good enough is chosen, so precious medicine is not wasted.",
            Schema(("target", "string", "Crew member name, or 'me'", null, true),
                   ("item", "string", "Optional: a specific medical item to use, e.g. 'bandage'", null, false))),
        new("inspect", "Look something up; the answer appears in lastActionResults next turn. kind: patient (injuries and suitable medicine for a crew member), items (where items are and how many), recipe (what an item needs at the fabricator and whether it is available).",
            Schema(("kind", "string", "What to look up", InspectKinds.ToArray(), true),
                   ("query", "string", "Crew member name (patient) or item name (items, recipe)", null, true))),
    };

    public static IReadOnlyList<ToolSpec> Filtered(ICollection<string> allowed) =>
        allowed.Count == 0 ? All : All.Where(t => allowed.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>Reads a string argument, tolerating models that send numbers/bools instead of strings.</summary>
    public static string? Str(JsonObject a, string key)
    {
        if (!a.TryGetPropertyValue(key, out var node) || node == null) return null;
        try
        {
            if (node is JsonValue v)
            {
                if (v.TryGetValue<string>(out var s)) return s.Trim();
                return v.ToJsonString().Trim('"').Trim();
            }
            return node.ToJsonString();
        }
        catch { return null; }
    }

    /// <summary>Runs on the game thread. Never throws.</summary>
    public static string Execute(ToolCall call, BotAgent agent, IGameApi game, AgentManager mgr)
    {
        try
        {
            var a = call.Arguments;
            if (SimpleOrders.ContainsKey(call.Name)) return game.SimpleOrder(agent.Id, call.Name);

            switch (call.Name)
            {
                case "go_to_room":
                    return Need(Str(a, "room"), "room", v => game.GoToRoom(agent.Id, v));
                case "follow":
                    return Need(Str(a, "target"), "target", v => game.Follow(agent.Id, v));
                case "operate":
                    return Need(Str(a, "role"), "role", v =>
                    {
                        var role = v.ToLowerInvariant();
                        if (!OperateRoles.Contains(role)) return $"error: role must be one of {string.Join(", ", OperateRoles)}";
                        var option = Str(a, "option");
                        return game.Operate(agent.Id, role, string.IsNullOrEmpty(option) ? null : option!.ToLowerInvariant());
                    });
                case "return_to_default":
                    return game.Dismiss(agent.Id);
                case "say":
                    return Need(Str(a, "text"), "text", v =>
                    {
                        game.Speak(agent.Id, Clip(v, MaxSayLength));
                        return "ok";
                    });
                case "message_crew":
                {
                    var to = Str(a, "to");
                    var text = Str(a, "text");
                    if (string.IsNullOrEmpty(to) || string.IsNullOrEmpty(text)) return "error: 'to' and 'text' are required";
                    return mgr.DeliverMessage(agent, to!, Clip(text!, MaxMessageLength));
                }
                case "remember":
                    return Need(Str(a, "note"), "note", v => { agent.AddNote(v, mgr.Settings.Global.MaxNotes); return "ok"; });
                case "equip":
                    return Need(Str(a, "item"), "item", v => game.Equip(agent.Id, v));
                case "refuel_reactor":
                    return game.RefuelReactor(agent.Id);
                case "craft":
                    return Need(Str(a, "item"), "item", v =>
                    {
                        int amount = int.TryParse(Str(a, "amount"), out var n) ? n : 1;
                        return game.Craft(agent.Id, v, Math.Clamp(amount, 1, MaxCraftAmount));
                    });
                case "deconstruct":
                    return Need(Str(a, "item"), "item", v => game.Deconstruct(agent.Id, v));
                case "treat":
                    return Need(Str(a, "target"), "target", v =>
                    {
                        var item = Str(a, "item");
                        var target = v.Equals("me", StringComparison.OrdinalIgnoreCase) || v.Equals("self", StringComparison.OrdinalIgnoreCase) ? agent.Name : v;
                        return game.Treat(agent.Id, target, string.IsNullOrEmpty(item) ? null : item);
                    });
                case "inspect":
                {
                    var kind = Str(a, "kind")?.ToLowerInvariant();
                    var query = Str(a, "query");
                    if (kind == null || !InspectKinds.Contains(kind)) return $"error: kind must be one of {string.Join(", ", InspectKinds)}";
                    if (string.IsNullOrEmpty(query)) return "error: missing 'query'";
                    return Clip(game.Inspect(agent.Id, kind, query!), MaxInspectLength);
                }
                default:
                    return $"error: unknown tool '{call.Name}'";
            }
        }
        catch (Exception e)
        {
            return "error: " + e.Message;
        }
    }

    public static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

    public static string ArgsToString(ToolCall c) =>
        string.Join(", ", c.Arguments.Select(kv => $"{kv.Key}={kv.Value?.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })}"));

    static string Need(string? v, string name, Func<string, string> f) =>
        string.IsNullOrEmpty(v) ? $"error: missing '{name}'" : f(v!);
}
