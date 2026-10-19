using System;
using System.Linq;
using System.Text.Json;
using LlmAgents.Agents;
using Xunit;

namespace LlmAgents.Tests;

public class PlanConditionReliabilityTests
{
    static Observation Inventory() => new()
    {
        Equipped = { "Welding Tool [weldingtool]" }, // Deliberately stale display text must not win over facts.
        InventoryItems = new()
        {
            new(11, "weldingtool", "Сварочный аппарат", 7, Equipped: true, Slot: 3),
            new(12, "weldingtool", "Сварочный аппарат", 80, Slot: 0, ParentId: 70),
            new(13, "bandage", "Бинт", 100, Slot: 1, ParentId: 70),
            new(14, "bandage", "Бинт", 100, Slot: 1, ParentId: 70),
            new(15, "superbandage", "Сильный бинт", 40, Slot: 2, ParentId: 70),
            new(70, "toolbelt", "Пояс", 100, Equipped: true, Slot: 9),
        },
        InventoryFreeSlots = 3,
    };

    static bool Eval(string expression, Observation observation) =>
        PlanConditions.Parse(expression).IsTrue(new PlanConditions.ObservationContext(observation, 30, null, null, false));

    [Theory]
    [InlineData("count('bandage') == 2", true)]
    [InlineData("count('Бинт') == 2", true)]
    [InlineData("count('BANDAGE') == 2", true)]
    [InlineData("count('#13') == 1", true)]
    [InlineData("count('#999') == 0", true)]
    [InlineData("count('band') == 0", true)]
    [InlineData("has('weldingtool')", true)]
    [InlineData("has('welding tool')", false)]
    [InlineData("has('Сварочный аппарат')", true)]
    [InlineData("has('#12') and not wearing('#12')", true)]
    [InlineData("wearing('#11') and wearing('toolbelt')", true)]
    [InlineData("wearing('bandage')", false)]
    [InlineData("condition('weldingtool') == 80", true)]
    [InlineData("condition('#11') < 10", true)]
    [InlineData("free_slots >= 3", true)]
    public void InventoryUsesExactActualInstancesIncludingContainers(string expression, bool expected) =>
        Assert.Equal(expected, Eval(expression, Inventory()));

    [Fact]
    public void AuthoritativeEmptyInventoryDoesNotFallBackToStaleDisplayText()
    {
        var observation = Inventory();
        observation.InventoryItems!.Clear();
        Assert.False(Eval("has('weldingtool') or wearing('weldingtool')", observation));
        Assert.True(Eval("count('weldingtool') == 0", observation));
    }

    [Fact]
    public void TypedInventorySurvivesObservationSerialization()
    {
        var json = Inventory().ToJson();
        var copy = JsonSerializer.Deserialize<Observation>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.True(Eval("count('bandage') == 2 and condition('#12') == 80 and free_slots == 3", copy));
        Assert.Equal(70, copy.InventoryItems!.Single(item => item.Id == 12).ParentId);
    }

    [Theory]
    [InlineData("condition('missing') == 0")]
    [InlineData("condition('missing') != 0")]
    [InlineData("not condition('missing')")]
    [InlineData("water('missing') == 0")]
    [InlineData("not fires('missing')")]
    [InlineData("reactor == 0")]
    [InlineData("reactor_fuel >= 10")]
    [InlineData("sub_oxygen >= 50")]
    public void MissingFactsCannotPretendTheItemIsEmptyOrTheWorldIsSafe(string expression) =>
        Assert.False(Eval(expression, Inventory()));

    [Fact]
    public void LegacyDisplayListsAreOnlyUsedForLegacyHasAndWearing()
    {
        var observation = new Observation { Equipped = { "Welding Tool [weldingtool]" } };
        Assert.True(Eval("has('welding tool') and wearing('welding')", observation));
        Assert.False(Eval("count('weldingtool') == 0", observation));
        Assert.False(Eval("free_slots == 0", observation));
    }

