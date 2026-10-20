using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LlmAgents.Agents;

/// <summary>Round-local identities of items actually transferred from a body, never item names or container membership.</summary>
public sealed class LootProvenanceLedger<TActor, TSource, TItem>
    where TActor : class where TSource : class where TItem : class
{
    readonly Dictionary<TItem, (TActor Actor, TSource Source)> origins = new(ReferenceEqualityComparer.Instance);

    public void Record(TActor actor, TSource source, IEnumerable<TItem> transferred)
    {
        foreach (var item in transferred) origins[item] = (actor, source);
    }

    public bool BelongsTo(TActor actor, TSource source, TItem item) => origins.TryGetValue(item, out var origin)
        && ReferenceEquals(origin.Actor, actor) && ReferenceEquals(origin.Source, source);

    public bool InScope(TActor actor, TSource source, TItem item, object? currentOwner) => ReferenceEquals(currentOwner, source)
        || ReferenceEquals(currentOwner, actor) && BelongsTo(actor, source, item);

    public void Prune(Func<TActor, TSource, TItem, bool> keep)
    {
        var expired = new List<TItem>();
        foreach (var pair in origins)
            if (!keep(pair.Value.Actor, pair.Value.Source, pair.Key)) expired.Add(pair.Key);
        foreach (var item in expired) origins.Remove(item);
    }

    public void Clear() => origins.Clear();
}

/// <summary>Destructive batches need an explicit scope; a bot's mixed inventory is never an implicit scrap pile.</summary>
public static class DestructiveSelectionRules
{
    public static bool StackWithinScope<T>(IEnumerable<T> stack, ISet<T>? scope) => scope == null || stack.All(scope.Contains);

    public static bool AllowsItem(bool carriedByActor, string query, string? from, string? mark)
    {
        if (!carriedByActor || mark == "deconstruct" || ExactItem(query.Trim())) return true;
        var source = from?.Trim();
        return source != null && (ExactItem(source) || source.StartsWith("[c", StringComparison.Ordinal)
            && source.EndsWith("]", StringComparison.Ordinal)
            && ushort.TryParse(source.Substring(2, source.Length - 3), NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }

    static bool ExactItem(string value) => value.StartsWith("#", StringComparison.Ordinal)
        && ushort.TryParse(value.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out _);

    public static string? ValidateBatch(bool all, string? from, string? mark) => all && from?.Trim() == "self" && mark != "deconstruct"
        ? "wildcard deconstruction from self would include your own supplies; use from='[cID]' for that body's recorded loot, exact item #IDs, or explicitly mark the selected scrap and use mark='deconstruct'"
        : null;
}
