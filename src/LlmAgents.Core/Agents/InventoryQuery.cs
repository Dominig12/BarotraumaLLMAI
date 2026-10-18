using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LlmAgents.Agents;

public enum ItemConditionFilter { Any, Empty, Low, Used }

/// <summary>Instance references and charge qualifiers shared by inventory searches and their reports.</summary>
public readonly record struct InventoryQuery(string Text, ItemConditionFilter Condition, int? EntityId)
{
    static readonly Regex Instance = new(@"#(?<id>\d+)\b", RegexOptions.CultureInvariant);
    static readonly Regex Low = new(@"\b(?:almost\s+empty|nearly\s+empty|low(?:\s+(?:charge|condition))?|partially\s+used|почти\s+пуст\p{L}*|мало(?:заряж\p{L}*|\s+патрон\p{L}*)|заканчива\p{L}*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Empty = new(@"\b(?:empty|spent|depleted|exhausted|пуст\p{L}*|опустош\p{L}*|разряж\p{L}*|отработан\p{L}*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Used = new(@"\b(?:used|damaged|неполн\p{L}*|израсходован\p{L}*|поврежден\p{L}*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static InventoryQuery Parse(string query)
    {
        int? entityId = null;
        var instance = Instance.Match(query);
        if (instance.Success && int.TryParse(instance.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) entityId = id;
        var text = Instance.Replace(query, " ");
        var condition = Low.IsMatch(text) ? ItemConditionFilter.Low : Empty.IsMatch(text) ? ItemConditionFilter.Empty : Used.IsMatch(text) ? ItemConditionFilter.Used : ItemConditionFilter.Any;
        text = Used.Replace(Empty.Replace(Low.Replace(text, " "), " "), " ").Trim();
        // "boxes" is the packaging, while the group is ammunition. Keep specific names such as "coilgun".
        if (condition != ItemConditionFilter.Any && Regex.IsMatch(text, @"боеприпас|патрон|\bammo(?:\b|unition)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            text = Regex.Replace(text, @"\b(?:ящик\p{L}*|короб\p{L}*|boxes|box|crates|crate)\b", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
        return new InventoryQuery(text, condition, entityId);
    }

    public bool MatchesCondition(float condition, float maximum)
    {
        if (Condition == ItemConditionFilter.Any) return true;
        if (maximum <= 0 || float.IsNaN(condition) || float.IsNaN(maximum)) return false;
        var fraction = condition / maximum;
        return Condition switch
        {
            ItemConditionFilter.Empty => fraction <= 0.001f,
            ItemConditionFilter.Low => fraction <= 0.3f,
            ItemConditionFilter.Used => fraction < 0.995f,
            _ => true,
        };
    }
}
