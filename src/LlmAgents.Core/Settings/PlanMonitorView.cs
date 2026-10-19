using System;
using System.Collections.Generic;
using System.Linq;

namespace LlmAgents.SettingsUi;

/// <summary>Pure tree presentation rules shared with the native client. Display text is never parsed as a plan.</summary>
public static class PlanMonitorView
{
    public static IReadOnlyList<MonitorPlanNode> VisibleNodes(MonitorPlanSnapshot plan, ISet<string> collapsed)
    {
        var byId = plan.Nodes.GroupBy(node => node.Id).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        return plan.Nodes.Where(node => !Ancestors(node, byId).Any(parent => collapsed.Contains(parent.Id))).ToList();
    }

    static IEnumerable<MonitorPlanNode> Ancestors(MonitorPlanNode node, IReadOnlyDictionary<string, MonitorPlanNode> byId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { node.Id };
        while (node.ParentId != null && byId.TryGetValue(node.ParentId, out var parent) && seen.Add(parent.Id))
        {
            yield return parent;
            node = parent;
        }
    }

    public static bool HasChildren(MonitorPlanSnapshot plan, string nodeId) => plan.Nodes.Any(node => node.ParentId == nodeId);

    public static void Reveal(MonitorPlanSnapshot plan, string nodeId, ISet<string> collapsed)
    {
        var byId = plan.Nodes.GroupBy(node => node.Id).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        if (byId.TryGetValue(nodeId, out var node)) foreach (var parent in Ancestors(node, byId)) collapsed.Remove(parent.Id);
    }

    public static string? SelectVisible(MonitorPlanSnapshot plan, IReadOnlyList<MonitorPlanNode> visible, string? selected)
    {
        if (selected != null && visible.Any(node => node.Id == selected)) return selected;
        var byId = plan.Nodes.GroupBy(node => node.Id).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        if (selected != null && byId.TryGetValue(selected, out var hidden))
        {
            var ancestor = Ancestors(hidden, byId).FirstOrDefault(parent => visible.Any(node => node.Id == parent.Id));
            if (ancestor != null) return ancestor.Id;
        }
        return visible.FirstOrDefault(node => node.Id == plan.CurrentNodeId)?.Id ?? visible.FirstOrDefault()?.Id;
    }

    public static bool CanCancel(bool permitted, bool paused, bool stale, string? activePlanId, MonitorPlanSnapshot? plan) =>
        permitted && !paused && !stale && plan is { Active: true, Revision: > 0 } && plan.PlanId == activePlanId && plan.Status == "running";

    public static float RestoreScroll(float original, float oldAnchorOffset, float newAnchorOffset, float overflow) =>
        overflow > 0 ? Math.Clamp(original + (newAnchorOffset - oldAnchorOffset) / overflow, 0, 1) : 0;

    public static string StatusText(string status) => status switch
    {
        "pending" => "ожидает", "running" => "выполняется", "verifying" => "проверка результата", "completed" => "готово",
        "skipped" => "пропущен", "failed" => "ошибка", "cancelled" => "отменён", "replaced" => "заменён", _ => status,
    };

    public static string Title(MonitorPlanNode node) => node.Kind switch
    {
        "action" => node.Tool ?? "Действие", "if" => "Условие", "wait" => "Ожидание условия", "repeat" => "Цикл",
        "try" => "Попытка с обработкой ошибки",
        "sequence" => node.Branch switch { "then" => "Если да", "else" => "Иначе", "try" => "Основные действия", "catch" => "При ошибке", "repeat" => "Тело цикла", "root" => "Последовательность", _ => "Действия" },
        _ => node.Kind,
    };

    public static string Summary(MonitorPlanNode node)
    {
        if (node.Kind == "repeat") return (node.ConditionKind == "while" ? "Пока " : node.ConditionKind == "until" ? "До " : "") +
            (node.Condition ?? "ограниченного числа повторов") + $" · {node.IterationsStarted ?? 0}/{node.MaxIterations ?? 0}";
        if (node.Condition != null) return node.Condition;
        if (node.Ensure != null) return "Проверить: " + node.Ensure;
        if (node.ArgumentsJson != null) return node.ArgumentsJson;
        return node.Kind == "sequence" ? "Вложенные шаги" : StatusText(node.Status);
    }
}
