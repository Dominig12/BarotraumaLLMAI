using System.Collections.Generic;
using System.Linq;
using LlmAgents.Agents;
using Xunit;

namespace LlmAgents.Tests;

public class InventoryReliabilityTests
{
    [Theory]
    [InlineData("empty ammo boxes")]
    [InlineData("пустые ящики с боеприпасами")]
    [InlineData("Empty Fuel Rod")]
    [InlineData("low coilgun ammo boxes")]
    [InlineData("Used Diving Suit")]
    public void WordsInNamesNeverBecomeImplicitFilters(string input)
    {
        var query = InventoryQuery.Parse(input);
        Assert.Equal(input, query.Text);
        Assert.Null(query.EntityId);
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
    [InlineData("empty", 0, 200, true)]
    [InlineData("empty", 1, 200, false)]
    [InlineData("low", 59, 200, true)]
    [InlineData("low", 61, 200, false)]
    [InlineData("used", 199.9f, 200, true)]
    [InlineData("used", 200, 200, false)]
    [InlineData("empty", 0, 0, false)]
    [InlineData("any", 200, 200, true)]
    public void Charge_filter_uses_the_fraction_instead_of_assuming_maximum_100(string input, float condition, float maximum, bool expected) =>
        Assert.Equal(expected, new ItemFilter(Condition: input).MatchesCondition(condition, maximum));

    [Fact]
    public void ExactInstanceAndExplicitChargeFiltersCanBeCombined()
    {
        var query = InventoryQuery.Parse("#412");
        Assert.Equal(412, query.EntityId);
        var filter = new ItemFilter(Category: "ammo", Condition: "empty");
        Assert.True(filter.MatchesCondition(0, 100));
        Assert.False(filter.MatchesCondition(100, 100));
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
    public void ExplicitPercentageBoundsAreInclusiveAndCombineWithState()
    {
        var filter = new ItemFilter(Condition: "low", MinConditionPercent: 10, MaxConditionPercent: 40);
        Assert.True(filter.MatchesCondition(20, 200));
        Assert.True(filter.MatchesCondition(60, 200));
        Assert.False(filter.MatchesCondition(10, 200));
        Assert.False(filter.MatchesCondition(70, 200));
    }

    [Fact]
    public void OutOfRangeInstanceNeverFallsBackToNameOnly()
    {
        Assert.Equal(-1, InventoryQuery.Parse("Railgun Shell #9999999999999999999").EntityId);
    }

    [Fact]
    public void FilterReaderRejectsNaturalLanguageValuesAndContradictoryBounds()
    {
        foreach (var json in new[] { "{\"category\":\"боеприпасы\"}", "{\"condition\":\"почти пустой\"}", "{\"mark\":\"marked crate\"}",
            "{\"min_condition_percent\":60,\"max_condition_percent\":20}", "{\"linked_to\":\"locker near fabricator\"}", "{\"max_condition_percent\":\"30\"}" })
            Assert.NotNull(ItemFilter.Read(System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject()).Error);
    }
}
