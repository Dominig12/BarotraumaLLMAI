using System.Globalization;
using System.Text.RegularExpressions;

namespace LlmAgents.Agents;

/// <summary>Only reference syntax is parsed. Words such as "empty" remain part of the literal item name.</summary>
public readonly record struct InventoryQuery(string Text, int? EntityId)
{
    static readonly Regex Instance = new(@"(?:^|\s)#(?<id>\d+)(?=\s|$)", RegexOptions.CultureInvariant);
    public static InventoryQuery Parse(string query)
    {
        var instance = Instance.Match(query);
        if (!instance.Success) return new InventoryQuery(query.Trim(), null);
        if (Instance.Matches(query).Count != 1) return new InventoryQuery(query.Trim(), -1);
        // An out-of-range ID must never fall back to a broad name search.
        int id = int.TryParse(instance.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
        return new InventoryQuery(Instance.Replace(query, " ").Trim(), id);
    }
}
