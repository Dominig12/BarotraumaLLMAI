using System;
using System.Linq;
using Barotrauma;
using LlmAgents.Agents;
using Microsoft.Xna.Framework;

namespace LlmAgents;

/// <summary>
/// Keeps native ownership of a body from acquisition until delivery. Navigation is a subobjective,
/// never a replacement forced order: switching forced orders can briefly activate a rescue/idle
/// objective which releases SelectedCharacter. The game still owns the physical pull joints and
/// all ordinary distance, visibility, incapacity and competing-holder checks.
/// </summary>
internal sealed class AIObjectiveDragBody : AIObjective
{
    enum NavigationPhase { Approach, Carry }

    readonly Character target;
    readonly ISpatialEntity destination;
    readonly string destinationName;
    readonly BodyCarryRules carry = new();
    readonly BodyDragProgress progress = new();
    readonly BodyApproachAnchor approachAnchor = new();
    AIObjectiveGoTo? navigation;
    NavigationPhase navigationPhase;
    Vector2 navigationPosition;
    Hull? navigationHull;
    bool holdingLastFrame;
    bool cancelled;
    // Only active carrying/recovery consumes the progress allowance. Native safety/gear tasks
    // and emergency preemption can take time without indicating a physically stuck corpse.
    double progressTime;
    Vector2 lastApproachPosition;
    double approachStillTime;

    public override Identifier Identifier { get; set; } = "drag body".ToIdentifier();
    public override bool ConcurrentObjectives => true;
    public override bool AbandonWhenCannotCompleteSubObjectives => false;
    public override bool AllowOutsideSubmarine => true;
    public override bool AllowInAnySub => true;
    public override bool AllowWhileHandcuffed => false;
    public override bool KeepDivingGearOn => true;

    public string? Failure { get; private set; }
    public bool HasHeld => carry.HasHeld;
    public int GripLosses => progress.GripLossesWithoutProgress;
    public string Status { get; private set; } = "approaching the body";

    public AIObjectiveDragBody(Character bot, HumanAIController ai, Character target,
        ISpatialEntity destination, string destinationName) : base(bot, ai.ObjectiveManager, 1f)
    {
        this.target = target;
        this.destination = destination;
        this.destinationName = destinationName;
        ForceWalkPermanently = true;
        lastApproachPosition = bot.Position;
    }

    bool DestinationAvailable => destination is not Entity { Removed: true }
        && destination is not Character { IsDead: true };

    bool Delivered => destination is Hull hull ? target.CurrentHull == hull
        : Vector2.DistanceSquared(target.WorldPosition, destination.WorldPosition) < 250f * 250f
            && (destination is not Character person || target.CurrentHull == person.CurrentHull);

    bool GettingDivingGear => navigation?.SubObjectives.Any() == true;

    BodyCarrySnapshot Snapshot(bool active, bool canAcquire = false) => new(
        CarrierAvailable: !character.Removed && !character.IsDead,
        TargetIsBody: GameAdapter.IsBody(target),
        DestinationAvailable: DestinationAvailable,
        Delivered: DestinationAvailable && Delivered,
        HoldingTarget: character.SelectedCharacter == target,
        OtherHolder: target.SelectedBy is { Removed: false } holder && holder != character,
        Active: active && !character.IsIncapacitated,
        CanAcquire: canAcquire,
        Cancelled: cancelled);

    public override bool CheckObjectiveState()
    {
        // Completion is exclusively the body's location. Arriving with an empty hand must never
        // complete the parent, nor should a GoTo completion release a body still behind a doorway.
        if (cancelled || Failure != null) return false;
        if (character.Removed || character.IsDead) { Fail("carrier is gone or dead"); return false; }
        if (!GameAdapter.IsBody(target)) { Fail("target is gone or conscious again"); return false; }
        if (!DestinationAvailable) { Fail("destination is gone"); return false; }
        if (target.SelectedBy is { Removed: false } holder && holder != character)
        {
            Fail($"body taken by {holder.Name}");
            return false;
        }
        return Delivered;
    }

