using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using LlmAgents.SettingsUi;
using Microsoft.Xna.Framework;

namespace LlmAgents.Client;

internal sealed partial class MonitorWindow
{
    GUILayoutGroup? planRoot;
    GUIListBox? planNodes, planDetails;
    GUITextBlock? planHeading, planMessage;
    GUIButton? olderPlan, newerPlan, activePlan, followPlan, cancelPlan;
    GUITickBox? clearPlanGoal;
    string? historyPlanId;
    string planRenderKey = "", planDetailKey = "";
    string renderedPlanScope = "";
    readonly Dictionary<string, GUIComponent> planCards = new(StringComparer.Ordinal);
    readonly Dictionary<string, PlanViewState> planViews = new(StringComparer.Ordinal);
    MonitorSnapshot? pausedSnapshot;
    double pausedSnapshotAt;
    CancelConfirmation? cancelConfirmation;
    string? pendingPlanCommand;
    int pendingPlanCommandBot;
    double pendingPlanCommandAt;
    string commandMessage = "";
    bool commandFailed;

    sealed class PlanViewState
    {
        public readonly HashSet<string> Collapsed = new(StringComparer.Ordinal);
        public string? Selected;
        public string? LastCurrent;
        public float Scroll;
        public bool Follow = true;
    }

    sealed record CancelConfirmation(int Bot, string PlanId, int Revision, bool ClearGoal);

    MonitorBotDetails? SelectedDetails => snap?.Details is { } details && details.Id == selected ? details : null;
    MonitorPlanSnapshot? DisplayedPlan
    {
        get
        {
            var details = SelectedDetails;
            var plan = details?.Plan;
            if (plan == null || historyPlanId != null && plan.PlanId != historyPlanId) return null;
            if (historyPlanId == null && details!.ActivePlanId != null && plan.PlanId != details.ActivePlanId) return null;
            return plan;
        }
    }

    PlanViewState ViewFor(MonitorPlanSnapshot plan)
    {
        string key = selected + ":" + plan.PlanId;
        if (!planViews.TryGetValue(key, out var state))
        {
            if (planViews.Count >= 32) planViews.Remove(planViews.Keys.First());
            planViews[key] = state = new PlanViewState();
        }
        return state;
    }

    void ResetPlanSelection()
    {
        historyPlanId = null;
        cancelConfirmation = null;
        commandMessage = "";
        commandFailed = false;
        planRenderKey = planDetailKey = "";
    }

