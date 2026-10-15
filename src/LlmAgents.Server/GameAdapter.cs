using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using LlmAgents.Agents;
using Microsoft.Xna.Framework;

namespace LlmAgents;

/// <summary>
/// The only place that touches Barotrauma internals. Orders are issued the same way the server
/// applies an order a player gives through the command UI (Character.SetOrder + OrderChatMessage),
/// so the vanilla AIObjective machinery does the actual work.
/// </summary>
public sealed class GameAdapter : IGameApi
{
    const float UnitsPerMeter = 100f;
    const float ThreatRadiusMeters = 25f;

    readonly ModSettings settings;

    public GameAdapter(ModSettings settings) { this.settings = settings; }

    // ------------------------------------------------------------------ bots

    public IReadOnlyList<BotHandle> GetBots() =>
        Character.CharacterList
            .Where(IsControllableBot)
            .Select(c => new BotHandle(c.ID, c.Name, JobOf(c)))
            .ToList();

    static bool IsControllableBot(Character c) =>
        !c.IsDead && !c.Removed && c.IsBot && c.IsOnPlayerTeam && c.Info != null;

    static string JobOf(Character c) => c.JobIdentifier.IsEmpty ? "none" : c.JobIdentifier.Value;

    static Character? Find(int id) => Entity.FindEntityByID((ushort)id) as Character;

    public bool IsAlive(int botId) => Find(botId) is { IsDead: false, Removed: false };

    // ----------------------------------------------------------- observation

    public Observation Observe(int botId)
    {
        var c = Find(botId);
        var o = new Observation();
        if (c == null) return o;

        o.Name = c.Name;
        o.Job = JobOf(c);
        o.HealthPercent = HealthPercent(c);
        o.OxygenPercent = (int)Math.Round(Math.Clamp(c.OxygenAvailable, 0f, 100f));

        var hull = c.CurrentHull;
        o.Room = hull != null ? RoomId(hull) : null;
        o.RoomWaterPercent = hull != null ? (int)Math.Round(hull.WaterPercentage) : 0;
        o.RoomFires = hull?.FireSources?.Count ?? 0;
        o.CurrentOrder = DescribeOrders(c);

        o.Inventory = (c.Inventory?.AllItems ?? Enumerable.Empty<Item>())
            .GroupBy(i => i.Name)
            .Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key)
            .Take(20).ToList();

        var sub = c.Submarine;
        if (sub != null)
        {
            o.Rooms = Hull.HullList
                .Where(h => h.Submarine == sub && !h.IsWetRoom)
                .Select(h => new RoomInfo(RoomId(h), (int)Math.Round(h.WaterPercentage), h.FireSources?.Count ?? 0))
                .Take(40).ToList();

            var breaches = Gap.GapList
                .Where(g => g.Submarine == sub && g.ConnectedWall != null && !g.IsRoomToRoom && g.Open > 0.01f)
                .ToList();
            o.Leaks = breaches
                .Select(g => g.linkedTo.OfType<Hull>().FirstOrDefault())
                .Where(h => h != null)
                .GroupBy(h => RoomId(h!))
                .Select(g => g.Count() > 1 ? $"{g.Key} ({g.Count()} breaches)" : g.Key)
                .Take(10).ToList();

            var reactor = Item.ItemList
                .Where(i => i.Submarine == sub)
                .Select(i => i.GetComponent<Reactor>())
                .FirstOrDefault(r => r != null);
            o.Submarine = new SubInfo(
                reactor != null ? (int)Math.Round(reactor.FissionRate) : null,
                (int)Math.Round(sub.RealWorldDepth),
                breaches.Count);
        }

        o.Crew = Character.CharacterList
            .Where(x => x != c && !x.IsDead && !x.Removed && x.IsHuman && x.TeamID == c.TeamID)
            .Select(x => new CrewInfo(
                x.Name, JobOf(x), x.CurrentHull != null ? RoomId(x.CurrentHull) : null,
                HealthPercent(x), x.IsPlayer, DescribeOrders(x)))
            .Take(20).ToList();

