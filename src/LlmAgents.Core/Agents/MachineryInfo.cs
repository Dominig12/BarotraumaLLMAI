using System;
using System.Collections.Generic;

namespace LlmAgents.Agents;

/// <summary>A real station and the directed entity links read from the current submarine.</summary>
public sealed record MachineryInfo(
    int Id, string Name, string Identifier, string Kind, string? Room, bool Active,
    List<MachineryLinkInfo> Links, List<string>? Input = null, List<string>? Output = null, string? Power = null);

/// <summary>FromId -> ToId is the actual entity link direction. An ingredient_source means that
/// the target inventory supplies the source fabricator; proximity alone never creates this relationship.</summary>
public sealed record MachineryLinkInfo(
    int FromId, int ToId, string Name, string Kind, string Relation, bool Accessible,
    string? Inventory = null, List<string>? Contents = null);

/// <summary>Walk the actual source inventories, including nested bags, without double counting
/// an inventory or item reachable through several machine links.</summary>
public static class MachineryRules
{
    public static List<TItem> InventoryTree<TInventory, TItem>(IEnumerable<TInventory> roots,
        Func<TInventory, IEnumerable<TItem>> contents, Func<TItem, TInventory?> nested,
        Func<TItem, bool> include) where TInventory : class where TItem : class
    {
        var pending = new Queue<TInventory>(roots);
        var visited = new HashSet<TInventory>();
        var seenItems = new HashSet<TItem>();
        var result = new List<TItem>();
        while (pending.Count > 0)
        {
            var inventory = pending.Dequeue();
            if (!visited.Add(inventory)) continue;
            foreach (var item in contents(inventory))
            {
                if (!include(item) || !seenItems.Add(item)) continue;
                result.Add(item);
                if (nested(item) is { } next) pending.Enqueue(next);
            }
        }
        return result;
    }
}
