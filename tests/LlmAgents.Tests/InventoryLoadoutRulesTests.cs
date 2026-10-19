using System;
using System.Linq;
using System.Text.Json;
using LlmAgents.Agents;
using Xunit;

namespace LlmAgents.Tests;

public class InventoryLoadoutRulesTests
{
    static InventorySlotRule Rule(int slot, string? prefab = "wrench", params InventoryContainerStep[] path) => new()
    {
        Slot = slot, Prefab = prefab, Path = path.ToList(),
    };

    static InventoryLoadout Profile(params InventorySlotRule[] rules) => new() { Slots = rules.ToList() };

    [Fact]
    public void DefaultsAndAnExplicitlyDisabledProfileAreValid()
    {
        var profile = new InventoryLoadout();
        Assert.True(profile.Enabled);
        Assert.Equal(15, profile.CheckIntervalSeconds);
        Assert.Equal(120, profile.ReviewIntervalSeconds);
        Assert.Null(InventoryLoadoutRules.Validate(profile));
        profile.Enabled = false;
        Assert.Null(InventoryLoadoutRules.Validate(profile));
    }

    [Fact]
    public void JsonRoundTripPreservesNestedLocationsAndExplicitEmptySlots()
    {
        var profile = Profile(Rule(3, "mod:медпак/advanced.v2", new InventoryContainerStep(6, 1), new InventoryContainerStep(2)), Rule(5, null));
        profile.Slots[0].Count = 8;
        profile.Enabled = false;
        profile.CheckIntervalSeconds = 30;
        profile.ReviewIntervalSeconds = 600;
        var restored = JsonSerializer.Deserialize<InventoryLoadout>(JsonSerializer.Serialize(profile))!;
        Assert.Null(InventoryLoadoutRules.Validate(restored));
        Assert.False(restored.Enabled);
        Assert.Equal(30, restored.CheckIntervalSeconds);
        Assert.Equal(600, restored.ReviewIntervalSeconds);
        Assert.Equal("mod:медпак/advanced.v2", restored.Slots[0].Prefab);
        Assert.Equal(8, restored.Slots[0].Count);
        Assert.Equal(new[] { new InventoryContainerStep(6, 1), new InventoryContainerStep(2) }, restored.Slots[0].Path);
        Assert.Null(restored.Slots[1].Prefab);
    }

    [Fact]
    public void CloneDoesNotShareRulesOrNestedPathLists()
    {
        var original = Profile(Rule(2, "wrench", new InventoryContainerStep(6, 1)));
        var copy = original.Clone();
        Assert.NotSame(original.Slots, copy.Slots);
        Assert.NotSame(original.Slots[0], copy.Slots[0]);
        Assert.NotSame(original.Slots[0].Path, copy.Slots[0].Path);
        copy.Slots[0].Prefab = "screwdriver";
        copy.Slots[0].Count = 3;
        copy.Slots[0].Path[0] = new(7, 0);
        copy.Slots.Add(Rule(0, null));
        Assert.Equal("wrench", original.Slots[0].Prefab);
        Assert.Equal(1, original.Slots[0].Count);
        Assert.Equal(new InventoryContainerStep(6, 1), original.Slots[0].Path[0]);
        Assert.Single(original.Slots);
    }

    [Theory]
    [InlineData(5, 30)]
    [InlineData(300, 3600)]
    public void SchedulingBoundsAreInclusive(int check, int review) =>
        Assert.Null(InventoryLoadoutRules.Validate(new() { CheckIntervalSeconds = check, ReviewIntervalSeconds = review }));

    [Theory]
    [InlineData(4, 120)]
    [InlineData(301, 120)]
    [InlineData(15, 29)]
    [InlineData(15, 3601)]
    public void InvalidSchedulingIsRejectedEvenForDisabledProfiles(int check, int review) =>
        Assert.NotNull(InventoryLoadoutRules.Validate(new() { Enabled = false, CheckIntervalSeconds = check, ReviewIntervalSeconds = review }));

