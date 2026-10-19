using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Barotrauma;
using Barotrauma.Items.Components;
using LlmAgents.Agents;
using Microsoft.Xna.Framework;

namespace LlmAgents;

public sealed partial class GameAdapter
{
    static readonly JsonSerializerOptions InventoryJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static bool InInventoryScope(Character bot, Entity target) => target == bot
        || target is Item item && item.GetRootInventoryOwner() == bot
        || bot.Submarine != null && target.Submarine == bot.Submarine
        || Vector2.DistanceSquared(bot.WorldPosition, target.WorldPosition) <= ThreatRadiusMeters * ThreatRadiusMeters * UnitsPerMeter * UnitsPerMeter;

    static Entity? ExactInventoryTarget(Character bot, string? target)
    {
        var value = target?.Trim() ?? "self";
        if (value is "" or "self") return bot;
        bool character = value.StartsWith("[c", StringComparison.Ordinal) && value.EndsWith("]", StringComparison.Ordinal);
        var idText = character ? value.Substring(2, value.Length - 3) : value.StartsWith("#", StringComparison.Ordinal) ? value.Substring(1) : "";
        if (!ushort.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return null;
        var entity = Entity.FindEntityByID(id);
        return entity != null && !entity.Removed && (character ? entity is Character : entity is not Character) && InInventoryScope(bot, entity) ? entity : null;
    }

    static Entity? ExactWorldTarget(Character bot, string target)
    {
        var value = target.Trim();
        if (value == "self") return bot;
        char kind = value.StartsWith("#", StringComparison.Ordinal) ? '#' : value.Length >= 4 && value[0] == '[' && value[^1] == ']' ? value[1] : '\0';
        var number = kind == '#' ? value.Substring(1) : kind is 'c' or 'h' or 's' or 'e' ? value.Substring(2, value.Length - 3) : "";
        if (!ushort.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return null;
        var entity = Entity.FindEntityByID(id);
        bool correctKind = kind switch { '#' => entity is Item, 'c' => entity is Character, 'h' => entity is Hull, 's' => entity is Submarine, 'e' => entity != null && entity is not (Item or Character or Hull or Submarine), _ => false };
        return correctKind && entity != null && !entity.Removed ? entity : null;
    }

    internal static string InventoryOwnerRef(Entity owner) => owner switch
    {
        Character c => $"[c{c.ID}]", Item item => $"#{item.ID}", Hull h => $"[h{h.ID}]", Submarine s => $"[s{s.ID}]", _ => $"[e{owner.ID}]",
    };

    /// <summary>Check every compartment along the containment chain; access to a child cannot bypass a locked parent.</summary>
    internal static string? InventoryAccessProblem(Character bot, Inventory inventory, bool requireRange = true, bool requireDragAndDrop = false)
    {
        var seen = new HashSet<Inventory>();
        for (var current = inventory; current != null; current = (current.Owner as Item)?.ParentInventory)
        {
            if (!seen.Add(current)) return "invalid containment cycle";
            if (current.Locked) return $"inventory {InventoryOwnerRef(current.Owner)} is locked";
            if (current.Owner is Item holder)
            {
                if (holder.Removed || ItemQueries.IsIgnored(holder)) return $"{InventoryOwnerRef(holder)} is gone or ordered ignored";
                var component = holder.GetComponents<ItemContainer>().FirstOrDefault(ic => ic.Inventory == current);
                if (component != null)
                {
                    if (!component.IsAccessible()) return $"container {InventoryOwnerRef(holder)} does not currently allow access";
                    if (!component.HasRequiredItems(bot, false)) return $"required access items are missing for {InventoryOwnerRef(holder)}";
                    if (requireDragAndDrop && !component.AllowDragAndDrop) return $"container {InventoryOwnerRef(holder)} does not allow drag-and-drop transfers";
                }
            }
            else if (current.Owner is Character character && !character.IsInventoryAccessibleTo(bot, CharacterInventory.AccessLevel.AllowBotsAndPets))
                return $"character inventory {InventoryOwnerRef(character)} is private or inaccessible";
            if (requireRange && !bot.CanAccessInventory(current)) return $"not in interaction range of {InventoryOwnerRef(current.Owner)}; use approach_object(target='{InventoryOwnerRef(current.Owner)}')";
        }
        return null;
    }

    internal static string? InventoryDestinationProblem(Character bot, Inventory inventory, int? slot = null, bool requireRange = true)
    {
        var problem = InventoryAccessProblem(bot, inventory, requireRange, requireDragAndDrop: true);
        return problem != null && InventoryView.AllowsGiving(bot, inventory, slot, requireRange) ? null : problem;
    }

    public string InspectInventory(int botId, string? target = null, int depth = 4)
    {
        if (Find(botId) is not { } bot) return "error: character gone";
        if (depth is < 0 or > 8) return "error: depth must be between 0 and 8";
        var entity = ExactInventoryTarget(bot, target);
        if (entity == null) return "error: target must be self, an existing exact #item ID, or [cID] in the observable area";
        var inventories = entity switch
        {
            Character character when character.Inventory != null => new[] { (Index: (int?)null, Inventory: (Inventory)character.Inventory, Component: (ItemContainer?)null) },
            Item item => item.GetComponents<ItemContainer>().Select((ic, index) => (Index: (int?)index, Inventory: (Inventory)ic.Inventory, Component: (ItemContainer?)ic)).ToArray(),
            _ => Array.Empty<(int? Index, Inventory Inventory, ItemContainer? Component)>(),
        };
        if (inventories.Length == 0) return $"error: {InventoryOwnerRef(entity)} has no inventory compartments";
        var visited = new HashSet<Inventory>();
        int itemBudget = 2048;
        var result = new JsonObject
        {
            ["target"] = InventoryOwnerRef(entity),
            ["name"] = entity is Character c ? c.Name : ((Item)entity).Name,
            ["reference_lifetime"] = "current round only",
            ["indexing"] = "container_index and slot are zero-based; repeated #IDs in hand slots are one two-handed item",
            ["interaction"] = "transfer_item(item='#ID',target='self|#container|[cID]',count=1,container_index=?,slot=?). Omit slot for normal placement; occupied contents are never replaced or dropped. Inspect inaccessible contents after approach_object.",
            ["inventories"] = new JsonArray(inventories.Select(i => (JsonNode)DescribeInventoryTree(bot, i.Inventory, i.Component, i.Index, depth, visited, ref itemBudget)).ToArray()),
        };
        return result.ToJsonString(InventoryJson);
    }

    static JsonObject DescribeInventoryTree(Character bot, Inventory inventory, ItemContainer? container, int? componentIndex,
        int depth, HashSet<Inventory> visited, ref int budget)
    {
        var node = new JsonObject { ["owner"] = InventoryOwnerRef(inventory.Owner), ["container_index"] = componentIndex, ["capacity"] = inventory.Capacity };
        if (!visited.Add(inventory)) { node["already_described"] = true; return node; }
        var access = InventoryAccessProblem(bot, inventory);
        node["accessible_now"] = access == null;
        node["access_problem"] = access;
        if (container != null)
        {
            node["role"] = inventory.Owner is Item holder && holder.GetComponent<Fabricator>() is { } fab
                ? fab.InputContainer == container ? "fabricator_input" : fab.OutputContainer == container ? "fabricator_output" : "inventory"
                : inventory.Owner is Item machine && machine.GetComponent<Deconstructor>() is { } decon
                    ? decon.InputContainer == container ? "deconstructor_input" : decon.OutputContainer == container ? "deconstructor_output" : "inventory" : "inventory";
            node["max_stack_size"] = container.MaxStackSize;
            node["allow_access"] = container.AllowAccess;
            node["allow_drag_and_drop"] = container.AllowDragAndDrop;
            node["accepts"] = DescribeContainment(container.ContainableItems);
            node["containable_restrictions"] = container.ContainableRestrictions;
        }
        var slots = new JsonArray();
        node["slots"] = slots;
        for (int index = 0; index < inventory.Capacity; index++)
        {
            var slot = new JsonObject { ["slot"] = index, ["slot_type"] = inventory is CharacterInventory ci && index < ci.SlotTypes.Length ? ci.SlotTypes[index].ToString() : "container" };
            slots.Add(slot);
            if (container != null)
            {
                slot["max_stack_size"] = container.GetMaxStackSize(index);
                if (!container.slotRestrictions.IsDefault && index < container.slotRestrictions.Length)
                    slot["accepts"] = DescribeContainment(container.slotRestrictions[index].ContainableItems);
            }
            if (access != null) { slot["contents_hidden"] = true; continue; }
            var items = inventory.GetItemsAt(index).Where(i => !i.Removed).Distinct().ToList();
            slot["count"] = items.Count;
            var itemNodes = new JsonArray();
            slot["items"] = itemNodes;
            foreach (var item in items)
            {
                if (budget-- <= 0) { node["truncated"] = "item limit reached; inspect a specific nested #ID"; return node; }
                var itemNode = new JsonObject
                {
                    ["id"] = $"#{item.ID}", ["name"] = item.Name, ["prefab"] = item.Prefab.Identifier.Value,
                    ["condition"] = item.Condition, ["max_condition"] = item.MaxCondition,
                    ["condition_percent"] = item.MaxCondition > 0 ? Math.Round(item.Condition / item.MaxCondition * 100, 2) : (double?)null,
                    ["max_stack_in_this_inventory"] = item.Prefab.GetMaxStackSize(inventory),
                    ["equipped"] = inventory.Owner is Character owner && owner.HasEquippedItem(item),
                    ["equipped_as_intended"] = inventory.Owner is Character wearer && InventoryView.EquippedAsIntended(wearer, item),
                    ["allowed_slot_combinations"] = new JsonArray(item.AllowedSlots.Select(allowed => (JsonNode?)JsonValue.Create(allowed.ToString())).ToArray()),
                    ["parent"] = InventoryOwnerRef(inventory.Owner), ["parent_container_index"] = componentIndex,
                };
                itemNodes.Add(itemNode);
                var nested = item.GetComponents<ItemContainer>().ToList();
                if (nested.Count == 0) continue;
                if (depth == 0) { itemNode["nested_compartments"] = nested.Count; itemNode["depth_limit"] = true; continue; }
                var children = new JsonArray();
                itemNode["inventories"] = children;
                for (int compartment = 0; compartment < nested.Count; compartment++)
                    children.Add(DescribeInventoryTree(bot, nested[compartment].Inventory, nested[compartment], compartment, depth - 1, visited, ref budget));
            }
        }
        return node;
    }

    static JsonArray DescribeContainment(IEnumerable<RelatedItem>? rules) => new((rules ?? Enumerable.Empty<RelatedItem>()).Select(rule => (JsonNode)new JsonObject
    {
        ["identifiers_or_tags"] = new JsonArray(rule.Identifiers.Select(id => (JsonNode?)JsonValue.Create(id.Value)).ToArray()),
        ["excluded_identifiers_or_tags"] = new JsonArray(rule.ExcludedIdentifiers.Select(id => (JsonNode?)JsonValue.Create(id.Value)).ToArray()),
        ["allow_variants"] = rule.AllowVariants, ["exclude_broken"] = rule.ExcludeBroken, ["exclude_full_condition"] = rule.ExcludeFullCondition,
    }).ToArray());

    public string TransferItem(int botId, string item, string target, int count = 1, int? containerIndex = null, int? slot = null)
    {
        if (Find(botId) is not { } bot || !Ai(bot, out var ai)) return "error: character gone";
        if (InventoryTransferRules.ValidateCount(item, count, MaxMoveStack) is { } countError) return "error: " + countError;
        if (containerIndex < 0 || slot < 0) return "error: container_index and slot must be nonnegative zero-based indexes";
        var destination = ExactInventoryTarget(bot, target);
        if (destination is not (Character or Item)) return "error: target must be self, an exact #container ID, or [cID]";
        if (destination is Item ignored && ItemQueries.IsIgnored(ignored)) return "error: destination is ordered ignored";
        var claimed = ClaimedByOthers(bot);
        var candidates = Item.ItemList.Where(i => !i.Removed && InInventoryScope(bot, i) && !claimed.Contains(i)
                && ItemQueries.MatchScore(i, item) == 3 && !ItemQueries.IsIgnored(i)
                && (i.GetRootInventoryOwner() == bot || i.GetComponent<Pickable>() != null && (i.ParentInventory != null || i.body != null))
                && (i.ParentInventory == null || InventoryAccessProblem(bot, i.ParentInventory, requireRange: false) == null))
            .OrderBy(i => i.ParentInventory?.Owner == destination ? 1 : 0)
            .ThenByDescending(i => i.GetRootInventoryOwner() == bot).ThenBy(i => Vector2.DistanceSquared(bot.WorldPosition, i.WorldPosition)).ToList();
        if (candidates.Count == 0) return $"error: no accessible movable item '{item}'; inspect_inventory exposes exact instance IDs";
        var selected = candidates.Where(i => i.Prefab == candidates[0].Prefab).Take(count).ToList();
        if (selected.Count < count) return $"error: requested {count} but only {selected.Count} accessible instances found; no transfer started";
        Inventory? inventory;
        ItemContainer? component = null;
        if (destination is Character character)
        {
            if (containerIndex != null) return "error: character inventory has no container_index; omit it";
            inventory = character.Inventory;
        }
        else
        {
            var compartments = ((Item)destination).GetComponents<ItemContainer>().ToList();
            if (containerIndex is { } explicitIndex)
            {
                if (explicitIndex >= compartments.Count) return $"error: container_index out of range; {target} has {compartments.Count} compartments";
                component = compartments[explicitIndex];
            }
            else
            {
                var suitable = compartments.Where(ic => (slot == null || slot < ic.Capacity) && selected.All(i => slot is { } s ? ic.CanBeContained(i, s) : ic.CanBeContained(i))).ToList();
                if (suitable.Count > 1) return "error: multiple compatible compartments; inspect_inventory and specify container_index";
                component = suitable.SingleOrDefault();
            }
            inventory = component?.Inventory;
        }
        if (inventory == null) return "error: no compatible inventory compartment at destination";
        if (slot >= inventory.Capacity) return $"error: slot out of range; capacity is {inventory.Capacity}";
        if (InventoryDestinationProblem(bot, inventory, slot, requireRange: false) is { } accessError) return "error: " + accessError;
        if (selected.Any(i => InventoryTransferRules.WouldCreateCycle(i, destination as Item, i => i.Container))) return "error: cannot move a container into itself or its descendants";
        if (selected.Any(i => i.HasTag(Tags.HeavyDivingGear) && bot.HasEquippedItem(i) && GearLock.IsLocked(bot) && !PlayerSaidSo(bot)
            && !(inventory == bot.Inventory && slot is { } s && bot.Inventory.SlotTypes[s].HasFlag(InvSlotType.OuterClothes))))
            return "error: diving gear is locked (danger or keep_gear)";
        if (selected.Any(i => i.ParentInventory != null && InventoryAccessProblem(bot, i.ParentInventory, requireRange: false, requireDragAndDrop: true) != null))
            return "error: source inventory does not allow explicit transfers; inspect_inventory for its access rules";
        if (selected.Any(i => slot is { } s ? !inventory.CanBePutInSlot(i, s) : !InventoryView.AutomaticSlots(inventory, InventoryView.GivingOnly(bot, inventory)).Any(index => inventory.CanBePutInSlot(i, index)))
            && !selected.All(i => i.ParentInventory == inventory && (slot == null || inventory.GetItemsAt(slot.Value).Contains(i))))
            return "error: destination is full or the selected slot/item is incompatible; no transfer started";
        return StartTask(bot, new TransferItemTask(bot, ai, selected, inventory, component, slot, PlayerSaidSo(bot)), evenOverPlayerOrders: true);
    }

    public string ApproachObject(int botId, string target)
    {
        if (Find(botId) is not { } bot || !Ai(bot, out var ai)) return "error: character gone";
        var entity = ExactWorldTarget(bot, target);
        if (entity is not ISpatialEntity) return "error: no observable spatial object at that exact #ID, [cID], [hID], [sID], or [eID]";
        return StartTask(bot, new ApproachObjectTask(bot, ai, entity), evenOverPlayerOrders: true);
    }
}
