using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LlmAgents.Agents;

/// <summary>Kinds of items a player or a model names without a specific item ("медикаменты", "оружие", "diving gear").</summary>
public enum ItemGroup { Weapon, Ammo, Medical, Tool, DivingGear, DivingSuit, DivingMask, Oxygen, ReactorFuel, Extinguisher, Battery, Material }

/// <summary>
/// Everyday words for items, so queries in plain words find items whose names do not contain them: "медикаменты",
/// "водолазный костюм" (the item is called "Водолазный скафандр"), "инструменты", "патроны". Word forms are matched by
/// stem, so "скафандры", "водолазное" and "водолазный" all count.
/// </summary>
public static class ItemWords
{
    enum W { Filler, Group, Diving, Suit, Costume, Mask, Weld }

    // stem -> meaning; stems are compared with StartsWith on lowercased words ("ё" -> "е")
    static readonly (string Stem, W Kind, ItemGroup Group)[] Stems =
    {
        ("оруж", W.Group, ItemGroup.Weapon), ("вооруж", W.Group, ItemGroup.Weapon), ("weapon", W.Group, ItemGroup.Weapon),
        ("патрон", W.Group, ItemGroup.Ammo), ("боеприпас", W.Group, ItemGroup.Ammo), ("боезапас", W.Group, ItemGroup.Ammo),
        ("снаряд", W.Group, ItemGroup.Ammo), ("ammo", W.Group, ItemGroup.Ammo), ("ammunition", W.Group, ItemGroup.Ammo),
        ("медикамент", W.Group, ItemGroup.Medical), ("лекарств", W.Group, ItemGroup.Medical), ("медицин", W.Group, ItemGroup.Medical),
        ("аптечк", W.Group, ItemGroup.Medical), ("медпрепарат", W.Group, ItemGroup.Medical), ("препарат", W.Group, ItemGroup.Medical),
        ("лечебн", W.Group, ItemGroup.Medical), ("medicine", W.Group, ItemGroup.Medical), ("medical", W.Group, ItemGroup.Medical),
        ("meds", W.Group, ItemGroup.Medical), ("medkit", W.Group, ItemGroup.Medical), ("drugs", W.Group, ItemGroup.Medical),
        ("инструмент", W.Group, ItemGroup.Tool), ("tool", W.Group, ItemGroup.Tool),
        ("кислород", W.Group, ItemGroup.Oxygen), ("баллон", W.Group, ItemGroup.Oxygen), ("oxygen", W.Group, ItemGroup.Oxygen),
        ("топлив", W.Group, ItemGroup.ReactorFuel), ("стерж", W.Group, ItemGroup.ReactorFuel), ("fuel", W.Group, ItemGroup.ReactorFuel),
        ("огнетушит", W.Group, ItemGroup.Extinguisher), ("extinguisher", W.Group, ItemGroup.Extinguisher),
        ("батаре", W.Group, ItemGroup.Battery), ("аккумулятор", W.Group, ItemGroup.Battery), ("batter", W.Group, ItemGroup.Battery),
        ("материал", W.Group, ItemGroup.Material), ("material", W.Group, ItemGroup.Material), ("сырь", W.Group, ItemGroup.Material),
        ("водолаз", W.Diving, default), ("подводн", W.Diving, default), ("diving", W.Diving, default), ("dive", W.Diving, default),
        ("scuba", W.Diving, default),
        ("скафандр", W.Suit, default), ("suit", W.Suit, default), ("костюм", W.Costume, default),
        ("маск", W.Mask, default), ("mask", W.Mask, default),
        ("свароч", W.Weld, default), ("weld", W.Weld, default),
    };

    // words that say nothing about which item ("все запасные", "some spare", "водолазное снаряжение")
    static readonly string[] FillerStems =
    {
        "снаряжени", "экипировк", "equipment", "предмет", "набор", "комплект", "запасн", "лишн", "немног", "нескольк",
        "штук", "побольше", "какие", "какой", "каждо",
    };
    static readonly HashSet<string> FillerWords = new(StringComparer.Ordinal)
    {
        "gear", "item", "items", "stuff", "things", "вещи", "вещь", "все", "весь", "вся", "всех", "еще", "свой", "свои", "свое",
        "себе", "мне", "мой", "мои", "твои", "пару", "для", "из", "на", "под", "и", "с", "со", "в", "во", "к", "от", "по",
        "the", "a", "an", "some", "any", "spare", "extra", "all", "my", "your", "of", "for", "and", "few", "more", "pack",
        "set", "kit", "every", "box", "это", "эти", "тот", "те",
    };

    public static List<string> Words(string text)
    {
        var words = new List<string>();
        var sb = new StringBuilder();
        foreach (var ch0 in text.ToLowerInvariant())
        {
            var ch = ch0 == 'ё' ? 'е' : ch0;
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0) { words.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) words.Add(sb.ToString());
        return words;
    }

    static (W Kind, ItemGroup Group)? Classify(string word)
    {
        foreach (var (stem, kind, group) in Stems)
            if (word.StartsWith(stem, StringComparison.Ordinal)) return (kind, group);
        if (FillerWords.Contains(word)) return (W.Filler, default);
        foreach (var f in FillerStems)
            if (word.StartsWith(f, StringComparison.Ordinal)) return (W.Filler, default);
        return null;
    }