    [Fact]
    public void RuleCountAndDepthLimitsAreInclusive()
    {
        var profile = Profile(Enumerable.Range(0, 64).Select(slot => Rule(slot, "tool")).ToArray());
        Assert.Null(InventoryLoadoutRules.Validate(profile));
        profile.Slots.Add(Rule(64));
        Assert.Contains("64", InventoryLoadoutRules.Validate(profile));

        var nested = Rule(255, "tool", new InventoryContainerStep(255, 31), new InventoryContainerStep(2), new InventoryContainerStep(3), new InventoryContainerStep(4));
        nested.Count = 32;
        Assert.Null(InventoryLoadoutRules.Validate(Profile(nested)));
        nested.Path.Add(new(5));
        Assert.Contains("4", InventoryLoadoutRules.Validate(Profile(nested)));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(256, 1)]
    [InlineData(0, 0)]
    [InlineData(0, 33)]
    public void SlotAndCountBoundsAreChecked(int slot, int count)
    {
        var rule = Rule(slot);
        rule.Count = count;
        Assert.NotNull(InventoryLoadoutRules.Validate(Profile(rule)));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(256, 0)]
    [InlineData(6, -1)]
    [InlineData(6, 32)]
    public void ContainerPathBoundsAreChecked(int slot, int component) =>
        Assert.NotNull(InventoryLoadoutRules.Validate(Profile(Rule(2, "tool", new InventoryContainerStep(slot, component)))));

    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("#123")]
    [InlineData(" #123 ")]
    [InlineData("[c12]")]
    [InlineData("[ref]")]
    [InlineData("tool*")]
    [InlineData("tool?")]
    [InlineData("tool\nname")]
    public void PrefabCannotBeAReferencePatternOrBlank(string prefab) =>
        Assert.NotNull(InventoryLoadoutRules.Validate(Profile(Rule(0, prefab))));

    [Theory]
    [InlineData("mod:oxygen-tank.v2")]
    [InlineData("мод/аптечка_2")]
    [InlineData("mod item+variant")]
    [InlineData("mod[variant]#2")]
    [InlineData("123")]
    public void ExactModIdentifiersAreNotLimitedToAnInventedAlphabet(string prefab) =>
        Assert.Null(InventoryLoadoutRules.Validate(Profile(Rule(0, prefab))));

    [Fact]
    public void PrefabLengthHasAFiniteInclusiveLimit()
    {
        Assert.Null(InventoryLoadoutRules.Validate(Profile(Rule(0, new string('x', 256)))));
        Assert.NotNull(InventoryLoadoutRules.Validate(Profile(Rule(0, new string('x', 257)))));
    }

    [Fact]
    public void AnEmptySlotHasNoStackCount()
    {
        var empty = Rule(3, null);
        Assert.Null(InventoryLoadoutRules.Validate(Profile(empty)));
        empty.Count = 2;
        Assert.Contains("empty", InventoryLoadoutRules.Validate(Profile(empty)));
    }

    [Fact]
    public void DuplicateRootAndNestedDestinationsAreRejected()
    {
        Assert.Contains("duplicate", InventoryLoadoutRules.Validate(Profile(Rule(1), Rule(1, "screwdriver"))));
        Assert.Contains("duplicate", InventoryLoadoutRules.Validate(Profile(
            Rule(2, "wrench", new InventoryContainerStep(6, 1)), Rule(2, "screwdriver", new InventoryContainerStep(6, 1)))));
    }

    [Fact]
    public void MultipleContainerComponentsAndUnrelatedSlotsAreDistinct()
    {
        Assert.Null(InventoryLoadoutRules.Validate(Profile(
            Rule(2, "wrench"), Rule(2, "wrench", new InventoryContainerStep(6, 0)), Rule(2, "screwdriver", new InventoryContainerStep(6, 1)))));
        Assert.Equal("root/6:1/2:0/3", InventoryLoadoutRules.Address(Rule(3, "tool", new InventoryContainerStep(6, 1), new InventoryContainerStep(2))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnExplicitEmptyAncestorRejectsNestedRulesRegardlessOfRuleOrder(bool reverse)
    {
        var profile = Profile(Rule(2, "wrench", new InventoryContainerStep(6, 1), new InventoryContainerStep(4)), Rule(4, null, new InventoryContainerStep(6, 1)));
        if (reverse) profile.Slots.Reverse();
        Assert.Contains("explicitly empty", InventoryLoadoutRules.Validate(profile));
        Assert.NotNull(InventoryLoadoutRules.Validate(Profile(Rule(0, null), Rule(2, "wrench", new InventoryContainerStep(0)))));
    }

    [Fact]
    public void AParentContainerAndItsContentsCanBothBeManaged()
    {
        Assert.Null(InventoryLoadoutRules.Validate(Profile(
            Rule(6, "toolbelt"), Rule(2, "wrench", new InventoryContainerStep(6)), Rule(3, null, new InventoryContainerStep(6)))));
    }

    [Fact]
    public void SwapsAndLongerRearrangementsAreValidDesiredStates()
    {
        // The desired state does not impose an execution order or require a free root slot. Actual item
        // moves, native slot compatibility and temporary placement are resolved against game state.
        Assert.Null(InventoryLoadoutRules.Validate(Profile(Rule(1, "screwdriver"), Rule(2, "wrench"))));
        Assert.Null(InventoryLoadoutRules.Validate(Profile(Rule(1, "screwdriver"), Rule(2, "crowbar"), Rule(3, "wrench"))));
    }

    [Fact]
    public void MalformedJsonNullsProduceValidationErrorsAndRemainInvalidAfterClone()
    {
        Assert.NotNull(InventoryLoadoutRules.Validate(null));
        var profiles = new[]
        {
            new InventoryLoadout { Slots = null! },
            Profile(new InventorySlotRule[] { null! }),
            Profile(new InventorySlotRule { Path = null! }),
            Profile(new InventorySlotRule { Prefab = "wrench", Path = new() { null! } }),
        };
        foreach (var profile in profiles)
        {
            Assert.NotNull(InventoryLoadoutRules.Validate(profile));
            Assert.NotNull(InventoryLoadoutRules.Validate(profile.Clone()));
        }
    }
}