    [Theory]
    [InlineData("TRUE and not FALSE", true)]
    [InlineData("-1 < 0 and +2 >= 2", true)]
    [InlineData(".5 <= 0.50 and 1e2 == 100", true)]
    [InlineData("1e-2 != 1e2", true)]
    [InlineData("1e-9 != 0 and not (1e-9 == 0)", true)]
    [InlineData("true or false and false", true)]
    [InlineData("(true or false) and false", false)]
    public void ExplicitExpressionSyntaxHasPredictablePrecedence(string expression, bool expected) =>
        Assert.Equal(expected, Eval(expression, new Observation()));

    [Theory]
    [InlineData("has()")]
    [InlineData("has('')")]
    [InlineData("has('   ')")]
    [InlineData("has('x','y')")]
    [InlineData("has((health))")]
    [InlineData("'true'")]
    [InlineData("has('unfinished)")]
    [InlineData("has('bad\\q')")]
    [InlineData("1e")]
    [InlineData("1e999")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-")]
    [InlineData("health + 2")]
    public void MalformedArgumentsAndNumbersAreRejectedBeforeAnyPlanRuns(string expression) =>
        Assert.Throws<FormatException>(() => PlanConditions.Parse(expression));

    [Theory]
    [InlineData("has('Captain\\'s \\\\ kit')", "Captain's \\ kit")]
    [InlineData("has(\"Captain\\\"s kit\")", "Captain\"s kit")]
    [InlineData("has('\\u0411\\u0438\\u043d\\u0442')", "Бинт")]
    public void QuotedArgumentsDecodeEscapesOnce(string expression, string expected)
    {
        var context = new RecordingContext { ExpectedArgument = expected };
        Assert.True(PlanConditions.Parse(expression).IsTrue(context));
        Assert.Equal(1, context.Calls);
    }

    [Fact]
    public void ShortCircuitDoesNotLookAtUnavailableWorldFacts()
    {
        var context = new RecordingContext();
        Assert.True(PlanConditions.Parse("true or has('anything')").IsTrue(context));
        Assert.False(PlanConditions.Parse("false and has('anything')").IsTrue(context));
        Assert.Equal(0, context.Calls);
        var lookups = 0;
        var observationContext = new PlanConditions.ObservationContext(() => { lookups++; return Inventory(); }, 1, true, "ok", false);
        Assert.True(PlanConditions.Parse("last_ok and time < 2 and not task").IsTrue(observationContext));
        Assert.Equal(0, lookups);
        Assert.True(PlanConditions.Parse("has('bandage') and count('bandage') >= 2 and health > 50").IsTrue(observationContext));
        Assert.Equal(1, lookups);
    }

    [Fact]
    public void NonFiniteRuntimeValuesCannotBecomeTruthyOrPassComparisons()
    {
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var context = new RecordingContext { NumberValue = value };
            Assert.False(PlanConditions.Parse("health").IsTrue(context));
            Assert.False(PlanConditions.Parse("not health").IsTrue(context));
            Assert.False(PlanConditions.Parse("health != 0").IsTrue(context));
            Assert.False(PlanConditions.Parse("health > 0 or true").IsTrue(context));
            Assert.True(PlanConditions.Parse("true or health").IsTrue(context));
        }
    }

    [Fact]
    public void ParserRejectsOversizedExpressionsAndDeepRecursion()
    {
        Assert.Throws<FormatException>(() => PlanConditions.Parse("true" + new string(' ', PlanConditions.MaxExpressionLength)));
        Assert.Throws<FormatException>(() => PlanConditions.Parse(string.Join(" or ", Enumerable.Repeat("true", 130))));
        Assert.Throws<FormatException>(() => PlanConditions.Parse(new string('(', PlanConditions.MaxDepth + 1) + "true" + new string(')', PlanConditions.MaxDepth + 1)));
        Assert.Throws<FormatException>(() => PlanConditions.Parse(string.Concat(Enumerable.Repeat("not ", PlanConditions.MaxDepth + 1)) + "true"));
        Assert.True(Eval(new string('(', PlanConditions.MaxDepth) + "true" + new string(')', PlanConditions.MaxDepth), new Observation()));
    }

    sealed class RecordingContext : IConditionContext
    {
        public int Calls;
        public string? ExpectedArgument;
        public double NumberValue;
        public double Number(string name) => NumberValue;
        public double Call(string name, string arg)
        {
            Calls++;
            return arg == ExpectedArgument ? 1 : throw new InvalidOperationException("unavailable");
        }
    }
}
