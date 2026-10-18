using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Barotrauma;
using Barotrauma.Items.Components;
using LlmAgents.Agents;
using Microsoft.Xna.Framework;

namespace LlmAgents;

/// <summary>Finding items by a free-text query (any language, name or identifier) and choosing medicine.</summary>
internal static class ItemQueries
{
    static readonly System.Text.RegularExpressions.Regex EntityIdText = new(@"\(id:\d+\)");

    public static string Norm(string s) =>
        // "(ID: 759)" is the game's debug text for an entity: it leaks into what bots see doing and they copy it
        EntityIdText.Replace(new string(s.ToLowerInvariant().Where(ch => !char.IsWhiteSpace(ch) && ch != '_' && ch != '-' && ch != '.').ToArray()), "");


    /// <summary>"Бинт [antibleeding1]": localized name plus identifier, so the LLM can copy either.</summary>
    public static string Label(Item i) => $"{i.Name} [{i.Prefab.Identifier}]";
    public static string Label(ItemPrefab p) => $"{p.Name.Value} [{p.Identifier}]";
    public static string InstanceLabel(Item i) => $"{Label(i)} #{i.ID}";

    public static int MatchScore(Item item, string query, bool kinds = true)
    {
        var parsed = InventoryQuery.Parse(query);
        if ((parsed.EntityId.HasValue && parsed.EntityId != item.ID) || !parsed.MatchesCondition(item.Condition, item.MaxCondition)) return 0;
        return string.IsNullOrWhiteSpace(parsed.Text) && (parsed.EntityId.HasValue || parsed.Condition != ItemConditionFilter.Any)
            ? 3 : MatchScore(item.Prefab, parsed.Text, kinds);
    }

