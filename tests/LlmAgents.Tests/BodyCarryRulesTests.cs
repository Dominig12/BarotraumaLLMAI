using LlmAgents.Agents;
using Xunit;

namespace LlmAgents.Tests;

public class BodyCarryRulesTests
{
    static readonly BodyCarrySnapshot Ready = new(
        CarrierAvailable: true, TargetIsBody: true, DestinationAvailable: true,
        Delivered: false, HoldingTarget: false, OtherHolder: false, Active: true, CanAcquire: true);

    [Fact]
    public void SustainedCarryAcquiresOnceAndNeverReleasesOrReselectsAnIntactGrip()
    {
        var carry = new BodyCarryRules();
        Assert.Equal(BodyCarryAction.Acquire, carry.Evaluate(Ready).Action);
        var held = Ready with { HoldingTarget = true };
        for (int frame = 0; frame < 18000; frame++)
        {
            var decision = carry.Evaluate(held);
            Assert.Equal(BodyCarryAction.Hold, decision.Action);
            Assert.False(decision.ReleaseGrip);
        }
        Assert.True(carry.HasHeld);
        Assert.False(carry.Finished);
    }

    [Fact]
    public void CarrierArrivalDoesNotCompleteCarryBeforeTheBodyArrives()
    {
        var carry = new BodyCarryRules();
        // The carrier is already at its navigation endpoint. Only Delivered describes the body's position.
        var stillOutside = Ready with { HoldingTarget = true, CanAcquire = false };
        for (int frame = 0; frame < 600; frame++)
            Assert.Equal(new BodyCarryDecision(BodyCarryAction.Hold), carry.Evaluate(stillOutside));
        Assert.False(carry.Finished);

        var complete = carry.Evaluate(stillOutside with { Delivered = true });
        Assert.Equal(BodyCarryAction.Complete, complete.Action);
        Assert.True(complete.ReleaseGrip);
        Assert.True(carry.Finished);
        Assert.Equal(new BodyCarryDecision(BodyCarryAction.Finished), carry.Evaluate(stillOutside));
    }

    [Fact]
    public void DeliveryNeverReleasesACharacterTheTaskDoesNotHold()
    {
        var carry = new BodyCarryRules();
        var complete = carry.Evaluate(Ready with { Delivered = true });
        Assert.Equal(BodyCarryAction.Complete, complete.Action);
        Assert.False(complete.ReleaseGrip);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("carrier")]
    [InlineData("awake")]
    [InlineData("destination")]
    [InlineData("taken")]
    public void TerminalConditionsPreventRegrabbingEvenIfAnOldObjectiveActsAgain(string reason)
    {
        var carry = new BodyCarryRules();
        var held = Ready with { HoldingTarget = true };
        carry.Evaluate(held);
        var ended = reason switch
        {
            "cancelled" => held with { Cancelled = true },
            "carrier" => held with { CarrierAvailable = false },
            "awake" => held with { TargetIsBody = false },
            "destination" => held with { DestinationAvailable = false },
            _ => held with { OtherHolder = true }
        };
        var stop = carry.Evaluate(ended);
        Assert.Equal(BodyCarryAction.Stop, stop.Action);
        Assert.True(stop.ReleaseGrip);
        Assert.NotNull(stop.Failure);
        Assert.True(carry.Finished);
        for (int frame = 0; frame < 120; frame++)
            Assert.Equal(new BodyCarryDecision(BodyCarryAction.Finished), carry.Evaluate(Ready));
    }

    [Fact]
    public void AnotherHolderWinsEvenWhenTheBodyIsAlreadyAtTheDestination()
    {
        var carry = new BodyCarryRules();
        var decision = carry.Evaluate(Ready with { OtherHolder = true, Delivered = true });
        Assert.Equal(BodyCarryAction.Stop, decision.Action);
        Assert.False(decision.ReleaseGrip);
        Assert.Equal("body taken by another character", decision.Failure);
    }

    [Fact]
    public void EmergencyPauseReleasesOwnedGripOnceAndDoesNotCompeteForTheCharacter()
    {
        var carry = new BodyCarryRules();
        carry.Evaluate(Ready with { HoldingTarget = true });
        var interrupted = Ready with { HoldingTarget = true, Active = false };
        var pause = carry.Evaluate(interrupted);
        Assert.Equal(BodyCarryAction.Pause, pause.Action);
        Assert.True(pause.ReleaseGrip);
        Assert.False(carry.Finished);
        // No repeated release operation even if the game's observation has not updated yet.
        Assert.Equal(new BodyCarryDecision(BodyCarryAction.Pause), carry.Evaluate(interrupted));
        for (int frame = 0; frame < 600; frame++)
            Assert.Equal(new BodyCarryDecision(BodyCarryAction.Pause), carry.Evaluate(interrupted with { HoldingTarget = false }));
        Assert.Equal(BodyCarryAction.Acquire, carry.Evaluate(Ready).Action);
    }

    [Fact]
    public void PhysicsReleaseRequiresARealApproachAndAcquisitionPermission()
    {
        var carry = new BodyCarryRules();
        carry.Evaluate(Ready with { HoldingTarget = true });
        // Native visibility/range checks or the recovery delay forbid grabbing at the old position.
        var obstructed = Ready with { CanAcquire = false };
        for (int frame = 0; frame < 600; frame++)
            Assert.Equal(new BodyCarryDecision(BodyCarryAction.Approach), carry.Evaluate(obstructed));
        Assert.Equal(BodyCarryAction.Acquire, carry.Evaluate(Ready).Action);
        Assert.Equal(BodyCarryAction.Hold, carry.Evaluate(Ready with { HoldingTarget = true }).Action);
    }

    [Fact]
    public void CancellationAndInvalidTargetsCannotBeHiddenByDelivery()
    {
        var cancelled = new BodyCarryRules().Evaluate(Ready with { Delivered = true, Cancelled = true });
        var awake = new BodyCarryRules().Evaluate(Ready with { Delivered = true, TargetIsBody = false });
        Assert.Equal(BodyCarryAction.Stop, cancelled.Action);
        Assert.Equal(BodyCarryAction.Stop, awake.Action);
    }
}
