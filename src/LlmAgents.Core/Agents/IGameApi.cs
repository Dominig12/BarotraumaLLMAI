using System.Collections.Generic;

namespace LlmAgents.Agents;

/// <param name="Trait">The game's own personality trait of the character (e.g. "Hothead"), if any.</param>
public sealed record BotHandle(int Id, string Name, string Job, string? Trait = null);

/// <summary>A message from the game about one bot, e.g. a multi-step task that finished or failed.</summary>
public sealed record GameReport(int BotId, string Text);

/// <summary>A player gave (Granted) or took back full AI control of a bot through the mod's command-menu order.</summary>
public sealed record ControlEvent(int BotId, bool Granted, string By);

/// <summary>A human player gave a bot an order through the command menu (or dismissed its orders).
/// The game has already applied it and cancelled the bot's running task.
/// PriorityOnly: the player only moved one of the bot's orders up or down.</summary>
public sealed record MenuOrder(int BotId, string By, string Order, bool Dismissal = false, bool PriorityOnly = false);

/// <summary>
/// Everything the agent layer needs from the game. Newer abilities have default bodies ("not supported"), so a test
/// fake or another backend only implements what it uses; adding a tool does not break every implementation.
/// The only real implementation is
/// LlmAgents.Server/GameAdapter.cs; tests use a fake. All methods are called on the game thread.
/// Every action returns a short human-readable result ("ok ..." or "error: ...") which is
/// fed back to the LLM in the next observation.
/// </summary>
public interface IGameApi
{
    IReadOnlyList<BotHandle> GetBots();
    bool IsAlive(int botId);
    Observation Observe(int botId);

    /// <summary>The room the bot is in, cheaper than a whole observation (used every second while walking).</summary>
    string? RoomOf(int botId) => Observe(botId).Room;

    /// <summary>Where the bot is and what it is doing, without building a whole observation (the status window asks
    /// for every bot several times a second).</summary>
    BotBrief Brief(int botId)
    {
        var o = Observe(botId);
        return new BotBrief(o.Room, o.CurrentOrder, o.CurrentTask, o.Doing);
    }

    /// <summary>
    /// Called before each batch of actions for a bot. playerAuthority = the decision was made to carry out
    /// a human player's chat order, so it may replace orders a player gave through the command menu.
    /// </summary>
    void BeginCommand(int botId, bool playerAuthority);

    // Orders. priority: 1..3 (3 = most important, the vanilla scale). A new order at priority p moves the bot's
    // orders at p or lower one step down; at most 3 orders, the lowest falls off.
    string GoToRoom(int botId, string roomId, int priority = Tools.TopPriority);
    string Follow(int botId, string targetName, int priority = Tools.TopPriority);
    /// <summary>Task: walk to the person's current position, stay with them for a while, done. ("come to me")</summary>
    string ComeTo(int botId, string targetName);
    /// <summary>A go_to_room step was only on the way to the next step: drop the bot's own "stay in that room" order.</summary>
    string EndTravel(int botId) => "ok";

    /// <summary>Any order of the game by its identifier ("repairelectrical", "chargebatteries"...); target = device or room text.</summary>
    string GiveOrder(int botId, string orderId, string? option, string? target, int priority = Tools.TopPriority) => "error: not supported in this game backend";

    /// <summary>orderKey is one of <see cref="Tools.SimpleOrders"/> keys.</summary>
    string SimpleOrder(int botId, string orderKey, int priority = Tools.TopPriority);

    /// <summary>role: reactor | navigation | weapons | pumps. option: e.g. "powerup"/"shutdown" for reactor, "pumpout"/"pumpin" for pumps.</summary>
    string Operate(int botId, string role, string? option, int priority = Tools.TopPriority);

    /// <summary>Change the priority of one of the bot's current orders; 0 = drop it. order = identifier, tool name or role.</summary>
    string SetOrderPriority(int botId, string order, int priority);

    /// <summary>Hand control back to the vanilla AI (dismiss the bot's own orders; player orders stay).</summary>
    string Dismiss(int botId);

    void Speak(int botId, string text);

    // ---- items and machinery: start a multi-step task, completion comes later through DrainReports
    string Equip(int botId, string item);
    string RefuelReactor(int botId);
    /// <summary>salvage = deconstruct scrap aboard for missing ingredients first.</summary>
    string Craft(int botId, string item, int amount, bool salvage = false);
    /// <summary>from = a container or room: deconstruct the matching items in it (item "all" = everything there).</summary>
    /// <param name="count">0 = the default: one of a named item, everything for 'all' / 'marked'.</param>
    string Deconstruct(int botId, string item, string? from = null, int count = 0);
    /// <summary>item null = choose the cheapest treatment that is good enough.</summary>
    string Treat(int botId, string target, string? item);

    // ---- inventory
    /// <summary>Move a worn/held item into the bot's own inventory.</summary>
    string Unequip(int botId, string item);
    string Drop(int botId, string item);
    /// <summary>to = container name, room id, or crew member name ("me" = take it). Multi-step task.</summary>
    /// <param name="count">0 = the whole stack.</param>
    string MoveItem(int botId, string item, string to, int count = 0);
    /// <summary>
    /// Pick up to <paramref name="count"/> matching items into the inventory; a kind ("medical") takes a mix. Multi-step task.
    /// from = a container, a room or a crew member (the room they are in); null = anywhere, nearest first.
    /// </summary>
    string TakeItem(int botId, string item, int count, string? from = null) => "error: not supported in this game backend";
    /// <summary>Attack ballast flora (burnt with a fire tool) or a creature / intruder, with a chosen tool or weapon. Multi-step task.</summary>
    string Attack(int botId, string target, string? weapon) => "error: not supported in this game backend";
    /// <summary>Load an item into something the bot carries (fuel into a welding tool, a tank into a suit...). Task when it must be fetched.</summary>
    string PutIn(int botId, string item, string into) => "error: not supported in this game backend";
    /// <summary>Refill what the bot carries (or one item) with full fuel, tanks, batteries, ammo. Multi-step task.</summary>
    string Refill(int botId, string? item) => "error: not supported in this game backend";
    /// <summary>Put away what the bot carries but does not wear or hold. Multi-step task.</summary>
    string TidyInventory(int botId, string? keep) => "error: not supported in this game backend";
    /// <summary>Empty a crate or container: every item goes where it belongs (stacks of the same items first). Multi-step task.</summary>
    string Unpack(int botId, string container, bool carry = true) => "error: not supported in this game backend";
    /// <summary>While on, the bot never takes its diving gear off by itself (except when its suit runs out of air).</summary>
    string KeepGear(int botId, bool on);

    /// <summary>Immediate lookup, kind from <see cref="Tools.InspectKinds"/>.</summary>
    string Inspect(int botId, string kind, string query);

    /// <summary>Task results and other events since the last call.</summary>
    IReadOnlyList<GameReport> DrainReports();

    /// <summary>Full AI control: the bot may override orders from the command menu and decides everything itself.</summary>
    void SetAiControl(int botId, bool on);

    /// <summary>Control orders given by players through the command menu since the last call.</summary>
    IReadOnlyList<ControlEvent> DrainControlEvents();

    /// <summary>Ordinary orders players gave bots through the command menu since the last call.</summary>
    IReadOnlyList<MenuOrder> DrainMenuOrders() => System.Array.Empty<MenuOrder>();

    /// <summary>A private server message to a human player (feedback: who took an order, a model failed...).
    /// Does nothing if no such player is connected.</summary>
    void NotifyPlayer(string playerName, string text) { }
}

public sealed record BotBrief(string? Room, string CurrentOrder, string? CurrentTask, string? Doing);
