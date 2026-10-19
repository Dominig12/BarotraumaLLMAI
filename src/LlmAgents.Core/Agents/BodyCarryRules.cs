namespace LlmAgents.Agents;

/// <summary>The intent for this frame; only Acquire is allowed to select a previously unheld body.</summary>
public enum BodyCarryAction { Approach, Acquire, Hold, Pause, Complete, Stop, Finished }

/// <summary>
/// Facts from the game, rather than progress inferred from the carrier's completed path. Active means that
/// the carry objective may act now. CanAcquire must include native interaction checks and recovery delays.
/// </summary>
public readonly record struct BodyCarrySnapshot(
    bool CarrierAvailable,
    bool TargetIsBody,
    bool DestinationAvailable,
    bool Delivered,
    bool HoldingTarget,
    bool OtherHolder,
    bool Active,
    bool CanAcquire,
    bool Cancelled = false);

public readonly record struct BodyCarryDecision(BodyCarryAction Action, bool ReleaseGrip = false, string? Failure = null);

/// <summary>
/// Owns the lifetime of a carry intent. An intact grip is kept across frames and path completion; a finished
/// or cancelled intent cannot acquire again. Native physics remains responsible for whether a grip survives.
/// </summary>
public sealed class BodyCarryRules
{
    bool pauseReleaseIssued;
    public bool Finished { get; private set; }
    public bool HasHeld { get; private set; }

    public BodyCarryDecision Evaluate(BodyCarrySnapshot state)
    {
        if (Finished) return new(BodyCarryAction.Finished);
        if (state.Cancelled) return End(state, "cancelled");
        if (!state.CarrierAvailable) return End(state, "carrier is gone or dead");
        if (!state.TargetIsBody) return End(state, "target is gone or conscious again");
        if (!state.DestinationAvailable) return End(state, "destination is gone");
        if (state.OtherHolder) return End(state, "body taken by another character");
        if (state.Delivered)
        {
            Finished = true;
            return new(BodyCarryAction.Complete, ReleaseGrip: state.HoldingTarget);
        }

        if (!state.Active)
        {
            // The native AI may need its hands for a higher-priority action or diving gear. The carry
            // intent stays pending, but must not select the body while that action owns the character.
            bool release = state.HoldingTarget && !pauseReleaseIssued;
            pauseReleaseIssued |= release;
            return new(BodyCarryAction.Pause, ReleaseGrip: release);
        }
        pauseReleaseIssued = false;

        if (state.HoldingTarget)
        {
            HasHeld = true;
            return new(BodyCarryAction.Hold);
        }
        return new(state.CanAcquire ? BodyCarryAction.Acquire : BodyCarryAction.Approach);
    }

    BodyCarryDecision End(BodyCarrySnapshot state, string failure)
    {
        Finished = true;
        return new(BodyCarryAction.Stop, ReleaseGrip: state.HoldingTarget, Failure: failure);
    }
}
