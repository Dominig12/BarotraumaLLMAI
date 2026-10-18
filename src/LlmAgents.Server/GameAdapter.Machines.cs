using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Barotrauma;
using Barotrauma.Items.Components;
using LlmAgents.Agents;
using Microsoft.Xna.Framework;

namespace LlmAgents;

public sealed partial class GameAdapter
{
    static readonly JsonSerializerOptions MachineJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static string MachineKind(Item item) => item.GetComponent<Fabricator>() != null ? "fabricator"
        : item.GetComponent<Deconstructor>() != null ? "deconstructor"
        : item.GetComponent<Reactor>() != null ? "reactor"
        : item.GetComponent<Turret>() != null ? "turret"
        : item.GetComponent<ItemContainer>() != null ? "container"
        : item.GetComponent<Powered>() != null ? "powered"
        : "item";

    static IEnumerable<Item> MachinesAboard(Character c) => c.Submarine == null ? Enumerable.Empty<Item>()
        : Item.ItemList.Where(i => !i.Removed && i.Submarine == c.Submarine &&
            (i.GetComponent<Fabricator>() != null || i.GetComponent<Deconstructor>() != null ||
             i.GetComponent<Reactor>() != null || i.GetComponent<Turret>() != null ||
             i.GetComponent<Powered>() != null && i.linkedTo.OfType<Item>().Any()));

    static List<MachineryInfo> ObserveMachines(Character c) => MachinesAboard(c)
        .OrderBy(i => i.GetComponent<Fabricator>() != null ? 0 : i.linkedTo.OfType<Item>().Any() ? 1 : 2)
        .ThenBy(i => Vector2.DistanceSquared(c.WorldPosition, i.WorldPosition))
        .Take(12).Select(i => DescribeMachine(c, i, contents: false)).ToList();

    static List<string> MachineContents(Inventory? inventory)
    {
        if (inventory == null) return new List<string>();
        var groups = Crafting.InventoryTree(new[] { inventory })
            .GroupBy(i => (i.Prefab, Condition: (int)Math.Round(i.ConditionPercentage)))
            .OrderBy(g => g.Key.Prefab.Identifier.Value).ToList();
        var rows = groups.Take(12).Select(g =>
            $"{ItemQueries.Label(g.Key.Prefab)} x{g.Count()} ({g.Key.Condition}%; {string.Join(", ", g.Take(6).Select(i => $"#{i.ID}"))}{(g.Count() > 6 ? ", ..." : "")})").ToList();
        if (groups.Count > rows.Count) rows.Add($"... {groups.Count - rows.Count} more item kinds; inspect(kind='items', query='#<container ID>') for details");
        return rows;
    }

    static MachineryInfo DescribeMachine(Character c, Item item, bool contents)
    {
        var fabricator = item.GetComponent<Fabricator>();
        var deconstructor = item.GetComponent<Deconstructor>();
        var powered = item.GetComponent<Powered>();
        var links = new List<MachineryLinkInfo>();
        foreach (var target in item.linkedTo.OfType<Item>().Where(i => !i.Removed).Distinct())
        {
            var firstContainer = target.GetComponent<ItemContainer>();
            bool supply = fabricator != null && firstContainer != null;
            var source = supply ? target.GetComponent<Deconstructor>()?.OutputContainer ?? firstContainer : firstContainer;
            bool accessible = !ItemQueries.IsIgnored(target) && (firstContainer == null || firstContainer.HasRequiredItems(c, false));
            links.Add(new MachineryLinkInfo(item.ID, target.ID, ItemQueries.Label(target), MachineKind(target),
                supply ? "ingredient_source" : "entity_link", accessible,
                source == null ? null : supply && target.GetComponent<Deconstructor>() != null ? "output" : "inventory",
                contents && source != null && accessible ? MachineContents(source.Inventory) : null));
        }
        return new MachineryInfo(item.ID, item.Name, item.Prefab.Identifier.Value, MachineKind(item),
            item.CurrentHull != null ? RoomId(item.CurrentHull) : null,
            powered?.IsActive ?? false, links,
            contents ? MachineContents(fabricator?.InputContainer.Inventory ?? deconstructor?.InputContainer.Inventory) : null,
            contents ? MachineContents(fabricator?.OutputContainer.Inventory ?? deconstructor?.OutputContainer.Inventory) : null,
            powered != null ? $"voltage {powered.Voltage:0.00}; minimum {powered.MinVoltage:0.00}" : null);
    }

    string InspectMachines(Character c, string query)
    {
        var machines = MachinesAboard(c).ToList();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var room = ResolveRoom(query);
            machines = machines.Where(i => room != null ? i.CurrentHull == room
                : ItemQueries.MatchScore(i, query, kinds: false) > 0
                    || i.linkedTo.OfType<Item>().Any(target => ItemQueries.MatchScore(target, query, kinds: false) > 0)).ToList();
        }
        if (machines.Count == 0) return "no matching machinery on this submarine; use an exact #ID, prefab identifier, or room ID";
        var shown = machines.OrderBy(i => i.GetComponent<Fabricator>() != null ? 0 : 1)
            .ThenBy(i => Vector2.DistanceSquared(c.WorldPosition, i.WorldPosition)).Take(10).ToList();
        return "Actual entity links: fromId -> toId. ingredient_source: the target inventory automatically supplies the source fabricator (including nested containers); craft moves its ingredients natively. "
            + "entity_link alone does not promise ingredient transfer. Adjacent unlinked containers are not automatic sources. accessible=false sources cannot be used by this bot. "
            + JsonSerializer.Serialize(shown.Select(i => DescribeMachine(c, i, contents: true)), MachineJson)
            + (machines.Count > shown.Count ? $"; {machines.Count - shown.Count} more machines; filter query by #ID or room" : "");
    }
}
