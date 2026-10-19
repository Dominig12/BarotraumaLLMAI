using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using LlmAgents.Llm;

namespace LlmAgents.Agents;

public sealed partial class AgentManager
{
    public string InspectPlan(BotAgent agent)
    {
        if (agent.Plan is not { } plan) return "{\"active\":false}";
        var snapshot = plan.Tree.Snapshot();
        snapshot["active"] = true;
        snapshot["plan_id"] = plan.Id;
        snapshot["waiting"] = plan.Waiting.ToString().ToLowerInvariant();
        snapshot["task_id"] = plan.AwaitedTaskId;
        snapshot["task_timed_out"] = plan.TaskTimedOut;
        snapshot["cancellation_failure"] = plan.CancellationFailure;
        snapshot["verification_condition"] = plan.Waiting == PlanWait.Verification ? plan.Tree.CurrentDo?.Ensure?.Text : null;
        snapshot["verification_native_result"] = plan.VerificationResult;
        if (ReadTaskProgress(agent.Id) is { } progress && progress.TaskId == plan.AwaitedTaskId)
            snapshot["task_progress"] = System.Text.Json.JsonSerializer.SerializeToNode(progress, ProgressJson);
        snapshot["wait_room"] = plan.WaitRoom;
        snapshot["wait_remaining_seconds"] = plan.WaitDeadline > 0 ? Math.Max(0, plan.WaitDeadline - clock()) : null;
        snapshot["elapsed_seconds"] = Math.Max(0, clock() - plan.StartedAt);
        snapshot["executed_steps"] = plan.ExecutedSteps;
        snapshot["execution_budget"] = ActivePlan.MaximumExecutedSteps;
        snapshot["goal"] = agent.Goal;
        return snapshot.ToJsonString();
    }

    static string? PlanVersionError(ActivePlan? plan, JsonObject arguments)
    {
        if (Tools.Str(arguments, "expected_plan_id") is { } expectedId && plan?.Id != expectedId)
            return "stale plan_id; inspect_plan before editing the current plan";
        if (arguments["expected_revision"] is JsonValue value && value.TryGetValue<int>(out int expectedRevision)
            && plan?.Tree.Revision != expectedRevision)
            return "stale plan revision; inspect_plan and retry against the current revision";
        return null;
    }

    string? ValidatePlanCall(BotAgent agent, ToolCall call)
    {
        if (ResolveTool(agent, call.Name) is not { } definition || !IsToolAllowed(agent, call.Name)
            || definition.ControlOnly && !agent.AiControl)
            return $"tool '{call.Name}' is not available for this bot";
        return Tools.ValidateAgainstSchema(call.Arguments, definition.Schema);
    }

    string? ValidatePlanActions(BotAgent agent, PlanTree.Node node)
    {
        if (node is PlanTree.Do action && ValidatePlanCall(agent, action.Call) is { } error)
            return $"step {node.Id} {action.Call.Name}: {error}";
        foreach (var child in node.Children)
            if (ValidatePlanActions(agent, child) is { } childError) return childError;
        return null;
    }

    bool ApplyPlanEdits(BotAgent agent, List<ToolCall> calls, double now)
    {
        if (agent.Plan is not { } plan) { RejectPlan(agent, "no active plan to edit; use plan to create one", now); return false; }
        var edits = new List<PlanEdit>();
        foreach (var call in calls.Where(c => c.Name is "update_plan" or "edit_plan" or "append_plan"))
        {
            if (PlanVersionError(plan, call.Arguments) is { } stale) { RejectPlan(agent, stale, now); return false; }
            if (call.Name == "edit_plan")
            {
                foreach (var operation in call.Arguments["operations"]!.AsArray().OfType<JsonObject>())
                {
                    string op = Tools.Str(operation, "op")!;
                    if (op == "remove" && operation.ContainsKey("steps") || op != "remove" && operation["steps"] == null)
                    { RejectPlan(agent, "remove must omit steps; other edits require steps", now); return false; }
                    edits.Add(new PlanEdit(op, Tools.Str(operation, "node")!, operation["steps"]?.DeepClone()));
                }
            }
            else edits.Add(new PlanEdit(call.Name == "append_plan" ? "append" : "replace",
                Tools.Str(call.Arguments, "node") ?? "s0", call.Arguments["steps"]?.DeepClone()));
        }
        if (edits.Count > 16) { RejectPlan(agent, "at most 16 edit operations in one reply", now); return false; }
        try
        {
            foreach (var edit in edits.Where(e => e.Steps != null))
            {
                var fragment = PlanTree.Build(edit.Steps, PlanToolNames(agent));
                if (ValidatePlanActions(agent, fragment.Root) is { } badAction) throw new FormatException(badAction);
            }
        }
        catch (FormatException e) { RejectPlan(agent, e.Message, now); return false; }
        var candidate = plan.Tree.Fork();
        var change = candidate.Edit(edits, PlanToolNames(agent));
        if (!change.Success) { RejectPlan(agent, change.Error ?? "invalid edit", now); return false; }
        if (change.InvalidatesCurrent)
        {
            if (StopAwaitedWork(agent, plan) is { } stopError) { RejectPlan(agent, stopError, now); return false; }
            plan.StopWaiting();
            agent.TaskTool = null;
        }
        plan.Tree = candidate;
        foreach (var call in calls.Where(c => c.Name is "update_plan" or "edit_plan" or "append_plan"))
            RecordPlanControl(agent, call.Name, $"ok, revision {candidate.Revision}; plan now: {candidate.Describe(500)}", now);
        return true;
    }

