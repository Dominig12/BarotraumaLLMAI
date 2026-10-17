using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlmAgents.Llm;

namespace LlmAgents.Agents;

/// <summary>What a tool does to the bot's plan: talk changes nothing in the game, a task runs for a while and
/// reports back, an order hands the vanilla AI a job, a plan tool is about plans themselves.</summary>
public enum ToolKind { Talk, Lookup, Memory, Order, Task, Plan }

/// <summary>Which tools a request carries. Core is always there; the others when the bot's job or situation needs
/// them (<see cref="ToolSelection"/>), so a small local model is not handed 40 schemas every time.</summary>
[Flags]
public enum ToolGroup
{
    Core = 1, Orders = 2, Items = 4, Inventory = 8, Crafting = 16, Medical = 32, Combat = 64, Plans = 128, Memory = 256, Command = 512,
}

/// <summary>Everything about one tool in one place: what the model sees, how it runs, how the plan treats it.</summary>
public sealed record ToolDef(string Name, string Description, JsonObject Schema, ToolKind Kind, ToolGroup Group, Func<ToolContext, string> Run)
{
    /// <summary>Only for bots under full AI control.</summary>
    public bool ControlOnly { get; init; }
    /// <summary>Takes the bot elsewhere or gives it other work (a go_to_room before it was only the way there).</summary>
    public bool MovesOn { get; init; }
    public ToolSpec Spec { get; } = new(Name, Description, Schema);
}

/// <summary>A call being carried out (game thread).</summary>
public sealed record ToolContext(ToolCall Call, BotAgent Agent, IGameApi Game, AgentManager Mgr)
{
    public JsonObject A => Call.Arguments;
    public string? Str(string key) => Tools.Str(A, key);
    public string? Str(params string[] keys) => keys.Select(k => Tools.Str(A, k)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    public bool? Bool(string key) => Tools.Bool(A, key);
    public int Priority => Tools.Priority(A);
    public int Int(string key, int fallback, int min, int max, params string[] more)
    {
        var s = Str(new[] { key }.Concat(more).ToArray());
        return Math.Clamp(int.TryParse(s, out var n) ? n : fallback, min, max);
    }
    public string Need(string? v, string name, Func<string, string> f) => string.IsNullOrEmpty(v) ? $"error: missing '{name}'" : f(v!);
    public int Id => Agent.Id;
}

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

    public static readonly IReadOnlyList<string> InspectKinds = new[] { "patient", "items", "recipes", "recipe", "salvage", "orders", "inventory", "tools" };

    public const int MaxCraftAmount = 24;
    public const int MaxTakeCount = 8;
    public const int MaxMoveCount = 32;
    public const int MaxDeconstructCount = 12;

    /// <summary>Vanilla order priorities: 1..3, 3 = most important.</summary>
    public const int TopPriority = 3;