    /// <summary>
    /// The kinds of items a query asks for when it is made of kind words only ("медикаменты", "оружие и патроны",
    /// "водолазный костюм"); empty when any word names something specific ("бинт", "harpoon gun").
    /// </summary>
    public static IReadOnlyList<ItemGroup> Groups(string query)
    {
        var groups = new List<ItemGroup>();
        bool diving = false, suit = false, costume = false, mask = false, weld = false;
        foreach (var w in Words(query))
        {
            var c = Classify(w);
            if (c == null) return Array.Empty<ItemGroup>();
            switch (c.Value.Kind)
            {
                case W.Group: if (!groups.Contains(c.Value.Group)) groups.Add(c.Value.Group); break;
                case W.Diving: diving = true; break;
                case W.Suit: suit = true; break;
                case W.Costume: costume = true; break;
                case W.Mask: mask = true; break;
                case W.Weld: weld = true; break;
            }
        }
        if (weld) return Array.Empty<ItemGroup>();                 // welding fuel, welding tool: the names say it
        if (mask) groups.Insert(0, ItemGroup.DivingMask);
        if (suit || (costume && diving)) groups.Insert(0, ItemGroup.DivingSuit);
        else if (diving && !mask) groups.Insert(0, ItemGroup.DivingGear);
        if (costume && !diving && !suit) return Array.Empty<ItemGroup>(); // just clothes
        return groups;
    }

    /// <summary>"водолазное" -> "водолазн": a stem that still matches other forms of the word.</summary>
    public static string Stem(string word) =>
        word.Length >= 7 ? word.Substring(0, word.Length - 2)
        : word.Length >= 4 && "sыиаяеуоьй".Contains(word[^1]) ? word.Substring(0, word.Length - 1)  // "rods", "ножи"
        : word;

    /// <summary>
    /// Every meaningful word of the query matches a word of one of the names (by stem): "скафандры водолазные" fits
    /// "Водолазный скафандр", "fuel rods" fits "Fuel Rod". Filler words ("все", "spare") are ignored.
    /// </summary>
    public static bool WordsMatch(string query, IEnumerable<string?> names)
    {
        var significant = Words(query).Where(w => Classify(w) is not { Kind: W.Filler }).ToList();
        if (significant.Count == 0) return false;
        var nameWords = names.Where(n => !string.IsNullOrEmpty(n)).SelectMany(n => Words(n!)).Distinct().ToList();
        if (nameWords.Count == 0) return false;
        return significant.All(q =>
        {
            var s = Stem(q);
            return nameWords.Any(n => n.StartsWith(s, StringComparison.Ordinal) || (n.Length >= 4 && q.StartsWith(Stem(n), StringComparison.Ordinal)));
        });
    }

    /// <summary>"Физраствор [antibloodloss1]": the identifier the model copied from an observation.</summary>
    public static string? BracketId(string query)
    {
        int open = query.LastIndexOf('['), close = query.LastIndexOf(']');
        if (open < 0 || close <= open + 1) return null;
        var id = query.Substring(open + 1, close - open - 1).Trim();
        return id.Length == 0 || id.Contains(' ') ? null : id;
    }

    static readonly HashSet<string> SelfWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "me", "self", "myself", "inventory", "my inventory", "own inventory", "себе", "мне", "я", "меня", "к себе",
        "инвентарь", "в инвентарь", "в свой инвентарь", "свой инвентарь", "себя", "мой инвентарь",
    };

    static readonly string[] PlaceStems = { "на место", "на свое место", "на своё место", "по местам", "на места", "куда положено", "куда надо",
        "где лежало", "its place", "where it belongs", "away", "storage", "back", "обратно", "на склад", "убрать", "убери" };

    /// <summary>move_item(to=...) meaning "where it belongs": "на место", "away".</summary>
    public static bool IsItsPlace(string to)
    {
        var t = to.Trim().Trim('.', '!', '"', '\'').ToLowerInvariant().Replace('ё', 'е');
        return PlaceStems.Any(p => t == p.Replace('ё', 'е') || t.StartsWith(p.Replace('ё', 'е') + " ", StringComparison.Ordinal));
    }

    /// <summary>move_item(to=...) meaning the bot itself: "me", "себе", its own name.</summary>
    public static bool IsSelf(string to, string ownName)
    {
        var t = to.Trim().Trim('.', '!', '"', '\'');
        return SelfWords.Contains(t) || t.Equals(ownName, StringComparison.OrdinalIgnoreCase)
            || (t.Length >= 4 && ownName.StartsWith(t, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Which marks a query means: "помеченный ящик", "the marked crate", "помеченное на разбор".</summary>
public enum MarkQuery { None, Ai, Deconstruct }

public static class MarkWords
{
    static readonly string[] MarkStems = { "помеч", "отмеч", "пометк", "отметк", "marked", "mark", "tagged" };
    static readonly string[] DeconstructStems = { "разбор", "разобр", "разбир", "деконстр", "deconstr", "salvag", "scrap" };

    public static MarkQuery Of(string query)
    {
        var words = ItemWords.Words(query);
        if (!words.Any(w => MarkStems.Any(s => w.StartsWith(s, StringComparison.Ordinal)))) return MarkQuery.None;
        return words.Any(w => DeconstructStems.Any(s => w.StartsWith(s, StringComparison.Ordinal))) ? MarkQuery.Deconstruct : MarkQuery.Ai;
    }

    /// <summary>The rest of the query once the mark words are gone: "помеченный ящик" -> "ящик" ("" = any marked item).</summary>
    public static string Rest(string query) =>
        string.Join(" ", ItemWords.Words(query).Where(w =>
            !MarkStems.Any(s => w.StartsWith(s, StringComparison.Ordinal)) && !DeconstructStems.Any(s => w.StartsWith(s, StringComparison.Ordinal))
            && w is not ("for" or "на" or "для" or "the" or "this" or "это" or "этот" or "эту" or "все" or "всё" or "all" or "ии" or "ai")));
}
