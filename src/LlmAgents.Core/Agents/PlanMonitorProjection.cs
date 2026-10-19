using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlmAgents.SettingsUi;

namespace LlmAgents.Agents;

/// <summary>Read-only projection of the tree's structured snapshot. Does not advance a plan or observe the game.</summary>
public static class PlanMonitorProjection
{
    public static MonitorPlanSnapshot Build(ActivePlan plan, double now, GameTaskProgress? progress = null)
    {
        var tree = plan.Tree.Snapshot();
        bool waiting = plan.Waiting is not (PlanWait.None or PlanWait.Yield);
        bool deadline = waiting && double.IsFinite(plan.WaitDeadline) && plan.WaitDeadline > 0;
        var snapshot = new MonitorPlanSnapshot
        {
            PlanId = plan.Id,
            Revision = Int(tree, "revision") ?? plan.Tree.Revision,
            Status = Text(tree, "status") ?? "running",
            CurrentNodeId = Text(tree, "current"),
            ElapsedSeconds = Elapsed(now, plan.StartedAt),
            Waiting = plan.Waiting.ToString().ToLowerInvariant(),
            WaitingSeconds = waiting ? Elapsed(now, plan.WaitingSince) : 0,
            WaitRemainingSeconds = deadline ? Elapsed(plan.WaitDeadline, now) : null,
            WaitTimeoutSeconds = deadline ? Elapsed(plan.WaitDeadline, plan.WaitingSince) : null,
            WaitRoom = plan.WaitRoom,
            AwaitedTaskId = plan.AwaitedTaskId,
            TaskTimedOut = plan.TaskTimedOut,
            CancellationFailure = plan.CancellationFailure,
            VerificationCondition = plan.Waiting == PlanWait.Verification ? plan.Tree.CurrentDo?.Ensure?.Text : null,
            VerificationNativeResult = plan.VerificationResult,
            LastOk = tree["last_ok"] is JsonValue value && value.TryGetValue<bool>(out var ok) ? ok : null,
            LastResult = Text(tree, "last_result"),
            Failure = Text(tree, "failure"),
            ExecutedSteps = plan.ExecutedSteps,
            ExecutionBudget = ActivePlan.MaximumExecutedSteps,
        };
        if (plan.Waiting == PlanWait.Task && plan.AwaitedTaskId is > 0 && progress?.TaskId == plan.AwaitedTaskId)
            snapshot.TaskProgress = new MonitorTaskProgress
            {
                TaskId = progress.TaskId, Phase = progress.Phase, Step = progress.Step,
                ElapsedSeconds = Nonnegative(progress.ElapsedSeconds), NoProgressSeconds = Nonnegative(progress.NoProgressSeconds),
                BlockerCode = progress.BlockerCode, Blocker = progress.Blocker,
                CompletedUnits = progress.CompletedUnits, TotalUnits = progress.TotalUnits,
            };
        if (tree["root"] is JsonObject root)
        {
            snapshot.RootNodeId = Text(root, "id") ?? "s0";
            Walk(root, null, 0, 0, "root", snapshot);
        }
        return snapshot;
    }

    /// <summary>Frozen terminal view. Current markers/countdowns disappear, while outcomes and completed work remain.</summary>
    public static MonitorPlanSnapshot BuildEnded(ActivePlan plan, double now, double endedAt, string status,
        string? reason = null, GameTaskProgress? progress = null)
    {
        var snapshot = Build(plan, endedAt, progress);
        snapshot.Active = false;
        snapshot.Status = status;
        snapshot.EndReason = reason;
        snapshot.EndedAgoSeconds = Elapsed(now, endedAt);
        snapshot.CurrentNodeId = null;
        snapshot.WaitRemainingSeconds = null;
        foreach (var node in snapshot.Nodes)
        {
            node.IsCurrent = false;
            if (status is "cancelled" or "replaced" && node.Status is "pending" or "running" or "verifying")
                node.Status = "cancelled";
            else if (status == "failed")
            {
                if (node.Status is "running" or "verifying") node.Status = "failed";
                else if (node.Status == "pending") node.Status = "skipped";
            }
        }
        return snapshot;
    }

