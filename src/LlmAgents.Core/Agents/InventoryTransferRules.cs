using System;
using System.Collections.Generic;

namespace LlmAgents.Agents;

/// <summary>Inventory identity and ownership rules, independent of game entities.</summary>
public static class InventoryTransferRules
{
    public static string? ValidateCount(string item, int count, int maximum)
    {
        if (string.IsNullOrWhiteSpace(item) || item.Trim() == "*") return "item must name an exact #ID, prefab identifier, or copied item name; wildcard is not allowed";
        if (count < 1 || count > maximum) return $"count must be between 1 and {maximum}";
        var reference = InventoryQuery.Parse(item);
        if (reference.EntityId is < 0) return "invalid item reference";
        if (reference.EntityId.HasValue && count != 1) return "an exact #ID selects one instance; count must be 1";
        return null;
    }

    /// <summary>Moving a container into itself or a descendant is invalid; corrupt pre-existing cycles fail closed.</summary>
    public static bool WouldCreateCycle<T>(T moving, T? destinationOwner, Func<T, T?> parent) where T : class
    {
        var visited = new HashSet<T>();
        for (var owner = destinationOwner; owner != null; owner = parent(owner))
            if (ReferenceEquals(owner, moving) || !visited.Add(owner)) return true;
        return false;
    }
}
