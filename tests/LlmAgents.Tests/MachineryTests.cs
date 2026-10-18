using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using LlmAgents.Agents;
using Xunit;

namespace LlmAgents.Tests;

public class MachineryTests
{
    sealed class Item
    {
        public string Name { get; }
        public List<Item>? Inventory { get; set; }
        public bool Removed { get; set; }
        public Item(string name) => Name = name;
    }

    [Fact]
    public void LinkedInventoryTraversalIncludesNestedBagsWithoutCountingDuplicateLinksTwice()
    {
        var steel = new Item("steel");
        var aluminium = new Item("aluminium");
        var bag = new Item("bag") { Inventory = new() { aluminium } };
        var cabinet = new List<Item> { steel, bag };
        var input = new List<Item> { new Item("copper") };
        var stock = MachineryRules.InventoryTree(new[] { input, cabinet, cabinet, bag.Inventory! },
            inventory => inventory, item => item.Inventory, item => !item.Removed);
        Assert.Equal(4, stock.Count);
        Assert.Single(stock, i => i == aluminium);
        Assert.Single(stock, i => i == steel);
    }

    [Fact]
    public void UnlinkedNearbyStorageIsNotPartOfTheNativeIngredientGraph()
    {
        var linkedSteel = new Item("steel");
        var nearbySteel = new Item("steel");
        var linkedCabinet = new List<Item> { linkedSteel };
        var nearbyCabinet = new List<Item> { nearbySteel };
        var stock = MachineryRules.InventoryTree(new[] { linkedCabinet },
            inventory => inventory, item => item.Inventory, item => !item.Removed);
        Assert.Contains(linkedSteel, stock);
        Assert.DoesNotContain(nearbyCabinet[0], stock);
    }

    [Fact]
    public void RemovedContainersDoNotKeepTheirContentsInTheNativeIngredientPlan()
    {
        var removedBag = new Item("bag") { Removed = true, Inventory = new() { new Item("steel") } };
        var stock = MachineryRules.InventoryTree(new[] { new List<Item> { removedBag } },
            inventory => inventory, item => item.Inventory, item => !item.Removed);
        Assert.Empty(stock);
    }

    [Fact]
    public void AllIngredientsInInputAndLinkedSourcesNeedNoCarrying()
    {
        var native = new[] { "input:steel", "cabinet:aluminium", "nestedbag:copper" };
        var allocation = CraftingRules.Allocate(native, new[] { 1, 1, 1 },
            (requirement, item) => item.EndsWith(new[] { "steel", "aluminium", "copper" }[requirement]));
        Assert.All(allocation, ingredients => Assert.Single(ingredients));
        Assert.Null(CraftingRules.NextIngredient(allocation, native.Contains));
    }

    [Fact]
    public void ALinkedIngredientCanBeReassignedWithoutFetchingItFromTheCabinet()
    {
        var nativeSteel = new Item("steel");
        var outsideAluminium = new Item("aluminium");
        var stock = CraftingRules.Allocate(new[] { nativeSteel, outsideAluminium }, new[] { 1, 1 },
            (requirement, item) => requirement == 0 || item.Name == "steel");
        var next = CraftingRules.NextIngredient(stock, item => item == nativeSteel);
        Assert.NotNull(next);
        Assert.Equal((0, outsideAluminium), next.Value);
    }

    [Fact]
    public void CompactObservationPreservesExactMachineIdsAndDirectedSupplySemantics()
    {
        var observation = new Observation
        {
            Machines = { new MachineryInfo(10, "Fabricator", "fabricator", "fabricator", "Workshop [h2]", false,
                new() { new MachineryLinkInfo(10, 20, "Cabinet", "container", "ingredient_source", true, "inventory") }) }
        };
        var machine = JsonNode.Parse(observation.ToCompactJson())!["machines"]![0]!;
        Assert.Equal(10, machine["id"]!.GetValue<int>());
        var link = machine["links"]![0]!;
        Assert.Equal(10, link["fromId"]!.GetValue<int>());
        Assert.Equal(20, link["toId"]!.GetValue<int>());
        Assert.Equal("ingredient_source", link["relation"]!.GetValue<string>());
        Assert.Null(link["contents"]);
    }
}
