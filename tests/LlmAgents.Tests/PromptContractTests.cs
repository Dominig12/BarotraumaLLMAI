using System;
using System.Linq;
using System.Text.Json.Nodes;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

public class PromptContractTests
{
    static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();
    static JsonObject Properties(string tool) => Tools.Def(tool)!.Schema["properties"]!.AsObject();

    [Fact]
    public void SystemExplainsStructuredSelectionMachineLinksAndCompletion()
    {
        var text = Prompts.System(new BotAgent(new BotHandle(1, "Ann", "mechanic")), 8);
        foreach (var rule in new[]
        {
            "ACTION CONTRACT", "exact declared argument keys", "JSON true/false", "min_condition_percent",
            "category='ammo', condition='empty'", "destination='storage'", "inspect(kind='machines')",
            "ingredient_source", "entity_link", "linked deconstructor supplies only its output",
            "proximity does not prove a link", "power/state", "not completed", "next turn",
        }) Assert.Contains(rule, text);
        Assert.DoesNotContain("Plain words work", text);
        Assert.DoesNotContain("to='its place'", text);
        Assert.DoesNotContain("query='empty ammo boxes'", text);
        Assert.DoesNotContain("attack(target='ballast flora')", text);
    }

    [Fact]
    public void PromptModePreservesFullArgumentSchemasIncludingNestedDescriptions()
    {
        var schema = Args("""
            {"type":"object","additionalProperties":false,"required":["selection"],"properties":{
              "selection":{"type":"object","description":"Choose a real observed object","additionalProperties":false,
                "required":["count"],"properties":{"count":{"type":"integer","minimum":1,"maximum":3,"description":"Individual objects"}}}
            }}
            """);
        var prompt = ToolPrompt.Build("SYS", new[] { new ToolSpec("custom", "Full tool description", schema) });
        var encoded = prompt.Split('\n').Single(l => l.StartsWith("Arguments JSON Schema: ", StringComparison.Ordinal))
            .Substring("Arguments JSON Schema: ".Length);
        Assert.True(JsonNode.DeepEquals(schema, JsonNode.Parse(encoded)));
        Assert.Contains("Full tool description", prompt);
        Assert.Contains("one complete valid JSON object", prompt);
        Assert.Contains("not a quoted JSON string", prompt);
    }

    [Fact]
    public void PromptExamplesNeverIntroduceUnofferedTools()
    {
        var onlyCraft = ToolPrompt.Build("SYS", Tools.Filtered(new[] { "craft" }));
        Assert.DoesNotContain("\"name\":\"say\"", onlyCraft);
        Assert.DoesNotContain("fix_leaks", onlyCraft);
        Assert.DoesNotContain("\"name\":\"carry_on\"", onlyCraft);
        var onlySay = ToolPrompt.Build("SYS", Tools.Filtered(new[] { "say" }));
        Assert.Contains("\"name\":\"say\"", onlySay);
        Assert.DoesNotContain("fix_leaks", onlySay);
        Assert.DoesNotContain("\"name\":\"carry_on\"", onlySay);
    }

