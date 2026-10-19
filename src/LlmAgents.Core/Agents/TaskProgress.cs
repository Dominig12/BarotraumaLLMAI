using System;

namespace LlmAgents.Agents;

/// <summary>A factual snapshot of a running native task. Phase and BlockerCode are stable machine-readable codes;
/// Step and Blocker are display text and are never parsed. Counts describe confirmed work, not predicted success.
/// NoProgressSeconds is measured since a meaningful world change, not a change to the display text or a task tick.</summary>
/// <param name="Phase">running, waiting, blocked, or cleanup.</param>
public sealed record GameTaskProgress(long TaskId, string Phase, string Step, double ElapsedSeconds, double NoProgressSeconds,
    string? BlockerCode = null, string? Blocker = null, int? CompletedUnits = null, int? TotalUnits = null);

/// <summary>Advisory task diagnosis. A review requests a decision; it never retries, cancels, or claims completion.</summary>
public static class TaskProgressRules
{
    public const double ReviewAfterSeconds = 45;

    /// <summary>Only a correlated task with an explicit, persistent blocker needs review. Stationary useful work
    /// without a blocker is normal; native cleanup has its own retry/ownership mechanism and is excluded.</summary>
    public static bool NeedsReview(GameTaskProgress? progress, long? awaitedTaskId)
    {
        if (progress == null || awaitedTaskId is not > 0 || progress.TaskId != awaitedTaskId) return false;
        if (progress.Phase is not ("running" or "waiting" or "blocked")) return false;
        if (string.IsNullOrWhiteSpace(progress.BlockerCode)) return false;
        return double.IsFinite(progress.NoProgressSeconds) && progress.NoProgressSeconds >= ReviewAfterSeconds;
    }

    /// <summary>Concise human-readable history text. Consumers use the original typed fields for decisions.</summary>
    public static string Describe(GameTaskProgress progress)
    {
        var count = progress.CompletedUnits is { } done ? $", confirmed {done}" + (progress.TotalUnits is { } total ? $"/{total}" : "") : "";
        var blocked = !string.IsNullOrWhiteSpace(progress.BlockerCode) ? $", blocker={progress.BlockerCode}" +
            (!string.IsNullOrWhiteSpace(progress.Blocker) ? ": " + progress.Blocker : "") : "";
        var stale = double.IsFinite(progress.NoProgressSeconds) ? $", no progress {Math.Max(0, progress.NoProgressSeconds):0}s" : "";
        return $"task #{progress.TaskId} {progress.Phase}{count}{stale}{blocked}";
    }
}
