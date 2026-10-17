using System;
using System.Linq;
using Barotrauma;
using Barotrauma.LuaCs;
using Barotrauma.Networking;
using LlmAgents.SettingsUi;

namespace LlmAgents;

/// <summary>
/// Server side of in-game configuration: the settings window talks to us through LuaCs networking
/// (message <see cref="SettingsNet.MessageId"/>, a JSON string each way), and "!llm ..." chat commands
/// are answered with a private server message. Only the server owner and clients with the
/// "Manage settings" permission may look at or change anything.
/// </summary>
internal sealed class SettingsBridge
{
    readonly Func<SettingsService?> service;
    readonly Func<GameAdapter?> adapter;
    INetworkingService? net;

    public SettingsBridge(Func<SettingsService?> service, Func<GameAdapter?> adapter)
    {
        this.service = service;
        this.adapter = adapter;
    }

    /// <summary>Registers the network message. Kept separate so a LuaCs API change only disables the window, not the mod.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void Register()
    {
        net = LuaCsSetup.Instance?.NetworkingService;
        if (net == null) throw new InvalidOperationException("LuaCs networking service not available");
        net.Receive(SettingsNet.MessageId, (NetMessageReceived)OnMessage);
        net.Receive(ControlNet.MessageId, (NetMessageReceived)OnControlRequest);
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
