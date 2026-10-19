using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using LlmAgents.SettingsUi;

namespace LlmAgents;

/// <summary>
/// Server side of in-game configuration: the settings window talks to us through LuaCs networking
/// (message <see cref="SettingsNet.MessageId"/>, a JSON string each way), and "!llm ..." chat commands
/// are answered with a private server message. Only the server owner and clients with the
/// "Manage settings" permission may change configuration or control plans. The bot monitor is visible to everyone.
/// </summary>
internal sealed class SettingsBridge
{
    readonly Func<SettingsService?> service;
    readonly Func<GameAdapter?> adapter;
    readonly Func<MonitorRequest, Client, MonitorSnapshot?> monitor;
    INetworkingService? net;
    sealed record PendingMonitor(MonitorRequest Request, Client Client, NetworkConnection Connection);
    readonly ConcurrentQueue<PendingMonitor> pendingMonitor = new();
    readonly object monitorQueueSync = new();
    const int MaximumPendingMonitorRequests = 128;
    int pendingMonitorCount;
    volatile bool disposed;

    public SettingsBridge(Func<SettingsService?> service, Func<GameAdapter?> adapter, Func<MonitorRequest, Client, MonitorSnapshot?> monitor)
    {
        this.service = service;
        this.adapter = adapter;
        this.monitor = monitor;
    }

    /// <summary>Registers the network message. Kept separate so a LuaCs API change only disables the window, not the mod.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void Register()
    {
        net = LuaCsSetup.Instance?.NetworkingService;
        if (net == null) throw new InvalidOperationException("LuaCs networking service not available");
        net.Receive(SettingsNet.MessageId, (NetMessageReceived)OnMessage);
        net.Receive(ControlNet.MessageId, (NetMessageReceived)OnControlRequest);
        net.Receive(MonitorNet.MessageId, (NetMessageReceived)OnMonitorRequest);
    }

    readonly System.Collections.Generic.Dictionary<string, DateTime> monitorLogged = new();

    /// <summary>The bot monitor window (about once a second while it is open). Everyone may look: it shows what the
    /// crew does, nothing of the configuration.</summary>
    void OnMonitorRequest(IReadMessage msg, Client client)
    {
        try
        {
            if (disposed || net == null || client?.Connection is not { } connection) return;
            var req = SettingsNet.Deserialize<MonitorRequest>(msg.ReadString()) ?? new MonitorRequest();
            // Only primitives/immutable strings cross the receive boundary. No game/agent state is read here.
            var copy = new MonitorRequest
            {
                Op = req.Op, Bot = req.Bot, Activity = req.Activity, HistoryPlanId = req.HistoryPlanId,
                RequestId = req.RequestId, ExpectedPlanId = req.ExpectedPlanId, ExpectedRevision = req.ExpectedRevision, ClearGoal = req.ClearGoal,
            };
            bool overflow;
            lock (monitorQueueSync)
            {
                if (disposed) return;
                overflow = Interlocked.Increment(ref pendingMonitorCount) > MaximumPendingMonitorRequests;
                if (overflow) Interlocked.Decrement(ref pendingMonitorCount);
                else pendingMonitor.Enqueue(new PendingMonitor(copy, client, connection));
            }
            if (overflow)
            {
                SendMonitor(client, new MonitorSnapshot
                {
                    Version = ModInfo.Version, Message = "Очередь монитора занята. Повторите запрос.",
                    CommandResult = copy.Op == "get" ? null : new MonitorCommandResult
                    { RequestId = copy.RequestId ?? "", Code = "busy", Success = false, Message = "Команда не выполнялась: очередь монитора занята." },
                });
                return;
            }
        }
        catch (Exception e)
        {
            Log.Warn($"monitor request from {client?.Name} failed: {e.Message}");
        }
    }

    /// <summary>Called from Plugin.OnUpdate even in the lobby or while agents are disabled.</summary>
    public void Tick()
    {
        for (int processed = 0; processed < 32 && pendingMonitor.TryDequeue(out var pending); processed++)
        {
            Interlocked.Decrement(ref pendingMonitorCount);
            if (disposed || net == null) continue;
            var client = pending.Client;
            if (!ReferenceEquals(client.Connection, pending.Connection) || GameMain.Server?.ConnectedClients.Contains(client) != true) continue;
            try
            {
                var snap = monitor(pending.Request, client) ?? new MonitorSnapshot
                { Version = ModInfo.Version, Message = "LLM-агенты не запущены: проверьте llm_config.json и консоль сервера." };
                snap.CanControlPlans = CanEdit(client);
                SendMonitor(client, snap);
                var now = DateTime.UtcNow;
                if (!monitorLogged.TryGetValue(client.Name, out var at) || (now - at).TotalSeconds > 300)
                    Log.Info($"bot monitor opened by {client.Name}");
                monitorLogged[client.Name] = now;
            }
            catch (Exception e)
            {
                Log.Warn($"monitor request from {client.Name} failed: {e.Message}");
                SendMonitor(client, new MonitorSnapshot
                {
                    Version = ModInfo.Version, CanControlPlans = CanEdit(client), Message = "Не удалось обновить монитор.",
                    CommandResult = pending.Request.Op == "get" ? null : new MonitorCommandResult
                    { RequestId = pending.Request.RequestId ?? "", Code = "unavailable", Success = false, Message = "Не удалось обработать команду монитора." },
                });
            }
        }
    }

    void SendMonitor(Client client, MonitorSnapshot snapshot)
    {
        if (net == null || disposed) return;
        var response = net.Start(MonitorNet.MessageId);
        response.WriteString(SettingsNet.Serialize(snapshot));
        net.SendToClient(response, client.Connection, DeliveryMethod.Reliable);
    }

