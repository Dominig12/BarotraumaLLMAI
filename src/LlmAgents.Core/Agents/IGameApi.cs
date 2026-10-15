using System.Collections.Generic;

namespace LlmAgents.Agents;

public sealed record BotHandle(int Id, string Name, string Job);

/// <summary>A message from the game about one bot, e.g. a multi-step task that finished or failed.</summary>
public sealed record GameReport(int BotId, string Text);

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

    string GoToRoom(int botId, string roomId);
    string Follow(int botId, string targetName);

    /// <summary>orderKey is one of <see cref="Tools.SimpleOrders"/> keys.</summary>
    string SimpleOrder(int botId, string orderKey);

    /// <summary>role: reactor | navigation | weapons | pumps. option: e.g. "powerup"/"shutdown" for reactor, "pumpout"/"pumpin" for pumps.</summary>
    string Operate(int botId, string role, string? option);

    /// <summary>Hand control back to the vanilla AI (dismiss the bot's own orders; player orders stay).</summary>
    string Dismiss(int botId);

    void Speak(int botId, string text);

    // ---- items and machinery: start a multi-step task, completion comes later through DrainReports
    string Equip(int botId, string item);
    string RefuelReactor(int botId);
    string Craft(int botId, string item, int amount);
    string Deconstruct(int botId, string item);
    /// <summary>item null = choose the cheapest treatment that is good enough.</summary>
    string Treat(int botId, string target, string? item);

    /// <summary>Immediate lookup. kind: patient | items | recipe.</summary>
    string Inspect(int botId, string kind, string query);

    /// <summary>Task results and other events since the last call.</summary>
    IReadOnlyList<GameReport> DrainReports();
}
