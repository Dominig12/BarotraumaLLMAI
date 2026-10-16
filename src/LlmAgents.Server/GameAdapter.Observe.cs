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

/// <summary>What a bot sees: the observation sent to the model and the descriptions it is built from.</summary>
public sealed partial class GameAdapter
{
    // ----------------------------------------------------------- observation

    public string? RoomOf(int botId) => Find(botId)?.CurrentHull is { } h ? RoomId(h) : null;

    public BotBrief Brief(int botId)
    {
        var c = Find(botId);
        if (c == null) return new BotBrief(null, "none", null, null);
        return new BotBrief(c.CurrentHull is { } h ? RoomId(h) : null, DescribeOrders(c),
            tasks.TryGetValue(c.ID, out var task) ? task.Describe() : null, DescribeActivity(c));
    }

    public Observation Observe(int botId)
    {
        var c = Find(botId);
        var o = new Observation();
        if (c == null) return o;

        o.Name = c.Name;
        o.Job = JobOf(c);
        o.HealthPercent = HealthPercent(c);
        o.Injuries = ItemQueries.DescribeInjuries(c);
        o.OxygenPercent = (int)Math.Round(Math.Clamp(c.OxygenAvailable, 0f, 100f));

        var hull = c.CurrentHull;
        o.Room = hull != null ? RoomId(hull) : null;
        o.RoomWaterPercent = hull != null ? (int)Math.Round(hull.WaterPercentage) : 0;
        o.RoomFires = hull?.FireSources?.Count ?? 0;
        o.CurrentOrder = DescribeOrders(c);
        o.CurrentTask = tasks.TryGetValue(c.ID, out var task) ? task.Describe() : null;
        o.Doing = DescribeActivity(c);

        var carried = (c.Inventory?.AllItems ?? Enumerable.Empty<Item>()).ToList();
        // the ID card is always there and only costs tokens
        // what is worn / held (with the slot), what is carried, how full tools and tanks are, what is inside bags
        o.Equipped = carried.Where(i => c.HasEquippedItem(i) && !InventoryView.IsBoring(i)).Distinct()
            .Select(i => (InventoryView.SlotName(c, i) is { } slot ? slot + ": " : "") + InventoryView.Describe(i)).Take(10).ToList();
        o.Inventory = carried.Where(i => !c.HasEquippedItem(i))
            .GroupBy(i => i.Prefab)
            .Select(g => g.Count() > 1 ? $"{ItemQueries.Label(g.First())} x{g.Count()}" : InventoryView.Describe(g.First()))
            .Take(20).ToList();
        o.InventorySpace = InventoryView.Space(c);
        o.Gear = DescribeGear(c);

        var sub = c.Submarine;
        if (sub != null)
        {
            var subHulls = Hull.HullList.Where(h => h.Submarine == sub && !h.IsWetRoom).ToList();
            o.Rooms = subHulls
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

            var reactor = FindReactor(c);
            var container = reactor?.Item.GetComponent<ItemContainer>();
            float volume = subHulls.Sum(h => h.Volume);
            int? oxygen = volume > 0 ? (int)Math.Round(subHulls.Sum(h => h.OxygenPercentage * h.Volume) / volume) : null;
            o.Submarine = new SubInfo(
                reactor != null ? (int)Math.Round(reactor.FissionRate) : null,
                (int)Math.Round(sub.RealWorldDepth),
                breaches.Count,
                ReactorFuelPercent: container != null ? (int)Math.Round(Math.Min(100f, FuelInReactor(container))) : null,
                ReactorTemperaturePercent: reactor != null ? (int)Math.Round(reactor.Temperature) : null,
                FuelRodsAboard: reactor != null ? SpareFuelRods(c, container).Count : null,
                OxygenPercent: oxygen);
        }

        var crew = Character.CharacterList
            .Where(x => x != c && !x.IsDead && !x.Removed && x.IsHuman && x.TeamID == c.TeamID)
            .ToList();
        o.Crew = crew
            .Select(x => new CrewInfo(
                x.Name, JobOf(x), x.CurrentHull != null ? RoomId(x.CurrentHull) : null,
                HealthPercent(x), x.IsPlayer, DescribeOrders(x),
                HealthPercent(x) < 95 ? ItemQueries.DescribeInjuries(x, 3) : null))
            .Take(20).ToList();

        bool someoneHurt = o.Injuries != null || o.Crew.Any(x => x.Injuries != null);
        if (someoneHurt || o.Job == "medicaldoctor")
            o.MedicalSupplies = ItemQueries.MedicalSupplies(c, o.Job == "medicaldoctor" ? 12 : 6);

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

        o.Marks = DescribeMarks(c);
        o.Infestations = DescribeFlora(c);
        return o;
    }


