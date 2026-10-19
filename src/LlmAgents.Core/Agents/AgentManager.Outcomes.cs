using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlmAgents.Agents;

public sealed partial class AgentManager
{
    static readonly JsonSerializerOptions ProgressJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The action ran once. Its Do remains current until the declared outcome is observed.</summary>
    void CompleteSuccessfulStep(BotAgent agent, ActivePlan plan, string result, double now)
    {
        if (plan.Tree.CurrentDo is not { Ensure: { } ensure } step)
        {
            plan.Tree.Report(true, result);
            return;
        }
        plan.WaitFor(PlanWait.Verification, now, deadline: now + step.VerifyTimeoutSeconds);
        plan.VerificationResult = result;
        plan.Tree.MarkVerifying();
        if (VerifyCurrentOutcome(agent, plan, now)) return;
        string line = "action accepted; verifying actual result: " + ensure.Text;
        agent.RecordResult(line);
        agent.AddActivity("verify", line, now);
        Log.Info($"[{agent.Name}] {line}");
    }

    bool VerifyCurrentOutcome(BotAgent agent, ActivePlan plan, double now)
    {
        if (plan.Tree.CurrentDo?.Ensure is not { } condition) return false;
        // Never confirm against the observation from before the action or a previous poll.
        agent.LastObservation = null;
        var context = new PlanConditions.ObservationContext(() => Look(agent, now), now - plan.StartedAt,
            true, plan.VerificationResult, taskRunning: false);
        if (!condition.IsTrue(context)) return false;
        string result = plan.VerificationResult + "; verified actual result: " + condition.Text;
        plan.StopWaiting();
        plan.Tree.Report(true, result);
        agent.RecordResult(result);
        agent.AddActivity("verified", result, now);
        Log.Info($"[{agent.Name}] {result}");
        return true;
    }

    void CheckVerification(BotAgent agent, ActivePlan plan, double now)
    {
        if (VerifyCurrentOutcome(agent, plan, now)) Continue(agent, plan, "verified actual result", now);
        else if (now >= plan.WaitDeadline)
        {
            string result = $"postcondition failed after {plan.WaitDeadline - plan.WaitingSince:0.#}s: "
                + (plan.Tree.CurrentDo?.Ensure?.Text ?? "current action unavailable")
                + "; the action was not repeated; native result: " + Tools.Clip(plan.VerificationResult ?? "unavailable", 240);
            agent.RecordResult(result);
            agent.AddError(result);
            agent.AddActivity("verification-failed", result, now);
            Log.Info($"[{agent.Name}] {result}");
            Resume(agent, plan, false, result, now);
        }
    }

    GameTaskProgress? ReadTaskProgress(int botId)
    {
        try { return game.TaskProgress(botId); }
        catch (Exception e)
        {
            // Diagnostic absence must not abort working tasks or invent a failure.
            Log.ErrorThrottled("task-progress:" + botId, "task progress is unavailable: " + e.Message);
            return null;
        }
    }

    void ReviewBlockedTask(BotAgent agent, ActivePlan plan, double now)
    {
        var progress = ReadTaskProgress(agent.Id);
        if (!TaskProgressRules.NeedsReview(progress, plan.AwaitedTaskId))
        {
            // Missing/stale diagnostics say nothing about recovery; only a matching fresh
            // progress snapshot can rearm a warning for this task.
            if (progress != null && progress.TaskId == plan.AwaitedTaskId && progress.NoProgressSeconds < TaskProgressRules.ReviewAfterSeconds)
                plan.ProgressReviewKey = null;
            return;
        }
        string key = progress!.TaskId + ":" + progress.BlockerCode;
        if (plan.ProgressReviewKey == key) return;
        plan.ProgressReviewKey = key;
        string detail = $"task {progress.TaskId} needs review: blocker={progress.BlockerCode}, "
            + $"phase={progress.Phase}, no progress for {progress.NoProgressSeconds:0}s; "
            + Tools.Clip(progress.Blocker ?? progress.Step, 220)
            + ". Task remains active. Inspect current progress, keep/edit/cancel the plan; do not repeat completed effects.";
        agent.RecordResult(detail);
        agent.AddActivity("task-blocked", detail, now);
        agent.AddEvent(EventKind.PlanReview, detail, now);
        Log.Info($"[{agent.Name}] {detail}");
    }
}
