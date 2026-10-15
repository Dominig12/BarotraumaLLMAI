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
public sealed class GameAdapter : IGameApi
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

    // ----------------------------------------------------------- observation

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

        var carried = (c.Inventory?.AllItems ?? Enumerable.Empty<Item>()).ToList();
        o.Equipped = carried.Where(i => c.HasEquippedItem(i)).Select(ItemQueries.Label).Distinct().Take(10).ToList();
        o.Inventory = carried.Where(i => !c.HasEquippedItem(i))
            .GroupBy(i => i.Prefab)
            .Select(g => g.Count() > 1 ? $"{ItemQueries.Label(g.First())} x{g.Count()}" : ItemQueries.Label(g.First()))
            .Take(20).ToList();
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

        return o;
    }

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

    // --------------------------------------------------------------- orders

    public string GoToRoom(int botId, string roomId, int priority = Tools.TopPriority)
    {
        if (Find(botId) is not { } c) return "error: character gone";
        var hull = ResolveRoom(roomId);
        if (hull == null) return $"error: unknown room '{roomId}'";
        if (!TryPrefab("wait", out var prefab, out var err)) return err;

        // "wait" with a position target = go there and stay (same as a player's "wait here" order on the map).
        var order = new Order(prefab, Identifier.Empty, new OrderTarget(hull.WorldPosition, hull), orderGiver: c);
        return Issue(c, order, $"ok, heading to {RoomId(hull)}", priority);
    }

    public string Follow(int botId, string targetName, int priority = Tools.TopPriority)
    {
        if (Find(botId) is not { } c) return "error: character gone";
        var target = FindCrew(targetName, c, includeSelf: false);
        if (target == null) return $"error: no crew member '{targetName}'";
        if (!TryPrefab("follow", out var prefab, out var err)) return err;

        // The vanilla follow objective follows the *order giver*.
        var order = new Order(prefab, targetEntity: null, targetItem: null, orderGiver: target);
        return Issue(c, order, $"ok, following {target.Name}", priority);
    }

    public string SimpleOrder(int botId, string orderKey, int priority = Tools.TopPriority)
    {
        if (Find(botId) is not { } c) return "error: character gone";
        if (!Tools.SimpleOrders.TryGetValue(orderKey, out var id)) return $"error: unsupported order '{orderKey}'";
        if (!TryPrefab(id, out var prefab, out var err)) return err;

        Order order = id == "wait"
            ? new Order(prefab, Identifier.Empty, new OrderTarget(c.WorldPosition, c.CurrentHull), orderGiver: c)
            : new Order(prefab, targetEntity: null, targetItem: null, orderGiver: c);
        return Issue(c, order, "ok", priority);
    }

    public string Operate(int botId, string role, string? option, int priority = Tools.TopPriority)
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
        var r = Issue(c, order, $"ok, operating {role}{(opt.IsEmpty ? "" : " (" + opt.Value + ")")}", priority);
        if (role == "reactor" && r.StartsWith("ok") && c.GetSkillLevel("electrical".ToIdentifier()) < 30)
            r += ". Note: with low electrical skill you will not refuel it; use refuel_reactor for that";
        return r;
    }

    public string Dismiss(int botId)
    {
        if (Find(botId) is not { } c) return "error: character gone";
        CancelTask(c, quiet: true);
        var orders = c.CurrentOrders?.Where(o => o != null).ToList() ?? new List<Order>();
        if (orders.Count == 0) return "ok (no orders to drop)";

        bool all = HasAuthority(c);
        var toDrop = orders.Where(o => all || !IsPlayerOrder(c, o)).ToList();
        foreach (var o in toDrop) DropOrder(c, o);
        var kept = orders.Except(toDrop).Where(o => IsPlayerOrder(c, o)).ToList();
        return kept.Count == 0
            ? "ok, back to default behaviour"
            : $"ok, dropped your own orders; kept {string.Join(", ", kept.Select(o => $"{o.Identifier} from player {o.OrderGiver.Name}"))}";
    }

    public void Speak(int botId, string text)
    {
        if (Find(botId) is not { } c) return;
        // null = let the game decide, exactly like vanilla bots: radio if the bot wears a working headset, else out loud.
        ChatMessageType? type = Settings.Global.SpeechChannel == "local" ? ChatMessageType.Default : null;
        c.Speak(text, type);
    }

    // --------------------------------------------------------------- order bookkeeping

    static string Signature(Order o)
    {
        var target = o.TargetEntity?.ID.ToString() ?? (o.TargetPosition?.Hull?.ID.ToString() ?? "-");
        return $"{o.Identifier}|{o.Option}|{target}|{o.OrderGiver?.ID}";
    }

    static bool Same(Order a, Order b) => Signature(a) == Signature(b);

    bool IsOwn(Character c, Order o) => ownOrders.TryGetValue(c.ID, out var set) && set.Contains(Signature(o));

    /// <summary>Forget own orders the game no longer has (completed, or dismissed by a player), so a later identical
    /// order from a player is not mistaken for one of ours.</summary>
    void PruneOwn(Character c)
    {
        if (!ownOrders.TryGetValue(c.ID, out var set) || set.Count == 0) return;
        var live = (c.CurrentOrders ?? new List<Order>()).Where(o => o != null).Select(Signature).ToHashSet();
        set.RemoveWhere(sig => !live.Contains(sig));
    }

    /// <summary>An order a human player gave this bot (command menu), and not one this mod issued.</summary>
    bool IsPlayerOrder(Character c, Order o) =>
        o.OrderGiver is { IsPlayer: true } && !o.IsDismissal && !IsOwn(c, o);

    List<Order> PlayerOrders(Character c)
    {
        PruneOwn(c);
        return c.CurrentOrders?.Where(o => o != null && IsPlayerOrder(c, o)).ToList() ?? new List<Order>();
    }

    /// <summary>Error text if the bot must not start something that would push a player's order aside, else null.</summary>
    string? PlayerOrderBlock(Character c, Order? replacement)
    {
        if (HasAuthority(c)) return null;
        var player = PlayerOrders(c);
        if (player.Count == 0) return null;
        if (replacement != null && player.Any(p => Same(p, replacement))) return null;
        var p0 = player[0];
        return $"error: you are carrying out '{p0.Identifier}' ordered by player {p0.OrderGiver.Name}; keep doing it unless a player tells you otherwise";
    }

    static string Name(Order o) => o.Option.IsEmpty ? o.Identifier.Value : $"{o.Identifier.Value}:{o.Option.Value}";

    /// <summary>
    /// Gives the bot an order at a priority (1..3). Vanilla semantics: the bot's orders at that priority or lower move
    /// one step down, at most 3 orders. Without authority the bot's own orders always stay below the players' orders,
    /// and nothing that would cancel a player's order (same order type, another movement order) is allowed.
    /// </summary>
    string Issue(Character c, Order order, string okText, int priority)
    {
        priority = Math.Clamp(priority, 1, CharacterInfo.HighestManualOrderPriority);
        string note = "";
        if (!HasAuthority(c))
        {
            var player = PlayerOrders(c);
            if (player.Any(p => Same(p, order))) return "ok (already doing that, ordered by a player)";
            if (player.Count > 0)
            {
                var clash = player.FirstOrDefault(p => p.Identifier == order.Identifier)
                    ?? (order.Category == OrderCategory.Movement && order.AutoDismiss ? player.FirstOrDefault(p => p.Category == OrderCategory.Movement) : null);
                if (clash != null)
                    return $"error: that would cancel '{Name(clash)}' ordered by player {clash.OrderGiver.Name}; keep doing it unless a player tells you otherwise";
                int below = player.Min(p => p.ManualPriority) - 1;
                if (below < 1)
                    return $"error: your order slots are taken by orders from players ({string.Join(", ", player.Select(Name))}); keep doing them unless a player tells you otherwise";
                if (priority > below)
                {
                    priority = below;
                    note = $" (priority lowered to {priority}: orders from players come first)";
                }
            }
            if (OperatedByOrderOfPlayer(c, order) is { } other)
                return $"error: {other.Name} was ordered by a player to operate that; pick another task or ask them";
        }

        var current = c.CurrentOrders?.Where(o => o != null).ToList() ?? new List<Order>();
        var existing = current.FirstOrDefault(o => Same(o, order));
        if (existing != null && !tasks.ContainsKey(c.ID))
        {
            if (existing.ManualPriority == priority) return "ok (already doing that)";
            return SetOrder(c, existing.WithManualPriority(priority), isNewOrder: false)
                ? $"ok, {Name(existing)} is now priority {priority}{note}"
                : "error: could not change the priority";
        }

        CancelTask(c, quiet: true);
        if (!SetOrder(c, order.WithManualPriority(priority))) return "error: could not issue order";
        if (!ownOrders.TryGetValue(c.ID, out var set)) ownOrders[c.ID] = set = new HashSet<string>();
        set.Add(Signature(order));
        return okText + (priority < CharacterInfo.HighestManualOrderPriority ? $" [p{priority}]" : "") + note;
    }

    /// <summary>Operating a station another bot was told to operate by a player would dismiss that bot's order (vanilla autodismiss).</summary>
    Character? OperatedByOrderOfPlayer(Character c, Order order)
    {
        if (order.Category != OrderCategory.Operate || order.TargetEntity == null || !order.AutoDismiss) return null;
        foreach (var other in Character.CharacterList)
        {
            if (other == c || other.IsDead || other.TeamID != c.TeamID || !other.IsBot) continue;
            if (other.CurrentOrders?.Any(o => o != null && o.Identifier == order.Identifier && o.TargetEntity == order.TargetEntity && IsPlayerOrder(other, o)) == true)
                return other;
        }
        return null;
    }

    public string SetOrderPriority(int botId, string query, int priority)
    {
        if (Find(botId) is not { } c) return "error: character gone";
        PruneOwn(c);
        var orders = c.CurrentOrders?.Where(o => o != null && !o.IsDismissal).ToList() ?? new List<Order>();
        if (orders.Count == 0) return "error: you have no orders";

        var q = query.Trim().ToLowerInvariant();
        q = q.Split(' ', '[', '(')[0];
        string? opt = null;
        int sep = q.IndexOfAny(new[] { ':', '.' });
        if (sep > 0) { opt = q.Substring(sep + 1); q = q.Substring(0, sep); }
        var id = Tools.OrderAliases.TryGetValue(q, out var alias) ? alias : q;
        var match = orders.FirstOrDefault(o => o.Identifier == id && (opt == null || o.Option == opt))
                 ?? orders.FirstOrDefault(o => o.Identifier == id);
        if (match == null)
            return $"error: no current order '{query}'; your orders: {string.Join(", ", orders.Select(o => $"{Name(o)} [p{o.ManualPriority}]"))}";

        bool fromPlayer = IsPlayerOrder(c, match);
        if (fromPlayer && !HasAuthority(c))
            return $"error: '{Name(match)}' was ordered by player {match.OrderGiver.Name}; only a player can change it";

        if (priority <= 0)
        {
            DropOrder(c, match);
            return $"ok, dropped {Name(match)}";
        }
        priority = Math.Clamp(priority, 1, CharacterInfo.HighestManualOrderPriority);
        string note = "";
        if (!HasAuthority(c))
        {
            var player = orders.Where(o => o != match && IsPlayerOrder(c, o)).ToList();
            if (player.Count > 0 && priority >= player.Min(p => p.ManualPriority))
            {
                priority = Math.Max(1, player.Min(p => p.ManualPriority) - 1);
                note = " (orders from players stay on top)";
            }
        }
        if (match.ManualPriority == priority) return $"ok ({Name(match)} already has priority {priority}){note}";
        return SetOrder(c, match.WithManualPriority(priority), isNewOrder: false)
            ? $"ok, {Name(match)} is now priority {priority}{note}"
            : "error: could not change the priority";
    }

    void DropOrder(Character c, Order o)
    {
        try
        {
            SetOrder(c, o.GetDismissal());
            if (ownOrders.TryGetValue(c.ID, out var set)) set.Remove(Signature(o));
        }
        catch (Exception e) { Log.Warn($"could not dismiss '{o.Identifier}' for {c.Name}: {e.Message}"); }
    }

    /// <summary>Applies an order the way the server handles one from the command UI (SetOrder + broadcast).</summary>
    bool SetOrder(Character c, Order order, bool isNewOrder = true)
    {
        try
        {
            c.SetOrder(order, isNewOrder: isNewOrder, speak: false, force: true);
            if (Settings.Global.SyncOrdersToClients && GameMain.Server != null)
                GameMain.Server.SendOrderChatMessage(new OrderChatMessage(order, c, c, isNewOrder));
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"order '{order.Identifier}' for {c.Name} failed: {e.Message}");
            return false;
        }
    }

    // --------------------------------------------------------------- tasks

    /// <summary>Called every frame from the plugin; ticks the running tasks a few times per second.</summary>
    public void Tick()
    {
        if (Timing.TotalTime >= nextDangerCheck)
        {
            nextDangerCheck = Timing.TotalTime + 1.0;
            try { GearLock.UpdateDanger(Settings); } catch (Exception e) { Log.Warn("danger check failed: " + e.Message); }
        }
        if (tasks.Count == 0 || Timing.TotalTime < nextTaskTick) return;
        nextTaskTick = Timing.TotalTime + TaskTickSeconds;
        foreach (var (id, task) in tasks.ToList())
        {
            var result = task.Tick();
            if (result == null) continue;
            tasks.Remove(id);
            reports.Add(new GameReport(id, $"task '{task.Name}' {result}"));
        }
    }

    public void Reset()
    {
        GearLock.Reset();
        foreach (var t in tasks.Values) t.Cancel();
        tasks.Clear();
        ownOrders.Clear();
        authority.Clear();
        controlled.Clear();       // restored from long-term memory when the agents are created again
        controlEvents.Clear();
        reports.Clear();
    }

    public IReadOnlyList<GameReport> DrainReports()
    {
        if (reports.Count == 0) return Array.Empty<GameReport>();
        var r = reports.ToList();
        reports.Clear();
        return r;
    }

    void CancelTask(Character c, bool quiet)
    {
        if (!tasks.Remove(c.ID, out var t)) return;
        t.Cancel();
        if (!quiet) reports.Add(new GameReport(c.ID, $"task '{t.Name}' cancelled"));
    }

    /// <param name="evenOverPlayerOrders">Short self-care tasks (putting on gear) are fine while carrying out a player's order.</param>
    string StartTask(Character c, BotTask task, bool evenOverPlayerOrders = false)
    {
        if (!evenOverPlayerOrders && PlayerOrderBlock(c, null) is { } blocked) return blocked;
        CancelTask(c, quiet: true);
        tasks[c.ID] = task;
        return $"ok, started: {task.Name} (result will be reported)";
    }

    static bool Ai(Character c, out HumanAIController ai)
    {
        ai = (c.AIController as HumanAIController)!;
        return ai != null;
    }

    public string Equip(int botId, string query)
    {
        if (Find(botId) is not { } c || !Ai(c, out var ai)) return "error: character gone";
        var item = ItemQueries.Find(c, query).FirstOrDefault();
        if (item == null) return $"error: no '{query}' available; try inspect(kind='items', query='{query}')";
        if (c.HasEquippedItem(item)) return $"ok ({item.Name} is already equipped)";
        return StartTask(c, new EquipTask(c, ai, item), evenOverPlayerOrders: true);
    }

    public string RefuelReactor(int botId)
    {
        if (Find(botId) is not { } c || !Ai(c, out var ai)) return "error: character gone";
        var reactor = FindReactor(c);
        var container = reactor?.Item.GetComponent<ItemContainer>();
        if (reactor == null || container == null) return "error: no reactor on this submarine";
        var spare = SpareFuelRods(c, container).OrderByDescending(i => i.ConditionPercentage).FirstOrDefault();
        if (spare == null)
            return "error: no spare fuel rods aboard; craft one (craft item='fuel rod', check inspect recipe first) or find some";
        float fuel = FuelInReactor(container);
        if (fuel >= 60 && !HasAuthority(c))
            return $"error: the reactor still has {fuel:0}% fuel; refuel when it drops below about 20% (a player can order it earlier)";
        var inside = container.Inventory.AllItems.Where(i => i.HasTag(Tags.ReactorFuel)).ToList();
        if (inside.Count > 0 && container.Inventory.IsFull() && inside.All(i => i.ConditionPercentage >= spare.ConditionPercentage))
            return $"error: the reactor is full and every rod in it is fuller than the best spare one ({spare.ConditionPercentage:0}%)";
        return StartTask(c, new RefuelReactorTask(c, ai, reactor, container, spare));
    }

    public string Craft(int botId, string query, int amount, bool salvage = false)
    {
        if (Find(botId) is not { } c || !Ai(c, out var ai)) return "error: character gone";
        var (fabricator, recipe, error) = Crafting.Find(c, query);
        if (error != null) return error;
        var stock = Crafting.Stock(c, fabricator);
        var missing = Crafting.Missing(recipe!, stock, amount);
        if (missing.Count == 0) return StartTask(c, new CraftTask(c, ai, fabricator!, recipe!, amount));

        var missingText = string.Join(", ", missing.Select(m => $"{m.Req.FirstMatchingPrefab?.Name.Value ?? "?"} x{m.Missing}"));
        var (plan, problem) = Crafting.SalvagePlan(c, recipe!, stock, amount);
        if (!salvage)
            return $"error: cannot make {recipe!.DisplayName.Value} x{amount}: missing {missingText}. " +
                   (plan != null
                       ? $"It can be salvaged: deconstruct {string.Join(", ", plan.GroupBy(i => i.Prefab).Select(g => $"{g.Key.Name.Value} x{g.Count()}"))} — call craft again with salvage=true to do it all."
                       : $"No scrap aboard gives it ({problem}). inspect(kind='recipe') shows other ways.");
        if (plan == null) return $"error: cannot make {recipe!.DisplayName.Value}: missing {missingText}, and salvaging is not enough: {problem}";
        if (plan.Count == 0) return StartTask(c, new CraftTask(c, ai, fabricator!, recipe!, amount));
        var f = fabricator!; var r = recipe!;
        return StartTask(c, new SalvageAndCraftTask(c, ai, plan, () => new CraftTask(c, ai, f, r, amount), $"{r.DisplayName.Value} x{amount}")) +
               $"; will first deconstruct {string.Join(", ", plan.GroupBy(i => i.Prefab).Select(g => $"{g.Key.Name.Value} x{g.Count()}"))}";
    }

    public string Deconstruct(int botId, string query, string? from = null)
    {
        if (Find(botId) is not { } c || !Ai(c, out var ai)) return "error: character gone";
        if (!Crafting.HasDeconstructor(c)) return "error: no deconstructor on this submarine";
        bool all = query.Trim().ToLowerInvariant() is "all" or "everything" or "*" or "все" or "всё" or "everything in it";

        if (!string.IsNullOrWhiteSpace(from))
        {
            var (places, where) = ContainersAt(c, from!);
            if (places.Count == 0) return $"error: no container or room '{from}'; use a room id from 'rooms' or a container name (cabinet, locker, crate...)";
            var items = places.SelectMany(ic => ic.Inventory.AllItems)
                .Where(i => i.Prefab.AllowDeconstruct && i.Prefab.DeconstructItems.Any() && !c.HasEquippedItem(i))
                .Where(i => all || ItemQueries.MatchScore(i.Prefab, query) > 0)
                .Take(MaxDeconstructBatch).ToList();
            if (items.Count == 0) return $"error: nothing {(all ? "" : $"matching '{query}' ")}that can be deconstructed in {where}";
            return StartTask(c, new DeconstructManyTask(c, ai, items, where)) +
                   $"; {items.Count} item(s): {string.Join(", ", items.GroupBy(i => i.Prefab).Select(g => $"{g.Key.Name.Value} x{g.Count()}"))}";
        }

        if (all) return "error: 'all' needs 'from' (a container or room), so nothing important is broken down by accident";
        var item = ItemQueries.Find(c, query, i => i.Prefab.DeconstructItems.Any() && i.Prefab.AllowDeconstruct && !c.HasEquippedItem(i)).FirstOrDefault();
        if (item == null)
        {
            // maybe they named the material they want
            var hint = Crafting.Salvage(c, query);
            return $"error: no '{query}' that can be deconstructed. {hint}";
        }
        return StartTask(c, new DeconstructTask(c, ai, item));
    }

    const int MaxDeconstructBatch = 12;

    /// <summary>Storage containers by name or room ("cabinet", "шкаф", "Электрика [h140]", "шкаф в Электрика [h140]").</summary>
    (List<ItemContainer> Containers, string Where) ContainersAt(Character c, string from)
    {
        var storages = Item.ItemList
            .Where(i => i.Submarine == c.Submarine && !i.Removed && i.GetRootInventoryOwner() is not Character)
            .Select(i => (item: i, ic: i.GetComponent<ItemContainer>()))
            .Where(t => t.ic != null && t.item.GetComponent<Fabricator>() == null && t.item.GetComponent<Deconstructor>() == null
                        && t.item.GetComponent<Reactor>() == null && t.item.GetComponent<Turret>() == null)
            .ToList();

        var hull = from.Contains("[h") ? ResolveRoom(from) : ResolveRoom(from.Trim());
        // "<container> in/в <room>"
        var named = storages.Where(t => ItemQueries.MatchScore(t.item.Prefab, from) > 0).ToList();
        if (hull == null)
        {
            var parts = from.Split(new[] { " in ", " в ", " из ", " from " }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                hull = ResolveRoom(parts[1].Trim()) ?? Hull.HullList.FirstOrDefault(h => h.Submarine == c.Submarine && RoomId(h).Contains(parts[1].Trim(), StringComparison.OrdinalIgnoreCase));
                named = storages.Where(t => ItemQueries.MatchScore(t.item.Prefab, parts[0]) > 0).ToList();
            }
        }
        if (hull != null)
        {
            var inRoom = storages.Where(t => t.item.CurrentHull == hull).ToList();
            var both = inRoom.Intersect(named).ToList();
            return ((both.Count > 0 ? both : inRoom).Select(t => t.ic!).ToList(), RoomId(hull));
        }
        var nearest = named.OrderBy(t => Vector2.DistanceSquared(t.item.WorldPosition, c.WorldPosition)).Take(1).ToList();
        return (nearest.Select(t => t.ic!).ToList(),
                nearest.Count > 0 ? $"{nearest[0].item.Name} in {(nearest[0].item.CurrentHull != null ? RoomId(nearest[0].item.CurrentHull) : "?")}" : from);
    }

    public string Treat(int botId, string target, string? itemQuery)
    {
        if (Find(botId) is not { } c || !Ai(c, out var ai)) return "error: character gone";
        var patient = FindCrew(target, c, includeSelf: true);
        if (patient == null) return $"error: no crew member '{target}'";
        if (!ItemQueries.TreatableAfflictions(patient).Any()) return $"ok ({patient.Name} needs no treatment)";
        if (itemQuery != null && !ItemQueries.Find(c, itemQuery, ItemQueries.IsMedical).Any())
            return $"error: no '{itemQuery}' available; medical supplies: {string.Join(", ", ItemQueries.MedicalSupplies(c, 8))}";
        if (itemQuery == null && ItemQueries.ChooseTreatment(c, patient) == null)
            return $"error: no medicine on the sub helps with {ItemQueries.DescribeInjuries(patient)}; craft some (inspect recipe) or find it";
        return StartTask(c, new TreatTask(c, ai, patient, itemQuery));
    }

    // --------------------------------------------------------------- inventory

    static Item? FindCarried(Character c, string query) =>
        (c.Inventory?.AllItems ?? Enumerable.Empty<Item>())
            .Select(i => (i, score: ItemQueries.MatchScore(i.Prefab, query)))
            .Where(t => t.score > 0)
            .OrderByDescending(t => t.score)
            .ThenByDescending(t => c.HasEquippedItem(t.i) ? 1 : 0)
            .Select(t => t.i)
            .FirstOrDefault();

    public string Unequip(int botId, string query)
    {
        if (Find(botId) is not { } c) return "error: character gone";
        var item = FindCarried(c, query);
        if (item == null) return $"error: you don't carry '{query}'";
        if (!c.HasEquippedItem(item)) return $"ok ({item.Name} is not equipped)";
        if (item.HasTag(Tags.HeavyDivingGear) && GearLock.IsLocked(c) && !PlayerSaidSo(c))
            return "error: your diving gear is locked (danger or keep_gear); turn keep_gear off first if it is really safe";
        if (c.Inventory.TryPutItem(item, c, new List<InvSlotType> { InvSlotType.Any })) return $"ok, {item.Name} put away";
        return $"error: no free inventory slot for {item.Name}; drop or store something first";
    }

    public string Drop(int botId, string query)
    {
        if (Find(botId) is not { } c) return "error: character gone";
        var item = FindCarried(c, query);
        if (item == null) return $"error: you don't carry '{query}'";
        if (item.HasTag(Tags.HeavyDivingGear) && c.HasEquippedItem(item) && GearLock.IsLocked(c) && !PlayerSaidSo(c))
            return "error: your diving gear is locked (danger or keep_gear)";
        item.Drop(c);
        return $"ok, dropped {item.Name}";
    }

    public string MoveItem(int botId, string query, string to)
    {
        if (Find(botId) is not { } c || !Ai(c, out var ai)) return "error: character gone";
        var item = FindCarried(c, query) ?? ItemQueries.Find(c, query).FirstOrDefault();
        if (item == null) return $"error: no '{query}' available";
        if (item.HasTag(Tags.HeavyDivingGear) && c.HasEquippedItem(item) && GearLock.IsLocked(c) && !PlayerSaidSo(c))
            return "error: your diving gear is locked (danger or keep_gear)";

        // a crew member?
        var receiver = FindCrew(to, c, includeSelf: false);
        if (receiver != null) return StartTask(c, new MoveItemTask(c, ai, item, null, null, receiver, receiver.Name));

        // a room?
        var hull = to.Contains("[h") ? ResolveRoom(to) : null;
        if (hull != null) return StartTask(c, new MoveItemTask(c, ai, item, null, hull, null, RoomId(hull)));

        // a container on the sub that accepts the item
        var container = Item.ItemList
            .Where(i => i.Submarine == c.Submarine && !i.Removed && i != item && i.GetRootInventoryOwner() is not Character)
            .Select(i => (i, ic: i.GetComponent<ItemContainer>(), score: ItemQueries.MatchScore(i.Prefab, to)))
            .Where(t => t.ic != null && t.score > 0 && t.ic.Inventory.CanBePut(item))
            .OrderByDescending(t => t.score)
            .ThenBy(t => Vector2.DistanceSquared(t.i.WorldPosition, c.WorldPosition))
            .FirstOrDefault();
        if (container.ic != null)
            return StartTask(c, new MoveItemTask(c, ai, item, container.ic, null, null, $"{container.i.Name} in {(container.i.CurrentHull != null ? RoomId(container.i.CurrentHull) : "?")}"));

        hull = ResolveRoom(to);
        if (hull != null) return StartTask(c, new MoveItemTask(c, ai, item, null, hull, null, RoomId(hull)));
        return $"error: '{to}' is not a crew member, a room id or a container that can hold {item.Name}";
    }

    public string KeepGear(int botId, bool on)
    {
        if (Find(botId) is not { } c) return "error: character gone";
        GearLock.SetManual(c, on);
        if (!on) return GearLock.InDanger(c) ? "ok, unlocked (but it stays locked automatically while there is danger)" : "ok, gear unlocked";
        var hasSuit = c.Inventory?.FindEquippedItemByTag(Tags.HeavyDivingGear) != null;
        return hasSuit ? "ok, diving gear locked on" : "ok, locked; you wear no diving suit yet: equip 'diving suit' to be safe";
    }

    // --------------------------------------------------------------- inspect

    public string Inspect(int botId, string kind, string query)
    {
        if (Find(botId) is not { } c) return "error: character gone";
        switch (kind)
        {
            case "patient":
            {
                var q = query.Trim().ToLowerInvariant();
                if (q is "" or "all" or "everyone" or "crew" or "all crew" or "injured" or "все" or "экипаж")
                {
                    var hurt = Character.CharacterList
                        .Where(x => !x.IsDead && !x.Removed && x.IsHuman && x.TeamID == c.TeamID && ItemQueries.TreatableAfflictions(x).Any())
                        .OrderBy(HealthPercent).Take(6)
                        .Select(x => $"{x.Name} ({HealthPercent(x)}%, {(x.CurrentHull != null ? RoomId(x.CurrentHull) : "outside")}): {ItemQueries.DescribeInjuries(x, 3)}")
                        .ToList();
                    return hurt.Count == 0 ? "nobody in the crew needs treatment"
                        : "hurt: " + string.Join("; ", hurt) + ". Ask inspect(kind='patient', query=<name>) for suitable medicine.";
                }
                var patient = FindCrew(query, c, includeSelf: true);
                return patient == null ? $"error: no crew member '{query}'" : ItemQueries.DescribePatient(c, patient);
            }
            case "items":
            {
                var found = ItemQueries.Find(c, query).Take(30).ToList();
                if (found.Count == 0) return $"no '{query}' on the sub";
                var groups = found.GroupBy(i => i.Prefab).Take(5).Select(g =>
                    $"{ItemQueries.Label(g.Key)}: " + string.Join(", ", g.GroupBy(i => ItemQueries.Where(i, c)).Take(4).Select(w => $"{w.Count()} {w.Key}")));
                return string.Join("; ", groups);
            }
            case "recipe":
            {
                var (fabricator, recipe, error) = Crafting.Find(c, query);
                return error ?? Crafting.RecipeDetails(c, fabricator!, recipe!);
            }
            case "recipes":
                return Crafting.RecipeList(c, query);
            case "salvage":
                return Crafting.Salvage(c, query);
            default:
                return $"error: unknown kind '{kind}'";
        }
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
