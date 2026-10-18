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

/// <summary>What the bot carries: equip, drop, move, put in, refill, tidy.</summary>
public sealed partial class GameAdapter
{
    // --------------------------------------------------------------- inventory

    /// <summary>What the bot carries, and what is inside the bags and crates it carries.</summary>
    static IEnumerable<Item> Carried(Character c) =>
        (c.Inventory?.AllItems ?? Enumerable.Empty<Item>()).SelectMany(i => new[] { i }.Concat(i.OwnInventory?.AllItems ?? Enumerable.Empty<Item>()));

    static Item? FindCarried(Character c, string query) =>
        Carried(c)
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

    public const int MaxMoveStack = 32;

    /// <summary>The item and the rest of its stack: all of that item the bot carries, or all in the same cabinet or machine
    /// output (or lying loose next to it). count &gt; 0 = that many, nearest first.</summary>
    List<Item> StackOf(Character c, Item item, int count)
    {
        int max = count > 0 ? Math.Min(count, MaxMoveStack) : MaxMoveStack;
        IEnumerable<Item> same;
        if (item.GetRootInventoryOwner() == c)
            same = Carried(c).Where(i => i.Prefab == item.Prefab && !c.HasEquippedItem(i));
        else if (item.ParentInventory != null)
            same = item.ParentInventory.AllItems.Where(i => i.Prefab == item.Prefab);
        else
            same = Item.ItemList.Where(i => !i.Removed && i.Prefab == item.Prefab && i.ParentInventory == null && i.CurrentHull == item.CurrentHull
                                            && Vector2.Distance(i.WorldPosition, item.WorldPosition) < 200f);
        var claimed = ClaimedByOthers(c);
        var list = new List<Item> { item };
        list.AddRange(same.Where(i => i != item && !claimed.Contains(i) && ItemQueries.IsAvailableTo(i, c)).Take(max - 1));
        if (count > list.Count)
        {
            // asked for more than one stack holds: the nearest others of the same item
            list.AddRange(ItemQueries.Find(c, item.Prefab.Identifier.Value, i => i.Prefab == item.Prefab && i.GetRootInventoryOwner() != c, claimed)
                .Where(i => !list.Contains(i)).Take(count - list.Count));
        }
        return list;
    }

    public string MoveItem(int botId, string query, string to, int count = 0)
    {
        if (Find(botId) is not { } c || !Ai(c, out var ai)) return "error: character gone";
        // "to me" / "себе" / its own name: pick it up into the inventory
        if (ItemWords.IsSelf(to, c.Name)) return TakeItem(botId, query, count > 0 ? count : 1);

        // what it carries first, but not the diving suit it has to keep on: then a spare one from the sub
        bool Locked(Item i) => i.HasTag(Tags.HeavyDivingGear) && c.HasEquippedItem(i) && GearLock.IsLocked(c) && !PlayerSaidSo(c);
        var carried = FindCarried(c, query);
        var item = carried != null && !Locked(carried) ? carried
            : ItemQueries.Find(c, query, i => i.GetRootInventoryOwner() != c, ClaimedByOthers(c)).FirstOrDefault();
        if (item == null)
            return carried != null ? "error: your diving gear is locked (danger or keep_gear) and there is no spare one aboard" : NotFound(c, query);
        if (item != carried && !ItemQueries.IsKindQuery(query) && ItemQueries.NoneFree(c, query, item) is { } busy) return "error: " + busy;
        var replace = Replacer(c);
        var stack = StackOf(c, item, count);

        // "на место" / "away": where it belongs (onto the same items first)
        if (ItemWords.IsItsPlace(to))
        {
            var place = StoragePlanner.Choose(c, item, new HashSet<Item>());
            if (place == null) return $"error: no fitting place for {item.Name} on the sub; name a container or a room";
            return StartTask(c, new MoveItemTask(c, ai, stack, place.Container, null, null, StoragePlanner.Describe(place.Container), replace));
        }

        // "шкаф у фабрикатора": the cabinet next to that machine
        if (NearAnchor(c, to, item) is { } near)
            return StartTask(c, new MoveItemTask(c, ai, stack, near.Container, null, null, near.Where, replace));

        // a crew member?
        var receiver = FindCrew(to, c, includeSelf: false);
        if (receiver != null) return StartTask(c, new MoveItemTask(c, ai, stack, null, null, receiver, receiver.Name, replace));

        // a room?
        var hull = IsRoomRef(to) ? ResolveRoom(to) : null;
        if (hull != null) return StartTask(c, new MoveItemTask(c, ai, stack, null, hull, null, RoomId(hull), replace));

        // a container on the sub that accepts the item
        var container = Item.ItemList
            .Where(i => i.Submarine == c.Submarine && !i.Removed && i != item && i.GetRootInventoryOwner() is not Character)
            .Select(i => (i, ic: i.GetComponent<ItemContainer>(), score: ItemQueries.MatchScore(i.Prefab, to)))
            .Where(t => t.ic != null && t.score > 0 && t.ic.Inventory.CanBePut(item) && !ItemQueries.IsIgnored(t.i))
            .OrderByDescending(t => t.score)
            // of several matching cabinets, the one that already holds such items (stacks, saves space)
            .ThenByDescending(t => t.ic!.Inventory.AllItems.Any(x => x.Prefab == item.Prefab) ? 1 : 0)
            .ThenBy(t => Vector2.DistanceSquared(t.i.WorldPosition, c.WorldPosition))
            .FirstOrDefault();
        if (container.ic != null)
            return StartTask(c, new MoveItemTask(c, ai, stack, container.ic, null, null, $"{container.i.Name} in {(container.i.CurrentHull != null ? RoomId(container.i.CurrentHull) : "?")}", replace));

        hull = ResolveRoom(to);
        if (hull != null) return StartTask(c, new MoveItemTask(c, ai, stack, null, hull, null, RoomId(hull), replace));
        return $"error: '{to}' is not a crew member, a room id or a container that can hold {item.Name} (to take it yourself: take_item)";
    }