    public override void Act(float deltaTime)
    {
        bool active = objectiveManager.CurrentObjective == this && !character.IsIncapacitated;
        if (Failure != null || cancelled) return;
        bool held = character.SelectedCharacter == target;
        bool gettingGear = GettingDivingGear;
        if (active && HasHeld && !gettingGear) progressTime += deltaTime;

        if (holdingLastFrame && !held)
        {
            holdingLastFrame = false;
            if (!gettingGear)
            {
                progress.LostGrip(progressTime);
                Log.Info($"[{character.Name}] drag grip released: {GameAdapter.BodyLabel(target)}; {PositionReport()}; "
                    + $"losses without body movement={GripLosses}");
                RemoveNavigation();
                SteeringManager.Reset();
            }
        }

        // The native GoTo may have to obtain protective equipment before traversing its route.
        // Its GetItem subobjective needs free interaction ownership; never fight it by regrabbing.
        if (gettingGear && active)
        {
            ReleaseGrip();
            holdingLastFrame = false;
            Status = "getting diving gear before moving the body";
            return;
        }

        if (HasHeld)
        {
            progress.Observe(target.Position.X, target.Position.Y, target.CurrentHull?.ID, progressTime);
            if (progress.CannotKeepGrip(progressTime))
            {
                Fail("native dragging repeatedly released the body without moving it; " + PositionReport());
                return;
            }
            if (progress.Stuck(progressTime))
            {
                Fail("body stopped moving before reaching the destination; " + PositionReport());
                return;
            }
        }

        if (navigation is { Abandon: true })
        {
            Fail(navigationPhase == NavigationPhase.Carry ? "cannot reach the destination while dragging; " + PositionReport()
                : "cannot reach the body; " + PositionReport());
            return;
        }

        // Acquisition checks are deliberately irrelevant to an intact grip: the native dragger
        // permits stretched/large ragdolls whose centre is farther away than the initial reach.
        bool inGripRange = !held && active && CanAcquireNativeGrip();
        if (!held && inGripRange && progressTime >= progress.RetryAt
            && (navigation == null || navigationPhase != NavigationPhase.Carry))
        {
            // Give GoTo one frame to assess the destination route and request diving gear BEFORE
            // selecting the corpse. This also avoids replacing an approach objective after grabbing.
            Navigate(NavigationPhase.Carry);
            SteeringManager.Reset();
            Status = "checking the route before grabbing the body";
            return;
        }

        var decision = carry.Evaluate(Snapshot(active,
            inGripRange && progressTime >= progress.RetryAt && navigationPhase == NavigationPhase.Carry));
        if (decision.ReleaseGrip) ReleaseGrip();
        switch (decision.Action)
        {
            case BodyCarryAction.Complete:
                IsCompleted = true;
                return;
            case BodyCarryAction.Stop:
                Fail(decision.Failure ?? "body transport was stopped");
                return;
            case BodyCarryAction.Finished:
                SteeringManager.Reset();
                return;
            case BodyCarryAction.Pause:
                Status = "paused for a higher-priority native action";
                return;
            case BodyCarryAction.Acquire:
                character.SelectCharacter(target);
                if (character.SelectedCharacter == target)
                {
                    // Record the held state immediately. A physical release in the next game
                    // update must be counted even when it lasted less than one coarse task tick.
                    carry.Evaluate(Snapshot(active));
                    holdingLastFrame = true;
                    progress.Observe(target.Position.X, target.Position.Y, target.CurrentHull?.ID, progressTime);
                    Status = $"holding and dragging {target.Name} to {destinationName}";
                    approachStillTime = 0;
                }
                else Fail("native interaction refused the physical grip; " + PositionReport());
                return;
            case BodyCarryAction.Hold:
                holdingLastFrame = true;
                if (navigation == null || navigationPhase != NavigationPhase.Carry || DestinationMoved())
                    Navigate(NavigationPhase.Carry);
                Status = $"holding and dragging {target.Name} to {destinationName}";
                return;
            case BodyCarryAction.Approach:
                if (progressTime < progress.RetryAt)
                {
                    SteeringManager.Reset();
                    Status = "recovering before approaching the body again";
                    return;
                }
                Approach(deltaTime);
                return;
        }
    }

    void Approach(float deltaTime)
    {
        if (Vector2.DistanceSquared(character.Position, lastApproachPosition) >= 40f * 40f)
        {
            lastApproachPosition = character.Position;
            approachStillTime = 0;
        }
        else approachStillTime += deltaTime;
        bool moved = approachAnchor.Update(target.Position.X, target.Position.Y, target.CurrentHull?.ID);
        if (moved) approachStillTime = 0;
        if (approachStillTime >= 15)
        {
            Fail("cannot get close enough to hold the body without an obstruction; " + PositionReport());
            return;
        }
        if (navigation == null || navigationPhase != NavigationPhase.Approach || moved)
            Navigate(NavigationPhase.Approach);
        Status = $"approaching {target.Name} to acquire a physical grip";
    }

    bool DestinationMoved() => destination is Character person
        && (person.CurrentHull != navigationHull
            || Vector2.DistanceSquared(person.WorldPosition, navigationPosition) > 100f * 100f);

