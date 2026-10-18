using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using LlmAgents.Agents;
using Microsoft.Xna.Framework;

namespace LlmAgents;

/// <summary>inspect(kind=...) lookups.</summary>
public sealed partial class GameAdapter
{
    // --------------------------------------------------------------- inspect

    public string Inspect(int botId, string kind, string query)
    {
        if (Find(botId) is not { } c) return "error: character gone";
        switch (kind)
        {
            case "bodies":
                return InspectBodies(c, query);
            case "patient":
            {
                var q = query.Trim().ToLowerInvariant();
                if (q is "" or "all" or "everyone" or "crew" or "all crew" or "injured" or "все" or "экипаж")
                {
                    var hurt = Character.CharacterList
                        .Where(x => !x.IsDead && !x.Removed && x.IsHuman && x.TeamID == c.TeamID && ItemQueries.TreatableAfflictions(x).Any())
                        .OrderBy(HealthPercent).Take(6)
                        .Select(x => $"{x.Name} ({HealthPercent(x)}%, {(x.CurrentHull != null ? RoomId(x.CurrentHull) : "outside")}): {ItemQueries.DescribeInjuries(x, 3)}")
                        .ToList();
                    return hurt.Count == 0 ? "nobody in the crew needs treatment"
                        : "hurt: " + string.Join("; ", hurt) + ". Ask inspect(kind='patient', query=<name>) for suitable medicine.";
                }
                var patient = FindCrew(query, c, includeSelf: true);
                return patient == null ? $"error: no crew member '{query}'" : ItemQueries.DescribePatient(c, patient);
            }
            case "items":
            {
                if (NearAnchor(c, query) is { } near) return DescribePlace(near.Container.Inventory.AllItems.ToList(), near.Where, c);
                // a room id, a container or a crew member's name: what is there ("what's in Электрика [h140]?")
                bool placeFirst = query.Contains("[h") || ResolveRoom(query.Trim()) != null
                    || (FindCrew(query.Trim(), c, includeSelf: true) != null && query.Trim().Length >= 3);
                if (placeFirst && ItemsAt(c, query) is { } p0) return DescribePlace(p0.Items, p0.Where, c);
                bool byKind = ItemQueries.IsKindQuery(query);
                var found = ItemQueries.Find(c, query).Take(byKind ? 60 : 30).ToList();
                var noneFree = byKind ? null : ItemQueries.NoneFree(c, query, found.FirstOrDefault());
                if (noneFree != null)
                    return noneFree + (found.Count > 0 ? $". Other matches: {string.Join(", ", found.Select(i => i.Prefab).Distinct().Take(3).Select(ItemQueries.Label))}" : "");
                if (found.Count == 0)
                    return ItemsAt(c, query) is { } p1 ? DescribePlace(p1.Items, p1.Where, c) : $"no '{query}' on the sub (try a plainer word: 'tools', 'medical', 'ammo', 'suit', or a room id to see what is there)";
                var groups = found.GroupBy(i => i.Prefab).Take(byKind ? 8 : 5).Select(g =>
                    $"{ItemQueries.Label(g.Key)}: " + string.Join("; ", g.GroupBy(i => (Where: ItemQueries.Where(i, c), Percent: i.MaxCondition > 0 ? (int)Math.Round(100 * i.Condition / i.MaxCondition) : -1))
                        .OrderBy(w => w.Key.Percent).Take(8)
                        .Select(w => $"{w.Count()} {w.Key.Where}" + (w.Key.Percent >= 0 ? $" ({w.Key.Percent}% condition/charge)" : "")
                            + $" [{string.Join(", ", w.Take(6).Select(i => $"#{i.ID}"))}]")));
                return string.Join("; ", groups);
            }
            case "recipe":
            {
                var (fabricator, recipe, error) = Crafting.Find(c, query);
                return error ?? Crafting.RecipeDetails(c, fabricator!, recipe!);
            }
            case "recipes":
                return Crafting.RecipeList(c, query);
            case "salvage":
                return Crafting.Salvage(c, query);
            case "orders":
                return DescribeOrders(c, query);
            case "inventory":
                return DescribeInventory(c);
            default:
                return $"error: unknown kind '{kind}'";
        }
    }
}