    public string KeepGear(int botId, bool on)
    {
        if (Find(botId) is not { } c) return "error: character gone";
        GearLock.SetManual(c, on);
        if (!on) return GearLock.InDanger(c) ? "ok, unlocked (but it stays locked automatically while there is danger)" : "ok, gear unlocked";
        var hasSuit = c.Inventory?.FindEquippedItemByTag(Tags.HeavyDivingGear) != null;
        return hasSuit ? "ok, diving gear locked on" : "ok, locked; you wear no diving suit yet: equip 'diving suit' to be safe";
    }

    /// <summary>Load an item into something the bot carries: fuel into the welding tool, a tank into the suit, ammo into a gun.</summary>
    public string PutIn(int botId, string query, string into)
    {
        if (Find(botId) is not { } c || !Ai(c, out var ai)) return "error: character gone";
        var holder = Carried(c).Where(i => i.GetComponent<ItemContainer>() != null && ItemQueries.MatchScore(i.Prefab, into) > 0)
            .OrderByDescending(i => ItemQueries.MatchScore(i.Prefab, into)).FirstOrDefault();
        if (holder == null) return $"error: you do not carry '{into}' (put_in loads things into what you carry; for cabinets use move_item)";
        var ic = holder.GetComponent<ItemContainer>()!;
        var item = Carried(c).Where(i => i != holder && i.Container != holder && ic.CanBeContained(i) && ItemQueries.MatchScore(i.Prefab, query) > 0).FirstOrDefault()
                   ?? ItemQueries.Find(c, query, i => i != holder && ic.CanBeContained(i) && (i.Container == null || IsStorage(i.Container)), ClaimedByOthers(c)).FirstOrDefault();
        if (item == null)
        {
            var fits = InventoryView.BestRefill(c, holder, ic, null, new HashSet<Item>());
            return $"error: no '{query}' that fits into {holder.Name}" + (fits != null ? $"; it takes e.g. {fits.Name}" : "");
        }
        if (item.GetRootInventoryOwner() == c)
        {
            var err = InventoryView.PutInto(c, item, ic, out var removed);
            return err != null ? "error: " + err
                : $"ok, {item.Name} is in {holder.Name}" + (removed != null ? $" (took out {removed.Name}{InventoryView.Condition(removed)})" : "");
        }
        return StartTask(c, new PutInTask(c, ai, new List<(Item, ItemContainer)> { (item, ic) }, $"put {item.Name} into {holder.Name}"), evenOverPlayerOrders: true);
    }

