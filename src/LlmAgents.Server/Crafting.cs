using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace LlmAgents;

/// <summary>
/// What the crew can make and salvage: fabricator recipes on this sub, what is in stock, and which items
/// aboard break down (deconstructor) into a needed material. Used by inspect(recipes|recipe|salvage) and by
/// craft, which can plan "deconstruct these first, then craft".
/// </summary>
internal static class Crafting
{
    // ------------------------------------------------------------------ names in English too

    static Dictionary<Identifier, string>? english;

    /// <summary>The English item name (the game may run in another language, models usually think in English).</summary>
    public static string? EnglishName(ItemPrefab p)
    {
        english ??= LoadEnglish();
        return english.TryGetValue(p.Identifier, out var n) ? n : null;
    }

    static Dictionary<Identifier, string> LoadEnglish()
    {
        var d = new Dictionary<Identifier, string>();
        try
        {
            if (!TextManager.TextPacks.TryGetValue(TextManager.DefaultLanguage, out var packs)) return d;
            foreach (var pack in packs)
                foreach (var kv in pack.Texts)
                {
                    var key = kv.Key.Value;
                    if (!key.StartsWith("entityname.", StringComparison.OrdinalIgnoreCase) || kv.Value.Length == 0) continue;
                    d[key.Substring("entityname.".Length).ToIdentifier()] = kv.Value[0].String;
                }
        }
        catch (Exception e) { Log.Warn("could not read English item names: " + e.Message); }
        return d;
    }

    /// <summary>"Топливный стержень (Fuel Rod) [fuelrod]" — local name, English name if different, identifier.</summary>
    public static string Label(ItemPrefab p)
    {
        var en = EnglishName(p);
        var local = p.Name.Value;
        return en != null && !en.Equals(local, StringComparison.OrdinalIgnoreCase) ? $"{local} ({en}) [{p.Identifier}]" : $"{local} [{p.Identifier}]";
    }

    // ------------------------------------------------------------------ fabricators and recipes

    public static List<Fabricator> Fabricators(Character c) =>
        Item.ItemList
            .Where(i => i.Submarine == c.Submarine && !i.Removed)
            .Select(i => i.GetComponent<Fabricator>())
            .Where(f => f != null)
            .OrderBy(f => Vector2.DistanceSquared(f!.Item.WorldPosition, c.WorldPosition))
            .Cast<Fabricator>()
            .ToList();

    public static bool HasDeconstructor(Character c) => Deconstructor(c) != null;

    public static Item? Deconstructor(Character c) =>
        Item.ItemList.FirstOrDefault(i => i.Submarine == c.Submarine && !i.Removed && i.GetComponent<Deconstructor>() != null);

    /// <summary>Recipes this crew can actually use (no recipe locked behind a talent nobody has, no traitor items).</summary>
    public static List<(Fabricator Fabricator, FabricationRecipe Recipe)> Recipes(Character c)
    {
        var seen = new HashSet<uint>();
        var list = new List<(Fabricator, FabricationRecipe)>();
        foreach (var f in Fabricators(c))
            foreach (var r in f.fabricationRecipes.Values)
            {
                if (r.HideForNonTraitors) continue;
                bool locked;
                try { locked = f.MissingRequiredRecipe(r, c); } catch { locked = false; }
                if (locked) continue;
                if (seen.Add(r.RecipeHash)) list.Add((f, r)); // an item may have several recipes: keep them all
            }
        return list;
    }

    /// <summary>
    /// Loose or stored items only: not something loaded into a working machine (the reactor's fuel rods, a turret's
    /// ammo, a pump's battery...). Fabricator and deconstructor outputs count, that is where products land.
    /// </summary>
    public static bool IsSpare(Item i)
    {
        if (i.ParentInventory?.Owner is not Item holder) return true;
        if (holder.GetRootInventoryOwner() is Character) return true;        // in a toolbox someone carries
        var fab = holder.GetComponent<Fabricator>();
        if (fab != null) return i.ParentInventory == fab.OutputContainer?.Inventory;
        var decon = holder.GetComponent<Deconstructor>();
        if (decon != null) return i.ParentInventory == decon.OutputContainer?.Inventory;
        return holder.GetComponent<Powered>() == null && holder.GetComponent<Turret>() == null; // cabinets, crates, lockers
    }

    /// <summary>Count of every item the bot could use as an ingredient, by prefab.</summary>
    public static Dictionary<Identifier, int> Stock(Character c, Fabricator? fabricator = null)
    {
        var d = new Dictionary<Identifier, int>();
        foreach (var i in ItemQueries.Available(c).Where(IsSpare).Concat(fabricator?.InputContainer.Inventory.AllItems ?? Enumerable.Empty<Item>()).Distinct())
        {
            if (c.HasEquippedItem(i)) continue;
            d[i.Prefab.Identifier] = d.GetValueOrDefault(i.Prefab.Identifier) + 1;
        }
        return d;
    }

