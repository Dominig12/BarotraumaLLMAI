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

    public string Inspect(int botId, string kind, string query, ItemFilter? filter = null)
    {
        if (Find(botId) is not { } c) return "error: character gone";
        switch (kind)
        {
            case "machines":
                return InspectMachines(c, query);
            case "bodies":
                return InspectBodies(c, query);
            case "patient":
            {
                var q = query.Trim().ToLowerInvariant();
                if (q is "" or "*")
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
                string itemQuery = string.IsNullOrWhiteSpace(query) ? "*" : query;
                List<Item> found;
                bool byKind = itemQuery == "*";
                if (filter?.From is { } source)
                {
                    // Source and item identity are independent fields, never guessed from query words.
                    if (FindCrew(source, c, includeSelf: true) is { } person)
                        found = Carried(person).Where(i => ItemQueries.MatchScore(i, itemQuery) > 0 && ItemQueries.Matches(c, i, filter)).ToList();
                    else if (ItemsAt(c, source) is { } place)
                        found = place.Items.Where(i => ItemQueries.IsAvailableTo(i, c) && ItemQueries.MatchScore(i, itemQuery) > 0 && ItemQueries.Matches(c, i, filter)).ToList();
                    else return $"error: no exact source '{source}'; use a container #ID, room [hID], or crew name";
                }
                else found = ItemQueries.Find(c, itemQuery, selection: filter);
                found = found.Take(byKind ? 100 : 50).ToList();
                var noneFree = byKind || filter?.From != null || filter?.Category != null ? null : ItemQueries.NoneFree(c, query, found.FirstOrDefault());
                if (noneFree != null)
                    return noneFree + (found.Count > 0 ? $". Other matches: {string.Join(", ", found.Select(i => i.Prefab).Distinct().Take(3).Select(ItemQueries.Label))}" : "");
                if (found.Count == 0)
                    return $"no items match '{itemQuery}' and the explicit filters; use query without filters to discover identifiers, or from=<exact source #ID/room>";
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
