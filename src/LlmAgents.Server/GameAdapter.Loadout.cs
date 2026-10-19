using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Barotrauma;
using Barotrauma.Items.Components;
using LlmAgents.Agents;

namespace LlmAgents;

public sealed partial class GameAdapter
{
    sealed class LoadoutRuntime
    {
        public readonly Character Bot;
        public readonly InventoryLoadout Profile;
        public double NextCheck;
        public double LastCheck;
        public string Status = "waiting for first inventory check";
        public LoadoutRuntime(Character bot, InventoryLoadout profile) { Bot = bot; Profile = profile; }
    }

    readonly Dictionary<ushort, LoadoutRuntime> loadouts = new();
    double nextLoadoutTick;

    readonly record struct LoadoutCell(Inventory Inventory, int Slot);
    sealed record LoadoutBinding(InventorySlotRule Rule, LoadoutCell Cell);

    public string ConfigureInventoryLoadout(int botId, InventoryLoadout profile)
    {
        if (Find(botId) is not { } bot || !IsControllableBot(bot)) return "error: character is not a living AI crew member";
        if (bot.Inventory == null) return "error: character has no inventory";
        if (InventoryLoadoutRules.Validate(profile) is { } structuralError) return "error: " + structuralError;
        var copy = profile.Clone();
        foreach (var rule in copy.Slots)
        {
            if (rule.Prefab != null)
            {
                if (!ItemPrefab.Prefabs.TryGet(rule.Prefab.ToIdentifier(), out var prefab))
                    return $"error: unknown prefab '{rule.Prefab}'; copy exact prefab identifiers from inspect_inventory";
                rule.Prefab = prefab.Identifier.Value;
            }
        }
        var destinations = new HashSet<LoadoutCell>();
        foreach (var rule in copy.Slots)
        {
            var inventory = ResolveLoadoutInventory(bot, copy, rule, out _, out bool invalid);
            if (invalid) return $"error: {InventoryLoadoutRules.Address(rule)}: invalid container path or slot";
            if (inventory == null) continue; // A missing desired bag is a deferred requirement, not a malformed profile.
            if (rule.Slot >= inventory.Capacity) return $"error: {InventoryLoadoutRules.Address(rule)}: slot is outside capacity {inventory.Capacity}";
            if (!destinations.Add(new LoadoutCell(inventory, rule.Slot)))
                return $"error: {InventoryLoadoutRules.Address(rule)} aliases another rule's actual inventory slot; use one path per physical compartment";
            if (rule.Prefab == null) continue;
            var sample = Item.ItemList.FirstOrDefault(i => !i.Removed && i.Prefab.Identifier.Value == rule.Prefab);
            if (sample != null && !LoadoutFootprints(sample, new LoadoutCell(inventory, rule.Slot), copy, bot).Any())
                return $"error: {InventoryLoadoutRules.Address(rule)}: '{rule.Prefab}' cannot occupy this slot or conflicts with another equipment slot rule";
            if (inventory is CharacterInventory ci && ci.SlotTypes[rule.Slot] != InvSlotType.Any && rule.Count != 1)
                return $"error: {InventoryLoadoutRules.Address(rule)}: equipment slots hold one item";
            if (sample != null && rule.Count > LoadoutStackLimit(sample, inventory, rule.Slot))
                return $"error: {InventoryLoadoutRules.Address(rule)}: count exceeds this slot's stack limit";
        }
        var runtime = new LoadoutRuntime(bot, copy);
        loadouts[bot.ID] = runtime; // Atomic replacement: no live profile changes before every validation succeeds.
        try { CheckLoadout(runtime); }
        catch (Exception e)
        {
            runtime.NextCheck = Timing.TotalTime + copy.CheckIntervalSeconds;
            runtime.Status = "blocked: first inventory check failed; " + e.Message;
            Log.ErrorThrottled("loadout:" + bot.ID, "inventory loadout: " + e);
        }
        return "ok, inventory loadout saved; " + runtime.Status + ". Only already-carried items are rearranged; missing supplies need take_item/craft/refill.";
    }

