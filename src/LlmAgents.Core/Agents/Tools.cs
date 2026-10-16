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

    public static readonly IReadOnlyList<string> InspectKinds = new[] { "patient", "items", "recipes", "recipe", "salvage", "orders", "inventory" };

    public const int MaxCraftAmount = 10;
    public const int MaxTakeCount = 8;

    /// <summary>Vanilla order priorities: 1..3, 3 = most important.</summary>
    public const int TopPriority = 3;

    /// <summary>Tool / role names the LLM may use to refer to a current order, mapped to vanilla order identifiers.</summary>
    public static readonly IReadOnlyDictionary<string, string> OrderAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["go_to_room"] = "wait", ["follow"] = "follow",
        ["reactor"] = "operatereactor", ["navigation"] = "steer", ["weapons"] = "operateweapons", ["pumps"] = "pumpwater",
        ["operate_reactor"] = "operatereactor", ["steer"] = "steer", ["operate_weapons"] = "operateweapons", ["pump_water"] = "pumpwater",
    }.Concat(SimpleOrders).GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>Tools offered only to bots under full AI control.</summary>
    public static readonly IReadOnlyCollection<string> ControlOnlyTools = new[] { "order_crew" };

    public const int MaxSayLength = 160;
    public const int MaxMessageLength = 300;
    public const int MaxInspectLength = 1400;

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
        ("priority", "integer", "1-3, default 3 = do this first. Your other orders at this priority or lower move one step down (max 3 orders, the lowest is dropped). Lower = only when more important orders have nothing to do", null, false);

    static JsonObject OrderArgs(params (string Name, string Type, string Desc, string[]? Enum, bool Required)[] props) =>
        Schema(props.Append(PriorityArg).ToArray());

    public static readonly IReadOnlyList<ToolSpec> All = new List<ToolSpec>
    {
        new("go_to_room", "Walk to a room of the submarine and stay there. Use a room id exactly as listed in 'rooms'.",
            OrderArgs(("room", "string", "Room id from the 'rooms' list, e.g. \"Engine Room [h57]\"", null, true))),
        new("follow", "Follow a crew member (a player or another bot).",
            OrderArgs(("target", "string", "Crew member name from 'crew'", null, true))),
        new("come_to", "Go to a crew member wherever they are now (keeps up if they move) and stay with them for a while; then the order is over and you are told so. Use for 'come to me', 'ко мне', 'сюда', 'all to me'. Better than go_to_room, which sends you to a room they may already have left.",
            Schema(("target", "string", "Crew member name from 'crew' (the player who called)", null, true))),
        new("fix_leaks", "Weld hull breaches / leaks anywhere on the sub.", OrderArgs()),
        new("repair_systems", "Repair damaged devices and machinery.", OrderArgs()),
        new("extinguish_fires", "Put out fires.", OrderArgs()),
        new("fight_intruders", "Fight hostile creatures or intruders aboard.", OrderArgs()),
        new("rescue_injured", "Treat injured crew and carry unconscious crew to safety.", OrderArgs()),
        new("clean_up", "Tidy up loose items.", OrderArgs()),
        new("operate", "Take a station and operate it.",
            OrderArgs(("role", "string", "Station to operate", OperateRoles.ToArray(), true),
                   ("option", "string", "Optional mode: reactor 'powerup'|'shutdown'; pumps 'pumpout'|'pumpin'", null, false))),
        new("give_order", "Give yourself ANY order of the game by its id, also the ones without their own tool: repairmechanical, repairelectrical, chargebatteries, loaditems, deconstructitems, assaultenemy, steer (navigation modes), operateweapons, pumpwater, report orders (reportfire, reportbreach, reportintruders, reportbrokendevices, requestfirstaid)... inspect(kind='orders') lists every order of this game with its options and targets.",
            OrderArgs(("order", "string", "Order id, e.g. 'repairelectrical', 'chargebatteries', 'loaditems'", null, true),
                   ("option", "string", "Optional mode from inspect(kind='orders'), e.g. steer 'navigatetactical', pumpwater 'pumpin'", null, false),
                   ("target", "string", "Optional: which device or room (a name or a room id); default the nearest free one", null, false))),
        new("wait", "An order to stop and stay where you are (until another order). Not for 'nothing to do': use carry_on for that.", OrderArgs()),
        new("carry_on", "Change nothing: keep doing what you are doing (your orders and tasks go on). Use when nothing needs to change.", NoArgs()),
        new("set_order_priority", "Reorder your current orders ('currentOrder' shows them as name [pN]). 3 = most important. 0 = drop that order.",
            Schema(("order", "string", "The order as shown in currentOrder (e.g. 'fixleaks', 'operatereactor') or the tool name (e.g. 'fix_leaks', 'reactor')", null, true),
                   ("priority", "integer", "0-3", null, true))),
        new("set_goal", "Set what you are trying to achieve over the next minutes (e.g. 'craft 3 bandages for Ivan', 'keep the reactor fuelled'). It is shown to you every turn as 'goal', so you don't lose track between small steps. done=true when it is achieved or no longer needed.",
            Schema(("goal", "string", "The goal, one short sentence (leave out with done=true)", null, false),
                   ("done", "boolean", "true = the goal is achieved or dropped", null, false))),
        new("plan", "A plan with conditions, carried out without asking you again until it is done or fails: a JSON array of steps. "
            + "Step kinds: {\"do\":\"<tool>\",\"args\":{...}}; {\"if\":\"<condition>\",\"then\":[...],\"else\":[...]}; "
            + "{\"wait_until\":\"<condition>\",\"timeout\":60}; {\"repeat\":[...],\"until\":\"<condition>\",\"max\":3}; "
            + "{\"try\":[...],\"catch\":[...]} (a failed step inside try runs catch instead of failing the plan). "
            + "Conditions: health, oxygen, room_water, room_fire, threats, leaks, flora, reactor, reactor_fuel, sub_oxygen, injured, time, task, last_ok, last_failed, marked; "
            + "in_room('X'), has('item'), wearing('item'), order('id'), threat('kind'), water('room'), fires('room'), near('name'), result('text'); "
            + "with < > <= >= == !=, and, or, not. Replaces the current plan. Use it for jobs with 'if'/'until'/'in case of'; plain call lists still work for simple jobs.",
            Schema(("steps", "array", "The steps (see above)", null, true))),
        new("update_plan", "Change part of the running plan: the node with this id ('plan' shows ids like n3) is replaced by the new steps (if it is running, the new steps run instead). The rest goes on.",
            Schema(("node", "string", "Node id from 'plan', e.g. 'n3'", null, true),
                   ("steps", "array", "New steps for that node, same format as in plan", null, true))),
        new("keep_plan", "Only when 'plan' is shown: keep carrying out those planned steps after the other calls of this reply. Without it, this reply replaces them.", NoArgs()),
        new("return_to_default", "Drop your orders and let your normal instincts take over. Use when nothing special is needed.", NoArgs()),
        new("say", "Say something out loud in the game chat (players see it). Keep it short and in character.",
            Schema(("text", "string", $"What to say (max {MaxSayLength} chars)", null, true))),
        new("message_crew", "Message to other AI crew members, for coordination. It lands in their inbox; human players also hear it on the radio, so keep it short and in character. Addressed to a human player, it is simply said to them over the radio (same as say).",
            Schema(("to", "string", "Crew member name, a job (all AI crew with that job, e.g. 'mechanic'), or 'all'", null, true), ("text", "string", "Message", null, true))),
        new("remember", "Save a short note to your memory. Normal notes last this round; long_term notes are kept across rounds (shown in 'memory.facts').",
            Schema(("note", "string", "One short sentence", null, true),
                   ("long_term", "boolean", "true = keep it for future rounds (important lasting facts only)", null, false))),
        new("forget", "Remove long-term facts that are no longer true.",
            Schema(("text", "string", "Part of the fact to remove", null, true))),
        new("note_relationship", "Remember how you feel about a crew member (kept across rounds). Update it when something important happens between you.",
            Schema(("person", "string", "Crew member name", null, true),
                   ("feeling", "string", "Your attitude", Memory.MemoryStore.Feelings.ToArray(), true),
                   ("note", "string", "Why, in a few words (e.g. 'saved me from a crawler')", null, true))),
        new("note_preference", "Remember what a human player likes or wants from the crew (kept across rounds, shared with the whole AI crew), e.g. 'wants short radio reports', 'reactor at 50%'.",
            Schema(("player", "string", "Player name", null, true),
                   ("preference", "string", "The preference, one short sentence", null, true))),
        new("order_crew", "FULL AI CONTROL ONLY: give an order to other AI crew members. They must follow it like an order from a player. Speak it in the chat too.",
            Schema(("to", "string", "Crew member name, a job (e.g. 'mechanic') or 'all'", null, true),
                   ("order", "string", "The order, short and concrete (what, where, priority)", null, true))),

        // ---- items and machinery (multi-step tasks; the result is reported in lastActionResults when done)
        new("equip", "Take an item from the sub (or your inventory) and equip / wear it, e.g. a diving suit, a weapon, a welding tool, a headset.",
            Schema(("item", "string", "Item name or [id], or a plain word, e.g. 'diving suit', 'harpoon gun', 'welding tool', 'weapon'", null, true))),
        new("refuel_reactor", "Bring a fuel rod to the reactor and load it (replaces a spent rod). Use when reactor fuel is low or the reactor is at 0%.",
            NoArgs()),
        new("craft", "Gather the ingredients and make an item at a fabricator (medicine, ammo, tools...). Do NOT guess names: first inspect(kind='recipes') to see what this sub can make, then use the exact name or [id] from that list. If materials are missing, the result says which scrap gives them; salvage=true breaks that scrap down in the deconstructor first and then crafts.",
            Schema(("item", "string", "Exact name or [id] from inspect(kind='recipes')", null, true),
                   ("amount", "integer", $"How many (1-{MaxCraftAmount}, default 1)", null, false),
                   ("salvage", "boolean", "true = deconstruct scrap aboard for missing materials first (never touches medicine, weapons, diving gear, fuel rods or equipped items)", null, false))),
        new("deconstruct", "Bring an item to the deconstructor and break it down into materials (waits until it is done). inspect(kind='salvage', query=<material>) shows which items give a material.",
            Schema(("item", "string", "Item name or [id] (the nearest matching loose/stored item is used); 'all' together with 'from' = everything in that container/room", null, true),
                   ("from", "string", "Optional: a container (e.g. 'cabinet', 'шкаф') or a room id; only items there are taken", null, false))),
        new("treat", "Treat a wounded crew member (or yourself) with medicine. Without 'item' the cheapest treatment that is good enough is chosen, so precious medicine is not wasted.",
            Schema(("target", "string", "Crew member name, or 'me'", null, true),
                   ("item", "string", "Optional: a specific medical item to use, e.g. 'bandage'", null, false))),
        new("unequip", "Take off a worn item or put a held item away into your inventory (it stays with you).",
            Schema(("item", "string", "Item you wear or hold", null, true))),
        new("drop", "Drop an item from your inventory on the floor here.",
            Schema(("item", "string", "Item in your inventory", null, true))),
        new("take_item", "Pick items up from the sub into your own inventory to carry them (not equipped): medicine for the wounded, spare tools, ammo, a spare suit for someone. A kind of items works too ('medical', 'ammo', 'tools', 'diving gear') and takes a mix.",
            Schema(("item", "string", "Item name or [id], or a kind: 'medical', 'ammo', 'tools', 'weapons', 'diving gear', 'oxygen'", null, true),
                   ("count", "integer", $"How many (1-{MaxTakeCount}, default 1)", null, false),
                   ("from", "string", "Optional: where from - a container ('medical cabinet'), a room id, or a crew member's name (= the room they are in, for 'take these from here')", null, false))),
        new("attack", "Attack with a tool or weapon of your choice. target='ballast flora' burns the flora (see 'infestations') with a welding tool or a flamer: the bot takes the tool and fuel, goes to the root and burns branches on the way. Or a creature / intruder from 'threats' (name or kind), with weapon = what to fight with (e.g. 'harpoon gun').",
            Schema(("target", "string", "'ballast flora' (optionally with a room id), a threat from 'threats', or 'nearest'", null, true),
                   ("weapon", "string", "Optional: the tool or weapon to use, e.g. 'welding tool', 'flamer', 'harpoon gun', 'stun baton'", null, false))),
        new("put_in", "Load an item into something you carry: welding fuel into the welding tool, an oxygen tank into the diving suit or mask, ammo or a magazine into a gun, a battery into a flashlight, bandages into a medkit or toolbelt. Gets the item from the sub if needed; a used-up one comes out into your inventory.",
            Schema(("item", "string", "What to load, e.g. 'welding fuel', 'oxygen tank', 'revolver rounds', 'battery cell'", null, true),
                   ("into", "string", "What you carry that takes it, e.g. 'welding tool', 'diving suit', 'revolver', 'toolbelt'", null, true))),
        new("refill", "Refill what you carry: everything with used-up or missing fuel, oxygen, battery or ammo (or only the item named) gets a full one from the sub.",
            Schema(("item", "string", "Optional: only this item, e.g. 'welding tool', 'diving suit'", null, false))),
        new("tidy_inventory", "Put away what you carry but do not wear or hold, each item where it belongs (onto the same items first). Free slots for the job.",
            Schema(("keep", "string", "Optional: things to keep, comma separated, e.g. 'bandage, welding fuel'", null, false))),
        new("unpack", "Empty a crate or container and put everything where it belongs on the sub: onto the same items first (stacks save space), then next to similar items, then their usual places. A crate is carried along. Items with no fitting place stay in it. The result lists what went where.",
            Schema(("container", "string", "The crate or container: a name ('metal crate'), 'marked' (the one a player marked), or a room id", null, true),
                   ("carry", "boolean", "Carry the crate along (default true); false = walk back and forth", null, false))),
        new("move_item", "Carry an item somewhere: into a container (cabinet, locker...), to a room (left on the floor), or hand it to a crew member; to='its place' puts it where it belongs. To keep it yourself use take_item.",
            Schema(("item", "string", "Item to carry (yours or one on the sub)", null, true),
                   ("to", "string", "A container name (e.g. 'medical cabinet', 'locker'), a room id from 'rooms', or a crew member name", null, true))),
        new("keep_gear", "Lock or unlock your diving gear. While locked you never take the diving suit/mask off by yourself, even in dry rooms. Turn it on before or during combat and whenever the hull may be breached.",
            Schema(("on", "boolean", "true = keep it on, false = normal behaviour", null, true))),
        new("inspect", "Look something up; the answer appears in lastActionResults next turn. kind: patient (injuries and suitable medicine; no query = everyone hurt), items (where items are and how many; query = a room id or a crew member's name: what is in that room), recipes (what the fabricators here can make, what can be made right now; query = optional filter like 'medical', 'ammo', or a material), recipe (one item: ingredients, what is missing and where to get it), salvage (query = a material: which items aboard break down into it; query = an item: what it breaks down into; no query = scrap aboard), orders (every order of the game: id, options, targets aboard; query = optional filter), inventory (your slots, what is inside tools and bags, charge, free space, what needs refilling).",
            Schema(("kind", "string", "What to look up", InspectKinds.ToArray(), true),
                   ("query", "string", "Crew member (patient), item or room (items), item (recipe, salvage), material (salvage), or filter (recipes); optional for patient, recipes, salvage", null, false))),
    };

    public static IReadOnlyList<ToolSpec> Filtered(ICollection<string> allowed) => For(allowed, aiControl: true);

    /// <summary>Tools for one bot: the allowed list, without the control-only tools unless it is under full AI control.</summary>
    public static IReadOnlyList<ToolSpec> For(ICollection<string> allowed, bool aiControl, bool memory = true) =>
        All.Where(t => allowed.Count == 0 || allowed.Contains(t.Name, StringComparer.OrdinalIgnoreCase))
           .Where(t => aiControl || !ControlOnlyTools.Contains(t.Name))
           .Where(t => memory || t.Name is not ("forget" or "note_relationship" or "note_preference"))
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

    static bool? Bool(JsonObject a, string key) => Str(a, key)?.ToLowerInvariant() switch
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
                    return t.Equals("none", StringComparison.OrdinalIgnoreCase) || t.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : t;
                }
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
            if (SimpleOrders.ContainsKey(call.Name)) return game.SimpleOrder(agent.Id, call.Name, Priority(a));

            switch (call.Name)
            {
                case "go_to_room":
                    return Need(Str(a, "room"), "room", v => game.GoToRoom(agent.Id, v, Priority(a)));
                case "come_to":
                    return Need(Str(a, "target"), "target", v => game.ComeTo(agent.Id, v));
                case "follow":
                    return Need(Str(a, "target"), "target", v => game.Follow(agent.Id, v, Priority(a)));
                case "operate":
                    return Need(Str(a, "role"), "role", v =>
                    {
                        var role = v.ToLowerInvariant();
                        if (!OperateRoles.Contains(role)) return $"error: role must be one of {string.Join(", ", OperateRoles)}";
                        var option = Str(a, "option");
                        return game.Operate(agent.Id, role, string.IsNullOrEmpty(option) ? null : option!.ToLowerInvariant(), Priority(a));
                    });
                case "give_order":
                    return Need(Str(a, "order") ?? Str(a, "id") ?? Str(a, "name"), "order", v =>
                    {
                        var option = Str(a, "option");
                        var target = Str(a, "target");
                        return game.GiveOrder(agent.Id, v.Trim().ToLowerInvariant(),
                            string.IsNullOrWhiteSpace(option) ? null : option!.Trim().ToLowerInvariant(),
                            string.IsNullOrWhiteSpace(target) ? null : target, Priority(a));
                    });
                case "plan":
                case "update_plan":
                    // carried out by the agent manager as a whole (see ApplyPlan); here only without sequential actions
                    return "error: plans with conditions need global.sequentialActions on";
                case "return_to_default":
                    return game.Dismiss(agent.Id);
                case "set_goal":
                {
                    if (Bool(a, "done") == true || string.IsNullOrWhiteSpace(Str(a, "goal")))
                    {
                        var old = agent.Goal;
                        agent.SetGoal(null, mgr.Now);
                        return old == null ? "ok (no goal)" : "ok, goal finished: " + old;
                    }
                    agent.SetGoal(Str(a, "goal"), mgr.Now);
                    return "ok, goal: " + agent.Goal;
                }
                case "carry_on":
                    return "ok";
                case "keep_plan":
                    return agent.Busy ? "ok, continuing the plan" : "ok (no plan left)";
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
                case "set_order_priority":
                    return Need(Str(a, "order"), "order", v =>
                    {
                        var p = Str(a, "priority");
                        if (p == null || !int.TryParse(p, out var n) || n < 0 || n > TopPriority) return $"error: priority must be 0-{TopPriority}";
                        return game.SetOrderPriority(agent.Id, v, n);
                    });
                case "remember":
                    return Need(Str(a, "note"), "note", v => mgr.Remember(agent, v, Bool(a, "long_term") == true));
                case "forget":
                    return Need(Str(a, "text"), "text", v => mgr.Forget(agent, v));
                case "note_relationship":
                {
                    var person = Str(a, "person");
                    var feeling = Str(a, "feeling")?.ToLowerInvariant();
                    if (string.IsNullOrEmpty(person)) return "error: missing 'person'";
                    if (feeling == null || !Memory.MemoryStore.Feelings.Contains(feeling))
                        return $"error: feeling must be one of {string.Join(", ", Memory.MemoryStore.Feelings)}";
                    return mgr.NoteRelationship(agent, person!, feeling, Str(a, "note") ?? "");
                }
                case "note_preference":
                {
                    var player = Str(a, "player");
                    var pref = Str(a, "preference");
                    if (string.IsNullOrEmpty(player) || string.IsNullOrEmpty(pref)) return "error: 'player' and 'preference' are required";
                    return mgr.NotePreference(agent, player!, pref!);
                }
                case "order_crew":
                {
                    var to = Str(a, "to");
                    var order = Str(a, "order");
                    if (string.IsNullOrEmpty(to) || string.IsNullOrEmpty(order)) return "error: 'to' and 'order' are required";
                    return mgr.OrderCrew(agent, to!, Clip(order!, MaxMessageLength));
                }
                case "equip":
                    return Need(Str(a, "item"), "item", v => game.Equip(agent.Id, v));
                case "refuel_reactor":
                    return game.RefuelReactor(agent.Id);
                case "craft":
                    return Need(Str(a, "item"), "item", v =>
                    {
                        int amount = int.TryParse(Str(a, "amount"), out var n) ? n : 1;
                        return game.Craft(agent.Id, v, Math.Clamp(amount, 1, MaxCraftAmount), Bool(a, "salvage") == true);
                    });
                case "deconstruct":
                    return Need(Str(a, "item"), "item", v => game.Deconstruct(agent.Id, v, Str(a, "from")));
                case "treat":
                    return Need(Str(a, "target"), "target", v =>
                    {
                        var item = Str(a, "item");
                        var target = v.Equals("me", StringComparison.OrdinalIgnoreCase) || v.Equals("self", StringComparison.OrdinalIgnoreCase) ? agent.Name : v;
                        return game.Treat(agent.Id, target, string.IsNullOrEmpty(item) ? null : item);
                    });
                case "unequip":
                    return Need(Str(a, "item"), "item", v => game.Unequip(agent.Id, v));
                case "drop":
                    return Need(Str(a, "item"), "item", v => game.Drop(agent.Id, v));
                case "take_item":
                    return Need(Str(a, "item"), "item", v =>
                    {
                        int count = int.TryParse(Str(a, "count") ?? Str(a, "amount"), out var n) ? n : 1;
                        var from = Str(a, "from");
                        return game.TakeItem(agent.Id, v, Math.Clamp(count, 1, MaxTakeCount), string.IsNullOrWhiteSpace(from) ? null : from);
                    });
                case "attack":
                    return Need(Str(a, "target") ?? Str(a, "enemy") ?? "nearest", "target", v =>
                    {
                        var weapon = Str(a, "weapon") ?? Str(a, "tool") ?? Str(a, "item");
                        return game.Attack(agent.Id, v, string.IsNullOrWhiteSpace(weapon) ? null : weapon);
                    });
                case "put_in":
                {
                    var item = Str(a, "item");
                    var into = Str(a, "into") ?? Str(a, "to") ?? Str(a, "container");
                    if (string.IsNullOrEmpty(item) || string.IsNullOrEmpty(into)) return "error: 'item' and 'into' are required";
                    return game.PutIn(agent.Id, item!, into!);
                }
                case "refill":
                {
                    var item = Str(a, "item") ?? Str(a, "target");
                    return game.Refill(agent.Id, string.IsNullOrWhiteSpace(item) ? null : item);
                }
                case "tidy_inventory":
                {
                    var keep = Str(a, "keep");
                    return game.TidyInventory(agent.Id, string.IsNullOrWhiteSpace(keep) ? null : keep);
                }
                case "unpack":
                    return Need(Str(a, "container") ?? Str(a, "item") ?? Str(a, "from"), "container", v => game.Unpack(agent.Id, v, Bool(a, "carry") != false));
                case "move_item":
                {
                    var item = Str(a, "item");
                    var to = Str(a, "to");
                    if (string.IsNullOrEmpty(item) || string.IsNullOrEmpty(to)) return "error: 'item' and 'to' are required";
                    return game.MoveItem(agent.Id, item!, to!);
                }
                case "keep_gear":
                {
                    var on = Bool(a, "on");
                    if (on == null) return "error: 'on' must be true or false";
                    return game.KeepGear(agent.Id, on.Value);
                }
                case "inspect":
                {
                    var kind = Str(a, "kind")?.ToLowerInvariant();
                    var query = Str(a, "query");
                    if (kind == null || !InspectKinds.Contains(kind)) return $"error: kind must be one of {string.Join(", ", InspectKinds)}";
                    if (string.IsNullOrEmpty(query) && kind is "items" or "recipe") return "error: missing 'query' (what to look up)";
                    return Clip(game.Inspect(agent.Id, kind, query ?? ""), MaxInspectLength);
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

    /// <summary>Small models sometimes wrap values in extra quotes: "'recipe'" -> "recipe".</summary>
    static string Unquote(string s) =>
        s.Length >= 2 && ((s[0] == '\'' && s[^1] == '\'') || (s[0] == '"' && s[^1] == '"')) ? s.Substring(1, s.Length - 2).Trim() : s;

    public static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max);

    public static string ArgsToString(ToolCall c) =>
        string.Join(", ", c.Arguments.Select(kv => $"{kv.Key}={kv.Value?.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })}"));

    static string Need(string? v, string name, Func<string, string> f) =>
        string.IsNullOrEmpty(v) ? $"error: missing '{name}'" : f(v!);
}
