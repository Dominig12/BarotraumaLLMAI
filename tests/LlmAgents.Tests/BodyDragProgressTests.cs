using LlmAgents.Agents;
using Xunit;

namespace LlmAgents.Tests;

public class BodyDragProgressTests
{
    [Fact]
    public void QuickReleasesHaveTimeToRecoverBeforeFailing()
    {
        var progress = new BodyDragProgress();
        progress.Observe(0, 0, 1, 100);
        for (int n = 0; n < 6; n++) progress.LostGrip(100 + n);
        Assert.False(progress.CannotKeepGrip(106));
        Assert.True(progress.CannotKeepGrip(115));
    }

    [Fact]
    public void LossesWithBodyMovementDoNotExhaustALifetimeRetryLimit()
    {
        var progress = new BodyDragProgress();
        progress.Observe(0, 0, 1, 0);
        for (int n = 1; n <= 12; n++)
        {
            progress.LostGrip(n * 5);
            progress.Observe(n * 50, 0, 1, n * 5 + 1);
            Assert.False(progress.CannotKeepGrip(n * 5 + 2));
        }
        Assert.Equal(0, progress.GripLossesWithoutProgress);
    }

    [Fact]
    public void SmallRagdollJitterDoesNotHideAStuckBody()
    {
        var progress = new BodyDragProgress();
        progress.Observe(0, 0, 1, 0);
        progress.Observe(10, -10, 1, 10);
        progress.Observe(-10, 10, 1, 20);
        Assert.True(progress.Stuck(30));
    }

    [Fact]
    public void EnteringAnotherHullCountsEvenWhenCoordinatesAreSimilar()
    {
        var progress = new BodyDragProgress();
        progress.Observe(0, 0, 1, 0);
        for (int n = 0; n < 6; n++) progress.LostGrip(n);
        progress.Observe(0, 0, 2, 25);
        Assert.False(progress.CannotKeepGrip(30));
        Assert.False(progress.Stuck(30));
        Assert.True(progress.Stuck(55));
    }

    [Fact]
    public void RecoveryDelayIsBoundedAndNoDeadlineExistsBeforeFirstGrip()
    {
        var progress = new BodyDragProgress();
        Assert.False(progress.Stuck(200));
        Assert.False(progress.CannotKeepGrip(200));
        progress.Observe(0, 0, null, 200);
        for (int n = 0; n < 20; n++) progress.LostGrip(201);
        Assert.Equal(203, progress.RetryAt);
        Assert.False(progress.Stuck(229));
        Assert.True(progress.Stuck(230));
    }
}
