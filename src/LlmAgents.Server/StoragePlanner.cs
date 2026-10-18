using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using LlmAgents.Agents;
using Microsoft.Xna.Framework;

namespace LlmAgents;

/// <summary>
/// Where an item belongs on this sub. What the cabinets hold NOW counts more than what they were built for (crews
/// repurpose them during a campaign), and stacking saves space:
///   1. a container that already holds the same items (it goes onto their stack),
///   2. a container holding items of the same kind (medicine with medicine, ammo with ammo),
///   3. the game's preferred containers for the item (the ones vanilla bots use when they clean up),
/// nearer and fixed (not a crate lying around) is better. Nothing fitting = null: the item stays where it is rather than
/// ending up in a random locker.
/// </summary>
internal static class StoragePlanner
{
    const float UnitsPerMeter = 100f;

    static readonly ItemGroup[] Groups = (ItemGroup[])Enum.GetValues(typeof(ItemGroup));

    static HashSet<ItemGroup> GroupsOf(ItemPrefab p) =>
        Groups.Where(g => g != ItemGroup.DivingGear && ItemQueries.InGroup(p, g)).ToHashSet();

    public sealed record Choice(ItemContainer Container, int Score, string Why);

    /// <param name="exclude">containers not to use (the crate being emptied, ones that could not be reached)</param>
    /// <param name="origin">where distances are measured from (the crate being emptied: the same item goes to the same
    /// place on every trip, however far the bot walked meanwhile); default the bot</param>
    public static Choice? Choose(Character c, Item item, ICollection<Item> exclude, Vector2? origin = null)
    {
        var from = origin ?? c.WorldPosition;
        var groups = GroupsOf(item.Prefab);
        Choice? best = null;
        float bestValue = float.MinValue;
        foreach (var i in Item.ItemList)
        {
            if (i.Removed || i == item || exclude.Contains(i) || i.Submarine != c.Submarine) continue;
            if (i.GetRootInventoryOwner() is Character || ItemQueries.IsIgnored(i) || !GameAdapter.IsStorage(i)) continue;
            if (IsInside(i, item)) continue;                       // not into itself or its own contents
            foreach (var ic in i.GetComponents<ItemContainer>())
            {
                if (!ic.Inventory.CanBePut(item) || GameAdapter.InventoryAccessProblem(c, ic.Inventory, requireRange: false) != null) continue;
                var held = ic.Inventory.AllItems.ToList();
                bool same = held.Any(x => x.Prefab == item.Prefab);
                bool similar = !same && groups.Count > 0 && held.Any(x => GroupsOf(x.Prefab).Overlaps(groups));
                bool preferred = false, secondary = false;
                if (!same && !similar) preferred = item.Prefab.IsContainerPreferred(item, ic, out _, out secondary);
                if (ItemRules.StorageScore(same, similar, preferred, secondary, i.GetComponent<Pickable>() != null) is not { } s) continue;

                float meters = Vector2.Distance(i.WorldPosition, from) / UnitsPerMeter;
                float value = ItemRules.StorageValue(s.Score, meters);
                if (value > bestValue) { bestValue = value; best = new Choice(ic, s.Score, s.Why); }
            }
        }
        return best;
    }

    static bool IsInside(Item container, Item item)
    {
        return InventoryTransferRules.WouldCreateCycle(item, container, x => x.Container);
    }

    public static string Describe(ItemContainer ic) =>
        $"{ItemQueries.InstanceLabel(ic.Item)} compartment {ic.Item.GetComponents<ItemContainer>().ToList().IndexOf(ic)} in {(ic.Item.CurrentHull != null ? GameAdapter.RoomId(ic.Item.CurrentHull) : "?")}";
}
