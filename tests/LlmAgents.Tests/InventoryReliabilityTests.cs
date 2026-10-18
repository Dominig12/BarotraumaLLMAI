using LlmAgents.Agents;
using Xunit;

namespace LlmAgents.Tests;

public class InventoryReliabilityTests
{
    [Theory]
    [InlineData("empty ammo boxes", "ammo", ItemConditionFilter.Empty)]
    [InlineData("пустые ящики с боеприпасами", "с боеприпасами", ItemConditionFilter.Empty)]
    [InlineData("почти пустой ящик с боеприпасами", "с боеприпасами", ItemConditionFilter.Low)]
    [InlineData("low coilgun ammo boxes", "coilgun ammo", ItemConditionFilter.Low)]
    [InlineData("малозаряженные боеприпасы", "боеприпасы", ItemConditionFilter.Low)]
    [InlineData("разряженные батареи", "батареи", ItemConditionFilter.Empty)]
    [InlineData("неполные кислородные баллоны", "кислородные баллоны", ItemConditionFilter.Used)]
    [InlineData("spent fuel rod", "fuel rod", ItemConditionFilter.Empty)]
    public void Charge_qualifiers_do_not_become_part_of_the_item_name(string input, string expected, ItemConditionFilter condition)
    {
        var query = InventoryQuery.Parse(input);
        Assert.Equal(expected, System.Text.RegularExpressions.Regex.Replace(query.Text, @"\s+", " "));
        Assert.Equal(condition, query.Condition);
    }

    [Theory]
    [InlineData("#123", "", 123)]
    [InlineData("Railgun Shell #487", "Railgun Shell", 487)]
    [InlineData("Блок C-4+ [ic4block] #98", "Блок C-4+ [ic4block]", 98)]
    public void Instance_references_preserve_prefab_identifiers_and_explosive_names(string input, string expected, int id)
    {
        var query = InventoryQuery.Parse(input);
        Assert.Equal(expected, query.Text);
        Assert.Equal(id, query.EntityId);
    }

    [Theory]
    [InlineData("empty ammo", 0, 200, true)]
    [InlineData("empty ammo", 1, 200, false)]
    [InlineData("low ammo", 59, 200, true)]
    [InlineData("low ammo", 61, 200, false)]
    [InlineData("used ammo", 198, 200, true)]
    [InlineData("used ammo", 200, 200, false)]
    [InlineData("empty ammo", 0, 0, false)]
    [InlineData("ammo", 200, 200, true)]
    public void Charge_filter_uses_the_fraction_instead_of_assuming_maximum_100(string input, float condition, float maximum, bool expected) =>
        Assert.Equal(expected, InventoryQuery.Parse(input).MatchesCondition(condition, maximum));

    [Fact]
    public void Empty_boxes_still_resolve_to_the_ammunition_group()
    {
        var query = InventoryQuery.Parse("пустые ящики с боеприпасами");
        Assert.Equal(new[] { ItemGroup.Ammo }, ItemWords.Groups(query.Text));
        Assert.Equal(new[] { ItemGroup.Ammo }, ItemWords.Groups(InventoryQuery.Parse("empty ammo boxes").Text));
    }

    [Fact]
    public void Exact_instance_and_charge_filters_can_be_combined()
    {
        var query = InventoryQuery.Parse("empty coilgun ammo #412");
        Assert.Equal("coilgun ammo", query.Text);
        Assert.Equal(412, query.EntityId);
        Assert.True(query.MatchesCondition(0, 100));
        Assert.False(query.MatchesCondition(100, 100));
    }
}