    static List<string> DescribeMarks(Character c)
    {
        var list = new List<string>();
        try
        {
            foreach (var m in ContextMarks.All().Where(m => m.Item.Submarine == c.Submarine || m.Item.GetRootInventoryOwner() is Character).Take(6))
            {
                var i = m.Item;
                var where = i.GetRootInventoryOwner() is Character holder ? (holder == c ? "on you" : "carried by " + holder.Name)
                          : i.Container != null ? ItemQueries.Where(i, c) : i.CurrentHull != null ? RoomId(i.CurrentHull) : "outside";
                var s = $"for the AI: {ItemQueries.Label(i)} in {where} (marked by {m.By}, {Ago(m.At)})";
                var inside = i.OwnInventory?.AllItems.ToList();
                if (inside is { Count: > 0 })
                    s += "; contains " + string.Join(", ", inside.GroupBy(x => x.Prefab).Take(6).Select(g => $"{g.Key.Name.Value} x{g.Count()}"));
                list.Add(s);
            }
            var decon = Item.DeconstructItems.Where(i => !i.Removed && i.Submarine == c.Submarine).ToList();
            if (decon.Count > 0)
                list.Add($"to deconstruct ({decon.Count}): " + string.Join(", ", decon.GroupBy(i => i.Prefab).Take(6).Select(g => $"{g.Key.Name.Value} x{g.Count()}"))
                         + " - deconstruct(item='marked for deconstruction') breaks them down");
            var ignored = Item.ItemList.Where(i => i.OrderedToBeIgnored && !i.Removed && i.Submarine == c.Submarine).ToList();
            if (ignored.Count > 0)
                list.Add("ignored by order (leave alone, do not take, use or deconstruct): "
                         + string.Join(", ", ignored.GroupBy(i => i.Prefab).Take(6).Select(g => $"{g.Key.Name.Value} x{g.Count()}")));
        }
        catch (Exception e) { Log.Warn("marks: " + e.Message); }
        return list;
    }

    static string Ago(double at)
    {
        var s = Math.Max(0, Timing.TotalTime - at);
        return s < 90 ? $"{s:0}s ago" : $"{s / 60:0} min ago";
    }

    /// <summary>What the game's AI is doing for the bot right now: "fix leaks > go to: Ballast [h5]", "operate item: Reactor", "idle".</summary>
    static string? DescribeActivity(Character c)
    {
        try
        {
            if (c.AIController is not HumanAIController ai || ai.ObjectiveManager == null) return null;
            var current = ai.ObjectiveManager.CurrentObjective;
            if (current == null) return "idle";
            var active = current.GetActiveObjective() ?? current;
            var s = Tag(current);
            if (active != current && Tag(active) != s) s += " > " + Tag(active);
            var target = TargetOf(active) ?? TargetOf(current);
            if (target != null) s += ": " + target;
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*\(ID: \d+\)", "").Replace("()", "").Replace("( )", "");
            return s.Length > 120 ? s.Substring(0, 120) : s;
        }
        catch { return null; }
    }

