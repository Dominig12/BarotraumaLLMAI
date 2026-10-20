using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using LlmAgents.Agents;
using Microsoft.Xna.Framework;

namespace LlmAgents;

internal abstract class BodyTask : BotTask
{
    public Character Target { get; }
    readonly BodyApproachAnchor approachAnchor = new();
    protected BodyTask(Character bot, HumanAIController ai, Character target) : base(bot, ai) => Target = target;
    protected override double TimeoutSeconds => 240;
    protected override void OnStopping()
    {
        if (Bot.SelectedCharacter == Target) Bot.DeselectCharacter();
    }

    /// <summary>Use the same interaction and visibility checks as a player, including outside the submarine.</summary>
    protected string? Approach(float interactionDistance = 200f)
    {
        if (Target.SelectedBy is { Removed: false } other && other != Bot) return $"failed: {Target.Name} is held by {other.Name}";
        if (Bot.CanInteractWith(Target, interactionDistance))
        {
            ClearObjective();
            return "ready";
        }
        // OrderTarget follows its submarine, so compare submarine-relative positions as well.
        // A falling ragdoll can move away from an old waypoint while the carrier is approaching it.
        bool moved = approachAnchor.Update(Target.Position.X, Target.Position.Y, Target.CurrentHull?.ID);
        if (!moved && (ObjectiveFailed || Stalled(15))) return $"failed: cannot reach {GameAdapter.BodyLabel(Target)}";
        if (!Running || moved)
            // Character targets select/deselect themselves inside CheckObjectiveState. A positional
            // target never leaves an old character objective able to release a newly acquired grip.
            Run(new AIObjectiveGoTo(new OrderTarget(Target.WorldPosition, Target.CurrentHull), Bot, Ai.ObjectiveManager,
                    closeEnough: System.Math.Min(70f, interactionDistance / 2))
                { AllowGoingOutside = true, ForceWalkPermanently = true, AlwaysUseEuclideanDistance = true }, $"going to {GameAdapter.BodyLabel(Target)}");
        return null;
    }
}

internal sealed class DragBodyTask : BodyTask
{
    readonly ISpatialEntity destination;
    readonly string destinationName;
    AIObjectiveDragBody? carry;
    float? nearestBodyDistance;
    protected override int? CompletedUnits => !Target.Removed && Delivered ? 1 : 0;
    protected override int? TotalUnits => 1;
    public override string Name => $"drag {GameAdapter.BodyLabel(Target)} to {destinationName}";

    public DragBodyTask(Character bot, HumanAIController ai, Character target, ISpatialEntity destination, string destinationName) : base(bot, ai, target)
    {
        this.destination = destination;
        this.destinationName = destinationName;
    }

    bool Delivered => destination is Hull h ? Target.CurrentHull == h
        : Vector2.DistanceSquared(Target.WorldPosition, destination.WorldPosition) < 250f * 250f
            && (destination is not Character person || Target.CurrentHull == person.CurrentHull);

    protected override string? Update()
    {
        if (!GameAdapter.IsBody(Target)) return "failed: target is gone or conscious again";
        if (destination is Entity { Removed: true } || destination is Character { IsDead: true }) return "failed: destination is gone";
        float bodyDistance = Vector2.Distance(Target.WorldPosition, destination.WorldPosition);
        if (nearestBodyDistance is { } nearest && bodyDistance + 40 < nearest) { MarkProgress(); nearestBodyDistance = bodyDistance; }
        nearestBodyDistance ??= bodyDistance;
        if (carry == null)
        {
            // The forced objective stays the same for the ENTIRE transfer. Replacing a GoTo used
            // to synchronously select idle/rescue between legs; those objectives release the body.
            // Native Act now owns grip and child navigation each game frame, not just this 0.25s task tick.
            carry = new AIObjectiveDragBody(Bot, Ai, Target, destination, destinationName);
            Run(carry, $"approaching {Target.Name} to carry it to {destinationName}");
            Ai.ObjectiveManager.SortObjectives();
        }
        Step = carry.Status;
        if (carry.Failure is { } failure) return "failed: " + failure + "; " + PositionReport();
        if (ObjectiveFailed) return "failed: carrying objective was abandoned; " + PositionReport();
        if (ObjectiveDone)
            return Delivered ? $"ok: {GameAdapter.BodyLabel(Target)} delivered to {destinationName}"
                : "failed: carrying objective ended before the body reached its destination; " + PositionReport();
        return null;
    }

