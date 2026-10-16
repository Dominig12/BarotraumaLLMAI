using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Extensions;
using Barotrauma.Items.Components;
using Barotrauma.MapCreatures.Behavior;
// The reference assemblies are publicized: members that are protected in the game appear public here. The runtime lets
// an override widen the access, so these overrides are declared public.
using Microsoft.Xna.Framework;

namespace LlmAgents;

/// <summary>
/// Burns ballast flora with a fire tool (welding tool, flamer...). Vanilla bots only report the flora, nobody fights
/// it. Like the vanilla "fix leak" objective: get the tool (and welding fuel), walk into reach, aim and fire. Branches
/// in reach are burnt on the way; the walk goes for the root (it kills the whole plant), or the nearest branch when
/// the root cannot be reached.
/// </summary>
internal sealed class AIObjectiveBurnFlora : AIObjective
{
    public override Identifier Identifier { get; set; } = "burn ballast flora".ToIdentifier();
    public override bool ForceRun => true;
    public override bool KeepDivingGearOn => true;
    public override bool AllowInAnySub => true;
    public override bool AllowWhileHandcuffed => false;

    static readonly Identifier WeldingEquipment = "weldingequipment".ToIdentifier();

    readonly Item tool;
    readonly Hull? area;
    readonly HashSet<BallastFloraBranch> unreachable = new();
    AIObjectiveGetItem? getTool;
    AIObjectiveContainItem? refuel;
    AIObjectiveGoTo? goTo;
    Vector2 goToPos;

    /// <summary>Why it gave up (shown in the task report).</summary>
    public string? Problem { get; private set; }
    public string Step { get; private set; } = "starting";

    public AIObjectiveBurnFlora(Character character, AIObjectiveManager manager, Item tool, Hull? area) : base(character, manager, 1f)
    {
        this.tool = tool;
        this.area = area;
    }

    public static IEnumerable<(BallastFloraBehavior Flora, BallastFloraBranch Branch)> LiveBranches(Submarine? sub, Hull? area = null) =>
        BallastFloraBehavior.EntityList
            .Where(f => f.Parent?.Submarine == sub && f.Parent?.Submarine != null)
            .SelectMany(f => f.Branches.Where(b => !b.Removed && b.Health > 0 && b.CurrentHull != null && (area == null || b.CurrentHull == area))
                .Select(b => (f, b)));

    public static Vector2 PositionOf(BallastFloraBehavior f, BallastFloraBranch b) => f.GetWorldPosition() + b.Position;

    public override bool CheckObjectiveState() => !LiveBranches(character.Submarine, area).Any();

