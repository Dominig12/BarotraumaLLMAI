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
    protected override void OnFinish()
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
    bool dragging;
    readonly BodyDragProgress progress = new();
    AIObjectiveGoTo? navigation;
    bool hasDragged;
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
        Run(navigation = new AIObjectiveGoTo(navigationTarget, Bot, Ai.ObjectiveManager, closeEnough: 50f)
            { AllowGoingOutside = true, ForceWalkPermanently = true }, $"dragging {Target.Name} to {destinationName}");
        // SetForcedOrder alone does not switch CurrentObjective. Switch before acquiring the body:
        // the previous objective's OnDeselected (notably rescue) is allowed to deselect characters.
        Ai.ObjectiveManager.SortObjectives();
    }

    protected override string? Update()
    {
        if (!GameAdapter.IsBody(Target)) return "failed: target is gone or conscious again";
        if (destination is Entity { Removed: true } || destination is Character { IsDead: true }) return "failed: destination is gone";
        if (Delivered) return $"ok: {GameAdapter.BodyLabel(Target)} delivered to {destinationName}";
        if (!Target.CanBeDraggedBy(Bot)) return "failed: target cannot be dragged";
        if (Target.SelectedBy is { Removed: false } other && other != Bot) return $"failed: body taken by {other.Name}";
        // Position is submarine-relative: movement of the submarine is not progress by the body.
        // Start the clock at the first grip, so a long initial walk does not consume recovery time.
        if (hasDragged) progress.Observe(Target.Position.X, Target.Position.Y, Target.CurrentHull?.ID, Timing.TotalTime);

        if (dragging && Bot.SelectedCharacter != Target)
        {
            // Character.Control already checks both the centre and the closest limb. Repeating
            // only its centre-distance check here drops valid grips on stretched/large ragdolls.
            progress.LostGrip(Timing.TotalTime);
            Log.Info($"[{Bot.Name}] drag grip released: {GameAdapter.BodyLabel(Target)}; {PositionReport()}; "
                + $"navigation active={Ai.ObjectiveManager.CurrentObjective == navigation}; losses without body movement={progress.GripLossesWithoutProgress}");
            ClearObjective();
            dragging = false;
        }
        if (progress.CannotKeepGrip(Timing.TotalTime))
            return "failed: native dragging repeatedly released the body without moving it; " + PositionReport();
        if (!dragging)
        {
            if (Timing.TotalTime < progress.RetryAt) return null;
            // Selecting a body at the full UI interaction range can immediately fail the native
            // hand-to-limb visibility checks. Walk close before grabbing instead of reselecting
            // it every tick from the same distant position.
            var approach = Approach(80f);
            if (approach != "ready") return approach;
            WalkToDestination();
            if (Ai.ObjectiveManager.CurrentObjective != navigation)
            {
                Step = "waiting for the native AI to finish its higher-priority action before grabbing";
                return null;
            }
            Bot.SelectCharacter(Target);
            dragging = Bot.SelectedCharacter == Target;
            if (dragging && !hasDragged)
            {
                hasDragged = true;
                progress.Observe(Target.Position.X, Target.Position.Y, Target.CurrentHull?.ID, Timing.TotalTime);
            }
            return null;
        }
        if (ObjectiveFailed || progress.Stuck(Timing.TotalTime)) return "failed: body stopped moving before reaching the destination; " + PositionReport();
        // A path finishing is not proof the body followed. Keep pulling until its actual position agrees.
        if (ObjectiveDone || destination is Character moved && (moved.CurrentHull != walkingHull
            || Vector2.DistanceSquared(moved.WorldPosition, walkingTo) > 100f * 100f)) WalkToDestination();
        return null;
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
            return taken.Count > 0 ? "ok: looted " + Summary() + $" from {GameAdapter.BodyLabel(Target)}"
                : "ok: no matching loot in " + GameAdapter.BodyLabel(Target);
        foreach (var item in candidates)
        {
            if (item.ParentInventory == null || GameAdapter.InventoryAccessProblem(Bot, item.ParentInventory) != null) { skipped.Add(item); continue; }
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