    /// <summary>Refill what the bot carries (or one item): welding fuel, oxygen tanks, batteries, ammo.</summary>
    public string Refill(int botId, string? query)
    {
        if (Find(botId) is not { } c || !Ai(c, out var ai)) return "error: character gone";
        Item? only = null;
        string note = "";
        if (!string.IsNullOrWhiteSpace(query))
        {
            only = Carried(c).Where(i => i.GetComponent<ItemContainer>() != null && ItemQueries.MatchScore(i.Prefab, query!) > 0).FirstOrDefault();
            // models name the refill itself ("oxygen tank") or a suit by another word: refill whatever needs it
            if (only == null) note = $" ('{query}' is not something you carry that takes a refill: refilling what needs one)";
        }
        var needs = InventoryView.NeedRefill(c, only);
        if (needs.Count == 0) return only != null ? $"ok ({only.Name} does not need anything)" : "ok (nothing you carry needs refilling)" + note;
        var pairs = new List<(Item, ItemContainer)>();
        var used = new HashSet<Item>(ClaimedByOthers(c));
        var missing = new List<string>();
        foreach (var (holder, ic, old) in needs)
        {
            var fill = InventoryView.BestRefill(c, holder, ic, old, used);
            if (fill == null) { missing.Add(holder.Name); continue; }
            used.Add(fill);
            pairs.Add((fill, ic));
        }
        if (pairs.Count == 0) return $"error: nothing aboard to refill {string.Join(", ", missing)}";
        var label = "refill " + string.Join(", ", pairs.Select(p => p.Item2.Item.Name).Distinct());
        var r = StartTask(c, new PutInTask(c, ai, pairs, label), evenOverPlayerOrders: true);
        return r + $"; {string.Join(", ", pairs.Select(p => $"{p.Item1.Name} -> {p.Item2.Item.Name}"))}" + (missing.Count > 0 ? $"; nothing for {string.Join(", ", missing)}" : "") + note;
    }

    /// <summary>Put away what the bot carries but does not wear or hold (except 'keep').</summary>
    public string TidyInventory(int botId, string? keep)
    {
        if (Find(botId) is not { } c || !Ai(c, out var ai)) return "error: character gone";
        var keepWords = (keep ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(k => k.Trim()).Where(k => k.Length > 0).ToList();
        var items = (c.Inventory?.AllItems ?? Enumerable.Empty<Item>())
            .Where(i => !c.HasEquippedItem(i) && !InventoryView.IsBoring(i) && !i.HasTag("mobileradio".ToIdentifier())
                        && !keepWords.Any(k => ItemQueries.MatchScore(i.Prefab, k) > 0))
            .Distinct().ToList();
        if (items.Count == 0) return "ok (you carry nothing to put away)";
        var plan = items.GroupBy(i => i.Prefab).Select(g => (g.Key, n: g.Count(), to: StoragePlanner.Choose(c, g.First(), new HashSet<Item>()))).ToList();
        if (plan.All(p => p.to == null)) return "error: no fitting place on the sub for what you carry; keep it or use move_item";
        return StartTask(c, new TidyInventoryTask(c, ai, items), evenOverPlayerOrders: true)
               + "; plan: " + string.Join("; ", plan.Take(6).Select(p => $"{p.Key.Name.Value} x{p.n} -> {(p.to != null ? StoragePlanner.Describe(p.to.Container) : "keep")}"));
    }

    /// <summary>inspect(kind='inventory'): every slot, contents, charge and what needs refilling.</summary>
    string DescribeInventory(Character c)
    {
        var inv = c.Inventory;
        if (inv == null) return "no inventory";
        var lines = new List<string>();
        foreach (var i in inv.AllItems.Distinct())
        {
            var slot = InventoryView.SlotName(c, i) ?? "carried";
            var inside = i.OwnInventory?.AllItems.ToList();
            var s = $"{slot}: {ItemQueries.Label(i)}{InventoryView.Condition(i)}";
            if (inside is { Count: > 0 })
                s += " [in it: " + string.Join(", ", inside.GroupBy(x => x.Prefab).Select(g => $"{g.Key.Name.Value} x{g.Count()}{InventoryView.Condition(g.First())}")) + "]";
            else if (i.GetComponent<ItemContainer>() is { Capacity: <= 4 }) s += " [empty]";
            lines.Add(s);
        }
        var needs = InventoryView.NeedRefill(c);
        var tail = needs.Count > 0 ? $". Needs refill: {string.Join(", ", needs.Select(n => n.Holder.Name))} (refill())" : "";
        return string.Join("; ", lines) + ". " + InventoryView.Space(c) + tail;
    }
}
