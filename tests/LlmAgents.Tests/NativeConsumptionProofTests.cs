using LlmAgents.Agents;
using Xunit;

namespace LlmAgents.Tests;

public class NativeConsumptionProofTests
{
    [Fact]
    public void FastNativeConsumptionCanQueueRemovalBeforeRemovedFlagIsUpdated() =>
        Assert.True(NativeConsumptionProof.ConfirmsRemoval(removedBefore: false, queuedBefore: false, removedAfter: false, queuedAfter: true));

    [Fact]
    public void ImmediateNativeRemovalIsConfirmedWithoutAQueueEntry() =>
        Assert.True(NativeConsumptionProof.ConfirmsRemoval(removedBefore: false, queuedBefore: false, removedAfter: true, queuedAfter: false));

    [Fact]
    public void NativeRemovalCanLeaveBothRemovedAndQueuedFlagsSet() =>
        Assert.True(NativeConsumptionProof.ConfirmsRemoval(removedBefore: false, queuedBefore: false, removedAfter: true, queuedAfter: true));

    [Fact]
    public void ItemStillPresentAfterTheNativeCallIsNotClaimedAsConsumed() =>
        Assert.False(NativeConsumptionProof.ConfirmsRemoval(removedBefore: false, queuedBefore: false, removedAfter: false, queuedAfter: false));

    [Fact]
    public void RemovalAlreadyQueuedBySomeoneElseIsNotAttributedToThisCall() =>
        Assert.False(NativeConsumptionProof.ConfirmsRemoval(removedBefore: false, queuedBefore: true, removedAfter: false, queuedAfter: true));

    [Fact]
    public void ProcessingAnExternalQueueDuringTheCallDoesNotBecomeOurConsumptionProof() =>
        Assert.False(NativeConsumptionProof.ConfirmsRemoval(removedBefore: false, queuedBefore: true, removedAfter: true, queuedAfter: false));

    [Fact]
    public void AnItemAlreadyRemovedBeforeTheCallIsNotConsumedTwice() =>
        Assert.False(NativeConsumptionProof.ConfirmsRemoval(removedBefore: true, queuedBefore: false, removedAfter: true, queuedAfter: false));

    [Fact]
    public void AnAlreadyRemovedAndQueuedItemCannotGenerateAnotherReceipt() =>
        Assert.False(NativeConsumptionProof.ConfirmsRemoval(removedBefore: true, queuedBefore: true, removedAfter: true, queuedAfter: true));
}