    public string InspectInventoryLoadout(int botId)
    {
        if (Find(botId) is not { } bot) return "error: character gone";
        if (!loadouts.TryGetValue(bot.ID, out var runtime) || runtime.Bot != bot)
            return "{\"configured\":false,\"interaction\":\"inspect_inventory, then set_inventory_loadout with exact prefab IDs and zero-based slot paths\"}";
        var rules = new JsonArray();
        foreach (var rule in runtime.Profile.Slots)
        {
            var inventory = ResolveLoadoutInventory(bot, runtime.Profile, rule, out var problem, out _);
            var actual = inventory != null && rule.Slot < inventory.Capacity
                ? inventory.GetItemsAt(rule.Slot).Where(i => !i.Removed).Distinct().ToList() : new List<Item>();
            bool matches = inventory != null && problem == null && (rule.Prefab == null ? actual.Count == 0
                : actual.Count == rule.Count && actual.All(i => i.Prefab.Identifier.Value == rule.Prefab));
            rules.Add(new JsonObject
            {
                ["address"] = InventoryLoadoutRules.Address(rule), ["path"] = new JsonArray(rule.Path.Select(s => (JsonNode)new JsonObject { ["slot"] = s.Slot, ["container_index"] = s.ContainerIndex }).ToArray()),
                ["slot"] = rule.Slot, ["prefab"] = rule.Prefab, ["count"] = rule.Count,
                ["actual"] = new JsonArray(actual.Select(i => (JsonNode)new JsonObject { ["id"] = $"#{i.ID}", ["prefab"] = i.Prefab.Identifier.Value, ["condition_percent"] = i.ConditionPercentage }).ToArray()),
                ["matches"] = matches, ["problem"] = problem,
            });
        }
        return new JsonObject
        {
            ["configured"] = true, ["enabled"] = runtime.Profile.Enabled,
            ["check_interval_seconds"] = runtime.Profile.CheckIntervalSeconds, ["review_interval_seconds"] = runtime.Profile.ReviewIntervalSeconds,
            ["status"] = runtime.Status, ["last_check_seconds_ago"] = Math.Max(0, Timing.TotalTime - runtime.LastCheck),
            ["next_check_in_seconds"] = runtime.Profile.Enabled ? Math.Max(0, runtime.NextCheck - Timing.TotalTime) : null,
            ["scope"] = "own carried items only; missing items are deferred, nothing is spawned, taken from the world, dropped, or deconstructed",
            ["slots"] = rules,
        }.ToJsonString(InventoryJson);
    }

    internal string? InventoryLoadoutSummary(Character bot)
    {
        if (!loadouts.TryGetValue(bot.ID, out var runtime) || runtime.Bot != bot) return null;
        return (runtime.Profile.Enabled ? "enabled" : "disabled") + "; " + runtime.Status + "; desired: "
            + string.Join(", ", runtime.Profile.Slots.Take(12).Select(r => InventoryLoadoutRules.Address(r) + "=" + (r.Prefab == null ? "empty" : r.Prefab + " x" + r.Count)))
            + (runtime.Profile.Slots.Count > 12 ? "; inspect_loadout for all slots" : "");
    }

    /// <summary>Native idle cleanup must not immediately undo explicit held-tool/suit rules.
    /// All active duties and safety reactions retain ordinary native cleanup.</summary>
    internal bool PreserveIdleLoadoutEquipment(Character bot)
    {
        if (!Settings.Enabled || !Settings.IsCharacterEnabled(bot.Name) || !Settings.IsJobEnabled(JobOf(bot))) return false;
        if (!loadouts.TryGetValue(bot.ID, out var runtime) || runtime.Bot != bot || !runtime.Profile.Enabled) return false;
        if (LoadoutBusy(bot) != null || bot.Inventory is not { } inventory) return false;
        return runtime.Profile.Slots.Where(r => r.Path.Count == 0 && r.Prefab != null && r.Slot < inventory.Capacity)
            .Any(rule => inventory.GetItemsAt(rule.Slot).Any(item => !item.Removed && item.Prefab.Identifier == rule.Prefab!.ToIdentifier()
                && bot.HasEquippedItem(item) && ((inventory.SlotTypes[rule.Slot] & (InvSlotType.LeftHand | InvSlotType.RightHand)) != 0
                    || item.HasTag(Tags.HeavyDivingGear) || item.HasTag(Tags.LightDivingGear))));
    }

    void ResetLoadouts() { loadouts.Clear(); nextLoadoutTick = 0; }