    public static int Have(FabricationRecipe.RequiredItem req, Dictionary<Identifier, int> stock) =>
        req.ItemPrefabs.Sum(p => stock.GetValueOrDefault(p.Identifier));

    public static string IngredientName(FabricationRecipe.RequiredItem req) =>
        req.FirstMatchingPrefab is { } p ? Label(p) : req.ToString();

    /// <summary>(ingredient, missing amount) for a recipe, given the stock.</summary>
    public static List<(FabricationRecipe.RequiredItem Req, int Missing)> Missing(FabricationRecipe r, Dictionary<Identifier, int> stock, int times = 1) =>
        r.RequiredItems.Select(req => (req, Math.Max(0, req.Amount * times - Have(req, stock)))).Where(t => t.Item2 > 0).ToList();

    /// <summary>Best recipe for a free-text query (local name, English name or identifier).</summary>
    public static (Fabricator? Fabricator, FabricationRecipe? Recipe, string? Error) Find(Character c, string query)
    {
        var all = Recipes(c);
        if (all.Count == 0) return (null, null, Fabricators(c).Count == 0 ? "error: no fabricator on this submarine" : "error: the fabricators here can make nothing");
        var stock = Stock(c);
        // best name match; among several recipes for the same item, the one with the fewest missing ingredients
        var best = all.Select(t => (t.Fabricator, t.Recipe, score: ItemQueries.MatchScore(t.Recipe.TargetItem, query, kinds: false)))
            .Where(t => t.score > 0)
            .OrderByDescending(t => t.score)
            .ThenBy(t => Missing(t.Recipe, stock).Sum(m => m.Missing))
            .FirstOrDefault();
        if (best.Recipe != null) return (best.Fabricator, best.Recipe, null);

        var similar = Similar(all.Select(t => t.Recipe.TargetItem), query, 6);
        return (null, null, $"error: no recipe for '{query}'." +
            (similar.Count > 0 ? $" Did you mean: {string.Join(", ", similar.Select(Label))}?" : "") +
            " Use inspect(kind='recipes') to see what can be made.");
    }

    /// <summary>Closest names by shared words / letters, for "did you mean".</summary>
    public static List<ItemPrefab> Similar(IEnumerable<ItemPrefab> prefabs, string query, int max)
    {
        var words = query.ToLowerInvariant().Split(new[] { ' ', '_', '-', ',' }, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3).ToList();
        if (words.Count == 0) return new List<ItemPrefab>();
        int Score(ItemPrefab p)
        {
            var hay = $"{p.Name.Value} {EnglishName(p)} {p.Identifier}".ToLowerInvariant();
            return words.Sum(w => hay.Contains(w) ? 3 : hay.Contains(w.Substring(0, Math.Min(4, w.Length))) ? 1 : 0);
        }
        return prefabs.Distinct().Select(p => (p, s: Score(p))).Where(t => t.s > 0).OrderByDescending(t => t.s).Take(max).Select(t => t.p).ToList();
    }

    // ------------------------------------------------------------------ salvage (deconstructor)

    /// <summary>What deconstructing one item of this prefab gives (amount per product), or empty.</summary>
    public static List<(ItemPrefab Product, int Amount)> Yields(ItemPrefab p, Item? deconstructor)
    {
        if (!p.AllowDeconstruct || p.DeconstructItems.Length == 0) return new();
        return p.DeconstructItems
            .Where(d => deconstructor == null || d.IsValidDeconstructor(deconstructor))
            .Select(d => (prefab: ItemPrefab.Prefabs.TryGet(d.ItemIdentifier, out var x) ? x : null, d.Amount))
            .Where(t => t.prefab != null)
            .Select(t => (t.prefab!, t.Amount))
            .ToList();
    }

    /// <summary>Items that should not be broken down without being asked: in use or valuable gear.</summary>
    static bool SafeToSalvage(Item i, Character c, IReadOnlyCollection<Identifier> protectedIds)
    {
        if (c.HasEquippedItem(i)) return false;
        if (protectedIds.Contains(i.Prefab.Identifier)) return false;
        var cat = i.Prefab.Category;
        if (cat.HasFlag(MapEntityCategory.Medical) || cat.HasFlag(MapEntityCategory.Weapon)) return false;
        if (i.HasTag(Tags.ReactorFuel) || i.HasTag(Tags.HeavyDivingGear) || i.HasTag(Tags.LightDivingGear) || i.HasTag("identitycard".ToIdentifier())) return false;
        if (i.OwnInventory != null && i.OwnInventory.AllItems.Any()) return false; // a full toolbox, a loaded gun...
        return true;
    }

