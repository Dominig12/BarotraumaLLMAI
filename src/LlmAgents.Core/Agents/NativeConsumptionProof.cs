namespace LlmAgents.Agents;

/// <summary>Confirms that a native consumption call changed a previously live, unqueued item into a removed or
/// queued-for-removal item. Already pending/external removal is not evidence that this call consumed it.</summary>
public static class NativeConsumptionProof
{
    public static bool ConfirmsRemoval(bool removedBefore, bool queuedBefore, bool removedAfter, bool queuedAfter) =>
        !removedBefore && !queuedBefore && (removedAfter || queuedAfter);
}
