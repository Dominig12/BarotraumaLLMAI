using System.Linq;
using System.Text.Json.Nodes;
using LlmAgents.Agents;
using LlmAgents.Llm;
using Xunit;

namespace LlmAgents.Tests;

public sealed class ItemMarkToolsTests
{
    [Theory]
    [InlineData("""{"item":"#42","mark":"attention","note":"Проверь питание"}""", "attention", true, "Проверь питание")]
    [InlineData("""{"item":"#42","mark":"deconstruct"}""", "deconstruct", true, null)]
    [InlineData("""{"item":"#42","mark":"attention","on":false}""", "attention", false, null)]
    [InlineData("""{"item":"#42","mark":"deconstruct","on":false}""", "deconstruct", false, null)]
    public void MarkerActionPreservesKindStateAndReasonWithoutStartingDeconstruction(string json, string kind, bool on, string? note)
    {
        var game = new FakeGame().AddBot(1, "Ann", "assistant");
        using var manager = new AgentManager(Fixtures.Settings(), game, new LlmRouter(Fixtures.Settings()), () => 0);
        var result = Tools.Execute(new ToolCall("mark_item", JsonNode.Parse(json)!.AsObject()), new BotAgent(game.Bots[0]), game, manager);
        Assert.StartsWith("ok", result);
        Assert.Equal(("#42", kind, on, note), game.LastMark!.Value);
        Assert.Equal($"1:mark #42 {kind} {on}", game.Actions.Single());
        Assert.Equal(ToolKind.Order, Tools.Def("mark_item")!.Kind);
        Assert.False(Tools.Def("mark_item")!.MovesOn);
    }

    [Theory]
    [InlineData("""{"item":"*","mark":"deconstruct"}""")]
    [InlineData("""{"item":"railgunshell","mark":"deconstruct"}""")]
    [InlineData("""{"item":"#42","mark":"ai"}""")]
    [InlineData("""{"item":"#42","mark":"attention","on":"yes"}""")]
    [InlineData("""{"item":"#42","mark":"deconstruct","note":"Look here"}""")]
    [InlineData("""{"item":"#42","mark":"attention","on":false,"note":"Look here"}""")]
    [InlineData("""{"item":"#42","mark":"attention","count":8}""")]
    [InlineData("""{"item":"#99999999999999","mark":"attention"}""")]
    [InlineData("""{"item":"[c42]","mark":"attention"}""")]
    public void InvalidMarkRequestsDoNotMutateGame(string json)
    {
        var game = new FakeGame().AddBot(1, "Ann", "assistant");
        using var manager = new AgentManager(Fixtures.Settings(), game, new LlmRouter(Fixtures.Settings()), () => 0);
        var result = Tools.Execute(new ToolCall("mark_item", JsonNode.Parse(json)!.AsObject()), new BotAgent(game.Bots[0]), game, manager);
        Assert.StartsWith("error:", result);
        Assert.Null(game.LastMark);
        Assert.Empty(game.Actions);
    }

    [Fact]
    public void AttentionNoteLengthIsValidatedBeforeAnyGameMutation()
    {
        var game = new FakeGame().AddBot(1, "Ann", "assistant");
        using var manager = new AgentManager(Fixtures.Settings(), game, new LlmRouter(Fixtures.Settings()), () => 0);
        var arguments = new JsonObject { ["item"] = "#42", ["mark"] = "attention", ["note"] = new string('x', ItemMarkRules.MaxNoteLength + 1) };
        Assert.Contains("300", Tools.Execute(new ToolCall("mark_item", arguments), new BotAgent(game.Bots[0]), game, manager));
        Assert.Empty(game.Actions);
    }

    [Fact]
    public void GeneratedMarkHandleBindsExactObjectAndMarkerAndRespectsCanonicalPermissions()
    {
        var game = new FakeGame().AddBot(1, "Ann", "assistant");
        game.Objects.Add(new GameObjectInfo("#42", "round1_item42", "Cabinet", "mod_cabinet", "container", new[] { "ItemContainer" },
            new[] { new ObjectToolBinding("mark_item", new JsonObject { ["item"] = "#42", ["mark"] = "attention" }) }));
        var settings = Fixtures.Settings();
        using var manager = new AgentManager(settings, game, new LlmRouter(settings), () => 0);
        var bot = new BotAgent(game.Bots[0]);
        manager.DiscoverObjectTools(bot, "#42", 0, 20);
        var handle = manager.ToolsFor(bot).Single(tool => tool.Name.StartsWith("obj_") && manager.CanonicalCall(bot, new ToolCall(tool.Name, new JsonObject())).Name == "mark_item");
        Assert.False(handle.Parameters["properties"]!.AsObject().ContainsKey("mark"));
        Assert.StartsWith("error:", Tools.Execute(new ToolCall(handle.Name, new JsonObject { ["mark"] = "deconstruct" }), bot, game, manager));
        Assert.Empty(game.Actions);
        Assert.StartsWith("ok", Tools.Execute(new ToolCall(handle.Name, new JsonObject { ["note"] = "Check stock" }), bot, game, manager));
        Assert.Equal(("#42", "attention", true, "Check stock"), game.LastMark!.Value);
        settings.Bots.AllowedTools.AddRange(new[] { "object_tools", "inspect_object" });
        var count = game.Actions.Count;
        Assert.Contains("allowedTools", Tools.Execute(new ToolCall(handle.Name, new JsonObject()), bot, game, manager));
        Assert.Equal(count, game.Actions.Count);
    }

    [Fact]
    public void MachineOutputSourceIsExplicitInsteadOfMisusingLinkedSupplies()
    {
        var game = new FakeGame().AddBot(1, "Ann", "assistant");
        using var manager = new AgentManager(Fixtures.Settings(), game, new LlmRouter(Fixtures.Settings()), () => 0);
        var result = Tools.Execute(new ToolCall("move_item", JsonNode.Parse("""{"item":"railgunshell","from":"#687","destination":"storage"}""")!.AsObject()), new BotAgent(game.Bots[0]), game, manager);
        Assert.StartsWith("ok", result);
        Assert.Equal("#687", game.LastMoveFrom);
        Assert.Null(game.LastFilter?.LinkedTo);
    }

    [Fact]
    public void MachineObservationOffersCopyableReferencesInsteadOfRequiringNumericIdConversion()
    {
        var observation = new Observation
        {
            Machines = { new MachineryInfo(687, "Fabricator", "fabricator", "fabricator", "Workshop [h4]", true,
                new() { new MachineryLinkInfo(687, 690, "Cabinet", "container", "ingredient_source", true) }) },
        };
        var machine = JsonNode.Parse(observation.ToCompactJson())!["machines"]![0]!;
        Assert.Equal("#687", machine["reference"]!.GetValue<string>());
        Assert.Equal("#687", machine["links"]![0]!["fromReference"]!.GetValue<string>());
        Assert.Equal("#690", machine["links"]![0]!["toReference"]!.GetValue<string>());
    }

    [Fact]
    public void PromptDistinguishesMarkerKindsEquipmentPossessionAndPredictedTreatment()
    {
        var text = Prompts.System(new BotAgent(new BotHandle(1, "Ann", "assistant")), 8);
        foreach (var rule in new[] { "mark='attention'", "ENTIRE CURRENT STACK", "on=false", "Holding a toolbelt/bag", "native cleanup", "no additional benefit", "identical failing drag" })
            Assert.Contains(rule, text);
    }
}