    static string Tag(AIObjective o)
    {
        var t = o.DebugTag;
        return string.IsNullOrWhiteSpace(t) ? o.GetType().Name.Replace("AIObjective", "").ToLowerInvariant() : t;
    }

    static string? TargetOf(AIObjective o) => o switch
    {
        AIObjectiveGoTo g => Where(g.Target),
        AIObjectiveRepairItem r when r.Item != null => r.Item.Name + (r.Item.CurrentHull != null ? " in " + RoomId(r.Item.CurrentHull) : ""),
        AIObjectiveFixLeak f when f.Leak != null => f.Leak.linkedTo.OfType<Hull>().FirstOrDefault() is Hull h ? RoomId(h) : null,
        AIObjectiveOperateItem op when op.Component?.Item != null => op.Component.Item.Name,
        AIObjectiveGetItem gi when gi.TargetItem != null => gi.TargetItem.Name,
        AIObjectiveCombat cb when cb.Enemy != null => cb.Enemy.IsHuman ? cb.Enemy.Name : cb.Enemy.SpeciesName.Value,
        _ => null,
    };

    static string? Where(ISpatialEntity? t) => t switch
    {
        null => null,
        Character ch => ch.Name,
        Item i => i.Name,
        Hull h => RoomId(h),
        _ => Hull.FindHull(t.WorldPosition) is Hull h2 ? RoomId(h2) : null,
    };

    static string DescribeGear(Character c)
    {
        var suit = c.Inventory?.FindEquippedItemByTag(Tags.HeavyDivingGear);
        var mask = suit == null ? c.Inventory?.FindEquippedItemByTag(Tags.LightDivingGear) : null;
        var worn = suit ?? mask;
        string s;
        if (worn == null) s = "no diving gear on";
        else
        {
            var tank = worn.OwnInventory?.AllItems.FirstOrDefault(i => i.HasTag(Tags.OxygenSource));
            s = $"{worn.Name} on ({(tank != null ? $"oxygen {tank.Condition:0}%" : "NO oxygen tank")})";
        }
        if (GearLock.IsManual(c)) s += "; gear locked (keep_gear)";
        else if (GearLock.InDanger(c)) s += "; DANGER: gear locked automatically";
        return s;
    }

    internal static int HealthPercent(Character c) =>
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

    string DescribeOrders(Character c)
    {
        PruneOwn(c);
        var orders = c.CurrentOrders;
        if (orders == null || orders.Count == 0) return "none";
        return string.Join(", ", orders.Where(o => o != null).Select(o =>
        {
            var s = (o.Option.IsEmpty ? o.Identifier.Value : $"{o.Identifier.Value}:{o.Option.Value}") + $" [p{o.ManualPriority}]";
            if (IsPlayerOrder(c, o)) s += $" (from player {o.OrderGiver.Name})";
            return s;
        }));
    }

    // ------------------------------------------------------------------ reactor helpers

    static Reactor? FindReactor(Character c) =>
        Item.ItemList
            .Where(i => i.Submarine == c.Submarine && !i.Removed)
            .Select(i => i.GetComponent<Reactor>())
            .Where(r => r != null)
            .OrderBy(r => Vector2.DistanceSquared(r!.Item.WorldPosition, c.WorldPosition))
            .FirstOrDefault();

    /// <summary>Sum of the condition of the fuel rods inside the reactor (100 = one full rod).</summary>
    internal static float FuelInReactor(ItemContainer container) =>
        container.Inventory.AllItems.Where(i => i.HasTag(Tags.ReactorFuel)).Sum(i => i.Condition);

    static List<Item> SpareFuelRods(Character c, ItemContainer? reactorContainer) =>
        ItemQueries.Available(c)
            .Where(i => i.HasTag(Tags.ReactorFuel) && i.Condition > 1 && (reactorContainer == null || i.ParentInventory != reactorContainer.Inventory))
            .ToList();
}
