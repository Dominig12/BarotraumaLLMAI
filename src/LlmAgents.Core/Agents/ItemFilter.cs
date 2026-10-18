using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace LlmAgents.Agents;

/// <summary>Explicit selection arguments chosen by the model. Query text is never interpreted as these fields.</summary>
public sealed record ItemFilter(string? Category = null, string Condition = "any", float? MinConditionPercent = null,
    float? MaxConditionPercent = null, string Mark = "none", string? LinkedTo = null, string? From = null)
{
    public static readonly string[] Categories = { "weapon", "ammo", "medical", "tool", "diving_gear", "diving_suit", "diving_mask", "oxygen", "reactor_fuel", "extinguisher", "battery", "material" };
    public static readonly string[] Conditions = { "any", "empty", "low", "used" };
    public static readonly string[] Marks = { "none", "ai", "deconstruct" };
    public bool HasCriteria => Category != null || Condition != "any" || MinConditionPercent != null || MaxConditionPercent != null || Mark != "none" || LinkedTo != null || From != null;

    public bool MatchesCondition(float condition, float maximum)
    {
        if (Condition == "any" && MinConditionPercent == null && MaxConditionPercent == null) return true;
        if (maximum <= 0 || !float.IsFinite(maximum) || !float.IsFinite(condition)) return false;
        double percent = (double)condition / maximum * 100;
        if (MinConditionPercent is { } min && percent < min || MaxConditionPercent is { } max && percent > max) return false;
        return Condition switch { "empty" => percent <= 0, "low" => percent <= 30, "used" => percent < 100, "any" => true, _ => false };
    }

    public static (ItemFilter? Filter, string? Error) Read(JsonObject args)
    {
        string? String(string name, string? fallback) => args[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : fallback;
        float? Percent(string name) => args[name] == null ? null
            : float.TryParse(args[name]!.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : float.NaN;
        var category = String("category", null);
        var condition = String("condition", "any")!;
        var mark = String("mark", "none")!;
        var linked = String("linked_to", null);
        var from = String("from", null);
        var min = Percent("min_condition_percent");
        var max = Percent("max_condition_percent");
        foreach (string field in new[] { "category", "condition", "mark", "linked_to", "from" })
            if (args[field] != null && (args[field] is not JsonValue v || !v.TryGetValue<string>(out _)))
                return (null, $"error: '{field}' must be a string");
        if (category != null && !Categories.Contains(category)) return (null, "error: 'category' must be one of " + string.Join(", ", Categories));
        if (!Conditions.Contains(condition)) return (null, "error: 'condition' must be one of " + string.Join(", ", Conditions));
        if (!Marks.Contains(mark)) return (null, "error: 'mark' must be one of " + string.Join(", ", Marks));
        if (min is { } mn && (!float.IsFinite(mn) || mn < 0 || mn > 100) || max is { } mx && (!float.IsFinite(mx) || mx < 0 || mx > 100))
            return (null, "error: condition bounds must be numbers between 0 and 100");
        if (min > max) return (null, "error: 'min_condition_percent' must not exceed 'max_condition_percent'");
        if (linked != null && (!linked.StartsWith("#", StringComparison.Ordinal) || !ushort.TryParse(linked.Substring(1), out var id) || id == 0))
            return (null, "error: 'linked_to' must be an exact machine reference such as '#123' from inspect(kind='machines')");
        return (new ItemFilter(category, condition, min, max, mark, linked, from), null);
    }
}
