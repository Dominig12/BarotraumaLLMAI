using System.Collections.Generic;
using System.Linq;
using LlmAgents.Agents;
using Xunit;

namespace LlmAgents.Tests;

public sealed class CorpseScrapSafetyTests
{
    // These equal-valued objects deliberately compare equal: game identity must remain reference based.
    sealed record Entity(string Prefab);

    [Fact]
    public void LootedMorphineCannotSelectTheMedicsOwnMorphineOrSuppliesInsertedIntoTheLootedBagLater()
    {
        var bot = new Entity("human");
        var body = new Entity("human");
        var loot = new Entity("morphine");
        var own = new Entity("morphine");
        var bag = new Entity("toolbelt");
        var insertedLater = new Entity("syringegun");
        var ledger = new LootProvenanceLedger<Entity, Entity, Entity>();
        ledger.Record(bot, body, new[] { bag, loot });

        Assert.True(ledger.InScope(bot, body, loot, bot));
        Assert.True(ledger.InScope(bot, body, bag, bot));
        Assert.False(ledger.InScope(bot, body, own, bot));
        Assert.False(ledger.InScope(bot, body, insertedLater, bot));
    }

    [Fact]
    public void SourceActorAndCurrentOwnerAreAllCheckedByIdentity()
    {
        var bot = new Entity("human");
        var body = new Entity("human");
        var otherBot = new Entity("human");
        var otherBody = new Entity("human");
        var item = new Entity("wrench");
        var ledger = new LootProvenanceLedger<Entity, Entity, Entity>();
        ledger.Record(bot, body, new[] { item });
        Assert.False(ledger.InScope(otherBot, body, item, otherBot));
        Assert.False(ledger.InScope(bot, otherBody, item, bot));
        Assert.False(ledger.InScope(bot, body, item, otherBot));
        Assert.False(ledger.InScope(bot, body, item, new Entity("cabinet")));
        Assert.True(ledger.InScope(bot, body, item, body));
    }

    [Fact]
    public void RemovedEntitiesAndRoundResetForgetOldProvenance()
    {
        var bot = new object();
        var body = new object();
        var retained = new object();
        var removed = new object();
        var ledger = new LootProvenanceLedger<object, object, object>();
        ledger.Record(bot, body, new[] { retained, removed });
        ledger.Prune((_, _, item) => !ReferenceEquals(item, removed));
        Assert.False(ledger.BelongsTo(bot, body, removed));
        Assert.True(ledger.BelongsTo(bot, body, retained));
        ledger.Clear();
        Assert.False(ledger.BelongsTo(bot, body, retained));
    }

    [Theory]
    [InlineData(true, "self", null, false)]
    [InlineData(true, "self", "ai", false)]
    [InlineData(true, " self ", null, false)]
    [InlineData(true, "self", "deconstruct", true)]
    [InlineData(true, "[c6551]", null, true)]
    [InlineData(false, "self", null, true)]
    public void MixedOwnInventoryCannotBecomeImplicitScrap(bool wildcard, string source, string? mark, bool allowed)
        => Assert.Equal(allowed, DestructiveSelectionRules.ValidateBatch(wildcard, source, mark) == null);

    [Fact]
    public void NativeStackMarkCannotExtendFromBodyLootToAnOwnItemInTheSameStack()
    {
        var looted = new object();
        var own = new object();
        var scope = new HashSet<object> { looted };
        Assert.False(DestructiveSelectionRules.StackWithinScope(new[] { looted, own }, scope));
        Assert.True(DestructiveSelectionRules.StackWithinScope(new[] { looted }, scope));
    }

    [Theory]
    [InlineData("*", "[c6551]", true)]
    [InlineData("*", "#690", true)]
    [InlineData("#6553", "[c6551]", true)]
    [InlineData("*", null, false)]
    [InlineData("*", "self", false)]
    [InlineData("*", "medicaldoctor", false)]
    [InlineData("*", "[h4]", false)]
    [InlineData("*", "[c99999999]", false)]
    [InlineData("baseballcap", "[c6551]", false)]
    public void BulkMarkerRequiresAnExactBodyOrContainerScope(string item, string? from, bool allowed)
        => Assert.Equal(allowed, ItemMarkRules.Validate(item, "deconstruct", true, null, from) == null);

    [Theory]
    [InlineData("morphine", "self", null, false)]
    [InlineData("morphine", null, null, false)]
    [InlineData("morphine", null, "ai", false)]
    [InlineData("morphine", "self", "attention", false)]
    [InlineData("#42", "self", null, true)]
    [InlineData("#42", null, null, true)]
    [InlineData("morphine", "self", "deconstruct", true)]
    [InlineData("morphine", null, "deconstruct", true)]
    [InlineData("morphine", "[c6551]", null, true)]
    [InlineData("morphine", "#690", null, true)]
    [InlineData("morphine", "[h4]", null, false)]
    [InlineData("#42 morphine", "self", null, false)]
    public void NamedOwnSuppliesNeedAnExactItemMarkOrBodyContainerScope(string query, string? from, string? mark, bool allowed)
        => Assert.Equal(allowed, DestructiveSelectionRules.AllowsItem(true, query, from, mark));

    [Fact]
    public void GlobalNamedPoolPrefersAboardScrapAndDoesNotFallBackToOwnOrCarriedBodyLoot()
    {
        var bot = new Entity("human");
        var own = new Entity("morphine");
        var bodyLoot = new Entity("morphine");
        var aboardScrap = new Entity("morphine");
        var candidates = new[] { (Item: own, Owner: (object)bot), (Item: bodyLoot, Owner: (object)bot), (Item: aboardScrap, Owner: new object()) };
        var selected = candidates.Where(entry => DestructiveSelectionRules.AllowsItem(ReferenceEquals(entry.Owner, bot), "morphine", null, null)).ToArray();
        Assert.Single(selected);
        Assert.Same(aboardScrap, selected[0].Item);
        Assert.DoesNotContain(candidates.Take(2), entry => DestructiveSelectionRules.AllowsItem(true, "morphine", null, null));
    }

    [Fact]
    public void BodyNamedPoolCanSelectItsVerifiedCarriedLootWithoutAuthorizingIdenticalOwnSupplies()
    {
        var bot = new Entity("human");
        var body = new Entity("human");
        var own = new Entity("morphine");
        var bodyLoot = new Entity("morphine");
        var ledger = new LootProvenanceLedger<Entity, Entity, Entity>();
        ledger.Record(bot, body, new[] { bodyLoot });
        var selected = new[] { own, bodyLoot }.Where(item => ledger.InScope(bot, body, item, bot)
            && DestructiveSelectionRules.AllowsItem(true, "morphine", "[c6551]", null)).ToArray();
        Assert.Single(selected);
        Assert.Same(bodyLoot, selected[0]);
    }
}
