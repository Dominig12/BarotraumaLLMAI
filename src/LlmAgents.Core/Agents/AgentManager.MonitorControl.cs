using System;
using System.Collections.Generic;
using System.Linq;
using LlmAgents.SettingsUi;

namespace LlmAgents.Agents;

public sealed partial class AgentManager
{
    const int MaximumMonitorReceipts = 128;
    const int MaximumMonitorReceiptsPerRequester = 32;
    const double MonitorReceiptLifetimeSeconds = 300;
    sealed record MonitorCommandPayload(string? Op, int Bot, string? PlanId, int? Revision, bool ClearGoal);
    sealed record MonitorReceipt(object Requester, string RequestId, MonitorCommandPayload Payload, double At, MonitorCommandResult Result);
    readonly List<MonitorReceipt> monitorReceipts = new();

    /// <summary>Game-thread command entry point. The server supplies the real connection identity and current permission.</summary>
    public MonitorCommandResult HandleMonitorCommand(MonitorRequest request, object requesterIdentity, string actor, bool authorized)
    {
        string requestId = request?.RequestId ?? "";
        MonitorCommandResult Reply(bool success, string code, string message) => new()
        { RequestId = requestId, Success = success, Code = code, Message = message };
        // Permission must be rechecked even for a replay of an earlier successful command.
        if (!authorized) return Reply(false, "forbidden", "Нужны права владельца сервера или «Управление настройками».");
        if (request == null || requesterIdentity == null || requestId.Length is < 1 or > 80
            || requestId.Any(character => !(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_')))
            return Reply(false, "invalid_request", "Нужен корректный уникальный идентификатор команды.");

        double now = clock();
        monitorReceipts.RemoveAll(receipt => now - receipt.At >= MonitorReceiptLifetimeSeconds);
        var payload = new MonitorCommandPayload(request.Op, request.Bot, request.ExpectedPlanId, request.ExpectedRevision, request.ClearGoal);
        var cached = monitorReceipts.FirstOrDefault(receipt => ReferenceEquals(receipt.Requester, requesterIdentity) && receipt.RequestId == requestId);
        if (cached != null)
            return cached.Payload == payload ? CopyMonitorResult(cached.Result)
                : Reply(false, "request_conflict", "Идентификатор команды уже использован с другими параметрами.");

        MonitorCommandResult Remember(MonitorCommandResult result)
        {
            while (monitorReceipts.Count(receipt => ReferenceEquals(receipt.Requester, requesterIdentity)) >= MaximumMonitorReceiptsPerRequester)
            {
                int oldest = monitorReceipts.FindIndex(receipt => ReferenceEquals(receipt.Requester, requesterIdentity));
                monitorReceipts.RemoveAt(oldest);
            }
            while (monitorReceipts.Count >= MaximumMonitorReceipts) monitorReceipts.RemoveAt(0);
            monitorReceipts.Add(new MonitorReceipt(requesterIdentity, requestId, payload, now, CopyMonitorResult(result)));
            return result;
        }

        if (request.Op != "cancel_plan") return Remember(Reply(false, "invalid_request", "Неизвестная команда монитора."));
        if (request.Bot is <= 0 or > ushort.MaxValue || string.IsNullOrWhiteSpace(request.ExpectedPlanId)
            || request.ExpectedPlanId.Length > 64 || request.ExpectedRevision is not >= 1)
            return Remember(Reply(false, "invalid_request", "Для отмены нужны точные бот, идентификатор плана и его ревизия."));
        if (!agents.TryGetValue(request.Bot, out var agent))
            return Remember(Reply(false, "bot_not_found", "Бот больше не управляется LLM. Обновите монитор."));
        if (agent.Plan is not { } plan)
            return Remember(Reply(false, "no_active_plan", "План уже завершён или отменён. Обновите монитор."));
        if (!string.Equals(plan.Id, request.ExpectedPlanId, StringComparison.Ordinal))
            return Remember(Reply(false, "stale_plan", "У бота уже другой план. Обновите монитор перед отменой."));
        if (plan.Tree.Revision != request.ExpectedRevision)
            return Remember(Reply(false, "stale_revision", "План был изменён. Обновите монитор перед отменой."));

        string actorName = Tools.Clip((actor ?? "администратор").Replace('\r', ' ').Replace('\n', ' '), 80);
        string result;
        try { result = CancelPlan(agent, $"отменён через монитор пользователем {actorName}", request.ClearGoal,
            request.ExpectedRevision, request.ExpectedPlanId); }
        catch (Exception e) { return Remember(Reply(false, "cancel_failed", "Не удалось освободить текущую работу: " + Tools.Clip(e.Message, 160))); }
        if (IsError(result)) return Remember(Reply(false, "cancel_failed", Tools.Clip(result, 250)));

        // A pending decision was based on the cancelled tree. Its eventual answer must not recreate that work.
        agent.RequestSeq++;
        agent.Pending = false;
        agent.Events.RemoveAll(e => e.Kind is EventKind.PlanReview or EventKind.Retry or EventKind.TaskDone or EventKind.TaskFailed);
        agent.LastObservation = null;
        var response = Remember(Reply(true, "cancelled", request.ClearGoal ? "План отменён, цель очищена." : "План отменён; цель сохранена."));
        string line = $"{actorName} отменил план {plan.Id} (ревизия {plan.Tree.Revision}) через монитор";
        agent.AddPastOrder(line, now);
        agent.RecordResult(line + "; не возобновляйте отменённые шаги без нового решения");
        agent.LogEvent(line);
        agent.AddActivity("order", line, now);
        Log.Info($"[{agent.Name}] {line}");
        return response;
    }

    static MonitorCommandResult CopyMonitorResult(MonitorCommandResult result) => new()
    { RequestId = result.RequestId, Success = result.Success, Code = result.Code, Message = result.Message };
}