    void TickLoadouts()
    {
        if (Timing.TotalTime < nextLoadoutTick) return;
        nextLoadoutTick = Timing.TotalTime + 1;
        foreach (var runtime in loadouts.Values.ToList())
        {
            if (runtime.Bot.Removed || Find(runtime.Bot.ID) != runtime.Bot) { loadouts.Remove(runtime.Bot.ID); continue; }
            if (Timing.TotalTime < runtime.NextCheck) continue;
            try { CheckLoadout(runtime); }
            catch (Exception e)
            {
                runtime.NextCheck = Timing.TotalTime + runtime.Profile.CheckIntervalSeconds;
                runtime.Status = "blocked: inventory check failed; " + e.Message;
                Log.ErrorThrottled("loadout:" + runtime.Bot.ID, "inventory loadout: " + e);
            }
        }
    }

    void CheckLoadout(LoadoutRuntime runtime)
    {
        var bot = runtime.Bot;
        runtime.LastCheck = Timing.TotalTime;
        runtime.NextCheck = Timing.TotalTime + runtime.Profile.CheckIntervalSeconds;
        if (!runtime.Profile.Enabled) { runtime.Status = "disabled"; return; }
        if (!Settings.Enabled || !Settings.IsCharacterEnabled(bot.Name) || !Settings.IsJobEnabled(JobOf(bot)))
        { runtime.Status = "deferred: AI is disabled for this character or job"; return; }
        if (LoadoutBusy(bot) is { } busy) { runtime.Status = "deferred: " + busy; return; }
        if (runtime.Profile.Slots.Count == 0) { runtime.Status = "no managed slots"; return; }
        runtime.Status = ReconcileLoadout(bot, runtime.Profile);
    }

    string? LoadoutBusy(Character bot)
    {
        if (!IsControllableBot(bot) || bot.Inventory == null) return "character or inventory unavailable";
        if (bot.IsIncapacitated || bot.IsHandcuffed) return "incapacitated or handcuffed";
        if (bot.CurrentHull == null || bot.AnimController.HeadInWater) return "outside or submerged; protective equipment has priority";
        if (HealthPercent(bot) < 75 || bot.CurrentHull.FireSources.Count > 0 || bot.CurrentHull.WaterPercentage >= 50)
            return "injury, fire or flooding";
        if (bot.IsLowInOxygen || GearLock.InDanger(bot)) return "danger or low oxygen";
        if (tasks.ContainsKey(bot.ID)) return "active AI task";
        if (bot.SelectedCharacter != null) return "holding or treating a character";
        if (bot.SelectedItem != null) return "interacting with an item";
        if (!Ai(bot, out var ai)) return "native AI controller unavailable";
        var current = ai.ObjectiveManager.CurrentObjective;
        // Pure travel uses no item interaction. Pocket maintenance can continue while following or
        // waiting, but obtaining gear/items on that route and every working objective retain priority.
        bool freeMovement = current is AIObjectiveGoTo && !current.SubObjectives.Any();
        bool freeIdle = current is AIObjectiveIdle && (current.GetActiveObjective() is not { } active || active == current
            || active is AIObjectiveGoTo && !active.SubObjectives.Any());
        if (!freeMovement && bot.CurrentOrders?.Any(o => o != null && !o.IsDismissal && IsPlayerOrder(bot, o)) == true) return "player order is active";
        if (current != null && !freeIdle && !freeMovement)
            return "native objective " + current.Identifier.Value;
        return null;
    }

