using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using LlmAgents.Agents;
using Microsoft.Xna.Framework;

namespace LlmAgents;

internal abstract class BodyTask : BotTask
{
    public Character Target { get; }
    protected BodyTask(Character bot, HumanAIController ai, Character target) : base(bot, ai) => Target = target;
    protected override double TimeoutSeconds => 240;
    protected override void OnFinish()
    {
        if (Bot.SelectedCharacter == Target) Bot.DeselectCharacter();
    }

    /// <summary>Use the same interaction and visibility checks as a player, including outside the submarine.</summary>
    protected string? Approach()
    {
        if (Target.SelectedBy is { Removed: false } other && other != Bot) return $"failed: {Target.Name} is held by {other.Name}";
        if (Bot.CanInteractWith(Target))
        {
            ClearObjective();
            return "ready";
        }
        if (ObjectiveFailed || Stalled(15)) return $"failed: cannot reach {GameAdapter.BodyLabel(Target)}";
        if (!Running)
            Run(new AIObjectiveGoTo(Target, Bot, Ai.ObjectiveManager, closeEnough: 70f)
                { IgnoreIfTargetDead = false, AllowGoingOutside = true }, $"going to {GameAdapter.BodyLabel(Target)}");
        return null;
    }
}

internal sealed class DragBodyTask : BodyTask
{
    readonly ISpatialEntity destination;
    readonly string destinationName;
    bool dragging;
    int regrabs;
    Vector2 walkingTo;
    Hull? walkingHull;
    public override string Name => $"drag {GameAdapter.BodyLabel(Target)} to {destinationName}";

    public DragBodyTask(Character bot, HumanAIController ai, Character target, ISpatialEntity destination, string destinationName) : base(bot, ai, target)
    {
        this.destination = destination;
        this.destinationName = destinationName;
    }

    bool Delivered => destination is Hull h ? Target.CurrentHull == h
        : Vector2.DistanceSquared(Target.WorldPosition, destination.WorldPosition) < 250f * 250f
            && (destination is not Character person || Target.CurrentHull == person.CurrentHull);

    void WalkToDestination()
    {
        walkingTo = destination.WorldPosition;
        walkingHull = (destination as Character)?.CurrentHull;
        // GoTo(Character) selects and deselects that character on completion, releasing the body.
        // A positional target preserves the grip and still uses the destination hull for pathfinding.
        var navigationTarget = destination is Character person
            ? new OrderTarget(person.WorldPosition, person.CurrentHull) : destination;
        Run(new AIObjectiveGoTo(navigationTarget, Bot, Ai.ObjectiveManager, closeEnough: 50f)
            { AllowGoingOutside = true }, $"dragging {Target.Name} to {destinationName}");
    }

    protected override string? Update()
    {
        if (!GameAdapter.IsBody(Target)) return "failed: target is gone or conscious again";
        if (destination is Entity { Removed: true } || destination is Character { IsDead: true }) return "failed: destination is gone";
        if (Delivered) return $"ok: {GameAdapter.BodyLabel(Target)} delivered to {destinationName}";
        if (!Target.CanBeDraggedBy(Bot)) return "failed: target cannot be dragged";
        if (Target.SelectedBy is { Removed: false } other && other != Bot) return $"failed: body taken by {other.Name}";

        if (dragging && (Bot.SelectedCharacter != Target || Vector2.DistanceSquared(Bot.WorldPosition, Target.WorldPosition) > Character.MaxDragDistance * Character.MaxDragDistance))
        {
            if (++regrabs > 5) return "failed: repeatedly lost grip on the body; it has not reached the destination";
            Bot.DeselectCharacter();
            ClearObjective();
            dragging = false;
        }
        if (!dragging)
        {
            var approach = Approach();
            if (approach != "ready") return approach;
            Bot.SelectCharacter(Target);
            dragging = true;
            WalkToDestination();
            return null;
        }
        if (ObjectiveFailed || Stalled(20)) return "failed: cannot drag the body to the destination";
        // A path finishing is not proof the body followed. Keep pulling until its actual position agrees.
        if (ObjectiveDone || destination is Character moved && (moved.CurrentHull != walkingHull
            || Vector2.DistanceSquared(moved.WorldPosition, walkingTo) > 100f * 100f)) WalkToDestination();
        return null;
    }
}

internal sealed class LootBodyTask : BodyTask
{
    readonly string? query;
    readonly int count;
    readonly ItemFilter? filter;
    readonly List<string> taken = new();
    readonly HashSet<Item> skipped = new();
    public override string Name => $"loot {GameAdapter.BodyLabel(Target)}";
    public LootBodyTask(Character bot, HumanAIController ai, Character target, string? query, int count, ItemFilter? filter = null) : base(bot, ai, target)
    {
        this.query = query;
        this.count = count;
        this.filter = filter;
    }

    List<Item> Candidates() => (Target.Inventory?.FindAllItems(recursive: true) ?? Enumerable.Empty<Item>())
        .Where(i => !i.Removed && !skipped.Contains(i) && i.GetRootInventoryOwner() == Target && ItemQueries.Matches(Bot, i, filter)
            && (string.IsNullOrWhiteSpace(query) || query.Trim() == "*" || ItemQueries.MatchScore(i, query!) > 0))
        .Distinct().ToList();
    public override IEnumerable<Item> Claimed => Candidates();

    protected override string? Update()
    {
        if (!GameAdapter.IsBody(Target)) return "failed: target is gone or conscious again; took " + Summary();
        var approach = Approach();
        if (approach != "ready") return approach;
        if (Target.Inventory == null || !Bot.CanAccessInventory(Target.Inventory)) return "failed: body inventory is inaccessible; took " + Summary();
        Bot.SelectCharacter(Target);
        var candidates = Candidates();
        if (candidates.Count == 0 || taken.Count >= count)
            return taken.Count > 0 ? "ok: looted " + Summary() + $" from {GameAdapter.BodyLabel(Target)}"
                : "ok: no matching loot in " + GameAdapter.BodyLabel(Target);
        foreach (var item in candidates)
        {
            if (item.ParentInventory == null || !Bot.CanAccessInventory(item.ParentInventory)) { skipped.Add(item); continue; }
            if (!PickUp(item))
                return "failed: no inventory space for " + item.Name + "; took " + Summary() + "; remaining loot stays on the body";
            taken.Add(ItemQueries.Label(item));
            // Transfer at most one item per tick; children of a bag already taken are no longer loot candidates.
            return null;
        }
        return taken.Count > 0 ? "ok: looted " + Summary() + "; remaining containers are inaccessible" : "failed: matching loot is inaccessible";
    }
    string Summary() => taken.Count == 0 ? "nothing" : string.Join(", ", taken);
}