    protected override void OnStopping()
    {
        carry?.Cancel();
    }

    string PositionReport() => $"body in {(Target.CurrentHull is { } bodyHull ? GameAdapter.RoomId(bodyHull) : "outside")}; "
        + $"carrier in {(Bot.CurrentHull is { } botHull ? GameAdapter.RoomId(botHull) : "outside")}; "
        + $"distance {Vector2.Distance(Bot.WorldPosition, Target.WorldPosition):0} game units";
}

internal sealed class LootBodyTask : BodyTask
{
    readonly string? query;
    readonly int count;
    readonly ItemFilter? filter;
    readonly Action<IReadOnlyList<Item>>? received;
    readonly List<Item> taken = new();
    protected override int? CompletedUnits => taken.Count(item => !item.Removed && item.GetRootInventoryOwner() == Bot);
    readonly HashSet<Item> skipped = new();
    public override string Name => $"loot {GameAdapter.BodyLabel(Target)}";
    public LootBodyTask(Character bot, HumanAIController ai, Character target, string? query, int count, ItemFilter? filter = null,
        Action<IReadOnlyList<Item>>? received = null) : base(bot, ai, target)
    {
        this.query = query;
        this.count = count;
        this.filter = filter;
        this.received = received;
    }

    List<Item> Candidates() => (Target.Inventory?.FindAllItems(recursive: true) ?? Enumerable.Empty<Item>())
        .Where(i => !i.Removed && !skipped.Contains(i) && i.GetRootInventoryOwner() == Target && ItemQueries.Matches(Bot, i, filter)
            && (string.IsNullOrWhiteSpace(query) || query.Trim() == "*" || ItemQueries.MatchScore(i, query!) == 3))
        .Distinct().ToList();
    public override IEnumerable<Item> Claimed => Candidates();

    protected override string? Update()
    {
        if (!GameAdapter.IsBody(Target)) return "failed: target is gone or conscious again; took " + Summary();
        var approach = Approach();
        if (approach != "ready") return approach;
        Bot.SelectCharacter(Target);
        if (Target.Inventory == null || !Bot.CanAccessInventory(Target.Inventory)) return "failed: body inventory is inaccessible; took " + Summary();
        var candidates = Candidates();
        if (candidates.Count == 0 || taken.Count >= count)
        {
            if (taken.Any(item => item.Removed || item.GetRootInventoryOwner() != Bot))
                return "failed: some looted items are no longer carried; inspect the current inventory; taken earlier: " + Summary();
            return taken.Count > 0 ? "ok: looted " + Summary() + $" from {GameAdapter.BodyLabel(Target)}"
                : "ok: no matching loot in " + GameAdapter.BodyLabel(Target);
        }
        foreach (var item in candidates)
        {
            if (item.ParentInventory == null || GameAdapter.InventoryAccessProblem(Bot, item.ParentInventory) != null) { skipped.Add(item); continue; }
            // Snapshot children before the transfer. Items inserted into this bag later are not body loot.
            var contents = Target.Inventory.FindAllItems(recursive: true)
                .Where(candidate => !candidate.Removed && candidate.GetRootInventoryOwner() == Target && (candidate == item || IsInside(candidate, item))).ToArray();
            if (!PickUp(item))
                return "failed: no inventory space for " + item.Name + "; took " + Summary() + "; remaining loot stays on the body";
            if (item.GetRootInventoryOwner() != Bot) return "failed: pickup did not transfer the selected body item; took " + Summary();
            received?.Invoke(contents.Where(candidate => !candidate.Removed && candidate.GetRootInventoryOwner() == Bot).ToArray());
            taken.Add(item);
            MarkProgress();
            // Transfer at most one item per tick; children of a bag already taken are no longer loot candidates.
            return null;
        }
        return taken.Count > 0 ? "ok: looted " + Summary() + "; remaining containers are inaccessible" : "failed: matching loot is inaccessible";
    }
    static bool IsInside(Item item, Item container)
    {
        var seen = new HashSet<Item>();
        for (var parent = item.Container; parent != null && seen.Add(parent); parent = parent.Container)
            if (parent == container) return true;
        return false;
    }
    string Summary() => taken.Count == 0 ? "nothing" : string.Join(", ", taken.Select(item => "#" + item.ID + " " + item.Prefab.Name.Value));
}