    static Inventory? ResolveLoadoutInventory(Character bot, InventoryLoadout profile, InventorySlotRule rule, out string? problem, out bool invalid)
    {
        Inventory? inventory = bot.Inventory;
        problem = null; invalid = false;
        for (int depth = 0; depth < rule.Path.Count; depth++)
        {
            var step = rule.Path[depth];
            if (inventory == null) { problem = "container is not carried yet"; return null; }
            if (step.Slot >= inventory.Capacity) { invalid = true; problem = "container path slot is outside inventory capacity"; return null; }
            var parentRule = profile.Slots.FirstOrDefault(r => InventoryLoadoutRules.Address(r) == InventoryLoadoutRules.Address(rule.Path.Take(depth).ToArray(), step.Slot));
            var parents = inventory.GetItemsAt(step.Slot).Where(i => !i.Removed).Distinct().ToList();
            if (parents.Count != 1) { problem = parents.Count == 0 ? "container is not in its configured parent slot yet" : "container path is ambiguous: parent slot holds multiple items"; return null; }
            if (parentRule?.Prefab != null && parents[0].Prefab.Identifier.Value != parentRule.Prefab)
            { problem = "desired parent container layout is pending"; return null; }
            var containers = parents[0].GetComponents<ItemContainer>().ToList();
            if (step.ContainerIndex >= containers.Count)
            { invalid = parentRule == null || parentRule.Prefab == parents[0].Prefab.Identifier.Value; problem = "parent item has no such container_index"; return null; }
            inventory = containers[step.ContainerIndex].Inventory;
        }
        if (inventory != null && rule.Slot >= inventory.Capacity) { invalid = true; problem = "slot is outside inventory capacity"; return null; }
        if (inventory != null && InventoryAccessProblem(bot, inventory, requireDragAndDrop: true) is { } access) problem = access;
        return inventory;
    }

    static int LoadoutStackLimit(Item item, Inventory inventory, int slot)
    {
        if (inventory is CharacterInventory ci && ci.SlotTypes[slot] != InvSlotType.Any) return 1;
        var container = (inventory.Owner as Item)?.GetComponents<ItemContainer>().FirstOrDefault(ic => ic.Inventory == inventory);
        return Math.Min(item.Prefab.GetMaxStackSize(inventory), container?.GetMaxStackSize(slot) ?? int.MaxValue);
    }

    static IEnumerable<List<LoadoutCell>> LoadoutFootprints(Item item, LoadoutCell destination, InventoryLoadout profile, Character bot)
    {
        if (InventoryTransferRules.WouldCreateCycle(item, destination.Inventory.Owner as Item, i => i.Container)) yield break;
        if (destination.Inventory is not CharacterInventory ci)
        {
            var container = ((Item)destination.Inventory.Owner).GetComponents<ItemContainer>().FirstOrDefault(ic => ic.Inventory == destination.Inventory);
            if (container != null && container.CanBeContained(item, destination.Slot)) yield return new List<LoadoutCell> { destination };
            yield break;
        }
        var slotType = ci.SlotTypes[destination.Slot];
        if (slotType == InvSlotType.Any)
        {
            if (item.AllowedSlots.Contains(InvSlotType.Any)) yield return new List<LoadoutCell> { destination };
            yield break;
        }
        foreach (var mask in item.GetComponents<Pickable>().SelectMany(p => p.AllowedSlots).Distinct()
            .Where(mask => mask.HasFlag(slotType)).OrderBy(mask => Enumerable.Range(0, ci.Capacity).Count(index => mask.HasFlag(ci.SlotTypes[index]))))
        {
            var footprint = Enumerable.Range(0, ci.Capacity).Where(index => mask.HasFlag(ci.SlotTypes[index])).Select(index => new LoadoutCell(ci, index)).ToList();
            if (footprint.Count == 0 || footprint.Any(cell => profile.Slots.Any(r => r.Path.Count == 0 && r.Slot == cell.Slot
                && (r.Prefab != item.Prefab.Identifier.Value || r.Count != 1)))) continue;
            if (mask.HasFlag(InvSlotType.LeftHand) && bot.AnimController.GetLimb(LimbType.LeftHand) == null
                || mask.HasFlag(InvSlotType.RightHand) && bot.AnimController.GetLimb(LimbType.RightHand) == null) continue;
            yield return footprint;
        }
    }

