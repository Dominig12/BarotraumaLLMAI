using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LlmAgents.Agents;

/// <summary>Item categories selected through explicit tool arguments.</summary>
public enum ItemGroup { Weapon, Ammo, Medical, Tool, DivingGear, DivingSuit, DivingMask, Oxygen, ReactorFuel, Extinguisher, Battery, Material }

/// <summary>Literal words and copied identifiers. Natural language instructions are interpreted by the LLM.</summary>
public static class ItemWords
{
    public static List<string> Words(string text)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) word.Append(ch);
            else if (word.Length > 0) { words.Add(word.ToString()); word.Clear(); }
        }
        if (word.Length > 0) words.Add(word.ToString());
        return words;
    }

    /// <summary>Canonical category tokens only; never infer a category from a sentence or item name.</summary>
    public static IReadOnlyList<ItemGroup> Groups(string query)
    {
        ItemGroup? group = query.Trim() switch
        {
            "weapon" => ItemGroup.Weapon, "ammo" => ItemGroup.Ammo, "medical" => ItemGroup.Medical,
            "tool" => ItemGroup.Tool, "diving_gear" => ItemGroup.DivingGear, "diving_suit" => ItemGroup.DivingSuit,
            "diving_mask" => ItemGroup.DivingMask, "oxygen" => ItemGroup.Oxygen, "reactor_fuel" => ItemGroup.ReactorFuel,
            "extinguisher" => ItemGroup.Extinguisher, "battery" => ItemGroup.Battery, "material" => ItemGroup.Material,
            _ => null,
        };
        return group is { } category ? new[] { category } : Array.Empty<ItemGroup>();
    }

    /// <summary>Literal word overlap for item labels and plan recall, without stemming or stop-word removal.</summary>
    public static bool WordsMatch(string query, IEnumerable<string?> names)
    {
        var requested = Words(query);
        if (requested.Count == 0) return false;
        var available = names.Where(name => !string.IsNullOrWhiteSpace(name)).SelectMany(name => Words(name!)).ToHashSet(StringComparer.Ordinal);
        return requested.All(available.Contains);
    }

    /// <summary>The identifier copied from a label such as "Физраствор [antibloodloss1]".</summary>
    public static string? BracketId(string query)
    {
        int open = query.LastIndexOf('['), close = query.LastIndexOf(']');
        if (open < 0 || close <= open + 1) return null;
        var id = query.Substring(open + 1, close - open - 1).Trim();
        return id.Length == 0 || id.Any(char.IsWhiteSpace) ? null : id;
    }

    public static bool IsItsPlace(string to) => to.Trim().Equals("storage", StringComparison.Ordinal);

    public static bool IsSelf(string to, string ownName) => to.Trim().Equals("self", StringComparison.Ordinal)
        || to.Trim().Equals(ownName, StringComparison.OrdinalIgnoreCase);

}

public enum MarkQuery { None, Ai, Deconstruct }