    void BuildPlanPanel(GUIComponent host)
    {
        planRoot = new GUILayoutGroup(new RectTransform(Vector2.One, host.RectTransform)) { Stretch = true, RelativeSpacing = 0.008f, Visible = tab == Plan };
        planHeading = new GUITextBlock(new RectTransform(new Vector2(1f, 0.10f), planRoot.RectTransform), "", font: GUIStyle.SmallFont, wrap: true, textColor: Head);
        var toolbar = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.075f), planRoot.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.005f };
        olderPlan = PlanButton(toolbar, "<", 0.08f, () => SelectHistory(1));
        olderPlan.ToolTip = "Предыдущий план из истории";
        newerPlan = PlanButton(toolbar, ">", 0.08f, () => SelectHistory(-1));
        newerPlan.ToolTip = "Более новый план";
        activePlan = PlanButton(toolbar, "Текущий", 0.20f, () => { historyPlanId = null; ResetPlanRender(); nextAsk = 0; Redraw(); });
        followPlan = PlanButton(toolbar, "Следить", 0.20f, () =>
        {
            if (DisplayedPlan is not { } plan) return;
            var view = ViewFor(plan);
            view.Follow = !view.Follow;
            view.LastCurrent = null;
            ResetPlanRender();
            Redraw();
        });
        PlanButton(toolbar, "Свернуть", 0.21f, () =>
        {
            if (DisplayedPlan is not { } plan) return;
            var view = ViewFor(plan);
            view.Follow = false;
            foreach (var node in plan.Nodes.Where(node => PlanMonitorView.HasChildren(plan, node.Id) && node.Id != plan.RootNodeId)) view.Collapsed.Add(node.Id);
            ResetPlanRender(); Redraw();
        });
        PlanButton(toolbar, "Раскрыть", 0.21f, () =>
        {
            if (DisplayedPlan is not { } plan) return;
            ViewFor(plan).Collapsed.Clear(); ResetPlanRender(); Redraw();
        });

        var body = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.66f), planRoot.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.012f };
        planNodes = new GUIListBox(new RectTransform(new Vector2(0.57f, 1f), body.RectTransform)) { Spacing = 3, SmoothScroll = false };
        planDetails = new GUIListBox(new RectTransform(new Vector2(0.42f, 1f), body.RectTransform)) { Spacing = 2 };
        var controls = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.075f), planRoot.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.02f };
        cancelPlan = PlanButton(controls, "Отменить план", 0.42f, CancelDisplayedPlan);
        clearPlanGoal = new GUITickBox(new RectTransform(new Vector2(0.56f, 1f), controls.RectTransform), "Снять также цель")
        {
            Selected = true, ToolTip = "При отмене убрать также текущую цель бота. Завершённые действия не отменяются.",
        };
        clearPlanGoal.OnSelected = _ => { cancelConfirmation = null; RefreshPlanControls(); return true; };
        planMessage = new GUITextBlock(new RectTransform(new Vector2(1f, 0.065f), planRoot.RectTransform), "", font: GUIStyle.SmallFont, wrap: true, textColor: Dim);
        planRenderKey = planDetailKey = "";
        renderedPlanScope = "";
        planCards.Clear();
    }

    static GUIButton PlanButton(GUILayoutGroup parent, string label, float width, Action clicked) =>
        new(new RectTransform(new Vector2(width, 1f), parent.RectTransform), label, style: "GUIButtonSmall")
        { OnClicked = (_, _) => { clicked(); return true; } };

    void ResetPlanRender() { planRenderKey = planDetailKey = ""; cancelConfirmation = null; }

    void SelectHistory(int delta)
    {
        var details = SelectedDetails;
        if (details == null || details.PlanHistory.Count == 0) return;
        int index = details.PlanHistory.FindIndex(plan => plan.PlanId == DisplayedPlan?.PlanId);
        int next = Math.Clamp(index + delta, 0, details.PlanHistory.Count - 1);
        historyPlanId = details.PlanHistory[next].PlanId;
        ResetPlanRender();
        nextAsk = 0;
        Redraw();
    }

    void DrawPlan(bool resetScroll)
    {
        if (planNodes == null || planDetails == null || planHeading == null) return;
        var details = SelectedDetails;
        var plan = DisplayedPlan;
        if (plan == null)
        {
            planHeading.Text = details == null ? "Загрузка сведений о плане..." : historyPlanId != null ? "Загрузка выбранного плана..." : "Плана пока нет";
            if (planRenderKey != "empty:" + selected + ":" + historyPlanId)
            {
                planRenderKey = "empty:" + selected + ":" + historyPlanId;
                planNodes.ClearChildren(); planDetails.ClearChildren(); planCards.Clear();
                AddLine(planNodes, new Line(details?.PlanSelectionMessage ?? (details?.PlanTree != null
                    ? "Сервер передал только текст плана. Для дерева нужны серверная и клиентская части версии 0.19.0 или новее."
                    : "Выберите бота. Дерево появится, когда у него будет план."), Dim));
                planNodes.RecalculateChildren(); planNodes.UpdateScrollBarSize();
            }
            RefreshPlanControls();
            return;
        }
        var view = ViewFor(plan);
        string scope = selected + ":" + plan.PlanId;
        bool followCurrent = view.Follow && plan.CurrentNodeId != null && view.LastCurrent != plan.CurrentNodeId;
        if (followCurrent)
        {
            PlanMonitorView.Reveal(plan, plan.CurrentNodeId!, view.Collapsed);
            view.Selected = plan.CurrentNodeId;
            view.LastCurrent = plan.CurrentNodeId;
        }
        var visible = PlanMonitorView.VisibleNodes(plan, view.Collapsed);
        view.Selected = PlanMonitorView.SelectVisible(plan, visible, view.Selected);
        string activity = plan.Active ? "текущий" : "история";
        var workNodes = plan.Nodes.Where(node => node.Kind is "action" or "wait").ToList();
        planHeading.Text = $"{activity} · {PlanMonitorView.StatusText(plan.Status)} · {ShortId(plan.PlanId)} · ревизия {plan.Revision}\n" +
            $"Длительность {Duration(plan.ElapsedSeconds)} · действий {plan.ExecutedSteps}/{plan.ExecutionBudget}\n" +
            $"Узлы: выполнено {workNodes.Count(node => node.Status == "completed")}, ожидает {workNodes.Count(node => node.Status == "pending")}, пропущено {workNodes.Count(node => node.Status == "skipped")}";
        planHeading.ToolTip = "План: " + plan.PlanId + (plan.EndedAgoSeconds is { } ago ? "\nЗавершён: " + Ago(ago) : "");
        string key = selected + ":" + plan.PlanId + ":" + plan.Revision + ":" + view.Selected + ":" + plan.CurrentNodeId + ":" +
            string.Join("|", visible.Select(node => $"{node.Id}:{node.Status}:{node.IsCurrent}:{node.IterationsStarted}:{view.Collapsed.Contains(node.Id)}"));
        if (key != planRenderKey)
        {
            string? anchorId = null;
            float anchorOffset = 0;
            if (renderedPlanScope == scope)
                foreach (var card in planCards.OrderBy(pair => pair.Value.Rect.Y))
                    if (card.Value.Rect.Bottom > planNodes.Rect.Top)
                    { anchorId = card.Key; anchorOffset = card.Value.Rect.Y - planNodes.Rect.Y; break; }
            float oldScroll = renderedPlanScope == scope && planCards.Count > 0 ? planNodes.BarScroll : view.Scroll;
            renderedPlanScope = scope;
            planRenderKey = key;
            planNodes.ClearChildren(); planCards.Clear();
            foreach (var node in visible) DrawPlanCard(plan, view, node);
            planNodes.RecalculateChildren(); planNodes.UpdateScrollBarSize();
            planNodes.BarScroll = oldScroll;
            if (followCurrent && plan.CurrentNodeId != null && planCards.TryGetValue(plan.CurrentNodeId, out var current)) planNodes.ScrollToElement(current, default);
            else if (anchorId != null && planCards.TryGetValue(anchorId, out var anchor))
            {
                float overflow = Math.Max(0, planCards.Values.Sum(card => card.Rect.Height + planNodes.Spacing) - planNodes.Content.Rect.Height);
                planNodes.BarScroll = PlanMonitorView.RestoreScroll(oldScroll, anchorOffset, anchor.Rect.Y - planNodes.Rect.Y, overflow);
            }
            view.Scroll = planNodes.BarScroll;
        }
        DrawPlanDetails(plan, view);
        RefreshPlanControls();
    }

    void DrawPlanCard(MonitorPlanSnapshot plan, PlanViewState view, MonitorPlanNode node)
    {
        if (planNodes == null) return;
        var row = new GUIFrame(new RectTransform(new Vector2(1f, 0f), planNodes.Content.RectTransform), style: null);
        row.RectTransform.MinSize = new Point(0, GUI.IntScale(66));
        planCards[node.Id] = row;
        float indent = Math.Min(0.28f, Math.Max(0, node.Depth) * 0.035f);
        if (PlanMonitorView.HasChildren(plan, node.Id))
        {
            var arrow = new GUIButton(new RectTransform(new Vector2(0.08f, 0.7f), row.RectTransform, Anchor.CenterLeft), view.Collapsed.Contains(node.Id) ? ">" : "v", style: "GUIButtonSmall");
            arrow.RectTransform.RelativeOffset = new Vector2(indent, 0);
            arrow.OnClicked = (_, _) =>
            {
                if (!view.Collapsed.Add(node.Id)) view.Collapsed.Remove(node.Id);
                view.Follow = false;
                ResetPlanRender(); Redraw(); return true;
            };
        }
        var button = new GUIButton(new RectTransform(new Vector2(0.90f - indent, 0.95f), row.RectTransform, Anchor.CenterRight), "", style: "ListBoxElement")
        {
            Selected = view.Selected == node.Id,
            Color = PlanColor(node.Status) * 0.16f,
            OutlineColor = node.IsCurrent ? Head : PlanColor(node.Status) * 0.7f,
            ToolTip = $"{node.Id}: {PlanMonitorView.Title(node)}\n{PlanMonitorView.StatusText(node.Status)}\n{PlanMonitorView.Summary(node)}",
            OnClicked = (_, _) => { view.Selected = node.Id; view.Follow = false; ResetPlanRender(); Redraw(); return true; },
        };
        var text = new GUILayoutGroup(new RectTransform(new Vector2(0.95f, 0.9f), button.RectTransform, Anchor.Center)) { CanBeFocused = false, Stretch = true };
        new GUITextBlock(new RectTransform(new Vector2(1f, 0.35f), text.RectTransform),
            (node.IsCurrent ? "> " : "") + node.Id + " · " + PlanMonitorView.Title(node), font: GUIStyle.SmallFont, textColor: node.IsCurrent ? Head : Text)
        { CanBeFocused = false, ToolTip = PlanMonitorView.Title(node) };
        new GUITextBlock(new RectTransform(new Vector2(1f, 0.27f), text.RectTransform), PlanMonitorView.StatusText(node.Status), font: GUIStyle.SmallFont, textColor: PlanColor(node.Status)) { CanBeFocused = false };
        new GUITextBlock(new RectTransform(new Vector2(1f, 0.38f), text.RectTransform), Clip(PlanMonitorView.Summary(node), 85), font: GUIStyle.SmallFont, textColor: Dim, wrap: true) { CanBeFocused = false };
    }

    void DrawPlanDetails(MonitorPlanSnapshot plan, PlanViewState view)
    {
        if (planDetails == null) return;
        var node = plan.Nodes.FirstOrDefault(item => item.Id == view.Selected);
        var lines = new List<Line>();
        lines.Add(new Line("Выбранный шаг", Head, true));
        if (node != null)
        {
            lines.Add(new Line(node.Id + " · " + PlanMonitorView.Title(node), Text));
            lines.Add(new Line(PlanMonitorView.StatusText(node.Status), PlanColor(node.Status)));
            if (node.Tool != null) lines.Add(new Line("Инструмент: " + node.Tool, Head));
            if (node.ArgumentsJson != null) { Section(lines, "Аргументы"); lines.Add(new Line(node.ArgumentsJson, Text)); }
            if (node.Condition != null) { Section(lines, "Условие · " + node.ConditionKind); lines.Add(new Line(node.Condition, Text)); }
            if (node.Ensure != null) { Section(lines, "Проверка результата"); lines.Add(new Line(node.Ensure, Head)); }
            if (node.TimeoutSeconds is { } timeout) lines.Add(new Line($"Таймаут действия: {Duration(timeout)}", Dim));
            if (node.VerifyTimeoutSeconds is { } verifyTimeout && node.Ensure != null) lines.Add(new Line($"Таймаут проверки: {Duration(verifyTimeout)}", Dim));
            if (node.MaxIterations is { } maximum) lines.Add(new Line($"Начато повторов: {node.IterationsStarted ?? 0}/{maximum}", Text));
            if (node.Result != null) { Section(lines, "Результат этого шага"); lines.Add(new Line(node.Result, node.LastOk == false ? Bad : Good)); }
        }
        Section(lines, "Сейчас в плане");
        lines.Add(new Line(WaitLabel(plan.Waiting), plan.TaskTimedOut ? Bad : Head));
        if (plan.WaitRemainingSeconds is { } remaining) lines.Add(new Line("До таймаута: " + Duration(remaining), Dim));
        if (plan.WaitRoom != null) lines.Add(new Line("Отсек: " + plan.WaitRoom, Text));
        if (plan.VerificationCondition != null) lines.Add(new Line("Подтвердить: " + plan.VerificationCondition, Head));
        if (plan.VerificationNativeResult != null) lines.Add(new Line("Ответ действия: " + plan.VerificationNativeResult, Dim));
        TaskProgressLines(lines, plan.TaskProgress);
        if (plan.CancellationFailure != null) lines.Add(new Line("Не удалось освободить задачу: " + plan.CancellationFailure, Bad));
        if (plan.LastResult != null) { Section(lines, "Последний результат"); lines.Add(new Line(plan.LastResult, plan.LastOk == false ? Warn : Text)); }
        if (plan.Failure != null) lines.Add(new Line("Ошибка: " + plan.Failure, Bad));
        if (plan.EndReason != null) lines.Add(new Line("Завершение: " + plan.EndReason, Dim));
        if (plan.EndedAgoSeconds is { } endedAgo) lines.Add(new Line("Завершён: " + Ago(endedAgo), Dim));
        Section(lines, "Обозначения");
        foreach (var status in new[] { "running", "verifying", "completed", "failed", "skipped", "pending", "cancelled" })
            lines.Add(new Line(PlanMonitorView.StatusText(status), PlanColor(status)));
        lines.Add(new Line("Счётчики узлов относятся к текущему проходу дерева; это не процент выполнения цели.", Dim));
        string key = plan.PlanId + ":" + view.Selected + ":" + string.Join("\n", lines.Select(line => line.Text));
        if (key == planDetailKey) return;
        float scroll = planDetailKey.StartsWith(plan.PlanId + ":" + view.Selected + ":", StringComparison.Ordinal) ? planDetails.BarScroll : 0;
        planDetailKey = key;
        planDetails.ClearChildren(); foreach (var line in lines) AddLine(planDetails, line);
        planDetails.RecalculateChildren(); planDetails.UpdateScrollBarSize(); planDetails.BarScroll = scroll;
    }

    void RefreshPlanControls()
    {
        if (planRoot == null) return;
        var details = SelectedDetails;
        var plan = DisplayedPlan;
        if (plan != null && planNodes != null && renderedPlanScope == selected + ":" + plan.PlanId) ViewFor(plan).Scroll = planNodes.BarScroll;
        bool stale = GameMain.Client == null || Timing.TotalTime - lastAnswer > 6;
        bool canCancel = PlanMonitorView.CanCancel(snap?.CanControlPlans == true, paused, stale, details?.ActivePlanId, plan) && pendingPlanCommand == null;
        if (cancelConfirmation is { } confirmation && (plan == null || confirmation.Bot != selected || confirmation.PlanId != plan.PlanId || confirmation.Revision != plan.Revision || !canCancel)) cancelConfirmation = null;
        if (cancelPlan != null)
        {
            cancelPlan.Enabled = canCancel;
            cancelPlan.Text = cancelConfirmation != null ? "Подтвердить отмену" : "Отменить план";
        }
        if (clearPlanGoal != null) clearPlanGoal.Enabled = canCancel;
        int index = details?.PlanHistory.FindIndex(item => item.PlanId == plan?.PlanId) ?? -1;
        if (olderPlan != null) olderPlan.Enabled = !paused && details != null && index >= 0 && index + 1 < details.PlanHistory.Count;
        if (newerPlan != null) newerPlan.Enabled = !paused && details != null && index > 0;
        if (activePlan != null) activePlan.Enabled = !paused && details?.ActivePlanId != null && historyPlanId != null;
        if (followPlan != null) { followPlan.Enabled = plan?.CurrentNodeId != null; followPlan.Selected = plan != null && ViewFor(plan).Follow; }
        if (pendingPlanCommand != null && Timing.TotalTime - pendingPlanCommandAt > 12)
        { pendingPlanCommand = null; commandMessage = "Ответ на команду не получен. Результат неизвестен; команда автоматически не повторяется."; commandFailed = true; }
        if (planMessage != null)
        {
            planMessage.Text = pendingPlanCommand != null ? "Команда отправлена. Ожидается подтверждение сервера..." : cancelConfirmation != null
                ? "Повторное нажатие отменит оставшиеся шаги" + (cancelConfirmation.ClearGoal ? " и снимет цель." : "; цель останется.")
                : commandMessage.Length > 0 ? commandMessage : paused ? "Снимок заморожен. Управление планом недоступно." : stale ? "Данные устарели. Управление планом недоступно."
                : snap?.CanControlPlans != true ? "Просмотр: нет разрешения управлять планами." : plan?.Active == false ? "История: завершённый план изменить нельзя." : "Выберите шаг для подробностей. Отмена требует подтверждения.";
            planMessage.TextColor = commandFailed || paused || stale || cancelConfirmation != null ? Warn : Dim;
        }
    }

    void CancelDisplayedPlan()
    {
        var details = SelectedDetails;
        var plan = DisplayedPlan;
        if (!PlanMonitorView.CanCancel(snap?.CanControlPlans == true, paused, GameMain.Client == null || Timing.TotalTime - lastAnswer > 6, details?.ActivePlanId, plan) || pendingPlanCommand != null) return;
        var intended = new CancelConfirmation(selected, plan!.PlanId, plan.Revision, clearPlanGoal?.Selected == true);
        if (cancelConfirmation != intended) { cancelConfirmation = intended; RefreshPlanControls(); return; }
        cancelConfirmation = null;
        pendingPlanCommand = Guid.NewGuid().ToString("N");
        pendingPlanCommandBot = intended.Bot;
        pendingPlanCommandAt = Timing.TotalTime;
        commandMessage = ""; commandFailed = false;
        send(new MonitorRequest { Op = "cancel_plan", Bot = intended.Bot, RequestId = pendingPlanCommand, ExpectedPlanId = intended.PlanId,
            ExpectedRevision = intended.Revision, ClearGoal = intended.ClearGoal, HistoryPlanId = historyPlanId });
        RefreshPlanControls();
    }

    void ReceivePlanCommand(MonitorCommandResult? result)
    {
        if (result == null || pendingPlanCommand == null || result.RequestId != pendingPlanCommand) return;
        pendingPlanCommand = null;
        commandMessage = (pendingPlanCommandBot != selected ? $"Бот #{pendingPlanCommandBot}: " : "") + result.Message;
        commandFailed = !result.Success;
        cancelConfirmation = null;
    }

    static Color PlanColor(string status) => status switch { "running" => Head, "verifying" => Warn, "completed" => Good, "failed" => Bad, "cancelled" => Warn, _ => Dim };
    static string ShortId(string id) => id.Length > 8 ? id.Substring(0, 8) : id;
    static string WaitLabel(string waiting) => waiting switch
    {
        "task" => "Ожидается завершение задачи", "arrival" => "Перемещение к отсеку", "condition" => "Ожидается условие",
        "verification" => "Проверяется фактический результат", "yield" => "Продолжение в следующем тике", _ => "Ожидания нет",
    };
}
