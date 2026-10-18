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

/// <summary>Multi-step tasks: taking, unpacking, crafting, deconstructing, treating.</summary>
public sealed partial class GameAdapter
{
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
            string? result;
            try { result = task.Tick(); }
            catch (Exception e)
            {
                // Tick catches the task's own faults; this is a fault around it (a character torn down mid-tick)
                Log.ErrorThrottled("task:" + task.GetType().Name, $"task {task.GetType().Name} broke: {e}");
                result = "failed: " + e.Message;
            }
            if (result == null) continue;
            tasks.Remove(id);
            reports.Add(new GameReport(id, $"task '{NameOf(task)}' {result}"));
        }
    }

    /// <summary>The task's name for its report; a name that cannot be built (a removed item) must not lose the report.</summary>
    static string NameOf(BotTask task)
    {
        try { return task.Name; }
        catch { return task.GetType().Name.Replace("Task", "").ToLowerInvariant(); }
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
        menuOrders.Clear();
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
        if (!quiet) reports.Add(new GameReport(c.ID, $"task '{NameOf(t)}' cancelled"));
    }

    /// <param name="evenOverPlayerOrders">Short self-care tasks (putting on gear) are fine while carrying out a player's order.</param>
    string StartTask(Character c, BotTask task, bool evenOverPlayerOrders = false)
    {
        if (!evenOverPlayerOrders && PlayerOrderBlock(c, null) is { } blocked) return blocked;
        CancelTask(c, quiet: true);
        tasks[c.ID] = task;
        return $"ok, started: {NameOf(task)} (result will be reported)";
    }

    static bool Ai(Character c, out HumanAIController ai)
    {
        ai = (c.AIController as HumanAIController)!;
        return ai != null;
    }

    /// <summary>Items other bots' tasks are going for right now.</summary>
    HashSet<Item> ClaimedByOthers(Character c) =>
        tasks.Where(kv => kv.Key != c.ID).SelectMany(kv => kv.Value.Claimed).ToHashSet();

    FindReplacement Replacer(Character c, string? query = null) => (failed, tried) => ItemQueries.Replacement(c, failed, tried, ClaimedByOthers(c), query);

    string NotFound(Character c, string query)
    {
        var kinds = ItemWords.Groups(query);
        return kinds.Count > 0
            ? $"error: no {string.Join("/", kinds).ToLowerInvariant()} items available on the sub (everything is carried by someone or used up)"
            : $"error: no '{query}' available; try inspect(kind='items', query='{query}') or a plainer word (e.g. 'suit', 'medical', 'ammo', 'tools')";
    }

    public string Equip(int botId, string query)
    {
        if (Find(botId) is not { } c || !Ai(c, out var ai)) return "error: character gone";
        // "diving gear" to put on = the suit (a mask only when no suit is left)
        var kinds = ItemWords.Groups(query);
        Func<Item, bool>? filter = kinds.Count == 1 && kinds[0] == ItemGroup.DivingGear && ItemQueries.Find(c, "diving suit").Any()
            ? i => i.HasTag(Tags.HeavyDivingGear) : null;
        // already wearing something that fits ("diving suit" while in a combat suit): keep it, do not swap
        var worn = (c.Inventory?.AllItems ?? Enumerable.Empty<Item>())
            .FirstOrDefault(i => c.HasEquippedItem(i) && (filter == null || filter(i)) && ItemQueries.MatchScore(i, query) > 0);
        if (worn != null) return $"ok ({worn.Name} is already equipped)";
        var item = ItemQueries.Find(c, query, filter, ClaimedByOthers(c)).FirstOrDefault();
        if (item == null) return NotFound(c, query);
        if (c.HasEquippedItem(item)) return $"ok ({item.Name} is already equipped)";
        return StartTask(c, new EquipTask(c, ai, item, Replacer(c, query)), evenOverPlayerOrders: true);
    }

    public const int MaxTake = 8;

    static bool IsAll(string query) =>
        query.Trim().ToLowerInvariant() is "all" or "everything" or "*" or "все" or "всё" or "everything in it" or "всё что есть" or "все что есть";

    /// <summary>
    /// Pick items up into the inventory. A kind of items ("medical") takes a mix, nearest first. With 'from' only items
    /// in that container, room or the room of that crew member ("take the tools from here").
    /// </summary>
    public string TakeItem(int botId, string query, int count, string? from = null)
    {
        if (Find(botId) is not { } c || !Ai(c, out var ai)) return "error: character gone";
        count = Math.Clamp(count, 1, MaxTake);
        var claimed = ClaimedByOthers(c);
        bool all = IsAll(query);
        List<Item> found;
        string? where = null;
        if (from != null && FindCrew(from.Trim(), c, includeSelf: false) is { } holder && HandOver(c, ai, holder, query, count, all) is { } handed)
            return handed;
        if (from != null)
        {
            var place = ItemsAt(c, from);
            if (place == null) return $"error: no container, room or crew member '{from}'; use a room id from 'rooms', a container name (cabinet, locker...) or a name";
            where = place.Value.Where;
            found = place.Value.Items
                .Where(i => ItemQueries.IsAvailableTo(i, c) && i.GetRootInventoryOwner() != c && !claimed.Contains(i))
                .Select(i => (i, score: all ? 1 : ItemQueries.MatchScore(i, query)))
                .Where(t => t.score > 0)
                .OrderByDescending(t => t.score)
                .ThenBy(t => Vector2.DistanceSquared(t.i.WorldPosition, c.WorldPosition))
                .Select(t => t.i).ToList();
            if (found.Count == 0)
                return $"error: no {(all ? "items you can take" : $"'{query}'")} in {where}" + Contents(place.Value.Items, c, " (there is: {0})");
        }
        else
        {
            if (all) return "error: 'all' needs 'from' (a container, a room or a crew member's name)";
            // not what the bot already carries, not what others are going for
            found = ItemQueries.Find(c, query, i => i.GetRootInventoryOwner() != c && !claimed.Contains(i))
                .OrderByDescending(i => ItemQueries.MatchScore(i, query)).ThenByDescending(i => InMachineOutput(i)).ToList();
            if (ItemQueries.Find(c, query).FirstOrDefault(i => i.GetRootInventoryOwner() == c) is { } own
                && ItemQueries.MatchScore(own, query, kinds: false) >= 3 && (found.Count == 0 || ItemQueries.MatchScore(found[0], query, kinds: false) < 3))
                return $"ok (you already carry {own.Name}; none left to take)";
            if (!ItemQueries.IsKindQuery(query) && ItemQueries.NoneFree(c, query, found.FirstOrDefault()) is { } busy) return "error: " + busy;
            if (found.Count == 0)
                return ItemQueries.Find(c, query).Any(i => i.GetRootInventoryOwner() == c)
                    ? $"ok (you already carry {query}; none left to take)"
                    : NotFound(c, query);
        }
        List<Item> pick;
        if (all || ItemQueries.IsKindQuery(query))
        {
            // a mix: one of each kind in turn (bandages, morphine, saline...), the nearest of each first
            var byKind = found.GroupBy(i => i.Prefab).Select(g => new Queue<Item>(g)).ToList();
            pick = new List<Item>();
            while (pick.Count < count && byKind.Any(q => q.Count > 0))
                foreach (var q in byKind)
                    if (q.Count > 0 && pick.Count < count) pick.Add(q.Dequeue());
        }
        else
        {
            var best = found[0].Prefab;
            pick = found.Where(i => i.Prefab == best).Take(count).ToList();
        }
        var label = (pick.Count == 1 ? pick[0].Name
            : string.Join(", ", pick.GroupBy(i => i.Prefab).Select(g => $"{g.Key.Name.Value} x{g.Count()}"))) + (where != null ? $" from {where}" : "");
        // with 'from' a replacement must come from the same place
        var inPlace = where != null ? found.ToHashSet() : null;
        FindReplacement replace = inPlace == null ? Replacer(c, query)
            : (failed, tried) => InventoryTaskRules.SelectReplacement(inPlace, failed, tried, ClaimedByOthers(c),
                i => i.Prefab == failed.Prefab && ItemQueries.IsAvailableTo(i, c) && i.GetRootInventoryOwner() != c && (all || ItemQueries.MatchScore(i, query) > 0));
        var r = StartTask(c, new TakeItemsTask(c, ai, pick, label, replace), evenOverPlayerOrders: true);
        return pick.Count < count && r.StartsWith("ok") && !all ? r + $"; only {pick.Count} found" : r;
    }

    /// <summary>Items in a container, a room (containers and floor) or the room a crew member is in.</summary>
    (List<Item> Items, string Where)? ItemsAt(Character c, string from)
    {
        var f = from.Trim();
        if (f.Split(new[] { " in ", " в ", " из ", " from " }, StringSplitOptions.RemoveEmptyEntries).Length == 2)
        {
            var (scoped, description) = ContainersAt(c, f, outputs: true);
            return scoped.Count == 0 ? null : (scoped.SelectMany(ic => ic.Inventory.AllItems).ToList(), description);
        }
        Hull? hull = null;
        if (IsRoomRef(f)) hull = ResolveRoom(f);
        if (f.ToLowerInvariant() is "here" or "this room" or "здесь" or "тут" or "отсюда" or "сюда" or "эта комната" or "этот отсек")
            hull = c.CurrentHull;
        var person = hull == null && f.Length >= 3 ? FindCrew(f, c, includeSelf: true) : null;
        if (person != null) hull = person.CurrentHull;
        hull ??= ResolveRoom(f) ?? Hull.HullList.FirstOrDefault(h => h.Submarine == c.Submarine && f.Length >= 4
                    && RoomId(h).StartsWith(f, StringComparison.OrdinalIgnoreCase));
        if (hull != null)
        {
            // loose on the floor, or directly in a cabinet / locker / crate of that room (not inside a gun or a machine)
            var items = Item.ItemList.Where(i => !i.Removed && i.GetRootInventoryOwner() is not Character
                    && (i.Container == null ? i.ParentInventory == null && i.CurrentHull == hull
                                            : i.Container.CurrentHull == hull && (IsStorage(i.Container) || i.Container.HasTag(TurretAmmoSource) || InMachineOutput(i))))
                .ToList();
            return (items, person != null ? $"{RoomId(hull)} (where {person.Name} is)" : RoomId(hull));
        }
        var (containers, where) = ContainersAt(c, f, outputs: true);
        if (containers.Count == 0) return null;
        return (containers.SelectMany(ic => ic.Inventory.AllItems).ToList(), where);
    }

    /// <summary>
    /// Cabinets, lockers, shelves, crates: not machines (fabricator, reactor, turret loaders...) and not the magazine of a
    /// gun, the tank of a suit or the battery of a flashlight.
    /// </summary>
    internal static bool IsStorage(Item i)
    {
        var ic = i.GetComponent<ItemContainer>();
        if (ic == null) return false;   // most items: skip the other lookups
        return ItemRules.IsStorage(new ContainerTraits(
            HasContainer: true,
            IsMachine: i.GetComponent<Fabricator>() != null || i.GetComponent<Deconstructor>() != null
                       || i.GetComponent<Reactor>() != null || i.GetComponent<Turret>() != null,
            IsTurretAmmoSource: i.HasTag(TurretAmmoSource),
            Pickable: i.GetComponent<Pickable>() != null,
            IsGearOrTool: i.GetComponent<Wearable>() != null || i.GetComponent<RangedWeapon>() != null || i.GetComponent<MeleeWeapon>() != null
                          || i.GetComponent<RepairTool>() != null || i.GetComponent<LightComponent>() != null,
            Capacity: ic.Capacity));
    }

    static readonly Identifier TurretAmmoSource = "turretammosource".ToIdentifier();

    /// <summary>"in Электрика [h140]: Средний стальной шкаф: Отвертка x2, ...; on the floor: ..." (inspect of a place).</summary>
    static string DescribePlace(List<Item> items, string where, Character c)
    {
        var free = items.Where(i => ItemQueries.IsAvailableTo(i, c)).ToList();
        if (free.Count == 0) return $"in {where}: nothing you can take";
        var parts = free.GroupBy(i => i.Container).OrderBy(g => g.Key == null ? 1 : 0).Take(5)
            .Select(g => $"{(g.Key != null ? g.Key.Name + (InMachineOutput(g.First()) ? " (output)" : "") : "on the floor")}: {Contents(g, c, max: 6)}");
        return $"in {where}: {string.Join("; ", parts)}. take_item(item=..., from=\"{where.Split(" (where")[0]}\") takes them";
    }

    /// <summary>"Бинт x5, Морфин x2" for a list of items (what is in a cabinet or a room).</summary>
    static string Contents(IEnumerable<Item> items, Character c, string format = "{0}", int max = 8)
    {
        var groups = items.Where(i => ItemQueries.IsAvailableTo(i, c)).GroupBy(i => i.Prefab)
            .OrderByDescending(g => g.Count()).Take(max).Select(g => $"{ItemQueries.Label(g.Key)} x{g.Count()}"
                + $" ({string.Join(", ", g.OrderBy(i => i.Condition / Math.Max(1, i.MaxCondition)).Take(5).Select(i => $"#{i.ID}" + (i.MaxCondition > 0 ? $" {100 * i.Condition / i.MaxCondition:0}%" : "")))})").ToList();
        return groups.Count == 0 ? (format == "{0}" ? "nothing you can take" : "") : string.Format(format, string.Join(", ", groups));
    }

    /// <summary>Empty a crate or container and put everything where it belongs (stacks first). Multi-step task.</summary>
    public string Unpack(int botId, string query, bool carry = true)
    {
        if (Find(botId) is not { } c || !Ai(c, out var ai)) return "error: character gone";
        bool HasStuff(Item i) => IsStorage(i) && i.OwnInventory?.AllItems.Any() == true;
        // what the bot holds, a marked or named container, or the fullest container in the named room
        var box = (c.Inventory?.AllItems ?? Enumerable.Empty<Item>()).FirstOrDefault(i => HasStuff(i) && ItemQueries.MatchScore(i, query) > 0);
        box ??= ItemQueries.Find(c, query, HasStuff).FirstOrDefault();
        if (box == null)
        {
            var named = Item.ItemList.Where(i => !i.Removed && i.Submarine == c.Submarine && HasStuff(i) && i.GetRootInventoryOwner() is not Character
                                                 && !ItemQueries.IsIgnored(i) && ItemQueries.MatchScore(i, query) > 0)
                .OrderBy(i => Vector2.DistanceSquared(i.WorldPosition, c.WorldPosition)).FirstOrDefault();
            box = named ?? (ItemsAt(c, query) is { } place
                ? place.Items.Select(i => i.Container).Where(x => x != null && HasStuff(x)).GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault()
                : null);
        }
        if (box == null) return $"error: no container with items '{query}'; name it ('metal crate'), say 'marked' for the one a player marked, or inspect(kind='items', query=<room>)";
        var ic = box.GetComponent<ItemContainer>();
        if (ic == null) return $"error: {box.Name} holds nothing";
        var plan = ic.Inventory.AllItems.GroupBy(i => i.Prefab)
            .Select(g => (g.Key, n: g.Count(), to: StoragePlanner.Choose(c, g.First(), new HashSet<Item> { box }, box.WorldPosition)))
            .ToList();
        if (plan.All(p => p.to == null))
            return $"error: no fitting place on the sub for anything in {box.Name} ({string.Join(", ", plan.Take(6).Select(p => $"{p.Key.Name.Value} x{p.n}"))}); use move_item with a container name";
        var preview = string.Join("; ", plan.Take(6).Select(p => $"{p.Key.Name.Value} x{p.n} -> {(p.to != null ? StoragePlanner.Describe(p.to.Container) : "stays")}"));
        return StartTask(c, new UnpackTask(c, ai, box, ic, carry), evenOverPlayerOrders: true) + $"; plan: {preview}";
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
        if (fuel >= 25 && !HasAuthority(c))
            return $"error: the reactor still has {fuel:0}% fuel; refuel when it drops below about 20% (a player can order it earlier). " +
                   "A second rod does not help: the reactor operator throws extra rods out";
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
        // amount = items wanted; one craft can make several (harpoons: 6)
        int runs = Crafting.Runs(recipe!, amount);
        amount = runs;
        var missing = Crafting.Missing(recipe!, stock, amount);
        if (missing.Count == 0) return StartTask(c, new CraftTask(c, ai, fabricator!, recipe!, amount));

        var missingText = string.Join(", ", missing.Select(m => $"{m.Req.FirstMatchingPrefab?.Name.Value ?? "?"} x{m.Missing}"));
        var (plan, problem) = Crafting.SalvagePlan(c, recipe!, stock, amount);
        if (!salvage)
            return $"error: cannot make {recipe!.DisplayName.Value} x{amount * Crafting.Yield(recipe!)} ({amount} craft(s)): missing {missingText}. " +
                   (plan != null
                       ? $"It can be salvaged: deconstruct {string.Join(", ", plan.GroupBy(i => i.Prefab).Select(g => $"{g.Key.Name.Value} x{g.Count()}"))} — call craft again with salvage=true to do it all."
                       : $"No scrap aboard gives it ({problem}). inspect(kind='recipe') shows other ways.");
        if (plan == null) return $"error: cannot make {recipe!.DisplayName.Value}: missing {missingText}, and salvaging is not enough: {problem}";
        if (plan.Count == 0) return StartTask(c, new CraftTask(c, ai, fabricator!, recipe!, amount));
        var f = fabricator!; var r = recipe!;
        return StartTask(c, new SalvageAndCraftTask(c, ai, plan, () => new CraftTask(c, ai, f, r, amount), $"{r.DisplayName.Value} x{amount * Crafting.Yield(r)}")) +
               $" ({runs} craft(s), {runs * Crafting.Yield(r)} items)" + $"; will first deconstruct {string.Join(", ", plan.GroupBy(i => i.Prefab).Select(g => $"{g.Key.Name.Value} x{g.Count()}"))}";
    }

    public string Deconstruct(int botId, string query, string? from = null, int count = 0)
    {
        if (Find(botId) is not { } c || !Ai(c, out var ai)) return "error: character gone";
        if (!Crafting.HasDeconstructor(c)) return "error: no deconstructor on this submarine";
        bool all = query.Trim().ToLowerInvariant() is "all" or "everything" or "*" or "все" or "всё" or "everything in it";

        if (!string.IsNullOrWhiteSpace(from))
        {
            var (places, where) = ContainersAt(c, from!);
            if (places.Count == 0) return $"error: no container or room '{from}'; use a room id from 'rooms' or a container name (cabinet, locker, crate...)";
            var candidates = places.SelectMany(ic => ic.Inventory.AllItems)
                .Where(i => i.Prefab.AllowDeconstruct && i.Prefab.DeconstructItems.Any() && !c.HasEquippedItem(i))
                .Select(i => (i, score: all ? 1 : ItemQueries.MatchScore(i, query)))
                .Where(t => t.score > 0).ToList();
            // only the best match: "Сварочный аппарат" is the welding tool, not every "Топливный бак сварочного аппарата" next to it
            int best = candidates.Count > 0 ? candidates.Max(t => t.score) : 0;
            var items = candidates.Where(t => t.score == best).Select(t => t.i)
                .Take(all ? (count > 0 ? count : MaxDeconstructBatch) : Math.Max(1, count)).ToList();
            if (items.Count == 0) return $"error: nothing {(all ? "" : $"matching '{query}' ")}that can be deconstructed in {where}";
            return StartTask(c, new DeconstructManyTask(c, ai, items, where)) +
                   $"; {items.Count} item(s): {string.Join(", ", items.GroupBy(i => i.Prefab).Select(g => $"{g.Key.Name.Value} x{g.Count()}"))}";
        }

        // what players marked for deconstruction (or for the AI) with the middle mouse button
        if (MarkWords.Of(query) is var mark && mark != MarkQuery.None)
        {
            var pool = ItemQueries.Marked(c, MarkQuery.Deconstruct);
            if (mark == MarkQuery.Ai && pool.Count == 0) pool = ItemQueries.Marked(c, MarkQuery.Ai);
            var items = pool.Where(i => ItemQueries.IsAvailableTo(i, c) && i.Prefab.AllowDeconstruct && i.Prefab.DeconstructItems.Any() && !c.HasEquippedItem(i))
                .OrderBy(i => Vector2.DistanceSquared(i.WorldPosition, c.WorldPosition)).Take(MaxDeconstructBatch).ToList();
            if (items.Count == 0) return "error: nothing marked that you can deconstruct (players mark items with the middle mouse button)";
            return StartTask(c, new DeconstructManyTask(c, ai, items, "marked items")) +
                   $"; {items.Count} item(s): {string.Join(", ", items.GroupBy(i => i.Prefab).Select(g => $"{g.Key.Name.Value} x{g.Count()}"))}";
        }

        if (all) return "error: 'all' needs 'from' (a container or room), so nothing important is broken down by accident";
        // without 'from' the item must be named: "tools" or "medical" alone would break down whatever is nearest
        // (with 'from', "инструменты из шкафа" is fine: only that cabinet)
        var found = ItemQueries.Find(c, query, i => i.Prefab.DeconstructItems.Any() && i.Prefab.AllowDeconstruct && !c.HasEquippedItem(i), kinds: false);
        if (found.Count == 0)
        {
            // maybe they named the material they want
            var hint = Crafting.Salvage(c, query);
            return $"error: no '{query}' that can be deconstructed. {hint}";
        }
        if (count <= 1) return StartTask(c, new DeconstructTask(c, ai, found[0]));
        var same = found.Where(i => i.Prefab == found[0].Prefab).Take(count).ToList();
        return StartTask(c, new DeconstructManyTask(c, ai, same, "around the sub")) +
               (same.Count < count ? $"; only {same.Count} found" : "");
    }

    const int MaxDeconstructBatch = 12;

    /// <summary>Storage containers by name or room ("cabinet", "шкаф", "Электрика [h140]", "шкаф в Электрика [h140]").</summary>
    /// <summary>In what a fabricator made or a deconstructor gave: products wait there to be taken.</summary>
    internal static bool InMachineOutput(Item i)
    {
        if (i.Container is not { } m || i.ParentInventory == null) return false;
        return m.GetComponent<Fabricator>()?.OutputContainer?.Inventory == i.ParentInventory
            || m.GetComponent<Deconstructor>()?.OutputContainer?.Inventory == i.ParentInventory;
    }

    /// <summary>"шкаф у фабрикатора", "the locker next to the reactor": the storage of that kind nearest to the named thing
    /// (a machine, a station, any item), within a few metres. null = not such a phrase, or nothing near it.</summary>
    internal (ItemContainer Container, string Where)? NearAnchor(Character c, string text, Item? toPut = null)
    {
        if (ItemWords.SplitNear(text) is not { } near) return null;
        var (what, anchorText) = near;
        var anchor = Item.ItemList
            .Where(i => !i.Removed && i.Submarine == c.Submarine && i.GetRootInventoryOwner() is not Character && ItemQueries.MatchScore(i, anchorText, kinds: false) > 0)
            .OrderByDescending(i => ItemQueries.MatchScore(i, anchorText, kinds: false))
            .ThenBy(i => Vector2.DistanceSquared(i.WorldPosition, c.WorldPosition))
            .FirstOrDefault();
        if (anchor == null) return null;
        var storages = Item.ItemList
            .Where(i => !i.Removed && i != anchor && i.Submarine == c.Submarine && i.GetRootInventoryOwner() is not Character && IsStorage(i)
                        && Vector2.Distance(i.WorldPosition, anchor.WorldPosition) < 700f)
            .Select(i => (i, ic: i.GetComponent<ItemContainer>()!))
            .Where(t => toPut == null || t.ic.Inventory.CanBePut(toPut))
            .ToList();
        // of that kind if any is there ("шкаф"), else any storage next to it
        var kind = storages.Where(t => ItemQueries.MatchScore(t.i, what, kinds: false) > 0).ToList();
        var pick = (kind.Count > 0 ? kind : storages).OrderBy(t => Vector2.DistanceSquared(t.i.WorldPosition, anchor.WorldPosition)).FirstOrDefault();
        if (pick.ic == null) return null;
        return (pick.ic, $"{pick.i.Name} next to {anchor.Name} in {(pick.i.CurrentHull != null ? RoomId(pick.i.CurrentHull) : "?")}");
    }

    /// <param name="outputs">also fabricators and deconstructors by name, meaning their output (to take products from)</param>
    (List<ItemContainer> Containers, string Where) ContainersAt(Character c, string from, bool outputs = false)
    {
        if (NearAnchor(c, from) is { } near) return (new List<ItemContainer> { near.Container }, near.Where);
        var storages = Item.ItemList
            .Where(i => i.Submarine == c.Submarine && !i.Removed && i.GetRootInventoryOwner() is not Character)
            .Select(i => (item: i, ic: outputs && (i.GetComponent<Fabricator>()?.OutputContainer ?? i.GetComponent<Deconstructor>()?.OutputContainer) is { } o
                ? o : i.GetComponent<ItemContainer>()))
            .Where(t => t.ic != null && (outputs && (t.item.GetComponent<Fabricator>() != null || t.item.GetComponent<Deconstructor>() != null)
                        || t.item.GetComponent<Fabricator>() == null && t.item.GetComponent<Deconstructor>() == null
                        && t.item.GetComponent<Reactor>() == null && t.item.GetComponent<Turret>() == null))
            .ToList();

        var hull = IsRoomRef(from) ? ResolveRoom(from) : ResolveRoom(from.Trim());
        var name = from;
        // Parse explicit scope even when a bracketed room id already resolved the hull.
        var parts = from.Split(new[] { " in ", " в ", " из ", " from " }, StringSplitOptions.RemoveEmptyEntries);
        bool namedScope = parts.Length == 2;
        if (namedScope)
        {
            name = parts[0].Trim();
            hull = ResolveRoom(parts[1].Trim()) ?? Hull.HullList.FirstOrDefault(h => h.Submarine == c.Submarine && RoomId(h).Contains(parts[1].Trim(), StringComparison.OrdinalIgnoreCase));
            if (hull == null) return (new List<ItemContainer>(), from);
        }
        var named = storages.Where(t => ItemQueries.MatchScore(t.item, name) > 0)
            .OrderByDescending(t => ItemQueries.MatchScore(t.item, name))
            .ThenBy(t => Vector2.DistanceSquared(t.item.WorldPosition, c.WorldPosition)).ToList();
        if (hull != null)
        {
            var inRoom = storages.Where(t => t.item.CurrentHull == hull).ToList();
            var both = named.Where(t => t.item.CurrentHull == hull).ToList();
            return ((namedScope ? both.Take(1) : both.Count > 0 ? both : inRoom).Select(t => t.ic!).ToList(), RoomId(hull));
        }
        var nearest = named.Take(1).ToList();
        return (nearest.Select(t => t.ic!).ToList(),
                nearest.Count > 0 ? $"{nearest[0].item.Name} in {(nearest[0].item.CurrentHull != null ? RoomId(nearest[0].item.CurrentHull) : "?")}" : from);
    }

    /// <summary>take_item(from=a crew member) when that person carries it: an AI crewmate hands it over (the taker walks
    /// to them); a human player is asked instead (nobody takes things out of a player's pockets). null = they carry
    /// nothing like it: then 'from' means the place where they are.</summary>
    string? HandOver(Character c, HumanAIController ai, Character holder, string query, int count, bool all)
    {
        var items = (holder.Inventory?.AllItems ?? Enumerable.Empty<Item>())
            .Where(i => !holder.HasEquippedItem(i) && (all || ItemQueries.MatchScore(i, query) > 0))
            .Distinct().ToList();
        if (items.Count == 0) return null;
        if (!holder.IsBot && !holder.IsIncapacitated)
            return $"error: {holder.Name} carries {items[0].Name}, but only they can hand it over: ask them (say) to give it to you or drop it";
        var best = all ? null : items.OrderByDescending(i => ItemQueries.MatchScore(i, query)).First().Prefab;
        var pick = items.Where(i => best == null || i.Prefab == best).Take(count).ToList();
        return StartTask(c, new ReceiveItemsTask(c, ai, holder, pick), evenOverPlayerOrders: true);
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
}
