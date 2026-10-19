using System;

namespace LlmAgents.Agents;

/// <summary>Stops a native task once, retrying only native objective cleanup after a transient failure.</summary>
public sealed class TaskStopState
{
    public bool Started { get; private set; }
    public bool CleanupPending { get; private set; }
    public string? Report { get; private set; }

    public string Finish(string report, Action onStopping, Action clearObjective, Action onFinish)
    {
        if (Started)
        {
            // Ownership may already have passed to a replacement objective. Never repeat the release hooks.
            if (CleanupPending) { clearObjective(); CleanupPending = false; }
            return Report!;
        }

        Started = true;
        Report = report;
        CleanupPending = true;
        try
        {
            try { onStopping(); }
            finally { clearObjective(); CleanupPending = false; }
        }
        finally
        {
            try { onFinish(); } catch { /* Reservations must be released on a best-effort basis. */ }
        }
        return Report!;
    }
}
