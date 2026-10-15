using System.Collections.Generic;

namespace LlmAgents.Agents;

public sealed record BotHandle(int Id, string Name, string Job);

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

    string GoToRoom(int botId, string roomId);
    string Follow(int botId, string targetName);

    /// <summary>orderKey is one of <see cref="Tools.SimpleOrders"/> keys.</summary>
    string SimpleOrder(int botId, string orderKey);

    /// <summary>role: reactor | navigation | weapons | pump. option: e.g. "powerup"/"shutdown" for reactor, "pumpout"/"pumpin" for pumps.</summary>
    string Operate(int botId, string role, string? option);

    /// <summary>Hand control back to the vanilla AI (dismiss all orders).</summary>
    string Dismiss(int botId);

    void Speak(int botId, string text);
}