    public override void Act(float deltaTime)
    {
        var rt = tool.GetComponent<RepairTool>();
        if (rt == null || tool.Removed) { Fail($"{tool.Name} cannot burn anything"); return; }
        if (!character.HasEquippedItem(tool))
        {
            Step = $"getting {tool.Name}";
            TryAddSubObjective(ref getTool, () => new AIObjectiveGetItem(character, tool, objectiveManager, equip: true),
                onCompleted: () => RemoveSubObjective(ref getTool),
                onAbandon: () => Fail($"could not get {tool.Name}"));
            return;
        }
        if (tool.HasTag(WeldingEquipment) && tool.OwnInventory != null && !tool.OwnInventory.AllItems.Any(i => i.HasTag(Tags.WeldingFuel) && i.Condition > 0))
        {
            Step = "refuelling the welding tool";
            TryAddSubObjective(ref refuel, () => new AIObjectiveContainItem(character, Tags.WeldingFuel, tool.GetComponent<ItemContainer>(), objectiveManager) { RemoveExisting = true },
                onCompleted: () => RemoveSubObjective(ref refuel),
                onAbandon: () => Fail("no welding fuel left aboard"));
            return;
        }
        if (getTool != null || refuel != null) return;

        var live = LiveBranches(character.Submarine, area).ToList();
        if (live.Count == 0) { IsCompleted = true; return; }

        float reach = AIObjectiveFixLeak.CalculateReach(rt, character);
        var aimFrom = character.AnimController.AimSourceWorldPos;
        // in reach AND nothing in between: a branch right below the floor is "in reach" too, and the bot welded the floor
        var inReach = live.Select(t => (t.Flora, t.Branch, Pos: PositionOf(t.Flora, t.Branch)))
            .Where(x => Vector2.Distance(x.Pos, aimFrom) <= reach)
            .OrderBy(x => x.Branch.CurrentHull == character.CurrentHull ? 0 : 1)
            .ThenBy(x => Vector2.DistanceSquared(x.Pos, aimFrom))
            .Take(6).ToList();
        var nearest = inReach.FirstOrDefault(x => ClearShot(aimFrom, x.Pos));
        if (nearest.Branch != null)
        {
            RemoveSubObjective(ref goTo);
            Step = $"burning ballast flora in {(nearest.Branch.CurrentHull != null ? GameAdapter.RoomId(nearest.Branch.CurrentHull) : "?")}";
            Burn(rt, nearest.Pos, deltaTime);
            return;
        }

        // walk: to the root if it can be reached (it kills the plant), else to the nearest branch
        // walking on while branches in reach are only behind a wall: they are reached from their own room
        var candidates = live.Where(t => !unreachable.Contains(t.Branch)).ToList();
        if (candidates.Count == 0) { Fail("cannot reach the flora"); return; }
        var dest = candidates.FirstOrDefault(t => t.Branch.IsRoot);
        if (dest.Branch == null)
            dest = candidates.OrderBy(t => Vector2.DistanceSquared(PositionOf(t.Flora, t.Branch), aimFrom)).First();
        var destPos = PositionOf(dest.Flora, dest.Branch);
        if (goTo != null && Vector2.Distance(goToPos, destPos) > 60) RemoveSubObjective(ref goTo);
        goToPos = destPos;
        var target = dest.Branch;
        Step = $"going to the ballast flora{(dest.Branch.IsRoot ? " root" : "")} in {(target.CurrentHull != null ? GameAdapter.RoomId(target.CurrentHull) : "?")}";
        TryAddSubObjective(ref goTo, () => new AIObjectiveGoTo(new OrderTarget(destPos, target.CurrentHull), character, objectiveManager, getDivingGearIfNeeded: true)
            {
                UseDistanceRelativeToAimSourcePos = true,
                CloseEnough = reach * 0.8f,
            },
            onCompleted: () =>
            {
                // there, but still no clear shot at it (it grows behind a wall): try another branch
                if (!ClearShot(character.AnimController.AimSourceWorldPos, destPos)) unreachable.Add(target);
                RemoveSubObjective(ref goTo);
            },
            onAbandon: () => { unreachable.Add(target); RemoveSubObjective(ref goTo); });
    }

    void Burn(RepairTool rt, Vector2 target, float deltaTime)
    {
        character.AIController.SteeringManager.Reset();
        character.CursorPosition = target;
        if (character.Submarine != null) character.CursorPosition -= character.Submarine.Position;
        if (tool.RequireAimToUse) character.SetInput(InputType.Aim, false, true);
        if (tool.body == null) return;
        var toTarget = target - tool.WorldPosition;
        if (VectorExtensions.Angle(VectorExtensions.Forward(tool.body.TransformedRotation), toTarget) >= MathHelper.PiOver4) return;
        // never through a crewmate
        bool friendInTheWay = Submarine.PickBodies(tool.SimPosition, ConvertSim(target), collisionCategory: Physics.CollisionCharacter)
            .Any(hit => hit.UserData is Character c && c != character && HumanAIController.IsFriendly(character, c));
        if (friendInTheWay) return;
        character.SetInput(InputType.Shoot, false, true);
        rt.Use(deltaTime, character);
    }

    /// <summary>No wall, floor or door between the tool and the branch (the last bit is not checked: branches grow on walls).</summary>
    bool ClearShot(Vector2 from, Vector2 to)
    {
        var dir = to - from;
        float len = dir.Length();
        if (len < 40f) return true;
        var end = from + dir * ((len - 30f) / len);
        return Submarine.PickBody(ConvertSim(from), ConvertSim(end), collisionCategory: Physics.CollisionWall) == null;
    }

    Vector2 ConvertSim(Vector2 world)
    {
        var local = character.Submarine != null ? world - character.Submarine.Position : world;
        return FarseerPhysics.ConvertUnits.ToSimUnits(local);
    }

    void Fail(string why)
    {
        Problem ??= why;
        Abandon = true;
    }
}
