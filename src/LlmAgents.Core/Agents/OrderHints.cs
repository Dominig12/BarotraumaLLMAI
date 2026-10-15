using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace LlmAgents.Agents;

/// <summary>
/// Cheap keyword hints for common chat orders, shown to the bot next to the order ("Hint: probably follow(...)").
/// Small local models often ignore a plain "ВСЕ ЗА МНОЙ!"; a concrete tool call next to it helps a lot.
/// The model still decides; hints only cover unambiguous phrases (Russian and English).
/// </summary>
public static class OrderHints
{
    static readonly (Regex Pattern, Func<string, string?, string> Hint)[] Rules =
    {
        (R(@"за\s*мной|follow\s*me|on\s*me\b"), (who, _) => $"follow(target=\"{who}\")"),
        (R(@"ко\s*мне|сюда|come\s*(here|to\s*me)"), (who, room) => room != null ? $"go_to_room(room=\"{room}\")" : $"follow(target=\"{who}\")"),
        (R(@"\bстой|\bстоп|\bжди(те)?\b|\bstop\b|\bhold\s*position|\bstay\b"), (_, _) => "wait()"),
        (R(@"\bвольно\b|\bотбой\b|\bas\s*you\s*were|\bdismiss"), (_, _) => "return_to_default()"),
        (R(@"течь|течи|пробоин|заварит|leak|breach"), (_, _) => "fix_leaks()"),
        (R(@"пожар|туши|огонь|fire"), (_, _) => "extinguish_fires()"),
        (R(@"почин|ремонт|repair|fix\s+(the\s+)?\w+"), (_, _) => "repair_systems()"),
        (R(@"монстр|тварь|убей|атак|враг|fight|kill|intruder"), (_, _) => "fight_intruders()"),
        (R(@"раненых|лечи|вылечи|heal|treat|medic"), (_, _) => "rescue_injured() or treat(target=...)"),
        (R(@"костюм|скафандр|diving\s*suit"), (_, _) => "equip(item=\"diving suit\") and keep_gear(on=true)"),
        (R(@"разбер|разобрать|деконстр|deconstruct|salvage|scrap"), (_, room) => "deconstruct(item=\"all\", from=<container or room>)" + (room != null ? $", e.g. from=\"{room}\" if they mean their room" : "")),
        (R(@"скрафт|сделай|изготов|craft|fabricate|make\s+\w+"), (_, _) => "inspect(kind='recipes') then craft(item=<exact name>, salvage=true)"),
        (R(@"реактор.*(заряд|топлив|стерж)|(заряд|топлив|стерж).*реактор|refuel"), (_, _) => "refuel_reactor()"),
        (R(@"насос|откачай|pump"), (_, _) => "operate(role=\"pumps\", option=\"pumpout\")"),
    };

    static Regex R(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string? Suggest(string text, string sender, string? senderRoom)
    {
        var hints = Rules.Where(r => r.Pattern.IsMatch(text)).Select(r => r.Hint(sender, senderRoom)).Distinct().Take(2).ToList();
        return hints.Count == 0 ? null : string.Join(" + ", hints);
    }
}
