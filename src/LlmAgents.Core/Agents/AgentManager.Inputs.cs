using System.Linq;

namespace LlmAgents.Agents;

/// <summary>What players did to the bots through the game's own UI: full AI control given or taken back, and orders
/// from the command menu (they win over the bot's own plan).</summary>
public sealed partial class AgentManager
{
    /// <summary>A player gave or took back full AI control through the command menu.</summary>
    void DrainControl(double now)
    {
        foreach (var e in game.DrainControlEvents())
        {
            if (!agents.TryGetValue(e.BotId, out var a))
            {
                game.SetAiControl(e.BotId, false); // not an AI agent (disabled job): nothing would act on it
                continue;
            }
            a.AiControl = e.Granted;
            a.ControlBy = e.Granted ? e.By : null;
            a.IdleStreak = 0;
            game.SetAiControl(a.Id, e.Granted);
            if (MemoryOn) memory.SetControl(a.Name, a.Job, e.Granted, e.By);
            a.AddEvent(EventKind.Control, e.Granted
                ? $"{e.By} handed you FULL CONTROL: from now on you decide everything yourself (see 'control'). Confirm briefly with say."
                : $"{e.By} took back full control: you are a normal crew member again. Confirm briefly with say.", now);
            a.LogEvent(e.Granted ? $"{e.By} gave me full control" : $"{e.By} took back full control");
            Log.Info($"[{a.Name}] full AI control {(e.Granted ? "given" : "taken back")} by {e.By}");
        }
    }

    /// <summary>Orders players gave through the command menu: they win over the bot's own plan.</summary>
    void DrainMenuOrders(double now)
    {
        foreach (var m in game.DrainMenuOrders())
        {
            if (!agents.TryGetValue(m.BotId, out var a)) continue;
            if (m.PriorityOnly)
            {
                // a player moved one of the bot's orders up or down: noted, the plan and the goal stay
                a.AddPastOrder($"{m.By} (command menu): {m.Order}", now);
                Log.Info($"[{a.Name}] {m.By} changed an order's priority: {m.Order}");
                continue;
            }
            if (a.Busy) Log.Info($"[{a.Name}] command menu order from {m.By} cancels the plan: {a.PlanText()}");
            a.ClearPlan();
            var text = m.Dismissal
                ? $"{m.By} dismissed your orders through the command menu ({m.Order}); do not give them to yourself again"
                : $"{m.By} gave you an order through the command menu: {m.Order}";
            a.AddPastOrder(text, now);
            a.SetGoal(m.Dismissal ? null : $"{m.Order} (command menu)", now, "player " + m.By);
            a.LogEvent(text);
            Log.Info($"[{a.Name}] {text}");
            if (Settings.Bots.ReactToMenuOrders)
                a.AddEvent(EventKind.MenuOrder, text + ". The game is already carrying it out; add steps only if they help (gear, tools), confirm briefly at most", now);
        }
    }
}
