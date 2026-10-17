using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using LlmAgents.Agents;
using Microsoft.Xna.Framework;

namespace LlmAgents;

/// <summary>
/// The only place that touches Barotrauma internals. Orders are issued the same way the server
/// applies an order a player gives through the command UI (Character.SetOrder + OrderChatMessage),
/// so the vanilla AIObjective machinery does the actual work. Multi-step actions (equip, craft,
/// treat...) run as <see cref="BotTask"/>s built from vanilla objectives.
///
/// Orders from human players are protected: a bot's own LLM decisions never replace them, unless the
/// decision was made to carry out a newer chat order from a player (see <see cref="BeginCommand"/>).
/// </summary>
public sealed partial class GameAdapter : IGameApi
{
    const float UnitsPerMeter = 100f;
    const float ThreatRadiusMeters = 25f;
    const double TaskTickSeconds = 0.25;

    /// <summary>Replaced when settings are changed in game.</summary>
    public ModSettings Settings { get; set; }
    readonly Dictionary<ushort, BotTask> tasks = new();
    readonly Dictionary<ushort, HashSet<string>> ownOrders = new();   // signatures of orders this mod issued, per bot
    readonly Dictionary<ushort, bool> authority = new();
    readonly List<GameReport> reports = new();
    readonly HashSet<ushort> controlled = new();                      // bots under full AI control
    readonly List<ControlEvent> controlEvents = new();
    readonly List<MenuOrder> menuOrders = new();
    double nextTaskTick, nextDangerCheck;

    public GameAdapter(ModSettings settings) { Settings = settings; }

    // ------------------------------------------------------------------ bots

    public IReadOnlyList<BotHandle> GetBots() =>
        Character.CharacterList
            .Where(IsControllableBot)
            .Select(c => new BotHandle(c.ID, c.Name, JobOf(c), TraitOf(c)))
            .ToList();

    static bool IsControllableBot(Character c) =>
        !c.IsDead && !c.Removed && c.IsBot && c.IsOnPlayerTeam && c.Info != null;