    string ReconcileLoadout(Character bot, InventoryLoadout profile)
    {
        var problems = new List<string>();
        var bindings = new List<LoadoutBinding>();
        foreach (var rule in profile.Slots.OrderBy(r => r.Path.Count))
        {
            var inventory = ResolveLoadoutInventory(bot, profile, rule, out var problem, out _);
            if (problem != null || inventory == null) { problems.Add(InventoryLoadoutRules.Address(rule) + ": " + (problem ?? "inventory unavailable")); continue; }
            if (bindings.Any(b => b.Cell == new LoadoutCell(inventory, rule.Slot)))
                return "blocked: multiple profile paths now resolve to the same physical inventory slot; revise the duplicate paths";
            bindings.Add(new LoadoutBinding(rule, new LoadoutCell(inventory, rule.Slot)));
        }
        var carried = Carried(bot).Take(2049).ToList();
        if (carried.Count > 2048) return "blocked: carried inventory exceeds maintenance budget (2048 items)";
        var inventories = new[] { (Inventory)bot.Inventory }.Concat(carried.SelectMany(i => i.GetComponents<ItemContainer>().Select(ic => (Inventory)ic.Inventory))).Distinct().Take(129).ToList();
        if (inventories.Count > 128 || inventories.Sum(i => i.Capacity) > 4096) return "blocked: carried inventory exceeds maintenance budget";
        var original = inventories.SelectMany(inv => Enumerable.Range(0, inv.Capacity).Select(s => new LoadoutCell(inv, s)))
            .ToDictionary(c => c, c => c.Inventory.GetItemsAt(c.Slot).Where(i => !i.Removed).Distinct().ToList());
        var planned = original.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        var claimed = ClaimedByOthers(bot);
        bool Movable(Item i) => i.ParentInventory != null && i.GetRootInventoryOwner() == bot && !i.Removed && !claimed.Contains(i)
            && !ItemQueries.IsIgnored(i) && i.IsInteractable(bot)
            && !(i.HasTag(Tags.HeavyDivingGear) && bot.HasEquippedItem(i) && GearLock.IsLocked(bot))
            && InventoryAccessProblem(bot, i.ParentInventory, requireDragAndDrop: true) == null;
        bool Spare(Item i) => i.ParentInventory == bot.Inventory || i.Container is { } container && IsStorage(container)
            || bindings.Any(b => b.Cell.Inventory == i.ParentInventory && b.Cell.Inventory.GetItemsAt(b.Cell.Slot).Contains(i));
        var allocated = new Dictionary<Item, List<LoadoutCell>>();
        var wanted = new Dictionary<LoadoutCell, List<Item>>();
        bool Assign(Item item, LoadoutCell cell)
        {
            if (!Movable(item) && !original[cell].Contains(item)) return false;
            if (allocated.TryGetValue(item, out var existing)) return existing.Contains(cell);
            foreach (var footprint in LoadoutFootprints(item, cell, profile, bot))
            {
                if (footprint.Any(part => wanted.TryGetValue(part, out var entries) && entries.Any(other => other != item)
                    && (part.Inventory is CharacterInventory ci && ci.SlotTypes[part.Slot] != InvSlotType.Any || entries.Count >= LoadoutStackLimit(item, part.Inventory, part.Slot)))) continue;
                if (!Movable(item) && original.Where(kv => kv.Value.Contains(item)).Select(kv => kv.Key).Except(footprint).Any()) continue;
                allocated[item] = footprint;
                foreach (var part in footprint)
                {
                    if (!wanted.TryGetValue(part, out var entries)) wanted[part] = entries = new List<Item>();
                    if (!entries.Contains(item)) entries.Add(item);
                }
                return true;
            }
            return false;
        }
        // Keep instances already satisfying another requested slot before allocating any shared spare stock.
        foreach (var binding in bindings.Where(b => b.Rule.Prefab != null))
            foreach (var item in original[binding.Cell].Where(i => i.Prefab.Identifier.Value == binding.Rule.Prefab).Take(binding.Rule.Count)) Assign(item, binding.Cell);
        foreach (var binding in bindings)
        {
            if (binding.Rule.Prefab == null) { wanted.TryAdd(binding.Cell, new List<Item>()); continue; }
            int count = wanted.TryGetValue(binding.Cell, out var entries) ? entries.Count : 0;
            foreach (var item in carried.Where(i => i.Prefab.Identifier.Value == binding.Rule.Prefab && Spare(i))
                .OrderBy(i => allocated.TryGetValue(i, out var cells) && cells.Contains(binding.Cell) ? 0 : 1).ThenBy(i => i.ID))
            {
                if (count >= binding.Rule.Count) break;
                if (allocated.TryGetValue(item, out var previous) && previous.Contains(binding.Cell)) continue;
                if (Assign(item, binding.Cell)) count++;
            }
            if (count < binding.Rule.Count) problems.Add(InventoryLoadoutRules.Address(binding.Rule) + $": missing carried {binding.Rule.Prefab} x{binding.Rule.Count - count}");
        }
        var managed = bindings.Select(b => b.Cell).ToHashSet();
        // An unavailable desired item does not justify evacuating a harmless occupant from its slot.
        foreach (var binding in bindings.Where(b => b.Rule.Prefab != null && !wanted.ContainsKey(b.Cell))) managed.Remove(binding.Cell);
        var affected = managed.Concat(wanted.Keys).Distinct().ToHashSet();
        var displaced = affected.SelectMany(cell => original[cell]).Concat(allocated.Keys).Distinct().ToList();
        foreach (var item in displaced)
            foreach (var cell in planned.Keys) planned[cell].Remove(item);
        foreach (var entry in wanted) planned[entry.Key].AddRange(entry.Value);
        foreach (var item in displaced.Where(i => !allocated.ContainsKey(i)).OrderBy(i => i.ID))
        {
            if (!Movable(item)) return "blocked: " + ItemQueries.InstanceLabel(item) + " occupies a managed slot and cannot be moved safely";
            var fallback = planned.Keys.Where(c => !affected.Contains(c) && InventoryAccessProblem(bot, c.Inventory, requireDragAndDrop: true) == null
                    && (c.Inventory == bot.Inventory || c.Inventory.Owner is Item holder && IsStorage(holder)))
                .OrderBy(c => original[c].Contains(item) ? 0 : c.Inventory == item.ParentInventory ? 1 : 2)
                .ThenBy(c => c.Inventory is CharacterInventory ci && ci.SlotTypes[c.Slot] != InvSlotType.Any ? 1 : 0).ThenBy(c => c.Slot);
            bool placed = false;
            foreach (var cell in fallback)
            {
                if (cell.Inventory is CharacterInventory ci && ci.SlotTypes[cell.Slot] != InvSlotType.Any
                    && ci.SlotTypes[cell.Slot] != InvSlotType.LeftHand && ci.SlotTypes[cell.Slot] != InvSlotType.RightHand) continue;
                foreach (var footprint in LoadoutFootprints(item, cell, profile, bot))
                {
                    if (footprint.Any(c => affected.Contains(c) || !LoadoutCanStack(item, c, planned[c]))) continue;
                    foreach (var part in footprint) planned[part].Add(item);
                    placed = true; break;
                }
                if (placed) break;
            }
            if (!placed) return "blocked: no safe carried slot for displaced " + ItemQueries.InstanceLabel(item) + "; free space or revise the profile; existing contents preserved";
        }
        var changed = carried.Where(item => !original.Where(kv => kv.Value.Contains(item)).Select(kv => kv.Key).ToHashSet()
            .SetEquals(planned.Where(kv => kv.Value.Contains(item)).Select(kv => kv.Key))).ToList();
        var parents = planned.SelectMany(kv => kv.Value.Select(item => (Item: item, Parent: kv.Key.Inventory.Owner as Item)))
            .GroupBy(pair => pair.Item).ToDictionary(group => group.Key, group => group.First().Parent);
        foreach (var item in parents.Keys)
        {
            var ancestors = new HashSet<Item> { item };
            var current = item;
            while (parents.TryGetValue(current, out var parent) && parent != null)
            {
                if (!ancestors.Add(parent)) return "blocked: desired layout would place a container inside itself or create a containment cycle; existing contents preserved";
                current = parent;
            }
        }
        if (changed.Count == 0) return problems.Count == 0 ? "matched: all managed slots are correct" : "waiting: " + string.Join("; ", problems.Take(8));
        if (changed.Count > 128) return "blocked: inventory change exceeds transaction budget (128 items)";
        if (changed.Any(i => !Movable(i))) return "blocked: a required item is reserved, locked, ignored, or inaccessible";
        if (planned.Where(kv => !kv.Value.ToHashSet().SetEquals(original[kv.Key]))
            .Any(kv => kv.Value.Any(i => !LoadoutCanStack(i, kv.Key, kv.Value.Where(other => other != i).ToList()))))
            return "blocked: desired item conditions, quality, stack limits or equipment footprints are incompatible";
        if (!ApplyLoadoutLayout(bot, original, planned, changed, out var error)) return "blocked: " + error;
        return $"managed {changed.Count} carried item(s)" + (problems.Count == 0 ? "; all resolved slots matched" : "; waiting: " + string.Join("; ", problems.Take(8)));
    }

