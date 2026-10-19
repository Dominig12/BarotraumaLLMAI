using System;
using System.Linq;
using LlmAgents.SettingsUi;

namespace LlmAgents.Agents;

public sealed partial class AgentManager
{
    void ArchivePlan(BotAgent agent, ActivePlan plan, string status, string? reason, double now)
    {
        // Routine speech/lookups must not displace the jobs the player wants to diagnose.
        if (plan.Tree.Root.Items.Count == 0 || !plan.HadWait && plan.Tree.Root.Items.All(node => node is PlanTree.Do action
            && ResolveTool(agent, action.Call.Name)?.Kind is ToolKind.Talk or ToolKind.Lookup or ToolKind.Memory)) return;
        try
        {
            var snapshot = PlanMonitorProjection.BuildEnded(plan, now, now, status, reason);
            agent.ArchivePlan(plan.Id, SettingsNet.Serialize(snapshot), now);
        }
        catch (Exception e) { Log.ErrorThrottled("plan-history:" + agent.Id, "could not retain final plan snapshot: " + e.Message); }
    }

    void FillPlanMonitor(BotAgent agent, MonitorBotDetails details, string? requestedPlanId, double now, GameTaskProgress? progress)
    {
        MonitorPlanSnapshot? active = agent.Plan is { } plan ? PlanMonitorProjection.Build(plan, now, progress) : null;
        details.ActivePlanId = active?.PlanId;
        if (active != null) details.PlanHistory.Add(PlanMonitorProjection.Summarize(active));
        foreach (var entry in agent.PlanHistory())
        {
            var snapshot = SettingsNet.Deserialize<MonitorPlanSnapshot>(entry.SnapshotJson);
            if (snapshot == null) continue;
            snapshot.EndedAgoSeconds = Math.Max(0, now - entry.EndedAt);
            details.PlanHistory.Add(PlanMonitorProjection.Summarize(snapshot));
            if (requestedPlanId == snapshot.PlanId || requestedPlanId == null && active == null && details.Plan == null)
                details.Plan = snapshot;
        }
        if (requestedPlanId == null || requestedPlanId == active?.PlanId) details.Plan = active ?? details.Plan;
        else if (details.Plan == null)
            details.PlanSelectionMessage = "Этот план уже недоступен в истории раунда. Выберите другой план.";
    }
}