    public string CancelPlan(BotAgent agent, string? reason = null, bool clearGoal = false, int? expectedRevision = null, string? expectedPlanId = null)
    {
        var plan = agent.Plan;
        if (expectedPlanId != null && plan?.Id != expectedPlanId) return "error: stale plan_id; inspect_plan before cancellation";
        if (expectedRevision != null && plan?.Tree.Revision != expectedRevision) return "error: stale plan revision; inspect_plan before cancellation";
        if (plan != null && StopAwaitedWork(agent, plan) is { } error) return "error: " + error;
        if (plan != null) ArchivePlan(agent, plan, "cancelled", reason ?? "Plan cancelled", clock());
        agent.ClearPlan();
        agent.TaskTool = null;
        agent.Events.RemoveAll(e => e.Kind == EventKind.PlanReview);
        if (clearGoal) agent.SetGoal(null, clock());
        return plan == null ? "ok (no active plan)" : "ok, plan cancelled" + (string.IsNullOrWhiteSpace(reason) ? "" : ": " + Tools.Clip(reason, 200));
    }

    /// <summary>Only stop work still owned by this plan; never cancel a newer task or a human's travel order.</summary>
    string? StopAwaitedWork(BotAgent agent, ActivePlan plan)
    {
        if (plan.Waiting == PlanWait.Task)
        {
            var current = game.CurrentTaskId(agent.Id);
            if (plan.AwaitedTaskId == null || current == plan.AwaitedTaskId)
            {
                string result = game.CancelCurrentTask(agent.Id);
                if (IsError(result)) return "could not cancel the plan's current task: " + result;
            }
        }
        if (plan.Waiting == PlanWait.Arrival || plan.TravelPending)
        {
            string result = game.EndTravel(agent.Id);
            if (IsError(result)) return "could not release the plan's travel order: " + result;
        }
        return null;
    }

    void RecordPlanControl(BotAgent agent, string tool, string result, double now)
    {
        string line = tool + " -> " + result;
        bool failed = IsError(result);
        agent.Stats.Call(tool, failed);
        agent.RecordResult(line);
        agent.AddActivity(failed ? "error" : "plan", line, now);
        if (failed) agent.AddError(line);
        else agent.ActedSinceDecision = true;
        Log.Info($"[{agent.Name}] {line}");
    }

    void CheckTaskWait(BotAgent agent, ActivePlan plan, double now)
    {
        if (plan.AwaitedTaskId is { } awaited && game.CurrentTaskId(agent.Id) != awaited)
        {
            Resume(agent, plan, false, plan.TaskTimedOut ? "task timed out; the native task is no longer active"
                : "the awaited task was cancelled or replaced without a matching completion report", now);
            return;
        }
        if (plan.WaitDeadline > 0 && now >= plan.WaitDeadline)
        {
            plan.TaskTimedOut = true;
            string? cleanup = StopAwaitedWork(agent, plan);
            if (cleanup != null)
            {
                if (plan.CancellationFailure != cleanup)
                {
                    agent.RecordResult("plan paused after task timeout: " + cleanup + "; waiting to release the owned task before recovery");
                    agent.AddEvent(EventKind.TaskFailed, "timed-out task could not be cancelled; the plan still owns it and recovery waits for release", now);
                }
                plan.CancellationFailure = cleanup;
                plan.NextCheckAt = now + 5;
                return;
            }
            Resume(agent, plan, false, "task timed out", now);
        }
        else ReviewBlockedTask(agent, plan, now);
    }

    static bool LegacyTaskFailed(string text)
    {
        // Compatibility for backends predating typed Success. Native reports always carry it.
        int separator = text.IndexOf("' ", StringComparison.Ordinal);
        string outcome = separator >= 0 ? text.Substring(separator + 2).TrimStart() : text.TrimStart();
        return outcome.StartsWith("failed", StringComparison.Ordinal) || outcome.StartsWith("cancelled", StringComparison.Ordinal);
    }
}
