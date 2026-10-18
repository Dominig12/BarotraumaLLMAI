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

/// <summary>Vanilla orders: issuing, protecting players' orders, priorities.</summary>
public sealed partial class GameAdapter
{
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

    /// <summary>
    /// A go_to_room step was only the way to the next step: the bot's own "wait there" order goes, so it does not walk
    /// back and stand in that room once the next task is done. Orders from players stay.
    /// </summary>
    public string EndTravel(int botId)
    {
        if (Find(botId) is not { } c) return "error: character gone";
        var waits = c.CurrentOrders?.Where(o => o != null && o.Identifier == "wait" && IsOwn(c, o) && !IsPlayerOrder(c, o)).ToList() ?? new List<Order>();
        foreach (var o in waits) DropOrder(c, o);
        return waits.Count > 0 ? "ok, travel order dropped" : "ok";
    }

    public string ComeTo(int botId, string targetName)
    {
        if (Find(botId) is not { } c || !Ai(c, out var ai)) return "error: character gone";
        var target = FindCrew(targetName, c, includeSelf: false);
        if (target == null) return $"error: no crew member '{targetName}'";
        return StartTask(c, new ComeToTask(c, ai, target, Settings.Global.ComeToWaitSeconds));
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
            // a searchlight is a turret too: not a weapon
            case "weapons": orderId = "operateweapons"; match = i => i.GetComponent<Turret>() != null && !IsSearchlight(i); break;
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
            // "steer" alone means sailing on, not holding position (the first option): captains said "heading to the
            // outpost" while the sub stood still
            var go = "navigatetodestination".ToIdentifier();
            opt = role == "navigation" && prefab.Options.Contains(go) ? go : prefab.Options.First();
        }

