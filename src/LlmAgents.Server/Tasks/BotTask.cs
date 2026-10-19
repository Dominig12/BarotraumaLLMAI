using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using LlmAgents.Agents;
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
    internal double TimeoutAllowance => TimeoutSeconds;

    AIObjective? objective;
    bool objectiveDone, objectiveFailed;
    readonly TaskStopState stop = new();
    internal bool CleanupPending => stop.CleanupPending;
    double progressedAt;
    AIObjectiveGoTo? progressNavigation;
    float closestNavigationDistance;
    string? blockerCode, blocker;
    bool blockerWaiting;
    int? previousCompletedUnits;
    protected virtual int? CompletedUnits => null;
    protected virtual int? TotalUnits => null;
    protected virtual BotTask? ProgressChild => null;

    protected BotTask(Character bot, HumanAIController ai)
    {
        Bot = bot;
        Ai = ai;
        StartedAt = Timing.TotalTime;
        progressedAt = StartedAt;
    }

    public string Describe() => $"{Name}: {Step}";

    /// <summary>Only call for observed changes to the requested work, never for polling, retrying or changing display text.</summary>
    protected void MarkProgress() => progressedAt = Timing.TotalTime;
    protected void SetBlocker(string code, string detail, bool waiting = true)
    { blockerCode = code; blocker = detail; blockerWaiting = waiting; }

    internal GameTaskProgress Progress(long taskId)
    {
        string phase = "running";
        string? code = blockerCode, detail = blocker;
        if (CleanupPending) { phase = "cleanup"; code = "cleanup_pending"; detail = "native objective cleanup is being retried"; }
        else if (Bot.IsIncapacitated) { phase = "waiting"; code = "incapacitated"; detail = "carrier is incapacitated"; }
        else if (objective is { Abandon: true }) { phase = "blocked"; code = "objective_abandoned"; detail = "the native objective was abandoned"; }
        else if (objective != null && Ai.ObjectiveManager.ForcedOrder == objective && Ai.ObjectiveManager.CurrentObjective != objective)
        { phase = "waiting"; code = "native_priority"; detail = "a higher-priority native action is running"; }
        else if (ActiveNavigation() != null && Stalled(15))
        { phase = "blocked"; code = "movement_stalled"; detail = "the active navigation objective has not moved the carrier for 15s"; }
        else if (code != null) phase = blockerWaiting ? "waiting" : "blocked";
        else if (ProgressChild is { } child)
            return child.Progress(taskId) with { ElapsedSeconds = Math.Max(0, Timing.TotalTime - StartedAt),
                CompletedUnits = CompletedUnits ?? child.CompletedUnits, TotalUnits = TotalUnits ?? child.TotalUnits };
        return new GameTaskProgress(taskId, phase, Step, Math.Max(0, Timing.TotalTime - StartedAt),
            Math.Max(0, Timing.TotalTime - progressedAt), code, detail, CompletedUnits, TotalUnits);
    }

    AIObjectiveGoTo? ActiveNavigation() => objective is AIObjectiveGoTo navigation
        && Ai.ObjectiveManager.CurrentObjective == navigation && !navigation.IsCompleted && !navigation.Abandon
        && !navigation.SubObjectives.Any() ? navigation : null;

    void TrackNavigationProgress()
    {
        var navigation = ActiveNavigation();
        if (navigation?.Target == null) { progressNavigation = null; return; }
        float distance = Vector2.Distance(Bot.WorldPosition, navigation.Target.WorldPosition);
        if (progressNavigation != navigation) { progressNavigation = navigation; closestNavigationDistance = distance; return; }
        if (distance + 40 < closestNavigationDistance) { closestNavigationDistance = distance; MarkProgress(); }
    }

    void TrackCompletedUnits()
    {
        var completed = CompletedUnits;
        if (previousCompletedUnits is { } previous && completed > previous) MarkProgress();
        previousCompletedUnits = completed;
    }

    /// <summary>Items this task is going for, so other bots pick different ones.</summary>
    public virtual IEnumerable<Item> Claimed => Enumerable.Empty<Item>();

    /// <summary>null = still running, otherwise the final report ("ok ..." / "failed ...").</summary>
    public string? Tick()
    {
        // A cancellation/completion whose native cleanup threw must only retry cleanup.
        // Running Update again here could resume work which the plan has already stopped.
        if (stop.Started) return Finish(stop.Report!);
        if (Bot.IsDead || Bot.Removed) return Finish("failed: you died");
        if (Bot.IsIncapacitated) return null;
        TrackMovement();
        if (Timing.TotalTime - StartedAt > TimeoutSeconds) return Finish($"failed: took too long ({Step})");
        try
        {
            blockerCode = blocker = null;
            TrackNavigationProgress();
            TrackCompletedUnits();
            var r = Update();
            TrackCompletedUnits();
            return r == null ? null : Finish(r);
        }
        catch (Exception e)
        {
            return Finish("failed: " + e.Message);
        }
    }

    protected abstract string? Update();

    public void Cancel() => Finish("cancelled");

    // Release transient ownership before native objective handoff. If native cleanup
    // throws, retry only owned work cleanup; never repeat hooks against a new owner.
    string Finish(string report) => stop.Finish(report, OnStopping, ClearOwnedWork, OnFinish);

    protected virtual void OnStopping() { }
    protected virtual void OnFinish() { }
    /// <summary>Retryable cleanup of this task's objective and any owned child task.</summary>
    protected virtual void ClearOwnedWork() => ClearObjective();

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
    protected bool Pocket(Item item) => InventoryView.TryStore(Bot, item, Bot.Inventory);

    /// <summary>Into the pockets; something that fits no pocket (a diving suit, a big crate) into the hands, after what
    /// the hands hold goes into the pockets. The vanilla "get item" gives up on those: it tries to wear the suit.</summary>
    protected bool PickUp(Item item)
    {
        if (item.Removed || Bot.Inventory == null) return false;
        if (item.GetRootInventoryOwner() == Bot) return true;
        if (item.ParentInventory != null)
        {
            if (GameAdapter.InventoryAccessProblem(Bot, item.ParentInventory) != null) return false;
        }
        else if (!Bot.CanInteractWith(item)) return false;
        if (Pocket(item)) return true;
        var hands = new List<InvSlotType> { InvSlotType.RightHand, InvSlotType.LeftHand };
        foreach (var held in hands.Select(slot => Bot.Inventory.GetItemInLimbSlot(slot)).Where(held => held != null && held != item).Distinct())
            foreach (int slot in InventoryView.AutomaticSlots(Bot.Inventory, pocketsOnly: true))
                if (Bot.Inventory.CanBePutInSlot(held, slot) && Bot.Inventory.TryPutItem(held, slot, allowSwapping: false, allowCombine: false, user: Bot)) break;
        return InventoryView.TryStore(Bot, item, Bot.Inventory);
    }

    protected AIObjectiveGoTo GoTo(ISpatialEntity target, float closeEnough = 100f) =>
        new(target, Bot, Ai.ObjectiveManager, repeat: false, getDivingGearIfNeeded: true, closeEnough: closeEnough);

    protected bool IsNear(Entity target, float distance = 150f) =>
        Vector2.Distance(Bot.WorldPosition, target.WorldPosition) < distance;
}
