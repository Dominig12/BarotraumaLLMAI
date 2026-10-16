using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LlmAgents.Agents;

/// <summary>
/// Which tool groups a request carries. Every schema costs prompt tokens (40 tools are about 6000), and small local
/// models choose worse among many tools, so a bot gets the core set plus what its job, its situation and its orders
/// call for. Tools it used lately stay for a while, and any tool still runs if the model names it.
/// </summary>
public static class ToolSelection
{
    public const double StickySeconds = 180;

    static readonly (Regex Pattern, ToolGroup Group)[] OrderWords =
    {
        (R(@"скрафт|сделай|изготов|craft|fabricat|разбер|разобр|деконстр|deconstruct|salvage|топлив\w*\s+реактор|реактор\w*\s+топлив|refuel"), ToolGroup.Crafting),
        (R(@"леч|ранен|медик|бинт|heal|treat|medic|wound"), ToolGroup.Medical),
        (R(@"убей|атак|стреля|флор|выжг|сожг|kill|attack|shoot|flora|burn|монстр|тварь|ползун|crawler"), ToolGroup.Combat),
        (R(@"разлож|распак|разнес|рассортир|ящик|unpack|crate|помеч|marked"), ToolGroup.Items),
        (R(@"заправ|перезаряд|баллон|вставь|положи\w*\s+в\b|выложи|разгрузи|брось|выбрось|сними|инвентар|карман|refill|reload|drop|unequip|tidy|inventory|put\s+\w+\s+in"), ToolGroup.Inventory),
    };

    static Regex R(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    static readonly Dictionary<string, ToolGroup> JobGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["engineer"] = ToolGroup.Crafting,
        ["mechanic"] = ToolGroup.Crafting | ToolGroup.Combat,      // welding the flora
        ["medicaldoctor"] = ToolGroup.Medical | ToolGroup.Crafting,
        ["securityofficer"] = ToolGroup.Combat,
        ["captain"] = ToolGroup.Combat,
        ["assistant"] = ToolGroup.Crafting,
    };

    /// <summary>Always sent: moving, talking, the vanilla orders and plans. Inventory, crafting, medical, combat,
    /// unpacking and memory come with the job, the situation or the words of an order.</summary>
    public static ToolGroup For(BotAgent agent, Observation obs, IEnumerable<string> orderTexts, double now, bool memory)
    {
        var g = ToolGroup.Core | ToolGroup.Orders | ToolGroup.Plans;
        if (agent.AiControl) return (ToolGroup)~0;   // in charge of everything: every tool
        if (memory) g |= ToolGroup.Memory;
        if (JobGroups.TryGetValue(agent.Job, out var job)) g |= job;
        if (obs.Threats.Count > 0 || obs.Infestations.Count > 0) g |= ToolGroup.Combat;
        if (obs.Injuries != null || obs.Crew.Any(c => c.HealthPercent < 70)) g |= ToolGroup.Medical;
        if (obs.Marks.Count > 0) g |= ToolGroup.Items | ToolGroup.Crafting;
        // pockets nearly full, or the suit's tank is empty: the inventory tools are what it needs
        if (obs.InventorySpace is { } space && (space.StartsWith("0 ") || space.StartsWith("1 ")) || obs.Gear?.Contains("NO oxygen tank") == true)
            g |= ToolGroup.Inventory;
        foreach (var text in orderTexts.Append(agent.Goal ?? ""))
            foreach (var (pattern, group) in OrderWords)
                if (pattern.IsMatch(text)) g |= group;
        g |= agent.StickyGroups(now);
        return g;
    }

    /// <summary>One line for the prompt: the tools this request does not describe, which still work when named.</summary>
    public static string? HiddenLine(IReadOnlyCollection<string> offered, bool aiControl)
    {
        var hidden = Tools.Defs.Where(d => !offered.Contains(d.Name) && (aiControl || !d.ControlOnly)).Select(d => d.Name).ToList();
        return hidden.Count == 0 ? null
            : $"More tools (not described here, they work when called by name; inspect(kind='tools', query=<name>) explains one): {string.Join(", ", hidden)}";
    }
}
