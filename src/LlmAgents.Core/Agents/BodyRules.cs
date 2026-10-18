using System;
using System.Text.RegularExpressions;

namespace LlmAgents.Agents;

/// <summary>Body references distinguish corpses of the same species without relying on the live crew roster.</summary>
public static class BodyRules
{
    static readonly Regex Reference = new(@"\[c(\d+)\]|^c(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static int MatchScore(string query, int id, string name, string species)
    {
        var q = query.Trim();
        var reference = Reference.Match(q);
        if (reference.Success)
            return int.TryParse(reference.Groups[1].Success ? reference.Groups[1].Value : reference.Groups[2].Value, out var wanted) && wanted == id ? 4 : 0;
        if (q.ToLowerInvariant() is "" or "all" or "nearest" or "body" or "bodies" or "corpse" or "corpses" or "тело" or "труп" or "трупы" or "ближайший") return 1;
        if (q.Equals(name, StringComparison.OrdinalIgnoreCase) || q.Equals(species, StringComparison.OrdinalIgnoreCase)) return 3;
        if (q.Length < 3) return 0;
        return name.Contains(q, StringComparison.OrdinalIgnoreCase) || species.Contains(q, StringComparison.OrdinalIgnoreCase) ? 2 : 0;
    }
}