    public sealed record Source(Item Item, int Yield, bool Random);

    /// <summary>Items aboard that break down into something matching <paramref name="wanted"/>, cheapest first.</summary>
    public static List<Source> SourcesFor(Character c, Func<ItemPrefab, bool> wanted, IReadOnlyCollection<Identifier>? protectedIds = null)
    {
        var decon = Deconstructor(c);
        if (decon == null) return new();
        protectedIds ??= Array.Empty<Identifier>();
        var list = new List<Source>();
        foreach (var i in ItemQueries.Available(c))
        {
            if (!SafeToSalvage(i, c, protectedIds) || !IsSpare(i)) continue;
            if (i.ParentInventory?.Owner is Item owner && owner.GetComponent<Deconstructor>() != null) continue; // already there
            float health = i.Condition / Math.Max(1f, i.MaxCondition);
            int yield = i.Prefab.DeconstructItems
                .Where(d => d.IsValidDeconstructor(decon) && health >= d.MinCondition && health <= d.MaxCondition
                            && ItemPrefab.Prefabs.TryGet(d.ItemIdentifier, out var p) && wanted(p))
                .Sum(d => d.Amount);
            if (yield > 0) list.Add(new Source(i, yield, i.Prefab.RandomDeconstructionOutput));
        }
        return list
            .OrderBy(s => s.Random ? 1 : 0)
            .ThenBy(s => ItemQueries.Price(s.Item.Prefab) / (double)s.Yield)
            .ThenBy(s => Vector2.DistanceSquared(s.Item.WorldPosition, c.WorldPosition))
            .ToList();
    }

    /// <summary>
    /// Items to deconstruct so a recipe can be made <paramref name="times"/> times, or null with the reason.
    /// Only one level deep (material from scrap), random-output items only as a last resort.
    /// </summary>
    public static (List<Item>? Items, string? Problem) SalvagePlan(Character c, FabricationRecipe r, Dictionary<Identifier, int> stock, int times)
    {
        var plan = new List<Item>();
        var used = new HashSet<Item>();
        var protectedIds = r.RequiredItems.SelectMany(q => q.ItemPrefabs.Select(p => p.Identifier)).Append(r.TargetItemPrefabIdentifier).ToHashSet();
        foreach (var (req, missing) in Missing(r, stock, times))
        {
            int need = missing;
            foreach (var s in SourcesFor(c, p => req.ItemPrefabs.Any(x => x.Identifier == p.Identifier), protectedIds))
            {
                if (need <= 0) break;
                if (used.Contains(s.Item)) continue;
                plan.Add(s.Item);
                used.Add(s.Item);
                need -= s.Random ? 1 : s.Yield;
            }
            if (need > 0) return (null, $"not enough {IngredientName(req)}: missing {need} even after salvaging");
        }
        return (plan, null);
    }

    // ------------------------------------------------------------------ texts for inspect

    /// <summary>inspect(kind='recipes'): what can be made, "can make now" first.</summary>
    public static string RecipeList(Character c, string filter)
    {
        var all = Recipes(c);
        if (all.Count == 0) return Fabricators(c).Count == 0 ? "no fabricator on this submarine" : "the fabricators here can make nothing";
        var stock = Stock(c);
        var rows = all
            .Where(t => string.IsNullOrWhiteSpace(filter) || ItemQueries.MatchScore(t.Recipe.TargetItem, filter) > 0
                        || t.Recipe.TargetItem.Category.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || t.Recipe.RequiredItems.Any(q => q.ItemPrefabs.Any(p => ItemQueries.MatchScore(p, filter) >= 2)))
            .Select(t => (t.Recipe, missing: Missing(t.Recipe, stock)))
            // one line per item: its easiest recipe
            .GroupBy(t => t.Recipe.TargetItemPrefabIdentifier)
            .Select(g => g.OrderBy(t => t.missing.Sum(m => m.Missing)).First())
            .OrderBy(t => t.missing.Count)
            .ThenBy(t => t.Recipe.TargetItem.Name.Value)
            .ToList();
        if (rows.Count == 0)
        {
            var similar = Similar(all.Select(t => t.Recipe.TargetItem), filter, 6);
            return $"no recipe matches '{filter}'" + (similar.Count > 0 ? $"; similar: {string.Join(", ", similar.Select(Label))}" : "");
        }
        var ready = rows.Where(t => t.missing.Count == 0).Select(t => Label(t.Recipe.TargetItem)).ToList();
        var notReady = rows.Where(t => t.missing.Count > 0)
            .Select(t => $"{Label(t.Recipe.TargetItem)} (missing {string.Join(", ", t.missing.Select(m => $"{m.Req.FirstMatchingPrefab?.Name.Value ?? "?"} x{m.Missing}"))})")
            .ToList();
        var text = (ready.Count > 0 ? $"CAN MAKE NOW ({ready.Count}): {string.Join("; ", ready.Take(25))}{(ready.Count > 25 ? "; ..." : "")}. " : "Nothing can be made from what is aboard right now. ")
                 + (notReady.Count > 0 ? $"NEED MATERIALS ({notReady.Count}): {string.Join("; ", notReady.Take(10))}{(notReady.Count > 10 ? "; ..." : "")}." : "");
        if (string.IsNullOrWhiteSpace(filter) && rows.Count > 25) text += " Filter with query (a word or category, e.g. 'medical', 'ammo').";
        return text + " Craft with the exact name or [id]; craft(salvage=true) first breaks down scrap for missing materials.";
    }

