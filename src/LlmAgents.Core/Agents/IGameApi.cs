using System.Collections.Generic;

namespace LlmAgents.Agents;

/// <param name="Trait">The game's own personality trait of the character (e.g. "Hothead"), if any.</param>
public sealed record BotHandle(int Id, string Name, string Job, string? Trait = null);

/// <summary>A message from the game about one bot, e.g. a multi-step task that finished or failed.</summary>
public sealed record GameReport(int BotId, string Text);

/// <summary>A player gave (Granted) or took back full AI control of a bot through the mod's command-menu order.</summary>
public sealed record ControlEvent(int BotId, bool Granted, string By);

/// <summary>
/// Everything the agent layer needs from the game. The only real implementation is
/// LlmAgents.Server/GameAdapter.cs; tests use a fake. All methods are called on the game thread.
/// Every action returns a short human-readable result ("ok ..." or "error: ...") which is
/// fed back to the LLM in the next observation.
/// </summary>
public interface IGameApi
{
    IReadOnlyList<BotHandle> GetBots();
    bool IsAlive(int botId);
    Observation Observe(int botId);

    /// <summary>
    /// Called before each batch of actions for a bot. playerAuthority = the decision was made to carry out
    /// a human player's chat order, so it may replace orders a player gave through the command menu.
    /// </summary>
    void BeginCommand(int botId, bool playerAuthority);

    // Orders. priority: 1..3 (3 = most important, the vanilla scale). A new order at priority p moves the bot's
    // orders at p or lower one step down; at most 3 orders, the lowest falls off.
    string GoToRoom(int botId, string roomId, int priority = Tools.TopPriority);
    string Follow(int botId, string targetName, int priority = Tools.TopPriority);

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
    string Deconstruct(int botId, string item, string? from = null);
    /// <summary>item null = choose the cheapest treatment that is good enough.</summary>
    string Treat(int botId, string target, string? item);

    // ---- inventory
    /// <summary>Move a worn/held item into the bot's own inventory.</summary>
    string Unequip(int botId, string item);
    string Drop(int botId, string item);
    /// <summary>to = container name, room id, or crew member name. Multi-step task.</summary>
    string MoveItem(int botId, string item, string to);
    /// <summary>While on, the bot never takes its diving gear off by itself (except when its suit runs out of air).</summary>
    string KeepGear(int botId, bool on);

    /// <summary>Immediate lookup. kind: patient | items | recipe | recipes | salvage.</summary>
    string Inspect(int botId, string kind, string query);

    /// <summary>Task results and other events since the last call.</summary>
    IReadOnlyList<GameReport> DrainReports();

    /// <summary>Full AI control: the bot may override orders from the command menu and decides everything itself.</summary>
    void SetAiControl(int botId, bool on);

    /// <summary>Control orders given by players through the command menu since the last call.</summary>
    IReadOnlyList<ControlEvent> DrainControlEvents();
}
