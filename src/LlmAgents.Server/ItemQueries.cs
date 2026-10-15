using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace LlmAgents;

/// <summary>Finding items by a free-text query (any language, name or identifier) and choosing medicine.</summary>
internal static class ItemQueries
{
    public static string Norm(string s) =>
        new string(s.ToLowerInvariant().Where(ch => !char.IsWhiteSpace(ch) && ch != '_' && ch != '-' && ch != '.').ToArray());

    /// <summary>"Бинт [antibleeding1]": localized name plus identifier, so the LLM can copy either.</summary>
    public static string Label(Item i) => $"{i.Name} [{i.Prefab.Identifier}]";
    public static string Label(ItemPrefab p) => $"{p.Name.Value} [{p.Identifier}]";

    /// <summary>0 = no match, higher = better (exact identifier / name beats substring).</summary>
    public static int MatchScore(ItemPrefab p, string query)
    {
        var q = Norm(query);
        if (q.Length == 0) return 0;
        var id = Norm(p.Identifier.Value);
        var name = Norm(p.Name.Value);
        var en = Crafting.EnglishName(p) is { } e ? Norm(e) : "";
        if (id == q || name == q || en == q) return 3;
        if (id.Contains(q) || name.Contains(q) || (en.Length > 0 && en.Contains(q))) return 2;
        if (q.Length >= 4 && (q.Contains(id) || q.Contains(name) || (en.Length > 0 && q.Contains(en)))) return 1;
        if (p.Tags.Any(t => t.Value.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase))) return 1;
        return 0;
    }

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
        return true;
    }

    public static IEnumerable<Item> Available(Character c) => Item.ItemList.Where(i => IsAvailableTo(i, c));

    /// <summary>Best matching available items, own inventory first, then by distance.</summary>
    public static List<Item> Find(Character c, string query, Func<Item, bool>? filter = null)
    {
        return Available(c)
            .Where(i => filter == null || filter(i))
            .Select(i => (i, score: MatchScore(i.Prefab, query)))
            .Where(t => t.score > 0)
            .OrderByDescending(t => t.score)
            .ThenBy(t => t.i.GetRootInventoryOwner() == c ? 0 : 1)
            .ThenBy(t => Vector2.DistanceSquared(t.i.WorldPosition, c.WorldPosition))
            .Select(t => t.i)
            .ToList();
    }

    public static string Where(Item i, Character c)
    {
        if (i.GetRootInventoryOwner() == c) return "on you";
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

    public static IEnumerable<Affliction> TreatableAfflictions(Character target) =>
        target.CharacterHealth.GetAllAfflictions()
            .Where(a => !a.Prefab.IsBuff && a.Strength > 0 && a.Strength >= a.Prefab.TreatmentThreshold)
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
