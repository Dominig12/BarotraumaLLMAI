using LlmAgents.Agents;
using Xunit;

namespace LlmAgents.Tests;

public class EquipmentSlotRulesTests
{
    // The same flags as the game's InvSlotType; keeping the rules independent of game assemblies
    // lets these regressions run without starting the game.
    const int Storage = 1, RightHand = 2, LeftHand = 4, Head = 8, OuterClothes = 32, Bag = 256;
    const int Hands = RightHand | LeftHand;

    [Fact]
    public void HoldingAToolbeltDoesNotMeanWearingIt()
    {
        var alternatives = EquipmentSlotRules.Preferred(new[] { Storage, RightHand, LeftHand, Bag }, Storage, Hands);

        Assert.Equal(new[] { Bag }, alternatives);
        Assert.DoesNotContain(alternatives, required => EquipmentSlotRules.Satisfies(RightHand, required));
        Assert.Contains(alternatives, required => EquipmentSlotRules.Satisfies(Bag, required));
    }

    [Fact]
    public void WearableAlternativesTakePriorityRegardlessOfDefinitionOrder()
    {
        Assert.Equal(new[] { Head, OuterClothes }, EquipmentSlotRules.Preferred(
            new[] { RightHand, Storage, Head, LeftHand, OuterClothes, Head }, Storage, Hands));
    }

    [Fact]
    public void ToolsWithoutWearableSlotsCanStillBeHeldInEitherHand()
    {
        Assert.Equal(new[] { RightHand, LeftHand }, EquipmentSlotRules.Preferred(
            new[] { Storage, RightHand, LeftHand, RightHand }, Storage, Hands));
    }

    [Fact]
    public void TwoHandedEquipmentRetainsItsCompleteCombination()
    {
        var alternatives = EquipmentSlotRules.Preferred(new[] { Storage, Hands }, Storage, Hands);

        Assert.Equal(new[] { Hands }, alternatives);
        Assert.False(EquipmentSlotRules.Satisfies(RightHand, alternatives[0]));
        Assert.False(EquipmentSlotRules.Satisfies(LeftHand, alternatives[0]));
        Assert.True(EquipmentSlotRules.Satisfies(Hands, alternatives[0]));
    }

    [Fact]
    public void CombinedWearableSlotsMustAllBeOccupied()
    {
        int both = Head | OuterClothes;
        Assert.Equal(new[] { both }, EquipmentSlotRules.Preferred(new[] { Hands, both }, Storage, Hands));
        Assert.False(EquipmentSlotRules.Satisfies(Head, both));
        Assert.False(EquipmentSlotRules.Satisfies(OuterClothes, both));
        Assert.True(EquipmentSlotRules.Satisfies(both, both));
    }

    [Fact]
    public void StorageOnlyOrMissingSlotDeclarationsCannotReportEquipped()
    {
        Assert.Empty(EquipmentSlotRules.Preferred(new[] { 0, Storage, Storage }, Storage, Hands));
        Assert.Empty(EquipmentSlotRules.Preferred(System.Array.Empty<int>(), Storage, Hands));
        Assert.False(EquipmentSlotRules.Satisfies(0, 0));
        Assert.False(EquipmentSlotRules.Satisfies(Storage, Bag));
        Assert.False(EquipmentSlotRules.Satisfies(0, Hands));
    }
}
