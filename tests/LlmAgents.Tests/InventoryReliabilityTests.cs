using System.Collections.Generic;
using System.Linq;
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

    [Fact]
    public void ReplacementDoesNotReuseAnItemStillQueuedForThisBatch()
    {
        var excluded = InventoryTaskRules.ReplacementExclusions("lost A", new string[0], new[] { "queued B" }, new string[0]);
        var replacement = InventoryTaskRules.SelectReplacement(new[] { "queued B", "free C" }, "lost A", excluded,
            new HashSet<string>(), _ => true);
        Assert.Equal("free C", replacement);
        Assert.Null(InventoryTaskRules.SelectReplacement(new[] { "queued B" }, "lost A", excluded,
            new HashSet<string>(), _ => true));
    }

    [Fact]
    public void ReplacementAlsoExcludesAlreadyCollectedAndPreviouslyFailedItems()
    {
        var excluded = InventoryTaskRules.ReplacementExclusions("A", new[] { "failed B" }, new[] { "queued C" }, new[] { "collected D" });
        Assert.Equal(4, excluded.Count);
        Assert.Null(InventoryTaskRules.SelectReplacement(excluded, "A", excluded, new HashSet<string>(), _ => true));
    }

    [Fact]
    public void ScopedReplacementChecksClaimsMadeAfterTheSourceWasInitiallyInspected()
    {
        var sourceSnapshot = new[] { "B", "C" };
        var claims = new HashSet<string>();
        Assert.Equal("B", InventoryTaskRules.SelectReplacement(sourceSnapshot, "lost A", new HashSet<string>(), claims, _ => true));
        claims.Add("B");
        Assert.Equal("C", InventoryTaskRules.SelectReplacement(sourceSnapshot, "lost A", new HashSet<string>(), claims, _ => true));
        claims.Add("C");
        Assert.Null(InventoryTaskRules.SelectReplacement(sourceSnapshot, "lost A", new HashSet<string>(), claims, _ => true));
    }

    [Fact]
    public void MissingExactInstanceDoesNotFallBackToOtherMarkedItems()
    {
        var marked = new[] { 100, 200 };
        var result = InventoryTaskRules.FilterMarked(marked, "#412", (id, text) => InventoryQuery.Parse(text).EntityId == id);
        Assert.Empty(result);
        Assert.Equal(new[] { 200 }, InventoryTaskRules.FilterMarked(marked, "#200", (id, text) => InventoryQuery.Parse(text).EntityId == id));
    }

    [Fact]
    public void MissingEmptyAmmoDoesNotFallBackToFullMarkedBoxes()
    {
        var result = InventoryTaskRules.FilterMarked(new[] { 100f, 100f }, "empty ammo",
            (charge, text) => InventoryQuery.Parse(text).MatchesCondition(charge, 100));
        Assert.Empty(result);
        Assert.Equal(new[] { 0f }, InventoryTaskRules.FilterMarked(new[] { 100f, 0f }, "empty ammo",
            (charge, text) => InventoryQuery.Parse(text).MatchesCondition(charge, 100)));
    }

    [Fact]
    public void PlainMarkedDescriptionStillAllowsTheExistingFallback()
    {
        var marked = new[] { "first marked item", "second marked item" };
        Assert.Equal(marked, InventoryTaskRules.FilterMarked(marked, "crate", (_, _) => false));
    }
}