    [Fact]
    public void ItemToolsExposeTheSameExplicitFilterContract()
    {
        foreach (var tool in new[] { "equip", "take_item", "move_item", "unequip", "drop", "put_in", "refill", "unpack", "loot_body", "deconstruct", "inspect" })
        {
            var properties = Properties(tool);
            Assert.Equal(ItemFilter.Categories, properties["category"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()));
            Assert.Equal(ItemFilter.Conditions, properties["condition"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()));
            Assert.Equal(ItemFilter.Marks, properties["mark"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()));
            Assert.Equal(0, properties["min_condition_percent"]!["minimum"]!.GetValue<int>());
            Assert.Equal(100, properties["max_condition_percent"]!["maximum"]!.GetValue<int>());
            Assert.NotNull(properties["linked_to"]);
        }
        Assert.Contains("machines", Tools.InspectKinds);
        Assert.NotNull(Properties("craft")["machine"]);
        Assert.NotNull(Properties("move_item")["destination"]);
        Assert.Equal(new[] { "character", "ballast_flora" }, Properties("attack")["target_type"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.All(Tools.All, t => Assert.False(t.Parameters["additionalProperties"]!.GetValue<bool>()));
    }

    [Fact]
    public void PlanSchemaDocumentsEveryCanonicalNodeAndItsNestedArguments()
    {
        var schema = Tools.Def("plan")!.Schema;
        Assert.Equal(5, schema["$defs"]!["step"]!["oneOf"]!.AsArray().Count);
        var prompt = ToolPrompt.Build("SYS", Tools.Filtered(new[] { "plan" }));
        foreach (var keyword in new[] { "\"do\"", "\"args\"", "\"if\"", "\"then\"", "\"else\"", "\"wait_until\"", "\"timeout\"", "\"repeat\"", "\"until\"", "\"max\"", "\"try\"", "\"catch\"" })
            Assert.Contains(keyword, prompt);
        Assert.Contains("minimum", Tools.ToolHelp("plan"));
        var error = Tools.ValidateArguments(new ToolCall("plan", Args("""{"steps":[{"wait_until":"oxygen > 50","timeout":301}]}""")));
        Assert.Contains("steps[0].timeout", error);
    }

    [Theory]
    [InlineData("craft", """{"item":"steel","amount":"2"}""", "amount")]
    [InlineData("craft", """{"item":"steel","salvage":"yes"}""", "salvage")]
    [InlineData("take_item", """{"category":"ammo","count":9}""", "count")]
    [InlineData("move_item", """{"item":"#17","destination":"its place"}""", "destination")]
    [InlineData("put_in", """{"item":"c4","into":"#17","container":"#18"}""", "container")]
    [InlineData("inspect", """{"kind":"items","category":"empty ammo"}""", "category")]
    [InlineData("inspect", """{"kind":"items","condition":"spent"}""", "condition")]
    [InlineData("inspect", """{"kind":"items","min_condition_percent":-1}""", "min_condition_percent")]
    [InlineData("keep_gear", """{"on":"off"}""", "on")]
    [InlineData("say", """{"text":42}""", "text")]
    public void InvalidStructuredArgumentsFailBeforeAffectingTheGame(string tool, string json, string field)
    {
        var game = new FakeGame().AddBot(1, "Ann", "mechanic");
        using var mgr = new AgentManager(Fixtures.Settings(), game, new LlmRouter(Fixtures.Settings()), () => 0);
        var result = Tools.Execute(new ToolCall(tool, Args(json)), new BotAgent(game.Bots[0]), game, mgr);
        Assert.StartsWith("error:", result);
        Assert.Contains(field, result);
        Assert.Empty(game.Actions);
    }

    [Fact]
    public void StructuredSelectorsAndMachineReferencesReachTheGameUnchanged()
    {
        var game = new FakeGame().AddBot(1, "Ann", "mechanic");
        using var mgr = new AgentManager(Fixtures.Settings(), game, new LlmRouter(Fixtures.Settings()), () => 0);
        var bot = new BotAgent(game.Bots[0]);
        string Run(string tool, string json) => Tools.Execute(new ToolCall(tool, Args(json)), bot, game, mgr);
        Assert.StartsWith("ok", Run("take_item", """{"category":"ammo","condition":"low","max_condition_percent":15,"count":2,"from":"#70"}"""));
        Assert.Equal("ammo", game.LastFilter!.Category);
        Assert.Equal("low", game.LastFilter.Condition);
        Assert.Equal(15f, game.LastFilter.MaxConditionPercent);
        Assert.Contains("1:take * x2 from #70", game.Actions);
        Assert.StartsWith("items :", Run("inspect", """{"kind":"items","linked_to":"#90"}"""));
        Assert.Equal("#90", game.LastFilter!.LinkedTo);
        Assert.StartsWith("ok", Run("move_item", """{"item":"#91","destination":"storage"}"""));
        Assert.Equal("storage", game.LastDestination);
        Assert.StartsWith("ok", Run("craft", """{"item":"railgunshell","machine":"#90","amount":2}"""));
        Assert.Equal("#90", game.LastMachine);
        Assert.StartsWith("ok", Run("attack", """{"target":"*","target_type":"ballast_flora"}"""));
        Assert.Equal("ballast_flora", game.LastTargetType);
    }

    [Fact]
    public void FilterErrorsHaveOnePrefixAndIdentifyTheInvalidField()
    {
        var game = new FakeGame().AddBot(1, "Ann", "mechanic");
        using var mgr = new AgentManager(Fixtures.Settings(), game, new LlmRouter(Fixtures.Settings()), () => 0);
        var result = Tools.Execute(new ToolCall("take_item", Args("""{"category":"ammo","min_condition_percent":70,"max_condition_percent":20}""")), new BotAgent(game.Bots[0]), game, mgr);
        Assert.StartsWith("error:", result);
        Assert.DoesNotContain("error: error:", result);
        Assert.Contains("min_condition_percent", result);
        Assert.Empty(game.Actions);
    }
}
