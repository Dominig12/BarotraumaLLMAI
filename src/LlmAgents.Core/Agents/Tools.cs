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

    public const int MaxSayLength = 160;
    public const int MaxMessageLength = 300;

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
            Schema(("to", "string", "Crew member name, or 'all'", null, true), ("text", "string", "Message", null, true))),
        new("remember", "Save a short note to your own memory (shown to you in later turns).",
            Schema(("note", "string", "One short sentence", null, true))),
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
