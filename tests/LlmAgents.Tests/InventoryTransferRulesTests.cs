using LlmAgents.Agents;
using Xunit;

namespace LlmAgents.Tests;

public class InventoryTransferRulesTests
{
    [Theory]
    [InlineData("#17", 1, true)]
    [InlineData("#17", 2, false)]
    [InlineData("Battery [batterycell] #17", 2, false)]
    [InlineData("batterycell", 32, true)]
    [InlineData("batterycell", 33, false)]
    [InlineData("batterycell", 0, false)]
    [InlineData("batterycell", -1, false)]
    [InlineData("#99999999999999999", 1, false)]
    [InlineData("*", 1, false)]
    [InlineData("", 1, false)]
    [InlineData("  ", 1, false)]
    public void ExplicitTransferPreservesInstanceIdentityAndRejectsAmbiguousSelection(string item, int count, bool valid) =>
        Assert.Equal(valid, InventoryTransferRules.ValidateCount(item, count, 32) == null);

    sealed class Container
    {
        public Container? Parent;
    }

    [Fact]
    public void CannotMoveBagIntoItselfOrItsNestedContainers()
    {
        var bag = new Container();
        var box = new Container { Parent = bag };
        var pouch = new Container { Parent = box };
        Assert.True(InventoryTransferRules.WouldCreateCycle(bag, bag, c => c.Parent));
        Assert.True(InventoryTransferRules.WouldCreateCycle(bag, pouch, c => c.Parent));
        Assert.False(InventoryTransferRules.WouldCreateCycle(pouch, bag, c => c.Parent));
        Assert.False(InventoryTransferRules.WouldCreateCycle(bag, new Container(), c => c.Parent));
        Assert.False(InventoryTransferRules.WouldCreateCycle(bag, null, c => c.Parent));
    }

    [Fact]
    public void PreExistingContainmentCycleFailsClosedWithoutHanging()
    {
        var a = new Container();
        var b = new Container { Parent = a };
        a.Parent = b;
        Assert.True(InventoryTransferRules.WouldCreateCycle(new Container(), a, c => c.Parent));
    }
}