    /// <summary>inspect(kind='recipe'): one recipe with stock and where missing materials can come from.</summary>
    public static string RecipeDetails(Character c, Fabricator f, FabricationRecipe r)
    {
        var stock = Stock(c, f);
        var parts = r.RequiredItems.Select(q => $"{IngredientName(q)} x{q.Amount} (have {Have(q, stock)})");
        var skills = r.RequiredSkills.Length > 0 ? "; skills: " + string.Join(", ", r.RequiredSkills.Select(s => $"{s.Identifier} {s.Level:0}")) : "";
        var text = $"{Label(r.TargetItem)} at {f.Item.Name}: needs {string.Join(", ", parts)}{skills}; time {r.RequiredTime:0}s. ";
        var missing = Missing(r, stock);
        if (missing.Count == 0) return text + "All ingredients available.";
        var notes = new List<string>();
        foreach (var (req, n) in missing)
        {
            var src = SourcesFor(c, p => req.ItemPrefabs.Any(x => x.Identifier == p.Identifier)).Take(3).ToList();
            var craftable = Recipes(c).FirstOrDefault(t => req.ItemPrefabs.Any(x => x.Identifier == t.Recipe.TargetItemPrefabIdentifier));
            var how = new List<string>();
            if (src.Count > 0) how.Add("deconstruct " + string.Join(" / ", src.GroupBy(s => s.Item.Prefab).Select(g => $"{g.Key.Name.Value} (gives {g.First().Yield}, {g.Count()} aboard)")));
            if (craftable.Recipe != null) how.Add($"craft {craftable.Recipe.TargetItem.Name.Value}");
            notes.Add($"{req.FirstMatchingPrefab?.Name.Value ?? "?"} x{n}: " + (how.Count > 0 ? string.Join(" or ", how) : "none aboard and no source; buy or find it"));
        }
        return text + "Missing: " + string.Join("; ", notes) + ".";
    }

    /// <summary>inspect(kind='salvage'): what an item breaks down into, or which items give a material.</summary>
    public static string Salvage(Character c, string query)
    {
        var decon = Deconstructor(c);
        if (decon == null) return "no deconstructor on this submarine";
        if (string.IsNullOrWhiteSpace(query))
        {
            var junk = SourcesFor(c, _ => true).GroupBy(s => s.Item.Prefab).Take(15)
                .Select(g => $"{g.Key.Name.Value} x{g.Count()} -> {string.Join(", ", Yields(g.Key, decon).Select(y => $"{y.Product.Name.Value} x{y.Amount}"))}");
            return "Can be broken down: " + string.Join("; ", junk);
        }

        // a material: who gives it?
        var materials = ItemPrefab.Prefabs.Where(p => ItemQueries.MatchScore(p, query) >= 2).ToList();
        var bySource = SourcesFor(c, p => materials.Contains(p)).GroupBy(s => s.Item.Prefab).Take(8).ToList();
        if (bySource.Count > 0)
            return $"For {string.Join("/", materials.Take(3).Select(m => m.Name.Value))} deconstruct: " +
                   string.Join("; ", bySource.Select(g => $"{Label(g.Key)} x{g.Count()} (gives {g.First().Yield} each{(g.First().Random ? ", random" : "")}, {ItemQueries.Where(g.First().Item, c)})")) +
                   ". Then deconstruct(item) or craft(..., salvage=true).";

        // an item: what does it give?
        var item = ItemQueries.Find(c, query, i => i.Prefab.DeconstructItems.Any()).FirstOrDefault();
        if (item != null)
        {
            var y = Yields(item.Prefab, decon);
            return y.Count == 0 ? $"{Label(item.Prefab)} gives nothing in this deconstructor"
                : $"{Label(item.Prefab)} breaks down into {string.Join(", ", y.Select(t => $"{t.Product.Name.Value} x{t.Amount}"))}{(item.Prefab.RandomDeconstructionOutput ? " (some of these, at random)" : "")}";
        }
        return $"nothing aboard gives '{query}' and no such deconstructable item";
    }
}
