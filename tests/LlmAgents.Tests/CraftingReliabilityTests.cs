using System.Collections.Generic;
using System.Linq;
using LlmAgents.Agents;
using Xunit;

namespace LlmAgents.Tests;

public class CraftingReliabilityTests
{
    [Fact]
    public void OneSteelBarCannotSatisfyTwoOverlappingIngredientRequirements()
    {
        var allocation = CraftingRules.Allocate(new[] { "steel" }, new[] { 1, 3 }, (_, item) => item == "steel");
        Assert.Equal(1, allocation.Sum(items => items.Count));
        Assert.Equal(3, allocation.Select((items, index) => new[] { 1, 3 }[index] - items.Count).Sum());
    }

    [Fact]
    public void FlexibleIngredientIsReassignedToLeaveSteelForTheSteelOnlyRequirement()
    {
        var allocation = CraftingRules.Allocate(new[] { "steel", "aluminium" }, new[] { 1, 1 },
            (requirement, item) => requirement == 0 || item == "steel");
        Assert.Equal(new[] { "aluminium" }, allocation[0]);
        Assert.Equal(new[] { "steel" }, allocation[1]);
    }

    [Fact]
    public void NextIngredientUsesTheWholeStockPlanWhenInputItemsMustBeReassigned()
    {
        // A is already in the machine and currently assigned to rule 0. B, outside, matches only rule 0.
        // Bringing B lets A satisfy rule 1, even though there is no outside item assigned to rule 1.
        bool Matches(int requirement, string item) => requirement == 0 || item == "A";
        var input = CraftingRules.Allocate(new[] { "A" }, new[] { 1, 1 }, Matches);
        Assert.Empty(input[1]);
        var stock = CraftingRules.Allocate(new[] { "A", "B" }, new[] { 1, 1 }, Matches);
        var next = CraftingRules.NextIngredient(stock, item => item == "A");
        Assert.NotNull(next);
        Assert.Equal((0, "B"), next.Value);
    }

    [Fact]
    public void AllocationFindsAFullMatchingEvenWhenAChainOfEarlierChoicesMustMove()
    {
        var matching = new[] { new[] { "A", "B" }, new[] { "B", "C" }, new[] { "A", "C" } };
        var allocation = CraftingRules.Allocate(new[] { "A", "B", "C" }, new[] { 1, 1, 1 },
            (requirement, item) => matching[requirement].Contains(item));
        Assert.All(allocation, items => Assert.Single(items));
        Assert.Equal(3, allocation.SelectMany(items => items).Distinct().Count());
    }

    [Fact]
    public void FullAmmoBoxDoesNotCountAsTheEmptyBoxNeededByARefillRecipe()
    {
        var allocation = CraftingRules.Allocate(new[] { (Id: "coilgunammobox", Condition: 100f), (Id: "coilgunammobox", Condition: 0f) },
            new[] { 1, 1 }, (requirement, item) => item.Id == "coilgunammobox" && (requirement == 0 ? item.Condition <= 10f : item.Condition >= 90f));
        Assert.Equal(0f, Assert.Single(allocation[0]).Condition);
        Assert.Equal(100f, Assert.Single(allocation[1]).Condition);
    }

    [Fact]
    public void ClearingAHeavyShellFallsBackToTheFloorWhenTheHandsAreFull()
    {
        var output = new List<string> { "railgunshell" };
        var cleared = CraftingRules.ClearOutput(output, () => output.Count == 0,
            carry: _ => false, drop: item => output.Remove(item));
        Assert.True(cleared.HasRoom);
        Assert.Empty(cleared.Carried);
        Assert.Equal(new[] { "railgunshell" }, cleared.Dropped);
    }

    [Fact]
    public void OutputClearingStopsAsSoonAsThereIsSpaceForTheNextCraft()
    {
        var output = new List<string> { "railgunshell", "other product" };
        var cleared = CraftingRules.ClearOutput(output, () => output.Count < 2,
            carry: item => output.Remove(item), drop: _ => throw new System.Exception("Should have fitted the hands"));
        Assert.True(cleared.HasRoom);
        Assert.Equal(new[] { "railgunshell" }, cleared.Carried);
        Assert.Equal(new[] { "other product" }, output);
    }

    [Fact]
    public void FailedOutputTransfersDoNotReportSpaceOrClaimThatItemsWereDropped()
    {
        var cleared = CraftingRules.ClearOutput(new[] { "locked shell" }, () => false, _ => false, _ => false);
        Assert.False(cleared.HasRoom);
        Assert.Empty(cleared.Carried);
        Assert.Empty(cleared.Dropped);
    }

    [Fact]
    public void ACancelledMachineIsNotCountedAsASuccessfulCraft()
    {
        var cycle = new FabricationCycle(1, 10, 0);
        Assert.Equal(FabricationCycleState.AwaitingProducts, cycle.Observe(false, 0, 1));
        Assert.Equal(FabricationCycleState.StoppedWithoutProducts, cycle.Observe(false, 0, 1 + FabricationCycle.SpawnGraceSeconds + 0.1));
        Assert.Equal(0, cycle.Products);
    }

    [Fact]
    public void SpawnQueueCanFinishAfterTheMachineStops()
    {
        var cycle = new FabricationCycle(6, 10, 0);
        Assert.Equal(FabricationCycleState.AwaitingProducts, cycle.Observe(false, 0, 10));
        for (int i = 0; i < 5; i++) cycle.ProductCreated();
        Assert.Equal(FabricationCycleState.AwaitingProducts, cycle.Observe(false, 0, 10.5));
        cycle.ProductCreated();
        Assert.Equal(FabricationCycleState.Completed, cycle.Observe(false, 0, 11));
    }

    [Fact]
    public void ASlowLowSkillCraftKeepsRunningAsLongAsTheTimerProgresses()
    {
        var cycle = new FabricationCycle(1, 600, 0);
        for (int elapsed = 30; elapsed <= 600; elapsed += 30)
            Assert.Equal(FabricationCycleState.Running, cycle.Observe(true, 600 - elapsed, elapsed));
        cycle.ProductCreated();
        Assert.Equal(FabricationCycleState.Completed, cycle.Observe(false, 0, 601));
    }

    [Fact]
    public void NoPowerTimesOutSinceTheLastActualProgress()
    {
        var cycle = new FabricationCycle(1, 100, 0);
        Assert.Equal(FabricationCycleState.Running, cycle.Observe(true, 90, 10));
        Assert.Equal(FabricationCycleState.Running, cycle.Observe(true, 90, 54));
        Assert.Equal(FabricationCycleState.Stalled, cycle.Observe(true, 90, 10 + FabricationCycle.NoProgressSeconds + 0.1));
    }

    [Fact]
    public void ProductsFromAnEarlierCraftDoNotCompleteTheNextCycle()
    {
        var first = new FabricationCycle(1, 10, 0);
        first.ProductCreated();
        Assert.Equal(FabricationCycleState.Completed, first.Observe(false, 0, 10));
        var next = new FabricationCycle(1, 10, 10);
        Assert.Equal(FabricationCycleState.AwaitingProducts, next.Observe(false, 0, 11));
        Assert.Equal(0, next.Products);
    }
}