    /// <summary>Tool / role names the LLM may use to refer to a current order, mapped to vanilla order identifiers.</summary>
    public static readonly IReadOnlyDictionary<string, string> OrderAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["go_to_room"] = "wait", ["follow"] = "follow",
        ["reactor"] = "operatereactor", ["navigation"] = "steer", ["weapons"] = "operateweapons", ["pumps"] = "pumpwater",
        ["operate_reactor"] = "operatereactor", ["steer"] = "steer", ["operate_weapons"] = "operateweapons", ["pump_water"] = "pumpwater",
    }.Concat(SimpleOrders).GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);

    public const int MaxSayLength = 160;
    public const int MaxMessageLength = 300;
    public const int MaxInspectLength = 1400;

    // ---------------------------------------------------------------------------------------------- schemas

    static JsonObject Schema(params (string Name, string Type, string Desc, string[]? Enum, bool Required)[] props)
    {
        var p = new JsonObject();
        var req = new JsonArray();
        foreach (var (name, type, desc, en, required) in props)
        {
            var o = new JsonObject { ["type"] = type, ["description"] = desc };
            if (type == "array") o["items"] = new JsonObject { ["type"] = "object" };
            if (en != null) o["enum"] = new JsonArray(en.Select(e => (JsonNode)JsonValue.Create(e)).ToArray());
            p[name] = o;
            if (required) req.Add(name);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = p, ["required"] = req };
    }

    static JsonObject NoArgs() => new() { ["type"] = "object", ["properties"] = new JsonObject() };

    static readonly (string, string, string, string[]?, bool) PriorityArg =
        ("priority", "integer", "1-3, default 3 = first. Your other orders move one step down (max 3)", null, false);

    static JsonObject OrderArgs(params (string Name, string Type, string Desc, string[]? Enum, bool Required)[] props) =>
        Schema(props.Append(PriorityArg).ToArray());

    static (string, string, string, string[]?, bool) Arg(string name, string desc, bool required = true, string type = "string", string[]? en = null) =>
        (name, type, desc, en, required);

    static ToolDef T(string name, string desc, JsonObject schema, ToolKind kind, ToolGroup group, Func<ToolContext, string> run) =>
        new(name, desc, schema, kind, group, run);

    static ToolDef SimpleOrder(string name, string desc) =>
        T(name, desc, OrderArgs(), ToolKind.Order, ToolGroup.Orders, c => c.Game.SimpleOrder(c.Id, name, c.Priority)) with { MovesOn = name != "wait" };

    // ---------------------------------------------------------------------------------------------- the tools

    public static readonly IReadOnlyList<ToolDef> Defs = new List<ToolDef>
    {
        // ---- moving and orders
        T("go_to_room", "Walk to a room and stay there. Room id exactly as in 'rooms'.",
            OrderArgs(Arg("room", "Room id from 'rooms', e.g. \"Engine Room [h57]\"")), ToolKind.Order, ToolGroup.Core,
            c => c.Need(c.Str("room"), "room", v => c.Game.GoToRoom(c.Id, v, c.Priority))) with { MovesOn = true },
        T("follow", "Follow a crew member.",
            OrderArgs(Arg("target", "Crew member name from 'crew'")), ToolKind.Order, ToolGroup.Core,
            c => c.Need(c.Str("target"), "target", v => c.Game.Follow(c.Id, v, c.Priority))) with { MovesOn = true },
        T("come_to", "Go to a crew member wherever they are now and stay a while, then the order is over ('come to me', 'ко мне', 'сюда').",
            Schema(Arg("target", "Crew member name (the one who called)")), ToolKind.Task, ToolGroup.Core,
            c => c.Need(c.Str("target"), "target", v => c.Game.ComeTo(c.Id, v))) with { MovesOn = true },
        SimpleOrder("fix_leaks", "Weld hull breaches anywhere on the sub."),
        SimpleOrder("repair_systems", "Repair damaged devices and machinery."),
        SimpleOrder("extinguish_fires", "Put out fires."),
        SimpleOrder("fight_intruders", "Fight hostile creatures or intruders aboard."),
        SimpleOrder("rescue_injured", "Treat injured crew, carry the unconscious to safety."),
        SimpleOrder("clean_up", "Tidy up loose items."),
        SimpleOrder("wait", "Order to stand still here. Not for 'nothing to do' (that is carry_on)."),
        T("operate", "Take a free station and operate it.",
            OrderArgs(Arg("role", "Station", en: OperateRoles.ToArray()),
                      Arg("option", "Optional mode: reactor powerup|shutdown; pumps pumpout|pumpin; navigation navigatetodestination "
                          + "(sail to the destination, the default)|navigateback (back to the start)|maintainposition (stop and hold here)", required: false)),
            ToolKind.Order, ToolGroup.Orders, c => c.Need(c.Str("role"), "role", v =>
            {
                var role = v.ToLowerInvariant();
                if (!OperateRoles.Contains(role)) return $"error: role must be one of {string.Join(", ", OperateRoles)}";
                var option = c.Str("option");
                return c.Game.Operate(c.Id, role, string.IsNullOrEmpty(option) ? null : option!.ToLowerInvariant(), c.Priority);
            })) with { MovesOn = true },
        T("give_order", "Give yourself ANY order of the game by id: repairelectrical, repairmechanical, chargebatteries, loaditems, deconstructitems, assaultenemy, steer modes, reports (reportfire, reportbreach, requestfirstaid)... inspect(kind='orders') lists them.",
            OrderArgs(Arg("order", "Order id, e.g. 'repairelectrical'"),
                      Arg("option", "Optional mode, e.g. steer 'navigatetactical'", required: false),
                      Arg("target", "Optional device or room; default the nearest free one", required: false)),
            ToolKind.Order, ToolGroup.Orders, c => c.Need(c.Str("order", "id", "name"), "order", v =>
            {
                var option = c.Str("option");
                var target = c.Str("target");
                return c.Game.GiveOrder(c.Id, v.Trim().ToLowerInvariant(), option?.Trim().ToLowerInvariant(), target, c.Priority);
            })) with { MovesOn = true },
        T("set_order_priority", "Reorder your orders ('currentOrder' shows name [pN]). 3 = most important, 0 = drop it.",
            Schema(Arg("order", "As in currentOrder (e.g. 'fixleaks') or the tool name"), Arg("priority", "0-3", type: "integer")),
            ToolKind.Order, ToolGroup.Orders, c => c.Need(c.Str("order"), "order", v =>
            {
                var p = c.Str("priority");
                if (p == null || !int.TryParse(p, out var n) || n < 0 || n > TopPriority) return $"error: priority must be 0-{TopPriority}";
                return c.Game.SetOrderPriority(c.Id, v, n);
            })),
        T("return_to_default", "Drop your orders, let your normal instincts take over.", NoArgs(), ToolKind.Order, ToolGroup.Core,
            c => c.Game.Dismiss(c.Id)) with { MovesOn = true },

        // ---- talking, goals, plans
        T("carry_on", "Change nothing: your orders and tasks go on.", NoArgs(), ToolKind.Talk, ToolGroup.Core, _ => "ok"),
        T("say", "Say something in the game chat (players see it). Short, in character.",
            Schema(Arg("text", $"What to say (max {MaxSayLength} chars)")), ToolKind.Talk, ToolGroup.Core,
            c => c.Need(c.Str("text"), "text", v => { c.Game.Speak(c.Id, Clip(v, MaxSayLength)); return "ok"; })),
        T("message_crew", "Message to AI crew members (their inbox; players hear it on the radio). To a human player it is just said to them.",
            Schema(Arg("to", "Name, a job (e.g. 'mechanic') or 'all'"), Arg("text", "Message")), ToolKind.Talk, ToolGroup.Core,
            c =>
            {
                var to = c.Str("to"); var text = c.Str("text");
                return string.IsNullOrEmpty(to) || string.IsNullOrEmpty(text) ? "error: 'to' and 'text' are required" : c.Mgr.DeliverMessage(c.Agent, to!, Clip(text!, MaxMessageLength));
            }),
        T("set_goal", "Set what you work toward over the next minutes; shown as 'goal' every turn. done=true when achieved.",
            Schema(Arg("goal", "One short sentence (leave out with done=true)", required: false), Arg("done", "true = achieved or dropped", required: false, type: "boolean")),
            ToolKind.Talk, ToolGroup.Core, c =>
            {
                if (c.Bool("done") == true || string.IsNullOrWhiteSpace(c.Str("goal")))
                {
                    var old = c.Agent.Goal;
                    c.Agent.SetGoal(null, c.Mgr.Now);
                    return old == null ? "ok (no goal)" : "ok, goal finished: " + old;
                }
                c.Agent.SetGoal(c.Str("goal"), c.Mgr.Now);
                return "ok, goal: " + c.Agent.Goal;
            }),
        T("plan", "A plan with conditions, run without asking you again until done or failed: an array of steps. "
            + "{\"do\":\"<tool>\",\"args\":{...}}; {\"if\":\"<cond>\",\"then\":[...],\"else\":[...]}; {\"wait_until\":\"<cond>\",\"timeout\":60}; "
            + "{\"repeat\":[...],\"until\":\"<cond>\",\"max\":3}; {\"try\":[...],\"catch\":[...]}. "
            + "Conditions: health, oxygen, room_water, room_fire, threats, leaks, flora, reactor, reactor_fuel, sub_oxygen, injured, time, task, last_ok, last_failed, marked; "
            + "in_room('X'), has('item'), wearing('item'), order('id'), threat('kind'), water('room'), fires('room'), near('name'), result('text'); "
            + "< > <= >= == !=, and, or, not. Replaces the current plan.",
            Schema(("steps", "array", "The steps", null, true)), ToolKind.Plan, ToolGroup.Plans,
            _ => "error: 'plan' is read with the whole reply; it cannot run inside a plan"),
        T("update_plan", "Replace one node of the running plan ('plan' shows ids like n3) with new steps; the rest goes on.",
            Schema(Arg("node", "Node id, e.g. 'n3'"), ("steps", "array", "New steps, as in plan", null, true)), ToolKind.Plan, ToolGroup.Plans,
            _ => "error: no plan is running to update; send the steps as a new reply"),
        T("keep_plan", "Only when 'plan' is shown: carry it on after this reply's other calls.", NoArgs(), ToolKind.Plan, ToolGroup.Core,
            c => c.Agent.Busy ? "ok, continuing the plan" : "ok (no plan left)"),

        // ---- memory
        T("remember", "Note for later; long_term = kept across rounds ('memory.facts').",
            Schema(Arg("note", "One short sentence"), Arg("long_term", "true = keep for future rounds", required: false, type: "boolean")),
            ToolKind.Memory, ToolGroup.Memory, c => c.Need(c.Str("note"), "note", v => c.Mgr.Remember(c.Agent, v, c.Bool("long_term") == true))),
        T("forget", "Remove long-term facts that are no longer true.", Schema(Arg("text", "Part of the fact")), ToolKind.Memory, ToolGroup.Memory,
            c => c.Need(c.Str("text"), "text", v => c.Mgr.Forget(c.Agent, v))),
        T("note_relationship", "Remember how you feel about a crew member (kept across rounds).",
            Schema(Arg("person", "Name"), Arg("feeling", "Attitude", en: Memory.MemoryStore.Feelings.ToArray()), Arg("note", "Why, a few words")),
            ToolKind.Memory, ToolGroup.Memory, c =>
            {
                var person = c.Str("person");
                var feeling = c.Str("feeling")?.ToLowerInvariant();
                if (string.IsNullOrEmpty(person)) return "error: missing 'person'";
                if (feeling == null || !Memory.MemoryStore.Feelings.Contains(feeling)) return $"error: feeling must be one of {string.Join(", ", Memory.MemoryStore.Feelings)}";
                return c.Mgr.NoteRelationship(c.Agent, person!, feeling, c.Str("note") ?? "");
            }),
        T("note_preference", "Remember what a human player wants from the crew (kept across rounds, shared by the AI crew).",
            Schema(Arg("player", "Player name"), Arg("preference", "One short sentence")), ToolKind.Memory, ToolGroup.Memory, c =>
            {
                var player = c.Str("player"); var pref = c.Str("preference");
                return string.IsNullOrEmpty(player) || string.IsNullOrEmpty(pref) ? "error: 'player' and 'preference' are required" : c.Mgr.NotePreference(c.Agent, player!, pref!);
            }),
        T("order_crew", "FULL AI CONTROL ONLY: a binding order to other AI crew members (say it in chat too).",
            Schema(Arg("to", "Name, a job or 'all'"), Arg("order", "Short and concrete")), ToolKind.Order, ToolGroup.Command, c =>
            {
                var to = c.Str("to"); var order = c.Str("order");
                return string.IsNullOrEmpty(to) || string.IsNullOrEmpty(order) ? "error: 'to' and 'order' are required" : c.Mgr.OrderCrew(c.Agent, to!, Clip(order!, MaxMessageLength));
            }) with { ControlOnly = true },

        // ---- items (multi-step tasks report their result in lastActionResults)
        T("equip", "Take an item and wear / hold it: diving suit, weapon, welding tool...",
            Schema(Arg("item", "Name, [id] or plain word ('diving suit', 'weapon')")), ToolKind.Task, ToolGroup.Core,
            c => c.Need(c.Str("item"), "item", v => c.Game.Equip(c.Id, v))) with { MovesOn = true },
        T("take_item", "Pick items up into your inventory to carry: medicine, spare tools, ammo. A kind ('medical', 'tools', 'ammo') takes a mix.",
            Schema(Arg("item", "Name, [id] or a kind"), Arg("count", $"1-{MaxTakeCount}, default 1", required: false, type: "integer"),
                   Arg("from", "Optional: a container, a room id or a crew member's name (their room)", required: false)),
            ToolKind.Task, ToolGroup.Core, c => c.Need(c.Str("item"), "item",
                v => c.Game.TakeItem(c.Id, v, c.Int("count", 1, 1, MaxTakeCount, "amount"), c.Str("from")))) with { MovesOn = true },
        T("move_item", "Carry items into a container, to a room, or to a crew member; to='its place' = where they belong. "
            + "Moves the whole stack (all of that item you carry, or all in the same cabinet or machine output) in your pockets, unless count is given.",
            Schema(Arg("item", "Item (yours or on the sub)"), Arg("to", "Container name, room id, crew member or 'its place'"),
                   Arg("count", $"How many, 1-{MaxMoveCount}; default: the whole stack", required: false, type: "integer")),
            ToolKind.Task, ToolGroup.Core, c =>
            {
                var item = c.Str("item"); var to = c.Str("to");
                return string.IsNullOrEmpty(item) || string.IsNullOrEmpty(to) ? "error: 'item' and 'to' are required"
                    : c.Game.MoveItem(c.Id, item!, to!, c.Int("count", 0, 0, MaxMoveCount));
            }) with { MovesOn = true },
        T("unequip", "Take off a worn or held item into your inventory.", Schema(Arg("item", "Item you wear or hold")), ToolKind.Order, ToolGroup.Inventory,
            c => c.Need(c.Str("item"), "item", v => c.Game.Unequip(c.Id, v))),
        T("drop", "Drop an item from your inventory here.", Schema(Arg("item", "Item in your inventory")), ToolKind.Order, ToolGroup.Inventory,
            c => c.Need(c.Str("item"), "item", v => c.Game.Drop(c.Id, v))),
        T("put_in", "Load something into an item you carry: fuel into the welding tool, a tank into the suit, ammo into a gun, a battery into a flashlight.",
            Schema(Arg("item", "What to load"), Arg("into", "What you carry that takes it")), ToolKind.Task, ToolGroup.Inventory, c =>
            {
                var item = c.Str("item"); var into = c.Str("into", "to", "container");
                return string.IsNullOrEmpty(item) || string.IsNullOrEmpty(into) ? "error: 'item' and 'into' are required" : c.Game.PutIn(c.Id, item!, into!);
            }),
        T("refill", "Refill what you carry: used-up fuel, oxygen, batteries, ammo (or only the item named).",
            Schema(Arg("item", "Optional: only this item", required: false)), ToolKind.Task, ToolGroup.Inventory,
            c => c.Game.Refill(c.Id, c.Str("item", "target"))),
        T("tidy_inventory", "Put away what you carry but do not wear or hold, each where it belongs.",
            Schema(Arg("keep", "Optional: things to keep, comma separated", required: false)), ToolKind.Task, ToolGroup.Inventory,
            c => c.Game.TidyInventory(c.Id, c.Str("keep"))) with { MovesOn = true },
        T("unpack", "Empty a crate or container and put everything where it belongs (onto the same items first). The result lists what went where.",
            Schema(Arg("container", "Name, 'marked' or a room id"), Arg("carry", "Carry the crate (default true)", required: false, type: "boolean")),
            ToolKind.Task, ToolGroup.Items, c => c.Need(c.Str("container", "item", "from"), "container", v => c.Game.Unpack(c.Id, v, c.Bool("carry") != false))) with { MovesOn = true },
        T("keep_gear", "Lock (true) or unlock your diving gear: while locked you never take it off by yourself. On in combat or when breaches are likely.",
            Schema(Arg("on", "true = keep it on", type: "boolean")), ToolKind.Order, ToolGroup.Core, c =>
            {
                var on = c.Bool("on");
                return on == null ? "error: 'on' must be true or false" : c.Game.KeepGear(c.Id, on.Value);
            }),

        // ---- jobs
        T("refuel_reactor", "Bring a fuel rod and load it into the reactor (replaces a spent one).", NoArgs(), ToolKind.Task, ToolGroup.Crafting,
            c => c.Game.RefuelReactor(c.Id)) with { MovesOn = true },
        T("craft", "Make an item at a fabricator. Exact name or [id] from inspect(kind='recipes'); salvage=true first breaks down scrap for missing materials. "
            + "Products stay in the fabricator's output: bring them with move_item.",
            Schema(Arg("item", "Name or [id] from the recipes"),
                   Arg("amount", $"How many items, 1-{MaxCraftAmount}, default 1 (one craft may make several: 'x6 per craft')", required: false, type: "integer"),
                   Arg("salvage", "Deconstruct scrap for missing materials first", required: false, type: "boolean")),
            ToolKind.Task, ToolGroup.Crafting, c => c.Need(c.Str("item"), "item",
                v => c.Game.Craft(c.Id, v, c.Int("amount", 1, 1, MaxCraftAmount), c.Bool("salvage") == true))) with { MovesOn = true },
        T("deconstruct", "Break items down in the deconstructor: count of the named item (default 1, so 'deconstruct 3 welding tools' = count 3); "
            + "'all' with 'from' = everything there; 'marked' = what players marked.",
            Schema(Arg("item", "Exact name, [id], 'all' or 'marked'"), Arg("from", "Optional container or room id", required: false),
                   Arg("count", $"How many of that item, 1-{MaxDeconstructCount}, default 1", required: false, type: "integer")),
            ToolKind.Task, ToolGroup.Crafting, c => c.Need(c.Str("item"), "item",
                v => c.Game.Deconstruct(c.Id, v, c.Str("from"), c.Int("count", 0, 0, MaxDeconstructCount)))) with { MovesOn = true },
        T("treat", "Treat a wounded crew member (or 'me'). Without 'item' the cheapest good-enough medicine.",
            Schema(Arg("target", "Name or 'me'"), Arg("item", "Optional medicine", required: false)), ToolKind.Task, ToolGroup.Medical,
            c => c.Need(c.Str("target"), "target", v =>
            {
                var target = v.Equals("me", StringComparison.OrdinalIgnoreCase) || v.Equals("self", StringComparison.OrdinalIgnoreCase) ? c.Agent.Name : v;
                return c.Game.Treat(c.Id, target, c.Str("item"));
            })) with { MovesOn = true },
        T("attack", "Attack with a tool or weapon: target='ballast flora' burns it with a welding tool or flamer; or a threat from 'threats', weapon = what to fight with.",
            Schema(Arg("target", "'ballast flora', a threat or 'nearest'"), Arg("weapon", "Optional tool or weapon", required: false)),
            ToolKind.Task, ToolGroup.Combat, c => c.Need(c.Str("target", "enemy") ?? "nearest", "target", v => c.Game.Attack(c.Id, v, c.Str("weapon", "tool", "item")))) with { MovesOn = true },

        // ---- lookups
        T("recall", "Search what you remember (answer next turn): this round's conversation, what you did and where you put things, your notes, "
            + "and long-term facts, past rounds, relationships and what players want. Use it when a player refers to something earlier "
            + "('there', 'like I said', 'the thing from before') or before you say you do not know.",
            Schema(Arg("query", "Words to look for (a name, an item, a room, a topic); empty = the latest of everything", required: false)),
            ToolKind.Lookup, ToolGroup.Core, c => c.Mgr.Recall(c.Agent, c.Str("query", "about", "text"))),
        T("inspect", "Look something up (answer next turn). kind: patient (injuries, medicine; no query = all hurt), items (where; query = room id or name: what is there), "
            + "recipes (what can be made; optional filter), recipe (one item), salvage (what gives a material), orders (all game orders), inventory (your slots, contents, charge), tools (help for one tool).",
            Schema(Arg("kind", "What", en: InspectKinds.ToArray()), Arg("query", "Name, item, room, material or filter", required: false)),
            ToolKind.Lookup, ToolGroup.Core, c =>
            {
                var kind = c.Str("kind")?.ToLowerInvariant();
                var query = c.Str("query");
                if (kind == null || !InspectKinds.Contains(kind)) return $"error: kind must be one of {string.Join(", ", InspectKinds)}";
                if (kind == "tools") return ToolHelp(query);
                if (string.IsNullOrEmpty(query) && kind is "items" or "recipe") return "error: missing 'query' (what to look up)";
                return Clip(c.Game.Inspect(c.Id, kind, query ?? ""), MaxInspectLength);
            }),
    };

    static readonly Dictionary<string, ToolDef> ByName = Defs.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

    public static ToolDef? Def(string name) => ByName.TryGetValue(name, out var d) ? d : null;

    public static readonly IReadOnlyList<ToolSpec> All = Defs.Select(d => d.Spec).ToList();

    /// <summary>Tools offered only to bots under full AI control.</summary>
    public static readonly IReadOnlyCollection<string> ControlOnlyTools = Defs.Where(d => d.ControlOnly).Select(d => d.Name).ToList();

    /// <summary>Tools that start a multi-step task: the plan's next step waits for its result.</summary>
    public static readonly IReadOnlyCollection<string> TaskTools = Defs.Where(d => d.Kind == ToolKind.Task).Select(d => d.Name).ToHashSet();

    /// <summary>Calls that change nothing in the game (talk, notes, lookups, plan bookkeeping).</summary>
    public static readonly IReadOnlyCollection<string> PassiveTools =
        Defs.Where(d => d.Kind is ToolKind.Talk or ToolKind.Lookup or ToolKind.Memory || d.Name is "keep_plan" or "update_plan")
            .Select(d => d.Name).ToHashSet();

    public static bool MovesOn(string tool) => Def(tool)?.MovesOn == true;

    /// <summary>"inspect(kind='tools', query='craft')": the full description and arguments of one tool (or the list).</summary>
    public static string ToolHelp(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || Def(name!) is not { } d)
            return "tools: " + string.Join(", ", Defs.Where(x => !x.ControlOnly).Select(x => x.Name)) + ". Ask with query=<tool name> for details.";
        var args = d.Schema["properties"] is JsonObject p
            ? string.Join("; ", p.Select(kv => $"{kv.Key}: {(string?)kv.Value?["description"]}"))
            : "";
        return $"{d.Name}: {d.Description}" + (args.Length > 0 ? $" Args: {args}" : "");
    }

    public static IReadOnlyList<ToolSpec> Filtered(ICollection<string> allowed) => For(allowed, aiControl: true);

    /// <summary>Tools for one bot: the allowed list, without the control-only tools unless it is under full AI control,
    /// and only the given groups (all when null).</summary>
    public static IReadOnlyList<ToolSpec> For(ICollection<string> allowed, bool aiControl, bool memory = true, ToolGroup? groups = null) =>
        Defs.Where(t => allowed.Count == 0 || allowed.Contains(t.Name, StringComparer.OrdinalIgnoreCase))
            .Where(t => aiControl || !t.ControlOnly)
            .Where(t => memory || t.Group != ToolGroup.Memory || t.Name == "remember")
            .Where(t => groups == null || (groups.Value & t.Group) != 0 || (t.ControlOnly && aiControl))
            .Select(t => t.Spec)
            .ToList();

    /// <summary>Reads the optional priority argument: 1..3, default 3.</summary>
    public static int Priority(JsonObject a, int fallback = TopPriority)
    {
        var v = Str(a, "priority")?.ToLowerInvariant();
        if (string.IsNullOrEmpty(v)) return fallback;
        if (v is "high" or "top" or "highest") return 3;
        if (v is "normal" or "medium") return 2;
        if (v is "low" or "lowest") return 1;
        return double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? Math.Clamp((int)Math.Round(d), 1, TopPriority) : fallback;
    }

    public static bool? Bool(JsonObject a, string key) => Str(a, key)?.ToLowerInvariant() switch
    {
        "true" or "on" or "1" or "yes" => true,
        "false" or "off" or "0" or "no" => false,
        _ => null,
    };

    /// <summary>Reads a string argument, tolerating models that send numbers/bools instead of strings.</summary>
    public static string? Str(JsonObject a, string key)
    {
        if (!a.TryGetPropertyValue(key, out var node) || node == null) return null;
        try
        {
            if (node is JsonValue v)
            {
                if (v.TryGetValue<string>(out var s))
                {
                    var t = Unquote(s.Trim());
                    // models write item=None / target=null for "no value"
                    return t.Length == 0 || t.Equals("none", StringComparison.OrdinalIgnoreCase) || t.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : t;
                }
                return v.ToJsonString().Trim('"').Trim();
            }
            return node.ToJsonString();
        }
        catch { return null; }
    }

    /// <summary>Runs on the game thread. Never throws. Every tool runs, also one not offered in this request.</summary>
    public static string Execute(ToolCall call, BotAgent agent, IGameApi game, AgentManager mgr)
    {
        try
        {
            var def = Def(call.Name);
            if (def == null) return $"error: unknown tool '{call.Name}'";
            return def.Run(new ToolContext(call, agent, game, mgr));
        }
        catch (Exception e)
        {
            return "error: " + e.Message;
        }
    }

    /// <summary>Small models sometimes wrap values in extra quotes: "'recipe'" -> "recipe".</summary>
    static string Unquote(string s) =>
        s.Length >= 2 && ((s[0] == '\'' && s[^1] == '\'') || (s[0] == '"' && s[^1] == '"')) ? s.Substring(1, s.Length - 2).Trim() : s;

    public static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

    static readonly JsonSerializerOptions ReadableJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string ArgsToString(ToolCall c) =>
        string.Join(", ", c.Arguments.Select(kv => $"{kv.Key}={kv.Value?.ToJsonString(ReadableJson)}"));
}
