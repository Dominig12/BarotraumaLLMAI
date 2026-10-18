using System;
using System.Collections.Generic;
using System.Linq;

namespace LlmAgents.Agents;

/// <summary>Selection rules shared by inventory tasks without depending on game entities.</summary>
public static class InventoryTaskRules
{
    public static HashSet<T> ReplacementExclusions<T>(T failed, IEnumerable<T> tried, IEnumerable<T> queued, IEnumerable<T> collected) =>
        tried.Concat(queued).Concat(collected).Append(failed).ToHashSet();

    /// <summary>Claims are read when a replacement is selected, since another task may have claimed it after this task started.</summary>
    public static T? SelectReplacement<T>(IEnumerable<T> candidates, T failed, ICollection<T> excluded,
        ICollection<T> claimed, Func<T, bool> eligible) where T : class =>
        candidates.FirstOrDefault(item => item != failed && !excluded.Contains(item) && !claimed.Contains(item) && eligible(item));

}