        o.Threats = Character.CharacterList
            .Where(x => !x.IsDead && !x.Removed && x != c && IsHostileTo(x, c))
            .Select(x => (x, d: Vector2.Distance(x.WorldPosition, c.WorldPosition) / UnitsPerMeter))
            .Where(t => t.d < ThreatRadiusMeters)
            .OrderBy(t => t.d)
            .Select(t => new ThreatInfo(
                t.x.IsHuman ? "hostile " + t.x.Name : t.x.SpeciesName.Value,
                (int)Math.Round(t.d),
                t.x.CurrentHull != null ? RoomId(t.x.CurrentHull) : null))
            .Take(6).ToList();

        return o;
    }

    static int HealthPercent(Character c) =>
        (int)Math.Round(Math.Clamp(c.Vitality / Math.Max(1f, c.MaxVitality) * 100f, 0f, 100f));

    static bool IsHostileTo(Character other, Character self) =>
        other.AIController is EnemyAIController || (other.IsHuman && !HumanAIController.IsFriendly(self, other));

    /// <summary>Stable room id the LLM can copy back, e.g. "Engine Room [h57]". Resolved by the trailing entity id.</summary>
    public static string RoomId(Hull h)
    {
        var name = h.DisplayName?.Value;
        if (string.IsNullOrWhiteSpace(name)) name = h.RoomName;
        if (string.IsNullOrWhiteSpace(name)) name = "Room";
        return $"{name} [h{h.ID}]";
    }

    static Hull? ResolveRoom(string roomId)
    {
        int open = roomId.LastIndexOf("[h", StringComparison.OrdinalIgnoreCase);
        if (open >= 0 && ushort.TryParse(roomId.Substring(open + 2).TrimEnd(']', ' '), out var id))
            return Entity.FindEntityByID(id) as Hull;
        var plain = roomId.Trim();
        return Hull.HullList.FirstOrDefault(h => string.Equals(h.DisplayName?.Value, plain, StringComparison.OrdinalIgnoreCase))
            ?? Hull.HullList.FirstOrDefault(h => string.Equals(h.RoomName, plain, StringComparison.OrdinalIgnoreCase));
    }

    static string DescribeOrders(Character c)
    {
        var orders = c.CurrentOrders;
        if (orders == null || orders.Count == 0) return "none";
        return string.Join(", ", orders.Where(o => o != null).Select(o =>
            o.Option.IsEmpty ? o.Identifier.Value : $"{o.Identifier.Value}:{o.Option.Value}"));
    }

    // --------------------------------------------------------------- actions

    public string GoToRoom(int botId, string roomId)
    {
        if (Find(botId) is not { } c) return "error: character gone";
        var hull = ResolveRoom(roomId);
        if (hull == null) return $"error: unknown room '{roomId}'";
        if (!TryPrefab("wait", out var prefab, out var err)) return err;

        // "wait" with a position target = go there and stay (same as a player's "wait here" order on the map).
        var order = new Order(prefab, Identifier.Empty, new OrderTarget(hull.WorldPosition, hull), orderGiver: c);
        return Issue(c, order) ? $"ok, heading to {RoomId(hull)}" : "error: could not issue order";
    }

    public string Follow(int botId, string targetName)
    {
        if (Find(botId) is not { } c) return "error: character gone";
        var target = FindCrew(targetName, c);
        if (target == null) return $"error: no crew member '{targetName}'";
        if (!TryPrefab("follow", out var prefab, out var err)) return err;

        // The vanilla follow objective follows the *order giver*.
        var order = new Order(prefab, targetEntity: null, targetItem: null, orderGiver: target);
        return Issue(c, order) ? $"ok, following {target.Name}" : "error: could not issue order";
    }

    public string SimpleOrder(int botId, string orderKey)
    {
        if (Find(botId) is not { } c) return "error: character gone";
        if (!Tools.SimpleOrders.TryGetValue(orderKey, out var id)) return $"error: unsupported order '{orderKey}'";
        if (!TryPrefab(id, out var prefab, out var err)) return err;

        Order order = id == "wait"
            ? new Order(prefab, Identifier.Empty, new OrderTarget(c.WorldPosition, c.CurrentHull), orderGiver: c)
            : new Order(prefab, targetEntity: null, targetItem: null, orderGiver: c);
        return Issue(c, order) ? "ok" : $"error: could not issue '{id}'";
    }

    public string Operate(int botId, string role, string? option)
    {
        if (Find(botId) is not { } c) return "error: character gone";

        string orderId;
        Func<Item, bool>? match;
        switch (role)
        {
            case "reactor": orderId = "operatereactor"; match = i => i.GetComponent<Reactor>() != null; break;
            case "navigation": orderId = "steer"; match = i => i.GetComponent<Steering>() != null; break;
            case "weapons": orderId = "operateweapons"; match = i => i.GetComponent<Turret>() != null; break;
            case "pumps": orderId = "pumpwater"; match = null; break; // all pumps, no specific target
            default: return $"error: unknown role '{role}'";
        }
        if (!TryPrefab(orderId, out var prefab, out var err)) return err;

        Identifier opt = Identifier.Empty;
        if (!string.IsNullOrEmpty(option))
        {
            opt = option.ToIdentifier();
            if (!prefab.AllOptions.Contains(opt))
                return $"error: option '{option}' not valid for {role}; valid: {string.Join(", ", prefab.Options.Select(x => x.Value))}";
        }
        else if (prefab.Options.Length > 0)
        {
            opt = prefab.Options.First();
        }

        Order order;
        if (match == null)
        {
            order = new Order(prefab, opt, targetEntity: null, targetItem: null, orderGiver: c);
        }
        else
        {
            var item = Item.ItemList
                .Where(i => i.Submarine == c.Submarine && !i.Removed && match(i))
                .OrderBy(i => Vector2.DistanceSquared(i.WorldPosition, c.WorldPosition))
                .FirstOrDefault();
            if (item == null) return $"error: no {role} station on this submarine";
            order = new Order(prefab, opt, item, prefab.GetTargetItemComponent(item), orderGiver: c);
        }
        return Issue(c, order) ? $"ok, operating {role}{(opt.IsEmpty ? "" : " (" + opt.Value + ")")}" : "error: could not issue order";
    }

    public string Dismiss(int botId)
    {
        if (Find(botId) is not { } c) return "error: character gone";
        if (c.CurrentOrders == null || c.CurrentOrders.Count == 0) return "ok (no orders to drop)";
        if (!TryPrefab("dismissed", out var prefab, out var err)) return err;
        var order = new Order(prefab, targetEntity: null, targetItem: null, orderGiver: c);
        return Issue(c, order) ? "ok, back to default behaviour" : "error: could not dismiss";
    }

    public void Speak(int botId, string text)
    {
        if (Find(botId) is not { } c) return;
        c.Speak(text, ChatMessageType.Default);
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

    static Character? FindCrew(string name, Character self)
    {
        var crew = Character.CharacterList.Where(x => x != self && !x.IsDead && !x.Removed && x.IsHuman && x.TeamID == self.TeamID).ToList();
        return crew.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? crew.FirstOrDefault(x => x.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Applies an order on the server and (optionally) broadcasts it, mirroring how the server handles
    /// an order a player issues (see GameServer chat handling: SetOrder + SendOrderChatMessage).
    /// </summary>
    bool Issue(Character c, Order order)
    {
        try
        {
            order = order.WithManualPriority(CharacterInfo.HighestManualOrderPriority);
            c.SetOrder(order, isNewOrder: true, speak: false, force: true);
            if (settings.Global.SyncOrdersToClients && GameMain.Server != null)
                GameMain.Server.SendOrderChatMessage(new OrderChatMessage(order, c, c));
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"order '{order.Identifier}' for {c.Name} failed: {e.Message}");
            return false;
        }
    }
}
