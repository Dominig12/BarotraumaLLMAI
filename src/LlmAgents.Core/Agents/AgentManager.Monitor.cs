using System;
using System.Collections.Generic;
using System.Linq;
using LlmAgents.SettingsUi;

namespace LlmAgents.Agents;

/// <summary>
/// The in-game monitor: what every bot is doing, its goal and plan, and (for the bot looked at) what it did lately,
/// its notes, gear and counters. Built on the game thread about once a second while someone has the window open.
/// </summary>
public sealed partial class AgentManager
{
    public MonitorSnapshot Monitor(int detailsFor, IReadOnlyList<FeatureStatus>? features = null, bool activity = true, string? historyPlanId = null)
    {
        double now = clock();
        var s = new MonitorSnapshot
        {
            Version = ModInfo.Version,
            Enabled = Settings.Enabled,
            RoundSeconds = double.IsNaN(roundStartedAt) ? 0 : now - roundStartedAt,
            Autonomy = ModStatus.AutonomyText(Settings.Bots.Autonomy),
            RequestsLastMinute = RequestsLastMinute,
            RequestsToday = router.GlobalGate.UsedToday,
            DailyBudget = Settings.Global.DailyRequestBudget,
            TokensIn = tokensIn,
            TokensOut = tokensOut,
            DispatchRequests = dispatchRequests,
            PendingChats = pendingChats.Count,
            DirectorEnabled = Settings.Director.Enabled,
            DirectorActive = DirectorActive,
            DirectorThinking = directorPending,
            DirectorDecisions = directorDecisions,
            DirectorLast = directorLastSummary,
            DirectorLastAgo = double.IsNaN(directorLastAt) ? null : now - directorLastAt,
            DirectorNotes = directorNotes.ToList(),
        };
        foreach (var p in router.Status())
            s.Providers.Add(new MonitorProvider
            {
                Name = p.Name, Model = p.Model, Ok = p.Ok, Errors = p.Errors, AvgSeconds = p.AvgSeconds, Active = p.Active,
                UsedToday = p.UsedToday, PausedFor = p.PausedForSeconds, LastError = p.LastError,
            });
        var all = (features ?? Array.Empty<FeatureStatus>()).ToList();
        if (DirectorFeature() is { } df) all.Add(df);
        s.Problems = all.Where(f => !f.Ok).Select(f => f.Name + (f.Note != null ? $": {f.Note}" : "")).ToList();
        s.Conversation = conversation.Recent(null, now, 15 * 60, 20).Select(l => ConversationLog.Format(l, now)).ToList();

        foreach (var a in agents.Values.OrderBy(a => a.Name))
        {
            BotBrief? brief = null;
            GameTaskProgress? taskProgress = null;
            bool alive = false;
            try
            {
                alive = game.IsAlive(a.Id);
                if (alive) brief = game.Brief(a.Id);
            }
            catch { /* the monitor must never fail */ }
            if (alive)
                try { taskProgress = game.TaskProgress(a.Id); }
                catch { /* Diagnostics are optional; a broken backend must not hide the rest of the monitor. */ }
            var obs = a.LastObservation;
            s.Bots.Add(new MonitorBot
            {
                Id = a.Id, Name = a.Name, Job = a.Job, Alive = alive, AiControl = a.AiControl, Directed = alive && IsDirected(a),
                State = StateOf(a, alive), Room = brief?.Room, Health = obs?.HealthPercent ?? -1,
                Goal = a.CurrentGoal?.Text, Doing = brief?.Doing, Task = brief?.CurrentTask, Order = brief?.CurrentOrder,
                TaskProgress = MonitorProgress(taskProgress),
                Decisions = a.Decisions, Errors = a.Stats.ToolErrors + a.Stats.FailedRequests + a.Stats.TasksFailed,
            });
            if (a.Id == detailsFor)
            {
                s.Details = Details(a, now, activity, taskProgress);
                FillPlanMonitor(a, s.Details, historyPlanId, now, taskProgress);
            }
        }
        return s;
    }

    static string StateOf(BotAgent a, bool alive)
    {
        if (!alive) return "dead";
        if (a.Pending) return "thinking";
        switch (a.Plan?.Waiting)
        {
            case PlanWait.Task: return "task";
            case PlanWait.Arrival: return "walking";
            case PlanWait.Condition: return "waiting";
            case PlanWait.Verification: return "verifying";
            case not null: return "plan";
        }
        if (a.FailedRequests > 0) return "retry";
        if (a.Events.Count > 0 || a.HasPlayerOrder || a.HasMail) return "queued";
        return "idle";
    }