        Order order;
        string station = "";
        if (match == null)
        {
            order = new Order(prefab, opt, targetEntity: null, targetItem: null, orderGiver: c);
        }
        else
        {
            var stations = Item.ItemList
                .Where(i => i.Submarine == c.Submarine && !i.Removed && match(i))
                .OrderBy(i => Vector2.DistanceSquared(i.WorldPosition, c.WorldPosition))
                .ToList();
            if (stations.Count == 0) return $"error: no {role} station on this submarine";
            // The game dismisses the order of whoever was told to operate the same station before, so five bots told
            // "man the guns" would keep pushing each other off one turret: take a free station, keep one already ours.
            var mine = stations.FirstOrDefault(i => c.CurrentOrders?.Any(o => o != null && o.Identifier == orderId && o.TargetEntity == i) == true);
            var item = mine ?? stations.FirstOrDefault(i => OperatorOf(c, orderId, i) == null);
            if (item == null)
            {
                var busy = stations.Select(i => OperatorOf(c, orderId, i)).Where(x => x != null).Select(x => x!.Name).Distinct().ToList();
                if (!HasAuthority(c))
                    return stations.Count == 1
                        ? $"error: {busy.FirstOrDefault()} already operates the {role}; do another job (or ask them with message_crew)"
                        : $"error: all {stations.Count} {role} stations are taken ({string.Join(", ", busy)}); do another job";
                item = stations[0];   // a player told this bot to: it takes over
                station = $" (taking over from {OperatorOf(c, orderId, item)?.Name})";
            }
            if (stations.Count > 1) station = $": {item.Name}{(item.CurrentHull != null ? " in " + RoomId(item.CurrentHull) : "")}" + station;
            order = new Order(prefab, opt, item, prefab.GetTargetItemComponent(item), orderGiver: c);
        }
        var r = Issue(c, order, $"ok, operating {role}{(opt.IsEmpty ? "" : " (" + opt.Value + ")")}{station}", priority);
        if (role == "reactor" && r.StartsWith("ok") && c.GetSkillLevel("electrical".ToIdentifier()) < 30)
            r += ". Note: with low electrical skill you will not refuel it; use refuel_reactor for that";
        return r;
    }

    static bool IsSearchlight(Item i) =>
        i.Prefab.Identifier.Value.Contains("searchlight", StringComparison.OrdinalIgnoreCase) || i.HasTag("searchlight".ToIdentifier())
        || (i.Prefab.Name.Value.Contains("searchlight", StringComparison.OrdinalIgnoreCase) || i.Prefab.Name.Value.Contains("прожектор", StringComparison.OrdinalIgnoreCase));

    /// <summary>Orders a bot must not give itself through give_order: the mod's own and the item marks.</summary>
    static bool IsSpecialOrder(OrderPrefab p) =>
        p.Identifier == ControlOrder.Id || p.Identifier == ContextMarks.MarkId || p.Identifier == ContextMarks.UnmarkId
        || p.IsIgnoreOrder || p.IsDeconstructOrder || p.IsDismissal;

    static bool NeedsTarget(OrderPrefab p) => p.ItemComponentType != null || p.TargetItems.Any() || p.RequireItems.Any();

    /// <summary>
    /// Any order of the game by its identifier. Orders with their own tool go there (wait, follow, operate stations);
    /// the rest are built like the command menu builds them: the option checked, the target device the nearest free
    /// one (or the one named), reports sent to the whole crew.
    /// </summary>
    public string GiveOrder(int botId, string orderId, string? option, string? target, int priority = Tools.TopPriority)
    {
        if (Find(botId) is not { } c) return "error: character gone";
        var id = Tools.OrderAliases.TryGetValue(orderId, out var alias) ? alias : orderId.Replace("_", "").Replace(" ", "");
        if (!OrderPrefab.Prefabs.TryGet(id.ToIdentifier(), out var prefab) || prefab == null)
        {
            var close = OrderPrefab.Prefabs.Where(p => !IsSpecialOrder(p) && (p.Identifier.Value.Contains(id) || id.Contains(p.Identifier.Value)))
                .Select(p => p.Identifier.Value).Take(5).ToList();
            return $"error: no order '{orderId}' in this game" + (close.Count > 0 ? $"; did you mean {string.Join(", ", close)}?" : "; inspect(kind='orders') lists them");
        }
        if (IsSpecialOrder(prefab)) return $"error: '{prefab.Identifier}' is not an order you give yourself" + (prefab.IsDismissal ? "; use return_to_default" : "");
        switch (prefab.Identifier.Value)
        {
            case "wait":
                return target != null ? GoToRoom(botId, target, priority) : SimpleOrder(botId, "wait", priority);
            case "follow":
                return target != null ? Follow(botId, target, priority) : "error: follow needs a target (a crew member)";
            case "operatereactor": return Operate(botId, "reactor", option, priority);
            case "steer" when target == null: return Operate(botId, "navigation", option, priority);
            case "operateweapons" when target == null && option == null: return Operate(botId, "weapons", option, priority);
        }

        Identifier opt = Identifier.Empty;
        if (!string.IsNullOrEmpty(option))
        {
            opt = prefab.AllOptions.FirstOrDefault(o => o.Value.Equals(option, StringComparison.OrdinalIgnoreCase)
                                                       || prefab.GetOptionName(o).Value.Equals(option, StringComparison.OrdinalIgnoreCase));
            if (opt.IsEmpty) return $"error: option '{option}' not valid for {prefab.Identifier}; valid: {string.Join(", ", prefab.Options.Select(x => x.Value))}";
        }
        else if (prefab.Options.Length > 0) opt = prefab.Options.First();
        var name = prefab.Identifier.Value + (opt.IsEmpty ? "" : ":" + opt.Value);

        Entity? targetEntity = null;
        if (NeedsTarget(prefab))
        {
            var items = c.Submarine == null ? new List<Item>() : prefab.GetMatchingItems(c.Submarine, true, c.TeamID, c, opt);
            if (items.Count == 0 && prefab.MustSetTarget) return $"error: nothing aboard to {prefab.Identifier} ({prefab.Name.Value})";
            if (items.Count > 0)
            {
                IEnumerable<Item> pool = items;
                if (target != null)
                {
                    var hull = ResolveRoom(target);
                    var named = items.Where(i => hull != null ? i.CurrentHull == hull
                        : ItemQueries.MatchScore(i, target) == 3).ToList();
                    if (named.Count == 0) return $"error: no '{target}' to {prefab.Identifier}; targets aboard: {string.Join(", ", items.Take(6).Select(i => $"{i.Name}{(i.CurrentHull != null ? " in " + RoomId(i.CurrentHull) : "")}"))}";
                    pool = named;
                }
                // a station another crew member already operates stays theirs (the game would take their order away)
                var ordered = pool.OrderBy(i => prefab.Category == OrderCategory.Operate && OperatorOf(c, prefab.Identifier.Value, i) != null ? 1 : 0)
                    .ThenBy(i => Vector2.DistanceSquared(i.WorldPosition, c.WorldPosition)).ToList();
                var item = ordered[0];
                if (prefab.Category == OrderCategory.Operate && OperatorOf(c, prefab.Identifier.Value, item) is { } other && !HasAuthority(c))
                    return $"error: {other.Name} already does {prefab.Identifier} there; do another job";
                targetEntity = item;
                name += $" ({item.Name}{(item.CurrentHull != null ? " in " + RoomId(item.CurrentHull) : "")})";
            }
        }

        if (prefab.TargetAllCharacters)
        {
            // a report ("fire here", "breach here"): to the whole crew, for the room the bot is in
            var hull = (targetEntity as Item)?.CurrentHull ?? c.CurrentHull;
            if (hull == null && targetEntity == null) return "error: you are not inside the sub";
            var report = new Order(prefab, opt, targetEntity ?? hull, null, orderGiver: c);
            GameMain.GameSession?.CrewManager?.AddOrder(report, prefab.FadeOutTime);
            if (report.IsReport) HumanAIController.ReportProblem(c, report);
            try { GameMain.Server?.SendOrderChatMessage(new OrderChatMessage(report, null, c)); } catch { /* the crew still knows */ }
            return $"ok, reported to the crew: {prefab.Name.Value}" + (hull != null ? $" in {RoomId(hull)}" : "");
        }

        var item0 = targetEntity as Item;
        var order = new Order(prefab, opt, targetEntity, item0 != null ? prefab.GetTargetItemComponent(item0) : null, orderGiver: c);
        return Issue(c, order, $"ok, {name}", priority);
    }

    /// <summary>Every order of the game, for inspect(kind='orders').</summary>
    string DescribeOrders(Character c, string filter)
    {
        var list = OrderPrefab.Prefabs.Where(p => !IsSpecialOrder(p) && p.Identifier != ControlOrder.Id)
            .Where(p => string.IsNullOrWhiteSpace(filter) || p.Identifier.Value.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)
                        || p.Name.Value.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.TargetAllCharacters ? 1 : 0).ThenBy(p => p.Category?.ToString() ?? "~")
            .Select(p =>
            {
                var s = $"{p.Identifier} ({p.Name.Value})";
                if (p.Options.Length > 0) s += " options " + string.Join("/", p.Options.Select(o => o.Value));
                if (NeedsTarget(p) && c.Submarine != null) s += $", {p.GetMatchingItems(c.Submarine, true, c.TeamID, c).Count} targets aboard";
                if (p.TargetAllCharacters) s += ", report to all";
                return s;
            });
        return "orders (give_order(order=<id>, option, target)): " + string.Join("; ", list);
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

    /// <summary>Error text if the bot must not start something that would push a player's order aside, else null.
    /// A task (replacement = null) only pauses the orders while it runs (a forced objective; the orders stay and go on
    /// after it), so it is held back only by an order to be somewhere: follow, wait, go to. A background job a player
    /// gave (weld, repair, deconstruct...) must not keep the bot from handing a crewmate an oxygen tank for the whole round.</summary>
    string? PlayerOrderBlock(Character c, Order? replacement)
    {
        if (HasAuthority(c)) return null;
        var player = PlayerOrders(c);
        if (replacement == null) player = player.Where(p => p.Category == OrderCategory.Movement).ToList();
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
            // the same kind of order a player already gave (repairsystems for repair_systems, another fixleaks): it is
            // being done; issuing it again would only replace the player's one
            if (player.FirstOrDefault(p => p.Identifier == order.Identifier) is { } same)
                return $"ok (already doing that: '{Name(same)}' ordered by player {same.OrderGiver.Name}; its priority stays as the player set it)";
            if (player.Count > 0)
            {
                var clash = order.Category == OrderCategory.Movement && order.AutoDismiss ? player.FirstOrDefault(p => p.Category == OrderCategory.Movement) : null;
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

    /// <summary>Someone else who has an order to operate that station, or a player using it right now.</summary>
    static Character? OperatorOf(Character c, string orderId, Item station) =>
        Character.CharacterList.FirstOrDefault(o => o != c && !o.IsDead && !o.Removed && o.TeamID == c.TeamID
            && ((o.IsBot && o.CurrentOrders?.Any(x => x != null && x.Identifier == orderId && x.TargetEntity == station) == true)
                || (o.IsPlayer && (o.SelectedItem == station || o.SelectedSecondaryItem == station))));

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
        // dropping what is not there is no failure: the plan goes on
        if (orders.Count == 0) return priority <= 0 ? "ok (you have no orders to drop)" : "error: you have no orders";

        var q = query.Trim().ToLowerInvariant();
        q = q.Split(' ', '[', '(')[0];
        string? opt = null;
        int sep = q.IndexOfAny(new[] { ':', '.' });
        if (sep > 0) { opt = q.Substring(sep + 1); q = q.Substring(0, sep); }
        var id = Tools.OrderAliases.TryGetValue(q, out var alias) ? alias : q;
        var match = orders.FirstOrDefault(o => o.Identifier == id && (opt == null || o.Option == opt))
                 ?? orders.FirstOrDefault(o => o.Identifier == id);
        if (match == null && priority <= 0)
            return $"ok (no order '{query}' to drop; your orders: {string.Join(", ", orders.Select(o => $"{Name(o)} [p{o.ManualPriority}]"))})";
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
            ControlOrder.Internal = true;
            try { c.SetOrder(order, isNewOrder: isNewOrder, speak: false, force: true); }
            finally { ControlOrder.Internal = false; }
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
}
