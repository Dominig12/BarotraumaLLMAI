using System.Collections.Generic;

// Shared wire DTOs compiled by the server and optional client. No agent/game types belong in this file.
namespace LlmAgents.SettingsUi;

/// <summary>A complete, detached plan tree and its execution facts for the read-only monitor.</summary>
public sealed class MonitorPlanSnapshot
{
    public string PlanId { get; set; } = "";
    public int Revision { get; set; }
    public bool Active { get; set; } = true;
    /// <summary>running / completed / failed / cancelled / replaced.</summary>
    public string Status { get; set; } = "";
    public string? CurrentNodeId { get; set; }
    public string RootNodeId { get; set; } = "";
    /// <summary>Complete preorder traversal. Sequence nodes and unchosen branches are included.</summary>
    public List<MonitorPlanNode> Nodes { get; set; } = new();
    public double ElapsedSeconds { get; set; }
    public double? EndedAgoSeconds { get; set; }
    public string? EndReason { get; set; }

    /// <summary>none / task / arrival / condition / verification / yield; for inactive plans, the last wait kind.</summary>
    public string Waiting { get; set; } = "none";
    public double WaitingSeconds { get; set; }
    public double? WaitRemainingSeconds { get; set; }
    public double? WaitTimeoutSeconds { get; set; }
    public string? WaitRoom { get; set; }
    public long? AwaitedTaskId { get; set; }
    public bool TaskTimedOut { get; set; }
    public string? CancellationFailure { get; set; }
    public MonitorTaskProgress? TaskProgress { get; set; }
    public string? VerificationCondition { get; set; }
    public string? VerificationNativeResult { get; set; }
    public bool? LastOk { get; set; }
    public string? LastResult { get; set; }
    public string? Failure { get; set; }
    public int ExecutedSteps { get; set; }
    public int ExecutionBudget { get; set; }
}

/// <summary>One row in a tree, identified by stable node and parent IDs rather than parsed display text.</summary>
public sealed class MonitorPlanNode
{
    public string Id { get; set; } = "";
    public string? ParentId { get; set; }
    public int Depth { get; set; }
    public int Index { get; set; }
    /// <summary>root / step / then / else / repeat / try / catch.</summary>
    public string Branch { get; set; } = "";
    /// <summary>sequence / action / if / wait / repeat / try.</summary>
    public string Kind { get; set; } = "";
    /// <summary>pending / running / verifying / completed / skipped / failed / cancelled.</summary>
    public string Status { get; set; } = "";
    public bool IsCurrent { get; set; }
    public bool? LastOk { get; set; }
    public string? Result { get; set; }
    public string? Tool { get; set; }
    /// <summary>The complete arguments object serialized as JSON; never interpreted as monitor commands.</summary>
    public string? ArgumentsJson { get; set; }
    public string? Condition { get; set; }
    /// <summary>if / wait_until / until / while, when a condition exists.</summary>
    public string? ConditionKind { get; set; }
    public string? Ensure { get; set; }
    public double? TimeoutSeconds { get; set; }
    public double? VerifyTimeoutSeconds { get; set; }
    public int? IterationsStarted { get; set; }
    public int? MaxIterations { get; set; }
}

/// <summary>Compact selection row for the active plan and bounded recent plan history.</summary>
public sealed class MonitorPlanSummary
{
    public string PlanId { get; set; } = "";
    public int Revision { get; set; }
    public string Status { get; set; } = "";
    public double ElapsedSeconds { get; set; }
    public double? EndedAgoSeconds { get; set; }
    public string? Reason { get; set; }
    public string Label { get; set; } = "";
}