    static MonitorTaskProgress? MonitorProgress(GameTaskProgress? progress) => progress == null ? null : new MonitorTaskProgress
    {
        TaskId = progress.TaskId, Phase = progress.Phase, Step = progress.Step, ElapsedSeconds = progress.ElapsedSeconds,
        NoProgressSeconds = progress.NoProgressSeconds, BlockerCode = progress.BlockerCode, Blocker = progress.Blocker,
        CompletedUnits = progress.CompletedUnits, TotalUnits = progress.TotalUnits,
    };

    MonitorBotDetails Details(BotAgent a, double now, bool withActivity, GameTaskProgress? taskProgress)
    {
        var st = a.Stats;
        var d = new MonitorBotDetails
        {
            Id = a.Id,
            Provider = ProviderFor(a),
            Persona = personas.PromptFor(a.Name) != null,
            ControlBy = a.AiControl ? a.ControlBy ?? "?" : null,
            GoalSource = a.CurrentGoal is { } g ? GoalSource(g) : null,
            GoalAgo = a.CurrentGoal is { } g2 ? now - g2.SetAt : null,
            PlanNow = a.Plan?.Waiting switch
            {
                PlanWait.Task => "waiting for the task" + (a.TaskTool != null ? $" ({a.TaskTool})" : ""),
                PlanWait.Arrival => "walking to " + a.Plan.WaitRoom,
                PlanWait.Condition => "waiting for: " + a.Plan.Tree.CurrentWait?.Cond.Text,
                PlanWait.Verification => "verifying actual result: " + a.Plan.Tree.CurrentDo?.Ensure?.Text,
                _ => null,
            },
            PlanTree = a.Plan?.Tree.Describe(1500),
            PlanAgo = a.Plan != null ? now - a.Plan.StartedAt : null,
            TaskProgress = MonitorProgress(taskProgress),
            VerificationCondition = a.Plan?.Waiting == PlanWait.Verification ? a.Plan.Tree.CurrentDo?.Ensure?.Text : null,
            VerificationNativeResult = a.Plan?.Waiting == PlanWait.Verification ? a.Plan.VerificationResult : null,
            VerificationRemainingSeconds = a.Plan?.Waiting == PlanWait.Verification ? Math.Max(0, a.Plan.WaitDeadline - now) : null,
            LastWhy = a.LastWhy,
            LastDecision = a.LastDecision,
            LastDecisionAgo = double.IsNaN(a.LastDecisionAt) ? null : now - a.LastDecisionAt,
            NotAssignable = Settings.Director.Enabled ? NotAssignable(a) : null,
            PendingEvents = a.Events.Select(e => $"{EventPolicy.Label(e.Kind)}: {e.Text}").ToList(),
            PastOrders = a.PastOrders.Select(o => $"({ModStatus.Ago(now - o.At)}) {o.Text}").ToList(),
            Notes = a.Notes(now),
            Errors = a.RecentErrors(),
            Activity = !withActivity ? new List<MonitorActivity>() : a.Activity().AsEnumerable().Reverse()
                .Select(x => new MonitorActivity { Ago = double.IsNaN(x.At) ? -1 : now - x.At, Kind = x.Kind, Text = x.Text }).ToList(),

            Requests = st.Requests, FailedRequests = st.FailedRequests, TokensIn = st.TokensIn, TokensOut = st.TokensOut,
            ReasoningTokens = st.ReasoningTokens, AvgLatency = st.AvgLatency, ToolCalls = st.ToolCalls, ToolErrors = st.ToolErrors,
            TasksDone = st.TasksDone, TasksFailed = st.TasksFailed, OrdersReceived = st.OrdersReceived,
            DirectorAssignments = st.DirectorAssignments,
            TopTools = st.PerTool.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(12).Select(kv => $"{kv.Key} {kv.Value}").ToList(),
        };
        if (a.LastObservation is { } o)
        {
            d.Oxygen = o.OxygenPercent;
            d.Injuries = o.Injuries;
            d.Gear = o.Gear;
            d.Equipped = o.Equipped.ToList();
            d.Inventory = o.Inventory.ToList();
            d.SeenAgo = double.IsNegativeInfinity(a.LastObservationAt) ? null : now - a.LastObservationAt;
        }
        return d;
    }
}