    void Navigate(NavigationPhase phase)
    {
        RemoveNavigation();
        navigationPhase = phase;
        ISpatialEntity goal;
        if (phase == NavigationPhase.Approach)
        {
            goal = new OrderTarget(target.WorldPosition, target.CurrentHull);
        }
        else
        {
            navigationPosition = destination.WorldPosition;
            navigationHull = (destination as Character)?.CurrentHull;
            goal = destination is Character person ? new OrderTarget(person.WorldPosition, person.CurrentHull) : destination;
        }
        navigation = new AIObjectiveGoTo(goal, character, objectiveManager, closeEnough: 50f)
        {
            AllowGoingOutside = true,
            ForceWalkPermanently = true,
            AlwaysUseEuclideanDistance = true,
            // Keep steering while the body trails the carrier. A completed GoTo would stop
            // pulling and potentially leave the corpse on the wrong side of a hatch.
            requiredCondition = phase == NavigationPhase.Carry ? () => Delivered : null,
            SpeakIfFails = false
        };
        AddSubObjective(navigation);
    }

    /// <summary>Same hand-to-ragdoll visibility geometry as native HumanoidAnimController.DragCharacter.</summary>
    bool CanAcquireNativeGrip()
    {
        if (!target.CanBeDraggedBy(character) || !character.CanInteractWith(target, 80f)) return false;
        if (target.SelectedBy is { Removed: false } holder && holder != character) return false;
        if (character.AnimController is not HumanoidAnimController human) return false;
        if (human.IsClimbing) return !target.AllowInput;
        var left = human.GetLimb(LimbType.LeftHand);
        var right = human.GetLimb(LimbType.RightHand);
        var fallback = target.AnimController.GetLimb(LimbType.Torso) ?? target.AnimController.MainLimb;
        var targetLeft = target.AnimController.GetLimb(LimbType.LeftForearm) ?? fallback;
        var targetRight = target.AnimController.GetLimb(LimbType.RightForearm) ?? fallback;
        if (left == null || right == null || targetLeft == null || targetRight == null) return false;
        var leftTarget = !targetLeft.IsSevered ? targetLeft : !targetRight.IsSevered ? targetRight : fallback;
        var rightTarget = !targetRight.IsSevered ? targetRight : !targetLeft.IsSevered ? targetLeft : fallback;
        if (leftTarget == null || rightTarget == null) return false;
        var offset = (target.Submarine?.SimPosition ?? Vector2.Zero) - (character.Submarine?.SimPosition ?? Vector2.Zero);
        return Submarine.CheckVisibility(left.SimPosition, leftTarget.SimPosition + offset, ignoreSubs: true) == null
            && Submarine.CheckVisibility(right.SimPosition, rightTarget.SimPosition + offset, ignoreSubs: true) == null;
    }

    public override void OnDeselected()
    {
        var decision = carry.Evaluate(Snapshot(active: false));
        if (decision.ReleaseGrip) ReleaseGrip();
        holdingLastFrame = false;
        // Validation can become terminal in the same frame as emergency preemption. Consume that
        // decision here: the policy emits terminal decisions once, so ignoring it would leave a
        // resumed objective in Finished forever without notifying the owning task.
        if (decision.Action == BodyCarryAction.Stop) Fail(decision.Failure ?? "body transport was stopped");
        else if (decision.Action == BodyCarryAction.Complete) IsCompleted = true;
        else if (decision.Action == BodyCarryAction.Pause) Status = "paused for a higher-priority native action";
        base.OnDeselected();
    }

    public override void Reset()
    {
        base.Reset();
        navigation = null;
        approachStillTime = 0;
        lastApproachPosition = character.Position;
    }

    public void Cancel()
    {
        if (cancelled) return;
        cancelled = true;
        carry.Evaluate(Snapshot(active: false));
        ReleaseGrip();
        RemoveNavigation();
        SteeringManager.Reset();
        Status = "cancelled";
    }

    public override void OnCompleted()
    {
        ReleaseGrip();
        RemoveNavigation();
        SteeringManager.Reset();
        Status = $"body delivered to {destinationName}";
        base.OnCompleted();
    }

    public override void OnAbandon()
    {
        ReleaseGrip();
        RemoveNavigation();
        SteeringManager.Reset();
        base.OnAbandon();
    }

    void RemoveNavigation() => RemoveSubObjective(ref navigation);

    void ReleaseGrip()
    {
        if (character.SelectedCharacter == target) character.DeselectCharacter();
    }

    void Fail(string problem)
    {
        if (Failure != null) return;
        Failure = problem;
        Status = problem;
        Abandon = true;
    }

    string PositionReport() => $"body in {(target.CurrentHull is { } bodyHull ? GameAdapter.RoomId(bodyHull) : "outside")}; "
        + $"carrier in {(character.CurrentHull is { } botHull ? GameAdapter.RoomId(botHull) : "outside")}; "
        + $"distance {Vector2.Distance(character.WorldPosition, target.WorldPosition):0} game units";
}