    public void Dispose()
    {
        lock (monitorQueueSync)
        {
            disposed = true;
            while (pendingMonitor.TryDequeue(out _)) Interlocked.Decrement(ref pendingMonitorCount);
        }
        net = null;
    }

    /// <summary>The client part asks (every few seconds during a round) which bots are under full AI control, to
    /// mark them in the crew list. Anyone may ask: the crew list is visible to everybody anyway.</summary>
    void OnControlRequest(IReadMessage msg, Client client)
    {
        try
        {
            if (net == null || client == null) return;
            var ids = adapter()?.ControlledIds.ToList() ?? new System.Collections.Generic.List<ushort>();
            var m = net.Start(ControlNet.MessageId);
            m.WriteUInt16((ushort)Math.Min(ids.Count, ControlNet.MaxIds));
            foreach (var id in ids.Take(ControlNet.MaxIds)) m.WriteUInt16(id);
            net.SendToClient(m, client.Connection, DeliveryMethod.Unreliable);
        }
        catch (Exception e)
        {
            Log.Warn($"control status for {client?.Name} failed: {e.Message}");
        }
    }

    public static bool CanEdit(Client? c)
    {
        if (c == null) return false;
        if (GameMain.Server != null && c.Connection == GameMain.Server.OwnerConnection) return true;
        return c.HasPermission(ClientPermissions.ManageSettings);
    }

    readonly System.Collections.Generic.Dictionary<string, DateTime> lastLogged = new();

    void OnMessage(IReadMessage msg, Client client)
    {
        try
        {
            var svc = service();
            if (svc == null) return;
            var request = msg.ReadString();
            var reply = svc.HandleJson(request, CanEdit(client));
            Send(client, reply);
            // the open window refreshes its status several times a second: log the opening, not every refresh
            var now = DateTime.UtcNow;
            if (!lastLogged.TryGetValue(client.Name, out var at) || (now - at).TotalSeconds > 60)
                Log.Info($"settings window: requests from {client.Name} ({(CanEdit(client) ? "allowed" : "denied")})");
            lastLogged[client.Name] = now;
        }
        catch (Exception e)
        {
            Log.Warn($"settings request from {client?.Name} failed: {e.Message}");
        }
    }

    void Send(Client client, string json)
    {
        if (net == null) return;
        var m = net.Start(SettingsNet.MessageId);
        m.WriteString(json);
        net.SendToClient(m, client.Connection, DeliveryMethod.Reliable);
    }

    /// <summary>
    /// A "!llm ..." chat message (game thread). Returns true when it was a command (the message is then not
    /// shown to others and not treated as an order).
    /// </summary>
    public bool TryHandleChat(Client sender, string text)
    {
        if (!SettingsService.IsCommand(text)) return false;
        text = SettingsService.NormalizeCommand(text);
        if (net == null)
        {
            try { Register(); } catch { /* the window stays unavailable; text commands still work */ }
        }
        var svc = service();
        if (svc == null)
        {
            Reply(sender, "LLM-агенты не запущены: проверьте llm_config.json и консоль сервера.");
            return true;
        }
        bool canEdit = CanEdit(sender);
        var args = SettingsService.Tokenize(text.Trim());
        if (args.Count == 2 && args[1].ToLowerInvariant() is "monitor" or "монитор" or "mon")
        {
            // the monitor is for everybody; the client part opens it (Ctrl+Shift+M also works)
            try { Send(sender, SettingsNet.Serialize(new SettingsResponse { Type = "monitor" })); }
            catch (Exception e) { Log.Warn("could not ask the client to open the monitor: " + e.Message); }
            Reply(sender, "Монитор ботов: окно открывается, если у вас стоит клиентская часть мода (LuaCs). Клавиши: Ctrl+Shift+M.");
            return true;
        }
        if (args.Count <= 1 && canEdit)
        {
            // ask the client's settings window (if it has our client plugin) to open; vanilla clients ignore this
            try { Send(sender, SettingsNet.Serialize(new SettingsResponse { Type = "open", CanEdit = true })); }
            catch (Exception e) { Log.Warn("could not ask the client to open the settings window: " + e.Message); }
        }
        var reply = svc.HandleCommand(text, canEdit);
        Reply(sender, reply);
        return true;
    }

    static void Reply(Client client, string text)
    {
        if (GameMain.Server == null) return;
        // long replies in several messages: the chat box cuts very long ones
        foreach (var part in Chunks(text, 900))
            GameMain.Server.SendDirectChatMessage(part, client, ChatMessageType.Server);
    }

    static System.Collections.Generic.IEnumerable<string> Chunks(string text, int max)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (sb.Length > 0 && sb.Length + line.Length + 1 > max)
            {
                yield return sb.ToString();
                sb.Clear();
            }
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line.Length > max ? line.Substring(0, max) : line);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary>Who is aboard and which jobs exist, for the window's crew and job tabs.</summary>
    public static GameInfo GameInfo(GameAdapter? adapter)
    {
        var crew = Character.CharacterList
            .Where(c => !c.Removed && c.IsHuman && c.Info != null && c.IsOnPlayerTeam)
            .Select(c => new CrewEntry(c.Name, c.JobIdentifier.IsEmpty ? "none" : c.JobIdentifier.Value,
                GameAdapter.TraitOf(c), adapter?.IsControlled(c) == true, c.IsBot && !c.IsDead))
            .ToList();
        var jobs = JobPrefab.Prefabs.Where(j => !j.HiddenJob).Select(j => j.Identifier.Value).ToList();
        return new GameInfo(crew, jobs);
    }
}
