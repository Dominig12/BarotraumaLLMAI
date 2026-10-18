using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace LlmAgents;

/// <summary>
/// Multi-step actions built from vanilla AI objectives. Each step is handed to the bot as a forced objective
/// (get item, go to, contain item...); the final step that vanilla bots never do on their own (apply a chosen
/// medicine, start the fabricator) is done here. Ticked on the game thread.
/// </summary>
internal abstract class BotTask
{
    public readonly Character Bot;
    public readonly HumanAIController Ai;
    public readonly double StartedAt;
    public abstract string Name { get; }
    public string Step { get; protected set; } = "starting";
    protected virtual double TimeoutSeconds => 180;

    AIObjective? objective;
    bool objectiveDone, objectiveFailed;

    protected BotTask(Character bot, HumanAIController ai)
    {
        Bot = bot;
        Ai = ai;
        StartedAt = Timing.TotalTime;
    }

    public string Describe() => $"{Name}: {Step}";

    /// <summary>Items this task is going for, so other bots pick different ones.</summary>
    public virtual IEnumerable<Item> Claimed => Enumerable.Empty<Item>();

    /// <summary>null = still running, otherwise the final report ("ok ..." / "failed ...").</summary>
    public string? Tick()
    {
        if (Bot.IsDead || Bot.Removed) return Finish("failed: you died");
        if (Bot.IsIncapacitated) return null;
        TrackMovement();
        if (Timing.TotalTime - StartedAt > TimeoutSeconds) return Finish($"failed: took too long ({Step})");
        try
        {
            var r = Update();
            return r == null ? null : Finish(r);
        }
        catch (Exception e)
        {
            return Finish("failed: " + e.Message);
        }
    }

    protected abstract string? Update();

    public void Cancel() => Finish("cancelled");

    string Finish(string report)
    {
        ClearObjective();
        try { OnFinish(); } catch { /* best effort */ }
        return report;
    }

    protected virtual void OnFinish() { }

    /// <summary>Runs a vanilla objective as the bot's top priority until it completes or is abandoned.</summary>
    protected void Run(AIObjective o, string step)
    {
        ClearObjective();
        objective = o;
        objectiveDone = objectiveFailed = false;
        ResetStall();
        o.Completed += () => objectiveDone = true;
        o.Abandoned += () => objectiveFailed = true;
        Ai.ObjectiveManager.SetForcedOrder(o);
        Step = step;
    }

    protected bool Running => objective != null && !ObjectiveDone && !ObjectiveFailed;
    protected bool ObjectiveDone => objective != null && (objectiveDone || objective.IsCompleted);
    protected bool ObjectiveFailed => objective != null && (objectiveFailed || objective.Abandon);
    protected bool HasObjective => objective != null;

    protected void ClearObjective()
    {
        if (objective != null && Ai.ObjectiveManager.ForcedOrder == objective) Ai.ObjectiveManager.ClearForcedOrder();
        objective = null;
    }

    Vector2 lastPos;
    double lastMovedAt = Timing.TotalTime;

    void TrackMovement()
    {
        if (Vector2.DistanceSquared(Bot.WorldPosition, lastPos) > 40f * 40f || objective == null)
        {
            lastPos = Bot.WorldPosition;
            lastMovedAt = Timing.TotalTime;
        }
    }

    /// <summary>The bot has not moved for this long while an objective runs: vanilla pathing gave up without saying so
    /// (a target behind a wall or above the last waypoint: the bot just stands next to something else).</summary>
    protected bool Stalled(double seconds = 6) => objective != null && Timing.TotalTime - lastMovedAt > seconds;

    /// <summary>Restarts the stall clock (after a new objective, when standing still is expected).</summary>
    protected void ResetStall()
    {
        lastPos = Bot.WorldPosition;
        lastMovedAt = Timing.TotalTime;
    }

    /// <summary>Into the bot's pockets, not its hands: Any slots first, the hands only if nothing else is free.</summary>
    protected bool Pocket(Item item) =>
        Bot.Inventory.TryPutItem(item, Bot, new List<InvSlotType> { InvSlotType.Any })
        || Bot.Inventory.TryPutItem(item, Bot, CharacterInventory.AnySlot);

    /// <summary>Into the pockets; something that fits no pocket (a diving suit, a big crate) into the hands, after what
    /// the hands hold goes into the pockets. The vanilla "get item" gives up on those: it tries to wear the suit.</summary>
    protected bool PickUp(Item item)
    {
        if (item.Removed || Bot.Inventory == null) return false;
        if (item.GetRootInventoryOwner() == Bot) return true;
        if (Pocket(item)) return true;
        var hands = new List<InvSlotType> { InvSlotType.RightHand, InvSlotType.LeftHand };
        foreach (var held in hands.Select(slot => Bot.Inventory.GetItemInLimbSlot(slot)).Where(held => held != null && held != item).Distinct())
            Bot.Inventory.TryPutItem(held, Bot, new List<InvSlotType> { InvSlotType.Any });
        return Bot.Inventory.TryPutItem(item, Bot, hands);
    }

    protected AIObjectiveGoTo GoTo(ISpatialEntity target, float closeEnough = 100f) =>
        new(target, Bot, Ai.ObjectiveManager, repeat: false, getDivingGearIfNeeded: true, closeEnough: closeEnough);

    protected bool IsNear(Entity target, float distance = 150f) =>
        Vector2.Distance(Bot.WorldPosition, target.WorldPosition) < distance;
}
