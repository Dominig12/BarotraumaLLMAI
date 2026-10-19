using LlmAgents.Agents;
using Xunit;

namespace LlmAgents.Tests;

public class BodyApproachAnchorTests
{
    [Fact]
    public void RagdollJitterDoesNotRepeatedlyRestartAnApproach()
    {
        var anchor = new BodyApproachAnchor();
        Assert.True(anchor.Update(100, 200, 1));
        Assert.False(anchor.Update(110, 190, 1));
        Assert.False(anchor.Update(90, 210, 1));
        Assert.False(anchor.Update(100, 200, 1));
    }

    [Fact]
    public void SlowBodyDriftEventuallyMovesTheDestination()
    {
        var anchor = new BodyApproachAnchor();
        Assert.True(anchor.Update(0, 0, 1));
        Assert.False(anchor.Update(0, -15, 1));
        Assert.False(anchor.Update(0, -30, 1));
        Assert.True(anchor.Update(0, -45, 1));
        Assert.False(anchor.Update(0, -55, 1));
        Assert.True(anchor.Update(0, -90, 1));
    }

    [Fact]
    public void ABodyEnteringAnotherHullOrLeavingTheSubmarineRefreshesItsPath()
    {
        var anchor = new BodyApproachAnchor();
        Assert.True(anchor.Update(0, 0, 1));
        Assert.True(anchor.Update(0, 0, 2));
        Assert.False(anchor.Update(0, 0, 2));
        Assert.True(anchor.Update(0, 0, null));
    }
}
