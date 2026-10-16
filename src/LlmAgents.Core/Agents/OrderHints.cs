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
        // "сюда", not "отсюда" (= "from here")
        (R(@"ко\s*мне|\bсюда\b|come\s*(here|to\s*me)"), (who, _) => $"come_to(target=\"{who}\")"),
        (R(@"\b(возьм|бер[иеё]|хватай|захват|прихват|взять|забер|take\b|grab|pick\s*up|bring)|подготов\w*\s+(медикамент|лекарств|аптечк|инструмент|патрон|боеприпас)"), (who, room) => TakeHint(who, room)),
        (R(@"\bстой|\bстоп|\bжди(те)?\b|\bstop\b|\bhold\s*position|\bstay\b"), (_, _) => "wait()"),
        (R(@"\bвольно\b|\bотбой\b|\bas\s*you\s*were|\bdismiss"), (_, _) => "return_to_default()"),
        (R(@"течь|течи|пробоин|заварит|leak|breach"), (_, _) => "fix_leaks()"),
        (R(@"пожар|туши|огонь|fire\b"), (_, _) => "extinguish_fires()"),
        (R(@"почин|ремонт|repair|fix\s+(the\s+)?\w+"), (_, _) => "repair_systems()"),
        (R(@"пушк|орудий|орудия|турел|\bguns?\b|turret|coilgun|railgun"), (_, _) => "operate(role=\"weapons\")"),
        (R(@"монстр|тварь|убей|атак|враг|fight|kill|intruder"), (_, _) => "fight_intruders()"),
        (R(@"раненых|лечи|вылечи|heal|treat|medic"), (_, _) => "rescue_injured() or treat(target=...)"),
        (R(@"не\s*снима|keep\s*(your\s*)?(gear|suit)"), (_, _) => "keep_gear(on=true)"),
        (R(@"костюм|скафандр|водолаз|diving\s*(suit|gear)|suit\s*up"), (_, _) => "equip(item=\"diving suit\") and keep_gear(on=true)"),
        (R(@"разбер[иу]|разобрать|деконстр|deconstruct|salvage|scrap"), (_, room) => "deconstruct(item=\"all\", from=<container or room>)" + (room != null ? $", e.g. from=\"{room}\" if they mean their room" : "")),
        (R(@"скрафт|сделай|изготов|craft|fabricate|make\s+\w+"), (_, _) => "inspect(kind='recipes') then craft(item=<exact name>, salvage=true)"),
        (R(@"реактор.*(заряд|топлив|стерж)|(заряд|топлив|стерж).*реактор|refuel"), (_, _) => "refuel_reactor()"),
        (R(@"насос|откачай|pump"), (_, _) => "operate(role=\"pumps\", option=\"pumpout\")"),
    };

    static string TakeHint(string who, string? room) =>
        "take_item(item=<what, or a kind: 'medical', 'tools', 'ammo'>, count=<how many>" +
        (room != null ? $", from=\"{room}\" if they mean 'from here'" : ", from=<container or room, if they said where>") + ")";

    static Regex R(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string? Suggest(string text, string sender, string? senderRoom)
    {
        var hints = Rules.Where(r => r.Pattern.IsMatch(text)).Select(r => r.Hint(sender, senderRoom)).Distinct().Take(3).ToList();
        return hints.Count == 0 ? null : string.Join(" + ", hints);
    }
}
