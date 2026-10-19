using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LlmAgents.Agents;

public sealed partial class AgentManager
{
    public string SetInventoryLoadout(BotAgent agent, InventoryLoadout profile)
    {
        if (InventoryLoadoutRules.Validate(profile) is { } error) return "error: " + error;
        var copy = profile.Clone();
        string result = game.ConfigureInventoryLoadout(agent.Id, copy.Clone());
        if (!result.StartsWith("ok", StringComparison.Ordinal)) return result;
        agent.InventoryLoadout = copy;
        agent.NextInventoryReviewAt = clock() + copy.ReviewIntervalSeconds;
        agent.Events.RemoveAll(e => e.Kind == EventKind.InventoryReview);
        agent.LastObservation = null;
        if (MemoryOn)
        {
            memory.SetInventoryLoadout(agent.Name, agent.Job, copy);
            memory.Flush();
        }
        return result;
    }

    public string InspectInventoryLoadout(BotAgent agent) => game.InspectInventoryLoadout(agent.Id);

    void RestoreInventoryLoadout(BotAgent agent, double now)
    {
        if (!MemoryOn || memory.Bot(agent.Name)?.InventoryLoadout is not { } profile) return;
        agent.InventoryLoadout = profile.Clone();
        agent.NextInventoryReviewAt = now + profile.ReviewIntervalSeconds;
        string result = game.ConfigureInventoryLoadout(agent.Id, profile.Clone());
        if (!result.StartsWith("ok", StringComparison.Ordinal))
        {
            Log.Warn($"[{agent.Name}] inventory layout restore deferred: {result}");
            agent.RecordResult("saved inventory layout could not be applied: " + result + "; inspect the current inventory and revise the saved layout");
            return;
        }
    }

    void CollectInventoryReview(BotAgent agent, double now)
    {
        if (agent.InventoryLoadout is not { Enabled: true, Slots.Count: > 0 } profile) return;
        if (now < agent.NextInventoryReviewAt || agent.HasEvent(EventKind.InventoryReview)) return;
        if (agent.Pending || agent.Busy || agent.InDanger || agent.HasPlayerOrder || agent.Events.Count > 0) return;
        // Autonomy=order deliberately skips danger-triggered decisions. Still read current safety
        // here: a stale InDanger=false must not turn an opted-in routine review into an emergency interruption.
        var observation = game.Observe(agent.Id);
        if (observation.CurrentTask != null || SubInDanger(observation)
            || observation.HealthPercent < 75 || observation.OxygenPercent < 75)
        {
            agent.NextInventoryReviewAt = now + Math.Min(15, profile.CheckIntervalSeconds);
            return;
        }
        agent.LastObservation = observation;
        agent.LastObservationAt = now;
        // The event never interrupts a plan or takes a slot reserved for human orders. The next
        // review is scheduled when consumed, so a delayed model cannot accumulate a review backlog.
        agent.AddEvent(EventKind.InventoryReview,
            "review your saved inventoryLoadout and inventoryLoadoutStatus: inspect_loadout/inspect_inventory if needed; keep a useful layout, revise it with set_inventory_loadout, or explicitly plan obtaining missing supplies. Respect current duties and survival.", now);
    }

    static JsonObject? LoadoutForPrompt(BotAgent agent) => agent.InventoryLoadout == null ? null
        : JsonSerializer.SerializeToNode(agent.InventoryLoadout, Tools.LoadoutJson)!.AsObject();
}
