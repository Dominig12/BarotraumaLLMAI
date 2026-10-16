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
    const int MinScore = 15;

    static readonly ItemGroup[] Groups = (ItemGroup[])Enum.GetValues(typeof(ItemGroup));

    static HashSet<ItemGroup> GroupsOf(ItemPrefab p) =>
        Groups.Where(g => g != ItemGroup.DivingGear && ItemQueries.InGroup(p, g)).ToHashSet();

    public sealed record Choice(ItemContainer Container, int Score, string Why);

    /// <param name="exclude">containers not to use (the crate being emptied, ones that could not be reached)</param>
    public static Choice? Choose(Character c, Item item, ICollection<Item> exclude)
    {
        var groups = GroupsOf(item.Prefab);
        Choice? best = null;
        float bestValue = float.MinValue;
        foreach (var i in Item.ItemList)
        {
            if (i.Removed || i == item || exclude.Contains(i) || i.Submarine != c.Submarine) continue;
            if (i.GetRootInventoryOwner() is Character || ItemQueries.IsIgnored(i) || !GameAdapter.IsStorage(i)) continue;
            if (IsInside(i, item)) continue;                       // not into itself or its own contents
            var ic = i.GetComponent<ItemContainer>();
            if (ic == null || !ic.Inventory.CanBePut(item)) continue;

            int score; string why;
            var held = ic.Inventory.AllItems.ToList();
            if (held.Any(x => x.Prefab == item.Prefab)) { score = 100; why = "with the same items"; }
            else if (groups.Count > 0 && held.Any(x => GroupsOf(x.Prefab).Overlaps(groups))) { score = 40; why = "with similar items"; }
            else
            {
                bool preferred = item.Prefab.IsContainerPreferred(item, ic, out _, out bool secondary);
                if (preferred && !secondary) { score = 30; why = "its usual place"; }
                else if (preferred) { score = 15; why = "a fitting place"; }
                else continue;
            }
            if (i.GetComponent<Pickable>() != null) score -= 20;     // a crate on the floor: only if nothing fixed fits
            if (score < MinScore) continue;

            float meters = Vector2.Distance(i.WorldPosition, c.WorldPosition) / UnitsPerMeter;
            float value = score - Math.Min(meters * 0.3f, 25f);
            if (value > bestValue) { bestValue = value; best = new Choice(ic, score, why); }
        }
        return best;
    }

    static bool IsInside(Item container, Item item)
    {
        for (var x = container; x != null; x = x.Container)
            if (x == item) return true;
        return false;
    }

    public static string Describe(ItemContainer ic) =>
        $"{ic.Item.Name} in {(ic.Item.CurrentHull != null ? GameAdapter.RoomId(ic.Item.CurrentHull) : "?")}";
}