    public static MonitorPlanSummary Summarize(MonitorPlanSnapshot snapshot)
    {
        var tools = snapshot.Nodes.Where(n => n.Kind == "action").Select(n => n.Tool).Where(t => t != null).ToList();
        string label = string.Join(" → ", tools.Take(3));
        if (tools.Count > 3) label += " …";
        if (label.Length == 0) label = snapshot.Nodes.FirstOrDefault(n => n.Kind != "sequence")?.Kind ?? "sequence";
        return new MonitorPlanSummary
        {
            PlanId = snapshot.PlanId, Revision = snapshot.Revision, Status = snapshot.Status,
            ElapsedSeconds = snapshot.ElapsedSeconds, EndedAgoSeconds = snapshot.EndedAgoSeconds,
            Reason = snapshot.EndReason ?? snapshot.Failure, Label = label,
        };
    }

    static void Walk(JsonObject source, string? parent, int depth, int index, string branch, MonitorPlanSnapshot snapshot)
    {
        string id = Text(source, "id") ?? "";
        var node = new MonitorPlanNode
        {
            Id = id, ParentId = parent, Depth = depth, Index = index, Branch = branch,
            Status = Text(source, "status") ?? "pending", IsCurrent = id == snapshot.CurrentNodeId,
            LastOk = source["ok"] is JsonValue outcome && outcome.TryGetValue<bool>(out var ok) ? ok : null,
            Result = Text(source, "result"),
            Ensure = Text(source, "ensure"), TimeoutSeconds = Number(source, "timeout"),
            VerifyTimeoutSeconds = Number(source, "verify_timeout"),
        };
        snapshot.Nodes.Add(node);
        if (source.ContainsKey("do"))
        {
            node.Kind = "action";
            node.Tool = Text(source, "do");
            node.ArgumentsJson = source["args"]?.ToJsonString() ?? "{}";
        }
        else if (source.ContainsKey("if"))
        {
            node.Kind = "if"; SetCondition(node, source, "if");
            Branch("then", 0); Branch("else", 1);
        }
        else if (source.ContainsKey("wait_until"))
        {
            node.Kind = "wait"; SetCondition(node, source, "wait_until");
        }
        else if (source.ContainsKey("repeat"))
        {
            node.Kind = "repeat";
            if (source.ContainsKey("until")) SetCondition(node, source, "until");
            else if (source.ContainsKey("while")) SetCondition(node, source, "while");
            node.IterationsStarted = Int(source, "iterations_started");
            node.MaxIterations = Int(source, "max");
            Branch("repeat", 0);
        }
        else if (source.ContainsKey("try"))
        {
            node.Kind = "try"; Branch("try", 0); Branch("catch", 1);
        }
        else if (source["steps"] is JsonArray steps)
        {
            node.Kind = "sequence";
            for (int i = 0; i < steps.Count; i++)
                if (steps[i] is JsonObject child) Walk(child, id, depth + 1, i, "step", snapshot);
        }
        else node.Kind = "unknown";

        void Branch(string key, int at)
        {
            if (source[key] is JsonObject child) Walk(child, id, depth + 1, at, key, snapshot);
        }
    }

    static void SetCondition(MonitorPlanNode node, JsonObject source, string key)
    {
        node.Condition = Text(source, key);
        node.ConditionKind = key;
    }
    static string? Text(JsonObject source, string key) =>
        source[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    static int? Int(JsonObject source, string key) =>
        source[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
    static double? Number(JsonObject source, string key) => source[key]?.GetValueKind() == JsonValueKind.Number &&
        double.TryParse(source[key]!.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number : null;
    static double Elapsed(double end, double start) => Nonnegative(end - start);
    static double Nonnegative(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
}