    static bool LoadoutCanStack(Item item, LoadoutCell cell, IReadOnlyList<Item> contents)
    {
        if (contents.Count >= LoadoutStackLimit(item, cell.Inventory, cell.Slot)) return false;
        if (contents.Any(other => other.Prefab != item.Prefab || other.Quality != item.Quality)) return false;
        if (contents.Count > 0 && !(item.IsFullCondition && contents.All(i => i.IsFullCondition)
            || Math.Abs(item.Condition) < 0.0001f && contents.All(i => Math.Abs(i.Condition) < 0.0001f))) return false;
        return true;
    }

    static bool ApplyLoadoutLayout(Character bot, Dictionary<LoadoutCell, List<Item>> original,
        Dictionary<LoadoutCell, List<Item>> planned, List<Item> changed, out string? error)
    {
        error = null;
        var wasEquipped = changed.ToDictionary(i => i, i => bot.HasEquippedItem(i));
        var insertedEffectsApplied = changed.ToDictionary(i => i, i => i.OnInsertedEffectsApplied);
        var changedSet = changed.ToHashSet();
        var inventories = original.Keys.Select(c => c.Inventory).Distinct().ToList();
        try
        {
            // Native swapping can intentionally drop items or insert them into the displaced container.
            // Detach the bounded change set on the game thread, then use only exact non-swapping inserts.
            // No physics update occurs while a transaction is detached; a rejection restores the snapshot.
            foreach (var item in changed)
            {
                DetachLoadoutItem(bot, item);
                if (item.Removed || item.ParentInventory != null)
                    throw new InvalidOperationException("native removal did not detach " + ItemQueries.InstanceLabel(item));
            }
            foreach (var item in changed.OrderBy(i => planned.Where(kv => kv.Value.Contains(i)).Any(kv => kv.Key.Inventory is CharacterInventory ci && ci.SlotTypes[kv.Key.Slot] != InvSlotType.Any) ? 0 : 1))
            {
                var destinations = planned.Where(kv => kv.Value.Contains(item)).Select(kv => kv.Key).ToList();
                if (destinations.Count == 0) throw new InvalidOperationException("planned item has no carried destination");
                var first = destinations.FirstOrDefault(c => c.Inventory is CharacterInventory ci && ci.SlotTypes[c.Slot] != InvSlotType.Any);
                if (first.Inventory == null) first = destinations[0];
                bool put;
                if (first.Inventory is CharacterInventory characterInventory && characterInventory.SlotTypes[first.Slot] != InvSlotType.Any)
                {
                    // The indexed native overload can choose a larger alternative equipment mask.
                    // Give it precisely the already-reserved mask, e.g. both hands for a railgun shell.
                    var mask = destinations.Aggregate(InvSlotType.None, (value, cell) => value | characterInventory.SlotTypes[cell.Slot]);
                    if (destinations.Any(cell => cell.Inventory.GetItemsAt(cell.Slot).Any(other => other != item)))
                        throw new InvalidOperationException("planned equipment slots became occupied before insertion");
                    put = characterInventory.TryPutItem(item, bot, new[] { mask }, createNetworkEvent: false, triggerOnInsertedEffects: false);
                }
                else put = first.Inventory.TryPutItem(item, first.Slot, allowSwapping: false, allowCombine: false, user: bot,
                    createNetworkEvent: false, triggerOnInsertedEffects: false);
                if (!put
                    || item.ParentInventory != first.Inventory || !destinations.All(c => c.Inventory.GetItemsAt(c.Slot).Contains(item)))
                    throw new InvalidOperationException("native inventory rejected " + ItemQueries.InstanceLabel(item));
            }
            if (planned.Any(kv => !kv.Value.ToHashSet().SetEquals(kv.Key.Inventory.GetItemsAt(kv.Key.Slot))))
                throw new InvalidOperationException("native placement did not match the planned slot layout");
            foreach (var inventory in inventories) inventory.CreateNetworkEvent();
            return true;
        }
        catch (Exception e)
        {
            var restorationErrors = new List<string>();
            void RestoreStep(string label, Action action)
            {
                try { action(); }
                catch (Exception restoreError)
                {
                    restorationErrors.Add(label + ": " + restoreError.Message);
                    Log.Warn("inventory loadout rollback " + label + ": " + restoreError);
                }
            }
            foreach (var item in changed.Where(i => !i.Removed))
            {
                // Continue restoring other items even if a mod's Unequip or OnRemoved callback throws.
                RestoreStep("detach #" + item.ID, () => DetachLoadoutItem(bot, item));
                var current = item.ParentInventory;
                var cleanupInventories = current == null ? inventories : inventories.Concat(new[] { current }).Distinct();
                foreach (var inventory in cleanupInventories)
                    for (int slot = 0; slot < inventory.Capacity; slot++)
                    {
                        int index = slot;
                        RestoreStep("clear #" + item.ID + " from slot " + index, () =>
                        {
                            if (inventory.GetItemsAt(index).Contains(item)) inventory.ForceRemoveFromSlot(item, index);
                        });
                    }
                RestoreStep("clear ownership #" + item.ID, () => { item.ParentInventory = null; item.Container = null; });
            }
            // Exact snapshot restoration is deliberately restricted to rollback, including duplicate hand
            // references to one two-handed item. Normal placement always respects native slot constraints.
            foreach (var entry in original)
                foreach (var item in entry.Value.Where(i => changedSet.Contains(i) && !i.Removed))
                    RestoreStep("slot #" + item.ID, () =>
                    {
                        if (!entry.Key.Inventory.GetItemsAt(entry.Key.Slot).Contains(item))
                            entry.Key.Inventory.ForceToSlot(item, entry.Key.Slot);
                    });
            foreach (var item in changed.Where(i => !i.Removed))
            {
                var source = original.FirstOrDefault(entry => entry.Value.Contains(item)).Key.Inventory;
                if (source?.Owner is Item holder && item.ParentInventory == source)
                    RestoreStep("container state #" + item.ID, () =>
                    {
                        var component = holder.GetComponents<ItemContainer>().FirstOrDefault(ic => ic.Inventory == source)
                            ?? throw new InvalidOperationException("original container component disappeared");
                        // ForceToSlot restores identity and ownership only. Rebuild the native contained-item
                        // caches without applying OnInserted effects for a second time.
                        component.OnItemContained(item, triggerOnInsertedEffects: false);
                    });
                RestoreStep("inserted state #" + item.ID, () => item.OnInsertedEffectsApplied = insertedEffectsApplied[item]);
                if (wasEquipped[item] && item.ParentInventory == bot.Inventory)
                    RestoreStep("equipment #" + item.ID, () => item.Equip(bot));
            }
            bool restored = false;
            RestoreStep("verify original layout", () =>
            {
                restored = original.All(entry => entry.Value.Where(item => !item.Removed).ToHashSet()
                    .SetEquals(entry.Key.Inventory.GetItemsAt(entry.Key.Slot).Where(item => !item.Removed)))
                    && changed.Where(item => !item.Removed).All(item =>
                    {
                        var locations = original.Where(entry => entry.Value.Contains(item)).Select(entry => entry.Key.Inventory).Distinct().ToList();
                        return locations.Count == 1 && item.ParentInventory == locations[0]
                            && item.Container == locations[0].Owner as Item && item.GetRootInventoryOwner() == bot;
                    });
            });
            foreach (var inventory in inventories) RestoreStep("network state", inventory.CreateNetworkEvent);
            int removed = original.Values.SelectMany(items => items).Distinct().Count(item => item.Removed);
            error = e.Message
                + (removed > 0 ? $"; native item effects removed {removed} item(s); removed items cannot be restored" : "")
                + (restored ? removed > 0 ? "; remaining original item locations restored" : "; original item locations restored"
                    : "; rollback is incomplete: some original item locations could not be restored")
                + (restorationErrors.Count > 0 ? "; native rollback reported: " + Tools.Clip(string.Join("; ", restorationErrors.Take(3)), 300) : "");
            return false;
        }
    }

    static void DetachLoadoutItem(Character bot, Item item)
    {
        try { item.Unequip(bot); }
        finally
        {
            try { item.ParentInventory?.RemoveItem(item); }
            finally
            {
                // ParentInventory=null deliberately leaves Item.Container unchanged in the native game.
                // Clear that old link after its normal removal callback, or the subsequent native PutItem
                // calls Drop and invokes OnRemoved on the previous container a second time.
                if (item.ParentInventory == null) item.Container = null;
            }
        }
    }
}
