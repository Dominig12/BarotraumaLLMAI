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
    internal static bool CanCarryWithoutDropping(Character c, Item item) => InventoryView.CanCarryWithoutDropping(c, item);

    // --------------------------------------------------------------- inventory

    /// <summary>What the bot carries, and what is inside the bags and crates it carries.</summary>
    internal static IEnumerable<Item> Carried(Character c)
    {
        var pending = new Queue<Item>(c.Inventory?.AllItems ?? Enumerable.Empty<Item>());
        var seen = new HashSet<Item>();
        while (pending.Count > 0)
        {
            var item = pending.Dequeue();
            if (item.Removed || !seen.Add(item)) continue;
            yield return item;
            foreach (var inventory in item.GetComponents<ItemContainer>().Select(ic => ic.Inventory).Distinct())
                foreach (var inside in inventory.AllItems) pending.Enqueue(inside);
        }
    }

    static Item? FindCarried(Character c, string query, ItemFilter? filter = null) =>
        Carried(c)
            .Where(i => ItemQueries.Matches(c, i, filter))
            .Select(i => (i, score: ItemQueries.MatchScore(i, query)))
            .Where(t => t.score == 3)
            .OrderByDescending(t => t.score)
            .ThenByDescending(t => c.HasEquippedItem(t.i) ? 1 : 0)
            .Select(t => t.i)
            .FirstOrDefault();

    public string Unequip(int botId, string query, ItemFilter? filter = null)
    {
        if (Find(botId) is not { } c) return "error: character gone";
        var item = FindCarried(c, query, filter);
        if (item == null) return $"error: you don't carry '{query}'";
        if (item.ParentInventory != null && InventoryAccessProblem(c, item.ParentInventory, requireDragAndDrop: true) is { } access)
            return "error: " + access;
        if (!c.HasEquippedItem(item)) return $"ok ({item.Name} is not equipped)";
        if (item.HasTag(Tags.HeavyDivingGear) && GearLock.IsLocked(c) && !PlayerSaidSo(c))
            return "error: your diving gear is locked (danger or keep_gear); turn keep_gear off first if it is really safe";
        if (c.Inventory.TryPutItem(item, c, new List<InvSlotType> { InvSlotType.Any })) return $"ok, {item.Name} put away";
        return $"error: no free inventory slot for {item.Name}; drop or store something first";
    }

    public string Drop(int botId, string query, ItemFilter? filter = null)
    {
        if (Find(botId) is not { } c) return "error: character gone";
        var item = FindCarried(c, query, filter);
        if (item == null)
        {
            // Products can occupy both hands and every output slot. Eject them at the machine without
            // requiring another free character slot first.
            var claimed = ClaimedByOthers(c);
            item = ItemQueries.Find(c, query, i => InMachineOutput(i) && !claimed.Contains(i), selection: filter).FirstOrDefault();
            if (item == null || !Ai(c, out var ai)) return $"error: you don't carry '{query}', and it is not in a machine output";
            return StartTask(c, new DropOutputTask(c, ai, item), evenOverPlayerOrders: true);
        }
        if (item.ParentInventory != null && InventoryAccessProblem(c, item.ParentInventory, requireDragAndDrop: true) is { } access)
            return "error: " + access;
        if (item.HasTag(Tags.HeavyDivingGear) && c.HasEquippedItem(item) && GearLock.IsLocked(c) && !PlayerSaidSo(c))
            return "error: your diving gear is locked (danger or keep_gear)";
        item.Drop(c);
        return item.ParentInventory == null ? $"ok, dropped {item.Name}" : $"error: could not drop {item.Name}";
    }

    public const int MaxMoveStack = 32;

    /// <summary>The item and the rest of its stack: all of that item the bot carries, or all in the same cabinet or machine
    /// output (or lying loose next to it). count &gt; 0 = that many, nearest first.</summary>
    List<Item> StackOf(Character c, Item item, int count, string query, ItemFilter? filter = null, ICollection<Item>? source = null)
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
        list.AddRange(same.Where(i => i != item && (source == null || source.Contains(i)) && !claimed.Contains(i) && ItemQueries.IsAvailableTo(i, c) && ItemQueries.Matches(c, i, filter) && ItemQueries.MatchScore(i, query) > 0).Take(max - 1));
        if (count > list.Count)
        {
            // asked for more than one stack holds: the nearest others of the same item
            list.AddRange(ItemQueries.Find(c, item.Prefab.Identifier.Value, i => i.Prefab == item.Prefab && i.GetRootInventoryOwner() != c, claimed)
                .Where(i => !list.Contains(i) && (source == null || source.Contains(i)) && !claimed.Contains(i) && ItemQueries.Matches(c, i, filter) && ItemQueries.MatchScore(i, query) > 0).Take(max - list.Count));
        }
        return list;
    }

    public string MoveItem(int botId, string query, string to, int count = 0, string destination = "auto", ItemFilter? filter = null, string? from = null)
    {
        if (Find(botId) is not { } c || !Ai(c, out var ai)) return "error: character gone";
        if (count < 0 || count > MaxMoveStack) return $"error: count must be between 0 and {MaxMoveStack}";
        if (count > 1 && InventoryQuery.Parse(query).EntityId.HasValue) return "error: an exact #ID selects one instance; count must be 0 or 1";
        if (destination == "self") return TakeItem(botId, query, count > 0 ? count : 1, from, filter);
        if (destination is not ("auto" or "container" or "room" or "character" or "storage")) return "error: invalid 'destination' type";
        var source = from != null ? ItemsAt(c, from) : null;
        if (from != null && source == null) return $"error: no source '{from}'; use self, an exact #container ID, or a room reference";
        bool InSource(Item i) => source == null || source.Value.Items.Contains(i);

        // what it carries first, but not the diving suit it has to keep on: then a spare one from the sub
        bool Locked(Item i) => i.HasTag(Tags.HeavyDivingGear) && c.HasEquippedItem(i) && GearLock.IsLocked(c) && !PlayerSaidSo(c);
        var claimed = ClaimedByOthers(c);
        var carried = Carried(c).FirstOrDefault(i => InSource(i) && ItemQueries.Matches(c, i, filter) && ItemQueries.MatchScore(i, query) == 3);
        var item = carried != null && !Locked(carried) ? carried
            : ItemQueries.Find(c, query, i => InSource(i) && i.GetRootInventoryOwner() != c && !claimed.Contains(i), selection: filter)
                .OrderByDescending(i => ItemQueries.MatchScore(i, query)).ThenByDescending(i => InMachineOutput(i) ? 1 : 0).FirstOrDefault();
        if (item == null)
            return carried != null ? "error: your diving gear is locked (danger or keep_gear) and there is no spare one aboard" : NotFound(c, query);
        if (item != carried && !ItemQueries.IsKindQuery(query) && ItemQueries.NoneFree(c, query, item) is { } busy) return "error: " + busy;
        FindReplacement replace = from == null ? Replacer(c, query, filter) : (failed, tried) =>
            InventoryTaskRules.SelectReplacement(ItemsAt(c, from)?.Items ?? new List<Item>(), failed, tried, ClaimedByOthers(c),
                i => i.Prefab == failed.Prefab && ItemQueries.IsAvailableTo(i, c) && !Locked(i) && ItemQueries.Matches(c, i, filter) && ItemQueries.MatchScore(i, query) == 3);
        var stack = StackOf(c, item, count, query, filter, source?.Items);

        // "на место" / "away": where it belongs (onto the same items first)
        if (destination == "storage")
        {
            var place = StoragePlanner.Choose(c, item, new HashSet<Item>());
            if (place == null) return $"error: no fitting place for {item.Name} on the sub; name a container or a room";
            if (stack.All(i => i.ParentInventory == place.Container.Inventory))
                return $"ok ({item.Name} x{stack.Count} already in {StoragePlanner.Describe(place.Container)})";
            return StartTask(c, new MoveItemTask(c, ai, stack, place.Container, null, null, StoragePlanner.Describe(place.Container), replace));
        }

        // a crew member?
        var receiver = destination is "auto" or "character" ? FindCrew(to, c, includeSelf: false) : null;
        if (receiver != null) return StartTask(c, new MoveItemTask(c, ai, stack, null, null, receiver, receiver.Name, replace));
        if (destination == "character") return $"error: no exact crew member '{to}'";

        // a room?
        var hull = destination is "auto" or "room" ? ResolveRoom(to) : null;
        if (hull != null) return StartTask(c, new MoveItemTask(c, ai, stack, null, hull, null, RoomId(hull), replace));
        if (destination == "room") return $"error: no exact room '{to}'";

        // a container on the sub that accepts the item
        var container = Item.ItemList
            .Where(i => i.Submarine == c.Submarine && !i.Removed && i != item && i.GetRootInventoryOwner() is not Character)
            .SelectMany(i => i.GetComponents<ItemContainer>().Select(ic => (i, ic, score: ItemQueries.MatchScore(i, to))))
            .Where(t => t.score == 3 && t.ic.Inventory.CanBePut(item) && !ItemQueries.IsIgnored(t.i) && InventoryAccessProblem(c, t.ic.Inventory, requireRange: false) == null)
            .OrderByDescending(t => t.score)
            // of several matching cabinets, the one that already holds such items (stacks, saves space)
            .ThenByDescending(t => t.ic!.Inventory.AllItems.Any(x => x.Prefab == item.Prefab) ? 1 : 0)
            .ThenBy(t => Vector2.DistanceSquared(t.i.WorldPosition, c.WorldPosition))
            .FirstOrDefault();
        if (container.ic != null)
            return StartTask(c, new MoveItemTask(c, ai, stack, container.ic, null, null, $"{container.i.Name} in {(container.i.CurrentHull != null ? RoomId(container.i.CurrentHull) : "?")}", replace));

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

    /// <summary>Load a carried or world container: fuel into a tool, C4 into a shell, ammo into a loader.</summary>
    public string PutIn(int botId, string query, string into, ItemFilter? filter = null)
    {
        if (Find(botId) is not { } c || !Ai(c, out var ai)) return "error: character gone";
        var claimed = ClaimedByOthers(c);
        var holder = Item.ItemList.Where(i => !i.Removed && !ItemQueries.IsIgnored(i) && !claimed.Contains(i)
                    && (i.GetRootInventoryOwner() == c || i.Submarine == c.Submarine && i.GetRootInventoryOwner() is not Character)
                    && i.GetComponents<ItemContainer>().Any(ic => InventoryAccessProblem(c, ic.Inventory, requireRange: false) == null) && ItemQueries.MatchScore(i, into) == 3)
            .OrderByDescending(i => ItemQueries.MatchScore(i, into))
            .ThenBy(i => i.GetComponents<ItemContainer>().Any(ic => ic.Inventory.AllItems.Any(x => ItemQueries.MatchScore(x, query) == 3)) ? 1 : 0)
            .ThenByDescending(i => i.GetRootInventoryOwner() == c ? 1 : 0)
            .ThenBy(i => Vector2.DistanceSquared(i.WorldPosition, c.WorldPosition)).FirstOrDefault();
        if (holder == null) return $"error: no available container '{into}' aboard or in your inventory";
        var compartments = holder.GetComponents<ItemContainer>().Where(ic => InventoryAccessProblem(c, ic.Inventory, requireRange: false) == null).ToList();
        // Loaded payloads are not spare supplies, even when the bot carries the other shell or tool.
        // Claims are strict here: two loading tasks cannot reserve the same C4 block.
        var item = ItemQueries.Find(c, query, i => i != holder && i.Container != holder && !claimed.Contains(i) && compartments.Any(ic => ic.CanBeContained(i))
                    && !InventoryTransferRules.WouldCreateCycle(i, holder, x => x.Container)
                    && (i.Container == null || IsStorage(i.Container) || InMachineOutput(i)), claimed, selection: filter).FirstOrDefault();
        var ic = item != null ? compartments.Where(ic => ic.CanBeContained(item)).OrderByDescending(ic => ic.Inventory.CanBePut(item)).First()
            : compartments.FirstOrDefault(ic => ic.Inventory.AllItems.Any(i => ItemQueries.MatchScore(i, query) == 3)) ?? compartments.First();
        var loaded = ic.Inventory.AllItems.FirstOrDefault(i => ItemQueries.MatchScore(i, query) == 3 && !ItemRules.IsSpent(i.MaxCondition > 0 ? i.Condition / i.MaxCondition : 1));
        if (loaded != null && (item == null || !ic.Inventory.CanBePut(item)
            && loaded.Condition / Math.Max(1, loaded.MaxCondition) >= item.Condition / Math.Max(1, item.MaxCondition)))
            return $"ok ({ItemQueries.InstanceLabel(holder)} already contains {query})";
        if (item == null)
        {
            var fits = InventoryView.BestRefill(c, holder, ic, null, claimed);
            return $"error: no '{query}' that fits into {holder.Name}" + (fits != null ? $"; it takes e.g. {fits.Name}" : "");
        }
        if (item.GetRootInventoryOwner() == c && holder.GetRootInventoryOwner() == c)
        {
            var err = InventoryView.PutInto(c, item, ic, out var removed);
            return err != null ? "error: " + err
                : $"ok, {item.Name} is in {holder.Name}" + (removed != null ? $" (took out {removed.Name}{InventoryView.Condition(removed)})" : "");
        }
        return StartTask(c, new PutInTask(c, ai, new List<(Item, ItemContainer)> { (item, ic) }, $"put {item.Name} into {holder.Name}"), evenOverPlayerOrders: true);
    }

    /// <summary>Refill what the bot carries (or one item): welding fuel, oxygen tanks, batteries, ammo.</summary>
    public string Refill(int botId, string? query, ItemFilter? filter = null)
    {
        if (Find(botId) is not { } c || !Ai(c, out var ai)) return "error: character gone";
        Item? only = null;
        if (!string.IsNullOrWhiteSpace(query))
        {
            only = Carried(c).Where(i => i.GetComponent<ItemContainer>() != null && ItemQueries.Matches(c, i, filter) && ItemQueries.MatchScore(i, query!) == 3).FirstOrDefault();
            if (only == null) return $"error: no carried refillable container matches '{query}'; item selects the holder, not its fuel or ammo";
        }
        var needs = InventoryView.NeedRefill(c, only).Where(n => ItemQueries.Matches(c, n.Holder, filter)).ToList();
        if (needs.Count == 0) return only != null ? $"ok ({only.Name} does not need anything)" : "ok (nothing selected needs refilling)";
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
        return r + $"; {string.Join(", ", pairs.Select(p => $"{p.Item1.Name} -> {p.Item2.Item.Name}"))}" + (missing.Count > 0 ? $"; nothing for {string.Join(", ", missing)}" : "");
    }

    /// <summary>Put away what the bot carries but does not wear or hold (except 'keep').</summary>
    public string TidyInventory(int botId, string? keep)
    {
        if (Find(botId) is not { } c || !Ai(c, out var ai)) return "error: character gone";
        var keepWords = (keep ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(k => k.Trim()).Where(k => k.Length > 0).ToList();
        var items = (c.Inventory?.AllItems ?? Enumerable.Empty<Item>())
            .Where(i => !c.HasEquippedItem(i) && !InventoryView.IsBoring(i) && !i.HasTag("mobileradio".ToIdentifier())
                        && !keepWords.Any(k => ItemQueries.MatchScore(i, k) > 0))
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
            var s = $"{slot}: {ItemQueries.InstanceLabel(i)}{InventoryView.Condition(i)}";
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