    /// <summary>The game's own personality trait (localized name), if the character has one.</summary>
    internal static string? TraitOf(Character c)
    {
        try
        {
            var t = c.Info?.PersonalityTrait;
            var name = t?.DisplayName.Value;
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch { return null; }
    }

    public bool IsControlled(Character c) => controlled.Contains(c.ID);

    /// <summary>Bots under full AI control (for the clients' crew list).</summary>
    public IReadOnlyCollection<ushort> ControlledIds => controlled;

    static string JobOf(Character c) => c.JobIdentifier.IsEmpty ? "none" : c.JobIdentifier.Value;

    static Character? Find(int id) => Entity.FindEntityByID((ushort)id) as Character;

    public bool IsAlive(int botId) => Find(botId) is { IsDead: false, Removed: false };

    public void BeginCommand(int botId, bool playerAuthority) => authority[(ushort)botId] = playerAuthority;

    /// <summary>The current decision may override orders players gave through the command menu.</summary>
    bool HasAuthority(Character c) => controlled.Contains(c.ID) || (authority.TryGetValue(c.ID, out var a) && a);

    /// <summary>Only a human player's chat order may take a locked diving suit off (not full AI control: the lock is a safety net).</summary>
    bool PlayerSaidSo(Character c) => authority.TryGetValue(c.ID, out var a) && a;

    // ------------------------------------------------------------ full AI control

    public void SetAiControl(int botId, bool on)
    {
        if (on) controlled.Add((ushort)botId); else controlled.Remove((ushort)botId);
    }

    public IReadOnlyList<ControlEvent> DrainControlEvents()
    {
        if (controlEvents.Count == 0) return Array.Empty<ControlEvent>();
        var r = controlEvents.ToList();
        controlEvents.Clear();
        return r;
    }

    /// <summary>Called from the Harmony patch when a player gives (or dismisses) the "Full AI control" order.</summary>
    internal void OnControlOrder(Character bot, Character? giver, ControlOrder.Kind kind)
    {
        if (!IsControllableBot(bot))
        {
            if (kind == ControlOrder.Kind.Toggle) Log.Info($"AI control order for {bot.Name} ignored: not an AI crew member");
            return;
        }
        bool on = controlled.Contains(bot.ID);
        bool grant = kind == ControlOrder.Kind.Toggle && !on;
        if (!grant && !on) return; // nothing to take back
        if (grant) controlled.Add(bot.ID); else controlled.Remove(bot.ID);
        controlEvents.Add(new ControlEvent(bot.ID, grant, giver?.Name ?? "a player"));
    }

    /// <summary>Called from the Harmony patch when a human player gives a bot an ordinary order through the command menu.
    /// The player's order wins: the bot's running task is cancelled, and the agent layer drops its plan.</summary>
    internal void OnMenuOrder(Character bot, Order order, bool isNewOrder)
    {
        if (!IsControllableBot(bot)) return;
        if (!isNewOrder && !order.IsDismissal)
        {
            // the player only changed the order's priority (dragged it in the crew list): the task goes on
            menuOrders.Add(new MenuOrder(bot.ID, order.OrderGiver?.Name ?? "a player", $"priority change: {Name(order)} is now p{order.ManualPriority}", PriorityOnly: true));
            return;
        }
        string task = "";
        if (tasks.Remove(bot.ID, out var t))
        {
            t.Cancel();
            task = $" (your task '{t.Name}' was cancelled)";
        }
        menuOrders.Add(new MenuOrder(bot.ID, order.OrderGiver?.Name ?? "a player", DescribeOrder(order) + task, order.IsDismissal));
    }

    public IReadOnlyList<MenuOrder> DrainMenuOrders()
    {
        if (menuOrders.Count == 0) return Array.Empty<MenuOrder>();
        var r = menuOrders.ToList();
        menuOrders.Clear();
        return r;
    }

    /// <summary>"operatereactor:powerup", "wait at Engine [h2]", "follow Daniil", "fixleaks".</summary>
    static string DescribeOrder(Order o)
    {
        if (o.IsDismissal) return o.Option.IsEmpty ? "dismiss all orders" : "dismiss " + o.Option.Value;
        var s = Name(o);
        if (o.Identifier == "follow" && o.OrderGiver != null) s += " " + o.OrderGiver.Name;
        else if (o.TargetEntity is Item item) s += " " + item.Name + (item.CurrentHull != null ? " in " + RoomId(item.CurrentHull) : "");
        else if (o.TargetPosition?.Hull is Hull h) s += " at " + RoomId(h);
        return s;
    }

    public void NotifyPlayer(string playerName, string text)
    {
        var server = GameMain.Server;
        if (server == null || string.IsNullOrWhiteSpace(playerName)) return;
        var client = server.ConnectedClients.FirstOrDefault(c => c.Character?.Name == playerName)
                  ?? server.ConnectedClients.FirstOrDefault(c => c.Name == playerName);
        if (client != null) server.SendDirectChatMessage(text, client, ChatMessageType.Server);
    }


    // ------------------------------------------------------------- chat input

    /// <summary>
    /// Builds a <see cref="ChatCommand"/> for a message a player typed. Only bots that could hear it are listeners:
    /// within speaking range, or both sides wearing working headsets. The LuaCs chat event does not say whether the
    /// player used the radio channel, so a player with a headset is assumed to be on the radio.
    /// </summary>
    public ChatCommand? BuildChatCommand(Client sender, string text, ChatMessageType type)
    {
        if (type != ChatMessageType.Default && type != ChatMessageType.Radio) return null; // ignore orders, dead chat, private...
        var speaker = sender.Character;
        var bots = Character.CharacterList.Where(IsControllableBot).ToList();

        if (speaker == null || speaker.IsDead || speaker.Removed)
        {
            if (!Settings.Chat.AllowSpectators) return null;
            return new ChatCommand(sender.Name, null, text, bots.Select(b => (int)b.ID).ToList(), ViaRadio: true);
        }

        bool radio = type == ChatMessageType.Radio || ChatMessage.CanUseRadio(speaker);
        var listeners = bots
            .Where(b => b != speaker && b.TeamID == speaker.TeamID)
            .Where(b => !string.IsNullOrEmpty(ChatMessage.ApplyDistanceEffect(text, ChatMessageType.Default, speaker, b))
                     || (radio && !string.IsNullOrEmpty(ChatMessage.ApplyDistanceEffect(text, ChatMessageType.Radio, speaker, b))))
            .Select(b => (int)b.ID)
            .ToList();
        var job = speaker.Info?.Job?.Name?.Value;
        return new ChatCommand(speaker.Name, string.IsNullOrEmpty(job) ? null : job, text, listeners, radio,
            speaker.CurrentHull != null ? RoomId(speaker.CurrentHull) : null);
    }

    // --------------------------------------------------------------- helpers

    static bool TryPrefab(string identifier, out OrderPrefab prefab, out string error)
    {
        if (OrderPrefab.Prefabs.TryGet(identifier.ToIdentifier(), out var p) && p != null)
        {
            prefab = p;
            error = "";
            return true;
        }
        prefab = null!;
        error = $"error: order '{identifier}' does not exist in this game version";
        return false;
    }

    static Character? FindCrew(string name, Character self, bool includeSelf)
    {
        var crew = Character.CharacterList
            .Where(x => (includeSelf || x != self) && !x.IsDead && !x.Removed && x.IsHuman && x.TeamID == self.TeamID).ToList();
        return crew.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? crew.FirstOrDefault(x => x.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            ?? crew.FirstOrDefault(x => Translit.ToLatin(x.Name).Contains(Translit.ToLatin(name), StringComparison.OrdinalIgnoreCase));
    }
}