    /// <summary>
    /// 0 = no match, higher = better (exact identifier / name beats substring). Plain words work too: other word forms
    /// ("скафандры"), a copied "Name [id]", and kinds of items ("медикаменты", "оружие", "водолазный костюм").
    /// </summary>
    /// <param name="kinds">false = the query must name the item itself (crafting and deconstructing: "medical" must not
    /// pick some medicine to make or break down).</param>
    public static int MatchScore(ItemPrefab p, string query, bool kinds = true)
    {
        var q = Norm(query);
        if (q.Length == 0) return 0;
        var id = Norm(p.Identifier.Value);
        if (ItemWords.BracketId(query) is { } bracket && Norm(bracket) == id) return 3;
        var name = Norm(p.Name.Value);
        var enName = Crafting.EnglishName(p);
        var en = enName != null ? Norm(enName) : "";
        if (id == q || name == q || en == q) return 3;
        if (id.Contains(q) || name.Contains(q) || (en.Length > 0 && en.Contains(q))) return 2;
        if (q.Length >= 4 && (q.Contains(id) || q.Contains(name) || (en.Length > 0 && q.Contains(en)))) return 1;
        if (p.Tags.Any(t => t.Value.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase))) return 1;
        if (ItemWords.WordsMatch(query, new[] { p.Name.Value, enName, p.Identifier.Value })) return 1;
        if (!kinds) return 0;
        var groups = ItemWords.Groups(query);
        if (groups.Count > 0 && groups.Any(g => InGroup(p, g))) return 1;
        return 0;
    }

    /// <summary>
    /// The query names an item exactly, but none of it is free (all carried or in use), while something weaker matches
    /// ("Сварочный аппарат": every welding tool is in someone's hands, only fuel tanks "of the welding tool" are left):
    /// who has them, instead of silently taking the other thing. null = not that case.
    /// </summary>
    public static string? NoneFree(Character c, string query, Item? best)
    {
        if (best != null && MatchScore(best, query, kinds: false) >= 3) return null;
        var exact = Item.ItemList.Where(i => !i.Removed && i.Submarine == c.Submarine && MatchScore(i, query, kinds: false) >= 3).ToList();
        if (exact.Count == 0) return null;
        var holders = exact
            .Select(i => i.GetRootInventoryOwner() is Character h ? (h == c ? "you" : h.Name) : i.Container != null ? $"{i.Container.Name} (in use)" : "in use")
            .GroupBy(x => x).Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key);
        return $"no free {Label(exact[0].Prefab)} aboard: {string.Join(", ", holders.Take(6))} has it; craft one, or ask them";
    }

    public static bool InGroup(ItemPrefab p, ItemGroup g) => g switch
    {
        ItemGroup.Weapon => (p.Tags.Contains(Tags.Weapon) || p.Category.HasFlag(MapEntityCategory.Weapon)) && !IsAmmo(p),
        ItemGroup.Ammo => IsAmmo(p),
        ItemGroup.Medical => p.Tags.Contains(Tags.MedicalItem) || p.Category.HasFlag(MapEntityCategory.Medical),
        ItemGroup.Tool => p.Tags.Contains(Tags.ToolItem),
        ItemGroup.DivingGear => p.Tags.Contains(Tags.HeavyDivingGear) || p.Tags.Contains(Tags.LightDivingGear),
        ItemGroup.DivingSuit => p.Tags.Contains(Tags.HeavyDivingGear),
        ItemGroup.DivingMask => p.Tags.Contains(Tags.LightDivingGear),
        ItemGroup.Oxygen => p.Tags.Contains(Tags.OxygenSource),
        ItemGroup.ReactorFuel => p.Tags.Contains(Tags.ReactorFuel),
        ItemGroup.Extinguisher => p.Tags.Contains(Tags.FireExtinguisher),
        ItemGroup.Battery => p.Tags.Contains(Tags.MobileBattery),
        ItemGroup.Material => p.Category.HasFlag(MapEntityCategory.Material),
        _ => false,
    };

    static readonly string[] AmmoWords = { "ammo", "round", "bullet", "shell", "magazine", "spear", "dart", "cartridge" };

    static bool IsAmmo(ItemPrefab p) =>
        p.Tags.Any(t => t.Value.Contains("ammo", StringComparison.OrdinalIgnoreCase))
        || AmmoWords.Any(w => p.Identifier.Value.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>The query names a kind of items, not one item ("медикаменты", "ammo").</summary>
    public static bool IsKindQuery(string query) => ItemWords.Groups(InventoryQuery.Parse(query).Text).Count > 0;

    /// <summary>Items the bot may take: in its own inventory, or anywhere on its sub that is not on another character or built in.</summary>
    public static bool IsAvailableTo(Item i, Character c)
    {
        if (i.Removed) return false;
        var owner = i.GetRootInventoryOwner();
        if (owner == c) return true;
        if (owner is Character) return false;                       // someone else carries it
        if (c.Submarine == null || i.Submarine != c.Submarine) return false;
        if (i.GetComponent<Pickable>() == null) return false;       // fixed machinery, walls...
        if (i.ParentInventory == null && i.body == null) return false; // attached to the hull
        if (IsIgnored(i)) return false;                             // a player said "ignore this" (middle mouse button)
        return true;
    }

    /// <summary>The item or a container it is in was ordered to be ignored.</summary>
    public static bool IsIgnored(Item i)
    {
        for (var x = i; x != null; x = x.Container)
            if (x.OrderedToBeIgnored) return true;
        return false;
    }

    /// <summary>Items players marked: for the AI ("помеченный ящик") or for deconstruction ("помеченное на разбор").</summary>
    public static List<Item> Marked(Character c, MarkQuery kind)
    {
        var pool = kind == MarkQuery.Deconstruct
            ? Item.DeconstructItems.Where(i => !i.Removed).ToList()
            : ContextMarks.All().Select(m => m.Item).ToList();
        return pool.Where(i => i.Submarine == c.Submarine || i.GetRootInventoryOwner() == c).ToList();
    }

    public static IEnumerable<Item> Available(Character c) => Item.ItemList.Where(i => IsAvailableTo(i, c));

    /// <summary>
    /// Best matching available items, own inventory first, then by distance. Items another bot is already going for
    /// (<paramref name="avoid"/>) come after the free ones, so two bots do not run for the same diving suit.
    /// </summary>
    public static List<Item> Find(Character c, string query, Func<Item, bool>? filter = null, ICollection<Item>? avoid = null, bool kinds = true)
    {
        // "the marked crate": what players marked with the middle mouse button
        var mark = MarkWords.Of(query);
        if (mark != MarkQuery.None)
        {
            var list = Marked(c, mark).Where(i => IsAvailableTo(i, c) && (filter == null || filter(i))).ToList();
            list = InventoryTaskRules.FilterMarked(list, MarkWords.Rest(query), (item, rest) => MatchScore(item, rest, kinds) > 0);
            return list.OrderBy(i => avoid != null && avoid.Contains(i) ? 1 : 0)
                .ThenBy(i => Vector2.DistanceSquared(i.WorldPosition, c.WorldPosition)).ToList();
        }
        var parsed = InventoryQuery.Parse(query);
        var scores = new Dictionary<ItemPrefab, int>();
        return Available(c)
            .Where(i => filter == null || filter(i))
            .Where(i => (!parsed.EntityId.HasValue || parsed.EntityId == i.ID) && parsed.MatchesCondition(i.Condition, i.MaxCondition))
            .Select(i =>
            {
                if (!scores.TryGetValue(i.Prefab, out var score))
                    scores[i.Prefab] = score = string.IsNullOrWhiteSpace(parsed.Text) && (parsed.EntityId.HasValue || parsed.Condition != ItemConditionFilter.Any)
                        ? 3 : MatchScore(i.Prefab, parsed.Text, kinds);
                return (i, score);
            })
            .Where(t => t.score > 0)
            .OrderByDescending(t => t.score)
            .ThenBy(t => t.i.GetRootInventoryOwner() == c ? 0 : 1)
            .ThenBy(t => avoid != null && avoid.Contains(t.i) ? 1 : 0)
            .ThenBy(t => Vector2.DistanceSquared(t.i.WorldPosition, c.WorldPosition))
            .Select(t => t.i)
            .ToList();
    }

    /// <summary>Another item of the same kind for a task whose item was taken by someone else or could not be reached.</summary>
    public static Item? Replacement(Character c, Item failed, ICollection<Item> tried, ICollection<Item>? avoid = null, string? query = null) =>
        InventoryTaskRules.SelectReplacement(Available(c).OrderBy(i => Vector2.DistanceSquared(i.WorldPosition, c.WorldPosition)),
            failed, tried, avoid ?? Array.Empty<Item>(),
            i => i.Prefab == failed.Prefab && i.GetRootInventoryOwner() != c && (query == null || MatchScore(i, query) > 0));

    public static string Where(Item i, Character c)
    {
        if (i.GetRootInventoryOwner() is Character owner) return owner == c ? "on you" : $"carried by {owner.Name}";
        var container = i.Container;
        var hull = (container ?? i).CurrentHull;
        var place = hull != null ? GameAdapter.RoomId(hull) : "outside";
        return container != null ? $"{container.Name} in {place}" : place;
    }

    public static int Price(ItemPrefab p) => p.DefaultPrice?.Price ?? 100;

    public static bool IsMedical(Item i) => i.HasTag("medical".ToIdentifier()) || i.Prefab.Category.HasFlag(MapEntityCategory.Medical);

    /// <summary>Medical items available to the bot, grouped, own inventory first.</summary>
    public static List<string> MedicalSupplies(Character c, int max = 12)
    {
        return Available(c).Where(IsMedical)
            .GroupBy(i => i.Prefab)
            .Select(g => (prefab: g.Key, total: g.Count(), mine: g.Count(i => i.GetRootInventoryOwner() == c)))
            .OrderByDescending(t => t.mine).ThenByDescending(t => t.total)
            .Take(max)
            .Select(t => $"{Label(t.prefab)} x{t.total}{(t.mine > 0 ? $" ({t.mine} on you)" : "")}")
            .ToList();
    }

    // ----------------------------------------------------------------- health

    /// <summary>Buffs, talents and genetic material (spliced genes: "Hunter genes 100") are not injuries.</summary>
    static bool IsBenign(AfflictionPrefab p) =>
        p.IsBuff || p.AfflictionType == "geneticmaterialbuff" || p.AfflictionType == "geneticmaterialdebuff" || p.AfflictionType == "talentbuff";

    public static IEnumerable<Affliction> TreatableAfflictions(Character target) =>
        target.CharacterHealth.GetAllAfflictions()
            .Where(a => !IsBenign(a.Prefab) && a.Strength > 0 && a.Strength >= a.Prefab.TreatmentThreshold)
            .OrderByDescending(a => a.Strength / Math.Max(1f, a.Prefab.MaxStrength));

    /// <summary>"burn 30 (left arm), bleeding 12": the worst few afflictions.</summary>
    public static string? DescribeInjuries(Character target, int max = 4)
    {
        var list = TreatableAfflictions(target).Take(max).Select(a =>
        {
            var limb = target.CharacterHealth.GetAfflictionLimb(a);
            var where = limb != null ? $" ({limb.type})" : "";
            return $"{a.Prefab.Name.Value} {(int)Math.Round(a.Strength)}{where}";
        }).ToList();
        return list.Count == 0 ? null : string.Join(", ", list);
    }

    public sealed record TreatmentChoice(Item Item, Affliction Affliction, float Suitability, int Price);

    /// <summary>
    /// Cheapest treatment that is good enough: for the worst afflictions first, every available item that helps is
    /// scored; among those reaching at least half of the best score the cheapest one wins (items already carried
    /// win ties). With <paramref name="onlyItem"/> the choice is restricted to that item.
    /// </summary>
    public static TreatmentChoice? ChooseTreatment(Character medic, Character target, Item? onlyItem = null)
    {
        var suitabilities = new Dictionary<Identifier, float>();
        var available = onlyItem != null
            ? new List<Item> { onlyItem }
            : Available(medic).Where(IsMedical)
                .Concat(target.IsIncapacitated && target != medic && target.Inventory != null
                    ? target.Inventory.AllItems.Where(IsMedical) : Enumerable.Empty<Item>())
                .ToList();
        if (available.Count == 0) return null;

        foreach (var affliction in TreatableAfflictions(target).Take(4))
        {
            var limb = target.CharacterHealth.GetAfflictionLimb(affliction);
            target.CharacterHealth.GetSuitableTreatments(suitabilities, medic, limb,
                checkTreatmentThreshold: true, checkTreatmentSuggestionThreshold: false, predictFutureDuration: 10.0f);

            var candidates = new List<TreatmentChoice>();
            foreach (var item in available)
            {
                if (!suitabilities.TryGetValue(item.Prefab.Identifier, out var general) || general <= 0) continue;
                var specific = affliction.Prefab.GetTreatmentSuitability(item);
                if (specific <= 0) continue;
                candidates.Add(new TreatmentChoice(item, affliction, general * specific, Price(item.Prefab)));
            }
            if (candidates.Count == 0) continue;

            float best = candidates.Max(x => x.Suitability);
            return candidates
                .Where(x => x.Suitability >= best * 0.5f)
                .OrderBy(x => x.Price)
                .ThenBy(x => x.Item.GetRootInventoryOwner() == medic ? 0 : 1)
                .ThenBy(x => Vector2.DistanceSquared(x.Item.WorldPosition, medic.WorldPosition))
                .First();
        }
        return null;
    }

    public static string DescribePatient(Character medic, Character target)
    {
        var sb = new StringBuilder();
        sb.Append($"{target.Name}: health {GameAdapter.HealthPercent(target)}%");
        var injuries = DescribeInjuries(target, 6);
        sb.Append(injuries != null ? $"; injuries: {injuries}" : "; no injuries that need treatment");
        if (injuries != null)
        {
            var suitabilities = new Dictionary<Identifier, float>();
            target.CharacterHealth.GetSuitableTreatments(suitabilities, medic, null,
                checkTreatmentThreshold: true, checkTreatmentSuggestionThreshold: false, predictFutureDuration: 10.0f);
            var have = Available(medic).Where(IsMedical).GroupBy(i => i.Prefab.Identifier).ToDictionary(g => g.Key, g => g.Count());
            var options = suitabilities.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).Take(6)
                .Select(kv =>
                {
                    var name = ItemPrefab.Prefabs.TryGet(kv.Key, out var p) ? p.Name.Value : kv.Key.Value;
                    var count = have.GetValueOrDefault(kv.Key);
                    var price = p != null ? Price(p) : 0;
                    return $"{name} [{kv.Key}] (suitability {kv.Value:0}, price {price}, available {count})";
                });
            sb.Append("; treatments: ").Append(string.Join("; ", options));
            var choice = ChooseTreatment(medic, target);
            if (choice != null) sb.Append($". Cheapest good choice now: {Label(choice.Item)} for {choice.Affliction.Prefab.Name.Value}");
        }
        return sb.ToString();
    }
}
