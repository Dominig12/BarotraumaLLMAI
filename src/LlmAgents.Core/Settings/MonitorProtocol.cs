using System.Collections.Generic;

// Shared by the server plugin and the client monitor window (the client project compiles this file too),
// so it must not depend on anything else in Core.
namespace LlmAgents.SettingsUi;

/// <summary>The in-game bot monitor: the client sends a <see cref="MonitorRequest"/> (JSON string) about once a second
/// while the window is open, the server answers with a <see cref="MonitorSnapshot"/>.</summary>
public static class MonitorNet
{
    public const string MessageId = "llmagents.monitor";
}

public sealed class MonitorRequest
{
    /// <summary>The bot whose details (activity, notes, inventory, statistics) are wanted; 0 = only the overview.</summary>
    public int Bot { get; set; }
    /// <summary>Also the bot's recent actions (the longest part: only while that tab is open).</summary>
    public bool Activity { get; set; }
}

public sealed class MonitorSnapshot
{
    public string Version { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Allowed { get; set; } = true;
    public string? Message { get; set; }
    public double RoundSeconds { get; set; }
    public string Autonomy { get; set; } = "";

    public int RequestsLastMinute { get; set; }
    public int RequestsToday { get; set; }
    public int DailyBudget { get; set; }
    public long TokensIn { get; set; }
    public long TokensOut { get; set; }
    public int DispatchRequests { get; set; }
    public int PendingChats { get; set; }
    public List<MonitorProvider> Providers { get; set; } = new();
    public List<string> Problems { get; set; } = new();

    public bool DirectorEnabled { get; set; }
    public bool DirectorActive { get; set; }
    public bool DirectorThinking { get; set; }
    public int DirectorDecisions { get; set; }
    public string? DirectorLast { get; set; }
    public double? DirectorLastAgo { get; set; }
    public List<string> DirectorNotes { get; set; } = new();

    /// <summary>Recent conversation aboard, newest last.</summary>
    public List<string> Conversation { get; set; } = new();
    public List<MonitorBot> Bots { get; set; } = new();
    /// <summary>Details of the requested bot (null if none was asked for or it is gone).</summary>
    public MonitorBotDetails? Details { get; set; }
}

public sealed class MonitorProvider
{
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public int Ok { get; set; }
    public int Errors { get; set; }
    public double AvgSeconds { get; set; }
    public int Active { get; set; }
    public int UsedToday { get; set; }
    public double PausedFor { get; set; }
    public string? LastError { get; set; }
}

/// <summary>One line of the bot list (for every bot, every refresh).</summary>
public sealed class MonitorBot
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Job { get; set; } = "";
    public bool Alive { get; set; }
    public bool AiControl { get; set; }
    public bool Directed { get; set; }
    /// <summary>thinking / plan / task / walking / waiting / retry / idle / dead</summary>
    public string State { get; set; } = "";
    public string? Room { get; set; }
    public int Health { get; set; } = -1;
    public string? Goal { get; set; }
    public string? Doing { get; set; }
    public string? Task { get; set; }
    public string? Order { get; set; }
    public int Decisions { get; set; }
    public int Errors { get; set; }
}

public sealed class MonitorBotDetails
{
    public int Id { get; set; }
    public string Provider { get; set; } = "";
    public bool Persona { get; set; }
    public string? ControlBy { get; set; }
    public string? GoalSource { get; set; }
    public double? GoalAgo { get; set; }
    /// <summary>What the plan waits for right now ("walking to Engine [h3]"...).</summary>
    public string? PlanNow { get; set; }
    public List<string> PlanSteps { get; set; } = new();
    public string? PlanTree { get; set; }
    public double? PlanAgo { get; set; }
    public string? LastWhy { get; set; }
    public string? LastDecision { get; set; }
    public double? LastDecisionAgo { get; set; }
    public string? NotAssignable { get; set; }
    public List<string> PendingEvents { get; set; } = new();
    public List<string> PastOrders { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public List<MonitorActivity> Activity { get; set; } = new();

    public int Oxygen { get; set; } = -1;
    public string? Injuries { get; set; }
    public string? Gear { get; set; }
    public List<string> Equipped { get; set; } = new();
    public List<string> Inventory { get; set; } = new();
    public double? SeenAgo { get; set; }

    public int Requests { get; set; }
    public int FailedRequests { get; set; }
    public int TokensIn { get; set; }
    public int TokensOut { get; set; }
    public int ReasoningTokens { get; set; }
    public double AvgLatency { get; set; }
    public int ToolCalls { get; set; }
    public int ToolErrors { get; set; }
    public int TasksDone { get; set; }
    public int TasksFailed { get; set; }
    public int OrdersReceived { get; set; }
    public int DirectorAssignments { get; set; }
    /// <summary>"go_to_room 12", most used first.</summary>
    public List<string> TopTools { get; set; } = new();
}

public sealed class MonitorActivity
{
    public double Ago { get; set; }
    /// <summary>think / do / task / task-failed / order / director / error</summary>
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
}
